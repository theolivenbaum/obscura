using System.Text;
using System.Threading.Channels;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The <c>__init</c> handshake must not outlive the processor that is the only
/// thing able to answer it.
/// </summary>
/// <remarks>
/// A socket handed off while shutdown is already signalled gets a processor that
/// breaks out of its loop before reading a single message, so nothing ever writes
/// <c>__init</c>. Waiting for it without a cancellation token parked the
/// connection - and, while each connection still owned an OS thread, that thread
/// - for the life of the process.
/// </remarks>
public sealed class ConnectionInitHandshakeTests
{
    [Fact]
    public async Task InitHandshakeGivesUpWhenTheProcessorStops()
    {
        // Nothing reads `messages`, which is the state a processor that exited
        // before its first message leaves behind: the NewConnection lands in the
        // channel and no reply is ever written.
        var messages = Channel.CreateUnbounded<ServerMessage>();
        using var processorStopped = new CancellationTokenSource();
        using var stream = new HandshakeThenParkStream();

        var connection = CdpServer.HandleConnectionWsAsync(
            stream, messages.Writer, processorStopped.Token);

        // The upgrade completed and the connection is now waiting for `__init`.
        var announced = await messages.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.IsType<ServerMessage.NewConnection>(announced);
        Assert.False(
            connection.IsCompleted,
            "the handshake must still be waiting while its processor could yet answer");

        await processorStopped.CancelAsync();

        // Before the token was threaded through, this waited forever.
        await connection.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Serves one WebSocket upgrade request and then parks: further reads never
    /// complete and writes are discarded. Stands in for a client that has
    /// connected and is waiting for the server to say something.
    /// </summary>
    private sealed class HandshakeThenParkStream : Stream
    {
        private static readonly byte[] Upgrade = Encoding.ASCII.GetBytes(
            "GET /devtools/browser HTTP/1.1\r\nHost: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");

        private readonly CancellationTokenSource _closed = new();
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < Upgrade.Length)
            {
                var n = Math.Min(buffer.Length, Upgrade.Length - _offset);
                Upgrade.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                return n;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _closed.Token, cancellationToken);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            return 0;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        // The connection path is async throughout; a synchronous call would mean
        // it had changed, and the test should say so rather than bridge it.
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed.IsCancellationRequested)
            {
                _closed.Cancel();
                _closed.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
