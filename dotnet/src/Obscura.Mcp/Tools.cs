using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Obscura.Browser;
using Obscura.Js.Runtime;
using Obscura.Net;

namespace Obscura.Mcp;

/// <summary>
/// The MCP browser-automation tools. Each one is the body of the matching
/// <c>tool_*</c> function in <c>crates/obscura-mcp/src/lib.rs</c>: the reply
/// strings are the tool's observable result and are reproduced exactly.
/// </summary>
internal static partial class Tools
{
    // ===== shared helpers =====

    /// <summary>
    /// Resolve a tool call's element target from either <c>ref</c> (preferred) or
    /// <c>selector</c> (fallback). Agents that called <c>browser_snapshot</c> /
    /// <c>browser_interactive_elements</c> get a ref table they can refer to;
    /// scripted clients can still pass raw CSS selectors.
    /// </summary>
    internal static string ResolveTarget(JsonNode? args, BrowserState state)
    {
        if (args.Get("ref").AsString() is { } reference)
        {
            return state.RefToSelector(reference);
        }

        if (args.Get("selector").AsString() is { } selector)
        {
            return selector;
        }

        throw new ToolException("Missing 'ref' or 'selector' parameter");
    }

    private static string RequireString(JsonNode? args, string name, string error) =>
        args.Get(name).AsString() ?? throw new ToolException(error);

    /// <summary>Rust's <c>n as usize</c> on a <c>u64</c>: a wider value saturates here rather than wrapping.</summary>
    private static int? ClampToInt(ulong? value) =>
        value is { } n ? (int)Math.Min(n, int.MaxValue) : null;

    /// <summary>Rust's <c>f64 as u64</c>, which saturates at both ends and maps NaN to 0.</summary>
    private static ulong SaturatingU64(double value)
    {
        if (double.IsNaN(value) || value <= 0.0)
        {
            return 0;
        }

        return value >= ulong.MaxValue ? ulong.MaxValue : (ulong)value;
    }

    // ===== core tools =====

