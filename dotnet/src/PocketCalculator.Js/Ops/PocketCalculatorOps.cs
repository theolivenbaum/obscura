using System.Globalization;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using PocketCalculator.Js.Url;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// Runs a page-document <c>op_dom</c> command and tells the other realms over that
/// document what it changed. Port addition (SECURITY.md M6): see
/// <c>PocketCalculator.Js.Runtime.IsolatedWorld</c>.
/// </summary>
public interface IDomMutationForwarder
{
    /// <param name="source">The isolated world that made the call, or null for the page realm.</param>
    string OpDom(object? source, PocketCalculatorState page, string cmd, string arg1, string arg2);
}

/// <summary>
/// Schedules one browser posted-task delivery on the host's V8 task queue.
/// </summary>
/// <remarks>
/// Rust uses deno_core's engine-local <c>V8TaskSpawner</c>, which is safe to call
/// from an async-op reaction and wakes the event loop without the timer wheel's
/// floor or another async-op registration. The runtime owns the equivalent queue in
/// the port, so <c>op_posted_task</c> takes it as a seam.
/// </remarks>
public interface IPostedTaskSpawner
{
    /// <summary>
    /// Queues <paramref name="deliver"/>. When it runs it is handed the owning
    /// realm's current document generation, or
    /// <see cref="CoreOps.InvalidPostedTaskGeneration"/> if that realm is gone or busy.
    /// </summary>
    void Spawn(Action<double> deliver);
}

/// <summary>
/// The op table handed to <c>bootstrap.js</c> as <c>Deno.core.ops</c>.
/// </summary>
/// <remarks>
/// <para>
/// One instance owns the page's <see cref="PocketCalculatorState"/> and the realm registry,
/// resolves the state each op should act on, and binds the 52 op functions onto the
/// object the bootstrap loader supplies.
/// </para>
/// <para>
/// The op bodies themselves live in <see cref="DomOps"/>, <see cref="RenderOps"/>,
/// <see cref="FetchOps"/>, <see cref="CoreOps"/>, <see cref="CryptoOps"/> and
/// <see cref="UrlOps"/>. This class is the wiring, so the ops stay testable without
/// a V8 engine.
/// </para>
/// </remarks>
public sealed class PocketCalculatorOps(PocketCalculatorState page, RealmStates? realms = null)
{
    /// <summary>The page's own realm state; frame 0.</summary>
    public PocketCalculatorState Page { get; } = page;

    /// <summary>
    /// Per-frame realm states, when the page has child frames. Pass the runtime's
    /// own registry so ops and realm construction agree on which state is which.
    /// </summary>
    public RealmStates Realms { get; } = realms ?? new RealmStates();

    /// <summary>Set by the runtime before page script runs.</summary>
    public IPostedTaskSpawner? TaskSpawner { get; set; }

    /// <summary>
    /// Set by the runtime before the ops are bound, so an async op stays counted
    /// until its promise reaction has run. See <see cref="AsyncOpBinding"/>.
    /// </summary>
    public IAsyncOpTracker? AsyncOps { get; set; }

    /// <summary>
    /// Cancelled by the isolate's watchdogs alongside the V8 interrupt. Every sync op
    /// runs under its token (<see cref="PocketCalculator.Dom.WorkCancellation"/>), so
    /// C# work inside an op stops when the deadline passes (SECURITY.md H8).
    /// </summary>
    public PocketCalculator.Js.Runtime.ScriptCancellation Cancellation { get; } = new();

    /// <summary>
    /// The document of the realm a DOM call came from, named rather than inferred.
    /// </summary>
    /// <remarks>
    /// A wrapper's methods live on its own realm's prototypes, so the code running for
    /// <c>parentPage.frameDoc.title</c> is the <em>frame's</em> getter even though the
    /// caller is the page. Inferring the realm from the running context therefore
    /// answers the wrong question for any cross-realm access. Each realm's bootstrap
    /// closure knows its own frame id and passes it, which is both correct here and
    /// cheap: a page with no frames resolves on <c>frameId == 0</c> alone.
    /// </remarks>
    public PocketCalculatorState FrameState(uint frameId) =>
        frameId == 0 ? Page : Realms.ByFrameId(frameId) ?? Page;

