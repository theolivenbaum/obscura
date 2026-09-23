// SECURITY.md C5: the box tree's depth cap and the deep-tree stack policy. No counterpart in
// crates/obscura-render, which recurses without a bound; see "Known deviations" in todo.md.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class DeepTreeLayoutTests
{
    /// <summary>A body holding a chain of <paramref name="depth"/> nested divs, each with text.</summary>
    private static (DomTree Tree, List<NodeId> Chain) Chain(int depth)
    {
        DomTree tree = HtmlParsing.ParseHtml("<!doctype html><html><body></body></html>");
        NodeId parent = tree.QuerySelector("body")!.Value;
        List<NodeId> chain = [];
        for (int i = 0; i < depth; i++)
        {
            NodeId div = tree.NewNode(NodeData.Element(QualName.Html("div")));
            tree.AppendChild(parent, div);
            tree.AppendText(div, "x");
            chain.Add(div);
            parent = div;
        }

        return (tree, chain);
    }

    [Fact]
    public void ElementsBeyondTheBoxDepthCapGenerateNoBox()
    {
        (DomTree tree, List<NodeId> chain) = Chain(1000);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));

        // html and body are levels 1 and 2, so the n-th div (0-based) is level n + 3.
        int lastBoxed = BuildContext.MaxBoxDepth - 3;
        Assert.True(laid.Rects.ContainsKey(chain[0]));
        Assert.True(laid.Rects.ContainsKey(chain[lastBoxed]));
        Assert.True(laid.Rects[chain[lastBoxed]].Height > 0f);
        Assert.False(
            laid.Rects.TryGetValue(chain[lastBoxed + 1], out Rect beyond) && beyond.Height > 0f,
            "an element past the cap is laid out like display:none");
        Assert.False(laid.Rects.TryGetValue(chain[^1], out Rect deepest) && deepest.Height > 0f);
    }

    [Fact]
    public void ShallowTreesAreUnaffectedByTheCap()
    {
        (DomTree tree, List<NodeId> chain) = Chain(300);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Assert.True(laid.Rects[chain[^1]].Height > 0f);
    }

    [Fact]
    public void DeepTreeDetectionCountsEveryLevel()
    {
        (DomTree tree, _) = Chain(StackGuard.DeepTreeThreshold);
        Assert.True(StackGuard.IsDeeperThan(tree, StackGuard.DeepTreeThreshold));
        Assert.False(StackGuard.IsDeeperThan(tree, StackGuard.DeepTreeThreshold + 10));

        (DomTree shallow, _) = Chain(10);
        Assert.False(StackGuard.IsDeeperThan(shallow, StackGuard.DeepTreeThreshold));
    }

    [Fact]
    public void RunWithStackForMovesDeepTreesToALargeStackAndPropagatesFailures()
    {
        (DomTree deep, _) = Chain(StackGuard.DeepTreeThreshold + 5);
        int caller = Environment.CurrentManagedThreadId;
        int ran = StackGuard.RunWithStackFor(deep, () => Environment.CurrentManagedThreadId);
        Assert.NotEqual(caller, ran);

        (DomTree shallow, _) = Chain(5);
        Assert.Equal(caller, StackGuard.RunWithStackFor(shallow, () => Environment.CurrentManagedThreadId));

        Assert.Throws<InvalidOperationException>(
            () => StackGuard.RunWithStackFor<int>(deep, () => throw new InvalidOperationException("x")));
    }
}
