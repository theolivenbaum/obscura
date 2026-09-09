using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>context_ownership_tests</c> module in
/// <c>crates/obscura-cdp/src/dispatch.rs</c>.
/// </summary>
public sealed class ContextOwnershipTests
{
    [Fact]
    public void RepeatedPageTeardownDoesNotGrowContextMaps()
    {
        var ctx = CdpContext.New();
        for (var cycle = 0; cycle < 64; cycle++)
        {
            var pageId = ctx.CreatePage();
            var sessionId = $"session-{cycle}";
            ctx.Sessions[sessionId] = pageId;
            Assert.NotNull(ctx.EnsureDefaultContext(pageId));
            ctx.CreateIsolatedContext(pageId, pageId, "about:blank", $"world-{cycle}", true);
            ctx.RemovePage(pageId);

            Assert.Equal(0, ctx.ExecutionContextCount);
            Assert.Equal(0, ctx.PageContextPageCount);
            Assert.Equal(0, ctx.PageIsolatedWorldPageCount);
            Assert.Empty(ctx.ValidContextIds);
            Assert.Empty(ctx.RuntimeEnabledSessions);
            Assert.Empty(ctx.Sessions);
        }
    }

    [Fact]
    public void IsolatedCompatibilityIdsAreNotClaimedAsDefaultRealmRoutes()
    {
        var ctx = CdpContext.New();
        var isolated = ctx.NextIsolatedContext();

        Assert.Contains(isolated, ctx.ValidContextIds);
        Assert.Null(ctx.ContextById(isolated));
    }

    [Fact]
    public void DefaultAndIsolatedAllocatorsDoNotCollidePastOneThousandIds()
    {
        var ctx = CdpContext.New();
        HashSet<long> ids = [];
        for (var index = 0; index < 1_200; index++)
        {
            var id = index % 2 == 0
                ? ctx.AllocateContext("page", "frame", "about:blank", string.Empty, true).Id
                : ctx.NextIsolatedContext();
            Assert.True(ids.Add(id), $"duplicate execution context id {id}");
        }

        Assert.Equal(1_200, ids.Count);
    }

    [Fact]
    public void RepeatedDocumentCommitsKeepOnlyLivePageContexts()
    {
        var ctx = CdpContext.New();
        var first = ctx.CreatePage();
        var second = ctx.CreatePage();
        Assert.NotNull(ctx.EnsureDefaultContext(second));
        var siblingId = ctx.DefaultContextId(second);
        Assert.NotNull(siblingId);

        for (var generation = 0; generation < 64; generation++)
        {
            var contexts = ctx.CommitDefaultContext(
                first, first, $"https://example.test/{generation}");
            Assert.Equal(2, contexts.Count);
            Assert.Equal(2, ctx.ContextIdsForPage(first).Count);
            Assert.Equal(3, ctx.ExecutionContextCount);
            Assert.Equal(siblingId, ctx.DefaultContextId(second));
        }
    }

    [Fact]
    public void DetachedFrameContextsArePrunedWithoutTouchingSiblings()
    {
        var ctx = CdpContext.New();
        var page = ctx.CreatePage();
        var main = ctx.EnsureDefaultContext(page);
        Assert.NotNull(main);
        var child = ctx.CreateIsolatedContext(
            page, "child-frame", "https://example.test/child", "utility", false).Context;
        var sibling = ctx.CreateIsolatedContext(
            page, "sibling-frame", "https://example.test/sibling", "utility", false).Context;

        var removed = ctx.RemoveFrameContexts(page, "child-frame");

        Assert.Equal([child.Id], removed.Select(context => context.Id));
        Assert.Null(ctx.ContextById(child.Id));
        Assert.NotNull(ctx.ContextById(main.Id));
        Assert.NotNull(ctx.ContextById(sibling.Id));
    }
}

/// <summary>
/// The xUnit port of the <c>tests</c> module in
/// <c>crates/obscura-cdp/src/dispatch.rs</c>.
/// </summary>
public sealed class DispatchTests
{
    private static CdpRequest Req(string method) => new()
    {
        Id = 1,
        Method = method,
        Params = new JsonObject(),
        SessionId = null,
    };

    [Fact]
    public async Task AuditsEnableReturnsEmptySuccess()
    {
        var ctx = CdpContext.New();
        var resp = await Dispatcher.DispatchAsync(Req("Audits.enable"), ctx);
        Assert.True(resp.Error is null, $"Audits.enable should not error: {resp.Error?.Message}");
        Assert.True(resp.HasResult);
        Assert.Equal("{}", CdpJson.Serialize(resp.Result));
    }

    [Fact]
    public async Task UnknownDomainStillErrors()
    {
        var ctx = CdpContext.New();
        var resp = await Dispatcher.DispatchAsync(Req("DefinitelyNotADomain.enable"), ctx);
        var err = resp.Error;
        Assert.NotNull(err);
        Assert.Equal(-32601, err.Code);
        Assert.Contains("Unknown domain", err.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMessageToTargetUnwrapsInnerCall()
    {
        var ctx = CdpContext.New();
        var inner = new JsonObject
        {
            ["id"] = 42,
            ["method"] = "Browser.getVersion",
            ["params"] = new JsonObject(),
        };
        var outer = new CdpRequest
        {
            Id = 99,
            Method = "Target.sendMessageToTarget",
            Params = new JsonObject
            {
                ["sessionId"] = "sess-1",
                ["message"] = CdpJson.Serialize(inner),
            },
            SessionId = null,
        };

        var resp = await Dispatcher.DispatchAsync(outer, ctx);
        Assert.True(resp.Error is null, $"wrapper must succeed: {resp.Error?.Message}");
        Assert.Equal(99UL, resp.Id);
        Assert.Equal("{}", CdpJson.Serialize(resp.Result));

        // headless_chrome reads the inner response from the
        // receivedMessageFromTarget event, not from the wrapper response.
        var evt = ctx.PendingEvents.FirstOrDefault(
            e => string.Equals(e.Method, "Target.receivedMessageFromTarget", StringComparison.Ordinal));
        Assert.NotNull(evt);
        Assert.Equal("sess-1", evt.Params.Get("sessionId").AsString());
        var innerMsg = evt.Params.Get("message").AsString();
        Assert.NotNull(innerMsg);
        var parsed = CdpJson.Parse(innerMsg);
        Assert.Equal(42UL, parsed.Get("id").AsU64());

        // Browser.getVersion returns a populated result object, not an error.
        Assert.NotNull(parsed.Get("result"));
        Assert.Null(parsed.Get("error"));
    }

    [Fact]
    public async Task SendMessageToTargetRejectsInvalidMessage()
    {
        var ctx = CdpContext.New();
        var outer = new CdpRequest
        {
            Id = 5,
            Method = "Target.sendMessageToTarget",
            Params = new JsonObject
            {
                ["sessionId"] = "sess-1",
                ["message"] = "{not valid json",
            },
            SessionId = null,
        };
        var resp = await Dispatcher.DispatchAsync(outer, ctx);
        var err = resp.Error;
        Assert.NotNull(err);
        Assert.Equal(-32700, err.Code);
    }
}
