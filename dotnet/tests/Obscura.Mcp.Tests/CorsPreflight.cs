using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Obscura.Mcp.Tests;

/// <summary>
/// Regression test for issue #175: the MCP HTTP server's OPTIONS preflight
/// response must list every header a browser MCP client may send, including
/// <c>mcp-protocol-version</c> (from the MCP spec) and <c>Authorization</c> /
/// <c>X-API-Key</c> (common in hosted deployments). Otherwise the browser blocks
/// the actual request with a CORS error.
/// </summary>
public sealed class CorsPreflightTests
{
    [Fact]
    public async Task OptionsPreflightListsRequiredBrowserHeaders()
    {
        var port = SseStreamDoesNotWedgeTests.PickFreePort();
        using var cts = new CancellationTokenSource();
        // Start the MCP HTTP server. It loops forever; we cancel it at the end of the
        // test.
        var server = Task.Run(() => Http.RunAsync("127.0.0.1", port, null, null, false, cts.Token));

        try
        {
            await SseStreamDoesNotWedgeTests.WaitForListenerAsync(port);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            var request = Encoding.UTF8.GetBytes(
                "OPTIONS /mcp HTTP/1.1\r\n"
                + "Host: 127.0.0.1\r\n"
                + "Origin: https://dashboard.example.com\r\n"
                + "Access-Control-Request-Method: POST\r\n"
                + "Access-Control-Request-Headers: Content-Type, mcp-protocol-version, Authorization\r\n"
                + "\r\n");
            await stream.WriteAsync(request);
            await stream.FlushAsync();

            var response = await SseStreamDoesNotWedgeTests.ReadOnceAsync(
                stream, TimeSpan.FromSeconds(2), "read timed out");

            Assert.True(
                response.StartsWith("HTTP/1.1 204", StringComparison.Ordinal),
                $"expected 204 No Content preflight, got:\n{response}");
            var lc = response.ToLowerInvariant();
            Assert.True(
                lc.Contains("access-control-allow-headers:", StringComparison.Ordinal),
                $"preflight must include Access-Control-Allow-Headers; got:\n{response}");
            Assert.True(
                lc.Contains("mcp-protocol-version", StringComparison.Ordinal),
                $"ACAH must list mcp-protocol-version (per MCP spec); got:\n{response}");
            Assert.True(
                lc.Contains("authorization", StringComparison.Ordinal),
                $"ACAH must list Authorization for hosted deployments; got:\n{response}");
            Assert.True(
                lc.Contains("x-api-key", StringComparison.Ordinal),
                $"ACAH must list X-API-Key for hosted deployments; got:\n{response}");
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAny(server, Task.Delay(2000));
        }
    }

    /// <summary>
    /// Regression test for the unbounded <c>Content-Length</c> allocation: a POST
    /// that advertises a huge body must be rejected with 413 <em>before</em> the
    /// server tries to allocate the buffer, rather than committing gigabytes of RAM
    /// and OOM-ing the process (unauthenticated DoS).
    /// </summary>
    [Fact]
    public async Task OversizedContentLengthIsRejected()
    {
        var port = SseStreamDoesNotWedgeTests.PickFreePort();
        using var cts = new CancellationTokenSource();
        var server = Task.Run(() => Http.RunAsync("127.0.0.1", port, null, null, false, cts.Token));

        try
        {
            await SseStreamDoesNotWedgeTests.WaitForListenerAsync(port);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            // 8 GiB advertised, zero body sent. Pre-fix the server allocates 8 GiB.
            var request = Encoding.UTF8.GetBytes(
                "POST /mcp HTTP/1.1\r\n"
                + "Host: 127.0.0.1\r\n"
                + "Content-Type: application/json\r\n"
                + "Content-Length: 8589934592\r\n"
                + "\r\n");
            await stream.WriteAsync(request);
            await stream.FlushAsync();

            var response = await SseStreamDoesNotWedgeTests.ReadOnceAsync(
                stream, TimeSpan.FromSeconds(2), "read timed out");

            Assert.True(
                response.StartsWith("HTTP/1.1 413", StringComparison.Ordinal),
                $"expected 413 Payload Too Large, got:\n{response}");
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAny(server, Task.Delay(2000));
        }
    }
}
