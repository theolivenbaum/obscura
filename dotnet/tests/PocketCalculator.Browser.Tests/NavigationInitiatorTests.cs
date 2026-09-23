using System.Globalization;
using PocketCalculator.Js.Url;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// The request a navigation sends depends on who started it. Expected values were
/// measured on Chromium 140 (Playwright's chromium-1194) against a logging server, with
/// Strict and Lax cookies on the target site.
/// </summary>
/// <remarks>
/// Cross-site needs two registrable domains on one loopback server: <c>localhost</c>
/// and <c>127.0.0.1</c> are different sites (an IP address is its own site). Two ports
/// on <c>127.0.0.1</c> are same-site but cross-origin. Upstream sends every one of these
/// navigations with no initiator, as though it were typed into the address bar.
/// </remarks>
public sealed class NavigationInitiatorTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, string> _pages = new(StringComparer.Ordinal);

        internal Fixture()
        {
            Server = TestHttpServer.Start(Handle);
            Other = TestHttpServer.Start(Handle);
            Port = new Uri(Server.Origin).Port.ToString(CultureInfo.InvariantCulture);
            Page = PageFixtures.NewPage("navigation-initiator");
            Uri target = new(Target(string.Empty));
            Page.Context.CookieJar.SetCookie("strict=1; SameSite=Strict; Path=/", target);
            Page.Context.CookieJar.SetCookie("lax=1; SameSite=Lax; Path=/", target);
        }

        internal TestHttpServer Server { get; }

        /// <summary>A second origin on 127.0.0.1, same-site with <see cref="Server"/>.</summary>
        internal TestHttpServer Other { get; }

        internal string Port { get; }

        internal Page Page { get; }

        /// <summary>A URL on the target site, 127.0.0.1, where the cookies live.</summary>
        internal string Target(string path) => $"http://127.0.0.1:{Port}{path}";

        /// <summary>The same server under a cross-site name.</summary>
        internal string CrossSite(string path) => $"http://localhost:{Port}{path}";

        internal string SameSite(string path) => Other.Origin + path;

        internal void Serve(string path, string html) => _pages[path] = html;

        private TestResponse Handle(TestRequest request)
        {
            string path = request.Path.Split('?')[0];
            if (path == "/redirect")
            {
                string to = Uri.UnescapeDataString(request.Path[(request.Path.IndexOf("to=", StringComparison.Ordinal) + 3)..]);
                return new TestResponse("text/plain", [], "302 Found", [("Location", to)]);
            }

            return _pages.TryGetValue(path, out string? html)
                ? TestResponse.Html(html)
                : TestResponse.Html("<p>logged</p>");
        }

        /// <summary>The first request either server saw for <paramref name="path"/>.</summary>
        internal TestRequest Logged(string path)
        {
            DateTime deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                foreach (TestRequest request in Server.Requests.Concat(Other.Requests))
                {
                    if (request.Path.Split('?')[0] == path)
                    {
                        return request;
                    }
                }

                Thread.Sleep(20);
            }

            throw new TimeoutException($"no request for {path}");
        }

        public void Dispose()
        {
            Page.Dispose();
            Server.Dispose();
            Other.Dispose();
        }
    }

    private static string? Header(TestRequest request, string name)
    {
        foreach (string line in request.Headers.Split("\r\n").Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && string.Equals(line[..colon], name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>The Cookie header's pairs in name order, independent of jar order.</summary>
    private static string? Cookies(TestRequest request) =>
        Header(request, "cookie") is { } cookie
            ? string.Join("; ", cookie.Split("; ").Order(StringComparer.Ordinal))
            : null;

    private static void AssertFetchMetadata(
        TestRequest request,
        string site,
        string dest,
        string? user,
        string? referer)
    {
        Assert.Equal(site, Header(request, "sec-fetch-site"));
        Assert.Equal("navigate", Header(request, "sec-fetch-mode"));
        Assert.Equal(dest, Header(request, "sec-fetch-dest"));
        Assert.Equal(user, Header(request, "sec-fetch-user"));
        Assert.Equal(referer, Header(request, "referer"));
    }

    [Fact]
    public async Task CrossSiteLinkClickDropsStrictKeepsLax()
    {
        using var fx = new Fixture();
        fx.Serve("/start", $"<a id=l href=\"{fx.Target("/log")}\">go</a><script>document.getElementById('l').click()</script>");

        await fx.Page.NavigateAsync(fx.CrossSite("/start"));

        TestRequest request = fx.Logged("/log");
        Assert.Equal("lax=1", Cookies(request));
        AssertFetchMetadata(request, "cross-site", "document", null, fx.CrossSite("/"));
        Assert.Null(Header(request, "origin"));
        Assert.Equal(fx.CrossSite("/"), fx.Page.Referrer);
    }

    [Fact]
    public async Task CrossSiteFormGetKeepsLax()
    {
        using var fx = new Fixture();
        fx.Serve("/start", $"<form id=f action=\"{fx.Target("/log")}\"><input name=q value=1></form><script>document.getElementById('f').submit()</script>");

        await fx.Page.NavigateAsync(fx.CrossSite("/start"));

        TestRequest request = fx.Logged("/log");
        Assert.Equal("GET", request.Method);
        Assert.Equal("lax=1", Cookies(request));
        AssertFetchMetadata(request, "cross-site", "document", null, fx.CrossSite("/"));
    }

    [Fact]
    public async Task CrossSiteFormPostDropsLaxAndStrict()
    {
        using var fx = new Fixture();
        fx.Serve("/start", $"<form id=f method=post action=\"{fx.Target("/log")}\"><input name=q value=1></form><script>document.getElementById('f').submit()</script>");

        await fx.Page.NavigateAsync(fx.CrossSite("/start"));

        TestRequest request = fx.Logged("/log");
        Assert.Equal("POST", request.Method);
        Assert.Null(Header(request, "cookie"));
        AssertFetchMetadata(request, "cross-site", "document", null, fx.CrossSite("/"));
        Assert.Equal(fx.CrossSite(string.Empty), Header(request, "origin"));
    }

    [Fact]
    public async Task SameOriginNavigationKeepsStrictAndSendsFullReferrer()
    {
        using var fx = new Fixture();
        fx.Serve("/start", "<script>location.href = '/log'</script>");

        await fx.Page.NavigateAsync(fx.Target("/start?q=1#frag"));

        TestRequest request = fx.Logged("/log");
        Assert.Equal("lax=1; strict=1", Cookies(request));
        AssertFetchMetadata(request, "same-origin", "document", null, fx.Target("/start?q=1"));
    }

    [Fact]
    public async Task SameSiteCrossOriginNavigationKeepsStrict()
    {
        using var fx = new Fixture();
        fx.Serve("/start", $"<script>location.href = '{fx.Target("/log")}'</script>");

        await fx.Page.NavigateAsync(fx.SameSite("/start"));

        TestRequest request = fx.Logged("/log");
        Assert.Equal("lax=1; strict=1", Cookies(request));
        AssertFetchMetadata(request, "same-site", "document", null, fx.SameSite("/"));
    }

    [Fact]
    public async Task SameSitePagePostKeepsStrictAndSendsOrigin()
    {
        using var fx = new Fixture();
        fx.Serve("/start", $"<form id=f method=post action=\"{fx.Target("/log")}\"><input name=q value=1></form><script>document.getElementById('f').submit()</script>");

        await fx.Page.NavigateAsync(fx.SameSite("/start"));

        TestRequest request = fx.Logged("/log");
        Assert.Equal("lax=1; strict=1", Cookies(request));
        AssertFetchMetadata(request, "same-site", "document", null, fx.SameSite("/"));
        Assert.Equal(fx.SameSite(string.Empty), Header(request, "origin"));
    }

    [Fact]
    public async Task BrowserInitiatedNavigationKeepsStrict()
    {
        using var fx = new Fixture();

        await fx.Page.NavigateAsync(fx.Target("/log"));

        TestRequest request = fx.Logged("/log");
        Assert.Equal("lax=1; strict=1", Cookies(request));
        AssertFetchMetadata(request, "none", "document", "?1", null);
        Assert.Equal(string.Empty, fx.Page.Referrer);
    }

    [Fact]
    public async Task BrowserInitiatedRedirectFromAnotherSiteDropsStrict()
    {
        using var fx = new Fixture();

        await fx.Page.NavigateAsync(fx.CrossSite("/redirect?to=" + Uri.EscapeDataString(fx.Target("/log"))));

        // Chromium: a redirect chain through another site makes the hop cross-site for
        // cookies, while Sec-Fetch-Site stays `none` for a browser-initiated one.
        TestRequest request = fx.Logged("/log");
        Assert.Equal("lax=1", Cookies(request));
        AssertFetchMetadata(request, "none", "document", "?1", null);
    }

    [Fact]
    public async Task PageInitiatedRedirectBackToTheTargetSiteStaysCrossSite()
    {
        using var fx = new Fixture();
        string bounce = fx.CrossSite("/redirect?to=" + Uri.EscapeDataString(fx.Target("/log")));
        fx.Serve("/start", $"<script>location.href = '{bounce}'</script>");

        await fx.Page.NavigateAsync(fx.Target("/start"));

        TestRequest request = fx.Logged("/log");
        Assert.Equal("lax=1", Cookies(request));
        AssertFetchMetadata(request, "cross-site", "document", null, fx.Target("/"));
    }

    [Fact]
    public async Task CrossSiteIframeDropsLaxAndStrict()
    {
        using var fx = new Fixture();
        fx.Serve("/start", $"<iframe src=\"{fx.Target("/log")}\"></iframe>");

        await fx.Page.NavigateAsync(fx.CrossSite("/start"));

        TestRequest request = fx.Logged("/log");
        Assert.Null(Header(request, "cookie"));
        AssertFetchMetadata(request, "cross-site", "iframe", null, fx.CrossSite("/"));
        Assert.Null(Header(request, "origin"));
    }

    [Fact]
    public async Task SameSiteIframeKeepsStrict()
    {
        using var fx = new Fixture();
        fx.Serve("/start", $"<iframe src=\"{fx.Target("/log")}\"></iframe>");

        await fx.Page.NavigateAsync(fx.SameSite("/start"));

        // Upstream loaded frames with same-origin credentials, so this frame got no
        // cookies at all.
        TestRequest request = fx.Logged("/log");
        Assert.Equal("lax=1; strict=1", Cookies(request));
        AssertFetchMetadata(request, "same-site", "iframe", null, fx.SameSite("/"));
    }

    [Fact]
    public async Task UserActivationMarksTheNavigation()
    {
        using var fx = new Fixture();
        fx.Serve("/start", "<p>start</p>");
        await fx.Page.NavigateAsync(fx.CrossSite("/start"));

        fx.Page.Evaluate($"location.href = '{fx.Target("/scripted")}'");
        Assert.True(await fx.Page.ProcessPendingNavigationAsync());
        Assert.Null(Header(fx.Logged("/scripted"), "sec-fetch-user"));

        await fx.Page.NavigateAsync(fx.CrossSite("/start"));
        fx.Page.NoteUserActivation();
        fx.Page.Evaluate($"location.href = '{fx.Target("/clicked")}'");
        Assert.True(await fx.Page.ProcessPendingNavigationAsync());
        TestRequest clicked = fx.Logged("/clicked");
        AssertFetchMetadata(clicked, "cross-site", "document", "?1", fx.CrossSite("/"));
        Assert.Equal("lax=1", Cookies(clicked));
    }

    [Fact]
    public void PendingNavigationRecordsTheInitiatingDocument()
    {
        using Page page = PageFixtures.NewPage("pending-initiator");
        page.Url = UrlRecord.Parse("https://source.example/page?x=1")!;
        page.Dom = PocketCalculator.Dom.HtmlParsing.ParseHtml("<p>x</p>");
        page.InitJs();

        page.Evaluate("location.href = 'https://target.example/next'");
        var pending = page.TakePendingNavigation();

        Assert.NotNull(pending);
        Assert.Equal("https://target.example/next", pending.Url);
        Assert.Equal("https://source.example/page?x=1", pending.Initiator);
        Assert.False(pending.UserActivated);
    }
}
