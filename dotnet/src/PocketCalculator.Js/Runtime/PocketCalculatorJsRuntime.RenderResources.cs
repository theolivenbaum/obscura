using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;
using PocketCalculator.Render;

namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Renderer resources loaded through the page transport (upstream 97ff86d).
/// </summary>
/// <remarks>
/// <para>
/// A runtime owned by a page never lets layout or paint open a synchronous request: the
/// compatibility loader (<c>ImageAgent</c>) bypasses the page's SSRF policy, tracker
/// blocklist, cookies, proxy, interception and URL blocking, and pins V8 for the full
/// latency of every unknown asset. Such runtimes keep their renderer cache cache-only.
/// A miss is recorded by the cache, taken here, loaded on the page transport under one
/// page-wide concurrency limit, and applied back on the runtime's own thread at its
/// event-loop turns, its promise waits and around protocol commands, fenced by
/// <c>DocumentGeneration</c>.
/// </para>
/// <para>
/// Standalone runtimes with no transport keep the compatibility loader, as upstream does.
/// </para>
/// </remarks>
public sealed partial class PocketCalculatorJsRuntime
{
    /// <summary>
    /// Whether a renderer miss may be loaded for this document: http(s) always, and
    /// <c>file:</c> only from a <c>file:</c> document (Rust
    /// <c>page_render_resource_url_allowed</c>). The transport applies SSRF and the
    /// tracker blocklist itself.
    /// </summary>
    internal static bool PageRenderResourceUrlAllowed(string documentUrl, string resourceUrl)
    {
        if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out Uri? resource))
        {
            return false;
        }

        return resource.Scheme switch
        {
            "http" or "https" => true,
            "file" => Uri.TryCreate(documentUrl, UriKind.Absolute, out Uri? document)
                && string.Equals(document.Scheme, "file", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>Whether a page transport (plain or available stealth client) is installed.</summary>
    public bool HasPageTransport => HasTransport(State);

    internal static bool HasTransport(PocketCalculatorState state) =>
        state.HttpClient is not null || state.StealthClient is { IsAvailable: true };

    /// <summary>
    /// A renderer cache for a new document: cache-only when a page transport is installed.
    /// Rust <c>fresh_render_resources</c>.
    /// </summary>
    internal static RenderResourceCache FreshRenderResources(PocketCalculatorState state)
    {
        RenderResourceCache cache = RenderResourceCache.Default();
        if (HasTransport(state))
        {
            cache.SetSyncLoadingEnabled(false);
        }

        return cache;
    }

    /// <summary>
    /// Whether layout and paint may still open synchronous compatibility requests. False
    /// for every runtime owned by a page transport.
    /// </summary>
    public bool RenderResourceSyncLoadingEnabled => State.RenderResources.SyncLoadingEnabled;

    /// <summary>
    /// <c>Fetch.enable</c> URL patterns of the owning page. Renderer resource loads
    /// honour them while interception is enabled, like the page's own subresources.
    /// </summary>
    public void SetInterceptBlockPatterns(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        State.InterceptBlockPatterns.Clear();
        State.InterceptBlockPatterns.AddRange(patterns);
    }

    /// <summary>
    /// Resources cache-only layout or paint asked for and did not have, ready for the page
    /// transport: blocked or disallowed URLs are remembered as missing instead of
    /// returned, URLs already loading are skipped, the rest are marked in flight. Cheap
    /// when nothing was missed.
    /// </summary>
    public IReadOnlyList<RenderResourceMiss> TakeRenderResourceRequests()
    {
        PocketCalculatorState state = State;
        if (!state.RenderResources.HasSyncMisses)
        {
            return [];
        }

        IReadOnlyList<RenderResourceMiss> misses = state.RenderResources.TakeSyncMisses();
        RenderResourceLoads loads = state.RenderResourceLoads;
        List<RenderResourceMiss> requests = [];
        foreach (RenderResourceMiss miss in misses)
        {
            // Same policy as Page.ShouldBlockUrl: Network.setBlockedURLs patterns, and
            // while Fetch.enable is active also its patterns.
            bool blocked = MatchesAnyPattern(state.BlockedUrls, miss.Url)
                || (state.InterceptEnabled && MatchesAnyPattern(state.InterceptBlockPatterns, miss.Url));
            if (blocked || !PageRenderResourceUrlAllowed(state.Url, miss.Url))
            {
                SeedMissing(state, miss);
                continue;
            }

            if (loads.InFlight.Count >= RenderResourceLoads.MaxPending)
            {
                continue;
            }

            if (loads.InFlight.Add(miss))
            {
                requests.Add(miss);
            }
        }

        return requests;
    }

    /// <summary>
    /// Mark resources the page discovered itself (the navigation warmup scan) as loading,
    /// returning the ones that were not already in flight, within the page-wide bound.
    /// </summary>
    public IReadOnlyList<RenderResourceMiss> MarkRenderResourcesInFlight(IEnumerable<RenderResourceMiss> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        HashSet<RenderResourceMiss> inFlight = State.RenderResourceLoads.InFlight;
        int available = Math.Max(0, RenderResourceLoads.MaxPending - inFlight.Count);
        List<RenderResourceMiss> marked = [];
        foreach (RenderResourceMiss candidate in candidates)
        {
            if (marked.Count >= available)
            {
                break;
            }

            if (inFlight.Add(candidate))
            {
                marked.Add(candidate);
            }
        }

        return marked;
    }

    /// <summary>
    /// Abort every background load of this document and discard results that already
    /// arrived. The page calls this when the document goes away; <see cref="SetDom"/>,
    /// <see cref="TakeDom"/> and disposal do the same.
    /// </summary>
    public void AbandonRenderResources()
    {
        PocketCalculatorState state = State;
        state.RenderResourceLoads.Retire();
        state.RenderResourceLoads = new RenderResourceLoads();
    }

    /// <summary>
    /// Start page-transport loads for <paramref name="requests"/> (already marked in
    /// flight). They keep the transport's cookies, proxy, SSRF policy, tracker blocklist,
    /// callbacks, CORS and response limits, share the page-wide concurrency limit, and
    /// deliver into this document's result set. Returns the number started.
    /// </summary>
    public int StartRenderResourceLoads(IReadOnlyList<RenderResourceMiss> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return 0;
        }

        PocketCalculatorState state = State;
        RenderResourceLoads loads = state.RenderResourceLoads;
        PocketCalculatorHttpClient? httpClient = state.HttpClient;
        IStealthHttpClient? stealthClient = state.StealthClient is { IsAvailable: true } stealth ? stealth : null;
        if ((httpClient is null && stealthClient is null)
            || !Uri.TryCreate(state.Url, UriKind.Absolute, out Uri? initiator))
        {
            // Nothing can be loaded; forget the in-flight marks so a later scan may retry.
            foreach (RenderResourceMiss request in requests)
            {
                loads.InFlight.Remove(request);
            }

            return 0;
        }

        CallbackRegistry? callbacks = state.Callbacks;
        ulong generation = state.DocumentGeneration;
        SemaphoreSlim limiter = state.RenderResourceLimiter;
        ReferrerPolicy documentPolicy = StateHelpers.DocumentReferrerPolicy(state);
        Dictionary<string, ReferrerPolicy>? imagePolicies = ImageReferrerPolicies(state);
        foreach (RenderResourceMiss request in requests)
        {
            // An <img>'s own referrerpolicy; else, for a URL an external sheet named, that
            // sheet as referrer under its policy; else the document under its policy.
            Uri? referrer = null;
            ReferrerPolicy policy;
            if (!request.IsFont && imagePolicies is not null
                && imagePolicies.TryGetValue(request.Url, out ReferrerPolicy own))
            {
                policy = own;
            }
            else if (state.CssSubresourceReferrers.Find(generation, request.Url) is { } css)
            {
                referrer = css.Sheet;
                policy = css.Policy;
            }
            else
            {
                policy = documentPolicy;
            }
            _ = LoadRenderResourceAsync(
                request, initiator, httpClient, stealthClient, callbacks, generation, limiter, loads, policy, referrer);
        }

        return requests.Count;
    }

    /// <summary>
    /// The <c>referrerpolicy</c> of each <c>&lt;img&gt;</c> that has a valid one, by its
    /// resolved <c>src</c> without fragment; null when none has. A layout miss carries only
    /// a URL, so this is how an image's own policy reaches its load. Port addition.
    /// </summary>
    private static Dictionary<string, ReferrerPolicy>? ImageReferrerPolicies(PocketCalculatorState state)
    {
        if (state.Dom is not { } dom
            || !dom.TryQuerySelectorAll("img[referrerpolicy]", out List<NodeId> images, out _)
            || images.Count == 0
            || UrlRecord.Parse(StateHelpers.DocumentBaseUrlMemoized(state) ?? state.Url) is not { } baseUrl)
        {
            return null;
        }

        Dictionary<string, ReferrerPolicy>? policies = null;
        foreach (NodeId image in images)
        {
            if (StateHelpers.ElementReferrerPolicy(dom, image) is { } policy
                && dom.GetNode(image)?.GetAttribute("src") is { Length: > 0 } src
                && baseUrl.Join(src.Trim()) is { } resolved)
            {
                string href = resolved.Href;
                int hash = href.IndexOf('#', StringComparison.Ordinal);
                (policies ??= new Dictionary<string, ReferrerPolicy>(StringComparer.Ordinal))[
                    hash < 0 ? href : href[..hash]] = policy;
            }
        }

        return policies;
    }

    private static async Task LoadRenderResourceAsync(
        RenderResourceMiss request,
        Uri initiator,
        PocketCalculatorHttpClient? httpClient,
        IStealthHttpClient? stealthClient,
        CallbackRegistry? callbacks,
        ulong generation,
        SemaphoreSlim limiter,
        RenderResourceLoads loads,
        ReferrerPolicy referrerPolicy = ReferrerPolicies.Default,
        Uri? referrer = null)
    {
        Response? response = null;
        double startedAt = 0;
        bool acquired = false;
        CancellationToken token = loads.Token;
        try
        {
            // Off the caller's thread before anything can block: every load of this page
            // waits for the same permits, so repeated scans cannot multiply the rate.
            await Task.Yield();
            await limiter.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            startedAt = PerformanceOps.UnixMilliseconds();
            if (Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? url))
            {
                ResourceRequest resourceRequest = ResourceRequest.Subresource(
                    request.IsFont ? ResourceType.Font : ResourceType.Image,
                    initiator);
                resourceRequest.ReferrerPolicy = referrerPolicy;
                resourceRequest.Referrer = referrer;
                switch (request.Profile)
                {
                    case ImageRequestProfile.CorsSameOrigin:
                        resourceRequest.Mode = RequestMode.Cors;
                        resourceRequest.Credentials = RequestCredentials.SameOrigin;
                        break;
                    case ImageRequestProfile.CorsInclude:
                        resourceRequest.Mode = RequestMode.Cors;
                        resourceRequest.Credentials = RequestCredentials.Include;
                        break;
                }

                response = stealthClient is not null
                    ? await stealthClient
                        .FetchResourceWithCallbacksAsync(url, resourceRequest, callbacks, token)
                        .ConfigureAwait(false)
                    : await httpClient!
                        .FetchResourceWithCallbacksAsync(url, resourceRequest, callbacks, token)
                        .ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A failed, blocked or cancelled load settles as missing; one failure must
            // not strand the in-flight entry, or waiters would keep waiting.
            response = null;
        }
        finally
        {
            if (acquired)
            {
                limiter.Release();
            }
        }

        loads.Deliver(new RenderResourceLoad(
            generation, request, response, startedAt, PerformanceOps.UnixMilliseconds()));
    }

    /// <summary>
    /// One service step: apply every finished load, then start loads for everything
    /// layout or paint missed since the last step. Runs at every JavaScript task and
    /// event-loop turn of this runtime, so a script that creates a miss while a command
    /// is waiting still gets its bytes. Returns the number of loads that stored bytes.
    /// </summary>
    public int ServiceRenderResources()
    {
        PocketCalculatorState state = State;
        if (!state.RenderResourceLoads.HasResults && !state.RenderResources.HasSyncMisses)
        {
            return 0;
        }

        int loaded = ApplyRenderResourceResults();
        if (!HasTransport(state))
        {
            // A standalone runtime only records misses while a capture holds its
            // compatibility loader off; the next synchronous layout loads them itself,
            // so they are neither loaded here nor remembered as missing.
            if (state.RenderResources.HasSyncMisses)
            {
                state.RenderResources.TakeSyncMisses();
            }

            return loaded;
        }

        StartRenderResourceLoads(TakeRenderResourceRequests());
        return loaded;
    }

    /// <summary>Whether page-transport loads are still running for this document.</summary>
    public bool HasPendingRenderResources => State.RenderResourceLoads.InFlight.Count != 0;

    /// <summary>
    /// Wait until a page-transport load of this document finishes, or the timeout passes.
    /// True when one did; apply it with <see cref="ApplyRenderResourceResults"/>.
    /// </summary>
    public Task<bool> WaitForRenderResourceLoadAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        State.RenderResourceLoads.WaitAsync(timeout, cancellationToken);

    /// <summary>
    /// Apply every finished page-transport load without waiting. Returns the number of
    /// loads that stored usable bytes.
    /// </summary>
    public int ApplyRenderResourceResults()
    {
        PocketCalculatorState state = State;
        RenderResourceLoads loads = state.RenderResourceLoads;
        int loaded = 0;
        while (loads.TryTake(out RenderResourceLoad load))
        {
            if (load.Generation != state.DocumentGeneration)
            {
                // A previous document's response: no seed, no event, and it must not
                // clear a same-URL request of the current document.
                continue;
            }

            loads.InFlight.Remove(load.Request);
            byte[]? bytes = load.Response is { Status: >= 200 and < 300 } ok ? ok.Body : null;
            if (bytes is not null)
            {
                loaded++;
            }

            if (load.Request.Profile is { } profile)
            {
                SeedRenderImageResource(load.Request.Url, profile, bytes);
            }
            else
            {
                SeedRenderResource(load.Request.Url, bytes);
            }

            if (load.Response is { } response && loads.Events.Count < RenderResourceLoads.MaxEvents)
            {
                loads.Events.Add(new RenderResourceEvent(
                    load.Request.IsFont, response, load.StartedAtUnixMs, load.EndedAtUnixMs));
            }
        }

        return loaded;
    }

    /// <summary>Applied transport responses the page has not reported as Network events yet.</summary>
    public IReadOnlyList<RenderResourceEvent> TakeRenderResourceEvents()
    {
        List<RenderResourceEvent> events = State.RenderResourceLoads.Events;
        if (events.Count == 0)
        {
            return [];
        }

        RenderResourceEvent[] taken = [.. events];
        events.Clear();
        return taken;
    }

    private static bool MatchesAnyPattern(List<string> patterns, string url)
    {
        foreach (string pattern in patterns)
        {
            if (FetchOps.GlobMatch(pattern, url))
            {
                return true;
            }
        }

        return false;
    }

    private static void SeedMissing(PocketCalculatorState state, RenderResourceMiss miss)
    {
        if (miss.Profile is { } profile)
        {
            state.RenderResources.SeedImageMissing(miss.Url, profile);
        }
        else
        {
            state.RenderResources.SeedMissing(miss.Url);
        }
    }
}
