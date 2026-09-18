// Chromium-verified facts for an atomic inline whose inline size is definite. These have no
// counterpart in `mod tests` in crates/obscura-render/src/dom.rs; see "Known deviations" in
// todo.md.
using Obscura.Dom;
using Obscura.Render;
using Xunit;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render.Tests;

public class AtomicInlineSizingTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    /// <summary>
    /// CSS 2.1 10.3.9 shrink-to-fits an atomic inline only when its <c>width</c> is
    /// <c>auto</c>. A definite inline size is used as specified and overflows the line box,
    /// for every inline-level inner display mode.
    /// </summary>
    [Fact]
    public void AtomicInlineWithDefiniteWidthOverflowsItsLineInsteadOfShrinking()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0;padding:0}
                *{box-sizing:border-box}
                .host{width:100px;border:1px solid #000}
                .leaf{width:20px;height:16px}
            </style>
            <div class="host"><div id="ib" style="display:inline-block;width:200px"><div class="leaf"></div></div></div>
            <div class="host"><div id="if" style="display:inline-flex;width:200px"><div class="leaf"></div></div></div>
            <div class="host"><div id="ig" style="display:inline-grid;width:200px"><div class="leaf"></div></div></div>
            <div class="host"><div id="it" style="display:inline-table;width:200px"><div class="leaf"></div></div></div>
            <div class="host"><div id="mr" style="display:inline-flex;width:200px;margin-right:22px"><div class="leaf"></div></div></div>
            <div class="host"><div id="fits" style="display:inline-flex;width:50px"><div class="leaf"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // Chromium 141, host content box 98px: every one of these is 200.
        foreach (string name in new[] { "ib", "if", "ig", "it", "mr" })
        {
            Assert.True(
                MathF.Abs(Width(name) - 200f) < 0.01f,
                $"{name} must keep its definite inline size and overflow: {Width(name)}");
        }

        Assert.True(MathF.Abs(Width("fits") - 50f) < 0.01f, $"fits: {Width("fits")}");
    }

    /// <summary>
    /// A percentage or <c>calc()</c> inline size that resolves larger than the line is the
    /// same case: Chromium reports 138 for <c>calc(100% + 40px)</c> and 147 for <c>150%</c>
    /// against a 98px content box.
    /// </summary>
    [Fact]
    public void AtomicInlinePercentageWidthLargerThanTheLineIsNotClamped()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0;padding:0}
                *{box-sizing:border-box}
                .host{width:100px;border:1px solid #000}
                .leaf{width:20px;height:16px}
            </style>
            <div class="host"><div id="calcflex" style="display:inline-flex;width:calc(100% + 40px)"><div class="leaf"></div></div></div>
            <div class="host"><div id="calcblock" style="display:inline-block;width:calc(100% + 40px)"><div class="leaf"></div></div></div>
            <div class="host"><div id="pctflex" style="display:inline-flex;width:150%"><div class="leaf"></div></div></div>
            <div class="host"><div id="pctblock" style="display:inline-block;width:150%"><div class="leaf"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        Assert.True(MathF.Abs(Width("calcflex") - 138f) < 0.01f, $"calcflex: {Width("calcflex")}");
        Assert.True(MathF.Abs(Width("calcblock") - 138f) < 0.01f, $"calcblock: {Width("calcblock")}");
        Assert.True(MathF.Abs(Width("pctflex") - 147f) < 0.01f, $"pctflex: {Width("pctflex")}");
        Assert.True(MathF.Abs(Width("pctblock") - 147f) < 0.01f, $"pctblock: {Width("pctblock")}");
    }

    /// <summary>
    /// The Tesserae dropdown reduction: an <c>inline-flex</c> carrying
    /// <c>width: calc(100% - 16px)</c> inside a content-sized flex item. The cyclic percentage
    /// behaves as <c>auto</c> while the item is measured, so the item shrink-wraps the
    /// dropdown's max-content width, and the dropdown then resolves against that definite
    /// result and overflows by the margin it was measured with.
    /// </summary>
    [Fact]
    public void CyclicCalcInlineSizeResolvesAgainstTheShrinkWrappedContainer()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0;padding:0}
                *{box-sizing:border-box}
                .row{display:flex;width:400px;align-items:center}
                .cont{border:1px solid #000;max-width:200px;position:relative}
                .dd{display:inline-flex;align-items:center;padding:0 5px;height:32px;overflow:hidden}
                .leaf{width:107px;height:16px}
            </style>
            <div class="row">
              <div id="cont" class="cont">
                <div id="dd" class="dd" style="width:calc(100% - 16px);margin-right:22px"><div id="leaf" class="leaf"></div></div>
              </div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // Chromium 141: the container shrink-wraps 107 + 10 padding + 22 margin + 2 border,
        // and the dropdown is that content box less the 16px the calc() subtracts.
        Assert.True(MathF.Abs(Width("cont") - 141f) < 0.01f, $"cont: {Width("cont")}");
        Assert.True(MathF.Abs(Width("dd") - 123f) < 0.01f, $"dd: {Width("dd")}");
        Assert.True(MathF.Abs(Width("leaf") - 107f) < 0.01f, $"leaf: {Width("leaf")}");
    }
}
