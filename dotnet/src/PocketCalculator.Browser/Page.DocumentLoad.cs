using System.Collections.Concurrent;
using System.Diagnostics;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;

namespace PocketCalculator.Browser;

/// <summary>
/// Loading a document with its scripts running while it is parsed.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from crates/obscura-browser (page.rs), which parses the whole document, fetches
/// every script, and then runs them back to back: an inline script saw every element after it,
/// async scripts ran in document order before the deferred ones with no task between them,
/// and a script inserted by a parser script could only run once every parser and deferred
/// script had (grammarly.com: Transcend's consent UI then ran after Next.js hydration, which
/// saw its extra script element and fell back to a client render, React errors #418/#423).
/// </para>
/// <para>
/// This follows the HTML parser's script handling, as Chromium 141 does it: the parser stops
/// at each script end tag; an inline script runs there, once the style sheets that block
/// scripts have loaded; an external parser-blocking script holds the parser until it has
/// loaded, while the event loop runs (timers, async scripts, scripts other scripts inserted);
/// async scripts run as soon as they have loaded; deferred classic and module scripts run in
/// document order after parsing, then DOMContentLoaded; the load event waits for the async and
/// load-delaying scripts. document.write inserts at the insertion point
/// (<see cref="DocumentParser"/>). The parser also yields to the event loop every few
/// milliseconds, as Chromium's does.
/// </para>
/// <para>
/// Not modelled: a speculative parser that fetches past a blocked script (a preload scan of
/// the whole response starts the external classic scripts' fetches instead); module graphs
/// load when their turn comes, not while parsing; custom elements are upgraded right after
/// the parser inserts them instead of being constructed by it.
/// </para>
/// </remarks>
public sealed partial class Page
{
    /// <summary>
    /// Tokens the parser processes before it lets page tasks run: Blink's
    /// <c>HTMLParserScheduler</c> chunk size.
    /// </summary>
    private const int ParserTokenBudget = 4096;

    /// <summary>How long the parser runs, across script stops, before it lets page tasks run.</summary>
    private const double ParserYieldMs = 50;

    /// <summary>Parser-inserted external classic scripts fetched at once, at most.</summary>
    private const int MaxConcurrentScriptFetches = 16;

    /// <summary>The fetch of one external classic script.</summary>
    private sealed record ScriptFetch(string Url, Response Response, double StartedAt, double EndedAt);

    /// <summary>Script fetches of the document being loaded, by URL and referrer policy.</summary>
    private readonly ConcurrentDictionary<string, Task<ScriptFetch?>> _scriptFetches = new(StringComparer.Ordinal);

    private SemaphoreSlim _scriptFetchSlots = new(MaxConcurrentScriptFetches);

    /// <summary>
    /// Parse <paramref name="bodyText"/> as the new document, running its scripts as the
    /// parse reaches them, through the load event. Initializes the realm (<see cref="InitJs"/>)
    /// once the parser reaches the first script, or the end of the document.
    /// </summary>
    private async Task LoadDocumentAsync(string bodyText, CancellationToken cancellationToken)
    {
        _scriptFetches.Clear();
        _scriptFetchSlots = new SemaphoreSlim(MaxConcurrentScriptFetches);

        var traceClock = Stopwatch.StartNew();
        void Trace(string what) { if (Environment.GetEnvironmentVariable("PC_TRACE") == "1") Console.Error.WriteLine($"[load] {traceClock.Elapsed.TotalMilliseconds:F1} {what}"); }
        var dom = new DomTree();
        var parser = DocumentParser.Begin(dom, bodyText);
        StartSpeculativeScriptFetches(bodyText, cancellationToken);

        // Up to the first script: nothing can observe the document before it runs.
        ParserStop first = parser.Run(0, out NodeId firstScript);
        List<NodeId> firstNodes = parser.TakeInsertedNodes();
        Title = dom.TryQuerySelector("title", out NodeId? titleId, out _) && titleId is { } id
            ? dom.TextContent(id)
            : string.Empty;

        Trace("first chunk");
        Dom = dom;
        InitJs();
        Trace("initjs");
        // Committed: the response is in and the document exists. A deadline from here on
        // leaves the page as it stands instead of failing it (RunWithNavigationDeadlineAsync).
        ReachReadiness(DocumentReadiness.Committed);
        if (Js is not { } js)
        {
            parser.Abort();
            return;
        }

        // The document is loading from here until the script phase completes it, so a
        // page the deadline stops before then reads as Chromium's does, not as complete.
        js.SetDocumentReadyState("loading");
        _documentTimelineOrigin = Stopwatch.GetTimestamp();
        js.ResetAnimationTimeline();

        // CDP `Page.addScriptToEvaluateOnNewDocument` contract: preload sources run before
        // any of the page's own scripts.
        foreach (string source in _preloadScripts.ToList())
        {
            js.ExecutePreloadScript(source);
        }

        Trace("preloads");
        var runner = new DocumentScriptRunner(this, js, parser, moduleBudgetOverride: null, cancellationToken);
        js.State.ParserWriter = runner;
        try
        {
            // Page init registered what the first chunk holds; the runner still owes it its
            // scripts and style sheets.
            runner.AdoptInitialNodes(firstNodes);
            await runner.RunAsync(first == ParserStop.Script ? firstScript : null).ConfigureAwait(false);
        }
        finally
        {
            js.State.ParserWriter = null;
            runner.FinishParsingWithoutScripts();
            Title = js.WithDom(d => d.TryQuerySelector("title", out NodeId? t, out _) && t is { } tid
                ? d.TextContent(tid)
                : null) ?? Title;
        }
    }

