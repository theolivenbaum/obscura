// Grid track and line limits (Chromium's kGridMaxTracks, GridLimits in the port). No
// counterpart in crates/obscura-render or taffy, which expand and place without a bound;
// see "Known deviations" in todo.md. Expected geometry is measured in Chromium 141.
using System.Diagnostics;
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using GridLimits = PocketCalculator.Render.Layout.GridLimits;
using GridPlacementKind = PocketCalculator.Render.Layout.GridPlacementKind;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class GridTrackLimitTests
{
    private const int Max = GridLimits.MaxTracks;

    // Generous: the machine running the suite may be loaded. The unbounded inputs took from
    // seconds (2^20 tracks) to never finishing (auto-fill in a 10^8 px container).
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static string Nest(int depth, string inner)
    {
        string value = inner;
        for (int i = 0; i < depth; i++)
        {
            value = $"repeat(2, {value})";
        }

        return value;
    }

    private static int TrackCount(string value)
    {
        Assert.True(ComputedStyle.TryParseTrackListNamed(value, out var tracks, out _, out _), value);
        return tracks.Count;
    }

    /// <summary>Lays out a margin-less page and returns the rects of #a and #b.</summary>
    private static (Rect A, Rect B) LayOut(string css, string items = "<div id=a>a</div><div id=b>b</div>")
    {
        DomTree tree = HtmlParsing.ParseHtml(
            $"<!doctype html><style>html,body{{margin:0}}{css}</style><div id=g>{items}</div>");
        var clock = Stopwatch.StartNew();
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));
        Assert.True(clock.Elapsed < Budget, $"layout took {clock.Elapsed}");
        NodeId Id(string id) => tree.GetElementById(id) ?? throw new InvalidOperationException(id);
        return (laid.Rects[Id("a")], laid.Rects[Id("b")]);
    }

    // ------------------------------------------------------------------ parsing

    [Theory]
    [InlineData("repeat(2, repeat(2, 1fr))")]
    [InlineData("repeat(2, repeat(2, 1px))")]
    [InlineData("10px repeat(2, 1px repeat(3, 2px))")]
    [InlineData("repeat(2, repeat(auto-fill, 1px))")]
    [InlineData("repeat(auto-fill, repeat(2, 1px))")]
    [InlineData("repeat(0, 1px)")]
    public void RepeatInsideRepeatAndZeroRepeatAreInvalid(string value)
    {
        Assert.False(ComputedStyle.TryParseTrackListNamed(value, out var tracks, out _, out _));
        Assert.Empty(tracks);
        Assert.False(ComputedStyle.SupportsDeclaration("grid-template-columns", value));
        Assert.False(ComputedStyle.SupportsDeclaration("grid-template-rows", value));
    }

    [Fact]
    public void DeeplyNestedRepeatIsRejectedWithoutExpanding()
    {
        var clock = Stopwatch.StartNew();
        Assert.False(ComputedStyle.TryParseTrackListNamed(Nest(40, "1fr"), out _, out _, out _));
        Assert.False(ComputedStyle.SupportsDeclaration("grid-template-columns", Nest(40, "1fr")));
        Assert.True(clock.Elapsed < Budget, $"{clock.Elapsed}");
    }

    [Fact]
    public void ValidRepeatStillExpands()
    {
        Assert.Equal(2, TrackCount("repeat(2, 1fr)"));
        Assert.Equal(6, TrackCount("repeat(3, 1px 2px)"));
        // Rust caps each repeat() at 1000; Chromium does not.
        Assert.Equal(1001, TrackCount("repeat(1001, 1px)"));
        Assert.True(ComputedStyle.SupportsDeclaration("grid-template-columns", "repeat(2, [a] 1fr [b])"));
    }

    [Theory]
    [InlineData("repeat(100000000, 1px)")]
    [InlineData("repeat(99999999999999999999999, 1px)")]
    [InlineData("repeat(3, 1px) repeat(100000000, 2px)")]
    [InlineData("repeat(10000, 1px 2px)")]
    public void TrackListIsTruncatedAtTheLimit(string value)
    {
        var clock = Stopwatch.StartNew();
        Assert.Equal(Max, TrackCount(value));
        Assert.True(clock.Elapsed < Budget, $"{clock.Elapsed}");
    }

    [Fact]
    public void SubgridLineNameRepeatIsBounded()
    {
        var clock = Stopwatch.StartNew();
        Assert.True(ComputedStyle.TryParseTrackListNamed(
            "subgrid repeat(100000000, [a] [b] [c])", out var tracks, out var names, out _));
        Assert.Empty(tracks);
        Assert.InRange(names.Count, 1, Max + 3);
        Assert.True(clock.Elapsed < Budget, $"{clock.Elapsed}");
    }

    [Theory]
    [InlineData("99999999", Max)]
    [InlineData("-99999999", -Max)]
    [InlineData("99999999999999999999999", Max)]
    [InlineData("32768", Max)]
    [InlineData("5000", 5000)]
    [InlineData("-3", -3)]
    public void LineNumbersClampToTheLimit(string value, int expected)
    {
        var placement = ComputedStyle.ParseGridPlacement(value);
        Assert.Equal(GridPlacementKind.Line, placement.Kind);
        Assert.Equal(expected, placement.LineIndex);
    }

    [Theory]
    [InlineData("span 99999999", Max)]
    [InlineData("span 70000", Max)]
    [InlineData("span 3", 3)]
    public void SpansClampToTheLimit(string value, int expected)
    {
        var placement = ComputedStyle.ParseGridPlacement(value);
        Assert.Equal(GridPlacementKind.Span, placement.Kind);
        Assert.Equal(expected, placement.SpanCount);
    }

    [Theory]
    [InlineData("1000000000", (ushort)64)]
    [InlineData("2147483648", (ushort)64)]
    [InlineData("99999999999999999999", (ushort)64)]
    [InlineData("3", (ushort)3)]
    public void ColumnCountClampsInsteadOfRejecting(string value, ushort expected)
    {
        Assert.Equal(expected, ComputedStyle.ParseColumnCount(value));
        Assert.True(ComputedStyle.SupportsDeclaration("column-count", value));
    }

    [Theory]
    [InlineData("1e9")]
    [InlineData("0")]
    [InlineData("-2")]
    public void ColumnCountStillRejectsNonPositiveIntegers(string value)
    {
        Assert.Null(ComputedStyle.ParseColumnCount(value));
        Assert.False(ComputedStyle.SupportsDeclaration("column-count", value));
    }

    // ------------------------------------------------------------------- layout

    [Fact]
    public void InvalidNestedRepeatIsDroppedFromTheCascade()
    {
        // Chromium keeps the earlier declaration: two 100px columns.
        (Rect a, Rect b) = LayOut(
            $"#g{{display:grid;width:1000px;grid-template-columns:100px 100px;grid-template-columns:{Nest(22, "1fr")}}}");
        Assert.Equal(100f, a.Width);
        Assert.Equal(100f, b.X);
    }

    [Fact]
    public void InvalidNestedRepeatDropsTheGridTemplateShorthand()
    {
        (Rect a, _) = LayOut(
            "#g{display:grid;width:1000px;grid-template:auto / 100px 100px;grid-template:auto / repeat(2, repeat(2, 1fr))}");
        Assert.Equal(100f, a.Width);
    }

    [Fact]
    public void HugeRepeatCountLaysOutQuickly()
    {
        (Rect a, Rect b) = LayOut("#g{display:grid;width:1000px;grid-template-columns:repeat(100000000, 1px)}");
        Assert.Equal(1f, a.Width);
        Assert.Equal(1f, b.X);
    }

    [Fact]
    public void HugeRowRepeatCountLaysOutQuickly()
    {
        (Rect a, Rect b) = LayOut("#g{display:grid;width:1000px;grid-template-rows:repeat(100000000, 1px)}");
        Assert.Equal(1f, a.Height);
        Assert.Equal(1f, b.Y);
    }

    [Fact]
    public void AutoFillInAHugeContainerStopsAtTheLimit()
    {
        // Chromium: 10^7 one-pixel columns, the items in the first two.
        (Rect a, Rect b) = LayOut("#g{display:grid;width:100000000px;grid-template-columns:repeat(auto-fill, 1px)}");
        Assert.Equal(1f, a.Width);
        Assert.Equal(1f, b.X);
    }

    [Theory]
    [InlineData("0px")]
    [InlineData("0.001px")]
    [InlineData("0.5px 0.5px")]
    public void AutoFillFloorsEachTrackAtOnePixel(string tracks)
    {
        // Chromium: repeat(auto-fill, 0.5px 0.5px) in 1000px makes 500 repetitions, not 1000,
        // and 0px tracks make 1000 rather than an unbounded count.
        (Rect a, Rect b) = LayOut(
            $"#g{{display:grid;width:1000px;grid-template-columns:repeat(auto-fill, {tracks})}}");
        Assert.Equal(a.Y, b.Y);
        Assert.True(b.X <= 1f, $"{b}");
    }

    [Fact]
    public void PlacementPastTheLimitSpansTheWholeGrid()
    {
        // Chromium: grid-column: 1 / 99999999 spans the grid (1000px) instead of being dropped.
        (Rect a, _) = LayOut("#g{display:grid;width:1000px;grid-template-columns:10px 10px}#a{grid-column:1 / 99999999}");
        Assert.Equal(0f, a.X);
        Assert.Equal(1000f, a.Width);
    }

    [Fact]
    public void SpanPastTheLimitSpansTheWholeGrid()
    {
        (Rect a, _) = LayOut("#g{display:grid;width:1000px;grid-template-columns:10px 10px}#a{grid-column:span 99999999}");
        Assert.Equal(1000f, a.Width);
    }

    [Fact]
    public void ItemPastTheLimitLandsInTheLastTrack()
    {
        // Chromium: grid-column: 10000000 / span 5 ends flush with the grid's right edge.
        (Rect a, _) = LayOut("#g{display:grid;width:1000px;grid-template-columns:10px 10px}#a{grid-column:99999999 / span 5}");
        Assert.Equal(1000f, a.X + a.Width, 2);
    }

    [Fact]
    public void FarImplicitRowIsBounded()
    {
        // Chromium: a lands in row 5000000 below b, which takes row 1.
        (Rect a, Rect b) = LayOut("#g{display:grid;width:1000px;grid-template-columns:10px 10px}#a{grid-row:5000000}");
        Assert.Equal(0f, a.X);
        Assert.Equal(b.Height, a.Y);
        Assert.Equal(0f, b.Y);
    }

    [Fact]
    public void ItemsAtOppositeFarCornersAreBounded()
    {
        (Rect a, Rect b) = LayOut(
            "#g{display:grid;width:1000px}",
            "<div id=a style='grid-column:-99999999;grid-row:-99999999'>a</div>"
            + "<div style='grid-column:99999999;grid-row:99999999'>z</div><div id=b>b</div>");
        Assert.Equal(0f, a.X);
        Assert.Equal(0f, a.Y);
        Assert.True(b.Width > 0f);
    }

    [Fact]
    public void AutoPlacementAroundAGridFillingItemIsFast()
    {
        // One item covers every cell the limit allows; the next is auto-placed after it. taffy
        // probes each of the rows x columns positions in turn.
        string items = string.Concat(Enumerable.Repeat("<div class=s>s</div>", 20));
        (Rect a, Rect b) = LayOut(
            "#g{display:grid;width:1000px}.s,#a{grid-column:1 / 99999999;grid-row:1 / 99999999}",
            $"<div id=a>a</div>{items}<div id=b>b</div>");
        Assert.Equal(1000f, a.Width);
        Assert.True(b.Y >= a.Y + a.Height - 0.5f, $"{a} {b}");
    }

    [Fact]
    public void GridTemplateAreasWiderThanTheLimitIsBounded()
    {
        string row = string.Join(' ', Enumerable.Repeat("x", 40_000)) + " a";
        (Rect a, _) = LayOut(
            $"#g{{display:grid;width:1000px;grid-template-areas:\"{row}\"}}#a{{grid-area:a}}");
        Assert.True(a.Width >= 0f);
    }

    [Fact]
    public void RtlColumnFlowStillGrowsColumnsToTheLeft()
    {
        // The window for a reversed axis is anchored at its far end; auto-placed columns must
        // still be created, right to left.
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><style>html,body{margin:0}#g{display:grid;direction:rtl;width:300px;"
            + "grid-auto-flow:column;grid-template-rows:20px;grid-auto-columns:50px}</style>"
            + "<div id=g><div id=a>a</div><div id=b>b</div><div id=c>c</div></div>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));
        Rect Of(string id) => laid.Rects[tree.GetElementById(id)!.Value];
        Assert.Equal(250f, Of("a").X);
        Assert.Equal(200f, Of("b").X);
        Assert.Equal(150f, Of("c").X);
    }

    [Fact]
    public void ManyAutoPlacedItemsStillFlowIntoRows()
    {
        string items = string.Concat(Enumerable.Range(0, 3000).Select(i => $"<div>{i}</div>"));
        (Rect a, Rect b) = LayOut(
            "#g{display:grid;width:900px;grid-template-columns:repeat(3, 1fr);grid-auto-rows:10px}",
            $"<div id=a>a</div>{items}<div id=b>b</div>");
        Assert.Equal(0f, a.Y);
        // 3002 items in three columns: b is item 3002, in row 1001, column 2.
        Assert.Equal(10000f, b.Y);
        Assert.Equal(300f, b.X);
    }
}
