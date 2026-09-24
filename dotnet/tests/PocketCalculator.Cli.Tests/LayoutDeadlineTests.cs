using System.Diagnostics;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// SECURITY.md H8: a layout that runs inside one op stops at the fetch deadline. Before,
/// the op held the thread in C# where the watchdog's interrupt cannot reach, and the run
/// ended only at the process-level hard deadline (exit 124).
/// </summary>
public sealed class LayoutDeadlineTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"obscura-layout-deadline-{Guid.NewGuid():N}");

    public LayoutDeadlineTests()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void APathologicalLayoutEndsAtTheFetchTimeout()
    {
        // Four chains of 700 nested floats laid out by one getBoundingClientRect(): about
        // 25 s of layout inside a single op, even after nested floats were made quadratic.
        var path = Path.Combine(_directory, "floats.html");
        File.WriteAllText(
            path,
            "<!doctype html><body><script>for(let c=0;c<4;c++){let p=document.createElement('section');"
            + "document.body.appendChild(p);for(let i=0;i<700;i++){"
            + "const d=document.createElement('div');d.style.cssText='float:left;padding:1px';"
            + "d.textContent='x';p.appendChild(d);p=d;}}document.body.getBoundingClientRect();"
            + "document.title='finished';</script>");

        var clock = Stopwatch.StartNew();
        var run = CliProcess.Run(
            "fetch", new Uri(path).AbsoluteUri, "--timeout", "2", "--quiet", "--eval", "document.title");

        Assert.NotEqual(124, run.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the fetch took {clock.Elapsed}");
        Assert.DoesNotContain("finished", run.StdOut, StringComparison.Ordinal);
    }
}
