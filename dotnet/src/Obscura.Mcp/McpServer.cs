using System.Text;
using System.Text.Json.Nodes;

namespace Obscura.Mcp;

/// <summary>
/// The MCP server: JSON-RPC framing, the method table, and the stdio transport.
/// </summary>
/// <remarks>
/// The JSON-RPC framing is written out here rather than delegated to an SDK
/// because the wire format is what parity depends on: the Rust crate frames the
/// messages itself, and the two have to agree byte for byte.
/// </remarks>
public static class McpServer
{
    /// <summary>
    /// Cap on text returned to the agent unless the caller passes a larger
    /// <c>max_chars</c>. Agents waste context on multi-KB raw page dumps; this keeps
    /// a single tool call from burning a window's worth of tokens. Override via tool
    /// args.
    /// </summary>
    internal const int DefaultTextLimit = 4000;

    internal static async Task<RpcResponse> DispatchAsync(
        string method,
        JsonNode? id,
        JsonNode? parameters,
        BrowserState state) => method switch
        {
            "initialize" => HandleInitialize(id, parameters),
            "ping" => RpcResponse.Ok(id, new JsonObject()),
            "tools/list" => HandleToolsList(id),
            "tools/call" => await HandleToolCallAsync(id, parameters, state).ConfigureAwait(false),
            "resources/list" => RpcResponse.Ok(id, new JsonObject { ["resources"] = new JsonArray() }),
            "prompts/list" => RpcResponse.Ok(id, new JsonObject { ["prompts"] = new JsonArray() }),
            _ => RpcResponse.Err(id, -32601, $"Unknown method: {method}"),
        };

