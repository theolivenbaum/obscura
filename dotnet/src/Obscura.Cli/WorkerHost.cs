using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Browser;
using Obscura.Cli.Json;
using Page = Obscura.Browser.Page;

namespace Obscura.Cli;

/// <summary>
/// Port of <c>crates/obscura-cli/src/worker.rs</c>: the <c>obscura-worker</c>
/// binary that parallel <c>scrape</c> drives over newline-delimited JSON.
/// </summary>
/// <remarks>
/// Rust builds this as a second binary from the same crate. The port ships one
/// managed assembly and selects the worker by the name of the launcher it was
/// started through, so the file layout beside the executable is the same
/// (<c>obscura</c> and <c>obscura-worker</c> side by side) and
/// <c>run_parallel_scrape</c>'s sibling lookup works unchanged.
/// </remarks>
public static class WorkerHost
{
    /// <summary>The launcher name that selects worker mode.</summary>
    public const string LauncherName = "obscura-worker";

    /// <summary>
    /// Whether this process was started as the worker, either through the
    /// <c>obscura-worker</c> launcher or with <c>OBSCURA_WORKER=1</c> set.
    /// </summary>
    public static bool IsWorkerProcess()
    {
        if (Environment.GetEnvironmentVariable("OBSCURA_WORKER") == "1")
        {
            return true;
        }
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }
        var name = Path.GetFileNameWithoutExtension(exe);
        return string.Equals(name, LauncherName, StringComparison.Ordinal);
    }

    /// <summary>Read a boolean env flag the way the Rust worker does.</summary>
    public static bool EnvFlag(string? raw) =>
        raw is not null && raw.Trim() is "1" or "true" or "yes" or "on";

    /// <summary>Run the worker loop against the process's stdin/stdout.</summary>
    public static async Task<int> RunAsync()
    {
        var proxy = Environment.GetEnvironmentVariable("OBSCURA_PROXY")?.Trim();
        if (proxy is { Length: 0 })
        {
            proxy = null;
        }
        var stealth = EnvFlag(Environment.GetEnvironmentVariable("OBSCURA_STEALTH"));
        var obeyRobots = EnvFlag(Environment.GetEnvironmentVariable("OBSCURA_OBEY_ROBOTS"));

        var context = BrowserContext.WithOptions("worker", proxy, stealth);
        context.ObeyRobots = obeyRobots;
        using var page = new Page("page-1", context);

        await RunLoopAsync(page, Console.In, Console.Out).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// The command loop, over explicit streams so it can be driven in a test
    /// without spawning a process.
    /// </summary>
    public static async Task RunLoopAsync(Page page, TextReader input, TextWriter output)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync().ConfigureAwait(false);
            }
            catch (IOException error)
            {
                Console.Error.WriteLine($"Worker stdin error: {error.Message}");
                break;
            }
            if (line is null)
            {
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            JsonObject? command;
            try
            {
                command = JsonNode.Parse(trimmed) as JsonObject;
            }
            catch (JsonException error)
            {
                await WriteAsync(output, Error($"Invalid command: {error.Message}")).ConfigureAwait(false);
                continue;
            }
            if (command is null || Str(command, "cmd") is not { } cmd)
            {
                await WriteAsync(output, Error("Invalid command: missing field `cmd`")).ConfigureAwait(false);
                continue;
            }

            JsonObject response;
            switch (cmd)
            {
                case "navigate":
                {
                    if (Str(command, "url") is not { } url)
                    {
                        await WriteAsync(output, Error("Invalid command: missing field `url`"))
                            .ConfigureAwait(false);
                        continue;
                    }
                    try
                    {
                        await page.NavigateAsync(url).ConfigureAwait(false);
                        response = Success(new JsonObject
                        {
                            ["title"] = JsonValue.Create(page.Title),
                            ["url"] = JsonValue.Create(page.UrlString()),
                        });
                    }
                    catch (PageException error)
                    {
                        response = Error(error.Message);
                    }
                    break;
                }
                case "evaluate":
                {
                    if (Str(command, "expression") is not { } expression)
                    {
                        await WriteAsync(output, Error("Invalid command: missing field `expression`"))
                            .ConfigureAwait(false);
                        continue;
                    }
                    // Await promise-returning expressions so async IIFEs resolve
                    // before serialization; the sync path serializes an
                    // unresolved Promise as {}. A 30s cap matches the CDP await
                    // timeout so a never-settling promise cannot hang the worker.
                    JsonNode? result;
                    try
                    {
                        var info = await page
                            .EvaluateForCdpWithTimeoutAsync(expression, true, true, 30_000)
                            .ConfigureAwait(false);
                        // Rust reads `info.value`, an Option that is Some even
                        // when the value itself is JSON null, and only falls back
                        // to the description when it is None. A JsonNode has no
                        // representation for a bare JSON null other than a null
                        // reference, so those two cases collapse here and
                        // `Thrown` is what separates them: a thrown value (or a
                        // rejected promise) is exactly where Rust has None and
                        // uses the description. Without this, `--eval null` and
                        // `--eval undefined` reported the string "null".
                        result = info.Value is null && info.Thrown
                            ? JsonValue.Create(info.Description)
                            : info.Value?.DeepClone();
                    }
                    // Rust matches `Err(_) => Value::Null`, so every failure mode
                    // becomes a successful reply carrying null rather than
                    // ending the command loop. Narrowing this to a few exception
                    // types would let an unexpected one kill the worker, and the
                    // parent would report "Read failed" for what the reference
                    // answers.
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        Log.Debug($"evaluate failed: {error.Message}");
                        result = null;
                    }
                    response = Success(result);
                    break;
                }
                case "title":
                    response = Success(JsonValue.Create(page.Title));
                    break;
                case "dump_html":
                    response = Success(JsonValue.Create(page.WithDom(dom =>
                        dom.TryQuerySelector("html", out var html, out _) && html is { } id
                            ? dom.OuterHtml(id)
                            : dom.InnerHtml(dom.Document)) ?? string.Empty));
                    break;
                case "dump_text":
                    response = Success(JsonValue.Create(page.WithDom(dom =>
                        dom.TryQuerySelector("body", out var body, out _) && body is { } id
                            ? dom.TextContent(id)
                            : string.Empty) ?? string.Empty));
                    break;
                case "shutdown":
                    await WriteAsync(output, Success(JsonValue.Create("bye"))).ConfigureAwait(false);
                    return;
                default:
                    await WriteAsync(output, Error(
                        $"Invalid command: unknown variant `{cmd}`, expected one of `navigate`, `evaluate`, `title`, `dump_html`, `dump_text`, `shutdown`"))
                        .ConfigureAwait(false);
                    continue;
            }

            await WriteAsync(output, response).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>WorkerResponse::success</c>. <c>result</c> and <c>error</c> both carry
    /// <c>skip_serializing_if = "Option::is_none"</c>, so only the populated one
    /// appears; a successful result of JSON null is still emitted.
    /// </summary>
    public static JsonObject Success(JsonNode? result) => new()
    {
        ["ok"] = JsonValue.Create(true),
        ["result"] = result,
    };

    /// <summary><c>WorkerResponse::error</c>.</summary>
    public static JsonObject Error(string message) => new()
    {
        ["ok"] = JsonValue.Create(false),
        ["error"] = JsonValue.Create(message),
    };

    private static async Task WriteAsync(TextWriter output, JsonObject response)
    {
        await output.WriteAsync(SerdeJson.ToJson(response) + "\n").ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static string? Str(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node?.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : null;
}
