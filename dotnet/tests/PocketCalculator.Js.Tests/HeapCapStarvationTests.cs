using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The heap cap when its sample comes late. ClearScript samples the heap from a timer whose
/// callback needs a .NET thread-pool thread, and V8 queues its own background GC and
/// compile work on that pool, so a saturated pool delays the check. V8 then reached its
/// own heap limit first and aborted the process ("Fatal JavaScript out of memory: Reached
/// heap limit", exit 134): the intermittent crash of this test host under load, from
/// <c>RuntimeTests.HeapLimitTerminatesScriptAndRuntimeRecovers</c>. See
/// <see cref="PocketCalculatorJsRuntime.HeapCapExpansionMultiplier"/>.
/// </summary>
/// <remarks>
/// Starving the pool is process-wide, so these tests run alone. Before the fix the test
/// does not fail: the test host dies.
/// </remarks>
[Collection(nameof(ThreadPoolStarvationCollection))]
public sealed class HeapCapStarvationTests
{
    // About 200 MB/s: past V8's 64 MB limit well inside the starvation window, and a
    // bounded overshoot (a few hundred MB) once V8 grows its limit instead of aborting.
    private const string Allocate =
        "(() => { const chunks = []; for (;;) { chunks.push(new Array(262144).fill(1.25));"
        + " const t = Date.now(); while (Date.now() - t < 10) {} } })()";

    [Fact]
    public void ALateHeapSampleTerminatesTheScriptInsteadOfAbortingTheProcess()
    {
        PocketCalculatorJsRuntime.V8OldSpaceLimitMbForTests.Value = 64;
        RuntimeFixture fixture;
        try
        {
            fixture = RuntimeFixture.Blank();
        }
        finally
        {
            PocketCalculatorJsRuntime.V8OldSpaceLimitMbForTests.Value = null;
        }

        using (fixture)
        {
            var rt = fixture.Runtime;
            rt.SetHeapLimit(32 * 1024 * 1024);

            JsRuntimeException error;
            using (StarveThreadPool(TimeSpan.FromSeconds(1)))
            {
                error = Assert.Throws<JsRuntimeException>(() => rt.Evaluate(Allocate));
            }

            Assert.Contains("heap limit exceeded", error.Message, StringComparison.Ordinal);
            Assert.True(rt.Evaluate("globalThis.__survived_late_sample = true")!.GetValue<bool>());
        }
    }

    /// <summary>
    /// Holds every thread-pool worker for <paramref name="duration"/> (measured from now),
    /// as a saturated pool does; disposing restores the pool and waits for the release.
    /// </summary>
    private static IDisposable StarveThreadPool(TimeSpan duration)
    {
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        Assert.True(ThreadPool.SetMaxThreads(minWorkers, maxIo));
        var gate = new ManualResetEventSlim();
        for (int i = 0; i < 4 * Math.Max(minWorkers, ThreadPool.ThreadCount); i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static g => g.Wait(), gate, preferLocal: false);
        }

        var release = new Thread(() =>
        {
            Thread.Sleep(duration);
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
            gate.Set();
        })
        {
            IsBackground = true,
            Name = "starvation-release",
        };
        release.Start();
        return new Release(release);
    }

    // The gate is not disposed: blockers the pool has not started yet still read it.
    private sealed class Release(Thread thread) : IDisposable
    {
        public void Dispose() => thread.Join();
    }
}

/// <summary>Tests that starve the process's thread pool, so nothing else runs beside them.</summary>
[CollectionDefinition(nameof(ThreadPoolStarvationCollection), DisableParallelization = true)]
public sealed class ThreadPoolStarvationCollection;
