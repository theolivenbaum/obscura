using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// CPU time the calling thread has used, for complexity bounds that should not move with
/// how busy the machine is.
/// </summary>
/// <remarks>
/// ClearScript runs script on the calling thread, so a synchronous <c>Evaluate</c> is
/// charged to it. Wall time is what a loaded host inflates: the same 40000 appends that take
/// a second alone took 14 s while other test processes held every core. On a platform
/// without <c>CLOCK_THREAD_CPUTIME_ID</c> this falls back to wall time.
/// </remarks>
internal static class ThreadCpuTime
{
    private const int ClockThreadCpuTimeId = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out TimeSpec time);

    /// <summary>Milliseconds of CPU the current thread spent running <paramref name="work"/>.</summary>
    public static double Measure(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!OperatingSystem.IsLinux())
        {
            var clock = Stopwatch.StartNew();
            work();
            return clock.Elapsed.TotalMilliseconds;
        }

        var before = Now();
        work();
        return (Now() - before) / 1_000_000.0;
    }

    private static long Now() =>
        ClockGetTime(ClockThreadCpuTimeId, out var time) == 0
            ? (time.Seconds * 1_000_000_000L) + time.Nanoseconds
            : throw new InvalidOperationException("clock_gettime(CLOCK_THREAD_CPUTIME_ID) failed");
}
