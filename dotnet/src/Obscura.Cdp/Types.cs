using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Obscura.Cdp;

/// <summary>One inbound CDP frame.</summary>
/// <remarks>
/// Mirrors the serde contract of <c>types.rs</c>: <c>id</c> and <c>method</c>
/// are required, <c>params</c> defaults to JSON null when absent, and
/// <c>sessionId</c> is optional. Anything that would fail
/// <c>serde_json::from_str::&lt;CdpRequest&gt;</c> fails <see cref="TryParse"/>,
/// because several call sites branch on exactly that.
/// </remarks>
public sealed class CdpRequest
{
    public required ulong Id { get; init; }

    public required string Method { get; init; }

    /// <summary>Null stands for serde's <c>Value::Null</c> default.</summary>
    public JsonNode? Params { get; init; }

    public string? SessionId { get; init; }

    /// <summary>
    /// Parse a frame, returning null where <c>serde_json::from_str</c> returns
    /// <c>Err</c>. <paramref name="error"/> carries a serde-shaped reason for the
    /// warn-level logs the Rust code emits.
    /// </summary>
    public static CdpRequest? TryParse(string text, out string? error)
    {
        var root = CdpJson.Parse(text, out var wellFormed);
        if (!wellFormed)
        {
            error = "expected value";
            return null;
        }

        if (root is not JsonObject obj)
        {
            error = "invalid type: expected struct CdpRequest";
            return null;
        }

        if (!obj.TryGetPropertyValue("id", out var idNode) || idNode.AsU64() is not { } id)
        {
            error = "missing field `id`";
            return null;
        }

        if (!obj.TryGetPropertyValue("method", out var methodNode) ||
            methodNode.AsString() is not { } method)
        {
            error = "missing field `method`";
            return null;
        }

        string? sessionId = null;
        if (obj.TryGetPropertyValue("sessionId", out var sessionNode) && !sessionNode.IsNull())
        {
            sessionId = sessionNode.AsString();
            if (sessionId is null)
            {
                error = "invalid type: expected a string for `sessionId`";
                return null;
            }
        }

        JsonNode? parameters = null;
        if (obj.TryGetPropertyValue("params", out var paramsNode))
        {
            parameters = paramsNode?.DeepClone();
        }

        error = null;
        return new CdpRequest
        {
            Id = id,
            Method = method,
            Params = parameters,
            SessionId = sessionId,
        };
    }

    /// <summary>Parse, discarding the reason.</summary>
    public static CdpRequest? TryParse(string text) => TryParse(text, out _);
}

/// <summary>The <c>error</c> member of a CDP response.</summary>
public sealed class CdpError
{
    public required long Code { get; init; }

    public required string Message { get; init; }
}

/// <summary>One outbound CDP command response.</summary>
public sealed class CdpResponse
{
    public required ulong Id { get; init; }

    /// <summary>
    /// Only meaningful when <see cref="HasResult"/> is set: serde's
    /// <c>Option&lt;Value&gt;</c> distinguishes an absent result from a present
    /// JSON <c>null</c>, and <see cref="JsonNode"/> alone cannot.
    /// </summary>
    public JsonNode? Result { get; init; }

    public bool HasResult { get; init; }

    public CdpError? Error { get; init; }

    public string? SessionId { get; init; }

    public static CdpResponse Success(ulong id, JsonNode? result, string? sessionId) => new()
    {
        Id = id,
        Result = result,
        HasResult = true,
        Error = null,
        SessionId = sessionId,
    };

    public static CdpResponse Failure(ulong id, long code, string message, string? sessionId) => new()
    {
        Id = id,
        Result = null,
        HasResult = false,
        Error = new CdpError { Code = code, Message = message },
        SessionId = sessionId,
    };

    /// <summary>
    /// The wire form, in serde's field-declaration order (<c>id</c>,
    /// <c>result</c>, <c>error</c>, <c>sessionId</c>) with the <c>None</c> members
    /// skipped.
    /// </summary>
    public string ToJson()
    {
        var sb = new StringBuilder(64);
        sb.Append("{\"id\":");
        sb.Append(Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (HasResult)
        {
            sb.Append(",\"result\":");
            CdpJson.Write(sb, Result);
        }

        if (Error is { } error)
        {
            sb.Append(",\"error\":{\"code\":");
            sb.Append(error.Code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"message\":");
            CdpJson.AppendString(sb, error.Message);
            sb.Append('}');
        }

        if (SessionId is { } sessionId)
        {
            sb.Append(",\"sessionId\":");
            CdpJson.AppendString(sb, sessionId);
        }

        sb.Append('}');
        return sb.ToString();
    }
}

/// <summary>One outbound CDP event.</summary>
public sealed class CdpEvent
{
    public required string Method { get; init; }

    public required JsonNode? Params { get; init; }

    public string? SessionId { get; set; }

    public static CdpEvent New(string method, JsonNode? parameters) => new()
    {
        Method = method,
        Params = parameters,
        SessionId = null,
    };

    public static CdpEvent WithSession(string method, JsonNode? parameters, string sessionId) => new()
    {
        Method = method,
        Params = parameters,
        SessionId = sessionId,
    };

    /// <summary>The wire form, in serde's field-declaration order.</summary>
    public string ToJson()
    {
        var sb = new StringBuilder(64);
        sb.Append("{\"method\":");
        CdpJson.AppendString(sb, Method);
        sb.Append(",\"params\":");
        CdpJson.Write(sb, Params);
        if (SessionId is { } sessionId)
        {
            sb.Append(",\"sessionId\":");
            CdpJson.AppendString(sb, sessionId);
        }

        sb.Append('}');
        return sb.ToString();
    }
}

/// <summary>
/// A domain handler's answer: Rust's <c>Result&lt;Value, String&gt;</c>.
/// </summary>
/// <remarks>
/// <c>dispatch</c> branches on this rather than on an exception because the
/// error is an ordinary protocol outcome (an unsupported method, a missing
/// session) that becomes a <c>-32601</c> response, not a fault. A handler that
/// throws anyway is caught by the dispatcher and folded into the same shape.
/// </remarks>
public readonly record struct DomainResult(JsonNode? Value, string? Error)
{
    public bool IsOk => Error is null;

    public static DomainResult Ok(JsonNode? value) => new(value, null);

    /// <summary><c>Ok(json!({}))</c>, which most handlers answer with.</summary>
    public static DomainResult Empty() => new(new JsonObject(), null);

    public static DomainResult Err(string message) => new(null, message);
}
