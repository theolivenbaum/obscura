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
        // 20,000 paragraphs, each wrapping around a float, laid out by one
        // getBoundingClientRect(): about 10 s of layout inside a single op. This used to be
        // four chains of 700 nested floats, which took about 25 s with floats built as flex
        // rows and about 1 s with real float layout, inside the 2 s timeout.
        var path = Path.Combine(_directory, "floats.html");
        File.WriteAllText(
            path,
            "<!doctype html><body><script>for(let i=0;i<20000;i++){const p=document.createElement('p');"
            + "p.innerHTML='text <span style=\"float:left;width:20px;height:30px\"></span>more words here';"
            + "document.body.appendChild(p);}document.body.getBoundingClientRect();"
            + "document.title='finished';</script>");

        var clock = Stopwatch.StartNew();
        var run = CliProcess.Run(
            "fetch", new Uri(path).AbsoluteUri, "--timeout", "2", "--quiet", "--eval", "document.title");

        Assert.NotEqual(124, run.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the fetch took {clock.Elapsed}");
        Assert.DoesNotContain("finished", run.StdOut, StringComparison.Ordinal);
    }
}
