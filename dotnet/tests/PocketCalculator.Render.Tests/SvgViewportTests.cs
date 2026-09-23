using System.Text;
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// SVG viewport geometry: the outermost viewport's clip, a nested <c>&lt;svg&gt;</c>'s own
/// viewport, and the transforms a <c>clipPath</c> carries.
/// </summary>
/// <remarks>
/// Every expectation here was measured against Chromium 141 on <c>svg-probe.html</c> and
/// <c>imgsvg-c.svg</c>, not against the Rust engine: <c>crates/obscura-render</c> hands the
/// whole document to resvg, so there is no reference behaviour to port member by member, only
/// the same observable result to reproduce.
/// </remarks>
public class SvgViewportTests
{
    private static Pixmap Raster(string markup, uint width, uint height) =>
        SvgRenderer.Render(Encoding.UTF8.GetBytes(markup), width, height)
        ?? throw new InvalidOperationException("SVG did not rasterize");

    /// <summary>The bounding box of every pixel within tolerance of <paramref name="color"/>.</summary>
    private static (int X, int Y, int Width, int Height, int Count) InkBounds(
        Pixmap pixmap,
        byte   r,
        byte   g,
        byte   b)
    {
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;
        int count = 0;
        for (uint y = 0; y < pixmap.Height; y++)
        {
            for (uint x = 0; x < pixmap.Width; x++)
            {
                PremultipliedColor pixel = pixmap.Pixel(x, y)!.Value;
                if (Math.Abs(pixel.R - r) > 8 || Math.Abs(pixel.G - g) > 8 || Math.Abs(pixel.B - b) > 8
                    || pixel.A < 250)
                {
                    continue;
                }

                count++;
                minX = Math.Min(minX, (int)x);
                minY = Math.Min(minY, (int)y);
                maxX = Math.Max(maxX, (int)x);
                maxY = Math.Max(maxY, (int)y);
            }
        }

        return count == 0
            ? (0, 0, 0, 0, 0)
            : (minX, minY, maxX - minX + 1, maxY - minY + 1, count);
    }

