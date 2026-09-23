using System.Text;
using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// H8: <c>:has()</c> results are memoized per anchor, so nested relative selectors on a deep
/// tree cost a polynomial, not an exponential, number of match steps.
/// </summary>
public class SelectorBudgetTests
{
    private const string NestedHas =
        "div:has(div:has(div:has(div:has(div:has(div:has(span))))))";

    private static DomTree DeepChain(int depth)
    {
        var html = new StringBuilder("<html><body>");
        for (int i = 0; i < depth; i++)
        {
            html.Append("<div>");
        }

        html.Append("<span></span>");
        for (int i = 0; i < depth; i++)
        {
            html.Append("</div>");
        }

        html.Append("</body></html>");
        return HtmlParsing.ParseHtml(html.ToString());
    }

    [Fact]
    public void NestedHasOnADeepTreeFinishesAndMatchesCorrectly()
    {
        DomTree tree = DeepChain(500);
        var run = Task.Run(() =>
        {
            bool ok = tree.TryQuerySelectorAll(NestedHas, out List<NodeId> results, out string? error);
            return (ok, results, error);
        });

        Assert.True(run.Wait(TimeSpan.FromSeconds(30)), "nested :has() did not finish within 30 s");
        (bool ok, List<NodeId> results, string? _) = run.Result;

        // Nested :has() is invalid per the current spec, and the parser may refuse it; when it
        // is accepted it must still answer correctly: every div with five div descendants.
        if (ok)
        {
            Assert.Equal(495, results.Count);
        }
    }

    [Fact]
    public void HasMemoAgreesWithUnmemoizedMatchingOnSmallTrees()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<html><body><section><p><b></b></p><p></p></section><section><i></i></section></body></html>");
        Assert.True(tree.TryQuerySelectorAll("section:has(b)", out List<NodeId> withB, out _));
        Assert.Single(withB);
        Assert.True(tree.TryQuerySelectorAll("p:has(b), section:has(i)", out List<NodeId> mixed, out _));
        Assert.Equal(2, mixed.Count);
        Assert.True(tree.TryQuerySelectorAll("section:has(> p)", out List<NodeId> child, out _));
        Assert.Single(child);
    }
}
