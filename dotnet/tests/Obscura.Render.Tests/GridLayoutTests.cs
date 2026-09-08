// xUnit port of the in-file `#[cfg(test)] mod tests` blocks of taffy's CSS Grid
// implementation:
//
//   vendor/taffy/src/compute/grid/alignment.rs
//   vendor/taffy/src/compute/grid/explicit_grid.rs
//   vendor/taffy/src/compute/grid/implicit_grid.rs
//   vendor/taffy/src/compute/grid/placement.rs
//
// (vendor/taffy/src/compute/grid/track_sizing.rs and types/*.rs carry no
// `mod tests` block.)
//
// Tests appear in the same order and with the same names as the Rust originals.
// The helpers from vendor/taffy/src/compute/grid/util/test_helpers.rs are ported
// as the private helpers at the top of each region.
namespace Obscura.Render.Tests;

// These usings sit inside the namespace on purpose: Obscura.Render (the computed
// style port) declares its own Display/BoxSizing/Dimension/Clear/Float types, and a
// compilation-unit-level alias loses to a type declared in an enclosing namespace.
using Obscura.Render.Layout;
using Xunit;
using BoxSizing = Obscura.Render.Layout.BoxSizing;
using Dimension = Obscura.Render.Layout.Dimension;
using Display = Obscura.Render.Layout.Display;

// ---------------------------------------------------------------------------
// Shared helpers (vendor/taffy/src/compute/grid/util/test_helpers.rs)
// ---------------------------------------------------------------------------

internal static class GridTestHelpers
{
    /// <summary>taffy's <c>CreateParentTestNode::into_grid</c>.</summary>
    public static Style IntoGrid(float width, float height, int cols, int rows)
    {
        var style = new Style
        {
            Display = Display.Grid,
            Size = new Size<Dimension>(Dimension.FromLength(width), Dimension.FromLength(height)),
        };

        for (int i = 0; i < cols; i++)
        {
            style.GridTemplateColumns.Add(GridTemplateComponent.FromSingle(TrackSizingFunction.Flex(1f)));
        }

        for (int i = 0; i < rows; i++)
        {
            style.GridTemplateRows.Add(GridTemplateComponent.FromSingle(TrackSizingFunction.Flex(1f)));
        }

        return style;
    }

    /// <summary>taffy's <c>CreateChildTestNode::into_grid_child</c>.</summary>
    public static Style IntoGridChild(
        GridPlacement columnStart,
        GridPlacement columnEnd,
        GridPlacement rowStart,
        GridPlacement rowEnd) => new()
        {
            Display = Display.Grid,
            GridColumn = new Line<GridPlacement>(columnStart, columnEnd),
            GridRow = new Line<GridPlacement>(rowStart, rowEnd),
        };

    public static GridPlacement Line(short index) => GridPlacement.FromLineIndex(index);

    public static GridPlacement Span(ushort span) => GridPlacement.FromSpan(span);

    public static GridPlacement Auto() => GridPlacement.Auto;

    public static GridTemplateComponent TrackLength(float value) =>
        GridTemplateComponent.FromSingle(TrackSizingFunction.FromLength(value));

    public static GridTemplateComponent TrackPercent(float value) =>
        GridTemplateComponent.FromSingle(TrackSizingFunction.FromPercent(value));

    public static GridTemplateComponent TrackFr(float value) =>
        GridTemplateComponent.FromSingle(TrackSizingFunction.Flex(value));

    public static GridTemplateComponent TrackMinMax(MinTrackSizingFunction min, MaxTrackSizingFunction max) =>
        GridTemplateComponent.FromSingle(TrackSizingFunction.MinMax(min, max));

    public static GridTemplateComponent Repeat(RepetitionCount count, params TrackSizingFunction[] tracks) =>
        GridTemplateComponent.FromRepeat(new GridTemplateRepetition { Count = count, Tracks = [.. tracks] });

    public static Size<LengthPercentage> UniformGap(float value) =>
        new(LengthPercentage.FromLength(value), LengthPercentage.FromLength(value));

    public static float NeverCalc(nuint handle, float basis) => 42.42f;

    public static Rect<LengthPercentage> ZeroEdges => new(
        LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero);
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/compute/grid/implicit_grid.rs
// ---------------------------------------------------------------------------

public class GridImplicitGridTests
{
    // mod test_child_min_max_line

    [Fact]
    public void ChildMinMaxLineAuto()
    {
        var (minCol, maxCol, span) = ImplicitGrid.ChildMinLineMaxLineSpan(
            new Line<GridPlacement>(GridTestHelpers.Line(5), GridTestHelpers.Span(6)), 6);
        Assert.Equal(new OriginZeroLine(4), minCol);
        Assert.Equal(new OriginZeroLine(10), maxCol);
        Assert.Equal(1, span);
    }

    [Fact]
    public void ChildMinMaxLineNegativeTrack()
    {
        var (minCol, maxCol, span) = ImplicitGrid.ChildMinLineMaxLineSpan(
            new Line<GridPlacement>(GridTestHelpers.Line(-5), GridTestHelpers.Span(3)), 6);
        Assert.Equal(new OriginZeroLine(2), minCol);
        Assert.Equal(new OriginZeroLine(5), maxCol);
        Assert.Equal(1, span);
    }

    // mod test_initial_grid_sizing

    [Fact]
    public void ExplicitGridSizingWithChildren()
    {
        const ushort explicitColCount = 6;
        const ushort explicitRowCount = 8;
        IGridItemStyle[] childStyles =
        [
            GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(1), GridTestHelpers.Span(2),
                GridTestHelpers.Line(2), GridTestHelpers.Auto()),
            GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-4), GridTestHelpers.Auto(),
                GridTestHelpers.Line(-2), GridTestHelpers.Auto()),
        ];

        var (inline, block) = ImplicitGrid.ComputeGridSizeEstimate(
            explicitColCount, explicitRowCount, Direction.Ltr, childStyles);

        Assert.Equal(0, inline.NegativeImplicit);
        Assert.Equal(explicitColCount, inline.Explicit);
        Assert.Equal(0, inline.PositiveImplicit);
        Assert.Equal(0, block.NegativeImplicit);
        Assert.Equal(explicitRowCount, block.Explicit);
        Assert.Equal(0, block.PositiveImplicit);
    }

    [Fact]
    public void NegativeImplicitGridSizing()
    {
        const ushort explicitColCount = 4;
        const ushort explicitRowCount = 4;
        IGridItemStyle[] childStyles =
        [
            GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-6), GridTestHelpers.Span(2),
                GridTestHelpers.Line(-8), GridTestHelpers.Auto()),
            GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(4), GridTestHelpers.Auto(),
                GridTestHelpers.Line(3), GridTestHelpers.Auto()),
        ];

        var (inline, block) = ImplicitGrid.ComputeGridSizeEstimate(
            explicitColCount, explicitRowCount, Direction.Ltr, childStyles);

        Assert.Equal(1, inline.NegativeImplicit);
        Assert.Equal(explicitColCount, inline.Explicit);
        Assert.Equal(0, inline.PositiveImplicit);
        Assert.Equal(3, block.NegativeImplicit);
        Assert.Equal(explicitRowCount, block.Explicit);
        Assert.Equal(0, block.PositiveImplicit);
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/compute/grid/placement.rs
// ---------------------------------------------------------------------------

