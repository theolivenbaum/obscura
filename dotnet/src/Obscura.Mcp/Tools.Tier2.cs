using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Obscura.Browser;
using Obscura.Net;

namespace Obscura.Mcp;

internal static partial class Tools
{
    // ===== Tier 2 agent-UX additions =====

    /// <summary>
    /// Describe every <c>&lt;form&gt;</c> on the page. For each form, list its
    /// action, method, and every field (input/select/textarea) with its name, type,
    /// current value, and any visible label text. Agents call this to understand a
    /// form's shape before filling it.
    /// </summary>
    internal static string DetectForms(BrowserState state)
    {
        var page = state.PageMut();
        const string js = """
            (function(){
                var forms = document.querySelectorAll('form');
                var out = [];
                for (var i = 0; i < forms.length; i++) {
                    var f = forms[i];
                    var fields = [];
                    var inputs = f.querySelectorAll('input, select, textarea, button');
                    for (var j = 0; j < inputs.length; j++) {
                        var el = inputs[j];
                        var tag = el.tagName.toLowerCase();
                        var type = (el.getAttribute('type') || (tag === 'input' ? 'text' : tag)).toLowerCase();
                        if (tag === 'input' && type === 'hidden') continue;
                        var name = el.getAttribute('name') || '';
                        var label = '';
                        if (el.id) {
                            var lab = document.querySelector('label[for="' + el.id + '"]');
                            if (lab) label = (lab.innerText || lab.textContent || '').trim();
                        }
                        if (!label) label = el.getAttribute('aria-label') || el.getAttribute('placeholder') || '';
                        var opts = null;
                        if (tag === 'select') {
                            opts = [];
                            var os = el.querySelectorAll('option');
                            for (var k = 0; k < os.length; k++) {
                                opts.push({ value: os[k].value, text: (os[k].textContent || '').trim() });
                            }
                        }
                        fields.push({
                            tag: tag,
                            type: type,
                            name: name,
                            value: el.value || '',
                            checked: el.checked || false,
                            required: el.required || false,
                            label: label.trim().slice(0, 100),
                            ref: el.getAttribute('data-obscura-ref') || null,
                            options: opts,
                        });
                    }
                    out.push({
                        index: i,
                        id: f.id || '',
                        name: f.getAttribute('name') || '',
                        action: f.action || '',
                        method: (f.method || 'get').toLowerCase(),
                        fields: fields,
                    });
                }
                return out;
            })()
            """;
        var val = page.Evaluate(js);
        if (val.IsNull())
        {
            return "No forms found.";
        }

        return McpJson.SerializePretty(val);
    }

