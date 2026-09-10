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
        if (Wrap(function) is { } fast)
        {
            var invoke = OpProfile.Enabled ? OpProfile.Wrap(name, fast.Invoke) : fast.Invoke;
            ops.SetProperty(name, new V8FastHostFunction(name, fast.Arity, invoke));
            return;
        }

        ops.SetProperty(name, function);
    }

    /// <summary>
    /// <c>OBSCURA_OP_PROFILE=1</c> prints a running total of time spent per op.
    /// </summary>
    /// <remarks>
    /// The Rust reference has no equivalent; this exists because the binding is the
    /// one place every op crossing passes through, which makes it the only cheap
    /// vantage point for asking which op a slow page is actually spending its time
    /// in. It answered that question once already: on a Tesserae route the whole
    /// budget turned out to be three <c>op_layout_geometry</c> calls, not the
    /// ~24,000 <c>op_dom</c> calls around them. Off, it costs one environment read
    /// at bind time and nothing per call.
    /// </remarks>
    internal static class OpProfile
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("OBSCURA_OP_PROFILE") is not null;

        private static readonly Dictionary<string, (long Ticks, long Count)> Totals = new(StringComparer.Ordinal);
        private static long _nextReport = System.Diagnostics.Stopwatch.GetTimestamp();

        /// <summary>
        /// Reported on a timer rather than at exit: the CLI can leave through paths
        /// that do not run process-exit handlers, and a run that hangs is exactly the
        /// one whose numbers are wanted.
        /// </summary>
        private const int ReportIntervalSeconds = 3;

        public static V8FastHostFunctionInvoker Wrap(string name, V8FastHostFunctionInvoker inner) =>
            (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                long t = System.Diagnostics.Stopwatch.GetTimestamp();
                inner(ctor, in a, in r);
                Record(name, System.Diagnostics.Stopwatch.GetTimestamp() - t);
            };

        private static void Record(string name, long ticks)
        {
            ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
                .GetValueRefOrAddDefault(Totals, name, out _);
            slot = (slot.Ticks + ticks, slot.Count + 1);

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _nextReport)
            {
                return;
            }

            _nextReport = now + (System.Diagnostics.Stopwatch.Frequency * ReportIntervalSeconds);
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var rows = new List<KeyValuePair<string, (long Ticks, long Count)>>(Totals);
            rows.Sort((x, y) => y.Value.Ticks.CompareTo(x.Value.Ticks));
            var line = new System.Text.StringBuilder("ops:");
            for (int i = 0; i < rows.Count && i < 8; i++)
            {
                line.Append(' ').Append(rows[i].Key).Append('=')
                    .Append((rows[i].Value.Ticks * f).ToString("F0")).Append("ms/")
                    .Append(rows[i].Value.Count);
            }

            Console.Error.WriteLine(line.ToString());
        }
    }

    /// <summary>
    /// Reads argument <paramref name="index"/> as the boxed CLR value the general
    /// marshaller would have produced, so the normalizers in
    /// <see cref="ObscuraOps"/> see exactly what they saw before.
    /// </summary>
    private static object? Box(in V8FastArgs args, int index) =>
        index < args.Count ? args.Get<object>(index) : null;

    private static (int Arity, V8FastHostFunctionInvoker Invoke)? Wrap(object function) => function switch
    {
        Action f => (0, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f();
            }),

        Action<object?> f => (1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f(Box(a, 0));
            }),

        Action<object?, object?> f => (2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f(Box(a, 0), Box(a, 1));
            }),

        Action<object?, object?, object?> f => (3, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f(Box(a, 0), Box(a, 1), Box(a, 2));
            }),

        Action<object?, object?, object?, object?, object?> f => (5, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3), Box(a, 4));
            }),

        Func<bool> f => (0, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f());
            }),

        Func<string> f => (0, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f());
            }),

        Func<object?, bool> f => (1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0)));
            }),

        Func<object?, double> f => (1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0)));
            }),

        Func<object?, string> f => (1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0)));
            }),

        Func<object?, object> f => (1, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0)));
            }),

        Func<object?, object?, bool> f => (2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, double> f => (2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, int> f => (2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, string> f => (2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, object> f => (2, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1)));
            }),

        Func<object?, object?, object?, bool> f => (3, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2)));
            }),

        Func<object?, object?, object?, string> f => (3, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2)));
            }),

        Func<object?, object?, object?, object> f => (3, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2)));
            }),

        Func<object?, object?, object?, object?, bool> f => (4, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3)));
            }),

        Func<object?, object?, object?, object?, double> f => (4, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3)));
            }),

        Func<object?, object?, object?, object?, string> f => (4, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3)));
            }),

        Func<object?, object?, object?, object?, object> f => (4, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3)));
            }),

        Func<object?, object?, object?, object?, object?, object> f => (5, (bool ctor, in V8FastArgs a, in V8FastResult r) =>
            {
                V8FastHostFunction.VerifyFunctionCall(ctor);
                r.Set(f(Box(a, 0), Box(a, 1), Box(a, 2), Box(a, 3), Box(a, 4)));
            }),

        // Task-returning ops stay on the general marshaller; see the type remarks.
        _ => null,
    };
}
