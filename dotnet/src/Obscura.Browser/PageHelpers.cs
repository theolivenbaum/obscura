using System.Globalization;
using System.Text;
using Obscura.Net;

namespace Obscura.Browser;

/// <summary>One <c>@import</c> target with its optional media condition.</summary>
internal sealed record StylesheetImport(string Url, string? Media);

/// <summary>A fetched stylesheet plus the imports it declares.</summary>
internal sealed record LoadedStylesheet(Uri ResponseUrl, IReadOnlyList<StylesheetImport> Imports, string Rules);

/// <summary>Where a materialized author sheet is inserted.</summary>
internal abstract record AuthorStylesheetTarget
{
    private AuthorStylesheetTarget()
    {
    }

    internal sealed record Linked(int LinkIndex) : AuthorStylesheetTarget;

    internal sealed record InlineImport(int StyleIndex) : AuthorStylesheetTarget;
}

/// <summary>
/// The free functions <c>page.rs</c> declares beside <c>impl Page</c>. They are
/// internal because the Rust test module reaches them with <c>use super::{...}</c>.
/// </summary>
internal static partial class PageHelpers
{
    internal const byte MaxStylesheetImportDepth = 4;
    internal const int MaxStylesheetResources = 128;
    internal const ulong DefaultNavigationTimeoutMs = 30_000;

    /// <summary>The first navigation counts, so the low default stops a page that resets location on every load.</summary>
    internal const int DefaultNavigationChainLimit = 10;

    /// <summary>
    /// Parse <c>OBSCURA_GEOLOCATION="lat,lon"</c> for the navigator.geolocation shim.
    /// </summary>
    /// <remarks>
    /// Returns null when unset or malformed, leaving the built-in default in place.
    /// Lets a deployment align the reported coordinates with the region its exit IP
    /// resolves to, so timezone and location stay consistent.
    /// </remarks>
    internal static (double Latitude, double Longitude)? EnvGeolocation()
    {
        string? raw = Environment.GetEnvironmentVariable("OBSCURA_GEOLOCATION");
        if (raw is null)
        {
            return null;
        }
        int comma = raw.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }
        if (!double.TryParse(raw[..comma].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
            || !double.TryParse(raw[(comma + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
        {
            return null;
        }
        bool valid = double.IsFinite(lat) && double.IsFinite(lon)
            && lat >= -90.0 && lat <= 90.0
            && lon >= -180.0 && lon <= 180.0;
        return valid ? (lat, lon) : null;
    }

    internal static byte[]? DecodeDataUri(string uri)
    {
        if (!uri.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }
        string rest = uri["data:".Length..];
        int comma = rest.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }
        string meta = rest[..comma];
        string payload = rest[(comma + 1)..];
        foreach (string token in meta.Split(';'))
        {
            if (string.Equals(token, "base64", StringComparison.OrdinalIgnoreCase))
            {
                var cleaned = new StringBuilder(payload.Length);
                foreach (char c in payload)
                {
                    if (!char.IsWhiteSpace(c))
                    {
                        cleaned.Append(c);
                    }
                }
                try
                {
                    return Convert.FromBase64String(cleaned.ToString());
                }
                catch (FormatException)
                {
                    return null;
                }
            }
        }
        return PercentDecode(payload);
    }

    internal static byte[] PercentDecode(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var output = new List<byte>(bytes.Length);
        int i = 0;
        while (i < bytes.Length)
        {
            if (bytes[i] == (byte)'%' && i + 2 < bytes.Length)
            {
                int high = HexValue(bytes[i + 1]);
                int low = HexValue(bytes[i + 2]);
                if (high >= 0 && low >= 0)
                {
                    output.Add((byte)((high << 4) | low));
                    i += 3;
                    continue;
                }
            }
            output.Add(bytes[i]);
            i += 1;
        }
        return [.. output];
    }

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => -1,
    };

    /// <summary>
    /// Truncate <paramref name="value"/> to at most <paramref name="maxBytes"/> UTF-8
    /// bytes without splitting a character.
    /// </summary>
    /// <remarks>
    /// The evaluated expression logged by <c>Page.Evaluate</c> is caller-controlled,
    /// and Rust's <c>&amp;s[..max]</c> panics when <c>max</c> lands inside a
    /// multi-byte character.
    /// </remarks>
    internal static string TruncateOnCharBoundary(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }
        int bytes = 0;
        int end = 0;
        while (end < value.Length)
        {
            int units = char.IsHighSurrogate(value[end]) && end + 1 < value.Length
                && char.IsLowSurrogate(value[end + 1]) ? 2 : 1;
            int width = Encoding.UTF8.GetByteCount(value.AsSpan(end, units));
            if (bytes + width > maxBytes)
            {
                break;
            }
            bytes += width;
            end += units;
        }
        return value[..end];
    }

    internal static ulong RemainingSettleResourceWarmupMs(
        ulong maxMs,
        TimeSpan elapsed,
        ulong configuredMs)
    {
        TimeSpan remaining = TimeSpan.FromMilliseconds(maxMs) - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return 0;
        }
        // Rust's `Duration::as_millis()` truncates: a sub-millisecond remainder
        // cannot safely fund a millisecond timeout.
        return Math.Min((ulong)Math.Floor(remaining.TotalMilliseconds), configuredMs);
    }

