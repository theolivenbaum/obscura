using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// <c>contain: layout/paint</c> makes a stacking context, so a <c>z-index: -1</c> descendant
/// paints above the backgrounds outside it (msn.com's hero card,
/// render-repros/contain-negative-z.html). Pixels are Chromium 141's.
/// </summary>
public class ContainStackingTests
{
    private const string Page = """
        <html><head><style>
          html, body { margin:0; padding:0; background:#ffffff }
          .card { position:relative; width:100px; height:60px; background:#333333; margin-bottom:10px }
          .root { contain:content; height:100% }
          .media { position:absolute; z-index:-1; left:0; top:0; width:100px; height:60px; background:#00ff00 }
          .ctx { position:relative; z-index:0; width:100px; height:60px; background:#0000ff }
        </style></head><body>
          <div class="card"><div class="root"><div class="media"></div></div></div>
          <div class="card"><div><div class="media"></div></div></div>
          <div class="ctx"><div class="media"></div></div>
        </body></html>
        """;

    private static (byte R, byte G, byte B) At(Pixmap pixmap, uint x, uint y)
    {
        var pixel = pixmap.Pixel(x, y)!.Value;
        return (pixel.R, pixel.G, pixel.B);
    }

    [Fact]
    public void NegativeZIndexStaysInsideAContainedBox()
    {
        using Pixmap pixmap = RenderPaint.PaintDom(HtmlParsing.ParseHtml(Page), (200f, 220f), null)
            ?? throw new InvalidOperationException("the page did not paint");

        // contain: content isolates the -1 layer above the card's own background.
        Assert.Equal(((byte)0, (byte)255, (byte)0), At(pixmap, 50, 30));

        // Without containment the -1 layer goes behind the card (root stacking context).
        Assert.Equal(((byte)51, (byte)51, (byte)51), At(pixmap, 50, 100));

        // A z-index stacking context paints its own background below its -1 children.
        Assert.Equal(((byte)0, (byte)255, (byte)0), At(pixmap, 50, 170));
    }
}
