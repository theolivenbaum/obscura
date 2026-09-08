using System.Diagnostics;
using Obscura.Dom;
using Obscura.Net;
using Obscura.Render;
using Obscura.Render.Css;

namespace Obscura.Browser;

public sealed partial class Page
{
    /// <summary>
    /// Concurrently seed the synchronous renderer cache through the owning page
    /// transport.
    /// </summary>
    /// <remarks>
    /// This removes serial image/font HTTP from the first screenshot while retaining
    /// cookies, proxy policy, interception, CORS, response limits and connection
    /// pooling. Returns how many resources loaded successfully.
    /// </remarks>
    public async Task<int> PrepareScreenshotResourcesAsync(
        ulong maxMs,
        CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        if (maxMs == 0 || Js is not { } js)
        {
            return 0;
        }
        if (Url is not { } documentUrl)
        {
            return 0;
        }
        Uri baseUrl = ResolveBaseUrl() ?? documentUrl;

        // A BTreeMap in Rust: ordered by (url, profile) so the request order is
        // deterministic and the 128-entry truncation always keeps the same set.
        var candidates = new SortedDictionary<(string Url, int Profile), ResourceType>(
            Comparer<(string Url, int Profile)>.Create((left, right) =>
            {
                int byUrl = string.CompareOrdinal(left.Url, right.Url);
                return byUrl != 0 ? byUrl : left.Profile.CompareTo(right.Profile);
            }));

        foreach ((string raw, ImageRequestProfile profile) in js.PendingRenderImageUrls())
        {
            if (PageUrl.TryParse(raw) is { } url)
            {
                candidates[(PageUrl.WithoutFragment(url).AbsoluteUri, (int)profile + 1)] =
                    ResourceType.Image;
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
                    ResourceType kind = PageHelpers.RenderResourceType(url);
                    candidates[(PageUrl.WithoutFragment(url).AbsoluteUri, 0)] = kind;
                }
            }
        }

        foreach ((string Url, int Profile) key in candidates.Keys.ToList())
        {
            bool known = key.Profile == 0
                ? js.RenderResourceIsKnown(key.Url)
                : js.RenderImageResourceIsKnown(key.Url, (ImageRequestProfile)(key.Profile - 1));
            if (known)
            {
                candidates.Remove(key);
            }
        }

        foreach ((string Url, int Profile) key in candidates.Keys.ToList())
        {
            if (!PageHelpers.SubresourceAllowed(documentUrl, key.Url) || ShouldBlockUrl(key.Url))
            {
                candidates.Remove(key);
            }
        }

        List<((string Url, int Profile) Key, ResourceType Kind)> requested =
            [.. candidates.Select(entry => (entry.Key, entry.Value)).Take(128)];
        if (requested.Count == 0)
        {
            return 0;
        }

        var factories =
            new List<Func<Task<(string Raw, int Profile, ResourceType Kind, Response? Response)>>>(requested.Count);
        foreach (((string url, int profile) key, ResourceType kind) in requested)
        {
            factories.Add(async () =>
            {
                Uri parsed = PageUrl.TryParse(key.url)!;
                ResourceRequest request = ResourceRequest.Subresource(kind, documentUrl);
                switch (key.profile)
                {
                    case (int)ImageRequestProfile.CorsSameOrigin + 1:
                        request.Mode = RequestMode.Cors;
                        request.Credentials = RequestCredentials.SameOrigin;
                        break;
                    case (int)ImageRequestProfile.CorsInclude + 1:
                        request.Mode = RequestMode.Cors;
                        request.Credentials = RequestCredentials.Include;
                        break;
                    default:
                        break;
                }
                try
                {
                    Response response = await HttpClient
                        .FetchResourceWithCallbacksAsync(parsed, request, _callbacks, cancellationToken)
                        .ConfigureAwait(false);
                    return (key.url, key.profile, kind, (Response?)response);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return (key.url, key.profile, kind, null);
                }
            });
        }

        int loaded = 0;
        // A deadline abandons unfinished work without negative-caching it, so a later
        // warmup can retry slow resources.
        await Buffered.UnorderedUntilAsync(
            factories,
            16,
            DateTime.UtcNow.AddMilliseconds(maxMs),
            result =>
            {
                byte[]? outcome = null;
                if (result.Response is { } response)
                {
                    RecordNetworkEventWithBody(
                        response.Url.AbsoluteUri,
                        "GET",
                        result.Kind == ResourceType.Font ? "Font" : "Image",
                        response.Status,
                        response.Headers,
                        response.Body,
                        base64Encoded: true);
                    if (response.Status is >= 200 and < 300)
                    {
                        loaded += 1;
                        outcome = response.Body;
                    }
                }
                if (Js is { } runtime)
                {
                    if (result.Profile == 0)
                    {
                        runtime.SeedRenderResource(result.Raw, outcome);
                    }
                    else
                    {
                        runtime.SeedRenderImageResource(
                            result.Raw,
                            (ImageRequestProfile)(result.Profile - 1),
                            outcome);
                    }
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

        _ = started;
        return loaded;
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
        string? baseUrl = ResolveBaseUrl()?.AbsoluteUri;
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
