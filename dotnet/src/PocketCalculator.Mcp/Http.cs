using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PocketCalculator.Mcp;

/// <summary>
/// MCP Streamable HTTP transport (POST /mcp -> JSON response).
/// </summary>
/// <remarks>
/// The HTTP framing is hand-written for the same reason the JSON-RPC framing is:
/// the exact status lines, header set and CORS behavior are what a browser MCP
/// client and the regression tests depend on.
/// </remarks>
public static class Http
{
    /// <summary>
    /// Hard cap on a single MCP request body. The client-supplied
    /// <c>Content-Length</c> is used to pre-size the read buffer; without a ceiling
    /// a request advertising e.g. <c>Content-Length: 4294967296</c> makes the server
    /// allocate and zero-fill that many bytes before reading any body - an
    /// unauthenticated OOM/DoS. One MiB is far above any real JSON-RPC tool call.
    /// </summary>
    internal const int MaxBodyBytes = 1024 * 1024;

    /// <summary>Cap on the request line, its line feed included.</summary>
    internal const int MaxRequestLineBytes = 8 * 1024;

    /// <summary>Cap on one header line, its line feed included.</summary>
    internal const int MaxHeaderLineBytes = 16 * 1024;

    /// <summary>Cap on the whole header block.</summary>
    internal const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Largest JSON-RPC batch. An empty batch is refused as well.</summary>
    internal const int MaxBatchItems = 64;

    /// <summary>Largest serialized JSON-RPC <c>id</c>.</summary>
    internal const int MaxIdBytes = 1024;

    /// <summary>Live connections, open SSE streams included. One more is closed at accept.</summary>
    internal const int MaxConnections = 128;

    /// <summary>
    /// Open SSE streams. A stream holds its connection slot for as long as the
    /// client keeps it open, so this keeps streams from taking every slot.
    /// </summary>
    internal const int MaxSseStreams = 16;

    /// <summary>Requests queued for the single dispatcher that owns the browser session.</summary>
    internal const int MaxPendingRequests = 32;

    /// <summary>Shortest bearer token <c>POCKETCALCULATOR_MCP_TOKEN</c> may hold, in bytes.</summary>
    internal const int MinTokenBytes = 32;

    /// <summary>
    /// Maximum time allowed to receive one complete HTTP request (request line,
    /// headers, and body). The deadline is shared across all reads so a client
    /// cannot keep a connection slot occupied by slowly dribbling data.
    /// </summary>
    internal static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often an open SSE stream is sent a keep-alive comment.</summary>
    internal static readonly TimeSpan SsePingInterval = TimeSpan.FromSeconds(15);

    /// <summary>The default idle timeout for SSE streams: 30 minutes.</summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long an SSE stream stays open with no MCP request on the server:
    /// <c>POCKETCALCULATOR_MCP_IDLE_TIMEOUT_MS</c> (0 disables), or
    /// <see cref="DefaultIdleTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Deviation (SECURITY.md L3): upstream pings an SSE stream forever, so a client
    /// that opened one and went away without closing held its slot for the life of the
    /// process. A keep-alive connection already closes after
    /// <see cref="RequestReadTimeout"/> with no request; an SSE stream's client never
    /// sends on it, so its idle clock is the server's: any request on any connection
    /// resets it. MCP clients reconnect a closed stream.
    /// </remarks>
    internal static TimeSpan IdleTimeoutFromEnv()
    {
        var configured = Environment.GetEnvironmentVariable("POCKETCALCULATOR_MCP_IDLE_TIMEOUT_MS");
        return long.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? TimeSpan.FromMilliseconds(value)
            : DefaultIdleTimeout;
    }

    /// <summary>Server options the CLI and tests set; defaults come from the environment.</summary>
    internal sealed record ServerOptions
    {
        /// <summary>SSE idle timeout (<see cref="IdleTimeoutFromEnv"/>); zero disables it.</summary>
        public TimeSpan IdleTimeout { get; init; } = IdleTimeoutFromEnv();
    }

