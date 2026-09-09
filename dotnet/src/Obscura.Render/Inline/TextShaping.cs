using System.Text;
using HarfBuzzSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace Obscura.Render;

/// <summary>One shaped glyph, in em-relative units.</summary>
/// <remarks>
/// Port of cosmic-text's <c>ShapeGlyph</c> including the vendored variable-font fields. The
/// coordinates recorded here are exactly the ones the face was varied with while shaping, so
/// rasterization can rebuild the same instance.
/// </remarks>
public struct ShapeGlyph
{
    public int Start;
    public int End;
    public float XAdvance;
    public float YAdvance;
    public float XOffset;
    public float YOffset;
    public float Ascent;
    public float Descent;
    public FontId FontId;
    public ushort GlyphId;
    public bool FontIsVariable;
    public float? FontWeightAxis;
    public float? FontOpticalSize;
    public bool FontItalicAxis;
    public RgbaColor? Color;
    public ulong Metadata;
    public bool FakeItalic;
    public (float FontSize, float LineHeight)? Metrics;

    /// <summary>Width at the given font size, honoring a per-span metrics override.</summary>
    public readonly float Width(float fontSize) => (Metrics?.FontSize ?? fontSize) * XAdvance;
}

/// <summary>A shaped word: the unit line breaking moves around.</summary>
public sealed class ShapeWord
{
    public bool Blank;
    public List<ShapeGlyph> Glyphs = [];

    /// <summary>Additional ordinary soft-wrap positions inside this UAX#14 word.</summary>
    internal List<int> SoftBreaks = [];

    /// <summary>Emergency positions used by constrained layout.</summary>
    internal List<int> EmergencyBreaks = [];

    /// <summary>Emergency positions that also contribute to min-content.</summary>
    internal List<int> MinContentBreaks = [];

    /// <summary>Whether this word came from CSS-policy-bearing rich text.</summary>
    internal bool CustomLineBreaks;

    public float Width(float fontSize)
    {
        float width = 0f;
        foreach (ShapeGlyph glyph in Glyphs)
        {
            width += glyph.Width(fontSize);
        }

        return width;
    }

    internal List<int> ClusterBoundaries()
    {
        List<int> result = [];
        for (int index = 1; index < Glyphs.Count; index++)
        {
            ShapeGlyph before = Glyphs[index - 1];
            ShapeGlyph after = Glyphs[index];
            if (before.End <= after.Start || after.End <= before.Start)
            {
                result.Add(index);
            }
        }

        return result;
    }

    internal List<int> BreakIndices(Wrap wrap, bool useEmergency)
    {
        if (wrap == Wrap.Glyph || (!CustomLineBreaks && useEmergency))
        {
            return ClusterBoundaries();
        }

        List<int> breaks = [.. SoftBreaks];
        if (useEmergency)
        {
            List<int> emergency = wrap == Wrap.WordOrGlyphMinContent ? MinContentBreaks : EmergencyBreaks;
            breaks.AddRange(emergency);
            breaks.Sort();
            int write = 0;
            for (int i = 0; i < breaks.Count; i++)
            {
                if (i == 0 || breaks[i] != breaks[i - 1])
                {
                    breaks[write++] = breaks[i];
                }
            }

            breaks.RemoveRange(write, breaks.Count - write);
        }

        return breaks;
    }

    internal List<int> GlyphIndicesForOffsets(List<int> offsets)
    {
        offsets.Sort();
        List<int> result = [];
        for (int index = 1; index < Glyphs.Count; index++)
        {
            ShapeGlyph before = Glyphs[index - 1];
            ShapeGlyph after = Glyphs[index];
            int offset;
            if (before.End <= after.Start)
            {
                offset = after.Start;
            }
            else if (after.End <= before.Start)
            {
                offset = before.Start;
            }
            else
            {
                continue;
            }

            if (offsets.BinarySearch(offset) >= 0)
            {
                result.Add(index);
            }
        }

        return result;
    }

