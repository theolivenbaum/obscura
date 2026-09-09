using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Obscura.Dom;
using Obscura.Js.Ops;
using Obscura.Js.Runtime;
using Obscura.Js.Url;
using Obscura.Net;
using Obscura.Render.Css;

namespace Obscura.Browser;

/// <summary>One network request/response this page observed.</summary>
public sealed class NetworkEvent
{
    public required string RequestId { get; init; }

    public required string Url { get; init; }

    public required string Method { get; init; }

    public required string ResourceType { get; init; }

    public required int Status { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Shared, so a large header map is not copied per observer.</summary>
    public required IReadOnlyDictionary<string, string> ResponseHeaders { get; init; }

    public required int BodySize { get; init; }

    public required double Timestamp { get; init; }
}

/// <summary>A response body retained for <c>Network.getResponseBody</c>.</summary>
public sealed record StoredResponseBody(string Body, bool Base64Encoded);

/// <summary>Which <c>PageError</c> variant a <see cref="PageException"/> carries.</summary>
public enum PageErrorKind
{
    InvalidUrl,
    NetworkError,
    ParseError,

    /// <summary>
    /// A page kept triggering its own navigations until the chain's limit was
    /// exhausted. HTTP 3xx redirects are followed one layer down, in
    /// <c>Obscura.Net</c>, and report <c>TooManyRedirects</c>.
    /// </summary>
    TooManyClientNavigations,
}

/// <summary>Port of <c>PageError</c>.</summary>
public sealed class PageException : Exception
{
    public PageException(PageErrorKind kind, string message)
        : base(message) => Kind = kind;

    public PageException(PageErrorKind kind, string message, int chainLimit)
        : base(message)
    {
        Kind = kind;
        ChainLimit = chainLimit;
    }

    public PageErrorKind Kind { get; }

    /// <summary>Meaningful only for <see cref="PageErrorKind.TooManyClientNavigations"/>.</summary>
    public int ChainLimit { get; }

    internal static PageException InvalidUrl(string detail) =>
        new(PageErrorKind.InvalidUrl, $"Invalid URL: {detail}");

    internal static PageException Network(string detail) =>
        new(PageErrorKind.NetworkError, $"Network error: {detail}");

    internal static PageException Parse(string detail) =>
        new(PageErrorKind.ParseError, $"Parse error: {detail}");

    internal static PageException TooManyClientNavigations(int limit) =>
        new(
            PageErrorKind.TooManyClientNavigations,
            $"Too many client-initiated navigations, the chain reached its limit of {limit.ToString(CultureInfo.InvariantCulture)} documents",
            limit);
}

/// <summary>Metrics captured when CDP device emulation is first enabled.</summary>
/// <remarks>
/// Chromium keeps this baseline across subsequent override calls and restores it
/// only when the override is cleared.
/// </remarks>
internal readonly record struct DeviceMetricsBaseline(
    (float Width, float Height) Viewport,
    float DeviceScaleFactor);

/// <summary>A frame document moving through Page-owned realm construction.</summary>
internal abstract class PendingFrameWork
{
    internal abstract uint FrameId { get; }

    internal abstract uint ParentFrameId { get; }

    internal sealed class Unattached(PendingFrame frame) : PendingFrameWork
    {
        internal PendingFrame Frame { get; } = frame;

        internal override uint FrameId => Frame.FrameId;

        internal override uint ParentFrameId => Frame.ParentFrameId;
    }

    internal sealed class Attached : PendingFrameWork
    {
        internal required uint Id { get; init; }

        internal required uint ParentId { get; init; }

        internal required string FrameUrl { get; init; }

        internal required IReadOnlyList<string> Urls { get; init; }

        internal int NextUrl { get; set; }

        internal Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);

        internal override uint FrameId => Id;

        internal override uint ParentFrameId => ParentId;
    }
}

