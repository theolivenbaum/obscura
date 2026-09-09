using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Obscura.Js.Ops;
using BrowserContext = Obscura.Browser.BrowserContext;
using Page = Obscura.Browser.Page;

namespace Obscura.Cdp;

/// <summary>Which codec an active screencast is producing frames in.</summary>
public enum ScreencastFormat
{
    Png,
    Jpeg,
}

/// <summary>One active <c>Page.startScreencast</c> stream.</summary>
public sealed class ScreencastState
{
    public required ScreencastFormat Format { get; set; }

    public required byte Quality { get; set; }

    public required uint? MaxWidth { get; set; }

    public required uint? MaxHeight { get; set; }

    public required uint EveryNthFrame { get; set; }

    public ulong CommandFrameCounter { get; set; }

    public required long SessionId { get; set; }

    public byte FramesInFlight { get; set; }

    /// <summary>
    /// Last connected-document generation observed by the frame producer. The
    /// autonomous pump uses this as a cheap compositor-damage signal, so an idle
    /// screencast does not continuously rasterize identical frames.
    /// </summary>
    public ulong ObservedActivityGeneration { get; set; }

    /// <summary>
    /// A changed generation remains pending across sampling skips and
    /// acknowledgement backpressure. Once capacity returns, the newest page state
    /// is captured instead of losing the change.
    /// </summary>
    public bool AutonomousFramePending { get; set; }
}

/// <summary>One allocated <c>Runtime.ExecutionContextDescription</c>.</summary>
public sealed record ExecutionContextRecord(
    long Id,
    string UniqueId,
    string PageId,
    string FrameId,
    string Origin,
    string WorldName,
    bool IsDefault);

/// <summary>
/// Everything one CDP connection owns: its pages, its sessions, its execution
/// contexts and the queue of events waiting to go back out on the wire.
/// </summary>
/// <remarks>
/// One instance per WebSocket. The server hands each connection an isolated
/// <see cref="BrowserContext"/> (its own cookie jar and HTTP client) and runs
/// its processor on a dedicated OS thread, so a page's V8 isolate is confined to
/// one thread and two connections' isolates can never collide.
/// </remarks>
public sealed class CdpContext
{
    private readonly Dictionary<long, ExecutionContextRecord> _executionContexts = [];
    private readonly Dictionary<string, List<long>> _pageContexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _pageIsolatedWorlds = new(StringComparer.Ordinal);
    private uint _pageCounter;
    private uint _browserContextCounter;
    private ulong _targetSessionCounter;
    private long _nextDefaultContextId = 1;
    private long _nextScreencastSessionId;

    private CdpContext(BrowserContext defaultContext) => DefaultContext = defaultContext;

    public List<Page> Pages { get; } = [];

