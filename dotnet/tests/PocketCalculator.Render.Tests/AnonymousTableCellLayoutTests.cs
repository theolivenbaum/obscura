using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The anonymous table cell CSS 2.1 17.2.1 generates around a run of children of a table box
/// that are not proper table children, and the anonymous table a <c>table-cell</c> parent
/// generates around table-internal children of its own.
/// </summary>
/// <remarks>
/// Every number here was measured on Chromium 141 through Playwright, on the fixture the fact
/// builds. Two things about the fixtures are load-bearing: <c>border-spacing: 0</c>, so the
/// UA's 2px default cannot move anything, and the typeface named explicitly - Chromium's
/// unqualified <c>monospace</c> is DejaVu Sans Mono on this host (1233/2048 advance) while the
/// engine picks its embedded Liberation Mono (1229/2048), which is a width difference of its
/// own. With the face named, <c>aa</c> is 19.2 wide in both.
/// <para>
/// What is left is rounding: taffy rounds every box rect to whole pixels where Chromium keeps
/// LayoutUnit sixty-fourths, so a value can be out by about a pixel per independently-rounded
/// box it is made of. <see cref="Near"/> takes that count.
/// </para>
/// <para>
/// Before this was implemented <c>BuildTable</c> answered <c>null</c> whenever an anonymous
/// cell would have been needed and the fallback was ordinary block layout, so the table was
/// not a table at all: <c>&lt;div style="display:table"&gt;aa&lt;/div&gt;</c> was 600 wide.
/// </para>
/// </remarks>
public class AnonymousTableCellLayoutTests
{
    private const string Style = """
        <style>
        html,body{margin:0;padding:0} body{font:16px/18px "Liberation Mono"}
        #p{width:600px;border-spacing:0} table{border-spacing:0}
        </style>
        """;

    /// <summary>`aa` in 16px Liberation Mono, as Chromium measures it.</summary>
    private const float Aa = 19.2f;

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
    /// Assert a coordinate against Chromium's, allowing a pixel for each independently-rounded
    /// box it is made of.
    /// </summary>
    private static void Near(float actual, float chromium, float roundedBoxes = 1f)
        => Assert.InRange(actual, chromium - roundedBoxes, chromium + roundedBoxes);

    [Fact]
    public void TableBoxHoldingOnlyTextShrinkWrapsThroughAnAnonymousCell()
    {
        // Chromium 141: 19.2 x 18. The text is wrapped in an anonymous cell inside an
        // anonymous row, and the table shrink-to-fits around it.
        var boxes = Boxes(
            """<div id=p><div id=t style="display:table">aa</div></div>""",
            "t");

        Near(boxes["t"].Width, Aa);
        Near(boxes["t"].Height, 18f);
    }

    [Fact]
    public void TableBoxHoldingOneBlockShrinkWrapsThroughAnAnonymousCell()
    {
        // Chromium 141: the table and the block are both 19.2 x 18 at the origin.
        var boxes = Boxes(
            """<div id=p><div id=t style="display:table"><div id=c>aa</div></div></div>""",
            "t", "c");

        Near(boxes["t"].Width, Aa);
        Near(boxes["c"].Width, Aa);
        Near(boxes["c"].Y, boxes["t"].Y);
    }

    [Fact]
    public void TableBoxHoldingOneInlineShrinkWrapsThroughAnAnonymousCell()
    {
        // Chromium 141: 19.2. An inline child is inline content of the anonymous cell.
        var boxes = Boxes(
            """<div id=p><div id=t style="display:table"><span id=c>aa</span></div></div>""",
            "t");

        Near(boxes["t"].Width, Aa);
    }

    [Fact]
    public void ACellAndALooseBlockShareOneAnonymousRow()
    {
        // Chromium 141: table 38.41 x 18, the cell 19.2 at x=0 and the block 19.2 at x=19.2 -
        // one row of two columns, not two stacked full-width blocks.
        var boxes = Boxes(
            """
            <div id=p><div id=t style="display:table">
            <div id=c1 style="display:table-cell">aa</div><div id=c2>bb</div></div></div>
            """,
            "t", "c1", "c2");

        Near(boxes["t"].Width, 2f * Aa, 2f);
        Near(boxes["t"].Height, 18f);
        Near(boxes["c1"].X, 0f);
        Near(boxes["c2"].X, Aa);
        Near(boxes["c2"].Y, boxes["c1"].Y);
    }

    [Fact]
    public void ALooseBlockBeforeACellIsTheFirstColumn()
    {
        // Chromium 141: the anonymous cell keeps source order - block at x=0, cell at x=19.2.
        var boxes = Boxes(
            """
            <div id=p><div id=t style="display:table">
            <div id=c1>bb</div><div id=c2 style="display:table-cell">aa</div></div></div>
            """,
            "t", "c1", "c2");

        Near(boxes["t"].Width, 2f * Aa, 2f);
        Near(boxes["c1"].X, 0f);
        Near(boxes["c2"].X, Aa);
        Near(boxes["c2"].Y, boxes["c1"].Y);
    }

