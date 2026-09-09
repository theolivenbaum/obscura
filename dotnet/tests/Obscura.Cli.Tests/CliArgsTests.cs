using Obscura.Cli;
using Obscura.Cli.CommandLine;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// The clap-parsing tests from <c>main.rs</c>'s <c>mod tests</c>, in the same
/// order and under the same names.
/// </summary>
public sealed class CliArgsTests
{
    private static CliArgs Parse(params string[] argv)
    {
        var args = CliArgs.TryParseFrom(argv, out var errors);
        Assert.True(args is not null, $"expected a successful parse, got: {string.Join("; ", errors)}");
        return args!;
    }

    private static bool ParseFails(params string[] argv) =>
        CliArgs.TryParseFrom(argv, out _) is null;

    // Issue #117: `--dump original` short-circuits the browser stack and streams
    // the raw response body verbatim, including for binary payloads.
    [Fact]
    public void Parsed_fetch_dump_original_is_accepted_by_clap()
    {
        var args = Parse("obscura", "fetch", "--dump", "original", "https://example.com/image.jpg");
        var fetch = Assert.IsType<CliCommand.Fetch>(args.Command);
        Assert.Equal(DumpFormat.Original, fetch.Dump);
    }

    // Issue #349: batch mode, with no positional URL.
    [Fact]
    public void Parsed_fetch_file_and_concurrency()
    {
        var args = Parse("obscura", "fetch", "--file", "urls.txt", "--dump", "original", "--concurrency", "25");
        var fetch = Assert.IsType<CliCommand.Fetch>(args.Command);
        Assert.Null(fetch.Url);
        Assert.Equal("urls.txt", fetch.File);
        Assert.Equal(25, fetch.Concurrency);
        Assert.Equal(DumpFormat.Original, fetch.Dump);
    }

    [Fact]
    public void Concurrency_rejects_zero()
    {
        // NonZeroUsize means --concurrency 0 is a parse error, not a silent hang
        // on a zero-permit semaphore.
        Assert.True(ParseFails("obscura", "fetch", "--file", "u.txt", "--concurrency", "0"));
    }

    [Fact]
    public void Parsed_fetch_with_quiet_flag_is_detected()
    {
        var args = Parse("obscura", "fetch", "--quiet", "https://example.com");
        Assert.True(CliOptions.IsQuietCommand(args.Command));
    }

    [Fact]
    public void Parsed_fetch_without_quiet_is_not_detected()
    {
        var args = Parse("obscura", "fetch", "https://example.com");
        Assert.False(CliOptions.IsQuietCommand(args.Command));
    }

    [Fact]
    public void Parsed_serve_command_is_not_quiet()
    {
        var args = Parse("obscura", "serve");
        Assert.False(CliOptions.IsQuietCommand(args.Command));
    }

    [Fact]
    public void No_subcommand_is_not_quiet() => Assert.False(CliOptions.IsQuietCommand(null));

    [Fact]
    public void Parsed_v8_flags_global_arg()
    {
        var args = Parse(
            "obscura", "--v8-flags", "--max-old-space-size=4096 --max-semi-space-size=64",
            "fetch", "https://example.com");
        Assert.Equal("--max-old-space-size=4096 --max-semi-space-size=64", args.V8Flags);
    }

    [Fact]
    public void V8_flags_default_is_none()
    {
        var args = Parse("obscura", "fetch", "https://example.com");
        Assert.Null(args.V8Flags);
    }

    [Fact]
    public void Parsed_v8_flags_with_serve_subcommand()
    {
        var args = Parse("obscura", "--v8-flags", "--max-old-space-size=2048", "serve", "--port", "9333");
        Assert.Equal("--max-old-space-size=2048", args.V8Flags);
    }

    [Fact]
    public void Parsed_v8_flags_with_scrape_subcommand()
    {
        var args = Parse("obscura", "--v8-flags", "--expose-gc", "scrape", "https://a.com", "https://b.com");
        Assert.Equal("--expose-gc", args.V8Flags);
    }

    [Fact]
    public void Parsed_v8_flags_empty_string_is_accepted()
    {
        var args = Parse("obscura", "--v8-flags", "", "fetch", "https://example.com");
        Assert.Equal(string.Empty, args.V8Flags);
    }

    [Fact]
    public void Parsed_fetch_quiet_resolves_to_off_filter()
    {
        var args = Parse("obscura", "fetch", "--quiet", "https://example.com");
        var filter = CliOptions.SelectLogFilter(args.Verbose, CliOptions.IsQuietCommand(args.Command));
        Assert.Equal("off", filter);
    }

