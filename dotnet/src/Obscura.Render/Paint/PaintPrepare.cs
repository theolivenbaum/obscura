// Port of `prepare_dom_..._internal`, `css_animation_is_active`,
// `retained_animation_restyle_mutations`, `paint_prepared_region_with_scroll_policy`,
// `native_raster_scale_supported`, the canvas-background source, and
// `PrintEconomyStyleSnapshot` / `print_economy_color` in crates/obscura-render/src/paint.rs.
using Obscura.Dom;
using Obscura.Render.Css;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>The root/source pair whose background is transferred to the canvas surface.</summary>
internal readonly record struct CanvasBackground(NodeId Root, NodeId Source);

internal static class PaintApi
{
    internal static PreparedRender? PrepareInternal(
        DomTree tree,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache resources,
        IReadOnlyList<DynamicFontFace> dynamicFonts,
        StylesheetCache stylesheetCache,
        RetainedStyleMaps? retained,
        IReadOnlyList<RetainedStyleMutation>? mutations,
        CssMediaType mediaType,
        AnimationSample animationSample,
        AnimationTimelineState animationTimeline)
    {
        if (!float.IsFinite(viewport.Width) || !float.IsFinite(viewport.Height)
            || viewport.Width <= 0f || viewport.Height <= 0f)
        {
            return null;
        }

        // Fetch <img> bytes up front to learn intrinsic sizes for layout. This seeds the same
        // cache the paint pass reads, so each URL is still fetched at most once.
        (Dictionary<NodeId, ReplacedIntrinsic> intrinsic, Dictionary<NodeId, SelectedImage> selectedImages) =
            PaintImages.CollectImageIntrinsics(tree, viewport, baseUrl, resources);

        // Only remembered nodes can need their HTML fallback restored.
        List<NodeId> rememberedContentNodes = [.. resources.ContentImageIntrinsics.Keys];
        Dictionary<NodeId, ReplacedIntrinsic> sourceIntrinsic = [];
        Dictionary<NodeId, SelectedImage> sourceSelectedImages = [];
        foreach (NodeId nid in rememberedContentNodes)
        {
            if (intrinsic.TryGetValue(nid, out ReplacedIntrinsic value))
            {
                sourceIntrinsic[nid] = value;
            }

            if (selectedImages.TryGetValue(nid, out SelectedImage? selected))
            {
                sourceSelectedImages[nid] = selected;
            }
        }

        HashSet<NodeId> seededContentImages =
            resources.SeedContentImageIntrinsics(tree, intrinsic, selectedImages);
        List<WebFont> fonts = PaintFonts.CollectWebFonts(tree, baseUrl, resources, dynamicFonts);

        // Only SVG text needs the page font faces; avoid cloning the database for icons.
        SvgFontDatabase svgFonts = PaintSvg.HasInlineSvgText(tree)
            ? SvgFontDatabase.WithWebFonts(fonts)
            : SvgFontDatabase.Shared;

        DomLayout laid = retained is not null
            ? RenderDom.LayoutDomWithWebFontsAndRetainedStylesWithAnimationState(
                tree,
                viewport,
                intrinsic,
                fonts,
                stylesheetCache,
                retained,
                mutations ?? [],
                animationSample,
                animationTimeline)
            : RenderDom.LayoutDomWithWebFontsAndStylesheetCacheForMediaWithAnimationState(
                tree,
                viewport,
                intrinsic,
                fonts,
                stylesheetCache,
                mediaType,
                animationSample,
                animationTimeline);

        // `content:url(...)` is computed by the author cascade. Pay for a second layout only on
        // pages that actually use a CSS image as replaced content.
        if (PaintImages.CollectContentImageIntrinsics(
                tree,
                laid.Styles,
                baseUrl,
                resources,
                intrinsic,
                selectedImages,
                sourceIntrinsic,
                sourceSelectedImages,
                seededContentImages))
        {
            resources.ContentImageLayoutRetries++;
            laid = RenderDom.LayoutDomWithWebFontsAndStylesheetCacheForMediaWithAnimationState(
                tree,
                viewport,
                intrinsic,
                fonts,
                stylesheetCache,
                mediaType,
                animationSample,
                animationTimeline);
        }

        DerivedLayoutState derived = laid.DerivedLayoutState(tree, viewport);
        float rootFontSize = 16f;
        if (tree.QuerySelector("html") is { } root
            && laid.Styles.TryGetValue(root, out LayoutStyle? rootStyle)
            && rootStyle.FontSize is { } size)
        {
            rootFontSize = size;
        }

        AnimationEffectImpact impact = animationTimeline.ActiveWaapiEffectImpact(animationSample.Time);
        foreach (LayoutStyle style in laid.Styles.Values)
        {
            if (CssAnimationIsActive(style) && style.AnimationEffectImpact > impact)
            {
                impact = style.AnimationEffectImpact;
            }
        }

        return new PreparedRender
        {
            ViewportSize = viewport,
            AnimationSampleValue = animationSample,
            HasActiveWaapiAnimations = animationTimeline.HasActiveWaapi(animationSample.Time),
            ActiveAnimationImpact = impact,
            RootFontSize = rootFontSize,
            BaseUrlValue = baseUrl,
            HasDynamicFonts = dynamicFonts.Count > 0,
            ContentSizeValue = derived.ContentSize,
            ViewportFixed = derived.ViewportFixed,
            Sticky = derived.Sticky,
            ScrollTree = derived.ScrollTree,
            SelectedImages = selectedImages,
            SvgFonts = svgFonts,
            Layout = laid,
        };
    }

