using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Cli.Json;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura-cli/tests/mcp_client.rs</c>: the MCP server driven
/// the way a client drives it, through <c>obscura mcp</c>'s stdio transport.
/// </summary>
/// <remarks>
/// <c>Obscura.Mcp.Tests</c> covers the server in process. What this file adds is
/// the CLI-driven path: the <c>mcp</c> subcommand actually starting a server, the
/// newline-delimited JSON-RPC framing over stdin/stdout, the initialize
/// handshake, notification silence, and the <c>browser_*</c> tools round-tripping
/// across a process boundary.
/// </remarks>
public sealed class McpClientTests
{
    private const string TestPage =
        "<!doctype html><html><head><title>Example Domain</title></head>\n"
        + "<body><h1>Example Domain</h1><p>Deterministic local MCP fixture.</p>\n"
        + "<a href=\"/more\">More information...</a></body></html>";

    private const string QueuedNavigationPage =
        "data:text/html,<title>queued-task-finished</title><p id=queued>queued</p>";

    private const string QueuedNavigationSelector = "#queued";
    private const string QueuedNavigationTitle = "queued-task-finished";
    private const string TimerTestInitialPage = "data:text/html,<title>initial</title>";
    private const int TimerTestTimeoutSeconds = 1;

    private const string InlineTextUrl =
        "data:text/html,"
        + "<html><body>"
        + "<h1><span>H</span><span>e</span><span>l</span><span>l</span><span>o</span>%20"
        + "<span>w</span><span>o</span><span>r</span><span>l</span><span>d</span><span>.</span></h1>"
        + "<p><span>Hello</span><span>,</span>%20<span>world</span><span>!</span></p>"
        + "</body></html>";

