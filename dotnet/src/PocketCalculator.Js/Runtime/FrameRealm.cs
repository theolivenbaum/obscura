using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Net;

namespace PocketCalculator.Js.Runtime;

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
/// implementations, with the frame's own <see cref="PocketCalculatorState"/>.
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
    private readonly PocketCalculatorJsRuntime _parent;
    private bool _disposed;

    /// <summary>
    /// The realm's own <c>Deno.core</c> shim, kept only so its promise-rejection
    /// callback can be detached before the engine goes away. See
    /// <see cref="DenoCoreShim.Detach"/>.
    /// </summary>
    private DenoCoreShim? _shim;

    /// <summary>The DOM collector's half for this frame's document.</summary>
    private RealmDomGc? _domGc;

    private FrameRealm(
        PocketCalculatorJsRuntime parent,
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
    public PocketCalculatorState State { get; private init; } = null!;

    internal V8ScriptEngine Engine => _engine;

    /// <summary>The realm's host helpers (<see cref="HostScript"/>), for CDP and isolated worlds.</summary>
    internal ScriptObject? HostHelpers => _shim?.HostHelpers;

    /// <summary>
    /// Builds a frame realm around an already-fetched document.
    /// </summary>
    /// <remarks>
    /// The frame inherits the page's browser identity and its shared resources
    /// by copying them from the parent rather than by being told them, so the
    /// two cannot drift apart.
    /// </remarks>
    public static FrameRealm? Create(
        PocketCalculatorJsRuntime parent,
        uint frameId,
        uint parentFrameId,
        string url,
        string html,
        bool opaqueOrigin = false,
        ReferrerPolicy? referrerPolicyHeader = null)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var state = new PocketCalculatorState
        {
            Dom = HtmlParsing.ParseHtml(html),
            Url = url,
            FrameId = frameId,
            // A sandboxed frame without allow-same-origin: its document's origin is opaque
            // whatever its URL (port addition; the Rust engine has no frame sandboxing).
            OpaqueOrigin = opaqueOrigin,
            // The frame document's own Referrer-Policy header governs what it fetches, as
            // in Chromium 141; srcdoc and about:blank frames inherit below instead.
            ReferrerPolicyHeader = referrerPolicyHeader,
        };
        parent.ShareResourcesWith(state);

        // Mixed content follows the ancestor chain: the nearest https ancestor decides
        // for a frame that is not itself secure.
        var parentState = (parentFrameId == 0 ? null : parent.RealmStates.ByFrameId(parentFrameId)) ?? parent.State;
        state.SecureAncestorUrl = Uri.TryCreate(parentState.Url, UriKind.Absolute, out var parentUri)
            && MixedContent.ProhibitsMixedContent(parentUri)
                ? parentState.Url
                : parentState.SecureAncestorUrl;
        InheritReferrerFromParent(state, parentState, url);
        InheritFrameScope(state, parent.State, parentState, url);

        var engine = parent.CreateRealmEngine();
        DenoCoreShim? shim = null;
        try
        {
            // The realm's op table is filled the same way the page's is, with
            // this frame's state, which is what makes an op called from the
            // frame resolve against the frame's own document.
            shim = BootstrapLoader.Install(engine, ops => parent.BindRealmOps(ops, state), frameId);
            HostVariables.Copy(parent.MainHostHelpers, shim.HostHelpers, IdentityGlobals);
        }
        catch (ScriptEngineException)
        {
            // The shim may already have registered V8's promise-reject hook, so
            // unhook it before the engine is destroyed under it.
            shim?.Detach();
            engine.Dispose();
            return null;
        }

        // Only a same-origin frame is reachable from the page. A cross-origin
        // frame is opaque, and nothing about it is published.
        var origin = opaqueOrigin ? "null" : OriginOf(url);
        var sameOrigin = !string.Equals(origin, "null", StringComparison.Ordinal)
            && string.Equals(origin, parent.PageOrigin, StringComparison.Ordinal);

        parent.RealmStates.Register(engine, frameId, state);

        var realm = new FrameRealm(parent, engine, frameId, parentFrameId, url, origin) { State = state };
        realm._shim = shim;
        realm._domGc = PocketCalculatorJsRuntime.AttachFrameDomGc(parent, state, () => realm._shim?.HostHelpers);
        parent.RegisterRealm(realm);

        // Both ids before init, not after: init is what installs `parent` and
        // `top`, and a document that runs even one script believing it is
        // top-level has already taken the wrong branch.
        try
        {
            realm.ExecuteHostScript(
                $"__obscura_host.vars.__obscura_frameId = {frameId.ToString(CultureInfo.InvariantCulture)};"
                + $"__obscura_host.vars.__obscura_parentFrameId = {parentFrameId.ToString(CultureInfo.InvariantCulture)};"
                + "__obscura_host.init();");
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
    /// The frame's cookie scope (CHIPS and the site for cookies): the page's URL as its top
    /// level, and whether it or an ancestor frame is cross-site with the page. A srcdoc or
    /// about:blank frame takes its site from its parent; an opaque-origin frame is
    /// cross-site with everything. Port addition: Rust frames read and send cookies as the
    /// page does.
    /// </summary>
    internal static void InheritFrameScope(
        PocketCalculatorState state, PocketCalculatorState page, PocketCalculatorState parentState, string url)
    {
        state.TopLevelUrl = page.Url;
        var aboutFrame = url.StartsWith("about:", StringComparison.Ordinal);
        if (aboutFrame)
        {
            state.SiteUrl = parentState.SiteUrl ?? parentState.Url;
        }

        var site = aboutFrame ? state.SiteUrl : url;
        state.CrossSiteAncestor = state.OpaqueOrigin
            || parentState.CrossSiteAncestor
            || !Uri.TryCreate(site, UriKind.Absolute, out var siteUri)
            || !Uri.TryCreate(page.Url, UriKind.Absolute, out var pageUri)
            || !CookieJar.IsSameSite(siteUri, pageUri);
    }

    /// <summary>
    /// An <c>about:srcdoc</c> or <c>about:blank</c> frame inherits its parent's referrer
    /// policy (the policy container), which the frame's own <c>&lt;meta name=referrer&gt;</c>
    /// can still override, and a srcdoc frame also refers as its parent does: its requests
    /// carry the parent's URL, and its <c>document.referrer</c> is that URL under the parent's
    /// policy. An about:blank frame's requests carry no Referer, as in Chromium 141.
    /// Port addition: Rust has no referrer policy and sends frame requests from the frame URL.
    /// </summary>
    internal static void InheritReferrerFromParent(PocketCalculatorState state, PocketCalculatorState parentState, string url)
    {
        var srcdoc = string.Equals(url, "about:srcdoc", StringComparison.Ordinal);
        if (!srcdoc && !url.StartsWith("about:blank", StringComparison.Ordinal))
        {
            return;
        }

        var policy = StateHelpers.DocumentReferrerPolicy(parentState);
        state.ReferrerPolicyHeader = policy;
        if (!srcdoc)
        {
            return;
        }

        var source = parentState.ReferrerSourceUrl ?? parentState.HistoryUrl ?? parentState.Url;
        state.ReferrerSourceUrl = source;
        // document.referrer: the parent under its policy, towards a document that is not
        // same-origin by URL and not a downgrade (Chromium 141 reports the parent's origin
        // by default).
        state.Referrer = Uri.TryCreate(source, UriKind.Absolute, out var parentUri)
            && ReferrerPolicies.Referrer(parentUri, new Uri("http://srcdoc.invalid/"), policy switch
            {
                ReferrerPolicy.StrictOrigin or ReferrerPolicy.StrictOriginWhenCrossOrigin => ReferrerPolicy.Origin,
                ReferrerPolicy.NoReferrerWhenDowngrade => ReferrerPolicy.UnsafeUrl,
                ReferrerPolicy.SameOrigin => ReferrerPolicy.NoReferrer,
                _ => policy,
            }) is { } referrer
                ? referrer
                : string.Empty;
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
    /// Through the shim's own event class and dispatch (SECURITY.md L10); see
    /// <see cref="PocketCalculatorJsRuntime.RunLifecycle"/>.
    /// </remarks>
    public void DispatchLoadEvents() =>
        ExecuteHostScript(PocketCalculatorJsRuntime.LifecycleScript(
            ["interactive", "DOMContentLoaded", "complete", "readystatechange", "load"]));

    /// <summary>Delivers a <c>postMessage</c> that another realm sent to this one.</summary>
    /// <remarks>
    /// A host helper (<see cref="HostScript"/>). DEVIATION from the Rust engine, which calls
    /// the page-visible <c>globalThis.__obscura_deliverMessage</c>.
    /// </remarks>
    public void DeliverMessage(string dataJson, string origin, uint sourceFrameId, string targetOrigin)
    {
        // Port addition: targetOrigin is enforced here, against the origin the host knows
        // this frame has, as well as by the shim (SECURITY.md H4).
        if (!CoreOps.TargetOriginAllows(targetOrigin, StateHelpers.DocumentOrigin(State), origin))
        {
            return;
        }

        ExecuteHostScript(
            $"__obscura_host.deliverMessage({EncodeJsonArgument(dataJson)}, "
            + $"{EncodeJsonArgument(origin)}, {sourceFrameId.ToString(CultureInfo.InvariantCulture)}, "
            + $"{EncodeJsonArgument(targetOrigin)});");
    }

    /// <summary>Sets the frame document's viewport before any of its scripts run.</summary>
    public void SetViewport(double width, double height)
    {
        var w = double.IsFinite(width) && width > 0 ? width : 300.0;
        var h = double.IsFinite(height) && height > 0 ? height : 150.0;
        // The frame's own renderer lays its document out in this viewport (port addition:
        // see PocketCalculatorOps.BindDocumentRenderOps).
        var viewport = ((float)w, (float)h);
        if (State.Viewport != viewport)
        {
            State.Viewport = viewport;
            State.PreparedRender = null;
            State.PendingStyleMutations.Clear();
            State.ResolvedScroll = null;
        }
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

    /// <summary>
    /// Runs one new-document entry in the frame: installs a <see cref="BindingPreload"/>
    /// binding, or runs a script.
    /// </summary>
    public void ExecutePreloadScript(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (BindingPreload.NameOf(source) is { } binding)
        {
            ExecuteHostScript(BindingPreload.InstallStatement(binding));
            return;
        }
        ExecuteScript(source);
    }

    /// <summary>
    /// <see cref="ExecuteScript"/> for host-authored statements that use this realm's host
    /// helpers as <c>__obscura_host</c> (see <see cref="HostScript"/>).
    /// </summary>
    public void ExecuteHostScript(string source) =>
        Run(() => HostScript.Invoke(_engine, _shim?.HostHelpers, "<frame-host>", HostScript.WrapStatements(source)));

    /// <summary>
    /// <see cref="Evaluate"/> for a host-authored expression that uses this realm's host
    /// helpers as <c>__obscura_host</c>.
    /// </summary>
    public JsonNode? EvaluateHost(string expression) =>
        ParseJson(Run(() => Serialize(HostScript.Invoke(
            _engine,
            _shim?.HostHelpers,
            "<frame-host>",
            HostScript.WrapStatements($"return ({expression});")))));

    /// <summary>Evaluates an expression inside the frame and decodes it as JSON.</summary>
    /// <remarks>
    /// DEVIATION from frame.rs, which evaluates <c>JSON.stringify(expression)</c> with the
    /// frame's global <c>JSON</c> and so a page's replacement or its <c>toJSON</c>: the value
    /// is serialized with bootstrap's own walk (SECURITY.md L10).
    /// </remarks>
    public JsonNode? Evaluate(string expression) =>
        ParseJson(Run(() => Serialize(_engine.Evaluate(new DocumentInfo("<frame>"), expression))));

    private object? Serialize(object? value) =>
        BootstrapLoader.StringifyOf(_engine) is { } serializer
            ? serializer.InvokeAsFunction(value)
            : ((ScriptObject)_engine.Global.GetProperty("JSON")).InvokeMethod("stringify", value);

    private static JsonNode? ParseJson(string? json)
    {
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
    /// The fetch profile of one of the frame's <c>src=</c> classic scripts: a no-cors
    /// script load by the frame's own document, referred by it (a srcdoc frame by its
    /// parent) under its referrer policy, in its cookie scope.
    /// </summary>
    /// <remarks>
    /// Port addition. Rust fetches a frame's scripts with the navigation profile, as though
    /// typed into the address bar: no Referer, <c>Sec-Fetch-Site: none</c>,
    /// <c>Sec-Fetch-Dest: document</c>, and every cookie of the script's site, SameSite=Strict
    /// included, even from a cross-site frame. Chromium 141 sends the frame's URL as the
    /// Referer under the frame document's policy, <c>Sec-Fetch-Dest: script</c>, and from a
    /// cross-site frame SameSite=None cookies only.
    /// </remarks>
    public ResourceRequest ScriptRequest()
    {
        var state = State;
        // A srcdoc or about:blank document's origin, and so its site, is its parent's.
        var source = !state.OpaqueOrigin && state.SiteUrl is { } site ? site : state.Url;
        var initiator = Uri.TryCreate(source, UriKind.Absolute, out var parsed) ? parsed : new Uri("about:blank");
        var request = ResourceRequest.Subresource(ResourceType.Script, initiator);
        request.Referrer = state.ReferrerSourceUrl is { } referrer && Uri.TryCreate(referrer, UriKind.Absolute, out var from)
            ? from
            : Uri.TryCreate(state.Url, UriKind.Absolute, out var own) ? own : null;
        request.ReferrerPolicy = StateHelpers.DocumentReferrerPolicy(state);
        request.SecureAncestor = state.SecureAncestorUrl is { } secure && Uri.TryCreate(secure, UriKind.Absolute, out var ancestor)
            ? ancestor
            : null;
        return StateHelpers.WithFrameScope(request, state);
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
        if (_domGc is { } gc && State.Dom is { } dom)
        {
            dom.RemoveGcParticipant(gc);
            dom.AutomaticCollection = false;
        }

        _parent.RealmStates.Forget(_engine);
        _parent.ForgetRealm(this);
        // Detach V8's promise-reject hook before the engine goes, for the reason
        // PocketCalculatorJsRuntime.Dispose gives.
        _shim?.Detach();
        _shim = null;
        _engine.Dispose();
    }

    /// <summary>
    /// Runs <paramref name="source"/> in the frame's realm and returns its value
    /// as a string. Ops called from it find the frame's document because the op
    /// table was bound with the frame's state.
    /// </summary>
    private string? Run(string source) => Run(() => _engine.Evaluate(new DocumentInfo("<frame>"), source));

    private string? Run(Func<object?> evaluate)
    {
        var previous = _parent.RealmStates.Current;
        _parent.RealmStates.Current = State;
        try
        {
            var value = evaluate();
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
    /// the frame registry (<c>__obscura_host.publishFrameObjects</c>).
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
            // A host helper: the registry is closure state of bootstrap.js, not the Rust
            // engine's page-visible __obscura_frameObjects (SECURITY.md L10).
            _parent.ExecuteHostScript(
                "<publish-frame-objects>",
                $"__obscura_host.publishFrameObjects({FrameId.ToString(CultureInfo.InvariantCulture)});");
        }
        catch (JsRuntimeException)
        {
            // The registry is best-effort; a page without it simply cannot
            // reach into the frame.
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
