namespace PocketCalculator.Net;

/// <summary>A referrer policy (W3C Referrer Policy).</summary>
public enum ReferrerPolicy
{
    /// <summary><c>no-referrer</c>.</summary>
    NoReferrer,

    /// <summary><c>no-referrer-when-downgrade</c>.</summary>
    NoReferrerWhenDowngrade,

    /// <summary><c>origin</c>.</summary>
    Origin,

    /// <summary><c>origin-when-cross-origin</c>.</summary>
    OriginWhenCrossOrigin,

    /// <summary><c>same-origin</c>.</summary>
    SameOrigin,

    /// <summary><c>strict-origin</c>.</summary>
    StrictOrigin,

    /// <summary><c>strict-origin-when-cross-origin</c>, the default.</summary>
    StrictOriginWhenCrossOrigin,

    /// <summary><c>unsafe-url</c>.</summary>
    UnsafeUrl,
}

/// <summary>
/// Parsing and applying referrer policies, as Chromium 141 does (measured). The Rust engine
/// has no referrer policy: every request used strict-origin-when-cross-origin, and
/// <c>&lt;meta name=referrer&gt;</c>, <c>referrerpolicy</c>, <c>rel=noreferrer</c>, the
/// <c>Referrer-Policy</c> header and fetch's <c>referrerPolicy</c> were all ignored.
/// </summary>
public static class ReferrerPolicies
{
    /// <summary>The policy when nothing sets one.</summary>
    public const ReferrerPolicy Default = ReferrerPolicy.StrictOriginWhenCrossOrigin;

    /// <summary>
    /// Chromium's cap on a referrer: a longer one is sent as its origin
    /// (<c>kMaxReferrerLength</c>, as the Fetch spec also has it).
    /// </summary>
    public const int MaxReferrerLength = 4096;

    /// <summary>The policy's token, as <c>referrerPolicy</c> reflects it.</summary>
    public static string Token(ReferrerPolicy policy) => policy switch
    {
        ReferrerPolicy.NoReferrer => "no-referrer",
        ReferrerPolicy.NoReferrerWhenDowngrade => "no-referrer-when-downgrade",
        ReferrerPolicy.Origin => "origin",
        ReferrerPolicy.OriginWhenCrossOrigin => "origin-when-cross-origin",
        ReferrerPolicy.SameOrigin => "same-origin",
        ReferrerPolicy.StrictOrigin => "strict-origin",
        ReferrerPolicy.UnsafeUrl => "unsafe-url",
        _ => "strict-origin-when-cross-origin",
    };

    /// <summary>
    /// One policy token, ASCII case-insensitive and not trimmed. <paramref name="allowLegacy"/>
    /// also accepts the legacy <c>&lt;meta&gt;</c> keywords Chromium keeps (<c>never</c>,
    /// <c>always</c>, <c>default</c>, <c>origin-when-crossorigin</c>).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> token, bool allowLegacy, out ReferrerPolicy policy)
    {
        policy = Default;
        ReferrerPolicy? parsed =
            token.Equals("no-referrer", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.NoReferrer
            : token.Equals("no-referrer-when-downgrade", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.NoReferrerWhenDowngrade
            : token.Equals("origin", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.Origin
            : token.Equals("origin-when-cross-origin", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.OriginWhenCrossOrigin
            : token.Equals("same-origin", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.SameOrigin
            : token.Equals("strict-origin", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.StrictOrigin
            : token.Equals("strict-origin-when-cross-origin", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.StrictOriginWhenCrossOrigin
            : token.Equals("unsafe-url", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.UnsafeUrl
            : !allowLegacy ? null
            : token.Equals("never", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.NoReferrer
            : token.Equals("always", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.UnsafeUrl
            : token.Equals("default", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.StrictOriginWhenCrossOrigin
            : token.Equals("origin-when-crossorigin", StringComparison.OrdinalIgnoreCase) ? ReferrerPolicy.OriginWhenCrossOrigin
            : null;
        if (parsed is { } value)
        {
            policy = value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A <c>Referrer-Policy</c> response header: comma-separated tokens, trimmed, the last
    /// recognised one wins and unknown ones are skipped, but a token with a character other
    /// than a letter or <c>-</c> voids the whole header (Chromium's
    /// <c>ReferrerPolicyFromHeaderValue</c>). Legacy keywords are not recognised here.
    /// </summary>
    public static ReferrerPolicy? ParseHeader(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        ReferrerPolicy? result = null;
        foreach (var raw in value.Split(','))
        {
            var token = raw.AsSpan().Trim(" \t");
            if (token.IsEmpty)
            {
                continue;
            }

            if (TryParse(token, allowLegacy: false, out var policy))
            {
                result = policy;
                continue;
            }

            foreach (var c in token)
            {
                if (!char.IsAsciiLetter(c) && c != '-')
                {
                    return null;
                }
            }
        }

        return result;
    }

    /// <summary>A <c>&lt;meta name=referrer&gt;</c> content: exactly one token, legacy keywords allowed.</summary>
    public static ReferrerPolicy? ParseMeta(string? content) =>
        content is not null && TryParse(content, allowLegacy: true, out var policy) ? policy : null;

    /// <summary>A <c>referrerpolicy</c> attribute or fetch <c>referrerPolicy</c>: one standard token, or null.</summary>
    public static ReferrerPolicy? ParseAttribute(string? value) =>
        value is not null && TryParse(value, allowLegacy: false, out var policy) ? policy : null;

    /// <summary>
    /// The Referer a request from <paramref name="source"/> to <paramref name="target"/>
    /// carries under <paramref name="policy"/>, or null for none (Fetch "determine request's
    /// referrer"): userinfo and fragment are stripped, a referrer longer than
    /// <see cref="MaxReferrerLength"/> is cut to its origin, and only http(s) sources and
    /// targets carry one.
    /// </summary>
    public static string? Referrer(Uri? source, Uri target, ReferrerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (source is null || policy == ReferrerPolicy.NoReferrer || !IsHttp(source) || !IsHttp(target))
        {
            return null;
        }

        var origin = $"{UrlOrigin.AsciiSerialization(source)}/";
        var full = UrlOrigin.WithoutCredentialsOrFragment(source);
        if (full.Length > MaxReferrerLength)
        {
            full = origin;
        }

        // A downgrade is an https referrer going to a target that is not https. Chromium's
        // network layer checks the scheme only (SchemeIsCryptographic), so http://localhost
        // and http://127.0.0.1 are downgrades too although mixed content allows them
        // (measured on Chromium 141 for images, fetch, navigations and Page.navigate).
        var downgrade = string.Equals(source.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var sameOrigin = UrlOrigin.SameOrigin(source, target);
        return policy switch
        {
            ReferrerPolicy.UnsafeUrl => full,
            ReferrerPolicy.Origin => origin,
            ReferrerPolicy.StrictOrigin => downgrade ? null : origin,
            ReferrerPolicy.NoReferrerWhenDowngrade => downgrade ? null : full,
            ReferrerPolicy.SameOrigin => sameOrigin ? full : null,
            ReferrerPolicy.OriginWhenCrossOrigin => sameOrigin ? full : origin,
            _ => sameOrigin ? full : downgrade ? null : origin,
        };
    }

    private static bool IsHttp(Uri url) =>
        string.Equals(url.Scheme, "http", StringComparison.Ordinal)
        || string.Equals(url.Scheme, "https", StringComparison.Ordinal);
}
