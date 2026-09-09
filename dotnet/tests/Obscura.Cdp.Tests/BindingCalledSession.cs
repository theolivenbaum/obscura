using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of
/// <c>crates/obscura-cdp/tests/binding_called_session.rs</c>.
/// </summary>
/// <remarks>
/// <c>Runtime.bindingCalled</c> was addressed to one arbitrary session of the
/// page, picked by hash ordering. A client reaching a page the ordinary way holds
/// two sessions (<c>Target.createTarget</c> opens one and the
/// <c>Target.attachToTarget</c> after it opens another) and discards any event
/// whose sessionId is not the one it attached with, so
/// <c>page.exposeFunction()</c> never fired its callback.
/// </remarks>
public sealed class BindingCalledSessionTests
{
    private static async Task<JsonNode?> CdpAsync(
        CdpContext ctx,
        ulong id,
        string method,
        JsonNode? parameters,
        string? session)
    {
        var resp = await Dispatcher.DispatchAsync(
            new CdpRequest
            {
                Id = id,
                Method = method,
                Params = parameters,
                SessionId = session,
            },
            ctx);
        Assert.True(resp.Error is null, $"CDP {method} failed: {resp.Error?.Message}");
        return resp.Result ?? new JsonObject();
    }

    /// <summary>
    /// Opens a page the way Puppeteer and Playwright do, rather than inserting a
    /// session for a hand-made page.
    /// </summary>
    private static async Task<(string TargetId, string SessionId)> CreatedAndAttachedAsync(
        CdpContext ctx)
    {
        var created = await CdpAsync(
            ctx, 900, "Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null);
        var targetId = created.Get("targetId").AsString();
        Assert.NotNull(targetId);
        var session = await AttachToAsync(ctx, targetId, 901);
        return (targetId, session);
    }

    private static async Task<string> AttachToAsync(CdpContext ctx, string targetId, ulong id)
    {
        var attached = await CdpAsync(
            ctx,
            id,
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = targetId, ["flatten"] = true },
            null);
        var sessionId = attached.Get("sessionId").AsString();
        Assert.NotNull(sessionId);
        return sessionId;
    }

    private static List<(string Session, JsonNode? Params)> BindingCalls(CdpContext ctx) =>
    [
        .. ctx.PendingEvents
            .Where(e => string.Equals(e.Method, "Runtime.bindingCalled", StringComparison.Ordinal))
            .Select(e => (e.SessionId ?? string.Empty, e.Params)),
    ];

    /// <summary>
    /// The deterministic guard. Two sessions each subscribe, so the correct answer
    /// is two events. Delivering to one session of the page can only ever produce
    /// one, so this fails on the old behaviour no matter which session the
    /// ordering happens to pick.
    /// </summary>
    [Fact]
    public async Task EverySessionThatAddedTheBindingIsCalled()
    {
        var ctx = CdpContext.New();
        var (targetId, first) = await CreatedAndAttachedAsync(ctx);
        var second = await AttachToAsync(ctx, targetId, 902);
        Assert.False(
            string.Equals(first, second, StringComparison.Ordinal),
            "the second attach reused the first session");

        foreach (var (id, session) in new (ulong, string)[] { (1, first), (2, second) })
        {
            await CdpAsync(ctx, id, "Runtime.enable", new JsonObject(), session);
            await CdpAsync(
                ctx,
                id + 10,
                "Runtime.addBinding",
                new JsonObject { ["name"] = "obscuraProbe" },
                session);
        }

        await CdpAsync(
            ctx,
            3,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "obscuraProbe('HELLO')" },
            first);

        var called = BindingCalls(ctx);
        var got = called.Select(c => c.Session).Order(StringComparer.Ordinal).ToList();
        var want = new List<string> { first, second }.Order(StringComparer.Ordinal).ToList();
        Assert.Equal(want, got);
        foreach (var (_, parameters) in called)
        {
            Assert.Equal("obscuraProbe", parameters.Get("name").AsString());
            Assert.Equal("HELLO", parameters.Get("payload").AsString());
        }
    }

    /// <summary>
    /// A session that never asked for the binding is not told about it, so a
    /// client does not see a call it has no handler for.
    /// </summary>
    [Fact]
    public async Task ASessionThatDidNotSubscribeIsNotCalled()
    {
        var ctx = CdpContext.New();
        var (targetId, subscriber) = await CreatedAndAttachedAsync(ctx);
        var bystander = await AttachToAsync(ctx, targetId, 902);

        await CdpAsync(ctx, 1, "Runtime.enable", new JsonObject(), subscriber);
        await CdpAsync(ctx, 2, "Runtime.enable", new JsonObject(), bystander);
        await CdpAsync(
            ctx, 3, "Runtime.addBinding", new JsonObject { ["name"] = "obscuraProbe" }, subscriber);
        await CdpAsync(
            ctx,
            4,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "obscuraProbe('HELLO')" },
            subscriber);

        var called = BindingCalls(ctx);
        Assert.Equal([subscriber], called.Select(c => c.Session));
    }

    /// <summary>
    /// Removing the binding drops both the shim and the subscription, so a later
    /// call cannot be delivered to a client that has stopped listening.
    /// </summary>
    [Fact]
    public async Task ARemovedBindingIsNotCalled()
    {
        var ctx = CdpContext.New();
        var (_, session) = await CreatedAndAttachedAsync(ctx);

        await CdpAsync(ctx, 1, "Runtime.enable", new JsonObject(), session);
        await CdpAsync(
            ctx, 2, "Runtime.addBinding", new JsonObject { ["name"] = "obscuraProbe" }, session);
        await CdpAsync(
            ctx, 3, "Runtime.removeBinding", new JsonObject { ["name"] = "obscuraProbe" }, session);

        var gone = await CdpAsync(
            ctx,
            4,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = "typeof globalThis.obscuraProbe",
                ["returnByValue"] = true,
            },
            session);
        Assert.Equal("undefined", gone.Get("result").Get("value").AsString());
        Assert.Empty(BindingCalls(ctx));
    }
}
