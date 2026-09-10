using Microsoft.ClearScript;
using Microsoft.ClearScript.V8.FastProxy;

namespace Obscura.Js.Ops;

/// <summary>
/// Binds an op delegate onto the <c>Deno.core.ops</c> object through ClearScript's
/// fast-proxy path instead of its general host-object marshaller.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from <c>crates/obscura-js</c>: Rust registers ops with deno_core, whose
/// generated bindings reach V8's fast-call path directly, so a call costs roughly
/// 0.1-0.5us. The port's first cut used <c>ScriptObject.SetProperty(name, delegate)</c>,
/// which routes every call through ClearScript's reflection-based host-object
/// dispatcher: measured at 1.4us for a no-argument op and 3.2us for the four-argument
/// <c>op_dom</c>, against 0.07us and 0.47us for the same ops in Rust. Since
/// <c>op_dom</c> alone multiplexes ~90 commands and a script-heavy page issues them by
/// the hundred thousand, that gap is a real wall-clock cost, not a micro-benchmark
/// artifact.
/// </para>
/// <para>
/// <see cref="V8FastHostFunction"/> (ClearScript 7.5) skips the dispatcher: V8 calls the
/// invoker with the raw argument list. The same delegates and the same argument
/// normalizers in <see cref="ObscuraOps"/> still run, so op semantics are unchanged;
/// only the boundary is different. Measured after the change: 0.33us for a
/// no-argument op and 0.97us for <c>op_dom</c>, a 3.5x cut.
/// </para>
/// <para>
/// Async ops keep the old path. They return <see cref="Task"/>, and the promise
/// conversion the shim depends on is a feature of the general marshaller
/// (<c>EnableTaskPromiseConversion</c>); a fast-proxy return value would reach script
/// as a host object instead of a promise. They are also cold, so the boundary cost
/// does not matter there.
/// </para>
/// </remarks>
internal static class FastOpBinding
{
    /// <summary>
    /// Sets <paramref name="name"/> on <paramref name="ops"/>, using the fast-proxy
    /// path when <paramref name="function"/> has a shape it supports.
    /// </summary>
    public static void Bind(ScriptObject ops, string name, object function)
    {
        if (Wrap(name, function) is { } fast)
        {
            ops.SetProperty(name, fast);
            return;
        }

        ops.SetProperty(name, function);
    }

    /// <summary>
    /// Reads argument <paramref name="index"/> as the boxed CLR value the general
    /// marshaller would have produced, so the normalizers in
    /// <see cref="ObscuraOps"/> see exactly what they saw before.
    /// </summary>
    private static object? Box(in V8FastArgs args, int index) =>
        index < args.Count ? args.Get<object>(index) : null;

    private static V8FastHostFunction? Wrap(string name, object function) => function switch
    {
        Action f => new V8FastHostFunction(
            name, 0, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f();
            }),

        Action<object?> f => new V8FastHostFunction(
            name, 1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f(Box(a, 0));
            }),

        Action<object?, object?> f => new V8FastHostFunction(
            name, 2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f(Box(a, 0), Box(a, 1));
            }),

        Action<object?, object?, object?> f => new V8FastHostFunction(
            name, 3, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f(Box(a, 0), Box(a, 1), Box(a, 2));
            }),

        Action<object?, object?, object?, object?, object?> f => new V8FastHostFunction(
            name, 5, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3), Box(a, 4));
            }),

        Func<bool> f => new V8FastHostFunction(
            name, 0, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f());
            }),

        Func<string> f => new V8FastHostFunction(
            name, 0, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f());
            }),

        Func<object?, bool> f => new V8FastHostFunction(
            name, 1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0)));
            }),

        Func<object?, double> f => new V8FastHostFunction(
            name, 1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0)));
            }),

        Func<object?, string> f => new V8FastHostFunction(
            name, 1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0)));
            }),

        Func<object?, object> f => new V8FastHostFunction(
            name, 1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0)));
            }),

        Func<object?, object?, bool> f => new V8FastHostFunction(
            name, 2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, double> f => new V8FastHostFunction(
            name, 2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, int> f => new V8FastHostFunction(
            name, 2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, string> f => new V8FastHostFunction(
            name, 2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, object> f => new V8FastHostFunction(
            name, 2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, object?, bool> f => new V8FastHostFunction(
            name, 3, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2)));
            }),

        Func<object?, object?, object?, string> f => new V8FastHostFunction(
            name, 3, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2)));
            }),

        Func<object?, object?, object?, object> f => new V8FastHostFunction(
            name, 3, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2)));
            }),

        Func<object?, object?, object?, object?, bool> f => new V8FastHostFunction(
            name, 4, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3)));
            }),

        Func<object?, object?, object?, object?, double> f => new V8FastHostFunction(
            name, 4, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3)));
            }),

        Func<object?, object?, object?, object?, string> f => new V8FastHostFunction(
            name, 4, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3)));
            }),

        Func<object?, object?, object?, object?, object> f => new V8FastHostFunction(
            name, 4, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3)));
            }),

        Func<object?, object?, object?, object?, object?, object> f => new V8FastHostFunction(
            name, 5, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3), Box(a, 4)));
            }),

        // Task-returning ops stay on the general marshaller; see the type remarks.
        _ => null,
    };
}
