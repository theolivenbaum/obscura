using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md M10: the markdown dump (CLI <c>--dump markdown</c>, MCP, <c>LP.getMarkdown</c>)
/// must not turn page text into markup a downstream renderer executes, and must not emit
/// links with script-capable schemes or let link text and URLs break out of their syntax.
/// </summary>
public sealed class MarkdownEscapingTests
{
    private static string Markdown(string body)
    {
        using var fixture = RuntimeFixture.Setup("<html><body>" + body + "</body></html>");
        return fixture.Runtime.Evaluate(MarkdownScript.HtmlToMarkdown)!.GetValue<string>();
    }

    [Fact]
    public void TextThatLooksLikeMarkupIsEscaped()
    {
        var md = Markdown("<p>&lt;script&gt;alert(1)&lt;/script&gt; &lt;img src=x onerror=alert(1)&gt;</p>");
        Assert.DoesNotContain("<script>", md, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", md, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", md, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeSpansCannotBeBrokenOutOf()
    {
        var md = Markdown("<p><code>a` &lt;img src=x onerror=alert(1)&gt; `b</code></p>");
        Assert.DoesNotContain("<img", md, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData(" java\tscript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    public void UnsafeLinkSchemesKeepOnlyTheText(string href)
    {
        var md = Markdown("<p><a href=\"" + href.Replace("<", "&lt;").Replace(">", "&gt;") + "\">click me</a></p>");
        Assert.Equal("click me", md);
    }

    [Fact]
    public void UnsafeImageSchemesKeepOnlyTheAltText()
    {
        var md = Markdown("<p><img src=\"javascript:alert(1)\" alt=\"logo\"></p>");
        Assert.Equal("logo", md);
    }

    [Fact]
    public void LinkTextAndAltCannotCloseTheLinkEarly()
    {
        var md = Markdown("<p><a href=\"https://good.test/\">x](https://evil.test/) [y</a></p>");
        Assert.Equal("[x\\](https://evil.test/) \\[y](https://good.test/)", md);

        var img = Markdown("<p><img src=\"https://good.test/a.png\" alt=\"a](https://evil.test/\"></p>");
        Assert.Equal("![a\\](https://evil.test/](https://good.test/a.png)", img);
    }

    [Fact]
    public void UrlsCannotCloseTheDestinationEarly()
    {
        var md = Markdown("<p><a href=\"https://good.test/a b)(c<d>\">t</a></p>");
        Assert.Equal("[t](https://good.test/a%20b%29%28c%3Cd%3E)", md);
    }

    [Fact]
    public void OrdinaryLinksAndImagesAreUnchanged()
    {
        var md = Markdown(
            "<p><a href=\"https://x.test/path?q=1#f\">link</a> <a href=\"/rel\">rel</a> "
            + "<a href=\"mailto:a@b.test\">mail</a> <img src=\"https://x.test/i.png\" alt=\"pic\"></p>");
        Assert.Equal(
            "[link](https://x.test/path?q=1#f) [rel](/rel) [mail](mailto:a@b.test) ![pic](https://x.test/i.png)",
            md);
    }
}
