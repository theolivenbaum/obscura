using System.Security.Authentication;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Obscura.Net.Tests;

/// <summary>
/// A one-shot loopback HTTP server that hands back canned raw responses in order
/// and records the request bytes it saw. Stands in for the Rust tests'
/// <c>http_fixture</c>.
/// </summary>
internal sealed class HttpFixture : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Channel<string> _requests = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _shutdown = new();

    private HttpFixture(TcpListener listener, Uri url)
    {
        _listener = listener;
        Url = url;
    }

    internal Uri Url { get; }

    internal int RequestCount => Volatile.Read(ref _observed);

    private int _observed;

    internal static HttpFixture Serve(IReadOnlyList<string> responses, string path = "/resource")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fixture = new HttpFixture(listener, new Uri($"http://127.0.0.1:{port}{path}"));
        _ = Task.Run(() => fixture.RunAsync(responses));
        return fixture;
    }

    private async Task RunAsync(IReadOnlyList<string> responses)
    {
        try
        {
            foreach (var response in responses)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                using (client)
                {
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, _shutdown.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _observed);
                    await _requests.Writer.WriteAsync(request, _shutdown.Token).ConfigureAwait(false);
                    var bytes = Encoding.ASCII.GetBytes(response);
                    await stream.WriteAsync(bytes, _shutdown.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (SocketException)
        {
            // shutting down
        }
        catch (IOException)
        {
            // shutting down
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    internal static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[2048];
        var request = new List<byte>();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            request.AddRange(buffer.AsSpan(0, read).ToArray());
            if (ContainsHeaderTerminator(request))
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(request.ToArray());
    }

    private static bool ContainsHeaderTerminator(List<byte> request)
    {
        for (var i = 0; i + 3 < request.Count; i++)
        {
            if (request[i] == '\r' && request[i + 1] == '\n'
                && request[i + 2] == '\r' && request[i + 3] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    internal async Task<string> NextRequestAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _requests.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
    }

    internal static string OkResponse(string headers, string body) =>
        $"HTTP/1.1 200 OK\r\n{headers}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";

    internal static string RedirectToSelf() =>
        "HTTP/1.1 302 Found\r\nLocation: /resource\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>
/// A loopback server that answers every connection with the same cacheable
/// response after a short delay. Stands in for <c>cacheable_resource_fixture</c>.
/// </summary>
internal sealed class CacheableResourceFixture : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private int _requests;

    private CacheableResourceFixture(TcpListener listener, Uri url)
    {
        _listener = listener;
        Url = url;
    }

    internal Uri Url { get; }

    internal int NetworkRequests => Volatile.Read(ref _requests);

    internal static CacheableResourceFixture Serve(int status, string headers)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fixture = new CacheableResourceFixture(listener, new Uri($"http://127.0.0.1:{port}/shared.js"));
        _ = Task.Run(() => fixture.RunAsync(status, headers));
        return fixture;
    }

    private async Task RunAsync(int status, string headers)
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            _ = await HttpFixture.ReadRequestAsync(stream, _shutdown.Token).ConfigureAwait(false);
                            Interlocked.Increment(ref _requests);
                            await Task.Delay(80, _shutdown.Token).ConfigureAwait(false);
                            const string body = "globalThis.__sharedRuns=(globalThis.__sharedRuns||0)+1;";
                            var response =
                                $"HTTP/1.1 {status} Test\r\nContent-Type: application/javascript\r\n"
                                + $"Content-Length: {body.Length}\r\n{headers}Connection: close\r\n\r\n{body}";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _shutdown.Token)
                                .ConfigureAwait(false);
                            await stream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                            client.Client.Shutdown(SocketShutdown.Send);
                        }
                        catch (Exception error) when (error is OperationCanceledException or SocketException
                            or IOException or ObjectDisposedException)
                        {
                            // shutting down
                        }
                    }
                });
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException
            or ObjectDisposedException)
        {
            // shutting down
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>A server that accepts one connection, reads it, then never answers.</summary>
internal sealed class HangingFixture : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HangingFixture(TcpListener listener, Uri url)
    {
        _listener = listener;
        Url = url;
    }

    internal Uri Url { get; }

    internal Task Started => _started.Task;

    internal static HangingFixture Serve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fixture = new HangingFixture(listener, new Uri($"http://127.0.0.1:{port}/hang"));
        _ = Task.Run(fixture.RunAsync);
        return fixture;
    }

    private async Task RunAsync()
    {
        try
        {
            var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            using (client)
            {
                var buffer = new byte[2048];
                _ = await client.GetStream().ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false);
                _started.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException
            or IOException or ObjectDisposedException)
        {
            _started.TrySetResult();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>
/// Serves three connections: the first is read and then held open forever (so the
/// shared-fetch leader can be cancelled mid-flight), the next two answer with a
/// cacheable body. Stands in for <c>cancelled_shared_fetch_fixture</c>.
/// </summary>
internal sealed class CancelledSharedFetchFixture : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _requests;

    private CancelledSharedFetchFixture(TcpListener listener, Uri url)
    {
        _listener = listener;
        Url = url;
    }

    internal Uri Url { get; }

    internal Task Started => _started.Task;

    internal int NetworkRequests => Volatile.Read(ref _requests);

    internal static CancelledSharedFetchFixture Serve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fixture = new CancelledSharedFetchFixture(
            listener,
            new Uri($"http://127.0.0.1:{port}/shared.js"));
        _ = Task.Run(fixture.RunAsync);
        return fixture;
    }

    private async Task RunAsync()
    {
        TcpClient? held = null;
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                var stream = client.GetStream();
                var buffer = new byte[2048];
                _ = await stream.ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _requests);
                if (index == 0)
                {
                    _started.TrySetResult();
                    // Hold the transport open until the leader task is cancelled. The
                    // next two connections prove both the waiting follower retry and a
                    // fresh cache leader work.
                    held = client;
                    continue;
                }

                const string body = "shared";
                var response =
                    "HTTP/1.1 200 OK\r\nCache-Control: public, max-age=3600\r\n"
                    + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _shutdown.Token)
                    .ConfigureAwait(false);
                await stream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Send);
                client.Dispose();
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException
            or IOException or ObjectDisposedException)
        {
            _started.TrySetResult();
        }
        finally
        {
            held?.Dispose();
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>
/// Mints a throwaway CA plus a 127.0.0.1 leaf it signed, and serves one canned
/// HTTPS response with the leaf on an ephemeral port. Stands in for
/// <c>https_fixture_with_private_ca</c>.
/// </summary>
internal sealed class PrivateCaHttpsFixture : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly X509Certificate2 _serverCertificate;

    private PrivateCaHttpsFixture(TcpListener listener, X509Certificate2 serverCertificate, int port, string caPem)
    {
        _listener = listener;
        _serverCertificate = serverCertificate;
        Port = port;
        CaPem = caPem;
    }

    internal int Port { get; }

    internal string CaPem { get; }

    internal static PrivateCaHttpsFixture Serve()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=obscura-test-ca",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var caCert = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(2));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest(
            "CN=127.0.0.1",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
        leafRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(sanBuilder.Build());
        leafRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));

        using var leafCert = leafRequest.Create(
            caCert,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            Guid.NewGuid().ToByteArray());
        using var leafWithKey = leafCert.CopyWithPrivateKey(leafKey);
        var serverCertificate = X509CertificateLoader.LoadPkcs12(
            leafWithKey.Export(X509ContentType.Pfx, "obscura"),
            "obscura");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fixture = new PrivateCaHttpsFixture(
            listener,
            serverCertificate,
            port,
            caCert.ExportCertificatePem());
        _ = Task.Run(fixture.RunAsync);
        return fixture;
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                        await using (tls.ConfigureAwait(false))
                        {
                            try
                            {
                                await tls.AuthenticateAsServerAsync(_serverCertificate)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is AuthenticationException
                                or IOException or SocketException or ObjectDisposedException)
                            {
                                // Handshake rejection is the point of one test.
                                return;
                            }

                            try
                            {
                                var buffer = new byte[1024];
                                _ = await tls.ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false);
                                const string body = "private ca ok";
                                var response =
                                    "HTTP/1.1 200 OK\r\ncontent-type: text/plain\r\n"
                                    + $"content-length: {body.Length}\r\nconnection: close\r\n\r\n{body}";
                                await tls.WriteAsync(Encoding.ASCII.GetBytes(response), _shutdown.Token)
                                    .ConfigureAwait(false);
                                await tls.FlushAsync(_shutdown.Token).ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is OperationCanceledException
                                or IOException or SocketException or ObjectDisposedException)
                            {
                                // shutting down
                            }
                        }
                    }
                });
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException
            or ObjectDisposedException)
        {
            // shutting down
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Dispose();
        _serverCertificate.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>Sets environment variables for the duration of a test and restores them.</summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly List<(string Name, string? Previous)> _saved = [];

    internal EnvironmentScope Set(string name, string? value)
    {
        _saved.Add((name, Environment.GetEnvironmentVariable(name)));
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        for (var i = _saved.Count - 1; i >= 0; i--)
        {
            Environment.SetEnvironmentVariable(_saved[i].Name, _saved[i].Previous);
        }
    }
}
