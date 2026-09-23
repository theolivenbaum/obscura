namespace PocketCalculator.Net.Tests;

/// <summary>
/// SECURITY.md M5: cookie prefixes (RFC 6265bis 4.1.3, matched case-insensitively as
/// Chromium does) and Chromium's per-cookie and per-domain limits.
/// </summary>
public sealed class CookieHardeningTests
{
    private static readonly Uri SecureApp = new("https://app.example.com/login");
    private static readonly Uri SecureSibling = new("https://evil.example.com/");
    private static readonly Uri Insecure = new("http://app.example.com/");

    [Fact]
    public void HostPrefixRejectsDomainAttributeFromASibling()
    {
        // Session fixation: a sibling subdomain plants __Host-session for the parent.
        var jar = new CookieJar();
        jar.SetCookie("__Host-session=fixed; Secure; Path=/; Domain=example.com", SecureSibling);
        Assert.Equal(string.Empty, jar.GetCookieHeader(SecureApp));
    }

    [Theory]
    [InlineData("__Host-a=1; Secure; Path=/")]
    [InlineData("__host-a=1; Secure; Path=/")]
    [InlineData("__Secure-a=1; Secure")]
    [InlineData("__SECURE-a=1; Secure; Domain=example.com")]
    public void ValidPrefixedCookiesAreStored(string header)
    {
        var jar = new CookieJar();
        jar.SetCookie(header, SecureApp);
        Assert.NotEqual(string.Empty, jar.GetCookieHeader(SecureApp));
    }

    [Theory]
    [InlineData("__Host-a=1; Path=/")]                            // not Secure
    [InlineData("__Host-a=1; Secure")]                            // no Path attribute
    [InlineData("__Host-a=1; Secure; Path=/app")]                 // Path is not /
    [InlineData("__Host-a=1; Secure; Path=/; Domain=app.example.com")] // any Domain
    [InlineData("__HOST-a=1; Path=/")]                            // case-insensitive
    [InlineData("__Secure-a=1")]                                  // not Secure
    [InlineData("__secure-a=1")]
    [InlineData("=__Host-a=1; Secure; Path=/")]                   // nameless, prefixed value
    [InlineData("=__Secure-a=1; Secure")]
    public void InvalidPrefixedCookiesAreRejected(string header)
    {
        var jar = new CookieJar();
        jar.SetCookie(header, SecureApp);
        Assert.Equal(string.Empty, jar.GetCookieHeader(SecureApp));
    }

    [Fact]
    public void PrefixedCookieFromInsecureOriginIsRejected()
    {
        var jar = new CookieJar();
        jar.SetCookieFromJs("__Secure-a=1; Secure", Insecure);
        jar.SetCookieFromJs("__Host-a=1; Secure; Path=/", Insecure);
        Assert.Empty(jar.GetAllCookies());
    }

    [Fact]
    public void OversizedCookieIsRejected()
    {
        var jar = new CookieJar();
        jar.SetCookie("big=" + new string('x', 4093), SecureApp);   // 3 + 4093 = 4096 bytes: kept
        jar.SetCookie("huge=" + new string('x', 4093), SecureApp);  // 4 + 4093 = 4097 bytes: dropped
        var names = jar.GetAllCookies().Select(c => c.Name).ToList();
        Assert.Contains("big", names);
        Assert.DoesNotContain("huge", names);
    }

    [Fact]
    public void PerDomainCapEvictsLeastRecentlyUsed()
    {
        var jar = new CookieJar();
        jar.SetCookie("used=1; Path=/used", SecureApp);
        for (var i = 0; i < 400; i++)
        {
            if (i % 50 == 0)
            {
                // Sending "used" keeps it recently used, so eviction passes it over.
                _ = jar.GetCookieHeader(new Uri("https://app.example.com/used"));
            }

            jar.SetCookie($"c{i}=v; Path=/c", SecureApp);
        }

        var all = jar.GetAllCookies();
        Assert.True(all.Count <= 180, $"jar holds {all.Count} cookies for one domain");
        Assert.Contains(all, c => c.Name == "c399");
        Assert.Contains(all, c => c.Name == "used");
        Assert.DoesNotContain(all, c => c.Name == "c0");
    }

    [Fact]
    public void JarHasAGlobalCap()
    {
        var jar = new CookieJar();
        for (var host = 0; host < 40; host++)
        {
            var url = new Uri($"https://h{host}.example.net/");
            for (var i = 0; i < 150; i++)
            {
                jar.SetCookie($"c{i}=v; Path=/", url);
            }
        }

        Assert.True(jar.GetAllCookies().Count <= 3300);
    }
}
