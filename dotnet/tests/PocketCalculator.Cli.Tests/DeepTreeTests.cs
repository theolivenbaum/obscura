using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cli.Tests;

/// <summary>
/// SECURITY.md C5: a deep DOM must not overflow the stack in layout or paint. A stack
/// overflow cannot be caught in .NET, so before the fix these runs aborted the process with
/// exit 134; they run in a child process for exactly that reason.
/// </summary>
public sealed class DeepTreeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"obscura-deep-tree-{Guid.NewGuid():N}");

    public DeepTreeTests()
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

    private string Write(string name, string html)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, html);
        return new Uri(path).AbsoluteUri;
    }

    /// <summary>
    /// Build a chain <paramref name="depth"/> elements deep off-document (so the build itself is
    /// linear), attach it, read the deepest element's rect and take a screenshot.
    /// </summary>
    private void AssertDeepChainRenders(int depth, string css)
    {
        var url = Write(
            "page.html",
            $"<!doctype html><html><head><style>div{{{css}}}</style></head><body></body></html>");
        var shot = Path.Combine(_directory, "shot.png");
        var script =
            "(()=>{const top=document.createElement('div');let p=top;" +
            $"for(let i=1;i<{depth};i++){{const d=document.createElement('div');p.appendChild(d);" +
            "d.appendChild(document.createTextNode('x'));p=d;}" +
            "document.body.appendChild(top);" +
            "const r=p.getBoundingClientRect();const t=top.getBoundingClientRect();" +
            "return 'ok '+r.height+' '+t.width})()";

        var run = CliProcess.Run(
            "fetch", url, "--screenshot", shot, "--wait", "0", "--timeout", "100", "--quiet",
            "--eval", script);

        Assert.True(run.Success, $"exit {run.ExitCode}: {run.StdErr[..Math.Min(run.StdErr.Length, 2000)]}");
        var state = JsonNode.Parse(run.StdOut) as JsonObject;
        Assert.StartsWith("ok ", state?["evaluation"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
        Assert.True(new FileInfo(shot).Length > 0);
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(5000)]
    [InlineData(100_000)]
    public void ScriptBuiltBlockChainRendersWithoutOverflowingTheStack(int depth) =>
        AssertDeepChainRenders(depth, string.Empty);

    [Fact]
    public void DeepTableChainRendersWithoutOverflowingTheStack() =>
        AssertDeepChainRenders(5000, "display:table");

    [Fact]
    public void DeepNestedScrollersPaintWithoutOverflowingTheStack() =>
        AssertDeepChainRenders(5000, "overflow:auto;position:sticky;top:0");

    [Fact]
    public void DeepParsedMarkupRendersWithoutOverflowingTheStack()
    {
        var url = Write("parsed.html", string.Concat(Enumerable.Repeat("<div>x", 3000)));
        var shot = Path.Combine(_directory, "parsed.png");

        var run = CliProcess.Run(
            "fetch", url, "--screenshot", shot, "--wait", "0", "--timeout", "100", "--quiet",
            "--eval", "document.querySelectorAll('div').length");

        Assert.True(run.Success, $"exit {run.ExitCode}: {run.StdErr[..Math.Min(run.StdErr.Length, 2000)]}");
        var state = JsonNode.Parse(run.StdOut) as JsonObject;
        Assert.Equal(3000.0, state?["evaluation"]?.GetValue<double>());
    }
}
