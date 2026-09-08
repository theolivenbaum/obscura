using System.Text;

namespace Obscura.Js.Url;

/// <summary>
/// The IDNA layer used by WHATWG host parsing: "domain to ASCII" plus the
/// forbidden-domain-code-point deny list applied to its output.
/// </summary>
/// <remarks>
/// <para>
/// The Rust engine calls <c>idna::domain_to_ascii_from_cow(.., AsciiDenyList::URL)</c>,
/// which is UTS 46 nontransitional processing with <c>CheckHyphens=false</c>,
/// <c>VerifyDnsLength=false</c> and the WHATWG forbidden-domain-code-point set as the
/// STD3 deny list. This implementation covers the parts of that profile that do not
/// require shipping the full UTS 46 <c>IdnaMappingTable</c>: case mapping, the IDEOGRAPHIC
/// / FULLWIDTH / HALFWIDTH full stop mappings, soft-hyphen removal, NFC (when the runtime
/// exposes it), Punycode via <see cref="Punycode"/>, and the deny list. See the port notes
/// for the parts that are deliberately absent.
/// </para>
/// <para>
/// Never throws. A failure is reported as <see langword="false"/>.
/// </para>
/// </remarks>
public static class Idna
{
    /// <summary>
    /// <see href="https://url.spec.whatwg.org/#forbidden-domain-code-point"/>, which is the
    /// idna crate's <c>AsciiDenyList::URL</c>: C0 controls, space, DEL, and
    /// <c>% # / : &lt; &gt; ? @ [ \ ] ^ |</c>. ASCII uppercase is denied too, but case
    /// mapping has already removed it by the time the list is applied.
    /// </summary>
    public static bool IsForbiddenDomainCodePoint(char c) =>
        c <= ' ' || c == '\u007F' || c is '%' or '#' or '/' or ':' or '<' or '>' or '?'
            or '@' or '[' or '\\' or ']' or '^' or '|';

