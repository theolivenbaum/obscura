using System.Globalization;
using System.Reflection;
using System.Text;

namespace Obscura.Render;

/// <summary>
/// The faces the engine ships, and the CSS-family-to-bundled-face mapping.
/// </summary>
/// <remarks>
/// The engine never uses system fonts: it embeds its own faces so rasterization is identical
/// on every host and works on distroless images with no fontconfig. The C# build links the
/// same font binaries out of <c>crates/obscura-render/assets/</c> that the Rust engine embeds,
/// so the two can never rasterize against different files.
/// <para>
/// Chrome on this class of host renders <c>sans-serif</c> and the ubiquitous Arial/Helvetica
/// stacks as Liberation Sans, <c>system-ui</c> as DejaVu Sans, <c>serif</c> as Liberation
/// Serif, and <c>monospace</c> as Liberation Mono. Matching those keeps text metrics aligned
/// with Chromium instead of drifting between unrelated host faces.
/// </para>
/// </remarks>
public static class FontAssets
{
    public const string SansFamily = "Liberation Sans";
    public const string SerifFamily = "Liberation Serif";
    public const string MonoFamily = "Liberation Mono";
    public const string SystemFamily = "DejaVu Sans";
    public const string EmojiFamily = "Noto Color Emoji";

    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    /// <summary>The bundled faces, in the order the Rust engine loads them.</summary>
    internal static readonly string[] BundledFaceFiles =
    [
        "liberation-sans",
        "liberation-sans-bold",
        "liberation-sans-oblique",
        "liberation-sans-boldoblique",
        "liberation-serif",
        "liberation-serif-bold",
        "liberation-serif-oblique",
        "liberation-serif-boldoblique",
        "liberation-mono",
        "liberation-mono-bold",
        "liberation-mono-oblique",
        "liberation-mono-boldoblique",
        "dejavu-sans",
        "dejavu-sans-bold",
    ];

    internal const string EmojiFaceFile = "noto-color-emoji";

