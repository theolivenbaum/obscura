using System.Diagnostics;
using System.Text.Json;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Js.Url;
using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// The render-resource transport tests upstream 97ff86d and 99647b4 added to
/// <c>crates/obscura-browser/src/page.rs</c>: layout and paint never fetch by
/// themselves, and what they miss loads through the page transport under the page's
/// blocking, interception and concurrency policy.
/// </summary>
public sealed class RenderResourceTransportTests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private const string RedSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"><rect width="20" height="10" fill="#f00"/></svg>""";

    /// <summary>A page with a transport and arbitrary body markup, wired the way InitJs does it.</summary>
    private static Page PageWithTransportAndBody(
        string id,
        string pageUrl,
        string body,
        (float Width, float Height) viewport)
    {
        Page page = PageFixtures.NewPage(id);
        page.SetViewport(viewport);
        var runtime = new PocketCalculatorJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml($"<html><body>{body}</body></html>"));
        runtime.SetUrl(pageUrl);
        runtime.SetViewport(viewport.Width, viewport.Height);
        runtime.SetHttpClient(page.HttpClient);
        runtime.RunPageInit();
        page.Js = runtime;
        page.Url = UrlRecord.Parse(pageUrl)!;
        return page;
    }

    private static Page PageWithTransportAndImage(string id, string pageUrl, string imageUrl) =>
        PageWithTransportAndBody(id, pageUrl, $"""<img id="i" src="{imageUrl}">""", (100f, 80f));

    private static double? Number(Page page, string expression) =>
        PageFixtures.AsDouble(page.Js!.Evaluate(expression));

    private static byte[] LateFont()
    {
        // DejaVu Sans is much wider than the default Liberation Sans, so a paragraph
        // changes width once the served font applies.
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "src", "PocketCalculator.Render")))
        {
            directory = Path.GetDirectoryName(directory);
        }
        Assert.NotNull(directory);
        return File.ReadAllBytes(Path.Combine(directory, "src", "PocketCalculator.Render", "Assets", "dejavu-sans.ttf"));
    }

    [Fact]
    public void RenderResourceWarmupKeepsTheExistingCandidateBound()
    {
        using Page page = PageFixtures.NewPage("warmup-bound");
        var html = new System.Text.StringBuilder("<html><body>");
        for (int index = 0; index < PageHelpers.MaxStylesheetResources + 4; index++)
        {
            html.Append($"<img src=\"https://assets.test/{index}.png\">");
        }
        html.Append("</body></html>");
        page.Js = PageFixtures.RuntimeFor("https://example.test/page", html.ToString());
        page.Url = UrlRecord.Parse("https://example.test/page")!;

        (List<RenderResourceMiss> loadable, List<RenderResourceMiss> rejected) = page.RenderResourceCandidates();
        Assert.Equal(PageHelpers.MaxStylesheetResources, loadable.Count + rejected.Count);
    }

    [Fact]
    public async Task CacheOnlyLayoutNeverBlocksOnASlowAssetAndLateBytesUpdateGeometry()
    {
        using TestHttpServer server = TestHttpServer.Start(_ => TestResponse.Svg(RedSvg) with { DelayMs = 1_200 });
        string assetUrl = $"{server.Origin}/slow.svg";
        using Page page = PageWithTransportAndImage("cache-only", $"{server.Origin}/page", assetUrl);
        Assert.False(page.Js!.RenderResourceSyncLoadingEnabled, "a page-owned runtime must not own a synchronous loader");

        // Layout answers from placeholder geometry at once instead of fetching on the V8 thread.
        var clock = Stopwatch.StartNew();
        double? width = Number(page, "document.getElementById('i').getBoundingClientRect().width");
        Assert.True(clock.ElapsedMilliseconds < 600, $"layout query took {clock.ElapsedMilliseconds} ms");
        Assert.NotNull(width);
        Assert.NotEqual(20.0, width);
        Assert.Equal(0.0, Number(page, "document.getElementById('i').naturalWidth"));

        // The miss goes to the page transport exactly once.
        page.QueuePendingRenderResources();
        Assert.True(page.HasPendingRenderResources);
        page.QueuePendingRenderResources();
        Assert.Equal("/slow.svg", server.NextPath(RequestTimeout));

        Assert.Equal(1, await page.PrepareScreenshotResourcesAsync(5_000));
        Assert.False(page.HasPendingRenderResources);
        Assert.True(page.Js.RenderImageResourceIsKnown(assetUrl, ImageRequestProfile.NoCorsInclude));
        Assert.Equal(20.0, Number(page, "document.getElementById('i').naturalWidth"));
        Assert.Equal(20.0, Number(page, "document.getElementById('i').getBoundingClientRect().width"));
        Assert.False(
            server.TryNextPath(TimeSpan.FromMilliseconds(300), out _),
            "neither layout nor capture may open a second request");
    }

    private static async Task AssertRenderResourceBlocklist(bool warmup)
    {
        using TestHttpServer server = TestHttpServer.Start(_ => TestResponse.Svg(RedSvg));
        string origin = server.Origin;
        using Page page = PageWithTransportAndBody(
            "blocked",
            $"{origin}/page",
            $"""
            <img id="allowed" src="{origin}/allowed.svg">
            <img src="{origin}/blocked.svg">
            <div style="width:20px;height:20px;background-image:url({origin}/blocked-css.svg)"></div>
            """,
            (400f, 300f));
        page.SetBlockedUrls(["*blocked*"]);

        // Each caller gets a fresh page: a prior miss cached by the other path would
        // hide a missing blocklist check here.
        if (warmup)
        {
            Assert.Equal(1, await page.PrepareScreenshotResourcesAsync(2_000));
        }
        else
        {
            Assert.NotNull(page.Screenshot(page.Viewport));
            page.QueuePendingRenderResources();
            var clock = Stopwatch.StartNew();
            while (page.HasPendingRenderResources && clock.ElapsedMilliseconds < 2_000)
            {
                await Task.Delay(10);
                page.DrainRenderResourceResults();
            }
            Assert.False(page.HasPendingRenderResources, "renderer loads must finish");
        }

        Assert.Equal(20.0, Number(page, "document.getElementById('allowed').naturalWidth"));
        Assert.Equal("/allowed.svg", server.NextPath(RequestTimeout));
        Assert.False(
            server.TryNextPath(TimeSpan.FromMilliseconds(100), out string extra),
            $"only the allowed image may reach the server, also saw {extra}");
    }

    [Fact]
    public Task ScreenshotWarmupHonoursBlockedUrlsOnAFreshPage() => AssertRenderResourceBlocklist(true);

    [Fact]
    public Task RendererMissesHonourBlockedUrlsOnAFreshPage() => AssertRenderResourceBlocklist(false);

    [Fact]
    public async Task RendererMissesCoverScriptFontsAndShadowRootStyles()
    {
        byte[] font = LateFont();
        using TestHttpServer server = TestHttpServer.Start(_ => new TestResponse("font/ttf", font));
        string shadowFont = $"{server.Origin}/shadow.ttf";
        string dynamicFont = $"{server.Origin}/dynamic.ttf";
        using Page page = PageWithTransportAndBody(
            "misses",
            $"{server.Origin}/page",
            """<div id="host"></div><p id="dyn" style="font-family:Dyn">dynamic</p>""",
            (400f, 300f));
        // Neither source is visible to a light-DOM scan: one lives in a shadow root, the
        // other only in the script FontFaceSet.
        page.Js!.Evaluate(
            $$"""
            (function() {
                const root = document.getElementById('host').attachShadow({mode: 'open'});
                root.innerHTML = '<style>@font-face { font-family: Shadow; src: url({{shadowFont}}); }</style><p style="font-family:Shadow">shadow</p>';
                document.fonts.add(new FontFace('Dyn', 'url({{dynamicFont}})'));
                return true;
            })()
            """);
        page.Js.Evaluate("document.getElementById('dyn').getBoundingClientRect().width");
        page.QueuePendingRenderResources();
        Assert.True(page.HasPendingRenderResources, "layout misses must name the fonts");
        await page.PrepareScreenshotResourcesAsync(3_000);
        Assert.False(page.HasPendingRenderResources);
        List<string> paths = [.. server.Requests.Select(request => request.Path)];
        Assert.Contains("/dynamic.ttf", paths);
        Assert.True(page.Js.RenderResourceIsKnown(dynamicFont));
        // The C# cascade may or may not reach a shadow-root @font-face; when it does,
        // the request must go through the transport like the other.
        Assert.Equal(paths.Contains("/shadow.ttf"), page.Js.RenderResourceIsKnown(shadowFont));
    }

    [Fact]
    public async Task LateBytesApplyWhileTheRuntimeWaitsForAPromise()
    {
        using TestHttpServer server = TestHttpServer.Start(_ => TestResponse.Svg(RedSvg) with { DelayMs = 1_200 });
        using Page page = PageWithTransportAndImage("promise-wait", $"{server.Origin}/page", $"{server.Origin}/late.svg");
        page.Js!.Evaluate("document.getElementById('i').getBoundingClientRect().width");
        page.QueuePendingRenderResources();
        Assert.True(page.HasPendingRenderResources);

        // No protocol command and no page pump runs here: only the runtime's own
        // promise wait, as Runtime.evaluate with awaitPromise does.
        var clock = Stopwatch.StartNew();
        bool resolved = await page.Js.ResolvePromisesUntilAsync(
            js => PageFixtures.AsDouble(js.Evaluate("document.getElementById('i').naturalWidth")) == 20.0,
            4_000);
        Assert.True(resolved, "the wait must observe the late bytes");
        Assert.True(clock.ElapsedMilliseconds < 3_000, $"observed after {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A single awaited CDP expression creates the miss (a script-added
    /// <c>@font-face</c>), and nothing but the runtime's own promise wait runs until it
    /// resolves: the load must be started and its bytes applied inside that wait.
    /// </summary>
    [Fact]
    public async Task AMissCreatedInsideAnAwaitedExpressionLoadsDuringTheWait()
    {
        byte[] font = LateFont();
        using TestHttpServer server = TestHttpServer.Start(_ => new TestResponse("font/ttf", font, DelayMs: 1_200));
        string fontUrl = $"{server.Origin}/late.ttf";
        using Page page = PageWithTransportAndBody(
            "await-miss",
            $"{server.Origin}/page",
            """<p><span id="t" style="font-size:20px">MMMMMMMMMMMMMMMMMMMM</span></p>""",
            (400f, 300f));
        string expression =
            $$"""
            (async () => {
                const style = document.createElement('style');
                style.textContent = '@font-face { font-family: Late; src: url({{fontUrl}}); }';
                document.head.appendChild(style);
                const t = document.getElementById('t');
                t.style.fontFamily = 'Late';
                const before = t.getBoundingClientRect().width;
                const started = Date.now();
                while (Date.now() - started < 4000) {
                    await new Promise(resolve => setTimeout(resolve, 20));
                    const now = t.getBoundingClientRect().width;
                    if (now !== before) return [before, now, Date.now() - started];
                }
                return [before, t.getBoundingClientRect().width, -1];
            })()
            """;
        var clock = Stopwatch.StartNew();
        RemoteObjectInfo result = await page.Js!.EvaluateForCdpWithTimeoutAsync(expression, true, true, 5_000);
        long elapsed = clock.ElapsedMilliseconds;
        string json = result.Value?.ToJsonString() ?? "null";
        double[] samples = JsonSerializer.Deserialize<double[]>(json) ?? [];
        Assert.Equal(3, samples.Length);
        Assert.True(samples[2] >= 0, $"the font must apply inside the awaited expression: {json}");
        Assert.True(samples[1] != samples[0] && samples[1] > 0, json);
        Assert.True(elapsed < 3_500, $"resolved after {elapsed} ms");
        Assert.Contains("/late.ttf", server.Requests.Select(request => request.Path));
    }

    /// <summary>
    /// Timer-driven page script inside one fixed-length wait (the CLI <c>--wait</c>
    /// path) observes the bytes that land during the wait.
    /// </summary>
    [Fact]
    public async Task TimersInsideAFixedWaitObserveBytesThatLandDuringIt()
    {
        byte[] font = LateFont();
        using TestHttpServer server = TestHttpServer.Start(_ => new TestResponse("font/ttf", font, DelayMs: 1_200));
        string fontUrl = $"{server.Origin}/late.ttf";
        using Page page = PageWithTransportAndBody(
            "fixed-wait",
            $"{server.Origin}/page",
            $"""
            <style>@font-face {"{"} font-family: Late; src: url({fontUrl}); {"}"}</style>
            <p><span id="t" style="font-family:Late;font-size:20px">MMMMMMMMMMMMMMMMMMMM</span></p>
            """,
            (400f, 300f));
        page.Js!.Evaluate(
            """
            (function() {
                const t = document.getElementById('t');
                window.__samples = [[0, t.getBoundingClientRect().width]];
                const started = Date.now();
                setInterval(() => {
                    window.__samples.push([Date.now() - started, t.getBoundingClientRect().width]);
                }, 20);
                return true;
            })()
            """);
        page.QueuePendingRenderResources();
        Assert.True(page.HasPendingRenderResources, "the font is a layout miss");
        await page.Js.RunEventLoopForDurationAsync(2_200);
        string json = page.Js.Evaluate("JSON.stringify(window.__samples)")!.GetValue<string>();
        double[][] samples = JsonSerializer.Deserialize<double[][]>(json)!;
        Assert.True(samples.Length > 20, $"timers must have run: {json}");
        double first = samples[0][1];
        double[]? changed = samples.FirstOrDefault(sample => sample[1] != first);
        Assert.True(changed is not null, $"a sample inside the wait must show the applied font: {json}");
        Assert.True(changed![0] < 2_100, $"observed only at {changed[0]} ms");
    }

    /// <summary>
    /// Renderer misses follow the page's Fetch.enable interception policy like the
    /// warmup scan: an intercepted URL is not fetched behind the client's back.
    /// </summary>
    [Fact]
    public async Task RendererMissesHonourFetchInterceptionPatterns()
    {
        byte[] font = LateFont();
        using TestHttpServer server = TestHttpServer.Start(_ => new TestResponse("font/ttf", font));
        string intercepted = $"{server.Origin}/intercepted.ttf";
        string plain = $"{server.Origin}/plain.ttf";
        using Page page = PageWithTransportAndBody(
            "interception",
            $"{server.Origin}/page",
            $"""
            <style>@font-face {"{"} font-family: A; src: url({intercepted}); {"}"}
            @font-face {"{"} font-family: B; src: url({plain}); {"}"}</style>
            <p id="a" style="font-family:A">a</p><p id="b" style="font-family:B">b</p>
            """,
            (400f, 300f));
        // What Fetch.enable does: patterns first, then enable.
        page.InterceptBlockPatterns.Add("*intercepted.ttf");
        page.EnableIntercept(true);
        page.Js!.Evaluate(
            "document.getElementById('a').getBoundingClientRect().width + document.getElementById('b').getBoundingClientRect().width");
        page.QueuePendingRenderResources();
        Assert.True(await page.PrepareScreenshotResourcesAsync(3_000) <= 1);
        Assert.False(page.HasPendingRenderResources);
        List<string> paths = [.. server.Requests.Select(request => request.Path)];
        Assert.DoesNotContain("/intercepted.ttf", paths);
        Assert.Contains("/plain.ttf", paths);
        Assert.True(page.Js.RenderResourceIsKnown(intercepted), "intercepted URL is settled as missing");
        Assert.True(page.Js.RenderResourceIsKnown(plain));

        // Disabling interception lifts the rule for later misses.
        page.InterceptBlockPatterns.Clear();
        page.EnableIntercept(false);
        string late = $"{server.Origin}/late-intercepted.ttf";
        page.Js.Evaluate(
            $$"""
            (function() {
                document.fonts.add(new FontFace('C', 'url({{late}})'));
                const a = document.getElementById('a');
                a.style.fontFamily = 'C';
                return a.getBoundingClientRect().width;
            })()
            """);
        page.QueuePendingRenderResources();
        await page.PrepareScreenshotResourcesAsync(3_000);
        Assert.Contains("/late-intercepted.ttf", server.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task RenderResourceLoadsShareOnePageWideConcurrencyLimit()
    {
        int open = 0;
        int peak = 0;
        using TestHttpServer server = TestHttpServer.Start(_ =>
        {
            int now = Interlocked.Increment(ref open);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }
            Thread.Sleep(300);
            Interlocked.Decrement(ref open);
            return TestResponse.Svg(RedSvg);
        });
        using Page page = PageWithTransportAndBody("limit", $"{server.Origin}/page", string.Empty, (400f, 300f));
        // Three groups added one after the other, each queued by its own paint miss
        // while the previous group's loads are still running: all share one limiter.
        for (int group = 0; group < 3; group++)
        {
            var markup = new System.Text.StringBuilder();
            for (int index = 0; index < 14; index++)
            {
                markup.Append(
                    $"<div style=\"width:10px;height:10px;background-image:url({server.Origin}/bg{group}-{index}.svg)\"></div>");
            }
            page.Js!.Evaluate(
                $"document.body.insertAdjacentHTML('beforeend', {JsonSerializer.Serialize(markup.ToString())});");
            page.Screenshot(page.Viewport);
            page.QueuePendingRenderResources();
            Assert.True(page.HasPendingRenderResources, $"group {group} must have started loads of its own");
        }

        Assert.Equal(42, await page.PrepareScreenshotResourcesAsync(8_000));
        Assert.False(page.HasPendingRenderResources);
        Assert.True(peak <= RenderResourceLoads.Concurrency, $"at most {RenderResourceLoads.Concurrency}, observed {peak}");
        Assert.True(peak >= 2, $"loads still run concurrently, observed {peak}");
    }

    [Fact]
    public async Task RetiredDocumentLoadsNeverSeedTheNextDocument()
    {
        // First request: slow, 20x10. Every later request: immediate, 30x10.
        int hits = 0;
        using TestHttpServer server = TestHttpServer.Start(_ =>
        {
            int index = Interlocked.Increment(ref hits) - 1;
            int width = index == 0 ? 20 : 30;
            return TestResponse.Svg(
                    $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="10"><rect width="{width}" height="10" fill="#f00"/></svg>""")
                with
                {
                    DelayMs = index == 0 ? 1_500 : 0,
                    ExtraHeaders = [("Cache-Control", "no-store")],
                };
        });
        string pageUrl = $"{server.Origin}/page";
        string assetUrl = $"{server.Origin}/shared.svg";
        using Page page = PageWithTransportAndImage("retire", pageUrl, assetUrl);
        page.Js!.Evaluate("document.getElementById('i').getBoundingClientRect().width");
        page.QueuePendingRenderResources();
        Assert.True(page.HasPendingRenderResources, "document A requests the asset");
        Assert.Equal("/shared.svg", server.NextPath(RequestTimeout));

        // Document B replaces A before A's slow response arrives.
        page.RetireRenderResources();
        Assert.False(page.HasPendingRenderResources);
        var runtime = new PocketCalculatorJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml($"""<html><body><img id="i" src="{assetUrl}"></body></html>"""));
        runtime.SetUrl(pageUrl);
        runtime.SetViewport(100, 80);
        runtime.SetHttpClient(page.HttpClient);
        runtime.RunPageInit();
        page.Js = runtime;
        page.Js.Evaluate("document.getElementById('i').getBoundingClientRect().width");
        page.QueuePendingRenderResources();
        Assert.True(
            page.HasPendingRenderResources,
            "document B must request the same URL itself despite A's abandoned load");
        Assert.Equal(1, await page.PrepareScreenshotResourcesAsync(3_000));
        Assert.Equal(30.0, Number(page, "document.getElementById('i').naturalWidth"));

        // A's response would land now if its request were still alive; either way it
        // must not replace B's bytes.
        await Task.Delay(1_800);
        page.DrainRenderResourceResults();
        Assert.Equal(30.0, Number(page, "document.getElementById('i').naturalWidth"));
        Assert.Equal(2, Volatile.Read(ref hits));
    }
}
