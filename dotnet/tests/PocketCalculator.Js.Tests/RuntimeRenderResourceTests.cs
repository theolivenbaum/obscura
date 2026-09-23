using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The render-resource transport tests upstream 97ff86d added to
/// <c>crates/obscura-js/src/runtime.rs</c>, kept apart from <see cref="RuntimeTests"/>
/// only because that file is already very large.
/// </summary>
public sealed class RuntimeRenderResourceTests
{
    private static PocketCalculatorHttpClient Transport() =>
        new(new CookieJar(), null, allowPrivateNetwork: false);

    [Fact]
    public void PageTransportKeepsRenderResourcesCacheOnlyAcrossDocumentResets()
    {
        using var standalone = new PocketCalculatorJsRuntime();
        Assert.True(
            standalone.RenderResourceSyncLoadingEnabled,
            "standalone render runtimes keep the compatibility loader");
        standalone.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        Assert.True(standalone.RenderResourceSyncLoadingEnabled);

        using var rt = new PocketCalculatorJsRuntime();
        rt.SetHttpClient(Transport());
        Assert.False(
            rt.RenderResourceSyncLoadingEnabled,
            "installing a page transport must switch the cache to cache-only");
        rt.SetDom(HtmlParsing.ParseHtml("<html><body><img src=\"https://example.test/a.png\"></body></html>"));
        Assert.False(rt.RenderResourceSyncLoadingEnabled, "SetDom rebuilds the cache and must keep it cache-only");
        Assert.NotNull(rt.TakeDom());
        Assert.False(rt.RenderResourceSyncLoadingEnabled, "TakeDom rebuilds the cache and must keep it cache-only");
    }

    [Fact]
    public void RendererResourceTransportPreservesFileSubresourcePolicy()
    {
        const string File = "file:///tmp/obscura-render-resource.svg";
        Assert.True(PocketCalculatorJsRuntime.PageRenderResourceUrlAllowed("file:///tmp/obscura-page.html", File));
        Assert.False(PocketCalculatorJsRuntime.PageRenderResourceUrlAllowed("https://example.test/page", File));
        Assert.True(PocketCalculatorJsRuntime.PageRenderResourceUrlAllowed(
            "https://example.test/page", "https://assets.test/image.svg"));
        Assert.False(PocketCalculatorJsRuntime.PageRenderResourceUrlAllowed(
            "https://example.test/page", "blob:https://example.test/1"));
    }

    [Fact]
    public void RenderResourceInFlightSetHasAPageWideBound()
    {
        using var rt = new PocketCalculatorJsRuntime();
        List<RenderResourceMiss> requests = [];
        for (int index = 0; index < RenderResourceLoads.MaxPending + 4; index++)
        {
            requests.Add(new RenderResourceMiss($"https://assets.test/{index}.png", null, false));
        }

        Assert.Equal(RenderResourceLoads.MaxPending, rt.MarkRenderResourcesInFlight(requests).Count);
        Assert.Empty(rt.MarkRenderResourcesInFlight(
            [new RenderResourceMiss("https://assets.test/extra.png", null, false)]));
    }

    /// <summary>
    /// A load that answers after the document was retired must neither seed the
    /// surviving runtime nor clear a same-URL request of the document that replaced it.
    /// </summary>
    [Fact]
    public void LateChannelAnswerOfARetiredDocumentIsDiscarded()
    {
        const string Url = "https://example.test/a.svg";
        using var rt = new PocketCalculatorJsRuntime();
        rt.SetHttpClient(Transport());
        rt.SetDom(HtmlParsing.ParseHtml($"<html><body><img src=\"{Url}\"></body></html>"));
        rt.SetUrl("https://example.test/page");
        byte[] body = """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"></svg>"""u8.ToArray();
        RenderResourceMiss request = new(Url, null, false);
        RenderResourceLoad Answer(ulong generation) => new(
            generation,
            request,
            new Response
            {
                Status = 200,
                Url = new Uri(Url),
                Headers = new Dictionary<string, string>(),
                Body = body,
                RedirectedFrom = [],
            },
            0,
            0);

        // The old document's load holds the result set it was started with.
        RenderResourceLoads oldLoads = rt.State.RenderResourceLoads;
        ulong generation = rt.State.DocumentGeneration;
        rt.MarkRenderResourcesInFlight([request]);
        rt.AbandonRenderResources();
        Assert.False(rt.HasPendingRenderResources);
        oldLoads.Deliver(Answer(generation));
        Assert.Equal(0, rt.ApplyRenderResourceResults());
        Assert.False(rt.RenderResourceIsKnown(Url), "late bytes must not seed the runtime");

        // The same runtime requests the URL again; a stale-generation answer that
        // reaches the live set is fenced too.
        Assert.Single(rt.MarkRenderResourcesInFlight([request]));
        RenderResourceLoads live = rt.State.RenderResourceLoads;
        live.Deliver(Answer(generation - 1));
        Assert.Equal(0, rt.ApplyRenderResourceResults());
        Assert.True(rt.HasPendingRenderResources, "stale answer must not clear the live request");
        Assert.False(rt.RenderResourceIsKnown(Url));
        live.Deliver(Answer(generation));
        Assert.Equal(1, rt.ApplyRenderResourceResults());
        Assert.False(rt.HasPendingRenderResources);
        Assert.True(rt.RenderResourceIsKnown(Url));
    }

    /// <summary>
    /// The review's probe: a script on a <c>file:</c> page inserts an image from a
    /// loopback server and reads its geometry. Layout must not fetch it, and without the
    /// private-network opt-in the page transport must refuse it too.
    /// </summary>
    [Fact]
    public async Task LayoutNeverFetchesLoopbackWithoutThePrivateNetworkOptIn()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Task<System.Net.Sockets.Socket> accept = listener.AcceptSocketAsync();

        string? previous = Environment.GetEnvironmentVariable("POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK");
        Environment.SetEnvironmentVariable("POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK", null);
        try
        {
            using var rt = new PocketCalculatorJsRuntime();
            rt.SetHttpClient(Transport());
            rt.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
            rt.SetUrl("file:///tmp/probe.html");
            rt.RunPageInit();
            var width = rt.Evaluate(
                $$"""
                (() => {
                    const image = document.createElement('img');
                    image.src = 'http://127.0.0.1:{{port}}/late.svg';
                    document.body.appendChild(image);
                    return image.getBoundingClientRect().width;
                })()
                """);
            Assert.Equal(0.0, width!.GetValue<double>());
            // The next task hands the miss to the transport, which refuses loopback.
            rt.Evaluate("1");
            for (int i = 0; i < 100 && rt.HasPendingRenderResources; i++)
            {
                await Task.Delay(10);
                rt.ApplyRenderResourceResults();
            }

            Assert.False(rt.HasPendingRenderResources);
            Assert.True(rt.RenderImageResourceIsKnown(
                $"http://127.0.0.1:{port}/late.svg", ImageRequestProfile.NoCorsInclude));
            Task winner = await Task.WhenAny(accept, Task.Delay(200));
            Assert.NotSame(accept, winner);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK", previous);
            listener.Stop();
        }
    }
}
