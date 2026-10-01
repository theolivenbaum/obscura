using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

/// <summary>
/// SECURITY.md I1: <c>mcp --http</c> spoke plaintext only. With a certificate it serves
/// over TLS, and a plaintext client gets nothing.
/// </summary>
public sealed class HttpOverTlsTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string ToolsList = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    [Fact]
    public async Task ToolCallsRunOverTlsWithTheToken()
    {
        using var loaded = CreateCertificate();
        var port = SseStreamDoesNotWedgeTests.PickFreePort();
        using var cts = new CancellationTokenSource();
        var server = Task.Run(() => Http.RunAsync(
            "127.0.0.1", port, null, null, false, null, Token,
            new Http.ServerOptions { Certificate = loaded }, cts.Token));
        try
        {
            await SseStreamDoesNotWedgeTests.WaitForListenerAsync(port);

            var authorized = await SendAsync(port, loaded, Post(ToolsList, Token));
            Assert.StartsWith("HTTP/1.1 200 OK", authorized, StringComparison.Ordinal);
            Assert.Contains("\"tools\"", authorized, StringComparison.Ordinal);

            var anonymous = await SendAsync(port, loaded, Post(ToolsList, null));
            Assert.StartsWith("HTTP/1.1 401 ", anonymous, StringComparison.Ordinal);

            // A plaintext request to the TLS port is never answered as HTTP.
            using var plain = new TcpClient();
            await plain.ConnectAsync(IPAddress.Loopback, port);
            var stream = plain.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(Post(ToolsList, Token)));
            var received = await ReadAllAsync(stream);
            Assert.DoesNotContain("HTTP/1.1", received, StringComparison.Ordinal);
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAny(server, Task.Delay(2000));
        }
    }

    private static string Post(string body, string? token) =>
        "POST /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\n"
        + (token is null ? string.Empty : $"Authorization: Bearer {token}\r\n")
        + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static async Task<string> SendAsync(int port, X509Certificate2 pinned, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var ssl = new SslStream(client.GetStream(), false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented?.GetCertHashString() == pinned.GetCertHashString(),
            },
            cts.Token);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
        return await ReadAllAsync(ssl);
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new StringBuilder();
        var buffer = new byte[8192];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                received.Append(Encoding.UTF8.GetString(buffer, 0, n));
                var text = received.ToString();
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0 && TryContentLength(text[..end], out var length)
                    && Encoding.UTF8.GetByteCount(text[(end + 4)..]) >= length)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
        }

        return received.ToString();
    }

    private static bool TryContentLength(string head, out int length)
    {
        length = 0;
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(line["Content-Length:".Length..].Trim(), out length);
            }
        }

        return false;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var directory = Directory.CreateTempSubdirectory("pc-mcp-tls-").FullName;
        var certPath = Path.Combine(directory, "cert.pem");
        var keyPath = Path.Combine(directory, "key.pem");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return ServerTls.Load(certPath, keyPath);
    }
}
