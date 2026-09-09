using Obscura.Dom;

namespace Obscura.Dom.Tests;

/// <summary>
/// Port of the <c>#[cfg(test)] mod tests</c> block in crates/obscura-dom/src/serialize.rs.
/// </summary>
public class SerializeTests
{
    [Fact]
    public void TestOuterHtml()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="test"><p>Hello</p></div>""");
        var div = tree.GetElementById("test")!.Value;
        var html = tree.OuterHtml(div);
        Assert.Contains("""<div id="test">""", html, StringComparison.Ordinal);
        Assert.Contains("<p>Hello</p>", html, StringComparison.Ordinal);
        Assert.Contains("</div>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestInnerHtml()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="test"><p>Hello</p><p>World</p></div>""");
        var div = tree.GetElementById("test")!.Value;
        var html = tree.InnerHtml(div);
        Assert.Contains("<p>Hello</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p>World</p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<div", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSerializeAttributes()
    {
        var tree = HtmlParsing.ParseHtml("""<a href="https://example.com" class="link">Click</a>""");
        var a = tree.QuerySelector("a")!.Value;
        var html = tree.OuterHtml(a);
        Assert.Contains("href=\"https://example.com\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"link\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSerializeSpecialChars()
    {
        var tree = HtmlParsing.ParseHtml("<p>Hello &amp; World &lt;3</p>");
        var p = tree.QuerySelector("p")!.Value;
        var html = tree.OuterHtml(p);
        Assert.Contains("&amp;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestVoidElements()
    {
        var tree = HtmlParsing.ParseHtml("""<img src="test.png"><br>""");
        var img = tree.QuerySelector("img")!.Value;
        var html = tree.OuterHtml(img);
        Assert.Contains("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</img>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentSerializationNeutralizesAllTerminatorForms()
    {
        // A comment ends on any of "-->", "--!>", a leading ">", or a leading "->".
        // document.createComment(...) accepts arbitrary strings, so a scripted comment can carry
        // each closing form followed by a real tag. Serializing must keep the payload inside a
        // single comment; if it closes early, the trailing "<img>" becomes live markup (mXSS).
        string[] payloads =
        [
            "><img src=x onerror=alert(1)>", // leading ">" abrupt-closes empty comment
            "-><img src=x>",                 // leading "->" abrupt-closes
            "a--!><img src=x>",              // internal "--!>" closes (incorrectly-closed)
            "a--><img src=x>",               // internal "-->" closes (the previously-fixed form)
        ];

        foreach (var payload in payloads)
        {
            var tree = HtmlParsing.ParseHtml("""<div id="host"></div>""");
            var host = tree.GetElementById("host")!.Value;
            var comment = tree.NewNode(NodeData.Comment(payload));
            tree.AppendChild(host, comment);

            var serialized = tree.OuterHtml(host);

            // Re-parsing the serialized markup must not surface an <img>: the payload has to stay
            // inside the comment.
            var reparsed = HtmlParsing.ParseHtml(serialized);
            Assert.True(
                reparsed.QuerySelector("img") is null,
                $"payload {payload} escaped the comment; serialized = {serialized}");

            // And the serialized comment data must carry no raw ">".
            var inner = serialized[(serialized.IndexOf("<!--", StringComparison.Ordinal) + 4)..];
            inner = inner[..inner.IndexOf("-->", StringComparison.Ordinal)];
            Assert.False(
                inner.Contains('>', StringComparison.Ordinal),
                $"comment data still contains a raw '>': {serialized}");
        }
    }

    [Fact]
    public void HostSerializationExcludesItsNativeShadowTree()
    {
        var tree = HtmlParsing.ParseHtml("""<x-card id="host"><span id="light">light</span></x-card>""");
        var host = tree.GetElementById("host")!.Value;
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadow = tree.NewNode(NodeData.Element(QualName.Html("strong")));
        var shadowText = tree.NewNode(NodeData.Text("shadow"));
        tree.AppendChild(root, shadow);
        tree.AppendChild(shadow, shadowText);

        Assert.Equal("""<span id="light">light</span>""", tree.InnerHtml(host));
        Assert.Equal(
            """<x-card id="host"><span id="light">light</span></x-card>""",
            tree.OuterHtml(host));
        Assert.Equal("<strong>shadow</strong>", tree.InnerHtml(root));
    }
}
