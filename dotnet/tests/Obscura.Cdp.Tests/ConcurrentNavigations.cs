using System.Net.WebSockets;
using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/concurrent_navigations.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Issue #19 smoke test: 5 parallel CDP clients each performing
/// <c>Target.createTarget</c> + <c>Page.navigate</c> must not abort the process.
/// </para>
/// <para>
/// NOTE on coverage. The deterministic abort in #19 requires the navigations to interleave,
/// which only happens once a navigation actually yields; the heaviest yields come from
/// subresource fetches when the page has scripts or images to pull. <c>data:</c> URLs skip
/// every fetch, so this test exercises the chokepoint shape (5 clients hitting dispatch
/// concurrently) without driving the original abort. Treat it as a smoke check.
/// </para>
/// <para>
/// Deviation: the Rust test is <c>#[ignore]</c>d because it boots a real CDP server, which
/// is heavier than a unit test. It costs about a second here, so it runs.
/// </para>
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class ConcurrentNavigations
{
    private static async Task OneClientAsync(int port, ulong idBase)
    {
        using ClientWebSocket ws = await CdpTestClient.ConnectAsync(port);

        // Target.createTarget - get a sessionId via the attachedToTarget event.
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
                JsonNode? value = CdpJson.Parse(text);
                sessionId = value.Get("params").Get("sessionId").AsString();
            }
        }

        // Page.navigate to a data URL - exercises the JS init and script execution
        // without touching the network, which is what the V8 race needs.
        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = idBase + 1,
            ["method"] = "Page.navigate",
            ["sessionId"] = sessionId,
            ["params"] = new JsonObject
            {
                ["url"] = "data:text/html,<html><body><h1>x</h1></body></html>",
            },
        });

        await CdpTestClient.AwaitResponseAsync(ws, idBase + 1, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Concurrency5DoesNotAbortV8()
    {
        await using CdpServerHandle server = await CdpServerHandle.StartAsync();

        List<Task> clients = [];
        for (ulong i = 0; i < 5; i++)
        {
            ulong idBase = (i + 1) * 1000;
            clients.Add(Task.Run(() => OneClientAsync(server.Port, idBase)));
        }

        await Task.WhenAll(clients);
        Assert.All(clients, client => Assert.True(client.IsCompletedSuccessfully));
    }
}
