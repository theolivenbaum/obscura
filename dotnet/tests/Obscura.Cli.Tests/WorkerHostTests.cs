using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Obscura.Cli;
using Obscura.Cli.Commands;
using Obscura.Cli.Json;
using Xunit;

namespace Obscura.Cli.Tests;

/// <summary>
/// The <c>obscura-worker</c> binary and the newline-delimited JSON protocol
/// <c>scrape</c> drives it with, ported from
/// <c>crates/obscura-cli/src/worker.rs</c>.
/// </summary>
/// <remarks>
/// The Rust worker has no tests of its own; the protocol is only exercised
/// through <c>scrape</c>. It is a contract between two processes, so it is worth
/// asserting directly: a change to either side that keeps <c>scrape</c> working
/// by accident would still break an embedder driving the worker.
/// </remarks>
public sealed class WorkerHostTests
{
    public WorkerHostTests() =>
        Assert.True(CliProcess.SkipReason is null, CliProcess.SkipReason ?? string.Empty);

    /// <summary>The worker binary the build lays down beside the CLI.</summary>
    private static string WorkerBinary =>
        Path.Combine(Path.GetDirectoryName(CliProcess.Binary!)!, ScrapeCommand.WorkerName);

    /// <summary>Feed the worker a command script and collect its reply lines.</summary>
    private static List<string> Drive(params string[] commands)
    {
        var psi = new ProcessStartInfo(WorkerBinary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["OBSCURA_ALLOW_PRIVATE_NETWORK"] = "1";
        using var child = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {WorkerBinary}");
        _ = child.StandardError.ReadToEndAsync();

        foreach (var command in commands)
        {
            child.StandardInput.Write(command);
            child.StandardInput.Write('\n');
        }
        child.StandardInput.Flush();
        child.StandardInput.Close();

        var stdout = child.StandardOutput.ReadToEndAsync();
        if (!child.WaitForExit(120_000))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("obscura-worker did not exit within 120s");
        }
        return [.. stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
    }

    [Fact]
    public void The_build_lays_the_worker_down_beside_the_cli()
    {
        // `scrape` looks for the worker as a sibling of the running executable,
        // exactly as run_parallel_scrape does, so the two-binary layout is what
        // makes the fan-out work at all.
        Assert.True(
            File.Exists(WorkerBinary),
            $"scrape resolves its worker as a sibling of the CLI; {WorkerBinary} is missing");
        Assert.Equal(
            OperatingSystem.IsWindows() ? "obscura-worker.exe" : "obscura-worker",
            ScrapeCommand.WorkerName);
    }

    [Fact]
    public void An_unknown_command_reports_serdes_unknown_variant_message()
    {
        var replies = Drive("{\"cmd\":\"nope\"}");
        Assert.Equal(
            "{\"ok\":false,\"error\":\"Invalid command: unknown variant `nope`, "
            + "expected one of `navigate`, `evaluate`, `title`, `dump_html`, "
            + "`dump_text`, `shutdown`\"}",
            Assert.Single(replies));
    }

    [Fact]
    public void A_command_missing_its_field_is_rejected_without_ending_the_loop()
    {
        var replies = Drive("{\"cmd\":\"navigate\"}", "{\"cmd\":\"title\"}");
        Assert.Equal(2, replies.Count);
        Assert.Equal(
            "{\"ok\":false,\"error\":\"Invalid command: missing field `url`\"}", replies[0]);
        // The loop survives a bad command: `continue`, not `break`.
        Assert.Equal("{\"ok\":true,\"result\":\"\"}", replies[1]);
    }

    /// <summary>
    /// A blank line is skipped, and <c>shutdown</c> replies before returning.
    /// </summary>
    [Fact]
    public void Blank_lines_are_skipped_and_shutdown_replies_bye()
    {
        var replies = Drive("", "   ", "{\"cmd\":\"shutdown\"}", "{\"cmd\":\"title\"}");
        // Nothing after shutdown is read, so there is exactly one reply.
        Assert.Equal("{\"ok\":true,\"result\":\"bye\"}", Assert.Single(replies));
    }

    [Fact]
    public void Navigate_replies_with_the_title_and_url_then_evaluate_and_dump()
    {
        using var server = LocalHttpServer.Html(
            "<!doctype html><html><head><title>worker</title></head>"
            + "<body><p>hello</p></body></html>");

        var replies = Drive(
            SerdeJson.ToJson(new JsonObject
            {
                ["cmd"] = "navigate",
                ["url"] = server.Base,
            }),
            "{\"cmd\":\"title\"}",
            "{\"cmd\":\"evaluate\",\"expression\":\"1+1\"}",
            "{\"cmd\":\"evaluate\",\"expression\":\"null\"}",
            "{\"cmd\":\"dump_text\"}",
            "{\"cmd\":\"shutdown\"}");

        Assert.Equal(6, replies.Count);
        // Key order is the WorkerResponse field order: ok, then result.
        Assert.Equal(
            $"{{\"ok\":true,\"result\":{{\"title\":\"worker\",\"url\":\"{server.Base}/\"}}}}",
            replies[0]);
        Assert.Equal("{\"ok\":true,\"result\":\"worker\"}", replies[1]);
        // A bare JS number reaches the parent as an f64, which serde_json prints
        // with a decimal point.
        Assert.Equal("{\"ok\":true,\"result\":2.0}", replies[2]);
        // Rust's `info.value` is Some(Null) for a null result, so the reply is a
        // JSON null and not the string "null".
        Assert.Equal("{\"ok\":true,\"result\":null}", replies[3]);
        Assert.Contains("hello", replies[4], StringComparison.Ordinal);
        Assert.Equal("{\"ok\":true,\"result\":\"bye\"}", replies[5]);
    }

    [Fact]
    public void A_failed_navigation_replies_not_ok_with_the_page_error()
    {
        var replies = Drive(
            "{\"cmd\":\"navigate\",\"url\":\"file:///definitely/not/here.html\"}",
            "{\"cmd\":\"shutdown\"}");
        Assert.Equal(2, replies.Count);
        var response = Assert.IsType<JsonObject>(JsonNode.Parse(replies[0]));
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Contains("Network error", response["error"]!.GetValue<string>(), StringComparison.Ordinal);
        // No `result` key at all: Rust skips it when None.
        Assert.False(response.ContainsKey("result"));
    }

    [Fact]
    public void Env_flags_are_read_the_way_the_rust_worker_reads_them()
    {
        foreach (var raw in new[] { "1", "true", "yes", "on", " 1 ", "\tyes\t" })
        {
            Assert.True(WorkerHost.EnvFlag(raw), raw);
        }
        foreach (var raw in new[] { "0", "", "TRUE", "Yes", "off", "no" })
        {
            Assert.False(WorkerHost.EnvFlag(raw), raw);
        }
        Assert.False(WorkerHost.EnvFlag(null));
    }
}
