using System.Reflection;

namespace Obscura.Js;

/// <summary>
/// The DOM/browser shim shared verbatim with the Rust engine
/// (crates/obscura-js/js/bootstrap.js), embedded at build time.
/// </summary>
public static class BootstrapSource
{
    private static readonly Lazy<string> LazyText = new(static () =>
    {
        var asm = typeof(BootstrapSource).Assembly;
        using var stream = asm.GetManifestResourceStream("Obscura.Js.bootstrap.js")
            ?? throw new InvalidOperationException("bootstrap.js resource missing from Obscura.Js");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Raw JavaScript source of the shim.</summary>
    public static string Text => LazyText.Value;

    /// <summary>
    /// Every op name the shim calls, discovered from the source itself so the
    /// host binding surface cannot silently drift from the shim.
    /// </summary>
    public static IReadOnlyList<string> OpNames { get; } =
        System.Text.RegularExpressions.Regex
            .Matches(Text, @"ops\.(op_[a-z0-9_]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
