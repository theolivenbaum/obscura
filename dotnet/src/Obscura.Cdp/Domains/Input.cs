using System.Globalization;
using System.Text.Json.Nodes;

namespace Obscura.Cdp.Domains;

/// <summary>CDP <c>Input</c> domain: mouse, keyboard and touch dispatch.</summary>
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
            + "var t = document.activeElement;"
            + "if (!t || (t.localName !== 'input' && t.localName !== 'textarea')) return;"
            + "var ins = " + literal + ";"
            + "var v = t.value || '';"
            + "var s = t.selectionStart, e = t.selectionEnd;"
            + "if (s == null) {"
            + "globalThis.__obscura_setFieldValue(t, 'value', v + ins);"
            + "} else {"
            + "s = Math.max(0, Math.min(s, v.length));"
            + "e = (e == null) ? s : Math.max(0, Math.min(e, v.length));"
            + "var lo = Math.min(s, e), hi = Math.max(s, e);"
            + "globalThis.__obscura_setFieldValue(t, 'value', v.slice(0, lo) + ins + v.slice(hi));"
            + "var caret = lo + ins.length;"
            + "t.setSelectionRange(caret, caret);"
            + "}"
            + "t.dispatchEvent(globalThis.__obscura_markTrusted(new Event('input', {bubbles:true})));"
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
        + "var t = document.activeElement;"
        + "if (!t || (t.localName !== 'input' && t.localName !== 'textarea')) return;"
        + "var v = t.value || '';"
        + "var s = t.selectionStart, e = t.selectionEnd;"
        + "if (s == null) {"
        + "globalThis.__obscura_setFieldValue(t, 'value', v.slice(0, -1));"
        + "} else {"
        + "s = Math.max(0, Math.min(s, v.length));"
        + "e = (e == null) ? s : Math.max(0, Math.min(e, v.length));"
        + "if (s !== e) {"
        + "var lo = Math.min(s, e), hi = Math.max(s, e);"
        + "globalThis.__obscura_setFieldValue(t, 'value', v.slice(0, lo) + v.slice(hi));"
        + "t.setSelectionRange(lo, lo);"
        + "} else if (s > 0) {"
        + "globalThis.__obscura_setFieldValue(t, 'value', v.slice(0, s - 1) + v.slice(s));"
        + "t.setSelectionRange(s - 1, s - 1);"
        + "}"
        + "}"
        + "t.dispatchEvent(globalThis.__obscura_markTrusted(new Event('input', {bubbles:true})));"
        + "})()";

    private const string EnterJs = "(function() {"
        + "var target = document.activeElement;"
        + "if (!target) return;"
        + "target.dispatchEvent(globalThis.__obscura_markTrusted("
        + "new KeyboardEvent('keypress', {bubbles:true,key:'Enter',code:'Enter'})));"
        + "if (target.localName === 'textarea') {"
        + "globalThis.__obscura_setFieldValue(target, 'value', (target.value || '') + '\\n');"
        + "target.dispatchEvent(globalThis.__obscura_markTrusted(new Event('input', {bubbles:true})));"
        + "} else {"
        + "var form = target.form || (target.closest && target.closest('form'));"
        + "if (form) { try { if (typeof form.requestSubmit === 'function') { form.requestSubmit(); }"
        + " else { form.submit(); } } catch(e) {} }"
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
                        page.Evaluate(MousePressedJs(
                            x, y, buttonCode, buttons, clickCount, altKey, ctrlKey, metaKey, shiftKey));
                    }
                }
                else if (eventType == "mouseReleased")
                {
                    (string PageId, string FrameId, string Url)? movedFrame = null;
                    if (ctx.GetSessionPageMut(sessionId) is { } page)
                    {
                        page.Evaluate(MouseReleasedJs(
                            x, y, buttonCode, clickCount, altKey, ctrlKey, metaKey, shiftKey));
                        bool moved;
                        try
                        {
                            moved = await page.ProcessPendingNavigationAsync().ConfigureAwait(false);
                        }
                        catch (Obscura.Browser.PageException error)
                        {
                            return DomainResult.Err(error.Message);
                        }

                        // Fork: a single page app answers a click by routing itself, with no
                        // document fetch. The client still has to be told the frame moved, or the
                        // click looks like it did nothing.
                        if (moved)
                        {
                            movedFrame = (page.Id, page.FrameId, page.UrlString());
                        }
                    }

                    if (movedFrame is { } frame)
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
                else if (eventType == "mouseWheel")
                {
                    double deltaX = parameters.Get("deltaX").AsF64() ?? 0.0;
                    double deltaY = parameters.Get("deltaY").AsF64() ?? 0.0;
                    if (ctx.GetSessionPageMut(sessionId) is { } page)
                    {
                        page.Evaluate(MouseWheelJs(
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
                ctx.GetSessionPageMut(sessionId)?.Evaluate(InsertTextJs(text));
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
                            // Escape backslash BEFORE single-quote (as the text path below does) so
                            // a key like "\" - Chrome's backslash key - doesn't escape the closing
                            // quote and produce a syntax error that drops the event.
                            page.Evaluate("(function() {"
                                + "var target = document.activeElement || document.body;"
                                + "var evt = globalThis.__obscura_markTrusted(new KeyboardEvent('keydown', "
                                + "{bubbles:true,cancelable:true,key:" + JsStr(key)
                                + ",code:" + JsStr(code) + "}));"
                                + "target.dispatchEvent(evt);"
                                + "})()");

                            if (text.Length != 0 && text != "\r" && text != "\n")
                            {
                                page.Evaluate(InsertTextJs(text));
                            }

                            if (key == "Enter")
                            {
                                // In a textarea Enter inserts a newline; in input fields it submits
                                // the containing form. Real Chrome distinguishes these two and we
                                // should too: previously every Enter tried to submit the nearest
                                // form even from a textarea.
                                page.Evaluate(EnterJs);
                            }

                            if (key == "Backspace")
                            {
                                page.Evaluate(BackspaceJs);
                            }

                            break;
                        }

                        case "keyUp":
                            page.Evaluate("(function() {"
                                + "var target = document.activeElement || document.body;"
                                + "var evt = globalThis.__obscura_markTrusted(new KeyboardEvent('keyup', "
                                + "{bubbles:true,key:" + JsStr(key) + ",code:" + JsStr(code) + "}));"
                                + "target.dispatchEvent(evt);"
                                + "})()");
                            break;

                        case "char":
                            if (text.Length != 0)
                            {
                                page.Evaluate(InsertTextJs(text));
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
            + "var target = (document.elementFromPoint && document.elementFromPoint(" + sx + "," + sy
            + ")) || globalThis.__obscura_click_target || document.activeElement || document.body;"
            + "if (!target) return;"
            + "globalThis.__obscura_click_target = target;"
            + "globalThis.__obscura_mouse_down = {target:target,button:" + button
            + ",clickCount:" + detail + "};"
            + "var evt = globalThis.__obscura_markTrusted(new MouseEvent('mousedown', "
            + "{bubbles:true,cancelable:true,view:globalThis,clientX:" + sx + ",clientY:" + sy
            + ",button:" + button + ",buttons:" + mask + ",detail:" + detail
            + ",altKey:" + Bool(altKey) + ",ctrlKey:" + Bool(ctrlKey)
            + ",metaKey:" + Bool(metaKey) + ",shiftKey:" + Bool(shiftKey) + "}));"
            + "target.dispatchEvent(evt);"
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
            + "var target = (document.elementFromPoint && document.elementFromPoint(" + sx + "," + sy
            + ")) || globalThis.__obscura_click_target || document.activeElement || document.body;"
            + "if (!target) return;"
            + "var down = globalThis.__obscura_mouse_down;"
            + "globalThis.__obscura_mouse_down = null;"
            + "var evt = globalThis.__obscura_markTrusted(new MouseEvent('mouseup', "
            + "{bubbles:true,cancelable:true,view:globalThis,clientX:" + sx + ",clientY:" + sy
            + ",button:" + button + ",buttons:0,detail:" + detail + modifiers + "}));"
            + "target.dispatchEvent(evt);"
            + "if (!down || down.button !== " + button + " || " + button + " !== 0) return;"
            + "var clickTarget = down.target;"
            + "while (clickTarget && clickTarget !== target && "
            + "!(clickTarget.contains && clickTarget.contains(target))) {"
            + "clickTarget = clickTarget.parentElement;"
            + "}"
            + "if (!clickTarget) return;"
            + "var tag = clickTarget.tagName;"
            + "var type = (clickTarget.getAttribute && clickTarget.getAttribute('type') || '').toLowerCase();"
            + "if (globalThis.__obscura_isDisabled(clickTarget)) return;"
            + "var checkable = tag === 'INPUT' && (type === 'checkbox' || type === 'radio');"
            + "var oldChecked = checkable ? !!clickTarget.checked : false;"
            + "var oldIndeterminate = checkable ? !!clickTarget.indeterminate : false;"
            + "var radioStates = null;"
            + "if (checkable && type === 'radio') {"
            + "var radioName = clickTarget.getAttribute('name') || '';"
            + "if (radioName) {"
            + "var candidates = document.querySelectorAll('input');"
            + "radioStates = [];"
            + "for (var ri = 0; ri < candidates.length; ri++) {"
            + "var radio = candidates[ri];"
            + "if ((radio.getAttribute('type') || '').toLowerCase() !== 'radio' || "
            + "(radio.getAttribute('name') || '') !== radioName || radio.form !== clickTarget.form) continue;"
            + "radioStates.push([radio, !!radio.checked]);"
            + "if (radio !== clickTarget) radio.checked = false;"
            + "}"
            + "}"
            + "clickTarget.checked = true;"
            + "} else if (checkable) {"
            + "clickTarget.checked = !oldChecked;"
            + "clickTarget.indeterminate = false;"
            + "}"
            + "var click = globalThis.__obscura_markTrusted(new MouseEvent('click', "
            + "{bubbles:true,cancelable:true,view:globalThis,clientX:" + sx + ",clientY:" + sy
            + ",button:0,buttons:0,detail:" + detail + modifiers + "}));"
            + "var cancelled = !clickTarget.dispatchEvent(click);"
            + "if (cancelled) {"
            + "if (radioStates) {"
            + "for (var rr = 0; rr < radioStates.length; rr++) radioStates[rr][0].checked = radioStates[rr][1];"
            + "} else if (checkable) { clickTarget.checked = oldChecked; "
            + "clickTarget.indeterminate = oldIndeterminate; }"
            + "return;"
            + "}"
            + "if (checkable && clickTarget.checked !== oldChecked) {"
            + "try { clickTarget.dispatchEvent(globalThis.__obscura_markTrusted("
            + "new Event('input', {bubbles:true}))); } catch(e) {}"
            + "try { clickTarget.dispatchEvent(globalThis.__obscura_markTrusted("
            + "new Event('change', {bubbles:true}))); } catch(e) {}"
            + "return;"
            + "}"
            + "var labelHost = tag === 'LABEL' ? clickTarget : "
            + "(clickTarget.closest ? clickTarget.closest('label') : null);"
            + "var interactiveHost = globalThis.__obscura_interactiveHost(clickTarget);"
            + "if (labelHost && !(interactiveHost && labelHost.contains(interactiveHost))) {"
            + "var ctl = globalThis.__obscura_labeledControl(labelHost);"
            + "if (ctl && ctl !== clickTarget && globalThis.__obscura_activateLabel(labelHost, ctl, true)) { return; }"
            + "}"
            + "var link = clickTarget.closest ? clickTarget.closest('a[href]') : null;"
            + "if (!link && tag === 'A' && clickTarget.getAttribute('href')) link = clickTarget;"
            + "if (link) {"
            + "var href = link.getAttribute('href');"
            + "if (href && !href.startsWith('#') && !href.startsWith('javascript:')) location.assign(href);"
            + "} else if (tag === 'BUTTON' && type !== 'button' && type !== 'reset') {"
            + "var form = clickTarget.closest ? clickTarget.closest('form') : null;"
            + "if (form) { try { if (typeof form.requestSubmit === 'function') { form.requestSubmit(clickTarget); }"
            + " else { form.submit(clickTarget); } } catch(e) {} }"
            + "} else if (tag === 'INPUT' && (type === 'submit' || type === 'image')) {"
            + "var form2 = clickTarget.closest ? clickTarget.closest('form') : null;"
            + "if (form2) { try { if (typeof form2.requestSubmit === 'function') { form2.requestSubmit(clickTarget); }"
            + " else { form2.submit(clickTarget); } } catch(e) {} }"
            + "} else if (" + detail + " >= 3 && (tag === 'INPUT' || tag === 'TEXTAREA')) {"
            + "var len = clickTarget.value ? clickTarget.value.length : 0;"
            + "if (clickTarget.setSelectionRange) clickTarget.setSelectionRange(0, len);"
            + "else { clickTarget.selectionStart = 0; clickTarget.selectionEnd = len; }"
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
            + "var target = (document.elementFromPoint && document.elementFromPoint(" + sx + "," + sy
            + ")) || document.body || document.documentElement;"
            + "if (!target) return;"
            + "var wheel = globalThis.__obscura_markTrusted(new WheelEvent('wheel', "
            + "{bubbles:true,cancelable:true,view:globalThis,clientX:" + sx + ",clientY:" + sy
            + ",deltaX:" + dx + ",deltaY:" + dy + ",deltaMode:0"
            + ",altKey:" + Bool(altKey) + ",ctrlKey:" + Bool(ctrlKey)
            + ",metaKey:" + Bool(metaKey) + ",shiftKey:" + Bool(shiftKey) + "}));"
            + "if (!target.dispatchEvent(wheel)) return;"
            + "var dx = " + dx + ", dy = " + dy + ";"
            + "var root = document.scrollingElement || document.documentElement || document.body;"
            + "var scrollTarget = null;"
            + "var el = target;"
            + "while (el && el.nodeType === 1 && el !== root && el !== document.body && "
            + "el !== document.documentElement) {"
            + "var maxX = Math.max(0, (el.scrollWidth || 0) - (el.clientWidth || 0));"
            + "var maxY = Math.max(0, (el.scrollHeight || 0) - (el.clientHeight || 0));"
            + "var style = null;"
            + "try { style = getComputedStyle(el); } catch (_e) {}"
            + "var ox = style ? (style.overflowX || style.overflow || '') : '';"
            + "var oy = style ? (style.overflowY || style.overflow || '') : '';"
            + "var allowX = ox === 'auto' || ox === 'scroll' || ox === 'overlay';"
            + "var allowY = oy === 'auto' || oy === 'scroll' || oy === 'overlay';"
            + "var consumesX = allowX && ((dx > 0 && el.scrollLeft < maxX) || (dx < 0 && el.scrollLeft > 0));"
            + "var consumesY = allowY && ((dy > 0 && el.scrollTop < maxY) || (dy < 0 && el.scrollTop > 0));"
            + "if (consumesX || consumesY) { scrollTarget = el; break; }"
            + "el = el.parentElement;"
            + "}"
            + "if (!scrollTarget) scrollTarget = root;"
            + "if (scrollTarget === root && root && typeof root.scrollBy === 'function') {"
            + "var beforeX = root.scrollLeft, beforeY = root.scrollTop;"
            + "root.scrollBy(dx, dy);"
            + "if (root.scrollLeft !== beforeX || root.scrollTop !== beforeY) setTimeout(function() {"
            + "try { document.dispatchEvent(new Event('scroll', {bubbles:false})); } catch (_e) {}"
            + "try { globalThis.dispatchEvent(new Event('scroll', {bubbles:false})); } catch (_e) {}"
            + "}, 0);"
            + "} else if (scrollTarget && typeof scrollTarget.scrollBy === 'function') scrollTarget.scrollBy(dx, dy);"
            + "})()";
    }
}
