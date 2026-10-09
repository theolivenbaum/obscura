using System.Diagnostics;
using System.Text;

namespace PocketCalculator.Render;

/// <summary>
/// Per-phase wall-clock timings for one prepare, written to stderr when
/// <c>POCKETCALCULATOR_LAYOUT_PROFILE=1</c>. A diagnostic only: with the variable unset every
/// call is a single static-bool test.
/// </summary>
internal static class LayoutPhaseProfile
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("POCKETCALCULATOR_LAYOUT_PROFILE") == "1";

    [ThreadStatic]
    private static List<(string Name, long Ticks)>? t_phases;

    [ThreadStatic]
    private static long t_last;

    [ThreadStatic]
    private static List<(string Name, long Value)>? t_notes;

    // Collections and pause time when the prepare began, for the gc notes End prints.
    private static long s_lastEnd;

    [ThreadStatic]
    private static (int Gen0, int Gen1, int Gen2, TimeSpan Pause, long Allocated) t_gc;

    internal static void Begin()
    {
        if (!Enabled)
        {
            return;
        }

        t_phases = [];
        t_notes = [];
        t_gc = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration(),
            GC.GetTotalAllocatedBytes());
        t_last = Stopwatch.GetTimestamp();
    }

    internal static void Mark(string name)
    {
        if (!Enabled || t_phases is null)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        t_phases.Add((name, now - t_last));
        t_last = now;
    }

    /// <summary>Record a count (boxes carried over, for instance) alongside the timings.</summary>
    internal static void Note(string name, long value)
    {
        if (Enabled && t_notes is not null)
        {
            t_notes.Add((name, value));
        }
    }

    internal static void End(string label)
    {
        if (!Enabled || t_phases is null)
        {
            return;
        }

        Mark("tail");
        Note("gc0", GC.CollectionCount(0) - t_gc.Gen0);
        Note("gc1", GC.CollectionCount(1) - t_gc.Gen1);
        Note("gc2", GC.CollectionCount(2) - t_gc.Gen2);
        Note("gcPauseMs", (long)(GC.GetTotalPauseDuration() - t_gc.Pause).TotalMilliseconds);
        Note("allocKB", (GC.GetTotalAllocatedBytes() - t_gc.Allocated) / 1024);

        // What the process allocated between the end of the previous prepare and the start of
        // this one (scripts, ops, other threads).
        Note("allocBetweenKB", s_lastEnd == 0 ? 0 : (t_gc.Allocated - s_lastEnd) / 1024);
        s_lastEnd = GC.GetTotalAllocatedBytes();
        StringBuilder text = new();
        long total = 0;
        Dictionary<string, long> merged = [];
        List<string> order = [];
        foreach ((string name, long ticks) in t_phases)
        {
            total += ticks;
            if (!merged.TryAdd(name, ticks))
            {
                merged[name] += ticks;
            }
            else
            {
                order.Add(name);
            }
        }

        text.Append("LAYOUT ").Append(label).Append(' ')
            .Append((total * 1000.0 / Stopwatch.Frequency).ToString("F1")).Append("ms");
        foreach (string name in order)
        {
            double ms = merged[name] * 1000.0 / Stopwatch.Frequency;
            if (ms >= 0.5)
            {
                text.Append(' ').Append(name).Append('=').Append(ms.ToString("F1"));
            }
        }

        foreach ((string name, long value) in t_notes ?? [])
        {
            text.Append(' ').Append(name).Append(':').Append(value);
        }

        Console.Error.WriteLine(text.ToString());
        t_phases = null;
        t_notes = null;
    }
}
