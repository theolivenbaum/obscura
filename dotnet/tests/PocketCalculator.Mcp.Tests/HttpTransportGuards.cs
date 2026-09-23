using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

/// <summary>
/// End-to-end checks of the HTTP transport's guards from upstream 04418a5: the
/// bearer token, Content-Type enforcement, the origin allowlist's CORS echo, the
/// head limits, the batch and id limits and the startup refusal. The Rust tree
/// tests these as unit facts on the helpers (ported in <see cref="HttpHardeningTests"/>);
/// these drive a real listener so the status lines and bodies are pinned too.
/// </summary>
public sealed class HttpTransportGuardsTests
{
    private const string Token = "01234567890123456789012345678901";

    private const string ToolsList = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    private static async Task<string> WithServerAsync(
        string? allowedOrigins,
        string? token,
        string request)
    {
        var port = SseStreamDoesNotWedgeTests.PickFreePort();
        using var cts = new CancellationTokenSource();
        var server = Task.Run(() => Http.RunAsync(
            "127.0.0.1", port, null, null, false, allowedOrigins, token, cts.Token));
        try
        {
            await SseStreamDoesNotWedgeTests.WaitForListenerAsync(port);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
            await stream.FlushAsync();
            return await ReadResponseAsync(stream, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAny(server, Task.Delay(2000));
        }
    }

    /// <summary>Read until the peer closes, the declared body has arrived, or the timeout.</summary>
    private static async Task<string> ReadResponseAsync(NetworkStream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var received = new List<byte>();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, cts.Token);
                if (n == 0)
                {
                    break;
                }

                received.AddRange(buffer.AsSpan(0, n));
                var text = Encoding.UTF8.GetString([.. received]);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }

                var lengthAt = text.IndexOf("Content-Length: ", StringComparison.Ordinal);
                if (lengthAt < 0 || lengthAt > end)
                {
                    break;
                }