    /// <summary>session id -&gt; page id.</summary>
    public Dictionary<string, string> Sessions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Current document loader per page. Navigation events and later
    /// script-initiated Network events must share this id; inventing a loader for
    /// each fetch breaks DevTools request grouping.
    /// </summary>
    public Dictionary<string, string> CurrentLoaderIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Pages whose initial navigation event sequence has been emitted. A page is
    /// created already loaded (about:blank), but Chrome emits that load's events
    /// when the client attaches; <c>Page.enable</c> emits them once per page so
    /// clients waiting on the initial load (chromiumoxide, #833) unblock.
    /// </summary>
    public HashSet<string> NavEventsEmitted { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Child frame ids already reported to the client, per page, so each frame is
    /// announced once and a frame that goes away can be retracted.
    /// </summary>
    public Dictionary<string, List<string>> AnnouncedFrames { get; } = new(StringComparer.Ordinal);

    public List<CdpEvent> PendingEvents { get; } = [];

    public Dictionary<string, ScreencastState> Screencasts { get; } = new(StringComparer.Ordinal);

    public BrowserContext DefaultContext { get; }

    public Dictionary<string, BrowserContext> BrowserContexts { get; } = new(StringComparer.Ordinal);

    /// <summary>(identifier, source) pairs, in insertion order.</summary>
    public List<(string Identifier, string Source)> PreloadScripts { get; } = [];

    public uint PreloadCounter { get; set; }

    /// <summary>
    /// Which sessions asked for each <c>Runtime.addBinding</c> name. A binding is
    /// a session-scoped subscription in CDP, and a client discards any event whose
    /// sessionId is not one it holds, so the call has to go back to the session
    /// that registered the name rather than to whichever session of the page
    /// happens to come first out of a dictionary.
    /// </summary>
    public Dictionary<string, List<string>> BindingSessions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Sessions that called <c>Runtime.enable</c>. Console and exception events
    /// are page-scoped but only delivered to these subscribers.
    /// </summary>
    public HashSet<string> RuntimeEnabledSessions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Legacy direct-embedder configuration. Protocol-created worlds live only in
    /// the per-page world list, so this does not grow with page churn.
    /// </summary>
    public List<string> IsolatedWorlds { get; } = [];

    /// <summary>
    /// Allocated executionContextIds. An id is routable for an attached session
    /// only when the execution-context map also records the owning page.
    /// </summary>
    /// <remarks>
    /// <c>Runtime.evaluate</c> / <c>Runtime.callFunctionOn</c> consult this set to
    /// reject requests targeting an unknown context, matching real Chrome's
    /// "Cannot find context with specified id" CDP error and unblocking the
    /// Playwright locator path described in issue #51.
    /// </remarks>
    public HashSet<long> ValidContextIds { get; } = [];

    /// <summary>
    /// Monotonic counter for isolated-world execution context ids. Issue #192:
    /// both <c>Page.createIsolatedWorld</c> and the navigation path used to
    /// hardcode id 100, so re-creating a context (Playwright opening a second
    /// page, or re-navigating) silently emitted the same id twice and the
    /// client's bookkeeping diverged from the server's. Each fresh isolated
    /// context now claims and increments from this counter, mirroring the
    /// incrementing, never-reused ids real Chrome emits.
    /// </summary>
    public long NextIsolatedContextId { get; set; } = 100;

    public FetchInterceptState FetchIntercept { get; } = new();

    public IInterceptSink? InterceptSink { get; set; }

    /// <summary>
    /// Open IO streams for <c>Fetch.takeResponseBodyAsStream</c>. Each holds a
    /// response body taken out of the page cache so a large download is streamed
    /// chunk-by-chunk via <c>IO.read</c> and freed on <c>IO.close</c> (issue
    /// #360). The store caps how many bodies (and how many bytes) can be held at
    /// once, evicting the oldest, so an abandoned or disconnected stream cannot
    /// leak unbounded.
    /// </summary>
    public IoStreamStore IoStreams { get; } = new();

    /// <summary>
    /// Serializes V8 work within THIS connection. With the thread-per-connection
    /// server (#430) each connection runs on its own OS thread, so isolates never
    /// collide across connections; this per-connection lock keeps a connection's
    /// own navigation task and command dispatch from interleaving two of its
    /// pages' isolates on that one thread. It is deliberately per-connection, not
    /// process-wide, so connections run in parallel (measured ~2x at concurrency
    /// 2, ~3x at 4) instead of serializing all V8 on one mutex.
    /// </summary>
    public SemaphoreSlim V8Lock { get; } = new(1, 1);

    public static CdpContext New() => NewWithOptions(null, false);

    public static CdpContext NewWithProxy(string? proxy) => NewWithOptions(proxy, false);

    public static CdpContext NewWithOptions(string? proxy, bool stealth) =>
        NewWithFullOptions(proxy, stealth, null);

    public static CdpContext NewWithFullOptions(string? proxy, bool stealth, string? userAgent) =>
        NewWithSecurity(proxy, stealth, userAgent, false);

    public static CdpContext NewWithStorage(
        string? proxy,
        bool stealth,
        string? userAgent,
        string? storageDir) =>
        NewInner(proxy, stealth, userAgent, storageDir, false, false);

    public static CdpContext NewWithSecurity(
        string? proxy,
        bool stealth,
        string? userAgent,
        bool allowFileAccess) =>
        NewInner(proxy, stealth, userAgent, null, allowFileAccess, false);

    /// <summary>
    /// Build a CDP context around an already-constructed default browser context.
    /// The server passes a fresh isolated context per WebSocket; tests and
    /// embedders may construct their own.
    /// </summary>
    public static CdpContext NewWithSharedContext(BrowserContext defaultContext) =>
        new(defaultContext);

    private static CdpContext NewInner(
        string? proxy,
        bool stealth,
        string? userAgent,
        string? storageDir,
        bool allowFileAccess,
        bool allowPrivateNetwork)
    {
        var context = BrowserContext.WithStorageAndNetwork(
            "default",
            proxy,
            stealth,
            userAgent,
            storageDir,
            allowPrivateNetwork);
        context.AllowFileAccess = allowFileAccess;
        return NewWithSharedContext(context);
    }

    /// <summary>
    /// Claim the next isolated-world execution context id and register it as
    /// valid for <c>Runtime.evaluate</c>/<c>callFunctionOn</c>. Issue #192.
    /// </summary>
    public long NextIsolatedContext()
    {
        while (ValidContextIds.Contains(NextIsolatedContextId) ||
               _executionContexts.ContainsKey(NextIsolatedContextId))
        {
            NextIsolatedContextId = checked(NextIsolatedContextId + 1);
        }

        var id = NextIsolatedContextId;
        NextIsolatedContextId = checked(id + 1);
        ValidContextIds.Add(id);
        return id;
    }

    public string CreatePage() =>
        CreatePageInContext(null, out var pageId, out var error)
            ? pageId!
            : throw new InvalidOperationException(error ?? "default browser context must exist");

    /// <summary>
    /// Create a page in the named browser context, or the default one when
    /// <paramref name="contextId"/> is null. False with an
    /// <paramref name="error"/> when the context does not exist.
    /// </summary>
    public bool CreatePageInContext(string? contextId, out string? pageId, out string? error)
    {
        BrowserContext context;
        if (contextId is null)
        {
            context = DefaultContext;
        }
        else if (BrowserContextById(contextId) is { } found)
        {
            context = found;
        }
        else
        {
            pageId = null;
            error = $"Browser context not found: {contextId}";
            return false;
        }

        _pageCounter++;
        var id = $"page-{_pageCounter}";
        var page = new Page(id, context);
        page.NavigateBlank();
        Pages.Add(page);
        CurrentLoaderIds[id] = $"loader-blank-{id}";
        pageId = id;
        error = null;
        return true;
    }

    public BrowserContext? BrowserContextById(string id) =>
        string.Equals(id, DefaultContext.Id, StringComparison.Ordinal)
            ? DefaultContext
            : BrowserContexts.GetValueOrDefault(id);

    public string CreateBrowserContext()
    {
        _browserContextCounter++;
        var id = $"context-{_browserContextCounter}";
        BrowserContexts[id] = DefaultContext.IsolatedCopy(id, false);
        return id;
    }

    /// <summary>
    /// Allocate a distinct CDP session for every explicit target attachment.
    /// </summary>
    /// <remarks>
    /// A target may have more than one client session at a time (for example,
    /// Playwright's managed page session plus <c>newCDPSession(page)</c>).
    /// Reusing the page's auto-attach session id makes the client's session
    /// registry overwrite the original route.
    /// </remarks>
    public string NextTargetSession(string targetId)
    {
        if (_targetSessionCounter != ulong.MaxValue)
        {
            _targetSessionCounter++;
        }

        return $"{targetId}-session-{_targetSessionCounter}";
    }

    /// <summary>
    /// Dispose a browser context and every page in it. False with an
    /// <paramref name="error"/> for the default context or an unknown id.
    /// </summary>
    public bool DisposeBrowserContext(string id, out List<string> pageIds, out string? error)
    {
        pageIds = [];
        if (string.Equals(id, DefaultContext.Id, StringComparison.Ordinal))
        {
            error = "The default browser context cannot be disposed";
            return false;
        }

        if (!BrowserContexts.Remove(id))
        {
            error = $"Browser context not found: {id}";
            return false;
        }

        foreach (var page in Pages)
        {
            if (string.Equals(page.Context.Id, id, StringComparison.Ordinal))
            {
                pageIds.Add(page.Id);
            }
        }

        foreach (var pageId in pageIds)
        {
            RemovePage(pageId);
        }

        error = null;
        return true;
    }

    public Page? GetPage(string id)
    {
        foreach (var page in Pages)
        {
            if (string.Equals(page.Id, id, StringComparison.Ordinal))
            {
                return page;
            }
        }

        return null;
    }

    /// <summary>
    /// The same lookup as <see cref="GetPage"/>. Rust needs a separate
    /// <c>_mut</c> accessor; C# does not, and this alias keeps the ported call
    /// sites readable.
    /// </summary>
    public Page? GetPageMut(string id) => GetPage(id);

    public void RefreshRuntimeEventCollection(string pageId)
    {
        var enabled = false;
        foreach (var sessionId in RuntimeEnabledSessions)
        {
            if (Sessions.TryGetValue(sessionId, out var owner) &&
                string.Equals(owner, pageId, StringComparison.Ordinal))
            {
                enabled = true;
                break;
            }
        }

        GetPage(pageId)?.SetRuntimeEventsEnabled(enabled);
    }

    public void RemovePage(string id)
    {
        List<string> removedSessions = [];
        foreach (var (sessionId, pageId) in Sessions)
        {
            if (string.Equals(pageId, id, StringComparison.Ordinal))
            {
                removedSessions.Add(sessionId);
            }
        }

        for (var i = Pages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(Pages[i].Id, id, StringComparison.Ordinal))
            {
                Pages[i].Dispose();
                Pages.RemoveAt(i);
            }
        }

        CurrentLoaderIds.Remove(id);
        AnnouncedFrames.Remove(id);
        foreach (var sessionId in removedSessions)
        {
            Screencasts.Remove(sessionId);
            RuntimeEnabledSessions.Remove(sessionId);
        }

        if (_pageContexts.Remove(id, out var contextIds))
        {
            foreach (var contextId in contextIds)
            {
                _executionContexts.Remove(contextId);
                ValidContextIds.Remove(contextId);
            }
        }

        _pageIsolatedWorlds.Remove(id);
        foreach (var sessionId in removedSessions)
        {
            Sessions.Remove(sessionId);
        }
    }

