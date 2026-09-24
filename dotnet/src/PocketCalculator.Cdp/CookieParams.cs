using System.Text.Json.Nodes;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;

namespace PocketCalculator.Cdp;

/// <summary>The filter <c>Network.deleteCookies</c> / <c>Storage.deleteCookies</c> resolve to.</summary>
public sealed class DeleteCookiesFilter
{
    public required string Name { get; init; }

    public required string Domain { get; init; }

    public required string? Path { get; init; }

    /// <summary>The partition to delete from; null deletes unpartitioned cookies only.</summary>
    public CookiePartitionKey? PartitionKey { get; init; }
}

/// <summary>
/// A parsed CDP cookie and its scope: host-only when the payload named a <c>url</c>
/// and no <c>domain</c>, as a cookie set from that URL without a Domain attribute is.
/// </summary>
public sealed record ParsedCookie(CookieInfo Cookie, bool HostOnly);

/// <summary>Turning CDP cookie payloads into <see cref="CookieInfo"/>.</summary>
public static class CookieParams
{
    private const string DefaultCookiePath = "/";

    /// <summary>
    /// Read one <c>Network.setCookie</c>-shaped object. Null when the payload
    /// carries neither a name nor a domain the cookie could be scoped to.
    /// </summary>
    public static ParsedCookie? ParseCdpCookie(JsonNode? value)
    {
        if (value.Get("name").AsString() is not { } name)
        {
            return null;
        }

        var cookieValue = value.Get("value").AsStringOr(string.Empty);

        var urlParsed = value.Get("url").AsString() is { } rawUrl ? UrlRecord.Parse(rawUrl) : null;

        var explicitDomain = value.Get("domain").AsString();
        var domain = explicitDomain ?? urlParsed?.HostStr ?? string.Empty;
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
        if (!TryParsePartitionKey(value, out var partitionKey))
        {
            return null;
        }

        return new ParsedCookie(
            new CookieInfo
            {
                Name = name,
                Value = cookieValue,
                Domain = domain,
                Path = path,
                Secure = secure,
                HttpOnly = httpOnly,
                SameSite = sameSite,
                Expires = expires,
                PartitionKey = partitionKey,
            },
            HostOnly: explicitDomain is null);
    }

    /// <summary>
    /// Read a payload's <c>partitionKey</c>, Chromium 141's
    /// <c>{topLevelSite, hasCrossSiteAncestor}</c>, both required, with the site reduced
    /// to a schemeful site. False when it is present and not that shape; an absent key
    /// is an unpartitioned cookie. Port addition (CHIPS).
    /// </summary>
    public static bool TryParsePartitionKey(JsonNode? value, out CookiePartitionKey? key)
    {
        key = null;
        var node = value.Get("partitionKey");
        if (node is null)
        {
            return true;
        }

        if (node is not JsonObject
            || node.Get("topLevelSite").AsString() is not { } site
            || node.Get("hasCrossSiteAncestor") is not JsonValue bit
            || !bit.TryGetValue<bool>(out var crossSite)
            || !CookiePartitionKey.TryNormalize(site, crossSite, out var normalized))
        {
            return false;
        }

        key = normalized;
        return true;
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

        return TryParsePartitionKey(parameters, out var partitionKey)
            ? new DeleteCookiesFilter { Name = name, Domain = domain, Path = path, PartitionKey = partitionKey }
            : null;
    }
}
