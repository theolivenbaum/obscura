using System.Diagnostics;

namespace Obscura.Js.Runtime;

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
/// </remarks>
public sealed class TimerQueue
{
    private readonly record struct Entry(long Id, double DueMs, double IntervalMs, bool Repeat, object Callback, long Sequence);

    private readonly Dictionary<long, Entry> _byId = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _nextId = 1;
    private long _sequence;

    /// <summary>Milliseconds since the queue was created.</summary>
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Number of timers still scheduled.</summary>
    public int Count => _byId.Count;

    /// <summary>Schedules a callback and returns its (always positive) id.</summary>
    public long Add(double delayMs, bool repeat, object callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        // HTML clamps a negative or non-finite delay to zero rather than
        // rejecting it; page script relies on setTimeout(fn, -1) firing.
        var delay = double.IsFinite(delayMs) && delayMs > 0 ? delayMs : 0;
        var id = _nextId++;
        _byId[id] = new Entry(id, NowMs + delay, delay, repeat, callback, _sequence++);
        return id;
    }

    /// <summary>Cancels a timer. Cancelling an unknown or already-fired id is a no-op.</summary>
    public void Cancel(long id) => _byId.Remove(id);

    /// <summary>
    /// Milliseconds until the earliest pending timer is due, or null when none
    /// are scheduled. Zero means at least one is already due.
    /// </summary>
    public double? NextDelayMs()
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
        if (_byId.Count == 0)
        {
            return [];
        }

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

        var callbacks = new List<object>(due.Count);
        foreach (var entry in due)
        {
            callbacks.Add(entry.Callback);
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
            }
        }
        return callbacks;
    }

    /// <summary>Drops every scheduled timer, as a navigation does.</summary>
    public void Clear() => _byId.Clear();
}