    internal void SetLineBreaks(bool custom, List<int> soft, List<int> emergency, List<int> minContent)
    {
        SoftBreaks = GlyphIndicesForOffsets(soft);
        EmergencyBreaks = GlyphIndicesForOffsets(emergency);
        MinContentBreaks = GlyphIndicesForOffsets(minContent);
        CustomLineBreaks = custom;
    }

    internal void ReverseGlyphs()
    {
        Glyphs.Reverse();
        int length = Glyphs.Count;
        foreach (List<int> breaks in new[] { SoftBreaks, EmergencyBreaks, MinContentBreaks })
        {
            for (int i = 0; i < breaks.Count; i++)
            {
                breaks[i] = Math.Max(0, length - breaks[i]);
            }

            breaks.Sort();
        }
    }
}

/// <summary>A shaped run of one bidi level.</summary>
public sealed class ShapeSpan
{
    public byte Level;
    public List<ShapeWord> Words = [];

    public bool IsRtl => (Level & 1) != 0;
}

/// <summary>The break opportunities one CSS-policy-aware span contributes.</summary>
internal sealed class CssBreakData
{
    public List<int> WordBreaks = [];
    public List<int> SoftBreaks = [];
    public List<int> EmergencyBreaks = [];
    public List<int> MinContentBreaks = [];
}

/// <summary>
/// One paragraph (mandatory-break-delimited) of shaped text.
/// </summary>
public sealed class ShapeLine
{
    public bool Rtl;
    public List<ShapeSpan> Spans = [];
    public (float FontSize, float LineHeight)? Metrics;
}

/// <summary>
/// HarfBuzz shaping with the engine's own font fallback.
/// </summary>
/// <remarks>
/// PORT NOTE. cosmic-text drives rustybuzz; this drives HarfBuzz through HarfBuzzSharp. The
/// two are the same shaping engine family (rustybuzz is a Rust port of HarfBuzz), so glyph
/// selection and advances agree for the faces the engine ships. Two structural differences
/// remain and are documented on <see cref="TextEngine"/>: cluster offsets here are UTF-16 code
/// units rather than UTF-8 bytes (an internal convention, consistent end to end), and font
/// fallback walks the loaded database in load order rather than cosmic-text's per-script
/// platform fallback lists.
/// </remarks>
public sealed class TextShaper(FontDatabase database)
{
    private readonly FontDatabase _database = database;

    public FontDatabase Database => _database;

    /// <summary>Shape one paragraph into spans and words.</summary>
    public ShapeLine ShapeParagraph(string line, AttrsList attrsList, int tabWidth)
    {
        var result = new ShapeLine();
        List<(int Start, int End, byte Level)> runs = Bidi.LevelRuns(line, out bool rtl);
        result.Rtl = rtl;
        foreach ((int start, int end, byte level) in runs)
        {
            result.Spans.Add(BuildSpan(line, attrsList, start, end, rtl, level));
        }

        // Adjust for tabs: they are shaped as spaces, so they always carry a space's advance.
        float x = 0f;
        foreach (ShapeSpan span in result.Spans)
        {
            foreach (ShapeWord word in span.Words)
            {
                for (int i = 0; i < word.Glyphs.Count; i++)
                {
                    ShapeGlyph glyph = word.Glyphs[i];
                    if (glyph.End - glyph.Start == 1 && line[glyph.Start] == '\t')
                    {
                        float tabAdvance = tabWidth * glyph.XAdvance;
                        if (tabAdvance > 0f)
                        {
                            float tabStop = (MathF.Floor(x / tabAdvance) + 1f) * tabAdvance;
                            glyph.XAdvance = tabStop - x;
                            word.Glyphs[i] = glyph;
                        }
                    }

                    x += glyph.XAdvance;
                }
            }
        }

        result.Metrics = attrsList.Defaults.Metrics;
        return result;
    }

