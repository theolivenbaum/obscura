// No Rust counterpart: deno_core runs every op reaction on the thread that owns the isolate.
// The port now does too (OpCompletionContext), but script entered from another thread (an
// embedder's) can still run beside host code; IsolateLock is what keeps captures from
// interleaving with it. See "Known deviations" in todo.md.
using PocketCalculator.Js.Ops;
using Xunit;

namespace PocketCalculator.Js.Tests;

public sealed class IsolateLockTests
{
    /// <summary>
    /// Script entered from another thread waits while host code holds the lock, and runs
    /// again once it is released (samsung.com's capture saw page script add nodes under its
    /// painter: "Collection was modified").
    /// </summary>
    [Fact]
    public void ScriptOnAnotherThreadDoesNotRunWhileTheLockIsHeld()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        long ran = 0;
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                runtime.Evaluate("(document.body.appendChild(document.createElement('i')), 1)");
                Interlocked.Increment(ref ran);
            }
        });

        try
        {
            SpinWait.SpinUntil(() => Interlocked.Read(ref ran) > 5, TimeSpan.FromSeconds(10));
            (long before, long after, int nodesBefore, int nodesAfter) = runtime.State.IsolateLock.Run(() =>
            {
                long start = Interlocked.Read(ref ran);
                int nodes = runtime.State.Dom!.Count;
                Thread.Sleep(300);
                return (start, Interlocked.Read(ref ran), nodes, runtime.State.Dom!.Count);
            });

            // At most the one evaluation that had already returned when the lock was taken.
            Assert.True(after - before <= 1, $"script ran {after - before} times under the lock");
            Assert.Equal(nodesBefore, nodesAfter);

            long released = Interlocked.Read(ref ran);
            Assert.True(
                SpinWait.SpinUntil(() => Interlocked.Read(ref ran) > released + 5, TimeSpan.FromSeconds(10)),
                "script must run again once the lock is released");
        }
        finally
        {
            stop.Cancel();
            writer.Wait(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>The body's result and exceptions come back as they are; it may call into script.</summary>
    [Fact]
    public void TheBodyMayEnterScriptAndItsFailuresAreNotWrapped()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        Assert.Equal(
            "2",
            runtime.State.IsolateLock.Run(() => runtime.Evaluate("1 + 1")?.ToJsonString()));
        Assert.Throws<InvalidOperationException>(
            () => runtime.State.IsolateLock.Run<int>(() => throw new InvalidOperationException("boom")));
        Assert.Equal(7, IsolateLock.None.Run(() => 7));
    }
}
