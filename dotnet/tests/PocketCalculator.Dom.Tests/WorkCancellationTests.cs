// SECURITY.md H8: the cancellation scope DOM, style, layout and paint passes observe. No
// counterpart in crates/obscura-dom; see "Known deviations" in todo.md.
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Dom.Tests;

public class WorkCancellationTests
{
    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    private static DomTree Wide(int count)
    {
        var tree = HtmlParsing.ParseHtml("<!doctype html><html><body></body></html>");
        NodeId body = tree.QuerySelector("body")!.Value;
        for (int i = 0; i < count; i++)
        {
            NodeId p = tree.NewNode(NodeData.Element(QualName.Html("p")));
            tree.AppendChild(body, p);
            tree.AppendText(p, "x");
        }

        return tree;
    }

    [Fact]
    public void WithNoScopeNothingIsCancelled()
    {
        Assert.False(WorkCancellation.IsCancellationRequested);
        WorkCancellation.ThrowIfCancellationRequested();
        Assert.False(WorkCancellation.Current.CanBeCanceled);
    }

    [Fact]
    public void ACancelledScopeThrowsUntilItIsDisposed()
    {
        using (WorkCancellation.Enter(Cancelled()))
        {
            Assert.True(WorkCancellation.IsCancellationRequested);
            Assert.Throws<OperationCanceledException>(WorkCancellation.ThrowIfCancellationRequested);
        }

        WorkCancellation.ThrowIfCancellationRequested();
        Assert.False(WorkCancellation.Current.CanBeCanceled);
    }

    [Fact]
    public void ANestedScopeObservesBothTokens()
    {
        using var outer = new CancellationTokenSource();
        using var inner = new CancellationTokenSource();
        using (WorkCancellation.Enter(outer.Token))
        {
            using (WorkCancellation.Enter(inner.Token))
            {
                WorkCancellation.ThrowIfCancellationRequested();
                outer.Cancel();
                Assert.Throws<OperationCanceledException>(WorkCancellation.ThrowIfCancellationRequested);
            }

            // Back to the outer token alone, which is cancelled.
            Assert.Equal(outer.Token, WorkCancellation.Current);
        }

        Assert.False(WorkCancellation.Current.CanBeCanceled);
    }

    [Fact]
    public void AnUncancellableTokenLeavesTheOuterScopeInPlace()
    {
        using var outer = new CancellationTokenSource();
        using (WorkCancellation.Enter(outer.Token))
        using (WorkCancellation.Enter(CancellationToken.None))
        {
            Assert.Equal(outer.Token, WorkCancellation.Current);
        }
    }

    [Fact]
    public void TreeWalksStopWhenCancelled()
    {
        DomTree tree = Wide(200);
        using (WorkCancellation.Enter(Cancelled()))
        {
            Assert.Throws<OperationCanceledException>(() => tree.Descendants(tree.Document));
            Assert.Throws<OperationCanceledException>(() => tree.QuerySelectorAll("p"));
            Assert.Throws<OperationCanceledException>(() => tree.OuterHtml(tree.Document));
        }

        // And run normally once the scope is gone.
        Assert.Equal(200, tree.QuerySelectorAll("p").Count);
    }
}
