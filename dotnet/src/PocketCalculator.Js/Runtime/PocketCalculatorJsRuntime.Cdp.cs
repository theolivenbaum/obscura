using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Evaluation and the CDP remote-object store.
/// </summary>
public sealed partial class PocketCalculatorJsRuntime
{
    /// <summary>
    /// Evaluate an expression (or a short statement bundle) and decode the
    /// result as JSON.
    /// </summary>
    public JsonNode? Evaluate(string expression)
    {
        BeginJavaScriptTask();
        var result = ExecuteRuntimeScript("<eval>", WrapExpression(expression));
        return ToJson(result);
    }

    /// <summary>
    /// <see cref="Evaluate"/> for host-authored script that uses the page realm's host
    /// helpers, which it names as <c>__obscura_host</c> (see <see cref="HostScript"/>).
    /// Never pass client- or page-supplied code here.
    /// </summary>
    public JsonNode? EvaluateHost(string expression)
    {
        BeginJavaScriptTask();
        return ToJson(InvokeHostScript("<host-eval>", HostScript.WrapExpression(expression)));
    }

    /// <summary>
    /// <see cref="ExecuteScript"/> for host-authored statements that use the page realm's
    /// host helpers as <c>__obscura_host</c> (see <see cref="HostScript"/>).
    /// </summary>
    public void ExecuteHostScript(string name, string source)
    {
        BeginJavaScriptTask();
        InvokeHostScript(name, HostScript.WrapStatements(source));
    }

    private object? InvokeHostScript(string name, string functionSource) =>
        InvokeIn(_engine, name, functionSource, _shim.HostHelpers);

    /// <summary>
    /// Compiles <paramref name="functionSource"/> in <paramref name="engine"/> and calls it
    /// with <paramref name="argument"/>, with <see cref="ExecuteIn"/>'s deadline and
    /// heap-limit handling.
    /// </summary>
    internal object? InvokeIn(V8ScriptEngine engine, string name, string functionSource, ScriptObject? argument)
    {
        CancellationToken deadline = _ops.Cancellation.Token;
        try
        {
            object? result = HostScript.Invoke(engine, argument, name, functionSource);
            ThrowIfDeadlinePassed(deadline);
            return result;
        }
        catch (ScriptInterruptedException)
        {
            throw new JsRuntimeException("JS error: Uncaught Error: execution terminated");
        }
        catch (ScriptEngineException error)
        {
            if (IsHeapLimitFailure(error))
            {
                RecoverHeapLimit();
                throw new JsRuntimeException("JavaScript heap limit exceeded; execution terminated");
            }
            throw new JsRuntimeException($"JS error: Uncaught {error.Message}");
        }
    }