/// <summary>
/// One browser page: its document, its JavaScript realm, its navigation
/// lifecycle, and the network bookkeeping the CDP layer reports.
/// </summary>
public sealed partial class Page : IDisposable
{
    private ObscuraJsRuntime? _js;
    private (float Width, float Height)? _screenSizeOverride;
    private bool _screenMetricsEmulated;
    private DeviceMetricsBaseline? _deviceMetricsBaseline;
    private RgbaColor? _defaultBackgroundColorOverride;
    private long _documentTimelineOrigin = Stopwatch.GetTimestamp();
    private TimeSpan? _navigationTimeout;
    private int? _navigationChainLimit;
    private readonly Dictionary<string, StoredResponseBody> _responseBodies = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _responseBodyOrder = new();
    private uint _networkEventCounter;
    private IInterceptSink? _interceptTx;
    private List<string> _preloadScripts = [];
    private bool _runtimeEventsEnabled;
    internal readonly LinkedList<PendingFrameWork> _pendingFrameWork = new();
    private List<uint> _suspendedStartedScriptIds = [];
    private CdpObjectState _suspendedCdpObjectState = new();
    private readonly CallbackRegistry _callbacks = new();
    private bool _disposed;

    public Page(string id, BrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Id = id;
        // Chromium convention: the main frame's frameId == the targetId.
        // Playwright's frame manager looks up the main frame by targetId, so any
        // divergence here makes Page.getFrameTree return a frame the client
        // cannot match, triggering a Target.closeTarget and "Frame has been
        // detached".
        FrameId = id;
        Context = context;
        HttpClient = context.HttpClient;
    }

    public string Id { get; }

    public string FrameId { get; }

    public UrlRecord? Url { get; set; }

    public DomTree? Dom { get; set; }

    /// <summary>Live child frame realms, in creation order.</summary>
    public List<FrameRealm> Frames { get; } = [];

    /// <summary>
    /// This page's JavaScript realm. Replacing it disposes the previous runtime,
    /// which is what dropping the Rust <c>Option&lt;ObscuraJsRuntime&gt;</c> does.
    /// </summary>
    public ObscuraJsRuntime? Js
    {
        get => _js;
        set
        {
            if (ReferenceEquals(_js, value))
            {
                return;
            }
            ObscuraJsRuntime? previous = _js;
            _js = value;
            previous?.Dispose();
        }
    }

    public LifecycleState Lifecycle { get; set; } = LifecycleState.Idle;

    public ObscuraHttpClient HttpClient { get; }

