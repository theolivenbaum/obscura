using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// The CLI must exit 0 on a run that succeeded, every time.
/// </summary>
/// <remarks>
/// <para>
/// Roughly one run in ten used to exit 139 (SIGSEGV) after doing its work
/// correctly: the crash lands in native shutdown after <c>main</c> returns and
/// after stdout is flushed, so the output was complete and byte-identical to
/// the reference while the status said the run had died. Anything reading the
/// exit code - CI, a shell script, a wrapper library, or
/// <c>scripts/parity-sweep.sh</c> - saw a failure that had not happened.
/// <c>ProcessExit.Immediately</c> is what fixes it; see that type for the
/// measurements and why <see cref="System.Environment.Exit"/> is not enough.
/// </para>
/// <para>
/// This is a probabilistic test, which is unusual and deliberate: the fault is
/// a race and there is nothing deterministic to assert against. At the measured
/// pre-fix rate of about one in ten, 25 runs would have caught it better than
/// nine times in ten, and the cost is a few seconds. It only ever fails in the
/// direction that matters - a green run never means the race is impossible,
/// but a red one always means it is back.
/// </para>
/// </remarks>
public sealed class ProcessExitTests
{
    [Fact]
    public void AboutBlankFetchNeverExitsOnASignal()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);

        List<int> failures = [];
        for (var i = 0; i < 25; i++)
        {
            CliRun run = CliProcess.Run("fetch", "about:blank", "--quiet");
            // 128+n is a death by signal n, so 139 is the SIGSEGV this pins.
            if (run.ExitCode != 0)
            {
                failures.Add(run.ExitCode);
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} of 25 runs did not exit 0: [{string.Join(", ", failures)}]. "
                + "An exit code of 139 is the teardown segfault ProcessExit.Immediately exists to avoid.");
    }

    /// <summary>
    /// The work still reaches stdout. Skipping CLR shutdown means nothing
    /// downstream of the exit call can flush, so the flush has to happen inside
    /// it; this fails if that ordering is ever broken.
    /// </summary>
    [Fact]
    public void OutputSurvivesTheImmediateExit()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);

        CliRun run = CliProcess.Run(
            "fetch", "data:text/html,<p>flushed</p>", "--dump", "text", "--quiet");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("flushed", run.StdOut, StringComparison.Ordinal);
    }
}
