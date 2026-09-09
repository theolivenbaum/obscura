using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/js_fetch_emits_network_events.rs</c>.
/// </summary>
/// <remarks>
/// Regression for issue #406: requests initiated by page JS (fetch/XHR/dynamic resource)
/// must emit <c>Network.requestWillBeSent</c> / <c>responseReceived</c> so Puppeteer's and
/// Playwright's <c>page.on('request'|'response')</c> observe them. Before the fix only the
/// static navigation subresources surfaced; a <c>fetch()</c> fired from the page produced
/// no CDP Network event, so clients captured zero XHR/JSON responses.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class JsFetchEmitsNetworkEvents
{
    private const string Document = """
        <html><head></head><body>
        <div id="r">stage1</div>
        <script>
        window.__done = new Promise(function (resolve) {
          fetch("/api/start.json")
            .then(function (r) { return r.json(); })
            .then(function (d) { document.getElementById("r").textContent = "got:" + d.value; resolve("ok"); })
            .catch(function (e) { resolve("err:" + e); });
        });
        </script>
        </body></html>
        """;

    /// <summary>
    /// Serves an HTML page that fetches <c>/api/start.json</c>, which redirects to the JSON
    /// response at <c>/api/data.json</c>.
    /// </summary>
    private static CoreCdpServer Serve() => CoreCdpServer.Advanced(path =>
        path.StartsWith("/api/start.json", StringComparison.Ordinal)
            ? new CoreCdpServer.Reply(string.Empty, "text/plain", 302, "/api/data.json")
            : path.StartsWith("/api/data.json", StringComparison.Ordinal)
                ? new CoreCdpServer.Reply("{\"value\":42}", "application/json", 200)
                : new CoreCdpServer.Reply(Document, "text/html", 200));

    /// <summary>
    /// Collect the request URLs from every <c>Network.requestWillBeSent</c> currently queued
    /// in <c>ctx.PendingEvents</c>, then clear the queue.
    /// </summary>
    private static List<string> DrainRequestUrls(CdpContext ctx)
    {
        List<string> urls = [.. ctx.PendingEvents
            .Where(e => e.Method == "Network.requestWillBeSent")
            .Select(e => e.Params.Get("request").Get("url").AsString())
            .Where(url => url is not null)
            .Select(url => url!)];
        ctx.PendingEvents.Clear();
        return urls;
    }

    /// <summary>The requestId that <c>Network.responseReceived</c> reported for a URL.</summary>
    private static string? ResponseRequestId(CdpContext ctx, string needle) =>
        ctx.PendingEvents
            .FirstOrDefault(e => e.Method == "Network.responseReceived"
                && (e.Params.Get("response").Get("url").AsString()?
                    .Contains(needle, StringComparison.Ordinal) ?? false))
            ?.Params.Get("requestId").AsString();

    [Fact]
    public async Task JsFetchEmitsNetworkRequestAndResponse()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;

        // An ordinary fetch() is not load-delaying in Chromium: `load` may fire while its
        // response is still pending. Ask for networkidle0 explicitly so this output-level
        // assertion observes the completed request without turning every load navigation
        // into an implicit global settle.
        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject { ["url"] = server.Url, ["waitUntil"] = "networkidle0" },
            sessionId);

        List<string> requestUrls = [.. ctx.PendingEvents
            .Where(e => e.Method == "Network.requestWillBeSent")
            .Select(e => e.Params.Get("request").Get("url").AsStringOr(string.Empty))];
        Assert.Contains(requestUrls, url => url.Contains("/api/data.json", StringComparison.Ordinal));

        string requestId = ResponseRequestId(ctx, "/api/data.json")
            ?? throw new InvalidOperationException(
                "fetch must emit Network.responseReceived with a requestId");
        JsonNode body = await CoreCdp.CdpAsync(
            ctx,
            2,
            "Network.getResponseBody",
            new JsonObject { ["requestId"] = requestId },
            sessionId);
        Assert.Equal("{\"value\":42}", body.Get("body").AsString());
    }

    /// <summary>
    /// A page that issues no script fetch must still emit exactly its document request,
    /// proving the #406 change adds nothing spurious.
    /// </summary>
    [Fact]
    public async Task NavigationWithoutScriptFetchIsUnaffected()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Html("<html><body>plain</body></html>");
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;

        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" },
            sessionId);

        List<string> urls = DrainRequestUrls(ctx);
        Assert.Contains(
            urls,
            url => string.Equals(url, server.Url, StringComparison.Ordinal)
                || url.StartsWith(server.Url, StringComparison.Ordinal));
        Assert.DoesNotContain(urls, url => url.Contains("/api/", StringComparison.Ordinal));
    }
}
