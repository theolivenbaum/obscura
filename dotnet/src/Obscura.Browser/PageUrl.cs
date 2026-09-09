namespace Obscura.Browser;

/// <summary>
/// The pieces of the WHATWG URL surface <c>page.rs</c> reaches for through the
/// <c>url</c> crate. <see cref="Uri"/> has no origin type and no fragment
/// setter, so they live here.
/// </summary>
internal static class PageUrl
{
    /// <summary>Rust's <c>Url::parse</c>: absolute only, no base.</summary>
    internal static Uri? TryParse(string? value) =>
        value is not null && Uri.TryCreate(value, UriKind.Absolute, out Uri? url) ? url : null;

    /// <summary>Rust's <c>Url::join</c>.</summary>
    internal static Uri? TryJoin(Uri baseUrl, string relative) =>
        Uri.TryCreate(baseUrl, relative, out Uri? joined) ? joined : null;

    /// <summary>The lowercase scheme, matching <c>Url::scheme()</c>.</summary>
    internal static string Scheme(Uri url) => url.Scheme;

    /// <summary>The host without IPv6 brackets, matching <c>Url::host_str</c>.</summary>
    internal static string Host(Uri url) =>
        url.HostNameType == UriHostNameType.IPv6 ? url.Host.Trim('[', ']') : url.Host;

    /// <summary><c>Origin::ascii_serialization</c>.</summary>
    internal static string AsciiOrigin(Uri url) =>
        url.IsDefaultPort
            ? $"{url.Scheme}://{url.Host}"
            : $"{url.Scheme}://{url.Host}:{url.Port}";

    internal static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.Ordinal)
        && string.Equals(Host(a), Host(b), StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary><c>url.set_fragment(None)</c>, returning the URL unchanged when it has none.</summary>
    internal static Uri WithoutFragment(Uri url)
    {
        string text = url.AbsoluteUri;
        int hash = text.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return url;
        }
        return Uri.TryCreate(text[..hash], UriKind.Absolute, out Uri? stripped) ? stripped : url;
    }

    /// <summary>
    /// <c>set_fragment(None)</c> plus <c>set_username("")</c> and
    /// <c>set_password(None)</c>, the shape a same-origin referrer takes.
    /// </summary>
    internal static string WithoutCredentialsOrFragment(Uri url)
    {
        string authority = url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";
        return $"{url.Scheme}://{authority}{url.AbsolutePath}{url.Query}";
    }

    /// <summary><c>robots_url.set_path("/robots.txt"); set_query(None); set_fragment(None)</c>.</summary>
    internal static Uri? RobotsUrl(Uri url) =>
        Uri.TryCreate($"{AsciiOrigin(url)}/robots.txt", UriKind.Absolute, out Uri? robots) ? robots : null;
}
