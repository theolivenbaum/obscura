using System.Globalization;
using System.Net;
using System.Text;
using Obscura.Js.Url;
using Obscura.Net;

namespace Obscura.Js.Ops;

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
public static class FetchOps
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
    internal static async Task<byte[]> ReadBodyCappedAsync(HttpResponseMessage response, int max)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Content.Headers.ContentLength is { } length && length > max)
        {
            throw new OpException($"response body of {length} bytes exceeds the maximum of {max}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > max)
            {
                throw new OpException($"response body exceeds the maximum of {max} bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    internal static int ResponseBodyEntryLimit() => EnvInt("OBSCURA_NETWORK_BODY_BUFFER_ENTRIES", 128);

    internal static int ResponseBodyByteLimit() => EnvInt("OBSCURA_NETWORK_BODY_BUFFER_BYTES", 2 * 1024 * 1024);

    /// <summary>
    /// Hard cap on a single JS fetch/XHR response body buffered fully in memory.
    /// </summary>
    /// <remarks>
    /// <c>op_fetch_url</c> reads the whole body, then makes a UTF-8 copy and a base64
    /// copy of it, so an unbounded body exhausts the process. This bounds the initial
    /// read; it is far larger than <see cref="ResponseBodyByteLimit"/> (which only
    /// decides whether a body is <em>cached</em>) because real page fetches can be large.
    /// </remarks>
    internal static int FetchMaxBodyBytes() => EnvInt("OBSCURA_FETCH_MAX_BODY_BYTES", 100 * 1024 * 1024);

    internal static TimeSpan FetchTimeout() =>
        TimeSpan.FromMilliseconds(EnvInt("OBSCURA_FETCH_TIMEOUT_MS", 30_000));

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
        ObscuraState state,
        string url,
        string method,
        string headersJson,
        byte[] body,
        string origin,
        string mode,
        string credentials)
    {
        try
        {
            return await FetchUrlAsync(state, url, method, headersJson, body, origin, mode, credentials)
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

    private static async Task<string> FetchUrlAsync(
        ObscuraState gs,
        string url,
        string method,
        string headersJson,
        byte[] body,
        string origin,
        string mode,
        string credentials)
    {
        ArgumentNullException.ThrowIfNull(gs);
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
                var customHeaders = ParseHeaders(headersJson);
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
                        case InterceptResolution.Fulfill fulfill:
                        {
                            var sb = new StringBuilder(256);
                            sb.Append("{\"status\":")
                                .Append(fulfill.Status.ToString(CultureInfo.InvariantCulture));
                            sb.Append(",\"body\":");
                            SerdeJson.AppendString(sb, fulfill.Body);
                            sb.Append(",\"url\":");
                            SerdeJson.AppendString(sb, url);
                            sb.Append(",\"headers\":");
                            AppendHeaders(sb, fulfill.Headers);
                            sb.Append('}');
                            return sb.ToString();
                        }

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

            var client = httpClient?.RequestClient ?? SharedRequestClient(allowPrivateNetwork);

            var initialRequestOrigin = RequestOrigin(url) ?? string.Empty;
            var pageOrigin = origin.Length == 0 ? initialRequestOrigin : origin;
            var isCrossOrigin = pageOrigin.Length != 0
                && !string.Equals(initialRequestOrigin, pageOrigin, StringComparison.Ordinal);
            var credentialsMode = ParseCredentials(credentials);
            var reqMethod = ParseMethod(method);
            var customHeaders2 = overrideHeaders is not null
                ? new Dictionary<string, string>(overrideHeaders, StringComparer.Ordinal)
                : ParseHeaders(headersJson);

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

            var needsPreflight = isCrossOrigin
                && string.Equals(mode, "cors", StringComparison.Ordinal)
                && ((reqMethod != HttpMethod.Get && reqMethod != HttpMethod.Head && reqMethod != HttpMethod.Post)
                    || HasNonSimpleHeader(customHeaders2));

            if (needsPreflight)
            {
                HttpResponseMessage preflight;
                try
                {
                    using var preflightRequest = new HttpRequestMessage(HttpMethod.Options, url);
                    preflightRequest.Headers.TryAddWithoutValidation("Origin", pageOrigin);
                    preflightRequest.Headers.TryAddWithoutValidation("Access-Control-Request-Method", method);
                    preflightRequest.Headers.TryAddWithoutValidation(
                        "Access-Control-Request-Headers",
                        string.Join(", ", customHeaders2.Keys));
                    preflight = await SendAsync(client, preflightRequest).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OpException)
                {
                    throw new OpException($"CORS preflight failed: {ex.Message}");
                }

                using (preflight)
                {
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
                }
            }

            // Follow redirects manually so the SSRF policy applies to every hop. An
            // auto-following transport would bypass the gate on the redirect target
            // and let an allowed origin 302 to http://127.0.0.1.
            var currentUrl = url;
            var currentMethod = reqMethod;
            var currentBody = body;
            var redirectsFollowed = 0;
            List<Uri> redirectedFrom = [];
            var crossedOrigin = isCrossOrigin;
            HttpResponseMessage? response = null;

            try
            {
                while (true)
                {
                    using var request = new HttpRequestMessage(currentMethod, currentUrl);
                    var currentIsCrossOrigin = RequestOrigin(currentUrl) is { } hopOrigin
                        && !string.Equals(hopOrigin, pageOrigin, StringComparison.Ordinal);
                    crossedOrigin |= currentIsCrossOrigin;
                    if (currentIsCrossOrigin)
                    {
                        request.Headers.TryAddWithoutValidation("Origin", pageOrigin);
                    }

                    var credentialsAllowed = credentialsMode.Allows(pageOrigin, currentUrl);
                    if (credentialsAllowed
                        && jar is not null
                        && Uri.TryCreate(currentUrl, UriKind.Absolute, out var cookieUri))
                    {
                        var cookieHeader = jar.GetCookieHeader(cookieUri);
                        if (cookieHeader.Length != 0)
                        {
                            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                        }
                    }

                    // Send a default User-Agent on fetch()/XHR requests; UA-gated
                    // servers reject a request with none. Honor an explicit override.
                    var hasUserAgent = false;
                    foreach (var key in customHeaders2.Keys)
                    {
                        if (key.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
                        {
                            hasUserAgent = true;
                            break;
                        }
                    }

                    if (!hasUserAgent)
                    {
                        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    }

                    if (currentBody.Length != 0)
                    {
                        request.Content = new ByteArrayContent(currentBody);
                    }

                    foreach (var (key, value) in customHeaders2)
                    {
                        if (!request.Headers.TryAddWithoutValidation(key, value))
                        {
                            request.Content ??= new ByteArrayContent([]);
                            request.Content.Headers.TryAddWithoutValidation(key, value);
                        }
                    }

                    var hop = await SendAsync(client, request).ConfigureAwait(false);

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

                    // Browser semantics: 301/302/303 downgrade to GET with no body.
                    // 307/308 preserve method and body.
                    if (status is 301 or 302 or 303)
                    {
                        currentMethod = HttpMethod.Get;
                        currentBody = [];
                    }

                    if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var from))
                    {
                        redirectedFrom.Add(from);
                    }

                    currentUrl = nextUrl.Href;
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

                var respBytes = await ReadBodyCappedAsync(response, FetchMaxBodyBytes()).ConfigureAwait(false);
                var respBody = Encoding.UTF8.GetString(respBytes);
                var respBodyBase64 = Convert.ToBase64String(respBytes);

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

                var result = new StringBuilder(respBody.Length + respBodyBase64.Length + 256);
                result.Append("{\"status\":").Append(finalStatus.ToString(CultureInfo.InvariantCulture));
                result.Append(",\"body\":");
                SerdeJson.AppendString(result, respBody);
                result.Append(",\"bodyBase64\":");
                SerdeJson.AppendString(result, respBodyBase64);
                result.Append(",\"requestId\":");
                SerdeJson.AppendString(result, requestId);
                result.Append(",\"url\":");
                SerdeJson.AppendString(result, currentUrl);
                result.Append(",\"redirected\":").Append(redirected ? "true" : "false");
                result.Append(",\"opaque\":").Append(
                    string.Equals(mode, "no-cors", StringComparison.Ordinal) && crossedOrigin
                        ? "true"
                        : "false");
                result.Append(",\"headers\":");
                AppendHeaders(result, respHeaders);
                result.Append('}');
                return result.ToString();
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
    {
        using var timeout = new CancellationTokenSource(FetchTimeout());
        try
        {
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new OpException("operation timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new OpException(ex.Message);
        }
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

    private static bool HasNonSimpleHeader(Dictionary<string, string> headers)
    {
        foreach (var key in headers.Keys)
        {
            var lower = key.ToLowerInvariant();
            if (!string.Equals(lower, "accept", StringComparison.Ordinal)
                && !string.Equals(lower, "accept-language", StringComparison.Ordinal)
                && !string.Equals(lower, "content-language", StringComparison.Ordinal)
                && !string.Equals(lower, "content-type", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
    /// Fallback transport for runtimes with no owning <see cref="ObscuraHttpClient"/>,
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

            var owner = new ObscuraHttpClient(new CookieJar(), null, allowPrivateNetwork);
            _fallbackOwner?.Dispose();
            _fallbackOwner = owner;
            _fallbackAllowsPrivate = allowPrivateNetwork;
            _fallbackClient = owner.RequestClient;
            return _fallbackClient;
        }
    }

    private static readonly Lock FallbackLock = new();
    private static ObscuraHttpClient? _fallbackOwner;
    private static HttpClient? _fallbackClient;
    private static bool _fallbackAllowsPrivate;
}
