using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// UA defaults, presentation attributes and computed values for elements in the SVG namespace.
/// </summary>
/// <remarks>
/// Every expectation here was read off Chromium 141's <c>getComputedStyle</c> on the same
/// markup, not off the Rust engine: <c>crates/obscura-render/src/style.rs</c> keys its UA table
/// on the tag name alone and maps no SVG presentation attribute at all, so the reference gives
/// an SVG <c>&lt;a&gt;</c> the HTML link colour, <c>&lt;title&gt;</c>/<c>&lt;desc&gt;</c>
/// <c>display: none</c>, every shape <c>display: block</c>, and reports nothing for
/// <c>fill</c> / <c>stroke</c> / <c>stroke-width</c> / <c>text-anchor</c>. See "Known
/// deviations" in todo.md.
/// </remarks>
public class SvgStyleTests
{
    private const string SvgNs = Namespaces.Svg;

    /// <summary>The probe page the 157-route SPA survey reduced its SVG mismatches to.</summary>
    private const string ProbeHtml = """
        <!doctype html><html><head><style>body{font-size:13px;font-family:sans-serif}</style></head>
        <body>
        <svg id="s" width="200" height="80" viewBox="0 0 200 80" font-size="20" fill="green">
          <title id="t">chart title</title>
          <desc id="de">a description</desc>
          <rect id="r" x="1" y="1" width="10" height="10" fill="red" opacity="0.5" stroke="blue" stroke-width="3"/>
          <g id="g" font-family="monospace"><text id="tx" x="5" y="40" font-size="10" text-anchor="middle">hello</text></g>
          <text id="tx2" x="5" y="60" style="font-size:11px">styled</text>
          <text id="tx3" x="5" y="70">inherit-from-svg</text>
          <circle id="ci" cx="50" cy="50" r="4" visibility="hidden"/>
          <a id="sa"><rect id="r2" width="4" height="4"/></a>
          <foreignObject id="fo" width="10" height="10"></foreignObject>
          <tspan id="ts">x</tspan>
        </svg>
        </body></html>
        """;

    private static Dictionary<string, string> Computed(string html, string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        return prepared.ComputedStyle(node) ?? throw new InvalidOperationException("no computed style");
    }

