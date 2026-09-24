// Inherited visibility, closed <details> and closed <dialog>, checked against Chromium 141. No
// counterpart in crates/obscura-render, which has none of the three.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class HiddenContentTests
{
    private static (DomTree Tree, PreparedRender Prepared) Prepare(string body)
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><html><body style=\"margin:8px;font:16px 'Liberation Sans'\">"
            + body + "</body></html>");
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, new RenderResourceCache())
            ?? throw new InvalidOperationException("layout did not prepare");
        return (tree, prepared);
    }

    private static NodeId Id(DomTree tree, string id) => tree.GetElementById(id)!.Value;

    [Fact]
    public void VisibilityInheritsIntoComputedStyleAndInnerText()
    {
        // Chromium 141: the children report `hidden`, the one that says `visible` shows, and
        // innerText of the hidden block is "shown".
        (DomTree tree, PreparedRender prepared) = Prepare(
            "<div id=v style=\"visibility:hidden\">hid <span id=c>child</span>"
            + "<div id=d>deep <b>bold</b></div> <span id=s style=\"visibility:visible\">shown</span></div>");
        Assert.Equal("hidden", prepared.ComputedStyle(Id(tree, "c"))!["visibility"]);
        Assert.Equal("hidden", prepared.ComputedStyle(Id(tree, "d"))!["visibility"]);
        Assert.Equal("visible", prepared.ComputedStyle(Id(tree, "s"))!["visibility"]);
        Assert.Equal("shown", prepared.InnerText(Id(tree, "v")));
    }

    [Fact]
    public void ClosedDetailsRendersOnlyItsSummaryText()
    {
        (DomTree tree, PreparedRender prepared) = Prepare(
            "<div id=w><details><summary>Sum</summary><p>Hidden content</p></details>"
            + "<details open><summary>Sum2</summary><p>Open content</p></details></div>");
        Assert.Equal("Sum\nSum2\n\nOpen content", prepared.InnerText(Id(tree, "w")));
    }

    [Fact]
    public void DialogWithoutOpenIsDisplayNone()
    {
        // Chromium 141: `display: none` and a 0,0,0,0 rect for a closed dialog; an open one lays out.
        (DomTree tree, PreparedRender prepared) = Prepare(
            "<div id=w><dialog id=closed><p>dialog text</p></dialog>"
            + "<dialog id=open open><p>open text</p></dialog></div>");
        Assert.Equal("none", prepared.ComputedStyle(Id(tree, "closed"))!["display"]);
        Assert.NotEqual("none", prepared.ComputedStyle(Id(tree, "open"))!["display"]);
        Assert.DoesNotContain("dialog text", prepared.InnerText(Id(tree, "w")));
        Assert.Contains("open text", prepared.InnerText(Id(tree, "w")));
    }
}