    /// <summary>Maps a domain to its ASCII form. Returns false when the domain is invalid.</summary>
    public static bool DomainToAscii(string domain, out string result)
    {
        result = string.Empty;
        try
        {
            return DomainToAsciiCore(domain, out result);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            result = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Maps percent-decoded host bytes to their ASCII domain form. Invalid UTF-8 is an error,
    /// matching the Rust path where the byte slice must decode before UTS 46 runs.
    /// </summary>
    public static bool DomainToAscii(ReadOnlySpan<byte> utf8, out string result)
    {
        result = string.Empty;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(utf8);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return false;
        }

        // U+FFFD is disallowed in IDNA, so a replacement character introduced by malformed
        // UTF-8 (or present literally) fails the same way it does in Rust.
        return decoded.IndexOf('\uFFFD') < 0 && DomainToAscii(decoded, out result);
    }

    private static bool DomainToAsciiCore(string domain, out string result)
    {
        result = string.Empty;

        // 1. UTS 46 mapping, restricted to the classes we can decide without the full table.
        var mapped = new StringBuilder(domain.Length);
        for (var i = 0; i < domain.Length; i++)
        {
            var c = domain[i];
            switch (c)
            {
                case '\u00AD':      // SOFT HYPHEN
                case '\u200B':      // ZERO WIDTH SPACE
                case >= '\u2060' and <= '\u2064':   // WORD JOINER, invisible operators
                case >= '\u206A' and <= '\u206F':   // deprecated format controls
                case '\uFEFF':      // ZERO WIDTH NO-BREAK SPACE
                    continue;        // "ignored" in the UTS 46 mapping table

                case '\u3002':      // IDEOGRAPHIC FULL STOP
                case '\uFF0E':      // FULLWIDTH FULL STOP
                case '\uFF61':      // HALFWIDTH IDEOGRAPHIC FULL STOP
                    mapped.Append('.');
                    continue;

                // disallowed_STD3_mapped to U+0020: the deny list then rejects the space.
                case '\u00A0':
                case '\u1680':
                case '\u2028':
                case '\u2029':
                case '\u202F':
                case '\u205F':
                case '\u3000':
                case >= '\u2000' and <= '\u200A':
                    mapped.Append(' ');
                    continue;

                case '\u017F':      // LATIN SMALL LETTER LONG S
                    mapped.Append('s');
                    continue;
                case '\u212A':      // KELVIN SIGN
                    mapped.Append('k');
                    continue;
                case '\u212B':      // ANGSTROM SIGN
                    mapped.Append('\u00E5');
                    continue;
                case '\u1E9E':      // LATIN CAPITAL LETTER SHARP S (nontransitional)
                    mapped.Append('\u00DF');
                    continue;
                case '\u0130':      // LATIN CAPITAL LETTER I WITH DOT ABOVE
                    mapped.Append("i\u0307");
                    continue;

                // Disallowed outright: C1 controls, the noncharacter blocks, the zero-width
                // space, the joiners (CheckJoiners rejects them outside a valid context, which
                // is every context this port can recognize), and the invisible operators.
                case >= '\u0080' and <= '\u009F':
                case '\u200C':
                case '\u200D':
                case >= '\uFDD0' and <= '\uFDEF':
                case >= '\uFFF9' and <= '\uFFFD':
                case '\uFFFE':
                case '\uFFFF':
                    return false;

                // Fullwidth forms map onto their ASCII counterparts.
                case >= '\uFF01' and <= '\uFF5E':
                    mapped.Append(AsciiLower((char)(c - 0xFEE0)));
                    continue;

                default:
                    if (char.IsHighSurrogate(c) && i + 1 < domain.Length && char.IsLowSurrogate(domain[i + 1]))
                    {
                        // Reject the two noncharacters at the end of every astral plane.
                        if ((char.ConvertToUtf32(c, domain[i + 1]) & 0xFFFE) == 0xFFFE)
                        {
                            return false;
                        }

                        mapped.Append(c).Append(domain[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        return false;   // a lone surrogate is never a valid domain
                    }
                    else
                    {
                        mapped.Append(c <= 0x7F ? AsciiLower(c) : char.ToLowerInvariant(c));
                    }

                    continue;
            }
        }

        var normalized = Normalize(mapped.ToString());

        // 2. Per-label Punycode. Splitting on '.' only, per UTS 46 step 3 (the alternate
        //    full stops were already mapped above).
        var sb = new StringBuilder(normalized.Length + 8);
        var start = 0;
        while (true)
        {
            var dot = normalized.IndexOf('.', start);
            var end = dot < 0 ? normalized.Length : dot;
            if (!AppendLabel(sb, normalized.AsSpan(start, end - start)))
            {
                return false;
            }

            if (dot < 0)
            {
                break;
            }

            sb.Append('.');
            start = dot + 1;
        }

        // 3. The WHATWG check that runs right after UTS 46.
        for (var i = 0; i < sb.Length; i++)
        {
            if (IsForbiddenDomainCodePoint(sb[i]))
            {
                return false;
            }
        }

        result = sb.ToString();
        return true;
    }

    private static bool AppendLabel(StringBuilder sb, ReadOnlySpan<char> label)
    {
        var ascii = true;
        foreach (var c in label)
        {
            if (c > 0x7F)
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            // An already-ASCII label may still be a Punycode label. UTS 46 decodes it, checks
            // that the result is valid, and requires the re-encoding to be identical; that
            // round trip is what rejects inputs such as "xn--a" (which decodes to U+0080).
            if (label.StartsWith("xn--", StringComparison.Ordinal))
            {
                if (!Punycode.Decode(label[4..], out var decoded)
                    || decoded.Length == 0
                    || !IsValidDecodedLabel(decoded)
                    || !Punycode.Encode(Normalize(decoded), out var reencoded)
                    || !label[4..].SequenceEqual(reencoded))
                {
                    return false;
                }
            }

            sb.Append(label);
            return true;
        }

        if (!Punycode.Encode(label, out var encoded))
        {
            return false;
        }

        sb.Append("xn--").Append(encoded);
        return true;
    }

    /// <summary>
    /// The subset of UTS 46 label validity we can decide without the mapping table: a decoded
    /// Punycode label must be non-ASCII, and must not carry controls, noncharacters, lone
    /// surrogates, or the forbidden-domain ASCII set.
    /// </summary>
    private static bool IsValidDecodedLabel(string decoded)
    {
        var sawNonAscii = false;
        for (var i = 0; i < decoded.Length; i++)
        {
            var c = decoded[i];
            if (c < 0x80)
            {
                if (IsForbiddenDomainCodePoint(c))
                {
                    return false;
                }

                continue;
            }

            sawNonAscii = true;
            if (c is >= '\u007F' and <= '\u009F' or '\uFFFD')
            {
                return false;
            }

            if (c is >= '\uFDD0' and <= '\uFDEF')
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= decoded.Length || !char.IsLowSurrogate(decoded[i + 1]))
                {
                    return false;
                }

                var cp = char.ConvertToUtf32(c, decoded[i + 1]);
                if ((cp & 0xFFFE) == 0xFFFE)
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c) || (c & 0xFFFE) == 0xFFFE)
            {
                return false;
            }
        }

        return sawNonAscii;
    }

    /// <summary>
    /// NFC. This cannot go through <c>string.Normalize</c>: the build sets
    /// <c>InvariantGlobalization</c>, where it silently does nothing.
    /// </summary>
    private static string Normalize(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                return UnicodeNormalization.ToNfc(value);
            }
        }

        return value;
    }

    private static char AsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}
