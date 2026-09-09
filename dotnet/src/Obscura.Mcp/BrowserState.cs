using System.Globalization;
using Obscura.Browser;
using Obscura.Dom;

namespace Obscura.Mcp;

/// <summary>
/// The MCP server's browser session: the open tabs, which one is active, and the
/// element-ref table the agent addresses elements through.
/// </summary>
/// <remarks>
/// The server is stateful on purpose: an agent navigates once and then inspects
/// and interacts with the page it landed on, so every tool call operates on the
/// active tab.
/// </remarks>
public sealed class BrowserState : IDisposable
{
    /// <summary>
    /// Open tabs keyed by tab_id (e.g. "tab-1"). Sorted so list ordering is stable
    /// across calls (agents reason about "tab #2" deterministically), matching the
    /// Rust <c>BTreeMap</c>. Ordinal, because that is the byte order a
    /// <c>BTreeMap&lt;String, _&gt;</c> uses.
    /// </summary>
    private readonly SortedDictionary<string, Page> _tabs = new(StringComparer.Ordinal);

    private readonly BrowserContext _context;

    private readonly List<string> _consoleMessages = [];

    /// <summary>
    /// Element-ref table from the last <c>browser_snapshot</c> on the ACTIVE tab.
    /// Agents click / fill / type by <c>ref</c> (e.g. <c>"e3"</c>) instead of
    /// guessing a CSS selector. Refs are stable within a snapshot; the table is
    /// wiped on every navigation / tab switch and refilled on the next snapshot
    /// call.
    /// </summary>
    private readonly Dictionary<string, NodeId> _interactiveRefs = new(StringComparer.Ordinal);

    /// <summary>
    /// The tab id every tool call operates on. Null means there are no open tabs;
    /// the next <see cref="PageMut"/> call creates one.
    /// </summary>
    private string? _activeTab;

    private uint _tabCounter;

    public BrowserState(string? proxy, string? userAgent, bool stealth)
    {
        _context = BrowserContext.WithOptions("mcp", proxy, stealth);
        UserAgent = userAgent;
    }

    internal string? UserAgent { get; }

    internal BrowserContext Context => _context;

    internal SortedDictionary<string, Page> Tabs => _tabs;

    internal string? ActiveTab
    {
        get => _activeTab;
        set => _activeTab = value;
    }

    internal List<string> ConsoleMessages => _consoleMessages;

    internal Dictionary<string, NodeId> InteractiveRefs => _interactiveRefs;

    /// <summary>
    /// Make sure there is at least one tab and return the active tab's page.
    /// Auto-creates a default tab if none exist so every legacy single-page tool
    /// continues to work without requiring an explicit <c>browser_tab_new</c>.
    /// </summary>
    internal Page PageMut()
    {
        if (_activeTab is null)
        {
            _tabCounter++;
            var newId = $"tab-{_tabCounter.ToString(CultureInfo.InvariantCulture)}";
            _tabs[newId] = new Page("mcp-page", _context);
            _activeTab = newId;
        }

        var id = _activeTab!;
        Activate(id);
        return _tabs[id];
    }

    internal string NewTab()
    {
        _tabCounter++;
        var id = $"tab-{_tabCounter.ToString(CultureInfo.InvariantCulture)}";
        _tabs[id] = new Page($"mcp-{id}", _context);
        _activeTab = id;
        _interactiveRefs.Clear();
        return id;
    }

    /// <summary>
    /// Enforce the single-live-isolate invariant. rusty_v8 enters each V8 isolate on
    /// creation and requires isolates be dropped in reverse order of creation, so
    /// keeping more than one tab's isolate live at once and then dropping a
    /// non-newest one aborts the whole process (#258). Suspend every other tab
    /// (drops its isolate, keeps its DOM) and make the active tab the only live
    /// isolate, mirroring the CDP server's <c>Dispatcher::get_session_page_mut</c>.
    /// </summary>
    /// <remarks>
    /// ClearScript is happy with many live isolates, so this is not load-bearing for
    /// the port's stability. It stays because it is observable: a suspended tab
    /// loses its JS heap and rebuilds it on the next activation, and a port that
    /// kept every isolate live would answer differently after a tab switch.
    /// </remarks>
    internal void Activate(string tabId)
    {
        foreach (var (id, page) in _tabs)
        {
            if (!string.Equals(id, tabId, StringComparison.Ordinal) && page.HasJs)
            {
                page.SuspendJs();
            }
        }

        if (_tabs.TryGetValue(tabId, out var active))
        {
            active.ResumeJs();
        }
    }

