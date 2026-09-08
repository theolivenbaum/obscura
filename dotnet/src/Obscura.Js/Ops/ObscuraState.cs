using System.Diagnostics;
using System.Text.Json.Nodes;
using Obscura.Dom;
using Obscura.Js.Modules;
using Obscura.Net;
using Obscura.Render;
using Obscura.Render.Css;

namespace Obscura.Js.Ops;

/// <summary>
/// The per-page (per-realm) state hub every op reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// This is the port of <c>ObscuraState</c> in <c>crates/obscura-js/src/ops.rs</c>.
/// The Rust engine holds it as <c>Rc&lt;RefCell&lt;ObscuraState&gt;&gt;</c>; the C# port
/// is a plain mutable class, because the engine is single-threaded per page and
/// the interior mutability exists only to satisfy Rust's borrow checker.
/// </para>
/// <para>
/// One instance belongs to one realm. The page's own realm is frame 0; every
/// child frame gets its own instance registered in <see cref="RealmStates"/>.
/// Fields the whole frame tree shares - the in-flight counter, the postMessage
/// queue, the pending-frame queue - are deliberately read off the *page's*
/// instance, not the caller's.
/// </para>
/// </remarks>
public sealed class ObscuraState
{
    private long _animationTimelineOrigin = Stopwatch.GetTimestamp();
    private int _borrowDepth;

    /// <summary>The document tree, or null before the first document is installed.</summary>
    public DomTree? Dom { get; set; }

    /// <summary>The document URL. Starts at <c>about:blank</c>, exactly as in Rust.</summary>
    public string Url { get; set; } = "about:blank";

    /// <summary>
    /// WHATWG canonical name of the document's character encoding (e.g. "UTF-8",
    /// "EUC-JP"). Backs <c>document.characterSet</c> and the URL query encoding
    /// override for <c>&lt;a&gt;</c>/<c>&lt;area&gt;</c> hrefs in legacy-charset documents.
    /// </summary>
    public string Encoding { get; set; } = "UTF-8";

    /// <summary>The navigation-time title snapshot. The DOM is authoritative after parsing.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// URL of the document that initiated this document's navigation. Direct
    /// browser/API navigations leave this empty; document-initiated navigations
    /// set it to the source document URL.
    /// </summary>
    public string Referrer { get; set; } = string.Empty;

    /// <summary>CDP <c>Network.setBlockedURLs</c> patterns, matched with <c>glob_match</c>.</summary>
    public List<string> BlockedUrls { get; } = [];

    public CookieJar? CookieJar { get; set; }

    public ObscuraHttpClient? HttpClient { get; set; }

    /// <summary>
    /// The owning page's passive on_request/on_response callbacks. Page-scoped, so
    /// scripted fetch()/XHR observation stays local to the page that registered it.
    /// </summary>
    public CallbackRegistry? Callbacks { get; set; }

    /// <summary>
    /// When set (stealth mode), scripted fetch()/XHR is routed through the stealth
    /// client so the request carries the browser identity headers instead of the
    /// plain ones <see cref="HttpClient"/> would send.
    /// </summary>
    public IStealthHttpClient? StealthClient { get; set; }

    /// <summary>A navigation recorded by <c>op_navigate</c>, drained by the Page.</summary>
    public (string Url, string Method, string Body)? PendingNavigation { get; set; }

    /// <summary>Where intercepted requests are published, when interception is on.</summary>
    public IInterceptSink? InterceptTx { get; set; }

    public ulong InterceptCounter { get; set; }

    public bool InterceptEnabled { get; set; }

    /// <summary>
    /// Queue of (binding name, payload) calls made by page JS via
    /// <c>op_binding_called</c>. Drained by the CDP layer after each dispatch and
    /// emitted as <c>Runtime.bindingCalled</c> events.
    /// </summary>
    public List<(string Name, string Payload)> PendingBindingCalls { get; } = [];

