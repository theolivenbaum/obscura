using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using Obscura.Dom;
using Obscura.Js.Ops;

namespace Obscura.Js.Runtime;

/// <summary>
/// One child browsing context: its own realm, document and origin, living in
/// the page's isolate.
/// </summary>
/// <remarks>
/// <para>
/// An iframe is a separate browsing context. Without a realm of its own, a
/// frame's HTML is fetched and its body dropped into a detached document in the
/// <em>parent's</em> realm, so no script inside a frame ever runs (#600).
/// </para>
/// <para>
/// The Rust engine restores a second <c>v8::Context</c> from the startup
/// snapshot and fills its op table from the page realm's, which is legal
/// because native function objects are shareable between contexts of one
/// isolate. ClearScript exposes neither snapshots nor realms, so a frame is a
/// second <see cref="V8ScriptEngine"/> on the page's
/// <see cref="V8Runtime"/> - still a second context in the same isolate - that
/// re-runs bootstrap.js and gets its own op table bound to the same host op
/// implementations, with the frame's own <see cref="ObscuraState"/>.
/// </para>
/// <para>
/// What that costs, precisely: ClearScript refuses a script object created by
/// one engine when it is handed to another ("Invalid host item"), so a frame's
/// real <c>window</c> and <c>document</c> cannot be published into the page
/// realm the way <c>publish_realm_objects</c> does. Same-origin
/// <c>contentWindow.someGlobal</c> and <c>contentDocument</c> therefore do not
/// resolve to the frame's live objects here; a cross-origin frame behaves the
/// same as in the reference, because it is opaque either way.
/// </para>
/// </remarks>
public sealed class FrameRealm : IDisposable
{
    private readonly V8ScriptEngine _engine;
    private readonly ObscuraJsRuntime _parent;
    private bool _disposed;

    private FrameRealm(
        ObscuraJsRuntime parent,
        V8ScriptEngine engine,
        uint frameId,
        uint parentFrameId,
        string url,
        string origin)
    {
        _parent = parent;
        _engine = engine;
        FrameId = frameId;
        ParentFrameId = parentFrameId;
        Url = url;
        Origin = origin;
    }

    /// <summary>Identity globals a frame inherits verbatim from its parent.</summary>
    /// <remarks>
    /// A frame must present the same identity as its parent: anti-bot code
    /// fingerprints inside the frame and compares it with the top document.
    /// Copying the values the parent already has makes that true by
    /// construction, rather than relying on a caller to reapply the same
    /// settings to both.
    /// </remarks>
    private static readonly string[] IdentityGlobals =
    [
        "__obscura_ua",
        "__obscura_platform",
        "__obscura_ua_platform",
        "__obscura_ua_platform_version",
        "__obscura_stealth",
        "__obscura_geo_lat",
        "__obscura_geo_lon",
    ];

    public uint FrameId { get; }

    public uint ParentFrameId { get; }

    public string Url { get; }

    /// <summary>The frame's origin, or <c>"null"</c> for an opaque origin.</summary>
    public string Origin { get; }

    /// <summary>The frame's own state, as registered in the realm table.</summary>
    public ObscuraState State { get; private init; } = null!;

    /// <summary>
    /// Builds a frame realm around an already-fetched document.
    /// </summary>
    /// <remarks>
    /// The frame inherits the page's browser identity and its shared resources
    /// by copying them from the parent rather than by being told them, so the
    /// two cannot drift apart.
    /// </remarks>
    public static FrameRealm? Create(
        ObscuraJsRuntime parent,
        uint frameId,
        uint parentFrameId,
        string url,
        string html)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var state = new ObscuraState
        {
            Dom = HtmlParsing.ParseHtml(html),
            Url = url,
            FrameId = frameId,
        };
        parent.ShareResourcesWith(state);

        var engine = parent.CreateRealmEngine();
        try
        {
            // The realm's op table is filled the same way the page's is, with
            // this frame's state, which is what makes an op called from the
            // frame resolve against the frame's own document.
            BootstrapLoader.Install(engine, ops => parent.BindRealmOps(ops, state));
            CopyIdentityToRealm(parent, engine);
        }
        catch (ScriptEngineException)
        {
            engine.Dispose();
            return null;
        }

