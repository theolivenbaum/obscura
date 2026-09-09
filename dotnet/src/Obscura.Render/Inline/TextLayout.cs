namespace Obscura.Render;

/// <summary>
/// Line breaking and visual-line construction over shaped words.
/// </summary>
/// <remarks>
/// Direct port of cosmic-text's <c>ShapeLine::layout_to_buffer</c>, including its exact
/// left-to-right accumulation order. That order matters: relayout at a previously computed
/// line width has to produce the same wrapping, which only holds if the widths are summed the
/// same way.
/// </remarks>
internal static class TextLayout
{
    private sealed class VisualLine
    {
        public List<(int SpanIndex, (int Word, int Glyph) Start, (int Word, int Glyph) End)> Ranges = [];
        public uint Spaces;
        public float W;
    }

    public static List<LayoutLine> LayoutToBuffer(
        ShapeLine shape,
        float fontSize,
        float? widthOpt,
        Wrap wrap,
        Align? alignOpt,
        float? matchMonoWidth)
    {
        var layoutLines = new List<LayoutLine>(1);
        var visualLines = new List<VisualLine>();
        var current = new VisualLine();

        static void AddToVisualLine(
            VisualLine line,
            int spanIndex,
            (int Word, int Glyph) start,
            (int Word, int Glyph) end,
            float width,
            uint blanks)
        {
            if (end == start)
            {
                return;
            }

            line.Ranges.Add((spanIndex, start, end));
            line.W += width;
            line.Spaces += blanks;
        }

        float limit = widthOpt ?? float.PositiveInfinity;

        if (wrap == Wrap.None)
        {
            for (int spanIndex = 0; spanIndex < shape.Spans.Count; spanIndex++)
            {
                ShapeSpan span = shape.Spans[spanIndex];
                float wordRangeWidth = 0f;
                uint blanks = 0;
                foreach (ShapeWord word in span.Words)
                {
                    wordRangeWidth += word.Width(fontSize);
                    if (word.Blank)
                    {
                        blanks++;
                    }
                }

                AddToVisualLine(current, spanIndex, (0, 0), (span.Words.Count, 0), wordRangeWidth, blanks);
            }
        }
        else
        {
            for (int spanIndex = 0; spanIndex < shape.Spans.Count; spanIndex++)
            {
                ShapeSpan span = shape.Spans[spanIndex];
                float wordRangeWidth = 0f;
                float widthBeforeLastBlank = 0f;
                uint blanks = 0;

                if (shape.Rtl != span.IsRtl)
                {
                    // Incongruent directions: walk the span's words backwards.
                    (int Word, int Glyph) fittingStart = (span.Words.Count, 0);
                    for (int i = span.Words.Count - 1; i >= 0; i--)
                    {
                        ShapeWord word = span.Words[i];
                        float wordWidth = word.Width(fontSize);
                        if (current.W + (wordRangeWidth + wordWidth) <= limit
                            || (word.Blank && current.W + wordRangeWidth <= limit))
                        {
                            if (word.Blank)
                            {
                                blanks++;
                                widthBeforeLastBlank = wordRangeWidth;
                            }

                            wordRangeWidth += wordWidth;
                            continue;
                        }

                        bool emergency = wrap == Wrap.Glyph
                            || (wrap is Wrap.WordOrGlyph or Wrap.WordOrGlyphMinContent && wordWidth > limit);
                        List<int> breakIndices = word.BreakIndices(wrap, emergency);
                        if (breakIndices.Count > 0)
                        {
                            if (wordRangeWidth > 0f
                                && word.SoftBreaks.Count == 0
                                && wrap is Wrap.WordOrGlyph or Wrap.WordOrGlyphMinContent
                                && wordWidth > limit)
                            {
                                AddToVisualLine(current, spanIndex, (i + 1, 0), fittingStart, wordRangeWidth, blanks);
                                visualLines.Add(current);
                                current = new VisualLine();
                                blanks = 0;
                                wordRangeWidth = 0f;
                                fittingStart = (i, 0);
                            }

                            List<int> boundaries = [0, .. breakIndices, word.Glyphs.Count];
                            for (int b = boundaries.Count - 2; b >= 0; b--)
                            {
                                int start = boundaries[b];
                                int end = boundaries[b + 1];
                                float chunkWidth = 0f;
                                for (int g = start; g < end; g++)
                                {
                                    chunkWidth += word.Glyphs[g].Width(fontSize);
                                }

                                if (current.W + (wordRangeWidth + chunkWidth) <= limit
                                    || (current.Ranges.Count == 0 && wordRangeWidth == 0f))
                                {
                                    wordRangeWidth += chunkWidth;
                                    continue;
                                }

                                AddToVisualLine(current, spanIndex, (i, end), fittingStart, wordRangeWidth, blanks);
                                visualLines.Add(current);
                                current = new VisualLine();
                                blanks = 0;
                                wordRangeWidth = chunkWidth;
                                fittingStart = (i, end);
                            }
                        }
                        else
                        {
                            if (wordRangeWidth > 0f)
                            {
                                bool trailingBlank = i + 1 < span.Words.Count && span.Words[i + 1].Blank;
                                if (trailingBlank)
                                {
                                    blanks = blanks > 0 ? blanks - 1 : 0;
                                    AddToVisualLine(current, spanIndex, (i + 2, 0), fittingStart, widthBeforeLastBlank, blanks);
                                }
                                else
                                {
                                    AddToVisualLine(current, spanIndex, (i + 1, 0), fittingStart, wordRangeWidth, blanks);
                                }

                                visualLines.Add(current);
                                current = new VisualLine();
                                blanks = 0;
                            }

                            if (word.Blank)
                            {
                                wordRangeWidth = 0f;
                                fittingStart = (i, 0);
                            }
                            else
                            {
                                wordRangeWidth = wordWidth;
                                fittingStart = (i + 1, 0);
                            }
                        }
                    }

                    AddToVisualLine(current, spanIndex, (0, 0), fittingStart, wordRangeWidth, blanks);
                }
                else
                {
                    (int Word, int Glyph) fittingStart = (0, 0);
                    for (int i = 0; i < span.Words.Count; i++)
                    {
                        ShapeWord word = span.Words[i];
                        float wordWidth = word.Width(fontSize);
                        if (current.W + (wordRangeWidth + wordWidth) <= limit
                            || (word.Blank && current.W + wordRangeWidth <= limit))
                        {
                            if (word.Blank)
                            {
                                blanks++;
                                widthBeforeLastBlank = wordRangeWidth;
                            }

                            wordRangeWidth += wordWidth;
                            continue;
                        }

                        bool emergency = wrap == Wrap.Glyph
                            || (wrap is Wrap.WordOrGlyph or Wrap.WordOrGlyphMinContent && wordWidth > limit);
                        List<int> breakIndices = word.BreakIndices(wrap, emergency);
                        if (breakIndices.Count > 0)
                        {
                            if (wordRangeWidth > 0f
                                && word.SoftBreaks.Count == 0
                                && wrap is Wrap.WordOrGlyph or Wrap.WordOrGlyphMinContent
                                && wordWidth > limit)
                            {
                                AddToVisualLine(current, spanIndex, fittingStart, (i, 0), wordRangeWidth, blanks);
                                visualLines.Add(current);
                                current = new VisualLine();
                                blanks = 0;
                                wordRangeWidth = 0f;
                                fittingStart = (i, 0);
                            }

                            List<int> boundaries = [0, .. breakIndices, word.Glyphs.Count];
                            for (int b = 0; b + 1 < boundaries.Count; b++)
                            {
                                int start = boundaries[b];
                                int end = boundaries[b + 1];
                                float chunkWidth = 0f;
                                for (int g = start; g < end; g++)
                                {
                                    chunkWidth += word.Glyphs[g].Width(fontSize);
                                }

                                if (current.W + (wordRangeWidth + chunkWidth) <= limit
                                    || (current.Ranges.Count == 0 && wordRangeWidth == 0f))
                                {
                                    wordRangeWidth += chunkWidth;
                                    continue;
                                }

                                AddToVisualLine(current, spanIndex, fittingStart, (i, start), wordRangeWidth, blanks);
                                visualLines.Add(current);
                                current = new VisualLine();
                                blanks = 0;
                                wordRangeWidth = chunkWidth;
                                fittingStart = (i, start);
                            }
                        }
                        else
                        {
                            if (wordRangeWidth > 0f)
                            {
                                bool trailingBlank = i > 0 && span.Words[i - 1].Blank;
                                if (trailingBlank)
                                {
                                    blanks = blanks > 0 ? blanks - 1 : 0;
                                    AddToVisualLine(current, spanIndex, fittingStart, (i - 1, 0), widthBeforeLastBlank, blanks);
                                }
                                else
                                {
                                    AddToVisualLine(current, spanIndex, fittingStart, (i, 0), wordRangeWidth, blanks);
                                }

                                visualLines.Add(current);
                                current = new VisualLine();
                                blanks = 0;
                            }

                            if (word.Blank)
                            {
                                wordRangeWidth = 0f;
                                fittingStart = (i + 1, 0);
                            }
                            else
                            {
                                wordRangeWidth = wordWidth;
                                fittingStart = (i, 0);
                            }
                        }
                    }

                    AddToVisualLine(current, spanIndex, fittingStart, (span.Words.Count, 0), wordRangeWidth, blanks);
                }
            }
        }

        if (current.Ranges.Count > 0)
        {
            visualLines.Add(current);
        }

        Align align = alignOpt ?? (shape.Rtl ? Align.Right : Align.Left);

        float lineWidth;
        if (widthOpt is { } definite)
        {
            lineWidth = definite;
        }
        else
        {
            lineWidth = 0f;
            foreach (VisualLine visual in visualLines)
            {
                lineWidth = F32.Max(lineWidth, visual.W);
            }
        }

        float startX = shape.Rtl ? lineWidth : 0f;
        int visualCount = visualLines.Count;
        for (int index = 0; index < visualCount; index++)
        {
            VisualLine visual = visualLines[index];
            if (visual.Ranges.Count == 0)
            {
                continue;
            }

            List<(int Start, int End)> newOrder = Bidi.Reorder(shape, visual.Ranges);
            var glyphs = new List<LayoutGlyph>(1);
            float x = startX;
            float y = 0f;
            float maxAscent = 0f;
            float maxDescent = 0f;
            float correction = (align, shape.Rtl) switch
            {
                (Align.Left, true) => lineWidth - visual.W,
                (Align.Left, false) => 0f,
                (Align.Right, true) => 0f,
                (Align.Right, false) => lineWidth - visual.W,
                (Align.Center, _) => (lineWidth - visual.W) / 2f,
                (Align.End, _) => lineWidth - visual.W,
                _ => 0f,
            };

            if (shape.Rtl)
            {
                x -= correction;
            }
            else
            {
                x += correction;
            }

            float justification = align == Align.Justified && visual.Spaces > 0 && index != visualCount - 1
                ? (lineWidth - visual.W) / visual.Spaces
                : 0f;

            void ProcessRange((int Start, int End) range)
            {
                for (int r = range.Start; r < range.End; r++)
                {
                    (int spanIndex, (int startingWord, int startingGlyph), (int endingWord, int endingGlyph)) = visual.Ranges[r];
                    ShapeSpan span = shape.Spans[spanIndex];
                    int last = endingWord + (endingGlyph != 0 ? 1 : 0);
                    for (int i = startingWord; i < last && i < span.Words.Count; i++)
                    {
                        ShapeWord word = span.Words[i];
                        int from = i == startingWord ? startingGlyph : 0;
                        int to = i == endingWord ? endingGlyph : word.Glyphs.Count;
                        for (int g = from; g < to && g < word.Glyphs.Count; g++)
                        {
                            ShapeGlyph glyph = word.Glyphs[g];
                            float glyphFontSize = glyph.Metrics?.FontSize ?? fontSize;
                            float xAdvance = (glyphFontSize * glyph.XAdvance) + (word.Blank ? justification : 0f);
                            if (shape.Rtl)
                            {
                                x -= xAdvance;
                            }

                            float yAdvance = glyphFontSize * glyph.YAdvance;
                            glyphs.Add(new LayoutGlyph
                            {
                                Start = glyph.Start,
                                End = glyph.End,
                                FontSize = glyphFontSize,
                                LineHeight = glyph.Metrics?.LineHeight,
                                FontId = glyph.FontId,
                                GlyphId = glyph.GlyphId,
                                FontIsVariable = glyph.FontIsVariable,
                                FontWeightAxis = glyph.FontWeightAxis,
                                FontOpticalSize = glyph.FontOpticalSize,
                                FontItalicAxis = glyph.FontItalicAxis,
                                X = x,
                                Y = y,
                                W = xAdvance,
                                Level = span.Level,
                                XOffset = glyph.XOffset,
                                YOffset = glyph.YOffset,
                                Color = glyph.Color,
                                Metadata = glyph.Metadata,
                                FakeItalic = glyph.FakeItalic,
                            });
                            if (!shape.Rtl)
                            {
                                x += xAdvance;
                            }

                            y += yAdvance;
                            maxAscent = F32.Max(maxAscent, glyphFontSize * glyph.Ascent);
                            maxDescent = F32.Max(maxDescent, glyphFontSize * glyph.Descent);
                        }
                    }
                }
            }

            if (shape.Rtl)
            {
                for (int r = newOrder.Count - 1; r >= 0; r--)
                {
                    ProcessRange(newOrder[r]);
                }
            }
            else
            {
                foreach ((int Start, int End) range in newOrder)
                {
                    ProcessRange(range);
                }
            }

            float? lineHeight = null;
            foreach (LayoutGlyph glyph in glyphs)
            {
                if (glyph.LineHeight is { } height)
                {
                    lineHeight = lineHeight is { } current2 ? F32.Max(current2, height) : height;
                }
            }

            layoutLines.Add(new LayoutLine
            {
                W = align != Align.Justified ? visual.W : (shape.Rtl ? startX - x : x),
                MaxAscent = maxAscent,
                MaxDescent = maxDescent,
                LineHeight = lineHeight,
                Glyphs = glyphs,
            });
        }

        // This is used to create a visual line for empty lines (e.g. lines with only a break).
        if (layoutLines.Count == 0)
        {
            layoutLines.Add(new LayoutLine
            {
                W = 0f,
                MaxAscent = 0f,
                MaxDescent = 0f,
                LineHeight = shape.Metrics?.LineHeight,
                Glyphs = [],
            });
        }

        return layoutLines;
    }
}
