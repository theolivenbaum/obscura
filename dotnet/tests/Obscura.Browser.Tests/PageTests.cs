using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Dom;
using Obscura.Js.Runtime;
using Obscura.Net;
using Obscura.Render;
using Obscura.Js.Url;
using Xunit;

namespace Obscura.Browser.Tests;

/// <summary>
/// The xUnit port of the tests in <c>crates/obscura-browser/src/page.rs</c>, in
/// source order and under the same names.
/// </summary>
public sealed class PageTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public void NavigationTimeoutEnvironmentDefaultRemainsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), PageHelpers.NavigationTimeoutFromEnvValue(null));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            PageHelpers.NavigationTimeoutFromEnvValue("not-a-timeout"));
    }

    [Fact]
    public void NavigationTimeoutEnvironmentOverrideRemainsAvailable() =>
        Assert.Equal(TimeSpan.FromSeconds(42), PageHelpers.NavigationTimeoutFromEnvValue("42000"));

    [Fact]
    public void NavigationChainLimitEnvironmentDefaultRemainsTen()
    {
        Assert.Equal(10, PageHelpers.NavigationChainLimitFromEnvValue(null));
        Assert.Equal(10, PageHelpers.NavigationChainLimitFromEnvValue("not-a-limit"));
    }

    [Fact]
    public void NavigationChainLimitEnvironmentOverrideRemainsAvailable() =>
        Assert.Equal(25, PageHelpers.NavigationChainLimitFromEnvValue("25"));

    [Fact]
    public void NavigationChainLimitRaisesAZeroThatWouldLoadNothing() =>
        Assert.Equal(1, PageHelpers.NavigationChainLimitFromEnvValue("0"));

    [Fact]
    public void CssResourceDiscoveryIgnoresStringsCommentsDataAndFragments()
    {
        var baseUrl = UrlRecord.Parse("https://example.test/css/app/main.css")!;
        const string Css = """

                        /* url(ignored.png) */
                        .copy::before { content: "url(also-ignored.png)"; }
                        @import URL("theme.css") print;
                        @import url("semi;colon.css") screen;
                        .hero { background: url('../img/hero.png'); }
                        .icon { mask: URL("https://cdn.test/icon.svg#shape"); }
                        .inline { background: url(data:image/svg+xml,<svg/>); }
                        .local { mask: url(#local); }

            """;
        Assert.Equal<string>(
            ["https://example.test/css/img/hero.png", "https://cdn.test/icon.svg"],
            PageHelpers.CssResourceUrls(Css, baseUrl));
    }

    [Fact]
    public void FontFaceWarmupTakesTheLastSrcDescriptorAndSkipsUndecodableSources()
    {
        // The IE8 idiom: a bare `.eot` in its own descriptor, then the real list. The
        // renderer resolves the cascade to the second descriptor and then drops
        // `.eot` and `.svg`, so three of the six are all it will ever consider.
        var baseUrl = UrlRecord.Parse("https://example.test/css/app.css")!;
        const string Css = """
            @font-face {
              font-family: 'Probe';
              src: url('/font/probe.eot');
              src: url('/font/probe.eot?#iefix') format('embedded-opentype'),
                   url('/font/probe.woff2') format('woff2'),
                   url('/font/probe.woff') format('woff'),
                   url('/font/probe.ttf') format('truetype'),
                   url('/font/probe.svg#probe') format('svg');
            }
            """;
        Assert.Equal<string>(
            [
                "https://example.test/font/probe.woff2",
                "https://example.test/font/probe.woff",
                "https://example.test/font/probe.ttf",
            ],
            PageHelpers.CssResourceUrls(Css, baseUrl));
    }

    [Fact]
    public void FontFaceWarmupIgnoresLocalSourcesAndKeepsScanningAfterTheBlock()
    {
        // `local()` names an installed face and is not a fetch, and the block has to
        // be reported as consumed at exactly its closing brace, or the rule after it
        // would be skipped with it.
        var baseUrl = UrlRecord.Parse("https://example.test/css/app.css")!;
        const string Css = """
            @font-face {
              font-family: 'Probe';
              src: local('Probe'), url('probe.woff2') format('woff2');
            }
            .hero { background: url('../img/hero.png'); }
            """;
        Assert.Equal<string>(
            ["https://example.test/css/probe.woff2", "https://example.test/img/hero.png"],
            PageHelpers.CssResourceUrls(Css, baseUrl));
    }

    [Fact]
    public void FontFaceWarmupSkipsDataSourcesAndSurvivesABraceInAString()
    {
        var baseUrl = UrlRecord.Parse("https://example.test/css/app.css")!;
        const string Css = """
            @font-face {
              font-family: 'A }';
              src: url(data:font/woff2;base64,d09GMg==) format('woff2'),
                   url('fallback.woff') format('woff');
            }
            .after { background: url('after.png'); }
            """;
        Assert.Equal<string>(
            ["https://example.test/css/fallback.woff", "https://example.test/css/after.png"],
            PageHelpers.CssResourceUrls(Css, baseUrl));
    }

    [Fact]
    public void AnUnterminatedFontFaceBlockFallsBackToTheGenericScan()
    {
        // Better to warm too much than to drop the rest of the stylesheet on the
        // floor, which is the same policy CssImportRuleLength follows.
        var baseUrl = UrlRecord.Parse("https://example.test/css/app.css")!;
        Assert.Equal<string>(
            ["https://example.test/css/probe.woff2"],
            PageHelpers.CssResourceUrls("@font-face { src: url('probe.woff2') format('woff2');", baseUrl));
    }

    /// <summary>Rust's `spawn_stylesheet_graph_server`.</summary>
    private static TestHttpServer SpawnStylesheetGraphServer()
    {
        TestHttpServer? server = null;
        server = TestHttpServer.Start(request =>
        {
            string origin = server!.Origin;
            (string contentType, string body) = request.Path switch
            {
                "/" => ("text/html", """
                    <!doctype html><html><head>
                        <link rel="stylesheet" href="/css/root.css#first">
                        <link rel="stylesheet" href="/css/root.css#second">
                        <link rel="preload stylesheet" href="/theme/second.css">
                    </head><body></body></html>
                    """),
                "/css/root.css" => ("text/css",
                    "@import '/css/nested/shared.css';@import '/blocked.css';@import '/intercepted.css';"
                    + ".root{background:url('img/root.png')}"),
                "/theme/second.css" => ("text/css",
                    "@import '../css/nested/shared.css';.second{background:url('img/second.png')}"),
                "/css/nested/shared.css" => ("text/css",
                    "@import '../root.css';.shared{background:url('../img/shared.png')}"),
                _ => ("text/plain", "unexpected"),
            };
            string status = request.Path is "/blocked.css" or "/intercepted.css"
                ? "500 Unexpected Request"
                : "200 OK";
            return new TestResponse(
                contentType,
                Encoding.UTF8.GetBytes(body),
                status,
                [("X-Origin", origin)]);
        });
        return server;
    }

    /// <summary>Rust's `spawn_inline_import_server`.</summary>
    private static TestHttpServer SpawnInlineImportServer() =>
        TestHttpServer.Start(request => request.Path switch
        {
            "/" => TestResponse.Html("""
                <!doctype html><style media="screen, print">
                    @import url('/a.css') print;
                    @import '/b.css' print;
                    .local { color: white; background-image: url('/local.svg') }
                </style><div class="local imported-a imported-b">marker</div>
                """),
            "/a.css" => TestResponse.Css(".imported-a{background:#9020d0 url('/imported.svg')}"),
            "/b.css" => TestResponse.Css(".imported-b{border-color:#f0d020}"),
            "/local.svg" or "/imported.svg" => TestResponse.Svg(
                """<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"><rect width="1" height="1" fill="white"/></svg>"""),
            _ => TestResponse.Text("unexpected"),
        });

    [Fact]
    public void DefaultNavigationReferrerMatchesStrictOriginWhenCrossOrigin()
    {
        var source = UrlRecord.Parse("https://user:pass@source.example/path?q=1#fragment")!;
        var sameOrigin = UrlRecord.Parse("https://source.example/next")!;
        var crossOrigin = UrlRecord.Parse("https://target.example/next")!;
        var downgrade = UrlRecord.Parse("http://source.example/next")!;

        Assert.Equal(
            "https://source.example/path?q=1",
            PageHelpers.NavigationReferrer(source, sameOrigin));
        Assert.Equal("https://source.example/", PageHelpers.NavigationReferrer(source, crossOrigin));
        Assert.Equal(string.Empty, PageHelpers.NavigationReferrer(source, downgrade));

        var dataSource = UrlRecord.Parse("data:text/html,source")!;
        Assert.Equal(string.Empty, PageHelpers.NavigationReferrer(dataSource, crossOrigin));
    }

    [Fact]
    public async Task DocumentNavigationReferrerSurvivesHttpRedirects()
    {
        using TestHttpServer server = TestHttpServer.Start(request => request.Path switch
        {
            "/source" => TestResponse.Html("<script>location.href='/redirect'</script>"),
            "/redirect" => new TestResponse(
                "text/plain",
                [],
                "302 Found",
                [("Location", "/final")]),
            "/final" => TestResponse.Html("<!doctype html><title>final</title>"),
            _ => new TestResponse("text/plain", [], "404 Not Found"),
        });

        using Page page = PageFixtures.NewPage("referrer-redirect");
        string source = $"{server.Origin}/source";
        await page.NavigateAsync(source);

        PageFixtures.AssertJson(
            $"[\"{server.Origin}/final\", \"{source}\"]",
            page.Js!.Evaluate("[document.URL, document.referrer]"));
    }

    /// <summary>
    /// `/hop/N` sets `location.href = "/hop/N-1"`, `/hop/0` is the target. An
    /// unreadable path gets a 404, so a broken fixture fails the test.
    /// </summary>
    private static TestHttpServer ClientNavigationChainServer(string name) =>
        TestHttpServer.Start(request =>
        {
            if (!request.Path.StartsWith("/hop/", StringComparison.Ordinal)
                || !int.TryParse(
                    request.Path["/hop/".Length..],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int hop))
            {
                return new TestResponse("text/plain", [], "404 Not Found");
            }
            return hop == 0
                ? TestResponse.Html($"<!doctype html><title>{name}</title>")
                : TestResponse.Html(
                    $"<script>location.href='/hop/{(hop - 1).ToString(CultureInfo.InvariantCulture)}'</script>");
        });

    /// <summary>
    /// The only place where the name of the environment variable is checked as a
    /// string, and the variable is the only way to raise the limit at runtime.
    /// Without this test a typo in the name would stay green.
    /// </summary>
    [Fact]
    public void NavigationChainLimitReadsTheEnvironmentVariableByName()
    {
        string? previous = Environment.GetEnvironmentVariable("OBSCURA_NAV_CHAIN_LIMIT");
        Environment.SetEnvironmentVariable("OBSCURA_NAV_CHAIN_LIMIT", "17");
        try
        {
            using Page page = PageFixtures.NewPage("chain-from-environment");
            Assert.Equal(17, page.NavigationChainLimit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OBSCURA_NAV_CHAIN_LIMIT", previous);
        }
    }

    [Fact]
    public void APerPageNavigationChainLimitWinsOverTheEnvironment()
    {
        string? previous = Environment.GetEnvironmentVariable("OBSCURA_NAV_CHAIN_LIMIT");
        Environment.SetEnvironmentVariable("OBSCURA_NAV_CHAIN_LIMIT", "17");
        try
        {
            using Page page = PageFixtures.NewPage("chain-per-page-wins");
            page.SetNavigationChainLimit(4);
            Assert.Equal(4, page.NavigationChainLimit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OBSCURA_NAV_CHAIN_LIMIT", previous);
        }
    }

    [Fact]
    public async Task NavigationChainDefaultAllowsNineClientNavigations()
    {
        using TestHttpServer server = ClientNavigationChainServer("arrived");
        using Page page = PageFixtures.ChainPage(
            "chain-within-default",
            PageHelpers.DefaultNavigationChainLimit);

        await page.NavigateAsync($"{server.Origin}/hop/9");

        Assert.Equal($"{server.Origin}/hop/0", page.UrlString());
    }

    [Fact]
    public async Task NavigationChainBeyondTheDefaultReportsClientNavigations()
    {
        using TestHttpServer server = ClientNavigationChainServer("arrived");
        using Page page = PageFixtures.ChainPage(
            "chain-beyond-default",
            PageHelpers.DefaultNavigationChainLimit);

        PageException error = await Assert.ThrowsAsync<PageException>(
            () => page.NavigateAsync($"{server.Origin}/hop/10"));

        Assert.Equal(PageErrorKind.TooManyClientNavigations, error.Kind);
        Assert.Equal(10, error.ChainLimit);
        Assert.Equal(
            "Too many client-initiated navigations, the chain reached its limit of 10 documents",
            error.Message);
    }

    [Fact]
    public async Task RaisedNavigationChainLimitReachesALongerChain()
    {
        using TestHttpServer server = ClientNavigationChainServer("arrived");
        using Page page = PageFixtures.ChainPage("chain-raised", 12);

        await page.NavigateAsync($"{server.Origin}/hop/11");

        Assert.Equal($"{server.Origin}/hop/0", page.UrlString());
    }

    [Fact]
    public async Task AZeroNavigationChainLimitStillLoadsTheInitialDocument()
    {
        using TestHttpServer server = ClientNavigationChainServer("arrived");
        using Page page = PageFixtures.ChainPage("chain-zero", 0);

        await page.NavigateAsync($"{server.Origin}/hop/0");

        Assert.Equal($"{server.Origin}/hop/0", page.UrlString());
    }

    [Fact]
    public async Task LinkedStylesheetGraphFetchesOnceAndPreservesOrderAndBases()
    {
        using TestHttpServer server = SpawnStylesheetGraphServer();
        using Page page = PageFixtures.NewPage("stylesheet-graph");
        page.SetBlockedUrls(["*blocked.css"]);
        page.InterceptBlockPatterns.Add("*intercepted.css");
        page.EnableIntercept(true);

        int requestCount = 0;
        int responseCount = 0;
        page.OnRequest(request =>
        {
            if (request.ResourceType == ResourceType.Stylesheet)
            {
                Interlocked.Increment(ref requestCount);
            }
        });
        page.OnResponse((request, _) =>
        {
            if (request.ResourceType == ResourceType.Stylesheet)
            {
                Interlocked.Increment(ref responseCount);
            }
        });

        await page.NavigateAsync($"{server.Origin}/");

        Assert.Equal<string>(
            ["/", "/css/nested/shared.css", "/css/root.css", "/theme/second.css"],
            server.SortedPaths(4, RequestTimeout));
        Assert.Equal(3, requestCount);
        Assert.Equal(3, responseCount);
        Assert.Equal(
            3,
            page.NetworkEvents.Count(e => string.Equals(e.ResourceType, "Stylesheet", StringComparison.Ordinal)));

        List<string> sheets = page.Js!.WithDom(dom =>
            dom.QuerySelectorAll("style[data-obscura-external-stylesheets]")
                .Select(dom.TextContent)
                .ToList())!;
        Assert.Equal(3, sheets.Count);
        Assert.Equal(sheets[0], sheets[1]);
        Assert.True(
            sheets[0].IndexOf(".shared", StringComparison.Ordinal)
                < sheets[0].IndexOf(".root", StringComparison.Ordinal),
            "imports precede the importing sheet");
        Assert.Contains($"url(\"{server.Origin}/css/img/shared.png\")", sheets[0], StringComparison.Ordinal);
        Assert.Contains($"url(\"{server.Origin}/css/img/root.png\")", sheets[0], StringComparison.Ordinal);
        int root = sheets[2].IndexOf(".root", StringComparison.Ordinal);
        int shared = sheets[2].IndexOf(".shared", StringComparison.Ordinal);
        int second = sheets[2].IndexOf(".second", StringComparison.Ordinal);
        Assert.True(root < shared && shared < second, "cycle is cut without reordering rules");
        Assert.Contains($"url(\"{server.Origin}/theme/img/second.png\")", sheets[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InlineImportsFetchInOrderAndMaterializeBeforeSourceStyle()
    {
        using TestHttpServer server = SpawnInlineImportServer();
        using Page page = PageFixtures.NewPage("inline-imports");
        page.SetViewport((100.0f, 80.0f));
        List<(string Path, ResourceType Type)> observed = [];
        page.OnRequest(request =>
        {
            lock (observed)
            {
                observed.Add((request.Url.AbsolutePath, request.ResourceType));
            }
        });
        await page.NavigateAsync($"{server.Origin}/");

        Assert.Equal<string>(["/", "/a.css", "/b.css"], server.SortedPaths(3, RequestTimeout));
        lock (observed)
        {
            foreach (string path in new[] { "/a.css", "/b.css" })
            {
                Assert.Equal<ResourceType>(
                    [ResourceType.Stylesheet],
                    [.. observed.Where(entry => string.Equals(entry.Path, path, StringComparison.Ordinal))
                        .Select(entry => entry.Type)]);
            }
            foreach (string path in new[] { "/local.svg", "/imported.svg" })
            {
                Assert.Equal<ResourceType>(
                    [ResourceType.Image],
                    [.. observed.Where(entry => string.Equals(entry.Path, path, StringComparison.Ordinal))
                        .Select(entry => entry.Type)]);
            }
        }

        List<(bool IsImport, string? Media, string Text)> styles = page.Js!.WithDom(dom =>
            dom.QuerySelectorAll("style")
                .Select(nid =>
                {
                    Node node = dom.GetNode(nid)!;
                    return (
                        node.GetAttribute("data-obscura-inline-import") is not null,
                        node.GetAttribute("media"),
                        dom.TextContent(nid));
                })
                .ToList())!;
        Assert.Equal(3, styles.Count);
        Assert.True(styles[0].IsImport);
        Assert.Contains(".imported-a", styles[0].Text, StringComparison.Ordinal);
        Assert.True(styles[1].IsImport);
        Assert.Contains(".imported-b", styles[1].Text, StringComparison.Ordinal);
        Assert.False(styles[2].IsImport);
        Assert.Contains(".local", styles[2].Text, StringComparison.Ordinal);
        Assert.Equal("screen, print", styles[0].Media);
        Assert.Equal("screen, print", styles[1].Media);
        Assert.StartsWith("@media print {\n", styles[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("@media print {\n", styles[1].Text, StringComparison.Ordinal);

        byte[] pdf = page.RasterPdf(new RasterPdfOptions
        {
            PrintBackground = true,
            PaperWidthIn = 100.0f / 72.0f,
            PaperHeightIn = 80.0f / 72.0f,
            MarginTopIn = 0.0f,
            MarginBottomIn = 0.0f,
            MarginLeftIn = 0.0f,
            MarginRightIn = 0.0f,
        });
        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(pdf[..8]), StringComparison.Ordinal);
    }
    /// <summary>Rust's `spawn_script_resource_cache_server`.</summary>
    private static (TestHttpServer Server, Func<int> Requests, string Path) SpawnScriptResourceCacheServer(
        bool distinct)
    {
        int scriptRequests = 0;
        TestHttpServer server = TestHttpServer.Start(request =>
        {
            if (string.Equals(request.Path, "/duplicate.html", StringComparison.Ordinal))
            {
                string tags = string.Concat(Enumerable.Repeat("<script src='/shared.js'></script>", 32));
                return new TestResponse(
                    "text/html",
                    Encoding.UTF8.GetBytes(
                        $"<!doctype html><html><body><script>globalThis.__runs=0</script>{tags}</body></html>"),
                    ExtraHeaders: [("Cache-Control", "no-store")]);
            }
            if (string.Equals(request.Path, "/distinct.html", StringComparison.Ordinal))
            {
                string tags = string.Concat(Enumerable.Range(0, 24).Select(index =>
                    $"<script src='/distinct/{index.ToString(CultureInfo.InvariantCulture)}.js'></script>"));
                return new TestResponse(
                    "text/html",
                    Encoding.UTF8.GetBytes(
                        $"<!doctype html><html><body><script>globalThis.__runs=0</script>{tags}</body></html>"),
                    ExtraHeaders: [("Cache-Control", "no-store")]);
            }
            if (string.Equals(request.Path, "/shared.js", StringComparison.Ordinal)
                || request.Path.StartsWith("/distinct/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref scriptRequests);
                return new TestResponse(
                    "application/javascript",
                    Encoding.UTF8.GetBytes("globalThis.__runs=(globalThis.__runs||0)+1;"),
                    ExtraHeaders: [("Cache-Control", "public, max-age=3600")],
                    DelayMs: 80);
            }
            return new TestResponse(
                "text/plain",
                Encoding.UTF8.GetBytes("not found"),
                ExtraHeaders: [("Cache-Control", "no-store")]);
        });
        return (server, () => Volatile.Read(ref scriptRequests), distinct ? "distinct.html" : "duplicate.html");
    }

    [Fact]
    public async Task DuplicateCacheableScriptsFetchOnceButExecuteForEachElement()
    {
        (TestHttpServer server, Func<int> scriptRequests, string path) = SpawnScriptResourceCacheServer(false);
        using (server)
        {
            using Page page = PageFixtures.NewPage("duplicate-script-cache");

            await page.NavigateAsync($"{server.Origin}/{path}");

            Assert.Equal(32.0, PageFixtures.AsDouble(page.Js!.Evaluate("globalThis.__runs")));
            Assert.Equal(1, scriptRequests());
        }
    }

    [Fact]
    public async Task DistinctCacheableScriptsKeepDistinctNetworkRequests()
    {
        (TestHttpServer server, Func<int> scriptRequests, string path) = SpawnScriptResourceCacheServer(true);
        using (server)
        {
            using Page page = PageFixtures.NewPage("distinct-script-cache");

            await page.NavigateAsync($"{server.Origin}/{path}");

            Assert.Equal(24.0, PageFixtures.AsDouble(page.Js!.Evaluate("globalThis.__runs")));
            Assert.Equal(24, scriptRequests());
        }
    }

    [Fact]
    public void ExternalScriptsRequireASuccessfulHttpStatus()
    {
        Assert.True(PageHelpers.ScriptResponseIsExecutable(200));
        Assert.True(PageHelpers.ScriptResponseIsExecutable(204));
        Assert.True(PageHelpers.ScriptResponseIsExecutable(299));
        Assert.False(PageHelpers.ScriptResponseIsExecutable(0));
        Assert.False(PageHelpers.ScriptResponseIsExecutable(304));
        Assert.False(PageHelpers.ScriptResponseIsExecutable(401));
        Assert.False(PageHelpers.ScriptResponseIsExecutable(404));
        Assert.False(PageHelpers.ScriptResponseIsExecutable(500));
    }

    /// <summary>
    /// `/` puts its iframe inside a closed shadow root, `/plain.html` puts the same
    /// iframe straight in the document, and `/child.html` is the frame.
    /// </summary>
    private static TestHttpServer SpawnShadowFrameServer() =>
        TestHttpServer.Start(request => TestResponse.Html(request.Path switch
        {
            "/child.html" => "<html><body><script>window.__ran = 'YES';</script></body></html>",
            "/plain.html" => "<html><body><iframe src=\"/child.html\"></iframe></body></html>",
            _ => "<html><body><div id=\"host\"></div><script>"
                + "var r = document.getElementById('host').attachShadow({mode:'closed'});"
                + "var f = document.createElement('iframe');"
                + "f.src = '/child.html';"
                + "r.appendChild(f);"
                + "</script></body></html>",
        }));

    private static (TestHttpServer Server, List<string> Requests, TaskCompletionSource SlowSeen)
        SpawnSlowFrameScriptServer()
    {
        List<string> requests = [];
        var slowSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestHttpServer server = TestHttpServer.Start(request =>
        {
            lock (requests)
            {
                requests.Add(request.Path);
            }
            if (string.Equals(request.Path, "/slow.js", StringComparison.Ordinal))
            {
                slowSeen.TrySetResult();
                return TestResponse.JavaScript(
                    "globalThis.__order.push('second'); globalThis.__childReady = true;")
                    with
                { DelayMs = 250 };
            }
            if (string.Equals(request.Path, "/first.js", StringComparison.Ordinal))
            {
                return TestResponse.JavaScript("globalThis.__order.push('first');");
            }
            return TestResponse.Html(
                "<html><body><script src=/first.js></script><script src=/slow.js></script></body></html>");
        });
        return (server, requests, slowSeen);
    }

    private static async Task<(TestHttpServer Server, Page Page, List<string> Requests, TaskCompletionSource SlowSeen)>
        PageWithFetchedSlowFramesAsync(string name, int frameCount)
    {
        (TestHttpServer server, List<string> requests, TaskCompletionSource slowSeen) =
            SpawnSlowFrameScriptServer();
        string iframes = string.Concat(
            Enumerable.Repeat("<iframe src=/child.html></iframe>", frameCount));
        Page page = PageFixtures.ImportMapTestPage(name, server.Origin, $"<html><body>{iframes}</body></html>");
        // Rust runs the event loop to full idle once. The port's pump can return at
        // an idle-looking moment between the two frame fetches, so wait for the
        // documents themselves rather than for one pass of the loop.
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (page.Js!.State.PendingFrames.Count < frameCount && DateTime.UtcNow < deadline)
        {
            await page.Js!.RunEventLoopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(frameCount, page.Js!.State.PendingFrames.Count);
        return (server, page, requests, slowSeen);
    }

    private static TestHttpServer SpawnNestedPendingFrameServer(List<string> requests) =>
        TestHttpServer.Start(request =>
        {
            lock (requests)
            {
                requests.Add(request.Path);
            }
            return request.Path switch
            {
                "/parent.html" => TestResponse.Html(
                    "<html><body><script>const child = document.createElement('iframe');"
                    + " child.src = '/grandchild.html'; document.body.appendChild(child);</script></body></html>"),
                "/grandchild.html" => TestResponse.Html(
                    "<html><body><script src=/grandchild-slow.js></script></body></html>"),
                _ => TestResponse.JavaScript("globalThis.__mustNotRun = true;"),
            };
        });

    [Fact]
    public async Task CancellingFrameScriptFetchKeepsAllSiblingsResumable()
    {
        (TestHttpServer server, Page page, List<string> requests, TaskCompletionSource slowSeen) =
            await PageWithFetchedSlowFramesAsync("cancel-frame-scripts", 2);
        using (server)
        using (page)
        {
            page.AddPreloadScript("globalThis.__order = ['preload'];");
            Assert.True(page.QueuePendingFrames());
            var unattached = Assert.IsType<PendingFrameWork.Unattached>(page._pendingFrameWork.First!.Value);
            (ulong width, ulong height) =
                (unattached.Frame.ViewportWidth, unattached.Frame.ViewportHeight);

            using var cancel = new CancellationTokenSource();
            Task advance = page.AdvanceFramesAsync(cancel.Token);
            Task completed = await Task.WhenAny(advance, slowSeen.Task, Task.Delay(2_000));
            Assert.Same(slowSeen.Task, completed);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advance);

            Assert.Single(page.Frames);
            // PORT DEVIATION: Rust reads this through `iframe.contentWindow`, which
            // hands the page the frame's real window object. ClearScript refuses a
            // ScriptObject from another engine (see "Cross-realm objects cannot be
            // shared" in todo.md), so the same invariant - a published loading frame
            // is observable, with its preload applied and its viewport set, before
            // its own scripts have run - is asserted from inside the realm.
            PageFixtures.AssertJson(
                $$"""{"order":["preload"],"width":{{width}},"height":{{height}}}""",
                page.EvaluateInFrame(
                    0,
                    "({ order: globalThis.__order, width: innerWidth, height: innerHeight })"));
            Assert.Equal(2, page._pendingFrameWork.Count);
            var attached = Assert.IsType<PendingFrameWork.Attached>(page._pendingFrameWork.First!.Value);
            Assert.Equal(1, attached.NextUrl);
            Assert.IsType<PendingFrameWork.Unattached>(page._pendingFrameWork.Last!.Value);

            await page.AdvanceFramesAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(page._pendingFrameWork);
            for (int index = 0; index < 2; index++)
            {
                PageFixtures.AssertJson("true", page.EvaluateInFrame(index, "globalThis.__childReady"));
                PageFixtures.AssertJson(
                    """["preload","first","second"]""",
                    page.EvaluateInFrame(index, "globalThis.__order"));
            }
            List<string> scriptOrder;
            lock (requests)
            {
                Assert.Equal(2, requests.Count(path => string.Equals(path, "/first.js", StringComparison.Ordinal)));
                scriptOrder = [.. requests.Where(path => path.EndsWith(".js", StringComparison.Ordinal))];
            }
            Assert.Equal<string>(
                ["/first.js", "/slow.js", "/slow.js", "/first.js", "/slow.js"],
                scriptOrder);
        }
    }

    [Fact]
    public async Task DetachedFrameDiscardsQueuedScriptWorkBeforeFetching()
    {
        (TestHttpServer server, Page page, _, TaskCompletionSource slowSeen) =
            await PageWithFetchedSlowFramesAsync("detach-frame-scripts", 2);
        using (server)
        using (page)
        {
            using var cancel = new CancellationTokenSource();
            Task advance = page.AdvanceFramesAsync(cancel.Token);
            Task completed = await Task.WhenAny(advance, slowSeen.Task, Task.Delay(2_000));
            Assert.Same(slowSeen.Task, completed);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advance);

            // Both iframes carry the same src, so which element ends up bound to the
            // frame whose script work is queued depends on fetch completion order.
            // Remove that element rather than the first one in document order.
            uint attachedId = page._pendingFrameWork.First!.Value.FrameId;
            PageFixtures.AssertJson(
                "1",
                page.Js!.Evaluate(
                    "(function(){ for (const frame of document.querySelectorAll('iframe')) {"
                    + $" if (frame._frameId === {attachedId}) {{ frame.remove(); return 1; }} }} return 0; }})()"));

            page.ReleaseDetachedFrames();
            Assert.Empty(page.Frames);
            Assert.Single(page._pendingFrameWork);
            Assert.IsType<PendingFrameWork.Unattached>(page._pendingFrameWork.First!.Value);
        }
    }

    [Fact]
    public async Task DetachedUnattachedFrameIsDiscardedBeforeRealmOrScriptWork()
    {
        (TestHttpServer server, Page page, List<string> requests, _) =
            await PageWithFetchedSlowFramesAsync("detach-unattached-frame", 1);
        using (server)
        using (page)
        {
            Assert.True(page.QueuePendingFrames());
            Assert.Empty(page.Frames);
            Assert.IsType<PendingFrameWork.Unattached>(page._pendingFrameWork.First!.Value);

            page.Js!.Evaluate("(document.querySelector('iframe').remove(), 1)");
            page.ReleaseDetachedFrames();

            Assert.Empty(page.Frames);
            Assert.Empty(page._pendingFrameWork);
            lock (requests)
            {
                Assert.DoesNotContain(requests, path => path.EndsWith(".js", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task PruningAnOldBatchDoesNotStrandANewRuntimeBatch()
    {
        (TestHttpServer server, Page page, _, TaskCompletionSource slowSeen) =
            await PageWithFetchedSlowFramesAsync("replace-frame-work-batch", 1);
        using (server)
        using (page)
        {
            page.AddPreloadScript("globalThis.__order = ['preload'];");
            using var cancel = new CancellationTokenSource();
            Task advance = page.AdvanceFramesAsync(cancel.Token);
            Task completed = await Task.WhenAny(advance, slowSeen.Task, Task.Delay(2_000));
            Assert.Same(slowSeen.Task, completed);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advance);
            Assert.Single(page._pendingFrameWork);

            page.Js!.Evaluate(
                "(function(){ const next = document.createElement('iframe'); next.src = '/child.html';"
                + " document.body.appendChild(next); return 1; })()");
            await page.Js!.RunEventLoopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            page.Js!.Evaluate("(document.querySelector('iframe').remove(), 1)");

            await page.AdvanceFramesAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(page.Frames);
            Assert.Empty(page._pendingFrameWork);
            PageFixtures.AssertJson("true", page.EvaluateInFrame(0, "globalThis.__childReady"));
        }
    }

    [Fact]
    public async Task RuntimeTeardownDiscardsQueuedFrameScriptWork()
    {
        (TestHttpServer suspendedServer, Page suspended, _, _) =
            await PageWithFetchedSlowFramesAsync("suspend-frame-scripts", 1);
        using (suspendedServer)
        using (suspended)
        {
            Assert.True(suspended.QueuePendingFrames());
            suspended.SuspendJs();
            Assert.Empty(suspended.Frames);
            Assert.Empty(suspended._pendingFrameWork);
        }

        (TestHttpServer replacedServer, Page replaced, _, _) =
            await PageWithFetchedSlowFramesAsync("replace-frame-scripts", 1);
        using (replacedServer)
        using (replaced)
        {
            Assert.True(replaced.QueuePendingFrames());
            replaced.NavigateBlank();
            Assert.Empty(replaced.Frames);
            Assert.Empty(replaced._pendingFrameWork);
        }
    }

    // BLOCKED on a bug in Obscura.Js, not on this port: `op_frame_document_ready`
    // reads the calling realm from `RealmStates.Current`, which the runtime sets
    // only around synchronous host entries into a realm. bootstrap.js calls that op
    // from inside `fetch(...).then(...)` in `_loadIframeSrc`, so a frame created by
    // a frame's script is recorded with parentFrameId 0 (the page) instead of its
    // real parent. Rust reads the parent from the V8 entered-or-microtask context,
    // which is correct for an async continuation. Everything below this point is
    // written and will pass once the op resolves the realm the way Rust does.
    [Fact(Skip = "blocked on Obscura.Js: op_frame_document_ready records parentFrameId 0 for a frame created inside an async continuation, because RealmStates.Current is only set around synchronous host entries")]
    public async Task DetachingAParentDiscardsItsQueuedDescendantWork()
    {
        List<string> requests = [];
        using TestHttpServer server = SpawnNestedPendingFrameServer(requests);
        using Page page = PageFixtures.ImportMapTestPage(
            "detach-nested-frame-work",
            server.Origin,
            "<html><body><iframe src=/parent.html></iframe></body></html>");
        await page.Js!.RunEventLoopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(page.QueuePendingFrames());
        Assert.True(await page.RunNextPendingFrameAsync(CancellationToken.None));
        Assert.Single(page.Frames);

        await page.Js!.RunEventLoopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(page.QueuePendingFrames());
        Assert.Single(page._pendingFrameWork);
        Assert.Equal(page.Frames[0].FrameId, page._pendingFrameWork.First!.Value.ParentFrameId);

        page.Js!.Evaluate("(document.querySelector('iframe').remove(), 1)");
        page.ReleaseDetachedFrames();
        Assert.Empty(page.Frames);
        Assert.Empty(page._pendingFrameWork);
        lock (requests)
        {
            Assert.DoesNotContain(
                requests,
                path => string.Equals(path, "/grandchild-slow.js", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A <c>FrameRealm</c> owns a handle into the runtime's isolate, which is why
    /// <c>InitJs</c> clears the frames before it drops the runtime. <c>SuspendJs</c>
    /// drops the same runtime and has to honour the same order, otherwise the realms
    /// of a suspended page outlive the isolate they point into.
    /// </summary>
    [Fact]
    public async Task SuspendingTheRuntimeReleasesTheFrameRealmsItOwns()
    {
        using TestHttpServer server = SpawnShadowFrameServer();
        using Page page = PageFixtures.NewPage("suspend-frames");
        await page.NavigateAsync($"{server.Origin}/plain.html");
        Assert.Single(page.FrameUrls());

        page.SuspendJs();
        Assert.Empty(page.FrameUrls());

        // The page still works afterwards: resuming rebuilds the runtime, and
        // navigating again builds the frames of the new document.
        page.ResumeJs();
        await page.NavigateAsync($"{server.Origin}/plain.html");
        Assert.Single(page.FrameUrls());
    }

    /// <summary>
    /// An iframe inside a shadow root is absent from
    /// <c>document.querySelectorAll('iframe')</c> - real Chrome reports 0 for it too
    /// - so a liveness check built on that query reads a live frame as detached and
    /// tears it down. That is the shape a challenge widget uses.
    /// </summary>
    [Fact]
    public async Task AFrameInsideAShadowRootSurvivesTheDetachSweep()
    {
        using TestHttpServer server = SpawnShadowFrameServer();
        using Page page = PageFixtures.FramePage("shadow-frame-survives");
        await page.NavigateAsync($"{server.Origin}/");
        await page.SettleAsync(1_000);

        Assert.Single(page.Frames);
        // The realm is not merely alive; the page can still reach into it.
        Assert.Equal(
            1.0,
            PageFixtures.AsDouble(
                page.Js!.Evaluate("Object.keys(globalThis.__obscura_frameObjects).length")));
    }

    /// <summary>
    /// The sweep still does its job: an iframe removed from the document has its
    /// realm and every reference the page realm holds to it released.
    /// </summary>
    [Fact]
    public async Task RemovingAnIframeReleasesItsRealm()
    {
        using TestHttpServer server = SpawnShadowFrameServer();
        using Page page = PageFixtures.FramePage("detached-frame-released");
        await page.NavigateAsync($"{server.Origin}/plain.html");
        await page.SettleAsync(1_000);
        Assert.Single(page.Frames);

        page.Js!.Evaluate("(document.querySelector('iframe').remove(), 1)");
        page.ReleaseDetachedFrames();

        Assert.Empty(page.Frames);
        Assert.Equal(
            0.0,
            PageFixtures.AsDouble(page.Js!.Evaluate(
                "Object.keys(globalThis.__obscura_frameObjects).length"
                + " + Object.keys(globalThis.__obscura_frameWindows).length"
                + " + Object.keys(globalThis.__obscura_frameElements).length")));
    }

    [Fact]
    public async Task SuspendResumePreservesDocumentScriptStartState()
    {
        using Page page = PageFixtures.NewPage("script-state-suspend");
        page.Url = UrlRecord.Parse("http://example.com/suspend.html")!;
        page.Dom = HtmlParsing.ParseHtml(
            """
            <html><head></head><body data-parser-runs="0" data-dynamic-runs="0" data-inert-runs="0">
            <script id="parser">
              document.body.setAttribute("data-parser-runs", String(Number(document.body.getAttribute("data-parser-runs")) + 1));
            </script>
            </body></html>
            """);
        page.InitJs();
        await page.ExecuteScriptsAsync(CancellationToken.None);

        PageFixtures.AssertJson(
            """["1", "1", "0"]""",
            page.Js!.Evaluate(
                """
                var scriptStateSetup = true;
                const dynamic = document.createElement("script");
                dynamic.id = "dynamic";
                dynamic.textContent =
                  'document.body.setAttribute("data-dynamic-runs", String(Number(document.body.getAttribute("data-dynamic-runs")) + 1))';
                document.body.appendChild(dynamic);

                const holder = document.createElement("div");
                holder.innerHTML =
                  '<script id="inert">document.body.setAttribute("data-inert-runs", String(Number(document.body.getAttribute("data-inert-runs")) + 1))<\/script>';
                document.body.appendChild(holder.firstChild);
                return [
                  document.body.getAttribute("data-parser-runs"),
                  document.body.getAttribute("data-dynamic-runs"),
                  document.body.getAttribute("data-inert-runs")
                ];
                """));

        page.SuspendJs();
        page.SuspendJs();
        page.ResumeJs();

        PageFixtures.AssertJson(
            """["1", "1", "0"]""",
            page.Js!.Evaluate(
                """
                var scriptStateCheck = true;
                for (const id of ["parser", "dynamic", "inert"]) {
                  const script = document.getElementById(id);
                  document.head.appendChild(script);
                  document.body.appendChild(script.cloneNode(true));
                }
                return [
                  document.body.getAttribute("data-parser-runs"),
                  document.body.getAttribute("data-dynamic-runs"),
                  document.body.getAttribute("data-inert-runs")
                ];
                """));
    }

    [Fact]
    public async Task SuspendResumePreservesCdpEvaluationHandles()
    {
        using Page page = PageFixtures.NewPage("cdp-handle-suspend");
        page.Url = UrlRecord.Parse("http://example.com/")!;
        page.Dom = HtmlParsing.ParseHtml("<html><body></body></html>");
        page.InitJs();

        RemoteObjectInfo remoteObject = await page.EvaluateForCdpWithTimeoutAsync(
            "({ increment(value) { return value + 1; } })",
            false,
            false,
            1_000);
        string objectId = Assert.IsType<string>(remoteObject.ObjectId);

        page.SuspendJs();
        page.ResumeJs();

        RemoteObjectInfo result = await page.CallFunctionOnForCdpWithTimeoutAsync(
            "function(value) { return this.increment(value); }",
            objectId,
            [JsonNode.Parse("""{ "value": 41 }""")],
            true,
            false,
            1_000);
        Assert.Equal(42.0, PageFixtures.AsDouble(result.Value));
    }

    [Fact]
    public void NewDocumentDoesNotInheritSuspendedScriptIds()
    {
        using Page page = PageFixtures.NewPage("script-state-navigation");
        page.Url = UrlRecord.Parse("http://example.com/old.html")!;
        page.Dom = HtmlParsing.ParseHtml(
            "<html><head></head><body><script id=old></script></body></html>");
        page.InitJs();
        page.Js!.Evaluate(
            "var setup = true; const old = document.getElementById('old');"
            + " globalThis.__markParserScripts([old._nid]); return old._nid;");
        page.SuspendJs();

        page.Url = UrlRecord.Parse("http://example.com/new.html")!;
        page.Dom = HtmlParsing.ParseHtml(
            "<html><head></head><body data-fresh-runs=0><script id=fresh>"
            + "document.body.setAttribute('data-fresh-runs', '1')</script></body></html>");
        page.InitJs();
        PageFixtures.AssertJson(
            "\"1\"",
            page.Js!.Evaluate(
                "var check = true; document.head.appendChild(document.getElementById('fresh'));"
                + " return document.body.getAttribute('data-fresh-runs');"));
    }

    [Fact]
    public async Task ModuleGraphAndEvaluationShareOneActiveBudget()
    {
        // Spend part of the module's allowance loading its graph. The synchronous
        // top-level work then fits in a freshly reset budget, but cannot fit in the
        // shared active load+evaluation budget.
        using TestHttpServer server = TestHttpServer.Start(_ =>
            TestResponse.JavaScript("export const delayed = true;") with { DelayMs = 100 });

        using Page page = PageFixtures.ImportMapTestPage(
            "shared-module-budget",
            server.Origin,
            """
            <html><head><script type="module">
                import "./delayed.js";
                globalThis.__shared_deadline_started = true;
                const until = Date.now() + 300;
                while (Date.now() < until) {}
                globalThis.__shared_deadline_completed = true;
            </script></head><body></body></html>
            """);
        await page.ExecuteScriptsWithModuleBudgetAsync(350, CancellationToken.None);

        Assert.Equal("/app/delayed.js", server.NextPath(RequestTimeout));
        PageFixtures.AssertJson(
            "[true, false]",
            page.Js!.Evaluate(
                "[globalThis.__shared_deadline_started === true,"
                + " globalThis.__shared_deadline_completed === true]"));
    }

    [Fact]
    public async Task QueuedModuleDoesNotSpendItsBudgetWaitingForDeferredScript()
    {
        using TestHttpServer server = TestHttpServer.Start(request =>
            request.Path.EndsWith("deferred.js", StringComparison.Ordinal)
                ? TestResponse.JavaScript("const until=Date.now()+500;while(Date.now()<until){}")
                : TestResponse.JavaScript("export const ready=true;"));

        using Page page = PageFixtures.ImportMapTestPage(
            "module-queue-budget",
            server.Origin,
            """
            <html><head>
                <script defer src="./deferred.js"></script>
                <script type="module">
                    import { ready } from "./quick.js";
                    globalThis.__queued_module_completed = ready;
                </script>
            </head><body></body></html>
            """);
        await page.ExecuteScriptsWithModuleBudgetAsync(300, CancellationToken.None);

        PageFixtures.AssertJson(
            "true",
            page.Js!.Evaluate("globalThis.__queued_module_completed === true"));
    }

    /// <summary>Rust's `spawn_parser_import_map_server`.</summary>
    private static TestHttpServer SpawnParserImportMapServer() =>
        TestHttpServer.Start(request => request.Path switch
        {
            "/app/before.js" => TestResponse.JavaScript("export const value = 'before-first-module';"),
            "/app/later.js" => TestResponse.JavaScript("export const value = 'later-map';"),
            "/app/async.js" => TestResponse.JavaScript(
                "import('too-late')"
                + ".then(module => globalThis.__async_before_map = module.value)"
                + ".catch(() => globalThis.__async_before_map = 'rejected');"),
            _ => new TestResponse(
                "application/javascript",
                Encoding.UTF8.GetBytes("not found"),
                "404 Not Found"),
        });

    /// <summary>Rust's `spawn_delayed_classic_script_server`.</summary>
    private static TestHttpServer SpawnDelayedClassicScriptServer(int delayMs, string body) =>
        TestHttpServer.Start(_ => TestResponse.JavaScript(body) with { DelayMs = delayMs });

    [Fact]
    public async Task ParserImportMapBeforeFirstModuleControlsResolution()
    {
        using TestHttpServer server = SpawnParserImportMapServer();
        using Page page = PageFixtures.ImportMapTestPage(
            "import-map-order",
            server.Origin,
            """
            <html><head>
            <script type="importmap">{"imports":{"ordered":"./before.js"}}</script>
            <script type="module">
                import { value } from "ordered";
                globalThis.__parser_import_map_value = value;
            </script>
            <script type="importmap">{"imports":{"ordered":"./after.js"}}</script>
            </head><body></body></html>
            """);
        await page.ExecuteScriptsAsync(CancellationToken.None);

        PageFixtures.AssertJson(
            "\"before-first-module\"",
            page.Js!.Evaluate("globalThis.__parser_import_map_value"));
        Assert.Equal("/app/before.js", server.NextPath(RequestTimeout));
    }

    [Fact]
    public async Task LaterImportMapAddsUnrelatedRuleWithoutRebindingResolvedRule()
    {
        using TestHttpServer server = SpawnParserImportMapServer();
        using Page page = PageFixtures.ImportMapTestPage(
            "multiple-import-map-order",
            server.Origin,
            """
            <html><head>
            <script type="importmap">{"imports":{"fixed":"./before.js"}}</script>
            <script type="module">
                import { value } from "fixed";
                globalThis.__first_map_value = value;
            </script>
            <script type="importmap">{"imports":{"fixed":"./after.js","later":"./later.js"}}</script>
            <script type="module">
                import { value as fixed } from "fixed";
                import { value as later } from "later";
                globalThis.__later_map_values = [fixed, later];
            </script>
            </head><body></body></html>
            """);
        await page.ExecuteScriptsAsync(CancellationToken.None);

        PageFixtures.AssertJson(
            "\"before-first-module\"",
            page.Js!.Evaluate("globalThis.__first_map_value"));
        PageFixtures.AssertJson(
            """["before-first-module", "later-map"]""",
            page.Js!.Evaluate("globalThis.__later_map_values"));
        List<string> paths = [server.NextPath(RequestTimeout), server.NextPath(RequestTimeout)];
        Assert.Contains("/app/before.js", paths);
        Assert.Contains("/app/later.js", paths);
        Assert.DoesNotContain("/app/after.js", paths);
    }

    [Fact]
    public async Task ClassicDynamicImportDoesNotSeeALaterParserImportMap()
    {
        using TestHttpServer server = SpawnParserImportMapServer();
        using Page page = PageFixtures.ImportMapTestPage(
            "classic-before-import-map",
            server.Origin,
            """
            <html><head>
            <script>
                import("too-late")
                    .then(() => globalThis.__classic_before_map = "resolved")
                    .catch(() => globalThis.__classic_before_map = "rejected");
            </script>
            <script type="importmap">{"imports":{"too-late":"./later.js"}}</script>
            </head><body></body></html>
            """);
        await page.ExecuteScriptsAsync(CancellationToken.None);
        await page.SettleForDurationAsync(500);
        PageFixtures.AssertJson("\"rejected\"", page.Js!.Evaluate("globalThis.__classic_before_map"));
    }

    [Fact]
    public async Task ReadyAsyncClassicScriptRunsBeforeALaterParserImportMap()
    {
        using TestHttpServer server = SpawnParserImportMapServer();
        using Page page = PageFixtures.ImportMapTestPage(
            "async-classic-before-map",
            server.Origin,
            """
            <html><head>
            <script async src="./async.js"></script>
            <script type="importmap">{"imports":{"too-late":"./later.js"}}</script>
            </head><body></body></html>
            """);
        await page.ExecuteScriptsAsync(CancellationToken.None);
        await page.SettleForDurationAsync(500);
        PageFixtures.AssertJson("\"rejected\"", page.Js!.Evaluate("globalThis.__async_before_map"));
        Assert.Equal("/app/async.js", server.NextPath(RequestTimeout));
        Assert.False(server.TryNextPath(TimeSpan.FromMilliseconds(50), out _));
    }

    // BLOCKED on a bug in Obscura.Js, not on this port: `ObscuraJsRuntime` builds
    // `new ObscuraModuleLoader(baseUrl, proxyUrl)`, which allocates its own
    // `new ImportMap()`, while `op_add_import_map` (the path bootstrap.js takes for a
    // script-inserted `<script type="importmap">`) writes into
    // `ObscuraState.ImportMap`. The two are different objects, so a map registered
    // from page JavaScript is silently dropped. Rust shares one
    // `Rc<RefCell<ImportMap>>` between the op state and the loader
    // (runtime.rs: `let import_map = state.borrow().import_map.clone();`).
    [Fact(Skip = "blocked on Obscura.Js: ObscuraJsRuntime builds the module loader with its own new ImportMap() while op_add_import_map writes into ObscuraState.ImportMap, so a map registered from JavaScript never reaches module resolution. Rust shares one Rc<RefCell<ImportMap>> between the two.")]
    public async Task DynamicallyInsertedImportMapControlsLaterDynamicImport()
    {
        using TestHttpServer server = SpawnParserImportMapServer();
        using Page page = PageFixtures.ImportMapTestPage(
            "dynamic-import-map",
            server.Origin,
            """
            <html><head></head><body>
            <script>
                const map = document.createElement("script");
                map.type = "importmap";
                map.textContent = JSON.stringify({imports:{dynamicName:"./later.js"}});
                document.head.appendChild(map);
                import("dynamicName")
                    .then(module => globalThis.__dynamic_map_value = module.value)
                    .catch(error => globalThis.__dynamic_map_value = error.message);
            </script>
            </body></html>
            """);
        await page.ExecuteScriptsAsync(CancellationToken.None);
        await page.SettleForDurationAsync(500);
        PageFixtures.AssertJson("\"later-map\"", page.Js!.Evaluate("globalThis.__dynamic_map_value"));
        Assert.Equal("/app/later.js", server.NextPath(RequestTimeout));
    }

    [Fact]
    public async Task BodyOnloadContentAttributeReflectsToWindow()
    {
        using Page page = PageFixtures.ImportMapTestPage(
            "body-onload-content-attribute",
            "http://127.0.0.1:9",
            """
            <html><head></head><body onload="
                globalThis.__bodyOnloadCalls++;
                globalThis.__bodyOnloadThisIsWindow = this === window;
                globalThis.__bodyOnloadEventType = event && event.type;
                throw new Error('body onload failure');
            "><script>
                globalThis.__bodyOnloadCalls = 0;
                globalThis.__bodyOnloadThisIsWindow = false;
                globalThis.__bodyOnloadEventType = null;
                globalThis.__bodyOnloadReflectedBeforeLoad =
                    typeof document.body.onload === 'function' &&
                    document.body.onload === window.onload;
                globalThis.__windowLoadListenerCalls = 0;
                window.addEventListener('load', () => __windowLoadListenerCalls++);
            </script></body></html>
            """);

        await page.ExecuteScriptsAsync(CancellationToken.None);

        PageFixtures.AssertJson(
            """[1, true, "load", true, 1]""",
            page.Js!.Evaluate(
                """
                [
                    __bodyOnloadCalls,
                    __bodyOnloadThisIsWindow,
                    __bodyOnloadEventType,
                    __bodyOnloadReflectedBeforeLoad,
                    __windowLoadListenerCalls
                ]
                """));

        PageFixtures.AssertJson(
            "true",
            page.Js!.Evaluate(
                """
                (function() {
                    const fromBody = function fromBody() {};
                    document.body.onload = fromBody;
                    const bodySetsWindow = window.onload === fromBody;
                    const fromWindow = function fromWindow() {};
                    window.onload = fromWindow;
                    const windowSetsBody = document.body.onload === fromWindow;
                    document.body.setAttribute('onload', 'globalThis.__bodyOnloadFromAttribute = true');
                    const attributeReplacesWindow = document.body.onload !== fromWindow
                        && document.body.onload === window.onload;
                    const reflected = window.onload;
                    const detachedBody = document.createElement('body');
                    detachedBody.onload = function detachedBodyOnload() {};
                    const detachedBodyStaysLocal = window.onload === reflected
                        && detachedBody.onload !== window.onload;
                    return bodySetsWindow && windowSetsBody && attributeReplacesWindow
                        && detachedBodyStaysLocal;
                })()
                """));
    }

    [Fact]
    public async Task PreloadDynamicScriptDelaysLoadButNotDomContentLoaded()
    {
        using TestHttpServer server = SpawnDelayedClassicScriptServer(
            150,
            "globalThis.__lifecycleOrder.push('dynamic-exec');");
        using Page page = PageFixtures.ImportMapTestPage(
            "preload-dynamic-lifecycle",
            "http://127.0.0.1:9",
            $$"""
            <html><head></head><body><script>
                globalThis.__lifecycleOrder = [];
                document.addEventListener('DOMContentLoaded', () =>
                    globalThis.__lifecycleOrder.push('dom-content-loaded'));
                window.onload = () =>
                    globalThis.__lifecycleOrder.push('window-onload');
                window.addEventListener('load', () =>
                    globalThis.__lifecycleOrder.push('window-load'));
                const script = document.createElement('script');
                script.src = '{{server.Origin}}/preload-dynamic.js';
                script.onload = () => globalThis.__lifecycleOrder.push('script-load');
                document.head.appendChild(script);
            </script></body></html>
            """);

        await page.ExecuteScriptsAsync(CancellationToken.None);

        Assert.Equal("/preload-dynamic.js", server.NextPath(RequestTimeout));
        PageFixtures.AssertJson(
            """["dom-content-loaded", "dynamic-exec", "script-load", "window-onload", "window-load"]""",
            page.Js!.Evaluate("globalThis.__lifecycleOrder"));
        Assert.Equal(
            1.0,
            PageFixtures.AsDouble(page.Js!.Evaluate(
                "globalThis.__lifecycleOrder.filter(value => value === 'window-onload').length")));
    }

    [Fact]
    public async Task LoadDelayingScriptProgressesThroughContinuouslyReadyTimerWork()
    {
        using TestHttpServer server = SpawnDelayedClassicScriptServer(
            75,
            "globalThis.__fairDynamicRan = true;");
        using Page page = PageFixtures.ImportMapTestPage(
            "load-delayer-scheduler-fairness",
            "http://127.0.0.1:9",
            $$"""
            <html><head></head><body><script>
                globalThis.__schedulerTicks = 0;
                setInterval(() => globalThis.__schedulerTicks++, 0);
                const script = document.createElement('script');
                script.src = '{{server.Origin}}/fair-dynamic.js';
                script.onload = () => globalThis.__fairDynamicLoaded = true;
                document.head.appendChild(script);
            </script></body></html>
            """);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await page.ExecuteScriptsAsync(CancellationToken.None);

        TimeSpan elapsed = started.Elapsed;
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(1500),
            $"continuous ready work must not starve a load-delaying fetch; elapsed={elapsed}");
        Assert.Equal("/fair-dynamic.js", server.NextPath(RequestTimeout));
        PageFixtures.AssertJson(
            "[true, true, true]",
            page.Js!.Evaluate(
                "[globalThis.__fairDynamicRan === true,"
                + " globalThis.__fairDynamicLoaded === true,"
                + " globalThis.__schedulerTicks > 0]"));
    }

    [Fact]
    public async Task LoadDelayingScriptDriverRespectsAbsoluteDeadline()
    {
        using TestHttpServer server = SpawnDelayedClassicScriptServer(
            1_000,
            "globalThis.__lateDynamicRan = true;");
        using Page page = PageFixtures.ImportMapTestPage(
            "load-delayer-deadline",
            "http://127.0.0.1:9",
            "<html><head></head><body></body></html>");
        page.Js!.ExecuteScript(
            "install-load-delayer",
            "globalThis.__documentReadyState__ = 'loading'; "
            + "const script = document.createElement('script'); "
            + $"script.src = '{server.Origin}/slow-dynamic.js'; "
            + "document.head.appendChild(script);");
        Assert.True(page.Js!.HasPendingLoadDelayingScripts());
        var started = System.Diagnostics.Stopwatch.StartNew();
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(125);

        bool completed = await Page.DriveLoadDelayingScriptsAsync(page.Js!, deadline);

        TimeSpan elapsed = started.Elapsed;
        Assert.False(completed, "the delayed resource must exceed the deadline");
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(100) && elapsed < TimeSpan.FromMilliseconds(500),
            $"the driver must honor its absolute wall-clock bound; elapsed={elapsed}");
        Assert.True(page.Js!.HasPendingLoadDelayingScripts());
        Assert.Equal("/slow-dynamic.js", server.NextPath(RequestTimeout));
    }

    /// <summary>
    /// An exception from an async page callback reaches the pump as an event-loop
    /// error, and the pump used to answer it by abandoning every still-pending
    /// load-delaying script. The error is transient: the tick carrying it fails and
    /// the following ticks poll clean, so the pending script was dropped over a
    /// condition that had already cleared, and dropped silently.
    /// </summary>
    [Fact]
    public async Task LoadDelayingScriptDriverSurvivesAPageScriptException()
    {
        using TestHttpServer server = SpawnDelayedClassicScriptServer(
            150,
            "globalThis.__lateDynamicRan = true;");
        using Page page = PageFixtures.ImportMapTestPage(
            "load-delayer-throws",
            "http://127.0.0.1:9",
            "<html><head></head><body></body></html>");
        // The throw is deferred through a timer so it lands on a pump tick. Thrown
        // during the installing script's own microtask drain it would clear the
        // pending-script bookkeeping before the fetch even starts.
        page.Js!.ExecuteScript(
            "install-load-delayer",
            "globalThis.__documentReadyState__ = 'loading'; "
            + "const script = document.createElement('script'); "
            + $"script.src = '{server.Origin}/slow-dynamic.js'; "
            + "document.head.appendChild(script); "
            + "setTimeout(() => { "
            + "    queueMicrotask(() => { throw new Error('page script boom'); }); "
            + "}, 0);");
        Assert.True(page.Js!.HasPendingLoadDelayingScripts());

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        bool completed = await Page.DriveLoadDelayingScriptsAsync(page.Js!, deadline);

        Assert.True(completed, "the throw must not end the pump while a script is still pending");
        Assert.Equal("/slow-dynamic.js", server.NextPath(RequestTimeout));
        Assert.False(page.Js!.HasPendingLoadDelayingScripts());
    }

    [Fact]
    public async Task PostLoadDynamicScriptWaitsOnlyWhenCallerRequestsSettle()
    {
        using TestHttpServer server = SpawnDelayedClassicScriptServer(
            400,
            "globalThis.__postLoadDynamicRan = true;");
        using Page page = PageFixtures.ImportMapTestPage(
            "post-load-dynamic-lifecycle",
            server.Origin,
            $$"""
            <html><body><script>
                window.addEventListener('load', () => {
                    const script = document.createElement('script');
                    script.src = '{{server.Origin}}/post-load.js';
                    document.head.appendChild(script);
                });
            </script></body></html>
            """);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await page.ExecuteScriptsAsync(CancellationToken.None);

        TimeSpan navigationElapsed = started.Elapsed;
        Assert.True(
            navigationElapsed < TimeSpan.FromMilliseconds(300),
            $"post-load enhancement must not extend navigation; elapsed={navigationElapsed}");
        PageFixtures.AssertJson(
            """["complete", false, true, false]""",
            page.Js!.Evaluate(
                "[document.readyState, globalThis.__postLoadDynamicRan === true,"
                + " globalThis.__obscura_hasPendingDynamicScripts(),"
                + " globalThis.__obscura_hasPendingLoadDelayingScripts()]"));

        await page.SettleForDurationAsync(700);

        Assert.Equal("/post-load.js", server.NextPath(RequestTimeout));
        PageFixtures.AssertJson(
            "true",
            page.Js!.Evaluate("globalThis.__postLoadDynamicRan === true"));
    }

    [Fact]
    public async Task TimerHydrationRunsDuringExplicitAdaptiveSettleNotNavigationLoad()
    {
        using Page page = PageFixtures.ImportMapTestPage(
            "timer-hydration-lifecycle",
            "http://example.com",
            """
            <html><body><main id="app">Server shell</main><script>
                window.addEventListener('load', () => {
                    setTimeout(() => {
                        document.getElementById('app').textContent = 'Hydrated app';
                        document.body.setAttribute('data-hydrated', 'true');
                    }, 80);
                });
            </script></body></html>
            """);

        await page.ExecuteScriptsAsync(CancellationToken.None);
        PageFixtures.AssertJson(
            """["complete", null, "Server shell"]""",
            page.Js!.Evaluate(
                "[document.readyState, document.body.getAttribute('data-hydrated'),"
                + " document.getElementById('app').textContent]"));

        await page.SettleAsync(500);
        PageFixtures.AssertJson(
            """["true", "Hydrated app"]""",
            page.Js!.Evaluate(
                "[document.body.getAttribute('data-hydrated'),"
                + " document.getElementById('app').textContent]"));
    }

    // BLOCKED on a limitation of the module port, not on this file: ClearScript
    // resolves `import()` through `DocumentLoader` synchronously while the calling
    // script is still executing, so the lazy graph is fetched inside
    // ExecuteScripts instead of being left as post-load work. deno_core defers a
    // dynamic import to the event loop, which is what makes it post-load in Rust.
    // See "ClearScript cannot tell a static import from a dynamic one" in todo.md.
    [Fact(Skip = "blocked on Obscura.Js: ClearScript resolves a dynamic import() synchronously during script evaluation, so it cannot be post-load work as it is under deno_core")]
    public async Task LazyModuleGraphIsPostLoadWorkUntilCallerSettles()
    {
        using TestHttpServer server = TestHttpServer.Start(request => request.Path switch
        {
            "/app/lazy.js" => TestResponse.JavaScript(
                "import { ready } from './lazy-child.js'; export { ready };"),
            // Cross the lifecycle's 500ms fast-settle floor on a descendant edge; the
            // lazy graph marker must propagate beyond its root for this to stay alive.
            "/app/lazy-child.js" => TestResponse.JavaScript("export const ready = 'lazy-ready';")
                with
            { DelayMs = 700 },
            _ => throw new InvalidOperationException($"unexpected module request: {request.Path}"),
        });

        using Page page = PageFixtures.ImportMapTestPage(
            "lazy-module-readiness",
            server.Origin,
            """
            <html><body><script>
                import("./lazy.js").then(module => {
                    document.body.setAttribute("data-lazy-state", module.ready);
                });
            </script></body></html>
            """);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await page.ExecuteScriptsAsync(CancellationToken.None);

        Assert.True(
            started.Elapsed < TimeSpan.FromMilliseconds(500),
            "dynamic import() must not become an implicit navigation settle");
        PageFixtures.AssertJson(
            "null",
            page.Js!.Evaluate("document.body.getAttribute('data-lazy-state')"));

        await page.SettleForDurationAsync(1_000);

        PageFixtures.AssertJson(
            "\"lazy-ready\"",
            page.Js!.Evaluate("document.body.getAttribute('data-lazy-state')"));
    }

    [Fact]
    public async Task OrdinaryFetchDoesNotExtendDynamicModuleSettle()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using TestHttpServer server = TestHttpServer.Start(request =>
        {
            Assert.Equal("/app/analytics", request.Path);
            accepted.TrySetResult();
            return new TestResponse("application/json", Encoding.UTF8.GetBytes("{}")) with { DelayMs = 2_000 };
        });

        using Page page = PageFixtures.ImportMapTestPage(
            "ordinary-fetch-readiness",
            server.Origin,
            $$"""
            <html><body><script>
                globalThis.__analyticsStarted = true;
                fetch("{{server.Origin}}/app/analytics").catch(error => {
                    globalThis.__analyticsError = error.message;
                });
            </script></body></html>
            """);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await page.ExecuteScriptsAsync(CancellationToken.None);
        TimeSpan elapsed = started.Elapsed;

        Assert.True(
            accepted.Task.Wait(TimeSpan.FromMilliseconds(500)),
            "ordinary fetch fixture must actually start its network request");
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(1_500),
            $"ordinary fetch/XHR must retain the fast settle path; elapsed={elapsed}");
        PageFixtures.AssertJson("true", page.Js!.Evaluate("globalThis.__analyticsStarted"));
    }

    // BLOCKED on a bug in Obscura.Js, not on this port: `ObscuraJsRuntime` builds
    // `new ObscuraModuleLoader(baseUrl, proxyUrl)`, which allocates its own
    // `new ImportMap()`, while `op_add_import_map` (the path bootstrap.js takes for a
    // script-inserted `<script type="importmap">`) writes into
    // `ObscuraState.ImportMap`. The two are different objects, so a map registered
    // from page JavaScript is silently dropped. Rust shares one
    // `Rc<RefCell<ImportMap>>` between the op state and the loader
    // (runtime.rs: `let import_map = state.borrow().import_map.clone();`).
    [Fact(Skip = "blocked on Obscura.Js: ObscuraJsRuntime builds the module loader with its own new ImportMap() while op_add_import_map writes into ObscuraState.ImportMap, so a map registered from JavaScript never reaches module resolution. Rust shares one Rc<RefCell<ImportMap>> between the two.")]
    public async Task DynamicImportMapUsesLiveDocumentBaseAtInsertion()
    {
        using TestHttpServer server = SpawnParserImportMapServer();
        using Page page = PageFixtures.ImportMapTestPage(
            "dynamic-import-map-base",
            server.Origin,
            """
            <html><head><base href="/old/"></head><body>
            <script>
                document.querySelector("base").setAttribute("href", "/app/");
                const map = document.createElement("script");
                map.type = "importmap";
                map.textContent = JSON.stringify({imports:{liveBase:"./later.js"}});
                document.head.appendChild(map);
                import("liveBase")
                    .then(module => globalThis.__dynamic_map_base = module.value)
                    .catch(error => globalThis.__dynamic_map_base = error.message);
            </script>
            </body></html>
            """);
        await page.ExecuteScriptsAsync(CancellationToken.None);
        await page.SettleForDurationAsync(500);
        PageFixtures.AssertJson("\"later-map\"", page.Js!.Evaluate("globalThis.__dynamic_map_base"));
        Assert.Equal("/app/later.js", server.NextPath(RequestTimeout));
    }

    [Fact]
    public async Task LaterBaseElementDoesNotRebaseAnEarlierImportMap()
    {
        using TestHttpServer server = SpawnParserImportMapServer();
        using Page page = PageFixtures.ImportMapTestPage(
            "temporal-import-map-base",
            server.Origin,
            """
            <html><head>
            <script type="importmap">{"imports":{"fixed":"./before.js"}}</script>
            <base href="/assets/">
            <script type="module">
                import { value } from "fixed";
                globalThis.__temporal_base_value = value;
            </script>
            </head><body></body></html>
            """);
        await page.ExecuteScriptsAsync(CancellationToken.None);
        PageFixtures.AssertJson(
            "\"before-first-module\"",
            page.Js!.Evaluate("globalThis.__temporal_base_value"));
        Assert.Equal("/app/before.js", server.NextPath(RequestTimeout));
    }

    [Fact]
    public async Task PageTransportPrefetchesOnceAndCaptureReusesTheBytes()
    {
        using TestHttpServer server = TestHttpServer.Start(_ => TestResponse.Svg(
            """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"><rect width="20" height="10" fill="#f00"/></svg>"""));

        using Page page = PageFixtures.NewPage("render-prefetch");
        page.SetViewport((100.0f, 80.0f));
        string pageUrl = $"{server.Origin}/page";
        string assetNetworkUrl = $"{server.Origin}/asset.svg";
        string assetUrl = $"{assetNetworkUrl}#icon";
        page.Js = PageFixtures.RuntimeFor(
            pageUrl,
            $"""<html><body><img src="{assetUrl}" style="width:20px;height:10px"></body></html>""",
            (100.0f, 80.0f));
        page.Url = UrlRecord.Parse(pageUrl)!;

        Assert.Equal(1, await page.PrepareScreenshotResourcesAsync(1_000));
        PageFixtures.AssertJson(
            $"\"{assetUrl}\"",
            page.Js!.Evaluate("document.querySelector('img').currentSrc"));
        Assert.NotNull(page.Screenshot(page.Viewport));
        Assert.Equal("/asset.svg", server.NextPath(RequestTimeout));
        Assert.False(
            server.TryNextPath(TimeSpan.FromMilliseconds(200), out _),
            "capture must not open a second synchronous renderer request");
    }

    [Fact]
    public async Task RenderResourceDeadlineDoesNotNegativeCacheCancelledRequests()
    {
        using TestHttpServer server = TestHttpServer.Start(_ =>
            TestResponse.Svg("""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"/>""")
                with
            { DelayMs = 100 });

        using Page page = PageFixtures.NewPage("render-deadline");
        page.SetViewport((100.0f, 80.0f));
        string pageUrl = $"{server.Origin}/page";
        string assetUrl = $"{server.Origin}/slow.svg";
        page.Js = PageFixtures.RuntimeFor(
            pageUrl,
            $"""<html><body><img src="{assetUrl}"></body></html>""",
            (100.0f, 80.0f));
        page.Url = UrlRecord.Parse(pageUrl)!;

        Assert.Equal(0, await page.PrepareScreenshotResourcesAsync(5));
        Assert.False(
            page.Js!.RenderResourceIsKnown(assetUrl),
            "a deadline-cancelled request must remain retryable");
    }

    [Fact]
    public async Task NavigationPostScriptWarmupSeedsDynamicImagesAndFonts()
    {
        using TestHttpServer server = TestHttpServer.Start(request => request.Path switch
        {
            "/page" => TestResponse.Html("""
                <!doctype html><html><head></head><body><script>
                    const image = document.createElement('img');
                    image.src = '/dynamic.svg';
                    document.body.appendChild(image);
                    const style = document.createElement('style');
                    style.textContent = "@font-face{font-family:Dynamic;src:url('/dynamic.woff2')}body{font-family:Dynamic}";
                    document.head.appendChild(style);
                </script></body></html>
                """),
            "/dynamic.svg" => TestResponse.Svg(
                """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"><rect width="20" height="10" fill="red"/></svg>"""),
            "/dynamic.woff2" => new TestResponse("font/woff2", Encoding.UTF8.GetBytes("not-a-real-font")),
            _ => TestResponse.Text("not found"),
        });

        using Page page = PageFixtures.NewPage("dynamic-render-warmup");
        await page.NavigateAsync($"{server.Origin}/page");

        Assert.Equal<string>(
            ["/dynamic.svg", "/dynamic.woff2", "/page"],
            server.SortedPaths(3, RequestTimeout));
        Assert.True(page.Js!.RenderResourceIsKnown($"{server.Origin}/dynamic.svg"));
        Assert.True(page.Js!.RenderResourceIsKnown($"{server.Origin}/dynamic.woff2"));
    }

    [Fact]
    public void PageScreenshotUsesTheLiveWindowScrollOffset()
    {
        using var page = new Page("scroll-page", BrowserContext.New("scroll-test"));
        page.SetViewport((100.0f, 80.0f));
        page.Js = PageFixtures.RuntimeFor(
            "https://example.test/scroll",
            """
            <html style="margin:0"><body style="margin:0">
                <div style="height:80px;background:#ff0000"></div>
                <div id="second" style="height:80px;background:#0000ff"></div>
                <div style="position:fixed;left:0;top:0;width:20px;height:20px;background:#00ff00"></div>
            </body></html>
            """,
            (100.0f, 80.0f));
        page.Url = UrlRecord.Parse("https://example.test/scroll")!;

        byte[] before = Assert.IsType<byte[]>(page.Screenshot(page.Viewport));
        Assert.Equal(
            80.0,
            PageFixtures.AsDouble(page.Evaluate(
                "return (document.getElementById('second').scrollIntoView(), window.scrollY)")));
        byte[] after = Assert.IsType<byte[]>(page.Screenshot(page.Viewport));

        Assert.False(
            before.AsSpan().SequenceEqual(after),
            "Page screenshot must paint the scrolled viewport");
        Assert.Equal((0.0f, 80.0f), page.Js!.ScrollOffset);
    }

    [Fact]
    public void TruncateNeverSplitsAMultibyteChar()
    {
        // A caller-supplied expression whose byte 80 lands inside a multi-byte char
        // would make Rust's `&expression[..80]` panic; the helper truncates safely.
        string value = new string('a', 79) + "€tail";
        Assert.Equal(3, Encoding.UTF8.GetByteCount("€"));
        string truncated = PageHelpers.TruncateOnCharBoundary(value, 80);
        Assert.StartsWith(truncated, value, StringComparison.Ordinal);
        Assert.Equal(79, Encoding.UTF8.GetByteCount(truncated));
        Assert.Equal("short", PageHelpers.TruncateOnCharBoundary("short", 80));
    }

    [Fact]
    public void ParseImportUrlExtractsUrlForms()
    {
        foreach ((string source, string expectedUrl) in new[]
        {
            (" url(\"basic.css\")", "basic.css"),
            (" url(basic.css)", "basic.css"),
            (" \"basic.css\"", "basic.css"),
            (" 'theme.css'", "theme.css"),
            (" URL('x.css')", "x.css"),
        })
        {
            Assert.Equal(new StylesheetImport(expectedUrl, null), PageHelpers.ParseImportUrl(source));
        }
    }

    [Fact]
    public void ParseImportUrlPreservesPrintAndColorSchemeMedia()
    {
        Assert.Equal(
            new StylesheetImport("p.css", "print"),
            PageHelpers.ParseImportUrl("url(\"p.css\") print"));
        Assert.Equal(
            new StylesheetImport("d.css", "(prefers-color-scheme: dark)"),
            PageHelpers.ParseImportUrl("url(\"d.css\") (prefers-color-scheme: dark)"));
        Assert.Equal(
            new StylesheetImport("a.css", "print, screen"),
            PageHelpers.ParseImportUrl("url(\"a.css\") print, screen"));
    }

    [Fact]
    public void SplitCssImportsPullsImportsAndStripsThem()
    {
        const string Css = "@import url(\"basic.css\");\nbody { color: red; }";
        (List<StylesheetImport> imports, string stripped) = PageHelpers.SplitCssImports(Css);
        Assert.Equal<StylesheetImport>([new StylesheetImport("basic.css", null)], imports);
        Assert.DoesNotContain("@import", stripped, StringComparison.Ordinal);
        Assert.Contains("body { color: red; }", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitCssImportsLeavesImportFreeCssUntouched()
    {
        const string Css = "body { color: red; }";
        (List<StylesheetImport> imports, string stripped) = PageHelpers.SplitCssImports(Css);
        Assert.Empty(imports);
        Assert.Equal(Css, stripped);
    }

    [Fact]
    public void MaterializedImportGraphRetainsPrintConditionAndImportBase()
    {
        var rootUrl = UrlRecord.Parse("https://example.test/css/root.css")!;
        var printUrl = rootUrl.Join("print/print.css")!;
        Dictionary<string, LoadedStylesheet> sheets = new(StringComparer.Ordinal)
        {
            [rootUrl.Href] = new LoadedStylesheet(
                rootUrl,
                [new StylesheetImport("print/print.css", "print")],
                ".root{color:red}"),
            [printUrl.Href] = new LoadedStylesheet(
                printUrl,
                [],
                ".print{background:url(../mark.svg)}"),
        };
        Dictionary<string, string> aliases = new(StringComparer.Ordinal)
        {
            [rootUrl.Href] = rootUrl.Href,
            [printUrl.Href] = printUrl.Href,
        };
        string materialized = Assert.IsType<string>(
            PageHelpers.MaterializeStylesheetGraph(rootUrl.Href, sheets, aliases, []));

        Assert.StartsWith("@media print {\n", materialized, StringComparison.Ordinal);
        Assert.Contains(
            """.print{background:url("https://example.test/css/mark.svg")}""",
            materialized,
            StringComparison.Ordinal);
        Assert.EndsWith(".root{color:red}", materialized, StringComparison.Ordinal);
    }

    [Fact]
    public void StylesheetAssetUrlsKeepTheImportingSheetsBase()
    {
        var baseUrl = UrlRecord.Parse("https://example.com/css/theme/app.css")!;
        const string Css = """
            .hero { background:url("../img/hero.png") }
            .icon { mask-image:URL('./icons/mark.svg') }
            .data { background:url("data:image/svg+xml,<svg></svg>") }
            .fragment { mask:url(#shape) }
            .copy::before { content:"url(../not-an-asset.png)" }
            /* url(../not-an-asset-either.png) */
            """;
        string rebased = PageHelpers.RebaseCssUrls(Css, baseUrl);

        Assert.Contains("""url("https://example.com/css/img/hero.png")""", rebased, StringComparison.Ordinal);
        Assert.Contains("""url("https://example.com/css/theme/icons/mark.svg")""", rebased, StringComparison.Ordinal);
        Assert.Contains("""url("data:image/svg+xml,<svg></svg>")""", rebased, StringComparison.Ordinal);
        Assert.Contains("url(#shape)", rebased, StringComparison.Ordinal);
        Assert.Contains("content:\"url(../not-an-asset.png)\"", rebased, StringComparison.Ordinal);
        Assert.Contains("/* url(../not-an-asset-either.png) */", rebased, StringComparison.Ordinal);
    }

    [Fact]
    public void StylesheetRelTokenSelectorIncludesPreloadedStylesheets()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <link rel="preload stylesheet" href="app.css">
            <link rel="preload" href="font.woff2">
            """);
        List<NodeId> links = dom.QuerySelectorAll("""link[rel~="stylesheet"]""");
        Assert.Single(links);
        Assert.Equal("app.css", dom.GetNode(links[0])?.GetAttribute("href"));
    }

    [Fact]
    public void MediaGatedStylesheetsAreFetchedButDisabledSheetsAreNot()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <link rel="stylesheet" href="screen.css">
            <link rel="stylesheet" href="async.css" media="print"
                  onload="this.media='all'">
            <link rel="stylesheet" href="dark.css"
                  media="(prefers-color-scheme: dark)">
            <link rel="stylesheet" href="disabled.css" disabled>
            """);

        Assert.Equal<(int, string)>(
            [(0, "screen.css"), (1, "async.css"), (2, "dark.css")],
            PageHelpers.LinkedStylesheetRequests(dom));
    }

    [Fact]
    public void PrintMediaOnloadCanActivateAFetchedStylesheet()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head>
                <link id="async" rel="stylesheet" href="async.css" media="print"
                      onload="this.media='all';this.setAttribute('data-loaded','yes')">
            </head><body></body></html>
            """));
        runtime.RunPageInit();
        runtime.ExecuteScript(
            "<async-sheet>",
            PageHelpers.MaterializeLinkedStylesheetScript(0, ".target{color:red}"));

        var state = runtime.WithDom(dom =>
        {
            NodeId link = dom.QuerySelector("#async")!.Value;
            List<NodeId> styles = dom.QuerySelectorAll("style[data-obscura-external-stylesheets]");
            return (
                dom.GetNode(link)?.GetAttribute("data-loaded"),
                styles.Count == 0 ? null : dom.TextContent(styles[0]));
        });

        Assert.Equal("yes", state.Item1);
        Assert.Equal(".target{color:red}", state.Item2);
    }

    [Fact]
    public void TruePrintStylesheetLoadsAndRemainsMediaGated()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head>
                <link id="print" rel="stylesheet" href="print.css" media="print"
                      onload="this.setAttribute('data-loaded','yes')">
            </head><body></body></html>
            """));
        runtime.RunPageInit();
        runtime.ExecuteScript(
            "<print-sheet>",
            PageHelpers.MaterializeLinkedStylesheetScript(0, "body{display:none}"));

        var state = runtime.WithDom(dom =>
        {
            NodeId link = dom.QuerySelector("#print")!.Value;
            NodeId? style = dom.QuerySelector("style[data-obscura-external-stylesheets]");
            return (
                dom.GetNode(link)?.GetAttribute("data-loaded"),
                style is { } id ? dom.GetNode(id)?.GetAttribute("media") : null);
        });

        Assert.Equal("yes", state.Item1);
        Assert.Equal("print", state.Item2);
    }

    [Fact]
    public void MaterializedLinkedStylesheetsExposeLinkOwnedCssomWithOriginSecurity()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head>
                <link id="same" rel="stylesheet" href="/assets/app.css" title="app">
                <style id="inline">.inline { color: green }</style>
                <link id="cross" rel="stylesheet" href="https://cdn.example.test/theme.css">
            </head><body></body></html>
            """));
        runtime.SetUrl("https://example.test/products/widget");
        runtime.RunPageInit();
        runtime.ExecuteScript(
            "<same-origin-sheet>",
            PageHelpers.MaterializeLinkedStylesheetScript(0, ".app { color: red } .wide { width: 20px }"));
        runtime.ExecuteScript(
            "<cross-origin-sheet>",
            PageHelpers.MaterializeLinkedStylesheetScript(1, ".secret { color: purple }"));

        JsonNode? result = runtime.Evaluate(
            """
            (() => {
                const list = document.styleSheets;
                const same = document.getElementById('same');
                const inline = document.getElementById('inline');
                const cross = document.getElementById('cross');
                const sameSheet = same.sheet;
                const sameRules = sameSheet.cssRules;
                const crossSheet = cross.sheet;
                const security = [];
                for (const operation of [
                    () => crossSheet.cssRules,
                    () => crossSheet.rules,
                    () => crossSheet.insertRule('.leak {}', 0),
                    () => crossSheet.deleteRule(0),
                    () => crossSheet.replaceSync('.leak {}'),
                ]) {
                    try { operation(); security.push('missing'); }
                    catch (error) { security.push(error && error.name); }
                }
                sameSheet.insertRule('.added { height: 9px }', sameRules.length);
                const source = document.querySelector(
                    'style[data-obscura-external-stylesheets]'
                );
                return {
                    stableList: list === document.styleSheets,
                    length: list.length,
                    order: [list[0] === sameSheet, list[1] === inline.sheet,
                            list[2] === crossSheet],
                    sameIdentity: same.sheet === sameSheet,
                    owner: sameSheet.ownerNode === same,
                    href: sameSheet.href,
                    title: sameSheet.title,
                    rulesIdentity: sameSheet.cssRules === sameRules,
                    rules: Array.from(sameRules, rule => rule.selectorText),
                    sourceUpdated: source.textContent.includes('.added'),
                    crossOwner: crossSheet.ownerNode === cross,
                    crossHref: crossSheet.href,
                    bridgeSheetsHidden: same.nextSibling.sheet === null
                        && cross.nextSibling.sheet === null,
                    security,
                };
            })()
            """);

        PageFixtures.AssertJson(
            """
            {
                "stableList": true,
                "length": 3,
                "order": [true, true, true],
                "sameIdentity": true,
                "owner": true,
                "href": "https://example.test/assets/app.css",
                "title": "app",
                "rulesIdentity": true,
                "rules": [".app", ".wide", ".added"],
                "sourceUpdated": true,
                "crossOwner": true,
                "crossHref": "https://cdn.example.test/theme.css",
                "bridgeSheetsHidden": true,
                "security": ["SecurityError", "SecurityError", "SecurityError",
                             "SecurityError", "SecurityError"]
            }
            """,
            result);
    }

    [Fact]
    public void ExternalStylesheetsKeepTheirPositionsBetweenInlineSheets()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml(
            """
            <html><head>
                <link rel="stylesheet" href="first.css">
                <style data-name="inline">.target{height:20px}</style>
                <link rel="preload stylesheet" href="second.css">
            </head><body></body></html>
            """));
        runtime.RunPageInit();
        runtime.ExecuteScript(
            "<first-sheet>",
            PageHelpers.MaterializeLinkedStylesheetScript(0, ".target{height:10px}"));
        runtime.ExecuteScript(
            "<second-sheet>",
            PageHelpers.MaterializeLinkedStylesheetScript(1, ".target{height:30px}"));

        List<string> sheetText = runtime.WithDom(dom =>
            dom.QuerySelectorAll("style").Select(dom.TextContent).ToList())!;
        Assert.Equal<string>(
            [".target{height:10px}", ".target{height:20px}", ".target{height:30px}"],
            sheetText);
    }

    /// <summary>Rust's `client_replacement_page`.</summary>
    private static Page ClientReplacementPage(string name, bool deferred)
    {
        Page page = PageFixtures.NewPage(name);
        string serverContent = string.Concat(Enumerable.Range(0, 45).Select(index =>
            $"<p>server content item {index.ToString(CultureInfo.InvariantCulture)} with enough text</p>"));
        string start = deferred
            ? "window.addEventListener('mount-client', () => setTimeout(mountClient, 0));"
            : "mountClient();";
        string html =
            $$"""
            <!doctype html><html><body><main id="ssr">{{serverContent}}</main><script>
                function mountClient() {
                    document.body.innerHTML = '<button id="client" data-clicks="0">Client view</button>';
                    const button = document.getElementById('client');
                    button.addEventListener('click', () => {
                        button.setAttribute('data-clicks', String(Number(button.getAttribute('data-clicks')) + 1));
                    });
                }
                {{start}}
            </script></body></html>
            """;
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        page.Url = UrlRecord.Parse($"data:text/html;base64,{encoded}")!;
        return page;
    }

    private static void AssertClientReplacementSurvived(Page page) =>
        PageFixtures.AssertJson(
            """
            {
                "staleServerContent": false,
                "clientPresent": true,
                "clientText": "Client view",
                "clicks": "1",
                "bodyElements": 1
            }
            """,
            page.Js!.Evaluate(
                """
                var clientReplacementCheck = true;
                const button = document.getElementById('client');
                if (button) button.dispatchEvent(new Event('click'));
                return {
                    staleServerContent: !!document.getElementById('ssr'),
                    clientPresent: !!button,
                    clientText: button ? button.textContent : null,
                    clicks: button ? button.getAttribute('data-clicks') : null,
                    bodyElements: document.querySelectorAll('body *').length
                };
                """));

    [Fact]
    public async Task ParserScriptBodyReplacementSurvivesNavigation()
    {
        using Page page = ClientReplacementPage("parser-client-replacement", false);
        string target = page.UrlString();

        await page.NavigateAsync(target);

        AssertClientReplacementSurvived(page);
    }

    [Fact]
    public async Task TimerBodyReplacementSurvivesSettle()
    {
        using Page page = ClientReplacementPage("timer-client-replacement", true);
        string target = page.UrlString();
        await page.NavigateAsync(target);

        PageFixtures.AssertJson(
            "true",
            page.Js!.Evaluate(
                "var scheduleClientReplacement = true;"
                + " window.dispatchEvent(new Event('mount-client'));"
                + " return !!document.getElementById('ssr');"));

        await page.SettleAsync(100);

        AssertClientReplacementSurvived(page);
    }

    [Fact]
    public void SettleResourceWarmupUsesOnlyRemainingAbsoluteBudget()
    {
        Assert.Equal(
            750UL,
            PageHelpers.RemainingSettleResourceWarmupMs(1_000, TimeSpan.FromMilliseconds(250), 1_000));
        Assert.Equal(
            100UL,
            PageHelpers.RemainingSettleResourceWarmupMs(1_000, TimeSpan.FromMilliseconds(250), 100));
        Assert.Equal(
            0UL,
            PageHelpers.RemainingSettleResourceWarmupMs(
                1_000,
                TimeSpan.FromMicroseconds(999_500),
                1_000));
        Assert.Equal(
            0UL,
            PageHelpers.RemainingSettleResourceWarmupMs(1_000, TimeSpan.FromMilliseconds(1_001), 1_000));
    }

    [Fact]
    public void UrlMatchesCdpPatternHandlesWildcardsAcrossUrlParts()
    {
        Assert.True(PageHelpers.UrlMatchesCdpPattern(
            "*://*.gstatic.com/*.woff2",
            "https://fonts.gstatic.com/s/inter/v18/UcCO3FwrK3iLTcviYwYZ8UA3.woff2"));
        Assert.True(PageHelpers.UrlMatchesCdpPattern(
            "*://*.google.com/maps/vt/*",
            "https://www.google.com/maps/vt/pb=!1m4!1m3"));
        Assert.True(PageHelpers.UrlMatchesCdpPattern(
            "https://example.com/assets/*",
            "https://example.com/assets/app.js"));
        Assert.False(PageHelpers.UrlMatchesCdpPattern(
            "https://example.com/assets/*",
            "https://cdn.example.com/assets/app.js"));
        Assert.False(PageHelpers.UrlMatchesCdpPattern(
            "*://*.gstatic.com/*.woff2",
            "https://fonts.gstatic.com/s/inter/v18/font.woff"));
    }


    /// <summary>
    /// <c>url_string</c> is the WHATWG serialization, not <see cref="Uri"/>'s.
    /// </summary>
    /// <remarks>
    /// <see cref="Uri.AbsoluteUri"/> percent-encodes <c>&lt;</c>, <c>&gt;</c> and
    /// space in a cannot-be-a-base URL's opaque path, so while <c>Page.Url</c> was a
    /// <see cref="Uri"/> every <c>data:</c> URL reaching the CDP wire read
    /// <c>data:text/html,%3Cb%3Ea%20b%3C/b%3E</c> where the reference engine writes
    /// <c>data:text/html,&lt;b&gt;a b&lt;/b&gt;</c>. That fed
    /// <c>Page.frameNavigated</c>, <c>Page.getFrameTree</c>, DOMSnapshot's
    /// <c>documentURL</c>/<c>baseURL</c>, Runtime origins and
    /// <c>Target.getTargets</c>, and also <c>location.href</c>, because the JS realm
    /// is built with <c>UrlString()</c> as its base.
    /// </remarks>
    [Fact]
    public void UrlStringKeepsTheWhatwgSpellingOfADataUrl()
    {
        const string raw = "data:text/html,<b>a b</b>";
        using Page page = PageFixtures.NewPage("url-string");
        page.Url = UrlRecord.Parse(raw)!;

        Assert.Equal(raw, page.UrlString());
        Assert.Equal("data:text/html,%3Cb%3Ea%20b%3C/b%3E", new Uri(raw).AbsoluteUri);
    }

    /// <summary>
    /// The <c>url</c> crate's component getters, which are not
    /// <see cref="Uri"/>'s: <c>path()</c> excludes the query, and <c>query()</c>
    /// excludes the leading <c>?</c> that <see cref="Uri.Query"/> includes.
    /// </summary>
    [Fact]
    public void PageUrlComponentsFollowTheUrlCrateAndNotSystemUri()
    {
        UrlRecord url = UrlRecord.Parse("https://example.test/submitted?q=1#frag")!;

        Assert.Equal("/submitted", url.Path);
        Assert.Equal("q=1", url.Query);
        Assert.Equal("frag", url.Fragment);
        Assert.Equal("https://example.test", url.AsciiOrigin);
    }

    /// <summary>
    /// <c>Url::clone</c> plus <c>set_fragment(None)</c>: the original keeps its
    /// fragment. Mutating in place made two stylesheet cache keys collide.
    /// </summary>
    [Fact]
    public void WithoutFragmentDoesNotMutateTheOriginal()
    {
        UrlRecord url = UrlRecord.Parse("https://example.test/a.css#frag")!;
        UrlRecord stripped = PageUrl.WithoutFragment(url);

        Assert.Equal("https://example.test/a.css", stripped.Href);
        Assert.Equal("https://example.test/a.css#frag", url.Href);
        UrlRecord noFragment = UrlRecord.Parse("https://example.test/a.css")!;
        Assert.Same(noFragment, PageUrl.WithoutFragment(noFragment));
    }

    /// <summary>
    /// <c>robots_url.set_path("/robots.txt"); set_query(None); set_fragment(None)</c>
    /// on a clone, so the navigation URL survives.
    /// </summary>
    [Fact]
    public void RobotsUrlIsTheOriginPlusRobotsTxt()
    {
        UrlRecord url = UrlRecord.Parse("https://example.test:8443/deep/page?x=1#f")!;

        Assert.Equal("https://example.test:8443/robots.txt", PageUrl.RobotsUrl(url).Href);
        Assert.Equal("https://example.test:8443/deep/page?x=1#f", url.Href);
    }
}
