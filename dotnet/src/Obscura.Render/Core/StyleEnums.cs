using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>
/// The display modes obscura-render cares about for phase 1. Inline text layout arrives with
/// the text/paint phase and is folded in then.
/// </summary>
public enum Display
{
    /// <summary>The default.</summary>
    Block = 0,

    Flex,
    Grid,
    Inline,
    None,
}

/// <summary>
/// Computed <c>container-type</c> value. Stage A carries it through the cascade; the layout
/// convergence stage will use it for query eligibility.
/// </summary>
public enum ContainerType
{
    /// <summary>The default.</summary>
    Normal = 0,

    InlineSize,
    Size,
}

/// <summary>Computed CSS <c>font-optical-sizing</c>.</summary>
public enum FontOpticalSizing
{
    /// <summary>Let a variable font's <c>opsz</c> axis follow the computed font size. The default.</summary>
    Auto = 0,

    /// <summary>Leave the font's optical-size axis at its normal/default setting.</summary>
    None,
}

/// <summary>One canonical CSS <c>font-variation-settings</c> axis assignment.</summary>
/// <remarks>
/// The CSS parser guarantees a printable four-byte ASCII tag and a finite value. Settings are
/// stored in tag order with duplicate tags collapsed so shaping and rasterization can consume
/// one deterministic axis tuple. Rust stores the tag as <c>[u8; 4]</c>; the port keeps the
/// four ASCII bytes as an ordinal-compared string.
/// </remarks>
public readonly record struct FontVariationSetting(string Tag, float Value);

/// <summary><c>float: left|right</c>.</summary>
/// <remarks>
/// True CSS float needs per-line reflow around the float's shape, which taffy's block/flex/grid
/// modes do not do; see the DOM float-zone grouping for the bounded approximation this drives.
/// </remarks>
public enum Float
{
    Left,
    Right,
}

/// <summary>Which box edge <c>width</c>/<c>height</c> and min/max sizes describe.</summary>
public enum BoxSizing
{
    /// <summary>The CSS initial value and the default.</summary>
    ContentBox = 0,

    BorderBox,

    /// <summary>
    /// A specified CSS-wide <c>inherit</c> value. The DOM's top-down computed-style pass
    /// resolves this to the parent's computed value before layout.
    /// </summary>
    Inherit,
}

/// <summary>One <c>box-shadow</c> layer.</summary>
/// <remarks>
/// Offsets, blur, and spread are in CSS px; <c>Color</c> is the resolved RGBA (falling back to
/// the element's text color, per CSS <c>currentColor</c>, when the value omits a color);
/// <c>Inset</c> distinguishes an inner shadow from the default outer (drop) shadow. Only the
/// first layer of a comma-separated list is modeled.
/// </remarks>
public readonly record struct BoxShadow(
    float OffsetX,
    float OffsetY,
    float Blur,
    float Spread,
    RgbaColor Color,
    bool Inset);

/// <summary><c>object-fit</c> for replaced elements.</summary>
/// <remarks>
/// How the image's intrinsic content is scaled into its box when their aspect ratios differ.
/// <c>Fill</c> (the default) stretches to the whole box; the others preserve the image's aspect
/// ratio, either letterboxing inside the box (<c>Contain</c>), cropping to cover it
/// (<c>Cover</c>), or using the intrinsic size (<c>None</c>, or <c>ScaleDown</c> which is
/// <c>Contain</c> capped at the intrinsic size so it never upscales).
/// </remarks>
public enum ObjectFit
{
    /// <summary>The default.</summary>
    Fill = 0,

    Contain,
    Cover,
    ScaleDown,
    None,
}

/// <summary><c>object-position</c> for replaced image content.</summary>
/// <remarks>
/// It shares the same length-percentage positioning model as backgrounds, but has a centered
/// initial value instead of <c>background-position</c>'s start-edge default. Because C# cannot
/// give a struct a non-zero parameterless default, callers must seed this from
/// <see cref="Default"/>; <c>default(ObjectPosition)</c> is <c>0% 0%</c> and is NOT the CSS
/// initial value.
/// </remarks>
public readonly record struct ObjectPosition(BackgroundPositionAxis X, BackgroundPositionAxis Y)
{
    /// <summary>The CSS initial value, <c>50% 50%</c>.</summary>
    public static readonly ObjectPosition Default = new(
        BackgroundPositionAxis.Percentage(0.5f),
        BackgroundPositionAxis.Percentage(0.5f));
}

