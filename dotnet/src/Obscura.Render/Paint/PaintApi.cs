// Port of the public paint/prepare/screenshot entry points of
// crates/obscura-render/src/paint.rs.
using Obscura.Dom;
using Obscura.Render.Css;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>Rasterize the laid-out DOM into a <see cref="Pixmap"/>.</summary>
public static partial class RenderPaint
{
    internal static readonly RgbaColor White = new(255, 255, 255, 255);

    /// <summary>
    /// Render <paramref name="tree"/> at <paramref name="viewport"/> in CSS pixels, or
    /// <c>null</c> if the viewport is zero-sized.
    /// </summary>
    public static Pixmap? PaintDom(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl) =>
        PaintDomScrolled(tree, viewport, baseUrl, (0f, 0f));

    /// <summary>Render the visible viewport after root scrolling.</summary>
    public static Pixmap? PaintDomScrolled(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll) =>
        PaintDomScrolledAtAnimationTime(tree, viewport, baseUrl, scroll, default);

    public static Pixmap? PaintDomScrolledAtAnimationTime(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll,
        AnimationSampleTime animationSampleTime) =>
        PaintDomScrolledAtAnimationTimeWithSurfaceColor(
            tree,
            viewport,
            baseUrl,
            scroll,
            animationSampleTime,
            White);

    public static Pixmap? PaintDomScrolledAtAnimationTimeWithSurfaceColor(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll,
        AnimationSampleTime animationSampleTime,
        RgbaColor surfaceColor)
    {
        RenderResourceCache resources = new();
        return PaintDomScrolledAtAnimationTimeWithSurfaceColorAndResources(
            tree,
            viewport,
            baseUrl,
            scroll,
            animationSampleTime,
            surfaceColor,
            resources);
    }

    /// <summary>
    /// As the surface-color overload, but painting against a caller-owned resource cache.
    /// </summary>
    public static Pixmap? PaintDomScrolledAtAnimationTimeWithSurfaceColorAndResources(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll,
        AnimationSampleTime animationSampleTime,
        RgbaColor surfaceColor,
        RenderResourceCache resources)
    {
        PreparedRender? prepared = PrepareDomAtAnimationTime(
            tree,
            viewport,
            baseUrl,
            resources,
            animationSampleTime);
        return prepared is null
            ? null
            : PaintPreparedWithSurfaceColor(tree, prepared, resources, scroll, surfaceColor);
    }

    /// <summary>
    /// Resolve image candidates and web fonts, then create the single final layout shared by
    /// CSS geometry consumers and repeated paint.
    /// </summary>
    public static PreparedRender? PrepareDom(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources) =>
        PrepareDomAtAnimationTime(tree, viewport, baseUrl, resources, default);

    public static PreparedRender? PrepareDomAtAnimationTime(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        AnimationSampleTime animationSampleTime) =>
        PrepareDomWithDynamicFontsAtAnimationTime(
            tree,
            viewport,
            baseUrl,
            resources,
            [],
            animationSampleTime);

    /// <summary>Prepare a DOM with the document's script-created <c>FontFace</c> registrations.</summary>
    public static PreparedRender? PrepareDomWithDynamicFonts(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts) =>
        PrepareDomWithDynamicFontsAtAnimationTime(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            default);

    public static PreparedRender? PrepareDomWithDynamicFontsAtAnimationTime(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        AnimationSampleTime animationSampleTime)
    {
        StylesheetCache stylesheetCache = new();
        return PrepareDomWithDynamicFontsAndStylesheetCacheAtAnimationTime(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            animationSampleTime);
    }

    /// <summary>
    /// Prepare a DOM while retaining source parsing and selector indexing across relayouts.
    /// </summary>
    public static PreparedRender? PrepareDomWithDynamicFontsAndStylesheetCache(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache) =>
        PrepareDomWithDynamicFontsAndStylesheetCacheAtAnimationTime(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            default);