    internal static async Task<string> NavigateAsync(JsonNode? args, BrowserState state)
    {
        var url = RequireString(args, "url", "Missing url parameter");
        var waitUntil = args.Get("waitUntil").AsString() ?? "load";

        var condition = WaitUntilExtensions.ParseWaitUntil(waitUntil);
        var ua = state.UserAgent;
        var page = state.PageMut();
        if (ua is not null)
        {
            page.HttpClient.SetUserAgent(ua);
        }

        try
        {
            await page.NavigateWithWaitAsync(url, condition).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        var summary = $"Navigated to {page.UrlString()} — \"{page.Title}\"";
        // DOM changed - invalidate the ref table. Next snapshot will rebuild.
        state.InteractiveRefs.Clear();
        return summary;
    }

    internal static string Snapshot(JsonNode? args, BrowserState state)
    {
        var maxChars = ClampToInt(args.Get("max_chars").AsU64()) ?? McpServer.DefaultTextLimit;
        RebuildInteractiveRefs(state);
        var page = state.PageMut();
        var url = page.UrlString();
        var title = page.Title;

        var bodyText = page.WithDom(dom =>
            TryQuery(dom, "body") is { } body ? TextExtraction.ExtractText(dom, body) : string.Empty)
            ?? string.Empty;

        var refsSummary = state.InteractiveRefs.Count == 0
            ? string.Empty
            : $"\n\n{state.InteractiveRefs.Count.ToString(CultureInfo.InvariantCulture)} interactive element(s) registered. Call browser_interactive_elements to list, or pass `ref` to browser_click/browser_fill/browser_type.";

        var body = TextExtraction.Truncate(bodyText.Trim(), maxChars);
        return $"URL: {url}\nTitle: {title}\n\n{body}{refsSummary}";
    }

    internal static async Task<string> ClickAsync(JsonNode? args, BrowserState state)
    {
        var selector = ResolveTarget(args, state);

        var js = $$"""
            (function(){
                var el = document.querySelector({{McpJson.String(selector)}});
                if (!el) return "error:element not found";
                el.click();
                return "ok";
            })()
            """;

        var result = state.PageMut().Evaluate(js);
        if (result.AsString() == "error:element not found")
        {
            throw new ToolException($"Element not found: {selector}");
        }

        // A click can navigate or rewrite the DOM; the old ref table may no longer
        // match. Conservative: invalidate. Next snapshot rebuilds.
        state.InteractiveRefs.Clear();
        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return $"Clicked '{selector}'";
    }

    internal static async Task<string> FillAsync(JsonNode? args, BrowserState state)
    {
        var selector = ResolveTarget(args, state);
        var value = RequireString(args, "value", "Missing value parameter");

        var js = $$"""
            (function(){
                var el = document.querySelector({{McpJson.String(selector)}});
                if (!el) return "error:element not found";
                globalThis.__obscura_setFieldValue(el, "value", {{McpJson.String(value)}});
                el.dispatchEvent(globalThis.__obscura_markTrusted(new Event("input", {bubbles:true})));
                el.dispatchEvent(globalThis.__obscura_markTrusted(new Event("change", {bubbles:true})));
                return "ok";
            })()
            """;

        var result = state.PageMut().Evaluate(js);
        if (result.AsString() == "error:element not found")
        {
            throw new ToolException($"Element not found: {selector}");
        }

        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return $"Filled '{selector}' with value";
    }

    internal static async Task<string> TypeAsync(JsonNode? args, BrowserState state)
    {
        var selector = ResolveTarget(args, state);
        var text = RequireString(args, "text", "Missing text parameter");

        var js = $$"""
            (function(){
                var el = document.querySelector({{McpJson.String(selector)}});
                if (!el) return "error:element not found";
                globalThis.__obscura_setFieldValue(el, "value", (el.value || "") + {{McpJson.String(text)}});
                el.dispatchEvent(globalThis.__obscura_markTrusted(new Event("input", {bubbles:true})));
                return "ok";
            })()
            """;

        var result = state.PageMut().Evaluate(js);
        if (result.AsString() == "error:element not found")
        {
            throw new ToolException($"Element not found: {selector}");
        }

        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return $"Typed into '{selector}'";
    }

    internal static async Task<string> PressKeyAsync(JsonNode? args, BrowserState state)
    {
        var key = RequireString(args, "key", "Missing key parameter");
        var selector = args.Get("selector").AsString();

        var target = selector is not null
            ? $"document.querySelector({McpJson.String(selector)})"
            : "document";

        var js = $$"""
            (function(){
                var t = {{target}};
                if (!t) return "error:element not found";
                t.dispatchEvent(new KeyboardEvent("keydown", {key:{{McpJson.String(key)}},bubbles:true}));
                t.dispatchEvent(new KeyboardEvent("keyup", {key:{{McpJson.String(key)}},bubbles:true}));
                return "ok";
            })()
            """;

        state.PageMut().Evaluate(js);
        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return $"Pressed key '{key}'";
    }

    internal static string SelectOption(JsonNode? args, BrowserState state)
    {
        var selector = RequireString(args, "selector", "Missing selector parameter");
        var value = RequireString(args, "value", "Missing value parameter");

        var js = $$"""
            (function(){
                var el = document.querySelector({{McpJson.String(selector)}});
                if (!el) return "error:element not found";
                var opts = Array.from(el.options);
                var opt = opts.find(function(o){ return o.value === {{McpJson.String(value)}} || o.text === {{McpJson.String(value)}}; });
                if (!opt) return "error:option not found";
                el.value = opt.value;
                el.dispatchEvent(new Event("change", {bubbles:true}));
                return "ok";
            })()
            """;

        var result = state.PageMut().Evaluate(js);
        return result.AsString() switch
        {
            "error:element not found" => throw new ToolException($"Element not found: {selector}"),
            "error:option not found" => throw new ToolException($"Option not found: {value}"),
            _ => $"Selected '{value}' in '{selector}'",
        };
    }

    internal static async Task<string> EvaluateAsync(JsonNode? args, BrowserState state)
    {
        var expression = RequireString(args, "expression", "Missing expression parameter");

        var result = state.PageMut().Evaluate(expression);
        await state.SettleSyntheticNavigationAsync().ConfigureAwait(false);
        return result.AsString() ?? (result.IsNull() ? "null" : McpJson.SerializePretty(result));
    }

    internal static async Task<string> WaitForAsync(JsonNode? args, BrowserState state)
    {
        var selector = RequireString(args, "selector", "Missing selector parameter");
        var timeoutSecs = SaturatingU64(args.Get("timeout").AsF64() ?? 30.0);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSecs);
        // Exponential backoff: 5 -> 10 -> 20 -> ... -> 200 ms. The old fixed 200ms
        // tick added up to a full poll cycle of latency every time; a selector that
        // appears in 30ms now returns in ~35ms instead of the next 200ms tick.
        ulong tickMs = 5;
        while (true)
        {
            var found = state.PageMut().WithDom(dom => TryQuery(dom, selector) is not null);
            if (found)
            {
                return $"Found '{selector}'";
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new ToolException($"Timeout waiting for '{selector}'");
            }

            await PumpForAsync(state, tickMs).ConfigureAwait(false);
            if (tickMs < 200)
            {
                tickMs = Math.Min(tickMs * 2, 200);
            }
        }
    }

