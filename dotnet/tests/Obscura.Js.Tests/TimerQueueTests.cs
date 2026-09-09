using Obscura.Js.Runtime;
using Xunit;

namespace Obscura.Js.Tests;

public sealed class TimerQueueTests
{
    [Fact]
    public void Ids_are_always_positive()
    {
        // clearTimeout distinguishes host timers from frame timers by sign:
        // frame realms use op_sleep and carry negative ids. A zero or negative
        // id handed out here would be routed to the wrong queue.
        var q = new TimerQueue();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(q.Add(0, repeat: false, callback: new object()) > 0);
        }
    }

    [Fact]
    public void A_zero_delay_timer_is_due_immediately()
    {
        var q = new TimerQueue();
        var cb = new object();
        q.Add(0, repeat: false, cb);
        Assert.Equal([cb], q.TakeDue());
    }

    [Fact]
    public void A_future_timer_is_not_taken_yet()
    {
        var q = new TimerQueue();
        q.Add(60_000, repeat: false, new object());
        Assert.Empty(q.TakeDue());
        Assert.Equal(1, q.Count);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void A_negative_or_non_finite_delay_is_clamped_to_zero(double delay)
    {
        // HTML clamps rather than rejecting; page script relies on
        // setTimeout(fn, -1) firing on the next turn.
        var q = new TimerQueue();
        var cb = new object();
        q.Add(delay, repeat: false, cb);
        Assert.Equal([cb], q.TakeDue());
    }

    [Fact]
    public void Equal_deadlines_fire_in_scheduling_order()
    {
        var q = new TimerQueue();
        object a = "a", b = "b", c = "c";
        q.Add(0, false, a);
        q.Add(0, false, b);
        q.Add(0, false, c);
        Assert.Equal([a, b, c], q.TakeDue());
    }

    [Fact]
    public void A_one_shot_is_removed_after_firing()
    {
        var q = new TimerQueue();
        q.Add(0, repeat: false, new object());
        Assert.Single(q.TakeDue());
        Assert.Equal(0, q.Count);
        Assert.Empty(q.TakeDue());
    }

    [Fact]
    public void A_repeating_timer_stays_scheduled()
    {
        var q = new TimerQueue();
        q.Add(0, repeat: true, new object());
        Assert.Single(q.TakeDue());
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void A_repeating_timer_does_not_burst_after_a_long_pause()
    {
        // A page that was not pumped for a while must not receive a flood of
        // catch-up firings: the next deadline is measured from the firing
        // instant, not from the missed one. Pausing for many intervals and
        // then pumping must yield exactly one callback, not one per interval.
        // A 250ms interval paused across ~5 intervals. The assertion is on the
        // count from ONE pump: catch-up would yield one callback per missed
        // interval. The interval is deliberately long relative to test
        // scheduling jitter, so the re-armed deadline cannot pass before the
        // second pump and make this flaky.
        var q = new TimerQueue();
        q.Add(250, repeat: true, new object());
        System.Threading.Thread.Sleep(1200);

        Assert.Single(q.TakeDue());
        Assert.Empty(q.TakeDue());
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void Cancel_removes_a_pending_timer()
    {
        var q = new TimerQueue();
        var id = q.Add(0, false, new object());
        q.Cancel(id);
        Assert.Empty(q.TakeDue());
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Cancelling_an_unknown_or_fired_id_is_a_no_op()
    {
        var q = new TimerQueue();
        var id = q.Add(0, false, new object());
        q.TakeDue();
        q.Cancel(id);
        q.Cancel(9999);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void A_callback_scheduling_a_zero_delay_timer_does_not_starve_the_loop()
    {
        // TakeDue snapshots the due set before any callback runs, so a timer
        // scheduled from inside a callback waits for the next turn. Without
        // that, a self-rescheduling zero-delay timer never returns control and
        // navigation can never complete.
        var q = new TimerQueue();
        q.Add(0, false, "first");
        var taken = q.TakeDue();
        Assert.Equal(["first"], taken);

        q.Add(0, false, "second");
        Assert.Equal(["second"], q.TakeDue());
    }

    [Fact]
    public void NextDelay_reports_null_when_idle_and_zero_when_due()
    {
        var q = new TimerQueue();
        Assert.Null(q.NextDelayMs());

        q.Add(0, false, new object());
        Assert.Equal(0, q.NextDelayMs());

        q.Clear();
        q.Add(60_000, false, new object());
        var next = q.NextDelayMs();
        Assert.NotNull(next);
        Assert.InRange(next.Value, 1, 60_000);
    }

    [Fact]
    public void Clear_drops_every_timer_as_a_navigation_does()
    {
        var q = new TimerQueue();
        q.Add(0, false, new object());
        q.Add(0, true, new object());
        q.Clear();
        Assert.Equal(0, q.Count);
        Assert.Empty(q.TakeDue());
        Assert.Null(q.NextDelayMs());
    }
}
