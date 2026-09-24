using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;

namespace PocketCalculator.Browser;

/// <summary>What a URL change was, so a CDP client is told the truth about the document.</summary>
/// <remarks>
/// CDP draws a hard line here: <c>Page.frameNavigated</c> announces a new document and every
/// client (Puppeteer, Playwright, chromiumoxide) retires the frame's execution contexts when
/// it arrives. A History API or fragment-only URL change keeps the document, and is
/// <c>Page.navigatedWithinDocument</c> instead.
/// </remarks>
public enum PageNavigationKind
{
    /// <summary>The URL did not move.</summary>
    None,

    /// <summary>The URL moved without fetching a document; the realm and its contexts survive.</summary>
    SameDocument,

    /// <summary>A document was fetched and replaced; the old contexts are gone.</summary>
    CrossDocument,
}

/// <summary>The outcome of draining a page's pending navigation.</summary>
/// <param name="Kind">Whether anything moved, and whether the document survived.</param>
/// <param name="NavigationType">
/// The CDP <c>navigationType</c> a same-document change is reported with.
/// </param>
public readonly record struct PageNavigationOutcome(PageNavigationKind Kind, string NavigationType)
{
    /// <summary>A <c>history.pushState</c> / <c>replaceState</c> URL change.</summary>
    public const string HistoryApiType = "historyApi";

    /// <summary>A change to nothing but the fragment.</summary>
    public const string FragmentType = "fragment";

    /// <summary>Anything else, including a real document navigation.</summary>
    public const string OtherType = "other";

    /// <summary>Nothing moved.</summary>
    public static PageNavigationOutcome None => new(PageNavigationKind.None, OtherType);

    /// <summary>A document was fetched and replaced.</summary>
    public static PageNavigationOutcome CrossDocument =>
        new(PageNavigationKind.CrossDocument, OtherType);

    /// <summary>A URL change the document survived, reported as <paramref name="navigationType"/>.</summary>
    public static PageNavigationOutcome SameDocument(string navigationType) =>
        new(PageNavigationKind.SameDocument, navigationType);

    /// <summary>Whether the URL moved at all.</summary>
    public bool Navigated => Kind != PageNavigationKind.None;

    /// <summary>Whether the URL moved without replacing the document.</summary>
    public bool IsSameDocument => Kind == PageNavigationKind.SameDocument;
}

/// <summary>
/// A referrer an automation client named for a browser-initiated navigation (CDP
/// <c>Page.navigate</c>). <see cref="Url"/> null means none. Port addition: Rust ignores
/// both parameters.
/// </summary>
public sealed record ClientReferrer(Uri? Url, ReferrerPolicy Policy);

public sealed partial class Page
{
    public Task NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        NavigateWithWaitAsync(url, WaitUntil.Load, cancellationToken);

    public Task NavigateWithWaitAsync(
        string url,
        WaitUntil waitUntil,
        CancellationToken cancellationToken = default) =>
        NavigateWithWaitPostAsync(url, waitUntil, "GET", string.Empty, cancellationToken);

    /// <summary>
    /// Navigate with an explicit method and body, bounded by the page's end-to-end
    /// navigation deadline.
    /// </summary>
    /// <remarks>
    /// Without the ceiling a slow primary fetch or a runaway settle loop can hold the
    /// V8 lock for arbitrarily long, wedging every other in-flight CDP request
    /// because the dispatcher holds the lock across the whole handler. Override with
    /// <c>POCKETCALCULATOR_NAV_TIMEOUT_MS</c>, or set a page-scoped deadline when the
    /// automation request already has an explicit timeout.
    /// </remarks>
    public Task NavigateWithWaitPostAsync(
        string url,
        WaitUntil waitUntil,
        string method,
        string body,
        CancellationToken cancellationToken = default) =>
        NavigateWithWaitPostAsync(url, waitUntil, method, body, null, cancellationToken);

    /// <summary>
    /// <see cref="NavigateWithWaitPostAsync(string, WaitUntil, string, string, CancellationToken)"/>
    /// for a navigation a document started. <paramref name="initiator"/> is the queued
    /// navigation as <c>op_navigate</c> recorded it; null is a browser-initiated one.
    /// </summary>
    public Task NavigateWithWaitPostAsync(
        string url,
        WaitUntil waitUntil,
        string method,
        string body,
        PendingNavigation? initiator,
        CancellationToken cancellationToken = default) =>
        NavigateWithWaitPostAsync(url, waitUntil, method, body, initiator, null, cancellationToken);

