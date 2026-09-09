// Port of `paint_inline_fragment_decorations`, `paint_in_flow_generated_box` and
// `paint_positioned_pseudo` in crates/obscura-render/src/paint.rs.
using Obscura.Render.Layout;
using SkiaSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal static class PaintInline
{
    /// <summary>
    /// Paint an ordinary inline's sliced border boxes instead of its multiline bounding union.
    /// </summary>
    internal static void PaintInlineFragmentDecorations(
        Pixmap pixmap,
        IReadOnlyList<Rect> fragments,
        (float X, float Y) offset,
        in Rect union,
        LayoutStyle style,
        Rect? clip,
        Mask? ancestorClipMask,
        float rootFontSize,
        (float Width, float Height) viewport,
        string? baseUrl,
        RenderResourceCache imageCache,
        float rasterScale)
    {
        Rect unionRect = union;
        LayoutStyle fragmentStyle = style.Clone();
        for (int index = 0; index < fragments.Count; index++)
        {
            Rect fragment = fragments[index] with
            {
                X = fragments[index].X + offset.X,
                Y = fragments[index].Y + offset.Y,
            };
            if (clip is { } clipRect && fragment.Intersect(clipRect) is null)
            {
                continue;
            }

            bool first = index == 0;
            bool last = index + 1 == fragments.Count;
            fragmentStyle.Border = fragmentStyle.Border with
            {
                Left = style.Border.Left,
                Right = style.Border.Right,
            };
            fragmentStyle.BorderModel = fragmentStyle.BorderModel with { Radii = style.BorderModel.Radii };
            if (!first)
            {
                fragmentStyle.Border = fragmentStyle.Border with { Left = 0f };
                fragmentStyle.BorderModel = fragmentStyle.BorderModel with
                {
                    Radii = fragmentStyle.BorderModel.Radii with
                    {
                        TopLeft = default,
                        BottomLeft = default,
                    },
                };
            }

            if (!last)
            {
                fragmentStyle.Border = fragmentStyle.Border with { Right = 0f };
                fragmentStyle.BorderModel = fragmentStyle.BorderModel with
                {
                    Radii = fragmentStyle.BorderModel.Radii with
                    {
                        TopRight = default,
                        BottomRight = default,
                    },
                };
            }

            Rect ink = PaintDomPainter.NonTextInkBounds(fragment, fragmentStyle);
            Rect? visibleInk = clip is { } clipBox ? ink.Intersect(clipBox) : ink;
            if (visibleInk is not { } inkRect
                || !PaintDomPainter.RectIntersectsPaintSurface(inkRect, pixmap, rasterScale))
            {
                continue;
            }

            ResolvedBorderRadii radius = fragmentStyle.BorderModel.Radii
                .Resolve(fragment.Width, fragment.Height);
            Mask? clipPathMask = fragmentStyle.ClipPath is { } polygon
                ? PaintClips.PolygonClipMask(
                    pixmap.Width,
                    pixmap.Height,
                    polygon,
                    fragment,
                    fragmentStyle.FontSize ?? 16f,
                    rootFontSize,
                    viewport)
                : null;
            BackgroundGeometry background = PaintGradients.BackgroundGeometryFor(fragment, fragmentStyle);

            // Keep the positioning area stable across fragments.
            Rect backgroundOrigin = PaintGradients.BackgroundGeometryFor(unionRect, style).OriginRect;
            SKPath? backgroundPath = PaintGradients.BackgroundClipPath(background);
            Mask? elementClipMask = PaintGradients.BackgroundExtraClip(ancestorClipMask, clipPathMask);
            Mask? backgroundMask = elementClipMask?.Clone();

            // backdrop-filter reads the surface as it stands before this element paints
            // anything of its own, so it runs ahead of the shadow.
            if (fragmentStyle.BackdropBlur is { } fragmentBackdropSigma)
            {
                PaintFilters.PaintBackdropFilter(
                    pixmap,
                    fragment,
                    fragmentStyle.BorderModel.Radii,
                    fragmentBackdropSigma,
                    ancestorClipMask);
            }

            if (fragmentStyle.BoxShadow is { } shadow)
            {
                PaintBorders.PaintBoxShadow(
                    pixmap,
                    shadow,
                    fragment,
                    fragmentStyle.BorderModel.Radii,
                    ancestorClipMask);
            }

            if (fragmentStyle.MaskImage is null && !fragmentStyle.BackgroundClipText)
            {
                if (fragmentStyle.BackgroundColor is { } color && backgroundPath is not null)
                {
                    Surface.FillPath(
                        pixmap,
                        backgroundPath,
                        color,
                        !background.ClipRadii.IsZero(),
                        Surface.RasterTransform(rasterScale),
                        backgroundMask);
                }

                if (fragmentStyle.BackgroundGradientLayers.Count > 0)
                {
                    if (backgroundPath is not null)
                    {
                        PaintGradients.PaintBackgroundGradientLayers(
                            pixmap,
                            backgroundPath,
                            backgroundOrigin,
                            background.ClipRect,
                            background.ClipRadii,
                            fragmentStyle,
                            rootFontSize,
                            viewport,
                            backgroundMask,
                            rasterScale);
                    }
                }
                else
                {
                    if (fragmentStyle.BackgroundRadialGradient is { } radial && backgroundPath is not null)
                    {
                        PaintGradients.PaintRadialGradient(
                            pixmap,
                            backgroundPath,
                            backgroundOrigin,
                            radial.Center,
                            radial.Stops,
                            // The legacy single-gradient tuple carries no authored stop strings; the
                            // percentages already in Stops are the whole story.
                            [],
                            fragmentStyle.BackgroundRadialGradientGeometry,
                            fragmentStyle.FontSize ?? 16f,
                            rootFontSize,
                            viewport,
                            backgroundMask,
                            rasterScale);
                    }

                    if (fragmentStyle.BackgroundConicGradient is { } conic)
                    {
                        PaintGradients.PaintConicGradientSampled(
                            pixmap,
                            background.ClipRect,
                            backgroundOrigin,
                            background.ClipRadii,
                            conic.Angle,
                            conic.Center,
                            conic.Stops,
                            backgroundMask);
                    }

                    if (fragmentStyle.BackgroundGradient is { } linear && backgroundPath is not null)
                    {
                        PaintGradients.PaintLinearGradient(
                            pixmap,
                            backgroundPath,
                            backgroundOrigin,
                            linear.Angle,
                            linear.Stops,
                            backgroundMask,
                            rasterScale);
                    }
                }
            }

            if (fragmentStyle.MaskImage is { } maskUrl)
            {
                RgbaColor fill = fragmentStyle.BackgroundColor
                    ?? fragmentStyle.Color
                    ?? new RgbaColor(0, 0, 0, 255);
                PaintImages.PaintMask(
                    maskUrl,
                    baseUrl,
                    fragment,
                    radius,
                    fill,
                    fragmentStyle.BackgroundRadialGradient,
                    fragmentStyle.BackgroundRadialGradientGeometry,
                    fragmentStyle.FontSize ?? 16f,
                    rootFontSize,
                    viewport,
                    fragmentStyle.BackgroundGradient,
                    fragmentStyle.BackgroundConicGradient,
                    fragmentStyle.MaskSize,
                    fragmentStyle.MaskRepeat,
                    elementClipMask,
                    pixmap,
                    imageCache);
            }
            else if (fragmentStyle.BackgroundImage is { } backgroundUrl)
            {
                Rect? imageRect = PaintImages.BackgroundImageRect(
                    backgroundUrl,
                    baseUrl,
                    backgroundOrigin,
                    fragmentStyle.BackgroundSize,
                    fragmentStyle.BackgroundSizeExpression,
                    fragmentStyle.BackgroundSizeFit,
                    fragmentStyle.BackgroundPosition,
                    fragmentStyle.FontSize ?? 16f,
                    rootFontSize,
                    viewport,
                    imageCache);
                if (imageRect is { } img)
                {
                    PaintImages.PaintImage(
                        backgroundUrl,
                        baseUrl,
                        img,
                        background.ClipRect,
                        ObjectFit.Fill,
                        ObjectPosition.Default,
                        pixmap,
                        imageCache,
                        null,
                        null,
                        background.ClipRadii,
                        backgroundMask);
                }
            }

            // CSS Backgrounds 3 paints an inset shadow over the background and under the
            // border, so it cannot ride along with the outset pass above.
            if (fragmentStyle.BoxShadow is { } insetShadow)
            {
                PaintBorders.PaintInsetBoxShadow(
                    pixmap, insetShadow, fragment, fragmentStyle.BorderModel.Radii, elementClipMask);
            }

            PaintBorders.PaintCssBorder(pixmap, fragment, fragmentStyle, elementClipMask, rasterScale);
            backgroundPath?.Dispose();
        }
    }
}

