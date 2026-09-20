using Obscura.Dom;
using Xunit;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render.Tests;

/// <summary>
/// Column widths of an auto-layout table whose own width is a percentage.
/// </summary>
/// <remarks>
/// Measured on Chromium 141, hand-launched over CDP at a 1280x720 viewport. A percentage table
/// used to be handed to taffy with `auto` tracks, and the grid filled it by growing them
/// equally; CSS 2.1 17.5.2.2 distributes the width a column does not claim in proportion to
/// max-content, which for a table wider than its content is the same as sizing every column in
/// proportion to max-content. The px-width path already did this, so the two spellings of the
/// same 600px table disagreed with each other.
/// </remarks>
public class PercentageTableColumnTests
{
    private static float Width(string html, string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        return prepared.Layout.Rects.TryGetValue(node, out Rect rect)
            ? rect.Width
            : throw new InvalidOperationException($"no box for #{id}");
    }

    private static float Left(string html, string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        return prepared.Layout.Rects.TryGetValue(node, out Rect rect)
            ? rect.X
            : throw new InvalidOperationException($"no box for #{id}");
    }

    private const string Style = """
        <style>
        html,body{margin:0} .wrap{width:600px}
        table{border-collapse:collapse;font:16px serif} td{padding:0;border:0}
        </style>
        """;

    private static string Table(string tableWidth) => TableIn("", $"width:{tableWidth}");

    /// <summary>The same two-column table, under a wrapper and a table style of the caller's.</summary>
    private static string TableIn(string wrapStyle, string tableStyle) => Style + $"""
        <div class="wrap" style="{wrapStyle}"><table id="t" style="{tableStyle}">
        <tr><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
        """;

    // Chromium 141 on this fixture: min-content 34.66 / 47.09 (81.75 together), max-content
    // 34.66 / 112.84 (147.5 together), so a table wider than its content gives the first column
    // 34.66 / 147.5 = 23.5% of the width. The port measures max-content with HarfBuzz and Skia
    // rather than cosmic-text, so each split lands a pixel or two away; what these assert is the
    // ratio, which is what the `auto` tracks used to get wrong by splitting the unclaimed width
    // equally between the columns.
    private const float MaxContentRatioLow = 0.225f;
    private const float MaxContentRatioHigh = 0.250f;

    private static void AssertMaxContentProportions(string html, float total)
    {
        float first = Width(html, "c1");
        float second = Width(html, "c2");

        Assert.Equal(total, first + second, 1.6f);
        Assert.InRange(first / (first + second), MaxContentRatioLow, MaxContentRatioHigh);
    }

    [Fact]
    public void PercentageAndPixelSpellingsOfTheSameWidthAgree()
    {
        float percentFirst = Width(Table("100%"), "c1");
        float percentSecond = Width(Table("100%"), "c2");
        float pixelFirst = Width(Table("600px"), "c1");
        float pixelSecond = Width(Table("600px"), "c2");

        Assert.Equal(pixelFirst, percentFirst, 1);
        Assert.Equal(pixelSecond, percentSecond, 1);
    }

    [Fact]
    public void ColumnsOfAFullWidthTableAreProportionalToMaxContent()
    {
        float first = Width(Table("100%"), "c1");
        float second = Width(Table("100%"), "c2");

        Assert.Equal(600f, first + second, 1);

        // Chromium: 141 / 459. The port measures max-content with HarfBuzz and Skia rather than
        // cosmic-text, so the split lands a pixel or two away; what is asserted is the ratio,
        // which is what used to be wrong (261 / 339, an equal share of the unclaimed width).
        Assert.InRange(first / (first + second), 0.225f, 0.250f);
    }

    [Fact]
    public void AHalfWidthTableIsHalfAsWideAndKeepsTheSameRatio()
    {
        float first = Width(Table("50%"), "c1");
        float second = Width(Table("50%"), "c2");

        Assert.Equal(300f, first + second, 1);
        Assert.InRange(first / (first + second), 0.225f, 0.250f);
    }

