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

    /// <summary>
    /// L3: an inbound message over the cap closes the connection before it is
    /// buffered in full, and one under it is served.
    /// </summary>
    [Fact]
    public async Task AnOversizedMessageClosesTheConnection()
    {
        await using var server = await CdpServerHandle.StartAsync();
        using var ws = await CdpTestClient.ConnectAsync(server.Port, TestContext.Current.CancellationToken);

        var padding = new string('a', 1 << 20);
        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 1,
            ["method"] = "Browser.getVersion",
            ["params"] = new JsonObject { ["padding"] = padding },
        }, TestContext.Current.CancellationToken);
        Assert.NotNull((await CdpTestClient.AwaitResponseAsync(ws, 1, Timeout))?["result"]);

        var oversized = new byte[CdpServer.MaxMessageBytes + 1];
        Array.Fill(oversized, (byte)' ');
        oversized[0] = (byte)'{';
        try
        {
            await ws.SendAsync(oversized, WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        }
        catch (WebSocketException)
        {
            // The server may already have closed mid-message.
        }

        using var deadline = new CancellationTokenSource(Timeout);
        try
        {
            Assert.Null(await CdpTestClient.ReceiveTextAsync(ws, deadline.Token));
        }
        catch (WebSocketException)
        {
        }
    }

    /// <summary>
    /// L3: the reply queue refuses a write that takes it past its limit, trips
    /// its overflow token so the connection closes, and accepts nothing after.
    /// </summary>
    [Fact]
    public async Task TheReplyQueueClosesWhenTheClientStopsReading()
    {
        var queue = new ReplyQueue(maxQueuedChars: 100);
        Assert.True(queue.Writer.TryWrite(new string('x', 60)));
        Assert.False(queue.Overflowed.IsCancellationRequested);

        Assert.False(queue.Writer.TryWrite(new string('y', 60)));
        Assert.True(queue.Overflowed.IsCancellationRequested);
        Assert.False(queue.Writer.TryWrite("z"));

        // What was queued before the overflow still drains, then the queue ends.
        Assert.True(queue.Reader.TryRead(out var first));
        Assert.Equal(60, first.Length);
        Assert.Equal(0, queue.QueuedChars);
        Assert.False(await queue.Reader.WaitToReadAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A reader that keeps up never trips the limit, however much passes through.</summary>
    [Fact]
    public void TheReplyQueueCountsOnlyWhatIsWaiting()
    {
        var queue = new ReplyQueue(maxQueuedChars: 100);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(queue.Writer.TryWrite(new string('x', 90)));
            Assert.True(queue.Reader.TryRead(out _));
        }
        Assert.False(queue.Overflowed.IsCancellationRequested);
    }

    /// <summary>L3: one connection holds at most <see cref="CdpContext.MaxPagesPerConnection"/> targets.</summary>
    [Fact]
    public async Task TargetsPerConnectionAreCapped()
    {
        await using var server = await CdpServerHandle.StartAsync();
        using var ws = await CdpTestClient.ConnectAsync(server.Port, TestContext.Current.CancellationToken);

        JsonNode? last = null;
        ulong id = 0;
        for (var i = 0; i <= CdpContext.MaxPagesPerConnection; i++)
        {
            id++;
            await CdpTestClient.SendAsync(ws, new JsonObject
            {
                ["id"] = id,
                ["method"] = "Target.createTarget",
                ["params"] = new JsonObject { ["url"] = "about:blank" },
            }, TestContext.Current.CancellationToken);
            last = await CdpTestClient.AwaitResponseAsync(ws, id, TimeSpan.FromSeconds(60));
            if (last?["error"] is not null)
            {
                break;
            }
        }

        Assert.NotNull(last?["error"]);
        Assert.Contains("Too many targets", last!["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(id <= CdpContext.MaxPagesPerConnection + 1);
    }
}
