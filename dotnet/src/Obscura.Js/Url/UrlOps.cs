using System.Globalization;
using System.Text;
using Obscura.Js.Ops;

namespace Obscura.Js.Url;

/// <summary>
/// The URL ops exposed to <c>bootstrap.js</c>, matching <c>crates/obscura-js/src/ops.rs</c>.
/// </summary>
/// <remarks>
/// Every op is total. The Rust bodies sit inside <c>catch_unwind</c> because the <c>url</c>
/// crate panics on a few pathological inputs and a panic there must read as "invalid URL" or
/// "the setter did nothing"; the managed equivalent routes each body through
/// <see cref="OpGuard"/> and returns the same failure value.
/// </remarks>
public static class UrlOps
{
    private const string Invalid = "{\"ok\":false}";

    /// <summary>
    /// <c>op_url_parse</c>: parses <paramref name="href"/>, optionally against
    /// <paramref name="baseHref"/>, and returns the component JSON or <c>{"ok":false}</c>.
    /// </summary>
    public static string UrlParse(string href, string baseHref) => OpGuard.Run(
        "op_url_parse",
        () =>
        {
            var parsed = ParseWithBase(href, baseHref);
            return parsed is null ? Invalid : UrlComponents(parsed);
        },
        Invalid);

    /// <summary>
    /// <c>op_url_set</c>: applies a WHATWG URL setter and returns the new components. An
    /// invalid value leaves the URL unchanged rather than throwing or clearing the component.
    /// </summary>
    public static string UrlSet(string href, string part, string value)
    {
        var applied = OpGuard.Run("op_url_set", () => UrlSetInner(href, part, value), null);
        if (applied is not null)
        {
            return applied;
        }

        return OpGuard.Run(
            "op_url_set",
            () =>
            {
                var url = UrlRecord.Parse(href);
                return url is null ? Invalid : UrlComponents(url);
            },
            Invalid);
    }

    /// <summary>
    /// <c>op_url_resolve</c>: the resolved absolute URL, or "" when the input is invalid.
    /// This is the hot <c>a.href</c> path, so it skips the component JSON entirely.
    /// </summary>
    public static string UrlResolve(string href, string baseHref) => OpGuard.Run(
        "op_url_resolve",
        () => ParseWithBase(href, baseHref)?.Href ?? string.Empty,
        string.Empty);

    /// <summary>
    /// <c>op_url_encode_query</c>: re-encodes a query with a non-UTF-8 document encoding
    /// override. An unknown label returns the input unchanged.
    /// </summary>
    public static string UrlEncodeQuery(string query, string label, bool special) => OpGuard.Run(
        "op_url_encode_query",
        () => QueryEncoding.TryEncodeQuery(query, label, special, out var encoded) ? encoded : query,
        query);

