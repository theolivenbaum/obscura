using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Mcp.Tests;

/// <summary>
/// Differential guard, port-specific: the bytes <c>tools/list</c> puts on the wire
/// must equal the bytes <c>serde_json</c> writes for the <c>json!</c> literals in
/// <c>crates/obscura-mcp/src/lib.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The tool names, argument schemas and result shapes are the contract an MCP
/// client is written against, and <c>ToolSchemas</c> is a transcription of the Rust
/// literal, so the failure mode this catches is a transcription drifting from the
/// specification - or the writer regressing on key order or number formatting.
/// </para>
/// <para>
/// Reading the Rust source directly is what makes it differential; it is compared
/// after a parse, so only the JSON matters, not the Rust file's own formatting.
/// The check skips itself when the Rust tree is not on disk, the same way the
/// parity suite skips without <c>OBSCURA_RUST_BIN</c>.
/// </para>
/// </remarks>
public sealed class ToolListMatchesRustSourceTests
{
    [Fact]
    public void ToolListBytesMatchTheRustJsonLiterals()
    {
        var source = FindRustSource();
        Assert.SkipWhen(source is null, "crates/obscura-mcp/src/lib.rs is not present in this checkout");

        var rust = File.ReadAllText(source!);
        var expected = new JsonObject { ["tools"] = RustToolArray(rust) };
        var actual = McpServer.HandleToolsList(JsonNode.Parse("1")).Result;

        Assert.Equal(McpJson.Serialize(expected), McpJson.Serialize(actual));
    }

    private static JsonArray RustToolArray(string rust)
    {
        const string baseOpen = "let mut tools = json!(";
        const string baseClose = "]).as_array().cloned()";
        var start = rust.IndexOf(baseOpen, StringComparison.Ordinal) + baseOpen.Length;
        var end = rust.IndexOf(baseClose, StringComparison.Ordinal) + 1;
        var tools = (JsonArray)JsonNode.Parse(rust[start..end])!;

        // `tools.extend([ json!({...}), json!({...}), ]);` - the macro wrappers and
        // the trailing comma are Rust syntax around otherwise plain JSON.
        const string renderOpen = "tools.extend([";
        const string renderClose = "        ]);";
        var renderStart = rust.IndexOf(renderOpen, StringComparison.Ordinal) + renderOpen.Length;
        var renderEnd = rust.IndexOf(renderClose, renderStart, StringComparison.Ordinal);
        var renderText = rust[renderStart..renderEnd]
            .Replace("json!(", string.Empty, StringComparison.Ordinal)
            .Replace("}),", "},", StringComparison.Ordinal)
            .TrimEnd()
            .TrimEnd(',');
        foreach (var tool in (JsonArray)JsonNode.Parse($"[{renderText}]")!)
        {
            tools.Add(tool!.DeepClone());
        }

        return tools;
    }

    private static string? FindRustSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "crates", "obscura-mcp", "src", "lib.rs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
