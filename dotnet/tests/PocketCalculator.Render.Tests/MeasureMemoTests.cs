using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// An inline context keeps every size it has measured to (<c>TextEngine.MeasureTextWithWrap</c>,
/// <c>InlineItem.MeasuredSizes</c>), so taffy asking it again for a min-content, max-content or
/// final size lays nothing out. That must be exact: the whole render, and a retained pass that
/// carries the item over, must be identical to measuring every time.
/// </summary>
public class MeasureMemoTests
{
    private static readonly (float Width, float Height) Viewport = (480f, 360f);

    public static TheoryData<string> Pages()
    {
        TheoryData<string> pages = [];
        foreach (TheoryDataRow<string> row in LineCarryTests.Pages())
        {
            pages.Add(row.Data);
        }

        // Nested intrinsic sizing: flex items in a grid in a shrink-to-fit table cell, which asks
        // the same paragraphs for their min- and max-content widths again and again.
        pages.Add("<table><tr><td><div style='display:grid;grid-template-columns:auto 1fr auto;gap:3px'>"
            + string.Concat(Enumerable.Range(0, 6).Select(i =>
                $"<div style='display:flex;gap:2px'><span>flex item {i}</span><b style='padding:0 3px'>bold words {i}</b></div>"))
            + "</div></td><td>cell <i>text</i> that wraps</td></tr></table>"
            + "<div style='display:inline-flex;flex-direction:column;width:min-content'><p>column flex paragraph text</p><p>another</p></div>");

        // An atomic inline and an anchored float among words, which are never memoized.
        pages.Add("<p style='width:200px'>words <span style='display:inline-block;width:40px'>ib</span> more words "
            + "<span style='float:right;width:30px;height:20px'></span> and the rest of the paragraph that wraps</p>");
        return pages;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void MemoizedMeasurementsLayOutExactlyAsFreshOnes(string body)
    {
        string html = "<!doctype html><html><head><style>body{font:14px/1.3 sans-serif;margin:6px}</style></head><body>"
            + body + "</body></html>";
        DomTree tree = HtmlParsing.ParseHtml(html);

        string memoized;
        string fresh;
        string retained;
        lock (Gate)
        {
            try
            {
                RenderResourceCache resources = new();
                StylesheetCache cache = new();
                PreparedRender first = Prepare(tree, resources, cache);
                memoized = IncrementalLayoutDifferentialTests.Snapshot(tree, first);

                // A retained pass with nothing changed but one class far from the text adopts the
                // measured items and their sizes.
                NodeId body2 = tree.QuerySelector("body")!.Value;
                tree.GetNode(body2)!.SetAttribute("class", "x");
                PreparedRender next = RenderPaint.PrepareDomWithRetainedStyles(
                    tree, Viewport, null, resources, [], cache, first,
                    [RetainedStyleMutation.From(new AttributeStyleMutation(body2, "class", null, "x"))])!;
                retained = IncrementalLayoutDifferentialTests.Snapshot(tree, next);

                TextEngine.MeasureMemoEnabled = false;
                fresh = IncrementalLayoutDifferentialTests.Snapshot(tree, Prepare(tree, new RenderResourceCache(), new StylesheetCache()));
            }
            finally
            {
                TextEngine.MeasureMemoEnabled = true;
            }
        }

        Assert.Equal(fresh, memoized);
        Assert.Equal(fresh, retained);
    }

    // The switch is process-wide; the theory cases flip it one at a time.
    private static readonly Lock Gate = new();

    private static PreparedRender Prepare(DomTree tree, RenderResourceCache resources, StylesheetCache cache) =>
        RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(tree, Viewport, null, resources, [], cache)
        ?? throw new InvalidOperationException("did not prepare");
}