public class GridPlacementTests
{
    private static void PlacementTestRunner(
        ushort explicitColCount,
        ushort explicitRowCount,
        List<(int Id, Style Style, (short ColStart, short ColEnd, short RowStart, short RowEnd) Expected)> children,
        TrackCounts expectedColCounts,
        TrackCounts expectedRowCounts,
        GridAutoFlow flow)
    {
        // Setup test
        var childrenIter = new List<(int Index, NodeId Node, IGridItemStyle Style)>();
        var childStylesIter = new List<IGridItemStyle>();
        foreach (var (id, style, _) in children)
        {
            childrenIter.Add((id, NodeId.FromIndex(id), style));
            childStylesIter.Add(style);
        }

        var estimatedSizes = ImplicitGrid.ComputeGridSizeEstimate(
            explicitColCount, explicitRowCount, Direction.Ltr, childStylesIter);
        var items = new List<GridItem>();
        var cellOccupancyMatrix =
            CellOccupancyMatrix.WithTrackCounts(estimatedSizes.Columns, estimatedSizes.Rows);
        var nameResolver = new NamedLineResolver(Style.Default, 0, 0);
        nameResolver.SetExplicitColumnCount(explicitColCount);
        nameResolver.SetExplicitRowCount(explicitRowCount);

        // Run placement algorithm
        GridPlacementAlgorithm.PlaceGridItems(
            cellOccupancyMatrix,
            items,
            childrenIter,
            Direction.Ltr,
            flow,
            AlignItems.Start,
            AlignItems.Start,
            nameResolver);

        // Assert that each item has been placed in the right location
        var sortedChildren = new List<(int Id, Style Style,
            (short ColStart, short ColEnd, short RowStart, short RowEnd) Expected)>(children);
        sortedChildren.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        for (int idx = 0; idx < sortedChildren.Count && idx < items.Count; idx++)
        {
            var (id, _, expected) = sortedChildren[idx];
            var item = items[idx];
            Assert.Equal(NodeId.FromIndex(id), item.Node);
            var actualPlacement = (item.Column.Start, item.Column.End, item.Row.Start, item.Row.End);
            var expectedPlacement = (
                new OriginZeroLine(expected.ColStart),
                new OriginZeroLine(expected.ColEnd),
                new OriginZeroLine(expected.RowStart),
                new OriginZeroLine(expected.RowEnd));
            Assert.True(
                actualPlacement == expectedPlacement,
                $"Item {idx} (0-indexed): expected {expectedPlacement}, got {actualPlacement}");
        }

        Assert.Equal(sortedChildren.Count, items.Count);

        // Assert that the correct number of implicit tracks have been generated
        var actualRowCounts = cellOccupancyMatrix.TrackCountsFor(AbsoluteAxis.Vertical);
        Assert.True(actualRowCounts == expectedRowCounts,
            $"row track counts: expected {expectedRowCounts}, got {actualRowCounts}");
        var actualColCounts = cellOccupancyMatrix.TrackCountsFor(AbsoluteAxis.Horizontal);
        Assert.True(actualColCounts == expectedColCounts,
            $"column track counts: expected {expectedColCounts}, got {actualColCounts}");
    }