    /// <summary>Read one embedded face by its asset stem (no extension).</summary>
    public static byte[] Load(string stem)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(stem, out byte[]? cached))
            {
                return cached;
            }

            string name = "Obscura.Render.Assets." + stem + ".ttf";
            Assembly assembly = typeof(FontAssets).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"embedded font resource '{name}' is missing");
            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            byte[] data = buffer.ToArray();
            Cache[stem] = data;
            return data;
        }
    }

    /// <summary>
    /// Whether text contains a code point that can request emoji presentation.
    /// </summary>
    /// <remarks>
    /// Keeps the color face out of ordinary render passes: its bitmap table is large, and
    /// loading it for every page would spend RSS and startup time even when no emoji can be
    /// shaped.
    /// </remarks>
    public static bool TextMayNeedEmojiFont(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (MayRequestEmoji(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MayRequestEmoji(int ch) => ch switch
    {
        0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 => true,
        >= 0x2194 and <= 0x2199 => true,
        >= 0x21A9 and <= 0x21AA => true,
        >= 0x231A and <= 0x231B => true,
        0x2328 or 0x23CF => true,
        >= 0x23E9 and <= 0x23F3 => true,
        >= 0x23F8 and <= 0x23FA => true,
        0x24C2 => true,
        >= 0x25AA and <= 0x25AB => true,
        0x25B6 or 0x25C0 => true,
        >= 0x25FB and <= 0x25FE => true,
        >= 0x2600 and <= 0x2604 => true,
        0x2611 => true,
        >= 0x2614 and <= 0x2615 => true,
        0x2618 or 0x261D or 0x2620 => true,
        >= 0x2622 and <= 0x2623 => true,
        0x2626 or 0x262A => true,
        >= 0x262E and <= 0x262F => true,
        >= 0x2638 and <= 0x263A => true,
        0x2640 or 0x2642 => true,
        >= 0x2648 and <= 0x2653 => true,
        >= 0x265F and <= 0x2660 => true,
        0x2663 => true,
        >= 0x2665 and <= 0x2666 => true,
        0x2668 or 0x267B => true,
        >= 0x267E and <= 0x267F => true,
        >= 0x2692 and <= 0x2697 => true,
        0x2699 => true,
        >= 0x269B and <= 0x269C => true,
        >= 0x26A0 and <= 0x26A1 => true,
        0x26A7 => true,
        >= 0x26AA and <= 0x26AB => true,
        >= 0x26B0 and <= 0x26B1 => true,
        >= 0x26BD and <= 0x26BE => true,
        >= 0x26C4 and <= 0x26C5 => true,
        0x26C8 => true,
        >= 0x26CE and <= 0x26CF => true,
        0x26D1 => true,
        >= 0x26D3 and <= 0x26D4 => true,
        >= 0x26E9 and <= 0x26EA => true,
        >= 0x26F0 and <= 0x26F5 => true,
        >= 0x26F7 and <= 0x26FA => true,
        0x26FD or 0x2702 or 0x2705 => true,
        >= 0x2708 and <= 0x270D => true,
        0x270F or 0x2712 or 0x2714 or 0x2716 or 0x271D or 0x2721 or 0x2728 => true,
        >= 0x2733 and <= 0x2734 => true,
        0x2744 or 0x2747 or 0x274C or 0x274E => true,
        >= 0x2753 and <= 0x2755 => true,
        0x2757 => true,
        >= 0x2763 and <= 0x2764 => true,
        >= 0x2795 and <= 0x2797 => true,
        0x27A1 or 0x27B0 or 0x27BF => true,
        >= 0x2934 and <= 0x2935 => true,
        >= 0x2B05 and <= 0x2B07 => true,
        >= 0x2B1B and <= 0x2B1C => true,
        0x2B50 or 0x2B55 or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0xFE0F => true,
        >= 0x1F000 and <= 0x1FAFF => true,
        _ => false,
    };

    /// <summary>
    /// Map a CSS <c>font-family</c> list to a bundled face the way Chromium resolves the
    /// generic families on this host. The first recognizable family wins, matching CSS
    /// fallback order.
    /// </summary>
    public static string ResolveFontFamily(string? family)
    {
        if (family is null)
        {
            return SansFamily;
        }

        foreach (string token in family.Split(','))
        {
            string? resolved = BundledFamilyForCssToken(token);
            if (resolved is not null)
            {
                return resolved;
            }

            // Unrecognized named webfont: keep scanning for a generic fallback.
        }

        return SansFamily;
    }

    public static string? BundledFamilyForCssToken(string token)
    {
        string trimmed = token.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed is "system-ui" or "ui-sans-serif")
        {
            return SystemFamily;
        }

        if (trimmed == "monospace"
            || trimmed.Contains("mono", StringComparison.Ordinal)
            || trimmed.Contains("courier", StringComparison.Ordinal)
            || trimmed.Contains("consol", StringComparison.Ordinal)
            || trimmed is "menlo" or "monaco" or "code")
        {
            return MonoFamily;
        }

        if (trimmed == "serif"
            || trimmed == "georgia"
            || trimmed.Contains("times", StringComparison.Ordinal)
            || trimmed == "cambria"
            || trimmed.Contains("garamond", StringComparison.Ordinal)
            || trimmed.Contains("liberation serif", StringComparison.Ordinal)
            || trimmed == "roman")
        {
            return SerifFamily;
        }

        if (trimmed == "sans-serif"
            || trimmed.Contains("sans", StringComparison.Ordinal)
            || trimmed is "arial" or "helvetica" or "helvetica neue" or "-apple-system"
                or "roboto" or "segoe ui" or "inter" or "verdana" or "tahoma")
        {
            return SansFamily;
        }

        return null;
    }

    /// <summary>
    /// Resolve <c>line-height: normal</c> from the selected face's horizontal header.
    /// </summary>
    /// <remarks>
    /// Chromium's FreeType-backed Linux path grid-fits the ascent, descent, and line gap
    /// independently before adding them. Multiplying their sum by the font size (or rounding
    /// the final line height) is observably different at fractional and small sizes:
    /// Liberation Sans at 9.333px is 10px in Chromium, not 11px.
    /// </remarks>
    public static FaceMetrics BundledFaceMetrics(string family)
    {
        (float ascent, float descent, float lineGap) = family switch
        {
            SerifFamily => (1825f, 443f, 87f),
            MonoFamily => (1705f, 615f, 0f),
            SystemFamily => (1901f, 483f, 0f),
            _ => (1854f, 434f, 67f),
        };

        return new FaceMetrics(ascent, descent, lineGap, 2048f);
    }

    public static float NormalLineHeight(float fontSize, FaceMetrics metrics)
    {
        float scale = fontSize / F32.Max(metrics.UnitsPerEm, 1f);
        return F32.Round(metrics.Ascent * scale)
            + F32.Round(metrics.Descent * scale)
            + F32.Round(metrics.LineGap * scale);
    }

    /// <summary>
    /// Grid-fitted font ascent plus descent, excluding both the face line gap and authored CSS
    /// <c>line-height</c>.
    /// </summary>
    /// <remarks>
    /// A non-replaced inline has two different vertical boxes. Its line participation uses the
    /// used line height, while its painted fragment/client rect uses this raw font box plus
    /// block-axis padding and border. Chromium and Gecko both fit ascent and descent
    /// independently.
    /// </remarks>
    public static (float Ascent, float Descent) FittedFontBoxMetrics(float fontSize, FaceMetrics metrics)
    {
        float scale = fontSize / F32.Max(metrics.UnitsPerEm, 1f);
        return (F32.Round(metrics.Ascent * scale), F32.Round(metrics.Descent * scale));
    }

    /// <summary>
    /// CSS Fonts' asymmetric missing-weight search. In particular, 600 selects 700 (not 400)
    /// when a family only provides regular and bold faces.
    /// </summary>
    public static ushort MatchFontWeight(ushort requested, IReadOnlyList<ushort> available)
    {
        foreach (ushort weight in available)
        {
            if (weight == requested)
            {
                return requested;
            }
        }

        List<ushort> weights = [.. available];
        weights.Sort();
        int write = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            if (i == 0 || weights[i] != weights[i - 1])
            {
                weights[write++] = weights[i];
            }
        }

        weights.RemoveRange(write, weights.Count - write);
        if (weights.Count == 0)
        {
            return requested;
        }

        if (requested is >= 400 and <= 500)
        {
            ushort? candidate = MinWhere(weights, w => w >= requested && w <= 500)
                ?? MaxWhere(weights, w => w < requested)
                ?? MinWhere(weights, w => w > 500);
            return candidate ?? requested;
        }

        if (requested < 400)
        {
            ushort? candidate = MaxWhere(weights, w => w <= requested)
                ?? MinWhere(weights, w => w > requested);
            return candidate ?? requested;
        }

        ushort? heavier = MinWhere(weights, w => w >= requested)
            ?? MaxWhere(weights, w => w < requested);
        return heavier ?? requested;
    }

    private static ushort? MinWhere(List<ushort> weights, Func<ushort, bool> predicate)
    {
        ushort? best = null;
        foreach (ushort weight in weights)
        {
            if (predicate(weight) && (best is null || weight < best))
            {
                best = weight;
            }
        }

        return best;
    }

    private static ushort? MaxWhere(List<ushort> weights, Func<ushort, bool> predicate)
    {
        ushort? best = null;
        foreach (ushort weight in weights)
        {
            if (predicate(weight) && (best is null || weight > best))
            {
                best = weight;
            }
        }

        return best;
    }

    internal static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
