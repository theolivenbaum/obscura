using Obscura.Dom;
using Xunit;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render.Tests;

/// <summary>
/// Layout of the seven internal-table <c>display</c> values an author can write:
/// <c>table-row</c>, <c>table-row-group</c>, <c>table-header-group</c>,
/// <c>table-footer-group</c>, <c>table-column</c>, <c>table-column-group</c> and
/// <c>table-caption</c>.
/// </summary>
/// <remarks>
/// Every number here was measured on Chromium 141, hand-launched over CDP at a 1280x800
/// viewport, on the fixture the fact builds. Two things about the fixtures are load-bearing:
/// <c>border-spacing: 0</c>, so the UA's 2px default cannot move anything, and the typeface
/// named explicitly. Chromium's unqualified <c>monospace</c> is DejaVu Sans Mono on the host
/// this was measured on while the engine picks its embedded Liberation Mono for it, and the
/// 1233/2048 against 1229/2048 advance is a width difference of its own; naming
/// <c>"Liberation Mono"</c> puts both engines on the same face.
/// <para>
/// What is left is rounding: taffy rounds every box rect to whole pixels where Chromium keeps
/// LayoutUnit sixty-fourths, so a value can be out by about a pixel per independently-rounded
/// box it is made of. <see cref="AssertWidth"/> takes that count. Across the 26 fixtures these
/// facts are built from, 360 measured coordinates, the largest disagreement is 1.97px - the
/// row whose width is two cells each rounded up from 48.02.
/// </para>
/// <para>
/// Before this was implemented these values were recorded for <c>getComputedStyle</c> and
/// ignored by layout, so every row, group and caption below was a full-width block at 600.
/// </para>
/// </remarks>
public class AuthoredTableDisplayLayoutTests
{
    private const string Style = """
        <style>
        html,body{margin:0;padding:0} body{font:16px/18px "Liberation Mono"}
        #p{width:600px;border-spacing:0}
        </style>
        """;

    private static Dictionary<string, Rect> Boxes(string body, params string[] ids)
    {
        DomTree tree = HtmlParsing.ParseHtml(Style + body);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 800f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        Dictionary<string, Rect> boxes = [];
        foreach (string id in ids)
        {
            NodeId node = tree.GetElementById(id)
                ?? throw new InvalidOperationException($"no element #{id}");
            boxes[id] = prepared.Layout.Rects.TryGetValue(node, out Rect rect)
                ? rect
                : throw new InvalidOperationException($"no box for #{id}");
        }

        return boxes;
    }

    /// <summary>
    /// Assert a width against Chromium's, allowing a pixel for each independently-rounded box
    /// the width is made of.
    /// </summary>
    private static void AssertWidth(Rect box, float chromium, float roundedBoxes = 1f)
        => Assert.InRange(box.Width, chromium - roundedBoxes, chromium + roundedBoxes);

    // `Hello world` in 16px Liberation Mono, as Chromium measures it.
    private const float HelloWorld = 105.63f;

    [Fact]
    public void RowInABlockShrinkWrapsInAnAnonymousTable()
    {
        // Chromium 141: the row is 105.63 - the shrink-to-fit width of the anonymous table CSS
        // 2.1 17.2.1 generates around it - not the 600 of the block it sits in.
        var boxes = Boxes(
            """<div id=p><div id=a style="display:table-row">Hello world</div></div>""",
            "p", "a");

        Assert.Equal(600f, boxes["p"].Width, 0.5f);
        AssertWidth(boxes["a"], HelloWorld);
        Assert.Equal(0f, boxes["a"].X, 0.5f);
        Assert.Equal(0f, boxes["a"].Y, 0.5f);
    }

