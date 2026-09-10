using System.Globalization;
using Obscura.Render.Css;
using Obscura.Dom;
using Xunit;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render.Tests;

/// <summary>
/// xUnit port of the in-file <c>mod tests</c> of <c>crates/obscura-render/src/paint.rs</c>, in
/// source order with the Rust names preserved.
/// </summary>
/// <remarks>
/// PIXEL TOLERANCE. Skia and tiny-skia anti-alias by a few coverage counts, so a Rust
/// assertion of exact equality on an anti-aliased edge would become the same property with an
/// explicit tolerance here, carrying a comment naming it. No test needed that: every Rust
/// exact-pixel assertion holds exactly against Skia too, and the only tolerances below
/// (&lt;8 per channel on the sticky-scroll samples, +/-5 against Chromium on the radial
/// ellipse, +/-2 on the print-economy grey) are the Rust test's own.
/// </remarks>
public class PaintTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static PremultipliedColor Pixel(Pixmap pixmap, uint x, uint y) =>
        pixmap.Pixel(x, y) ?? throw new InvalidOperationException($"pixel ({x},{y}) is outside the surface");

    private static (byte R, byte G, byte B) Rgb(Pixmap pixmap, uint x, uint y)
    {
        PremultipliedColor pixel = Pixel(pixmap, x, y);
        return (pixel.R, pixel.G, pixel.B);
    }

    private static (byte R, byte G, byte B, byte A) Rgba(Pixmap pixmap, uint x, uint y)
    {
        PremultipliedColor pixel = Pixel(pixmap, x, y);
        return (pixel.R, pixel.G, pixel.B, pixel.A);
    }

    private static NodeId Selector(DomTree tree, string selector) =>
        tree.QuerySelector(selector) ?? throw new InvalidOperationException($"no match for {selector}");

    private static NodeId ById(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");

    private static SkiaSharp.SKBitmap DecodePng(byte[] png) =>
        SkiaSharp.SKBitmap.Decode(png) ?? throw new InvalidOperationException("PNG did not decode");

    private static (int R, int G, int B) PngRgb(SkiaSharp.SKBitmap bitmap, int x, int y)
    {
        SkiaSharp.SKColor color = bitmap.GetPixel(x, y);
        return (color.Red, color.Green, color.Blue);
    }

    /// <summary>
    /// Stand-in for Rust's <c>format!("{:?}", style)</c> structural comparison of a computed
    /// style: C# has no derived Debug, so the port compares the fields the Rust assertion is
    /// about (transform pipeline, geometry, and paint) instead.
    /// </summary>
    private static string LayoutStyleDigest(LayoutStyle style) => string.Join(
        "|",
        TransformOpsDigest(style),
        style.Opacity?.ToString(CultureInfo.InvariantCulture) ?? "-",
        style.TransformOrigin?.ToString() ?? "-",
        style.ContainingBlockTriggers.ToString(CultureInfo.InvariantCulture),
        style.EffectivelyInvisible.ToString(),
        style.VisibilityHidden?.ToString() ?? "-");

    private static string TransformOpsDigest(LayoutStyle style) =>
        string.Join(";", style.TransformOps.Select(static op => op.ToString()));

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void NativeShadowFlatTreePaintsShadowAndSlottedContentOnly()
    {
        DomTree tree = Parse(
            """<html style="background:white"><body style="margin:0"><x-card id="host" style="display:block;width:30px"><span id="light" style="display:block;height:10px;background:rgb(255,0,0)"></span><span id="unslotted" slot="missing" style="display:block;height:10px;background:rgb(0,255,0)"></span></x-card><div id="source"><div style="height:10px;background:rgb(0,0,255)"></div><slot></slot><div style="height:10px;background:rgb(255,255,0)"></div></div></body></html>""");
        NodeId host = ById(tree, "host");
        NodeId source = ById(tree, "source");
        NodeId root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        foreach (NodeId child in tree.Children(source))
        {
            tree.AppendChild(root, child);
        }

        tree.Remove(source);

        Pixmap pixmap = RenderPaint.PaintDom(tree, (30f, 30f), null)!;
        PremultipliedColor blue = Pixel(pixmap, 5, 5);
        PremultipliedColor red = Pixel(pixmap, 5, 15);
        PremultipliedColor yellow = Pixel(pixmap, 5, 25);
        Assert.True(blue.B > 240 && blue.R < 20 && blue.G < 20);
        Assert.True(red.R > 240 && red.G < 20 && red.B < 20);
        Assert.True(yellow.R > 240 && yellow.G > 240 && yellow.B < 20);
    }

    [Fact]
    public void OutsetBoxShadowStaysOutsideTransparentAndOpaqueBorderBoxes()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:white">
                <div style="position:absolute;left:20px;top:20px;width:40px;height:30px;
                            box-shadow:4px 4px 0 black"></div>
                <div style="position:absolute;left:100px;top:20px;width:40px;height:30px;
                            background:rgb(0,255,0);box-shadow:4px 4px 0 black"></div>
                <div style="position:absolute;left:20px;top:70px;width:40px;height:30px;
                            border-radius:12px;box-shadow:0 0 0 4px black"></div>
                <div style="position:absolute;left:100px;top:70px;width:40px;height:30px;
                            box-shadow:2px 2px 3px rgb(51,51,51)"></div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (160f, 120f), null)!;

        Assert.Equal((255, 255, 255), Rgb(pixmap, 35, 35));
        Assert.Equal((255, 255, 255), Rgb(pixmap, 21, 35));
        PremultipliedColor shadowEdge = Pixel(pixmap, 62, 35);
        Assert.True(
            shadowEdge.R < 10 && shadowEdge.G < 10 && shadowEdge.B < 10,
            $"shadow ink must remain outside the border box: {shadowEdge}");
        PremultipliedColor opaqueCenter = Pixel(pixmap, 115, 35);
        Assert.True(
            opaqueCenter.G > 245 && opaqueCenter.R < 10 && opaqueCenter.B < 10,
            $"opaque backgrounds must continue to cover the shadow: {opaqueCenter}");
        PremultipliedColor roundedCorner = Pixel(pixmap, 21, 71);
        Assert.True(
            roundedCorner.R < 10 && roundedCorner.G < 10 && roundedCorner.B < 10,
            $"the hole must follow the rounded border box: {roundedCorner}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 115, 85));
        PremultipliedColor blurredEdge = Pixel(pixmap, 142, 85);
        Assert.True(
            blurredEdge.R < 240 && blurredEdge.G < 240 && blurredEdge.B < 240,
            $"the 2px 2px 3px shadow must retain ink outside the box: {blurredEdge}");
    }

    [Fact]
    public void RadialGradientLengthStopsResolveAgainstTheGradientRay()
    {
        // `transparent 100px` ends the ramp 100px from the center, not at the edge of
        // the box. Only percentages used to survive parsing, so the stop became
        // unpositioned and the ramp spread over the whole 200px radius - which is what
        // turned Tailwind's `transparent 32rem` page glows into a wash over the entire
        // document.
        DomTree tree = Parse(
            """
            <html style="margin:0;background:rgb(0,0,255)">
            <body style="margin:0;width:400px;height:400px;
                         background:radial-gradient(circle at 50% 50%,rgb(255,0,0),transparent 100px)">
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (400f, 400f), null)!;

        Assert.True(Pixel(pixmap, 200, 200).R > 250, $"center: {Pixel(pixmap, 200, 200)}");
        PremultipliedColor halfway = Pixel(pixmap, 100, 200);
        Assert.True(
            halfway.R <= 2 && halfway.B >= 250,
            $"halfway to the 100px stop the ramp must be nearly clear: {halfway}");
        Assert.Equal((0, 0, 255), Rgb(pixmap, 5, 200));
        Assert.Equal((0, 0, 255), Rgb(pixmap, 395, 200));
    }

    [Fact]
    public void BackgroundShorthandPaintsItsFinalColorLayerUnderTheGradient()
    {
        // `background: <gradient>, <color>` sets background-color from the final layer.
        // Reading the color only when no gradient parsed dropped it, so a translucent
        // gradient composited over whatever was behind the element.
        DomTree tree = Parse(
            """
            <html style="margin:0;background:rgb(255,255,255)">
            <body style="margin:0;width:400px;height:400px;
                         background:linear-gradient(90deg,transparent,transparent),rgb(0,255,255)">
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (400f, 400f), null)!;
        foreach (uint x in (uint[])[5, 200, 395])
        {
            Assert.Equal((0, 255, 255), Rgb(pixmap, x, 200));
        }
    }

    [Fact]
    public void BackdropFilterBlursWhatIsBehindTheElementOnly()
    {
        // `backdrop-filter` was parsed only for containing-block bookkeeping, so a frosted
        // panel showed the backdrop through it perfectly sharp.
        static DomTree Page(string filter) => Parse(
            $$"""
            <html style="margin:0"><body style="margin:0;width:200px;height:120px;
                background:linear-gradient(90deg,rgb(255,0,0) 0 50%,rgb(0,0,255) 50% 100%)">
            <div style="position:absolute;left:60px;top:30px;width:80px;height:60px;
                        {{filter}}"></div>
            </body></html>
            """);

        Pixmap sharp = RenderPaint.PaintDom(Page(""), (200f, 120f), null)!;
        Pixmap frosted = RenderPaint.PaintDom(
            Page("backdrop-filter:blur(8px)"), (200f, 120f), null)!;

        // The backdrop's hard colour boundary sits at x = 100, inside the panel.
        Assert.Equal((255, 0, 0), Rgb(sharp, 96, 60));
        Assert.Equal((0, 0, 255), Rgb(sharp, 104, 60));

        // Blurred: the boundary becomes a ramp, so both sides carry the other colour.
        (byte lr, _, byte lb) = Rgb(frosted, 96, 60);
        (byte rr, _, byte rb) = Rgb(frosted, 104, 60);
        Assert.True(lb > 20, $"red side must pick up blue: {Rgb(frosted, 96, 60)}");
        Assert.True(rr > 20, $"blue side must pick up red: {Rgb(frosted, 104, 60)}");
        Assert.True(lr > rr, $"the ramp must still run red to blue: {lr} vs {rr}");
        Assert.True(rb > lb, $"and blue to red the other way: {rb} vs {lb}");

        // Outside the panel the backdrop is untouched: this is not a `filter`.
        Assert.Equal((255, 0, 0), Rgb(frosted, 96, 10));
        Assert.Equal((0, 0, 255), Rgb(frosted, 104, 10));
        Assert.Equal((255, 0, 0), Rgb(frosted, 20, 60));
        Assert.Equal((0, 0, 255), Rgb(frosted, 180, 60));
    }

    [Fact]
    public void FilterBlurSoftensTheElementAndBleedsPastItsBox()
    {
        // `filter` was parsed only for containing-block bookkeeping, so a blurred element
        // rendered perfectly sharp. blur()'s argument is sigma itself, unlike box-shadow's
        // blur radius, which is 2 sigma.
        DomTree sharp = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:black">
            <div style="position:absolute;left:30px;top:30px;width:40px;height:40px;
                        background:rgb(0,255,0)"></div>
            </body></html>
            """);
        DomTree blurred = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:black">
            <div style="position:absolute;left:30px;top:30px;width:40px;height:40px;
                        background:rgb(0,255,0);filter:blur(6px)"></div>
            </body></html>
            """);
        Pixmap a = RenderPaint.PaintDom(sharp, (100f, 100f), null)!;
        Pixmap b = RenderPaint.PaintDom(blurred, (100f, 100f), null)!;

        // Sharp: hard edge at x = 30, nothing outside the box.
        Assert.Equal(0, Pixel(a, 24, 50).G);
        Assert.Equal(255, Pixel(a, 31, 50).G);

        // Blurred: ink bleeds outside the box, the edge is a ramp, and the middle stays
        // saturated because 40px is wide against sigma 6.
        Assert.True(Pixel(b, 24, 50).G > 8, $"must bleed past the box: {Pixel(b, 24, 50).G}");
        byte edge = Pixel(b, 30, 50).G;
        Assert.True(edge is >= 60 and <= 200, $"the edge must be a ramp: {edge}");
        Assert.True(Pixel(b, 50, 50).G > 245, $"middle must stay saturated: {Pixel(b, 50, 50).G}");

        byte previous = 0;
        for (uint x = 22; x < 40; x++)
        {
            byte value = Pixel(b, x, 50).G;
            Assert.True(value >= previous, $"coverage must rise into the box at x={x}");
            previous = value;
        }
    }

    [Fact]
    public void BorderRadiusHalfTheSideDrawsACircleNotASquircle()
    {
        // Corners were quadratic Beziers controlled by the corner point, which is a
        // parabola: its midpoint sits 6.1% further out than the arc, so every rounded box
        // bulged and border-radius:50% was visibly not a circle.
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:black">
            <div style="position:absolute;left:20px;top:20px;width:80px;height:80px;
                        background:rgb(0,255,0);border-radius:999px"></div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (120f, 120f), null)!;

        // Centre (60, 60), radius 40. Sample the silhouette well away from the axes,
        // where a parabola departs from the arc most.
        float worst = 0f;
        for (uint y = 26; y <= 94; y++)
        {
            uint? left = null;
            for (uint x = 20; x <= 100; x++)
            {
                if (Pixel(pixmap, x, y).G > 127)
                {
                    left = x;
                    break;
                }
            }

            if (left is not { } edge)
            {
                continue;
            }

            float dy = y + 0.5f - 60f;
            float expected = 60f - MathF.Sqrt(F32.Max((40f * 40f) - (dy * dy), 0f));
            worst = F32.Max(worst, MathF.Abs(edge - expected));
        }

        Assert.True(worst < 1f, $"silhouette departs from a true circle by {worst:F2}px");
    }

    [Fact]
    public void InsetBoxShadowPaintsInwardFromTheBorderBoxEdge()
    {
        // PaintBoxShadow used to return early on Inset, so an inner shadow painted
        // nothing at all. Inset coverage is the complement of the outset ramp: strongest
        // at the border-box edge, half at the offset-and-spread inner edge, near zero
        // deep inside. It also paints over the background and under the border, so an
        // opaque background must not hide it.
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:white">
            <div style="position:absolute;left:20px;top:20px;width:40px;height:40px;
                        background:rgb(0,0,255);
                        box-shadow:inset 0 0 12px rgb(255,255,0)"></div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (100f, 100f), null)!;

        // Yellow over blue, so the red channel is the shadow's own coverage.
        byte edge = Pixel(pixmap, 22, 40).R;
        byte mid = Pixel(pixmap, 30, 40).R;
        byte center = Pixel(pixmap, 40, 40).R;
        Assert.True(edge > 60, $"the shadow must be strong at the border-box edge: {edge}");
        Assert.True(edge > mid && mid > center, $"must decay inward: {edge}, {mid}, {center}");
        Assert.Equal(0, center);
        Assert.Equal(
            (255, 255, 255),
            Rgb(pixmap, 18, 40));
    }

    [Fact]
    public void BlurredBoxShadowFallsOffAsAGaussianRatherThanASolidBlob()
    {
        // A 40x40 black box with a 15px blur on white. sigma is blur/2 = 7.5, so
        // coverage at the shape edge is ~50%, decaying to nothing by 2.5 sigma
        // (18.75px). Painting the ramp only outward from an opaque shape - the bug
        // this guards - made every pixel out to 15px fully black instead.
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:white">
                <div style="position:absolute;left:60px;top:60px;width:40px;height:40px;
                            background:black;box-shadow:0 0 15px black"></div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (160f, 160f), null)!;
        byte Gray(uint x) => Pixel(pixmap, x, 80).R;

        byte edge = Gray(59);
        Assert.True(
            edge is >= 60 and <= 200,
            $"one pixel outside the edge must be roughly half covered, not solid: {edge}");
        byte mid = Gray(52);
        byte far = Gray(45);
        Assert.True(edge < mid && mid < far, $"coverage must decay outward: {edge}, {mid}, {far}");
        Assert.True(far > 220, $"2 sigma out must be nearly clear: {far}");
        Assert.Equal(255, Gray(38));
        Assert.Equal(0, Gray(80));
    }

    [Fact]
    public void SvgImageMetadataKeepsViewBoxAsRatioOnly()
    {
        ReplacedIntrinsic ratioOnly = PaintSvg.SvgImageIntrinsicMetadata(
            "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 576 576'/>"u8.ToArray())!.Value;
        Assert.Null(ratioOnly.Width);
        Assert.Null(ratioOnly.Height);
        Assert.Equal(1.0f, ratioOnly.Ratio);
        Assert.Equal((150f, 150f), ratioOnly.NaturalSize());

        ReplacedIntrinsic explicitSize = PaintSvg.SvgImageIntrinsicMetadata(
            "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80' viewBox='0 0 200 100'/>"u8
                .ToArray())!.Value;
        Assert.Equal(120f, explicitSize.Width);
        Assert.Equal(80f, explicitSize.Height);
        Assert.Equal(1.5f, explicitSize.Ratio);
        Assert.Equal((120f, 80f), explicitSize.NaturalSize());

        ReplacedIntrinsic commented = PaintSvg.SvgImageIntrinsicMetadata(
            "<!-- <svg width='999' height='999'/> --><svg xmlns='http://www.w3.org/2000/svg' width='12' height='8'/>"u8
                .ToArray())!.Value;
        Assert.Equal((12f, 8f), commented.NaturalSize());

        Assert.Null(PaintSvg.SvgImageIntrinsicMetadata(
            "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='20'><g>"u8.ToArray()));
    }

    [Fact]
    public void ViewBoxOnlySvgTransfersDefiniteCssWidthWithoutUsingViewBoxUnits()
    {
        byte[] square = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'/>"u8.ToArray();
        Assert.Equal((150f, 150f), PaintResources.ImageMetadataFromBytes(square));

        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0 }
                #host { width:360px }
                img { display:block }
                #ratio { width:100%; height:auto }
            </style></head><body><div id="host">
                <img id="auto" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%20100%20100'/%3E">
                <img id="ratio" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%20200%20100'/%3E">
                <img id="explicit" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='120'%20height='80'%20viewBox='0%200%20200%20100'/%3E">
            </div></body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (500f, 400f), null, resources)!;
        Rect RectFor(string selector) => prepared.Layout.Rects[Selector(tree, selector)];

        Rect auto = RectFor("#auto");
        Assert.Equal((360f, 360f), (auto.Width, auto.Height));
        Rect ratio = RectFor("#ratio");
        Assert.Equal((360f, 180f), (ratio.Width, ratio.Height));
        Rect explicitRect = RectFor("#explicit");
        Assert.Equal((120f, 80f), (explicitRect.Width, explicitRect.Height));
    }

    [Fact]
    public void RatioOnlyInlineImagesStretchFitTheDefiniteLineWidth()
    {
        const string Source =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20viewBox='0%200%20576%20576'%3E%3Crect%20width='576'%20height='576'%20fill='%23ff80ab'/%3E%3C/svg%3E";
        const string Raster =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAAFklEQVR4nGP8z8Dwn4GBgYGJAQrgDAAxOwIE7x6DkQAAAABJRU5ErkJggg==";
        DomTree tree = Parse($$"""
            <html><head><style>
                html,body { margin:0 }
                .case { width:370px; line-height:0 }
                #authored-inline { display:inline }
                #authored-block { display:block }
                #authored-inline-block { display:inline-block }
                #positioned { position:absolute; left:0; top:1900px; width:340px;
                    min-width:360px; max-width:380px; box-sizing:border-box;
                    padding:10px; border:10px solid transparent }
                #float-row { width:500px }
                #float-column { float:left; width:40%; box-sizing:border-box;
                    padding-left:10px; padding-right:10px }
            </style></head><body>
                <div class="case"><img id="direct" src="{{Source}}"></div>
                <div class="case"><a><img id="anchored" src="{{Source}}"></a></div>
                <div class="case"><img id="authored-inline" src="{{Source}}"></div>
                <div class="case"><img id="authored-block" src="{{Source}}"></div>
                <div class="case"><img id="authored-inline-block" src="{{Source}}"></div>
                <div class="case"><img id="intrinsic-raster" src="{{Raster}}"></div>
                <div id="positioned"><img id="positioned-image" src="{{Source}}"></div>
                <div id="float-row"><div id="float-column"><img id="float-image" src="{{Source}}"></div></div>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (500f, 2400f), null, resources)!;
        Rect RectFor(string selector) => prepared.Layout.Rects[Selector(tree, selector)];

        foreach (string selector in (string[])
        [
            "#direct",
            "#anchored",
            "#authored-inline",
            "#authored-block",
            "#authored-inline-block",
        ])
        {
            Rect image = RectFor(selector);
            Assert.Equal((370f, 370f), (image.Width, image.Height));
        }

        Assert.Equal((2f, 3f), (RectFor("#intrinsic-raster").Width, RectFor("#intrinsic-raster").Height));
        Assert.Equal(
            (320f, 320f),
            (RectFor("#positioned-image").Width, RectFor("#positioned-image").Height));
        Assert.Equal(
            (180f, 180f),
            (RectFor("#float-image").Width, RectFor("#float-image").Height));

        string Display(string selector) => prepared.ComputedStyle(Selector(tree, selector))!["display"];
        Assert.Equal("inline", Display("#direct"));
        Assert.Equal("inline", Display("#anchored"));
        Assert.Equal("inline", Display("#authored-inline"));
        Assert.Equal("block", Display("#authored-block"));
        Assert.Equal("inline-block", Display("#authored-inline-block"));
    }

    [Fact]
    public void NativePlaceholdersHonorDefaultAuthorColorOpacityAndValueState()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                html,body { margin:0; background:white }
                input { display:block; box-sizing:border-box; width:180px; height:30px;
                        padding:0; border:0; font-size:20px; background:white }
                #colored::placeholder { color:rgb(255,0,0) }
                #hidden::placeholder { opacity:0 }
                #inherited { color:rgb(0,0,255) }
                #inherited::placeholder { color:inherit; opacity:.5 }
            </style></head><body>
                <input id="default" placeholder="default">
                <input id="colored" placeholder="colored">
                <input id="hidden" placeholder="hidden">
                <input id="filled" placeholder="must not paint" value="actual">
                <input id="inherited" placeholder="inherited">
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (200f, 130f), null, resources)!;

        Assert.Null(prepared.Layout.Styles[Selector(tree, "#default")].PlaceholderPseudo);
        Assert.Equal(
            new RgbaColor(255, 0, 0, 255),
            prepared.Layout.Styles[Selector(tree, "#colored")].PlaceholderPseudo!.Color);
        Assert.Equal(0f, prepared.Layout.Styles[Selector(tree, "#hidden")].PlaceholderPseudo!.Opacity);
        LayoutStyle inherited = prepared.Layout.Styles[Selector(tree, "#inherited")].PlaceholderPseudo!;
        Assert.Equal(new RgbaColor(0, 0, 255, 255), inherited.Color);
        Assert.Equal(0.5f, inherited.Opacity);

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        int NonWhite(uint top)
        {
            int count = 0;
            for (uint y = top; y < top + 30; y++)
            {
                for (uint x = 0; x < 180; x++)
                {
                    PremultipliedColor pixel = Pixel(pixmap, x, y);
                    if (pixel.R < 245 || pixel.G < 245 || pixel.B < 245)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        Assert.True(NonWhite(0) > 10, "the native default placeholder must paint");
        bool authoredColor = false;
        for (uint y = 30; y < 60 && !authoredColor; y++)
        {
            for (uint x = 0; x < 180 && !authoredColor; x++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                authoredColor = pixel.R > 200 && pixel.G < 80 && pixel.B < 80;
            }
        }

        Assert.True(authoredColor, "the authored placeholder color must reach glyph paint");
        Assert.Equal(0, NonWhite(60));
        Assert.True(NonWhite(90) > 10, "a control's value must paint where its placeholder is suppressed");
        bool darkValue = false;
        for (uint y = 90; y < 120 && !darkValue; y++)
        {
            for (uint x = 0; x < 180 && !darkValue; x++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                darkValue = pixel.R < 60 && pixel.G < 60 && pixel.B < 60;
            }
        }

        Assert.True(darkValue, "the painted glyphs must be the value's color, not the grey placeholder");
    }

    [Fact]
    public void CacheOnlyModeDoesNotLoadOrNegativeCacheUnknownUrls()
    {
        int calls = 0;
        RenderResourceCache cache = RenderResourceCache.WithLoader(_ =>
        {
            calls++;
            return new byte[] { 1, 2, 3 };
        });
        const string Url = "https://example.test/dynamic.png";

        bool previous = cache.SetSyncLoadingEnabled(false);
        Assert.True(previous);
        Assert.Null(cache.GetOrLoad(Url));
        Assert.Equal(0, calls);
        Assert.False(cache.HasLiveOutcome(Url), "a cache-only miss must remain eligible for later preparation");

        cache.SetSyncLoadingEnabled(previous);
        cache.Seed(Url, [9, 8, 7]);
        Assert.Equal(new byte[] { 9, 8, 7 }, cache.GetOrLoad(Url));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void HtmlImageProfilesKeepIntrinsicGeometryAndPaintSeparate()
    {
        const string NetworkUrl = "https://assets.test/shared.svg";
        const string Url = "https://assets.test/shared.svg#icon";
        DomTree tree = Parse($$"""
            <html style="margin:0"><body style="margin:0">
                <img id="plain" src="{{Url}}" style="display:block">
                <img id="anonymous" crossorigin="anonymous" src="{{Url}}" style="display:block">
                <img id="credentialed" crossorigin="use-credentials" alt="" src="{{Url}}"
                     style="display:block;width:20px;height:10px">
            </body></html>
            """);
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ =>
            throw new InvalidOperationException("seeded profile resources must not reach the loader"));
        resources.SeedImage(
            NetworkUrl,
            ImageRequestProfile.NoCorsInclude,
            """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"><rect width="20" height="10" fill="#f00"/></svg>"""u8
                .ToArray());
        resources.SeedImage(
            NetworkUrl,
            ImageRequestProfile.CorsSameOrigin,
            """<svg xmlns="http://www.w3.org/2000/svg" width="40" height="30"><rect width="40" height="30" fill="#0f0"/></svg>"""u8
                .ToArray());
        resources.SeedImageMissing(NetworkUrl, ImageRequestProfile.CorsInclude);

        PreparedRender prepared = RenderPaint.PrepareDom(tree, (80f, 60f), null, resources)!;
        Rect plain = prepared.Layout.Rects[Selector(tree, "#plain")];
        Rect anonymous = prepared.Layout.Rects[Selector(tree, "#anonymous")];
        Assert.Equal(Url, prepared.SelectedImage(Selector(tree, "#plain"))!.ResolvedUrl);
        Assert.Equal((20f, 10f), (plain.Width, plain.Height));
        Assert.Equal((40f, 30f), (anonymous.Width, anonymous.Height));

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        PremultipliedColor red = Pixel(pixmap, 5, 5);
        Assert.True(red.R > 240 && red.G < 20, $"{red}");
        PremultipliedColor green = Pixel(pixmap, 5, 20);
        Assert.True(green.G > 240 && green.R < 20, $"{green}");
        Assert.Equal((255, 255, 255, 255), Rgba(pixmap, 5, 45));
    }

    [Fact]
    public void ImageAcceptAdvertisesExactlyDecodableMimeTypes()
    {
        Assert.DoesNotContain('*', RenderResourceCache.ImageAccept);
        Assert.DoesNotContain("avif", RenderResourceCache.ImageAccept.ToLowerInvariant(), StringComparison.Ordinal);
        foreach (string mime in RenderResourceCache.ImageAccept.Split(','))
        {
            Assert.True(ImageCapability.SourceTypeSupported(mime), $"advertised MIME type must be decodable: {mime}");
        }

        foreach (string required in (string[])
        [
            "image/webp",
            "image/apng",
            "image/svg+xml",
            "image/png",
            "image/jpeg",
            "image/gif",
            "image/bmp",
            "image/x-icon",
            "image/vnd.microsoft.icon",
        ])
        {
            Assert.Contains(required, RenderResourceCache.ImageAccept.Split(','));
        }
    }

    [Fact]
    public void PictureTypeFilterSkipsAvifAndAcceptsParameterizedMimeEssence()
    {
        DomTree tree = Parse(
            """
            <picture>
                 <source type="IMAGE/AVIF; codecs=av01" srcset="unsupported.avif">
                 <source type=" Image/WebP ; codecs=lossless " srcset="supported.webp">
                 <img id="hero" src="fallback.png">
               </picture>
            """);
        NodeId hero = ById(tree, "hero");
        Assert.Equal(("supported.webp", 1f), PaintImages.PictureSourceUrl(tree, hero, (800f, 600f)));
    }

    [Fact]
    public void DataSourceAttributesAreNotImageCandidatesOrFetches()
    {
        DomTree tree = Parse(
            """
            <img id="lazy" data-src="real.png"
                     data-srcset="small.png 1x, large.png 2x"
                     data-lazy-src="other.png" data-original="original.png">
            """);
        NodeId lazy = ById(tree, "lazy");
        int calls = 0;
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ =>
        {
            calls++;
            return null;
        });

        Assert.Null(PaintImages.ResolveImgUrl(tree, lazy, (800f, 600f)));
        Assert.Null(resources.ImageElementMetadata(tree, lazy, (800f, 600f), "https://example.test/page"));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DataUriSrcRemainsSelectedWhenDataSrcIsPresent()
    {
        const string Placeholder =
            "data:image/svg+xml,%3Csvg%20xmlns=%22http://www.w3.org/2000/svg%22%20width=%221%22%20height=%221%22/%3E";
        DomTree tree = Parse($"""<img id="lazy" src="{Placeholder}" data-src="real.png">""");
        NodeId lazy = ById(tree, "lazy");
        int calls = 0;
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ =>
        {
            calls++;
            return null;
        });

        Assert.Equal((Placeholder, 1f), PaintImages.ResolveImgUrl(tree, lazy, (800f, 600f)));
        var metadata = resources.ImageElementMetadata(tree, lazy, (800f, 600f), "https://example.test/page")!.Value;
        Assert.Equal(Placeholder, metadata.Url);
        Assert.Equal((1f, 1f), metadata.Dimensions);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void EnabledBmpAndIcoFormatsHaveMetadataAndFullRasterDecode()
    {
        using SkiaSharp.SKBitmap source = new(2, 3, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
        using (SkiaSharp.SKCanvas canvas = new(source))
        {
            canvas.Clear(new SkiaSharp.SKColor(20, 40, 60, 255));
        }

        // Skia encodes BMP/ICO only through its PNG/JPEG/WEBP writers, so the fixture is
        // authored directly. The assertion is unchanged: header metadata and a full decode.
        foreach (byte[] encoded in (byte[][])[BmpFixture(2, 3), IcoFixture(2, 3)])
        {
            Assert.Equal((2u, 3u), PaintResources.ImageDimensions(encoded));
            using Pixmap raster = PaintResources.RasterToPixmap(encoded, 2, 3)!;
            Assert.Equal((2u, 3u), (raster.Width, raster.Height));
        }
    }

    private static byte[] BmpFixture(int width, int height)
    {
        int rowBytes = ((width * 3) + 3) / 4 * 4;
        int pixelBytes = rowBytes * height;
        byte[] bytes = new byte[54 + pixelBytes];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(width).CopyTo(bytes, 18);
        BitConverter.GetBytes(height).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(pixelBytes).CopyTo(bytes, 34);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = 54 + (y * rowBytes) + (x * 3);
                bytes[offset] = 60;
                bytes[offset + 1] = 40;
                bytes[offset + 2] = 20;
            }
        }

        return bytes;
    }

    private static byte[] IcoFixture(int width, int height)
    {
        // An ICO whose single entry embeds a BMP with a doubled-height DIB header.
        int rowBytes = ((width * 3) + 3) / 4 * 4;
        int maskBytes = (((width + 31) / 32) * 4) * height;
        int imageBytes = 40 + (rowBytes * height) + maskBytes;
        byte[] bytes = new byte[6 + 16 + imageBytes];
        BitConverter.GetBytes((short)0).CopyTo(bytes, 0);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 2);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 4);
        bytes[6] = (byte)width;
        bytes[7] = (byte)height;
        BitConverter.GetBytes((short)1).CopyTo(bytes, 10);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 12);
        BitConverter.GetBytes(imageBytes).CopyTo(bytes, 14);
        BitConverter.GetBytes(22).CopyTo(bytes, 18);
        int header = 22;
        BitConverter.GetBytes(40).CopyTo(bytes, header);
        BitConverter.GetBytes(width).CopyTo(bytes, header + 4);
        BitConverter.GetBytes(height * 2).CopyTo(bytes, header + 8);
        BitConverter.GetBytes((short)1).CopyTo(bytes, header + 12);
        BitConverter.GetBytes((short)24).CopyTo(bytes, header + 14);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = header + 40 + (y * rowBytes) + (x * 3);
                bytes[offset] = 60;
                bytes[offset + 1] = 40;
                bytes[offset + 2] = 20;
            }
        }

        return bytes;
    }

    [Fact]
    public void GifPlaceholdersHaveIntrinsicPixelsAndValidGifsDecode()
    {
        // Apple's lazy-picture system uses a transparent 1x1 GIF as the selected source until
        // the real candidate enters its preload range.
        byte[] transparentGif =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x70, 0x00, 0x00, 0x21,
            0xf9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
            0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3b,
        ];
        Assert.Equal((1u, 1u), PaintResources.ImageDimensions(transparentGif));

        byte[] visibleGif =
        [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00,
            0x00, 0x00, 0xff, 0xff, 0xff, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
            0x00, 0x02, 0x01, 0x4c, 0x00, 0x3b,
        ];
        using Pixmap decoded = PaintResources.RasterToPixmap(visibleGif, 1, 1)!;
        Assert.Equal(1u, decoded.Width);
        Assert.Equal(1u, decoded.Height);
        Assert.Equal(255, Pixel(decoded, 0, 0).A);
    }

    [Fact]
    public void ScrolledViewportMovesDocumentContentButNotFixedSubtrees()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="height:80px;background:#ff0000"></div>
                <div style="height:80px;background:#0000ff"></div>
                <div style="position:fixed;z-index:10;left:0;top:0;width:20px;height:20px;background:#00ff00">
                    <span style="color:#00ff00">x</span>
                </div>
            </body></html>
            """);
        Pixmap top = RenderPaint.PaintDomScrolled(tree, (100f, 80f), null, (0f, 0f))!;
        Pixmap scrolled = RenderPaint.PaintDomScrolled(tree, (100f, 80f), null, (0f, 80f))!;

        PremultipliedColor topContent = Pixel(top, 50, 10);
        Assert.True(topContent.R > 240 && topContent.B < 15, $"{topContent}");
        PremultipliedColor scrolledContent = Pixel(scrolled, 50, 10);
        Assert.True(scrolledContent.B > 240 && scrolledContent.R < 15, $"{scrolledContent}");
        foreach ((string name, Pixmap pixmap) in (( string, Pixmap)[])[("top", top), ("scrolled", scrolled)])
        {
            PremultipliedColor fixedPixel = Pixel(pixmap, 5, 5);
            Assert.True(
                fixedPixel.G > 240 && fixedPixel.R < 15 && fixedPixel.B < 15,
                $"{name} viewport should keep fixed subtree at the viewport origin: {fixedPixel}");
        }
    }

    [Fact]
    public void RepeatedScrollCaptureMovesShapedTextAndItsOverflowClipTogether()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;overflow:auto"><body style="margin:0">
               <div style="height:1000px;background:#ff0000"></div>
               <section style="height:160px;overflow:hidden;background:#000000;color:#ffffff">
                 <h2 style="margin:0;font-size:32px;line-height:40px">VISIBLE SCROLLED TEXT</h2>
                 <p style="margin:0;font-size:20px;line-height:28px">SECOND SHAPED LINE</p>
                 <div style="position:absolute;left:260px;top:1100px;width:20px;height:20px;background:#00ff00"></div>
                 <svg style="position:absolute;left:260px;top:1040px;width:20px;height:20px"
                      viewBox="0 0 20 20"><rect width="20" height="20" fill="cyan"/></svg>
                 <div style="position:absolute;left:20px;top:1150px;width:20px;height:30px;background:#ff00ff"></div>
               </section>
               <div style="height:200px"></div>
               </body></html>
            """);
        (float Width, float Height) viewport = (300f, 180f);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, viewport, null, resources)!;
        Pixmap top = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        Pixmap scrolled = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 1000f))!;
        Pixmap topRepeat = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        Pixmap scrolledRepeat = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 1000f))!;

        Assert.Equal(top.Data(), topRepeat.Data());
        Assert.Equal(scrolled.Data(), scrolledRepeat.Data());
        int whiteInk = 0;
        for (uint y = 0; y < 90; y++)
        {
            for (uint x = 0; x < 250; x++)
            {
                PremultipliedColor pixel = Pixel(scrolled, x, y);
                if (pixel.R > 220 && pixel.G > 220 && pixel.B > 220)
                {
                    whiteInk++;
                }
            }
        }

        Assert.True(whiteInk > 100, $"visible shaped text must share the viewport-space overflow clip, found {whiteInk}");
        PremultipliedColor marker = Pixel(scrolled, 270, 110);
        Assert.True(marker.G > 220 && marker.R < 40 && marker.B < 40, $"{marker}");
        PremultipliedColor svgMarker = Pixel(scrolled, 270, 50);
        Assert.True(svgMarker.G > 220 && svgMarker.B > 220 && svgMarker.R < 40, $"{svgMarker}");
        PremultipliedColor clippedMarker = Pixel(scrolled, 30, 165);
        Assert.True(
            clippedMarker.R > 240 && clippedMarker.G > 240 && clippedMarker.B > 240,
            $"nested overflow must still clip content below its padding box: {clippedMarker}");
    }

    [Fact]
    public void BodyOverflowStaysAContentClipWhenHtmlOwnsRootOverflow()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;overflow:auto">
               <body style="margin:0;width:100px;height:50px;overflow:hidden">
                 <div style="position:absolute;left:10px;top:60px;width:20px;height:20px;background:red"></div>
               </body>
               </html>
            """);
        Pixmap output = RenderPaint.PaintDom(tree, (100f, 100f), null)!;
        PremultipliedColor belowBody = Pixel(output, 15, 65);
        Assert.True(
            belowBody.R > 240 && belowBody.G > 240 && belowBody.B > 240,
            $"body overflow must not be mistaken for a viewport clip: {belowBody}");
    }

    [Fact]
    public void RepeatedScrollCapturesReuseImmutableLayoutGeometry()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><head><style>
                @font-face { font-family: Fixture; src: url("https://assets.test/font.ttf"); }
                body { font-family: Fixture; }
            </style></head><body style="margin:0">
                <img id="hero" src="https://assets.test/fallback.svg"
                     srcset="https://assets.test/hero.svg 2x"
                     style="display:block;width:100px;height:auto">
                <div style="position:sticky;top:0;height:10px;background:#00ff00"></div>
                <div style="height:60px;background:#ff0000"></div>
                <div style="height:180px;overflow:hidden;background:#0000ff;
                            background-image:url('https://assets.test/background.svg')">
                    <div style="position:sticky;top:5px;height:20px;background:#00ff00"></div>
                    <div style="transform:translate(3px,4px);height:80px;color:#ffffff">stable text</div>
                </div>
                <div id="fixed" style="position:fixed;top:2px;left:2px;width:8px;height:8px"></div>
            </body></html>
            """);
        (float Width, float Height) viewport = (100f, 80f);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        byte[] fontBytes = FontAssets.Load("liberation-sans");
        RenderResourceCache resources = RenderResourceCache.WithLoader(url =>
        {
            counts[url] = counts.TryGetValue(url, out int existing) ? existing + 1 : 1;
            return url switch
            {
                "https://assets.test/font.ttf" => fontBytes,
                "https://assets.test/hero.svg" =>
                    """
                    <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100">
                        <rect width="200" height="100" fill="#ffff00"/>
                    </svg>
                    """u8.ToArray(),
                "https://assets.test/background.svg" =>
                    """
                    <svg xmlns="http://www.w3.org/2000/svg" width="20" height="20">
                        <rect width="20" height="20" fill="#0000ff"/>
                    </svg>
                    """u8.ToArray(),
                _ => null,
            };
        });
        PreparedRender prepared = RenderPaint.PrepareDom(tree, viewport, null, resources)!;
        NodeId hero = Selector(tree, "#hero");
        Assert.Equal(
            new SelectedImage("https://assets.test/hero.svg", 2f, ImageRequestProfile.NoCorsInclude),
            prepared.SelectedImage(hero));
        Rect heroRect = prepared.Layout.Rects[hero];
        Assert.True(MathF.Abs(heroRect.Width - 100f) < 0.1f);
        Assert.True(MathF.Abs(heroRect.Height - 50f) < 0.1f);
        Assert.True(prepared.ContentSize().Height > viewport.Height);
        Assert.False(prepared.StickyLayout().IsEmpty);
        Assert.Equal(
            prepared.DocumentRect(hero)!.Value.Y - 20f,
            prepared.ViewportRect(hero, (0f, 20f))!.Value.Y);
        NodeId fixedNode = Selector(tree, "#fixed");
        Assert.Contains(fixedNode, prepared.ViewportFixedNodes());
        Assert.Equal(prepared.DocumentRect(fixedNode), prepared.ViewportRect(fixedNode, (0f, 100f)));
        Dictionary<NodeId, Rect> baseRects = new(prepared.Layout.Rects);
        Dictionary<NodeId, (float X, float Y)> baseTranslates = new(prepared.Layout.Translates);
        Dictionary<NodeId, OverflowClip?> baseClips = new(prepared.Layout.ClipRects);

        byte[] near = RenderPaint.ScreenshotPrepared(tree, prepared, resources, (0f, 20f))!;
        byte[] far = RenderPaint.ScreenshotPrepared(tree, prepared, resources, (0f, 100f))!;
        byte[] farRepeat = RenderPaint.ScreenshotPrepared(tree, prepared, resources, (0f, 100f))!;
        byte[] nearAfterFar = RenderPaint.ScreenshotPrepared(tree, prepared, resources, (0f, 20f))!;

        Assert.NotEqual(near, far);
        Assert.Equal(far, farRepeat);
        Assert.Equal(near, nearAfterFar);
        Assert.Equal(baseRects, prepared.Layout.Rects);
        Assert.Equal(baseTranslates, prepared.Layout.Translates);
        Assert.Equal(baseClips, prepared.Layout.ClipRects);
        foreach (string url in (string[])
        [
            "https://assets.test/font.ttf",
            "https://assets.test/hero.svg",
            "https://assets.test/background.svg",
        ])
        {
            Assert.Equal(1, counts[url]);
        }

        Assert.DoesNotContain("https://assets.test/fallback.svg", counts.Keys);
        Assert.Equal(3, resources.RetainedEntryCount());
        Assert.True(resources.RetainedByteLen() > fontBytes.Length);
    }

    /// <summary>
    /// Portable pixel counterpart to the Chromium root-sticky geometry probe.
    /// </summary>
    [Fact]
    public void RootScrollStickyPaintsSubtreesAndRespectsBottomBoundary()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:#220022">
                <div style="height:40px;background:#ffffff"></div>
                <div style="box-sizing:border-box;height:900px;padding:10px 12px;border:4px solid #333;background:#dddddd">
                    <div style="box-sizing:border-box;position:sticky;top:20px;height:60px;margin:6px;background:#ff0000">
                        <div style="height:12px;background:#0000ff"></div>
                    </div>
                    <div style="height:500px"></div>
                    <div style="box-sizing:border-box;position:sticky;bottom:15px;height:50px;margin:5px;background:#ff8800"></div>
                </div>
                <div style="height:700px;background:#220022"></div>
                <div style="position:fixed;z-index:10;left:600px;top:20px;width:60px;height:60px;background:#00ff00"></div>
            </body></html>
            """);
        (float Width, float Height) viewport = (800f, 513f);
        Pixmap top = RenderPaint.PaintDomScrolled(tree, viewport, null, (0f, 0f))!;
        Pixmap stuck = RenderPaint.PaintDomScrolled(tree, viewport, null, (0f, 100f))!;
        Pixmap bottomNormal = RenderPaint.PaintDomScrolled(tree, viewport, null, (0f, 400f))!;
        Pixmap boundary = RenderPaint.PaintDomScrolled(tree, viewport, null, (0f, 9999f))!;

        static bool IsColor(PremultipliedColor pixel, byte r, byte g, byte b) =>
            Math.Abs(pixel.R - r) < 8 && Math.Abs(pixel.G - g) < 8 && Math.Abs(pixel.B - b) < 8;

        Assert.True(IsColor(Pixel(top, 100, 80), 255, 0, 0));
        Assert.True(IsColor(Pixel(top, 100, 460), 255, 136, 0));
        Assert.True(IsColor(Pixel(stuck, 100, 25), 0, 0, 255));
        Assert.True(IsColor(Pixel(stuck, 100, 50), 255, 0, 0));
        Assert.True(IsColor(Pixel(stuck, 100, 460), 255, 136, 0));
        Assert.True(IsColor(Pixel(bottomNormal, 100, 240), 255, 136, 0));
        Assert.True(IsColor(Pixel(boundary, 100, 20), 34, 0, 34));
        foreach (Pixmap pixmap in (Pixmap[])[top, stuck, bottomNormal, boundary])
        {
            Assert.True(IsColor(Pixel(pixmap, 620, 30), 0, 255, 0));
        }
    }

    [Fact]
    public void ObjectFitContainAndCoverCenterAndPreserveAspect()
    {
        Rect boxRect = new(10f, 20f, 200f, 100f);
        const float Iw = 100f;
        const float Ih = 100f;

        Rect c = PaintImages.ObjectFitDest(boxRect, Iw, Ih, ObjectFit.Contain);
        Assert.True(MathF.Abs(c.Width - 100f) < 0.01f && MathF.Abs(c.Height - 100f) < 0.01f, $"{c}");
        Assert.True(MathF.Abs((c.Width / c.Height) - (Iw / Ih)) < 1e-3f, $"{c}");
        Assert.True(MathF.Abs(c.X - 60f) < 0.01f, $"{c.X}");
        Assert.True(MathF.Abs(c.Y - 20f) < 0.01f, $"{c.Y}");
        Assert.True(c.X >= boxRect.X - 0.01f && c.X + c.Width <= boxRect.X + boxRect.Width + 0.01f);
        Assert.True(c.Y >= boxRect.Y - 0.01f && c.Y + c.Height <= boxRect.Y + boxRect.Height + 0.01f);

        Rect v = PaintImages.ObjectFitDest(boxRect, Iw, Ih, ObjectFit.Cover);
        Assert.True(MathF.Abs(v.Width - 200f) < 0.01f && MathF.Abs(v.Height - 200f) < 0.01f, $"{v}");
        Assert.True(MathF.Abs((v.Width / v.Height) - (Iw / Ih)) < 1e-3f, $"{v}");
        Assert.True(MathF.Abs(v.X - 10f) < 0.01f, $"{v.X}");
        Assert.True(MathF.Abs(v.Y + 30f) < 0.01f, $"{v.Y}");
        Assert.True(v.X <= boxRect.X + 0.01f && v.X + v.Width >= boxRect.X + boxRect.Width - 0.01f);
        Assert.True(v.Y <= boxRect.Y + 0.01f && v.Y + v.Height >= boxRect.Y + boxRect.Height - 0.01f);

        Rect box2 = new(0f, 0f, 200f, 200f);
        Rect sd = PaintImages.ObjectFitDest(box2, Iw, Ih, ObjectFit.ScaleDown);
        Assert.True(MathF.Abs(sd.Width - 100f) < 0.01f && MathF.Abs(sd.Height - 100f) < 0.01f, $"{sd}");
        Assert.True(MathF.Abs(sd.X - 50f) < 0.01f && MathF.Abs(sd.Y - 50f) < 0.01f, $"{sd}");
        Rect cn = PaintImages.ObjectFitDest(box2, Iw, Ih, ObjectFit.Contain);
        Assert.True(MathF.Abs(cn.Width - 200f) < 0.01f, $"{cn}");

        Rect n = PaintImages.ObjectFitDest(box2, Iw, Ih, ObjectFit.None);
        Assert.True(MathF.Abs(n.Width - 100f) < 0.01f && MathF.Abs(n.Height - 100f) < 0.01f, $"{n}");
        Assert.True(MathF.Abs(n.X - 50f) < 0.01f && MathF.Abs(n.Y - 50f) < 0.01f, $"{n}");

        Rect f = PaintImages.ObjectFitDest(boxRect, Iw, Ih, ObjectFit.Fill);
        Assert.True(
            MathF.Abs(f.Width - boxRect.Width) < 0.01f && MathF.Abs(f.Height - boxRect.Height) < 0.01f,
            $"{f}");
        Assert.True(MathF.Abs(f.X - boxRect.X) < 0.01f && MathF.Abs(f.Y - boxRect.Y) < 0.01f, $"{f}");
    }

    [Fact]
    public void PaintsBackgroundColor()
    {
        DomTree tree = Parse(
            "<html><body><div style=\"background-color: #ff0000; width: 100px; height: 80px\"></div></body></html>");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;
        Assert.Equal(200u, pixmap.Width);
        PremultipliedColor inside = Pixel(pixmap, 10, 10);
        Assert.True(inside.R > 200, $"expected red bg, got {inside}");
        Assert.True(inside.G < 60);
        Assert.True(inside.B < 60);
        Assert.Equal((255, 255, 255), Rgb(pixmap, 150, 150));
    }

    [Fact]
    public void BodyBackgroundTransfersToCanvasOnceOverBaseSurface()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;background:transparent">
               <body style="margin:0;width:20px;height:20px;background:rgba(255,0,0,.5)"></body>
               </html>
            """);
        Pixmap pixmap = RenderPaint.PaintDomScrolledAtAnimationTimeWithSurfaceColor(
            tree,
            (100f, 80f),
            null,
            (0f, 0f),
            default,
            new RgbaColor(0, 0, 255, 255))!;
        PremultipliedColor inside = Pixel(pixmap, 10, 10);
        PremultipliedColor outside = Pixel(pixmap, 90, 70);
        Assert.Equal(outside, inside);
        Assert.InRange(inside.R, (byte)127, (byte)128);
        Assert.Equal(0, inside.G);
        Assert.InRange(inside.B, (byte)127, (byte)128);
        Assert.Equal(255, inside.A);
    }

    [Fact]
    public void AuthoredHtmlBackgroundOwnsCanvasAndBodyKeepsItsBoxBackground()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;background:rgb(10,20,30)">
               <body style="margin:0;width:20px;height:20px;background:rgb(200,100,50)"></body>
               </html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (100f, 80f), null)!;
        Assert.Equal((200, 100, 50), Rgb(pixmap, 10, 10));
        Assert.Equal((10, 20, 30), Rgb(pixmap, 90, 70));
    }

    [Fact]
    public void TransparentHtmlAndBodyLeaveBaseSurfaceUnchanged()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;background:transparent">
               <body style="margin:0;background:transparent"></body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDomScrolledAtAnimationTimeWithSurfaceColor(
            tree,
            (40f, 30f),
            null,
            (0f, 0f),
            default,
            new RgbaColor(4, 8, 12, 255))!;
        foreach (PremultipliedColor pixel in pixmap.Pixels)
        {
            Assert.Equal((4, 8, 12, 255), (pixel.R, pixel.G, pixel.B, pixel.A));
        }
    }

    [Fact]
    public void TransferredCanvasBackgroundPaintsBelowNegativeZContent()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;background:transparent">
               <body style="margin:0;background:red">
                 <div style="position:absolute;z-index:-1;left:0;top:0;width:20px;height:20px;background:blue"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (60f, 40f), null)!;
        Assert.Equal((0, 0, 255), Rgb(pixmap, 10, 10));
        Assert.Equal((255, 0, 0), Rgb(pixmap, 40, 30));
    }

    [Fact]
    public void FloatPaintsAboveNormalBlockBackgroundsAndBelowLaterFlow()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #bfc{display:flow-root;width:200px}
                #float{float:right;width:50px;height:80px;background:#1971c2}
                #lead{height:50px;background:#087f5b}
                #heading{height:10px;background:#e8590c}
                #beside{height:20px;background:#7048e8}
                #after{height:20px;background:#a61e4d}
            </style>
            <main id="bfc">
              <aside id="float"></aside>
              <div id="lead"></div>
              <div id="heading"></div>
              <div id="beside"></div>
              <div id="after"></div>
            </main>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 120f), null)!;

        Assert.Equal((8, 127, 91), Rgb(pixmap, 25, 25));
        Assert.Equal((232, 89, 12), Rgb(pixmap, 25, 55));
        Assert.Equal((112, 72, 232), Rgb(pixmap, 25, 70));
        foreach (uint y in (uint[])[25, 55, 70])
        {
            Assert.Equal((25, 113, 194), Rgb(pixmap, 175, y));
        }

        Assert.Equal((166, 30, 77), Rgb(pixmap, 175, 90));
    }

    /// <summary>Chromium 150 oracle for CSS Backgrounds box geometry.</summary>
    [Fact]
    public void BackgroundOriginClipBoxesRadiiAndSamplingMatchChromium()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;background:white"><body style="margin:0;background:white">
              <style>
                .box { position:absolute;top:0;width:60px;height:40px;padding:10px;
                       border:10px solid transparent;background:#00aa00 }
                #border { left:0;background-clip:border-box }
                #padding { left:110px;background-clip:padding-box }
                #content { left:220px;background-clip:content-box }
                #radius { left:330px;border-radius:40px;background-clip:content-box }
                #gradient { position:absolute;left:0;top:100px;width:100px;height:40px;
                            border-left:20px solid transparent;padding-left:20px;
                            background-image:linear-gradient(90deg,red 0 50%,blue 50%);
                            background-origin:border-box;background-clip:content-box;
                            background-repeat:no-repeat }
                #wrapper { position:absolute;left:170px;top:100px;width:60px;height:40px;
                           overflow:hidden }
                #wide { width:140px;height:40px;
                        background:linear-gradient(90deg,red 0 50%,blue 50%) }
                #origin { position:absolute;left:280px;top:100px;width:100px;height:40px;
                          border-left:20px solid transparent;padding-left:20px;
                          background-image:linear-gradient(90deg,red,blue);
                          background-size:20px 20px;background-repeat:no-repeat;
                          background-origin:content-box;background-clip:border-box }
              </style>
              <div id="border" class="box"></div>
              <div id="padding" class="box"></div>
              <div id="content" class="box"></div>
              <div id="radius" class="box"></div>
              <div id="gradient"></div>
              <div id="wrapper"><div id="wide"></div></div>
              <div id="origin"></div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (500f, 220f), null)!;
        bool IsWhite(uint x, uint y)
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            return pixel.R > 240 && pixel.G > 240 && pixel.B > 240;
        }

        bool IsGreen(uint x, uint y)
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            return pixel.G > 140 && pixel.R < 30 && pixel.B < 30;
        }

        bool IsRed(uint x, uint y)
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            return pixel.R > 220 && pixel.B < 40;
        }

        bool IsBlue(uint x, uint y)
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            return pixel.B > 220 && pixel.R < 40;
        }

        Assert.True(IsGreen(5, 30), "border-box clip paints beneath transparent border");
        Assert.True(IsWhite(115, 30) && IsGreen(125, 30), "padding-box excludes border");
        Assert.True(IsWhite(225, 30) && IsWhite(235, 30) && IsGreen(245, 30));
        Assert.True(IsWhite(351, 21) && IsGreen(370, 40), "content radius must inset to 20px");
        Assert.True(IsRed(60, 120) && IsBlue(80, 120), "clip must not rebase gradient line");
        Assert.True(IsRed(220, 120), "ancestor clipping must not resize gradient coordinates");
        Assert.True(IsWhite(300, 120) && !IsWhite(325, 110), "content origin anchors no-repeat tile");
    }

    [Fact]
    public void RoundedOverflowClipsDescendantsAtThePaddingEdge()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:white">
                <div id="outer" style="position:absolute;left:10px;top:10px;box-sizing:border-box;
                     width:100px;height:100px;border:10px solid black;border-radius:30px;
                     overflow:hidden">
                  <div id="child" style="position:absolute;left:0;top:0;width:100px;height:100px;
                       background:red;transform:translate(-10px,-10px)"></div>
                </div>
            </body></html>
            """);
        NodeId child = ById(tree, "child");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (130f, 130f), null, resources)!;
        OverflowClip childClip = prepared.Layout.ClipRects[child]!;
        RoundedOverflowClipChain rounded = childClip.RoundedChain()!;
        Assert.Equal(new Rect(20f, 20f, 80f, 80f), rounded.Clip.Rect);
        Assert.Equal((20f, 20f), rounded.Clip.Radii.TopLeft);
        Mask directMask = PaintClips.OverflowClipMask(130, 130, childClip, (130f, 130f))!;
        Assert.Equal(0, directMask.Data[(23 * 130) + 23]);
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;

        Assert.Equal((0, 0, 0), Rgb(pixmap, 23, 23));
        Assert.Equal((255, 0, 0), Rgb(pixmap, 40, 25));
        Assert.Equal((255, 0, 0), Rgb(pixmap, 50, 50));
        Assert.Equal((0, 0, 0), Rgb(pixmap, 15, 50));
    }

    [Fact]
    public void NestedRoundedOverflowKeepsEveryClipChainNode()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:white">
                <div id="outer" style="position:absolute;left:10px;top:10px;width:100px;height:100px;
                     border-radius:40px;overflow:hidden;background:blue">
                  <div style="position:absolute;left:30px;top:0;width:80px;height:80px;
                       border-radius:20px;overflow:hidden">
                    <div id="child" style="width:80px;height:80px;background:lime"></div>
                  </div>
                </div>
            </body></html>
            """);
        NodeId child = ById(tree, "child");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (140f, 130f), null, resources)!;
        RoundedOverflowClipChain? chain = prepared.Layout.ClipRects[child]?.RoundedChain();
        int chainLength = 0;
        while (chain is not null)
        {
            chainLength++;
            chain = chain.Parent;
        }

        Assert.Equal(2, chainLength);
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;

        Assert.Equal((0, 255, 0), Rgb(pixmap, 70, 30));
        Assert.Equal((0, 0, 255), Rgb(pixmap, 43, 13));
        Assert.Equal((255, 255, 255), Rgb(pixmap, 105, 15));
    }

    [Fact]
    public void PaintsBorderWhenVarIsAdjacentToBorderStyleToken()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                body { margin:0 }
                #target {
                    --stroke:2px;
                    --ink:#e11d48;
                    width:30px;
                    height:30px;
                    border:var(--stroke)solid var(--ink);
                    background:#fff;
                }
            </style></head><body><div id="target"></div></body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (60f, 60f), null)!;
        PremultipliedColor border = Pixel(pixmap, 1, 1);
        Assert.True(
            border.R > 200 && border.G < 70 && border.B < 100,
            $"adjacent var() substitution must retain a painted red border: {border}");
        PremultipliedColor interior = Pixel(pixmap, 5, 5);
        Assert.True(
            interior.R > 245 && interior.G > 245 && interior.B > 245,
            $"the border must not consume the content box: {interior}");
    }

    [Fact]
    public void BorderOutlineComputedGeometryAndPixelsShareOneUsedModel()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0;background:white">
              <div id="none" style="width:100px;height:50px;border-width:10px;border-style:none"></div>
              <div id="decorated" style="position:absolute;left:30px;top:90px;width:100px;height:50px;
                   border-width:8px;border-style:solid;border-color:red green blue purple;
                   border-radius:30px 20px 14px 8px/20px 12px 10px 6px;
                   outline:4px dashed black;outline-offset:3px;background:#ffff00"></div>
            </body></html>
            """);
        NodeId none = ById(tree, "none");
        NodeId decorated = ById(tree, "decorated");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (180f, 180f), null, resources)!;

        Rect noneRect = prepared.DocumentRect(none)!.Value;
        Assert.Equal((100f, 50f), (noneRect.Width, noneRect.Height));
        Dictionary<string, string> noneStyle = prepared.ComputedStyle(none)!;
        Assert.Equal("0px", noneStyle["border-top-width"]);
        Assert.Equal("none", noneStyle["border-top-style"]);

        Rect rect = prepared.DocumentRect(decorated)!.Value;
        Assert.Equal((116f, 66f), (rect.Width, rect.Height));
        Dictionary<string, string> computed = prepared.ComputedStyle(decorated)!;
        Assert.Equal("rgb(255, 0, 0)", computed["border-top-color"]);
        Assert.Equal("rgb(0, 128, 0)", computed["border-right-color"]);
        Assert.Equal("rgb(0, 0, 255)", computed["border-bottom-color"]);
        Assert.Equal("rgb(128, 0, 128)", computed["border-left-color"]);
        Assert.Equal("30px 20px", computed["border-top-left-radius"]);
        Assert.Equal("8px 6px", computed["border-bottom-left-radius"]);
        Assert.Equal("4px", computed["outline-width"]);
        Assert.Equal("dashed", computed["outline-style"]);
        Assert.Equal("3px", computed["outline-offset"]);

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        PremultipliedColor top = Pixel(pixmap, (uint)(rect.X + (rect.Width / 2f)), (uint)(rect.Y + 2f));
        PremultipliedColor right = Pixel(pixmap, (uint)(rect.X + rect.Width - 2f), (uint)(rect.Y + (rect.Height / 2f)));
        PremultipliedColor bottom = Pixel(pixmap, (uint)(rect.X + (rect.Width / 2f)), (uint)(rect.Y + rect.Height - 2f));
        PremultipliedColor left = Pixel(pixmap, (uint)(rect.X + 2f), (uint)(rect.Y + (rect.Height / 2f)));
        Assert.True(top.R > 220 && top.G < 40 && top.B < 40, $"{top}");
        Assert.True(right.G > 90 && right.R < 40 && right.B < 40, $"{right}");
        Assert.True(bottom.B > 220 && bottom.R < 40 && bottom.G < 40, $"{bottom}");
        Assert.True(left.R > 90 && left.B > 90 && left.G < 40, $"{left}");

        PremultipliedColor roundedCorner = Pixel(pixmap, (uint)(rect.X + 1f), (uint)(rect.Y + 1f));
        Assert.False(
            roundedCorner.R > 220 && roundedCorner.G > 220 && roundedCorner.B < 40,
            $"asymmetric elliptical corner must stay outside the yellow background: {roundedCorner}");
        int outlinePixels = 0;
        uint outerX = (uint)F32.Max(rect.X - 8f, 0f);
        uint outerY = (uint)F32.Max(rect.Y - 8f, 0f);
        uint outerRight = (uint)(rect.X + rect.Width + 8f);
        uint outerBottom = (uint)(rect.Y + rect.Height + 8f);
        for (uint y = outerY; y < Math.Min(outerBottom, pixmap.Height); y++)
        {
            for (uint x = outerX; x < Math.Min(outerRight, pixmap.Width); x++)
            {
                bool outside = x < rect.X
                    || x >= rect.X + rect.Width
                    || y < rect.Y
                    || y >= rect.Y + rect.Height;
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                if (outside && pixel.R < 30 && pixel.G < 30 && pixel.B < 30)
                {
                    outlinePixels++;
                }
            }
        }

        Assert.True(outlinePixels > 40, $"dashed outline should paint outside layout: {outlinePixels}");
    }

    [Fact]
    public void OutlineNoneRetainsWidthButDoesNotChangeGeometryOrComputedUsedWidth()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0"><div id="box" style="width:40px;height:20px;
                outline-width:9px;outline-style:none"></div></body></html>
            """);
        NodeId id = ById(tree, "box");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (80f, 50f), null, resources)!;
        Rect rect = prepared.DocumentRect(id)!.Value;
        Assert.Equal((40f, 20f), (rect.Width, rect.Height));
        Assert.Equal(9f, prepared.Layout.Styles[id].Outline.SpecifiedWidth);
        Assert.Equal("0px", prepared.ComputedStyle(id)!["outline-width"]);
    }

    [Fact]
    public void DirectPureTextWebkitClampMatchesLineGeometryAndComputedValues()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
              <div id="clamped" style="width:120px;font:16px/20px Arial;
                display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;
                overflow:hidden">one<br>two<br>three<br>four<br>five</div>
              <div id="exact" style="width:120px;font:16px/20px Arial;
                display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;
                overflow:hidden">one<br>two</div>
              <div id="inactive" style="width:120px;font:16px/20px Arial;
                -webkit-box-orient:vertical;-webkit-line-clamp:2;overflow:hidden">
                one<br>two<br>three<br>four<br>five</div>
              <div id="nested" style="width:120px;font:16px/20px Arial;
                display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2;
                overflow:hidden"><div>one</div><div>two</div><div>three</div></div>
            </body></html>
            """);
        NodeId clamped = ById(tree, "clamped");
        NodeId exact = ById(tree, "exact");
        NodeId inactive = ById(tree, "inactive");
        NodeId nested = ById(tree, "nested");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (300f, 300f), null, resources)!;

        Assert.Equal(40f, prepared.DocumentRect(clamped)!.Value.Height);
        Assert.Equal(40f, prepared.DocumentRect(exact)!.Value.Height);
        Assert.Equal(100f, prepared.DocumentRect(inactive)!.Value.Height);
        Assert.Equal(60f, prepared.DocumentRect(nested)!.Value.Height);
        Dictionary<string, string> computed = prepared.ComputedStyle(clamped)!;
        Assert.Equal("flow-root", computed["display"]);
        Assert.Equal("2", computed["-webkit-line-clamp"]);
        Assert.Equal("vertical", computed["-webkit-box-orient"]);
        Assert.Equal("clip", computed["text-overflow"]);
    }

    [Fact]
    public void TruncationMarkersAreShapedAndPaintedWithoutChangingDomText()
    {
        DomTree ellipsisTree = Parse(
            """
            <html><body style="margin:0"><div id="nowrap" style="position:absolute;top:0;width:120px;font:16px/20px Arial;
              white-space:nowrap;overflow:hidden;text-overflow:ellipsis">ABCDEFGHIJKLMNO</div>
              <div id="clamp" style="position:absolute;top:20px;width:120px;font:16px/20px Arial;display:-webkit-box;
              -webkit-box-orient:vertical;-webkit-line-clamp:2;overflow:hidden">one<br>two<br>three</div>
              <div id="exact" style="position:absolute;top:80px;width:120px;font:16px/20px Arial;display:-webkit-box;
              -webkit-box-orient:vertical;-webkit-line-clamp:2;overflow:hidden">one<br>two</div>
              <div id="visible" style="position:absolute;top:120px;width:120px;font:16px/20px Arial;
              white-space:nowrap;overflow:visible;text-overflow:ellipsis">ABCDEFGHIJKLMNO</div>
              </body></html>
            """);
        DomTree clipTree = Parse(
            """
            <html><body style="margin:0"><div id="nowrap" style="position:absolute;top:0;width:120px;font:16px/20px Arial;
              white-space:nowrap;overflow:hidden;text-overflow:clip">ABCDEFGHIJKLMNO</div>
              <div id="clamp" style="position:absolute;top:20px;width:120px;font:16px/20px Arial;overflow:hidden">one<br>two<br>three</div>
              <div id="exact" style="position:absolute;top:80px;width:120px;font:16px/20px Arial;overflow:hidden">one<br>two</div>
              <div id="visible" style="position:absolute;top:120px;width:120px;font:16px/20px Arial;
              white-space:nowrap;overflow:visible;text-overflow:clip">ABCDEFGHIJKLMNO</div>
              </body></html>
            """);
        Pixmap ellipsis = RenderPaint.PaintDom(ellipsisTree, (180f, 160f), null)!;
        Pixmap clip = RenderPaint.PaintDom(clipTree, (180f, 160f), null)!;

        int Differences(uint x0, uint x1, uint y0, uint y1)
        {
            int count = 0;
            for (uint x = x0; x < x1; x++)
            {
                for (uint y = y0; y < y1; y++)
                {
                    if (!Pixel(ellipsis, x, y).Equals(Pixel(clip, x, y)))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        Assert.True(Differences(85, 120, 0, 20) > 8, "ellipsis must replace end glyph pixels");
        Assert.True(Differences(20, 55, 40, 60) > 4, "line clamp must paint a separately shaped marker");
        Assert.Equal(0, Differences(0, 140, 80, 120));
        Assert.Equal(0, Differences(0, 180, 120, 140));
        foreach (string id in (string[])["nowrap", "clamp", "exact", "visible"])
        {
            Assert.Equal(
                ellipsisTree.TextContent(ById(ellipsisTree, id)),
                clipTree.TextContent(ById(clipTree, id)));
        }
    }

    [Fact]
    public void PercentageBorderRadiusPaintsCirclesEllipsesAndReplacedClips()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <div id="circle" style="position:absolute;left:0;top:0;width:40px;height:40px;
                    border-radius:50%;background:#ff0000"></div>
               <div id="ellipse" style="position:absolute;left:50px;top:0;width:80px;height:40px;
                    border-radius:50%;background:#0000ff"></div>
               <div id="pill" style="position:absolute;left:140px;top:0;width:80px;height:40px;
                    border-radius:20px;background:#00aa00"></div>
               <img alt="" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='40'%20height='40'%3E%3Crect%20width='40'%20height='40'%20fill='%23800080'/%3E%3C/svg%3E"
                    style="position:absolute;left:230px;top:0;width:40px;height:40px;border-radius:50%">
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (280f, 50f), null)!;
        bool IsWhite(uint x, uint y)
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            return pixel.R > 245 && pixel.G > 245 && pixel.B > 245;
        }

        Assert.True(IsWhite(1, 1), "a square 50% radius must clear its corner");
        PremultipliedColor circleTop = Pixel(pixmap, 20, 1);
        Assert.True(circleTop.R > 200 && circleTop.G < 40 && circleTop.B < 40);

        Assert.True(IsWhite(60, 3), "a rectangular 50% radius must use a 40x20 elliptical corner");
        foreach (PremultipliedColor pixel in (PremultipliedColor[])[Pixel(pixmap, 90, 1), Pixel(pixmap, 51, 20)])
        {
            Assert.True(pixel.B > 200 && pixel.R < 40 && pixel.G < 40);
        }

        PremultipliedColor pillCorner = Pixel(pixmap, 150, 3);
        Assert.True(
            pillCorner.G > 100 && pillCorner.R < 40 && pillCorner.B < 40,
            $"a 20px radius must remain circular rather than resolving like 50%: {pillCorner}");

        Assert.True(IsWhite(231, 1), "a circular replaced image must clip its raster corner");
        PremultipliedColor imageCenter = Pixel(pixmap, 250, 20);
        Assert.True(imageCenter.R > 80 && imageCenter.B > 80 && imageCenter.G < 40);
    }

    [Fact]
    public void AutoBackgroundSizeUsesIntrinsicDimensionsAndPosition()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <div style="width:100px;height:100px;background-color:red;
                 background-image:url(&quot;data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='20'%20height='10'%3E%3Crect%20width='20'%20height='10'%20fill='blue'/%3E%3C/svg%3E&quot;);
                 background-position:right bottom;background-repeat:no-repeat"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (120f, 120f), null)!;
        PremultipliedColor background = Pixel(pixmap, 10, 10);
        Assert.True(background.R > 200 && background.B < 60, "the intrinsic image must not stretch across the owner");
        PremultipliedColor image = Pixel(pixmap, 90, 95);
        Assert.True(image.B > 200 && image.R < 60, "the 20x10 intrinsic image must anchor at bottom right");
    }

    [Fact]
    public void CoverAndContainBackgroundSizesFitThePositioningArea()
    {
        const string Image =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='200'%20height='100'%3E%3Crect%20width='200'%20height='100'%20fill='blue'/%3E%3C/svg%3E";
        DomTree tree = Parse($$"""
            <html><body style="margin:0;background:white">
               <div style="position:absolute;left:0;top:0;width:100px;height:200px;
                 background:red url(&quot;{{Image}}&quot;) center/cover no-repeat"></div>
               <div style="position:absolute;left:100px;top:0;width:100px;height:200px;
                 background-color:red;background-image:url(&quot;{{Image}}&quot;);
                 background-position:center;background-size:contain;background-repeat:no-repeat"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;

        PremultipliedColor coverEdge = Pixel(pixmap, 50, 10);
        Assert.True(coverEdge.B > 200 && coverEdge.R < 40, $"{coverEdge}");
        PremultipliedColor containOutside = Pixel(pixmap, 150, 60);
        Assert.True(containOutside.R > 200 && containOutside.B < 40, $"{containOutside}");
        PremultipliedColor containCenter = Pixel(pixmap, 150, 100);
        Assert.True(containCenter.B > 200 && containCenter.R < 40, $"{containCenter}");
    }

    [Fact]
    public void OrderedBackgroundGradientsPaintEveryTranslucentLayer()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <div style="width:100px;height:100px;background-color:white;
                 background-image:
                   linear-gradient(180deg,transparent,white 85%),
                   radial-gradient(circle at top left,rgba(255,0,0,.9),transparent 50%),
                   radial-gradient(circle at top right,rgba(0,0,255,.9),transparent 50%)">
               </div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (100f, 100f), null)!;
        PremultipliedColor left = Pixel(pixmap, 4, 4);
        PremultipliedColor right = Pixel(pixmap, 95, 4);
        Assert.True(left.R > left.B + 80, $"{left}");
        Assert.True(right.B > right.R + 80, $"{right}");
    }

    [Fact]
    public void RepeatingLinearGradientTilesAtBackgroundSizeOverOrderedLayer()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <div style="width:40px;height:40px;background-color:white;
                 background-image:
                   repeating-linear-gradient(315deg,
                     rgba(0,0,0,.65) 0,rgba(0,0,0,.65) 1px,
                     transparent 0,transparent 50%),
                   linear-gradient(red,red);
                 background-size:10px 10px">
               </div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (40f, 40f), null)!;
        int dark = 0;
        int red = 0;
        for (uint y = 0; y < 10; y++)
        {
            for (uint x = 0; x < 10; x++)
            {
                PremultipliedColor first = Pixel(pixmap, x, y);
                Assert.Equal(first, Pixel(pixmap, x + 20, y + 20));
                if (first.R < 150)
                {
                    dark++;
                }
                else if (first.R > 220 && first.G < 40 && first.B < 40)
                {
                    red++;
                }
            }
        }

        Assert.True(dark > 4, "the repeating hatch must paint dark stripes");
        Assert.True(red > 20, "transparent hatch gaps must reveal the ordered red layer");
    }

    [Fact]
    public void LengthBackgroundPositionsSelectSpriteFrames()
    {
        const string Sprite =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='48'%20height='24'%3E%3Crect%20width='24'%20height='24'%20fill='%23ff0000'/%3E%3Crect%20x='24'%20width='24'%20height='24'%20fill='%230000ff'/%3E%3C/svg%3E";
        DomTree tree = Parse($$"""
            <html><body style="margin:0;background:white">
               <div style="position:absolute;left:0;top:0;width:24px;height:42px;
                 background-color:black;background-image:url(&quot;{{Sprite}}&quot;);
                 background-size:48px 24px;background-position:0;background-repeat:no-repeat"></div>
               <div style="position:absolute;left:30px;top:0;width:24px;height:42px;
                 background-color:black;background-image:url(&quot;{{Sprite}}&quot;);
                 background-size:48px 24px;background-position:-24px;background-repeat:no-repeat"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (60f, 45f), null)!;

        PremultipliedColor first = Pixel(pixmap, 12, 21);
        Assert.True(first.R > 200 && first.G < 40 && first.B < 40, $"{first}");
        PremultipliedColor second = Pixel(pixmap, 42, 21);
        Assert.True(second.B > 200 && second.R < 40 && second.G < 40, $"{second}");
        PremultipliedColor above = Pixel(pixmap, 12, 2);
        Assert.True(above.R < 30 && above.G < 30 && above.B < 30, $"{above}");
    }

    [Fact]
    public void NegativeTextIndentClipsLabelWithoutHidingBackgroundIcon()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <a style="display:block;width:24px;height:24px;overflow:hidden;
                 white-space:nowrap;text-indent:-9999px;color:black;background-color:blue;
                 background-image:url(&quot;data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='16'%20height='16'%3E%3Crect%20width='16'%20height='16'%20fill='red'/%3E%3C/svg%3E&quot;);
                 background-position:center;background-size:16px 16px;background-repeat:no-repeat">Bluesky (@mozilla.org)</a>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (32f, 32f), null)!;
        PremultipliedColor corner = Pixel(pixmap, 1, 1);
        Assert.True(corner.B > 200 && corner.R < 40 && corner.G < 40, $"{corner}");
        PremultipliedColor icon = Pixel(pixmap, 12, 12);
        Assert.True(icon.R > 200 && icon.G < 40 && icon.B < 40, $"{icon}");
        for (uint y = 0; y < 24; y++)
        {
            for (uint x = 0; x < 24; x++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                Assert.True(
                    pixel.R > 40 || pixel.G > 40 || pixel.B > 40,
                    $"black label glyph leaked through the 24px overflow clip at ({x},{y})");
            }
        }
    }

    [Fact]
    public void ContextualBackgroundSizePreservesAutoAxisRatio()
    {
        const string Source =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='200'%20height='50'%3E%3C/svg%3E";
        Rect owner = new(0f, 0f, 132f, 60f);
        RenderResourceCache cache = new();
        Rect image = PaintImages.BackgroundImageRect(
            Source,
            null,
            owner,
            null,
            "calc(100% - 2rem) auto",
            null,
            BackgroundPosition.New(
                BackgroundPositionAxis.Percentage(0f),
                BackgroundPositionAxis.Percentage(0.5f)),
            10f,
            10f,
            (1280f, 720f),
            cache)!.Value;
        Assert.Equal(112f, image.Width);
        Assert.Equal(28f, image.Height);
        Assert.Equal(16f, image.Y);
    }

    [Fact]
    public void PaintsPositionedEmptyPseudoBackgroundBox()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               body { margin:0 }
               #host { position:relative; width:100px; height:50px }
               #host::before {
                 content:"";
                 position:absolute;
                 top:10px;
                 left:20px;
                 width:40px;
                 height:30px;
                 background:
                   linear-gradient(to bottom, transparent, #ffffff),
                   radial-gradient(circle at 50% 50%, #ebf3f9, #d6dee4);
               }
               </style></head><body><div id="host"></div></body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (120f, 80f), null)!;
        PremultipliedColor center = Pixel(pixmap, 40, 25);
        Assert.True(
            center.R >= 214 && center.G >= 222 && center.B >= 228,
            $"transparent-to-white over a light radial layer must not darken it: {center}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 5, 5));
    }

    [Fact]
    public void DisplayNoneSuppressesAnAbsolutelyPositionedPseudo()
    {
        // DEVIATION from the Rust reference, whose paint_positioned_pseudo guards on
        // `position: absolute` alone. An out-of-flow pseudo never reaches the taffy tree, so
        // the `display: none` that suppresses an in-flow one is not applied anywhere else
        // either and the box paints regardless. Tesserae hides an unselected radio's dot with
        // `display: none` on an absolutely positioned pseudo, so every radio and checkbox in
        // the samples painted as selected.
        DomTree tree = Parse(
            """
            <html><head><style>
               html, body { margin:0 }
               div { position:absolute; top:0; width:40px; height:40px }
               div::after { content:""; position:absolute; inset:0; background:#248efa }
               #hidden { left:0 }
               #hidden::after { display:none }
               #shown { left:60px }
               #invisible { left:120px; visibility:hidden }
               </style></head><body>
                 <div id="hidden"></div><div id="shown"></div><div id="invisible"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 60f), null)!;

        Assert.Equal((255, 255, 255), Rgb(pixmap, 20, 20));
        Assert.Equal((255, 255, 255), Rgb(pixmap, 140, 20));

        PremultipliedColor shown = Pixel(pixmap, 80, 20);
        Assert.True(
            shown.B > 180 && shown.R < 120,
            $"a positioned pseudo with no display:none must still paint: {shown}");
    }

    [Fact]
    public void PolygonClipPathPaintsResponsiveGeometryOnElementsAndPseudos()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               html, body { margin:0 }
               @supports (clip-path:polygon(0 0,100% 0,50% 100%)) {
                 .triangle, #in-flow::before, #positioned::after {
                   clip-path:polygon(0 0,100% 0,50% 100%);
                 }
               }
               .triangle { position:absolute; left:0; top:0;
                 width:80px; height:80px; background:#f00 }
               #in-flow { position:absolute; left:100px; top:0 }
               #in-flow::before { content:""; display:block;
                 width:80px; height:80px; background:#0a0 }
               #positioned { position:absolute; left:200px; top:0;
                 width:80px; height:80px }
               #positioned::after { content:""; position:absolute; inset:0;
                 background:#00f }
               </style></head><body>
                 <div class="triangle"></div>
                 <div id="in-flow"></div>
                 <div id="positioned"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (300f, 100f), null)!;
        foreach ((string name, uint left, int channel) in ((string, uint, int)[])
        [
            ("ordinary element", 0, 0),
            ("in-flow pseudo", 100, 1),
            ("positioned pseudo", 200, 2),
        ])
        {
            PremultipliedColor inside = Pixel(pixmap, left + 40, 65);
            bool colored = channel switch
            {
                0 => inside.R > 180 && inside.G < 80 && inside.B < 80,
                1 => inside.G > 100 && inside.R < 80 && inside.B < 80,
                _ => inside.B > 180 && inside.R < 80 && inside.G < 80,
            };
            Assert.True(colored, $"{name} must paint inside its percentage polygon: {inside}");
            Assert.Equal((255, 255, 255), Rgb(pixmap, left + 4, 65));
        }
    }

    [Fact]
    public void PolygonClipPathHonorsEvenoddFillRule()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <div style="width:100px;height:100px;background:#e00;
                 clip-path:polygon(evenodd,
                   0 0,100% 0,100% 100%,0 100%,0 25%,
                   75% 25%,75% 75%,25% 75%,25% 25%,0 25%)"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (120f, 120f), null)!;
        PremultipliedColor shell = Pixel(pixmap, 10, 50);
        Assert.True(shell.R > 180 && shell.G < 80, $"{shell}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 50, 50));
    }

    [Fact]
    public void DegeneratePolygonClipPathClipsTheElementAway()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <div style="width:80px;height:80px;background:red;
                 clip-path:polygon(20px 20px)"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (100f, 100f), null)!;
        foreach ((uint x, uint y) in ((uint, uint)[])[(10, 10), (20, 20), (40, 40)])
        {
            Assert.Equal((255, 255, 255), Rgb(pixmap, x, y));
        }
    }

    [Fact]
    public void PaintsGeneratedStyleImagesAndSizesContentUrlAsReplacedContent()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               body { margin:0 }
               #host { position:relative; width:100px; height:40px }
               #host::before {
                 content:""; position:absolute; left:0; top:0;
                 width:40px; height:40px;
                 background-image:url("data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='40'%20height='40'%3E%3Crect%20width='40'%20height='40'%20fill='blue'/%3E%3C/svg%3E");
                 background-size:100% 100%;
               }
               #host::after {
                 content:""; position:absolute; left:50px; top:0;
                 width:40px; height:40px; background-color:red;
                 mask-image:url("data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='40'%20height='40'%3E%3Ccircle%20cx='20'%20cy='20'%20r='16'%20fill='white'/%3E%3C/svg%3E");
                 mask-size:40px 40px; mask-repeat:no-repeat;
               }
               #content-image {
                 display:block;
                 content:url("data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='30'%20height='20'%3E%3Crect%20width='30'%20height='20'%20fill='lime'/%3E%3C/svg%3E");
               }
               </style></head><body>
                 <div id="host"></div><img id="content-image" alt="">
               </body></html>
            """);

        // The first cascade exposes the style image; feeding its decoded dimensions back
        // through the ordinary intrinsic map must give the source-less replaced element its box.
        Dictionary<NodeId, ReplacedIntrinsic> intrinsic = [];
        DomLayout first = RenderDom.LayoutDomWithWebFonts(tree, (120f, 80f), intrinsic, []);
        NodeId hostId = Selector(tree, "#host");
        LayoutStyle hostStyle = first.Styles[hostId];
        Assert.NotNull(hostStyle.BeforePseudo?.BackgroundImage);
        Assert.NotNull(hostStyle.AfterPseudo?.MaskImage);
        Rect hostRect = first.Rects[hostId];
        Assert.Equal((0f, 0f, 100f, 40f), (hostRect.X, hostRect.Y, hostRect.Width, hostRect.Height));
        RenderResourceCache cache = new();
        Dictionary<NodeId, SelectedImage> selected = [];
        Assert.True(PaintImages.CollectContentImageIntrinsics(
            tree,
            first.Styles,
            null,
            cache,
            intrinsic,
            selected,
            new Dictionary<NodeId, ReplacedIntrinsic>(),
            new Dictionary<NodeId, SelectedImage>(),
            new HashSet<NodeId>()));
        DomLayout laid = RenderDom.LayoutDomWithWebFonts(tree, (120f, 80f), intrinsic, []);
        NodeId imageId = Selector(tree, "#content-image");
        Rect imageRect = laid.Rects[imageId];
        Assert.Equal(
            (0f, 40f, 30f, 20f),
            (imageRect.X, imageRect.Y, imageRect.Width, imageRect.Height));

        Pixmap pixmap = RenderPaint.PaintDom(tree, (120f, 80f), null)!;
        PremultipliedColor blue = Pixel(pixmap, 20, 20);
        Assert.True(blue.B > 220 && blue.R < 40 && blue.G < 80, $"{blue}");
        PremultipliedColor red = Pixel(pixmap, 70, 20);
        Assert.True(red.R > 220 && red.G < 40 && red.B < 40, $"{red}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 51, 1));
        PremultipliedColor green = Pixel(pixmap, 15, 50);
        Assert.True(green.G > 220 && green.R < 40 && green.B < 40, $"{green}");
    }

    [Fact]
    public void RepeatedContentImagePrepareReusesIntrinsicAndCorrectsChanges()
    {
        const string Source =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='12'%20height='8'%3E%3C/svg%3E";
        const string ContentA =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='30'%20height='20'%3E%3C/svg%3E";
        const string ContentB =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='50'%20height='10'%3E%3C/svg%3E";
        static DomTree MakeTree(string? content)
        {
            string declaration = content is null ? string.Empty : $"content:url('{content}');";
            return Parse(
                $"""<html><body style="margin:0"><img id="target" src="{Source}" style="display:block;{declaration}"></body></html>""");
        }

        static Rect RectFor(DomTree tree, PreparedRender prepared) =>
            prepared.Layout.Rects[Selector(tree, "#target")];

        RenderResourceCache resources = new();
        DomTree firstTree = MakeTree(ContentA);
        PreparedRender first = RenderPaint.PrepareDom(firstTree, (100f, 80f), null, resources)!;
        Rect firstRect = RectFor(firstTree, first);
        Assert.Equal((30f, 20f), (firstRect.Width, firstRect.Height));
        Assert.Equal(1, resources.ContentImageLayoutRetries);

        PreparedRender repeated = RenderPaint.PrepareDom(firstTree, (100f, 80f), null, resources)!;
        Rect repeatedRect = RectFor(firstTree, repeated);
        Assert.Equal((30f, 20f), (repeatedRect.Width, repeatedRect.Height));
        Assert.Equal(1, resources.ContentImageLayoutRetries);

        DomTree changedTree = MakeTree(ContentB);
        PreparedRender changed = RenderPaint.PrepareDom(changedTree, (100f, 80f), null, resources)!;
        Rect changedRect = RectFor(changedTree, changed);
        Assert.Equal((50f, 10f), (changedRect.Width, changedRect.Height));
        Assert.Equal(2, resources.ContentImageLayoutRetries);

        DomTree removedTree = MakeTree(null);
        PreparedRender removed = RenderPaint.PrepareDom(removedTree, (100f, 80f), null, resources)!;
        NodeId removedId = Selector(removedTree, "#target");
        Rect removedRect = removed.Layout.Rects[removedId];
        Assert.Equal((12f, 8f), (removedRect.Width, removedRect.Height));
        Assert.Equal(3, resources.ContentImageLayoutRetries);
        Assert.Empty(resources.ContentImageIntrinsics);
        Assert.Equal(Source, removed.SelectedImages[removedId].ResolvedUrl);
    }

    [Fact]
    public void ContentImageIntrinsicMemoryIsBoundedAndRefreshesRecency()
    {
        RenderResourceCache resources = RenderResourceCache.WithLoaderAndLimits(_ => null, 2, 1024);
        DomTree tree = Parse("""<html><body><img id="a"><img id="b"><img id="c"></body></html>""");
        NodeId[] ids = [Selector(tree, "#a"), Selector(tree, "#b"), Selector(tree, "#c")];
        resources.RememberContentImageIntrinsic(ids[0], "a", ReplacedIntrinsic.FromDimensions(1f, 1f));
        resources.RememberContentImageIntrinsic(ids[1], "b", ReplacedIntrinsic.FromDimensions(2f, 2f));

        // Refreshing id 1 makes id 2 the oldest entry.
        resources.RememberContentImageIntrinsic(ids[0], "a2", ReplacedIntrinsic.FromDimensions(3f, 3f));
        resources.RememberContentImageIntrinsic(ids[2], "c", ReplacedIntrinsic.FromDimensions(4f, 4f));

        Assert.Equal(2, resources.ContentImageIntrinsics.Count);
        Assert.Contains(ids[0], resources.ContentImageIntrinsics.Keys);
        Assert.DoesNotContain(ids[1], resources.ContentImageIntrinsics.Keys);
        Assert.Contains(ids[2], resources.ContentImageIntrinsics.Keys);
        Assert.Equal("a2", resources.ContentImageIntrinsics[ids[0]].ResolvedUrl);
    }

    [Fact]
    public void RepeatedDataSvgMasksSampleRadialSourcesOnEveryBoxPath()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               html, body { margin:0 }
               .mask {
                 width:88px; height:66px;
               }
               .mask-source, #in-flow::before, #positioned::after {
                 mask-image:url("data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' width='72' height='72' viewBox='0 0 72 72'><defs><pattern id='p' patternUnits='userSpaceOnUse' width='72' height='72'><g transform='translate(36 36) rotate(-60)'><line x1='-10' y1='0' x2='10' y2='0' stroke='white' stroke-width='3' stroke-linecap='round'/></g></pattern></defs><rect width='100%' height='100%' fill='url(%23p)'/></svg>");
                 mask-size:22px 22px; mask-repeat:repeat;
               }
               #ordinary { position:absolute; left:0; top:0 }
               #in-flow { position:absolute; left:100px; top:0 }
               #in-flow::before {
                 content:""; display:block;
                 width:88px; height:66px;
                 background:radial-gradient(circle at 50% 125%,transparent 20%,#f627e3 35%,#6911d2 55%,transparent 75%);
               }
               #positioned { position:absolute; left:200px; top:0 }
               #positioned::after {
                 content:""; position:absolute; inset:0;
                 background:radial-gradient(circle at 50% 125%,transparent 20%,#f627e3 35%,#6911d2 55%,transparent 75%);
               }
               #ordinary {
                 background:radial-gradient(circle at 50% 125%,transparent 20%,#f627e3 35%,#6911d2 55%,transparent 75%);
               }
               #solid-source { position:absolute; left:0; top:80px; background-color:#00aa00 }
               #linear-source { position:absolute; left:100px; top:80px; background:linear-gradient(90deg,#ff0000,#0000ff) }
               #conic-source { position:absolute; left:200px; top:80px; background:conic-gradient(from 0deg at 50% 50%,#ff0000,#0000ff,#ff0000) }
               </style></head><body>
                 <div id="ordinary" class="mask mask-source"></div>
                 <div id="in-flow" class="mask"></div>
                 <div id="positioned" class="mask"></div>
                 <div id="solid-source" class="mask mask-source"></div>
                 <div id="linear-source" class="mask mask-source"></div>
                 <div id="conic-source" class="mask mask-source"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (300f, 160f), null)!;
        int CountPixels(uint left, uint top, Func<byte, byte, byte, bool> predicate)
        {
            int count = 0;
            for (uint y = top; y < top + 66; y++)
            {
                for (uint x = left; x < left + 88; x++)
                {
                    PremultipliedColor pixel = Pixel(pixmap, x, y);
                    if (predicate(pixel.R, pixel.G, pixel.B))
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        static bool IsRadialColor(byte red, byte green, byte blue) =>
            red > 70 && blue > 100 && blue > green * 2;

        foreach ((string name, uint left) in ((string, uint)[])
        [
            ("ordinary element", 0),
            ("in-flow pseudo", 100),
            ("positioned pseudo", 200),
        ])
        {
            int colored = CountPixels(left, 0, IsRadialColor);
            Assert.True(colored > 20, $"{name} must sample the radial source through the mask, found {colored}");
            int black = CountPixels(left, 0, (r, g, b) => r < 20 && g < 20 && b < 20);
            Assert.Equal(0, black);
        }

        Assert.True(CountPixels(0, 80, (r, g, b) => g > 100 && r < 40 && b < 40) > 20);
        Assert.True(CountPixels(100, 80, (r, g, b) => (r > 100 || b > 100) && g < 80) > 20);
        Assert.True(CountPixels(200, 80, (r, g, b) => (r > 100 || b > 100) && g < 80) > 20);
    }

    [Fact]
    public void PaintsEmptyInFlowGeneratedBlockAtItsLayoutRect()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               html, body { margin:0 }
               body { font-size:20px; line-height:20px }
               #host { width:200px }
               #host::before {
                 content:""; display:block; width:80px; height:40px;
                 margin-bottom:10px; background:#0066cc;
               }
               #next { width:20px; height:10px; background:#00aa00 }
               </style></head><body>
                 <div id="host">TEXT</div><div id="next"></div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (240f, 100f), null)!;
        PremultipliedColor generated = Pixel(pixmap, 40, 20);
        Assert.True(generated.B > 180 && generated.G > 70 && generated.R < 30, $"{generated}");
        PremultipliedColor following = Pixel(pixmap, 10, 75);
        Assert.True(following.G > 120 && following.R < 30 && following.B < 30, $"{following}");
    }

    [Fact]
    public void PaintsPositionedAttrContentOverTheHostBackground()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               body { margin:0 }
               #cta {
                 position:relative; width:120px; height:40px; border:0;
                 padding:0; color:transparent; background:red;
               }
               #cta::before {
                 content:attr(data-label);
                 position:absolute; inset:1px;
                 display:flex; align-items:center; justify-content:center;
                 border-radius:4px; color:black; background:white;
               }
               </style></head><body>
               <button id="cta" data-label="Get Started">Get Started</button>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (140f, 60f), null)!;
        Assert.Equal((255, 255, 255), Rgb(pixmap, 5, 5));
        int darkPixels = 0;
        for (uint x = 35; x < 85; x++)
        {
            for (uint y = 8; y < 32; y++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                if (pixel.R < 100 && pixel.G < 100 && pixel.B < 100)
                {
                    darkPixels++;
                }
            }
        }

        Assert.True(darkPixels > 10, "generated attr() text must be painted");
    }

    [Fact]
    public void LaterPositionedPseudoOpaquelyCoversTheEarlierOne()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               body { margin:0 }
               #cta {
                 position:relative; width:120px; height:40px; padding:0;
                 color:transparent; background:black;
               }
               #cta::before {
                 content:"before";
                 position:absolute; inset:1px;
                 display:flex; align-items:center; justify-content:center;
                 color:red; background:red;
               }
               #cta::after {
                 content:"after";
                 position:absolute; inset:1px;
                 display:flex; align-items:center; justify-content:center;
                 color:blue; background:white;
               }
               </style></head><body><button id="cta">host</button></body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (140f, 60f), null)!;
        Assert.Equal((255, 255, 255), Rgb(pixmap, 5, 5));
        int redPixels = 0;
        for (uint x = 1; x < 119; x++)
        {
            for (uint y = 1; y < 39; y++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                if (pixel.R > 180 && pixel.G < 80 && pixel.B < 80)
                {
                    redPixels++;
                }
            }
        }

        Assert.Equal(0, redPixels);
    }

    [Fact]
    public void AngularAbsolutePrimaryButtonPaintsOneGeneratedLabel()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               :root {
                 --orange-red:#f00; --vivid-pink:#f0f; --electric-violet:#70f;
                 --page-bg-radial-gradient:radial-gradient(circle,#fff 0%,#fff 100%);
                 --page-background:#fff; --primary-contrast:#111;
               }
               html, body { margin:0 }
               .section { position:relative; width:500px; height:300px }
               .content button { position:absolute; bottom:48px }
               .docs-primary-btn {
                 cursor:pointer; border:none; outline:none; position:relative;
                 border-radius:4px; padding:12px 24px; width:max-content;
                 color:transparent; font-size:14px; font-weight:600;
                 background:linear-gradient(90deg,var(--orange-red) 0%,
                   var(--vivid-pink) 50%,var(--electric-violet) 100%);
               }
               .docs-primary-btn::before {
                 content:attr(text); position:absolute; inset:1px;
                 background:var(--page-bg-radial-gradient); border-radius:3px;
                 display:flex; align-items:center; justify-content:center;
                 color:var(--primary-contrast);
               }
               .docs-primary-btn::after {
                 content:attr(text); position:absolute; inset:1px;
                 background:var(--page-background); border-radius:3px;
                 display:flex; align-items:center; justify-content:center;
                 color:var(--primary-contrast);
               }
               </style></head><body>
                 <section class="section"><div class="content">
                   <button id="cta" class="docs-primary-btn" text="Learn more">Learn more</button>
                 </div></section>
               </body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 300f));
        NodeId cta = ById(tree, "cta");
        Rect rect = laid.Rects[cta];
        LayoutStyle style = laid.Styles[cta];
        Assert.Equal(new RgbaColor(0, 0, 0, 0), style.Color);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (500f, 300f), null)!;
        List<int> rows = [];
        for (uint y = (uint)MathF.Floor(rect.Y); y < (uint)MathF.Ceiling(rect.Y + rect.Height); y++)
        {
            int ink = 0;
            for (uint x = (uint)MathF.Floor(rect.X); x < (uint)MathF.Ceiling(rect.X + rect.Width); x++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                if (pixel.R < 100 && pixel.G < 100 && pixel.B < 100)
                {
                    ink++;
                }
            }

            rows.Add(ink);
        }

        List<int> inkRows = [];
        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row] > 0)
            {
                inkRows.Add(row);
            }
        }

        Assert.True(
            inkRows[^1] - inkRows[0] <= 14,
            $"only the generated label may paint; transparent host text leaked across rows [{string.Join(", ", rows)}]");
    }

    [Fact]
    public void NativeSelectPaintsOnlyTheSelectedLabelAndArrow()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <select id="theme">
                    <option>Light</option>
                    <option selected>Dark</option>
                </select>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (160f, 60f), null)!;
        int darkPixels = 0;
        for (uint x = 0; x < 120; x++)
        {
            for (uint y = 0; y < 30; y++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                if (pixel.R < 100 && pixel.G < 100 && pixel.B < 100)
                {
                    darkPixels++;
                }
            }
        }

        Assert.True(darkPixels > 20, "selected label, border, and disclosure arrow should paint");
        NodeId select = ById(tree, "theme");
        Assert.Equal("Dark", PaintText.SelectedOptionLabel(tree, select));
    }

    [Fact]
    public void AuthoredComboboxButtonPaintsCssMathBorder()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><head><style>
                :root {
                    --stroke-standard: calc(1 * 1px);
                    --control-height: calc(4px * 10);
                    --control-radius: calc(4px * 2);
                }
                #host { width:260px }
                #language {
                    display:flex;
                    align-items:center;
                    justify-content:space-between;
                    width:100%;
                    height:var(--control-height);
                    box-sizing:border-box;
                    border-style:solid;
                    border-width:var(--stroke-standard);
                    border-color:rgba(208,217,251,.4);
                    border-radius:var(--control-radius);
                    padding:1px 12px;
                }
            </style></head><body style="margin:0">
                <div id="host">
                    <button id="language" role="combobox">
                        <span>English (United States)</span><span>&#x25BC;</span>
                    </button>
                </div>
            </body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (320f, 80f));
        NodeId language = ById(tree, "language");
        Rect rect = laid.Rects[language];
        LayoutStyle style = laid.Styles[language];
        Assert.Equal((260f, 40f), (rect.Width, rect.Height));
        Assert.Equal(new Edges(1f, 1f, 1f, 1f), style.Border);

        Pixmap pixmap = RenderPaint.PaintDom(tree, (320f, 80f), null)!;
        int paintedTopEdge = 0;
        for (uint x = 20; x < 240; x++)
        {
            if (Pixel(pixmap, x, 0).A > 0)
            {
                paintedTopEdge++;
            }
        }

        Assert.True(paintedTopEdge > 200, $"the rounded authored border should paint across the control: {paintedTopEdge}");
    }


    [Fact]
    public void FractionalOpacityCompositesBoxesAndInlineSvgOnce()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <div style="width:20px;height:20px;background:black;opacity:.05"></div>
                <svg style="position:absolute;left:30px;top:0;opacity:.05"
                     width="20" height="20" viewBox="0 0 20 20">
                    <rect width="20" height="20" fill="black"/>
                </svg>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (60f, 30f), null)!;
        foreach ((string label, uint x) in ((string, uint)[])[("box", 10), ("svg", 40)])
        {
            PremultipliedColor pixel = Pixel(pixmap, x, 10);
            Assert.True(
                pixel.R >= 240 && pixel.R <= 244 && pixel.R == pixel.G && pixel.G == pixel.B,
                $"{label} opacity:.05 should composite black once over white: {pixel}");
        }
    }

    [Fact]
    public void OpacityIsAppliedToOverlappingChildrenAsOneGroup()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <div style="position:relative;width:30px;height:20px;opacity:.5">
                    <div style="position:absolute;left:0;width:20px;height:20px;background:black"></div>
                    <div style="position:absolute;left:10px;width:20px;height:20px;background:black"></div>
                </div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (40f, 30f), null)!;
        foreach (uint x in (uint[])[5, 15, 25])
        {
            byte channel = Pixel(pixmap, x, 10).R;
            Assert.InRange(channel, (byte)126, (byte)129);
        }
    }

    [Fact]
    public void NestedOpacityGroupsMultiplyAtCompositeBoundaries()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <div style="width:20px;height:20px;opacity:.5">
                    <div style="width:20px;height:20px;background:black;opacity:.5"></div>
                </div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (30f, 30f), null)!;
        PremultipliedColor pixel = Pixel(pixmap, 10, 10);
        Assert.True(
            pixel.R >= 190 && pixel.R <= 193 && pixel.R == pixel.G && pixel.G == pixel.B,
            $"nested .5 groups should produce .25 black over white: {pixel}");
    }

    [Fact]
    public void OpacityGroupContainsZOrderAndPreservesClipTransforms()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <div style="position:relative;width:20px;height:20px;
                            overflow:hidden;opacity:.5">
                    <div style="position:absolute;z-index:999;left:0;top:0;
                                transform:translate(10px,0);width:20px;height:20px;
                                background:red"></div>
                </div>
                <div style="position:absolute;left:30px;top:0;width:20px;height:20px;
                            opacity:.5">
                    <div style="position:absolute;z-index:999;width:20px;height:20px;
                                background:red"></div>
                </div>
                <div style="position:absolute;left:30px;top:0;width:20px;height:20px;
                            background:blue"></div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (60f, 30f), null)!;
        PremultipliedColor clippedOut = Pixel(pixmap, 5, 10);
        PremultipliedColor clippedIn = Pixel(pixmap, 15, 10);
        PremultipliedColor outside = Pixel(pixmap, 25, 10);
        Assert.Equal((255, 255, 255), (clippedOut.R, clippedOut.G, clippedOut.B));
        Assert.True(
            clippedIn.R > 240 && clippedIn.G >= 126 && clippedIn.G <= 129,
            $"translated child should be clipped then group-composited: {clippedIn}");
        Assert.Equal((255, 255, 255), (outside.R, outside.G, outside.B));
        PremultipliedColor covered = Pixel(pixmap, 40, 10);
        Assert.True(
            covered.B > 240 && covered.R < 20,
            $"high-z descendant must stay inside the earlier opacity group: {covered}");
    }

    [Fact]
    public void FixedAndStickyAutoZIndexContainDescendantStacking()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <div id="fixed" style="position:fixed;left:0;top:0;width:20px;height:20px;background:red">
                    <div style="position:absolute;z-index:999;inset:0;background:lime"></div>
                </div>
                <div style="position:absolute;z-index:1;left:0;top:0;width:20px;height:20px;background:blue"></div>
                <div style="position:absolute;left:0;top:30px">
                    <div id="sticky" style="position:sticky;top:0;width:20px;height:20px;background:red">
                        <div style="position:absolute;z-index:999;inset:0;background:lime"></div>
                    </div>
                </div>
                <div style="position:absolute;z-index:1;left:0;top:30px;width:20px;height:20px;background:blue"></div>
            </body></html>
            """);
        NodeId fixedNode = ById(tree, "fixed");
        NodeId sticky = ById(tree, "sticky");
        DomLayout laid = RenderDom.LayoutDom(tree, (40f, 60f));
        Assert.Equal(0, PaintDomPainter.StackingZIndex(tree, laid, fixedNode));
        Assert.Equal(0, PaintDomPainter.StackingZIndex(tree, laid, sticky));

        Pixmap pixmap = RenderPaint.PaintDom(tree, (40f, 60f), null)!;
        foreach ((string label, uint y) in ((string, uint)[])[("fixed", 10), ("sticky", 40)])
        {
            PremultipliedColor pixel = Pixel(pixmap, 10, y);
            Assert.True(
                pixel.B > 240 && pixel.R < 20 && pixel.G < 20,
                $"{label} high-z descendant escaped its auto-z stacking context: {pixel}");
        }
    }

    [Fact]
    public void StaticFlexAndGridItemZIndexPaintsEachSubtreeAtomically()
    {
        const string BlueImage =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='100'%20height='60'%3E%3Crect%20width='100'%20height='60'%20fill='blue'/%3E%3C/svg%3E";
        DomTree tree = Parse($$"""
            <html><head><style>
                 body { margin:0 }
                 .row {
                   position:relative; width:120px; height:70px;
                   display:flex; align-items:flex-start;
                 }
                 .grid {
                   width:120px; height:70px; display:grid;
                   grid-template-columns:100px;
                   grid-template-rows:60px;
                 }
                 .front {
                   box-sizing:border-box; width:100px; height:60px;
                   z-index:2; background:#ff0000; border:5px solid #00ff00;
                   color:#000000; font-size:24px; line-height:30px;
                 }
                 .cover {
                   position:absolute; z-index:auto; left:0; top:0;
                   width:100px; height:60px; background:#0000ff;
                 }
                 .cell { grid-area:1 / 1; }
                 img.cell { width:100px; height:60px; }
               </style></head><body>
                 <div class="row">
                   <div class="front">FLEX</div>
                   <div class="cover"></div>
                 </div>
                 <div class="grid">
                   <div class="front cell">GRID</div>
                   <img class="cell" alt="" src="{{BlueImage}}">
                 </div>
                 <div class="row" style="height:60px">
                   <div class="front" style="z-index:1">LOW</div>
                   <div class="cover" style="z-index:3"></div>
                 </div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (140f, 210f), null)!;

        bool IsGreen(uint x, uint y)
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            return pixel.G > 230 && pixel.R < 30 && pixel.B < 30;
        }

        bool IsRed(uint x, uint y)
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            return pixel.R > 230 && pixel.G < 30 && pixel.B < 30;
        }

        Assert.True(IsGreen(2, 30) && IsRed(80, 50));
        Assert.True(IsGreen(2, 100) && IsRed(80, 120));

        foreach ((string label, uint y0) in ((string, uint)[])[("flex", 0), ("grid", 70)])
        {
            int darkInk = 0;
            for (uint x = 8; x < 92; x++)
            {
                for (uint y = y0 + 8; y < y0 + 42; y++)
                {
                    PremultipliedColor pixel = Pixel(pixmap, x, y);
                    if (pixel.R < 40 && pixel.G < 40 && pixel.B < 40)
                    {
                        darkInk++;
                    }
                }
            }

            Assert.True(darkInk > 20, $"{label} item text must remain inside the raised subtree, found {darkInk}");
        }

        for (uint dy = 0; dy < 60; dy++)
        {
            for (uint x = 0; x < 100; x++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, 140 + dy);
                Assert.True(pixel.B > 230 && pixel.R < 30 && pixel.G < 30);
            }
        }
    }

    [Fact]
    public void WrappedInlineBackgroundAndSliceBordersPaintPerContinuation()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { width:70px; font:16px/24px monospace }
                #token {
                    color:transparent; background:#ff0000; padding:0 10px;
                    border-left:2px solid #0000ff;
                    border-right:2px solid #0000ff
                }
            </style>
            <p><span id="token">aaaa aaaa aaaa</span></p>
            """);
        NodeId token = ById(tree, "token");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 100f), null, resources)!;
        List<Rect> fragments = [.. prepared.Layout.InlineFragments[token]];
        Assert.Equal(3, fragments.Count);
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        (byte R, byte G, byte B) At(float x, float y) =>
            Rgb(pixmap, (uint)F32.Max(MathF.Floor(x), 0f), (uint)F32.Max(MathF.Floor(y), 0f));

        Rect first = fragments[0];
        Rect middle = fragments[1];
        Rect last = fragments[2];
        Assert.Equal((0, 0, 255), At(first.X + 0.5f, first.Y + 1f));
        Assert.Equal((255, 0, 0), At(middle.X + 0.5f, middle.Y + 1f));
        Assert.Equal((0, 0, 255), At(last.X + last.Width - 0.5f, last.Y + 1f));
        float gapY = (first.Y + first.Height + middle.Y) * 0.5f;
        Assert.Equal((255, 255, 255), At(5f, gapY));
    }

    [Fact]
    public void OverflowClippingKeepsPhysicalAxesIndependent()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <div style="position:relative;width:20px;height:20px;
                            overflow-x:clip">
                    <div style="position:absolute;left:10px;top:30px;
                                width:20px;height:10px;background:red"></div>
                </div>
                <div style="position:absolute;left:40px;top:0;width:20px;height:20px;
                            overflow-y:clip">
                    <div style="position:absolute;left:30px;top:10px;
                                width:10px;height:20px;background:blue"></div>
                </div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (90f, 50f), null)!;

        PremultipliedColor xInsideYOutside = Pixel(pixmap, 15, 35);
        Assert.True(xInsideYOutside.R > 240 && xInsideYOutside.B < 20, $"{xInsideYOutside}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 25, 35));

        PremultipliedColor yInsideXOutside = Pixel(pixmap, 75, 15);
        Assert.True(yInsideXOutside.B > 240 && yInsideXOutside.R < 20, $"{yInsideXOutside}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 75, 25));
    }

    [Fact]
    public void GeneratedBoxesUseTheHostsOverflowOnOnlyTheAuthoredAxis()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <div id="x"></div><div id="y"></div>
                <style>
                  #x { position:relative;width:20px;height:20px;overflow-x:clip }
                  #x::before { content:"";position:absolute;left:10px;top:30px;
                               width:20px;height:10px;background:red }
                  #y { position:absolute;left:40px;top:0;width:20px;height:20px;
                       overflow-y:clip }
                  #y::after { content:"";display:block;width:10px;height:30px;
                              margin-left:30px;background:blue }
                </style>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (90f, 50f), null)!;

        PremultipliedColor positionedYOverflow = Pixel(pixmap, 15, 35);
        Assert.True(positionedYOverflow.R > 240 && positionedYOverflow.B < 20, $"{positionedYOverflow}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 25, 35));

        PremultipliedColor inFlowXOverflow = Pixel(pixmap, 75, 10);
        Assert.True(inFlowXOverflow.B > 240 && inFlowXOverflow.R < 20, $"{inFlowXOverflow}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 75, 25));
    }

    [Fact]
    public void LaterElementPaintsOverEarlier()
    {
        DomTree tree = Parse(
            "<html><body>"
            + "<div style=\"background-color:red; width:100px; height:100px\">"
            + "<div style=\"background-color:blue; width:50px; height:50px\"></div>"
            + "</div>"
            + "</body></html>");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;
        PremultipliedColor p = Pixel(pixmap, 5, 5);
        Assert.True(p.B > 200, $"expected blue to paint over red, got {p}");
    }

    [Fact]
    public void NestedTranslateAccumulatesThroughSubtree()
    {
        DomTree tree = Parse(
            "<html><body style=\"margin:0\">"
            + "<div style=\"position:relative; width:200px; height:200px\">"
            + "<div style=\"position:absolute; top:0; left:0; width:20px; height:20px; "
            + "background:#ff0000; transform:translate(50px,60px)\">"
            + "<div style=\"width:10px; height:10px; background:#0000ff; "
            + "transform:translate(30px,0)\"></div>"
            + "</div></div></body></html>");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;
        PremultipliedColor blue = Pixel(pixmap, 85, 65);
        Assert.True(blue.B > 200 && blue.R < 60, $"expected blue child at accumulated offset (80,60), got {blue}");
        PremultipliedColor red = Pixel(pixmap, 55, 75);
        Assert.True(red.R > 200 && red.B < 60, $"expected red parent at its own translate (50,60), got {red}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 5, 5));
    }

    [Fact]
    public void RotateAndScalePaintCompleteMixedSubtrees()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
              <div style="position:absolute;left:20px;top:20px;width:60px;height:40px;
                          background:#ff0000;transform-origin:0 0;transform:scale(2)">
                <span style="color:#0000ff;font:12px sans-serif">MMMM</span>
                <img alt="" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='10'%20height='10'%3E%3Crect%20width='10'%20height='10'%20fill='%2300ff00'/%3E%3C/svg%3E"
                     style="position:absolute;left:40px;top:20px;width:10px;height:10px">
              </div>
              <div style="position:absolute;left:180px;top:20px;width:30px;height:20px;
                          background:#ff00ff;transform-origin:0 0;transform:rotate(90deg)">
                <span style="color:#0000ff;font:10px sans-serif">M</span>
                <img alt="" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='10'%20height='10'%3E%3Crect%20width='10'%20height='10'%20fill='%2300ffff'/%3E%3C/svg%3E"
                     style="position:absolute;left:20px;top:0;width:10px;height:10px">
              </div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (240f, 120f), null)!;

        PremultipliedColor scaledBox = Pixel(pixmap, 30, 70);
        Assert.True(scaledBox.R > 220 && scaledBox.G < 40);
        PremultipliedColor scaledImage = Pixel(pixmap, 110, 70);
        Assert.True(scaledImage.G > 220 && scaledImage.R < 40);
        bool scaledText = false;
        for (uint x = 20; x < 100 && !scaledText; x++)
        {
            for (uint y = 20; y < 60 && !scaledText; y++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                scaledText = pixel.R < 80 && pixel.G < 80 && pixel.B < 80;
            }
        }

        Assert.True(scaledText, "text must be rasterized inside the scaled atomic subtree");

        PremultipliedColor rotatedBox = Pixel(pixmap, 165, 25);
        Assert.True(rotatedBox.R > 220 && rotatedBox.B > 220);
        PremultipliedColor rotatedImage = Pixel(pixmap, 175, 45);
        Assert.True(rotatedImage.G > 220 && rotatedImage.B > 220);
    }

    [Fact]
    public void TransformFunctionOrderNestedWorldMatricesAndCssomAabbs()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
              <div id="translate-rotate" style="position:absolute;left:50px;top:50px;
                   width:20px;height:10px;transform-origin:0 0;
                   transform:translateX(100px) rotate(90deg)"></div>
              <div id="rotate-translate" style="position:absolute;left:50px;top:50px;
                   width:20px;height:10px;transform-origin:0 0;
                   transform:rotate(90deg) translateX(100px)"></div>
              <div id="parent" style="position:absolute;left:50px;top:200px;width:100px;
                   height:100px;transform-origin:0 0;transform:scale(2)">
                <div id="child" style="position:absolute;left:10px;top:20px;width:10px;
                     height:20px;transform-origin:0 0;transform:rotate(90deg)"></div>
                <div id="captured-fixed" style="position:fixed;left:0;top:0;width:10px;
                     height:10px"></div>
              </div>
              <div id="center-origin" style="position:absolute;left:100px;top:400px;
                   width:20px;height:10px;transform:rotate(90deg)"></div>
              <div id="cssom-transform" style="position:absolute;left:200px;top:400px;
                   width:20px;height:10px;transform:translateX(50%);translate:7px;
                   rotate:30deg;scale:2"></div>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (500f, 600f), null, resources)!;
        static void AssertRect(Rect actual, Rect expected)
        {
            Assert.True(MathF.Abs(actual.X - expected.X) < 0.02f, $"x: {actual}");
            Assert.True(MathF.Abs(actual.Y - expected.Y) < 0.02f, $"y: {actual}");
            Assert.True(MathF.Abs(actual.Width - expected.Width) < 0.02f, $"width: {actual}");
            Assert.True(MathF.Abs(actual.Height - expected.Height) < 0.02f, $"height: {actual}");
        }

        NodeId first = ById(tree, "translate-rotate");
        NodeId second = ById(tree, "rotate-translate");
        AssertRect(prepared.DocumentRect(first)!.Value, new Rect(140f, 50f, 10f, 20f));
        AssertRect(prepared.DocumentRect(second)!.Value, new Rect(40f, 150f, 10f, 20f));
        NodeId child = ById(tree, "child");
        Rect childRect = new(30f, 240f, 40f, 20f);
        AssertRect(prepared.DocumentRect(child)!.Value, childRect);
        AssertRect(prepared.ViewportClientRects(child, (0f, 0f))![0], childRect);
        NodeId fixedNode = ById(tree, "captured-fixed");
        AssertRect(prepared.DocumentRect(fixedNode)!.Value, new Rect(50f, 200f, 20f, 20f));
        NodeId centered = ById(tree, "center-origin");
        AssertRect(prepared.DocumentRect(centered)!.Value, new Rect(105f, 395f, 10f, 20f));
        NodeId cssom = ById(tree, "cssom-transform");
        Dictionary<string, string> computed = prepared.ComputedStyle(cssom)!;
        Assert.Equal("matrix(1, 0, 0, 1, 10, 0)", computed["transform"]);
        Assert.Equal("7px 0px", computed["translate"]);
        Assert.Equal("30deg", computed["rotate"]);
        Assert.Equal("2 2", computed["scale"]);
    }

    [Fact]
    public void TransformedSubtreeIsClippedByOutsideAncestor()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
              <div style="position:absolute;left:20px;top:20px;width:40px;height:40px;
                          overflow:hidden">
                <div style="position:absolute;left:30px;top:10px;width:20px;height:20px;
                            background:#00aa00;transform-origin:0 0;transform:scale(2)"></div>
              </div>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (100f, 80f), null)!;
        PremultipliedColor inside = Pixel(pixmap, 55, 40);
        Assert.True(inside.G > 120 && inside.R < 40);
        Assert.Equal((255, 255, 255), Rgb(pixmap, 65, 40));
    }

    [Fact]
    public void NoTransformKeepsIdentityGeometryAndBaselinePaint()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0"><div id="plain" style="position:absolute;
               left:10px;top:12px;width:20px;height:15px;background:#ff0000"></div>
               </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (80f, 60f), null, resources)!;
        NodeId plain = ById(tree, "plain");
        Assert.Empty(prepared.Layout.Transforms);
        Assert.Equal(new Rect(10f, 12f, 20f, 15f), prepared.DocumentRect(plain));
        Pixmap pixmap = RenderPaint.PaintDom(tree, (80f, 60f), null)!;
        PremultipliedColor painted = Pixel(pixmap, 15, 15);
        Assert.True(painted.R > 220 && painted.G < 40);
        Assert.Equal((255, 255, 255), Rgb(pixmap, 5, 5));
    }

    [Fact]
    public void TransformedImageIsClippedInsideOverflowBorder()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <div style="width:100px;height:60px;overflow:hidden;
                           border:4px solid red">
                 <div style="display:flex;transform:translate(-50px,0)">
                   <img alt="" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='100'%20height='60'%3E%3Crect%20width='100'%20height='60'%20fill='blue'/%3E%3C/svg%3E"
                        style="width:100px;height:60px;object-fit:cover;flex-shrink:0">
                   <img alt="" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='100'%20height='60'%3E%3Crect%20width='100'%20height='60'%20fill='blue'/%3E%3C/svg%3E"
                        style="width:100px;height:60px;object-fit:cover;flex-shrink:0">
                 </div>
               </div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (140f, 90f), null)!;

        foreach ((uint x, uint y) in ((uint, uint)[])[(0, 30), (3, 30), (104, 30), (107, 30), (50, 0), (50, 67)])
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            Assert.True(
                pixel.R > 220 && pixel.G < 40 && pixel.B < 40,
                $"translated image must not overwrite border pixel ({x},{y}): {pixel}");
        }

        PremultipliedColor content = Pixel(pixmap, 50, 30);
        Assert.True(content.B > 220 && content.R < 40, $"{content}");
    }

    [Fact]
    public void VideoPosterSuppliesIntrinsicSizeAndPaintsAsReplacedContent()
    {
        const string Poster =
            "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='300'%20height='100'%3E%3Crect%20width='100'%20height='100'%20fill='%23ff0000'/%3E%3Crect%20x='100'%20width='100'%20height='100'%20fill='%2300ff00'/%3E%3Crect%20x='200'%20width='100'%20height='100'%20fill='%230000ff'/%3E%3C/svg%3E";
        DomTree tree = Parse($$"""
            <html><body style="margin:0;background:white">
                <video id="poster" style="position:absolute;left:0;top:0" poster="{{Poster}}"></video>
                <video id="positioned" style="position:absolute;left:0;top:110px;width:100px;height:100px;object-fit:cover;object-position:right center;border-radius:20px;opacity:.5" poster="{{Poster}}"></video>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (360f, 220f), null, resources)!;
        NodeId video = ById(tree, "poster");
        Rect rect = prepared.DocumentRect(video)!.Value;
        Assert.Equal((300f, 100f), (rect.Width, rect.Height));

        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        PremultipliedColor red = Pixel(pixmap, 50, 50);
        PremultipliedColor green = Pixel(pixmap, 150, 50);
        PremultipliedColor blue = Pixel(pixmap, 250, 50);
        Assert.True(red.R > 240 && red.G < 20 && red.B < 20);
        Assert.True(green.G > 240 && green.R < 20 && green.B < 20);
        Assert.True(blue.B > 240 && blue.R < 20 && blue.G < 20);

        PremultipliedColor roundedCorner = Pixel(pixmap, 0, 110);
        Assert.True(
            roundedCorner.R > 245 && roundedCorner.G > 245 && roundedCorner.B > 245,
            $"poster must be clipped by the video radius: {roundedCorner}");
        PremultipliedColor positioned = Pixel(pixmap, 50, 160);
        Assert.True(
            positioned.R > 100 && positioned.R < 160
            && positioned.G > 100 && positioned.G < 160
            && positioned.B > 240,
            $"right object-position must select the blue stripe and opacity must composite it: {positioned}");
    }

    [Fact]
    public void ProjectedImageTransformEntersOverflowClip()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <div style="position:relative;width:120px;height:100px;overflow:hidden">
                 <div style="position:absolute;left:-60px;top:50px;transform-origin:0 0;
                             transform:rotateX(60deg) rotateZ(-45deg);scale:200%">
                   <img alt="" src="data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%20width='40'%20height='40'%3E%3Crect%20width='40'%20height='40'%20fill='red'/%3E%3C/svg%3E"
                        style="display:block;width:40px;height:40px">
                 </div>
               </div>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (160f, 120f), null)!;

        bool entered = false;
        for (uint y = 0; y < 100 && !entered; y++)
        {
            for (uint x = 0; x < 120 && !entered; x++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                entered = pixel.R > 220 && pixel.G < 40 && pixel.B < 40;
            }
        }

        Assert.True(entered, "projected image should enter the overflow clip");
        for (uint y = 0; y < 120; y++)
        {
            for (uint x = 120; x < 160; x++)
            {
                PremultipliedColor pixel = Pixel(pixmap, x, y);
                Assert.True(pixel.R > 240 && pixel.G > 240 && pixel.B > 240);
            }
        }
    }

    [Fact]
    public void TranslateOffscreenBoxIsNotPainted()
    {
        DomTree tree = Parse(
            "<html><body>"
            + "<div style=\"position:absolute; top:0; left:0; width:50px; height:50px; "
            + "background:#ff0000; transform:translate(-10000px,0)\"></div>"
            + "</body></html>");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;
        bool anyRed = false;
        for (uint y = 0; y < 200 && !anyRed; y++)
        {
            for (uint x = 0; x < 200 && !anyRed; x++)
            {
                PremultipliedColor p = Pixel(pixmap, x, y);
                anyRed = p.R > 200 && p.G < 60 && p.B < 60;
            }
        }

        Assert.False(anyRed, "translate(-10000px,0) box should be off-screen and unpainted");
    }

    [Fact]
    public void TranslatePercentCentersAbsoluteBox()
    {
        DomTree tree = Parse(
            "<html><body style=\"margin:0\">"
            + "<div style=\"position:relative; width:200px; height:200px\">"
            + "<div style=\"position:absolute; top:50%; left:50%; width:40px; height:40px; "
            + "background:#ff0000; transform:translate(-50%,-50%)\"></div>"
            + "</div></body></html>");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;
        PremultipliedColor center = Pixel(pixmap, 100, 100);
        Assert.True(center.R > 200 && center.B < 60, $"expected centered red box, got {center}");
        Assert.Equal((255, 255, 255), Rgb(pixmap, 70, 70));
    }

    [Fact]
    public void PaintsTextColor()
    {
        DomTree tree = Parse(
            "<html><body><div style=\"color: #00ff00; width: 100px; height: 100px\">Hello</div></body></html>");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;
        bool foundGreen = false;
        for (uint y = 0; y < 200 && !foundGreen; y++)
        {
            for (uint x = 0; x < 200 && !foundGreen; x++)
            {
                PremultipliedColor p = Pixel(pixmap, x, y);
                foundGreen = p.G > 200 && p.R < 50 && p.B < 50;
            }
        }

        Assert.True(foundGreen, "expected green text to be painted");
    }

    [Fact]
    public void WordMeasurementHonorsGenericFontFamily()
    {
        float sans = PaintText.MeasureText("iiiiiiii", 16f, false, "sans-serif");
        float mono = PaintText.MeasureText("iiiiiiii", 16f, false, "monospace");
        Assert.True(mono > sans * 1.5f, $"monospace advances must be used for code text: sans={sans}, mono={mono}");

        const string Sample = "Build fast, responsive sites with Bootstrap";
        float system = PaintText.MeasureText(Sample, 64f, false, "system-ui, sans-serif");
        float arial = PaintText.MeasureText(Sample, 64f, false, "Arial, sans-serif");
        Assert.True(system > arial * 1.08f, $"system={system}, arial={arial}");
    }

    [Fact]
    public void PaintsVendorGradientOnInlineTextSpan()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
               h1 { color:#17233c; font-size:50px; margin:0 }
               html:not(.dark) .accent[data-v-x] {
                 -webkit-text-fill-color:transparent;
                 background:-webkit-linear-gradient(315deg,#42d392 25%,#647eff);
                 -webkit-background-clip:text;
                 background-clip:text
               }
               </style></head><body style="margin:0">
               <h1>The <span class="accent" data-v-x>Progressive</span></h1>
               </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (500f, 100f), null)!;
        bool green = false;
        bool blue = false;
        bool normal = false;
        foreach (PremultipliedColor pixel in pixmap.Pixels)
        {
            int r = pixel.R;
            int g = pixel.G;
            int b = pixel.B;
            green |= g > Math.Min(r + 20, 255) && g > Math.Min(b + 10, 255);
            blue |= b > Math.Min(r + 20, 255) && b > Math.Min(g + 5, 255);
            normal |= b > Math.Min(g + 10, 255) && r < 80 && g < 100;
        }

        Assert.True(normal, "surrounding heading text should retain its normal color");
        Assert.True(green && blue, "inline accent should contain both gradient colors");
    }


    [Fact]
    public void SerializesInlineSvgSubtree()
    {
        // A sprite-style svg: a <use> that references a <symbol> in the same document must
        // survive serialization so the rasterizer can resolve it.
        DomTree tree = Parse(
            """<html><body><svg viewBox="0 0 10 10"><use href="#a"/><symbol id="a"><path d="M0 0h10v10z"/></symbol></svg></body></html>""");
        NodeId svg = Selector(tree, "svg");
        string output = PaintSvg.SerializeSvg(tree, svg);
        Assert.StartsWith("<svg", output, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 10 10\"", output, StringComparison.Ordinal);
        Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", output, StringComparison.Ordinal);
        Assert.Contains("<use", output, StringComparison.Ordinal);
        Assert.Contains("href=\"#a\"", output, StringComparison.Ordinal);
        Assert.Contains("<symbol", output, StringComparison.Ordinal);
        Assert.Contains("id=\"a\"", output, StringComparison.Ordinal);
        Assert.Contains("<path", output, StringComparison.Ordinal);
        Assert.Contains("</path>", output, StringComparison.Ordinal);
        Assert.EndsWith("</svg>", output.TrimEnd(), StringComparison.Ordinal);

        // The serialized string parses as a standalone SVG document.
        Assert.NotNull(SvgDocument.Parse(System.Text.Encoding.UTF8.GetBytes(output)));
    }

    [Fact]
    public void InlineSvgKeepsAuthorCssAndEmbeddedTextFonts()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                .art > text {
                    fill:#00cc55;
                    font-family:sans-serif;
                    font-size:24px;
                    font-weight:400
                }
                </style></head><body style="margin:0">
                <svg class="art" width="120" height="50"
                     viewBox="0 0 120 50" fill="none">
                    <text x="4" y="32">SVG text</text>
                </svg>
                </body></html>
            """);
        NodeId svg = Selector(tree, "svg");
        DomLayout layout = RenderDom.LayoutDom(tree, (160f, 80f));
        string markup = PaintSvg.SerializeSvgStyled(
            tree,
            svg,
            layout.Styles,
            layout.CustomProperties,
            null);
        Assert.Contains("fill:#00cc55", markup, StringComparison.Ordinal);
        Assert.Contains("font-size:24px!important", markup, StringComparison.Ordinal);

        Pixmap pixmap = RenderPaint.PaintDom(tree, (160f, 80f), null)!;
        bool paintedGreen = pixmap.Pixels.Any(pixel => pixel.G > 150 && pixel.R < 80 && pixel.B < 120);
        Assert.True(paintedGreen, "author-styled SVG text should rasterize with embedded fonts");
    }

    /// <summary>
    /// MECHANISM SUBSTITUTION. Rust captures usvg's per-glyph fallback warnings to prove the
    /// symbol resolved rather than rendering .notdef tofu. There is no usvg logger here, so the
    /// same property is asserted directly against the SVG font database: the run rasterizes,
    /// and a face in the database actually maps U+2605 (a .notdef fallback would map to 0).
    /// </summary>
    [Fact]
    public void SvgTextSymbolGlyphsResolveInTheFontDatabase()
    {
        const string Svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="60" height="50">
                <text x="8" y="34" font-size="24" fill="#00cc55">&#x2605;</text>
                <text x="8" y="4" font-size="24" fill="#00cc55">&#x378;</text>
            </svg>
            """;
        using Pixmap pixmap = SvgRenderer.RenderWithFontDatabase(
            System.Text.Encoding.UTF8.GetBytes(Svg),
            60,
            50,
            SvgFontDatabase.Shared)!;
        Assert.True(
            pixmap.Pixels.Any(pixel => pixel.G > 150 && pixel.R < 80 && pixel.B < 120),
            "the star text run should rasterize");

        SkiaSharp.SKTypeface star = SvgFontDatabase.Shared.Resolve("sans-serif", false, false);
        SkiaSharp.SKTypeface broad = SvgFontDatabase.Shared.Resolve("system-ui", false, false);
        Assert.True(
            star.GetGlyph(0x2605) != 0 || broad.GetGlyph(0x2605) != 0,
            "the SVG font database must cover U+2605 instead of dropping to .notdef");

        // Positive control: an unassigned codepoint maps to .notdef in every bundled face.
        Assert.Equal(0, star.GetGlyph(0x378));
        Assert.Equal(0, broad.GetGlyph(0x378));
    }

    [Fact]
    public void InjectsXmlnsOnlyWhenAbsent()
    {
        DomTree tree = Parse(
            """<html><body><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 4 4"><rect width="4" height="4"/></svg></body></html>""");
        NodeId svg = Selector(tree, "svg");
        string output = PaintSvg.SerializeSvg(tree, svg);
        int count = 0;
        int index = 0;
        while ((index = output.IndexOf("xmlns=", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += "xmlns=".Length;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void PaintsInlineSvg()
    {
        DomTree tree = Parse(
            """<html><body><svg width="40" height="40" viewBox="0 0 40 40"><rect x="0" y="0" width="40" height="40" fill="#ff0000"/></svg></body></html>""");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;
        bool foundRed = false;
        for (uint y = 0; y < 80 && !foundRed; y++)
        {
            for (uint x = 0; x < 80 && !foundRed; x++)
            {
                PremultipliedColor p = Pixel(pixmap, x, y);
                foundRed = p.R > 200 && p.G < 60 && p.B < 60;
            }
        }

        Assert.True(foundRed, "expected inline svg <rect> to paint red");
    }

    [Fact]
    public void SvgMissingRootHeightUsesTheFinalCssViewport()
    {
        byte[] svg = """
            <svg xmlns="http://www.w3.org/2000/svg"
                width="32" viewBox="0 0 223 236">
                <rect width="223" height="236" fill="#ed174c"/>
            </svg>
            """u8.ToArray();
        using Pixmap pixmap = SvgRenderer.Render(svg, 32, 34)!;
        uint minY = 34;
        uint maxY = 0;
        for (uint y = 0; y < 34; y++)
        {
            for (uint x = 0; x < 32; x++)
            {
                if (Pixel(pixmap, x, y).A > 0)
                {
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        Assert.True(maxY - minY >= 30, $"viewBox artwork should fill the resolved 32x34 viewport, got rows {minY}..{maxY}");
    }

    [Fact]
    public void PaintsInlineSvgCurrentColorFromComputedStyle()
    {
        DomTree tree = Parse(
            """<html><body><svg style="color:#0784aa" width="40" height="40" viewBox="0 0 40 40"><circle cx="20" cy="20" r="18" fill="currentColor"/></svg></body></html>""");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (80f, 80f), null)!;
        bool found = pixmap.Pixels.Any(pixel => pixel.B > 120 && pixel.G > 80 && pixel.R < 40);
        Assert.True(found, "computed color should resolve currentColor in inline svg");
    }

    [Fact]
    public void InlineSvgResolvesCustomPropertyPresentationAttributesLikeChromium()
    {
        Assert.True(PaintSvg.SvgCssPresentationAttribute("transform-origin"));
        Assert.False(PaintSvg.SvgCssPresentationAttribute("transform"));
        Assert.False(PaintSvg.SvgCssPresentationAttribute("d"));
        Assert.False(PaintSvg.SvgCssPresentationAttribute("viewBox"));
        Assert.False(PaintSvg.SvgCssPresentationAttribute("id"));
        DomTree tree = Parse(
            """
            <html><head><style>#winner{fill:#a030c0}</style></head>
            <body style="margin:0">
              <svg id="icon" width="70" height="10" viewBox="0 0 70 10"
                   fill="#176b75"
                   style="display:block;--direct:#dc1e28;--nested:#c02020;
                          --paint:currentColor;--x:10;--w:10;--stroke-width:2;
                          --cycle-a:var(--cycle-b);--cycle-b:var(--cycle-a);
                          color:#e09020">
                <defs>
                  <linearGradient id="gradient">
                    <stop id="stop" offset="0" stop-color="var(--nested)"/>
                  </linearGradient>
                  <path id="probe" d="var(--path)" fill="none"
                        stroke="var(--direct)"
                        stroke-width="var(--stroke-width,4)"/>
                </defs>
                <rect id="direct" x="0" width="10" height="10"
                      fill="var(--direct,rgb(255,255,255))"/>
                <rect id="nested" x="var(--x)" width="var(--w,5)" height="10"
                      style="--nested:#20c060"
                      fill="var(--missing,var(--nested,#ffffff))"/>
                <rect id="cycle" x="20" width="10" height="10"
                      fill="var(--cycle-a,#3040e0)"/>
                <rect id="current" x="30" width="10" height="10"
                      fill="var(--paint,#000000)"/>
                <rect id="winner" x="40" width="10" height="10"
                      fill="var(--direct,#ffffff)"/>
                <rect id="invalid" x="50" width="10" height="10"
                      fill="var(--missing)"/>
                <rect id="invalid-fallback" x="60" width="10" height="10"
                      fill="var(--missing,definitely-not-a-paint)"/>
              </svg>
            </body></html>
            """);
        NodeId svg = Selector(tree, "#icon");
        DomLayout laid = RenderDom.LayoutDom(tree, (70f, 10f));
        string markup = PaintSvg.SerializeSvgStyled(tree, svg, laid.Styles, laid.CustomProperties, null);

        Assert.Contains("fill=\"#dc1e28\"", markup, StringComparison.Ordinal);
        Assert.Contains("stop-color=\"#c02020\"", markup, StringComparison.Ordinal);
        Assert.Contains("stroke=\"#dc1e28\"", markup, StringComparison.Ordinal);
        Assert.Contains("stroke-width=\"2\"", markup, StringComparison.Ordinal);
        Assert.Contains("x=\"10\"", markup, StringComparison.Ordinal);
        Assert.Contains("width=\"10\"", markup, StringComparison.Ordinal);
        Assert.Contains("d=\"var(--path)\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("fill=\"var(", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke=\"var(", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("stop-color=\"var(", markup, StringComparison.Ordinal);
        string invalid = markup.Split("<rect id=\"invalid\"")[1].Split('>')[0];
        Assert.DoesNotContain("fill=", invalid, StringComparison.Ordinal);
        string invalidFallback = markup.Split("<rect id=\"invalid-fallback\"")[1].Split('>')[0];
        Assert.DoesNotContain("fill=", invalidFallback, StringComparison.Ordinal);
        Assert.Contains("fill:#a030c0", markup, StringComparison.Ordinal);

        Pixmap pixmap = RenderPaint.PaintDom(tree, (70f, 10f), null)!;
        (byte R, byte G, byte B) Sample(uint x) => Rgb(pixmap, x, 5);
        Assert.Equal((0xdc, 0x1e, 0x28), Sample(5));
        Assert.Equal((0x20, 0xc0, 0x60), Sample(15));
        Assert.Equal((0x30, 0x40, 0xe0), Sample(25));
        Assert.Equal((0xe0, 0x90, 0x20), Sample(35));
        Assert.Equal((0xa0, 0x30, 0xc0), Sample(45));
        Assert.Equal((0x17, 0x6b, 0x75), Sample(55));
        Assert.Equal((0x17, 0x6b, 0x75), Sample(65));
    }

    [Fact]
    public void ComputedStyleExposesTextBreakLonghandsAndAlias()
    {
        DomTree tree = Parse(
            """
            <style>#copy{overflow-wrap:anywhere;word-break:keep-all}</style>
               <p id="copy">copy</p>
            """);
        NodeId copy = ById(tree, "copy");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (320f, 200f), null, resources)!;
        Dictionary<string, string> computed = prepared.ComputedStyle(copy)!;

        Assert.Equal("anywhere", computed["overflow-wrap"]);
        Assert.Equal("anywhere", computed["word-wrap"]);
        Assert.Equal("keep-all", computed["word-break"]);
    }

    /// <summary>
    /// A text <c>&lt;input&gt;</c>'s value has no DOM text node, so it needs painting from the
    /// attribute the way the placeholder does. <c>type=password</c> masks with bullets.
    /// </summary>
    [Fact]
    public void InputValuesPaintLikePlaceholders()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                 body{margin:0;background:#fff}
                 input{display:block;width:180px;height:30px;border:0;padding:0;font-size:16px}
               </style></head><body>
                 <input id="filled" value="ABC">
                 <input id="empty">
                 <input id="secret" type="password" value="ABC">
               </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (200f, 120f), null, resources)!;
        Pixmap pixmap = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        int Ink(uint top)
        {
            int count = 0;
            for (uint y = top; y < top + 30; y++)
            {
                for (uint x = 0; x < 180; x++)
                {
                    PremultipliedColor pixel = Pixel(pixmap, x, y);
                    if (pixel.R < 200 || pixel.G < 200 || pixel.B < 200)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        Assert.True(Ink(0) > 10, "a value from markup must paint");
        Assert.Equal(0, Ink(30));
        Assert.True(Ink(60) > 10, "a password value must paint as bullets");
    }

    [Fact]
    public void ComputedStyleExposesFloatAndClear()
    {
        DomTree tree = Parse(
            """
            <style>#left{float:left} #right{float:right;clear:both}</style>
               <div id="left"></div><div id="right"></div><div id="plain"></div>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (320f, 200f), null, resources)!;
        Dictionary<string, string> Computed(string id) => prepared.ComputedStyle(ById(tree, id))!;

        Assert.Equal("left", Computed("left")["float"]);
        Assert.Equal("none", Computed("left")["clear"]);
        Assert.Equal("right", Computed("right")["float"]);
        Assert.Equal("both", Computed("right")["clear"]);
        Assert.Equal("none", Computed("plain")["float"]);
        Assert.Equal("none", Computed("plain")["clear"]);
    }

    [Fact]
    public void PaintsInlineSvgWithFrameworkColonAttribute()
    {
        DomTree tree = Parse(
            """<html><body><svg q:id="f" width="40" height="40" viewBox="0 0 40 40"><rect width="40" height="40" fill="#18b6f6"/></svg></body></html>""");
        Pixmap output = RenderPaint.PaintDom(tree, (80f, 80f), null)!;
        bool foundBlue = false;
        for (uint y = 0; y < 80 && !foundBlue; y++)
        {
            for (uint x = 0; x < 80 && !foundBlue; x++)
            {
                PremultipliedColor pixel = Pixel(output, x, y);
                foundBlue = pixel.B > 200 && pixel.G > 120 && pixel.R < 80;
            }
        }

        Assert.True(foundBlue, "framework hydration attributes must not invalidate inline SVG XML");
    }

    [Fact]
    public void PaintsInlineSvgUseReference()
    {
        DomTree tree = Parse(
            """<html><body><svg width="40" height="40" viewBox="0 0 40 40"><defs><rect id="a" width="40" height="40" fill="#0000ff"/></defs><use href="#a"/></svg></body></html>""");
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 200f), null)!;
        bool foundBlue = false;
        for (uint y = 0; y < 80 && !foundBlue; y++)
        {
            for (uint x = 0; x < 80 && !foundBlue; x++)
            {
                PremultipliedColor p = Pixel(pixmap, x, y);
                foundBlue = p.B > 200 && p.R < 60 && p.G < 60;
            }
        }

        Assert.True(foundBlue, "expected <use> to instantiate the referenced <rect>");
    }

    [Fact]
    public void ExtractsSymbolByIdFromSprite()
    {
        const string Sprite =
            """<svg xmlns="http://www.w3.org/2000/svg"><defs><symbol id="a" viewBox="0 0 10 10"><path d="M0 0h10v10z"/></symbol><symbol id="b"><rect width="4" height="4"/></symbol></defs></svg>""";
        string output = PaintSvg.ExtractSvgElementById(Sprite, "a")!;
        Assert.StartsWith("<symbol", output, StringComparison.Ordinal);
        Assert.Contains("id=\"a\"", output, StringComparison.Ordinal);
        Assert.Contains("<path", output, StringComparison.Ordinal);
        Assert.Contains("h10v10z", output, StringComparison.Ordinal);
        Assert.EndsWith("</symbol>", output.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"b\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<rect", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractHandlesSelfClosingNestingAndAbsent()
    {
        const string S1 = """<svg><rect id="x" width="4" height="4"/></svg>""";
        Assert.Equal("""<rect id="x" width="4" height="4"/>""", PaintSvg.ExtractSvgElementById(S1, "x"));
        const string S2 = """<svg><g id="grp"><g><path/></g></g></svg>""";
        Assert.Equal("""<g id="grp"><g><path/></g></g>""", PaintSvg.ExtractSvgElementById(S2, "grp"));
        const string S3 = """<svg><symbol data-id="a"><path/></symbol></svg>""";
        Assert.Null(PaintSvg.ExtractSvgElementById(S3, "a"));
        Assert.Null(PaintSvg.ExtractSvgElementById(S2, "nope"));
    }

    [Fact]
    public void SameDocumentUseLeftUnchangedByInject()
    {
        DomTree tree = Parse(
            """<html><body><svg viewBox="0 0 10 10"><use href="#a"/><symbol id="a"><path d="M0 0h10v10z"/></symbol></svg></body></html>""");
        NodeId svg = Selector(tree, "svg");
        string markup = PaintSvg.SerializeSvg(tree, svg);
        string before = markup;
        RenderResourceCache cache = new();
        Dictionary<string, string?> spriteCache = new(StringComparer.Ordinal);
        markup = PaintSvg.InjectExternalSprites(tree, svg, null, null, null, markup, cache, spriteCache);
        Assert.Equal(before, markup);
    }

    [Fact]
    public void InjectsDocumentLevelSymbolIntoTargetSvg()
    {
        DomTree tree = Parse(
            """
            <html><body>
                <svg style="display:none"><symbol id="arrow" viewBox="0 0 10 10"><path d="M0 0h10v10z"/></symbol></svg>
                <svg id="icon" viewBox="0 0 10 10"><use href="#arrow"/></svg>
            </body></html>
            """);
        NodeId svg = Selector(tree, "#icon");
        string markup = PaintSvg.SerializeSvg(tree, svg);
        RenderResourceCache cache = new();
        Dictionary<string, string?> spriteCache = new(StringComparer.Ordinal);
        markup = PaintSvg.InjectExternalSprites(tree, svg, null, null, null, markup, cache, spriteCache);
        Assert.Contains("<defs><symbol id=\"arrow\"", markup, StringComparison.Ordinal);
        Assert.Contains("<use href=\"#arrow\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedDocumentSymbolCarriesResolvedPresentationStyle()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                body { --icon-fill: #20c997; }
                #sprite rect { stroke: var(--icon-stroke, #114433); }
            </style></head><body>
                <svg id="sprite" style="display:none"><symbol id="badge" viewBox="0 0 10 10"><rect width="10" height="10" fill="var(--icon-fill, #ff0000)"/></symbol></svg>
                <svg id="icon" width="10" height="10" viewBox="0 0 10 10"><use href="#badge"/></svg>
            </body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (100f, 100f));
        NodeId svg = Selector(tree, "#icon");
        string markup = PaintSvg.SerializeSvgStyled(tree, svg, laid.Styles, laid.CustomProperties, null);
        RenderResourceCache cache = new();
        Dictionary<string, string?> spriteCache = new(StringComparer.Ordinal);
        markup = PaintSvg.InjectExternalSprites(
            tree,
            svg,
            laid.Styles,
            laid.CustomProperties,
            null,
            markup,
            cache,
            spriteCache);
        Assert.DoesNotContain("var(", markup, StringComparison.Ordinal);
        Assert.Contains("#20c997", markup, StringComparison.Ordinal);
        Assert.Contains("#114433", markup, StringComparison.Ordinal);
        using Pixmap pixmap = SvgRenderer.Render(System.Text.Encoding.UTF8.GetBytes(markup), 10, 10)!;
        PremultipliedColor center = Pixel(pixmap, 5, 5);
        Assert.True(
            center.G > 150 && center.R < 80 && center.B > 90,
            $"resolved injected-symbol fill must paint: {center}");
    }

    [Fact]
    public void InjectedExternalSymbolResolvesHostCustomPropertiesAndFallbacks()
    {
        DomTree tree = Parse(
            """<html><body><svg id="icon" style="--icon-fill:#e83e8c" width="10" height="10" viewBox="0 0 10 10"><use href="icons.svg#badge"/></svg></body></html>""");
        DomLayout laid = RenderDom.LayoutDom(tree, (100f, 100f));
        NodeId svg = Selector(tree, "#icon");
        string markup = PaintSvg.SerializeSvgStyled(tree, svg, laid.Styles, laid.CustomProperties, null);
        RenderResourceCache cache = new();
        Dictionary<string, string?> spriteCache = new(StringComparer.Ordinal)
        {
            ["icons.svg#badge"] =
                """<symbol id="badge" viewBox="0 0 10 10"><rect width="10" height="10" fill="var(--icon-fill, #ff0000)" stroke="var(--missing, #224466)"/><path d="var(--xml-only)"/></symbol>""",
        };
        markup = PaintSvg.InjectExternalSprites(
            tree,
            svg,
            laid.Styles,
            laid.CustomProperties,
            null,
            markup,
            cache,
            spriteCache);
        Assert.Contains("href=\"#badge\"", markup, StringComparison.Ordinal);
        Assert.Contains("fill=\"#e83e8c\"", markup, StringComparison.Ordinal);
        Assert.Contains("stroke=\"#224466\"", markup, StringComparison.Ordinal);
        Assert.Contains("d=\"var(--xml-only)\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedDocumentSymbolInheritsTargetCurrentColor()
    {
        DomTree tree = Parse(
            """
            <html><body>
                <svg style="display:none"><symbol id="arrow" viewBox="0 0 10 10"><rect width="10" height="10" fill="currentColor"/></symbol></svg>
                <svg id="icon" viewBox="0 0 10 10"><use href="#arrow"/></svg>
            </body></html>
            """);
        NodeId svg = Selector(tree, "#icon");
        string markup = PaintSvg.SerializeSvg(tree, svg);
        RenderResourceCache cache = new();
        Dictionary<string, string?> spriteCache = new(StringComparer.Ordinal);
        markup = PaintSvg.InjectExternalSprites(tree, svg, null, null, null, markup, cache, spriteCache);
        markup = PaintSvg.InjectSvgCurrentColor(markup, new RgbaColor(220, 20, 60, 255));
        using Pixmap pixmap = SvgRenderer.Render(System.Text.Encoding.UTF8.GetBytes(markup), 20, 20)!;
        Assert.True(
            pixmap.Pixels.Any(pixel => pixel.R > 180 && pixel.G < 60 && pixel.B < 100),
            $"injected currentColor symbol should inherit target SVG color: {markup}");
    }

    [Fact]
    public void SvgLightDarkPresentationIsResolvedBeforeUsvg()
    {
        DomTree tree = Parse(
            """
            <style>
               #dark { color-scheme:dark }
               #dark rect {
                 fill:light-dark(#c3c7cb,#51565d);
                 stroke:light-dark(#ffffff,#000000);
               }
               </style>
               <div id="dark">
                 <svg id="icon" width="10" height="10" viewBox="0 0 10 10">
                   <rect width="10" height="10"/>
                 </svg>
               </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (100f, 100f));
        NodeId svg = Selector(tree, "#icon");
        string markup = PaintSvg.SerializeSvgStyled(tree, svg, laid.Styles, laid.CustomProperties, null);
        Assert.DoesNotContain("light-dark(", markup.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("fill:#51565dff!important", markup, StringComparison.Ordinal);
        Assert.Contains("stroke:#000000ff!important", markup, StringComparison.Ordinal);
        using Pixmap pixmap = SvgRenderer.Render(System.Text.Encoding.UTF8.GetBytes(markup), 10, 10)!;
        PremultipliedColor center = Pixel(pixmap, 5, 5);
        Assert.True(
            center.R > 60 && center.R < 110
            && center.G > 60 && center.G < 120
            && center.B > 70 && center.B < 130,
            $"resolved dark fill must survive rasterization: {center}");
    }

    [Fact]
    public void FontFaceParserSelectsAsciiSubsetAndPreservesFunctionalSrc()
    {
        const string Css = """
            @font-face {
                font-family: "Example";
                src: local("Example"), url("./example-cyrillic.woff2") format("woff2");
                unicode-range: U+0400-04FF;
            }
            @font-face {
                font-family: "Example";
                font-style: italic;
                font-weight: 350 650;
                src: url(data:font/woff2;base64,d09GMg==) format("woff2"),
                     url("./example-latin.woff") format("woff");
                unicode-range: U+??, U+2000-206F;
            }
            """;
        List<string> faces = PaintFonts.FontFaceBlocks(Css);
        Assert.Equal(2, faces.Count);
        Assert.False(PaintFonts.FontFaceCoversAscii(faces[0]));
        Assert.True(PaintFonts.FontFaceCoversAscii(faces[1]));
        Assert.Equal("Example", PaintFonts.FontFaceFamily(faces[1]));
        Assert.Equal<(ushort, ushort)?>(((ushort)350, (ushort)650), PaintFonts.FontFaceWeight(faces[1]));
        Assert.Equal(true, PaintFonts.FontFaceItalic(faces[1]));
        Assert.Equal(
            new List<string> { "data:font/woff2;base64,d09GMg==", "./example-latin.woff" },
            PaintFonts.FontFaceUrls(faces[1]));
    }

    [Fact]
    public void FontFaceWithoutUnicodeRangeIsGeneralPurpose()
    {
        const string Css = "@font-face{font-family:Example;src:url(example.otf)}";
        string face = PaintFonts.FontFaceBlocks(Css)[0];
        Assert.True(PaintFonts.FontFaceCoversAscii(face));
        Assert.Equal(new List<string> { "example.otf" }, PaintFonts.FontFaceUrls(face));
    }

    [Fact]
    public void FontFaceUsesTheFirstDecodableSource()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                @font-face {
                    font-family: Fixture;
                    src: url("fixture.eot") format("embedded-opentype"),
                         url("fixture.woff2") format("woff2"),
                         url("fixture.ttf") format("truetype");
                }
            </style></head><body></body></html>
            """);
        List<string> loads = [];
        byte[] serifBytes = FontAssets.Load("liberation-serif");
        RenderResourceCache resources = RenderResourceCache.WithLoader(url =>
        {
            loads.Add(url);
            return url switch
            {
                // A server can return malformed bytes for a preferred source; CSS Fonts
                // requires trying the next candidate in the list.
                "https://example.test/fixture.woff2" => "not a font"u8.ToArray(),
                "https://example.test/fixture.ttf" => serifBytes,
                _ => null,
            };
        });

        List<WebFont> fonts = PaintFonts.CollectWebFonts(tree, "https://example.test/page.html", resources, []);

        Assert.Single(fonts);
        Assert.Equal("Fixture", fonts[0].Family);
        Assert.Equal(serifBytes, fonts[0].Data);
        Assert.Equal(
            new List<string> { "https://example.test/fixture.woff2", "https://example.test/fixture.ttf" },
            loads);
    }

    [Fact]
    public void FontFaceUsesTheLastDuplicateSrcDescriptor()
    {
        const string Css = """
            @font-face {
                        font-family: FontAwesome;
                        src: url("legacy.eot");
                        src: url("legacy.eot?#iefix") format("embedded-opentype"),
                             url("icons.woff2") format("woff2"),
                             url("icons.ttf") format("truetype");
                    }
            """;
        string face = PaintFonts.FontFaceBlocks(Css)[0];

        Assert.Equal(
            new List<string> { "legacy.eot?#iefix", "icons.woff2", "icons.ttf" },
            PaintFonts.FontFaceUrls(face));
    }

    [Fact]
    public void DynamicFontFaceUsesSharedFetchDecodeAndLayoutPath()
    {
        DomTree tree = Parse(
            """
            <html><body><span id="sample" style="display:inline-block;width:max-content;
                font:40px DynamicFixture;white-space:nowrap">WWWWiiii</span></body></html>
            """);
        NodeId sample = Selector(tree, "#sample");
        float fallback = RenderPaint.PrepareDom(
                tree,
                (400f, 100f),
                "https://example.test/page/index.html",
                new RenderResourceCache())!
            .DocumentRect(sample)!.Value.Width;
        List<string> loads = [];
        byte[] serifBytes = FontAssets.Load("liberation-serif");
        RenderResourceCache resources = RenderResourceCache.WithLoader(url =>
        {
            loads.Add(url);
            return url == "https://example.test/fonts/fixture.ttf" ? serifBytes : null;
        });
        PreparedRender prepared = RenderPaint.PrepareDomWithDynamicFonts(
            tree,
            (400f, 100f),
            "https://example.test/page/index.html",
            resources,
            [
                new DynamicFontFace
                {
                    Family = "DynamicFixture",
                    Source = "url('../../fonts/fixture.ttf') format('truetype')",
                    Style = "normal",
                    Weight = "400",
                    UnicodeRange = "U+20-7E",
                },
            ])!;
        float dynamicWidth = prepared.DocumentRect(sample)!.Value.Width;
        Assert.NotEqual(fallback, dynamicWidth);
        Assert.Equal(new List<string> { "https://example.test/fonts/fixture.ttf" }, loads);
    }


    [Fact]
    public void ResolvedNestedScrollKeepsOwnerAndClipStationaryWhilePixelsMove()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="outer" style="box-sizing:border-box;width:120px;height:100px;
                     border:4px solid red;overflow:hidden;position:relative;background:red">
                  <div id="inner" style="width:220px;height:200px;overflow:hidden;
                       position:relative;background:blue">
                    <div id="target" style="position:absolute;left:300px;top:280px;
                         width:30px;height:20px;background:lime"></div>
                  </div>
                </div>
            </body></html>
            """);
        NodeId outer = ById(tree, "outer");
        NodeId inner = ById(tree, "inner");
        NodeId target = ById(tree, "target");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (360f, 240f), null, resources)!;
        ResolvedScrollState topState = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        Rect topOuter = prepared.ViewportRectWithScroll(outer, topState)!.Value;
        Rect topTarget = prepared.ViewportRectWithScroll(target, topState)!.Value;
        using Pixmap top = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, topState)!;

        Dictionary<NodeId, (float, float)> offsets = new()
        {
            [outer] = (9999f, 9999f),
            [inner] = (9999f, 9999f),
        };
        ResolvedScrollState scrolledState = prepared.ResolveScrollState(tree, (0f, 0f), offsets);
        ElementScrollMetrics outerMetrics = prepared.ElementScrollMetrics(outer, scrolledState)!.Value;
        ElementScrollMetrics innerMetrics = prepared.ElementScrollMetrics(inner, scrolledState)!.Value;
        Assert.Equal((112f, 92f), outerMetrics.ClientSize);
        Assert.Equal((220f, 200f), outerMetrics.ContentSize);
        Assert.Equal((108f, 108f), outerMetrics.Offset);
        Assert.Equal((330f, 300f), innerMetrics.ContentSize);
        Assert.Equal((110f, 100f), innerMetrics.Offset);

        Rect scrolledOuter = prepared.ViewportRectWithScroll(outer, scrolledState)!.Value;
        Rect scrolledTarget = prepared.ViewportRectWithScroll(target, scrolledState)!.Value;
        Assert.Equal(topOuter, scrolledOuter);
        Assert.Equal(topTarget.X - 218f, scrolledTarget.X);
        Assert.Equal(topTarget.Y - 208f, scrolledTarget.Y);

        using Pixmap scrolled = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, scrolledState)!;
        using Pixmap repeated = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, scrolledState)!;
        Assert.NotEqual(top.Data(), scrolled.Data());
        Assert.Equal(scrolled.Data(), repeated.Data());
        Assert.Equal(Pixel(top, 2, 2), Pixel(scrolled, 2, 2));
        Assert.Equal(Pixel(top, 125, 50), Pixel(scrolled, 125, 50));
        Assert.NotEqual(Pixel(top, 95, 85), Pixel(scrolled, 95, 85));
    }

    [Fact]
    public void NestedScrollStickyGeometryPixelsAndPercentageBasisShareOneState()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="plain" style="position:relative;width:100px;height:80px;
                     overflow:hidden;background:red">
                    <div style="height:40px"></div>
                    <div id="plain-sticky" style="position:sticky;top:5px;
                         width:30px;height:20px;background:lime">
                        <div id="fixed-child" style="position:fixed;right:0;top:0;
                             width:5px;height:5px;background:black"></div>
                    </div>
                    <div id="plain-tail" style="height:160px;background:blue"></div>
                </div>
                <div id="padded" style="position:absolute;left:120px;top:0;
                     width:100px;height:80px;padding-top:20px;overflow:hidden;background:red">
                    <div style="height:70px"></div>
                    <div id="percent-sticky" style="position:sticky;top:50%;
                         width:30px;height:20px;background:lime"></div>
                    <div style="height:160px;background:blue"></div>
                </div>
                <div id="fixed-scroller" style="position:fixed;left:240px;top:0;
                     width:100px;height:80px;overflow:hidden;background:red">
                    <div style="height:40px"></div>
                    <div id="fixed-sticky" style="position:sticky;top:5px;
                         width:30px;height:20px;background:lime"></div>
                    <div style="height:160px;background:blue"></div>
                </div>
                <div id="calc-scroller" style="position:absolute;left:360px;top:0;
                     width:100px;height:80px;padding-top:20px;overflow:hidden;background:red">
                    <div style="height:70px"></div>
                    <div id="calc-sticky" style="position:sticky;top:calc(50% + 1px);
                         width:30px;height:20px;background:lime"></div>
                    <div style="height:160px;background:blue"></div>
                </div>
            </body></html>
            """);
        NodeId plain = ById(tree, "plain");
        NodeId plainSticky = ById(tree, "plain-sticky");
        NodeId plainTail = ById(tree, "plain-tail");
        NodeId padded = ById(tree, "padded");
        NodeId percentSticky = ById(tree, "percent-sticky");
        NodeId fixedChild = ById(tree, "fixed-child");
        NodeId fixedScroller = ById(tree, "fixed-scroller");
        NodeId fixedSticky = ById(tree, "fixed-sticky");
        NodeId calcScroller = ById(tree, "calc-scroller");
        NodeId calcSticky = ById(tree, "calc-sticky");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (480f, 120f), null, resources)!;

        ResolvedScrollState top = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        Assert.Equal(40f, prepared.ViewportRectWithScroll(plainSticky, top)!.Value.Y);
        Dictionary<NodeId, (float, float)> offsets = new()
        {
            [plain] = (0f, 60f),
            [padded] = (0f, 80f),
            [fixedScroller] = (0f, 60f),
            [calcScroller] = (0f, 80f),
        };
        ResolvedScrollState scrolled = prepared.ResolveScrollState(tree, (0f, 0f), offsets);
        Assert.Equal((0f, 60f), prepared.ElementScrollMetrics(plain, scrolled)!.Value.Offset);
        // scroll-container chrome must remain stationary
        Assert.Equal(0f, prepared.ViewportRectWithScroll(plain, scrolled)!.Value.Y);
        Assert.Equal(5f, prepared.ViewportRectWithScroll(plainSticky, scrolled)!.Value.Y);
        // ordinary content must retain the element scroll movement
        Assert.Equal(0f, prepared.ViewportRectWithScroll(plainTail, scrolled)!.Value.Y);
        // 50% sticky inset must use the 80px content box after 20px padding
        Assert.Equal(60f, prepared.ViewportRectWithScroll(percentSticky, scrolled)!.Value.Y);
        // an element scroller inside a fixed subtree must still drive sticky positioning
        Assert.Equal(5f, prepared.ViewportRectWithScroll(fixedSticky, scrolled)!.Value.Y);
        // a viewport-fixed descendant must not inherit its sticky ancestor's movement
        Assert.Equal(0f, prepared.ViewportRectWithScroll(fixedChild, scrolled)!.Value.Y);
        // calc() percentage inset must use the nested content-box basis
        Assert.Equal(61f, prepared.ViewportRectWithScroll(calcSticky, scrolled)!.Value.Y);

        using Pixmap pixels = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, scrolled)!;
        PremultipliedColor plainPixel = Pixel(pixels, 10, 10);
        PremultipliedColor percentPixel = Pixel(pixels, 130, 65);
        PremultipliedColor fixedPixel = Pixel(pixels, 250, 10);
        PremultipliedColor calcPixel = Pixel(pixels, 370, 65);
        Assert.True(plainPixel.Green > 240 && plainPixel.Red < 20);
        Assert.True(percentPixel.Green > 240 && percentPixel.Red < 20);
        Assert.True(fixedPixel.Green > 240 && fixedPixel.Red < 20);
        Assert.True(calcPixel.Green > 240 && calcPixel.Red < 20);
    }

    [Fact]
    public void ViewportFixedDescendantEscapesNestedScrollportClipInAllPaintPaths()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="scroller" style="position:relative;width:100px;height:80px;
                     overflow:hidden;background:red">
                    <div style="height:40px"></div>
                    <div style="position:sticky;top:5px;width:30px;height:20px;background:blue">
                        <div id="fixed" style="position:fixed;z-index:10;left:150px;top:10px;
                             width:20px;height:20px;background:lime"></div>
                    </div>
                    <div style="height:160px"></div>
                </div>
                <div id="captured-host" style="position:absolute;left:0;top:50px;
                     width:100px;height:30px;overflow:hidden;transform:translateX(0);background:red">
                    <div id="captured-fixed" style="position:fixed;left:150px;top:0;
                         width:20px;height:20px;background:blue"></div>
                </div>
                <div id="fixed-clip" style="position:fixed;left:100px;top:75px;
                     width:40px;height:25px;overflow:hidden;background:red">
                    <div style="position:absolute;left:50px;top:0;
                         width:20px;height:20px;background:blue"></div>
                </div>
            </body></html>
            """);
        NodeId scroller = ById(tree, "scroller");
        NodeId fixedNode = ById(tree, "fixed");
        NodeId capturedFixed = ById(tree, "captured-fixed");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (220f, 100f), null, resources)!;
        Assert.Contains(fixedNode, prepared.ViewportFixedNodes());
        Assert.DoesNotContain(capturedFixed, prepared.ViewportFixedNodes());

        using Pixmap tuple = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        PremultipliedColor tuplePixel = Pixel(tuple, 155, 15);
        Assert.True(
            tuplePixel.Green > 240 && tuplePixel.Red < 20 && tuplePixel.Blue < 20,
            $"the default paint path retained the ancestor scroller clip: {tuplePixel}");
        PremultipliedColor capturedPixel = Pixel(tuple, 155, 55);
        Assert.True(
            capturedPixel.Red > 240 && capturedPixel.Green > 240 && capturedPixel.Blue > 240,
            $"a containing-block-captured fixed descendant must remain clipped: {capturedPixel}");
        PremultipliedColor internalClipPixel = Pixel(tuple, 155, 80);
        Assert.True(
            internalClipPixel.Red > 240 && internalClipPixel.Green > 240 && internalClipPixel.Blue > 240,
            $"clips created inside a viewport-fixed subtree must still apply: {internalClipPixel}");

        Dictionary<NodeId, (float, float)> offsets = new() { [scroller] = (0f, 60f) };
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), offsets);
        // a viewport-fixed boundary must clear document-space overflow clips
        Assert.Null(scroll.InheritedClipFor(fixedNode));
        using Pixmap resolved = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, scroll)!;
        PremultipliedColor resolvedPixel = Pixel(resolved, 155, 15);
        Assert.True(
            resolvedPixel.Green > 240 && resolvedPixel.Red < 20 && resolvedPixel.Blue < 20,
            $"the resolved paint path retained the ancestor scroller clip: {resolvedPixel}");
        PremultipliedColor resolvedCaptured = Pixel(resolved, 155, 55);
        Assert.True(resolvedCaptured.Red > 240 && resolvedCaptured.Green > 240 && resolvedCaptured.Blue > 240);
        PremultipliedColor resolvedInternal = Pixel(resolved, 155, 80);
        Assert.True(resolvedInternal.Red > 240 && resolvedInternal.Green > 240 && resolvedInternal.Blue > 240);
    }

    [Fact]
    public void VirtualViewportReResolvesFunctionalAndViewportStickyInsets()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;height:600px">
                <div style="height:150px"></div>
                <div id="calc" style="position:sticky;top:calc(50% + 1px);
                     width:20px;height:20px"></div>
                <div id="vh" style="position:sticky;top:10vh;width:20px;height:20px"></div>
            </body></html>
            """);
        NodeId calc = ById(tree, "calc");
        NodeId vh = ById(tree, "vh");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (200f, 200f), null, resources)!;
        Dictionary<NodeId, (float, float)> none = new();
        ResolvedScrollState live = prepared.ResolveScrollStateForViewport(tree, (0f, 200f), none, (200f, 200f));
        ResolvedScrollState page = prepared.ResolveScrollStateForViewport(tree, (0f, 200f), none, (200f, 100f));

        Assert.Equal(101f, prepared.ViewportRectWithScroll(calc, live)!.Value.Y);
        Assert.Equal(51f, prepared.ViewportRectWithScroll(calc, page)!.Value.Y);
        Assert.Equal(20f, prepared.ViewportRectWithScroll(vh, live)!.Value.Y);
        Assert.Equal(10f, prepared.ViewportRectWithScroll(vh, page)!.Value.Y);
    }

    [Fact]
    public void HiddenScrollerCapturesStickyWhileOverflowClipDoesNot()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;height:400px">
                <div id="hidden" style="position:absolute;left:0;top:50px;
                     width:100px;height:80px;overflow:hidden">
                    <div id="hidden-sticky" style="position:sticky;top:0;
                         width:20px;height:20px;background:lime"></div>
                </div>
                <div id="clip" style="position:absolute;left:120px;top:50px;
                     width:100px;height:80px;overflow:clip">
                    <div id="clip-sticky" style="position:sticky;top:0;
                         width:20px;height:20px;background:lime"></div>
                </div>
            </body></html>
            """);
        NodeId hidden = ById(tree, "hidden");
        NodeId hiddenSticky = ById(tree, "hidden-sticky");
        NodeId clip = ById(tree, "clip");
        NodeId clipSticky = ById(tree, "clip-sticky");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (240f, 120f), null, resources)!;
        Assert.Contains(hidden, prepared.ScrollContainerNodes());
        Assert.DoesNotContain(clip, prepared.ScrollContainerNodes());

        ResolvedScrollState scrolled = prepared.ResolveScrollState(tree, (0f, 100f), new Dictionary<NodeId, (float, float)>());
        // overflow:hidden must capture sticky even without local overflow
        Assert.Equal(-50f, prepared.ViewportRectWithScroll(hiddenSticky, scrolled)!.Value.Y);
        // overflow:clip must leave sticky owned by the root viewport
        Assert.Equal(0f, prepared.ViewportRectWithScroll(clipSticky, scrolled)!.Value.Y);
    }

    [Fact]
    public void OuterStickyMotionCancelsFromInnerScrollportConstraints()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;height:400px">
                <div style="height:40px"></div>
                <div id="outer-sticky" style="position:sticky;top:0;
                     width:100px;height:80px;background:red">
                    <div id="inner-scroller" style="width:100px;height:60px;
                         overflow:hidden;background:blue">
                        <div style="height:30px"></div>
                        <div id="inner-sticky" style="position:sticky;top:5px;
                             width:30px;height:10px;background:lime"></div>
                        <div style="height:100px"></div>
                    </div>
                </div>
            </body></html>
            """);
        NodeId outerSticky = ById(tree, "outer-sticky");
        NodeId innerScroller = ById(tree, "inner-scroller");
        NodeId innerSticky = ById(tree, "inner-sticky");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 100f), null, resources)!;
        Dictionary<NodeId, (float, float)> offsets = new() { [innerScroller] = (0f, 40f) };
        ResolvedScrollState scrolled = prepared.ResolveScrollState(tree, (0f, 60f), offsets);

        Assert.Equal(0f, prepared.ViewportRectWithScroll(outerSticky, scrolled)!.Value.Y);
        Assert.Equal(0f, prepared.ViewportRectWithScroll(innerScroller, scrolled)!.Value.Y);
        // outer sticky movement must affect the inner port and its content equally
        Assert.Equal(5f, prepared.ViewportRectWithScroll(innerSticky, scrolled)!.Value.Y);

        using Pixmap pixels = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, scrolled)!;
        PremultipliedColor pixel = Pixel(pixels, 10, 7);
        Assert.True(pixel.Green > 240 && pixel.Red < 20 && pixel.Blue < 20);
    }

    [Fact]
    public void TupleCaptureAndGeometryResolveNestedStickyAtZeroElementScroll()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div id="scroller" style="width:100px;height:80px;overflow:hidden;background:red">
                    <div style="height:100px"></div>
                    <div id="sticky" style="position:sticky;bottom:0;
                         width:30px;height:20px;background:lime"></div>
                    <div style="height:20px"></div>
                </div>
                <div style="position:fixed;left:120px;top:0;width:100px;height:80px;
                     overflow:hidden;background:red">
                    <div style="height:100px"></div>
                    <div id="fixed-sticky" style="position:sticky;bottom:0;
                         width:30px;height:20px;background:lime"></div>
                    <div style="height:20px"></div>
                </div>
            </body></html>
            """);
        NodeId sticky = ById(tree, "sticky");
        NodeId fixedSticky = ById(tree, "fixed-sticky");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (240f, 100f), null, resources)!;
        ResolvedScrollState resolved = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        Assert.Equal(60f, prepared.ViewportRect(sticky, (0f, 0f))!.Value.Y);
        Assert.Equal(60f, prepared.ViewportRect(fixedSticky, (0f, 0f))!.Value.Y);
        Assert.Equal(60f, prepared.ViewportRectWithScroll(sticky, resolved)!.Value.Y);

        using Pixmap pixels = RenderPaint.PaintPrepared(tree, prepared, resources, (0f, 0f))!;
        PremultipliedColor pixel = Pixel(pixels, 10, 65);
        PremultipliedColor fixedPixel = Pixel(pixels, 130, 65);
        Assert.True(pixel.Green > 240 && pixel.Red < 20 && pixel.Blue < 20);
        Assert.True(
            fixedPixel.Green > 240 && fixedPixel.Red < 20 && fixedPixel.Blue < 20,
            $"tuple paint dropped nested sticky movement in a fixed subtree: {fixedPixel}");
    }

    [Fact]
    public void DocumentRegionCaptureReusesLayoutScrollAndResources()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;height:300px;background:white">
                <div style="height:80px;background:red"></div>
                <div id="sticky" style="position:sticky;z-index:1;top:0;margin-left:20px;
                     width:10px;height:10px;background:yellow"></div>
                <div style="height:210px;background:green"></div>
                <div id="offscreen" style="position:absolute;left:40px;top:150px;
                     width:10px;height:10px;background:magenta"></div>
                <img src="fixture.svg" style="position:absolute;left:60px;top:200px;
                     width:10px;height:10px">
                <div id="fixed" style="position:fixed;left:0;top:0;width:10px;
                     height:10px;background:blue"></div>
            </body></html>
            """);
        int loads = 0;
        RenderResourceCache resources = RenderResourceCache.WithLoader(url =>
        {
            Assert.Equal("https://example.test/fixture.svg", url);
            loads++;
            return System.Text.Encoding.UTF8.GetBytes(
                """
                <svg xmlns="http://www.w3.org/2000/svg" width="10" height="10">
                    <rect width="10" height="10" fill="#00ffff"/>
                </svg>
                """);
        });
        PreparedRender prepared = RenderPaint.PrepareDom(
            tree, (80f, 60f), "https://example.test/page", resources)!;
        Assert.Equal(1, loads);
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 100f), new Dictionary<NodeId, (float, float)>());
        NodeId fixedNode = ById(tree, "fixed");
        Rect beforeFixed = prepared.ViewportRectWithScroll(fixedNode, scroll)!.Value;

        using Pixmap offscreen = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(0f, 100f, 80f, 80f, 1f)).Pixmap!;
        Assert.Equal((80u, 80u), (offscreen.Width, offscreen.Height));
        PremultipliedColor fixedPixel = Pixel(offscreen, 5, 5);
        Assert.True(fixedPixel.Blue > 240 && fixedPixel.Red < 20);
        PremultipliedColor stickyPixel = Pixel(offscreen, 25, 5);
        Assert.True(stickyPixel.Red > 240 && stickyPixel.Green > 240);
        PremultipliedColor targetPixel = Pixel(offscreen, 45, 55);
        Assert.True(targetPixel.Red > 240 && targetPixel.Blue > 240);

        float fullHeight = prepared.ContentSize().Height;
        using Pixmap full = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 80f, fullHeight, 1f)).Pixmap!;
        Assert.Equal((uint)MathF.Ceiling(fullHeight), full.Height);
        PremultipliedColor fixedAtLiveViewport = Pixel(full, 5, 105);
        Assert.True(fixedAtLiveViewport.Blue > 240 && fixedAtLiveViewport.Red < 20);
        PremultipliedColor fixedNotDuplicated = Pixel(full, 5, 5);
        Assert.True(fixedNotDuplicated.Red > 240 && fixedNotDuplicated.Blue < 20);

        using Pixmap scaled = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(40f, 150f, 10f, 10f, 2f)).Pixmap!;
        Assert.Equal((20u, 20u), (scaled.Width, scaled.Height));
        PremultipliedColor scaledCenter = Pixel(scaled, 10, 10);
        Assert.True(scaledCenter.Red > 240 && scaledCenter.Blue > 240);

        using Pixmap protocolSized = RenderPaint.PaintPreparedRegionWithScroll(
            tree,
            prepared,
            resources,
            scroll,
            CaptureRegion.WithOutputSize(40f, 150f, 10f, 9f, 1.1f, 11, 10)).Pixmap!;
        Assert.Equal((11u, 10u), (protocolSized.Width, protocolSized.Height));

        Assert.Equal((0f, 100f), scroll.RootOffset());
        Assert.Equal(beforeFixed, prepared.ViewportRectWithScroll(fixedNode, scroll)!.Value);
        Assert.Equal(1, loads);
    }

    [Fact]
    public void PrintBackgroundPolicyMatchesChromiumEconomyAndRestoresScreenPaint()
    {
        Assert.Equal(
            new RgbaColor(171, 171, 171, 255),
            RenderPaint.PrintEconomyColor(new RgbaColor(255, 255, 255, 255)));
        Assert.Equal(
            new RgbaColor(105, 105, 105, 255),
            RenderPaint.PrintEconomyColor(new RgbaColor(105, 105, 105, 255)));
        Assert.Equal(
            new RgbaColor(25, 25, 25, 255),
            RenderPaint.PrintEconomyColor(new RgbaColor(110, 110, 110, 255)));
        Assert.Equal(
            new RgbaColor(255, 0, 0, 255),
            RenderPaint.PrintEconomyColor(new RgbaColor(255, 0, 0, 255)));
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;background:#123456">
                <div style="box-sizing:border-box;width:100px;height:100px;
                     background:#ff0000;border:10px solid #0000ff;
                     color:#ffffff;font:40px sans-serif">X</div>
                <div style="position:relative;width:120px;height:120px">
                    <img src="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='120' height='120'%3E%3Crect width='120' height='120' fill='orange'/%3E%3C/svg%3E"
                         style="position:absolute;inset:0;width:120px;height:120px">
                    <div style="position:absolute;left:20px;top:20px;width:80px;height:80px;
                         background-image:linear-gradient(#00ff00,#00ff00)"></div>
                </div>
            </body></html>
            """);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 220f), null, resources)!;
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        CaptureRegion region = CaptureRegion.New(0f, 0f, 120f, 220f, 1f);

        byte[] screenBefore = RenderPaint.ScreenshotPreparedRegionWithScrollAndBackgrounds(
            tree, prepared, resources, scroll, region, true).Png!;
        byte[] economyPng = RenderPaint.ScreenshotPreparedRegionWithScrollAndBackgrounds(
            tree, prepared, resources, scroll, region, false).Png!;
        byte[] screenAfter = RenderPaint.ScreenshotPreparedRegionWithScrollAndBackgrounds(
            tree, prepared, resources, scroll, region, true).Png!;
        Assert.Equal(screenBefore, screenAfter);

        using SkiaSharp.SKBitmap screen = DecodePng(screenBefore);
        using SkiaSharp.SKBitmap economy = DecodePng(economyPng);
        Assert.Equal((byte)255, PngRgb(screen, 80, 80).R);
        Assert.Equal((0, 0), (PngRgb(screen, 80, 80).G, PngRgb(screen, 80, 80).B));
        Assert.Equal((255, 255, 255), PngRgb(economy, 80, 80));
        Assert.Equal((0, 0, 255), PngRgb(economy, 5, 50));
        Assert.Equal((0, 255, 0), PngRgb(screen, 50, 150));
        Assert.Equal((255, 165, 0), PngRgb(economy, 5, 105));
        Assert.Equal((255, 255, 255), PngRgb(economy, 50, 150));
        bool shaded = false;
        for (int y = 10; y < 90 && !shaded; y++)
        {
            for (int x = 10; x < 90; x++)
            {
                (int R, int G, int B) sample = PngRgb(economy, x, y);
                if (sample.R >= 140 && sample.R <= 200
                    && Math.Abs(sample.R - sample.G) <= 2
                    && Math.Abs(sample.R - sample.B) <= 2)
                {
                    shaded = true;
                    break;
                }
            }
        }

        Assert.True(shaded, "print economy must shade the white glyph");
    }

    [Fact]
    public void DocumentRegionCaptureRejectsInvalidAndOversizedSurfacesBeforePaint()
    {
        DomTree tree = Parse("<div style='width:10px;height:10px;background:red'></div>");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (20f, 20f), null, resources)!;
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());

        foreach (CaptureRegion invalid in new[]
        {
            CaptureRegion.New(0f, 0f, 0f, 10f, 1f),
            CaptureRegion.New(float.NaN, 0f, 10f, 10f, 1f),
            CaptureRegion.New(0f, 0f, 10f, 10f, float.PositiveInfinity),
        })
        {
            Assert.Equal(
                CaptureError.InvalidRegion,
                RenderPaint.PaintPreparedRegionWithScroll(tree, prepared, resources, scroll, invalid).Error);
        }

        Assert.Equal(
            CaptureError.AllocationLimitExceeded,
            RenderPaint.PaintPreparedRegionWithScroll(
                tree,
                prepared,
                resources,
                scroll,
                CaptureRegion.New(0f, 0f, CaptureLimits.MaxCaptureDimension, 10f, 2f)).Error);
        Assert.Equal(
            CaptureError.AllocationLimitExceeded,
            RenderPaint.PaintPreparedRegionWithScroll(
                tree,
                prepared,
                resources,
                scroll,
                CaptureRegion.New(0f, 0f, 10f, 10f, CaptureLimits.MaxCaptureScale + 1f)).Error);
        Assert.Equal(
            CaptureError.AllocationLimitExceeded,
            // Each surface is below the legacy 64M-pixel per-surface bound, but their
            // simultaneous RGBA peak exceeds 256 MiB.
            RenderPaint.PaintPreparedRegionWithScroll(
                tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 6000f, 6000f, 1.2f)).Error);
        Assert.Equal(
            CaptureError.AllocationLimitExceeded,
            RenderPaint.PaintPreparedRegionWithScroll(
                tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 9000f, 9000f, 1f)).Error);
    }

    [Fact]
    public void SimpleRegionCaptureRasterizesGeometryAndTextNativelyAtTwoX()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="width:8px;height:8px;background:#f00"></div>
                <p style="margin:0;color:#000;font:12px sans-serif">Hi</p>
            </body></html>
            """);
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (40f, 30f), null, resources)!;
        Assert.True(
            PaintApi.NativeRasterScaleSupported(tree, prepared.Layout),
            "solid boxes and shaped text should use direct device-scale paint");
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        using Pixmap oneX = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 40f, 30f, 1f)).Pixmap!;
        using Pixmap twoX = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 40f, 30f, 2f)).Pixmap!;
        Assert.Equal((80u, 60u), (twoX.Width, twoX.Height));

        bool Red(uint x, uint y)
        {
            PremultipliedColor pixel = Pixel(twoX, x, y);
            return pixel.Red > 245 && pixel.Green < 10 && pixel.Blue < 10;
        }

        Assert.True(Red(15, 7), "the 8 CSS-px box must cover 16 device pixels");
        Assert.False(Red(16, 7), "the adjacent CSS pixel must remain outside the box");

        using Pixmap postScaled = PaintResample.Lanczos3(oneX, twoX.Width, twoX.Height)!;
        byte[] native = twoX.Data();
        byte[] post = postScaled.Data();
        int differing = 0;
        for (int index = 0; index < native.Length; index++)
        {
            if (native[index] != post[index])
            {
                differing++;
            }
        }

        Assert.True(
            differing > 100,
            $"2x glyph outlines and box edges must be rerasterized, not resize-equivalent ({differing} differing channels)");
        int darkTextPixels = 0;
        for (uint y = 16; y < 60; y++)
        {
            for (uint x = 0; x < 50; x++)
            {
                PremultipliedColor pixel = Pixel(twoX, x, y);
                if (pixel.Red < 100 && pixel.Green < 100 && pixel.Blue < 100)
                {
                    darkTextPixels++;
                }
            }
        }

        Assert.True(darkTextPixels > 10, "native 2x text must remain painted");
    }

    [Fact]
    public void DirectVectorGradientsRasterizeNativelyAtTwoX()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="width:20px;height:16px;background:linear-gradient(90deg,#f00 0%,#00f 100%)"></div>
                <div style="width:20px;height:16px;background:radial-gradient(circle at 50% 50%,#fff 0%,#000 100%)"></div>
            </body></html>
            """);
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (30f, 40f), null, resources)!;
        Assert.True(
            PaintApi.NativeRasterScaleSupported(tree, prepared.Layout),
            "direct non-repeating linear/radial gradients should use device-scale paint");
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        using Pixmap oneX = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 30f, 40f, 1f)).Pixmap!;
        using Pixmap twoX = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 30f, 40f, 2f)).Pixmap!;
        Assert.Equal((30u, 40u), (oneX.Width, oneX.Height));
        Assert.Equal((60u, 80u), (twoX.Width, twoX.Height));

        PremultipliedColor linearLeft = Pixel(twoX, 1, 8);
        PremultipliedColor linearRight = Pixel(twoX, 38, 8);
        Assert.True(
            linearLeft.Red > 220 && linearLeft.Blue < 40,
            $"linear gradient start must remain red at 2x: {linearLeft}");
        Assert.True(
            linearRight.Blue > 220 && linearRight.Red < 40,
            $"linear gradient end must remain blue at 2x: {linearRight}");
        PremultipliedColor radialCenter = Pixel(twoX, 20, 48);
        PremultipliedColor radialEdge = Pixel(twoX, 1, 48);
        Assert.True(radialCenter.Red > 230, $"radial center must remain light at 2x: {radialCenter}");
        Assert.True(radialEdge.Red < 100, $"radial edge must remain dark at 2x: {radialEdge}");

        using Pixmap postScaled = PaintResample.Lanczos3(oneX, twoX.Width, twoX.Height)!;
        byte[] native = twoX.Data();
        byte[] post = postScaled.Data();
        int differing = 0;
        for (int index = 0; index < native.Length; index++)
        {
            if (native[index] != post[index])
            {
                differing++;
            }
        }

        Assert.True(
            differing > 200,
            $"2x vector gradient samples must be rerasterized, not resize-equivalent ({differing} differing channels)");
    }

    [Fact]
    public void ExplicitRadialEllipseMatchesChromiumAxisSamples()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="width:200px;height:100px;
                    background:radial-gradient(50% 25% at 50% 50%,#fff 0%,#000 100%)">
                </div>
            </body></html>
            """);
        using Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 100f), null)!;
        // Chromium 144 at DPR 1 yields R values 249, 126, 2, 127, and 5 at these pixel centers.
        // Allow a small raster-backend interpolation tolerance while requiring both authored
        // radii to control geometry.
        foreach (((uint X, uint Y) point, int chromium) in new[]
        {
            (((uint)100, (uint)50), 249),
            (((uint)150, (uint)50), 126),
            (((uint)199, (uint)50), 2),
            (((uint)100, (uint)62), 127),
            (((uint)100, (uint)74), 5),
        })
        {
            PremultipliedColor pixel = Pixel(pixmap, point.X, point.Y);
            int actual = pixel.Red;
            Assert.True(
                Math.Abs(actual - chromium) <= 5,
                $"ellipse sample ({point.X},{point.Y}) was {actual}, Chromium was {chromium}: {pixel}");
        }
    }

    [Fact]
    public void RadialExtentKeywordsKeepCircleAndEllipseGeometryDistinct()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;display:flex">
                <div style="width:200px;height:100px;
                    background:radial-gradient(circle farthest-side at 25% 50%,#fff,#000)">
                </div>
                <div style="width:200px;height:100px;
                    background:radial-gradient(ellipse farthest-side at 25% 50%,#fff,#000)">
                </div>
            </body></html>
            """);
        using Pixmap pixmap = RenderPaint.PaintDom(tree, (400f, 100f), null)!;
        byte circleBottom = Pixel(pixmap, 50, 99).Red;
        byte ellipseBottom = Pixel(pixmap, 250, 99).Red;
        Assert.True(
            circleBottom > 150,
            $"farthest-side circle radius is 150px, so its bottom remains light: {circleBottom}");
        Assert.True(
            ellipseBottom < 10,
            $"farthest-side ellipse vertical radius is 50px: {ellipseBottom}");
    }

    [Fact]
    public void EffectfulRegionCaptureKeepsTheProvenBoundedResampleFallback()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="width:20px;height:20px;background:conic-gradient(red,blue,red)"></div>
            </body></html>
            """);
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (30f, 30f), null, resources)!;
        Assert.False(
            PaintApi.NativeRasterScaleSupported(tree, prepared.Layout),
            "sampled conic gradients must retain the logical-pixel fallback");
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        using Pixmap oneX = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 30f, 30f, 1f)).Pixmap!;
        using Pixmap twoX = RenderPaint.PaintPreparedRegionWithScroll(
            tree, prepared, resources, scroll, CaptureRegion.New(0f, 0f, 30f, 30f, 2f)).Pixmap!;
        using Pixmap expected = PaintResample.Lanczos3(oneX, 60, 60)!;
        Assert.Equal(expected.Data(), twoX.Data());
    }

    [Fact]
    public void PaintSurfaceCullingUsesCssCoordinatesAndInkOverflow()
    {
        using Pixmap pixmap = Pixmap.New(200, 160)!;
        Assert.True(PaintDomPainter.RectIntersectsPaintSurface(
            new Rect { X = 90f, Y = 70f, Width = 20f, Height = 20f }, pixmap, 2f));
        Assert.False(PaintDomPainter.RectIntersectsPaintSurface(
            new Rect { X = 0f, Y = 81f, Width = 20f, Height = 20f }, pixmap, 2f));

        LayoutStyle style = new()
        {
            BoxShadow = new BoxShadow
            {
                OffsetX = -24f,
                OffsetY = 0f,
                Blur = 8f,
                Spread = 2f,
                Color = new RgbaColor(0, 0, 0, 255),
                Inset = false,
            },
        };
        style.Outline = style.Outline with
        {
            Style = BorderStyle.Solid,
            SpecifiedWidth = 4f,
            Offset = 3f,
        };
        Rect ink = PaintDomPainter.NonTextInkBounds(
            new Rect { X = 110f, Y = 10f, Width = 10f, Height = 10f }, style);
        Assert.Equal(76f, ink.X);
        Assert.Equal(0f, ink.Y);
        Assert.Equal(127f, ink.X + ink.Width);
    }

    [Fact]
    public void OffscreenOverflowClipSuppressesOffsetShadowAndOutlineInk()
    {
        static Pixmap PaintOverflow(string overflow)
        {
            string html = $$"""
                <html style="margin:0"><body style="margin:0">
                <div style="position:absolute;left:200px;top:0;width:100px;height:120px;
                            overflow:{{overflow}}">
                    <div style="position:absolute;left:50px;top:20px;width:20px;height:20px;
                                box-shadow:-210px 0 0 0 black"></div>
                    <div style="position:absolute;left:50px;top:75px;width:20px;height:20px;
                                outline:210px solid red"></div>
                </div>
            </body></html>
            """;
            DomTree tree = Parse(html);
            return RenderPaint.PaintDom(tree, (100f, 120f), null)!;
        }

        using Pixmap unclipped = PaintOverflow("visible");
        foreach ((uint x, uint y, string label) in new[] { (45u, 25u, "shadow"), (45u, 85u, "outline") })
        {
            PremultipliedColor pixel = Pixel(unclipped, x, y);
            Assert.True(
                (pixel.Red, pixel.Green, pixel.Blue) != ((byte)255, (byte)255, (byte)255),
                $"control must place {label} ink at the regression sample");
        }

        using Pixmap pixmap = PaintOverflow("hidden");
        foreach ((uint x, uint y, string label) in new[] { (45u, 25u, "shadow"), (45u, 85u, "outline") })
        {
            PremultipliedColor pixel = Pixel(pixmap, x, y);
            Assert.True(
                (pixel.Red, pixel.Green, pixel.Blue, pixel.Alpha) == ((byte)255, (byte)255, (byte)255, (byte)255),
                $"offscreen ancestor overflow clip must suppress {label}: {pixel}");
        }
    }

    [Fact]
    public void TransformedOutlineSurvivesTightAtomicSourceBounds()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="position:absolute;left:40px;top:40px;width:20px;height:20px;
                            outline:10px solid red;transform-origin:0 0;transform:scale(2)">
                </div>
            </body></html>
            """);
        using Pixmap pixmap = RenderPaint.PaintDom(tree, (120f, 120f), null)!;
        PremultipliedColor outerOutline = Pixel(pixmap, 25, 50);
        Assert.True(
            outerOutline.Red > 220 && outerOutline.Green < 40 && outerOutline.Blue < 40,
            $"outline ink outside the border-box source bounds must survive the transform: {outerOutline}");
    }

    [Fact]
    public void OffscreenImageIsRejectedBeforeResourceLookupAndDecode()
    {
        int calls = 0;
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ =>
        {
            calls++;
            return new byte[1024];
        });
        using Pixmap pixmap = Pixmap.New(100, 100)!;
        Rect rect = new() { X = 0f, Y = 10_000f, Width = 80f, Height = 80f };
        Assert.False(PaintImages.PaintImage(
            "https://example.test/offscreen.svg",
            null,
            rect,
            rect,
            ObjectFit.Fill,
            default,
            pixmap,
            resources,
            null,
            null,
            default,
            null));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ViewportCullUsesPostTranslateFixedAndStickyCoordinates()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0;height:1400px">
                <div style="position:absolute;left:10px;top:1000px;width:20px;height:20px;
                     transform:translateY(-990px);background:red"></div>
                <div style="position:fixed;left:40px;top:10px;width:20px;height:20px;
                     background:lime"></div>
                <div style="height:1000px"></div>
                <div style="position:sticky;left:70px;top:10px;width:20px;height:20px;
                     background:blue"></div>
            </body></html>
            """);
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 80f), null, resources)!;
        ResolvedScrollState top = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        using Pixmap topPixmap = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, top)!;
        PremultipliedColor translated = Pixel(topPixmap, 15, 15);
        Assert.True(translated.Red > 240 && translated.Green < 20);

        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 990f), new Dictionary<NodeId, (float, float)>());
        using Pixmap pixmap = RenderPaint.PaintPreparedWithScroll(tree, prepared, resources, scroll)!;
        PremultipliedColor fixedPixel = Pixel(pixmap, 45, 15);
        Assert.True(fixedPixel.Green > 240 && fixedPixel.Red < 20);
        PremultipliedColor sticky = Pixel(pixmap, 75, 15);
        Assert.True(sticky.Blue > 240 && sticky.Red < 20);
    }

    [Fact]
    public void AnimationRestyleMutationsDeduplicateWaapiAndFilterDisconnectedTargets()
    {
        DomTree tree = Parse("<main><div id=connected></div><div id=detached></div></main>");
        NodeId connected = ById(tree, "connected");
        NodeId detached = ById(tree, "detached");
        tree.RemoveChild(detached);
        static WaapiAnimation Animation(ulong id, NodeId node) => new()
        {
            Id = id,
            Node = node,
            Keyframes = [new WaapiKeyframe { Offset = 0f, Opacity = 0f, Transform = null }],
            Timing = AnimationTiming.Default with { DurationMs = 1_000f },
            Easing = null,
            LinearEasing = null,
            StartTimeMs = 0f,
            HoldTimeMs = null,
            PlayState = WaapiPlayState.Running,
        };
        AnimationTimelineState timeline = new();
        timeline.RegisterWaapi(Animation(1, connected));
        timeline.RegisterWaapi(Animation(2, connected));
        timeline.RegisterWaapi(Animation(3, detached));
        Assert.Equal(new HashSet<NodeId> { connected, detached }, timeline.WaapiNodes());

        List<RetainedStyleMutation> mutations = PaintApi.RetainedAnimationRestyleMutations(
            tree, new Dictionary<NodeId, LayoutStyle>(), timeline);
        Assert.Equal<RetainedStyleMutation>(
            [new RetainedStyleMutation.WaapiAnimation(connected)],
            mutations);
    }

    [Fact]
    public void ActiveWaapiTransformIsConservativelyGeometryAffecting()
    {
        DomTree tree = Parse("<div id=target></div>");
        NodeId target = ById(tree, "target");
        WaapiAnimation Animation(ulong id, float? opacity, string? transform) => new()
        {
            Id = id,
            Node = target,
            Keyframes = [new WaapiKeyframe { Offset = 0f, Opacity = opacity, Transform = transform }],
            Timing = AnimationTiming.Default with { DurationMs = 1_000f },
            Easing = null,
            LinearEasing = null,
            StartTimeMs = 0f,
            HoldTimeMs = null,
            PlayState = WaapiPlayState.Running,
        };
        AnimationSampleTime sample = new(100f);
        AnimationTimelineState timeline = new();
        timeline.RegisterWaapi(Animation(1, 0.5f, null));
        Assert.Equal(AnimationEffectImpact.Paint, timeline.ActiveWaapiEffectImpact(sample));
        timeline.RegisterWaapi(Animation(2, null, "future-transform(1)"));
        Assert.Equal(AnimationEffectImpact.Geometry, timeline.ActiveWaapiEffectImpact(sample));
    }

    [Fact]
    public void VisualWaapiRefreshMatchesForcedFullGeometryAndPixels()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;background:white"><body style="margin:0">
                <div id="moving" style="position:absolute;left:20px;top:20px;width:80px;height:60px;
                     overflow:hidden;border-radius:12px;background:red;transform:translateX(0px)">
                    <div style="width:130px;height:60px;background:lime"></div>
                    <div id="captured-fixed" style="position:fixed;left:4px;top:4px;
                         width:12px;height:12px;background:yellow"></div>
                </div>
                <div style="position:absolute;left:145px;top:20px;width:75px;height:75px;overflow:hidden">
                    <div id="affine" style="width:60px;height:60px;background:blue;
                         transform-origin:30px 30px;transform:rotate(0deg)"></div>
                </div>
                <div id="overflow" style="position:absolute;left:10px;top:100px;width:10px;height:10px;
                     background:black;transform:translateX(0px)"></div>
                <div id="scroller" style="position:absolute;left:150px;top:96px;width:70px;height:22px;
                     overflow:auto;background:purple">
                    <div id="scroll-target" style="width:150px;height:80px;background:orange"></div>
                </div>
                <div id="flow" style="width:24px;height:320px">
                    <div style="height:65px"></div>
                    <div id="sticky" style="position:sticky;top:3px;width:24px;height:12px;background:cyan">
                        <div id="sticky-child" style="width:8px;height:8px;background:black"></div>
                    </div>
                </div>
                <div id="viewport-fixed" style="position:fixed;right:0;top:0;
                     width:8px;height:8px;background:magenta"></div>
            </body></html>
            """);
        NodeId moving = ById(tree, "moving");
        NodeId affine = ById(tree, "affine");
        NodeId overflow = ById(tree, "overflow");
        NodeId capturedFixed = ById(tree, "captured-fixed");
        NodeId viewportFixed = ById(tree, "viewport-fixed");
        NodeId scroller = ById(tree, "scroller");
        NodeId scrollTarget = ById(tree, "scroll-target");
        NodeId sticky = ById(tree, "sticky");
        NodeId stickyChild = ById(tree, "sticky-child");
        AnimationTimelineState MakeTimeline()
        {
            AnimationTimelineState timeline = new();
            foreach ((ulong id, NodeId node, string from, string to) in new (ulong, NodeId, string, string)[]
            {
                (1, moving, "translateX(0px)", "translateX(36px)"),
                (2, affine, "rotate(0deg)", "rotate(28deg)"),
                (3, overflow, "translateX(0px)", "translateX(500px)"),
            })
            {
                timeline.RegisterWaapi(new WaapiAnimation
                {
                    Id = id,
                    Node = node,
                    Keyframes =
                    [
                        new WaapiKeyframe { Offset = 0f, Opacity = null, Transform = from },
                        new WaapiKeyframe { Offset = 1f, Opacity = null, Transform = to },
                    ],
                    Timing = AnimationTiming.Default with
                    {
                        DurationMs = 1_000f,
                        FillMode = AnimationFillMode.Both,
                    },
                    Easing = null,
                    LinearEasing = null,
                    StartTimeMs = 0f,
                    HoldTimeMs = null,
                    PlayState = WaapiPlayState.Running,
                });
            }

            timeline.RegisterWaapi(new WaapiAnimation
            {
                Id = 4,
                Node = moving,
                Keyframes =
                [
                    new WaapiKeyframe { Offset = 0f, Opacity = 0f, Transform = null },
                    new WaapiKeyframe { Offset = 1f, Opacity = 1f, Transform = null },
                ],
                Timing = AnimationTiming.Default with
                {
                    DurationMs = 1_000f,
                    FillMode = AnimationFillMode.Both,
                },
                Easing = null,
                LinearEasing = null,
                StartTimeMs = 0f,
                HoldTimeMs = null,
                PlayState = WaapiPlayState.Running,
            });
            return timeline;
        }

        (float Width, float Height) viewport = (240f, 120f);
        RenderResourceCache candidateResources = RenderResourceCache.WithLoader(_ => null);
        StylesheetCache candidateCache = new();
        AnimationTimelineState candidateTimeline = MakeTimeline();
        PreparedRender candidate = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
            tree,
            viewport,
            null,
            candidateResources,
            [],
            candidateCache,
            AnimationSample.Document(0f),
            candidateTimeline)!;
        Dictionary<NodeId, Affine2> initialTransforms = new(candidate.Layout.Transforms);
        Assert.True(candidate.TryAdvanceVisualWaapiSample(
            tree, AnimationSample.Document(500f), candidateTimeline));
        Assert.NotEqual(initialTransforms, candidate.Layout.Transforms);

        RenderResourceCache oracleResources = RenderResourceCache.WithLoader(_ => null);
        StylesheetCache oracleCache = new();
        AnimationTimelineState oracleTimeline = MakeTimeline();
        PreparedRender oracle = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
            tree,
            viewport,
            null,
            oracleResources,
            [],
            oracleCache,
            AnimationSample.Document(500f),
            oracleTimeline)!;

        Assert.Equal(oracle.Layout.Rects, candidate.Layout.Rects);
        Assert.Equal(oracle.Layout.InlineFragments, candidate.Layout.InlineFragments);
        Assert.Equal(oracle.Layout.Translates, candidate.Layout.Translates);
        Assert.Equal(oracle.Layout.Transforms, candidate.Layout.Transforms);
        Assert.Equal(oracle.Layout.ClipRects, candidate.Layout.ClipRects);
        Assert.Equal(oracle.ContentSize(), candidate.ContentSize());
        Assert.True(candidate.ContentSize().Width > viewport.Width);
        Assert.True(candidate.ContentSize().Height > viewport.Height);
        Assert.Equal(oracle.ViewportFixedNodes(), candidate.ViewportFixedNodes());
        Assert.Contains(viewportFixed, candidate.ViewportFixedNodes());
        Assert.DoesNotContain(capturedFixed, candidate.ViewportFixedNodes());
        Assert.Equal(
            oracle.ScrollContainerNodes().ToList(),
            candidate.ScrollContainerNodes().ToList());
        Assert.Contains(scroller, candidate.ScrollContainerNodes());
        foreach (NodeId node in new[] { moving, affine, overflow })
        {
            Assert.Equal(
                LayoutStyleDigest(oracle.Layout.Styles[node]),
                LayoutStyleDigest(candidate.Layout.Styles[node]));
        }

        Dictionary<NodeId, (float, float)> elementOffsets = new() { [scroller] = (9999f, 9999f) };
        ResolvedScrollState candidateScroll = candidate.ResolveScrollState(tree, (0f, 80f), elementOffsets);
        ResolvedScrollState oracleScroll = oracle.ResolveScrollState(tree, (0f, 80f), elementOffsets);
        Assert.Equal(oracleScroll.RootOffset(), candidateScroll.RootOffset());
        Assert.Equal(oracleScroll.ContainerOffsets, candidateScroll.ContainerOffsets);
        Assert.Equal(oracleScroll.NodeMovement, candidateScroll.NodeMovement);
        Assert.Equal(oracleScroll.InheritedClips, candidateScroll.InheritedClips);
        Assert.Equal(
            oracle.ElementScrollMetrics(scroller, oracleScroll),
            candidate.ElementScrollMetrics(scroller, candidateScroll));
        Assert.Equal(
            oracle.ViewportRectWithScroll(sticky, oracleScroll),
            candidate.ViewportRectWithScroll(sticky, candidateScroll));
        Assert.Equal(
            oracle.ViewportRectWithScroll(stickyChild, oracleScroll),
            candidate.ViewportRectWithScroll(stickyChild, candidateScroll));
        Assert.Equal(
            oracle.ViewportRectWithScroll(capturedFixed, oracleScroll),
            candidate.ViewportRectWithScroll(capturedFixed, candidateScroll));
        Assert.Equal(
            oracle.ViewportRectWithScroll(scrollTarget, oracleScroll),
            candidate.ViewportRectWithScroll(scrollTarget, candidateScroll));
        Assert.NotEqual(
            new Dictionary<NodeId, (float X, float Y)>(),
            candidate.StickyLayout().Translations(viewport, candidateScroll.RootOffset()));
        using Pixmap candidatePixels = RenderPaint.PaintPreparedWithScroll(
            tree, candidate, candidateResources, candidateScroll)!;
        using Pixmap oraclePixels = RenderPaint.PaintPreparedWithScroll(
            tree, oracle, oracleResources, oracleScroll)!;
        Assert.Equal(oraclePixels.Data(), candidatePixels.Data());
    }

    [Fact]
    public void VisualWaapiRefreshRejectsUnsupportedEffectAtomically()
    {
        DomTree tree = Parse(
            """
            <div id="pure" style="transform:translateX(0px)"></div>
                <div id="mixed" style="transform:translateX(0px)"></div>
            """);
        NodeId pure = ById(tree, "pure");
        NodeId mixed = ById(tree, "mixed");
        AnimationTimelineState timeline = new();
        foreach ((ulong id, NodeId node, float? opacity, string? transform) in
            new (ulong, NodeId, float?, string?)[]
            {
                (1, pure, null, "translateX(40px)"),
                (2, mixed, null, null),
            })
        {
            timeline.RegisterWaapi(new WaapiAnimation
            {
                Id = id,
                Node = node,
                Keyframes = [new WaapiKeyframe { Offset = 1f, Opacity = opacity, Transform = transform }],
                Timing = AnimationTiming.Default with
                {
                    DurationMs = 1_000f,
                    FillMode = AnimationFillMode.Both,
                },
                Easing = null,
                LinearEasing = null,
                StartTimeMs = 0f,
                HoldTimeMs = null,
                PlayState = WaapiPlayState.Running,
            });
        }

        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        StylesheetCache cache = new();
        PreparedRender prepared = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
            tree,
            (100f, 100f),
            null,
            resources,
            [],
            cache,
            AnimationSample.Document(0f),
            timeline)!;
        AnimationSample sampleBefore = prepared.AnimationSample();
        string pureBefore = TransformOpsDigest(prepared.Layout.Styles[pure]);
        Assert.False(prepared.TryAdvanceVisualWaapiSample(
            tree, AnimationSample.Document(500f), timeline));
        Assert.Equal(sampleBefore, prepared.AnimationSample());
        Assert.Equal(pureBefore, TransformOpsDigest(prepared.Layout.Styles[pure]));
    }

    [Fact]
    public void VisualWaapiRefreshRejectsContainingBlockTopologyChange()
    {
        DomTree tree = Parse(
            """<div id="target"><div style="position:fixed;left:10px;top:10px"></div></div>""");
        NodeId target = ById(tree, "target");
        AnimationTimelineState timeline = new();
        timeline.RegisterWaapi(new WaapiAnimation
        {
            Id = 1,
            Node = target,
            Keyframes = [new WaapiKeyframe { Offset = 1f, Opacity = null, Transform = "translateX(40px)" }],
            Timing = AnimationTiming.Default with { DelayMs = 100f, DurationMs = 1_000f },
            Easing = null,
            LinearEasing = null,
            StartTimeMs = 0f,
            HoldTimeMs = null,
            PlayState = WaapiPlayState.Running,
        });
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        StylesheetCache cache = new();
        PreparedRender prepared = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
            tree,
            (100f, 100f),
            null,
            resources,
            [],
            cache,
            AnimationSample.Document(0f),
            timeline)!;
        Assert.Equal(
            0,
            prepared.Layout.Styles[target].ContainingBlockTriggers & ContainingBlockTrigger.Transform);
        Assert.False(prepared.TryAdvanceVisualWaapiSample(
            tree, AnimationSample.Document(500f), timeline));
        Assert.Equal(AnimationSample.Document(0f), prepared.AnimationSample());
    }

    [Fact]
    public void PreparedRenderSamplesExplicitAnimationTimeAndReportsLiveDamage()
    {
        DomTree tree = Parse(
            """
            <style>
                @keyframes dismiss {
                    from { opacity:1; visibility:visible }
                    to { opacity:0; visibility:hidden }
                }
                #overlay { opacity:1; animation:dismiss 600ms linear forwards }
            </style><div id="overlay" style="width:20px;height:20px;background:red"></div>
            """);
        NodeId overlay = Selector(tree, "#overlay");
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        StylesheetCache stylesheets = new();
        PreparedRender atZero = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheAtAnimationTime(
            tree,
            (40f, 40f),
            null,
            resources,
            [],
            stylesheets,
            new AnimationSampleTime(0f))!;
        Assert.Equal(0f, atZero.AnimationSampleTime().Milliseconds);
        Assert.Equal(1f, atZero.Layout.Styles[overlay].Opacity);
        Assert.NotEqual(true, atZero.Layout.Styles[overlay].VisibilityHidden);
        Assert.True(atZero.HasActiveCssAnimations());

        PreparedRender atEnd = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheAtAnimationTime(
            tree,
            (40f, 40f),
            null,
            resources,
            [],
            stylesheets,
            new AnimationSampleTime(600f))!;
        Assert.Equal(0f, atEnd.Layout.Styles[overlay].Opacity);
        Assert.Equal(true, atEnd.Layout.Styles[overlay].VisibilityHidden);
        Assert.True(atEnd.Layout.Styles[overlay].EffectivelyInvisible);
        Assert.False(atEnd.HasActiveCssAnimations());
    }
}
