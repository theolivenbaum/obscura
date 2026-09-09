using System.Globalization;
using System.Text.Json.Nodes;

using Obscura.Browser;
using Obscura.Js.Runtime;

using BrowserPage = Obscura.Browser.Page;

namespace Obscura.Cdp.Domains;

/// <summary>
/// The CDP <c>Runtime</c> domain: evaluate, callFunctionOn, property walks,
/// console/exception plumbing and bindings.
/// </summary>
public static class Runtime
{
    /// <summary>Chrome's protocolTimeout, used when a command does not name one.</summary>
    private const ulong DefaultCommandTimeoutMs = 30_000;

    internal static CdpEvent ExecutionContextCreatedEvent(
        ExecutionContextRecord context,
        string? sessionId) =>
        new()
        {
            Method = "Runtime.executionContextCreated",
            Params = new JsonObject
            {
                ["context"] = new JsonObject
                {
                    ["id"] = context.Id,
                    ["origin"] = context.Origin,
                    ["name"] = context.WorldName,
                    ["uniqueId"] = context.UniqueId,
                    ["auxData"] = new JsonObject
                    {
                        ["isDefault"] = context.IsDefault,
                        ["type"] = context.IsDefault ? "default" : "isolated",
                        ["frameId"] = context.FrameId,
                    },
                },
            },
            SessionId = sessionId,
        };

