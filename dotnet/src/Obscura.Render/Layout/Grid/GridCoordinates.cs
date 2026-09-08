// Port of vendor/taffy/src/compute/grid/types/coordinates.rs and
// vendor/taffy/src/compute/grid/types/grid_track_counts.rs
//
// Taffy uses two coordinate systems to refer to grid lines (the gaps/gutters
// between rows/columns):
//
//   "CSS Grid Line" coordinates are those used in grid-row/grid-column in the
//   CSS grid spec:
//     - 0 is not a valid index
//     - The line at the left hand (or top) edge of the explicit grid is line 1
//     - The line at the right hand (or bottom) edge of the explicit grid is -1
//
//   "OriginZero" coordinates are a normalized form:
//     - The line at the left hand (or top) edge of the explicit grid is line 0
//     - The next line to the right (or down) is 1, and so on
//     - The next line to the left (or up) is -1, and so on
//
// Taffy also uses two coordinate systems to refer to grid tracks (rows/columns):
//
//   "CellOccupancyMatrix track indices": indexes into the CellOccupancyMatrix,
//   which stores only tracks; 0 is the leftmost track of the implicit grid.
//
//   "GridTrackVec track indices": the track vectors store both lines and tracks,
//   so even indices are lines and odd indices are tracks.
namespace Obscura.Render.Layout;

/// <summary>
/// Represents a grid line position in "CSS Grid Line" coordinates.
/// </summary>
internal readonly record struct GridLine(short Value)
{
    /// <summary>Returns the underlying i16.</summary>
    public short AsI16() => Value;

    /// <summary>Create from a raw i16.</summary>
    public static GridLine From(short value) => new(value);

    /// <summary>Convert into OriginZero coordinates using the specified explicit track count.</summary>
    public OriginZeroLine IntoOriginZeroLine(ushort explicitTrackCount)
    {
        int explicitLineCount = explicitTrackCount + 1;
        if (Value > 0)
        {
            return new OriginZeroLine((short)(Value - 1));
        }

        if (Value < 0)
        {
            return new OriginZeroLine((short)(Value + (short)explicitLineCount));
        }

        throw new InvalidOperationException("Grid line of zero is invalid");
    }

    /// <inheritdoc/>
    public override string ToString() => $"GridLine({Value})";
}

/// <summary>
/// Represents a grid line position in "OriginZero" coordinates.
/// </summary>
internal readonly record struct OriginZeroLine(short Value) : IComparable<OriginZeroLine>
{
    /// <summary>Add two origin-zero lines.</summary>
    public static OriginZeroLine operator +(OriginZeroLine a, OriginZeroLine b) =>
        new((short)(a.Value + b.Value));

    /// <summary>Subtract two origin-zero lines.</summary>
    public static OriginZeroLine operator -(OriginZeroLine a, OriginZeroLine b) =>
        new((short)(a.Value - b.Value));

    /// <summary>Add a track count.</summary>
    public static OriginZeroLine operator +(OriginZeroLine a, ushort b) => new((short)(a.Value + (short)b));

    /// <summary>Subtract a track count.</summary>
    public static OriginZeroLine operator -(OriginZeroLine a, ushort b) => new((short)(a.Value - (short)b));

    /// <summary>Less-than comparison.</summary>
    public static bool operator <(OriginZeroLine a, OriginZeroLine b) => a.Value < b.Value;

    /// <summary>Greater-than comparison.</summary>
    public static bool operator >(OriginZeroLine a, OriginZeroLine b) => a.Value > b.Value;

    /// <summary>Less-than-or-equal comparison.</summary>
    public static bool operator <=(OriginZeroLine a, OriginZeroLine b) => a.Value <= b.Value;

    /// <summary>Greater-than-or-equal comparison.</summary>
    public static bool operator >=(OriginZeroLine a, OriginZeroLine b) => a.Value >= b.Value;

    /// <inheritdoc/>
    public int CompareTo(OriginZeroLine other) => Value.CompareTo(other.Value);

    /// <summary>The smaller of two lines.</summary>
    public static OriginZeroLine Min(OriginZeroLine a, OriginZeroLine b) => a.Value <= b.Value ? a : b;

    /// <summary>The larger of two lines.</summary>
    public static OriginZeroLine Max(OriginZeroLine a, OriginZeroLine b) => a.Value >= b.Value ? a : b;

    /// <summary>
    /// Converts a grid line in OriginZero coordinates into the index of that same grid line in the
    /// grid track vector. Throws if the line is out of range.
    /// </summary>
    public int IntoTrackVecIndex(TrackCounts trackCounts) =>
        TryIntoTrackVecIndex(trackCounts)
        ?? throw new InvalidOperationException(
            Value > 0
                ? "OriginZero grid line cannot be more than the number of positive grid lines"
                : "OriginZero grid line cannot be less than the number of negative grid lines");

    /// <summary>
    /// Fallible version of <see cref="IntoTrackVecIndex"/>, used when placing absolutely positioned
    /// grid items.
    /// </summary>
    public int? TryIntoTrackVecIndex(TrackCounts trackCounts)
    {
        if (Value < -(short)trackCounts.NegativeImplicit)
        {
            return null;
        }

        if (Value > (short)(trackCounts.Explicit + trackCounts.PositiveImplicit))
        {
            return null;
        }

        return 2 * (Value + trackCounts.NegativeImplicit);
    }

    /// <summary>
    /// The minimum number of negative implicit tracks there must be if a grid item starts at this
    /// line.
    /// </summary>
    public ushort ImpliedNegativeImplicitTracks() => Value < 0 ? (ushort)(-Value) : (ushort)0;

    /// <summary>
    /// The minimum number of positive implicit tracks there must be if a grid item ends at this
    /// line.
    /// </summary>
    public ushort ImpliedPositiveImplicitTracks(ushort explicitTrackCount) =>
        Value > (short)explicitTrackCount ? (ushort)(Value - explicitTrackCount) : (ushort)0;

    /// <inheritdoc/>
    public override string ToString() => $"OriginZeroLine({Value})";
}

