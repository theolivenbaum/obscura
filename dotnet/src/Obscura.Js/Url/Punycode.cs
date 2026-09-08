using System.Text;

namespace Obscura.Js.Url;

/// <summary>
/// Punycode (RFC 3492) bootstring encoding, as used by IDNA to map a Unicode
/// domain label to its ASCII <c>xn--</c> form.
/// </summary>
/// <remarks>
/// <para>
/// This is implemented in-tree rather than delegated to <c>System.Globalization.IdnMapping</c>
/// on purpose: <c>IdnMapping</c> applies an IDNA2003-era profile (with
/// <c>AllowUnassigned</c>/<c>UseStd3AsciiRules</c> knobs and its own validity checks),
/// while the WHATWG URL Standard requires UTS 46 nontransitional processing on top of a
/// raw bootstring codec. Keeping the codec separate lets the IDNA layer decide policy.
/// </para>
/// <para>
/// Every entry point is total: malformed input returns <see langword="false"/> rather than
/// throwing, because the URL ops must never let an exception escape into V8.
/// </para>
/// </remarks>
public static class Punycode
{
    private const int Base = 36;
    private const int TMin = 1;
    private const int TMax = 26;
    private const int Skew = 38;
    private const int Damp = 700;
    private const int InitialBias = 72;
    private const int InitialN = 128;
    private const char Delimiter = '-';

    /// <summary>Encodes one label. Returns false on overflow or invalid input.</summary>
    public static bool Encode(ReadOnlySpan<char> input, out string output)
    {
        output = string.Empty;

        // Work in code points; surrogate pairs must be treated as one unit.
        var codePoints = ToCodePoints(input);
        if (codePoints is null)
        {
            return false;
        }

        var sb = new StringBuilder(input.Length + 8);
        var basicCount = 0;
        foreach (var cp in codePoints)
        {
            if (cp < 0x80)
            {
                sb.Append((char)cp);
                basicCount++;
            }
        }

        var handled = basicCount;
        if (basicCount > 0)
        {
            sb.Append(Delimiter);
        }

        var n = InitialN;
        var delta = 0;
        var bias = InitialBias;

        while (handled < codePoints.Count)
        {
            // Smallest code point >= n in the input.
            var m = int.MaxValue;
            foreach (var cp in codePoints)
            {
                if (cp >= n && cp < m)
                {
                    m = cp;
                }
            }

            if (m == int.MaxValue)
            {
                return false;
            }

            // delta += (m - n) * (handled + 1), with overflow rejection.
            var advance = (long)(m - n) * (handled + 1);
            if (advance > int.MaxValue - delta)
            {
                return false;
            }

            delta += (int)advance;
            n = m;

            foreach (var cp in codePoints)
            {
                if (cp < n)
                {
                    if (delta == int.MaxValue)
                    {
                        return false;
                    }

                    delta++;
                }
                else if (cp == n)
                {
                    var q = delta;
                    for (var k = Base; ; k += Base)
                    {
                        var t = k <= bias ? TMin : (k >= bias + TMax ? TMax : k - bias);
                        if (q < t)
                        {
                            break;
                        }

                        sb.Append(DigitToChar(t + ((q - t) % (Base - t))));
                        q = (q - t) / (Base - t);
                    }

                    sb.Append(DigitToChar(q));
                    bias = Adapt(delta, handled + 1, handled == basicCount);
                    delta = 0;
                    handled++;
                }
            }

            delta++;
            n++;
        }

        output = sb.ToString();
        return true;
    }

    /// <summary>Decodes one label body (the part after <c>xn--</c>). False on malformed input.</summary>
    public static bool Decode(ReadOnlySpan<char> input, out string output)
    {
        output = string.Empty;

        var decoded = new List<int>(input.Length);
        var lastDelimiter = input.LastIndexOf(Delimiter);
        var start = 0;
        if (lastDelimiter >= 0)
        {
            for (var i = 0; i < lastDelimiter; i++)
            {
                if (input[i] >= 0x80)
                {
                    return false;
                }

                decoded.Add(input[i]);
            }

            start = lastDelimiter + 1;
        }

        var n = InitialN;
        var bias = InitialBias;
        var i2 = 0;

        var pos = start;
        while (pos < input.Length)
        {
            var oldi = i2;
            var w = 1;
            for (var k = Base; ; k += Base)
            {
                if (pos >= input.Length)
                {
                    return false;
                }

                var digit = CharToDigit(input[pos++]);
                if (digit < 0)
                {
                    return false;
                }

                if ((long)digit * w > int.MaxValue - i2)
                {
                    return false;
                }

                i2 += digit * w;
                var t = k <= bias ? TMin : (k >= bias + TMax ? TMax : k - bias);
                if (digit < t)
                {
                    break;
                }

                if ((long)w * (Base - t) > int.MaxValue)
                {
                    return false;
                }

                w *= Base - t;
            }

            var outLen = decoded.Count + 1;
            bias = Adapt(i2 - oldi, outLen, oldi == 0);
            if ((long)n + (i2 / outLen) > 0x10FFFF)
            {
                return false;
            }

            n += i2 / outLen;
            i2 %= outLen;
            if (n is >= 0xD800 and <= 0xDFFF)
            {
                return false;
            }

            decoded.Insert(i2, n);
            i2++;
        }

        var sb = new StringBuilder(decoded.Count + 4);
        foreach (var cp in decoded)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }

        output = sb.ToString();
        return true;
    }

    private static int Adapt(int delta, int numPoints, bool firstTime)
    {
        delta = firstTime ? delta / Damp : delta / 2;
        delta += delta / numPoints;
        var k = 0;
        while (delta > ((Base - TMin) * TMax) / 2)
        {
            delta /= Base - TMin;
            k += Base;
        }

        return k + (((Base - TMin + 1) * delta) / (delta + Skew));
    }

    private static char DigitToChar(int d) =>
        d < 26 ? (char)('a' + d) : (char)('0' + (d - 26));

    private static int CharToDigit(char c) => c switch
    {
        >= 'a' and <= 'z' => c - 'a',
        >= 'A' and <= 'Z' => c - 'A',
        >= '0' and <= '9' => c - '0' + 26,
        _ => -1,
    };

    private static List<int>? ToCodePoints(ReadOnlySpan<char> input)
    {
        var list = new List<int>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= input.Length || !char.IsLowSurrogate(input[i + 1]))
                {
                    return null;
                }

                list.Add(char.ConvertToUtf32(c, input[i + 1]));
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return null;
            }
            else
            {
                list.Add(c);
            }
        }

        return list;
    }
}
