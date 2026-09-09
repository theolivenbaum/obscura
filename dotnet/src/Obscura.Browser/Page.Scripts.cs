using System.Diagnostics;
using System.Globalization;
using Obscura.Dom;
using Obscura.Js.Runtime;
using Obscura.Js.Url;
using Obscura.Net;

namespace Obscura.Browser;

public sealed partial class Page
{
    private enum ScriptKind
    {
        Classic,
        Module,
        ImportMap,
    }

    private sealed class ScriptInfo
    {
        internal required string? Src { get; init; }

        internal required string Inline { get; init; }

        internal required bool IsDefer { get; init; }

        internal required bool IsAsync { get; init; }

        internal required ScriptKind Kind { get; init; }

        internal required uint Nid { get; init; }

        /// <summary>Document base URL at this element's parser encounter point.</summary>
        internal required string BaseUrl { get; init; }
    }

    private abstract record ScheduledScript
    {
        private ScheduledScript()
        {
        }

        internal sealed record Classic(int Index) : ScheduledScript;

        internal sealed record Module(
            PreparedModule Prepared,
            string? Url,
            ulong RemainingActiveMs,
            ulong GraphElapsedMs,
            long QueuedAt) : ScheduledScript;
    }

    internal Task ExecuteScriptsAsync(CancellationToken cancellationToken) =>
        ExecuteScriptsWithModuleBudgetAsync(null, cancellationToken);

    /// <summary>
    /// Drive only dynamic script elements which participate in the current
    /// document's load-event delay set.
    /// </summary>
    /// <remarks>
    /// Browser script runners keep this set separate from arbitrary post-load
    /// imports, timers and enhancement scripts; navigation readiness must not turn
    /// those into an implicit multi-second settle.
    /// </remarks>
    internal static async Task<bool> DriveLoadDelayingScriptsAsync(
        ObscuraJsRuntime js,
        DateTime deadlineUtc)
    {
        while (js.HasPendingLoadDelayingScripts())
        {
            TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            ulong pollBudget = (ulong)Math.Min(remaining.TotalMilliseconds, 25.0);
            if (pollBudget == 0)
            {
                return false;
            }
            try
            {
                await js.RunEventLoopBoundedAsync(pollBudget).ConfigureAwait(false);
                if (js.HasPendingLoadDelayingScripts())
                {
                    await Task.Yield();
                }
            }
            catch (JsRuntimeException error)
            {
                if (ObscuraJsRuntime.IsFatalEventLoopError(error.Message))
                {
                    return false;
                }
                // A load-delaying script threw or left an unhandled rejection. Chrome
                // reports the error and runs the rest; killing the pump would strand
                // every still-pending script. The absolute deadline above bounds a
                // page that errors on every turn.
                await Task.Yield();
            }
        }
        return true;
    }

