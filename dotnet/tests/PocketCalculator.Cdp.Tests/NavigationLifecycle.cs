using System.Text.Json.Nodes;

using PocketCalculator.Browser;

using Xunit;

using BrowserPage = PocketCalculator.Browser.Page;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// What a CDP client is told about a navigation that redirects, one whose deadline passes
/// after the document committed, and a document reopened with <c>document.open()</c>.
/// Expected values are Chromium's, measured with the Chromium build Playwright 1.56 ships.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class NavigationLifecycle
{
    private static (CdpContext Ctx, string Session, IDisposable Owned) NewPage()
    {
        CoreCdp.AllowLoopback();
        var ctx = CdpContext.New();
        IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string session = "session-1";
        ctx.Sessions[session] = pageId;
        return (ctx, session, owned);
    }

    private static List<CdpEvent> Events(CdpContext ctx, string method) =>
        [.. ctx.PendingEvents.Where(e => e.Method == method)];

    private static List<string> LifecycleNames(CdpContext ctx) =>
        [.. Events(ctx, "Page.lifecycleEvent").Select(e => e.Params!["name"].AsString()!)];

    /// <summary>
    /// Chromium reports a redirected navigation under one request id, the loader id: a
    /// <c>Network.requestWillBeSent</c> per hop, each after the first carrying the
    /// response that redirected it, then the final response. Playwright resolves
    /// <c>page.goto()</c> to that final response, with <c>redirectedFrom()</c> walking the
    /// hops. The port matched the document by its pre-redirect URL, so no request carried
    /// the loader id and <c>goto()</c> returned null after any redirect.
    /// </summary>
    [Fact]
    public async Task RedirectedNavigationReportsEachHopUnderTheLoaderId()
    {
        using CoreCdpServer server = CoreCdpServer.Advanced(path => path switch
        {
            "/r1" => new CoreCdpServer.Reply(string.Empty, "text/html", 301, Location: "/r2"),
            "/r2" => new CoreCdpServer.Reply(string.Empty, "text/html", 302, Location: "/final"),
            _ => new CoreCdpServer.Reply("<title>Final</title><p>final page</p>", "text/html; charset=utf-8", 200),
        });
        (CdpContext ctx, string session, IDisposable owned) = NewPage();
        using IDisposable _ = owned;
        string origin = server.Url.TrimEnd('/');

        JsonNode navigated = await CoreCdp.CdpAsync(
            ctx, 1, "Page.navigate", new JsonObject { ["url"] = origin + "/r1" }, session);
        string loaderId = navigated["loaderId"].AsString()!;

        List<JsonNode> requests = [.. Events(ctx, "Network.requestWillBeSent")
            .Select(e => e.Params!)
            .Where(p => p["type"].AsString() == "Document")];
        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal(loaderId, r["requestId"].AsString()));
        Assert.Equal(
            new[] { origin + "/r1", origin + "/r2", origin + "/final" },
            requests.Select(r => r["request"]!["url"].AsString()!).ToArray());
        Assert.Null(requests[0]["redirectResponse"]);
        Assert.Equal(origin + "/r1", requests[1]["redirectResponse"]!["url"].AsString());
        Assert.Equal(301L, requests[1]["redirectResponse"]!["status"].AsI64());
        Assert.Equal("/r2", requests[1]["redirectResponse"]!["headers"]!["location"].AsString());
        Assert.Equal(origin + "/r2", requests[2]["redirectResponse"]!["url"].AsString());
        Assert.Equal(302L, requests[2]["redirectResponse"]!["status"].AsI64());

        JsonNode response = Assert.Single(
            Events(ctx, "Network.responseReceived"),
            e => e.Params!["requestId"].AsString() == loaderId).Params!["response"]!;
        Assert.Equal(origin + "/final", response["url"].AsString());
        Assert.Equal(200L, response["status"].AsI64());
        Assert.Single(Events(ctx, "Network.loadingFinished"), e => e.Params!["requestId"].AsString() == loaderId);

        // The request for the document precedes the frame's navigation (issue #190), and
        // the frame lands on the final URL.
        int lastRequest = ctx.PendingEvents.FindLastIndex(e =>
            e.Method == "Network.requestWillBeSent" && e.Params!["requestId"].AsString() == loaderId);
        int frameNavigated = ctx.PendingEvents.FindIndex(e => e.Method == "Page.frameNavigated");
        Assert.True(lastRequest < frameNavigated, "the document's requests must precede Page.frameNavigated");
        Assert.Equal(origin + "/final", ctx.PendingEvents[frameNavigated].Params!["frame"]!["url"].AsString());

        // The body is still readable under the loader id.
        JsonNode body = await CoreCdp.CdpAsync(
            ctx, 2, "Network.getResponseBody", new JsonObject { ["requestId"] = loaderId }, session);
        Assert.Contains("final page", body["body"].AsString(), StringComparison.Ordinal);
    }

    /// <summary>An unredirected navigation keeps its single request, with no redirect fields.</summary>
    [Fact]
    public async Task PlainNavigationHasOneDocumentRequest()
    {
        using CoreCdpServer server = CoreCdpServer.Html("<title>Plain</title><p>plain</p>");
        (CdpContext ctx, string session, IDisposable owned) = NewPage();
        using IDisposable _ = owned;

        JsonNode navigated = await CoreCdp.CdpAsync(
            ctx, 1, "Page.navigate", new JsonObject { ["url"] = server.Url + "plain" }, session);
        string loaderId = navigated["loaderId"].AsString()!;

        JsonNode request = Assert.Single(
            Events(ctx, "Network.requestWillBeSent"),
            e => e.Params!["requestId"].AsString() == loaderId).Params!;
        Assert.Equal(server.Url + "plain", request["request"]!["url"].AsString());
        Assert.False(request.AsObject().ContainsKey("redirectResponse"));
        Assert.False(request.AsObject().ContainsKey("redirectHasExtraInfo"));
    }

    /// <summary>
    /// Chromium's <c>Page.navigate</c> answers once the navigation commits; a document whose
    /// load is late is still the page, and lifecycle events come when they happen. The port
    /// failed the whole navigation when its deadline passed, even with the DOM built. A
    /// document that has committed now keeps its DOM, <c>Page.navigate</c> succeeds, and the
    /// events stop where the document stopped, so the client's own waitUntil decides.
    /// </summary>
    [Fact]
    public async Task DeadlineAfterCommitLeavesThePageAsItStood()
    {
        using CoreCdpServer server = CoreCdpServer.Advanced(path => path switch
        {
            "/stall.js" => new CoreCdpServer.Reply("window.stalled = 1;", "text/javascript", 200, DelayMs: 10_000),
            _ => new CoreCdpServer.Reply(
                "<title>Slow</title><p id=a>before</p><script src=/stall.js></script><p id=b>after</p>",
                "text/html",
                200),
        });
        (CdpContext ctx, string session, IDisposable owned) = NewPage();
        using IDisposable _ = owned;
        BrowserPage page = ctx.GetSessionPageMut(session)!;
        page.SetNavigationTimeout(TimeSpan.FromMilliseconds(1_500));

        JsonNode navigated = await CoreCdp.CdpAsync(
            ctx, 1, "Page.navigate", new JsonObject { ["url"] = server.Url + "slow" }, session);
        Assert.False(string.IsNullOrEmpty(navigated["loaderId"].AsString()));
        Assert.True(page.LoadAbandoned);
        Assert.Equal(DocumentReadiness.Committed, page.Readiness);

        // Committed, and nothing past it: the stalled script never ran, so neither did the
        // document's DOMContentLoaded or load.
        List<string> lifecycle = LifecycleNames(ctx);
        Assert.Contains("commit", lifecycle);
        Assert.DoesNotContain("DOMContentLoaded", lifecycle);
        Assert.DoesNotContain("load", lifecycle);
        Assert.Empty(Events(ctx, "Page.loadEventFired"));
        Assert.Single(Events(ctx, "Page.frameNavigated"));

        JsonNode state = CoreCdp.ParseStringified(await CoreCdp.EvalAsync(
            ctx,
            2,
            "JSON.stringify([document.title, !!document.getElementById('a'), window.stalled === undefined, document.readyState])",
            session));
        Assert.Equal("Slow", state[0].AsString());
        Assert.True(state[1]!.GetValue<bool>());
        Assert.True(state[2]!.GetValue<bool>());
        Assert.Equal("loading", state[3].AsString());
    }

    /// <summary>A deadline before any response is still a failed navigation.</summary>
    [Fact]
    public async Task DeadlineBeforeCommitStillFails()
    {
        using CoreCdpServer server = CoreCdpServer.Advanced(
            _ => new CoreCdpServer.Reply("<p>late</p>", "text/html", 200, DelayMs: 10_000));
        (CdpContext ctx, string session, IDisposable owned) = NewPage();
        using IDisposable _ = owned;
        BrowserPage page = ctx.GetSessionPageMut(session)!;
        page.SetNavigationTimeout(TimeSpan.FromMilliseconds(1_000));

        CdpResponse response = await CoreCdp.DispatchAsync(
            ctx, 1, "Page.navigate", new JsonObject { ["url"] = server.Url + "late" }, session);
        Assert.Contains("navigation exceeded 1000ms deadline", response.Error?.Message, StringComparison.Ordinal);
        Assert.False(page.LoadAbandoned);
        Assert.Equal(LifecycleState.Failed, page.Lifecycle);
    }

    /// <summary>
    /// Playwright's <c>setContent</c> runs <c>document.open()</c>, a tagged
    /// <c>console.debug</c>, <c>write()</c> and <c>close()</c> in its utility world, then
    /// waits for the tag and for the frame's <c>load</c>. Chromium reports the console call
    /// with the world's context, and the reopened document's load as <c>init</c>,
    /// <c>DOMContentLoaded</c> and <c>load</c> under the current loader, with no
    /// <c>Page.frameNavigated</c>. The port reported neither, so setContent hung.
    /// </summary>
    [Fact]
    public async Task ReopenedDocumentReportsWorldConsoleAndLoadLifecycle()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        CdpResponse created = await CoreCdp.DispatchAsync(
            ctx, 1, "Target.createTarget", new JsonObject { ["url"] = "data:text/html,<p>orig</p>" }, null);
        string target = created.Result!["targetId"].AsString()!;
        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx, 2, "Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true }, null);
        string session = attached.Result!["sessionId"].AsString()!;
        await CoreCdp.CdpAsync(ctx, 3, "Runtime.enable", new JsonObject(), session);
        JsonNode frame = (await CoreCdp.CdpAsync(ctx, 4, "Page.getFrameTree", new JsonObject(), session))["frameTree"]!["frame"]!;
        string frameId = frame["id"].AsString()!;
        string loaderId = frame["loaderId"].AsString()!;
        long world = (await CoreCdp.CdpAsync(
            ctx,
            5,
            "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = frameId, ["worldName"] = "utility", ["grantUniveralAccess"] = true },
            session))["executionContextId"].AsI64()!.Value;
        ctx.PendingEvents.Clear();

        await CoreCdp.CdpAsync(
            ctx,
            6,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = "document.open(); console.debug('--tag--'); document.write('<p id=x>hello</p>'); document.close();",
                ["contextId"] = world,
            },
            session);

        CdpEvent console = Assert.Single(Events(ctx, "Runtime.consoleAPICalled"));
        Assert.Equal("debug", console.Params!["type"].AsString());
        Assert.Equal(world, console.Params!["executionContextId"].AsI64());
        Assert.Equal("--tag--", console.Params!["args"]![0]!["value"].AsString());

        // Every session on the page hears it: createTarget's and attachToTarget's.
        List<CdpEvent> lifecycle = [.. Events(ctx, "Page.lifecycleEvent").Where(e => e.SessionId == session)];
        Assert.Equal(
            new[] { "init", "DOMContentLoaded", "load", "networkIdle" },
            lifecycle.Select(e => e.Params!["name"].AsString()).ToArray());
        Assert.All(lifecycle, e =>
        {
            Assert.Equal(loaderId, e.Params!["loaderId"].AsString());
            Assert.Equal(frameId, e.Params!["frameId"].AsString());
        });
        Assert.Equal(8, Events(ctx, "Page.lifecycleEvent").Count);
        Assert.Single(Events(ctx, "Page.domContentEventFired"), e => e.SessionId == session);
        Assert.Single(Events(ctx, "Page.loadEventFired"), e => e.SessionId == session);
        Assert.Empty(Events(ctx, "Page.frameNavigated"));
        Assert.Empty(Events(ctx, "Runtime.executionContextsCleared"));

        // The tag precedes the load, which is the order Playwright waits in.
        Assert.True(
            ctx.PendingEvents.IndexOf(console)
                < ctx.PendingEvents.FindIndex(e => e.Method == "Page.lifecycleEvent"));

        JsonNode written = await CoreCdp.EvalAsync(ctx, 7, "document.getElementById('x').textContent", session);
        Assert.Equal("hello", written["result"]!["value"].AsString());
    }

    /// <summary>
    /// <c>document.open()</c> from a script while the document is still loading does not
    /// start a new load in Chromium, so its <c>close()</c> reports nothing beyond the
    /// navigation's own lifecycle.
    /// </summary>
    [Fact]
    public async Task DocumentCloseDuringLoadAddsNoLifecycle()
    {
        (CdpContext ctx, string session, IDisposable owned) = NewPage();
        using IDisposable _ = owned;

        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<p>x</p><script>document.open(); document.close();</script>",
            },
            session);
        await CoreCdp.EvalAsync(ctx, 2, "1", session);

        Assert.Single(LifecycleNames(ctx), name => name == "load");
    }
}
