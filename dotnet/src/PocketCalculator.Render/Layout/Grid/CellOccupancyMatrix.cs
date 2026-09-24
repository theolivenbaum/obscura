// Port of vendor/taffy/src/compute/grid/types/cell_occupancy.rs
//
// Deviation from taffy, which stores one CellOccupancyState per cell in a
// `grid::Grid<CellOccupancyState>` (rows x columns, grown by copying): the port keeps the
// placed areas themselves, indexed by a segment tree over the secondary axis whose nodes
// hold merged interval sets of primary-axis tracks. A dense matrix made one item spanning a
// grid of GridLimits.MaxTracks tracks per axis cost 100M cells (about 80 ms and 100-400 MB
// for one layout), and it could not grow to the implicit-track window placement now allows.
// Memory is O(items x log tracks) and every query is polylogarithmic; the answers are the
// dense matrix's, cell for cell (CellOccupancyMatrixTests compares the two on random input).
// A grid of at most DefaultDenseCellLimit cells keeps a dense byte per cell instead, which is
// cheaper to build for the small grids a page lays out many times over.
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
/// Tracks the occupancy of each grid cell during auto-placement. It also keeps tabs on how many
/// tracks there are and which are implicit/explicit.
/// </summary>
/// <remarks>
/// Only whether a cell is occupied is kept, not by what: taffy's one reader of the state
/// (<c>last_of_type</c>, the step 2 cursor) is replaced by Chromium's per-line cursor in
/// <see cref="GridPlacementAlgorithm"/>.
/// </remarks>
internal sealed class CellOccupancyMatrix
{
    /// <summary>The most cells a grid may have and still be tracked densely.</summary>
    internal const int DefaultDenseCellLimit = 1024;

    private readonly List<Area> _areas = [];
    private readonly int _denseCellLimit;
    private TrackCounts _columns;
    private TrackCounts _rows;

    // A byte per cell while the grid is small; null once it has outgrown the limit, after
    // which the indexes below answer.
    private DenseCells? _dense;

    // One index per primary axis, built on first use from _areas. Placement uses only the one
    // for its flow direction.
    private OccupancyIndex? _columnsPrimary;
    private OccupancyIndex? _rowsPrimary;

    private CellOccupancyMatrix(TrackCounts columns, TrackCounts rows, int denseCellLimit)
    {
        _columns = columns;
        _rows = rows;
        _denseCellLimit = denseCellLimit;
        _dense = DenseCells.Resize(null, columns, rows, denseCellLimit);
    }

    /// <summary>Create a CellOccupancyMatrix given a set of provisional track counts.</summary>
    public static CellOccupancyMatrix WithTrackCounts(
        TrackCounts columns, TrackCounts rows, int denseCellLimit = DefaultDenseCellLimit) =>
        new(columns, rows, denseCellLimit);

    /// <summary>Determines whether the specified area fits within the tracks currently allocated.</summary>
    public bool IsAreaInRange(AbsoluteAxis primaryAxis, TrackRange primaryRange, TrackRange secondaryRange)
    {
        if (primaryRange.Start < 0 || primaryRange.End > TrackCountsFor(primaryAxis).Len())
        {
            return false;
        }

        if (secondaryRange.Start < 0
            || secondaryRange.End > TrackCountsFor(primaryAxis.OtherAxis()).Len())
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
        // the grid.
        if (!IsAreaInRange(AbsoluteAxis.Horizontal, colRange, rowRange))
        {
            ExpandToFitRange(rowRange, colRange);
            if (_dense is not null)
            {
                _dense = DenseCells.Resize(_dense, _columns, _rows, _denseCellLimit);
            }
        }

        if (value == CellOccupancyState.Unoccupied
            || columnSpan.End.Value <= columnSpan.Start.Value
            || rowSpan.End.Value <= rowSpan.Start.Value)
        {
            return;
        }

        var area = new Area(columnSpan.Start.Value, columnSpan.End.Value, rowSpan.Start.Value, rowSpan.End.Value);
        _areas.Add(area);
        _dense?.Fill(area);
        _columnsPrimary?.Add(area);
        _rowsPrimary?.Add(area);
    }