    public McpClientTests() =>
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);

    /// <summary>
    /// A loopback fixture keeps protocol tests independent of public-site bot
    /// policy, DNS, and content changes.
    /// </summary>
    private static LocalHttpServer TestPageServer() =>
        new(_ => ("text/html; charset=utf-8", Encoding.UTF8.GetBytes(TestPage)));

    // -- minimal MCP client ---------------------------------------------------

    /// <summary>
    /// The Rust <c>McpClient</c>: spawn <c>obscura mcp</c>, perform the
    /// initialize handshake, and exchange newline-delimited JSON-RPC.
    /// </summary>
    private sealed class McpClient : IDisposable
    {
        private readonly Process _child;
        private readonly StreamWriter _stdin;
        private readonly StreamReader _stdout;
        private long _nextId = 1;

        public McpClient()
        {
            var psi = new ProcessStartInfo(CliProcess.Binary!)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("mcp");
            psi.Environment["OBSCURA_ALLOW_PRIVATE_NETWORK"] = "1";
            _child = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to spawn obscura mcp");
            _stdin = _child.StandardInput;
            _stdout = _child.StandardOutput;
            // Rust sends the child's stderr to /dev/null; draining keeps the pipe
            // from filling and blocking the server.
            _ = _child.StandardError.ReadToEndAsync();

            // The initialize handshake, performed automatically.
            var id = _nextId++;
            _ = CallRaw(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "test-client",
                        ["version"] = "0.0.0",
                    },
                },
            });

            // The initialized notification: no id, no response expected.
            Notify("notifications/initialized", new JsonObject());
        }

        public void Notify(string method, JsonNode? parameters) => WriteMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        });

        public JsonObject Call(string method, JsonNode? parameters) => CallRaw(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = _nextId++,
            ["method"] = method,
            ["params"] = parameters,
        });

        public JsonObject Tool(string name, JsonNode? arguments) => Call(
            "tools/call",
            new JsonObject { ["name"] = name, ["arguments"] = arguments });

        private JsonObject CallRaw(JsonObject message)
        {
            WriteMessage(message);
            return ReadMessage();
        }

        private void WriteMessage(JsonObject message)
        {
            // MCP stdio transport: newline-delimited JSON.
            _stdin.Write(SerdeJson.ToJson(message));
            _stdin.Write('\n');
            _stdin.Flush();
        }

        private JsonObject ReadMessage()
        {
            var line = ReadLineWithin(TimeSpan.FromSeconds(120))
                ?? throw new InvalidOperationException("obscura mcp closed stdout");
            return JsonNode.Parse(line.Trim()) as JsonObject
                ?? throw new InvalidOperationException($"not a JSON object: {line}");
        }

        /// <summary>
        /// Rust blocks on <c>read_line</c>; a hung server must fail the test
        /// rather than hang the whole run.
        /// </summary>
        private string? ReadLineWithin(TimeSpan budget)
        {
            var read = _stdout.ReadLineAsync();
            if (!read.Wait(budget))
            {
                throw new TimeoutException($"obscura mcp did not reply within {budget}");
            }
            return read.Result;
        }

        public void Dispose()
        {
            try
            {
                _child.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
            {
                // Already gone.
            }
            _child.WaitForExit(10_000);
            _child.Dispose();
        }
    }

    /// <summary>The first content text of a <c>tools/call</c> response.</summary>
    private static string ContentText(JsonObject response) =>
        response["result"]?["content"]?[0]?["text"] is { } text
            && text.GetValueKind() == JsonValueKind.String
                ? text.GetValue<string>()
                : string.Empty;

    private static bool IsAbsent(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null;

    // -- tests ----------------------------------------------------------------

    [Fact]
    public void Test_initialize()
    {
        using var c = new McpClient();
        // A second initialize should still return a valid response.
        var response = c.Call("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
        });
        Assert.Equal("2024-11-05", response["result"]?["protocolVersion"]?.GetValue<string>());
        Assert.Equal("obscura-mcp", response["result"]?["serverInfo"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public void Snapshot_preserves_whitespace_between_inline_spans()
    {
        using var client = new McpClient();
        var navigation = client.Tool("browser_navigate", new JsonObject { ["url"] = InlineTextUrl });
        Assert.True(IsAbsent(navigation["error"]), $"navigation failed: {navigation}");

        var snapshot = client.Tool("browser_snapshot", new JsonObject());
        var text = ContentText(snapshot);
        Assert.Contains("Hello world.\n\nHello, world!", text, StringComparison.Ordinal);
        Assert.DoesNotContain("H e l l o", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_ping()
    {
        using var c = new McpClient();
        var response = c.Call("ping", new JsonObject());
        Assert.True(IsAbsent(response["error"]), "ping should not error");
        Assert.Equal("{}", SerdeJson.ToJson(response["result"]));
    }

    [Fact]
    public void Test_tools_list()
    {
        using var c = new McpClient();
        var response = c.Call("tools/list", new JsonObject());
        var tools = Assert.IsType<JsonArray>(response["result"]?["tools"]);
        Assert.NotEmpty(tools);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            names.Add(tool!["name"]!.GetValue<string>());
        }
        foreach (var expected in new[]
        {
            "browser_navigate",
            "browser_snapshot",
            "browser_click",
            "browser_fill",
            "browser_type",
            "browser_press_key",
            "browser_select_option",
            "browser_evaluate",
            "browser_wait_for",
            "browser_network_requests",
            "browser_console_messages",
            "browser_close",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void Test_resources_list()
    {
        using var c = new McpClient();
        var response = c.Call("resources/list", new JsonObject());
        Assert.True(IsAbsent(response["error"]));
        Assert.Equal("[]", SerdeJson.ToJson(response["result"]?["resources"]));
    }

    [Fact]
    public void Test_prompts_list()
    {
        using var c = new McpClient();
        var response = c.Call("prompts/list", new JsonObject());
        Assert.True(IsAbsent(response["error"]));
        Assert.Equal("[]", SerdeJson.ToJson(response["result"]?["prompts"]));
    }

    [Fact]
    public void Test_unknown_method_returns_error()
    {
        using var c = new McpClient();
        var response = c.Call("nonexistent/method", new JsonObject());
        Assert.Equal(-32601.0, response["error"]?["code"]?.GetValue<double>());
    }

    [Fact]
    public void Test_notifications_are_silent()
    {
        // Notifications must not produce a response; the next real call should
        // succeed. If the server accidentally replied to a notification, the
        // next read would consume that stray response and the ping result would
        // be mismatched.
        using var c = new McpClient();
        c.Notify("notifications/initialized", new JsonObject());
        c.Notify("some/other_notification", new JsonObject { ["x"] = 1 });
        var response = c.Call("ping", new JsonObject());
        Assert.True(IsAbsent(response["error"]));
    }

    [Fact]
    public void Test_navigate_and_snapshot()
    {
        using var server = TestPageServer();
        using var c = new McpClient();

        var nav = c.Tool("browser_navigate", new JsonObject { ["url"] = $"{server.Base}/" });
        Assert.True(IsAbsent(nav["result"]?["isError"]), $"navigate failed: {nav}");
        Assert.Contains("127.0.0.1", ContentText(nav), StringComparison.Ordinal);

        var snap = c.Tool("browser_snapshot", new JsonObject());
        Assert.True(IsAbsent(snap["result"]?["isError"]), $"snapshot failed: {snap}");
        var text = ContentText(snap);
        Assert.Contains("Example Domain", text, StringComparison.Ordinal);
        Assert.Contains("URL:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_evaluate()
    {
        using var server = TestPageServer();
        using var c = new McpClient();
        c.Tool("browser_navigate", new JsonObject { ["url"] = $"{server.Base}/" });

        var response = c.Tool(
            "browser_evaluate", new JsonObject { ["expression"] = "document.title" });
        Assert.Equal("Example Domain", ContentText(response));
    }

    [Fact]
    public void Test_evaluate_math()
    {
        using var server = TestPageServer();
        using var c = new McpClient();
        c.Tool("browser_navigate", new JsonObject { ["url"] = $"{server.Base}/" });

        var response = c.Tool("browser_evaluate", new JsonObject { ["expression"] = "1 + 2" });
        var text = ContentText(response);
        // V8 serialises integer results as floats ("3" or "3.0" depending on context).
        Assert.True(text is "3" or "3.0", $"unexpected result: {text}");
    }

    [Fact]
    public void Test_wait_drives_timer_and_queued_navigation()
    {
        using var c = new McpClient();
        c.Tool("browser_navigate", new JsonObject { ["url"] = TimerTestInitialPage });
        var target = SerdeJson.StringLiteral(QueuedNavigationPage);
        c.Tool("browser_evaluate", new JsonObject
        {
            ["expression"] = $"setTimeout(() => {{ location.href = {target}; }}, 0)",
        });

        var waited = c.Tool("browser_wait_for", new JsonObject
        {
            ["selector"] = QueuedNavigationSelector,
            ["timeout"] = TimerTestTimeoutSeconds,
        });

        Assert.True(IsAbsent(waited["result"]?["isError"]), $"wait failed: {waited}");
        var title = c.Tool(
            "browser_evaluate", new JsonObject { ["expression"] = "document.title" });
        Assert.Equal(QueuedNavigationTitle, ContentText(title));
    }

    [Fact]
    public void Test_wait_for_selector()
    {
        using var server = TestPageServer();
        using var c = new McpClient();
        c.Tool("browser_navigate", new JsonObject { ["url"] = $"{server.Base}/" });

        var response = c.Tool(
            "browser_wait_for", new JsonObject { ["selector"] = "h1", ["timeout"] = 5 });
        Assert.True(IsAbsent(response["result"]?["isError"]), $"wait_for failed: {response}");
        Assert.Contains("Found", ContentText(response), StringComparison.Ordinal);
    }

    [Fact]
    public void Test_wait_for_timeout()
    {
        using var server = TestPageServer();
        using var c = new McpClient();
        c.Tool("browser_navigate", new JsonObject { ["url"] = $"{server.Base}/" });

        var response = c.Tool(
            "browser_wait_for",
            new JsonObject { ["selector"] = "#does-not-exist", ["timeout"] = 1 });
        Assert.Equal(JsonValueKind.True, response["result"]?["isError"]?.GetValueKind());
        Assert.Contains("Timeout", ContentText(response), StringComparison.Ordinal);
    }

    [Fact]
    public void Test_navigate_missing_url_returns_error()
    {
        using var c = new McpClient();
        var response = c.Tool("browser_navigate", new JsonObject());
        Assert.Equal(JsonValueKind.True, response["result"]?["isError"]?.GetValueKind());
    }

    [Fact]
    public void Test_unknown_tool_returns_error()
    {
        using var c = new McpClient();
        var response = c.Tool("browser_does_not_exist", new JsonObject());
        Assert.Equal(JsonValueKind.True, response["result"]?["isError"]?.GetValueKind());
    }

    [Fact]
    public void Test_network_requests()
    {
        using var server = TestPageServer();
        using var c = new McpClient();
        c.Tool("browser_navigate", new JsonObject { ["url"] = $"{server.Base}/" });

        var response = c.Tool("browser_network_requests", new JsonObject());
        var text = ContentText(response);
        Assert.True(
            text.Contains("127.0.0.1", StringComparison.Ordinal)
                || text.Contains("No network", StringComparison.Ordinal),
            $"unexpected: {text}");
    }

    [Fact]
    public void Test_close_resets_state()
    {
        using var server = TestPageServer();
        using var c = new McpClient();
        c.Tool("browser_navigate", new JsonObject { ["url"] = $"{server.Base}/" });
        var close = c.Tool("browser_close", new JsonObject());
        Assert.True(IsAbsent(close["result"]?["isError"]));

        // After close, snapshot should return the empty/default page, not fail.
        var snap = c.Tool("browser_snapshot", new JsonObject());
        Assert.True(IsAbsent(snap["result"]?["isError"]));
    }
}
