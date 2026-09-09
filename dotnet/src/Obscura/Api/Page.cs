using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Obscura.Browser;
using Obscura.Js.Ops;
using Obscura.Net;
using InnerPage = Obscura.Browser.Page;

namespace Obscura.Api;

/// <summary>A browser tab/page.</summary>
/// <remarks>
/// Rust wraps the inner page in a <c>RefCell</c> so read-style operations,
/// including <see cref="Evaluate"/>, take <c>&amp;self</c> and an
/// <see cref="Element"/> can drive evaluation through a shared borrow. A managed
/// reference already gives that, so the port holds the page directly.
/// </remarks>
public sealed class Page : IDisposable
{
    internal Page(InnerPage inner) => Inner = inner;

    /// <summary>The underlying browser page.</summary>
    internal InnerPage Inner { get; }

    /// <summary>
    /// Release the page's V8 isolate and detach its per-page callbacks.
    /// </summary>
    /// <remarks>
    /// Rust reclaims both when the <c>Page</c> is dropped. ClearScript needs the
    /// call to be explicit, and a leaked <c>V8ScriptEngine</c> wedges the
    /// process, so the API page is disposable and disposing it is what a Rust
    /// <c>drop(page)</c> means here.
    /// </remarks>
    public void Dispose() => Inner.Dispose();

    /// <summary>Navigate to a URL and wait for load.</summary>
    public async Task GotoAsync(string url)
    {
        try
        {
            await Inner.NavigateWithWaitAsync(url, WaitUntil.Load).ConfigureAwait(false);
        }
        catch (PageException error)
        {
            throw ObscuraException.Navigation(error.Message);
        }
    }

    /// <summary>The current URL.</summary>
    public string Url => Inner.UrlString();

    /// <summary>Execute JS in the page.</summary>
    public JsonNode? Evaluate(string expression) => Inner.Evaluate(expression);

    /// <summary>URLs of the page's child frames, in creation order.</summary>
    public IReadOnlyList<string> FrameUrls() => Inner.FrameUrls();

    /// <summary>
    /// Execute JS inside one of the page's child frames. Each frame is its own
    /// realm with its own document, so this is the only way to observe one.
    /// </summary>
    public JsonNode? EvaluateInFrame(int index, string expression) =>
        Inner.EvaluateInFrame(index, expression);

    /// <summary>The page's HTML content.</summary>
    public string Content()
    {
        var value = Evaluate("document.documentElement.outerHTML");
        return AsString(value) ?? string.Empty;
    }

    /// <summary>Query a single element by CSS selector.</summary>
    public Element? QuerySelector(string selector)
    {
        var value = Evaluate(QuerySelectorScript(selector));
        return NidFromValue(value) is { } nid ? new Element(nid, this) : null;
    }

