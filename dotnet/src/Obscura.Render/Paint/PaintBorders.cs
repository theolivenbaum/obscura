// Port of the border, outline, and box-shadow painters of crates/obscura-render/src/paint.rs.
using SkiaSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal enum BorderSide
{
    Top,
    Right,
    Bottom,
    Left,
}

internal static class PaintBorders
{
    internal static Sides<BorderStyle> EffectiveBorderStyles(LayoutStyle style)
    {
        Sides<BorderStyle> styles = style.BorderModel.Styles;

        // Preserve the public LayoutStyle contract for embedding code and older renderer tests
        // that construct used `border` edges directly.
        BorderStyle top = style.Border.Top > 0f && !styles.Top.IsVisible() ? BorderStyle.Solid : styles.Top;
        BorderStyle right = style.Border.Right > 0f && !styles.Right.IsVisible() ? BorderStyle.Solid : styles.Right;
        BorderStyle bottom = style.Border.Bottom > 0f && !styles.Bottom.IsVisible() ? BorderStyle.Solid : styles.Bottom;
        BorderStyle left = style.Border.Left > 0f && !styles.Left.IsVisible() ? BorderStyle.Solid : styles.Left;
        return new Sides<BorderStyle>(top, right, bottom, left);
    }

    internal static void PaintCssBorder(
        Pixmap pixmap,
        in Rect rect,
        LayoutStyle style,
        Mask? mask,
        float rasterScale)
    {
        Sides<float> widths = new(
            style.Border.Top,
            style.Border.Right,
            style.Border.Bottom,
            style.Border.Left);
        if (widths.Top <= 0f && widths.Right <= 0f && widths.Bottom <= 0f && widths.Left <= 0f)
        {
            return;
        }

        if (!PaintDomPainter.RectIntersectsPaintSurface(rect, pixmap, rasterScale))
        {
            return;
        }

        RgbaColor current = style.Color ?? new RgbaColor(0, 0, 0, 255);
        Sides<RgbaColor> colors = style.BorderModel.Colors.Map(
            color => color ?? style.BorderColor ?? current);
        Sides<BorderStyle> styles = EffectiveBorderStyles(style);
        ResolvedBorderRadii radii = style.BorderModel.Radii.Resolve(rect.Width, rect.Height);
        bool uniform = widths.Top == widths.Right
            && widths.Right == widths.Bottom
            && widths.Bottom == widths.Left
            && styles.Top == styles.Right
            && styles.Right == styles.Bottom
            && styles.Bottom == styles.Left
            && colors.Top == colors.Right
            && colors.Right == colors.Bottom
            && colors.Bottom == colors.Left;
        if (uniform
            && styles.Top is not (BorderStyle.Inset or BorderStyle.Outset
                or BorderStyle.Groove or BorderStyle.Ridge))
        {
            PaintUniformBorder(pixmap, rect, widths.Top, styles.Top, colors.Top, radii, mask, rasterScale);
            return;
        }

        foreach (BorderSide side in (BorderSide[])[BorderSide.Top, BorderSide.Right, BorderSide.Bottom, BorderSide.Left])
        {
            (float width, BorderStyle lineStyle, RgbaColor color) = side switch
            {
                BorderSide.Top => (widths.Top, styles.Top, colors.Top),
                BorderSide.Right => (widths.Right, styles.Right, colors.Right),
                BorderSide.Bottom => (widths.Bottom, styles.Bottom, colors.Bottom),
                _ => (widths.Left, styles.Left, colors.Left),
            };
            if (width <= 0f || !lineStyle.IsVisible())
            {
                continue;
            }

            switch (lineStyle)
            {
                case BorderStyle.Solid:
                case BorderStyle.Auto:
                    FillSolidBorderSide(pixmap, rect, widths, radii, side, color, mask, rasterScale);
                    break;
                case BorderStyle.Inset:
                case BorderStyle.Outset:
                case BorderStyle.Groove:
                case BorderStyle.Ridge:
                {
                    // CSS's relief styles are two-tone; preserve the directional light source.
                    bool topLeft = side is BorderSide.Top or BorderSide.Left;
                    bool darkSide = lineStyle switch
                    {
                        BorderStyle.Inset or BorderStyle.Groove => topLeft,
                        BorderStyle.Outset or BorderStyle.Ridge => !topLeft,
                        _ => false,
                    };
                    RgbaColor shaded = ShadeBorderColor(color, darkSide ? -0.28f : 0.28f);
                    FillSolidBorderSide(pixmap, rect, widths, radii, side, shaded, mask, rasterScale);
                    break;
                }

                case BorderStyle.Double:
                    if (width < 3f)
                    {
                        FillSolidBorderSide(pixmap, rect, widths, radii, side, color, mask, rasterScale);
                    }
                    else
                    {
                        PaintStraightBorderSide(
                            pixmap, rect, side, width / 3f, width / 6f, color, null, mask, rasterScale);
                        PaintStraightBorderSide(
                            pixmap, rect, side, width / 3f, width * 5f / 6f, color, null, mask, rasterScale);
                    }

                    break;
                case BorderStyle.Dashed:
                case BorderStyle.Dotted:
                    PaintStraightBorderSide(
                        pixmap, rect, side, width, width / 2f, color, lineStyle, mask, rasterScale);
                    break;
                default:
                    break;
            }
        }
    }

