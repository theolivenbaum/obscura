using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/execution_context_ownership.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class ExecutionContextOwnership
{
    private static async Task<(string Target, string Session)> CreateAndAttachAsync(
        CdpContext ctx,
        string url,
        ulong id)
    {
        CdpResponse created = await CoreCdp.DispatchAsync(
            ctx, id, "Target.createTarget", new JsonObject { ["url"] = url }, null);
        Assert.True(created.Error is null, $"createTarget failed: {created.Error?.Message}");
        string target = created.Result!["targetId"].AsString()!;
        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx,
            id + 1,
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = target, ["flatten"] = true },
            null);
        return (target, attached.Result!["sessionId"].AsString()!);
    }

    private static (long Id, string UniqueId, string Origin) LatestDefaultContext(
        CdpContext ctx,
        string session)
    {
        CdpEvent found = ctx.PendingEvents
            .Where(e => e.Method == "Runtime.executionContextCreated"
                && e.SessionId == session
                && e.Params.Get("context").Get("auxData").Get("isDefault").AsBool() == true)
            .LastOrDefault()
            ?? throw new InvalidOperationException("missing default context");
        JsonNode? context = found.Params.Get("context");
        return (
            context.Get("id").AsI64()!.Value,
            context.Get("uniqueId").AsString()!,
            context.Get("origin").AsString()!);
    }

    [Fact]
    public async Task NonblankCreateTargetExposesOnlyTheCommittedDocumentContext()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        (_, string session) = await CreateAndAttachAsync(
            ctx, "data:text/html,<title>committed</title>", 1);
        ctx.PendingEvents.Clear();
        await CoreCdp.DispatchAsync(ctx, 3, "Runtime.enable", new JsonObject(), session);

        (long id, _, string origin) = LatestDefaultContext(ctx, session);
        Assert.Equal(1, id);
        Assert.StartsWith("data:text/html,", origin, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ctx.PendingEvents,
            e => e.Method == "Runtime.executionContextCreated"
                && e.Params.Get("context").Get("origin").AsString() == "about:blank");
    }

    [Fact]
    public async Task DefaultContextIdentityIsPageOwnedForIdAndUniqueId()
    {
        var ctx = CdpContext.New();
        using IDisposable owned2 = CoreCdp.Owned(ctx);
        (_, string first) = await CreateAndAttachAsync(ctx, "about:blank", 1);
        (_, string second) = await CreateAndAttachAsync(ctx, "about:blank", 10);
        await CoreCdp.DispatchAsync(ctx, 20, "Runtime.enable", new JsonObject(), first);
        (long id, string uniqueId, _) = LatestDefaultContext(ctx, first);

        CdpResponse foreignId = await CoreCdp.DispatchAsync(
            ctx,
            21,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "1", ["contextId"] = id },
            second);
        Assert.Contains("Cannot find context", foreignId.Error!.Message, StringComparison.Ordinal);

        CdpResponse foreignUnique = await CoreCdp.DispatchAsync(
            ctx,
            22,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "1", ["uniqueContextId"] = uniqueId },
            second);
        Assert.Contains("Cannot find context", foreignUnique.Error!.Message, StringComparison.Ordinal);

        CdpResponse foreignCall = await CoreCdp.DispatchAsync(
            ctx,
            23,
            "Runtime.callFunctionOn",
            new JsonObject
            {
                ["functionDeclaration"] = "() => 1",
                ["executionContextId"] = id,
                ["returnByValue"] = true,
            },
            second);
        Assert.Contains("Cannot find context", foreignCall.Error!.Message, StringComparison.Ordinal);

        await CoreCdp.DispatchAsync(
            ctx,
            24,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<p>replacement</p>",
                ["waitUntil"] = "load",
            },
            first);

        (ulong RequestId, string Method, JsonObject Params)[] stalePairs =
        [
            (25UL, "Runtime.evaluate",
                new JsonObject { ["expression"] = "1", ["uniqueContextId"] = uniqueId }),
            (26UL, "Runtime.callFunctionOn", new JsonObject
            {
                ["functionDeclaration"] = "() => 1",
                ["executionContextId"] = id,
                ["returnByValue"] = true,
            }),
        ];
        foreach ((ulong requestId, string method, JsonObject parameters) in stalePairs)
        {
            CdpResponse stale = await CoreCdp.DispatchAsync(ctx, requestId, method, parameters, first);
            Assert.NotNull(stale.Error);
            Assert.Contains("Cannot find context", stale.Error!.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NavigatingOnePagePreservesTheOtherPagesIsolatedContext()
    {
        var ctx = CdpContext.New();
        using IDisposable owned3 = CoreCdp.Owned(ctx);
        (_, string first) = await CreateAndAttachAsync(ctx, "about:blank", 1);
        (_, string second) = await CreateAndAttachAsync(ctx, "about:blank", 10);
        foreach ((ulong id, string session) in new[] { (20UL, first), (21UL, second) })
        {
            await CoreCdp.DispatchAsync(ctx, id, "Runtime.enable", new JsonObject(), session);
        }

        long firstIsolated = (await CoreCdp.CdpAsync(
            ctx,
            30,
            "Page.createIsolatedWorld",
            new JsonObject { ["worldName"] = "first-utility" },
            first))["executionContextId"].AsI64()!.Value;
        long secondIsolated = (await CoreCdp.CdpAsync(
            ctx,
            31,
            "Page.createIsolatedWorld",
            new JsonObject { ["worldName"] = "second-utility" },
            second))["executionContextId"].AsI64()!.Value;

        await CoreCdp.DispatchAsync(
            ctx,
            32,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<p>first replacement</p>",
                ["waitUntil"] = "load",
            },
            first);

        CdpResponse secondStillRoutes = await CoreCdp.DispatchAsync(
            ctx,
            33,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "1", ["contextId"] = secondIsolated },
            second);
        Assert.Null(secondStillRoutes.Error);

        CdpResponse firstIsStale = await CoreCdp.DispatchAsync(
            ctx,
            34,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "1", ["contextId"] = firstIsolated },
            first);
        Assert.NotNull(firstIsStale.Error);
        Assert.Contains("Cannot find context", firstIsStale.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachedIsolatedContextIdsShareTheCurrentPageGlobalForNow()
    {
        var ctx = CdpContext.New();
        using IDisposable owned4 = CoreCdp.Owned(ctx);
        (_, string session) = await CreateAndAttachAsync(ctx, "about:blank", 1);
        await CoreCdp.DispatchAsync(
            ctx,
            3,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "globalThis.realmProbe = 'default'" },
            session);
        long isolated = (await CoreCdp.CdpAsync(
            ctx,
            4,
            "Page.createIsolatedWorld",
            new JsonObject { ["worldName"] = "bookkeeping-only" },
            session))["executionContextId"].AsI64()!.Value;

        CdpResponse isolatedResponse = await CoreCdp.DispatchAsync(
            ctx,
            5,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] =
                    "(function(){ globalThis.realmProbe += '-isolated'; return globalThis.realmProbe; })()",
                ["contextId"] = isolated,
                ["returnByValue"] = true,
            },
            session);
        Assert.True(
            isolatedResponse.Error is null,
            $"isolated evaluate failed: {isolatedResponse.Error?.Message}");
        Assert.Equal(
            "default-isolated", isolatedResponse.Result!["result"]!["value"].AsString());

        JsonNode defaultValue = await CoreCdp.CdpAsync(
            ctx,
            6,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "globalThis.realmProbe", ["returnByValue"] = true },
            session);
        Assert.Equal("default-isolated", defaultValue["result"]!["value"].AsString());
    }

    [Fact]
    public async Task NavigationContextEventsPreserveOrderForEveryRuntimeAttachment()
    {
        var ctx = CdpContext.New();
        using IDisposable owned5 = CoreCdp.Owned(ctx);
        (string target, string first) = await CreateAndAttachAsync(ctx, "about:blank", 1);
        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx,
            3,
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = target, ["flatten"] = true },
            null);
        string second = attached.Result!["sessionId"].AsString()!;
        foreach ((ulong id, string session) in new[] { (10UL, first), (11UL, second) })
        {
            await CoreCdp.DispatchAsync(ctx, id, "Runtime.enable", new JsonObject(), session);
        }

        await CoreCdp.DispatchAsync(
            ctx,
            12,
            "Page.createIsolatedWorld",
            new JsonObject { ["worldName"] = "utility" },
            first);
        ctx.PendingEvents.Clear();

        await CoreCdp.DispatchAsync(
            ctx,
            13,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<p>replacement</p>",
                ["waitUntil"] = "load",
            },
            first);

        List<string> firstMethods = [.. ctx.PendingEvents
            .Where(e => e.SessionId == first
                && (e.Method == "Page.lifecycleEvent"
                    || e.Method == "Page.frameNavigated"
                    || e.Method.StartsWith("Runtime.executionContext", StringComparison.Ordinal)))
            .Select(e => e.Method == "Page.lifecycleEvent"
                ? $"{e.Method}:{e.Params.Get("name").AsStringOr(string.Empty)}"
                : e.Method)];
        Assert.Equal(
            [
                "Page.lifecycleEvent:init",
                "Runtime.executionContextsCleared",
                "Page.frameNavigated",
                "Runtime.executionContextCreated",
            ],
            firstMethods.Take(4));
        Assert.Equal(
            2,
            firstMethods.Count(m => string.Equals(m, "Runtime.executionContextCreated", StringComparison.Ordinal)));

        List<string> secondMethods = [.. ctx.PendingEvents
            .Where(e => e.SessionId == second
                && e.Method.StartsWith("Runtime.executionContext", StringComparison.Ordinal))
            .Select(e => e.Method)];
        Assert.Equal(
            [
                "Runtime.executionContextsCleared",
                "Runtime.executionContextCreated",
                "Runtime.executionContextCreated",
            ],
            secondMethods);
    }

    [Fact]
    public async Task RuntimeAndBindingEventsUseTheOwningPagesDefaultContext()
    {
        var ctx = CdpContext.New();
        using IDisposable owned6 = CoreCdp.Owned(ctx);
        (_, string first) = await CreateAndAttachAsync(ctx, "about:blank", 1);
        (_, string second) = await CreateAndAttachAsync(ctx, "about:blank", 10);
        foreach ((ulong id, string session) in new[] { (20UL, first), (21UL, second) })
        {
            await CoreCdp.DispatchAsync(ctx, id, "Runtime.enable", new JsonObject(), session);
        }

        (long firstContext, _, _) = LatestDefaultContext(ctx, first);
        await CoreCdp.DispatchAsync(
            ctx, 22, "Runtime.addBinding", new JsonObject { ["name"] = "probe" }, second);
        ctx.PendingEvents.Clear();

        await CoreCdp.DispatchAsync(
            ctx,
            23,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<script>console.log('owned');probe('bound')</script>",
                ["waitUntil"] = "load",
            },
            second);
        (long secondContext, _, _) = LatestDefaultContext(ctx, second);
        Assert.NotEqual(firstContext, secondContext);

        CdpEvent console = ctx.PendingEvents.First(e =>
            e.Method == "Runtime.consoleAPICalled" && e.SessionId == second);
        Assert.Equal(secondContext, console.Params.Get("executionContextId").AsI64());
        CdpEvent binding = ctx.PendingEvents.First(e =>
            e.Method == "Runtime.bindingCalled" && e.SessionId == second);
        Assert.Equal(secondContext, binding.Params.Get("executionContextId").AsI64());
    }
}
