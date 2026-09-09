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
    /// The blur falloff comes from <see cref="FillShadowRamp"/>, shared with the inset
    /// path. Inset shadows are painted by <see cref="PaintInsetBoxShadow"/> instead,
    /// later in the box's paint order.
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
        FillShadowRamp(
            owned,
            x0 - left,
            y0 - top,
            w0,
            h0,
            rx0,
            ry0,
            blur,
            shadow.Color,
            shadowMask);

        Surface.DrawPixmap(pixmap, left, top, owned, 1f, false, Affine2.Identity, ancestorClip);
    }

    /// <summary>Lay one shadow shape's gaussian ramp into <paramref name="pixmap"/>.</summary>
    /// <remarks>
    /// The blur is a gaussian of standard deviation blur/2 applied to the shape, so
    /// coverage is <c>A * Phi(-d / sigma)</c> at signed distance <c>d</c> outside the edge:
    /// full alpha well inside, half at the edge itself, ~0 well outside. Layers march
    /// inward from 2.5 sigma outside the shape, each adding only the coverage the ones
    /// outside it have not already laid down, followed by a solid core inside the ramp.
    /// 2.5 sigma is where the remaining coverage (0.6%) is the last step an 8-bit alpha
    /// can represent, and roughly one layer per pixel of ramp keeps the banding under the
    /// anti-aliasing.
    /// <para>Shared by the outset and inset paths so the two falloffs cannot drift.</para>
    /// </remarks>
    private static void FillShadowRamp(
        Pixmap pixmap,
        float x0,
        float y0,
        float w0,
        float h0,
        float rx0,
        float ry0,
        float blur,
        RgbaColor color,
        Mask? mask)
    {
        if (blur < 0.5f)
        {
            // No blur: a single crisp rounded rect.
            FillShadowRect(pixmap, x0, y0, w0, h0, rx0, ry0, color, mask);
            return;
        }

        float sigma = F32.Max(blur / 2f, 0.05f);
        float reach = 2.5f * sigma;
        uint steps = (uint)Math.Clamp((int)MathF.Ceiling(2f * reach), 6, 24);
        float targetAlpha = color.A / 255f;
        float accumulated = 0f;
        for (uint j = 0; j < steps; j++)
        {
            float t = 1f - (2f * j / (steps - 1));
            float e = t * reach;
            float target = targetAlpha * GaussianCoverage(e / sigma);
            float remaining = 1f - accumulated;
            if (remaining <= 0.001f)
            {
                break;
            }

            float layer = Math.Clamp((target - accumulated) / remaining, 0f, 1f);
            byte layerAlpha = (byte)Math.Clamp(F32.Round(layer * 255f), 0f, 255f);
            if (layerAlpha == 0)
            {
                continue;
            }

            FillShadowRect(
                pixmap,
                x0 - e,
                y0 - e,
                w0 + (2f * e),
                h0 + (2f * e),
                F32.Max(rx0 + e, 0f),
                F32.Max(ry0 + e, 0f),
                new RgbaColor(color.R, color.G, color.B, layerAlpha),
                mask);
            accumulated = target;
        }

        // Everything further inside than the ramp is solid.
        float coreRemaining = 1f - accumulated;
        if (coreRemaining > 0.001f)
        {
            float layer = Math.Clamp((targetAlpha - accumulated) / coreRemaining, 0f, 1f);
            byte layerAlpha = (byte)Math.Clamp(F32.Round(layer * 255f), 0f, 255f);
            if (layerAlpha > 0)
            {
                FillShadowRect(
                    pixmap,
                    x0 + reach,
                    y0 + reach,
                    w0 - (2f * reach),
                    h0 - (2f * reach),
                    F32.Max(rx0 - reach, 0f),
                    F32.Max(ry0 - reach, 0f),
                    new RgbaColor(color.R, color.G, color.B, layerAlpha),
                    mask);
            }
        }
    }

    /// <summary>Paint an inset <c>box-shadow</c> inside the element's border box.</summary>
    /// <remarks>
    /// Inset coverage is the complement of the outset ramp: full alpha at the border-box
    /// edge, half at the offset-and-spread inner edge, near zero deep inside. Rather than
    /// fill rings, this lays the <em>outset</em> ramp of the inner shape into an eraser at
    /// full alpha and subtracts it from a solid fill, which reuses
    /// <see cref="FillShadowRamp"/> verbatim: <c>A * (1 - Phi(-d / sigma))</c>.
    /// <para>
    /// Painted after the background and before the border, which is where CSS
    /// Backgrounds 3 puts it.
    /// </para>
    /// </remarks>
    internal static void PaintInsetBoxShadow(
        Pixmap pixmap,
        BoxShadow shadow,
        in Rect rect,
        BorderRadii borderRadius,
        Mask? ancestorClip)
    {
        if (!shadow.Inset || shadow.Color.A == 0 || rect.Width <= 0f || rect.Height <= 0f)
        {
            return;
        }

        // An inset shadow never leaves the border box, so that is its whole extent.
        if (!PaintDomPainter.RectIntersectsPaintSurface(rect, pixmap, 1f))
        {
            return;
        }

        int left = (int)F32.Max(MathF.Floor(rect.X), 0f);
        int top = (int)F32.Max(MathF.Floor(rect.Y), 0f);
        int right = (int)F32.Min(MathF.Ceiling(rect.X + rect.Width), pixmap.Width);
        int bottom = (int)F32.Min(MathF.Ceiling(rect.Y + rect.Height), pixmap.Height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        Pixmap? scratch = Pixmap.New((uint)(right - left), (uint)(bottom - top));
        if (scratch is null)
        {
            return;
        }

        using Pixmap owned = scratch;
        ResolvedBorderRadii borderRadii = borderRadius.Resolve(rect.Width, rect.Height);
        Rect local = rect with { X = rect.X - left, Y = rect.Y - top };
        SKPath? boxPath = PaintClips.RoundedRectPathRadii(
            local.X, local.Y, local.Width, local.Height, borderRadii);
        if (boxPath is null)
        {
            return;
        }

        RgbaColor color = shadow.Color;
        using (SKPath ownedPath = boxPath)
        {
            Surface.FillPath(owned, ownedPath, color, true, Affine2.Identity, null);
        }

        // The inner edge: the border box moved by the offset and shrunk by the spread. A
        // positive x offset moves it right, which thickens the shadow on the left, the
        // same way a positive offset moves an outset shadow right.
        float spread = shadow.Spread;
        float innerX = local.X + shadow.OffsetX + spread;
        float innerY = local.Y + shadow.OffsetY + spread;
        float innerW = local.Width - (2f * spread);
        float innerH = local.Height - (2f * spread);
        if (innerW > 0f && innerH > 0f)
        {
            Pixmap? eraserSurface = Pixmap.New(owned.Width, owned.Height);
            if (eraserSurface is not null)
            {
                using Pixmap eraser = eraserSurface;
                (float X, float Y) radius = borderRadii.TopLeft;
                // Opaque, so the subtraction leaves A * (1 - coverage) rather than
                // A * (1 - A * coverage).
                FillShadowRamp(
                    eraser,
                    innerX,
                    innerY,
                    innerW,
                    innerH,
                    F32.Max(radius.X - spread, 0f),
                    F32.Max(radius.Y - spread, 0f),
                    F32.Max(shadow.Blur, 0f),
                    new RgbaColor(color.R, color.G, color.B, 255),
                    null);
                Surface.DrawPixmap(
                    owned, 0, 0, eraser, 1f, false, Affine2.Identity, null, SKBlendMode.DstOut);
            }
        }

        Surface.DrawPixmap(pixmap, left, top, owned, 1f, false, Affine2.Identity, ancestorClip);
    }

    /// <summary>
    /// Fraction of a gaussian-blurred edge's coverage at <paramref name="z"/> standard
    /// deviations outside the shape: 1 well inside, 0.5 at the edge, ~0 well outside.
    /// </summary>
    /// <remarks>
    /// This is <c>Phi(-z)</c>. .NET has no <c>float</c> erf, so it uses the same
    /// Abramowitz and Stegun 7.1.26 series the reference does (max error 1.5e-7, far
    /// below one 8-bit alpha step) rather than a different approximation that would
    /// drift from Rust in the last alpha count.
    /// </remarks>
    private static float GaussianCoverage(float z) => 0.5f * (1f + Erf(-z / MathF.Sqrt(2f)));

    private static float Erf(float x)
    {
        float sign = x < 0f ? -1f : 1f;
        x = MathF.Abs(x);
        float t = 1f / (1f + (0.3275911f * x));
        float y = 1f
            - (((((((1.061405429f * t) - 1.453152027f) * t) + 1.421413741f) * t) - 0.284496736f) * t
                + 0.254829592f)
                * t
                * MathF.Exp(-x * x);
        return sign * y;
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
