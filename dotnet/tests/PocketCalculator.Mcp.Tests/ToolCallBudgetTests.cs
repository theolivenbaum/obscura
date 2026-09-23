using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

/// <summary>
/// SECURITY.md H8: every MCP tool call runs under a budget on the active page's isolate,
/// as a CDP command does. crates/obscura-mcp bounds none; see "Known deviations" in todo.md.
/// </summary>
public sealed class ToolCallBudgetTests
{
    private static JsonNode Call(string name, JsonObject arguments) =>
        new JsonObject { ["name"] = name, ["arguments"] = arguments };

    private static string Text(RpcResponse response)
    {
        JsonNode json = response.ToJson();
        return json["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }

    [Fact]
    public async Task ARunawayEvaluateIsStoppedAndThePageStaysUsable()
    {
        McpServer.ToolCallBudget.BudgetOverrideMs.Value = 500;
        using var state = new BrowserState(null, null, false);
        await McpServer.HandleToolCallAsync(
            1, Call("browser_navigate", new JsonObject { ["url"] = "data:text/html,<p>hi</p>" }), state);

        var clock = Stopwatch.StartNew();
        string stopped = Text(await McpServer.HandleToolCallAsync(
            2, Call("browser_evaluate", new JsonObject { ["expression"] = "(() => { for (;;) {} })()" }), state));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the call ran {clock.Elapsed}");
        Assert.Equal("Error: browser_evaluate exceeded its 500ms time budget", stopped);
        Assert.Equal("2.0", Text(await McpServer.HandleToolCallAsync(
            3, Call("browser_evaluate", new JsonObject { ["expression"] = "1 + 1" }), state)));
    }

    [Fact]
    public async Task ACallWithinItsBudgetIsUnaffected()
    {
        McpServer.ToolCallBudget.BudgetOverrideMs.Value = 10_000;
        using var state = new BrowserState(null, null, false);
        await McpServer.HandleToolCallAsync(
            1, Call("browser_navigate", new JsonObject { ["url"] = "data:text/html,<p id=p>hi</p>" }), state);
        Assert.Equal("hi", Text(await McpServer.HandleToolCallAsync(
            2, Call("browser_evaluate", new JsonObject { ["expression"] = "document.getElementById('p').textContent" }), state)));
    }
}