    [Fact]
    public void RowIsTheBandOverItsCellsNotOneOfThem()
    {
        // Chromium 141: two 48.02 cells side by side, and the row is their 96.03 band.
        var boxes = Boxes(
            """
            <div id=p><div id=a style="display:table-row">
            <div id=c1 style="display:table-cell">Hello</div>
            <div id=c2 style="display:table-cell">world</div></div></div>
            """,
            "a", "c1", "c2");

        AssertWidth(boxes["c1"], 48.02f);
        AssertWidth(boxes["c2"], 48.02f);
        Assert.Equal(boxes["c1"].Width, boxes["c2"].Width, 0.5f);
        Assert.Equal(boxes["c1"].Width, boxes["c2"].X, 0.5f);
        Assert.Equal(boxes["c1"].Width + boxes["c2"].Width, boxes["a"].Width, 0.5f);
    }

    [Fact]
    public void RowGroupAndItsRowShareOneBand()
    {
        // Chromium 141: group and row are both 105.63.
        var boxes = Boxes(
            """
            <div id=p><div id=g style="display:table-row-group">
            <div id=a style="display:table-row">Hello world</div></div></div>
            """,
            "g", "a");

        AssertWidth(boxes["g"], HelloWorld);
        Assert.Equal(boxes["g"].Width, boxes["a"].Width, 0.5f);
        Assert.Equal(boxes["g"].Height, boxes["a"].Height, 0.5f);
    }

    [Fact]
    public void NestedRowGroupsShareTheBandOfTheirRow()
    {
        // Chromium 141: both groups and the row are 105.63 - a nested group is not a box of
        // its own inside the outer one.
        var boxes = Boxes(
            """
            <div id=p><div id=g1 style="display:table-row-group">
            <div id=g2 style="display:table-row-group">
            <div id=a style="display:table-row">Hello world</div></div></div></div>
            """,
            "g1", "g2", "a");

        AssertWidth(boxes["g1"], HelloWorld);
        Assert.Equal(boxes["g1"].Width, boxes["g2"].Width, 0.5f);
        Assert.Equal(boxes["g1"].Width, boxes["a"].Width, 0.5f);
    }

    [Fact]
    public void SiblingRowsShareOneTableAndStack()
    {
        // Chromium 141: both rows are 153.63 - the wider row's width, negotiated across the one
        // column they share - and the second sits directly under the first.
        var boxes = Boxes(
            """
            <div id=p><div id=a style="display:table-row">AAAA</div>
            <div id=b style="display:table-row">BBBBBBBBBBBBBBBB</div></div>
            """,
            "a", "b");

        AssertWidth(boxes["a"], 153.63f);
        Assert.Equal(boxes["a"].Width, boxes["b"].Width, 0.5f);
        Assert.Equal(boxes["a"].Height, boxes["b"].Y, 0.5f);
    }

    [Fact]
    public void ColumnsAreNegotiatedAcrossRows()
    {
        // Chromium 141: column one is 38.41 (`AAAA`), column two 76.81 (`DDDDDDDD`), and both
        // rows are the 115.22 sum, although neither row holds both widest cells.
        var boxes = Boxes(
            """
            <div id=p>
            <div id=a style="display:table-row">
            <div id=a1 style="display:table-cell">AAAA</div>
            <div id=a2 style="display:table-cell">B</div></div>
            <div id=b style="display:table-row">
            <div id=b1 style="display:table-cell">C</div>
            <div id=b2 style="display:table-cell">DDDDDDDD</div></div></div>
            """,
            "a", "a1", "a2", "b", "b1", "b2");

        Assert.Equal(boxes["a1"].Width, boxes["b1"].Width, 0.5f);
        Assert.Equal(boxes["a2"].Width, boxes["b2"].Width, 0.5f);
        Assert.Equal(boxes["a"].Width, boxes["b"].Width, 0.5f);
        AssertWidth(boxes["a1"], 38.41f);
        AssertWidth(boxes["a2"], 76.81f);
        AssertWidth(boxes["a"], 115.22f, 2f);
    }

