using Xunit;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// U+0000 in markup. AngleSharp's tokenizer drops it in the data state, so foreign content
/// (which turns it into U+FFFD) lost the character; the tree builder hands it through as a
/// stand-in. Expected values are Chromium 141's <c>DOMParser</c> for the same input.
/// </summary>
public sealed class NullCharacterParsingTests
{
    [Fact]
    public void NullIsDroppedInHtmlAndReplacedElsewhere()
    {
        const string source = "a\0b<textarea>x\0y</textarea><!--c\0d--><p t=\"u\0v\"></p><svg>\0<![CDATA[q\0r]]></svg>"
            + "<x\0y></x\0y><table>\0<tr><td>1</td></tr></table><math><mi>\0</mi></math>";
        var tree = HtmlParsing.ParseHtml(source);
        var body = tree.QuerySelector("body")!.Value;
        Assert.Equal(
            "ab<textarea>x\uFFFDy</textarea><!--c\uFFFDd--><p t=\"u\uFFFDv\"></p><svg>\uFFFDq\uFFFDr</svg>"
            + "<x\uFFFDy></x\uFFFDy><table><tbody><tr><td>1</td></tr></tbody></table><math><mi></mi></math>",
            tree.InnerHtml(body));
    }

    [Fact]
    public void InputWithoutNullIsUntouched()
    {
        // The stand-in is a lone low surrogate; without U+0000 in the input it is ordinary text.
        var tree = HtmlParsing.ParseHtml("<p>a\uDFFFb</p>");
        var p = tree.QuerySelector("p")!.Value;
        Assert.Equal("a\uDFFFb", tree.TextContent(p));
    }
}