    [Fact]
    public void TestOnlyFixedPlacement()
    {
        const GridAutoFlow flow = GridAutoFlow.Row;
        const ushort explicitColCount = 2;
        const ushort explicitRowCount = 2;
        List<(int, Style, (short, short, short, short))> children =
        [
            (1, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(1), GridTestHelpers.Auto(),
                GridTestHelpers.Line(1), GridTestHelpers.Auto()), (0, 1, 0, 1)),
            (2, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-4), GridTestHelpers.Auto(),
                GridTestHelpers.Line(-3), GridTestHelpers.Auto()), (-1, 0, 0, 1)),
            (3, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-3), GridTestHelpers.Auto(),
                GridTestHelpers.Line(-4), GridTestHelpers.Auto()), (0, 1, -1, 0)),
            (4, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(3), GridTestHelpers.Span(2),
                GridTestHelpers.Line(5), GridTestHelpers.Auto()), (2, 4, 4, 5)),
        ];
        var expectedCols = new TrackCounts(1, 2, 2);
        var expectedRows = new TrackCounts(1, 2, 3);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestPlacementSpanningOrigin()
    {
        const GridAutoFlow flow = GridAutoFlow.Row;
        const ushort explicitColCount = 2;
        const ushort explicitRowCount = 2;
        List<(int, Style, (short, short, short, short))> children =
        [
            (1, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-1), GridTestHelpers.Line(-1),
                GridTestHelpers.Line(-1), GridTestHelpers.Line(-1)), (2, 3, 2, 3)),
            (2, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-1), GridTestHelpers.Span(2),
                GridTestHelpers.Line(-1), GridTestHelpers.Span(2)), (2, 4, 2, 4)),
            (3, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-4), GridTestHelpers.Line(-4),
                GridTestHelpers.Line(-4), GridTestHelpers.Line(-4)), (-1, 0, -1, 0)),
            (4, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-4), GridTestHelpers.Span(2),
                GridTestHelpers.Line(-4), GridTestHelpers.Span(2)), (-1, 1, -1, 1)),
        ];
        var expectedCols = new TrackCounts(1, 2, 2);
        var expectedRows = new TrackCounts(1, 2, 2);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestOnlyAutoPlacementRowFlow()
    {
        const GridAutoFlow flow = GridAutoFlow.Row;
        const ushort explicitColCount = 2;
        const ushort explicitRowCount = 2;
        static Style AutoChild() => GridTestHelpers.IntoGridChild(
            GridTestHelpers.Auto(), GridTestHelpers.Auto(),
            GridTestHelpers.Auto(), GridTestHelpers.Auto());
        List<(int, Style, (short, short, short, short))> children =
        [
            (1, AutoChild(), (0, 1, 0, 1)),
            (2, AutoChild(), (1, 2, 0, 1)),
            (3, AutoChild(), (0, 1, 1, 2)),
            (4, AutoChild(), (1, 2, 1, 2)),
            (5, AutoChild(), (0, 1, 2, 3)),
            (6, AutoChild(), (1, 2, 2, 3)),
            (7, AutoChild(), (0, 1, 3, 4)),
            (8, AutoChild(), (1, 2, 3, 4)),
        ];
        var expectedCols = new TrackCounts(0, 2, 0);
        var expectedRows = new TrackCounts(0, 2, 2);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestOnlyAutoPlacementColumnFlow()
    {
        const GridAutoFlow flow = GridAutoFlow.Column;
        const ushort explicitColCount = 2;
        const ushort explicitRowCount = 2;
        static Style AutoChild() => GridTestHelpers.IntoGridChild(
            GridTestHelpers.Auto(), GridTestHelpers.Auto(),
            GridTestHelpers.Auto(), GridTestHelpers.Auto());
        List<(int, Style, (short, short, short, short))> children =
        [
            (1, AutoChild(), (0, 1, 0, 1)),
            (2, AutoChild(), (0, 1, 1, 2)),
            (3, AutoChild(), (1, 2, 0, 1)),
            (4, AutoChild(), (1, 2, 1, 2)),
            (5, AutoChild(), (2, 3, 0, 1)),
            (6, AutoChild(), (2, 3, 1, 2)),
            (7, AutoChild(), (3, 4, 0, 1)),
            (8, AutoChild(), (3, 4, 1, 2)),
        ];
        var expectedCols = new TrackCounts(0, 2, 2);
        var expectedRows = new TrackCounts(0, 2, 0);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestOversizedItem()
    {
        const GridAutoFlow flow = GridAutoFlow.Row;
        const ushort explicitColCount = 2;
        const ushort explicitRowCount = 2;
        List<(int, Style, (short, short, short, short))> children =
        [
            (1, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Span(5), GridTestHelpers.Auto(),
                GridTestHelpers.Auto(), GridTestHelpers.Auto()), (0, 5, 0, 1)),
        ];
        var expectedCols = new TrackCounts(0, 2, 3);
        var expectedRows = new TrackCounts(0, 2, 0);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestFixedInSecondaryAxis()
    {
        const GridAutoFlow flow = GridAutoFlow.Row;
        const ushort explicitColCount = 2;
        const ushort explicitRowCount = 2;
        List<(int, Style, (short, short, short, short))> children =
        [
            (1, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Span(2), GridTestHelpers.Auto(),
                GridTestHelpers.Line(1), GridTestHelpers.Auto()), (0, 2, 0, 1)),
            (2, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Auto(),
                GridTestHelpers.Line(2), GridTestHelpers.Auto()), (0, 1, 1, 2)),
            (3, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Auto(),
                GridTestHelpers.Line(1), GridTestHelpers.Auto()), (2, 3, 0, 1)),
            (4, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Auto(),
                GridTestHelpers.Line(4), GridTestHelpers.Auto()), (0, 1, 3, 4)),
        ];
        var expectedCols = new TrackCounts(0, 2, 1);
        var expectedRows = new TrackCounts(0, 2, 2);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestDefiniteInSecondaryAxisWithFullyDefiniteNegative()
    {
        const GridAutoFlow flow = GridAutoFlow.Row;
        const ushort explicitColCount = 2;
        const ushort explicitRowCount = 2;
        List<(int, Style, (short, short, short, short))> children =
        [
            (2, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Auto(),
                GridTestHelpers.Line(2), GridTestHelpers.Auto()), (0, 1, 1, 2)),
            (1, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-4), GridTestHelpers.Auto(),
                GridTestHelpers.Line(2), GridTestHelpers.Auto()), (-1, 0, 1, 2)),
            (3, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Auto(),
                GridTestHelpers.Line(1), GridTestHelpers.Auto()), (-1, 0, 0, 1)),
        ];
        var expectedCols = new TrackCounts(1, 2, 0);
        var expectedRows = new TrackCounts(0, 2, 0);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestDensePackingAlgorithm()
    {
        const GridAutoFlow flow = GridAutoFlow.RowDense;
        const ushort explicitColCount = 4;
        const ushort explicitRowCount = 4;
        List<(int, Style, (short, short, short, short))> children =
        [
            // Definitely positioned in column 2
            (1, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(2), GridTestHelpers.Auto(),
                GridTestHelpers.Line(1), GridTestHelpers.Auto()), (1, 2, 0, 1)),
            // Spans 2 columns, so positioned after item 1
            (2, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Span(2), GridTestHelpers.Auto(),
                GridTestHelpers.Auto(), GridTestHelpers.Auto()), (2, 4, 0, 1)),
            // Spans 1 column, so should be positioned before item 1
            (3, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Auto(),
                GridTestHelpers.Auto(), GridTestHelpers.Auto()), (0, 1, 0, 1)),
        ];
        var expectedCols = new TrackCounts(0, 4, 0);
        var expectedRows = new TrackCounts(0, 4, 0);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestSparsePackingAlgorithm()
    {
        const GridAutoFlow flow = GridAutoFlow.Row;
        const ushort explicitColCount = 4;
        const ushort explicitRowCount = 4;
        List<(int, Style, (short, short, short, short))> children =
        [
            // Width 3
            (1, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Span(3),
                GridTestHelpers.Auto(), GridTestHelpers.Auto()), (0, 3, 0, 1)),
            // Width 3 (wraps to next row)
            (2, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Span(3),
                GridTestHelpers.Auto(), GridTestHelpers.Auto()), (0, 3, 1, 2)),
            // Width 1 (uses second row as we're already on it)
            (3, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Span(1),
                GridTestHelpers.Auto(), GridTestHelpers.Auto()), (3, 4, 1, 2)),
        ];
        var expectedCols = new TrackCounts(0, 4, 0);
        var expectedRows = new TrackCounts(0, 4, 0);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }

    [Fact]
    public void TestAutoPlacementInNegativeTracks()
    {
        const GridAutoFlow flow = GridAutoFlow.RowDense;
        const ushort explicitColCount = 2;
        const ushort explicitRowCount = 2;
        List<(int, Style, (short, short, short, short))> children =
        [
            // Row 1. Definitely positioned in column -2
            (1, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Line(-5), GridTestHelpers.Auto(),
                GridTestHelpers.Line(1), GridTestHelpers.Auto()), (-2, -1, 0, 1)),
            // Row 2. Auto positioned in column -2
            (2, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Auto(),
                GridTestHelpers.Line(2), GridTestHelpers.Auto()), (-2, -1, 1, 2)),
            // Row 1. Auto positioned in column -1
            (3, GridTestHelpers.IntoGridChild(
                GridTestHelpers.Auto(), GridTestHelpers.Auto(),
                GridTestHelpers.Auto(), GridTestHelpers.Auto()), (-1, 0, 0, 1)),
        ];
        var expectedCols = new TrackCounts(2, 2, 0);
        var expectedRows = new TrackCounts(0, 2, 0);
        PlacementTestRunner(
            explicitColCount, explicitRowCount, children, expectedCols, expectedRows, flow);
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/compute/grid/explicit_grid.rs
// ---------------------------------------------------------------------------

public class GridExplicitGridTests
{
    private static Size<float?> PreferredSize(Style style) =>
        new(style.Size.Width.IntoOption(), style.Size.Height.IntoOption());