    /// <summary>
    /// Run the scripts of a document that was parsed before (a test fixture, or a document
    /// installed without a navigation), in the order a parser would have reached them.
    /// </summary>
    private async Task RunPreparsedDocumentScriptsAsync(ulong? moduleBudgetOverride, CancellationToken cancellationToken)
    {
        if (Js is not { } js)
        {
            return;
        }

        _scriptFetches.Clear();
        _scriptFetchSlots = new SemaphoreSlim(MaxConcurrentScriptFetches);
        js.SetDocumentReadyState("loading");
        foreach (string source in _preloadScripts.ToList())
        {
            js.ExecutePreloadScript(source);
        }

        var runner = new DocumentScriptRunner(this, js, parser: null, moduleBudgetOverride, cancellationToken);
        await runner.RunAsync(null).ConfigureAwait(false);
    }

    /// <summary>
    /// The preload scanner: start fetching the response's external classic scripts now, so a
    /// parser-blocking script later in the document is not fetched only once the parser
    /// reaches it.
    /// </summary>
    private void StartSpeculativeScriptFetches(string html, CancellationToken cancellationToken)
    {
        if (Url is not { } documentUrl)
        {
            return;
        }

        PreloadScan scan = PreloadScanner.Scan(html);
        UrlRecord baseUrl = scan.BaseHref is { } href && PageUrl.TryJoin(documentUrl, href) is { } joined
            ? joined
            : documentUrl;
        ReferrerPolicy documentPolicy = ReferrerPolicyHeader ?? ReferrerPolicies.Default;
        foreach (PreloadScript script in scan.Scripts)
        {
            if (ResolveScriptUrl(baseUrl.Href, script.Src) is not { } url)
            {
                continue;
            }

            ReferrerPolicy policy = ReferrerPolicies.ParseAttribute(script.ReferrerPolicy)
                ?? (script.MetaReferrer is { } meta ? ReferrerPolicies.ParseMeta(meta) : null)
                ?? documentPolicy;
            _ = StartScriptFetch(url, policy, cancellationToken);
        }
    }

    /// <summary>The absolute URL a script's src names, or null when it may not be fetched.</summary>
    private string? ResolveScriptUrl(string baseUrl, string src)
    {
        string fullUrl =
            src.StartsWith("http://", StringComparison.Ordinal)
            || src.StartsWith("https://", StringComparison.Ordinal)
                ? src
                : PageUrl.TryParse(baseUrl) is { } scriptBase
                    && PageUrl.TryJoin(scriptBase, src) is { } joined
                    ? joined.Href
                    : src;

        // Block file://, javascript: and other off-origin schemes from being injected as a
        // <script src>: an http page could otherwise read a local file as script source.
        if (!PageHelpers.SubresourceAllowed(Url, fullUrl) || ShouldBlockUrl(fullUrl))
        {
            return null;
        }

        return fullUrl;
    }

    /// <summary>The fetch of <paramref name="url"/>, shared by every script that names it.</summary>
    private Task<ScriptFetch?> StartScriptFetch(string url, ReferrerPolicy policy, CancellationToken cancellationToken)
    {
        string key = policy + "\n" + url;
        return _scriptFetches.GetOrAdd(key, _ => FetchScriptAsync(url, policy, cancellationToken));
    }