                var lineEnd = text.IndexOf("\r\n", lengthAt, StringComparison.Ordinal);
                var length = int.Parse(text[(lengthAt + "Content-Length: ".Length)..lineEnd]);
                if (Encoding.UTF8.GetByteCount(text[(end + 4)..]) >= length)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }

        return Encoding.UTF8.GetString([.. received]);
    }

    private static string Post(string body, string extraHeaders = "", string contentType = "application/json") =>
        "POST /mcp HTTP/1.1\r\n"
        + "Host: 127.0.0.1\r\n"
        + $"Content-Type: {contentType}\r\n"
        + extraHeaders
        + $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
        + "\r\n"
        + body;

    [Fact]
    public async Task ConfiguredTokenIsRequired()
    {
        var refused = await WithServerAsync(null, Token, Post(ToolsList));
        Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", refused, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n{\"error\":\"authentication required\"}", refused, StringComparison.Ordinal);

        var wrong = await WithServerAsync(null, Token, Post(ToolsList, "Authorization: Bearer nope\r\n"));
        Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", wrong, StringComparison.Ordinal);

        var accepted = await WithServerAsync(null, Token, Post(ToolsList, $"Authorization: Bearer {Token}\r\n"));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", accepted, StringComparison.Ordinal);
        Assert.Contains("\"tools\"", accepted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTokenOnLoopbackServesNativeClients()
    {
        var accepted = await WithServerAsync(null, null, Post(ToolsList));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", accepted, StringComparison.Ordinal);
        Assert.DoesNotContain("Access-Control-Allow-Origin", accepted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonJsonContentTypeIsRefused()
    {
        // The CORS "simple request" shape a web page can send without a preflight.
        var refused = await WithServerAsync(null, null, Post(ToolsList, contentType: "text/plain"));
        Assert.StartsWith("HTTP/1.1 415 Unsupported Media Type\r\n", refused, StringComparison.Ordinal);
        Assert.EndsWith(
            "\r\n\r\n{\"error\":\"Content-Type must be application/json\"}", refused, StringComparison.Ordinal);

        var withParameters = await WithServerAsync(
            null, null, Post(ToolsList, contentType: "Application/JSON; charset=utf-8"));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", withParameters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserOriginIsRefusedWithoutAnAllowlist()
    {
        var refused = await WithServerAsync(
            null, null, Post(ToolsList, "Origin: https://evil.example\r\n"));
        Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", refused, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n{\"error\":\"origin not allowed\"}", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowlistedOriginIsEchoedNeverWildcarded()
    {
        const string list = "https://app.example.com";
        var accepted = await WithServerAsync(
            list, null, Post(ToolsList, "Origin: https://app.example.com\r\n"));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", accepted, StringComparison.Ordinal);
        Assert.Contains(
            "Access-Control-Allow-Origin: https://app.example.com\r\nVary: Origin\r\n",
            accepted,
            StringComparison.Ordinal);

        var preflight = await WithServerAsync(
            list,
            Token,
            "OPTIONS /mcp HTTP/1.1\r\nHost: 127.0.0.1\r\nOrigin: https://app.example.com\r\n"
            + "Access-Control-Request-Method: POST\r\n\r\n");
        // A preflight carries no credentials, so it is answered without the token.
        Assert.StartsWith("HTTP/1.1 204 No Content\r\n", preflight, StringComparison.Ordinal);
        Assert.Contains(
            "Access-Control-Allow-Headers: Content-Type, Authorization, mcp-protocol-version\r\n",
            preflight,
            StringComparison.Ordinal);
        Assert.DoesNotContain("X-API-Key", preflight, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedRequestLineClosesTheConnection()
    {
        var line = "GET /" + new string('a', Http.MaxRequestLineBytes) + " HTTP/1.1\r\n\r\n";
        var response = await WithServerAsync(null, null, line);
        Assert.Equal(string.Empty, response);
    }

    [Fact]
    public async Task OversizedHeaderBlockClosesTheConnection()
    {
        var header = "X-Filler: " + new string('a', 8000) + "\r\n";
        var request = "POST /mcp HTTP/1.1\r\n" + string.Concat(Enumerable.Repeat(header, 9)) + "\r\n";
        var response = await WithServerAsync(null, null, request);
        Assert.Equal(string.Empty, response);
    }

    [Fact]
    public async Task BatchAndIdLimitsAnswerInvalidRequest()
    {
        using var state = new BrowserState(null, null, false);
        const string invalid = "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32600,\"message\":\"Invalid Request\"}}";

        async Task<string> Run(string body) =>
            McpJson.Serialize(await Http.ProcessBodyAsync(Encoding.UTF8.GetBytes(body), state));

        Assert.Equal(invalid, await Run("[]"));
        var tooMany = "[" + string.Join(',', Enumerable.Repeat(ToolsList, Http.MaxBatchItems + 1)) + "]";
        Assert.Equal(invalid, await Run(tooMany));
        var atCap = await Run("[" + string.Join(',', Enumerable.Repeat(ToolsList, Http.MaxBatchItems)) + "]");
        Assert.Equal(Http.MaxBatchItems, ((JsonArray)JsonNode.Parse(atCap)!).Count);

        Assert.Equal(invalid, await Run("{\"jsonrpc\":\"2.0\",\"id\":{\"a\":1},\"method\":\"tools/list\"}"));
        Assert.Equal(invalid, await Run("{\"jsonrpc\":\"2.0\",\"id\":[1],\"method\":\"tools/list\"}"));
        var longId = new string('x', Http.MaxIdBytes);
        Assert.Equal(invalid, await Run($"{{\"jsonrpc\":\"2.0\",\"id\":\"{longId}\",\"method\":\"tools/list\"}}"));
        Assert.Contains("\"tools\"", await Run(ToolsList), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonLoopbackBindWithoutTokenIsRefused()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Http.RunAsync(
            "0.0.0.0", 0, null, null, false, null, null, TestContext.Current.CancellationToken));
        Assert.Equal(
            "refusing to expose MCP without authentication; set POCKETCALCULATOR_MCP_TOKEN to at least 32 bytes",
            error.Message);

        var shortToken = await Assert.ThrowsAsync<InvalidOperationException>(() => Http.RunAsync(
            "127.0.0.1", 0, null, null, false, null, "short", TestContext.Current.CancellationToken));
        Assert.Equal("POCKETCALCULATOR_MCP_TOKEN must be at least 32 bytes", shortToken.Message);
    }
}