    internal static List<RetainedStyleMutation> RetainedAnimationRestyleMutations(
        DomTree tree,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        AnimationTimelineState animationTimeline)
    {
        bool Connected(NodeId node) =>
            tree.GetNode(node) is not null
            && (node == tree.Document || tree.Ancestors(node).Contains(tree.Document));

        HashSet<NodeId> cssNodes = [];
        foreach ((NodeId node, LayoutStyle style) in styles)
        {
            if (style.AnimationName is not null && style.AnimationHasRenderEffect && Connected(node))
            {
                cssNodes.Add(node);
            }
        }

        List<RetainedStyleMutation> mutations =
            [.. cssNodes.Select(node => (RetainedStyleMutation)new RetainedStyleMutation.Animation(node))];
        mutations.AddRange(animationTimeline.WaapiNodes()
            .Where(Connected)
            .Select(node => (RetainedStyleMutation)new RetainedStyleMutation.WaapiAnimation(node)));
        return mutations;
    }

    internal static bool CssAnimationIsActive(LayoutStyle style)
    {
        if (style.AnimationName is null
            || !style.AnimationHasRenderEffect
            || style.AnimationTiming.PlayState == AnimationPlayState.Paused
            || style.AnimationTiming.DurationMs <= 0f
            || style.AnimationTiming.IterationCount <= 0f)
        {
            return false;
        }

        float end = style.AnimationTiming.DelayMs
            + (style.AnimationTiming.DurationMs * style.AnimationTiming.IterationCount);
        return float.IsInfinity(end) || style.AnimationLocalTimeMs < F32.Max(end, 0f);
    }

    internal static (Pixmap? Pixmap, CaptureError? Error) PaintPreparedRegionWithScrollPolicy(
        DomTree tree,
        PreparedRender prepared,
        RenderResourceCache resources,
        ResolvedScrollState scroll,
        CaptureRegion region,
        bool printEconomy,
        RgbaColor surfaceColor,
        ICanvasSurfaceSource canvasSurfaces)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(scroll);
        if (CaptureLimits.CheckedCaptureDimensions(region, out var dimensions) is { } error)
        {
            return (null, error);
        }

        (uint nativeWidth, uint nativeHeight, uint outputWidth, uint outputHeight) = dimensions;
        bool scaleMatchesOutput =
            Math.Abs(outputWidth - ((double)region.Width * region.Scale)) <= 1.0
            && Math.Abs(outputHeight - ((double)region.Height * region.Scale)) <= 1.0;
        bool nativeScaled = (outputWidth != nativeWidth || outputHeight != nativeHeight)
            && scaleMatchesOutput
            && NativeRasterScaleSupported(tree, prepared.Layout);
        (uint paintWidth, uint paintHeight, float rasterScale) = nativeScaled
            ? (outputWidth, outputHeight, region.Scale)
            : (nativeWidth, nativeHeight, 1f);

        Pixmap? pixmap = Pixmap.New(paintWidth, paintHeight);
        if (pixmap is null)
        {
            return (null, CaptureError.AllocationLimitExceeded);
        }

        pixmap.Fill(surfaceColor);

