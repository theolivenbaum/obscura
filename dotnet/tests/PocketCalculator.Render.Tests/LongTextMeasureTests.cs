// SECURITY.md H5: text measurement sized a stackalloc by the page's text, so a long enough
// option label overflowed the stack and killed the process.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Render.Tests;

public class LongTextMeasureTests
{
    [Fact]
    public void MeasuringMegabytesOfTextDoesNotOverflowTheStack()
    {
        string text = new('m', 5 * 1024 * 1024);
        float width = DomTextMeasure.MeasureText(text, 16f, isBold: false, "sans-serif");
        float one = DomTextMeasure.MeasureText("m", 16f, isBold: false, "sans-serif");
        Assert.True(width > one * 1024 * 1024, $"width {width}");
    }

    [Fact]
    public void ShortAndLongTextMeasureAlike()
    {
        string shortText = new('a', 200);
        string longText = new('a', 400);
        float shortWidth = DomTextMeasure.MeasureText(shortText, 16f, isBold: true, "sans-serif");
        float longWidth = DomTextMeasure.MeasureText(longText, 16f, isBold: true, "sans-serif");
        Assert.Equal(shortWidth * 2f, longWidth, 0.5f);
    }

    [Fact]
    public void SelectWithAHugeOptionLabelLaysOut()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<select><option>" + new string('x', 5 * 1024 * 1024) + "</option></select>");
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Assert.True(laid.Rects.ContainsKey(tree.QuerySelector("select")!.Value));
    }
}
