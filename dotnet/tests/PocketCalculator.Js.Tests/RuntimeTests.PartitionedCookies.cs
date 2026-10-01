using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// A frame's cookies, measured on Chromium 141: <c>document.cookie</c> in a cross-site frame
/// sees SameSite=None cookies only, a <c>Partitioned</c> cookie it sets is kept under the
/// page's site (with the cross-site bit when an ancestor is cross-site), and neither
/// another top-level site embedding the same frame nor the frame's site visited top-level
/// can read it. Rust frames read and write cookies as the page does, unpartitioned.
/// </summary>
public sealed partial class RuntimeTests
{
    private static RuntimeFixture CookiePage(string url, CookieJar jar)
    {
        var fixture = RuntimeFixture.Blank();
        fixture.Runtime.SetDom(HtmlParsing.ParseHtml("<html><head></head><body></body></html>"));
        fixture.Runtime.SetUrl(url);
        fixture.Runtime.SetCookieJar(jar);
        fixture.Runtime.RunPageInit();
        return fixture;
    }

    private static string DocumentCookie(PocketCalculatorJsRuntime runtime) =>
        runtime.Evaluate("document.cookie")!.GetValue<string>();

    private static string DocumentCookie(FrameRealm frame) =>
        frame.Evaluate("document.cookie")!.GetValue<string>();

    [Fact]
    public void PartitionedCookieFromACrossSiteFrameStaysInItsPartition()
    {
        var jar = new CookieJar();
        jar.SetCookie("lax=1; Secure; SameSite=Lax; Path=/", new Uri("https://third-b.com/"));

        using (var topA = CookiePage("https://top-a.com/page", jar))
        using (var frame = FrameRealm.Create(topA.Runtime, 1, 0, "https://third-b.com/frame", "<html><body></body></html>"))
        {
            Assert.NotNull(frame);
            Assert.True(frame.State.CrossSiteAncestor);
            frame.Evaluate(
                "document.cookie = 'part=1; Secure; SameSite=None; Partitioned; Path=/';"
                + "document.cookie = 'unpart=1; Secure; SameSite=None; Path=/';"
                + "document.cookie = 'jslax=1; Secure; SameSite=Lax; Path=/';"
                + "document.cookie = 'insecure=1; SameSite=None; Partitioned; Path=/'; 0");
            // The frame's own site's Lax cookie is not visible, and its Lax write is refused.
            Assert.Equal("part=1; unpart=1", DocumentCookie(frame));
        }

        using (var topC = CookiePage("https://top-c.com/page", jar))
        using (var frame = FrameRealm.Create(topC.Runtime, 1, 0, "https://third-b.com/frame", "<html><body></body></html>"))
        {
            Assert.NotNull(frame);
            Assert.Equal("unpart=1", DocumentCookie(frame));
        }

        using (var third = CookiePage("https://third-b.com/", jar))
        {
            Assert.Equal("lax=1; unpart=1", DocumentCookie(third.Runtime));
        }

        var partitioned = Assert.Single(jar.GetAllCookies(), cookie => cookie.Name == "part");
        Assert.Equal(new CookiePartitionKey("https://top-a.com", true), partitioned.PartitionKey);
    }

    [Fact]
    public void SameSiteFrameAndSrcdocKeepThePagesCookieContext()
    {
        var jar = new CookieJar();
        using var page = CookiePage("https://top-a.com/page", jar);
        using var sameSite = FrameRealm.Create(page.Runtime, 1, 0, "https://www.top-a.com/frame", "<html><body></body></html>");
        using var viaThird = FrameRealm.Create(page.Runtime, 2, 0, "https://third-b.com/frame", "<html><body></body></html>");
        using var backHome = FrameRealm.Create(page.Runtime, 3, 2, "https://top-a.com/frame", "<html><body></body></html>");
        using var srcdoc = FrameRealm.Create(page.Runtime, 4, 1, "about:srcdoc", "<html><body></body></html>");
        Assert.NotNull(sameSite);
        Assert.NotNull(viaThird);
        Assert.NotNull(backHome);
        Assert.NotNull(srcdoc);

        Assert.False(sameSite.State.CrossSiteAncestor);
        Assert.False(srcdoc.State.CrossSiteAncestor);
        // top-a.com inside third-b.com inside top-a.com has a cross-site ancestor.
        Assert.True(backHome.State.CrossSiteAncestor);

        sameSite.Evaluate("document.cookie = 'lax=1; Secure; SameSite=Lax; Path=/'; 0");
        Assert.Equal("lax=1", DocumentCookie(sameSite));
        backHome.Evaluate("document.cookie = 'aba=1; Secure; SameSite=None; Partitioned; Path=/'; 0");
        Assert.DoesNotContain("aba", DocumentCookie(page.Runtime), StringComparison.Ordinal);
        Assert.Equal(
            new CookiePartitionKey("https://top-a.com", true),
            Assert.Single(jar.GetAllCookies(), cookie => cookie.Name == "aba").PartitionKey);
    }

    [Fact]
    public async Task CrossSiteFrameFetchCarriesAndSetsSameSiteNoneCookiesOnly()
    {
        using var server = new RawHttpServer(_ =>
            "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nSet-Cookie: set=1; SameSite=Lax; Path=/\r\n"
            + "Content-Length: 2\r\nConnection: close\r\n\r\nok");
        var jar = new CookieJar();
        var frameUrl = $"{server.Origin}/frame";
        jar.SetCookie("lax=1; SameSite=Lax; Path=/", new Uri(frameUrl));
        var port = new Uri(server.Origin).Port;

        using var page = CookiePage($"http://localhost:{port}/page", jar);
        page.Runtime.SetHttpClient(new PocketCalculatorHttpClient(jar, null, allowPrivateNetwork: true));
        using var frame = FrameRealm.Create(page.Runtime, 1, 0, frameUrl, "<html><body></body></html>");
        Assert.NotNull(frame);

        await FetchOps.FetchUrlAsync(
            page.Runtime.State, frame.State, $"{server.Origin}/api", "GET", "{}", [], "cors", "include", internalLoad: false);

        var request = Assert.Single(server.Requests, r => RawHttpServer.RequestPath(r) == "/api");
        Assert.DoesNotContain("\r\nCookie:", request, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("lax=1", jar.GetCookieHeader(new Uri(frameUrl)));
    }
}
