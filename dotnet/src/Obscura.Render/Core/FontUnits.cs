// The font-relative CSS units CSS Values 4 defines against the element's own first available
// font. crates/obscura-render has no equivalent: style.rs scales the font size by a constant.
using System.Runtime.CompilerServices;

namespace Obscura.Render;

/// <summary>
/// The pixel size of the three font-relative CSS units for one element: <c>em</c>, <c>ch</c> and
/// <c>ex</c>.
/// </summary>
/// <remarks>
/// CSS Values 4 defines <c>1ch</c> as the advance of U+0030 and <c>1ex</c> as the x-height, both
/// in the element's <em>first available font</em>, so neither is a fixed multiple of the font
/// size the way <c>em</c> is. Carrying all three together is what lets one context travel to
/// every length reader.
/// <para>
/// The implicit conversion from a bare font size exists for the readers that genuinely have no
/// element font - a media-query breakpoint, an <c>@supports</c> length check, the grid-track
/// context - and gives them Liberation Sans' ratios, which is what the whole engine used before
/// the face was reachable.
/// </para>
/// </remarks>
public readonly record struct FontUnits(float EmPx, float ChPx, float ExPx)
{
    /// <summary>Liberation Sans' ratios at <paramref name="emPx"/>: the pre-font-metrics fallback.</summary>
    public static FontUnits FromEm(float emPx) =>
        new(emPx, emPx * Dimension.ChPerEm, emPx * Dimension.ExPerEm);

    public static implicit operator FontUnits(float emPx) => FromEm(emPx);

    /// <summary>
    /// Remember these sizes on the style, so a reader that runs after the computed-value pass -
    /// paint-time <c>transform: translate()</c> above all - can have the element's face without
    /// carrying a resolver.
    /// </summary>
    public void StoreOn(LayoutStyle style)
    {
        style.FontChPx = ChPx;
        style.FontExPx = ExPx;
    }

    /// <summary>
    /// The three unit sizes a style was measured with, or Liberation Sans' ratios when nothing
    /// measured it. <c>em</c> comes from <see cref="LayoutStyle.FontSize"/> rather than being
    /// stored a second time, so it cannot disagree with the font size in force.
    /// </summary>
    public static FontUnits ForStyle(LayoutStyle style)
    {
        float emPx = style.FontSize ?? 16f;
        return style.FontChPx > 0f && style.FontExPx > 0f
            ? new FontUnits(emPx, style.FontChPx, style.FontExPx)
            : FromEm(emPx);
    }
}

/// <summary>
/// Measures <see cref="FontUnits"/> on the face the text engine would select, memoized for one
/// layout pass.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION FROM RUST: <c>crates/obscura-render/src/style.rs</c> has no equivalent - it resolves
/// <c>ex</c> (and, in this port, <c>ch</c>) as a fixed fraction of the font size, so a page in
/// Archivo, Liberation Mono or any webfont gets Liberation Sans' numbers. Chromium reads the
/// selected face: measured over HTTP at <c>font-size: 100px</c>, <c>width: 10ch</c> is 572.98px in
/// Archivo, 556.14px in Liberation Sans and 600.09px in Liberation Mono. See "Known deviations" in
/// todo.md.
/// </para>
/// <para>
/// The <c>0</c> advance is read through HarfBuzz at the face's design em with the same canonical
/// axis tuple shaping uses, so a variable face answers for the weight the element actually renders
/// at: Archivo's digit is 0.573em at <c>wght</c> 400 and 0.6248em at 800, and Chromium's
/// <c>18ch</c> on a 69.12px <c>font-weight: 800</c> heading is 777.48px, not the 712.9px the base
/// instance would give.
/// </para>
/// <para>
/// Every element asks, so the answer is memoized on the font decision rather than on the element.
/// One page is typically two or three distinct faces at a handful of sizes.
/// </para>
/// </remarks>
public sealed class FontUnitResolver(TextEngine? engine)
{
    private readonly record struct Key(
        string? Family,
        ushort Weight,
        bool Italic,
        bool OpticalSizing,
        float EmPx,
        string? Variations);

    private readonly Dictionary<Key, FontUnits> _cache = [];
    private readonly Dictionary<(int FontId, string? Variations), float> _zeroAdvance = [];

    /// <summary>The three unit sizes for an element already carrying its computed font properties.</summary>
    public FontUnits For(LayoutStyle style, float emPx) => For(
        emPx,
        style.FontFamily,
        ComputedStyle.UsedFontWeight(style),
        style.FontStyleItalic ?? false,
        style.FontOpticalSizing ?? FontOpticalSizing.Auto,
        style.FontVariationSettings);