    [Fact]
    public void ARowMixingACellWithABlockPutsBothInOneRow()
    {
        // Chromium 141: row 38.41 x 18, cell at x=0 and block at x=19.2. Before this the row
        // could not be read at all and the whole table fell back.
        var boxes = Boxes(
            """
            <div id=p><div id=t style="display:table"><div id=r style="display:table-row">
            <div id=c1 style="display:table-cell">aa</div><div id=c2>bb</div></div></div></div>
            """,
            "r", "c1", "c2");

        Near(boxes["r"].Width, 2f * Aa, 2f);
        Near(boxes["r"].Height, 18f);
        Near(boxes["c2"].X, Aa);
        Near(boxes["c2"].Y, boxes["c1"].Y);
    }

    [Fact]
    public void ConsecutiveLooseBlocksBecomeOneAnonymousCell()
    {
        // Chromium 141: 19.2 x 36. One cell holding both blocks, so they stack inside a
        // single column rather than becoming a column each.
        var boxes = Boxes(
            """
            <div id=p><div id=t style="display:table">
            <div id=c1>aa</div><div id=c2>bb</div></div></div>
            """,
            "t", "c1", "c2");

        Near(boxes["t"].Width, Aa);
        Near(boxes["t"].Height, 36f);
        Near(boxes["c2"].Y - boxes["c1"].Y, 18f);
        Near(boxes["c2"].X, boxes["c1"].X);
    }

    [Fact]
    public void AnAnonymousCellTakesTheColumnWidthNegotiatedByTheRowsAboveIt()
    {
        // Chromium 141: table 57.61, first column 38.41 (sized by `aaaa`), the anonymous cell
        // holding `cc` 19.2 at x=38.41 in the second row.
        var boxes = Boxes(
            """
            <div id=p><div id=t style="display:table">
            <div id=r1 style="display:table-row"><div id=c1 style="display:table-cell">aaaa</div></div>
            <div id=r2 style="display:table-row"><div id=c2 style="display:table-cell">bb</div><div id=c3>cc</div></div>
            </div></div>
            """,
            "t", "c1", "c2", "c3");

        Near(boxes["t"].Width, 57.61f, 2f);
        Near(boxes["c1"].Width, 2f * Aa, 2f);
        Near(boxes["c2"].Width, 2f * Aa, 2f);
        Near(boxes["c3"].X, 2f * Aa, 2f);
        Near(boxes["c3"].Width, Aa);
    }

    [Fact]
    public void ANonTableParentDoesNotSwallowItsLooseContentIntoTheAnonymousTable()
    {
        // Chromium 141: the `xx` is a line of the cell's own inline formatting context at
        // y=0, and the two table-cells are a 38.41 x 18 anonymous table below it at y=18.
        // Only a genuine table box wraps loose children in anonymous cells.
        var boxes = Boxes(
            """
            <div id=p><div id=t style="display:table-cell">xx
            <div id=c1 style="display:table-cell">aa</div>
            <div id=c2 style="display:table-cell">bb</div></div></div>
            """,
            "t", "c1", "c2");

        Near(boxes["t"].Width, 2f * Aa, 2f);
        Near(boxes["t"].Height, 36f);
        Near(boxes["c1"].Y, 18f);
        Near(boxes["c2"].Y, boxes["c1"].Y);
        Near(boxes["c2"].X, Aa);
    }

    [Fact]
    public void ACellParentGeneratesAnAnonymousTableForItsCellChildren()
    {
        // Chromium 141: 38.41 x 18 with the two cells side by side. `WantsAnonymousTableBox`
        // used to answer false for any `IsTableCellBox` parent before it looked at the
        // children, so the two cells stacked at 20 wide and 36 tall.
        var boxes = Boxes(
            """
            <div id=p><div id=t style="display:table-cell">
            <div id=c1 style="display:table-cell">aa</div>
            <div id=c2 style="display:table-cell">bb</div></div></div>
            """,
            "t", "c1", "c2");

        Near(boxes["t"].Width, 2f * Aa, 2f);
        Near(boxes["t"].Height, 18f);
        Near(boxes["c1"].X, 0f);
        Near(boxes["c2"].X, Aa);
        Near(boxes["c2"].Y, boxes["c1"].Y);
    }

    [Fact]
    public void ATdGeneratesAnAnonymousTableForItsCellChildren()
    {
        // Chromium 141: the `<td>`'s 1px UA padding, then the two cells side by side at x=1
        // and x=20.2 on the same line. The same arm as the CSS cell parent above: a `<td>`
        // carries `IsTableCellBox` too.
        var boxes = Boxes(
            """
            <div id=p><table id=t><tr><td id=d>
            <div id=c1 style="display:table-cell">aa</div>
            <div id=c2 style="display:table-cell">bb</div></td></tr></table></div>
            """,
            "c1", "c2");

        Near(boxes["c1"].X, 1f);
        Near(boxes["c2"].X, 1f + Aa);
        Near(boxes["c2"].Y, boxes["c1"].Y);
    }

    [Fact]
    public void ACellParentWithRowChildrenStillStacksThem()
    {
        // Regression fence. Chromium 141: 19.2 wide, the two rows stacked 18 apart. This arm
        // was already right and the cell-parent change must not move it.
        var boxes = Boxes(
            """
            <div id=p><div id=t style="display:table-cell">
            <div id=r1 style="display:table-row">aa</div>
            <div id=r2 style="display:table-row">bb</div></div></div>
            """,
            "t", "r1", "r2");

        Near(boxes["t"].Width, Aa);
        Near(boxes["t"].Height, 36f);
        Near(boxes["r2"].Y - boxes["r1"].Y, 18f);
    }
}
