using System.Net.WebSockets;
using System.Text.Json.Nodes;

using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// An awaited <c>Runtime.evaluate</c> does not hold up the connection's other commands.
/// </summary>
/// <remarks>
/// Chromium 141 serves other commands while an <c>awaitPromise</c> promise is pending and
/// answers it when it settles. The server used to await inline, so a promise calling an
/// exposed binding (Playwright <c>exposeFunction</c>, Puppeteer <c>exposeFunction</c>)
/// deadlocked: the client's answer to <c>Runtime.bindingCalled</c> is a command of its
/// own, queued behind the await.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class AwaitedEvaluateParking
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static async Task<string> OpenPageAsync(ClientWebSocket ws)
    {
        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 1,
            ["method"] = "Target.createTarget",
            ["params"] = new JsonObject { ["url"] = "about:blank" },
        });

        string? sessionId = null;
        await CdpTestClient.AwaitResponseAsync(ws, 1, Wait, value =>
            sessionId ??= value.Get("params").Get("sessionId").AsString());
        Assert.NotNull(sessionId);
        return sessionId;
    }

    private static Task SendAsync(ClientWebSocket ws, ulong id, string method, string session, JsonObject parameters) =>
        CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["sessionId"] = session,
            ["params"] = parameters,
        });

    private static async Task<string> ReadyPageAsync(ClientWebSocket ws)
    {
        string session = await OpenPageAsync(ws);
        await SendAsync(ws, 2, "Runtime.enable", session, []);
        await CdpTestClient.AwaitResponseAsync(ws, 2, Wait);
        await SendAsync(ws, 3, "Runtime.addBinding", session, new JsonObject { ["name"] = "exposed" });
        await CdpTestClient.AwaitResponseAsync(ws, 3, Wait);
        await SendAsync(ws, 4, "Page.navigate", session, new JsonObject
        {
            ["url"] = "data:text/html,<p>x</p>",
        });
        await CdpTestClient.AwaitResponseAsync(ws, 4, Wait);
        return session;
    }

    private static JsonObject AwaitedEvaluate(string expression) => new()
    {
        ["expression"] = expression,
        ["awaitPromise"] = true,
        ["returnByValue"] = true,
        ["timeout"] = 10_000,
    };

    /// <summary>
    /// The exposed-function round trip: the awaited promise settles only when the
    /// client, having seen <c>Runtime.bindingCalled</c>, delivers the result.
    /// </summary>
    [Fact]
    public async Task BindingRoundTripAnswersTheAwaitedEvaluate()
    {
        await using CdpServerHandle server = await CdpServerHandle.StartAsync();
        using ClientWebSocket ws = await CdpTestClient.ConnectAsync(server.Port);
        string session = await ReadyPageAsync(ws);

        await SendAsync(ws, 10, "Runtime.evaluate", session, AwaitedEvaluate(
            "new Promise(r => { globalThis.__deliver = r; exposed('41'); })"));

        string? payload = null;
        using var deadline = new CancellationTokenSource(Wait);
        while (payload is null)
        {
            JsonNode? message = CdpJson.Parse(
                await CdpTestClient.ReceiveTextAsync(ws, deadline.Token) ?? throw new IOException("ws closed"));
            Assert.NotEqual(10UL, message.Get("id").AsU64());
            if (message.Get("method").AsString() == "Runtime.bindingCalled")
            {
                payload = message.Get("params").Get("payload").AsString();
            }
        }

        Assert.Equal("41", payload);
        List<ulong> order = [];
        JsonNode? delivered = null;
        await SendAsync(ws, 11, "Runtime.evaluate", session, new JsonObject
        {
            ["expression"] = "__deliver(Number('41') + 1), 'delivered'",
            ["returnByValue"] = true,
        });
        JsonNode? awaited = await CdpTestClient.AwaitResponseAsync(ws, 10, Wait, value =>
        {
            if (value.Get("id").AsU64() is { } id)
            {
                order.Add(id);
                if (id == 11)
                {
                    delivered = value;
                }
            }
        });

        Assert.Equal([11UL, 10UL], order);
        Assert.Equal("delivered", delivered.Get("result").Get("result").Get("value").AsString());
        Assert.Equal(42.0, awaited.Get("result").Get("result").Get("value")!.GetValue<double>());
    }

    /// <summary>A pending promise does not hold up a command sent after it; Chromium answers that one first.</summary>
    [Fact]
    public async Task LaterCommandIsAnsweredWhileAPromiseIsPending()
    {
        await using CdpServerHandle server = await CdpServerHandle.StartAsync();
        using ClientWebSocket ws = await CdpTestClient.ConnectAsync(server.Port);
        string session = await ReadyPageAsync(ws);

        await SendAsync(ws, 20, "Runtime.evaluate", session, AwaitedEvaluate(
            "new Promise(r => setTimeout(() => r('slow'), 300))"));
        await SendAsync(ws, 21, "Runtime.evaluate", session, new JsonObject
        {
            ["expression"] = "'fast'",
            ["returnByValue"] = true,
        });

        List<(ulong Id, string? Value)> answers = [];
        await CdpTestClient.AwaitResponseAsync(ws, 20, Wait, value =>
        {
            if (value.Get("id").AsU64() is { } id)
            {
                answers.Add((id, value.Get("result").Get("result").Get("value").AsString()));
            }
        });
        if (answers.Count == 1)
        {
            await CdpTestClient.AwaitResponseAsync(ws, 21, Wait, value =>
            {
                if (value.Get("id").AsU64() is { } id)
                {
                    answers.Add((id, value.Get("result").Get("result").Get("value").AsString()));
                }
            });
        }

        Assert.Equal([(21UL, "fast"), (20UL, "slow")], answers);
    }

    /// <summary>
    /// A navigation while a promise is parked fails the command as Chromium does,
    /// rather than leaving it unanswered.
    /// </summary>
    [Fact]
    public async Task NavigationFailsAParkedAwait()
    {
        await using CdpServerHandle server = await CdpServerHandle.StartAsync();
        using ClientWebSocket ws = await CdpTestClient.ConnectAsync(server.Port);
        string session = await ReadyPageAsync(ws);

        await SendAsync(ws, 30, "Runtime.evaluate", session, AwaitedEvaluate("new Promise(() => {})"));
        await SendAsync(ws, 31, "Page.navigate", session, new JsonObject
        {
            ["url"] = "data:text/html,<p>y</p>",
        });

        JsonNode? failed = await CdpTestClient.AwaitResponseAsync(ws, 30, Wait);
        Assert.Equal(-32000, failed.Get("error").Get("code")!.GetValue<long>());
        Assert.Equal("Inspected target navigated or closed", failed.Get("error").Get("message").AsString());
    }

    /// <summary>A parked promise that never settles still fails at its timeout.</summary>
    [Fact]
    public async Task ParkedAwaitTimesOut()
    {
        await using CdpServerHandle server = await CdpServerHandle.StartAsync();
        using ClientWebSocket ws = await CdpTestClient.ConnectAsync(server.Port);
        string session = await ReadyPageAsync(ws);

        JsonObject never = AwaitedEvaluate("new Promise(() => {})");
        never["timeout"] = 500;
        await SendAsync(ws, 40, "Runtime.evaluate", session, never);
        await SendAsync(ws, 41, "Runtime.evaluate", session, new JsonObject { ["expression"] = "1" });

        JsonNode? failed = await CdpTestClient.AwaitResponseAsync(ws, 40, Wait);
        Assert.Contains("did not settle within 500ms", failed.Get("error").Get("message").AsString(), StringComparison.Ordinal);
    }
}