    /// <summary>When the server last read a request, on any connection.</summary>
    private sealed class ServerActivity
    {
        private long _last = Environment.TickCount64;

        internal void Touch() => Volatile.Write(ref _last, Environment.TickCount64);

        internal long IdleMs => Environment.TickCount64 - Volatile.Read(ref _last);
    }

    internal enum RequestBodyKind
    {
        NotRead,
        MissingLength,
        TooLarge,
        Read,
    }

    internal sealed record RequestBody(RequestBodyKind Kind, byte[]? Bytes)
    {
        internal static readonly RequestBody NotRead = new(RequestBodyKind.NotRead, null);
        internal static readonly RequestBody MissingLength = new(RequestBodyKind.MissingLength, null);
        internal static readonly RequestBody TooLarge = new(RequestBodyKind.TooLarge, null);

        internal static RequestBody Read(byte[] bytes) => new(RequestBodyKind.Read, bytes);
    }

    internal sealed record HttpRequestLine(
        string Method,
        string Path,
        bool AcceptSse,
        bool KeepAlive,
        string? Origin,
        bool Authorized,
        bool ContentTypeIsJson,
        RequestBody Body)
    {
        /// <summary>The <c>Host</c> header, or null when the request carried none.</summary>
        public string? Host { get; init; }
    }

    internal enum RequestReadKind
    {
        Closed,
        Invalid,
        Request,
    }

    internal sealed record RequestRead(RequestReadKind Kind, HttpRequestLine? Request)
    {
        internal static readonly RequestRead Closed = new(RequestReadKind.Closed, null);
        internal static readonly RequestRead Invalid = new(RequestReadKind.Invalid, null);

        internal static RequestRead Of(HttpRequestLine request) => new(RequestReadKind.Request, request);
    }

    /// <summary>Raised where the Rust transport returns <c>Err</c> from a read.</summary>
    internal sealed class HttpTransportException(string message) : Exception(message);

    /// <summary>One request queued for the dispatcher, and where its reply goes.</summary>
    private sealed record PendingRequest(byte[] Body, TaskCompletionSource<JsonNode?> Reply);

    /// <summary>
    /// The bearer token from <c>POCKETCALCULATOR_MCP_TOKEN</c>. Unset or empty means no
    /// token; one shorter than <see cref="MinTokenBytes"/> is a startup error.
    /// </summary>
    internal static string? TokenFromEnv() =>
        ValidateToken(Environment.GetEnvironmentVariable("POCKETCALCULATOR_MCP_TOKEN"));