    /// <summary>Wait for a CSS selector to appear, polling every 100ms.</summary>
    public async Task<Element> WaitForSelectorAsync(string selector, TimeSpan timeout)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        var script = QuerySelectorScript(selector);
        while (true)
        {
            var value = Evaluate(script);
            if (NidFromValue(value) is { } nid)
            {
                return new Element(nid, this);
            }
            if (start.Elapsed > timeout)
            {
                throw ObscuraException.Timeout(
                    $"wait_for_selector({selector}) timed out after {((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)}ms");
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drive the page's JS event loop for up to <paramref name="maxMs"/>
    /// milliseconds, so async work scheduled by an earlier
    /// <see cref="Evaluate"/> can settle before the next one.
    /// </summary>
    public Task SettleAsync(ulong maxMs) => Inner.SettleAsync(maxMs);

    /// <summary>
    /// Register a script that runs before any of the page's own
    /// <c>&lt;script&gt;</c> tags, equivalent to CDP
    /// <c>Page.addScriptToEvaluateOnNewDocument</c>.
    /// </summary>
    public void AddPreloadScript(string script) => Inner.AddPreloadScript(script);

    /// <summary>
    /// Enable CDP-Fetch-style interception of every JS <c>fetch()</c>/XHR.
    /// </summary>
    public ChannelReader<InterceptedRequest> EnableInterception() => Inner.EnableInterception();

    /// <summary>Register a passive per-request callback. Returns a detach id.</summary>
    public ulong OnRequest(RequestCallback callback) => Inner.OnRequest(callback);

    /// <summary>Register a passive per-response callback. Returns a detach id.</summary>
    public ulong OnResponse(ResponseCallback callback) => Inner.OnResponse(callback);

    /// <summary>Detach a request callback; true when one was removed.</summary>
    public bool OffRequest(ulong id) => Inner.OffRequest(id);

    /// <summary>Detach a response callback; true when one was removed.</summary>
    public bool OffResponse(ulong id) => Inner.OffResponse(id);

    /// <summary>
    /// The escaping Rust applies before interpolating a selector into a JS
    /// string literal, so a quote or backslash cannot break out of it.
    /// </summary>
    internal static string EscapeJsSingleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'", StringComparison.Ordinal);

    private static string QuerySelectorScript(string selector) =>
        $"(function() {{ var el = document.querySelector('{EscapeJsSingleQuoted(selector)}'); return el ? el._nid : null; }})()";

    internal static string? AsString(JsonNode? value) =>
        value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>
    /// Read a DOM node id from a JS <c>evaluate</c> result. Obscura serializes JS
    /// numbers as f64, so an integer-valued result is still a float; accept either
    /// an integer or a non-negative finite float, and reject null / non-numbers.
    /// </summary>
    internal static ulong? NidFromValue(JsonNode? value)
    {
        if (value?.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        var number = value.GetValue<double>();
        return double.IsFinite(number) && number >= 0.0 ? (ulong)number : null;
    }
}

/// <summary>Handle to a DOM element.</summary>
/// <remarks>
/// Created through <see cref="Page.QuerySelector"/> or
/// <see cref="Page.WaitForSelectorAsync"/>. Rust ties the handle to the page's
/// lifetime with a borrow; here the handle keeps the page alive by referencing it.
/// </remarks>
public sealed class Element
{
    private readonly ulong _nodeId;
    private readonly Page _page;

    internal Element(ulong nodeId, Page page)
    {
        _nodeId = nodeId;
        _page = page;
    }

    /// <summary>The node id this handle wraps.</summary>
    public ulong NodeId => _nodeId;

    /// <summary>The element's text content.</summary>
    public string Text()
    {
        var value = _page.Evaluate(
            $"(function() {{ var el = globalThis._wrap && globalThis._wrap({Nid}); return el ? el.textContent : ''; }})()");
        return Page.AsString(value) ?? string.Empty;
    }

    /// <summary>An attribute value, or null when the attribute is absent.</summary>
    public string? Attribute(string name)
    {
        var escaped = Page.EscapeJsSingleQuoted(name);
        var value = _page.Evaluate(
            $"(function() {{ var el = globalThis._wrap && globalThis._wrap({Nid}); return el ? el.getAttribute('{escaped}') : null; }})()");
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return null;
        }
        return Page.AsString(value) ?? string.Empty;
    }

    /// <summary>Scroll the element into view and click it.</summary>
    public void Click()
    {
        _page.Evaluate(
            $"(function() {{ var el = globalThis._wrap && globalThis._wrap({Nid}); if (el) el.scrollIntoView({{block:'center'}}); }})()");
        var result = _page.Evaluate(
            $"(function() {{ var el = globalThis._wrap && globalThis._wrap({Nid}); if (el) {{ el.click(); return true; }} return false; }})()");
        var clicked = result?.GetValueKind() == JsonValueKind.True;
        if (!clicked)
        {
            throw ObscuraException.ElementNotFound("click failed");
        }
    }

    private string Nid => _nodeId.ToString(CultureInfo.InvariantCulture);
}