    /// <summary>
    /// Fill multiple fields in one call. Each entry: <c>{ref|selector, value,
    /// type?}</c>. <c>type='text'</c> (default) sets value, <c>'check'</c> /
    /// <c>'uncheck'</c> toggles a checkbox, <c>'select'</c> picks an option by value
    /// or visible text. Optional <c>submit_ref</c> / <c>submit_selector</c> clicks
    /// after filling.
    /// </summary>
    internal static string FillForm(JsonNode? args, BrowserState state)
    {
        var fields = args.Get("fields").ArrayOrNull() ?? throw new ToolException("Missing fields array");
        var snapshot = new List<JsonNode?>(fields.Count);
        foreach (var field in fields)
        {
            snapshot.Add(field?.DeepClone());
        }

        var filled = 0u;
        var errors = new List<string>();
        foreach (var field in snapshot)
        {
            var value = field.Get("value").AsString() ?? string.Empty;
            var kind = field.Get("type").AsString() ?? "text";
            string selector;
            try
            {
                selector = ResolveTarget(field, state);
            }
            catch (ToolException error)
            {
                errors.Add(error.Message);
                continue;
            }

            var sel = McpJson.String(selector);
            var val = McpJson.String(value);
            var js = kind switch
            {
                "check" => $$"""
                    (function(){
                        var el = document.querySelector({{sel}});
                        if (!el) return "error:not found";
                        globalThis.__obscura_setFieldValue(el, 'checked', true);
                        el.dispatchEvent(globalThis.__obscura_markTrusted(new Event('input', {bubbles:true})));
                        el.dispatchEvent(globalThis.__obscura_markTrusted(new Event('change', {bubbles:true})));
                        return "ok";
                    })()
                    """,
                "uncheck" => $$"""
                    (function(){
                        var el = document.querySelector({{sel}});
                        if (!el) return "error:not found";
                        globalThis.__obscura_setFieldValue(el, 'checked', false);
                        el.dispatchEvent(globalThis.__obscura_markTrusted(new Event('input', {bubbles:true})));
                        el.dispatchEvent(globalThis.__obscura_markTrusted(new Event('change', {bubbles:true})));
                        return "ok";
                    })()
                    """,
                "select" => $$"""
                    (function(){
                        var el = document.querySelector({{sel}});
                        if (!el) return "error:not found";
                        var want = {{val}};
                        var matched = false;
                        for (var i = 0; i < el.options.length; i++) {
                            var o = el.options[i];
                            if (o.value === want || (o.textContent || '').trim() === want) {
                                el.selectedIndex = i;
                                matched = true;
                                break;
                            }
                        }
                        if (!matched) return "error:no matching option";
                        el.dispatchEvent(globalThis.__obscura_markTrusted(new Event('input', {bubbles:true})));
                        el.dispatchEvent(globalThis.__obscura_markTrusted(new Event('change', {bubbles:true})));
                        return "ok";
                    })()
                    """,
                _ => $$"""
                    (function(){
                        var el = document.querySelector({{sel}});
                        if (!el) return "error:not found";
                        globalThis.__obscura_setFieldValue(el, 'value', {{val}});
                        el.dispatchEvent(globalThis.__obscura_markTrusted(new Event('input', {bubbles:true})));
                        el.dispatchEvent(globalThis.__obscura_markTrusted(new Event('change', {bubbles:true})));
                        return "ok";
                    })()
                    """,
            };
            var res = state.PageMut().Evaluate(js);
            switch (res.AsString())
            {
                case "ok":
                    filled++;
                    break;
                case { } message:
                    errors.Add($"{selector}: {message}");
                    break;
                default:
                    errors.Add($"{selector}: unknown error");
                    break;
            }
        }

        // Optional submit click
        string? submitTarget = null;
        if (args.Has("submit_ref") || args.Has("submit_selector"))
        {
            var pseudo = new JsonObject
            {
                ["ref"] = args.Get("submit_ref")?.DeepClone(),
                ["selector"] = args.Get("submit_selector")?.DeepClone(),
            };
            try
            {
                submitTarget = ResolveTarget(pseudo, state);
            }
            catch (ToolException)
            {
                submitTarget = null;
            }
        }

        if (submitTarget is { } target)
        {
            var js = $$"""
                (function(){
                    var el = document.querySelector({{McpJson.String(target)}});
                    if (!el) return "error:not found";
                    el.click();
                    return "ok";
                })()
                """;
            state.PageMut().Evaluate(js);
            state.InteractiveRefs.Clear();
        }

        var count = filled.ToString(CultureInfo.InvariantCulture);
        return errors.Count == 0
            ? $"Filled {count} fields."
            : $"Filled {count} fields. Errors: {string.Join("; ", errors)}";
    }

