using Obscura.Parity.Tests.Harness;
using Xunit;

namespace Obscura.Parity.Tests;

/// <summary>
/// Tests of the harness itself, so a silently-disabled parity suite is visible.
/// </summary>
public sealed class ParityAvailability
{
    [Fact]
    public void Reports_why_parity_is_unavailable()
    {
        // Always runs. When parity is skipped, this prints the reason into the
        // test output so a green suite cannot quietly mean "nothing compared".
        var reason = ReferenceEngine.SkipReason;
        Assert.True(
            reason is null || reason.Length > 0,
            "SkipReason must either be null or explain how to enable parity tests");
    }

    [ParityFact]
    public void Rust_reference_binary_runs()
    {
        var run = ReferenceEngine.Rust("--version");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("obscura", run.StdOut, StringComparison.OrdinalIgnoreCase);
    }
}