        // Resolved node movement already contains `-root_scroll` for ordinary document content
        // and zero root movement for fixed content.
        (float X, float Y) root = scroll.RootOffset();
        (float X, float Y) surfaceOffset = (root.X - region.X, root.Y - region.Y);
        CanvasBackground? canvasBackground = CanvasBackgroundSource(tree, prepared.Layout);
        Pixmap? painted = PaintDomPainter.PaintLaidDomScrolled(new PaintPass
        {
            Tree = tree,
            Viewport = prepared.ViewportSize,
            BaseUrl = prepared.BaseUrlValue,
            Scroll = root,
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
            SurfaceExtent = (region.Width, region.Height),
            SurfaceOffset = surfaceOffset,
            RasterScale = rasterScale,
            PrintEconomy = printEconomy,
            CanvasBackground = canvasBackground,
        });
        if (painted is null)
        {
            return (null, CaptureError.PaintFailed);
        }

        if (nativeScaled || (outputWidth == nativeWidth && outputHeight == nativeHeight))
        {
            return (painted, null);
        }

        // The retained painter rasterizes glyphs and decoded images in CSS-pixel space. Keep
        // scale out of layout and scroll state, then use one high-quality surface transform.
        Pixmap? scaled = PaintResample.Lanczos3(painted, outputWidth, outputHeight);
        return scaled is null ? (null, CaptureError.PaintFailed) : (scaled, null);
    }

    /// <summary>
    /// Whether a retained display list can currently be rasterized directly at a non-1 device
    /// scale.
    /// </summary>
    internal static bool NativeRasterScaleSupported(DomTree tree, DomLayout laid)
    {
        static bool DirectGradientGeometrySupported(LayoutStyle style)
        {
            if (style.BackgroundSize is not null
                || style.BackgroundSizeExpression is not null
                || style.BackgroundSizeFit is not null)
            {
                return false;
            }

            // The direct vector painter is scale-safe only when no logical-pixel tile surface
            // is needed.
            BackgroundGeometry geometry = PaintGradients.BackgroundGeometryFor(
                new Rect(0f, 0f, 100f, 100f),
                style);
            return MathF.Abs(geometry.OriginRect.X - geometry.ClipRect.X) <= 0.01f
                && MathF.Abs(geometry.OriginRect.Y - geometry.ClipRect.Y) <= 0.01f
                && MathF.Abs(geometry.OriginRect.Width - geometry.ClipRect.Width) <= 0.01f
                && MathF.Abs(geometry.OriginRect.Height - geometry.ClipRect.Height) <= 0.01f;
        }

        static bool StyleSupported(LayoutStyle style)
        {
            bool hasGradient = style.BackgroundGradient is not null
                || style.BackgroundRadialGradient is not null
                || style.BackgroundGradientLayers.Count > 0;
            bool gradientsSupported = style.BackgroundConicGradient is null
                && style.BackgroundGradientLayers.All(layer => layer switch
                {
                    BackgroundGradientLayer.Linear linear => !linear.Repeating,
                    BackgroundGradientLayer.Radial => true,
                    _ => false,
                })
                && (!hasGradient || DirectGradientGeometrySupported(style));
            bool simple = !PaintDomPainter.HasAuthoredTransform(style)
                && (style.Opacity is not { } opacity || opacity >= 1f)
                && style.ClipPath is null
                && style.BackgroundImage is null
                && style.MaskImage is null
                && gradientsSupported
                && !style.BackgroundClipText
                && style.BoxShadow is null
                && style.ContentImage is null
                && style.TextOverflow == TextOverflow.Clip;
            return simple
                && (style.BeforePseudo is null || StyleSupported(style.BeforePseudo))
                && (style.AfterPseudo is null || StyleSupported(style.AfterPseudo));
        }

        if (laid.ClipRects.Values.Any(clip => clip is not null)
            || laid.Styles.Values.Any(style => !StyleSupported(style)))
        {
            return false;
        }

        return !tree.Descendants(tree.Document).Any(id =>
            tree.GetNode(id)?.AsElement() is { } element
            && element.Name.Local is "img" or "picture" or "svg" or "canvas" or "video");
    }

    internal static bool StyleHasCanvasBackground(LayoutStyle style) =>
        (style.BackgroundColor is { } color && color.A != 0)
        || style.BackgroundImage is not null
        || style.BackgroundGradient is not null
        || style.BackgroundRadialGradient is not null
        || style.BackgroundConicGradient is not null
        || style.BackgroundGradientLayers.Count > 0;

    internal static CanvasBackground? CanvasBackgroundSource(DomTree tree, DomLayout laid)
    {
        if (tree.QuerySelector("html") is not { } root
            || !laid.Styles.TryGetValue(root, out LayoutStyle? rootStyle))
        {
            return null;
        }

        bool rootIsContained =
            (rootStyle.ContainingBlockTriggers & ContainingBlockTrigger.Contain) != 0;
        if (rootIsContained || StyleHasCanvasBackground(rootStyle))
        {
            return new CanvasBackground(root, root);
        }

        NodeId? body = tree.QuerySelector("body");
        NodeId source = root;
        if (body is { } bodyId
            && laid.Styles.TryGetValue(bodyId, out LayoutStyle? bodyStyle)
            && (bodyStyle.ContainingBlockTriggers & ContainingBlockTrigger.Contain) == 0)
        {
            source = bodyId;
        }

        return new CanvasBackground(root, source);
    }
}