    internal async Task ExecuteScriptsWithModuleBudgetAsync(
        ulong? moduleBudgetOverride,
        CancellationToken cancellationToken)
    {
        // Soft deadline on the entire script-execution phase. Heavy SPAs ship 50+
        // scripts and a serial fetch + execute loop can blow past a
        // Puppeteer/Playwright goto timeout. Only pages that actually run past the
        // deadline are affected; fast pages finish well before it. 30s gives an app
        // room to initialize while the per-phase watchdog (armed at this + 1s) still
        // bounds a real synchronous hang.
        ulong scriptDeadlineMs = PageHelpers.EnvUlong("OBSCURA_SCRIPT_DEADLINE_MS", 30_000);
        DateTime scriptDeadline = DateTime.UtcNow.AddMilliseconds(scriptDeadlineMs);

        // Hard backstop over the WHOLE script-execution phase. Inline scripts run
        // back-to-back with no await between them, so neither the soft deadline (only
        // checked between scripts) nor the per-script guard can interrupt a page that
        // burns the budget across many synchronous scripts. This watchdog terminates
        // the isolate if cumulative synchronous script work overruns.
        WatchdogToken? execWatchdog = Js?.ArmWatchdog(TimeSpan.FromMilliseconds(scriptDeadlineMs + 1000));

        List<ScriptInfo> allScripts;
        if (Js is { } discoveryRuntime)
        {
            string documentUrl = UrlString();
            allScripts = discoveryRuntime.WithDom(dom => DiscoverScripts(dom, documentUrl)) ?? [];
        }
        else
        {
            return;
        }

        // HTML scripts have an "already started" flag. Mark every parser-discovered
        // script before running page code so React/Next hydration can move or hoist
        // those nodes without appendChild executing them a second time.
        if (Js is { } marker)
        {
            string ids = string.Join(
                ',',
                allScripts.Select(script => script.Nid.ToString(CultureInfo.InvariantCulture)));
            TryExecute(marker, "<parser-scripts>", $"globalThis.__markParserScripts([{ids}]);");
        }

        List<(int Index, string Url)> fetchTasks = [];
        for (int i = 0; i < allScripts.Count; i++)
        {
            ScriptInfo script = allScripts[i];
            if (script.Kind != ScriptKind.Classic || script.Src is not { } srcUrl)
            {
                continue;
            }
            string fullUrl =
                srcUrl.StartsWith("http://", StringComparison.Ordinal)
                || srcUrl.StartsWith("https://", StringComparison.Ordinal)
                    ? srcUrl
                    : PageUrl.TryParse(script.BaseUrl) is { } scriptBase
                        && PageUrl.TryJoin(scriptBase, srcUrl) is { } joined
                        ? joined.Href
                        : srcUrl;

            if (!PageHelpers.SubresourceAllowed(Url, fullUrl))
            {
                // Block file://, data:, javascript: and other off-origin schemes from
                // being injected as a <script src>. Without this an http page can
                // include <script src="file:///etc/passwd"> and see the body parsed
                // as JS source.
                continue;
            }
            if (ShouldBlockUrl(fullUrl))
            {
                continue;
            }
            fetchTasks.Add((i, fullUrl));
        }

        UrlRecord scriptInitiator = Url ?? PageUrl.TryParse("about:blank")!;
        var fetchFactories =
            new List<Func<Task<(int Index, string Url, Response Response)?>>>(fetchTasks.Count);
        foreach ((int index, string url) in fetchTasks)
        {
            fetchFactories.Add(async () =>
            {
                UrlRecord parsed = PageUrl.TryParse(url) ?? PageUrl.TryParse("about:blank")!;
                if (string.Equals(parsed.Scheme, "data", StringComparison.Ordinal))
                {
                    // data: URIs are inline; decode locally, no network fetch.
                    // Instagram and other Meta properties serve their bootstrap as
                    // <script src="data:application/x-javascript;base64,...">.
                    byte[] body = PageHelpers.DecodeDataUri(url) ?? [];
                    string meta = url["data:".Length..];
                    int comma = meta.IndexOf(',', StringComparison.Ordinal);
                    string head = comma < 0 ? meta : meta[..comma];
                    int semi = head.IndexOf(';', StringComparison.Ordinal);
                    string contentType = (semi < 0 ? head : head[..semi]) is { Length: > 0 } value
                        ? value
                        : "application/javascript";
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["content-type"] = contentType,
                    };
                    return (index, url, new Response
                    {
                        Url = NetUrl.From(parsed),
                        Status = 200,
                        Headers = headers,
                        Body = body,
                        RedirectedFrom = [],
                    });
                }

                ResourceRequest request =
                    ResourceRequest.Subresource(ResourceType.Script, NetUrl.From(scriptInitiator));
                try
                {
                    Response response = await HttpClient
                        .FetchResourceWithCallbacksAsync(NetUrl.From(parsed), request, _callbacks, cancellationToken)
                        .ConfigureAwait(false);
                    return (index, url, response);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return null;
                }
            });
        }

