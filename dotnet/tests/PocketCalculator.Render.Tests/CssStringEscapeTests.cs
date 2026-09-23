using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// A backslash escape inside a CSS string, through the declaration splitter and the rule parser.
/// </summary>
/// <remarks>
/// CSS Syntax 3 4.3.1: a backslash inside a string escapes the next code point. The splitters
/// tracked quote state without it, so a <c>\"</c> read as the end of the string and the rest of
/// the block read as string content - which silently dropped every later declaration in the
/// rule, not only the one carrying the escape. Measured against Chromium 141.
/// </remarks>
public class CssStringEscapeTests
{
    private static Dictionary<string, string> Computed(string html, string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        return prepared.ComputedStyle(node) ?? throw new InvalidOperationException("no computed style");
    }

    [Fact]
    public void EscapedQuoteDoesNotEndTheStringInTheDeclarationSplitter()
    {
        List<string> parts = CssDeclarations.Split("""content: "ESC\"Q"; color: rgb(2,2,2)""");

        Assert.Equal(2, parts.Count);
        Assert.Equal("content: \"ESC\\\"Q\"", parts[0].Trim());
        Assert.Equal("color: rgb(2,2,2)", parts[1].Trim());
    }

    [Fact]
    public void EscapedQuoteDoesNotSwallowTheDeclarationsAfterIt()
    {
        const string html = """
            <style>#v { content: "ESC\"Q"; color: rgb(2,2,2); width: 40px; }</style>
            <div id="v">v</div>
            """;

        Dictionary<string, string> computed = Computed(html, "v");

        Assert.Equal("rgb(2, 2, 2)", computed["color"]);
        Assert.Equal("40px", computed["width"]);
    }

    [Fact]
    public void EscapedQuoteInAFontFamilyDoesNotSwallowTheRestOfTheRule()
    {
        const string html = """
            <style>#w { font-family: "He\"llo", serif; color: rgb(3,3,3); width: 60px; }</style>
            <div id="w">w</div>
            """;

        Dictionary<string, string> computed = Computed(html, "w");

        Assert.Equal("rgb(3, 3, 3)", computed["color"]);
        Assert.Equal("60px", computed["width"]);
    }

    [Fact]
    public void ASemicolonInsideAStringIsStillNotASeparator()
    {
        List<string> parts = CssDeclarations.Split("""content: "SEMI;X"; color: rgb(3,3,3)""");

        Assert.Equal(2, parts.Count);
        Assert.Equal("content: \"SEMI;X\"", parts[0].Trim());
    }

    [Fact]
    public void ATrailingEscapedBackslashStillClosesItsString()
    {
        List<string> parts = CssDeclarations.Split("""content: "tail\\"; color: rgb(5,5,5)""");

        Assert.Equal(2, parts.Count);
        Assert.Equal("color: rgb(5,5,5)", parts[1].Trim());
    }

    [Fact]
    public void ANestedRuleWithAnEscapedQuoteStillFlattens()
    {
        // The brace scan that finds a nested rule's end tracks quotes of its own.
        const string html = """
            <style>#n { color: rgb(1,1,1); & .inner { content: "X\"Y"; color: rgb(6,6,6); } }</style>
            <div id="n">n<span class="inner" id="inner">i</span></div>
            """;

        Assert.Equal("rgb(6, 6, 6)", Computed(html, "inner")["color"]);
        Assert.Equal("rgb(1, 1, 1)", Computed(html, "n")["color"]);
    }
}
