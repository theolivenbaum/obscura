// Grid auto-placement where taffy (vendor/taffy/src/compute/grid/placement.rs and
// implicit_grid.rs) and Chromium disagree; see "Known deviations" in todo.md. Expected rects
// are measured in Chromium 141 and are relative to the grid container.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class GridAutoPlacementTests
{
    private static string[] Place(string gridStyle, params string[] itemStyles)
    {
        string items = string.Concat(itemStyles.Select((s, i) => $"<div id=i{i} style=\"{s}\"></div>"));
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><style>html,body{margin:0}#g{display:grid;grid-auto-rows:10px;"
            + $"grid-auto-columns:10px}}#g>div{{min-width:0;min-height:0}}</style><div id=g style=\"{gridStyle}\">{items}</div>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));
        NodeId Id(string id) => tree.GetElementById(id) ?? throw new InvalidOperationException(id);
        Rect grid = laid.Rects[Id("g")];
        return itemStyles.Select((_, i) =>
        {
            Rect r = laid.Rects[Id($"i{i}")];
            return $"{r.X - grid.X},{r.Y - grid.Y},{r.Width},{r.Height}";
        }).ToArray();
    }

    [Fact]
    public void StepTwoCursorIsPerStartLine()
    {
        // The third item starts in row 2, where no item of this step started yet, so its search
        // starts at column 1 (which is free). taffy started after the last auto-placed cell of
        // row 2, which belongs to the item spanning into it from row 1.
        Assert.Equal(
            ["0,0,10,10", "10,0,10,20", "0,10,10,10"],
            Place("grid-template-columns:repeat(3,10px)", "grid-row:1", "grid-row:1 / span 2", "grid-row:2"));
    }

    [Fact]
    public void StepTwoCursorIsReadInItsOwnAxis()
    {
        // Two negative implicit rows and none in the columns: taffy read the cursor back with
        // the row counts and started the last item one column early.
        Assert.Equal(
            ["0,0,10,40", "10,0,10,10", "10,20,10,10"],
            Place(
                "grid-template-columns:repeat(2,10px);grid-template-rows:repeat(2,10px)",
                "grid-row:-5 / span 4",
                string.Empty,
                "grid-row:auto / 2"));
    }

    [Fact]
    public void AnAutoToLinePlacementCountsIntoTheImplicitGrid()
    {
        // grid-row: auto / 1 occupies the negative implicit row before line 1; taffy's estimate
        // left it out, and the first item was then placed a row late.
        Assert.Equal(
            ["0,0,10,20", "10,0,10,10"],
            Place(
                "grid-auto-flow:column;grid-template-columns:10px;grid-template-rows:repeat(4,10px)",
                "grid-row:span 2",
                "grid-row:auto / 1"));
    }

    [Fact]
    public void RightToLeftPlacesInLogicalColumns()
    {
        // Chromium places an RTL grid in logical columns and mirrors the result. The port
        // searched mirrored columns from the right end of the explicit grid, so the fourth
        // item, auto-placed after the span-2 item in column 3, landed one column left.
        Assert.Equal(
            ["40,40,10,10", "30,0,10,20", "20,20,20,10", "30,30,10,10", "0,0,10,10"],
            Place(
                "grid-auto-flow:column;direction:rtl;width:50px;grid-template-columns:repeat(5,10px);"
                + "grid-template-rows:repeat(3,10px)",
                "grid-row:5 / auto",
                "grid-row:auto / span 2",
                "grid-column:span 2 / auto",
                string.Empty,
                "grid-column:-1 / -2"));
    }

    [Fact]
    public void RightToLeftMirrorsImplicitTracksWithTheGrid()
    {
        // Implicit columns on both sides of the explicit grid, with a two-size
        // grid-auto-columns pattern: the tracks keep their logical sizes and the whole grid is
        // mirrored, so the implicit columns after the explicit grid lie to its left.
        Assert.Equal(
            ["216,0,7,10", "295,10,5,10", "288,10,7,10", "258,10,30,10"],
            Place(
                "direction:rtl;width:300px;grid-template-columns:10px 20px 30px;"
                + "grid-auto-columns:5px 7px;grid-template-rows:10px",
                "grid-column:5",
                "grid-column:-6",
                string.Empty,
                "grid-column:span 2"));
    }

    [Fact]
    public void StepFourWithADefinitePrimaryPositionSkipsOccupiedTracks()
    {
        // Items with a definite column and an auto row search the rows below the cursor; the
        // search jumps over occupied runs instead of probing each row, and finds the same row.
        Assert.Equal(
            ["0,0,20,20", "0,30,10,10", "10,20,10,10", "0,20,10,10", "0,40,20,10", "10,30,10,10", "10,50,10,30"],
            Place(
                "grid-auto-flow:row dense;grid-template-columns:10px 10px",
                "grid-column:1 / 3;grid-row:1 / 3",
                "grid-column:1;grid-row:4",
                "grid-column:2;grid-row:3",
                "grid-column:1",
                "grid-column:1 / span 2",
                "grid-column:2",
                "grid-column:2;grid-row:span 3"));
        Assert.Equal(
            ["0,0,20,30", "20,10,10,10", "30,10,20,20", "40,0,10,10"],
            Place(
                "grid-auto-flow:column;grid-template-rows:10px 10px 10px",
                "grid-row:1 / 4;grid-column:1 / 3",
                "grid-row:2",
                "grid-row:2 / 4;grid-column:span 2",
                "grid-row:1"));
    }
}
