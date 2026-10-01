using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

/// <summary>
/// SECURITY.md L3: an SSE stream was pinged forever, so a client that opened one and went
/// away held its slot for good. It now closes after
/// <c>POCKETCALCULATOR_MCP_IDLE_TIMEOUT_MS</c> with no MCP request on the server.
/// </summary>
public sealed class SseIdleTimeoutTests
{
    [Fact]
    public async Task SseStreamClosesWhenTheServerIsIdle()
    {
        var closed = await SseClosesWithinAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));
        Assert.True(closed, "the SSE stream should close after the idle timeout");
    }

    [Fact]
    public async Task ZeroDisablesTheSseIdleTimeout()
    {
        var closed = await SseClosesWithinAsync(TimeSpan.Zero, TimeSpan.FromMilliseconds(1_200));
        Assert.False(closed);
    }

    private static async Task<bool> SseClosesWithinAsync(TimeSpan idle, TimeSpan wait)
    {
        var port = SseStreamDoesNotWedgeTests.PickFreePort();
        using var cts = new CancellationTokenSource();
        var server = Task.Run(() => Http.RunAsync(
            "127.0.0.1", port, null, null, false, null, null,
            new Http.ServerOptions { IdleTimeout = idle }, cts.Token));
        try
        {
            await SseStreamDoesNotWedgeTests.WaitForListenerAsync(port);
            using var sse = new TcpClient();
            await sse.ConnectAsync(IPAddress.Loopback, port);
            var stream = sse.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(
                "GET /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nAccept: text/event-stream\r\n\r\n"));
            var head = await SseStreamDoesNotWedgeTests.ReadOnceAsync(
                stream, TimeSpan.FromSeconds(5), "SSE headers read timed out");
            Assert.Contains("text/event-stream", head, StringComparison.OrdinalIgnoreCase);

            using var timeout = new CancellationTokenSource(wait);
            var buffer = new byte[256];
            try
            {
                while (true)
                {
                    var n = await stream.ReadAsync(buffer, timeout.Token);
                    if (n == 0)
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAny(server, Task.Delay(2000));
        }
    }
}
