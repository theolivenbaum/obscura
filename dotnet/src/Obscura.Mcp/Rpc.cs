using System.Text.Json.Nodes;

namespace Obscura.Mcp;

/// <summary>
/// A tool's <c>Err(String)</c> arm. <c>handle_tool_call</c> turns it into an MCP
/// error result rather than letting it escape, so this never leaves the server.
/// </summary>
internal sealed class ToolException(string message) : Exception(message);

/// <summary>One inbound JSON-RPC message, as <c>RpcMessage</c> deserializes it.</summary>
internal sealed class RpcMessage
{
    private RpcMessage(JsonNode? id, string method, JsonNode? parameters)
    {
        Id = id;
        Method = method;
        Params = parameters;
    }

    /// <summary>
    /// The request id, or null for a notification. Note that
    /// <c>#[serde(default)] id: Option&lt;Value&gt;</c> also maps an explicit
    /// <c>"id": null</c> to <c>None</c>, so a null id is a notification on this
    /// path (the HTTP path, which reads the raw <c>Value</c>, differs).
    /// </summary>
    internal JsonNode? Id { get; }

    internal string Method { get; }

    /// <summary><c>#[serde(default)] params: Value</c>, i.e. <c>Value::Null</c> when absent.</summary>
    internal JsonNode? Params { get; }

    /// <summary>
    /// <c>serde_json::from_str::&lt;RpcMessage&gt;</c>: a missing or wrongly typed
    /// <c>jsonrpc</c> / <c>method</c> is a deserialization failure, which the stdio
    /// loop drops.
    /// </summary>
    internal static RpcMessage? TryParse(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        if (obj.Get("jsonrpc").AsString() is null || obj.Get("method").AsString() is not { } method)
        {
            return null;
        }

        var id = obj.Get("id");
        var parameters = obj.TryGetPropertyValue("params", out var raw) ? raw : null;
        return new RpcMessage(id, method, parameters);
    }
}

/// <summary>One JSON-RPC response frame.</summary>
/// <remarks>
/// <c>result</c> and <c>error</c> are skipped when absent
/// (<c>skip_serializing_if = "Option::is_none"</c>), and the field order is the
/// struct's: <c>jsonrpc</c>, <c>id</c>, <c>result</c>, <c>error</c>.
/// </remarks>
internal sealed class RpcResponse
{
    private RpcResponse(JsonNode? id, JsonNode? result, RpcError? error)
    {
        Id = id;
        Result = result;
        Error = error;
    }

    internal JsonNode? Id { get; }

    internal JsonNode? Result { get; }

    internal RpcError? Error { get; }

    internal static RpcResponse Ok(JsonNode? id, JsonNode? result) => new(id, result, null);

    internal static RpcResponse Err(JsonNode? id, int code, string message) =>
        new(id, null, new RpcError(code, message));

    internal JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Id?.DeepClone(),
        };
        if (Result is not null)
        {
            json["result"] = Result.DeepClone();
        }

        if (Error is { } error)
        {
            json["error"] = new JsonObject
            {
                ["code"] = JsonExt.Int(error.Code),
                ["message"] = error.Message,
            };
        }

        return json;
    }
}

internal sealed record RpcError(int Code, string Message);