    public static PreparedRender? PrepareDomWithDynamicFontsAndStylesheetCacheAtAnimationTime(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache,
        AnimationSampleTime animationSampleTime)
    {
        AnimationTimelineState animationTimeline = new();
        return PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            new AnimationSample(animationSampleTime, AnimationSampleMode.DocumentTime),
            animationTimeline);
    }

    public static PreparedRender? PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline) =>
        PrepareDomWithDynamicFontsAndStylesheetCacheForMediaWithAnimationState(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            CssMediaType.Screen,
            animationSample,
            animationTimeline);

    public static PreparedRender? PrepareDomWithDynamicFontsAndStylesheetCacheForMediaWithAnimationState(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache,
        CssMediaType mediaType,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline) =>
        PaintApi.PrepareInternal(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            null,
            null,
            mediaType,
            animationSample,
            animationTimeline);

    /// <summary>
    /// Rebuild geometry while moving clean computed styles out of the previous prepared render.
    /// </summary>
    public static PreparedRender? PrepareDomWithRetainedAttributeStyles(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache,
        PreparedRender previous,
        IReadOnlyList<AttributeStyleMutation> mutations) =>
        PrepareDomWithRetainedStyles(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            previous,
            [.. mutations.Select(RetainedStyleMutation.From)]);

    /// <summary>
    /// Rebuild geometry while retaining clean styles across attribute and tree/text changes.
    /// </summary>
    public static PreparedRender? PrepareDomWithRetainedStyles(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache,
        PreparedRender previous,
        IReadOnlyList<RetainedStyleMutation> mutations) =>
        PrepareDomWithRetainedStylesAtAnimationTime(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            previous,
            mutations,
            default);

    public static PreparedRender? PrepareDomWithRetainedStylesAtAnimationTime(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache,
        PreparedRender previous,
        IReadOnlyList<RetainedStyleMutation> mutations,
        AnimationSampleTime animationSampleTime)
    {
        AnimationTimelineState animationTimeline = new();
        return PrepareDomWithRetainedStylesWithAnimationState(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            previous,
            mutations,
            new AnimationSample(animationSampleTime, AnimationSampleMode.DocumentTime),
            animationTimeline);
    }

    public static PreparedRender? PrepareDomWithRetainedStylesWithAnimationState(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache,
        PreparedRender previous,
        IReadOnlyList<RetainedStyleMutation> mutations,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline)
    {
        ArgumentNullException.ThrowIfNull(previous);
        bool sampleChanged = previous.AnimationSampleValue != animationSample;
        bool forwardDocumentSample = sampleChanged
            && previous.AnimationSampleValue.Mode == AnimationSampleMode.DocumentTime
            && animationSample.Mode == AnimationSampleMode.DocumentTime
            && animationSample.Time.Milliseconds >= previous.AnimationSampleValue.Time.Milliseconds;
        if (sampleChanged && !forwardDocumentSample)
        {
            return PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
                tree,
                viewport,
                baseUrl,
                resources,
                dynamicFonts,
                stylesheetCache,
                animationSample,
                animationTimeline);
        }

        if (forwardDocumentSample
            && previous.ViewportSize == viewport
            && string.Equals(previous.BaseUrlValue, baseUrl, StringComparison.Ordinal)
            && !previous.HasDynamicFonts
            && dynamicFonts.Count == 0
            && mutations.Count == 0
            && previous.TryAdvanceVisualWaapiSample(tree, animationSample, animationTimeline))
        {
            return previous;
        }

        List<RetainedStyleMutation> sampledAnimationMutations = sampleChanged
            ? PaintApi.RetainedAnimationRestyleMutations(tree, previous.Layout.Styles, animationTimeline)
            : [];
        IReadOnlyList<RetainedStyleMutation> effective = sampledAnimationMutations.Count == 0
            ? mutations
            : [.. mutations, .. sampledAnimationMutations];
        RetainedStyleMaps retained = previous.Layout.TakeRetainedStyleMaps();
        return PaintApi.PrepareInternal(
            tree,
            viewport,
            baseUrl,
            resources,
            dynamicFonts,
            stylesheetCache,
            retained,
            effective,
            CssMediaType.Screen,
            animationSample,
            animationTimeline);
    }

    /// <summary>Paint one root-scroll position from an already prepared layout.</summary>
    public static Pixmap? PaintPrepared(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        (float X, float Y) scroll) =>
        PaintPreparedWithSurfaceColor(tree, prepared, resources, scroll, White);

    internal static Pixmap? PaintPreparedWithSurfaceColor(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        (float X, float Y) scroll,
        RgbaColor surfaceColor)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (CaptureLimits.ValidateCaptureRegion(CaptureRegion.New(
                scroll.X,
                scroll.Y,
                prepared.ViewportSize.Width,
                prepared.ViewportSize.Height,
                1f)) is not null)
        {
            return null;
        }

        Pixmap? pixmap = Pixmap.New(
            (uint)prepared.ViewportSize.Width,
            (uint)prepared.ViewportSize.Height);
        if (pixmap is null)
        {
            return null;
        }

        pixmap.Fill(surfaceColor);
        CanvasBackground? canvasBackground = PaintApi.CanvasBackgroundSource(tree, prepared.Layout);
        return PaintDomPainter.PaintLaidDomScrolled(new PaintPass
        {
            Tree = tree,
            Viewport = prepared.ViewportSize,
            BaseUrl = prepared.BaseUrlValue,
            Scroll = scroll,
            ResolvedScroll = null,
            SharedScrollState = null,
            Pixmap = pixmap,
            ImageCache = resources,
            SelectedImages = prepared.SelectedImages,
            CanvasSurfaces = EmptyCanvasSurfaceSource.Instance,
            SvgFonts = prepared.SvgFonts,
            ContentSize = prepared.ContentSizeValue,
            ViewportFixed = prepared.ViewportFixed,
            Sticky = prepared.Sticky,
            ScrollTree = prepared.ScrollTree,
            Laid = prepared.Layout,
            PaintRoot = null,
            SuppressOpacityFor = null,
            SuppressStackingFor = null,
            SuppressTransformFor = null,
            ClipScopeRoot = null,
            SurfaceExtent = null,
            SurfaceOffset = (0f, 0f),
            RasterScale = 1f,
            PrintEconomy = false,
            CanvasBackground = canvasBackground,
        });
    }

    /// <summary>Paint from the same resolved scroll snapshot CSSOM geometry uses.</summary>
    public static Pixmap? PaintPreparedWithScroll(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll) =>
        PaintPreparedWithScrollAndSurfaceColor(tree, prepared, resources, scroll, White);

    public static Pixmap? PaintPreparedWithScrollAndSurfaceColor(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        RgbaColor surfaceColor) =>
        PaintPreparedWithScrollAndSurfaceColorAndCanvasSurfaces(
            tree,
            prepared,
            resources,
            scroll,
            surfaceColor,
            EmptyCanvasSurfaceSource.Instance);

    public static Pixmap? PaintPreparedWithScrollAndSurfaceColorAndCanvasSurfaces(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        RgbaColor surfaceColor,
        ICanvasSurfaceSource canvasSurfaces)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(scroll);
        if (CaptureLimits.ValidateCaptureRegion(CaptureRegion.New(
                scroll.RootOffset().X,
                scroll.RootOffset().Y,
                prepared.ViewportSize.Width,
                prepared.ViewportSize.Height,
                1f)) is not null)
        {
            return null;
        }

        Pixmap? pixmap = Pixmap.New(
            (uint)prepared.ViewportSize.Width,
            (uint)prepared.ViewportSize.Height);
        if (pixmap is null)
        {
            return null;
        }

        pixmap.Fill(surfaceColor);
        CanvasBackground? canvasBackground = PaintApi.CanvasBackgroundSource(tree, prepared.Layout);
        return PaintDomPainter.PaintLaidDomScrolled(new PaintPass
        {
            Tree = tree,
            Viewport = prepared.ViewportSize,
            BaseUrl = prepared.BaseUrlValue,
            Scroll = scroll.RootOffset(),
            ResolvedScroll = scroll,
            SharedScrollState = null,
            Pixmap = pixmap,
            ImageCache = resources,
            SelectedImages = prepared.SelectedImages,
            CanvasSurfaces = canvasSurfaces,
            SvgFonts = prepared.SvgFonts,
            ContentSize = prepared.ContentSizeValue,
            ViewportFixed = prepared.ViewportFixed,
            Sticky = prepared.Sticky,
            ScrollTree = prepared.ScrollTree,
            Laid = prepared.Layout,
            PaintRoot = null,
            SuppressOpacityFor = null,
            SuppressStackingFor = null,
            SuppressTransformFor = null,
            ClipScopeRoot = null,
            SurfaceExtent = null,
            SurfaceOffset = (0f, 0f),
            RasterScale = 1f,
            PrintEconomy = false,
            CanvasBackground = canvasBackground,
        });
    }

    /// <summary>Paint an arbitrary document-space rectangle from one retained layout.</summary>
    public static (Pixmap? Pixmap, CaptureError? Error) PaintPreparedRegionWithScroll(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        CaptureRegion region) =>
        PaintPreparedRegionWithScrollAndSurfaceColor(tree, prepared, resources, scroll, region, White);

    public static (Pixmap? Pixmap, CaptureError? Error) PaintPreparedRegionWithScrollAndSurfaceColor(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        CaptureRegion region,
        RgbaColor surfaceColor) =>
        PaintPreparedRegionWithScrollAndSurfaceColorAndCanvasSurfaces(
            tree,
            prepared,
            resources,
            scroll,
            region,
            surfaceColor,
            EmptyCanvasSurfaceSource.Instance);

    public static (Pixmap? Pixmap, CaptureError? Error)
        PaintPreparedRegionWithScrollAndSurfaceColorAndCanvasSurfaces(
            DomTree tree,
            PreparedRender prepared,
            RenderResourceCache resources,
            ResolvedScrollState scroll,
            CaptureRegion region,
            RgbaColor surfaceColor,
            ICanvasSurfaceSource canvasSurfaces) =>
        PaintApi.PaintPreparedRegionWithScrollPolicy(
            tree,
            prepared,
            resources,
            scroll,
            region,
            printEconomy: false,
            surfaceColor,
            canvasSurfaces);

    /// <summary>Render <paramref name="tree"/> to PNG bytes.</summary>
    public static byte[]? ScreenshotPng(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl) =>
        PaintDom(tree, viewport, baseUrl)?.EncodePng();

    public static byte[]? ScreenshotPngScrolled(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll) =>
        PaintDomScrolled(tree, viewport, baseUrl, scroll)?.EncodePng();

    public static byte[]? ScreenshotPngScrolledAtAnimationTime(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll,
        AnimationSampleTime animationSampleTime) =>
        PaintDomScrolledAtAnimationTime(tree, viewport, baseUrl, scroll, animationSampleTime)?.EncodePng();

    public static byte[]? ScreenshotPngScrolledAtAnimationTimeWithSurfaceColor(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll,
        AnimationSampleTime animationSampleTime,
        RgbaColor surfaceColor) =>
        PaintDomScrolledAtAnimationTimeWithSurfaceColor(
            tree,
            viewport,
            baseUrl,
            scroll,
            animationSampleTime,
            surfaceColor)?.EncodePng();

    public static byte[]? ScreenshotPngScrolledAtAnimationTimeWithSurfaceColorAndResources(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        (float X, float Y) scroll,
        AnimationSampleTime animationSampleTime,
        RgbaColor surfaceColor,
        RenderResourceCache resources) =>
        PaintDomScrolledAtAnimationTimeWithSurfaceColorAndResources(
            tree,
            viewport,
            baseUrl,
            scroll,
            animationSampleTime,
            surfaceColor,
            resources)?.EncodePng();

    public static byte[]? ScreenshotPrepared(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        (float X, float Y) scroll) =>
        PaintPrepared(tree, prepared, resources, scroll)?.EncodePng();

    public static byte[]? ScreenshotPreparedWithScroll(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll) =>
        PaintPreparedWithScroll(tree, prepared, resources, scroll)?.EncodePng();

    public static byte[]? ScreenshotPreparedWithScrollAndSurfaceColor(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        RgbaColor surfaceColor) =>
        PaintPreparedWithScrollAndSurfaceColor(tree, prepared, resources, scroll, surfaceColor)?.EncodePng();

    public static byte[]? ScreenshotPreparedWithScrollAndSurfaceColorAndCanvasSurfaces(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        RgbaColor surfaceColor,
        ICanvasSurfaceSource canvasSurfaces) =>
        PaintPreparedWithScrollAndSurfaceColorAndCanvasSurfaces(
            tree,
            prepared,
            resources,
            scroll,
            surfaceColor,
            canvasSurfaces)?.EncodePng();

    public static (byte[]? Png, CaptureError? Error) ScreenshotPreparedRegionWithScroll(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        CaptureRegion region) =>
        Encode(PaintPreparedRegionWithScroll(tree, prepared, resources, scroll, region));

    public static (byte[]? Png, CaptureError? Error) ScreenshotPreparedRegionWithScrollAndSurfaceColor(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        CaptureRegion region,
        RgbaColor surfaceColor) =>
        Encode(PaintPreparedRegionWithScrollAndSurfaceColor(
            tree,
            prepared,
            resources,
            scroll,
            region,
            surfaceColor));

    public static (byte[]? Png, CaptureError? Error)
        ScreenshotPreparedRegionWithScrollAndSurfaceColorAndCanvasSurfaces(
            DomTree tree,
            PreparedRender prepared,
            RenderResourceCache resources,
            ResolvedScrollState scroll,
            CaptureRegion region,
            RgbaColor surfaceColor,
            ICanvasSurfaceSource canvasSurfaces) =>
        Encode(PaintPreparedRegionWithScrollAndSurfaceColorAndCanvasSurfaces(
            tree,
            prepared,
            resources,
            scroll,
            region,
            surfaceColor,
            canvasSurfaces));

    /// <summary>Capture a retained region using the PDF print-background policy.</summary>
    public static (byte[]? Png, CaptureError? Error) ScreenshotPreparedRegionWithScrollAndBackgrounds(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        CaptureRegion region,
        bool paintBackgrounds) =>
        ScreenshotPreparedRegionWithScrollAndBackgroundsAndCanvasSurfaces(
            tree,
            prepared,
            resources,
            scroll,
            region,
            paintBackgrounds,
            EmptyCanvasSurfaceSource.Instance);

    public static (byte[]? Png, CaptureError? Error)
        ScreenshotPreparedRegionWithScrollAndBackgroundsAndCanvasSurfaces(
            DomTree tree,
            PreparedRender prepared,
            RenderResourceCache resources,
            ResolvedScrollState scroll,
            CaptureRegion region,
            bool paintBackgrounds,
            ICanvasSurfaceSource canvasSurfaces)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (paintBackgrounds)
        {
            return Encode(PaintPreparedRegionWithScrollAndSurfaceColorAndCanvasSurfaces(
                tree,
                prepared,
                resources,
                scroll,
                region,
                White,
                canvasSurfaces));
        }

        List<(NodeId Node, PrintEconomyStyleSnapshot Snapshot)> snapshots =
            [.. prepared.Layout.Styles.Select(pair => (pair.Key, PrintEconomyStyleSnapshot.Apply(pair.Value)))];
        (byte[]? Png, CaptureError? Error) result = Encode(PaintApi.PaintPreparedRegionWithScrollPolicy(
            tree,
            prepared,
            resources,
            scroll,
            region,
            printEconomy: true,
            White,
            canvasSurfaces));
        foreach ((NodeId node, PrintEconomyStyleSnapshot snapshot) in snapshots)
        {
            if (prepared.Layout.Styles.TryGetValue(node, out LayoutStyle? style))
            {
                snapshot.Restore(style);
            }
        }

        return result;
    }

    private static (byte[]? Png, CaptureError? Error) Encode((Pixmap? Pixmap, CaptureError? Error) painted)
    {
        if (painted.Error is { } error)
        {
            return (null, error);
        }

        byte[]? png = painted.Pixmap?.EncodePng();
        return png is null ? (null, CaptureError.EncodeFailed) : (png, null);
    }

    /// <summary>
    /// Inspect already-fetched image bytes without inserting them into a resource cache.
    /// </summary>
    public static (float Width, float Height)? ImageIntrinsicDimensions(byte[] bytes) =>
        PaintResources.ImageMetadataFromBytes(bytes);
}