    [Fact]
    public void ExplicitGridSizingNoRepeats()
    {
        var gridStyle = GridTestHelpers.IntoGrid(600.0f, 600.0f, 2, 4);
        var preferredSize = PreferredSize(gridStyle);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(2, colCount);
        Assert.Equal(4, rowCount);
        Assert.Equal(0, autoColReps);
        Assert.Equal(0, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingAutoFillExactFit()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            Size = new Size<Dimension>(Dimension.FromLength(120.0f), Dimension.FromLength(80.0f)),
            GridTemplateColumns =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(40.0f))],
            GridTemplateRows =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f))],
        };
        var preferredSize = PreferredSize(gridStyle);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(3, colCount);
        Assert.Equal(4, rowCount);
        Assert.Equal(3, autoColReps);
        Assert.Equal(4, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingAutoFillNonExactFit()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            Size = new Size<Dimension>(Dimension.FromLength(140.0f), Dimension.FromLength(90.0f)),
            GridTemplateColumns =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(40.0f))],
            GridTemplateRows =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f))],
        };
        var preferredSize = PreferredSize(gridStyle);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(3, colCount);
        Assert.Equal(4, rowCount);
        Assert.Equal(3, autoColReps);
        Assert.Equal(4, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingAutoFillMinSizeExactFit()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            MinSize = new Size<Dimension>(Dimension.FromLength(120.0f), Dimension.FromLength(80.0f)),
            GridTemplateColumns =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(40.0f))],
            GridTemplateRows =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f))],
        };
        var innerContainerSize = new Size<float?>(120.0f, 80.0f);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            innerContainerSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MinRepetitionsThatDoOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            innerContainerSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MinRepetitionsThatDoOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(3, colCount);
        Assert.Equal(4, rowCount);
        Assert.Equal(3, autoColReps);
        Assert.Equal(4, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingAutoFillMinSizeNonExactFit()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            MinSize = new Size<Dimension>(Dimension.FromLength(140.0f), Dimension.FromLength(90.0f)),
            GridTemplateColumns =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(40.0f))],
            GridTemplateRows =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f))],
        };
        var innerContainerSize = new Size<float?>(140.0f, 90.0f);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            innerContainerSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MinRepetitionsThatDoOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            innerContainerSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MinRepetitionsThatDoOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(4, colCount);
        Assert.Equal(5, rowCount);
        Assert.Equal(4, autoColReps);
        Assert.Equal(5, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingAutoFillMultipleRepeatedTracks()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            Size = new Size<Dimension>(Dimension.FromLength(140.0f), Dimension.FromLength(100.0f)),
            GridTemplateColumns =
            [
                GridTestHelpers.Repeat(
                    RepetitionCount.AutoFill,
                    TrackSizingFunction.FromLength(40.0f),
                    TrackSizingFunction.FromLength(20.0f)),
            ],
            GridTemplateRows =
            [
                GridTestHelpers.Repeat(
                    RepetitionCount.AutoFill,
                    TrackSizingFunction.FromLength(20.0f),
                    TrackSizingFunction.FromLength(10.0f)),
            ],
        };
        var preferredSize = PreferredSize(gridStyle);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(4, colCount); // 2 repetitions * 2 repeated tracks
        Assert.Equal(6, rowCount); // 3 repetitions * 2 repeated tracks
        Assert.Equal(2, autoColReps);
        Assert.Equal(3, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingAutoFillGap()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            Size = new Size<Dimension>(Dimension.FromLength(140.0f), Dimension.FromLength(100.0f)),
            GridTemplateColumns =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(40.0f))],
            GridTemplateRows =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f))],
            Gap = GridTestHelpers.UniformGap(20.0f),
        };
        var preferredSize = PreferredSize(gridStyle);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(2, colCount); // 2 tracks + 1 gap
        Assert.Equal(3, rowCount); // 3 tracks + 2 gaps
        Assert.Equal(2, autoColReps);
        Assert.Equal(3, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingNoDefinedSize()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            GridTemplateColumns =
            [
                GridTestHelpers.Repeat(
                    RepetitionCount.AutoFill,
                    TrackSizingFunction.FromLength(40.0f),
                    TrackSizingFunction.FromPercent(0.5f),
                    TrackSizingFunction.FromLength(20.0f)),
            ],
            GridTemplateRows =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f))],
            Gap = GridTestHelpers.UniformGap(20.0f),
        };
        var preferredSize = PreferredSize(gridStyle);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MinRepetitionsThatDoOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MinRepetitionsThatDoOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(3, colCount);
        Assert.Equal(1, rowCount);
        Assert.Equal(1, autoColReps);
        Assert.Equal(1, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingMixRepeatedAndNonRepeated()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            Size = new Size<Dimension>(Dimension.FromLength(140.0f), Dimension.FromLength(100.0f)),
            GridTemplateColumns =
            [
                GridTestHelpers.TrackLength(20.0f),
                GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(40.0f)),
            ],
            GridTemplateRows =
            [
                GridTestHelpers.TrackLength(40.0f),
                GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f)),
            ],
            Gap = GridTestHelpers.UniformGap(20.0f),
        };
        var preferredSize = PreferredSize(gridStyle);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            preferredSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(3, colCount); // 3 tracks + 2 gaps
        Assert.Equal(2, rowCount); // 2 tracks + 1 gap
        Assert.Equal(2, autoColReps);
        Assert.Equal(1, autoRowReps);
    }

    [Fact]
    public void ExplicitGridSizingMixWithPadding()
    {
        var gridStyle = new Style
        {
            Display = Display.Grid,
            Size = new Size<Dimension>(Dimension.FromLength(120.0f), Dimension.FromLength(120.0f)),
            Padding = new Rect<LengthPercentage>(
                LengthPercentage.FromLength(10.0f),
                LengthPercentage.FromLength(10.0f),
                LengthPercentage.FromLength(20.0f),
                LengthPercentage.FromLength(20.0f)),
            GridTemplateColumns =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f))],
            GridTemplateRows =
                [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(20.0f))],
        };
        var innerContainerSize = new Size<float?>(100.0f, 80.0f);
        var (autoColReps, colCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            innerContainerSize.GetAbs(AbsoluteAxis.Horizontal),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Horizontal);
        var (autoRowReps, rowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            gridStyle,
            innerContainerSize.GetAbs(AbsoluteAxis.Vertical),
            AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow,
            GridTestHelpers.NeverCalc,
            AbsoluteAxis.Vertical);
        Assert.Equal(5, colCount); // 40px horizontal padding
        Assert.Equal(4, rowCount); // 20px vertical padding
        Assert.Equal(5, autoColReps);
        Assert.Equal(4, autoRowReps);
    }

    [Fact]
    public void TestInitializeGridTracks()
    {
        var minpx0 = MinTrackSizingFunction.FromLength(0.0f);
        var minpx20 = MinTrackSizingFunction.FromLength(20.0f);
        var minpx100 = MinTrackSizingFunction.FromLength(100.0f);

        var maxpx0 = MaxTrackSizingFunction.FromLength(0.0f);
        var maxpx20 = MaxTrackSizingFunction.FromLength(20.0f);
        var maxpx100 = MaxTrackSizingFunction.FromLength(100.0f);

        var gridStyle = new Style
        {
            Display = Display.Grid,
            Gap = GridTestHelpers.UniformGap(20.0f),
            GridTemplateColumns =
            [
                GridTestHelpers.TrackLength(100.0f),
                GridTestHelpers.TrackMinMax(
                    MinTrackSizingFunction.FromLength(100.0f), MaxTrackSizingFunction.FromFr(2.0f)),
                GridTestHelpers.TrackFr(1.0f),
            ],
            GridAutoColumns = [TrackSizingFunction.Auto, TrackSizingFunction.FromLength(100.0f)],
        };
        var trackCounts = new TrackCounts(3, (ushort)gridStyle.GridTemplateColumns.Count, 3);

        var tracks = new List<GridTrack>();
        ExplicitGrid.InitializeGridTracks(
            tracks, trackCounts, gridStyle, AbsoluteAxis.Horizontal, static _ => false);

        (GridTrackKind Kind, MinTrackSizingFunction Min, MaxTrackSizingFunction Max)[] expected =
        [
            // Gutter
            (GridTrackKind.Gutter, minpx0, maxpx0),
            // Negative implicit tracks
            (GridTrackKind.Track, minpx100, maxpx100),
            (GridTrackKind.Gutter, minpx20, maxpx20),
            (GridTrackKind.Track, MinTrackSizingFunction.Auto, MaxTrackSizingFunction.Auto),
            (GridTrackKind.Gutter, minpx20, maxpx20),
            (GridTrackKind.Track, minpx100, maxpx100),
            (GridTrackKind.Gutter, minpx20, maxpx20),
            // Explicit tracks
            (GridTrackKind.Track, minpx100, maxpx100),
            (GridTrackKind.Gutter, minpx20, maxpx20),
            // Note: separate min-max functions
            (GridTrackKind.Track, minpx100, MaxTrackSizingFunction.FromFr(2.0f)),
            (GridTrackKind.Gutter, minpx20, maxpx20),
            // Note: min sizing function of flex sizing functions is AUTO
            (GridTrackKind.Track, MinTrackSizingFunction.Auto, MaxTrackSizingFunction.FromFr(1.0f)),
            (GridTrackKind.Gutter, minpx20, maxpx20),
            // Positive implicit tracks
            (GridTrackKind.Track, MinTrackSizingFunction.Auto, MaxTrackSizingFunction.Auto),
            (GridTrackKind.Gutter, minpx20, maxpx20),
            (GridTrackKind.Track, minpx100, maxpx100),
            (GridTrackKind.Gutter, minpx20, maxpx20),
            (GridTrackKind.Track, MinTrackSizingFunction.Auto, MaxTrackSizingFunction.Auto),
            (GridTrackKind.Gutter, minpx0, maxpx0),
        ];

        Assert.True(tracks.Count == expected.Length, "Number of tracks doesn't match");

        for (int idx = 0; idx < tracks.Count; idx++)
        {
            var actual = tracks[idx];
            var (kind, min, max) = expected[idx];
            Assert.True(actual.Kind == kind, $"Track {idx} (0-based index) kind");
            Assert.True(actual.MinTrackSizingFunction == min, $"Track {idx} (0-based index) min");
            Assert.True(actual.MaxTrackSizingFunction == max, $"Track {idx} (0-based index) max");
        }
    }
}

