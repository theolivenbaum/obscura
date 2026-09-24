using System.Text.Json;
using System.Text.Json.Nodes;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Js.Modules;

namespace PocketCalculator.Browser;

public sealed partial class Page
{
    /// <summary>
    /// V8 isolate handle for this page's runtime, if it has been initialized.
    /// </summary>
    /// <remarks>
    /// Lets the CDP dispatcher arm a per-command watchdog (bounding any one command
    /// so a hung page cannot hold this connection's V8 lock forever).
    /// </remarks>
    public IIsolateHandle? IsolateHandle => Js?.IsolateHandleForWatchdog;

    /// <summary>
    /// Clear a V8 termination left by a per-command watchdog so the next command on
    /// this page can run. A no-op if the runtime is absent or not terminating.
    /// </summary>
    public void CancelV8Termination() => Js?.CancelTermination();

    /// <summary>
    /// Like <see cref="Evaluate"/> but bounded by a V8 watchdog so a runaway
    /// expression cannot hang the process. A zero timeout falls back to the unbounded
    /// path.
    /// </summary>
    public JsonNode? EvaluateWithTimeout(string expression, TimeSpan timeout)
    {
        if (Js is not { } js)
        {
            return Evaluate(expression);
        }
        try
        {
            return js.EvaluateWithTimeout(expression, timeout);
        }
        catch (JsRuntimeException)
        {
            return null;
        }
    }

    public JsonNode? Evaluate(string expression)
    {
        if (Js is { } js)
        {
            try
            {
                return js.Evaluate(expression);
            }
            catch (JsRuntimeException)
            {
                return null;
            }
        }

        return expression.Trim() switch
        {
            "document.title" => JsonValue.Create(Title),
            "document.URL" or "document.location.href" or "window.location.href" =>
                JsonValue.Create(UrlString()),
            _ => null,
        };
    }

    /// <summary>
    /// <see cref="Evaluate"/> for host-authored script that uses the page realm's host
    /// helpers, which it names as <c>__obscura_host</c> (<c>__obscura_host.markTrusted(ev)</c>
    /// and the rest; see <see cref="HostScript"/>). Page script cannot reach these, so
    /// never pass client- or page-supplied code here.
    /// </summary>
    /// <remarks>
    /// DEVIATION from the Rust engine, whose host scripts call the helpers as page-visible
    /// <c>globalThis.__obscura_*</c> globals through the ordinary evaluate path.
    /// </remarks>
    public JsonNode? EvaluateHost(string expression)
    {
        if (Js is not { } js)
        {
            return null;
        }
        try
        {
            return js.EvaluateHost(expression);
        }
        catch (JsRuntimeException)
        {
            return null;
        }
    }

    public async Task<RemoteObjectInfo> EvaluateForCdpAsync(
        string expression,
        bool returnByValue,
        bool awaitPromise)
    {
        if (Js is { } js)
        {
            try
            {
                return await js.EvaluateForCdpAsync(expression, returnByValue, awaitPromise)
                    .ConfigureAwait(false);
            }
            catch (JsRuntimeException)
            {
                return UndefinedRemoteObject();
            }
        }

        return ValueRemoteObject(Evaluate(expression));
    }

    public async Task<RemoteObjectInfo> EvaluateForCdpWithTimeoutAsync(
        string expression,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs)
    {
        if (Js is { } js)
        {
            return await js
                .EvaluateForCdpWithTimeoutAsync(expression, returnByValue, awaitPromise, awaitTimeoutMs)
                .ConfigureAwait(false);
        }
        return ValueRemoteObject(Evaluate(expression));
    }

