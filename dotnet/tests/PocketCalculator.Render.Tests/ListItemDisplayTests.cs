using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// Only a <c>display: list-item</c> box has a marker. `paint.rs` draws one for every
/// <c>li</c>; Chromium 141 draws none for an <c>li</c> whose display the author replaced
/// (render-repros/list-item-display.html), and none for one whose <c>::marker</c> has
/// empty content (grammarly.com's carousel slides).
/// </summary>
public class ListItemDisplayTests
{
    private const string Page = """
        <html><head><style>
          html, body { margin:0; padding:0; background:#ffffff }
          ul { margin:0; padding:0 0 0 40px; font:16px/20px sans-serif; color:#000000 }
          .row { display:flex; gap:10px }
          .row li { display:block; width:100px; height:40px; background:#cccccc }
          .reset li::marker { content:"" }
        </style></head><body>
          <ul id="plain"><li id="a">one</li></ul>
          <ul class="row" id="row"><li id="b"></li><li></li></ul>
          <ul id="explicit"><li id="c" style="display:list-item">three</li></ul>
          <ul class="reset"><li>four</li></ul>
        </body></html>
        """;

    private static int InkIn(Pixmap pixmap, uint left, uint top, uint right, uint bottom)
    {
        int ink = 0;
        for (uint y = top; y < bottom; y++)
        {
            for (uint x = left; x < right; x++)
            {
                if (pixmap.Pixel(x, y) is { } pixel && pixel.R < 100 && pixel.G < 100 && pixel.B < 100)
                {
                    ink++;
                }
            }
        }

        return ink;
    }

    [Fact]
    public void OnlyListItemBoxesPaintAMarker()
    {
        using Pixmap pixmap = RenderPaint.PaintDom(HtmlParsing.ParseHtml(Page), (400f, 200f), null)
            ?? throw new InvalidOperationException("the page did not paint");

        // The marker sits in the 40px indent left of each item's content box.
        Assert.True(InkIn(pixmap, 0, 0, 40, 20) > 0, "a plain li draws its bullet");
        Assert.Equal(0, InkIn(pixmap, 0, 20, 40, 60));
        Assert.True(InkIn(pixmap, 0, 60, 40, 80) > 0, "display: list-item draws its bullet");

        // `::marker { content: "" }` (grammarly.com's carousel) leaves a list item with none.
        Assert.Equal(0, InkIn(pixmap, 0, 80, 40, 100));
        Assert.True(InkIn(pixmap, 40, 80, 120, 100) > 0, "the item's own text still paints");
    }

    [Fact]
    public void ComputedDisplayReportsListItem()
    {
        DomTree tree = HtmlParsing.ParseHtml(Page);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (400f, 200f), null, new RenderResourceCache())
            ?? throw new InvalidOperationException("layout did not prepare");
        NodeId Id(string id) => tree.GetElementById(id) ?? throw new InvalidOperationException(id);

        Assert.Equal("list-item", prepared.ComputedStyle(Id("a"))?["display"]);
        Assert.Equal("block", prepared.ComputedStyle(Id("b"))?["display"]);
        Assert.Equal("list-item", prepared.ComputedStyle(Id("c"))?["display"]);
        Assert.True(ComputedStyle.SupportsDeclaration("display", "list-item"));
    }
}
