using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Obscura.Js.Runtime;

/// <summary>
/// The <c>Deno.core</c> surface <c>bootstrap.js</c> expects.
/// </summary>
/// <remarks>
/// The shim is deliberately tiny: the shared shim touches exactly four non-op
/// members of <c>Deno.core</c> plus the op table, so this is the whole contract
/// between the shim and a host runtime. Keeping it explicit means a shim update
/// that reaches for a new deno_core API fails loudly here instead of silently
/// diverging from the Rust engine.
/// </remarks>
public sealed class DenoCoreShim
{
    /// <summary>Pending host timers. The embedder pumps them; they never fire on their own.</summary>
    public TimerQueue Timers { get; } = new();

    /// <summary>The bound op table exposed as <c>Deno.core.ops</c>.</summary>
    public required object Ops { get; init; }

    /// <summary>Callback registered via <c>setUnhandledPromiseRejectionHandler</c>.</summary>
    /// <remarks>
    /// Storing this is not enough: nothing in V8 calls it on its own. The engine's
    /// <c>PromiseRejectionCallback</c> has to be wired to invoke it, or the
    /// <c>PromiseRejectionEvent</c> the shim builds is never dispatched and
    /// <c>window.onunhandledrejection</c> silently never fires. See
    /// <see cref="AttachTo"/>.
    /// </remarks>
    public object? UnhandledRejectionHandler { get; private set; }

    /// <summary>Callback registered via <c>setHandledPromiseRejectionHandler</c>.</summary>
    public object? HandledRejectionHandler { get; private set; }

    /// <summary>
    /// Routes V8's promise-rejection events into the handlers the shim registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// deno_core installs V8's <c>PromiseRejectCallback</c> and calls the host handler
    /// from it. ClearScript surfaces the same callback, so the wiring is direct, but
    /// the REPORT MUST BE DEFERRED. V8 raises <c>RejectedWithoutHandler</c> at the
    /// instant of rejection, which is before a synchronous <c>.catch()</c> on the very
    /// next expression has attached. Reporting eagerly therefore fires
    /// <c>unhandledrejection</c> for <c>Promise.reject(x).catch(...)</c>, which no
    /// browser does. Rejections are collected here and flushed at the microtask
    /// checkpoint by <see cref="FlushRejections"/>, which is where HTML specifies the
    /// event fires.
    /// </para>
    /// <para>
    /// Browsers report an unhandled rejection WITHOUT terminating the page's event
    /// loop, so a throwing handler is contained rather than propagated.
    /// </para>
    /// </remarks>
    public void AttachTo(V8ScriptEngine engine, ScriptObject tracker)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;

        // Hand the tracker the two handlers bootstrap.js registered, so delivery
        // happens entirely inside JS and the promise argument keeps its identity.
        tracker.InvokeMethod("setHandlers", UnhandledRejectionHandler!, HandledRejectionHandler!);

        engine.PromiseRejectionCallback = (kind, promise, value) =>
        {
            try
            {
                switch (kind)
                {
                    case V8PromiseRejectionEventKind.RejectedWithoutHandler:
                        tracker.InvokeMethod("rejected", promise!, value!);
                        break;
                    case V8PromiseRejectionEventKind.HandlerAddedAfterRejection:
                        tracker.InvokeMethod("handlerAdded", promise!, value!);
                        break;
                    default:
                        // The remaining kinds are re-settlement attempts, which
                        // browsers do not surface.
                        break;
                }
            }
            catch (Exception ex) when (ex is not ScriptInterruptedException)
            {
                RejectionDeliveryFailed?.Invoke(ex);
            }
        };
    }

    /// <summary>
    /// Reports rejections still unhandled at the microtask checkpoint. The event loop
    /// calls this after draining microtasks.
    /// </summary>
    public void FlushRejections()
    {
        if (_tracker is null)
        {
            return;
        }
        try
        {
            _tracker.InvokeMethod("flush");
        }
        catch (Exception ex) when (ex is not ScriptInterruptedException)
        {
            // A failure delivering the event must not take down the loop.
            RejectionDeliveryFailed?.Invoke(ex);
        }
    }

    private ScriptObject? _tracker;

    /// <summary>Diagnostics for a handler that threw while delivering a rejection.</summary>
    public event Action<Exception>? RejectionDeliveryFailed;


    /// <summary>
    /// Queues a user timer. Mirrors deno_core's signature
    /// <c>queueUserTimer(realmId, repeat, delayMs, callback)</c> and returns the
    /// id <c>cancelTimer</c> accepts.
    /// </summary>
    [ScriptMember("queueUserTimer")]
    public double QueueUserTimer(double realmId, bool repeat, double delayMs, object callback) =>
        Timers.Add(delayMs, repeat, callback);

    /// <summary>Cancels a timer previously returned by <see cref="QueueUserTimer"/>.</summary>
    [ScriptMember("cancelTimer")]
    public void CancelTimer(double id) => Timers.Cancel((long)id);

    [ScriptMember("setUnhandledPromiseRejectionHandler")]
    public void SetUnhandledPromiseRejectionHandler(object handler) => UnhandledRejectionHandler = handler;

    [ScriptMember("setHandledPromiseRejectionHandler")]
    public void SetHandledPromiseRejectionHandler(object handler) => HandledRejectionHandler = handler;
}