    [Fact]
    public void APercentageNarrowerThanTheContentStillHonoursMinContent()
    {
        // Chromium: the 10% table is 81.8px, its min-content width, not 60px.
        float first = Width(Table("10%"), "c1");
        float second = Width(Table("10%"), "c2");

        Assert.InRange(first + second, 70f, 95f);
    }

    [Fact]
    public void APercentageOverOneHundredStillOverflowsItsContainer()
    {
        // 150% of 600px. Chromium 141: 900px wide, 211.45 / 688.55.
        Assert.InRange(Width(Table("150%"), "t"), 890f, 910f);
        AssertMaxContentProportions(Table("150%"), 900f);
    }

    [Fact]
    public void AFlexParentGetsTheSameColumnsAsABlockParent()
    {
        // Chromium 141: 600 wide, 140.97 / 459.03 - the same as in a plain block.
        AssertMaxContentProportions(TableIn("display:flex", "width:100%"), 600f);
        AssertMaxContentProportions(TableIn("display:grid", "width:100%"), 600f);
        AssertMaxContentProportions(TableIn("display:flex", "width:50%"), 300f);
    }

    [Fact]
    public void AFlexItemIsShrunkBackRatherThanOverflowing()
    {
        // Chromium 141: `width: 150%` in a 600px flex row is 600, not 900 - the item's
        // flex-basis is 900 and flex-shrink takes it back to the line. The distribution then
        // follows the used width, not the percentage.
        Assert.InRange(Width(TableIn("display:flex", "width:150%"), "t"), 595f, 605f);
        AssertMaxContentProportions(TableIn("display:flex", "width:150%"), 600f);
    }

    [Fact]
    public void AFloatedPercentageResolvesAgainstItsContainingBlock()
    {
        // A float shrinks to fit only when its width is `auto`. Chromium 141 gives a floated
        // `width: 100%` table in a 600px block 600px and 140.97 / 459.03; this used to come out
        // at max-content, 147.5, because taffy resolved the percentage against the width the
        // float had shrunk to.
        AssertMaxContentProportions(TableIn("", "width:100%;float:left"), 600f);
        AssertMaxContentProportions(TableIn("", "width:50%;float:left"), 300f);
        AssertMaxContentProportions(TableIn("", "width:150%;float:left"), 900f);
    }

    [Fact]
    public void AFloatedTableWithAutoWidthStillShrinksToFit()
    {
        // Chromium 141: 147.5, its max-content width.
        Assert.InRange(Width(TableIn("", "float:left"), "t"), 140f, 155f);
    }

    [Fact]
    public void MarginsDoNotComeOffThePercentageBase()
    {
        // Chromium 141: `width: 100%; margin: 0 50px` in a 600px block is 600 wide and overflows
        // its container; it is not 500. The margins do come off an `auto` table, which is a
        // different question from what the percentage resolves against.
        Assert.InRange(Width(TableIn("", "width:100%;margin:0 50px"), "t"), 595f, 605f);
        Assert.InRange(Width(TableIn("", "width:50%;margin:0 50px"), "t"), 295f, 305f);
        Assert.InRange(Width(TableIn("", "width:100%;float:left;margin:0 50px"), "t"), 595f, 605f);
    }

    [Fact]
    public void AWrapperOutsideNormalFlowStillSizesItsTable()
    {
        // The base cannot be read off the style chain here, so the table's used width comes from
        // the layout snapshot instead. Chromium 141: 400 wide, 93.97 / 306.03 in all three.
        foreach (string wrapper in new[]
        {
            "float:left;width:400px",
            "display:inline-block;width:400px",
            "position:absolute;width:400px",
        })
        {
            string html = Style + $"""
                <div class="wrap" style="position:relative"><div style="{wrapper}">
                <table id="t" style="width:100%">
                <tr><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div></div>
                """;
            AssertMaxContentProportions(html, 400f);
        }
    }

    [Fact]
    public void AnAbsolutelyPositionedPercentageTableDistributesTheSameWay()
    {
        // Chromium 141: 600 wide, 140.97 / 459.03.
        string html = Style + """
            <div class="wrap" style="position:relative">
            <table id="t" style="width:100%;position:absolute">
            <tr><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
            """;

        AssertMaxContentProportions(html, 600f);
    }

