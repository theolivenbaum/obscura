using System.Globalization;
using System.Net;
using System.Text;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// The failure an op reports back to JavaScript as a thrown error / rejected promise.
/// </summary>
/// <remarks>
/// Rust returns <c>Err(JsErrorBox)</c> from the fallible ops and deno_core turns that
/// into a JS exception. That is a documented outcome, not a leak, so it must reach
/// the shim rather than being converted to a sentinel by <see cref="OpGuard"/>.
/// </remarks>
public sealed class OpException(string message) : Exception(message);

/// <summary>Which credentials a scripted fetch may attach.</summary>
public enum FetchCredentials
{
    Omit,
    SameOrigin,
    Include,
}

/// <summary><c>op_fetch_url</c> and the request policy helpers around it.</summary>
public static partial class FetchOps
{
    /// <summary>
    /// Cap on the number of redirect hops <c>op_fetch_url</c> will follow.
    /// </summary>
    /// <remarks>
    /// The Fetch standard fixes the number at 20: HTTP-redirect fetch returns a
    /// network error as soon as a request's redirect count <em>reaches</em> 20 and
    /// only increments it afterwards, so the twentieth hop must still succeed and the
    /// twenty-first must fail. The redirects are followed by hand, one hop per loop
    /// iteration, so every hop can be checked against the SSRF rules again.
    /// </remarks>
    internal const int FetchRedirectLimit = 20;

    private const int MaxJsNetworkEvents = 4096;

    private const string DefaultUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";

    internal static FetchCredentials ParseCredentials(string value) => value switch
    {
        "omit" => FetchCredentials.Omit,
        "include" => FetchCredentials.Include,
        _ => FetchCredentials.SameOrigin,
    };

    /// <summary>Whether the given credentials mode sends cookies to this request URL.</summary>
    public static bool Allows(this FetchCredentials credentials, string pageOrigin, string requestUrl) =>
        credentials switch
        {
            FetchCredentials.Omit => false,
            FetchCredentials.Include => true,
            _ => RequestOrigin(requestUrl) is { } origin
                && string.Equals(origin, pageOrigin, StringComparison.Ordinal),
        };

    internal static string? RequestOrigin(string requestUrl) => UrlRecord.Parse(requestUrl)?.AsciiOrigin;

    internal static bool CorsResponseAllows(
        FetchCredentials credentials,
        string pageOrigin,
        string allowedOrigin,
        string allowCredentials) =>
        credentials == FetchCredentials.Include
            ? string.Equals(allowedOrigin, pageOrigin, StringComparison.Ordinal)
                && string.Equals(allowCredentials, "true", StringComparison.Ordinal)
            : string.Equals(allowedOrigin, "*", StringComparison.Ordinal)
                || string.Equals(allowedOrigin, pageOrigin, StringComparison.Ordinal);

    /// <summary>
    /// The scheme and private-network gate every scripted fetch URL passes, including
    /// interception rewrites and every redirect hop. Returns null when allowed.
    /// </summary>
    internal static string? ValidateFetchUrl(UrlRecord url, bool allowPrivateNetwork)
    {
        var scheme = url.Scheme;
        // file:// is denied by default here, matching Page.navigate /
        // Target.createTarget (which gate it behind --allow-file-access). The
        // transports cannot fetch file:// anyway, so a page never reaches the
        // filesystem through fetch()/XHR.
        if (!string.Equals(scheme, "http", StringComparison.Ordinal)
            && !string.Equals(scheme, "https", StringComparison.Ordinal))
        {
            return $"Forbidden URL scheme '{scheme}' - only http and https are allowed";
        }

        if (allowPrivateNetwork || SsrfGuard.EnvAllowsPrivateNetwork())
        {
            return null;
        }

        if (url.HostStr is not { } host || host.Length == 0)
        {
            return null;
        }

        switch (url.HostKind)
        {
            case HostKind.Ipv4:
                if (IPAddress.TryParse(host, out var v4) && SsrfGuard.IsForbiddenIp(v4))
                {
                    return $"Access to private/internal IP address {v4} is not allowed";
                }

                break;

            case HostKind.Ipv6:
            {
                var literal = host.Length >= 2 && host[0] == '[' ? host[1..^1] : host;
                if (IPAddress.TryParse(literal, out var v6) && SsrfGuard.IsForbiddenIp(v6))
                {
                    return $"Access to private/internal IPv6 address {v6} is not allowed";
                }

                break;
            }

            default:
            {
                var lower = host.ToLowerInvariant();
                if (string.Equals(lower, "localhost", StringComparison.Ordinal)
                    || lower.EndsWith(".localhost", StringComparison.Ordinal)
                    || string.Equals(lower, "127.0.0.1", StringComparison.Ordinal)
                    || string.Equals(lower, "::1", StringComparison.Ordinal))
                {
                    return $"Access to localhost domain '{host}' is not allowed";
                }

                break;
            }
        }

        return null;
    }

    /// <summary>The CDP <c>Network.setBlockedURLs</c> glob dialect.</summary>
    internal static bool GlobMatch(string pattern, string url)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        var remainder = url.AsSpan();
        var first = true;
        foreach (var range in pattern.AsSpan().Split('*'))
        {
            var part = pattern.AsSpan()[range];
            if (part.Length == 0)
            {
                continue;
            }

            var index = remainder.IndexOf(part, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            if (first && !pattern.StartsWith('*') && index != 0)
            {
                return false;
            }

            remainder = remainder[(index + part.Length)..];
            first = false;
        }

        return pattern.EndsWith('*') || remainder.Length == 0;
    }

