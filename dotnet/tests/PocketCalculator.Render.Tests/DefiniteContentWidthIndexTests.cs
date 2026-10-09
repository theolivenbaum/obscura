using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The table pass's memoized subtree maxima (<c>DomTableSupport.DefiniteContentWidthIndex</c>)
/// answer exactly what the per-table walk does, whichever node is asked first.
/// </summary>
public class DefiniteContentWidthIndexTests
{
    private const string Html = """
        <!doctype html><html><body>
        <div id=a><div style="width:120px">x</div><div id=b><span style="display:inline-block;width:300px">y</span>
          <table id=t><tr style="width:900px"><td style="width:500px"><div style="width:40px">z</div></td></tr></table></div></div>
        <div id=c><p id=d style="width:80px"><b style="display:inline-block;width:90px">w</b></p></div>
        <div id=e>no widths <i>at all</i></div>
        </body></html>
        """;

    [Fact]
    public void TheIndexMatchesTheWalkInAnyQueryOrder()
    {
        DomTree tree = HtmlParsing.ParseHtml(Html);
        PreparedRender prepared = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(
            tree, (800f, 600f), null, new RenderResourceCache(), [], new StylesheetCache())!;
        Dictionary<NodeId, LayoutStyle> styles = prepared.Layout.Styles;
        List<NodeId> nodes = [tree.Document, .. tree.Descendants(tree.Document)];

        foreach (bool reversed in (ReadOnlySpan<bool>)[false, true])
        {
            DomTableSupport.DefiniteContentWidthIndex index = new(tree, styles);
            IEnumerable<NodeId> order = reversed ? Enumerable.Reverse(nodes) : nodes;
            foreach (NodeId id in order)
            {
                Assert.Equal(DomTableSupport.MaxDefiniteTableContentWidth(tree, id, styles), index.Get(id));
            }
        }

        DomTableSupport.DefiniteContentWidthIndex fresh = new(tree, styles);
        Assert.Equal(300f, fresh.Get(tree.GetElementById("b")!.Value));
        Assert.Equal(300f, fresh.Get(tree.GetElementById("a")!.Value));
        Assert.Equal(90f, fresh.Get(tree.GetElementById("d")!.Value));
        Assert.Null(fresh.Get(tree.GetElementById("e")!.Value));
    }
}
