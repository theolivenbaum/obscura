// SECURITY.md H8: a watchdog that interrupts the isolate also stops the C# work of the op
// (or capture) that is running, instead of waiting for it to return to V8. No counterpart in
// crates/obscura-js, where terminate_execution has the same gap; see "Known deviations" in
// todo.md.
using System.Diagnostics;
using System.Text;
using PocketCalculator.Js.Modules;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

public sealed class OpCancellationTests
{
    /// <summary>
    /// A long page of paragraphs, each wrapping around a float of its own: one layout takes
    /// seconds uncancelled. This used to be 300 nested floats, which took well over a minute
    /// with floats laid out as flex rows and take milliseconds with real float layout.
    /// </summary>
    private static string FloatedParagraphs(int count)
    {
        var html = new StringBuilder("<!doctype html><html><body>");
        for (int i = 0; i < count; i++)
        {
            html.Append(i == count - 1 ? "<p id=deep>" : "<p>")
                .Append("text <span style=\"float:left;width:20px;height:30px\"></span>more words here</p>");
        }

        html.Append("</body></html>");
        return html.ToString();
    }

    [Fact]
    public void AWatchdogStopsALayoutOpInsideCSharp()
    {
        using var fixture = RuntimeFixture.Setup(FloatedParagraphs(8_000));
        var rt = fixture.Runtime;
        var watchdog = rt.ArmWatchdog(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        Assert.Throws<JsRuntimeException>(() =>
            rt.Evaluate("document.getElementById('deep').getBoundingClientRect().height"));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the op ran {clock.Elapsed} past the watchdog");
        Assert.True(rt.DisarmWatchdog(watchdog));

        // Disarming reset the deadline: the page is usable, including for layout once the
        // pathological styles are gone.
        Assert.False(rt.WorkCancellationToken.IsCancellationRequested);
        Assert.Equal(
            "ok",
            rt.Evaluate(
                "(document.body.innerHTML = '<p id=p style=\"height:10px\">ok</p>', "
                + "document.getElementById('p').getBoundingClientRect().height === 10 ? 'ok' : 'bad')")!
                .GetValue<string>());
    }

    [Fact]
    public void APageThatSwallowsTheCancelledOpAndSpinsIsStillTerminated()
    {
        // The watchdog's own interrupt is spent while the op is in C#, and the page catches
        // whatever the op does; the loop that follows must still be stopped.
        using var fixture = RuntimeFixture.Setup(FloatedParagraphs(8_000));
        var rt = fixture.Runtime;
        var watchdog = rt.ArmWatchdog(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        Assert.Throws<JsRuntimeException>(() => rt.Evaluate(
            "(() => { try { document.getElementById('deep').getBoundingClientRect(); } catch (e) {} "
            + "for (;;) {} })()"));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the page ran {clock.Elapsed} past the watchdog");
        Assert.True(rt.DisarmWatchdog(watchdog));
        Assert.Equal(2.0, rt.Evaluate("1 + 1")!.GetValue<double>());
    }

    [Fact]
    public void AWatchdogThatFiresAfterTheOpLeavesLaterWorkAlone()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p id=p style='height:10px'>x</p></body></html>");
        var rt = fixture.Runtime;
        var watchdog = rt.ArmWatchdog(TimeSpan.FromMilliseconds(50));
        Thread.Sleep(200);
        Assert.True(rt.DisarmWatchdog(watchdog));

        Assert.Equal(10.0, rt.Evaluate("document.getElementById('p').getBoundingClientRect().height")!.GetValue<double>());
    }

    [Fact]
    public void ACallersTokenInterruptsScriptAndIsClearedAfterwards()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();
        using (rt.InterruptOnCancellation(deadline.Token))
        {
            Assert.Throws<JsRuntimeException>(() => rt.Evaluate("(() => { while (true) {} })()"));
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal(2.0, rt.Evaluate("1 + 1")!.GetValue<double>());
    }

    [Fact]
    public void ACallersTokenThatNeverFiresChangesNothing()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        using var live = new CancellationTokenSource();
        using (rt.InterruptOnCancellation(live.Token))
        {
            Assert.Equal(2.0, rt.Evaluate("1 + 1")!.GetValue<double>());
        }

        live.Cancel();
        Assert.Equal(3.0, rt.Evaluate("1 + 2")!.GetValue<double>());
    }

    [Fact]
    public void TheCdpCommandWatchdogStopsACaptureOutsideAnyOp()
    {
        using var fixture = RuntimeFixture.Setup(FloatedParagraphs(8_000));
        var rt = fixture.Runtime;
        var armed = CdpWatchdog.Arm(rt.IsolateHandleForWatchdog, TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() => rt.ScreenshotPrepared(rt.State.Viewport, "http://example.com/test"));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"the capture ran {clock.Elapsed} past the watchdog");
        Assert.True(CdpWatchdog.Disarm(armed));
        rt.CancelTermination();
        Assert.False(rt.WorkCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void ResetStartsAFreshDeadlineOnlyAfterACancellation()
    {
        var cancellation = new ScriptCancellation();
        CancellationToken first = cancellation.Token;
        cancellation.Reset();
        Assert.Equal(first, cancellation.Token);

        cancellation.Cancel();
        Assert.True(first.IsCancellationRequested);
        cancellation.Reset();
        Assert.False(cancellation.Token.IsCancellationRequested);
        Assert.True(first.IsCancellationRequested, "work started under the old deadline stays cancelled");
    }
}
