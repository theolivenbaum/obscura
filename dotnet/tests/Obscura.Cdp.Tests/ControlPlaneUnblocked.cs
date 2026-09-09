using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of
/// <c>crates/obscura-cdp/tests/control_plane_unblocked.rs</c>: issue #62, the
/// HTTP control plane must respond promptly even while V8 JS evaluation blocks
/// the connection's thread.
/// </summary>
/// <remarks>
/// Before the fix, the HTTP accept loop competed with the CDP processor on the
/// same scheduler, so a synchronous JS <c>while</c> loop starved every other
/// task, including the accept. The dedicated accept thread fixes this.
/// </remarks>
public sealed class ControlPlaneUnblockedTests
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(3);
    private const int JsDurationMs = 5000;

    [Fact]
    public async Task HttpControlPlaneUnblockedDuringLongJs()
    {
        await using var server = await CdpServerHandle.StartAsync();

        // Connect via WebSocket, create a target, then send a long-running JS
        // evaluation on that target's session.
        using var ws = await CdpTestClient.ConnectAsync(server.Port);
        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 1,
            ["method"] = "Target.createTarget",
            ["params"] = new JsonObject { ["url"] = "about:blank" },
        });

        string? sessionId = null;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            while (sessionId is null)
            {
                var text = await CdpTestClient.ReceiveTextAsync(ws, deadline.Token)
                           ?? throw new IOException("ws closed");
                sessionId = CdpJson.Parse(text).Get("params").Get("sessionId").AsString();
            }
        }

        // Fire a synchronous JS loop that holds V8 for JsDurationMs.
        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 2,
            ["method"] = "Runtime.evaluate",
            ["sessionId"] = sessionId,
            ["params"] = new JsonObject
            {
                ["expression"] = $"var s=Date.now();while(Date.now()-s<{JsDurationMs}){{}}'done'",
                ["awaitPromise"] = false,
                ["returnByValue"] = true,
            },
        });

        // Give the JS evaluation a moment to enter V8.
        await Task.Delay(300);

        // Issue the HTTP request from a separate thread so the test's own
        // scheduler cannot be what keeps it responsive.
        var started = Stopwatch.GetTimestamp();
        var response = await Task.Run(
            () => CdpTestClient.HttpGetAsync(server.Port, "/json/version", HttpTimeout));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Contains("webSocketDebuggerUrl", response, StringComparison.Ordinal);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(2),
            $"/json/version response too slow during JS eval: {elapsed}");
    }
}