    /// <summary>
    /// Whether a binding name is a plain JS identifier and therefore safe to interpolate
    /// into the generated shim / teardown scripts.
    /// </summary>
    /// <remarks>
    /// Chromium bindings are identifiers; anything else (quotes, brackets, spaces,
    /// operators) could break out of the surrounding string literal and inject arbitrary
    /// JS into the page. <c>Runtime.addBinding</c> always enforced this, but
    /// <c>Runtime.removeBinding</c> did not, so a crafted name escaped
    /// <c>delete globalThis['{name}']</c> and ran in the page context. Both handlers now
    /// share this guard.
    /// </remarks>
    internal static bool IsValidBindingName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '$')
            {
                return false;
            }
        }

        return !char.IsAsciiDigit(name[0]);
    }

    /// <summary>
    /// Drain pending JS-initiated navigation (form.submit, location.assign, etc), then
    /// emit the same CDP nav-event sequence Page.navigate emits so Puppeteer's
    /// waitForNavigation / Playwright's wait_for_url resolves.
    /// </summary>
    /// <remarks>
    /// Without this, in-page navigations look like Runtime.evaluate finishing to clients
    /// and they hang waiting for a frameNavigated that never fires.
    /// </remarks>
    private static async Task EmitPostEvalNavAsync(CdpContext ctx, string? sessionId)
    {
        BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
        bool didNavigate;
        try
        {
            didNavigate = await page.ProcessPendingNavigationAsync().ConfigureAwait(false);
        }
        catch (Obscura.Browser.PageException exception)
        {
            throw new DomainError(exception.Message);
        }

        if (!didNavigate)
        {
            return;
        }

        BrowserPage current = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
        string frameId = current.FrameId;
        string pageUrl = current.UrlString();
        string pageId = current.Id;
        List<NetworkEvent> networkEvents = [.. current.NetworkEvents];
        current.NetworkEvents.Clear();
        bool reachedIdle = current.Lifecycle.IsNetworkIdle();

        string loaderId = $"loader-{Guid.NewGuid()}";
        Page.EmitNavigationEvents(
            ctx,
            sessionId,
            frameId,
            loaderId,
            pageUrl,
            pageId,
            networkEvents,
            WaitUntil.Load,
            reachedIdle);
    }

    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            return await HandleCoreAsync(method, parameters, ctx, sessionId).ConfigureAwait(false);
        }
        catch (DomainError error)
        {
            return DomainResult.Err(error.Message);
        }
    }

    private static async Task<DomainResult> HandleCoreAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        switch (method)
        {
            case "enable":
            {
                // puppeteer-extra's FrameManager.initialize calls Runtime.enable on the
                // browser-level connection BEFORE any page target exists. Real Chrome
                // replies with `{}` and emits executionContextCreated when a context
                // appears. Returning "No page" here breaks the standard puppeteer
                // connect/newPage flow. If there's no session, succeed silently - the
                // next Target.attachToTarget will set things up.
                if (sessionId is not null
                    && ctx.Sessions.TryGetValue(sessionId, out string? pageId))
                {
                    bool newlyEnabled = ctx.RuntimeEnabledSessions.Add(sessionId);
                    ctx.RefreshRuntimeEventCollection(pageId);
                    ctx.EnsureDefaultContext(pageId);
                    if (newlyEnabled)
                    {
                        List<CdpEvent> events = [.. ctx.ContextsForPage(pageId)
                            .Select(context => ExecutionContextCreatedEvent(context, sessionId))];
                        ctx.PendingEvents.AddRange(events);
                    }
                }

                return DomainResult.Empty();
            }

            case "disable":
            {
                if (sessionId is not null)
                {
                    ctx.Sessions.TryGetValue(sessionId, out string? pageId);
                    ctx.RuntimeEnabledSessions.Remove(sessionId);
                    if (pageId is not null)
                    {
                        ctx.RefreshRuntimeEventCollection(pageId);
                    }
                }

                return DomainResult.Empty();
            }

            case "evaluate":
            {
                string expression = parameters.Get("expression").AsString()
                    ?? throw new DomainError("expression required");
                bool returnByValue = parameters.Get("returnByValue").AsBool() ?? false;

                ValidateContext(parameters, "contextId", ctx, sessionId, "evaluate");

                bool awaitPromise = parameters.Get("awaitPromise").AsBool() ?? false;

                // CDP `timeout` field (milliseconds). Default to Chrome's protocolTimeout
                // (30s) so long evaluations don't pin the V8 lock indefinitely and starve
                // every other CDP command on the same session.
                ulong timeoutMs = parameters.Get("timeout").AsU64() ?? DefaultCommandTimeoutMs;

                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                RemoteObjectInfo info = await RunBoundedAsync(
                    () => page.EvaluateForCdpWithTimeoutAsync(
                        expression, returnByValue, awaitPromise, timeoutMs),
                    timeoutMs,
                    $"Runtime.evaluate exceeded {timeoutMs.ToString(CultureInfo.InvariantCulture)}ms timeout")
                    .ConfigureAwait(false);
                await EmitPostEvalNavAsync(ctx, sessionId).ConfigureAwait(false);

                return DomainResult.Ok(EvaluationReply(info));
            }

            case "callFunctionOn":
            {
                string functionDeclaration =
                    parameters.Get("functionDeclaration").AsString() ?? "() => undefined";
                bool returnByValue = parameters.Get("returnByValue").AsBool() ?? false;
                bool awaitPromise = parameters.Get("awaitPromise").AsBool() ?? false;
                string? objectId = parameters.Get("objectId").AsString();
                List<JsonNode?> arguments = [];
                foreach (JsonNode? argument in JsonExt.AsJsonArray(parameters.Get("arguments")) ?? [])
                {
                    arguments.Add(argument?.DeepClone());
                }

                // #51: validate executionContextId the same way Runtime.evaluate does.
                // CDP names this field `executionContextId` on callFunctionOn (not
                // `contextId`); a request may omit it when `objectId` is supplied - in
                // that case context validation is a no-op and the default context is used.
                ValidateContext(parameters, "executionContextId", ctx, sessionId, "callFunctionOn");

                // Keep awaitPromise alive for the same command budget as evaluate.
                // Playwright implements waits with callFunctionOn on some utility paths,
                // so a shorter hidden cap makes the client return before the requested
                // browser timer fires.
                ulong timeoutMs = parameters.Get("timeout").AsU64() ?? DefaultCommandTimeoutMs;

                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                RemoteObjectInfo info = await RunBoundedAsync(
                    () => page.CallFunctionOnForCdpWithTimeoutAsync(
                        functionDeclaration, objectId, arguments, returnByValue, awaitPromise, timeoutMs),
                    timeoutMs,
                    $"Runtime.callFunctionOn exceeded {timeoutMs.ToString(CultureInfo.InvariantCulture)}ms timeout")
                    .ConfigureAwait(false);
                await EmitPostEvalNavAsync(ctx, sessionId).ConfigureAwait(false);

                return DomainResult.Ok(EvaluationReply(info));
            }

            case "getProperties":
                return DomainResult.Ok(GetProperties(parameters, ctx, sessionId));

            case "releaseObject":
            {
                if (parameters.Get("objectId").AsString() is { } objectId
                    && ctx.GetSessionPageMut(sessionId) is { } page)
                {
                    page.ReleaseObject(objectId);
                }

                return DomainResult.Empty();
            }

            case "releaseObjectGroup":
            {
                ctx.GetSessionPageMut(sessionId)?.ReleaseObjectGroup();
                return DomainResult.Empty();
            }

            case "addBinding":
            {
                string name = parameters.Get("name").AsString() ?? string.Empty;
                if (IsValidBindingName(name))
                {
                    // The shim forwards every call back to the host through
                    // op_binding_called; the CDP dispatcher then drains the queue and
                    // emits Runtime.bindingCalled events the same way Chromium does.
                    // Chromium's V8InspectorImpl rejects calls without exactly one
                    // argument and ToString-coerces that argument before emitting it as
                    // the payload - we match the coercion (`String(arg)`) and silently
                    // drop calls with wrong arity, which is what Chrome does.
                    string shim =
                        $"globalThis['{name}'] = function (arg) {{"
                        + "if (arguments.length !== 1) return;"
                        + "try {"
                        + "const payload = typeof arg === 'string' ? arg : String(arg);"
                        + $"Deno.core.ops.op_binding_called('{name}', payload);"
                        + "} catch (e) { /* swallow: binding must not throw into page */ }"
                        + "};";
                    // Re-install on every navigation: globalThis is wiped on each new
                    // document, and puppeteer registers bindings once-per-page rather
                    // than once-per-document.
                    string key = $"__obscura_binding__{name}";
                    ctx.PreloadScripts.RemoveAll(entry =>
                        string.Equals(entry.Identifier, key, StringComparison.Ordinal));
                    ctx.PreloadScripts.Add((key, shim));
                    // Remember who subscribed, so the call goes back to this session
                    // rather than to whichever session of the page a dictionary happens
                    // to yield first. A client discards an event addressed to a session
                    // it does not hold, and the session Target.createTarget leaves behind
                    // is not the one a client ends up using.
                    if (sessionId is not null)
                    {
                        if (!ctx.BindingSessions.TryGetValue(name, out List<string>? owners))
                        {
                            owners = [];
                            ctx.BindingSessions[name] = owners;
                        }

                        if (!owners.Contains(sessionId, StringComparer.Ordinal))
                        {
                            owners.Add(sessionId);
                        }
                    }

                    // Install on the current page so the binding is usable immediately,
                    // without waiting for the next navigation.
                    ctx.GetSessionPageMut(sessionId)?.Evaluate(shim);
                }

                return DomainResult.Empty();
            }

            case "removeBinding":
            {
                string name = parameters.Get("name").AsString() ?? string.Empty;
                if (IsValidBindingName(name))
                {
                    string key = $"__obscura_binding__{name}";
                    ctx.PreloadScripts.RemoveAll(entry =>
                        string.Equals(entry.Identifier, key, StringComparison.Ordinal));
                    if (sessionId is not null
                        && ctx.BindingSessions.TryGetValue(name, out List<string>? owners))
                    {
                        owners.RemoveAll(owner => string.Equals(owner, sessionId, StringComparison.Ordinal));
                        if (owners.Count == 0)
                        {
                            ctx.BindingSessions.Remove(name);
                        }
                    }

                    ctx.GetSessionPageMut(sessionId)?.Evaluate($"delete globalThis['{name}'];");
                }

                return DomainResult.Empty();
            }

            case "runIfWaitingForDebugger":
                return DomainResult.Empty();

            case "getExceptionDetails":
                return DomainResult.Ok(new JsonObject { ["exceptionDetails"] = null });

            case "discardConsoleEntries":
                return DomainResult.Empty();

            default:
                return DomainResult.Err($"Unknown Runtime method: {method}");
        }
    }

    /// <summary>
    /// Bound one evaluation by the caller's timeout, translating a runtime failure into
    /// the protocol error string the Rust engine returns.
    /// </summary>
    private static async Task<RemoteObjectInfo> RunBoundedAsync(
        Func<Task<RemoteObjectInfo>> start,
        ulong timeoutMs,
        string timeoutMessage)
    {
        Task<RemoteObjectInfo> work;
        try
        {
            work = start();
        }
        catch (JsRuntimeException exception)
        {
            throw new DomainError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            throw new DomainError(exception.Message);
        }

        // The inner call already bounds its own await; this outer bound exists so a
        // synchronous-but-slow path cannot pin the connection past the budget either.
        // The budget is doubled here because the inner timeout is the one whose message
        // the protocol reports.
        long outerMs = timeoutMs > long.MaxValue / 4 ? long.MaxValue / 4 : (long)(timeoutMs * 2) + 1000;
        Task completed = await Task.WhenAny(work, Task.Delay(TimeSpan.FromMilliseconds(outerMs)))
            .ConfigureAwait(false);
        if (!ReferenceEquals(completed, work))
        {
            throw new DomainError(timeoutMessage);
        }

        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (JsRuntimeException exception)
        {
            throw new DomainError(exception.Message);
        }
    }

    /// <summary>
    /// Walk an object handle, minting a stable child objectId per property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Puppeteer's <c>$$()</c> flow:
    /// (1) evaluate querySelectorAll to get a handle for the NodeList,
    /// (2) getProperties on that handle for the indexed items,
    /// (3) for each item, <c>JSHandle.asElement()</c> checks <c>subtype === 'node'</c>;
    /// if true, it wraps the item as an ElementHandle (with click/type/etc).
    /// </para>
    /// <para>
    /// An earlier implementation returned the raw value via JSON, dropping the node
    /// identity. Items came back as <c>{type:'object'}</c> with no objectId and no
    /// subtype, so asElement returned null and the caller got plain JSHandles back from
    /// <c>page.$$</c> - breaking <c>checkboxes[0].click()</c>.
    /// </para>
    /// </remarks>
    private static JsonNode GetProperties(JsonNode? parameters, CdpContext ctx, string? sessionId)
    {
        var empty = new JsonObject
        {
            ["result"] = new JsonArray(),
            ["internalProperties"] = new JsonArray(),
        };
        if (parameters.Get("objectId").AsString() is not { } objectId)
        {
            return empty;
        }

        BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
        // The child ids minted below are `<parent>::<key>` and the key is a property name
        // off a page object, so the page decides what ends up inside this literal. A JSON
        // literal covers the C0 controls a manual quote/backslash pair leaves alone.
        string oid = CdpUtil.ObjectIdLiteral(objectId);
        string code =
            "(function() {"
            + $"var obj = globalThis.__obscura_objects[{oid}];"
            + "if (!obj || typeof obj !== 'object') return [];"
            + "var keys = Object.keys(obj);"
            + "return keys.map(function(k) {"
            + "var v = obj[k];"
            + "var t = typeof v;"
            + "var item = { name: k, type: t };"
            + "if (v === null) { item.value = null; return item; }"
            + "if (t !== 'object' && t !== 'function') { item.value = v; return item; }"
            + $"var childOid = {oid} + '::' + k;"
            + "globalThis.__obscura_objects[childOid] = v;"
            + "item.childOid = childOid;"
            + "if (typeof v.nodeType === 'number') {"
            + "item.subtype = 'node';"
            + "item.className = v.constructor && v.constructor.name ? v.constructor.name : (v.tagName ? 'HTML' + v.tagName.charAt(0) + v.tagName.slice(1).toLowerCase() + 'Element' : 'Node');"
            + "item.description = v.tagName ? v.tagName.toLowerCase() : (v.nodeName || 'node');"
            + "} else if (Array.isArray(v)) {"
            + "item.subtype = 'array';"
            + "item.className = 'Array';"
            + "item.description = 'Array(' + v.length + ')';"
            + "} else {"
            + "item.className = (v.constructor && v.constructor.name) || 'Object';"
            + "item.description = item.className;"
            + "}"
            + "return item;"
            + "});"
            + "})()";

        JsonNode? result = page.Evaluate(code);
        if (JsonExt.AsJsonArray(result) is not { } properties)
        {
            return empty;
        }

        var descriptors = new JsonArray();
        foreach (JsonNode? property in properties)
        {
            string name = property.Get("name").AsString() ?? string.Empty;
            string propertyType = property.Get("type").AsString() ?? "undefined";
            var remote = new JsonObject { ["type"] = propertyType };
            if (property.Get("childOid").AsString() is { } childOid)
            {
                remote["type"] = "object";
                if (property.Get("subtype").AsString() is { } subtype)
                {
                    remote["subtype"] = subtype;
                }

                if (property.Get("className").AsString() is { } className)
                {
                    remote["className"] = className;
                }

                if (property.Get("description").AsString() is { } description)
                {
                    remote["description"] = description;
                }

                remote["objectId"] = childOid;
            }
            else if (property is JsonObject propertyObject && propertyObject.ContainsKey("value"))
            {
                JsonNode? value = property.Get("value");
                switch (value)
                {
                    case null:
                        remote["type"] = "object";
                        remote["subtype"] = "null";
                        remote["value"] = null;
                        break;
                    case JsonValue stringValue when stringValue.TryGetValue(out string? text):
                        remote["type"] = "string";
                        remote["value"] = text;
                        break;
                    case JsonValue boolValue when boolValue.TryGetValue(out bool flag):
                        remote["type"] = "boolean";
                        remote["value"] = flag;
                        break;
                    case JsonValue numberValue when numberValue.AsF64() is not null:
                        remote["type"] = "number";
                        remote["value"] = value.DeepClone();
                        break;
                    default:
                        remote["value"] = value.DeepClone();
                        break;
                }
            }

            descriptors.Add(new JsonObject
            {
                ["name"] = name,
                ["value"] = remote,
                ["configurable"] = true,
                ["enumerable"] = true,
                ["writable"] = true,
                ["isOwn"] = true,
            });
        }

        return new JsonObject
        {
            ["result"] = descriptors,
            ["internalProperties"] = new JsonArray(),
        };
    }

    /// <summary>
    /// Reject <c>Runtime.{evaluate,callFunctionOn}</c> calls that target an execution
    /// context Obscura has not advertised for the attached page.
    /// </summary>
    /// <remarks>
    /// An absent identity uses the page's default context. Direct embedders retain the
    /// compatibility path for ids reserved through <c>NextIsolatedContext</c>.
    /// </remarks>
    internal static void ValidateContext(
        JsonNode? parameters,
        string field,
        CdpContext ctx,
        string? sessionId,
        string method)
    {
        long? id = parameters.Get(field).AsI64();
        string? uniqueId = parameters.Get("uniqueContextId").AsString();
        if (id is not null && uniqueId is not null)
        {
            throw new DomainError(
                $"Runtime.{method} cannot specify both {field} and uniqueContextId");
        }

        if (id is null && uniqueId is null)
        {
            return;
        }

        ExecutionContextRecord? record = null;
        if (id is { } contextId)
        {
            record = ctx.ContextById(contextId);
        }

        record ??= uniqueId is not null ? ctx.ContextByUniqueId(uniqueId) : null;

        if (record is not null)
        {
            string? owner = sessionId is not null && ctx.Sessions.TryGetValue(sessionId, out string? pageId)
                ? pageId
                : null;
            if (owner is not null && string.Equals(owner, record.PageId, StringComparison.Ordinal))
            {
                // This registry currently validates ownership/routing only. A named
                // isolated context still executes in the owning page's current V8
                // runtime/global; it is not a separate V8 realm yet.
                return;
            }
        }
        else if (sessionId is null && uniqueId is null
            && id is { } reserved && ctx.ValidContextIds.Contains(reserved))
        {
            // Direct embedders can still reserve an id through the existing public
            // NextIsolatedContext API. Attached sessions require page ownership.
            return;
        }

        string identity = id?.ToString(CultureInfo.InvariantCulture) ?? uniqueId ?? string.Empty;
        if (record is null || sessionId is not null)
        {
            throw new DomainError($"Cannot find context with specified id: {identity}");
        }
    }

    /// <summary>
    /// Shape one <c>Runtime.evaluate</c> or <c>Runtime.callFunctionOn</c> reply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A thrown value, or the value a promise rejected with, is not a protocol failure:
    /// the command succeeds and reports it through <c>exceptionDetails</c>, which is what
    /// a client rebuilds the page error from. The value is repeated in <c>result</c> the
    /// way Chrome does, so a client that reads only <c>result</c> sees the error object
    /// rather than a success that never happened.
    /// </para>
    /// <para>
    /// <c>exceptionId</c> is a fixed 1 because nothing here correlates exceptions across
    /// commands; clients key off the presence of the field, not its id.
    /// </para>
    /// </remarks>
    internal static JsonNode EvaluationReply(RemoteObjectInfo info)
    {
        JsonObject remote = RemoteObjectFromInfo(info);
        if (!info.Thrown)
        {
            return new JsonObject { ["result"] = remote };
        }

        return new JsonObject
        {
            ["result"] = remote.DeepClone(),
            ["exceptionDetails"] = new JsonObject
            {
                ["exceptionId"] = 1,
                ["text"] = "Uncaught",
                ["lineNumber"] = 0,
                ["columnNumber"] = 0,
                ["exception"] = remote,
            },
        };
    }

    internal static JsonObject RemoteObjectFromInfo(RemoteObjectInfo info)
    {
        var obj = new JsonObject { ["type"] = info.JsType };

        if (info.Subtype is { } subtype)
        {
            obj["subtype"] = subtype;
        }

        if (info.ClassName.Length != 0)
        {
            obj["className"] = info.ClassName;
        }

        if (info.Description.Length != 0)
        {
            obj["description"] = info.Description;
        }

        if (info.ObjectId is { } objectId)
        {
            obj["objectId"] = objectId;
        }

        if (info.Value is { } value)
        {
            obj["value"] = value.DeepClone();
        }

        return obj;
    }
}