    internal static void PaintCssOutline(
        Pixmap pixmap,
        in Rect rect,
        LayoutStyle style,
        Mask? mask,
        float rasterScale)
    {
        float width = style.Outline.UsedWidth();
        if (width <= 0f)
        {
            return;
        }

        float outerExpansion = F32.Max(style.Outline.Offset + width, 0f);
        Rect outlineBounds = new(
            rect.X - outerExpansion,
            rect.Y - outerExpansion,
            rect.Width + (2f * outerExpansion),
            rect.Height + (2f * outerExpansion));
        if (!PaintDomPainter.RectIntersectsPaintSurface(outlineBounds, pixmap, rasterScale))
        {
            return;
        }

        float centerExpansion = style.Outline.Offset + (width / 2f);
        Rect centerRect = new(
            rect.X - centerExpansion,
            rect.Y - centerExpansion,
            rect.Width + (2f * centerExpansion),
            rect.Height + (2f * centerExpansion));
        if (centerRect.Width <= 0f || centerRect.Height <= 0f)
        {
            return;
        }

        ResolvedBorderRadii radii = style.BorderModel.Radii
            .Resolve(rect.Width, rect.Height)
            .Outset(Sides<float>.All(centerExpansion));
        RgbaColor color = style.Outline.Color ?? style.Color ?? new RgbaColor(0, 0, 0, 255);
        StrokeRoundedBorderPath(
            pixmap,
            centerRect,
            width,
            style.Outline.Style,
            color,
            radii,
            mask,
            rasterScale);
    }

    private static void PaintUniformBorder(
        Pixmap pixmap,
        in Rect rect,
        float width,
        BorderStyle lineStyle,
        RgbaColor color,
        ResolvedBorderRadii radii,
        Mask? mask,
        float rasterScale)
    {
        if (width <= 0f || !lineStyle.IsVisible())
        {
            return;
        }

        if (lineStyle == BorderStyle.Double && width >= 3f)
        {
            float stripe = width / 3f;
            foreach (float inset in (float[])[stripe / 2f, width - (stripe / 2f)])
            {
                Rect stripeRect = new(
                    rect.X + inset,
                    rect.Y + inset,
                    rect.Width - (2f * inset),
                    rect.Height - (2f * inset));
                if (stripeRect.Width > 0f && stripeRect.Height > 0f)
                {
                    StrokeRoundedBorderPath(
                        pixmap,
                        stripeRect,
                        stripe,
                        BorderStyle.Solid,
                        color,
                        radii.Inset(Sides<float>.All(inset)),
                        mask,
                        rasterScale);
                }
            }

            return;
        }

        float center = width / 2f;
        Rect centerRect = new(
            rect.X + center,
            rect.Y + center,
            rect.Width - width,
            rect.Height - width);
        if (centerRect.Width <= 0f || centerRect.Height <= 0f)
        {
            return;
        }

        StrokeRoundedBorderPath(
            pixmap,
            centerRect,
            width,
            lineStyle,
            color,
            radii.Inset(Sides<float>.All(center)),
            mask,
            rasterScale);
    }

