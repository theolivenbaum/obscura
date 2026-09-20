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
        <tr id="r"><td id="c1"><span id="s1">alpha</span></td>
        <td id="c2">beta gamma delta</td></tr></table></div>
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
        // CSS 2.1 17.6.2, and Chromium 141 agrees: 605 either way, and the first cell still
        // starts at the table's border edge rather than 7px in.
        string padded = BorderedTable(
            "width:100%;box-sizing:content-box;border:5px solid;padding:7px;border-collapse:collapse");

        Assert.Equal(605f, Width(padded, "t"), 1f);

        // Re-measured on Chromium 141 over CDP at 1280x720: the cell's border *box* is at
        // x=2.5, not 5. The collapsed 5px border is centred on the table's edge, so 2.5 of it
        // lies inside the table box and the other 2.5 is the cell's own border-left - which is
        // what puts the cell's *content* at 5. This assertion used to read 5 because the engine
        // gave the table its whole border and the cell none; both numbers below are Chromium's.
        Assert.Equal(2.5f, Left(padded, "c1"), 1f);
        Assert.Equal(5f, Left(padded, "s1"), 1f);
    }

    /// <summary>The two-column fixture with a caption, and a table style of the caller's.</summary>
    private static string Captioned(string tableStyle, string captionStyle = "") => """
        <style>html,body{margin:0} .wrap{width:600px}
        table{font:16px serif} td{padding:0;border:0}</style>
        """ + $"""
        <div class="wrap"><table id="t" style="{tableStyle}">
        <caption id="cap" style="{captionStyle}">cap</caption>
        <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
        """;

    [Fact]
    public void ACaptionIsTheTablesFullWidthAndCountsInItsHeight()
    {
        // Chromium 141 over CDP at 1280x720. A caption is laid out above the table's rows, the
        // width of the table's *border box* (so outside its border and its border-spacing), and
        // its height is part of the table's. There used to be no box for it at all: the element
        // reported no rect and the table was 18 shorter.
        string auto = Captioned(string.Empty);

        Assert.Equal(153.5f, Width(auto, "t"), 1.5f);
        Assert.Equal(153.5f, Width(auto, "cap"), 1.5f);
        Assert.Equal(0f, Left(auto, "cap"), 0.5f);
        Assert.Equal(0f, Top(auto, "cap"), 0.5f);
        Assert.Equal(18f, Height(auto, "cap"), 1f);

        // 18 caption + 2 spacing + 18 row + 2 spacing, and the first row starts after both.
        Assert.Equal(40f, Height(auto, "t"), 1f);
        Assert.Equal(20f, Top(auto, "r"), 1f);
    }

    [Fact]
    public void ACaptionClearsTheTablesBorderAndSpacing()
    {
        // Chromium 141: the caption is still at y=0 and still the full border-box width, and
        // what it clears before the first row is the border plus the leading spacing.
        string separate = Captioned("width:100%;border:5px solid");

        Assert.Equal(600f, Width(separate, "cap"), 1f);
        Assert.Equal(0f, Top(separate, "cap"), 0.5f);
        Assert.Equal(25f, Top(separate, "r"), 1f);
        Assert.Equal(50f, Height(separate, "t"), 1f);

        // Collapsing halves the border and drops the spacing: 18 + 2.5 puts the row at 20.5 and
        // the table at 46.
        string collapsing = Captioned("width:100%;border:5px solid;border-collapse:collapse");

        Assert.Equal(600f, Width(collapsing, "cap"), 1f);
        Assert.Equal(20.5f, Top(collapsing, "r"), 1f);
        Assert.Equal(46f, Height(collapsing, "t"), 1f);
    }

    [Fact]
    public void ABottomCaptionFollowsTheRowsAndSeveralCaptionsStack()
    {
        // Chromium 141: `caption-side: bottom` puts the caption at y=22 with the row still at
        // y=2, and the table is 40 tall either way.
        string bottom = Captioned("width:100%", "caption-side:bottom");

        Assert.Equal(22f, Top(bottom, "cap"), 1f);
        Assert.Equal(2f, Top(bottom, "r"), 1f);
        Assert.Equal(40f, Height(bottom, "t"), 1f);

        // Two captions stack with no spacing between them: 0, 18, then the row at 38.
        string two = """
            <style>html,body{margin:0} .wrap{width:600px}
            table{font:16px serif} td{padding:0;border:0}</style>
            <div class="wrap"><table id="t" style="width:100%">
            <caption id="cap">one</caption><caption id="cap2">two</caption>
            <tr id="r"><td id="c1">alpha</td></tr></table></div>
            """;

        Assert.Equal(0f, Top(two, "cap"), 0.5f);
        Assert.Equal(18f, Top(two, "cap2"), 1f);
        Assert.Equal(38f, Top(two, "r"), 1f);
        Assert.Equal(58f, Height(two, "t"), 1f);
    }

    [Fact]
    public void ACaptionSizesNoColumnAndOnlyFloorsTheTable()
    {
        // Chromium 141 leaves an auto table at what its cells need and wraps the caption inside
        // it: a caption whose max-content is over 200 leaves the table at 67.31. Pinning the
        // caption in the column pass instead made it 196.
        string wide = """
            <style>html,body{margin:0} .wrap{width:600px}
            table{font:16px serif} td{padding:0;border:0}</style>
            <div class="wrap"><table id="t">
            <caption id="cap">a very long caption indeed yes</caption>
            <tr id="r"><td id="c1">alpha</td><td id="c2">beta</td></tr></table></div>
            """;

        Assert.Equal(67.31f, Width(wide, "t"), 1.5f);
        Assert.InRange(Height(wide, "cap"), 70f, 92f);
    }

    /// <summary>The auto-width fixture under a wrapper whose display the caller picks.</summary>
    private static string ItemTable(string wrapStyle, string tableStyle = "") => Style + $"""
        <div class="wrap" style="{wrapStyle}"><table id="t" style="{tableStyle}">
        <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
        """;

    [Fact]
    public void AnAutoTableStretchesToTheItemAreaItsParentGivesIt()
    {
        // Chromium 141 over CDP at 1280x720: a grid item's `auto` inline size stretches, so an
        // auto table in a 600px `display: grid` block is 600 wide with 140.97 / 459.03 columns
        // rather than the 147.5 max-content it used to keep. A flex container stretches its
        // items in the *cross* axis, so the same happens in a column flow and not in a row.
        foreach (string wrapper in new[] { "display:grid", "display:flex;flex-direction:column" })
        {
            string html = ItemTable(wrapper);

            Assert.Equal(600f, Width(html, "t"), 1f);
            Assert.InRange(Width(html, "c1") / 600f, 0.225f, 0.250f);
        }

        // Chromium 141 on the row flow and on a plain block alike: 147.5.
        Assert.Equal(147.5f, Width(ItemTable("display:flex"), "t"), 1.5f);
        Assert.Equal(147.5f, Width(ItemTable(string.Empty), "t"), 1.5f);
    }

    [Fact]
    public void AStretchedTableIsStillTheItemAreaAndStillInsideItsOwnLimits()
    {
        // Chromium 141: the area, not the container - a 200px track gives the table 200
        // (46.98 / 153.02) - and a `max-width` clamps the stretch, which a shrink-to-fit never
        // had to do.
        string track = Style + """
            <div class="wrap" style="display:grid;grid-template-columns:200px 1fr">
            <table id="t"><tr id="r"><td id="c1">alpha</td>
            <td id="c2">beta gamma delta</td></tr></table><div>x</div></div>
            """;

        Assert.Equal(200f, Width(track, "t"), 1f);

        Assert.Equal(200f, Width(ItemTable("display:grid", "max-width:200px"), "t"), 1f);
        Assert.Equal(300f, Width(ItemTable("display:grid", "max-width:300px"), "t"), 1f);

        // Anything that asks for shrink-to-fit keeps it: Chromium 141 gives both of these
        // 147.5, and centres the second one at x=226.25.
        Assert.Equal(147.5f, Width(ItemTable("display:grid", "justify-self:start"), "t"), 1.5f);
        Assert.Equal(147.5f, Width(ItemTable("display:grid", "margin:0 auto"), "t"), 1.5f);
        Assert.Equal(226.25f, Left(ItemTable("display:grid", "margin:0 auto"), "t"), 1.5f);
    }

    [Fact]
    public void AStretchedTableStillGivesAPercentageColumnItsShare()
    {
        // Chromium 141: 180 / 420 - the 30% is measured against the 600 the table stretched to,
        // not against its max-content.
        string html = Style + """
            <div class="wrap" style="display:grid"><table id="t">
            <tr id="r"><td id="c1" style="width:30%">alpha</td>
            <td id="c2">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(600f, Width(html, "t"), 1f);
        Assert.Equal(180f, Width(html, "c1"), 1f);
        Assert.Equal(420f, Width(html, "c2"), 1f);
    }

    /// <summary>The same two-column fixture with an auto-width table and a caller's cell style.</summary>
    private static string AutoTable(string firstCellStyle, string tableStyle = "") => Style + $"""
        <div class="wrap"><table id="t" style="{tableStyle}">
        <tr id="r"><td id="c1" style="{firstCellStyle}">alpha</td>
        <td id="c2">beta gamma delta</td></tr></table></div>
        """;

    [Fact]
    public void AnAutoTableWidensSoAPercentageColumnGetsItsShare()
    {
        // CSS 2.1 17.5.2.2, measured on Chromium 141 over CDP at 1280x720. The auto column's
        // 112.84 max-content has to fit in the share the percentage leaves it, so the table
        // grows past its own max-content (147.5) rather than taking a percentage of it. These
        // came out at 147.5 wide with 44 / 104 columns. The class fixture collapses its
        // borders, so there is no border-spacing in these numbers.
        foreach ((string width, float table, float first, float second) in new[]
        {
            ("30%", 161.2f, 48.36f, 112.84f),
            ("50%", 225.69f, 112.84f, 112.84f),
            ("80%", 564.22f, 451.38f, 112.84f),
        })
        {
            string html = AutoTable($"width:{width}");

            Assert.Equal(table, Width(html, "t"), 1.5f);
            Assert.Equal(first, Width(html, "c1"), 1.5f);
            Assert.Equal(second, Width(html, "c2"), 1.5f);
        }

        // The percentage on the *second* column pulls the first one far past its own
        // max-content: Chromium 141 gives 263.31 / 112.83 in a 376.14 table.
        string trailing = Style + """
            <div class="wrap"><table id="t">
            <tr id="r"><td id="c1">alpha</td>
            <td id="c2" style="width:30%">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(376.14f, Width(trailing, "t"), 1.5f);
        Assert.Equal(263.31f, Width(trailing, "c1"), 1.5f);
        Assert.Equal(112.83f, Width(trailing, "c2"), 1.5f);
    }

    [Fact]
    public void ThePercentageShareComesOffAColgroupAndOffAFixedSibling()
    {
        // Chromium 141: a `<col style="width:30%">` is the same constraint as the cell's own
        // width - 161.2 wide, 48.36 / 112.84.
        string colgroup = Style + """
            <div class="wrap"><table id="t"><colgroup><col style="width:30%"><col></colgroup>
            <tr id="r"><td id="c1">alpha</td>
            <td id="c2">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(161.2f, Width(colgroup, "t"), 1.5f);
        Assert.Equal(48.36f, Width(colgroup, "c1"), 1.5f);

        // And a fixed sibling column contributes its declared width, not its text: Chromium
        // 141 gives 85.7 / 200 in a 285.7 table, because 200 has to fit in the 70%.
        string fixedSibling = Style + """
            <div class="wrap"><table id="t">
            <tr id="r"><td id="c1" style="width:30%">alpha</td>
            <td id="c2" style="width:200px">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(285.7f, Width(fixedSibling, "t"), 1.5f);
        Assert.Equal(85.7f, Width(fixedSibling, "c1"), 1.5f);
        Assert.Equal(200f, Width(fixedSibling, "c2"), 1.5f);

        // Two percentage columns and no auto one: the share that neither claims is split
        // between them in proportion to their percentages. Chromium 141: 84.63 / 141.06.
        string bothPercentage = Style + """
            <div class="wrap"><table id="t">
            <tr id="r"><td id="c1" style="width:30%">alpha</td>
            <td id="c2" style="width:50%">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(84.63f, Width(bothPercentage, "c1"), 1.5f);
        Assert.Equal(141.06f, Width(bothPercentage, "c2"), 1.5f);
    }

    [Fact]
    public void TheShareNeverWidensADefiniteTableOrOverflowsTheContainer()
    {
        // Chromium 141 keeps a definite width and lets the percentage resolve against it: a
        // `width: 120px` table holding a 30% cell stays 120 (36 / 84), and a
        // `width: 300px` one holding an 80% cell stays 300 (240 / 60).
        Assert.Equal(120f, Width(AutoTable("width:30%", "width:120px"), "t"), 1f);
        Assert.Equal(84f, Width(AutoTable("width:30%", "width:120px"), "c2"), 1.5f);
        Assert.Equal(300f, Width(AutoTable("width:80%", "width:300px"), "t"), 1f);
        Assert.Equal(240f, Width(AutoTable("width:80%", "width:300px"), "c1"), 1.5f);

        // A share that cannot be met inside the container stops at the container: Chromium 141
        // makes a `width: 95%` cell's auto table 600, not 2256, and the auto column falls back
        // to its 47.09 min-content.
        Assert.Equal(600f, Width(AutoTable("width:95%"), "t"), 1f);
        Assert.Equal(47.09f, Width(AutoTable("width:95%"), "c2"), 1.5f);
    }

    /// <summary>A collapsing table whose table, cell, row and column borders the caller varies.</summary>
    private static string Collapsing(string tableStyle, string cellStyle = "", string rowStyle = "") =>
        """
        <style>html,body{margin:0} .wrap{width:600px}
        table{font:16px serif;border-collapse:collapse} td{padding:0;border:0}</style>
        """ + $"""
        <div class="wrap"><table id="t" style="{tableStyle}">
        <tr id="r" style="{rowStyle}"><td id="c1" style="{cellStyle}"><span id="s1">alpha</span></td>
        <td id="c2" style="{cellStyle}">beta gamma delta</td></tr></table></div>
        """;

    private static float Top(string html, string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        return prepared.Layout.Rects.TryGetValue(node, out Rect rect)
            ? rect.Y
            : throw new InvalidOperationException($"no box for #{id}");
    }

    private static float Height(string html, string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        return prepared.Layout.Rects.TryGetValue(node, out Rect rect)
            ? rect.Height
            : throw new InvalidOperationException($"no box for #{id}");
    }

    [Fact]
    public void AnEdgeCellTakesTheInnerHalfOfACollapsedBorder()
    {
        // Chromium 141 over CDP at 1280x720, `border: 5px; border-collapse: collapse` on a
        // 600px table: the cells run from x=2.5 to x=597.5, 595 of content, and the first cell
        // is 144.97 wide with its own content at x=5. The engine used to give the table its
        // whole 5px on each edge and the cells none, so they ran 5..595 - 590 of content.
        string collapsing = Collapsing("width:100%;border:5px solid");

        Assert.Equal(600f, Width(collapsing, "t"), 1f);
        Assert.Equal(2.5f, Left(collapsing, "c1"), 1f);
        Assert.Equal(595f, Width(collapsing, "r"), 1f);
        Assert.Equal(595f, Width(collapsing, "c1") + Width(collapsing, "c2"), 1.5f);
        Assert.Equal(5f, Left(collapsing, "s1"), 1f);

        // The block axis is the same split: 2.5 of the table's border on each side of a 23px
        // row whose cell is an 18px line plus its own 2.5 top and bottom.
        Assert.Equal(28f, Height(collapsing, "t"), 1f);
        Assert.Equal(23f, Height(collapsing, "c1"), 1f);
    }

    [Fact]
    public void TheWidestBorderAtAnEdgeWinsAndIsSplitBetweenTheTwoBoxes()
    {
        // Chromium 141, all measured on the same 600px block. A border is resolved per edge
        // (CSS 2.1 17.6.2) and half of it goes to each of the two boxes that meet there, so a
        // wider cell border widens the table's own edge and a wider table border is what an
        // unbordered cell carries.
        foreach ((string table, string cell, float edge) in new[]
        {
            ("border:5px solid", "border:9px solid", 4.5f),
            ("border:5px solid", "border:3px solid", 2.5f),
            ("border:0", "border:4px solid", 2f),
            ("border:7px solid", "border:0", 3.5f),
        })
        {
            string html = Collapsing($"width:100%;{table}", cell);

            Assert.Equal(600f, Width(html, "t"), 1f);
            Assert.Equal(edge, Left(html, "c1"), 1f);
            Assert.Equal(600f - (2f * edge), Width(html, "r"), 1f);
            Assert.Equal(18f + (2f * edge), Height(html, "c1"), 1f);
        }

        // A row's border reaches the table's edge too: Chromium 141 puts the cells of a
        // `border: 6px` row in a borderless table at x=3 and makes the row 24 tall.
        string rowBordered = Collapsing("width:100%;border:0", string.Empty, "border:6px solid");

        Assert.Equal(3f, Left(rowBordered, "c1"), 1f);
        Assert.Equal(594f, Width(rowBordered, "r"), 1f);
        Assert.Equal(24f, Height(rowBordered, "c1"), 1f);
    }

    [Fact]
    public void ACollapsedBorderWidensAContentBoxDeclarationByTheResolvedHalves()
    {
        // Chromium 141: the declaration gains the half of each edge that lies inside the table
        // box, and that edge is the resolved one - a `border: 5px` table holding `border: 9px`
        // cells is 609 wide, not 605.
        Assert.Equal(
            605f,
            Width(Collapsing("width:600px;box-sizing:content-box;border:5px solid"), "t"),
            1f);
        Assert.Equal(
            609f,
            Width(
                Collapsing("width:600px;box-sizing:content-box;border:5px solid", "border:9px solid"),
                "t"),
            1f);
    }

    [Fact]
    public void AnAutoWidthCollapsingTableStillFitsItsMaxContent()
    {
        // A collapsed border puts half a pixel on a cell edge at an odd border width, and the
        // intrinsic widths used to be read from taffy's *rounded* layout - the rounded table
        // total and the rounded per-cell totals then disagreed, the columns came out under
        // their own max-content, and every cell wrapped. Chromium 141 at these five widths:
        // 147.5, 149.5, 151.5, 155.5, 157.5, each one line tall.
        foreach ((string border, float width, float thickness) in new[]
        {
            ("0", 147.5f, 0f),
            ("1px", 149.5f, 1f),
            ("2px", 151.5f, 2f),
            ("4px", 155.5f, 4f),
            ("5px", 157.5f, 5f),
        })
        {
            string html = Collapsing($"border:{border} solid");

            Assert.Equal(width, Width(html, "t"), 1f);

            // One 18px line, plus half the border on each of the table's two edges and half
            // again on each of the cell's. Two lines would mean the column came out under its
            // own max-content.
            Assert.Equal(18f + (2f * thickness), Height(html, "t"), 1f);
        }
    }

    [Fact]
    public void ACellsOwnBorderContributesToItsRowHeight()
    {
        // Independent of the collapsing model, and wrong in the separate model too: Chromium
        // 141 makes a `border-collapse: separate; border: 9px` cell holding one 18px line 36
        // tall. A cell's border contributed no row height at all, because taffy reports a
        // leaf's `content_size` with its padding but without its border.
        string html = """
            <style>html,body{margin:0} .wrap{width:600px}
            table{font:16px serif;border-collapse:separate;border-spacing:0} td{padding:0}</style>
            <div class="wrap"><table id="t" style="width:100%;border:0">
            <tr id="r"><td id="c1" style="border:9px solid">alpha</td>
            <td id="c2" style="border:9px solid">beta</td></tr></table></div>
            """;

        Assert.Equal(36f, Height(html, "t"), 1f);
        Assert.Equal(36f, Height(html, "c1"), 1f);
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
