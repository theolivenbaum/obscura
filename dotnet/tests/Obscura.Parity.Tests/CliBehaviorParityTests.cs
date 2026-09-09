using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Parity.Tests.Harness;
using Xunit;

namespace Obscura.Parity.Tests;

/// <summary>
/// Differential coverage for the CLI surfaces that are not a <c>--dump</c> of a
/// fixture: <c>--eval</c>, the stdout/stderr split, exit codes, <c>--version</c>,
/// and that <c>serve</c> and <c>mcp</c> really start their engines.
/// </summary>
public sealed class CliBehaviorParityTests
{
    private static string Fixture =>
        Fixtures.Named().FirstOrDefault().Url
        ?? throw new InvalidOperationException("render-repros corpus not found");

    private const string EvalPage =
        "data:text/html,<html><head><title>PT</title></head><body>" +
        "<p>Hello world.</p><a href=\"/x\">Link X</a><a href=\"https://e.test/y\">Y</a>" +
        "</body></html>";

    public static TheoryData<string> EvalExpressions() =>
    [
        // A bare number is converted straight to f64, so serde_json prints it
        // with a decimal point: 1+1 is "2.0", not "2".
        "1+1",
        "1/3",
        "0.1+0.2",
        "2**53",
        // ryu's exponential form, which carries an explicit '+' on a positive
        // exponent. Getting this wrong is silent: the value still parses.
        "1e30",
        "1.234e33",
        "1e21",
        "1e16",
        "1e17",
        "1e-30",
        "1e-7",
        "1e-6",
        "0.001234",
        "5e-324",
        "1.7976931348623157e308",
        "(0-1)*1e30",
        "123456789012345678901234567890",
        "document.title",
        // Multi-statement --eval starting with `const` returns null, because V8
        // gives `const` an empty completion value. Intended behavior.
        "const a = 1; a",
        "let b = 2; b",
        // A nested number arrives through JSON.stringify and stays an integer.
        "({a:1,b:[1,2],c:'x'})",
        "[1,2,3]",
        "null",
        "undefined",
        "true",
        "JSON.stringify({a:1})",
        // serde_json escapes neither HTML-sensitive ASCII nor non-ASCII.
        "'<&>+é'",
        "document.querySelectorAll('a').length",
        "document.body.textContent",
        "(() => { throw new Error('boom'); })()",
    ];

    [ParityTheory]
    [MemberData(nameof(EvalExpressions))]
    public void Eval_result_matches(string expression) =>
        ParityAssert.SameStdOut("fetch", EvalPage, "--eval", expression, "--quiet");

    /// <summary>
    /// <c>--eval</c> combined with <c>--dump</c> reads the page after the eval's
    /// async work settles rather than returning the eval value (issue #248).
    /// </summary>
    [ParityFact]
    public void Eval_with_dump_reads_the_page_after_settling() =>
        ParityAssert.SameStdOut(
            "fetch",
            "data:text/html,<html><body><p>before</p></body></html>",
            "--eval",
            "setTimeout(() => { document.querySelector('p').textContent = 'after'; }, 10)",
            "--dump",
            "text",
            "--wait",
            "1",
            "--quiet");

