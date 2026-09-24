namespace PocketCalculator.Net.Tests;

/// <summary>
/// CHIPS and the site for cookies, as measured on Chromium 141 (default settings, which do
/// not block third-party cookies) with https://top-a.com, https://top-c.com and
/// https://third-b.com on one local server. Rust's jar has no partitions and judges a
/// request's SameSite context by its initiator alone.
/// </summary>
public class PartitionedCookieTests
{
    private static readonly Uri TopA = new("https://top-a.com/page");
    private static readonly Uri TopC = new("https://top-c.com/page");
    private static readonly Uri Third = new("https://third-b.com/frame");

    /// <summary>A document of <paramref name="frame"/> embedded directly in <paramref name="top"/>.</summary>
    private static CookieAccess Framed(Uri top, Uri frame) =>
        new(
            CookieJar.IsSameSite(top, frame) ? SameSiteContext.SameSite : SameSiteContext.CrossSite,
            CookiePartitionKey.For(top, crossSiteAncestor: false, frame));

    [Fact]
    public void PartitionedCookieIsReadOnlyUnderTheTopLevelSiteThatSetIt()
    {
        var jar = new CookieJar();
        jar.SetCookie("part=1; Secure; SameSite=None; Partitioned; Path=/", Third, Framed(TopA, Third));
        jar.SetCookie("unpart=1; Secure; SameSite=None; Path=/", Third, Framed(TopA, Third));

        Assert.Equal("part=1; unpart=1", Sorted(jar.GetJsVisibleCookies(Third, Framed(TopA, Third))));
        // Another top-level site embedding the same third party, and the third party
        // visited top-level, see the unpartitioned cookie only.
        Assert.Equal("unpart=1", jar.GetJsVisibleCookies(Third, Framed(TopC, Third)));
        Assert.Equal("unpart=1", jar.GetCookieHeader(Third));
        Assert.Equal("unpart=1", jar.GetJsVisibleCookies(Third));
    }

    [Fact]
    public void PartitionedCookieWithoutSecureIsRejected()
    {
        var jar = new CookieJar();
        jar.SetCookie("hdr=1; SameSite=None; Partitioned", Third, Framed(TopA, Third));
        jar.SetCookieFromJs("js=1; Partitioned", TopA);
        Assert.Empty(jar.GetAllCookies());
    }

    [Fact]
    public void TopLevelPartitionedCookieIsKeyedWithoutTheCrossSiteBit()
    {
        var jar = new CookieJar();
        jar.SetCookie("first=1; Secure; Partitioned; Path=/", Third);

        var cookie = Assert.Single(jar.GetAllCookies());
        Assert.Equal(new CookiePartitionKey("https://third-b.com", false), cookie.PartitionKey);
        Assert.Equal("first=1", jar.GetCookieHeader(Third));
        // Not sent into a frame of the same site under another top level.
        Assert.Equal(string.Empty, jar.GetCookieHeader(Third, Framed(TopA, Third)));
    }

    [Fact]
    public void CrossSiteAncestorBitSeparatesPartitions()
    {
        // top-a.com embeds third-b.com, which embeds top-a.com again.
        var nested = new Uri("https://top-a.com/frame");
        var viaThird = new CookieAccess(
            SameSiteContext.CrossSite, CookiePartitionKey.For(TopA, crossSiteAncestor: true, nested));
        Assert.Equal(new CookiePartitionKey("https://top-a.com", true), viaThird.Partition);

        var jar = new CookieJar();
        jar.SetCookie("aba=1; Secure; SameSite=None; Partitioned; Path=/", nested, viaThird);
        Assert.Equal(string.Empty, jar.GetJsVisibleCookies(nested));
        Assert.Equal("aba=1", jar.GetJsVisibleCookies(nested, viaThird));
    }

