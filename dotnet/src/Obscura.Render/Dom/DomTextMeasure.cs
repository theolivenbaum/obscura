// Port of `text_width` in crates/obscura-render/src/dom.rs and the `measure_text` /
// `fallback_font_bytes` pair it calls in paint.rs.
//
// RECONCILIATION NOTE: paint.rs is not ported yet, and this pair is the only part of it the
// render-tree build needs (native control label widths and the static-font word fallback).
// When paint.rs lands, it should call these rather than adding a second copy.
using SkiaSharp;

namespace Obscura.Render;

internal static class DomTextMeasure
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, SKFont> Fonts = new(StringComparer.Ordinal);

    /// <summary>
    /// Text width for layout, from real glyph metrics of the deterministic bundled face.
    /// </summary>
    internal static float TextWidth(
        string text,
        float size,
        bool isBold,
        string? family,
        float letterSpacing)
    {
        // Rust counts Unicode scalar values (`str::chars`). C# strings are UTF-16, so a
        // non-BMP character is two `char` values; enumerate runes to keep letter-spacing
        // from being applied twice per astral code point.
        int glyphCount = 0;
        foreach (System.Text.Rune rune in text.EnumerateRunes())
        {
            if (!System.Text.Rune.IsControl(rune))
            {
                glyphCount++;
            }
        }

        return MeasureText(text, size, isBold, family) + (glyphCount * letterSpacing);
    }

    internal static float MeasureText(string text, float size, bool isBold, string? family)
    {
        if (text.Length == 0 || !float.IsFinite(size) || size <= 0f)
        {
            return 0f;
        }

        SKFont font = FallbackFont(family);
        float width = MeasureAdvances(font, text, size, out int glyphCount);

        // paint.rs adds one pixel per glyph as its synthetic-bold advance.
        if (isBold)
        {
            width += glyphCount;
        }

        return width;
    }

    private static float MeasureAdvances(SKFont font, string text, float size, out int glyphCount)
    {
        Span<char> buffer = stackalloc char[text.Length];
        int length = 0;
        // Rust advances one glyph per Unicode scalar value; C# strings are UTF-16, so the
        // scalar count is taken over runes while the shaped buffer keeps its code units.
        glyphCount = 0;
        foreach (System.Text.Rune rune in text.EnumerateRunes())
        {
            if (System.Text.Rune.IsControl(rune))
            {
                continue;
            }

            length += rune.EncodeToUtf16(buffer[length..]);
            glyphCount++;
        }

        if (length == 0)
        {
            return 0f;
        }

        lock (Gate)
        {
            font.Size = size;
            ReadOnlySpan<char> visible = buffer[..length];
            ushort[] glyphs = font.GetGlyphs(visible);
            if (glyphs.Length == 0)
            {
                return 0f;
            }

            float[] widths = font.GetGlyphWidths(glyphs);
            float total = 0f;
            foreach (float advance in widths)
            {
                total += advance;
            }

            return total;
        }
    }

    private static SKFont FallbackFont(string? family)
    {
        string stem = FallbackFaceStem(family);
        lock (Gate)
        {
            if (Fonts.TryGetValue(stem, out SKFont? cached))
            {
                return cached;
            }

            using SKData data = SKData.CreateCopy(FontAssets.Load(stem));
            SKTypeface typeface = SKTypeface.FromData(data)
                ?? throw new InvalidOperationException($"bundled face '{stem}' failed to load");
            SKFont font = new(typeface, 16f)
            {
                Subpixel = true,
                Hinting = SKFontHinting.None,
                LinearMetrics = true,
            };
            Fonts[stem] = font;
            return font;
        }
    }

    /// <summary>Port of paint.rs <c>fallback_font_bytes</c>, by asset stem.</summary>
    internal static string FallbackFaceStem(string? family)
    {
        if (family is null)
        {
            return "liberation-sans";
        }

        foreach (string raw in family.Split(','))
        {
            string token = raw.Trim().Trim('"', '\'').ToLowerInvariant();
            if (token is "system-ui" or "ui-sans-serif")
            {
                return "dejavu-sans";
            }

            if (token is "monospace" or "menlo" or "monaco" or "code"
                || token.Contains("mono", StringComparison.Ordinal)
                || token.Contains("courier", StringComparison.Ordinal)
                || token.Contains("consol", StringComparison.Ordinal))
            {
                return "liberation-mono";
            }

            if (token is "serif" or "georgia" or "cambria" or "roman"
                || token.Contains("times", StringComparison.Ordinal)
                || token.Contains("garamond", StringComparison.Ordinal)
                || token.Contains("liberation serif", StringComparison.Ordinal))
            {
                return "liberation-serif";
            }

            if (token is "sans-serif" or "arial" or "helvetica" or "helvetica neue"
                or "-apple-system" or "roboto" or "segoe ui" or "inter" or "verdana" or "tahoma"
                || token.Contains("sans", StringComparison.Ordinal))
            {
                return "liberation-sans";
            }
        }

        return "liberation-sans";
    }
}
