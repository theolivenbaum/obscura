using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The anonymous <c>inline-table</c> CSS 2.1 17.2.1 generates inside a <c>display: inline</c>
/// element whose children are table-internal.
/// </summary>
/// <remarks>
/// Measured on Chromium 141, hand-launched over CDP at a 1280x720 viewport. The sibling facts in
/// <see cref="PercentageTableColumnTests"/> cover the block-level displays, where the anonymous
/// table sits inside a box that is already in the parent's block flow. <c>display: inline</c>
/// collects its children as inline items instead, so the table fixup was never consulted and the
/// rows laid out in the ancestor block formatting context: one full-width row per line, with the
/// element itself generating no box at all.
/// <para>
/// The engine measures max-content with HarfBuzz and Skia rather than cosmic-text, so each
/// column split lands within a pixel of Chromium's; the ranges are the same ones the sibling
/// facts use. The element's own inline-box fragment is placed at the top of its line rather than
/// on the line's baseline, which is the line-box model's known limitation and is why these facts
/// assert the fragment's size and inline position but not its <c>y</c>.
/// </para>
/// </remarks>
public class InlineAnonymousTableTests
{
    private static Rect Box(string html, string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        return prepared.Layout.Rects.TryGetValue(node, out Rect rect)
            ? rect
            : throw new InvalidOperationException($"no box for #{id}");
    }

    private const string Style = """
        <style>
        html,body{margin:0} body{font:16px serif} .wrap{width:600px}
        table{border-collapse:collapse} td{padding:0;border:0}
        </style>
        """;

    /// <summary>The two-column fixture, with text of the caller's around it.</summary>
    private static string Inline(string before, string after, string wrapStyle = "") => Style + $"""
        <div class="wrap" style="{wrapStyle}">{before}<table id="t" style="display:inline">
        <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table>{after}</div>
        """;

    // Chromium 141 on this fixture: the anonymous table is 147.5 wide with 34.66 / 112.84
    // columns, exactly as the same rows under `display: inline-table`.
    private static void AssertAnonymousTable(string html)
    {
        Assert.InRange(Box(html, "t").Width, 145f, 150f);
        Assert.InRange(Box(html, "c1").Width, 33f, 36f);
        Assert.InRange(Box(html, "c2").Width, 111f, 115f);
    }

    /// <summary>The same, for a fixture whose row carries an id.</summary>
    private static void AssertAnonymousTableRow(string html)
    {
        AssertAnonymousTable(html);
        Assert.InRange(Box(html, "r").Width, 145f, 150f);
    }

    [Fact]
    public void DisplayInlineOverTableChildrenGeneratesAnAnonymousTable()
    {
        // Chromium 141: the row is 147.5 wide, not the 600 of the containing block, and the
        // element itself has a fragment 147.5 wide. Both used to be wrong: the element had no
        // box, and its row was spliced into the wrapper's block flow at the full 600.
        string html = Inline("", "");

        AssertAnonymousTableRow(html);
        Assert.Equal(0f, Box(html, "r").X, 1f);
        Assert.Equal(0f, Box(html, "r").Y, 1f);
    }

    [Fact]
    public void TheAnonymousTableIsAnAtomicInlineOnItsLine()
    {
        // Chromium 141: 44.86 is the width of "before ", and the table shares that line - its
        // row is at y=0. It used to start a block of its own at y=18.
        foreach (string html in new[] { Inline("before ", ""), Inline("before ", " after") })
        {
            AssertAnonymousTableRow(html);
            Assert.InRange(Box(html, "r").X, 43f, 47f);
            Assert.Equal(0f, Box(html, "r").Y, 1f);
        }

        // Text after it alone leaves the table at the line's start.
        string trailing = Inline("", " after");

        Assert.Equal(0f, Box(trailing, "r").X, 1f);
        Assert.Equal(0f, Box(trailing, "r").Y, 1f);
    }