    [Fact]
    public void PartitionKeyIsTheSchemefulSite()
    {
        Assert.Equal(
            new CookiePartitionKey("https://example.com", true),
            CookiePartitionKey.For(new Uri("https://www.example.com:8443/x"), false, new Uri("https://b.example.org/")));
        Assert.Equal("http://127.0.0.1", CookiePartitionKey.SiteOf(new Uri("http://127.0.0.1:9/")));
        Assert.Null(CookiePartitionKey.SiteOf(new Uri("about:blank")));
        Assert.True(CookiePartitionKey.TryNormalize("https://a.example.com", true, out var key));
        Assert.Equal(new CookiePartitionKey("https://example.com", true), key);
        Assert.False(CookiePartitionKey.TryNormalize("not a url", true, out _));
    }

    [Fact]
    public void CrossSiteContextSetsSameSiteNoneCookiesOnly()
    {
        var jar = new CookieJar();
        var access = Framed(TopA, Third);
        jar.SetCookie("lax=1; Secure; SameSite=Lax; Path=/", Third, access);
        jar.SetCookie("strict=1; Secure; SameSite=Strict; Path=/", Third, access);
        jar.SetCookie("unspecified=1; Secure; Path=/", Third, access);
        jar.SetCookieFromJs("jslax=1; Secure; SameSite=Lax; Path=/", Third, access);
        jar.SetCookie("none=1; Secure; SameSite=None; Path=/", Third, access);

        Assert.Equal("none=1", jar.GetCookieHeader(Third));
    }

    [Fact]
    public void CrossSiteFrameSeesNoLaxCookiesOfItsOwnSite()
    {
        var jar = new CookieJar();
        jar.SetCookie("lax=1; Secure; SameSite=Lax; Path=/", Third);
        jar.SetCookie("none=1; Secure; SameSite=None; Path=/", Third);

        Assert.Equal("none=1", jar.GetJsVisibleCookies(Third, Framed(TopA, Third)));
    }

    [Fact]
    public void SameNameCookiesInDifferentPartitionsCoexist()
    {
        var jar = new CookieJar();
        jar.SetCookie("id=top; Secure; SameSite=None; Path=/", Third);
        jar.SetCookie("id=a; Secure; SameSite=None; Partitioned; Path=/", Third, Framed(TopA, Third));
        jar.SetCookie("id=c; Secure; SameSite=None; Partitioned; Path=/", Third, Framed(TopC, Third));

        Assert.Equal(3, jar.GetAllCookies().Count);
        Assert.Equal("id=top; id=a", jar.GetJsVisibleCookies(Third, Framed(TopA, Third)));

        // CDP deleteCookies without a key deletes the unpartitioned cookie only; with one,
        // only that partition's.
        jar.DeleteCookiesFiltered("id", "third-b.com", null, partition: null);
        Assert.Equal("id=a", jar.GetJsVisibleCookies(Third, Framed(TopA, Third)));
        jar.DeleteCookiesFiltered("id", "third-b.com", null, new CookiePartitionKey("https://top-a.com", true));
        Assert.Equal(string.Empty, jar.GetJsVisibleCookies(Third, Framed(TopA, Third)));
        Assert.Equal("id=c", jar.GetJsVisibleCookies(Third, Framed(TopC, Third)));
    }

    [Fact]
    public void ImportNormalizesTheKeyAndForcesSecure()
    {
        var jar = new CookieJar();
        jar.SetCookiesFromCdp(
        [
            new CookieInfo
            {
                Name = "cdp",
                Value = "1",
                Domain = "third-b.com",
                Path = "/",
                SameSite = "None",
                PartitionKey = new CookiePartitionKey("https://www.top-a.com:8443", true),
            },
            new CookieInfo
            {
                Name = "bad",
                Value = "1",
                Domain = "third-b.com",
                Path = "/",
                Secure = true,
                PartitionKey = new CookiePartitionKey("not a url", true),
            },
        ]);

        var cookie = Assert.Single(jar.GetAllCookies());
        Assert.True(cookie.Secure);
        Assert.Equal(new CookiePartitionKey("https://top-a.com", true), cookie.PartitionKey);
        Assert.Equal("cdp=1", jar.GetCookieHeader(Third, Framed(TopA, Third)));
        Assert.Equal(string.Empty, jar.GetCookieHeader(Third));
    }