    /// <summary>
    /// Live execution-context records. The Rust ownership tests read the private
    /// map directly; this is the same view for the ported ones, which exist to
    /// prove the maps do not grow across page churn.
    /// </summary>
    internal int ExecutionContextCount => _executionContexts.Count;

    /// <summary>How many pages currently own at least one execution context.</summary>
    internal int PageContextPageCount => _pageContexts.Count;

    /// <summary>The context ids a page owns, in allocation order.</summary>
    internal IReadOnlyList<long> ContextIdsForPage(string pageId) =>
        _pageContexts.TryGetValue(pageId, out var ids) ? ids : [];

    /// <summary>How many pages have a persisted isolated-world list.</summary>
    internal int PageIsolatedWorldPageCount => _pageIsolatedWorlds.Count;

    internal ExecutionContextRecord AllocateContext(
        string pageId,
        string frameId,
        string origin,
        string worldName,
        bool isDefault)
    {
        while (ValidContextIds.Contains(_nextDefaultContextId) ||
               _executionContexts.ContainsKey(_nextDefaultContextId))
        {
            _nextDefaultContextId = checked(_nextDefaultContextId + 1);
        }

        var id = _nextDefaultContextId;
        _nextDefaultContextId = checked(id + 1);
        var context = new ExecutionContextRecord(
            id,
            $"obscura-context-{Guid.NewGuid()}",
            pageId,
            frameId,
            origin,
            worldName,
            isDefault);
        ValidContextIds.Add(id);
        _executionContexts[id] = context;
        if (!_pageContexts.TryGetValue(pageId, out var owned))
        {
            owned = [];
            _pageContexts[pageId] = owned;
        }

        owned.Add(id);
        return context;
    }

