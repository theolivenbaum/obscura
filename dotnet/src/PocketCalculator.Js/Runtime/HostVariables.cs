using Microsoft.ClearScript;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// bootstrap.js's host variables: <c>__obscura_host.vars</c>, a closure object holding the
/// values the host sets (<c>__obscura_ua</c>, <c>__obscura_frameId</c>, the viewport and
/// screen overrides, ...) and the engine's own state.
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, where each of these is a <c>globalThis.__obscura_*</c>
/// property: any page could detect them by name, and overwrite the ones a frame or an
/// isolated world copies from the page (SECURITY.md I10). Only host code holding the
/// realm's helpers object reaches them.
/// </remarks>
internal static class HostVariables
{
    /// <summary>
    /// Copies the primitive values of <paramref name="names"/> from one realm's host
    /// variables to another's, skipping any that are unset.
    /// </summary>
    internal static void Copy(ScriptObject? fromHelpers, ScriptObject? toHelpers, IReadOnlyList<string> names)
    {
        if (fromHelpers?.GetProperty("vars") is not ScriptObject from
            || toHelpers?.GetProperty("vars") is not ScriptObject to)
        {
            return;
        }
        foreach (var name in names)
        {
            var value = from.GetProperty(name);
            if (value is bool or string or int or long or double or float)
            {
                to.SetProperty(name, value);
            }
        }
    }

    /// <summary>One realm's host variable, or null when it is unset or not a primitive.</summary>
    internal static object? Get(ScriptObject? helpers, string name) =>
        helpers?.GetProperty("vars") is ScriptObject vars
            && vars.GetProperty(name) is var value and (bool or string or int or long or double or float)
                ? value
                : null;
}