    /// <summary>
    /// Console calls and uncaught script exceptions, in occurrence order. The CDP
    /// layer drains this after commands and autonomous event-loop turns.
    /// </summary>
    public Queue<RuntimeEvent> PendingRuntimeEvents { get; } = new();

    public bool RuntimeEventsEnabled { get; set; }

    public ulong RuntimeExceptionCounter { get; set; }

    public Dictionary<string, StoredNetworkResponseBody> NetworkResponseBodies { get; } =
        new(StringComparer.Ordinal);

    public Queue<string> NetworkResponseBodyOrder { get; } = new();

    public ulong NetworkResponseBodyCounter { get; set; }

    /// <summary>
    /// Absolute URLs requested via JS fetch()/XHR (<c>op_fetch_url</c>), in request
    /// order. Surfaced by <c>--dump assets</c> so resources pulled in by script, not
    /// just static DOM attributes, are listed.
    /// </summary>
    public List<string> FetchedUrls { get; } = [];

    /// <summary>
    /// Network events for script-initiated requests, drained by the Page into its
    /// own list so the CDP layer emits requestWillBeSent / responseReceived.
    /// </summary>
    public List<JsNetworkEvent> JsNetworkEvents { get; } = [];

    /// <summary>
    /// Frame documents that have been fetched and are waiting for a realm. Building
    /// one needs the whole runtime, which an op cannot reach, so
    /// <c>op_frame_document_ready</c> queues here and the Page drains it between
    /// event loop turns.
    /// </summary>
    public List<PendingFrame> PendingFrames { get; } = [];

    /// <summary>Total URL and HTML bytes held by <see cref="PendingFrames"/>.</summary>
    public long PendingFrameBytes { get; set; }

    public uint FrameIdCounter { get; set; }

    /// <summary>Which frame this state belongs to; 0 is the page's own realm.</summary>
    public uint FrameId { get; set; }

    /// <summary>
    /// postMessage traffic between realms, waiting to be delivered. Queued on the
    /// *page's* state whichever realm sent it, so one drain sees the traffic of the
    /// whole tree.
    /// </summary>
    public List<PendingFrameMessage> PendingFrameMessages { get; } = [];

    /// <summary>Bytes of payload currently queued above, tracked rather than summed.</summary>
    public long PendingFrameMessageBytes { get; set; }

    /// <summary>
    /// Requests initiated by this runtime only. Browser contexts share their
    /// transport client across pages, so the client's aggregate counter cannot be
    /// used as a page-readiness signal. Shared by reference with child frames.
    /// </summary>
    public InFlightCounter PageInFlight { get; set; } = new();

    /// <summary>
    /// Monotonic generation for observable changes to the connected document. The
    /// browser settle policy samples this to distinguish useful deferred rendering
    /// work from unrelated long-lived timers.
    /// </summary>
    public ulong ActivityGeneration { get; set; }

    /// <summary>
    /// Monotonic identity of the currently installed document. Async resource
    /// completions use this to discard bytes and lifecycle results belonging to a
    /// navigation that has already been replaced.
    /// </summary>
    public ulong DocumentGeneration { get; set; }

    /// <summary>
    /// Cached document base URL. Computing it walks the tree and runs the selector
    /// engine, and the JS layer asks for it on every relative URL.
    /// </summary>
    public BaseUrlCache? BaseUrlCache { get; set; }

    /// <summary>
    /// Final image/font-aware layout shared by CSSOM geometry and screenshots.
    /// DOM/style/viewport changes clear this value but retain resource bytes.
    /// </summary>
    public PreparedRender? PreparedRender { get; set; }

    /// <summary>
    /// CSS media type selected for the next retained layout. Live pages use screen;
    /// PDF export switches to print for one synchronous capture and restores screen.
    /// </summary>
    public CssMediaType RenderMedia { get; set; } = CssMediaType.Screen;

    /// <summary>
    /// Explicit document-timeline sample used by the next style/layout flush.
    /// Captures set this to either deterministic T=0 or live document time.
    /// </summary>
    public AnimationSample AnimationSample { get; set; }

    public AnimationTimelineState AnimationTimeline { get; set; } = new();

