// Nested shrink-to-fit boxes and the layout cache. No counterpart in crates/obscura-render: the
// measurement ring is a deviation from vendor/taffy/src/tree/cache.rs (see Layout/Cache.cs).
using System.Diagnostics;
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Render;
using PocketCalculator.Render.Layout;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class NestedShrinkToFitLayoutTests
{
    /// <summary>A body holding <paramref name="depth"/> nested divs, each with text and the next.</summary>
    private static (DomTree Tree, List<NodeId> Chain) Chain(string css, int depth)
    {
        StringBuilder html = new("<!doctype html><html><body>");
        for (int i = 0; i < depth; i++)
        {
            html.Append("<div style=\"").Append(css).Append("\">x");
        }

        for (int i = 0; i < depth; i++)
        {
            html.Append("</div>");
        }

        html.Append("</body></html>");
        DomTree tree = HtmlParsing.ParseHtml(html.ToString());
        List<NodeId> chain = [.. tree.QuerySelectorAll("div")];
        return (tree, chain);
    }

    private static LayoutInput WidthMeasure(float width) => new()
    {
        RunMode = RunMode.ComputeSize,
        SizingMode = SizingMode.ContentSize,
        Axis = RequestedAxis.Vertical,
        KnownDimensions = new Size<float?>(width, null),
        ParentSize = new Size<float?>(width, null),
        AvailableSpace = new Size<AvailableSpace>(AvailableSpace.Definite(width), AvailableSpace.MinContent),
        VerticalMarginsAreCollapsible = GeometryExtensions.LineFalse,
    };

    [Fact]
    public void MeasurementsThatShareASlotDoNotEvictEachOther()
    {
        // Known width, unknown height, min-content height: both land in taffy's slot 2, which
        // keeps one entry. A shrink-to-fit parent asks for its child at its min-content and at
        // its max-content width, alternately, so the second evicted the first on every round.
        Cache cache = new();
        LayoutInput narrow = WidthMeasure(14f);
        LayoutInput wide = WidthMeasure(38f);
        cache.Store(narrow, LayoutOutput.FromOuterSize(new Size<float>(14f, 40f)));
        cache.Store(wide, LayoutOutput.FromOuterSize(new Size<float>(38f, 20f)));

        Assert.Equal(new Size<float>(14f, 40f), cache.Get(narrow)?.Size);
        Assert.Equal(new Size<float>(38f, 20f), cache.Get(wide)?.Size);
        Assert.Null(cache.Get(WidthMeasure(20f)));

        // More distinct widths than the ring holds: the most recent ones still answer, and a
        // clear drops the evicted ones too.
        for (int i = 0; i < 40; i++)
        {
            cache.Store(WidthMeasure(100f + i), LayoutOutput.FromOuterSize(new Size<float>(100f + i, i)));
        }

        Assert.Equal(new Size<float>(139f, 39f), cache.Get(WidthMeasure(139f))?.Size);
        Assert.Equal(new Size<float>(130f, 30f), cache.Get(WidthMeasure(130f))?.Size);
        Assert.False(cache.IsEmpty());
        Assert.Equal(ClearState.Cleared, cache.Clear());
        Assert.True(cache.IsEmpty());
        Assert.Null(cache.Get(WidthMeasure(130f)));
        Assert.Null(cache.Get(narrow));
    }

    [Fact]
    public void NestedFloatsKeepChromiumInlineGeometry()
    {
        // Chromium 141, body margin 8: each float shrinks to its 'x' plus the float inside it,
        // so the widths step down by the 8px glyph and the 2px of padding, and each one starts
        // at its parent's content edge.
        (DomTree tree, List<NodeId> chain) = Chain("float:left;padding:1px", 5);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        float[] widths = [50f, 40f, 30f, 20f, 10f];
        for (int i = 0; i < chain.Count; i++)
        {
            Rect rect = laid.Rects[chain[i]];
            Assert.Equal(8f + i, rect.X);
            Assert.Equal(widths[i], rect.Width);
        }
    }

    [Theory]
    [InlineData("float:left;padding:1px")]
    [InlineData("display:inline-block;padding:1px")]
    [InlineData("position:absolute;padding:1px")]
    [InlineData("display:inline-flex;padding:1px")]
    [InlineData("display:flex;width:max-content;padding:1px")]
    [InlineData("display:table;padding:1px")]
    public void DeepShrinkToFitChainsLayOutInBoundedTime(string css)
    {
        // 300 nested floats took over a minute when every level re-measured its subtree once
        // per request from its parent; Chromium lays them out in milliseconds. The bound is
        // loose because the machine running the suite may be loaded.
        (DomTree tree, List<NodeId> chain) = Chain(css, 300);
        Stopwatch watch = Stopwatch.StartNew();
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        watch.Stop();

        Assert.True(laid.Rects[chain[0]].Width > 0f);
        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(20),
            $"{css}: 300 levels took {watch.Elapsed.TotalSeconds:F1}s");
    }
}
