using System.Text;

namespace Obscura.Net;

/// <summary>
/// Charset detection and decoding for HTTP response bodies (port of
/// <c>crates/obscura-net/src/encoding.rs</c>).
///
/// obscura used to call the equivalent of a lossy UTF-8 decode on every response
/// body, which silently corrupts every non-UTF-8 page (GBK, Big5, Shift-JIS,
/// Windows-125x, EUC-KR, ISO-8859-x). Picking the right decoder is required for
/// scraping non-Latin sites at all.
///
/// Detection order, mirroring real browsers (HTML5 spec section 8.2.2.4):
///   1. <c>Content-Type: text/html; charset=...</c> from the HTTP response header.
///   2. <c>&lt;meta charset&gt;</c> sniffed from the first 1024 bytes of the body.
///   3. Default UTF-8.
///
/// For non-HTML resources (JS, CSS, JSON), only steps 1 and 3 apply.
/// </summary>
public static class ContentEncoding
{
    /// <summary>Where <see cref="DetectEncoding"/> picked the encoding from.</summary>
    public const string SourceContentType = "content-type";

    /// <summary>The encoding came from a sniffed <c>&lt;meta charset&gt;</c>.</summary>
    public const string SourceMetaCharset = "meta-charset";

    /// <summary>Nothing declared an encoding, so UTF-8 was assumed.</summary>
    public const string SourceDefaultUtf8 = "default-utf8";

    private const string PctHex = "0123456789ABCDEF";

    /// <summary>
    /// WHATWG canonical (lowercased) name for an encoding label, or null if the
    /// label is not a known encoding. Backs <c>TextDecoder</c>'s label validation
    /// and its <c>.encoding</c> property.
    /// </summary>
    public static string? LabelName(string label) => WhatwgEncoding.LabelName(label);

    /// <summary>
    /// Decode <paramref name="bytes"/> with an explicit encoding label, with
    /// TextDecoder semantics. Returns null when the label is unknown, or (when
    /// <paramref name="fatal"/>) when the input is not valid in that encoding.
    /// Non-fatal decoding replaces errors with U+FFFD.
    /// </summary>
    public static string? DecodeWithLabel(string label, ReadOnlySpan<byte> bytes, bool fatal, bool ignoreBom)
    {
        var encoding = WhatwgEncoding.ForLabel(label);
        if (encoding is null)
        {
            return null;
        }

        if (fatal)
        {
            return encoding.DecodeFatal(bytes, ignoreBom);
        }

        return ignoreBom ? encoding.DecodeWithoutBomHandling(bytes) : encoding.Decode(bytes);
    }

    /// <summary>
    /// Decode an HTTP response body. <paramref name="contentTypeHeader"/> is the
    /// raw header value if present (e.g. <c>text/html; charset=gbk</c>). For HTML
    /// resources the parser also sniffs <c>&lt;meta charset&gt;</c> in the first 1KB.
    /// </summary>
    public static string DecodeResponse(ReadOnlySpan<byte> bytes, string? contentTypeHeader)
    {
        var (encoding, _) = DetectEncoding(bytes, contentTypeHeader);
        return encoding.Decode(bytes);
    }

    /// <summary>
    /// Like <see cref="DecodeResponse"/>, but also returns the WHATWG canonical
    /// name of the encoding that was used (e.g. "EUC-JP", "Shift_JIS", "UTF-8").
    /// Callers use the name to expose <c>document.characterSet</c> and to do
    /// document-encoding-aware URL query serialization (the WHATWG "encoding
    /// override").
    /// </summary>
    public static (string Text, string EncodingName) DecodeResponseWithName(
        ReadOnlySpan<byte> bytes,
        string? contentTypeHeader)
    {
        var (encoding, _) = DetectEncoding(bytes, contentTypeHeader);
        return (encoding.Decode(bytes), encoding.Name);
    }

    /// <summary>
    /// Same as <see cref="DecodeResponse"/> but skips the <c>&lt;meta charset&gt;</c>
    /// sniff. Use for non-HTML resources where embedded HTML meta tags are not
    /// authoritative (script and style bodies, JSON, plain text).
    /// </summary>
    public static string DecodeNonHtml(ReadOnlySpan<byte> bytes, string? contentTypeHeader)
    {
        var charset = contentTypeHeader is null ? null : CharsetFromContentType(contentTypeHeader);
        var encoding = (charset is null ? null : WhatwgEncoding.ForLabel(charset)) ?? WhatwgEncoding.Utf8;
        return encoding.Decode(bytes);
    }

