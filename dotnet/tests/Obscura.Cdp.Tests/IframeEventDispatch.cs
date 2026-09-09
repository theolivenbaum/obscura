using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/iframe_event_dispatch.rs</c>.
/// </summary>
/// <remarks>
/// <c>_IframeDocument</c>'s addEventListener/removeEventListener/dispatchEvent were no-ops,
/// so listeners registered on an iframe document never ran. Separately, iframe load invoked
/// the <c>onload</c> property directly instead of dispatching through the element, which
/// skipped any <c>addEventListener('load', ...)</c> listener.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class IframeEventDispatch
{
    private const string Body = "<html><body><div id=a></div></body></html>";

    /// <summary>
    /// Accessing contentDocument on a src-less iframe lazily creates an about:blank
    /// <c>_IframeDocument</c> synchronously, so this exercises the event target without
    /// waiting on an async load.
    /// </summary>
    [Fact]
    public async Task IframeDocumentDispatchesRegisteredListeners()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        JsonNode evaluated = await CoreCdp.EvalAsync(
            ctx,
            2,
            """
            (function () {
                const iframe = document.createElement('iframe');
                document.body.appendChild(iframe);
                const doc = iframe.contentDocument;
                const out = { hasDoc: !!doc };
                let calls = 0;
                const listener = () => { calls++; };
                doc.addEventListener('probe', listener);
                doc.dispatchEvent(new Event('probe'));
                out.afterRegister = calls;
                doc.addEventListener('probe', listener);
                doc.addEventListener('probe', listener);
                doc.dispatchEvent(new Event('probe'));
                out.afterDuplicate = calls;
                doc.removeEventListener('probe', listener);
                doc.dispatchEvent(new Event('probe'));
                out.afterRemove = calls;
                doc.addEventListener('cancelme', (e) => e.preventDefault());
                out.cancelReturn = doc.dispatchEvent(new Event('cancelme', { cancelable: true }));
                out.plainReturn = doc.dispatchEvent(new Event('nolisteners'));
                return JSON.stringify(out);
            })()
            """,
            session,
            awaitPromise: true);
        JsonNode value = CoreCdp.ParseStringified(evaluated);
        Assert.True(value["hasDoc"].AsBool());
        Assert.Equal(1UL, value["afterRegister"].AsU64());
        Assert.Equal(2UL, value["afterDuplicate"].AsU64());
        Assert.Equal(2UL, value["afterRemove"].AsU64());
        Assert.False(value["cancelReturn"].AsBool(), "preventDefault -> dispatchEvent returns false");
        Assert.True(value["plainReturn"].AsBool(), "no cancellation -> dispatchEvent returns true");
    }

    /// <summary>
    /// Both the onload property and an <c>addEventListener('load', ...)</c> listener must
    /// run exactly once. Before the fix, setting onload made the direct-call path skip the
    /// addEventListener listener entirely.
    /// </summary>
    [Fact]
    public async Task IframeLoadReachesOnloadAndAddeventlistener()
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        JsonNode evaluated = await CoreCdp.EvalAsync(
            ctx,
            2,
            """
            (function () {
                return new Promise((resolve) => {
                    const iframe = document.createElement('iframe');
                    const events = [];
                    iframe.onload = () => {
                        events.push('property');
                        Promise.resolve().then(() => resolve(JSON.stringify({ events })));
                    };
                    iframe.addEventListener('load', () => events.push('listener'));
                    document.body.appendChild(iframe);
                    iframe.src = location.href;
                });
            })()
            """,
            session,
            awaitPromise: true);
        JsonNode value = CoreCdp.ParseStringified(evaluated);
        Assert.Equal("""["property","listener"]""", CdpJson.Serialize(value["events"]));
    }
}
