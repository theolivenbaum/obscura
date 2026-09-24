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
