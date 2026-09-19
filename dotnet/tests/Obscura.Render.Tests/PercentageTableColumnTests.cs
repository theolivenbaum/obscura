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

    private static string Table(string tableWidth) => Style + $"""
        <div class="wrap"><table id="t" style="width:{tableWidth}">
        <tr><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
        """;

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
        // 150% of 600px. Column distribution in this case is still the old equal share; what is
        // pinned here is that the table is not clamped back to its containing block.
        Assert.InRange(Width(Table("150%"), "t"), 890f, 910f);
    }
}
