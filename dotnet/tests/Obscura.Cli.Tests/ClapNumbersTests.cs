using Obscura.Cli.CommandLine;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// The numeric and enum option parsers, whose failure messages are clap's and
/// therefore part of the CLI's user-facing contract.
/// </summary>
/// <remarks>
/// Not in the Rust suite: clap owns this and needs no test. The port has to
/// reproduce it, and before these parsers existed a non-numeric value escaped
/// the parse-error check and killed the process with an unhandled
/// <c>InvalidOperationException</c>, where the reference prints a usage error
/// and exits 2.
/// </remarks>
public sealed class ClapNumbersTests
{
    private static IReadOnlyList<string> Errors(params string[] args)
    {
        _ = CliArgs.TryParseFrom(["obscura", .. args], out var errors);
        return errors;
    }

    private static string OneError(params string[] args) => Assert.Single(Errors(args));

    [Theory]
    // scrape and fetch share both options and both messages.
    [InlineData("scrape", "--timeout", "0",
        "invalid value '0' for '--timeout <TIMEOUT>': 0 is not in 1..18446744073709551615")]
    [InlineData("fetch", "--timeout", "0",
        "invalid value '0' for '--timeout <TIMEOUT>': 0 is not in 1..18446744073709551615")]
    [InlineData("scrape", "--timeout", "abc",
        "invalid value 'abc' for '--timeout <TIMEOUT>': invalid digit found in string")]
    [InlineData("scrape", "--concurrency", "0",
        "invalid value '0' for '--concurrency <CONCURRENCY>': number would be zero for non-zero type")]
    [InlineData("fetch", "--concurrency", "0",
        "invalid value '0' for '--concurrency <CONCURRENCY>': number would be zero for non-zero type")]
    [InlineData("scrape", "--concurrency", "abc",
        "invalid value 'abc' for '--concurrency <CONCURRENCY>': invalid digit found in string")]
    [InlineData("fetch", "--wait", "abc",
        "invalid value 'abc' for '--wait <WAIT>': invalid digit found in string")]
    [InlineData("fetch", "--wait", "99999999999999999999",
        "invalid value '99999999999999999999' for '--wait <WAIT>': number too large to fit in target type")]
    // u16 overflow is a range error in clap, spelled inclusively, not "too large".
    [InlineData("serve", "--port", "99999",
        "invalid value '99999' for '--port <PORT>': 99999 is not in 0..=65535")]
    [InlineData("serve", "--workers", "99999",
        "invalid value '99999' for '--workers <WORKERS>': 99999 is not in 0..=65535")]
    [InlineData("mcp", "--port", "99999",
        "invalid value '99999' for '--port <PORT>': 99999 is not in 0..=65535")]
    [InlineData("serve", "--port", "abc",
        "invalid value 'abc' for '--port <PORT>': invalid digit found in string")]
    [InlineData("serve", "--max-connections", "abc",
        "invalid value 'abc' for '--max-connections <MAX_CONNECTIONS>': invalid digit found in string")]
    [InlineData("serve", "--max-connections", "99999999999999999999",
        "invalid value '99999999999999999999' for '--max-connections <MAX_CONNECTIONS>': number too large to fit in target type")]
    public void Numeric_options_report_claps_message(
        string command, string flag, string value, string expected)
    {
        Assert.Contains(expected, Errors(command, "https://example.com", flag, value));
    }

    /// <summary>
    /// A dash-leading value never reaches clap's parser, because these options
    /// do not set <c>allow_hyphen_values</c>.
    /// </summary>
    [Theory]
    [InlineData("--timeout")]
    [InlineData("--concurrency")]
    public void A_negative_value_is_an_unexpected_argument(string flag)
    {
        Assert.Contains(
            "unexpected argument '-1' found",
            Errors("scrape", "https://example.com", flag, "-1"));
    }

    [Fact]
    public void Root_port_is_a_u16_too()
    {
        Assert.Contains(
            "invalid value '99999' for '--port <PORT>': 99999 is not in 0..=65535",
            Errors("--port", "99999"));
        Assert.Equal(65535, CliArgs.TryParseFrom(["obscura", "--port", "65535"], out _)?.Port);
    }

    [Fact]
    public void Dump_accepts_only_claps_lowercase_names()
    {
        // clap's ValueEnum is case sensitive; System.CommandLine's default was not.
        Assert.Equal(
            "invalid value 'HTML' for '--dump <DUMP>'\n"
            + "  [possible values: html, text, links, markdown, original, assets, cookies]",
            OneError("fetch", "https://example.com", "--dump", "HTML"));
        Assert.Equal(
            "invalid value 'nope' for '--dump <DUMP>'\n"
            + "  [possible values: html, text, links, markdown, original, assets, cookies]",
            OneError("fetch", "https://example.com", "--dump", "nope"));

        foreach (var (name, expected) in new (string, DumpFormat)[]
        {
            ("html", DumpFormat.Html),
            ("text", DumpFormat.Text),
            ("links", DumpFormat.Links),
            ("markdown", DumpFormat.Markdown),
            ("original", DumpFormat.Original),
            ("assets", DumpFormat.Assets),
            ("cookies", DumpFormat.Cookies),
        })
        {
            var args = CliArgs.TryParseFrom(
                ["obscura", "fetch", "https://example.com", "--dump", name], out var errors);
            Assert.Empty(errors);
            Assert.Equal(expected, Assert.IsType<CliCommand.Fetch>(args?.Command).Dump);
        }
    }

    /// <summary>
    /// The defaults are not range-checked, because clap validates only the token
    /// the user typed. A validator would see an absent option as 0.
    /// </summary>
    [Fact]
    public void Absent_numeric_options_keep_their_defaults()
    {
        var scrape = Assert.IsType<CliCommand.Scrape>(
            CliArgs.TryParseFrom(["obscura", "scrape", "https://example.com"], out var scrapeErrors)?.Command);
        Assert.Empty(scrapeErrors);
        Assert.Equal(60L, scrape.Timeout);
        Assert.Equal(10, scrape.Concurrency);

        var serve = Assert.IsType<CliCommand.Serve>(
            CliArgs.TryParseFrom(["obscura", "serve"], out var serveErrors)?.Command);
        Assert.Empty(serveErrors);
        Assert.Equal(9222, serve.Port);
        Assert.Equal(1, serve.Workers);
        Assert.Equal(CliDefinition.DefaultMaxConnections, serve.MaxConnections);
    }

    /// <summary>
    /// clap makes the subcommand optional (<c>Option&lt;Command&gt;</c>), and a
    /// bare <c>obscura</c> prints the banner and serves. System.CommandLine
    /// reports "Required command was not provided." unless the root has an
    /// action of its own, which made <c>obscura --port N</c> exit 2.
    /// </summary>
    [Fact]
    public void A_bare_invocation_parses_with_no_subcommand()
    {
        var args = CliArgs.TryParseFrom(["obscura", "--port", "9333"], out var errors);
        Assert.Empty(errors);
        Assert.NotNull(args);
        Assert.Null(args!.Command);
        Assert.Equal(9333, args.Port);
    }
}
