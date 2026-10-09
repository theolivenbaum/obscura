using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// UA default <c>display</c> for tags the UA sheet does not name. `style.rs` makes them
/// block; Chromium 141 reports <c>inline</c> (see "Known deviations" in todo.md).
/// </summary>
public class UaDisplayTests
{
    [Theory]
    [InlineData("foo-bar")]
    [InlineData("x-y")]
    [InlineData("ytd-logo")]
    [InlineData("foo")]
    [InlineData("picture")]
    [InlineData("map")]
    [InlineData("nobr")]
    [InlineData("acronym")]
    [InlineData("strike")]
    public void UnknownAndCustomElementsDefaultToInline(string tag)
    {
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle(tag, Namespaces.Html).Display);
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle(tag).Display);
    }

    [Theory]
    [InlineData("div")]
    [InlineData("section")]
    [InlineData("search")]
    [InlineData("listing")]
    [InlineData("frameset")]
    public void BlockElementsStayBlock(string tag)
    {
        Assert.Equal(Display.Block, ComputedStyle.UaStyle(tag, Namespaces.Html).Display);
    }

    /// <summary>A custom element in running text stays on the line (Chromium 141: one line).</summary>
    [Fact]
    public void CustomElementFlowsInlineWithText()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """
            <style>html,body{margin:0} p{margin:0;font:16px/20px sans-serif;width:400px}</style>
            <p id="p">before <x-chip id="chip">chip</x-chip> after</p>
            """);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (800f, 600f), null, new RenderResourceCache())
            ?? throw new InvalidOperationException("layout did not prepare");
        NodeId Id(string id) => tree.GetElementById(id) ?? throw new InvalidOperationException(id);

        Assert.Equal(20f, prepared.Layout.Rects[Id("p")].Height);
        Assert.Equal("inline", prepared.ComputedStyle(Id("chip"))?["display"]);
    }
}
