// Port of `paint_laid_dom_scrolled`, `ScrollPaintState`, and their helpers in
// crates/obscura-render/src/paint.rs.
using Obscura.Dom;
using SkiaSharp;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>One invocation of the recursive display-list builder.</summary>
internal sealed class PaintPass
{
    internal required DomTree Tree { get; init; }

    internal required (float Width, float Height) Viewport { get; init; }

    internal required string? BaseUrl { get; init; }

    internal required (float X, float Y) Scroll { get; init; }

    internal required ResolvedScrollState? ResolvedScroll { get; init; }

    internal required ScrollPaintState? SharedScrollState { get; init; }

    internal required Pixmap Pixmap { get; set; }

    internal required RenderResourceCache ImageCache { get; init; }

    internal required IReadOnlyDictionary<NodeId, SelectedImage> SelectedImages { get; init; }

    internal required ICanvasSurfaceSource CanvasSurfaces { get; init; }

    internal required SvgFontDatabase SvgFonts { get; init; }

    internal required (float Width, float Height) ContentSize { get; init; }

    internal required HashSet<NodeId> ViewportFixed { get; init; }

    internal required StickyLayout Sticky { get; init; }

    internal required ScrollTree ScrollTree { get; init; }

    internal required DomLayout Laid { get; init; }

    internal required NodeId? PaintRoot { get; init; }

    internal required NodeId? SuppressOpacityFor { get; init; }

    internal required NodeId? SuppressStackingFor { get; init; }

    internal required NodeId? SuppressTransformFor { get; init; }

    internal required NodeId? ClipScopeRoot { get; init; }

    internal required (float Width, float Height)? SurfaceExtent { get; init; }

    internal required (float X, float Y) SurfaceOffset { get; init; }

    internal required float RasterScale { get; init; }

    internal required bool PrintEconomy { get; init; }

    internal required CanvasBackground? CanvasBackground { get; init; }

    internal PaintPass With(
        Pixmap pixmap,
        ScrollPaintState shared,
        NodeId? paintRoot,
        NodeId? suppressOpacityFor,
        NodeId? suppressStackingFor,
        NodeId? suppressTransformFor,
        NodeId? clipScopeRoot,
        (float X, float Y) surfaceOffset) => new()
        {
            Tree = Tree,
            Viewport = Viewport,
            BaseUrl = BaseUrl,
            Scroll = Scroll,
            ResolvedScroll = ResolvedScroll,
            SharedScrollState = shared,
            Pixmap = pixmap,
            ImageCache = ImageCache,
            SelectedImages = SelectedImages,
            CanvasSurfaces = CanvasSurfaces,
            SvgFonts = SvgFonts,
            ContentSize = ContentSize,
            ViewportFixed = ViewportFixed,
            Sticky = Sticky,
            ScrollTree = ScrollTree,
            Laid = Laid,
            PaintRoot = paintRoot,
            SuppressOpacityFor = suppressOpacityFor,
            SuppressStackingFor = suppressStackingFor,
            SuppressTransformFor = suppressTransformFor,
            ClipScopeRoot = clipScopeRoot,
            SurfaceExtent = SurfaceExtent,
            SurfaceOffset = surfaceOffset,
            RasterScale = RasterScale,
            PrintEconomy = PrintEconomy,
            CanvasBackground = CanvasBackground,
        };
}

internal static class PaintDomPainter
{
    /// <summary>The raster target in the CSS-pixel coordinate space used by paint items.</summary>
    internal static Rect PaintSurfaceRect(Pixmap pixmap, float rasterScale)
    {
        float scale = float.IsFinite(rasterScale) && rasterScale > 0f ? rasterScale : 1f;
        return new Rect(0f, 0f, pixmap.Width / scale, pixmap.Height / scale);
    }

    internal static bool RectIntersectsPaintSurface(in Rect rect, Pixmap pixmap, float rasterScale) =>
        rect.Width > 0f
        && rect.Height > 0f
        && rect.Intersect(PaintSurfaceRect(pixmap, rasterScale)) is not null;

    /// <summary>Conservative ink overflow for the non-text primitives of one CSS box.</summary>
    internal static Rect NonTextInkBounds(in Rect rect, LayoutStyle style)
    {
        Rect bounds = rect;
        if (style.BoxShadow is { } shadow && !shadow.Inset && shadow.Color.A != 0)
        {
            float expansion = shadow.Spread + F32.Max(shadow.Blur, 0f);
            Rect shadowBounds = new(
                rect.X + shadow.OffsetX - expansion,
                rect.Y + shadow.OffsetY - expansion,
                F32.Max(rect.Width + (2f * expansion), 0f),
                F32.Max(rect.Height + (2f * expansion), 0f));
            if (shadowBounds.Width > 0f && shadowBounds.Height > 0f)
            {
                bounds = bounds.Union(shadowBounds);
            }
        }

        float outline = F32.Max(style.Outline.Offset + style.Outline.UsedWidth(), 0f);
        if (outline > 0f)
        {
            bounds = bounds.Union(new Rect(
                rect.X - outline,
                rect.Y - outline,
                rect.Width + (2f * outline),
                rect.Height + (2f * outline)));
        }

        return bounds;
    }

    internal static bool HasAuthoredTransform(LayoutStyle style) =>
        style.TransformOps.Count > 0
        || style.IndividualTranslate is not null
        || style.IndividualRotate is not null
        || style.IndividualScale is not null;

