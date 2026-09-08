using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Obscura.Js.Runtime;

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

        engine.Execute(new DocumentInfo("bootstrap.js"), BootstrapSource.Text);

        // Drop the op-table handoff, exactly as `take_ops_handoff` does in
        // runtime.rs: the host has the table already, and page script must never
        // reach it through `__obscura_core_handoff`.
        //
        // `globalThis.Deno` itself stays. bootstrap.js is one IIFE that resolves
        // `Deno.core.ops.<op>` at *call* time on more than thirty lines
        // (_scheduleAfter, the console bridge, every layout and image op), so
        // deleting it does not hide the ops - it breaks setTimeout, console and
        // CSSOM the first time page script touches them. It is made
        // non-enumerable instead so it does not show up in Object.keys(window).
        engine.Execute("bootstrap-postamble", """
            delete globalThis.__obscura_core_handoff;
            delete globalThis.__obscura_deno_core;
            try {
              Object.defineProperty(globalThis, 'Deno',
                { value: globalThis.Deno, writable: false, enumerable: false, configurable: false });
            } catch (_) {}
            """);
        return shim;
    }
}
