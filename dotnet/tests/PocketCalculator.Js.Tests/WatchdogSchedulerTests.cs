using System.Diagnostics;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

public class WatchdogSchedulerTests
{
    [Fact]
    public void ArmingDoesNotCreateAThreadPerArm()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;

        // One arm used to start and join a fresh OS thread. RunEventLoopBoundedAsync
        // arms one per event-loop tick and every CDP command arms one, so under load
        // the cost was unbounded and on a small box it aborted the process outright
        // with "ResumeThread failed with error 6" out of Thread.StartCore.
        int before = Process.GetCurrentProcess().Threads.Count;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 2000; i++)
        {
            var token = rt.ArmWatchdog(TimeSpan.FromSeconds(30));
            token.Stop();
        }

        sw.Stop();
        int after = Process.GetCurrentProcess().Threads.Count;
        // One shared scheduler thread, so the delta is 0 or 1 and not 2000. The bound
        // is generous because the pool may grow for unrelated reasons during the run.
        Assert.True(
            after - before < 50,
            $"arming 2000 watchdogs added {after - before} threads (before {before}, after {after})");
    }

    [Fact]
    public void AnArmedWatchdogStillInterruptsARunawayScript()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        var token = rt.ArmWatchdog(TimeSpan.FromMilliseconds(250));
        var sw = Stopwatch.StartNew();
        bool interrupted = false;
        try
        {
            rt.Evaluate("while (true) {}");
        }
        catch (Exception)
        {
            interrupted = true;
        }

        sw.Stop();
        bool fired = token.Stop();
        rt.CancelTermination();
        Assert.True(interrupted, "the runaway script was not interrupted");
        Assert.True(fired, "the token did not report that it fired");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"interrupt was late: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void StoppingBeforeTheDeadlineNeverFires()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        var token = rt.ArmWatchdog(TimeSpan.FromMilliseconds(200));
        Assert.False(token.Stop());
        Thread.Sleep(400);
        Assert.Equal("2", rt.Evaluate("1 + 1")?.ToJsonString());
    }
}