    public ExecutionContextRecord? EnsureDefaultContext(string pageId)
    {
        foreach (var context in ContextsForPage(pageId))
        {
            if (context.IsDefault)
            {
                return context;
            }
        }

        if (GetPage(pageId) is not { } page)
        {
            return null;
        }

        return AllocateContext(pageId, page.FrameId, page.UrlString(), string.Empty, true);
    }

    /// <summary>The single hook for an installed replacement Document.</summary>
    public List<ExecutionContextRecord> CommitDefaultContext(
        string pageId,
        string frameId,
        string origin)
    {
        if (_pageContexts.Remove(pageId, out var previous))
        {
            foreach (var id in previous)
            {
                _executionContexts.Remove(id);
                ValidContextIds.Remove(id);
            }
        }

        List<ExecutionContextRecord> contexts =
            [AllocateContext(pageId, frameId, origin, string.Empty, true)];
        List<string> worlds = [.. IsolatedWorlds];
        if (_pageIsolatedWorlds.TryGetValue(pageId, out var pageWorlds))
        {
            foreach (var world in pageWorlds)
            {
                if (!worlds.Contains(world, StringComparer.Ordinal))
                {
                    worlds.Add(world);
                }
            }
        }

        if (worlds.Count == 0)
        {
            worlds.Add("__puppeteer_utility_world__24.40.0");
        }

        foreach (var world in worlds)
        {
            contexts.Add(AllocateContext(pageId, frameId, origin, world, false));
        }

        return contexts;
    }

    public ExecutionContextRecord? ContextById(long id) => _executionContexts.GetValueOrDefault(id);

    public ExecutionContextRecord? ContextByUniqueId(string uniqueId)
    {
        foreach (var context in _executionContexts.Values)
        {
            if (string.Equals(context.UniqueId, uniqueId, StringComparison.Ordinal))
            {
                return context;
            }
        }

        return null;
    }

