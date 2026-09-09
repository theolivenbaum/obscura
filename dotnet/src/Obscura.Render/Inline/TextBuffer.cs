using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>Wrapping mode. Mirrors the vendored cosmic-text <c>Wrap</c>, extra variant included.</summary>
public enum Wrap
{
    /// <summary>No wrapping.</summary>
    None,

    /// <summary>Wraps at a glyph level.</summary>
    Glyph,

    /// <summary>Wraps at the word level.</summary>
    Word,

    /// <summary>Word level, falling back to glyph level for a word that cannot fit alone.</summary>
    WordOrGlyph,

    /// <summary>
    /// Browser min-content wrapping. Like <see cref="WordOrGlyph"/>, but only span boundaries
    /// contributed by <c>overflow-wrap:anywhere</c> (and legacy <c>word-break:break-word</c>)
    /// participate.
    /// </summary>
    WordOrGlyphMinContent,
}

/// <summary>Horizontal alignment of a laid out line.</summary>
public enum Align
{
    Left,
    Right,
    Center,
    Justified,
    End,
}

/// <summary>Font size and line height, in pixels.</summary>
public readonly record struct TextMetrics(float FontSize, float LineHeight);

/// <summary>Binning of a subpixel position, for glyph cache identity.</summary>
public enum SubpixelBin
{
    Zero,
    One,
    Two,
    Three,
}

public static class SubpixelBinExtensions
{
    public static (int Integer, SubpixelBin Bin) New(float position)
    {
        int trunc = (int)position;
        float fract = position - trunc;
        if (float.IsNegative(position))
        {
            if (fract > -0.125f)
            {
                return (trunc, SubpixelBin.Zero);
            }

            if (fract > -0.375f)
            {
                return (trunc - 1, SubpixelBin.Three);
            }

            if (fract > -0.625f)
            {
                return (trunc - 1, SubpixelBin.Two);
            }

            if (fract > -0.875f)
            {
                return (trunc - 1, SubpixelBin.One);
            }

            return (trunc - 1, SubpixelBin.Zero);
        }

        if (fract < 0.125f)
        {
            return (trunc, SubpixelBin.Zero);
        }

        if (fract < 0.375f)
        {
            return (trunc, SubpixelBin.One);
        }

        if (fract < 0.625f)
        {
            return (trunc, SubpixelBin.Two);
        }

        if (fract < 0.875f)
        {
            return (trunc, SubpixelBin.Three);
        }

        return (trunc + 1, SubpixelBin.Zero);
    }

    public static float AsFloat(this SubpixelBin bin) => bin switch
    {
        SubpixelBin.One => 0.25f,
        SubpixelBin.Two => 0.5f,
        SubpixelBin.Three => 0.75f,
        _ => 0f,
    };
}

/// <summary>Key for a rasterized glyph. Deliberately carries no variation coordinates.</summary>
/// <remarks>
/// This mirrors cosmic-text's <c>CacheKey</c>, which upstream has no axis tuple. Variable
/// instances therefore need the separate cache in <see cref="VariableGlyphCache"/>, exactly as
/// the Rust engine does, so two different axis tuples can never share an outline.
/// </remarks>
public readonly record struct GlyphCacheKey(
    FontId FontId,
    ushort GlyphId,
    uint FontSizeBits,
    SubpixelBin XBin,
    SubpixelBin YBin,
    bool FakeItalic)
{
    public float FontSize => BitConverter.UInt32BitsToSingle(FontSizeBits);

    public static (GlyphCacheKey Key, int X, int Y) New(
        FontId fontId,
        ushort glyphId,
        float fontSize,
        (float X, float Y) position,
        bool fakeItalic)
    {
        (int x, SubpixelBin xBin) = SubpixelBinExtensions.New(position.X);
        (int y, SubpixelBin yBin) = SubpixelBinExtensions.New(position.Y);
        return (
            new GlyphCacheKey(fontId, glyphId, BitConverter.SingleToUInt32Bits(fontSize), xBin, yBin, fakeItalic),
            x,
            y);
    }
}

/// <summary>A glyph pinned to a device-pixel position.</summary>
public readonly record struct PhysicalGlyph(GlyphCacheKey CacheKey, int X, int Y);

/// <summary>A laid out glyph.</summary>
public struct LayoutGlyph
{
    public int Start;
    public int End;
    public float FontSize;
    public float? LineHeight;
    public FontId FontId;
    public ushort GlyphId;
    public bool FontIsVariable;
    public float? FontWeightAxis;
    public float? FontOpticalSize;
    public bool FontItalicAxis;
    public float X;
    public float Y;
    public float W;
    public byte Level;
    public float XOffset;
    public float YOffset;
    public RgbaColor? Color;
    public ulong Metadata;
    public bool FakeItalic;

