// Port of vendor/taffy/src/tree/cache.rs
namespace PocketCalculator.Render.Layout;

/// <summary>Clear operation outcome. See <see cref="Cache.Clear"/>.</summary>
public enum ClearState : byte
{
    /// <summary>Cleared some values.</summary>
    Cleared,

    /// <summary>Everything was already cleared.</summary>
    AlreadyEmpty,
}

/// <summary>A cache for caching the results of sizing a Grid Item or Flexbox Item.</summary>
public sealed class Cache
{
    /// <summary>The number of cache entries for each node in the tree.</summary>
    private const int CacheSize = 9;

    /// <summary><c>f32::INFINITY</c> as a u32.</summary>
    private const uint InfinityBits = 0b0_11111111_00000000000000000000000u;

    /// <summary><c>f32::NEG_INFINITY</c> as a u32.</summary>
    private const uint NegInfinityBits = 0b1_11111111_00000000000000000000000u;

    /// <summary>The sign bit of the first f32.</summary>
    private const ulong SignBit1 = 1UL << 63;

    /// <summary>The sign bit of the second f32.</summary>
    private const ulong SignBit2 = 1UL << 31;

    /// <summary>Mask of both sign bits.</summary>
    private const ulong BothSignBitsMask = SignBit1 | SignBit2;

    /// <summary>Mask excluding the sign bits.</summary>
    private const ulong NonSignBitsMask = ~BothSignBitsMask;

    /// <summary>Mask which includes only the bits which encode the x-axis value.</summary>
    private const ulong XAxisValueMask = (ulong)uint.MaxValue << 32;

    private CacheEntry<LayoutOutput>? _finalLayoutEntry;

    /// <summary>
    /// The nine measurement slots, allocated on the first measurement: many nodes (text leaves
    /// under a block, most of a large document) are only ever laid out, never measured, and
    /// the slots are the bulk of a node's cache.
    /// </summary>
    private CacheEntry<MeasureOutput>?[]? _measureEntries;

    /// <summary>
    /// Measurements evicted from <see cref="_measureEntries"/>, kept so an alternating pair of
    /// inputs that share a slot does not thrash it. Allocated on the first eviction only.
    /// </summary>
    /// <remarks>
    /// Deviation from taffy, which keeps one entry per slot and drops the old one. A
    /// shrink-to-fit box (a float, which the port lays out as a flex row) is measured at both
    /// its min-content and its max-content width, and each of those measures its child at
    /// definite widths that land in the same slot (known width, unknown height). The two
    /// evict each other, so every level re-measures its whole subtree once per request from
    /// its parent and 300 nested floats took minutes. Keeping the evicted entries makes the
    /// number of distinct measurements per node the bound instead. The cache is keyed on the
    /// full input either way, so what a hit returns is unchanged: only how often it hits.
    /// </remarks>
    private CacheEntry<MeasureOutput>[]? _overflow;
    private int _overflowCount;
    private int _overflowNext;
    private bool _isEmpty = true;

    /// <summary>The most evicted measurements a node keeps (a ring: the oldest is replaced).</summary>
    private const int OverflowSize = 16;

    /// <summary>The size of the overflow ring when a node first evicts a measurement.</summary>
    private const int InitialOverflowSize = 2;

    /// <summary>Space-optimised cache key that packs bits into as small a size as possible.</summary>
    private readonly record struct CacheKey(ulong KdAvailableSpace, ulong ParentSize)
    {
        /// <summary>The parent size with the requested-axis bits and the y-axis value masked out.</summary>
        public ulong XAxisParentSize() => ParentSize & (XAxisValueMask & NonSignBitsMask);

        /// <summary>The requested-axis bits that were packed into the parent-size key.</summary>
        public ulong AxisBits() => ParentSize & BothSignBitsMask;
    }

    /// <summary>Cached intermediate layout results.</summary>
    private readonly record struct CacheEntry<T>(CacheKey Key, T Content);

    /// <summary>The block-layout metadata required by a preliminary size contribution.</summary>
    private readonly record struct MeasureOutput(
        Size<float> Size,
        CollapsibleMarginSet TopMargin,
        CollapsibleMarginSet BottomMargin,
        bool MarginsCanCollapseThrough,
        byte VerticalMarginsAreCollapsible)
    {
        public static MeasureOutput New(in LayoutInput input, in LayoutOutput output) => new(
            output.Size,
            output.TopMargin,
            output.BottomMargin,
            output.MarginsCanCollapseThrough,
            VerticalMarginContextKey(input));

        public LayoutOutput IntoLayoutOutput()
        {
            var output = LayoutOutput.FromOuterSize(Size);
            output.TopMargin = TopMargin;
            output.BottomMargin = BottomMargin;
            output.MarginsCanCollapseThrough = MarginsCanCollapseThrough;
            return output;
        }
    }