    public IEnumerable<ExecutionContextRecord> ContextsForPage(string pageId)
    {
        if (!_pageContexts.TryGetValue(pageId, out var ids))
        {
            yield break;
        }

        foreach (var id in ids)
        {
            if (_executionContexts.TryGetValue(id, out var context))
            {
                yield return context;
            }
        }
    }

    public (ExecutionContextRecord Context, bool Created) CreateIsolatedContext(
        string pageId,
        string frameId,
        string origin,
        string worldName,
        bool persistAcrossNavigation)
    {
        if (worldName.Length != 0)
        {
            foreach (var existing in ContextsForPage(pageId))
            {
                if (!existing.IsDefault &&
                    string.Equals(existing.FrameId, frameId, StringComparison.Ordinal) &&
                    string.Equals(existing.WorldName, worldName, StringComparison.Ordinal))
                {
                    return (existing, false);
                }
            }

            if (persistAcrossNavigation)
            {
                if (!_pageIsolatedWorlds.TryGetValue(pageId, out var worlds))
                {
                    worlds = [];
                    _pageIsolatedWorlds[pageId] = worlds;
                }

                if (!worlds.Contains(worldName, StringComparer.Ordinal))
                {
                    worlds.Add(worldName);
                }
            }
        }

        return (AllocateContext(pageId, frameId, origin, worldName, false), true);
    }

    public long? DefaultContextId(string pageId)
    {
        foreach (var context in ContextsForPage(pageId))
        {
            if (context.IsDefault)
            {
                return context.Id;
            }
        }

        return null;
    }

    internal List<ExecutionContextRecord> RemoveFrameContexts(string pageId, string frameId)
    {
        List<long> removed = [];
        if (_pageContexts.TryGetValue(pageId, out var owned))
        {
            foreach (var id in owned)
            {
                if (_executionContexts.TryGetValue(id, out var context) &&
                    string.Equals(context.FrameId, frameId, StringComparison.Ordinal))
                {
                    removed.Add(id);
                }
            }

            owned.RemoveAll(removed.Contains);
        }

        List<ExecutionContextRecord> records = [];
        foreach (var id in removed)
        {
            ValidContextIds.Remove(id);
            if (_executionContexts.Remove(id, out var record))
            {
                records.Add(record);
            }
        }

        return records;
    }

    public List<string> RuntimeSessionsForPage(string pageId)
    {
        List<string> sessions = [];
        foreach (var session in RuntimeEnabledSessions)
        {
            if (Sessions.TryGetValue(session, out var owner) &&
                string.Equals(owner, pageId, StringComparison.Ordinal))
            {
                sessions.Add(session);
            }
        }

        sessions.Sort(StringComparer.Ordinal);
        return sessions;
    }

    /// <summary>Never wrap a delayed acknowledgement onto a replacement stream.</summary>
    public long NextScreencastSession()
    {
        if (_nextScreencastSessionId != long.MaxValue)
        {
            _nextScreencastSessionId++;
        }

        return _nextScreencastSessionId;
    }

    public Page? GetSessionPage(string? sessionId)
    {
        if (sessionId is null || !Sessions.TryGetValue(sessionId, out var pageId))
        {
            return null;
        }

        return GetPage(pageId);
    }

    /// <summary>
    /// The page behind a session, made ready to run JS.
    /// </summary>
    /// <remarks>
    /// V8 allows one entered isolate per OS thread, so before the target page is
    /// resumed any other page holding a live runtime is suspended. Handlers that
    /// only read Rust-side fields should use <see cref="GetSessionPage"/>, which
    /// does not disturb any isolate.
    /// </remarks>
    public Page? GetSessionPageMut(string? sessionId)
    {
        if (sessionId is null || !Sessions.TryGetValue(sessionId, out var pageId))
        {
            return null;
        }

        var targetHasJs = false;
        foreach (var page in Pages)
        {
            if (string.Equals(page.Id, pageId, StringComparison.Ordinal) && page.HasJs)
            {
                targetHasJs = true;
                break;
            }
        }

        if (!targetHasJs)
        {
            foreach (var page in Pages)
            {
                if (!string.Equals(page.Id, pageId, StringComparison.Ordinal) && page.HasJs)
                {
                    page.SuspendJs();
                    break;
                }
            }

            foreach (var page in Pages)
            {
                if (string.Equals(page.Id, pageId, StringComparison.Ordinal))
                {
                    page.ResumeJs();
                    break;
                }
            }
        }

        return GetPage(pageId);
    }
}