    /// <summary>
    /// Scroll the page (or an element) by direction + amount, or scroll an element
    /// into view. Used to trigger infinite-scroll loaders or to reach off-viewport
    /// content. Returns the new scroll position.
    /// </summary>
    internal static string Scroll(JsonNode? args, BrowserState state)
    {
        var direction = args.Get("direction").AsString() ?? "down";
        var amount = args.Get("amount").AsF64();

        // Element scroll-into-view path
        if (args.Has("ref") || args.Has("selector"))
        {
            var selector = ResolveTarget(args, state);
            var js = $$"""
                (function(){
                    var el = document.querySelector({{McpJson.String(selector)}});
                    if (!el) return "error:not found";
                    el.scrollIntoView({behavior:'instant', block:'center'});
                    return JSON.stringify({x: window.scrollX, y: window.scrollY});
                })()
                """;
            var elementResult = state.PageMut().Evaluate(js);
            if (elementResult.AsString() == "error:not found")
            {
                throw new ToolException($"Element not found: {selector}");
            }

            return $"Scrolled element into view. {elementResult.AsString() ?? string.Empty}";
        }

        // Page-level scroll. Also dispatch a 'scroll' event so infinite-scroll
        // handlers fire (we don't have a real layout engine, so the window.scrollY
        // value won't change but the event is what matters).
        var amt = amount ?? 720.0;
        var scrollJs = $$"""
            (function(){
                var dir = {{McpJson.String(direction)}};
                var amt = {{McpJson.Display(amt)}};
                switch (dir) {
                    case 'top': window.scrollTo(0, 0); break;
                    case 'bottom': window.scrollTo(0, document.body.scrollHeight); break;
                    case 'up': window.scrollBy(0, -amt); break;
                    case 'down': window.scrollBy(0, amt); break;
                    case 'left': window.scrollBy(-amt, 0); break;
                    case 'right': window.scrollBy(amt, 0); break;
                }
                try { window.dispatchEvent(new Event('scroll', {bubbles:true})); } catch(e) {}
                try { document.dispatchEvent(new Event('scroll', {bubbles:true})); } catch(e) {}
                return JSON.stringify({x: window.scrollX, y: window.scrollY, max_y: document.body.scrollHeight, viewport_h: window.innerHeight});
            })()
            """;
        var res = state.PageMut().Evaluate(scrollJs);
        // A scroll can reveal new DOM (infinite scroll); invalidate refs.
        state.InteractiveRefs.Clear();
        return $"Scrolled {direction}. {res.AsString() ?? string.Empty}";
    }

    internal static string GetAttribute(JsonNode? args, BrowserState state)
    {
        var selector = ResolveTarget(args, state);
        var attr = RequireString(args, "attribute", "Missing attribute parameter");
        var attrLiteral = McpJson.String(attr);
        var js = $$"""
            (function(){
                var el = document.querySelector({{McpJson.String(selector)}});
                if (!el) return null;
                var v = el.getAttribute({{attrLiteral}});
                if (v === null && {{attrLiteral}} === 'value') v = el.value || '';
                return v == null ? '' : v;
            })()
            """;
        var res = state.PageMut().Evaluate(js);
        if (res.IsNull())
        {
            throw new ToolException($"Element not found: {selector}");
        }

        return res.AsString() ?? string.Empty;
    }