    private async Task<ScriptFetch?> FetchScriptAsync(string url, ReferrerPolicy policy, CancellationToken cancellationToken)
    {
        UrlRecord parsed = PageUrl.TryParse(url) ?? PageUrl.TryParse("about:blank")!;
        double startedAt = PerformanceOps.UnixMilliseconds();
        if (string.Equals(parsed.Scheme, "data", StringComparison.Ordinal))
        {
            // data: URIs are inline; decode locally, no network fetch. Instagram and other
            // Meta properties serve their bootstrap as <script src="data:...;base64,...">.
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
            return new ScriptFetch(url, new Response
            {
                Url = NetUrl.From(parsed),
                Status = 200,
                Headers = headers,
                Body = body,
                RedirectedFrom = [],
            }, startedAt, startedAt);
        }

        UrlRecord initiator = Url ?? PageUrl.TryParse("about:blank")!;
        ResourceRequest request = ResourceRequest.Subresource(ResourceType.Script, NetUrl.From(initiator));
        request.ReferrerPolicy = policy;
        SemaphoreSlim slots = _scriptFetchSlots;
        try
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            Response response = await HttpClient
                .FetchResourceWithCallbacksAsync(NetUrl.From(parsed), request, _callbacks, cancellationToken)
                .ConfigureAwait(false);
            return new ScriptFetch(url, response, startedAt, PerformanceOps.UnixMilliseconds());
        }
        catch (Exception)
        {
            // One script's failure, its own request timeout included, is that script's.
            return null;
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// Describe a script element for preparation, or null when it does not run (a type that
    /// is not script, an empty inline script, a classic script with nomodule).
    /// </summary>
    private static ScriptInfo? DescribeScript(DomTree dom, NodeId sid, string baseUrl)
    {
        Node? node = dom.GetNode(sid);
        if (node is null)
        {
            return null;
        }
        string? src = node.GetAttribute("src");
        string scriptType = (node.GetAttribute("type") ?? string.Empty).Trim().ToLowerInvariant();
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
                return null;
        }

        // HTML "prepare the script element" step 21. DEVIATION from crates/obscura-browser,
        // which ran a nomodule classic script; Chromium 141 does not.
        if (kind == ScriptKind.Classic && node.GetAttribute("nomodule") is not null)
        {
            return null;
        }

        string inlineCode = src is null ? dom.TextContent(sid) : string.Empty;
        if (kind != ScriptKind.ImportMap && src is null && inlineCode.Trim().Length == 0)
        {
            return null;
        }

        return new ScriptInfo
        {
            Src = src,
            Inline = inlineCode,
            IsDefer = node.GetAttribute("defer") is not null,
            IsAsync = node.GetAttribute("async") is not null,
            Kind = kind,
            Nid = sid.Raw,
            BaseUrl = baseUrl,
            ReferrerPolicy = ReferrerPolicies.ParseAttribute(node.GetAttribute("referrerpolicy")),
        };
    }

    /// <summary>One script the runner has prepared and not yet run.</summary>
    private sealed class PreparedScript
    {
        internal required ScriptInfo Info { get; init; }

        /// <summary>An external classic script's fetch; null when it is inline, or blocked.</summary>
        internal Task<ScriptFetch?>? Fetch { get; init; }

        /// <summary>An external classic script whose URL may not be fetched (an error event).</summary>
        internal bool Blocked { get; init; }

        internal bool IsReady => Fetch is null || Fetch.IsCompleted;
    }

    /// <summary>
    /// The scripts of one document load, from the parser's first script through the load event.
    /// </summary>
    private sealed class DocumentScriptRunner : IDocumentWriteTarget
    {
        private readonly Page _page;
        private readonly PocketCalculatorJsRuntime _js;
        private readonly DocumentParser? _parser;
        private readonly CancellationToken _ct;
        private readonly ulong? _moduleBudgetOverride;
        private readonly ulong _scriptDeadlineMs;
        private readonly DateTime _deadline;
        private readonly ulong _moduleHostcallGraceMs;

        /// <summary>Scripts of a document parsed before the runner started, in tree order.</summary>
        private readonly Queue<NodeId>? _preparsed;

        /// <summary>The base URL at each of those scripts' place in the document.</summary>
        private readonly Dictionary<uint, string>? _preparsedBases;

        private PreparedScript? _pendingBlocking;
        private readonly List<PreparedScript> _asyncScripts = [];
        private readonly ConcurrentQueue<PreparedScript> _asyncLoaded = new();
        private readonly List<PreparedScript> _asyncModules = [];
        private readonly List<PreparedScript> _deferred = [];
        private readonly List<Task<List<(AuthorStylesheetTarget Target, string Css, bool OriginClean, string ResponseUrl)>>> _sheetLoads = [];
        private readonly List<Action> _sheetReports = [];
        private readonly List<NodeId> _emptyStyles = [];
        private readonly Stack<uint> _currentScripts = new();
        private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _baseUrl;
        private bool _stopScripts;

