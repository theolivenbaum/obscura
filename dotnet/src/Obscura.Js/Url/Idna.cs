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
        foreach (var c in domain)
        {
            switch (c)
            {
                case '\u00AD':      // SOFT HYPHEN: ignored
                case '\u200B':      // ZERO WIDTH SPACE: ignored
                case '\uFEFF':      // ZERO WIDTH NO-BREAK SPACE: ignored
                    continue;
                case '\u3002':      // IDEOGRAPHIC FULL STOP
                case '\uFF0E':      // FULLWIDTH FULL STOP
                case '\uFF61':      // HALFWIDTH IDEOGRAPHIC FULL STOP
                    mapped.Append('.');
                    continue;
                case '\uFFFD':
                    return false;
                default:
                    if (char.IsSurrogate(c))
                    {
                        mapped.Append(c);   // validated as a pair by the Punycode codec
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
            // An already-ASCII label may still be a Punycode label; it has to decode for the
            // domain to be valid, which is what rejects inputs such as "xn--a".
            if (label.StartsWith("xn--", StringComparison.Ordinal))
            {
                if (!Punycode.Decode(label[4..], out var decoded)
                    || decoded.Length == 0
                    || decoded.IndexOf('\uFFFD') >= 0)
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
    /// NFC, when the runtime can do it. Invariant globalization builds may refuse to
    /// normalize non-ASCII text; an unnormalized label is better than a failed parse.
    /// </summary>
    private static string Normalize(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7F)
            {
                try
                {
                    return value.Normalize(NormalizationForm.FormC);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
                {
                    return value;
                }
            }
        }

        return value;
    }

    private static char AsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}
