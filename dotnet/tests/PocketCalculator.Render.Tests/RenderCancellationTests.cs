// SECURITY.md H8: layout and paint stop when their pass is cancelled. No counterpart in
// crates/obscura-render, which has no way to stop a pass; see "Known deviations" in todo.md.
using System.Diagnostics;
using System.Text;
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class RenderCancellationTests
{
    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    /// <summary>
    /// A long page of paragraphs, each wrapping around a float of its own: seconds of layout.
    /// This used to be 300 nested floats, which the flex-mapped float layout re-measured at
    /// every level for every ancestor (well over a minute, SECURITY.md "Fix status"); real
    /// float layout lays those out in a few milliseconds, so they no longer reach a deadline.
    /// </summary>
    private static DomTree FloatedParagraphs(int count)
    {
        var html = new StringBuilder("<!doctype html><html><body>");
        for (int i = 0; i < count; i++)
        {
            html.Append("<p>text <span style=\"float:left;width:20px;height:30px\"></span>more words here</p>");
        }

        html.Append("</body></html>");
        return HtmlParsing.ParseHtml(html.ToString());
    }

    [Fact]
    public void APathologicalLayoutStopsAtItsDeadline()
    {
        DomTree tree = FloatedParagraphs(5_000);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() =>
            RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCacheWithAnimationState(
                tree,
                (800f, 600f),
                null,
                new RenderResourceCache(),
                [],
                new Css.StylesheetCache(),
                AnimationSample.Document(0f),
                new AnimationTimelineState(),
                deadline.Token));

        // Generous for a loaded machine; the uncancelled layout takes several seconds.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"layout ran {clock.Elapsed} past its deadline");
    }

    [Fact]
    public void TheLargeStackLayoutThreadObservesTheCallersScope()
    {
        // Deeper than StackGuard.DeepTreeThreshold, so layout runs on its own thread.
        var html = new StringBuilder("<!doctype html><html><body>");
        for (int i = 0; i < StackGuard.DeepTreeThreshold + 50; i++)
        {
            html.Append("<div>");
        }

        html.Append("x</body></html>");
        DomTree tree = HtmlParsing.ParseHtml(html.ToString());
        Assert.True(StackGuard.IsDeeperThan(tree, StackGuard.DeepTreeThreshold));

        using (WorkCancellation.Enter(Cancelled()))
        {
            Assert.Throws<OperationCanceledException>(() =>
                RenderPaint.PrepareDom(tree, (800f, 600f), null, new RenderResourceCache()));
        }

        Assert.NotNull(RenderPaint.PrepareDom(tree, (800f, 600f), null, new RenderResourceCache()));
    }

    [Fact]
    public void AnUncancelledPassIsUnaffected()
    {
        DomTree tree = HtmlParsing.ParseHtml("<html><body><p>hello</p></body></html>");
        using var live = new CancellationTokenSource();
        using (WorkCancellation.Enter(live.Token))
        {
            Assert.NotNull(RenderPaint.PaintDom(tree, (200f, 100f), null));
        }
    }

    [Fact]
    public void ACancelledPrintEconomyPaintRestoresTheRetainedStyles()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            """
            <html style="margin:0"><body style="margin:0">
                <div style="width:100px;height:100px;background:#ff0000;color:#ffffff">X</div>
            </body></html>
            """);
        var resources = new RenderResourceCache();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (120f, 120f), null, resources)!;
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        CaptureRegion region = CaptureRegion.New(0f, 0f, 120f, 120f, 1f);
        byte[] before = RenderPaint.ScreenshotPreparedRegionWithScrollAndBackgrounds(
            tree, prepared, resources, scroll, region, true).Png!;

        // The economy capture clears backgrounds on the retained styles before it paints;
        // cancelled half-way, it must still put them back.
        using (WorkCancellation.Enter(Cancelled()))
        {
            Assert.Throws<OperationCanceledException>(() =>
                RenderPaint.ScreenshotPreparedRegionWithScrollAndBackgrounds(
                    tree, prepared, resources, scroll, region, false));
        }

        byte[] after = RenderPaint.ScreenshotPreparedRegionWithScrollAndBackgrounds(
            tree, prepared, resources, scroll, region, true).Png!;
        Assert.Equal(before, after);
    }
}
