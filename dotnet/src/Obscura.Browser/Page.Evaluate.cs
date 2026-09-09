using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Js.Ops;
using Obscura.Js.Runtime;
using Obscura.Js.Modules;

namespace Obscura.Browser;

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
                return UndefinedRemoteObject(null);
            }
        }

        return UndefinedRemoteObject(Evaluate(expression));
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
        return UndefinedRemoteObject(Evaluate(expression));
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
            return UndefinedRemoteObject(null);
        }
        try
        {
            return await js
                .CallFunctionOnForCdpAsync(functionDeclaration, objectId, args, returnByValue, awaitPromise)
                .ConfigureAwait(false);
        }
        catch (JsRuntimeException)
        {
            return UndefinedRemoteObject(null);
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

    public void ReleaseObject(string objectId) => Js?.ReleaseObject(objectId);

    public void ReleaseObjectGroup() => Js?.ReleaseObjectGroup();

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
        Obscura.Dom.DomTree? dom = js.TakeDom();
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

    public (string Url, string Method, string Body)? TakePendingNavigation() => Js?.TakePendingNavigation();

    public IReadOnlyList<(string Name, string Payload)> TakePendingBindingCalls() =>
        Js?.TakePendingBindingCalls() ?? [];

    public IReadOnlyList<RuntimeEvent> TakePendingRuntimeEvents() =>
        Js?.TakePendingRuntimeEvents() ?? [];

    public void SetRuntimeEventsEnabled(bool enabled)
    {
        _runtimeEventsEnabled = enabled;
        Js?.SetRuntimeEventsEnabled(enabled);
    }

    private static RemoteObjectInfo UndefinedRemoteObject(JsonNode? value) => new(
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
        Value: value);
}
