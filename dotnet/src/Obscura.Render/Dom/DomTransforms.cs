// Port of the clip/transform accumulation walk in crates/obscura-render/src/dom.rs.
using Obscura.Dom;

namespace Obscura.Render;

internal static class DomTransforms
{
    /// <summary>
    /// Walk the tree top-down accumulating the clip rect imposed by ancestor
    /// <c>overflow: hidden</c> boxes. Must run after layout, since it needs border boxes.
    /// </summary>
    /// <remarks>
    /// Clips are stored in SCREEN space: the clip owner's border box offset by the owner's own
    /// accumulated <c>transform: translate()</c>. A clip belongs to its owner's coordinate
    /// space, not the painted descendant's. The same walk records each node's accumulated
    /// translate in <paramref name="translates"/>.
    /// </remarks>
    internal static void ResolveClipRects(
        DomTree tree,
        NodeId id,
        OverflowClip? inherited,
        float tx,
        float ty,
        Dictionary<NodeId, Rect> rects,
        Dictionary<NodeId, LayoutStyle> styles,
        Dictionary<NodeId, OverflowClip?> clipRects,
        Dictionary<NodeId, (float X, float Y)> translates,
        Affine2 parentTransform,
        Dictionary<NodeId, Affine2> transforms,
        float rootFontSize,
        (float Width, float Height) viewport)
    {
        clipRects[id] = inherited?.Clone();

        // This node's own translate joins the accumulation for its box and its whole subtree
        // (percentages resolve against its own border box).
        (float ownTx, float ownTy) = (0f, 0f);
        Affine2 ownTransform = Affine2.Identity;
        if (styles.TryGetValue(id, out LayoutStyle? style))
        {
            Rect rect = rects.TryGetValue(id, out Rect found) ? found : default;
            (ownTx, ownTy) = ResolvedOwnTranslate(style, rect, rootFontSize, viewport);
            ownTransform = ResolvedTransformMatrix(style, rect, rootFontSize, viewport);
        }

        tx += ownTx;
        ty += ownTy;
        if (tx != 0f || ty != 0f)
        {
            translates[id] = (tx, ty);
        }

        Affine2 transform = parentTransform.Then(ownTransform);
        if (!transform.IsIdentity())
        {
            transforms[id] = transform;
        }

        // Overflow propagated from html/body establishes the root scrolling viewport. It is
        // anchored to the capture surface, not to document coordinates.
        OverflowClip? next = inherited;
        if (styles.TryGetValue(id, out LayoutStyle? ownStyle)
            && rects.TryGetValue(id, out Rect ownRect)
            && ownStyle.OverflowHidden
            && !ownStyle.OverflowPropagatedToViewport)
        {
            OverflowClip own = OverflowClip.ForBox(ownRect, ownStyle, tx, ty);
            next = inherited is null ? own : inherited.Intersect(own);
        }

        foreach (NodeId cid in DomTraversal.RenderedChildren(tree, id))
        {
            ResolveClipRects(
                tree,
                cid,
                next?.Clone(),
                tx,
                ty,
                rects,
                styles,
                clipRects,
                translates,
                transform,
                transforms,
                rootFontSize,
                viewport);
        }
    }

    internal static (float X, float Y) ResolvedOwnTranslate(
        LayoutStyle style,
        in Rect rect,
        float rootFontSize,
        (float Width, float Height) viewport)
    {
        Affine2 transform = ResolvedTransformMatrix(style, rect, rootFontSize, viewport);
        return transform.IsTranslation() ? (transform.E, transform.F) : (0f, 0f);
    }

    private static float ResolveTransformLength(
        TransformLength length,
        int axis,
        LayoutStyle style,
        in Rect rect,
        float rootFontSize,
        (float Width, float Height) viewport)
    {
        float basis = axis == 0 ? rect.Width : rect.Height;
        float em = style.FontSize ?? 16f;
        if (length.Expression is { } expression
            && ComputedStyle.ResolveContextualLength(
                expression,
                em,
                rootFontSize,
                viewport.Width / 100f,
                viewport.Height / 100f,
                basis) is { } resolved)
        {
            return resolved;
        }

        return ResolveTranslate(length.Value, basis);
    }

