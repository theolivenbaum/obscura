namespace Obscura.Js.Url;

/// <summary>
/// Why a URL failed to parse: the port of the <c>url</c> crate's
/// <c>ParseError</c>.
/// </summary>
/// <remarks>
/// <para>
/// The reason is an observable surface, not a diagnostic. <c>page.rs</c> maps a
/// failed <c>Url::parse</c> straight onto <c>PageError::InvalidUrl(e.to_string())</c>,
/// so the crate's <c>Display</c> text is what a client reads back from a rejected
/// navigation. <see cref="UrlParseErrorText.Message"/> reproduces those strings
/// verbatim.
/// </para>
/// <para>
/// The crate carries two further variants this parser cannot produce and which are
/// therefore not modelled: <c>SetHostOnCannotBeABaseUrl</c>, raised only by
/// <c>Url::set_host</c>, and <c>Overflow</c>, raised only for an input over 4 GB
/// (the port indexes with <see cref="int"/> and never reaches it).
/// </para>
/// </remarks>
public enum UrlParseError
{
    /// <summary>A relative reference was parsed with no base URL.</summary>
    /// <remarks>
    /// The default, because it is the outcome of the one failure that is not a
    /// malformed URL at all.
    /// </remarks>
    RelativeUrlWithoutBase = 0,

    /// <summary>A relative reference was parsed against a cannot-be-a-base base.</summary>
    RelativeUrlWithCannotBeABaseBase,

    /// <summary>A special scheme was given no host, or an empty one.</summary>
    EmptyHost,

    /// <summary>The host is not a valid domain under IDNA/UTS#46.</summary>
    IdnaError,

    /// <summary>The port is not a number, or does not fit in a <see cref="ushort"/>.</summary>
    InvalidPort,

    /// <summary>A host that ends in a number is not a valid IPv4 address.</summary>
    InvalidIpv4Address,

    /// <summary>A bracketed host is not a valid IPv6 address, or is unterminated.</summary>
    InvalidIpv6Address,

    /// <summary>An opaque host holds a forbidden host code point.</summary>
    InvalidDomainCharacter,
}

/// <summary>The <c>Display</c> text the <c>url</c> crate writes for a <c>ParseError</c>.</summary>
/// <remarks>
/// These strings are copied from the crate's <c>simple_enum_error!</c> table and are
/// part of the wire surface (see <see cref="UrlParseError"/>); do not reword them.
/// </remarks>
public static class UrlParseErrorText
{
    /// <summary>The message for <paramref name="error"/>, as the <c>url</c> crate prints it.</summary>
    public static string Message(this UrlParseError error) => error switch
    {
        UrlParseError.RelativeUrlWithoutBase => "relative URL without a base",
        UrlParseError.RelativeUrlWithCannotBeABaseBase => "relative URL with a cannot-be-a-base base",
        UrlParseError.EmptyHost => "empty host",
        UrlParseError.IdnaError => "invalid international domain name",
        UrlParseError.InvalidPort => "invalid port number",
        UrlParseError.InvalidIpv4Address => "invalid IPv4 address",
        UrlParseError.InvalidIpv6Address => "invalid IPv6 address",
        UrlParseError.InvalidDomainCharacter => "invalid domain character",
        _ => "relative URL without a base",
    };
}