        internal DocumentScriptRunner(
            Page page,
            PocketCalculatorJsRuntime js,
            DocumentParser? parser,
            ulong? moduleBudgetOverride,
            CancellationToken cancellationToken)
        {
            _page = page;
            _js = js;
            _parser = parser;
            _ct = cancellationToken;
            _moduleBudgetOverride = moduleBudgetOverride;
            // Soft deadline on the whole script phase, as before: past it no further script
            // starts, and the document completes.
            _scriptDeadlineMs = PageHelpers.EnvUlong("POCKETCALCULATOR_SCRIPT_DEADLINE_MS", 30_000);
            _deadline = DateTime.UtcNow.AddMilliseconds(_scriptDeadlineMs);
            _moduleHostcallGraceMs = moduleBudgetOverride is not null
                ? 0
                : PageHelpers.EnvUlong("POCKETCALCULATOR_MODULE_HOSTCALL_GRACE_MS", 5_000);
            if (parser is null)
            {
                string documentUrl = page.UrlString();
                List<ScriptInfo> discovered = js.WithDom(dom => DiscoverScripts(dom, documentUrl)) ?? [];
                _preparsedBases = [];
                foreach (ScriptInfo info in discovered)
                {
                    _preparsedBases[info.Nid] = info.BaseUrl;
                }

                List<NodeId> scripts = js.WithDom(dom => dom.TryQuerySelectorAll("script", out List<NodeId> found, out _) ? found : []) ?? [];
                _preparsed = new Queue<NodeId>(scripts);
                // HTML scripts have an "already started" flag. Mark every parser-discovered
                // script before running page code so React/Next hydration can move or hoist
                // those nodes without appendChild executing them a second time.
                js.MarkScriptsStarted(scripts);
            }
        }

        // ------------------------------------------------------------ document.write

        public bool HasInsertionPoint => _parser is { HasInsertionPoint: true };

        public bool TryWrite(string text)
        {
            if (_parser is not { HasInsertionPoint: true } parser)
            {
                return false;
            }

            parser.Write(text);
            // With a script waiting to block the parser, the text waits in the input for it.
            if (_pendingBlocking is not null)
            {
                return true;
            }

            while (true)
            {
                ParserStop stop = parser.RunToInsertionPoint(out NodeId script);
                NotifyParserProgress();
                if (stop == ParserStop.CustomElement)
                {
                    continue;
                }

                if (stop != ParserStop.Script)
                {
                    return true;
                }

                Prepare(script);
                if (_pendingBlocking is not null)
                {
                    return true;
                }
            }
        }

        // ------------------------------------------------------------ the parse

        /// <summary>The first chunk, which page init has seen: only its scripts and sheets are owed.</summary>
        internal void AdoptInitialNodes(List<NodeId> nodes) => HandleInserted(nodes, notifyRealm: false);

        internal async Task RunAsync(NodeId? firstScript)
        {
            WatchdogToken execWatchdog = _js.ArmWatchdog(TimeSpan.FromMilliseconds(_scriptDeadlineMs + 1000));
            using WatchdogDisarm execDisarm = new(_js, execWatchdog);
            using IDisposable interruptOnCancel = _js.InterruptOnCancellation(_ct);

            if (firstScript is { } script)
            {
                Prepare(script);
            }

            await ParseAsync().ConfigureAwait(false);
            _page.SpawnPendingRenderResources();

            // The end of the input arrives as a task of its own in Chromium (the network's
            // "finished loading"), so what the last scripts queued runs before readyState
            // becomes interactive (Chromium 141: a 0 ms timer set by the last script fires
            // before readystatechange).
            await YieldToEventLoopAsync().ConfigureAwait(false);

            // The end of parsing: readyState interactive, then the deferred scripts in order.
            _js.RunLifecycle("interactive", "readystatechange");
            foreach (PreparedScript deferred in _deferred)
            {
                if (TimedOut())
                {
                    break;
                }

                await RunDeferredAsync(deferred).ConfigureAwait(false);
            }

            _deferred.Clear();
            while (_asyncModules.Count > 0 && !TimedOut())
            {
                await RunNextAsyncModuleAsync().ConfigureAwait(false);
            }

            // DOMContentLoaded follows parser/defer/module work; async scripts do not gate
            // it. They and the dynamic scripts inserted while the document loads (including
            // by a DOMContentLoaded listener) delay the load event.
            _js.RunLifecycle("DOMContentLoaded");
            _page.ReachReadiness(DocumentReadiness.DomContentLoaded);

            await WaitAsync(
                () => _asyncScripts.Count == 0 && _asyncModules.Count == 0 && SheetsReady && !_js.HasPendingLoadDelayingScripts(),
                null).ConfigureAwait(false);

            // readyState becomes complete before the load event. A script inserted by an
            // onload handler is therefore post-load work.
            _js.RunLifecycle("complete", "readystatechange", "load");
            _page.ReachReadiness(DocumentReadiness.Loaded);
            execDisarm.Dispose();
        }

        private bool TimedOut()
        {
            if (!_stopScripts && DateTime.UtcNow >= _deadline)
            {
                _stopScripts = true;
            }

            return _stopScripts;
        }