    /// <summary>
    /// Close a tab without breaking the LIFO isolate-drop rule. <c>suspend_js</c>
    /// drops this tab's isolate (if it is the live one) while it is still the only
    /// entered isolate, so the following remove disposes no isolate (#258).
    /// </summary>
    internal bool CloseTab(string tabId)
    {
        if (_tabs.TryGetValue(tabId, out var page))
        {
            page.SuspendJs();
        }

        if (!_tabs.Remove(tabId, out var removed))
        {
            return false;
        }

        removed.Dispose();
        return true;
    }

    internal bool HasActivePageRuntime() =>
        _activeTab is { } id && _tabs.TryGetValue(id, out var page) && page.HasJs;

    /// <summary>
    /// Advance the active page by one wake-driven browser task and immediately
    /// consume any navigation that task queued. MCP owns its pages continuously, so
    /// leaving either half for the next tool call strands timers, fetches, and
    /// location/form/click navigations while the transport waits on stdin.
    /// </summary>
    /// <returns>True when the event loop reached idle and nothing navigated.</returns>
    internal async Task<bool> AdvanceActivePageTasksAsync()
    {
        var page = PageMut();
        bool reachedIdle;
        try
        {
            reachedIdle = await page.RunAutonomousEventLoopTurnAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        bool navigated;
        try
        {
            navigated = await page.ProcessPendingNavigationAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        if (navigated)
        {
            _interactiveRefs.Clear();
        }

        return reachedIdle && !navigated;
    }

    /// <summary>
    /// Consume a navigation that a synthesized interaction just queued, before the
    /// tool replies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A click on a submit button, an Enter keypress, or an evaluated
    /// <c>location.href</c> assignment does not issue a request by itself. It runs
    /// the page's own glue and leaves the navigation as pending state, which becomes
    /// a request only when a driving layer converts it. The CDP path already
    /// converts it, in <c>Input.dispatchMouseEvent</c> and after
    /// <c>Runtime.evaluate</c>, which is why the same click POSTs over CDP and does
    /// nothing over MCP (#618).
    /// </para>
    /// <para>
    /// <see cref="AdvanceActivePageTasksAsync"/> cannot cover this. It is armed
    /// after every dispatch, but it sits behind the transport's read, so a client
    /// that sends its next tool call immediately, which an agent does, wins that
    /// race every time. The reply is also written before any pump turn could run, so
    /// the tool would answer "Clicked" while the request has not left the process.
    /// </para>
    /// </remarks>
    internal async Task SettleSyntheticNavigationAsync()
    {
        bool navigated;
        try
        {
            navigated = await PageMut().ProcessPendingNavigationAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        if (navigated)
        {
            // The ref table names elements in a document that has gone away.
            _interactiveRefs.Clear();
            // One slice on the landed document, so a tool that reads the URL or the
            // text next sees the new page rather than an empty one.
            try
            {
                await PageMut().RunAutonomousEventLoopTurnAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not ToolException)
            {
                throw new ToolException(error.Message);
            }
        }
    }

    /// <summary>
    /// Resolve <c>ref=eN</c> to a CSS selector that uniquely targets the element.
    /// Snapshot writes <c>data-obscura-ref="eN"</c> onto every interactable, so the
    /// attribute survives across calls as long as the page isn't re-rendered without
    /// it. Throws if the ref hasn't been registered (caller must call
    /// <c>browser_snapshot</c> first).
    /// </summary>
    internal string RefToSelector(string reference)
    {
        if (!_interactiveRefs.ContainsKey(reference))
        {
            throw new ToolException(
                $"unknown ref '{reference}'; call browser_snapshot first to refresh the ref table");
        }

        return $"[data-obscura-ref=\"{reference}\"]";
    }

    public void Dispose()
    {
        foreach (var page in _tabs.Values)
        {
            page.SuspendJs();
            page.Dispose();
        }

        _tabs.Clear();
        _activeTab = null;
    }
}
