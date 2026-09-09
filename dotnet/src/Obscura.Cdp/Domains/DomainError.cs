namespace Obscura.Cdp.Domains;

/// <summary>
/// The <c>Err(String)</c> arm of a domain handler's <c>Result&lt;Value, String&gt;</c>.
/// </summary>
/// <remarks>
/// Raised out of the parsing helpers so they read like the Rust <c>?</c> chains, and caught at the
/// handler boundary, where it becomes <see cref="DomainResult.Err"/>. It never escapes a handler,
/// so it is public only so the ported unit tests can assert on a parser rejecting its input the way
/// the Rust tests assert <c>is_err()</c>.
/// </remarks>
public sealed class DomainError : Exception
{
    public DomainError(string message)
        : base(message)
    {
    }

    public DomainError(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The one <c>serde_json::Value</c> predicate the shared JSON accessors do not carry.
/// </summary>
/// <remarks>
/// <c>Get</c> collapses "absent" and "present but JSON null" into the same <c>null</c>, but several
/// handlers branch on exactly that difference: <c>Emulation.setDefaultBackgroundColorOverride</c>
/// answers <c>Ok(None)</c> for an absent <c>color</c> and errors for <c>color: null</c>, and
/// <c>Page.printToPDF</c> rejects <c>printBackground: null</c> while defaulting an omitted one.
/// Rust sees the split because <c>Value::get</c> returns <c>Some(Value::Null)</c>.
/// </remarks>
internal static class DomainParams
{
    /// <summary>True when the object carries the key, even with a JSON <c>null</c> value.</summary>
    internal static bool Has(System.Text.Json.Nodes.JsonNode? node, string name) =>
        node is System.Text.Json.Nodes.JsonObject obj && obj.ContainsKey(name);
}
