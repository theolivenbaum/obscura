// Client rects of ordinary inline boxes, checked against Chromium 141. No counterpart in
// crates/obscura-render: the reference reports no box for a spliced inline wrapper.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class InlineClientRectTests
{
    private static (DomTree Tree, DomLayout Laid) Lay(string body)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><html><body style=\"margin:8px;font:16px 'Liberation Sans'\">"
            + body + "</body></html>");
        return (tree, RenderDom.LayoutDom(tree, (1280f, 720f)));
    }

    private static NodeId Id(DomTree tree, string id) => tree.QuerySelectorAll("#" + id).First();

    private static void Near(float expected, float actual) =>
        Assert.True(MathF.Abs(expected - actual) < 0.1f, $"expected {expected}, got {actual}");

    private static void AssertRect(Rect actual, float x, float y, float width, float height)
    {
        Near(x, actual.X);
        Near(y, actual.Y);
        Near(width, actual.Width);
        Near(height, actual.Height);
    }

    [Fact]
    public void LinkInMixedBlockHasItsLineBox()
    {
        // Chromium 141: 57.8,8,71.1,17 for the link, 57.8,44,81.8,17 for the span. The run
        // used to fold from the block's spliced text, so neither owned a box (0,0,0,0).
        (DomTree tree, DomLayout laid) = Lay(
            "<div>before <a id=a href=#>mixed link</a><div>block</div></div>"
            + "<div>before <span id=s>mixed span</span><div>block</div></div>");
        AssertRect(laid.Rects[Id(tree, "a")], 57.81f, 8f, 71.13f, 17f);
        AssertRect(laid.Rects[Id(tree, "s")], 57.81f, 44f, 81.82f, 17f);
    }

    [Fact]
    public void NestedInlineInMixedBlockKeepsBothBoxes()
    {
        // Chromium 141: the link and the span inside it both at 38.23,8,35.59,17.
        (DomTree tree, DomLayout laid) = Lay(
            "<div>text <a id=a href=#><span id=s>deep</span></a><div>block</div></div>");
        AssertRect(laid.Rects[Id(tree, "a")], 38.23f, 8f, 35.59f, 17f);
        AssertRect(laid.Rects[Id(tree, "s")], 38.23f, 8f, 35.59f, 17f);
    }

    [Fact]
    public void WrappedSpanFragmentsStopBeforeHangingSpace()
    {
        // Chromium 141: 68.48,50,36.47,17 ; 8,68,80.92,17 ; 8,86,81.84,17 relative to a first
        // line at y 8. The continuing fragments used to run over the space the wrap leaves.
        (DomTree tree, DomLayout laid) = Lay(
            "<p style=\"width:120px;margin:0\">one two <span id=s>three four five six seven eight</span> nine</p>");
        List<Rect> fragments = laid.InlineFragments[Id(tree, "s")];
        Assert.Equal(3, fragments.Count);
        AssertRect(fragments[0], 68.48f, 8f, 36.47f, 17f);
        AssertRect(fragments[1], 8f, 26f, 80.91f, 17f);
        AssertRect(fragments[2], 8f, 44f, 81.84f, 17f);
        AssertRect(laid.Rects[Id(tree, "s")], 8f, 8f, 96.95f, 53f);
    }

    [Fact]
    public void RightToLeftSpanHasItsGlyphWidth()
    {
        // Chromium 141: 1235,8,37,17. The logical start of a right-to-left line is its right
        // edge, so the fragment used to come out 0 wide.
        (DomTree tree, DomLayout laid) = Lay(
            "<div style=\"direction:rtl\"><span id=s>مرحبا</span></div>");
        AssertRect(laid.Rects[Id(tree, "s")], 1235.01f, 8f, 36.99f, 17f);
    }

    [Fact]
    public void WrapperOfAnImageGetsTheImageExtent()
    {
        // The run holds a replaced element, so it does not fold into one shaped item. Chromium
        // 141 reports the link as the image's width over the link's font box (20x17); the
        // union of the content is the image's box. Either is a target a click can hit.
        (DomTree tree, DomLayout laid) = Lay(
            "<p><a id=a href=#><img id=i width=20 height=20></a></p>");
        Rect link = laid.Rects[Id(tree, "a")];
        Rect image = laid.Rects[Id(tree, "i")];
        Assert.Equal(image, link);
        Assert.Equal(20f, link.Width);
    }

    [Fact]
    public void ClosingEdgesAtTheEndMoveOnlyTheLastWord()
    {
        // Chromium 141: 300 nested `padding:0 3px;margin:0 2px` spans of "x " at 16px/20px
        // Liberation Serif take 6 lines; the innermost sits alone on the last one at 10,109 with
        // every closing edge after it. The retry used to pay for all 1500px of closing edges on
        // each candidate line, and made 60 lines.
        System.Text.StringBuilder html = new();
        for (int i = 0; i < 300; i++)
        {
            html.Append("<span id=n").Append(i).Append(" style=\"padding:0 3px;margin:0 2px\">x ");
        }

        for (int i = 0; i < 300; i++)
        {
            html.Append("</span>");
        }

        (DomTree tree, DomLayout laid) = Lay(
            "<div id=host style=\"font:16px/20px 'Liberation Serif'\">" + html + "</div>");
        Assert.Equal(120f, laid.Rects[Id(tree, "host")].Height);
        AssertRect(laid.Rects[Id(tree, "n299")], 10f, 109f, 14f, 17f);
        Assert.Equal(6, laid.InlineFragments[Id(tree, "n0")].Count);
    }
}
