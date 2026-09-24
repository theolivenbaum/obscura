using System.Globalization;
using System.Text.Json.Nodes;

namespace PocketCalculator.Cdp.Domains;

/// <summary>CDP <c>Input</c> domain: mouse, keyboard and touch dispatch.</summary>
/// <remarks>
/// Every snippet here runs through <c>Page.EvaluateHost</c> and reaches the shim's input
/// helpers as <c>__obscura_host.markTrusted</c>, <c>setFieldValue</c>, <c>isDisabled</c>,
/// <c>labeledControl</c>, <c>interactiveHost</c>, <c>activateLabel</c> and
/// <c>pointer.down</c>. DEVIATION from crates/obscura-cdp/src/domains/input.rs, which
/// names them as page-visible <c>globalThis.__obscura_*</c> globals, so page script could
/// mark its own events trusted and aim the click a real mouseup produces.
/// <para>
/// Every DOM call, event constructor and built-in the snippets use comes from
/// <c>__obscura_host.dom</c>, as bootstrap.js left them (SECURITY.md L10). The Rust
/// snippets call <c>document.elementFromPoint</c>, <c>el.dispatchEvent</c>,
/// <c>new MouseEvent</c> and the rest on whatever the page has put there, so a page could
/// swallow or redirect a real click, or build the trusted event itself.
/// </para>
/// </remarks>
public static class Input
{
    /// <summary>
    /// Embed a string as a JS string literal (double-quoted, with backslash, quotes, and control
    /// characters escaped) for interpolation into generated KeyboardEvent scripts.
    /// </summary>
    /// <remarks>
    /// A plain <c>replace('\'', ...)</c> misses newline / NUL / U+2028-29, which terminate the
    /// literal and silently drop the event.
    /// </remarks>
    public static string JsStr(string value) => CdpJson.String(value);

    /// <summary>
    /// Insert <paramref name="text"/> at the caret, replacing any non-collapsed selection the way a
    /// real browser does when you type over selected text (for example after a triple-click
    /// select-all).
    /// </summary>
    /// <remarks>
    /// selectionStart is null during ordinary typing, so the legacy append path is kept when no
    /// selection is tracked.
    /// <para>
    /// The text is embedded as a JSON string literal rather than escaped by hand into single
    /// quotes. JSON string syntax is a subset of JavaScript's, so this covers the quote and the
    /// backslash of issue #433 and the control characters they left out: a newline inside a
    /// single-quoted literal is a syntax error, so the whole snippet was dropped and nothing was
    /// inserted. obscura-mcp already builds its typing snippet this way.
    /// </para>
    /// </remarks>
    private static string InsertTextJs(string text)
    {
        string literal = JsStr(text);
        return "(function() {"
            + "var h = __obscura_host.dom;"
            + "var t = h.activeElement();"
            + "var tag = h.localName(t);"
            + "if (!t || (tag !== 'input' && tag !== 'textarea')) return;"
            + "var ins = " + literal + ";"
            + "var v = h.get(t, 'value') || '';"
            + "var s = h.get(t, 'selectionStart'), e = h.get(t, 'selectionEnd');"
            + "if (s == null) {"
            + "__obscura_host.setFieldValue(t, 'value', v + ins);"
            + "} else {"
            + "s = h.max(0, h.min(s, v.length));"
            + "e = (e == null) ? s : h.max(0, h.min(e, v.length));"
            + "var lo = h.min(s, e), hi = h.max(s, e);"
            + "__obscura_host.setFieldValue(t, 'value', h.slice(v, 0, lo) + ins + h.slice(v, hi));"
            + "var caret = lo + ins.length;"
            + "h.call(t, 'setSelectionRange', [caret, caret]);"
            + "}"
            + "h.dispatch(t, h.event('Event', 'input', {__proto__:null,bubbles:true}, true));"
            + "})()";
    }