    /// <summary>
    /// The MCP stdio transport: newline-delimited JSON, one message per line.
    /// </summary>
    public static async Task RunAsync(
        string? proxy,
        string? userAgent,
        bool stealth,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(
            Console.OpenStandardInput(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        await using var writer = new StreamWriter(
            Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };

        using var state = new BrowserState(proxy, userAgent, stealth);
        var runtimePumpArmed = false;
        Task<string?>? pendingRead = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            pendingRead ??= reader.ReadLineAsync(cancellationToken).AsTask();

            // The Rust loop is a `biased` select of read_line against one pump turn,
            // so a line that is already available always wins. A .NET read cannot be
            // cancelled and restarted the way dropping a future cancels it, so the
            // equivalent is: serve a completed read first, otherwise run exactly one
            // pump turn and look again.
            if (runtimePumpArmed && !pendingRead.IsCompleted)
            {
                try
                {
                    var reachedIdle = await state.AdvanceActivePageTasksAsync().ConfigureAwait(false);
                    runtimePumpArmed = !reachedIdle;
                }
                catch (ToolException error)
                {
                    runtimePumpArmed = false;
                    await Console.Error.WriteLineAsync($"MCP page task failed: {error.Message}")
                        .ConfigureAwait(false);
                }

                continue;
            }

            var line = await pendingRead.ConfigureAwait(false);
            pendingRead = null;
            if (line is null)
            {
                return;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (RpcMessage.TryParse(trimmed) is not { } message)
            {
                continue;
            }

            // Notifications (no id) need no response.
            if (message.Id is null)
            {
                continue;
            }

            var response = await DispatchAsync(message.Method, message.Id, message.Params, state)
                .ConfigureAwait(false);
            runtimePumpArmed = state.HasActivePageRuntime();

            await writer.WriteAsync(McpJson.Serialize(response.ToJson())).ConfigureAwait(false);
            await writer.WriteAsync('\n').ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static RpcResponse HandleInitialize(JsonNode? id, JsonNode? parameters)
    {
        _ = parameters.Get("protocolVersion").AsString() ?? string.Empty;
        return RpcResponse.Ok(id, new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "obscura-mcp",
                // Same build version the CLI reports (tag-derived at release time),
                // so MCP clients see the version the binary was actually cut from.
                ["version"] = BuildVersion.Value,
            },
        });
    }

    internal static RpcResponse HandleToolsList(JsonNode? id) =>
        RpcResponse.Ok(id, new JsonObject { ["tools"] = ToolSchemas.All() });

    internal static async Task<RpcResponse> HandleToolCallAsync(
        JsonNode? id,
        JsonNode? parameters,
        BrowserState state)
    {
        if (parameters.Get("name").AsString() is not { } name)
        {
            return RpcResponse.Err(id, -32602, "Missing tool name");
        }

        var args = parameters.Get("arguments");

        // The render tools answer with binary MCP content rather than text, so they
        // are resolved before the text table.
        if (name is "browser_screenshot" or "browser_pdf")
        {
            try
            {
                var content = name == "browser_screenshot"
                    ? await Tools.ScreenshotAsync(args, state).ConfigureAwait(false)
                    : await Tools.PdfAsync(args, state).ConfigureAwait(false);
                return RpcResponse.Ok(id, new JsonObject { ["content"] = new JsonArray(content) });
            }
            catch (ToolException error)
            {
                return RpcResponse.Ok(id, ErrorContent(error.Message));
            }
        }

        try
        {
            var content = await CallTextToolAsync(name, args, state).ConfigureAwait(false);
            return RpcResponse.Ok(id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = content,
                }),
            });
        }
        catch (ToolException error)
        {
            return RpcResponse.Ok(id, ErrorContent(error.Message));
        }
    }

    private static JsonObject ErrorContent(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = $"Error: {message}",
        }),
        ["isError"] = true,
    };

    private static async Task<string> CallTextToolAsync(
        string name,
        JsonNode? args,
        BrowserState state) => name switch
        {
            "browser_navigate" => await Tools.NavigateAsync(args, state).ConfigureAwait(false),
            "browser_snapshot" => Tools.Snapshot(args, state),
            "browser_click" => await Tools.ClickAsync(args, state).ConfigureAwait(false),
            "browser_fill" => await Tools.FillAsync(args, state).ConfigureAwait(false),
            "browser_type" => await Tools.TypeAsync(args, state).ConfigureAwait(false),
            "browser_press_key" => await Tools.PressKeyAsync(args, state).ConfigureAwait(false),
            "browser_select_option" => Tools.SelectOption(args, state),
            "browser_evaluate" => await Tools.EvaluateAsync(args, state).ConfigureAwait(false),
            "browser_wait_for" => await Tools.WaitForAsync(args, state).ConfigureAwait(false),
            "browser_network_requests" => Tools.NetworkRequests(state),
            "browser_console_messages" => Tools.ConsoleMessages(state),
            "browser_close" => Tools.Close(state),
            // Tier 1 agent-UX additions
            "browser_markdown" => Tools.Markdown(args, state),
            "browser_links" => Tools.Links(args, state),
            "browser_interactive_elements" => Tools.InteractiveElements(args, state),
            "browser_back" => await Tools.BackAsync(state).ConfigureAwait(false),
            "browser_forward" => await Tools.ForwardAsync(state).ConfigureAwait(false),
            "browser_reload" => await Tools.ReloadAsync(state).ConfigureAwait(false),
            "browser_get_cookies" => Tools.GetCookies(args, state),
            "browser_set_cookie" => Tools.SetCookie(args, state),
            "browser_clear_cookies" => Tools.ClearCookies(state),
            "browser_wait_for_text" => await Tools.WaitForTextAsync(args, state).ConfigureAwait(false),
            // Tier 2 agent-UX additions
            "browser_detect_forms" => Tools.DetectForms(state),
            "browser_fill_form" => Tools.FillForm(args, state),
            "browser_scroll" => Tools.Scroll(args, state),
            "browser_get_attribute" => Tools.GetAttribute(args, state),
            "browser_count" => Tools.Count(args, state),
            "browser_extract" => Tools.Extract(args, state),
            "browser_tab_new" => await Tools.TabNewAsync(args, state).ConfigureAwait(false),
            "browser_tab_list" => Tools.TabList(state),
            "browser_tab_switch" => Tools.TabSwitch(args, state),
            "browser_tab_close" => Tools.TabClose(args, state),
            "browser_search" => Tools.Search(args, state),
            "browser_storage_state" => Tools.StorageState(state),
            "browser_set_storage_state" => Tools.SetStorageState(args, state),
            _ => throw new ToolException($"Unknown tool: {name}"),
        };
}
