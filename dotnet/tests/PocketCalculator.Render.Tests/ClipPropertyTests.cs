using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The CSS 2.1 <c>clip: rect(...)</c> property, which `crates/obscura-render` does not
/// implement. Expected pixels are Chromium 141's on the same markup
/// (render-repros/clip-rect-visually-hidden.html).
/// </summary>
public class ClipPropertyTests
{
    private const string Page = """
        <html><head><style>
          html, body { margin:0; padding:0; background:#ffffff }
          .box { position:absolute; width:100px; height:60px }
          #hidden { position:fixed; left:20px; top:20px; clip:rect(0px, 0px, 0px, 0px); background:#ff0000 }
          #hidden span { display:block; width:200px; height:20px; background:#ff8000 }
          #half { left:160px; top:20px; clip:rect(10px, 50px, auto, auto); background:#0000ff }
          #half span { position:absolute; left:-40px; top:40px; width:200px; height:40px; background:#00ff00 }
          #static { position:static; clip:rect(0 0 0 0); width:100px; height:40px; margin-top:120px; background:#800080 }
        </style></head><body>
          <a id="hidden" class="box" href="#f"><span>Skip</span></a>
          <div id="half" class="box"><span></span></div>
          <div id="static"></div>
        </body></html>
        """;

    private static (byte R, byte G, byte B) At(Pixmap pixmap, uint x, uint y)
    {
        var pixel = pixmap.Pixel(x, y)!.Value;
        return (pixel.R, pixel.G, pixel.B);
    }

    [Fact]
    public void ClipRectHidesAndNarrowsAbsolutelyPositionedBoxes()
    {
        using Pixmap pixmap = RenderPaint.PaintDom(HtmlParsing.ParseHtml(Page), (400f, 200f), null)
            ?? throw new InvalidOperationException("the page did not paint");

        // rect(0 0 0 0): neither the fixed link nor its in-flow child paints.
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixmap, 30, 30));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixmap, 150, 30));

        // rect(10px, 50px, auto, auto) of a box at (160, 20): 160..210 x 30..80.
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixmap, 180, 25));
        Assert.Equal(((byte)0, (byte)0, (byte)255), At(pixmap, 180, 45));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixmap, 230, 45));

        // The absolutely positioned child is clipped by the same rect.
        Assert.Equal(((byte)0, (byte)255, (byte)0), At(pixmap, 180, 70));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixmap, 140, 70));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixmap, 260, 70));

        // `clip` does not apply to a static box.
        Assert.Equal(((byte)128, (byte)0, (byte)128), At(pixmap, 50, 140));
    }

    [Theory]
    [InlineData("rect(0px, 0px, 0px, 0px)", 0f, 0f, 0f, 0f)]
    [InlineData("rect(0 0 0 0)", 0f, 0f, 0f, 0f)]
    [InlineData("rect(1px 2px 3px 4px)", 1f, 2f, 3f, 4f)]
    [InlineData("rect(10px, auto, auto, 5px)", 10f, null, null, 5f)]
    public void ClipRectParses(string value, float? top, float? right, float? bottom, float? left)
    {
        LayoutStyle style = ComputedStyle.Compute("div", $"position:absolute;clip:{value}");

        Assert.Equal(new ClipRect(top, right, bottom, left), style.Clip);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("rect(1px, 2px, 3px)")]
    [InlineData("rect(1px, 2%, 3px, 4px)")]
    [InlineData("inset(1px)")]
    public void ClipAutoOrInvalidLeavesNoClip(string value)
    {
        LayoutStyle style = ComputedStyle.Compute("div", $"position:absolute;clip:{value}");

        Assert.Null(style.Clip);
    }
}
