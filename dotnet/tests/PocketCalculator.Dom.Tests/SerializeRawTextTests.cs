using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// SECURITY.md M9: HTML fragment serialization emits text raw only under HTML style, script,
/// xmp, iframe, noembed, noframes, plaintext and (scripting on) noscript. Everything else,
/// including RCDATA textarea and title and foreign style and script, is escaped, as Chromium
/// does. Rust's serializer emitted textarea/title and SVG style raw, which turned escaped text
/// into live markup on an innerHTML round trip.
/// </summary>
public class SerializeRawTextTests
{
    private static string BodyHtml(string markup)
    {
        var tree = HtmlParsing.ParseHtml("<body>" + markup + "</body>");
        return tree.InnerHtml(tree.QuerySelector("body")!.Value);
    }

    [Theory]
    [InlineData(
        "<title>&lt;/title&gt;&lt;img src=x onerror=alert(1)&gt;</title>",
        "<title>&lt;/title&gt;&lt;img src=x onerror=alert(1)&gt;</title>")]
    [InlineData(
        "<textarea>&lt;/textarea&gt;&lt;img src=x onerror=alert(1)&gt;</textarea>",
        "<textarea>&lt;/textarea&gt;&lt;img src=x onerror=alert(1)&gt;</textarea>")]
    [InlineData(
        "<svg><style>&lt;/style&gt;&lt;img src=x onerror=alert(1)&gt;</style></svg>",
        "<svg><style>&lt;/style&gt;&lt;img src=x onerror=alert(1)&gt;</style></svg>")]
    [InlineData(
        "<math><style>&lt;img&gt;</style></math>",
        "<math><style>&lt;img&gt;</style></math>")]
    [InlineData(
        "<svg><script>a &lt; b</script></svg>",
        "<svg><script>a &lt; b</script></svg>")]
    public void RcdataAndForeignTextIsEscaped(string markup, string expected) =>
        Assert.Equal(expected, BodyHtml(markup));

    [Theory]
    [InlineData("<script>if (a < b && c > d) {}</script>")]
    [InlineData("<style>a > b { content: \"&\" }</style>")]
    [InlineData("<xmp><b>x</b></xmp>")]
    [InlineData("<noembed><b>x</b></noembed>")]
    [InlineData("<noframes><b>x</b></noframes>")]
    [InlineData("<noscript><b>x</b></noscript>")]
    [InlineData("<iframe><b>x</b></iframe>")]
    public void HtmlRawTextElementsStayRaw(string markup) =>
        Assert.Equal(markup, BodyHtml(markup));

    [Fact]
    public void TitleRoundTripDoesNotCreateAnElement()
    {
        var tree = HtmlParsing.ParseHtml(
            "<body><title>&lt;/title&gt;&lt;img src=x onerror=alert(1)&gt;</title></body>");
        var html = tree.InnerHtml(tree.QuerySelector("body")!.Value);
        var reparsed = HtmlParsing.ParseHtml("<body>" + html + "</body>");
        Assert.Empty(reparsed.QuerySelectorAll("img"));
    }
}
