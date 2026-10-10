using PocketCalculator.Js.Runtime;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Deterministic waits on the engine's own signals, for tests whose assertion is about
/// what happens and in what order, not about how fast a loaded host gets there.
/// </summary>
/// <remarks>
/// A fixed pump (<c>RunEventLoopBoundedAsync(40)</c>) asserts that the work fits in 40 ms
/// of wall clock, which a cold JIT or a loaded machine does not promise: the first layout
/// in a fresh process alone takes a few hundred milliseconds. <see cref="UntilIdleAsync"/>
/// drives the loop until it has no pending task or timer left, and returns as soon as it
/// does, so a passing run is as fast as before. <see cref="UntilAsync"/> drives it until a
/// page condition holds. Both stop at a generous deadline so a regression still fails
/// rather than hangs.
/// </remarks>
internal static class EventLoopWait
{
    /// <summary>The deadline for a wait that should take milliseconds.</summary>
    public const int GenerousMs = 20_000;

    /// <summary>Drives the loop until it is idle, or <see cref="GenerousMs"/> pass.</summary>
    public static Task UntilIdleAsync(PocketCalculatorJsRuntime rt) =>
        rt.RunEventLoopBoundedAsync(GenerousMs);

    /// <summary>
    /// Drives the loop in short slices until <paramref name="condition"/> is truthy in the
    /// page, or <paramref name="timeoutMs"/> pass. Returns whether the condition held.
    /// </summary>
    public static async Task<bool> UntilAsync(
        PocketCalculatorJsRuntime rt, string condition, int timeoutMs = GenerousMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (rt.Evaluate($"Boolean({condition})")?.GetValue<bool>() == true)
            {
                return true;
            }

            if (clock.ElapsedMilliseconds >= timeoutMs)
            {
                return false;
            }

            await rt.RunEventLoopBoundedAsync(10);
            await Task.Delay(1);
        }
    }
}