    [Fact]
    public void SvgNamespaceDefaultsToInlineExceptTextAndForeignObject()
    {
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle("rect", SvgNs).Display);
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle("g", SvgNs).Display);
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle("svg", SvgNs).Display);
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle("tspan", SvgNs).Display);
        Assert.Equal(Display.Block, ComputedStyle.UaStyle("text", SvgNs).Display);
        Assert.Equal(Display.Block, ComputedStyle.UaStyle("foreignObject", SvgNs).Display);
    }

    /// <summary>
    /// Chromium's SVG UA sheet does not hide <c>title</c> / <c>desc</c> / <c>metadata</c>: they
    /// report <c>display: inline</c> and are non-rendered through the SVG rendering model,
    /// which is what the SVG rasterizer skips them by tag name for.
    /// </summary>
    [Fact]
    public void SvgTitleAndDescAreInlineNotDisplayNone()
    {
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle("title", SvgNs).Display);
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle("desc", SvgNs).Display);
        Assert.Equal(Display.Inline, ComputedStyle.UaStyle("metadata", SvgNs).Display);

        // The HTML elements of the same name stay hidden.
        Assert.Equal(Display.None, ComputedStyle.UaStyle("title").Display);
    }

    [Fact]
    public void HtmlLinkStylingDoesNotReachAnSvgAnchor()
    {
        LayoutStyle htmlAnchor = ComputedStyle.UaStyle("a");
        Assert.Equal(new RgbaColor(0, 0, 238, 255), htmlAnchor.Color);
        Assert.True(htmlAnchor.Underline);

        LayoutStyle svgAnchor = ComputedStyle.UaStyle("a", SvgNs);
        Assert.Null(svgAnchor.Color);
        Assert.Null(svgAnchor.Underline);
    }

    /// <summary>
    /// <c>fill</c>, <c>stroke</c>, <c>stroke-width</c> and <c>text-anchor</c> are ordinary
    /// inherited CSS properties in Chromium and apply to every element, so an HTML box reports
    /// their initial values rather than the empty string.
    /// </summary>
    [Fact]
    public void SvgPaintPropertiesReportTheirInitialValuesOnAnHtmlBox()
    {
        Dictionary<string, string> computed = Computed("<div id=\"box\">x</div>", "box");

        Assert.Equal("rgb(0, 0, 0)", computed["fill"]);
        Assert.Equal("none", computed["stroke"]);
        Assert.Equal("1px", computed["stroke-width"]);
        Assert.Equal("start", computed["text-anchor"]);
    }

    [Fact]
    public void PresentationAttributesFeedTheCascade()
    {
        Dictionary<string, string> rect = Computed(ProbeHtml, "r");

        Assert.Equal("rgb(255, 0, 0)", rect["fill"]);
        Assert.Equal("rgb(0, 0, 255)", rect["stroke"]);
        Assert.Equal("3px", rect["stroke-width"]);
        Assert.Equal("0.5", rect["opacity"]);
        Assert.Equal("inline", rect["display"]);

        Assert.Equal("middle", Computed(ProbeHtml, "tx")["text-anchor"]);
        Assert.Equal("hidden", Computed(ProbeHtml, "ci")["visibility"]);
    }

    [Fact]
    public void PresentationAttributesInheritDownTheSubtree()
    {
        // font-size="20" on the <svg>, font-family="monospace" on the <g>.
        Assert.Equal("20px", Computed(ProbeHtml, "tx3")["font-size"]);
        Assert.Equal("monospace", Computed(ProbeHtml, "tx")["font-family"]);

        // fill="green" on the <svg> reaches a rect that specifies none of its own.
        Assert.Equal("rgb(0, 128, 0)", Computed(ProbeHtml, "r2")["fill"]);
        Assert.Equal("rgb(0, 128, 0)", Computed(ProbeHtml, "g")["fill"]);
    }

    /// <summary>
    /// A presentation attribute is author origin at the very bottom of the cascade, so the
    /// <c>style</c> attribute beats an inherited one.
    /// </summary>
    [Fact]
    public void TheStyleAttributeBeatsAnInheritedPresentationAttribute()
    {
        Assert.Equal("11px", Computed(ProbeHtml, "tx2")["font-size"]);
    }

    /// <summary>An author rule of any specificity outranks a presentation attribute.</summary>
    [Fact]
    public void AnAuthorTypeSelectorBeatsAPresentationAttribute()
    {
        const string Html = """
            <html><head><style>rect { fill: blue }</style></head>
            <body><svg width="20" height="20"><rect id="r" width="4" height="4" fill="red"/></svg></body></html>
            """;

        Assert.Equal("rgb(0, 0, 255)", Computed(Html, "r")["fill"]);
    }

    /// <summary>A bare number on a length-valued presentation attribute is in user units.</summary>
    [Fact]
    public void BareNumbersOnLengthAttributesAreUserUnits()
    {
        const string Html = """
            <html><body><svg width="20" height="20" font-size="7">
            <rect id="r" width="4" height="4" stroke-width="2.5"/></svg></body></html>
            """;

        Dictionary<string, string> computed = Computed(Html, "r");
        Assert.Equal("2.5px", computed["stroke-width"]);
        Assert.Equal("7px", computed["font-size"]);
    }

    /// <summary><c>currentColor</c> resolves against the element's own computed colour.</summary>
    [Fact]
    public void CurrentColorFillResolvesAgainstTheComputedColor()
    {
        const string Html = """
            <html><body><svg width="20" height="20" style="color:#ff8800">
            <rect id="r" width="4" height="4" fill="currentColor"/></svg></body></html>
            """;

        Assert.Equal("rgb(255, 136, 0)", Computed(Html, "r")["fill"]);
    }

    /// <summary>A paint server reports its own reference, not a colour.</summary>
    [Fact]
    public void APaintServerFillKeepsItsReference()
    {
        const string Html = """
            <html><body><svg width="20" height="20">
            <rect id="r" width="4" height="4" fill="url(#grad)"/></svg></body></html>
            """;

        Assert.Equal("url(#grad)", Computed(Html, "r")["fill"]);
    }

    /// <summary>
    /// <c>&lt;title&gt;</c> computing to <c>inline</c> must not put its text on the page: the SVG
    /// rendering model is what makes it non-rendered, and the rasterizer skips it by tag name.
    /// </summary>
    [Fact]
    public void AnSvgTitleStillDoesNotPaint()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """
            <html><body style="margin:0">
            <svg width="200" height="80"><title>TITLEWORD</title><desc>DESCWORD</desc>
            <rect x="1" y="1" width="50" height="20" fill="#ff0000"/></svg>
            </body></html>
            """);
        Pixmap pixmap = RenderPaint.PaintDom(tree, (200f, 80f), null)
            ?? throw new InvalidOperationException("paint produced no surface");

        // The only ink is the red rect; glyphs anywhere would show as non-red, non-white pixels.
        foreach (PremultipliedColor pixel in pixmap.Pixels)
        {
            bool white = pixel.R > 250 && pixel.G > 250 && pixel.B > 250;
            bool red = pixel.R > 200 && pixel.G < 60 && pixel.B < 60;
            Assert.True(white || red, $"unexpected ink rgb({pixel.R}, {pixel.G}, {pixel.B})");
        }
    }

    /// <summary>
    /// An inline <c>&lt;svg&gt;</c> is a replaced box, so it keeps its own box no matter what it
    /// contains. Its SVG children compute block-level (<c>&lt;text&gt;</c>), which used to make
    /// the boxless-inline pass splice the svg away entirely: the svg laid out 0x0 and its text
    /// was laid out and painted as HTML in the body.
    /// </summary>
    [Fact]
    public void AnInlineSvgWrappingBlockLevelContentKeepsItsBox()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """
            <html><body style="margin:0">
            <svg id="s" width="120" height="50" viewBox="0 0 120 50"><text x="4" y="32">SVG text</text></svg>
            </body></html>
            """);
        DomLayout layout = RenderDom.LayoutDom(tree, (160f, 80f));
        NodeId svg = tree.GetElementById("s") ?? throw new InvalidOperationException("no #s");

        Assert.True(layout.Rects.TryGetValue(svg, out Rect rect));
        Assert.Equal(120f, rect.Width);
        Assert.Equal(50f, rect.Height);
    }
}