/// <summary>Backup of every authored background/foreground a print-economy capture removes.</summary>
internal sealed class PrintEconomyStyleSnapshot
{
    private RgbaColor? _backgroundColor;
    private (float Angle, List<GradientStop> Stops)? _backgroundGradient;
    private ((float X, float Y) Center, List<GradientStop> Stops)? _backgroundRadialGradient;
    private (float Angle, (float X, float Y) Center, List<GradientStop> Stops)? _backgroundConicGradient;
    private List<BackgroundGradientLayer> _backgroundGradientLayers = [];
    private string? _backgroundImage;
    private RgbaColor? _color;
    private PrintEconomyStyleSnapshot? _beforePseudo;
    private PrintEconomyStyleSnapshot? _afterPseudo;

    internal static PrintEconomyStyleSnapshot Apply(LayoutStyle style)
    {
        PrintEconomyStyleSnapshot snapshot = new()
        {
            _backgroundColor = style.BackgroundColor,
            _backgroundGradient = style.BackgroundGradient,
            _backgroundRadialGradient = style.BackgroundRadialGradient,
            _backgroundConicGradient = style.BackgroundConicGradient,
            _backgroundGradientLayers = style.BackgroundGradientLayers,
            _backgroundImage = style.BackgroundImage,
            _color = style.Color,
        };
        style.BackgroundColor = null;
        style.BackgroundGradient = null;
        style.BackgroundRadialGradient = null;
        style.BackgroundConicGradient = null;
        style.BackgroundGradientLayers = [];
        style.BackgroundImage = null;

        bool hadBackground = (snapshot._backgroundColor is { } color && color.A != 0)
            || snapshot._backgroundGradient is not null
            || snapshot._backgroundRadialGradient is not null
            || snapshot._backgroundConicGradient is not null
            || snapshot._backgroundGradientLayers.Count > 0
            || snapshot._backgroundImage is not null;
        style.BackgroundColor = hadBackground ? new RgbaColor(255, 255, 255, 255) : null;
        style.Color = snapshot._color is { } foreground
            ? RenderPaint.PrintEconomyColor(foreground)
            : null;
        snapshot._beforePseudo = style.BeforePseudo is { } before ? Apply(before) : null;
        snapshot._afterPseudo = style.AfterPseudo is { } after ? Apply(after) : null;
        return snapshot;
    }

    internal void Restore(LayoutStyle style)
    {
        style.BackgroundColor = _backgroundColor;
        style.BackgroundGradient = _backgroundGradient;
        style.BackgroundRadialGradient = _backgroundRadialGradient;
        style.BackgroundConicGradient = _backgroundConicGradient;
        style.BackgroundGradientLayers = _backgroundGradientLayers;
        style.BackgroundImage = _backgroundImage;
        style.Color = _color;
        if (_beforePseudo is { } before && style.BeforePseudo is { } beforeStyle)
        {
            before.Restore(beforeStyle);
        }

        if (_afterPseudo is { } after && style.AfterPseudo is { } afterStyle)
        {
            after.Restore(afterStyle);
        }
    }
}

