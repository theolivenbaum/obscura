using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>A browser-grade line-breaking policy for one text span.</summary>
public readonly record struct CssLineBreak(bool Wrap, WordBreak WordBreak, OverflowWrap OverflowWrap)
{
    public static readonly CssLineBreak Default = new(true, WordBreak.Normal, OverflowWrap.Normal);
}

/// <summary>One OpenType feature setting applied while shaping.</summary>
public readonly record struct ShapingFeature(uint Tag, uint Value);

/// <summary>
/// Everything that decides how one run of text shapes and rasterizes.
/// </summary>
/// <remarks>
/// Port of the vendored cosmic-text <c>Attrs</c>, including its local variable-font additions:
/// <see cref="FontWeightAxis"/>, <see cref="FontOpticalSize"/>, <see cref="FontItalicAxis"/> and
/// <see cref="Variations"/> travel with the span so shaping and rasterization derive the same
/// canonical axis tuple. <see cref="FontId"/> pins the exact <c>@font-face</c> resource a CSS
/// matcher already chose, so sibling resources with identical internal metadata cannot replace
/// it during family rematching.
/// </remarks>
public sealed record TextAttrs
{
    public required string Family { get; init; }

    public FontId? FontId { get; init; }

    public float? FontWeightAxis { get; init; }

    public float? FontOpticalSize { get; init; }

    public bool FontItalicAxis { get; init; }

    public FaceStyle Style { get; init; } = FaceStyle.Normal;

    public ushort Weight { get; init; } = 400;

    public RgbaColor? Color { get; init; }

    public ulong Metadata { get; init; }

    /// <summary>Skew by 14 degrees to synthesize italic (cosmic-text's FAKE_ITALIC flag).</summary>
    public bool FakeItalic { get; init; }

    /// <summary>Per-span metrics override; inline descendants keep their own computed size.</summary>
    public (float FontSize, float LineHeight)? Metrics { get; init; }

    /// <summary>Letter spacing (tracking) in em.</summary>
    public float? LetterSpacingEm { get; init; }

    public ShapingFeature[] Features { get; init; } = [];

    public FontVariations? Variations { get; init; }

    public CssLineBreak? CssLineBreakPolicy { get; init; }

    /// <summary>
    /// Whether two spans can shape as one run. Anything that changes glyph selection or
    /// advances must break the run; color and metadata deliberately do not.
    /// </summary>
    public bool Compatible(TextAttrs other) =>
        string.Equals(Family, other.Family, StringComparison.Ordinal)
        && FontId == other.FontId
        && NullableEquals(FontWeightAxis, other.FontWeightAxis)
        && NullableEquals(FontOpticalSize, other.FontOpticalSize)
        && FontItalicAxis == other.FontItalicAxis
        && Style == other.Style
        && Weight == other.Weight
        && VariationsEqual(Variations, other.Variations);

    private static bool NullableEquals(float? left, float? right) =>
        (left, right) switch
        {
            (null, null) => true,
            ({ } a, { } b) => new VariationValue(a) == new VariationValue(b),
            _ => false,
        };

    private static bool VariationsEqual(FontVariations? left, FontVariations? right)
    {
        bool leftEmpty = left is null || left.IsEmpty;
        bool rightEmpty = right is null || right.IsEmpty;
        if (leftEmpty || rightEmpty)
        {
            return leftEmpty == rightEmpty;
        }

        return left!.Equals(right!);
    }
}

/// <summary>Attribute spans over one buffer line, keyed by character offset.</summary>
public sealed class AttrsList
{
    private readonly List<(int Start, int End, TextAttrs Attrs)> _spans = [];

    public AttrsList(TextAttrs defaults) => Defaults = defaults;

    public TextAttrs Defaults { get; private set; }

    public IReadOnlyList<(int Start, int End, TextAttrs Attrs)> Spans => _spans;

    public void AddSpan(int start, int end, TextAttrs attrs)
    {
        if (end <= start)
        {
            return;
        }

        _spans.Add((start, end, attrs));
    }

    public TextAttrs GetSpan(int index)
    {
        for (int i = _spans.Count - 1; i >= 0; i--)
        {
            (int start, int end, TextAttrs attrs) = _spans[i];
            if (index >= start && index < end)
            {
                return attrs;
            }
        }

        return Defaults;
    }

    public AttrsList Clone()
    {
        var copy = new AttrsList(Defaults);
        copy._spans.AddRange(_spans);
        return copy;
    }

    /// <summary>Split this list at <paramref name="index"/>, returning the tail.</summary>
    public AttrsList SplitOff(int index)
    {
        var tail = new AttrsList(Defaults);
        for (int i = _spans.Count - 1; i >= 0; i--)
        {
            (int start, int end, TextAttrs attrs) = _spans[i];
            if (end <= index)
            {
                continue;
            }

            if (start >= index)
            {
                tail._spans.Add((start - index, end - index, attrs));
                _spans.RemoveAt(i);
            }
            else
            {
                tail._spans.Add((0, end - index, attrs));
                _spans[i] = (start, index, attrs);
            }
        }

        tail._spans.Reverse();
        return tail;
    }

    internal void Reset(TextAttrs defaults)
    {
        Defaults = defaults;
        _spans.Clear();
    }
}
