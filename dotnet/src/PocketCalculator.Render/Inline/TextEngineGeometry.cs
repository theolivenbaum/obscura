namespace PocketCalculator.Render;

/// <summary>Line geometry queries over finalized inline items, for hit testing and ranges.</summary>
public sealed partial class TextEngine
{
    /// <summary>
    /// Append, in document coordinates, the content rect of every line box of item
    /// <paramref name="index"/>: from its first glyph to its last (inline-box edges included),
    /// the full line-box height. Lines without glyphs have none.
    /// </summary>
    internal void AppendLineContentRects(int index, List<Rect> output)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        InlineItem item = _items[index];
        List<int> lineStarts = item.OwnerText is { } source && item.BoundaryEvents.Count > 0
            ? InlineGeometry.SourceLineStarts(item.Buffer, source)
            : [];
        int lineIndex = 0;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            float lineOffset = lineIndex == 0 ? item.FirstLineOffset : 0f;
            lineIndex++;
            if (run.Glyphs.Count == 0)
            {
                continue;
            }

            // White space a line ends with is not content when it is removed or hangs (CSS
            // Text 3 4.1.3): a right-aligned line's trailing space sits past its content, beside
            // a float.
            int contentEnd = int.MaxValue;
            if (item.Buffer.Lines[run.LineIndex].AlignOptions.Trailing != TrailingSpace.Counts)
            {
                contentEnd = 0;
                foreach (LayoutGlyph glyph in run.Glyphs)
                {
                    if (!IsWhiteSpaceRange(run.Text, glyph.Start, glyph.End))
                    {
                        contentEnd = Math.Max(contentEnd, glyph.End);
                    }
                }
            }

            float left = float.PositiveInfinity;
            float right = float.NegativeInfinity;
            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                if (glyph.Start >= contentEnd)
                {
                    continue;
                }

