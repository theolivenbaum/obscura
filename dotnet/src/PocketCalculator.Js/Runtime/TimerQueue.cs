using System.Diagnostics;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// The host timer queue behind <c>Deno.core.queueUserTimer</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shim schedules every <c>setTimeout</c> and <c>setInterval</c> through
/// this queue, with one exception: a child frame realm cannot use it (the Rust
/// engine's realms lack the per-context state deno_core's timer queue needs), so
/// frame timers go through <c>op_sleep</c> and carry negative ids. Ids handed
/// out here are therefore always positive, and <c>clearTimeout</c> uses the sign
/// to tell the two queues apart. Do not hand out zero or negative ids.
/// </para>
/// <para>
/// Callbacks fire only when the embedder pumps the loop, never spontaneously.
/// A page that is never pumped simply accumulates pending timers, which is what
/// a host doing a synchronous geometry mutation and capture expects.
/// </para>
/// <para>
/// The queue is locked. deno_core's queue is touched from one thread only; here an
/// async op's promise is resolved by ClearScript on the thread that completed the
/// op's <see cref="Task"/>, and the page's continuation (which may call
/// <c>setTimeout</c>) runs there while the pump thread is between scripts, taking
/// the due set. Unlocked, that threw "Collection was modified" out of the pump on
/// reddit.com, and could as easily have lost an entry.
/// </para>
/// </remarks>
public sealed class TimerQueue
{
    internal readonly record struct Entry(long Id, double DueMs, double IntervalMs, bool Repeat, object Callback, long Sequence);

    /// <summary>A timer taken by <see cref="TakeDueTimers"/>, to run or to hand back.</summary>
    public readonly struct DueTimer
    {
        internal DueTimer(Entry entry) => Value = entry;

        internal Entry Value { get; }

        /// <summary>The callback to run.</summary>
        public object Callback => Value.Callback;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _byId = [];

    // The one-shots of the last batch taken, until a clearTimeout reaches one: Restore
    // must not bring back a timer the batch's own callbacks cancelled.
    private readonly HashSet<long> _taken = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextId = 1;
    private long _sequence;

    /// <summary>Milliseconds since the queue was created.</summary>
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Number of timers still scheduled.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    /// <summary>Schedules a callback and returns its (always positive) id.</summary>
    public long Add(double delayMs, bool repeat, object callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        // HTML clamps a negative or non-finite delay to zero rather than
        // rejecting it; page script relies on setTimeout(fn, -1) firing.
        var delay = double.IsFinite(delayMs) && delayMs > 0 ? delayMs : 0;
        lock (_gate)
        {
            var id = _nextId++;
            _byId[id] = new Entry(id, NowMs + delay, delay, repeat, callback, _sequence++);
            return id;
        }
    }

    /// <summary>Cancels a timer. Cancelling an unknown or already-fired id is a no-op.</summary>
    public void Cancel(long id)
    {
        lock (_gate)
        {
            _byId.Remove(id);
            _taken.Remove(id);
        }
    }

    /// <summary>
    /// Milliseconds until the earliest pending timer is due, or null when none
    /// are scheduled. Zero means at least one is already due.
    /// </summary>
    public double? NextDelayMs()
    {
        lock (_gate)
        {
            if (_byId.Count == 0)
            {
                return null;
            }
            var now = NowMs;
            var earliest = double.MaxValue;
            foreach (var entry in _byId.Values)
            {
                if (entry.DueMs < earliest)
                {
                    earliest = entry.DueMs;
                }
            }
            return Math.Max(0, earliest - now);
        }
    }

    /// <summary>
    /// Collects the timers due at the current instant, in fire order, removing
    /// one-shots and rescheduling repeats.
    /// </summary>
    /// <remarks>
    /// The due set is snapshotted before any callback runs. A callback that
    /// schedules a zero-delay timer would otherwise be re-collected in the same
    /// turn and starve the rest of the loop, which is exactly the "never regain
    /// control" case the shim warns about.
    /// </remarks>
    public IReadOnlyList<object> TakeDue()
    {
        var due = TakeDueTimers();
        if (due.Count == 0)
        {
            return [];
        }

        var callbacks = new List<object>(due.Count);
        foreach (var timer in due)
        {
            callbacks.Add(timer.Callback);
        }
        return callbacks;
    }

    /// <summary>
    /// <see cref="TakeDue"/>, keeping what <see cref="Restore"/> needs to put back the
    /// timers a batch did not get to run.
    /// </summary>
    public IReadOnlyList<DueTimer> TakeDueTimers()
    {
        lock (_gate)
        {
            if (_byId.Count == 0)
            {
                return [];
            }

            _taken.Clear();
            var now = NowMs;
            List<Entry>? due = null;
            foreach (var entry in _byId.Values)
            {
                if (entry.DueMs <= now)
                {
                    (due ??= []).Add(entry);
                }
            }
            if (due is null)
            {
                return [];
            }

            // Equal deadlines fire in scheduling order, as the HTML timer task
            // source requires.
            due.Sort(static (a, b) =>
            {
                var byDue = a.DueMs.CompareTo(b.DueMs);
                return byDue != 0 ? byDue : a.Sequence.CompareTo(b.Sequence);
            });

            var taken = new List<DueTimer>(due.Count);
            foreach (var entry in due)
            {
                taken.Add(new DueTimer(entry));
                if (entry.Repeat)
                {
                    // Reschedule from now, not from the old deadline: a page that
                    // was not pumped for a while must not get a burst of catch-up
                    // firings.
                    _byId[entry.Id] = entry with { DueMs = now + entry.IntervalMs, Sequence = _sequence++ };
                }
                else
                {
                    _byId.Remove(entry.Id);
                    _taken.Add(entry.Id);
                }
            }
            return taken;
        }
    }

    /// <summary>
    /// Put back one-shot timers a batch took but never ran, with their ids, deadlines
    /// and order, so they fire on the next turn and <c>clearTimeout</c> still reaches them.
    /// </summary>
    /// <remarks>
    /// A watchdog that interrupts one callback ends the pump's turn there. The timers
    /// after it in the batch were already out of the queue, and dropping them lost their
    /// work for good: a dynamic script whose execution was such a timer never ran and
    /// never fired <c>load</c>, so the document's load event waited out the whole script
    /// deadline (reddit.com, behind an animation frame that forced a 5s layout). A
    /// repeating timer is already rescheduled, so it is left as it stands.
    /// </remarks>
    public void Restore(IReadOnlyList<DueTimer> timers, int start)
    {
        ArgumentNullException.ThrowIfNull(timers);
        lock (_gate)
        {
            for (var index = Math.Max(0, start); index < timers.Count; index++)
            {
                var entry = timers[index].Value;
                if (!entry.Repeat && _taken.Remove(entry.Id))
                {
                    _byId.TryAdd(entry.Id, entry);
                }
            }
        }
    }

    /// <summary>Drops every scheduled timer, as a navigation does.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byId.Clear();
            _taken.Clear();
        }
    }
}