    [Fact]
    public void BelowMaxContentTheColumnsStillInterpolateFromMinContent()
    {
        // CSS 2.1 17.5.2.2 interpolates min-content -> max-content below the max-content sum
        // rather than scaling in proportion to max-content, and these are the cases that tell the
        // two apart. Chromium 141: 34.66 / 85.34 at 120px (proportional would be 28.2 / 91.8)
        // and 34.66 / 65.34 at 100px.
        foreach (string html in new[]
        {
            TableIn("display:flex", "width:20%"),
            TableIn("", "width:20%;float:left"),
            TableIn("", "width:120px"),
        })
        {
            Assert.InRange(Width(html, "c1"), 33f, 36f);
            Assert.InRange(Width(html, "c2"), 84f, 87f);
        }

        Assert.InRange(Width(TableIn("", "width:100px;float:left"), "c1"), 33f, 36f);
        Assert.InRange(Width(TableIn("", "width:100px;float:left"), "c2"), 64f, 67f);
    }

    [Fact]
    public void AFixedWidthColumnKeepsItsWidthInEveryContext()
    {
        // Chromium 141: 200 / 400 in all three - the fixed column takes its declared width and
        // the auto column takes everything the table does not claim.
        foreach (string wrap in new[] { "", "display:flex" })
        {
            string html = Style + $"""
                <div class="wrap" style="{wrap}"><table id="t" style="width:100%">
                <tr><td id="c1" style="width:200px">alpha</td>
                <td id="c2">beta gamma delta</td></tr></table></div>
                """;
            Assert.Equal(200f, Width(html, "c1"), 1f);
            Assert.Equal(400f, Width(html, "c2"), 1f);
        }

        string floated = Style + """
            <div class="wrap"><table id="t" style="width:100%;float:left">
            <tr><td id="c1" style="width:200px">alpha</td>
            <td id="c2">beta gamma delta</td></tr></table></div>
            """;
        Assert.Equal(200f, Width(floated, "c1"), 1f);
        Assert.Equal(400f, Width(floated, "c2"), 1f);
    }