    [Fact]
    public void TwoOfThemShareOneLine()
    {
        // Chromium 141: the second starts at x=147.5, where the first ends, both on y=0.
        string html = Style + """
            <div class="wrap"><table id="t" style="display:inline">
            <tr id="r"><td>alpha</td><td>beta gamma delta</td></tr></table><table id="t2"
            style="display:inline"><tr id="r2"><td>alpha</td><td>beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(0f, Box(html, "r").X, 1f);
        Assert.InRange(Box(html, "r2").X, 145f, 150f);
        Assert.Equal(0f, Box(html, "r").Y, 1f);
        Assert.Equal(0f, Box(html, "r2").Y, 1f);
    }

    [Fact]
    public void ItWrapsToTheNextLineWhenTheLineIsTooNarrow()
    {
        // Chromium 141: in a 160px block, "xx " plus a 147.5 table does not fit, so the table
        // takes the second line whole - x=0, y=18 - and the block is 36 tall.
        string html = Inline("xx ", "", "width:160px");

        AssertAnonymousTableRow(html);
        Assert.Equal(0f, Box(html, "r").X, 1f);
        Assert.Equal(18f, Box(html, "r").Y, 1f);
    }

    [Fact]
    public void AuthoredTableCellChildrenUnderDisplayInlineDoTheSame()
    {
        // Chromium 141: 34.66 / 112.84 again - the fixup is about the children being
        // table-internal, not about the element being a `<table>`. The two cells used to be
        // spliced out as two 600-wide blocks stacked under the text.
        string html = """
            <style>html,body{margin:0} body{font:16px serif} .wrap{width:600px}
            .cellish{display:table-cell}</style>
            <div class="wrap">before <div id="t" style="display:inline">
            <div class="cellish" id="c1">alpha</div>
            <div class="cellish" id="c2">beta gamma delta</div></div> after</div>
            """;

        AssertAnonymousTable(html);
        Assert.InRange(Box(html, "c1").X, 43f, 47f);
        Assert.Equal(0f, Box(html, "c1").Y, 1f);
        Assert.Equal(0f, Box(html, "c2").Y, 1f);
    }

    [Fact]
    public void ItLaysOutExactlyLikeTheInlineTableSpellingOfTheSameRows()
    {
        // CSS 2.1 17.2.1 calls for an anonymous `inline-table`, and Chromium 141 agrees: the
        // rows and cells of the two spellings land on the same pixels. Only the element's own
        // box differs, because `display: inline` makes it an inline box around the anonymous
        // table rather than the table box itself.
        string anonymous = Inline("before ", " after");
        string authored = Style + """
            <div class="wrap">before <table id="t" style="display:inline-table">
            <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table> after</div>
            """;

        foreach (string id in new[] { "r", "c1", "c2" })
        {
            Assert.Equal(Box(authored, id).X, Box(anonymous, id).X, 0.5f);
            Assert.Equal(Box(authored, id).Y, Box(anonymous, id).Y, 0.5f);
            Assert.Equal(Box(authored, id).Width, Box(anonymous, id).Width, 0.5f);
            Assert.Equal(Box(authored, id).Height, Box(anonymous, id).Height, 0.5f);
        }
    }

    [Fact]
    public void TheElementsOwnBorderAndPaddingStayOnTheElement()
    {
        // Chromium 141: the element's 5px border and 10px padding stay on the inline box, so
        // the anonymous table starts at x=15 and is still 147.5 wide, and the element's own
        // fragment is 177.5 across.
        string html = Style + """
            <div class="wrap"><table id="t" style="display:inline;border:5px solid;padding:10px">
            <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
            """;

        Assert.InRange(Box(html, "t").Width, 175f, 180f);
        Assert.InRange(Box(html, "r").Width, 145f, 150f);
        Assert.Equal(15f, Box(html, "c1").X, 1f);
    }

    [Fact]
    public void BorderSpacingInheritsIntoTheAnonymousTable()
    {
        // `border-spacing` inherits, so it reaches the anonymous box the element generates:
        // Chromium 141 puts the first cell at x=8 and makes the row 155.5 wide.
        string html = """
            <style>html,body{margin:0} body{font:16px serif} .wrap{width:600px}
            table{border-spacing:8px} td{padding:0;border:0}</style>
            <div class="wrap"><table id="t" style="display:inline">
            <tr id="r"><td id="c1">alpha</td><td id="c2">beta gamma delta</td></tr></table></div>
            """;

        Assert.Equal(8f, Box(html, "c1").X, 1f);
        Assert.Equal(8f, Box(html, "c1").Y, 1f);
        Assert.InRange(Box(html, "r").Width, 153f, 158f);
    }
}
