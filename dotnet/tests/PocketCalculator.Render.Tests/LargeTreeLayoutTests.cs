// Layout cost on large documents: nested tables, a deep chain and a wide sibling list. No
// counterpart in crates/obscura-render. The timing bounds are loose on purpose (the suite may run
// on a loaded machine): they catch a pass that turned quadratic again, not a slow constant.
using System.Diagnostics;
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class LargeTreeLayoutTests
{
    private static string NestedTables(int depth)
    {
        StringBuilder html = new("<!doctype html><html><body>");
        for (int i = 0; i < depth; i++)
        {
            html.Append("<table><tr><td>");
        }

        html.Append('x');
        for (int i = 0; i < depth; i++)
        {
            html.Append("</td></tr></table>");
        }

        return html.Append("</body></html>").ToString();
    }

    private static TimeSpan Time(Action action)
    {
        Stopwatch watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }

    [Fact]
    public void DeeplyNestedAutoTablesLayOutInBoundedTime()
    {
        // Every table walked its whole subtree for a definite-width descendant, and every
        // intrinsic measurement rounded the subtree it measured, so a page of nested tables cost
        // the square of its size: 1,500 levels took over two seconds on a quiet machine.
        DomTree tree = HtmlParsing.ParseHtml(NestedTables(1500));
        DomLayout laid = null!;
        TimeSpan elapsed = Time(() => laid = RenderDom.LayoutDom(tree, (800f, 600f)));

        List<NodeId> tables = [.. tree.QuerySelectorAll("table")];
        Assert.True(laid.Rects[tables[0]].Width > 0f);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(30),
            $"1,500 nested tables took {elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void NestedAutoTablesShrinkToTheirContent()
    {
        // Chromium 141: each auto table is its cell's content plus 2px of border-spacing and 1px
        // of cell padding per side, so the widths step down by 6px a level to the 8px glyph.
        // The nested levels take their available width from the layout snapshot of the level
        // above, which is now laid out without rounding.
        DomTree tree = HtmlParsing.ParseHtml(NestedTables(5));
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        List<NodeId> tables = [.. tree.QuerySelectorAll("table")];
        float[] widths = [38f, 32f, 26f, 20f, 14f];
        for (int i = 0; i < tables.Count; i++)
        {
            Assert.Equal(widths[i], laid.Rects[tables[i]].Width);
        }
    }

    [Fact]
    public void WideSiblingListLaysOutInBoundedTime()
    {
        StringBuilder html = new("<!doctype html><html><body>");
        for (int i = 0; i < 20_000; i++)
        {
            html.Append(i % 2 == 0 ? $"<p>p{i}</p>" : $"<span>s{i}</span>");
        }

        DomTree tree = HtmlParsing.ParseHtml(html.Append("</body></html>").ToString());
        DomLayout laid = null!;
        TimeSpan elapsed = Time(() => laid = RenderDom.LayoutDom(tree, (800f, 600f)));

        List<NodeId> paragraphs = [.. tree.QuerySelectorAll("p")];
        Assert.True(laid.Rects[paragraphs[^1]].Y > laid.Rects[paragraphs[0]].Y);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(30),
            $"20,000 siblings took {elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void DeepAlternatingChainLaysOutInBoundedTime()
    {
        StringBuilder html = new("<!doctype html><html><body>");
        for (int i = 0; i < 10_000; i++)
        {
            html.Append(i % 2 == 0 ? "<div>" : "<span>").Append('t').Append(i);
        }

        for (int i = 10_000 - 1; i >= 0; i--)
        {
            html.Append(i % 2 == 0 ? "</div>" : "</span>");
        }

        DomTree tree = HtmlParsing.ParseHtml(html.Append("</body></html>").ToString());
        DomLayout laid = null!;
        TimeSpan elapsed = Time(() => laid = StackGuard.RunWithStackFor(
            tree, () => RenderDom.LayoutDom(tree, (800f, 600f))));

        Assert.True(laid.Rects[tree.QuerySelector("div")!.Value].Height > 0f);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(30),
            $"a 10,000-deep chain took {elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void DefiniteContentWidthIndexAgreesWithTheSubtreeWalk()
    {
        // The index answers in one pass what MaxDefiniteTableContentWidth walks per table; it
        // must answer the same for every node, structural boxes and nested tables included.
        const string html = """
            <!doctype html><html><body>
            <table id="outer"><tr><td><div style="width:120px">a</div>
              <table id="inner"><tr style="width:900px"><td style="width:300px">
                <span style="display:inline-block;width:75px">b</span>
                <div style="display:table-row;width:500px">c</div>
              </td></tr></table>
            </td><td><p style="width:40px">d</p></td></tr></table>
            <div style="width:10px"><div style="width:250.5px"></div></div>
            </body></html>
            """;
        DomTree tree = HtmlParsing.ParseHtml(html);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        DomTableSupport.DefiniteContentWidthIndex index = new(tree, laid.Styles);
        int checkedNodes = 0;
        foreach (NodeId node in tree.Descendants(tree.Document))
        {
            Assert.Equal(
                DomTableSupport.MaxDefiniteTableContentWidth(tree, node, laid.Styles),
                index.Get(node));
            checkedNodes++;
        }

        Assert.True(checkedNodes > 20);
        Assert.Equal(250.5f, index.Get(tree.Document));
        // The cell's 300px and the rows' widths are structural; the 120px block is the floor.
        Assert.Equal(120f, index.Get(tree.GetElementById("outer")!.Value));
    }

    [Fact]
    public void RenderedDescendantsMatchesTheRenderedChildrenDefinition()
    {
        // The walk reads plain elements' children off the sibling chain; shadow hosts, slots and
        // details still go through RenderedChildren. Both must give the same preorder.
        const string html = """
            <!doctype html><html><body>
            <div id="host"><span slot="a">slotted</span><em>unslotted</em><b>default</b></div>
            <template id="shadow"><p>before</p><slot name="a"><i>fallback</i></slot><slot></slot></template>
            <details><summary>s</summary><div>hidden</div></details>
            <details open><summary>t</summary><div>shown</div></details>
            <div style="display:contents"><span>through contents</span></div>
            </body></html>
            """;
        DomTree tree = HtmlParsing.ParseHtml(html);
        NodeId host = tree.GetElementById("host")!.Value;
        NodeId template = tree.GetElementById("shadow")!.Value;
        NodeId root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        NodeId contents = tree.GetNode(template)!.AsElement()!.TemplateContents ?? template;
        foreach (NodeId child in tree.Children(contents))
        {
            tree.AppendChild(root, child);
        }

        List<NodeId> expected = [];
        void Walk(NodeId node)
        {
            foreach (NodeId child in DomTraversal.RenderedChildren(tree, node))
            {
                expected.Add(child);
                Walk(child);
            }
        }

        Walk(tree.Document);
        Assert.Equal(expected, DomTraversal.RenderedDescendants(tree, tree.Document));
        List<NodeId> underHost = [];
        void WalkFrom(NodeId node, List<NodeId> into)
        {
            foreach (NodeId child in DomTraversal.RenderedChildren(tree, node))
            {
                into.Add(child);
                WalkFrom(child, into);
            }
        }

        WalkFrom(host, underHost);
        Assert.Equal(underHost, DomTraversal.RenderedDescendants(tree, host));
        Assert.Contains(underHost, id => DomTraversal.TextContentOfTextNode(tree, id) == "slotted");
        Assert.DoesNotContain(expected, id => DomTraversal.TextContentOfTextNode(tree, id) == "hidden");
    }
}
