using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// List markers against Chromium 141: the ink box of each marker (its dark pixels, the item's
/// text made transparent) and where an inside marker leaves the item's text. Measured on the
/// same page at a 1280x720 viewport; ink boxes agree within 1px (anti-aliasing).
/// </summary>
public sealed class ListMarkerTests
{
    private const string Page =
        """
        <!doctype html><html><head><style>
        html, body { margin: 0; background: #fff; }
        body { font: 32px/40px 'Liberation Sans'; }
        ul, ol { margin: 0; }
        li { color: #000; }
        li span { color: transparent; }
        .box { width: 500px; }
        </style></head><body>
        <div class=box><ul id=u1><li><span id=t1>disc</span></li></ul>
        <ol id=o1><li><span id=t2>decimal</span></li></ol>
        <ul id=u2 style="list-style-position:inside"><li><span id=t3>inside</span></li></ul>
        <ol id=o2 style="list-style-position:inside"><li><span id=t4>inside</span></li></ol>
        <ul id=u3 dir=rtl><li><span id=t5>rtl</span></li></ul>
        <ol id=o3 dir=rtl><li><span id=t6>rtl</span></li></ol>
        <ul id=u4 style="list-style-type:square"><li><span id=t7>square</span></li></ul>
        <ul id=u5 style="list-style-type:circle;font-size:16px;line-height:20px"><li><span id=t8>small</span></li></ul>
        <ol id=o5 style="font-size:16px;line-height:20px" start=10><li><span id=t9>small</span></li></ol>
        </div>
        </body></html>
        """;

    // Chromium 141: [x0, y0, x1, y1) of each list item's dark pixels, in list order.
    private static readonly int[][] ChromiumInk =
    [
        [14, 17, 24, 27],
        [7, 49, 28, 71],
        [40, 97, 50, 107],
        [43, 129, 63, 151],
        [476, 177, 486, 187],
        [472, 209, 494, 231],
        [14, 257, 24, 267],
        [24, 288, 29, 293],
        [15, 303, 34, 315],
    ];

    private static (DomTree Tree, PreparedRender Prepared, Pixmap Pixmap) Render()
    {
        DomTree tree = HtmlParsing.ParseHtml(Page);
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)!;
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        Pixmap pixmap = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, scroll)!;
        return (tree, prepared, pixmap);
    }

    [Fact]
    public void MarkersInkWhereChromiumsDo()
    {
        (DomTree tree, PreparedRender prepared, Pixmap pixmap) = Render();
        using (pixmap)
        {
            List<NodeId> items = [.. tree.Descendants(tree.Document).Where(id => DomElementName(tree, id) == "li")];
            Assert.Equal(ChromiumInk.Length, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                Rect row = prepared.DocumentRect(items[i])!.Value;
                int[] ink = InkBox(pixmap, (int)row.Y, (int)(row.Y + row.Height));
                for (int k = 0; k < 4; k++)
                {
                    Assert.True(
                        Math.Abs(ink[k] - ChromiumInk[i][k]) <= 1,
                        $"item {i}: ink [{string.Join(',', ink)}], Chromium [{string.Join(',', ChromiumInk[i])}]");
                }
            }
        }
    }

    [Fact]
    public void AnInsideMarkerIndentsTheFirstLine()
    {
        // Chromium 141: an inside disc takes its box less 1px plus 1em (43px at 32px), an
        // inside "1. " its own width (35.58); outside markers and a right-to-left list's
        // padding-inline-start leave the text at the content edge.
        (DomTree tree, PreparedRender prepared, Pixmap pixmap) = Render();
        using (pixmap)
        {
            Assert.Equal(40f, prepared.DocumentRect(tree.GetElementById("t1")!.Value)!.Value.X, 0.05f);
            Assert.Equal(83f, prepared.DocumentRect(tree.GetElementById("t3")!.Value)!.Value.X, 0.05f);
            Assert.Equal(75.58f, prepared.DocumentRect(tree.GetElementById("t4")!.Value)!.Value.X, 0.05f);
            Assert.Equal(433.34f, prepared.DocumentRect(tree.GetElementById("t5")!.Value)!.Value.X, 0.05f);
        }
    }

    private static string? DomElementName(DomTree tree, NodeId id) =>
        tree.GetNode(id)?.AsElement()?.Name.Local;

    private static int[] InkBox(Pixmap pixmap, int top, int bottom)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = Math.Max(top, 0); y < Math.Min(bottom, (int)pixmap.Height); y++)
        {
            for (int x = 0; x < pixmap.Width; x++)
            {
                PremultipliedColor pixel = pixmap.Pixel((uint)x, (uint)y)!.Value;
                if (pixel.R < 128 && pixel.G < 128 && pixel.B < 128 && pixel.A > 127)
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x + 1);
                    y1 = Math.Max(y1, y + 1);
                }
            }
        }

        return [x0, y0, x1, y1];
    }
}
