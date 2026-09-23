// SECURITY.md C5/C6: a stylesheet must not be able to overflow the stack, or hang, with deep
// nesting. No counterpart in crates/obscura-render; see "Known deviations" in todo.md.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Render.Tests;

public class DeepStylesheetTests
{
    private static string Nest(string open, string inner, string close, int depth) =>
        string.Concat(Enumerable.Repeat(open, depth)) + inner + string.Concat(Enumerable.Repeat(close, depth));

    private static void AssertLaysOut(string css)
    {
        DomTree tree = HtmlParsing.ParseHtml("<style>" + css + " p{height:20px}</style><p>hi</p>");
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        NodeId p = tree.QuerySelector("p")!.Value;
        Assert.Equal(20f, laid.Rects[p].Height);
    }

    [Theory]
    [InlineData(":not(")]
    [InlineData(":is(")]
    [InlineData(":where(")]
    [InlineData(":has(")]
    public void DeeplyNestedSelectorRuleIsDropped(string open) =>
        AssertLaysOut(Nest(open, "b", ")", 20000) + "{height:99px}");

    [Fact]
    public void DeeplyNestedStyleRulesDoNotOverflow() =>
        AssertLaysOut(Nest("p{", "color:red", "}", 20000));

    [Fact]
    public void DeeplyNestedAtRulesDoNotOverflow() =>
        AssertLaysOut(Nest("@media screen{", "p{color:red}", "}", 20000));
}
