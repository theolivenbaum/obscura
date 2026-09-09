using System.Globalization;
using System.Text;
using System.Text.Json;
using Obscura.Dom;
using Obscura.Net;
using Obscura.Render;

namespace Obscura.Js.Ops;

/// <summary>
/// The render-facing ops: layout geometry, computed style, scrolling, canvas,
/// image metadata, WAAPI and the dynamic font registry.
/// </summary>
/// <remarks>
/// Number formatting differs between these ops and is observable. The ops that build
/// their JSON with <c>serde_json::json!</c> widen every <c>f32</c> to <c>f64</c> and
/// print it with ryu; the ones that build it with <c>format!</c> print the
/// <c>f32</c> with Rust's <c>Display</c>. See <see cref="SerdeJson"/>.
/// </remarks>
public static class RenderOps
{
    private const uint MaxCanvasDimension = 32_767;
    private const long MaxCanvasPixels = 67_108_864;
    private const long MaxCanvasSurfaceBytes = 256L * 1024 * 1024;

    /// <summary>
    /// <c>op_begin_render_task</c>. Opens a task epoch so geometry and computed-style
    /// reads within one task share a frozen animation frame.
    /// </summary>
    public static void OpBeginRenderTask(ObscuraState state) =>
        OpGuard.Run("op_begin_render_task", () => RenderState.BeginAnimationTask(state));

    /// <summary>
    /// <c>op_css_supports</c>. The renderer's declaration parser is the single
    /// feature-query source of truth, which keeps <c>CSS.supports()</c> to one call.
    /// </summary>
    public static bool OpCssSupports(string name, string value) => OpGuard.Run(
        "op_css_supports",
        () => ComputedStyle.SupportsDeclaration(name, value),
        false);

    /// <summary>
    /// <c>op_set_dynamic_fonts</c>. Replaces the native snapshot of
    /// <c>document.fonts</c>. The JS implementation remains the source of truth for
    /// set semantics; this narrow bridge only supplies resource descriptors.
    /// </summary>
    public static bool OpSetDynamicFonts(ObscuraState state, string registrations) => OpGuard.Run(
        "op_set_dynamic_fonts",
        () =>
        {
            var inputs = ParseDynamicFonts(registrations);
            if (inputs is null)
            {
                return false;
            }

            // Keep the observable registry broad enough for generated font families
            // (large applications commonly register dozens of subset faces). The
            // renderer independently caps decoded resources after ASCII filtering and
            // URL deduplication. BufferSource faces arrive as data URLs, so cap their
            // aggregate descriptor payload as well as each entry.
            if (inputs.Count > 256)
            {
                return false;
            }

            long total = 0;
            foreach (var face in inputs)
            {
                total += face.Source.Length;
                if (total > 64L * 1024 * 1024
                    || face.Family.Length > 1024
                    || face.Source.Length > 12 * 1024 * 1024
                    || face.Style.Length > 256
                    || face.Weight.Length > 256
                    || face.UnicodeRange.Length > 4096)
                {
                    return false;
                }
            }

            if (!DynamicFontsEqual(state.DynamicFonts, inputs))
            {
                state.DynamicFonts = inputs;
                RenderInvalidation.InvalidateRenderResourceGeometry(state);
            }

            return true;
        },
        false);

