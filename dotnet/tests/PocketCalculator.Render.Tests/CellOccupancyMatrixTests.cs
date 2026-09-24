// The sparse CellOccupancyMatrix against a dense one, cell for cell. taffy's matrix
// (vendor/taffy/src/compute/grid/types/cell_occupancy.rs) stores a state per cell; the port
// keeps the placed areas instead (see "Known deviations" in todo.md), and every query must
// still give taffy's answer. DenseOracle below is taffy's matrix, queried naively.
namespace PocketCalculator.Render.Tests;

using PocketCalculator.Render.Layout;
using Xunit;

public class CellOccupancyMatrixTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void MatchesADenseMatrixOnRandomInput(int seed)
    {
        var random = new Random(seed);
        for (int round = 0; round < 300; round++)
        {
            RunRandomSequence(random);
        }
    }

    [Fact]
    public void AGridFillingAreaCostsNoCells()
    {
        var matrix = CellOccupancyMatrix.WithTrackCounts(new TrackCounts(0, 0, 0), new TrackCounts(0, 0, 0));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var whole = new Line<OriginZeroLine>(new OriginZeroLine(0), new OriginZeroLine(10_000_000));
        matrix.MarkAreaAs(AbsoluteAxis.Horizontal, whole, whole, CellOccupancyState.DefinitelyPlaced);
        var next = matrix.NextFreePrimaryTrack(
            AbsoluteAxis.Horizontal,
            new OriginZeroLine(0),
            new Line<OriginZeroLine>(new OriginZeroLine(5), new OriginZeroLine(6)),
            reversed: false);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10_000_000, next.Value);
        Assert.Equal(10_000_000, matrix.TrackCountsFor(AbsoluteAxis.Vertical).Len());
        Assert.True(allocated < 64 * 1024, $"allocated {allocated} bytes");
    }

    private static void RunRandomSequence(Random random)
    {
        var columns = new TrackCounts(random.Next(3), (ushort)random.Next(5), random.Next(3));
        var rows = new TrackCounts(random.Next(3), (ushort)random.Next(5), random.Next(3));
        var sparse = CellOccupancyMatrix.WithTrackCounts(columns, rows);
        var dense = new DenseOracle(columns, rows);
        var primary = random.Next(2) == 0 ? AbsoluteAxis.Horizontal : AbsoluteAxis.Vertical;
        int definite = random.Next(6);
        int auto = random.Next(10);

        for (int i = 0; i < definite + auto; i++)
        {
            var state = i < definite ? CellOccupancyState.DefinitelyPlaced : CellOccupancyState.AutoPlaced;
            var p = RandomSpan(random);
            var s = RandomSpan(random);
            sparse.MarkAreaAs(primary, p, s, state);
            dense.MarkAreaAs(primary, p, s, state);
            CompareAll(random, sparse, dense, primary);
            if (random.Next(4) == 0)
            {
                CompareAll(random, sparse, dense, primary.OtherAxis());
            }
        }
    }

    private static Line<OriginZeroLine> RandomSpan(Random random)
    {
        int start = random.Next(-6, 9);
        return new Line<OriginZeroLine>(new OriginZeroLine(start), new OriginZeroLine(start + random.Next(0, 5)));
    }

    private static void CompareAll(Random random, CellOccupancyMatrix sparse, DenseOracle dense, AbsoluteAxis primary)
    {
        Assert.Equal(dense.Columns, sparse.TrackCountsFor(AbsoluteAxis.Horizontal));
        Assert.Equal(dense.Rows, sparse.TrackCountsFor(AbsoluteAxis.Vertical));

        for (int i = -1; i < dense.Columns.Len(); i++)
        {
            if (i >= 0)
            {
                Assert.Equal(dense.ColumnIsOccupied(i), sparse.ColumnIsOccupied(i));
            }
        }

        for (int i = 0; i < dense.Rows.Len(); i++)
        {
            Assert.Equal(dense.RowIsOccupied(i), sparse.RowIsOccupied(i));
        }

        for (int q = 0; q < 12; q++)
        {
            var p = RandomSpan(random);
            var s = RandomSpan(random);
            string where = $"{primary} p={p.Start.Value}..{p.End.Value} s={s.Start.Value}..{s.End.Value}";
            Assert.True(
                dense.LineAreaIsUnoccupied(primary, p, s) == sparse.LineAreaIsUnoccupied(primary, p, s),
                "LineAreaIsUnoccupied " + where);
            Assert.True(
                dense.OccupiedPrimaryTrackBounds(primary, p, s) == sparse.OccupiedPrimaryTrackBounds(primary, p, s),
                "OccupiedPrimaryTrackBounds " + where);
            foreach (bool reversed in new[] { false, true })
            {
                Assert.True(
                    dense.NextFreePrimaryTrack(primary, p.Start, s, reversed)
                    == sparse.NextFreePrimaryTrack(primary, p.Start, s, reversed),
                    $"NextFreePrimaryTrack reversed={reversed} " + where);
            }
        }
    }

    /// <summary>taffy's dense matrix, with each query answered cell by cell.</summary>
    private sealed class DenseOracle(TrackCounts columns, TrackCounts rows)
    {
        private CellOccupancyState[,] _cells = Normalized(rows.Len(), columns.Len());

        public TrackCounts Columns { get; private set; } = columns;

        public TrackCounts Rows { get; private set; } = rows;

        private int RowCount => _cells.GetLength(0);

        private int ColCount => _cells.GetLength(1);

        private static CellOccupancyState[,] Normalized(int rowCount, int colCount) =>
            rowCount == 0 || colCount == 0 ? new CellOccupancyState[0, 0] : new CellOccupancyState[rowCount, colCount];

        private TrackCounts Counts(AbsoluteAxis axis) => axis == AbsoluteAxis.Horizontal ? Columns : Rows;

        private CellOccupancyState Cell(AbsoluteAxis primary, int p, int s) =>
            primary == AbsoluteAxis.Horizontal ? _cells[s, p] : _cells[p, s];

        private int Length(AbsoluteAxis axis) => axis == AbsoluteAxis.Horizontal ? ColCount : RowCount;

        public void MarkAreaAs(
            AbsoluteAxis primary, Line<OriginZeroLine> p, Line<OriginZeroLine> s, CellOccupancyState value)
        {
            var rowSpan = primary == AbsoluteAxis.Horizontal ? s : p;
            var colSpan = primary == AbsoluteAxis.Horizontal ? p : s;
            var colRange = Columns.OzLineRangeToTrackRange(colSpan);
            var rowRange = Rows.OzLineRangeToTrackRange(rowSpan);
            if (colRange.Start < 0 || colRange.End > Columns.Len() || rowRange.Start < 0 || rowRange.End > Rows.Len())
            {
                int negRows = Math.Max(-rowRange.Start, 0);
                int posRows = Math.Max(rowRange.End - Rows.Len(), 0);
                int negCols = Math.Max(-colRange.Start, 0);
                int posCols = Math.Max(colRange.End - Columns.Len(), 0);
                int newRows = Rows.Len() + negRows + posRows;
                int newCols = Columns.Len() + negCols + posCols;

                // grid::Grid: a zero dimension empties both.
                var grown = Normalized(newRows, newCols);
                for (int r = 0; r < RowCount; r++)
                {
                    for (int c = 0; c < ColCount; c++)
                    {
                        grown[r + negRows, c + negCols] = _cells[r, c];
                    }
                }

                _cells = grown;
                Rows = Rows with { NegativeImplicit = Rows.NegativeImplicit + negRows, PositiveImplicit = Rows.PositiveImplicit + posRows };
                Columns = Columns with { NegativeImplicit = Columns.NegativeImplicit + negCols, PositiveImplicit = Columns.PositiveImplicit + posCols };
                colRange = Columns.OzLineRangeToTrackRange(colSpan);
                rowRange = Rows.OzLineRangeToTrackRange(rowSpan);
            }

            for (int r = rowRange.Start; r < rowRange.End; r++)
            {
                for (int c = colRange.Start; c < colRange.End; c++)
                {
                    _cells[r, c] = value;
                }
            }
        }

        public bool LineAreaIsUnoccupied(AbsoluteAxis primary, Line<OriginZeroLine> p, Line<OriginZeroLine> s)
        {
            var pr = Counts(primary).OzLineRangeToTrackRange(p);
            var sr = Counts(primary.OtherAxis()).OzLineRangeToTrackRange(s);
            for (int i = pr.Start; i < pr.End; i++)
            {
                for (int j = sr.Start; j < sr.End; j++)
                {
                    if (i >= 0 && i < Length(primary) && j >= 0 && j < Length(primary.OtherAxis())
                        && Cell(primary, i, j) != CellOccupancyState.Unoccupied)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public (OriginZeroLine First, OriginZeroLine Last)? OccupiedPrimaryTrackBounds(
            AbsoluteAxis primary, Line<OriginZeroLine> p, Line<OriginZeroLine> s)
        {
            var pr = Counts(primary).OzLineRangeToTrackRange(p);
            int first = -1;
            int last = -1;
            for (int i = pr.Start; i < pr.End; i++)
            {
                var one = new Line<OriginZeroLine>(Counts(primary).TrackToPrevOzLine(i), Counts(primary).TrackToPrevOzLine(i + 1));
                if (!LineAreaIsUnoccupied(primary, one, s))
                {
                    first = first < 0 ? i : first;
                    last = i;
                }
            }

            return first < 0 ? null : (Counts(primary).TrackToPrevOzLine(first), Counts(primary).TrackToPrevOzLine(last));
        }

        public OriginZeroLine NextFreePrimaryTrack(
            AbsoluteAxis primary, OriginZeroLine from, Line<OriginZeroLine> s, bool reversed)
        {
            var counts = Counts(primary);
            var sr = Counts(primary.OtherAxis()).OzLineRangeToTrackRange(s);
            if (Math.Min(sr.End, Length(primary.OtherAxis())) <= Math.Max(sr.Start, 0))
            {
                return from;
            }

            int track = counts.OzLineToNextTrack(from);
            while (track >= 0 && track < Length(primary))
            {
                var one = new Line<OriginZeroLine>(counts.TrackToPrevOzLine(track), counts.TrackToPrevOzLine(track + 1));
                if (LineAreaIsUnoccupied(primary, one, s))
                {
                    break;
                }

                track += reversed ? -1 : 1;
            }

            return counts.TrackToPrevOzLine(track);
        }

        public bool RowIsOccupied(int row)
        {
            for (int c = 0; row < RowCount && c < ColCount; c++)
            {
                if (_cells[row, c] != CellOccupancyState.Unoccupied)
                {
                    return true;
                }
            }

            return false;
        }

        public bool ColumnIsOccupied(int column)
        {
            for (int r = 0; column < ColCount && r < RowCount; r++)
            {
                if (_cells[r, column] != CellOccupancyState.Unoccupied)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
