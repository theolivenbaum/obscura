using System.Text.Json.Serialization;

namespace PocketCalculator.Net;

/// <summary>
/// The partition a <c>Partitioned</c> (CHIPS) cookie is kept in: the top-level site of the
/// context that set it and whether that context had a cross-site ancestor, as Chromium 141
/// keys it. The JSON shape is CDP's <c>Network.CookiePartitionKey</c>.
/// </summary>
/// <remarks>
/// Port addition: Rust's jar has no partitions, so a <c>Partitioned</c> cookie a
/// cross-site frame set was an ordinary third-party cookie, readable by that site
/// top-level and by every other top-level site embedding it.
/// </remarks>
/// <param name="TopLevelSite">The scheme and registrable domain, <c>https://example.com</c>.</param>
/// <param name="HasCrossSiteAncestor">
/// Whether the context, or a frame between it and the top-level document, is cross-site
/// with the top-level site.
/// </param>
public readonly record struct CookiePartitionKey(
    [property: JsonPropertyName("topLevelSite")] string TopLevelSite,
    [property: JsonPropertyName("hasCrossSiteAncestor")] bool HasCrossSiteAncestor)
{
    /// <summary>
    /// The schemeful site of <paramref name="url"/> (<c>https://example.com</c>), or null
    /// when it has none: only http(s) URLs with a host have one here.
    /// </summary>
    public static string? SiteOf(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri
            || (!string.Equals(url.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(url.Scheme, "http", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var host = CookieJar.HostOf(url);
        if (host.Length == 0)
        {
            return null;
        }

        string site;
        if (url.HostNameType is UriHostNameType.IPv4)
        {
            site = host;
        }
        else if (url.HostNameType is UriHostNameType.IPv6)
        {
            site = "[" + host + "]";
        }
        else
        {
            site = PublicSuffixList.TryGetRegistrableDomain(host, out var registrable)
                ? registrable.ToString()
                : host;
        }

        return url.Scheme.ToLowerInvariant() + "://" + site.ToLowerInvariant();
    }

    /// <summary>The key of a top-level document at <paramref name="url"/>, or null when it has no site.</summary>
    public static CookiePartitionKey? ForTopLevel(Uri url) =>
        SiteOf(url) is { } site ? new CookiePartitionKey(site, false) : null;

    /// <summary>
    /// The key of a request to <paramref name="target"/> made in a context whose top-level
    /// document is <paramref name="topLevel"/>: the cross-site bit is set when the context
    /// already had a cross-site ancestor or the target is cross-site with the top level.
    /// Null when the top level has no site.
    /// </summary>
    public static CookiePartitionKey? For(Uri topLevel, bool crossSiteAncestor, Uri target)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        ArgumentNullException.ThrowIfNull(target);
        return SiteOf(topLevel) is { } site
            ? new CookiePartitionKey(site, crossSiteAncestor || !CookieJar.IsSameSite(topLevel, target))
            : null;
    }

    /// <summary>
    /// Read a key a client supplied (CDP, a cookie file): the top-level site must parse as
    /// an http(s) URL, and is reduced to its site as Chromium does
    /// (<c>https://www.example.com:8443</c> becomes <c>https://example.com</c>).
    /// </summary>
    public static bool TryNormalize(string topLevelSite, bool hasCrossSiteAncestor, out CookiePartitionKey key)
    {
        key = default;
        if (!Uri.TryCreate(topLevelSite, UriKind.Absolute, out var url) || SiteOf(url) is not { } site)
        {
            return false;
        }

        key = new CookiePartitionKey(site, hasCrossSiteAncestor);
        return true;
    }
}

/// <summary>
/// The context a cookie is read or written in: the SameSite relationship of the request (or
/// document) with its site for cookies, and the partition key of that context. A null
/// <see cref="Partition"/> means the context has none (an opaque top level): partitioned
/// cookies are then neither sent nor stored.
/// </summary>
public readonly record struct CookieAccess(SameSiteContext SameSite, CookiePartitionKey? Partition)
{
    /// <summary>The context of a top-level document at <paramref name="url"/>.</summary>
    public static CookieAccess TopLevel(Uri url) =>
        new(SameSiteContext.SameSite, CookiePartitionKey.ForTopLevel(url));
}
