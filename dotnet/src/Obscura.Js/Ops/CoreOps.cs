using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Dom;
using Obscura.Js.Modules;
using Obscura.Net;

namespace Obscura.Js.Ops;

/// <summary>Whether a posted task's owning realm can still be consulted.</summary>
/// <remarks>
/// Rust reaches all three states through <c>Weak&lt;RefCell&lt;ObscuraState&gt;&gt;</c>:
/// the realm is gone, the realm is mid-mutation and must not be re-entered, or its
/// document generation can be read. See <see cref="ObscuraState.IsBorrowed"/>.
/// </remarks>
public abstract record PostedTaskOwnerStatus
{
    private PostedTaskOwnerStatus()
    {
    }

    public sealed record Gone : PostedTaskOwnerStatus
    {
        public static readonly Gone Instance = new();
    }

    public sealed record Busy : PostedTaskOwnerStatus
    {
        public static readonly Busy Instance = new();
    }

    public sealed record Generation(ulong Value) : PostedTaskOwnerStatus;
}

/// <summary>
/// The ops that are neither DOM dispatch, render, nor network: scripts, shadow
/// roots, cookies, navigation, frames, posted tasks, bindings, crypto, URL and text
/// encoding.
/// </summary>
public static class CoreOps
{
    /// <summary>The value a posted-task callback receives when its owner is unusable.</summary>
    public const double InvalidPostedTaskGeneration = -1.0;

    private const int MaxPendingRuntimeEvents = 1024;
    private const int MaxPendingFrameDocuments = 64;
    private const long MaxPendingFrameBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Raised for every <c>console.*</c> call, so a host can mirror it the way the
    /// Rust engine mirrors it into <c>tracing</c>.
    /// </summary>
    public static event Action<string, string>? ConsoleMessage;

    /// <summary><c>op_script_mark_started</c>. Marks one script inert.</summary>
    public static bool OpScriptMarkStarted(ObscuraState state, uint nid) => OpGuard.Run(
        "op_script_mark_started",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.Dom is not { } dom)
            {
                return false;
            }

            var nodeId = NodeId.New(nid);
            if (!StateHelpers.NodeIsScript(dom, nodeId))
            {
                return false;
            }

