// No counterpart in crates/obscura-dom: the Rust engine parses a document whole before any
// script runs. These pin the parser half of running scripts at their place in the parse, and
// document.write inserting at the insertion point, as the HTML specification and Chromium 141 do.
using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

public class DocumentParserTests
{
    private static string Body(DomTree tree) =>
        tree.TryQuerySelector("body", out var body, out _) && body is { } id ? tree.InnerHtml(id) : "<no body>";

    private static string Html(DomTree tree) => tree.InnerHtml(tree.Document);

    private static NodeId RunToScript(DocumentParser parser)
    {
        Assert.Equal(ParserStop.Script, parser.Run(0, out var script));
        return script;
    }

    [Fact]
    public void StopsAtEachScriptEndTagWithOnlyWhatPrecedesItParsed()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<p id=a></p><script>one()</script><p id=b></p><script>two()</script><p id=c></p>");

        var first = RunToScript(parser);
        Assert.Equal("one()", tree.TextContent(first));
        Assert.Equal("<p id=\"a\"></p><script>one()</script>", Body(tree));

        var second = RunToScript(parser);
        Assert.Equal("two()", tree.TextContent(second));
        Assert.Equal("<p id=\"a\"></p><script>one()</script><p id=\"b\"></p><script>two()</script>", Body(tree));

