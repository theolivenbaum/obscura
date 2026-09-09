using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/scroll_event_on_assignment.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Assigning <c>scrollTop</c> / <c>scrollLeft</c> must fire a <c>scroll</c> event.
/// <c>scrollTo</c>/<c>scrollBy</c> already dispatched one; direct assignment did not, so the
/// very common lazy-load idiom
/// (<c>el.addEventListener('scroll', loadMore); el.scrollTop = el.scrollHeight;</c>)
/// silently did nothing and scroll-driven feeds stalled after their first batch.
/// </para>
/// <para>
/// Checked against a real Chrome over CDP with this same probe: assigning <c>scrollTop</c>
/// fires exactly one <c>scroll</c> event when the element can actually scroll. The fixture
/// therefore has explicit viewport and content dimensions; Chromium clamps a non-overflowing
/// element to zero and dispatches no event.
/// </para>
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class ScrollEventOnAssignment
{
    private const string Body = """
        <html><head><style>
            #box { width: 100px; height: 100px; overflow: scroll; }
            #content { width: 1000px; height: 2000px; }
        </style></head><body><div id="box"><div id="content"><p>a</p><p>b</p></div></div></body></html>
        """;

    /// <summary>
    /// <c>awaitPromise</c> matters here: the scroll event is dispatched from a
    /// <c>setTimeout(..., 0)</c>, so the probe has to yield before reading the counter.
    /// </summary>
    private static async Task<JsonNode> ProbeAsync(CdpContext ctx, string session, string body)
    {
        string expression = $$"""
            (() => {
                const el = document.getElementById('box');
                let fired = 0;
                el.addEventListener('scroll', () => { fired++; });
                {{body}}
                return new Promise(r => setTimeout(() => r(JSON.stringify({
                    fired, top: el.scrollTop, left: el.scrollLeft,
                })), 0));
            })()
            """;
        JsonNode evaluated = await CoreCdp.EvalAsync(ctx, 2, expression, session, awaitPromise: true);
        return CoreCdp.ParseStringified(evaluated);
    }

    [Fact]
    public async Task AssigningScrollTopFiresOneScrollEvent()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        JsonNode result = await ProbeAsync(ctx, session, "el.scrollTop = 100;");
        Assert.Equal(1, result["fired"].AsI64());
        Assert.Equal(100, result["top"].AsI64());
    }

    [Fact]
    public async Task AssigningScrollLeftFiresOneScrollEvent()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        JsonNode result = await ProbeAsync(ctx, session, "el.scrollLeft = 40;");
        Assert.Equal(1, result["fired"].AsI64());
        Assert.Equal(40, result["left"].AsI64());
    }

    /// <summary>
    /// Re-assigning the same offset is not a scroll, so it must stay silent - otherwise a
    /// loader that writes <c>scrollTop</c> on every frame re-enters forever.
    /// </summary>
    [Fact]
    public async Task ReassigningTheSameOffsetIsSilent()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        JsonNode result = await ProbeAsync(
            ctx, session, "el.scrollTop = 100; el.scrollTop = 100;");
        Assert.Equal(1, result["fired"].AsI64());
    }

    /// <summary>
    /// <c>scrollTo</c> moves both axes, and a real browser reports one scroll per movement
    /// rather than one per axis. The setters suppress their own event inside
    /// <c>scrollTo</c>/<c>scrollBy</c> so the operation stays a single event.
    /// </summary>
    [Fact]
    public async Task ScrollToCoalescesBothAxesIntoOneEvent()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        JsonNode result = await ProbeAsync(ctx, session, "el.scrollTo(30, 60);");
        Assert.Equal(1, result["fired"].AsI64());
        Assert.Equal(60, result["top"].AsI64());
        Assert.Equal(30, result["left"].AsI64());
    }

    /// <summary>
    /// The lazy-load idiom the fix exists for: a listener that appends more rows when the
    /// feed is scrolled. Before the fix this counted 0 and feeds froze.
    /// </summary>
    [Fact]
    public async Task ScrollDrivenLazyLoaderAdvances()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        JsonNode evaluated = await CoreCdp.EvalAsync(
            ctx,
            2,
            """
            (() => {
                const el = document.getElementById('box');
                let batches = 0;
                el.addEventListener('scroll', () => {
                    if (batches >= 3) return;
                    batches++;
                    for (let i = 0; i < 5; i++) el.appendChild(document.createElement('p'));
                });
                el.scrollTop = 500;
                return new Promise(r => setTimeout(() => {
                    el.scrollTop = 1000;
                    setTimeout(() => r(JSON.stringify({
                        batches, rows: el.querySelectorAll('p').length,
                    })), 0);
                }, 0));
            })()
            """,
            session,
            awaitPromise: true);
        JsonNode result = CoreCdp.ParseStringified(evaluated);
        Assert.Equal(2, result["batches"].AsI64());
        Assert.Equal(12, result["rows"].AsI64());
    }
}