    [Fact]
    public void APercentageColumnOfAFloatedTableTakesItsShare()
    {
        // Chromium 141: 180 / 420, the 30% column measured against the table's 600px used width.
        string html = Style + """
            <div class="wrap"><table id="t" style="width:100%;float:left">
            <tr><td id="c1" style="width:30%">alpha</td>
            <td id="c2">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(180f, Width(html, "c1"), 1f);
        Assert.Equal(420f, Width(html, "c2"), 1f);
    }

    [Fact]
    public void AFloatedFixedLayoutPercentageTableIsAsWideAsItsContainingBlock()
    {
        // Chromium 141: 600 wide, 300 / 300. Taffy resolved the percentage against the width the
        // float shrank to, which for a fixed-layout table came out at 0.
        string html = TableIn("", "width:100%;table-layout:fixed;float:left");

        Assert.InRange(Width(html, "t"), 595f, 605f);
        Assert.Equal(300f, Width(html, "c1"), 1f);
        Assert.Equal(300f, Width(html, "c2"), 1f);
    }

    /// <summary>The same fixture with an authored <c>display</c> on the table and an id'd row.</summary>
    private static string DisplayedTable(string display, string tableWidth) => Style + $"""
        <div class="wrap"><table id="t" style="display:{display};width:{tableWidth}">
        <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
        """;

    [Fact]
    public void ANonTableDisplayStillGeneratesAnAnonymousTableBox()
    {
        // Chromium 141, all four displays alike: the element is 600 wide (its own declared
        // width), and the anonymous table inside it shrink-to-fits to its 147.5 max-content
        // with 34.66 / 112.84 columns. Without the anonymous box the rows laid out in the
        // element's own formatting context and the columns came out 35 / 565.
        foreach (string display in new[] { "block", "inline-block", "flex", "grid" })
        {
            string html = DisplayedTable(display, "100%");

            Assert.Equal(600f, Width(html, "t"), 1f);
            Assert.InRange(Width(html, "r"), 145f, 150f);
            Assert.InRange(Width(html, "c1"), 33f, 36f);
            Assert.InRange(Width(html, "c2"), 111f, 115f);
        }
    }

    [Fact]
    public void AnAnonymousTableShrinksToTheSpaceItsElementDeclares()
    {
        // Chromium 141: at `width: 100px` the anonymous table is clamped to the 100px element
        // and the columns interpolate to 34.66 / 65.34; at `width: auto` an inline-block
        // shrink-to-fits to 147.5 while a block stays 600 and the table inside it is 147.5.
        foreach (string display in new[] { "block", "inline-block", "flex", "grid" })
        {
            string narrow = DisplayedTable(display, "100px");

            Assert.Equal(100f, Width(narrow, "t"), 1f);
            Assert.InRange(Width(narrow, "c1"), 33f, 36f);
            Assert.InRange(Width(narrow, "c2"), 64f, 67f);
        }

        Assert.InRange(Width(DisplayedTable("inline-block", "auto"), "t"), 145f, 150f);
        Assert.Equal(600f, Width(DisplayedTable("block", "auto"), "t"), 1f);
        Assert.InRange(Width(DisplayedTable("block", "auto"), "r"), 145f, 150f);
    }

    [Fact]
    public void AnAnonymousTableIsAlsoGeneratedForAuthoredTableCellChildren()
    {
        // Chromium 141: 34.66 / 112.84 under all three displays - the fixup is about the
        // children being table-internal, not about the element being a `<table>`.
        foreach (string display in new[] { "block", "flex", "inline-block" })
        {
            string html = """
                <style>html,body{margin:0} .wrap{width:600px}
                .cellish{display:table-cell;font:16px serif}</style>
                """ + $"""
                <div class="wrap"><div id="t" style="display:{display};width:100%">
                <div class="cellish" id="c1">alpha</div>
                <div class="cellish" id="c2">beta gamma delta</div></div></div>
                """;

            Assert.Equal(600f, Width(html, "t"), 1f);
            Assert.InRange(Width(html, "c1"), 33f, 36f);
            Assert.InRange(Width(html, "c2"), 111f, 115f);
        }
    }

    [Fact]
    public void AnAnonymousTableKeepsTheElementsBorderPaddingAndInheritedSpacing()
    {
        // Chromium 141: the element's own 5px border and 10px padding stay on the element, so
        // the anonymous table starts at x=15 and is still 147.5 wide.
        string bordered = Style + """
            <div class="wrap"><table id="t" style="display:block;width:100%;border:5px solid;padding:10px">
            <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(600f, Width(bordered, "t"), 1f);
        Assert.Equal(15f, Left(bordered, "c1"), 1f);
        Assert.InRange(Width(bordered, "r"), 145f, 150f);

        // `border-spacing` inherits, so it travels into the anonymous box: Chromium puts the
        // first cell at x=8 and makes the row 155.5 wide.
        string spaced = """
            <style>html,body{margin:0} .wrap{width:600px}
            table{font:16px serif;border-spacing:8px} td{padding:0;border:0}</style>
            <div class="wrap"><table id="t" style="display:block;width:100%">
            <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(8f, Left(spaced, "c1"), 1f);
        Assert.InRange(Width(spaced, "r"), 153f, 158f);
    }

    [Fact]
    public void APercentageCellKeepsItsColumnInsideAFlexParent()
    {
        // Chromium 141: 180 / 420 whether the table's own width is a percentage or a length.
        // `DeferCyclicFlexInlineSizes` rewrites the cell's `width: 30%` to a definite `0px`
        // before the box tree is built, which used to lose the column entirely (43 / 458), and
        // then put the percentage back on the cell's own box, which resolved it a second time
        // against its track (54 instead of 180).
        foreach (string tableWidth in new[] { "100%", "600px" })
        {
            string html = Style + $"""
                <div class="wrap" style="display:flex"><table id="t" style="width:{tableWidth}">
                <tr><td id="c1" style="width:30%">alpha</td>
                <td id="c2">beta gamma delta</td></tr></table></div>
                """;

            Assert.Equal(600f, Width(html, "t"), 1f);
            Assert.Equal(180f, Width(html, "c1"), 1f);
            Assert.Equal(420f, Width(html, "c2"), 1f);
        }
    }

    [Fact]
    public void APresentationalPercentageCellWidthResolvesOnlyOnce()
    {
        // Chromium 141: 180 / 420. The `width` attribute took the same path: the column came
        // out right and the cell box rendered 54, its own 30% of its own 180px track.
        string html = Style + """
            <div class="wrap" style="display:flex"><table id="t" style="width:100%">
            <tr><td id="c1" width="30%">alpha</td>
            <td id="c2">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(180f, Width(html, "c1"), 1f);
        Assert.Equal(420f, Width(html, "c2"), 1f);
    }

