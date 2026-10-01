using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// SECURITY.md I1: the CDP server spoke plaintext only, so the bearer token crossed the
/// network in clear text on a non-loopback bind. With a certificate it serves the
/// discovery endpoints and the WebSocket over TLS and advertises <c>wss://</c>.
/// </summary>
public sealed class ServeOverTlsTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task DiscoveryAndWebSocketRunOverTls()
    {
        using var certificate = TestCertificate.Create(out var certPath, out var keyPath);
        using var loaded = ServerTls.Load(certPath, keyPath);
        await using var server = await TlsServer.StartAsync(loaded, null);

        var version = await HttpsGetAsync(server.Port, "/json/version", null, loaded);
        Assert.StartsWith("HTTP/1.1 200 OK", version, StringComparison.Ordinal);
        Assert.Contains($"\"webSocketDebuggerUrl\": \"wss://127.0.0.1:{server.Port}/devtools/browser\"", version,
            StringComparison.Ordinal);

        var list = await HttpsGetAsync(server.Port, "/json/list", null, loaded);
        Assert.Contains($"wss://127.0.0.1:{server.Port}/devtools/page/page-1", list, StringComparison.Ordinal);

        using var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented?.GetCertHashString() == loaded.GetCertHashString();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{server.Port}/devtools/browser"), cts.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"id\":7,\"method\":\"Browser.getVersion\"}"),
            WebSocketMessageType.Text, true, cts.Token);
        var reply = await ReceiveTextAsync(ws, cts.Token);
        Assert.StartsWith("{\"id\":7,\"result\":", reply, StringComparison.Ordinal);
        // The server closes the socket on a close frame without echoing it, as upstream does.
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
    }

    [Fact]
    public async Task PlaintextClientGetsNoResponseFromATlsServer()
    {
        using var certificate = TestCertificate.Create(out var certPath, out var keyPath);
        using var loaded = ServerTls.Load(certPath, keyPath);
        await using var server = await TlsServer.StartAsync(loaded, null);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /json/version HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new StringBuilder();
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                received.Append(Encoding.Latin1.GetString(buffer, 0, n));
            }
        }
        catch (IOException)
        {
        }

        Assert.DoesNotContain("webSocketDebuggerUrl", received.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenIsStillCheckedOverTls()
    {
        using var certificate = TestCertificate.Create(out var certPath, out var keyPath);
        using var loaded = ServerTls.Load(certPath, keyPath);
        await using var server = await TlsServer.StartAsync(loaded, Token);

        Assert.StartsWith("HTTP/1.1 401 ", await HttpsGetAsync(server.Port, "/json/version", null, loaded),
            StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200 ", await HttpsGetAsync(server.Port, "/json/version", Token, loaded),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonLoopbackTlsBindStillNeedsAToken()
    {
        using var certificate = TestCertificate.Create(out var certPath, out var keyPath);
        using var loaded = ServerTls.Load(certPath, keyPath);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CdpServer.StartWithControlTokenAsync(
                CdpTestClient.PickPort(), "0.0.0.0", null, false, null, false, null, false,
                CdpServer.DefaultMaxConnections, () => null, () => null, loaded));
        Assert.Equal(
            "refusing to expose CDP without authentication; set POCKETCALCULATOR_CDP_TOKEN to at least 32 bytes",
            error.Message);
    }

    [Fact]
    public void OnePathWithoutTheOtherIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => ServerTls.Resolve("/tmp/cert.pem", null));
        Assert.Throws<InvalidOperationException>(() => ServerTls.Resolve(null, "/tmp/key.pem"));
    }

    private static async Task<string> HttpsGetAsync(int port, string path, string? token, X509Certificate2 pinned)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var ssl = new SslStream(client.GetStream(), false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented?.GetCertHashString() == pinned.GetCertHashString(),
            },
            cts.Token);
        var request = $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
            + (token is null ? string.Empty : $"Authorization: Bearer {token}\r\n")
            + "Connection: close\r\n\r\n";
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
        var received = new StringBuilder();
        var buffer = new byte[4096];
        try
        {
            int n;
            while ((n = await ssl.ReadAsync(buffer, cts.Token)) > 0)
            {
                received.Append(Encoding.UTF8.GetString(buffer, 0, n));
            }
        }
        catch (IOException)
        {
        }

        return received.ToString();
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var text = new StringBuilder();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, token);
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return text.ToString();
            }
        }
    }

    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _server = Task.CompletedTask;

        internal int Port { get; private init; }

        internal static async Task<TlsServer> StartAsync(X509Certificate2 certificate, string? token)
        {
            var server = new TlsServer { Port = CdpTestClient.PickPort() };
            server._server = Task.Run(() => CdpServer.StartWithControlTokenAsync(
                server.Port, "127.0.0.1", null, false, null, false, null, true,
                CdpServer.DefaultMaxConnections, () => token, () => null, certificate, server._stop.Token));
            var deadline = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < deadline)
            {
                if (server._server.IsFaulted)
                {
                    await server._server;
                }

                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, server.Port);
                    return server;
                }
                catch (SocketException)
                {
                    await Task.Delay(25);
                }
            }

            throw new TimeoutException("TLS CDP server never bound");
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _server.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
            }

            _stop.Dispose();
        }
    }
}

/// <summary>A throwaway self-signed certificate written as PEM files.</summary>
internal static class TestCertificate
{
    internal static X509Certificate2 Create(out string certPath, out string keyPath)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var directory = Directory.CreateTempSubdirectory("pc-tls-").FullName;
        certPath = Path.Combine(directory, "cert.pem");
        keyPath = Path.Combine(directory, "key.pem");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return certificate;
    }
}