    public async Task<RemoteObjectInfo> CallFunctionOnForCdpAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> args,
        bool returnByValue,
        bool awaitPromise)
    {
        if (Js is not { } js)
        {
            return UndefinedRemoteObject();
        }
        try
        {
            return await js
                .CallFunctionOnForCdpAsync(functionDeclaration, objectId, args, returnByValue, awaitPromise)
                .ConfigureAwait(false);
        }
        catch (JsRuntimeException)
        {
            return UndefinedRemoteObject();
        }
    }

    public Task<RemoteObjectInfo> CallFunctionOnForCdpWithTimeoutAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> args,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs)
    {
        if (Js is not { } js)
        {
            throw new InvalidOperationException("JavaScript runtime unavailable");
        }
        return js.CallFunctionOnForCdpWithTimeoutAsync(
            functionDeclaration,
            objectId,
            args,
            returnByValue,
            awaitPromise,
            awaitTimeoutMs);
    }

    /// <summary>
    /// <c>Runtime.evaluate</c> in a CDP isolated world (null: the page realm). Port
    /// addition, SECURITY.md M6: the Rust engine runs every context in the page realm.
    /// </summary>
    public async Task<RemoteObjectInfo> EvaluateForCdpWithTimeoutAsync(
        string expression,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs,
        IsolatedWorldTarget? world)
    {
        if (world is null)
        {
            return await EvaluateForCdpWithTimeoutAsync(expression, returnByValue, awaitPromise, awaitTimeoutMs)
                .ConfigureAwait(false);
        }
        if (Js is not { } js)
        {
            throw new InvalidOperationException("JavaScript runtime unavailable");
        }
        return await js
            .EvaluateForCdpWithTimeoutAsync(expression, returnByValue, awaitPromise, awaitTimeoutMs, world)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>Runtime.callFunctionOn</c>: in the realm <paramref name="objectId"/> was minted
    /// in, or in <paramref name="world"/> when there is no object (null: the page realm).
    /// </summary>
    public Task<RemoteObjectInfo> CallFunctionOnForCdpWithTimeoutAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> args,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs,
        IsolatedWorldTarget? world)
    {
        if (Js is not { } js)
        {
            throw new InvalidOperationException("JavaScript runtime unavailable");
        }
        return js.CallFunctionOnForCdpWithTimeoutAsync(
            functionDeclaration, objectId, args, returnByValue, awaitPromise, awaitTimeoutMs, world);
    }

    /// <summary>
    /// <see cref="Evaluate"/> in the realm that minted <paramref name="objectId"/>: the page
    /// realm for its own ids, or the isolated world that made it. The expression names that
    /// realm's CDP store as <c>__obscura_cdp</c>; host-authored code only.
    /// </summary>
    public JsonNode? EvaluateInObjectRealm(string objectId, string expression)
    {
        if (Js is not { } js)
        {
            return null;
        }
        try
        {
            return js.EvaluateInObjectRealm(objectId, expression);
        }
        catch (JsRuntimeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs host-authored <paramref name="source"/> in each isolated world named
    /// <paramref name="worldName"/> that exists now. Worlds created later run it from their
    /// init scripts.
    /// </summary>
    public void ExecuteInIsolatedWorlds(string worldName, string source)
    {
        if (Js is not { } js)
        {
            return;
        }
        foreach (var world in js.IsolatedWorlds.ToArray())
        {
            if (!string.Equals(world.Name, worldName, StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                world.ExecutePreloadScript(source);
            }
            catch (JsRuntimeException)
            {
                // The same as a failing init script.
            }
        }
    }

    /// <summary>Binding calls made from isolated worlds, with the world's context id.</summary>
    public IReadOnlyList<(long WorldKey, string Name, string Payload)> TakePendingWorldBindingCalls() =>
        Js?.TakePendingWorldBindingCalls() ?? [];

    public void ReleaseObject(string objectId) => Js?.ReleaseObject(objectId);

    public void ReleaseObjectGroup() => Js?.ReleaseObjectGroup();

    /// <summary>
    /// Runs a new-document entry (a script, or a <see cref="BindingPreload"/> binding) in
    /// the current document's main realm and in every child frame's, as Chromium does for
    /// a <c>Runtime.addBinding</c> made after the document loaded. A failure in one realm
    /// does not stop the others.
    /// </summary>
    public void InstallPreloadNow(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Js is not { } js)
        {
            return;
        }
        try
        {
            js.ExecutePreloadScript(source);
        }
        catch (JsRuntimeException)
        {
            // The same as a failing init script.
        }
        foreach (FrameRealm frame in Frames.ToArray())
        {
            try
            {
                frame.ExecutePreloadScript(source);
            }
            catch (JsRuntimeException)
            {
                // The same as a failing init script.
            }
        }
    }

    public void ExecutePreloadScript(string source)
    {
        if (Js is not { } js)
        {
            throw new InvalidOperationException("No JS runtime");
        }
        js.ExecuteScript("<preload>", source);
    }

    public void SuspendJs()
    {
        if (Js is not { } js)
        {
            return;
        }
        IReadOnlyList<uint> startedScriptIds = js.StartedScriptIds();
        _suspendedCdpObjectState = js.TakeCdpObjectState();
        PocketCalculator.Dom.DomTree? dom = js.TakeDom();
        if (dom is not null)
        {
            Dom = dom;
            _suspendedStartedScriptIds = [.. startedScriptIds];
        }
        else
        {
            _suspendedStartedScriptIds.Clear();
        }
        // Every frame realm holds a V8 handle into this isolate, so the frames go
        // before the runtime does, the same order InitJs keeps on a new document.
        // Suspending is a teardown of the realm the frames live in, and a realm
        // cannot be suspended and resumed the way the page's DOM can, so they are
        // rebuilt when the page next loads a document.
        _pendingFrameWork.Clear();
        Frames.Clear();
        Js = null;
    }

    public void ResumeJs()
    {
        if (Js is not null)
        {
            return;
        }
        List<uint> startedScriptIds = _suspendedStartedScriptIds;
        _suspendedStartedScriptIds = [];
        CdpObjectState cdpObjectState = _suspendedCdpObjectState;
        _suspendedCdpObjectState = new CdpObjectState();
        InitJs();
        if (Js is { } js)
        {
            js.RestoreStartedScriptIds(startedScriptIds);
            js.RestoreCdpObjectState(cdpObjectState);
        }
    }

    public bool HasJs => Js is not null;

    public PendingNavigation? TakePendingNavigation() => Js?.TakePendingNavigation();

    /// <summary>
    /// Give the page transient user activation, as a real mouse press or key press does.
    /// The host input paths (CDP <c>Input</c>, MCP click and type) call this before they
    /// dispatch, so a navigation the input causes reports <c>Sec-Fetch-User: ?1</c>.
    /// </summary>
    public void NoteUserActivation() => Js?.NoteUserActivation();

    public IReadOnlyList<(string Name, string Payload)> TakePendingBindingCalls() =>
        Js?.TakePendingBindingCalls() ?? [];

    public IReadOnlyList<RuntimeEvent> TakePendingRuntimeEvents() =>
        Js?.TakePendingRuntimeEvents() ?? [];

    public void SetRuntimeEventsEnabled(bool enabled)
    {
        _runtimeEventsEnabled = enabled;
        Js?.SetRuntimeEventsEnabled(enabled);
    }

    /// <summary>
    /// The <c>value: None</c> remote object Rust builds when a runtime call fails,
    /// and when <c>callFunctionOn</c> is asked of a page with no JavaScript realm.
    /// </summary>
    private static RemoteObjectInfo UndefinedRemoteObject() => new(
        Thrown: false,
        JsType: "undefined",
        Subtype: null,
        ClassName: string.Empty,
        Description: string.Empty,
        ObjectId: null,
        Value: null);

    /// <summary>
    /// The remote object for a page with no JavaScript realm, where the answer
    /// comes from <see cref="Evaluate"/>'s small static table instead.
    /// </summary>
    /// <remarks>
    /// Rust builds this arm with <c>value: Some(val)</c>, and its <c>evaluate</c>
    /// returns a <c>serde_json::Value</c> rather than an <c>Option</c>, so an
    /// unmatched expression is <c>Some(Value::Null)</c> and the reply carries
    /// <c>{"type":"undefined","value":null}</c>. Sharing one helper with
    /// <see cref="UndefinedRemoteObject"/> dropped the key, because a null
    /// <see cref="JsonNode"/> cannot say which of the two it is.
    /// </remarks>
    private static RemoteObjectInfo ValueRemoteObject(JsonNode? value) => new(
        Thrown: false,
        JsType: value?.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "undefined",
        },
        Subtype: null,
        ClassName: string.Empty,
        Description: string.Empty,
        ObjectId: null,
        Value: value)
    {
        HasValue = true,
    };
}
