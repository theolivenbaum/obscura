using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Cli.CommandLine;
using Obscura.Cli.Json;

namespace Obscura.Cli.Commands;

/// <summary>Port of <c>run_parallel_scrape</c>.</summary>
/// <remarks>
/// Each URL is driven by a separate <c>obscura-worker</c> process over a
/// newline-delimited JSON protocol, so one page cannot take the parent down.
/// </remarks>
public static class ScrapeCommand
{
    /// <summary>Dispatch for the <c>scrape</c> subcommand.</summary>
    public static Task RunAsync(CliArgs args, CliCommand.Scrape scrape) =>
        RunParallelScrapeAsync(
            scrape.Urls,
            scrape.Eval,
            scrape.Concurrency,
            scrape.Format,
            (ulong)scrape.Timeout,
            scrape.Quiet,
            args.Proxy,
            args.Stealth,
            args.ObeyRobots);

    /// <summary>The worker binary's file name for this platform.</summary>
    public static string WorkerName =>
        OperatingSystem.IsWindows() ? "obscura-worker.exe" : "obscura-worker";

    /// <summary>
    /// The worker binary next to the running executable, falling back to a bare
    /// name so a PATH lookup can still find it.
    /// </summary>
    public static string ResolveWorkerPath()
    {
        var dir = Environment.ProcessPath is { } exe ? Path.GetDirectoryName(exe) : null;
        return dir is null ? WorkerName : Path.Combine(dir, WorkerName);
    }

