using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// Shadow roots with the same styles share one compiled sheet, within a pass and across
/// passes. reddit.com has ~170 web-component roots, and every style or layout read after a
/// mutation parsed and indexed all of their sheets again: ~2s per getComputedStyle().
/// </summary>
public sealed class ShadowStylesheetCacheTests
{
    private static string Host(string id, string css) =>
        $"<x-card id={id}><template shadowrootmode=open><style>{css}</style><slot></slot></template></x-card>";

    private static DomTree Page() => HtmlParsing.ParseHtml(
        "<html><body style=\"margin:0\">"
        + Host("a", ":host { display:block; width:77px; height:5px }")
        + Host("b", ":host { display:block; width:77px; height:5px }")
        + Host("c", ":host { display:block; width:33px; height:5px }")
        + "</body></html>");

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    [Fact]
    public void RootsWithTheSameSourcesShareOneSheetAndKeepItForTheNextPass()
    {
        DomTree tree = Page();
        NodeId a = tree.ShadowRootOf(Id(tree, "a"))!.Value;
        NodeId b = tree.ShadowRootOf(Id(tree, "b"))!.Value;
        NodeId c = tree.ShadowRootOf(Id(tree, "c"))!.Value;
        StylesheetCache cache = new();

        Dictionary<NodeId, Stylesheet> first =
            DomCascade.CollectShadowStylesheets(tree, (800f, 600f), CssMediaType.Screen, cache);
        Assert.Same(first[a], first[b]);
        Assert.NotSame(first[a], first[c]);
        Assert.Equal(2, cache.ShadowEntryCount);

        Dictionary<NodeId, Stylesheet> second =
            DomCascade.CollectShadowStylesheets(tree, (800f, 600f), CssMediaType.Screen, cache);
        Assert.Same(first[a], second[a]);
        Assert.Same(first[c], second[c]);

        // Another viewport can change media queries, so nothing carries over.
        Dictionary<NodeId, Stylesheet> resized =
            DomCascade.CollectShadowStylesheets(tree, (400f, 600f), CssMediaType.Screen, cache);
        Assert.NotSame(first[a], resized[a]);
        Assert.Same(resized[a], resized[b]);
    }

    [Fact]
    public void ARootWhoseStyleChangesGetsItsOwnSheetAndAnUnusedOneIsDropped()
    {
        DomTree tree = Page();
        NodeId c = tree.ShadowRootOf(Id(tree, "c"))!.Value;
        StylesheetCache cache = new();
        Dictionary<NodeId, Stylesheet> first =
            DomCascade.CollectShadowStylesheets(tree, (800f, 600f), CssMediaType.Screen, cache);

        NodeId style = tree.Children(c).First(child =>
            tree.GetNode(child)?.AsElement() is { } element
            && string.Equals(element.Name.Local, "style", StringComparison.Ordinal));
        tree.Remove(tree.Children(style)[0]);
        tree.AppendText(style, ":host { display:block; width:44px; height:5px }");

        Dictionary<NodeId, Stylesheet> second =
            DomCascade.CollectShadowStylesheets(tree, (800f, 600f), CssMediaType.Screen, cache);
        Assert.NotSame(first[c], second[c]);
        // The 33px sheet is no longer used by any root.
        Assert.Equal(2, cache.ShadowEntryCount);
    }

    [Fact]
    public void EachHostIsStyledByItsOwnRootsSheet()
    {
        DomTree tree = Page();
        StylesheetCache cache = new();
        for (int pass = 0; pass < 2; pass++)
        {
            DomLayout laid = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
                tree, (800f, 600f), new Dictionary<NodeId, ReplacedIntrinsic>(), [], cache);
            Assert.Equal(77f, laid.Rects[Id(tree, "a")].Width, 0.01f);
            Assert.Equal(77f, laid.Rects[Id(tree, "b")].Width, 0.01f);
            Assert.Equal(33f, laid.Rects[Id(tree, "c")].Width, 0.01f);
        }
    }
}