        // Only a same-origin frame is reachable from the page. A cross-origin
        // frame is opaque, and nothing about it is published.
        var origin = OriginOf(url);
        var sameOrigin = !string.Equals(origin, "null", StringComparison.Ordinal)
            && string.Equals(origin, parent.PageOrigin, StringComparison.Ordinal);

        parent.RealmStates.Register(engine, frameId, state);

        var realm = new FrameRealm(parent, engine, frameId, parentFrameId, url, origin) { State = state };
        parent.RegisterRealm(realm);

        // Both ids before init, not after: init is what installs `parent` and
        // `top`, and a document that runs even one script believing it is
        // top-level has already taken the wrong branch.
        try
        {
            realm.Run(
                $"globalThis.__obscura_frameId = {frameId.ToString(CultureInfo.InvariantCulture)};"
                + $"globalThis.__obscura_parentFrameId = {parentFrameId.ToString(CultureInfo.InvariantCulture)};"
                + "globalThis.__obscura_init();");
        }
        catch (JsRuntimeException)
        {
            realm.Dispose();
            return null;
        }

        if (sameOrigin)
        {
            realm.PublishRealmObjects();
        }
        return realm;
    }

    /// <summary>
    /// Fires the frame document's lifecycle events, in spec order.
    /// </summary>
    /// <remarks>
    /// The page's own document gets these; a frame's did not, and a document
    /// that is never told it finished loading will not run the work scripts
    /// defer until then. That is most of what a widget does: a frame can talk
    /// to its parent perfectly and still never build its interface, which looks
    /// like a rendering problem and is a lifecycle one.
    /// </remarks>
    public void DispatchLoadEvents() =>
        ExecuteScript(
            "globalThis.__documentReadyState__ = 'interactive';"
            + "try { document.dispatchEvent(new Event('DOMContentLoaded', "
            + "{ bubbles: false, cancelable: false })); } catch (_) {}"
            + "try { window.dispatchEvent(new Event('DOMContentLoaded', "
            + "{ bubbles: false, cancelable: false })); } catch (_) {}"
            + "globalThis.__documentReadyState__ = 'complete';"
            + "try { document.dispatchEvent(new Event('readystatechange')); } catch (_) {}"
            + "try { const loadEvent = new Event('load', "
            + "{ bubbles: false, cancelable: false }); "
            + "if (typeof window.onload === 'function') { try { window.onload.call(window, loadEvent); } catch (_) {} } "
            + "try { window.dispatchEvent(loadEvent); } catch (_) {} } catch (_) {}");

    /// <summary>Delivers a <c>postMessage</c> that another realm sent to this one.</summary>
    public void DeliverMessage(string dataJson, string origin, uint sourceFrameId, string targetOrigin) =>
        ExecuteScript(
            $"globalThis.__obscura_deliverMessage({EncodeJsonArgument(dataJson)}, "
            + $"{EncodeJsonArgument(origin)}, {sourceFrameId.ToString(CultureInfo.InvariantCulture)}, "
            + $"{EncodeJsonArgument(targetOrigin)});");

    /// <summary>Sets the frame document's viewport before any of its scripts run.</summary>
    public void SetViewport(double width, double height)
    {
        var w = double.IsFinite(width) && width > 0 ? width : 300.0;
        var h = double.IsFinite(height) && height > 0 ? height : 150.0;
        ExecuteScript(
            $"globalThis.innerWidth={Format(w)};globalThis.innerHeight={Format(h)};"
            + "if(globalThis.visualViewport){"
            + $"globalThis.visualViewport.width={Format(w)};"
            + $"globalThis.visualViewport.height={Format(h)};"
            + "}");
    }

    /// <summary>
    /// Whether script from <paramref name="otherOrigin"/> may reach into this
    /// frame's DOM. Two opaque origins are never same-origin, which is why
    /// <c>"null"</c> never matches.
    /// </summary>
    public bool IsSameOriginAs(string otherOrigin) =>
        !string.Equals(Origin, "null", StringComparison.Ordinal)
        && string.Equals(Origin, otherOrigin, StringComparison.Ordinal);

    /// <summary>
    /// Runs a script inside the frame, reporting a script error as a throw.
    /// </summary>
    public void ExecuteScript(string source) => Run(source);

    /// <summary>Evaluates an expression inside the frame and decodes it as JSON.</summary>
    public JsonNode? Evaluate(string expression)
    {
        var json = Run($"JSON.stringify({expression})");
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new JsRuntimeException(error.Message);
        }
    }

    /// <summary>
    /// Runs the frame document's classic scripts, in document order.
    /// </summary>
    /// <remarks>
    /// <paramref name="loadExternal"/> resolves a <c>src=</c> script to its
    /// source text; returning null skips it, which is what a failed subresource
    /// fetch looks like to the page. One script throwing does not stop the ones
    /// after it, matching how a browser treats separate classic scripts. Module
    /// scripts are skipped and reported: they need the frame's own module
    /// loader, which is not wired up. Returns one message per script that
    /// failed or was skipped.
    /// </remarks>
    public IReadOnlyList<string> RunDocumentScripts(Func<string, string?> loadExternal)
    {
        ArgumentNullException.ThrowIfNull(loadExternal);

        List<DocumentScript> scripts;
        try
        {
            scripts = ListScripts();
        }
        catch (JsRuntimeException error)
        {
            return [error.Message];
        }

        var problems = new List<string>();
        for (var index = 0; index < scripts.Count; index++)
        {
            var script = scripts[index];
            if (!script.IsClassic)
            {
                if (string.Equals(script.Type, "module", StringComparison.Ordinal))
                {
                    problems.Add($"frame module script {index.ToString(CultureInfo.InvariantCulture)} skipped: not supported");
                }
                continue;
            }

            string name;
            string source;
            if (script.Src.Length == 0)
            {
                name = $"inline {index.ToString(CultureInfo.InvariantCulture)}";
                source = script.Text;
            }
            else
            {
                var resolved = Resolve(script.Src);
                var loaded = loadExternal(resolved);
                if (loaded is null)
                {
                    problems.Add($"frame script {resolved} could not be loaded");
                    continue;
                }
                name = resolved;
                source = loaded;
            }
            if (source.Trim().Length == 0)
            {
                continue;
            }
            try
            {
                ExecuteScript(source);
            }
            catch (JsRuntimeException error)
            {
                problems.Add($"frame script {name} failed: {error.Message}");
            }
        }
        return problems;
    }

    /// <summary>
    /// Absolute URLs of the frame's <c>src=</c> classic scripts, in document
    /// order. A caller that fetches over the network needs the list before
    /// running anything, because <see cref="RunDocumentScripts"/> resolves
    /// sources synchronously.
    /// </summary>
    public IReadOnlyList<string> ExternalScriptUrls()
    {
        try
        {
            return ListScripts()
                .Where(script => script.IsClassic && script.Src.Length > 0)
                .Select(script => Resolve(script.Src))
                .ToArray();
        }
        catch (JsRuntimeException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _parent.RealmStates.Forget(_engine);
        _parent.ForgetRealm(this);
        _engine.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="source"/> in the frame's realm and returns its value
    /// as a string. Ops called from it find the frame's document because the op
    /// table was bound with the frame's state.
    /// </summary>
    private string? Run(string source)
    {
        var previous = _parent.RealmStates.Current;
        _parent.RealmStates.Current = State;
        try
        {
            var value = _engine.Evaluate(new DocumentInfo("<frame>"), source);
            return value switch
            {
                null or Undefined or VoidResult => null,
                string text => text,
                _ => value.ToString(),
            };
        }
        catch (ScriptInterruptedException)
        {
            throw new JsRuntimeException("execution terminated");
        }
        catch (ScriptEngineException error)
        {
            throw new JsRuntimeException(error.Message);
        }
        finally
        {
            _parent.RealmStates.Current = previous;
        }
    }

    /// <summary>
    /// Publishes what this realm can share with the page realm under
    /// <c>__obscura_frameObjects[frameId]</c>.
    /// </summary>
    /// <remarks>
    /// In the reference this hands the page the frame's <em>real</em>
    /// <c>window</c> and <c>document</c>, which is the whole point of staying in
    /// one isolate. ClearScript will not let a script object cross engines, so
    /// the entry is registered without them; a page reading
    /// <c>contentWindow.someGlobal</c> gets undefined rather than the frame's
    /// live value. Recorded as a known gap rather than faked with a copy, which
    /// would be worse: a copy looks live and silently is not.
    /// </remarks>
    private void PublishRealmObjects()
    {
        try
        {
            _parent.ExecuteScript(
                "<publish-frame-objects>",
                "globalThis.__obscura_frameObjects = globalThis.__obscura_frameObjects || {};"
                + $"globalThis.__obscura_frameObjects[{FrameId.ToString(CultureInfo.InvariantCulture)}] = "
                + "globalThis.__obscura_frameObjects["
                + FrameId.ToString(CultureInfo.InvariantCulture)
                + "] || {};");
        }
        catch (JsRuntimeException)
        {
            // The registry is best-effort; a page without it simply cannot
            // reach into the frame.
        }
    }

    private static void CopyIdentityToRealm(ObscuraJsRuntime parent, V8ScriptEngine realm)
    {
        foreach (var name in IdentityGlobals)
        {
            var value = parent.Engine.Evaluate($"globalThis.{name}");
            if (value is null or Undefined or VoidResult)
            {
                continue;
            }
            var literal = value switch
            {
                bool flag => flag ? "true" : "false",
                string text => JsonSerializer.Serialize(text),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "undefined",
            };
            realm.Execute("<copy-identity>", $"globalThis.{name} = {literal};");
        }
    }

    /// <summary>
    /// Resolves a subresource URL against the frame's own document URL, not the
    /// parent's. A relative <c>src</c> in a frame is relative to the frame.
    /// </summary>
    private string Resolve(string src) =>
        Uri.TryCreate(Url, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, src, out var resolved)
            ? resolved.ToString()
            : src;

    private List<DocumentScript> ListScripts()
    {
        JsonNode? listed;
        try
        {
            listed = Evaluate("""
                [...document.querySelectorAll('script')].map(node => ({
                                src: node.getAttribute('src') || '',
                                type: (node.getAttribute('type') || '').toLowerCase(),
                                text: node.textContent || '',
                            }))
                """);
        }
        catch (JsRuntimeException error)
        {
            throw new JsRuntimeException($"could not list frame scripts: {error.Message}");
        }

        var scripts = new List<DocumentScript>();
        if (listed is not JsonArray array)
        {
            return scripts;
        }
        foreach (var entry in array)
        {
            if (entry is not JsonObject obj)
            {
                continue;
            }
            scripts.Add(new DocumentScript(
                Text(obj, "src"),
                Text(obj, "type"),
                Text(obj, "text")));
        }
        return scripts;

        static string Text(JsonObject obj, string key) =>
            obj.TryGetPropertyValue(key, out var node) && node?.GetValueKind() == JsonValueKind.String
                ? node.GetValue<string>()
                : string.Empty;
    }

    private sealed record DocumentScript(string Src, string Type, string Text)
    {
        /// <summary>
        /// An empty type, or a JavaScript MIME type, is a classic script.
        /// Anything else is data or a module.
        /// </summary>
        public bool IsClassic =>
            Type.Length == 0
            || Type is "text/javascript" or "application/javascript" or "text/ecmascript";
    }

    /// <summary>
    /// Embeds a string in JavaScript source as a literal, so a payload holding
    /// quotes or newlines cannot end the literal and be read as code.
    /// </summary>
    private static string EncodeJsonArgument(string value) => JsonSerializer.Serialize(value);

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Serializes an origin the way <c>location.origin</c> does, using
    /// <c>"null"</c> for schemes that have no tuple origin.
    /// </summary>
    public static string OriginOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return "null";
        }
        if (parsed.Scheme is not ("http" or "https" or "ws" or "wss" or "ftp"))
        {
            return "null";
        }
        return parsed.IsDefaultPort
            ? $"{parsed.Scheme}://{parsed.Host}"
            : $"{parsed.Scheme}://{parsed.Host}:{parsed.Port.ToString(CultureInfo.InvariantCulture)}";
    }
}
