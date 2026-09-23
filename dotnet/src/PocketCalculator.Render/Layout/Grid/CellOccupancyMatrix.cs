// Port of vendor/taffy/src/compute/grid/types/cell_occupancy.rs
//
// taffy stores the occupancy states in a `grid::Grid<CellOccupancyState>`. The
// port uses a flat row-major array with the same semantics, including the grid
// crate's rule that a grid with zero rows or zero columns is empty in both.
namespace PocketCalculator.Render.Layout;

/// <summary>The occupancy state of a single grid cell.</summary>
internal enum CellOccupancyState : byte
{
    /// <summary>Indicates that a grid cell is unoccupied.</summary>
    Unoccupied,

    /// <summary>Indicates that a grid cell is occupied by a definitely placed item.</summary>
    DefinitelyPlaced,

    /// <summary>Indicates that a grid cell is occupied by an item placed by auto-placement.</summary>
    AutoPlaced,
}

/// <summary>
/// A dynamically sized matrix which tracks the occupancy of each grid cell during auto-placement.
/// It also keeps tabs on how many tracks there are and which are implicit/explicit.
/// </summary>
internal sealed class CellOccupancyMatrix
{
    private CellOccupancyState[] _inner;
    private int _rowCount;
    private int _colCount;
    private TrackCounts _columns;
    private TrackCounts _rows;

    private CellOccupancyMatrix(TrackCounts columns, TrackCounts rows)
    {
        _columns = columns;
        _rows = rows;
        (_rowCount, _colCount) = NormalizeDimensions(rows.Len(), columns.Len());
        _inner = new CellOccupancyState[_rowCount * _colCount];
    }

    /// <summary>Create a CellOccupancyMatrix given a set of provisional track counts.</summary>
    public static CellOccupancyMatrix WithTrackCounts(TrackCounts columns, TrackCounts rows) =>
        new(columns, rows);

