using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// Upstream 04418a5: `serve` and `mcp --http` refuse a non-loopback bind unless
/// their bearer token (`POCKETCALCULATOR_CDP_TOKEN`, `POCKETCALCULATOR_MCP_TOKEN`) is set, and
/// refuse a token shorter than 32 bytes. The refusal is an anyhow error out of
/// main: "Error: ..." on stderr and exit 1, before anything is bound.
/// </summary>
public sealed class ControlPlaneExposureTests
{
    private const string CdpRefusal =
        "Error: refusing to expose CDP without authentication; set POCKETCALCULATOR_CDP_TOKEN to at least 32 bytes";

    private static CliRun Run(string token, params string[] args)
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        return CliProcess.Run(
            new Dictionary<string, string> { ["POCKETCALCULATOR_CDP_TOKEN"] = token, ["POCKETCALCULATOR_MCP_TOKEN"] = token },
            args);
    }

    [Fact]
    public void ServeRefusesAPublicBindWithoutAToken()
    {
        var run = Run("", "serve", "--host", "0.0.0.0", "--port", "0");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(CdpRefusal, run.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiWorkerServeRefusesAPublicBindWithoutAToken()
    {
        var run = Run("", "serve", "--host", "0.0.0.0", "--port", "0", "--workers", "2");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(CdpRefusal, run.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ServeRefusesAShortToken()
    {
        var run = Run("short", "serve", "--port", "0");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Error: POCKETCALCULATOR_CDP_TOKEN must be at least 32 bytes", run.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void McpHttpRefusesAPublicBindWithoutAToken()
    {
        var run = Run("", "mcp", "--http", "--host", "0.0.0.0", "--port", "0");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            "Error: refusing to expose MCP without authentication; set POCKETCALCULATOR_MCP_TOKEN to at least 32 bytes",
            run.StdErr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void McpHttpRefusesAShortToken()
    {
        var run = Run("short", "mcp", "--http", "--port", "0");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("Error: POCKETCALCULATOR_MCP_TOKEN must be at least 32 bytes", run.StdErr, StringComparison.Ordinal);
    }
}