    /// <summary>
    /// Resolve the encoding to use for an HTML response, mirroring the HTML5
    /// detection order. Returns the encoding and a tag describing where it was
    /// picked from (for logging / tests).
    /// </summary>
    public static (WhatwgEncoding Encoding, string Source) DetectEncoding(
        ReadOnlySpan<byte> bytes,
        string? contentTypeHeader)
    {
        if (contentTypeHeader is not null)
        {
            var charset = CharsetFromContentType(contentTypeHeader);
            if (charset is not null)
            {
                var fromHeader = WhatwgEncoding.ForLabel(charset);
                if (fromHeader is not null)
                {
                    return (fromHeader, SourceContentType);
                }
            }
        }

        var sniffed = SniffMetaCharset(bytes);
        if (sniffed is not null)
        {
            return (sniffed, SourceMetaCharset);
        }

        return (WhatwgEncoding.Utf8, SourceDefaultUtf8);
    }

    /// <summary>
    /// WHATWG URL "percent-encode after encoding" for the query component, using a
    /// non-UTF-8 document encoding override (<paramref name="label"/>).
    /// <paramref name="query"/> is the already UTF-8-percent-decoded query string.
    /// ASCII code points use the (special-)query percent-encode set so real query
    /// delimiters (<c>=</c>, <c>&amp;</c>) stay literal; runs of non-ASCII code
    /// points are encoded to the target charset with every byte percent-encoded.
    /// Returns null when the label is unknown.
    /// </summary>
    public static string? UrlEncodeQuery(string query, string label, bool special)
    {
        var encoding = WhatwgEncoding.ForLabel(label);
        if (encoding is null)
        {
            return null;
        }

        var output = new StringBuilder(query.Length * 3);
        int? runStart = null;
        var index = 0;
        while (index < query.Length)
        {
            var length = char.IsHighSurrogate(query[index]) && index + 1 < query.Length
                && char.IsLowSurrogate(query[index + 1])
                ? 2
                : 1;
            var c = query[index];
            if (length == 1 && char.IsAscii(c))
            {
                if (runStart is { } start)
                {
                    EncodeRunPct(output, query[start..index], encoding);
                    runStart = null;
                }

                PushQueryAscii(output, (byte)c, special);
            }
            else
            {
                runStart ??= index;
            }

            index += length;
        }

        if (runStart is { } tail)
        {
            EncodeRunPct(output, query[tail..], encoding);
        }

        return output.ToString();
    }

    private static void PushPct(StringBuilder output, byte b)
    {
        output.Append('%');
        output.Append(PctHex[b >> 4]);
        output.Append(PctHex[b & 0x0F]);
    }

    /// <summary>
    /// Append an ASCII byte to a URL query string, percent-encoding it when it is
    /// in the WHATWG query percent-encode set (C0 controls, space, <c>"</c>,
    /// <c>#</c>, <c>&lt;</c>, <c>&gt;</c>, 0x7F), plus <c>'</c> for special schemes
    /// (the special-query set). ASCII delimiters like <c>=</c> and <c>&amp;</c> are
    /// left literal, so structured queries survive.
    /// </summary>
    private static void PushQueryAscii(StringBuilder output, byte b, bool special)
    {
        var mustEncode = b <= 0x20
            || b == 0x7F
            || b is 0x22 or 0x23 or 0x3C or 0x3E
            || (special && b == 0x27);
        if (mustEncode)
        {
            PushPct(output, b);
        }
        else
        {
            output.Append((char)b);
        }
    }