    internal static Affine2 ResolvedTransformMatrix(
        LayoutStyle style,
        in Rect rect,
        float rootFontSize,
        (float Width, float Height) viewport)
    {
        Affine2 matrix = Affine2.Identity;
        if (style.IndividualTranslate is { } translate)
        {
            TransformLength x = new(translate.X, style.IndividualTranslateExpressions[0]);
            TransformLength y = new(translate.Y, style.IndividualTranslateExpressions[1]);
            matrix = matrix.Then(Affine2.Translate(
                ResolveTransformLength(x, 0, style, rect, rootFontSize, viewport),
                ResolveTransformLength(y, 1, style, rect, rootFontSize, viewport)));
        }

        if (style.IndividualRotate is { } angle)
        {
            matrix = matrix.Then(Affine2.Rotate(angle));
        }

        if (style.IndividualScale is { } scale)
        {
            matrix = matrix.Then(Affine2.Scale(scale.X, scale.Y));
        }

        matrix = matrix.Then(ResolvedTransformPropertyMatrix(style, rect, rootFontSize, viewport));
        if (matrix.IsIdentity())
        {
            return matrix;
        }

        (Dimension originX, Dimension originY) = style.TransformOrigin
            ?? (Dimension.Percent(0.5f), Dimension.Percent(0.5f));
        return matrix.Around((
            rect.X + ResolveTranslate(originX, rect.Width),
            rect.Y + ResolveTranslate(originY, rect.Height)));
    }

    /// <summary>
    /// Resolved value of the <c>transform</c> property alone. Individual transform properties
    /// and <c>transform-origin</c> are intentionally excluded: CSSOM serializes them as
    /// separate computed properties.
    /// </summary>
    internal static Affine2 ResolvedTransformPropertyMatrix(
        LayoutStyle style,
        in Rect rect,
        float rootFontSize,
        (float Width, float Height) viewport)
    {
        Affine2 matrix = Affine2.Identity;
        foreach (TransformOp operation in style.TransformOps)
        {
            Affine2 applied = operation switch
            {
                TransformOp.Translate translate => Affine2.Translate(
                    ResolveTransformLength(translate.X, 0, style, rect, rootFontSize, viewport),
                    ResolveTransformLength(translate.Y, 1, style, rect, rootFontSize, viewport)),
                TransformOp.Scale scale => Affine2.Scale(scale.X, scale.Y),
                TransformOp.Rotate rotate => Affine2.Rotate(rotate.Degrees),
                TransformOp.Skew skew => Affine2.Skew(skew.XDegrees, skew.YDegrees),
                TransformOp.Matrix native => native.Value,
                _ => Affine2.Identity,
            };
            matrix = matrix.Then(applied);
        }

        return matrix;
    }

    /// <summary>
    /// Resolve one <c>transform: translate()</c> component to px: a length passes through, a
    /// percentage is taken against <paramref name="basis"/>.
    /// </summary>
    internal static float ResolveTranslate(Dimension d, float basis) => d.Kind switch
    {
        DimensionKind.Px => d.Value,
        DimensionKind.Percent => d.Value * basis,
        DimensionKind.Em or DimensionKind.Rem => d.Value * 16f,
        DimensionKind.Ex => d.Value * 16f * Dimension.ExPerEm,
        DimensionKind.Vw or DimensionKind.Vh or DimensionKind.Vmin or DimensionKind.Vmax => d.Value,
        _ => 0f,
    };

    internal static bool IsViewportOverflowSource(
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles) =>
        styles.TryGetValue(id, out LayoutStyle? style) && style.OverflowPropagatedToViewport;

    internal static void MarkViewportOverflowSource(
        DomTree tree,
        NodeId root,
        Dictionary<NodeId, LayoutStyle> styles)
    {
        if (!DomTraversal.IsLocal(tree, root, "html"))
        {
            return;
        }

        bool rootOwnsOverflow = styles.TryGetValue(root, out LayoutStyle? rootStyle)
            && rootStyle.OverflowHidden;
        if (rootStyle is not null)
        {
            rootStyle.OverflowPropagatedToViewport = true;
        }

        if (rootOwnsOverflow)
        {
            return;
        }

        foreach (NodeId child in tree.Children(root))
        {
            if (DomTraversal.IsLocal(tree, child, "body"))
            {
                if (styles.TryGetValue(child, out LayoutStyle? bodyStyle))
                {
                    bodyStyle.OverflowPropagatedToViewport = true;
                }

                return;
            }
        }
    }
}
