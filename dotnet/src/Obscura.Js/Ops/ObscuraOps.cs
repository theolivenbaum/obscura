using System.Globalization;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Obscura.Js.Url;

namespace Obscura.Js.Ops;

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
/// One instance owns the page's <see cref="ObscuraState"/> and the realm registry,
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
public sealed class ObscuraOps(ObscuraState page, RealmStates? realms = null)
{
    /// <summary>The page's own realm state; frame 0.</summary>
    public ObscuraState Page { get; } = page;

    /// <summary>
    /// Per-frame realm states, when the page has child frames. Pass the runtime's
    /// own registry so ops and realm construction agree on which state is which.
    /// </summary>
    public RealmStates Realms { get; } = realms ?? new RealmStates();

    /// <summary>Set by the runtime before page script runs.</summary>
    public IPostedTaskSpawner? TaskSpawner { get; set; }

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
    public ObscuraState FrameState(uint frameId) =>
        frameId == 0 ? Page : Realms.ByFrameId(frameId) ?? Page;

    /// <summary>
    /// The state of the realm running right now, or the page's when the caller is the
    /// page itself. A page with no frames pays only an emptiness check.
    /// </summary>
    public ObscuraState RealmState() => Realms.IsEmpty ? Page : Realms.Current ?? Page;

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

            var weak = new WeakReference<ObscuraState>(owner);
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

    private ObscuraState? PostedTaskOwner(uint frameId) =>
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

        // --- DOM -----------------------------------------------------------
        Bind(ops, "op_dom", (Func<object?, object?, object?, object?, string>)(
            (cmd, a1, a2, frameId) =>
                DomOps.OpDom(FrameState(U32(frameId)), S(cmd), S(a1), S(a2))));
        Bind(ops, "op_script_mark_started", (Func<object?, bool>)(
            nid => CoreOps.OpScriptMarkStarted(Page, U32(nid))));
        Bind(ops, "op_script_try_start", (Func<object?, bool>)(
            nid => CoreOps.OpScriptTryStart(Page, U32(nid))));
        Bind(ops, "op_shadow_attach", (Func<object?, object?, int>)(
            (nid, mode) => CoreOps.OpShadowAttach(Page, U32(nid), S(mode))));
        Bind(ops, "op_shadow_root_info", (Func<object?, string>)(
            nid => CoreOps.OpShadowRootInfo(Page, U32(nid))));

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
        Bind(ops, "op_post_frame_message", (Action<object?, object?, object?, object?, object?>)(
            (target, source, origin, targetOrigin, data) => CoreOps.OpPostFrameMessage(
                Page, U32(target), U32(source), S(origin), S(targetOrigin), S(data))));
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
        Bind(ops, "op_fetch_url", (Func<object?, object?, object?, object?, object?, object?, object?, Task<string>>)(
            (url, method, headers, body, origin, mode, credentials) => FetchOps.OpFetchUrlAsync(
                RealmState(), S(url), S(method), S(headers), Bytes(body), S(origin), S(mode), S(credentials))));

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
            (algorithm, data) => Uint8Array(engine, CryptoOps.Digest(S(algorithm), Bytes(data)))));
        Bind(ops, "op_subtle_hmac", (Func<object?, object?, object?, object>)(
            (hash, key, data) => Uint8Array(engine, CryptoOps.Hmac(S(hash), Bytes(key), Bytes(data)))));
        Bind(ops, "op_subtle_aes_gcm", (Func<object?, object?, object?, object?, object?, object>)(
            (encrypt, key, iv, aad, data) => Uint8Array(
                engine, CryptoOps.AesGcm(B(encrypt), Bytes(key), Bytes(iv), Bytes(aad), Bytes(data)))));
        Bind(ops, "op_subtle_aes_cbc", (Func<object?, object?, object?, object?, object>)(
            (encrypt, key, iv, data) => Uint8Array(
                engine, CryptoOps.AesCbc(B(encrypt), Bytes(key), Bytes(iv), Bytes(data)))));
        Bind(ops, "op_subtle_aes_ctr", (Func<object?, object?, object?, object?, object>)(
            (key, counter, counterLength, data) => Uint8Array(
                engine, CryptoOps.AesCtr(Bytes(key), Bytes(counter), U32(counterLength), Bytes(data)))));
        Bind(ops, "op_subtle_pbkdf2", (Func<object?, object?, object?, object?, object?, object>)(
            (hash, password, salt, iterations, length) => Uint8Array(
                engine,
                CryptoOps.Pbkdf2(S(hash), Bytes(password), Bytes(salt), U32(iterations), U32(length)))));
        Bind(ops, "op_subtle_hkdf", (Func<object?, object?, object?, object?, object?, object>)(
            (hash, ikm, salt, info, length) => Uint8Array(
                engine, CryptoOps.Hkdf(S(hash), Bytes(ikm), Bytes(salt), Bytes(info), U32(length)))));
        Bind(ops, "op_random_bytes", (Func<object?, object>)(
            length => Uint8Array(engine, CryptoOps.RandomBytes(U32(length)))));

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
    }

    private static void Bind(ScriptObject ops, string name, object function) =>
        ops.SetProperty(name, function);

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

    private static object Uint8Array(ScriptEngine engine, byte[] bytes)
    {
        var array = (ITypedArray<byte>)((ScriptObject)engine.Evaluate("Uint8Array"))
            .Invoke(true, (double)bytes.Length);
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
