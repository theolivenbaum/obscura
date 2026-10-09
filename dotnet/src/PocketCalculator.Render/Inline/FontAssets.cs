using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using HbBlob = HarfBuzzSharp.Blob;

namespace PocketCalculator.Render;

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
/// <para>
/// Noto Sans CJK SC is the one face the Rust engine does not embed (its CJK text is missing
/// glyphs). It is the fallback for Han, kana and Hangul, and is loaded only into a render pass
/// whose text needs it, as the emoji face is. See <c>Assets/FONT-PROVENANCE.md</c>.
/// </para>
/// </remarks>
public static class FontAssets
{
    public const string SansFamily = "Liberation Sans";
    public const string SerifFamily = "Liberation Serif";
    public const string MonoFamily = "Liberation Mono";
    public const string SystemFamily = "DejaVu Sans";
    public const string EmojiFamily = "Noto Color Emoji";

    /// <summary>The internal family name of the embedded CJK face.</summary>
    public const string CjkFamily = "Noto Sans CJK SC";

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

    /// <summary>
    /// Noto Sans CJK SC Regular, the fallback for Chinese, Japanese and Korean text. Its file
    /// name carries the extension because it is the one CFF (<c>.otf</c>) asset.
    /// </summary>
    internal const string CjkFaceFile = "noto-sans-cjk-sc-regular.otf";

    private static readonly Dictionary<string, EmbeddedFontSource> Mapped = new(StringComparer.Ordinal);

