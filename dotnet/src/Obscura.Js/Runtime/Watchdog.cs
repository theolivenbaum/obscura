using Microsoft.ClearScript.V8;

namespace Obscura.Js.Runtime;

/// <summary>
/// Handle to an armed V8 execution watchdog (see <see cref="Watchdog.Spawn"/>).
/// Holds the cancel signal and the watchdog thread; pass it back to
/// <see cref="ObscuraJsRuntime.DisarmWatchdog"/> to stop the watchdog and learn
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
    private readonly ManualResetEventSlim _cancelled;
    private readonly Thread _thread;
    private int _stopped;

    internal WatchdogToken(Thread thread, ManualResetEventSlim cancelled, Func<bool> fired)
    {
        _thread = thread;
        _cancelled = cancelled;
        FiredProbe = fired;
    }

    internal Func<bool> FiredProbe { get; }

    /// <summary>
    /// Stop the watchdog. Returns true if it had already fired (interrupted the
    /// engine). The caller must then clear any lingering interrupt through
    /// <see cref="ObscuraJsRuntime.CancelTermination"/> before the next eval.
    /// </summary>
    public bool Stop()
    {
        CancelAndJoin();
        return FiredProbe();
    }

    private void CancelAndJoin()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }
        _cancelled.Set();
        _thread.Join();
    }

    public void Dispose()
    {
        CancelAndJoin();
        _cancelled.Dispose();
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

        var cancelled = new ManualResetEventSlim(false);
        var fired = new BoolBox();
        var thread = new Thread(() =>
        {
            var deadline = Environment.TickCount64 + (long)Math.Max(0, budget.TotalMilliseconds);
            while (true)
            {
                // Check first: Stop() may have signalled before this thread even
                // started, which happens constantly for fast CDP commands where
                // the token is stopped right after Spawn. Without this top check
                // the join blocks for the whole budget.
                if (cancelled.IsSet)
                {
                    return;
                }
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    Volatile.Write(ref fired.Value, true);
                    try
                    {
                        engine.Interrupt();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The engine went away first; nothing to interrupt.
                    }
                    catch (InvalidOperationException)
                    {
                        // Same, reported differently by some ClearScript paths.
                    }
                    return;
                }
                if (cancelled.Wait((int)Math.Min(remaining, int.MaxValue)))
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "obscura-v8-watchdog",
        };
        thread.Start();
        return new WatchdogToken(thread, cancelled, () => Volatile.Read(ref fired.Value));
    }

    private sealed class BoolBox
    {
        public bool Value;
    }
}
