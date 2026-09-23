using System.Diagnostics;
using System.Runtime.CompilerServices;

using Microsoft.ClearScript.V8;

using PocketCalculator.Js.Runtime;

namespace PocketCalculator.Js.Modules;

/// <summary>
/// A thread-safe handle that can terminate whatever a V8 isolate is currently
/// executing.
/// </summary>
/// <remarks>
/// Stands in for <c>deno_core::v8::IsolateHandle</c>. Synchronous V8 work runs
/// unbounded, so a timeout that only cancels at await points cannot interrupt it;
/// the isolate has to be terminated from another thread.
/// </remarks>
public interface IIsolateHandle
{
    /// <summary>Terminate the isolate's current execution. Must never throw.</summary>
    void TerminateExecution();
}

/// <summary>
/// <see cref="IIsolateHandle"/> over a ClearScript engine.
/// <see cref="V8ScriptEngine.Interrupt"/> is the managed equivalent of
/// <c>terminate_execution</c>: it raises <c>ScriptInterruptedException</c> out of
/// the running script from any thread.
/// </summary>
public sealed class V8IsolateHandle(V8ScriptEngine engine, ScriptCancellation? cancellation = null) : IIsolateHandle
{
    private readonly V8ScriptEngine _engine =
        engine ?? throw new ArgumentNullException(nameof(engine));

    /// <inheritdoc/>
    public void TerminateExecution()
    {
        // Before the interrupt, for C# work inside an op (see WatchdogScheduler.Entry.Fire).
        cancellation?.Cancel();
        try
        {
            _engine.Interrupt();
        }
        catch (ObjectDisposedException)
        {
            // The engine was torn down between the deadline and the interrupt.
        }
        catch (InvalidOperationException)
        {
            // Same race, reported differently by some ClearScript builds.
        }
    }

    /// <summary>A fired command has settled: later work gets a fresh deadline.</summary>
    internal void Settled() => cancellation?.Reset();
}

/// <summary>Handle to an armed command; pass to <see cref="CdpWatchdog.Disarm"/>.</summary>
public sealed class ArmedWatchdog
{
    internal ArmedWatchdog(CdpWatchdogCore owner, ulong generation, IIsolateHandle? handle = null)
    {
        Owner = owner;
        Generation = generation;
        Handle = handle;
    }

    /// <summary>The isolate this command was armed on.</summary>
    internal IIsolateHandle? Handle { get; }

    internal CdpWatchdogCore Owner { get; }

    internal ulong Generation { get; }

    internal int FiredFlag;

    /// <summary>Whether the watchdog has already terminated the isolate.</summary>
    public bool Fired => Volatile.Read(ref FiredFlag) != 0;
}