    private ShapeSpan BuildSpan(string line, AttrsList attrsList, int start, int end, bool lineRtl, byte level)
    {
        var span = new ShapeSpan { Level = level };
        string text = line[start..end];
        CssBreakData breaks = BuildBreakData(text, start, attrsList);

        int startWord = 0;
        foreach (int endLb in breaks.WordBreaks)
        {
            int startLb = endLb;
            for (int i = endLb - 1; i >= startWord; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    startLb = i;
                }
                else
                {
                    break;
                }
            }

            if (startWord < startLb)
            {
                ShapeWord word = BuildWord(line, attrsList, start + startWord, start + startLb, level, blank: false);
                bool custom = false;
                for (int offset = start + startWord; offset < start + startLb; offset++)
                {
                    if (attrsList.GetSpan(offset).CssLineBreakPolicy is not null)
                    {
                        custom = true;
                        break;
                    }
                }

                word.SetLineBreaks(
                    custom,
                    Slice(breaks.SoftBreaks, start + startWord, start + startLb),
                    Slice(breaks.EmergencyBreaks, start + startWord, start + startLb),
                    Slice(breaks.MinContentBreaks, start + startWord, start + startLb));
                span.Words.Add(word);
            }

            if (startLb < endLb)
            {
                // Each whitespace character is its own blank word, exactly as cosmic-text
                // builds them; justification counts blanks and wrapping drops a trailing one.
                int i = startLb;
                while (i < endLb)
                {
                    int width = char.IsHighSurrogate(text[i]) && i + 1 < endLb ? 2 : 1;
                    ShapeWord word = BuildWord(line, attrsList, start + i, start + i + width, level, blank: true);
                    word.SetLineBreaks(
                        attrsList.GetSpan(start + i).CssLineBreakPolicy is not null,
                        [],
                        [],
                        []);
                    span.Words.Add(word);
                    i += width;
                }
            }

            startWord = endLb;
        }

        if (lineRtl)
        {
            foreach (ShapeWord word in span.Words)
            {
                word.ReverseGlyphs();
            }
        }

        if (lineRtl != span.IsRtl)
        {
            span.Words.Reverse();
        }

