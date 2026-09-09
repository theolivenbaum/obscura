using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Obscura.Mcp;

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
    /// unauthenticated OOM/DoS. 16 MiB is far above any real JSON-RPC tool call.
    /// </summary>
    internal const int MaxBodyBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Maximum time allowed to receive one complete HTTP request (request line,
    /// headers, and body). The deadline is shared across all reads so a client
    /// cannot keep the sequential MCP server occupied by slowly dribbling data.
    /// </summary>
    internal static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(30);

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
        RequestBody Body);

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

    internal static async Task<RequestRead> ReadRequestAsync(
        LineReader reader,
        string? allowedOrigins,
        CancellationToken cancellationToken)
    {
        var requestLineRaw = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var trimmed = line ?? string.Empty;
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

        // Only consume a body for a POST that can reach the MCP route. Invalid paths
        // and forbidden origins retain the existing early-response behavior.
        RequestBody body;
        if (string.Equals(method, "POST", StringComparison.Ordinal)
            && string.Equals(path, "/mcp", StringComparison.Ordinal)
            && OriginAllowed(origin, allowedOrigins))
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

        return RequestRead.Of(new HttpRequestLine(method, path, acceptSse, keepAlive, origin, body));
    }

    internal static async Task<RequestRead> ReadRequestWithTimeoutAsync(
        LineReader reader,
        string? allowedOrigins,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        var read = ReadRequestAsync(reader, allowedOrigins, cts.Token);
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
    /// <c>OBSCURA_MCP_ALLOWED_ORIGINS</c> (comma-separated). Unset/empty means
    /// permissive (unchanged <c>*</c>) so hosted dashboards keep working (issue #175).
    /// </summary>
    internal static string? AllowedOriginsEnv()
    {
        var value = Environment.GetEnvironmentVariable("OBSCURA_MCP_ALLOWED_ORIGINS");
        return string.IsNullOrEmpty(value) || value.Trim().Length == 0 ? null : value;
    }

    /// <summary>
    /// Whether a request's <c>Origin</c> is permitted. A request with no
    /// <c>Origin</c> (native, non-browser MCP clients) is always allowed - the
    /// same-origin policy only constrains browser callers. When an allowlist is
    /// configured, a browser <c>Origin</c> must match one of its entries
    /// (case-insensitive); this stops a malicious local web page from driving the
    /// loopback MCP port.
    /// </summary>
    internal static bool OriginAllowed(string? origin, string? allowlist)
    {
        if (allowlist is null)
        {
            return true;
        }

        if (origin is null)
        {
            return true;
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
    /// CORS <c>Access-Control-Allow-Origin</c> value for a response. With no
    /// allowlist we keep the permissive <c>*</c> (issue #175). With an allowlist the
    /// request's origin has already passed <see cref="OriginAllowed"/>, so echo it
    /// back plus <c>Vary: Origin</c> instead of advertising <c>*</c>; a native client
    /// with no <c>Origin</c> needs no CORS header at all.
    /// </summary>
    internal static string CorsHeader(string? origin, string? allowlist) =>
        allowlist is null
            ? "Access-Control-Allow-Origin: *\r\n"
            : origin is { } value
                ? $"Access-Control-Allow-Origin: {value}\r\nVary: Origin\r\n"
                : string.Empty;

    /// <summary>
    /// Run the MCP HTTP server until cancelled.
    /// </summary>
    /// <remarks>
    /// Connections are handled sequentially - the browser session (including the V8
    /// runtime) is single-threaded, so state never crosses threads. That is exactly
    /// why the SSE keep-alive must be detached: holding its infinite ping loop
    /// inline would never return to the accept loop and would wedge every later
    /// request.
    /// </remarks>
    public static async Task RunAsync(
        string host,
        ushort port,
        string? proxy,
        string? userAgent,
        bool stealth,
        CancellationToken cancellationToken = default)
    {
        var address = IPAddress.Parse(host);
        var listener = new TcpListener(address, port);
        listener.Start();

        using var state = new BrowserState(proxy, userAgent, stealth);
        var allowedOrigins = AllowedOriginsEnv();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    await HandleConnectionAsync(client, state, allowedOrigins, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // tracing::debug!("connection closed: {}", e)
                    client.Dispose();
                }
            }
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }

    private static async Task HandleConnectionAsync(
        TcpClient client,
        BrowserState state,
        string? allowedOrigins,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var reader = new LineReader(stream);
        var detached = false;

        try
        {
            while (true)
            {
                var read = await ReadRequestWithTimeoutAsync(reader, allowedOrigins, RequestReadTimeout)
                    .ConfigureAwait(false);
                if (read.Kind is RequestReadKind.Closed or RequestReadKind.Invalid)
                {
                    break;
                }

                var request = read.Request!;

                // -- routing ------------------------------------------------------
                if (!string.Equals(request.Path, "/mcp", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, 404, "{\"error\":\"not found\"}").ConfigureAwait(false);
                    break;
                }

                // Origin gate: when OBSCURA_MCP_ALLOWED_ORIGINS is configured, a
                // browser request from a non-listed origin is refused before it can
                // drive the browser session (mitigates a malicious local web page
                // issuing cross-origin POSTs to the loopback MCP port). The
                // permissive default and no-Origin native clients are unaffected.
                if (!OriginAllowed(request.Origin, allowedOrigins))
                {
                    await RespondAsync(stream, 403, "{\"error\":\"origin not allowed\"}")
                        .ConfigureAwait(false);
                    break;
                }

                var cors = CorsHeader(request.Origin, allowedOrigins);

                if (string.Equals(request.Method, "OPTIONS", StringComparison.Ordinal))
                {
                    // mcp-protocol-version is part of the MCP spec, Authorization /
                    // X-API-Key are common for hosted deployments. Without these
                    // listed the browser preflight check fails and blocks the actual
                    // request.
                    var header =
                        "HTTP/1.1 204 No Content\r\n"
                        + cors
                        + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n"
                        + "Access-Control-Allow-Headers: Content-Type, Authorization, X-API-Key, mcp-protocol-version\r\n"
                        + "Access-Control-Max-Age: 86400\r\n"
                        + "\r\n";
                    await WriteAsync(stream, Encoding.UTF8.GetBytes(header)).ConfigureAwait(false);
                }
                else if (string.Equals(request.Method, "GET", StringComparison.Ordinal) && request.AcceptSse)
                {
                    // SSE stream: hold open and send periodic keep-alive comments.
                    // Connections are served sequentially, so holding this infinite
                    // keep-alive loop inline would never return to the accept loop
                    // and would wedge every later request. The keep-alive touches no
                    // browser state, so detach the ping loop onto its own task and
                    // return - the accept loop stays free while the stream lives on
                    // independently.
                    var header =
                        "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: text/event-stream\r\n"
                        + "Cache-Control: no-cache\r\n"
                        + "Connection: keep-alive\r\n"
                        + cors
                        + "\r\n";
                    await WriteAsync(stream, Encoding.UTF8.GetBytes(header)).ConfigureAwait(false);
                    detached = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (true)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)
                                    .ConfigureAwait(false);
                                await stream.WriteAsync(": ping\n\n"u8.ToArray(), cancellationToken)
                                    .ConfigureAwait(false);
                                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                            }
                        }
                        catch (Exception)
                        {
                            // The client went away; the stream closes with the client.
                        }
                        finally
                        {
                            client.Dispose();
                        }
                    }, CancellationToken.None);
                    return;
                }
                else if (string.Equals(request.Method, "POST", StringComparison.Ordinal))
                {
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

                    var response = await ProcessBodyAsync(request.Body.Bytes!, state).ConfigureAwait(false);
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
            if (!detached)
            {
                client.Dispose();
            }
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
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
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
    /// <c>tokio::io::BufReader</c>'s <c>read_line</c> / <c>read_exact</c> pair over a
    /// byte stream: line reads must not swallow the bytes that follow the blank line,
    /// because the request body is read straight out of the same buffer.
    /// </summary>
    internal sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _start;
        private int _end;

        /// <summary>
        /// One line without its trailing CR/LF, or null at end of stream. An empty
        /// string means a blank line, which is how the header block ends.
        /// </summary>
        internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new List<byte>(128);
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

            return Encoding.UTF8.GetString(span);
        }
    }
}
