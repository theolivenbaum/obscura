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
}
