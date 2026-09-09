using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Obscura.Mcp.Tests;

/// <summary>
/// Regression test: an open SSE stream (<c>GET /mcp</c> with
/// <c>Accept: text/event-stream</c>) must not wedge the whole MCP HTTP server.
/// </summary>
/// <remarks>
/// The server serves connections sequentially (the browser session owns a V8
/// runtime and never crosses threads). The SSE keep-alive is an infinite ping
/// loop, so if it is held inline it never returns and the accept loop never runs
/// again - every later request hangs forever. The keep-alive carries no browser
/// state, so it must be detached and the connection handler must return, leaving
/// the accept loop free.
/// </remarks>
public sealed class SseStreamDoesNotWedgeTests
{
    internal static ushort PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        listener.Dispose();
        return port;
    }

    internal static async Task WaitForListenerAsync(ushort port)
    {
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }
    }

    internal static async Task<string> ReadOnceAsync(NetworkStream stream, TimeSpan timeout, string because)
    {
        var buffer = new byte[4096];
        var read = stream.ReadAsync(buffer.AsMemory()).AsTask();
        var completed = await Task.WhenAny(read, Task.Delay(timeout));
        Assert.True(completed == read, because);
        var n = await read;
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    [Fact]
    public async Task OpenSseStreamDoesNotBlockOtherRequests()
    {
        var port = PickFreePort();
        using var cts = new CancellationTokenSource();
        var server = Task.Run(() => Http.RunAsync("127.0.0.1", port, null, null, false, cts.Token));

        try
        {
            await WaitForListenerAsync(port);

            // Connection A: open the SSE stream and read its headers so the
            // server-side handler has entered its keep-alive path. Keep it open.
            using var sse = new TcpClient();
            await sse.ConnectAsync(IPAddress.Loopback, port);
            var sseStream = sse.GetStream();
            var get = Encoding.UTF8.GetBytes(
                "GET /mcp HTTP/1.1\r\n"
                + "Host: 127.0.0.1\r\n"
                + "Accept: text/event-stream\r\n"
                + "\r\n");
            await sseStream.WriteAsync(get);
            await sseStream.FlushAsync();

            var sseHead = (await ReadOnceAsync(
                sseStream, TimeSpan.FromSeconds(2), "SSE headers read timed out")).ToLowerInvariant();
            Assert.True(
                sseHead.Contains("text/event-stream", StringComparison.Ordinal),
                $"expected an SSE response on the GET stream, got:\n{sseHead}");

            // Connection B: while A's SSE stream is still open, a second request must
            // still be accepted and answered promptly. Pre-fix this hangs because the
            // SSE loop never yields the accept loop.
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var request = Encoding.UTF8.GetBytes(
                "OPTIONS /mcp HTTP/1.1\r\n"
                + "Host: 127.0.0.1\r\n"
                + "Origin: https://dashboard.example.com\r\n"
                + "Access-Control-Request-Method: POST\r\n"
                + "\r\n");
            await stream.WriteAsync(request);
            await stream.FlushAsync();

            var response = await ReadOnceAsync(
                stream,
                TimeSpan.FromSeconds(3),
                "second request timed out - the SSE stream wedged the server");

            Assert.True(
                response.StartsWith("HTTP/1.1 204", StringComparison.Ordinal),
                $"expected the second request to be served (204), got:\n{response}");
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAny(server, Task.Delay(2000));
        }
    }
}