    /// <summary>
    /// True when a JS-initiated navigation would step from a non-file scheme into a
    /// <c>file:</c> URL.
    /// </summary>
    /// <remarks>
    /// That move is an SOP violation because the existing realm survives the
    /// navigation and can read the new document's body.
    /// </remarks>
    internal static bool CrossSchemeToFile(string from, string to)
    {
        Uri? target = PageUrl.TryParse(to);
        bool toIsFile = target is not null
            && string.Equals(target.Scheme, "file", StringComparison.OrdinalIgnoreCase);
        if (!toIsFile)
        {
            return false;
        }
        Uri? source = PageUrl.TryParse(from);
        return source is null || !string.Equals(source.Scheme, "file", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sub-resource fetch policy. http(s) is always fine; <c>data:</c> is allowed
    /// because the bytes are inline in the URI (no network fetch, no SSRF);
    /// <c>file:</c> is only allowed when the page itself was loaded from
    /// <c>file:</c>; everything else (<c>javascript:</c>, <c>chrome:</c>, ...) is
    /// blocked.
    /// </summary>
    /// <remarks>
    /// Real Chrome allows <c>data:</c> subresources by default; Instagram and most
    /// Meta properties depend on this for their inline bootstrap scripts.
    /// </remarks>
    internal static bool SubresourceAllowed(Uri? pageUrl, string resource)
    {
        Uri? target = PageUrl.TryParse(resource);
        if (target is null)
        {
            return false;
        }
        return target.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" or "data" => true,
            "file" => pageUrl is not null
                && string.Equals(pageUrl.Scheme, "file", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// The default <c>strict-origin-when-cross-origin</c> referrer for a
    /// document-initiated navigation.
    /// </summary>
    /// <remarks>
    /// Direct navigations bypass this helper and use an empty referrer.
    /// Referrer-Policy overrides are not yet plumbed through the navigation request.
    /// </remarks>
    internal static string NavigationReferrer(Uri source, Uri target)
    {
        bool sourceWeb = source.Scheme is "http" or "https";
        bool targetWeb = target.Scheme is "http" or "https";
        if (!sourceWeb || !targetWeb
            || (string.Equals(source.Scheme, "https", StringComparison.Ordinal)
                && string.Equals(target.Scheme, "http", StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        return PageUrl.SameOrigin(source, target)
            ? PageUrl.WithoutCredentialsOrFragment(source)
            : PageUrl.AsciiOrigin(source) + "/";
    }

    /// <summary>
    /// Escape a value for safe inclusion inside a JavaScript template literal.
    /// </summary>
    /// <remarks>
    /// Escaping only <c>\</c>, <c>`</c> and <c>${</c> left U+2028 / U+2029 (the
    /// JS-specific line terminators) and other control characters as breakout
    /// vectors. Doing it in one function means future tweaks come back here.
    /// </remarks>
    internal static string EscapeForJsTemplateLiteral(string input)
    {
        var output = new StringBuilder(input.Length);
        foreach (char ch in input)
        {
            switch (ch)
            {
                case '\\':
                    output.Append("\\\\");
                    break;
                case '`':
                    output.Append("\\`");
                    break;
                case '$':
                    output.Append("\\$");
                    break;
                case '\u2028':
                    output.Append("\\u2028");
                    break;
                case '\u2029':
                    output.Append("\\u2029");
                    break;
                case '\0':
                    output.Append("\\0");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        output.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}");
                    }
                    else
                    {
                        output.Append(ch);
                    }
                    break;
            }
        }
        return output.ToString();
    }

    internal static (string Key, Uri Url) CanonicalStylesheetUrl(Uri url)
    {
        Uri stripped = PageUrl.WithoutFragment(url);
        return (stripped.AbsoluteUri, stripped);
    }

    /// <summary>
    /// Expand a cached stylesheet graph in CSS cascade order.
    /// </summary>
    /// <remarks>
    /// Network deduplication is separate from expansion: a shared import is
    /// downloaded once but expanded at each import position, while the active stack
    /// cuts cycles.
    /// </remarks>
    internal static string? MaterializeStylesheetGraph(
        string key,
        IReadOnlyDictionary<string, LoadedStylesheet> sheets,
        IReadOnlyDictionary<string, string> aliases,
        HashSet<string> active)
    {
        string actualKey = aliases.TryGetValue(key, out string? alias) ? alias : key;
        if (!active.Add(actualKey))
        {
            return null;
        }
        if (!sheets.TryGetValue(actualKey, out LoadedStylesheet? sheet))
        {
            active.Remove(actualKey);
            return null;
        }

        var output = new StringBuilder();
        foreach (StylesheetImport import in sheet.Imports)
        {
            Uri? importUrl = PageUrl.TryJoin(sheet.ResponseUrl, import.Url);
            if (importUrl is null)
            {
                continue;
            }
            (string importKey, _) = CanonicalStylesheetUrl(importUrl);
            string? imported = MaterializeStylesheetGraph(importKey, sheets, aliases, active);
            if (imported is null)
            {
                continue;
            }
            if (import.Media is { } media)
            {
                output.Append("@media ").Append(media).Append(" {\n").Append(imported).Append("\n}\n");
            }
            else
            {
                output.Append(imported).Append('\n');
            }
        }
        output.Append(RebaseCssUrls(sheet.Rules, sheet.ResponseUrl));
        active.Remove(actualKey);
        return output.ToString();
    }

    /// <summary>
    /// Preserve the URL base of a fetched stylesheet after it is materialized as
    /// inline CSS.
    /// </summary>
    /// <remarks>
    /// Relative <c>url(...)</c> values resolve against the stylesheet's URL in
    /// browsers, not the document URL; failing to rebase them drops common
    /// background, mask, cursor and font assets from nested theme directories.
    /// </remarks>
    internal static string RebaseCssUrls(string css, Uri baseUrl)
    {
        var output = new StringBuilder(css.Length);
        int index = 0;
        while (index < css.Length)
        {
            ReadOnlySpan<char> rest = css.AsSpan(index);
            if (rest.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = rest[2..].IndexOf("*/", StringComparison.Ordinal);
                if (end >= 0)
                {
                    int length = end + 4;
                    output.Append(rest[..length]);
                    index += length;
                }
                else
                {
                    output.Append(rest);
                    break;
                }
                continue;
            }

            char first = rest[0];
            if (first is '"' or '\'')
            {
                int length = QuotedRunLength(rest, first);
                output.Append(rest[..length]);
                index += length;
                continue;
            }

            bool isUrl = rest.Length >= 4 && rest[..4].Equals("url(", StringComparison.OrdinalIgnoreCase);
            if (!isUrl)
            {
                output.Append(first);
                index += 1;
                continue;
            }

            int close = UrlValueEnd(rest);
            if (close < 0)
            {
                output.Append(rest);
                break;
            }

            string raw = rest[4..close].ToString().Trim();
            string value = Unquote(raw);
            string? resolved = null;
            if (value.Length != 0
                && !value.StartsWith('#')
                && !value.Contains("var(", StringComparison.Ordinal)
                && PageUrl.TryParse(value) is null
                && PageUrl.TryJoin(baseUrl, value) is { } joined)
            {
                resolved = joined.AbsoluteUri;
            }

            if (resolved is not null)
            {
                output.Append("url(\"");
                foreach (char ch in resolved)
                {
                    if (ch is '\\' or '"')
                    {
                        output.Append('\\');
                    }
                    output.Append(ch);
                }
                output.Append("\")");
            }
            else
            {
                output.Append(rest[..(close + 1)]);
            }
            index += close + 1;
        }
        return output.ToString();
    }

    /// <summary>
    /// Extract network-backed <c>url(...)</c> assets while respecting CSS comments
    /// and strings.
    /// </summary>
    /// <remarks>
    /// Linked sheets have already been rebased before materialization; inline
    /// declarations are resolved against the document base here.
    /// </remarks>
    internal static List<string> CssResourceUrls(string css, Uri baseUrl)
    {
        List<string> urls = [];
        int index = 0;
        while (index < css.Length)
        {
            ReadOnlySpan<char> rest = css.AsSpan(index);
            if (rest.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = rest[2..].IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    break;
                }
                index += end + 4;
                continue;
            }

            // `@import url(...)` is a stylesheet dependency, not a paint asset. It is
            // fetched by the bounded stylesheet graph; letting the generic warmup
            // rediscover it issues a second request with the wrong Image type.
            int importLength = CssImportRuleLength(rest);
            if (importLength > 0)
            {
                index += importLength;
                continue;
            }

            // A `@font-face` `src` list is a priority order, not a set of resources.
            // `CssFontFaceRule` collects the ones the renderer will consider and
            // reports the whole block as consumed.
            if (CssFontFaceRule(rest, baseUrl) is { } face)
            {
                urls.AddRange(face.Sources);
                index += face.Length;
                continue;
            }

            char first = rest[0];
            if (first is '"' or '\'')
            {
                index += QuotedRunLength(rest, first);
                continue;
            }

            if (rest.Length < 4 || !rest[..4].Equals("url(", StringComparison.OrdinalIgnoreCase))
            {
                index += 1;
                continue;
            }

            int close = UrlValueEnd(rest);
            if (close < 0)
            {
                break;
            }
            PushCssUrl(rest[4..close].ToString(), baseUrl, urls);
            index += close + 1;
        }
        return urls;
    }

    /// <summary>
    /// Record one <c>url(...)</c> value when it names a network resource.
    /// </summary>
    /// <remarks>
    /// Shared by the generic scan and the <c>@font-face</c> path so the two cannot
    /// disagree about quoting, fragments, <c>data:</c> or an unresolved <c>var()</c>.
    /// </remarks>
    internal static void PushCssUrl(string raw, Uri baseUrl, List<string> urls)
    {
        string value = Unquote(raw.Trim());
        if (value.Length == 0
            || value.StartsWith('#')
            || value.StartsWith("data:", StringComparison.Ordinal)
            || value.Contains("var(", StringComparison.Ordinal))
        {
            return;
        }
        if (PageUrl.TryJoin(baseUrl, value) is not { } url)
        {
            return;
        }
        url = PageUrl.WithoutFragment(url);
        if (url.Scheme is "http" or "https")
        {
            urls.Add(url.AbsoluteUri);
        }
    }

    /// <summary>
    /// The length of a leading <c>@font-face</c> block plus the sources the renderer
    /// will actually consider, in source order.
    /// </summary>
    /// <remarks>
    /// A <c>src</c> list is a priority order, not a set. Both rules come from the
    /// layer this warms: <c>font_face_declaration</c> ends in <c>.last()</c>, so a
    /// rule carrying several <c>src</c> descriptors uses the final one, which is what
    /// the <c>src: url(.eot); src: url(...)</c> idiom relies on; and
    /// <c>font_source_may_be_supported</c> drops <c>.eot</c> and <c>.svg</c> after
    /// stripping the query and fragment. A malformed block is left to the normal
    /// scanner so this cannot swallow the rules that follow it.
    /// </remarks>
    internal static (int Length, List<string> Sources)? CssFontFaceRule(ReadOnlySpan<char> css, Uri baseUrl)
    {
        const string Marker = "@font-face";
        if (css.Length < Marker.Length || !css[..Marker.Length].Equals(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        int openRelative = css[Marker.Length..].IndexOf('{');
        if (openRelative < 0 || !css[Marker.Length..(Marker.Length + openRelative)].Trim().IsEmpty)
        {
            return null;
        }
        int open = Marker.Length + openRelative;
        int blockEnd = CssBlockEnd(css[open..]);
        if (blockEnd < 0)
        {
            return null;
        }
        int close = open + blockEnd;

        List<string> urls = [];
        string? src = CssLastDeclaration(css[(open + 1)..close].ToString(), "src");
        if (src is not null)
        {
            foreach (string value in CssUrlValues(src))
            {
                if (FontSourceIsDecodable(value))
                {
                    PushCssUrl(value, baseUrl, urls);
                }
            }
        }
        return (close + 1, urls);
    }

    /// <summary>
    /// The offset of the brace closing the block <paramref name="css"/> opens, or -1
    /// when it is unterminated. Braces inside strings do not count.
    /// </summary>
    internal static int CssBlockEnd(ReadOnlySpan<char> css)
    {
        int depth = 0;
        char quote = '\0';
        bool escaped = false;
        for (int offset = 0; offset < css.Length; offset++)
        {
            char ch = css[offset];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            switch (ch)
            {
                case '"':
                case '\'':
                    quote = ch;
                    break;
                case '{':
                    depth += 1;
                    break;
                case '}':
                    depth -= 1;
                    if (depth == 0)
                    {
                        return offset;
                    }
                    break;
                default:
                    break;
            }
        }
        return -1;
    }

    /// <summary>
    /// The value of the last declaration named <paramref name="name"/> in a
    /// declaration block, because that is what the cascade resolves to and what
    /// <c>obscura-render</c> reads.
    /// </summary>
    internal static string? CssLastDeclaration(string block, string name)
    {
        string? found = null;
        int start = 0;
        int depth = 0;
        char quote = '\0';
        bool escaped = false;
        List<int> boundaries = [];
        for (int offset = 0; offset < block.Length; offset++)
        {
            char ch = block[offset];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            switch (ch)
            {
                case '"':
                case '\'':
                    quote = ch;
                    break;
                case '(':
                    depth += 1;
                    break;
                case ')':
                    depth = depth > 0 ? depth - 1 : 0;
                    break;
                case ';' when depth == 0:
                    boundaries.Add(offset);
                    break;
                default:
                    break;
            }
        }
        boundaries.Add(block.Length);
        foreach (int end in boundaries)
        {
            string declaration = block[start..end];
            start = Math.Min(end + 1, block.Length);
            int colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }
            if (string.Equals(declaration[..colon].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                found = declaration[(colon + 1)..].Trim();
            }
        }
        return found;
    }

    /// <summary>The <c>url(...)</c> values of one declaration, unquoted, in source order.</summary>
    internal static List<string> CssUrlValues(string value)
    {
        string lower = value.ToLowerInvariant();
        List<string> output = [];
        int cursor = 0;
        while (true)
        {
            int relative = lower.AsSpan(cursor).IndexOf("url(", StringComparison.Ordinal);
            if (relative < 0)
            {
                break;
            }
            int start = cursor + relative + 4;
            int endRelative = value.AsSpan(start).IndexOf(')');
            if (endRelative < 0)
            {
                break;
            }
            int end = start + endRelative;
            string unquoted = Unquote(value[start..end].Trim());
            if (unquoted.Length != 0)
            {
                output.Add(unquoted);
            }
            cursor = end + 1;
        }
        return output;
    }

    /// <summary>
    /// Whether the renderer can decode a font source, by the same extension rule
    /// <c>obscura-render</c> applies before it will even consider one.
    /// </summary>
    internal static bool FontSourceIsDecodable(string src)
    {
        int cut = src.AsSpan().IndexOfAny('?', '#');
        string path = (cut < 0 ? src : src[..cut]).ToLowerInvariant();
        return !path.EndsWith(".eot", StringComparison.Ordinal)
            && !path.EndsWith(".svg", StringComparison.Ordinal);
    }

    /// <summary>
    /// The length of a leading CSS <c>@import</c> rule including its semicolon, or 0
    /// when this is not one.
    /// </summary>
    /// <remarks>
    /// Semicolons inside quoted URLs, comments or <c>url()</c> parentheses do not end
    /// the rule. A malformed import is left to the normal scanner so this helper
    /// cannot swallow following declarations.
    /// </remarks>
    internal static int CssImportRuleLength(ReadOnlySpan<char> css)
    {
        const string Marker = "@import";
        if (css.Length < Marker.Length || !css[..Marker.Length].Equals(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (css.Length > Marker.Length)
        {
            char next = css[Marker.Length];
            if (char.IsAsciiLetterOrDigit(next) || next == '-' || next == '_')
            {
                return 0;
            }
        }

        int index = Marker.Length;
        char quote = '\0';
        bool escaped = false;
        int parenDepth = 0;
        while (index < css.Length)
        {
            char ch = css[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == quote)
                {
                    quote = '\0';
                }
                index += 1;
                continue;
            }
            if (ch == '/' && index + 1 < css.Length && css[index + 1] == '*')
            {
                int end = css[(index + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    return 0;
                }
                index += end + 4;
                continue;
            }
            switch (ch)
            {
                case '\'':
                case '"':
                    quote = ch;
                    break;
                case '(':
                    parenDepth += 1;
                    break;
                case ')':
                    parenDepth = parenDepth > 0 ? parenDepth - 1 : 0;
                    break;
                case ';' when parenDepth == 0:
                    return index + 1;
                case '{' when parenDepth == 0:
                    return 0;
                default:
                    break;
            }
            index += 1;
        }
        return 0;
    }

    internal static ResourceType RenderResourceType(Uri url)
    {
        string path = url.AbsolutePath.ToLowerInvariant();
        foreach (string extension in FontExtensions)
        {
            if (path.EndsWith(extension, StringComparison.Ordinal))
            {
                return ResourceType.Font;
            }
        }
        return ResourceType.Image;
    }

    private static readonly string[] FontExtensions = [".woff", ".woff2", ".ttf", ".otf", ".eot"];

    /// <summary>
    /// Pull leading <c>@import</c> rules out of a stylesheet, returning each target
    /// with its optional media condition plus the CSS with those statements removed.
    /// </summary>
    /// <remarks>
    /// Browsers fetch media-gated imports even when they do not match the current
    /// screen; preserving the condition lets the same bytes participate in a later
    /// PDF print cascade.
    /// </remarks>
    internal static (List<StylesheetImport> Imports, string Stripped) SplitCssImports(string css)
    {
        List<StylesheetImport> urls = [];
        var stripped = new StringBuilder(css.Length);
        string rest = css;
        while (true)
        {
            int pos = rest.IndexOf("@import", StringComparison.Ordinal);
            if (pos < 0)
            {
                stripped.Append(rest);
                break;
            }
            // Real sheets place `@import` at the top (after an optional @charset), so
            // scanning for it anywhere is safe in practice and tolerates minified
            // whitespace. Text before this match carries through unchanged.
            stripped.Append(rest, 0, pos);
            string after = rest[(pos + "@import".Length)..];
            int semi = after.IndexOf(';', StringComparison.Ordinal);
            if (semi < 0)
            {
                // Malformed; keep the remainder verbatim.
                stripped.Append(rest, pos, rest.Length - pos);
                break;
            }
            string statement = after[..semi];
            StylesheetImport? target = ParseImportUrl(statement);
            if (target is not null)
            {
                urls.Add(target);
            }
            else
            {
                // Could not parse a URL; preserve the statement so we don't lose it.
                stripped.Append("@import").Append(after, 0, semi + 1);
            }
            rest = after[(semi + 1)..];
        }
        return (urls, stripped.ToString());
    }

    /// <summary>
    /// Extract the URL and optional trailing media query from an <c>@import</c>
    /// statement body (the text between <c>@import</c> and <c>;</c>).
    /// </summary>
    internal static StylesheetImport? ParseImportUrl(string statement)
    {
        string s = statement.Trim();
        bool isUrlFunction = s.Length >= 4 && s.AsSpan(0, 4).Equals("url(", StringComparison.OrdinalIgnoreCase);
        string url;
        string media;
        if (isUrlFunction)
        {
            string rest = s[4..];
            int end = rest.IndexOf(')', StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }
            url = rest[..end].Trim().Trim('"', '\'');
            media = rest[(end + 1)..].Trim();
        }
        else
        {
            if (s.Length == 0 || (s[0] != '"' && s[0] != '\''))
            {
                return null;
            }
            char quote = s[0];
            string rest = s[1..];
            int end = rest.IndexOf(quote, StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }
            url = rest[..end];
            media = rest[(end + 1)..].Trim();
        }
        return url.Length == 0 ? null : new StylesheetImport(url, media.Length == 0 ? null : media);
    }

    /// <summary>
    /// Materialize a fetched linked sheet immediately after its source
    /// <c>&lt;link&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Keeping each sheet at its document position matters when linked and inline
    /// author sheets are interleaved. Appending one aggregate <c>&lt;style&gt;</c> to
    /// <c>&lt;head&gt;</c> makes every external rule later than every inline rule,
    /// which changes the cascade even when the fetches complete in order. The
    /// synthetic style retains the link's effective media query so the same bytes can
    /// enter print layout without leaking into screen layout.
    /// </remarks>
    internal static string MaterializeLinkedStylesheetScript(int linkIndex, string css)
    {
        string escapedCss = EscapeForJsTemplateLiteral(css);
        return $$"""
            (function() {
                        var links = document.querySelectorAll('link[rel~="stylesheet"]');
                        var link = links[{{linkIndex.ToString(CultureInfo.InvariantCulture)}}];
                        if (!link || !link.parentNode) return;
                        var style = null;
                        function effectiveMedia() {
                            // Until the generic Element shim reflects HTMLLinkElement.media,
                            // `this.media = "all"` creates an own property while the parsed
                            // media="print" attribute remains unchanged.
                            if (Object.prototype.hasOwnProperty.call(link, 'media')) {
                                return String(link.media || '');
                            }
                            return link.getAttribute('media') || '';
                        }
                        function syncSheet() {
                            if (!style) {
                                style = document.createElement('style');
                                style.setAttribute('data-obscura-external-stylesheets', '');
                                style.textContent = `{{escapedCss}}`;
                                globalThis.__obscura_registerLinkedStylesheet(link, style);
                            }
                            var enabled = link.parentNode
                                && !link.disabled
                                && !link.hasAttribute('disabled');
                            if (!enabled) {
                                if (style && style.parentNode) style.parentNode.removeChild(style);
                                return;
                            }
                            var media = effectiveMedia().trim();
                            if (media) style.setAttribute('media', media);
                            else style.removeAttribute('media');
                            if (!style.parentNode) {
                                link.parentNode.insertBefore(style, link.nextSibling);
                            }
                        }

                        // A non-matching sheet still loads and fires its event. Its handler
                        // may then make the sheet applicable (the common
                        // media=print/onload="this.media='all'" async-CSS pattern).
                        syncSheet();
                        try { link.dispatchEvent(new Event('load')); }
                        finally { syncSheet(); }
                    })()
            """;
    }

    /// <summary>
    /// Materialize one fetched <c>@import</c> immediately before its source inline
    /// <c>&lt;style&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Imported rules precede the importing sheet in the author cascade, and inherit
    /// the source sheet's own media condition in addition to the import rule's media
    /// wrapper.
    /// </remarks>
    internal static string MaterializeInlineImportScript(int styleIndex, string css)
    {
        string escapedCss = EscapeForJsTemplateLiteral(css);
        return $$"""
            (function() {
                        var styles = document.querySelectorAll('style');
                        var source = null;
                        var authorIndex = -1;
                        for (var i = 0; i < styles.length; i++) {
                            var candidate = styles[i];
                            if (candidate.hasAttribute('data-obscura-external-stylesheets')
                                || candidate.hasAttribute('data-obscura-inline-import')) continue;
                            authorIndex++;
                            if (authorIndex === {{styleIndex.ToString(CultureInfo.InvariantCulture)}}) { source = candidate; break; }
                        }
                        if (!source || !source.parentNode) return;
                        var imported = document.createElement('style');
                        imported.setAttribute('data-obscura-inline-import', '');
                        var media = source.getAttribute('media') || '';
                        if (media.trim()) imported.setAttribute('media', media);
                        imported.textContent = `{{escapedCss}}`;
                        source.parentNode.insertBefore(imported, source);
                    })()
            """;
    }

    internal static bool ScriptResponseIsExecutable(int status) => status is >= 200 and <= 299;

    internal static bool UrlMatchesCdpPattern(string pattern, string url)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        ReadOnlySpan<char> remainder = url;
        bool first = true;
        foreach (Range range in SplitOnStar(pattern))
        {
            ReadOnlySpan<char> part = pattern.AsSpan(range);
            if (part.IsEmpty)
            {
                continue;
            }
            int index = remainder.IndexOf(part, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }
            if (first && !pattern.StartsWith('*') && index != 0)
            {
                return false;
            }
            remainder = remainder[(index + part.Length)..];
            first = false;
        }
        return pattern.EndsWith('*') || remainder.IsEmpty;
    }

    private static List<Range> SplitOnStar(string pattern)
    {
        List<Range> ranges = [];
        int start = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*')
            {
                ranges.Add(start..i);
                start = i + 1;
            }
        }
        ranges.Add(start..pattern.Length);
        return ranges;
    }

    /// <summary>
    /// Whether a Content-Type is text-like and can be stored as a UTF-8 string.
    /// </summary>
    /// <remarks>
    /// Everything else (images, PDF, fonts, octet-stream) is binary and must be
    /// base64-encoded so <c>Network.getResponseBody</c> returns intact bytes.
    /// </remarks>
    internal static bool IsTextLikeContentType(string? contentType)
    {
        if (contentType is null)
        {
            // No Content-Type: assume text (matches the HTML-parse default).
            return true;
        }
        int semi = contentType.IndexOf(';', StringComparison.Ordinal);
        string ct = (semi < 0 ? contentType : contentType[..semi]).Trim().ToLowerInvariant();
        if (ct.Length == 0)
        {
            return true;
        }
        return ct.StartsWith("text/", StringComparison.Ordinal)
            || ct is "application/json" or "application/xml" or "application/xhtml+xml"
                or "application/javascript" or "application/ecmascript" or "image/svg+xml"
            || ct.EndsWith("+json", StringComparison.Ordinal)
            || ct.EndsWith("+xml", StringComparison.Ordinal);
    }

    internal static TimeSpan DefaultNavigationTimeout() =>
        NavigationTimeoutFromEnvValue(Environment.GetEnvironmentVariable("OBSCURA_NAV_TIMEOUT_MS"));

    internal static TimeSpan NavigationTimeoutFromEnvValue(string? value) =>
        TimeSpan.FromMilliseconds(
            value is not null
            && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong ms)
                ? ms
                : DefaultNavigationTimeoutMs);

    internal static int DefaultNavigationChainLimitValue() =>
        NavigationChainLimitFromEnvValue(Environment.GetEnvironmentVariable("OBSCURA_NAV_CHAIN_LIMIT"));

    /// <summary>
    /// Only an unreadable value falls back to the default, so
    /// <c>OBSCURA_NAV_CHAIN_LIMIT=0</c> and <c>SetNavigationChainLimit(0)</c> agree.
    /// </summary>
    internal static int NavigationChainLimitFromEnvValue(string? value) =>
        value is not null
        && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong limit)
            ? (int)Math.Max(1, Math.Min(limit, int.MaxValue))
            : DefaultNavigationChainLimit;

    /// <summary>
    /// How many child frame realms one document may hold at once.
    /// </summary>
    /// <remarks>
    /// Real pages use a handful; the cap exists so a page that creates iframes in a
    /// loop cannot make the engine hold an unbounded number of contexts and DOM
    /// trees. Frames are released when the document is replaced.
    /// </remarks>
    internal static int MaxLiveFrames() =>
        Environment.GetEnvironmentVariable("OBSCURA_MAX_LIVE_FRAMES") is { } value
        && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)
            ? (int)Math.Min(parsed, int.MaxValue)
            : 64;

    internal static int ResponseBodyEntryLimit() =>
        Environment.GetEnvironmentVariable("OBSCURA_NETWORK_BODY_BUFFER_ENTRIES") is { } value
        && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)
            ? (int)Math.Min(parsed, int.MaxValue)
            : 128;

    internal static int ResponseBodyByteLimit() =>
        Environment.GetEnvironmentVariable("OBSCURA_NETWORK_BODY_BUFFER_BYTES") is { } value
        && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)
            ? (int)Math.Min(parsed, int.MaxValue)
            : 2 * 1024 * 1024;

    internal static ulong EnvUlong(string key, ulong fallback) =>
        Environment.GetEnvironmentVariable(key) is { } value
        && ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)
            ? parsed
            : fallback;

    private static string Unquote(string raw) =>
        raw.Length >= 2
        && ((raw.StartsWith('"') && raw.EndsWith('"')) || (raw.StartsWith('\'') && raw.EndsWith('\'')))
            ? raw[1..^1]
            : raw;

    /// <summary>The length of the quoted run starting at <paramref name="rest"/>[0].</summary>
    private static int QuotedRunLength(ReadOnlySpan<char> rest, char quote)
    {
        bool escaped = false;
        int length = 1;
        for (int i = 1; i < rest.Length; i++)
        {
            char ch = rest[i];
            length += 1;
            if (escaped)
            {
                escaped = false;
            }
            else if (ch == '\\')
            {
                escaped = true;
            }
            else if (ch == quote)
            {
                break;
            }
        }
        return length;
    }

    /// <summary>The offset of the <c>)</c> closing a leading <c>url(</c>, or -1.</summary>
    private static int UrlValueEnd(ReadOnlySpan<char> rest)
    {
        char quote = '\0';
        bool escaped = false;
        for (int offset = 4; offset < rest.Length; offset++)
        {
            char ch = rest[offset];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == ')')
            {
                return offset;
            }
        }
        return -1;
    }
}
