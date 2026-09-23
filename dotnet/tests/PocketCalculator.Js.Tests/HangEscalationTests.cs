using System.Diagnostics;
using PocketCalculator.Js.Modules;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// L12: a command the watchdog interrupted but that never returns (stuck in C# beneath an
/// op, where the interrupt cannot reach) escalates to <see cref="HangEscalation.Handler"/>,
/// and one that returns does not.
/// </summary>
[Collection(nameof(HangEscalationCollection))]
public sealed class HangEscalationTests
{
    private sealed class InertHandle : IIsolateHandle
    {
        public void TerminateExecution()
        {
            // Stands in for a thread stuck in C#: the interrupt is delivered and ignored.
        }
    }

    [Fact]
    public void AnInterruptedCommandThatNeverReturnsEscalates()
    {
        var core = new CdpWatchdogCore("hang-escalation-test");
        var escalated = new ManualResetEventSlim();
        string? reason = null;
        TimeSpan savedGrace = HangEscalation.Grace;
        HangEscalation.Grace = TimeSpan.FromMilliseconds(200);
        HangEscalation.Handler = what =>
        {
            reason = what;
            escalated.Set();
        };
        try
        {
            ArmedWatchdog armed = core.Arm(new InertHandle(), TimeSpan.FromMilliseconds(10));
            Assert.True(SpinUntil(() => armed.Fired, TimeSpan.FromSeconds(10)), "watchdog never fired");
            Assert.True(escalated.Wait(TimeSpan.FromSeconds(10)), "a stuck command did not escalate");
            Assert.Contains("CDP command", reason, StringComparison.Ordinal);
            Assert.False(HangEscalation.IsPending(armed));
        }
        finally
        {
            HangEscalation.Handler = null;
            HangEscalation.Grace = savedGrace;
        }
    }

    [Fact]
    public void AnInterruptedCommandThatReturnsDoesNotEscalate()
    {
        var core = new CdpWatchdogCore("hang-settle-test");
        TimeSpan savedGrace = HangEscalation.Grace;
        HangEscalation.Grace = TimeSpan.FromMinutes(10);
        HangEscalation.Handler = _ => { };
        try
        {
            ArmedWatchdog armed = core.Arm(new InertHandle(), TimeSpan.FromMilliseconds(10));
            Assert.True(SpinUntil(() => armed.Fired, TimeSpan.FromSeconds(10)), "watchdog never fired");
            Assert.True(HangEscalation.IsPending(armed));
            Assert.True(core.Disarm(armed));
            Assert.False(HangEscalation.IsPending(armed));
        }
        finally
        {
            HangEscalation.Handler = null;
            HangEscalation.Grace = savedGrace;
        }
    }

    [Fact]
    public void WithNoHandlerNothingIsTracked()
    {
        var core = new CdpWatchdogCore("hang-untracked-test");
        ArmedWatchdog armed = core.Arm(new InertHandle(), TimeSpan.FromMilliseconds(10));
        Assert.True(SpinUntil(() => armed.Fired, TimeSpan.FromSeconds(10)), "watchdog never fired");
        Assert.False(HangEscalation.IsPending(armed));
        core.Disarm(armed);
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }
}

/// <summary>The escalation handler is process-wide, so its tests run alone.</summary>
[CollectionDefinition(nameof(HangEscalationCollection), DisableParallelization = true)]
public sealed class HangEscalationCollection;