    /// <summary>
    /// An outermost <c>&lt;svg&gt;</c> clips to its viewport, which is the UA sheet's
    /// <c>overflow: hidden</c>. Chromium paints 900 of the rect's pixels here.
    /// </summary>
    [Fact]
    public void OutermostViewportClipsOverflowByDefault()
    {
        using Pixmap pixmap = Raster(
            """<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60" viewBox="0 0 120 60"><rect x="90" y="30" width="80" height="60" fill="#cc0000"/></svg>""",
            120,
            60);
        Assert.Equal(120u, pixmap.Width);
        Assert.Equal(60u, pixmap.Height);
        Assert.Equal((90, 30, 30, 30, 900), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }

    /// <summary>
    /// An author <c>overflow: visible</c> overrides that UA <c>hidden</c>, so the whole 80x60
    /// rect paints. Chromium: 4800 pixels at [90,30,80,60]; before this fix Obscura painted 900.
    /// </summary>
    [Fact]
    public void OutermostViewportOverflowVisibleSuppressesTheClip()
    {
        using Pixmap pixmap = Raster(
            """<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60" viewBox="0 0 120 60" style="overflow:visible"><rect x="90" y="30" width="80" height="60" fill="#cc0000"/></svg>""",
            120,
            60);
        Assert.Equal((90, 30, 80, 60, 4800), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }

    /// <summary>The <c>overflow</c> presentation attribute says the same thing as the property.</summary>
    [Fact]
    public void OutermostViewportOverflowAttributeSuppressesTheClipToo()
    {
        using Pixmap pixmap = Raster(
            """<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60" viewBox="0 0 120 60" overflow="visible"><rect x="90" y="30" width="80" height="60" fill="#cc0000"/></svg>""",
            120,
            60);
        Assert.Equal((90, 30, 80, 60, 4800), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }

    /// <summary>
    /// A stylesheet <c>overflow: visible</c> reaches the rasterizer through the serialized
    /// computed style, which is the only path a rule outside the element itself can take.
    /// </summary>
    [Fact]
    public void StylesheetOverflowVisibleTravelsIntoTheSerializedSvg()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """<html><head><style>svg{overflow:visible}</style></head><body><svg id="s" width="10" height="10"><rect width="4" height="4"/></svg></body></html>""");
        DomLayout laid = RenderDom.LayoutDom(tree, (100f, 100f));
        NodeId svg = tree.GetElementById("s") ?? throw new InvalidOperationException("no #s");
        string markup = PaintSvg.SerializeSvgStyled(tree, svg, laid.Styles, laid.CustomProperties, null);
        Assert.Contains("overflow:visible", markup, StringComparison.Ordinal);
    }

    /// <summary>An svg nobody gave an overflow to keeps the UA clip, so nothing is serialized.</summary>
    [Fact]
    public void UnstyledSvgDoesNotCarryAnOverflowDeclaration()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """<html><body><svg id="s" width="10" height="10"><rect width="4" height="4"/></svg></body></html>""");
        DomLayout laid = RenderDom.LayoutDom(tree, (100f, 100f));
        NodeId svg = tree.GetElementById("s") ?? throw new InvalidOperationException("no #s");
        string markup = PaintSvg.SerializeSvgStyled(tree, svg, laid.Styles, laid.CustomProperties, null);
        Assert.DoesNotContain("overflow", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A nested <c>&lt;svg&gt;</c> establishes its own viewport: <c>x</c>/<c>y</c> place it and
    /// its <c>viewBox</c> maps 10x6 user units onto the inner 100x60 box. Chromium paints 6000
    /// pixels at [50,20,100,60]; before this fix Obscura painted 60 at [0,0,10,6], so the
    /// placement assertion is as load-bearing as the count.
    /// </summary>
    [Fact]
    public void NestedSvgAppliesItsOwnViewportScaleAndPosition()
    {
        using Pixmap pixmap = Raster(
            """<svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 200 100"><svg x="50" y="20" width="100" height="60" viewBox="0 0 10 6"><rect width="10" height="6" fill="#cc0000"/></svg></svg>""",
            200,
            100);
        Assert.Equal((50, 20, 100, 60, 6000), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }

    /// <summary>A nested viewport with no <c>viewBox</c> still translates by <c>x</c>/<c>y</c>.</summary>
    [Fact]
    public void NestedSvgWithoutViewBoxTranslatesByXAndY()
    {
        using Pixmap pixmap = Raster(
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><svg x="20" y="30" width="40" height="40"><rect width="10" height="10" fill="#cc0000"/></svg></svg>""",
            100,
            100);
        Assert.Equal((20, 30, 10, 10, 100), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }

    /// <summary>
    /// A nested viewport clips its own content, and its <c>width</c>/<c>height</c> percentages
    /// resolve against the viewport that contains it.
    /// </summary>
    [Fact]
    public void NestedSvgClipsToItsViewportAndResolvesPercentages()
    {
        using Pixmap pixmap = Raster(
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100"><svg x="10" y="10" width="50%" height="50%"><rect width="90" height="90" fill="#cc0000"/></svg></svg>""",
            100,
            100);

        // 50% of the 100x100 viewport is a 50x50 box at (10,10); the 90x90 rect is cut to it.
        Assert.Equal((10, 10, 50, 50, 2500), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }

    /// <summary>
    /// A <c>clipPath</c> shape's own <c>transform</c> positions the clip. Every Figma export
    /// wraps its artboard clip in one, and ignoring it cropped the illustration to the top
    /// 203 rows of 444.
    /// </summary>
    [Fact]
    public void ClipPathShapeTransformPositionsTheClip()
    {
        using Pixmap pixmap = Raster(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
              <g clip-path="url(#c)"><rect width="100" height="100" fill="#cc0000"/></g>
              <defs><clipPath id="c"><rect width="40" height="30" transform="translate(20 50)"/></clipPath></defs>
            </svg>
            """,
            100,
            100);
        Assert.Equal((20, 50, 40, 30, 1200), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }

    /// <summary>The <c>clipPath</c> element's own <c>transform</c> counts as well.</summary>
    [Fact]
    public void ClipPathElementTransformPositionsTheClip()
    {
        using Pixmap pixmap = Raster(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
              <g clip-path="url(#c)"><rect width="100" height="100" fill="#cc0000"/></g>
              <defs><clipPath id="c" transform="translate(20 50)"><rect width="40" height="30"/></clipPath></defs>
            </svg>
            """,
            100,
            100);
        Assert.Equal((20, 50, 40, 30, 1200), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }

    /// <summary>
    /// <c>filter</c> with a lone <c>feGaussianBlur</c> blurs the group. A sharp 20x20 rect fills
    /// exactly 400 pixels; blurred at sigma 4 it spreads well past that and its own corner is no
    /// longer at full coverage.
    /// </summary>
    [Fact]
    public void GaussianBlurFilterSpreadsTheGroupItWraps()
    {
        const string Markup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
              <g filter="url(#f)"><rect x="40" y="40" width="20" height="20" fill="#0000ff"/></g>
              <defs><filter id="f" x="0" y="0" width="100" height="100" filterUnits="userSpaceOnUse"><feGaussianBlur stdDeviation="4"/></filter></defs>
            </svg>
            """;
        using Pixmap blurred = Raster(Markup, 100, 100);
        using Pixmap sharp = Raster(Markup.Replace(" filter=\"url(#f)\"", string.Empty, StringComparison.Ordinal), 100, 100);

        int blurredInk = 0;
        int sharpInk = 0;
        for (uint y = 0; y < 100; y++)
        {
            for (uint x = 0; x < 100; x++)
            {
                if (blurred.Pixel(x, y)!.Value.A > 0)
                {
                    blurredInk++;
                }

                if (sharp.Pixel(x, y)!.Value.A > 0)
                {
                    sharpInk++;
                }
            }
        }

        Assert.Equal(400, sharpInk);
        Assert.True(blurredInk > 1200, $"sigma-4 blur must spread well past the 400-pixel rect: {blurredInk}");
        Assert.Equal(255, sharp.Pixel(41, 41)!.Value.A);
        Assert.True(blurred.Pixel(41, 41)!.Value.A < 220, "the blurred corner must lose coverage");
    }

    /// <summary>
    /// The <c>userSpaceOnUse</c> filter region clips the filtered result, so a region no bigger
    /// than the source rect cuts the blur off at the rect's own edge.
    /// </summary>
    [Fact]
    public void UserSpaceFilterRegionClipsTheBlur()
    {
        const string Markup = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
              <g filter="url(#f)"><rect x="40" y="40" width="20" height="20" fill="#0000ff"/></g>
              <defs><filter id="f" x="REGION" filterUnits="userSpaceOnUse"><feGaussianBlur stdDeviation="4"/></filter></defs>
            </svg>
            """;
        using Pixmap tight = Raster(
            Markup.Replace("REGION", "40\" y=\"40\" width=\"20\" height=\"20", StringComparison.Ordinal),
            100,
            100);
        using Pixmap wide = Raster(
            Markup.Replace("REGION", "0\" y=\"0\" width=\"100\" height=\"100", StringComparison.Ordinal),
            100,
            100);

        int outsideTight = 0;
        int outsideWide = 0;
        for (uint y = 0; y < 100; y++)
        {
            for (uint x = 0; x < 100; x++)
            {
                if (x is >= 40 and < 60 && y is >= 40 and < 60)
                {
                    continue;
                }

                // Skia treats a layer's bounds as a rasterization hint, so the clipped edge can
                // keep a single count of coverage; anything visible is a real leak.
                if (tight.Pixel(x, y)!.Value.A > 8)
                {
                    outsideTight++;
                }

                if (wide.Pixel(x, y)!.Value.A > 8)
                {
                    outsideWide++;
                }
            }
        }

        Assert.Equal(0, outsideTight);
        Assert.True(outsideWide > 500, $"a region covering the viewport must let the blur spread: {outsideWide}");
    }

    /// <summary>
    /// A filter this renderer cannot express paints unfiltered rather than being approximated or
    /// dropped, which is what keeps an unsupported primitive from erasing its group.
    /// </summary>
    [Fact]
    public void UnsupportedFilterPrimitivePaintsUnfiltered()
    {
        using Pixmap pixmap = Raster(
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="100" height="100" viewBox="0 0 100 100">
              <g filter="url(#f)"><rect x="40" y="40" width="20" height="20" fill="#cc0000"/></g>
              <defs><filter id="f"><feTurbulence baseFrequency="0.05"/></filter></defs>
            </svg>
            """,
            100,
            100);
        Assert.Equal((40, 40, 20, 20, 400), InkBounds(pixmap, 0xCC, 0x00, 0x00));
    }
}
