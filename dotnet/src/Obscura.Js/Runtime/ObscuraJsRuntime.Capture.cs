using Obscura.Dom;
using Obscura.Js.Ops;
using Obscura.Render;
using Obscura.Render.Css;

namespace Obscura.Js.Runtime;

/// <summary>
/// Adapts the ops layer's live canvas backing stores to the paint layer's
/// surface source. Port of <c>RuntimeCanvasSurfaceSource</c> in runtime.rs.
/// </summary>
internal sealed class RuntimeCanvasSurfaceSource(
    IReadOnlyDictionary<NodeId, CanvasBackingSurface> surfaces) : ICanvasSurfaceSource
{
    public CanvasSurface? Surface(NodeId node) =>
        surfaces.TryGetValue(node, out CanvasBackingSurface? surface)
            ? CanvasSurface.FromRgba8(surface.Width, surface.Height, surface.Pixels.Read())
            : null;
}

/// <summary>
/// The Page/capture boundary: the retained-render screenshot family and the
/// render-resource seeding surface the browser layer drives.
/// </summary>
public sealed partial class ObscuraJsRuntime
{
    private static readonly RgbaColor OpaqueWhite = new(255, 255, 255, 255);

    /// <summary>
    /// Run <paramref name="capture"/> with the renderer's synchronous resource
    /// loader disabled, restoring the previous setting even when the capture
    /// throws. Port of <c>with_sync_render_loading_disabled</c>.
    /// </summary>
    /// <remarks>
    /// A capture must observe the retained page rather than initiate its own
    /// network phase; the browser layer seeds resources ahead of time through
    /// the page transport.
    /// </remarks>
    internal static T WithSyncRenderLoadingDisabled<T>(ObscuraState state, Func<ObscuraState, T> capture)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(capture);
        bool previous = state.RenderResources.SetSyncLoadingEnabled(false);
        try
        {
            return capture(state);
        }
        finally
        {
            state.RenderResources.SetSyncLoadingEnabled(previous);
        }
    }

    /// <summary>
    /// Select the document-timeline instant used by the next render flush.
    /// Returns false for invalid times and preserves the current sample.
    /// </summary>
    public bool SetAnimationSampleTime(AnimationSampleTime sample) =>
        SetAnimationSample(AnimationSample.Document(sample.Milliseconds));

    /// <inheritdoc cref="SetAnimationSampleTime"/>
    public bool SetAnimationSample(AnimationSample sample)
    {
        if (!float.IsFinite(sample.Time.Milliseconds) || sample.Time.Milliseconds < 0.0f)
        {
            return false;
        }

        ObscuraState state = State;
        if (state.AnimationSample != sample)
        {
            bool forwardDocumentSample =
                sample.Mode == AnimationSampleMode.DocumentTime
                && state.AnimationSample.Mode == AnimationSampleMode.DocumentTime
                && sample.Time.Milliseconds > state.AnimationSample.Time.Milliseconds;
            if (forwardDocumentSample
                && state.PendingStyleMutations.Count == 0
                && state.PreparedRender is { } prepared
                && prepared.AdvanceInactiveAnimationSampleTime(sample.Time))
            {
                state.AnimationSample = sample;
                return true;
            }

            state.AnimationSample = sample;
            if (!forwardDocumentSample)
            {
                state.PreparedRender = null;
                state.PendingStyleMutations.Clear();
            }

            state.ResolvedScroll = null;
        }

        return true;
    }

    /// <summary>
    /// Capture the live render viewport from the same prepared layout used by
    /// CSSOM geometry. A mismatched ad-hoc viewport/base returns <c>null</c> so
    /// the browser layer can retain its compatibility one-shot path.
    /// </summary>
    public byte[]? ScreenshotPrepared((float Width, float Height) viewport, string? baseUrl) =>
        ScreenshotPreparedWithSurfaceColor(viewport, baseUrl, OpaqueWhite);

    /// <inheritdoc cref="ScreenshotPrepared"/>
    public byte[]? ScreenshotPreparedWithSurfaceColor(
        (float Width, float Height) viewport,
        string? baseUrl,
        RgbaColor surfaceColor)
    {
        ObscuraState state = State;
        string? effectiveBase = StateHelpers.DocumentBaseUrl(state);
        if (viewport != state.Viewport || !string.Equals(baseUrl, effectiveBase, StringComparison.Ordinal))
        {
            return null;
        }

        return WithSyncRenderLoadingDisabled(state, s =>
        {
            if (!RenderState.EnsureResolvedScroll(s))
            {
                return null;
            }

            if (s.Dom is not { } dom
                || s.PreparedRender is not { } prepared
                || s.ResolvedScroll is not { } resolved)
            {
                return null;
            }

            return RenderPaint.ScreenshotPreparedWithScrollAndSurfaceColorAndCanvasSurfaces(
                dom,
                prepared,
                s.RenderResources,
                resolved.State,
                surfaceColor,
                new RuntimeCanvasSurfaceSource(s.CanvasSurfaces));
        });
    }

    /// <summary>
    /// Paint an unprepared view of the current document against the runtime's
    /// retained resource cache.
    /// </summary>
    /// <remarks>
    /// <c>Page</c> falls back to a raw-DOM paint when a capture's viewport does
    /// not match the prepared render key. Reusing the cache the runtime already
    /// holds for this document keeps the fallback correct while fetching each
    /// resource once.
    /// </remarks>
    public byte[]? ScreenshotUnpreparedWithRetainedResources(
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll,
        AnimationSampleTime animationSampleTime,
        RgbaColor surfaceColor)
    {
        ObscuraState state = State;
        return state.Dom is not { } dom
            ? null
            : RenderPaint.ScreenshotPngScrolledAtAnimationTimeWithSurfaceColorAndResources(
                dom,
                viewport,
                baseUrl,
                scroll,
                animationSampleTime,
                surfaceColor,
                state.RenderResources);
    }

    /// <summary>
    /// Capture a document-space rectangle without changing the live viewport,
    /// root scroll, element scroll offsets, or retained layout. The current
    /// resolved scroll snapshot supplies fixed/sticky and nested-scroll state.
    /// </summary>
    public (byte[]? Png, CaptureError? Error) ScreenshotPreparedRegion(CaptureRegion region) =>
        ScreenshotPreparedRegionWithSurfaceColor(region, OpaqueWhite);

    /// <inheritdoc cref="ScreenshotPreparedRegion"/>
    public (byte[]? Png, CaptureError? Error) ScreenshotPreparedRegionWithSurfaceColor(
        CaptureRegion region,
        RgbaColor surfaceColor) =>
        WithSyncRenderLoadingDisabled(State, state =>
        {
            if (!RenderState.EnsureResolvedScroll(state)
                || state.Dom is not { } dom
                || state.PreparedRender is not { } prepared
                || state.ResolvedScroll is not { } resolved)
            {
                return ((byte[]?)null, (CaptureError?)CaptureError.PaintFailed);
            }

            return RenderPaint.ScreenshotPreparedRegionWithScrollAndSurfaceColorAndCanvasSurfaces(
                dom,
                prepared,
                state.RenderResources,
                resolved.State,
                region,
                surfaceColor,
                new RuntimeCanvasSurfaceSource(state.CanvasSurfaces));
        });

    /// <summary>
    /// Capture a document-space rectangle with the PDF print-background policy
    /// without mutating the page DOM or retained geometry.
    /// </summary>
    public (byte[]? Png, CaptureError? Error) ScreenshotPreparedRegionWithBackgrounds(
        CaptureRegion region,
        bool paintBackgrounds) =>
        WithSyncRenderLoadingDisabled(State, state =>
        {
            if (!RenderState.EnsureResolvedScroll(state)
                || state.Dom is not { } dom
                || state.PreparedRender is not { } prepared
                || state.ResolvedScroll is not { } resolved)
            {
                return ((byte[]?)null, (CaptureError?)CaptureError.PaintFailed);
            }

            return RenderPaint.ScreenshotPreparedRegionWithScrollAndBackgroundsAndCanvasSurfaces(
                dom,
                prepared,
                state.RenderResources,
                resolved.State,
                region,
                paintBackgrounds,
                new RuntimeCanvasSurfaceSource(state.CanvasSurfaces));
        });

    /// <summary>
    /// Capture one immutable document slice as if its origin were the root
    /// scroll position of a virtual viewport. This leaves the live page scroll
    /// untouched while giving fixed and sticky descendants page-local paint
    /// geometry, which paginated raster PDF export requires.
    /// </summary>
    public (byte[]? Png, CaptureError? Error) ScreenshotPreparedRegionAtScrollWithBackgrounds(
        CaptureRegion region,
        (float X, float Y) rootScroll,
        bool paintBackgrounds) =>
        WithSyncRenderLoadingDisabled(State, state =>
        {
            if (!RenderState.EnsureResolvedScroll(state)
                || state.Dom is not { } dom
                || state.PreparedRender is not { } prepared)
            {
                return ((byte[]?)null, (CaptureError?)CaptureError.PaintFailed);
            }

            ResolvedScrollState scroll = prepared.ResolveScrollStateForViewport(
                dom,
                rootScroll,
                state.ElementScrollOffsets,
                (region.Width, region.Height));

            return RenderPaint.ScreenshotPreparedRegionWithScrollAndBackgroundsAndCanvasSurfaces(
                dom,
                prepared,
                state.RenderResources,
                scroll,
                region,
                paintBackgrounds,
                new RuntimeCanvasSurfaceSource(state.CanvasSurfaces));
        });

    /// <summary>
    /// Return the retained layout's scrollable document size without changing
    /// the live viewport or scroll position. PDF/full-document consumers use
    /// this to paginate document-space captures from the same geometry.
    /// </summary>
    public (float Width, float Height)? PreparedContentSize() =>
        WithSyncRenderLoadingDisabled(State, state =>
            RenderState.EnsureResolvedScroll(state)
                ? state.PreparedRender?.ContentSize()
                : null);

    /// <summary>
    /// Return the exact responsive candidates selected for live <c>&lt;img&gt;</c>
    /// elements and <c>&lt;video poster&gt;</c> resources without loading them.
    /// The browser layer can then fetch them concurrently through the page-owned
    /// transport before synchronous layout or paint observes the cache.
    /// </summary>
    public IReadOnlyList<(string Url, ImageRequestProfile Profile)> PendingRenderImageUrls()
    {
        ObscuraState state = State;
        string? baseUrl = StateHelpers.DocumentBaseUrl(state);
        if (state.Dom is not { } dom)
        {
            return [];
        }

        List<(string Url, ImageRequestProfile Profile)> urls = [];
        foreach (NodeId id in dom.Descendants(dom.Document))
        {
            Node? node = dom.GetNode(id);
            if (node?.AsElement() is not { } element)
            {
                continue;
            }

            (string Url, ImageRequestProfile Profile, bool Known)? candidate = null;
            if (string.Equals(element.Name.Local, "img", StringComparison.Ordinal))
            {
                var metadata = state.RenderResources.CachedImageElementMetadata(
                    dom,
                    id,
                    state.Viewport,
                    baseUrl);
                if (metadata is { } image)
                {
                    ImageRequestProfile profile = node.GetAttribute("crossorigin")?.Trim().ToLowerInvariant() switch
                    {
                        null => ImageRequestProfile.NoCorsInclude,
                        "use-credentials" => ImageRequestProfile.CorsInclude,
                        _ => ImageRequestProfile.CorsSameOrigin,
                    };
                    candidate = (image.Url, profile, image.Known);
                }
            }
            else if (string.Equals(element.Name.Local, "video", StringComparison.Ordinal))
            {
                var metadata = state.RenderResources.CachedVideoPosterMetadata(dom, id, baseUrl);
                if (metadata is { } poster)
                {
                    candidate = (poster.Url, poster.Profile, poster.Known);
                }
            }

            if (candidate is not { } selected)
            {
                continue;
            }

            if (!selected.Known && !selected.Url.StartsWith("data:", StringComparison.Ordinal))
            {
                urls.Add((selected.Url, selected.Profile));
            }
        }

        // Rust sorts the (url, profile) tuples then dedups adjacent duplicates.
        urls.Sort(static (left, right) =>
        {
            int byUrl = string.CompareOrdinal(left.Url, right.Url);
            return byUrl != 0 ? byUrl : left.Profile.CompareTo(right.Profile);
        });

        List<(string Url, ImageRequestProfile Profile)> deduped = [];
        foreach ((string Url, ImageRequestProfile Profile) entry in urls)
        {
            if (deduped.Count == 0 || deduped[^1] != entry)
            {
                deduped.Add(entry);
            }
        }

        return deduped;
    }

    /// <summary>
    /// Insert one page-transport resource outcome into the retained renderer
    /// cache. Successful image/font bytes queue one resource-dependent layout
    /// refresh while preserving computed styles and any DOM damage already
    /// queued. A negative outcome cannot change geometry and preserves the
    /// retained layout/scroll.
    /// </summary>
    public void SeedRenderResource(string url, byte[]? bytes)
    {
        ObscuraState state = State;
        if (bytes is not null)
        {
            state.RenderResources.Seed(url, bytes);
            RenderInvalidation.InvalidateRenderResourceGeometry(state);
        }
        else
        {
            state.RenderResources.SeedMissing(url);
        }
    }

    /// <inheritdoc cref="SeedRenderResource"/>
    public void SeedRenderImageResource(string url, ImageRequestProfile profile, byte[]? bytes)
    {
        ObscuraState state = State;
        if (bytes is not null && RenderPaint.ImageIntrinsicDimensions(bytes) is not null)
        {
            bool needsGeometry = state.PreparedRender is { } prepared && state.Dom is { } dom
                ? prepared.ImageResourceNeedsGeometry(dom, url, profile)
                : true;
            state.RenderResources.SeedImage(url, profile, bytes);
            state.ActivityGeneration = unchecked(state.ActivityGeneration + 1);
            if (needsGeometry)
            {
                RenderInvalidation.InvalidateRenderResourceGeometry(state);
            }
        }
        else
        {
            state.RenderResources.SeedImageMissing(url, profile);
        }
    }

    /// <summary>Whether the retained cache already holds an outcome for this URL.</summary>
    public bool RenderResourceIsKnown(string url) => State.RenderResources.HasLiveOutcome(url);

    /// <inheritdoc cref="RenderResourceIsKnown"/>
    public bool RenderImageResourceIsKnown(string url, ImageRequestProfile profile) =>
        State.RenderResources.HasLiveImageOutcome(url, profile);
}
