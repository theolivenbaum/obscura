using System.Globalization;
using System.Text;

namespace Obscura.Js.Url;

/// <summary>What kind of host a URL record carries.</summary>
public enum HostKind
{
    /// <summary>No host at all (or, following the Rust <c>url</c> crate, an empty one).</summary>
    None,

    /// <summary>A registrable domain, or an opaque host for a non-special scheme.</summary>
    Domain,

    /// <summary>An IPv4 literal.</summary>
    Ipv4,

    /// <summary>An IPv6 literal.</summary>
    Ipv6,
}

/// <summary>
/// A parsed WHATWG host. <see href="https://url.spec.whatwg.org/#host-parsing"/>.
/// </summary>
public readonly struct ParsedHost
{
    private ParsedHost(HostKind kind, string domain, uint v4, ushort[]? v6)
    {
        Kind = kind;
        Domain = domain;
        V4 = v4;
        V6 = v6;
    }

    /// <summary>Which of the host shapes this is.</summary>
    public HostKind Kind { get; }

    /// <summary>The domain or opaque host text; empty for the other kinds.</summary>
    public string Domain { get; }

    /// <summary>The IPv4 address as a big-endian integer.</summary>
    public uint V4 { get; }

    /// <summary>The eight IPv6 pieces.</summary>
    public ushort[]? V6 { get; }

    /// <summary>A domain host whose text is empty, which the Rust crate treats as "no host".</summary>
    public bool IsEmptyDomain => Kind == HostKind.Domain && Domain.Length == 0;

    /// <summary>Builds a domain (or opaque) host.</summary>
    public static ParsedHost FromDomain(string domain) =>
        new(HostKind.Domain, domain, 0, null);

    /// <summary>Builds an IPv4 host.</summary>
    public static ParsedHost FromIpv4(uint address) =>
        new(HostKind.Ipv4, string.Empty, address, null);

    /// <summary>Builds an IPv6 host.</summary>
    public static ParsedHost FromIpv6(ushort[] pieces) =>
        new(HostKind.Ipv6, string.Empty, 0, pieces);

    /// <summary><see href="https://url.spec.whatwg.org/#host-serializing"/>.</summary>
    public override string ToString() => Kind switch
    {
        HostKind.Domain => Domain ?? string.Empty,
        HostKind.Ipv4 => SerializeIpv4(V4),
        HostKind.Ipv6 => "[" + SerializeIpv6(V6!) + "]",
        _ => string.Empty,
    };

    private static string SerializeIpv4(uint address) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}");

    private static string SerializeIpv6(ushort[] pieces)
    {
        // Longest run of zeroes, ignoring lone zeroes; see the IPv6 serializer, steps 2-3.
        var longest = -1;
        var longestLength = -1;
        var start = -1;
        for (var i = 0; i <= 8; i++)
        {
            if (i < 8 && pieces[i] == 0)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                var length = i - start;
                if (length > longestLength)
                {
                    longest = start;
                    longestLength = length;
                }

                start = -1;
            }
        }

        var compressStart = longestLength < 2 ? -1 : longest;
        var compressEnd = longestLength < 2 ? -2 : longest + longestLength;

        var sb = new StringBuilder(45);
        var index = 0;
        while (index < 8)
        {
            if (index == compressStart)
            {
                sb.Append(':');
                if (index == 0)
                {
                    sb.Append(':');
                }

                if (compressEnd < 8)
                {
                    index = compressEnd;
                }
                else
                {
                    break;
                }
            }

            sb.Append(pieces[index].ToString("x", CultureInfo.InvariantCulture));
            if (index < 7)
            {
                sb.Append(':');
            }

            index++;
        }

        return sb.ToString();
    }
}

/// <summary>Host parsing: domains, IPv4/IPv6 literals, and opaque hosts.</summary>
public static class HostParser
{
    /// <summary><see href="https://url.spec.whatwg.org/#concept-host-parser"/> for special schemes.</summary>
    public static bool TryParse(string input, out ParsedHost host)
    {
        host = default;
        if (input.StartsWith('['))
        {
            if (!input.EndsWith(']'))
            {
                return false;
            }

            if (!TryParseIpv6(input.AsSpan(1, input.Length - 2), out var pieces))
            {
                return false;
            }

            host = ParsedHost.FromIpv6(pieces);
            return true;
        }

        var decoded = PercentEncoding.Decode(input);
        if (!Idna.DomainToAscii(decoded, out var domain) || domain.Length == 0)
        {
            return false;
        }

        if (EndsInANumber(domain))
        {
            if (!TryParseIpv4(domain, out var address))
            {
                return false;
            }

            host = ParsedHost.FromIpv4(address);
            return true;
        }

        host = ParsedHost.FromDomain(domain);
        return true;
    }