/// <summary>A half-open range of CellOccupancyMatrix track indexes.</summary>
internal readonly record struct TrackRange(short Start, short End);

/// <summary>
/// Stores the number of tracks in a given dimension, split between the implicit and explicit grids.
/// </summary>
internal readonly record struct TrackCounts(ushort NegativeImplicit, ushort Explicit, ushort PositiveImplicit)
{
    /// <summary>Create a TrackCounts instance from raw track count numbers.</summary>
    public static TrackCounts FromRaw(ushort negativeImplicit, ushort @explicit, ushort positiveImplicit) =>
        new(negativeImplicit, @explicit, positiveImplicit);

    /// <summary>Count the total number of tracks in the axis.</summary>
    public int Len() => NegativeImplicit + Explicit + PositiveImplicit;

    /// <summary>The OriginZeroLine representing the start of the implicit grid.</summary>
    public OriginZeroLine ImplicitStartLine() => new((short)(-(short)NegativeImplicit));

    /// <summary>The OriginZeroLine representing the end of the implicit grid.</summary>
    public OriginZeroLine ImplicitEndLine() => new((short)(Explicit + PositiveImplicit));

    /// <summary>
    /// Converts a grid line in OriginZero coordinates into the track immediately following that
    /// grid line as an index into the CellOccupancyMatrix.
    /// </summary>
    public short OzLineToNextTrack(OriginZeroLine index) => (short)(index.Value + (short)NegativeImplicit);

    /// <summary>
    /// Converts start and end grid lines in OriginZero coordinates into a range of tracks as
    /// indexes into the CellOccupancyMatrix.
    /// </summary>
    public TrackRange OzLineRangeToTrackRange(Line<OriginZeroLine> input) =>
        new(OzLineToNextTrack(input.Start), OzLineToNextTrack(input.End));

    /// <summary>
    /// Converts a track as an index into the CellOccupancyMatrix into the grid line immediately
    /// preceding that track in OriginZero coordinates.
    /// </summary>
    public OriginZeroLine TrackToPrevOzLine(ushort index) =>
        new((short)((short)index - (short)NegativeImplicit));

    /// <summary>
    /// Converts a range of tracks as indexes into the CellOccupancyMatrix into start and end grid
    /// lines in OriginZero coordinates.
    /// </summary>
    public Line<OriginZeroLine> TrackRangeToOzLineRange(TrackRange input) =>
        new(TrackToPrevOzLine((ushort)input.Start), TrackToPrevOzLine((ushort)input.End));
}

/// <summary>Helpers over lines of grid coordinates.</summary>
internal static class GridCoordinateExtensions
{
    /// <summary>The number of tracks between the start and end lines.</summary>
    public static ushort Span(this Line<OriginZeroLine> line)
    {
        int span = line.End.Value - line.Start.Value;
        return span > 0 ? (ushort)span : (ushort)0;
    }
}