        /// <summary>Parse to the end, running the scripts the parser reaches.</summary>
        private async Task ParseAsync()
        {
            if (_parser is { } parser)
            {
                parser.IsDefinedCustomElement = _js.State.DefinedCustomElements.Contains;
            }

            long parseTime = 0;
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                if (_pendingBlocking is { } pending)
                {
                    _pendingBlocking = null;
                    await WaitAsync(() => SheetsReady && pending.IsReady, pending.Fetch).ConfigureAwait(false);
                    if (!TimedOut())
                    {
                        ExecuteClassic(pending, parserBlocking: true);
                    }

                    NotifyParserProgress();
                    continue;
                }

                long parseStarted = Stopwatch.GetTimestamp();
                ParserStop stop = NextParserStep(out NodeId script);
                long parsed = Stopwatch.GetTimestamp() - parseStarted;
                NotifyParserProgress();
                if (stop == ParserStop.Finished)
                {
                    return;
                }

                if (stop == ParserStop.Script)
                {
                    Prepare(script);
                }

                // The parser yields to the event loop as Chromium's does, so async scripts and
                // timers run during a long parse, not only once it is done. What counts is the
                // time spent parsing: scripts the parser ran do not make it yield sooner.
                parseTime += parsed;
                if (stop == ParserStop.Yield || Stopwatch.GetElapsedTime(0, parseTime).TotalMilliseconds >= ParserYieldMs)
                {
                    parseTime = 0;
                    await YieldToEventLoopAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>One turn of the event loop between two parser steps: loaded async scripts and modules, due tasks.</summary>
        private async Task YieldToEventLoopAsync()
        {
            ServiceReady();
            if (TimedOut())
            {
                return;
            }

            if (_asyncModules.Count > 0)
            {
                await RunNextAsyncModuleAsync().ConfigureAwait(false);
            }

            _js.RunDueTasks();
            ServiceReady();
            await Task.Yield();
        }

        private ParserStop NextParserStep(out NodeId script)
        {
            if (_parser is { } parser)
            {
                return parser.Run(ParserTokenBudget, out script);
            }

            if (_preparsed!.TryDequeue(out script))
            {
                return ParserStop.Script;
            }

            return ParserStop.Finished;
        }

        /// <summary>
        /// The rest of the document without its scripts, when the load stops early (its deadline,
        /// a navigation away): the document keeps all its content, as before scripts ran mid-parse.
        /// </summary>
        internal void FinishParsingWithoutScripts()
        {
            if (_parser is not { IsFinished: false } parser)
            {
                return;
            }

            try
            {
                while (parser.Run(0, out _) != ParserStop.Finished)
                {
                }

                HandleInserted(parser.TakeInsertedNodes(), notifyRealm: true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The page stays as far as it got.
                parser.Abort();
            }
        }

        /// <summary>Tell the realm and the runner what the parser inserted since the last call.</summary>
        private void NotifyParserProgress()
        {
            if (_parser is null)
            {
                return;
            }

            HandleInserted(_parser.TakeInsertedNodes(), notifyRealm: true);
        }

        private void HandleInserted(List<NodeId> inserted, bool notifyRealm)
        {
            if (inserted.Count == 0 && _emptyStyles.Count == 0)
            {
                return;
            }

            List<NodeId>? scripts = null;
            List<NodeId>? sheets = null;
            bool sawBase = false;
            _js.WithDom(dom =>
            {
                foreach (NodeId id in inserted)
                {
                    if (dom.GetNode(id)?.AsElement() is not { } element)
                    {
                        continue;
                    }

                    switch (element.Name.Local)
                    {
                        case "script":
                            (scripts ??= []).Add(id);
                            break;
                        case "link":
                            (sheets ??= []).Add(id);
                            break;
                        case "style":
                            // A <style> whose text has not been parsed yet is looked at again
                            // next time.
                            if (dom.TextContent(id).Length == 0 && _parser is { IsFinished: false })
                            {
                                _emptyStyles.Add(id);
                            }
                            else
                            {
                                (sheets ??= []).Add(id);
                            }

                            break;
                        case "base":
                            sawBase = true;
                            break;
                    }
                }

                return 0;
            });

            if (_emptyStyles.Count > 0 && (inserted.Count > 0 || _parser is { IsFinished: true }))
            {
                (sheets ??= []).AddRange(_emptyStyles);
                _emptyStyles.Clear();
            }

            if (sawBase)
            {
                _baseUrl = null;
            }

            if (scripts is not null)
            {
                _js.MarkScriptsStarted(scripts);
            }

            if (notifyRealm)
            {
                _js.ParserInserted(inserted);
            }

            if (sheets is not null)
            {
                StartSheetLoads(sheets);
            }
        }

        // ------------------------------------------------------------ style sheets

        /// <summary>Parser-inserted style sheets block scripts until they load (HTML "script-blocking style sheet").</summary>
        private bool SheetsReady
        {
            get
            {
                ApplyLoadedSheets();
                return _sheetLoads.Count == 0;
            }
        }

        private void StartSheetLoads(List<NodeId> candidates)
        {
            Task<List<(AuthorStylesheetTarget Target, string Css, bool OriginClean, string ResponseUrl)>> load =
                _page.FetchStylesheetsAsync(candidates, _sheetReports, _ct);
            if (load.IsCompleted && load.Result.Count == 0)
            {
                RunSheetReports();
                return;
            }

            _sheetLoads.Add(load);
            _ = load.ContinueWith(_ => Signal(), TaskScheduler.Default);
        }

        private void ApplyLoadedSheets()
        {
            for (int i = 0; i < _sheetLoads.Count;)
            {
                var load = _sheetLoads[i];
                if (!load.IsCompleted)
                {
                    i++;
                    continue;
                }

                _sheetLoads.RemoveAt(i);
                RunSheetReports();
                if (load.IsCompletedSuccessfully && load.Result.Count != 0)
                {
                    _page.ApplyAuthorStylesheets(_js, load.Result);
                }
            }
        }

        private void RunSheetReports()
        {
            Action[] reports;
            lock (_sheetReports)
            {
                reports = [.. _sheetReports];
                _sheetReports.Clear();
            }

            foreach (Action report in reports)
            {
                report();
            }
        }

        // ------------------------------------------------------------ preparing scripts

        private string BaseUrl() => _baseUrl ??= _page.ResolveBaseUrl()?.Href ?? _page.UrlString();

        /// <summary>
        /// HTML "prepare the script element" for a parser-inserted script whose end tag the parser
        /// just processed.
        /// </summary>
        private void Prepare(NodeId nid)
        {
            if (TimedOut())
            {
                return;
            }

            string baseUrl = _preparsedBases is { } bases && bases.TryGetValue(nid.Raw, out string? atScript)
                ? atScript
                : BaseUrl();
            ScriptInfo? info = _js.WithDom(dom => dom.IsConnected(nid) ? DescribeScript(dom, nid, baseUrl) : null);
            if (info is null)
            {
                return;
            }
            if (Environment.GetEnvironmentVariable("PC_TRACE") == "1") Console.Error.WriteLine($"[prep] {info.Kind} {info.Src} async={info.IsAsync}");

            switch (info.Kind)
            {
                case ScriptKind.ImportMap:
                    if (info.Src is null)
                    {
                        try
                        {
                            _js.AddImportMap(info.Inline, info.BaseUrl);
                        }
                        catch (JsRuntimeException)
                        {
                            // Ignore an invalid import map, as Chromium does.
                        }
                    }

                    break;

                case ScriptKind.Classic when info.Src is { } src:
                {
                    string? url = _page.ResolveScriptUrl(info.BaseUrl, src);
                    Task<ScriptFetch?>? fetch = null;
                    if (url is not null)
                    {
                        ReferrerPolicy policy = info.ReferrerPolicy ?? _js.DocumentReferrerPolicy;
                        fetch = _page.StartScriptFetch(url, policy, _ct);
                    }

                    var prepared = new PreparedScript { Info = info, Fetch = fetch, Blocked = url is null };
                    if (info.IsAsync)
                    {
                        _asyncScripts.Add(prepared);
                        if (fetch is null)
                        {
                            _asyncLoaded.Enqueue(prepared);
                        }
                        else
                        {
                            _ = fetch.ContinueWith(
                                _ =>
                                {
                                    _asyncLoaded.Enqueue(prepared);
                                    Signal();
                                },
                                TaskScheduler.Default);
                        }
                    }
                    else if (info.IsDefer)
                    {
                        _deferred.Add(prepared);
                    }
                    else
                    {
                        _pendingBlocking = prepared;
                    }

                    break;
                }

                case ScriptKind.Classic:
                {
                    var prepared = new PreparedScript { Info = info };
                    // An inline script waits for the style sheets that block scripts.
                    if (!SheetsReady)
                    {
                        _pendingBlocking = prepared;
                    }
                    else
                    {
                        ExecuteClassic(prepared, parserBlocking: true);
                    }

                    break;
                }

                case ScriptKind.Module:
                {
                    var prepared = new PreparedScript { Info = info };
                    if (info.IsAsync)
                    {
                        _asyncModules.Add(prepared);
                    }
                    else
                    {
                        _deferred.Add(prepared);
                    }

                    break;
                }
            }
        }

        // ------------------------------------------------------------ running scripts

        /// <summary>
        /// Execute a classic script. A parser-blocking one runs with the insertion point
        /// defined, so its document.write output is parsed in place.
        /// </summary>
        private void ExecuteClassic(PreparedScript prepared, bool parserBlocking)
        {
            ScriptInfo info = prepared.Info;
            NodeId nid = NodeId.New(info.Nid);
            string name;
            string code;
            if (info.Src is not null)
            {
                ScriptFetch? fetched = prepared.Fetch is { IsCompletedSuccessfully: true } done ? done.Result : null;
                if (fetched is null)
                {
                    _js.DispatchScriptEvent(nid, "error");
                    return;
                }

                Response response = fetched.Response;
                _page.RecordResourceTiming(response.Url.AbsoluteUri, "script", response, fetched.StartedAt, fetched.EndedAt);
                _page.RecordNetworkEventWithBody(
                    fetched.Url,
                    "GET",
                    "Script",
                    response.Status,
                    response.Headers,
                    response.Body,
                    base64Encoded: false);
                if (!PageHelpers.ScriptResponseIsExecutable(response.Status))
                {
                    _js.DispatchScriptEvent(nid, "error");
                    return;
                }

                // Script bodies: only the HTTP Content-Type charset matters (no in-band
                // meta-charset for JS).
                code = ContentEncoding.DecodeNonHtml(response.Body, response.ContentType());
                name = NetUrl.To(response.Url).Href;
            }
            else
            {
                code = info.Inline;
                name = info.BaseUrl;
            }

            DocumentParser? parser = parserBlocking ? _parser : null;
            parser?.EnterScript();
            _currentScripts.Push(info.Nid);
            _js.SetCurrentScriptNid(info.Nid);
            try
            {
                // A page script that throws is a page problem, not a navigation failure.
                _js.ExecuteScriptGuarded(name, code);
            }
            catch (JsRuntimeException)
            {
                // Reported to the page through the runtime's uncaught-exception queue.
            }
            finally
            {
                _currentScripts.Pop();
                _js.SetCurrentScriptNid(_currentScripts.TryPeek(out uint outer) ? outer : 0);
                parser?.ExitScript();
            }

            if (info.Src is not null)
            {
                _js.DispatchScriptEvent(nid, "load");
            }
        }

        /// <summary>Run the async scripts that have loaded, in the order they loaded, and apply loaded style sheets.</summary>
        private void ServiceReady()
        {
            ApplyLoadedSheets();
            while (!TimedOut() && _asyncLoaded.TryDequeue(out PreparedScript? ready))
            {
                if (Environment.GetEnvironmentVariable("PC_TRACE") == "1") Console.Error.WriteLine($"[async] {ready.Info.Src} {Environment.StackTrace}");
                _asyncScripts.Remove(ready);
                ExecuteClassic(ready, parserBlocking: false);
                NotifyParserProgress();
            }
        }

        private async Task RunDeferredAsync(PreparedScript deferred)
        {
            await WaitAsync(() => SheetsReady && deferred.IsReady, deferred.Fetch).ConfigureAwait(false);
            if (TimedOut())
            {
                return;
            }

            if (deferred.Info.Kind == ScriptKind.Module)
            {
                await RunModuleAsync(deferred.Info).ConfigureAwait(false);
            }
            else
            {
                ExecuteClassic(deferred, parserBlocking: false);
            }
        }

        private async Task RunNextAsyncModuleAsync()
        {
            PreparedScript next = _asyncModules[0];
            _asyncModules.RemoveAt(0);
            await RunModuleAsync(next.Info).ConfigureAwait(false);
        }

        /// <summary>Load and evaluate a module script's graph within its budget.</summary>
        private async Task RunModuleAsync(ScriptInfo script)
        {
            if (RemainingBudgetMs(_deadline) is not { } pageMs)
            {
                return;
            }

            // Modules on an already-rendered page are enhancement, not the app: a short budget,
            // so one slow non-essential module cannot block navigation. A page whose body is
            // still an empty shell IS the SPA, and gets the full script budget.
            int bodyNodes = _js.WithDom(dom =>
                dom.TryQuerySelector("body", out NodeId? body, out _) && body is { } id
                    ? dom.Descendants(id).Count
                    : 0);
            ulong shortMs = _moduleBudgetOverride ?? PageHelpers.EnvUlong("POCKETCALCULATOR_MODULE_BUDGET_MS", 3_000);
            ulong moduleBudgetMs = _moduleBudgetOverride is not null || bodyNodes > 50 ? shortMs : _scriptDeadlineMs;
            ulong prepareBudgetMs = Math.Min(moduleBudgetMs, pageMs);
            long prepareStarted = Stopwatch.GetTimestamp();
            PreparedModule prepared;
            string? moduleUrl;
            try
            {
                if (script.Src is { } src)
                {
                    moduleUrl =
                        src.StartsWith("http://", StringComparison.Ordinal)
                        || src.StartsWith("https://", StringComparison.Ordinal)
                        || src.StartsWith("data:", StringComparison.Ordinal)
                            ? src
                            : PageUrl.TryParse(script.BaseUrl) is { } moduleBase
                                && PageUrl.TryJoin(moduleBase, src) is { } joined
                                ? joined.Href
                                : src;
                    prepared = await _js.PrepareModuleAsync(moduleUrl, prepareBudgetMs).ConfigureAwait(false);
                }
                else
                {
                    moduleUrl = null;
                    prepared = await _js.PrepareInlineModuleAsync(script.Inline, script.BaseUrl, prepareBudgetMs).ConfigureAwait(false);
                }
            }
            catch (Exception) when (!_ct.IsCancellationRequested)
            {
                // Any failure but the navigation's own cancellation is this module's: a module
                // load that times out, or a watchdog interrupt, must not end the script phase.
                return;
            }

            ulong graphElapsedMs = ElapsedMsCeil(prepareStarted);
            ulong remainingActiveMs = moduleBudgetMs > graphElapsedMs ? moduleBudgetMs - graphElapsedMs : 0;
            if (remainingActiveMs == 0)
            {
                return;
            }

            await _page.EvaluateModuleAsync(
                new ScheduledScript.Module(prepared, moduleUrl, remainingActiveMs, graphElapsedMs, Stopwatch.GetTimestamp()),
                _deadline,
                _moduleHostcallGraceMs,
                _ct).ConfigureAwait(false);
            NotifyParserProgress();
        }

        // ------------------------------------------------------------ the event loop

        private void Signal() => _wake.TrySetResult();

        /// <summary>
        /// Run the event loop (due page tasks, loaded async scripts and style sheets, async
        /// modules) until <paramref name="ready"/> holds or the script deadline passes.
        /// </summary>
        private async Task WaitAsync(Func<bool> ready, Task? wake)
        {
            if (Environment.GetEnvironmentVariable("PC_TRACE") == "1") Console.Error.WriteLine($"[wait] start {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} timers={_js.NextTimerDelayMs} ready={ready()}");
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                ServiceReady();
                if (ready() || TimedOut())
                {
                    return;
                }

                if (_asyncModules.Count > 0)
                {
                    await RunNextAsyncModuleAsync().ConfigureAwait(false);
                    continue;
                }

                TaskCompletionSource signal = _wake;
                bool progressed = _js.RunDueTasks();
                if (Environment.GetEnvironmentVariable("PC_TRACE") == "1") Console.Error.WriteLine($"[wait] turn {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} progressed={progressed} timers={_js.NextTimerDelayMs}");
                ServiceReady();
                if (ready())
                {
                    return;
                }

                if (progressed)
                {
                    await Task.Yield();
                    continue;
                }

                double wait = Math.Clamp(_js.NextTimerDelayMs ?? 10, 1, 10);
                Task delay = Task.Delay(TimeSpan.FromMilliseconds(wait), _ct);
                Task woken = wake is null
                    ? await Task.WhenAny(signal.Task, delay).ConfigureAwait(false)
                    : await Task.WhenAny(signal.Task, delay, wake).ConfigureAwait(false);
                if (woken == signal.Task)
                {
                    Interlocked.CompareExchange(
                        ref _wake,
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                        signal);
                }

                _ct.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// Record fetched author style sheets against their elements, and fire each linked one's
    /// load event (as the parser-time path always has).
    /// </summary>
    internal void ApplyAuthorStylesheets(
        PocketCalculatorJsRuntime js,
        List<(AuthorStylesheetTarget Target, string Css, bool OriginClean, string ResponseUrl)> sheets)
    {
        // DEVIATION from crates/obscura-browser, which runs one script per sheet to insert a
        // synthetic <style>. Chromium 141 inserts no element, so the bytes are recorded against
        // the <link> (or, for an @import, against the importing <style>) and the renderer reads
        // them from there. See "Known deviations" in todo.md.
        List<(NodeId Node, string ResponseUrl)> linked = [];
        js.WithDom(dom =>
        {
            foreach ((AuthorStylesheetTarget target, string css, bool originClean, string responseUrl) in sheets)
            {
                switch (target)
                {
                    case AuthorStylesheetTarget.Linked link:
                        dom.AppendExternalStylesheet(link.Node, css, originClean);
                        linked.Add((link.Node, responseUrl));
                        break;
                    case AuthorStylesheetTarget.InlineImport inline:
                        dom.AppendExternalStylesheet(inline.Node, css, originClean);
                        break;
                }
            }

            return 0;
        });

        if (linked.Count == 0)
        {
            return;
        }

        // An @import needs no script: it owns no CSSOM sheet and fires no event. A <link>
        // does both, and its load handler may still change what applies. The load script
        // addresses the link by its index among the document's stylesheet links now.
        List<NodeId> links = js.WithDom(dom =>
            dom.TryQuerySelectorAll("link[rel~=\"stylesheet\"]", out List<NodeId> found, out _) ? found : []) ?? [];
        foreach ((NodeId node, string responseUrl) in linked)
        {
            int index = links.IndexOf(node);
            if (index >= 0)
            {
                TryExecuteHost(js, "<fetch_stylesheets>", PageHelpers.LinkedStylesheetLoadScript(index, responseUrl));
            }
        }
    }
}