    /// <summary>
    /// <see cref="NavigateWithWaitPostAsync(string, WaitUntil, string, string, PendingNavigation?, CancellationToken)"/>
    /// with the referrer a client named (CDP <c>Page.navigate</c> <c>referrer</c> and
    /// <c>referrerPolicy</c>). It changes only the Referer and <c>document.referrer</c>: the
    /// navigation stays browser-initiated, as in Chromium 141. Ignored for a page-initiated
    /// navigation.
    /// </summary>
    public async Task NavigateWithWaitPostAsync(
        string url,
        WaitUntil waitUntil,
        string method,
        string body,
        PendingNavigation? initiator,
        ClientReferrer? clientReferrer,
        CancellationToken cancellationToken = default)
    {
        // A document may not navigate from a non-file: URL into file: (SECURITY.md H2),
        // however the navigation reached here. The page is left as it was.
        if (NavigationPolicy.RefusesPageInitiated(initiator, url))
        {
            throw PageException.Network(NavigationPolicy.LocalResourceRefusal(url));
        }

        TimeSpan navTimeout = NavigationTimeout;
        ulong navTimeoutMs = (ulong)Math.Max(0.0, navTimeout.TotalMilliseconds);

        using var deadline = new CancellationTokenSource(navTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, cancellationToken);
        ResourceRequest profile = NavigationProfile(initiator);
        if (initiator is null && clientReferrer is not null)
        {
            profile = profile with { Referrer = clientReferrer.Url, ReferrerPolicy = clientReferrer.Policy };
        }
        string referrer = DocumentReferrer(profile, url);
        try
        {
            await NavigateWithWaitPostInnerAsync(url, waitUntil, method, body, referrer, profile, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Lifecycle = LifecycleState.Failed;
            throw PageException.Network(
                $"navigation exceeded {navTimeoutMs.ToString(CultureInfo.InvariantCulture)}ms deadline");
        }
        PushHistory(UrlString());
    }

    private async Task NavigateWithWaitPostInnerAsync(
        string urlString,
        WaitUntil waitUntil,
        string method,
        string body,
        string initialReferrer,
        ResourceRequest initialProfile,
        CancellationToken cancellationToken)
    {
        string currentUrl = urlString;
        string currentMethod = method;
        string currentBody = body;
        string documentReferrer = initialReferrer;
        ResourceRequest profile = initialProfile;
        int chainLimit = NavigationChainLimit;
        for (int chain = 0; chain < chainLimit; chain++)
        {
            await NavigateSingleAsync(
                currentUrl,
                waitUntil,
                currentMethod,
                currentBody,
                documentReferrer,
                profile,
                cancellationToken).ConfigureAwait(false);

            if (TakePendingNavigation() is not { } next)
            {
                break;
            }
            (string nextUrl, string nextMethod, string nextBody) = next;
            // The loaded document queued this one (onload, a script, a form), so it is
            // page-initiated, whoever started the navigation that loaded it.
            profile = NavigationProfile(next);
            if (PageHelpers.CrossSchemeToFile(currentUrl, nextUrl))
            {
                // SOP gate. A web page must not be able to drive a navigation to
                // file:// and then read the loaded document. Without this an http(s)
                // page sets window.onload, assigns location.href = "file:..." and
                // harvests document.body from a local file once it loads.
                break;
            }
            documentReferrer = DocumentReferrer(profile, nextUrl);
            currentUrl = nextUrl;
            currentMethod = nextMethod;
            currentBody = nextBody;
            if (chain + 1 == chainLimit)
            {
                // Hit the cap and the page still wants to keep chaining. Surface that
                // as an error instead of returning success so callers can distinguish
                // a successful load from a navigation storm.
                throw PageException.TooManyClientNavigations(chainLimit);
            }
        }
    }

    private async Task NavigateSingleAsync(
        string urlString,
        WaitUntil waitUntil,
        string method,
        string body,
        string referrer,
        ResourceRequest profile,
        CancellationToken cancellationToken)
    {
        UrlRecord? parsed = PageUrl.TryParse(urlString, out UrlParseError parseError);
        if (parsed is null)
        {
            // Rust reports `e.to_string()` of the url crate's ParseError, so every reason
            // is distinct: "empty host", "invalid port number", "invalid IPv6 address".
            // The port used to hardcode the relative-reference reason for all of them.
            throw PageException.InvalidUrl(parseError.Message());
        }
        UrlRecord url = parsed;

        // The previous document's background render-resource loads end with it.
        RetireRenderResources();
        Lifecycle = LifecycleState.Loading;
        Referrer = referrer;
        ReferrerPolicyHeader = null;
        Url = url;
        NetworkEvents.Clear();

        // The request ids of the outgoing document go with the events that named
        // them, so a body still held for one can never be asked for again. Chromium
        // discards them when the navigation commits for the same reason.
        //
        // Deviation from crates/obscura-browser/src/page.rs, which clears
        // `response_bodies` only from Network.clearBrowserCache: there the buffer
        // grows by a document's worth of bodies on every navigation until it hits
        // its 128-entry cap, which on a script-heavy site is hundreds of megabytes
        // of dead response text per page.
        ClearResponseBodies();

        if (Context.ObeyRobots && url.Scheme is "http" or "https")
        {
            string origin = PageUrl.AsciiOrigin(url);
            if (!Context.RobotsCache.Contains(origin))
            {
                string robotsBody = string.Empty;
                {
                    UrlRecord robotsUrl = PageUrl.RobotsUrl(url);
                    try
                    {
                        Response robots = await HttpClient
                            .FetchWithCallbacksAsync(NetUrl.From(robotsUrl), _callbacks, cancellationToken)
                            .ConfigureAwait(false);
                        if (robots.Status == 200)
                        {
                            robotsBody = System.Text.Encoding.UTF8.GetString(robots.Body);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        robotsBody = string.Empty;
                    }
                }
                Context.RobotsCache.ParseAndStore(origin, robotsBody, Context.UserAgent);
            }

            if (!Context.RobotsCache.IsAllowed(origin, url.Path))
            {
                Lifecycle = LifecycleState.Failed;
                throw PageException.Network($"Blocked by robots.txt: {url.Href}");
            }
        }

        if (string.Equals(url.Scheme, "about", StringComparison.Ordinal))
        {
            NavigateBlank();
            InitJs();
            // Preloads (Page.addScriptToEvaluateOnNewDocument, the Runtime.addBinding
            // shim) must run on about:blank too: puppeteer's browser.newPage() lands
            // on about:blank and a follow-up exposeFunction is unusable otherwise.
            List<string> preloadSources = [.. _preloadScripts];
            if (Js is { } blankJs)
            {
                foreach (string source in preloadSources)
                {
                    blankJs.ExecuteScriptGuarded("<preload>", source);
                }
            }
            return;
        }

        Response response;
        try
        {
            if (string.Equals(url.Scheme, "data", StringComparison.Ordinal))
            {
                string meta = urlString["data:".Length..];
                int comma = meta.IndexOf(',', StringComparison.Ordinal);
                string head = comma < 0 ? meta : meta[..comma];
                int semi = head.IndexOf(';', StringComparison.Ordinal);
                string contentType = (semi < 0 ? head : head[..semi]) is { Length: > 0 } value
                    ? value
                    : "text/html";
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["content-type"] = contentType,
                };
                response = new Response
                {
                    Url = NetUrl.From(url),
                    Status = 200,
                    Headers = headers,
                    Body = PageHelpers.DecodeDataUri(urlString) ?? [],
                    RedirectedFrom = [],
                };
            }
            else if (string.Equals(method, "POST", StringComparison.Ordinal))
            {
                response = await HttpClient
                    .FetchNavigationAsync(
                        HttpMethod.Post,
                        NetUrl.From(url),
                        System.Text.Encoding.UTF8.GetBytes(body),
                        profile,
                        _callbacks,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                response = await HttpClient
                    .FetchNavigationAsync(HttpMethod.Get, NetUrl.From(url), null, profile, _callbacks, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Lifecycle = LifecycleState.Failed;
            throw PageException.Network(error.Message);
        }

        // Store binary main resources (images, PDFs, octet-stream) base64 so
        // Network.getResponseBody returns intact bytes. A UTF-8-lossy text store
        // corrupts them. Text-like types stay as text.
        bool mainIsBinary = !PageHelpers.IsTextLikeContentType(response.ContentType());
        RecordNetworkEventWithBody(
            url.Href,
            "GET",
            "Document",
            response.Status,
            response.Headers,
            response.Body,
            mainIsBinary);

        if (response.RedirectedFrom.Count != 0)
        {
            Url = NetUrl.To(response.Url);
        }

        ReferrerPolicyHeader = ReferrerPolicies.ParseHeader(response.Header("referrer-policy"));

        // Honor the response charset: HTTP Content-Type, then a <meta charset> sniff
        // in the first 1KB, then UTF-8. Without this every non-UTF-8 page came
        // through as replacement characters.
        (string bodyText, string encodingName) =
            ContentEncoding.DecodeResponseWithName(response.Body, response.ContentType());
        Encoding = encodingName;
        DomTree dom = HtmlParsing.ParseHtml(bodyText);

        Title = dom.TryQuerySelector("title", out NodeId? titleId, out _) && titleId is { } id
            ? dom.TextContent(id)
            : string.Empty;

        Dom = dom;
        InitJs();
        var authorStylesheets = await FetchStylesheetsAsync(cancellationToken).ConfigureAwait(false);

        // Upstream 04418a5: fetched CSS goes into the DomTree store with its origin-clean bit,
        // and nowhere page script can read it. The globalThis.__obscura_css global that used to
        // hold every fetched sheet, cross-origin ones included, is gone.
        if (authorStylesheets.Count != 0 && Js is { } cssJs)
        {
            // DEVIATION from crates/obscura-browser, which runs one script per sheet to insert
            // a synthetic <style>. Chromium 141 inserts no element, so the bytes are recorded
            // against the <link> (or, for an @import, against the importing <style>) and the
            // renderer reads them from there. Several @import rules in one <style> contribute
            // in source order, so they are appended to one entry, which stays origin-clean only
            // if all of them are. See "Known deviations" in todo.md.
            List<(int LinkIndex, string ResponseUrl)> linked = [];
            cssJs.WithDom(dom =>
            {
                foreach ((AuthorStylesheetTarget target, string css, bool originClean, string responseUrl) in authorStylesheets)
                {
                    switch (target)
                    {
                        case AuthorStylesheetTarget.Linked link:
                            dom.AppendExternalStylesheet(link.Node, css, originClean);
                            linked.Add((link.LinkIndex, responseUrl));
                            break;
                        case AuthorStylesheetTarget.InlineImport inline:
                            dom.AppendExternalStylesheet(inline.Node, css, originClean);
                            break;
                    }
                }

                return 0;
            });

            // An @import needs no script: it owns no CSSOM sheet and fires no event. A <link>
            // does both, and its load handler may still change what applies.
            foreach ((int linkIndex, string responseUrl) in linked)
            {
                TryExecuteHost(
                    cssJs,
                    "<fetch_stylesheets>",
                    PageHelpers.LinkedStylesheetLoadScript(linkIndex, responseUrl));
            }
        }

        // DEVIATION from upstream 04418a5, which deletes the page-visible
        // __obscura_registerLinkedStylesheet bridge here. The registration is a host
        // helper (HostScript), so there is no global to remove.

        _documentTimelineOrigin = Stopwatch.GetTimestamp();
        Js?.ResetAnimationTimeline();
        if (Js is { } iframeJs)
        {
            TryExecute(
                iframeJs,
                "<iframe-load>",
                "(function() { var iframes = document.querySelectorAll('iframe[src]');"
                + " for (var i = 0; i < iframes.length; i++) { var src = iframes[i].getAttribute('src');"
                + " if (src && src !== 'about:blank') iframes[i]._loadIframeSrc(src); } })()");
        }

        // Scripts can synchronously flush style/layout through getComputedStyle(),
        // geometry, ResizeObserver or IntersectionObserver. Seed their image/font
        // dependencies concurrently through the page transport first. Otherwise the
        // first CSSOM read falls into the renderer's synchronous resource loader and
        // serial network latency pins V8. Deliberately bounded: navigation should not
        // wait indefinitely for decorative resources.
        await PrepareScreenshotResourcesAsync(
            PageHelpers.EnvUlong("POCKETCALCULATOR_RENDER_RESOURCE_WARMUP_MS", 1_000),
            cancellationToken).ConfigureAwait(false);

        // Spec: DOMContentLoaded fires AFTER parser-blocking scripts run, not before.
        // Skipping ExecuteScripts on the DCL path silently dropped every inline
        // <script>: form listeners never registered, frameworks never bootstrapped,
        // click handlers were no-ops. Scripts run regardless of waitUntil and DCL
        // means "DOM parsed AND scripts executed".
        await ExecuteScriptsAsync(cancellationToken).ConfigureAwait(false);

        // Page scripts and their bounded post-script event-loop pass can create
        // responsive images, inline styles and @font-face rules that did not exist
        // during the parser warmup. Discover them before navigation becomes
        // capture-ready. Known parser resources are filtered by the render cache, so
        // ordinary pages pay only the inexpensive scan on this second pass.
        await PrepareScreenshotResourcesAsync(
            PageHelpers.EnvUlong("POCKETCALCULATOR_RENDER_RESOURCE_POST_SCRIPT_WARMUP_MS", 1_000),
            cancellationToken).ConfigureAwait(false);

        Lifecycle = LifecycleState.DomContentLoaded;

        // Before any waitUntil can return, because the frames belong to the document
        // rather than to one readiness level. Puppeteer and Playwright send
        // Page.navigate with no waitUntil, which lands here and returns on the next
        // line, so building frames further down left every real CDP client seeing a
        // page with no frames at all.
        await BuildDocumentFramesAsync(cancellationToken).ConfigureAwait(false);

        if (waitUntil == WaitUntil.DomContentLoaded)
        {
            return;
        }

        if (Js is { } titleJs)
        {
            try
            {
                if (titleJs.Evaluate("document.title") is { } newTitle
                    && newTitle.GetValueKind() == System.Text.Json.JsonValueKind.String)
                {
                    Title = newTitle.GetValue<string>();
                }
            }
            catch (JsRuntimeException)
            {
                // A page that breaks document.title keeps the parsed one.
            }
        }

        Lifecycle = LifecycleState.Loaded;

        if (waitUntil is WaitUntil.NetworkIdle0 or WaitUntil.NetworkIdle2)
        {
            int threshold = waitUntil == WaitUntil.NetworkIdle2 ? 2 : 0;

            // Same hazard as the post-script settle: a synchronous poll can pin the
            // thread past the 5s network-idle deadline, so arm a watchdog that
            // terminates the isolate ~500ms past it.
            WatchdogToken? netIdleWatchdog = Js?.ArmWatchdog(TimeSpan.FromMilliseconds(5500));
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            DateTime? idleSince = null;

            while (true)
            {
                int active = HttpClient.ActiveRequests;
                DateTime now = DateTime.UtcNow;

                if (active <= threshold)
                {
                    idleSince ??= now;
                    if (now - idleSince.Value >= TimeSpan.FromMilliseconds(500))
                    {
                        break;
                    }
                }
                else
                {
                    idleSince = null;
                }

                if (now >= deadline)
                {
                    break;
                }

                if (Js is { } idleJs)
                {
                    try
                    {
                        await idleJs.RunEventLoopBoundedAsync(50).ConfigureAwait(false);
                    }
                    catch (JsRuntimeException)
                    {
                        // A page-local event-loop error must not fail the navigation.
                    }
                }
                else
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }

            if (netIdleWatchdog is { } token)
            {
                Js?.DisarmWatchdog(token);
            }
            Lifecycle = LifecycleState.NetworkIdle;
        }
    }

    /// <summary>
    /// Build the child frames of the document that just loaded.
    /// </summary>
    /// <remarks>
    /// Loading a document includes loading the frames in it, so this belongs to
    /// navigation rather than to settle: a CDP client that only navigates would
    /// otherwise be told the page has no frames. A frame's document is fetched by
    /// page script, so it arrives from the event loop rather than being ready the
    /// moment parsing ends. Pages without an iframe skip the pumping entirely and pay
    /// one native selector query.
    /// </remarks>
    private async Task BuildDocumentFramesAsync(CancellationToken cancellationToken)
    {
        // How many rounds of "attach a frame, let it start its own" to follow. A
        // frame can add a frame, so this needs a bound rather than a loop until
        // quiet: a page that adds one on every turn would never finish.
        const int Rounds = 8;
        const ulong RoundMs = 50;

        bool hasIframe = WithDom(dom => dom.TryQuerySelector("iframe", out NodeId? found, out _) && found is not null);
        if (!hasIframe)
        {
            return;
        }

        for (int round = 0; round < Rounds; round++)
        {
            if (Js is { } js)
            {
                try
                {
                    await js.RunEventLoopBoundedAsync(RoundMs).ConfigureAwait(false);
                }
                catch (JsRuntimeException)
                {
                    // A page-local error does not stop frame construction.
                }
            }
            if (!await AdvanceFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    public void NavigateBlank()
    {
        _pendingFrameWork.Clear();
        Frames.Clear();
        Js = null;
        Url = PageUrl.TryParse("about:blank")!;
        Dom = HtmlParsing.ParseHtml("<!DOCTYPE html><html><head></head><body></body></html>");
        Title = string.Empty;
        Lifecycle = LifecycleState.Loaded;
        _documentTimelineOrigin = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Drive the JS event loop after navigation so deferred work can run: pending
    /// timers, queued microtasks, in-flight fetches and completion callbacks.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the loop goes idle, or after <paramref name="maxMs"/>.
    /// Without this the page is observed exactly as it stood at the load event,
    /// before any async work settles, which strands timer-driven tests and dynamic
    /// pages.
    /// </remarks>
    public async Task SettleAsync(ulong maxMs, CancellationToken cancellationToken = default)
    {
        if (maxMs == 0)
        {
            return;
        }
        long settleStarted = Stopwatch.GetTimestamp();
        // Pump, then give any frame document that finished fetching a realm of its
        // own. Attaching one runs its scripts, which can start timers, fetches and
        // further frames, so keep alternating until no new frame appears or the
        // budget is gone.
        while (true)
        {
            ulong elapsed = ElapsedMilliseconds(settleStarted);
            if (elapsed >= maxMs)
            {
                break;
            }
            ulong remaining = maxMs - elapsed;
            if (Js is { } js)
            {
                try
                {
                    if (Environment.GetEnvironmentVariable("POCKETCALCULATOR_STRICT_SETTLE") is not null)
                    {
                        await SettleRuntimeForDurationAsync(js, remaining).ConfigureAwait(false);
                    }
                    else
                    {
                        // A deno_core event loop remains "busy" for any future timer,
                        // including analytics intervals and animation loops which do
                        // not make the page more ready. Require a short window
                        // without observable document/network/script activity
                        // instead. The absolute caller budget and V8 watchdog still
                        // bound both asynchronous work and synchronous microtask
                        // storms.
                        await js.RunEventLoopUntilQuiescentAsync(remaining, 150).ConfigureAwait(false);
                    }
                }
                catch (JsRuntimeException)
                {
                    // Settling degrades rather than throwing on a broken page.
                }
            }
            QueuePendingRenderResources();
            if (!await AdvanceFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        // Timers, fetch completions and framework commits commonly append images or
        // @font-face rules during settling. Seed those resources here so a following
        // capture remains a fast observation of the retained page rather than
        // initiating its own network phase.
        ulong warmupMs = PageHelpers.EnvUlong("POCKETCALCULATOR_RENDER_RESOURCE_SETTLE_WARMUP_MS", 1_000);
        ulong remainingMs = PageHelpers.RemainingSettleResourceWarmupMs(
            maxMs,
            StopwatchElapsed(settleStarted),
            warmupMs);
        if (remainingMs != 0)
        {
            await PrepareScreenshotResourcesAsync(remainingMs, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pump the event loop and retain the full requested wall-clock delay.
    /// </summary>
    /// <remarks>
    /// The CLI uses this for an explicitly supplied <c>--wait</c>; callers asking for
    /// a fixed capture delay should not be silently shortened by adaptive readiness
    /// heuristics.
    /// </remarks>
    public async Task SettleForDurationAsync(ulong durationMs, CancellationToken cancellationToken = default)
    {
        if (durationMs == 0)
        {
            return;
        }
        if (Js is { } js)
        {
            await SettleRuntimeForDurationAsync(js, durationMs).ConfigureAwait(false);
        }
        // A fixed wait must retain its full wall clock, so frames get their realms
        // once at the end instead of being interleaved as in SettleAsync. Their
        // document scripts still run; only their own deferred work is left for a
        // later settle.
        await AdvanceFramesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advance one wake-driven browser task for a continuously owned page.
    /// </summary>
    /// <remarks>
    /// True means the event loop reached full idle; false means one wake/task was
    /// delivered and the owner should offer another turn after servicing any
    /// higher-priority automation commands.
    /// </remarks>
    public async Task<bool> RunAutonomousEventLoopTurnAsync(CancellationToken cancellationToken = default)
    {
        QueuePendingRenderResources();
        bool reachedIdle = Js is { } js
            ? await js.RunAutonomousEventLoopTurnAsync().ConfigureAwait(false)
            : true;
        // Dynamic iframe fetches finish on the page event loop, but their realms must
        // be built by Page between turns. Keep the autonomous CDP pump on the same
        // generic frame path as settle, so a client that stays attached can observe
        // and run child documents as they arrive.
        bool frameWork = await AdvanceFramesAsync(cancellationToken).ConfigureAwait(false);
        // Geometry this turn produced may have missed resources; start their loads now.
        QueuePendingRenderResources();
        if (reachedIdle && !frameWork && Js is { } live && live.HasPendingRenderResources)
        {
            // A finished transport load is page work too: the next layout, paint or
            // lifecycle read must see the real bytes without waiting for a protocol
            // command. Upstream parks on the load's notification inside a select the
            // connection processor cancels when a command arrives; this turn is awaited
            // to completion instead, so the park is capped and the pump stays armed.
            if (await live.WaitForRenderResourceLoadAsync(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false))
            {
                DrainRenderResourceResults();
            }
            return false;
        }
        return reachedIdle && !frameWork;
    }

    private static async Task SettleRuntimeForDurationAsync(PocketCalculatorJsRuntime js, ulong durationMs)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            await js.RunEventLoopForDurationAsync(durationMs).ConfigureAwait(false);
        }
        catch (JsRuntimeException)
        {
            // A page-local event-loop error still owes the caller the full wait.
        }
        TimeSpan requested = TimeSpan.FromMilliseconds(durationMs);
        TimeSpan elapsed = StopwatchElapsed(started);
        if (elapsed < requested)
        {
            await Task.Delay(requested - elapsed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Navigate to <paramref name="url"/> without fetching a document, when it names the
    /// document already loaded and differs from it in nothing but the fragment.
    /// </summary>
    /// <remarks>
    /// An automation client navigating to <c>page.html#/route</c> from <c>page.html</c> is
    /// doing what a user does by clicking an in-page link, and Chromium answers it the same
    /// way: no request, the realm and every <c>window</c> property intact, <c>hashchange</c>
    /// and <c>popstate</c> fired. Refetching instead reboots a single page app on every
    /// route change, which is what a client driving one with <c>goto(base + '#/route')</c>
    /// does on every step.
    /// <para>
    /// The decision and the events belong to <c>bootstrap.js</c>
    /// (the <c>tryFragmentNavigate</c> host helper), which already owns the fragment path for
    /// <c>location.href</c> / <c>assign</c> / <c>replace</c> and the component setters. The
    /// host asks rather than re-deciding, so the two cannot disagree about what counts as a
    /// fragment navigation.
    /// </para>
    /// <para>
    /// Deviation from <c>crates/obscura-cdp/src/domains/page.rs</c>, whose <c>navigate</c>
    /// always fetches. The Rust tree has the same gap; this is a fix against Chromium, not a
    /// port correction.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see cref="PageNavigationOutcome.SameDocument"/> when the navigation was handled
    /// here; <see cref="PageNavigationOutcome.None"/> when the caller must fetch.
    /// </returns>
    public async Task<PageNavigationOutcome> TryNavigateSameDocumentAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (Js is null || Url is null || string.IsNullOrEmpty(url))
        {
            return PageNavigationOutcome.None;
        }

        // A fragment can only be same-document relative to a document that is actually
        // loaded; about:blank has nothing to stay on.
        if (string.Equals(Url.Scheme, "about", StringComparison.Ordinal))
        {
            return PageNavigationOutcome.None;
        }

        // The host checks first that the target differs from the document only in its
        // fragment (port addition, SECURITY.md L10): the shim's answer runs in the page's
        // realm, and a cross-document navigation must never be turned into a no-op there.
        if (Url.Join(url) is not { Fragment: not null } target
            || !string.Equals(
                PageUrl.WithoutFragment(Url).Href, PageUrl.WithoutFragment(target).Href, StringComparison.Ordinal))
        {
            return PageNavigationOutcome.None;
        }

        string literal = System.Text.Json.JsonSerializer.Serialize(url);
        JsonNode? handled;
        try
        {
            // A host helper, not upstream's page-visible __obscura_tryFragmentNavigate.
            handled = EvaluateHost($"__obscura_host.tryFragmentNavigate({literal}, false)");
        }
        catch (JsRuntimeException)
        {
            return PageNavigationOutcome.None;
        }
        if (handled?.GetValueKind() != System.Text.Json.JsonValueKind.True)
        {
            return PageNavigationOutcome.None;
        }

        // bootstrap.js moved the history URL (op_history_url); adopt it host-side so page.url() and every CDP
        // payload that reports it agree with what the page now thinks it is.
        SyncVirtualUrl();

        // Routers answer hashchange/popstate by queueing work. Give that a bounded turn so
        // the navigation is observed after the route has had a chance to render, the way a
        // document navigation already settles before returning.
        if (Js is { } js)
        {
            try
            {
                await js.RunEventLoopBoundedAsync(
                    PageHelpers.EnvUlong("POCKETCALCULATOR_FRAGMENT_NAV_SETTLE_MS", 250)).ConfigureAwait(false);
            }
            catch (JsRuntimeException)
            {
                // A router that throws must not fail the navigation.
            }
        }
        await AdvanceFramesAsync(cancellationToken).ConfigureAwait(false);

        return PageNavigationOutcome.SameDocument(PageNavigationOutcome.FragmentType);
    }

    /// <summary>Whether anything moved; <see cref="ProcessPendingNavigationOutcomeAsync"/> says what.</summary>
    public async Task<bool> ProcessPendingNavigationAsync(CancellationToken cancellationToken = default) =>
        (await ProcessPendingNavigationOutcomeAsync(cancellationToken).ConfigureAwait(false)).Navigated;

    /// <summary>
    /// Drain a pending navigation, answering whether the document was replaced.
    /// </summary>
    /// <remarks>
    /// The caller needs the distinction to pick the CDP event: only a fetched document is a
    /// <c>Page.frameNavigated</c>. A page that routed itself through the History API kept
    /// its document, and saying otherwise destroys the client's execution context.
    /// </remarks>
    public async Task<PageNavigationOutcome> ProcessPendingNavigationOutcomeAsync(
        CancellationToken cancellationToken = default)
    {
        if (TakePendingNavigation() is not { } pending)
        {
            // Fork: a page that routed itself through history has still navigated.
            return SyncVirtualUrl();
        }
        (string url, string method, string body) = pending;
        // Deviation from page.rs, which follows any queued navigation here (SECURITY.md
        // H3): a link or script on a non-file: document cannot take the page to file:.
        // Chromium drops the navigation and the page stays where it is.
        if (NavigationPolicy.RefusesPageInitiated(pending, url))
        {
            return PageNavigationOutcome.None;
        }

        ResourceRequest profile = NavigationProfile(pending);
        string sourceUrl = DocumentReferrer(profile, url);
        TimeSpan navTimeout = NavigationTimeout;
        ulong navTimeoutMs = (ulong)Math.Max(0.0, navTimeout.TotalMilliseconds);
        using var deadline = new CancellationTokenSource(navTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, cancellationToken);
        try
        {
            await NavigateWithWaitPostInnerAsync(url, WaitUntil.Load, method, body, sourceUrl, profile, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Lifecycle = LifecycleState.Failed;
            throw PageException.Network(
                $"navigation exceeded {navTimeoutMs.ToString(CultureInfo.InvariantCulture)}ms deadline");
        }
        PushHistory(UrlString());
        return PageNavigationOutcome.CrossDocument;
    }

    /// <summary>
    /// The fetch profile of a navigation: browser-initiated when nothing queued it, and
    /// otherwise page-initiated from the queuing document's URL.
    /// </summary>
    /// <remarks>
    /// Deviation: upstream fetches every navigation, page-initiated or not, with the
    /// initiator-less browser profile. An initiator URL that does not parse (it is the
    /// realm URL, so this is only defensive) is an opaque origin: cross-site to
    /// everything and no Referer, which is what Chromium gives an about:blank or data:
    /// document too.
    /// </remarks>
    internal static ResourceRequest NavigationProfile(PendingNavigation? initiator)
    {
        if (initiator is null)
        {
            return ResourceRequest.Navigation();
        }

        Uri source = Uri.TryCreate(initiator.Initiator, UriKind.Absolute, out Uri? parsed)
            ? parsed
            : new Uri("about:blank");
        // The link's or the document's referrer policy (port addition).
        return ResourceRequest.PageNavigation(source, initiator.UserActivated) with
        {
            ReferrerPolicy = initiator.ReferrerPolicy,
        };
    }

    /// <summary>
    /// <c>document.referrer</c> for the document a navigation loads: the same
    /// strict-origin-when-cross-origin value the request's Referer header carries, and
    /// empty for a browser-initiated navigation.
    /// </summary>
    /// <remarks>
    /// Under the navigation's referrer policy (port addition): upstream always applies
    /// strict-origin-when-cross-origin.
    /// </remarks>
    private static string DocumentReferrer(ResourceRequest profile, string targetUrl) =>
        profile.Referrer is { } source
            && PageUrl.TryParse(targetUrl) is { } target
            && Uri.TryCreate(target.Href, UriKind.Absolute, out Uri? targetUri)
            ? ReferrerPolicies.Referrer(source, targetUri, profile.ReferrerPolicy ?? ReferrerPolicies.Default) ?? string.Empty
            : string.Empty;

    private static ulong ElapsedMilliseconds(long startTimestamp) =>
        (ulong)Math.Max(0.0, StopwatchElapsed(startTimestamp).TotalMilliseconds);

    private static TimeSpan StopwatchElapsed(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp);
}
