using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// The <c>scrape</c> subcommand end to end, ported from
/// <c>run_parallel_scrape</c> in <c>crates/obscura-cli/src/main.rs</c>.
/// </summary>
/// <remarks>
/// The Rust suite has no test for it; <c>scripts/parity-sweep-scrape.sh</c> is
/// the differential check. These assert the parts of the contract that hold
/// without a reference binary: the JSON shape and key order, the stdout/stderr
/// split, argv result order, and that a per-URL failure is in-band rather than a
/// nonzero exit.
/// </remarks>
public sealed class ScrapeCommandTests
{
    public ScrapeCommandTests() =>
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);

    private static readonly Dictionary<string, string> AllowLoopback =
        new() { ["OBSCURA_ALLOW_PRIVATE_NETWORK"] = "1" };

    private static LocalHttpServer TitledServer() => new(target => (
        "text/html",
        Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><title>t{target.Trim('/')}</title></head>"
            + "<body><p>body</p></body></html>")));

    [Fact]
    public void Scrape_reports_results_in_argv_order_with_the_rust_key_order()
    {
        using var server = TitledServer();
        var run = CliProcess.Run(
            AllowLoopback, "scrape", $"{server.Base}/a", $"{server.Base}/b", $"{server.Base}/c",
            "--concurrency", "3", "--quiet");
        Assert.True(run.Success, $"scrape failed: {run.StdErr}");
        // --quiet suppresses the progress line; the JSON is the whole of stdout.
        Assert.Equal(string.Empty, run.StdErr);

        var output = Assert.IsType<JsonObject>(JsonNode.Parse(run.StdOut));
        Assert.Equal(
            new[] { "total_urls", "concurrency", "total_time_ms", "avg_time_ms", "results" },
            output.Select(pair => pair.Key));
        Assert.Equal(3.0, output["total_urls"]!.GetValue<double>());
        Assert.Equal(3.0, output["concurrency"]!.GetValue<double>());

        var results = Assert.IsType<JsonArray>(output["results"]);
        Assert.Equal(3, results.Count);
        // Results follow argv order, not completion order.
        foreach (var (index, suffix) in new[] { (0, "a"), (1, "b"), (2, "c") })
        {
            var entry = Assert.IsType<JsonObject>(results[index]);
            Assert.Equal(
                new[] { "url", "title", "eval", "time_ms", "worker" },
                entry.Select(pair => pair.Key));
            Assert.Equal($"{server.Base}/{suffix}", entry["url"]!.GetValue<string>());
            Assert.Equal($"t{suffix}", entry["title"]!.GetValue<string>());
            // Without --eval the slot is present and JSON null, which a JsonNode
            // models as a null reference.
            Assert.True(entry.ContainsKey("eval"));
            Assert.Null(entry["eval"]);
            Assert.Equal((double)index, entry["worker"]!.GetValue<double>());
        }
    }

    /// <summary>
    /// <c>avg_time_ms</c> is <c>total as f64 / count as f64</c> in Rust, so it is
    /// always a float and serde_json always prints a decimal point.
    /// </summary>
    [Fact]
    public void Avg_time_is_written_as_a_float()
    {
        using var server = TitledServer();
        var run = CliProcess.Run(AllowLoopback, "scrape", $"{server.Base}/a", "--quiet");
        Assert.True(run.Success, run.StdErr);
        Assert.Matches("\"avg_time_ms\": [0-9]+\\.[0-9]+", run.StdOut);
    }

    [Fact]
    public void Scrape_carries_the_eval_value_across_the_worker_protocol()
    {
        using var server = TitledServer();
        var run = CliProcess.Run(
            AllowLoopback, "scrape", $"{server.Base}/a", "--quiet",
            "--eval", "({title: document.title, two: 1 + 1})");
        Assert.True(run.Success, run.StdErr);

        var entry = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(
                Assert.IsType<JsonObject>(JsonNode.Parse(run.StdOut))["results"])[0]);
        var eval = Assert.IsType<JsonObject>(entry["eval"]);
        Assert.Equal("ta", eval["title"]!.GetValue<string>());
        Assert.Equal(2.0, eval["two"]!.GetValue<double>());
        // A number nested in an object arrives through JSON.stringify and is
        // re-parsed, so serde_json keeps the integer spelling: 2, not 2.0. A
        // bare `--eval 1+1` is an f64 and does print 2.0; the two are different
        // paths and SerdeJson reproduces both.
        Assert.Contains("\"two\": 2", run.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("\"two\": 2.0", run.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page that fails to load is reported in-band, and the run still exits 0.
    /// The failure object has no <c>title</c>, <c>eval</c> or <c>worker</c> key.
    /// </summary>
    [Fact]
    public void A_failing_url_is_reported_in_band_and_the_run_still_succeeds()
    {
        using var server = TitledServer();
        var run = CliProcess.Run(
            AllowLoopback, "scrape", $"{server.Base}/a", "file:///definitely/not/here.html",
            "--quiet");
        Assert.True(run.Success, $"a per-URL failure must not fail the run: {run.StdErr}");

        var results = Assert.IsType<JsonArray>(
            Assert.IsType<JsonObject>(JsonNode.Parse(run.StdOut))["results"]);
        Assert.Equal(2, results.Count);
        Assert.Equal("ta", Assert.IsType<JsonObject>(results[0])["title"]!.GetValue<string>());

        var failure = Assert.IsType<JsonObject>(results[1]);
        Assert.Equal(new[] { "url", "error", "time_ms" }, failure.Select(pair => pair.Key));
        Assert.Contains("Network error", failure["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The text format is one tab-separated line per URL on stdout, with the
    /// summary on stderr unless <c>--quiet</c>.
    /// </summary>
    [Fact]
    public void Text_format_writes_rows_to_stdout_and_the_summary_to_stderr()
    {
        using var server = TitledServer();
        var run = CliProcess.Run(
            AllowLoopback, "scrape", $"{server.Base}/a", $"{server.Base}/b", "--format", "text");
        Assert.True(run.Success, run.StdErr);

        var rows = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length);
        Assert.Matches($@"^\d+ms\t{System.Text.RegularExpressions.Regex.Escape(server.Base)}/a\tta$", rows[0]);
        Assert.Matches($@"^\d+ms\t{System.Text.RegularExpressions.Regex.Escape(server.Base)}/b\ttb$", rows[1]);

        Assert.StartsWith("Scraping 2 URLs with 10 concurrent workers (per-worker timeout: 60s)...",
            run.StdErr, StringComparison.Ordinal);
        Assert.Contains("URLs (10 concurrent)", run.StdErr, StringComparison.Ordinal);
    }

    /// <summary>With <c>--eval</c> the text row carries the value, not the title.</summary>
    [Fact]
    public void Text_format_prints_the_eval_value_in_place_of_the_title()
    {
        using var server = TitledServer();
        var run = CliProcess.Run(
            AllowLoopback, "scrape", $"{server.Base}/a", "--format", "text", "--quiet",
            "--eval", "\"marker\"");
        Assert.True(run.Success, run.StdErr);
        Assert.EndsWith("\t\"marker\"", run.StdOut.TrimEnd('\n'), StringComparison.Ordinal);
    }

    [Fact]
    public void No_urls_is_an_anyhow_error_on_stderr_with_exit_1()
    {
        var run = CliProcess.Run("scrape");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(string.Empty, run.StdOut);
        Assert.Equal(
            "Error: No URLs provided. Pass at least one URL to scrape." + Environment.NewLine,
            run.StdErr);
    }

    /// <summary>
    /// The progress line names the count, the concurrency and the timeout, and
    /// goes to stderr so stdout stays machine readable.
    /// </summary>
    [Fact]
    public void The_progress_line_goes_to_stderr()
    {
        using var server = TitledServer();
        var run = CliProcess.Run(
            AllowLoopback, "scrape", $"{server.Base}/a", "--concurrency", "4", "--timeout", "9");
        Assert.True(run.Success, run.StdErr);
        Assert.StartsWith(
            "Scraping 1 URLs with 4 concurrent workers (per-worker timeout: 9s)...",
            run.StdErr,
            StringComparison.Ordinal);
        Assert.StartsWith("{", run.StdOut, StringComparison.Ordinal);
    }
}
