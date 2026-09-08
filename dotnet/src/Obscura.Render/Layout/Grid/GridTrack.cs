// Port of vendor/taffy/src/compute/grid/types/grid_track.rs
namespace Obscura.Render.Layout;

/// <summary>Whether a <see cref="GridTrack"/> represents an actual track or a gutter.</summary>
internal enum GridTrackKind : byte
{
    /// <summary>Track is an actual track.</summary>
    Track,

    /// <summary>Track is a gutter (aka grid line) (aka gap).</summary>
    Gutter,
}

/// <summary>
/// Internal sizing information for a single grid track (row/column). Gutters between tracks are
/// sized similarly to actual tracks, so they are also represented by this class.
/// </summary>
/// <remarks>
/// DEVIATION: taffy's <c>GridTrack</c> is a Copy-free struct held in a <c>Vec</c> and mutated
/// through <c>&amp;mut [GridTrack]</c> slices. The port makes it a class so the slice views
/// (<see cref="TrackSlice"/>) alias the same objects.
/// </remarks>
internal sealed class GridTrack
{
    private GridTrack(
        GridTrackKind kind,
        MinTrackSizingFunction minTrackSizingFunction,
        MaxTrackSizingFunction maxTrackSizingFunction)
    {
        Kind = kind;
        MinTrackSizingFunction = minTrackSizingFunction;
        MaxTrackSizingFunction = maxTrackSizingFunction;
    }

    /// <summary>Whether the track is a full track or a gutter.</summary>
    public GridTrackKind Kind { get; }

    /// <summary>
    /// Whether the track is a collapsed track/gutter. Collapsed tracks are effectively treated as if
    /// they don't exist for the purposes of grid sizing.
    /// </summary>
    public bool IsCollapsed { get; private set; }

    /// <summary>The minimum track sizing function of the track.</summary>
    public MinTrackSizingFunction MinTrackSizingFunction { get; private set; }

    /// <summary>The maximum track sizing function of the track.</summary>
    public MaxTrackSizingFunction MaxTrackSizingFunction { get; private set; }

    /// <summary>The distance of the start of the track from the start of the grid container.</summary>
    public float Offset;

    /// <summary>The size (width/height as applicable) of the track.</summary>
    public float BaseSize;

    /// <summary>A temporary scratch value when sizing tracks. Note: can be infinity.</summary>
    public float GrowthLimit;

    /// <summary>
    /// A temporary scratch value when sizing tracks. Used as an additional amount to add to the
    /// estimate for the available space in the opposite axis when content sizing items.
    /// </summary>
    public float ContentAlignmentAdjustment;

    /// <summary>A temporary scratch value when "distributing space".</summary>
    public float ItemIncurredIncrease;

    /// <summary>A temporary scratch value when "distributing space".</summary>
    public float BaseSizePlannedIncrease;

    /// <summary>A temporary scratch value when "distributing space".</summary>
    public float GrowthLimitPlannedIncrease;

    /// <summary>
    /// A temporary scratch value when "distributing space".
    /// <see href="https://www.w3.org/TR/css3-grid-layout/#infinitely-growable"/>
    /// </summary>
    public bool InfinitelyGrowable;

    /// <summary>Create a new GridTrack representing an actual track (not a gutter).</summary>
    public static GridTrack New(
        MinTrackSizingFunction minTrackSizingFunction,
        MaxTrackSizingFunction maxTrackSizingFunction) =>
        new(GridTrackKind.Track, minTrackSizingFunction, maxTrackSizingFunction);

    /// <summary>Create a new GridTrack representing a gutter.</summary>
    public static GridTrack Gutter(LengthPercentage size) =>
        new(GridTrackKind.Gutter, MinTrackSizingFunction.From(size), MaxTrackSizingFunction.From(size));

    /// <summary>
    /// Mark a GridTrack as collapsed. Also sets both of the track's sizing functions to fixed
    /// zero-sized sizing functions.
    /// </summary>
    public void Collapse()
    {
        IsCollapsed = true;
        MinTrackSizingFunction = MinTrackSizingFunction.Zero;
        MaxTrackSizingFunction = MaxTrackSizingFunction.Zero;
    }

    /// <summary>Returns true if the track is flexible (has an fr max track sizing function).</summary>
    public bool IsFlexible() => MaxTrackSizingFunction.IsFr();

