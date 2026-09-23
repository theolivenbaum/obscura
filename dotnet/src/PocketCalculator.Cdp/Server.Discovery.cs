using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PocketCalculator.Cdp;

/// <summary>
/// The client-facing endpoint the discovery JSON advertises (upstream 0671d94).
/// </summary>
public static partial class CdpServer
{
    /// <summary>
    /// The trimmed value of the first header called <paramref name="name"/>
    /// (case-insensitive) in a request head, or null. The request line is skipped
    /// and the search stops at the blank line that ends the head.
    /// </summary>
    internal static string? HeaderValue(string head, string name)
    {
        var lines = head.Split("\r\n");
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && line.AsSpan(0, colon).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The authority the discovery JSON advertises in <c>webSocketDebuggerUrl</c>:
    /// the request's own <c>Host</c> when it is a bare authority, so a client that
    /// reached the server through a published port or a tunnel is sent back the way
    /// it came, and <c>127.0.0.1:{port}</c> otherwise. Chromium's DevTools server
    /// echoes the Host header here too.
    /// </summary>
    internal static string WebSocketAuthority(string requestHead, int port) =>
        WebSocketAuthority(requestHead, port, null);

    /// <summary>
    /// As <see cref="WebSocketAuthority(string, int)"/>, for a worker behind the
    /// multi-worker balancer. A DNS Host the forwarded authority let through is
    /// echoed like any other (upstream a156914).
    /// </summary>
    /// <remarks>
    /// Deviation: with no usable Host, upstream falls back to
    /// <c>127.0.0.1:{worker port}</c>, an internal port no client can use. It never
    /// reaches that fallback through the balancer, because it refuses a request
    /// without Host; this port serves one (Chromium's rule, see
    /// <see cref="HostAllowed(string?, IPAddress)"/>), so it falls back to the
    /// balancer's public authority instead: its address, or loopback for a
    /// wildcard bind, and its port.
    /// </remarks>
    internal static string WebSocketAuthority(string requestHead, int port, ForwardedAuthority? forwarded)
    {
        var fallback = forwarded is { } authority
            ? FallbackAuthority(authority)
            : $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        if (HeaderValue(requestHead, "host") is not { Length: > 0 } value)
        {
            return fallback;
        }

        // url::Url::parse("http://{value}/") in Rust. The host must be present and
        // the value must add nothing beyond it and a port: no userinfo, path,
        // query or fragment.
        if (!Uri.TryCreate("http://" + value + "/", UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || value.Contains('@', StringComparison.Ordinal)
            || value.Contains('?', StringComparison.Ordinal)
            || value.Contains('#', StringComparison.Ordinal))
        {
            return fallback;
        }

        return value;
    }

    private static string FallbackAuthority(ForwardedAuthority authority)
    {
        var address = authority.Address;
        if (address.Equals(IPAddress.Any))
        {
            address = IPAddress.Loopback;
        }
        else if (address.Equals(IPAddress.IPv6Any))
        {
            address = IPAddress.IPv6Loopback;
        }

        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"{host}:{authority.Port.ToString(CultureInfo.InvariantCulture)}";
    }
}