    public readonly PhysicalGlyph Physical((float X, float Y) offset, float scale)
    {
        float xOffset = FontSize * XOffset;
        float yOffset = FontSize * YOffset;
        (GlyphCacheKey key, int x, int y) = GlyphCacheKey.New(
            FontId,
            GlyphId,
            FontSize * scale,
            (
                ((X + xOffset) * scale) + offset.X,
                MathF.Truncate(((Y - yOffset) * scale) + offset.Y)),
            FakeItalic);
        return new PhysicalGlyph(key, x, y);
    }
}

/// <summary>A line of laid out glyphs.</summary>
public sealed class LayoutLine
{
    public float W;
    public float MaxAscent;
    public float MaxDescent;
    public float? LineHeight;
    public List<LayoutGlyph> Glyphs = [];
}

/// <summary>One visual line, ready to paint.</summary>
public sealed class LayoutRun
{
    public required int LineIndex { get; init; }

    public required string Text { get; init; }

    public required bool Rtl { get; init; }

    public required List<LayoutGlyph> Glyphs { get; init; }

    public required float LineY { get; init; }

    public required float LineTop { get; init; }

    public required float LineHeight { get; init; }

    public required float LineW { get; init; }

    /// <summary>
    /// The x extent of the character range <paramref name="start"/>..<paramref name="end"/>,
    /// the way cosmic-text's <c>LayoutRun::highlight</c> computes it.
    /// </summary>
    public (float X, float Width)? Highlight(int start, int end)
    {
        float? xStart = null;
        float? xEnd = null;
        float rtlFactor = Rtl ? 1f : 0f;
        float ltrFactor = 1f - rtlFactor;
        foreach (LayoutGlyph glyph in Glyphs)
        {
            int left = Rtl ? glyph.End : glyph.Start;
            if (left >= start && left <= end)
            {
                xStart ??= glyph.X + (glyph.W * rtlFactor);
                xEnd = glyph.X + (glyph.W * rtlFactor);
            }

            int right = Rtl ? glyph.Start : glyph.End;
            if (right >= start && right <= end)
            {
                xStart ??= glyph.X + (glyph.W * ltrFactor);
                xEnd = glyph.X + (glyph.W * ltrFactor);
            }
        }

        if (xStart is not { } from)
        {
            return null;
        }

        float to = xEnd!.Value;
        return from < to ? (from, to - from) : (to, from - to);
    }
}

/// <summary>One paragraph of a buffer: its text, attributes, and cached shaping.</summary>
public sealed class BufferLine
{
    private ShapeLine? _shape;
    private List<LayoutLine>? _layout;

    public BufferLine(string text, AttrsList attrsList)
    {
        Text = text;
        AttrsList = attrsList;
    }

    public string Text { get; private set; }

    public AttrsList AttrsList { get; private set; }

    public Align? Align { get; set; }

    public void SetAlign(Align? align)
    {
        if (Align != align)
        {
            Align = align;
            ResetLayout();
        }
    }

    public void ResetShaping()
    {
        _shape = null;
        _layout = null;
    }

    public void ResetLayout() => _layout = null;

    public ShapeLine? ShapeOpt => _shape;

    public List<LayoutLine>? LayoutOpt => _layout;

    public ShapeLine Shape(TextShaper shaper, int tabWidth)
    {
        if (_shape is null)
        {
            _shape = shaper.ShapeParagraph(Text, AttrsList, tabWidth);
            _layout = null;
        }

        return _shape;
    }

    public List<LayoutLine> Layout(
        TextShaper shaper,
        float fontSize,
        float? width,
        Wrap wrap,
        float? matchMonoWidth,
        int tabWidth)
    {
        if (_layout is null)
        {
            ShapeLine shape = Shape(shaper, tabWidth);
            _layout = TextLayout.LayoutToBuffer(shape, fontSize, width, wrap, Align, matchMonoWidth);
        }

        return _layout;
    }

    /// <summary>Split this line at <paramref name="index"/>, returning the tail line.</summary>
    public BufferLine SplitOff(int index)
    {
        string tailText = Text[index..];
        Text = Text[..index];
        AttrsList tailAttrs = AttrsList.SplitOff(index);
        ResetShaping();
        return new BufferLine(tailText, tailAttrs) { Align = Align };
    }

    public BufferLine Clone()
    {
        var copy = new BufferLine(Text, AttrsList.Clone()) { Align = Align };
        return copy;
    }
}

/// <summary>
/// A buffer of text that is shaped and laid out.
/// </summary>
/// <remarks>
/// Port of cosmic-text's <c>Buffer</c> restricted to what the inline layer uses: no scrolling,
/// no editing, no cursor motion. Offsets are UTF-16 code units throughout (cosmic-text uses
/// UTF-8 byte offsets); the convention is internal and consistent end to end.
/// </remarks>
public sealed class TextBuffer
{
    public TextBuffer(TextMetrics metrics) => Metrics = metrics;

    public List<BufferLine> Lines { get; private set; } = [];