    /// <summary>
    /// Encode a run of non-ASCII code points to the target charset and percent-
    /// encode EVERY resulting byte. The bytes serialize a non-ASCII character, so
    /// all of them are escaped (this is what the WPT legacy-mb encode-href tests
    /// expect, e.g. Big5 U+4E00 -> <c>%A4%40</c> even though the 0x40 trail byte is
    /// ASCII). Unmappable code points become the literal
    /// <c>%26%23&lt;decimal&gt;%3B</c> sequence (a percent-encoded <c>&amp;#NNN;</c>
    /// numeric character reference), per the URL spec.
    /// </summary>
    private static void EncodeRunPct(StringBuilder output, string run, WhatwgEncoding encoding)
    {
        foreach (var rune in run.EnumerateRunes())
        {
            if (encoding.TryEncodeRune(rune, out var bytes))
            {
                foreach (var b in bytes)
                {
                    PushPct(output, b);
                }
            }
            else
            {
                output.Append("%26%23");
                output.Append(rune.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                output.Append("%3B");
            }
        }
    }

    /// <summary>Pull the <c>charset=</c> parameter out of a Content-Type header value.</summary>
    internal static string? CharsetFromContentType(string header)
    {
        foreach (var part in header.Split(';'))
        {
            var trimmed = part.Trim();
            string? rest = null;
            if (trimmed.StartsWith("charset=", StringComparison.Ordinal))
            {
                rest = trimmed["charset=".Length..];
            }
            else if (trimmed.StartsWith("Charset=", StringComparison.Ordinal))
            {
                rest = trimmed["Charset=".Length..];
            }

            if (rest is not null)
            {
                // Strip surrounding quotes if present.
                var value = rest.Trim('"', '\'').Trim();
                if (value.Length != 0)
                {
                    return value.ToLowerInvariant();
                }
            }

            // Some servers send `Content-Type: text/html; CHARSET = gbk`.
            var lower = trimmed.ToLowerInvariant();
            if (lower.StartsWith("charset", StringComparison.Ordinal))
            {
                var after = lower["charset".Length..].TrimStart();
                if (after.StartsWith('='))
                {
                    var value = after[1..].Trim().Trim('"', '\'');
                    if (value.Length != 0)
                    {
                        return value.ToLowerInvariant();
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Return an exact attribute value from a lowercased <c>&lt;meta ...&gt;</c> tag.
    ///
    /// This deliberately tokenizes attribute names instead of searching for a
    /// substring: <c>data-charset</c> and a description containing
    /// <c>charset=...</c> are not character encoding declarations.
    /// </summary>
    internal static string? MetaAttribute(string tag, string target)
    {
        if (!tag.StartsWith("<meta", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = tag["<meta".Length..].AsSpan();
        if (rest.Length > 0 && !IsAsciiWhitespace(rest[0]) && rest[0] is not ('>' or '/'))
        {
            return null;
        }

        while (!rest.IsEmpty)
        {
            var skip = 0;
            while (skip < rest.Length && (IsAsciiWhitespace(rest[skip]) || rest[skip] == '/'))
            {
                skip++;
            }

            rest = rest[skip..];
            if (rest.IsEmpty || rest[0] == '>')
            {
                break;
            }

            var nameEnd = 0;
            while (nameEnd < rest.Length
                && !IsAsciiWhitespace(rest[nameEnd])
                && rest[nameEnd] is not ('=' or '>' or '/'))
            {
                nameEnd++;
            }

            if (nameEnd == 0)
            {
                rest = rest[1..];
                continue;
            }

            var name = rest[..nameEnd];
            rest = rest[nameEnd..].TrimStart();

            var value = ReadOnlySpan<char>.Empty;
            if (!rest.IsEmpty && rest[0] == '=')
            {
                var afterEquals = rest[1..].TrimStart();
                if (!afterEquals.IsEmpty && afterEquals[0] is '"' or '\'')
                {
                    var quote = afterEquals[0];
                    var quoted = afterEquals[1..];
                    var end = quoted.IndexOf(quote);
                    if (end >= 0)
                    {
                        value = quoted[..end];
                        rest = quoted[(end + 1)..];
                    }
                    else
                    {
                        value = quoted;
                        rest = ReadOnlySpan<char>.Empty;
                    }
                }
                else
                {
                    var end = 0;
                    while (end < afterEquals.Length
                        && !IsAsciiWhitespace(afterEquals[end])
                        && afterEquals[end] is not ('>' or '/'))
                    {
                        end++;
                    }

                    value = afterEquals[..end];
                    rest = afterEquals[end..];
                }
            }

            if (name.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return value.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Scan the first 1024 bytes for a <c>&lt;meta charset="..."&gt;</c> or
    /// <c>&lt;meta http-equiv="Content-Type" content="...; charset=..."&gt;</c>
    /// declaration. We only look at ASCII bytes; valid meta-charset declarations
    /// are always ASCII regardless of the page's actual encoding.
    /// </summary>
    private static WhatwgEncoding? SniffMetaCharset(ReadOnlySpan<byte> bytes)
    {
        var prefixLength = Math.Min(bytes.Length, 1024);
        var prefix = bytes[..prefixLength];
        // Lossy is fine: any meta charset attribute is ASCII, even on a non-UTF-8 page.
        var s = System.Text.Encoding.UTF8.GetString(prefix).ToLowerInvariant();

        // Look for any `<meta ... charset=...>` pattern in the first 1KB. We
        // intentionally accept both the modern shorthand (`<meta charset=gbk>`)
        // and the legacy http-equiv form.
        var pos = 0;
        while (pos < s.Length)
        {
            var metaStart = s.IndexOf("<meta", pos, StringComparison.Ordinal);
            if (metaStart < 0)
            {
                break;
            }

            var closing = s.IndexOf('>', metaStart);
            var end = closing < 0 ? s.Length : closing;
            var tag = s[metaStart..end];

            var charset = MetaAttribute(tag, "charset");
            if (!string.IsNullOrEmpty(charset))
            {
                var encoding = WhatwgEncoding.ForLabel(charset);
                if (encoding is not null)
                {
                    return encoding;
                }
            }

            var httpEquiv = MetaAttribute(tag, "http-equiv");
            if (httpEquiv is not null
                && httpEquiv.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                var content = MetaAttribute(tag, "content");
                var declared = content is null ? null : CharsetFromContentType(content);
                var encoding = declared is null ? null : WhatwgEncoding.ForLabel(declared);
                if (encoding is not null)
                {
                    return encoding;
                }
            }

            pos = end + 1;
            if (pos >= s.Length)
            {
                break;
            }
        }

        return null;
    }

    private static bool IsAsciiWhitespace(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';
}