    [Fact]
    public void RowBandSpansEveryColumnNotOnlyItsOwnCells()
    {
        // Chromium 141: the second row's whole content is one anonymous cell in the first of
        // two columns, and the row is still the full 192.03 of the table's columns.
        var boxes = Boxes(
            """
            <div id=p><div id=r1 style="display:table-row">
            <div id=c1 style="display:table-cell">AAAA</div>
            <div id=c2 style="display:table-cell">BBBB</div></div>
            <div id=r2 style="display:table-row">TEXTTEXTTEXTTEXT</div></div>
            """,
            "r1", "r2", "c1");

        AssertWidth(boxes["r1"], 192.03f, 2f);
        Assert.Equal(boxes["r1"].Width, boxes["r2"].Width, 0.5f);
        Assert.True(
            boxes["r2"].Width > boxes["c1"].Width,
            "the band is wider than the widest column it spans");
    }

    [Fact]
    public void HeaderGroupIsHoistedAndFooterGroupPushed()
    {
        // Chromium 141: the header group lays out first and the footer last whatever the source
        // order - here the source order is footer, body, header.
        var boxes = Boxes(
            """
            <div id=p>
            <div id=f style="display:table-footer-group">
            <div id=fr style="display:table-row">FOOT</div></div>
            <div id=b style="display:table-row-group">
            <div id=br style="display:table-row">BODY</div></div>
            <div id=h style="display:table-header-group">
            <div id=hr style="display:table-row">HEAD</div></div></div>
            """,
            "f", "b", "h", "fr", "br", "hr");

        Assert.Equal(0f, boxes["h"].Y, 0.5f);
        Assert.Equal(18f, boxes["b"].Y, 0.5f);
        Assert.Equal(36f, boxes["f"].Y, 0.5f);
        Assert.Equal(boxes["h"].Y, boxes["hr"].Y, 0.5f);
        Assert.Equal(boxes["f"].Y, boxes["fr"].Y, 0.5f);
        Assert.Equal(boxes["b"].Y, boxes["br"].Y, 0.5f);
    }

    [Fact]
    public void LooseCellsUnderARowGroupGetAnAnonymousRow()
    {
        // Chromium 141: the group shrink-wraps its one cell at 48.02.
        var boxes = Boxes(
            """
            <div id=p><div id=g style="display:table-row-group">
            <div id=c style="display:table-cell">Hello</div></div></div>
            """,
            "g", "c");

        AssertWidth(boxes["g"], 48.02f);
        Assert.Equal(boxes["g"].Width, boxes["c"].Width, 0.5f);
    }

    [Fact]
    public void RowGroupWithNoRowsIsOneAnonymousRow()
    {
        // Chromium 141: a `display: table-row-group` holding only text shrink-wraps to 38.41,
        // the width of the anonymous row and cell around it.
        var boxes = Boxes(
            """<div id=p><div id=g style="display:table-row-group">TEXT</div></div>""",
            "g");

        AssertWidth(boxes["g"], 38.41f);
    }

    [Fact]
    public void CaptionSitsAboveTheRowsAndTakesTheTableWidth()
    {
        // Chromium 141: the caption is the table's 105.63 at y=0 and the row follows it.
        var boxes = Boxes(
            """
            <div id=p><div id=a style="display:table-caption">Hello world</div>
            <div id=b style="display:table-row">Hello world</div></div>
            """,
            "a", "b");

        AssertWidth(boxes["a"], HelloWorld);
        Assert.Equal(0f, boxes["a"].Y, 0.5f);
        Assert.Equal(boxes["a"].Height, boxes["b"].Y, 0.5f);
        Assert.Equal(boxes["a"].Width, boxes["b"].Width, 0.5f);
    }

