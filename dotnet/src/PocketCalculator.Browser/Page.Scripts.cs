using System.Diagnostics;
using System.Globalization;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;

namespace PocketCalculator.Browser;

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

        /// <summary>The element's valid <c>referrerpolicy</c>, or null (port addition).</summary>
        internal ReferrerPolicy? ReferrerPolicy { get; init; }
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
        PocketCalculatorJsRuntime js,
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
                if (PocketCalculatorJsRuntime.IsFatalEventLoopError(error.Message))
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

    /// <summary>
    /// Run the scripts of the document already in place (parsed before), with
    /// <paramref name="moduleBudgetOverride"/> as every module's budget when given. A
    /// navigation runs its scripts while parsing instead (<see cref="LoadDocumentAsync"/>).
    /// </summary>
    internal Task ExecuteScriptsWithModuleBudgetAsync(
        ulong? moduleBudgetOverride,
        CancellationToken cancellationToken) =>
        RunPreparsedDocumentScriptsAsync(moduleBudgetOverride, cancellationToken);

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
        ulong moduleHostcallGraceMs,
        CancellationToken cancellationToken)
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A module that fails, or overruns its budget, is the page's problem and the
            // next script still runs. Only the navigation's own cancellation ends the phase:
            // ClearScript's ScriptInterruptedException is an OperationCanceledException, and
            // letting a module watchdog's interrupt through as one failed the navigation
            // (reddit.com's challenge follow-up, whose components overran a module budget).
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
            js.SetCurrentScriptNid(script.Nid);
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

            js.SetCurrentScriptNid(0);
        }
        else if (script.Inline.Length != 0 && Js is { } inlineJs)
        {
            inlineJs.SetCurrentScriptNid(script.Nid);
            try
            {
                inlineJs.ExecuteScriptGuarded(script.BaseUrl, script.Inline);
            }
            catch (JsRuntimeException)
            {
                // Same as the external-script arm: an inline script's uncaught error
                // must not abort the navigation.
            }

            inlineJs.SetCurrentScriptNid(0);
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
                    ReferrerPolicy = ReferrerPolicies.ParseAttribute(node.GetAttribute("referrerpolicy")),
                });
            }
        }
        return scripts;
    }

    /// <summary>Disarms a page watchdog once, on whichever path leaves its scope first.</summary>
    private sealed class WatchdogDisarm(PocketCalculator.Js.Runtime.PocketCalculatorJsRuntime? runtime, WatchdogToken? token)
        : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (token is not null && runtime is not null && Interlocked.Exchange(ref _done, 1) == 0)
            {
                runtime.DisarmWatchdog(token);
            }
        }
    }
}
