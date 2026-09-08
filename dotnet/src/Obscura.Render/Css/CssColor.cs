using System.Globalization;

namespace Obscura.Render.Css;

/// <summary>An 8-bit sRGB color with straight alpha, mirroring Rust's <c>[u8; 4]</c>.</summary>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A)
{
    public static readonly RgbaColor Transparent = new(0, 0, 0, 0);

    public byte[] ToArray() => [R, G, B, A];

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}{A:x2}");
}

/// <summary>
/// CSS color parsing.
/// </summary>
/// <remarks>
/// SHARED CODE: this is a port of <c>obscura-render/src/style.rs</c>'s
/// <c>parse_color_for_scheme</c>. css.rs needs it for the <c>&lt;color&gt;</c>
/// arm of <c>@property</c> value matching and for keyframe declaration support,
/// so it lives here to keep the CSS port self-contained. The coordinator should
/// reconcile it with the style.rs port.
/// </remarks>
public static class CssColor
{
    /// <summary>Parse a CSS color, resolving <c>light-dark()</c> to its light branch.</summary>
    public static RgbaColor? Parse(string value) => ParseForScheme(value, darkScheme: false);

    public static RgbaColor? ParseForScheme(string value, bool darkScheme)
    {
        var raw = value.Trim();
        var lower = CssText.AsciiLower(raw);

        // CSS Color 5 `light-dark(light, dark)` selects by the used color
        // scheme. Both branches still have to be complete valid colors:
        // accepting a valid light arm beside malformed dark syntax would keep a
        // declaration that Chromium rejects at parse time.
        if (lower.StartsWith("light-dark(", StringComparison.Ordinal))
        {
            var innerAndClose = raw["light-dark(".Length..];
            var close = FindMatchingParen(innerAndClose);
            if (close is null || innerAndClose[(close.Value + 1)..].Trim().Length != 0)
            {
                return null;
            }

            var arguments = SplitTopCommas(innerAndClose[..close.Value]);
            if (arguments.Count != 2
                || arguments.Any(argument => argument.Trim().Length == 0 || !IsCompleteColorToken(argument)))
            {
                return null;
            }

            var light = ParseForScheme(arguments[0].Trim(), darkScheme);
            var dark = ParseForScheme(arguments[1].Trim(), darkScheme);
            if (light is null || dark is null)
            {
                return null;
            }

            return darkScheme ? dark : light;
        }

        // CSS custom property with a fallback: var(--name, <fallback>). The
        // variable itself cannot be resolved here, but the fallback after the
        // comma is a real color.
        if (raw.StartsWith("var(", StringComparison.Ordinal))
        {
            var inner = raw[4..];
            if (inner.EndsWith(')'))
            {
                inner = inner[..^1];
            }

            var comma = inner.IndexOf(',');
            return comma >= 0 ? ParseForScheme(inner[(comma + 1)..].Trim(), darkScheme) : null;
        }

        // rgb()/rgba() functional notation.
        var rgbRest = StripPrefix(lower, "rgb(") ?? StripPrefix(lower, "rgba(");
        if (rgbRest is not null)
        {
            return ParseRgbFunction(rgbRest);
        }

        // hsl()/hsla() functional notation.
        var hslRest = StripPrefix(lower, "hsl(") ?? StripPrefix(lower, "hsla(");
        if (hslRest is not null)
        {
            return ParseHslFunction(hslRest);
        }

        // oklch()/oklab() - Tailwind v4's entire palette.
        if (lower.StartsWith("oklch(", StringComparison.Ordinal)
            || lower.StartsWith("oklab(", StringComparison.Ordinal))
        {
            return ParseOkFunction(lower);
        }

        // color-mix(in <space>, c1 p1%, c2 p2%) - Tailwind v4 uses this
        // pervasively, usually to apply opacity.
        if (lower.StartsWith("color-mix(", StringComparison.Ordinal))
        {
            return ParseColorMix(raw, lower, darkScheme);
        }

        var first = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        var keyword = CssText.AsciiLower(first);
        if (keyword.StartsWith('#'))
        {
            return ParseHex(keyword[1..]);
        }

        return keyword switch
        {
            "white" => new RgbaColor(255, 255, 255, 255),
            "black" => new RgbaColor(0, 0, 0, 255),
            "gray" or "grey" => new RgbaColor(128, 128, 128, 255),
            "silver" => new RgbaColor(192, 192, 192, 255),
            "lightgray" or "lightgrey" => new RgbaColor(211, 211, 211, 255),
            "darkgray" or "darkgrey" => new RgbaColor(169, 169, 169, 255),
            "whitesmoke" => new RgbaColor(245, 245, 245, 255),
            "gainsboro" => new RgbaColor(220, 220, 220, 255),
            "red" => new RgbaColor(255, 0, 0, 255),
            "green" => new RgbaColor(0, 128, 0, 255),
            "lime" => new RgbaColor(0, 255, 0, 255),
            "blue" => new RgbaColor(0, 0, 255, 255),
            "navy" => new RgbaColor(0, 0, 128, 255),
            "yellow" => new RgbaColor(255, 255, 0, 255),
            "orange" => new RgbaColor(255, 165, 0, 255),
            "purple" => new RgbaColor(128, 0, 128, 255),
            "maroon" => new RgbaColor(128, 0, 0, 255),
            "teal" => new RgbaColor(0, 128, 128, 255),
            "aqua" or "cyan" => new RgbaColor(0, 255, 255, 255),
            "fuchsia" or "magenta" => new RgbaColor(255, 0, 255, 255),
            "olive" => new RgbaColor(128, 128, 0, 255),
            "transparent" => RgbaColor.Transparent,
            _ => NamedColor(keyword),
        };
    }

