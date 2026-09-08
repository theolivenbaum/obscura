using System.Globalization;
using System.Text;

namespace Obscura.Js.Url;

/// <summary>
/// The WHATWG "percent-encode after encoding" step for a URL query, used when a document's
/// encoding override is not UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// The Rust side hands this to <c>encoding_rs</c>. The managed counterpart is
/// <see cref="Obscura.Net.WhatwgEncoding"/>, which resolves the whole WHATWG label
/// table and encodes single scalars, so the legacy multi-byte charsets (GBK, Big5,
/// Shift_JIS, EUC-JP, EUC-KR and the rest) are covered here too. A label neither
/// side can resolve is reported as unresolvable, and the op then returns its input
/// unchanged, which is what Rust produces for a label <c>encoding_rs</c> rejects.
/// </para>
/// </remarks>
public static class QueryEncoding
{
    /// <summary>
    /// Re-encodes <paramref name="query"/> using <paramref name="label"/>. Returns false when
    /// the label cannot be resolved, in which case the caller leaves the query alone.
    /// </summary>
    public static bool TryEncodeQuery(string query, string label, bool special, out string result)
    {
        result = string.Empty;
        var encoder = ResolveEncoder(label);
        if (encoder is null)
        {
            return false;
        }

        var sb = new StringBuilder(query.Length * 3);
        var runStart = -1;
        for (var i = 0; i < query.Length; i++)
        {
            var c = query[i];
            if (c < 0x80)
            {
                if (runStart >= 0)
                {
                    EncodeRun(sb, query.AsSpan(runStart, i - runStart), encoder);
                    runStart = -1;
                }

                AppendQueryAscii(sb, (byte)c, special);
            }
            else if (runStart < 0)
            {
                runStart = i;
            }
        }

        if (runStart >= 0)
        {
            EncodeRun(sb, query.AsSpan(runStart), encoder);
        }

        result = sb.ToString();
        return true;
    }

    /// <summary>
    /// The (special-)query percent-encode set applied to a single ASCII byte, so that real
    /// query delimiters such as <c>=</c> and <c>&amp;</c> stay literal.
    /// </summary>
    public static void AppendQueryAscii(StringBuilder output, byte b, bool special)
    {
        var mustEncode = b <= 0x20 || b == 0x7F
            || b is 0x22 or 0x23 or 0x3C or 0x3E
            || (special && b == 0x27);
        if (mustEncode)
        {
            PercentEncoding.AppendByte(output, b);
        }
        else
        {
            output.Append((char)b);
        }
    }

    /// <summary>
    /// Encodes a run of non-ASCII code points and percent-encodes every resulting byte: the
    /// bytes serialize a non-ASCII character, so even an ASCII-range trail byte is escaped.
    /// An unmappable code point becomes a percent-encoded <c>&amp;#NNN;</c> reference.
    /// </summary>
    private static void EncodeRun(StringBuilder output, ReadOnlySpan<char> run, Obscura.Net.WhatwgEncoding encoder)
    {
        for (var i = 0; i < run.Length; i++)
        {
            int codePoint;
            if (char.IsHighSurrogate(run[i]) && i + 1 < run.Length && char.IsLowSurrogate(run[i + 1]))
            {
                codePoint = char.ConvertToUtf32(run[i], run[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(run[i]))
            {
                codePoint = 0xFFFD;
            }
            else
            {
                codePoint = run[i];
            }

            if (System.Text.Rune.TryCreate(codePoint, out var rune) && encoder.TryEncodeRune(rune, out var bytes))
            {
                foreach (var b in bytes)
                {
                    PercentEncoding.AppendByte(output, b);
                }
            }
            else
            {
                output.Append("%26%23");
                output.Append(codePoint.ToString(CultureInfo.InvariantCulture));
                output.Append("%3B");
            }
        }
    }

    /// <summary>
    /// Resolves a charset label through the shared WHATWG label table. Returns null
    /// when the label names no encoding, which leaves the query untouched.
    /// </summary>
    private static Obscura.Net.WhatwgEncoding? ResolveEncoder(string label) =>
        Obscura.Net.WhatwgEncoding.ForLabel(label);
}
