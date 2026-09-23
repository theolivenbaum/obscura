using System.Globalization;
using System.Text;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// The CORS request policy of <c>op_fetch_url</c>: the request-header safelist, the
/// preflight permission lists, and what a response may show page script.
/// </summary>
public static partial class FetchOps
{
    internal static bool IsCorsSafelistedMethod(HttpMethod method) =>
        method.Method is "GET" or "HEAD" or "POST";

    private static bool IsCorsUnsafeRequestHeaderByte(byte b) =>
        (b < 0x20 && b != (byte)'\t')
        || b is (byte)'"' or (byte)'(' or (byte)')' or (byte)':' or (byte)'<' or (byte)'>' or (byte)'?'
            or (byte)'@' or (byte)'[' or (byte)'\\' or (byte)']' or (byte)'{' or (byte)'}' or 0x7f;

    private static bool IsHttpTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`'
            or '|' or '~';

    private static bool AllHttpToken(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!IsHttpTokenChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyUnsafeHeaderByte(string value)
    {
        foreach (var c in value)
        {
            // Every byte of a non-ASCII character's UTF-8 form is >= 0x80, which
            // the unsafe-byte set never contains, so checking chars is exact.
            if (c < 0x80 && IsCorsUnsafeRequestHeaderByte((byte)c))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsCorsSafelistedContentType(string value)
    {
        if (AnyUnsafeHeaderByte(value))
        {
            return false;
        }

        // A MIME type must have a valid type/subtype before its parameters. This is
        // deliberately narrower than merely splitting at ';': malformed values must
        // not turn an application/json request into a simple request.
        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        var essence = (semicolon < 0 ? value : value[..semicolon]).Trim([' ', '\t']);
        var slash = essence.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return false;
        }

        var type = essence.AsSpan(0, slash);
        var subtype = essence.AsSpan(slash + 1);
        if (type.IsEmpty || subtype.IsEmpty || !AllHttpToken(type) || !AllHttpToken(subtype))
        {
            return false;
        }

        return essence.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || essence.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || essence.Equals("text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DecimalIsAtMost(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');
        return left.Length < right.Length
            || (left.Length == right.Length && string.CompareOrdinal(left, right) <= 0);
    }

    private static bool AllAsciiDigits(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCorsSafelistedRange(string value)
    {
        if (!value.StartsWith("bytes=", StringComparison.Ordinal))
        {
            return false;
        }

        var range = value["bytes=".Length..];
        var dash = range.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return false;
        }

        var start = range[..dash];
        var end = range[(dash + 1)..];
        if (start.Length == 0 || !AllAsciiDigits(start) || !AllAsciiDigits(end))
        {
            return false;
        }

        return end.Length == 0 || DecimalIsAtMost(start, end);
    }

    internal static bool IsCorsSafelistedRequestHeader(string name, string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > 128)
        {
            return false;
        }

        if (name.Equals("accept", StringComparison.OrdinalIgnoreCase))
        {
            return !AnyUnsafeHeaderByte(value);
        }

        if (name.Equals("accept-language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("content-language", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in value)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not (' ' or '*' or ',' or '-' or '.' or ';' or '='))
                {
                    return false;
                }
            }

            return true;
        }

        if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
        {
            return IsCorsSafelistedContentType(value);
        }

        if (name.Equals("range", StringComparison.OrdinalIgnoreCase))
        {
            return IsCorsSafelistedRange(value);
        }

        return false;
    }

    /// <summary>
    /// The sorted, lower-case names of the request headers a CORS preflight has to
    /// authorize. Once the safelisted values exceed 1024 bytes together, they count
    /// as unsafe as well.
    /// </summary>
    internal static List<string> CorsUnsafeRequestHeaderNames(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        List<string> unsafeNames = [];
        var safelistValueSize = 0L;
        foreach (var (name, value) in headers)
        {
            if (IsCorsSafelistedRequestHeader(name, value))
            {
                safelistValueSize += Encoding.UTF8.GetByteCount(value);
            }
            else
            {
                unsafeNames.Add(name.ToLowerInvariant());
            }
        }

        if (safelistValueSize > 1024)
        {
            foreach (var (name, value) in headers)
            {
                if (IsCorsSafelistedRequestHeader(name, value))
                {
                    unsafeNames.Add(name.ToLowerInvariant());
                }
            }
        }

        unsafeNames.Sort(StringComparer.Ordinal);
        for (var i = unsafeNames.Count - 1; i > 0; i--)
        {
            if (string.Equals(unsafeNames[i], unsafeNames[i - 1], StringComparison.Ordinal))
            {
                unsafeNames.RemoveAt(i);
            }
        }

        return unsafeNames;
    }

    /// <summary>
    /// Every item of a comma-separated token list, over all values of the header.
    /// Null when an item is empty or not a token, or a value is not visible ASCII.
    /// </summary>
    internal static List<string>? ParseCorsHeaderList(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<string> items = [];
        foreach (var value in values)
        {
            foreach (var c in value)
            {
                // http's HeaderValue::to_str: visible ASCII and tab only.
                if ((c < 0x20 && c != '\t') || c >= 0x7f)
                {
                    return null;
                }
            }

            foreach (var raw in value.Split(','))
            {
                var item = raw.Trim([' ', '\t']);
                if (item.Length == 0 || !AllHttpToken(item))
                {
                    return null;
                }

                items.Add(item);
            }
        }

        return items;
    }

    private static List<string>? ParseCorsHeaderList(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? ParseCorsHeaderList(values)
            : response.Content.Headers.TryGetValues(name, out var contentValues)
                ? ParseCorsHeaderList(contentValues)
                : [];

    internal static bool PreflightAllowsMethod(string method, IReadOnlyList<string> allowed, bool credentialed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        if (method is "GET" or "HEAD" or "POST")
        {
            return true;
        }

        foreach (var item in allowed)
        {
            if (string.Equals(item, method, StringComparison.Ordinal)
                || (string.Equals(item, "*", StringComparison.Ordinal) && !credentialed))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool PreflightAllowsHeader(string name, IReadOnlyList<string> allowed, bool credentialed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        var wildcard = false;
        foreach (var item in allowed)
        {
            if (item.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            wildcard |= string.Equals(item, "*", StringComparison.Ordinal);
        }

        return wildcard && !credentialed && !name.Equals("authorization", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a redirect with this status turns the request into a bodiless GET.
    /// </summary>
    /// <remarks>
    /// Deviation from Rust, which downgrades every 301/302/303: this follows Fetch's
    /// HTTP-redirect fetch and Chromium, where 301/302 downgrade only a POST and 303
    /// downgrades anything but GET and HEAD.
    /// </remarks>
    internal static bool RedirectDowngradesToGet(int status, HttpMethod method) => status switch
    {
        301 or 302 => method.Method == "POST",
        303 => method.Method is not ("GET" or "HEAD"),
        _ => false,
    };

    /// <summary>
    /// Strips headers that must not survive a redirect (upstream #967). A redirect
    /// that changes origin drops <c>Authorization</c>, <c>Proxy-Authorization</c> and
    /// an explicit <c>Cookie</c>; a downgrade to GET drops the request-body headers.
    /// Cookies from the jar are unaffected: they are added per hop for the hop's URL.
    /// </summary>
    internal static void SanitizeRedirectHeaders(
        Dictionary<string, string> headers,
        bool crossesOrigin,
        bool downgradedToGet)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!crossesOrigin && !downgradedToGet)
        {
            return;
        }

        List<string>? remove = null;
        foreach (var name in headers.Keys)
        {
            var lower = name.ToLowerInvariant();
            var strip = (crossesOrigin && lower is "authorization" or "proxy-authorization" or "cookie")
                || (downgradedToGet
                    && lower is "content-type" or "content-length" or "content-encoding"
                        or "content-language" or "content-location");
            if (strip)
            {
                (remove ??= []).Add(name);
            }
        }

        if (remove is not null)
        {
            foreach (var name in remove)
            {
                headers.Remove(name);
            }
        }
    }

    /// <summary>
    /// A status as the http crate's <c>StatusCode</c> displays it: the code and its
    /// canonical reason phrase, never the one the server sent.
    /// </summary>
    internal static string StatusDisplay(int status) =>
        status.ToString(CultureInfo.InvariantCulture) + " " + (status switch
        {
            100 => "Continue",
            101 => "Switching Protocols",
            200 => "OK",
            201 => "Created",
            202 => "Accepted",
            203 => "Non Authoritative Information",
            204 => "No Content",
            205 => "Reset Content",
            206 => "Partial Content",
            300 => "Multiple Choices",
            301 => "Moved Permanently",
            302 => "Found",
            303 => "See Other",
            304 => "Not Modified",
            307 => "Temporary Redirect",
            308 => "Permanent Redirect",
            400 => "Bad Request",
            401 => "Unauthorized",
            402 => "Payment Required",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            406 => "Not Acceptable",
            407 => "Proxy Authentication Required",
            408 => "Request Timeout",
            409 => "Conflict",
            410 => "Gone",
            411 => "Length Required",
            412 => "Precondition Failed",
            413 => "Payload Too Large",
            414 => "URI Too Long",
            415 => "Unsupported Media Type",
            416 => "Range Not Satisfiable",
            417 => "Expectation Failed",
            418 => "I'm a teapot",
            421 => "Misdirected Request",
            422 => "Unprocessable Entity",
            423 => "Locked",
            424 => "Failed Dependency",
            426 => "Upgrade Required",
            428 => "Precondition Required",
            429 => "Too Many Requests",
            431 => "Request Header Fields Too Large",
            451 => "Unavailable For Legal Reasons",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            505 => "HTTP Version Not Supported",
            _ => "<unknown status code>",
        });
}
