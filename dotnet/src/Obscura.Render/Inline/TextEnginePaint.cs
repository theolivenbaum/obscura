using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

public sealed partial class TextEngine
{
    /// <summary>
    /// Rasterize inline context <paramref name="index"/> into <paramref name="pixmap"/>,
    /// honoring its finalized clip.
    /// </summary>
    public void PaintItem(int index, Pixmap pixmap, (float X, float Y) offset) =>
        PaintItemWithClip(index, pixmap, offset, null);

    /// <summary>
    /// Rasterize an inline context with a capture-space clip override.
    /// </summary>
    /// <remarks>
    /// Root scrolling is layered over immutable document layout, so the glyph origin and any
    /// overflow clip must both be sampled in the current viewport rather than mixing viewport
    /// and document coordinates.
    /// </remarks>
    public void PaintItemWithClip(int index, Pixmap pixmap, (float X, float Y) offset, Rect? clipOverride) =>
        PaintItemWithClipMask(index, pixmap, offset, clipOverride, null);

    /// <summary>
    /// Rasterize shaped text against the rectangular culling envelope and an optional full
    /// descendant clip-chain mask.
    /// </summary>
    public void PaintItemWithClipMask(
        int index,
        Pixmap pixmap,
        (float X, float Y) offset,
        Rect? clipOverride,
        Mask? clipMask) =>
        PaintItemWithClipMaskScaled(index, pixmap, offset, clipOverride, clipMask, 1f);

    /// <summary>
    /// Rasterize already-shaped CSS-pixel glyph positions directly into a device-pixel surface.
    /// Shaping and line breaking stay immutable; only glyph outline sampling, clipping, and
    /// compositing use <paramref name="rasterScale"/>.
    /// </summary>
    public void PaintItemWithClipMaskScaled(
        int index,
        Pixmap pixmap,
        (float X, float Y) offset,
        Rect? clipOverride,
        Mask? clipMask,
        float rasterScale) =>
        PaintItemWithClipMaskScaledForPrint(index, pixmap, offset, clipOverride, clipMask, rasterScale, false);

    public void PaintItemWithClipMaskScaledForPrint(
        int index,
        Pixmap pixmap,
        (float X, float Y) offset,
        Rect? clipOverride,
        Mask? clipMask,
        float rasterScale,
        bool printEconomy)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        InlineItem item = _items[index];
        Rect? clip = clipOverride ?? item.Clip;
        if (!InlineItemMayIntersectSurface(item, offset, clip, pixmap.Height, rasterScale))
        {
            return;
        }

        float ox = (item.Origin.X + offset.X) * rasterScale;
        float oy = (item.Origin.Y + offset.Y) * rasterScale;

        // The glyph origin shifts by the container's accumulated translate, but the clip is
        // already in screen space and must not move with the container, or a translated slide
        // would drag its viewport's clip along with it.
        int pw = (int)pixmap.Width;
        int ph = (int)pixmap.Height;
        (float X0, float Y0, float X1, float Y1)? clipBounds = clip is { } c
            ? (c.X * rasterScale, c.Y * rasterScale, (c.X + c.Width) * rasterScale, (c.Y + c.Height) * rasterScale)
            : null;

        List<int> lineSourceStarts = item.OwnerText is { } ownerSource
            && (item.RelativeOwnerRanges.Count > 0 || item.BoundaryEvents.Count > 0)
            ? InlineGeometry.SourceLineStarts(item.Buffer, ownerSource)
            : [];

        // Collect underline segments before drawing glyphs. Underline is carried per glyph via
        // metadata; runs of consecutive underlined glyphs on a line become one stroke below the
        // baseline.
        List<(float X0, float X1, float Y, float Thickness, RgbaColor Color)> underlines = [];
        var fillBounds = new (float X0, float Y0, float X1, float Y1)?[item.ClipFills.Count];

        int lineIndex = 0;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            float lineOffset = lineIndex == 0 ? item.FirstLineOffset : 0f;
            int lineSourceStart = run.LineIndex < lineSourceStarts.Count ? lineSourceStarts[run.LineIndex] : 0;
            int lineSourceEnd = lineSourceStart + run.Text.Length;
            float inlineAlignment = InlineGeometry.LineEdgeAlignmentShift(item, lineSourceStart, lineSourceEnd);
            float baseY = run.LineY;
            (float X0, float X1, float FontSize, RgbaColor Color, (float X, float Y) Relative)? segment = null;

            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                // Keep decoration and background-clip bounds in lockstep with glyph painting. A
                // truncated glyph must not leave an underline tail or expand a gradient's
                // sampling bounds past the separately painted ellipsis marker.
                if (item.Marker is { } marker
                    && marker.LineIndex == lineIndex
                    && glyph.X + lineOffset + glyph.W > marker.ContentEnd)
                {
                    continue;
                }

