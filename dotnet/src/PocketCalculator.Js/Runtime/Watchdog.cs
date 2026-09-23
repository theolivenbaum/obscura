using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Handle to an armed V8 execution watchdog (see <see cref="Watchdog.Spawn"/>).
/// Holds the cancel signal and the watchdog thread; pass it back to
/// <see cref="PocketCalculatorJsRuntime.DisarmWatchdog"/> to stop the watchdog and learn
/// whether it fired.
/// </summary>
/// <remarks>
/// Disposing the token cancels and joins the thread. Futures which own a
/// watchdog may be abandoned while parked on I/O; dropping the token must not
/// leave a detached thread which later interrupts an engine that has already
/// moved on to another task.
/// </remarks>
public sealed class WatchdogToken : IDisposable
{
    private readonly WatchdogScheduler.Entry _entry;
    private int _stopped;

    internal WatchdogToken(WatchdogScheduler.Entry entry) => _entry = entry;

    internal Func<bool> FiredProbe => _entry.HasFired;

    /// <summary>
    /// Stop the watchdog. Returns true if it had already fired (interrupted the
    /// engine). The caller must then clear any lingering interrupt through
    /// <see cref="PocketCalculatorJsRuntime.CancelTermination"/> before the next eval.
    /// </summary>
    public bool Stop()
    {
        CancelAndSettle();
        return _entry.HasFired();
    }

    /// <summary>
    /// Withdraw the entry and wait out a firing already in progress. The wait is
    /// what the old per-arm <c>Thread.Join()</c> bought: once this returns, this
    /// watchdog can no longer interrupt an engine that has moved on to another
    /// task.
    /// </summary>
    private void CancelAndSettle()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        WatchdogScheduler.Cancel(_entry);
    }

    public void Dispose() => CancelAndSettle();
}

/// <summary>
/// One dedicated thread that services every armed watchdog, rather than one thread
/// per arm.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from <c>crates/obscura-js</c>, which arms a tokio timer against an
/// <c>IsolateHandle</c> and needs no thread of its own. The port used to start and
/// join a fresh OS thread for every arm - including one per event-loop tick from
/// <c>RunEventLoopBoundedAsync</c> and one per CDP command - which under load cost
/// more than the work it guarded and, on a small box running the suite at high
/// parallelism, aborted the process outright with
/// <c>Fatal error. ResumeThread failed with error 6</c> out of
/// <c>Thread.StartCore</c>.
/// </para>
/// <para>
/// A <see cref="Timer"/> would have removed the thread churn as well, and is the
/// wrong tool here: its callback runs on the thread pool, and the situation this
/// exists for - a page pinning threads inside V8 - is exactly when a pool callback
/// is late. A dedicated thread keeps the backstop independent of the pool, which is
/// the property the Rust engine gets for free.
/// </para>
/// </remarks>
public static class WatchdogScheduler
{
    /// <summary>One armed watchdog.</summary>
    public sealed class Entry
    {
        private int _fired;

        internal Entry(V8ScriptEngine engine, long deadline)
        {
            Engine = engine;
            Deadline = deadline;
        }

        internal V8ScriptEngine Engine { get; }

        internal long Deadline { get; }

        internal bool HasFired() => Volatile.Read(ref _fired) != 0;

        internal void Fire()
        {
            Volatile.Write(ref _fired, 1);
            try
            {
                Engine.Interrupt();
            }
            catch (ObjectDisposedException)
            {
                // The engine went away first; nothing to interrupt.
            }
            catch (InvalidOperationException)
            {
                // Same, reported differently by some ClearScript paths.
            }
        }
    }

    private static readonly object Gate = new();
    private static readonly HashSet<Entry> Armed = [];

    /// <summary>Entries whose <see cref="Entry.Fire"/> is running right now.</summary>
    private static readonly HashSet<Entry> Firing = [];

    private static Thread? _worker;

