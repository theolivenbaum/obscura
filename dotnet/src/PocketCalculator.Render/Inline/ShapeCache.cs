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
    /// </summary>
    /// <remarks>
    /// The faces are compared by their decoded bytes' identity and their descriptors, in order.
    /// This used to be reference equality on the <see cref="WebFont"/> itself, on the belief that
    /// one is produced once per decoded resource; but <c>PaintFonts.CollectWebFonts</c> builds new
    /// ones on every pass, so on any page with an <c>@font-face</c> no cache was ever adopted and
    /// every forced relayout reshaped the whole document (nvidia.com: 16 faces, ~1250 paragraphs
    /// reshaped per pass). The bytes are the memoized decode from
    /// <see cref="RenderResourceCache"/>, so an unchanged resource hands over the same array; a
    /// face that arrives, changes or is evicted is a different array and still discards the cache.
    /// The order matters as well as the set: it decides each face's <see cref="FontId"/>.
    /// </remarks>
    internal bool MatchesFontSet(IReadOnlyList<WebFont> fonts)
    {
        if (_fonts.Length != fonts.Count)
        {
            return false;
        }

        for (int index = 0; index < _fonts.Length; index++)
        {
            WebFont mine = _fonts[index];
            WebFont theirs = fonts[index];
            if (!ReferenceEquals(mine, theirs)
                && !(ReferenceEquals(mine.Data, theirs.Data)
                    && string.Equals(mine.Family, theirs.Family, StringComparison.Ordinal)
                    && mine.Weight == theirs.Weight
                    && mine.Italic == theirs.Italic))
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
            _memoGlyphs = 0;
            _memoGeneration++;
        }

        _entries[key] = shape;
    }

    /// <summary>
    /// Glyphs, summed over every memoized line layout, above which no further layout is kept.
    /// A laid-out glyph is about a hundred bytes, so this bounds the memo near 25 MB however
    /// much text a document carries; past it, lines are laid out per call as before.
    /// </summary>
    internal const long MaxMemoGlyphs = 1 << 18;

    /// <summary>Line layouts kept per shaped paragraph: min-content, max-content and used.</summary>
    internal const int MemoSlots = 4;

    private long _memoGlyphs;
    private int _memoGeneration;

    internal int LayoutHits { get; private set; }

    internal int LayoutMisses { get; private set; }

    /// <summary>
    /// <see cref="TextLayout.LayoutToBuffer"/> for a paragraph this cache handed out, reusing a
    /// result computed for the same arguments in this or an earlier pass.
    /// </summary>
    /// <remarks>
    /// PORT NOTE, alongside the shaping cache above. Line breaking is a pure function of the
    /// shaped paragraph and its five arguments, and the paragraph is never mutated after it is
    /// shaped, so a layout computed once stays exact for as long as the paragraph is reused.
    /// Nothing mutates a returned <see cref="LayoutLine"/> or its glyph list either: they are
    /// built in <c>LayoutToBuffer</c> and only read afterwards. A forced relayout re-measures
    /// every text leaf at the same widths it had before, and laying those lines out again was
    /// a third of everything such a pass allocated (the <see cref="LayoutGlyph"/> arrays alone
    /// were a fifth), so it is kept with the shaping. Floats compare by bit pattern: a width
    /// that differs in the last place may wrap differently and must not share a result.
    /// </remarks>
    internal List<LayoutLine> Layout(
        ShapeLine shape,
        float fontSize,
        float? width,
        Wrap wrap,
        Align? align,
        float? matchMonoWidth)
    {
        LayoutMemoKey key = new(
            BitConverter.SingleToInt32Bits(fontSize),
            width is { } w ? BitConverter.SingleToUInt32Bits(w) : long.MinValue,
            wrap,
            align,
            matchMonoWidth is { } m ? BitConverter.SingleToUInt32Bits(m) : long.MinValue);
        LayoutMemo? memo = shape.LayoutMemo is { } existing && existing.Generation == _memoGeneration
            ? existing
            : null;
        if (memo is not null)
        {
            for (int index = 0; index < memo.Count; index++)
            {
                if (memo.Keys[index] == key)
                {
                    LayoutHits++;
                    return memo.Layouts[index];
                }
            }
        }

        LayoutMisses++;
        List<LayoutLine> layout = TextLayout.LayoutToBuffer(shape, fontSize, width, wrap, align, matchMonoWidth);
        int glyphs = 0;
        foreach (LayoutLine line in layout)
        {
            glyphs += line.Glyphs.Count;
        }

        memo ??= shape.LayoutMemo = new LayoutMemo(_memoGeneration);
        int slot = memo.Count < MemoSlots ? memo.Count : memo.Next;
        long released = slot < memo.Count ? memo.Glyphs[slot] : 0;
        if (_memoGlyphs - released + glyphs > MaxMemoGlyphs)
        {
            return layout;
        }

        _memoGlyphs += glyphs - released;
        memo.Keys[slot] = key;
        memo.Layouts[slot] = layout;
        memo.Glyphs[slot] = glyphs;
        if (slot == memo.Count)
        {
            memo.Count++;
        }
        else
        {
            memo.Next = (memo.Next + 1) % MemoSlots;
        }

        return layout;
    }
}

/// <summary>The arguments of one <see cref="TextLayout.LayoutToBuffer"/> call, floats as bits.</summary>
internal readonly record struct LayoutMemoKey(int FontSize, long Width, Wrap Wrap, Align? Align, long Mono);

/// <summary>
/// The line layouts <see cref="ShapeCache.Layout"/> keeps for one shaped paragraph. A memo from
/// before the cache last cleared itself is ignored, which keeps the glyph budget exact.
/// </summary>
internal sealed class LayoutMemo(int generation)
{
    internal readonly int Generation = generation;
    internal readonly LayoutMemoKey[] Keys = new LayoutMemoKey[ShapeCache.MemoSlots];
    internal readonly List<LayoutLine>[] Layouts = new List<LayoutLine>[ShapeCache.MemoSlots];
    internal readonly int[] Glyphs = new int[ShapeCache.MemoSlots];
    internal int Count;
    internal int Next;
}
