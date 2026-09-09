using System.Text.Json;
using System.Text.Json.Nodes;

namespace Obscura.Cdp;

/// <summary>
/// The <c>serde_json::Value</c> accessors the Rust handlers lean on, so a
/// <c>params.get("url").and_then(|v| v.as_str())</c> chain ports one for one to
/// <c>parameters.Get("url").AsString()</c>.
/// </summary>
/// <remarks>
/// Every accessor is null-tolerant on both ends: a missing key, a JSON
/// <c>null</c>, and a value of the wrong kind all answer <c>null</c>, which is
/// what <c>Option</c> does on the Rust side. Nothing here throws.
/// </remarks>
public static class JsonExt
{
    /// <summary><c>Value::get(key)</c>: null unless this is an object holding the key.</summary>
    public static JsonNode? Get(this JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    /// <summary><c>Value::get(index)</c>: null unless this is an array with that index.</summary>
    public static JsonNode? Get(this JsonNode? node, int index) =>
        node is JsonArray array && index >= 0 && index < array.Count ? array[index] : null;

    /// <summary><c>Value::as_str</c>.</summary>
    public static string? AsString(this JsonNode? node) =>
        node is JsonValue value && node.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    /// <summary><c>Value::as_str().unwrap_or(fallback)</c>.</summary>
    public static string AsStringOr(this JsonNode? node, string fallback) =>
        node.AsString() ?? fallback;

    /// <summary><c>Value::as_bool</c>.</summary>
    public static bool? AsBool(this JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary><c>Value::as_bool().unwrap_or(fallback)</c>.</summary>
    public static bool AsBoolOr(this JsonNode? node, bool fallback) => node.AsBool() ?? fallback;

    /// <summary><c>Value::as_i64</c>: integers only, as serde has it.</summary>
    public static long? AsI64(this JsonNode? node) =>
        TryNumber(node, out var element) && element.TryGetInt64(out var value) ? value : null;

    /// <summary><c>Value::as_u64</c>: non-negative integers only.</summary>
    public static ulong? AsU64(this JsonNode? node) =>
        TryNumber(node, out var element) && element.TryGetUInt64(out var value) ? value : null;

    /// <summary><c>Value::as_f64</c>: any JSON number, integers included.</summary>
    public static double? AsF64(this JsonNode? node) =>
        TryNumber(node, out var element) && element.TryGetDouble(out var value) ? value : null;

    /// <summary><c>Value::as_array</c>. Named apart from <c>JsonNode.AsArray</c>, which throws on a non-array instead of answering null.</summary>
    public static JsonArray? AsJsonArray(this JsonNode? node) => node as JsonArray;

    /// <summary><c>Value::as_object</c>. Named apart from <c>JsonNode.AsObject</c>, which throws on a non-object instead of answering null.</summary>
    public static JsonObject? AsJsonObject(this JsonNode? node) => node as JsonObject;

    /// <summary><c>Value::is_null</c>: JSON null, or absent, which the Rust default makes the same thing.</summary>
    public static bool IsNull(this JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null;

    /// <summary>
    /// A detached copy. <see cref="JsonNode"/> is a tree with parent pointers, so
    /// the same node cannot sit in two documents at once the way a cloned
    /// <c>serde_json::Value</c> can; every reuse goes through this.
    /// </summary>
    public static JsonNode? Clone(this JsonNode? node) => node?.DeepClone();

    private static bool TryNumber(JsonNode? node, out JsonElement element)
    {
        if (node is JsonValue value && node.GetValueKind() == JsonValueKind.Number)
        {
            if (value.TryGetValue(out element))
            {
                return true;
            }

            // Backed by a CLR number rather than a parsed element: round-trip it
            // through its serde-shaped text so the same predicates apply.
            var parsed = JsonNode.Parse(CdpJson.Serialize(node));
            if (parsed is JsonValue reparsed && reparsed.TryGetValue(out element))
            {
                return true;
            }
        }

        element = default;
        return false;
    }
}
