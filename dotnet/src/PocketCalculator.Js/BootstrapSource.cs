using System.Reflection;

namespace PocketCalculator.Js;

/// <summary>
/// The DOM/browser shim shared verbatim with the Rust engine
/// (crates/obscura-js/js/bootstrap.js), embedded at build time.
/// </summary>
public static class BootstrapSource
{
    private static readonly Lazy<string> LazyText = new(static () =>
    {
        var asm = typeof(BootstrapSource).Assembly;
        using var stream = asm.GetManifestResourceStream("PocketCalculator.Js.bootstrap.js")
            ?? throw new InvalidOperationException("bootstrap.js resource missing from PocketCalculator.Js");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<string> LazyEngineText = new(static () => ApplyClassicScriptBridge(Text));

    /// <summary>Raw JavaScript source of the shim, exactly as the file reads.</summary>
    public static string Text => LazyText.Value;

    /// <summary>
    /// The shim as this engine runs it: <see cref="Text"/> with the
    /// dynamically-inserted-classic-script call sites rewritten onto
    /// <c>op_run_classic_script</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deviation from the Rust engine. The shim executes a dynamically inserted
    /// classic script (both <c>script.textContent</c> and a fetched
    /// <c>script.src</c> body) with <c>(0, eval)(source)</c>. Indirect eval runs
    /// in the global scope, but ES eval semantics confine a <em>strict</em> eval's
    /// top-level <c>var</c> and <c>function</c> declarations to the eval's own
    /// variable environment, so a script whose source starts with
    /// <c>"use strict"</c> publishes nothing: every bundler prologue and every
    /// <c>"use strict"; var lib = (() =&gt; { ... })();</c> library loads, fires
    /// <c>load</c>, and leaves <c>globalThis.lib</c> undefined. Chromium evaluates
    /// the element as a top-level classic script, where those declarations always
    /// create global bindings whatever the strictness.
    /// </para>
    /// <para>
    /// The fix belongs in the shim, which is shared with Rust and read-only here
    /// (CLAUDE.md rule 1), so the port rewrites the two call sites on the way into
    /// V8 and runs the source through a host op that compiles it as a script. The
    /// rewrite is a no-op if the shim stops spelling them this way - upstream
    /// fixing this the same way removes the needles and the bridge simply stops
    /// applying.
    /// </para>
    /// </remarks>
    public static string EngineText => LazyEngineText.Value;

    /// <summary>
    /// The two <c>(0, eval)</c> call sites that execute a dynamically inserted
    /// classic script, and what the port runs instead. The third indirect eval in
    /// the shim compiles an inline event-handler attribute, whose body is a
    /// function body rather than a script, and is deliberately left alone.
    /// </summary>
    /// <remarks>
    /// Declared above <see cref="OpNames"/> because static initializers run in
    /// textual order and that one reads <see cref="EngineText"/>.
    /// </remarks>
    private static readonly (string Needle, string Replacement)[] ClassicScriptBridge =
    [
        // __runDynScriptTask: a fetched <script src> body.
        (
            "try { (0, eval)(body); }",
            "try { Deno.core.ops.op_run_classic_script(body, task.url); }"
        ),
        // __prepareInsertedScript: an inserted script element's own text.
        (
            "try { (0, eval)(code); }",
            "try { Deno.core.ops.op_run_classic_script(code, globalThis.location?.href || ''); }"
        ),
    ];

    /// <summary>
    /// Every op name the shim calls, discovered from the source itself so the
    /// host binding surface cannot silently drift from the shim.
    /// </summary>
    public static IReadOnlyList<string> OpNames { get; } =
        System.Text.RegularExpressions.Regex
            .Matches(EngineText, @"ops\.(op_[a-z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ApplyClassicScriptBridge(string source)
    {
        foreach ((string needle, string replacement) in ClassicScriptBridge)
        {
            // Only an unambiguous single occurrence is rewritten: a shim that has
            // moved on keeps its own behaviour rather than being half-patched.
            int first = source.IndexOf(needle, StringComparison.Ordinal);
            if (first < 0 || source.IndexOf(needle, first + needle.Length, StringComparison.Ordinal) >= 0)
            {
                continue;
            }
            source = string.Concat(source.AsSpan(0, first), replacement, source.AsSpan(first + needle.Length));
        }

        return source;
    }
}
