using System.Text.Json.Nodes;

namespace Obscura.Js.Runtime;

/// <summary>
/// A CDP <c>Runtime.RemoteObject</c> as the runtime reports it.
/// </summary>
/// <param name="Thrown">
/// True when this object is a value that was thrown or that a promise rejected
/// with, rather than the evaluation result. CDP reports those through
/// <c>exceptionDetails</c> on a successful reply, so the difference has to
/// survive the trip out of the runtime.
/// </param>
public sealed record RemoteObjectInfo(
    bool Thrown,
    string JsType,
    string? Subtype,
    string ClassName,
    string Description,
    string? ObjectId,
    JsonNode? Value);

/// <summary>
/// CDP remote objects that can be rebuilt when a page's V8 runtime is
/// temporarily replaced during tab switching.
/// </summary>
/// <remarks>
/// Obscura keeps one entered isolate per connection. Switching tabs therefore
/// rebuilds the target page's runtime, but CDP clients reasonably expect handles
/// returned by <c>Runtime.evaluate</c> to remain usable. Retaining the
/// originating expressions lets the page restore a handle on demand under the
/// same id without retaining a second isolate.
/// </remarks>
public sealed class CdpObjectState
{
    internal ulong ObjectCounter { get; init; }

    internal Dictionary<string, string> EvaluationRecipes { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A fetched and instantiated module graph whose evaluation is intentionally
/// delayed until the HTML script scheduler reaches its post-parse turn.
/// </summary>
public sealed class PreparedModule
{
    /// <summary>Identity of the prepared graph within this runtime.</summary>
    public required long ModuleId { get; init; }

    /// <summary>How the module is named in an error message.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The entry module's URL, or null for an inline module: several inline
    /// modules deliberately share the document URL, so only a URL-addressed
    /// entry can be deduplicated by specifier.
    /// </summary>
    public string? EntrySpecifier { get; init; }

    /// <summary>
    /// The URL this module reports as <c>import.meta.url</c> and uses as the
    /// referrer for its relative imports. Unlike <see cref="EntrySpecifier"/>
    /// it is also set for an inline module, whose module URL is the document
    /// base URL even though several inline modules share it.
    /// </summary>
    public string? ModuleUrl { get; init; }

    /// <summary>Every specifier that becomes evaluated with this root.</summary>
    public required IReadOnlyList<string> GraphSpecifiers { get; init; }
}