    private static uint OptionCacheKey(float? input) =>
        input.HasValue ? BitConverter.SingleToUInt32Bits(input.Value) : InfinityBits;

    private static ulong SizeOptionCacheKey(Size<float?> input) =>
        ((ulong)OptionCacheKey(input.Width) << 32) | OptionCacheKey(input.Height);

    private static uint AvailableSpaceCacheKey(AvailableSpace input) => input.Kind switch
    {
        AvailableSpaceKind.Definite => BitConverter.SingleToUInt32Bits(-input.Unwrap()),
        AvailableSpaceKind.MinContent => NegInfinityBits,
        _ => InfinityBits,
    };

    private static uint MixedCacheKey(float? kd, AvailableSpace avs) =>
        kd.HasValue ? BitConverter.SingleToUInt32Bits(kd.Value) : AvailableSpaceCacheKey(avs);

    private static ulong SizeMixedCacheKey(Size<float?> kd, Size<AvailableSpace> avs) =>
        ((ulong)MixedCacheKey(kd.Width, avs.Width) << 32) | MixedCacheKey(kd.Height, avs.Height);

    private static byte VerticalMarginContextKey(in LayoutInput input) =>
        (byte)((input.VerticalMarginsAreCollapsible.Start ? 1 : 0)
            | ((input.VerticalMarginsAreCollapsible.End ? 1 : 0) << 1));

    private static CacheKey KeyFrom(in LayoutInput input)
    {
        ulong extraBits = input.Axis switch
        {
            RequestedAxis.Horizontal => SignBit1,
            RequestedAxis.Vertical => SignBit2,
            _ => SignBit1 | SignBit2,
        };

        return new CacheKey(
            SizeMixedCacheKey(input.KnownDimensions, input.AvailableSpace),
            (SizeOptionCacheKey(input.ParentSize) & NonSignBitsMask) | extraBits);
    }

    /// <summary>
    /// Return the cache slot to cache the current computed result in.
    /// </summary>
    /// <remarks>
    /// <para>Slot 0: both known dimensions were set.</para>
    /// <para>Slots 1-4: one of the two known dimensions was set.</para>
    /// <para>Slots 5-8: neither known dimension was set.</para>
    /// </remarks>
    private static int ComputeCacheSlot(Size<float?> knownDimensions, Size<AvailableSpace> availableSpace)
    {
        bool hasKnownWidth = knownDimensions.Width.HasValue;
        bool hasKnownHeight = knownDimensions.Height.HasValue;

        if (hasKnownWidth && hasKnownHeight)
        {
            return 0;
        }

        if (hasKnownWidth && !hasKnownHeight)
        {
            return 1 + (availableSpace.Height.Kind == AvailableSpaceKind.MinContent ? 1 : 0);
        }

        if (hasKnownHeight && !hasKnownWidth)
        {
            return 3 + (availableSpace.Width.Kind == AvailableSpaceKind.MinContent ? 1 : 0);
        }

        bool widthIsMinContent = availableSpace.Width.Kind == AvailableSpaceKind.MinContent;
        bool heightIsMinContent = availableSpace.Height.Kind == AvailableSpaceKind.MinContent;
        return (widthIsMinContent, heightIsMinContent) switch
        {
            (false, false) => 5,
            (false, true) => 6,
            (true, false) => 7,
            (true, true) => 8,
        };
    }

