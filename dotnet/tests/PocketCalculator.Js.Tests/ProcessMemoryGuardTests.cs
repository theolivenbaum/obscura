using System.Diagnostics;
using PocketCalculator.Js.Modules;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// M7: with <c>POCKETCALCULATOR_MAX_PROCESS_BYTES</c> set, a process over its budget stops
/// running page work and refuses new pages instead of being killed by the OS. The guards here
/// are the tests' own, with a probe they control, so no other test's runtime is touched.
/// </summary>
public sealed class ProcessMemoryGuardTests
{
    [Fact]
    public void TheDefaultGuardIsOffUnlessConfigured()
    {
        if (Environment.GetEnvironmentVariable("POCKETCALCULATOR_MAX_PROCESS_BYTES") is null)
        {
            Assert.Equal(0, ProcessMemoryGuard.Default.LimitBytes);
        }

        var off = new ProcessMemoryGuard(0, () => long.MaxValue, TimeSpan.FromMilliseconds(10));
        off.ThrowIfOverLimit();
    }

    [Fact]
    public void OverTheLimitNewWorkIsRefused()
    {
        long used = 100;
        var guard = new ProcessMemoryGuard(1000, () => Volatile.Read(ref used), TimeSpan.FromMilliseconds(10));
        guard.ThrowIfOverLimit();
        Volatile.Write(ref used, 5000);
        var error = Assert.Throws<ProcessMemoryExceededException>(guard.ThrowIfOverLimit);
        Assert.Equal(5000, error.Used);
        Assert.Equal(1000, error.Limit);
    }

    [Fact]
    public void OverTheLimitRunningScriptIsTerminated()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        long used = 100;
        var guard = new ProcessMemoryGuard(1000, () => Volatile.Read(ref used), TimeSpan.FromMilliseconds(20));
        using var watch = fixture.Runtime.WatchMemoryWith(guard);

        // Goes over once the loop is running.
        using var timer = new Timer(_ => Volatile.Write(ref used, 5000), null, 200, Timeout.Infinite);
        var stopwatch = Stopwatch.StartNew();
        var error = Record.Exception(() => fixture.Runtime.Evaluate("(() => { for (;;) {} })()"));
        stopwatch.Stop();

        Assert.NotNull(error);
        Assert.True(guard.Trips >= 1);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), stopwatch.Elapsed.ToString());

        // Back under the limit, the page can be reset and used again.
        Volatile.Write(ref used, 100);
        fixture.Runtime.CancelTermination();
        Assert.Equal("2", fixture.Runtime.Evaluate("1 + 1")?.ToString());
    }

    [Fact]
    public void AReleasedIsolateIsNoLongerWatched()
    {
        var handle = new CountingIsolate();
        var guard = new ProcessMemoryGuard(1000, () => 5000, TimeSpan.FromMilliseconds(10));
        guard.Register(handle).Dispose();
        Thread.Sleep(100);
        Assert.Equal(0, handle.Terminations);
    }

    private sealed class CountingIsolate : IIsolateHandle
    {
        public int Terminations;

        public void TerminateExecution() => Interlocked.Increment(ref Terminations);
    }
}
