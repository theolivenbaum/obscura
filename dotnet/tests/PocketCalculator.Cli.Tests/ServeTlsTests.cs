using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PocketCalculator.Cli.Commands;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// SECURITY.md I1: <c>serve --tls-cert/--tls-key</c> serves CDP over TLS, workers included,
/// and a half-configured TLS setup refuses to start.
/// </summary>
public sealed partial class MultiWorkerServeTests
{
    [Fact]
    public void WorkersReceiveTheTlsFlags()
    {
        var args = ServeCommand.WorkerStartInfo(
            new ServeCommand.WorkerLauncher("/app/pocket-calculator", []),
            new ServeCommand.WorkerSettings { TlsCert = "/etc/pc/cert.pem", TlsKey = "/etc/pc/key.pem" },
            4242,
            "127.0.0.1",
            "9222").ArgumentList.ToList();
        Assert.Equal("/etc/pc/cert.pem", args[args.IndexOf("--tls-cert") + 1]);
        Assert.Equal("/etc/pc/key.pem", args[args.IndexOf("--tls-key") + 1]);
    }

    [Fact]
    public void TlsCertWithoutKeyRefusesToStart()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        var run = CliProcess.Run("serve", "--port", FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--tls-cert", "/nonexistent/cert.pem");
        Assert.False(run.Success);
        Assert.Contains("TLS needs both a certificate and a key", run.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TlsWorkersServeDiscoveryOverTlsThroughTheBalancer()
    {
        var (certPath, keyPath, hash) = WriteCertificate();
        var port = FreePort();
        using var balancer = Start("127.0.0.1", port, Token, serveExtra: ["--tls-cert", certPath, "--tls-key", keyPath]);

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        using var ssl = new SslStream(client.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == hash,
            },
            cts.Token);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(Get("/json/version", $"127.0.0.1:{port}", Token)), cts.Token);
        using var reader = new StreamReader(ssl, Encoding.UTF8);
        var response = await reader.ReadToEndAsync(cts.Token);
        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains($"wss://127.0.0.1:{port}/devtools/browser", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The balancer's own max-connections 503 goes out over TLS on a TLS listener:
    /// a TLS client reads it after the handshake, and a plaintext client never
    /// sees a plaintext HTTP reply on the TLS port.
    /// </summary>
    [Fact]
    public async Task TheBalancerRefusesOverTlsOnATlsListener()
    {
        var (certPath, keyPath, hash) = WriteCertificate();
        var port = FreePort();
        using var balancer = Start("127.0.0.1", port, Token, maxConnections: 1,
            serveExtra: ["--tls-cert", certPath, "--tls-key", keyPath]);

        using var holder = new TcpClient();
        await holder.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        using (var client = new TcpClient())
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            using var ssl = new SslStream(client.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == hash,
                },
                cts.Token);
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(Get("/json/version", $"127.0.0.1:{port}", Token)), cts.Token);
            using var reader = new StreamReader(ssl, Encoding.UTF8);
            var response = await reader.ReadToEndAsync(cts.Token);
            Assert.StartsWith("HTTP/1.1 503 Service Unavailable\r\n", response, StringComparison.Ordinal);
        }

        string plain;
        try
        {
            plain = await RequestAsync(port, Get("/json/version", $"127.0.0.1:{port}", Token));
        }
        catch (IOException)
        {
            plain = string.Empty;
        }
        Assert.DoesNotContain("HTTP/1.1", plain, StringComparison.Ordinal);
    }

    /// <summary>A refusal with a certificate handshakes first, so a plaintext client reads no HTTP.</summary>
    [Fact]
    public async Task RefuseAsyncWritesNothingPlaintextWithACertificate()
    {
        var (certPath, keyPath, _) = WriteCertificate();
        using var certificate = PocketCalculator.Net.ServerTls.Load(certPath, keyPath);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);
            var refuse = ServeCommand.RefuseAsync(await accept, certificate);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"), TestContext.Current.CancellationToken);
            var received = new MemoryStream();
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
                {
                    received.Write(buffer, 0, n);
                }
            }
            catch (IOException)
            {
            }
            await refuse;
            Assert.DoesNotContain("HTTP/1.1", Encoding.Latin1.GetString(received.ToArray()), StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static (string Cert, string Key, string Hash) WriteCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var directory = Directory.CreateTempSubdirectory("pc-cli-tls-").FullName;
        var certPath = Path.Combine(directory, "cert.pem");
        var keyPath = Path.Combine(directory, "key.pem");
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath, certificate.GetCertHashString());
    }
}
