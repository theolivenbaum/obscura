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
    private readonly Dictionary<long, TimerEntry> _timers = [];
    private long _nextTimerId = 1;

    /// <summary>The bound op table exposed as <c>Deno.core.ops</c>.</summary>
    public required object Ops { get; init; }

    /// <summary>Callback registered via <c>setUnhandledPromiseRejectionHandler</c>.</summary>
    public object? UnhandledRejectionHandler { get; private set; }

    /// <summary>Callback registered via <c>setHandledPromiseRejectionHandler</c>.</summary>
    public object? HandledRejectionHandler { get; private set; }

    internal sealed record TimerEntry(object Callback, double DelayMs, bool Repeat, long Generation);

    /// <summary>
    /// Queues a user timer. Mirrors deno_core's signature
    /// <c>queueUserTimer(realmId, repeat, delayMs, callback)</c> and returns the
    /// id <c>cancelTimer</c> accepts.
    /// </summary>
    [ScriptMember("queueUserTimer")]
    public double QueueUserTimer(double realmId, bool repeat, double delayMs, object callback)
    {
        var id = _nextTimerId++;
        _timers[id] = new TimerEntry(callback, delayMs, repeat, (long)realmId);
        return id;
    }

    /// <summary>Cancels a timer previously returned by <see cref="QueueUserTimer"/>.</summary>
    [ScriptMember("cancelTimer")]
    public void CancelTimer(double id) => _timers.Remove((long)id);

    [ScriptMember("setUnhandledPromiseRejectionHandler")]
    public void SetUnhandledPromiseRejectionHandler(object handler) => UnhandledRejectionHandler = handler;

    [ScriptMember("setHandledPromiseRejectionHandler")]
    public void SetHandledPromiseRejectionHandler(object handler) => HandledRejectionHandler = handler;

    /// <summary>Timers that are due, oldest deadline first. Drives the event loop.</summary>
    internal IReadOnlyDictionary<long, TimerEntry> Timers => _timers;
}
