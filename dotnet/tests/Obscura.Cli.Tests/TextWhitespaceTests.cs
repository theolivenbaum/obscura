using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>Port of <c>crates/obscura-cli/tests/text_whitespace.rs</c>.</summary>
public sealed class TextWhitespaceTests
{
    private const string InlineTextUrl =
        "data:text/html," +
        "<html><body>" +
        "<h1><span>H</span><span>e</span><span>l</span><span>l</span><span>o</span>%20" +
        "<span>w</span><span>o</span><span>r</span><span>l</span><span>d</span><span>.</span></h1>" +
        "<p><span>Hello</span><span>,</span>%20<span>world</span><span>!</span></p>" +
        "</body></html>";

    [Fact]
    public void Dump_text_preserves_whitespace_between_inline_spans()
    {
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);
        var run = CliProcess.Run("fetch", InlineTextUrl, "--dump", "text", "--quiet");
        Assert.True(run.Success, $"obscura fetch failed: {run.StdErr}");
        Assert.Equal(
            "Hello world.\n\nHello, world!",
            run.StdOut.ReplaceLineEndings("\n").Trim());
    }
}