    /// <summary>
    /// <c>op_canvas_register_surface</c>. Retains the JavaScript-owned Canvas2D pixel
    /// buffer without copying it. A canvas resize supplies a new fixed backing store
    /// and atomically replaces the previous surface for the same DOM node.
    /// </summary>
    public static bool OpCanvasRegisterSurface(
        ObscuraState state,
        uint nid,
        uint width,
        uint height,
        IJsBuffer pixels) => OpGuard.Run(
        "op_canvas_register_surface",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(pixels);
            var expected = (long)width * height * 4;
            if (width > MaxCanvasDimension
                || height > MaxCanvasDimension
                || expected / 4 > MaxCanvasPixels
                || pixels.Length != expected)
            {
                return false;
            }

            var node = NodeId.New(nid);
            var isCanvas = state.Dom?.GetNode(node)?.AsElement() is { } element
                && string.Equals(element.Name.Local, "canvas", StringComparison.Ordinal);
            if (!isCanvas)
            {
                return false;
            }

            long replacing = state.CanvasSurfaces.TryGetValue(node, out var previous)
                ? previous.Pixels.Length
                : 0;
            long retained = 0;
            foreach (var surface in state.CanvasSurfaces.Values)
            {
                retained += surface.Pixels.Length;
            }

            if (retained - replacing + expected > MaxCanvasSurfaceBytes)
            {
                return false;
            }

            state.CanvasSurfaces[node] = new CanvasBackingSurface
            {
                Width = width,
                Height = height,
                Pixels = pixels,
            };
            return true;
        },
        false);

    /// <summary>
    /// <c>op_canvas_paint_damage</c>. Reports one coalesced Canvas2D paint at the
    /// JavaScript task boundary. Pixel bytes are already live through the retained
    /// backing store, so damage wakes screencast/readiness without throwing away
    /// otherwise-valid layout.
    /// </summary>
    public static bool OpCanvasPaintDamage(ObscuraState state, uint nid) => OpGuard.Run(
        "op_canvas_paint_damage",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            var node = NodeId.New(nid);
            if (!state.CanvasSurfaces.ContainsKey(node))
            {
                return false;
            }

            var connected = state.Dom is { } dom && StateHelpers.NodeIsConnected(dom, node);
            if (connected)
            {
                state.ActivityGeneration = unchecked(state.ActivityGeneration + 1);
            }

            return connected;
        },
        false);

    /// <summary>
    /// <c>op_layout_geometry</c>. Real border-box geometry for an element from the
    /// retained layout, or the empty string when the node has no box.
    /// </summary>
    /// <remarks>
    /// Coordinates are viewport-relative after the shared root scroll offset, except
    /// for viewport-fixed subtrees. The client dimensions are the unscaled padding box
    /// used by CSSOM View, <c>clientRects</c> retains every inline continuation, and
    /// the top-level rect is their visual viewport-relative bounding union.
    /// </remarks>
    public static string OpLayoutGeometry(ObscuraState state, string nidStr) => OpGuard.Run(
        "op_layout_geometry",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            var nid = ParseNode(nidStr);
            RenderState.SampleLiveDocumentAnimations(state);
            if (!RenderState.EnsureResolvedScrollForGeometry(state))
            {
                return string.Empty;
            }

            if (state.ResolvedScroll is not { } resolved
                || state.PreparedRender is not { } prepared
                || prepared.ViewportRectWithScroll(nid, resolved.State) is not { } rect
                || prepared.ClientSize(nid) is not { } client
                || prepared.ViewportClientRectsWithScroll(nid, resolved.State) is not { } clientRects)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(256);
            sb.Append("{\"x\":").Append(SerdeJson.NumberF32(rect.X));
            sb.Append(",\"y\":").Append(SerdeJson.NumberF32(rect.Y));
            sb.Append(",\"width\":").Append(SerdeJson.NumberF32(rect.Width));
            sb.Append(",\"height\":").Append(SerdeJson.NumberF32(rect.Height));
            sb.Append(",\"clientWidth\":").Append(SerdeJson.NumberF32(client.Width));
            sb.Append(",\"clientHeight\":").Append(SerdeJson.NumberF32(client.Height));
            sb.Append(",\"clientRects\":[");
            for (var i = 0; i < clientRects.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                var r = clientRects[i];
                sb.Append("{\"x\":").Append(SerdeJson.NumberF32(r.X));
                sb.Append(",\"y\":").Append(SerdeJson.NumberF32(r.Y));
                sb.Append(",\"width\":").Append(SerdeJson.NumberF32(r.Width));
                sb.Append(",\"height\":").Append(SerdeJson.NumberF32(r.Height));
                sb.Append('}');
            }

            sb.Append("],\"viewportFixed\":");
            sb.Append(prepared.ViewportFixedNodes().Contains(nid) ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        },
        string.Empty);

    /// <summary>
    /// <c>op_resize_observer_measurements</c>. Measures every target in one
    /// ResizeObserver rendering opportunity.
    /// </summary>
    /// <remarks>
    /// ResizeObserver gathers all observations before it invokes any callback.
    /// Crossing the JS/native boundary once per target defeated that batching: each
    /// read sampled the document timeline and could rebuild the retained
    /// cascade/layout independently. The result is index-aligned with the input and
    /// contains <c>null</c> for targets which currently generate no box (detached,
    /// <c>display:none</c>, and stale ids).
    /// </remarks>
    public static string OpResizeObserverMeasurements(ObscuraState state, string nidsJson) => OpGuard.Run(
        "op_resize_observer_measurements",
        () => Measure(state, nidsJson, ResizeMeasurement),
        "[]");

    /// <summary>
    /// <c>op_intersection_observer_measurements</c>. Measures the complete
    /// IntersectionObserver clip graph in one rendering opportunity: the unique
    /// observed targets, element roots, and intervening element ancestors.
    /// </summary>
    public static string OpIntersectionObserverMeasurements(ObscuraState state, string nidsJson) =>
        OpGuard.Run(
            "op_intersection_observer_measurements",
            () => Measure(state, nidsJson, IntersectionMeasurement),
            "[]");

    /// <summary>
    /// <c>op_computed_style</c>. One renderer-computed CSS snapshot for
    /// <c>getComputedStyle()</c>. Returning all supported properties together keeps a
    /// single JS style object to one native call and one use of the retained layout.
    /// </summary>
    public static string OpComputedStyle(ObscuraState state, string nidStr) => OpGuard.Run(
        "op_computed_style",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            var nid = ParseNode(nidStr);
            RenderState.SampleLiveDocumentAnimations(state);
            if (RenderState.EnsurePreparedRender(state) is not { } prepared
                || prepared.ComputedStyle(nid) is not { } snapshot)
            {
                return string.Empty;
            }

            var custom = prepared.ComputedCustomProperties(nid);
            var sb = new StringBuilder(4096);
            sb.Append('{');
            var first = true;
            foreach (var (name, value) in snapshot)
            {
                if (!first)
                {
                    sb.Append(',');
                }

                first = false;
                SerdeJson.AppendString(sb, name);
                sb.Append(':');
                SerdeJson.AppendString(sb, value);
            }

            if (custom is not null)
            {
                foreach (var (name, value) in custom)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    SerdeJson.AppendString(sb, name);
                    sb.Append(':');
                    SerdeJson.AppendString(sb, value);
                }
            }

            sb.Append('}');
            return sb.ToString();
        },
        string.Empty);

    /// <summary>
    /// <c>op_layout_metrics</c>. Root scrolling overflow in CSS pixels. Built with
    /// Rust's <c>Display</c> formatting for <c>f32</c>, not serde's.
    /// </summary>
    public static string OpLayoutMetrics(ObscuraState state) => OpGuard.Run(
        "op_layout_metrics",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            RenderState.SampleLiveDocumentAnimations(state);
            var viewport = state.Viewport;
            var content = RenderState.EnsurePreparedGeometry(state)?.ContentSize() ?? viewport;
            return "{\"scrollWidth\":" + SerdeJson.DisplayF32(content.Width)
                + ",\"scrollHeight\":" + SerdeJson.DisplayF32(content.Height)
                + ",\"clientWidth\":" + SerdeJson.DisplayF32(viewport.Width)
                + ",\"clientHeight\":" + SerdeJson.DisplayF32(viewport.Height)
                + "}";
        },
        string.Empty);

    /// <summary><c>op_element_scroll_metrics</c>. CSSOM scrolling metrics for one element.</summary>
    public static string OpElementScrollMetrics(ObscuraState state, string nidStr) => OpGuard.Run(
        "op_element_scroll_metrics",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            var nid = ParseNode(nidStr);
            RenderState.SampleLiveDocumentAnimations(state);
            if (!RenderState.EnsureResolvedScrollForGeometry(state))
            {
                return string.Empty;
            }

            if (state.ResolvedScroll is not { } resolved)
            {
                return string.Empty;
            }

            if (state.PreparedRender?.ElementScrollMetrics(nid, resolved.State) is not { } metrics)
            {
                // The op exists in render builds, so an unboxed/detached node must not
                // fall through to bootstrap's synthetic non-render metrics.
                return "{\"scrollWidth\":0,\"scrollHeight\":0,\"clientWidth\":0,\"clientHeight\":0,"
                    + "\"x\":0,\"y\":0,\"maxX\":0,\"maxY\":0,\"hasBox\":false}";
            }

            return "{\"scrollWidth\":" + SerdeJson.NumberF32(metrics.ContentSize.Width)
                + ",\"scrollHeight\":" + SerdeJson.NumberF32(metrics.ContentSize.Height)
                + ",\"clientWidth\":" + SerdeJson.NumberF32(metrics.ClientSize.Width)
                + ",\"clientHeight\":" + SerdeJson.NumberF32(metrics.ClientSize.Height)
                + ",\"x\":" + SerdeJson.NumberF32(metrics.Offset.X)
                + ",\"y\":" + SerdeJson.NumberF32(metrics.Offset.Y)
                + ",\"maxX\":" + SerdeJson.NumberF32(metrics.MaxOffset.X)
                + ",\"maxY\":" + SerdeJson.NumberF32(metrics.MaxOffset.Y)
                + ",\"hasBox\":true}";
        },
        string.Empty);

    /// <summary><c>op_element_scroll_to</c>. Scrolls one element and reports the applied offset.</summary>
    public static string OpElementScrollTo(ObscuraState state, string nidStr, double x, double y) =>
        OpGuard.Run(
            "op_element_scroll_to",
            () =>
            {
                ArgumentNullException.ThrowIfNull(state);
                var nid = ParseNode(nidStr);
                RenderState.SampleLiveDocumentAnimations(state);
                if (!RenderState.EnsureResolvedScrollForGeometry(state))
                {
                    return string.Empty;
                }

                if (state.ResolvedScroll is not { } resolved
                    || state.PreparedRender?.ElementScrollMetrics(nid, resolved.State) is not { } current)
                {
                    return string.Empty;
                }

                var requested = (Clamp(x, current.MaxOffset.X), Clamp(y, current.MaxOffset.Y));
                if (requested != current.Offset)
                {
                    if (requested == (0f, 0f))
                    {
                        state.ElementScrollOffsets.Remove(nid);
                    }
                    else
                    {
                        state.ElementScrollOffsets[nid] = requested;
                    }

                    state.ActivityGeneration = unchecked(state.ActivityGeneration + 1);
                    state.ScrollGeneration = unchecked(state.ScrollGeneration + 1);
                    state.ResolvedScroll = null;
                    return Point(requested.Item1, requested.Item2);
                }

                return Point(current.Offset.X, current.Offset.Y);
            },
            string.Empty);

    /// <summary><c>op_scroll_offset</c>. The clamped root scroll offset.</summary>
    public static string OpScrollOffset(ObscuraState state) => OpGuard.Run(
        "op_scroll_offset",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            RenderState.SampleLiveDocumentAnimations(state);
            var requested = state.ScrollOffset;
            var (x, y) = RenderState.ClampScrollOffsetForGeometry(state, requested);
            return Point(x, y);
        },
        string.Empty);

    /// <summary><c>op_scroll_to</c>. Sets and reports the clamped root scroll offset.</summary>
    public static string OpScrollTo(ObscuraState state, double x, double y) => OpGuard.Run(
        "op_scroll_to",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            RenderState.SampleLiveDocumentAnimations(state);
            var (nx, ny) = RenderState.ClampScrollOffsetForGeometry(state, ((float)x, (float)y));
            return Point(nx, ny);
        },
        string.Empty);

    /// <summary>
    /// <c>op_image_metadata</c>. Probes one ordinary <c>&lt;img&gt;</c> through the
    /// renderer's page-scoped resource cache. Intentionally cache-only: lifecycle
    /// getters call it synchronously and must never open a socket.
    /// </summary>
    public static string OpImageMetadata(ObscuraState state, uint nid, bool cachedOnly) => OpGuard.Run(
        "op_image_metadata",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            _ = cachedOnly;
            var nodeId = NodeId.New(nid);
            var isImage = state.Dom?.GetNode(nodeId)?.AsElement() is { } element
                && string.Equals(element.Name.Local, "img", StringComparison.Ordinal);
            return isImage
                ? CachedImageMetadataForNode(state, nodeId)
                : "{\"ok\":false,\"currentSrc\":\"\"}";
        },
        "{\"ok\":false,\"currentSrc\":\"\"}");

    /// <summary>
    /// <c>op_load_image_metadata</c>. Loads HTMLImageElement bytes through the owning
    /// page's async transport.
    /// </summary>
    /// <remarks>
    /// Network runs after the synchronous state reads are done, requests for the same
    /// navigation/URL/profile share one fetch, and completion revalidates both the
    /// document identity and the responsive candidate before exposing lifecycle state.
    /// </remarks>
    public static Task<string> OpLoadImageMetadataAsync(ObscuraState state, uint nid) =>
        OpGuard.RunAsync(
            "op_load_image_metadata",
            () => LoadImageMetadataAsync(state, nid),
            "{\"state\":\"stale\",\"currentSrc\":\"\"}");

    /// <summary>
    /// <c>op_waapi_create</c>. Registers one Web Animation effect against the
    /// document timeline.
    /// </summary>
    public static bool OpWaapiCreate(ObscuraState state, string input) => OpGuard.Run(
        "op_waapi_create",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (WaapiCreateInput.Parse(input) is not { } parsed)
            {
                return false;
            }

            if (!float.IsFinite(parsed.Duration)
                || parsed.Duration < 0f
                || !float.IsFinite(parsed.Delay)
                || !float.IsFinite(parsed.Iterations)
                || parsed.Iterations < 0f
                || parsed.Keyframes.Count == 0)
            {
                return false;
            }

            var node = NodeId.New(parsed.Node);
            if (state.Dom?.GetNode(node) is null)
            {
                return false;
            }

            var startTimeMs = WaapiDocumentTimeMs(state);
            var fillMode = parsed.Fill switch
            {
                "forwards" => AnimationFillMode.Forwards,
                "backwards" => AnimationFillMode.Backwards,
                "both" => AnimationFillMode.Both,
                _ => AnimationFillMode.None,
            };
            var direction = parsed.Direction switch
            {
                "reverse" => AnimationDirection.Reverse,
                "alternate" => AnimationDirection.Alternate,
                "alternate-reverse" => AnimationDirection.AlternateReverse,
                _ => AnimationDirection.Normal,
            };
            var iterations = parsed.IterationsInfinite ? float.PositiveInfinity : parsed.Iterations;

            var keyframes = new List<WaapiKeyframe>(parsed.Keyframes.Count);
            foreach (var frame in parsed.Keyframes)
            {
                keyframes.Add(new WaapiKeyframe
                {
                    Offset = Math.Clamp(frame.Offset, 0f, 1f),
                    Opacity = frame.Opacity is { } opacity ? Math.Clamp(opacity, 0f, 1f) : null,
                    Transform = frame.Transform,
                });
            }

            state.AnimationTimeline.RegisterWaapi(new WaapiAnimation
            {
                Id = parsed.Id,
                Node = node,
                Keyframes = keyframes,
                Timing = new AnimationTiming(
                    parsed.Duration,
                    parsed.Delay,
                    iterations,
                    direction,
                    fillMode,
                    AnimationPlayState.Running),
                Easing = parsed.EasingBezier,
                LinearEasing = parsed.LinearEasing,
                StartTimeMs = startTimeMs,
                HoldTimeMs = null,
                PlayState = WaapiPlayState.Running,
            });
            InvalidateWaapiRender(state, node);
            return true;
        },
        false);

    /// <summary><c>op_waapi_control</c>. Drives play state and current time of one effect.</summary>
    public static bool OpWaapiControl(ObscuraState state, double id, string action, double value) =>
        OpGuard.Run(
            "op_waapi_control",
            () =>
            {
                ArgumentNullException.ThrowIfNull(state);
                if (!double.IsFinite(id) || id < 0d)
                {
                    return false;
                }

                var animationId = (ulong)id;
                if (state.AnimationTimeline.WaapiNode(animationId) is not { } node)
                {
                    return false;
                }

                var documentTime = WaapiDocumentTimeMs(state);
                var changed = action switch
                {
                    "cancel" => state.AnimationTimeline.CancelWaapi(animationId),
                    "finish" => state.AnimationTimeline.FinishWaapi(animationId),
                    "pause" => state.AnimationTimeline.SetWaapiPlayState(
                        animationId, WaapiPlayState.Paused, documentTime),
                    "play" => state.AnimationTimeline.SetWaapiPlayState(
                        animationId, WaapiPlayState.Running, documentTime),
                    "currentTime" when double.IsFinite(value) =>
                        state.AnimationTimeline.SetWaapiCurrentTime(animationId, documentTime, (float)value),
                    _ => false,
                };
                if (changed)
                {
                    InvalidateWaapiRender(state, node);
                }

                return changed;
            },
            false);

    internal static float WaapiDocumentTimeMs(ObscuraState state) =>
        (float)state.AnimationTimelineElapsedMilliseconds;

    /// <summary>
    /// Adding or controlling one effect changes the animation cascade only for its
    /// target, so the previous style graph stays available to the retained planner
    /// instead of turning every animation setup into a full document cascade.
    /// </summary>
    private static void InvalidateWaapiRender(ObscuraState state, NodeId node)
    {
        if (state.PreparedRender is not null
            && !RenderInvalidation.QueueRetainedStyleMutation(
                state.PendingStyleMutations,
                new RetainedStyleMutation.WaapiAnimation(node)))
        {
            state.PreparedRender = null;
            state.PendingStyleMutations.Clear();
        }

        state.ResolvedScroll = null;
        state.ActivityGeneration = unchecked(state.ActivityGeneration + 1);
    }

    private static string Measure(
        ObscuraState state,
        string nidsJson,
        Func<PreparedRender, ResolvedScrollState, NodeId, string?> measurement)
    {
        ArgumentNullException.ThrowIfNull(state);
        var nids = ParseNodeList(nidsJson);
        if (nids.Count == 0)
        {
            return "[]";
        }

        RenderState.SampleLiveDocumentAnimations(state);
        if (!RenderState.EnsureResolvedScrollForGeometry(state)
            || state.ResolvedScroll is not { } resolved
            || state.PreparedRender is not { } prepared)
        {
            return NullArray(nids.Count);
        }

        var sb = new StringBuilder(nids.Count * 96 + 2);
        sb.Append('[');
        for (var i = 0; i < nids.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(measurement(prepared, resolved.State, NodeId.New(nids[i])) ?? "null");
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string? ResizeMeasurement(PreparedRender prepared, ResolvedScrollState scroll, NodeId nid)
    {
        if (prepared.ViewportRectWithScroll(nid, scroll) is not { } rect
            || prepared.ClientSize(nid) is not { } client
            || prepared.ComputedStyle(nid) is not { } snapshot)
        {
            return null;
        }

        var sb = new StringBuilder(320);
        sb.Append("{\"x\":").Append(SerdeJson.NumberF32(rect.X));
        sb.Append(",\"y\":").Append(SerdeJson.NumberF32(rect.Y));
        sb.Append(",\"clientWidth\":").Append(SerdeJson.NumberF32(client.Width));
        sb.Append(",\"clientHeight\":").Append(SerdeJson.NumberF32(client.Height));
        AppendStyle(sb, "paddingTop", snapshot, "padding-top");
        AppendStyle(sb, "paddingRight", snapshot, "padding-right");
        AppendStyle(sb, "paddingBottom", snapshot, "padding-bottom");
        AppendStyle(sb, "paddingLeft", snapshot, "padding-left");
        AppendStyle(sb, "borderTopWidth", snapshot, "border-top-width");
        AppendStyle(sb, "borderRightWidth", snapshot, "border-right-width");
        AppendStyle(sb, "borderBottomWidth", snapshot, "border-bottom-width");
        AppendStyle(sb, "borderLeftWidth", snapshot, "border-left-width");
        AppendStyle(sb, "writingMode", snapshot, "writing-mode");
        AppendStyle(sb, "display", snapshot, "display");
        sb.Append('}');
        return sb.ToString();
    }

    private static string? IntersectionMeasurement(
        PreparedRender prepared,
        ResolvedScrollState scroll,
        NodeId nid)
    {
        if (prepared.ViewportRectWithScroll(nid, scroll) is not { } rect
            || prepared.ClientSize(nid) is not { } client
            || prepared.ComputedStyle(nid) is not { } snapshot)
        {
            return null;
        }

        var sb = new StringBuilder(256);
        sb.Append("{\"x\":").Append(SerdeJson.NumberF32(rect.X));
        sb.Append(",\"y\":").Append(SerdeJson.NumberF32(rect.Y));
        sb.Append(",\"width\":").Append(SerdeJson.NumberF32(rect.Width));
        sb.Append(",\"height\":").Append(SerdeJson.NumberF32(rect.Height));
        sb.Append(",\"clientWidth\":").Append(SerdeJson.NumberF32(client.Width));
        sb.Append(",\"clientHeight\":").Append(SerdeJson.NumberF32(client.Height));
        AppendStyle(sb, "borderTopWidth", snapshot, "border-top-width");
        AppendStyle(sb, "borderLeftWidth", snapshot, "border-left-width");
        AppendStyle(sb, "overflowX", snapshot, "overflow-x");
        AppendStyle(sb, "overflowY", snapshot, "overflow-y");
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendStyle(
        StringBuilder sb,
        string jsonName,
        Dictionary<string, string> snapshot,
        string property)
    {
        sb.Append(",\"").Append(jsonName).Append("\":");
        SerdeJson.AppendString(sb, snapshot.TryGetValue(property, out var value) ? value : string.Empty);
    }

    private static string NullArray(int count)
    {
        var sb = new StringBuilder(count * 5 + 2);
        sb.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("null");
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string Point(float x, float y) =>
        "{\"x\":" + SerdeJson.DisplayF32(x) + ",\"y\":" + SerdeJson.DisplayF32(y) + "}";

    private static float Clamp(double value, float max) => double.IsFinite(value)
        ? Math.Clamp(RenderMath.QuantizeScrollValue((float)value, 1f), 0f, max)
        : 0f;

    private static NodeId ParseNode(string value) =>
        NodeId.New(uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)
            ? raw
            : 0);

    private static List<uint> ParseNodeList(string json)
    {
        List<uint> nids = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return nids;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetUInt32(out var value))
                {
                    return [];
                }

                nids.Add(value);
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return nids;
    }

    // -----------------------------------------------------------------------
    // Image metadata
    // -----------------------------------------------------------------------

    internal static string ImageMetadataJson(
        string currentSrc,
        float density,
        bool known,
        (float Width, float Height)? dimensions)
    {
        if (!known)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"state\":\"pending\",\"currentSrc\":");
            SerdeJson.AppendString(sb, currentSrc);
            sb.Append(",\"density\":").Append(SerdeJson.NumberF32(density)).Append('}');
            return sb.ToString();
        }

        if (dimensions is { } size)
        {
            var sb = new StringBuilder(160);
            sb.Append("{\"state\":\"loaded\",\"ok\":true,\"currentSrc\":");
            SerdeJson.AppendString(sb, currentSrc);
            sb.Append(",\"density\":").Append(SerdeJson.NumberF32(density));
            sb.Append(",\"width\":").Append(SerdeJson.NumberF32(size.Width));
            sb.Append(",\"height\":").Append(SerdeJson.NumberF32(size.Height));
            sb.Append('}');
            return sb.ToString();
        }

        var error = new StringBuilder(96);
        error.Append("{\"state\":\"error\",\"ok\":false,\"currentSrc\":");
        SerdeJson.AppendString(error, currentSrc);
        error.Append(",\"density\":").Append(SerdeJson.NumberF32(density)).Append('}');
        return error.ToString();
    }

    internal static ImageRequestProfile ImageRequestProfileOf(DomTree dom, NodeId nodeId) =>
        dom.GetNode(nodeId)?.GetAttribute("crossorigin")?.Trim().ToLowerInvariant() switch
        {
            null => ImageRequestProfile.NoCorsInclude,
            "use-credentials" => ImageRequestProfile.CorsInclude,
            _ => ImageRequestProfile.CorsSameOrigin,
        };

    private static (string Url, float Density, bool Known, (float Width, float Height)? Dimensions)?
        ProfiledCachedImageMetadata(ObscuraState gs, NodeId nodeId)
    {
        if (gs.Dom is not { } dom)
        {
            return null;
        }

        var baseUrl = StateHelpers.DocumentBaseUrl(gs);
        return gs.RenderResources.CachedImageElementMetadata(dom, nodeId, gs.Viewport, baseUrl);
    }

    private static string CachedImageMetadataForNode(ObscuraState gs, NodeId nodeId) =>
        ProfiledCachedImageMetadata(gs, nodeId) is { } cached
            ? ImageMetadataJson(cached.Url, cached.Density, cached.Known, cached.Dimensions)
            : "{\"ok\":false,\"currentSrc\":\"\"}";

    /// <summary>
    /// Compatibility path for standalone render runtimes which deliberately install
    /// an in-memory resource loader but have no owning page transport. Browser pages
    /// always install a transport before page script runs.
    /// </summary>
    private static string LoadImageMetadataWithoutPageTransport(ObscuraState gs, NodeId nodeId)
    {
        var baseUrl = StateHelpers.DocumentBaseUrl(gs);
        var viewport = gs.Viewport;
        (float Width, float Height)? previousDimensions = null;
        if (gs.Dom is { } cachedDom
            && gs.RenderResources.CachedImageElementMetadata(cachedDom, nodeId, viewport, baseUrl)
                is { } cached
            && cached.Known)
        {
            previousDimensions = cached.Dimensions;
        }

        if (gs.Dom is not { } dom)
        {
            return "{\"ok\":false,\"currentSrc\":\"\"}";
        }

        if (gs.RenderResources.ImageElementMetadata(dom, nodeId, viewport, baseUrl) is not { } metadata)
        {
            return "{\"state\":\"error\",\"ok\":false,\"currentSrc\":\"\"}";
        }

        if (metadata.Dimensions is not null && metadata.Dimensions != previousDimensions)
        {
            RenderInvalidation.InvalidateRenderResourceGeometry(gs);
        }

        return ImageMetadataJson(metadata.Url, metadata.Density, true, metadata.Dimensions);
    }

    private static string FinishAsyncImageMetadata(
        ObscuraState gs,
        NodeId nodeId,
        ulong documentGeneration,
        string expectedUrl,
        ImageRequestProfile requestProfile)
    {
        if (gs.DocumentGeneration != documentGeneration || gs.Dom is not { } dom)
        {
            return Stale(expectedUrl);
        }

        if (ImageRequestProfileOf(dom, nodeId) != requestProfile)
        {
            return Stale(expectedUrl);
        }

        if (ProfiledCachedImageMetadata(gs, nodeId) is not { } cached)
        {
            return Stale(expectedUrl);
        }

        if (!string.Equals(cached.Url, expectedUrl, StringComparison.Ordinal))
        {
            return Stale(cached.Url);
        }

        return ImageMetadataJson(cached.Url, cached.Density, cached.Known, cached.Dimensions);
    }

    private static string Stale(string currentSrc)
    {
        var sb = new StringBuilder(64);
        sb.Append("{\"state\":\"stale\",\"currentSrc\":");
        SerdeJson.AppendString(sb, currentSrc);
        sb.Append('}');
        return sb.ToString();
    }

    private static async Task<string> LoadImageMetadataAsync(ObscuraState shared, uint nid)
    {
        ArgumentNullException.ThrowIfNull(shared);
        var nodeId = NodeId.New(nid);
        if (shared.Dom is not { } dom)
        {
            return "{\"state\":\"stale\",\"currentSrc\":\"\"}";
        }

        var isImage = dom.GetNode(nodeId)?.AsElement() is { } element
            && string.Equals(element.Name.Local, "img", StringComparison.Ordinal);
        if (!isImage)
        {
            return "{\"state\":\"stale\",\"currentSrc\":\"\"}";
        }

        var profile = ImageRequestProfileOf(dom, nodeId);
        if (ProfiledCachedImageMetadata(shared, nodeId) is not { } selected)
        {
            return "{\"state\":\"error\",\"ok\":false,\"currentSrc\":\"\"}";
        }

        if (selected.Known)
        {
            return CachedImageMetadataForNode(shared, nodeId);
        }

        var documentGeneration = shared.DocumentGeneration;
        var selectedUrl = selected.Url;
        var initiator = TryUri(shared.Url) ?? TryUri(selectedUrl) ?? new Uri("about:blank");
        var resourceRequest = ResourceRequest.Subresource(ResourceType.Image, initiator);
        switch (profile)
        {
            case ImageRequestProfile.CorsInclude:
                resourceRequest.Mode = RequestMode.Cors;
                resourceRequest.Credentials = RequestCredentials.Include;
                break;
            case ImageRequestProfile.CorsSameOrigin:
                resourceRequest.Mode = RequestMode.Cors;
                resourceRequest.Credentials = RequestCredentials.SameOrigin;
                break;
        }

        var blocked = false;
        foreach (var pattern in shared.BlockedUrls)
        {
            if (string.Equals(pattern, "*", StringComparison.Ordinal)
                || selectedUrl.Contains(pattern, StringComparison.Ordinal)
                || FetchOps.GlobMatch(pattern, selectedUrl))
            {
                blocked = true;
                break;
            }
        }

        var httpClient = shared.HttpClient;
        var stealthClient = shared.StealthClient;
        var hasPageTransport = httpClient is not null || stealthClient is { IsAvailable: true };
        if (!hasPageTransport)
        {
            return LoadImageMetadataWithoutPageTransport(shared, nodeId);
        }

        // Different CORS/credential profiles do not share an in-flight response.
        var requestKey = (documentGeneration, selectedUrl, profile);
        TaskCompletionSource? follower = null;
        if (shared.RenderImageInFlight.TryGetValue(requestKey, out var waiters))
        {
            follower = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add(follower);
        }
        else
        {
            shared.RenderImageInFlight[requestKey] = [];
        }

        if (follower is not null)
        {
            await follower.Task.ConfigureAwait(false);
            return FinishAsyncImageMetadata(shared, nodeId, documentGeneration, selectedUrl, profile);
        }

        shared.PageInFlight.Increment();
        try
        {
            var parsedUrl = TryUri(selectedUrl);
            Response? response = null;
            if (!blocked && parsedUrl is not null)
            {
                try
                {
                    response = stealthClient is { IsAvailable: true }
                        ? await stealthClient
                            .FetchResourceWithCallbacksAsync(parsedUrl, resourceRequest, shared.Callbacks)
                            .ConfigureAwait(false)
                        : await httpClient!
                            .FetchResourceWithCallbacksAsync(parsedUrl, resourceRequest, shared.Callbacks)
                            .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    response = null;
                }
            }

            var bytes = response is { Status: >= 200 and < 300 } ok ? ok.Body : null;
            if (shared.DocumentGeneration == documentGeneration)
            {
                if (bytes is not null && RenderPaint.ImageIntrinsicDimensions(bytes) is not null)
                {
                    shared.RenderResources.SeedImage(selectedUrl, profile, bytes);
                    // The leader owns the unknown-to-known cache transition. Followers
                    // only observe this result and must not invalidate again.
                    RenderInvalidation.InvalidateRenderResourceGeometry(shared);
                }
                else
                {
                    shared.RenderResources.SeedImageMissing(selectedUrl, profile);
                }
            }

            List<TaskCompletionSource> pending = [];
            if (shared.RenderImageInFlight.Remove(requestKey, out var registered))
            {
                pending = registered;
            }

            foreach (var waiter in pending)
            {
                waiter.TrySetResult();
            }

            return FinishAsyncImageMetadata(shared, nodeId, documentGeneration, selectedUrl, profile);
        }
        finally
        {
            shared.PageInFlight.Decrement();
        }
    }

    private static Uri? TryUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static List<DynamicFontFace>? ParseDynamicFonts(string registrations)
    {
        try
        {
            using var document = JsonDocument.Parse(registrations);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<DynamicFontFace> fonts = [];
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || RequiredString(element, "family") is not { } family
                    || RequiredString(element, "source") is not { } source
                    || RequiredString(element, "style") is not { } style
                    || RequiredString(element, "weight") is not { } weight
                    || RequiredString(element, "unicodeRange") is not { } unicodeRange)
                {
                    return null;
                }

                fonts.Add(new DynamicFontFace
                {
                    Family = family,
                    Source = source,
                    Style = style,
                    Weight = weight,
                    UnicodeRange = unicodeRange,
                });
            }

            return fonts;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool DynamicFontsEqual(List<DynamicFontFace> left, List<DynamicFontFace> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The <c>op_waapi_create</c> payload, decoded from the shim's JSON.</summary>
    private sealed record WaapiCreateInput(
        ulong Id,
        uint Node,
        List<WaapiKeyframeInput> Keyframes,
        float Duration,
        float Delay,
        float Iterations,
        bool IterationsInfinite,
        string Fill,
        string Direction,
        float[]? EasingBezier,
        List<float>? LinearEasing)
    {
        internal static WaapiCreateInput? Parse(string input)
        {
            try
            {
                using var document = JsonDocument.Parse(input);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetUInt64(out var id)
                    || !root.TryGetProperty("node", out var nodeElement)
                    || !nodeElement.TryGetUInt32(out var node)
                    || !root.TryGetProperty("keyframes", out var keyframesElement)
                    || keyframesElement.ValueKind != JsonValueKind.Array
                    || Single(root, "duration") is not { } duration
                    || Single(root, "delay") is not { } delay
                    || Single(root, "iterations") is not { } iterations
                    || RequiredString(root, "fill") is not { } fill
                    || RequiredString(root, "direction") is not { } direction)
                {
                    return null;
                }

                List<WaapiKeyframeInput> keyframes = [];
                foreach (var frame in keyframesElement.EnumerateArray())
                {
                    if (frame.ValueKind != JsonValueKind.Object || Single(frame, "offset") is not { } offset)
                    {
                        return null;
                    }

                    float? opacity = frame.TryGetProperty("opacity", out var o)
                        && o.ValueKind == JsonValueKind.Number
                        ? o.GetSingle()
                        : null;
                    var transform = frame.TryGetProperty("transform", out var t)
                        && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;
                    keyframes.Add(new WaapiKeyframeInput(offset, opacity, transform));
                }

                var infinite = root.TryGetProperty("iterationsInfinite", out var inf)
                    && inf.ValueKind == JsonValueKind.True;

                float[]? bezier = null;
                if (root.TryGetProperty("easingBezier", out var bezierElement)
                    && bezierElement.ValueKind == JsonValueKind.Array)
                {
                    var values = new List<float>(4);
                    foreach (var value in bezierElement.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Number)
                        {
                            return null;
                        }

                        values.Add(value.GetSingle());
                    }

                    if (values.Count != 4)
                    {
                        return null;
                    }

                    bezier = [.. values];
                }

                List<float>? linear = null;
                if (root.TryGetProperty("linearEasing", out var linearElement)
                    && linearElement.ValueKind == JsonValueKind.Array)
                {
                    linear = [];
                    foreach (var value in linearElement.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Number)
                        {
                            return null;
                        }

                        linear.Add(value.GetSingle());
                    }
                }

                return new WaapiCreateInput(
                    id, node, keyframes, duration, delay, iterations, infinite, fill, direction, bezier, linear);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static float? Single(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetSingle()
                : null;
    }

    private sealed record WaapiKeyframeInput(float Offset, float? Opacity, string? Transform);
}
