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
    /// stripped and was read as the bare number - <c>1ch</c> meant 1px. Approximated here by
    /// <see cref="Dimension.ChPerEm"/> rather than measured per face; see "Known deviations"
    /// in todo.md. Appended last so no persisted or interop ordinal moves.
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
    /// Resolve font/viewport-relative units to <see cref="DimensionKind.Px"/>. <paramref name="emPx"/>
    /// is the element's own font-size, <paramref name="remPx"/> the root's, and <paramref name="vw"/>
    /// / <paramref name="vh"/> are one hundredth of the viewport width/height. Px, Percent, and Auto
    /// pass through (Percent stays for taffy to resolve against the containing block).
    /// </summary>
    public Dimension Resolve(float emPx, float remPx, float vw, float vh) => Kind switch
    {
        DimensionKind.Em => Px(Value * emPx),
        // Liberation Sans is the deterministic generic sans face used by the renderer. This is
        // its x-height as a fraction of the em, matching Chromium's generic sans face on the
        // capture host.
        DimensionKind.Ex => Px(Value * emPx * ExPerEm),
        DimensionKind.Ch => Px(Value * emPx * ChPerEm),
        DimensionKind.Rem => Px(Value * remPx),
        DimensionKind.Vw => Px(Value * vw),
        DimensionKind.Vh => Px(Value * vh),
        DimensionKind.Vmin => Px(Value * F32.Min(vw, vh)),
        DimensionKind.Vmax => Px(Value * F32.Max(vw, vh)),
        _ => this,
    };

    /// <summary>Liberation Sans x-height as a fraction of the em (Rust <c>0.528_320_3</c>).</summary>
    public const float ExPerEm = 0.528_320_3f;

    /// <summary>
    /// Liberation Sans' advance for <c>0</c> as a fraction of the em, which is what CSS says
    /// <c>1ch</c> is.
    /// </summary>
    /// <remarks>
    /// 1139/2048 units, read out of <c>crates/obscura-render/assets/liberation-sans.ttf</c> -
    /// the deterministic generic sans face the renderer rasterizes with, chosen for the same
    /// reason <see cref="ExPerEm"/> is that face's x-height. It is a constant, not a per-face
    /// measurement: a page setting a monospace or serif family gets a <c>ch</c> a little wide
    /// or narrow (Liberation Mono is 0.6001 em, Liberation Serif 0.5). Chromium measures the
    /// real glyph. See "Known deviations" in todo.md.
    /// </remarks>
    public const float ChPerEm = 0.556_152_3f;
}