    internal static Entry Arm(V8ScriptEngine engine, TimeSpan budget)
    {
        long milliseconds = budget <= TimeSpan.Zero
            ? 0
            : (long)Math.Min(budget.TotalMilliseconds, int.MaxValue);
        Entry entry = new(engine, Environment.TickCount64 + milliseconds);
        lock (Gate)
        {
            Armed.Add(entry);
            if (_worker is null)
            {
                _worker = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "obscura-v8-watchdog",
                };
                _worker.Start();
            }

            Monitor.Pulse(Gate);
        }

        return entry;
    }

    internal static void Cancel(Entry entry)
    {
        lock (Gate)
        {
            Armed.Remove(entry);

            // A firing already under way has to finish before the caller may treat
            // this engine as free of interrupts.
            while (Firing.Contains(entry))
            {
                Monitor.Wait(Gate);
            }

            Monitor.Pulse(Gate);
        }
    }

    private static void Loop()
    {
        lock (Gate)
        {
            while (true)
            {
                long? next = FireExpiredAndFindNextDelay();
                if (next is null)
                {
                    Monitor.Wait(Gate);
                }
                else
                {
                    Monitor.Wait(Gate, (int)Math.Clamp(next.Value, 1, int.MaxValue));
                }
            }
        }
    }

    /// <summary>
    /// Interrupt every overrun engine and report how long until the nearest deadline
    /// left, or null when nothing is armed. Called with <see cref="Gate"/> held.
    /// </summary>
    /// <remarks>
    /// Never inlined, for the reason <c>CdpWatchdogCore</c> documents: this thread
    /// parks on <see cref="Monitor.Wait(object)"/> for as long as nothing is armed,
    /// and an <see cref="Entry"/> left in the calling frame's stack slots would keep
    /// its <see cref="V8ScriptEngine"/> - and through it a whole document - alive for
    /// that entire time.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long? FireExpiredAndFindNextDelay()
    {
        long now = Environment.TickCount64;
        List<Entry>? expired = null;
        foreach (Entry entry in Armed)
        {
            if (entry.Deadline <= now)
            {
                (expired ??= []).Add(entry);
            }
        }

        if (expired is not null)
        {
            foreach (Entry entry in expired)
            {
                Armed.Remove(entry);
                Firing.Add(entry);
            }

            foreach (Entry entry in expired)
            {
                // Interrupt outside the lock: ClearScript may block, and a canceller
                // waiting on this firing must be able to reach its Monitor.Wait.
                Monitor.Exit(Gate);
                try
                {
                    entry.Fire();
                }
                finally
                {
                    Monitor.Enter(Gate);
                }
            }

            foreach (Entry entry in expired)
            {
                Firing.Remove(entry);
            }

            Monitor.PulseAll(Gate);
        }

        long? next = null;
        foreach (Entry entry in Armed)
        {
            long remaining = entry.Deadline - now;
            if (next is null || remaining < next)
            {
                next = remaining;
            }
        }

        return next;
    }
}

/// <summary>
/// A hard wall-clock backstop on synchronous V8 work.
/// </summary>
/// <remarks>
/// <para>
/// This is load-bearing and it is why the port does not simply use a
/// <see cref="CancellationToken"/>: a page stuck in a synchronous loop or a
/// microtask storm pins the OS thread inside V8, so any timeout that can only
/// be observed at an <c>await</c> point never fires. The Rust engine terminates
/// the isolate from a separate thread through
/// <c>IsolateHandle::terminate_execution</c>; ClearScript's equivalent is
/// <see cref="Microsoft.ClearScript.ScriptEngine.Interrupt"/>, which is
/// documented as callable from any thread and which raises
/// <see cref="Microsoft.ClearScript.ScriptInterruptedException"/> out of the
/// running script.
/// </para>
/// <para>
/// <c>OpGuard</c> deliberately does not contain
/// <c>ScriptInterruptedException</c>, so a termination raised inside an op
/// propagates instead of being converted into an op failure value.
/// </para>
/// </remarks>
public static class Watchdog
{
    /// <summary>
    /// Arms an interrupt on <paramref name="engine"/> after <paramref name="budget"/>.
    /// </summary>
    public static WatchdogToken Spawn(V8ScriptEngine engine, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(engine);

        return new WatchdogToken(WatchdogScheduler.Arm(engine, budget));
    }
}
