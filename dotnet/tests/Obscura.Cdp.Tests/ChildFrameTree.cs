using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/child_frame_tree.rs</c>.
/// </summary>
/// <remarks>
/// Regression test for the protocol half of issue #600: <c>Page.getFrameTree</c> reported
/// <c>childFrames: []</c> however many frames a page had built, and no
/// <c>Page.frameAttached</c> was ever emitted, so a Playwright or Puppeteer client saw a
/// single-frame page and could never address the child.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class ChildFrameTree
{
    /// <summary>
    /// <c>/</c> embeds <c>/child.html</c>, which itself embeds <c>/grandchild.html</c>, so
    /// the tree is deep enough to show nesting rather than a flat list.
    /// </summary>
    private static CoreCdpServer Serve() => CoreCdpServer.Routed(path => path switch
    {
        "/child.html" => (
            "<html><body><iframe src=\"/grandchild.html\"></iframe></body></html>", "text/html", 200),
        "/grandchild.html" => ("<html><body><p>deep</p></body></html>", "text/html", 200),
        _ => ("<html><body><iframe src=\"/child.html\"></iframe></body></html>", "text/html", 200),
    });

    /// <summary>
    /// Pump page turns until the whole nested frame tree exists, or give up.
    /// </summary>
    /// <remarks>
    /// The Rust test does one <c>Runtime.evaluate</c> after the navigation, because frames
    /// are built when the page settles and one settle is enough there. The managed engine
    /// occasionally needs another turn before a grandchild frame's realm exists, so this
    /// pumps the same command a bounded number of times instead of asserting on the first
    /// read. It only ever waits; nothing about the assertions changes.
    /// </remarks>
    private static async Task<JsonNode> FrameTreeWithDepthAsync(
        CdpContext ctx,
        string session,
        int depth,
        CoreCdpServer? server = null)
    {
        JsonNode tree = new JsonObject();
        for (int attempt = 0; attempt < 40; attempt++)
        {
            tree = await CoreCdp.CdpAsync(ctx, 3, "Page.getFrameTree", new JsonObject(), session);
            JsonNode? node = tree["frameTree"];
            int found = 0;
            while (node.Get("childFrames").Get(0) is { } child)
            {
                found++;
                node = child;
            }

            if (found >= depth)
            {
                return tree;
            }

            await CoreCdp.CdpAsync(
                ctx, 2, "Runtime.evaluate", new JsonObject { ["expression"] = "1" }, session);
            if (ctx.GetSessionPageMut(session) is { } page)
            {
                await page.RunAutonomousEventLoopTurnAsync();
            }

            await Task.Delay(50);
        }

        Assert.Fail(
            "frame tree never reached depth " + depth + ": " + CdpJson.Serialize(tree)
            + " served=[" + string.Join("|", server?.Requests ?? []) + "]");
        return tree;
    }

    /// <summary>
    /// Gets a page and a session the way a real client does, rather than by inserting a
    /// session for a hand-made page: <c>Target.createTarget</c> then
    /// <c>Target.attachToTarget</c>, which is the only route Puppeteer and Playwright can
    /// take and therefore the only one worth asserting against.
    /// </summary>
    private static async Task<string> AttachedSessionAsync(CdpContext ctx)
    {
        CdpResponse created = await CoreCdp.DispatchAsync(
            ctx, 900, "Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null);
        string targetId = created.Result!["targetId"].AsString()!;

        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx,
            901,
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = targetId, ["flatten"] = true },
            null);
        return attached.Result!["sessionId"].AsString()!;
    }

    [Fact]
    public async Task GetFrameTreeReportsNestedChildFrames()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string session = await AttachedSessionAsync(ctx);

        // Deliberately no `waitUntil`: that is what Puppeteer and Playwright send, and it
        // resolves to DomContentLoaded rather than to load. Passing "load" here hid the
        // frame build behind a readiness level no real client asks for, so the tree came
        // back empty for every one of them.
        await CoreCdp.CdpAsync(
            ctx, 1, "Page.navigate", new JsonObject { ["url"] = server.Url }, session);
        // Frames are built when the page settles, which is not part of the navigation.
        await CoreCdp.CdpAsync(
            ctx, 2, "Runtime.evaluate", new JsonObject { ["expression"] = "1" }, session);

        JsonNode tree = await FrameTreeWithDepthAsync(ctx, session, 2, server);
        JsonNode? root = tree["frameTree"];
        JsonNode? child = root.Get("childFrames").Get(0);
        Assert.EndsWith(
            "/child.html",
            child.Get("frame").Get("url").AsStringOr(string.Empty),
            StringComparison.Ordinal);
        Assert.Equal(
            root.Get("frame").Get("id").AsString(),
            child.Get("frame").Get("parentId").AsString());

        JsonNode? grandchild = child.Get("childFrames").Get(0);
        Assert.EndsWith(
            "/grandchild.html",
            grandchild.Get("frame").Get("url").AsStringOr(string.Empty),
            StringComparison.Ordinal);
        Assert.Equal(
            child.Get("frame").Get("id").AsString(),
            grandchild.Get("frame").Get("parentId").AsString());

        // A client builds its frame list from the events, so the tree alone is not enough.
        // They have to carry the session the client attached with, because a client
        // discards anything addressed to a session it does not hold, and
        // Target.createTarget leaves a second session on the same page.
        string childId = child.Get("frame").Get("id").AsString()!;
        string pageId = ctx.Sessions[session];
        foreach ((string candidate, string owner) in ctx.Sessions)
        {
            if (!string.Equals(owner, pageId, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                ctx.PendingEvents.Any(e => e.Method == "Page.frameAttached"
                    && e.Params.Get("frameId").AsString() == childId
                    && e.SessionId == candidate),
                $"session {candidate} on the page was never told the child frame attached");
        }

        int attachedIndex = ctx.PendingEvents.FindIndex(e => e.Method == "Page.frameAttached"
            && e.Params.Get("frameId").AsString() == childId
            && e.SessionId == session);
        Assert.True(attachedIndex >= 0, "no Page.frameAttached for the child on the client's session");
        int navigatedIndex = ctx.PendingEvents.FindIndex(e => e.Method == "Page.frameNavigated"
            && e.Params.Get("frame").Get("id").AsString() == childId
            && e.SessionId == session);
        Assert.True(navigatedIndex >= 0, "no Page.frameNavigated for the child on the client's session");
        Assert.True(attachedIndex < navigatedIndex, "frameAttached must come before frameNavigated");

        // Each frame is announced once, however many commands the client sends.
        int before = ctx.PendingEvents.Count;
        await CoreCdp.CdpAsync(ctx, 4, "Page.getFrameTree", new JsonObject(), session);
        int repeats = ctx.PendingEvents.Skip(before).Count(e => e.Method == "Page.frameAttached");
        Assert.Equal(0, repeats);
    }

    [Fact]
    public async Task IsolatedWorldsAreOwnedByTheirExactFrame()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string session = await AttachedSessionAsync(ctx);

        await CoreCdp.CdpAsync(
            ctx, 1, "Page.navigate", new JsonObject { ["url"] = server.Url }, session);
        await CoreCdp.CdpAsync(
            ctx, 2, "Runtime.evaluate", new JsonObject { ["expression"] = "1" }, session);
        await FrameTreeWithDepthAsync(ctx, session, 2, server);
        string target = ctx.Sessions[session];
        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx,
            3,
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = target, ["flatten"] = true },
            null);
        string secondSession = attached.Result!["sessionId"].AsString()!;
        foreach (string runtimeSession in new[] { session, secondSession })
        {
            await CoreCdp.CdpAsync(ctx, 3, "Runtime.enable", new JsonObject(), runtimeSession);
        }

        JsonNode tree = await FrameTreeWithDepthAsync(ctx, session, 2, server);
        string mainId = tree["frameTree"].Get("frame").Get("id").AsString()!;
        string childId = tree["frameTree"].Get("childFrames").Get(0).Get("frame").Get("id").AsString()!;
        string grandchildId = tree["frameTree"].Get("childFrames").Get(0)
            .Get("childFrames").Get(0).Get("frame").Get("id").AsString()!;

        long mainWorld = (await CoreCdp.CdpAsync(
            ctx,
            5,
            "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = mainId, ["worldName"] = "utility" },
            session))["executionContextId"].AsI64()!.Value;
        long childWorld = (await CoreCdp.CdpAsync(
            ctx,
            6,
            "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = childId, ["worldName"] = "utility" },
            session))["executionContextId"].AsI64()!.Value;
        long grandchildWorld = (await CoreCdp.CdpAsync(
            ctx,
            7,
            "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = grandchildId, ["worldName"] = "utility" },
            session))["executionContextId"].AsI64()!.Value;
        Assert.NotEqual(mainWorld, childWorld);
        Assert.NotEqual(childWorld, grandchildWorld);

        CdpResponse unknown = await CoreCdp.DispatchAsync(
            ctx,
            8,
            "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = "missing-frame", ["worldName"] = "utility" },
            session);
        Assert.NotNull(unknown.Error);
        Assert.Contains("No frame with given id", unknown.Error!.Message, StringComparison.Ordinal);

        ctx.PendingEvents.Clear();
        await CoreCdp.CdpAsync(
            ctx,
            9,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "document.querySelector('iframe').remove()" },
            session);
        for (int i = 0; i < 3; i++)
        {
            await ctx.GetSessionPageMut(session)!.RunAutonomousEventLoopTurnAsync();
        }

        await CoreCdp.CdpAsync(
            ctx, 10, "Runtime.evaluate", new JsonObject { ["expression"] = "1" }, session);

        foreach ((string frameId, long contextId) in new[]
        {
            (childId, childWorld), (grandchildId, grandchildWorld),
        })
        {
            foreach (string runtimeSession in new[] { session, secondSession })
            {
                int destroyed = ctx.PendingEvents.FindIndex(e =>
                    e.Method == "Runtime.executionContextDestroyed"
                    && e.SessionId == runtimeSession
                    && e.Params.Get("executionContextId").AsI64() == contextId);
                Assert.True(
                    destroyed >= 0,
                    $"missing executionContextDestroyed for {frameId}/{contextId}");
                int detached = ctx.PendingEvents.FindIndex(e =>
                    e.Method == "Page.frameDetached"
                    && e.SessionId == runtimeSession
                    && e.Params.Get("frameId").AsString() == frameId);
                Assert.True(detached >= 0, "missing frameDetached");
                Assert.True(
                    destroyed < detached,
                    "context must be destroyed before its frame detaches");
            }
        }

        CdpResponse mainStillRoutes = await CoreCdp.DispatchAsync(
            ctx,
            11,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "1", ["contextId"] = mainWorld },
            session);
        Assert.Null(mainStillRoutes.Error);
        foreach ((ulong id, long staleContext) in new[]
        {
            (12UL, childWorld), (13UL, grandchildWorld),
        })
        {
            CdpResponse stale = await CoreCdp.DispatchAsync(
                ctx,
                id,
                "Runtime.evaluate",
                new JsonObject { ["expression"] = "1", ["contextId"] = staleContext },
                session);
            Assert.NotNull(stale.Error);
            Assert.Contains("Cannot find context", stale.Error!.Message, StringComparison.Ordinal);
        }

        ctx.PendingEvents.Clear();
        await CoreCdp.CdpAsync(
            ctx,
            14,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<p>replacement</p>",
                ["waitUntil"] = "load",
            },
            session);
        List<CdpEvent> recreated = [.. ctx.PendingEvents.Where(e =>
            e.Method == "Runtime.executionContextCreated"
            && e.SessionId == session
            && e.Params.Get("context").Get("name").AsString() == "utility")];
        Assert.Single(recreated);
        Assert.Equal(
            mainId,
            recreated[0].Params.Get("context").Get("auxData").Get("frameId").AsString());
    }
}
