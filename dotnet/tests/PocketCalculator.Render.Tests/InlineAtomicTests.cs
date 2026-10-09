using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// Atomic inlines (inline-blocks, images, buttons) laid out inside their lines, against
/// Chromium 141's <c>getBoundingClientRect()</c> (1280x720 viewport, 16px Liberation Sans;
/// within 0.05px). No counterpart in crates/obscura-render, which lays a line holding an
/// atomic out as a flex row of word boxes; see "Known deviations" in todo.md.
/// </summary>
public sealed class InlineAtomicTests
{
    private const string Page =
        """
        <!doctype html><html><body style="margin:8px;font:16px 'Liberation Sans'">
        <div id=c1 style="width:400px">before <span id=ib1 style="display:inline-block">inside</span> after <span id=ib2 style="display:inline-block;font-size:30px">big</span></div>
        <div id=c2 style="width:400px">x<span id=ib3 style="display:inline-block;width:60px;height:30px"></span>y</div>
        <div id=c3 style="width:400px">x<img id=im1 width=30 height=30 style="vertical-align:middle">y<img id=im2 width=30 height=30 style="vertical-align:top">z<img id=im3 width=20 height=10>w</div>
        <div id=c4 style="width:400px" dir=rtl><span id=r1>alpha</span> <span id=ib4 style="display:inline-block;width:30px;height:18px"></span> <span id=r2>beta</span></div>
        <div id=c5 style="width:400px"><div id=fl style="float:left;width:100px;height:50px"></div><span id=w1 style="display:inline-block;width:80px;height:20px"></span> <span id=w2 style="display:inline-block;width:80px;height:20px"></span> <span id=w3 style="display:inline-block;width:80px;height:20px"></span> <span id=w4 style="display:inline-block;width:80px;height:20px"></span> <span id=w5 style="display:inline-block;width:80px;height:20px"></span></div>
        <div id=c6 style="width:400px">a<button id=bt style="height:40px"></button>b</div>
        <div id=c7 style="width:400px">a <span id=ib5 style="display:inline-block;overflow:hidden">clip</span> b</div>
        <div id=c8 style="width:400px;text-align:center"><span id=ib6 style="display:inline-block;width:60px;height:20px"></span> <span id=t8>beta</span></div>
        <div id=c9 style="width:150px"><span id=n1>wordwordword</span>&nbsp;<span id=ib7 style="display:inline-block;width:60px;height:20px"></span></div>
        </body></html>
        """;

    private static readonly Lazy<(DomTree Tree, DomLayout Laid)> Laid = new(() =>
    {
        DomTree tree = HtmlParsing.ParseHtml(Page);
        return (tree, RenderDom.LayoutDom(tree, (1280f, 720f)));
    });

    private static void AssertBox(string id, float x, float y, float width, float height)
    {
        (DomTree tree, DomLayout laid) = Laid.Value;
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException(id);
        Rect rect = laid.PreciseRect(node) ?? laid.Rects[node];
        string message = $"{id}: expected {x},{y},{width},{height}, got {rect}";
        Assert.True(MathF.Abs(rect.X - x) < 0.05f, message);
        Assert.True(MathF.Abs(rect.Y - y) < 0.05f, message);
        Assert.True(MathF.Abs(rect.Width - width) < 0.05f, message);
        Assert.True(MathF.Abs(rect.Height - height) < 0.05f, message);
    }

    /// <summary>An inline-block sits on the baseline of its last line box and grows the line.</summary>
    [Fact]
    public void InlineBlockTakesTheBaselineOfItsText()
    {
        AssertBox("c1", 8f, 8f, 400f, 34f);
        AssertBox("ib1", 57.813f, 21f, 41.813f, 18f);
        AssertBox("ib2", 140.531f, 8f, 40.047f, 34f);
    }

    /// <summary>
    /// An empty inline-block's baseline is its bottom margin edge, so the strut's descent
    /// hangs below it: 30px box on a 34px line.
    /// </summary>
    [Fact]
    public void EmptyInlineBlockSitsOnTheBaseline()
    {
        AssertBox("c2", 8f, 42f, 400f, 34f);
        AssertBox("ib3", 16f, 42f, 60f, 30f);
    }

    [Fact]
    public void ImagesFollowVerticalAlign()
    {
        AssertBox("c3", 8f, 76f, 400f, 30f);
        AssertBox("im1", 16f, 76f, 30f, 30f);
        AssertBox("im2", 54f, 76f, 30f, 30f);
        AssertBox("im3", 92f, 85.234f, 20f, 10f);
    }

    /// <summary>A right-to-left line orders the atomic among its words, right to left.</summary>
    [Fact]
    public void RightToLeftLineOrdersTheAtomic()
    {
        AssertBox("r1", 298.797f, 110f, 39.156f, 17f);
        AssertBox("ib4", 342.406f, 106f, 30f, 18f);
        AssertBox("r2", 376.859f, 110f, 31.141f, 17f);
    }

    /// <summary>A row of inline-blocks beside a float is narrowed line by line.</summary>
    [Fact]
    public void InlineBlocksBesideAFloatWrapLineByLine()
    {
        AssertBox("w1", 108f, 128f, 80f, 20f);
        AssertBox("w2", 192.453f, 128f, 80f, 20f);
        AssertBox("w3", 276.906f, 128f, 80f, 20f);
        AssertBox("w4", 108f, 152f, 80f, 20f);
        AssertBox("w5", 192.453f, 152f, 80f, 20f);
        AssertBox("c5", 8f, 128f, 400f, 48f);
    }

    /// <summary>An empty button's baseline is its content-box bottom.</summary>
    [Fact]
    public void EmptyButtonBaselineIsItsContentBoxBottom()
    {
        AssertBox("bt", 116.906f, 176f, 16f, 40f);
        AssertBox("c6", 8f, 176f, 400f, 41f);
    }

    /// <summary>An inline-block that clips its overflow takes its bottom margin edge as baseline.</summary>
    [Fact]
    public void OverflowHiddenInlineBlockUsesItsBottomEdge()
    {
        AssertBox("ib5", 21.344f, 217f, 24.016f, 18f);
        AssertBox("c7", 8f, 217f, 400f, 22f);
    }

    [Fact]
    public void CentredLineCountsTheAtomic()
    {
        AssertBox("ib6", 160.203f, 239f, 60f, 20f);
        AssertBox("t8", 224.656f, 245f, 31.141f, 17f);
    }

    /// <summary>Chromium breaks before an atomic even after a no-break space.</summary>
    [Fact]
    public void AtomicBreaksAfterANoBreakSpace()
    {
        AssertBox("n1", 8f, 263f, 104.047f, 17f);
        AssertBox("ib7", 8f, 281f, 60f, 20f);
    }
}