    [Fact]
    public void SeveralPercentageColumnsEachTakeTheirShareInAFlexParent()
    {
        // Chromium 141: 120 / 300 / 180 - 20%, 50% and the remainder of the 600px table.
        string html = Style + """
            <div class="wrap" style="display:flex"><table id="t" style="width:100%">
            <tr><td id="c1" style="width:20%">alpha</td>
            <td id="c2" style="width:50%">beta gamma delta</td>
            <td id="c3">eps</td></tr></table></div>
            """;

        Assert.Equal(120f, Width(html, "c1"), 1f);
        Assert.Equal(300f, Width(html, "c2"), 1f);
        Assert.Equal(180f, Width(html, "c3"), 1f);
    }

    /// <summary>The border-collapse fixture: a table whose own border and box-sizing vary.</summary>
    private static string BorderedTable(string tableStyle) => """
        <style>html,body{margin:0} .wrap{width:600px}
        table{font:16px serif} td{padding:0;border:0}</style>
        """ + $"""
        <div class="wrap"><table id="t" style="{tableStyle}">
        <tr><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
        """;

    [Fact]
    public void OnlyTheOuterHalfOfACollapsedBorderIsOutsideAContentBoxWidth()
    {
        // Chromium 141: a collapsed border is centred on the table's edge, so a
        // `box-sizing: content-box; width: 600px` table is 600 + border wide, not 600 + 2*border.
        // Measured at four border widths, and the same for the percentage spelling.
        foreach ((string border, float expected) in new[]
        {
            ("1px", 601f), ("5px", 605f), ("11px", 611f), ("20px", 620f),
        })
        {
            string percent = BorderedTable(
                $"width:100%;box-sizing:content-box;border:{border} solid;border-collapse:collapse");
            string pixels = BorderedTable(
                $"width:600px;box-sizing:content-box;border:{border} solid;border-collapse:collapse");

            Assert.Equal(expected, Width(percent, "t"), 1f);
            Assert.Equal(expected, Width(pixels, "t"), 1f);
        }

        // Chromium 141: a border-box width is unaffected, and the separate model keeps the
        // whole border outside the declaration.
        Assert.Equal(
            600f,
            Width(
                BorderedTable("width:100%;box-sizing:border-box;border:5px solid;border-collapse:collapse"),
                "t"),
            1f);
        Assert.Equal(
            610f,
            Width(
                BorderedTable("width:100%;box-sizing:content-box;border:5px solid;border-collapse:separate"),
                "t"),
            1f);
    }

    [Fact]
    public void ACollapsingTableIgnoresItsOwnPadding()
    {
        // CSS 2.1 17.6.2, and Chromium 141 agrees: 605 either way, and the first cell's content
        // still starts at the border edge rather than 7px in.
        string padded = BorderedTable(
            "width:100%;box-sizing:content-box;border:5px solid;padding:7px;border-collapse:collapse");

        Assert.Equal(605f, Width(padded, "t"), 1f);
        Assert.Equal(5f, Left(padded, "c1"), 1f);
    }

    [Fact]
    public void BorderSpacingIsInsideAContentBoxTableWidth()
    {
        // Chromium 141: `width: 600px; box-sizing: content-box; border: 5px` with the UA's 2px
        // border-spacing is 610 wide - the spacing is already part of the 600 - and the first
        // cell sits 7px in (5 border + 2 spacing).
        string separate = BorderedTable(
            "width:600px;box-sizing:content-box;border:5px solid;border-collapse:separate");

        Assert.Equal(610f, Width(separate, "t"), 1f);
        Assert.Equal(7f, Left(separate, "c1"), 1f);
    }
}
