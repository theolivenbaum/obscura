using System.Text.Json;

namespace Obscura.Net.Tests;

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
        // The cookie is stored host-only on the attacker origin instead.
        Assert.Contains("sid=attacker", jar.GetCookieHeader(attacker), StringComparison.Ordinal);
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
