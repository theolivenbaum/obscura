namespace Obscura.Render;

/// <summary>Which CSS unit a <see cref="Dimension"/> carries.</summary>
public enum DimensionKind : byte
{
    /// <summary>The CSS <c>auto</c> keyword. This is the default.</summary>
    Auto = 0,

    Px,

    /// <summary>0.0-1.0 fraction of the containing block.</summary>
    Percent,

    Em,
    Ex,
    Rem,
    Vw,
    Vh,
    Vmin,
    Vmax,

    /// <summary>
    /// <c>ch</c>: the advance of the <c>0</c> glyph in the element's font.
    /// </summary>
    /// <remarks>
    /// DEVIATION FROM RUST: <c>crates/obscura-render</c> has no <c>ch</c> unit at all, so
    /// <c>1ch</c> either fell through to <c>auto</c> or, inside <c>px_value</c>, had its unit
    /// stripped and was read as the bare number - <c>1ch</c> meant 1px. Resolved here from the
    /// selected face's own advance for <c>0</c> (<see cref="FontUnits.ChPx"/>); see "Known
    /// deviations" in todo.md. Appended last so no persisted or interop ordinal moves.
    /// </remarks>
    Ch,
}

/// <summary>
/// A CSS length keyword, absolute length, percentage, or unresolved relative unit.
/// </summary>
/// <remarks>
/// Font-relative and viewport-relative units are kept unresolved at parse time (the element
/// font-size and viewport are not known then) and resolved to <see cref="DimensionKind.Px"/>
/// during the DOM layout top-down pass via <see cref="Resolve"/>. Resolving font/viewport
/// units against a hardcoded 16px at parse time silently corrupts every relative length.
/// <para>
/// <c>default(Dimension)</c> is <see cref="Auto"/>, matching Rust's <c>#[default] Auto</c>.
/// </para>
/// </remarks>
public readonly record struct Dimension(DimensionKind Kind, float Value)
{
    /// <summary>The CSS <c>auto</c> keyword; also <c>default(Dimension)</c>.</summary>
    public static readonly Dimension Auto = default;

    public static Dimension Px(float value) => new(DimensionKind.Px, value);

    /// <summary>A percentage as a 0.0-1.0 fraction of the containing block.</summary>
    public static Dimension Percent(float fraction) => new(DimensionKind.Percent, fraction);

    public static Dimension Em(float value) => new(DimensionKind.Em, value);

    public static Dimension Ex(float value) => new(DimensionKind.Ex, value);

    /// <summary>A <c>ch</c> length: <paramref name="value"/> advances of the <c>0</c> glyph.</summary>
    public static Dimension Ch(float value) => new(DimensionKind.Ch, value);

    public static Dimension Rem(float value) => new(DimensionKind.Rem, value);

    public static Dimension Vw(float value) => new(DimensionKind.Vw, value);

    public static Dimension Vh(float value) => new(DimensionKind.Vh, value);

    public static Dimension Vmin(float value) => new(DimensionKind.Vmin, value);

    public static Dimension Vmax(float value) => new(DimensionKind.Vmax, value);

    public bool IsAuto => Kind == DimensionKind.Auto;

    /// <summary>The pixel value, or <c>null</c> when this is not an absolute length.</summary>
    public float? AsPx => Kind == DimensionKind.Px ? Value : null;

    /// <summary>The 0.0-1.0 fraction, or <c>null</c> when this is not a percentage.</summary>
    public float? AsPercent => Kind == DimensionKind.Percent ? Value : null;

    /// <summary>
    /// Resolve font/viewport-relative units to <see cref="DimensionKind.Px"/>. <paramref name="font"/>
    /// carries the element's own <c>em</c>, <c>ch</c> and <c>ex</c> sizes, <paramref name="remPx"/>
    /// is the root font-size, and <paramref name="vw"/> / <paramref name="vh"/> are one hundredth of
    /// the viewport width/height. Px, Percent, and Auto pass through (Percent stays for taffy to
    /// resolve against the containing block).
    /// <para>
    /// A caller with no font in hand can pass a bare font size: <see cref="FontUnits"/> converts
    /// implicitly and falls back to Liberation Sans' ratios, which is what every site here did
    /// before the element's face was reachable.
    /// </para>
    /// </summary>
    public Dimension Resolve(FontUnits font, float remPx, float vw, float vh) => Kind switch
    {
        DimensionKind.Em => Px(Value * font.EmPx),
        DimensionKind.Ex => Px(Value * font.ExPx),
        DimensionKind.Ch => Px(Value * font.ChPx),
        DimensionKind.Rem => Px(Value * remPx),
        DimensionKind.Vw => Px(Value * vw),
        DimensionKind.Vh => Px(Value * vh),
        DimensionKind.Vmin => Px(Value * F32.Min(vw, vh)),
        DimensionKind.Vmax => Px(Value * F32.Max(vw, vh)),
        _ => this,
    };

    /// <summary>
    /// Liberation Sans' x-height as a fraction of the em, used only where no face is in hand.
    /// </summary>
    /// <remarks>
    /// DEVIATION FROM RUST: <c>crates/obscura-render/src/style.rs</c> multiplies the font size by
    /// this fraction for <em>every</em> element, so <c>ex</c> is Liberation Sans' x-height whatever
    /// the page's font is. CSS Values 4 says <c>1ex</c> is the x-height of the element's own first
    /// available font, which is what <see cref="FontUnits.ExPx"/> now carries; this constant is the
    /// fallback for the readers that run before a font is selected (media queries,
    /// <c>@supports</c> validation, the grid-track context). See "Known deviations" in todo.md.
    /// </remarks>
    public const float ExPerEm = 0.528_320_3f;

    /// <summary>
    /// Liberation Sans' advance for <c>0</c> as a fraction of the em, used only where no face is
    /// in hand.
    /// </summary>
    /// <remarks>
    /// 1139/2048 units, read out of <c>Assets/liberation-sans.ttf</c>.
    /// <para>
    /// DEVIATION FROM RUST: <c>crates/obscura-render</c> has no <c>ch</c> unit at all, and this
    /// port used to apply this one constant to every element, so a monospace or webfont page got
    /// Liberation Sans' <c>ch</c>. Measured on Chromium 141 over HTTP at <c>font-size: 100px</c>,
    /// <c>width: 10ch</c> is 572.98px in Archivo, 556.14px in Liberation Sans and 600.09px in
    /// Liberation Mono, where this engine answered 556 for all three. <see cref="FontUnits.ChPx"/>
    /// now carries the selected face's real advance. See "Known deviations" in todo.md.
    /// </para>
    /// </remarks>
    public const float ChPerEm = 0.556_152_3f;
}