internal static class PaintGenerated
{
    internal static void PaintInFlowGeneratedBox(
        Pixmap pixmap,
        GeneratedBox generated,
        DomLayout laid,
        ScrollPaintState scrollState,
        (float Width, float Height) viewport,
        float rootFontSize,
        string? baseUrl,
        RenderResourceCache imageCache,
        float rasterScale)
    {
        if (!laid.Styles.TryGetValue(generated.Host, out LayoutStyle? hostStyle))
        {
            return;
        }

        LayoutStyle? style = generated.Kind == GeneratedBoxKind.Before
            ? hostStyle.BeforePseudo
            : hostStyle.AfterPseudo;
        if (style is null || style.EffectivelyInvisible)
        {
            return;
        }

        (float ox, float oy) = scrollState.TranslationFor(laid, generated.Host);
        Rect rect = new(
            generated.Rect.X + ox,
            generated.Rect.Y + oy,
            generated.Rect.Width,
            generated.Rect.Height);
        OverflowClip? overflowClip = scrollState.DescendantOverflowClipFor(laid, generated.Host);
        Rect? clip = overflowClip?.ViewportRect(scrollState.SurfaceExtent ?? viewport);
        Rect? visibleRect = clip is { } clipRect ? rect.Intersect(clipRect) : rect;
        if (visibleRect is not { } visible || visible.Width <= 0f || visible.Height <= 0f)
        {
            return;
        }

        Rect ink = PaintDomPainter.NonTextInkBounds(rect, style);
        Rect? visibleInk = clip is { } inkClip ? ink.Intersect(inkClip) : ink;
        if (visibleInk is not { } inkRect
            || !PaintDomPainter.RectIntersectsPaintSurface(inkRect, pixmap, rasterScale))
        {
            return;
        }

        Mask? ancestorClipMask = overflowClip is null
            ? null
            : PaintClips.OverflowClipMask(
                pixmap.Width,
                pixmap.Height,
                overflowClip,
                scrollState.SurfaceExtent ?? viewport);

        // backdrop-filter reads the surface as it stands before this element paints
        // anything of its own, so it runs ahead of the shadow.
        if (style.BackdropBlur is { } backdropSigma)
        {
            PaintFilters.PaintBackdropFilter(
                pixmap, rect, style.BorderModel.Radii, backdropSigma, ancestorClipMask);
        }

        if (style.BoxShadow is { } shadow)
        {
            PaintBorders.PaintBoxShadow(pixmap, shadow, rect, style.BorderModel.Radii, ancestorClipMask);
        }

        ResolvedBorderRadii radius = style.BorderModel.Radii.Resolve(rect.Width, rect.Height);
        Mask? clipPathMask = style.ClipPath is { } polygon
            ? PaintClips.PolygonClipMask(
                pixmap.Width,
                pixmap.Height,
                polygon,
                rect,
                style.FontSize ?? 16f,
                rootFontSize,
                viewport)
            : null;
        BackgroundGeometry background = PaintGradients.BackgroundGeometryFor(rect, style);
        SKPath? backgroundPath = PaintGradients.BackgroundClipPath(background);
        Mask? elementClipMask = PaintGradients.BackgroundExtraClip(ancestorClipMask, clipPathMask);
        Mask? backgroundMask = elementClipMask?.Clone();

        PaintBoxBackground(
            pixmap,
            style,
            background,
            backgroundPath,
            background.OriginRect,
            rootFontSize,
            viewport,
            backgroundMask,
            rasterScale,
            antiAliasSolid: true);

        PaintBoxForeground(
            pixmap,
            style,
            visible,
            radius,
            background,
            baseUrl,
            rootFontSize,
            viewport,
            elementClipMask,
            backgroundMask,
            imageCache,
            style.FontSize ?? 16f);

        // CSS Backgrounds 3 paints an inset shadow over the background and under the
        // border, so it cannot ride along with the outset pass above.
        if (style.BoxShadow is { } insetShadow)
        {
            PaintBorders.PaintInsetBoxShadow(
                pixmap, insetShadow, rect, style.BorderModel.Radii, elementClipMask);
        }

        PaintBorders.PaintCssBorder(pixmap, rect, style, elementClipMask, rasterScale);
        PaintBorders.PaintCssOutline(pixmap, rect, style, elementClipMask, rasterScale);
        backgroundPath?.Dispose();
    }

