using System.Globalization;
using System.Text;

namespace Obscura.Js.Url;

/// <summary>
/// The WHATWG "percent-encode after encoding" step for a URL query, used when a document's
/// encoding override is not UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// The Rust side hands this to <c>encoding_rs</c>, which ships every WHATWG legacy encoding.
/// The managed tree has no encoding_rs equivalent and cannot take a native dependency, so
/// this covers UTF-8 and the windows-1252 label family (which is also where <c>us-ascii</c>,
/// <c>iso-8859-1</c> and <c>latin1</c> land per the Encoding Standard). Any other label is
/// reported as unresolvable, and the op then returns its input unchanged - the same value
/// Rust produces for a label <c>encoding_rs</c> does not know.
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
    private static void EncodeRun(StringBuilder output, ReadOnlySpan<char> run, ILegacyEncoder encoder)
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

            if (encoder.TryEncode(codePoint, out var bytes))
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

    /// <summary>The encodings this port can actually produce bytes for.</summary>
    private interface ILegacyEncoder
    {
        bool TryEncode(int codePoint, out byte[] bytes);
    }

    private static ILegacyEncoder? ResolveEncoder(string label)
    {
        var name = NormalizeLabel(label);
        return name switch
        {
            "unicode-1-1-utf-8" or "unicode11utf8" or "unicode20utf8" or "utf-8" or "utf8"
                or "x-unicode20utf8" => Utf8Encoder.Instance,
            "ansi_x3.4-1968" or "ascii" or "cp1252" or "cp819" or "csisolatin1" or "ibm819"
                or "iso-8859-1" or "iso-ir-100" or "iso8859-1" or "iso88591" or "iso_8859-1"
                or "iso_8859-1:1987" or "l1" or "latin1" or "us-ascii" or "windows-1252"
                or "x-cp1252" => Windows1252Encoder.Instance,
            _ => null,
        };
    }

    private static string NormalizeLabel(string label)
    {
        var start = 0;
        var end = label.Length;
        while (start < end && label[start] is '\t' or '\n' or '\f' or '\r' or ' ')
        {
            start++;
        }

        while (end > start && label[end - 1] is '\t' or '\n' or '\f' or '\r' or ' ')
        {
            end--;
        }

        return label[start..end].ToLowerInvariant();
    }

    private sealed class Utf8Encoder : ILegacyEncoder
    {
        public static readonly Utf8Encoder Instance = new();

        public bool TryEncode(int codePoint, out byte[] bytes)
        {
            bytes = Encoding.UTF8.GetBytes(char.ConvertFromUtf32(codePoint));
            return true;
        }
    }

    private sealed class Windows1252Encoder : ILegacyEncoder
    {
        public static readonly Windows1252Encoder Instance = new();

        /// <summary>The 0x80-0x9F block of the WHATWG windows-1252 index.</summary>
        private static readonly Dictionary<int, byte> HighRange = new()
        {
            [0x20AC] = 0x80, [0x0081] = 0x81, [0x201A] = 0x82, [0x0192] = 0x83,
            [0x201E] = 0x84, [0x2026] = 0x85, [0x2020] = 0x86, [0x2021] = 0x87,
            [0x02C6] = 0x88, [0x2030] = 0x89, [0x0160] = 0x8A, [0x2039] = 0x8B,
            [0x0152] = 0x8C, [0x008D] = 0x8D, [0x017D] = 0x8E, [0x008F] = 0x8F,
            [0x0090] = 0x90, [0x2018] = 0x91, [0x2019] = 0x92, [0x201C] = 0x93,
            [0x201D] = 0x94, [0x2022] = 0x95, [0x2013] = 0x96, [0x2014] = 0x97,
            [0x02DC] = 0x98, [0x2122] = 0x99, [0x0161] = 0x9A, [0x203A] = 0x9B,
            [0x0153] = 0x9C, [0x009D] = 0x9D, [0x017E] = 0x9E, [0x0178] = 0x9F,
        };

        public bool TryEncode(int codePoint, out byte[] bytes)
        {
            if (codePoint <= 0x7F || (codePoint >= 0xA0 && codePoint <= 0xFF))
            {
                bytes = [(byte)codePoint];
                return true;
            }

            if (HighRange.TryGetValue(codePoint, out var b))
            {
                bytes = [b];
                return true;
            }

            bytes = [];
            return false;
        }
    }
}