/// <summary><c>vertical-align</c> positions for table-cell content.</summary>
/// <remarks>
/// <c>baseline</c> (and the text-level values like sub/super, which do not apply to cells) map
/// to <c>Top</c> as an approximation: real per-row baseline alignment needs shared ascent
/// metrics across the row.
/// </remarks>
public enum VerticalAlign
{
    Top,
    Middle,
    Bottom,
}

/// <summary><c>clear</c>: which floated side(s) an element moves below.</summary>
public enum Clear
{
    Left,
    Right,
    Both,
}

/// <summary>Which form a <see cref="LineHeight"/> carries.</summary>
public enum LineHeightKind : byte
{
    /// <summary>The font-relative <c>normal</c> keyword.</summary>
    Normal = 0,

    /// <summary>
    /// Unitless number. Unlike length/percentage values, this remains a ratio when inherited
    /// and therefore scales with each descendant's font size.
    /// </summary>
    Ratio,

    /// <summary>An absolute pixel length.</summary>
    Px,

    /// <summary>
    /// A specified length or percentage awaiting computed-value resolution. It becomes
    /// <see cref="Px"/> on the declaring element before inheritance.
    /// </summary>
    Relative,
}

/// <summary>
/// <c>line-height</c>: <c>normal</c> (a font-relative default), a unitless multiple of
/// font-size, or an absolute pixel length.
/// </summary>
public readonly record struct LineHeight(LineHeightKind Kind, float Number, Dimension Length)
{
    public static readonly LineHeight Normal = default;

    public static LineHeight Ratio(float value) => new(LineHeightKind.Ratio, value, Dimension.Auto);

    public static LineHeight Px(float value) => new(LineHeightKind.Px, value, Dimension.Auto);

    public static LineHeight Relative(Dimension value) => new(LineHeightKind.Relative, 0f, value);
}

/// <summary>
/// The legacy <c>white-space</c> shorthand values needed by inline collection and line
/// breaking. <c>BreakSpaces</c> currently shares pre-wrap's wrapping model; its finer trailing
/// space opportunity rules can be added independently.
/// </summary>
public enum WhiteSpace
{
    /// <summary>The default.</summary>
    Normal = 0,

    NoWrap,
    Pre,
    PreWrap,
    PreLine,
    BreakSpaces,
}

/// <summary>
/// Single-value CSS <c>text-overflow</c> behavior supported by Chromium's default feature set.
/// The marker is generated by the inline formatter and never inserted into DOM text.
/// </summary>
public enum TextOverflow
{
    /// <summary>The default.</summary>
    Clip = 0,

    Ellipsis,
}

/// <summary>Emergency wrapping behavior for otherwise-unbreakable text.</summary>
public enum OverflowWrap
{
    /// <summary>The default.</summary>
    Normal = 0,

    BreakWord,
    Anywhere,
}

/// <summary>
/// The supported <c>word-break</c> values. <c>BreakWord</c> is the legacy compatibility value
/// whose effective behavior is <c>word-break: normal</c> plus <c>overflow-wrap: anywhere</c>,
/// matching Blink and Gecko.
/// </summary>
public enum WordBreak
{
    /// <summary>The default.</summary>
    Normal = 0,

    BreakAll,
    KeepAll,
    BreakWord,
}

/// <summary>
/// The implemented <c>text-wrap-style</c> values. Other line-breaking strategies such as
/// <c>pretty</c> remain unsupported until their distinct scoring model is available; treating
/// them as <c>auto</c> would make <c>@supports</c> lie.
/// </summary>
public enum TextWrapStyle
{
    /// <summary>The default.</summary>
    Auto = 0,

    Balance,
}

/// <summary><c>text-transform</c>, applied to span text before shaping.</summary>
public enum TextTransform
{
    None,
    Uppercase,
    Lowercase,
    Capitalize,
}

/// <summary>
/// <c>list-style-type</c> values the renderer draws a marker for. <c>Decimal</c> numbers the
/// item by its position among sibling list items.
/// </summary>
public enum ListStyle
{
    None,
    Disc,
    Circle,
    Square,
    Decimal,
}
