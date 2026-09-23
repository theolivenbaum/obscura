using System.Globalization;

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
    internal static string WebSocketAuthority(string requestHead, int port)
    {
        var fallback = $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
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
}
