using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/window_conformance_parity.rs</c>.
/// </summary>
/// <remarks>
/// Conformance parity: a handful of standard Web APIs that real Chrome exposes but
/// <c>bootstrap.js</c> left undefined, so vendor JS that feature-detects them dies with
/// "X is not a function". These are spec surface gaps, not anti-bot work.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class WindowConformanceParity
{
    private const string Body = "<html><body><div id=a></div></body></html>";

    /// <summary>
    /// <c>Element.prototype.toggleAttribute</c> is on every real Chrome. Bootstrap left it
    /// undefined, so any framework that calls <c>el.toggleAttribute()</c> (Lit, Stencil,
    /// several ad SDKs) throws.
    /// </summary>
    [Fact]
    public async Task ElementToggleAttributeIsCallableAndToggles()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        using IDisposable owned = CoreCdp.Owned(ctx);
        JsonNode evaluated = await CoreCdp.EvalAsync(
            ctx,
            2,
            """
            (function () {
                const el = document.getElementById('a');
                const t = typeof el.toggleAttribute;
                const first = el.toggleAttribute('hidden');
                const afterFirst = el.hasAttribute('hidden');
                const second = el.toggleAttribute('hidden');
                const afterSecond = el.hasAttribute('hidden');
                return JSON.stringify({ type: t, first, afterFirst, second, afterSecond });
            })()
            """,
            session,
            awaitPromise: true);
        JsonNode value = CoreCdp.ParseStringified(evaluated);
        Assert.Equal("function", value["type"].AsString());
        Assert.True(value["first"].AsBool(), "first toggle should add the attribute");
        Assert.True(value["afterFirst"].AsBool());
        Assert.False(value["second"].AsBool(), "second toggle should remove it");
        Assert.False(value["afterSecond"].AsBool());
    }

    /// <summary>
    /// <c>document.adoptNode</c> is standard DOM; bootstrap left it undefined. Frameworks
    /// that move nodes between documents (iframes, portals) call it.
    /// </summary>
    [Fact]
    public async Task DocumentAdoptNodeMovesNode()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        using IDisposable owned2 = CoreCdp.Owned(ctx);
        JsonNode evaluated = await CoreCdp.EvalAsync(
            ctx,
            2,
            """
            (function () {
                const t = typeof document.adoptNode;
                const span = document.createElement('span');
                span.id = 'moved';
                const adopted = document.adoptNode(span);
                return JSON.stringify({
                    type: t,
                    sameNode: adopted === span,
                    ownerDoc: adopted.ownerDocument === document,
                });
            })()
            """,
            session,
            awaitPromise: true);
        JsonNode value = CoreCdp.ParseStringified(evaluated);
        Assert.Equal("function", value["type"].AsString());
        Assert.True(value["sameNode"].AsBool());
        Assert.True(value["ownerDoc"].AsBool());
    }
}