    /// <summary>
    /// The state of the realm running right now, or the page's when the caller is the
    /// page itself. A page with no frames pays only an emptiness check.
    /// </summary>
    public PocketCalculatorState RealmState() => Realms.IsEmpty ? Page : Realms.Current ?? Page;

    /// <summary><c>op_posted_task</c>. Queues one posted-task delivery.</summary>
    /// <returns>
    /// The owning realm's document generation at queue time, or
    /// <see cref="CoreOps.InvalidPostedTaskGeneration"/> when it has none.
    /// </returns>
    public double OpPostedTask(uint frameId, Action<double> callback) => OpGuard.Run(
        "op_posted_task",
        () =>
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (PostedTaskOwner(frameId) is not { } owner)
            {
                return CoreOps.InvalidPostedTaskGeneration;
            }

            if (owner.IsBorrowed)
            {
                return CoreOps.InvalidPostedTaskGeneration;
            }

            var documentGeneration = owner.DocumentGeneration;
            if (TaskSpawner is not { } spawner)
            {
                return CoreOps.InvalidPostedTaskGeneration;
            }

            var weak = new WeakReference<PocketCalculatorState>(owner);
            spawner.Spawn(_ =>
            {
                var current = CoreOps.PostedTaskOwnerStatusOf(weak) switch
                {
                    PostedTaskOwnerStatus.Generation generation => (double)generation.Value,
                    _ => CoreOps.InvalidPostedTaskGeneration,
                };
                OpGuard.Run("op_posted_task delivery", () => callback(current));
            });
            return documentGeneration;
        },
        CoreOps.InvalidPostedTaskGeneration);

    /// <summary><c>op_posted_task_generation</c>.</summary>
    public double OpPostedTaskGeneration(uint frameId) => OpGuard.Run(
        "op_posted_task_generation",
        () => PostedTaskOwner(frameId) is { IsBorrowed: false } owner
            ? owner.DocumentGeneration
            : CoreOps.InvalidPostedTaskGeneration,
        CoreOps.InvalidPostedTaskGeneration);

    private PocketCalculatorState? PostedTaskOwner(uint frameId) =>
        frameId == 0 ? Page : Realms.ByFrameId(frameId);

    /// <summary>
    /// Attaches every op onto <paramref name="ops"/>, the object
    /// <c>BootstrapLoader.Install</c> exposes as <c>Deno.core.ops</c>.
    /// </summary>
    /// <remarks>
    /// The async ops return <see cref="Task"/>, so the host engine must have
    /// <c>EnableTaskPromiseConversion</c> switched on or the shim will see a host
    /// object where it expects a promise.
    /// </remarks>
    public void BindTo(ScriptObject ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var engine = ops.Engine;
        // The realm's own Uint8Array constructor, taken now: the table is bound before
        // bootstrap.js or any page script runs. DEVIATION from the port's earlier lookup of
        // the global on every call, which ran whatever the page had put there by then
        // inside the op (SECURITY.md L10).
        var uint8Array = (ScriptObject)engine.Evaluate("Uint8Array");

        // --- DOM -----------------------------------------------------------
        Bind(ops, "op_dom", (Func<object?, object?, object?, object?, string>)(
            (cmd, a1, a2, frameId) =>
            {
                var realm = U32(frameId);
                // An isolated world over the page's document hears about the page's
                // mutations through the forwarder, which exists only while a world does.
                return realm == 0 && MutationForwarder is { } forwarder
                    ? forwarder.OpDom(null, Page, S(cmd), S(a1), S(a2))
                    : DomOps.OpDom(FrameState(realm), S(cmd), S(a1), S(a2));
            }));
        Bind(ops, "op_script_mark_started", (Func<object?, bool>)(
            nid => CoreOps.OpScriptMarkStarted(Page, U32(nid))));
        Bind(ops, "op_script_try_start", (Func<object?, bool>)(
            nid => CoreOps.OpScriptTryStart(Page, U32(nid))));
        Bind(ops, "op_run_classic_script", (Action<object?, object?>)(
            (source, url) => RunClassicScript(engine, S(source), S(url))));
        Bind(ops, "op_shadow_attach", (Func<object?, object?, int>)(
            (nid, mode) => CoreOps.OpShadowAttach(Page, U32(nid), S(mode))));
        Bind(ops, "op_shadow_root_info", (Func<object?, string>)(
            nid => CoreOps.OpShadowRootInfo(Page, U32(nid))));
        // Upstream 04418a5: host-held linked-sheet CSS and its origin-clean bit.
        Bind(ops, "op_external_stylesheet_set", (Func<object?, object?, object?, object?, object?, bool>)(
            (nid, css, responseUrl, originClean, frameId) => StylesheetOps.OpExternalStylesheetSet(
                FrameState(U32(frameId)), U32(nid), S(css), S(responseUrl), B(originClean))));
        Bind(ops, "op_external_stylesheet_remove", (Func<object?, object?, bool>)(
            (nid, frameId) => StylesheetOps.OpExternalStylesheetRemove(FrameState(U32(frameId)), U32(nid))));
        Bind(ops, "op_external_stylesheet_get", (Func<object?, object?, string>)(
            (nid, frameId) => StylesheetOps.OpExternalStylesheetGet(FrameState(U32(frameId)), U32(nid))));

        // --- Runtime / page ------------------------------------------------
        Bind(ops, "op_runtime_events_enabled", (Func<bool>)(
            () => CoreOps.OpRuntimeEventsEnabled(Page)));
        Bind(ops, "op_console_msg", (Action<object?, object?, object?>)(
            (level, msg, args) => CoreOps.OpConsoleMsg(Page, S(level), S(msg), S(args))));
        Bind(ops, "op_binding_called", (Action<object?, object?>)(
            (name, payload) => CoreOps.OpBindingCalled(Page, S(name), S(payload))));
        Bind(ops, "op_navigate", (Action<object?, object?, object?>)(
            (url, method, body) => CoreOps.OpNavigate(RealmState(), S(url), S(method), S(body))));
        Bind(ops, "op_get_cookies", (Func<string>)(
            () => CoreOps.OpGetCookies(RealmState())));
        Bind(ops, "op_set_cookie", (Action<object?>)(
            cookie => CoreOps.OpSetCookie(RealmState(), S(cookie))));
        // Port addition: the calling realm's origin as the host knows it, for the
        // shim's postMessage targetOrigin checks (SECURITY.md H4).
        Bind(ops, "op_realm_origin", (Func<object?, string>)(
            frameId => OpGuard.Run(
                "op_realm_origin", () => StateHelpers.DocumentOrigin(FrameState(U32(frameId))), "null")));
        Bind(ops, "op_frame_document_ready", (Func<object?, object?, object?, object?, double>)(
            (url, html, width, height) => CoreOps.OpFrameDocumentReady(
                Page, RealmState().FrameId, S(url), S(html), U64(width), U64(height))));
        Bind(ops, "op_async_runtime_available", (Func<bool>)CoreOps.OpAsyncRuntimeAvailable);
        Bind(ops, "op_sleep", (Func<object?, Task>)(millis => CoreOps.OpSleepAsync(U64(millis))));
        Bind(ops, "op_posted_task", (Func<object?, object?, double>)(
            (frameId, callback) => OpPostedTask(U32(frameId), Callback(callback))));
        Bind(ops, "op_posted_task_generation", (Func<object?, double>)(
            frameId => OpPostedTaskGeneration(U32(frameId))));
        Bind(ops, "op_add_import_map", (Func<object?, object?, string>)(
            (source, baseUrl) => CoreOps.OpAddImportMap(Page, S(source), S(baseUrl))));

        // --- Network -------------------------------------------------------
        BindDocumentOps(ops, Page);

        // --- Encoding ------------------------------------------------------
        Bind(ops, "op_encoding_for_label", (Func<object?, string>)(
            label => CoreOps.OpEncodingForLabel(S(label))));
        Bind(ops, "op_text_decode", (Func<object?, object?, object?, object?, string>)(
            (label, bytes, fatal, ignoreBom) =>
                CoreOps.OpTextDecode(S(label), Bytes(bytes), B(fatal), B(ignoreBom))));

        // --- URL -----------------------------------------------------------
        Bind(ops, "op_url_parse", (Func<object?, object?, string>)(
            (href, baseHref) => UrlOps.UrlParse(S(href), S(baseHref))));
        Bind(ops, "op_url_set", (Func<object?, object?, object?, string>)(
            (href, part, value) => UrlOps.UrlSet(S(href), S(part), S(value))));
        Bind(ops, "op_url_resolve", (Func<object?, object?, string>)(
            (href, baseHref) => UrlOps.UrlResolve(S(href), S(baseHref))));
        Bind(ops, "op_url_encode_query", (Func<object?, object?, object?, string>)(
            (query, label, special) => UrlOps.UrlEncodeQuery(S(query), S(label), B(special))));
        Bind(ops, "op_document_domain_candidate", (Func<object?, object?, string>)(
            (current, input) => UrlOps.DocumentDomainCandidate(S(current), S(input))));

        // --- WebCrypto -----------------------------------------------------
        Bind(ops, "op_subtle_digest", (Func<object?, object?, object>)(
            (algorithm, data) => Uint8Array(uint8Array, CryptoOps.Digest(S(algorithm), Bytes(data)))));
        Bind(ops, "op_subtle_hmac", (Func<object?, object?, object?, object>)(
            (hash, key, data) => Uint8Array(uint8Array, CryptoOps.Hmac(S(hash), Bytes(key), Bytes(data)))));
        Bind(ops, "op_subtle_aes_gcm", (Func<object?, object?, object?, object?, object?, object>)(
            (encrypt, key, iv, aad, data) => Uint8Array(
                uint8Array, CryptoOps.AesGcm(B(encrypt), Bytes(key), Bytes(iv), Bytes(aad), Bytes(data)))));
        Bind(ops, "op_subtle_aes_cbc", (Func<object?, object?, object?, object?, object>)(
            (encrypt, key, iv, data) => Uint8Array(
                uint8Array, CryptoOps.AesCbc(B(encrypt), Bytes(key), Bytes(iv), Bytes(data)))));
        Bind(ops, "op_subtle_aes_ctr", (Func<object?, object?, object?, object?, object>)(
            (key, counter, counterLength, data) => Uint8Array(
                uint8Array, CryptoOps.AesCtr(Bytes(key), Bytes(counter), U32(counterLength), Bytes(data)))));
        Bind(ops, "op_subtle_pbkdf2", (Func<object?, object?, object?, object?, object?, object>)(
            (hash, password, salt, iterations, length) => Uint8Array(
                uint8Array,
                CryptoOps.Pbkdf2(S(hash), Bytes(password), Bytes(salt), U32(iterations), U32(length)))));
        Bind(ops, "op_subtle_hkdf", (Func<object?, object?, object?, object?, object?, object>)(
            (hash, ikm, salt, info, length) => Uint8Array(
                uint8Array, CryptoOps.Hkdf(S(hash), Bytes(ikm), Bytes(salt), Bytes(info), U32(length)))));
        Bind(ops, "op_random_bytes", (Func<object?, object>)(
            length => Uint8Array(uint8Array, CryptoOps.RandomBytes(U32(length)))));

        // --- Render --------------------------------------------------------
        Bind(ops, "op_begin_render_task", (Action)(() => RenderOps.OpBeginRenderTask(Page)));
        Bind(ops, "op_css_supports", (Func<object?, object?, bool>)(
            (name, value) => RenderOps.OpCssSupports(S(name), S(value))));
        Bind(ops, "op_set_dynamic_fonts", (Func<object?, bool>)(
            registrations => RenderOps.OpSetDynamicFonts(Page, S(registrations))));
        Bind(ops, "op_canvas_register_surface", (Func<object?, object?, object?, object?, bool>)(
            (nid, width, height, pixels) => RenderOps.OpCanvasRegisterSurface(
                Page, U32(nid), U32(width), U32(height), JsBuffer(pixels))));
        Bind(ops, "op_canvas_paint_damage", (Func<object?, bool>)(
            nid => RenderOps.OpCanvasPaintDamage(Page, U32(nid))));
        Bind(ops, "op_image_metadata", (Func<object?, object?, string>)(
            (nid, cachedOnly) => RenderOps.OpImageMetadata(Page, U32(nid), B(cachedOnly))));
        Bind(ops, "op_load_image_metadata", (Func<object?, Task<string>>)(
            nid => RenderOps.OpLoadImageMetadataAsync(Page, U32(nid))));
        Bind(ops, "op_layout_geometry", (Func<object?, string>)(
            nid => RenderOps.OpLayoutGeometry(Page, S(nid))));
        Bind(ops, "op_resize_observer_measurements", (Func<object?, string>)(
            nids => RenderOps.OpResizeObserverMeasurements(Page, S(nids))));
        Bind(ops, "op_intersection_observer_measurements", (Func<object?, string>)(
            nids => RenderOps.OpIntersectionObserverMeasurements(Page, S(nids))));
        Bind(ops, "op_computed_style", (Func<object?, string>)(
            nid => RenderOps.OpComputedStyle(Page, S(nid))));

        // Additive: op_computed_style keeps its one-argument shape and its payload, because the
        // op protocol is a contract. getComputedStyle() only reaches for this second op when it
        // is given a pseudo-element.
        Bind(ops, "op_computed_style_pseudo", (Func<object?, object?, string>)(
            (nid, pseudo) => RenderOps.OpComputedStylePseudo(Page, S(nid), S(pseudo))));
        // Additive as well: innerText for a whole subtree in one call (RenderOps.OpInnerText).
        Bind(ops, "op_inner_text", (Func<object?, string>)(
            nid => RenderOps.OpInnerText(Page, S(nid))));
        Bind(ops, "op_layout_metrics", (Func<string>)(() => RenderOps.OpLayoutMetrics(Page)));
        Bind(ops, "op_element_scroll_metrics", (Func<object?, string>)(
            nid => RenderOps.OpElementScrollMetrics(Page, S(nid))));
        Bind(ops, "op_element_scroll_to", (Func<object?, object?, object?, string>)(
            (nid, x, y) => RenderOps.OpElementScrollTo(Page, S(nid), D(x), D(y))));
        Bind(ops, "op_scroll_offset", (Func<string>)(() => RenderOps.OpScrollOffset(Page)));
        Bind(ops, "op_scroll_to", (Func<object?, object?, string>)(
            (x, y) => RenderOps.OpScrollTo(Page, D(x), D(y))));
        Bind(ops, "op_waapi_create", (Func<object?, bool>)(
            input => RenderOps.OpWaapiCreate(Page, S(input))));
        Bind(ops, "op_waapi_control", (Func<object?, object?, object?, bool>)(
            (id, action, value) => RenderOps.OpWaapiControl(Page, D(id), S(action), D(value))));
        Bind(ops, "op_font_resource_loaded", (Func<object?, bool>)(
            url => RenderOps.OpFontResourceLoaded(Page, S(url))));

        // --- Performance timeline ------------------------------------------
        // Page-scoped on purpose: the host records a subresource against the page it
        // fetched for, not the realm whose script happened to reference it.
        Bind(ops, "op_resource_timings", (Func<object?, string>)(
            sinceIndex => PerformanceOps.OpResourceTimings(Page, D(sinceIndex))));
        Bind(ops, "op_resource_timing_count", (Func<double>)(
            () => PerformanceOps.OpResourceTimingCount(Page)));
    }

    /// <summary>
    /// Rebind the ops whose target realm is "whoever is calling" onto one frame realm's
    /// own state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most ops carry an explicit <c>__obscura_frameId</c> from the shim and resolve
    /// through <see cref="FrameState(uint)"/>. Four do not: <c>op_navigate</c>,
    /// <c>op_get_cookies</c>, <c>op_set_cookie</c> and <c>op_frame_document_ready</c> ask
    /// <see cref="RealmState"/> which realm is running. The reference resolves that from
    /// the V8 scope of the call, which is always right. Here it was an ambient
    /// <see cref="RealmStates.Current"/> that only a synchronous
    /// <c>FrameRealm.Run</c> sets, so an op reached from a frame's promise continuation -
    /// which is how every frame timer runs, because a frame realm schedules through
    /// <c>op_sleep(...).then(...)</c> - resolved against the page instead.
    /// </para>
    /// <para>
    /// The visible symptom was an iframe inside an iframe: the grandchild's
    /// <c>op_frame_document_ready</c> recorded parent frame 0, so
    /// <c>Page.getFrameTree</c> reported two siblings of the main frame instead of a
    /// nested tree, and it flipped between correct and wrong depending on whether the
    /// registration happened inside the synchronous run or after it.
    /// </para>
    /// </remarks>
    public void BindRealmOverrides(ScriptObject ops, PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(state);
        Bind(ops, "op_navigate", (Action<object?, object?, object?>)(
            (url, method, body) => CoreOps.OpNavigate(state, S(url), S(method), S(body))));
        Bind(ops, "op_get_cookies", (Func<string>)(() => CoreOps.OpGetCookies(state)));
        Bind(ops, "op_set_cookie", (Action<object?>)(
            cookie => CoreOps.OpSetCookie(state, S(cookie))));
        Bind(ops, "op_frame_document_ready", (Func<object?, object?, object?, object?, double>)(
            (url, html, width, height) => CoreOps.OpFrameDocumentReady(
                Page, state.FrameId, S(url), S(html), U64(width), U64(height))));
        BindDocumentOps(ops, state);
    }

    /// <summary>
    /// The ops whose answer depends on which document is asking, bound to
    /// <paramref name="document"/>: the realm the op table belongs to. Nothing the shim passes
    /// can name another realm, and nothing page script computes decides an origin.
    /// </summary>
    private void BindDocumentOps(ScriptObject ops, PocketCalculatorState document)
    {
        var engine = ops.Engine;
        BindFetch(ops, document);
        BindPostFrameMessage(ops, document);

        // Port additions (SECURITY.md C2, C3): the consumers of an internal load's host-held
        // body. See InternalLoads.
        Bind(ops, "op_run_fetched_script", (Action<object?, object?>)(
            (token, url) => RunFetchedScript(engine, document, D(token), S(url))));
        Bind(ops, "op_frame_document_from_load", (Func<object?, object?, object?, object?, double>)(
            (token, width, height, sandboxed) => CoreOps.OpFrameDocumentFromLoad(
                Page, document, D(token), U64(width), U64(height), B(sandboxed))));
        Bind(ops, "op_load_stylesheet", (Func<object?, object?, Task<string>>)(
            (nid, url) => LinkedStylesheetLoader.OpLoadStylesheetAsync(RealmState(), document, U32(nid), S(url))));
        Bind(ops, "op_frame_same_origin", (Func<object?, double>)(
            frameId => OpGuard.Run("op_frame_same_origin", () => FrameSameOrigin(document, U32(frameId)), -1d)));
    }

    /// <summary>
    /// <c>op_frame_same_origin</c>: 1 when child frame <paramref name="frameId"/> is
    /// same-origin with <paramref name="document"/>, 0 when it is not, and -1 when the host
    /// has no record of the frame yet (its document is still queued for a realm).
    /// </summary>
    private double FrameSameOrigin(PocketCalculatorState document, uint frameId)
    {
        if (frameId == 0)
        {
            return -1d;
        }

        string? frameOrigin = null;
        if (Realms.ByFrameId(frameId) is { } frame)
        {
            frameOrigin = StateHelpers.DocumentOrigin(frame);
        }
        else
        {
            foreach (var pending in Page.PendingFrames)
            {
                if (pending.FrameId == frameId)
                {
                    frameOrigin = pending.OpaqueOrigin ? "null" : UrlRecord.Parse(pending.Url)?.AsciiOrigin ?? "null";
                    break;
                }
            }
        }

        if (frameOrigin is null)
        {
            return -1d;
        }

        var own = StateHelpers.DocumentOrigin(document);
        return !string.Equals(own, "null", StringComparison.Ordinal)
            && string.Equals(own, frameOrigin, StringComparison.Ordinal)
                ? 1d
                : 0d;
    }

    /// <summary>
    /// <c>op_run_fetched_script</c>: runs a dynamically inserted classic script whose source
    /// the host holds, as <see cref="RunClassicScript"/> does for source the shim holds.
    /// </summary>
    /// <remarks>
    /// Only a successful (2xx) <c>no-cors</c> load of this realm runs; anything else is the
    /// network error the HTML script-fetch algorithm makes of it. The script is named by the
    /// URL the host requested, not by what the shim passes. Not wrapped in
    /// <see cref="OpGuard"/>, for the reason <see cref="RunClassicScript"/> gives.
    /// </remarks>
    private static void RunFetchedScript(ScriptEngine engine, PocketCalculatorState document, double token, string url)
    {
        _ = url;
        if (InternalLoads.Take(document, token, "no-cors") is not { } load)
        {
            throw new InvalidOperationException("script body unavailable");
        }

        if (load.Status is < 200 or > 299)
        {
            throw new InvalidOperationException("HTTP " + load.Status.ToString(CultureInfo.InvariantCulture));
        }

        RunClassicScript(engine, load.Body, load.RequestUrl);
    }

    /// <summary>
    /// <c>op_post_frame_message</c>, sent as <paramref name="sender"/>: the realm the op
    /// table belongs to.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-js (ops.rs), which queues the source frame id and the
    /// origin the shim passes. The shim computed the origin with the page's own
    /// <c>URL</c> global, so a frame that replaced <c>URL</c> could post as any origin and
    /// defeat every <c>event.origin</c> check (SECURITY.md H4). Both arguments are
    /// accepted and ignored; the host fills them from the sending realm.
    /// </remarks>
    private void BindPostFrameMessage(ScriptObject ops, PocketCalculatorState sender) =>
        Bind(ops, "op_post_frame_message", (Action<object?, object?, object?, object?, object?>)(
            (target, _, _, targetOrigin, data) => CoreOps.OpPostFrameMessage(
                Page, U32(target), sender.FrameId, StateHelpers.DocumentOrigin(sender), S(targetOrigin), S(data))));

    /// <summary>
    /// <c>op_fetch_url</c>, judged against <paramref name="document"/>: the realm the op
    /// table belongs to, never an origin the shim passes.
    /// </summary>
    /// <remarks>
    /// The transport state (cookie jar, client, interception) is still the running realm's,
    /// as before; only the document the request is made <em>by</em> is pinned, because that
    /// is what decides CORS, credentials and SameSite.
    /// </remarks>
    private void BindFetch(ScriptObject ops, PocketCalculatorState document) =>
        Bind(ops, "op_fetch_url", (Func<object?, object?, object?, object?, object?, object?, object?, object?, Task<string>>)(
            (url, method, headers, body, origin, mode, credentials, internalLoad) => FetchOps.OpFetchUrlAsync(
                RealmState(), S(url), S(method), S(headers), Bytes(body), S(origin), S(mode), S(credentials),
                B(internalLoad), document)));

    private void Bind(ScriptObject ops, string name, object function)
    {
        // An async op is bound through the JS shim that keeps it counted until its
        // promise reaction runs; everything else goes straight to the fast path.
        if (AsyncOpBinding.TryBind(ops, name, function, AsyncOps))
        {
            return;
        }

        FastOpBinding.Bind(ops, name, function, Cancellation);
    }

    /// <summary>
    /// Delivers the page document's mutations to the CDP isolated worlds over it. Null
    /// until a world exists, which keeps a page without one on the plain path.
    /// </summary>
    public IDomMutationForwarder? MutationForwarder { get; set; }

    /// <summary>
    /// The ops that differ in a CDP isolated world (port addition, SECURITY.md M6). The
    /// world's table is otherwise the page realm's, bound with <see cref="BindTo"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>op_dom</c> always acts on the page's document, and goes through the
    /// forwarder so the page realm's observers hear about the world's mutations.</item>
    /// <item><c>op_world_call</c> reaches the page realm's copy of the node state
    /// bootstrap.js keeps in JavaScript (form values, focus, selection).</item>
    /// <item><c>op_binding_called</c> queues the call as the world's, so it is reported
    /// with the world's execution context.</item>
    /// <item>Console calls are not reported: they would carry the page's context.</item>
    /// </list>
    /// </remarks>
    internal void BindIsolatedWorldOverrides(
        ScriptObject ops,
        object world,
        Func<string, double, string, string> worldCall,
        Action<string, string> bindingCalled)
    {
        ArgumentNullException.ThrowIfNull(ops);
        Bind(ops, "op_dom", (Func<object?, object?, object?, object?, string>)(
            (cmd, a1, a2, _) => MutationForwarder is { } forwarder
                ? forwarder.OpDom(world, Page, S(cmd), S(a1), S(a2))
                : DomOps.OpDom(Page, S(cmd), S(a1), S(a2))));
        Bind(ops, "op_world_call", (Func<object?, object?, object?, string>)(
            (kind, nid, arg) => OpGuard.Run("op_world_call", () => worldCall(S(kind), D(nid), S(arg)), string.Empty)));
        Bind(ops, "op_binding_called", (Action<object?, object?>)(
            (name, payload) => OpGuard.Run("op_binding_called", () => bindingCalled(S(name), S(payload)))));
        Bind(ops, "op_runtime_events_enabled", (Func<bool>)(() => false));
        Bind(ops, "op_console_msg", (Action<object?, object?, object?>)((_, _, _) => { }));
    }

    /// <summary>
    /// Binds one realm-local async op - the frame timer source - with the same
    /// tracking every other async op gets.
    /// </summary>
    internal void BindRealmAsyncOp(ScriptObject ops, string name, object function) =>
        Bind(ops, name, function);

    // -----------------------------------------------------------------------
    // Argument normalization. The shim passes JS values; deno_core coerced them
    // for the Rust ops, and these do the same job at the ClearScript boundary.
    // -----------------------------------------------------------------------

    private static string S(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "true" : "false",
        double number => number.ToString(CultureInfo.InvariantCulture),
        _ when value is Undefined => string.Empty,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static double D(object? value) => value switch
    {
        null => 0d,
        double number => number,
        int number => number,
        long number => number,
        float number => number,
        string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            ? n
            : double.NaN,
        _ => double.NaN,
    };

    private static uint U32(object? value)
    {
        var number = D(value);
        return double.IsFinite(number) && number >= 0 && number <= uint.MaxValue ? (uint)number : 0u;
    }

    private static ulong U64(object? value)
    {
        var number = D(value);
        return double.IsFinite(number) && number >= 0 && number <= ulong.MaxValue ? (ulong)number : 0ul;
    }

    private static bool B(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        double number => number != 0d,
        string text => text.Length != 0,
        _ => value is not Undefined,
    };

    private static byte[] Bytes(object? value) => value switch
    {
        null => [],
        byte[] bytes => bytes,
        ITypedArray<byte> typed => typed.ToArray(),
        IArrayBufferView view => view.GetBytes(),
        IArrayBuffer buffer => buffer.GetBytes(),
        _ => [],
    };

    private static IJsBuffer JsBuffer(object? value) => value switch
    {
        ITypedArray<byte> typed => new TypedArrayJsBuffer(typed),
        _ => new ByteArrayJsBuffer(Bytes(value)),
    };

    private static Action<double> Callback(object? value) => value is ScriptObject function
        ? generation => function.InvokeAsFunction(generation)
        : _ => { };

    /// <summary>
    /// <c>op_run_classic_script</c>. Compiles and runs a dynamically inserted
    /// classic script's source as a top-level script in the calling realm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Port-added op, with no counterpart in <c>ops.rs</c>. The shim reaches it
    /// through the rewrite in <see cref="BootstrapSource.EngineText"/>, which is
    /// where the reason is written down: <c>(0, eval)(source)</c> drops a strict
    /// script's top-level <c>var</c> and <c>function</c> declarations instead of
    /// publishing them as globals, and a script is the only evaluation form that
    /// publishes them while still running the body in strict mode.
    /// </para>
    /// <para>
    /// Deliberately not wrapped in <see cref="OpGuard"/>: the two call sites each
    /// catch and report what the script threw, exactly as they did when eval
    /// threw it, so a failure has to travel back into JavaScript rather than be
    /// contained here. A script error arrives at the shim's <c>catch</c> as an
    /// <c>Error</c> whose message carries the original error's name and text; a
    /// watchdog interrupt keeps unwinding and stays uncatchable.
    /// </para>
    /// </remarks>
    private static void RunClassicScript(ScriptEngine engine, string source, string url)
    {
        if (source.Length == 0)
        {
            return;
        }

        engine.Execute(ScriptDocumentInfo(url), source);
    }

    /// <summary>
    /// Names a script the way the parser-inserted path does: the script URL, so a
    /// stack trace and <c>import()</c>'s referrer both resolve against it.
    /// </summary>
    private static DocumentInfo ScriptDocumentInfo(string url) =>
        url.Length == 0
            ? new DocumentInfo("<dynamic-script>")
            : Uri.TryCreate(url, UriKind.Absolute, out var uri) ? new DocumentInfo(uri) : new DocumentInfo(url);

    private static object Uint8Array(ScriptObject constructor, byte[] bytes)
    {
        var array = (ITypedArray<byte>)constructor.Invoke(true, (double)bytes.Length);
        if (bytes.Length != 0)
        {
            array.Write(bytes, 0, (ulong)bytes.Length, 0);
        }

        return array;
    }

    /// <summary>
    /// A live view over a JavaScript typed array, so a canvas backing store is shared
    /// rather than copied - the same guarantee deno_core's <c>JsBuffer</c> gives.
    /// </summary>
    private sealed class TypedArrayJsBuffer(ITypedArray<byte> array) : IJsBuffer
    {
        public int Length => (int)array.Length;

        public ReadOnlyMemory<byte> Read() => array.ToArray();
    }
}
