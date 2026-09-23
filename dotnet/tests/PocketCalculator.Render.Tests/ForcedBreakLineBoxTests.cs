// Chromium-verified facts about the line box a forced break ends, and about the box a <br>
// itself reports. Both are deviations from crates/obscura-render/src/inline.rs, which turns a
// <br> into a newline and keeps every buffer line the newline split produces; see "Known
// deviations" in todo.md.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class ForcedBreakLineBoxTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    /// <summary>
    /// Every host below is <c>font: 16px/20px 'Liberation Mono'</c> in a 600px block, which is
    /// what the Chromium 141 numbers in each test were measured at.
    /// </summary>
    private const string HOST_STYLE = """
        <style>
            html,body{margin:0;padding:0;font:16px/20px 'Liberation Mono'}
            .w{width:600px}
        </style>
        """;

    private static float Height(DomTree tree, DomLayout laid, string id) => laid.Rects[Id(tree, id)].Height;

    /// <summary>
    /// CSS 2.1 9.4.2: a line box with no text, no preserved white space and no inline box
    /// carrying a margin, border or padding "must be treated as not existing". The line after a
    /// forced break that ends the inline formatting context is the only such box an IFC can
    /// produce, so a trailing break does not add a line.
    /// </summary>
    [Fact]
    public void ATrailingForcedBreakDoesNotAddALineBox()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="only"><br></div>
            <div class="w" id="after">a<br></div>
            <div class="w" id="twice"><br><br></div>
            <div class="w" id="threeAndText">a<br><br></div>
            <div class="w" id="collapsedTail">a<br> </div>
            <div class="w" id="emptyInlineTail">a<br><span></span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));

        // Chromium 141: 20, 20, 40, 40, 20, 20.
        Assert.Equal(20f, Height(tree, laid, "only"), 2);
        Assert.Equal(20f, Height(tree, laid, "after"), 2);
        Assert.Equal(40f, Height(tree, laid, "twice"), 2);
        Assert.Equal(40f, Height(tree, laid, "threeAndText"), 2);
        Assert.Equal(20f, Height(tree, laid, "collapsedTail"), 2);
        Assert.Equal(20f, Height(tree, laid, "emptyInlineTail"), 2);
    }

    /// <summary>
    /// The same rule reaches a preserved newline that ends a <c>white-space: pre</c> block,
    /// which is the other way an IFC ends on a forced break.
    /// </summary>
    [Fact]
    public void APreservedTrailingNewlineDoesNotAddALineBox()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + "<div class=\"w\" id=\"one\" style=\"white-space:pre\">a\n</div>"
            + "<div class=\"w\" id=\"two\" style=\"white-space:pre\">a\nb\n</div>"
            + "<div class=\"w\" id=\"blank\" style=\"white-space:pre\">a\n\n</div>"
            + "<div class=\"w\" id=\"spaces\" style=\"white-space:pre\">a\n   </div>"
            + "<div class=\"w\" id=\"wrap\" style=\"white-space:pre-wrap\">a\n</div>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));

        // Chromium 141: 20, 40, 40, 40, 20. `spaces` keeps its last line because preserved
        // white space is content.
        Assert.Equal(20f, Height(tree, laid, "one"), 2);
        Assert.Equal(40f, Height(tree, laid, "two"), 2);
        Assert.Equal(40f, Height(tree, laid, "blank"), 2);
        Assert.Equal(40f, Height(tree, laid, "spaces"), 2);
        Assert.Equal(20f, Height(tree, laid, "wrap"), 2);
    }

    /// <summary>
    /// Every line that carries content keeps its box, which is the fence around the rule above.
    /// </summary>
    [Fact]
    public void AForcedBreakWithContentAfterItStillAddsALineBox()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="pair">alpha beta<br>gamma delta</div>
            <div class="w" id="noBreak">alpha beta gamma delta</div>
            <div class="w" id="three">x<br>y<br>z</div>
            <div class="w" id="gap">a<br><br>b</div>
            <div class="w" id="leading"><br>a</div>
            <div class="w" id="centered" style="text-align:center">alpha beta<br>gamma delta</div>
            <div class="w" id="empty"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));

        // Chromium 141: 40, 20, 60, 60, 40, 40, 0.
        Assert.Equal(40f, Height(tree, laid, "pair"), 2);
        Assert.Equal(20f, Height(tree, laid, "noBreak"), 2);
        Assert.Equal(60f, Height(tree, laid, "three"), 2);
        Assert.Equal(60f, Height(tree, laid, "gap"), 2);
        Assert.Equal(40f, Height(tree, laid, "leading"), 2);
        Assert.Equal(40f, Height(tree, laid, "centered"), 2);
        Assert.Equal(0f, Height(tree, laid, "empty"), 2);
    }

    /// <summary>
    /// An inline box that <em>starts</em> on the trailing line keeps it alive when it has a
    /// margin, border or padding; one that merely ends there does not, and an edgeless one
    /// never does. Measured in Chromium 141.
    /// </summary>
    [Fact]
    public void AnInlineBoxOpeningOnTheTrailingLineKeepsIt()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="opensBordered">a<br><span style="border:1px solid red"></span></div>
            <div class="w" id="opensMargin">a<br><span style="margin-left:5px"></span></div>
            <div class="w" id="opensPlain">a<br><span></span></div>
            <div class="w" id="closesBordered"><span style="border:1px solid red">a<br></span></div>
            <div class="w" id="beforeBreak"><span style="border:1px solid red">a</span><br></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));

        // Chromium 141: 40, 40, 20, 20, 20.
        Assert.Equal(40f, Height(tree, laid, "opensBordered"), 2);
        Assert.Equal(40f, Height(tree, laid, "opensMargin"), 2);
        Assert.Equal(20f, Height(tree, laid, "opensPlain"), 2);
        Assert.Equal(20f, Height(tree, laid, "closesBordered"), 2);
        Assert.Equal(20f, Height(tree, laid, "beforeBreak"), 2);
    }

    /// <summary>
    /// CSS Text 3 §4.1 collapses only space, tab and the segment breaks. A no-break space, and
    /// the fixed-width spaces, are ordinary text: they are content on the line after a break,
    /// so that line survives. Chromium 141 reports both blocks below as 40 tall.
    /// </summary>
    [Fact]
    public void ANoBreakSpaceAfterAForcedBreakIsContent()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + "<div class=\"w\" id=\"nbsp\">a<br> </div>"
            + "<div class=\"w\" id=\"figure\">a<br> </div>"
            + "<div class=\"w\" id=\"plain\">a<br> </div>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));

        // Chromium 141: 40, 40, 20.
        Assert.Equal(40f, Height(tree, laid, "nbsp"), 2);
        Assert.Equal(40f, Height(tree, laid, "figure"), 2);
        Assert.Equal(20f, Height(tree, laid, "plain"), 2);
    }

    private static Rect BreakRect(DomTree tree, DomLayout laid, string id)
    {
        NodeId node = Id(tree, id);
        return laid.InlineFragments.TryGetValue(node, out List<Rect>? fragments) && fragments.Count > 0
            ? fragments[0]
            : laid.Rects[node];
    }

    /// <summary>
    /// Chromium gives a <c>&lt;br&gt;</c> a real zero-width box on the line it ends, as tall as
    /// its own font box - the baseline less the ascent, not the line-box top. The Rust engine
    /// gives it no box at all, so <c>getBoundingClientRect()</c> answers 0,0,0,0.
    /// </summary>
    [Fact]
    public void AForcedBreakReportsAZeroWidthFontBoxOnItsLine()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w">alpha<br id="first">beta<br id="second">gamma</div>
            <div class="w"><br id="alone"></div>
            <div class="w" style="text-align:center">alpha<br id="centered">beta</div>
            <div class="w" style="text-align:right">alpha<br id="right">beta</div>
            <div class="w" style="font-size:30px">alpha<br id="big">beta</div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));

        // Chromium 141, x,y,width,height: 48.02,1,0,18 / 38.41,21,0,18 / 0,61,0,18 /
        // 324,81,0,18 / 600,121,0,18 / 90.02,153,0,34. The x tolerance carries the known
        // monospace advance difference against Chromium's DejaVu-calibrated shaping.
        Rect first = BreakRect(tree, laid, "first");
        Assert.Equal(48.02f, first.X, 1);
        Assert.Equal(1f, first.Y, 2);
        Assert.Equal(0f, first.Width, 2);
        Assert.Equal(18f, first.Height, 2);

        Rect second = BreakRect(tree, laid, "second");
        Assert.Equal(38.41f, second.X, 1);
        Assert.Equal(21f, second.Y, 2);

        Rect alone = BreakRect(tree, laid, "alone");
        Assert.Equal(0f, alone.X, 2);
        Assert.Equal(61f, alone.Y, 2);
        Assert.Equal(0f, alone.Width, 2);
        Assert.Equal(18f, alone.Height, 2);

        Rect centered = BreakRect(tree, laid, "centered");
        Assert.Equal(324f, centered.X, 2);
        Assert.Equal(81f, centered.Y, 2);
        Rect right = BreakRect(tree, laid, "right");
        Assert.Equal(600f, right.X, 2);
        Assert.Equal(121f, right.Y, 2);

        Rect big = BreakRect(tree, laid, "big");
        Assert.Equal(90.02f, big.X, 1);
        Assert.Equal(153f, big.Y, 2);
        Assert.Equal(34f, big.Height, 2);
    }

    /// <summary>
    /// An empty inline box reports the same shape for the same reason - it too owns no shaped
    /// glyphs - and an empty span sitting where a break does was reporting 0,0,0,0 as well.
    /// </summary>
    [Fact]
    public void AnEmptyInlineBoxAtTheEndOfALineReportsItsFontBox()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w">alpha<span id="tight"></span><br>beta</div>
            <div class="w" style="line-height:40px">alpha<span id="leaded"></span><br>beta</div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));

        // Chromium 141: 48.02,1,0,18 and 48.02,51,0,18 - the height is the font box either way,
        // so the 40px line-height does not change it.
        Rect tight = BreakRect(tree, laid, "tight");
        Assert.Equal(48.02f, tight.X, 1);
        Assert.Equal(1f, tight.Y, 2);
        Assert.Equal(18f, tight.Height, 2);

        Rect leaded = BreakRect(tree, laid, "leaded");
        Assert.Equal(48.02f, leaded.X, 1);
        Assert.Equal(51f, leaded.Y, 2);
        Assert.Equal(18f, leaded.Height, 2);
    }
}