    /// <summary>
    /// Backspace deletes the selected range when there is one, so the common "triple-click to
    /// select-all, then Backspace to clear" pattern works.
    /// </summary>
    /// <remarks>
    /// With a collapsed caret it removes the character before the caret, and with no selection
    /// tracked it falls back to trimming the last character (legacy).
    /// </remarks>
    private const string BackspaceJs = "(function() {"
        + "var h = __obscura_host.dom;"
        + "var t = h.activeElement();"
        + "var tag = h.localName(t);"
        + "if (!t || (tag !== 'input' && tag !== 'textarea')) return;"
        + "var v = h.get(t, 'value') || '';"
        + "var s = h.get(t, 'selectionStart'), e = h.get(t, 'selectionEnd');"
        + "if (s == null) {"
        + "__obscura_host.setFieldValue(t, 'value', h.slice(v, 0, -1));"
        + "} else {"
        + "s = h.max(0, h.min(s, v.length));"
        + "e = (e == null) ? s : h.max(0, h.min(e, v.length));"
        + "if (s !== e) {"
        + "var lo = h.min(s, e), hi = h.max(s, e);"
        + "__obscura_host.setFieldValue(t, 'value', h.slice(v, 0, lo) + h.slice(v, hi));"
        + "h.call(t, 'setSelectionRange', [lo, lo]);"
        + "} else if (s > 0) {"
        + "__obscura_host.setFieldValue(t, 'value', h.slice(v, 0, s - 1) + h.slice(v, s));"
        + "h.call(t, 'setSelectionRange', [s - 1, s - 1]);"
        + "}"
        + "}"
        + "h.dispatch(t, h.event('Event', 'input', {__proto__:null,bubbles:true}, true));"
        + "})()";

    private const string EnterJs = "(function() {"
        + "var h = __obscura_host.dom;"
        + "var target = h.activeElement();"
        + "if (!target) return;"
        + "h.dispatch(target, h.event('KeyboardEvent', 'keypress', "
        + "{__proto__:null,bubbles:true,key:'Enter',code:'Enter'}, true));"
        + "if (h.localName(target) === 'textarea') {"
        + "__obscura_host.setFieldValue(target, 'value', (h.get(target, 'value') || '') + '\\n');"
        + "h.dispatch(target, h.event('Event', 'input', {__proto__:null,bubbles:true}, true));"
        + "} else {"
        + "var form = h.get(target, 'form') || h.closest(target, 'form');"
        + "if (form) { try { if (h.has(form, 'requestSubmit')) { h.call(form, 'requestSubmit', []); }"
        + " else { h.call(form, 'submit', []); } } catch(e) {} }"
        + "}"
        + "})()";

    public static int MouseButtonCode(string button) => button switch
    {
        "middle" => 1,
        "right" => 2,
        "back" => 3,
        "forward" => 4,
        _ => 0,
    };

    public static ulong MouseButtonMask(string button) => button switch
    {
        "right" => 2,
        "middle" => 4,
        "back" => 8,
        "forward" => 16,
        "none" => 0,
        _ => 1,
    };

    /// <summary>CDP Input.Modifier: Alt=1, Ctrl=2, Meta=4, Shift=8.</summary>
    public static (bool Alt, bool Ctrl, bool Meta, bool Shift) ModifierFlags(ulong modifiers) => (
        (modifiers & 1) != 0,
        (modifiers & 2) != 0,
        (modifiers & 4) != 0,
        (modifiers & 8) != 0);