    /// <summary>
    /// Read one embedded face by its asset stem. A name without an extension is a <c>.ttf</c>.
    /// </summary>
    public static byte[] Load(string stem)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(stem, out byte[]? cached))
            {
                return cached;
            }

            string name = ResourceName(stem);
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

    private static string ResourceName(string stem) =>
        "PocketCalculator.Render.Assets." + stem + (Path.HasExtension(stem) ? string.Empty : ".ttf");

    /// <summary>
    /// One large embedded face shared by every render pass of the process, without copying it
    /// into the managed heap.
    /// </summary>
    /// <remarks>
    /// The emoji (10 MB) and CJK (16 MB) faces used to be read into a managed array, copied
    /// into an <see cref="SKData"/> and copied twice more into a HarfBuzz blob for every
    /// <see cref="TextEngine"/> that needed them, which is every layout pass of a page that
    /// shows such text. Here Skia reads the face straight out of the assembly's resource
    /// section, which the runtime already holds for the life of the process, and the
    /// HarfBuzz blob is built once with <see cref="HarfBuzzSharp.MemoryMode.Duplicate"/> and
    /// shared: HarfBuzz still never sees managed memory, and a blob is immutable and
    /// reference-counted, so faces on any thread can share it.
    /// </remarks>
    internal static EmbeddedFontSource Embedded(string file)
    {
        lock (Gate)
        {
            if (Mapped.TryGetValue(file, out EmbeddedFontSource? cached))
            {
                return cached;
            }

            string name = ResourceName(file);
            Stream stream = typeof(FontAssets).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"embedded font resource '{name}' is missing");
            IntPtr pointer;
            int length;
            unsafe
            {
                if (stream is UnmanagedMemoryStream mapped && mapped.Length <= int.MaxValue)
                {
                    // The resource section of a loaded assembly: never moved, never unloaded
                    // (the engine lives in the default load context), so the stream is kept
                    // open for the life of the process.
                    pointer = (IntPtr)mapped.PositionPointer;
                    length = (int)mapped.Length;
                }
                else
                {
                    // An assembly loaded from bytes: copy once into native memory that is
                    // likewise never freed.
                    using (stream)
                    {
                        length = checked((int)stream.Length);
                        pointer = Marshal.AllocHGlobal(length);
                        stream.ReadExactly(new Span<byte>((void*)pointer, length));
                    }
                }
            }

            var source = new EmbeddedFontSource(file, pointer, length);
            Mapped[file] = source;
            return source;
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
    /// <summary>
    /// <see cref="TextMayNeedEmojiFont"/> and <see cref="TextMayNeedCjkFont"/> of one text, by
    /// the identity of the string for a long one.
    /// </summary>
    /// <remarks>
    /// Every layout pass scans every text node of the document for the two optional faces,
    /// including the text of every <c>&lt;style&gt;</c> and <c>&lt;script&gt;</c> (nvidia.com:
    /// 2 MB of CSS, 7ms a pass). An unchanged text node hands every pass the same string, whose
    /// answer is a function of its contents.
    /// </remarks>
    internal static (bool Emoji, bool Cjk) TextMayNeedOptionalFaces(string text)
    {
        if (text.Length < 256)
        {
            return (TextMayNeedEmojiFont(text), TextMayNeedCjkFont(text));
        }

        if (OptionalFaceMemo.TryGetValue(text, out StrongBox<byte>? memo))
        {
            return ((memo.Value & 1) != 0, (memo.Value & 2) != 0);
        }

        bool emoji = TextMayNeedEmojiFont(text);
        bool cjk = TextMayNeedCjkFont(text);
        OptionalFaceMemo.AddOrUpdate(text, new StrongBox<byte>((byte)((emoji ? 1 : 0) | (cjk ? 2 : 0))));
        return (emoji, cjk);
    }

    private static readonly ConditionalWeakTable<string, StrongBox<byte>> OptionalFaceMemo = new();

    public static bool TextMayNeedEmojiFont(string text)
    {
        // No code point below U+00A9 requests the emoji face, and every pass scans every text
        // node of the document: let the vectorized ASCII test answer most of them.
        if (System.Text.Ascii.IsValid(text))
        {
            return false;
        }

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (MayRequestEmoji(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether text contains a code point only the embedded CJK face can draw: Han ideographs,
    /// kana, Hangul, bopomofo, CJK punctuation and the fullwidth forms.
    /// </summary>
    /// <remarks>
    /// Keeps the 16 MB face out of every render pass of a page that has no such text, the same
    /// way <see cref="TextMayNeedEmojiFont"/> does for the color face. None of these ranges is
    /// covered by Liberation or DejaVu, so loading on them changes no Latin layout.
    /// </remarks>
    public static bool TextMayNeedCjkFont(string text)
    {
        if (System.Text.Ascii.IsValid(text))
        {
            return false;
        }

        foreach (char ch in text)
        {
            // Supplementary ideographs (Extension B and on) arrive as a surrogate pair; the
            // high surrogates D840-D8BF cover planes 2 and 3 entirely.
            if (ch >= 0x1100 && (IsCjkCodepoint(ch) || ch is >= '\uD840' and <= '\uD8BF'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A BMP code point in a block the embedded CJK face exists for.</summary>
    internal static bool IsCjkCodepoint(int ch) => ch switch
    {
        >= 0x1100 and <= 0x11FF => true,   // Hangul Jamo
        >= 0x2E80 and <= 0x2FDF => true,   // CJK radicals, Kangxi radicals
        >= 0x2FF0 and <= 0x4DBF => true,   // ideographic description .. Extension A
        >= 0x4E00 and <= 0x9FFF => true,   // CJK Unified Ideographs
        >= 0xA960 and <= 0xA97F => true,   // Hangul Jamo Extended-A
        >= 0xAC00 and <= 0xD7FF => true,   // Hangul Syllables, Jamo Extended-B
        >= 0xF900 and <= 0xFAFF => true,   // CJK Compatibility Ideographs
        >= 0xFE10 and <= 0xFE1F => true,   // vertical forms
        >= 0xFE30 and <= 0xFE4F => true,   // CJK compatibility forms
        >= 0xFF00 and <= 0xFFEF => true,   // halfwidth and fullwidth forms
        _ => false,
    };

    /// <summary>
    /// Whether a CSS <c>font-family</c> list names the embedded CJK face, which then has to be
    /// loaded even for text with no CJK code point (its Latin glyphs and metrics apply).
    /// </summary>
    public static bool FamilyNamesCjkFace(string? family)
    {
        if (family is null || !family.Contains("cjk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string token in family.Split(','))
        {
            if (BundledFamilyForCssToken(token) == CjkFamily)
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

        // The regional Noto Sans CJK families are one design with one set of metrics; they
        // differ only in which regional glyph forms are the default. A host that has one of
        // them has all five (the distribution packages ship the collection), so each resolves
        // to the embedded SC face rather than to Liberation Sans through the "sans" rule below.
        // Chromium with only the SC face installed resolves the SC name alone, and falls
        // through on PingFang SC, Microsoft YaHei, SimSun, Meiryo, Malgun Gothic and the
        // Google Fonts "Noto Sans SC/JP/KR" names, which is what this table does too: those
        // pages get the CJK face per glyph, through fallback.
        if (trimmed is "noto sans cjk sc" or "noto sans cjk tc" or "noto sans cjk jp"
            or "noto sans cjk kr" or "noto sans cjk hk")
        {
            return CjkFamily;
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
        // The x-height is `OS/2.sxHeight` where the face ships one (the three Liberation
        // faces, OS/2 version 3) and Skia's measurement of the 'x' glyph where it does not
        // (DejaVu Sans, OS/2 version 1) - the same split `FontTables.ReadFaceMetrics` makes.
        // `vertical-align: middle` is what reads it.
        //
        // The DejaVu number is where this port cannot match Chromium exactly: with no declared
        // x-height, Chromium re-measures the 'x' outline GRID-FITTED AT THE USED SIZE, so its
        // x-height is 6px at 10px, 9px at 16px and 14px at 24px - not one em fraction at all.
        // Measured here at the em, which costs `vertical-align: middle` up to half a pixel on
        // that face and nothing on the Liberation ones.
        (float ascent, float descent, float lineGap, float unitsPerEm, float xHeight) = family switch
        {
            SerifFamily => (1825f, 443f, 87f, 2048f, 940f),
            MonoFamily => (1705f, 615f, 0f, 2048f, 1082f),
            SystemFamily => (1901f, 483f, 0f, 2048f, 1120f),

            // hhea 1160/-288/0 (no USE_TYPO_METRICS), OS/2 v3 sxHeight 543, 1000 units.
            CjkFamily => (1160f, 288f, 0f, 1000f, 543f),
            _ => (1854f, 434f, 67f, 2048f, 1082f),
        };

        return new FaceMetrics(ascent, descent, lineGap, unitsPerEm, XHeight: xHeight);
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
    /// The used <c>line-height</c> as Blink stores it: a <c>LayoutUnit</c>, which is 1/64px.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/inline.rs, which keeps the CSS value as an f32.
    /// It is observable once the leading is split around the font box: Chromium 141 lays out
    /// <c>16px/25.7px 'Liberation Mono'</c> with a 32px span at 30.703125px, which is the
    /// 1/64-rounded 25.703125 and not the 25.6875 a truncation would give.
    /// </remarks>
    public static float QuantizedLineHeight(float lineHeight) => RoundedToLayoutUnit(lineHeight);

    /// <summary>A value rounded to the nearest 1/64px, as <c>LayoutUnit::FromFloatRound</c>.</summary>
    public static float RoundedToLayoutUnit(float value) => F32.Round(value * 64f) / 64f;

    /// <summary>A value as Blink's <c>LayoutUnit(float)</c> stores it: 1/64px, toward zero.</summary>
    public static float TruncatedToLayoutUnit(float value) => MathF.Truncate(value * 64f) / 64f;

    /// <summary>
    /// The half of the line box an inline box occupies above and below its own baseline: the
    /// grid-fitted font box with half the leading added to each side (CSS 2.1 10.8.1).
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/inline.rs, which has no such split - it gives
    /// a line the largest <c>line-height</c> on it and aligns every span to the line's top.
    /// The halves are not symmetric in Chromium: Blink floors the ascent to whole pixels and
    /// then takes the descent as <c>line-height - ascent</c>, so the descent carries the
    /// fractional remainder and can go negative. Measured on Chromium 141: a 20px span in an
    /// <c>16px/18px 'Liberation Mono'</c> block makes the line 19px (ascent
    /// <c>floor(17 + (18-23)/2) = 14</c>, descent <c>18-14 = 4</c>, against the strut's 13/5),
    /// and a 48px span makes it 27px (ascent 22, descent -4). Rounding the pair symmetrically
    /// instead gives 19.5 and 27 - right for one and wrong for the other. See "Known
    /// deviations" in todo.md.
    /// </remarks>
    public static (float Above, float Below) LineBoxHalves(
        float fontSize,
        float lineHeight,
        FaceMetrics metrics)
    {
        (float ascent, float descent) = FittedFontBoxMetrics(fontSize, metrics);
        float used = QuantizedLineHeight(lineHeight);
        float above = MathF.Floor(ascent + ((used - (ascent + descent)) / 2f));

        return (above, used - above);
    }

    /// <summary>The face's x-height at <paramref name="fontSize"/>, in CSS pixels.</summary>
    public static float XHeight(float fontSize, FaceMetrics metrics) =>
        metrics.XHeight * fontSize / F32.Max(metrics.UnitsPerEm, 1f);

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

/// <summary>
/// A large embedded face kept outside the managed heap for the life of the process. See
/// <see cref="FontAssets.Embedded"/>.
/// </summary>
internal sealed class EmbeddedFontSource(string file, IntPtr pointer, int length)
{
    private readonly Lock _gate = new();
    private SKData? _data;
    private HbBlob? _blob;

    public string File { get; } = file;

    public int Length { get; } = length;

    /// <summary>
    /// Whether Chromium would embolden this face for a bold request it cannot meet. True for
    /// the single-weight CJK outline face; false for the color emoji face, whose bitmaps Skia
    /// does not embolden.
    /// </summary>
    public bool SynthesizesBold => string.Equals(File, FontAssets.CjkFaceFile, StringComparison.Ordinal);

    /// <summary>Skia's view of the bytes, referencing them in place.</summary>
    public SKData Data
    {
        get
        {
            lock (_gate)
            {
                return _data ??= SKData.Create(pointer, Length);
            }
        }
    }

    /// <summary>One HarfBuzz blob over a native duplicate of the bytes, shared by every face.</summary>
    public HbBlob Blob
    {
        get
        {
            lock (_gate)
            {
                if (_blob is null)
                {
                    var blob = new HbBlob(pointer, Length, HarfBuzzSharp.MemoryMode.Duplicate);
                    blob.MakeImmutable();
                    _blob = blob;
                }

                return _blob;
            }
        }
    }

    /// <summary>A managed copy, for the few callers that want the raw bytes.</summary>
    public byte[] ToArray()
    {
        byte[] copy = new byte[Length];
        Marshal.Copy(pointer, copy, 0, Length);
        return copy;
    }
}