    /// <summary>
    /// Progress lines go to stderr and <c>--dump</c> output to stdout, so a
    /// pipeline gets only the payload. Both halves are user visible.
    /// </summary>
    [ParityFact]
    public void Progress_lines_are_on_stderr_and_quiet_removes_them()
    {
        var rustLoud = ReferenceEngine.Rust("fetch", Fixture, "--dump", "text");
        var portLoud = ReferenceEngine.Port("fetch", Fixture, "--dump", "text");

        foreach (var (engine, run) in new[] { ("rust", rustLoud), ("port", portLoud) })
        {
            Assert.False(
                run.StdOut.Contains("Fetching ", StringComparison.Ordinal),
                $"{engine} put the progress line on stdout");
            Assert.Contains("Fetching ", run.StdErr, StringComparison.Ordinal);
            Assert.Contains("Page loaded: ", run.StdErr, StringComparison.Ordinal);
        }

        var rustQuiet = ReferenceEngine.Rust("fetch", Fixture, "--dump", "text", "--quiet");
        var portQuiet = ReferenceEngine.Port("fetch", Fixture, "--dump", "text", "--quiet");
        foreach (var run in new[] { rustQuiet, portQuiet })
        {
            Assert.DoesNotContain("Fetching ", run.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("Page loaded: ", run.StdErr, StringComparison.Ordinal);
        }

        // And the payload itself is unchanged by the progress lines.
        ParityAssert.SameLines(rustLoud, portLoud, ["fetch", Fixture, "--dump", "text"]);
        ParityAssert.SameLines(rustQuiet, portQuiet, ["fetch", Fixture, "--dump", "text", "--quiet"]);
    }

    /// <summary>
    /// <c>--wait</c> is a maximum when absent and a fixed delay when supplied,
    /// which is the only reason it is modelled as an option rather than a
    /// defaulted value.
    /// </summary>
    /// <remarks>
    /// The page mutates at 300ms: with no <c>--wait</c> the adaptive settle
    /// waits for quiescence and sees the mutation, <c>--wait 0</c> is a fixed
    /// zero delay and does not, and <c>--wait 1</c> is a fixed one-second delay
    /// and does. Collapsing the two modes reads as a plausible simplification
    /// and silently changes all three answers.
    /// </remarks>
    [ParityTheory]
    [InlineData()]
    [InlineData("--wait", "0")]
    [InlineData("--wait", "1")]
    public void Wait_distinguishes_the_adaptive_cap_from_a_fixed_delay(params string[] wait)
    {
        const string page =
            "data:text/html,<html><body><p id=p>before</p><script>" +
            "setTimeout(()=>{document.getElementById(\"p\").textContent=\"after\"},300)" +
            "</script></body></html>";
        ParityAssert.SameStdOut([.. new[] { "fetch", page, "--dump", "text", "--quiet" }, .. wait]);
    }

    /// <summary>
    /// The <c>Page loaded:</c> progress line prints the page's own URL, which
    /// must be the URL the reference prints.
    /// </summary>
    /// <remarks>
    /// BLOCKED on the recorded Obscura.Browser issue: <c>PageUrl.TryParse</c>
    /// returns a <see cref="Uri"/> and <c>Page.UrlString()</c> reads
    /// <c>AbsoluteUri</c>, which percent-encodes <c>&lt;</c>, <c>&gt;</c> and
    /// space in a cannot-be-a-base URL's opaque path. So for a <c>data:</c> page
    /// this line reads
    /// <c>data:text/html,%3Chtml%3E...</c> where the reference reads
    /// <c>data:text/html,&lt;html&gt;...</c>. The CLI is a symptom, not the
    /// cause; the fix is migrating <c>Obscura.Browser</c> onto
    /// <c>Obscura.Js.Url.UrlRecord</c>. stdout is unaffected, which
    /// <see cref="Progress_lines_are_on_stderr_and_quiet_removes_them"/> and the
    /// whole dump corpus cover. Remove the Skip once Browser serializes through
    /// the WHATWG parser.
    /// </remarks>
    [Fact(Skip = "Obscura.Browser serializes URLs through System.Uri; see 'Open issues' in todo.md")]
    public void Page_loaded_line_prints_the_same_url()
    {
        const string dataUrl =
            "data:text/html,<html><head><title>T</title></head><body><b>a b</b></body></html>";
        var rust = ReferenceEngine.Rust("fetch", dataUrl, "--dump", "text");
        var port = ReferenceEngine.Port("fetch", dataUrl, "--dump", "text");
        Assert.Equal(PageLoadedLine(rust.StdErr), PageLoadedLine(port.StdErr));
    }

    private static string PageLoadedLine(string stderr) =>
        stderr.ReplaceLineEndings("\n").Split('\n')
            .FirstOrDefault(line => line.StartsWith("Page loaded: ", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("no 'Page loaded:' line on stderr");

    /// <summary>
    /// A missing selector warns on stderr and still dumps, rather than failing.
    /// </summary>
    [ParityFact]
    public void Missing_selector_warns_and_still_dumps()
    {
        string[] args = ["fetch", EvalPage, "--selector", "zzz", "--wait", "1", "--dump", "text", "--quiet"];
        var rust = ReferenceEngine.Rust(args);
        var port = ReferenceEngine.Port(args);
        Assert.Equal(0, rust.ExitCode);
        Assert.Equal(rust.ExitCode, port.ExitCode);
        Assert.Contains("Warning: selector 'zzz' not found after 1s", rust.StdErr, StringComparison.Ordinal);
        Assert.Contains("Warning: selector 'zzz' not found after 1s", port.StdErr, StringComparison.Ordinal);
        ParityAssert.SameLines(rust, port, args);
    }

    public static TheoryData<string[]> UsageErrors()
    {
        var data = new TheoryData<string[]>();
        // anyhow bail!: "Error: ..." on stderr, exit 1.
        data.Add(["fetch"]);
        data.Add(["fetch", "file:///tmp/does-not-matter.html", "--file", "urls.txt"]);
        data.Add(["fetch", "--file", "urls.txt", "--dump", "text"]);
        data.Add(["scrape"]);
        // clap usage error: exit 2.
        data.Add(["fetch", "--file", "urls.txt", "--screenshot", "shot.png"]);
        data.Add(["fetch", "https://example.com", "--timeout", "0"]);
        data.Add(["fetch", "https://example.com", "--concurrency", "0"]);
        data.Add(["fetch", "--nope"]);
        data.Add(["badcmd"]);
        return data;
    }

    [ParityTheory]
    [MemberData(nameof(UsageErrors))]
    public void Usage_errors_share_an_exit_code_and_write_nothing_to_stdout(string[] args)
    {
        var rust = ReferenceEngine.Rust(args);
        var port = ReferenceEngine.Port(args);
        Assert.Equal(rust.ExitCode, port.ExitCode);
        Assert.NotEqual(0, rust.ExitCode);
        Assert.Equal(string.Empty, rust.StdOut);
        Assert.Equal(string.Empty, port.StdOut);
    }

    /// <summary>
    /// <c>--version</c> is <c>obscura &lt;version&gt;</c> in both engines. The
    /// version itself differs (the port has its own build metadata), so only the
    /// shape is compared.
    /// </summary>
    [ParityFact]
    public void Version_has_the_same_shape()
    {
        var rust = ReferenceEngine.Rust("--version");
        var port = ReferenceEngine.Port("--version");
        Assert.Equal(0, rust.ExitCode);
        Assert.Equal(0, port.ExitCode);
        Assert.StartsWith("obscura ", rust.StdOut, StringComparison.Ordinal);
        Assert.StartsWith("obscura ", port.StdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>scrape</c> drives one <c>obscura-worker</c> process per URL and emits
    /// the same JSON report, in input order, with the same worker indices.
    /// </summary>
    /// <remarks>
    /// The three timing fields are wall-clock and are masked; everything else,
    /// including key order and the <c>"eval": null</c> that
    /// <c>skip_serializing_if</c> does not skip, is compared byte for byte. This
    /// is also the only end-to-end coverage of the worker binary and its
    /// newline-delimited JSON protocol.
    /// </remarks>
    [ParityFact]
    public void Scrape_report_matches_across_the_worker_protocol()
    {
        var pages = new List<string>();
        try
        {
            foreach (var name in new[] { "A", "B", "C" })
            {
                var path = Path.Combine(Path.GetTempPath(), $"obscura-scrape-{name}-{Guid.NewGuid():N}.html");
                File.WriteAllText(
                    path, $"<html><head><title>{name}</title></head><body><p>{name}</p></body></html>");
                pages.Add($"file://{path}");
            }

            string[] args = ["scrape", pages[0], pages[1], pages[2], "--concurrency", "2", "--quiet"];
            var rust = ReferenceEngine.Rust(args);
            var port = ReferenceEngine.Port(args);
            Assert.Equal(0, rust.ExitCode);
            Assert.Equal(rust.ExitCode, port.ExitCode);
            Assert.Equal(MaskTimings(rust.StdOut), MaskTimings(port.StdOut));
            // The eval-free shape must really be there, not two empty strings.
            Assert.Contains("\"worker\": 1", MaskTimings(rust.StdOut), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var page in pages)
            {
                var path = page["file://".Length..];
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    /// <summary>Replace the wall-clock fields so the rest can be compared exactly.</summary>
    private static string MaskTimings(string json) =>
        System.Text.RegularExpressions.Regex.Replace(
            json.ReplaceLineEndings("\n").TrimEnd('\n'),
            "\"(total_time_ms|avg_time_ms|time_ms)\": [0-9.]+",
            "\"$1\": N");

    /// <summary>
    /// <c>mcp</c> starts the stdio MCP server in both engines and lists the
    /// same tools.
    /// </summary>
    /// <remarks>
    /// This checks the CLI's job: parse the flags, merge the proxy, and hand
    /// off to the MCP server. Deep MCP wire parity belongs to that component's
    /// own suite; here the tool set is the cheapest end-to-end proof the
    /// subcommand actually started an engine rather than exiting quietly.
    /// </remarks>
    [ParityFact]
    public void Mcp_stdio_starts_and_lists_the_same_tools()
    {
        const string session =
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"parity","version":"1"}}}""" + "\n" +
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""" + "\n";

        var rust = PipedEngine.Rust(session, "mcp");
        var port = PipedEngine.Port(session, "mcp");
        Assert.Equal(0, rust.ExitCode);
        Assert.Equal(0, port.ExitCode);

        Assert.Equal(ToolNames(rust.StdOut), ToolNames(port.StdOut));
        Assert.NotEmpty(ToolNames(rust.StdOut));
    }

    private static string[] ToolNames(string stdout)
    {
        foreach (var line in stdout.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            if (parsed?["result"]?["tools"] is JsonArray tools)
            {
                return [.. tools
                    .Select(tool => tool?["name"]?.GetValue<string>() ?? string.Empty)
                    .Order(StringComparer.Ordinal)];
            }
        }
        return [];
    }

    /// <summary>
    /// <c>serve</c> binds the CDP port and answers <c>/json/version</c>
    /// identically in both engines.
    /// </summary>
    /// <remarks>
    /// Each engine gets its own port, so the two servers can run at once and the
    /// port that appears in <c>webSocketDebuggerUrl</c> is masked before the
    /// comparison.
    /// </remarks>
    [ParityFact]
    public async Task Serve_answers_json_version_identically()
    {
        var rust = await ServeJsonVersionAsync(ReferenceEngine.RustBinary!, 19_431).ConfigureAwait(true);
        var port = await ServeJsonVersionAsync(ReferenceEngine.PortBinary!, 19_432).ConfigureAwait(true);
        Assert.Equal(
            rust.Replace("19431", "PORT", StringComparison.Ordinal),
            port.Replace("19432", "PORT", StringComparison.Ordinal));
    }

    /// <summary>
    /// Start one engine's <c>serve</c>, read <c>/json/version</c>, then stop it.
    /// </summary>
    /// <remarks>
    /// The server has to stay up while the request is made, so the shared
    /// run-to-completion helper cannot be used.
    /// </remarks>
    private static async Task<string> ServeJsonVersionAsync(string exe, int port)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "serve", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--quiet" })
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exe}");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // The listener takes a moment to bind; retry rather than sleeping a
            // fixed amount and hoping.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await client
                        .GetStringAsync($"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}/json/version")
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
                {
                    if (attempt >= 40)
                    {
                        throw;
                    }
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }
    }
}
