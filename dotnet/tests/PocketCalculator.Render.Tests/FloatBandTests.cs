// Float placement against Chromium 141. No counterpart in crates/obscura-render.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Render.Tests;

public class FloatBandTests
{
    [Fact]
    public void LeftRightLeftPutsTheThirdFloatBesideTheFirst()
    {
        // Chromium 141: 0,0 / 900,0 / 100,0. The float-zone approximation put the third at 0,400.
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><body style=\"margin:0;width:1000px\">"
            + "<div id=a style=\"float:left;width:100px;height:400px\"></div>"
            + "<div id=b style=\"float:right;width:100px;height:400px\"></div>"
            + "<div id=c style=\"float:left;width:100px;height:400px\"></div></body>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Assert.Equal(new Rect(0f, 0f, 100f, 400f), laid.Rects[tree.GetElementById("a")!.Value]);
        Assert.Equal(new Rect(900f, 0f, 100f, 400f), laid.Rects[tree.GetElementById("b")!.Value]);
        Assert.Equal(new Rect(100f, 0f, 100f, 400f), laid.Rects[tree.GetElementById("c")!.Value]);
    }

    [Fact]
    public void HeaderFloatsOnBothSidesLeaveTheirBlockEmpty()
    {
        // Chromium 141: a block holding only a left and a right float is 0 tall, so the
        // paragraph after it starts at the block's own top. It used to be 18 tall.
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><body style=\"margin:8px;font:16px 'Liberation Sans'\">"
            + "<div id=h><div style=\"float:left\">Logo here</div><div style=\"float:right\">Nav</div></div>"
            + "<p id=p style=\"margin:0\">Paragraph</p></body>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Assert.Equal(0f, laid.Rects[tree.GetElementById("h")!.Value].Height);
        Assert.Equal(8f, laid.Rects[tree.GetElementById("p")!.Value].Y);
    }
}