    private static void StrokeRoundedBorderPath(
        Pixmap pixmap,
        in Rect centerRect,
        float width,
        BorderStyle lineStyle,
        RgbaColor color,
        ResolvedBorderRadii radii,
        Mask? mask,
        float rasterScale)
    {
        SKPath? path = PaintClips.RoundedRectPathRadii(
            centerRect.X,
            centerRect.Y,
            centerRect.Width,
            centerRect.Height,
            radii);
        if (path is null)
        {
            return;
        }

        using SKPath owned = path;
        using SKPaint paint = new()
        {
            Color = PaintColor.ToSk(color),
            IsAntialias = !radii.IsZero(),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
        };
        switch (lineStyle)
        {
            case BorderStyle.Dashed:
                paint.PathEffect = SKPathEffect.CreateDash([width * 3f, width * 3f], 0f);
                break;
            case BorderStyle.Dotted:
                paint.PathEffect = SKPathEffect.CreateDash([0f, width * 2f], 0f);
                paint.StrokeCap = SKStrokeCap.Round;
                break;
            default:
                break;
        }

        Surface.StrokePath(pixmap, owned, paint, Surface.RasterTransform(rasterScale), mask);
        paint.PathEffect?.Dispose();
    }

    private static void FillSolidBorderSide(
        Pixmap pixmap,
        in Rect rect,
        Sides<float> widths,
        ResolvedBorderRadii radii,
        BorderSide side,
        RgbaColor color,
        Mask? mask,
        float rasterScale)
    {
        SKPath? path = SolidBorderSidePath(rect, widths, radii, side);
        if (path is null)
        {
            return;
        }

        using SKPath owned = path;
        Surface.FillPath(
            pixmap,
            owned,
            color,
            !radii.IsZero(),
            Surface.RasterTransform(rasterScale),
            mask);
    }

    private static (float X, float Y) ArcPoint(
        (float X, float Y) center,
        (float X, float Y) radius,
        float degrees)
    {
        float angle = F32.ToRadians(degrees);
        return (center.X + (radius.X * MathF.Cos(angle)), center.Y + (radius.Y * MathF.Sin(angle)));
    }

    private static void AppendArc(
        SKPathBuilder path,
        (float X, float Y) center,
        (float X, float Y) radius,
        float start,
        float end)
    {
        for (int step = 0; step <= 4; step++)
        {
            float angle = start + ((end - start) * step / 4f);
            (float x, float y) = ArcPoint(center, radius, angle);
            path.LineTo(x, y);
        }
    }

    private static SKPath? SolidBorderSidePath(
        in Rect rect,
        Sides<float> widths,
        ResolvedBorderRadii outer,
        BorderSide side)
    {
        ResolvedBorderRadii inner = outer.Inset(widths);
        float x = rect.X;
        float y = rect.Y;
        float right = x + rect.Width;
        float bottom = y + rect.Height;
        float ix = x + widths.Left;
        float iy = y + widths.Top;
        float iright = right - widths.Right;
        float ibottom = bottom - widths.Bottom;
        (float X, float Y)[] outerCenters =
        [
            (x + outer.TopLeft.X, y + outer.TopLeft.Y),
            (right - outer.TopRight.X, y + outer.TopRight.Y),
            (right - outer.BottomRight.X, bottom - outer.BottomRight.Y),
            (x + outer.BottomLeft.X, bottom - outer.BottomLeft.Y),
        ];
        (float X, float Y)[] innerCenters =
        [
            (ix + inner.TopLeft.X, iy + inner.TopLeft.Y),
            (iright - inner.TopRight.X, iy + inner.TopRight.Y),
            (iright - inner.BottomRight.X, ibottom - inner.BottomRight.Y),
            (ix + inner.BottomLeft.X, ibottom - inner.BottomLeft.Y),
        ];
        (float X, float Y)[] outerRadii =
            [outer.TopLeft, outer.TopRight, outer.BottomRight, outer.BottomLeft];
        (float X, float Y)[] innerRadii =
            [inner.TopLeft, inner.TopRight, inner.BottomRight, inner.BottomLeft];
        (float A, float B, int OuterA, int OuterB, int InnerB, int InnerA) shape = side switch
        {
            BorderSide.Top => (225f, 270f, 0, 1, 1, 0),
            BorderSide.Right => (315f, 360f, 1, 2, 2, 1),
            BorderSide.Bottom => (45f, 90f, 2, 3, 3, 2),
            _ => (135f, 180f, 3, 0, 0, 3),
        };
        float secondStart = side switch
        {
            BorderSide.Top => 270f,
            BorderSide.Right => 0f,
            BorderSide.Bottom => 90f,
            _ => 180f,
        };
        float secondEnd = side switch
        {
            BorderSide.Top => 315f,
            BorderSide.Right => 45f,
            BorderSide.Bottom => 135f,
            _ => 225f,
        };

        using SKPathBuilder builder = new();
        (float sx, float sy) = ArcPoint(outerCenters[shape.OuterA], outerRadii[shape.OuterA], shape.A);
        builder.MoveTo(sx, sy);
        AppendArc(builder, outerCenters[shape.OuterA], outerRadii[shape.OuterA], shape.A, shape.B);
        AppendArc(builder, outerCenters[shape.OuterB], outerRadii[shape.OuterB], secondStart, secondEnd);
        AppendArc(builder, innerCenters[shape.InnerB], innerRadii[shape.InnerB], secondEnd, secondStart);
        AppendArc(builder, innerCenters[shape.InnerA], innerRadii[shape.InnerA], shape.B, shape.A);
        builder.Close();
        return builder.Detach();
    }