    /// <summary>
    /// Rust holds an <c>Instant</c>; the port holds the equivalent monotonic
    /// timestamp so tests can move the origin into the past.
    /// </summary>
    public long AnimationTimelineOrigin
    {
        get => _animationTimelineOrigin;
        set => _animationTimelineOrigin = value;
    }

    /// <summary>Milliseconds since <see cref="AnimationTimelineOrigin"/>.</summary>
    public double AnimationTimelineElapsedMilliseconds =>
        (Stopwatch.GetTimestamp() - _animationTimelineOrigin) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Restarts the document timeline, as <c>Instant::now()</c> does in Rust.</summary>
    public void ResetAnimationTimelineOrigin() => _animationTimelineOrigin = Stopwatch.GetTimestamp();

    /// <summary>Moves the timeline origin <paramref name="elapsed"/> into the past.</summary>
    public void SetAnimationTimelineElapsed(TimeSpan elapsed) =>
        _animationTimelineOrigin = Stopwatch.GetTimestamp() - (long)(elapsed.TotalSeconds * Stopwatch.Frequency);

    /// <summary>
    /// Host/HTML task epoch for document-timeline sampling. Geometry and
    /// computed-style reads within one task share one frozen animation frame.
    /// </summary>
    public ulong AnimationTaskGeneration { get; set; }

    public ulong AnimationSampledTaskGeneration { get; set; }

    /// <summary>
    /// Connected mutations awaiting dependency-indexed retained style refresh. Tree
    /// changes carry stable node/parent ids so a later geometry read can coalesce
    /// framework DOM churn into one conservative local cascade.
    /// </summary>
    public List<RetainedStyleMutation> PendingStyleMutations { get; } = [];

    /// <summary>
    /// Page-lifetime raw image/font bytes. A new document resets this cache;
    /// relayout of the same document reuses it without refetching.
    /// </summary>
    public RenderResourceCache RenderResources { get; set; } = RenderResourceCache.Default();

    /// <summary>
    /// Waiters sharing an asynchronous HTMLImageElement request. The key keeps
    /// navigation identity and request credentials separate so neither stale pages
    /// nor incompatible CORS profiles share a completion.
    /// </summary>
    public Dictionary<(ulong Generation, string Url, ImageRequestProfile Profile), List<TaskCompletionSource>>
        RenderImageInFlight { get; } = [];

    /// <summary>
    /// One exact-key compiled author stylesheet for this document. Connected
    /// mutations still discard <see cref="PreparedRender"/>; the next prepare reuses
    /// only parsing/indexing when ordered CSS source and viewport stay identical.
    /// </summary>
    public StylesheetCache StylesheetCache { get; set; } = new();

    /// <summary>
    /// Script-created faces in this document's <c>FontFaceSet</c>. Separate from the
    /// DOM so the bridge does not manufacture a selector-visible <c>&lt;style&gt;</c>
    /// element merely to feed the renderer.
    /// </summary>
    public List<DynamicFontFace> DynamicFonts { get; set; } = [];

    /// <summary>
    /// Live Canvas2D backing stores keyed by stable DOM identity. Pixel damage
    /// updates this resource independently of retained style/layout geometry.
    /// </summary>
    public Dictionary<NodeId, CanvasBackingSurface> CanvasSurfaces { get; } = [];

    public (float Width, float Height) Viewport { get; set; } = (1280.0f, 720.0f);

    /// <summary>
    /// Root scrolling offset in CSS pixels, clamped against the cached document
    /// overflow. The single source read by CSSOM geometry and screenshot paint.
    /// </summary>
    public (float X, float Y) ScrollOffset { get; set; }

    /// <summary>
    /// Element scroll offsets persist by DOM identity across relayout. Dense
    /// renderer scroll ids are rebuild-local and resolved only into the snapshot.
    /// </summary>
    public Dictionary<NodeId, (float X, float Y)> ElementScrollOffsets { get; } = [];

    public ulong ScrollGeneration { get; set; }

