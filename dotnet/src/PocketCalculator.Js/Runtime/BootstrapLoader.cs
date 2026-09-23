using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Installs <c>Deno.core</c> and evaluates the shared <c>bootstrap.js</c> into a
/// V8 engine, then takes the op table handoff back off the global.
/// </summary>
public static class BootstrapLoader
{
    /// <summary>
    /// Installs the shim and runs bootstrap.js. <paramref name="bindOps"/> is
    /// called with the ops object so the host can attach its op functions.
    /// </summary>
    /// <summary>
    /// Test-only seam, the counterpart of upstream's <c>#[cfg(test)]</c>
    /// <c>expose_ops_for_tests</c>: when set, each new <see cref="PocketCalculatorJsRuntime"/>
    /// republishes its page realm's op table as <c>globalThis.__obscura_test_ops</c>
    /// so a test can wrap or stub an op. Only the PocketCalculator.Js test assembly sets it
    /// (a module initializer); nothing in a production process does.
    /// </summary>
    internal static bool ExposeOpsForTests { get; set; }

    public static DenoCoreShim Install(V8ScriptEngine engine, Action<ScriptObject> bindOps)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(bindOps);

        var ops = (ScriptObject)engine.Evaluate("({})");
        bindOps(ops);

        var shim = new DenoCoreShim { Ops = ops };
        engine.AddHostObject("__obscura_deno_core", HostItemFlags.PrivateAccess, shim);
        // The shim closes over the host object in a private const rather than
        // reading it off the global on every call, which is what lets the global
        // be removed below without breaking the four members.
        engine.Execute("bootstrap-preamble", """
            globalThis.Deno = (function () {
              const host = __obscura_deno_core;
              return { core: {
                ops: host.Ops,
                queueUserTimer: (r, rep, d, cb) => host.queueUserTimer(r, rep, d, cb),
                cancelTimer: (id) => host.cancelTimer(id),
                setUnhandledPromiseRejectionHandler: (h) => host.setUnhandledPromiseRejectionHandler(h),
                setHandledPromiseRejectionHandler: (h) => host.setHandledPromiseRejectionHandler(h),
              } };
            })();
            """);

        // Ahead of bootstrap.js, which adopts the two globals this installs.
        FormStateMirror.Install(engine);

        // EngineText, not Text: the shim's dynamic-classic-script call sites are
        // bridged onto op_run_classic_script on the way in. See BootstrapSource.
        engine.Execute(new DocumentInfo("bootstrap.js"), BootstrapSource.EngineText);

        // The shim registers its rejection handlers while bootstrap.js runs, so the
        // tracker and the engine callback are installed afterwards, once there is
        // something to call.
        //
        // Promise identity is tracked in JS rather than in managed code: the host
        // sees each promise through a fresh ClearScript wrapper per callback, so
        // reference equality on the managed side does not identify the same promise
        // across the rejection and the later handler-attached event. A JS Map keyed
        // by the promise itself does.
        var tracker = (ScriptObject)engine.Evaluate("rejection-tracker", """
            (function () {
              const pending = new Map();
              const reported = new Set();
              let unhandled = null, handled = null;
              return {
                setHandlers(u, h) { unhandled = u; handled = h; },
                rejected(p, reason) { if (!reported.has(p)) pending.set(p, reason); },
                handlerAdded(p, value) {
                  if (pending.delete(p)) return;
                  if (reported.delete(p) && typeof handled === 'function') handled(p, value);
                },
                flush() {
                  if (pending.size === 0) return;
                  const due = Array.from(pending.entries());
                  pending.clear();
                  for (const [p, reason] of due) {
                    reported.add(p);
                    if (typeof unhandled === 'function') unhandled(p, reason);
                  }
                }
              };
            })()
            """);
        shim.AttachTo(engine, tracker);

        // Drop the op-table handoff and `Deno` itself, exactly as
        // `take_ops_handoff` / `share_ops_with_realm` do in upstream runtime.rs
        // (04418a5): the host has the table already, and bootstrap.js closes over
        // `Deno.core` in a private const (`__obscuraCore`), so page script must
        // never reach the ops through either global. This runs for the page realm
        // and for every frame realm, since both are installed through here.
        //
        // This reverses the port's earlier decision to keep `globalThis.Deno`
        // (non-enumerable), which left every op callable from page script.
        engine.Execute("bootstrap-postamble", """
            delete globalThis.__obscura_core_handoff;
            delete globalThis.__obscura_deno_core;
            delete globalThis.Deno;
            """);
        return shim;
    }
}