    internal static void PaintPositionedPseudo(
        TextEngine textEngine,
        Pixmap pixmap,
        LayoutStyle style,
        in Rect containingBlock,
        in Rect staticPositionRect,
        (float Width, float Height) viewport,
        float rootFontSize,
        (float Width, float Height) clipExtent,
        OverflowClip? ancestorOverflowClip,
        string? baseUrl,
        RenderResourceCache imageCache,
        float rasterScale)
    {
        if (style.Position != Layout.Position.Absolute)
        {
            return;
        }

        float em = style.FontSize ?? 16f;
        float? Resolve(Dimension dimension, float basis)
        {
            Dimension resolved = dimension.Resolve(
                em,
                rootFontSize,
                viewport.Width / 100f,
                viewport.Height / 100f);
            return resolved.Kind switch
            {
                DimensionKind.Px => resolved.Value,
                DimensionKind.Percent => resolved.Value * basis,
                _ => null,
            };
        }

        float? top = style.Inset[0] is { } t ? Resolve(t, containingBlock.Height) : null;
        float? right = style.Inset[1] is { } r ? Resolve(r, containingBlock.Width) : null;
        float? bottom = style.Inset[2] is { } b ? Resolve(b, containingBlock.Height) : null;
        float? left = style.Inset[3] is { } l ? Resolve(l, containingBlock.Width) : null;

        // Generated text supplies the shrink-to-fit dimensions of an absolutely positioned
        // pseudo whose width and/or height is auto.
        int? generatedItem = style.BeforeContent is { Length: > 0 } content
            ? textEngine.PushGeneratedText(content, style)
            : null;
        (float Width, float Height)? generatedIntrinsic = generatedItem is { } item
            ? textEngine.Measure(item, null)
            : null;
        float? width = Resolve(style.Width, containingBlock.Width)
            ?? (left is { } l2 && right is { } r2 ? containingBlock.Width - l2 - r2 : (float?)null)
            ?? generatedIntrinsic?.Width;
        float? height = Resolve(style.Height, containingBlock.Height)
            ?? (top is { } t2 && bottom is { } b2 ? containingBlock.Height - t2 - b2 : (float?)null)
            ?? generatedIntrinsic?.Height;
        if (width is not { } usedWidth || height is not { } usedHeight)
        {
            return;
        }

        if (usedWidth <= 0f || usedHeight <= 0f)
        {
            return;
        }

        float x = left is { } leftInset
            ? containingBlock.X + leftInset
            : right is { } rightInset
                ? containingBlock.X + containingBlock.Width - rightInset - usedWidth
                : staticPositionRect.X;
        float y = top is { } topInset
            ? containingBlock.Y + topInset
            : bottom is { } bottomInset
                ? containingBlock.Y + containingBlock.Height - bottomInset - usedHeight
                : staticPositionRect.Y;
        Rect rect = new(x, y, usedWidth, usedHeight);

        Rect? ancestorClip = ancestorOverflowClip?.ViewportRect(clipExtent);
        Rect? visibleRect = ancestorClip is { } clipRect ? rect.Intersect(clipRect) : rect;
        if (visibleRect is not { } visible)
        {
            return;
        }

        if (!PaintDomPainter.RectIntersectsPaintSurface(
                PaintDomPainter.NonTextInkBounds(rect, style),
                pixmap,
                rasterScale))
        {
            return;
        }

        Mask? ancestorClipMask = ancestorOverflowClip is null
            ? null
            : PaintClips.OverflowClipMask(pixmap.Width, pixmap.Height, ancestorOverflowClip, clipExtent);
        ResolvedBorderRadii radius = style.BorderModel.Radii.Resolve(rect.Width, rect.Height);
        Mask? clipPathMask = style.ClipPath is { } polygon
            ? PaintClips.PolygonClipMask(
                pixmap.Width,
                pixmap.Height,
                polygon,
                rect,
                em,
                rootFontSize,
                viewport)
            : null;
        BackgroundGeometry background = PaintGradients.BackgroundGeometryFor(rect, style);
        SKPath? backgroundPath = PaintGradients.BackgroundClipPath(background);
        Mask? elementClipMask = PaintGradients.BackgroundExtraClip(ancestorClipMask, clipPathMask);
        Mask? backgroundMask = elementClipMask?.Clone();

        PaintBoxBackground(
            pixmap,
            style,
            background,
            backgroundPath,
            background.OriginRect,
            rootFontSize,
            viewport,
            backgroundMask,
            rasterScale,
            antiAliasSolid: false);

        PaintBoxForeground(
            pixmap,
            style,
            visible,
            radius,
            background,
            baseUrl,
            rootFontSize,
            viewport,
            elementClipMask,
            backgroundMask,
            imageCache,
            em);

        PaintBorders.PaintCssBorder(pixmap, rect, style, elementClipMask, rasterScale);
        PaintBorders.PaintCssOutline(pixmap, rect, style, elementClipMask, rasterScale);
        backgroundPath?.Dispose();

        if (generatedItem is { } textItem)
        {
            (float textWidth, float textHeight) = generatedIntrinsic ?? (0f, 0f);

            // `text-align` positions inline content within the pseudo's content box, and is
            // independent of flex/grid `justify-content`.
            float textX = style.Display == Display.Flex
                ? style.JustifyContent?.Keyword switch
                {
                    AlignContentKeyword.Center => rect.X + ((rect.Width - textWidth) / 2f),
                    AlignContentKeyword.FlexEnd or AlignContentKeyword.End =>
                        rect.X + rect.Width - style.Padding.Right - textWidth,
                    _ => rect.X + style.Padding.Left,
                }
                : style.TextAlign?.Keyword switch
                {
                    AlignItemsKeyword.Center => rect.X + ((rect.Width - textWidth) / 2f),
                    AlignItemsKeyword.FlexEnd or AlignItemsKeyword.End =>
                        rect.X + rect.Width - style.Padding.Right - textWidth,
                    _ => rect.X + style.Padding.Left,
                };
            float textY = style.AlignItems?.Keyword switch
            {
                AlignItemsKeyword.Center => rect.Y + ((rect.Height - textHeight) / 2f),
                AlignItemsKeyword.FlexEnd or AlignItemsKeyword.End =>
                    rect.Y + rect.Height - style.Padding.Bottom - textHeight,
                _ => rect.Y + style.Padding.Top,
            };
            textEngine.Finalize(textItem, (textX, textY), textWidth, visible);
            textEngine.PaintItemWithClipMaskScaled(
                textItem,
                pixmap,
                (0f, 0f),
                visible,
                elementClipMask,
                rasterScale);
        }
    }