        Assert.Equal(ParserStop.Finished, parser.Run(0, out _));
        Assert.True(parser.IsFinished);
        Assert.EndsWith("<p id=\"c\"></p>", Body(tree), StringComparison.Ordinal);
    }

    [Fact]
    public void AScriptInHeadStopsBeforeTheBodyExists()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<!doctype html><html><head><script>x</script></head><body><div></div></body></html>");
        RunToScript(parser);
        Assert.False(tree.TryQuerySelector("body", out var body, out _) && body is not null);
        Assert.Equal(ParserStop.Finished, parser.Run(0, out _));
        Assert.Equal("<div></div>", Body(tree));
    }

    [Fact]
    public void WrittenMarkupIsParsedAtTheInsertionPointBeforeTheRestOfTheInput()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<body><script>w()</script><p id=after></p>");
        RunToScript(parser);

        parser.EnterScript();
        parser.Write("<div id=w1></div>");
        Assert.Equal(ParserStop.InsertionPoint, parser.RunToInsertionPoint(out _));
        // Written content is in the tree while the script still runs; the rest of the input is not.
        Assert.Equal("<script>w()</script><div id=\"w1\"></div>", Body(tree));
        parser.Write("<div id=w2></div>");
        Assert.Equal(ParserStop.InsertionPoint, parser.RunToInsertionPoint(out _));
        parser.ExitScript();

        Assert.Equal(ParserStop.Finished, parser.Run(0, out _));
        Assert.Equal("<script>w()</script><div id=\"w1\"></div><div id=\"w2\"></div><p id=\"after\"></p>", Body(tree));
    }

    [Fact]
    public void ATagSplitAcrossWritesIsParsedOnceItIsComplete()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<body><script>w()</script><p id=after></p>");
        RunToScript(parser);

        parser.EnterScript();
        parser.Write("<span id=s cla");
        parser.RunToInsertionPoint(out _);
        Assert.Equal("<script>w()</script>", Body(tree));
        parser.Write("ss=m>x</span>");
        parser.RunToInsertionPoint(out _);
        Assert.Equal("<script>w()</script><span id=\"s\" class=\"m\">x</span>", Body(tree));
        parser.ExitScript();

        parser.Run(0, out _);
        Assert.Equal("<script>w()</script><span id=\"s\" class=\"m\">x</span><p id=\"after\"></p>", Body(tree));
    }

    [Fact]
    public void WrittenTextFollowedByInputTextIsSplitAtTheInsertionPoint()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<body><script>w()</script>tail<p></p>");
        RunToScript(parser);

        parser.EnterScript();
        parser.Write("hello ");
        parser.RunToInsertionPoint(out _);
        Assert.Equal("<script>w()</script>hello ", Body(tree));
        parser.ExitScript();

        parser.Run(0, out _);
        Assert.Equal("<script>w()</script>hello tail<p></p>", Body(tree));
    }

    [Fact]
    public void AWrittenScriptStopsTheNestedRunAndItsOwnWritesGoRightAfterIt()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<body><script>outer()</script><p id=after></p>");
        RunToScript(parser);

        parser.EnterScript();
        parser.Write("<script>inner()</script><i></i>");
        Assert.Equal(ParserStop.Script, parser.RunToInsertionPoint(out var inner));
        Assert.Equal("inner()", tree.TextContent(inner));
        Assert.Equal("<script>outer()</script><script>inner()</script>", Body(tree));

        // The inner script runs and writes: its text goes just after it, before the rest of
        // what the outer script wrote.
        parser.EnterScript();
        parser.Write("<b></b>");
        Assert.Equal(ParserStop.InsertionPoint, parser.RunToInsertionPoint(out _));
        parser.ExitScript();

        Assert.Equal(ParserStop.InsertionPoint, parser.RunToInsertionPoint(out _));
        Assert.Equal("<script>outer()</script><script>inner()</script><b></b><i></i>", Body(tree));
        parser.Write("<u></u>");
        parser.RunToInsertionPoint(out _);
        parser.ExitScript();

        parser.Run(0, out _);
        Assert.Equal("<script>outer()</script><script>inner()</script><b></b><i></i><u></u><p id=\"after\"></p>", Body(tree));
    }

    [Fact]
    public void AnUnfinishedWrittenScriptTakesTheRestOfTheInputAsItsText()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<body><script>w()</script>x</script><p></p>");
        RunToScript(parser);
        parser.EnterScript();
        parser.Write("<script>a=1;");
        parser.RunToInsertionPoint(out _);
        parser.ExitScript();

        var written = RunToScript(parser);
        Assert.Equal("a=1;x", tree.TextContent(written));
        parser.Run(0, out _);
        Assert.Equal("<script>w()</script><script>a=1;x</script><p></p>", Body(tree));
    }

    [Fact]
    public void ATokenBudgetYieldsWithoutChangingTheResult()
    {
        const string Markup = "<!doctype html><table><tr><td>a<b>b<i>c</b>d</i></td></tr></table><script>s</script><select><option>x</select>text";
        var expected = HtmlParsing.ParseHtml(Markup);

        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, Markup);
        var yields = 0;
        while (true)
        {
            var stop = parser.Run(1, out _);
            if (stop == ParserStop.Finished)
            {
                break;
            }

            if (stop == ParserStop.Yield)
            {
                yields++;
            }
        }

        Assert.True(yields > 10);
        Assert.Equal(Html(expected), Html(tree));
    }

    [Fact]
    public void AnSvgScriptStopsTheParserToo()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<body><svg><script>s()</script><rect/></svg><p></p>");
        var script = RunToScript(parser);
        Assert.Equal(Namespaces.Svg, tree.GetNode(script)!.ElementName!.Value.Ns);
        Assert.Equal(ParserStop.Finished, parser.Run(0, out _));
    }

    [Fact]
    public void TheInsertionLogNamesEveryParsedNodeOnce()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<body><div id=a><p id=p1>t</p></div><script>s</script><div id=b></div>");
        RunToScript(parser);
        var ids = parser.TakeInsertedNodes()
            .Select(n => tree.GetNode(n)!.GetAttribute("id"))
            .Where(id => id is not null)
            .ToList();
        Assert.Equal(["a", "p1"], ids);
        parser.Run(0, out _);
        Assert.Contains(parser.TakeInsertedNodes(), n => tree.GetNode(n)!.GetAttribute("id") == "b");
    }

    [Fact]
    public void ParserHeldNodesSurviveACollectionWhileTheParseIsPaused()
    {
        var tree = new DomTree();
        var parser = DocumentParser.Begin(tree, "<body><div id=open><script>s</script><span id=late></span></div>");
        RunToScript(parser);
        // Script removes the element the parser is still inserting into.
        Assert.True(tree.TryQuerySelector("#open", out var open, out _));
        tree.RemoveChild(open!.Value);
        tree.CollectGarbage();
        parser.Run(0, out _);
        Assert.NotNull(tree.GetNode(open.Value));
        Assert.Equal("<script>s</script><span id=\"late\"></span>", tree.InnerHtml(open.Value));
    }
}