    [Fact]
    public void PersistedJarKeepsPartitionsAndOldFilesStillLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc-chips-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = Path.Combine(dir, "cookies.json");
            var jar = new CookieJar();
            jar.SetCookie("part=1; Secure; SameSite=None; Partitioned; Path=/", Third, Framed(TopA, Third));
            jar.SetCookie("plain=1; Secure; SameSite=None; Path=/", Third);
            jar.SaveToFile(file);

            var text = File.ReadAllText(file);
            Assert.Contains("\"partitionKey\"", text, StringComparison.Ordinal);
            Assert.Contains("\"topLevelSite\": \"https://top-a.com\"", text, StringComparison.Ordinal);
            // Only the partitioned cookie carries the field.
            Assert.Equal(1, text.Split("partitionKey").Length - 1);

            var loaded = new CookieJar();
            Assert.Equal(2, loaded.LoadFromFile(file));
            Assert.Equal("plain=1", loaded.GetCookieHeader(Third));
            Assert.Equal("part=1; plain=1", Sorted(loaded.GetCookieHeader(Third, Framed(TopA, Third))));

            // A file written before partitions existed.
            File.WriteAllText(
                file,
                """[{"name":"old","value":"1","domain":"third-b.com","path":"/","secure":true,"httpOnly":false,"sameSite":"None","expires":null}]""");
            var legacy = new CookieJar();
            Assert.Equal(1, legacy.LoadFromFile(file));
            Assert.Equal("old=1", legacy.GetCookieHeader(Third));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void RequestContextFollowsTheTopLevelDocument()
    {
        // An image on top-a.com from third-b.com: cross-site, partitioned under top-a.com
        // with the cross-site bit.
        var image = ResourceRequest.Subresource(ResourceType.Image, TopA);
        var imageAccess = PocketCalculatorHttpClient.CookieAccessFor(image, Third, true, []);
        Assert.Equal(SameSiteContext.CrossSite, imageAccess.SameSite);
        Assert.Equal(new CookiePartitionKey("https://top-a.com", true), imageAccess.Partition);

        // A same-origin script of a third-b.com frame on top-a.com: its initiator is
        // same-site with the target, but the frame has no site for cookies.
        var script = ResourceRequest.Subresource(ResourceType.Script, Third);
        script.TopLevel = TopA;
        script.CrossSiteAncestor = true;
        var scriptAccess = PocketCalculatorHttpClient.CookieAccessFor(script, new Uri("https://third-b.com/s.js"), true, []);
        Assert.Equal(SameSiteContext.CrossSite, scriptAccess.SameSite);
        Assert.Equal(new CookiePartitionKey("https://top-a.com", true), scriptAccess.Partition);

        // A top-level navigation is keyed by its own target, and may set Lax cookies even
        // when another site started it.
        var navigation = ResourceRequest.PageNavigation(TopA, userActivated: false);
        var navigationAccess = PocketCalculatorHttpClient.CookieSetAccessFor(navigation, Third, []);
        Assert.Equal(SameSiteContext.SameSite, navigationAccess.SameSite);
        Assert.Equal(new CookiePartitionKey("https://third-b.com", false), navigationAccess.Partition);

        // A same-site frame's own requests stay same-site.
        var sameSiteFrame = new Uri("https://www.top-a.com/frame");
        var framed = ResourceRequest.Subresource(ResourceType.Fetch, sameSiteFrame);
        framed.TopLevel = TopA;
        var framedAccess = PocketCalculatorHttpClient.CookieAccessFor(framed, sameSiteFrame, true, []);
        Assert.Equal(SameSiteContext.SameSite, framedAccess.SameSite);
        Assert.Equal(new CookiePartitionKey("https://top-a.com", false), framedAccess.Partition);
    }

    private static string Sorted(string header) =>
        string.Join("; ", header.Split("; ").Order(StringComparer.Ordinal));
}
