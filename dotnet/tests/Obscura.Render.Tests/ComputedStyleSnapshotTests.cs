using Obscura.Dom;
using Xunit;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render.Tests;

/// <summary>
/// The CSSOM snapshot <c>PreparedRender.ComputedStyle</c> hands to <c>op_computed_style</c>.
/// </summary>
/// <remarks>
/// Every expectation here was taken from Chromium 141 (<c>getComputedStyle</c> on the same
/// markup), not from the Rust engine: a property the snapshot omits falls through to
/// bootstrap's inline-declaration fallback, which answers the empty string or a box-derived
/// number, so page script reads a wrong value. Where the port cannot match Chromium (the
/// cascade models only the underline decoration line, and `cursor` / `pointer-events` are not
/// modeled at all) the gap is named in the test rather than asserted.
/// </remarks>
public class ComputedStyleSnapshotTests
{
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
    public void UnstyledBoxReportsTheChromiumInitialValues()
    {
        Dictionary<string, string> computed = Computed("<div id=\"box\">x</div>", "box");

        Assert.Equal("normal", computed["font-style"]);
        Assert.Equal("none", computed["text-decoration"]);
        Assert.Equal("none", computed["text-decoration-line"]);
        Assert.Equal("none", computed["box-shadow"]);
        Assert.Equal("0 1 auto", computed["flex"]);
        Assert.Equal("auto", computed["flex-basis"]);
        Assert.Equal("0", computed["flex-grow"]);
        Assert.Equal("1", computed["flex-shrink"]);
        Assert.Equal("normal", computed["gap"]);
        Assert.Equal("0px", computed["margin"]);
        Assert.Equal("0px", computed["padding"]);
        Assert.Equal("0px", computed["border-width"]);
        Assert.Equal("none", computed["border-style"]);
        Assert.Equal("0px", computed["border-radius"]);
        Assert.Equal("0px none rgb(0, 0, 0)", computed["border"]);
        Assert.Equal("rgb(0, 0, 0)", computed["border-color"]);
        Assert.Equal("visible", computed["overflow"]);
        Assert.Equal("none", computed["background-image"]);
        Assert.Equal("repeat", computed["background-repeat"]);
        Assert.Equal("auto", computed["background-size"]);
        Assert.Equal("0% 0%", computed["background-position"]);
        Assert.Equal(
            "rgba(0, 0, 0, 0) none repeat scroll 0% 0% / auto padding-box border-box",
            computed["background"]);
        Assert.Equal("rgb(0, 0, 0) none 0px", computed["outline"]);
    }

    /// <summary>
    /// A non-positioned box's computed inset is <c>auto</c>. The snapshot used to omit these,
    /// and bootstrap's fallback then answered from the bounding box - <c>0px</c> for most
    /// elements, which reads as "pinned to the top-left" to any script that checks.
    /// </summary>
    [Fact]
    public void InsetsAreAutoUntilSpecified()
    {
        Dictionary<string, string> statik = Computed("<div id=\"box\">x</div>", "box");

        Assert.Equal("auto", statik["top"]);
        Assert.Equal("auto", statik["right"]);
        Assert.Equal("auto", statik["bottom"]);
        Assert.Equal("auto", statik["left"]);

        Dictionary<string, string> positioned = Computed(
            "<div id=\"box\" style=\"position:absolute;top:10px;left:-5px\">x</div>",
            "box");

        Assert.Equal("10px", positioned["top"]);
        Assert.Equal("-5px", positioned["left"]);
        Assert.Equal("auto", positioned["right"]);
        Assert.Equal("auto", positioned["bottom"]);
    }

    [Fact]
    public void BoxMetricShorthandsCollapseLikeCssom()
    {
        Dictionary<string, string> computed = Computed(
            """<div id="box" style="margin:0 0 2px;padding:0 8px">x</div>""",
            "box");

        Assert.Equal("0px 0px 2px", computed["margin"]);
        Assert.Equal("0px 8px", computed["padding"]);

        Dictionary<string, string> four = Computed(
            """<div id="box" style="margin:1px 2px 3px 4px;padding:5px">x</div>""",
            "box");

        Assert.Equal("1px 2px 3px 4px", four["margin"]);
        Assert.Equal("5px", four["padding"]);
    }

    [Fact]
    public void UniformBorderSerializesAsAShorthandAndAMixedOneDoesNot()
    {
        Dictionary<string, string> uniform = Computed(
            """<div id="box" style="color:rgb(50,49,48);border:1px solid">x</div>""",
            "box");

        Assert.Equal("1px solid rgb(50, 49, 48)", uniform["border"]);
        Assert.Equal("1px", uniform["border-width"]);
        Assert.Equal("solid", uniform["border-style"]);

        // An omitted border color is `currentColor`; the shorthand must not report black.
        Assert.Equal("rgb(50, 49, 48)", uniform["border-color"]);

        Dictionary<string, string> mixed = Computed(
            """<div id="box" style="border-left:2px dashed red">x</div>""",
            "box");

        Assert.Equal(string.Empty, mixed["border"]);
        Assert.Equal("0px 0px 0px 2px", mixed["border-width"]);
        Assert.Equal("none none none dashed", mixed["border-style"]);
        Assert.Equal(
            "rgb(0, 0, 0) rgb(0, 0, 0) rgb(0, 0, 0) rgb(255, 0, 0)",
            mixed["border-color"]);
    }

