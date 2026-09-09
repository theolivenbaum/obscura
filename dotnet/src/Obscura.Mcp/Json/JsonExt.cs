using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Obscura.Mcp;

/// <summary>
/// The <c>serde_json::Value</c> accessors <c>lib.rs</c> leans on, so a
/// <c>args.get("url").and_then(Value::as_str)</c> chain ports one for one to
/// <c>args.Get("url").AsString()</c>.
/// </summary>
/// <remarks>
/// Every accessor is null-tolerant on both ends: a missing key, a JSON <c>null</c>
/// and a value of the wrong kind all answer <c>null</c>, which is what
/// <c>Option</c> does on the Rust side. Nothing here throws.
/// </remarks>
internal static class JsonExt
{
    /// <summary><c>Value::get(key)</c>; <c>None</c> for a non-object or a missing key.</summary>
    internal static JsonNode? Get(this JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary>
    /// Whether the key is present at all, JSON <c>null</c> included.
    /// </summary>
    /// <remarks>
    /// <see cref="Get"/> cannot tell "absent" from "present and null" because
    /// <see cref="JsonObject"/> stores a JSON null as a null reference, and
    /// <c>process_one</c>'s <c>msg.get("id").cloned()?</c> depends on the
    /// difference: over HTTP an explicit <c>"id": null</c> is a request, a missing
    /// <c>id</c> is a notification.
    /// </remarks>
    internal static bool Has(this JsonNode? node, string key) =>
        node is JsonObject obj && obj.ContainsKey(key);

    /// <summary><c>Value::get(index)</c> for arrays.</summary>
    internal static JsonNode? Get(this JsonNode? node, int index) =>
        node is JsonArray array && index >= 0 && index < array.Count ? array[index] : null;

    /// <summary><c>Value::as_str</c>.</summary>
    internal static string? AsString(this JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    /// <summary><c>Value::as_bool</c>.</summary>
    internal static bool? AsBool(this JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary><c>Value::as_array</c>. Named apart from <see cref="JsonNode.AsArray"/>, whose instance method would shadow it and throw on a non-array.</summary>
    internal static JsonArray? ArrayOrNull(this JsonNode? node) => node as JsonArray;

    /// <summary><c>Value::as_object</c>. Named apart from <see cref="JsonNode.AsObject"/> for the same reason as <see cref="ArrayOrNull"/>.</summary>
    internal static JsonObject? ObjectOrNull(this JsonNode? node) => node as JsonObject;

    /// <summary><c>Value::is_null</c>. A JSON null is a null reference in this model.</summary>
    internal static bool IsNull(this JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null;

    /// <summary><c>Value::is_object</c>.</summary>
    internal static bool IsObject(this JsonNode? node) => node is JsonObject;

    /// <summary><c>Value::as_f64</c>; every serde number answers.</summary>
    internal static double? AsF64(this JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetValue<double>(out var number) ? number : null;
    }

    /// <summary>
    /// <c>Value::as_u64</c>. serde only answers for a number it parsed as an
    /// integer, so <c>4000</c> is a <c>u64</c> and <c>4000.0</c> is not.
    /// </summary>
    internal static ulong? AsU64(this JsonNode? node)
    {
        var raw = RawNumber(node);
        return raw is not null
            && ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary><c>Value::as_i64</c>, with the same integer-literal rule as <see cref="AsU64"/>.</summary>
    internal static long? AsI64(this JsonNode? node)
    {
        var raw = RawNumber(node);
        return raw is not null
            && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// The number literal as the client wrote it, or null when the value is not a
    /// number or was built as a CLR double (which serde would hold as an <c>f64</c>,
    /// where <c>as_u64</c> answers <c>None</c>).
    /// </summary>
    private static string? RawNumber(JsonNode? node)
    {
        if (node is not JsonValue value
            || value.GetValueKind() != JsonValueKind.Number
            || !value.TryGetValue<JsonElement>(out var element))
        {
            return null;
        }

        return element.GetRawText();
    }

    /// <summary>
    /// An integer as a JSON node whose literal survives serialization, the way a
    /// <c>json!</c> literal built from a <c>usize</c> stays an integer rather than
    /// picking up ryu's trailing <c>.0</c>.
    /// </summary>
    internal static JsonNode Int(long value) =>
        JsonNode.Parse(value.ToString(CultureInfo.InvariantCulture))!;
}