    /// <summary>
    /// <c>op_document_domain_candidate</c>: canonicalizes and validates a
    /// <c>document.domain</c> assignment. An empty return means SecurityError on the JS side.
    /// </summary>
    public static string DocumentDomainCandidate(string current, string input) => OpGuard.Run(
        "op_document_domain_candidate",
        () =>
        {
            if (!HostParser.TryParse(input, out var host))
            {
                return string.Empty;
            }

            var canonical = host.ToString().ToLowerInvariant();
            var effective = current.ToLowerInvariant();

            // Gecko permits assigning the exact current host, IP literals and single-label
            // hosts included. Neither of those can then be relaxed to a parent.
            if (string.Equals(canonical, effective, StringComparison.Ordinal))
            {
                return canonical;
            }

            if (IsIpAddress(effective) || IsIpAddress(canonical)
                || !effective.EndsWith("." + canonical, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            // A candidate shorter than the registrable domain is a public suffix.
            var registrable = PublicSuffixList.RegistrableDomain(effective);
            return registrable is not null && canonical.Length >= registrable.Length
                ? canonical
                : string.Empty;
        },
        string.Empty);

    /// <summary>
    /// The component shape consumed directly by the <c>URL</c> class in <c>bootstrap.js</c>.
    /// Field names, field order, and the empty-versus-absent choices are a contract: the
    /// getters read these fields with no further op call.
    /// </summary>
    public static string UrlComponents(UrlRecord url)
    {
        var port = url.PortNumber is int p ? p.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var hostname = url.HostStr ?? string.Empty;
        var host = hostname.Length == 0
            ? string.Empty
            : port.Length == 0 ? hostname : hostname + ":" + port;

        // The WHATWG search/hash getters return "" for a null OR empty component.
        var query = url.Query;
        var search = string.IsNullOrEmpty(query) ? string.Empty : "?" + query;
        var fragment = url.Fragment;
        var hash = string.IsNullOrEmpty(fragment) ? string.Empty : "#" + fragment;

        var sb = new StringBuilder(url.Href.Length + 160);
        sb.Append("{\"ok\":true,\"href\":");
        AppendJsonString(sb, url.Href);
        sb.Append(",\"protocol\":");
        AppendJsonString(sb, url.Scheme + ":");
        sb.Append(",\"username\":");
        AppendJsonString(sb, url.Username);
        sb.Append(",\"password\":");
        AppendJsonString(sb, url.Password ?? string.Empty);
        sb.Append(",\"host\":");
        AppendJsonString(sb, host);
        sb.Append(",\"hostname\":");
        AppendJsonString(sb, hostname);
        sb.Append(",\"port\":");
        AppendJsonString(sb, port);
        sb.Append(",\"pathname\":");
        AppendJsonString(sb, url.Path);
        sb.Append(",\"search\":");
        AppendJsonString(sb, search);
        sb.Append(",\"hash\":");
        AppendJsonString(sb, hash);
        sb.Append(",\"origin\":");
        AppendJsonString(sb, url.AsciiOrigin);
        sb.Append('}');
        return sb.ToString();
    }

    private static UrlRecord? ParseWithBase(string href, string baseHref)
    {
        if (baseHref.Length == 0)
        {
            return UrlRecord.Parse(href);
        }

        var baseUrl = UrlRecord.Parse(baseHref);
        return baseUrl?.Join(href);
    }

    private static string? UrlSetInner(string href, string part, string value)
    {
        var url = UrlRecord.Parse(href);
        if (url is null)
        {
            return null;
        }

        switch (part)
        {
            case "href":
            {
                var replacement = UrlRecord.Parse(value);
                return replacement is null ? null : UrlComponents(replacement);
            }

            case "protocol":
                url.SetScheme(value.TrimEnd(':'));
                break;
            case "username":
                url.SetUsername(value);
                break;
            case "password":
                url.SetPassword(value.Length == 0 ? null : value);
                break;
            case "host":
                SetHostPort(url, value);
                break;
            case "hostname":
                if (value.Length != 0)
                {
                    url.SetHost(value);
                }

                break;
            case "port":
                if (value.Length == 0)
                {
                    url.SetPort(null);
                }
                else if (TryParsePortValue(value, out var portValue))
                {
                    url.SetPort(portValue);
                }

                break;
            case "pathname":
                url.SetPath(value);
                break;
            case "search":
            {
                var q = value.StartsWith('?') ? value[1..] : value;
                url.SetQuery(q.Length == 0 ? null : q);
                break;
            }

            case "hash":
            {
                var f = value.StartsWith('#') ? value[1..] : value;
                url.SetFragment(f.Length == 0 ? null : f);
                break;
            }

            default:
                break;
        }

        return UrlComponents(url);
    }

    /// <summary>
    /// Best-effort <c>host</c> setter: splits <c>host[:port]</c> (never inside IPv6 brackets)
    /// and applies hostname and port separately, since the host setter rejects a port.
    /// </summary>
    private static void SetHostPort(UrlRecord url, string value)
    {
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close >= 0)
            {
                var bracketed = value[..(close + 1)];
                var rest = value[(close + 1)..];
                if (url.SetHost(bracketed) && rest.StartsWith(':')
                    && TryParsePortValue(rest[1..], out var bracketPort))
                {
                    url.SetPort(bracketPort);
                }

                return;
            }
        }

        var idx = value.LastIndexOf(':');
        if (idx >= 0)
        {
            var hostPart = value[..idx];
            var portPart = value[(idx + 1)..];
            var allDigits = true;
            foreach (var c in portPart)
            {
                if (!char.IsAsciiDigit(c))
                {
                    allDigits = false;
                    break;
                }
            }

            if (portPart.Length == 0 || allDigits)
            {
                if (url.SetHost(hostPart))
                {
                    if (portPart.Length == 0)
                    {
                        url.SetPort(null);
                    }
                    else if (TryParsePortValue(portPart, out var port))
                    {
                        url.SetPort(port);
                    }
                }

                return;
            }
        }

        url.SetHost(value);
    }

    /// <summary>
    /// Rust's <c>str::parse::&lt;u16&gt;</c>: an optional leading <c>+</c>, then ASCII digits
    /// only, and the result must fit in 16 bits.
    /// </summary>
    private static bool TryParsePortValue(string value, out int port)
    {
        port = 0;
        var span = value.AsSpan();
        if (span.Length > 0 && span[0] == '+')
        {
            span = span[1..];
        }

        if (span.Length == 0)
        {
            return false;
        }

        var accumulator = 0;
        foreach (var c in span)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            accumulator = (accumulator * 10) + (c - '0');
            if (accumulator > ushort.MaxValue)
            {
                return false;
            }
        }

        port = accumulator;
        return true;
    }

    /// <summary>
    /// Rust's <c>std::net::IpAddr</c> parse, which is strict: a dotted quad with no leading
    /// zeros, or a bare IPv6 literal.
    /// </summary>
    internal static bool IsIpAddress(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            return HostParser.TryParseIpv6(value, out _);
        }

        var parts = value.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            var n = 0;
            foreach (var c in part)
            {
                if (!char.IsAsciiDigit(c))
                {
                    return false;
                }

                n = (n * 10) + (c - '0');
            }

            if (n > 255)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A <c>serde_json</c>-compatible string writer. System.Text.Json escapes far more than
    /// serde does (<c>+</c>, <c>&lt;</c>, <c>&amp;</c>, non-ASCII), and the payload has to be
    /// byte-identical to the Rust op's output.
    /// </summary>
    internal static void AppendJsonString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u00");
                        output.Append("0123456789abcdef"[c >> 4]);
                        output.Append("0123456789abcdef"[c & 0xF]);
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }
}
