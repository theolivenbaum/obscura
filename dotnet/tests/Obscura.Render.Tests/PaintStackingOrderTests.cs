using Obscura.Dom;
using Xunit;

namespace Obscura.Render.Tests;

/// <summary>
/// Where the shaped inline items are flushed inside one stacking context. These pin a C# fix
/// that `crates/obscura-render/src/paint.rs` does not carry: it flushes them after the whole
/// paint order, so a level's text paints over that level's own positive z-index layers.
/// Every expected value below was taken from Chromium 141 on the same markup.
/// </summary>
public class PaintStackingOrderTests
{
    private const string Page = """
        <html><head><style>
          html, body { margin:0; padding:0; background:#ffffff }
          .box { position:absolute; left:0; width:300px; height:100px }
          .t { font-size:40px; line-height:50px; color:#000000 }
          #s1 { top:0 }
          #cover { margin-top:-50px; height:50px; background:#0000ff }
          #s2 { top:120px }
          #neg { position:absolute; left:0; top:0; width:300px; height:50px;
                 background:#00ff00; z-index:-1 }
          #s3 { top:240px }
          #layer { position:fixed; left:0; top:240px; width:300px; height:100px;
                   z-index:1010; background:#ffff00 }
          #spacer { height:50px }
        </style></head><body>
          <div class="box" id="s1"><div class="t">AAAA</div><div id="cover"></div></div>
          <div class="box" id="s2"><div class="t">BBBB</div><div id="neg"></div></div>
          <div class="box" id="s3"><div class="t">CCCC</div></div>
          <div id="layer"><div id="spacer"></div><div class="t">DDDD</div></div>
        </body></html>
        """;

    private static Pixmap Render() =>
        RenderPaint.PaintDom(HtmlParsing.ParseHtml(Page), (300f, 340f), null)
        ?? throw new InvalidOperationException("the page did not paint");

    /// <summary>Glyph ink inside one band, counted as pixels no channel of which is bright.</summary>
    private static int InkIn(Pixmap pixmap, uint top, uint bottom)
    {
        int ink = 0;
        for (uint y = top; y < bottom; y++)
        {
            for (uint x = 0; x < pixmap.Width; x++)
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
    public void InlineTextDoesNotPaintOverAPositiveZIndexLayer()
    {
        using Pixmap pixmap = Render();

        Assert.Equal(0, InkIn(pixmap, 240, 290));
    }

    [Fact]
    public void APositiveZIndexLayerPaintsItsOwnText()
    {
        using Pixmap pixmap = Render();

        // The covering layer is yellow where no glyph lands, and its own "DDDD" still paints.
        Assert.Equal((byte)255, pixmap.Pixel(295, 285)!.Value.R);
        Assert.Equal((byte)255, pixmap.Pixel(295, 285)!.Value.G);
        Assert.Equal((byte)0, pixmap.Pixel(295, 285)!.Value.B);
        Assert.True(InkIn(pixmap, 290, 340) > 100, "the covering layer painted no text of its own");
    }

    [Fact]
    public void InlineTextPaintsAboveAnInFlowSiblingBackground()
    {
        using Pixmap pixmap = Render();

        // `#cover` is a later in-flow block: its background is step 4, the text is step 7.
        Assert.True(InkIn(pixmap, 0, 50) > 100, "an in-flow sibling background covered the text");
    }

    [Fact]
    public void InlineTextPaintsAboveANegativeZIndexLayer()
    {
        using Pixmap pixmap = Render();

        Assert.True(InkIn(pixmap, 120, 170) > 100, "a negative z-index layer covered the text");
    }
}