    private static void PaintBoxBackground(
        Pixmap pixmap,
        LayoutStyle style,
        BackgroundGeometry background,
        SKPath? backgroundPath,
        in Rect originRect,
        float rootFontSize,
        (float Width, float Height) viewport,
        Mask? backgroundMask,
        float rasterScale,
        bool antiAliasSolid)
    {
        if (style.MaskImage is not null || style.BackgroundClipText)
        {
            return;
        }

        if (style.BackgroundColor is { } color && backgroundPath is not null)
        {
            Surface.FillPath(
                pixmap,
                backgroundPath,
                color,
                antiAliasSolid && !background.ClipRadii.IsZero(),
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
                    originRect,
                    background.ClipRect,
                    background.ClipRadii,
                    style,
                    rootFontSize,
                    viewport,
                    backgroundMask,
                    rasterScale);
            }

            return;
        }

        if (style.BackgroundRadialGradient is { } radial && backgroundPath is not null)
        {
            PaintGradients.PaintRadialGradient(
                pixmap,
                backgroundPath,
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
                backgroundMask,
                rasterScale);
        }

        if (style.BackgroundConicGradient is { } conic)
        {
            PaintGradients.PaintConicGradientSampled(
                pixmap,
                background.ClipRect,
                originRect,
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
                originRect,
                linear.Angle,
                linear.Stops,
                backgroundMask,
                rasterScale);
        }
    }

    private static void PaintBoxForeground(
        Pixmap pixmap,
        LayoutStyle style,
        in Rect visible,
        ResolvedBorderRadii radius,
        BackgroundGeometry background,
        string? baseUrl,
        float rootFontSize,
        (float Width, float Height) viewport,
        Mask? elementClipMask,
        Mask? backgroundMask,
        RenderResourceCache imageCache,
        float em)
    {
        if (style.MaskImage is { } maskUrl)
        {
            RgbaColor fill = style.BackgroundColor ?? style.Color ?? new RgbaColor(0, 0, 0, 255);
            PaintImages.PaintMask(
                maskUrl,
                baseUrl,
                visible,
                radius,
                fill,
                style.BackgroundRadialGradient,
                style.BackgroundRadialGradientGeometry,
                em,
                rootFontSize,
                viewport,
                style.BackgroundGradient,
                style.BackgroundConicGradient,
                style.MaskSize,
                style.MaskRepeat,
                elementClipMask,
                pixmap,
                imageCache);
            return;
        }

        if (style.BackgroundImage is { } backgroundUrl)
        {
            Rect? imageRect = PaintImages.BackgroundImageRect(
                backgroundUrl,
                baseUrl,
                background.OriginRect,
                style.BackgroundSize,
                style.BackgroundSizeExpression,
                style.BackgroundSizeFit,
                style.BackgroundPosition,
                em,
                rootFontSize,
                viewport,
                imageCache);
            if (imageRect is { } img)
            {
                PaintImages.PaintImage(
                    backgroundUrl,
                    baseUrl,
                    img,
                    background.ClipRect,
                    ObjectFit.Fill,
                    ObjectPosition.Default,
                    pixmap,
                    imageCache,
                    null,
                    null,
                    background.ClipRadii,
                    backgroundMask);
            }
        }
    }
}