        return span;
    }

    private static List<int> Slice(List<int> offsets, int start, int end)
    {
        List<int> result = [];
        foreach (int offset in offsets)
        {
            if (offset > start && offset < end)
            {
                result.Add(offset);
            }
        }

        return result;
    }

    /// <summary>
    /// Break opportunities for one span, applying the Blink tailorings cosmic-text carries.
    /// </summary>
    private static CssBreakData BuildBreakData(string span, int spanStart, AttrsList attrsList)
    {
        List<int> normalBreaks = LineBreaking.Breaks(span);
        var clusters = new List<(int End, BreakClass Class, BreakClass BreakAllClass, bool VerticalLine, CssLineBreak? Policy)>();
        foreach ((int start, int length) in LineBreaking.GraphemeClusters(span))
        {
            clusters.Add((
                start + length,
                LineBreaking.ClusterClass(span, start, length),
                LineBreaking.BreakAllClass(span, start, length),
                length == 1 && span[start] == '|',
                attrsList.GetSpan(spanStart + start).CssLineBreakPolicy));
        }

        var data = new CssBreakData();
        for (int i = 0; i + 1 < clusters.Count; i++)
        {
            var before = clusters[i];
            var after = clusters[i + 1];
            int offset = before.End;
            int absolute = spanStart + offset;
            bool normalBreak = normalBreaks.BinarySearch(offset) >= 0;
            if (before.Policy is not { } policy)
            {
                if (normalBreak)
                {
                    data.WordBreaks.Add(offset);
                }

                continue;
            }

            if (!policy.Wrap)
            {
                continue;
            }

            bool keepAll = policy.WordBreak == WordBreak.KeepAll
                && LineBreaking.KeepAllWordClass(before.Class)
                && LineBreaking.KeepAllWordClass(after.Class);
            if (BlinkNormalBreak(normalBreak, before.Class, before.VerticalLine, after.Class) && !keepAll)
            {
                data.WordBreaks.Add(offset);
            }
            else if (policy.WordBreak == WordBreak.BreakAll
                && BreakAllPair(before.BreakAllClass, after.BreakAllClass, after.VerticalLine))
            {
                data.SoftBreaks.Add(absolute);
            }

            bool anywhere = policy.WordBreak == WordBreak.BreakWord
                || policy.OverflowWrap == OverflowWrap.Anywhere;
            bool emergency = anywhere || policy.OverflowWrap == OverflowWrap.BreakWord;
            if (emergency)
            {
                data.EmergencyBreaks.Add(absolute);
            }

            if (anywhere)
            {
                data.MinContentBreaks.Add(absolute);
            }
        }

        // One ShapeLine is one mandatory-break segment. Its end must terminate the final word
        // even when the preceding CSS span disables soft wrapping.
        data.WordBreaks.Add(span.Length);
        data.WordBreaks.Sort();
        int write = 0;
        for (int i = 0; i < data.WordBreaks.Count; i++)
        {
            if (i == 0 || data.WordBreaks[i] != data.WordBreaks[i - 1])
            {
                data.WordBreaks[write++] = data.WordBreaks[i];
            }
        }

        data.WordBreaks.RemoveRange(write, data.WordBreaks.Count - write);
        return data;
    }

    /// <summary>
    /// UAX#14 keeps ASCII solidus and vertical line inside ordinary word runs (<c>a/b</c>,
    /// <c>a|b</c>) in Blink's ICU iterator. Blink's break-all tailoring then adds the narrower
    /// opportunities explicitly.
    /// </summary>
    private static bool BlinkNormalBreak(bool normalBreak, BreakClass before, bool verticalLine, BreakClass after)
    {
        if (!normalBreak)
        {
            return false;
        }

        return !(verticalLine || (before == BreakClass.Symbol && LineBreaking.KeepAllWordClass(after)));
    }

    /// <summary>
    /// CSS <c>break-all</c> adds opportunities between typographic letter units. The
    /// surrounding UAX#14 iterator continues to own punctuation, emoji, spaces, joiners, and
    /// mandatory breaks.
    /// </summary>
    private static bool BreakAllPair(BreakClass before, BreakClass after, bool afterVerticalLine)
    {
        // Rust's `BreakClass::After` is UAX#14 class BA, which this port names `BreakAfter`.
        bool afterVertical = after == BreakClass.BreakAfter && afterVerticalLine;
        bool alLike = after is BreakClass.Ambiguous or BreakClass.Alphabetic or BreakClass.Hyphen
            or BreakClass.Numeric or BreakClass.OpenPunctuation or BreakClass.Prefix
            or BreakClass.ComplexContext or BreakClass.HebrewLetter || afterVertical;
        return before switch
        {
            BreakClass.Ambiguous or BreakClass.Alphabetic or BreakClass.BreakAfter or BreakClass.Numeric
                or BreakClass.ComplexContext or BreakClass.Symbol or BreakClass.HebrewLetter => alLike,
            BreakClass.ClosePunctuation => after is BreakClass.Ambiguous or BreakClass.Alphabetic
                or BreakClass.Hyphen or BreakClass.Numeric or BreakClass.Prefix
                or BreakClass.HebrewLetter || afterVertical,
            BreakClass.CloseParenthesis => after is BreakClass.Ambiguous or BreakClass.Alphabetic
                or BreakClass.Hyphen or BreakClass.Numeric or BreakClass.Prefix
                or BreakClass.ComplexContext or BreakClass.HebrewLetter || afterVertical,
            BreakClass.Exclamation or BreakClass.Postfix => after is BreakClass.Ambiguous
                or BreakClass.Alphabetic or BreakClass.Hyphen or BreakClass.Numeric
                or BreakClass.Postfix or BreakClass.Prefix or BreakClass.HebrewLetter
                || afterVertical,
            BreakClass.InfixSeparator => after is BreakClass.Ambiguous or BreakClass.Alphabetic
                or BreakClass.Hyphen or BreakClass.Numeric or BreakClass.HebrewLetter
                || afterVertical,
            BreakClass.Hyphen => after == BreakClass.Numeric,
            BreakClass.Prefix => after == BreakClass.Postfix,
            _ => false,
        };
    }

    private ShapeWord BuildWord(string line, AttrsList attrsList, int start, int end, byte level, bool blank)
    {
        var word = new ShapeWord { Blank = blank };
        bool spanRtl = (level & 1) != 0;
        int startRun = start;
        TextAttrs attrs = attrsList.Defaults;
        foreach ((int clusterStart, int _) in LineBreaking.GraphemeClusters(line[start..end]))
        {
            int startCluster = start + clusterStart;
            TextAttrs clusterAttrs = attrsList.GetSpan(startCluster);
            if (!attrs.Compatible(clusterAttrs))
            {
                ShapeRun(word.Glyphs, line, attrsList, startRun, startCluster, spanRtl);
                startRun = startCluster;
                attrs = clusterAttrs;
            }
        }

        if (startRun < end)
        {
            ShapeRun(word.Glyphs, line, attrsList, startRun, end, spanRtl);
        }

        return word;
    }

    /// <summary>Shape one attribute-uniform run, falling back per cluster for missing glyphs.</summary>
    private void ShapeRun(List<ShapeGlyph> glyphs, string line, AttrsList attrsList, int startRun, int endRun, bool spanRtl)
    {
        if (endRun <= startRun)
        {
            return;
        }

        TextAttrs attrs = attrsList.GetSpan(startRun);
        FaceRecord? selected = attrs.FontId is { } id ? _database.Face(id) : null;
        List<FaceRecord> order = [.. _database.FallbackOrder(
            attrs.FontId,
            attrs.Family,
            attrs.Weight,
            attrs.Style != FaceStyle.Normal)];
        if (order.Count == 0)
        {
            return;
        }

        FaceRecord first = selected ?? order[0];
        int glyphStart = glyphs.Count;
        List<int> missing = ShapeFallback(glyphs, first, line, attrsList, startRun, endRun, spanRtl);

        int next = 0;
        while (missing.Count > 0)
        {
            FaceRecord? font = null;
            while (next < order.Count)
            {
                FaceRecord candidate = order[next++];
                if (!ReferenceEquals(candidate, first))
                {
                    font = candidate;
                    break;
                }
            }

            if (font is null)
            {
                break;
            }

            List<ShapeGlyph> fallbackGlyphs = [];
            List<int> fallbackMissing = ShapeFallback(fallbackGlyphs, font, line, attrsList, startRun, endRun, spanRtl);

            int fb = 0;
            while (fb < fallbackGlyphs.Count)
            {
                int start = fallbackGlyphs[fb].Start;
                int end = fallbackGlyphs[fb].End;
                if (!missing.Contains(start) || fallbackMissing.Contains(start))
                {
                    fb++;
                    continue;
                }

                int missingIndex = 0;
                while (missingIndex < missing.Count)
                {
                    if (missing[missingIndex] >= start && missing[missingIndex] < end)
                    {
                        missing.RemoveAt(missingIndex);
                    }
                    else
                    {
                        missingIndex++;
                    }
                }

                int i = glyphStart;
                while (i < glyphs.Count)
                {
                    if (glyphs[i].Start >= start && glyphs[i].End <= end)
                    {
                        break;
                    }

                    i++;
                }

                while (i < glyphs.Count && glyphs[i].Start >= start && glyphs[i].End <= end)
                {
                    glyphs.RemoveAt(i);
                }

                while (fb < fallbackGlyphs.Count
                    && fallbackGlyphs[fb].Start >= start
                    && fallbackGlyphs[fb].End <= end)
                {
                    glyphs.Insert(i, fallbackGlyphs[fb]);
                    fallbackGlyphs.RemoveAt(fb);
                    i++;
                }
            }
        }
    }

    /// <summary>
    /// Shape one run with one face, returning the cluster starts it could not represent.
    /// </summary>
    /// <remarks>
    /// This is the shaping half of the vendored variable-font fix: the face is instanced with
    /// the span's canonical coordinates once, and metrics, glyph selection, and advances all
    /// come from that same instance.
    /// </remarks>
    private List<int> ShapeFallback(
        List<ShapeGlyph> glyphs,
        FaceRecord font,
        string line,
        AttrsList attrsList,
        int startRun,
        int endRun,
        bool spanRtl)
    {
        string run = line[startRun..endRun];
        TextAttrs attrs = attrsList.GetSpan(startRun);
        FontVariations? shapingVariations = ShapingVariations(font, attrs);
        HbFont hbFont = font.HarfBuzzFontFor(shapingVariations);

        float fontScale = font.UnitsPerEm;
        float ascent = font.Metrics.Ascent / fontScale;
        float descent = font.Metrics.Descent / fontScale;

        using var buffer = new HbBuffer();
        buffer.Direction = spanRtl ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.ClusterLevel = ClusterLevel.MonotoneCharacters;
        buffer.AddUtf16(run.Contains('\t', StringComparison.Ordinal) ? run.Replace('\t', ' ') : run);
        buffer.GuessSegmentProperties();
        buffer.Direction = spanRtl ? Direction.RightToLeft : Direction.LeftToRight;

        Feature[] features = new Feature[attrs.Features.Length];
        for (int i = 0; i < attrs.Features.Length; i++)
        {
            features[i] = new Feature((Tag)attrs.Features[i].Tag, attrs.Features[i].Value, 0, uint.MaxValue);
        }

        hbFont.Shape(buffer, features);
        GlyphInfo[] infos = buffer.GlyphInfos;
        GlyphPosition[] positions = buffer.GlyphPositions;

        List<int> missing = [];
        int glyphStart = glyphs.Count;
        for (int index = 0; index < infos.Length; index++)
        {
            GlyphInfo info = infos[index];
            GlyphPosition position = positions[index];
            int startGlyph = startRun + (int)info.Cluster;
            if (info.Codepoint == 0)
            {
                missing.Add(startGlyph);
            }

            TextAttrs glyphAttrs = attrsList.GetSpan(startGlyph);
            float letterSpacing = 0f;
            if (glyphAttrs.LetterSpacingEm is { } spacing && spacing != 0f)
            {
                bool clusterEnd = index + 1 >= infos.Length || infos[index + 1].Cluster != info.Cluster;
                if (clusterEnd && ClusterAllowsLetterSpacing(line, startGlyph))
                {
                    letterSpacing = spacing;
                }
            }

            glyphs.Add(new ShapeGlyph
            {
                Start = startGlyph,
                End = endRun,
                XAdvance = (position.XAdvance / fontScale) + letterSpacing,
                YAdvance = position.YAdvance / fontScale,
                XOffset = position.XOffset / fontScale,
                YOffset = position.YOffset / fontScale,
                Ascent = ascent,
                Descent = descent,
                FontId = font.Id,
                GlyphId = (ushort)info.Codepoint,
                FontIsVariable = font.IsVariable,
                FontWeightAxis = glyphAttrs.FontWeightAxis,
                FontOpticalSize = glyphAttrs.FontOpticalSize,
                FontItalicAxis = glyphAttrs.FontItalicAxis,
                Color = glyphAttrs.Color,
                Metadata = glyphAttrs.Metadata,
                FakeItalic = glyphAttrs.FakeItalic,
                Metrics = glyphAttrs.Metrics,
            });
        }

        // Adjust end of glyphs.
        if (spanRtl)
        {
            for (int i = glyphStart + 1; i < glyphs.Count; i++)
            {
                int nextStart = glyphs[i - 1].Start;
                int nextEnd = glyphs[i - 1].End;
                ShapeGlyph previous = glyphs[i];
                previous.End = previous.Start == nextStart ? nextEnd : nextStart;
                glyphs[i] = previous;
            }
        }
        else
        {
            for (int i = glyphs.Count - 1; i > glyphStart; i--)
            {
                int nextStart = glyphs[i].Start;
                int nextEnd = glyphs[i].End;
                ShapeGlyph previous = glyphs[i - 1];
                previous.End = previous.Start == nextStart ? nextEnd : nextStart;
                glyphs[i - 1] = previous;
            }
        }

        return missing;
    }

    /// <summary>
    /// The canonical axis tuple one span shapes with.
    /// </summary>
    /// <remarks>
    /// This is the port of the vendored cosmic-text change in <c>shape_fallback</c>: clone the
    /// face only when the span requests non-default coordinates, apply the automatic
    /// <c>wght</c> and <c>opsz</c> axes, synthesize italic through <c>ital</c> (or <c>slnt</c>
    /// when the face has no <c>ital</c>), then apply the authored low-level settings last so
    /// they win.
    /// </remarks>
    public static FontVariations? ShapingVariations(FaceRecord font, TextAttrs attrs)
    {
        if (!font.IsVariable)
        {
            return null;
        }

        bool explicitEmpty = attrs.Variations is null || attrs.Variations.IsEmpty;
        if (explicitEmpty
            && attrs.FontWeightAxis is null
            && attrs.FontOpticalSize is null
            && !attrs.FontItalicAxis)
        {
            return null;
        }

        var variations = new FontVariations();
        if (attrs.FontWeightAxis is { } weight && float.IsFinite(weight))
        {
            variations.Set(VariationTag.FromAscii("wght"), weight);
        }

        if (attrs.FontOpticalSize is { } optical && float.IsFinite(optical))
        {
            variations.Set(VariationTag.FromAscii("opsz"), optical);
        }

        if (attrs.FontItalicAxis)
        {
            VariationTag italic = VariationTag.FromAscii("ital");
            bool hasItalic = false;
            foreach (FontAxis axis in font.Axes)
            {
                if (axis.Tag == italic)
                {
                    hasItalic = true;
                    break;
                }
            }

            if (hasItalic)
            {
                variations.Set(italic, 1f);
            }
            else
            {
                variations.Set(VariationTag.FromAscii("slnt"), -14f);
            }
        }

        if (attrs.Variations is { } authored)
        {
            foreach (FontVariation variation in authored.Items)
            {
                if (float.IsFinite(variation.Value.Value))
                {
                    variations.Set(variation.Tag, variation.Value.Value);
                }
            }
        }

        return variations.IsEmpty ? null : variations;
    }

    /// <summary>Cursive scripts do not accept tracking between joined letters.</summary>
    private static bool ClusterAllowsLetterSpacing(string line, int clusterStart)
    {
        if (clusterStart >= line.Length)
        {
            return true;
        }

        Rune.DecodeFromUtf16(line.AsSpan(clusterStart), out Rune rune, out _);
        int ch = rune.Value;
        return ch switch
        {
            >= 0x0600 and <= 0x06FF => false, // Arabic
            >= 0x0750 and <= 0x077F => false, // Arabic Supplement
            >= 0x08A0 and <= 0x08FF => false, // Arabic Extended-A
            >= 0xFB50 and <= 0xFDFF => false, // Arabic Presentation Forms-A
            >= 0xFE70 and <= 0xFEFF => false, // Arabic Presentation Forms-B
            >= 0x0700 and <= 0x074F => false, // Syriac
            >= 0x0840 and <= 0x085F => false, // Mandaic
            >= 0x07C0 and <= 0x07FF => false, // NKo
            _ => true,
        };
    }
}
