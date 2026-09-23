using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Host-authored script that needs the realm's host helpers.
/// </summary>
/// <remarks>
/// <para>
/// bootstrap.js keeps the helpers that grant what page script must not have (marking an
/// event trusted, delivering a <c>message</c> as another realm, forwarding a trusted
/// label activation, filling a file input) in a closure, and hands them to the host once
/// through a global that <see cref="BootstrapLoader"/> deletes before any page script
/// runs (<see cref="DenoCoreShim.HostHelpers"/>). Host script reaches them as the
/// parameter <c>__obscura_host</c> of a function the host compiles and calls with that
/// object, so the only reference lives in C# and in the frames of host calls.
/// </para>
/// <para>
/// The wrapper is strict. A page function the host script calls (an overridden
/// <c>dispatchEvent</c>, say) could otherwise walk <c>arguments.callee.caller</c> up to
/// the wrapper and read <c>__obscura_host</c> from its <c>arguments</c>; V8 answers
/// <c>caller</c> with null for a strict caller.
/// </para>
/// <para>
/// DEVIATION from the Rust engine, where the helpers are page-visible
/// <c>__obscura_*</c> globals and host script names them as
/// <c>globalThis.__obscura_markTrusted(...)</c>.
/// </para>
/// </remarks>
public static class HostScript
{
    /// <summary>The name host script uses for the helpers object.</summary>
    public const string Parameter = "__obscura_host";

    /// <summary>
    /// A function that returns <paramref name="expression"/>'s value, or null if it
    /// throws (the <see cref="PocketCalculatorJsRuntime.Evaluate"/> contract).
    /// </summary>
    internal static string WrapExpression(string expression)
    {
        var cleaned = expression.Trim().TrimEnd(';', ' ', '\t', '\r', '\n', '\f', '\v');
        return $"(function({Parameter}) {{ \"use strict\"; try {{ return (\n{cleaned}\n); }} catch (e) {{ return null; }} }})";
    }

    /// <summary>
    /// A function whose body is <paramref name="source"/>; an exception propagates to
    /// the caller, as it does from a script run with <c>ExecuteScript</c>.
    /// </summary>
    internal static string WrapStatements(string source) =>
        $"(function({Parameter}) {{ \"use strict\";\n{source}\n}})";

    /// <summary>
    /// Compiles <paramref name="functionSource"/> (a <see cref="WrapExpression"/> or
    /// <see cref="WrapStatements"/> result) in <paramref name="engine"/> and calls it with
    /// <paramref name="helpers"/>.
    /// </summary>
    /// <exception cref="ScriptEngineException">The script threw, or did not compile.</exception>
    internal static object? Invoke(V8ScriptEngine engine, ScriptObject? helpers, string name, string functionSource)
    {
        var function = (ScriptObject)engine.Evaluate(new DocumentInfo(name), functionSource);
        return function.InvokeAsFunction(helpers);
    }
}