    /// <summary>
    /// Determines whether a grid area specified by the bounding grid lines in OriginZero coordinates
    /// is entirely unoccupied.
    /// </summary>
    public bool LineAreaIsUnoccupied(
        AbsoluteAxis primaryAxis,
        Line<OriginZeroLine> primarySpan,
        Line<OriginZeroLine> secondarySpan) =>
        _areas.Count == 0
        || !(_dense is { } dense
            ? dense.Intersects(
                primaryAxis == AbsoluteAxis.Horizontal,
                primarySpan.Start.Value,
                primarySpan.End.Value,
                secondarySpan.Start.Value,
                secondarySpan.End.Value)
            : IndexFor(primaryAxis).Intersects(
                primarySpan.Start.Value, primarySpan.End.Value, secondarySpan.Start.Value, secondarySpan.End.Value));

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
        if (_areas.Count == 0)
        {
            return null;
        }

        var found = _dense is { } dense
            ? dense.OccupiedBounds(
                primaryAxis == AbsoluteAxis.Horizontal,
                primarySpan.Start.Value,
                primarySpan.End.Value,
                secondarySpan.Start.Value,
                secondarySpan.End.Value)
            : IndexFor(primaryAxis).OccupiedBounds(
                primarySpan.Start.Value, primarySpan.End.Value, secondarySpan.Start.Value, secondarySpan.End.Value);
        return found is { } bounds
            ? (new OriginZeroLine(bounds.First), new OriginZeroLine(bounds.Last))
            : null;
    }

    /// <summary>
    /// The first primary-axis track at or after <paramref name="from"/> (at or before it, when
    /// <paramref name="reversed"/>) whose cells across <paramref name="secondarySpan"/> are all
    /// unoccupied, as the origin-zero line starting it. Tracks outside the occupied area are
    /// unoccupied, so the search ends at its edge.
    /// </summary>
    /// <remarks>
    /// Port addition. A candidate position whose own track is occupied fails whatever its span,
    /// so auto-placement may skip every such position at once.
    /// </remarks>
    public OriginZeroLine NextFreePrimaryTrack(
        AbsoluteAxis primaryAxis,
        OriginZeroLine from,
        Line<OriginZeroLine> secondarySpan,
        bool reversed) =>
        _areas.Count == 0
            ? from
            : new OriginZeroLine(_dense is { } dense
                ? dense.NextFree(
                    primaryAxis == AbsoluteAxis.Horizontal,
                    from.Value,
                    secondarySpan.Start.Value,
                    secondarySpan.End.Value,
                    reversed)
                : IndexFor(primaryAxis).NextFree(
                    from.Value, secondarySpan.Start.Value, secondarySpan.End.Value, reversed));

    /// <summary>
    /// Determines whether a grid area specified by a range of indexes into this matrix is entirely
    /// unoccupied. Out of bounds cells are considered unoccupied.
    /// </summary>
    public bool TrackAreaIsUnoccupied(
        AbsoluteAxis primaryAxis,
        TrackRange primaryRange,
        TrackRange secondaryRange)
    {
        var primaryCounts = TrackCountsFor(primaryAxis);
        var secondaryCounts = TrackCountsFor(primaryAxis.OtherAxis());
        return LineAreaIsUnoccupied(
            primaryAxis,
            primaryCounts.TrackRangeToOzLineRange(primaryRange),
            secondaryCounts.TrackRangeToOzLineRange(secondaryRange));
    }

    /// <summary>Determines whether the specified row contains any items.</summary>
    public bool RowIsOccupied(int rowIndex) => TrackIsOccupied(AbsoluteAxis.Vertical, rowIndex);

    /// <summary>Determines whether the specified column contains any items.</summary>
    public bool ColumnIsOccupied(int columnIndex) => TrackIsOccupied(AbsoluteAxis.Horizontal, columnIndex);

    /// <summary>Returns the track counts of this matrix in the relevant axis.</summary>
    public TrackCounts TrackCountsFor(AbsoluteAxis trackType) =>
        trackType == AbsoluteAxis.Horizontal ? _columns : _rows;

    private bool TrackIsOccupied(AbsoluteAxis axis, int index)
    {
        if (_areas.Count == 0)
        {
            return false;
        }

        int line = TrackCountsFor(axis).TrackToPrevOzLine(index).Value;
        if (_dense is { } dense)
        {
            return dense.TrackIsOccupied(axis == AbsoluteAxis.Horizontal, line);
        }

        // Either index answers; prefer the one placement already built.
        return _columnsPrimary is null && _rowsPrimary is null
            ? IndexFor(AbsoluteAxis.Horizontal).TrackIsOccupied(axis == AbsoluteAxis.Horizontal, line)
            : _columnsPrimary is { } byColumns
                ? byColumns.TrackIsOccupied(axis == AbsoluteAxis.Horizontal, line)
                : _rowsPrimary!.TrackIsOccupied(axis == AbsoluteAxis.Vertical, line);
    }

    private OccupancyIndex IndexFor(AbsoluteAxis primaryAxis)
    {
        if (primaryAxis == AbsoluteAxis.Horizontal)
        {
            return _columnsPrimary ??= OccupancyIndex.Build(primaryIsColumns: true, _areas);
        }

        return _rowsPrimary ??= OccupancyIndex.Build(primaryIsColumns: false, _areas);
    }

    /// <summary>
    /// Expands the grid (potentially in all 4 directions) so that the specified range fits within
    /// the allocated space. Areas are kept in origin-zero lines, so only the counts change.
    /// </summary>
    private void ExpandToFitRange(TrackRange rowRange, TrackRange colRange)
    {
        int reqNegativeRows = Math.Max(-rowRange.Start, 0);
        int reqPositiveRows = Math.Max(rowRange.End - _rows.Len(), 0);
        int reqNegativeCols = Math.Max(-colRange.Start, 0);
        int reqPositiveCols = Math.Max(colRange.End - _columns.Len(), 0);

        _rows = _rows with
        {
            NegativeImplicit = _rows.NegativeImplicit + reqNegativeRows,
            PositiveImplicit = _rows.PositiveImplicit + reqPositiveRows,
        };
        _columns = _columns with
        {
            NegativeImplicit = _columns.NegativeImplicit + reqNegativeCols,
            PositiveImplicit = _columns.PositiveImplicit + reqPositiveCols,
        };
    }

    /// <summary>An occupied area, in origin-zero lines.</summary>
    private readonly record struct Area(int ColumnStart, int ColumnEnd, int RowStart, int RowEnd);

    /// <summary>
    /// A byte per cell over the grid's current tracks, row-major, for a small grid. Out of range
    /// cells are unoccupied.
    /// </summary>
    private sealed class DenseCells
    {
        private readonly byte[] _cells;
        private readonly int _row0;
        private readonly int _col0;
        private readonly int _rows;
        private readonly int _cols;

        private DenseCells(int row0, int rows, int col0, int cols)
        {
            _row0 = row0;
            _rows = rows;
            _col0 = col0;
            _cols = cols;
            _cells = new byte[rows * cols];
        }

        /// <summary>
        /// Cells for the given track counts with the old cells copied in, or null when the grid
        /// is over the limit.
        /// </summary>
        public static DenseCells? Resize(DenseCells? old, TrackCounts columns, TrackCounts rows, int limit)
        {
            long cells = (long)columns.Len() * rows.Len();
            if (cells > limit)
            {
                return null;
            }

            var grown = new DenseCells(-rows.NegativeImplicit, rows.Len(), -columns.NegativeImplicit, columns.Len());
            if (old is not null && old._cols != 0)
            {
                for (int row = 0; row < old._rows; row++)
                {
                    old._cells.AsSpan(row * old._cols, old._cols).CopyTo(grown._cells.AsSpan(
                        ((row + old._row0 - grown._row0) * grown._cols) + (old._col0 - grown._col0)));
                }
            }

            return grown;
        }

        public void Fill(Area area)
        {
            int colStart = Math.Max(area.ColumnStart - _col0, 0);
            int colEnd = Math.Min(area.ColumnEnd - _col0, _cols);
            if (colEnd <= colStart)
            {
                return;
            }

            for (int row = Math.Max(area.RowStart - _row0, 0); row < Math.Min(area.RowEnd - _row0, _rows); row++)
            {
                _cells.AsSpan((row * _cols) + colStart, colEnd - colStart).Fill(1);
            }
        }

        public bool Intersects(bool primaryIsColumns, int pLo, int pHi, int sLo, int sHi)
        {
            var (colLo, colHi, rowLo, rowHi) = Cells(primaryIsColumns, pLo, pHi, sLo, sHi);
            if (colHi <= colLo)
            {
                return false;
            }

            for (int row = rowLo; row < rowHi; row++)
            {
                if (_cells.AsSpan((row * _cols) + colLo, colHi - colLo).Contains((byte)1))
                {
                    return true;
                }
            }

            return false;
        }

        public (int First, int Last)? OccupiedBounds(bool primaryIsColumns, int pLo, int pHi, int sLo, int sHi)
        {
            var (colLo, colHi, rowLo, rowHi) = Cells(primaryIsColumns, pLo, pHi, sLo, sHi);
            if (colHi <= colLo)
            {
                return null;
            }

            int first = int.MaxValue;
            int last = -1;
            for (int row = rowLo; row < rowHi; row++)
            {
                var cells = _cells.AsSpan((row * _cols) + colLo, colHi - colLo);
                if (primaryIsColumns)
                {
                    int lo = cells.IndexOf((byte)1);
                    if (lo >= 0)
                    {
                        first = Math.Min(first, colLo + lo);
                        last = Math.Max(last, colLo + cells.LastIndexOf((byte)1));
                    }
                }
                else if (cells.Contains((byte)1))
                {
                    first = Math.Min(first, row);
                    last = row;
                }
            }

            if (last < 0)
            {
                return null;
            }

            int origin = primaryIsColumns ? _col0 : _row0;
            return (first + origin, last + origin);
        }

        public int NextFree(bool primaryIsColumns, int from, int sLo, int sHi, bool reversed)
        {
            int origin = primaryIsColumns ? _col0 : _row0;
            int length = primaryIsColumns ? _cols : _rows;
            int track = from - origin;
            while (track >= 0 && track < length)
            {
                bool occupied = primaryIsColumns
                    ? Intersects(primaryIsColumns: true, track + origin, track + origin + 1, sLo, sHi)
                    : Intersects(primaryIsColumns: false, track + origin, track + origin + 1, sLo, sHi);
                if (!occupied)
                {
                    break;
                }

                track += reversed ? -1 : 1;
            }

            return track + origin;
        }

        public bool TrackIsOccupied(bool columnAxis, int line)
        {
            int index = line - (columnAxis ? _col0 : _row0);
            if (index < 0 || index >= (columnAxis ? _cols : _rows))
            {
                return false;
            }

            if (!columnAxis)
            {
                return _cells.AsSpan(index * _cols, _cols).Contains((byte)1);
            }

            for (int row = 0; row < _rows; row++)
            {
                if (_cells[(row * _cols) + index] != 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A query's column and row index ranges, clamped to the cells.</summary>
        private (int ColLo, int ColHi, int RowLo, int RowHi) Cells(
            bool primaryIsColumns, int pLo, int pHi, int sLo, int sHi)
        {
            var (cLo, cHi, rLo, rHi) = primaryIsColumns ? (pLo, pHi, sLo, sHi) : (sLo, sHi, pLo, pHi);
            return (
                Math.Max(cLo - _col0, 0),
                Math.Min(cHi - _col0, _cols),
                Math.Max(rLo - _row0, 0),
                Math.Min(rHi - _row0, _rows));
        }
    }

    /// <summary>
    /// A segment tree over the secondary axis. Each area is stored, as its primary-axis interval,
    /// in the O(log n) nodes that exactly cover its secondary range (<c>Own</c>); every node
    /// also keeps the union of the intervals stored anywhere beneath it (<c>Sub</c>). The
    /// primary tracks occupied across a secondary band are then the union of <c>Own</c> along
    /// the band's two boundary paths and <c>Sub</c> of the nodes it covers whole.
    /// </summary>
    private sealed class OccupancyIndex
    {
        private readonly bool _primaryIsColumns;
        private readonly List<IntervalSet> _scratch = [];
        private Node? _root;
        private int _lo;
        private int _size;

        private OccupancyIndex(bool primaryIsColumns) => _primaryIsColumns = primaryIsColumns;

        public static OccupancyIndex Build(bool primaryIsColumns, List<Area> areas)
        {
            var index = new OccupancyIndex(primaryIsColumns);
            foreach (var area in areas)
            {
                index.Add(area);
            }

            return index;
        }

        public void Add(Area area)
        {
            var (pLo, pHi, sLo, sHi) = _primaryIsColumns
                ? (area.ColumnStart, area.ColumnEnd, area.RowStart, area.RowEnd)
                : (area.RowStart, area.RowEnd, area.ColumnStart, area.ColumnEnd);
            Cover(sLo, sHi);
            Insert(_root!, _lo, _size, sLo, sHi, pLo, pHi);
        }

        public bool Intersects(int pLo, int pHi, int sLo, int sHi)
        {
            if (pHi <= pLo)
            {
                return false;
            }

            var sets = Collect(sLo, sHi);
            foreach (var set in sets)
            {
                if (set.Intersects(pLo, pHi))
                {
                    return true;
                }
            }

            return false;
        }

        public (int First, int Last)? OccupiedBounds(int pLo, int pHi, int sLo, int sHi)
        {
            int first = int.MaxValue;
            int last = int.MinValue;
            if (pHi > pLo)
            {
                foreach (var set in Collect(sLo, sHi))
                {
                    if (set.BoundsWithin(pLo, pHi) is { } bounds)
                    {
                        first = Math.Min(first, bounds.First);
                        last = Math.Max(last, bounds.Last);
                    }
                }
            }

            return last == int.MinValue ? null : (first, last);
        }

        public int NextFree(int from, int sLo, int sHi, bool reversed) =>
            Uncovered(Collect(sLo, sHi), from, reversed);

        public bool TrackIsOccupied(bool axisIsPrimary, int line)
        {
            if (axisIsPrimary)
            {
                return _root?.Sub is { } all && all.Intersects(line, line + 1);
            }

            return Collect(line, line + 1).Count > 0;
        }

        /// <summary>The first line at or after (at or before, reversed) p covered by no set.</summary>
        private static int Uncovered(List<IntervalSet> sets, int p, bool reversed)
        {
            bool moved = true;
            while (moved)
            {
                moved = false;
                foreach (var set in sets)
                {
                    int q = reversed ? set.PrevUncovered(p) : set.NextUncovered(p);
                    if (q != p)
                    {
                        p = q;
                        moved = true;
                    }
                }
            }

            return p;
        }

        private List<IntervalSet> Collect(int sLo, int sHi)
        {
            var into = _scratch;
            into.Clear();
            if (_root is not null && sHi > sLo)
            {
                CollectFrom(_root, _lo, _size, sLo, sHi, into);
            }

            return into;
        }

        private static void CollectFrom(
            Node node, int lo, int size, int sLo, int sHi, List<IntervalSet> into)
        {
            while (true)
            {
                int hi = lo + size;
                if (hi <= sLo || lo >= sHi)
                {
                    return;
                }

                if (sLo <= lo && hi <= sHi)
                {
                    if (node.Sub is { } whole)
                    {
                        into.Add(whole);
                    }

                    return;
                }

                if (node.Own is { } own)
                {
                    into.Add(own);
                }

                int half = size >> 1;
                if (node.Left is { } left)
                {
                    CollectFrom(left, lo, half, sLo, sHi, into);
                }

                if (node.Right is not { } right)
                {
                    return;
                }

                node = right;
                lo += half;
                size = half;
            }
        }

        private static void Insert(
            Node node, int lo, int size, int sLo, int sHi, int pLo, int pHi)
        {
            while (true)
            {
                (node.Sub ??= new IntervalSet()).Add(pLo, pHi);

                int hi = lo + size;
                if (sLo <= lo && hi <= sHi)
                {
                    (node.Own ??= new IntervalSet()).Add(pLo, pHi);

                    return;
                }

                int half = size >> 1;
                int mid = lo + half;
                bool goLeft = sLo < mid;
                bool goRight = sHi > mid;
                if (goLeft && goRight)
                {
                    Insert(node.Left ??= new Node(), lo, half, sLo, sHi, pLo, pHi);
                }
                else if (goLeft)
                {
                    node = node.Left ??= new Node();
                    size = half;
                    continue;
                }

                node = node.Right ??= new Node();
                lo = mid;
                size = half;
            }
        }

        /// <summary>Grow the tree's domain, by doubling, until it covers [sLo, sHi).</summary>
        private void Cover(int sLo, int sHi)
        {
            if (_root is null)
            {
                _root = new Node();
                _lo = sLo;
                _size = 1;
                while (_size < sHi - sLo)
                {
                    _size <<= 1;
                }

                return;
            }

            while (sLo < _lo)
            {
                var grown = new Node { Right = _root, Sub = _root.Sub?.Clone() };
                _root = grown;
                _lo -= _size;
                _size <<= 1;
            }

            while (sHi > _lo + _size)
            {
                var grown = new Node { Left = _root, Sub = _root.Sub?.Clone() };
                _root = grown;
                _size <<= 1;
            }
        }

        private sealed class Node
        {
            public Node? Left;
            public Node? Right;
            public IntervalSet? Own;
            public IntervalSet? Sub;
        }
    }

    /// <summary>
    /// A set of integers as sorted, disjoint, non-touching half-open intervals. Never empty once
    /// created.
    /// </summary>
    private sealed class IntervalSet
    {
        private int[] _starts = new int[2];
        private int[] _ends = new int[2];
        private int _count;

        public IntervalSet Clone() => new()
        {
            _starts = (int[])_starts.Clone(),
            _ends = (int[])_ends.Clone(),
            _count = _count,
        };

        /// <summary>Add [start, end), merging with every interval it overlaps or touches.</summary>
        public void Add(int start, int end)
        {
            // i: the first interval ending at or after start; j: the first starting after end.
            int i = FirstEndingAtOrAfter(start);
            int j = i;
            while (j < _count && _starts[j] <= end)
            {
                j++;
            }

            if (i == j)
            {
                if (_count == _starts.Length)
                {
                    Array.Resize(ref _starts, _count * 2);
                    Array.Resize(ref _ends, _count * 2);
                }

                Array.Copy(_starts, i, _starts, i + 1, _count - i);
                Array.Copy(_ends, i, _ends, i + 1, _count - i);
                _starts[i] = start;
                _ends[i] = end;
                _count++;
                return;
            }

            _starts[i] = Math.Min(start, _starts[i]);
            _ends[i] = Math.Max(end, _ends[j - 1]);
            int removed = j - i - 1;
            if (removed > 0)
            {
                Array.Copy(_starts, j, _starts, i + 1, _count - j);
                Array.Copy(_ends, j, _ends, i + 1, _count - j);
                _count -= removed;
            }
        }

        public bool Intersects(int start, int end)
        {
            int i = FirstEndingAfter(start);
            return i < _count && _starts[i] < end;
        }

        /// <summary>The first and last covered values in [start, end), or null.</summary>
        public (int First, int Last)? BoundsWithin(int start, int end)
        {
            int i = FirstEndingAfter(start);
            if (i == _count || _starts[i] >= end)
            {
                return null;
            }

            // j: the last interval starting before end.
            int j = FirstStartingAtOrAfter(end) - 1;
            return (Math.Max(_starts[i], start), Math.Min(_ends[j], end) - 1);
        }

        /// <summary>p when p is not covered, else the end of the interval covering it.</summary>
        public int NextUncovered(int p)
        {
            int i = FirstEndingAfter(p);
            return i < _count && _starts[i] <= p ? _ends[i] : p;
        }

        /// <summary>p when p is not covered, else one before the interval covering it.</summary>
        public int PrevUncovered(int p)
        {
            int i = FirstEndingAfter(p);
            return i < _count && _starts[i] <= p ? _starts[i] - 1 : p;
        }

        private int FirstEndingAfter(int p) => FirstEndingAtOrAfter(p + 1);

        private int FirstEndingAtOrAfter(int p)
        {
            int lo = 0;
            int hi = _count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (_ends[mid] < p)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        private int FirstStartingAtOrAfter(int p)
        {
            int lo = 0;
            int hi = _count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (_starts[mid] < p)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }
}