    public BrowserContext Context { get; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Source document URL for the current document.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Url"/>: direct automation navigations
    /// have no referrer, while a navigation requested by page script uses the
    /// previous document.
    /// </remarks>
    public string Referrer { get; set; } = string.Empty;

    /// <summary>
    /// CSS viewport used by responsive page JavaScript and CDP screenshots. The
    /// physical <c>screen</c> fingerprint stays independent.
    /// </summary>
    public (float Width, float Height) Viewport { get; private set; } = (1280.0f, 720.0f);

    /// <summary>
    /// Output device pixels per CSS pixel for CDP surface capture. Layout and CSSOM
    /// stay in CSS pixels; <c>Emulation.setDeviceMetricsOverride</c> owns this
    /// independent raster scale.
    /// </summary>
    public float DeviceScaleFactor { get; private set; } = 1.0f;

    /// <summary>
    /// WHATWG canonical name of the current document's character encoding.
    /// </summary>
    /// <remarks>
    /// Detected when the response body is decoded, exposed to JS as
    /// <c>document.characterSet</c>, and used for the URL query encoding override on
    /// <c>&lt;a&gt;</c>/<c>&lt;area&gt;</c> hrefs in legacy-charset documents.
    /// </remarks>
    public string Encoding { get; set; } = "UTF-8";

    /// <summary>
    /// Navigation history for <c>Page.getNavigationHistory</c> /
    /// <c>navigateToHistoryEntry</c>, in visit order.
    /// </summary>
    public List<string> History { get; } = [];

    public int HistoryIndex { get; set; }

    public List<NetworkEvent> NetworkEvents { get; } = [];

    public bool InterceptEnabled { get; private set; }

    public List<string> InterceptBlockPatterns { get; } = [];

    public List<string> BlockedUrlPatterns { get; private set; } = [];

    /// <summary>
    /// Set the end-to-end navigation deadline for this page.
    /// </summary>
    /// <remarks>
    /// This page-scoped value takes precedence over <c>OBSCURA_NAV_TIMEOUT_MS</c>;
    /// callers that do not set it keep the environment-configurable 30s default.
    /// </remarks>
    public void SetNavigationTimeout(TimeSpan timeout) => _navigationTimeout = timeout;

    /// <summary>The effective end-to-end navigation deadline for this page.</summary>
    public TimeSpan NavigationTimeout => _navigationTimeout ?? PageHelpers.DefaultNavigationTimeout();

    /// <summary>
    /// Takes precedence over <c>OBSCURA_NAV_CHAIN_LIMIT</c>. A limit of 0 is raised
    /// to 1, which would otherwise report success having loaded nothing.
    /// </summary>
    public void SetNavigationChainLimit(int limit) => _navigationChainLimit = Math.Max(1, limit);

    public int NavigationChainLimit =>
        _navigationChainLimit ?? PageHelpers.DefaultNavigationChainLimitValue();

    internal bool ShouldBlockUrl(string url)
    {
        foreach (string pattern in BlockedUrlPatterns)
        {
            if (PageHelpers.UrlMatchesCdpPattern(pattern, url))
            {
                return true;
            }
        }
        if (InterceptEnabled)
        {
            foreach (string pattern in InterceptBlockPatterns)
            {
                if (PageHelpers.UrlMatchesCdpPattern(pattern, url))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Update the page's CSS viewport.
    /// </summary>
    /// <remarks>
    /// Calling this before navigation makes responsive scripts observe it from their
    /// first instruction; calling it on a live page mirrors CDP's device-metrics
    /// override surfaces.
    /// </remarks>
    public void SetViewport((float Width, float Height) viewport)
    {
        if (!float.IsFinite(viewport.Width) || !float.IsFinite(viewport.Height)
            || viewport.Width <= 0.0f || viewport.Height <= 0.0f)
        {
            return;
        }
        Viewport = viewport;
        Js?.SetViewport(viewport.Width, viewport.Height);
    }

    /// <summary>Set or clear the CDP physical-screen override independently of layout.</summary>
    public void SetScreenSizeOverride((float Width, float Height)? size, bool emulated)
    {
        _screenSizeOverride = size is { } value
            && float.IsFinite(value.Width) && float.IsFinite(value.Height)
            && value.Width > 0.0f && value.Height > 0.0f
                ? value
                : null;
        _screenMetricsEmulated = emulated;
        Js?.SetScreenSizeOverride(
            _screenSizeOverride is { } screen ? ((double)screen.Width, (double)screen.Height) : null,
            _screenMetricsEmulated);
    }

    /// <summary>
    /// Apply CDP device metrics relative to the metrics active when emulation was
    /// first enabled. A zero protocol dimension/scale arrives as null and therefore
    /// restores that axis from the retained baseline.
    /// </summary>
    public void ApplyDeviceMetricsOverride(
        float? width,
        float? height,
        float? deviceScaleFactor,
        (float Width, float Height)? screenSize,
        bool mobile)
    {
        _deviceMetricsBaseline ??= new DeviceMetricsBaseline(Viewport, DeviceScaleFactor);
        DeviceMetricsBaseline baseline = _deviceMetricsBaseline.Value;
        (float Width, float Height) viewport = (
            width ?? baseline.Viewport.Width,
            height ?? baseline.Viewport.Height);
        SetViewport(viewport);

        // Blink uses the effective widget size as the screen size for mobile
        // emulation when no complete explicit screen size was supplied.
        (float Width, float Height)? effectiveScreenSize =
            screenSize ?? (mobile ? viewport : null);
        SetScreenSizeOverride(effectiveScreenSize, true);
        SetDeviceScaleFactor(deviceScaleFactor ?? baseline.DeviceScaleFactor);
    }

    /// <summary>
    /// Disable CDP device metrics and restore the state captured by the first
    /// override. Clearing while emulation is inactive is intentionally a no-op.
    /// </summary>
    public void ClearDeviceMetricsOverride()
    {
        if (_deviceMetricsBaseline is not { } baseline)
        {
            return;
        }
        _deviceMetricsBaseline = null;
        SetViewport(baseline.Viewport);
        SetScreenSizeOverride(null, false);
        SetDeviceScaleFactor(baseline.DeviceScaleFactor);
    }

    /// <summary>
    /// Set the screenshot surface density without changing CSS layout. CDP uses zero
    /// to disable its override, which restores the native 1x surface.
    /// </summary>
    public void SetDeviceScaleFactor(float deviceScaleFactor)
    {
        if (!float.IsFinite(deviceScaleFactor) || deviceScaleFactor < 0.0f)
        {
            return;
        }
        DeviceScaleFactor = deviceScaleFactor == 0.0f ? 1.0f : deviceScaleFactor;
        if (Js is { } js)
        {
            TryExecute(js, "<device-metrics>", DevicePixelRatioScript());
        }
    }

    public void SetDefaultBackgroundColorOverride(RgbaColor? color) =>
        _defaultBackgroundColorOverride = color;

    internal RgbaColor CaptureSurfaceColor() =>
        _defaultBackgroundColorOverride ?? new RgbaColor(255, 255, 255, 255);

    private string DevicePixelRatioScript() =>
        "globalThis.devicePixelRatio="
        + DeviceScaleFactor.ToString("R", CultureInfo.InvariantCulture)
        + ";";

    internal Task<Response> DoFetchAsync(UrlRecord url, CancellationToken cancellationToken) =>
        HttpClient.FetchWithCallbacksAsync(NetUrl.From(url), _callbacks, cancellationToken);

    /// <summary>
    /// Rebuild the JavaScript realm for a new document.
    /// </summary>
    /// <remarks>
    /// The old code reused the V8 isolate and only re-bound <c>globalThis.document</c>,
    /// leaving window.onload, custom window properties and event handlers from the
    /// prior page in place. That let a page set attacker-controlled state, trigger a
    /// navigation, and then run code in the next document's context.
    /// </remarks>
    internal void InitJs()
    {
        // InitJs is also the new-document path. Only ResumeJs takes these ids out
        // before entering here and restores them after the same DomTree is
        // installed; a navigation must never inherit ids from a suspended prior
        // document whose allocator may reuse them.
        _suspendedStartedScriptIds.Clear();
        _suspendedCdpObjectState = new CdpObjectState();
        _pendingFrameWork.Clear();
        if (Js is not null)
        {
            // Every frame realm holds a V8 handle into this isolate, so the frames of
            // the outgoing document must go before the runtime does.
            Frames.Clear();
            Js = null;
        }

        // Thread the BrowserContext's proxy through to the ES-module loader and
        // op_fetch_url so dynamic imports and JS fetch() honour the configured
        // upstream proxy. A null proxy is a direct connection.
        var rt = ObscuraJsRuntime.WithBaseUrlAndProxy(UrlString(), Context.ProxyUrl);
        rt.SetUrl(UrlString());
        rt.SetEncoding(Encoding);
        rt.SetTitle(Title);
        rt.SetReferrer(Referrer);

        rt.SetUserAgent(HttpClient.UserAgent);
        rt.SetPlatform(Context.Platform, Context.UaPlatform, Context.UaPlatformVersion);

        if (PageHelpers.EnvGeolocation() is { } geolocation)
        {
            rt.SetGeolocation(geolocation.Latitude, geolocation.Longitude);
        }
        rt.SetViewport(Viewport.Width, Viewport.Height);
        rt.SetScreenSizeOverride(
            _screenSizeOverride is { } screen ? ((double)screen.Width, (double)screen.Height) : null,
            _screenMetricsEmulated);

        rt.SetCookieJar(Context.CookieJar);
        rt.SetHttpClient(HttpClient);
        rt.SetCallbacks(_callbacks);
        rt.SetBlockedUrls(BlockedUrlPatterns);

        if (_interceptTx is { } sink)
        {
            rt.SetInterceptSink(sink);
        }
        // Re-apply InterceptEnabled: EnableInterception()/EnableIntercept() called
        // before the first navigation sets this on the Page while the runtime does
        // not exist yet, so a new runtime would otherwise start with interception
        // disabled and op_fetch_url would never intercept.
        rt.SetInterceptEnabled(InterceptEnabled);
        rt.SetRuntimeEventsEnabled(_runtimeEventsEnabled);

        if (Dom is { } dom)
        {
            Dom = null;
            rt.SetDom(dom);
        }

        rt.RunPageInit();
        TryExecute(rt, "<device-metrics>", DevicePixelRatioScript());

        Js = rt;
    }

    /// <summary>
    /// Resolve the document base URL per the HTML spec, falling back to
    /// <see cref="Url"/> when no <c>&lt;base href&gt;</c> exists.
    /// </summary>
    internal UrlRecord? ResolveBaseUrl()
    {
        if (Url is not { } documentUrl)
        {
            return null;
        }
        string? baseHref = Js?.WithDom(dom =>
        {
            NodeId? node = dom.TryQuerySelector("base[href]", out NodeId? found, out _) ? found : null;
            return node is { } id ? dom.GetNode(id)?.GetAttribute("href") : null;
        });
        return baseHref is null ? documentUrl : PageUrl.TryJoin(documentUrl, baseHref);
    }

    public string UrlString() => Url?.Href ?? "about:blank";

    public T? WithDom<T>(Func<DomTree, T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Js is { } js)
        {
            return js.WithDom(body);
        }
        return Dom is { } dom ? body(dom) : default;
    }

    /// <summary>
    /// Append the current URL to the history stack, truncating any forward entries
    /// past the cursor (matching Chrome: navigating after a goBack clobbers the
    /// forward history).
    /// </summary>
    public void PushHistory(string url)
    {
        if (url.Length == 0)
        {
            return;
        }
        // Don't dupe consecutive entries (Page.reload would otherwise pile up).
        if (HistoryIndex < History.Count
            && string.Equals(History[HistoryIndex], url, StringComparison.Ordinal))
        {
            return;
        }
        if (History.Count != 0 && HistoryIndex < History.Count - 1)
        {
            History.RemoveRange(HistoryIndex + 1, History.Count - HistoryIndex - 1);
        }
        History.Add(url);
        HistoryIndex = History.Count - 1;
    }

    /// <summary>
    /// Move the history cursor without re-navigating; used by
    /// <c>Page.navigateToHistoryEntry</c>, which then drives the actual fetch.
    /// </summary>
    public void SetHistoryIndex(int index)
    {
        if (index >= 0 && index < History.Count)
        {
            HistoryIndex = index;
        }
    }

    /// <summary>
    /// Fork-only: adopt a URL the page routed to itself, without fetching anything.
    /// </summary>
    /// <remarks>
    /// A single page app answers a click by calling <c>history.pushState</c> and
    /// rendering the next view in place. <c>bootstrap.js</c> tracks that in
    /// <c>__virtualUrl</c> so <c>location.href</c> reads correctly, but nothing on
    /// the host side looked at it, so the page had moved on while
    /// <c>page.url()</c> still reported the old document. Returns whether the URL
    /// changed.
    /// </remarks>
    public bool SyncVirtualUrl()
    {
        if (Js is not { } js)
        {
            return false;
        }
        JsonNode? value;
        try
        {
            value = js.Evaluate("globalThis.__virtualUrl || ''");
        }
        catch (JsRuntimeException)
        {
            return false;
        }
        if (value?.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return false;
        }
        string virtualUrl = value.GetValue<string>();
        if (virtualUrl.Length == 0 || PageUrl.TryParse(virtualUrl) is not { } parsed)
        {
            return false;
        }
        if (Url is { } current && string.Equals(current.Href, parsed.Href, StringComparison.Ordinal))
        {
            return false;
        }
        Url = parsed;
        PushHistory(UrlString());
        return true;
    }

    /// <summary>Runs a host script, swallowing a page-level failure as Rust's <c>let _ =</c> does.</summary>
    internal static void TryExecute(ObscuraJsRuntime js, string name, string source)
    {
        try
        {
            js.ExecuteScript(name, source);
        }
        catch (JsRuntimeException)
        {
            // A broken page must degrade, never throw out of navigation.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _pendingFrameWork.Clear();
        Frames.Clear();
        Js = null;
    }
}
