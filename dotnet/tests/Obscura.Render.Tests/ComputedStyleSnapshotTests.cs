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
/// cascade models only the underline decoration line) the gap is named in the test rather
/// than asserted.
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

        Assert.Equal("hidden", same["overflow-x"]);
        Assert.Equal("hidden", same["overflow-y"]);
        Assert.Equal("hidden", same["overflow"]);

        Dictionary<string, string> split = Computed(
            """<div id="box" style="overflow-x:clip;overflow-y:visible">x</div>""",
            "box");

        Assert.Equal("clip", split["overflow-x"]);
        Assert.Equal("visible", split["overflow-y"]);
        Assert.Equal("clip visible", split["overflow"]);

        // One scrollable axis turns `visible` into `auto` on the other.
        Dictionary<string, string> coupled = Computed(
            """<div id="box" style="overflow-y:scroll">x</div>""",
            "box");

        Assert.Equal("auto", coupled["overflow-x"]);
        Assert.Equal("scroll", coupled["overflow-y"]);
        Assert.Equal("auto scroll", coupled["overflow"]);

        // Chromium's UA sheet clips an image to its box.
        Assert.Equal("clip", Computed("""<img id="box" width="8" height="8">""", "box")["overflow"]);
    }

    [Fact]
    public void FontFamilyCursorAndPointerEventsAreReported()
    {
        Dictionary<string, string> computed = Computed(
            """
            <div id="box" style="font-family:'Plus Jakarta Sans', Inter, sans-serif;
                                 cursor:pointer; pointer-events:none">x</div>
            """,
            "box");

        Assert.Equal("\"Plus Jakarta Sans\", Inter, sans-serif", computed["font-family"]);
        Assert.Equal("pointer", computed["cursor"]);
        Assert.Equal("none", computed["pointer-events"]);

        Dictionary<string, string> plain = Computed("""<div id="box">x</div>""", "box");
        Assert.Equal("auto", plain["cursor"]);
        Assert.Equal("auto", plain["pointer-events"]);

        // The UA form-control font reaches the snapshot with Chromium's spelling, and the
        // author rule every reset ships takes it back off.
        Assert.Equal(
            "Arial",
            Computed("""<button id="box">x</button>""", "box")["font-family"]);
        Assert.Equal(
            "Verdana",
            Computed(
                """
                <style>body { font-family:Verdana } button { font-family:inherit }</style>
                <button id="box">x</button>
                """,
                "box")["font-family"]);
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
            "rgba(0, 0, 0, 0.3) 0px 1px 2px 0px",
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
    public void FilterIsReportedAndDefaultsToNone()
    {
        Assert.Equal("none", Computed("<div id=\"box\">x</div>", "box")["filter"]);

        static string Filter(string declaration) => Computed(
            $"""<div id="box" style="filter:{declaration}">x</div>""",
            "box")["filter"];

        // A multiplier reports as a number whatever it was authored as, and hue-rotate
        // always carries deg. Measured against Chromium 141.
        Assert.Equal("brightness(0.5)", Filter("brightness(50%)"));
        Assert.Equal("contrast(2)", Filter("contrast(200%)"));
        Assert.Equal("grayscale(1)", Filter("grayscale()"));
        Assert.Equal("opacity(0.25)", Filter("opacity(25%)"));
        Assert.Equal("hue-rotate(90deg)", Filter("hue-rotate(90deg)"));
        Assert.Equal("hue-rotate(0deg)", Filter("hue-rotate(0)"));
        Assert.Equal("blur(0px)", Filter("blur()"));
        Assert.Equal("blur(5px)", Filter("blur(5px)"));
        Assert.Equal(
            "blur(2px) grayscale(1) drop-shadow(rgb(0, 0, 255) 1px 2px 3px)",
            Filter("blur(2px) grayscale(1) drop-shadow(1px 2px 3px blue)"));

        // An SVG filter reference round-trips quoted, even though nothing paints it.
        Assert.Equal("url(\"#f\")", Filter("url(#f)"));
    }

    [Fact]
    public void FilterDropShadowSerializesColorFirstAndAlwaysThreeLengths()
    {
        // The exact computed string Chromium reports for Curiosity's
        // `.tss-pixelavatar-canvas`, which outlines the pixel-art avatar with four 1px
        // shadows. Unlike every other CSS shadow the colour comes FIRST, and the blur is
        // emitted even when it was omitted.
        const string AVATAR_OUTLINE =
            "drop-shadow(rgba(0, 0, 0, 0.5) 1px 0px 0px) "
            + "drop-shadow(rgba(0, 0, 0, 0.5) -1px 0px 0px) "
            + "drop-shadow(rgba(0, 0, 0, 0.5) 0px 1px 0px) "
            + "drop-shadow(rgba(0, 0, 0, 0.5) 0px -1px 0px)";

        Assert.Equal(
            AVATAR_OUTLINE,
            Computed(
                $"""<div id="box" style="filter:{AVATAR_OUTLINE}">x</div>""",
                "box")["filter"]);

        // The authored spelling need not be the serialized one: a trailing colour moves to
        // the front, and an omitted blur reports as 0px.
        Assert.Equal(
            "drop-shadow(rgb(255, 0, 0) 2px 3px 0px)",
            Computed(
                """<div id="box" style="filter:drop-shadow(2px 3px red)">x</div>""",
                "box")["filter"]);

        // An omitted colour resolves to currentColor before it is serialized.
        Assert.Equal(
            "drop-shadow(rgb(10, 20, 30) 1px 2px 0px)",
            Computed(
                """<div id="box" style="color:rgb(10,20,30);filter:drop-shadow(1px 2px)">x</div>""",
                "box")["filter"]);
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

    /// <summary>
    /// Chromium 141 on a bare <c>&lt;button&gt;Hi&lt;/button&gt;</c>: <c>2px</c> / <c>outset</c>
    /// / <c>rgb(0, 0, 0)</c>. The rgb(118, 118, 118) a button is drawn with comes from the
    /// native form-control painter and is not this value; carrying it here made every button
    /// in the app report a border colour Chromium does not. See the <c>button</c> arm of
    /// <c>ComputedStyle</c> and <c>PaintBorders.NativeControlStroke</c>.
    /// </summary>
    [Fact]
    public void ButtonComputesTheBlackUserAgentBorderChromiumReports()
    {
        Dictionary<string, string> computed = Computed("<button id=\"b\">Hi</button>", "b");

        Assert.Equal("2px", computed["border-top-width"]);
        Assert.Equal("outset", computed["border-top-style"]);
        Assert.Equal("rgb(0, 0, 0)", computed["border-top-color"]);
        Assert.Equal("rgb(0, 0, 0)", computed["border-color"]);
    }

    /// <summary>
    /// Chromium 141 on a bare <c>&lt;input value=x&gt;</c>: <c>2px</c> / <c>inset</c> /
    /// <c>rgb(118, 118, 118)</c>. Unlike <c>button</c>, an input's <c>ButtonBorder</c> really
    /// does compute grey; only the style was wrong here, reported as <c>solid</c>.
    /// </summary>
    [Fact]
    public void InputComputesTheInsetUserAgentBorderChromiumReports()
    {
        Dictionary<string, string> computed = Computed("<input id=\"i\" value=\"x\">", "i");

        Assert.Equal("2px", computed["border-top-width"]);
        Assert.Equal("inset", computed["border-top-style"]);
        Assert.Equal("inset", computed["border-bottom-style"]);
        Assert.Equal("rgb(118, 118, 118)", computed["border-top-color"]);
        Assert.Equal("1px", computed["padding-top"]);
        Assert.Equal("2px", computed["padding-left"]);
    }

    /// <summary>
    /// A font-relative length resolves against the element's own computed <c>font-size</c>, not
    /// against CSS's initial 16px. <c>PxValue</c> scaled every one of them by a flat 16, which
    /// is what the reference does, so on the 13px element below Chromium 141 reports 13px for
    /// <c>blur(1em)</c> and for the shadow's blur and this port reported 16px for both.
    /// <c>padding: 1em</c> was always right, because it defers through <c>Dimension</c>.
    /// </summary>
    [Theory]
    [InlineData("filter:blur(1em)", "filter", "blur(13px)")]
    [InlineData("filter:blur(0.5em)", "filter", "blur(6.5px)")]
    [InlineData("filter:blur(1rem)", "filter", "blur(16px)")]
    [InlineData("filter:drop-shadow(1em 2em)", "filter", "drop-shadow(rgb(0, 0, 0) 13px 26px 0px)")]
    [InlineData("box-shadow:0 0 1em red", "box-shadow", "rgb(255, 0, 0) 0px 0px 13px 0px")]
    [InlineData("box-shadow:1em 0 0 red", "box-shadow", "rgb(255, 0, 0) 13px 0px 0px 0px")]
    [InlineData("box-shadow:0 0 1rem red", "box-shadow", "rgb(255, 0, 0) 0px 0px 16px 0px")]
    [InlineData("padding:1em", "padding-top", "13px")]
    public void FontRelativeLengthsResolveAgainstTheElementsOwnFontSize(
        string declarations,
        string property,
        string expected)
    {
        // The root keeps the initial 16px, so an `em` that read the wrong base would land on
        // the `rem` answer and be indistinguishable from it.
        Dictionary<string, string> computed = Computed(
            $"""<div id="box" style="font-size:13px;{declarations}">x</div>""",
            "box");

        Assert.Equal(expected, computed[property]);
    }

    /// <summary>
    /// <c>calc()</c> in a filter or shadow length. These read through a bare-token reader that
    /// cannot see into a function, so the whole declaration used to invalidate; Chromium 141
    /// reports <c>blur(15px)</c> and <c>rgb(255, 0, 0) 0px 0px 15px 0px</c> for these two.
    /// </summary>
    [Fact]
    public void CalcResolvesInsideAFilterOrShadowLength()
    {
        Assert.Equal(
            "blur(15px)",
            Computed(
                """<div id="box" style="font-size:13px;filter:blur(calc(1em + 2px))">x</div>""",
                "box")["filter"]);
        Assert.Equal(
            "rgb(255, 0, 0) 0px 0px 15px 0px",
            Computed(
                """<div id="box" style="font-size:13px;box-shadow:0 0 calc(1em + 2px) red">x</div>""",
                "box")["box-shadow"]);
    }

    /// <summary>
    /// <c>ch</c> was not a unit anywhere in the port: <c>width: 1ch</c> fell through to
    /// <c>auto</c>, and inside the bare-token reader the unit was stripped and <c>1ch</c> read
    /// as the number 1. It now resolves as <c>Dimension.ChPerEm</c> em.
    /// </summary>
    /// <remarks>
    /// DEVIATION: that is one constant - Liberation Sans' advance for <c>0</c>, the face this
    /// renderer paints unstyled text with, matching how <c>ExPerEm</c> was chosen - where
    /// Chromium measures the glyph in the face the page actually resolved. On these elements
    /// Chromium 141 reports 6.5px, its default serif face's 0.5 em. See "Known deviations" in
    /// todo.md.
    /// </remarks>
    [Fact]
    public void ChResolvesAgainstTheFontSizeInsteadOfBeingDroppedOrReadAsPx()
    {
        Dictionary<string, string> computed = Computed(
            """<div id="box" style="font-size:13px;padding-left:1ch;filter:blur(1ch)">x</div>""",
            "box");

        Assert.Equal("7.22998px", computed["padding-left"]);
        Assert.Equal("blur(7.22998px)", computed["filter"]);
    }

    /// <summary>
    /// The same length under an inherited font size, so the value cannot be right by the
    /// element happening to carry the declaration that set it.
    /// </summary>
    [Fact]
    public void EmInAFilterFollowsTheInheritedFontSizeToo()
    {
        Dictionary<string, string> computed = Computed(
            """
            <html style="font-size:32px"><body>
                <div><span id="box" style="filter:blur(1em);box-shadow:0 0 1em red">x</span></div>
            </body></html>
            """,
            "box");

        Assert.Equal("blur(32px)", computed["filter"]);
        Assert.Equal("rgb(255, 0, 0) 0px 0px 32px 0px", computed["box-shadow"]);
        Assert.Equal("32px", computed["font-size"]);
    }

    /// <summary>
    /// <c>select</c> keeps the 1px solid grey border it already had, so the two changes above
    /// cannot spread to the control whose border was right.
    /// </summary>
    [Fact]
    public void SelectKeepsItsSolidGreyUserAgentBorder()
    {
        Dictionary<string, string> computed = Computed(
            "<select id=\"s\"><option>a</option></select>",
            "s");

        Assert.Equal("1px", computed["border-top-width"]);
        Assert.Equal("solid", computed["border-top-style"]);
        Assert.Equal("rgb(118, 118, 118)", computed["border-top-color"]);
    }
}
