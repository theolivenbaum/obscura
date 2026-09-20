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
    /// <remarks>
    /// The positioned half of this test asserted <c>auto</c> for the two sides nobody
    /// specified. Chromium reports the *used* offsets of a positioned box on all four sides:
    /// measured on Chromium 141 at a 1280x720 viewport, this box reports
    /// <c>top: 10px / right: 1277px / bottom: 692px / left: -5px</c>.
    /// </remarks>
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
        Assert.Equal("1277px", positioned["right"]);
        Assert.Equal("692px", positioned["bottom"]);
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

    /// <summary>
    /// <c>currentcolor</c> resolves against the element's FINAL computed colour, not against
    /// whatever <c>color</c> the cascade had reached when the declaration was applied.
    /// </summary>
    /// <remarks>
    /// The cascade walks declarations in order, so before the deferral both of these reported
    /// black: the first because <c>color</c> is declared after <c>filter</c>, the second
    /// because the element inherits its colour and never declares one. Chromium 141 reports
    /// the element's own colour in both. <c>box-shadow</c> shared the defect and the fix.
    /// </remarks>
    [Fact]
    public void CurrentColorInAFilterOrShadowResolvesAgainstTheFinalComputedColor()
    {
        Assert.Equal(
            "drop-shadow(rgb(10, 20, 30) 1px 2px 3px)",
            Computed(
                """<div id="box" style="filter:drop-shadow(currentColor 1px 2px 3px);color:rgb(10,20,30)">x</div>""",
                "box")["filter"]);

        Assert.Equal(
            "drop-shadow(rgb(1, 2, 3) 1px 2px 3px)",
            Computed(
                """<div style="color:rgb(1,2,3)"><div id="box" style="filter:drop-shadow(currentColor 1px 2px 3px)">x</div></div>""",
                "box")["filter"]);

        Assert.Equal(
            "rgb(10, 20, 30) 1px 2px 3px 0px",
            Computed(
                """<div id="box" style="box-shadow:currentColor 1px 2px 3px;color:rgb(10,20,30)">x</div>""",
                "box")["box-shadow"]);

        Assert.Equal(
            "rgb(1, 2, 3) 1px 2px 3px 0px",
            Computed(
                """<div style="color:rgb(1,2,3)"><div id="box" style="box-shadow:currentColor 1px 2px 3px">x</div></div>""",
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
    /// A <c>flex</c> shorthand carrying a percentage-dependent <c>calc()</c> basis keeps it,
    /// both as the computed value and as the basis the flex algorithm lays the item out with.
    /// </summary>
    /// <remarks>
    /// The shorthand split on plain whitespace, so <c>calc(50% - 6px)</c> arrived as three
    /// fragments and the declaration lost its basis entirely; the item then fell back to its
    /// <c>width</c> and laid out 820px wide, one per row, where Chromium lays out two 404px
    /// items per row.
    /// </remarks>
    [Fact]
    public void FlexShorthandKeepsAPercentageCalcBasis()
    {
        Dictionary<string, string> computed = Computed(
            """
            <div style="display:flex;flex-wrap:wrap;gap:12px;width:820px">
                <div id="box" style="flex:1 1 calc(50% - 6px);width:100%"></div>
                <div style="flex:1 1 calc(50% - 6px);width:100%"></div>
            </div>
            """,
            "box");

        Assert.Equal("calc(50% - 6px)", computed["flex-basis"]);
        Assert.Equal("1 1 calc(50% - 6px)", computed["flex"]);
        Assert.Equal("404px", computed["width"]);
    }

    /// <summary>
    /// The same basis on the longhand, with no growth to hide a wrong one: the item is sized by
    /// the basis alone.
    /// </summary>
    [Fact]
    public void FlexBasisLonghandResolvesACalcAgainstTheContainer()
    {
        Dictionary<string, string> computed = Computed(
            """
            <div style="display:flex;flex-wrap:wrap;gap:12px;width:820px">
                <div id="box" style="flex:0 0 calc(50% - 6px)"></div>
                <div style="flex:0 0 calc(50% - 6px)"></div>
            </div>
            """,
            "box");

        Assert.Equal("calc(50% - 6px)", computed["flex-basis"]);
        Assert.Equal("404px", computed["width"]);
    }

    /// <summary>
    /// Math that does not depend on the percentage basis still computes to a length, which is
    /// what Chromium reports for it.
    /// </summary>
    [Fact]
    public void FlexBasisWithoutAPercentageComputesToALength()
    {
        Dictionary<string, string> computed = Computed(
            """<div style="display:flex"><div id="box" style="flex-basis:calc(10px + 2px)"></div></div>""",
            "box");

        Assert.Equal("12px", computed["flex-basis"]);
    }

    /// <summary>
    /// The UA sheet's <c>overflow</c> defaults for the replaced and form elements that have
    /// one. A missing default let an input's value and a canvas's children paint outside the
    /// box, and reported <c>visible</c> where Chromium reports <c>clip</c> or <c>auto</c>.
    /// </summary>
    [Theory]
    [InlineData("<input id=\"box\">", "clip", "0px")]
    [InlineData("<textarea id=\"box\"></textarea>", "auto", "0px")]
    [InlineData("<canvas id=\"box\"></canvas>", "clip", "content-box")]
    [InlineData("<video id=\"box\"></video>", "clip", "content-box")]
    [InlineData("<img id=\"box\">", "clip", "content-box")]
    [InlineData("<select id=\"box\"></select>", "visible", "0px")]
    [InlineData("<button id=\"box\">b</button>", "visible", "0px")]
    [InlineData("<div id=\"box\">d</div>", "visible", "0px")]
    public void UserAgentOverflowDefaultsMatchChromium(string html, string overflow, string clipMargin)
    {
        Dictionary<string, string> computed = Computed(html, "box");

        Assert.Equal(overflow, computed["overflow"]);
        Assert.Equal(overflow, computed["overflow-x"]);
        Assert.Equal(overflow, computed["overflow-y"]);
        Assert.Equal(clipMargin, computed["overflow-clip-margin"]);
    }

    /// <summary>
    /// An author <c>overflow-clip-margin</c> is reported; the property has no paint effect.
    /// </summary>
    [Fact]
    public void OverflowClipMarginReportsTheAuthoredValue()
    {
        Assert.Equal(
            "10px",
            Computed("""<div id="box" style="overflow:clip;overflow-clip-margin:10px">x</div>""", "box")
                ["overflow-clip-margin"]);
        Assert.Equal(
            "padding-box",
            Computed("""<img id="box" style="overflow-clip-margin:padding-box">""", "box")
                ["overflow-clip-margin"]);
    }

    /// <summary>
    /// Chromium's UA sheet gives a form control <c>color: fieldtext</c>, which is black and
    /// does not inherit, and clears the field background on the controls it paints itself.
    /// </summary>
    [Theory]
    [InlineData("<input id=\"box\">", "rgb(0, 0, 0)", "rgb(255, 255, 255)")]
    [InlineData("<input id=\"box\" type=\"checkbox\">", "rgb(0, 0, 0)", "rgba(0, 0, 0, 0)")]
    [InlineData("<input id=\"box\" type=\"radio\">", "rgb(0, 0, 0)", "rgba(0, 0, 0, 0)")]
    [InlineData("<input id=\"box\" type=\"file\">", "rgb(50, 49, 48)", "rgba(0, 0, 0, 0)")]
    [InlineData("<textarea id=\"box\"></textarea>", "rgb(0, 0, 0)", "rgb(255, 255, 255)")]
    public void UserAgentFormControlColoursMatchChromium(string control, string color, string background)
    {
        Dictionary<string, string> computed = Computed(
            $"""<body style="color:rgb(50, 49, 48)">{control}</body>""",
            "box");

        Assert.Equal(color, computed["color"]);
        Assert.Equal(background, computed["background-color"]);
    }

    /// <summary>An author declaration still wins over the UA control colours.</summary>
    [Fact]
    public void AuthorColoursWinOverTheFormControlDefaults()
    {
        Dictionary<string, string> computed = Computed(
            """<input id="box" style="color:#a0f;background:#fe0">""",
            "box");

        Assert.Equal("rgb(170, 0, 255)", computed["color"]);
        Assert.Equal("rgb(255, 238, 0)", computed["background-color"]);
    }

    /// <summary>
    /// A colour that is not in the legacy sRGB space serializes as <c>color(srgb …)</c>. The
    /// channels are the engine's 8-bit sRGB, so a mix of two opaque colours can differ from
    /// Chromium in the sixth digit; a mix with <c>transparent</c>, which is what Tesserae's
    /// surfaces use, is exact.
    /// </summary>
    [Fact]
    public void ColorMixInSrgbSerializesAsAColorFunction()
    {
        Dictionary<string, string> computed = Computed(
            """
            <div id="box" style="background:color-mix(in srgb, #0443d3 14%, transparent);
                                 color:color-mix(in srgb, #0443d3 40%, transparent)">x</div>
            """,
            "box");

        Assert.Equal("color(srgb 0.0156863 0.262745 0.827451 / 0.14)", computed["background-color"]);
        Assert.Equal("color(srgb 0.0156863 0.262745 0.827451 / 0.4)", computed["color"]);
    }

    /// <summary>A legacy notation keeps <c>rgb()</c>/<c>rgba()</c>, including one with alpha.</summary>
    [Fact]
    public void LegacyColourNotationsStillSerializeAsRgb()
    {
        Dictionary<string, string> computed = Computed(
            """<div id="box" style="background:rgb(4 67 211 / 14%);color:#0443d3">x</div>""",
            "box");

        Assert.Equal("rgba(4, 67, 211, 0.14)", computed["background-color"]);
        Assert.Equal("rgb(4, 67, 211)", computed["color"]);
    }

    /// <summary>
    /// <c>align-self</c>, <c>aspect-ratio</c> and <c>overflow-clip-margin</c> were missing from
    /// the snapshot entirely, so script read the empty string for all three.
    /// </summary>
    [Fact]
    public void SelfAlignmentAndRatioAreReported()
    {
        Dictionary<string, string> initial = Computed("<div id=\"box\">x</div>", "box");

        Assert.Equal("auto", initial["align-self"]);
        Assert.Equal("auto", initial["aspect-ratio"]);
        Assert.Equal("0px", initial["overflow-clip-margin"]);

        Dictionary<string, string> set = Computed(
            """<div style="display:flex"><div id="box" style="align-self:center;aspect-ratio:16/9">x</div></div>""",
            "box");

        Assert.Equal("center", set["align-self"]);
        Assert.Equal("16 / 9", set["aspect-ratio"]);

        // Chromium writes the implicit second term out, and keeps `auto` in front of a ratio.
        Assert.Equal(
            "1.5 / 1",
            Computed("""<div id="box" style="aspect-ratio:1.5">x</div>""", "box")["aspect-ratio"]);
        Assert.Equal(
            "auto 16 / 9",
            Computed("""<div id="box" style="aspect-ratio:auto 16/9">x</div>""", "box")["aspect-ratio"]);

        // A ratio mapped from the width/height attributes is not the property's value.
        Assert.Equal(
            "auto",
            Computed("""<img id="box" width="16" height="9">""", "box")["aspect-ratio"]);
    }

    /// <summary>
    /// The initial <c>auto</c> minimum size computes to <c>0px</c> except on a flex or grid
    /// item, where it stays <c>auto</c>. Reporting <c>auto</c> everywhere told script the box
    /// had a minimum it never set.
    /// </summary>
    [Fact]
    public void AutomaticMinimumSizeIsReportedOnlyForFlexAndGridItems()
    {
        Dictionary<string, string> block = Computed(
            """<div style="display:block"><div id="box">x</div></div>""",
            "box");

        Assert.Equal("0px", block["min-height"]);
        Assert.Equal("0px", block["min-width"]);

        Dictionary<string, string> flexItem = Computed(
            """<div style="display:flex"><div id="box">x</div></div>""",
            "box");

        Assert.Equal("auto", flexItem["min-height"]);
        Assert.Equal("auto", flexItem["min-width"]);

        Dictionary<string, string> gridItem = Computed(
            """<div style="display:grid"><div id="box">x</div></div>""",
            "box");

        Assert.Equal("auto", gridItem["min-height"]);

        // An out-of-flow child of a flex container is not a flex item.
        Dictionary<string, string> positioned = Computed(
            """<div style="display:flex"><div id="box" style="position:absolute">x</div></div>""",
            "box");

        Assert.Equal("0px", positioned["min-height"]);

        // A declared minimum is reported whatever the box is.
        Assert.Equal(
            "4px",
            Computed("""<div style="display:flex"><div id="box" style="min-height:4px">x</div></div>""", "box")
                ["min-height"]);
    }

    [Fact]
    public void IntrinsicSizingKeywordsAreTheComputedMinAndMaxSizes()
    {
        // Unlike `width`/`height`, which report a used length, these four report the keyword
        // itself - it is their computed value. Chromium 141 on the same markup.
        Dictionary<string, string> box = Computed(
            """
            <div id="box" style="min-width:min-content;min-height:max-content;
                                 max-width:fit-content;max-height:min-content">x</div>
            """,
            "box");

        Assert.Equal("min-content", box["min-width"]);
        Assert.Equal("max-content", box["min-height"]);
        Assert.Equal("fit-content", box["max-width"]);
        Assert.Equal("min-content", box["max-height"]);

        // A flex item's `auto` minimum is still reported as `auto` on the axis that has no
        // keyword, not swallowed by the one that does.
        Dictionary<string, string> item = Computed(
            """
            <div style="display:flex">
              <div id="box" style="min-width:max-content;min-height:fit-content">x</div>
            </div>
            """,
            "box");

        Assert.Equal("max-content", item["min-width"]);
        Assert.Equal("fit-content", item["min-height"]);
        Assert.Equal("none", item["max-width"]);
        Assert.Equal("none", item["max-height"]);
    }

    /// <summary>
    /// A relatively positioned box reports its used offsets, which are the shift it was given
    /// and the negation of that on the opposite side.
    /// </summary>
    [Fact]
    public void RelativeInsetsReportTheUsedOffsets()
    {
        Dictionary<string, string> none = Computed(
            """<div id="box" style="position:relative">x</div>""",
            "box");

        Assert.Equal("0px", none["top"]);
        Assert.Equal("0px", none["right"]);
        Assert.Equal("0px", none["bottom"]);
        Assert.Equal("0px", none["left"]);

        Dictionary<string, string> shifted = Computed(
            """<div id="box" style="position:relative;bottom:8px;right:3px">x</div>""",
            "box");

        Assert.Equal("-8px", shifted["top"]);
        Assert.Equal("3px", shifted["right"]);
        Assert.Equal("8px", shifted["bottom"]);
        Assert.Equal("-3px", shifted["left"]);

        // A percentage offset resolves against the containing block, not the viewport.
        Dictionary<string, string> percent = Computed(
            """
            <div style="position:relative;width:300px;height:34px">
                <div id="box" style="position:absolute;top:50%;width:10px;height:10px"></div>
            </div>
            """,
            "box");

        Assert.Equal("17px", percent["top"]);
        Assert.Equal("7px", percent["bottom"]);
    }

    /// <summary>
    /// A table box is laid out as an internal flex container, which must not reach the reported
    /// style: CSS gives a table no flex formatting context, so <c>flex-direction</c> keeps its
    /// initial <c>row</c>.
    /// </summary>
    [Theory]
    [InlineData("<table id=\"box\"><tr><td>d</td></tr></table>")]
    [InlineData("<table><thead id=\"box\"><tr><th>h</th></tr></thead></table>")]
    [InlineData("<table><tbody id=\"box\"><tr><td>d</td></tr></tbody></table>")]
    [InlineData("<table><tr><th id=\"box\">h</th></tr></table>")]
    [InlineData("<table><tr><td id=\"box\">d</td></tr></table>")]
    public void TableBoxesReportTheInitialFlexDirection(string html)
    {
        Assert.Equal("row", Computed(html, "box")["flex-direction"]);
    }

    /// <summary>An author <c>flex-direction</c> on such a box is still reported.</summary>
    [Fact]
    public void AnAuthoredFlexDirectionOnATableBoxIsReported()
    {
        Assert.Equal(
            "column",
            Computed("""<table id="box" style="flex-direction:column"><tr><td>d</td></tr></table>""", "box")
                ["flex-direction"]);
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

    /// <summary>
    /// An authored <c>display</c> on a <c>&lt;table&gt;</c> leaves nothing of the UA rule's
    /// internal flex construction behind.
    /// </summary>
    /// <remarks>
    /// The UA <c>table</c> arm approximates table layout with a column flex container -
    /// <c>flex-direction: column</c>, <c>align-items: stretch</c>, <c>min-width: 0</c> - and an
    /// authored <c>display</c> used to keep those, so a <c>&lt;table style="display:flex"&gt;</c>
    /// reported <c>flex-direction: column</c>. Chromium 141 reports the initial <c>row</c> and
    /// <c>normal</c> for every one of these displays, exactly as it does for a <c>&lt;div&gt;</c>.
    /// </remarks>
    [Theory]
    [InlineData("flex")]
    [InlineData("block")]
    [InlineData("inline-block")]
    [InlineData("grid")]
    [InlineData("inline-flex")]
    [InlineData("inline")]
    [InlineData("table")]
    [InlineData("inline-table")]
    [InlineData("table-cell")]
    public void AnAuthoredDisplayDropsTheUserAgentTableFlexConstruction(string display)
    {
        Dictionary<string, string> table = Computed(
            $"""<table id="box" style="display:{display}"><tr><td>d</td></tr></table>""",
            "box");

        Assert.Equal("row", table["flex-direction"]);
        Assert.Equal("normal", table["align-items"]);
        Assert.Equal("0px", table["min-width"]);

        // The same authored display on a plain box, which never had the construction: the two
        // agree on everything but `box-sizing`, which is a real UA declaration on `table`.
        Dictionary<string, string> div = Computed(
            $"""<div id="box" style="display:{display}">d</div>""",
            "box");

        Assert.Equal(div["flex-direction"], table["flex-direction"]);
        Assert.Equal(div["align-items"], table["align-items"]);
        Assert.Equal(div["min-width"], table["min-width"]);
    }

    /// <summary>
    /// <c>box-sizing: border-box</c> is a genuine UA declaration on <c>table</c>, not part of
    /// the flex construction, so it survives an authored <c>display</c> - Chromium 141 reports
    /// it on a <c>&lt;table&gt;</c> under every display, and <c>content-box</c> on a
    /// <c>&lt;div&gt;</c>.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("flex")]
    [InlineData("block")]
    [InlineData("inline-block")]
    [InlineData("grid")]
    [InlineData("inline-flex")]
    [InlineData("inline")]
    public void TheUserAgentTableBoxSizingSurvivesAnAuthoredDisplay(string display)
    {
        string style = display.Length == 0 ? string.Empty : $""" style="display:{display}" """;

        Assert.Equal(
            "border-box",
            Computed($"""<table id="box"{style}><tr><td>d</td></tr></table>""", "box")["box-sizing"]);
        Assert.Equal(
            "content-box",
            Computed($"""<div id="box"{style}>d</div>""", "box")["box-sizing"]);
    }

    /// <summary>
    /// The construction does not reach the reported style of a table that kept its UA display
    /// either: Chromium 141 reports <c>row</c> / <c>normal</c> on a <c>&lt;table&gt;</c>, its
    /// row groups, and its cells.
    /// </summary>
    [Theory]
    [InlineData("<table id=\"box\"><tr><td>d</td></tr></table>")]
    [InlineData("<table><thead id=\"box\"><tr><th>h</th></tr></thead></table>")]
    [InlineData("<table><tbody id=\"box\"><tr><td>d</td></tr></tbody></table>")]
    [InlineData("<table><tr id=\"box\"><td>d</td></tr></table>")]
    [InlineData("<table><tr><th id=\"box\">h</th></tr></table>")]
    [InlineData("<table><tr><td id=\"box\">d</td></tr></table>")]
    public void TableBoxesReportTheInitialAlignItems(string html)
    {
        Assert.Equal("normal", Computed(html, "box")["align-items"]);
    }

    /// <summary>
    /// An authored value of one of the three wins wherever it sits relative to the
    /// <c>display</c> declaration, which is what separates the construction from real style.
    /// Every expectation is Chromium 141's on the same markup.
    /// </summary>
    [Theory]
    [InlineData("flex-direction:column;display:flex", "column", "normal", "0px")]
    [InlineData("display:flex;flex-direction:column", "column", "normal", "0px")]
    [InlineData("align-items:center;display:flex", "row", "center", "0px")]
    [InlineData("display:flex;align-items:center", "row", "center", "0px")]
    [InlineData("align-items:stretch;display:flex", "row", "stretch", "0px")]
    [InlineData("place-items:center;display:flex", "row", "center", "0px")]
    [InlineData("min-width:50px;display:flex", "row", "normal", "50px")]
    [InlineData("display:flex;min-width:50px", "row", "normal", "50px")]
    public void AnAuthoredValueSurvivesTheDisplayThatDropsTheConstruction(
        string inline,
        string flexDirection,
        string alignItems,
        string minWidth)
    {
        Dictionary<string, string> computed = Computed(
            $"""<table id="box" style="{inline}"><tr><td>d</td></tr></table>""",
            "box");

        Assert.Equal(flexDirection, computed["flex-direction"]);
        Assert.Equal(alignItems, computed["align-items"]);
        Assert.Equal(minWidth, computed["min-width"]);
    }

    /// <summary>
    /// The snapshot used to serialize the taffy display rather than the computed CSS one, so a
    /// table reported the block/flex approximation it is laid out as. Chromium 141 on the same
    /// markup reports the CSS value.
    /// </summary>
    [Theory]
    [InlineData("<table id=\"box\"><tr><td>d</td></tr></table>", "table")]
    [InlineData("<div id=\"box\" style=\"display:table\">d</div>", "table")]
    [InlineData("<div id=\"box\" style=\"display:inline-table\">d</div>", "inline-table")]
    [InlineData("<div id=\"box\" style=\"display:table-cell\">d</div>", "table-cell")]
    [InlineData("<table><caption id=\"box\">c</caption><tr><td>d</td></tr></table>", "table-caption")]
    [InlineData("<table><colgroup id=\"box\"><col></colgroup><tr><td>d</td></tr></table>", "table-column-group")]
    [InlineData("<table><colgroup><col id=\"box\"></colgroup><tr><td>d</td></tr></table>", "table-column")]
    [InlineData("<table><thead id=\"box\"><tr><th>h</th></tr></thead></table>", "table-header-group")]
    [InlineData("<table><tbody id=\"box\"><tr><td>d</td></tr></tbody></table>", "table-row-group")]
    [InlineData("<table><tfoot id=\"box\"><tr><td>d</td></tr></tfoot></table>", "table-footer-group")]
    [InlineData("<table><tr id=\"box\"><td>d</td></tr></table>", "table-row")]
    [InlineData("<table><tr><th id=\"box\">h</th></tr></table>", "table-cell")]
    [InlineData("<table><tr><td id=\"box\">d</td></tr></table>", "table-cell")]
    public void TableBoxesReportTheComputedCssDisplay(string html, string display)
    {
        Assert.Equal(display, Computed(html, "box")["display"]);
    }

    /// <summary>
    /// An authored <c>display</c> replaces the whole outer/inner pair, so it is reported
    /// instead of the table one - including on an element whose user-agent display is a table
    /// display. Chromium 141's values on the same markup.
    /// </summary>
    [Theory]
    [InlineData("<table id=\"box\" style=\"display:flex\"><tr><td>d</td></tr></table>", "flex")]
    [InlineData("<table id=\"box\" style=\"display:block\"><tr><td>d</td></tr></table>", "block")]
    [InlineData("<table><tr id=\"box\" style=\"display:block\"><td>d</td></tr></table>", "block")]
    [InlineData("<table><tbody id=\"box\" style=\"display:flex\"><tr><td>d</td></tr></tbody></table>", "flex")]
    [InlineData("<table><tr><td id=\"box\" style=\"display:block\">d</td></tr></table>", "block")]
    [InlineData("<table><tr><td id=\"box\" style=\"display:flex\">d</td></tr></table>", "flex")]
    [InlineData("<table><caption id=\"box\" style=\"display:flex\">c</caption><tr><td>d</td></tr></table>", "flex")]
    [InlineData("<div id=\"box\" style=\"display:table;display:block\">d</div>", "block")]
    public void AnAuthoredDisplayReplacesTheTableOne(string html, string display)
    {
        Assert.Equal(display, Computed(html, "box")["display"]);
    }

    /// <summary>
    /// The six table-internal displays and <c>table-caption</c> are recorded and reported even
    /// though this engine does not lay them out: CSSOM asks for the resolved value, not the
    /// used one. Chromium 141 on the same markup reports the authored keyword on a
    /// <c>&lt;div&gt;</c> and on a <c>&lt;table&gt;</c> / <c>&lt;tr&gt;</c> / <c>&lt;td&gt;</c>
    /// alike - the element's user-agent display makes no difference to the answer.
    /// </summary>
    [Theory]
    [InlineData("div", "table-row")]
    [InlineData("div", "table-row-group")]
    [InlineData("div", "table-header-group")]
    [InlineData("div", "table-footer-group")]
    [InlineData("div", "table-column")]
    [InlineData("div", "table-column-group")]
    [InlineData("div", "table-caption")]
    [InlineData("span", "table-row")]
    [InlineData("span", "table-caption")]
    public void AnAuthoredInternalTableDisplayIsReported(string tag, string display)
    {
        Assert.Equal(
            display,
            Computed($"""<{tag} id="box" style="display:{display}">d</{tag}>""", "box")["display"]);
    }

    /// <summary>
    /// The same on the elements whose user-agent display is already a table one. Measured on
    /// Chromium 141: <c>&lt;table style="display:table-row"&gt;</c> reports <c>table-row</c>,
    /// and so do a <c>&lt;tbody&gt;</c>, a <c>&lt;tr&gt;</c> and a <c>&lt;td&gt;</c> carrying
    /// the same declaration. The keyword is read before the user-agent table flags because
    /// recording it changes nothing in layout and so cannot clear them.
    /// </summary>
    [Theory]
    [InlineData("<table id=\"box\" style=\"display:table-row\"><tr><td>d</td></tr></table>", "table-row")]
    [InlineData("<table id=\"box\" style=\"display:table-caption\"><tr><td>d</td></tr></table>", "table-caption")]
    [InlineData("<table><tbody id=\"box\" style=\"display:table-row\"><tr><td>d</td></tr></tbody></table>", "table-row")]
    [InlineData("<table><tr id=\"box\" style=\"display:table-row\"><td>d</td></tr></table>", "table-row")]
    [InlineData("<table><tr><td id=\"box\" style=\"display:table-row\">d</td></tr></table>", "table-row")]
    [InlineData("<table><caption id=\"box\" style=\"display:table-caption\">c</caption><tr><td>d</td></tr></table>", "table-caption")]
    [InlineData("<table><colgroup><col id=\"box\" style=\"display:table-column\"></colgroup><tr><td>d</td></tr></table>", "table-column")]
    public void AnAuthoredInternalTableDisplayReplacesTheUserAgentTableOne(string html, string display)
    {
        Assert.Equal(display, Computed(html, "box")["display"]);
    }

    /// <summary>
    /// An internal table display recorded by one declaration is dropped by the next, in either
    /// order, and the keyword is matched case-insensitively. Chromium 141 on the same markup.
    /// </summary>
    [Theory]
    [InlineData("display:table-row;display:block", "block")]
    [InlineData("display:block;display:table-row", "table-row")]
    [InlineData("display:table-row;display:table", "table")]
    [InlineData("display:table;display:table-row", "table-row")]
    [InlineData("display:table-row;display:table-caption", "table-caption")]
    [InlineData("display:TABLE-ROW", "table-row")]
    [InlineData("display:table-row;display:none", "none")]
    [InlineData("display:table-row;display:contents", "contents")]
    public void TheLastDisplayDeclarationWinsOverAnInternalTableOne(string inline, string display)
    {
        Assert.Equal(display, Computed($"""<div id="box" style="{inline}">d</div>""", "box")["display"]);
    }

    /// <summary>
    /// Blockification applies to the seven recorded displays exactly as it does to
    /// <c>table-cell</c>. Measured on Chromium 141: each of the seven reports <c>block</c> as a
    /// flex item, a grid item, a float and an absolutely positioned box, and keeps its keyword
    /// inside an <c>inline-block</c>, which is not a flex or grid container.
    /// </summary>
    [Theory]
    [InlineData("<div style=\"display:flex\"><div id=\"box\" style=\"display:table-row\">d</div></div>", "block")]
    [InlineData("<div style=\"display:flex\"><div id=\"box\" style=\"display:table-caption\">d</div></div>", "block")]
    [InlineData("<div style=\"display:grid\"><div id=\"box\" style=\"display:table-row-group\">d</div></div>", "block")]
    [InlineData("<div style=\"display:grid\"><div id=\"box\" style=\"display:table-column\">d</div></div>", "block")]
    [InlineData("<div id=\"box\" style=\"display:table-row;float:left\">d</div>", "block")]
    [InlineData("<div id=\"box\" style=\"display:table-column-group;float:left\">d</div>", "block")]
    [InlineData("<div id=\"box\" style=\"display:table-header-group;position:absolute\">d</div>", "block")]
    [InlineData("<div id=\"box\" style=\"display:table-footer-group;position:absolute\">d</div>", "block")]
    [InlineData("<div style=\"display:inline-block\"><div id=\"box\" style=\"display:table-row\">d</div></div>", "table-row")]
    public void BlockificationIsReportedOnTheRecordedInternalTableDisplays(string html, string display)
    {
        Assert.Equal(display, Computed(html, "box")["display"]);
    }

    /// <summary>
    /// A <c>&lt;caption&gt;</c>, <c>&lt;col&gt;</c> or <c>&lt;colgroup&gt;</c> has no
    /// user-agent arm - it gets the plain <c>display: block</c> every element starts from - so
    /// an authored <c>display: block</c> on one used to be indistinguishable from the
    /// user-agent value and reported the table display. Chromium 141 reports the authored
    /// value: <c>block</c>, <c>contents</c> and <c>inline</c> for <c>display: initial</c>,
    /// whose initial value is <c>inline</c>.
    /// </summary>
    [Theory]
    [InlineData("caption", "block", "block")]
    [InlineData("caption", "contents", "contents")]
    [InlineData("caption", "initial", "inline")]
    [InlineData("colgroup", "block", "block")]
    [InlineData("colgroup", "initial", "inline")]
    [InlineData("col", "block", "block")]
    [InlineData("col", "contents", "contents")]
    public void AnAuthoredDisplayOnACaptionOrColumnIsReportedOverTheUserAgentTableOne(
        string tag,
        string authored,
        string display)
    {
        string inner = tag switch
        {
            "caption" => $"""<caption id="box" style="display:{authored}">c</caption>""",
            "colgroup" => $"""<colgroup id="box" style="display:{authored}"><col></colgroup>""",
            _ => $"""<colgroup><col id="box" style="display:{authored}"></colgroup>""",
        };

        Assert.Equal(
            display,
            Computed($"<table>{inner}<tr><td>d</td></tr></table>", "box")["display"]);
    }

    /// <summary>
    /// A pseudo-element carries a recorded internal table display the same way, and is
    /// blockified by the element it is generated inside. Chromium 141 on the same markup.
    /// </summary>
    [Theory]
    [InlineData("display:table-row", "", "table-row")]
    [InlineData("display:table-caption", "", "table-caption")]
    [InlineData("display:table-column", "", "table-column")]
    [InlineData("display:table-row", "display:flex", "block")]
    public void AGeneratedBoxReportsItsRecordedInternalTableDisplay(
        string pseudo,
        string host,
        string display)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<style>#box::before{content:\"x\";" + pseudo + "}</style>"
            + "<div id=\"box\" style=\"" + host + "\">d</div>");
        NodeId node = tree.GetElementById("box") ?? throw new InvalidOperationException("no #box");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");
        Dictionary<string, string> computed = prepared.ComputedStyle(node, "::before")
            ?? throw new InvalidOperationException("no computed style");

        Assert.Equal(display, computed["display"]);
    }

    /// <summary>
    /// CSS Display blockification turns an internal table display into <c>block</c> while
    /// <c>inline-table</c> becomes <c>table</c>. Measured on Chromium 141: a
    /// <c>display: table-cell</c> flex item, grid item, float and absolutely positioned box all
    /// report <c>block</c>, and <c>display: inline-table</c> reports <c>table</c> in all four.
    /// A row group inside a <c>&lt;table style="display:flex"&gt;</c> is a real flex item and
    /// reports <c>block</c>, while its rows and cells keep their table displays.
    /// </summary>
    [Theory]
    [InlineData("<div style=\"display:flex\"><div id=\"box\" style=\"display:table-cell\">d</div></div>", "block")]
    [InlineData("<div style=\"display:flex\"><div id=\"box\" style=\"display:inline-table\">d</div></div>", "table")]
    [InlineData("<div style=\"display:grid\"><div id=\"box\" style=\"display:table-cell\">d</div></div>", "block")]
    [InlineData("<div style=\"display:grid\"><div id=\"box\" style=\"display:inline-table\">d</div></div>", "table")]
    [InlineData("<div id=\"box\" style=\"display:table-cell;float:left\">d</div>", "block")]
    [InlineData("<div id=\"box\" style=\"display:inline-table;float:left\">d</div>", "table")]
    [InlineData("<div id=\"box\" style=\"display:table-cell;position:absolute\">d</div>", "block")]
    [InlineData("<div id=\"box\" style=\"display:inline-table;position:absolute\">d</div>", "table")]
    [InlineData("<table style=\"display:flex\"><tbody id=\"box\"><tr><td>d</td></tr></tbody></table>", "block")]
    [InlineData("<table style=\"display:flex\"><tbody><tr id=\"box\"><td>d</td></tr></tbody></table>", "table-row")]
    [InlineData("<table style=\"display:flex\"><tbody><tr><td id=\"box\">d</td></tr></tbody></table>", "table-cell")]
    [InlineData("<div style=\"display:flex\"><table id=\"box\"><tr><td>d</td></tr></table></div>", "table")]
    public void BlockificationIsReportedOnTableDisplays(string html, string display)
    {
        Assert.Equal(display, Computed(html, "box")["display"]);
    }

    /// <summary>
    /// A generated box carries the table display it was given, and is blockified by the
    /// element it is generated inside rather than by that element's parent. Chromium 141 on
    /// the same markup reports <c>table</c> / <c>inline-table</c> / <c>table-cell</c> for a
    /// <c>::before</c> in a block, and <c>block</c> for a <c>table-cell</c> one in a flex
    /// container.
    /// </summary>
    [Theory]
    [InlineData("display:table", "", "table")]
    [InlineData("display:inline-table", "", "inline-table")]
    [InlineData("display:table-cell", "", "table-cell")]
    [InlineData("display:table-cell", "display:flex", "block")]
    [InlineData("display:inline-table", "display:flex", "table")]
    public void AGeneratedBoxReportsItsOwnTableDisplay(string pseudo, string host, string display)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<style>#box::before{content:\"x\";" + pseudo + "}</style>"
            + "<div id=\"box\" style=\"" + host + "\">d</div>");
        NodeId node = tree.GetElementById("box") ?? throw new InvalidOperationException("no #box");
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");
        Dictionary<string, string> computed = prepared.ComputedStyle(node, "::before")
            ?? throw new InvalidOperationException("no computed style");

        Assert.Equal(display, computed["display"]);
    }

    /// <summary>
    /// <c>border-spacing</c> and <c>border-collapse</c> had no key in the snapshot, so page
    /// script read the empty string through bootstrap's inline-declaration fallback. Both are
    /// inherited, which measurement on Chromium 141 confirms: a <c>&lt;div&gt;</c> inside a
    /// table reports <c>2px</c> from the user-agent <c>table { border-spacing: 2px }</c> rule,
    /// a box outside one reports <c>0px</c>, and a descendant still reports <c>2px</c> when the
    /// table itself carries <c>display: flex</c>.
    /// </summary>
    [Theory]
    [InlineData("<table id=\"box\"><tr><td>d</td></tr></table>", "2px", "separate")]
    [InlineData("<table><tr><td id=\"box\">d</td></tr></table>", "2px", "separate")]
    [InlineData("<table><tr><td><div id=\"box\">d</div></td></tr></table>", "2px", "separate")]
    [InlineData("<table style=\"display:flex\"><tr><td><span id=\"box\">d</span></td></tr></table>", "2px", "separate")]
    [InlineData("<div id=\"box\">d</div>", "0px", "separate")]
    [InlineData("<div id=\"box\" style=\"display:table\">d</div>", "0px", "separate")]
    [InlineData("<table style=\"border-collapse:collapse\"><tr><td id=\"box\">d</td></tr></table>", "2px", "collapse")]
    [InlineData("<table style=\"border-collapse:collapse\"><tr><td><div id=\"box\">d</div></td></tr></table>", "2px", "collapse")]
    [InlineData("<div style=\"border-spacing:9px\"><table id=\"box\"><tr><td>d</td></tr></table></div>", "2px", "separate")]
    [InlineData("<div style=\"border-spacing:9px\"><span id=\"box\">d</span></div>", "9px", "separate")]
    public void BorderSpacingAndBorderCollapseAreInherited(
        string html,
        string borderSpacing,
        string borderCollapse)
    {
        Dictionary<string, string> computed = Computed(html, "box");

        Assert.Equal(borderSpacing, computed["border-spacing"]);
        Assert.Equal(borderCollapse, computed["border-collapse"]);
    }

    /// <summary>
    /// <c>border-spacing</c> serializes as one value when the two axes match and as two when
    /// they differ, and Chromium stores it as whole pixels - measured on Chromium 141,
    /// <c>0.6px</c>, <c>1.5px</c>, <c>2.5px</c> and <c>3.75px 7.25px</c> compute to
    /// <c>0px</c>, <c>1px</c>, <c>2px</c> and <c>3px 7px</c>.
    /// </summary>
    /// <remarks>
    /// Not quite a truncation: <c>0.99px</c>, <c>1.99px</c> and <c>4.999px</c> compute to
    /// <c>1px</c>, <c>2px</c> and <c>5px</c> while <c>1.989px</c> and <c>4.5px</c> compute to
    /// <c>1px</c> and <c>4px</c>, which is a hundredth of a pixel added before truncating.
    /// The store is 16 bits wide and overflows to zero rather than saturating: <c>32766px</c>
    /// computes to itself and <c>32767px</c> to <c>0px</c>. This is what layout uses too -
    /// the fraction used to survive into the geometry and only the reported value was whole.
    /// </remarks>
    [Theory]
    [InlineData("border-spacing:3px 7px", "3px 7px")]
    [InlineData("border-spacing:5px", "5px")]
    [InlineData("border-spacing:3px 3px", "3px")]
    [InlineData("border-spacing:100px 0", "100px 0px")]
    [InlineData("border-spacing:0.6px", "0px")]
    [InlineData("border-spacing:1.5px", "1px")]
    [InlineData("border-spacing:2.5px", "2px")]
    [InlineData("border-spacing:3.75px 7.25px", "3px 7px")]
    [InlineData("border-spacing:0.99px", "1px")]
    [InlineData("border-spacing:1.989px", "1px")]
    [InlineData("border-spacing:1.99px", "2px")]
    [InlineData("border-spacing:4.5px", "4px")]
    [InlineData("border-spacing:4.999px", "5px")]
    [InlineData("border-spacing:32766px", "32766px")]
    [InlineData("border-spacing:32767px", "0px")]
    public void BorderSpacingSerializesBothAxesInWholePixels(string inline, string expected)
    {
        Dictionary<string, string> computed = Computed(
            $"""<div id="box" style="{inline}"><span id="kid">k</span></div>""",
            "box");

        Assert.Equal(expected, computed["border-spacing"]);
        Assert.Equal(
            expected,
            Computed(
                $"""<div style="{inline}"><span id="box">k</span></div>""",
                "box")["border-spacing"]);
    }

    /// <summary>
    /// The <c>cellspacing</c> attribute is the table's <c>border-spacing</c> and inherits the
    /// same way: Chromium 141 reports <c>7px</c> on a <c>cellspacing="7"</c> table and on its
    /// cells.
    /// </summary>
    [Fact]
    public void CellSpacingIsReportedAsBorderSpacing()
    {
        Assert.Equal(
            "7px",
            Computed("<table id=\"box\" cellspacing=\"7\"><tr><td>d</td></tr></table>", "box")["border-spacing"]);
        Assert.Equal(
            "7px",
            Computed("<table cellspacing=\"7\"><tr><td id=\"box\">d</td></tr></table>", "box")["border-spacing"]);
    }

    /// <summary>
    /// A font-relative <c>border-spacing</c> resolves against the element's own computed
    /// <c>font-size</c>. Measured on Chromium 141, <c>font-size: 20px; border-spacing: 1em</c>
    /// computes to <c>20px</c> in either declaration order and <c>1em 2em</c> to
    /// <c>20px 40px</c>, while <c>1rem</c> stays at the root's <c>16px</c>. The declaration
    /// was parsed through the <c>PxValue</c> overload that hard-codes <c>em</c> and
    /// <c>rem</c> at 16px, so every one of these read <c>16px</c>.
    /// </summary>
    /// <remarks>
    /// It reaches layout through <c>DomStyleFixups.TableSpacing</c>, so this moves geometry
    /// and is not a reporting-only fix.
    /// </remarks>
    [Theory]
    [InlineData("font-size:20px;border-spacing:1em", "20px")]
    [InlineData("border-spacing:1em;font-size:20px", "20px")]
    [InlineData("font-size:20px;border-spacing:1em 2em", "20px 40px")]
    [InlineData("font-size:20px;border-spacing:1rem", "16px")]
    [InlineData("font-size:10px;border-spacing:1.5em", "15px")]
    public void BorderSpacingResolvesEmAgainstTheElementsOwnFontSize(string inline, string expected)
    {
        Assert.Equal(
            expected,
            Computed($"""<div id="box" style="{inline}">d</div>""", "box")["border-spacing"]);

        // ... and what a child inherits is the resolved value, not the 16px the cascade could
        // only guess at while it ran.
        Assert.Equal(
            expected,
            Computed(
                $"""<div style="{inline}"><table id="box" style="border-spacing:inherit"><tr><td>d</td></tr></table></div>""",
                "box")["border-spacing"]);
    }

    /// <summary>
    /// An invalid <c>border-spacing</c> is dropped, so the property keeps the value it
    /// inherits. Measured on Chromium 141, every declaration below computes to <c>0px</c> in
    /// a plain <c>&lt;div&gt;</c>: a percentage and an <c>auto</c> are not lengths, a third
    /// length is one too many, a negative length is out of range, and a unitless number is a
    /// length only when it is zero.
    /// </summary>
    /// <remarks>
    /// The parser used to keep whatever <c>PxValue</c> could make of each token and report
    /// <c>1px</c>, <c>1px 2px</c>, <c>3px 0px</c>, <c>1px</c> and <c>5px</c> for these; the
    /// negative one reached layout and was clamped only where the snapshot is written.
    /// </remarks>
    [Theory]
    [InlineData("border-spacing:10%")]
    [InlineData("border-spacing:1px 2px 3px")]
    [InlineData("border-spacing:-5px")]
    [InlineData("border-spacing:3px -5px")]
    [InlineData("border-spacing:1px auto")]
    [InlineData("border-spacing:auto")]
    [InlineData("border-spacing:5")]
    [InlineData("border-spacing:")]
    public void AnInvalidBorderSpacingIsDropped(string inline)
    {
        Assert.Equal(
            "0px",
            Computed($"""<div id="box" style="{inline}">d</div>""", "box")["border-spacing"]);

        // ... and leaves the value an earlier declaration in the same block had won.
        Assert.Equal(
            "4px",
            Computed(
                $"""<div id="box" style="border-spacing:4px;{inline}">d</div>""",
                "box")["border-spacing"]);
    }

    /// <summary>
    /// The CSS-wide keywords. Measured on Chromium 141 with a
    /// <c>border-spacing: 10px</c> parent: <c>initial</c> is <c>0px</c> on a
    /// <c>&lt;table&gt;</c> even though the user-agent sheet says <c>2px</c>, <c>inherit</c>
    /// and <c>unset</c> are the parent's <c>10px</c> because the property is inherited, and
    /// <c>revert</c> is the user-agent <c>2px</c>.
    /// </summary>
    [Theory]
    [InlineData("initial", "0px")]
    [InlineData("inherit", "10px")]
    [InlineData("unset", "10px")]
    [InlineData("revert", "2px")]
    [InlineData("revert-layer", "2px")]
    public void BorderSpacingAnswersTheCssWideKeywords(string keyword, string expected)
    {
        Assert.Equal(
            expected,
            Computed(
                $"""<div style="border-spacing:10px"><table id="box" style="border-spacing:{keyword}"><tr><td>d</td></tr></table></div>""",
                "box")["border-spacing"]);
    }

    /// <summary>
    /// <c>cellspacing</c> is mapped with HTML's rules for parsing dimension values, minus the
    /// percentage case. Measured on Chromium 141: leading whitespace is skipped, a run of
    /// digits with an optional fraction is read, and whatever follows is ignored - so
    /// <c>"3px"</c>, <c>"3.9em"</c>, <c>"12abc"</c>, <c>"0007"</c> and <c>"8 9"</c> map, while
    /// <c>"3%"</c>, <c>"-3"</c>, <c>"+7"</c>, <c>".5"</c>, <c>"abc"</c> and the empty string do
    /// not and the table keeps the user-agent <c>2px</c>.
    /// </summary>
    /// <remarks>
    /// The attribute was mapped only when every character was a digit, so all of the mapping
    /// cases below but <c>"3"</c> and <c>"0007"</c> reported <c>2px</c>.
    /// </remarks>
    [Theory]
    [InlineData("3", "3px")]
    [InlineData("3px", "3px")]
    [InlineData("3.9em", "3px")]
    [InlineData("12abc", "12px")]
    [InlineData("0007", "7px")]
    [InlineData("8 9", "8px")]
    [InlineData(" 4 ", "4px")]
    [InlineData("4.7", "4px")]
    [InlineData("0", "0px")]
    [InlineData("3%", "2px")]
    [InlineData("-3", "2px")]
    [InlineData("+7", "2px")]
    [InlineData(".5", "2px")]
    [InlineData("abc", "2px")]
    [InlineData("", "2px")]
    public void CellSpacingIsMappedWithTheHtmlDimensionRules(string attribute, string expected)
    {
        Assert.Equal(
            expected,
            Computed(
                $"""<table id="box" cellspacing="{attribute}"><tr><td>d</td></tr></table>""",
                "box")["border-spacing"]);
    }

    /// <summary>
    /// Chromium reports the two longhands <c>border-spacing</c> is a shorthand for, on every
    /// element and with the same inherited value. Measured on Chromium 141: a
    /// <c>border-spacing: 1px 2px</c> box answers <c>1px</c> and <c>2px</c>, a plain
    /// <c>&lt;div&gt;</c> answers <c>0px</c> for both, and a cell inside an untouched table
    /// answers <c>2px</c>. Neither key was in the snapshot, so page script read the empty
    /// string through bootstrap's inline-declaration fallback.
    /// </summary>
    [Theory]
    [InlineData("<div id=\"box\" style=\"border-spacing:1px 2px\">d</div>", "1px", "2px")]
    [InlineData("<div id=\"box\" style=\"border-spacing:5px\">d</div>", "5px", "5px")]
    [InlineData("<div id=\"box\">d</div>", "0px", "0px")]
    [InlineData("<table><tr><td id=\"box\">d</td></tr></table>", "2px", "2px")]
    public void TheWebkitBorderSpacingLonghandsAreReported(
        string html,
        string horizontal,
        string vertical)
    {
        Dictionary<string, string> computed = Computed(html, "box");

        Assert.Equal(horizontal, computed["-webkit-border-horizontal-spacing"]);
        Assert.Equal(vertical, computed["-webkit-border-vertical-spacing"]);
    }

    /// <summary>
    /// <c>display: flow-root</c> reports <c>flow-root</c>, measured on Chromium 141 - on a
    /// <c>&lt;div&gt;</c>, on an inline element it blockifies, and through
    /// <c>display: inherit</c>. The generic serializer had no <c>flow-root</c> arm, so every
    /// one of these reported <c>block</c>.
    /// </summary>
    /// <remarks>
    /// The other four things that set the flag are answered before this arm is reached:
    /// <c>display: table</c> and <c>inline-table</c> and the anonymous table box through
    /// <c>IsTableBox</c>, and the <c>-webkit-line-clamp</c> adjustment through its own arm -
    /// which Chromium also reports as <c>flow-root</c>, asserted here so the two stay together.
    /// </remarks>
    [Theory]
    [InlineData("<div id=\"box\" style=\"display:flow-root\">d</div>", "flow-root")]
    [InlineData("<span id=\"box\" style=\"display:flow-root\">d</span>", "flow-root")]
    [InlineData("<div style=\"display:flow-root\"><div id=\"box\" style=\"display:inherit\">d</div></div>", "flow-root")]
    [InlineData("<div id=\"box\" style=\"display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:2\">d</div>", "flow-root")]
    [InlineData("<div id=\"box\" style=\"display:table\">d</div>", "table")]
    [InlineData("<div id=\"box\" style=\"display:inline-table\">d</div>", "inline-table")]
    [InlineData("<div id=\"box\" style=\"display:block\">d</div>", "block")]
    [InlineData("<div id=\"box\">d</div>", "block")]
    public void FlowRootIsReportedAsFlowRoot(string html, string display)
    {
        Assert.Equal(display, Computed(html, "box")["display"]);
    }

    /// <summary>
    /// <c>display: inherit</c> carries the recorded internal table display with the rest of
    /// the display pair. Measured on Chromium 141, a <c>display: inherit</c> child of a
    /// <c>display: table-row</c> box reports <c>table-row</c>, and so on through the other
    /// six keywords and through a second level of <c>inherit</c>; the child reported
    /// <c>block</c> because only the layout half of the display was copied.
    /// </summary>
    [Theory]
    [InlineData("table-row")]
    [InlineData("table-row-group")]
    [InlineData("table-header-group")]
    [InlineData("table-footer-group")]
    [InlineData("table-column")]
    [InlineData("table-column-group")]
    [InlineData("table-caption")]
    public void DisplayInheritCarriesTheRecordedInternalTableDisplay(string display)
    {
        Assert.Equal(
            display,
            Computed(
                $"""<div style="display:{display}"><div id="box" style="display:inherit">d</div></div>""",
                "box")["display"]);

        Assert.Equal(
            display,
            Computed(
                $"""<div style="display:{display}"><div style="display:inherit"><div id="box" style="display:inherit">d</div></div></div>""",
                "box")["display"]);
    }

    /// <summary>
    /// ... and a later <c>display</c> replaces it, the same way it replaces the rest of the
    /// pair: Chromium 141 reports <c>block</c> for a <c>display: inherit</c> that a
    /// <c>display: block</c> follows, and <c>block</c> for a <c>display: inherit</c> child of
    /// a box whose own display is not an internal table one.
    /// </summary>
    [Theory]
    [InlineData("<div style=\"display:table-row\"><div id=\"box\" style=\"display:inherit;display:block\">d</div></div>", "block")]
    [InlineData("<div style=\"display:table-row\"><div id=\"box\" style=\"display:block;display:inherit\">d</div></div>", "table-row")]
    [InlineData("<div style=\"display:block\"><div id=\"box\" style=\"display:inherit\">d</div></div>", "block")]
    [InlineData("<div style=\"display:table-cell\"><div id=\"box\" style=\"display:inherit\">d</div></div>", "table-cell")]
    [InlineData("<div style=\"display:table\"><div id=\"box\" style=\"display:inherit\">d</div></div>", "table")]
    public void ADisplayAfterAnInheritReplacesTheRecordedTableDisplay(string html, string display)
    {
        Assert.Equal(display, Computed(html, "box")["display"]);
    }
}
