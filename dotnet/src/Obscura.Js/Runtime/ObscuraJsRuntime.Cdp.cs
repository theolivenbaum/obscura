using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;

namespace Obscura.Js.Runtime;

/// <summary>
/// Evaluation and the CDP remote-object store.
/// </summary>
public sealed partial class ObscuraJsRuntime
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
    public async Task<RemoteObjectInfo> EvaluateForCdpWithTimeoutAsync(
        string expression,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs)
    {
        BeginJavaScriptTask();

        _objectCounter += 1;
        var oid = MakeOid(_objectCounter);
        var sourceLiteral = JsStringLiteral(expression);
        var doneCounter = _objectCounter;

        var metaCode = awaitPromise
            ? $$"""
                (async function() {
                    try {
                        var __result = await (0, eval)({{sourceLiteral}});
                        globalThis.__obscura_objects['{{oid}}'] = __result;
                        globalThis.__obscura_await_meta = {{MetaExtractJs("__result")}};
                        globalThis.__obscura_await_rejected = false;
                    } catch(e) {
                        globalThis.__obscura_objects['{{oid}}'] = e;
                        globalThis.__obscura_await_meta = {{MetaExtractJs("e")}};
                        globalThis.__obscura_await_rejected = true;
                    }
                    globalThis.__obscura_done_{{doneCounter}} = true;
                })()
                """
            // The synchronous half writes the same two globals as the await half,
            // so one outcome protocol covers both. Before this, a throw here became
            // `__result = undefined`: a page error was indistinguishable from an
            // expression with no value.
            : $$"""
                (function() {
                    var __result;
                    try {
                        __result = (0, eval)({{sourceLiteral}});
                    } catch(e) {
                        globalThis.__obscura_objects['{{oid}}'] = e;
                        globalThis.__obscura_await_meta = {{MetaExtractJs("e")}};
                        globalThis.__obscura_await_rejected = true;
                        return globalThis.__obscura_await_meta;
                    }
                    globalThis.__obscura_objects['{{oid}}'] = __result;
                    globalThis.__obscura_await_rejected = false;
                    return {{MetaExtractJs("__result")}};
                })()
                """;

        var result = ExecuteRuntimeScript("<eval-remote>", metaCode);

        object? metaValue;
        if (awaitPromise)
        {
            var sentinel = $"globalThis.__obscura_done_{doneCounter} === true";
            var settled = await ResolvePromisesUntilAsync(
                runtime =>
                {
                    try
                    {
                        return ToJson(runtime.ExecuteRuntimeScript("<done?>", sentinel))?.GetValue<bool>() ?? false;
                    }
                    catch (JsRuntimeException)
                    {
                        return false;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                },
                awaitTimeoutMs).ConfigureAwait(false);
            if (!settled)
            {
                throw new JsRuntimeException(
                    $"Runtime.evaluate promise did not settle within {awaitTimeoutMs}ms");
            }
            metaValue = ExecuteRuntimeScript("<readMeta>", "globalThis.__obscura_await_meta");
        }
        else
        {
            metaValue = result;
        }

        // Neither a rejection nor a synchronous throw is a protocol failure. CDP
        // answers the command and puts the value in exceptionDetails, so it
        // travels back as a remote object flagged Thrown, by reference even when
        // the caller asked for a value: JSON.stringify(new Error("boom")) is {},
        // so serializing it would throw the message away.
        if (AsBool(ExecuteRuntimeScript("<readRejected>", "globalThis.__obscura_await_rejected")))
        {
            return ThrownInfo(oid);
        }

        var metaJson = DecodeMeta(ToJson(metaValue));
        _objectStore[oid] = $"globalThis.__obscura_objects['{oid}']";
        if (!returnByValue)
        {
            _evaluationRecipes[oid] = expression;
        }

        if (returnByValue)
        {
            var read = ExecuteRuntimeScript("<readResult>", $"globalThis.__obscura_objects['{oid}']");
            return InfoFromJson(ToJson(read));
        }

        return InfoFromMeta(metaJson, oid);
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

    public async Task<RemoteObjectInfo> CallFunctionOnForCdpWithTimeoutAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> arguments,
        bool returnByValue,
        bool awaitPromise,
        ulong awaitTimeoutMs)
    {
        BeginJavaScriptTask();
        var thisExpr = ResolveThis(objectId);
        var (setup, argsList) = BuildArgs(arguments);

        _objectCounter += 1;
        var oid = MakeOid(_objectCounter);

        if (awaitPromise)
        {
            var doneCounter = _objectCounter;
            var code = $$"""
                (async function() {
                    {{setup}}
                    var __fn = ({{functionDeclaration}});
                    var __this = ({{thisExpr}});
                    var __result;
                    try {
                        __result = await __fn.call(__this, {{argsList}});
                        globalThis.__obscura_objects['{{oid}}'] = __result;
                        globalThis.__obscura_await_meta = {{MetaExtractJs("__result")}};
                        globalThis.__obscura_await_rejected = false;
                    } catch(e) {
                        __result = e;
                        globalThis.__obscura_objects['{{oid}}'] = e;
                        globalThis.__obscura_await_meta = {{MetaExtractJs("__result")}};
                        globalThis.__obscura_await_rejected = true;
                    } finally {
                        globalThis.__obscura_done_{{doneCounter}} = true;
                    }
                })()
                """;
            ExecuteRuntimeScript("<callFnAsync>", code);

            var sentinel = $"globalThis.__obscura_done_{doneCounter} === true";
            var settled = await ResolvePromisesUntilAsync(
                runtime =>
                {
                    try
                    {
                        return ToJson(runtime.ExecuteRuntimeScript("<done?>", sentinel))?.GetValue<bool>() ?? false;
                    }
                    catch (JsRuntimeException)
                    {
                        return false;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                },
                awaitTimeoutMs).ConfigureAwait(false);
            if (!settled)
            {
                throw new JsRuntimeException(
                    $"Runtime.callFunctionOn promise did not settle within {awaitTimeoutMs}ms");
            }

            // Same rule as evaluate: a rejected call is answered, not failed, and
            // never serialized by value. Without this the wrapper stored the error
            // under the object id a success uses, so a rejection came back as an
            // ordinary result.
            if (AsBool(ExecuteRuntimeScript("<readRejected>", "globalThis.__obscura_await_rejected")))
            {
                return ThrownInfo(oid);
            }

            if (returnByValue)
            {
                var read = ExecuteRuntimeScript("<readResult>", $"globalThis.__obscura_objects['{oid}']");
                return InfoFromJson(ToJson(read));
            }

            var metaResult = ExecuteRuntimeScript("<readMeta>", "globalThis.__obscura_await_meta");
            _objectStore[oid] = $"globalThis.__obscura_objects['{oid}']";
            return InfoFromMeta(DecodeMeta(ToJson(metaResult)), oid);
        }

        if (returnByValue)
        {
            var byValue = $$"""
                (function() {
                    {{setup}}
                    var __fn = ({{functionDeclaration}});
                    var __this = ({{thisExpr}});
                    return __fn.call(__this, {{argsList}});
                })()
                """;
            var value = ExecuteRuntimeScript("<callFnByValue>", byValue);
            return InfoFromJson(ToJson(value));
        }

        var remote = $$"""
            (function() {
                {{setup}}
                var __fn = ({{functionDeclaration}});
                var __this = ({{thisExpr}});
                var __result = __fn.call(__this, {{argsList}});
                globalThis.__obscura_objects['{{oid}}'] = __result;
                return {{MetaExtractJs("__result")}};
            })()
            """;
        var meta = ExecuteRuntimeScript("<callFnRemote>", remote);
        _objectStore[oid] = $"globalThis.__obscura_objects['{oid}']";
        return InfoFromMeta(DecodeMeta(ToJson(meta)), oid);
    }

    public Task<RemoteObjectInfo> CallFunctionOnAsync(
        string functionDeclaration,
        string? objectId,
        IReadOnlyList<JsonNode?> arguments,
        bool returnByValue) =>
        CallFunctionOnForCdpAsync(functionDeclaration, objectId, arguments, returnByValue, awaitPromise: false);

    // ------------------------------------------------------------- object store

    public string StoreObject(string jsExpression)
    {
        BeginJavaScriptTask();
        _objectCounter += 1;
        var oid = MakeOid(_objectCounter);
        try
        {
            ExecuteRuntimeScript("<store>", $"globalThis.__obscura_objects['{oid}'] = ({jsExpression});");
        }
        catch (JsRuntimeException error)
        {
            throw new JsRuntimeException($"Store error: {error.Message}");
        }
        _objectStore[oid] = $"globalThis.__obscura_objects['{oid}']";
        return oid;
    }

    public RemoteObjectInfo StoreObjectWithMeta(string jsExpression)
    {
        BeginJavaScriptTask();
        _objectCounter += 1;
        var oid = MakeOid(_objectCounter);
        var code = $$"""
            (function() {
                var __result = (
            {{jsExpression}}
            );
                globalThis.__obscura_objects['{{oid}}'] = __result;
                return {{MetaExtractJs("__result")}};
            })()
            """;
        object? result;
        try
        {
            result = ExecuteRuntimeScript("<store-meta>", code);
        }
        catch (JsRuntimeException error)
        {
            throw new JsRuntimeException($"Store error: {error.Message}");
        }
        _objectStore[oid] = $"globalThis.__obscura_objects['{oid}']";
        return InfoFromMeta(DecodeMeta(ToJson(result)), oid);
    }

    public void ReleaseObject(string objectId)
    {
        _evaluationRecipes.Remove(objectId);
        if (!_objectStore.Remove(objectId))
        {
            return;
        }
        var frameId = ConsoleObjectFrameId(objectId);
        var code = frameId == 0
            ? $"delete globalThis.__obscura_objects['{objectId}'];"
            : $"delete globalThis.__obscura_frameObjects[{frameId}]?.window?.__obscura_objects['{objectId}'];";
        TryRun("<release>", code);
    }

    public void ReleaseObjectGroup()
    {
        var frameIds = _objectStore.Keys
            .Select(ConsoleObjectFrameId)
            .Where(frameId => frameId != 0)
            .Distinct()
            .Order()
            .ToArray();
        var code = new System.Text.StringBuilder("globalThis.__obscura_objects = {};");
        foreach (var frameId in frameIds)
        {
            code.Append(CultureInfo.InvariantCulture, $"if(globalThis.__obscura_frameObjects[{frameId}]?.window)globalThis.__obscura_frameObjects[{frameId}].window.__obscura_objects={{}};");
        }
        TryRun("<releaseGroup>", code.ToString());
        _objectStore.Clear();
        _evaluationRecipes.Clear();
    }

    public CdpObjectState TakeCdpObjectState()
    {
        var state = new CdpObjectState
        {
            ObjectCounter = _objectCounter,
            EvaluationRecipes = new Dictionary<string, string>(_evaluationRecipes, StringComparer.Ordinal),
        };
        _evaluationRecipes.Clear();
        return state;
    }

    public void RestoreCdpObjectState(CdpObjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _objectCounter = Math.Max(_objectCounter, state.ObjectCounter);
        _evaluationRecipes.Clear();
        foreach (var (key, value) in state.EvaluationRecipes)
        {
            _evaluationRecipes[key] = value;
        }
    }

    private void TryRun(string name, string source)
    {
        try
        {
            ExecuteRuntimeScript(name, source);
        }
        catch (JsRuntimeException)
        {
            // The reference discards these results; a missing frame registry
            // must not fail a release.
        }
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

    private static string MakeOid(ulong counter) =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"injectedScriptId\":1,\"id\":{counter}}}");

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

    /// <summary>
    /// The metadata probe both CDP wrappers use to describe a value without
    /// serializing it.
    /// </summary>
    internal static string MetaExtractJs(string varName) =>
        $$"""
        (function(v) {
                        var t = typeof v;
                        var st = null, cn = '', desc = '';
                        if (v === null) { t = 'object'; st = 'null'; }
                        else if (v === undefined) { t = 'undefined'; }
                        else if (Array.isArray(v)) {
                            st = 'array'; cn = 'Array';
                            desc = 'Array(' + v.length + ')';
                        }
                        else if (t === 'object' && typeof v._nid === 'number') {
                            st = 'node';
                            cn = v.constructor ? v.constructor.name : 'Node';
                            if (v.nodeType === 9) cn = 'HTMLDocument';
                            else if (v.nodeType === 1) cn = 'HTML' + (v.tagName || 'Element').charAt(0) + (v.tagName || 'Element').slice(1).toLowerCase() + 'Element';
                            desc = v.tagName ? v.tagName.toLowerCase() : (v.nodeName || 'node');
                        }
                        else if (t === 'function') {
                            cn = 'Function';
                            desc = v.name ? 'function ' + v.name + '()' : 'function()';
                        }
                        else if (t === 'object' && v instanceof Error) {
                            st = 'error';
                            cn = (v.constructor && v.constructor.name) || 'Error';
                            desc = (typeof v.stack === 'string' && v.stack) ? v.stack
                                 : (cn + (v.message ? ': ' + v.message : ''));
                        }
                        else if (t === 'object') {
                            cn = (v.constructor && v.constructor.name) || 'Object';
                            desc = cn;
                        }
                        else { desc = String(v); }
                        return JSON.stringify({type:t,subtype:st,className:cn,description:desc});
                    })({{varName}})
        """;

    private string? ResolveRemoteObject(string objectId)
    {
        if (_objectStore.TryGetValue(objectId, out var retrieval))
        {
            return retrieval;
        }
        if (!_evaluationRecipes.TryGetValue(objectId, out var expression))
        {
            return null;
        }
        var literal = JsStringLiteral(objectId);
        try
        {
            ExecuteRuntimeScript(
                "<restore-cdp-object>",
                $"globalThis.__obscura_objects[{literal}] = (\n{expression}\n);");
        }
        catch (JsRuntimeException)
        {
            return null;
        }
        var restored = $"globalThis.__obscura_objects[{literal}]";
        _objectStore[objectId] = restored;
        return restored;
    }

    private string ResolveThis(string? objectId)
    {
        if (objectId is null)
        {
            return "globalThis";
        }
        var retrieval = ResolveRemoteObject(objectId);
        if (retrieval is not null)
        {
            return retrieval;
        }
        if (objectId.StartsWith("node-", StringComparison.Ordinal))
        {
            var nid = objectId["node-".Length..];
            if (nid.Length == 0)
            {
                nid = "0";
            }
            return "(function() { "
                + $"var nid = {nid}; "
                + "var cache = globalThis._cache || new Map(); "
                + "if (cache.has(nid)) return cache.get(nid); "
                + "return null; "
                + "})()";
        }
        return "globalThis";
    }

    private (string Setup, string Args) BuildArgs(IReadOnlyList<JsonNode?> arguments)
    {
        var setupLines = new List<string>(arguments.Count);
        var argNames = new List<string>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            var argName = $"__arg{i.ToString(CultureInfo.InvariantCulture)}";
            var arg = arguments[i] as JsonObject;
            if (arg is not null && arg.TryGetPropertyValue("value", out var value))
            {
                setupLines.Add($"var {argName} = {value?.ToJsonString() ?? "null"};");
            }
            else if (arg is not null
                && arg.TryGetPropertyValue("objectId", out var objectIdNode)
                && objectIdNode?.GetValueKind() == JsonValueKind.String)
            {
                var retrieval = ResolveRemoteObject(objectIdNode.GetValue<string>());
                setupLines.Add($"var {argName} = {retrieval ?? "undefined"};");
            }
            else if (arg is not null
                && arg.TryGetPropertyValue("unserializableValue", out var unserializable)
                && unserializable?.GetValueKind() == JsonValueKind.String)
            {
                setupLines.Add($"var {argName} = {unserializable.GetValue<string>()};");
            }
            else
            {
                setupLines.Add($"var {argName} = undefined;");
            }
            argNames.Add(argName);
        }
        return (string.Join("\n", setupLines), string.Join(", ", argNames));
    }

    private static bool AsBool(object? value) => value is bool flag && flag;

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
    internal JsonNode? ToJson(object? result)
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
            var json = Stringify(scriptObject);
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

    private string? Stringify(ScriptObject value)
    {
        try
        {
            var json = ((ScriptObject)_engine.Global.GetProperty("JSON")).InvokeMethod("stringify", value);
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

    private static JsonNode? DecodeMeta(JsonNode? meta)
    {
        if (meta?.GetValueKind() != JsonValueKind.String)
        {
            return meta;
        }
        try
        {
            return JsonNode.Parse(meta.GetValue<string>());
        }
        catch (JsonException)
        {
            return meta;
        }
    }

    /// <summary>
    /// Build the remote object for a value that was thrown, or that a promise
    /// rejected with.
    /// </summary>
    /// <remarks>
    /// Both wrappers already stored the value under the id and put its metadata
    /// in <c>__obscura_await_meta</c>. The <c>Thrown</c> mark is what lets the
    /// CDP layer answer with <c>exceptionDetails</c> rather than fail the
    /// command or present the value as the result.
    /// </remarks>
    private RemoteObjectInfo ThrownInfo(string oid)
    {
        var meta = DecodeMeta(ToJson(ExecuteRuntimeScript("<readMeta>", "globalThis.__obscura_await_meta")));
        _objectStore[oid] = $"globalThis.__obscura_objects['{oid}']";
        return InfoFromMeta(meta, oid) with { Thrown = true };
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
    private static string FormatNumber(double value) => Obscura.Js.Ops.SerdeJson.NumberText(value);

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
