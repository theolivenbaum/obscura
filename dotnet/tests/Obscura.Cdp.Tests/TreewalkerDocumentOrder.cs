using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/treewalker_document_order.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class TreewalkerDocumentOrder
{
    private static async Task<(CdpContext Ctx, string Session, CoreCdpServer Server)> SetupAsync()
    {
        CoreCdpServer server = CoreCdpServer.Html("<html><body></body></html>");
        (CdpContext ctx, string session) =
            await CoreCdp.NavigateAsync(server.Url, "treewalker-session");
        return (ctx, session, server);
    }

    [Fact]
    public async Task NextNodeWalksTheWholeSubtreeInDocumentOrder()
    {
        (CdpContext ctx, string session, CoreCdpServer server) = await SetupAsync();
        using IDisposable owned = CoreCdp.Owned(ctx);
        using (server)
        {
            JsonNode evaluated = await CoreCdp.EvalAsync(
                ctx,
                2,
                """
                (() => {
                    document.body.innerHTML =
                      '<div id="r"><span></span><p>hi</p><section><a></a><b></b></section></div>';
                    const walker = document.createTreeWalker(
                      document.getElementById('r'), NodeFilter.SHOW_ELEMENT);
                    const seen = [];
                    let node;
                    while ((node = walker.nextNode())) seen.push(node.tagName);
                    return JSON.stringify(seen);
                })()
                """,
                session);
            JsonNode seen = CoreCdp.ParseStringified(evaluated);
            Assert.Equal(
                """["SPAN","P","SECTION","A","B"]""",
                CdpJson.Serialize(seen));
        }
    }

    [Fact]
    public async Task NextNodeKeepsSearchingAfterFilteredLeafNodes()
    {
        (CdpContext ctx, string session, CoreCdpServer server) = await SetupAsync();
        using IDisposable owned = CoreCdp.Owned(ctx);
        using (server)
        {
            JsonNode evaluated = await CoreCdp.EvalAsync(
                ctx,
                2,
                """
                (() => {
                    document.body.innerHTML =
                      '<div id="r"><p>one</p><p>two</p><p>three</p></div>';
                    const walker = document.createTreeWalker(
                      document.getElementById('r'), NodeFilter.SHOW_TEXT, {
                        acceptNode(node) {
                          return node.data === 'two'
                            ? NodeFilter.FILTER_REJECT
                            : NodeFilter.FILTER_ACCEPT;
                        }
                      });
                    const seen = [];
                    let node;
                    while ((node = walker.nextNode())) seen.push(node.data);
                    return JSON.stringify(seen);
                })()
                """,
                session);
            JsonNode seen = CoreCdp.ParseStringified(evaluated);
            Assert.Equal("""["one","three"]""", CdpJson.Serialize(seen));
        }
    }

    [Fact]
    public async Task NextNodeHandlesADeepAcceptedChildFastPath()
    {
        (CdpContext ctx, string session, CoreCdpServer server) = await SetupAsync();
        using IDisposable owned = CoreCdp.Owned(ctx);
        using (server)
        {
            JsonNode evaluated = await CoreCdp.EvalAsync(
                ctx,
                2,
                """
                (() => {
                    const root = document.createElement('div');
                    let parent = root;
                    for (let i = 0; i < 5000; i++) {
                      const child = document.createElement('span');
                      parent.appendChild(child);
                      parent = child;
                    }
                    document.body.appendChild(root);
                    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
                    let count = 0;
                    while (walker.nextNode()) count++;
                    return count;
                })()
                """,
                session);
            Assert.Equal(5000.0, evaluated["result"]!["value"].AsF64());
        }
    }
}