                (float X, float Y) relative = InlineGeometry.GlyphRelativeOffset(
                    item.RelativeOwnerRanges,
                    lineSourceStart,
                    glyph.Start,
                    glyph.End);
                relative.X += inlineAlignment + InlineGeometry.LineAdvanceBeforeText(
                    item,
                    lineSourceStart + glyph.Start,
                    lineSourceStart,
                    lineSourceEnd);

                bool underlined = (glyph.Metadata & InlineGeometry.MetaUnderline) != 0;
                if (InlineGeometry.MetadataFill(glyph.Metadata) is { } fillIndex
                    && fillIndex < fillBounds.Length)
                {
                    (float X0, float Y0, float X1, float Y1) glyphBounds = (
                        glyph.X + lineOffset + relative.X,
                        run.LineTop + relative.Y,
                        glyph.X + lineOffset + glyph.W + relative.X,
                        run.LineTop + run.LineHeight + relative.Y);
                    fillBounds[fillIndex] = fillBounds[fillIndex] is { } existing
                        ? (
                            F32.Min(existing.X0, glyphBounds.X0),
                            F32.Min(existing.Y0, glyphBounds.Y0),
                            F32.Max(existing.X1, glyphBounds.X1),
                            F32.Max(existing.Y1, glyphBounds.Y1))
                        : glyphBounds;
                }

                RgbaColor color = glyph.Color ?? new RgbaColor(0, 0, 0, 255);
                if (printEconomy)
                {
                    color = RenderPaint.PrintEconomyColor(color);
                }

