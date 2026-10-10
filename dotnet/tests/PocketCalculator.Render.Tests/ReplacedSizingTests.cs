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
    /// Canvas, video, iframe, embed and object are inline by default, so they sit on the text
    /// line after "text " (30.23px in 16px Liberation Sans) instead of starting a block.
    /// </summary>
    [Fact]
    public void CanvasVideoAndIframeAreAtomicInlines()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0} body{font:16px/20px 'Liberation Sans'} .w{width:400px}</style>
            <div class=w>text <canvas id=canvas></canvas> tail</div>
            <div class=w>text <video id=video></video> tail</div>
            <div class=w>text <iframe id=iframe></iframe> tail</div>
            <div class=w>text <object id=object></object> tail</div>
            <div class=w>text <audio id=audio></audio> tail</div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        foreach ((string id, float width, float height) in new[]
        {
            ("canvas", 300f, 150f), ("video", 300f, 150f), ("iframe", 304f, 154f), ("object", 300f, 150f),
        })
        {
            Rect rect = laid.Rects[Id(tree, id)];
            Assert.True(MathF.Abs(rect.X - 30.23f) < 0.5f, $"{id} x: {rect.X}");
            AssertSize(laid, tree, id, width, height);
        }

        // audio:not([controls]) { display: none }
        Assert.False(laid.Rects.TryGetValue(Id(tree, "audio"), out Rect audio) && audio.Width > 0f);
    }

    /// <summary>
    /// A video without metadata and an iframe have a natural size but no natural ratio: a
    /// definite width keeps the natural 150px height. A canvas has one (its 300x150 bitmap),
    /// so a height or a percentage width transfers. A block-level replaced box with an auto
    /// width takes its natural width, not the containing block's.
    /// </summary>
    [Fact]
    public void NaturalRatiosOfVideoIframeAndCanvas()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0} .w{width:400px} .w > *{display:block}</style>
            <div class=w><video id=videoW style="width:120px"></video></div>
            <div class=w><iframe id=iframeW style="width:120px"></iframe></div>
            <div class=w><video id=videoMaxH style="max-height:20px"></video></div>
            <div class=w><iframe id=iframeAttrs width=120 height=24 style="width:60px;height:auto"></iframe></div>
            <div class=w><canvas id=canvasAuto></canvas></div>
            <div class=w><canvas id=canvasH style="height:24px"></canvas></div>
            <div class=w><canvas id=canvasPct style="width:50%"></canvas></div>
            <div class=w style="display:flex"><video id=flexVideoW style="width:120px;display:block"></video></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        AssertSize(laid, tree, "videoW", 120f, 150f);
        AssertSize(laid, tree, "iframeW", 124f, 154f);
        AssertSize(laid, tree, "videoMaxH", 300f, 20f);
        AssertSize(laid, tree, "iframeAttrs", 64f, 154f);
        AssertSize(laid, tree, "canvasAuto", 300f, 150f);
        AssertSize(laid, tree, "canvasH", 48f, 24f);
        AssertSize(laid, tree, "canvasPct", 200f, 100f);
        AssertSize(laid, tree, "flexVideoW", 120f, 150f);
    }

    /// <summary>
    /// A natural ratio applies to the content box: <c>height:24px; box-sizing:border-box;
    /// padding:3px</c> on a 150x36 image is (24 - 6) x 150/36 + 6 = 81 wide. And min/max
    /// constraints transfer only into an auto axis, a transferred minimum capped by that
    /// axis's maximum.
    /// </summary>
    [Fact]
    public void TheNaturalRatioAppliesToTheContentBoxAndLimitsTransferOnlyIntoAutoAxes()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0} .w{width:400px} img{display:block}</style>
            <div class=w><img id=borderBox src="a.png" style="height:24px;box-sizing:border-box;padding:3px"></div>
            <div class=w><img id=minWMaxH src="a.png" style="min-width:200px;max-height:20px"></div>
            <div class=w><img id=whMinW src="a.png" style="width:120px;height:24px;min-width:200px"></div>
            <div class=w><img id=wMaxH src="a.png" style="width:120px;max-height:20px"></div>
            """);
        Dictionary<NodeId, (float Width, float Height)> intrinsic = [];
        foreach (string id in new[] { "borderBox", "minWMaxH", "whMinW", "wMaxH" })
        {
            intrinsic[Id(tree, id)] = (150f, 36f);
        }

        DomLayout laid = RenderDom.LayoutDomWithImages(tree, (1280f, 720f), intrinsic);
        AssertSize(laid, tree, "borderBox", 81f, 24f);
        AssertSize(laid, tree, "minWMaxH", 200f, 20f);
        AssertSize(laid, tree, "whMinW", 200f, 24f);
        AssertSize(laid, tree, "wMaxH", 120f, 20f);
    }

    /// <summary>
    /// A flex item's min/max transfer only into its auto axes; <c>aspect-ratio: auto 1</c>
    /// keeps the natural ratio; an image button is sized as its image.
    /// </summary>
    [Fact]
    public void FlexLimitsAutoRatioAndImageButtons()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0} .w{width:400px}</style>
            <div class=w style="display:flex;align-items:flex-start"><img id=flexMaxH src="a.png" style="width:120px;height:24px;max-height:20px"></div>
            <div class=w style="display:flex;align-items:flex-start"><img id=flexMinW src="a.png" style="width:120px;height:24px;min-width:200px"></div>
            <div class=w><img id=autoRatio src="a.png" style="display:block;aspect-ratio:auto 1;width:120px"></div>
            <div class=w><canvas id=canvasAutoRatio style="display:block;aspect-ratio:auto 1;width:120px"></canvas></div>
            <div class=w><input type=image id=button src="a.png"></div>
            <div class=w><input type=image id=buttonSized src="a.png" style="width:120px;height:24px"></div>
            """);
        Dictionary<NodeId, (float Width, float Height)> intrinsic = [];
        foreach (string id in new[] { "flexMaxH", "flexMinW", "autoRatio", "button", "buttonSized" })
        {
            intrinsic[Id(tree, id)] = (150f, 36f);
        }

        DomLayout laid = RenderDom.LayoutDomWithImages(tree, (1280f, 720f), intrinsic);
        AssertSize(laid, tree, "flexMaxH", 120f, 20f);
        AssertSize(laid, tree, "flexMinW", 200f, 24f);
        AssertSize(laid, tree, "autoRatio", 120f, 29f); // 28.8, snapped
        AssertSize(laid, tree, "canvasAutoRatio", 120f, 60f);
        AssertSize(laid, tree, "button", 150f, 36f);
        AssertSize(laid, tree, "buttonSized", 120f, 24f);
    }

    /// <summary>
    /// capcut.com's card images: an inline image's percentage height resolves against the
    /// definite height of the block holding its line (Chromium 176x104; the port laid it out
    /// at its ratio height, 99px). Padding comes off a border-box block's height.
    /// </summary>
    [Fact]
    public void AnInlineImagesPercentageHeightResolvesAgainstItsBlock()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0}</style>
            <div style="width:176px;height:104px;overflow:hidden"><img id=card src="a.png" style="width:100%;height:100%;object-fit:cover"></div>
            <div style="width:400px;height:100px">text <img id=half src="a.png" style="height:50%"> tail</div>
            <div style="width:400px;height:100px;box-sizing:border-box;padding:10px">text <img id=padded src="a.png" style="height:50%"></div>
            """);
        Dictionary<NodeId, (float Width, float Height)> intrinsic = [];
        foreach (string id in new[] { "card", "half", "padded" })
        {
            intrinsic[Id(tree, id)] = (150f, 36f);
        }

        DomLayout laid = RenderDom.LayoutDomWithImages(tree, (1280f, 720f), intrinsic);
        AssertSize(laid, tree, "card", 176f, 104f);
        AssertSize(laid, tree, "half", 209f, 50f); // 208.33 from x 30.23, edges snapped
        AssertSize(laid, tree, "padded", 167f, 40f); // 166.66, snapped
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
