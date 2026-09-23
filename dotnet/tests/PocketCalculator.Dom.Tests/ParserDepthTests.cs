using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// The parser's open-element depth cap (SECURITY.md C5). Chromium's
/// <c>kMaximumHTMLParserDOMTreeDepth</c> is 512: an element inserted while the stack of open
/// elements is deeper than that is attached to the current node's parent instead, so
/// <c>"&lt;div&gt;".repeat(1000)</c> nests 511 divs under body and makes the other 489 siblings of
/// the 511th.
/// </summary>
public class ParserDepthTests
{
    private static int ElementDepth(DomTree tree, NodeId id)
    {
        var depth = 0;
        for (NodeId? cur = id; cur is { } c && tree.GetNode(c) is { } node && node.IsElement; cur = node.Parent)
        {
            depth++;
        }

        return depth;
    }

    private static int MaxElementDepth(DomTree tree, IReadOnlyList<NodeId> elements)
    {
        var max = 0;
        foreach (var id in elements)
        {
            max = Math.Max(max, ElementDepth(tree, id));
        }

        return max;
    }

    [Fact]
    public void DocumentParseFlattensBeyondDepth512LikeChromium()
    {
        var tree = HtmlParsing.ParseHtml(string.Concat(Enumerable.Repeat("<div>", 1000)));
        var divs = tree.QuerySelectorAll("div");
        Assert.Equal(1000, divs.Count);

        // html(1) > body(2) > div#1(3) ... div#511(513); every later div is a child of div#510.
        Assert.Equal(513, MaxElementDepth(tree, divs));
        Assert.Equal(513, ElementDepth(tree, divs[510]));
        Assert.Equal(divs[509], tree.GetNode(divs[510])!.Parent);
        Assert.Equal(divs[509], tree.GetNode(divs[511])!.Parent);
        Assert.Equal(divs[509], tree.GetNode(divs[999])!.Parent);
        Assert.Equal(490, tree.Children(divs[509]).Count);
        Assert.Empty(tree.Children(divs[510]));
    }

    [Fact]
    public void ShallowDocumentsAreUntouched()
    {
        var tree = HtmlParsing.ParseHtml(string.Concat(Enumerable.Repeat("<div>", 511)) + "x");
        var divs = tree.QuerySelectorAll("div");
        for (var i = 1; i < divs.Count; i++)
        {
            Assert.Equal(divs[i - 1], tree.GetNode(divs[i])!.Parent);
        }

        Assert.Equal("x", tree.TextContent(divs[^1]));
    }

    [Fact]
    public void TextStaysInTheFlattenedCurrentNode()
    {
        var tree = HtmlParsing.ParseHtml(string.Concat(Enumerable.Repeat("<div>", 600)) + "deep<!--c-->");
        var divs = tree.QuerySelectorAll("div");
        var last = divs[^1];
        Assert.Equal("deep", tree.TextContent(last));
        Assert.Equal(divs[509], tree.GetNode(last)!.Parent);

        // A comment is attached like an element, one level up from the current node.
        var comment = tree.GetNode(divs[509])!.LastChild;
        Assert.NotNull(comment);
        Assert.IsType<CommentData>(tree.GetNode(comment!.Value)!.Data);
    }

    [Fact]
    public void FragmentParseIsCappedToo()
    {
        var tree = HtmlParsing.ParseFragment(string.Concat(Enumerable.Repeat("<section>", 2000)));
        var sections = tree.QuerySelectorAll("section");
        Assert.Equal(2000, sections.Count);

        // The fragment's stack starts at its synthetic <html> root, so the same 513 bound applies.
        Assert.Equal(513, MaxElementDepth(tree, sections));
    }

    [Fact]
    public void VeryDeepMarkupParsesWithinTheCap()
    {
        var tree = HtmlParsing.ParseHtml(string.Concat(Enumerable.Repeat("<span>", 100_000)));
        var spans = tree.QuerySelectorAll("span");
        Assert.Equal(100_000, spans.Count);
        Assert.Equal(513, MaxElementDepth(tree, spans));
    }
}
