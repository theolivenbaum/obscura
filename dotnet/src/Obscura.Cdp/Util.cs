using System.Text;
using Obscura.Js.Url;

namespace Obscura.Cdp;

/// <summary>Helpers shared across CDP domain handlers.</summary>
/// <remarks>
/// The file-scheme detector here is reused by every CDP entrypoint that can
/// trigger a navigation, so we do not end up with one domain enforcing the
/// <c>--allow-file-access</c> gate and another silently letting <c>file://</c>
/// through (see GHSA-q55h-vfv9-qcr5 and its incomplete-fix variant in
/// <c>Target.createTarget</c>).
/// </remarks>
public static class CdpUtil
{
    /// <summary>
    /// True when <paramref name="raw"/> parses as a <c>file:</c>-scheme URL, or
    /// syntactically starts with <c>file:</c> after a possible leading-whitespace
    /// strip. Matching is case-insensitive on the scheme so neither
    /// <c>FILE://</c> nor <c>File://</c> slips past callers that gate on
    /// <c>file://</c>.
    /// </summary>
    public static bool UrlIsFileScheme(string raw)
    {
        var parsed = UrlRecord.Parse(raw);
        if (parsed is not null)
        {
            return string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase);
        }

        return raw.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Embed an objectId as a JS string literal, quotes included.</summary>
    /// <remarks>
    /// <para>
    /// objectIds are interpolated into generated snippets that look up
    /// <c>__obscura_objects[&lt;here&gt;]</c>. Escaping <c>\</c> and <c>'</c> by
    /// hand covers the two characters that close a single-quoted literal and
    /// leaves every C0 control alone - and a raw newline ends a JS string literal
    /// just as a stray quote does. The snippet is then a syntax error, the lookup
    /// yields nothing, and the caller is told nothing:
    /// <c>Runtime.getProperties</c> answers with an empty property list and
    /// <c>DOM.describeNode</c> falls back to node 0, so a client is handed the
    /// document where it asked for an element.
    /// </para>
    /// <para>
    /// Not an injection vector, and it never was: the worst case is an
    /// unterminated string, i.e. a clean resolution failure. What makes it worth
    /// removing is that the failure is invisible from the outside.
    /// </para>
    /// <para>
    /// The ids are not all ours to shape. <c>Runtime.getProperties</c> mints
    /// child ids as <c>parentId + "::" + key</c>, and the key is a property name
    /// off a page object, so the page decides what ends up inside the literal.
    /// </para>
    /// <para>
    /// JSON string syntax is a subset of JavaScript's, so serialising the id
    /// gives a double-quoted literal that escapes <c>"</c>, <c>\</c> and every C0
    /// control at once. Callers interpolate the result *without* adding quotes of
    /// their own.
    /// </para>
    /// </remarks>
    public static string ObjectIdLiteral(string objectId) => CdpJson.String(objectId);

    /// <summary>
    /// Truncate <paramref name="value"/> to at most <paramref name="max"/> UTF-8
    /// bytes, never splitting a character.
    /// </summary>
    /// <remarks>
    /// The Rust helper exists because <c>&amp;s[..max]</c> panics when
    /// <paramref name="max"/> lands inside a multi-byte character, and the strings
    /// truncated for log previews are attacker-controlled (raw WebSocket frames,
    /// intercepted URLs). C# indexes UTF-16 rather than UTF-8, so the same input
    /// would silently produce a different preview length instead of a panic; the
    /// budget is counted in UTF-8 bytes here to keep the previews identical, and
    /// a surrogate pair is never split either.
    /// </remarks>
    public static string TruncateOnCharBoundary(string value, int max)
    {
        if (max <= 0)
        {
            return string.Empty;
        }

        var total = Encoding.UTF8.GetByteCount(value);
        if (total <= max)
        {
            return value;
        }

        var used = 0;
        var end = 0;
        while (end < value.Length)
        {
            var runeLength = char.IsHighSurrogate(value[end]) && end + 1 < value.Length &&
                             char.IsLowSurrogate(value[end + 1])
                ? 2
                : 1;
            var bytes = Encoding.UTF8.GetByteCount(value.AsSpan(end, runeLength));
            if (used + bytes > max)
            {
                break;
            }

            used += bytes;
            end += runeLength;
        }

        return value[..end];
    }

    /// <summary>The UTF-8 length the Rust helper measures against.</summary>
    public static int Utf8Length(string value) => Encoding.UTF8.GetByteCount(value);
}