    public TextMetrics Metrics { get; private set; }

    public float? WidthOpt { get; private set; }

    public float? HeightOpt { get; private set; }

    public Wrap Wrap { get; private set; } = Wrap.Word;

    public float? MonospaceWidth { get; set; }

    public int TabWidth { get; set; } = 8;

    public (float? Width, float? Height) Size => (WidthOpt, HeightOpt);

    public void SetWrap(Wrap wrap)
    {
        if (Wrap != wrap)
        {
            Wrap = wrap;
            Relayout();
        }
    }

    public void SetSize(float? width, float? height)
    {
        float? clampedWidth = width is { } w ? F32.Max(w, 0f) : null;
        float? clampedHeight = height is { } h ? F32.Max(h, 0f) : null;
        if (clampedWidth != WidthOpt || clampedHeight != HeightOpt)
        {
            WidthOpt = clampedWidth;
            HeightOpt = clampedHeight;
            Relayout();
        }
    }

    public void SetMetrics(TextMetrics metrics)
    {
        if (metrics != Metrics)
        {
            Metrics = metrics;
            Relayout();
        }
    }

    private void Relayout()
    {
        foreach (BufferLine line in Lines)
        {
            line.ResetLayout();
        }
    }

    /// <summary>Shape and lay out every line. There is no scrolling in this engine.</summary>
    public void ShapeUntilScroll(TextShaper shaper)
    {
        foreach (BufferLine line in Lines)
        {
            line.Layout(shaper, Metrics.FontSize, WidthOpt, Wrap, MonospaceWidth, TabWidth);
        }
    }

    /// <summary>Replace the buffer contents with a run of styled spans.</summary>
    public void SetRichText(IReadOnlyList<(string Text, TextAttrs Attrs)> spans, TextAttrs defaults)
    {
        Lines = [];
        var whole = new System.Text.StringBuilder();
        var ranges = new List<(int Start, int End, TextAttrs Attrs)>(spans.Count);
        foreach ((string text, TextAttrs attrs) in spans)
        {
            int start = whole.Length;
            whole.Append(text);
            ranges.Add((start, whole.Length, attrs));
        }

        string source = whole.ToString();
        int lineStart = 0;
        int position = 0;
        while (true)
        {
            int newline = source.IndexOf('\n', position);
            int lineEnd = newline < 0 ? source.Length : newline;
            int textEnd = lineEnd;
            if (textEnd > lineStart && source[textEnd - 1] == '\r')
            {
                textEnd--;
            }

            var attrsList = new AttrsList(defaults);
            foreach ((int start, int end, TextAttrs attrs) in ranges)
            {
                int from = Math.Max(start, lineStart);
                int to = Math.Min(end, textEnd);
                if (from < to && !attrs.Equals(defaults))
                {
                    attrsList.AddSpan(from - lineStart, to - lineStart, attrs);
                }
            }

            Lines.Add(new BufferLine(source[lineStart..textEnd], attrsList));
            if (newline < 0)
            {
                break;
            }

            position = newline + 1;
            lineStart = position;
        }
    }

    /// <summary>Iterate the visual lines, in paint order.</summary>
    public IEnumerable<LayoutRun> LayoutRuns()
    {
        float lineTop = 0f;
        for (int lineIndex = 0; lineIndex < Lines.Count; lineIndex++)
        {
            BufferLine line = Lines[lineIndex];
            List<LayoutLine>? layout = line.LayoutOpt;
            if (layout is null)
            {
                yield break;
            }

            foreach (LayoutLine layoutLine in layout)
            {
                float lineHeight = layoutLine.LineHeight ?? Metrics.LineHeight;
                float glyphHeight = layoutLine.MaxAscent + layoutLine.MaxDescent;
                float centeringOffset = (lineHeight - glyphHeight) / 2f;
                float lineY = lineTop + centeringOffset + layoutLine.MaxAscent;
                if (HeightOpt is { } height && lineY > height)
                {
                    yield break;
                }

                float currentTop = lineTop;
                lineTop += lineHeight;
                yield return new LayoutRun
                {
                    LineIndex = lineIndex,
                    Text = line.Text,
                    Rtl = line.ShapeOpt?.Rtl ?? false,
                    Glyphs = layoutLine.Glyphs,
                    LineY = lineY,
                    LineTop = currentTop,
                    LineHeight = lineHeight,
                    LineW = layoutLine.W,
                };
            }
        }
    }

    public TextBuffer Clone()
    {
        var copy = new TextBuffer(Metrics)
        {
            WidthOpt = WidthOpt,
            HeightOpt = HeightOpt,
            Wrap = Wrap,
            MonospaceWidth = MonospaceWidth,
            TabWidth = TabWidth,
        };
        foreach (BufferLine line in Lines)
        {
            copy.Lines.Add(line.Clone());
        }

        return copy;
    }
}
