using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// A URL change the document survived is <c>Page.navigatedWithinDocument</c>, never
/// <c>Page.frameNavigated</c>.
/// </summary>
/// <remarks>
/// <c>Page.frameNavigated</c> means a new document in CDP, and every client retires the
/// frame's execution contexts when it arrives. Reporting a <c>history.pushState</c> /
/// <c>replaceState</c> that way made the client's next <c>Runtime.evaluate</c> fail with
/// "Execution context was destroyed", which cost 20 of 157 routes when driving a single page
/// app over CDP. There is no Rust counterpart: <c>crates/obscura-cdp</c> has the same gap.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class SameDocumentNavigationEvents
{
    private const string Session = "session-1";

    private static long DefaultContextId(CdpContext ctx)
    {
        CdpEvent created = ctx.PendingEvents
            .Where(e => e.Method == "Runtime.executionContextCreated"
                && e.SessionId == Session
                && e.Params.Get("context").Get("auxData").Get("isDefault").AsBool() == true)
            .LastOrDefault()
            ?? throw new InvalidOperationException("missing default context");

        return created.Params.Get("context").Get("id").AsI64()!.Value;
    }

    /// <summary>A loaded page with Runtime enabled, plus its default execution context id.</summary>
    private static async Task<long> LoadAsync(CdpContext ctx, string url)
    {
        string pageId = ctx.CreatePage();
        ctx.Sessions[Session] = pageId;
        await CoreCdp.CdpAsync(ctx, 1, "Runtime.enable", new JsonObject(), Session);
        await CoreCdp.CdpAsync(
            ctx,
            2,
            "Page.navigate",
            new JsonObject { ["url"] = url, ["waitUntil"] = "load" },
            Session);

        return DefaultContextId(ctx);
    }

    private static CdpEvent SoleSameDocumentEvent(CdpContext ctx)
    {
        List<string> methods = [.. ctx.PendingEvents.Select(e => e.Method)];
        Assert.DoesNotContain("Page.frameNavigated", methods);
        Assert.DoesNotContain("Runtime.executionContextsCleared", methods);
        Assert.DoesNotContain("Page.loadEventFired", methods);

        return Assert.Single(ctx.PendingEvents, e => e.Method == "Page.navigatedWithinDocument");
    }

    [Fact]
    public async Task FragmentOnlyReplaceStateIsReportedWithinTheDocument()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Html("<html><body><p>spa</p></body></html>");
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        long contextId = await LoadAsync(ctx, server.Url);
        ctx.PendingEvents.Clear();

        await CoreCdp.EvalAsync(
            ctx, 3, "history.replaceState(null, '', '#/manage/ai')", Session);

        CdpEvent moved = SoleSameDocumentEvent(ctx);
        Assert.Equal(server.Url + "#/manage/ai", moved.Params.Get("url").AsString());
        Assert.Equal("fragment", moved.Params.Get("navigationType").AsString());
        Assert.False(string.IsNullOrEmpty(moved.Params.Get("frameId").AsString()));

        // The whole point of the event: the context the client is holding is still live.
        CdpResponse after = await CoreCdp.DispatchAsync(
            ctx,
            4,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = "location.hash",
                ["returnByValue"] = true,
                ["contextId"] = contextId,
            },
            Session);
        Assert.True(after.Error is null, $"context was destroyed: {after.Error?.Message}");
        Assert.Equal("#/manage/ai", after.Result!["result"]!["value"].AsString());
    }

    [Fact]
    public async Task PushStateToAnotherPathIsReportedAsAHistoryApiNavigation()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Html("<html><body><p>spa</p></body></html>");
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        await LoadAsync(ctx, server.Url);
        ctx.PendingEvents.Clear();

        await CoreCdp.EvalAsync(ctx, 3, "history.pushState(null, '', '/manage/ai')", Session);

        CdpEvent moved = SoleSameDocumentEvent(ctx);
        Assert.Equal(server.Url + "manage/ai", moved.Params.Get("url").AsString());
        Assert.Equal("historyApi", moved.Params.Get("navigationType").AsString());

        // A client that tracks the page url from TargetInfo alone still has to see it move.
        CdpEvent targetInfo = Assert.Single(
            ctx.PendingEvents, e => e.Method == "Target.targetInfoChanged");
        Assert.Equal(
            server.Url + "manage/ai",
            targetInfo.Params.Get("targetInfo").Get("url").AsString());
        Assert.NotNull(targetInfo.Params.Get("targetInfo").Get("canAccessOpener").AsBool());

        // The route never asked for a document, so the fixture never served a second one.
        Assert.Single(server.Requests);
    }

    /// <summary>
    /// A <c>Page.navigate</c> that differs from the loaded document only in its fragment is
    /// a same-document navigation, not a load.
    /// </summary>
    /// <remarks>
    /// Measured against real Chromium: no document request, the realm and every
    /// <c>window</c> property intact, <c>hashchange</c> and <c>popstate</c> fired.
    /// Refetching instead reboots a single page app on every route change, which is what a
    /// client driving one with <c>goto(base + '#/route')</c> asks for on every step.
    /// </remarks>
    [Fact]
    public async Task FragmentOnlyPageNavigateKeepsTheDocument()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Html("<html><body><p>spa</p></body></html>");
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        long contextId = await LoadAsync(ctx, server.Url);
        await CoreCdp.EvalAsync(
            ctx,
            3,
            "globalThis.__marker = 'ALIVE'; globalThis.__hc = 0; globalThis.__ps = 0;"
            + " addEventListener('hashchange', function () { __hc++; });"
            + " addEventListener('popstate', function () { __ps++; }); 1",
            Session);
        ctx.PendingEvents.Clear();

        JsonNode navigate = await CoreCdp.CdpAsync(
            ctx,
            4,
            "Page.navigate",
            new JsonObject { ["url"] = server.Url + "#/x", ["waitUntil"] = "load" },
            Session);

        // No loaderId: that field is the id of the document the navigation created, and
        // nothing was created. Playwright reads it as newDocumentId and, when it is there,
        // waits for a load lifecycle that never comes -- goto() then hangs to its timeout.
        Assert.False(string.IsNullOrEmpty(navigate.Get("frameId").AsString()));
        Assert.Null(navigate.Get("loaderId").AsString());

        CdpEvent moved = SoleSameDocumentEvent(ctx);
        Assert.Equal(server.Url + "#/x", moved.Params.Get("url").AsString());
        Assert.Equal("fragment", moved.Params.Get("navigationType").AsString());

        // The route never asked for a document, so the fixture never served a second one.
        Assert.Single(server.Requests);

        // The realm survived, so the client's context is still live and so is its state.
        CdpResponse after = await CoreCdp.DispatchAsync(
            ctx,
            5,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = "[__marker, __hc, __ps, location.href]",
                ["returnByValue"] = true,
                ["contextId"] = contextId,
            },
            Session);
        Assert.True(after.Error is null, $"context was destroyed: {after.Error?.Message}");
        Assert.Equal(
            $"""["ALIVE",1,1,"{server.Url}#/x"]""",
            after.Result!["result"]!["value"]!.ToJsonString());
    }

    /// <summary>
    /// <c>Page.reload</c> is handed the current URL and means the document literally, so a
    /// page sitting on a fragment must still be refetched.
    /// </summary>
    [Fact]
    public async Task ReloadStillFetchesWhenTheUrlCarriesAFragment()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Html("<html><body><p>spa</p></body></html>");
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        await LoadAsync(ctx, server.Url + "#/x");
        ctx.PendingEvents.Clear();

        await CoreCdp.CdpAsync(ctx, 4, "Page.reload", new JsonObject(), Session);

        Assert.Contains("Page.frameNavigated", ctx.PendingEvents.Select(e => e.Method));
        Assert.Equal(2, server.Requests.Count);
    }

    /// <summary>
    /// A real input-driven click on an in-page link navigates, the same way
    /// <c>element.click()</c> does.
    /// </summary>
    /// <remarks>
    /// The CDP mouse path carried its own copy of the "skip a fragment href" rule that the
    /// <c>bootstrap.js</c> click path had, so an actual mouse click on an SPA's own link --
    /// how most of them route -- did nothing at all.
    /// </remarks>
    [Fact]
    public async Task AnInputDrivenClickOnAFragmentLinkNavigatesWithinTheDocument()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Html(
            """
            <html><body style="margin:0">
            <a id="lnk" href="#/x"
               style="position:absolute;left:0;top:0;width:200px;height:60px;display:block">go</a>
            </body></html>
            """);
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        await LoadAsync(ctx, server.Url);
        await CoreCdp.EvalAsync(
            ctx,
            3,
            "globalThis.__hc = 0; globalThis.__ps = 0;"
            + " addEventListener('hashchange', function () { __hc++; });"
            + " addEventListener('popstate', function () { __ps++; }); 1",
            Session);
        ctx.PendingEvents.Clear();

        foreach ((ulong id, string type, int buttons) in
            new (ulong, string, int)[] { (4, "mousePressed", 1), (5, "mouseReleased", 0) })
        {
            await CoreCdp.CdpAsync(
                ctx,
                id,
                "Input.dispatchMouseEvent",
                new JsonObject
                {
                    ["type"] = type,
                    ["x"] = 20,
                    ["y"] = 20,
                    ["button"] = "left",
                    ["buttons"] = buttons,
                    ["clickCount"] = 1,
                },
                Session);
        }

        JsonNode state = await CoreCdp.EvalAsync(
            ctx, 6, "JSON.stringify([location.href, __hc, __ps])", Session);
        Assert.Equal(
            $"""["{server.Url}#/x",1,1]""",
            CoreCdp.ParseStringified(state).ToJsonString());

        CdpEvent moved = SoleSameDocumentEvent(ctx);
        Assert.Equal(server.Url + "#/x", moved.Params.Get("url").AsString());
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ARealDocumentNavigationIsStillReportedAsAFrameNavigation()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Routed(
            path => (path == "/next" ? "<p>next</p>" : "<p>first</p>", "text/html", 200));
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        await LoadAsync(ctx, server.Url);
        ctx.PendingEvents.Clear();

        await CoreCdp.EvalAsync(ctx, 3, "location.href = '/next'", Session);

        List<string> methods = [.. ctx.PendingEvents.Select(e => e.Method)];
        Assert.Contains("Page.frameNavigated", methods);
        Assert.DoesNotContain("Page.navigatedWithinDocument", methods);
    }
}
