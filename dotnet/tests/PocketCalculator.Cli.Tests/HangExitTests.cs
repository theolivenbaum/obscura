using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// L12: the serve/mcp hang backstop is opt-in. With <c>POCKETCALCULATOR_HANG_EXIT_MS</c>
/// unset nothing is armed, so an interrupted command never ends the process.
/// </summary>
public sealed class HangExitTests
{
    [Fact]
    public void HangExitIsOffUnlessConfigured()
    {
        if (Environment.GetEnvironmentVariable("POCKETCALCULATOR_HANG_EXIT_MS") is not null)
        {
            return;
        }

        Assert.False(HardDeadline.ArmHangExit());
        Assert.Null(PocketCalculator.Js.Runtime.HangEscalation.Handler);
    }
}