// ---------------------------------------------------------------------------
// vendor/taffy/src/compute/grid/alignment.rs
// ---------------------------------------------------------------------------

public class GridAlignmentTests
{
    private readonly record struct IntrinsicInlineSize(float Min, float Max);

    private readonly record struct ReplacedIntrinsicSize(float Width, float Height);

    private static Size<float> MeasureIntrinsicChild(
        Size<float?> knownDimensions,
        Size<AvailableSpace> availableSpace,
        NodeId node,
        IntrinsicInlineSize intrinsic,
        Style style)
    {
        float width = knownDimensions.Width ?? availableSpace.Width.Kind switch
        {
            AvailableSpaceKind.MinContent => intrinsic.Min,
            AvailableSpaceKind.MaxContent => intrinsic.Max,
            _ => Sys.F32Max(intrinsic.Min, Sys.F32Min(availableSpace.Width.Unwrap(), intrinsic.Max)),
        };
        return new Size<float>(width, knownDimensions.Height ?? 10.0f);
    }

    private static Size<float> MeasureReplacedChild(
        Size<float?> knownDimensions,
        Size<AvailableSpace> availableSpace,
        NodeId node,
        ReplacedIntrinsicSize intrinsic,
        Style style)
    {
        float ratio = intrinsic.Width / intrinsic.Height;
        if (knownDimensions.Width.HasValue && knownDimensions.Height.HasValue)
        {
            return new Size<float>(knownDimensions.Width.Value, knownDimensions.Height.Value);
        }

        if (knownDimensions.Width.HasValue)
        {
            return new Size<float>(knownDimensions.Width.Value, knownDimensions.Width.Value / ratio);
        }

        if (knownDimensions.Height.HasValue)
        {
            return new Size<float>(knownDimensions.Height.Value * ratio, knownDimensions.Height.Value);
        }

        return new Size<float>(intrinsic.Width, intrinsic.Height);
    }

    private static (Point<float> Location, Size<float> Size) LayoutReplacedGridItem(
        Style itemStyle,
        Style rootStyle)
    {
        var tree = new TaffyTree<ReplacedIntrinsicSize>();
        tree.DisableRounding();
        itemStyle.AspectRatio = 2.0f;
        var item = tree.NewLeafWithContext(itemStyle, new ReplacedIntrinsicSize(100.0f, 50.0f));
        rootStyle.Display = Display.Grid;
        rootStyle.Size = new Size<Dimension>(Dimension.FromLength(300.0f), Dimension.FromLength(200.0f));
        rootStyle.GridTemplateColumns = [GridTestHelpers.TrackLength(300.0f)];
        rootStyle.GridTemplateRows = [GridTestHelpers.TrackLength(200.0f)];
        var root = tree.NewWithChildren(rootStyle, [item]);
        tree.ComputeLayoutWithMeasure(
            root, GeometryExtensions.SizeMaxContent, MeasureReplacedChild);
        var layout = tree.GetLayout(item);
        return (layout.Location, layout.Size);
    }

    private static Rect<LengthPercentageAuto> InlineMargins(bool leftAuto, bool rightAuto) => new(
        leftAuto ? LengthPercentageAuto.Auto : LengthPercentageAuto.Zero,
        rightAuto ? LengthPercentageAuto.Auto : LengthPercentageAuto.Zero,
        LengthPercentageAuto.Zero,
        LengthPercentageAuto.Zero);