    private static void PaintStraightBorderSide(
        Pixmap pixmap,
        in Rect rect,
        BorderSide side,
        float strokeWidth,
        float inward,
        RgbaColor color,
        BorderStyle? pattern,
        Mask? mask,
        float rasterScale)
    {
        using SKPathBuilder builder = new();
        switch (side)
        {
            case BorderSide.Top:
                builder.MoveTo(rect.X, rect.Y + inward);
                builder.LineTo(rect.X + rect.Width, rect.Y + inward);
                break;
            case BorderSide.Right:
                builder.MoveTo(rect.X + rect.Width - inward, rect.Y);
                builder.LineTo(rect.X + rect.Width - inward, rect.Y + rect.Height);
                break;
            case BorderSide.Bottom:
                builder.MoveTo(rect.X + rect.Width, rect.Y + rect.Height - inward);
                builder.LineTo(rect.X, rect.Y + rect.Height - inward);
                break;
            default:
                builder.MoveTo(rect.X + inward, rect.Y + rect.Height);
                builder.LineTo(rect.X + inward, rect.Y);
                break;
        }

        using SKPath path = builder.Detach();
        using SKPaint paint = new()
        {
            Color = PaintColor.ToSk(color),
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
        };
        if (pattern == BorderStyle.Dashed)
        {
            paint.PathEffect = SKPathEffect.CreateDash([strokeWidth * 3f, strokeWidth * 3f], 0f);
        }
        else if (pattern == BorderStyle.Dotted)
        {
            paint.PathEffect = SKPathEffect.CreateDash([0f, strokeWidth * 2f], 0f);
            paint.StrokeCap = SKStrokeCap.Round;
        }

        Surface.StrokePath(pixmap, path, paint, Surface.RasterTransform(rasterScale), mask);
        paint.PathEffect?.Dispose();
    }

    internal static RgbaColor ShadeBorderColor(RgbaColor color, float amount)
    {
        float target = amount >= 0f ? 255f : 0f;
        float factor = F32.Min(MathF.Abs(amount), 1f);
        byte Channel(byte value) =>
            (byte)Math.Clamp(F32.Round(value + ((target - value) * factor)), 0f, 255f);

        return new RgbaColor(Channel(color.R), Channel(color.G), Channel(color.B), color.A);
    }