    /// <summary>Port of <c>run_parallel_scrape</c>.</summary>
    public static async Task RunParallelScrapeAsync(
        IReadOnlyList<string> urls,
        string? eval,
        int concurrency,
        string format,
        ulong timeoutSecs,
        bool quiet,
        string? proxy,
        bool stealth,
        bool obeyRobots)
    {
        var total = urls.Count;
        var start = Stopwatch.StartNew();

        if (total == 0)
        {
            throw new CliException("No URLs provided. Pass at least one URL to scrape.");
        }

        if (!quiet)
        {
            Console.Error.WriteLine(
                $"Scraping {total} URLs with {concurrency} concurrent workers (per-worker timeout: {timeoutSecs}s)...");
        }

        var workerPath = ResolveWorkerPath();
        if (!File.Exists(workerPath))
        {
            throw new CliException(
                $"Worker binary not found at {workerPath}. Build with: dotnet build -c Release");
        }

        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var workerTimeout = TimeSpan.FromSeconds(timeoutSecs);
        var readTimeout = TimeSpan.FromSeconds(Math.Min(timeoutSecs, 30));
        var shutdownTimeout = TimeSpan.FromSeconds(5);

        var tasks = new Task<JsonObject>[total];
        for (var i = 0; i < total; i++)
        {
            var index = i;
            var url = urls[i];
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    return await ScrapeOneAsync(
                        workerPath, url, index, eval, proxy, stealth, obeyRobots,
                        workerTimeout, readTimeout, shutdownTimeout).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }

        var results = new JsonArray();
        foreach (var task in tasks)
        {
            results.Add(await task.ConfigureAwait(false));
        }

        var totalTime = start.Elapsed;
        var totalMs = (long)totalTime.TotalMilliseconds;

        if (format == "json")
        {
            var output = new JsonObject
            {
                ["total_urls"] = JsonValue.Create(total),
                ["concurrency"] = JsonValue.Create(concurrency),
                ["total_time_ms"] = JsonValue.Create(totalMs),
                // Rust computes `total_time_ms as f64 / total as f64`, so this is
                // always an f64 and prints with a decimal point.
                ["avg_time_ms"] = JsonValue.Create((double)totalMs / total),
                ["results"] = results,
            };
            Console.Out.WriteLine(SerdeJson.ToJsonPretty(output));
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                var obj = (JsonObject)r!;
                var url = Str(obj, "url") ?? "?";
                var title = Str(obj, "title") ?? string.Empty;
                var time = obj.TryGetPropertyValue("time_ms", out var t)
                    && t?.GetValueKind() == JsonValueKind.Number
                    ? t.GetValue<long>()
                    : 0L;
                var evalValue = obj.TryGetPropertyValue("eval", out var e) ? e : null;
                sb.Append(evalValue is null || evalValue.GetValueKind() == JsonValueKind.Null
                    ? $"{time}ms\t{url}\t{title}"
                    : $"{time}ms\t{url}\t{SerdeJson.ToJson(evalValue)}");
                sb.Append('\n');
            }
            Console.Out.Write(sb.ToString());
            Console.Out.Flush();
            if (!quiet)
            {
                Console.Error.WriteLine(
                    $"\nTotal: {totalMs}ms for {total} URLs ({concurrency} concurrent)");
            }
        }
    }

    private static string? Str(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node?.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : null;

    private static async Task<JsonObject> ScrapeOneAsync(
        string workerPath,
        string url,
        int index,
        string? eval,
        string? proxy,
        bool stealth,
        bool obeyRobots,
        TimeSpan workerTimeout,
        TimeSpan readTimeout,
        TimeSpan shutdownTimeout)
    {
        var taskStart = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(workerPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["OBSCURA_PROXY"] = proxy ?? string.Empty;
        psi.Environment["OBSCURA_STEALTH"] = stealth ? "1" : string.Empty;
        psi.Environment["OBSCURA_OBEY_ROBOTS"] = obeyRobots ? "1" : string.Empty;

        Process? child;
        try
        {
            child = Process.Start(psi);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Failure(url, $"Failed to spawn worker: {error.Message}", taskStart);
        }
        if (child is null)
        {
            return Failure(url, "Failed to spawn worker", taskStart);
        }

        using (child)
        {
            // Drain stderr so a chatty worker cannot fill its pipe and block.
            _ = child.StandardError.ReadToEndAsync();
            using var budget = new CancellationTokenSource(workerTimeout);
            try
            {
                var stdin = child.StandardInput;
                var reader = child.StandardOutput;

                await SendAsync(stdin, new JsonObject
                {
                    ["cmd"] = JsonValue.Create("navigate"),
                    ["url"] = JsonValue.Create(url),
                }).ConfigureAwait(false);

                var navLine = await ReadLineAsync(reader, readTimeout, budget.Token).ConfigureAwait(false);
                var navResponse = ParseOrNotOk(navLine);
                if (navResponse["ok"]?.GetValueKind() != JsonValueKind.True)
                {
                    var message = Str(navResponse, "error") ?? "navigate failed";
                    return await KillAndFailAsync(child, url, message, taskStart, shutdownTimeout)
                        .ConfigureAwait(false);
                }

                var title = navResponse["result"] is JsonObject navResult
                    ? Str(navResult, "title") ?? string.Empty
                    : string.Empty;

                JsonNode? evalResult = null;
                if (eval is { } expression)
                {
                    await SendAsync(stdin, new JsonObject
                    {
                        ["cmd"] = JsonValue.Create("evaluate"),
                        ["expression"] = JsonValue.Create(expression),
                    }).ConfigureAwait(false);
                    var evalLine = await ReadLineAsync(reader, readTimeout, budget.Token).ConfigureAwait(false);
                    evalResult = ParseOrNotOk(evalLine)["result"]?.DeepClone();
                }

                await SendAsync(stdin, new JsonObject { ["cmd"] = JsonValue.Create("shutdown") })
                    .ConfigureAwait(false);
                await WaitForExitAsync(child, shutdownTimeout).ConfigureAwait(false);

                return new JsonObject
                {
                    ["url"] = JsonValue.Create(url),
                    ["title"] = JsonValue.Create(title),
                    ["eval"] = evalResult,
                    ["time_ms"] = JsonValue.Create((long)taskStart.Elapsed.TotalMilliseconds),
                    ["worker"] = JsonValue.Create(index),
                };
            }
            catch (WorkerReadTimeoutException)
            {
                return await KillAndFailAsync(child, url, "timeout", taskStart, shutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return await KillAndFailAsync(child, url, "timeout", taskStart, shutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                return await KillAndFailAsync(child, url, "Write failed", taskStart, shutdownTimeout)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task SendAsync(StreamWriter stdin, JsonObject command)
    {
        await stdin.WriteAsync(SerdeJson.ToJson(command) + "\n").ConfigureAwait(false);
        await stdin.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<string> ReadLineAsync(
        StreamReader reader,
        TimeSpan readTimeout,
        CancellationToken cancellationToken)
    {
        using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readBudget.CancelAfter(readTimeout);
        var line = await reader.ReadLineAsync(readBudget.Token).ConfigureAwait(false);
        return line is null or "" ? throw new WorkerReadTimeoutException() : line;
    }

    private static JsonObject ParseOrNotOk(string line)
    {
        try
        {
            return JsonNode.Parse(line.Trim()) as JsonObject
                ?? new JsonObject { ["ok"] = JsonValue.Create(false) };
        }
        catch (JsonException)
        {
            return new JsonObject { ["ok"] = JsonValue.Create(false) };
        }
    }

    private static async Task<JsonObject> KillAndFailAsync(
        Process child, string url, string error, Stopwatch taskStart, TimeSpan shutdownTimeout)
    {
        try
        {
            child.Kill(entireProcessTree: true);
        }
        catch (Exception kill) when (kill is InvalidOperationException or NotSupportedException)
        {
            // Already gone.
        }
        await WaitForExitAsync(child, shutdownTimeout).ConfigureAwait(false);
        return Failure(url, error, taskStart);
    }

    private static async Task WaitForExitAsync(Process child, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await child.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
        {
            // The worker outlived its shutdown budget; the process handle is
            // disposed by the caller either way.
        }
    }

    private static JsonObject Failure(string url, string error, Stopwatch taskStart) => new()
    {
        ["url"] = JsonValue.Create(url),
        ["error"] = JsonValue.Create(error),
        ["time_ms"] = JsonValue.Create((long)taskStart.Elapsed.TotalMilliseconds),
    };

    private sealed class WorkerReadTimeoutException : Exception;
}
