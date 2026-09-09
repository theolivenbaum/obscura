using Obscura.Js.Url;
using Obscura.Net;

namespace Obscura.Browser;

/// <summary>
/// The boundary between the WHATWG <see cref="UrlRecord"/> the page keeps and the
/// <see cref="Uri"/> <c>Obscura.Net</c> still speaks.
/// </summary>
/// <remarks>
/// The Rust engine has no such boundary: <c>obscura-net</c> takes and returns the
/// <c>url</c> crate's <c>Url</c>, so a URL crosses into the transport without being
/// reserialized. Here it does, which means a URL that <see cref="Uri"/> spells
/// differently changes spelling on the way through. That only affects URLs that are
/// actually fetched, so it is confined to <c>http</c>, <c>https</c>, <c>file</c> and
/// <c>data</c>; <c>data:</c> bodies never reach the transport with markup intact
/// today either. Migrating <c>Obscura.Net</c> to <see cref="UrlRecord"/> removes the
/// conversion entirely and is the real fix.
/// </remarks>
internal static class NetUrl
{
    /// <summary>
    /// The transport's spelling of a parsed page URL.
    /// </summary>
    /// <remarks>
    /// <see cref="UrlRecord"/> accepts hosts <see cref="Uri"/> rejects, because it
    /// follows the WHATWG host parser and the reference engine's <c>url</c> crate:
    /// <c>http://a..b/</c> parses, and the failure comes later, from DNS. Letting
    /// <see cref="Uri"/>'s <c>UriFormatException</c> escape reported that as
    /// "Invalid URI: The hostname could not be parsed"; the reference reports a
    /// transport failure naming the URL, so raise the transport's own error instead.
    /// </remarks>
    internal static Uri From(UrlRecord url) =>
        Uri.TryCreate(url.Href, UriKind.Absolute, out Uri? uri)
            ? uri
            : throw ObscuraNetException.Network($"{url.Href}: error sending request for url ({url.Href})");

    /// <summary>Reparses a URL the transport handed back.</summary>
    internal static UrlRecord To(Uri url) =>
        UrlRecord.Parse(url.AbsoluteUri) ?? UrlRecord.Parse("about:blank")!;
}
