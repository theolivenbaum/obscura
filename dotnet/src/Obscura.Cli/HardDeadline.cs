using System.Globalization;

namespace Obscura.Cli;

/// <summary>
/// A process-level hard deadline: an absolute backstop so one page can never
/// wedge a worker.
/// </summary>
/// <remarks>
/// <para>
/// A synchronous hang inside a host op invoked from page JS cannot be cancelled
/// by the async machinery (there is no await to interrupt), nor by the V8
/// watchdog (terminating execution only unwinds JS bytecode, not the native
/// frame running beneath a V8 to host call). So a daemon thread force-exits the
/// process if the whole operation overruns navigation plus every configured
/// settle pass plus grace. A normal fetch returns first and the process exits
/// before this fires.
/// </para>
/// <para>
/// Exit code 124 and the message are both user visible; keep them.
/// </para>
/// </remarks>
public static class HardDeadline
{
    /// <summary>
    /// How many settle passes a fetch will run, which is what the deadline is
    /// sized against.
    /// </summary>
    public static ulong SettlePasses(
        bool evalAtCaptureBoundary,
        bool hasControlledScroll,
        bool hasEval,
        bool hasScreenshot,
        bool hasSelector,
        bool dumpSpecified)
    {
        if (evalAtCaptureBoundary)
        {
            return 1 + (hasControlledScroll ? 1ul : 0ul);
        }
        if (hasEval && (hasScreenshot || hasSelector || dumpSpecified))
        {
            return 2;
        }
        return 1;
    }

    /// <summary>
    /// The deadline itself: the request timeout, plus one wait budget per settle
    /// pass, plus ten seconds of grace. Saturating, as the Rust arithmetic is.
    /// </summary>
    public static TimeSpan Budget(ulong timeoutSecs, ulong waitSecs, ulong settlePasses)
    {
        var seconds = SaturatingAdd(SaturatingAdd(timeoutSecs, SaturatingMul(waitSecs, settlePasses)), 10);
        // TimeSpan tops out well below ulong.MaxValue seconds; a saturated
        // budget is "never fires", which is the same observable behavior.
        return seconds > (ulong)TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(seconds);
    }

    private static ulong SaturatingAdd(ulong a, ulong b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b;

    private static ulong SaturatingMul(ulong a, ulong b) =>
        a == 0 || b == 0 ? 0 : a > ulong.MaxValue / b ? ulong.MaxValue : a * b;

    /// <summary>Arm the backstop. The thread is a daemon, so it never keeps the process alive.</summary>
    public static void Arm(TimeSpan budget)
    {
        if (budget == TimeSpan.MaxValue)
        {
            return;
        }
        var thread = new Thread(() =>
        {
            Thread.Sleep(budget);
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"obscura: hard timeout exceeded ({(long)budget.TotalSeconds}s); forcing exit"));
            Console.Error.Flush();
            Console.Out.Flush();
            Environment.Exit(124);
        })
        {
            IsBackground = true,
            Name = "obscura-hard-deadline",
        };
        thread.Start();
    }
}
