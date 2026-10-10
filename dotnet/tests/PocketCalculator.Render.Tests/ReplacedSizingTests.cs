// Chromium-verified facts for replaced-element sizing and for `aspect-ratio`: a definite
// size in an axis wins over the ratio, which only fills an axis that is auto (CSS 2.1
// 10.3.2/10.6.2, CSS Sizing 4 "aspect-ratio"). The values are Chromium 141's, measured on the
// pages scripts/replaced-sizing-conformance/gen-pages.mjs writes to render-repros/replaced-sizing.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class ReplacedSizingTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    private static void AssertSize(DomLayout laid, DomTree tree, string id, float width, float height)
    {
        Rect rect = laid.Rects[Id(tree, id)];
        Assert.True(
            MathF.Abs(rect.Width - width) < 0.01f && MathF.Abs(rect.Height - height) < 0.01f,
            $"{id}: expected {width}x{height}, got {rect.Width}x{rect.Height}");
    }

    /// <summary>
    /// capcut.com: a 150x36 logo with <c>width:120px; height:24px</c> was 120x28.8, the
    /// height taken from the image's ratio. Chromium uses both specified sizes, inline,
    /// block-level, floated, and inside an inline-block.
    /// </summary>
    [Fact]
    public void ASpecifiedHeightWinsOverAnImagesRatio()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0} .w{width:400px}</style>
            <div class=w>text <img id=inline src="a.png" style="width:120px;height:24px;object-fit:contain"> tail</div>
            <div class=w><img id=block src="a.png" style="display:block;width:120px;height:24px"></div>
            <div class=w><img id=float src="a.png" style="float:left;width:120px;height:24px"></div>
            <div class=w><div style="display:inline-block"><img id=nested src="a.png" style="width:120px;height:24px"></div></div>
            <div class=w><img id=bb src="a.png" style="display:block;width:120px;height:24px;box-sizing:border-box;padding:4px;border:2px solid"></div>
            <div class=w><img id=maxh src="a.png" style="display:block;width:120px;height:24px;max-height:20px"></div>
            """);
        Dictionary<NodeId, (float Width, float Height)> intrinsic = [];
        foreach (string id in new[] { "inline", "block", "float", "nested", "bb", "maxh" })
        {
            intrinsic[Id(tree, id)] = (150f, 36f);
        }

        DomLayout laid = RenderDom.LayoutDomWithImages(tree, (1280f, 720f), intrinsic);
        AssertSize(laid, tree, "inline", 120f, 24f);
        AssertSize(laid, tree, "block", 120f, 24f);
        AssertSize(laid, tree, "float", 120f, 24f);
        AssertSize(laid, tree, "nested", 120f, 24f);
        AssertSize(laid, tree, "bb", 120f, 24f);
        AssertSize(laid, tree, "maxh", 120f, 20f);
    }

    /// <summary>
    /// The same rule for a non-replaced box with an authored <c>aspect-ratio</c>: with both
    /// sizes definite the ratio is ignored (Chromium: 120x24, the port gave 120x60).
    /// </summary>
    [Fact]
    public void AnAspectRatioDoesNotOverrideTwoDefiniteSizes()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0}</style>
            <div id=block style="aspect-ratio:2;width:120px;height:24px"></div>
            <div><span id=ib style="display:inline-block;aspect-ratio:2;width:120px;height:24px"></span></div>
            <div style="display:flex"><div id=flex style="aspect-ratio:2;width:120px;height:24px"></div></div>
            <div id=heightOnly style="aspect-ratio:2;height:24px"></div>
            <div id=widthOnly style="aspect-ratio:2;width:120px"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        AssertSize(laid, tree, "block", 120f, 24f);
        AssertSize(laid, tree, "ib", 120f, 24f);
        AssertSize(laid, tree, "flex", 120f, 24f);
        AssertSize(laid, tree, "heightOnly", 48f, 24f);
        AssertSize(laid, tree, "widthOnly", 120f, 60f);
    }
}
