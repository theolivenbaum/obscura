using System.Diagnostics;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;
using PocketCalculator.Render;
using PocketCalculator.Render.Css;

namespace PocketCalculator.Browser;

public sealed partial class Page
{
    /// <summary>
    /// Render resources the retained document references but the renderer cache does
    /// not know yet, found by scanning the light DOM for <c>&lt;img&gt;</c>,
    /// <c>&lt;video poster&gt;</c>, <c>&lt;style&gt;</c>, fetched stylesheets,
    /// <c>style</c> attributes and <c>&lt;use&gt;</c>. This is only the navigation
    /// warmup: everything layout or paint actually asks for later is reported by the
    /// renderer itself (<see cref="PocketCalculatorJsRuntime.TakeRenderResourceRequests"/>).
    /// Blocked or disallowed URLs come back separately so they can be remembered as
    /// missing. Rust <c>render_resource_candidates</c>.
    /// </summary>
    internal (List<RenderResourceMiss> Loadable, List<RenderResourceMiss> Rejected) RenderResourceCandidates()
    {
        if (Js is not { } js || Url is not { } documentUrl)
        {
            return ([], []);
        }
        UrlRecord baseUrl = ResolveBaseUrl() ?? documentUrl;

        // A BTreeSet in Rust: ordered by (url, profile, is_font) so the request order is
        // deterministic and the truncation always keeps the same set.
        var candidates = new SortedSet<RenderResourceMiss>(Comparer<RenderResourceMiss>.Create(
            static (left, right) =>
            {
                int byUrl = string.CompareOrdinal(left.Url, right.Url);
                if (byUrl != 0)
                {
                    return byUrl;
                }
                int leftProfile = left.Profile is { } lp ? (int)lp + 1 : 0;
                int rightProfile = right.Profile is { } rp ? (int)rp + 1 : 0;
                return leftProfile != rightProfile
                    ? leftProfile.CompareTo(rightProfile)
                    : left.IsFont.CompareTo(right.IsFont);
            }));

        foreach ((string raw, ImageRequestProfile profile) in js.PendingRenderImageUrls())
        {
            if (PageUrl.TryParse(raw) is { } url)
            {
                candidates.Add(new RenderResourceMiss(PageUrl.WithoutFragment(url).Href, profile, false));
            }
        }

        List<string> cssSources = js.WithDom(dom =>
        {
            List<string> sources = [];
            foreach (NodeId id in dom.Descendants(dom.Document))
            {
                Node? node = dom.GetNode(id);
                if (node is null)
                {
                    continue;
                }
                if (node.AsElement() is { } element
                    && string.Equals(element.Name.Local, "style", StringComparison.Ordinal))
                {
                    sources.Add(dom.TextContent(id));
                }
                // A fetched <link> sheet and an @import are held beside their node rather than
                // in a synthetic <style> (see DomTree.ExternalStylesheetCss). Without this the
                // url() references in every linked sheet - backgrounds, masks, @font-face src -
                // would stop being prefetched. DEVIATION from crates/obscura-browser, where the
                // <style> walk above reaches them.
                if (dom.ExternalStylesheetCss(id) is { } externalCss)
                {
                    sources.Add(externalCss);
                }
                if (node.GetAttribute("style") is { } style)
                {
                    sources.Add(style);
                }
                if (node.AsElement() is { } useElement
                    && string.Equals(useElement.Name.Local, "use", StringComparison.Ordinal))
                {
                    string? href = node.GetAttribute("href") ?? node.GetAttribute("xlink:href");
                    if (href is not null)
                    {
                        sources.Add($"url({href})");
                    }
                }
            }
            return sources;
        }) ?? [];

        foreach (string css in cssSources)
        {
            foreach (string raw in PageHelpers.CssResourceUrls(css, baseUrl))
            {
                if (PageUrl.TryParse(raw) is { } url)
                {
                    bool isFont = PageHelpers.RenderResourceType(url) == ResourceType.Font;
                    candidates.Add(new RenderResourceMiss(PageUrl.WithoutFragment(url).Href, null, isFont));
                }
            }
        }

        List<RenderResourceMiss> loadable = [];
        List<RenderResourceMiss> rejected = [];
        int taken = 0;
        foreach (RenderResourceMiss candidate in candidates)
        {
            // The renderer decodes data: URLs inline and never asks the cache for them.
            if (candidate.Url.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            bool known = candidate.Profile is { } profile
                ? js.RenderImageResourceIsKnown(candidate.Url, profile)
                : js.RenderResourceIsKnown(candidate.Url);
            if (known)
            {
                continue;
            }
            // Preserve the historical warmup bound. Anything beyond this batch is
            // reported again by a later cache-only layout.
            if (taken++ >= PageHelpers.MaxStylesheetResources)
            {
                break;
            }
            if (PageHelpers.SubresourceAllowed(documentUrl, candidate.Url) && !ShouldBlockUrl(candidate.Url))
            {
                loadable.Add(candidate);
            }
            else
            {
                rejected.Add(candidate);
            }
        }
        return (loadable, rejected);
    }

    /// <summary>
    /// Abandon every background render-resource load of the current document, so a
    /// response that belongs to a previous document can neither seed the next one's
    /// cache nor be mistaken for its own request of the same URL.
    /// </summary>
    public void RetireRenderResources() => Js?.AbandonRenderResources();

    /// <summary>
    /// Apply every finished background load without waiting and report the responses
    /// as Network events. Returns the number of loads that stored usable bytes.
    /// </summary>
    public int DrainRenderResourceResults()
    {
        if (Js is not { } js)
        {
            return 0;
        }
        int loaded = js.ApplyRenderResourceResults();
        RecordRenderResourceEvents(js);
        return loaded;
    }

    /// <summary>
    /// Apply finished loads and start transport loads for the resources cache-only
    /// layout or paint missed since the last call, and report the Network events.
    /// Returns the number of loads that stored usable bytes.
    /// </summary>
    public int QueuePendingRenderResources()
    {
        if (Js is not { } js)
        {
            return 0;
        }
        int loaded = js.ServiceRenderResources();
        RecordRenderResourceEvents(js);
        return loaded;
    }

    /// <summary>Whether background render-resource loads are still running.</summary>
    public bool HasPendingRenderResources => Js?.HasPendingRenderResources ?? false;

    /// <summary>
    /// Start loads for everything the light-DOM scan finds, plus the renderer's own
    /// misses. Returns the number of loads started.
    /// </summary>
    public int SpawnPendingRenderResources()
    {
        if (Js is not { } js)
        {
            return 0;
        }
        // A runtime attached without InitJs loads through the page transport all the same.
        if (!js.HasPageTransport)
        {
            js.SetHttpClient(HttpClient);
            js.SetCallbacks(_callbacks);
        }
        (List<RenderResourceMiss> loadable, List<RenderResourceMiss> rejected) = RenderResourceCandidates();
        foreach (RenderResourceMiss miss in rejected)
        {
            if (miss.Profile is { } profile)
            {
                js.SeedRenderImageResource(miss.Url, profile, null);
            }
            else
            {
                js.SeedRenderResource(miss.Url, null);
            }
        }
        List<RenderResourceMiss> requests = [.. js.MarkRenderResourcesInFlight(loadable)];
        requests.AddRange(js.TakeRenderResourceRequests());
        return js.StartRenderResourceLoads(requests);
    }

    /// <summary>
    /// Seed the renderer cache through the owning page transport and wait up to
    /// <paramref name="maxMs"/> for the results.
    /// </summary>
    /// <remarks>
    /// This removes serial image/font HTTP from the first screenshot while retaining
    /// cookies, proxy policy, SSRF policy, the tracker blocklist, interception, CORS,
    /// response limits and connection pooling. Loads that miss the deadline keep running
    /// in the background and are applied by a later drain; they are neither cancelled nor
    /// negative-cached. Returns how many resources loaded successfully.
    /// </remarks>
    public async Task<int> PrepareScreenshotResourcesAsync(
        ulong maxMs,
        CancellationToken cancellationToken = default)
    {
        if (maxMs == 0 || Js is not { } js)
        {
            return 0;
        }
        long started = Stopwatch.GetTimestamp();
        SpawnPendingRenderResources();
        int loaded = 0;
        while (ReferenceEquals(Js, js))
        {
            loaded += DrainRenderResourceResults();
            if (!js.HasPendingRenderResources)
            {
                break;
            }
            double remaining = maxMs - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (remaining <= 0
                || !await js.WaitForRenderResourceLoadAsync(TimeSpan.FromMilliseconds(remaining), cancellationToken)
                    .ConfigureAwait(false))
            {
                if (ReferenceEquals(Js, js))
                {
                    loaded += DrainRenderResourceResults();
                }
                break;
            }
        }
        return loaded;
    }

    /// <summary>
    /// Load what the last capture missed, waiting up to <paramref name="maxMs"/>. True
    /// when new bytes arrived, so capturing again shows them.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-browser: upstream reports these misses to the
    /// transport but leaves the capture that found them without the bytes. Chromium
    /// would have loaded them before painting, so the CLI and MCP screenshot paths call
    /// this and capture once more. Before 97ff86d the C# compatibility loader fetched
    /// them synchronously during the capture, so this keeps what those pages showed.
    /// </remarks>
    public async Task<bool> LoadCaptureMissesAsync(ulong maxMs, CancellationToken cancellationToken = default)
    {
        QueuePendingRenderResources();
        return HasPendingRenderResources
            && await PrepareScreenshotResourcesAsync(maxMs, cancellationToken).ConfigureAwait(false) > 0;
    }

    private void RecordRenderResourceEvents(PocketCalculatorJsRuntime js)
    {
        foreach (RenderResourceEvent ev in js.TakeRenderResourceEvents())
        {
            Response response = ev.Response;
            RecordNetworkEventWithBody(
                response.Url.AbsoluteUri,
                "GET",
                ev.IsFont ? "Font" : "Image",
                response.Status,
                response.Headers,
                response.Body,
                base64Encoded: true);
            // These are render resources, discovered from CSS, DOM attributes or layout
            // rather than requested by script: a font is "css", an image "img".
            RecordResourceTiming(
                response.Url.AbsoluteUri,
                ev.IsFont ? "css" : "img",
                response,
                ev.StartedAtUnixMs,
                ev.EndedAtUnixMs);
        }
    }

    /// <summary>
    /// Rasterize the current DOM to PNG bytes at <paramref name="viewport"/> (CSS
    /// pixels). Null if the page has no DOM or the viewport is zero-sized.
    /// </summary>
    public byte[]? Screenshot((float Width, float Height) viewport) =>
        ScreenshotWithAnimationSample(viewport, LiveAnimationSample());

    /// <summary>
    /// Rasterize every CSS animation at one explicit local time.
    /// </summary>
    /// <remarks>
    /// Mirrors Web Animations <c>currentTime</c> and is intended for deterministic
    /// parity capture; ordinary screenshots use each live instance's start epoch.
    /// </remarks>
    public byte[]? ScreenshotAtAnimationTime(
        (float Width, float Height) viewport,
        AnimationSampleTime animationSampleTime) =>
        ScreenshotWithAnimationSample(
            viewport,
            new AnimationSample(animationSampleTime, AnimationSampleMode.LocalOverride));

    public byte[]? ScreenshotWithAnimationSample(
        (float Width, float Height) viewport,
        AnimationSample animationSample)
    {
        // Needed to resolve the relative image URLs ("logo.svg") that make up the
        // overwhelming majority of real markup.
        string? baseUrl = ResolveBaseUrl()?.Href;
        if (Js is { } js)
        {
            if (!js.SetAnimationSample(animationSample))
            {
                return null;
            }
            if (js.ScreenshotPreparedWithSurfaceColor(viewport, baseUrl, CaptureSurfaceColor()) is { } png)
            {
                return png;
            }
        }
        // Compatibility path for a page without a JS runtime, or an ad-hoc
        // viewport/base that does not match the runtime's CSSOM render key.
        (float X, float Y) scroll = Js?.ScrollOffset ?? (0.0f, 0.0f);
        // When there IS a runtime, paint against the resource cache it already holds
        // for this document. Building a fresh one refetched every image on every
        // capture, so a caller repeating a screenshot at a viewport that does not
        // match the prepared key paid the whole network cost per frame.
        if (Js is { } retained
            && retained.ScreenshotUnpreparedWithRetainedResources(
                viewport,
                baseUrl,
                scroll,
                animationSample.Time,
                CaptureSurfaceColor()) is { } fallback)
        {
            return fallback;
        }
        return WithDom(dom => RenderPaint.ScreenshotPngScrolledAtAnimationTimeWithSurfaceColor(
            dom,
            viewport,
            baseUrl,
            scroll,
            animationSample.Time,
            CaptureSurfaceColor()));
    }

    /// <summary>
    /// Rasterize an immutable document-space rectangle from the page's retained
    /// layout.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Screenshot"/> this may address content outside the live
    /// viewport and scale the output without relayout or scripted scroll.
    /// </remarks>
    public (byte[]? Png, CaptureError? Error) ScreenshotRegion(CaptureRegion region) =>
        ScreenshotRegionWithAnimationSample(region, LiveAnimationSample());

    public (byte[]? Png, CaptureError? Error) ScreenshotRegionAtAnimationTime(
        CaptureRegion region,
        AnimationSampleTime animationSampleTime) =>
        ScreenshotRegionWithAnimationSample(
            region,
            new AnimationSample(animationSampleTime, AnimationSampleMode.LocalOverride));

    public (byte[]? Png, CaptureError? Error) ScreenshotRegionWithAnimationSample(
        CaptureRegion region,
        AnimationSample animationSample)
    {
        if (Js is not { } js)
        {
            return (null, CaptureError.PaintFailed);
        }
        if (!js.SetAnimationSample(animationSample))
        {
            return (null, CaptureError.PaintFailed);
        }
        return js.ScreenshotPreparedRegionWithSurfaceColor(region, CaptureSurfaceColor());
    }

    /// <summary>
    /// Scrollable document dimensions from the retained render layout.
    /// </summary>
    /// <remarks>
    /// Unlike DOM properties evaluated in page JavaScript, this cannot be shadowed or
    /// monkey-patched by the document being captured.
    /// </remarks>
    public (float Width, float Height)? PreparedContentSize() =>
        PreparedContentSizeWithAnimationSample(LiveAnimationSample());

    public (float Width, float Height)? PreparedContentSizeAtAnimationTime(
        AnimationSampleTime animationSampleTime) =>
        PreparedContentSizeWithAnimationSample(
            new AnimationSample(animationSampleTime, AnimationSampleMode.LocalOverride));

    public (float Width, float Height)? PreparedContentSizeWithAnimationSample(
        AnimationSample animationSample)
    {
        if (Js is not { } js)
        {
            return null;
        }
        return js.SetAnimationSample(animationSample) ? js.PreparedContentSize() : null;
    }

    public AnimationSample LiveAnimationSample()
    {
        if (Js is { } js)
        {
            return js.LiveAnimationSample;
        }
        double milliseconds = Stopwatch.GetElapsedTime(_documentTimelineOrigin).TotalMilliseconds;
        return AnimationSample.Document((float)Math.Min(milliseconds, float.MaxValue));
    }

    public bool PreparedHasActiveCssAnimations => Js?.PreparedHasActiveCssAnimations ?? false;

    /// <summary>Renderer-owned root scroll offset for document-space capture routing.</summary>
    public (float X, float Y) ScreenshotScrollOffset() => Js?.ScrollOffset ?? (0.0f, 0.0f);
}