    public (ulong Generation, ResolvedScrollState State)? ResolvedScroll { get; set; }

    /// <summary>
    /// Window-global import-map state shared by parser-discovered scripts,
    /// dynamically inserted import maps, and the module loader.
    /// </summary>
    public ImportMap ImportMap { get; set; } = new();

    /// <summary>
    /// HTML's per-script "already started" flag. Native page state rather than
    /// wrapper state, because it must survive moves and clones and because fragment
    /// parsing can create nodes before a JS wrapper exists.
    /// </summary>
    public HashSet<NodeId> AlreadyStartedScripts { get; } = [];

    /// <summary>
    /// The document's input stream for <c>document.write()</c>, created on the first
    /// call. Null until then, and reset to null by <c>document.open()</c>.
    /// </summary>
    public DocumentWriteStream? WriteStream { get; set; }

    /// <summary>
    /// Whether a mutable borrow is currently outstanding.
    /// </summary>
    /// <remarks>
    /// Rust reaches this state through <c>RefCell::try_borrow</c>, and
    /// <c>op_posted_task</c> treats it as "the owner is busy, do not touch it".
    /// C# has no borrow tracking, so the same re-entrancy window is modelled
    /// explicitly by <see cref="EnterExclusive"/>. Nothing else depends on it.
    /// </remarks>
    public bool IsBorrowed => _borrowDepth > 0;

    /// <summary>Marks the state busy for the lifetime of the returned scope.</summary>
    public ExclusiveScope EnterExclusive() => new(this);

    /// <summary>The scope handle returned by <see cref="EnterExclusive"/>.</summary>
    public readonly struct ExclusiveScope : IDisposable
    {
        private readonly ObscuraState _state;

        internal ExclusiveScope(ObscuraState state)
        {
            _state = state;
            state._borrowDepth++;
        }

        public void Dispose() => _state._borrowDepth--;
    }
}

/// <summary>A shared, mutable in-flight request counter, standing in for <c>Arc&lt;AtomicU32&gt;</c>.</summary>
public sealed class InFlightCounter
{
    private int _value;

    public int Value => Volatile.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);

    public void Decrement() => Interlocked.Decrement(ref _value);
}

/// <summary>Where <c>op_fetch_url</c> publishes an intercepted request.</summary>
public interface IInterceptSink
{
    /// <summary>Returns false when the consumer is gone, matching a closed Rust channel.</summary>
    bool TrySend(InterceptedRequest request);
}

/// <summary>How a consumer resolved an intercepted request.</summary>
public abstract record InterceptResolution
{
    private InterceptResolution()
    {
    }

    public sealed record Continue(
        string? Url,
        string? Method,
        IReadOnlyDictionary<string, string>? Headers,
        string? Body) : InterceptResolution;

    public sealed record Fulfill(
        int Status,
        IReadOnlyDictionary<string, string> Headers,
        string Body) : InterceptResolution;

    public sealed record Fail(string Reason) : InterceptResolution;
}

/// <summary>One request handed to the interception channel, with its resolver.</summary>
public sealed class InterceptedRequest
{
    public required string RequestId { get; init; }

    public required string Url { get; init; }

