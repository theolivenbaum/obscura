namespace Obscura.Net;

/// <summary>
/// The pieces of the WHATWG URL "origin" concept obscura relies on. .NET's
/// <see cref="Uri"/> has no origin type, so scheme/host/port comparison and ASCII
/// serialization live here and are used everywhere the Rust code calls
/// <c>Url::origin()</c>.
/// </summary>
internal static class UrlOrigin
{
    /// <summary>True when both URLs have the same scheme, host and port.</summary>
    internal static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.Ordinal)
        && string.Equals(Host(a), Host(b), StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary>The host without IPv6 brackets, matching <c>Url::host_str</c>.</summary>
    internal static string Host(Uri url) =>
        url.HostNameType == UriHostNameType.IPv6 ? url.Host.Trim('[', ']') : url.Host;

    /// <summary>
    /// <c>Origin::ascii_serialization</c>: scheme://host, with the port appended when
    /// it is not the scheme default.
    /// </summary>
    internal static string AsciiSerialization(Uri url) =>
        url.IsDefaultPort
            ? $"{url.Scheme}://{url.Host}"
            : $"{url.Scheme}://{url.Host}:{url.Port}";

    /// <summary>
    /// Serialize a URL with the userinfo and the fragment stripped, the shape the
    /// same-origin Referer header uses.
    /// </summary>
    internal static string WithoutCredentialsOrFragment(Uri url)
    {
        var authority = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
        return $"{url.Scheme}://{authority}{url.AbsolutePath}{url.Query}";
    }
}
