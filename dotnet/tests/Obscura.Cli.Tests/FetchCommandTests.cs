using Obscura.Browser;
using Obscura.Cli;
using Obscura.Cli.CommandLine;
using Obscura.Cli.Commands;
using Xunit;
using Page = Obscura.Browser.Page;

namespace Obscura.Cli.Tests;

/// <summary>
/// The <c>--dump original</c>, URL-list and output-writing tests from
/// <c>main.rs</c>'s <c>mod tests</c>.
/// </summary>
public sealed class FetchCommandTests
{
    // A real binary payload: a 1x1 transparent PNG, exactly the kind of resource
    // `--dump original` exists to stream without HTML/JS rendering.
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48,
        0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00,
        0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78,
        0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    private static string TempPath(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"obscura-cli-test-{Guid.NewGuid():N}{suffix}");

    [Fact]
    public void Read_urls_skips_blanks_and_comments()
    {
        var path = TempPath(".txt");
        File.WriteAllText(
            path,
            "https://a.example/one.js\n\n  # a comment\n   https://b.example/two.css  \nhttps://c.example/three.json\n");
        try
        {
            Assert.Equal(
                new List<string>
                {
                    "https://a.example/one.js",
                    "https://b.example/two.css",
                    "https://c.example/three.json",
                },
                FetchCommand.ReadUrlsFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Fetch_original_bytes_returns_file_contents_verbatim()
    {
        var path = TempPath(".png");
        await File.WriteAllBytesAsync(path, PngBytes, TestContext.Current.CancellationToken);
        try
        {
            var bytes = await FetchCommand.FetchOriginalBytesAsync(
                $"file://{path}", null, null, 5, stealth: false);
            Assert.Equal(PngBytes, bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A stealth-enabled build routes `--dump original` through the stealth
    // client, which only speaks http(s). file:// must keep working the same as
    // without --stealth instead of being handed to it (issue #482).
    [Fact]
    public async Task Fetch_original_bytes_file_url_ignores_stealth_flag()
    {
        var path = TempPath(".png");
        await File.WriteAllBytesAsync(path, PngBytes, TestContext.Current.CancellationToken);
        try
        {
            var bytes = await FetchCommand.FetchOriginalBytesAsync(
                $"file://{path}", null, null, 5, stealth: true);
            Assert.Equal(PngBytes, bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Write_or_print_bytes_writes_without_trailing_newline()
    {
        // Regression guard for #117: a println-style write would append a 0x0A
        // byte and corrupt binary payloads.
        byte[] payload = [0x00, 0xFF, (byte)'h', (byte)'i', 0x00];
        var path = TempPath(".bin");
        try
        {
            await FetchCommand.WriteOrPrintBytesAsync(payload, path);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Write_or_print_writes_output_file_without_a_trailing_newline()
    {
        var path = TempPath(".txt");
        try
        {
            await FetchCommand.WriteOrPrintAsync("rendered output", path);
            Assert.Equal("rendered output", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static TimeSpan ConfiguredFetchTimeout(CliArgs args)
    {
        var timeout = Assert.IsType<CliCommand.Fetch>(args.Command).Timeout;
        var context = BrowserContext.WithStorageAndNetwork(
            "cli-timeout-test", null, false, null, null, true);
        using var page = new Page("cli-timeout-test", context);
        FetchCommand.ConfigureFetchNavigationTimeout(page, (ulong)timeout);
        return page.NavigationTimeout;
    }

    [Fact]
    public void Fetch_timeout_sets_the_page_navigation_budget()
    {
        var args = CliArgs.TryParseFrom(
            ["obscura", "fetch", "https://example.com", "--timeout", "50"], out _);
        Assert.NotNull(args);
        Assert.Equal(TimeSpan.FromSeconds(50), ConfiguredFetchTimeout(args!));
    }

    [Fact]
    public void Fetch_default_navigation_budget_remains_thirty_seconds()
    {
        var args = CliArgs.TryParseFrom(["obscura", "fetch", "https://example.com"], out _);
        Assert.NotNull(args);
        Assert.Equal(TimeSpan.FromSeconds(30), ConfiguredFetchTimeout(args!));
    }

    // Not in the Rust suite: the process-level hard deadline is an explicit
    // robustness invariant, and its size is what makes it a backstop rather than
    // a second timeout that fires first.
    [Fact]
    public void Hard_deadline_is_the_timeout_plus_every_settle_pass_plus_grace()
    {
        // A plain fetch runs one settle pass.
        Assert.Equal(
            1ul,
            HardDeadline.SettlePasses(false, false, hasEval: false, false, false, false));
        Assert.Equal(TimeSpan.FromSeconds(30 + 5 + 10), HardDeadline.Budget(30, 5, 1));

        // --eval combined with --dump settles twice.
        Assert.Equal(
            2ul,
            HardDeadline.SettlePasses(false, false, hasEval: true, false, false, dumpSpecified: true));
        Assert.Equal(TimeSpan.FromSeconds(30 + (5 * 2) + 10), HardDeadline.Budget(30, 5, 2));

        // The capture-boundary opt-in adds a pass only when a controlled scroll
        // was requested.
        Assert.Equal(
            1ul,
            HardDeadline.SettlePasses(true, false, hasEval: true, true, false, false));
        Assert.Equal(
            2ul,
            HardDeadline.SettlePasses(true, true, hasEval: true, true, false, false));
    }

    [Fact]
    public void Hard_deadline_arithmetic_saturates_instead_of_overflowing() =>
        Assert.Equal(TimeSpan.MaxValue, HardDeadline.Budget(ulong.MaxValue, ulong.MaxValue, 2));
}