        // Bound concurrency: a page with 100 external scripts would otherwise open
        // 100 sockets at once, exhausting the connection pool and ephemeral ports.
        Dictionary<int, (string Url, string Code, Response Response)> fetched = [];
        List<(int Index, string Url, Response Response)?> fetchResults = [];
        using (var fetchDeadline = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(Math.Max(0.0, (scriptDeadline - DateTime.UtcNow).TotalMilliseconds))))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
            fetchDeadline.Token, cancellationToken))
        {
            try
            {
                fetchResults = await Buffered
                    .AllAsync(fetchFactories, 16, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                fetchResults = [];
            }
        }

        foreach ((int Index, string Url, Response Response)? result in fetchResults)
        {
            if (result is not { } value)
            {
                continue;
            }
            if (!PageHelpers.ScriptResponseIsExecutable(value.Response.Status))
            {
                RecordNetworkEventWithBody(
                    value.Url,
                    "GET",
                    "Script",
                    value.Response.Status,
                    value.Response.Headers,
                    value.Response.Body,
                    base64Encoded: false);
                continue;
            }
            // Script bodies: only the HTTP Content-Type charset matters (no in-band
            // meta-charset for JS).
            string code = ContentEncoding.DecodeNonHtml(value.Response.Body, value.Response.ContentType());
            fetched[value.Index] = (value.Url, code, value.Response);
        }

        // Spec: readyState is "loading" while parser-discovered scripts execute.
        // Scripts that check readyState === 'loading' register DOMContentLoaded
        // listeners instead of calling their callback immediately.
        if (Js is { } readyState)
        {
            TryExecute(readyState, "<ready-state>", "globalThis.__documentReadyState__ = 'loading';");
        }

        // CDP `Page.addScriptToEvaluateOnNewDocument` contract: preload sources must
        // run BEFORE any of the page's own scripts. This is also where puppeteer's
        // `exposeFunction` wrapper installs itself - if preload runs after page
        // scripts, every early binding call hits an undefined function.
        List<string> preloadSources = [.. _preloadScripts];
        if (Js is { } preloadRuntime)
        {
            foreach (string source in preloadSources)
            {
                preloadRuntime.ExecuteScriptGuarded("<preload>", source);
            }
        }

        // Per-module budget. Modules on an already-rendered page are enhancement, not
        // the app: give them a short budget so one slow non-essential module cannot
        // block navigation completion. A page whose body is still an empty shell IS
        // the SPA, so give it the full script budget and the app module still mounts.
        int bodyNodes = Js?.WithDom(dom =>
            dom.TryQuerySelector("body", out NodeId? body, out _) && body is { } id
                ? dom.Descendants(id).Count
                : 0) ?? 0;
        ulong shortMs = moduleBudgetOverride ?? PageHelpers.EnvUlong("OBSCURA_MODULE_BUDGET_MS", 3_000);
        // A rendered body has hundreds of descendants; an unmounted Vite/Next shell
        // is <root> plus maybe a spinner.
        ulong moduleBudgetMs = moduleBudgetOverride is not null || bodyNodes > 50
            ? shortMs
            : scriptDeadlineMs;
        // V8 can flag an overrun while a synchronous renderer host call is in
        // progress, but it cannot preempt the host after entering that call. Allow
        // one bounded, finite style/layout flush without weakening the page-wide
        // script deadline. Private test overrides keep zero grace.
        ulong moduleHostcallGraceMs = moduleBudgetOverride is not null
            ? 0
            : PageHelpers.EnvUlong("OBSCURA_MODULE_HOSTCALL_GRACE_MS", 5_000);

        List<ScheduledScript> postParse = [];

        // Process parser-discovered scripts in encounter order. Import maps register
        // at their exact position; module graphs start there too, but evaluation of
        // non-async modules remains post-parse.
        for (int index = 0; index < allScripts.Count; index++)
        {
            if (DateTime.UtcNow >= scriptDeadline)
            {
                break;
            }
            ScriptInfo script = allScripts[index];
            switch (script.Kind)
            {
                case ScriptKind.ImportMap:
                    if (script.Src is not null)
                    {
                        continue;
                    }
                    if (Js is { } importMapRuntime)
                    {
                        try
                        {
                            importMapRuntime.AddImportMap(script.Inline, script.BaseUrl);
                        }
                        catch (JsRuntimeException)
                        {
                            // Ignore an invalid import map, as Chromium does.
                        }
                    }
                    break;

                case ScriptKind.Classic:
                    if (script.IsDefer && !script.IsAsync && script.Src is not null)
                    {
                        postParse.Add(new ScheduledScript.Classic(index));
                    }
                    else
                    {
                        ExecuteClassic(script, Take(fetched, index));
                    }
                    break;

                case ScriptKind.Module:
                {
                    // Graph loading and evaluation share one active-work allowance.
                    // Queue time behind other post-parse scripts is not work
                    // performed by this module.
                    ulong? remainingPageMs = RemainingBudgetMs(scriptDeadline);
                    if (remainingPageMs is not { } pageMs)
                    {
                        continue;
                    }
                    ulong prepareBudgetMs = Math.Min(moduleBudgetMs, pageMs);
                    long prepareStarted = Stopwatch.GetTimestamp();
                    PreparedModule? prepared;
                    string? moduleUrl;
                    if (script.Src is { } src)
                    {
                        string fullUrl =
                            src.StartsWith("http://", StringComparison.Ordinal)
                            || src.StartsWith("https://", StringComparison.Ordinal)
                            || src.StartsWith("data:", StringComparison.Ordinal)
                                ? src
                                : PageUrl.TryParse(script.BaseUrl) is { } moduleBase
                                    && PageUrl.TryJoin(moduleBase, src) is { } joined
                                    ? joined.Href
                                    : src;
                        if (Js is not { } moduleRuntime)
                        {
                            continue;
                        }
                        try
                        {
                            prepared = await moduleRuntime
                                .PrepareModuleAsync(fullUrl, prepareBudgetMs)
                                .ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            continue;
                        }
                        moduleUrl = fullUrl;
                    }
                    else
                    {
                        if (Js is not { } inlineRuntime)
                        {
                            continue;
                        }
                        try
                        {
                            prepared = await inlineRuntime
                                .PrepareInlineModuleAsync(script.Inline, script.BaseUrl, prepareBudgetMs)
                                .ConfigureAwait(false);
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            continue;
                        }
                        moduleUrl = null;
                    }

                    ulong graphElapsedMs = ElapsedMsCeil(prepareStarted);
                    ulong remainingActiveMs = moduleBudgetMs > graphElapsedMs
                        ? moduleBudgetMs - graphElapsedMs
                        : 0;
                    if (remainingActiveMs == 0)
                    {
                        continue;
                    }
                    var scheduled = new ScheduledScript.Module(
                        prepared!,
                        moduleUrl,
                        remainingActiveMs,
                        graphElapsedMs,
                        Stopwatch.GetTimestamp());
                    if (script.IsAsync)
                    {
                        await EvaluateModuleAsync(
                            scheduled,
                            scriptDeadline,
                            moduleHostcallGraceMs).ConfigureAwait(false);
                    }
                    else
                    {
                        postParse.Add(scheduled);
                    }
                    break;
                }

                default:
                    break;
            }
        }

        // Parsing has finished before defer scripts and non-async modules run. They
        // still gate DOMContentLoaded, but observe the browser's `interactive`
        // readyState while they execute.
        if (Js is { } interactive)
        {
            TryExecute(
                interactive,
                "<ready-state-interactive>",
                "globalThis.__documentReadyState__ = 'interactive';");
        }

        foreach (ScheduledScript scheduled in postParse)
        {
            if (DateTime.UtcNow >= scriptDeadline)
            {
                break;
            }
            switch (scheduled)
            {
                case ScheduledScript.Classic classic:
                    ExecuteClassic(allScripts[classic.Index], Take(fetched, classic.Index));
                    break;
                case ScheduledScript.Module module:
                    await EvaluateModuleAsync(module, scriptDeadline, moduleHostcallGraceMs)
                        .ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }

        if (Js is { } lifecycle)
        {
            // DOMContentLoaded follows parser/defer/module work, but async dynamic
            // script elements do not gate it. They do remain in the document's
            // load-event delay set, including scripts inserted by a DOMContentLoaded
            // listener.
            TryExecute(
                lifecycle,
                "<dom-content-loaded>",
                "try { document.dispatchEvent(new Event('DOMContentLoaded', {bubbles:false,cancelable:false})); } catch(e) {}\n"
                + "try { window.dispatchEvent(new Event('DOMContentLoaded', {bubbles:false,cancelable:false})); } catch(e) {}");

            await DriveLoadDelayingScriptsAsync(lifecycle, scriptDeadline).ConfigureAwait(false);

            // readyState becomes complete before the load event. A script inserted by
            // an onload handler is therefore post-load work and remains pending until
            // an explicit caller settle/wait.
            TryExecute(
                lifecycle,
                "<load-event>",
                "globalThis.__documentReadyState__ = 'complete';\n"
                + "try {\n"
                + "  const loadEvent = new Event('load', {bubbles:false,cancelable:false});\n"
                + "  if (typeof window.onload === 'function') {\n"
                + "    try { window.onload.call(window, loadEvent); } catch(e) {}\n"
                + "  }\n"
                + "  try { window.dispatchEvent(loadEvent); } catch(e) {}\n"
                + "} catch(e) {}");
        }

        if (execWatchdog is { } token)
        {
            Js?.DisarmWatchdog(token);
        }

        static (string Url, string Code, Response Response)? Take(
            Dictionary<int, (string Url, string Code, Response Response)> map,
            int index)
        {
            if (map.Remove(index, out (string Url, string Code, Response Response) value))
            {
                return value;
            }
            return null;
        }
    }

    private static ulong? RemainingBudgetMs(DateTime deadlineUtc)
    {
        TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }
        return (ulong)Math.Max(1.0, Math.Ceiling(remaining.TotalMilliseconds));
    }

    private static ulong ElapsedMsCeil(long startTimestamp)
    {
        double micros = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency;
        return (ulong)Math.Max(1.0, Math.Ceiling(micros / 1000.0));
    }

    private async Task EvaluateModuleAsync(
        ScheduledScript.Module module,
        DateTime scriptDeadline,
        ulong moduleHostcallGraceMs)
    {
        ulong? remainingPageMs = RemainingBudgetMs(scriptDeadline);
        if (remainingPageMs is not { } pageMs)
        {
            return;
        }
        ulong budget = Math.Min(module.RemainingActiveMs + moduleHostcallGraceMs, pageMs);
        if (budget == 0 || Js is not { } js)
        {
            return;
        }
        try
        {
            await js.EvaluatePreparedModuleAsync(module.Prepared, budget).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return;
        }
        if (module.Url is { } url)
        {
            RecordNetworkEvent(url, "GET", "Script", 200, NoHeaders, 0);
        }
    }

    private void ExecuteClassic(ScriptInfo script, (string Url, string Code, Response Response)? fetchedScript)
    {
        if (script.Src is not null)
        {
            if (fetchedScript is not { } value || Js is not { } js)
            {
                return;
            }
            string executionUrl = NetUrl.To(value.Response.Url).Href;
            RecordNetworkEventWithBody(
                value.Url,
                "GET",
                "Script",
                value.Response.Status,
                value.Response.Headers,
                value.Response.Body,
                base64Encoded: false);
            TryExecute(
                js,
                "<current-script>",
                $"globalThis.__currentScriptNid={script.Nid.ToString(CultureInfo.InvariantCulture)};");
            // A page script that throws is a page problem, not a navigation failure:
            // the reference logs `Script error (url): ...` and runs the next script.
            // Letting it escape here failed the whole navigation (and therefore
            // Page.navigate over CDP) for any page with one uncaught error.
            try
            {
                js.ExecuteScriptGuarded(executionUrl, value.Code);
            }
            catch (JsRuntimeException)
            {
                // Reported to the page through the runtime's uncaught-exception queue.
            }

            TryExecute(js, "<current-script>", "globalThis.__currentScriptNid=0;");
        }
        else if (script.Inline.Length != 0 && Js is { } inlineJs)
        {
            TryExecute(
                inlineJs,
                "<current-script>",
                $"globalThis.__currentScriptNid={script.Nid.ToString(CultureInfo.InvariantCulture)};");
            try
            {
                inlineJs.ExecuteScriptGuarded(script.BaseUrl, script.Inline);
            }
            catch (JsRuntimeException)
            {
                // Same as the external-script arm: an inline script's uncaught error
                // must not abort the navigation.
            }

            TryExecute(inlineJs, "<current-script>", "globalThis.__currentScriptNid=0;");
        }
    }

    private static List<ScriptInfo> DiscoverScripts(DomTree dom, string documentUrl)
    {
        List<NodeId> scriptIds = dom.TryQuerySelectorAll("script", out List<NodeId> found, out _) ? found : [];
        Dictionary<uint, string> basesAtScript = [];
        UrlRecord? activeBase = PageUrl.TryParse(documentUrl);
        bool foundBase = false;
        foreach (NodeId nid in dom.Descendants(dom.Document))
        {
            Node? node = dom.GetNode(nid);
            if (node?.AsElement() is not { } element)
            {
                continue;
            }
            if (string.Equals(element.Name.Local, "base", StringComparison.Ordinal) && !foundBase)
            {
                if (node.GetAttribute("href") is { } href)
                {
                    foundBase = true;
                    if (activeBase is { } current && PageUrl.TryJoin(current, href) is { } resolved)
                    {
                        activeBase = resolved;
                    }
                }
            }
            else if (string.Equals(element.Name.Local, "script", StringComparison.Ordinal))
            {
                basesAtScript[nid.Raw] = activeBase?.Href ?? documentUrl;
            }
        }

        List<ScriptInfo> scripts = [];
        foreach (NodeId sid in scriptIds)
        {
            Node? node = dom.GetNode(sid);
            if (node is null)
            {
                continue;
            }
            string? src = node.GetAttribute("src");
            string scriptType = (node.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
            bool isDefer = node.GetAttribute("defer") is not null;
            bool isAsync = node.GetAttribute("async") is not null;
            ScriptKind kind;
            switch (scriptType)
            {
                case "module":
                    kind = ScriptKind.Module;
                    break;
                case "importmap":
                    kind = ScriptKind.ImportMap;
                    break;
                case "":
                case "text/javascript":
                case "application/javascript":
                    kind = ScriptKind.Classic;
                    break;
                default:
                    continue;
            }

            string inlineCode = src is null ? dom.TextContent(sid) : string.Empty;
            if (kind == ScriptKind.ImportMap || src is not null || inlineCode.Trim().Length != 0)
            {
                scripts.Add(new ScriptInfo
                {
                    Src = src,
                    Inline = inlineCode,
                    IsDefer = isDefer,
                    IsAsync = isAsync,
                    Kind = kind,
                    Nid = sid.Raw,
                    BaseUrl = basesAtScript.TryGetValue(sid.Raw, out string? baseUrl) ? baseUrl : documentUrl,
                });
            }
        }
        return scripts;
    }
}
