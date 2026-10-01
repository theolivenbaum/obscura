using System.Text.Json;

namespace PocketCalculator.Net.Tests;

/// <summary>Port of the <c>#[cfg(test)] mod tests</c> block in <c>cookies.rs</c>.</summary>
public class CookieTests
{
    private const string DefaultSameSite = "Lax";

    [Fact]
    public void TestSetAndGetCookie()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/path");
        jar.SetCookie("session=abc123; Path=/; Secure; HttpOnly", url);

        var header = jar.GetCookieHeader(url);
        Assert.Contains("session=abc123", header, StringComparison.Ordinal);
    }

    // RFC 6265 5.3: document.cookie (a non-HTTP API) must not overwrite or delete a
    // server-set HttpOnly cookie. See upstream #915.
    [Fact]
    public void JsCannotOverwriteHttponlyCookie()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookie("session=server_secret; Path=/; HttpOnly", url);

        jar.SetCookieFromJs("session=attacker_value", url);

        Assert.Contains("session=server_secret", jar.GetCookieHeader(url), StringComparison.Ordinal);
        Assert.DoesNotContain("attacker_value", jar.GetCookieHeader(url), StringComparison.Ordinal);
    }

    [Fact]
    public void JsCannotDeleteHttponlyCookie()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookie("session=server_secret; Path=/; HttpOnly", url);

        jar.SetCookieFromJs("session=; Max-Age=0", url);

        Assert.Contains("session=server_secret", jar.GetCookieHeader(url), StringComparison.Ordinal);
    }

    [Fact]
    public void JsCanStillOverwriteNonHttponlyCookie()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookie("pref=light; Path=/", url);

        jar.SetCookieFromJs("pref=dark", url);

        Assert.Contains("pref=dark", jar.GetCookieHeader(url), StringComparison.Ordinal);
    }

    [Fact]
    public void TestCookieDomainMatching()
    {
        var jar = new CookieJar();
        var url = new Uri("https://www.example.com/");
        jar.SetCookie("token=xyz; Domain=example.com", url);

        Assert.Contains("token=xyz", jar.GetCookieHeader(url), StringComparison.Ordinal);

        var subUrl = new Uri("https://api.example.com/");
        Assert.Contains("token=xyz", jar.GetCookieHeader(subUrl), StringComparison.Ordinal);

        var otherUrl = new Uri("https://other.com/");
        Assert.Equal(string.Empty, jar.GetCookieHeader(otherUrl));
    }

    [Fact]
    public void TestCdpCookieWithLeadingDotDomainMatchesRequests()
    {
        var jar = new CookieJar();
        jar.SetCookiesFromCdp([
            new CookieInfo
            {
                Name = "token",
                Value = "xyz",
                Domain = ".example.com",
                Path = "/",
                Secure = false,
                HttpOnly = false,
                SameSite = string.Empty,
                Expires = null,
            },
        ]);

        var apexUrl = new Uri("https://example.com/");
        Assert.Contains("token=xyz", jar.GetCookieHeader(apexUrl), StringComparison.Ordinal);

        var subdomainUrl = new Uri("https://api.example.com/");
        Assert.Contains("token=xyz", jar.GetCookieHeader(subdomainUrl), StringComparison.Ordinal);

        var otherUrl = new Uri("https://other.com/");
        Assert.Equal(string.Empty, jar.GetCookieHeader(otherUrl));
    }

    [Fact]
    public void TestSecureCookieNotSentOverHttp()
    {
        var jar = new CookieJar();
        var httpsUrl = new Uri("https://example.com/");
        jar.SetCookie("secure_token=secret; Secure", httpsUrl);

        var httpUrl = new Uri("http://example.com/");
        Assert.Equal(string.Empty, jar.GetCookieHeader(httpUrl));
    }

    [Fact]
    public void TestMaxAgeZeroDeletesCookie()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookie("session=abc", url);
        Assert.Contains("session=abc", jar.GetCookieHeader(url), StringComparison.Ordinal);

        jar.SetCookie("session=abc; Max-Age=0", url);
        Assert.Equal(string.Empty, jar.GetCookieHeader(url));
    }

    [Fact]
    public void TestSameNameCookiesWithDifferentPathsCoexist()
    {
        // RFC 6265 section 5.3: a cookie is identified by (name, domain, path). Two
        // cookies that share a name but differ in path are distinct and must both be
        // retained - storing by name alone clobbers the first.
        var jar = new CookieJar();
        var setUrl = new Uri("https://example.com/");
        jar.SetCookie("id=1; Path=/a", setUrl);
        jar.SetCookie("id=2; Path=/b", setUrl);

        var headerA = jar.GetCookieHeader(new Uri("https://example.com/a/page"));
        var headerB = jar.GetCookieHeader(new Uri("https://example.com/b/page"));
        Assert.Contains("id=1", headerA, StringComparison.Ordinal);
        Assert.Contains("id=2", headerB, StringComparison.Ordinal);
        Assert.DoesNotContain("id=2", headerA, StringComparison.Ordinal);
        Assert.DoesNotContain("id=1", headerB, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSameNameSamePathCookieIsReplaced()
    {
        // Same (name, path): the newer value replaces the older one - still one entry,
        // not two.
        var jar = new CookieJar();
        var url = new Uri("https://example.com/a/x");
        jar.SetCookie("id=1; Path=/a", url);
        jar.SetCookie("id=2; Path=/a", url);
        var header = jar.GetCookieHeader(url);
        Assert.Contains("id=2", header, StringComparison.Ordinal);
        Assert.DoesNotContain("id=1", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TestMaxAgeZeroDeletesOnlyMatchingPath()
    {
        // A Max-Age=0 Set-Cookie deletes the (name, path) it targets, leaving a
        // same-name cookie on a different path intact.
        var jar = new CookieJar();
        var setUrl = new Uri("https://example.com/");
        jar.SetCookie("id=1; Path=/a", setUrl);
        jar.SetCookie("id=2; Path=/b", setUrl);
        jar.SetCookie("id=x; Path=/a; Max-Age=0", setUrl);

        var headerA = jar.GetCookieHeader(new Uri("https://example.com/a/page"));
        var headerB = jar.GetCookieHeader(new Uri("https://example.com/b/page"));
        Assert.Equal(string.Empty, headerA);
        Assert.Contains("id=2", headerB, StringComparison.Ordinal);
    }

    [Fact]
    public void TestMaxAgeSetsExpiry()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookie("token=xyz; Max-Age=3600", url);
        Assert.Contains("token=xyz", jar.GetCookieHeader(url), StringComparison.Ordinal);
    }

    [Fact]
    public void TestExpiredCookieNotSent()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookie("old=current", url);
        jar.SetCookie("old=gone; Expires=Thu, 01 Jan 2020 00:00:00 GMT", url);
        Assert.Equal(string.Empty, jar.GetCookieHeader(url));
        Assert.Empty(jar.GetAllCookies());
    }

    [Fact]
    public void TestExpiredJsCookieDeletesExistingCookie()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookieFromJs("old=current", url);
        jar.SetCookieFromJs("old=gone; Expires=Thu, 01 Jan 2020 00:00:00 GMT", url);
        Assert.Empty(jar.GetAllCookies());
    }

    [Fact]
    public void TestSamesiteParsed()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookie("strict_cookie=val; SameSite=Strict", url);
        Assert.Contains("strict_cookie=val", jar.GetCookieHeader(url), StringComparison.Ordinal);
    }

    [Fact]
    public void TestClearCookies()
    {
        var jar = new CookieJar();
        var url = new Uri("https://example.com/");
        jar.SetCookie("a=1", url);
        Assert.NotEqual(string.Empty, jar.GetCookieHeader(url));

        jar.Clear();
        Assert.Equal(string.Empty, jar.GetCookieHeader(url));
    }

    [Fact]
    public void TestSetCookiesFromCdpPreservesSameSiteAndExpires()
    {
        var jar = new CookieJar();
        var futureExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        jar.SetCookiesFromCdp([
            new CookieInfo
            {
                Name = "sid",
                Value = "abc",
                Domain = "example.com",
                Path = "/",
                Secure = true,
                HttpOnly = true,
                SameSite = "Strict",
                Expires = futureExpiry,
            },
        ]);

        var cookies = jar.GetAllCookies();
        Assert.Single(cookies);
        Assert.Equal("Strict", cookies[0].SameSite);
        Assert.Equal(futureExpiry, cookies[0].Expires);
    }

    [Fact]
    public void TestSetCookiesFromCdpSessionWhenExpiresNone()
    {
        var jar = new CookieJar();
        jar.SetCookiesFromCdp([Cdp("n", "v", "example.com", "/")]);
        var cookies = jar.GetAllCookies();
        Assert.Null(cookies[0].Expires);
        Assert.Equal(DefaultSameSite, cookies[0].SameSite);
    }

    [Fact]
    public void TestSetCookiesFromCdpMinusOneIsASessionCookie()
    {
        var jar = new CookieJar();
        var cookie = Cdp("n", "v", "example.com", "/");
        cookie.Expires = -1;
        jar.SetCookiesFromCdp([cookie]);
        var cookies = jar.GetAllCookies();
        Assert.Single(cookies);
        Assert.Null(cookies[0].Expires);
    }

    private static CookieInfo Cdp(string name, string value, string domain, string path) => new()
    {
        Name = name,
        Value = value,
        Domain = domain,
        Path = path,
        Secure = false,
        HttpOnly = false,
        SameSite = string.Empty,
        Expires = null,
    };

    private static List<CookieInfo> Marker(CookieJar jar) =>
        jar.GetAllCookies().Where(c => c.Name == "marker").ToList();

    [Fact]
    public void TestCdpDomainLeadingDotIsNotASeparateCookie()
    {
        // The two entrances disagreed: CDP retained the dot, while Set-Cookie stripped it.
        var jar = new CookieJar();
        var url = new Uri("https://app.example.com/");

        jar.SetCookiesFromCdp([Cdp("marker", "stale", ".example.com", "/")]);
        jar.SetCookie("marker=fresh; Domain=example.com; Path=/", url);

        Assert.Single(Marker(jar));
        var header = jar.GetCookieHeader(url);
        Assert.Equal(1, CountOccurrences(header, "marker="));
        Assert.Contains("marker=fresh", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TestCdpStoredDomainFieldCarriesNoDot()
    {
        // The dotted spelling comes LAST. That is why an implementation which only
        // canonicalizes the map key and leaves the entry's domain raw also fails here.
        var jar = new CookieJar();
        jar.SetCookiesFromCdp([Cdp("marker", "one", "example.com", "/")]);
        jar.SetCookiesFromCdp([Cdp("marker", "two", ".EXAMPLE.com", "/")]);

        var all = Marker(jar);
        Assert.Single(all);
        Assert.Equal("example.com", all[0].Domain);
        Assert.Equal("two", all[0].Value);
    }

    [Fact]
    public void TestCdpDomainLeadingDotRepairsAPersistedStore()
    {
        // Loading from file imports through here, so an old store must not resurrect
        // the duplicate.
        var jar = new CookieJar();
        jar.SetCookiesFromCdp([
            Cdp("marker", "one", ".example.com", "/"),
            Cdp("marker", "two", "example.com", "/"),
        ]);
        Assert.Single(Marker(jar));
    }

    [Fact]
    public void TestCdpZeroExpiryDeletesAcrossDomainSpellings()
    {
        // The insert canonicalizes the key, so every delete path has to canonicalize
        // its lookup too. Otherwise the cookie becomes unreachable and keeps going out.
        (string Stored, string Deleted)[] cases =
        [
            ("Example.COM", "Example.COM"),
            (".example.com", "example.com"),
            ("example.com", ".EXAMPLE.com"),
        ];
        foreach (var (stored, deleted) in cases)
        {
            var jar = new CookieJar();
            jar.SetCookiesFromCdp([Cdp("marker", "current", stored, "/")]);
            var gone = Cdp("marker", string.Empty, deleted, "/");
            gone.Expires = 0;
            jar.SetCookiesFromCdp([gone]);
            Assert.Empty(Marker(jar));
        }
    }

    [Fact]
    public void TestDeleteCookieCanonicalizesItsLookup()
    {
        foreach (var spelling in new[] { "Example.COM", ".example.com", "example.com" })
        {
            var jar = new CookieJar();
            jar.SetCookiesFromCdp([Cdp("marker", "current", "Example.COM", "/")]);
            jar.DeleteCookie("marker", spelling);
            Assert.Empty(Marker(jar));

            jar = new CookieJar();
            jar.SetCookiesFromCdp([Cdp("marker", "current", "Example.COM", "/")]);
            jar.DeleteCookiesFiltered("marker", spelling, "/");
            Assert.Empty(Marker(jar));
        }
    }

    [Fact]
    public void TestSetCookiesFromCdpZeroExpiryDeletesMatchingCookie()
    {
        var jar = new CookieJar();
        jar.SetCookiesFromCdp([Cdp("sid", "current", ".example.com", "/account")]);
        var gone = Cdp("sid", string.Empty, "example.com", "/account");
        gone.Expires = 0;
        jar.SetCookiesFromCdp([gone]);
        Assert.Empty(jar.GetAllCookies());
    }

    [Fact]
    public void TestDeleteCookiesFilteredPathMismatchPreservesCookie()
    {
        var jar = new CookieJar();
        jar.SetCookiesFromCdp([Cdp("sid", "v", "example.com", "/admin")]);
        jar.DeleteCookiesFiltered("sid", "example.com", "/other");
        Assert.Single(jar.GetAllCookies());

        jar.DeleteCookiesFiltered("sid", "example.com", "/admin");
        Assert.Empty(jar.GetAllCookies());
    }

    [Fact]
    public void TestDeleteCookiesFilteredNoPathDeletesRegardless()
    {
        var jar = new CookieJar();
        jar.SetCookiesFromCdp([Cdp("sid", "v", "example.com", "/admin")]);
        jar.DeleteCookiesFiltered("sid", "example.com", null);
        Assert.Empty(jar.GetAllCookies());
    }

    [Fact]
    public void TestSetCookiesFromCdpExpiredDoesNotPersist()
    {
        var jar = new CookieJar();
        jar.SetCookiesFromCdp([Cdp("old", "current", "example.com", "/")]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = Cdp("old", "v", "example.com", "/");
        expired.Expires = now - 1;
        jar.SetCookiesFromCdp([expired]);
        Assert.Empty(jar.GetAllCookies());
    }

    [Fact]
    public void TestSaveLoadRoundtrip()
    {
        var directory = Directory.CreateTempSubdirectory("obscura-cookies");
        try
        {
            var path = Path.Combine(directory.FullName, "cookies.json");

            var jar = new CookieJar();
            var url = new Uri("https://example.com/");
            jar.SetCookie("session=abc123; Domain=example.com; Path=/", url);
            jar.SetCookie("token=xyz; Secure; HttpOnly", url);

            jar.SaveToFile(path);
            Assert.True(File.Exists(path));

            var jar2 = new CookieJar();
            var count = jar2.LoadFromFile(path);
            Assert.Equal(2, count);

            var header = jar2.GetCookieHeader(url);
            Assert.Contains("session=abc123", header, StringComparison.Ordinal);
            Assert.Contains("token=xyz", header, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// SECURITY.md I4: the jar holds session cookies, so its file (and a storage
    /// directory the save creates) is owner-only on Unix, whatever the umask.
    /// </summary>
    [Fact]
    public void SavedCookieFileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("obscura-cookies");
        try
        {
            var storage = Path.Combine(directory.FullName, "storage");
            var path = Path.Combine(storage, "cookies.json");
            var jar = new CookieJar();
            jar.SetCookie("session=abc123; Path=/", new Uri("https://example.com/"));

            jar.SaveToFile(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(storage));

            // A second save replaces the file and keeps the mode.
            jar.SaveToFile(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Single(Directory.GetFiles(storage));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TestLoadNonexistentFileReturnsZero()
    {
        var jar = new CookieJar();
        Assert.Equal(0, jar.LoadFromFile("/nonexistent/cookies.json"));
    }

    [Fact]
    public void TestDomainMatchesSubdomainWithoutLeadingDot()
    {
        var jar = new CookieJar();
        var cookie = Cdp("session", "abc", "xiaohongshu.com", "/");
        cookie.HttpOnly = true;
        jar.SetCookiesFromCdp([cookie]);
        var url = new Uri("https://www.xiaohongshu.com/explore");
        var header = jar.GetCookieHeader(url);
        Assert.Contains("session=abc", header, StringComparison.Ordinal);
    }

    [Fact]
    public void TestCookieFromFileLoadThenSendInRequest()
    {
        // Simulate what happens: load cookies from file -> navigate -> cookie should
        // be in the request.
        var directory = Directory.CreateTempSubdirectory("obscura-cookies");
        try
        {
            var path = Path.Combine(directory.FullName, "cookies.json");

            // Write cookies like we exported from Chrome.
            var cookies = JsonSerializer.Serialize(new[]
            {
                new
                {
                    name = "a1",
                    value = "testval",
                    domain = "xiaohongshu.com",
                    path = "/",
                    secure = false,
                    httpOnly = false,
                },
                new
                {
                    name = "web_session",
                    value = "sess123",
                    domain = "xiaohongshu.com",
                    path = "/",
                    secure = false,
                    httpOnly = true,
                },
            });
            File.WriteAllText(path, cookies);

            var jar = new CookieJar();
            var count = jar.LoadFromFile(path);
            Assert.Equal(2, count);

            var url = new Uri("https://www.xiaohongshu.com/explore");
            var header = jar.GetCookieHeader(url);
            Assert.Contains("a1=testval", header, StringComparison.Ordinal);
            Assert.Contains("web_session=sess123", header, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AttackerResponseCannotSetUnrelatedVictimDomainCookie()
    {
        // GHSA-f22c-8v6q-v6h6: a response from attacker.test must not be able to plant
        // a cookie scoped to victim.test.
        var jar = new CookieJar();
        var attacker = new Uri("http://attacker.test/");
        jar.SetCookie("sid=attacker; Domain=victim.test; Path=/", attacker);

        var victim = new Uri("http://victim.test/account");
        Assert.DoesNotContain("sid=attacker", jar.GetCookieHeader(victim), StringComparison.Ordinal);
        // Upstream 04418a5: an invalid Domain attribute rejects the cookie rather than
        // silently changing its scope. It used to be stored host-only on the attacker
        // origin; Chromium rejects it too.
        Assert.DoesNotContain("sid=attacker", jar.GetCookieHeader(attacker), StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentCookieCannotSetUnrelatedVictimDomainCookie()
    {
        var jar = new CookieJar();
        var attacker = new Uri("http://attacker.test/");
        jar.SetCookieFromJs("js_sid=attacker; Domain=victim.test; Path=/", attacker);

        var victim = new Uri("http://victim.test/account");
        Assert.DoesNotContain("js_sid=attacker", jar.GetCookieHeader(victim), StringComparison.Ordinal);
    }

    [Fact]
    public void PublicSuffixDomainAttributeIsIgnored()
    {
        var jar = new CookieJar();
        var url = new Uri("http://www.example.com/");
        jar.SetCookie("bad=1; Domain=com; Path=/", url);
        // "com" is a public suffix; the cookie must not be scoped to it.
        var other = new Uri("http://other.com/");
        Assert.DoesNotContain("bad=1", jar.GetCookieHeader(other), StringComparison.Ordinal);
    }

    [Fact]
    public void MultiLabelAndPrivatePublicSuffixesAreRejected()
    {
        (string Origin, string Suffix, string Sibling)[] cases =
        [
            ("https://a.example.co.uk/", "co.uk", "https://b.example.co.uk/"),
            ("https://alice.github.io/", "github.io", "https://bob.github.io/"),
            // SECURITY.md L6: missing from the old curated list.
            ("https://app.onrender.com/", "onrender.com", "https://evil.onrender.com/"),
            ("https://shop.myshopify.com/", "myshopify.com", "https://evil.myshopify.com/"),
            ("https://b.s3.eu-west-1.amazonaws.com/", "s3.eu-west-1.amazonaws.com", "https://c.s3.eu-west-1.amazonaws.com/"),
        ];
        foreach (var (origin, suffix, sibling) in cases)
        {
            var jar = new CookieJar();
            jar.SetCookie($"sid=secret; Domain={suffix}; Path=/; Secure", new Uri(origin));
            Assert.Equal(string.Empty, jar.GetCookieHeaderSameSite(new Uri(origin)));
            Assert.Equal(string.Empty, jar.GetCookieHeaderSameSite(new Uri(sibling)));
        }

        var suffixJar = new CookieJar();
        var publicSuffixHost = new Uri("https://github.io/");
        suffixJar.SetCookie("sid=secret; Domain=github.io; Path=/; Secure", publicSuffixHost);
        Assert.Contains("sid=secret", suffixJar.GetCookieHeaderSameSite(publicSuffixHost), StringComparison.Ordinal);
        Assert.Equal(string.Empty, suffixJar.GetCookieHeaderSameSite(new Uri("https://sub.github.io/")));

        Assert.False(CookieJar.IsSameSite(new Uri("https://a.ngrok-free.app/"), new Uri("https://b.ngrok-free.app/")));
        Assert.False(CookieJar.IsSameSite(new Uri("https://a.fly.dev/"), new Uri("https://b.fly.dev/")));
        Assert.True(CookieJar.IsSameSite(new Uri("https://x.a.fly.dev/"), new Uri("https://a.fly.dev/")));
    }

    [Fact]
    public void InsecureOriginCannotSetOrOverwriteSecureCookie()
    {
        var jar = new CookieJar();
        var https = new Uri("https://example.com/");
        var http = new Uri("http://example.com/");

        jar.SetCookie("sid=secure; Secure; Path=/", https);
        jar.SetCookie("sid=attacker; Path=/", http);
        Assert.Equal("sid=secure", jar.GetCookieHeaderSameSite(https));

        jar.SetCookie("new=attacker; Secure; Path=/", http);
        Assert.DoesNotContain("new=", jar.GetCookieHeaderSameSite(https), StringComparison.Ordinal);
    }

    [Fact]
    public void InsecureOriginCannotDeleteSecureCookie()
    {
        // The overlay check runs before the Max-Age=0 delete, as it does in Rust.
        var jar = new CookieJar();
        jar.SetCookie("sid=secure; Secure; Path=/", new Uri("https://example.com/"));
        jar.SetCookieFromJs("sid=; Max-Age=0; Path=/", new Uri("http://example.com/"));
        Assert.Equal("sid=secure", jar.GetCookieHeaderSameSite(new Uri("https://example.com/")));
    }

    [Fact]
    public void HostOnlyScopeSurvivesSaveAndLoad()
    {
        var directory = Directory.CreateTempSubdirectory("obscura-cookies");
        try
        {
            var path = Path.Combine(directory.FullName, "cookies.json");
            var jar = new CookieJar();
            var host = new Uri("https://www.example.com/");
            jar.SetCookie("sid=host-only; Secure; Path=/", host);
            jar.SaveToFile(path);

            var loaded = new CookieJar();
            loaded.LoadFromFile(path);
            Assert.Contains("sid=host-only", loaded.GetCookieHeaderSameSite(host), StringComparison.Ordinal);
            Assert.Equal(string.Empty, loaded.GetCookieHeaderSameSite(new Uri("https://sub.www.example.com/")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CookiesJsonWritesHostOnlyAfterTheCookieInfoFields()
    {
        // serde writes Rust's PersistedCookie as the flattened CookieInfo, then hostOnly.
        var directory = Directory.CreateTempSubdirectory("obscura-cookies");
        try
        {
            var path = Path.Combine(directory.FullName, "cookies.json");
            var jar = new CookieJar();
            jar.SetCookie("sid=1; Path=/", new Uri("https://example.com/"));
            jar.SaveToFile(path);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var names = document.RootElement[0].EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(
                ["name", "value", "domain", "path", "secure", "httpOnly", "sameSite", "expires", "hostOnly"],
                names);
            Assert.True(document.RootElement[0].GetProperty("hostOnly").GetBoolean());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CookiesJsonWithoutHostOnlyLoadsDomainScoped()
    {
        var directory = Directory.CreateTempSubdirectory("obscura-cookies");
        try
        {
            var path = Path.Combine(directory.FullName, "cookies.json");
            File.WriteAllText(
                path,
                """[{"name":"sid","value":"1","domain":"example.com","path":"/","secure":false,"httpOnly":false,"sameSite":"Lax","expires":null}]""");
            var jar = new CookieJar();
            Assert.Equal(1, jar.LoadFromFile(path));
            Assert.Contains("sid=1", jar.GetCookieHeaderSameSite(new Uri("https://api.example.com/")), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SameSiteIsEnforcedForSubresources()
    {
        var jar = new CookieJar();
        var target = new Uri("https://api.example.com/data");
        jar.SetCookie("strict=1; Domain=example.com; SameSite=Strict; Secure", target);
        jar.SetCookie("lax=1; Domain=example.com; SameSite=Lax; Secure", target);
        jar.SetCookie("none=1; Domain=example.com; SameSite=None; Secure", target);

        var sameSiteSource = new Uri("https://www.example.com/page");
        Assert.True(CookieJar.IsSameSite(sameSiteSource, target));
        var sameSiteHeader = jar.GetCookieHeaderInContext(target, SameSiteContext.SameSite);
        Assert.Contains("strict=1", sameSiteHeader, StringComparison.Ordinal);
        Assert.Contains("lax=1", sameSiteHeader, StringComparison.Ordinal);
        Assert.Contains("none=1", sameSiteHeader, StringComparison.Ordinal);

        var crossSiteSource = new Uri("https://attacker.test/page");
        Assert.False(CookieJar.IsSameSite(crossSiteSource, target));
        Assert.Equal("none=1", jar.GetCookieHeaderInContext(target, SameSiteContext.CrossSite));

        var topLevel = jar.GetCookieHeaderInContext(target, SameSiteContext.CrossSiteTopLevelSafe);
        Assert.DoesNotContain("strict=1", topLevel, StringComparison.Ordinal);
        Assert.Contains("lax=1", topLevel, StringComparison.Ordinal);
        Assert.Contains("none=1", topLevel, StringComparison.Ordinal);
    }

    [Fact]
    public void SameSiteNoneRequiresSecure()
    {
        var jar = new CookieJar();
        var target = new Uri("https://example.com/");
        jar.SetCookie("insecure=1; SameSite=None", target);
        jar.SetCookie("secure=1; SameSite=None; Secure", target);
        Assert.Equal("secure=1", jar.GetCookieHeaderInContext(target, SameSiteContext.CrossSite));
    }

    [Fact]
    public void SameSiteUsesRegistrableDomainAndScheme()
    {
        Assert.True(CookieJar.IsSameSite(new Uri("https://a.example.co.uk/"), new Uri("https://b.example.co.uk/")));
        Assert.False(CookieJar.IsSameSite(new Uri("https://a.example.co.uk/"), new Uri("https://b.other.co.uk/")));
        Assert.False(CookieJar.IsSameSite(new Uri("https://alice.github.io/"), new Uri("https://bob.github.io/")));
        Assert.False(CookieJar.IsSameSite(new Uri("http://example.com/"), new Uri("https://example.com/")));
        Assert.True(CookieJar.IsSameSite(new Uri("https://example.com:8443/"), new Uri("https://www.example.com/")));
    }

    [Fact]
    public void IpHostsAreTheirOwnSite()
    {
        // Deviation from Rust, whose psl lookup reads the last two octets as a site.
        Assert.False(CookieJar.IsSameSite(new Uri("http://10.0.0.1/"), new Uri("http://192.168.0.1/")));
        Assert.True(CookieJar.IsSameSite(new Uri("http://10.0.0.1/"), new Uri("http://10.0.0.1:8080/")));
    }

    [Fact]
    public void ContextForInitiatorTreatsAnOpaqueOriginAsCrossSite()
    {
        var target = new Uri("https://example.com/api");
        Assert.Equal(SameSiteContext.SameSite, CookieJar.ContextForInitiator("https://www.example.com", target));
        Assert.Equal(SameSiteContext.CrossSite, CookieJar.ContextForInitiator("https://attacker.test", target));
        Assert.Equal(SameSiteContext.CrossSite, CookieJar.ContextForInitiator("null", target));
        Assert.Equal(SameSiteContext.CrossSite, CookieJar.ContextForInitiator(string.Empty, target));
    }

    [Fact]
    public void HttpClientDerivesTheContextFromTheInitiatorAndMode()
    {
        // Port of same_site_context in client.rs.
        var target = new Uri("https://api.example.com/data");
        var attacker = new Uri("https://attacker.test/page");

        Assert.Equal(
            SameSiteContext.SameSite,
            PocketCalculatorHttpClient.SameSiteContextFor(ResourceRequest.Navigation(), target, methodIsSafe: false));
        Assert.Equal(
            SameSiteContext.SameSite,
            PocketCalculatorHttpClient.SameSiteContextFor(
                ResourceRequest.Subresource(ResourceType.Script, new Uri("https://www.example.com/")), target, true));
        Assert.Equal(
            SameSiteContext.CrossSite,
            PocketCalculatorHttpClient.SameSiteContextFor(ResourceRequest.Subresource(ResourceType.Image, attacker), target, true));

        var navigation = ResourceRequest.Subresource(ResourceType.Document, attacker);
        Assert.Equal(
            SameSiteContext.CrossSiteTopLevelSafe,
            PocketCalculatorHttpClient.SameSiteContextFor(navigation, target, methodIsSafe: true));
        Assert.Equal(
            SameSiteContext.CrossSite,
            PocketCalculatorHttpClient.SameSiteContextFor(navigation, target, methodIsSafe: false));
    }

    [Fact]
    public void PageNavigationsFollowChromiumSameSiteRules()
    {
        // Deviation from same_site_context in client.rs: measured on Chromium 140.
        var target = new Uri("https://api.example.com/data");
        var sameSite = new Uri("https://www.example.com/page");
        var attacker = new Uri("https://attacker.test/page");

        Assert.Equal(
            SameSiteContext.SameSite,
            PocketCalculatorHttpClient.SameSiteContextFor(
                ResourceRequest.PageNavigation(sameSite, userActivated: false), target, methodIsSafe: false));
        Assert.Equal(
            SameSiteContext.CrossSiteTopLevelSafe,
            PocketCalculatorHttpClient.SameSiteContextFor(
                ResourceRequest.PageNavigation(attacker, userActivated: true), target, methodIsSafe: true));

        // An iframe is never a top-level navigation, so a cross-site one carries only None.
        Assert.Equal(
            SameSiteContext.CrossSite,
            PocketCalculatorHttpClient.SameSiteContextFor(ResourceRequest.FrameNavigation(attacker), target, true));
        Assert.Equal(
            SameSiteContext.SameSite,
            PocketCalculatorHttpClient.SameSiteContextFor(ResourceRequest.FrameNavigation(sameSite), target, true));

        // A redirect chain through another site loses Strict, even for the address bar.
        Assert.Equal(
            SameSiteContext.CrossSiteTopLevelSafe,
            PocketCalculatorHttpClient.SameSiteContextFor(ResourceRequest.Navigation(), target, true, [attacker]));
        Assert.Equal(
            SameSiteContext.SameSite,
            PocketCalculatorHttpClient.SameSiteContextFor(ResourceRequest.Navigation(), target, true, [sameSite]));
        Assert.Equal(
            SameSiteContext.CrossSiteTopLevelSafe,
            PocketCalculatorHttpClient.SameSiteContextFor(
                ResourceRequest.PageNavigation(sameSite, userActivated: false), target, true, [attacker]));
    }

    [Fact]
    public void EmptyDomainAttributeIsIgnored()
    {
        // Deviation from Rust, which rejects the cookie: Chromium and RFC 6265bis 5.6.3
        // ignore an empty Domain, leaving the cookie host-only.
        var jar = new CookieJar();
        var www = new Uri("https://www.example.com/");
        jar.SetCookie("sid=1; Domain=; Path=/", www);
        Assert.Equal("sid=1", jar.GetCookieHeaderSameSite(www));
        Assert.Equal(string.Empty, jar.GetCookieHeaderSameSite(new Uri("https://sub.www.example.com/")));
    }

    [Fact]
    public void IpHostAcceptsOnlyItsOwnAddressAsDomain()
    {
        // Deviation from Rust (which accepts Domain=0.0.1 on 10.0.0.1): Chromium takes a
        // Domain on an IP host only when it is that address, as a host cookie.
        var jar = new CookieJar();
        var ip = new Uri("http://10.0.0.1/");
        jar.SetCookie("wide=1; Domain=0.0.1; Path=/", ip);
        jar.SetCookie("exact=1; Domain=10.0.0.1; Path=/", ip);
        Assert.Equal("exact=1", jar.GetCookieHeaderSameSite(ip));
    }

    [Fact]
    public void DomainEqualToTheOriginIsDomainScoped()
    {
        // Upstream 04418a5: Domain=example.com set by example.com reaches subdomains, as
        // in Chromium. It used to be stored host-only.
        var jar = new CookieJar();
        jar.SetCookie("sid=1; Domain=example.com; Path=/", new Uri("https://example.com/"));
        Assert.Equal("sid=1", jar.GetCookieHeaderSameSite(new Uri("https://api.example.com/")));
    }

    [Fact]
    public void CdpImportSkipsPublicSuffixAndInsecureSameSiteNone()
    {
        var jar = new CookieJar();
        var suffix = Cdp("a", "1", "co.uk", "/");
        var none = Cdp("b", "1", "example.com", "/");
        none.SameSite = "None";
        var localhost = Cdp("c", "1", "localhost", "/");
        jar.SetCookiesFromCdp([suffix, none, localhost]);
        var all = jar.GetAllCookies();
        Assert.Single(all);
        Assert.Equal("c", all[0].Name);
    }

    [Fact]
    public void CopyFromKeepsHostOnlyScopeAndIsIndependent()
    {
        var source = new CookieJar();
        source.SetCookie("host=1; Path=/", new Uri("https://example.com/"));
        var copy = new CookieJar();
        copy.SetCookie("stale=1; Path=/", new Uri("https://example.com/"));
        copy.CopyFrom(source);

        Assert.Equal("host=1", copy.GetCookieHeaderSameSite(new Uri("https://example.com/")));
        Assert.Equal(string.Empty, copy.GetCookieHeaderSameSite(new Uri("https://sub.example.com/")));
        copy.Clear();
        Assert.Single(source.GetAllCookies());
    }

    [Fact]
    public void HostOnlyCookieNotSentToSubdomain()
    {
        var jar = new CookieJar();
        var www = new Uri("http://www.example.com/");
        jar.SetCookie("hostonly=1; Path=/", www); // no Domain attribute -> host-only

        Assert.Contains("hostonly=1", jar.GetCookieHeader(www), StringComparison.Ordinal);
        var sub = new Uri("http://sub.www.example.com/");
        Assert.DoesNotContain("hostonly=1", jar.GetCookieHeader(sub), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidSubdomainCanSetParentDomainCookie()
    {
        // A subdomain setting Domain=<parent> (a legitimate parent) still works.
        var jar = new CookieJar();
        var www = new Uri("http://www.example.com/");
        jar.SetCookie("token=1; Domain=example.com; Path=/", www);

        var apex = new Uri("http://example.com/");
        Assert.Contains("token=1", jar.GetCookieHeader(apex), StringComparison.Ordinal);
        var api = new Uri("http://api.example.com/");
        Assert.Contains("token=1", jar.GetCookieHeader(api), StringComparison.Ordinal);
    }

    [Fact]
    public void CookiePathRequiresSlashBoundary()
    {
        // RFC 6265 5.1.4: a Path=/admin cookie must NOT be sent to a sibling path like
        // /administrator that merely shares the string prefix. It is sent to /admin,
        // /admin/, and /admin/x.
        var jar = new CookieJar();
        var admin = new Uri("https://example.com/admin");
        jar.SetCookie("sess=1; Path=/admin", admin);

        var sibling = new Uri("https://example.com/administrator");
        Assert.DoesNotContain("sess=1", jar.GetCookieHeader(sibling), StringComparison.Ordinal);

        Assert.Contains("sess=1", jar.GetCookieHeader(admin), StringComparison.Ordinal);
        var exactSlash = new Uri("https://example.com/admin/");
        Assert.Contains("sess=1", jar.GetCookieHeader(exactSlash), StringComparison.Ordinal);
        var sub = new Uri("https://example.com/admin/panel");
        Assert.Contains("sess=1", jar.GetCookieHeader(sub), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCookiePathIsRequestDirectory()
    {
        // RFC 6265 5.1.4 default-path: up to (not including) the right-most '/'.
        Assert.Equal("/app", CookieJar.DefaultCookiePath("/app/login"));
        Assert.Equal("/app", CookieJar.DefaultCookiePath("/app/"));
        Assert.Equal("/a/b", CookieJar.DefaultCookiePath("/a/b/c"));
        // No more than one '/', empty, or non-absolute -> "/".
        Assert.Equal("/", CookieJar.DefaultCookiePath("/foo"));
        Assert.Equal("/", CookieJar.DefaultCookiePath("/"));
        Assert.Equal("/", CookieJar.DefaultCookiePath(string.Empty));
        Assert.Equal("/", CookieJar.DefaultCookiePath("relative"));
    }

    [Fact]
    public void CookieWithoutPathDefaultsToDirectoryNotFullPath()
    {
        // A Set-Cookie with no Path attribute on /app/login must scope to /app
        // (RFC 6265 5.1.4), so the session survives navigation to /app/dashboard.
        // Before this fix it was scoped to the full path /app/login and vanished on the
        // next page, appearing as a silent logout.
        var jar = new CookieJar();
        var login = new Uri("https://example.com/app/login");
        jar.SetCookie("sid=abc", login);

        var dashboard = new Uri("https://example.com/app/dashboard");
        Assert.Contains("sid=abc", jar.GetCookieHeader(dashboard), StringComparison.Ordinal);
        // Still sent at the directory root and the original path.
        var appRoot = new Uri("https://example.com/app/");
        Assert.Contains("sid=abc", jar.GetCookieHeader(appRoot), StringComparison.Ordinal);
        Assert.Contains("sid=abc", jar.GetCookieHeader(login), StringComparison.Ordinal);

        // But not to an unrelated top-level path outside the directory.
        var other = new Uri("https://example.com/other");
        Assert.DoesNotContain("sid=abc", jar.GetCookieHeader(other), StringComparison.Ordinal);
    }

    [Fact]
    public void JsCookieWithoutPathAlsoDefaultsToDirectory()
    {
        // document.cookie set on /shop/cart with no path must reach /shop/checkout.
        var jar = new CookieJar();
        var cart = new Uri("https://example.com/shop/cart");
        jar.SetCookieFromJs("cart=xyz", cart);
        var checkout = new Uri("https://example.com/shop/checkout");
        Assert.Contains("cart=xyz", jar.GetJsVisibleCookies(checkout), StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