    internal static string? ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(token) < MinTokenBytes)
        {
            throw new InvalidOperationException("POCKETCALCULATOR_MCP_TOKEN must be at least 32 bytes");
        }

        return token;
    }

    /// <summary>
    /// Whether an <c>Authorization</c> value carries the expected bearer token. With
    /// no token configured every request is authorized. The comparison does not
    /// short-circuit on the first differing byte.
    /// </summary>
    internal static bool BearerAuthorized(string? header, string? expected)
    {
        if (expected is null)
        {
            return true;
        }

        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(header["Bearer ".Length..]),
            Encoding.UTF8.GetBytes(expected));
    }

    internal static async Task<RequestRead> ReadRequestAsync(
        LineReader reader,
        string? allowedOrigins,
        string? authToken,
        CancellationToken cancellationToken)
    {
        var requestLineRaw = await reader.ReadLineAsync(MaxRequestLineBytes, cancellationToken)
            .ConfigureAwait(false);
        if (requestLineRaw is null)
        {
            return RequestRead.Closed;
        }

        var requestLine = requestLineRaw.Trim();
        if (requestLine.Length == 0)
        {
            return RequestRead.Closed;
        }

        var parts = requestLine.Split(' ', 3);
        if (parts.Length < 3)
        {
            return RequestRead.Invalid;
        }

        var method = parts[0];
        var path = parts[1];

        int? contentLength = null;
        var acceptSse = false;
        var keepAlive = false;
        string? origin = null;
        string? authorization = null;
        string? hostHeader = null;
        var contentTypeIsJson = false;
        var headerBytes = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(MaxHeaderLineBytes, cancellationToken)
                .ConfigureAwait(false);
            if (line is null)
            {
                return RequestRead.Invalid;
            }

            headerBytes += reader.LastLineBytes;
            if (headerBytes > MaxHeaderBytes)
            {
                throw new HttpTransportException("HTTP headers too large");
            }

            var trimmed = line;
            if (trimmed.Length == 0)
            {
                break;
            }

            var lower = trimmed.ToLowerInvariant();
            if (lower.StartsWith("content-length: ", StringComparison.Ordinal))
            {
                var value = lower["content-length: ".Length..].Trim();
                contentLength = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
                // A value that does not fit a machine word is not a length serde
                // could parse either; leaving it None means "missing Content-Length",
                // which is the same 400 the Rust server answers.
                if (contentLength is null && ulong.TryParse(
                        value, NumberStyles.None, CultureInfo.InvariantCulture, out var wide)
                    && wide > MaxBodyBytes)
                {
                    contentLength = int.MaxValue;
                }
            }

            if (lower.StartsWith("origin:", StringComparison.Ordinal))
            {
                var idx = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (idx >= 0)
                {
                    origin = trimmed[(idx + 1)..].Trim();
                }
            }

            if (lower.StartsWith("host:", StringComparison.Ordinal))
            {
                hostHeader = trimmed["host:".Length..].Trim();
            }

            if (lower.StartsWith("authorization:", StringComparison.Ordinal))
            {
                var idx = trimmed.IndexOf(':', StringComparison.Ordinal);
                if (idx >= 0)
                {
                    authorization = trimmed[(idx + 1)..].Trim();
                }
            }

            if (lower.StartsWith("content-type:", StringComparison.Ordinal))
            {
                var mediaType = lower["content-type:".Length..];
                var semicolon = mediaType.IndexOf(';', StringComparison.Ordinal);
                if (semicolon >= 0)
                {
                    mediaType = mediaType[..semicolon];
                }

                contentTypeIsJson = mediaType.Trim() == "application/json";
            }

            if (lower.Contains("text/event-stream", StringComparison.Ordinal))
            {
                acceptSse = true;
            }

            if (lower.StartsWith("connection: ", StringComparison.Ordinal)
                && lower.Contains("keep-alive", StringComparison.Ordinal))
            {
                keepAlive = true;
            }
        }

        // Only consume a body for a POST that can reach the MCP route. Invalid paths,
        // forbidden origins, unauthenticated callers and non-JSON bodies retain the
        // early-response behavior.
        var authorized = BearerAuthorized(authorization, authToken);
        RequestBody body;
        if (string.Equals(method, "POST", StringComparison.Ordinal)
            && string.Equals(path, "/mcp", StringComparison.Ordinal)
            && OriginAllowed(origin, allowedOrigins)
            && authorized
            && contentTypeIsJson)
        {
            if (contentLength is not { } length)
            {
                body = RequestBody.MissingLength;
            }
            else if (length > MaxBodyBytes)
            {
                // Reject the client-supplied size before allocating the body.
                body = RequestBody.TooLarge;
            }
            else
            {
                body = RequestBody.Read(
                    await reader.ReadExactAsync(length, cancellationToken).ConfigureAwait(false));
            }
        }
        else
        {
            body = RequestBody.NotRead;
        }

        return RequestRead.Of(new HttpRequestLine(
            method, path, acceptSse, keepAlive, origin, authorized, contentTypeIsJson, body)
        {
            Host = hostHeader,
        });
    }

    internal static async Task<RequestRead> ReadRequestWithTimeoutAsync(
        LineReader reader,
        string? allowedOrigins,
        string? authToken,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        var read = ReadRequestAsync(reader, allowedOrigins, authToken, cts.Token);
        try
        {
            return await read.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            Observe(read);
            throw new HttpTransportException("request read timed out");
        }
        catch (OperationCanceledException)
        {
            throw new HttpTransportException("request read timed out");
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

    /// <summary>
    /// Origin allowlist for browser callers, read from
    /// <c>POCKETCALCULATOR_MCP_ALLOWED_ORIGINS</c> (comma-separated). Unset/empty refuses
    /// browser callers; native clients do not send Origin and remain unaffected.
    /// </summary>
    internal static string? AllowedOriginsEnv()
    {
        var value = Environment.GetEnvironmentVariable("POCKETCALCULATOR_MCP_ALLOWED_ORIGINS");
        return string.IsNullOrEmpty(value) || value.Trim().Length == 0 ? null : value;
    }

    /// <summary>
    /// Whether a request's <c>Origin</c> is permitted. A request with no
    /// <c>Origin</c> (native, non-browser MCP clients) is always allowed - the
    /// same-origin policy only constrains browser callers. A browser <c>Origin</c>
    /// must match an allowlist entry (case-insensitive), and with no allowlist it is
    /// refused; this stops a malicious local web page from driving the loopback MCP
    /// port.
    /// </summary>
    internal static bool OriginAllowed(string? origin, string? allowlist)
    {
        if (origin is null)
        {
            return true;
        }

        if (allowlist is null)
        {
            return false;
        }

        var trimmed = origin.Trim();
        foreach (var entry in allowlist.Split(','))
        {
            var candidate = entry.Trim();
            if (candidate.Length != 0
                && candidate.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// DNS-rebinding protection: whether a request's <c>Host</c> may be served by a
    /// server bound to <paramref name="bindIp"/>.
    /// </summary>
    /// <remarks>
    /// Deviation (SECURITY.md L1): upstream's MCP transport never reads
    /// <c>Host</c>. A DNS-rebound page cannot call tools, since its POSTs carry an
    /// <c>Origin</c> the Origin gate refuses, but it could open same-origin SSE
    /// streams. The port applies the CDP server's rule (<c>CdpServer.HostAllowed</c>,
    /// which is Chromium's <c>RequestIsSafeToServe</c>), as the MCP specification
    /// recommends: no Host, an IP literal, or <c>localhost</c> / <c>*.localhost</c>,
    /// on any port; and any Host at all for an unspecified bind, which is reached by
    /// whatever name the network gives it and cannot start without a token. The
    /// two copies are kept apart because this assembly does not reference the CDP
    /// one; keep them in step.
    /// </remarks>
    internal static bool HostAllowed(string? hostHeader, IPAddress bindIp)
    {
        if (hostHeader is null || hostHeader.Length == 0)
        {
            return true;
        }

        if (bindIp.Equals(IPAddress.Any) || bindIp.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (!Uri.TryCreate("http://" + hostHeader + "/", UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return true;
        }

        var name = uri.IdnHost.EndsWith('.') ? uri.IdnHost[..^1] : uri.IdnHost;
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// CORS response headers for an already-authorized browser caller: its origin
    /// echoed back plus <c>Vary: Origin</c>. A wildcard is never emitted for this
    /// privileged endpoint, and a native client with no <c>Origin</c> needs no CORS
    /// header at all.
    /// </summary>
    internal static string CorsHeader(string? origin, string? allowlist) =>
        origin is { } value && allowlist is not null
            ? $"Access-Control-Allow-Origin: {value}\r\nVary: Origin\r\n"
            : string.Empty;

    /// <summary>
    /// Run the MCP HTTP server until cancelled.
    /// </summary>
    /// <remarks>
    /// Reads <c>POCKETCALCULATOR_MCP_TOKEN</c> and <c>POCKETCALCULATOR_MCP_ALLOWED_ORIGINS</c>. A
    /// non-loopback bind without a token is refused before anything is bound.
    /// </remarks>
    public static Task RunAsync(
        string host,
        ushort port,
        string? proxy,
        string? userAgent,
        bool stealth,
        CancellationToken cancellationToken = default) =>
        RunAsync(host, port, proxy, userAgent, stealth, AllowedOriginsEnv(), TokenFromEnv(), cancellationToken);

    /// <summary>
    /// <see cref="RunAsync(string, ushort, string?, string?, bool, CancellationToken)"/>
    /// with the allowlist and token passed in rather than read from the process
    /// environment, which tests running in parallel share.
    /// </summary>
    /// <remarks>
    /// Connections are served concurrently, up to <see cref="MaxConnections"/>, but
    /// every JSON-RPC body goes through one dispatcher over a bounded queue. The
    /// browser session is single-threaded, so only the dispatcher ever touches it,
    /// and a slow or silent client holds a connection slot rather than the server.
    /// </remarks>
    internal static Task RunAsync(
        string host,
        ushort port,
        string? proxy,
        string? userAgent,
        bool stealth,
        string? allowedOrigins,
        string? authToken,
        CancellationToken cancellationToken) =>
        RunAsync(host, port, proxy, userAgent, stealth, allowedOrigins, authToken, new ServerOptions(), cancellationToken);

    /// <summary>The server body, with <paramref name="options"/> passed in.</summary>
    internal static async Task RunAsync(
        string host,
        ushort port,
        string? proxy,
        string? userAgent,
        bool stealth,
        string? allowedOrigins,
        string? authToken,
        ServerOptions options,
        CancellationToken cancellationToken)
    {
        var address = IPAddress.Parse(host);
        authToken = ValidateToken(authToken);
        if (!IPAddress.IsLoopback(address) && authToken is null)
        {
            throw new InvalidOperationException(
                "refusing to expose MCP without authentication; set POCKETCALCULATOR_MCP_TOKEN to at least 32 bytes");
        }

        var listener = new TcpListener(address, port);
        listener.Start();

        using var state = new BrowserState(proxy, userAgent, stealth);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requests = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(MaxPendingRequests)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        var stopping = stop.Token;
        var dispatcher = DispatchAsync(requests.Reader, state, stopping);
        using var slots = new SemaphoreSlim(MaxConnections, MaxConnections);
        using var sseSlots = new SemaphoreSlim(MaxSseStreams, MaxSseStreams);
        var activity = new ServerActivity();

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (!slots.Wait(0))
                {
                    // tracing::warn!("refusing MCP connection: connection limit reached")
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleConnectionAsync(
                                client, requests.Writer, address, sseSlots, allowedOrigins, authToken,
                                options, activity, stopping)
                            .ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // tracing::debug!("connection closed: {}", e)
                        client.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            slots.Release();
                        }
                        catch (ObjectDisposedException)
                        {
                            // The server already stopped.
                        }
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
            await stop.CancelAsync().ConfigureAwait(false);
            requests.Writer.TryComplete();
            try
            {
                await dispatcher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            while (requests.Reader.TryRead(out var orphan))
            {
                orphan.Reply.TrySetCanceled();
            }
        }
    }

    /// <summary>The one consumer of the request queue: the only code that touches the browser session.</summary>
    private static async Task DispatchAsync(
        ChannelReader<PendingRequest> requests,
        BrowserState state,
        CancellationToken cancellationToken)
    {
        await foreach (var request in requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                request.Reply.TrySetResult(await ProcessBodyAsync(request.Body, state).ConfigureAwait(false));
            }
            catch (Exception error)
            {
                // The connection that asked closes; the dispatcher keeps serving.
                request.Reply.TrySetException(error);
            }
        }
    }

    private static async Task HandleConnectionAsync(
        TcpClient client,
        ChannelWriter<PendingRequest> requests,
        IPAddress bindAddress,
        SemaphoreSlim sseSlots,
        string? allowedOrigins,
        string? authToken,
        ServerOptions options,
        ServerActivity activity,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var reader = new LineReader(stream);

        try
        {
            while (true)
            {
                var read = await ReadRequestWithTimeoutAsync(reader, allowedOrigins, authToken, RequestReadTimeout)
                    .ConfigureAwait(false);
                if (read.Kind is RequestReadKind.Closed or RequestReadKind.Invalid)
                {
                    break;
                }

                var request = read.Request!;
                activity.Touch();

                // -- routing ------------------------------------------------------
                if (!string.Equals(request.Path, "/mcp", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "{\"error\":\"not found\"}").ConfigureAwait(false);
                    break;
                }

                // Host gate (SECURITY.md L1): a DNS-rebound name is refused before
                // anything else, as the CDP server refuses it.
                if (!HostAllowed(request.Host, bindAddress))
                {
                    await RespondAsync(stream, 403, "{\"error\":\"host not allowed\"}")
                        .ConfigureAwait(false);
                    break;
                }

                // Origin gate: a browser request is refused unless its origin is on
                // POCKETCALCULATOR_MCP_ALLOWED_ORIGINS, before it can drive the browser
                // session (a malicious local web page issuing cross-origin POSTs to
                // the loopback MCP port). No-Origin native clients are unaffected.
                if (!OriginAllowed(request.Origin, allowedOrigins))
                {
                    await RespondAsync(stream, 403, "{\"error\":\"origin not allowed\"}")
                        .ConfigureAwait(false);
                    break;
                }

                if (!string.Equals(request.Method, "OPTIONS", StringComparison.Ordinal) && !request.Authorized)
                {
                    await RespondAsync(stream, 401, "{\"error\":\"authentication required\"}")
                        .ConfigureAwait(false);
                    break;
                }

                var cors = CorsHeader(request.Origin, allowedOrigins);

                if (string.Equals(request.Method, "OPTIONS", StringComparison.Ordinal))
                {
                    // mcp-protocol-version is part of the MCP spec and Authorization
                    // carries the bearer token. Without these listed the browser
                    // preflight check fails and blocks the actual request.
                    var header =
                        "HTTP/1.1 204 No Content\r\n"
                        + cors
                        + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n"
                        + "Access-Control-Allow-Headers: Content-Type, Authorization, mcp-protocol-version\r\n"
                        + "Access-Control-Max-Age: 86400\r\n"
                        + "\r\n";
                    await WriteAsync(stream, Encoding.UTF8.GetBytes(header)).ConfigureAwait(false);
                }
                else if (string.Equals(request.Method, "GET", StringComparison.Ordinal) && request.AcceptSse)
                {
                    // SSE stream: hold open and send periodic keep-alive comments.
                    // Deviation (SECURITY.md L2): upstream detaches the ping loop and
                    // drops its connection permit, so streams were never counted and
                    // any admitted client could open them without limit. The port
                    // keeps the stream on this connection's task, which holds its
                    // slot until the client goes away, and caps open streams at
                    // MaxSseStreams. Connections are served concurrently, so a held
                    // stream does not block the accept loop.
                    if (!sseSlots.Wait(0))
                    {
                        await RespondAsync(stream, 503, "{\"error\":\"too many event streams\"}")
                            .ConfigureAwait(false);
                        break;
                    }

                    try
                    {
                        var header =
                            "HTTP/1.1 200 OK\r\n"
                            + "Content-Type: text/event-stream\r\n"
                            + "Cache-Control: no-cache\r\n"
                            + "Connection: keep-alive\r\n"
                            + cors
                            + "\r\n";
                        await WriteAsync(stream, Encoding.UTF8.GetBytes(header)).ConfigureAwait(false);
                        // A read that completes means the client closed (or sent
                        // something it should not): either way the stream ends, and
                        // its slot is free at once rather than at the next ping.
                        var hangup = stream.ReadAsync(new byte[1], cancellationToken).AsTask();
                        var idleMs = (long)options.IdleTimeout.TotalMilliseconds;
                        var sincePing = 0L;
                        while (true)
                        {
                            // Wake for the next ping, or sooner when the idle timeout
                            // falls first.
                            var wait = (long)SsePingInterval.TotalMilliseconds - sincePing;
                            if (idleMs > 0)
                            {
                                var left = idleMs - activity.IdleMs;
                                if (left <= 0)
                                {
                                    break;
                                }

                                wait = Math.Min(wait, left);
                            }

                            var tick = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, wait)), cancellationToken);
                            if (await Task.WhenAny(hangup, tick).ConfigureAwait(false) == hangup)
                            {
                                Observe(hangup);
                                break;
                            }

                            await tick.ConfigureAwait(false);
                            sincePing += Math.Max(1, wait);
                            if (sincePing < (long)SsePingInterval.TotalMilliseconds)
                            {
                                continue;
                            }

                            sincePing = 0;
                            await stream.WriteAsync(": ping\n\n"u8.ToArray(), cancellationToken)
                                .ConfigureAwait(false);
                            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        Observe(hangup);
                    }
                    catch (Exception error) when (error is IOException or ObjectDisposedException
                        or OperationCanceledException or SocketException)
                    {
                        // The client went away, or the server stopped.
                    }
                    finally
                    {
                        try
                        {
                            sseSlots.Release();
                        }
                        catch (ObjectDisposedException)
                        {
                            // The server already stopped.
                        }
                    }

                    return;
                }
                else if (string.Equals(request.Method, "POST", StringComparison.Ordinal))
                {
                    if (!request.ContentTypeIsJson)
                    {
                        await RespondAsync(stream, 415, "{\"error\":\"Content-Type must be application/json\"}")
                            .ConfigureAwait(false);
                        break;
                    }

                    if (request.Body.Kind == RequestBodyKind.MissingLength)
                    {
                        await RespondAsync(stream, 400, "{\"error\":\"missing Content-Length\"}")
                            .ConfigureAwait(false);
                        break;
                    }

                    if (request.Body.Kind == RequestBodyKind.TooLarge)
                    {
                        await RespondAsync(stream, 413, "{\"error\":\"payload too large\"}")
                            .ConfigureAwait(false);
                        break;
                    }

                    var pending = new PendingRequest(
                        request.Body.Bytes!,
                        new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously));
                    await requests.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
                    var response = await pending.Reply.Task.ConfigureAwait(false);
                    await RespondJsonAsync(stream, McpJson.SerializeToUtf8(response), cors)
                        .ConfigureAwait(false);

                    if (!request.KeepAlive)
                    {
                        break;
                    }
                }
                else
                {
                    await RespondAsync(stream, 405, "{\"error\":\"method not allowed\"}")
                        .ConfigureAwait(false);
                    break;
                }
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    internal static async Task<JsonNode?> ProcessBodyAsync(byte[] body, BrowserState state)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return ParseError();
        }

        if (message is JsonArray batch)
        {
            if (batch.Count == 0 || batch.Count > MaxBatchItems)
            {
                return InvalidRequest();
            }

            var results = new JsonArray();
            foreach (var item in batch)
            {
                if (await ProcessOneAsync(item, state).ConfigureAwait(false) is { } result)
                {
                    results.Add(result);
                }
            }

            return results;
        }

        return await ProcessOneAsync(message, state).ConfigureAwait(false) ?? InvalidRequest();
    }

    private static JsonObject ParseError() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = null,
        ["error"] = new JsonObject
        {
            ["code"] = JsonExt.Int(-32700),
            ["message"] = "Parse error",
        },
    };

    private static JsonObject InvalidRequest() => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = null,
        ["error"] = new JsonObject
        {
            ["code"] = JsonExt.Int(-32600),
            ["message"] = "Invalid Request",
        },
    };

    private static async Task<JsonNode?> ProcessOneAsync(JsonNode? message, BrowserState state)
    {
        // Notifications have no id. An explicit `"id": null` IS an id here, which is
        // the one place the HTTP transport differs from stdio (whose
        // `Option<Value>` maps a null id to a notification).
        if (!message.Has("id"))
        {
            return null;
        }

        var id = message.Get("id")?.DeepClone();
        // The id is echoed into the reply, so a structured or oversized one would
        // let a caller make the server build and send arbitrary payloads.
        if (id is JsonArray or JsonObject || McpJson.SerializeToUtf8(id).Length > MaxIdBytes)
        {
            return InvalidRequest();
        }

        var method = message.Get("method").AsString() ?? string.Empty;
        var parameters = message.Get("params");
        var response = await McpServer.DispatchAsync(method, id, parameters, state).ConfigureAwait(false);
        return response.ToJson();
    }

    private static async Task RespondJsonAsync(Stream writer, byte[] body, string cors)
    {
        var header =
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n"
            + cors
            + "Connection: keep-alive\r\n"
            + "\r\n";
        await WriteAsync(writer, Encoding.UTF8.GetBytes(header)).ConfigureAwait(false);
        await WriteAsync(writer, body).ConfigureAwait(false);
    }

    private static async Task RespondAsync(Stream writer, int status, string body)
    {
        var statusText = status switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            415 => "Unsupported Media Type",
            503 => "Service Unavailable",
            _ => "OK",
        };
        var payload = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} {statusText}\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n"
            + "\r\n";
        await WriteAsync(writer, Encoding.UTF8.GetBytes(header)).ConfigureAwait(false);
        await WriteAsync(writer, payload).ConfigureAwait(false);
    }

    private static async Task WriteAsync(Stream writer, byte[] bytes)
    {
        await writer.WriteAsync(bytes).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// <c>tokio::io::BufReader</c>'s bounded line read / <c>read_exact</c> pair over a
    /// byte stream: line reads must not swallow the bytes that follow the blank line,
    /// because the request body is read straight out of the same buffer.
    /// </summary>
    internal sealed class LineReader(Stream stream)
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        /// <summary>Raw length of the last line read, its CR/LF included.</summary>
        internal int LastLineBytes { get; private set; }

        /// <summary>
        /// One line without its trailing CR/LF, or null at end of stream. An empty
        /// string means a blank line, which is how the header block ends. A line
        /// longer than <paramref name="limit"/> bytes, line feed included, or one that
        /// is not UTF-8, is an error rather than a line.
        /// </summary>
        internal async Task<string?> ReadLineAsync(int limit, CancellationToken cancellationToken)
        {
            var line = new List<byte>(128);
            LastLineBytes = 0;
            while (true)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (_end == 0)
                    {
                        return line.Count == 0 ? null : Decode(line);
                    }
                }

                var b = _buffer[_start++];
                if (++LastLineBytes > limit)
                {
                    throw new HttpTransportException("HTTP line too long");
                }

                if (b == (byte)'\n')
                {
                    return Decode(line);
                }

                line.Add(b);
            }
        }

        internal async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
        {
            var body = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                if (_start == _end)
                {
                    _start = 0;
                    _end = await stream.ReadAsync(_buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (_end == 0)
                    {
                        throw new HttpTransportException("unexpected end of stream reading body");
                    }
                }

                var take = Math.Min(count - filled, _end - _start);
                Array.Copy(_buffer, _start, body, filled, take);
                _start += take;
                filled += take;
            }

            return body;
        }

        private static string Decode(List<byte> line)
        {
            var span = CollectionsMarshal.AsSpan(line);
            if (span.Length > 0 && span[^1] == (byte)'\r')
            {
                span = span[..^1];
            }

            try
            {
                return StrictUtf8.GetString(span);
            }
            catch (DecoderFallbackException)
            {
                throw new HttpTransportException("HTTP head is not UTF-8");
            }
        }
    }
}
