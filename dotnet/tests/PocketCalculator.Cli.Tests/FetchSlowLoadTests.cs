using System.Text;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// <c>fetch</c> on a page that commits but does not finish loading inside <c>--timeout</c>.
/// </summary>
/// <remarks>
/// The navigation deadline used to fail the whole fetch, though the document had arrived and
/// its DOM was built. A deadline after the commit now leaves the page as it stood, so the
/// fetch reads it and warns that it is partial; one before the commit is still an error.
/// </remarks>
public sealed class FetchSlowLoadTests
{
    [Fact]
    public void Fetch_reads_a_committed_page_whose_load_outlives_the_timeout()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        using var server = new LocalHttpServer(target =>
        {
            if (target.StartsWith("/stall.js", StringComparison.Ordinal))
            {
                Thread.Sleep(8_000);
                return ("text/javascript", Encoding.UTF8.GetBytes("window.stalled = 1;"));
            }

            return ("text/html", Encoding.UTF8.GetBytes(
                "<title>Slow</title><p id=a>before</p><script src=/stall.js></script><p id=b>after</p>"));
        });

        var run = CliProcess.Run(
            "--allow-private-network", "fetch", "--quiet", "--timeout", "2", "--wait", "0",
            "--eval", "document.title + '|' + !!document.getElementById('a') + '|' + (window.stalled === undefined)",
            server.Base + "/slow");

        Assert.True(run.Success, $"stderr: {run.StdErr}");
        Assert.Equal("Slow|true|true", run.StdOut.Trim());
        Assert.Contains("did not finish loading within 2s", run.StdErr, StringComparison.Ordinal);
    }
}
