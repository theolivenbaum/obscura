using System.Security.Cryptography.X509Certificates;

namespace Obscura.Net;

/// <summary>
/// The <c>SSL_CERT_FILE</c> / <c>SSL_CERT_DIR</c> trust-store additions
/// (port of <c>configured_root_paths</c> / <c>configured_root_certificates</c>).
/// </summary>
internal static class CertificateRoots
{
    private static readonly Dictionary<(string File, string Dir), X509Certificate2[]> Cache = new();
    private static readonly System.Threading.Lock CacheLock = new();

    /// <summary>
    /// Whether <c>SSL_CERT_FILE</c> / <c>SSL_CERT_DIR</c> request a custom TLS trust
    /// store. A variable that is set but empty (e.g. <c>SSL_CERT_FILE=""</c>, a common
    /// shell accident) is treated as unset, matching the non-empty filter in
    /// <c>ConfiguredRootPaths</c>. Supplying a store to a transport that REPLACES its
    /// bundled roots would otherwise build a near-empty store and break all HTTPS.
    /// </summary>
    internal static bool CustomCertStoreRequested(string? certFile, string? certDir) =>
        (certFile is not null && certFile.Length != 0)
        || (certDir is not null && certDir.Length != 0);

    /// <summary>True when either env var is present at all, empty or not.</summary>
    internal static bool AnyCertEnvSet() =>
        Environment.GetEnvironmentVariable("SSL_CERT_FILE") is not null
        || Environment.GetEnvironmentVariable("SSL_CERT_DIR") is not null;

    private static List<string> ConfiguredRootPaths()
    {
        var paths = new List<string>();
        var file = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        if (!string.IsNullOrEmpty(file))
        {
            paths.Add(file);
        }

        var directory = Environment.GetEnvironmentVariable("SSL_CERT_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                paths.AddRange(Directory.GetFileSystemEntries(directory));
            }
            catch (IOException)
            {
                // failed to read SSL_CERT_DIR
            }
            catch (UnauthorizedAccessException)
            {
                // failed to read SSL_CERT_DIR
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>
    /// Parse every configured root. Cached per (SSL_CERT_FILE, SSL_CERT_DIR) value
    /// pair rather than once per process, so a test that changes the environment does
    /// not read a stale store.
    /// </summary>
    internal static X509Certificate2[] Configured()
    {
        var key = (
            Environment.GetEnvironmentVariable("SSL_CERT_FILE") ?? string.Empty,
            Environment.GetEnvironmentVariable("SSL_CERT_DIR") ?? string.Empty);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var certificates = new List<X509Certificate2>();
            foreach (var path in ConfiguredRootPaths())
            {
                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                var collection = new X509Certificate2Collection();
                try
                {
                    collection.ImportFromPem(System.Text.Encoding.ASCII.GetString(bytes));
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    collection.Clear();
                }
                catch (ArgumentException)
                {
                    collection.Clear();
                }

                if (collection.Count != 0)
                {
                    certificates.AddRange(collection);
                    continue;
                }

                try
                {
                    certificates.Add(X509CertificateLoader.LoadCertificate(bytes));
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // failed to parse CA certificate file
                }
            }

            var result = certificates.ToArray();
            Cache[key] = result;
            return result;
        }
    }
}