    /// <summary>
    /// <c>tokio::time::timeout(tick, advance_active_page_tasks())</c>: give the page
    /// one pump turn before the poll loop rechecks its predicate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tick is deliberately not enforced. <c>tokio::time::timeout</c> bounds the
    /// wait by dropping the future, which cancels the turn; .NET cannot cancel a
    /// running turn, so bounding the wait here would leave a live event-loop turn
    /// running against the same V8 isolate and DOM that the next iteration's
    /// predicate reads - a data race the Rust code never has. Awaiting the turn is
    /// the same wait in every case except a turn slower than the tick, where the
    /// loop simply notices the deadline one turn later.
    /// </para>
    /// <para>
    /// It costs no responsiveness on an idle page: a turn that reaches idle returns
    /// immediately, and <c>timeout</c> returns as soon as its future completes too,
    /// so both engines re-poll at the same rate.
    /// </para>
    /// </remarks>
    private static Task PumpForAsync(BrowserState state, ulong tickMs)
    {
        _ = tickMs;
        return state.AdvanceActivePageTasksAsync();
    }

    private static Obscura.Dom.NodeId? TryQuery(Obscura.Dom.DomTree dom, string selector) =>
        dom.TryQuerySelector(selector, out var result, out _) ? result : null;

    internal static string NetworkRequests(BrowserState state)
    {
        var page = state.PageMut();
        var events = page.NetworkEvents;

        if (events.Count == 0)
        {
            return "No network requests recorded.";
        }

        var lines = new List<string>(events.Count);
        foreach (var e in events)
        {
            lines.Add(
                $"[{e.Status.ToString(CultureInfo.InvariantCulture)}] {e.Method} {e.Url} ({e.BodySize.ToString(CultureInfo.InvariantCulture)}B)");
        }

        return string.Join("\n", lines);
    }

    internal static string ConsoleMessages(BrowserState state) =>
        state.ConsoleMessages.Count == 0
            ? "No console messages."
            : string.Join("\n", state.ConsoleMessages);

    internal static string Close(BrowserState state)
    {
        // Drop the one live isolate (if any) via suspend_js before clearing, so the
        // map drop disposes no isolate and the LIFO rule holds regardless of the
        // map's ascending drop order (#258).
        foreach (var page in state.Tabs.Values)
        {
            page.SuspendJs();
            page.Dispose();
        }

        state.Tabs.Clear();
        state.ActiveTab = null;
        state.ConsoleMessages.Clear();
        state.InteractiveRefs.Clear();
        return "All browser tabs closed.";
    }

    // ===== Tier 1 agent-UX additions =====

    /// <summary>
    /// Convert the rendered page to Markdown by running the JS-side converter
    /// already used by <c>obscura fetch --dump markdown</c>. More token-dense than
    /// <c>browser_snapshot</c> for content-heavy pages (article bodies, docs sites).
    /// </summary>
    internal static string Markdown(JsonNode? args, BrowserState state)
    {
        var maxChars = ClampToInt(args.Get("max_chars").AsU64()) ?? McpServer.DefaultTextLimit;
        var page = state.PageMut();
        var result = page.Evaluate(MarkdownScript.HtmlToMarkdown);
        var md = result.AsString() ?? string.Empty;
        return TextExtraction.Truncate(md, maxChars);
    }

