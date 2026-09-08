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
    public object? UnhandledRejectionHandler { get; private set; }

    /// <summary>Callback registered via <c>setHandledPromiseRejectionHandler</c>.</summary>
    public object? HandledRejectionHandler { get; private set; }


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
