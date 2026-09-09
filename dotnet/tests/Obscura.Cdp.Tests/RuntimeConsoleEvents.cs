using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/runtime_console_events.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class RuntimeConsoleEvents
{
    private static async Task<(string TargetId, string Session)> CreateAndAttachAsync(CdpContext ctx)
    {
        JsonNode created = await CoreCdp.CdpAsync(
            ctx, 900, "Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null);
        string targetId = created["targetId"].AsString()
            ?? throw new InvalidOperationException("Target.createTarget returned no targetId");
        string session = await AttachAsync(ctx, targetId, 901);
        return (targetId, session);
    }

    private static async Task<string> AttachAsync(CdpContext ctx, string targetId, ulong id)
    {
        JsonNode attached = await CoreCdp.CdpAsync(
            ctx,
            id,
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = targetId, ["flatten"] = true },
            null);
        return attached["sessionId"].AsString()
            ?? throw new InvalidOperationException("Target.attachToTarget returned no sessionId");
    }

    private static List<(string? Session, JsonNode? Params)> Events(CdpContext ctx, string method) =>
        [.. ctx.PendingEvents.Where(e => e.Method == method).Select(e => (e.SessionId, e.Params))];

    [Fact]
    public async Task NavigationEmitsConsoleArgumentsAndUncaughtExceptionDetails()
    {
        var ctx = CdpContext.New();
        (_, string session) = await CreateAndAttachAsync(ctx);
        await CoreCdp.CdpAsync(ctx, 1, "Runtime.enable", new JsonObject(), session);
        ctx.PendingEvents.Clear();

        const string url = "data:text/html,<script>"
            + "window.__probe=1;"
            + "console.log('probe',{answer:42},undefined,NaN,-0,1n);"
            + "console.error('bad');"
            + "throw new Error('boom')"
            + "</script>";
        await CoreCdp.CdpAsync(
            ctx,
            2,
            "Page.navigate",
            new JsonObject { ["url"] = url, ["waitUntil"] = "load" },
            session);

        List<(string? Session, JsonNode? Params)> console = Events(ctx, "Runtime.consoleAPICalled");
        Assert.Equal(2, console.Count);
        Assert.All(console, entry => Assert.Equal(session, entry.Session));

        JsonNode log = console.First(entry => entry.Params!["type"].AsString() == "log").Params!;
        Assert.Equal(2, log["executionContextId"].AsI64());
        Assert.True((log["timestamp"].AsF64() ?? 0.0) > 0.0);
        Assert.Equal(6, JsonExt.AsJsonArray(log["args"])!.Count);
        Assert.Equal("string", log["args"]![0]!["type"].AsString());
        Assert.Equal("probe", log["args"]![0]!["value"].AsString());
        Assert.Equal("object", log["args"]![1]!["type"].AsString());
        string objectId = log["args"]![1]!["objectId"].AsString()
            ?? throw new InvalidOperationException("console object argument had no objectId");
        Assert.Equal("undefined", log["args"]![2]!["type"].AsString());
        Assert.Equal("NaN", log["args"]![3]!["unserializableValue"].AsString());
        Assert.Equal("-0", log["args"]![4]!["unserializableValue"].AsString());
        Assert.Equal("bigint", log["args"]![5]!["type"].AsString());
        Assert.Equal("1n", log["args"]![5]!["unserializableValue"].AsString());

        JsonNode error = console.First(entry => entry.Params!["type"].AsString() == "error").Params!;
        Assert.Equal("bad", error["args"]![0]!["value"].AsString());

        List<(string? Session, JsonNode? Params)> exceptions = Events(ctx, "Runtime.exceptionThrown");
        Assert.Single(exceptions);
        Assert.Equal(session, exceptions[0].Session);
        JsonNode details = exceptions[0].Params!["exceptionDetails"]!;
        Assert.Equal("Uncaught", details["text"].AsString());
        Assert.NotNull(details["exceptionId"].AsU64());
        Assert.StartsWith(
            "data:text/html,", details["url"].AsStringOr(string.Empty), StringComparison.Ordinal);
        Assert.Contains(
            "Error: boom",
            details["exception"]!["description"].AsStringOr(string.Empty),
            StringComparison.Ordinal);
        Assert.True((exceptions[0].Params!["timestamp"].AsF64() ?? 0.0) > 0.0);

        JsonNode objectValue = await CoreCdp.CdpAsync(
            ctx,
            20,
            "Runtime.callFunctionOn",
            new JsonObject
            {
                ["functionDeclaration"] = "function() { return this.answer; }",
                ["objectId"] = objectId,
                ["returnByValue"] = true,
            },
            session);
        Assert.Equal(42.0, objectValue["result"]!["value"].AsF64());

        JsonNode marker = await CoreCdp.CdpAsync(
            ctx,
            3,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "window.__probe", ["returnByValue"] = true },
            session);
        Assert.Equal(1.0, marker["result"]!["value"].AsF64());
    }

    [Fact]
    public async Task RuntimeEventsOnlyReachSessionsWhileRuntimeIsEnabled()
    {
        var ctx = CdpContext.New();
        (string targetId, string first) = await CreateAndAttachAsync(ctx);
        string second = await AttachAsync(ctx, targetId, 902);

        await CoreCdp.CdpAsync(ctx, 1, "Runtime.enable", new JsonObject(), first);
        ctx.PendingEvents.Clear();
        await CoreCdp.CdpAsync(
            ctx,
            2,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "console.log('first')" },
            first);
        List<(string? Session, JsonNode? Params)> firstEvents = Events(ctx, "Runtime.consoleAPICalled");
        Assert.Equal([first], firstEvents.Select(e => e.Session));
        Assert.Equal(1, firstEvents[0].Params!["executionContextId"].AsI64());

        await CoreCdp.CdpAsync(ctx, 3, "Runtime.enable", new JsonObject(), second);
        ctx.PendingEvents.Clear();
        await CoreCdp.CdpAsync(
            ctx,
            4,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "console.warn('both')" },
            first);
        List<string> targets = [.. Events(ctx, "Runtime.consoleAPICalled")
            .Select(e => e.Session ?? throw new InvalidOperationException("runtime event had no session"))];
        targets.Sort(StringComparer.Ordinal);
        List<string> expected = [first, second];
        expected.Sort(StringComparer.Ordinal);
        Assert.Equal(expected, targets);
        Assert.All(
            Events(ctx, "Runtime.consoleAPICalled"),
            entry => Assert.Equal("warning", entry.Params!["type"].AsString()));

        await CoreCdp.CdpAsync(ctx, 5, "Runtime.disable", new JsonObject(), first);
        ctx.PendingEvents.Clear();
        await CoreCdp.CdpAsync(
            ctx,
            6,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "console.error('second')" },
            second);
        Assert.Equal(
            [second],
            Events(ctx, "Runtime.consoleAPICalled").Select(e => e.Session));

        await CoreCdp.CdpAsync(ctx, 7, "Runtime.releaseObjectGroup", new JsonObject(), second);
        await CoreCdp.CdpAsync(ctx, 8, "Runtime.disable", new JsonObject(), second);
        ctx.PendingEvents.Clear();
        JsonNode retained = await CoreCdp.CdpAsync(
            ctx,
            9,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] =
                    "(() => { console.log({notRetained:true}); return Object.keys("
                    + "globalThis.__obscura_objects).filter(k => k.startsWith('console-')).length; })()",
                ["returnByValue"] = true,
            },
            second);
        Assert.Equal(0.0, retained["result"]!["value"].AsF64());
        Assert.Empty(Events(ctx, "Runtime.consoleAPICalled"));
    }
}