    /// <summary>
    /// Paint an outset <c>box-shadow</c> layer behind the element's own box.
    /// </summary>
    /// <remarks>
    /// The blur is approximated by nested rounded rects from a solid core out to the blur
    /// radius, each at a fraction of the shadow alpha so source-over accumulation ramps the
    /// coverage from full at the core to near-zero at the outer edge.
    /// </remarks>
    internal static void PaintBoxShadow(
        Pixmap pixmap,
        BoxShadow shadow,
        in Rect rect,
        BorderRadii borderRadius,
        Mask? ancestorClip)
    {
        if (shadow.Inset || shadow.Color.A == 0)
        {
            return;
        }

        float spread = shadow.Spread;
        float x0 = rect.X + shadow.OffsetX - spread;
        float y0 = rect.Y + shadow.OffsetY - spread;
        float w0 = rect.Width + (2f * spread);
        float h0 = rect.Height + (2f * spread);
        if (w0 <= 0f || h0 <= 0f)
        {
            return;
        }

        ResolvedBorderRadii borderRadii = borderRadius.Resolve(rect.Width, rect.Height);
        (float X, float Y) radius = borderRadii.TopLeft;
        float rx0 = F32.Max(radius.X + spread, 0f);
        float ry0 = F32.Max(radius.Y + spread, 0f);
        float blur = F32.Max(shadow.Blur, 0f);
        Rect shadowBounds = new(
            x0 - blur,
            y0 - blur,
            w0 + (2f * blur),
            h0 + (2f * blur));

        // Shadows disable the native >1x path, so their paint coordinate space is one CSS pixel
        // per surface pixel.
        if (!PaintDomPainter.RectIntersectsPaintSurface(shadowBounds, pixmap, 1f))
        {
            return;
        }

        int left = (int)F32.Max(MathF.Floor(shadowBounds.X), 0f);
        int top = (int)F32.Max(MathF.Floor(shadowBounds.Y), 0f);
        int right = (int)F32.Min(MathF.Ceiling(shadowBounds.X + shadowBounds.Width), pixmap.Width);
        int bottom = (int)F32.Min(MathF.Ceiling(shadowBounds.Y + shadowBounds.Height), pixmap.Height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        Pixmap? shadowPixmap = Pixmap.New((uint)(right - left), (uint)(bottom - top));
        if (shadowPixmap is null)
        {
            return;
        }

        using Pixmap owned = shadowPixmap;
        Rect localRect = rect with { X = rect.X - left, Y = rect.Y - top };
        Mask? shadowMask = PaintClips.RoundedBoxClipMaskRadii(
            owned.Width,
            owned.Height,
            localRect,
            borderRadii);
        if (shadowMask is null)
        {
            return;
        }

        shadowMask.Invert();
        RgbaColor color = shadow.Color;
        if (blur < 0.5f)
        {
            // No blur: a single crisp, offset (and spread) rounded rect.
            FillShadowRect(owned, x0 - left, y0 - top, w0, h0, rx0, ry0, color, shadowMask);
        }
        else
        {
            uint steps = (uint)Math.Clamp((int)MathF.Ceiling(blur), 2, 24);

            // Per-layer alpha chosen so `steps` source-over composites reach the target alpha
            // at the core: 1 - (1 - a)^steps == A  =>  a = 1 - (1 - A)^(1/steps).
            float aFrac = color.A / 255f;
            float per = 1f - MathF.Pow(1f - aFrac, 1f / steps);
            byte layerAlpha = (byte)Math.Clamp(F32.Round(per * 255f), 1f, 255f);
            RgbaColor layerColor = new(color.R, color.G, color.B, layerAlpha);
            for (uint j = 0; j < steps; j++)
            {
                float e = blur * j / (steps - 1);
                FillShadowRect(
                    owned,
                    x0 - e - left,
                    y0 - e - top,
                    w0 + (2f * e),
                    h0 + (2f * e),
                    rx0 + e,
                    ry0 + e,
                    layerColor,
                    shadowMask);
            }
        }

        Surface.DrawPixmap(pixmap, left, top, owned, 1f, false, Affine2.Identity, ancestorClip);
    }

    /// <summary>Fill one (possibly rounded) shadow rectangle with a flat color.</summary>
    private static void FillShadowRect(
        Pixmap pixmap,
        float x,
        float y,
        float w,
        float h,
        float radiusX,
        float radiusY,
        RgbaColor color,
        Mask? mask)
    {
        if (w <= 0f || h <= 0f || color.A == 0)
        {
            return;
        }

        SKPath? path;
        if (radiusX > 0.5f && radiusY > 0.5f)
        {
            path = PaintClips.RoundedRectPath(x, y, w, h, radiusX, radiusY);
        }
        else
        {
            using SKPathBuilder builder = new();
            builder.AddRect(new SKRect(x, y, x + w, y + h));
            path = builder.Detach();
        }

        if (path is null)
        {
            return;
        }

        using SKPath owned = path;
        Surface.FillPath(pixmap, owned, color, true, Affine2.Identity, mask);
    }
}