    /// <summary><see href="https://url.spec.whatwg.org/#concept-opaque-host-parser"/>.</summary>
    public static bool TryParseOpaque(string input, out ParsedHost host)
    {
        host = default;
        if (input.StartsWith('['))
        {
            if (!input.EndsWith(']'))
            {
                return false;
            }

            if (!TryParseIpv6(input.AsSpan(1, input.Length - 2), out var pieces))
            {
                return false;
            }

            host = ParsedHost.FromIpv6(pieces);
            return true;
        }

        foreach (var c in input)
        {
            if (IsForbiddenHostCodePoint(c))
            {
                return false;
            }
        }

        var sb = new StringBuilder(input.Length);
        PercentEncoding.AppendEncoded(sb, input, AsciiSet.Controls);
        host = ParsedHost.FromDomain(sb.ToString());
        return true;
    }

    /// <summary><see href="https://url.spec.whatwg.org/#forbidden-host-code-point"/>.</summary>
    public static bool IsForbiddenHostCodePoint(char c) => c is '\0' or '\t' or '\n' or '\r'
        or ' ' or '#' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|';

    /// <summary><see href="https://url.spec.whatwg.org/#ends-in-a-number-checker"/>.</summary>
    public static bool EndsInANumber(string input)
    {
        var lastDot = input.LastIndexOf('.');
        var last = lastDot < 0 ? input : input[(lastDot + 1)..];
        if (last.Length == 0)
        {
            if (lastDot <= 0)
            {
                return false;
            }

            var prevDot = input.LastIndexOf('.', lastDot - 1);
            last = input[(prevDot + 1)..lastDot];
        }

        if (last.Length > 0)
        {
            var allDigits = true;
            foreach (var c in last)
            {
                if (!char.IsAsciiDigit(c))
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits)
            {
                return true;
            }
        }

        return TryParseIpv4Number(last, out _, out var overflow) || overflow;
    }

    /// <summary>
    /// <see href="https://url.spec.whatwg.org/#ipv4-number-parser"/>. Returns false for a
    /// syntactically invalid number; <paramref name="overflow"/> reports a valid number that
    /// does not fit in 32 bits (which is a validity failure for the address, not the number).
    /// </summary>
    public static bool TryParseIpv4Number(string input, out uint value, out bool overflow)
    {
        value = 0;
        overflow = false;
        if (input.Length == 0)
        {
            return false;
        }

        var radix = 10;
        var span = input.AsSpan();
        if (span.StartsWith("0x", StringComparison.Ordinal) || span.StartsWith("0X", StringComparison.Ordinal))
        {
            span = span[2..];
            radix = 16;
        }
        else if (span.Length >= 2 && span[0] == '0')
        {
            span = span[1..];
            radix = 8;
        }

        if (span.Length == 0)
        {
            return true;
        }

        ulong accumulator = 0;
        foreach (var c in span)
        {
            int digit;
            switch (radix)
            {
                case 8 when c is >= '0' and <= '7':
                    digit = c - '0';
                    break;
                case 10 when char.IsAsciiDigit(c):
                    digit = c - '0';
                    break;
                case 16 when char.IsAsciiHexDigit(c):
                    digit = char.IsAsciiDigit(c) ? c - '0' : (char.ToLowerInvariant(c) - 'a' + 10);
                    break;
                default:
                    return false;
            }

            if (!overflow)
            {
                accumulator = (accumulator * (ulong)radix) + (ulong)digit;
                if (accumulator > uint.MaxValue)
                {
                    // Keep scanning so a malformed digit later in the input still reports
                    // "invalid" rather than "overflow", matching the Rust ordering.
                    overflow = true;
                }
            }
        }

        if (overflow)
        {
            return true;
        }

        value = (uint)accumulator;
        return true;
    }

