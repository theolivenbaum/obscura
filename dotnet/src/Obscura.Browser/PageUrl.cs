using Obscura.Js.Url;

namespace Obscura.Browser;

/// <summary>
/// The pieces of the WHATWG URL surface <c>page.rs</c> reaches for through the
/// <c>url</c> crate.
/// </summary>
/// <remarks>
/// These sit on <see cref="UrlRecord"/>, the in-tree port of the <c>url</c>
/// crate, and not on <see cref="System.Uri"/>. <c>System.Uri</c> is not
/// WHATWG-compliant: it percent-encodes <c>&lt;</c>, <c>&gt;</c> and space in a
/// cannot-be-a-base URL's opaque path, so <c>data:text/html,&lt;b&gt;a b&lt;/b&gt;</c>
/// came back as <c>data:text/html,%3Cb%3Ea%20b%3C/b%3E</c> and every
/// <c>data:</c> URL on the CDP wire differed from the reference engine.
/// </remarks>
internal static class PageUrl
{
    /// <summary>Rust's <c>Url::parse</c>: absolute only, no base.</summary>
    internal static UrlRecord? TryParse(string? value) =>
        value is null ? null : UrlRecord.Parse(value);

    /// <summary>Rust's <c>Url::join</c>.</summary>
    internal static UrlRecord? TryJoin(UrlRecord baseUrl, string relative) =>
        baseUrl.Join(relative);

    /// <summary>The lowercase scheme, matching <c>Url::scheme()</c>.</summary>
    internal static string Scheme(UrlRecord url) => url.Scheme;

    /// <summary>Matching <c>Url::host_str</c>, brackets and all.</summary>
    internal static string? Host(UrlRecord url) => url.HostStr;

    /// <summary><c>Origin::ascii_serialization</c>.</summary>
    internal static string AsciiOrigin(UrlRecord url) => url.AsciiOrigin;

    /// <summary>
    /// <c>Url::origin() == Url::origin()</c>. Both callers gate on http/https
    /// first, where the origin is always a tuple origin, so comparing the
    /// serialization is the same test as comparing the origins.
    /// </summary>
    internal static bool SameOrigin(UrlRecord a, UrlRecord b) =>
        string.Equals(a.AsciiOrigin, b.AsciiOrigin, StringComparison.Ordinal);

    /// <summary><c>url.set_fragment(None)</c>, returning the URL unchanged when it has none.</summary>
    internal static UrlRecord WithoutFragment(UrlRecord url)
    {
        if (url.Fragment is null)
        {
            return url;
        }
        UrlRecord stripped = url.Clone();
        stripped.SetFragment(null);
        return stripped;
    }

    /// <summary>
    /// <c>set_fragment(None)</c> plus <c>set_username("")</c> and
    /// <c>set_password(None)</c>, the shape a same-origin referrer takes.
    /// </summary>
    internal static string WithoutCredentialsOrFragment(UrlRecord url)
    {
        UrlRecord sanitized = url.Clone();
        sanitized.SetFragment(null);
        sanitized.SetUsername(string.Empty);
        sanitized.SetPassword(null);
        return sanitized.Href;
    }

    /// <summary><c>robots_url.set_path("/robots.txt"); set_query(None); set_fragment(None)</c>.</summary>
    internal static UrlRecord RobotsUrl(UrlRecord url)
    {
        UrlRecord robots = url.Clone();
        robots.SetPath("/robots.txt");
        robots.SetQuery(null);
        robots.SetFragment(null);
        return robots;
    }
}