    [Fact]
    public void Fetch_wait_distinguishes_adaptive_default_from_fixed_delay()
    {
        var byDefault = Parse("obscura", "fetch", "https://example.com");
        Assert.Null(Assert.IsType<CliCommand.Fetch>(byDefault.Command).Wait);

        var fixedDelay = Parse("obscura", "fetch", "https://example.com", "--wait", "0");
        Assert.Equal(0L, Assert.IsType<CliCommand.Fetch>(fixedDelay.Command).Wait);
    }

    [Fact]
    public void Fetch_screenshot_has_a_short_alias_and_rejects_batch_mode()
    {
        var args = Parse("obscura", "fetch", "https://example.com", "-s", "page.png");
        Assert.Equal("page.png", Assert.IsType<CliCommand.Fetch>(args.Command).Screenshot);

        Assert.True(ParseFails("obscura", "fetch", "--file", "urls.txt", "--screenshot", "page.png"));
    }

    [Fact]
    public void Matcher_still_uses_fetch_variant()
    {
        var command = new CliCommand.Fetch
        {
            Url = "https://x",
            Dump = DumpFormat.Html,
            Selector = null,
            File = null,
            Concurrency = 1,
            Wait = 5,
            Timeout = 30,
            WaitUntil = "load",
            UserAgent = null,
            Eval = null,
            Quiet = true,
            Output = null,
            StorageDir = null,
            Screenshot = null,
        };
        Assert.True(CliOptions.IsQuietCommand(command));
    }

    [Fact]
    public void Parsed_fetch_dump_assets_is_accepted_by_clap()
    {
        var args = Parse("obscura", "fetch", "--dump", "assets", "https://example.com");
        Assert.Equal(DumpFormat.Assets, Assert.IsType<CliCommand.Fetch>(args.Command).Dump);
    }

    // Not in the Rust suite: the whole point of the "did the user type it"
    // distinction is that an absent --timeout/--concurrency must keep its
    // default rather than be range-checked as 0.
    [Fact]
    public void Fetch_without_explicit_timeout_or_concurrency_parses_with_its_defaults()
    {
        var fetch = Assert.IsType<CliCommand.Fetch>(Parse("obscura", "fetch", "https://x").Command);
        Assert.Equal(30L, fetch.Timeout);
        Assert.Equal(1, fetch.Concurrency);

        var scrape = Assert.IsType<CliCommand.Scrape>(Parse("obscura", "scrape", "https://x").Command);
        Assert.Equal(60L, scrape.Timeout);
        Assert.Equal(10, scrape.Concurrency);
    }

    [Fact]
    public void Timeout_below_one_is_rejected()
    {
        Assert.True(ParseFails("obscura", "fetch", "https://x", "--timeout", "0"));
        Assert.True(ParseFails("obscura", "scrape", "https://x", "--timeout", "0"));
    }

    // Not in the Rust suite, because clap rejects an unknown flag for free.
    // System.CommandLine binds it to the next positional instead, so
    // `obscura fetch --nope` would navigate to "--nope" and exit 1 where clap
    // exits 2. No URL starts with a dash.
    [Fact]
    public void An_option_like_positional_is_a_usage_error()
    {
        Assert.Null(CliArgs.TryParseFrom(["obscura", "fetch", "--nope"], out var fetchErrors));
        Assert.Contains("unexpected argument '--nope' found", fetchErrors);

        Assert.Null(CliArgs.TryParseFrom(["obscura", "scrape", "--nope"], out var scrapeErrors));
        Assert.Contains("unexpected argument '--nope' found", scrapeErrors);

        // A real URL, and the stdin sentinel on --file, still parse.
        Assert.NotNull(CliArgs.TryParseFrom(["obscura", "fetch", "https://example.com"], out _));
        var batch = CliArgs.TryParseFrom(["obscura", "fetch", "--file", "-"], out _);
        Assert.Equal("-", Assert.IsType<CliCommand.Fetch>(batch!.Command).File);
    }

    // The global flags must be accepted both before and after the subcommand,
    // which is what clap's `global = true` means.
    [Fact]
    public void Global_flags_are_accepted_on_either_side_of_the_subcommand()
    {
        Assert.True(Parse("obscura", "--stealth", "fetch", "https://x").Stealth);
        Assert.True(Parse("obscura", "fetch", "https://x", "--stealth").Stealth);
        Assert.True(Parse("obscura", "fetch", "https://x", "--allow-private-network").AllowPrivateNetwork);
        Assert.True(Parse("obscura", "fetch", "https://x", "--obey-robots").ObeyRobots);
        Assert.Equal("http://p:1", Parse("obscura", "fetch", "https://x", "--proxy", "http://p:1").Proxy);
    }
}
