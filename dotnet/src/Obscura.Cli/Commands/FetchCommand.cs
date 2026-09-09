using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Browser;
using Obscura.Cli.CommandLine;
using Obscura.Cli.Json;
using Obscura.Net;
using Obscura.Render;
using Page = Obscura.Browser.Page;

namespace Obscura.Cli.Commands;

/// <summary>Port of <c>run_fetch</c> and everything it reaches.</summary>
public static class FetchCommand
{
    /// <summary>Dispatch for the <c>fetch</c> subcommand, including batch mode.</summary>
    public static async Task RunAsync(CliArgs args, CliCommand.Fetch fetch)
    {
        if (fetch.File is { } file)
        {
            if (fetch.Url is not null)
            {
                throw new CliException("Pass URLs via a positional argument or --file, not both.");
            }
            if (fetch.Screenshot is not null)
            {
                throw new CliException(
                    "--screenshot is only supported for a single URL, not --file batch mode.");
            }
            // Batch mode is raw HTTP only. Rendering each URL through the
            // browser/JS stack is what `scrape` is for.
            if (fetch.Dump is not (null or DumpFormat.Original))
            {
                throw new CliException(
                    "batch mode (--file) only supports --dump original. Use `scrape` for rendered/DOM output.");
            }

            var urls = ReadUrlsFromFile(file);
            await RunBatchFetchAsync(
                urls,
                fetch.Concurrency,
                fetch.Timeout,
                fetch.UserAgent,
                args.Proxy,
                fetch.Output,
                fetch.Quiet,
                args.Stealth).ConfigureAwait(false);
            return;
        }

        var url = fetch.Url
            ?? throw new CliException("No URL provided. Pass a URL, or a list of URLs with --file <path>.");

        await RunFetchAsync(
            url,
            fetch.Dump,
            fetch.Selector,
            (ulong)(fetch.Wait ?? 5),
            waitIsFixed: fetch.Wait is not null,
            (ulong)fetch.Timeout,
            fetch.WaitUntil,
            fetch.UserAgent,
            args.Stealth,
            fetch.Eval,
            fetch.Output,
            fetch.Quiet,
            args.Proxy,
            fetch.StorageDir,
            args.AllowPrivateNetwork,
            args.ObeyRobots,
            fetch.Screenshot).ConfigureAwait(false);
    }

    /// <summary>Give the page the same end-to-end navigation ceiling as the CLI request deadline.</summary>
    public static void ConfigureFetchNavigationTimeout(Page page, ulong timeoutSecs) =>
        page.SetNavigationTimeout(TimeSpan.FromSeconds(timeoutSecs));

    private static Task SettlePageAsync(Page page, ulong waitSecs, bool fixedDelay)
    {
        var waitMs = waitSecs > ulong.MaxValue / 1000 ? ulong.MaxValue : waitSecs * 1000;
        return fixedDelay ? page.SettleForDurationAsync(waitMs) : page.SettleAsync(waitMs);
    }