    public required string Method { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public required string ResourceType { get; init; }

    /// <summary>The consumer completes this with its decision (Rust's oneshot sender).</summary>
    public TaskCompletionSource<InterceptResolution> Resolver { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A response body retained for CDP <c>Network.getResponseBody</c>.</summary>
public sealed record StoredNetworkResponseBody(string Body, bool Base64Encoded);

/// <summary>
/// A network request made from page JS (fetch()/XHR/dynamic resource) recorded so
/// the CDP layer can emit Network.requestWillBeSent / responseReceived for it.
/// </summary>
public sealed class JsNetworkEvent
{
    /// <summary>Matches the <c>fetch-{N}</c> id under which the body is stored.</summary>
    public required string RequestId { get; init; }

    public required string Url { get; init; }

    public required string Method { get; init; }

    public required int Status { get; init; }

    public required IReadOnlyDictionary<string, string> ResponseHeaders { get; init; }

    public required int BodySize { get; init; }

    public required double Timestamp { get; init; }
}

/// <summary>A frame document waiting to be given a realm.</summary>
public sealed class PendingFrame
{
    public required uint FrameId { get; init; }

    public required string Url { get; init; }

    public required string Html { get; init; }

    public required ulong ViewportWidth { get; init; }

    public required ulong ViewportHeight { get; init; }

    /// <summary>The frame that holds this one; 0 when the page does.</summary>
    public required uint ParentFrameId { get; init; }
}

/// <summary>One <c>postMessage</c> in flight between two realms.</summary>
public sealed class PendingFrameMessage
{
    /// <summary>Where it is going. 0 is the page's realm.</summary>
    public required uint TargetFrameId { get; init; }

    /// <summary>Where it came from, so the receiver can reply through <c>event.source</c>.</summary>
    public required uint SourceFrameId { get; init; }

    /// <summary>The sender's origin, for <c>event.origin</c>.</summary>
    public required string Origin { get; init; }

    /// <summary>
    /// The origin the sender restricted delivery to. <c>"*"</c> means any origin;
    /// <c>"/"</c> means the receiver must be same-origin as the sender; anything else
    /// is matched against the receiver's own origin. An empty string means the sender
    /// did not specify one and delivery stays permissive.
    /// </summary>
    public required string TargetOrigin { get; init; }

    /// <summary>The payload, JSON encoded.</summary>
    public required string DataJson { get; init; }
}

/// <summary>What the cached base values were computed from.</summary>
public sealed class BaseUrlCache
{
    public required ulong ActivityGeneration { get; init; }

    public required ulong DocumentGeneration { get; init; }

    public required string Url { get; init; }

    public required string? Resolved { get; init; }

    public required string? RawHref { get; init; }
}

/// <summary>A console call recorded for the CDP Runtime domain.</summary>
public sealed record RuntimeConsoleEvent(string Kind, List<JsonNode?> Args, double Timestamp);

/// <summary>An uncaught script exception recorded for the CDP Runtime domain.</summary>
public sealed record RuntimeExceptionEvent(
    ulong ExceptionId,
    string Name,
    string Description,
    string Url,
    long LineNumber,
    long ColumnNumber,
    List<JsonNode?> StackTrace,
    double Timestamp);

/// <summary>Either kind of pending runtime event, in occurrence order.</summary>
public abstract record RuntimeEvent
{
    private RuntimeEvent()
    {
    }

    public sealed record Console(RuntimeConsoleEvent Event) : RuntimeEvent;

    public sealed record Exception(RuntimeExceptionEvent Event) : RuntimeEvent;
}

/// <summary>
/// A live Canvas2D backing store retained from V8.
/// </summary>
/// <remarks>
/// Rust holds a <c>JsBuffer</c>, which owns a shared reference to the ArrayBuffer
/// backing store so the pixels stay valid while the canvas wrapper and native page
/// state share it. The port holds an <see cref="IJsBuffer"/> for the same reason:
/// paint borrows these bytes synchronously while JavaScript is not executing.
/// </remarks>
public sealed class CanvasBackingSurface
{
    public required uint Width { get; init; }

    public required uint Height { get; init; }

    public required IJsBuffer Pixels { get; init; }
}

/// <summary>
/// A byte view onto a JavaScript typed array that stays live for as long as the
/// host keeps it, standing in for deno_core's <c>JsBuffer</c>.
/// </summary>
public interface IJsBuffer
{
    int Length { get; }

    /// <summary>Reads the current contents. Callers must not retain the result.</summary>
    ReadOnlyMemory<byte> Read();
}

/// <summary>An <see cref="IJsBuffer"/> over managed bytes, for hosts and tests.</summary>
public sealed class ByteArrayJsBuffer(byte[] bytes) : IJsBuffer
{
    public int Length => bytes.Length;

    public ReadOnlyMemory<byte> Read() => bytes;
}
