namespace PocketCalculator.Render;

// PORT NOTE. crates/obscura-render builds its TextEngine per pass too, and at the Rust engine's
// shaping speed re-shaping the document each time is affordable. HarfBuzz through P/Invoke is
// not: shaping is roughly 40% of a prepare on a text-heavy page (31ms of 77ms on a 2059-node
// page, 24ms over 509 paragraphs on a 133ms one). A layout-affecting restyle cannot reuse its
// layout, but it can reuse the shaping, which is a pure function of the text, its attributes and
// the tab width. This is a deliberate C# deviation recorded under "Known deviations" in todo.md.

/// <summary>
/// One shaped paragraph's identity: everything <see cref="TextShaper.ShapeParagraph"/> reads.
/// </summary>
internal readonly struct ShapeCacheKey : IEquatable<ShapeCacheKey>
{
    private readonly string _text;
    private readonly int _tabWidth;
    private readonly TextAttrs _defaults;
    private readonly (int Start, int End, TextAttrs Attrs)[] _spans;
    private readonly int _hash;

    internal ShapeCacheKey(string text, AttrsList attrs, int tabWidth)
    {
        _text = text;
        _tabWidth = tabWidth;
        _defaults = attrs.Defaults;
        _spans = [.. attrs.Spans];

        var hash = new HashCode();
        hash.Add(text, StringComparer.Ordinal);
        hash.Add(tabWidth);
        hash.Add(_defaults);
        foreach ((int start, int end, TextAttrs one) in _spans)
        {
            hash.Add(start);
            hash.Add(end);
            hash.Add(one);
        }

        _hash = hash.ToHashCode();
    }

    public bool Equals(ShapeCacheKey other)
    {
        if (_hash != other._hash
            || _tabWidth != other._tabWidth
            || _spans.Length != other._spans.Length
            || !string.Equals(_text, other._text, StringComparison.Ordinal)
            || !_defaults.Equals(other._defaults))
        {
            return false;
        }

        for (int index = 0; index < _spans.Length; index++)
        {
            (int start, int end, TextAttrs attrs) = _spans[index];
            (int otherStart, int otherEnd, TextAttrs otherAttrs) = other._spans[index];
            if (start != otherStart || end != otherEnd || !attrs.Equals(otherAttrs))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ShapeCacheKey other && Equals(other);

    public override int GetHashCode() => _hash;
}

/// <summary>
/// Shaped paragraphs, reusable across render passes that share a font set.
/// </summary>
/// <remarks>
/// <para>
/// Correctness rests on two things. A <see cref="ShapeLine"/> is never mutated after
/// <see cref="TextShaper.ShapeParagraph"/> returns it - <c>TextLayout</c> and <c>Bidi</c> only
/// read it, and nothing outside <c>TextShaping.cs</c> assigns to a <c>ShapeWord</c>,
/// <c>ShapeSpan</c> or <c>ShapeGlyph</c> - so one instance can be handed to several passes. And
/// the key covers every input the shaper reads, including <see cref="TextAttrs.FontId"/>, which
/// pins the exact <c>@font-face</c> resource; a face that arrives later changes the attributes
/// and therefore the key.
/// </para>
/// <para>
/// That last point is belt and braces only: a cache is carried from one pass to the next solely
/// when the two passes were built from the same web-font set, so a newly loaded face discards it
/// wholesale (<see cref="MatchesFontSet"/>).
/// </para>
/// </remarks>
internal sealed class ShapeCache
{
    /// <summary>
    /// Paragraph count above which the cache is dropped and rebuilt. A document's paragraphs are
    /// stable, so this is a ceiling on pathological churn rather than a working-set limit; a
    /// clear costs one re-shape of the next pass, which is what would have happened anyway.
    /// </summary>
    internal const int MaxEntries = 8192;

    /// <summary>Kill switch, so an A/B can be run against one binary.</summary>
    internal static readonly bool Disabled =
        Environment.GetEnvironmentVariable("POCKETCALCULATOR_DISABLE_SHAPE_CACHE") == "1";

    private readonly Dictionary<ShapeCacheKey, ShapeLine> _entries = [];
    private readonly WebFont[] _fonts;

    internal ShapeCache(IReadOnlyList<WebFont> fonts) => _fonts = [.. fonts];

    internal int Count => _entries.Count;

    internal int Hits { get; private set; }

    internal int Misses { get; private set; }

    /// <summary>
    /// Whether a cache filled against one web-font set may be used by a pass built from another.
    /// Reference equality per face is deliberate: a <see cref="WebFont"/> is produced once per
    /// decoded resource, so two passes over an unchanged document hand over the same instances,
    /// and anything else is treated as a different font set.
    /// </summary>
    internal bool MatchesFontSet(IReadOnlyList<WebFont> fonts)
    {
        if (_fonts.Length != fonts.Count)
        {
            return false;
        }

        for (int index = 0; index < _fonts.Length; index++)
        {
            if (!ReferenceEquals(_fonts[index], fonts[index]))
            {
                return false;
            }
        }

        return true;
    }

    internal bool TryGet(in ShapeCacheKey key, out ShapeLine shape)
    {
        if (_entries.TryGetValue(key, out ShapeLine? found))
        {
            Hits++;
            shape = found;
            return true;
        }

        Misses++;
        shape = null!;
        return false;
    }

    internal void Add(in ShapeCacheKey key, ShapeLine shape)
    {
        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        _entries[key] = shape;
    }
}