    /// <summary>
    /// Like <see cref="Evaluate"/> but bounded by a watchdog, so a
    /// <c>--eval</c> expression that loops forever cannot hang the process.
    /// </summary>
    public JsonNode? EvaluateWithTimeout(string expression, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return Evaluate(expression);
        }
        BeginJavaScriptTask();
        var wrapped = WrapExpression(expression);
        var token = ArmWatchdog(timeout);
        object? result = null;
        JsRuntimeException? failure = null;
        try
        {
            result = ExecuteRuntimeScript("<eval>", wrapped);
        }
        catch (JsRuntimeException error)
        {
            failure = error;
        }
        var fired = DisarmWatchdog(token);
        if (failure is not null)
        {
            if (failure.Message.Contains("heap limit exceeded", StringComparison.Ordinal))
            {
                throw failure;
            }
            throw fired || failure.Message.Contains("execution terminated", StringComparison.Ordinal)
                ? new JsRuntimeException("eval timed out")
                : failure;
        }
        if (fired)
        {
            throw new JsRuntimeException("eval timed out");
        }
        return ToJson(result);
    }

    // ------------------------------------------------------------- Runtime.evaluate

    public Task<RemoteObjectInfo> EvaluateForCdpAsync(string expression, bool returnByValue, bool awaitPromise) =>
        EvaluateForCdpWithTimeoutAsync(expression, returnByValue, awaitPromise, DefaultCdpAwaitTimeoutMs);

    /// <summary>
    /// The whole of <c>Runtime.evaluate</c>, in one path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expression travels as a <em>string</em> into an indirect eval rather
    /// than being pasted into a wrapper. Pasting forced two workarounds that
    /// only half worked: a trailing <c>;</c> had to be trimmed or <c>(expr;)</c>
    /// failed to parse, and a trailing <c>//# sourceURL=</c> comment (Puppeteer
    /// and Playwright both append one) would swallow the closing paren unless it
    /// sat on its own line. Neither can happen to a string literal.
    /// </para>
    /// <para>
    /// It also makes statements legal. <c>Runtime.evaluate</c> is specified to
    /// take a script, not an expression, so <c>throw new Error('x')</c> and
    /// <c>var x = 1; x * 2</c> are both valid input. <c>(0, eval)</c> rather
    /// than <c>eval</c> so the script runs at global scope, where <c>var</c>
    /// lands on <c>globalThis</c> the way it does in Chrome (#746).
    /// </para>
    /// </remarks>
    public Task<RemoteObjectInfo> EvaluateForCdpWithTimeoutAsync(
        string expression,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs) =>
        EvaluateForCdpInScopeAsync(_mainScope, expression, returnByValue, awaitPromise, awaitTimeoutMs);

    /// <summary>
    /// <c>Runtime.evaluate</c> in <paramref name="world"/>, or in the page realm when it is
    /// null. The world is created the first time a client names it.
    /// </summary>
    public Task<RemoteObjectInfo> EvaluateForCdpWithTimeoutAsync(
        string expression,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs,
        IsolatedWorldTarget? world) =>
        EvaluateForCdpInScopeAsync(
            world is { } target ? GetOrCreateIsolatedWorld(target).Scope : _mainScope,
            expression, returnByValue, awaitPromise, awaitTimeoutMs);

    private async Task<RemoteObjectInfo> EvaluateForCdpInScopeAsync(
        CdpScope scope,
        string expression,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs)
    {
        BeginJavaScriptTask();

        scope.Counter += 1;
        var oid = scope.MakeOid(scope.Counter);
        var sourceLiteral = JsStringLiteral(expression);
        var oidLiteral = JsStringLiteral(oid);
        var done = scope.Counter.ToString(CultureInfo.InvariantCulture);

        // DEVIATION from crates/obscura-js/src/runtime.rs (SECURITY.md L10): the handle and
        // the outcome go into the realm's closure store (__obscura_cdp, see bootstrap.js
        // _cdpHost), not the page-visible __obscura_objects, __obscura_await_meta,
        // __obscura_await_rejected and __obscura_done_N, and the source runs through the
        // eval bootstrap captured. The page can neither read a client's handles nor settle
        // an awaitPromise with a forged result.
        ScriptObject outcome;
        if (awaitPromise)
        {
            scope.Call("<eval-remote>", $$"""
                var c = __obscura_cdp;
                (async function () {
                    try {
                        var r = await c.eval({{sourceLiteral}});
                        c.objects[{{oidLiteral}}] = r;
                        c.outcomes[{{done}}] = c.settle(false, r);
                    } catch (e) {
                        c.objects[{{oidLiteral}}] = e;
                        c.outcomes[{{done}}] = c.settle(true, e);
                    }
                })();
                """);
            outcome = await AwaitOutcomeAsync(scope, done, awaitTimeoutMs, "Runtime.evaluate").ConfigureAwait(false);
        }
        else
        {
            // The synchronous half reports the same outcome as the await half, so one
            // protocol covers both. Before this, a throw here became `__result =
            // undefined`: a page error was indistinguishable from an expression with no
            // value.
            outcome = scope.Call("<eval-remote>", $$"""
                var c = __obscura_cdp, r;
                try {
                    r = c.eval({{sourceLiteral}});
                } catch (e) {
                    c.objects[{{oidLiteral}}] = e;
                    return c.settle(true, e);
                }
                c.objects[{{oidLiteral}}] = r;
                return c.settle(false, r);
                """) as ScriptObject
                ?? throw new JsRuntimeException("Runtime.evaluate produced no result");
        }

        // Neither a rejection nor a synchronous throw is a protocol failure. CDP
        // answers the command and puts the value in exceptionDetails, so it
        // travels back as a remote object flagged Thrown, by reference even when
        // the caller asked for a value: JSON.stringify(new Error("boom")) is {},
        // so serializing it would throw the message away.
        scope.Store[oid] = CdpScope.Retrieval(oid);
        if (IsRejected(outcome))
        {
            return InfoFromMeta(MetaOf(outcome), oid) with { Thrown = true };
        }

        if (!returnByValue)
        {
            scope.Recipes[oid] = expression;
            return InfoFromMeta(MetaOf(outcome), oid);
        }

        return InfoFromJson(ToJson(scope.Objects.GetProperty(oid), scope.Engine));
    }

    /// <summary>
    /// Pumps the event loop until the awaiting wrapper numbered <paramref name="done"/> has
    /// settled, and takes its outcome.
    /// </summary>
    private async Task<ScriptObject> AwaitOutcomeAsync(CdpScope scope, string done, ulong awaitTimeoutMs, string method)
    {
        var outcomes = scope.Outcomes;
        var settled = await ResolvePromisesUntilAsync(
            _ =>
            {
                try
                {
                    return outcomes.GetProperty(done) is ScriptObject;
                }
                catch (ScriptEngineException)
                {
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            },
            awaitTimeoutMs).ConfigureAwait(false);
        if (!settled || outcomes.GetProperty(done) is not ScriptObject outcome)
        {
            throw new JsRuntimeException($"{method} promise did not settle within {awaitTimeoutMs}ms");
        }
        outcomes.DeleteProperty(done);
        return outcome;
    }

    /// <summary>Whether a wrapper's outcome (<c>[rejected, type, subtype, className, description]</c>) is a throw.</summary>
    private static bool IsRejected(ScriptObject outcome) => outcome.GetProperty(0) is bool flag && flag;

    /// <summary>
    /// The metadata of a wrapper's outcome, in the shape the page's <c>JSON.stringify</c>
    /// used to produce, read field by field so no page-replaceable built-in serializes it.
    /// </summary>
    private static JsonObject MetaOf(ScriptObject outcome)
    {
        var meta = new JsonObject();
        AddMeta(meta, "type", outcome.GetProperty(1));
        AddMeta(meta, "subtype", outcome.GetProperty(2));
        AddMeta(meta, "className", outcome.GetProperty(3));
        AddMeta(meta, "description", outcome.GetProperty(4));
        return meta;
    }

    private static void AddMeta(JsonObject meta, string key, object? value)
    {
        if (value is string text)
        {
            meta[key] = text;
        }
    }

    // -------------------------------------------------------- Runtime.callFunctionOn

    public Task<RemoteObjectInfo> CallFunctionOnForCdpAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> arguments,
        bool returnByValue,
        bool awaitPromise) =>
        CallFunctionOnForCdpWithTimeoutAsync(
            functionDeclaration, objectId, arguments, returnByValue, awaitPromise, DefaultCdpAwaitTimeoutMs);

    public Task<RemoteObjectInfo> CallFunctionOnForCdpWithTimeoutAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> arguments,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs) =>
        CallFunctionOnForCdpWithTimeoutAsync(
            functionDeclaration, objectId, arguments, returnByValue, awaitPromise, awaitTimeoutMs, world: null);

    /// <summary>
    /// <c>Runtime.callFunctionOn</c>. An <paramref name="objectId"/> names the realm it
    /// was minted in, which is where the call runs; with none, the call runs in
    /// <paramref name="world"/>, or in the page realm when that is null too.
    /// </summary>
    public Task<RemoteObjectInfo> CallFunctionOnForCdpWithTimeoutAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> arguments,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs,
        IsolatedWorldTarget? world)
    {
        CdpScope scope;
        if (objectId is not null)
        {
            scope = ScopeForObjectId(objectId)
                ?? throw new JsRuntimeException("Cannot find context with specified id");
        }
        else
        {
            scope = world is { } target ? GetOrCreateIsolatedWorld(target).Scope : _mainScope;
        }
        return CallFunctionOnInScopeAsync(
            scope, functionDeclaration, objectId, arguments, returnByValue, awaitPromise, awaitTimeoutMs);
    }

    /// <remarks>
    /// The declaration is compiled by the captured indirect eval, at global scope in
    /// sloppy mode, as Chromium compiles it. DEVIATION from crates/obscura-js/src/runtime.rs,
    /// which pastes it into the wrapper, where it closed over the wrapper's own variables
    /// and called <c>Function.prototype.call</c> as the page had left it.
    /// </remarks>
    private async Task<RemoteObjectInfo> CallFunctionOnInScopeAsync(
        CdpScope scope,
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> arguments,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs)
    {
        BeginJavaScriptTask();
        var thisExpr = ResolveThis(scope, objectId);
        var args = BuildArgs(scope, arguments);
        var declaration = JsStringLiteral("(" + functionDeclaration + "\n)");

        scope.Counter += 1;
        var oid = scope.MakeOid(scope.Counter);
        var oidLiteral = JsStringLiteral(oid);

        if (awaitPromise)
        {
            var done = scope.Counter.ToString(CultureInfo.InvariantCulture);
            scope.Call("<callFnAsync>", $$"""
                var c = __obscura_cdp;
                var fn = c.eval({{declaration}});
                var self = ({{thisExpr}});
                var args = {{args}};
                (async function () {
                    try {
                        var r = await c.apply(fn, self, args);
                        c.objects[{{oidLiteral}}] = r;
                        c.outcomes[{{done}}] = c.settle(false, r);
                    } catch (e) {
                        c.objects[{{oidLiteral}}] = e;
                        c.outcomes[{{done}}] = c.settle(true, e);
                    }
                })();
                """);
            var outcome = await AwaitOutcomeAsync(scope, done, awaitTimeoutMs, "Runtime.callFunctionOn").ConfigureAwait(false);

            // Same rule as evaluate: a rejected call is answered, not failed, and
            // never serialized by value. Without this the wrapper stored the error
            // under the object id a success uses, so a rejection came back as an
            // ordinary result.
            scope.Store[oid] = CdpScope.Retrieval(oid);
            if (IsRejected(outcome))
            {
                return InfoFromMeta(MetaOf(outcome), oid) with { Thrown = true };
            }

            if (returnByValue)
            {
                return InfoFromJson(ToJson(scope.Objects.GetProperty(oid), scope.Engine));
            }

            return InfoFromMeta(MetaOf(outcome), oid);
        }

        if (returnByValue)
        {
            var value = scope.Call("<callFnByValue>", $$"""
                var c = __obscura_cdp;
                return c.apply(c.eval({{declaration}}), ({{thisExpr}}), {{args}});
                """);
            return InfoFromJson(ToJson(value, scope.Engine));
        }

        var remote = scope.Call("<callFnRemote>", $$"""
            var c = __obscura_cdp;
            var r = c.apply(c.eval({{declaration}}), ({{thisExpr}}), {{args}});
            c.objects[{{oidLiteral}}] = r;
            return c.settle(false, r);
            """) as ScriptObject
            ?? throw new JsRuntimeException("Runtime.callFunctionOn produced no result");
        scope.Store[oid] = CdpScope.Retrieval(oid);
        return InfoFromMeta(MetaOf(remote), oid);
    }

    public Task<RemoteObjectInfo> CallFunctionOnAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> arguments,
        bool returnByValue) =>
        CallFunctionOnForCdpAsync(functionDeclaration, objectId, arguments, returnByValue, awaitPromise: false);

    // ------------------------------------------------------------- object store

    /// <summary>
    /// Stores the value of host-authored <paramref name="jsExpression"/>, which may name
    /// the realm's CDP helpers as <c>__obscura_cdp</c>, and returns its object id.
    /// </summary>
    public string StoreObject(string jsExpression)
    {
        BeginJavaScriptTask();
        _mainScope.Counter += 1;
        var oid = _mainScope.MakeOid(_mainScope.Counter);
        try
        {
            _mainScope.Call("<store>", $"__obscura_cdp.objects[{JsStringLiteral(oid)}] = (\n{jsExpression}\n);");
        }
        catch (JsRuntimeException error)
        {
            throw new JsRuntimeException($"Store error: {error.Message}");
        }
        _objectStore[oid] = CdpScope.Retrieval(oid);
        return oid;
    }

    public RemoteObjectInfo StoreObjectWithMeta(string jsExpression) => StoreObjectWithMeta(_mainScope, jsExpression);

    /// <summary>
    /// <see cref="StoreObjectWithMeta(string)"/> in <paramref name="world"/>, or in the page
    /// realm when it is null: <c>DOM.resolveNode</c> with an <c>executionContextId</c>.
    /// </summary>
    public RemoteObjectInfo StoreObjectWithMeta(string jsExpression, IsolatedWorldTarget? world) =>
        StoreObjectWithMeta(world is { } target ? GetOrCreateIsolatedWorld(target).Scope : _mainScope, jsExpression);

    private RemoteObjectInfo StoreObjectWithMeta(CdpScope scope, string jsExpression)
    {
        BeginJavaScriptTask();
        scope.Counter += 1;
        var oid = scope.MakeOid(scope.Counter);
        ScriptObject? outcome;
        try
        {
            outcome = scope.Call("<store-meta>", $$"""
                var r = (
                {{jsExpression}}
                );
                __obscura_cdp.objects[{{JsStringLiteral(oid)}}] = r;
                return __obscura_cdp.settle(false, r);
                """) as ScriptObject;
        }
        catch (JsRuntimeException error)
        {
            throw new JsRuntimeException($"Store error: {error.Message}");
        }
        scope.Store[oid] = CdpScope.Retrieval(oid);
        return InfoFromMeta(outcome is null ? null : MetaOf(outcome), oid);
    }

    /// <summary>
    /// Evaluates host-authored <paramref name="expression"/> in the realm that minted
    /// <paramref name="objectId"/> and decodes the result as JSON, the way
    /// <see cref="Evaluate"/> does for the page realm. Null when the id names an isolated
    /// world that no longer exists.
    /// </summary>
    /// <remarks>
    /// The CDP handlers that look an object up by id (<c>Runtime.getProperties</c>,
    /// <c>DOM.describeNode</c> and the rest) name that realm's store as
    /// <c>__obscura_cdp</c>, and the store is per realm. Never pass client- or page-supplied
    /// code here.
    /// </remarks>
    public JsonNode? EvaluateInObjectRealm(string objectId, string expression)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        if (ScopeForObjectId(objectId) is not { } scope)
        {
            return null;
        }
        BeginJavaScriptTask();
        var cleaned = expression.Trim().TrimEnd(';', ' ', '\t', '\r', '\n', '\f', '\v');
        var result = scope.Call("<eval>", $"try {{ return (\n{cleaned}\n); }} catch (e) {{ return null; }}");
        return ToJson(result, scope.Engine);
    }

    public void ReleaseObject(string objectId)
    {
        if (IsolatedWorldKeyOf(objectId) != 0)
        {
            if (FindIsolatedWorld(IsolatedWorldKeyOf(objectId)) is { } world)
            {
                var scope = world.Scope;
                scope.Recipes.Remove(objectId);
                if (scope.Store.Remove(objectId))
                {
                    scope.Delete(objectId);
                }
            }
            return;
        }
        _evaluationRecipes.Remove(objectId);
        if (_objectStore.Remove(objectId))
        {
            // A frame's console handle lives in that frame's realm, which this cannot
            // reach (FrameRealm.PublishRealmObjects); deleting it here is a no-op.
            _mainScope.Delete(objectId);
        }
    }

    public void ReleaseObjectGroup()
    {
        _mainScope.Clear();
        _objectStore.Clear();
        _evaluationRecipes.Clear();
        ReleaseIsolatedWorldObjects();
    }

    public CdpObjectState TakeCdpObjectState()
    {
        var state = new CdpObjectState
        {
            ObjectCounter = _mainScope.Counter,
            EvaluationRecipes = new Dictionary<string, string>(_evaluationRecipes, StringComparer.Ordinal),
            Worlds = TakeIsolatedWorldStates(),
        };
        _evaluationRecipes.Clear();
        return state;
    }

    public void RestoreCdpObjectState(CdpObjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _mainScope.Counter = Math.Max(_mainScope.Counter, state.ObjectCounter);
        _evaluationRecipes.Clear();
        foreach (var (key, value) in state.EvaluationRecipes)
        {
            _evaluationRecipes[key] = value;
        }
        RestoreIsolatedWorldStates(state.Worlds);
    }

    internal static uint ConsoleObjectFrameId(string objectId)
    {
        if (!objectId.StartsWith("console-", StringComparison.Ordinal))
        {
            return 0;
        }
        var rest = objectId["console-".Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && uint.TryParse(rest[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameId)
            ? frameId
            : 0;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Wrap an expression or short statement bundle for <c>--eval</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A multi-statement bundle is run as a function body, which is why a
    /// bundle starting with <c>const</c> answers null: V8 gives a <c>const</c>
    /// declaration an empty completion value and the wrapper has no
    /// <c>return</c> to supply one. That is V8 behavior and is preserved
    /// deliberately.
    /// </para>
    /// <para>
    /// The expression branch strips trailing semicolons before wrapping in
    /// <c>return (...)</c>: Playwright's utility-script expression is an IIFE
    /// ending in <c>})();</c>, and leaving the <c>;</c> produces
    /// <c>return (...;);</c>, a SyntaxError that no <c>catch</c> can see. The
    /// newline before the closing paren also terminates any
    /// <c>//# sourceURL=</c> comment the caller appended.
    /// </para>
    /// </remarks>
    internal static string WrapExpression(string expression)
    {
        var trimmed = expression.Trim();
        var isMultiStatement =
            trimmed.StartsWith("var ", StringComparison.Ordinal)
            || trimmed.StartsWith("let ", StringComparison.Ordinal)
            || trimmed.StartsWith("const ", StringComparison.Ordinal)
            || trimmed.StartsWith("if ", StringComparison.Ordinal)
            || trimmed.StartsWith("for ", StringComparison.Ordinal)
            || trimmed.StartsWith("while ", StringComparison.Ordinal)
            || trimmed.StartsWith("return ", StringComparison.Ordinal);

        if (isMultiStatement)
        {
            return $"(function() {{ try {{\n{expression}\n}} catch(e) {{ return null; }} }})()";
        }

        var cleaned = trimmed.TrimEnd(';', ' ', '\t', '\r', '\n', '\f', '\v');
        return $"(function() {{ try {{ return (\n{cleaned}\n); }} catch(e) {{ return null; }} }})()";
    }

    private string? ResolveRemoteObject(CdpScope scope, string objectId)
    {
        if (scope.Store.TryGetValue(objectId, out var retrieval))
        {
            return retrieval;
        }
        if (!scope.Recipes.TryGetValue(objectId, out var expression))
        {
            return null;
        }
        try
        {
            scope.Call(
                "<restore-cdp-object>",
                $"__obscura_cdp.objects[{JsStringLiteral(objectId)}] = __obscura_cdp.eval({JsStringLiteral(expression)});");
        }
        catch (JsRuntimeException)
        {
            return null;
        }
        var restored = CdpScope.Retrieval(objectId);
        scope.Store[objectId] = restored;
        return restored;
    }

    /// <summary>
    /// The <c>this</c> of a <c>callFunctionOn</c>: the object the id names, <c>null</c> for
    /// a <c>node-N</c> id the store does not hold, and the global object otherwise.
    /// </summary>
    /// <remarks>
    /// A handle <c>Runtime.getProperties</c> minted for a property (<c>&lt;parent&gt;::key</c>)
    /// is in the realm's store but not in <see cref="CdpScope.Store"/>, so an id the host
    /// has no record of is still looked up there before falling back.
    /// </remarks>
    private string ResolveThis(CdpScope scope, string? objectId)
    {
        if (objectId is null)
        {
            return "globalThis";
        }
        var retrieval = ResolveRemoteObject(scope, objectId);
        if (retrieval is not null)
        {
            return retrieval;
        }
        // The Rust engine's node-N fallback looks the node up in a page-visible
        // globalThis._cache, which does not exist, so it answers null; that value is kept
        // without the page-writable lookup.
        var fallback = objectId.StartsWith("node-", StringComparison.Ordinal) ? "null" : "globalThis";
        var literal = JsStringLiteral(objectId);
        return $"({literal} in __obscura_cdp.objects ? __obscura_cdp.objects[{literal}] : {fallback})";
    }

    /// <summary>The arguments of a <c>callFunctionOn</c>, as a JavaScript array literal.</summary>
    private string BuildArgs(CdpScope scope, IReadOnlyList<JsonNode?> arguments)
    {
        var builder = new System.Text.StringBuilder("[");
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }
            var arg = arguments[i] as JsonObject;
            if (arg is not null && arg.TryGetPropertyValue("value", out var value))
            {
                builder.Append(value?.ToJsonString() ?? "null");
            }
            else if (arg is not null
                && arg.TryGetPropertyValue("objectId", out var objectIdNode)
                && objectIdNode?.GetValueKind() == JsonValueKind.String)
            {
                var argumentId = objectIdNode.GetValue<string>();
                // An object lives in one realm, and Chromium refuses to hand it to a
                // function running in another (port addition, SECURITY.md M6: until
                // isolated worlds had realms of their own there was only one).
                if (IsolatedWorldKeyOf(argumentId) != scope.WorldKey)
                {
                    throw new JsRuntimeException("Argument should belong to the same JavaScript world as target object");
                }
                builder.Append(ResolveRemoteObject(scope, argumentId) ?? CdpScope.Retrieval(argumentId));
            }
            else if (arg is not null
                && arg.TryGetPropertyValue("unserializableValue", out var unserializable)
                && unserializable?.GetValueKind() == JsonValueKind.String)
            {
                // Pasted as source, so only the forms CDP defines are accepted: the Rust
                // engine pastes any string, which runs whatever a client sends inside the
                // host's wrapper.
                var text = unserializable.GetValue<string>();
                if (!IsUnserializableValue(text))
                {
                    throw new JsRuntimeException("Couldn't parse value object in call argument");
                }
                builder.Append(text);
            }
            else
            {
                builder.Append("undefined");
            }
        }
        return builder.Append(']').ToString();
    }

    /// <summary><c>Infinity</c>, <c>-Infinity</c>, <c>NaN</c>, <c>-0</c> or a BigInt literal.</summary>
    internal static bool IsUnserializableValue(string text)
    {
        if (text is "Infinity" or "-Infinity" or "NaN" or "-0")
        {
            return true;
        }
        var digits = text.AsSpan();
        if (digits.Length > 0 && digits[0] == '-')
        {
            digits = digits[1..];
        }
        if (digits.Length < 2 || digits[^1] != 'n')
        {
            return false;
        }
        foreach (var ch in digits[..^1])
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Decode a ClearScript result into JSON, the way the reference decodes a
    /// <c>v8::Value</c>.
    /// </summary>
    /// <remarks>
    /// Primitives are converted directly; anything else goes through
    /// <c>JSON.stringify</c>, and a value that cannot be stringified (a
    /// function, a cyclic object) falls back to its string form, exactly as
    /// <c>v8_to_json</c> does.
    /// </remarks>
    internal JsonNode? ToJson(object? result) => ToJson(result, _engine);

    /// <summary>
    /// <see cref="ToJson(object?)"/> with <paramref name="engine"/>'s own
    /// <c>JSON.stringify</c>: a value from an isolated world is serialized by that world.
    /// </summary>
    internal JsonNode? ToJson(object? result, V8ScriptEngine engine)
    {
        switch (result)
        {
            case null:
            case Undefined:
            case VoidResult:
                return null;
            case bool flag:
                return JsonValue.Create(flag);
            case string text:
                return JsonValue.Create(text);
            case int number:
                return JsonValue.Create((double)number);
            case uint number:
                return JsonValue.Create((double)number);
            case long number:
                return JsonValue.Create((double)number);
            case ulong number:
                return JsonValue.Create((double)number);
            case float number:
                return JsonValue.Create((double)number);
            case double number:
                return JsonValue.Create(number);
            case decimal number:
                return JsonValue.Create((double)number);
        }

        if (result is ScriptObject scriptObject)
        {
            var json = Stringify(scriptObject, engine);
            if (json is not null)
            {
                try
                {
                    return JsonNode.Parse(json);
                }
                catch (JsonException)
                {
                    // Fall through to the string form, as the reference does.
                }
            }
        }
        return JsonValue.Create(result.ToString() ?? string.Empty);
    }

    /// <remarks>
    /// With the <c>JSON.stringify</c> the realm had when bootstrap.js finished
    /// (<see cref="BootstrapLoader"/>), not the global's current one: a page that replaced
    /// it answered every by-value result and every host snippet's value (SECURITY.md L10).
    /// </remarks>
    private static string? Stringify(ScriptObject value, V8ScriptEngine engine)
    {
        try
        {
            var json = BootstrapLoader.StringifyOf(engine) is { } stringify
                ? stringify.InvokeAsFunction(value)
                : ((ScriptObject)engine.Global.GetProperty("JSON")).InvokeMethod("stringify", value);
            return json as string;
        }
        catch (ScriptEngineException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static RemoteObjectInfo InfoFromJson(JsonNode? value) => value switch
    {
        // Rust stores Some(Value::Null) here, not None, so the value is present.
        null => new RemoteObjectInfo(false, "object", "null", string.Empty, "null", null, null)
        {
            HasValue = true,
        },
        JsonArray array => new RemoteObjectInfo(
            false, "object", "array", "Array",
            $"Array({array.Count.ToString(CultureInfo.InvariantCulture)})", null, value.DeepClone()),
        JsonObject => new RemoteObjectInfo(false, "object", null, "Object", "Object", null, value.DeepClone()),
        _ => value.GetValueKind() switch
        {
            JsonValueKind.Null => new RemoteObjectInfo(false, "object", "null", string.Empty, "null", null, null)
            {
                HasValue = true,
            },
            JsonValueKind.True or JsonValueKind.False => new RemoteObjectInfo(
                false, "boolean", null, string.Empty,
                value.GetValue<bool>() ? "true" : "false", null, value.DeepClone()),
            JsonValueKind.Number => new RemoteObjectInfo(
                false, "number", null, string.Empty,
                FormatNumber(value.GetValue<double>()), null, value.DeepClone()),
            _ => new RemoteObjectInfo(
                false, "string", null, string.Empty, value.GetValue<string>(), null, value.DeepClone()),
        },
    };

    /// <summary>
    /// Formats a number for a CDP <c>RemoteObject.description</c>.
    /// </summary>
    /// <remarks>
    /// Rust builds this with <c>serde_json::Number::to_string</c> on an f64, which
    /// keeps the fractional part: <c>Runtime.evaluate("1+1")</c> reports
    /// <c>"2.0"</c>, not <c>"2"</c>. Formatting it as an integer here produced a
    /// different string than the reference server for every whole-numbered result.
    /// Confirmed by diffing the wire output of both servers.
    /// </remarks>
    private static string FormatNumber(double value) => PocketCalculator.Js.Ops.SerdeJson.NumberText(value);

    internal static RemoteObjectInfo InfoFromMeta(JsonNode? meta, string? objectId)
    {
        var jsType = MetaString(meta, "type") ?? "undefined";
        var subtype = MetaString(meta, "subtype");
        var className = MetaString(meta, "className") ?? string.Empty;
        var description = MetaString(meta, "description") ?? string.Empty;
        var value = jsType is not ("object" or "function") && MetaString(meta, "description") is { } raw
            ? JsonValue.Create(raw)
            : null;
        return new RemoteObjectInfo(false, jsType, subtype, className, description, objectId, value);
    }

    private static string? MetaString(JsonNode? meta, string key) =>
        meta is JsonObject obj
        && obj.TryGetPropertyValue(key, out var node)
        && node?.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : null;
}