    /// <summary>The used <c>z-index</c> for an element which participates in stacking order.</summary>
    internal static int? StackingZIndex(DomTree tree, DomLayout laid, NodeId id)
    {
        if (!laid.Styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        if (style.Display == Display.None || style.DisplayContents)
        {
            return null;
        }

        // Fixed and sticky positioned boxes establish stacking contexts even when their used
        // z-index is `auto`.
        if (style.PositionFixed || style.PositionSticky)
        {
            return style.ZIndex ?? 0;
        }

        if (style.ZIndex is not { } z)
        {
            return null;
        }

        if (style.Position is not null)
        {
            return z;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (parent is { } parentId)
        {
            if (!laid.Styles.TryGetValue(parentId, out LayoutStyle? parentStyle))
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            if (parentStyle.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            bool isFlexOrGrid = parentStyle.Display == Display.Grid
                || (parentStyle.Display == Display.Flex && !parentStyle.InternalFlexContainer);
            return isFlexOrGrid ? z : null;
        }

        return null;
    }

    /// <summary>Whether this box participates in the float painting band.</summary>
    internal static bool IsEffectiveFloat(DomTree tree, DomLayout laid, NodeId id)
    {
        if (!laid.Styles.TryGetValue(id, out LayoutStyle? style))
        {
            return false;
        }

        if (style.Float is null
            || style.Display == Display.None
            || style.DisplayContents
            || style.Position == Layout.Position.Absolute)
        {
            return false;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (parent is { } parentId)
        {
            if (!laid.Styles.TryGetValue(parentId, out LayoutStyle? parentStyle))
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            if (parentStyle.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            return parentStyle.Display != Display.Grid
                && !(parentStyle.Display == Display.Flex && !parentStyle.InternalFlexContainer);
        }

        return true;
    }

    /// <summary>Untransformed source bounds for one atomic transform layer.</summary>
    private static Rect? TransformSubtreeSourceBounds(
        DomTree tree,
        DomLayout laid,
        ScrollPaintState scrollState,
        NodeId root)
    {
        Rect? bounds = null;
        List<NodeId> nodes = [root, .. DomTraversal.RenderedDescendants(tree, root)];
        foreach (NodeId id in nodes)
        {
            if (!laid.Rects.TryGetValue(id, out Rect rect))
            {
                continue;
            }

            (float x, float y) = scrollState.TranslationFor(laid, id);
            Rect visual = new(rect.X + x, rect.Y + y, rect.Width, rect.Height);
            if (laid.Styles.TryGetValue(id, out LayoutStyle? style))
            {
                // The atomic source surface must retain every primitive that can extend beyond
                // the border box, such as a thick outline.
                visual = NonTextInkBounds(visual, style);
            }

            bounds = bounds is { } current ? current.Union(visual) : visual;
        }

        return bounds is { } final
            ? new Rect(final.X - 2f, final.Y - 2f, final.Width + 4f, final.Height + 4f)
            : null;
    }

    internal static Pixmap? PaintLaidDomScrolled(PaintPass pass)
    {
        DomTree tree = pass.Tree;
        DomLayout laid = pass.Laid;
        Pixmap pixmap = pass.Pixmap;
        (float Width, float Height) viewport = pass.Viewport;
        float rasterScale = pass.RasterScale;

        ScrollPaintState scrollState = pass.ResolvedScroll is { } resolved
            ? ScrollPaintState.FromResolved(
                tree,
                viewport,
                pass.ViewportFixed,
                resolved,
                pass.ClipScopeRoot,
                pass.SurfaceExtent,
                pass.SurfaceOffset)
            : ScrollPaintState.Create(
                tree,
                viewport,
                pass.Scroll,
                pass.ContentSize,
                pass.ViewportFixed,
                pass.Sticky,
                pass.ScrollTree,
                laid,
                pass.SharedScrollState,
                pass.ClipScopeRoot,
                pass.SurfaceExtent,
                pass.SurfaceOffset);

        float rootFontSize = 16f;
        if (tree.QuerySelector("html") is { } htmlRoot
            && laid.Styles.TryGetValue(htmlRoot, out LayoutStyle? htmlStyle)
            && htmlStyle.FontSize is { } size)
        {
            rootFontSize = size;
        }

        if (pass.PaintRoot is null && pass.CanvasBackground is { } canvas)
        {
            bool rootVisible = laid.Styles.TryGetValue(canvas.Root, out LayoutStyle? canvasRootStyle)
                && !canvasRootStyle.EffectivelyInvisible;
            if (rootVisible
                && laid.Styles.TryGetValue(canvas.Source, out LayoutStyle? sourceStyle)
                && laid.Rects.TryGetValue(canvas.Source, out Rect sourceRect))
            {
                (float x, float y) = scrollState.TranslationFor(laid, canvas.Source);
                Rect originRect = new(sourceRect.X + x, sourceRect.Y + y, sourceRect.Width, sourceRect.Height);
                Rect surfaceRect = PaintSurfaceRect(pixmap, rasterScale);
                PaintCanvasBackground(
                    pixmap,
                    sourceStyle,
                    originRect,
                    surfaceRect,
                    rootFontSize,
                    viewport,
                    pass.BaseUrl,
                    pass.ImageCache,
                    rasterScale);
            }
        }

        // Nodes whose painting is owned by an inline `<svg>` raster.
        HashSet<NodeId> svgSubtreeSkip = [];

        // Subtrees already painted into an opacity layer, so shaped text is not drawn twice.
        HashSet<NodeId> opacitySubtreeSkip = [];

        // External sprite symbols, keyed by "url#id", cached across every inline svg.
        Dictionary<string, string?> spriteCache = new(StringComparer.Ordinal);

        // Immutable overflow masks for this one paint surface, shared by descendants.
        OverflowClipMaskCache overflowMaskCache = [];

        // Paint order follows CSS2's stacking bands.
        List<(int Z, NodeId Node)> negLayers = [];
        List<(int Z, NodeId Node)> posLayers = [];
        List<NodeId> floatLayers = [];
        List<NodeId> normal = [];
        HashSet<NodeId> consumed = [];
        List<NodeId> paintNodes = pass.PaintRoot is { } root ? [root] : [];
        paintNodes.AddRange(DomTraversal.RenderedDescendants(tree, pass.PaintRoot ?? tree.Document));

        foreach (NodeId nid in paintNodes)
        {
            if (consumed.Contains(nid))
            {
                continue;
            }

            bool isOpacityRoot = pass.SuppressOpacityFor != nid
                && laid.Styles.TryGetValue(nid, out LayoutStyle? opacityStyle)
                && opacityStyle.Opacity is { } opacityValue
                && Math.Clamp(opacityValue, 0f, 1f) < 1f;
            bool isTransformRoot = pass.SuppressTransformFor != nid
                && laid.Styles.TryGetValue(nid, out LayoutStyle? transformStyle)
                && HasAuthoredTransform(transformStyle);
            int? z = pass.SuppressStackingFor != nid ? StackingZIndex(tree, laid, nid) : null;
            bool isFloatRoot = pass.PaintRoot != nid && IsEffectiveFloat(tree, laid, nid);

            if (isOpacityRoot || isTransformRoot)
            {
                consumed.Add(nid);
                foreach (NodeId member in DomTraversal.RenderedDescendants(tree, nid))
                {
                    consumed.Add(member);
                }

                // An opacity effect is one atomic paint-order unit.
                if (z is { } zv)
                {
                    if (zv < 0)
                    {
                        negLayers.Add((zv, nid));
                    }
                    else
                    {
                        posLayers.Add((zv, nid));
                    }
                }
                else if (isFloatRoot)
                {
                    floatLayers.Add(nid);
                }
                else
                {
                    normal.Add(nid);
                }
            }
            else if (z is { } zv)
            {
                consumed.Add(nid);
                foreach (NodeId member in DomTraversal.RenderedDescendants(tree, nid))
                {
                    consumed.Add(member);
                }

                if (zv < 0)
                {
                    negLayers.Add((zv, nid));
                }
                else
                {
                    posLayers.Add((zv, nid));
                }
            }
            else if (isFloatRoot)
            {
                consumed.Add(nid);
                foreach (NodeId member in DomTraversal.RenderedDescendants(tree, nid))
                {
                    consumed.Add(member);
                }

                floatLayers.Add(nid);
            }
            else
            {
                normal.Add(nid);
            }
        }

        List<NodeId> paintOrder =
        [
            .. negLayers.OrderBy(entry => entry.Z).Select(entry => entry.Node),
            .. normal,
            .. floatLayers,
            .. posLayers.OrderBy(entry => entry.Z).Select(entry => entry.Node),
        ];

        // Generated boxes are anonymous layout children: ::before paints directly after its
        // host's own box; ::after paints after the host's last descendant in this paint order.
        Dictionary<NodeId, List<GeneratedBox>> generatedBefore = [];
        List<List<GeneratedBox>> generatedAfterAt = [.. paintOrder.Select(_ => new List<GeneratedBox>())];
        if (laid.GeneratedBoxes.Count > 0)
        {
            Dictionary<NodeId, int> paintIndices = [];
            for (int index = 0; index < paintOrder.Count; index++)
            {
                paintIndices[paintOrder[index]] = index;
            }

            Dictionary<NodeId, int> lastIndex = new(paintIndices);
            for (int i = paintNodes.Count - 1; i >= 0; i--)
            {
                NodeId nid = paintNodes[i];
                if (!lastIndex.TryGetValue(nid, out int index))
                {
                    continue;
                }

                if (pass.PaintRoot != nid && DomTraversal.RenderedParent(tree, nid) is { } parent)
                {
                    lastIndex[parent] = lastIndex.TryGetValue(parent, out int existing)
                        ? Math.Max(existing, index)
                        : index;
                }
            }

            foreach (GeneratedBox generated in laid.GeneratedBoxes)
            {
                if (generated.Kind == GeneratedBoxKind.Before)
                {
                    if (paintIndices.ContainsKey(generated.Host))
                    {
                        if (!generatedBefore.TryGetValue(generated.Host, out List<GeneratedBox>? list))
                        {
                            list = [];
                            generatedBefore[generated.Host] = list;
                        }

                        list.Add(generated);
                    }
                }
                else if (lastIndex.TryGetValue(generated.Host, out int index))
                {
                    generatedAfterAt[index].Add(generated);
                }
            }
        }

        for (int paintIndex = 0; paintIndex < paintOrder.Count; paintIndex++)
        {
            NodeId nid = paintOrder[paintIndex];
            if (svgSubtreeSkip.Contains(nid))
            {
                continue;
            }

            if (pass.SuppressStackingFor != nid && StackingZIndex(tree, laid, nid) is not null)
            {
                opacitySubtreeSkip.Add(nid);
                foreach (NodeId member in DomTraversal.RenderedDescendants(tree, nid))
                {
                    opacitySubtreeSkip.Add(member);
                }

                // A stacking context is one structural paint item in its parent.
                Pixmap? nested = PaintLaidDomScrolled(pass.With(
                    pixmap,
                    scrollState,
                    nid,
                    pass.SuppressOpacityFor,
                    nid,
                    pass.SuppressTransformFor,
                    pass.ClipScopeRoot,
                    pass.SurfaceOffset));
                if (nested is null)
                {
                    return null;
                }

                pixmap = nested;
                continue;
            }

            if (pass.PaintRoot != nid && IsEffectiveFloat(tree, laid, nid))
            {
                opacitySubtreeSkip.Add(nid);
                foreach (NodeId member in DomTraversal.RenderedDescendants(tree, nid))
                {
                    opacitySubtreeSkip.Add(member);
                }

                // CSS paints each float as an atomic unit in the float band.
                Pixmap? nested = PaintLaidDomScrolled(pass.With(
                    pixmap,
                    scrollState,
                    nid,
                    pass.SuppressOpacityFor,
                    pass.SuppressStackingFor,
                    pass.SuppressTransformFor,
                    pass.ClipScopeRoot,
                    pass.SurfaceOffset));
                if (nested is null)
                {
                    return null;
                }

                pixmap = nested;
                continue;
            }

            Node? node = tree.GetNode(nid);
            if (node is null)
            {
                continue;
            }

            if (node.IsText)
            {
                if (laid.WordIfcItems.TryGetValue(nid, out List<int>? items))
                {
                    (float X, float Y) offset = scrollState.TranslationFor(laid, nid);
                    OverflowClip? overflow = scrollState.ShapedTextOverflowClipFor(laid, nid);
                    Rect? clip = overflow?.ViewportRect(scrollState.SurfaceExtent ?? viewport);
                    Mask? clipMask = overflow is null
                        ? null
                        : PaintClips.CachedOverflowClipMask(
                            overflowMaskCache,
                            pixmap.Width,
                            pixmap.Height,
                            overflow,
                            scrollState.SurfaceExtent ?? viewport);
                    foreach (int item in items)
                    {
                        laid.TextEngine.PaintItemWithClipMaskScaledForPrint(
                            item,
                            pixmap,
                            offset,
                            clip,
                            clipMask,
                            rasterScale,
                            pass.PrintEconomy);
                    }
                }
                else
                {
                    PaintText.PaintTextNode(tree, nid, laid, scrollState, pixmap, rasterScale);
                }

                foreach (GeneratedBox generated in generatedAfterAt[paintIndex])
                {
                    PaintGenerated.PaintInFlowGeneratedBox(
                        pixmap,
                        generated,
                        laid,
                        scrollState,
                        viewport,
                        rootFontSize,
                        pass.BaseUrl,
                        pass.ImageCache,
                        rasterScale);
                }

                continue;
            }

            if (node.AsElement() is not { } element)
            {
                continue;
            }

            if (!laid.Rects.TryGetValue(nid, out Rect layoutRect))
            {
                continue;
            }

            if (!laid.Styles.TryGetValue(nid, out LayoutStyle? style))
            {
                continue;
            }

            string localName = element.Name.Local;

            if (pass.SuppressTransformFor != nid && HasAuthoredTransform(style))
            {
                opacitySubtreeSkip.Add(nid);
                foreach (NodeId member in DomTraversal.RenderedDescendants(tree, nid))
                {
                    opacitySubtreeSkip.Add(member);
                }

                Affine2 transform = DomTransforms.ResolvedTransformMatrix(
                    style,
                    layoutRect,
                    rootFontSize,
                    viewport);
                if (transform.IsTranslation())
                {
                    // Translation-only transforms retain the direct offset path.
                    Pixmap? nested = PaintLaidDomScrolled(pass.With(
                        pixmap,
                        scrollState,
                        nid,
                        pass.SuppressOpacityFor,
                        pass.SuppressStackingFor,
                        nid,
                        pass.ClipScopeRoot,
                        pass.SurfaceOffset));
                    if (nested is null)
                    {
                        return null;
                    }

                    pixmap = nested;
                    continue;
                }

                // A transform wraps the complete atomic child display list.
                OverflowClip? outsideOverflowClip = scrollState.OverflowClipFor(laid, nid);
                Rect? outsideClip = outsideOverflowClip?.ViewportRect(scrollState.SurfaceExtent ?? viewport);
                (float mx, float my) = scrollState.TranslationFor(laid, nid);
                Affine2 displayTransform = Affine2.Translate(mx, my)
                    .Then(transform)
                    .Then(Affine2.Translate(-mx, -my));
                Rect target = new(0f, 0f, pixmap.Width, pixmap.Height);
                if (outsideClip is { } clipRect)
                {
                    if (target.Intersect(clipRect) is not { } clipped)
                    {
                        continue;
                    }

                    target = clipped;
                }

                if (displayTransform.Inverse() is not { } inverse)
                {
                    // A singular transform has no two-dimensional painted area.
                    continue;
                }

                Rect neededSource = inverse.MapRect(target);
                Rect? sourceBounds = TransformSubtreeSourceBounds(tree, laid, scrollState, nid)
                    ?.Intersect(neededSource);
                if (sourceBounds is not { } layerSource)
                {
                    continue;
                }

                float left = MathF.Floor(layerSource.X);
                float top = MathF.Floor(layerSource.Y);
                float right = MathF.Ceiling(layerSource.X + layerSource.Width);
                float bottom = MathF.Ceiling(layerSource.Y + layerSource.Height);
                uint layerWidth = (uint)F32.Max(right - left, 1f);
                uint layerHeight = (uint)F32.Max(bottom - top, 1f);
                (float X, float Y) layerDelta = (-left, -top);
                Pixmap? layer = Pixmap.New(layerWidth, layerHeight);
                if (layer is null)
                {
                    return null;
                }

                Pixmap? painted = PaintLaidDomScrolled(pass.With(
                    layer,
                    scrollState,
                    nid,
                    pass.SuppressOpacityFor,
                    pass.SuppressStackingFor,
                    nid,
                    nid,
                    (pass.SurfaceOffset.X + layerDelta.X, pass.SurfaceOffset.Y + layerDelta.Y)));
                if (painted is null)
                {
                    return null;
                }

                Affine2 mapped = displayTransform.Then(Affine2.Translate(-layerDelta.X, -layerDelta.Y));
                Mask? layerMask = outsideOverflowClip is null
                    ? null
                    : PaintClips.CachedOverflowClipMask(
                        overflowMaskCache,
                        pixmap.Width,
                        pixmap.Height,
                        outsideOverflowClip,
                        scrollState.SurfaceExtent ?? viewport);
                Surface.DrawPixmap(pixmap, 0, 0, painted, 1f, bilinear: true, mapped, layerMask);
                painted.Dispose();
                continue;
            }

            float ownOpacity = Math.Clamp(style.Opacity ?? 1f, 0f, 1f);
            // `filter` groups exactly like `opacity` does: the whole finished stacking
            // context is post-processed once, never each primitive. Sharing the opacity
            // group's bookkeeping keeps one suppression flag and one recursion rather
            // than a second, near-identical layer path.
            float? groupBlur = style.FilterBlur is { } blurSigma
                && float.IsFinite(blurSigma) && blurSigma > 0f
                    ? blurSigma
                    : null;
            if (pass.SuppressOpacityFor != nid && (ownOpacity < 1f || groupBlur is not null))
            {
                opacitySubtreeSkip.Add(nid);
                foreach (NodeId member in DomTraversal.RenderedDescendants(tree, nid))
                {
                    opacitySubtreeSkip.Add(member);
                }

                if (ownOpacity <= 0f)
                {
                    continue;
                }

                // Opacity is applied to the finished stacking context, never to each primitive.
                Pixmap? layer = Pixmap.New(pixmap.Width, pixmap.Height);
                if (layer is null)
                {
                    return null;
                }

                Pixmap? painted = PaintLaidDomScrolled(pass.With(
                    layer,
                    scrollState,
                    nid,
                    nid,
                    pass.SuppressStackingFor,
                    pass.SuppressTransformFor,
                    pass.ClipScopeRoot,
                    pass.SurfaceOffset));
                if (painted is null)
                {
                    return null;
                }

                if (groupBlur is { } sigma)
                {
                    PaintFilters.BlurPixmap(painted, sigma);
                }

                Surface.DrawPixmap(pixmap, 0, 0, painted, ownOpacity, false, Affine2.Identity, null);
                painted.Dispose();
                continue;
            }

            if (style.EffectivelyInvisible)
            {
                continue;
            }

            bool backgroundTransfersToCanvas = pass.CanvasBackground is { } transfer
                && (nid == transfer.Root || nid == transfer.Source);

            (float ox, float oy) = scrollState.TranslationFor(laid, nid);
            Rect rect = new(layoutRect.X + ox, layoutRect.Y + oy, layoutRect.Width, layoutRect.Height);

            OverflowClip? overflowClip = scrollState.OverflowClipFor(laid, nid);
            Rect? boxClip = overflowClip?.ViewportRect(scrollState.SurfaceExtent ?? viewport);
            Rect visibleRect;
            if (boxClip is { } c)
            {
                if (rect.Intersect(c) is not { } intersected)
                {
                    continue;
                }

                visibleRect = intersected;
            }
            else
            {
                visibleRect = rect;
            }

            Rect surface = PaintSurfaceRect(pixmap, rasterScale);
            bool boxOnSurface = visibleRect.Intersect(surface) is not null;
            Rect inkBounds = NonTextInkBounds(rect, style);
            bool nonTextOnSurface = inkBounds.Intersect(surface) is not null;
            float textGuard = ((style.FontSize ?? 16f) * 4f) + 4f;
            Rect textBounds = new(
                rect.X - textGuard,
                rect.Y - textGuard,
                rect.Width + (2f * textGuard),
                rect.Height + (2f * textGuard));
            bool textOnSurface = textBounds.Intersect(surface) is not null;
            bool needsHostMasks = nonTextOnSurface || textOnSurface;
            Mask? ancestorClipMask = needsHostMasks && overflowClip is not null
                ? PaintClips.CachedOverflowClipMask(
                    overflowMaskCache,
                    pixmap.Width,
                    pixmap.Height,
                    overflowClip,
                    scrollState.SurfaceExtent ?? viewport)
                : null;
            Mask? clipPathMask = needsHostMasks && style.ClipPath is { } polygon
                ? PaintClips.PolygonClipMask(
                    pixmap.Width,
                    pixmap.Height,
                    polygon,
                    rect,
                    style.FontSize ?? 16f,
                    rootFontSize,
                    viewport)
                : null;

            bool hasBackgroundBoxPaint = !style.BackgroundClipText
                && (style.BackgroundColor is not null
                    || style.BackgroundImage is not null
                    || style.MaskImage is not null
                    || style.BackgroundGradient is not null
                    || style.BackgroundRadialGradient is not null
                    || style.BackgroundConicGradient is not null
                    || style.BackgroundGradientLayers.Count > 0);
            bool hasInlineBoxPaint = style.BoxShadow is not null
                || hasBackgroundBoxPaint
                || style.Border != default;
            List<Rect>? inlinePieces = style.IgnoresUsedBoxSizes() && hasInlineBoxPaint
                && laid.InlineFragments.TryGetValue(nid, out List<Rect>? fragments)
                ? fragments
                : null;
            bool paintsInlineFragments = inlinePieces is not null;
            if (inlinePieces is not null)
            {
                PaintInline.PaintInlineFragmentDecorations(
                    pixmap,
                    inlinePieces,
                    (ox, oy),
                    rect,
                    style,
                    boxClip,
                    ancestorClipMask,
                    rootFontSize,
                    viewport,
                    pass.BaseUrl,
                    pass.ImageCache,
                    rasterScale);
            }

            // Outset box-shadow paints behind this element's own background/border.
            if (!paintsInlineFragments && style.BoxShadow is { } boxShadow)
            {
                PaintBorders.PaintBoxShadow(
                    pixmap,
                    boxShadow,
                    rect,
                    style.BorderModel.Radii,
                    ancestorClipMask);
            }

            ResolvedBorderRadii radius = style.BorderModel.Radii.Resolve(rect.Width, rect.Height);
            bool hasRadius = !radius.IsZero();
            BackgroundGeometry background = PaintGradients.BackgroundGeometryFor(rect, style);
            SKPath? backgroundPath = PaintGradients.BackgroundClipPath(background);
            Mask? combinedClipMask = clipPathMask is not null
                ? PaintClips.IntersectClipMasks(ancestorClipMask?.Clone(), clipPathMask)
                : null;
            Mask? elementClipMask = combinedClipMask ?? ancestorClipMask;
            Mask? backgroundMask = elementClipMask;

            if (boxOnSurface
                && !paintsInlineFragments
                && !backgroundTransfersToCanvas
                && style.MaskImage is null
                && !style.BackgroundClipText)
            {
                if (style.BackgroundColor is { } bg && backgroundPath is not null)
                {
                    Surface.FillPath(
                        pixmap,
                        backgroundPath,
                        bg,
                        !background.ClipRadii.IsZero(),
                        Surface.RasterTransform(rasterScale),
                        backgroundMask);
                }

                if (style.BackgroundGradientLayers.Count > 0)
                {
                    if (backgroundPath is not null)
                    {
                        PaintGradients.PaintBackgroundGradientLayers(
                            pixmap,
                            backgroundPath,
                            background.OriginRect,
                            background.ClipRect,
                            background.ClipRadii,
                            style,
                            rootFontSize,
                            viewport,
                            backgroundMask,
                            rasterScale);
                    }
                }
                else
                {
                    if (style.BackgroundRadialGradient is { } radial && backgroundPath is not null)
                    {
                        PaintGradients.PaintRadialGradient(
                            pixmap,
                            backgroundPath,
                            background.OriginRect,
                            radial.Center,
                            radial.Stops,
                            // The legacy single-gradient tuple carries no authored stop strings; the
                            // percentages already in Stops are the whole story.
                            [],
                            style.BackgroundRadialGradientGeometry,
                            style.FontSize ?? 16f,
                            rootFontSize,
                            viewport,
                            backgroundMask,
                            rasterScale);
                    }

                    if (style.BackgroundConicGradient is { } conic)
                    {
                        PaintGradients.PaintConicGradientSampled(
                            pixmap,
                            background.ClipRect,
                            background.OriginRect,
                            background.ClipRadii,
                            conic.Angle,
                            conic.Center,
                            conic.Stops,
                            backgroundMask);
                    }

                    if (style.BackgroundGradient is { } linear && backgroundPath is not null)
                    {
                        PaintGradients.PaintLinearGradient(
                            pixmap,
                            backgroundPath,
                            background.OriginRect,
                            linear.Angle,
                            linear.Stops,
                            backgroundMask,
                            rasterScale);
                    }
                }
            }

            if (boxOnSurface && !paintsInlineFragments && !backgroundTransfersToCanvas)
            {
                if (style.MaskImage is { } maskUrl)
                {
                    RgbaColor fill = style.BackgroundColor
                        ?? style.Color
                        ?? new RgbaColor(0, 0, 0, 255);
                    PaintImages.PaintMask(
                        maskUrl,
                        pass.BaseUrl,
                        visibleRect,
                        radius,
                        fill,
                        style.BackgroundRadialGradient,
                        style.BackgroundRadialGradientGeometry,
                        style.FontSize ?? 16f,
                        rootFontSize,
                        viewport,
                        style.BackgroundGradient,
                        style.BackgroundConicGradient,
                        style.MaskSize,
                        style.MaskRepeat,
                        elementClipMask,
                        pixmap,
                        pass.ImageCache);
                }
                else if (style.BackgroundImage is { } bgUrl)
                {
                    Rect? imageRect = PaintImages.BackgroundImageRect(
                        bgUrl,
                        pass.BaseUrl,
                        background.OriginRect,
                        style.BackgroundSize,
                        style.BackgroundSizeExpression,
                        style.BackgroundSizeFit,
                        style.BackgroundPosition,
                        style.FontSize ?? 16f,
                        rootFontSize,
                        viewport,
                        pass.ImageCache);
                    if (imageRect is { } img
                        && background.ClipRect.Width > 0f
                        && background.ClipRect.Height > 0f)
                    {
                        PaintImages.PaintImage(
                            bgUrl,
                            pass.BaseUrl,
                            img,
                            background.ClipRect,
                            ObjectFit.Fill,
                            ObjectPosition.Default,
                            pixmap,
                            pass.ImageCache,
                            null,
                            null,
                            background.ClipRadii,
                            backgroundMask);
                    }
                }
            }

            bool hasPositionedPseudo = new[] { style.BeforePseudo, style.AfterPseudo }
                .Any(pseudo => pseudo is { Position: Layout.Position.Absolute });
            if (hasPositionedPseudo)
            {
                Rect positionedPseudoContainingBlock = rect;
                NodeId? ancestor = nid;
                while (ancestor is { } candidate)
                {
                    bool establishes = laid.Styles.TryGetValue(candidate, out LayoutStyle? candidateStyle)
                        && (candidateStyle.Position is not null
                            || candidateStyle.EstablishesPositioningContainingBlock());
                    if (establishes && laid.Rects.TryGetValue(candidate, out Rect candidateRect))
                    {
                        (float cx, float cy) = scrollState.TranslationFor(laid, candidate);
                        positionedPseudoContainingBlock = new Rect(
                            candidateRect.X + cx,
                            candidateRect.Y + cy,
                            candidateRect.Width,
                            candidateRect.Height);
                        break;
                    }

                    ancestor = DomTraversal.RenderedParent(tree, candidate);
                }

                OverflowClip? positionedPseudoOverflowClip =
                    scrollState.DescendantOverflowClipFor(laid, nid);
                foreach (LayoutStyle? pseudo in new[] { style.BeforePseudo, style.AfterPseudo })
                {
                    if (pseudo is null)
                    {
                        continue;
                    }

                    PaintGenerated.PaintPositionedPseudo(
                        laid.TextEngine,
                        pixmap,
                        pseudo,
                        positionedPseudoContainingBlock,
                        rect,
                        viewport,
                        rootFontSize,
                        scrollState.SurfaceExtent ?? viewport,
                        positionedPseudoOverflowClip,
                        pass.BaseUrl,
                        pass.ImageCache,
                        rasterScale);
                }
            }

            if (boxOnSurface && string.Equals(localName, "canvas", StringComparison.Ordinal)
                && pass.CanvasSurfaces.Surface(nid) is { } canvasSurface)
            {
                // A canvas bitmap is replaced content: CSS sizing and object-fit operate on the
                // content box, never the padding or border box.
                Sides<float> contentInsets = new(
                    style.Border.Top + style.Padding.Top,
                    style.Border.Right + style.Padding.Right,
                    style.Border.Bottom + style.Padding.Bottom,
                    style.Border.Left + style.Padding.Left);
                Rect contentRect = new(
                    rect.X + contentInsets.Left,
                    rect.Y + contentInsets.Top,
                    F32.Max(rect.Width - contentInsets.Left - contentInsets.Right, 0f),
                    F32.Max(rect.Height - contentInsets.Top - contentInsets.Bottom, 0f));
                Rect contentVisible = contentRect.Intersect(visibleRect) ?? default;
                PaintImages.PaintCanvasSurface(
                    canvasSurface,
                    contentRect,
                    contentVisible,
                    style.ObjectFit,
                    style.ObjectPosition,
                    pixmap,
                    radius.Inset(contentInsets),
                    elementClipMask);
            }

            if (!paintsInlineFragments)
            {
                // CSS Backgrounds 3 paints an inset shadow over the background and
                // under the border, so it cannot ride along with the outset pass above.
                if (style.BoxShadow is { } insetShadow)
                {
                    PaintBorders.PaintInsetBoxShadow(
                        pixmap, insetShadow, rect, style.BorderModel.Radii, elementClipMask);
                }

                PaintBorders.PaintCssBorder(pixmap, rect, style, elementClipMask, rasterScale);
            }

            PaintBorders.PaintCssOutline(pixmap, rect, style, elementClipMask, rasterScale);

            if (boxOnSurface && localName is "img" or "video"
                && pass.SelectedImages.TryGetValue(nid, out SelectedImage? source))
            {
                bool painted = PaintImages.PaintImage(
                    source.ResolvedUrl,
                    null,
                    rect,
                    visibleRect,
                    style.ObjectFit,
                    style.ObjectPosition,
                    pixmap,
                    pass.ImageCache,
                    source.Profile,
                    null,
                    radius,
                    elementClipMask);

                // Fall back when the image itself did not paint, following what browsers show
                // for a broken image.
                if (!painted && string.Equals(localName, "img", StringComparison.Ordinal))
                {
                    string? alt = node.GetAttribute("alt");
                    if (alt is not null && alt.Trim().Length > 0)
                    {
                        PaintText.DrawText(
                            pixmap,
                            alt,
                            rect.X,
                            rect.Y,
                            new RgbaColor(0, 0, 0, 255),
                            12f,
                            false,
                            null,
                            0f,
                            boxClip,
                            elementClipMask,
                            rasterScale);
                    }
                    else if (alt is null && visibleRect.Width >= 4f && visibleRect.Height >= 4f)
                    {
                        using SKPathBuilder builder = new();
                        builder.AddRect(new SKRect(
                            visibleRect.X,
                            visibleRect.Y,
                            visibleRect.X + visibleRect.Width,
                            visibleRect.Y + visibleRect.Height));
                        using SKPath placeholder = builder.Detach();
                        Surface.FillPath(
                            pixmap,
                            placeholder,
                            new RgbaColor(0xE9, 0xEA, 0xEC, 0xFF),
                            false,
                            Surface.RasterTransform(rasterScale),
                            null);
                    }
                }
            }

            // Inline `<svg>...</svg>`: serialize the whole subtree back to one standalone SVG
            // document and rasterize it as a unit.
            if (string.Equals(localName, "svg", StringComparison.Ordinal))
            {
                if (boxOnSurface)
                {
                    string markup = PaintSvg.SerializeSvgStyled(
                        tree,
                        nid,
                        laid.Styles,
                        laid.CustomProperties,
                        pass.SuppressOpacityFor == nid ? nid : null);

                    // Resolve referenced symbols before carrying the host color in.
                    markup = PaintSvg.InjectExternalSprites(
                        tree,
                        nid,
                        laid.Styles,
                        laid.CustomProperties,
                        pass.BaseUrl,
                        markup,
                        pass.ImageCache,
                        spriteCache);

                    // Preserve the host element's computed `color` for `currentColor`.
                    if (style.Color is { } color)
                    {
                        markup = PaintSvg.InjectSvgCurrentColor(markup, color);
                    }

                    Pixmap? content = SvgRenderer.RenderWithFontDatabase(
                        System.Text.Encoding.UTF8.GetBytes(markup),
                        (uint)rect.Width,
                        (uint)rect.Height,
                        pass.SvgFonts);
                    if (content is not null)
                    {
                        Mask? mask = elementClipMask?.Clone();
                        if (hasRadius)
                        {
                            Mask? own = PaintClips.RoundedBoxClipMaskRadii(
                                pixmap.Width,
                                pixmap.Height,
                                visibleRect,
                                radius);
                            mask = PaintClips.IntersectClipMasks(mask, own);
                        }

                        Surface.DrawPixmap(
                            pixmap,
                            (int)rect.X,
                            (int)rect.Y,
                            content,
                            1f,
                            false,
                            Affine2.Identity,
                            mask);
                        content.Dispose();
                    }
                }

                foreach (NodeId child in DomTraversal.RenderedDescendants(tree, nid))
                {
                    svgSubtreeSkip.Add(child);
                }
            }

            if (generatedBefore.TryGetValue(nid, out List<GeneratedBox>? before))
            {
                foreach (GeneratedBox generated in before)
                {
                    PaintGenerated.PaintInFlowGeneratedBox(
                        pixmap,
                        generated,
                        laid,
                        scrollState,
                        viewport,
                        rootFontSize,
                        pass.BaseUrl,
                        pass.ImageCache,
                        rasterScale);
                }
            }

            // List-item marker, drawn in the indent to the left of the item's content box.
            if (string.Equals(localName, "li", StringComparison.Ordinal)
                && PaintText.ListMarkerText(tree, nid, style.ListStyle) is { } marker)
            {
                float markerSize = style.FontSize ?? 16f;
                RgbaColor markerColor = style.Color ?? new RgbaColor(0, 0, 0, 255);
                float markerWidth = PaintText.MeasureText(marker, markerSize, false, style.FontFamily);
                float mx = rect.X + style.Padding.Left - markerWidth - 6f;
                float my = rect.Y + style.Border.Top + style.Padding.Top;
                PaintText.DrawText(
                    pixmap,
                    marker,
                    mx,
                    my,
                    markerColor,
                    markerSize,
                    false,
                    style.FontFamily,
                    style.LetterSpacing ?? 0f,
                    boxClip,
                    elementClipMask,
                    rasterScale);
            }

            // `::before`/`::after` generated text has no DOM text node of its own.
            if (laid.WordIfcItems.TryGetValue(nid, out List<int>? hostItems))
            {
                (float X, float Y) offset = scrollState.TranslationFor(laid, nid);
                foreach (int item in hostItems)
                {
                    laid.TextEngine.PaintItemWithClipMaskScaledForPrint(
                        item,
                        pixmap,
                        offset,
                        boxClip,
                        elementClipMask,
                        rasterScale,
                        pass.PrintEconomy);
                }
            }
            else if (laid.TextRuns.TryGetValue(nid, out List<(Rect Rect, string Text)>? runs))
            {
                RgbaColor runColor = style.Color ?? new RgbaColor(0, 0, 0, 255);
                float runSize = style.FontSize ?? 16f;
                bool runBold = ComputedStyle.UsedFontWeight(style) >= 600;
                foreach ((Rect wordRect, string word) in runs)
                {
                    PaintText.DrawText(
                        pixmap,
                        word,
                        wordRect.X + ox,
                        wordRect.Y + oy,
                        runColor,
                        runSize,
                        runBold,
                        style.FontFamily,
                        style.LetterSpacing ?? 0f,
                        boxClip,
                        elementClipMask,
                        rasterScale);
                }
            }

            // A closed native `<select>` paints only its selected option.
            if (string.Equals(localName, "select", StringComparison.Ordinal))
            {
                if (PaintText.SelectedOptionLabel(tree, nid) is { } label)
                {
                    float labelSize = style.FontSize ?? 13.333_333f;
                    float lineHeight = FontResolution.UsedLineHeight(style);
                    float textX = rect.X + style.Border.Left + style.Padding.Left;
                    float textY = rect.Y + ((rect.Height - lineHeight) / 2f);
                    PaintText.DrawText(
                        pixmap,
                        label,
                        textX,
                        textY,
                        style.Color ?? new RgbaColor(0, 0, 0, 255),
                        labelSize,
                        ComputedStyle.UsedFontWeight(style) >= 600,
                        style.FontFamily,
                        style.LetterSpacing ?? 0f,
                        visibleRect,
                        elementClipMask,
                        rasterScale);
                }

                if (rect.Width >= 12f && rect.Height >= 8f)
                {
                    float centerX = rect.X + rect.Width - style.Border.Right - 8f;
                    float centerY = rect.Y + (rect.Height / 2f);
                    using SKPathBuilder arrow = new();
                    arrow.MoveTo(centerX - 3.5f, centerY - 2f);
                    arrow.LineTo(centerX + 3.5f, centerY - 2f);
                    arrow.LineTo(centerX, centerY + 2.5f);
                    arrow.Close();
                    using SKPath arrowPath = arrow.Detach();
                    Surface.FillPath(
                        pixmap,
                        arrowPath,
                        style.Color ?? new RgbaColor(0, 0, 0, 255),
                        false,
                        Surface.RasterTransform(rasterScale),
                        elementClipMask);
                }
            }

            // An empty text `<input>`/`<textarea>` shows its `placeholder` attribute.
            if (localName is "input" or "textarea")
            {
                string? valueAttribute = node.GetAttribute("value");
                bool hasValue = (valueAttribute is not null && valueAttribute.Length > 0)
                    || (string.Equals(localName, "textarea", StringComparison.Ordinal)
                        && tree.TextContent(nid).Length > 0);
                if (hasValue
                    && string.Equals(localName, "input", StringComparison.Ordinal)
                    && valueAttribute is { Length: > 0 })
                {
                    float valueSize = style.FontSize ?? 16f;
                    float textX = rect.X + style.Padding.Left + style.Border.Left;
                    float textY = rect.Y + style.Padding.Top + style.Border.Top;
                    RgbaColor color = style.Color ?? new RgbaColor(0, 0, 0, 255);
                    string shown = node.GetAttribute("type") is { } kind
                        && kind.Equals("password", StringComparison.OrdinalIgnoreCase)
                        ? new string('•', valueAttribute.EnumerateRunes().Count())
                        : valueAttribute;
                    if (color.A != 0)
                    {
                        PaintText.DrawText(
                            pixmap,
                            shown,
                            textX,
                            textY,
                            color,
                            valueSize,
                            false,
                            style.FontFamily,
                            style.LetterSpacing ?? 0f,
                            boxClip,
                            elementClipMask,
                            rasterScale);
                    }
                }

                if (!hasValue && node.GetAttribute("placeholder") is { Length: > 0 } placeholder)
                {
                    float placeholderSize = style.FontSize ?? 16f;
                    float textX = rect.X + style.Padding.Left + style.Border.Left;
                    float textY = rect.Y + style.Padding.Top + style.Border.Top;
                    LayoutStyle? placeholderStyle = style.PlaceholderPseudo;
                    RgbaColor color = placeholderStyle?.Color ?? new RgbaColor(117, 117, 117, 255);
                    float opacity = Math.Clamp(placeholderStyle?.Opacity ?? 1f, 0f, 1f);
                    color = color with { A = (byte)F32.Round(color.A * opacity) };
                    if (color.A != 0)
                    {
                        PaintText.DrawText(
                            pixmap,
                            placeholder,
                            textX,
                            textY,
                            color,
                            placeholderSize,
                            false,
                            style.FontFamily,
                            style.LetterSpacing ?? 0f,
                            boxClip,
                            elementClipMask,
                            rasterScale);
                    }
                }
            }

            foreach (GeneratedBox generated in generatedAfterAt[paintIndex])
            {
                PaintGenerated.PaintInFlowGeneratedBox(
                    pixmap,
                    generated,
                    laid,
                    scrollState,
                    viewport,
                    rootFontSize,
                    pass.BaseUrl,
                    pass.ImageCache,
                    rasterScale);
            }
        }

        // Inline formatting contexts draw last, in tree order, so their glyphs sit above the
        // box backgrounds/borders painted in the loop above.
        foreach (NodeId nid in paintNodes)
        {
            if (svgSubtreeSkip.Contains(nid) || opacitySubtreeSkip.Contains(nid))
            {
                continue;
            }

            bool hasWhole = laid.IfcItems.TryGetValue(nid, out int whole);
            bool hasRuns = laid.RunIfcItems.TryGetValue(nid, out List<int>? runItems);
            if (!hasWhole && !hasRuns)
            {
                continue;
            }

            if (laid.Styles.TryGetValue(nid, out LayoutStyle? textStyle) && textStyle.EffectivelyInvisible)
            {
                continue;
            }

            (float X, float Y) off = scrollState.TranslationFor(laid, nid);
            OverflowClip? overflow = scrollState.ShapedTextOverflowClipFor(laid, nid);
            Rect? clip = overflow?.ViewportRect(scrollState.SurfaceExtent ?? viewport);
            Mask? clipMask = overflow is null
                ? null
                : PaintClips.CachedOverflowClipMask(
                    overflowMaskCache,
                    pixmap.Width,
                    pixmap.Height,
                    overflow,
                    scrollState.SurfaceExtent ?? viewport);
            if (hasWhole)
            {
                laid.TextEngine.PaintItemWithClipMaskScaledForPrint(
                    whole,
                    pixmap,
                    off,
                    clip,
                    clipMask,
                    rasterScale,
                    pass.PrintEconomy);
            }

            if (runItems is not null)
            {
                foreach (int index in runItems)
                {
                    laid.TextEngine.PaintItemWithClipMaskScaledForPrint(
                        index,
                        pixmap,
                        off,
                        clip,
                        clipMask,
                        rasterScale,
                        pass.PrintEconomy);
                }
            }
        }

        return pixmap;
    }

    private static void PaintCanvasBackground(
        Pixmap pixmap,
        LayoutStyle style,
        in Rect originRect,
        in Rect surfaceRect,
        float rootFontSize,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache imageCache,
        float rasterScale)
    {
        if (surfaceRect.Width <= 0f || surfaceRect.Height <= 0f)
        {
            return;
        }

        using SKPathBuilder builder = new();
        builder.AddRect(new SKRect(
            surfaceRect.X,
            surfaceRect.Y,
            surfaceRect.X + surfaceRect.Width,
            surfaceRect.Y + surfaceRect.Height));
        using SKPath path = builder.Detach();

        if (style.BackgroundColor is { } color)
        {
            Surface.FillPath(pixmap, path, color, false, Surface.RasterTransform(rasterScale), null);
        }

        if (style.BackgroundGradientLayers.Count > 0)
        {
            PaintGradients.PaintBackgroundGradientLayers(
                pixmap,
                path,
                originRect,
                surfaceRect,
                default,
                style,
                rootFontSize,
                viewport,
                null,
                rasterScale);
        }
        else
        {
            if (style.BackgroundRadialGradient is { } radial)
            {
                PaintGradients.PaintRadialGradient(
                    pixmap,
                    path,
                    originRect,
                    radial.Center,
                    radial.Stops,
                    // The legacy single-gradient tuple carries no authored stop strings; the
                    // percentages already in Stops are the whole story.
                    [],
                    style.BackgroundRadialGradientGeometry,
                    style.FontSize ?? 16f,
                    rootFontSize,
                    viewport,
                    null,
                    rasterScale);
            }

            if (style.BackgroundConicGradient is { } conic)
            {
                PaintGradients.PaintConicGradientSampled(
                    pixmap,
                    surfaceRect,
                    originRect,
                    default,
                    conic.Angle,
                    conic.Center,
                    conic.Stops,
                    null);
            }

            if (style.BackgroundGradient is { } linear)
            {
                PaintGradients.PaintLinearGradient(
                    pixmap,
                    path,
                    originRect,
                    linear.Angle,
                    linear.Stops,
                    null,
                    rasterScale);
            }
        }

        if (style.BackgroundImage is { } url)
        {
            Rect? imageRect = PaintImages.BackgroundImageRect(
                url,
                baseUrl,
                originRect,
                style.BackgroundSize,
                style.BackgroundSizeExpression,
                style.BackgroundSizeFit,
                style.BackgroundPosition,
                style.FontSize ?? 16f,
                rootFontSize,
                viewport,
                imageCache);
            if (imageRect is { } img)
            {
                PaintImages.PaintImage(
                    url,
                    baseUrl,
                    img,
                    surfaceRect,
                    ObjectFit.Fill,
                    ObjectPosition.Default,
                    pixmap,
                    imageCache,
                    null,
                    null,
                    default,
                    null);
            }
        }
    }
}