    /// <summary>
    /// The three unit sizes for a font decision. The font properties are passed in rather than
    /// read off the style because the top-down pass resolves every length before it inherits
    /// <c>font-family</c>, <c>font-weight</c> and <c>font-style</c> onto the element.
    /// </summary>
    public FontUnits For(
        float emPx,
        string? family,
        ushort weight,
        bool italic,
        FontOpticalSizing opticalSizing,
        IReadOnlyList<FontVariationSetting>? variationSettings)
    {
        if (engine is null || !float.IsFinite(emPx) || emPx <= 0f)
        {
            return FontUnits.FromEm(emPx);
        }

        string? variationKey = VariationKey(variationSettings);
        Key key = new(family, weight, italic, opticalSizing == FontOpticalSizing.Auto, emPx, variationKey);
        if (_cache.TryGetValue(key, out FontUnits cached))
        {
            return cached;
        }

        ResolvedFont font = FontResolution.ResolveLoadedFont(family, weight, italic, engine.LoadedFamilies);
        float ex = FontAssets.XHeight(emPx, font.Metrics);
        if (!(ex > 0f))
        {
            ex = emPx * Dimension.ExPerEm;
        }

        float ch = emPx * Dimension.ChPerEm;
        if (font.FontId is { } id && engine.Database.Face(id) is { } face)
        {
            float advance = ZeroAdvanceUnits(
                face,
                id,
                weight,
                italic || font.SyntheticItalic,
                opticalSizing == FontOpticalSizing.Auto ? emPx : null,
                variationSettings,
                variationKey);
            if (advance > 0f)
            {
                ch = advance / face.UnitsPerEm * emPx;
            }
        }

        FontUnits units = new(emPx, ch, ex);
        _cache[key] = units;
        return units;
    }

    /// <summary>The advance of U+0030 in design units, with the variable axes shaping would apply.</summary>
    private float ZeroAdvanceUnits(
        FaceRecord face,
        FontId id,
        ushort weight,
        bool italic,
        float? opticalSize,
        IReadOnlyList<FontVariationSetting>? variationSettings,
        string? variationKey)
    {
        string? axisKey = face.IsVariable
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{weight}/{(italic ? 1 : 0)}/{opticalSize ?? -1f}/{variationKey}")
            : variationKey;
        if (_zeroAdvance.TryGetValue((id.Value, axisKey), out float memoized))
        {
            return memoized;
        }

        float advance = 0f;
        try
        {
            HarfBuzzSharp.Font hb = face.HarfBuzzFontFor(
                AxisTuple(face, weight, italic, opticalSize, variationSettings));
            if (hb.TryGetNominalGlyph((uint)'0', out uint glyph) && glyph != 0)
            {
                advance = hb.GetHorizontalGlyphAdvance(glyph);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A face HarfBuzz cannot open leaves `ch` on the Liberation Sans fallback rather than
            // failing the whole layout; the same face would already have failed to shape.
            advance = 0f;
        }

        _zeroAdvance[(id.Value, axisKey)] = advance;
        return advance;
    }

    /// <summary>
    /// The canonical axis tuple for a face, mirroring <c>TextShaper.ShapingVariations</c>: the
    /// automatic <c>wght</c> and <c>opsz</c> axes, a synthesized italic, then the authored
    /// low-level settings last so they win.
    /// </summary>
    /// <remarks>
    /// This reproduces that rule rather than calling it, because calling it means building the
    /// inline layer's <c>TextAttrs</c> to measure a CSS unit. The two MUST agree - if they drift,
    /// <c>ch</c> on a variable face stops matching the advance the shaper uses for the very same
    /// glyph, and nothing fails. <c>FontRelativeUnitTests.TheMeasuringAxisTupleMatchesTheShapingAxisTuple</c>
    /// is what keeps them honest; it is not an optional test.
    /// </remarks>
    internal static FontVariations? AxisTuple(
        FaceRecord face,
        ushort weight,
        bool italic,
        float? opticalSize,
        IReadOnlyList<FontVariationSetting>? variationSettings)
    {
        bool authored = variationSettings is { Count: > 0 };
        if (!face.IsVariable)
        {
            return null;
        }

        FontVariations variations = new();
        variations.Set(VariationTag.FromAscii("wght"), weight);
        if (opticalSize is { } optical && float.IsFinite(optical))
        {
            variations.Set(VariationTag.FromAscii("opsz"), optical);
        }

        if (italic)
        {
            VariationTag italicAxis = VariationTag.FromAscii("ital");
            bool hasItalic = false;
            foreach (FontAxis axis in face.Axes)
            {
                if (axis.Tag == italicAxis)
                {
                    hasItalic = true;
                    break;
                }
            }

            variations.Set(hasItalic ? italicAxis : VariationTag.FromAscii("slnt"), hasItalic ? 1f : -14f);
        }

        if (authored)
        {
            foreach (FontVariationSetting setting in variationSettings!)
            {
                if (float.IsFinite(setting.Value) && setting.Tag.Length == 4)
                {
                    variations.Set(VariationTag.FromAscii(setting.Tag), setting.Value);
                }
            }
        }

        return variations.IsEmpty ? null : variations;
    }

    private static string? VariationKey(IReadOnlyList<FontVariationSetting>? settings)
    {
        if (settings is null || settings.Count == 0)
        {
            return null;
        }

        DefaultInterpolatedStringHandler handler = new(0, settings.Count * 2);
        foreach (FontVariationSetting setting in settings)
        {
            handler.AppendFormatted(setting.Tag);
            handler.AppendLiteral("=");
            handler.AppendFormatted(setting.Value);
            handler.AppendLiteral(";");
        }

        return handler.ToStringAndClear();
    }
}