    private static float LayoutNestedGridItem(
        IntrinsicInlineSize intrinsic,
        Rect<LengthPercentageAuto> margin,
        AlignItems? justifySelf,
        Dimension minWidth,
        Dimension maxWidth,
        Rect<LengthPercentage> padding,
        Rect<LengthPercentage> border,
        BoxSizing boxSizing)
    {
        var tree = new TaffyTree<IntrinsicInlineSize>();
        tree.DisableRounding();

        var text = tree.NewLeafWithContext(Style.Default, intrinsic);
        var item = tree.NewWithChildren(
            new Style
            {
                Display = Display.Grid,
                GridTemplateColumns = [GridTestHelpers.TrackFr(1.0f)],
                Margin = margin,
                JustifySelf = justifySelf,
                MinSize = new Size<Dimension>(minWidth, Dimension.Auto),
                MaxSize = new Size<Dimension>(maxWidth, Dimension.Auto),
                Padding = padding,
                Border = border,
                BoxSizing = boxSizing,
            },
            [text]);
        var root = tree.NewWithChildren(
            new Style
            {
                Display = Display.Grid,
                Size = new Size<Dimension>(Dimension.FromLength(300.0f), Dimension.Auto),
                GridTemplateColumns = [GridTestHelpers.TrackLength(300.0f)],
            },
            [item]);

        tree.ComputeLayoutWithMeasure(
            root, GeometryExtensions.SizeMaxContent, MeasureIntrinsicChild);
        return tree.GetLayout(item).Size.Width;
    }

    private static float UnconstrainedItemWidth(
        IntrinsicInlineSize intrinsic,
        Rect<LengthPercentageAuto> margin,
        AlignItems? justifySelf) =>
        LayoutNestedGridItem(
            intrinsic,
            margin,
            justifySelf,
            Dimension.Auto,
            Dimension.Auto,
            GridTestHelpers.ZeroEdges,
            GridTestHelpers.ZeroEdges,
            BoxSizing.BorderBox);

    [Fact]
    public void BreakableFitContentHonorsAutoMarginAndSelfAlignmentMatrix()
    {
        var breakable = new IntrinsicInlineSize(100.0f, 600.0f);
        (Rect<LengthPercentageAuto> Margin, AlignItems? JustifySelf)[] cases =
        [
            (InlineMargins(true, true), null),
            (InlineMargins(true, false), null),
            (InlineMargins(false, true), null),
            (InlineMargins(true, true), AlignItems.Stretch),
            (InlineMargins(false, false), AlignItems.Start),
            (InlineMargins(false, false), AlignItems.Center),
            (InlineMargins(false, false), AlignItems.End),
        ];

        foreach (var (margin, justifySelf) in cases)
        {
            Assert.Equal(300.0f, UnconstrainedItemWidth(breakable, margin, justifySelf));
        }
    }

    [Fact]
    public void UnbreakableFitContentPreservesTheMinContentFloor()
    {
        var unbreakable = new IntrinsicInlineSize(600.0f, 600.0f);
        (Rect<LengthPercentageAuto> Margin, AlignItems? JustifySelf)[] cases =
        [
            (InlineMargins(true, true), null),
            (InlineMargins(true, false), null),
            (InlineMargins(false, true), null),
            (InlineMargins(true, true), AlignItems.Stretch),
            (InlineMargins(false, false), AlignItems.Start),
            (InlineMargins(false, false), AlignItems.Center),
            (InlineMargins(false, false), AlignItems.End),
        ];

        foreach (var (margin, justifySelf) in cases)
        {
            Assert.Equal(600.0f, UnconstrainedItemWidth(unbreakable, margin, justifySelf));
        }

        Assert.True(
            UnconstrainedItemWidth(unbreakable, InlineMargins(false, false), null) == 300.0f,
            "stretch remains active when neither inline margin is auto");
    }

    [Fact]
    public void FitContentAppliesAuthorMinMaxAndBoxEdges()
    {
        var breakable = new IntrinsicInlineSize(100.0f, 600.0f);
        var unbreakable = new IntrinsicInlineSize(600.0f, 600.0f);
        var autoMargins = InlineMargins(true, true);
        var zeroEdges = GridTestHelpers.ZeroEdges;

        float authorMin = LayoutNestedGridItem(
            breakable,
            autoMargins,
            null,
            Dimension.FromLength(350.0f),
            Dimension.Auto,
            zeroEdges,
            zeroEdges,
            BoxSizing.BorderBox);
        Assert.Equal(350.0f, authorMin);

        float authorMax = LayoutNestedGridItem(
            unbreakable,
            autoMargins,
            null,
            Dimension.Auto,
            Dimension.FromLength(250.0f),
            zeroEdges,
            zeroEdges,
            BoxSizing.BorderBox);
        Assert.Equal(250.0f, authorMax);

        var edges = new Rect<LengthPercentage>(
            LengthPercentage.FromLength(10.0f),
            LengthPercentage.FromLength(10.0f),
            LengthPercentage.Zero,
            LengthPercentage.Zero);
        float contentBoxMax = LayoutNestedGridItem(
            unbreakable,
            autoMargins,
            null,
            Dimension.Auto,
            Dimension.FromLength(250.0f),
            edges,
            edges,
            BoxSizing.ContentBox);
        Assert.Equal(290.0f, contentBoxMax);
    }

    [Fact]
    public void GridNormalUsesNaturalReplacedSizeButStretchesOrdinaryItems()
    {
        var (_, replaced) = LayoutReplacedGridItem(
            new Style { ItemIsReplaced = true }, Style.Default);
        Assert.Equal(new Size<float>(100.0f, 50.0f), replaced);

        var (_, ordinary) = LayoutReplacedGridItem(Style.Default, Style.Default);
        Assert.Equal(new Size<float>(300.0f, 150.0f), ordinary);
    }