    /// <summary><see href="https://url.spec.whatwg.org/#concept-ipv4-parser"/>.</summary>
    public static bool TryParseIpv4(string input, out uint address)
    {
        address = 0;
        var parts = new List<string>(input.Split('.'));
        if (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        if (parts.Count > 4 || parts.Count == 0)
        {
            return false;
        }

        var numbers = new uint[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            if (!TryParseIpv4Number(parts[i], out var n, out var overflow) || overflow)
            {
                return false;
            }

            numbers[i] = n;
        }

        var last = numbers[^1];
        var rest = numbers.Length - 1;
        if (rest < 4 && last > (uint.MaxValue >> (8 * rest)))
        {
            return false;
        }

        for (var i = 0; i < rest; i++)
        {
            if (numbers[i] > 255)
            {
                return false;
            }
        }

        var result = last;
        for (var i = 0; i < rest; i++)
        {
            result += numbers[i] << (8 * (3 - i));
        }

        address = result;
        return true;
    }

    /// <summary><see href="https://url.spec.whatwg.org/#concept-ipv6-parser"/>.</summary>
    public static bool TryParseIpv6(ReadOnlySpan<char> input, out ushort[] pieces)
    {
        pieces = new ushort[8];
        var len = input.Length;
        var isIpv4 = false;
        var piecePointer = 0;
        int? compressPointer = null;
        var i = 0;

        if (len < 2)
        {
            return false;
        }

        if (input[0] == ':')
        {
            if (input[1] != ':')
            {
                return false;
            }

            i = 2;
            piecePointer = 1;
            compressPointer = 1;
        }

        while (i < len)
        {
            if (piecePointer == 8)
            {
                return false;
            }

            if (input[i] == ':')
            {
                if (compressPointer is not null)
                {
                    return false;
                }

                i++;
                piecePointer++;
                compressPointer = piecePointer;
                continue;
            }

            var start = i;
            var end = Math.Min(len, start + 4);
            ushort value = 0;
            while (i < end)
            {
                var digit = HexDigit(input[i]);
                if (digit < 0)
                {
                    break;
                }

                value = (ushort)((value * 0x10) + digit);
                i++;
            }

            if (i < len)
            {
                switch (input[i])
                {
                    case '.':
                        if (i == start)
                        {
                            return false;
                        }

                        i = start;
                        if (piecePointer > 6)
                        {
                            return false;
                        }

                        isIpv4 = true;
                        break;
                    case ':':
                        i++;
                        if (i == len)
                        {
                            return false;
                        }

                        break;
                    default:
                        return false;
                }
            }

            if (isIpv4)
            {
                break;
            }

            pieces[piecePointer] = value;
            piecePointer++;
        }

        if (isIpv4)
        {
            if (piecePointer > 6)
            {
                return false;
            }

            var numbersSeen = 0;
            while (i < len)
            {
                if (numbersSeen > 0)
                {
                    if (numbersSeen < 4 && i < len && input[i] == '.')
                    {
                        i++;
                    }
                    else
                    {
                        return false;
                    }
                }

                int? ipv4Piece = null;
                while (i < len)
                {
                    if (!char.IsAsciiDigit(input[i]))
                    {
                        break;
                    }

                    var digit = input[i] - '0';
                    if (ipv4Piece is null)
                    {
                        ipv4Piece = digit;
                    }
                    else if (ipv4Piece == 0)
                    {
                        return false;   // no leading zero
                    }
                    else
                    {
                        ipv4Piece = (ipv4Piece * 10) + digit;
                        if (ipv4Piece > 255)
                        {
                            return false;
                        }
                    }

                    i++;
                }

                if (ipv4Piece is null)
                {
                    return false;
                }

                pieces[piecePointer] = (ushort)((pieces[piecePointer] * 0x100) + ipv4Piece.Value);
                numbersSeen++;
                if (numbersSeen is 2 or 4)
                {
                    piecePointer++;
                }
            }

            if (numbersSeen != 4)
            {
                return false;
            }
        }

        if (i < len)
        {
            return false;
        }

        if (compressPointer is { } compress)
        {
            var swaps = piecePointer - compress;
            piecePointer = 7;
            while (swaps > 0)
            {
                (pieces[piecePointer], pieces[compress + swaps - 1]) =
                    (pieces[compress + swaps - 1], pieces[piecePointer]);
                swaps--;
                piecePointer--;
            }
        }
        else if (piecePointer != 8)
        {
            return false;
        }

        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