    internal static string Count(JsonNode? args, BrowserState state)
    {
        var selector = RequireString(args, "selector", "Missing selector parameter");
        var js = $"document.querySelectorAll({McpJson.String(selector)}).length";
        var res = state.PageMut().Evaluate(js);
        // V8 numbers come back as f64 even when they are integer-valued; as_u64
        // returns None for f64 in serde_json, so coerce via f64.
        var n = res.AsU64() ?? (res.AsF64() is { } f ? SaturatingU64(f) : 0);
        return n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Extract structured data: <c>schema</c> is a map of field name to CSS selector.
    /// Suffix the selector with <c>@attr</c> to read an attribute instead of text.
    /// Suffix the field name with <c>[]</c> to return an array (queries all matching
    /// elements rather than the first).
    /// </summary>
    internal static string Extract(JsonNode? args, BrowserState state)
    {
        var schema = args.Get("schema").ObjectOrNull() ?? throw new ToolException("Missing schema object");
        var schemaJson = McpJson.Serialize(schema);
        var js = $$"""
            (function(){
                var schema = {{schemaJson}};
                var out = {};
                for (var key in schema) {
                    if (!Object.prototype.hasOwnProperty.call(schema, key)) continue;
                    var spec = schema[key];
                    var is_array = key.endsWith('[]');
                    var name = is_array ? key.slice(0, -2) : key;
                    // Selector may end with `@attr` to read an attribute.
                    var attr = null;
                    var sel = spec;
                    var at = spec.lastIndexOf('@');
                    if (at > 0 && spec.indexOf(' ', at) < 0) {
                        attr = spec.slice(at + 1);
                        sel = spec.slice(0, at);
                    }
                    var get = function(el) {
                        if (!el) return null;
                        if (attr) return el.getAttribute(attr) || '';
                        return ((el.innerText || el.textContent) || '').trim();
                    };
                    if (is_array) {
                        var els = document.querySelectorAll(sel);
                        var arr = [];
                        for (var i = 0; i < els.length; i++) arr.push(get(els[i]));
                        out[name] = arr;
                    } else {
                        out[name] = get(document.querySelector(sel));
                    }
                }
                return out;
            })()
            """;
        var res = state.PageMut().Evaluate(js);
        return McpJson.SerializePretty(res);
    }

    internal static async Task<string> TabNewAsync(JsonNode? args, BrowserState state)
    {
        var url = args.Get("url").AsString();
        var id = state.NewTab();
        if (url is null)
        {
            return $"Opened {id} (about:blank).";
        }

        var ua = state.UserAgent;
        var page = state.PageMut();
        if (ua is not null)
        {
            page.HttpClient.SetUserAgent(ua);
        }

        try
        {
            await page.NavigateWithWaitAsync(url, WaitUntil.DomContentLoaded).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        return $"Opened {id} and navigated to {page.UrlString()}";
    }

    internal static string TabList(BrowserState state)
    {
        if (state.Tabs.Count == 0)
        {
            return "No tabs open.";
        }

        var lines = new List<string>(state.Tabs.Count);
        foreach (var (id, page) in state.Tabs)
        {
            var active = string.Equals(id, state.ActiveTab, StringComparison.Ordinal) ? "*" : " ";
            var url = page.UrlString();
            var title = page.Title.Replace('\n', ' ');
            lines.Add($"{active} {id}  {url}  \"{title}\"");
        }

        return string.Join("\n", lines);
    }

    internal static string TabSwitch(JsonNode? args, BrowserState state)
    {
        var tabId = RequireString(args, "tab_id", "Missing tab_id parameter");
        if (!state.Tabs.ContainsKey(tabId))
        {
            throw new ToolException($"No such tab: {tabId}");
        }

        state.ActiveTab = tabId;
        state.InteractiveRefs.Clear();
        return $"Active tab: {tabId}";
    }

    internal static string TabClose(JsonNode? args, BrowserState state)
    {
        var tabId = args.Get("tab_id").AsString() ?? state.ActiveTab
            ?? throw new ToolException("No tab to close");
        if (!state.CloseTab(tabId))
        {
            throw new ToolException($"No such tab: {tabId}");
        }

        if (string.Equals(state.ActiveTab, tabId, StringComparison.Ordinal))
        {
            // Promote some remaining tab to active, if any.
            state.ActiveTab = state.Tabs.Count == 0 ? null : state.Tabs.Keys.First();
            state.InteractiveRefs.Clear();
        }

        return state.ActiveTab is { } remaining
            ? $"Closed {tabId}. Active tab now {remaining}."
            : $"Closed {tabId}. No tabs remain.";
    }

    // ===== Tier 3 agent-UX additions =====

    /// <summary>
    /// Substring search in visible page text. Returns each match with N chars of
    /// surrounding context so the agent can locate the section without pulling the
    /// whole page into its window.
    /// </summary>
    /// <remarks>
    /// Offsets and slice bounds are UTF-8 byte offsets, as they are in Rust, because
    /// the reported <c>offset</c> is part of the tool's output and the char-boundary
    /// snapping below only makes sense in UTF-8.
    /// </remarks>
    internal static string Search(JsonNode? args, BrowserState state)
    {
        var query = RequireString(args, "query", "Missing query parameter");
        var caseSensitive = args.Get("case_sensitive").AsBool() ?? false;
        var limit = ClampToInt(args.Get("limit").AsU64()) ?? 10;
        var context = ClampToInt(args.Get("context_chars").AsU64()) ?? 80;

        var page = state.PageMut();
        var bodyText = page.WithDom(dom =>
            TryQuery(dom, "body") is { } body ? TextExtraction.ExtractText(dom, body) : string.Empty)
            ?? string.Empty;

        var body = Encoding.UTF8.GetBytes(bodyText);
        var haystack = caseSensitive ? body : Encoding.UTF8.GetBytes(bodyText.ToLowerInvariant());
        var needle = Encoding.UTF8.GetBytes(caseSensitive ? query : query.ToLowerInvariant());

        var matches = new List<JsonNode?>();
        var idx = 0;
        while (true)
        {
            var pos = IndexOf(haystack, needle, idx);
            if (pos < 0)
            {
                break;
            }

            var abs = pos;
            var start = Math.Max(0, abs - context);
            var end = Math.Min(abs + needle.Length + context, body.Length);
            // start/end are byte offsets derived from char counts and needle.len(),
            // so they can land inside a multi-byte (CJK) character. Snap to char
            // boundaries before slicing (#257).
            start = Math.Min(start, body.Length);
            while (start > 0 && !IsCharBoundary(body, start))
            {
                start--;
            }

            while (end < body.Length && !IsCharBoundary(body, end))
            {
                end++;
            }

            // Trim inward to the nearest whitespace so snippets start/end on words.
            var (wsIndex, wsLength) = LastWhitespace(body, start);
            if (wsIndex >= 0)
            {
                start = wsIndex + wsLength;
            }

            var forward = FirstWhitespace(body, end);
            if (forward >= 0)
            {
                end += forward;
            }

            var snippet = end >= start && end <= body.Length
                ? Encoding.UTF8.GetString(body, start, end - start).Trim().Replace('\n', ' ')
                : string.Empty;
            matches.Add(new JsonObject
            {
                ["offset"] = JsonExt.Int(abs),
                ["snippet"] = snippet,
            });
            idx = abs + needle.Length;
            if (matches.Count >= limit)
            {
                break;
            }
        }

        if (matches.Count == 0)
        {
            return $"No matches for {TextExtraction.RustDebugString(query)}.";
        }

        var rendered = new List<string>(matches.Count);
        foreach (var match in matches)
        {
            rendered.Add(McpJson.Serialize(match));
        }

        return $"{matches.Count.ToString(CultureInfo.InvariantCulture)} match(es). {string.Join("\n", rendered)}";
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        if (from > haystack.Length)
        {
            return -1;
        }

        var index = haystack.AsSpan(from).IndexOf(needle);
        return index < 0 ? -1 : from + index;
    }

    private static bool IsCharBoundary(byte[] utf8, int index) =>
        index >= utf8.Length || (utf8[index] & 0xC0) != 0x80;

    /// <summary>
    /// <c>body[..end].rfind(char::is_whitespace)</c> plus the matched character's
    /// UTF-8 length, so the caller can step past it.
    /// </summary>
    private static (int Index, int Length) LastWhitespace(byte[] utf8, int end)
    {
        for (var i = Math.Min(end, utf8.Length) - 1; i >= 0; i--)
        {
            if (!IsCharBoundary(utf8, i))
            {
                continue;
            }

            if (System.Text.Rune.DecodeFromUtf8(utf8.AsSpan(i, utf8.Length - i), out var rune, out var consumed)
                == System.Buffers.OperationStatus.Done
                && i + consumed <= end
                && System.Text.Rune.IsWhiteSpace(rune))
            {
                return (i, consumed);
            }
        }

        return (-1, 0);
    }

    /// <summary><c>body[start..].find(char::is_whitespace)</c>, relative to start.</summary>
    private static int FirstWhitespace(byte[] utf8, int start)
    {
        for (var i = Math.Max(start, 0); i < utf8.Length; i++)
        {
            if (!IsCharBoundary(utf8, i))
            {
                continue;
            }

            if (System.Text.Rune.DecodeFromUtf8(utf8.AsSpan(i, utf8.Length - i), out var rune, out _)
                == System.Buffers.OperationStatus.Done
                && System.Text.Rune.IsWhiteSpace(rune))
            {
                return i - start;
            }
        }

        return -1;
    }

    /// <summary>
    /// Export full session state: cookies + localStorage + sessionStorage for every
    /// origin the page knows about. Agents stash this between runs to skip a login
    /// flow.
    /// </summary>
    internal static string StorageState(BrowserState state)
    {
        var cookies = new JsonArray();
        foreach (var c in state.Context.CookieJar.GetAllCookies())
        {
            cookies.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["value"] = c.Value,
                ["domain"] = c.Domain,
                ["path"] = c.Path,
                ["secure"] = c.Secure,
                ["http_only"] = c.HttpOnly,
                ["same_site"] = c.SameSite,
                ["expires"] = c.Expires is { } expires ? JsonExt.Int(expires) : null,
            });
        }

        // Pull localStorage + sessionStorage for the current page's origin.
        const string storageJs = """
            (function(){
                var ls = [], ss = [];
                try { for (var i = 0; i < localStorage.length; i++) { var k = localStorage.key(i); ls.push([k, localStorage.getItem(k)]); } } catch(e) {}
                try { for (var j = 0; j < sessionStorage.length; j++) { var k2 = sessionStorage.key(j); ss.push([k2, sessionStorage.getItem(k2)]); } } catch(e) {}
                return { origin: location.origin || '', localStorage: ls, sessionStorage: ss };
            })()
            """;
        var storage = state.ActiveTab is not null ? state.PageMut().Evaluate(storageJs) : null;
        var origins = new JsonArray();
        if (storage.IsObject())
        {
            origins.Add(storage!.DeepClone());
        }

        var output = new JsonObject
        {
            ["cookies"] = cookies,
            ["origins"] = origins,
        };
        return McpJson.SerializePretty(output);
    }

