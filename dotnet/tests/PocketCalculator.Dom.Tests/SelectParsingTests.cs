// SECURITY.md M11 replaced the tree builder; these pin the customizable select parser changes
// it implements, as measured in Chromium 141. No counterpart in crates/obscura-dom.
using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// Chromium parses <c>select</c> by the 2025 specification: the "in select" insertion modes are
/// gone, a select keeps arbitrary content, and it bounds "has an element in scope". The expected
/// markup is Chromium's <c>documentElement.outerHTML</c> for each input.
/// </summary>
public class SelectParsingTests
{
    [Theory]
    [InlineData("<select><div>x</div><hr></select>", "<html><head></head><body><select><div>x</div><hr></select></body></html>")]
    [InlineData("<select><option><hr>", "<html><head></head><body><select><option></option><hr></select></body></html>")]
    [InlineData("<p><select><p>x", "<html><head></head><body><p><select><p>x</p></select></p></body></html>")]
    [InlineData("<div><select></div>x", "<html><head></head><body><div><select>x</select></div></body></html>")]
    [InlineData("<select><div><select>x", "<html><head></head><body><select><div></div></select>x</body></html>")]
    [InlineData("<b><select><i>a<input>b</b>c", "<html><head></head><body><b><select><i>a</i></select><i><input>b</i></b><i>c</i></body></html>")]
    [InlineData("<b><select></select><div>x</b>y", "<html><head></head><body><b><select></select></b><div><b>x</b>y</div></body></html>")]
    [InlineData("<table><td><select><td>x", "<html><head></head><body><table><tbody><tr><td><select></select></td><td>x</td></tr></tbody></table></body></html>")]
    [InlineData("<select><svg><select>x", "<html><head></head><body><select><svg><select>x</select></svg></select></body></html>")]
    [InlineData("<select><textarea>x</textarea>y", "<html><head></head><body><select><textarea>x</textarea>y</select></body></html>")]
    public void SelectContentParsesAsInChromium(string html, string expected)
    {
        var tree = HtmlParsing.ParseHtml(html);
        Assert.Equal(expected, tree.OuterHtml(tree.QuerySelector("html")!.Value));
    }

    [Fact]
    public void AnInputInASelectFragmentIsDropped()
    {
        var tree = HtmlParsing.ParseFragmentWithContext("<input><option>a", QualName.Html("select"));
        Assert.Equal("<option>a</option>", tree.InnerHtml(tree.FragmentRoot()));
    }
}