/// <summary>
/// Shared per-command V8 watchdog for the CDP server.
/// </summary>
/// <remarks>
/// <para>
/// Port of <c>crates/obscura-js/src/cdp_watchdog.rs</c>. One long-lived watchdog
/// thread bounds every in-flight V8 command with a deadline, instead of spawning
/// and joining a thread per command (which adds ~240us per command on the hot
/// dispatch path). <c>Arm</c> and <c>Disarm</c> are a lock plus a pulse, in the
/// low microseconds.
/// </para>
/// <para>
/// With a thread-per-connection server several connections can have a command
/// armed at the same time (one engine per connection, each on its own thread), so
/// a single global slot would let one connection's arm overwrite another's and
/// leave that command unbounded. The watchdog therefore tracks a set of armed
/// slots keyed by a monotonic generation, fires whichever have overrun, and
/// terminates each through its thread-safe handle.
/// </para>
/// </remarks>
public static class CdpWatchdog
{
    private static readonly Lazy<CdpWatchdogCore> SharedCore =
        new(() => new CdpWatchdogCore("cdp-watchdog"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Arm the shared watchdog for the current command. If the isolate is still
    /// executing <paramref name="budget"/> later, it is terminated. O(1), no thread
    /// spawn. Safe to call concurrently from several connections: each command gets
    /// its own slot.
    /// </summary>
    public static ArmedWatchdog Arm(IIsolateHandle handle, TimeSpan budget) =>
        SharedCore.Value.Arm(handle, budget);

    /// <summary>
    /// Disarm the command's watchdog. Returns true if it had already fired
    /// (terminated the isolate), in which case the caller must clear the
    /// termination state before the next command runs.
    /// </summary>
    public static bool Disarm(ArmedWatchdog armed)
    {
        ArgumentNullException.ThrowIfNull(armed);
        return armed.Owner.Disarm(armed);
    }
}

/// <summary>
/// The watchdog itself. <see cref="CdpWatchdog"/> owns one process-wide instance,
/// matching the Rust <c>OnceLock</c>; a separate instance exists so the arm,
/// disarm and fire paths can be tested without racing the shared one.
/// </summary>
internal sealed class CdpWatchdogCore
{
    // A plain object, not System.Threading.Lock: the worker parks on
    // Monitor.Wait and arm/disarm wake it with Monitor.Pulse, which Lock does not
    // offer. This is the condvar half of the Rust Mutex + Condvar pair.
    private readonly object _gate = new();
    private readonly Dictionary<ulong, Slot> _slots = [];
    private ulong _generation;
    private Thread? _worker;

    internal CdpWatchdogCore(string threadName) => ThreadName = threadName;

    private string ThreadName { get; }

    internal int ArmedCount
    {
        get
        {
            lock (_gate)
            {
                return _slots.Count;
            }
        }
    }

    internal ArmedWatchdog Arm(IIsolateHandle handle, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(handle);

        lock (_gate)
        {
            EnsureWorker();
            _generation++;
            var armed = new ArmedWatchdog(this, _generation, handle);
            _slots[_generation] = new Slot(Deadline(budget), handle, armed);
            Monitor.Pulse(_gate);
            return armed;
        }
    }

    internal bool Disarm(ArmedWatchdog armed)
    {
        lock (_gate)
        {
            _slots.Remove(armed.Generation);
            // Wake the worker so it recomputes its sleep if we removed the nearest
            // slot.
            Monitor.Pulse(_gate);
        }

        if (armed.Fired)
        {
            HangEscalation.Settled(armed);
            (armed.Handle as V8IsolateHandle)?.Settled();
        }

        return armed.Fired;
    }

    private void EnsureWorker()
    {
        if (_worker is not null)
        {
            return;
        }

        _worker = new Thread(WatchdogLoop)
        {
            // Background, so a live watchdog never keeps the process (or a test
            // host) from exiting. The Rust thread is detached for the same reason.
            IsBackground = true,
            Name = ThreadName,
        };
        _worker.Start();
    }

    private void WatchdogLoop()
    {
        lock (_gate)
        {
            while (true)
            {
                // The scan runs in its own frame, and that is load-bearing rather
                // than tidiness. This thread parks on Monitor.Wait below for as long
                // as nothing is armed, and a Slot left in one of this frame's stack
                // slots stays a GC root for exactly that long. A Slot holds the
                // V8IsolateHandle of the page that was executing, and through it
                // ClearScript's V8ScriptEngine -> DocumentSettings ->
                // RecordingModuleLoader -> PocketCalculatorJsRuntime -> PocketCalculatorState ->
                // PreparedRender: the whole previous document. Measured over CDP on
                // a 60k-node page, that pinned about 440 MB from the moment the
                // command disarmed until the next command armed - so every
                // navigation built its new document with the old one still resident,
                // and an aggressive gen2 collection could not reclaim it.
                //
                // Scanning in a callee that has returned puts those references below
                // the stack pointer, where the GC does not look. Disarm has already
                // removed the slot from _slots, so nothing else holds it.
                //
                // Deviation from crates/obscura-js/src/cdp_watchdog.rs, which needs
                // no equivalent: Rust drops the IsolateHandle when the slot leaves
                // the map.
                long? next = FireExpiredAndFindNextDeadline();

                // Sleep until the nearest remaining deadline, or until arm/disarm
                // wakes us. The worker holds the lock until it waits, so a pulse
                // cannot be lost into the void.
                if (next is null)
                {
                    Monitor.Wait(_gate);
                }
                else
                {
                    var wait = Stopwatch.GetElapsedTime(0, Math.Max(next.Value, 0));
                    // Round up to the tick the deadline actually falls on: waiting
                    // for a truncated interval wakes just short of it and spins.
                    Monitor.Wait(_gate, wait + TimeSpan.FromTicks(1));
                }
            }
        }
    }

    /// <summary>
    /// Terminate every overrun slot and report how long until the nearest deadline
    /// that is left, or null when nothing is armed. Called with <c>_gate</c> held.
    /// </summary>
    /// <remarks>
    /// Never inlined: see <see cref="WatchdogLoop"/> for why no reference to a
    /// <c>Slot</c> may survive into the wait.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private long? FireExpiredAndFindNextDeadline()
    {
        var now = Stopwatch.GetTimestamp();

        // Terminate every slot that has overrun its deadline. The dispatcher's
        // Disarm will observe Fired and clear the termination state before that
        // isolate runs its next command.
        List<ulong>? expired = null;
        foreach (var (generation, slot) in _slots)
        {
            if (slot.Deadline <= now)
            {
                (expired ??= []).Add(generation);
            }
        }

        if (expired is not null)
        {
            foreach (var generation in expired)
            {
                if (!_slots.Remove(generation, out var slot))
                {
                    continue;
                }

                Volatile.Write(ref slot.Armed.FiredFlag, 1);
                HangEscalation.Fired(slot.Armed, "an interrupted CDP command");
                try
                {
                    slot.Handle.TerminateExecution();
                }
                catch
                {
                    // A handle that cannot terminate must not take the watchdog
                    // thread down with it; every other armed command still depends
                    // on this loop.
                }
            }
        }

        long? next = null;
        foreach (var slot in _slots.Values)
        {
            var remaining = slot.Deadline - now;
            if (next is null || remaining < next)
            {
                next = remaining;
            }
        }

        return next;
    }

    private static long Deadline(TimeSpan budget)
    {
        var ticks = budget <= TimeSpan.Zero
            ? 0L
            : (long)(budget.TotalSeconds * Stopwatch.Frequency);
        return Stopwatch.GetTimestamp() + ticks;
    }

    private sealed class Slot(long deadline, IIsolateHandle handle, ArmedWatchdog armed)
    {
        public long Deadline { get; } = deadline;

        public IIsolateHandle Handle { get; } = handle;

        public ArmedWatchdog Armed { get; } = armed;
    }
}