    /// <summary>Port of <c>run_fetch</c>.</summary>
    public static async Task RunFetchAsync(
        string urlStr,
        DumpFormat? dump,
        string? selector,
        ulong waitSecs,
        bool waitIsFixed,
        ulong timeoutSecs,
        string waitUntil,
        string? userAgent,
        bool stealth,
        string? eval,
        string? output,
        bool quiet,
        string? proxy,
        string? storageDir,
        bool allowPrivateNetwork,
        bool obeyRobots,
        string? screenshot)
    {
        // Whether the user explicitly passed --dump. With --eval also present
        // this decides whether the eval value is returned or the page is read
        // after the eval's async work settles.
        var dumpSpecified = dump is not null;
        var format = dump ?? DumpFormat.Html;

        // --dump original short-circuits the browser stack entirely: fetch the
        // raw response body over HTTP and stream the bytes verbatim. Useful for
        // binary payloads and any non-HTML resource where parsing the body
        // through the DOM/JS layer would corrupt or discard data.
        if (format == DumpFormat.Original)
        {
            var bytes = await FetchOriginalBytesAsync(urlStr, proxy, userAgent, timeoutSecs, stealth)
                .ConfigureAwait(false);
            await WriteOrPrintBytesAsync(bytes, output).ConfigureAwait(false);
            return;
        }

        var context = BrowserContext.WithStorageAndNetwork(
            "fetch", proxy, stealth, userAgent, storageDir, allowPrivateNetwork);
        context.ObeyRobots = obeyRobots;
        using var page = new Page("fetch-page", context);
        ConfigureFetchNavigationTimeout(page, timeoutSecs);

        // A screenshot viewport is also the navigation viewport: responsive
        // frameworks must build the DOM for the same dimensions later painted.
        (float Width, float Height)? screenshotViewport = screenshot is null
            ? null
            : (EnvFloat("OBSCURA_SHOT_W") ?? 1280.0f, EnvFloat("OBSCURA_SHOT_H") ?? 720.0f);
        if (screenshotViewport is { } viewportOverride)
        {
            page.SetViewport(viewportOverride);
        }

        if (userAgent is { } ua)
        {
            page.HttpClient.SetUserAgent(ua);
        }

        var waitCondition = WaitUntilExtensions.ParseWaitUntil(waitUntil);

        if (!quiet)
        {
            Console.Error.WriteLine($"Fetching {urlStr}...");
        }

        // The paired corpus opts into a truthful capture boundary: its read-only
        // evaluation runs after all settle passes and the final scroll reassert,
        // immediately before screenshot paint. Ordinary CLI evaluation keeps its
        // evaluate-then-settle behavior when this private variable is absent.
        var evalAtCaptureBoundary = screenshot is not null
            && eval is not null
            && Environment.GetEnvironmentVariable("OBSCURA_SHOT_EVAL_AT_CAPTURE") == "1";
        var controlledScrollRequest = screenshot is null ? null : ControlledScrollRequest();

        HardDeadline.Arm(HardDeadline.Budget(
            timeoutSecs,
            waitSecs,
            HardDeadline.SettlePasses(
                evalAtCaptureBoundary,
                controlledScrollRequest is not null,
                eval is not null,
                screenshot is not null,
                selector is not null,
                dumpSpecified)));

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSecs));
            await page.NavigateWithWaitAsync(urlStr, waitCondition, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new CliException($"Timed out navigating to {urlStr} after {timeoutSecs}s");
        }
        catch (PageException error)
        {
            throw new CliException($"Failed to navigate to {urlStr}: {error.Message}");
        }

        if (!quiet)
        {
            Console.Error.WriteLine($"Page loaded: {page.UrlString()} - \"{page.Title}\"");
        }

        // --wait is a post-load settle: drive the event loop so timers, async
        // work, and completion callbacks run before the page is read. Returns
        // early once the loop is idle, so static pages stay fast.
        await SettlePageAsync(page, waitSecs, waitIsFixed).ConfigureAwait(false);

        // Rust models this as Option<Value>, where Some(Value::Null) is a real
        // eval result of null. A bare JsonNode? cannot tell those apart, so the
        // presence flag is carried alongside.
        DeferredEval deferredEvalOutput = default;
        JsonNode? initialControlledScroll = null;
        if (evalAtCaptureBoundary && controlledScrollRequest is { } initialRequest)
        {
            initialControlledScroll = page.Evaluate(InitialControlledScrollScript(initialRequest));
            await SettlePageAsync(page, waitSecs, waitIsFixed).ConfigureAwait(false);
        }

        if (!evalAtCaptureBoundary && eval is { } expression)
        {
            // Bound the eval by the same budget as navigation so a runaway
            // expression cannot hang.
            var result = page.EvaluateWithTimeout(expression, TimeSpan.FromSeconds(timeoutSecs));

            // A bare --eval (no --selector, --dump, or --screenshot) returns the
            // eval value directly, so synchronous expressions are unchanged.
            // Screenshot captures continue below so an evaluation such as
            // scrollTo() affects the painted viewport instead of being ignored.
            if (!dumpSpecified && selector is null && screenshot is null)
            {
                var rendered = result?.GetValueKind() switch
                {
                    JsonValueKind.String => result.GetValue<string>(),
                    null or JsonValueKind.Null => "null",
                    _ => SerdeJson.ToJson(result),
                };
                await WriteOrPrintAsync(rendered, output).ConfigureAwait(false);
                context.SaveCookies();
                return;
            }
            if (screenshot is not null)
            {
                deferredEvalOutput = new DeferredEval(true, result);
            }

            // --eval combined with --selector, --dump, and/or --screenshot
            // typically kicks off async work that writes the DOM. Drive the
            // event loop again so that work completes, then fall through to
            // selector/capture/dump instead of returning a still-pending value.
            await SettlePageAsync(page, waitSecs, waitIsFixed).ConfigureAwait(false);
        }

        if (selector is { } sel)
        {
            var found = await WaitForSelectorAsync(page, sel, waitSecs).ConfigureAwait(false);
            if (!found)
            {
                Console.Error.WriteLine($"Warning: selector '{sel}' not found after {waitSecs}s");
            }
        }

        if (screenshot is { } screenshotPath)
        {
            await CaptureScreenshotAsync(
                page,
                context,
                screenshotPath,
                screenshotViewport,
                eval,
                evalAtCaptureBoundary,
                deferredEvalOutput,
                initialControlledScroll,
                controlledScrollRequest,
                timeoutSecs,
                quiet).ConfigureAwait(false);
            return;
        }

        var text = format switch
        {
            DumpFormat.Html => DumpExtractors.DumpHtml(page),
            DumpFormat.Text => DumpExtractors.DumpText(page),
            DumpFormat.Links => DumpExtractors.DumpLinks(page),
            DumpFormat.Markdown => DumpExtractors.DumpMarkdown(page),
            DumpFormat.Assets => DumpExtractors.DumpAssets(page),
            DumpFormat.Cookies => DumpExtractors.DumpCookies(page),
            // Handled above via the short-circuit branch; unreachable here.
            _ => throw new UnreachableException("Original dump handled before page navigation"),
        };
        await WriteOrPrintAsync(text, output).ConfigureAwait(false);

        // Save cookies to disk if a storage dir is configured.
        context.SaveCookies();
    }

    private static async Task CaptureScreenshotAsync(
        Page page,
        BrowserContext context,
        string path,
        (float Width, float Height)? screenshotViewport,
        string? eval,
        bool evalAtCaptureBoundary,
        DeferredEval deferredEvalOutput,
        JsonNode? initialControlledScroll,
        (double X, string RequestedY)? controlledScrollRequest,
        ulong timeoutSecs,
        bool quiet)
    {
        var resourceDeadlineMs = EnvUlong("OBSCURA_RENDER_RESOURCE_DEADLINE_MS") ?? 3_000;
        await page.PrepareScreenshotResourcesAsync(resourceDeadlineMs).ConfigureAwait(false);

        // Default CSS-pixel viewport, matching the engine's innerWidth/Height.
        var viewport = screenshotViewport ?? (1280.0f, 720.0f);

        // Ordinary screenshots sample the live document timeline. The comparison
        // harness can request an exact instant (normally T=0) so both engines
        // paint the same animation frame.
        AnimationSampleTime? requestedAnimationSample = null;
        if (Environment.GetEnvironmentVariable("OBSCURA_SHOT_ANIMATION_TIME_MS") is { } raw)
        {
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                || !float.IsFinite(ms) || ms < 0.0f)
            {
                throw new CliException(
                    "OBSCURA_SHOT_ANIMATION_TIME_MS must be a finite non-negative number");
            }
            requestedAnimationSample = new AnimationSampleTime(ms);
        }

        byte[]? Capture() => requestedAnimationSample is { } sample
            ? page.ScreenshotAtAnimationTime(viewport, sample)
            : page.Screenshot(viewport);

        // The parity harness performs one throwaway paint in both engines before
        // observing image/font readiness, because retained render resources
        // resolve during prepare/paint. Keep this private opt-in out of ordinary
        // CLI screenshots.
        var warmupCapture = Environment.GetEnvironmentVariable("OBSCURA_SHOT_RESOURCE_WARMUP") == "1";
        if (warmupCapture)
        {
            if (Capture() is null)
            {
                throw new CliException("resource warm-up screenshot failed: page has no DOM to render");
            }
            // Give completion callbacks one bounded task turn before the
            // capture-boundary evaluation reads resource state.
            await page.SettleAsync(1).ConfigureAwait(false);
        }

        // Paired renderer captures need a stable final coordinate after the
        // post-eval settle. Authored smooth scrolling and scroll anchoring may
        // legitimately move an earlier scrollTo, so the comparison harness opts
        // into one instant reassertion at the actual capture boundary.
        JsonNode? controlledScroll = controlledScrollRequest is { } request
            ? page.Evaluate(ReassertControlledScrollScript(request))
            : null;

        if (evalAtCaptureBoundary && eval is { } expression)
        {
            deferredEvalOutput = new DeferredEval(
                true, page.EvaluateWithTimeout(expression, TimeSpan.FromSeconds(timeoutSecs)));
        }

        var captureState = !deferredEvalOutput.Present
            ? null
            : page.Evaluate(
                "(()=>({" +
                "scrollX:window.scrollX,scrollY:window.scrollY," +
                "innerWidth:window.innerWidth,innerHeight:window.innerHeight," +
                "scrollWidth:document.documentElement?document.documentElement.scrollWidth:0," +
                "scrollHeight:document.documentElement?document.documentElement.scrollHeight:0" +
                "}))()");

        var png = Capture() ?? throw new CliException("screenshot failed: page has no DOM to render");
        await File.WriteAllBytesAsync(path, png).ConfigureAwait(false);

        // A screenshot+eval command used to ignore the expression completely.
        // Emit both its value and a standard state sampled after the post-eval
        // settle so automation can record the exact live viewport painted.
        if (deferredEvalOutput.Present)
        {
            var report = controlledScroll?.DeepClone();
            if (report is JsonObject reportObject && initialControlledScroll is JsonObject initial)
            {
                foreach (var key in new[]
                         {
                             "preInitialActual", "postInitialActual", "initialBehavior", "initialPhase",
                         })
                {
                    if (initial.TryGetPropertyValue(key, out var value))
                    {
                        reportObject[key] = value?.DeepClone();
                    }
                }
            }

            Console.Out.WriteLine(SerdeJson.ToJson(new JsonObject
            {
                ["evaluation"] = deferredEvalOutput.Value?.DeepClone(),
                ["controlledScroll"] = report,
                ["resourceWarmup"] = new JsonObject
                {
                    ["performed"] = JsonValue.Create(warmupCapture),
                    ["discardedShots"] = JsonValue.Create(warmupCapture ? 1 : 0),
                    ["taskTurnMs"] = JsonValue.Create(warmupCapture ? 1 : 0),
                    ["phase"] = JsonValue.Create("before-final-scroll-reassert-and-state-sample"),
                },
                ["captureState"] = captureState?.DeepClone(),
            }));
        }

        if (!quiet)
        {
            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                length = 0;
            }
            Console.Error.WriteLine($"Screenshot written: {path} ({length} bytes)");
        }
        context.SaveCookies();
    }

    /// <summary>
    /// Rust's <c>Option&lt;Value&gt;</c> for the deferred eval result.
    /// <c>Some(Value::Null)</c> is a real result of null and must still print,
    /// which a bare <see cref="JsonNode"/> reference cannot express.
    /// </summary>
    private readonly record struct DeferredEval(bool Present, JsonNode? Value);

    private static (double X, string RequestedY)? ControlledScrollRequest()
    {
        if (Environment.GetEnvironmentVariable("OBSCURA_SHOT_SCROLL_Y") is not { } rawY)
        {
            return null;
        }
        double x = 0.0;
        if (Environment.GetEnvironmentVariable("OBSCURA_SHOT_SCROLL_X") is { } rawX)
        {
            if (!double.TryParse(rawX, NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !double.IsFinite(x))
            {
                return null;
            }
        }
        string requestedY;
        if (string.Equals(rawY, "bottom", StringComparison.OrdinalIgnoreCase))
        {
            requestedY = "document.documentElement.scrollHeight";
        }
        else if (double.TryParse(rawY, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedY)
                 && double.IsFinite(parsedY))
        {
            requestedY = RustDisplay(parsedY);
        }
        else
        {
            return null;
        }
        return (x, requestedY);
    }

    private static string InitialControlledScrollScript((double X, string RequestedY) request) =>
        "(()=>{" +
        $"const requestedX={RustDisplay(request.X)},requestedY={request.RequestedY};" +
        "const preInitial={x:window.scrollX,y:window.scrollY};" +
        "window.scrollTo(requestedX,requestedY);" +
        "return {requested:{x:requestedX,y:requestedY}," +
        "preInitialActual:preInitial," +
        "postInitialActual:{x:window.scrollX,y:window.scrollY}," +
        "initialBehavior:'authored'," +
        "initialPhase:'before-controlled-scroll-settle'}" +
        "})()";

    private static string ReassertControlledScrollScript((double X, string RequestedY) request) =>
        "(()=>{" +
        $"const requestedX={RustDisplay(request.X)},requestedY={request.RequestedY};" +
        "const preReassert={x:window.scrollX,y:window.scrollY};" +
        "const root=document.documentElement;" +
        "const previous=root?root.style.getPropertyValue('scroll-behavior'):'';" +
        "const priority=root?root.style.getPropertyPriority('scroll-behavior'):'';" +
        "if(root)root.style.setProperty('scroll-behavior','auto','important');" +
        "window.scrollTo(requestedX,requestedY);" +
        "if(root){if(previous)root.style.setProperty('scroll-behavior',previous,priority);" +
        "else root.style.removeProperty('scroll-behavior')}" +
        "return {requested:{x:requestedX,y:requestedY}," +
        "preReassertActual:preReassert," +
        "finalReassertActual:{x:window.scrollX,y:window.scrollY}," +
        "behavior:'instant'," +
        "phase:'immediately-before-capture-state-and-screenshot'}" +
        "})()";

    /// <summary>
    /// Rust's <c>Display</c> for <c>f64</c>: the shortest decimal that round
    /// trips, positional, with no gratuitous trailing <c>.0</c>. These values are
    /// interpolated into JS source, so the spelling has to match.
    /// </summary>
    private static string RustDisplay(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static float? EnvFloat(string name) =>
        Environment.GetEnvironmentVariable(name) is { } raw
        && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static ulong? EnvUlong(string name) =>
        Environment.GetEnvironmentVariable(name) is { } raw
        && ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>Port of <c>fetch_original_response</c>.</summary>
    public static async Task<Response> FetchOriginalResponseAsync(
        string urlStr,
        string? proxy,
        string? userAgent,
        ulong timeoutSecs,
        bool stealth)
    {
        if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var url))
        {
            throw new CliException($"Invalid URL '{urlStr}': relative URL without a base");
        }

        // `--dump original` short-circuits the browser stack and builds its own
        // client here rather than going through BrowserContext / Page, which is
        // where --stealth is normally applied. file:// has no TLS handshake to
        // impersonate and the stealth client only speaks http(s), so Rust
        // excludes it there exactly as the ordinary client excludes it
        // internally. The stealth transport itself has no managed equivalent
        // (see "Known deviations" in todo.md), so the request falls through to
        // the ordinary client and says so rather than downgrading silently.
        if (stealth && !string.Equals(url.Scheme, "file", StringComparison.Ordinal))
        {
            Log.Warn(
                "--stealth: TLS fingerprint impersonation is not available in this build, " +
                "so --dump original uses the ordinary transport");
        }

        using var client = new ObscuraHttpClient(new CookieJar(), proxy);
        if (userAgent is { } ua)
        {
            client.SetUserAgent(ua);
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSecs));
            return await client.FetchAsync(url, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new CliException($"Timed out fetching {urlStr} after {timeoutSecs}s");
        }
        catch (ObscuraNetException error)
        {
            throw new CliException($"Failed to fetch {urlStr}: {error.Message}");
        }
    }

    /// <summary>Port of <c>fetch_original_bytes</c>.</summary>
    public static async Task<byte[]> FetchOriginalBytesAsync(
        string urlStr,
        string? proxy,
        string? userAgent,
        ulong timeoutSecs,
        bool stealth) =>
        (await FetchOriginalResponseAsync(urlStr, proxy, userAgent, timeoutSecs, stealth)
            .ConfigureAwait(false)).Body;

    /// <summary>
    /// Read newline-delimited URLs from <paramref name="path"/>, or stdin when it
    /// is <c>-</c>. Blank lines and <c>#</c> comments are dropped, and
    /// surrounding whitespace is trimmed so an indented copy-paste still works.
    /// </summary>
    public static List<string> ReadUrlsFromFile(string path)
    {
        string content;
        if (path == "-")
        {
            try
            {
                content = Console.In.ReadToEnd();
            }
            catch (IOException error)
            {
                throw new CliException($"Failed to read URLs from stdin: {error.Message}");
            }
        }
        else
        {
            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                throw new CliException($"Failed to read {path}: {error.Message}");
            }
        }

        var urls = new List<string>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            urls.Add(line);
        }
        return urls;
    }

    /// <summary>
    /// Batch raw fetch: run <c>--dump original</c> over many URLs concurrently
    /// and print one JSON status line per URL.
    /// </summary>
    /// <remarks>
    /// The raw-resource-check counterpart to <c>scrape</c>; it never renders, so
    /// there is no browser/JS cost per URL. Output stays in input order
    /// regardless of completion order.
    /// </remarks>
    public static async Task RunBatchFetchAsync(
        IReadOnlyList<string> urls,
        int concurrency,
        long timeoutSecs,
        string? userAgent,
        string? proxy,
        string? output,
        bool quiet,
        bool stealth)
    {
        var total = urls.Count;
        if (total == 0)
        {
            throw new CliException("No URLs to fetch (--file was empty).");
        }

        if (!quiet)
        {
            Console.Error.WriteLine(
                $"Fetching {total} URLs with {concurrency} concurrent request(s) (per-fetch timeout: {timeoutSecs}s)...");
        }

        var start = Stopwatch.StartNew();
        using var semaphore = new SemaphoreSlim(concurrency, concurrency);
        var tasks = new Task<JsonObject>[total];

        for (var i = 0; i < total; i++)
        {
            var url = urls[i];
            tasks[i] = Task.Run(async () =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                var taskStart = Stopwatch.StartNew();
                try
                {
                    var response = await FetchOriginalResponseAsync(
                        url, proxy, userAgent, (ulong)timeoutSecs, stealth).ConfigureAwait(false);
                    return new JsonObject
                    {
                        ["url"] = JsonValue.Create(url),
                        ["ok"] = JsonValue.Create(response.Status is >= 200 and < 400),
                        ["status"] = JsonValue.Create(response.Status),
                        ["content_type"] = JsonValue.Create(
                            response.Headers.TryGetValue("content-type", out var contentType)
                                ? contentType
                                : string.Empty),
                        ["bytes"] = JsonValue.Create(response.Body.Length),
                        ["elapsed_ms"] = JsonValue.Create((long)taskStart.Elapsed.TotalMilliseconds),
                    };
                }
                catch (Exception error) when (error is CliException or ObscuraNetException)
                {
                    return new JsonObject
                    {
                        ["url"] = JsonValue.Create(url),
                        ["ok"] = JsonValue.Create(false),
                        ["error"] = JsonValue.Create(error.Message),
                        ["elapsed_ms"] = JsonValue.Create((long)taskStart.Elapsed.TotalMilliseconds),
                    };
                }
                finally
                {
                    semaphore.Release();
                }
            });
        }

        var failures = 0;
        var sb = new StringBuilder();
        foreach (var task in tasks)
        {
            var line = await task.ConfigureAwait(false);
            if (line["ok"]?.GetValueKind() != JsonValueKind.True)
            {
                failures++;
            }
            sb.Append(SerdeJson.ToJson(line));
            sb.Append('\n');
        }

        var text = sb.ToString();
        if (output is { } path)
        {
            try
            {
                await File.WriteAllTextAsync(path, text).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new CliException($"Failed to write {path}: {error.Message}");
            }
        }
        else
        {
            await using var stdout = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(text);
            await stdout.WriteAsync(bytes).ConfigureAwait(false);
            await stdout.FlushAsync().ConfigureAwait(false);
        }

        if (!quiet)
        {
            var elapsed = start.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
            Console.Error.WriteLine(
                $"Done: {total} URLs in {elapsed}s ({total - failures} ok, {failures} failed).");
        }
    }

    /// <summary>Port of <c>write_or_print</c>: a file, or stdout with a trailing newline.</summary>
    public static async Task WriteOrPrintAsync(string content, string? output)
    {
        if (output is { } path)
        {
            try
            {
                await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new CliException($"Failed to write {path}: {error.Message}");
            }
            return;
        }
        Console.Out.Write(content);
        Console.Out.Write('\n');
        Console.Out.Flush();
    }

    /// <summary>
    /// Port of <c>write_or_print_bytes</c>: raw bytes, never a println, so a
    /// binary payload keeps its exact length.
    /// </summary>
    public static async Task WriteOrPrintBytesAsync(byte[] bytes, string? output)
    {
        if (output is { } path)
        {
            try
            {
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new CliException($"Failed to write {path}: {error.Message}");
            }
            return;
        }
        await using var stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync(bytes).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Poll for a selector, driving the event loop between polls.
    /// </summary>
    /// <remarks>
    /// The selector may be created by a timer, dynamic import, or fetch
    /// completion. Sleeping without pumping V8 makes those callbacks unable to
    /// run, so a valid selector wait would always time out. One bounded
    /// event-loop slice runs per poll, then a 100ms cadence is retained if the
    /// slice returned idle immediately.
    /// </remarks>
    public static async Task<bool> WaitForSelectorAsync(Page page, string selector, ulong timeoutSecs)
    {
        var deadline = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(timeoutSecs);
        while (true)
        {
            var found = page.WithDom(dom =>
                dom.TryQuerySelector(selector, out var node, out _) && node is not null);
            if (found)
            {
                return true;
            }
            if (deadline.Elapsed >= budget)
            {
                return false;
            }

            var sliceStarted = Stopwatch.StartNew();
            await page.SettleAsync(100).ConfigureAwait(false);
            var spent = sliceStarted.Elapsed;
            var cadence = TimeSpan.FromMilliseconds(100);
            if (spent < cadence)
            {
                await Task.Delay(cadence - spent).ConfigureAwait(false);
            }
        }
    }
}