                if (underlined)
                {
                    if (segment is { } current && current.Color == color && current.Relative == relative)
                    {
                        segment = current with
                        {
                            X1 = glyph.X + lineOffset + glyph.W + relative.X,
                            FontSize = F32.Max(current.FontSize, glyph.FontSize),
                        };
                    }
                    else
                    {
                        if (segment is { } previous)
                        {
                            underlines.Add((
                                previous.X0,
                                previous.X1,
                                baseY + previous.Relative.Y + F32.Max(previous.FontSize * 0.12f, 1f),
                                F32.Max(previous.FontSize / 14f, 1f),
                                previous.Color));
                        }

                        segment = (
                            glyph.X + lineOffset + relative.X,
                            glyph.X + lineOffset + glyph.W + relative.X,
                            glyph.FontSize,
                            color,
                            relative);
                    }
                }
                else if (segment is { } previous)
                {
                    underlines.Add((
                        previous.X0,
                        previous.X1,
                        baseY + previous.Relative.Y + F32.Max(previous.FontSize * 0.12f, 1f),
                        F32.Max(previous.FontSize / 14f, 1f),
                        previous.Color));
                    segment = null;
                }
            }

            if (segment is { } tail)
            {
                underlines.Add((
                    tail.X0,
                    tail.X1,
                    baseY + tail.Relative.Y + F32.Max(tail.FontSize * 0.12f, 1f),
                    F32.Max(tail.FontSize / 14f, 1f),
                    tail.Color));
            }

            lineIndex++;
        }

        // Fallback color if a glyph carries none (shouldn't happen: every span sets one).
        var defaultColor = new RgbaColor(0, 0, 0, 255);
        List<ClipTextFill> clipFills = item.ClipFills;
        PremultipliedColor[] pixels = pixmap.Pixels;

        lineIndex = 0;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            float lineOffset = lineIndex == 0 ? item.FirstLineOffset : 0f;
            int lineSourceStart = run.LineIndex < lineSourceStarts.Count ? lineSourceStarts[run.LineIndex] : 0;
            int lineSourceEnd = lineSourceStart + run.Text.Length;
            float inlineAlignment = InlineGeometry.LineEdgeAlignmentShift(item, lineSourceStart, lineSourceEnd);

            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                if (item.Marker is { } marker
                    && marker.LineIndex == lineIndex
                    && glyph.X + lineOffset + glyph.W > marker.ContentEnd)
                {
                    continue;
                }

                (float X, float Y) relative = InlineGeometry.GlyphRelativeOffset(
                    item.RelativeOwnerRanges,
                    lineSourceStart,
                    glyph.Start,
                    glyph.End);
                relative.X += inlineAlignment + InlineGeometry.LineAdvanceBeforeText(
                    item,
                    lineSourceStart + glyph.Start,
                    lineSourceStart,
                    lineSourceEnd);

                PhysicalGlyph physical = glyph.Physical((lineOffset + relative.X, relative.Y), rasterScale);
                RgbaColor glyphColor = glyph.Color ?? defaultColor;
                int? fillIndex = InlineGeometry.MetadataFill(glyph.Metadata);
                if (printEconomy && fillIndex is not null)
                {
                    continue;
                }

                FontVariations? explicitVariations = InlineGeometry.MetadataVariation(glyph.Metadata) is { } variationIndex
                    && variationIndex < item.VariationSets.Count
                        ? item.VariationSets[variationIndex]
                        : null;
                FontVariations? effectiveVariations = glyph.FontIsVariable
                    ? _variableCache.EffectiveVariations(
                        physical.CacheKey.FontId,
                        glyph.FontWeightAxis,
                        glyph.FontOpticalSize,
                        glyph.FontItalicAxis,
                        explicitVariations)
                    : null;

                GlyphImage? image = _rasterizer.Rasterize(physical.CacheKey, effectiveVariations);
                if (image is null)
                {
                    continue;
                }

                int lineYScaled = (int)(run.LineY * rasterScale);
                for (int row = 0; row < image.Height; row++)
                {
                    for (int column = 0; column < image.Width; column++)
                    {
                        uint coverage;
                        byte sourceRed;
                        byte sourceGreen;
                        byte sourceBlue;
                        if (image.Content == GlyphContent.Mask)
                        {
                            coverage = image.Data[(row * image.Width) + column];
                            sourceRed = glyphColor.R;
                            sourceGreen = glyphColor.G;
                            sourceBlue = glyphColor.B;
                        }
                        else
                        {
                            int offsetInData = (((row * image.Width) + column) * 4);
                            sourceRed = image.Data[offsetInData];
                            sourceGreen = image.Data[offsetInData + 1];
                            sourceBlue = image.Data[offsetInData + 2];
                            coverage = image.Data[offsetInData + 3];
                        }

                        // The mask rasterizer replaces the authored alpha with glyph coverage.
                        // Reapply the span alpha here so transparent and translucent CSS text
                        // do not become opaque at paint.
                        uint alpha = coverage * glyphColor.A / 255;
                        if (alpha == 0)
                        {
                            continue;
                        }

                        int gx = physical.X + image.Left + column;
                        int gy = lineYScaled + physical.Y + image.Top + row;
                        byte r = sourceRed;
                        byte g = sourceGreen;
                        byte b = sourceBlue;
                        if (fillIndex is { } fill && fill < clipFills.Count && fillBounds[fill] is { } bounds)
                        {
                            RgbaColor sampled = Inline.SampleGradient(
                                clipFills[fill],
                                ((gx + 0.5f) / rasterScale) - bounds.X0,
                                ((gy + 0.5f) / rasterScale) - bounds.Y0,
                                bounds.X1 - bounds.X0,
                                bounds.Y1 - bounds.Y0);
                            (r, g, b) = (sampled.R, sampled.G, sampled.B);
                        }

                        if (printEconomy)
                        {
                            RgbaColor adjusted = RenderPaint.PrintEconomyColor(new RgbaColor(r, g, b, 255));
                            (r, g, b) = (adjusted.R, adjusted.G, adjusted.B);
                        }

                        int px = (int)ox + gx;
                        int py = (int)oy + gy;
                        if (clipBounds is { } cb
                            && (px < cb.X0 || px >= cb.X1 || py < cb.Y0 || py >= cb.Y1))
                        {
                            continue;
                        }

                        if (px < 0 || px >= pw || py < 0 || py >= ph)
                        {
                            continue;
                        }

                        int pixelIndex = (py * pw) + px;
                        uint maskAlpha = clipMask is { } mask && pixelIndex < mask.Data.Length
                            ? mask.Data[pixelIndex]
                            : 255u;
                        uint sa = alpha * maskAlpha / 255;
                        if (sa == 0)
                        {
                            continue;
                        }

                        PremultipliedColor dst = pixels[pixelIndex];
                        uint sr = r * sa / 255;
                        uint sg = g * sa / 255;
                        uint sb = b * sa / 255;
                        uint inv = 255 - sa;
                        uint outA = sa + (dst.A * inv / 255);
                        if (outA == 0)
                        {
                            continue;
                        }

                        pixels[pixelIndex] = PremultipliedColor.FromRgba(
                            (byte)(sr + (dst.R * inv / 255)),
                            (byte)(sg + (dst.G * inv / 255)),
                            (byte)(sb + (dst.B * inv / 255)),
                            (byte)outA);
                    }
                }
            }

            lineIndex++;
        }

        // The marker owns its own shaped buffer so it uses a real U+2026 glyph from the selected
        // font without changing DOM text or the natural content buffer. The direct pure-text
        // slice is LTR-only; logical-start marker placement arrives with the bidi foundation.
        if (item.Marker is { } placement && item.MarkerBuffer is { } markerBuffer)
        {
            foreach (LayoutRun run in markerBuffer.LayoutRuns())
            {
                foreach (LayoutGlyph glyph in run.Glyphs)
                {
                    PhysicalGlyph physical = glyph.Physical((0f, 0f), 1f);
                    RgbaColor color = glyph.Color ?? defaultColor;
                    GlyphImage? image = _rasterizer.Rasterize(physical.CacheKey, null);
                    if (image is null)
                    {
                        continue;
                    }

                    for (int row = 0; row < image.Height; row++)
                    {
                        for (int column = 0; column < image.Width; column++)
                        {
                            uint alpha = image.Content == GlyphContent.Mask
                                ? image.Data[(row * image.Width) + column]
                                : image.Data[((((row * image.Width) + column) * 4)) + 3];
                            alpha = alpha * color.A / 255;
                            if (alpha == 0)
                            {
                                continue;
                            }

                            int px = (int)ox + (int)F32.Round(placement.X) + physical.X + image.Left + column;
                            int py = (int)oy + (int)F32.Round(placement.Y) + (int)run.LineY
                                + physical.Y + image.Top + row;
                            if (clipBounds is { } cb
                                && (px < cb.X0 || px >= cb.X1 || py < cb.Y0 || py >= cb.Y1))
                            {
                                continue;
                            }

                            if (px < 0 || px >= pw || py < 0 || py >= ph)
                            {
                                continue;
                            }

                            int pixelIndex = (py * pw) + px;
                            PremultipliedColor dst = pixels[pixelIndex];
                            uint inv = 255 - alpha;
                            pixels[pixelIndex] = PremultipliedColor.FromRgba(
                                (byte)((color.R * alpha / 255) + (dst.R * inv / 255)),
                                (byte)((color.G * alpha / 255) + (dst.G * inv / 255)),
                                (byte)((color.B * alpha / 255) + (dst.B * inv / 255)),
                                (byte)(alpha + (dst.A * inv / 255)));
                        }
                    }
                }
            }
        }

        // Stroke underline segments with the same authored alpha as their glyphs. Transparent
        // text decorations are transparent too.
        foreach ((float x0, float x1, float y, float thickness, RgbaColor color) in underlines)
        {
            if (color.A == 0)
            {
                continue;
            }

            int t = Math.Max((int)F32.Round(F32.Max(thickness, 1f) * rasterScale), 1);
            for (int dt = 0; dt < t; dt++)
            {
                int py = (int)oy + (int)(y * rasterScale) + dt;
                if (py < 0 || py >= ph)
                {
                    continue;
                }

                if (clipBounds is { } cb && (py < cb.Y0 || py >= cb.Y1))
                {
                    continue;
                }

                for (int px = (int)(ox + (x0 * rasterScale)); px < (int)(ox + (x1 * rasterScale)); px++)
                {
                    if (px < 0 || px >= pw)
                    {
                        continue;
                    }

                    if (clipBounds is { } cbx && (px < cbx.X0 || px >= cbx.X1))
                    {
                        continue;
                    }

                    int pixelIndex = (py * pw) + px;
                    PremultipliedColor dst = pixels[pixelIndex];
                    uint sa = color.A;
                    uint inv = 255 - sa;
                    pixels[pixelIndex] = PremultipliedColor.FromRgba(
                        (byte)((color.R * sa / 255) + (dst.R * inv / 255)),
                        (byte)((color.G * sa / 255) + (dst.G * inv / 255)),
                        (byte)((color.B * sa / 255) + (dst.B * inv / 255)),
                        (byte)(sa + (dst.A * inv / 255)));
                }
            }
        }
    }

}