    /// <summary>Returns true if either sizing function depends on the size of the parent node.</summary>
    public bool UsesPercentage() =>
        MinTrackSizingFunction.UsesPercentage() || MaxTrackSizingFunction.UsesPercentage();

    /// <summary>Returns true if the track has an intrinsic min and/or max sizing function.</summary>
    public bool HasIntrinsicSizingFunction() =>
        MinTrackSizingFunction.IsIntrinsic() || MaxTrackSizingFunction.IsIntrinsic();

    /// <summary>The fit-content() limit of the track, or infinity when it has none.</summary>
    public float FitContentLimit(float? axisAvailableGridSpace)
    {
        var raw = MaxTrackSizingFunction.IntoRaw();
        if (raw.Tag == CompactLength.FitContentPxTag)
        {
            return raw.Value;
        }

        if (raw.Tag == CompactLength.FitContentPercentTag)
        {
            return axisAvailableGridSpace.HasValue
                ? axisAvailableGridSpace.Value * raw.Value
                : float.PositiveInfinity;
        }

        return float.PositiveInfinity;
    }

    /// <summary>The growth limit clamped by the fit-content() limit.</summary>
    public float FitContentLimitedGrowthLimit(float? axisAvailableGridSpace) =>
        Sys.F32Min(GrowthLimit, FitContentLimit(axisAvailableGridSpace));

    /// <summary>Returns the track's flex factor if it is a flex track, else 0.</summary>
    public float FlexFactor() =>
        MaxTrackSizingFunction.IsFr() ? MaxTrackSizingFunction.IntoRaw().Value : 0.0f;
}

/// <summary>
/// A view over a contiguous run of a track list, standing in for Rust's <c>&amp;mut [GridTrack]</c>.
/// </summary>
internal readonly struct TrackSlice
{
    private readonly List<GridTrack> _tracks;
    private readonly int _start;

    /// <summary>View the whole list.</summary>
    public TrackSlice(List<GridTrack> tracks)
        : this(tracks, 0, tracks.Count)
    {
    }

    /// <summary>View a sub-range of the list.</summary>
    public TrackSlice(List<GridTrack> tracks, int start, int count)
    {
        _tracks = tracks;
        _start = start;
        Count = count;
    }

    /// <summary>The number of tracks in the view.</summary>
    public int Count { get; }

    /// <summary>Access a track by index within the view.</summary>
    public GridTrack this[int index] => _tracks[_start + index];

    /// <summary>Create a sub-view over the half-open range.</summary>
    public TrackSlice Range(int startInclusive, int endExclusive) =>
        new(_tracks, _start + startInclusive, endExclusive - startInclusive);

    /// <summary>Whether any track matches the predicate.</summary>
    public bool Any(Func<GridTrack, bool> predicate)
    {
        for (int i = 0; i < Count; i++)
        {
            if (predicate(this[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether every track matches the predicate.</summary>
    public bool All(Func<GridTrack, bool> predicate)
    {
        for (int i = 0; i < Count; i++)
        {
            if (!predicate(this[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The number of tracks matching the predicate.</summary>
    public int CountWhere(Func<GridTrack, bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < Count; i++)
        {
            if (predicate(this[i]))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The sum of the selected value across the view, accumulated in source order.</summary>
    public float Sum(Func<GridTrack, float> selector)
    {
        float total = 0.0f;
        for (int i = 0; i < Count; i++)
        {
            total += selector(this[i]);
        }

        return total;
    }

    /// <summary>
    /// The sum of the selected optional value, in source order, returning null if any element is
    /// null. Matches Rust's <c>.sum::&lt;Option&lt;f32&gt;&gt;()</c>.
    /// </summary>
    public float? SumOption(Func<GridTrack, float?> selector)
    {
        float total = 0.0f;
        for (int i = 0; i < Count; i++)
        {
            float? value = selector(this[i]);
            if (!value.HasValue)
            {
                return null;
            }

            total += value.Value;
        }

        return total;
    }

    /// <summary>Enumerate the tracks in the view.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Struct enumerator over a <see cref="TrackSlice"/>.</summary>
    internal struct Enumerator(TrackSlice slice)
    {
        private readonly TrackSlice _slice = slice;
        private int _index = -1;

        /// <summary>The current track.</summary>
        public readonly GridTrack Current => _slice[_index];

        /// <summary>Advance the enumerator.</summary>
        public bool MoveNext() => ++_index < _slice.Count;
    }
}