                left = F32.Min(left, glyph.X);
                right = F32.Max(right, glyph.X + glyph.W);
            }

            if (left > right)
            {
                continue;
            }

            float edges = 0f;
            float shift = 0f;
            if (run.LineIndex < lineStarts.Count)
            {
                int lineStart = lineStarts[run.LineIndex];
                int lineEnd = lineStart + run.Text.Length;
                edges = InlineGeometry.LineEdgeAdvance(item, lineStart, lineEnd);
                shift = InlineGeometry.LineEdgeAlignmentShift(item, lineStart, lineEnd);
            }

            float x = item.Origin.X + lineOffset + shift + left;
            output.Add(new Rect(x, item.Origin.Y + run.LineTop, F32.Max(right - left + edges, 0f), run.LineHeight));
        }
    }

    private readonly Lock _transientGate = new();

    /// <summary>Indent item <paramref name="index"/>'s first line by an inside list marker.</summary>
    internal void SetMarkerIndent(int index, float advance)
    {
        if (index < 0 || index >= _items.Count || !float.IsFinite(advance))
        {
            return;
        }

        InlineItem item = _items[index];
        if (item.MarkerIndent.Equals(advance))
        {
            return;
        }

        // The first line's own wrap boundary is derived from the unsplit source, as for a
        // text indent (ShapeWithTextIndent).
        item.SourceBuffer ??= (item.PristineBuffer ?? item.Buffer).Clone();
        item.MarkerIndent = advance;
        item.ShapedFor = null;
    }

    /// <summary>The baseline of item <paramref name="index"/>'s first line, in document coordinates.</summary>
    internal float? FirstLineBaseline(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return null;
        }

        InlineItem item = _items[index];
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            return item.Origin.Y + run.LineY;
        }

        return null;
    }

    /// <summary>
    /// The width of list-marker text shaped in <paramref name="style"/>'s font, its trailing
    /// space kept (the marker's <c>white-space: pre</c>). Pushes an item only to shape it.
    /// </summary>
    internal float MeasureMarkerText(string text, LayoutStyle style)
    {
        lock (_transientGate)
        {
            if (PushGeneratedText(text, style, WhiteSpace.Pre) is not { } index)
            {
                return 0f;
            }

            try
            {
                return F32.Max(MeasureWord(index).Width, 0f);
            }
            finally
            {
                _items.RemoveRange(index, _items.Count - index);
            }
        }
    }

    /// <summary>
    /// Paint list-marker text shaped in <paramref name="style"/>'s font with its left edge at
    /// <paramref name="left"/> and its baseline at <paramref name="baselineY"/> (surface
    /// coordinates before raster scale), without leaving an item behind.
    /// </summary>
    internal void PaintTransientText(
        string text,
        LayoutStyle style,
        float left,
        float baselineY,
        Pixmap pixmap,
        Rect? clip,
        Mask? clipMask,
        float rasterScale,
        bool printEconomy)
    {
        lock (_transientGate)
        {
            if (PushGeneratedText(text, style, WhiteSpace.Pre) is not { } index)
            {
                return;
            }

            try
            {
                MeasureWord(index);
                InlineItem item = _items[index];
                float lineY = 0f;
                foreach (LayoutRun run in item.Buffer.LayoutRuns())
                {
                    lineY = run.LineY;
                    break;
                }

                item.Origin = (left, baselineY - lineY);
                item.Clip = clip;
                PaintItemWithClipMaskScaledForPrint(index, pixmap, (0f, 0f), clip, clipMask, rasterScale, printEconomy);
            }
            finally
            {
                _items.RemoveRange(index, _items.Count - index);
            }
        }
    }

    /// <summary>The DOM text nodes collected into item <paramref name="index"/>.</summary>
    internal IReadOnlyList<TextNodeChunk> TextNodeChunks(int index) =>
        index >= 0 && index < _items.Count ? _items[index].TextNodes : [];

    /// <summary>
    /// Append, in document coordinates, one rect per line for the collected text
    /// <paramref name="start"/>..<paramref name="end"/> of item <paramref name="index"/>, as
    /// <c>Range.getClientRects()</c> reports a text node's selected part: from the first
    /// selected glyph to the last on each line (inline-box edges and relative offsets applied),
    /// the text's font box tall (<paramref name="fontBox"/>) on its own baseline. A collapsed
    /// range (<paramref name="caret"/>) gives one zero-width rect at its offset.
    /// </summary>
    internal void AppendRangeRects(
        int index,
        int start,
        int end,
        bool caret,
        (float Ascent, float Descent) fontBox,
        List<Rect> output)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        InlineItem item = _items[index];
        List<int> lineStarts = RangeLineStarts(item);
        int lineIndex = 0;
        foreach (LayoutRun run in item.Buffer.LayoutRuns())
        {
            float lineOffset = lineIndex == 0 ? item.FirstLineOffset : 0f;
            lineIndex++;
            if (run.LineIndex >= lineStarts.Count)
            {
                continue;
            }

            int lineStart = lineStarts[run.LineIndex];
            int lineEnd = lineStart + run.Text.Length;
            int relStart = start - lineStart;
            int relEnd = end - lineStart;
            if (relEnd < 0 || relStart > run.Text.Length)
            {
                continue;
            }

            float inlineAlignment = InlineGeometry.LineEdgeAlignmentShift(item, lineStart, lineEnd);
            float[]? visualShifts = InlineGeometry.VisualEdgeShifts(item, run, lineStart, lineEnd);
            float Shift(int glyphIndex, LayoutGlyph glyph) =>
                lineOffset + inlineAlignment + (visualShifts is not null
                    ? visualShifts[glyphIndex]
                    : InlineGeometry.LineAdvanceBeforeText(item, lineStart + glyph.Start, lineStart, lineEnd));

            float BaselineY(TextMetrics? box) => box is not { } metrics
                ? run.LineY
                : metrics.Align switch
                {
                    LineBoxAlign.LineTop => run.LineTop + metrics.Above,
                    LineBoxAlign.LineBottom => run.LineTop + run.LineHeight - metrics.Below,
                    _ => run.LineY - metrics.Shift,
                };

            if (caret)
            {
                // The run holding the offset: a glyph starting at or covering it, or the end of
                // the last glyph.
                int at = -1;
                for (int g = 0; g < run.Glyphs.Count; g++)
                {
                    LayoutGlyph glyph = run.Glyphs[g];
                    if ((glyph.Start <= relStart && relStart < glyph.End)
                        || (g == run.Glyphs.Count - 1 && relStart == glyph.End))
                    {
                        at = g;
                        break;
                    }
                }

                if (at < 0)
                {
                    continue;
                }

                LayoutGlyph hit = run.Glyphs[at];
                (float rx, float ry) = InlineGeometry.GlyphRelativeOffset(item.RelativeOwnerRanges, lineStart, hit.Start, hit.End);
                float x = InlineGeometry.RunCursorX(run, relStart) + Shift(at, hit) + rx;
                float top = BaselineY(hit.BoxMetrics) - fontBox.Ascent + ry;
                output.Add(new Rect(item.Origin.X + x, item.Origin.Y + top, 0f, fontBox.Ascent + fontBox.Descent));
                return;
            }

            // White space removed at the end of a line has no text box (Chromium 141 ends a
            // wrapped line's rect at its last word).
            int contentEnd = int.MaxValue;
            if (item.Buffer.Lines[run.LineIndex].AlignOptions.Trailing == TrailingSpace.Removed)
            {
                contentEnd = 0;
                foreach (LayoutGlyph glyph in run.Glyphs)
                {
                    if (!IsWhiteSpaceRange(run.Text, glyph.Start, glyph.End))
                    {
                        contentEnd = Math.Max(contentEnd, glyph.End);
                    }
                }
            }

            float left = float.PositiveInfinity;
            float right = float.NegativeInfinity;
            float lineTop = 0f;
            bool any = false;
            for (int g = 0; g < run.Glyphs.Count; g++)
            {
                LayoutGlyph glyph = run.Glyphs[g];
                if (glyph.End <= relStart || glyph.Start >= relEnd || glyph.Start >= contentEnd)
                {
                    continue;
                }

                (float rx, float ry) = InlineGeometry.GlyphRelativeOffset(item.RelativeOwnerRanges, lineStart, glyph.Start, glyph.End);
                float x = glyph.X + Shift(g, glyph) + rx;
                left = F32.Min(left, x);
                right = F32.Max(right, x + glyph.W);
                if (!any)
                {
                    lineTop = BaselineY(glyph.BoxMetrics) - fontBox.Ascent + ry;
                    any = true;
                }
            }

            if (any)
            {
                output.Add(new Rect(
                    item.Origin.X + left,
                    item.Origin.Y + lineTop,
                    F32.Max(right - left, 0f),
                    fontBox.Ascent + fontBox.Descent));
            }

            // A preserved newline the range holds has a text box of its own, zero wide at the
            // end of its line (Chromium 141, `white-space: pre`).
            bool lastRunOfLine = run.Glyphs.Count == 0 || relEnd > run.Text.Length || run.Glyphs[^1].End >= run.Text.Length;
            if (relStart <= run.Text.Length
                && relEnd > run.Text.Length
                && lastRunOfLine
                && !item.Buffer.Lines[run.LineIndex].AlignOptions.SoftWrapEnd
                && run.LineIndex + 1 < item.Buffer.Lines.Count)
            {
                float endX = run.Glyphs.Count > 0
                    ? InlineGeometry.RunCursorX(run, run.Text.Length)
                        + lineOffset + inlineAlignment
                        + InlineGeometry.LineAdvanceBeforeText(item, lineEnd, lineStart, lineEnd)
                    : run.X + lineOffset + inlineAlignment;
                float top = (run.Glyphs.Count > 0 ? BaselineY(run.Glyphs[^1].BoxMetrics) : run.LineY) - fontBox.Ascent;
                output.Add(new Rect(item.Origin.X + endX, item.Origin.Y + top, 0f, fontBox.Ascent + fontBox.Descent));
            }
        }
    }

    /// <summary>
    /// Where each buffer line starts in the collected text: one newline separates two lines
    /// except where a float or text-indent split a paragraph (the head line then ends at a soft
    /// wrap), or as <see cref="InlineGeometry.SourceLineStarts"/> finds them when the item keeps
    /// its source text.
    /// </summary>
    private static List<int> RangeLineStarts(InlineItem item)
    {
        if (item.OwnerText is { } source)
        {
            return InlineGeometry.SourceLineStarts(item.Buffer, source);
        }

        List<int> starts = new(item.Buffer.Lines.Count);
        int offset = 0;
        foreach (BufferLine line in item.Buffer.Lines)
        {
            starts.Add(offset);
            offset += line.Text.Length + (line.AlignOptions.SoftWrapEnd ? 0 : 1);
        }

        return starts;
    }

    private static bool IsWhiteSpaceRange(string text, int start, int end)
    {
        for (int i = Math.Max(start, 0); i < Math.Min(end, text.Length); i++)
        {
            if (text[i] is not (' ' or '\t' or '\n'))
            {
                return false;
            }
        }

        return true;
    }
}
