// Font- and viewport-relative lengths in grid track lists. crates/obscura-render's px_value
// resolves every font-relative unit against 16px and reads `10vw` as 10px; Chromium 141
// resolves them against the element's font, the root font and the viewport.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class GridTrackUnitTests
{
    private static Rect[] LayOut(string gridStyle, int items)
    {
        string children = string.Concat(Enumerable.Range(0, items).Select(i => $"<div id=i{i}></div>"));
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><html style=\"font-size:20px\"><style>body{margin:0}#g{display:grid;"
            + $"font-size:40px;width:900px}}#g>div{{min-width:0}}</style><div id=g style=\"{gridStyle}\">{children}</div>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));
        NodeId Id(string id) => tree.GetElementById(id) ?? throw new InvalidOperationException(id);
        return [.. Enumerable.Range(0, items).Select(i => laid.Rects[Id($"i{i}")])];
    }

    [Fact]
    public void RelativeTrackLengthsResolveAgainstTheElement()
    {
        Rect[] r = LayOut("grid-template-columns:2em 1rem 10vw 10px;grid-template-rows:1em;grid-auto-rows:2em", 5);
        Assert.Equal([80f, 20f, 100f, 10f], r[..4].Select(static x => x.Width));
        Assert.Equal(40f, r[0].Height);
        Assert.Equal(80f, r[4].Height);
    }

    [Fact]
    public void RelativeLengthsInsideMinmaxAndAutoTracks()
    {
        Rect[] r = LayOut("grid-template-columns:minmax(1em,2em) repeat(2,1em);grid-auto-columns:1em;grid-auto-flow:column", 4);
        Assert.Equal([80f, 40f, 40f, 40f], r.Select(static x => x.Width));
    }
}