    [Fact]
    public void CaptionSideBottomPutsTheCaptionUnderTheRows()
    {
        // Chromium 141: the row is at y=0 and the 67.22-wide caption below it.
        var boxes = Boxes(
            """
            <div id=p>
            <div id=cap style="display:table-caption;caption-side:bottom">CAP</div>
            <div id=r style="display:table-row">
            <div id=c style="display:table-cell">XXXXXXX</div></div></div>
            """,
            "cap", "r");

        Assert.Equal(0f, boxes["r"].Y, 0.5f);
        Assert.Equal(boxes["r"].Height, boxes["cap"].Y, 0.5f);
        AssertWidth(boxes["cap"], 67.22f);
    }

    [Fact]
    public void WidthOnARowSizesNothing()
    {
        // Chromium 141: `width` does not apply to a row - the box stays at the anonymous
        // table's 105.63 shrink-to-fit width rather than taking the declared 300.
        var boxes = Boxes(
            """<div id=p><div id=a style="display:table-row;width:300px">Hello world</div></div>""",
            "a");

        AssertWidth(boxes["a"], HelloWorld);
    }

    [Fact]
    public void HeightOnARowIsARowMinimum()
    {
        // Chromium 141: 105.63 x 50.
        var boxes = Boxes(
            """<div id=p><div id=a style="display:table-row;height:50px">Hello world</div></div>""",
            "a");

        AssertWidth(boxes["a"], HelloWorld);
        Assert.Equal(50f, boxes["a"].Height, 0.5f);
    }

    [Fact]
    public void MarginPaddingAndBorderOnARowAreIgnored()
    {
        // Chromium 141: identical to the same row with none of the three - 105.63 x 18 at the
        // parent's origin - because none of them applies to an internal table box.
        var boxes = Boxes(
            """
            <div id=p><div id=a
            style="display:table-row;padding:10px;margin:10px;border:3px solid">Hello world</div></div>
            """,
            "a");

        AssertWidth(boxes["a"], HelloWorld);
        Assert.Equal(0f, boxes["a"].X, 0.5f);
        Assert.Equal(0f, boxes["a"].Y, 0.5f);
        Assert.Equal(18f, boxes["a"].Height, 0.5f);
    }

    [Fact]
    public void BorderSpacingOffsetsTheRowBand()
    {
        // Chromium 141 with `border-spacing: 8px`: the first row starts at 8,8 and is 84.81
        // wide - two 38.41 columns plus the 8 between them, not the outer spacing.
        var boxes = Boxes(
            """
            <div id=p style="border-spacing:8px">
            <div id=r style="display:table-row">
            <div id=c1 style="display:table-cell">AAAA</div>
            <div id=c2 style="display:table-cell">BBBB</div></div></div>
            """,
            "r", "c1", "c2");

        Assert.Equal(8f, boxes["r"].X, 0.5f);
        Assert.Equal(8f, boxes["r"].Y, 0.5f);
        AssertWidth(boxes["r"], 84.81f, 2f);
        Assert.Equal(8f, boxes["c1"].X, 0.5f);
    }

    [Fact]
    public void ColumnOutsideATableGeneratesNoBox()
    {
        // Chromium 141: 0 x 0, and none of the element's content is rendered - a column box has
        // no content of its own.
        var boxes = Boxes(
            """<div id=p><div id=a style="display:table-column">Hello world</div></div>""",
            "p", "a");

        Assert.Equal(0f, boxes["a"].Width, 0.01f);
        Assert.Equal(0f, boxes["a"].Height, 0.01f);
        Assert.Equal(0f, boxes["p"].Height, 0.01f);
    }

    [Fact]
    public void ColumnWidthSizesItsTrackAndTheColumnBandsIt()
    {
        // Chromium 141: the column is 0,0 200x18 - its track and the table's rows - the first
        // cell takes the declared 200 and the second shrink-wraps `W` at 9.61.
        var boxes = Boxes(
            """
            <div id=p><div id=col style="display:table-column;width:200px"></div>
            <div id=a style="display:table-row">
            <div id=c1 style="display:table-cell">H</div>
            <div id=c2 style="display:table-cell">W</div></div></div>
            """,
            "col", "a", "c1", "c2");

        Assert.Equal(200f, boxes["c1"].Width, 0.5f);
        Assert.Equal(200f, boxes["col"].Width, 0.5f);
        Assert.Equal(0f, boxes["col"].X, 0.5f);
        Assert.Equal(boxes["a"].Height, boxes["col"].Height, 0.5f);
        AssertWidth(boxes["c2"], 9.61f);
    }