    private static string? StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : null;

    private static RgbaColor? ParseRgbFunction(string rest)
    {
        var inner = rest.EndsWith(')') ? rest[..^1] : rest;
        var parts = inner.Split([',', '/', ' '], StringSplitOptions.None)
            .Where(part => part.Trim().Length != 0)
            .ToList();
        if (parts.Count < 3)
        {
            return null;
        }

        var r = Component(parts[0]);
        var g = Component(parts[1]);
        var b = Component(parts[2]);
        if (r is null || g is null || b is null)
        {
            return null;
        }

        byte alpha = 255;
        if (parts.Count > 3 && CssNumber.ParseFloat(parts[3].Trim()) is { } parsedAlpha)
        {
            alpha = ToByte(parsedAlpha * 255f);
        }

        return new RgbaColor(r.Value, g.Value, b.Value, alpha);

        static byte? Component(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.EndsWith('%'))
            {
                return CssNumber.ParseFloat(trimmed.AsSpan()[..^1]) is { } percent ? ToByte(percent * 2.55f) : null;
            }

            return CssNumber.ParseFloat(trimmed) is { } value ? ToByte(value) : null;
        }
    }

    private static RgbaColor? ParseHslFunction(string rest)
    {
        var inner = rest.EndsWith(')') ? rest[..^1] : rest;
        var parts = inner.Split([',', '/', ' '], StringSplitOptions.None)
            .Where(part => part.Trim().Length != 0)
            .ToList();
        if (parts.Count < 3)
        {
            return null;
        }

        var hue = CssNumber.ParseFloat(TrimEndAscii(parts[0].Trim(), "deg"));
        var saturation = CssNumber.ParseFloat(TrimEndAscii(parts[1].Trim(), "%"));
        var lightness = CssNumber.ParseFloat(TrimEndAscii(parts[2].Trim(), "%"));
        if (hue is null || saturation is null || lightness is null)
        {
            return null;
        }

        byte alpha = 255;
        if (parts.Count > 3)
        {
            var raw = parts[3].Trim();
            if (CssNumber.ParseFloat(TrimEndAscii(raw, "%")) is { } value)
            {
                alpha = ToByte(raw.Contains('%', StringComparison.Ordinal) ? value * 2.55f : value * 255f);
            }
        }

        return HslToRgba(hue.Value, Math.Clamp(saturation.Value / 100f, 0f, 1f), Math.Clamp(lightness.Value / 100f, 0f, 1f), alpha);
    }

    private static RgbaColor? ParseOkFunction(string lower)
    {
        var isLch = lower.StartsWith("oklch(", StringComparison.Ordinal);
        var inner = lower[6..];
        if (inner.EndsWith(')'))
        {
            inner = inner[..^1];
        }

        string main;
        string? alphaText = null;
        var slash = inner.IndexOf('/');
        if (slash >= 0)
        {
            main = inner[..slash];
            alphaText = inner[(slash + 1)..];
        }
        else
        {
            main = inner;
        }

        var components = main.Split([',', ' '], StringSplitOptions.None)
            .Where(part => part.Trim().Length != 0)
            .ToList();
        if (components.Count < 3)
        {
            return null;
        }

        var lightness = Number(components[0]);
        var chroma = Number(components[1]);
        if (lightness is null || chroma is null)
        {
            return null;
        }

        var alpha = alphaText is null ? 1f : Number(alphaText) ?? 1f;

        float okA;
        float okB;
        if (isLch)
        {
            var hue = CssNumber.ParseFloat(TrimEndAscii(components[2].Trim(), "deg"));
            if (hue is null)
            {
                return null;
            }

            var radians = hue.Value * MathF.PI / 180f;
            okA = chroma.Value * MathF.Cos(radians);
            okB = chroma.Value * MathF.Sin(radians);
        }
        else
        {
            okA = chroma.Value;
            var third = CssNumber.ParseFloat(components[2].Trim());
            if (third is null)
            {
                return null;
            }

            okB = third.Value;
        }

        return OklabToRgba(lightness.Value, okA, okB, ToByte(alpha * 255f));

        static float? Number(string text)
        {
            var trimmed = text.Trim();
            return trimmed.EndsWith('%')
                ? CssNumber.ParseFloat(trimmed.AsSpan()[..^1]) is { } percent ? percent / 100f : null
                : CssNumber.ParseFloat(trimmed);
        }
    }

    private static RgbaColor? ParseColorMix(string raw, string lower, bool darkScheme)
    {
        var start = lower.IndexOf("color-mix(", StringComparison.Ordinal) + "color-mix(".Length;
        var inner = raw[start..].TrimEnd();
        if (inner.EndsWith(')'))
        {
            inner = inner[..^1];
        }

        var arguments = SplitTopCommas(inner);
        if (arguments.Count < 3)
        {
            return null;
        }

        var first = ParseArgument(arguments[1]);
        var second = ParseArgument(arguments[2]);
        if (first is null || second is null)
        {
            return null;
        }

        var (c1, p1) = first.Value;
        var (c2, p2) = second.Value;
        var (w1, w2) = (p1, p2) switch
        {
            ({ } a, { } b) => (a, b),
            ({ } a, null) => (a, 1f - a),
            (null, { } b) => (1f - b, b),
            _ => (0.5f, 0.5f),
        };

        var total = MathF.Max(w1 + w2, 1e-6f);
        w1 /= total;
        w2 /= total;

        // Mixing with a fully transparent color is the opacity idiom: keep the
        // visible color, scale its alpha (not toward black).
        if (c2.A == 0)
        {
            return c1 with { A = ToByte(c1.A * w1) };
        }

        if (c1.A == 0)
        {
            return c2 with { A = ToByte(c2.A * w2) };
        }

        return new RgbaColor(
            ToByte((c1.R * w1) + (c2.R * w2)),
            ToByte((c1.G * w1) + (c2.G * w2)),
            ToByte((c1.B * w1) + (c2.B * w2)),
            ToByte((c1.A * w1) + (c2.A * w2)));

        (RgbaColor Color, float? Weight)? ParseArgument(string text)
        {
            var trimmed = text.Trim();
            var lastSpace = LastIndexOfWhitespace(trimmed);
            if (lastSpace >= 0)
            {
                var tail = trimmed[(lastSpace + 1)..].Trim();
                if (tail.EndsWith('%') && CssNumber.ParseFloat(tail.AsSpan()[..^1]) is { } percent)
                {
                    var head = ParseForScheme(trimmed[..lastSpace].Trim(), darkScheme);
                    return head is null ? null : (head.Value, percent / 100f);
                }
            }

            var color = ParseForScheme(trimmed, darkScheme);
            return color is null ? null : (color.Value, (float?)null);
        }
    }

    private static int LastIndexOfWhitespace(string value)
    {
        for (var index = value.Length - 1; index >= 0; index--)
        {
            if (CssText.IsWhitespace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static RgbaColor? ParseHex(string digits)
    {
        static byte? Hex(ReadOnlySpan<char> text) =>
            byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

        static byte? Nibble(char digit) => Hex(stackalloc char[] { digit, digit });

        switch (digits.Length)
        {
            case 3:
            {
                var r = Nibble(digits[0]);
                var g = Nibble(digits[1]);
                var b = Nibble(digits[2]);
                return r is null || g is null || b is null ? null : new RgbaColor(r.Value, g.Value, b.Value, 255);
            }

            case 4:
            {
                var r = Nibble(digits[0]);
                var g = Nibble(digits[1]);
                var b = Nibble(digits[2]);
                var a = Nibble(digits[3]);
                return r is null || g is null || b is null || a is null
                    ? null
                    : new RgbaColor(r.Value, g.Value, b.Value, a.Value);
            }

            case 6:
            {
                var r = Hex(digits.AsSpan(0, 2));
                var g = Hex(digits.AsSpan(2, 2));
                var b = Hex(digits.AsSpan(4, 2));
                return r is null || g is null || b is null ? null : new RgbaColor(r.Value, g.Value, b.Value, 255);
            }

            case 8:
            {
                var r = Hex(digits.AsSpan(0, 2));
                var g = Hex(digits.AsSpan(2, 2));
                var b = Hex(digits.AsSpan(4, 2));
                var a = Hex(digits.AsSpan(6, 2));
                return r is null || g is null || b is null || a is null
                    ? null
                    : new RgbaColor(r.Value, g.Value, b.Value, a.Value);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Reject trailing tokens and unbalanced functions before a
    /// <c>light-dark()</c> branch reaches the intentionally permissive legacy
    /// color parser. Nested color functions and their internal whitespace
    /// remain valid.
    /// </summary>
    internal static bool IsCompleteColorToken(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var depth = 0;
        int? firstOpen = null;
        int? outerClose = null;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    if (depth == 0 && firstOpen is null)
                    {
                        firstOpen = index;
                    }

                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth < 0)
                    {
                        return false;
                    }

                    if (depth == 0)
                    {
                        outerClose = index;
                    }

                    break;
            }
        }

        if (depth != 0)
        {
            return false;
        }

        if (firstOpen is { } open)
        {
            return open > 0
                && value[..open].All(character => CssText.IsAsciiAlphanumeric(character) || character == '-')
                && outerClose == value.Length - 1;
        }

        return !value.Any(CssText.IsWhitespace);
    }

    /// <summary>Split on top-level commas, respecting nested <c>()</c>.</summary>
    internal static List<string> SplitTopCommas(string value)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(depth - 1, 0);
                    break;
                case ',' when depth == 0:
                    parts.Add(value[start..index]);
                    start = index + 1;
                    break;
            }
        }

        parts.Add(value[start..]);
        return parts;
    }

    internal static int? FindMatchingParen(string value)
    {
        var depth = 1;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }

                    break;
            }
        }

        return null;
    }

    private static string TrimEndAscii(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;

    /// <summary>Rust's <c>f32::round</c> is half-away-from-zero; .NET's default is banker's rounding.</summary>
    private static byte ToByte(float value) =>
        (byte)Math.Clamp(MathF.Round(value, MidpointRounding.AwayFromZero), 0f, 255f);

    internal static RgbaColor HslToRgba(float hue, float saturation, float lightness, byte alpha)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        var chroma = (1f - MathF.Abs((2f * lightness) - 1f)) * saturation;
        var x = chroma * (1f - MathF.Abs(((hue / 60f) % 2f) - 1f));
        var m = lightness - (chroma / 2f);
        var (r, g, b) = ((uint)hue / 60) switch
        {
            0 => (chroma, x, 0f),
            1 => (x, chroma, 0f),
            2 => (0f, chroma, x),
            3 => (0f, x, chroma),
            4 => (x, 0f, chroma),
            _ => (chroma, 0f, x),
        };

        return new RgbaColor(ToByte((r + m) * 255f), ToByte((g + m) * 255f), ToByte((b + m) * 255f), alpha);
    }

    /// <summary>Standard Bjorn Ottosson OKLab to sRGB matrix.</summary>
    internal static RgbaColor OklabToRgba(float l, float a, float b, byte alpha)
    {
        var lPrime = l + (0.3963377774f * a) + (0.2158037573f * b);
        var mPrime = l - (0.1055613458f * a) - (0.0638541728f * b);
        var sPrime = l - (0.0894841775f * a) - (1.2914855480f * b);
        var lc = lPrime * lPrime * lPrime;
        var mc = mPrime * mPrime * mPrime;
        var sc = sPrime * sPrime * sPrime;
        var lr = (4.0767416621f * lc) - (3.3077115913f * mc) + (0.2309699292f * sc);
        var lg = (-1.2684380046f * lc) + (2.6097574011f * mc) - (0.3413193965f * sc);
        var lb = (-0.0041960863f * lc) - (0.7034186147f * mc) + (1.7076147010f * sc);

        return new RgbaColor(Encode(lr), Encode(lg), Encode(lb), alpha);

        static byte Encode(float channel)
        {
            var x = Math.Clamp(channel, 0f, 1f);
            var s = x <= 0.0031308f ? 12.92f * x : (1.055f * MathF.Pow(x, 1f / 2.4f)) - 0.055f;
            return ToByte(s * 255f);
        }
    }

    private static RgbaColor? NamedColor(string keyword) =>
        NamedColors.TryGetValue(keyword, out var rgb)
            ? new RgbaColor(rgb.R, rgb.G, rgb.B, 255)
            : null;

    /// <summary>The common CSS named colors beyond the handful spelled out above.</summary>
    private static readonly Dictionary<string, (byte R, byte G, byte B)> NamedColors = new(StringComparer.Ordinal)
    {
        ["darkblue"] = (0x00, 0x00, 0x8B),
        ["mediumblue"] = (0x00, 0x00, 0xCD),
        ["royalblue"] = (0x41, 0x69, 0xE1),
        ["dodgerblue"] = (0x1E, 0x90, 0xFF),
        ["cornflowerblue"] = (0x64, 0x95, 0xED),
        ["steelblue"] = (0x46, 0x82, 0xB4),
        ["deepskyblue"] = (0x00, 0xBF, 0xFF),
        ["skyblue"] = (0x87, 0xCE, 0xEB),
        ["lightskyblue"] = (0x87, 0xCE, 0xFA),
        ["lightblue"] = (0xAD, 0xD8, 0xE6),
        ["powderblue"] = (0xB0, 0xE0, 0xE6),
        ["cadetblue"] = (0x5F, 0x9E, 0xA0),
        ["slateblue"] = (0x6A, 0x5A, 0xCD),
        ["darkslateblue"] = (0x48, 0x3D, 0x8B),
        ["midnightblue"] = (0x19, 0x19, 0x70),
        ["indigo"] = (0x4B, 0x00, 0x82),
        ["darkgreen"] = (0x00, 0x64, 0x00),
        ["forestgreen"] = (0x22, 0x8B, 0x22),
        ["seagreen"] = (0x2E, 0x8B, 0x57),
        ["mediumseagreen"] = (0x3C, 0xB3, 0x71),
        ["limegreen"] = (0x32, 0xCD, 0x32),
        ["yellowgreen"] = (0x9A, 0xCD, 0x32),
        ["olivedrab"] = (0x6B, 0x8E, 0x23),
        ["darkolivegreen"] = (0x55, 0x6B, 0x2F),
        ["greenyellow"] = (0xAD, 0xFF, 0x2F),
        ["lightgreen"] = (0x90, 0xEE, 0x90),
        ["palegreen"] = (0x98, 0xFB, 0x98),
        ["springgreen"] = (0x00, 0xFF, 0x7F),
        ["mediumaquamarine"] = (0x66, 0xCD, 0xAA),
        ["aquamarine"] = (0x7F, 0xFF, 0xD4),
        ["turquoise"] = (0x40, 0xE0, 0xD0),
        ["mediumturquoise"] = (0x48, 0xD1, 0xCC),
        ["darkcyan"] = (0x00, 0x8B, 0x8B),
        ["crimson"] = (0xDC, 0x14, 0x3C),
        ["firebrick"] = (0xB2, 0x22, 0x22),
        ["darkred"] = (0x8B, 0x00, 0x00),
        ["indianred"] = (0xCD, 0x5C, 0x5C),
        ["tomato"] = (0xFF, 0x63, 0x47),
        ["orangered"] = (0xFF, 0x45, 0x00),
        ["coral"] = (0xFF, 0x7F, 0x50),
        ["salmon"] = (0xFA, 0x80, 0x72),
        ["lightsalmon"] = (0xFF, 0xA0, 0x7A),
        ["darksalmon"] = (0xE9, 0x96, 0x7A),
        ["hotpink"] = (0xFF, 0x69, 0xB4),
        ["deeppink"] = (0xFF, 0x14, 0x93),
        ["pink"] = (0xFF, 0xC0, 0xCB),
        ["lightpink"] = (0xFF, 0xB6, 0xC1),
        ["palevioletred"] = (0xDB, 0x70, 0x93),
        ["mediumvioletred"] = (0xC7, 0x15, 0x85),
        ["violet"] = (0xEE, 0x82, 0xEE),
        ["orchid"] = (0xDA, 0x70, 0xD6),
        ["plum"] = (0xDD, 0xA0, 0xDD),
        ["mediumpurple"] = (0x93, 0x70, 0xDB),
        ["blueviolet"] = (0x8A, 0x2B, 0xE2),
        ["darkviolet"] = (0x94, 0x00, 0xD3),
        ["darkorchid"] = (0x99, 0x32, 0xCC),
        ["darkmagenta"] = (0x8B, 0x00, 0x8B),
        ["lavender"] = (0xE6, 0xE6, 0xFA),
        ["thistle"] = (0xD8, 0xBF, 0xD8),
        ["gold"] = (0xFF, 0xD7, 0x00),
        ["goldenrod"] = (0xDA, 0xA5, 0x20),
        ["darkgoldenrod"] = (0xB8, 0x86, 0x0B),
        ["khaki"] = (0xF0, 0xE6, 0x8C),
        ["darkkhaki"] = (0xBD, 0xB7, 0x6B),
        ["peachpuff"] = (0xFF, 0xDA, 0xB9),
        ["moccasin"] = (0xFF, 0xE4, 0xB5),
        ["papayawhip"] = (0xFF, 0xEF, 0xD5),
        ["wheat"] = (0xF5, 0xDE, 0xB3),
        ["tan"] = (0xD2, 0xB4, 0x8C),
        ["burlywood"] = (0xDE, 0xB8, 0x87),
        ["sandybrown"] = (0xF4, 0xA4, 0x60),
        ["peru"] = (0xCD, 0x85, 0x3F),
        ["chocolate"] = (0xD2, 0x69, 0x1E),
        ["sienna"] = (0xA0, 0x52, 0x2D),
        ["saddlebrown"] = (0x8B, 0x45, 0x13),
        ["brown"] = (0xA5, 0x2A, 0x2A),
        ["rosybrown"] = (0xBC, 0x8F, 0x8F),
        ["darkorange"] = (0xFF, 0x8C, 0x00),
        ["lightyellow"] = (0xFF, 0xFF, 0xE0),
        ["lightgoldenrodyellow"] = (0xFA, 0xFA, 0xD2),
        ["lemonchiffon"] = (0xFF, 0xFA, 0xCD),
        ["beige"] = (0xF5, 0xF5, 0xDC),
        ["ivory"] = (0xFF, 0xFF, 0xF0),
        ["azure"] = (0xF0, 0xFF, 0xFF),
        ["mintcream"] = (0xF5, 0xFF, 0xFA),
        ["honeydew"] = (0xF0, 0xFF, 0xF0),
        ["snow"] = (0xFF, 0xFA, 0xFA),
        ["seashell"] = (0xFF, 0xF5, 0xEE),
        ["linen"] = (0xFA, 0xF0, 0xE6),
        ["oldlace"] = (0xFD, 0xF5, 0xE6),
        ["floralwhite"] = (0xFF, 0xFA, 0xF0),
        ["ghostwhite"] = (0xF8, 0xF8, 0xFF),
        ["aliceblue"] = (0xF0, 0xF8, 0xFF),
        ["lavenderblush"] = (0xFF, 0xF0, 0xF5),
        ["mistyrose"] = (0xFF, 0xE4, 0xE1),
        ["cornsilk"] = (0xFF, 0xF8, 0xDC),
        ["antiquewhite"] = (0xFA, 0xEB, 0xD7),
        ["bisque"] = (0xFF, 0xE4, 0xC4),
        ["blanchedalmond"] = (0xFF, 0xEB, 0xCD),
        ["navajowhite"] = (0xFF, 0xDE, 0xAD),
        ["dimgray"] = (0x69, 0x69, 0x69),
        ["dimgrey"] = (0x69, 0x69, 0x69),
        ["slategray"] = (0x70, 0x80, 0x90),
        ["slategrey"] = (0x70, 0x80, 0x90),
        ["lightslategray"] = (0x77, 0x88, 0x99),
        ["lightslategrey"] = (0x77, 0x88, 0x99),
        ["darkslategray"] = (0x2F, 0x4F, 0x4F),
        ["darkslategrey"] = (0x2F, 0x4F, 0x4F),
    };
}
