namespace PocketCalculator.Js.Runtime;

/// <summary>
/// The new-document entry <c>Runtime.addBinding</c> files beside the
/// <c>Page.addScriptToEvaluateOnNewDocument</c> sources: not a script but the binding's
/// name, which the realm installs through <c>__obscura_host.installBinding</c>.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from the Rust engine, whose entry is a script defining
/// <c>globalThis[name]</c> as a call to the page-visible global
/// <c>__obscura_binding_called</c>, a bridge any page can call and detect by name
/// (SECURITY.md I10). Here the binding function closes over <c>op_binding_called</c>
/// and nothing else is added to the global.
/// </para>
/// <para>
/// A preload list is run with <see cref="PocketCalculatorJsRuntime.ExecutePreloadScript"/>,
/// <see cref="FrameRealm.ExecutePreloadScript"/> or an isolated world's init, which install
/// an entry of this form and run anything else as script. Only the name crosses into the
/// host call, as a string literal, so an entry of this form a client wrote itself installs
/// a binding and grants nothing else.
/// </para>
/// </remarks>
public static class BindingPreload
{
    private const string Prefix = "\u0000obscura-binding:";

    /// <summary>The preload entry for binding <paramref name="name"/>.</summary>
    public static string Source(string name) => Prefix + name;

    /// <summary>The binding name <paramref name="source"/> stands for, or null for a script.</summary>
    public static string? NameOf(string source) =>
        source.StartsWith(Prefix, StringComparison.Ordinal) ? source[Prefix.Length..] : null;

    /// <summary>The host statement that installs binding <paramref name="name"/>.</summary>
    internal static string InstallStatement(string name) =>
        $"__obscura_host.installBinding({PocketCalculatorJsRuntime.JsStringLiteral(name)});";
}
