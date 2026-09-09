using System.Net.WebSockets;
using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/concurrent_navigations_with_fetch.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Issue #19 hard repro: 5 parallel clients with Fetch interception enabled must not abort
/// V8. The smoke test used <c>data:</c> URLs, which skip the subresource fetch path that
/// drives the abort. This test enables <c>Fetch</c> with <c>patterns: ["*"]</c>, navigates
/// to a URL with a subresource so the fetch op parks inside the V8 lock, and auto-replies
/// to <c>Fetch.requestPaused</c> with <c>Fetch.continueRequest</c> so the parked op resumes
/// - which is where two isolates can collide.
/// </para>
/// <para>
/// Deviation: <c>#[ignore]</c> in Rust because it boots a real server; it runs here.
/// </para>
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class ConcurrentNavigationsWithFetch
{
    private const string FixtureHtml =
        "<!DOCTYPE html><html><head><script src=\"/app.js\"></script></head><body><h1>ok</h1></body></html>";

    private const string FixtureJs = "console.log('fixture');";

    /// <summary>
    /// One client: createTarget for a sessionId, Fetch.enable, Page.navigate, then answer
    /// every Fetch.requestPaused with continueRequest until the navigate response lands.
    /// </summary>
    private static async Task OneClientWithFetchAsync(int port, ulong idBase, string targetUrl)
    {
        using ClientWebSocket ws = await CdpTestClient.ConnectAsync(port);

        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = idBase,
            ["method"] = "Target.createTarget",
            ["params"] = new JsonObject { ["url"] = "about:blank" },
        });

        string? sessionId = null;
        using (var createDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (sessionId is null)
            {
                string text = await CdpTestClient.ReceiveTextAsync(ws, createDeadline.Token)
                    ?? throw new IOException("ws closed");
                sessionId = CdpJson.Parse(text).Get("params").Get("sessionId").AsString();
            }
        }

        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = idBase + 1,
            ["method"] = "Fetch.enable",
            ["sessionId"] = sessionId,
            ["params"] = new JsonObject
            {
                ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*" }),
            },
        });
        await CdpTestClient.AwaitResponseAsync(ws, idBase + 1, TimeSpan.FromSeconds(15));

        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = idBase + 2,
            ["method"] = "Page.navigate",
            ["sessionId"] = sessionId,
            ["params"] = new JsonObject { ["url"] = targetUrl },
        });

        ulong autoId = idBase + 1000;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            string text = await CdpTestClient.ReceiveTextAsync(ws, deadline.Token)
                ?? throw new IOException("ws closed mid-navigate");
            JsonNode? value = CdpJson.Parse(text);

            if (value.Get("id").AsU64() == idBase + 2)
            {
                string? errorText = value.Get("result").Get("errorText").AsString();
                Assert.True(errorText is null, $"navigation failed: {errorText}");
                return;
            }

            if (value.Get("method").AsString() == "Fetch.requestPaused")
            {
                string requestId = value.Get("params").Get("requestId").AsStringOr(string.Empty);
                autoId += 1;
                await CdpTestClient.SendAsync(ws, new JsonObject
                {
                    ["id"] = autoId,
                    ["method"] = "Fetch.continueRequest",
                    ["sessionId"] = sessionId,
                    ["params"] = new JsonObject { ["requestId"] = requestId },
                });
            }
        }
    }

    [Fact]
    public async Task FetchInterceptConcurrency5DoesNotAbortV8()
    {
        using CoreCdpServer fixture = CoreCdpServer.Routed(path =>
            path.StartsWith("/app.js", StringComparison.Ordinal)
                ? (FixtureJs, "application/javascript", 200)
                : (FixtureHtml, "text/html", 200));
        await using CdpServerHandle server = await CdpServerHandle.StartAsync();

        List<Task> clients = [];
        for (ulong i = 0; i < 5; i++)
        {
            ulong idBase = (i + 1) * 1000;
            clients.Add(Task.Run(() => OneClientWithFetchAsync(server.Port, idBase, fixture.Url)));
        }

        await Task.WhenAll(clients);
        Assert.All(clients, client => Assert.True(client.IsCompletedSuccessfully));
    }
}