    /// <summary>Whether a stored measurement answers a request with this key.</summary>
    private static bool Matches(in CacheEntry<MeasureOutput> measure, in CacheKey key, byte marginKey)
    {
        // Deviation from taffy: taffy masks the requested-axis bits out of the key on
        // both sides here, so any stored measurement answers a request for either axis.
        // That is unsound for an entry produced by a horizontal-only run, because
        // BlockLayout / FlexboxLayout / GridLayout all short-circuit such a run to
        // `(known width, 0)` without laying the box out - its height is a placeholder,
        // not a measurement, and its collapsible margins are unset. taffy then reuses
        // that zero as the box's block-axis contribution: a grid whose row track is
        // sized from a lone item that was first measured horizontally (which happens as
        // soon as any column track is intrinsically sized, e.g. an unoccupied `1fr`)
        // collapses the row to 0. Chromium sizes the row to the item's content, so keep
        // a horizontal-only entry for horizontal-only requests. Vertical and both-axis
        // runs never short-circuit, so their entries stay shareable.
        if (measure.Key.AxisBits() == SignBit1 && key.AxisBits() != SignBit1)
        {
            return false;
        }

        return measure.Key.KdAvailableSpace == key.KdAvailableSpace
            && measure.Key.XAxisParentSize() == key.XAxisParentSize()
            && measure.Content.VerticalMarginsAreCollapsible == marginKey;
    }

    /// <summary>Try to retrieve a cached result from the cache.</summary>
    public LayoutOutput? Get(in LayoutInput input)
    {
        var key = KeyFrom(input);
        switch (input.RunMode)
        {
            case RunMode.PerformLayout:
                if (_finalLayoutEntry is { } entry && entry.Key == key)
                {
                    return entry.Content;
                }

                return null;

            case RunMode.ComputeSize:
                if (_measureEntries is not { } entries)
                {
                    // The overflow ring only ever holds what a slot evicted.
                    return null;
                }

                byte marginKey = VerticalMarginContextKey(input);
                for (int i = 0; i < entries.Length; i++)
                {
                    if (entries[i] is not { } measure)
                    {
                        continue;
                    }

                    if (Matches(measure, key, marginKey))
                    {
                        return measure.Content.IntoLayoutOutput();
                    }
                }

                if (_overflow is { } overflow)
                {
                    for (int i = 0; i < _overflowCount; i++)
                    {
                        if (Matches(overflow[i], key, marginKey))
                        {
                            return overflow[i].Content.IntoLayoutOutput();
                        }
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Store a computed size in the cache.</summary>
    public void Store(in LayoutInput input, in LayoutOutput layoutOutput)
    {
        var key = KeyFrom(input);
        switch (input.RunMode)
        {
            case RunMode.PerformLayout:
                _isEmpty = false;
                _finalLayoutEntry = new CacheEntry<LayoutOutput>(key, layoutOutput);
                break;

            case RunMode.ComputeSize:
                _isEmpty = false;
                int cacheSlot = ComputeCacheSlot(input.KnownDimensions, input.AvailableSpace);
                _measureEntries ??= new CacheEntry<MeasureOutput>?[CacheSize];
                if (_measureEntries[cacheSlot] is { } evicted)
                {
                    KeepEvicted(evicted);
                }

                _measureEntries[cacheSlot] =
                    new CacheEntry<MeasureOutput>(key, MeasureOutput.New(input, layoutOutput));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Keep a measurement a slot is about to drop. The ring starts small and doubles up to
    /// <see cref="OverflowSize"/>, since most nodes only ever evict one or two, and once full
    /// replaces its oldest entry.
    /// </summary>
    private void KeepEvicted(in CacheEntry<MeasureOutput> evicted)
    {
        if (_overflow is null)
        {
            _overflow = new CacheEntry<MeasureOutput>[InitialOverflowSize];
        }
        else if (_overflowCount == _overflow.Length && _overflow.Length < OverflowSize)
        {
            Array.Resize(ref _overflow, _overflow.Length * 2);
            _overflowNext = _overflowCount;
        }

        _overflow[_overflowNext] = evicted;
        _overflowNext = (_overflowNext + 1) % _overflow.Length;
        if (_overflowCount < _overflow.Length)
        {
            _overflowCount++;
        }
    }

    /// <summary>Clear all cache entries and report the clear operation outcome.</summary>
    public ClearState Clear()
    {
        if (_isEmpty)
        {
            return ClearState.AlreadyEmpty;
        }

        _isEmpty = true;
        _finalLayoutEntry = null;
        if (_measureEntries is { } slots)
        {
            Array.Clear(slots);
        }

        _overflowCount = 0;
        _overflowNext = 0;
        return ClearState.Cleared;
    }

    /// <summary>Returns true if all cache entries are empty.</summary>
    public bool IsEmpty()
    {
        if (_finalLayoutEntry is not null)
        {
            return false;
        }

        if (_measureEntries is { } entries)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i] is not null)
                {
                    return false;
                }
            }
        }

        return _overflowCount == 0;
    }
}
