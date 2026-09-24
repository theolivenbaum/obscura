using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace PocketCalculator.Net;

/// <summary>
/// TLS for the control planes (<c>serve</c>'s CDP server and <c>mcp --http</c>): a PEM
/// certificate and key loaded once, and the server side of the handshake on an accepted
/// stream. The Rust engine speaks plaintext only (SECURITY.md I1), so a token crossed the
/// network in clear text on a non-loopback bind.
/// </summary>
/// <remarks>
/// <see cref="SslStream"/> is the runtime's managed TLS surface over the platform library it
/// already uses for outbound https (OpenSSL on Linux, SChannel on Windows, Apple's on macOS),
/// so this adds no native dependency.
/// </remarks>
public static class ServerTls
{
    /// <summary>The certificate file variable, the counterpart of <c>--tls-cert</c>.</summary>
    public const string CertEnv = "POCKETCALCULATOR_TLS_CERT";

    /// <summary>The private key file variable, the counterpart of <c>--tls-key</c>.</summary>
    public const string KeyEnv = "POCKETCALCULATOR_TLS_KEY";

    /// <summary>How long a client has to finish the handshake.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The certificate for <paramref name="certPath"/> and <paramref name="keyPath"/>, or for
    /// <see cref="CertEnv"/> and <see cref="KeyEnv"/> when neither argument is given; null
    /// when neither is configured. Naming only one of the two is an error.
    /// </summary>
    /// <exception cref="InvalidOperationException">One path without the other, or files that do not load.</exception>
    public static X509Certificate2? Resolve(string? certPath, string? keyPath)
    {
        if (certPath is null && keyPath is null)
        {
            certPath = NonEmpty(Environment.GetEnvironmentVariable(CertEnv));
            keyPath = NonEmpty(Environment.GetEnvironmentVariable(KeyEnv));
        }

        if (certPath is null && keyPath is null)
        {
            return null;
        }

        if (certPath is null || keyPath is null)
        {
            throw new InvalidOperationException("TLS needs both a certificate and a key (--tls-cert and --tls-key)");
        }

        return Load(certPath, keyPath);
    }

    /// <summary>Load a PEM certificate (chain) and its PEM private key.</summary>
    /// <exception cref="InvalidOperationException">The files do not load, or the key does not match.</exception>
    public static X509Certificate2 Load(string certPath, string keyPath)
    {
        try
        {
            using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            if (!pem.HasPrivateKey)
            {
                throw new InvalidOperationException($"TLS key {keyPath} does not match certificate {certPath}");
            }

            // A PEM key is ephemeral, which SChannel cannot use for a server handshake;
            // a PKCS#12 round trip gives every platform a usable key.
            return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"cannot load TLS certificate {certPath} / key {keyPath}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Run the server handshake on <paramref name="inner"/> within <see cref="HandshakeTimeout"/>.
    /// The returned stream owns <paramref name="inner"/>.
    /// </summary>
    public static async Task<SslStream> AuthenticateAsync(
        Stream inner,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(certificate);
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(HandshakeTimeout);
            await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.None,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                    },
                    deadline.Token)
                .ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
