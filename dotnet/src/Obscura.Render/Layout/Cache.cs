// Port of vendor/taffy/src/tree/cache.rs
namespace Obscura.Render.Layout;

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
    private readonly CacheEntry<MeasureOutput>?[] _measureEntries = new CacheEntry<MeasureOutput>?[CacheSize];
    private bool _isEmpty = true;

    /// <summary>Space-optimised cache key that packs bits into as small a size as possible.</summary>
    private readonly record struct CacheKey(ulong KdAvailableSpace, ulong ParentSize)
    {
        /// <summary>The parent size with the requested-axis bits and the y-axis value masked out.</summary>
        public ulong XAxisParentSize() => ParentSize & (XAxisValueMask & NonSignBitsMask);
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
                byte marginKey = VerticalMarginContextKey(input);
                for (int i = 0; i < _measureEntries.Length; i++)
                {
                    if (_measureEntries[i] is not { } measure)
                    {
                        continue;
                    }

                    if (measure.Key.KdAvailableSpace == key.KdAvailableSpace
                        && measure.Key.XAxisParentSize() == key.XAxisParentSize()
                        && measure.Content.VerticalMarginsAreCollapsible == marginKey)
                    {
                        return measure.Content.IntoLayoutOutput();
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
                _measureEntries[cacheSlot] =
                    new CacheEntry<MeasureOutput>(key, MeasureOutput.New(input, layoutOutput));
                break;

            default:
                break;
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
        Array.Clear(_measureEntries);
        return ClearState.Cleared;
    }

    /// <summary>Returns true if all cache entries are empty.</summary>
    public bool IsEmpty()
    {
        if (_finalLayoutEntry is not null)
        {
            return false;
        }

        for (int i = 0; i < _measureEntries.Length; i++)
        {
            if (_measureEntries[i] is not null)
            {
                return false;
            }
        }

        return true;
    }
}
