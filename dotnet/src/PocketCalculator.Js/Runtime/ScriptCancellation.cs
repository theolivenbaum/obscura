namespace PocketCalculator.Js.Runtime;

/// <summary>
/// The cancellation source a watchdog trips alongside <c>V8ScriptEngine.Interrupt()</c>,
/// so C# work running inside an op stops too.
/// </summary>
/// <remarks>
/// <para>
/// One per isolate: the page runtime and its frame realms share it, as they share the
/// isolate an interrupt terminates. <see cref="Cancel"/> is called from the watchdog
/// thread before the interrupt; <see cref="Reset"/> from
/// <c>PocketCalculatorJsRuntime.CancelTermination</c>, which every fired watchdog path
/// already calls to make the isolate usable again, and when a fired watchdog settles.
/// A cancelled <see cref="CancellationTokenSource"/> cannot be reset, so
/// <see cref="Reset"/> swaps in a fresh one; tokens taken before the swap stay
/// cancelled, which is what a pass that started under the old deadline should see.
/// </para>
/// <para>
/// A deadline nobody reset (a watchdog leaked without being disarmed) expires after
/// <see cref="StaleAfter"/> for work started from then on, so a leak costs that page a
/// bounded window instead of every later script.
/// </para>
/// </remarks>
public sealed class ScriptCancellation
{
    /// <summary>How long a cancelled deadline nobody reset keeps applying to new work.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ReinterruptInterval = TimeSpan.FromMilliseconds(2);

    private readonly object _gate = new();
    private CancellationTokenSource _source = new();
    private long _cancelledAt;
    private CancellationTokenSource? _reinterrupting;

    /// <summary>
    /// Interrupts the isolate, returning false once there is no engine left to interrupt.
    /// Set by the runtime that owns this source.
    /// </summary>
    internal Func<bool>? Interrupter { get; set; }

    /// <summary>The token for work started now.</summary>
    public CancellationToken Token
    {
        get
        {
            var source = Volatile.Read(ref _source);
            if (source.IsCancellationRequested
                && Environment.TickCount64 - Volatile.Read(ref _cancelledAt) > (long)StaleAfter.TotalMilliseconds)
            {
                Reset();
                source = Volatile.Read(ref _source);
            }

            return source.Token;
        }
    }

    /// <summary>Whether the current deadline has passed and not been reset.</summary>
    public bool IsCancellationRequested => Volatile.Read(ref _source).IsCancellationRequested;

    /// <summary>Cancel the work running under the current token. Safe from any thread.</summary>
    public void Cancel()
    {
        var source = Volatile.Read(ref _source);
        if (!source.IsCancellationRequested)
        {
            Volatile.Write(ref _cancelledAt, Environment.TickCount64);
        }

        try
        {
            source.Cancel();
        }
        catch (AggregateException)
        {
            // A registration threw; the token is cancelled regardless.
        }
    }

    /// <summary>
    /// Start a fresh deadline window if the current one was cancelled. Once this returns,
    /// the loop <see cref="EnsureInterrupted"/> started can no longer interrupt the isolate,
    /// so a caller may then clear the pending interrupt and trust it stays cleared.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (!_source.IsCancellationRequested)
            {
                return;
            }

            // Not disposed: a pass on another thread may still hold its token, and a
            // source with no timer owns nothing that needs releasing.
            Volatile.Write(ref _source, new CancellationTokenSource());
        }
    }

    /// <summary>
    /// Keep the isolate's interrupt coming until the script that ran the cancelled op has
    /// been terminated, that is until <see cref="Reset"/>.
    /// </summary>
    /// <remarks>
    /// An interrupt raised while V8 is inside a long host callback does not take effect: the
    /// watchdog's one interrupt was spent while the op was still in C#, and the script
    /// carried on once the op returned (and, before this, could then run unbounded). An
    /// interrupt requested from inside the callback does not survive it either. So the op
    /// that observed the cancellation starts this loop and returns normally; the next
    /// interrupt lands once control is back in JavaScript. It stops when the deadline is
    /// reset, when the engine is gone, or after <see cref="StaleAfter"/>.
    /// </remarks>
    internal void EnsureInterrupted()
    {
        CancellationTokenSource cancelled = Volatile.Read(ref _source);
        if (Interrupter is not { } interrupt
            || !cancelled.IsCancellationRequested
            || Interlocked.CompareExchange(ref _reinterrupting, cancelled, null) is not null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                long stopAt = Environment.TickCount64 + (long)StaleAfter.TotalMilliseconds;
                while (Environment.TickCount64 < stopAt)
                {
                    // Checked and interrupted under the gate Reset takes, so no interrupt
                    // from this loop can land after the deadline has been reset.
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_source, cancelled) || !interrupt())
                        {
                            return;
                        }
                    }

                    Thread.Sleep(ReinterruptInterval);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _reinterrupting, null, cancelled);
            }
        })
        {
            IsBackground = true,
            Name = "pocket-calculator reinterrupt",
        };
        thread.Start();
    }
}
