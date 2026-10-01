using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using PocketCalculator.Net;

namespace PocketCalculator.Cdp;

/// <summary>
/// One connection the accept thread hands to the connection loop: a plaintext socket whose
/// request head is still queued on it, or a TLS stream that has finished its handshake and
/// replays the head it read.
/// </summary>
internal readonly record struct AcceptedConnection(Socket? Socket, Stream? Stream);

/// <summary>TLS for the CDP server (SECURITY.md I1).</summary>
public static partial class CdpServer
{
    /// <summary>
    /// The accept thread of a TLS server. The plaintext loop peeks at request heads
    /// without consuming them; under TLS the head only exists after a handshake, so each
    /// connection instead gets a task that runs the handshake and reads the head from the
    /// decrypted stream, bounded by the same silent-connection TTL and pending cap.
    /// </summary>
    private static void TlsAcceptLoop(
        Socket listener,
        IPAddress bindIp,
        int port,
        ForwardedAuthority? forwarded,
        string? authToken,
        X509Certificate2 certificate,
        ChannelWriter<AcceptedConnection> handoff,
        CancellationToken shutdown)
    {
        var pending = 0;
        listener.Blocking = true;
        while (!shutdown.IsCancellationRequested)
        {
            Socket accepted;
            try
            {
                accepted = listener.Accept();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                if (shutdown.IsCancellationRequested)
                {
                    return;
                }

                CdpLog.Error($"Accept error: {e.SocketErrorCode}");
                Thread.Sleep(AcceptPollInterval);
                continue;
            }

            if (shutdown.IsCancellationRequested)
            {
                accepted.Dispose();
                return;
            }

            if (Interlocked.Increment(ref pending) > MaxSilentPending)
            {
                Interlocked.Decrement(ref pending);
                CdpLog.Warn($"dropping connection: {MaxSilentPending} TLS handshakes in progress");
                accepted.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeTlsConnectionAsync(
                            accepted, bindIp, port, forwarded, authToken, certificate, handoff, shutdown)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Handshake, read the request head, then refuse it, answer a <c>/json</c> endpoint, or
    /// hand the WebSocket upgrade on with its head replayed.
    /// </summary>
    private static async Task ServeTlsConnectionAsync(
        Socket socket,
        IPAddress bindIp,
        int port,
        ForwardedAuthority? forwarded,
        string? authToken,
        X509Certificate2 certificate,
        ChannelWriter<AcceptedConnection> handoff,
        CancellationToken shutdown)
    {
        Stream? owned = null;
        try
        {
            socket.NoDelay = true;
            owned = new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            socket.Dispose();
            return;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            deadline.CancelAfter(SilentConnectionTtl);
            SslStream ssl;
            try
            {
                ssl = await ServerTls.AuthenticateAsync(owned, certificate, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException
                or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                CdpLog.Debug($"TLS handshake failed: {e.Message}");
                return;
            }

            owned = ssl;
            var buffer = new byte[HttpPeekBuf];
            var received = 0;
            var complete = false;
            while (received < buffer.Length)
            {
                var n = await ssl.ReadAsync(buffer.AsMemory(received), deadline.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }

                received += n;
                var head = buffer.AsSpan(0, received);
                // As the plaintext peek: a GET waits for its blank line, anything else is
                // classified at once so garbage gets a prompt refusal.
                if (head.IndexOf("\r\n\r\n"u8) >= 0 || (received >= 4 && !head[..4].SequenceEqual("GET "u8)))
                {
                    complete = true;
                    break;
                }
            }

            if (!complete)
            {
                await WriteAndCloseAsync(ssl, ControlRefusalResponse(ControlRefusal.OversizedHead)).ConfigureAwait(false);
                return;
            }

            var requestHead = Lossy(buffer.AsSpan(0, received));
            if (GetControlRefusal(requestHead, bindIp, forwarded, authToken) is { } refusal)
            {
                await WriteAndCloseAsync(ssl, ControlRefusalResponse(refusal)).ConfigureAwait(false);
                return;
            }

            if (JsonEndpoint(requestHead) is { } endpoint)
            {
                var (responseHead, body) = JsonEndpointResponse(port, forwarded, endpoint, requestHead, "wss");
                await ssl.WriteAsync(responseHead, deadline.Token).ConfigureAwait(false);
                await ssl.WriteAsync(body, deadline.Token).ConfigureAwait(false);
                await ssl.FlushAsync(deadline.Token).ConfigureAwait(false);
                await ssl.ShutdownAsync().ConfigureAwait(false);
                return;
            }

            var replay = new ReplayStream(buffer.AsMemory(0, received), ssl);
            if (handoff.TryWrite(new AcceptedConnection(null, replay)))
            {
                owned = null;
                return;
            }

            CdpLog.Warn(
                $"WS handoff channel unavailable (capacity {MaxPendingWsHandoffs}); " +
                "dropping new WebSocket connection");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException
            or SocketException)
        {
            CdpLog.Debug($"TLS connection dropped: {e.Message}");
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Write a whole HTTP response to a TLS stream and close it for writing.</summary>
    private static async Task WriteAndCloseAsync(SslStream ssl, string response)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await ssl.WriteAsync(Encoding.UTF8.GetBytes(response), deadline.Token).ConfigureAwait(false);
        await ssl.FlushAsync(deadline.Token).ConfigureAwait(false);
        await ssl.ShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>Refuse a handed-off TLS connection with <paramref name="response"/>; best effort.</summary>
    private static async Task RefuseStreamAsync(Stream stream, string response)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException
            or SocketException)
        {
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A stream that first returns bytes already read from <paramref name="inner"/> (the
    /// request head), then reads <paramref name="inner"/>. Owns it.
    /// </summary>
    private sealed class ReplayStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.IsEmpty)
            {
                return inner.ReadAsync(buffer, cancellationToken);
            }

            var n = Math.Min(buffer.Length, _prefix.Length);
            _prefix.Span[..n].CopyTo(buffer.Span);
            _prefix = _prefix[n..];
            return ValueTask.FromResult(n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