            state.AlreadyStartedScripts.Add(nodeId);
            return true;
        },
        false);

    /// <summary>
    /// <c>op_script_try_start</c>. Atomically claims an executable script. A false
    /// result means the node was created inert by an HTML-string API or has already
    /// been prepared once.
    /// </summary>
    public static bool OpScriptTryStart(ObscuraState state, uint nid) => OpGuard.Run(
        "op_script_try_start",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.Dom is not { } dom)
            {
                return false;
            }

            var nodeId = NodeId.New(nid);
            return StateHelpers.NodeIsScript(dom, nodeId) && state.AlreadyStartedScripts.Add(nodeId);
        },
        false);

    /// <summary>
    /// <c>op_shadow_attach</c>. Attaches one native shadow-tree scope without making
    /// it part of the light tree. Layout intentionally remains unaware of the detached
    /// root until scoped style, slot assignment, and composed-tree paint exist.
    /// </summary>
    /// <returns>
    /// The root node id, <c>-2</c> when the host already has a shadow root, or
    /// <c>-1</c> for any other failure.
    /// </returns>
    public static int OpShadowAttach(ObscuraState state, uint hostNid, string mode) => OpGuard.Run(
        "op_shadow_attach",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            ShadowRootMode rootMode;
            switch (mode)
            {
                case "open":
                    rootMode = ShadowRootMode.Open;
                    break;
                case "closed":
                    rootMode = ShadowRootMode.Closed;
                    break;
                default:
                    return -1;
            }

            if (state.Dom is not { } dom)
            {
                return -1;
            }

            var error = dom.TryAttachShadowRoot(NodeId.New(hostNid), rootMode, out var root);
            return error switch
            {
                AttachShadowError.None => (int)root.Raw,
                AttachShadowError.HostAlreadyHasShadowRoot => -2,
                _ => -1,
            };
        },
        -1);

    /// <summary>
    /// <c>op_shadow_root_info</c>. Native host-owned shadow identity as
    /// <c>root-id\0mode</c>. Closed roots are included here; the Web-facing
    /// <c>Element.shadowRoot</c> getter applies mode visibility in bootstrap.js.
    /// </summary>
    public static string OpShadowRootInfo(ObscuraState state, uint hostNid) => OpGuard.Run(
        "op_shadow_root_info",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.Dom is not { } dom)
            {
                return string.Empty;
            }

            if (dom.ShadowRootOf(NodeId.New(hostNid)) is not { } root
                || dom.ShadowRootInfo(root) is not { } shadow)
            {
                return string.Empty;
            }

            var mode = shadow.Mode == ShadowRootMode.Open ? "open" : "closed";
            return shadow.Id.Raw.ToString(CultureInfo.InvariantCulture) + "\0" + mode;
        },
        string.Empty);

    /// <summary><c>op_runtime_events_enabled</c>.</summary>
    public static bool OpRuntimeEventsEnabled(ObscuraState state) => OpGuard.Run(
        "op_runtime_events_enabled",
        () => state.RuntimeEventsEnabled,
        false);

    /// <summary><c>op_console_msg</c>. Records one console call for the CDP Runtime domain.</summary>
    public static void OpConsoleMsg(ObscuraState page, string level, string msg, string argsJson) =>
        OpGuard.Run("op_console_msg", () =>
        {
            ArgumentNullException.ThrowIfNull(page);
            ConsoleMessage?.Invoke(level, msg);

            if (!page.RuntimeEventsEnabled)
            {
                return;
            }

            List<JsonNode?> args;
            try
            {
                if (JsonNode.Parse(argsJson) is not JsonArray array)
                {
                    return;
                }

                args = [];
                for (var i = 0; i < array.Count; i++)
                {
                    args.Add(array[i]?.DeepClone());
                }
            }
            catch (JsonException)
            {
                return;
            }

            while (page.PendingRuntimeEvents.Count >= MaxPendingRuntimeEvents)
            {
                page.PendingRuntimeEvents.Dequeue();
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            page.PendingRuntimeEvents.Enqueue(
                new RuntimeEvent.Console(new RuntimeConsoleEvent(level, args, timestamp)));
        });

    /// <summary><c>op_get_cookies</c>. The JS-visible cookies for the realm's document URL.</summary>
    public static string OpGetCookies(ObscuraState state) => OpGuard.Run(
        "op_get_cookies",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.CookieJar is not { } jar
                || !Uri.TryCreate(state.Url, UriKind.Absolute, out var url))
            {
                return string.Empty;
            }

            return jar.GetJsVisibleCookies(url);
        },
        string.Empty);

    /// <summary><c>op_set_cookie</c>. Applies one <c>document.cookie</c> assignment.</summary>
    public static void OpSetCookie(ObscuraState state, string cookieStr) =>
        OpGuard.Run("op_set_cookie", () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            if (state.CookieJar is not { } jar
                || !Uri.TryCreate(state.Url, UriKind.Absolute, out var url))
            {
                return;
            }

            jar.SetCookieFromJs(cookieStr, url);
        });

    /// <summary>
    /// <c>op_navigate</c>. A frame that navigates itself must not move the top
    /// document, so the navigation is recorded against the calling realm.
    /// </summary>
    public static void OpNavigate(ObscuraState state, string url, string method, string body) =>
        OpGuard.Run("op_navigate", () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            state.Url = url;
            state.PendingNavigation = (url, method, body);
        });

    internal static int FrameMessageQueueEntryLimit() =>
        EnvInt("OBSCURA_FRAME_MESSAGE_QUEUE_ENTRIES", 4096);

    internal static long FrameMessageQueueByteLimit() =>
        EnvInt("OBSCURA_FRAME_MESSAGE_QUEUE_BYTES", 8 * 1024 * 1024);

    /// <summary>
    /// <c>op_post_frame_message</c>. Queues one postMessage for another realm.
    /// </summary>
    /// <remarks>
    /// Always on the page's state, never the caller's: the Page drains a single
    /// queue, and a message sent by a nested frame would otherwise sit in that frame's
    /// own state and never be looked at. The queue is capped, because script can post
    /// in a synchronous loop while the host only drains between event loop turns, and
    /// this buffer lives outside V8's heap where the heap-limit guard cannot see it.
    /// Over the cap the newest message is dropped, keeping the earlier traffic that a
    /// widget handshake actually depends on.
    /// </remarks>
    public static void OpPostFrameMessage(
        ObscuraState page,
        uint targetFrameId,
        uint sourceFrameId,
        string origin,
        string targetOrigin,
        string dataJson) =>
        OpGuard.Run("op_post_frame_message", () =>
        {
            ArgumentNullException.ThrowIfNull(page);
            var overEntries = page.PendingFrameMessages.Count >= FrameMessageQueueEntryLimit();
            var overBytes = page.PendingFrameMessageBytes + dataJson.Length > FrameMessageQueueByteLimit();
            if (overEntries || overBytes)
            {
                return;
            }

            page.PendingFrameMessageBytes += dataJson.Length;
            page.PendingFrameMessages.Add(new PendingFrameMessage
            {
                TargetFrameId = targetFrameId,
                SourceFrameId = sourceFrameId,
                Origin = origin,
                TargetOrigin = targetOrigin,
                DataJson = dataJson,
            });
        });

    /// <summary>
    /// <c>op_frame_document_ready</c>. Hands a fetched frame document to the host and
    /// returns the id the frame will have. The realm itself is built later, by whoever
    /// owns the runtime. A zero id means the bounded native queue refused the document.
    /// </summary>
    public static uint OpFrameDocumentReady(
        ObscuraState page,
        uint parentFrameId,
        string url,
        string html,
        ulong viewportWidth,
        ulong viewportHeight) => OpGuard.Run(
        "op_frame_document_ready",
        () =>
        {
            ArgumentNullException.ThrowIfNull(page);
            long bytes = (long)url.Length + html.Length;
            if (page.PendingFrames.Count >= MaxPendingFrameDocuments
                || page.PendingFrameBytes + bytes > MaxPendingFrameBytes)
            {
                return 0u;
            }

            if (page.FrameIdCounter == uint.MaxValue)
            {
                return 0u;
            }

            var frameId = page.FrameIdCounter + 1;
            page.FrameIdCounter = frameId;
            page.PendingFrameBytes += bytes;
            page.PendingFrames.Add(new PendingFrame
            {
                FrameId = frameId,
                Url = url,
                Html = html,
                ViewportWidth = viewportWidth,
                ViewportHeight = viewportHeight,
                ParentFrameId = parentFrameId,
            });
            return frameId;
        },
        0u);

    /// <summary>
    /// <c>op_sleep</c>. Resolves after <paramref name="millis"/>, as the timer source
    /// for child frame realms.
    /// </summary>
    /// <remarks>
    /// deno_core's own timer queue is not usable from a frame, so Rust resolves an
    /// ordinary promise instead and lets V8 report the frame as the microtask context.
    /// The port has the same requirement for the same reason.
    /// </remarks>
    public static Task OpSleepAsync(ulong millis) =>
        Task.Delay(TimeSpan.FromMilliseconds(millis));

    /// <summary>
    /// <c>op_async_runtime_available</c>. Whether async host work can be scheduled.
    /// </summary>
    /// <remarks>
    /// Some low-level embedders intentionally execute a synchronous expression without
    /// entering an async context (update scroll state, then immediately capture). The
    /// bootstrap uses this probe for its sync-only compatibility path. The port always
    /// has a thread pool available, so this is always true.
    /// </remarks>
    public static bool OpAsyncRuntimeAvailable() => true;

    /// <summary>
    /// <c>op_binding_called</c>. Records a binding call from page JS. The CDP layer
    /// drains this queue after every dispatch and emits one
    /// <c>Runtime.bindingCalled</c> event per entry - that is how puppeteer's
    /// <c>page.exposeFunction</c> callbacks fire.
    /// </summary>
    public static void OpBindingCalled(ObscuraState page, string name, string payload) =>
        OpGuard.Run("op_binding_called", () =>
        {
            ArgumentNullException.ThrowIfNull(page);
            page.PendingBindingCalls.Add((name, payload));
        });

    /// <summary>Reads the owning realm's document generation, if it can be read at all.</summary>
    public static PostedTaskOwnerStatus PostedTaskOwnerStatusOf(WeakReference<ObscuraState> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.TryGetTarget(out var state))
        {
            return PostedTaskOwnerStatus.Gone.Instance;
        }

        return state.IsBorrowed
            ? PostedTaskOwnerStatus.Busy.Instance
            : new PostedTaskOwnerStatus.Generation(state.DocumentGeneration);
    }

    /// <summary><c>op_add_import_map</c>. Returns the parse error, or "" on success.</summary>
    public static string OpAddImportMap(ObscuraState state, string source, string baseUrl) => OpGuard.Run(
        "op_add_import_map",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            return state.ImportMap.MergeParsed(source, baseUrl, out var error)
                ? string.Empty
                : error ?? "Invalid import map";
        },
        "Import map could not be added");

    /// <summary>
    /// <c>op_encoding_for_label</c>. The canonical (lower-cased) WHATWG name for a
    /// TextDecoder label, or "" if the label is unknown - the JS constructor turns ""
    /// into a RangeError.
    /// </summary>
    public static string OpEncodingForLabel(string label) => OpGuard.Run(
        "op_encoding_for_label",
        () => WhatwgEncoding.LabelName(label) ?? string.Empty,
        string.Empty);

    /// <summary>
    /// <c>op_text_decode</c>. Decodes bytes with a legacy/explicit encoding. Returns
    /// <c>{"ok":true,"v":&lt;string&gt;}</c> or <c>{"ok":false}</c> (unknown label, or a
    /// fatal decode error). The UTF-8 non-fatal common case is handled in JS.
    /// </summary>
    public static string OpTextDecode(string label, byte[] bytes, bool fatal, bool ignoreBom) => OpGuard.Run(
        "op_text_decode",
        () =>
        {
            var decoded = ContentEncoding.DecodeWithLabel(label, bytes, fatal, ignoreBom);
            if (decoded is null)
            {
                return "{\"ok\":false}";
            }

            var sb = new System.Text.StringBuilder(decoded.Length + 16);
            sb.Append("{\"ok\":true,\"v\":");
            SerdeJson.AppendString(sb, decoded);
            sb.Append('}');
            return sb.ToString();
        },
        "{\"ok\":false}");

    private static long EnvInt(string name, long fallback) =>
        long.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    private static int EnvInt(string name, int fallback) => (int)EnvInt(name, (long)fallback);
}
