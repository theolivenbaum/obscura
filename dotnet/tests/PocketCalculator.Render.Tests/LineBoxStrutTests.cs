// Chromium-verified facts for the strut of a line box that carries an atomic inline. These have
// no counterpart in `mod tests` in crates/obscura-render/src/dom.rs, which models a line box as a
// flex-start row and so cannot express them; see "Known deviations" in todo.md.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class LineBoxStrutTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    /// <summary>
    /// Every host below is <c>font-size: 12px; line-height: 12px</c> over the bundled sans,
    /// whose grid-fitted box at 12px is 11 up and 3 down. The strut's half-leading is therefore
    /// -1, putting its ascent at 10 and its descent at 2 - which is the 2px every case here
    /// turns on.
    /// </summary>
    private const string HOST_STYLE = """
        <style>
            html,body{margin:0;padding:0}
            .w{width:300px;font-size:12px;line-height:12px}
            .ib{display:inline-block;width:12px;height:12px}
        </style>
        """;

    private static float Height(DomTree tree, DomLayout laid, string id) => laid.Rects[Id(tree, id)].Height;

    /// <summary>
    /// CSS 2.1 10.8.1: an atomic inline with no in-flow line boxes has its baseline at its
    /// bottom margin edge, so the whole box sits above the line's baseline and the strut's
    /// descent still has to fit below it. A 12px empty <c>inline-block</c> on a 12px line is
    /// 14px tall in Chromium 141, not 12. This is the 2px icon-box gap.
    /// </summary>
    [Fact]
    public void EmptyBaselineAlignedAtomicInlineLeavesTheStrutsDescentBelowItself()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="plain"><span class="ib"></span></div>
            <div class="w" id="declared"><span class="ib" style="vertical-align:baseline"></span></div>
            <div class="w" id="tall"><span class="ib" style="height:30px"></span></div>
            <div class="w" id="inlineflex"><span style="display:inline-flex;width:12px;height:12px"></span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));

        // Chromium 141: 14, 14, 32, 14.
        Assert.Equal(14f, Height(tree, laid, "plain"), 2);
        Assert.Equal(14f, Height(tree, laid, "declared"), 2);
        Assert.Equal(32f, Height(tree, laid, "tall"), 2);
        Assert.Equal(14f, Height(tree, laid, "inlineflex"), 2);
    }

    /// <summary>
    /// The two cases that pin the rule: an atomic aligned to the line's top is not on the
    /// baseline at all, and an atomic that contains a line box takes that line's baseline
    /// rather than its own bottom edge. Chromium reports 12 for both.
    /// </summary>
    [Fact]
    public void TopAlignedAndTextBearingAtomicInlinesDoNotExtendTheLine()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="top"><span class="ib" style="vertical-align:top"></span></div>
            <div class="w" id="text"><span class="ib">M</span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));

        Assert.Equal(12f, Height(tree, laid, "top"), 2);
        Assert.Equal(12f, Height(tree, laid, "text"), 2);
    }

    /// <summary>
    /// A line takes the tallest ascent and the tallest descent over everything on it, so two
    /// bottom-baseline atomics of different heights give the taller one plus the strut's
    /// descent, and text beside one does not shorten it. Chromium 141: 22 and 14.
    /// </summary>
    [Fact]
    public void TheDescentIsAddedOnceForTheWholeLine()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="two"><span class="ib"></span><span class="ib" style="height:20px"></span></div>
            <div class="w" id="withtext"><span class="ib"></span>text</div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));

        Assert.Equal(22f, Height(tree, laid, "two"), 2);
        Assert.Equal(14f, Height(tree, laid, "withtext"), 2);
    }

    /// <summary>
    /// A replaced element never has an in-flow line box, and neither does a form control - but
    /// Chromium gives the control the baseline of the text it renders itself, so only the
    /// replaced box extends the line. Chromium 141: 14 for the image, 12.5 for the input.
    /// </summary>
    [Fact]
    public void AReplacedBoxExtendsTheLineAndAFormControlDoesNot()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="img"><img src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7" style="width:12px;height:12px"></div>
            <div class="w" id="input"><input style="width:12px;height:12px;padding:0;border:0;margin:0"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));

        Assert.Equal(14f, Height(tree, laid, "img"), 2);
        Assert.True(
            Height(tree, laid, "input") < 13f,
            $"a form control keeps its own text baseline: {Height(tree, laid, "input")}");
    }

    /// <summary>
    /// A <c>line-height</c> shorter than the font box puts the strut's bottom above the
    /// baseline, where it adds nothing: <c>line-height: 0</c> over a 12px atomic is 12 in
    /// Chromium, not 12 minus the negative descent.
    /// </summary>
    [Fact]
    public void AStrutWhoseDescentIsNegativeAddsNothing()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="zero" style="line-height:0"><span class="ib"></span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));

        Assert.Equal(12f, Height(tree, laid, "zero"), 2);
    }

    /// <summary>
    /// An atomic inline that is a flex or grid ITEM is not a line participant and gets no
    /// descent: Chromium reports 12 for both, against 14 for the same box on a line.
    /// </summary>
    [Fact]
    public void AnAtomicThatIsAFlexOrGridItemKeepsItsOwnHeight()
    {
        DomTree tree = Parse(
            HOST_STYLE
            + """
            <div class="w" id="flex" style="display:flex"><span class="ib"></span></div>
            <div class="w" id="grid" style="display:grid"><span class="ib"></span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));

        Assert.Equal(12f, Height(tree, laid, "flex"), 2);
        Assert.Equal(12f, Height(tree, laid, "grid"), 2);
    }
}
