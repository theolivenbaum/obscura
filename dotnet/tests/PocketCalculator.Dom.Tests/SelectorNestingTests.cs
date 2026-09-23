using PocketCalculator.Dom;
using PocketCalculator.Dom.Selectors;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// SECURITY.md C6: selector nesting is capped at <see cref="SelectorParser.MaxNestingDepth"/>,
/// and matching never recurses past the stack. Before the fix every one of these that nests
/// thousands deep overflowed the stack and killed the test host.
/// </summary>
public class SelectorNestingTests
{
    private static string Nest(string open, int depth, string inner = "a") =>
        string.Concat(Enumerable.Repeat(open, depth)) + inner + new string(')', depth);

    [Theory]
    [InlineData(":not(")]
    [InlineData(":is(")]
    [InlineData(":where(")]
    [InlineData(":nth-child(1 of ")]
    public void DeeplyNestedSelectorsAreInvalid(string open)
    {
        Assert.False(SelectorParser.TryParse(Nest(open, 20000), out _, out _));
        Assert.False(SelectorParser.TryParse(Nest(open, SelectorParser.MaxNestingDepth + 1), out _, out _));
        Assert.True(SelectorParser.TryParse(Nest(open, SelectorParser.MaxNestingDepth), out _, out _));
    }

    [Fact]
    public void DeeplyNestedHasIsInvalid()
    {
        Assert.False(SelectorParser.TryParse(Nest(":has(", 20000), out _, out _));
        Assert.False(SelectorParser.TryParse(":has(" + Nest(":is(", 20000) + ")", out _, out _));
    }

    [Fact]
    public void HasInsideHasIsInvalid()
    {
        Assert.False(SelectorParser.TryParse(":has(:has(a))", out _, out _));
        Assert.False(SelectorParser.TryParse("div:has(> p:has(span))", out _, out _));
        Assert.True(SelectorParser.TryParse("div:has(> p):has(span)", out _, out _));
        Assert.True(SelectorParser.TryParse(":is(:has(a), b)", out _, out _));
    }

    [Fact]
    public void ForgivingListsDoNotHideTheNestingCap()
    {
        // :is() keeps an invalid arm as never-matching, but too-deep nesting fails the whole list.
        Assert.False(SelectorParser.TryParse(":is(b, " + Nest(":is(", 100) + ")", out _, out _));

        // An ordinary bad arm is still forgiven, and does not count against later nesting.
        Assert.True(SelectorParser.TryParse(":is(:bogus(x), b)" + Nest(":is(", SelectorParser.MaxNestingDepth - 1), out _, out _));
    }

    [Fact]
    public void QueryWithATooDeepSelectorFindsNothing()
    {
        var tree = HtmlParsing.ParseHtml("<div><a></a></div>");
        Assert.False(tree.TryQuerySelectorAll(Nest(":not(", 20000, "b"), out var results, out var error));
        Assert.Empty(results);
        Assert.NotNull(error);
        Assert.Single(tree.QuerySelectorAll(Nest(":is(", SelectorParser.MaxNestingDepth, "a")));
    }

    [Fact]
    public void LongCombinatorChainsDoNotOverflowWhileMatching()
    {
        var tree = HtmlParsing.ParseHtml("<body>" + string.Concat(Enumerable.Repeat("<i></i>", 50000)) + "</body>");
        var items = tree.QuerySelectorAll("i");
        var selector = string.Join('~', Enumerable.Repeat("i", 50000));

        // The chain matches the last sibling in principle; once the stack runs out it simply
        // reports no match instead of overflowing.
        Assert.True(tree.TryMatchesSelector(items[^1], selector, out _, out _));
        Assert.True(tree.TryMatchesSelector(items[^1], "i~i~i", out var shallow, out _));
        Assert.True(shallow);
    }
}
