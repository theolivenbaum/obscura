using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PocketCalculator.Net;

/// <summary>
/// obscura's HTTP client (port of <c>crates/obscura-net/src/client.rs</c>).
/// Redirects, cookies, CORS, fetch metadata, the resource cache and the SSRF gate
/// are all owned here rather than delegated to the transport: redirects are
/// followed by hand so every hop is revalidated, and
/// <c>SocketsHttpHandler.UseCookies</c> is off so the jar is the only cookie store.
/// </summary>
public sealed class PocketCalculatorHttpClient : IDisposable
{
    private const int MaxRedirects = 20;
    private const int ResourceCacheMaxEntries = 256;
    private const long ResourceCacheMaxBytes = 64L * 1024 * 1024;

    private readonly System.Threading.Lock _clientLock = new();
    private readonly System.Threading.Lock _stateLock = new();
    private readonly System.Threading.Lock _loaderLock = new();
    private readonly ResourceCache _cache = new();
    private readonly Dictionary<ResourceCacheKey, SharedFetch> _sharedFetches = [];
    private readonly string? _proxyUrl;

    private HttpClient? _client;
    private int _inFlight;
    private string _userAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";
    private string _acceptLanguage = "en-US,en;q=0.9";
    private Dictionary<string, string> _extraHeaders = new(StringComparer.Ordinal);

    /// <summary>Build a client with a fresh cookie jar.</summary>
    public PocketCalculatorHttpClient()
        : this(new CookieJar(), null, false)
    {
    }

    /// <summary>Build a client sharing <paramref name="cookieJar"/>.</summary>
    public PocketCalculatorHttpClient(CookieJar cookieJar)
        : this(cookieJar, null, false)
    {
    }

    /// <summary>Build a client with a cookie jar and an optional upstream proxy.</summary>
    public PocketCalculatorHttpClient(CookieJar cookieJar, string? proxyUrl)
        : this(cookieJar, proxyUrl, false)
    {
    }

    /// <summary>
    /// Build a client with a cookie jar, an optional upstream proxy and the
    /// <c>--allow-private-network</c> escape hatch.
    /// </summary>
    public PocketCalculatorHttpClient(CookieJar cookieJar, string? proxyUrl, bool allowPrivateNetwork)
    {
        CookieJar = cookieJar;
        _proxyUrl = proxyUrl;
        AllowPrivateNetwork = allowPrivateNetwork;
    }

    /// <summary>
    /// This context's HSTS state (SECURITY.md I7). Filled from the
    /// <c>Strict-Transport-Security</c> headers of https responses on this client; an
    /// embedder can also seed it with <see cref="HstsStore.Add"/>. See
    /// <see cref="HstsStore"/> for the rules.
    /// </summary>
    public HstsStore Hsts { get; } = new();

    /// <summary>
    /// Turn off the mixed-content check (<see cref="MixedContent"/>), Chromium's
    /// <c>--allow-running-insecure-content</c>. Defaults to false, or to true when
    /// <c>POCKETCALCULATOR_ALLOW_INSECURE_CONTENT=1</c> is set.
    /// </summary>
    public bool AllowInsecureContent { get; set; } = MixedContent.EnvAllowsInsecureContent();

    /// <summary>The cookie jar every request on this client reads and writes.</summary>
    public CookieJar CookieJar { get; }

    /// <summary>The interceptor consulted before every hop, if any.</summary>
    public IRequestInterceptor? Interceptor { get; set; }

    /// <summary>Per-request timeout. Read once, when the transport is first built.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>When true, requests to hosts on the tracker blocklist return status 0.</summary>
    public bool BlockTrackers { get; set; }

    /// <summary>
    /// When true, the URL gate lets localhost / RFC1918 / link-local addresses
    /// through in addition to the <c>POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK</c> env var. Set
    /// via <c>--allow-private-network</c> on the CLI (issue #33).
    /// </summary>
    public bool AllowPrivateNetwork { get; }

    /// <summary>The User-Agent sent on every request.</summary>
    public string UserAgent
    {
        get { lock (_stateLock) { return _userAgent; } }
    }

    /// <summary>The Accept-Language sent on every request.</summary>
    public string AcceptLanguage
    {
        get { lock (_stateLock) { return _acceptLanguage; } }
    }