    /// <summary>
    /// Reads a response body into memory, refusing bodies larger than
    /// <paramref name="max"/> bytes - both when the server advertises an oversized
    /// <c>Content-Length</c> and when the streamed chunks exceed the cap (a lying or
    /// chunked server). Streaming keeps a multi-GB response from ever being fully
    /// allocated.
    /// </summary>
    internal static async Task<byte[]> ReadBodyCappedAsync(
        HttpResponseMessage response,
        int max,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Content.Headers.ContentLength is { } length && length > max)
        {
            throw new OpException($"response body of {length} bytes exceeds the maximum of {max}");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // A declared length is read straight into an array of that size, so the body is
            // held once. The port used to grow a MemoryStream and copy it out, three times
            // the body at the peak (SECURITY.md M4).
            byte[]? exact = response.Content.Headers.ContentLength is { } declared and > 0
                ? new byte[declared]
                : null;
            var filled = 0;
            if (exact is not null)
            {
                while (filled < exact.Length)
                {
                    var read = await stream.ReadAsync(exact.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled < exact.Length)
                {
                    return exact[..filled];
                }
            }

            // No length, or more bytes than it declared: chunks, then one exact copy.
            List<byte[]> chunks = [];
            var chunkSize = 64 * 1024;
            long total = filled;
            while (true)
            {
                var chunk = new byte[chunkSize];
                var used = 0;
                while (used < chunk.Length)
                {
                    var read = await stream.ReadAsync(chunk.AsMemory(used), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (total + read > max)
                    {
                        throw new OpException($"response body exceeds the maximum of {max} bytes");
                    }

                    used += read;
                    total += read;
                }

                if (used == 0)
                {
                    break;
                }

                chunks.Add(used == chunk.Length ? chunk : chunk[..used]);
                if (used < chunk.Length)
                {
                    break;
                }

                chunkSize = Math.Min(chunkSize * 2, 4 * 1024 * 1024);
            }

            if (exact is not null && chunks.Count == 0)
            {
                return exact;
            }

            var body = new byte[total];
            var at = 0;
            if (exact is not null)
            {
                exact.CopyTo(body, 0);
                at = exact.Length;
            }

            foreach (var chunk in chunks)
            {
                chunk.CopyTo(body, at);
                at += chunk.Length;
            }

            return body;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OpException("operation timed out");
        }
    }

    internal static int ResponseBodyEntryLimit() => EnvInt("POCKETCALCULATOR_NETWORK_BODY_BUFFER_ENTRIES", 128);

    internal static int ResponseBodyByteLimit() => EnvInt("POCKETCALCULATOR_NETWORK_BODY_BUFFER_BYTES", 2 * 1024 * 1024);

    /// <summary>
    /// Hard cap on a single JS fetch/XHR response body buffered fully in memory.
    /// </summary>
    /// <remarks>
    /// <c>op_fetch_url</c> reads the whole body, then makes a UTF-8 copy and a base64
    /// copy of it, so an unbounded body exhausts the process. This bounds the initial
    /// read; it is far larger than <see cref="ResponseBodyByteLimit"/> (which only
    /// decides whether a body is <em>cached</em>) because real page fetches can be large.
    /// </remarks>
    internal static int FetchMaxBodyBytes() => EnvInt("POCKETCALCULATOR_FETCH_MAX_BODY_BYTES", 100 * 1024 * 1024);

    internal static TimeSpan FetchTimeout() =>
        FetchTimeoutOverride.Value
        ?? TimeSpan.FromMilliseconds(EnvInt("POCKETCALCULATOR_FETCH_TIMEOUT_MS", 30_000));

    /// <summary>Test seam: a per-flow <see cref="FetchTimeout"/> that parallel tests cannot see.</summary>
    internal static readonly AsyncLocal<TimeSpan?> FetchTimeoutOverride = new();

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    /// <summary>
    /// <c>op_fetch_url</c>. The scripted fetch()/XHR transport.
    /// </summary>
    /// <remarks>
    /// A thrown <see cref="OpException"/> is the documented failure channel and
    /// reaches the shim as a rejected promise, exactly as <c>Err(JsErrorBox)</c> does
    /// in Rust; anything else is contained and converted into one.
    /// </remarks>
    public static async Task<string> OpFetchUrlAsync(
        PocketCalculatorState state,
        string url,
        string method,
        string headersJson,
        byte[] body,
        string origin,
        string mode,
        string credentials,
        bool internalLoad = false,
        PocketCalculatorState? document = null)
    {
        try
        {
            // `origin` is the shim's argument slot and is ignored: see FetchUrlAsync.
            _ = origin;
            return await FetchUrlAsync(
                    state, document ?? state, url, method, headersJson, body, mode, credentials, internalLoad)
                .ConfigureAwait(false);
        }
        catch (OpException)
        {
            throw;
        }
        catch (Microsoft.ClearScript.ScriptInterruptedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            throw new OpException(ex.Message);
        }
    }

    internal static async Task<string> FetchUrlAsync(
        PocketCalculatorState gs,
        PocketCalculatorState document,
        string url,
        string method,
        string headersJson,
        byte[] body,
        string mode,
        string credentials,
        bool internalLoad,
        bool hostConsumesBody = false)
    {
        ArgumentNullException.ThrowIfNull(gs);
        ArgumentNullException.ThrowIfNull(document);

        // DEVIATION from crates/obscura-js (ops.rs), which takes the request origin from
        // the op's `origin` argument. The shim computed it with the page's own `URL`
        // global, so a page that replaced `URL` could claim any origin and read another
        // site's credentialed responses. The origin is the calling realm's document
        // origin as the host knows it, and the argument is accepted and ignored so the
        // op keeps its shape.
        var origin = StateHelpers.DocumentOrigin(document);
        using FetchConcurrency.Slot slot = await FetchConcurrency.EnterAsync(gs).ConfigureAwait(false);

        // Page-script requests run under the Fetch request guards; the engine's own
        // loads do not. Rust applies neither (see FilterScriptRequestHeaders).
        if (!internalLoad)
        {
            mode = ScriptRequestMode(mode);

            // Fetch forbids CONNECT, TRACE and TRACK in any case; the shim throws
            // Chromium's TypeError first, and this holds when the op is reached without it.
            // Rust sent them.
            if (IsForbiddenMethod(method))
            {
                throw new OpException($"'{method}' HTTP method is unsupported.");
            }

            if (string.Equals(mode, "no-cors", StringComparison.Ordinal) && !NoCorsAllowsMethod(method))
            {
                throw new OpException($"'{method}' is unsupported in no-cors mode.");
            }
        }

        var requestHeaders = internalLoad
            ? ParseHeaders(headersJson)
            : FilterScriptRequestHeaders(
                ParseHeaders(headersJson),
                noCors: string.Equals(mode, "no-cors", StringComparison.Ordinal));

        var startedAtUnixMs = PerformanceOps.UnixMilliseconds();
        foreach (var pattern in gs.BlockedUrls)
        {
            if (string.Equals(pattern, "*", StringComparison.Ordinal)
                || url.Contains(pattern, StringComparison.Ordinal)
                || GlobMatch(pattern, url))
            {
                return Blocked(url, null);
            }
        }

        // Record the resource the page pulled in via fetch()/XHR so `--dump assets`
        // can list it. The URL is already absolute here.
        StateHelpers.PushCapped(gs.FetchedUrls, url, StateHelpers.MaxFetchedUrls);

        var jar = gs.CookieJar;
        var httpClient = gs.HttpClient;
        var callbacks = gs.Callbacks;
        var pageInFlight = gs.PageInFlight;

        (IInterceptSink Sink, string RequestId)? intercept = null;
        if (gs.InterceptEnabled && gs.InterceptTx is { } sink)
        {
            gs.InterceptCounter++;
            intercept = (sink, "intercept-" + gs.InterceptCounter.ToString(CultureInfo.InvariantCulture));
        }
        else if (gs.InterceptEnabled)
        {
            gs.InterceptCounter++;
        }

        // The private-network opt-in is a BrowserContext policy, not only a
        // process-wide environment setting. Navigation already honours the context's
        // configured HTTP client; scripted fetch/XHR must use the same policy for its
        // initial URL and every URL it can reach below.
        var allowPrivateNetwork = httpClient?.AllowPrivateNetwork ?? false;
        if (UrlRecord.Parse(url) is { } parsedInitial
            && ValidateFetchUrl(parsedInitial, allowPrivateNetwork) is { } initialError)
        {
            return Blocked(url, initialError);
        }

        pageInFlight.Increment();
        try
        {
            // Slots the interception channel can override via Continue so a consumer
            // can rewrite url/method/headers/body before the request goes out.
            string? overrideUrl = null;
            string? overrideMethod = null;
            IReadOnlyDictionary<string, string>? overrideHeaders = null;
            byte[]? overrideBody = null;

            if (intercept is { } channel)
            {
                var customHeaders = new Dictionary<string, string>(requestHeaders, StringComparer.Ordinal);
                var intercepted = new InterceptedRequest
                {
                    RequestId = channel.RequestId,
                    Url = url,
                    Method = method,
                    Headers = customHeaders,
                    ResourceType = "Fetch",
                };
                if (channel.Sink.TrySend(intercepted))
                {
                    var resolution = await intercepted.Resolver.Task.ConfigureAwait(false);
                    switch (resolution)
                    {
                        case InterceptResolution.Fulfill fulfill when internalLoad:
                            return InternalLoadResponse(
                                document,
                                mode,
                                fulfill.Status,
                                url,
                                url,
                                fulfill.Body,
                                tainted: RequestOrigin(url) is not { } fulfilledOrigin
                                    || !string.Equals(fulfilledOrigin, origin, StringComparison.Ordinal)
                                    || string.Equals(origin, "null", StringComparison.Ordinal),
                                redirected: false,
                                requestId: null,
                                fulfill.Headers,
                                hostConsumesBody);

                        case InterceptResolution.Fulfill fulfill:
                            return InterceptFulfillResponse(
                                fulfill.Status,
                                fulfill.Headers,
                                fulfill.Body,
                                url,
                                origin,
                                mode,
                                ParseCredentials(credentials),
                                internalLoad);

                        case InterceptResolution.Fail fail:
                            return Blocked(url, fail.Reason);

                        case InterceptResolution.Continue cont:
                            overrideUrl = cont.Url;
                            overrideMethod = cont.Method;
                            overrideHeaders = cont.Headers;
                            overrideBody = cont.Body is { } text ? Encoding.UTF8.GetBytes(text) : null;
                            break;
                    }
                }
            }

            // Apply interception overrides. A Continue rewrite of the URL must pass
            // the same SSRF / private-network gate as the original request and as
            // redirects; without this re-validation a rewrite to an internal address
            // would bypass the gate entirely.
            if (overrideUrl is { } rewritten)
            {
                if (UrlRecord.Parse(rewritten) is { } parsedRewrite
                    && ValidateFetchUrl(parsedRewrite, allowPrivateNetwork) is { } rewriteError)
                {
                    var sb = new StringBuilder(256);
                    sb.Append("{\"status\":0,\"body\":\"\",\"url\":");
                    SerdeJson.AppendString(sb, rewritten);
                    sb.Append(",\"blocked\":true,\"error\":");
                    SerdeJson.AppendString(sb, "Intercept rewrite to forbidden URL blocked: " + rewriteError);
                    sb.Append('}');
                    return sb.ToString();
                }

                url = rewritten;
            }

            method = overrideMethod ?? method;
            body = overrideBody ?? body;

            // SECURITY.md I7 (Rust has neither check). Everything this op loads (fetch,
            // XHR, iframe documents, dynamic scripts, linked stylesheets, workers) is
            // blockable mixed content from an https document; then HSTS moves a known
            // host to https, reported as a redirect the way Chromium's internal 307 is.
            if (MixedContentBlocked(httpClient, callbacks, document, url, mode) is { } mixedInitial)
            {
                return mixedInitial;
            }

            string? hstsUpgradedFrom = null;
            if (HstsUpgrade(httpClient, url) is { } secureInitial)
            {
                hstsUpgradedFrom = url;
                url = secureInitial;
            }

            var client = httpClient?.RequestClient ?? SharedRequestClient(allowPrivateNetwork);

            var initialRequestOrigin = RequestOrigin(url) ?? string.Empty;
            var pageOrigin = origin;
            var isCrossOrigin = pageOrigin.Length != 0
                && !string.Equals(initialRequestOrigin, pageOrigin, StringComparison.Ordinal);
            var credentialsMode = ParseCredentials(credentials);
            var reqMethod = ParseMethod(method);
            var customHeaders2 = overrideHeaders is not null
                ? new Dictionary<string, string>(overrideHeaders, StringComparer.Ordinal)
                : requestHeaders;

            // Passive request observation (non-blocking). Fires for every request that
            // reaches the network; Fulfill/Fail from the interception channel
            // short-circuits earlier.
            if (callbacks is not null && callbacks.HasRequestCallbacks()
                && Uri.TryCreate(url, UriKind.Absolute, out var callbackUri))
            {
                callbacks.FireRequest(new RequestInfo
                {
                    Url = callbackUri,
                    Method = method,
                    Headers = customHeaders2,
                    ResourceType = ResourceType.Fetch,
                });
            }

            // One deadline for the whole exchange: the preflight, every redirect hop and
            // the body. DEVIATION from crates/obscura-js (ops.rs), whose timeout covered
            // each hop's headers only, so a server that trickled the body one byte at a
            // time held the fetch, and the page's in-flight count, forever (SECURITY.md M3).
            using var deadline = new CancellationTokenSource(FetchTimeout());

            var isCors = string.Equals(mode, "cors", StringComparison.Ordinal);
            var unsafeHeaderNames = isCrossOrigin && isCors
                ? CorsUnsafeRequestHeaderNames(customHeaders2)
                : [];
            var needsPreflight = isCrossOrigin
                && isCors
                && (!IsCorsSafelistedMethod(reqMethod) || unsafeHeaderNames.Count != 0);

            if (needsPreflight)
            {
                HttpResponseMessage preflight;
                try
                {
                    using var preflightRequest = new HttpRequestMessage(HttpMethod.Options, url);
                    preflightRequest.Headers.TryAddWithoutValidation("Origin", pageOrigin);
                    preflightRequest.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
                    if (unsafeHeaderNames.Count != 0)
                    {
                        preflightRequest.Headers.TryAddWithoutValidation(
                            "Access-Control-Request-Headers",
                            string.Join(',', unsafeHeaderNames));
                    }

                    preflight = await SendAsync(client, preflightRequest, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OpException)
                {
                    throw new OpException($"CORS preflight failed: {ex.Message}");
                }

                using (preflight)
                {
                    // Fetch spec: the preflight response's status must be an ok status
                    // before its CORS headers are consulted (upstream #973).
                    var preflightStatus = (int)preflight.StatusCode;
                    if (preflightStatus is < 200 or > 299)
                    {
                        throw new OpException($"CORS preflight returned HTTP {StatusDisplay(preflightStatus)}");
                    }

                    var preflightHeaders = CollectHeaders(preflight);
                    var allowedOrigin = preflightHeaders.GetValueOrDefault(
                        "access-control-allow-origin", string.Empty);
                    var allowCredentials = preflightHeaders.GetValueOrDefault(
                        "access-control-allow-credentials", string.Empty);
                    if (!CorsResponseAllows(credentialsMode, pageOrigin, allowedOrigin, allowCredentials))
                    {
                        throw new OpException(
                            $"CORS preflight: Origin '{pageOrigin}' not allowed by "
                            + $"Access-Control-Allow-Origin '{allowedOrigin}'");
                    }

                    var allowedMethods = ParseCorsHeaderList(preflight, "Access-Control-Allow-Methods")
                        ?? throw new OpException(
                            "CORS preflight returned an invalid Access-Control-Allow-Methods value");
                    var allowedHeaders = ParseCorsHeaderList(preflight, "Access-Control-Allow-Headers")
                        ?? throw new OpException(
                            "CORS preflight returned an invalid Access-Control-Allow-Headers value");
                    var credentialed = credentialsMode == FetchCredentials.Include;
                    if (!PreflightAllowsMethod(reqMethod.Method, allowedMethods, credentialed))
                    {
                        throw new OpException($"CORS preflight did not allow method '{reqMethod.Method}'");
                    }

                    foreach (var name in unsafeHeaderNames)
                    {
                        if (!PreflightAllowsHeader(name, allowedHeaders, credentialed))
                        {
                            throw new OpException($"CORS preflight did not allow request header '{name}'");
                        }
                    }
                }
            }

            // Follow redirects manually so the SSRF policy applies to every hop. An
            // auto-following transport would bypass the gate on the redirect target
            // and let an allowed origin 302 to http://127.0.0.1.
            var currentUrl = url;
            var currentMethod = reqMethod;
            var currentBody = body;
            // A mutable copy applied per hop: credential headers are dropped when a
            // redirect crosses origin, and body headers when the method downgrades.
            var currentHeaders = new Dictionary<string, string>(customHeaders2, StringComparer.Ordinal);
            var redirectsFollowed = 0;
            List<Uri> redirectedFrom = [];
            if (hstsUpgradedFrom is not null && Uri.TryCreate(hstsUpgradedFrom, UriKind.Absolute, out var hstsFrom))
            {
                redirectedFrom.Add(hstsFrom);
                redirectsFollowed = 1;
            }
            var crossedOrigin = isCrossOrigin;
            var taintedOrigin = false;
            HttpResponseMessage? response = null;

            // An iframe's document load reaches this transport with mode "navigate".
            // Deviation: upstream loads the frame as a no-cors, same-origin-credentials
            // fetch, so a cross-origin frame got no cookies at all, even a same-site one,
            // and none of the navigation headers. Chromium sends it as a nested
            // navigation: fetch metadata with Sec-Fetch-Dest: iframe, the default
            // Referer, no Origin on a GET, and SameSite=None cookies only when it is
            // cross-site with the embedding document.
            var frameNavigation = string.Equals(mode, "navigate", StringComparison.Ordinal)
                && Uri.TryCreate(document.Url, UriKind.Absolute, out var frameInitiator)
                    ? ResourceRequest.FrameNavigation(frameInitiator)
                    : null;

            try
            {
                while (true)
                {
                    using var request = new HttpRequestMessage(currentMethod, currentUrl);
                    var currentIsCrossOrigin = RequestOrigin(currentUrl) is { } hopOrigin
                        && !string.Equals(hopOrigin, pageOrigin, StringComparison.Ordinal);
                    crossedOrigin |= currentIsCrossOrigin;

                    // Fetch main fetch: a same-origin request, or any hop of one, to
                    // another origin is a network error. Rust sent it and showed page
                    // script the response unfiltered.
                    if (currentIsCrossOrigin && string.Equals(mode, "same-origin", StringComparison.Ordinal))
                    {
                        return CorsBlocked(
                            currentUrl,
                            $"Request mode is 'same-origin' but the URL's origin is not same as "
                                + $"the request origin '{pageOrigin}'");
                    }

                    // Fetch "append a request Origin header", as Chromium sends it: on a
                    // CORS request to another origin, and on any request whose method is
                    // not GET or HEAD (a same-origin POST included). Deviation from Rust,
                    // which sent Origin on every cross-origin hop, no-cors GET and HEAD
                    // included, and never on a same-origin POST.
                    // After a redirect from one foreign origin to another the origin is
                    // tainted and serializes as "null".
                    if (frameNavigation is null
                        && ((isCors && crossedOrigin)
                            || (currentMethod != HttpMethod.Get && currentMethod != HttpMethod.Head)))
                    {
                        request.Headers.TryAddWithoutValidation("Origin", taintedOrigin ? "null" : pageOrigin);
                    }

                    if (frameNavigation is not null
                        && Uri.TryCreate(currentUrl, UriKind.Absolute, out var frameTarget))
                    {
                        foreach (var (name, value) in PocketCalculatorHttpClient.NavigationHeaders(
                                     frameNavigation, frameTarget, redirectedFrom))
                        {
                            request.Headers.TryAddWithoutValidation(name, value);
                        }
                    }

                    var credentialsAllowed = credentialsMode.Allows(pageOrigin, currentUrl);
                    if (credentialsAllowed
                        && jar is not null
                        && Uri.TryCreate(currentUrl, UriKind.Absolute, out var cookieUri))
                    {
                        var cookieHeader = jar.GetCookieHeaderInContext(
                            cookieUri,
                            frameNavigation is not null
                                ? PocketCalculatorHttpClient.NavigationSameSiteContext(
                                    frameNavigation,
                                    cookieUri,
                                    currentMethod == HttpMethod.Get || currentMethod == HttpMethod.Head,
                                    redirectedFrom)
                                : CookieJar.ContextForInitiator(pageOrigin, cookieUri));
                        if (cookieHeader.Length != 0)
                        {
                            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                        }
                    }

                    // Send the context's User-Agent on fetch()/XHR requests; UA-gated
                    // servers reject a request with none. Page script cannot replace it
                    // (FilterScriptRequestHeaders drops it, as Chromium does); an
                    // interception rewrite or an internal load's header still can.
                    var hasUserAgent = false;
                    foreach (var key in currentHeaders.Keys)
                    {
                        if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
                        {
                            hasUserAgent = true;
                            break;
                        }
                    }

                    if (!hasUserAgent)
                    {
                        request.Headers.TryAddWithoutValidation("User-Agent", httpClient?.UserAgent ?? DefaultUserAgent);
                    }

                    if (currentBody.Length != 0)
                    {
                        request.Content = new ByteArrayContent(currentBody);
                    }

                    foreach (var (key, value) in currentHeaders)
                    {
                        if (!request.Headers.TryAddWithoutValidation(key, value))
                        {
                            request.Content ??= new ByteArrayContent([]);
                            request.Content.Headers.TryAddWithoutValidation(key, value);
                        }
                    }

                    var hop = await SendAsync(client, request, deadline.Token).ConfigureAwait(false);

                    if (credentialsAllowed
                        && jar is not null
                        && Uri.TryCreate(currentUrl, UriKind.Absolute, out var setCookieUri)
                        && hop.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    {
                        foreach (var value in setCookies)
                        {
                            jar.SetCookie(value, setCookieUri);
                        }
                    }

                    var status = (int)hop.StatusCode;
                    if (status is < 300 or >= 400)
                    {
                        response = hop;
                        break;
                    }

                    // Fetch spec: the CORS check applies to every response in cors mode,
                    // not only the final one. A cross-origin redirect response must be
                    // authorized before it is followed (upstream #973).
                    if (isCors && currentIsCrossOrigin)
                    {
                        var hopHeaders = CollectHeaders(hop);
                        var hopAllowed = hopHeaders.GetValueOrDefault("access-control-allow-origin", string.Empty);
                        var hopAllowCredentials = hopHeaders.GetValueOrDefault(
                            "access-control-allow-credentials", string.Empty);
                        if (!CorsResponseAllows(credentialsMode, pageOrigin, hopAllowed, hopAllowCredentials))
                        {
                            hop.Dispose();
                            var sb = new StringBuilder(256);
                            sb.Append("{\"status\":0,\"body\":\"\",\"url\":");
                            SerdeJson.AppendString(sb, currentUrl);
                            sb.Append(",\"headers\":{},\"corsBlocked\":true,\"corsError\":");
                            SerdeJson.AppendString(
                                sb,
                                $"CORS error: cross-origin redirect from '{currentUrl}' not allowed by "
                                    + $"Access-Control-Allow-Origin '{hopAllowed}'");
                            sb.Append('}');
                            return sb.ToString();
                        }
                    }

                    string? location = hop.Headers.Location?.OriginalString;
                    if (location is null
                        && hop.Headers.TryGetValues("Location", out var locations))
                    {
                        foreach (var value in locations)
                        {
                            location = value;
                            break;
                        }
                    }

                    if (location is null)
                    {
                        // 3xx without a Location header is not actually a redirect.
                        response = hop;
                        break;
                    }

                    if (UrlRecord.Parse(currentUrl) is not { } baseUrl)
                    {
                        response = hop;
                        break;
                    }

                    if (baseUrl.Join(location) is not { } nextUrl)
                    {
                        response = hop;
                        break;
                    }

                    hop.Dispose();

                    // Re-validate every redirect target against the SSRF policy.
                    if (ValidateFetchUrl(nextUrl, allowPrivateNetwork) is { } redirectError)
                    {
                        return Blocked(nextUrl.Href, "Redirect to forbidden URL blocked: " + redirectError);
                    }

                    redirectsFollowed++;
                    if (redirectsFollowed > FetchRedirectLimit)
                    {
                        return Blocked(nextUrl.Href, $"Too many redirects (>{FetchRedirectLimit})");
                    }

                    // Deviation from Rust, which turns every 301/302/303 into a GET: Fetch
                    // (HTTP-redirect fetch, step 12) and Chromium downgrade 301/302 only for
                    // POST, and 303 for anything but GET/HEAD. A PUT that 302s stays a PUT
                    // with its body, and a HEAD that 303s stays a HEAD. 307/308 always
                    // preserve method and body.
                    var downgradedToGet = RedirectDowngradesToGet(status, currentMethod);
                    if (downgradedToGet)
                    {
                        currentMethod = HttpMethod.Get;
                        currentBody = [];
                    }

                    // Do not forward the caller's credentials to a different origin, and
                    // drop the body headers once the body is gone (upstream #967).
                    var crossesOrigin = !string.Equals(
                        baseUrl.AsciiOrigin, nextUrl.AsciiOrigin, StringComparison.Ordinal);
                    SanitizeRedirectHeaders(currentHeaders, crossesOrigin, downgradedToGet);
                    taintedOrigin |= crossesOrigin
                        && !string.Equals(baseUrl.AsciiOrigin, pageOrigin, StringComparison.Ordinal);

                    if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var from))
                    {
                        redirectedFrom.Add(from);
                    }

                    currentUrl = nextUrl.Href;
                    if (MixedContentBlocked(httpClient, callbacks, document, currentUrl, mode) is { } mixedHop)
                    {
                        return mixedHop;
                    }

                    if (HstsUpgrade(httpClient, currentUrl) is { } secureHop)
                    {
                        if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var insecureHop))
                        {
                            redirectedFrom.Add(insecureHop);
                        }

                        currentUrl = secureHop;
                    }
                }

                var redirected = redirectsFollowed > 0;
                var finalStatus = (int)response.StatusCode;
                var respHeaders = CollectHeaders(response);

                var finalIsCrossOrigin = RequestOrigin(currentUrl) is { } finalOrigin
                    && !string.Equals(finalOrigin, pageOrigin, StringComparison.Ordinal);
                if (finalIsCrossOrigin && string.Equals(mode, "cors", StringComparison.Ordinal))
                {
                    var allowed = respHeaders.GetValueOrDefault("access-control-allow-origin", string.Empty);
                    var allowCredentials = respHeaders.GetValueOrDefault(
                        "access-control-allow-credentials", string.Empty);
                    if (!CorsResponseAllows(credentialsMode, pageOrigin, allowed, allowCredentials))
                    {
                        var message = credentialsMode == FetchCredentials.Include
                            ? "CORS error: credentialed request requires Access-Control-Allow-Origin "
                                + $"'{pageOrigin}' and Access-Control-Allow-Credentials 'true'"
                            : $"CORS error: Origin '{pageOrigin}' not in Access-Control-Allow-Origin '{allowed}'";
                        var sb = new StringBuilder(256);
                        sb.Append("{\"status\":0,\"body\":\"\",\"url\":");
                        SerdeJson.AppendString(sb, url);
                        sb.Append(",\"headers\":{},\"corsBlocked\":true,\"corsError\":");
                        SerdeJson.AppendString(sb, message);
                        sb.Append('}');
                        return sb.ToString();
                    }
                }

                var respBytes = await ReadBodyCappedAsync(response, FetchMaxBodyBytes(), deadline.Token)
                    .ConfigureAwait(false);
                // Decoded only where a string is kept (a small stored body, an internal
                // load); the script-facing JSON is written from the bytes (M4).
                string? respBody = null;

                if (callbacks is not null && callbacks.HasResponseCallbacks())
                {
                    var responseUri = Uri.TryCreate(currentUrl, UriKind.Absolute, out var parsedFinal)
                        ? parsedFinal
                        : new Uri("http://0.0.0.0/");
                    var built = new Response
                    {
                        Url = responseUri,
                        Status = finalStatus,
                        Headers = respHeaders,
                        Body = respBytes,
                        RedirectedFrom = redirectedFrom,
                    };
                    callbacks.FireResponse(
                        new RequestInfo
                        {
                            Url = responseUri,
                            Method = currentMethod.Method,
                            Headers = respHeaders,
                            ResourceType = ResourceType.Fetch,
                        },
                        built);
                }

                gs.NetworkResponseBodyCounter++;
                var requestId = "fetch-" + gs.NetworkResponseBodyCounter.ToString(CultureInfo.InvariantCulture);
                var maxEntries = ResponseBodyEntryLimit();
                var maxBytes = ResponseBodyByteLimit();
                if (maxEntries > 0 && maxBytes > 0 && respBytes.Length <= maxBytes)
                {
                    respBody ??= Encoding.UTF8.GetString(respBytes);
                    gs.NetworkResponseBodies[requestId] = new StoredNetworkResponseBody(respBody, false);
                    gs.NetworkResponseBodyOrder.Enqueue(requestId);
                    while (gs.NetworkResponseBodyOrder.Count > maxEntries)
                    {
                        gs.NetworkResponseBodies.Remove(gs.NetworkResponseBodyOrder.Dequeue());
                    }
                }

                // Record a network event so the CDP layer emits requestWillBeSent /
                // responseReceived for this script-initiated request, keyed by the same
                // fetch-{N} id as the stored body so Network.getResponseBody resolves.
                gs.JsNetworkEvents.Add(new JsNetworkEvent
                {
                    RequestId = requestId,
                    Url = currentUrl,
                    Method = currentMethod.Method,
                    Status = finalStatus,
                    ResponseHeaders = respHeaders,
                    BodySize = respBytes.Length,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                });
                if (gs.JsNetworkEvents.Count > MaxJsNetworkEvents)
                {
                    gs.JsNetworkEvents.RemoveRange(0, gs.JsNetworkEvents.Count - MaxJsNetworkEvents);
                }

                // The one request the page can see go out, so it is also the one whose
                // start the shim could have measured itself. Recording it here keeps
                // fetch()/XHR on the same timeline as the host-owned subresources, and
                // the start is the real one rather than the resolution of the promise.
                PerformanceOps.RecordResourceTiming(
                    gs,
                    new ResourceTimingRecord
                    {
                        Url = currentUrl,
                        InitiatorType = "fetch",
                        Status = finalStatus,
                        StartedAtUnixMs = startedAtUnixMs,
                        EndedAtUnixMs = PerformanceOps.UnixMilliseconds(),
                        DecodedBodySize = respBytes.Length,
                        EncodedBodySize = PerformanceOps.EncodedBodySize(respHeaders, respBytes.Length),
                        ContentType = respHeaders.GetValueOrDefault("content-type", string.Empty),
                    });

                if (internalLoad)
                {
                    return InternalLoadResponse(
                        document,
                        mode,
                        finalStatus,
                        url,
                        currentUrl,
                        respBody ?? Encoding.UTF8.GetString(respBytes),
                        tainted: crossedOrigin || string.Equals(pageOrigin, "null", StringComparison.Ordinal),
                        redirected,
                        requestId,
                        VisibleResponseHeaders(respHeaders, finalIsCrossOrigin, credentialsMode),
                        hostConsumesBody);
                }

                // Page script sees an opaque no-cors response as status 0 with no body
                // and no headers; the engine's own subresource loads (internalLoad) still
                // get the body. Set-Cookie and unexposed cross-origin headers never
                // reach script (upstream 04418a5). The CDP-facing records above keep
                // the full response.
                var opaque = !internalLoad
                    && string.Equals(mode, "no-cors", StringComparison.Ordinal)
                    && crossedOrigin;
                var scriptHeaders = opaque
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : VisibleResponseHeaders(respHeaders, finalIsCrossOrigin, credentialsMode);

                return ScriptResponseJson(
                    StatusText(opaque, finalStatus),
                    respBytes,
                    requestId,
                    currentUrl,
                    redirected,
                    opaque,
                    scriptHeaders);
            }
            finally
            {
                response?.Dispose();
            }
        }
        finally
        {
            pageInFlight.Decrement();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken deadline)
    {
        try
        {
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new OpException("operation timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new OpException(ex.Message);
        }
    }

    /// <summary>
    /// What an internal load (<c>internalLoad</c>: a dynamic script, a frame document, a
    /// stylesheet) returns to the shim.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-js (ops.rs), which returns every internal load's body,
    /// cross-origin ones included, to page-reachable JSON. Here the body is kept host-side
    /// behind <c>bodyToken</c> (see <see cref="InternalLoads"/>) and appears in the JSON only
    /// for a frame document that is same-origin with the requesting document, which page
    /// script could have fetched itself. A cross-origin response also keeps its redirect target and
    /// headers to itself. <c>sameOrigin</c> is the host's own verdict. <c>bodyBase64</c> is
    /// never sent: nothing that reads an internal load uses it.
    /// </remarks>
    private static string InternalLoadResponse(
        PocketCalculatorState document,
        string mode,
        int status,
        string requestUrl,
        string finalUrl,
        string bodyText,
        bool tainted,
        bool redirected,
        string? requestId,
        IReadOnlyDictionary<string, string> headers,
        bool hostConsumesBody)
    {
        var token = InternalLoads.Put(
            document, new InternalLoad(mode, status, requestUrl, finalUrl, bodyText, tainted));
        // Only a frame document is read back by the shim (its parent-side copy for a
        // same-origin contentDocument); a script or a sheet is run or installed by the host.
        var visible = !tainted && !hostConsumesBody && string.Equals(mode, "navigate", StringComparison.Ordinal);
        var result = new StringBuilder((visible ? bodyText.Length : 0) + 256);
        result.Append("{\"status\":").Append(status.ToString(CultureInfo.InvariantCulture));
        result.Append(",\"body\":");
        SerdeJson.AppendString(result, visible ? bodyText : string.Empty);
        if (requestId is not null)
        {
            result.Append(",\"requestId\":");
            SerdeJson.AppendString(result, requestId);
        }

        result.Append(",\"url\":");
        SerdeJson.AppendString(result, tainted ? requestUrl : finalUrl);
        result.Append(",\"redirected\":").Append(!tainted && redirected ? "true" : "false");
        result.Append(",\"opaque\":false");
        result.Append(",\"sameOrigin\":").Append(tainted ? "false" : "true");
        result.Append(",\"bodyToken\":").Append(token.ToString(CultureInfo.InvariantCulture));
        result.Append(",\"headers\":");
        AppendHeaders(result, tainted ? new Dictionary<string, string>(StringComparer.Ordinal) : headers);
        result.Append('}');
        return result.ToString();
    }

    /// <summary>
    /// The blocked response for a mixed-content request from <paramref name="documentUrl"/>
    /// to <paramref name="target"/>, or null when it may go out. It reuses the network
    /// error shape a CORS failure has, so fetch() rejects with a TypeError as in Chromium,
    /// and the page's console gets Chromium's message.
    /// </summary>
    private static string? MixedContentBlocked(
        PocketCalculatorHttpClient? httpClient,
        CallbackRegistry? callbacks,
        PocketCalculatorState state,
        string target,
        string mode)
    {
        if ((httpClient?.AllowInsecureContent ?? MixedContent.EnvAllowsInsecureContent())
            || !Uri.TryCreate(target, UriKind.Absolute, out var targetUri)
            || MixedContentContext(state) is not { } document)
        {
            return null;
        }

        var frame = string.Equals(mode, "navigate", StringComparison.Ordinal);
        var type = frame ? ResourceType.Document : ResourceType.Fetch;
        if (MixedContent.Check(document, targetUri, type, topLevelNavigation: false) == MixedContentDecision.Allow)
        {
            return null;
        }

        var message = MixedContent.BlockedMessage(
            document.AbsoluteUri, MixedContent.RequestKind(type, nestedDocument: frame), targetUri.AbsoluteUri);
        callbacks?.FireConsole("error", message);
        return CorsBlocked(target, message);
    }

    /// <summary>
    /// The document mixed content is decided against for a request from
    /// <paramref name="state"/>: the document itself, or for a frame that is not secure
    /// (srcdoc, about:blank, http) its nearest secure ancestor, as Chromium checks the
    /// top frame too. The I7 fix checked only the calling document.
    /// </summary>
    internal static Uri? MixedContentContext(PocketCalculatorState state)
    {
        Uri.TryCreate(state.Url, UriKind.Absolute, out var document);
        Uri? ancestor = state.SecureAncestorUrl is { } secure && Uri.TryCreate(secure, UriKind.Absolute, out var parsed)
            ? parsed
            : null;
        return MixedContent.Context(document, ancestor);
    }

    /// <summary>
    /// Chromium's check in the WebSocket constructor: a ws: URL from a secure context is
    /// blocked (SecurityError), with a console error. Returns that message, or the empty
    /// string when the socket may be created.
    /// </summary>
    internal static string WebSocketMixedContent(PocketCalculatorState state, string url)
    {
        if ((state.HttpClient?.AllowInsecureContent ?? MixedContent.EnvAllowsInsecureContent())
            || !Uri.TryCreate(url, UriKind.Absolute, out var target)
            || MixedContentContext(state) is not { } document
            || MixedContent.Check(document, target, ResourceType.Other, topLevelNavigation: false)
                == MixedContentDecision.Allow)
        {
            return string.Empty;
        }

        var message = MixedContent.BlockedWebSocketMessage(document.AbsoluteUri, target.AbsoluteUri);
        state.Callbacks?.FireConsole("error", message);
        return message;
    }

    private static string? HstsUpgrade(PocketCalculatorHttpClient? httpClient, string url) =>
        httpClient is not null
        && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && httpClient.Hsts.Upgrade(parsed) is { } secure
            ? secure.AbsoluteUri
            : null;

    private static string CorsBlocked(string url, string error)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"status\":0,\"body\":\"\",\"url\":");
        SerdeJson.AppendString(sb, url);
        sb.Append(",\"headers\":{},\"corsBlocked\":true,\"corsError\":");
        SerdeJson.AppendString(sb, error);
        sb.Append('}');
        return sb.ToString();
    }

    private static string Blocked(string url, string? error)
    {
        var sb = new StringBuilder(192);
        sb.Append("{\"status\":0,\"body\":\"\",\"url\":");
        SerdeJson.AppendString(sb, url);
        sb.Append(",\"headers\":{},\"blocked\":true");
        if (error is not null)
        {
            sb.Append(",\"error\":");
            SerdeJson.AppendString(sb, error);
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendHeaders(StringBuilder sb, IReadOnlyDictionary<string, string> headers)
    {
        sb.Append('{');
        var first = true;
        foreach (var (key, value) in headers)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            SerdeJson.AppendString(sb, key);
            sb.Append(':');
            SerdeJson.AppendString(sb, value);
        }

        sb.Append('}');
    }

    /// <summary>
    /// Response headers, lower-cased, last value winning for a repeated name. The
    /// Rust transport normalizes header names to lower case and collects them into a
    /// map, and the CORS checks below index that map with lower-case keys.
    /// </summary>
    private static Dictionary<string, string> CollectHeaders(HttpResponseMessage response)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        foreach (var (name, values) in response.Headers)
        {
            foreach (var value in values)
            {
                headers[name.ToLowerInvariant()] = value;
            }
        }

        foreach (var (name, values) in response.Content.Headers)
        {
            foreach (var value in values)
            {
                headers[name.ToLowerInvariant()] = value;
            }
        }

        return headers;
    }

    private static Dictionary<string, string> ParseHeaders(string json)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return headers;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    headers[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return headers;
    }

    /// <summary>Fetch's forbidden methods: <c>CONNECT</c>, <c>TRACE</c> and <c>TRACK</c>, in any case.</summary>
    internal static bool IsForbiddenMethod(string method) =>
        method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase)
        || method.Equals("TRACE", StringComparison.OrdinalIgnoreCase)
        || method.Equals("TRACK", StringComparison.OrdinalIgnoreCase);

    private static HttpMethod ParseMethod(string method)
    {
        try
        {
            return HttpMethod.Parse(method);
        }
        catch (FormatException)
        {
            return HttpMethod.Get;
        }
    }

    /// <summary>
    /// Fallback transport for runtimes with no owning <see cref="PocketCalculatorHttpClient"/>,
    /// such as a standalone module loader. Browser pages use their context-scoped
    /// client so sequential runtimes never share an async network pool.
    /// </summary>
    private static HttpClient SharedRequestClient(bool allowPrivateNetwork)
    {
        lock (FallbackLock)
        {
            if (_fallbackClient is { } existing && _fallbackAllowsPrivate == allowPrivateNetwork)
            {
                return existing;
            }

            var owner = new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork);
            _fallbackOwner?.Dispose();
            _fallbackOwner = owner;
            _fallbackAllowsPrivate = allowPrivateNetwork;
            _fallbackClient = owner.RequestClient;
            return _fallbackClient;
        }
    }

    private static readonly Lock FallbackLock = new();
    private static PocketCalculatorHttpClient? _fallbackOwner;
    private static HttpClient? _fallbackClient;
    private static bool _fallbackAllowsPrivate;
}