    /// <summary>Determines whether the specified area fits within the tracks currently allocated.</summary>
    public bool IsAreaInRange(AbsoluteAxis primaryAxis, TrackRange primaryRange, TrackRange secondaryRange)
    {
        if (primaryRange.Start < 0 || primaryRange.End > (short)TrackCountsFor(primaryAxis).Len())
        {
            return false;
        }

        if (secondaryRange.Start < 0
            || secondaryRange.End > (short)TrackCountsFor(primaryAxis.OtherAxis()).Len())
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Mark an area of the matrix as occupied, expanding the allocated space as necessary.
    /// </summary>
    public void MarkAreaAs(
        AbsoluteAxis primaryAxis,
        Line<OriginZeroLine> primarySpan,
        Line<OriginZeroLine> secondarySpan,
        CellOccupancyState value)
    {
        var rowSpan = primaryAxis == AbsoluteAxis.Horizontal ? secondarySpan : primarySpan;
        var columnSpan = primaryAxis == AbsoluteAxis.Horizontal ? primarySpan : secondarySpan;

        var colRange = _columns.OzLineRangeToTrackRange(columnSpan);
        var rowRange = _rows.OzLineRangeToTrackRange(rowSpan);

        // Check that the resolved ranges fit within the allocated grid. If they don't then expand
        // the grid and re-resolve the ranges, as the resolved indexes may have changed.
        bool isInRange = IsAreaInRange(AbsoluteAxis.Horizontal, colRange, rowRange);
        if (!isInRange)
        {
            ExpandToFitRange(rowRange, colRange);
            colRange = _columns.OzLineRangeToTrackRange(columnSpan);
            rowRange = _rows.OzLineRangeToTrackRange(rowSpan);
        }

        // One Fill per row rather than a store per cell: a spanning item covers up to
        // GridLimits.MaxTracks squared cells.
        int width = colRange.End - colRange.Start;
        if (width <= 0)
        {
            return;
        }

        for (int x = rowRange.Start; x < rowRange.End; x++)
        {
            _inner.AsSpan((x * _colCount) + colRange.Start, width).Fill(value);
        }
    }

    /// <summary>
    /// Determines whether a grid area specified by the bounding grid lines in OriginZero coordinates
    /// is entirely unoccupied.
    /// </summary>
    public bool LineAreaIsUnoccupied(
        AbsoluteAxis primaryAxis,
        Line<OriginZeroLine> primarySpan,
        Line<OriginZeroLine> secondarySpan)
    {
        var primaryRange = TrackCountsFor(primaryAxis).OzLineRangeToTrackRange(primarySpan);
        var secondaryRange = TrackCountsFor(primaryAxis.OtherAxis()).OzLineRangeToTrackRange(secondarySpan);
        return TrackAreaIsUnoccupied(primaryAxis, primaryRange, secondaryRange);
    }

    /// <summary>
    /// The first and last occupied primary-axis tracks of an area given by grid lines, as the
    /// origin-zero line starting each, or <c>null</c> when the whole area is unoccupied.
    /// </summary>
    /// <remarks>
    /// Port addition (taffy has only the yes/no query). Auto-placement uses it to jump past an
    /// occupied cell instead of stepping one track at a time, which visits the same positions'
    /// outcomes in far fewer probes: a candidate that contains an occupied track fails however
    /// far the search has moved within it.
    /// </remarks>
    public (OriginZeroLine First, OriginZeroLine Last)? OccupiedPrimaryTrackBounds(
        AbsoluteAxis primaryAxis,
        Line<OriginZeroLine> primarySpan,
        Line<OriginZeroLine> secondarySpan)
    {
        var primaryCounts = TrackCountsFor(primaryAxis);
        var primaryRange = primaryCounts.OzLineRangeToTrackRange(primarySpan);
        var secondaryRange = TrackCountsFor(primaryAxis.OtherAxis()).OzLineRangeToTrackRange(secondarySpan);
        bool primaryIsColumns = primaryAxis == AbsoluteAxis.Horizontal;
        var rowRange = primaryIsColumns ? secondaryRange : primaryRange;
        var colRange = primaryIsColumns ? primaryRange : secondaryRange;

        int rowStart = Math.Max((int)rowRange.Start, 0);
        int rowEnd = Math.Min((int)rowRange.End, _rowCount);
        int colStart = Math.Max((int)colRange.Start, 0);
        int colEnd = Math.Min((int)colRange.End, _colCount);
        if (colEnd <= colStart || rowEnd <= rowStart)
        {
            return null;
        }

        int first = int.MaxValue;
        int last = -1;
        for (int x = rowStart; x < rowEnd; x++)
        {
            var row = _inner.AsSpan((x * _colCount) + colStart, colEnd - colStart);
            if (primaryIsColumns)
            {
                int lo = row.IndexOfAnyExcept(CellOccupancyState.Unoccupied);
                if (lo >= 0)
                {
                    first = Math.Min(first, colStart + lo);
                    last = Math.Max(last, colStart + row.LastIndexOfAnyExcept(CellOccupancyState.Unoccupied));
                }
            }
            else if (row.ContainsAnyExcept(CellOccupancyState.Unoccupied))
            {
                first = Math.Min(first, x);
                last = x;
            }
        }

        return last < 0
            ? null
            : (primaryCounts.TrackToPrevOzLine((ushort)first), primaryCounts.TrackToPrevOzLine((ushort)last));
    }

    /// <summary>
    /// The first primary-axis track at or after <paramref name="from"/> (at or before it, when
    /// <paramref name="reversed"/>) whose cells across <paramref name="secondarySpan"/> are all
    /// unoccupied, as the origin-zero line starting it. Tracks outside the matrix are unoccupied,
    /// so the search ends at the matrix edge.
    /// </summary>
    /// <remarks>
    /// Port addition. A candidate position whose own track is occupied fails whatever its span,
    /// so auto-placement may skip every such position at once.
    /// </remarks>
    public OriginZeroLine NextFreePrimaryTrack(
        AbsoluteAxis primaryAxis,
        OriginZeroLine from,
        Line<OriginZeroLine> secondarySpan,
        bool reversed)
    {
        var primaryCounts = TrackCountsFor(primaryAxis);
        var secondaryRange = TrackCountsFor(primaryAxis.OtherAxis()).OzLineRangeToTrackRange(secondarySpan);
        bool primaryIsColumns = primaryAxis == AbsoluteAxis.Horizontal;
        int primaryLength = primaryIsColumns ? _colCount : _rowCount;
        int secondaryStart = Math.Max((int)secondaryRange.Start, 0);
        int secondaryEnd = Math.Min((int)secondaryRange.End, primaryIsColumns ? _rowCount : _colCount);
        int track = primaryCounts.OzLineToNextTrack(from);
        if (secondaryEnd <= secondaryStart)
        {
            return from;
        }

        while (track >= 0 && track < primaryLength)
        {
            int next = track;
            if (primaryIsColumns)
            {
                // Each row of the span proposes its own next free column; the answer is a column
                // every row agrees on, so move to the furthest proposal until they agree.
                for (int x = secondaryStart; x < secondaryEnd; x++)
                {
                    var row = _inner.AsSpan(x * _colCount, _colCount);
                    int free = reversed
                        ? row[..(next + 1)].LastIndexOf(CellOccupancyState.Unoccupied)
                        : row[next..].IndexOf(CellOccupancyState.Unoccupied) is var offset and >= 0
                            ? next + offset
                            : -1;
                    if (free < 0)
                    {
                        next = reversed ? -1 : primaryLength;
                        break;
                    }

                    next = free;
                }
            }
            else if (_inner.AsSpan((track * _colCount) + secondaryStart, secondaryEnd - secondaryStart)
                .ContainsAnyExcept(CellOccupancyState.Unoccupied))
            {
                next = reversed ? track - 1 : track + 1;
            }

            if (next == track)
            {
                break;
            }

            track = next;
        }

        return new OriginZeroLine((short)(track - primaryCounts.NegativeImplicit));
    }

    /// <summary>
    /// Determines whether a grid area specified by a range of indexes into this matrix is entirely
    /// unoccupied. Out of bounds cells are considered unoccupied.
    /// </summary>
    public bool TrackAreaIsUnoccupied(
        AbsoluteAxis primaryAxis,
        TrackRange primaryRange,
        TrackRange secondaryRange)
    {
        var rowRange = primaryAxis == AbsoluteAxis.Horizontal ? secondaryRange : primaryRange;
        var colRange = primaryAxis == AbsoluteAxis.Horizontal ? primaryRange : secondaryRange;

        // Out-of-bounds cells are unoccupied, so only the part of the area inside the matrix is
        // scanned, a row at a time.
        int rowStart = Math.Max((int)rowRange.Start, 0);
        int rowEnd = Math.Min((int)rowRange.End, _rowCount);
        int colStart = Math.Max((int)colRange.Start, 0);
        int colEnd = Math.Min((int)colRange.End, _colCount);
        if (colEnd <= colStart)
        {
            return true;
        }

        for (int x = rowStart; x < rowEnd; x++)
        {
            if (_inner.AsSpan((x * _colCount) + colStart, colEnd - colStart)
                .ContainsAnyExcept(CellOccupancyState.Unoccupied))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Determines whether the specified row contains any items.</summary>
    public bool RowIsOccupied(int rowIndex)
    {
        if (rowIndex >= _rowCount)
        {
            return false;
        }

        for (int col = 0; col < _colCount; col++)
        {
            if (_inner[(rowIndex * _colCount) + col] != CellOccupancyState.Unoccupied)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Determines whether the specified column contains any items.</summary>
    public bool ColumnIsOccupied(int columnIndex)
    {
        if (columnIndex >= _colCount)
        {
            return false;
        }

        for (int row = 0; row < _rowCount; row++)
        {
            if (_inner[(row * _colCount) + columnIndex] != CellOccupancyState.Unoccupied)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the track counts of this matrix in the relevant axis.</summary>
    public TrackCounts TrackCountsFor(AbsoluteAxis trackType) =>
        trackType == AbsoluteAxis.Horizontal ? _columns : _rows;

    /// <summary>
    /// Search backwards from the end of the track and find the last grid cell matching the specified
    /// state (if any).
    /// </summary>
    public OriginZeroLine? LastOfType(AbsoluteAxis trackType, OriginZeroLine startAt, CellOccupancyState kind)
    {
        var trackCounts = TrackCountsFor(trackType.OtherAxis());
        short trackComputedIndex = trackCounts.OzLineToNextTrack(startAt);

        int? maybeIndex;
        if (trackType == AbsoluteAxis.Horizontal)
        {
            maybeIndex = trackComputedIndex < 0 || trackComputedIndex >= _rowCount
                ? null
                : RPositionInRow(trackComputedIndex, kind);
        }
        else
        {
            maybeIndex = trackComputedIndex < 0 || trackComputedIndex >= _colCount
                ? null
                : RPositionInColumn(trackComputedIndex, kind);
        }

        return maybeIndex.HasValue ? trackCounts.TrackToPrevOzLine((ushort)maybeIndex.Value) : null;
    }

    /// <summary>
    /// Search forwards from the start of the track and find the first grid cell matching the
    /// specified state (if any).
    /// </summary>
    public OriginZeroLine? FirstOfType(AbsoluteAxis trackType, OriginZeroLine startAt, CellOccupancyState kind)
    {
        var trackCounts = TrackCountsFor(trackType.OtherAxis());
        short trackComputedIndex = trackCounts.OzLineToNextTrack(startAt);

        int? maybeIndex;
        if (trackType == AbsoluteAxis.Horizontal)
        {
            maybeIndex = trackComputedIndex < 0 || trackComputedIndex >= _rowCount
                ? null
                : PositionInRow(trackComputedIndex, kind);
        }
        else
        {
            maybeIndex = trackComputedIndex < 0 || trackComputedIndex >= _colCount
                ? null
                : PositionInColumn(trackComputedIndex, kind);
        }

        return maybeIndex.HasValue ? trackCounts.TrackToPrevOzLine((ushort)maybeIndex.Value) : null;
    }

    private static (int Rows, int Cols) NormalizeDimensions(int rows, int cols) =>
        rows == 0 || cols == 0 ? (0, 0) : (rows, cols);

    private int? PositionInRow(int row, CellOccupancyState kind)
    {
        for (int col = 0; col < _colCount; col++)
        {
            if (_inner[(row * _colCount) + col] == kind)
            {
                return col;
            }
        }

        return null;
    }

    private int? RPositionInRow(int row, CellOccupancyState kind)
    {
        for (int col = _colCount - 1; col >= 0; col--)
        {
            if (_inner[(row * _colCount) + col] == kind)
            {
                return col;
            }
        }

        return null;
    }

    private int? PositionInColumn(int col, CellOccupancyState kind)
    {
        for (int row = 0; row < _rowCount; row++)
        {
            if (_inner[(row * _colCount) + col] == kind)
            {
                return row;
            }
        }

        return null;
    }

    private int? RPositionInColumn(int col, CellOccupancyState kind)
    {
        for (int row = _rowCount - 1; row >= 0; row--)
        {
            if (_inner[(row * _colCount) + col] == kind)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// Expands the grid (potentially in all 4 directions) so that the specified range fits within
    /// the allocated space.
    /// </summary>
    private void ExpandToFitRange(TrackRange rowRange, TrackRange colRange)
    {
        int reqNegativeRows = Math.Max(-rowRange.Start, 0);
        int reqPositiveRows = Math.Max(rowRange.End - _rows.Len(), 0);
        int reqNegativeCols = Math.Max(-colRange.Start, 0);
        int reqPositiveCols = Math.Max(colRange.End - _columns.Len(), 0);

        int oldRowCount = _rows.Len();
        int oldColCount = _columns.Len();
        int newRowCount = oldRowCount + reqNegativeRows + reqPositiveRows;
        int newColCount = oldColCount + reqNegativeCols + reqPositiveCols;

        // Copy whole rows into a fresh zeroed array (Unoccupied is zero) rather than appending
        // cell by cell as taffy's Vec-based grid does; the layout is the same.
        var data = new CellOccupancyState[newRowCount * newColCount];
        for (int row = 0; row < oldRowCount; row++)
        {
            _inner.AsSpan(row * _colCount, oldColCount)
                .CopyTo(data.AsSpan(((row + reqNegativeRows) * newColCount) + reqNegativeCols));
        }

        _inner = data;
        _colCount = newColCount;
        _rowCount = newColCount == 0 ? 0 : data.Length / newColCount;

        _rows = _rows with { NegativeImplicit = (ushort)(_rows.NegativeImplicit + reqNegativeRows) };
        _rows = _rows with { PositiveImplicit = (ushort)(_rows.PositiveImplicit + reqPositiveRows) };
        _columns = _columns with { NegativeImplicit = (ushort)(_columns.NegativeImplicit + reqNegativeCols) };
        _columns = _columns with { PositiveImplicit = (ushort)(_columns.PositiveImplicit + reqPositiveCols) };
    }
}
