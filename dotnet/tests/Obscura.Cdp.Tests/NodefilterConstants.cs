using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/nodefilter_constants.rs</c>.
/// </summary>
/// <remarks>
/// <c>NodeFilter</c> must expose the standard filter constants (issue #439).
/// <c>bootstrap.js</c> defined NodeFilter twice - a partial live one (SHOW_ELEMENT /
/// SHOW_TEXT / SHOW_ALL) and a complete one behind a dead <c>typeof === undefined</c>
/// guard - so <c>NodeFilter.FILTER_ACCEPT</c> was undefined at runtime and the canonical
/// <c>acceptNode() { return NodeFilter.FILTER_ACCEPT; }</c> idiom made the walker reject
/// every node.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class NodefilterConstants
{
    private const string Body =
        "<html><body><script>window.__boot = true;</script></body></html>";

    [Fact]
    public async Task NodeFilterExposesTheStandardConstants()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        using IDisposable owned = CoreCdp.Owned(ctx);
        JsonNode evaluated = await CoreCdp.EvalAsync(
            ctx,
            2,
            """
            JSON.stringify({
                accept: NodeFilter.FILTER_ACCEPT,
                reject: NodeFilter.FILTER_REJECT,
                skip: NodeFilter.FILTER_SKIP,
                showAll: NodeFilter.SHOW_ALL,
                showElement: NodeFilter.SHOW_ELEMENT,
                showText: NodeFilter.SHOW_TEXT,
                showComment: NodeFilter.SHOW_COMMENT,
            })
            """,
            session);
        JsonNode value = CoreCdp.ParseStringified(evaluated);
        Assert.Equal(1, value["accept"].AsI64());
        Assert.Equal(2, value["reject"].AsI64());
        Assert.Equal(3, value["skip"].AsI64());
        Assert.Equal(0xFFFFFFFFL, value["showAll"].AsI64());
        Assert.Equal(1, value["showElement"].AsI64());
        Assert.Equal(4, value["showText"].AsI64());
        Assert.Equal(128, value["showComment"].AsI64());
    }

    /// <summary>
    /// The canonical MDN idiom: acceptNode returns NodeFilter.FILTER_ACCEPT. Only the first
    /// accepted child is checked, so this does not depend on the separate nextNode
    /// leaf-advance fix (#432).
    /// </summary>
    [Fact]
    public async Task TreeWalkerFilterUsingFilterAcceptConstantWorks()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        using IDisposable owned2 = CoreCdp.Owned(ctx);
        JsonNode evaluated = await CoreCdp.EvalAsync(
            ctx,
            2,
            """
            (() => {
                document.body.innerHTML = '<div id="r"><p id="target">keep</p></div>';
                const r = document.getElementById('r');
                const w = document.createTreeWalker(r, NodeFilter.SHOW_ELEMENT, {
                    acceptNode() { return NodeFilter.FILTER_ACCEPT; }
                });
                const first = w.nextNode();
                return JSON.stringify({ id: first ? first.id : null });
            })()
            """,
            session);
        JsonNode value = CoreCdp.ParseStringified(evaluated);
        Assert.Equal("target", value["id"].AsString());
    }
}