public static partial class RenderPaint
{
    /// <summary>
    /// Blink's print-economy foreground correction: colors too close to white move one third
    /// down the HSV value axis while preserving hue and alpha.
    /// </summary>
    public static RgbaColor PrintEconomyColor(RgbaColor color)
    {
        const int MinDifferenceSquared = 65_025;
        int Difference(byte target)
        {
            int dr = color.R - target;
            int dg = color.G - target;
            int db = color.B - target;
            return (dr * dr) + (dg * dg) + (db * db);
        }

        if (Difference(255) > MinDifferenceSquared)
        {
            return color;
        }

        float max = Math.Max(color.R, Math.Max(color.G, color.B)) / 255f;
        if (max <= float.Epsilon)
        {
            return color;
        }

        float scale = F32.Max(max - 0.33f, 0f) / max;
        byte Adjusted(byte component) =>
            (byte)Math.Min((ushort)(component / 255f * scale * 256f), (ushort)255);

        return new RgbaColor(Adjusted(color.R), Adjusted(color.G), Adjusted(color.B), color.A);
    }
}

/// <summary>Lanczos3 resampling, standing in for `image::imageops::resize`.</summary>
internal static class PaintResample
{
    internal static Pixmap? Lanczos3(Pixmap source, uint width, uint height)
    {
        Pixmap? target = Pixmap.New(width, height);
        if (target is null || source.Width == 0 || source.Height == 0)
        {
            return null;
        }

        float scaleX = (float)source.Width / width;
        float scaleY = (float)source.Height / height;
        float supportX = F32.Max(scaleX, 1f) * 3f;
        float supportY = F32.Max(scaleY, 1f) * 3f;
        float ratioX = F32.Max(scaleX, 1f);
        float ratioY = F32.Max(scaleY, 1f);

        // Two-pass separable resample: horizontal into a scratch buffer, then vertical.
        float[] scratch = new float[(int)(width * source.Height * 4)];
        for (int y = 0; y < source.Height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                float center = ((x + 0.5f) * scaleX) - 0.5f;
                int left = (int)MathF.Ceiling(center - supportX);
                int right = (int)MathF.Floor(center + supportX);
                float total = 0f;
                float r = 0f;
                float g = 0f;
                float b = 0f;
                float a = 0f;
                for (int sx = left; sx <= right; sx++)
                {
                    int clamped = Math.Clamp(sx, 0, (int)source.Width - 1);
                    float weight = Lanczos((sx - center) / ratioX);
                    if (weight == 0f)
                    {
                        continue;
                    }

                    PremultipliedColor pixel = source.Pixels[(y * (int)source.Width) + clamped];
                    r += pixel.R * weight;
                    g += pixel.G * weight;
                    b += pixel.B * weight;
                    a += pixel.A * weight;
                    total += weight;
                }

                int index = ((y * (int)width) + (int)x) * 4;
                if (total != 0f)
                {
                    scratch[index] = r / total;
                    scratch[index + 1] = g / total;
                    scratch[index + 2] = b / total;
                    scratch[index + 3] = a / total;
                }
            }
        }

        for (uint y = 0; y < height; y++)
        {
            float center = ((y + 0.5f) * scaleY) - 0.5f;
            int top = (int)MathF.Ceiling(center - supportY);
            int bottom = (int)MathF.Floor(center + supportY);
            for (uint x = 0; x < width; x++)
            {
                float total = 0f;
                float r = 0f;
                float g = 0f;
                float b = 0f;
                float a = 0f;
                for (int sy = top; sy <= bottom; sy++)
                {
                    int clamped = Math.Clamp(sy, 0, (int)source.Height - 1);
                    float weight = Lanczos((sy - center) / ratioY);
                    if (weight == 0f)
                    {
                        continue;
                    }

                    int index = ((clamped * (int)width) + (int)x) * 4;
                    r += scratch[index] * weight;
                    g += scratch[index + 1] * weight;
                    b += scratch[index + 2] * weight;
                    a += scratch[index + 3] * weight;
                    total += weight;
                }

                if (total == 0f)
                {
                    continue;
                }

                target.Pixels[(int)((y * width) + x)] = PremultipliedColor.FromRgba(
                    Clamp255(r / total),
                    Clamp255(g / total),
                    Clamp255(b / total),
                    Clamp255(a / total));
            }
        }

        return target;

        static byte Clamp255(float value) =>
            (byte)Math.Clamp((int)MathF.Round(value, MidpointRounding.AwayFromZero), 0, 255);

        static float Lanczos(float x)
        {
            const float A = 3f;
            if (x == 0f)
            {
                return 1f;
            }

            float abs = MathF.Abs(x);
            if (abs >= A)
            {
                return 0f;
            }

            float pix = MathF.PI * x;
            return A * MathF.Sin(pix) * MathF.Sin(pix / A) / (pix * pix);
        }
    }
}