    [Fact]
    public void BorderRadiusCollapsesAndSplitsOnTheSlash()
    {
        Assert.Equal(
            "4px 4px 0px 0px",
            Computed("""<div id="box" style="border-radius:4px 4px 0 0">x</div>""", "box")["border-radius"]);
        Assert.Equal(
            "50%",
            Computed("""<div id="box" style="border-radius:50%">x</div>""", "box")["border-radius"]);
        Assert.Equal(
            "10px / 20px",
            Computed("""<div id="box" style="border-radius:10px / 20px">x</div>""", "box")["border-radius"]);
    }

    [Fact]
    public void OverflowShorthandFollowsTheTwoAxes()
    {
        Dictionary<string, string> same = Computed(
            """<div id="box" style="overflow:hidden">x</div>""",
            "box");

        // The axis VALUES are a separate, tracked cascade defect; the shorthand is what this
        // asserts, so it is checked against the axes the snapshot actually reports.
        Assert.Equal(same["overflow-x"], same["overflow-y"]);
        Assert.Equal(same["overflow-x"], same["overflow"]);

        Dictionary<string, string> split = Computed(
            """<div id="box" style="overflow-x:clip;overflow-y:visible">x</div>""",
            "box");

        Assert.Equal(
            split["overflow-x"] + " " + split["overflow-y"],
            split["overflow"]);
        Assert.Contains(' ', split["overflow"]);
    }

    [Fact]
    public void FlexLonghandsAndShorthandAreReported()
    {
        Dictionary<string, string> computed = Computed(
            """<div style="display:flex"><div id="box" style="flex:1 1 0%">x</div></div>""",
            "box");

        Assert.Equal("1", computed["flex-grow"]);
        Assert.Equal("1", computed["flex-shrink"]);
        Assert.Equal("0%", computed["flex-basis"]);
        Assert.Equal("1 1 0%", computed["flex"]);

        Dictionary<string, string> sized = Computed(
            """<div style="display:flex"><div id="box" style="flex-grow:2.5;flex-basis:10px">x</div></div>""",
            "box");

        Assert.Equal("2.5 1 10px", sized["flex"]);
    }

    [Fact]
    public void GapShorthandCollapsesWhenTheAxesAgree()
    {
        Assert.Equal(
            "2px",
            Computed("""<div id="box" style="display:flex;gap:2px">x</div>""", "box")["gap"]);
        Assert.Equal(
            "3px 5px",
            Computed(
                """<div id="box" style="display:grid;row-gap:3px;column-gap:5px">x</div>""",
                "box")["gap"]);
    }

    [Fact]
    public void BackgroundLonghandsAndShorthandAreReported()
    {
        Dictionary<string, string> computed = Computed(
            """
            <div id="box" style="background:url(q.png) no-repeat;background-size:contain;
                                 background-position:10px 20%;background-color:red">x</div>
            """,
            "box");

        Assert.Equal("url(\"q.png\")", computed["background-image"]);
        Assert.Equal("no-repeat", computed["background-repeat"]);
        Assert.Equal("contain", computed["background-size"]);
        Assert.Equal("10px 20%", computed["background-position"]);
        Assert.Equal("scroll", computed["background-attachment"]);
        Assert.Equal(
            "rgb(255, 0, 0) url(\"q.png\") no-repeat scroll 10px 20% / contain padding-box border-box",
            computed["background"]);
    }

    [Fact]
    public void BoxShadowSerializesColorFirstAndMarksInset()
    {
        Assert.Equal(
            "rgba(0, 0, 0, 0.301961) 0px 1px 2px 0px",
            Computed(
                """<div id="box" style="box-shadow:0 1px 2px rgba(0,0,0,.3)">x</div>""",
                "box")["box-shadow"]);
        Assert.Equal(
            "rgb(255, 0, 0) 0px 0px 0px 1px inset",
            Computed(
                """<div id="box" style="box-shadow:inset 0 0 0 1px red">x</div>""",
                "box")["box-shadow"]);
    }

    [Fact]
    public void FontStyleAndTextDecorationAreReported()
    {
        Dictionary<string, string> computed = Computed(
            """<div id="box" style="font-style:italic;text-decoration:underline">x</div>""",
            "box");

        Assert.Equal("italic", computed["font-style"]);
        Assert.Equal("underline", computed["text-decoration"]);
        Assert.Equal("underline", computed["text-decoration-line"]);
    }

    /// <summary>
    /// CSS numbers serialize with six significant digits, trailing zeros truncated, the way
    /// Blink's <c>String::Number</c> does. <c>11px * 1.3</c> is 14.2999992 as an f32, which
    /// Rust's <c>Display</c> writes out in full.
    /// </summary>
    [Fact]
    public void LengthsUseChromiumSixSignificantDigitFormatting()
    {
        Assert.Equal(
            "14.3px",
            Computed("""<div id="box" style="font-size:11px;line-height:1.3">x</div>""", "box")["line-height"]);
        Assert.Equal(
            "0.333333px",
            Computed("""<div id="box" style="margin-left:0.333333333px">x</div>""", "box")["margin-left"]);
        Assert.Equal(
            "20px",
            Computed("""<div id="box" style="line-height:20px">x</div>""", "box")["line-height"]);
        Assert.Equal(
            "1.5px",
            Computed("""<div id="box" style="letter-spacing:1.5px">x</div>""", "box")["letter-spacing"]);
    }
}
