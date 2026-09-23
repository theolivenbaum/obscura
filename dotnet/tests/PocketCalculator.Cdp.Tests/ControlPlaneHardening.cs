using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// Control-plane hardening on the CDP WebSocket (SECURITY.md L3, L4).
/// </summary>
public sealed class ControlPlaneHardeningTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// L4: a message is a <c>Browser.close</c> only when its method is. A string
    /// argument that happens to read <c>"Browser.close"</c> used to close the
    /// connection, because the check was a substring match over the raw text.
    /// </summary>
    [Fact]
    public async Task BrowserCloseInAnArgumentDoesNotCloseTheConnection()
    {
        await using var server = await CdpServerHandle.StartAsync();
        using var ws = await CdpTestClient.ConnectAsync(server.Port, TestContext.Current.CancellationToken);

        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 1,
            ["method"] = "Target.getTargets",
            ["params"] = new JsonObject { ["filter"] = "Browser.close" },
        }, TestContext.Current.CancellationToken);
        var first = await CdpTestClient.AwaitResponseAsync(ws, 1, Timeout);
        Assert.NotNull(first);

        // Still open: a second command is answered.
        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 2,
            ["method"] = "Browser.getVersion",
        }, TestContext.Current.CancellationToken);
        var second = await CdpTestClient.AwaitResponseAsync(ws, 2, Timeout);
        Assert.NotNull(second?["result"]);

        // The real method still closes it, after answering.
        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 3,
            ["method"] = "Browser.close",
        }, TestContext.Current.CancellationToken);
        var closed = await CdpTestClient.AwaitResponseAsync(ws, 3, Timeout);
        Assert.NotNull(closed?["result"]);
        using var deadline = new CancellationTokenSource(Timeout);
        try
        {
            Assert.Null(await CdpTestClient.ReceiveTextAsync(ws, deadline.Token));
        }
        catch (WebSocketException)
        {
            // Closed without the close handshake, which is also closed.
        }
    }
}