    [Fact]
    public void ColumnGroupBandsEveryColumnItHolds()
    {
        // Chromium 141 on a 600px `display: table`: the group spans both columns at 600, the
        // first column is its declared 200 and the second the remaining 400.
        var boxes = Boxes(
            """
            <div id=p style="display:table">
            <div id=cg style="display:table-column-group">
            <div id=col1 style="display:table-column;width:200px"></div>
            <div id=col2 style="display:table-column"></div></div>
            <div id=a style="display:table-row">
            <div id=c1 style="display:table-cell">A</div>
            <div id=c2 style="display:table-cell">B</div></div></div>
            """,
            "cg", "col1", "col2", "c1", "c2");

        Assert.Equal(600f, boxes["cg"].Width, 0.5f);
        Assert.Equal(200f, boxes["col1"].Width, 0.5f);
        Assert.Equal(400f, boxes["col2"].Width, 0.5f);
        Assert.Equal(200f, boxes["col2"].X, 0.5f);
        Assert.Equal(200f, boxes["c1"].Width, 0.5f);
        Assert.Equal(400f, boxes["c2"].Width, 0.5f);
    }

    [Fact]
    public void SpanAttributeOnACssColumnIsIgnored()
    {
        // Chromium 141: `span` is an HTML attribute on `<col>`, so the div column sizes only
        // its own track - 150 for the first cell, 450 for the second, not 300 and 300.
        var boxes = Boxes(
            """
            <div id=p style="display:table">
            <div id=col style="display:table-column;width:150px" span=2></div>
            <div id=a style="display:table-row">
            <div id=c1 style="display:table-cell">A</div>
            <div id=c2 style="display:table-cell">B</div></div></div>
            """,
            "col", "c1", "c2");

        Assert.Equal(150f, boxes["col"].Width, 0.5f);
        Assert.Equal(150f, boxes["c1"].Width, 0.5f);
        Assert.Equal(450f, boxes["c2"].Width, 0.5f);
    }

    [Fact]
    public void DisplayTableOverAuthoredRowsShrinkWraps()
    {
        // Chromium 141: an auto-width `display: table` div holding one `display: table-row` is
        // 105.63, and so is the row.
        var boxes = Boxes(
            """
            <div id=q style="display:table;border-spacing:0">
            <div id=a style="display:table-row">Hello world</div></div>
            """,
            "q", "a");

        AssertWidth(boxes["q"], HelloWorld);
        Assert.Equal(boxes["q"].Width, boxes["a"].Width, 0.5f);
    }

    [Fact]
    public void InlineTableOverAuthoredRowsShrinkWraps()
    {
        // Chromium 141: the same under `display: inline-table`.
        var boxes = Boxes(
            """
            <div id=q style="display:inline-table;border-spacing:0">
            <div id=a style="display:table-row">Hello world</div></div>
            """,
            "q", "a");

        AssertWidth(boxes["q"], HelloWorld);
        Assert.Equal(boxes["q"].Width, boxes["a"].Width, 0.5f);
    }

    [Fact]
    public void RowInAnInlineGeneratesAnAnonymousInlineTable()
    {
        // Chromium 141: the `display: inline` parent is 105.63 wide, the width of the anonymous
        // inline-table its row generated, not zero.
        var boxes = Boxes(
            """
            <span id=p style="border-spacing:0">
            <div id=a style="display:table-row">Hello world</div></span>
            """,
            "p", "a");

        AssertWidth(boxes["a"], HelloWorld);
        AssertWidth(boxes["p"], HelloWorld);
    }

