using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// <see cref="PreparedRender.HitTest"/> against Chromium 141's <c>elementFromPoint</c> and
/// <c>elementsFromPoint</c> on the same pages (1280x720 viewport). Not in crates/obscura-render,
/// which has no hit testing.
/// </summary>
public sealed class HitTestTests
{
    private static List<string> Hit(string html, float x, float y, bool all)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)!;
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        List<string> labels = [];
        foreach (NodeId id in prepared.HitTest(tree, scroll, x, y, all))
        {
            DomElementLabel(tree, id, labels);
        }

        return labels;
    }

    private static void DomElementLabel(DomTree tree, NodeId id, List<string> labels)
    {
        if (tree.GetNode(id) is { } node && node.AsElement() is { } element)
        {
            labels.Add(node.GetAttribute("id") ?? element.Name.Local);
        }
    }

    private const string LineBoxes =
        """
        <!doctype html><style>body{margin:0;font:16px/20px 'Liberation Sans'} p{margin:0}</style>
        <div id=c style="width:400px"><p id=p>ab</p><div id=o style="margin-top:-15px;margin-left:100px;width:100px;height:30px;background:red"></div>
        <p id=p2 style="text-align:right">cd</p><div id=o2 style="margin-top:-15px;margin-left:10px;width:100px;height:30px;background:red"></div></div>
        """;

    [Theory]
    // A later block's background overlapping a line box past its text is hit, as is one
    // overlapping a right-aligned line before its text; the text itself hits its block.
    [InlineData(150f, 8f, "o,p,c,body,html")]
    [InlineData(10f, 8f, "p,c,body,html")]
    [InlineData(50f, 45f, "o2,p2,c,body,html")]
    [InlineData(395f, 45f, "p2,c,body,html")]
    [InlineData(150f, 30f, "o,c,body,html")]
    public void ALineBoxIsHitOnlyWhereItHasContent(float x, float y, string expected) =>
        Assert.Equal(expected, string.Join(',', Hit(LineBoxes, x, y, all: true)));

    private const string Layers =
        """
        <!doctype html><html><body style="margin:0;font:16px/20px 'Liberation Sans'">
        <div id=c style="width:400px;background:#eee"><div id=f style="float:left;width:100px;height:60px;background:#0a0"></div><p id=p style="margin:0">Lorem <span id=s>ipsum dolor</span> sit amet consectetur adipiscing elit sed do eiusmod tempor</p></div>
        <div id=z1 style="position:absolute;left:10px;top:100px;width:100px;height:50px;z-index:2;background:red"></div>
        <div id=z2 style="position:absolute;left:50px;top:120px;width:100px;height:50px;z-index:1;background:blue"></div>
        <div id=e style="position:absolute;left:200px;top:200px;width:60px;height:60px;z-index:3;background:#888"><div id=e1 style="position:absolute;left:30px;top:30px;width:60px;height:60px;z-index:-5;background:#f8a"></div></div>
        <div id=pe style="position:absolute;left:200px;top:100px;width:100px;height:50px;pointer-events:none;background:#ccc"><div id=pea style="pointer-events:auto;width:40px;height:20px;background:#333"></div></div>
        </body></html>
        """;

    [Theory]
    [InlineData(5f, 5f, "f")] // in the float, not the paragraph after it
    [InlineData(150f, 5f, "s")] // text in a span hits the span
    [InlineData(350f, 30f, "p")] // text directly in the paragraph hits it
    [InlineData(60f, 130f, "z1")] // z-index 2 over z-index 1
    [InlineData(120f, 160f, "z2")]
    [InlineData(240f, 240f, "e1")] // a negative child is above its stacking context's background
    [InlineData(210f, 105f, "pea")] // pointer-events: auto inside none
    [InlineData(260f, 120f, "html")] // pointer-events: none passes through to the canvas
    public void TheTopmostElementFollowsPaintOrder(float x, float y, string expected) =>
        Assert.Equal(expected, Hit(Layers, x, y, all: false).Single());

    [Fact]
    public void NothingIsHitOutsideTheViewport()
    {
        Assert.Empty(Hit(Layers, -1f, 5f, all: true));
        Assert.Empty(Hit(Layers, 5f, 720f, all: false));
    }
}
