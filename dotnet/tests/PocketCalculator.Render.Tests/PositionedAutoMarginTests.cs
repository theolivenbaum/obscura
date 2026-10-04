// Chromium-verified facts for the auto margins of absolutely positioned and fixed boxes and
// for the containing block of a fixed box. Every expectation was measured on Chromium 141 at a
// 1280x720 viewport (getBoundingClientRect / getComputedStyle on the same markup). These have
// no counterpart in crates/obscura-render; see "Known deviations" in todo.md.
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class PositionedAutoMarginTests
{
    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    private static (DomTree Tree, DomLayout Laid) Layout(string html)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        return (tree, RenderDom.LayoutDom(tree, (1280f, 720f)));
    }

    private static void AssertRect(DomLayout laid, DomTree tree, string id, float x, float y, float width, float height)
    {
        Rect rect = laid.Rects[Id(tree, id)];
        Assert.True(
            MathF.Abs(rect.X - x) < 0.51f && MathF.Abs(rect.Y - y) < 0.51f
                && MathF.Abs(rect.Width - width) < 0.51f && MathF.Abs(rect.Height - height) < 0.51f,
            $"#{id}: expected ({x}, {y}, {width}x{height}), got ({rect.X}, {rect.Y}, {rect.Width}x{rect.Height})");
    }

    private static Dictionary<string, string> Computed(string html, string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, new RenderResourceCache())
            ?? throw new InvalidOperationException("layout did not prepare");
        return prepared.ComputedStyle(Id(tree, id)) ?? throw new InvalidOperationException("no computed style");
    }

    private const string CentredFixed =
        """
        <!doctype html><html><body style="margin:0">
        <div style="position:relative;width:1024px;margin:0 128px;height:400px">
         <div id="fx" style="position:fixed;left:0;right:0;top:160px;width:760px;height:50px;margin:0 auto"></div>
         <div id="ab" style="position:absolute;left:0;right:0;top:20px;width:200px;height:50px;margin:0 auto"></div>
         <div id="fx2" style="position:fixed;inset:0;width:300px;height:100px;margin:auto"></div>
        </div>
        <div style="transform:translateX(0);position:relative;margin-left:50px;width:600px;height:300px;margin-top:20px">
         <div id="fx4" style="position:fixed;left:0;right:0;top:10px;width:200px;height:50px;margin:0 auto"></div>
         <div id="fx5" style="position:fixed;inset:0;width:200px;height:50px;margin:auto"></div>
        </div>
        </body></html>
        """;

    /// <summary>
    /// bing.com's search box and the standard centred modal: a fixed box with both horizontal
    /// insets and <c>margin: 0 auto</c> centres in the viewport. taffy zeroed both auto
    /// margins whenever the box was at least as wide as the space left beside it (a 760px box
    /// in a 1280px viewport), so it sat at x=0.
    /// </summary>
    [Fact]
    public void FixedBoxWithAutoMarginsCentresInTheViewport()
    {
        (DomTree tree, DomLayout laid) = Layout(CentredFixed);

        AssertRect(laid, tree, "fx", 260f, 160f, 760f, 50f);
        AssertRect(laid, tree, "ab", 540f, 20f, 200f, 50f);
        AssertRect(laid, tree, "fx2", 490f, 310f, 300f, 100f);
    }

    /// <summary>A transformed ancestor is the containing block of a fixed descendant.</summary>
    [Fact]
    public void FixedBoxInsideATransformCentresInTheTransformedAncestor()
    {
        (DomTree tree, DomLayout laid) = Layout(CentredFixed);

        AssertRect(laid, tree, "fx4", 250f, 430f, 200f, 50f);
        AssertRect(laid, tree, "fx5", 250f, 545f, 200f, 50f);
    }

    /// <summary>
    /// getComputedStyle reports a specified inset as specified, and the used value of an auto
    /// margin. The fixed box was measured against its positioned ancestor (left: -128px).
    /// </summary>
    [Fact]
    public void FixedBoxReportsSpecifiedInsetsAndUsedAutoMargins()
    {
        Dictionary<string, string> fx = Computed(CentredFixed, "fx");
        Assert.Equal("0px", fx["left"]);
        Assert.Equal("0px", fx["right"]);
        Assert.Equal("160px", fx["top"]);
        Assert.Equal("510px", fx["bottom"]);
        Assert.Equal("260px", fx["margin-left"]);
        Assert.Equal("260px", fx["margin-right"]);

        Dictionary<string, string> ab = Computed(CentredFixed, "ab");
        Assert.Equal("0px", ab["left"]);
        Assert.Equal("412px", ab["margin-left"]);

        Dictionary<string, string> fx2 = Computed(CentredFixed, "fx2");
        Assert.Equal("0px", fx2["top"]);
        Assert.Equal("310px", fx2["margin-top"]);
        Assert.Equal("490px", fx2["margin-left"]);
    }

    /// <summary>
    /// The CSS 2.1 10.3.7 / 10.6.4 cases taffy got wrong: auto margins resolve only between two
    /// set insets (otherwise they are 0), against the clamped size; a negative inline-axis pair
    /// pins the start margin per the containing block's direction, while a negative block-axis
    /// pair splits equally.
    /// </summary>
    [Fact]
    public void AbsoluteAutoMarginsFollowTheConstraintEquation()
    {
        (DomTree tree, DomLayout laid) = Layout(
            """
            <!doctype html><html><body style="margin:0">
            <div style="position:relative;width:400px;height:300px;margin-left:100px">
             <div id="a" style="position:absolute;left:0;right:0;width:500px;height:50px;margin:0 auto"></div>
             <div id="c" style="position:absolute;top:0;bottom:0;width:50px;height:400px;margin:auto 0"></div>
             <div id="d" style="position:absolute;left:0;right:0;max-width:200px;height:50px;margin:0 auto"></div>
             <div id="f" style="position:absolute;right:10px;width:100px;height:50px;margin-left:20px;margin-right:auto"></div>
             <div id="g" style="position:absolute;left:0;right:0;width:100px;height:50px;margin-left:auto;margin-right:20px"></div>
             <div id="j" style="position:absolute;top:0;bottom:0;max-height:100px;width:10px;margin:auto 0"></div>
            </div>
            <div style="position:relative;width:400px;height:300px;margin-left:100px;direction:rtl">
             <div id="l" style="position:absolute;left:0;right:0;width:500px;height:50px;margin:0 auto"></div>
            </div>
            </body></html>
            """);

        AssertRect(laid, tree, "a", 100f, 0f, 500f, 50f);
        AssertRect(laid, tree, "c", 100f, -50f, 50f, 400f);
        AssertRect(laid, tree, "d", 200f, 0f, 200f, 50f);
        AssertRect(laid, tree, "f", 390f, 0f, 100f, 50f);
        AssertRect(laid, tree, "g", 380f, 0f, 100f, 50f);
        AssertRect(laid, tree, "j", 100f, 100f, 10f, 100f);
        AssertRect(laid, tree, "l", 0f, 300f, 500f, 50f);
    }

    /// <summary>
    /// A stretched fixed box (both insets, auto size) subtracts its margins, padding and
    /// border from the viewport; it was sized to the whole viewport and overflowed it.
    /// </summary>
    [Fact]
    public void StretchedFixedBoxSubtractsItsMarginsAndEdges()
    {
        (DomTree tree, DomLayout laid) = Layout(
            """
            <!doctype html><html><body style="margin:0">
            <div style="position:relative;width:400px;height:300px;margin-left:100px">
             <div id="a" style="position:fixed;left:0;right:0;height:50px;margin:0 20px"></div>
             <div id="b" style="position:fixed;top:0;bottom:0;width:50px;margin:30px 0 10px"></div>
            </div>
            <div id="e" style="position:fixed;inset:0;margin:20px;padding:10px;border:3px solid"></div>
            <div style="transform:translateX(0);margin-left:50px;width:600px;height:300px">
             <div id="t" style="position:fixed;left:0;right:0;top:0;height:20px"></div>
            </div>
            </body></html>
            """);

        AssertRect(laid, tree, "a", 20f, 0f, 1240f, 50f);
        AssertRect(laid, tree, "b", 100f, 30f, 50f, 680f);
        AssertRect(laid, tree, "e", 20f, 20f, 1240f, 680f);
        AssertRect(laid, tree, "t", 50f, 300f, 600f, 20f);
    }

    /// <summary>The same constraint equation in flex and grid containers.</summary>
    [Fact]
    public void AbsoluteAutoMarginsInFlexAndGridContainers()
    {
        (DomTree tree, DomLayout laid) = Layout(
            """
            <!doctype html><html><body style="margin:0">
            <div style="display:flex;position:relative;width:400px;height:300px;margin-left:100px;border:5px solid;padding:7px">
             <div id="fa" style="position:absolute;width:100px;height:50px;margin:auto"></div>
             <div id="fc" style="position:absolute;left:0;right:0;width:100px;height:50px;margin:0 auto"></div>
             <div id="fd" style="position:absolute;inset:0;width:100px;height:50px;margin:auto 10px auto auto"></div>
            </div>
            <div style="display:flex;position:relative;width:400px;height:300px;margin-left:100px;justify-content:center;align-items:center">
             <div id="fe" style="position:absolute;width:100px;height:50px;margin:auto"></div>
            </div>
            <div style="display:grid;position:relative;width:400px;height:300px;margin-left:100px;border:5px solid;padding:7px">
             <div id="gg" style="position:absolute;width:100px;height:50px;margin:auto"></div>
             <div id="gh" style="position:absolute;inset:0;width:100px;height:50px;margin:auto"></div>
            </div>
            </body></html>
            """);

        AssertRect(laid, tree, "fa", 112f, 12f, 100f, 50f);
        AssertRect(laid, tree, "fc", 262f, 12f, 100f, 50f);
        AssertRect(laid, tree, "fd", 409f, 137f, 100f, 50f);
        AssertRect(laid, tree, "fe", 250f, 449f, 100f, 50f);
        AssertRect(laid, tree, "gg", 105f, 629f, 100f, 50f);
        AssertRect(laid, tree, "gh", 262f, 761f, 100f, 50f);
    }
}