    /// <summary>
    /// Enumerate every <c>&lt;a href&gt;</c> on the page. One JSON object per line so
    /// the agent can grep / split without round-tripping to a JSON parser.
    /// </summary>
    internal static string Links(JsonNode? args, BrowserState state)
    {
        var limit = ClampToInt(args.Get("limit").AsU64()) ?? 100;
        var internalOnly = args.Get("internal_only").AsBool() ?? false;
        var page = state.PageMut();
        var baseOrigin = TupleOrigin(page.UrlString());

        const string js = """
            (function(){
                var out = [];
                var seen = new Set();
                var as = document.querySelectorAll('a[href]');
                for (var i = 0; i < as.length; i++) {
                    var a = as[i];
                    var href = a.href || '';
                    if (!href || href === '#' || href.startsWith('javascript:')) continue;
                    if (seen.has(href)) continue;
                    seen.add(href);
                    var t = (a.innerText || a.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 200);
                    out.push({text: t, href: href});
                }
                return out;
            })()
            """;
        var val = page.Evaluate(js);
        var arr = val.ArrayOrNull();
        var lines = new List<string>();
        if (arr is not null)
        {
            foreach (var item in arr)
            {
                if (internalOnly)
                {
                    var href = item.Get("href").AsString();
                    var origin = href is null ? null : TupleOrigin(href);
                    if (origin is null || baseOrigin is null
                        || !string.Equals(origin, baseOrigin, StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                if (lines.Count >= limit)
                {
                    break;
                }

                lines.Add(McpJson.Serialize(item));
            }
        }

        return lines.Count == 0 ? "No links found." : string.Join("\n", lines);
    }

    /// <summary>
    /// <c>url::Url::origin()</c> reduced to what a comparison needs: the tuple
    /// origin's serialization for the schemes that have one, and null for an opaque
    /// origin.
    /// </summary>
    /// <remarks>
    /// The <c>url</c> crate gives <c>data:</c>, <c>about:</c> and <c>file:</c> a
    /// fresh opaque origin per parse, and two opaque origins are never equal, so a
    /// null here must never compare equal to anything - including another null.
    /// That is why <c>internal_only</c> filters everything out on an
    /// <c>about:blank</c> page, exactly as the Rust fallback
    /// (<c>Url::parse("about:blank").origin()</c>) does.
    /// </remarks>
    private static string? TupleOrigin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme is not ("http" or "https" or "ws" or "wss" or "ftp"))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// List every interactable element with a stable ref ID, the kind of element,
    /// and a one-line description. Agents pass <c>ref</c> to click/fill/type instead
    /// of crafting selectors. Also assigns <c>data-obscura-ref</c> to each element so
    /// the ref survives until the next navigation.
    /// </summary>
    internal static string InteractiveElements(JsonNode? args, BrowserState state)
    {
        var limit = (long)Math.Min(args.Get("limit").AsU64() ?? 100, long.MaxValue);
        RebuildInteractiveRefs(state);
        if (state.InteractiveRefs.Count == 0)
        {
            return "No interactive elements on this page.";
        }

        var page = state.PageMut();
        var js = $$"""
            (function(){
                var els = document.querySelectorAll('[data-obscura-ref]');
                var out = [];
                for (var i = 0; i < els.length && out.length < {{limit.ToString(CultureInfo.InvariantCulture)}}; i++) {
                    var e = els[i];
                    var label = (e.innerText || e.textContent || e.getAttribute('aria-label') || e.getAttribute('placeholder') || e.getAttribute('value') || e.getAttribute('name') || '').trim().replace(/\s+/g, ' ').slice(0, 80);
                    var role = e.getAttribute('role') || '';
                    var typeAttr = e.getAttribute('type') || '';
                    out.push({
                        ref: e.getAttribute('data-obscura-ref'),
                        tag: e.tagName.toLowerCase(),
                        type: typeAttr,
                        role: role,
                        name: e.getAttribute('name') || '',
                        label: label,
                    });
                }
                return out;
            })()
            """;
        var val = page.Evaluate(js);
        var arr = val.ArrayOrNull();
        var lines = new List<string>();
        if (arr is not null)
        {
            foreach (var item in arr)
            {
                var reference = item.Get("ref").AsString() ?? "?";
                var tag = item.Get("tag").AsString() ?? "?";
                var ty = item.Get("type").AsString() ?? string.Empty;
                var label = item.Get("label").AsString() ?? string.Empty;
                var name = item.Get("name").AsString() ?? string.Empty;
                var role = item.Get("role").AsString() ?? string.Empty;
                var kind = ty.Length != 0 ? $"{tag}[{ty}]"
                    : role.Length != 0 ? $"{tag}[role={role}]"
                    : tag;
                var detail = name.Length != 0
                    ? $" name={TextExtraction.RustDebugString(name)}"
                    : string.Empty;
                lines.Add(
                    $"ref={TextExtraction.PadRight(reference, 5)} {TextExtraction.PadRight(kind, 22)} {TextExtraction.RustDebugString(label)}{detail}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Rebuild the ref table: walk the DOM, find every interactable, assign a stable
    /// <c>data-obscura-ref="eN"</c> attribute, remember the nid for later validation.
    /// Called on every snapshot / interactive-elements call so the agent always sees
    /// fresh refs.
    /// </summary>
    internal static void RebuildInteractiveRefs(BrowserState state)
    {
        state.InteractiveRefs.Clear();
        var page = state.PageMut();
        // Tag every interactable with data-obscura-ref="eN" in DOM order.
        const string tagJs = """
            (function(){
                var sel = 'a[href], button, input:not([type=hidden]), select, textarea, [role=button], [role=link], [role=checkbox], [role=tab], [role=menuitem], [role=option], [onclick], [tabindex]:not([tabindex="-1"])';
                var els = document.querySelectorAll(sel);
                var refs = [];
                for (var i = 0; i < els.length; i++) {
                    var ref = 'e' + (i + 1);
                    els[i].setAttribute('data-obscura-ref', ref);
                    refs.push(ref);
                }
                return refs;
            })()
            """;
        var val = page.Evaluate(tagJs);
        var refs = new List<string>();
        if (val.ArrayOrNull() is { } array)
        {
            foreach (var entry in array)
            {
                if (entry.AsString() is { } text)
                {
                    refs.Add(text);
                }
            }
        }

        // Map ref -> nid via a second pass so ref_to_selector can sanity-check.
        foreach (var reference in refs)
        {
            var selector = $"[data-obscura-ref=\"{reference}\"]";
            var current = state.PageMut();
            var nid = current.WithDom(dom => TryQuery(dom, selector));
            if (nid is { } found)
            {
                state.InteractiveRefs[reference] = found;
            }
        }
    }

    internal static async Task<string> BackAsync(BrowserState state)
    {
        // We track simple page history on the Page itself; navigate to the entry
        // before the cursor.
        var page = state.PageMut();
        if (page.History.Count < 2 || page.HistoryIndex == 0)
        {
            throw new ToolException("No previous page in history.");
        }

        var prevIdx = page.HistoryIndex - 1;
        var url = page.History[prevIdx];
        page.SetHistoryIndex(prevIdx);
        var stash = (History: new List<string>(page.History), Index: page.HistoryIndex);
        try
        {
            await page.NavigateWithWaitAsync(url, WaitUntil.DomContentLoaded).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        RestoreHistory(state.PageMut(), stash.History, stash.Index);
        state.InteractiveRefs.Clear();
        return $"Back to {url}";
    }

    internal static async Task<string> ForwardAsync(BrowserState state)
    {
        var page = state.PageMut();
        if (page.HistoryIndex + 1 >= page.History.Count)
        {
            throw new ToolException("No forward page in history.");
        }

        var nextIdx = page.HistoryIndex + 1;
        var url = page.History[nextIdx];
        page.SetHistoryIndex(nextIdx);
        var stash = (History: new List<string>(page.History), Index: page.HistoryIndex);
        try
        {
            await page.NavigateWithWaitAsync(url, WaitUntil.DomContentLoaded).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        RestoreHistory(state.PageMut(), stash.History, stash.Index);
        state.InteractiveRefs.Clear();
        return $"Forward to {url}";
    }

    /// <summary>
    /// <c>page.history = stash.0; page.history_index = stash.1;</c>. Back / forward
    /// navigate through the normal path, which appends to history, so the stash is
    /// put back to keep the cursor where the agent moved it.
    /// </summary>
    private static void RestoreHistory(Page page, List<string> history, int index)
    {
        page.History.Clear();
        page.History.AddRange(history);
        page.HistoryIndex = index;
    }

    internal static async Task<string> ReloadAsync(BrowserState state)
    {
        var url = state.PageMut().UrlString();
        if (string.Equals(url, "about:blank", StringComparison.Ordinal))
        {
            throw new ToolException("Nothing to reload.");
        }

        try
        {
            await state.PageMut().NavigateWithWaitAsync(url, WaitUntil.DomContentLoaded)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        state.InteractiveRefs.Clear();
        return $"Reloaded {url}";
    }

    internal static string GetCookies(JsonNode? args, BrowserState state)
    {
        var domainFilter = args.Get("domain").AsString();
        var cookies = state.Context.CookieJar.GetAllCookies();
        var lines = new List<string>();
        foreach (var c in cookies)
        {
            if (domainFilter is not null
                && !string.Equals(c.Domain, CookieJar.CanonicalDomain(domainFilter), StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(McpJson.Serialize(new JsonObject
            {
                ["name"] = c.Name,
                ["value"] = c.Value,
                ["domain"] = c.Domain,
                ["path"] = c.Path,
                ["secure"] = c.Secure,
                ["http_only"] = c.HttpOnly,
            }));
        }

        return lines.Count == 0 ? "No cookies." : string.Join("\n", lines);
    }

    internal static string SetCookie(JsonNode? args, BrowserState state)
    {
        var name = RequireString(args, "name", "Missing name parameter");
        var value = RequireString(args, "value", "Missing value parameter");
        var domain = RequireString(args, "domain", "Missing domain parameter");
        var path = args.Get("path").AsString() ?? "/";
        var secure = args.Get("secure").AsBool() ?? false;
        var httpOnly = args.Get("http_only").AsBool() ?? false;
        var cookie = new CookieInfo
        {
            Name = name,
            Value = value,
            Domain = domain,
            Path = path,
            Secure = secure,
            HttpOnly = httpOnly,
            SameSite = string.Empty,
            Expires = null,
        };
        state.Context.CookieJar.SetCookiesFromCdp([cookie]);
        return $"Set cookie {name} on {domain}{path}";
    }

    internal static string ClearCookies(BrowserState state)
    {
        state.Context.CookieJar.Clear();
        return "Cleared all cookies.";
    }

    internal static async Task<string> WaitForTextAsync(JsonNode? args, BrowserState state)
    {
        var needle = RequireString(args, "text", "Missing text parameter");
        var timeoutSecs = SaturatingU64(args.Get("timeout").AsF64() ?? 30.0);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSecs);
        var escaped = McpJson.String(needle);
        var js = $$"""
            (function(){
                var t = (document.body && (document.body.innerText || document.body.textContent)) || '';
                return t.indexOf({{escaped}}) >= 0;
            })()
            """;
        // Exponential backoff like browser_wait_for (see comment there).
        ulong tickMs = 5;
        while (true)
        {
            var found = state.PageMut().Evaluate(js).AsBool() ?? false;
            if (found)
            {
                return $"Found text {TextExtraction.RustDebugString(needle)}";
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new ToolException($"Timeout waiting for text {TextExtraction.RustDebugString(needle)}");
            }

            await PumpForAsync(state, tickMs).ConfigureAwait(false);
            if (tickMs < 200)
            {
                tickMs = Math.Min(tickMs * 2, 200);
            }
        }
    }
}
