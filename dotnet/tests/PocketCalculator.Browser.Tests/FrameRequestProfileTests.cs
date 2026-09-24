using System.Globalization;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// What a frame's own requests carry, measured on Chromium 141 with a cross-site frame
/// (https://third-b.com in https://top-a.com): its <c>src=</c> scripts and its fetches are
/// referred by the frame document under that document's policy (its own
/// <c>Referrer-Policy</c> header included), report <c>Sec-Fetch-Site</c> against the frame,
/// and, the frame having no site for cookies, carry and set SameSite=None cookies only.
/// </summary>
/// <remarks>
/// As in <see cref="NavigationInitiatorTests"/>, <c>localhost</c> and <c>127.0.0.1</c> are two
/// sites on one loopback server. Rust fetched a frame's scripts with the address-bar
/// navigation profile: no Referer, <c>Sec-Fetch-Site: none</c>, <c>Sec-Fetch-Dest:
/// document</c> and every cookie, Strict included.
/// </remarks>
public sealed class FrameRequestProfileTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, TestResponse> _pages = new(StringComparer.Ordinal);

        internal Fixture()
        {
            Server = TestHttpServer.Start(Handle);
            Port = new Uri(Server.Origin).Port.ToString(CultureInfo.InvariantCulture);
            Page = PageFixtures.NewPage("frame-request-profile");
            Uri frameSite = new(FrameSite(string.Empty));
            Page.Context.CookieJar.SetCookie("strict=1; SameSite=Strict; Path=/", frameSite);
            Page.Context.CookieJar.SetCookie("lax=1; SameSite=Lax; Path=/", frameSite);
        }

        internal TestHttpServer Server { get; }

        internal string Port { get; }

        internal Page Page { get; }

        /// <summary>The frame's site, 127.0.0.1, where the cookies live.</summary>
        internal string FrameSite(string path) => $"http://127.0.0.1:{Port}{path}";

        /// <summary>The embedding page's site.</summary>
        internal string PageSite(string path) => $"http://localhost:{Port}{path}";

        internal void Serve(string path, TestResponse response) => _pages[path] = response;

        private TestResponse Handle(TestRequest request) =>
            _pages.TryGetValue(request.Path.Split('?')[0], out TestResponse? response)
                ? response
                : TestResponse.JavaScript("/* logged */");

        internal TestRequest Logged(string path)
        {
            DateTime deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                foreach (TestRequest request in Server.Requests)
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

    private static TestResponse FrameDocument(string html, params (string Name, string Value)[] headers) =>
        new("text/html", System.Text.Encoding.UTF8.GetBytes(html), ExtraHeaders: headers);

    [Fact]
    public async Task CrossSiteFrameScriptIsReferredByTheFrameAndCarriesNoLaxCookies()
    {
        using var fx = new Fixture();
        fx.Serve("/start", TestResponse.Html($"<iframe src=\"{fx.FrameSite("/frame?x=1")}\"></iframe>"));
        fx.Serve("/frame", FrameDocument("<script src=\"/frame.js\"></script><script>fetch('/api')</script>"));

        await fx.Page.NavigateAsync(fx.PageSite("/start"));

        TestRequest script = fx.Logged("/frame.js");
        Assert.Equal(fx.FrameSite("/frame?x=1"), Header(script, "referer"));
        Assert.Equal("same-origin", Header(script, "sec-fetch-site"));
        Assert.Equal("no-cors", Header(script, "sec-fetch-mode"));
        Assert.Equal("script", Header(script, "sec-fetch-dest"));
        Assert.Null(Header(script, "sec-fetch-user"));
        Assert.Null(Header(script, "cookie"));

        TestRequest fetch = fx.Logged("/api");
        Assert.Equal(fx.FrameSite("/frame?x=1"), Header(fetch, "referer"));
        Assert.Null(Header(fetch, "cookie"));
    }

    [Fact]
    public async Task FrameDocumentReferrerPolicyHeaderGovernsItsRequests()
    {
        using var fx = new Fixture();
        fx.Serve("/start", TestResponse.Html($"<iframe src=\"{fx.FrameSite("/frame")}\"></iframe>"));
        fx.Serve(
            "/frame",
            FrameDocument(
                "<script src=\"/frame.js\"></script><script>fetch('/api')</script>",
                ("Referrer-Policy", "no-referrer")));

        await fx.Page.NavigateAsync(fx.PageSite("/start"));

        Assert.Null(Header(fx.Logged("/frame.js"), "referer"));
        Assert.Null(Header(fx.Logged("/api"), "referer"));
    }

    [Fact]
    public async Task SameSiteFrameScriptKeepsItsCookies()
    {
        using var fx = new Fixture();
        fx.Serve("/start", TestResponse.Html($"<iframe src=\"{fx.FrameSite("/frame")}\"></iframe>"));
        fx.Serve("/frame", FrameDocument("<script src=\"/frame.js\"></script>"));

        await fx.Page.NavigateAsync(fx.FrameSite("/start"));

        TestRequest script = fx.Logged("/frame.js");
        Assert.Equal(
            "lax=1; strict=1",
            string.Join("; ", (Header(script, "cookie") ?? string.Empty).Split("; ").Order(StringComparer.Ordinal)));
        Assert.Equal(fx.FrameSite("/frame"), Header(script, "referer"));
    }

    [Fact]
    public async Task CrossSiteFrameCannotSetOrReadLaxCookies()
    {
        using var fx = new Fixture();
        fx.Serve("/start", TestResponse.Html($"<iframe src=\"{fx.FrameSite("/frame")}\"></iframe>"));
        fx.Serve(
            "/frame",
            FrameDocument(
                "<script>document.cookie = 'js=1; SameSite=Lax; Path=/';"
                + "fetch('/seen?c=' + encodeURIComponent(document.cookie));</script>",
                ("Set-Cookie", "header=1; SameSite=Lax; Path=/")));

        await fx.Page.NavigateAsync(fx.PageSite("/start"));

        TestRequest seen = fx.Logged("/seen");
        Assert.Equal("/seen?c=", seen.Path);
        Assert.Equal("lax=1; strict=1", fx.Page.Context.CookieJar.GetCookieHeader(new Uri(fx.FrameSite("/"))) switch
        {
            string header => string.Join("; ", header.Split("; ").Order(StringComparer.Ordinal)),
        });
    }
}