    internal static string SetStorageState(JsonNode? args, BrowserState state)
    {
        if (!args.Has("state"))
        {
            throw new ToolException("Missing state object");
        }

        var s = args.Get("state");
        var applied = 0u;
        // Cookies
        if (s.Get("cookies").ArrayOrNull() is { } cookies)
        {
            var parsed = new List<CookieInfo>();
            foreach (var c in cookies)
            {
                if (c.Get("name").AsString() is not { } name
                    || c.Get("value").AsString() is not { } value
                    || c.Get("domain").AsString() is not { } domain)
                {
                    continue;
                }

                parsed.Add(new CookieInfo
                {
                    Name = name,
                    Value = value,
                    Domain = domain,
                    Path = c.Get("path").AsString() ?? "/",
                    Secure = c.Get("secure").AsBool() ?? false,
                    HttpOnly = c.Get("http_only").AsBool() ?? false,
                    SameSite = c.Get("same_site").AsString() ?? string.Empty,
                    Expires = c.Get("expires").AsI64(),
                });
            }

            applied += (uint)parsed.Count;
            state.Context.CookieJar.SetCookiesFromCdp(parsed);
        }

        // Storage (per origin). Only applies if there's an active page; we restore
        // on whatever origin is currently loaded, which usually matches because
        // agents navigate before restoring state.
        if (state.ActiveTab is not null && s.Get("origins").ArrayOrNull() is { } origins)
        {
            foreach (var originEntry in origins)
            {
                var snippets = new List<string>();
                applied += CollectStorageSnippets(originEntry, "localStorage", snippets);
                applied += CollectStorageSnippets(originEntry, "sessionStorage", snippets);
                if (snippets.Count != 0)
                {
                    state.PageMut().Evaluate(string.Join("\n", snippets));
                }
            }
        }

        return $"Restored {applied.ToString(CultureInfo.InvariantCulture)} state entries.";
    }

    private static uint CollectStorageSnippets(JsonNode? originEntry, string area, List<string> snippets)
    {
        if (originEntry.Get(area).ArrayOrNull() is not { } pairs)
        {
            return 0;
        }

        var applied = 0u;
        foreach (var pair in pairs)
        {
            if (pair.Get(0).AsString() is not { } key || pair.Get(1).AsString() is not { } value)
            {
                continue;
            }

            snippets.Add(
                $"try {{ {area}.setItem({McpJson.String(key)},{McpJson.String(value)}); }} catch(e) {{}};");
            applied++;
        }

        return applied;
    }
}