    [Fact]
    public void RelativePositionOffsetsARowBand()
    {
        // Chromium 141: relative positioning is not blockification, so the box is still a row
        // and still 105.63, and `top: 5px` moves it to y=5.
        var boxes = Boxes(
            """
            <div id=p><div id=r
            style="display:table-row;position:relative;top:5px">Hello world</div></div>
            """,
            "r");

        AssertWidth(boxes["r"], HelloWorld);
        Assert.Equal(5f, boxes["r"].Y, 0.5f);
    }

    [Fact]
    public void BlockifiedRowIsAnOrdinaryShrinkToFitBlock()
    {
        // Chromium 141: a floated `display: table-row` computes to `block`, shrink-to-fits at
        // 105.63 and stays out of flow, so it generates no anonymous table and its 600px parent
        // is 0 tall.
        var boxes = Boxes(
            """<div id=p><div id=a style="display:table-row;float:left">Hello world</div></div>""",
            "p", "a");

        AssertWidth(boxes["a"], HelloWorld);
        Assert.Equal(0f, boxes["p"].Height, 0.5f);
    }

    [Fact]
    public void EachRunOfTableInternalSiblingsGetsItsOwnTable()
    {
        // Chromium 141: the plain block between the two rows splits them into two anonymous
        // tables, so the rows are 38.41 and 153.63 rather than one shared 153.63 column, and
        // the block keeps the parent's full 600 between them.
        var boxes = Boxes(
            """
            <div id=p><div id=a style="display:table-row">AAAA</div>
            <div id=m>MID</div>
            <div id=b style="display:table-row">BBBBBBBBBBBBBBBB</div></div>
            """,
            "a", "m", "b");

        AssertWidth(boxes["a"], 38.41f);
        Assert.Equal(600f, boxes["m"].Width, 0.5f);
        AssertWidth(boxes["b"], 153.63f);
        Assert.Equal(0f, boxes["a"].Y, 0.5f);
        Assert.Equal(18f, boxes["m"].Y, 0.5f);
        Assert.Equal(36f, boxes["b"].Y, 0.5f);
    }

    [Fact]
    public void AStandaloneRowAmongOrdinarySiblingsStillShrinkWraps()
    {
        // Chromium 141: each of these is 38.41 wide whatever surrounds it. The parent cannot
        // generate one table over all of its children, so each table-internal box is its own
        // run and generates its own.
        var boxes = Boxes(
            """
            <div id=p><div id=x>AAAA</div>
            <div id=r style="display:table-row">AAAA</div>
            <div id=g style="display:table-row-group">AAAA</div>
            <div id=c style="display:table-cell">AAAA</div></div>
            """,
            "x", "r", "g", "c");

        Assert.Equal(600f, boxes["x"].Width, 0.5f);
        AssertWidth(boxes["r"], 38.41f);
        AssertWidth(boxes["g"], 38.41f);
        AssertWidth(boxes["c"], 38.41f);
    }

    [Fact]
    public void ARunWhoseTableCannotBeBuiltKeepsEveryBox()
    {
        // A row mixing a cell with loose text needs an anonymous cell, which this engine does
        // not generate, so the run's table cannot be built and every member falls back to an
        // ordinary box. This pins that they all still get one: the run's later members are
        // dropped only when the run's first member really did build the table that holds them.
        var boxes = Boxes(
            """
            <div id=p><div id=x>x</div>
            <div id=r1 style="display:table-row">
            <div id=c style="display:table-cell">AAAA</div>MID</div>
            <div id=r2 style="display:table-row">BBBB</div></div>
            """,
            "x", "r1", "c", "r2");

        Assert.True(boxes["r1"].Height > 0f, "the first row of the run has a box");
        Assert.True(boxes["c"].Height > 0f, "its cell has a box");
        Assert.True(boxes["r2"].Height > 0f, "the second row of the run has a box");
        Assert.True(boxes["r2"].Y >= boxes["r1"].Y + boxes["r1"].Height, "and follows the first");
    }
}
