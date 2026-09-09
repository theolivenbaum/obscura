using System.Text.Json.Nodes;
using Obscura.Js.Url;
using Obscura.Net;

namespace Obscura.Cdp;

/// <summary>The filter <c>Network.deleteCookies</c> / <c>Storage.deleteCookies</c> resolve to.</summary>
public sealed class DeleteCookiesFilter
{
    public required string Name { get; init; }

    public required string Domain { get; init; }

    public required string? Path { get; init; }
}

/// <summary>Turning CDP cookie payloads into <see cref="CookieInfo"/>.</summary>
public static class CookieParams
{
    private const string DefaultCookiePath = "/";

    /// <summary>
    /// Read one <c>Network.setCookie</c>-shaped object. Null when the payload
    /// carries neither a name nor a domain the cookie could be scoped to.
    /// </summary>
    public static CookieInfo? ParseCdpCookie(JsonNode? value)
    {
        if (value.Get("name").AsString() is not { } name)
        {
            return null;
        }

        var cookieValue = value.Get("value").AsStringOr(string.Empty);

        var urlParsed = value.Get("url").AsString() is { } rawUrl ? UrlRecord.Parse(rawUrl) : null;

        var domain = value.Get("domain").AsString() ?? urlParsed?.HostStr ?? string.Empty;
        if (domain.Length == 0)
        {
            return null;
        }

        var path = value.Get("path").AsString()
                   ?? (urlParsed is null ? null : CookieJar.DefaultCookiePath(urlParsed.Path))
                   ?? DefaultCookiePath;

        var secure = value.Get("secure").AsBoolOr(false);
        var httpOnly = value.Get("httpOnly").AsBoolOr(false);
        var sameSite = value.Get("sameSite").AsStringOr(string.Empty);
        var expires = value.Get("expires").AsF64() is { } seconds ? SaturatingI64(seconds) : (long?)null;

        return new CookieInfo
        {
            Name = name,
            Value = cookieValue,
            Domain = domain,
            Path = path,
            Secure = secure,
            HttpOnly = httpOnly,
            SameSite = sameSite,
            Expires = expires,
        };
    }

    /// <summary>
    /// Rust's <c>f as i64</c>, which saturates at the bounds and maps NaN to
    /// zero. An unchecked C# cast would wrap to <c>long.MinValue</c> instead, so
    /// a nonsense <c>expires</c> would read as a date in the distant past rather
    /// than the distant future.
    /// </summary>
    private static long SaturatingI64(double value) => value switch
    {
        _ when double.IsNaN(value) => 0,
        >= 9.2233720368547758e18 => long.MaxValue,
        <= -9.2233720368547758e18 => long.MinValue,
        _ => (long)value,
    };

    /// <summary>Read a <c>deleteCookies</c> filter. Null when it carries no usable name.</summary>
    public static DeleteCookiesFilter? ParseDeleteCookiesParams(JsonNode? parameters)
    {
        if (parameters.Get("name").AsString() is not { } name || name.Length == 0)
        {
            return null;
        }

        var urlParsed = parameters.Get("url").AsString() is { } rawUrl
            ? UrlRecord.Parse(rawUrl)
            : null;

        var domain = parameters.Get("domain").AsString() ?? urlParsed?.HostStr ?? string.Empty;
        var path = parameters.Get("path").AsString() ?? urlParsed?.Path;

        return new DeleteCookiesFilter { Name = name, Domain = domain, Path = path };
    }
}
