using System.Text;

namespace Obscura.Js.Url;

/// <summary>
/// The WHATWG percent-encode sets and the encode/decode primitives built on them.
/// </summary>
/// <remarks>
/// A set only ever describes ASCII: every byte above 0x7F is always percent-encoded,
/// which is what the <c>percent-encoding</c> crate does behind <c>utf8_percent_encode</c>.
/// </remarks>
public readonly struct AsciiSet
{
    private readonly ulong _low;
    private readonly ulong _high;

    private AsciiSet(ulong low, ulong high)
    {
        _low = low;
        _high = high;
    }

    /// <summary>True when <paramref name="b"/> must be percent-encoded.</summary>
    public bool Contains(byte b) =>
        b >= 0x80 || (b < 64 ? (_low & (1UL << b)) != 0 : (_high & (1UL << (b - 64))) != 0);

    /// <summary>Returns a copy of this set with the given ASCII bytes added.</summary>
    public AsciiSet Add(params char[] chars)
    {
        var low = _low;
        var high = _high;
        foreach (var c in chars)
        {
            var b = (byte)c;
            if (b < 64)
            {
                low |= 1UL << b;
            }
            else
            {
                high |= 1UL << (b - 64);
            }
        }

        return new AsciiSet(low, high);
    }

    /// <summary>C0 controls plus DEL: <c>percent_encoding::CONTROLS</c>.</summary>
    public static AsciiSet Controls { get; } = MakeControls();

    private static AsciiSet MakeControls()
    {
        ulong low = 0;
        for (var b = 0; b <= 0x1F; b++)
        {
            low |= 1UL << b;
        }

        return new AsciiSet(low, 1UL << (0x7F - 64));
    }
}

/// <summary>Percent-encoding helpers shared by the parser and the setters.</summary>
public static class PercentEncoding
{
    private const string HexUpper = "0123456789ABCDEF";

    /// <summary><see href="https://url.spec.whatwg.org/#fragment-percent-encode-set"/></summary>
    public static readonly AsciiSet Fragment = AsciiSet.Controls.Add(' ', '"', '<', '>', '`');

    /// <summary><see href="https://url.spec.whatwg.org/#path-percent-encode-set"/></summary>
    public static readonly AsciiSet Path = Fragment.Add('#', '?', '{', '}');

    /// <summary><see href="https://url.spec.whatwg.org/#userinfo-percent-encode-set"/></summary>
    public static readonly AsciiSet Userinfo =
        Path.Add('/', ':', ';', '=', '@', '[', '\\', ']', '^', '|');

    /// <summary>The path set plus the segment separators, for the path-segment setter.</summary>
    public static readonly AsciiSet PathSegment = Path.Add('/', '%');

    /// <summary>Backslash separates segments in special URLs, so it is escaped there too.</summary>
    public static readonly AsciiSet SpecialPathSegment = PathSegment.Add('\\');

    /// <summary><see href="https://url.spec.whatwg.org/#query-state"/></summary>
    public static readonly AsciiSet Query = AsciiSet.Controls.Add(' ', '"', '#', '<', '>');

    /// <summary>The query set for special URLs, which additionally escapes the apostrophe.</summary>
    public static readonly AsciiSet SpecialQuery = Query.Add('\'');

    /// <summary>Appends <paramref name="value"/>, percent-encoding per <paramref name="set"/>.</summary>
    public static void AppendEncoded(StringBuilder output, ReadOnlySpan<char> value, AsciiSet set)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                AppendEncodedPair(output, c, value[i + 1]);
                i++;
                continue;
            }

            AppendEncoded(output, c, set);
        }
    }

    /// <summary>Appends one UTF-16 code unit, percent-encoding per <paramref name="set"/>.</summary>
    public static void AppendEncoded(StringBuilder output, char c, AsciiSet set)
    {
        if (c < 0x80)
        {
            var b = (byte)c;
            if (set.Contains(b))
            {
                AppendByte(output, b);
            }
            else
            {
                output.Append(c);
            }

            return;
        }

        // Non-ASCII is always escaped, one UTF-8 byte at a time. A lone surrogate cannot be
        // encoded, so it takes the replacement character, matching Rust's lossy behavior for
        // text that reached the parser as a JS string.
        Span<byte> bytes = stackalloc byte[4];
        Span<char> chars = stackalloc char[1];
        chars[0] = char.IsSurrogate(c) ? '�' : c;
        var written = Encoding.UTF8.GetBytes(chars, bytes);
        for (var i = 0; i < written; i++)
        {
            AppendByte(output, bytes[i]);
        }
    }

    /// <summary>Appends a surrogate pair as its UTF-8 bytes, all percent-encoded.</summary>
    public static void AppendEncodedPair(StringBuilder output, char high, char low)
    {
        Span<byte> bytes = stackalloc byte[4];
        Span<char> chars = stackalloc char[2];
        chars[0] = high;
        chars[1] = low;
        var written = Encoding.UTF8.GetBytes(chars, bytes);
        for (var i = 0; i < written; i++)
        {
            AppendByte(output, bytes[i]);
        }
    }

    /// <summary>Percent-encodes an already-encoded byte sequence per <paramref name="set"/>.</summary>
    public static void AppendEncodedBytes(StringBuilder output, ReadOnlySpan<byte> bytes, AsciiSet set)
    {
        foreach (var b in bytes)
        {
            if (set.Contains(b))
            {
                AppendByte(output, b);
            }
            else
            {
                output.Append((char)b);
            }
        }
    }

    /// <summary>Writes <c>%XX</c> with uppercase hex.</summary>
    public static void AppendByte(StringBuilder output, byte b)
    {
        output.Append('%');
        output.Append(HexUpper[b >> 4]);
        output.Append(HexUpper[b & 0xF]);
    }

    /// <summary>
    /// Percent-decodes to bytes. A <c>%</c> not followed by two hex digits is literal,
    /// which is what <c>percent_decode</c> does.
    /// </summary>
    public static byte[] Decode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hasPercent = false;
        foreach (var b in bytes)
        {
            if (b == (byte)'%')
            {
                hasPercent = true;
                break;
            }
        }

        if (!hasPercent)
        {
            return bytes;
        }

        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'%' && i + 2 < bytes.Length
                && TryHex(bytes[i + 1], out var hi) && TryHex(bytes[i + 2], out var lo))
            {
                output.Add((byte)((hi << 4) | lo));
                i += 2;
            }
            else
            {
                output.Add(bytes[i]);
            }
        }

        return output.ToArray();
    }

    private static bool TryHex(byte b, out int value)
    {
        switch (b)
        {
            case >= (byte)'0' and <= (byte)'9':
                value = b - '0';
                return true;
            case >= (byte)'a' and <= (byte)'f':
                value = b - 'a' + 10;
                return true;
            case >= (byte)'A' and <= (byte)'F':
                value = b - 'A' + 10;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