    [Fact]
    public void OrdinaryGridAspectRatioPreservesNormalAlignmentProvenance()
    {
        (AlignItems? JustifySelf, AlignItems? AlignSelf, AlignItems? AlignItems, AlignItems? JustifyItems,
            Size<float> Expected)[] cases =
        [
            (null, null, null, null, new Size<float>(300.0f, 150.0f)),
            (null, AlignItems.Stretch, null, null, new Size<float>(400.0f, 200.0f)),
            (AlignItems.Stretch, null, null, null, new Size<float>(300.0f, 150.0f)),
            (AlignItems.Stretch, AlignItems.Stretch, null, null, new Size<float>(300.0f, 200.0f)),
            (null, AlignItems.Start, null, null, new Size<float>(300.0f, 150.0f)),
            (AlignItems.Start, null, null, null, new Size<float>(400.0f, 200.0f)),
            (null, null, AlignItems.Stretch, null, new Size<float>(400.0f, 200.0f)),
            (null, null, null, AlignItems.Stretch, new Size<float>(300.0f, 150.0f)),
        ];

        foreach (var (justifySelf, alignSelf, alignItems, justifyItems, expected) in cases)
        {
            var (_, actual) = LayoutReplacedGridItem(
                new Style { JustifySelf = justifySelf, AlignSelf = alignSelf },
                new Style { AlignItems = alignItems, JustifyItems = justifyItems });
            Assert.True(
                actual == expected,
                $"justify={justifySelf}, align={alignSelf}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void ReplacedGridNormalAppliesPercentageMinimumsWithoutImplicitStretch()
    {
        var (_, inlineMin) = LayoutReplacedGridItem(
            new Style
            {
                ItemIsReplaced = true,
                MinSize = new Size<Dimension>(Dimension.FromPercent(0.5f), Dimension.Auto),
            },
            Style.Default);
        Assert.Equal(new Size<float>(150.0f, 75.0f), inlineMin);

        var (_, blockMin) = LayoutReplacedGridItem(
            new Style
            {
                ItemIsReplaced = true,
                MinSize = new Size<Dimension>(Dimension.Auto, Dimension.FromPercent(0.5f)),
            },
            Style.Default);
        Assert.Equal(new Size<float>(200.0f, 100.0f), blockMin);
    }

    [Fact]
    public void ReplacedGridExplicitStretchPrecedesAspectRatioTransfer()
    {
        static Style Base() => new() { ItemIsReplaced = true };

        var inlineStretchStyle = Base();
        inlineStretchStyle.JustifySelf = AlignItems.Stretch;
        var (_, inlineStretch) = LayoutReplacedGridItem(inlineStretchStyle, Style.Default);
        Assert.Equal(new Size<float>(300.0f, 150.0f), inlineStretch);

        var blockStretchStyle = Base();
        blockStretchStyle.AlignSelf = AlignItems.Stretch;
        var (_, blockStretch) = LayoutReplacedGridItem(blockStretchStyle, Style.Default);
        Assert.Equal(new Size<float>(400.0f, 200.0f), blockStretch);

        var bothStretchStyle = Base();
        bothStretchStyle.JustifySelf = AlignItems.Stretch;
        bothStretchStyle.AlignSelf = AlignItems.Stretch;
        var (_, bothStretch) = LayoutReplacedGridItem(bothStretchStyle, Style.Default);
        Assert.Equal(new Size<float>(300.0f, 200.0f), bothStretch);

        var definiteInlineStyle = Base();
        definiteInlineStyle.Size = new Size<Dimension>(Dimension.FromLength(120.0f), Dimension.Auto);
        definiteInlineStyle.AlignSelf = AlignItems.Stretch;
        var (_, definiteInline) = LayoutReplacedGridItem(definiteInlineStyle, Style.Default);
        Assert.Equal(new Size<float>(120.0f, 200.0f), definiteInline);

        var definiteBlockStyle = Base();
        definiteBlockStyle.Size = new Size<Dimension>(Dimension.Auto, Dimension.FromLength(80.0f));
        definiteBlockStyle.JustifySelf = AlignItems.Stretch;
        var (_, definiteBlock) = LayoutReplacedGridItem(definiteBlockStyle, Style.Default);
        Assert.Equal(new Size<float>(300.0f, 80.0f), definiteBlock);
    }

    [Fact]
    public void ReplacedGridNormalAndAutoMarginsRemainNatural()
    {
        var (location, size) = LayoutReplacedGridItem(
            new Style
            {
                ItemIsReplaced = true,
                MinSize = new Size<Dimension>(Dimension.FromPercent(0.5f), Dimension.Auto),
                Margin = new Rect<LengthPercentageAuto>(
                    LengthPercentageAuto.Auto,
                    LengthPercentageAuto.Zero,
                    LengthPercentageAuto.Zero,
                    LengthPercentageAuto.Zero),
            },
            new Style { JustifyItems = AlignItems.Normal });
        Assert.Equal(new Size<float>(150.0f, 75.0f), size);
        Assert.Equal(150.0f, location.X);
    }

    [Fact]
    public void OverflowingAutoMarginsOverrideUnsafeSelfAlignment()
    {
        var area = new Line<float>(0.0f, 300.0f);
        const float oversized = 600.0f;
        Line<float?>[] marginCases =
        [
            new(null, 0.0f),
            new(0.0f, null),
            new(null, null),
        ];

        foreach (var alignment in new[] { AlignItems.Center, AlignItems.End })
        {
            foreach (var margin in marginCases)
            {
                var (ltrStart, ltrMargin) = GridAlignment.AlignItemWithinArea(
                    area,
                    alignment,
                    oversized,
                    Position.Relative,
                    new Line<float?>(null, null),
                    margin,
                    0.0f,
                    Direction.Ltr);
                Assert.Equal(0.0f, ltrStart);
                Assert.Equal(new Line<float>(0.0f, 0.0f), ltrMargin);

                var (rtlStart, rtlMargin) = GridAlignment.AlignItemWithinArea(
                    area,
                    alignment,
                    oversized,
                    Position.Relative,
                    new Line<float?>(null, null),
                    margin,
                    0.0f,
                    Direction.Rtl);
                Assert.Equal(-300.0f, rtlStart);
                Assert.Equal(new Line<float>(0.0f, 0.0f), rtlMargin);
            }
        }

        var (absoluteCenter, _) = GridAlignment.AlignItemWithinArea(
            area,
            AlignItems.Center,
            oversized,
            Position.Absolute,
            new Line<float?>(null, null),
            new Line<float?>(null, 0.0f),
            0.0f,
            Direction.Ltr);
        Assert.Equal(-150.0f, absoluteCenter);

        var (absoluteEnd, _) = GridAlignment.AlignItemWithinArea(
            area,
            AlignItems.End,
            oversized,
            Position.Absolute,
            new Line<float?>(null, null),
            new Line<float?>(null, 0.0f),
            0.0f,
            Direction.Ltr);
        Assert.Equal(-300.0f, absoluteEnd);
    }
}

// ---------------------------------------------------------------------------
// End-to-end smoke test: proves GridLayoutDispatch.Compute is wired up and a
// grid layout runs through TaffyTree. Not a port of a Rust test.
// ---------------------------------------------------------------------------

public class GridLayoutDispatchTests
{
    [Fact]
    public void GridLayoutIsRegisteredAndRunsEndToEnd()
    {
        Assert.NotNull(GridLayoutDispatch.Compute);

        var tree = new TaffyTree<object>();
        tree.DisableRounding();

        var childStyles = new Style[4];
        var children = new NodeId[4];
        for (int i = 0; i < 4; i++)
        {
            childStyles[i] = new Style { Display = Display.Block };
            children[i] = tree.NewLeaf(childStyles[i]);
        }

        var root = tree.NewWithChildren(
            new Style
            {
                Display = Display.Grid,
                Size = new Size<Dimension>(Dimension.FromLength(200.0f), Dimension.FromLength(100.0f)),
                GridTemplateColumns = [GridTestHelpers.TrackFr(1.0f), GridTestHelpers.TrackFr(1.0f)],
                GridTemplateRows = [GridTestHelpers.TrackFr(1.0f), GridTestHelpers.TrackFr(1.0f)],
            },
            children);

        tree.ComputeLayout(root, GeometryExtensions.SizeMaxContent);

        Assert.Equal(200.0f, tree.GetLayout(root).Size.Width);
        Assert.Equal(100.0f, tree.GetLayout(root).Size.Height);

        // 2x2 grid of 100x50 cells, laid out row-wise.
        (float X, float Y)[] expected = [(0f, 0f), (100f, 0f), (0f, 50f), (100f, 50f)];
        for (int i = 0; i < 4; i++)
        {
            var layout = tree.GetLayout(children[i]);
            Assert.Equal(100.0f, layout.Size.Width);
            Assert.Equal(50.0f, layout.Size.Height);
            Assert.Equal(expected[i].X, layout.Location.X);
            Assert.Equal(expected[i].Y, layout.Location.Y);
        }

        // The detailed grid info hook is populated.
        var detailed = Assert.IsType<DetailedGridInfo>(tree.GetDetailedLayoutInfo(root));
        Assert.Equal(2, detailed.Columns.ExplicitTracks);
        Assert.Equal(2, detailed.Rows.ExplicitTracks);
        Assert.Equal(4, detailed.Items.Count);
    }

    [Fact]
    public void FixedTracksWithGapOffsetTheSecondColumn()
    {
        var tree = new TaffyTree<object>();
        tree.DisableRounding();
        var a = tree.NewLeaf(new Style { Display = Display.Block });
        var b = tree.NewLeaf(new Style { Display = Display.Block });
        var root = tree.NewWithChildren(
            new Style
            {
                Display = Display.Grid,
                Size = new Size<Dimension>(Dimension.FromLength(220.0f), Dimension.FromLength(50.0f)),
                GridTemplateColumns = [GridTestHelpers.TrackLength(100.0f), GridTestHelpers.TrackLength(100.0f)],
                GridTemplateRows = [GridTestHelpers.TrackLength(50.0f)],
                Gap = GridTestHelpers.UniformGap(20.0f),
            },
            [a, b]);

        tree.ComputeLayout(root, GeometryExtensions.SizeMaxContent);

        Assert.Equal(0.0f, tree.GetLayout(a).Location.X);
        Assert.Equal(100.0f, tree.GetLayout(a).Size.Width);
        Assert.Equal(120.0f, tree.GetLayout(b).Location.X);
        Assert.Equal(100.0f, tree.GetLayout(b).Size.Width);
    }

    [Fact]
    public void AutoFillRepeatGeneratesTracksToFillTheContainer()
    {
        var tree = new TaffyTree<object>();
        tree.DisableRounding();
        var children = new NodeId[4];
        for (int i = 0; i < 4; i++)
        {
            children[i] = tree.NewLeaf(new Style { Display = Display.Block });
        }

        var root = tree.NewWithChildren(
            new Style
            {
                Display = Display.Grid,
                Size = new Size<Dimension>(Dimension.FromLength(200.0f), Dimension.FromLength(40.0f)),
                GridTemplateColumns =
                    [GridTestHelpers.Repeat(RepetitionCount.AutoFill, TrackSizingFunction.FromLength(50.0f))],
                GridTemplateRows = [GridTestHelpers.TrackLength(40.0f)],
            },
            children);

        tree.ComputeLayout(root, GeometryExtensions.SizeMaxContent);

        for (int i = 0; i < 4; i++)
        {
            var layout = tree.GetLayout(children[i]);
            Assert.Equal(50.0f, layout.Size.Width);
            Assert.Equal(i * 50.0f, layout.Location.X);
            Assert.Equal(0.0f, layout.Location.Y);
        }
    }

    [Fact]
    public void SpanningItemCoversTwoColumnsIncludingTheGutter()
    {
        var tree = new TaffyTree<object>();
        tree.DisableRounding();
        var spanning = tree.NewLeaf(new Style
        {
            Display = Display.Block,
            GridColumn = new Line<GridPlacement>(GridPlacement.FromSpan(2), GridPlacement.Auto),
        });
        var trailing = tree.NewLeaf(new Style { Display = Display.Block });
        var root = tree.NewWithChildren(
            new Style
            {
                Display = Display.Grid,
                Size = new Size<Dimension>(Dimension.FromLength(320.0f), Dimension.FromLength(40.0f)),
                GridTemplateColumns =
                [
                    GridTestHelpers.TrackLength(100.0f),
                    GridTestHelpers.TrackLength(100.0f),
                    GridTestHelpers.TrackLength(100.0f),
                ],
                GridTemplateRows = [GridTestHelpers.TrackLength(40.0f)],
                Gap = GridTestHelpers.UniformGap(10.0f),
            },
            [spanning, trailing]);

        tree.ComputeLayout(root, GeometryExtensions.SizeMaxContent);

        // Two 100px tracks plus the 10px gutter between them.
        Assert.Equal(0.0f, tree.GetLayout(spanning).Location.X);
        Assert.Equal(210.0f, tree.GetLayout(spanning).Size.Width);
        Assert.Equal(220.0f, tree.GetLayout(trailing).Location.X);
        Assert.Equal(100.0f, tree.GetLayout(trailing).Size.Width);
    }

    [Fact]
    public void JustifyContentCenterOffsetsTheTrackOrigin()
    {
        var tree = new TaffyTree<object>();
        tree.DisableRounding();
        var a = tree.NewLeaf(new Style { Display = Display.Block });
        var root = tree.NewWithChildren(
            new Style
            {
                Display = Display.Grid,
                Size = new Size<Dimension>(Dimension.FromLength(300.0f), Dimension.FromLength(40.0f)),
                GridTemplateColumns = [GridTestHelpers.TrackLength(100.0f)],
                GridTemplateRows = [GridTestHelpers.TrackLength(40.0f)],
                JustifyContent = AlignContent.Center,
            },
            [a]);

        tree.ComputeLayout(root, GeometryExtensions.SizeMaxContent);

        Assert.Equal(100.0f, tree.GetLayout(a).Location.X);
        Assert.Equal(100.0f, tree.GetLayout(a).Size.Width);
    }

    [Fact]
    public void AutoTracksSizeToTheirContent()
    {
        var tree = new TaffyTree<Size<float>>();
        tree.DisableRounding();
        var a = tree.NewLeafWithContext(
            new Style { Display = Display.Block }, new Size<float>(60.0f, 30.0f));
        var b = tree.NewLeafWithContext(
            new Style { Display = Display.Block }, new Size<float>(25.0f, 30.0f));
        var root = tree.NewWithChildren(
            new Style
            {
                Display = Display.Grid,
                GridTemplateColumns = [TrackAuto(), TrackAuto()],
                GridTemplateRows = [TrackAuto()],
            },
            [a, b]);

        tree.ComputeLayoutWithMeasure(
            root,
            GeometryExtensions.SizeMaxContent,
            static (known, available, node, context, style) => new Size<float>(
                known.Width ?? context.Width, known.Height ?? context.Height));

        Assert.Equal(60.0f, tree.GetLayout(a).Size.Width);
        Assert.Equal(25.0f, tree.GetLayout(b).Size.Width);
        Assert.Equal(60.0f, tree.GetLayout(b).Location.X);
        Assert.Equal(85.0f, tree.GetLayout(root).Size.Width);
        Assert.Equal(30.0f, tree.GetLayout(root).Size.Height);
    }

    private static GridTemplateComponent TrackAuto() =>
        GridTemplateComponent.FromSingle(TrackSizingFunction.Auto);
}