    /// <summary>
    /// Rust's <c>Display</c> for <c>f64</c> as the <c>format!</c> interpolations in the generated
    /// snippets produce it: the shortest decimal that round-trips, never exponential.
    /// </summary>
    private static string Num(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        string shortest = value.ToString("R", CultureInfo.InvariantCulture);
        if (shortest.Contains('E', StringComparison.Ordinal))
        {
            // Rust never prints an exponent for `{}`; expand to positional notation.
            shortest = decimal.TryParse(
                shortest,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out decimal expanded)
                ? expanded.ToString(CultureInfo.InvariantCulture)
                : value.ToString("F20", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        }

        return shortest;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        switch (method)
        {
            case "dispatchMouseEvent":
            {
                string eventType = parameters.Get("type").AsString() ?? string.Empty;
                double x = parameters.Get("x").AsF64() ?? 0.0;
                double y = parameters.Get("y").AsF64() ?? 0.0;
                string button = parameters.Get("button").AsString() ?? "left";
                int buttonCode = MouseButtonCode(button);
                ulong buttons = parameters.Get("buttons").AsU64()
                    ?? MouseButtonMask(button);
                ulong clickCount = parameters.Get("clickCount").AsU64() ?? 1;
                ulong modifiers = parameters.Get("modifiers").AsU64() ?? 0;
                (bool altKey, bool ctrlKey, bool metaKey, bool shiftKey) = ModifierFlags(modifiers);

                if (eventType == "mousePressed")
                {
                    if (ctx.GetSessionPageMut(sessionId) is { } page)
                    {
                        // A mouse press is activation-triggering input (HTML "user
                        // activation"), so a navigation it causes is user-activated.
                        page.NoteUserActivation();
                        page.EvaluateHost(MousePressedJs(
                            x, y, buttonCode, buttons, clickCount, altKey, ctrlKey, metaKey, shiftKey));
                    }
                }
                else if (eventType == "mouseReleased")
                {
                    (string PageId, string FrameId, string Url, PocketCalculator.Browser.PageNavigationOutcome Outcome)?
                        movedFrame = null;
                    if (ctx.GetSessionPageMut(sessionId) is { } page)
                    {
                        page.NoteUserActivation();
                        page.EvaluateHost(MouseReleasedJs(
                            x, y, buttonCode, clickCount, altKey, ctrlKey, metaKey, shiftKey));
                        PocketCalculator.Browser.PageNavigationOutcome moved;
                        try
                        {
                            moved = await page.ProcessPendingNavigationOutcomeAsync()
                                .ConfigureAwait(false);
                        }
                        catch (PocketCalculator.Browser.PageException error)
                        {
                            return DomainResult.Err(error.Message);
                        }

                        // Fork: a single page app answers a click by routing itself, with no
                        // document fetch. The client still has to be told the frame moved, or the
                        // click looks like it did nothing.
                        if (moved.Navigated)
                        {
                            movedFrame = (page.Id, page.FrameId, page.UrlString(), moved);
                        }
                    }

                    if (movedFrame is { } frame)
                    {
                        if (frame.Outcome.IsSameDocument)
                        {
                            // A click a router answered with pushState kept the document, so
                            // announcing frameNavigated would retire the client's execution
                            // context for a page that never reloaded.
                            Page.EmitSameDocumentNavigation(
                                ctx,
                                sessionId,
                                frame.FrameId,
                                frame.Url,
                                frame.PageId,
                                frame.Outcome.NavigationType);
                        }
                        else
                        {
                            string loaderId =
                                ctx.CurrentLoaderIds.TryGetValue(frame.PageId, out string? existing)
                                    ? existing
                                    : "loader-blank-" + frame.PageId;
                            ctx.PendingEvents.Add(new CdpEvent
                            {
                                Method = "Page.frameNavigated",
                                Params = new JsonObject
                                {
                                    ["frame"] = Page.FrameValue(
                                        frame.FrameId,
                                        null,
                                        loaderId,
                                        frame.Url,
                                        "text/html"),
                                    ["type"] = "Navigation",
                                },
                                SessionId = sessionId ?? string.Empty,
                            });
                        }
                    }
                }
                else if (eventType == "mouseWheel")
                {
                    double deltaX = parameters.Get("deltaX").AsF64() ?? 0.0;
                    double deltaY = parameters.Get("deltaY").AsF64() ?? 0.0;
                    if (ctx.GetSessionPageMut(sessionId) is { } page)
                    {
                        page.EvaluateHost(MouseWheelJs(
                            x, y, deltaX, deltaY, altKey, ctrlKey, metaKey, shiftKey));
                    }
                }

                return DomainResult.Empty();
            }

            // Chrome's Input.insertText: Playwright's fill() focuses the field in page and then
            // types the whole value through this one call (#577).
            case "insertText":
            {
                string text = parameters.Get("text").AsString() ?? string.Empty;
                ctx.GetSessionPageMut(sessionId)?.EvaluateHost(InsertTextJs(text));
                return DomainResult.Empty();
            }

            case "dispatchKeyEvent":
            {
                string eventType = parameters.Get("type").AsString() ?? string.Empty;
                string key = parameters.Get("key").AsString() ?? string.Empty;
                string code = parameters.Get("code").AsString() ?? string.Empty;
                string text = parameters.Get("text").AsString() ?? string.Empty;

                if (ctx.GetSessionPageMut(sessionId) is { } page)
                {
                    switch (eventType)
                    {
                        case "keyDown":
                        case "rawKeyDown":
                        {
                            // Chromium: every keydown but Escape gives the page activation.
                            if (key != "Escape")
                            {
                                page.NoteUserActivation();
                            }

                            // Escape backslash BEFORE single-quote (as the text path below does) so
                            // a key like "\" - Chrome's backslash key - doesn't escape the closing
                            // quote and produce a syntax error that drops the event.
                            page.EvaluateHost("(function() {"
                                + "var h = __obscura_host.dom;"
                                + "var target = h.activeElement() || h.body();"
                                + "var evt = h.event('KeyboardEvent', 'keydown', "
                                + "{__proto__:null,bubbles:true,cancelable:true,key:" + JsStr(key)
                                + ",code:" + JsStr(code) + "}, true);"
                                + "h.dispatch(target, evt);"
                                + "})()");

                            if (text.Length != 0 && text != "\r" && text != "\n")
                            {
                                page.EvaluateHost(InsertTextJs(text));
                            }

                            if (key == "Enter")
                            {
                                // In a textarea Enter inserts a newline; in input fields it submits
                                // the containing form. Real Chrome distinguishes these two and we
                                // should too: previously every Enter tried to submit the nearest
                                // form even from a textarea.
                                page.EvaluateHost(EnterJs);
                            }

                            if (key == "Backspace")
                            {
                                page.EvaluateHost(BackspaceJs);
                            }

                            break;
                        }

                        case "keyUp":
                            page.EvaluateHost("(function() {"
                                + "var h = __obscura_host.dom;"
                                + "var target = h.activeElement() || h.body();"
                                + "var evt = h.event('KeyboardEvent', 'keyup', "
                                + "{__proto__:null,bubbles:true,key:" + JsStr(key) + ",code:" + JsStr(code) + "}, true);"
                                + "h.dispatch(target, evt);"
                                + "})()");
                            break;

                        case "char":
                            if (text.Length != 0)
                            {
                                page.EvaluateHost(InsertTextJs(text));
                                // Pump the event loop so Angular change detection picks up the input.
                                await page.SettleAsync(50).ConfigureAwait(false);
                            }

                            break;

                        default:
                            break;
                    }
                }

                return DomainResult.Empty();
            }

            case "dispatchTouchEvent":
            case "setIgnoreInputEvents":
                return DomainResult.Empty();

            default:
                return DomainResult.Err($"Unknown Input method: {method}");
        }
    }

    private static string MousePressedJs(
        double x,
        double y,
        int buttonCode,
        ulong buttons,
        ulong clickCount,
        bool altKey,
        bool ctrlKey,
        bool metaKey,
        bool shiftKey)
    {
        string sx = Num(x);
        string sy = Num(y);
        string button = buttonCode.ToString(CultureInfo.InvariantCulture);
        string mask = buttons.ToString(CultureInfo.InvariantCulture);
        string detail = clickCount.ToString(CultureInfo.InvariantCulture);
        return "(function() {"
            + "var h = __obscura_host.dom;"
            + "var target = h.elementFromPoint(" + sx + "," + sy
            + ") || __obscura_host.clickTarget.get() || h.activeElement() || h.body();"
            + "if (!target) return;"
            + "__obscura_host.clickTarget.set(target);"
            + "__obscura_host.pointer.down = {__proto__:null,target:target,button:" + button
            + ",clickCount:" + detail + "};"
            + "var evt = h.event('MouseEvent', 'mousedown', "
            + "{__proto__:null,bubbles:true,cancelable:true,view:globalThis,clientX:" + sx + ",clientY:" + sy
            + ",button:" + button + ",buttons:" + mask + ",detail:" + detail
            + ",altKey:" + Bool(altKey) + ",ctrlKey:" + Bool(ctrlKey)
            + ",metaKey:" + Bool(metaKey) + ",shiftKey:" + Bool(shiftKey) + "}, true);"
            + "h.dispatch(target, evt);"
            + "})()";
    }

    private static string MouseReleasedJs(
        double x,
        double y,
        int buttonCode,
        ulong clickCount,
        bool altKey,
        bool ctrlKey,
        bool metaKey,
        bool shiftKey)
    {
        string sx = Num(x);
        string sy = Num(y);
        string button = buttonCode.ToString(CultureInfo.InvariantCulture);
        string detail = clickCount.ToString(CultureInfo.InvariantCulture);
        string modifiers = ",altKey:" + Bool(altKey) + ",ctrlKey:" + Bool(ctrlKey)
            + ",metaKey:" + Bool(metaKey) + ",shiftKey:" + Bool(shiftKey);
        return "(function() {"
            + "var h = __obscura_host.dom;"
            + "var target = h.elementFromPoint(" + sx + "," + sy
            + ") || __obscura_host.clickTarget.get() || h.activeElement() || h.body();"
            + "if (!target) return;"
            + "var down = __obscura_host.pointer.down;"
            + "__obscura_host.pointer.down = null;"
            + "var evt = h.event('MouseEvent', 'mouseup', "
            + "{__proto__:null,bubbles:true,cancelable:true,view:globalThis,clientX:" + sx + ",clientY:" + sy
            + ",button:" + button + ",buttons:0,detail:" + detail + modifiers + "}, true);"
            + "h.dispatch(target, evt);"
            + "if (!down || down.button !== " + button + " || " + button + " !== 0) return;"
            + "var clickTarget = down.target;"
            + "while (clickTarget && clickTarget !== target && !h.contains(clickTarget, target)) {"
            + "clickTarget = h.parentElement(clickTarget);"
            + "}"
            + "if (!clickTarget) return;"
            + "var tag = h.tagName(clickTarget);"
            + "var type = h.lower(h.getAttribute(clickTarget, 'type') || '');"
            + "if (__obscura_host.isDisabled(clickTarget)) return;"
            + "var checkable = tag === 'INPUT' && (type === 'checkbox' || type === 'radio');"
            + "var oldChecked = checkable ? !!h.get(clickTarget, 'checked') : false;"
            + "var oldIndeterminate = checkable ? !!h.get(clickTarget, 'indeterminate') : false;"
            + "var radioStates = null;"
            + "if (checkable && type === 'radio') {"
            + "var radioName = h.getAttribute(clickTarget, 'name') || '';"
            + "if (radioName) {"
            + "var candidates = h.querySelectorAll(h.document(), 'input');"
            + "radioStates = [];"
            + "var ownForm = h.get(clickTarget, 'form');"
            + "for (var ri = 0; ri < candidates.length; ri++) {"
            + "var radio = candidates[ri];"
            + "if (h.lower(h.getAttribute(radio, 'type') || '') !== 'radio' || "
            + "(h.getAttribute(radio, 'name') || '') !== radioName || h.get(radio, 'form') !== ownForm) continue;"
            + "radioStates[radioStates.length] = [radio, !!h.get(radio, 'checked')];"
            + "if (radio !== clickTarget) h.set(radio, 'checked', false);"
            + "}"
            + "}"
            + "h.set(clickTarget, 'checked', true);"
            + "} else if (checkable) {"
            + "h.set(clickTarget, 'checked', !oldChecked);"
            + "h.set(clickTarget, 'indeterminate', false);"
            + "}"
            + "var click = h.event('MouseEvent', 'click', "
            + "{__proto__:null,bubbles:true,cancelable:true,view:globalThis,clientX:" + sx + ",clientY:" + sy
            + ",button:0,buttons:0,detail:" + detail + modifiers + "}, true);"
            + "var cancelled = !h.dispatch(clickTarget, click);"
            + "if (cancelled) {"
            + "if (radioStates) {"
            + "for (var rr = 0; rr < radioStates.length; rr++) h.set(radioStates[rr][0], 'checked', radioStates[rr][1]);"
            + "} else if (checkable) { h.set(clickTarget, 'checked', oldChecked); "
            + "h.set(clickTarget, 'indeterminate', oldIndeterminate); }"
            + "return;"
            + "}"
            + "if (checkable && h.get(clickTarget, 'checked') !== oldChecked) {"
            + "try { h.dispatch(clickTarget, h.event('Event', 'input', {__proto__:null,bubbles:true}, true)); } catch(e) {}"
            + "try { h.dispatch(clickTarget, h.event('Event', 'change', {__proto__:null,bubbles:true}, true)); } catch(e) {}"
            + "return;"
            + "}"
            + "var labelHost = tag === 'LABEL' ? clickTarget : h.closest(clickTarget, 'label');"
            + "var interactiveHost = __obscura_host.interactiveHost(clickTarget);"
            + "if (labelHost && !(interactiveHost && h.contains(labelHost, interactiveHost))) {"
            + "var ctl = __obscura_host.labeledControl(labelHost);"
            + "if (ctl && ctl !== clickTarget && __obscura_host.activateLabel(labelHost, ctl, true)) { return; }"
            + "}"
            + "var link = h.closest(clickTarget, 'a[href]');"
            + "if (!link && tag === 'A' && h.getAttribute(clickTarget, 'href')) link = clickTarget;"
            + "if (link) {"
            + "var href = h.getAttribute(link, 'href');"
            // Deviation from crates/obscura-cdp/src/domains/input.rs, which skips a
            // fragment href here: it did so because location.assign used to tear the
            // document down, so an in-page link would have rebooted the realm. Fragment
            // navigation is same-document now, and skipping it made a real mouse click on
            // an SPA's own link do nothing at all. Same fix as the el.click() path in
            // bootstrap.js. The navigation goes through the shim's own location path
            // (__obscura_host.navigate), not the page-replaceable location.assign.
            + "if (href && h.slice(href, 0, 11) !== 'javascript:') __obscura_host.navigate(href);"
            + "} else if (tag === 'BUTTON' && type !== 'button' && type !== 'reset') {"
            + "var form = h.closest(clickTarget, 'form');"
            + "if (form) { try { if (h.has(form, 'requestSubmit')) { h.call(form, 'requestSubmit', [clickTarget]); }"
            + " else { h.call(form, 'submit', [clickTarget]); } } catch(e) {} }"
            + "} else if (tag === 'INPUT' && (type === 'submit' || type === 'image')) {"
            + "var form2 = h.closest(clickTarget, 'form');"
            + "if (form2) { try { if (h.has(form2, 'requestSubmit')) { h.call(form2, 'requestSubmit', [clickTarget]); }"
            + " else { h.call(form2, 'submit', [clickTarget]); } } catch(e) {} }"
            + "} else if (" + detail + " >= 3 && (tag === 'INPUT' || tag === 'TEXTAREA')) {"
            + "var value = h.get(clickTarget, 'value');"
            + "var len = value ? value.length : 0;"
            + "if (h.has(clickTarget, 'setSelectionRange')) h.call(clickTarget, 'setSelectionRange', [0, len]);"
            + "else { h.set(clickTarget, 'selectionStart', 0); h.set(clickTarget, 'selectionEnd', len); }"
            + "}"
            + "})()";
    }

    private static string MouseWheelJs(
        double x,
        double y,
        double deltaX,
        double deltaY,
        bool altKey,
        bool ctrlKey,
        bool metaKey,
        bool shiftKey)
    {
        string sx = Num(x);
        string sy = Num(y);
        string dx = Num(deltaX);
        string dy = Num(deltaY);
        return "(function() {"
            + "var h = __obscura_host.dom;"
            + "var body = h.body(), html = h.documentElement();"
            + "var target = h.elementFromPoint(" + sx + "," + sy + ") || body || html;"
            + "if (!target) return;"
            + "var wheel = h.event('WheelEvent', 'wheel', "
            + "{__proto__:null,bubbles:true,cancelable:true,view:globalThis,clientX:" + sx + ",clientY:" + sy
            + ",deltaX:" + dx + ",deltaY:" + dy + ",deltaMode:0"
            + ",altKey:" + Bool(altKey) + ",ctrlKey:" + Bool(ctrlKey)
            + ",metaKey:" + Bool(metaKey) + ",shiftKey:" + Bool(shiftKey) + "}, true);"
            + "if (!h.dispatch(target, wheel)) return;"
            + "var dx = " + dx + ", dy = " + dy + ";"
            + "var root = h.scrollingElement() || html || body;"
            + "var scrollTarget = null;"
            + "var el = target;"
            + "while (el && h.nodeType(el) === 1 && el !== root && el !== body && el !== html) {"
            + "var maxX = h.max(0, (h.get(el, 'scrollWidth') || 0) - (h.get(el, 'clientWidth') || 0));"
            + "var maxY = h.max(0, (h.get(el, 'scrollHeight') || 0) - (h.get(el, 'clientHeight') || 0));"
            + "var style = null;"
            + "try { style = h.computedStyle(el); } catch (_e) {}"
            + "var ox = style ? (style.overflowX || style.overflow || '') : '';"
            + "var oy = style ? (style.overflowY || style.overflow || '') : '';"
            + "var allowX = ox === 'auto' || ox === 'scroll' || ox === 'overlay';"
            + "var allowY = oy === 'auto' || oy === 'scroll' || oy === 'overlay';"
            + "var left = h.get(el, 'scrollLeft'), top = h.get(el, 'scrollTop');"
            + "var consumesX = allowX && ((dx > 0 && left < maxX) || (dx < 0 && left > 0));"
            + "var consumesY = allowY && ((dy > 0 && top < maxY) || (dy < 0 && top > 0));"
            + "if (consumesX || consumesY) { scrollTarget = el; break; }"
            + "el = h.parentElement(el);"
            + "}"
            + "if (!scrollTarget) scrollTarget = root;"
            + "if (scrollTarget === root && root && h.has(root, 'scrollBy')) {"
            + "var beforeX = h.get(root, 'scrollLeft'), beforeY = h.get(root, 'scrollTop');"
            + "h.call(root, 'scrollBy', [dx, dy]);"
            + "if (h.get(root, 'scrollLeft') !== beforeX || h.get(root, 'scrollTop') !== beforeY) h.setTimeout(function() {"
            + "try { h.dispatch(h.document(), h.event('Event', 'scroll', {__proto__:null,bubbles:false}, false)); } catch (_e) {}"
            + "try { h.dispatch(globalThis, h.event('Event', 'scroll', {__proto__:null,bubbles:false}, false)); } catch (_e) {}"
            + "}, 0);"
            + "} else if (scrollTarget && h.has(scrollTarget, 'scrollBy')) h.call(scrollTarget, 'scrollBy', [dx, dy]);"
            + "})()";
    }
}
