// Port of the static-font text painting of crates/obscura-render/src/paint.rs:
// `measure_text`, `draw_text`, `paint_text_node`, `clip_text_fill_color`, `list_marker_text`
// and `selected_option_label`.
//
// `measure_text` / `fallback_font_bytes` were ported early as `DomTextMeasure` because the
// render-tree build needs them; this file adopts that pair rather than declaring a second copy.
using System.Globalization;
using System.Text;
using Obscura.Dom;
using SkiaSharp;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal static class PaintText
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, SKTypeface> Typefaces = new(StringComparer.Ordinal);

    internal static float MeasureText(string text, float size, bool isBold, string? family) =>
        DomTextMeasure.MeasureText(text, size, isBold, family);

    private static SKTypeface Typeface(string? family)
    {
        string stem = DomTextMeasure.FallbackFaceStem(family);
        lock (Gate)
        {
            if (Typefaces.TryGetValue(stem, out SKTypeface? cached))
            {
                return cached;
            }

            using SKData data = SKData.CreateCopy(FontAssets.Load(stem));
            SKTypeface typeface = SKTypeface.FromData(data)
                ?? throw new InvalidOperationException($"bundled face '{stem}' failed to load");
            Typefaces[stem] = typeface;
            return typeface;
        }
    }

    /// <summary>
    /// Paint one run of text with the static bundled face, matching paint.rs's `draw_text`
    /// caret accumulation, synthetic-bold smear and manual source-over blend.
    /// </summary>
    internal static void DrawText(
        Pixmap pixmap,
        string text,
        float x,
        float y,
        RgbaColor color,
        float size,
        bool isBold,
        string? family,
        float letterSpacing,
        Rect? clip,
        Mask? clipMask,
        float rasterScale)
    {
        // A fully clipped-away run paints nothing at all.
        if (clip is { } c && (c.Width <= 0f || c.Height <= 0f))
        {
            return;
        }

        // ab_glyph's PxScale is height-based; see DomTextMeasure.HeightScaledSize.
        // Drawing has to use the same conversion as measuring or the glyphs are
        // ~11% larger than the reference paints and sit on a different baseline.
        float scale = DomTextMeasure.HeightScaledSize(family, size * rasterScale);
        if (text.Length == 0 || !float.IsFinite(scale) || scale <= 0f || color.A == 0)
        {
            return;
        }

        SKTypeface typeface;
        try
        {
            typeface = Typeface(family);
        }
        catch (Exception)
        {
            return;
        }

        using SKFont font = new(typeface, scale)
        {
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.None,
            LinearMetrics = true,
        };
        SKFontMetrics metrics = font.Metrics;
        float caretX = x * rasterScale;
        float baseline = (y * rasterScale) + (-metrics.Ascent);

        int width = (int)pixmap.Width;
        int height = (int)pixmap.Height;
        (float X0, float Y0, float X1, float Y1)? clipBounds = clip is { } bounds
            ? (bounds.X * rasterScale,
                bounds.Y * rasterScale,
                (bounds.X + bounds.Width) * rasterScale,
                (bounds.Y + bounds.Height) * rasterScale)
            : null;
        PremultipliedColor[] pixels = pixmap.Pixels;
        int boldSmear = isBold ? Math.Max((int)MathF.Ceiling(rasterScale), 1) : 1;

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            ReadOnlySpan<char> encoded = rune.ToString().AsSpan();
            ushort[] glyphs = font.GetGlyphs(encoded);
            float advance = 0f;
            if (glyphs.Length > 0)
            {
                float[] widths = font.GetGlyphWidths(glyphs);
                foreach (float value in widths)
                {
                    advance += value;
                }

                DrawGlyphs(
                    pixmap,
                    pixels,
                    font,
                    glyphs,
                    caretX,
                    baseline,
                    color,
                    width,
                    height,
                    clipBounds,
                    clipMask,
                    boldSmear);
            }

            caretX += advance + (isBold ? rasterScale : 0f) + (letterSpacing * rasterScale);
        }
    }

    private static void DrawGlyphs(
        Pixmap pixmap,
        PremultipliedColor[] pixels,
        SKFont font,
        ushort[] glyphs,
        float originX,
        float originY,
        RgbaColor color,
        int width,
        int height,
        (float X0, float Y0, float X1, float Y1)? clipBounds,
        Mask? clipMask,
        int boldSmear)
    {
        SKRect[] bounds = new SKRect[glyphs.Length];
        font.GetGlyphWidths(glyphs, null, bounds);
        SKRect ink = bounds[0];
        if (ink.Width <= 0f || ink.Height <= 0f)
        {
            return;
        }

        int left = (int)MathF.Floor(originX + ink.Left) - 1;
        int top = (int)MathF.Floor(originY + ink.Top) - 1;
        int right = (int)MathF.Ceiling(originX + ink.Right) + 1;
        int bottom = (int)MathF.Ceiling(originY + ink.Bottom) + 1;
        int glyphWidth = right - left;
        int glyphHeight = bottom - top;
        if (glyphWidth <= 0 || glyphHeight <= 0 || glyphWidth > 4096 || glyphHeight > 4096)
        {
            return;
        }

        byte[] mask;
        try
        {
            var info = new SKImageInfo(glyphWidth, glyphHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using SKBitmap bitmap = new(info);
            using (SKCanvas canvas = new(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                using SKPaint paint = new()
                {
                    Color = SKColors.White,
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                };
                using SKTextBlobBuilder builder = new();
                builder.AddPositionedRun(glyphs, font, [new SKPoint(originX - left, originY - top)]);
                using SKTextBlob? blob = builder.Build();
                if (blob is null)
                {
                    return;
                }

                canvas.DrawText(blob, 0, 0, paint);
            }

            ReadOnlySpan<byte> raw = bitmap.GetPixelSpan();
            mask = new byte[glyphWidth * glyphHeight];
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = raw[(i * 4) + 3];
            }
        }
        catch (Exception)
        {
            return;
        }

        for (int gy = 0; gy < glyphHeight; gy++)
        {
            int py = top + gy;
            if (py < 0 || py >= height)
            {
                continue;
            }

            if (clipBounds is { } cb && (py < cb.Y0 || py >= cb.Y1))
            {
                continue;
            }

            for (int gx = 0; gx < glyphWidth; gx++)
            {
                byte coverage = mask[(gy * glyphWidth) + gx];
                if (coverage == 0)
                {
                    continue;
                }

                int px = left + gx;
                if (clipBounds is { } cbx && (px < cbx.X0 || px >= cbx.X1))
                {
                    continue;
                }

                uint alpha = (uint)(color.A * coverage / 255);
                if (alpha == 0)
                {
                    continue;
                }

                for (int dx = 0; dx < boldSmear; dx++)
                {
                    int target = px + dx;
                    if (target < 0 || target >= width)
                    {
                        continue;
                    }

                    int index = (py * width) + target;
                    uint maskAlpha = clipMask is not null && index < clipMask.Data.Length
                        ? clipMask.Data[index]
                        : 255u;
                    uint blended = alpha * maskAlpha / 255;
                    if (blended == 0)
                    {
                        continue;
                    }

                    PremultipliedColor dst = pixels[index];
                    uint srcA = blended;
                    uint srcR = color.R * srcA / 255;
                    uint srcG = color.G * srcA / 255;
                    uint srcB = color.B * srcA / 255;
                    uint outA = srcA + (dst.A * (255 - srcA) / 255);
                    if (outA == 0)
                    {
                        continue;
                    }

                    pixels[index] = PremultipliedColor.FromRgba(
                        (byte)(srcR + (dst.R * (255 - srcA) / 255)),
                        (byte)(srcG + (dst.G * (255 - srcA) / 255)),
                        (byte)(srcB + (dst.B * (255 - srcA) / 255)),
                        (byte)outA);
                }
            }
        }
    }

    /// <summary>
    /// A representative visible color for <c>background-clip: text</c> text whose own color is
    /// transparent, used on the word-split paint path.
    /// </summary>
    internal static RgbaColor? ClipTextFillColor(LayoutStyle style)
    {
        if (!style.BackgroundClipText)
        {
            return null;
        }

        if (style.Color is not { } color || color.A != 0)
        {
            return null;
        }

        if (style.BackgroundGradient is { } gradient && gradient.Stops.Count > 0)
        {
            RgbaColor mid = gradient.Stops[gradient.Stops.Count / 2].Color;
            return new RgbaColor(mid.R, mid.G, mid.B, 255);
        }

        if (style.BackgroundColor is { } background && background.A != 0)
        {
            return new RgbaColor(background.R, background.G, background.B, 255);
        }

        return null;
    }

    /// <summary>Paint every word of a text node at its own laid-out position.</summary>
    internal static void PaintTextNode(
        DomTree tree,
        NodeId nid,
        DomLayout laid,
        ScrollPaintState scrollState,
        Pixmap pixmap,
        float rasterScale)
    {
        if (!laid.TextRuns.TryGetValue(nid, out List<(Rect Rect, string Text)>? runs))
        {
            return;
        }

        if (DomTraversal.RenderedParent(tree, nid) is not { } parent
            || !laid.Styles.TryGetValue(parent, out LayoutStyle? style))
        {
            return;
        }

        if (style.EffectivelyInvisible)
        {
            return;
        }

        RgbaColor color = ClipTextFillColor(style)
            ?? style.Color
            ?? new RgbaColor(0, 0, 0, 255);
        float size = style.FontSize ?? 16f;
        bool isBold = ComputedStyle.UsedFontWeight(style) >= 600;

        // A text node has no transform of its own, but any transformed element ancestor offsets
        // it. The clip receives root-scroll/sticky movement without the descendant's transform.
        (float ox, float oy) = scrollState.TranslationFor(laid, nid);
        OverflowClip? overflowClip = scrollState.OverflowClipFor(laid, nid);
        Rect? clip = overflowClip?.ViewportRect(scrollState.SurfaceExtent ?? scrollState.Viewport);
        Mask? clipMask = overflowClip is null
            ? null
            : PaintClips.OverflowClipMask(
                pixmap.Width,
                pixmap.Height,
                overflowClip,
                scrollState.SurfaceExtent ?? scrollState.Viewport);

        foreach ((Rect rect, string word) in runs)
        {
            DrawText(
                pixmap,
                word,
                rect.X + ox,
                rect.Y + oy,
                color,
                size,
                isBold,
                style.FontFamily,
                style.LetterSpacing ?? 0f,
                clip,
                clipMask,
                rasterScale);
        }
    }

    /// <summary>The marker text for a list item, or <c>null</c> when markers are suppressed.</summary>
    internal static string? ListMarkerText(DomTree tree, NodeId nid, ListStyle? style) => style switch
    {
        ListStyle.Disc => "•",
        ListStyle.Circle => "◦",
        ListStyle.Square => "▪",
        ListStyle.Decimal => DecimalMarker(tree, nid),
        _ => null,
    };

    private static string DecimalMarker(DomTree tree, NodeId nid)
    {
        int n = 1;
        NodeId? current = tree.GetNode(nid)?.PrevSibling;
        while (current is { } sibling)
        {
            if (tree.GetNode(sibling)?.AsElement() is { } element
                && string.Equals(element.Name.Local, "li", StringComparison.Ordinal))
            {
                n++;
            }

            current = tree.GetNode(sibling)?.PrevSibling;
        }

        return n.ToString(CultureInfo.InvariantCulture) + ".";
    }

    internal static string? SelectedOptionLabel(DomTree tree, NodeId select)
    {
        string? first = null;
        foreach (NodeId optionId in tree.Descendants(select))
        {
            Node? option = tree.GetNode(optionId);
            if (option?.AsElement() is not { } element
                || !string.Equals(element.Name.Local, "option", StringComparison.Ordinal))
            {
                continue;
            }

            string label = option.GetAttribute("label") ?? tree.TextContent(optionId).Trim();
            first ??= label;
            if (option.GetAttribute("selected") is not null)
            {
                return label;
            }
        }

        return first;
    }
}