    /// <summary>Extra headers merged into every request.</summary>
    public IReadOnlyDictionary<string, string> ExtraHeaders
    {
        get
        {
            lock (_stateLock)
            {
                return new Dictionary<string, string>(_extraHeaders, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Override the User-Agent.</summary>
    public void SetUserAgent(string userAgent)
    {
        lock (_stateLock)
        {
            _userAgent = userAgent;
        }
    }

    /// <summary>Override the Accept-Language.</summary>
    public void SetAcceptLanguage(string acceptLanguage)
    {
        lock (_stateLock)
        {
            _acceptLanguage = acceptLanguage;
        }
    }

    /// <summary>Replace the extra header set.</summary>
    public void SetExtraHeaders(IReadOnlyDictionary<string, string> headers)
    {
        lock (_stateLock)
        {
            _extraHeaders = new Dictionary<string, string>(headers, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Resolves a proxied request's target host so it can be vetted before the proxy
    /// sees it. Tests substitute a resolver; the default asks the system resolver.
    /// </summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> ProxyTargetResolver { get; set; } =
        static (host, token) => Dns.GetHostAddressesAsync(host, token);

    /// <summary>
    /// Read-only accessor for the proxy URL the client was configured with (if any).
    /// Exposed so callers outside this assembly - notably the JS fetch op (#139) -
    /// can route their own requests through the same upstream proxy.
    /// </summary>
    public string? ProxyUrl => _proxyUrl;

    /// <summary>Requests currently on the wire.</summary>
    public int ActiveRequests => Volatile.Read(ref _inFlight);

    /// <summary>True when nothing is on the wire.</summary>
    public bool IsNetworkIdle => ActiveRequests == 0;

    /// <summary>
    /// The transport client owned by this browser context. Scripted fetch/XHR uses
    /// the same pool as navigation instead of a process-global client, keeping its
    /// async network state inside the same ownership boundary as the V8 runtime
    /// (issue #453).
    /// </summary>
    public HttpClient RequestClient => GetClient();

    private HttpClient GetClient()
    {
        if (_client is { } existing)
        {
            return existing;
        }

        lock (_clientLock)
        {
            if (_client is { } raced)
            {
                return raced;
            }

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // Obscura manages cookies through its own CookieJar; the transport
                // must never keep a second store that could shadow it.
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip
                    | DecompressionMethods.Deflate
                    | DecompressionMethods.Brotli,
            };

            // SSRF guard: reject hostnames that resolve to a private/loopback IP.
            var resolver = new SsrfGuardResolver(AllowPrivateNetwork);
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                // Only guard an endpoint the reference would guard. reqwest installs
                // this as a `dns_resolver`, so it runs for a host that needs resolving
                // and never sees an IP-literal endpoint, which reaches the socket with
                // no lookup. A ConnectCallback fires for every connection, so without
                // this the port refused endpoints the reference never inspects - most
                // visibly an HTTPS_PROXY on 127.0.0.1, where every proxied fetch died
                // with "SSRF blocked: '127.0.0.1' resolves to forbidden address" while
                // the reference proxied normally. An IP-literal *target* is still
                // refused: ValidateUrl checks the request URL's host on entry and on
                // every redirect hop, which is where Rust rejects it too.
                var addresses = IPAddress.TryParse(StripIpv6Brackets(host), out var literal)
                    ? [literal]
                    : await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket
                        .ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };

            // Revocation: Chromium does no online OCSP/CRL checks for ordinary
            // certificates (it relies on CRLSets pushed with the browser), and an online
            // check would leak every visited host to the CA and stall on a slow
            // responder. Both validation paths (the platform one here and the
            // SSL_CERT_FILE / SSL_CERT_DIR one below) therefore use NoCheck; this states
            // the SocketsHttpHandler default explicitly so the two cannot drift.
            handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;

            if (CertificateRoots.AnyCertEnvSet())
            {
                handler.SslOptions.RemoteCertificateValidationCallback = ValidateWithConfiguredRoots;
            }

            // Deviation from client.rs (SECURITY.md M2): reqwest, like SocketsHttpHandler,
            // picks up HTTP_PROXY / HTTPS_PROXY from the environment. Behind a proxy the
            // connect-time guard sees only the proxy's address, so an ambient proxy
            // silently switched off the hostname half of the SSRF check. Only a proxy
            // the operator configured (--proxy, POCKETCALCULATOR_PROXY, BrowserConfig)
            // is used, and its targets are vetted by TransportGuardHandler.
            handler.UseProxy = false;
            var proxied = false;
            if (_proxyUrl is not null && Uri.TryCreate(_proxyUrl, UriKind.Absolute, out var proxyUri))
            {
                handler.Proxy = new WebProxy(proxyUri);
                handler.UseProxy = true;
                proxied = true;
            }

            var guard = new TransportGuardHandler(this, proxied, handler);
            _client = new HttpClient(guard, disposeHandler: true) { Timeout = Timeout };
            return _client;
        }
    }

    /// <summary>Whether <see cref="BlockTrackers"/> refuses a request to <paramref name="url"/>.</summary>
    internal bool IsBlockedTracker(Uri url)
    {
        if (!BlockTrackers)
        {
            return false;
        }

        var host = UrlOrigin.Host(url);
        return host.Length != 0 && Blocklist.IsBlocked(host);
    }

    /// <summary>
    /// <see cref="DnsEndPoint.Host"/> carries an IPv6 literal unbracketed on some
    /// paths and bracketed on others; <see cref="IPAddress.TryParse(string, out IPAddress)"/>
    /// only accepts the bare form.
    /// </summary>
    private static string StripIpv6Brackets(string host) =>
        host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    /// <summary>
    /// Additive trust: the platform roots still apply, and anything they reject gets
    /// a second chance against the SSL_CERT_FILE / SSL_CERT_DIR store. This mirrors
    /// reqwest's <c>add_root_certificate</c>, which adds to rather than replaces the
    /// bundled roots.
    /// </summary>
    private static bool ValidateWithConfiguredRoots(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (certificate is null
            || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0
            || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return false;
        }

        var roots = CertificateRoots.Configured();
        if (roots.Length == 0)
        {
            return false;
        }

        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.CustomTrustStore.AddRange(roots);
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

        // SECURITY.md I8: the platform path requires the leaf to be usable for TLS server
        // authentication, and so does Chromium; the custom-root path did not, so a
        // clientAuth or code-signing certificate from a configured CA authenticated a
        // server. The chain's ApplicationPolicy is not used for this because it also
        // applies the policy to the intermediates, which Chromium does not require of a
        // locally trusted root.
        return HasServerAuthUsage(leaf) && custom.Build(leaf);
    }

    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string AnyExtendedKeyUsageOid = "2.5.29.37.0";

    /// <summary>
    /// RFC 5280 4.2.1.12 as Chromium applies it to a TLS server leaf: no EKU extension
    /// means any purpose; otherwise it must list serverAuth or anyExtendedKeyUsage.
    /// </summary>
    internal static bool HasServerAuthUsage(X509Certificate2 leaf)
    {
        foreach (var extension in leaf.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.37")
            {
                continue;
            }

            var usages = extension as X509EnhancedKeyUsageExtension
                ?? new X509EnhancedKeyUsageExtension(extension, extension.Critical);
            foreach (var oid in usages.EnhancedKeyUsages)
            {
                if (oid.Value is ServerAuthOid or AnyExtendedKeyUsageOid)
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// The mixed-content decision for one hop: the URL to request (upgraded for an
    /// image), or a <see cref="PocketCalculatorNetException"/> for blockable content.
    /// Either way the page's console hears about it, with Chromium's text.
    /// </summary>
    private Uri ApplyMixedContent(ResourceRequest request, Uri target, CallbackRegistry? callbacks)
    {
        if (AllowInsecureContent)
        {
            return target;
        }

        var topLevel = request.ResourceType == ResourceType.Document && !request.NestedDocument;
        var context = MixedContent.Context(request.Initiator, request.SecureAncestor);
        switch (MixedContent.Check(context, target, request.ResourceType, topLevel))
        {
            case MixedContentDecision.Upgrade:
                // Chromium names the initiating frame here (about:srcdoc for a srcdoc
                // frame), and the secure page in the blocked message below.
                callbacks?.FireConsole(
                    "warning",
                    MixedContent.UpgradedMessage((request.Initiator ?? context)!.AbsoluteUri, target.AbsoluteUri));
                return MixedContent.UpgradeUrl(target);
            case MixedContentDecision.Block:
                var message = MixedContent.BlockedMessage(
                    context!.AbsoluteUri,
                    MixedContent.RequestKind(request.ResourceType, request.NestedDocument),
                    target.AbsoluteUri);
                callbacks?.FireConsole("error", message);
                throw PocketCalculatorNetException.MixedContent(message);
            default:
                return target;
        }
    }

    /// <summary>GET <paramref name="url"/> with the navigation profile.</summary>
    public Task<Response> FetchAsync(Uri url, CancellationToken cancellationToken = default) =>
        FetchWithMethodAsync(HttpMethod.Get, url, null, null, cancellationToken);

    /// <summary>
    /// <see cref="FetchAsync"/> that also fires the page's passive
    /// on_request/on_response callbacks (issue #408: callbacks are page-scoped, so
    /// the page-driven fetch paths pass their registry in).
    /// </summary>
    public Task<Response> FetchWithCallbacksAsync(
        Uri url,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default) =>
        FetchWithMethodAsync(HttpMethod.Get, url, null, callbacks, cancellationToken);

    /// <summary>POST a form-encoded body.</summary>
    public Task<Response> PostFormAsync(
        Uri url,
        string body,
        CancellationToken cancellationToken = default) =>
        FetchWithMethodAsync(HttpMethod.Post, url, Encoding.UTF8.GetBytes(body), null, cancellationToken);

    /// <summary><see cref="PostFormAsync"/> variant of <see cref="FetchWithCallbacksAsync"/>.</summary>
    public Task<Response> PostFormWithCallbacksAsync(
        Uri url,
        string body,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default) =>
        FetchWithMethodAsync(HttpMethod.Post, url, Encoding.UTF8.GetBytes(body), callbacks, cancellationToken);

    /// <summary>Issue a request with an explicit method and body.</summary>
    public Task<Response> FetchWithMethodAsync(
        HttpMethod initialMethod,
        Uri url,
        byte[]? initialBody,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default) =>
        FetchWithProfileAsync(
            initialMethod,
            url,
            initialBody,
            callbacks,
            ResourceRequest.Navigation(),
            cancellationToken);

    /// <summary>
    /// Issue a navigation with an explicit fetch profile: <see cref="ResourceRequest.Navigation"/>
    /// for a browser-initiated one, <see cref="ResourceRequest.PageNavigation"/> for one a
    /// document started, which carries the initiator into cookies, fetch metadata and Referer.
    /// </summary>
    public Task<Response> FetchNavigationAsync(
        HttpMethod method,
        Uri url,
        byte[]? body,
        ResourceRequest request,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default) =>
        FetchWithProfileAsync(method, url, body, callbacks, request, cancellationToken);

    /// <summary>
    /// Fetch a non-navigation resource through the same validated client, cookie jar,
    /// proxy, connection pool, interception and callback path as the owning page. The
    /// renderer can seed its byte cache from this result instead of opening a second
    /// synchronous HTTP stack.
    /// </summary>
    public Task<Response> FetchResourceWithCallbacksAsync(
        Uri url,
        ResourceRequest request,
        CallbackRegistry? callbacks,
        CancellationToken cancellationToken = default) =>
        FetchWithProfileAsync(HttpMethod.Get, url, null, callbacks, request, cancellationToken);

    private ResourceCacheKey? ResourceCacheKeyFor(
        HttpMethod method,
        Uri url,
        byte[]? body,
        ResourceRequest request)
    {
        if (method != HttpMethod.Get
            || body is not null
            || request.ResourceType == ResourceType.Document
            || (!string.Equals(url.Scheme, "http", StringComparison.Ordinal)
                && !string.Equals(url.Scheme, "https", StringComparison.Ordinal))
            || Interceptor is not null)
        {
            return null;
        }

        var extraHeaders = ExtraHeaders
            .Select(pair => (Name: pair.Key.ToLowerInvariant(), pair.Value))
            .OrderBy(pair => pair.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .ToList();
        foreach (var (name, value) in extraHeaders)
        {
            if (name == "authorization"
                || name == "cookie"
                || (name == "cache-control"
                    && (value.Contains("no-cache", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("no-store", StringComparison.OrdinalIgnoreCase))))
            {
                return null;
            }
        }

        if (request.SendsCredentialsTo(url)
            && CookieJar.GetCookieHeader(url, CookieAccessFor(request, url, methodIsSafe: true, [])).Length != 0)
        {
            return null;
        }

        var headerFingerprint = string.Join(
            ' ',
            extraHeaders.Select(pair => $"{pair.Name}={pair.Value}"));

        return new ResourceCacheKey(
            url.ToString(),
            request.ResourceType,
            request.Mode,
            request.Credentials,
            request.Initiator?.ToString(),
            request.Referrer?.ToString(),
            request.TopLevel?.ToString(),
            request.CrossSiteAncestor,
            UserAgent,
            headerFingerprint,
            request.MaxResponseBytes);
    }

    private async Task<Response> FetchWithProfileAsync(
        HttpMethod initialMethod,
        Uri url,
        byte[]? initialBody,
        CallbackRegistry? callbacks,
        ResourceRequest request,
        CancellationToken cancellationToken)
    {
        var cacheKey = ResourceCacheKeyFor(initialMethod, url, initialBody, request);
        if (cacheKey is null)
        {
            return await FetchWithProfileUncachedAsync(
                    initialMethod, url, initialBody, callbacks, request, cancellationToken)
                .ConfigureAwait(false);
        }

        // Cache lookup and leader election are one critical section. Without that
        // atomicity a request can miss the cache, pause, and install a second leader
        // after the first request has already populated it.
        Response? cached;
        SharedFetch? follow = null;
        SharedFetch? lead = null;
        lock (_loaderLock)
        {
            cached = _cache.Get(cacheKey);
            if (cached is null)
            {
                if (_sharedFetches.TryGetValue(cacheKey, out var existing))
                {
                    existing.Waiters++;
                    follow = existing;
                }
                else
                {
                    lead = new SharedFetch();
                    _sharedFetches[cacheKey] = lead;
                }
            }
        }

        if (cached is not null)
        {
            FireLogicalResourceCallbacks(callbacks, url, request, cached);
            return cached;
        }

        if (follow is not null)
        {
            SharedFetchOutcome outcome;
            try
            {
                outcome = await follow.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_loaderLock)
                {
                    follow.Waiters--;
                }
            }

            if (outcome.Response is { } shared)
            {
                FireLogicalResourceCallbacks(callbacks, url, request, shared);
                return shared;
            }

            return await FetchWithProfileUncachedAsync(
                    initialMethod, url, initialBody, callbacks, request, cancellationToken)
                .ConfigureAwait(false);
        }

        // If this task is cancelled or throws while awaiting I/O, the finally clause
        // removes the stale leader and wakes followers so they can retry instead of
        // waiting forever.
        var finished = false;
        try
        {
            var response = await FetchWithProfileUncachedAsync(
                    initialMethod, url, initialBody, callbacks, request, cancellationToken)
                .ConfigureAwait(false);

            var lifetime = ResponseCacheLifetime(response);
            SharedFetchOutcome outcome;
            if (lifetime is { } ttl)
            {
                lock (_loaderLock)
                {
                    _cache.Insert(cacheKey, response, ttl);
                }

                outcome = SharedFetchOutcome.Cacheable(response);
            }
            else
            {
                outcome = SharedFetchOutcome.RetryUncoalesced;
            }

            lock (_loaderLock)
            {
                _sharedFetches.Remove(cacheKey);
            }

            finished = true;
            lead!.Completion.TrySetResult(outcome);
            return response;
        }
        finally
        {
            if (!finished)
            {
                lock (_loaderLock)
                {
                    _sharedFetches.Remove(cacheKey);
                }

                lead!.Completion.TrySetResult(SharedFetchOutcome.RetryUncoalesced);
            }
        }
    }

    /// <summary>
    /// A cache hit or shared transport still represents an independent page request.
    /// Keep passive request/response observers at logical-resource granularity even
    /// when only one HTTP transaction reaches the server.
    /// </summary>
    private void FireLogicalResourceCallbacks(
        CallbackRegistry? callbacks,
        Uri url,
        ResourceRequest request,
        Response response)
    {
        if (callbacks is null)
        {
            return;
        }

        var requestInfo = new RequestInfo
        {
            Url = url,
            Method = HttpMethod.Get.Method,
            Headers = ExtraHeaders,
            ResourceType = request.ResourceType,
        };
        callbacks.FireRequest(requestInfo);
        callbacks.FireResponse(requestInfo, response);
    }

    private async Task<Response> FetchWithProfileUncachedAsync(
        HttpMethod initialMethod,
        Uri url,
        byte[]? initialBody,
        CallbackRegistry? callbacks,
        ResourceRequest request,
        CancellationToken cancellationToken)
    {
        // file: is served only to an initiator that may read local files (C4); the gate
        // runs before the file system is touched, so a refusal leaks nothing.
        SsrfGuard.ValidateUrl(url, AllowPrivateNetwork, SsrfGuard.FileAccessAllowed(request));
        ValidateRequestMode(request, url);

        if (string.Equals(url.Scheme, "file", StringComparison.Ordinal))
        {
            return await FetchFileUrlAsync(url, request.MaxResponseBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        var method = initialMethod;
        var body = initialBody;
        var currentUrl = url;
        var redirects = new List<Uri>();
        // Follow up to 20 redirects, matching the Fetch spec and the fetch()/XHR path
        // in obscura-js. The inclusive bound makes MaxRedirects+1 requests (the
        // initial one plus 20 hops), so the 20th redirect is still followed and only
        // the 21st fails.
        var redirectTainted = false;
        var requestCallbackFired = false;

        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            // SECURITY.md I7 (Rust has neither check). Mixed content first, as Chromium
            // decides it in the renderer before HSTS is consulted in the network stack;
            // then HSTS, which rewrites the hop to https as an internal 307 redirect.
            currentUrl = ApplyMixedContent(request, currentUrl, callbacks);
            if (Hsts.Upgrade(currentUrl) is { } secure)
            {
                redirects.Add(currentUrl);
                currentUrl = secure;
            }

            // Deviation from client.rs, which checks the blocklist on the first URL only
            // (SECURITY.md L7): a redirect into a tracker is blocked the same way.
            if (IsBlockedTracker(currentUrl))
            {
                return new Response
                {
                    Status = 0,
                    Url = currentUrl,
                    Headers = new Dictionary<string, string>(StringComparer.Ordinal),
                    Body = [],
                    RedirectedFrom = redirects,
                };
            }

            ValidateRequestMode(request, currentUrl);
            var requestInfo = new RequestInfo
            {
                Url = currentUrl,
                Method = method.Method,
                Headers = ExtraHeaders,
                ResourceType = request.ResourceType,
            };

            IReadOnlyDictionary<string, string>? hopHeaders = null;
            if (Interceptor is { } interceptor)
            {
                var action = await interceptor.InterceptAsync(requestInfo).ConfigureAwait(false);
                switch (action)
                {
                    case InterceptAction.Continue:
                        break;
                    case InterceptAction.Block:
                        throw PocketCalculatorNetException.Blocked(currentUrl.ToString());
                    case InterceptAction.Fulfill fulfill:
                        return fulfill.Response;
                    case InterceptAction.ModifyHeaders modify:
                        // Deviation from client.rs, which merges these into the
                        // client-wide extra headers for good (SECURITY.md L8): a header
                        // added for one request (an Authorization, say) then went to
                        // every later host. They apply to this request only.
                        hopHeaders = modify.Headers;
                        break;
                    default:
                        break;
                }
            }

            if (!requestCallbackFired)
            {
                callbacks?.FireRequest(requestInfo);
                requestCallbackFired = true;
            }

            var requestOrigin = SerializedRequestOrigin(request, redirectTainted);
            using var message = BuildRequest(method, currentUrl, body, request, requestOrigin, redirects, hopHeaders);

            // Deviation (SECURITY.md M3): HttpClient.Timeout stops at the response
            // headers under ResponseHeadersRead, so a server trickling the body held a
            // navigation forever. One deadline spans this hop's headers and body.
            using var hopDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (Timeout > TimeSpan.Zero && Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                hopDeadline.CancelAfter(Timeout);
            }
            var hopToken = hopDeadline.Token;

            Interlocked.Increment(ref _inFlight);
            HttpResponseMessage resp;
            try
            {
                resp = await GetClient()
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, hopToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Decrement(ref _inFlight);
                throw PocketCalculatorNetException.Network($"{currentUrl}: request timed out");
            }
            catch (HttpRequestException error)
            {
                Interlocked.Decrement(ref _inFlight);
                throw PocketCalculatorNetException.Network($"{currentUrl}: {error.Message}", error);
            }
            catch
            {
                Interlocked.Decrement(ref _inFlight);
                throw;
            }

            var released = false;
            try
            {
                var status = (int)resp.StatusCode;
                var responseHeaders = CollectHeaders(resp);
                ValidateCorsResponseHeaders(request, currentUrl, requestOrigin, resp);

                if (request.SendsCredentialsTo(currentUrl)
                    && resp.Headers.NonValidated.TryGetValues("Set-Cookie", out var setCookies))
                {
                    var setAccess = CookieSetAccessFor(request, currentUrl, redirects);
                    foreach (var value in setCookies)
                    {
                        CookieJar.SetCookie(value, currentUrl, setAccess);
                    }
                }

                if (status is >= 300 and < 400
                    && resp.Headers.NonValidated.TryGetValues("Location", out var locations))
                {
                    var locationString = locations.FirstOrDefault();
                    if (locationString is not null)
                    {
                        if (!Uri.TryCreate(currentUrl, locationString, out var nextUrl))
                        {
                            throw PocketCalculatorNetException.Network("Invalid redirect URL");
                        }

                        SsrfGuard.ValidateUrl(nextUrl, AllowPrivateNetwork);
                        ValidateRequestMode(request, nextUrl);

                        // Fetch "set request's referrer policy on redirect": a policy on
                        // the redirect response governs the following hops. Rust ignores it.
                        if (ReferrerPolicies.ParseHeader(responseHeaders.GetValueOrDefault("referrer-policy")) is { } redirectPolicy)
                        {
                            request = request with { ReferrerPolicy = redirectPolicy };
                        }

                        redirectTainted |= RedirectTaintsOrigin(request, currentUrl, nextUrl);
                        redirects.Add(currentUrl);
                        currentUrl = nextUrl;
                        if (status is 301 or 302 or 303)
                        {
                            method = HttpMethod.Get;
                            body = null;
                        }

                        continue;
                    }
                }

                byte[] bodyBytes;
                try
                {
                    bodyBytes = await ReadBodyLimitedAsync(
                            resp, currentUrl, request.MaxResponseBytes, hopToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw PocketCalculatorNetException.Network($"{currentUrl}: request timed out");
                }
                catch (HttpRequestException error)
                {
                    throw PocketCalculatorNetException.Network($"Failed to read body: {error.Message}", error);
                }
                catch (IOException error)
                {
                    throw PocketCalculatorNetException.Network($"Failed to read body: {error.Message}", error);
                }

                Interlocked.Decrement(ref _inFlight);
                released = true;

                var response = new Response
                {
                    Url = currentUrl,
                    Status = status,
                    Headers = responseHeaders,
                    Body = bodyBytes,
                    RedirectedFrom = redirects,
                };

                callbacks?.FireResponse(requestInfo, response);
                return response;
            }
            finally
            {
                if (!released)
                {
                    Interlocked.Decrement(ref _inFlight);
                }

                resp.Dispose();
            }
        }

        throw PocketCalculatorNetException.TooManyRedirects(currentUrl.ToString());
    }

    private HttpRequestMessage BuildRequest(
        HttpMethod method,
        Uri currentUrl,
        byte[]? body,
        ResourceRequest request,
        string requestOrigin,
        IReadOnlyList<Uri> redirects,
        IReadOnlyDictionary<string, string>? hopHeaders)
    {
        var message = new HttpRequestMessage(method, currentUrl);
        var headers = message.Headers;
        var ua = UserAgent;
        var (secChUa, secChUaPlatform) = ChromeClientHints(ua);

        // Chrome's top-level navigation header order.
        headers.TryAddWithoutValidation("sec-ch-ua", secChUa);
        headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        headers.TryAddWithoutValidation("sec-ch-ua-platform", secChUaPlatform);
        if (request.Mode == RequestMode.Navigate)
        {
            headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");
        }

        headers.TryAddWithoutValidation("User-Agent", ua);
        headers.TryAddWithoutValidation("Accept", request.Accept());
        headers.TryAddWithoutValidation("sec-fetch-site", RequestFetchSite(request, currentUrl, redirects));
        headers.TryAddWithoutValidation("sec-fetch-mode", request.Mode.HeaderValue());
        // Deviation: upstream sends `?1` on every navigation. Chromium sends it only when
        // the navigation had user activation: always for a browser-initiated one, never
        // for a script's `location.href = ...` or `a.click()` outside a user gesture.
        if (request.Mode == RequestMode.Navigate && request.UserActivated)
        {
            headers.TryAddWithoutValidation("sec-fetch-user", "?1");
        }

        headers.TryAddWithoutValidation("sec-fetch-dest", request.Destination());
        if (RequestReferrer(request, currentUrl, redirects) is { } referer)
        {
            headers.TryAddWithoutValidation("Referer", referer);
        }

        var acceptLanguage = AcceptLanguage;
        if (acceptLanguage.Length != 0)
        {
            headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        var cookieHeader = request.SendsCredentialsTo(currentUrl)
            ? CookieJar.GetCookieHeader(
                currentUrl,
                CookieAccessFor(
                    request, currentUrl, method == HttpMethod.Get || method == HttpMethod.Head, redirects))
            : string.Empty;
        if (cookieHeader.Length != 0)
        {
            if (IsValidHeaderValue(cookieHeader))
            {
                headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
            else
            {
                var filtered = string.Join(
                    "; ",
                    cookieHeader.Split("; ").Where(IsValidHeaderValue));
                if (filtered.Length != 0)
                {
                    headers.TryAddWithoutValidation("Cookie", filtered);
                }
            }
        }

        foreach (var (name, value) in ExtraHeaders)
        {
            headers.Remove(name);
            headers.TryAddWithoutValidation(name, value);
        }

        if (hopHeaders is not null)
        {
            foreach (var (name, value) in hopHeaders)
            {
                headers.Remove(name);
                headers.TryAddWithoutValidation(name, value);
            }
        }

        // Origin is a forbidden browser request header. Keep it derived from the
        // initiator even when callers supplied extra headers.
        headers.Remove("Origin");
        if (CorsRequired(request, currentUrl) || NavigationSendsOrigin(request, method))
        {
            headers.TryAddWithoutValidation("Origin", requestOrigin);
        }

        if (body is not null)
        {
            var content = new ByteArrayContent(body);
            if (method == HttpMethod.Post)
            {
                content.Headers.TryAddWithoutValidation(
                    "Content-Type",
                    "application/x-www-form-urlencoded");
            }

            message.Content = content;
        }

        return message;
    }

    private static bool IsValidHeaderValue(string value)
    {
        foreach (var c in value)
        {
            if (c is '\r' or '\n' || c == (char)0 || c > (char)0xFF)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers.NonValidated)
        {
            headers[header.Key.ToLowerInvariant()] = LastValue(header.Value);
        }

        foreach (var header in response.Content.Headers.NonValidated)
        {
            headers[header.Key.ToLowerInvariant()] = LastValue(header.Value);
        }

        return headers;
    }

    private static string LastValue(HeaderStringValues values)
    {
        var last = string.Empty;
        foreach (var value in values)
        {
            last = value;
        }

        return last;
    }

    private static void RejectOversizedContentLength(HttpResponseMessage response, Uri url, long limit)
    {
        if (TryDeclaredLength(response, out var length) && length > limit)
        {
            throw PocketCalculatorNetException.ResponseTooLarge(url, limit);
        }
    }

    private static bool TryDeclaredLength(HttpResponseMessage response, out long length)
    {
        length = 0;
        return response.Content.Headers.NonValidated.TryGetValues("Content-Length", out var values)
            && long.TryParse(
                values.FirstOrDefault()?.Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out length);
    }

    private static async Task<byte[]> ReadBodyLimitedAsync(
        HttpResponseMessage response,
        Uri url,
        long limit,
        CancellationToken cancellationToken)
    {
        RejectOversizedContentLength(response, url, limit);
        var declared = TryDeclaredLength(response, out var parsed) ? parsed : 0;
        var capacity = (int)Math.Clamp(Math.Min(declared, limit), 0, 1 << 20);

        using var buffered = new MemoryStream(capacity);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (read > limit - buffered.Length)
                {
                    throw PocketCalculatorNetException.ResponseTooLarge(url, limit);
                }

                buffered.Write(chunk, 0, read);
            }
        }

        return buffered.ToArray();
    }

    internal static async Task<Response> FetchFileUrlAsync(
        Uri url,
        long maxResponseBytes,
        CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = url.LocalPath;
        }
        catch (InvalidOperationException)
        {
            throw PocketCalculatorNetException.Network("Invalid file URL");
        }

        if (path.Length == 0)
        {
            throw PocketCalculatorNetException.Network("Invalid file URL");
        }

        var info = new FileInfo(path);
        if (info.Exists && info.Length > maxResponseBytes)
        {
            throw PocketCalculatorNetException.ResponseTooLarge(url, maxResponseBytes);
        }

        byte[] body;
        try
        {
            body = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw PocketCalculatorNetException.Network($"Failed to read file: {error.Message}", error);
        }

        if (body.Length > maxResponseBytes)
        {
            throw PocketCalculatorNetException.ResponseTooLarge(url, maxResponseBytes);
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var extension = Path.GetExtension(path);
        if (extension.Length > 1)
        {
            var contentType = extension[1..].ToLowerInvariant() switch
            {
                "html" or "htm" => "text/html",
                "css" => "text/css",
                "js" or "mjs" => "application/javascript",
                "json" => "application/json",
                "png" => "image/png",
                "jpg" or "jpeg" => "image/jpeg",
                "gif" => "image/gif",
                "svg" => "image/svg+xml",
                "webp" => "image/webp",
                "ico" => "image/x-icon",
                _ => "application/octet-stream",
            };
            headers["content-type"] = contentType;
        }

        return new Response
        {
            Url = url,
            Status = 200,
            Headers = headers,
            Body = body,
            RedirectedFrom = [],
        };
    }

    internal static bool SameOrigin(ResourceRequest request, Uri target) =>
        request.Initiator is not null && UrlOrigin.SameOrigin(request.Initiator, target);

    internal static bool CorsRequired(ResourceRequest request, Uri target) =>
        request.Mode == RequestMode.Cors && !SameOrigin(request, target);

    /// <summary>
    /// Serialize the request origin used by both the Origin request header and the
    /// response CORS check. A redirect chain that changes origin after it has already
    /// left the initiator origin is tainted and serializes to <c>null</c>.
    /// </summary>
    internal static string SerializedRequestOrigin(ResourceRequest request, bool redirectTainted)
    {
        if (redirectTainted)
        {
            return "null";
        }

        var initiator = request.Initiator;
        if (initiator is null
            || (!string.Equals(initiator.Scheme, "http", StringComparison.Ordinal)
                && !string.Equals(initiator.Scheme, "https", StringComparison.Ordinal)))
        {
            return "null";
        }

        return UrlOrigin.AsciiSerialization(initiator);
    }

    internal static bool RedirectTaintsOrigin(ResourceRequest request, Uri current, Uri next) =>
        !UrlOrigin.SameOrigin(current, next)
        && (request.Initiator is null || !UrlOrigin.SameOrigin(request.Initiator, current));

    internal static void ValidateRequestMode(ResourceRequest request, Uri target)
    {
        if (request.Mode == RequestMode.SameOrigin && !SameOrigin(request, target))
        {
            throw PocketCalculatorNetException.Cors($"same-origin request blocked for {target}");
        }
    }

    internal static void ValidateCorsResponse(
        ResourceRequest request,
        Uri target,
        string serializedOrigin,
        string? allowOrigin,
        string? allowCredentials)
    {
        if (!CorsRequired(request, target))
        {
            return;
        }

        if (allowOrigin is null)
        {
            throw PocketCalculatorNetException.Cors(
                $"{target} did not include Access-Control-Allow-Origin for origin {serializedOrigin}");
        }

        if (request.Credentials != RequestCredentials.Include && allowOrigin == "*")
        {
            return;
        }

        if (!string.Equals(allowOrigin, serializedOrigin, StringComparison.Ordinal))
        {
            throw PocketCalculatorNetException.Cors(
                $"{target} returned Access-Control-Allow-Origin {Quote(allowOrigin)}, expected {Quote(serializedOrigin)}");
        }

        if (request.Credentials == RequestCredentials.Include
            && !string.Equals(allowCredentials, "true", StringComparison.Ordinal))
        {
            throw PocketCalculatorNetException.Cors(
                $"credentialed response from {target} requires Access-Control-Allow-Credentials: true");
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static void ValidateCorsResponseHeaders(
        ResourceRequest request,
        Uri target,
        string serializedOrigin,
        HttpResponseMessage response)
    {
        if (!CorsRequired(request, target))
        {
            return;
        }

        var allowOrigin = SingleHeader(response, "access-control-allow-origin", target);
        var allowCredentials = SingleHeader(response, "access-control-allow-credentials", target);
        ValidateCorsResponse(request, target, serializedOrigin, allowOrigin, allowCredentials);
    }

    private static string? SingleHeader(HttpResponseMessage response, string name, Uri url)
    {
        if (!response.Headers.NonValidated.TryGetValues(name, out var values))
        {
            return null;
        }

        string? first = null;
        var count = 0;
        foreach (var value in values)
        {
            first ??= value;
            count++;
        }

        if (count > 1)
        {
            throw PocketCalculatorNetException.Cors($"{url} returned multiple {name} headers");
        }

        return first;
    }

    internal static string RequestFetchSite(ResourceRequest request, Uri target) =>
        RequestFetchSite(request, target, []);

    /// <summary>
    /// The <c>Sec-Fetch-Site</c> value (Fetch Metadata, "set the Sec-Fetch-Site header"):
    /// the least trusted relation between the initiator and any URL in the request's
    /// URL list, so a redirect through another site stays <c>cross-site</c> after it
    /// comes back. No initiator is a browser-initiated navigation, <c>none</c> across
    /// its redirects too (measured on Chromium 140).
    /// </summary>
    /// <remarks>
    /// Deviation: upstream compares origins only, so a same-site cross-origin request
    /// reported <c>cross-site</c>, and it looks at the current hop alone. This uses the
    /// registrable-domain site the cookie jar uses and walks the whole chain.
    /// </remarks>
    internal static string RequestFetchSite(ResourceRequest request, Uri target, IReadOnlyList<Uri> redirects)
    {
        if (request.Initiator is not { } initiator)
        {
            return "none";
        }

        var value = "same-origin";
        for (var i = 0; i <= redirects.Count; i++)
        {
            var url = i < redirects.Count ? redirects[i] : target;
            if (UrlOrigin.SameOrigin(initiator, url))
            {
                continue;
            }

            if (!CookieJar.IsSameSite(initiator, url))
            {
                return "cross-site";
            }

            value = "same-site";
        }

        return value;
    }

    /// <summary>
    /// Whether a navigation carries an <c>Origin</c> header: a document-initiated one
    /// with a method other than GET or HEAD (a form POST), as in Chromium. Upstream
    /// sends none on any navigation.
    /// </summary>
    internal static bool NavigationSendsOrigin(ResourceRequest request, HttpMethod method) =>
        request.Mode == RequestMode.Navigate
        && request.Initiator is not null
        && method != HttpMethod.Get
        && method != HttpMethod.Head;

    /// <summary>
    /// The SameSite context for a request's cookies (port of <c>same_site_context</c>
    /// in <c>client.rs</c>). No initiator is a browser-initiated navigation, which is
    /// same-site; a cross-site top-level navigation with a safe method may carry Lax
    /// cookies; anything else cross-site carries only SameSite=None.
    /// </summary>
    internal static SameSiteContext SameSiteContextFor(ResourceRequest request, Uri target, bool methodIsSafe) =>
        SameSiteContextFor(request, target, methodIsSafe, []);

    /// <summary>
    /// <see cref="SameSiteContextFor(ResourceRequest, Uri, bool)"/> for one hop of a
    /// redirect chain. A navigation whose chain passed through another site than the
    /// current target's is cross-site even when it has no initiator or a same-site
    /// one, as Chromium computes it: <c>Page.navigate</c> to a.example that redirects
    /// to b.example sends b's Lax cookies but not its Strict ones.
    /// </summary>
    /// <remarks>
    /// Deviation: upstream judges the initiator alone, so it has no redirect-chain
    /// rule, and a cross-site iframe load counted as a top-level navigation for Lax.
    /// Chromium only lets Lax cookies through on a top-level navigation.
    /// </remarks>
    internal static SameSiteContext SameSiteContextFor(
        ResourceRequest request,
        Uri target,
        bool methodIsSafe,
        IReadOnlyList<Uri> redirects)
    {
        var sameSite = request.Initiator is not { } initiator || CookieJar.IsSameSite(initiator, target);
        if (sameSite && request.Mode == RequestMode.Navigate)
        {
            foreach (var hop in redirects)
            {
                if (!CookieJar.IsSameSite(hop, target))
                {
                    sameSite = false;
                    break;
                }
            }
        }

        // A request from inside a frame is same-site only when the frame and every
        // ancestor are same-site with the top-level document, which the target must be
        // too: Chromium's site for cookies. Deviation: Rust judges the initiator alone,
        // so a cross-site frame's requests to its own site carried Lax and Strict cookies
        // (Chromium 141 sends SameSite=None cookies only).
        if (sameSite && !IsTopLevelNavigation(request) && request.TopLevel is { } topLevel
            && (request.CrossSiteAncestor || !CookieJar.IsSameSite(topLevel, target)))
        {
            sameSite = false;
        }

        if (sameSite)
        {
            return SameSiteContext.SameSite;
        }

        return request.Mode == RequestMode.Navigate && !request.NestedDocument && methodIsSafe
            ? SameSiteContext.CrossSiteTopLevelSafe
            : SameSiteContext.CrossSite;
    }

    private static bool IsTopLevelNavigation(ResourceRequest request) =>
        request.Mode == RequestMode.Navigate && !request.NestedDocument;

    /// <summary>
    /// The cookie context of one hop of <paramref name="request"/>: its SameSite context and
    /// its partition key. A top-level navigation is keyed by its own target; any other
    /// request by the top-level document (<see cref="ResourceRequest.TopLevel"/>, else the
    /// initiator), with the cross-site bit when the initiating frame had a cross-site
    /// ancestor or the target is cross-site with the top level. Port addition (CHIPS).
    /// </summary>
    public static CookieAccess CookieAccessFor(
        ResourceRequest request,
        Uri target,
        bool methodIsSafe,
        IReadOnlyList<Uri> redirects)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sameSite = SameSiteContextFor(request, target, methodIsSafe, redirects);
        var topLevel = IsTopLevelNavigation(request) ? null : request.TopLevel ?? request.Initiator;
        var partition = topLevel is null
            ? CookiePartitionKey.ForTopLevel(target)
            : CookiePartitionKey.For(topLevel, request.CrossSiteAncestor, target);
        return new CookieAccess(sameSite, partition);
    }

    /// <summary>
    /// The context a <c>Set-Cookie</c> on the response to one hop of <paramref name="request"/>
    /// is stored in: <see cref="CookieAccessFor"/>, except that the response to a top-level
    /// navigation may set any cookie, as Chromium lets it.
    /// </summary>
    public static CookieAccess CookieSetAccessFor(
        ResourceRequest request,
        Uri target,
        IReadOnlyList<Uri> redirects)
    {
        var access = CookieAccessFor(request, target, methodIsSafe: true, redirects);
        return IsTopLevelNavigation(request) ? access with { SameSite = SameSiteContext.SameSite } : access;
    }

    /// <summary>
    /// The navigation request headers a transport other than this client has to add
    /// itself: the page's <c>op_fetch_url</c>, which loads iframe documents. Values are
    /// those <see cref="BuildRequest"/> sends for the same profile and hop.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> NavigationHeaders(
        ResourceRequest request,
        Uri target,
        IReadOnlyList<Uri> redirects)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<(string Name, string Value)> headers =
        [
            ("upgrade-insecure-requests", "1"),
            ("Accept", request.Accept()),
            ("sec-fetch-site", RequestFetchSite(request, target, redirects)),
            ("sec-fetch-mode", request.Mode.HeaderValue()),
        ];
        if (request.Mode == RequestMode.Navigate && request.UserActivated)
        {
            headers.Add(("sec-fetch-user", "?1"));
        }

        headers.Add(("sec-fetch-dest", request.Destination()));
        if (RequestReferrer(request, target, redirects) is { } referer)
        {
            headers.Add(("Referer", referer));
        }

        return headers;
    }

    /// <summary>
    /// The SameSite context of one hop of <paramref name="request"/>, for a transport
    /// other than this client (see <see cref="NavigationHeaders"/>).
    /// </summary>
    public static SameSiteContext NavigationSameSiteContext(
        ResourceRequest request,
        Uri target,
        bool methodIsSafe,
        IReadOnlyList<Uri> redirects) =>
        SameSiteContextFor(request, target, methodIsSafe, redirects);

    internal static string? RequestReferrer(ResourceRequest request, Uri target) =>
        ReferrerFor(request.Referrer ?? request.Initiator, target, request.ReferrerPolicy);

    /// <summary>
    /// The Referer of one hop of a redirect chain. The policy is applied at every hop to
    /// what the previous hop sent, so a referrer trimmed to its origin for a
    /// cross-origin first hop stays trimmed when the chain comes back to the referrer's
    /// own origin, and one dropped stays dropped (Fetch "HTTP-redirect fetch" and
    /// Chromium 140: a.example to b.example/redirect to a.example/next sends
    /// <c>https://a.example/</c> on the last hop).
    /// </summary>
    /// <remarks>
    /// Deviation: upstream derives each hop's Referer from the original referrer URL,
    /// which restored the full URL on the way back.
    /// </remarks>
    internal static string? RequestReferrer(ResourceRequest request, Uri target, IReadOnlyList<Uri> redirects)
    {
        var source = request.Referrer ?? request.Initiator;
        foreach (var hop in redirects)
        {
            if (ReferrerFor(source, hop, request.ReferrerPolicy) is not { } sent
                || !Uri.TryCreate(sent, UriKind.Absolute, out source))
            {
                return null;
            }
        }

        return ReferrerFor(source, target, request.ReferrerPolicy);
    }

    /// <summary>
    /// The Referer under <paramref name="policy"/> (the default when null). Deviation:
    /// upstream hard-codes strict-origin-when-cross-origin and treats any https to http
    /// request as a downgrade; the policy now comes from the document, the element or
    /// fetch(), and http://localhost is potentially trustworthy, as in Chromium.
    /// </summary>
    private static string? ReferrerFor(Uri? source, Uri target, ReferrerPolicy? policy) =>
        ReferrerPolicies.Referrer(source, target, policy ?? ReferrerPolicies.Default);

    private static bool IsHttpScheme(Uri url) =>
        string.Equals(url.Scheme, "http", StringComparison.Ordinal)
        || string.Equals(url.Scheme, "https", StringComparison.Ordinal);

    internal static TimeSpan? ResponseCacheLifetime(Response response)
    {
        if (response.Status is < 200 or >= 300
            || response.RedirectedFrom.Count != 0
            || response.Header("set-cookie") is not null
            || (response.Header("vary") is { } vary
                && vary.Split(',').Any(name => name.Trim() == "*")))
        {
            return null;
        }

        if (response.Header("cache-control") is not { } cacheControl)
        {
            return null;
        }

        long? maxAge = null;
        foreach (var raw in cacheControl.Split(','))
        {
            var lower = raw.Trim().ToLowerInvariant();
            if (lower == "no-store" || lower == "no-cache")
            {
                return null;
            }

            if (lower.StartsWith("max-age=", StringComparison.Ordinal)
                && long.TryParse(
                    lower["max-age=".Length..].Trim('"'),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                maxAge = parsed;
            }
        }

        return maxAge is > 0 ? TimeSpan.FromSeconds(maxAge.Value) : null;
    }

    /// <summary>
    /// Derive the sec-ch-ua and sec-ch-ua-platform client-hint header values from a
    /// User-Agent string, using Chromium's per-major-version GREASE algorithm so the
    /// non-stealth HTTP path agrees with navigator.userAgentData instead of shipping a
    /// fixed Linux/Chrome-145 hint that contradicts a Windows profile.
    /// </summary>
    internal static (string SecChUa, string Platform) ChromeClientHints(string ua)
    {
        var major = 145;
        var marker = ua.IndexOf("Chrome/", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var rest = ua[(marker + "Chrome/".Length)..];
            var dot = rest.IndexOf('.');
            var digits = dot < 0 ? rest : rest[..dot];
            if (int.TryParse(
                    digits,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
            {
                major = parsed;
            }
        }

        ReadOnlySpan<char> greaseChars = [' ', '(', ':', '-', '.', '/', ')', ';', '=', '?', '_'];
        string[] greaseVer = ["8", "99", "24"];
        int[][] perms = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        var greaseBrand = $"Not{greaseChars[major % 11]}A{greaseChars[(major + 1) % 11]}Brand";
        var majorText = major.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (string Brand, string Version)[] brands =
        [
            (greaseBrand, greaseVer[major % 3]),
            ("Chromium", majorText),
            ("Google Chrome", majorText),
        ];
        var permutation = perms[major % 6];
        var secChUa = string.Join(
            ", ",
            permutation.Select(i => $"\"{brands[i].Brand}\";v=\"{brands[i].Version}\""));
        var platform = ua.Contains("Windows NT", StringComparison.Ordinal)
            ? "\"Windows\""
            : ua.Contains("Macintosh", StringComparison.Ordinal)
                ? "\"macOS\""
                : "\"Linux\"";
        return (secChUa, platform);
    }

    /// <summary>Dispose the underlying transport.</summary>
    public void Dispose()
    {
        HttpClient? client;
        lock (_clientLock)
        {
            client = _client;
            _client = null;
        }

        client?.Dispose();
    }

    /// <summary>Test hook: true while at least one follower is parked on a shared fetch.</summary>
    internal bool HasWaitingSharedFollowers()
    {
        lock (_loaderLock)
        {
            return _sharedFetches.Values.Any(entry => entry.Waiters > 0);
        }
    }

    private sealed record ResourceCacheKey(
        string Url,
        ResourceType ResourceType,
        RequestMode Mode,
        RequestCredentials Credentials,
        string? Initiator,
        string? Referrer,
        string? TopLevel,
        bool CrossSiteAncestor,
        string UserAgent,
        string ExtraHeaders,
        long MaxResponseBytes);

    private sealed class SharedFetch
    {
        internal TaskCompletionSource<SharedFetchOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Waiters { get; set; }
    }

    private sealed record SharedFetchOutcome(Response? Response)
    {
        internal static SharedFetchOutcome Cacheable(Response response) => new(response);

        internal static SharedFetchOutcome RetryUncoalesced { get; } = new((Response?)null);
    }

    private sealed class ResourceCache
    {
        private readonly Dictionary<ResourceCacheKey, (Response Response, long ExpiresAtTicks)> _entries = [];
        private readonly LinkedList<ResourceCacheKey> _insertionOrder = new();
        private long _bodyBytes;

        internal Response? Get(ResourceCacheKey key)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            if (entry.ExpiresAtTicks <= Environment.TickCount64)
            {
                _entries.Remove(key);
                RemoveFromOrder(key);
                _bodyBytes = Math.Max(0, _bodyBytes - entry.Response.Body.Length);
                return null;
            }

            return entry.Response;
        }

        internal void Insert(ResourceCacheKey key, Response response, TimeSpan lifetime)
        {
            long responseBytes = response.Body.Length;
            if (responseBytes > ResourceCacheMaxBytes)
            {
                return;
            }

            if (_entries.Remove(key, out var previous))
            {
                _bodyBytes = Math.Max(0, _bodyBytes - previous.Response.Body.Length);
                RemoveFromOrder(key);
            }

            while (_entries.Count >= ResourceCacheMaxEntries
                || _bodyBytes + responseBytes > ResourceCacheMaxBytes)
            {
                var oldest = _insertionOrder.First;
                if (oldest is null)
                {
                    break;
                }

                _insertionOrder.RemoveFirst();
                if (_entries.Remove(oldest.Value, out var evicted))
                {
                    _bodyBytes = Math.Max(0, _bodyBytes - evicted.Response.Body.Length);
                }
            }

            _bodyBytes += responseBytes;
            _insertionOrder.AddLast(key);
            _entries[key] = (response, Environment.TickCount64 + (long)lifetime.TotalMilliseconds);
        }

        private void RemoveFromOrder(ResourceCacheKey key)
        {
            var node = _insertionOrder.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value == key)
                {
                    _insertionOrder.Remove(node);
                }

                node = next;
            }
        }
    }
}
