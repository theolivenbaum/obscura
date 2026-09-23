using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PocketCalculator.Cdp;

/// <summary>
/// Control-plane admission for the CDP port (upstream 04418a5): the bearer token,
/// the refusal of browser callers and of rebound hosts, and the oversized-head
/// refusal.
/// </summary>
public static partial class CdpServer
{
    /// <summary>Shortest bearer token <c>POCKETCALCULATOR_CDP_TOKEN</c> may hold, in bytes.</summary>
    internal const int MinControlTokenBytes = 32;

    /// <summary>Why a control-plane request was turned away before it was served.</summary>
    internal enum ControlRefusal
    {
        BrowserOrigin,
        ForeignHost,
        Unauthorized,
        OversizedHead,
    }

    /// <summary>
    /// The bearer token from <c>POCKETCALCULATOR_CDP_TOKEN</c>. Unset or empty means no
    /// token; one shorter than <see cref="MinControlTokenBytes"/> is a startup error.
    /// </summary>
    internal static string? ControlTokenFromEnv() =>
        ValidateControlToken(Environment.GetEnvironmentVariable("POCKETCALCULATOR_CDP_TOKEN"));

    internal static string? ValidateControlToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (Encoding.UTF8.GetByteCount(token) < MinControlTokenBytes)
        {
            throw new InvalidOperationException("POCKETCALCULATOR_CDP_TOKEN must be at least 32 bytes");
        }

        return token;
    }

    /// <summary>
    /// Whether the head carries <c>Authorization: Bearer &lt;token&gt;</c>. With no
    /// token configured every request is authorized. The comparison does not
    /// short-circuit on the first differing byte.
    /// </summary>
    internal static bool BearerAuthorized(string head, string? expected)
    {
        if (expected is null)
        {
            return true;
        }

        if (HeaderValue(head, "authorization") is not { } value
            || !value.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(value["Bearer ".Length..]),
            Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// DNS-rebinding protection: whether a request's <c>Host</c> header may be
    /// served by a server bound to <paramref name="bindIp"/>.
    /// </summary>
    /// <remarks>
    /// Deviation from upstream's <c>host_matches_bind</c>, which requires the Host
    /// to name the bind address itself (or any loopback address for a loopback
    /// bind) and the bind port, and refuses a request with no Host. That refuses
    /// requests Chromium's DevTools server serves: an SSH tunnel or port forward
    /// that lands on a different local port (<c>Host: localhost:9333</c> reaching
    /// port 9222), the multi-worker balancer forwarding <c>Host: 127.0.0.1:9222</c>
    /// to a worker on 9223, and an HTTP/1.0 client with no Host. Chromium's rule
    /// (<c>RequestIsSafeToServe</c> in devtools_http_handler.cc) is that the Host is
    /// absent, an IP literal, or a localhost name, on any port; that is what stops
    /// rebinding, because a rebound page's Host is the attacker's domain. The port
    /// takes Chromium's rule, and keeps upstream's one widening: an unspecified bind
    /// (0.0.0.0, ::) accepts any Host, because it is reached by whatever name the
    /// network gives it and cannot start without a token.
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

        return IsLocalHostname(uri.IdnHost);
    }

    /// <summary>Chromium's <c>net::IsLocalHostname</c>: <c>localhost</c> and <c>*.localhost</c>, with or without the root dot.</summary>
    private static bool IsLocalHostname(string host)
    {
        var name = host.EndsWith('.') ? host[..^1] : host;
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first reason to refuse a control-plane request, or null to serve it. A
    /// browser caller is recognised by any <c>Origin</c> header: native CDP clients
    /// (Puppeteer, Playwright, chromedp over Node or Go websockets) send none.
    /// </summary>
    internal static ControlRefusal? GetControlRefusal(string head, IPAddress bindIp, string? authToken)
    {
        if (HeaderValue(head, "origin") is not null)
        {
            return ControlRefusal.BrowserOrigin;
        }

        if (!HostAllowed(HeaderValue(head, "host"), bindIp))
        {
            return ControlRefusal.ForeignHost;
        }

        if (!BearerAuthorized(head, authToken))
        {
            return ControlRefusal.Unauthorized;
        }

        return null;
    }

    /// <summary>The refusal response, byte for byte what <c>refuse_control_connection</c> writes.</summary>
    internal static string ControlRefusalResponse(ControlRefusal refusal)
    {
        var (status, reason) = refusal switch
        {
            ControlRefusal.Unauthorized => ("401 Unauthorized", "authentication required"),
            ControlRefusal.OversizedHead => ("431 Request Header Fields Too Large", "request head too large"),
            _ => ("403 Forbidden", "request refused"),
        };
        var body = $"{{\"error\":\"{reason}\"}}";
        return $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\n"
            + $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n"
            + $"Connection: close\r\n\r\n{body}";
    }

    /// <summary>
    /// Answer a refused request on the accept thread and close it.
    /// </summary>
    /// <remarks>
    /// The Rust server writes the response and drops the stream. Closing a socket
    /// with unread request bytes makes the kernel send a reset, which can destroy
    /// the response before the client reads it, so the port first consumes what
    /// has already arrived (without waiting for more: this runs on the accept
    /// thread) and half-closes after writing.
    /// </remarks>
    private static void RefuseControlConnection(Socket socket, ControlRefusal refusal)
    {
        try
        {
            socket.Blocking = false;
            var scratch = new byte[HttpPeekBuf];
            var drained = 0;
            while (drained < 64 * 1024 && socket.Available > 0)
            {
                var n = socket.Receive(scratch, 0, scratch.Length, SocketFlags.None, out var error);
                if (n <= 0 || error != SocketError.Success)
                {
                    break;
                }

                drained += n;
            }

            socket.Blocking = true;
            socket.SendTimeout = 1000;
            socket.Send(Encoding.UTF8.GetBytes(ControlRefusalResponse(refusal)));
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            socket.Dispose();
        }
    }
}
