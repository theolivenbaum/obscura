using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>Intrinsic metadata exposed by a decoded replaced resource.</summary>
/// <remarks>
/// Presence of this value means metadata is available; each dimension may still be absent
/// independently. This is required for SVG, whose root can declare one dimension, both
/// dimensions, or only a <c>viewBox</c> ratio.
/// </remarks>
internal readonly record struct ReplacedIntrinsic(float? Width, float? Height, float? Ratio)
{
    public static ReplacedIntrinsic FromDimensions(float width, float height) => new(
        width,
        height,
        float.IsFinite(width) && float.IsFinite(height) && width > 0f && height > 0f
            ? width / height
            : null);

    /// <summary>
    /// Resolve the concrete natural size from CSS Images' 300x150 default object size. A
    /// definite authored layout axis still overrides this fallback and transfers through the
    /// intrinsic ratio.
    /// </summary>
    public (float Width, float Height)? NaturalSize()
    {
        float? width = Positive(Width);
        float? height = Positive(Height);
        float? ratio = Positive(Ratio);
        return (width, height, ratio) switch
        {
            ({ } w, { } h, _) => (w, h),
            ({ } w, null, { } r) => (w, w / r),
            (null, { } h, { } r) => (h * r, h),
            ({ } w, null, null) => (w, 150f),
            (null, { } h, null) => (300f, h),
            (null, null, { } r) when r >= 2f => (300f, 300f / r),
            (null, null, { } r) => (150f * r, 150f),
            _ => (300f, 150f),
        };

        static float? Positive(float? value) =>
            value is { } v && float.IsFinite(v) && v > 0f ? v : null;
    }
}

/// <summary>
/// Sparse retained provenance for direct transform-only WAAPI sampling.
/// </summary>
/// <remarks>
/// Kept behind one reference so ordinary, non-animated elements do not pay for another list in
/// every <see cref="LayoutStyle"/>.
/// </remarks>
internal sealed class WaapiSampleState
{
    public List<TransformOp> UnderlyingTransformOps = [];
    public float? UnderlyingOpacity;
    public bool TransformFastPath;
    public bool OpacityFastPath;

    public WaapiSampleState Clone() => new()
    {
        UnderlyingTransformOps = [.. UnderlyingTransformOps],
        UnderlyingOpacity = UnderlyingOpacity,
        TransformFastPath = TransformFastPath,
        OpacityFastPath = OpacityFastPath,
    };
}

/// <summary>
/// An authored internal-table <c>display</c>: one of the six table-internal values plus
/// <c>table-caption</c>, none of which this engine lays out.
/// </summary>
/// <remarks>
/// <c>ApplyDisplay</c> records the keyword and leaves the box's layout style untouched, so
/// the CSSOM snapshot can report the computed value while layout keeps treating the element
/// as whatever it was. See <see cref="LayoutStyle.AuthoredTableDisplay"/>.
/// </remarks>
internal enum TableInternalDisplay : byte
{
    /// <summary>No internal-table display was authored.</summary>
    None,

    /// <summary><c>display: table-row</c>.</summary>
    Row,

    /// <summary><c>display: table-row-group</c>.</summary>
    RowGroup,

    /// <summary><c>display: table-header-group</c>.</summary>
    HeaderGroup,

    /// <summary><c>display: table-footer-group</c>.</summary>
    FooterGroup,

    /// <summary><c>display: table-column</c>.</summary>
    Column,

    /// <summary><c>display: table-column-group</c>.</summary>
    ColumnGroup,

    /// <summary><c>display: table-caption</c>.</summary>
    Caption,
}

internal enum BorderCascadeSide
{
    Top,
    Right,
    Bottom,
    Left,
    Inline,
    InlineStart,
    InlineEnd,
    Block,
    BlockStart,
    BlockEnd,
    All,
}

/// <summary>One replayable border declaration recorded during the cascade.</summary>
/// <remarks>
/// An absent <c>Color</c> means this operation leaves color unchanged; a present
/// <c>Color</c> holding <c>null</c> is the valid computed <c>currentcolor</c> value. C# has no
/// <c>Option&lt;Option&lt;T&gt;&gt;</c>, so the outer level is modeled by
/// <see cref="ColorSet"/>.
/// </remarks>
internal readonly record struct BorderCascadeOp(
    BorderCascadeSide Side,
    float? Width,
    BorderStyle? Style,
    bool ColorSet,
    RgbaColor? Color);

/// <summary>Which <c>filter</c> function a <see cref="FilterFunction"/> carries.</summary>
/// <remarks>
/// DEVIATION FROM RUST: <c>crates/obscura-render</c> models <c>filter</c> as a single optional
/// blur sigma, so every other function is dropped at parse time and the property reports as
/// absent. The port models the whole list, because Chromium reports it through
/// <c>getComputedStyle</c> and page script compares the string.
/// </remarks>
public enum FilterFunctionKind
{
    /// <summary><c>blur(&lt;length&gt;)</c>; the argument is sigma itself.</summary>
    Blur,

    /// <summary><c>brightness(&lt;number|percentage&gt;)</c>, unbounded above.</summary>
    Brightness,

    /// <summary><c>contrast(&lt;number|percentage&gt;)</c>, unbounded above.</summary>
    Contrast,

    /// <summary><c>drop-shadow(&lt;color&gt;? &lt;x&gt; &lt;y&gt; &lt;blur&gt;?)</c>.</summary>
    DropShadow,

    /// <summary><c>grayscale(&lt;number|percentage&gt;)</c>, clamped to 1.</summary>
    Grayscale,

    /// <summary><c>hue-rotate(&lt;angle&gt;)</c>, in degrees.</summary>
    HueRotate,

    /// <summary><c>invert(&lt;number|percentage&gt;)</c>, clamped to 1.</summary>
    Invert,

    /// <summary><c>opacity(&lt;number|percentage&gt;)</c>, clamped to 1.</summary>
    Opacity,

    /// <summary><c>saturate(&lt;number|percentage&gt;)</c>, unbounded above.</summary>
    Saturate,

    /// <summary><c>sepia(&lt;number|percentage&gt;)</c>, clamped to 1.</summary>
    Sepia,

    /// <summary>
    /// <c>url(&lt;reference&gt;)</c>, an SVG filter element. Parsed and reported; never painted.
    /// </summary>
    Reference,
}

/// <summary>One function of a computed <c>filter</c> list.</summary>
/// <remarks>
/// A single struct rather than a hierarchy: the list is walked once per filtered element in the
/// paint pass, and a flat value type keeps that walk allocation-free. Only the fields its
/// <see cref="Kind"/> names are meaningful.
/// <para>
/// <see cref="Amount"/> carries the single argument of every one-argument function - sigma for
/// <c>blur()</c>, degrees for <c>hue-rotate()</c>, and the multiplier for the colour-matrix
/// functions, always as a number even when the value was authored as a percentage. The
/// <c>drop-shadow()</c> fields are the two offsets, the blur <em>radius</em> (twice sigma, the
/// <c>box-shadow</c> convention, unlike <c>blur()</c>), and the resolved colour with
/// <c>currentColor</c> already substituted.
/// </para>
/// </remarks>
public readonly record struct FilterFunction(
    FilterFunctionKind Kind,
    float              Amount,
    float              OffsetX,
    float              OffsetY,
    float              ShadowBlur,
    RgbaColor          Color,
    string?            Reference)
{
    /// <summary>A one-argument function: <c>blur()</c>, <c>hue-rotate()</c>, or a matrix.</summary>
    public static FilterFunction Scalar(FilterFunctionKind kind, float amount) =>
        new(kind, amount, 0f, 0f, 0f, default, null);

    /// <summary><c>drop-shadow()</c>, with <c>currentColor</c> already resolved.</summary>
    public static FilterFunction DropShadowOf(float offsetX, float offsetY, float blur, RgbaColor color) =>
        new(FilterFunctionKind.DropShadow, 0f, offsetX, offsetY, blur, color, null);

    /// <summary><c>url()</c>, kept verbatim so the computed value round-trips.</summary>
    public static FilterFunction ReferenceTo(string reference) =>
        new(FilterFunctionKind.Reference, 0f, 0f, 0f, 0f, default, reference);
}

/// <summary>The subset of CSS that influences box layout. Expanded in later phases.</summary>
/// <remarks>
/// Allocated once per element, so this is a class rather than a struct. Every field initializer
/// reproduces the Rust <c>Default</c> exactly; several nested value types (
/// <see cref="Obscura.Render.BorderModel"/>, <see cref="OutlineModel"/>, <see cref="ObjectPosition"/>,
/// <see cref="AnimationTiming"/>) have non-zero Rust defaults and must be seeded from their
/// <c>Default</c> static rather than left as <c>default</c>.
/// </remarks>
/// <summary>
/// The parts of a computed style that almost no element sets, held apart from
/// <see cref="LayoutStyle"/> and allocated on the first write of any of them.
/// </summary>
/// <remarks>
/// DEVIATION FROM RUST: crates/obscura-render keeps every one of these inline in its style
/// struct. They are ~600 bytes of the C# object, and measured over the layout fixtures fewer
/// than one element in a thousand sets any of them, so on a 60k-element page they were ~34 MB
/// of live heap holding nothing but defaults. Splitting them out is a memory layout decision
/// only: every member is still reached through a property of the same name on
/// <see cref="LayoutStyle"/>, with the same value it had before, and a write of the default
/// value does not allocate this object. See "Known deviations" in todo.md.
/// </remarks>
internal sealed class LayoutStyleRare
{
    public BorderModel? BorderCascadeBase;

    public Layout.Line<Layout.GridPlacement>? GridColumn;

    public Layout.Line<Layout.GridPlacement>? GridRow;

    public (float Angle, (float X, float Y) Center, List<GradientStop> Stops)? BackgroundConicGradient;

    public ReplacedIntrinsic? ReplacedIntrinsic;

    public RadialGradientGeometry? BackgroundRadialGradientGeometry;

    public BoxShadow? BoxShadow;

    public (float Angle, List<GradientStop> Stops)? BackgroundGradient;

    public ((float X, float Y) Center, List<GradientStop> Stops)? BackgroundRadialGradient;

    public AnimationTiming AnimationTiming = Obscura.Render.AnimationTiming.Default;

    public (Dimension X, Dimension Y)? IndividualTranslate;

    public (Dimension X, Dimension Y)? TransformOrigin;

    public OutlineModel Outline = OutlineModel.Default;

    public (float Width, float Height)? IntrinsicSize;

    public (float Width, float Height)? NativeControlContent;

    public (float Width, float Height)? BackgroundSize;

    public (float Width, float Height)? MaskSize;

    public (float Horizontal, float Vertical)? BorderSpacing;

    public string? BorderSpacingFontRelative;

    public bool BorderSpacingInherit;

    public Edges? CollapsedBorder;

    public bool? CaptionSideBottom;

    public ObjectPosition ObjectPosition = Obscura.Render.ObjectPosition.Default;

    public (float X, float Y)? IndividualScale;

    public BackgroundPosition BackgroundPosition = default;

    public Dimension? FontSizeRaw;

    public Dimension? LetterSpacingRaw;

    /// <summary>A shallow copy; <see cref="LayoutStyle.Clone"/> deep-copies what needs it.</summary>
    public LayoutStyleRare Clone() => (LayoutStyleRare)MemberwiseClone();
}

public sealed class LayoutStyle
{
    /// <summary>Rarely-set members, absent until one of them is written.</summary>
    private LayoutStyleRare? m_rare;

    /// <summary>The rare-member store, allocated on first write.</summary>
    private LayoutStyleRare Rare => m_rare ??= new LayoutStyleRare();

    // Lazily allocated per-side/per-slot state.
    //
    // DEVIATION FROM RUST: crates/obscura-render stores these inline in the style struct
    // ([Option<Dimension>; 4], SmallVec, ...), which costs nothing when unused. A C# array or
    // List is a separate heap object, and on a 60k-element page 23 always-allocated empty ones
    // per computed style were ~58 MB of live heap that no element read anything out of. The
    // fields below hold null until something is actually written; reads of the unwritten state
    // come from one shared all-default array, which is why the read accessors are
    // ReadOnlySpan/IReadOnlyList - the shared instance must never be written through.
    // See "Known deviations" in todo.md.
    private static readonly bool[] s_noBools4 = new bool[4];
    private static readonly float?[] s_noFloats4 = new float?[4];
    private static readonly Dimension?[] s_noDimensions4 = new Dimension?[4];
    private static readonly string?[] s_noStrings2 = new string?[2];
    private static readonly string?[] s_noStrings4 = new string?[4];
    private static readonly string?[] s_noStrings6 = new string?[6];

    /// <summary>
    /// Write one slot of a lazily allocated fixed-length array, leaving it unallocated while
    /// every slot would still hold the default.
    /// </summary>
    private static void SetSlot<T>(ref T[]? slots, int length, int index, T value)
    {
        if (slots is null)
        {
            if (EqualityComparer<T>.Default.Equals(value, default!))
            {
                return;
            }

            slots = new T[length];
        }

        slots[index] = value;
    }

    public Display Display;

    /// <summary>
    /// Computed inline base direction. <c>null</c> is the inherited specified state before the
    /// DOM top-down pass resolves it.
    /// </summary>
    public Layout.Direction? Direction;

    /// <summary>The specified <c>display</c> value was the CSS-wide <c>inherit</c> keyword.</summary>
    /// <remarks>
    /// <c>display</c> is normally non-inherited, so the cascade cannot resolve this until the
    /// parent's computed outer/inner display is known. The DOM top-down pass copies that
    /// provenance and then clears this marker.
    /// </remarks>
    internal bool DisplayInherit;

    /// <summary>The cascade's winning <c>display</c> came from a declaration, not the UA arm.</summary>
    /// <remarks>
    /// <c>&lt;caption&gt;</c>, <c>&lt;col&gt;</c> and <c>&lt;colgroup&gt;</c> have no user-agent
    /// arm in <see cref="ComputedStyle.UaStyle"/> - they get the plain <c>display: block</c>
    /// every element starts from - so their computed CSS display is only reconstructible from
    /// whether a declaration replaced it. Chromium 141 reports <c>table-caption</c> /
    /// <c>table-column</c> / <c>table-column-group</c> for the three untouched and the authored
    /// value otherwise, including <c>block</c> and <c>contents</c>.
    /// <para>
    /// Only <see cref="PreparedRender"/> reads this; nothing in layout does.
    /// </para>
    /// </remarks>
    internal bool DisplayAuthored;

    /// <summary>
    /// The authored internal-table <c>display</c>, which this engine records and reports but
    /// does not lay out.
    /// </summary>
    /// <remarks>
    /// Taffy has no table formatting mode and this engine's table builder is keyed on the HTML
    /// element names (<c>tr</c>, <c>tbody</c>, <c>col</c>, <c>caption</c>, ...) rather than on
    /// the computed display, so a <c>display: table-row</c> box is laid out as whatever it was
    /// before the declaration. CSSOM defines the computed value as the resolved value rather
    /// than the used one, and Chromium reports it whatever layout does with the box, so the
    /// snapshot reports the authored keyword and the gap is layout's.
    /// <para>
    /// Only <see cref="PreparedRender"/> reads this; nothing in layout does. Adding a layout
    /// consumer means implementing CSS 2.1 17.2.1 anonymous table boxes for these values
    /// first - see the remarks on <c>PreparedRender.TableDisplay</c>.
    /// </para>
    /// <para>
    /// Not carried across <c>display: inherit</c>: the DOM top-down pass copies the parent's
    /// computed outer/inner display through <c>LayoutDomComputed</c>'s inherited context, which
    /// does not carry this keyword, so a <c>display: inherit</c> child of a
    /// <c>display: table-row</c> box reports <c>block</c> where Chromium 141 reports
    /// <c>table-row</c>.
    /// </para>
    /// </remarks>
    internal TableInternalDisplay AuthoredTableDisplay;

    /// <summary>Original legacy flexbox display provenance.</summary>
    /// <remarks>
    /// <c>false</c> is <c>-webkit-box</c>; <c>true</c> is <c>-webkit-inline-box</c>. A vertical
    /// legacy box with an active line clamp computes to flow-root/inline-block, but retaining
    /// the specified display makes that adjustment independent of declaration order and keeps
    /// unclamped CSSOM serialization honest.
    /// </remarks>
    internal bool? WebkitBoxDisplay;

    /// <summary>
    /// Computed legacy box orientation. Only the vertical/block-axis form activates the legacy
    /// WebKit line-clamp contract.
    /// </summary>
    internal bool WebkitBoxOrientVertical;

    /// <summary>Computed CSS <c>container-type</c> (not inherited).</summary>
    public ContainerType ContainerType;

    /// <summary>
    /// The specified value was the CSS-wide <c>inherit</c> keyword. Resolved top-down before
    /// layout because <c>container-type</c> is otherwise non-inherited.
    /// </summary>
    internal bool ContainerTypeInherit;

    /// <summary>Computed CSS <c>container-name</c>; empty represents <c>none</c> (not inherited).</summary>
    public IReadOnlyList<string> ContainerNames
    {
        get => m_containerNames ?? (IReadOnlyList<string>)Array.Empty<string>();
        set => m_containerNames = value as List<string> ?? [.. value];
    }

    /// <summary>The backing list of <see cref="ContainerNames"/>, allocated on first call.</summary>
    public List<string> EnsureContainerNames() => m_containerNames ??= [];

    /// <summary>Reset <see cref="ContainerNames"/> to empty, releasing its list.</summary>
    public void ClearContainerNames() => m_containerNames = null;

    private List<string>? m_containerNames;

    /// <summary>The specified <c>container-name</c> value was CSS-wide <c>inherit</c>.</summary>
    internal bool ContainerNamesInherit;

    /// <summary>
    /// True when <c>display:flex</c> is only an internal stand-in for native HTML layout such
    /// as table cells, rather than the computed CSS display. Descendants are not CSS flex items
    /// in these containers.
    /// </summary>
    public bool InternalFlexContainer;

    /// <summary>The computed inner display is <c>table</c>.</summary>
    /// <remarks>
    /// Taffy has no table display mode, so authored table boxes are represented by
    /// block/grid-compatible layout styles. Keep this provenance for CSS features such as
    /// container-query axis availability that depend on the computed display rather than our
    /// internal layout approximation.
    /// </remarks>
    internal bool IsTableBox;

    /// <summary>
    /// This box came out of the <c>button</c> or <c>input</c> user-agent arm, so it is a form
    /// control Chromium hands to its native theme painter rather than painting the CSS border
    /// of.
    /// </summary>
    /// <remarks>
    /// DEVIATION FROM RUST: <c>crates/obscura-render</c> has no counterpart, because its
    /// <c>button</c> arm sets no border at all and its <c>input</c> arm paints the one it sets.
    /// Both controls compute a 2px relief border that Chromium never draws - it strokes a flat
    /// 1px rgb(118, 118, 118) line instead - so the computed value and the painted one are two
    /// different things, and this flag is what lets the port report the first and draw the
    /// second. <see cref="PaintBorders"/> honours it only while the border is still the
    /// untouched user-agent one, because an author border makes Chromium drop the native
    /// appearance too. See "Known deviations" in todo.md.
    /// </remarks>
    internal bool NativeControlAppearance;

    /// <summary>
    /// The computed <c>table-layout: fixed</c> value. The fixed algorithm is only activated
    /// when the table also has a definite inline size; otherwise CSS requires the automatic
    /// table layout algorithm.
    /// </summary>
    internal bool TableLayoutFixed;

    /// <summary>The computed inner display is <c>table-cell</c>.</summary>
    /// <remarks>
    /// Authored internal table boxes need the same cell sizing path as native
    /// <c>&lt;td&gt;</c>/<c>&lt;th&gt;</c> elements even though Taffy represents their
    /// cell-content wrapper as an internal column flexbox.
    /// </remarks>
    internal bool IsTableCellBox;

    /// <summary>
    /// The HTML UA sheet's vendor <c>text-align</c> behavior for <c>&lt;center&gt;</c>. Unlike
    /// ordinary <c>text-align:center</c>, it also centers fixed-width block descendants while
    /// leaving auto-width blocks fill-available.
    /// </summary>
    public bool LegacyCenter;

    public Dimension Width;

    /// <summary>
    /// Which intrinsic sizing keyword the preferred inline size carries, if any.
    /// </summary>
    /// <remarks>
    /// Taffy's box-size dimension cannot represent intrinsic sizing keywords, so <c>Width</c>
    /// remains <c>Auto</c> while the DOM layout convergence pass resolves the keyword from
    /// min/max-content measurements: <c>fit-content</c> applies the shrink-to-fit formula,
    /// <c>max-content</c> and <c>min-content</c> take the measurement directly.
    /// DEVIATION: crates/obscura-render implements none of the three (its <c>width</c> parse
    /// drops every keyword to <c>auto</c>), so all of them fill the containing block there.
    /// See "Known deviations" in todo.md.
    /// </remarks>
    public IntrinsicSizeKeyword WidthIntrinsicKeyword;

    /// <summary>The preferred inline size is one of the intrinsic sizing keywords.</summary>
    public bool WidthFitContent => WidthIntrinsicKeyword != IntrinsicSizeKeyword.None;

    public Dimension Height;

    /// <summary>
    /// Which intrinsic sizing keyword the preferred block size carries, if any.
    /// </summary>
    /// <remarks>
    /// In the block axis all three keywords size to content exactly like <c>auto</c>, so
    /// <c>Height</c> stays <c>Auto</c>. The one observable difference is that none of them is
    /// an automatic size, so a flex or grid item carrying one is never stretched to fill its
    /// line or row.
    /// DEVIATION: crates/obscura-render implements none of them (it has no counterpart of
    /// this field), so the keywords there leave the box free to stretch. See "Known
    /// deviations" in todo.md.
    /// </remarks>
    public IntrinsicSizeKeyword HeightIntrinsicKeyword;

    /// <summary>The preferred block size is one of the intrinsic sizing keywords.</summary>
    public bool HeightFitContent => HeightIntrinsicKeyword != IntrinsicSizeKeyword.None;

    /// <summary>
    /// Which intrinsic sizing keyword <c>min-width</c> carries, if any.
    /// </summary>
    /// <remarks>
    /// Like the preferred sizes above, the dimension stays at its initial value and the
    /// keyword is resolved from a measurement during layout convergence
    /// (<c>DomPasses.ApplyIntrinsicInlineSizes</c>).
    /// DEVIATION: crates/obscura-render implements none of the intrinsic sizing keywords on
    /// the min/max properties either. See "Known deviations" in todo.md.
    /// </remarks>
    public IntrinsicSizeKeyword MinWidthIntrinsicKeyword;

    /// <summary>Which intrinsic sizing keyword <c>min-height</c> carries, if any.</summary>
    public IntrinsicSizeKeyword MinHeightIntrinsicKeyword;

    /// <summary>Which intrinsic sizing keyword <c>max-width</c> carries, if any.</summary>
    public IntrinsicSizeKeyword MaxWidthIntrinsicKeyword;

    /// <summary>Which intrinsic sizing keyword <c>max-height</c> carries, if any.</summary>
    public IntrinsicSizeKeyword MaxHeightIntrinsicKeyword;

    /// <summary>Any inline-axis size property carries an intrinsic sizing keyword.</summary>
    public bool HasInlineIntrinsicKeyword =>
        WidthIntrinsicKeyword != IntrinsicSizeKeyword.None
        || MinWidthIntrinsicKeyword != IntrinsicSizeKeyword.None
        || MaxWidthIntrinsicKeyword != IntrinsicSizeKeyword.None;

    /// <summary>A min/max block-axis size property carries an intrinsic sizing keyword.</summary>
    /// <remarks>
    /// <c>height</c> itself is excluded: all three keywords size a block box to its content
    /// there, which is what <c>auto</c> already does, so it needs no measurement.
    /// </remarks>
    public bool HasBlockMinMaxIntrinsicKeyword =>
        MinHeightIntrinsicKeyword != IntrinsicSizeKeyword.None
        || MaxHeightIntrinsicKeyword != IntrinsicSizeKeyword.None;

    /// <summary>
    /// Read the intrinsic sizing keyword of one of the six box-size slots, in the order
    /// <c>width</c>, <c>height</c>, <c>min-width</c>, <c>min-height</c>, <c>max-width</c>,
    /// <c>max-height</c> that <see cref="SizeExpressions"/> and <see cref="SizeInherit"/> use.
    /// </summary>
    internal IntrinsicSizeKeyword SizeIntrinsicKeyword(int index) => index switch
    {
        0 => WidthIntrinsicKeyword,
        1 => HeightIntrinsicKeyword,
        2 => MinWidthIntrinsicKeyword,
        3 => MinHeightIntrinsicKeyword,
        4 => MaxWidthIntrinsicKeyword,
        _ => MaxHeightIntrinsicKeyword,
    };

    /// <summary>Write the intrinsic sizing keyword of one of the six box-size slots.</summary>
    internal void SetSizeIntrinsicKeyword(int index, IntrinsicSizeKeyword keyword)
    {
        switch (index)
        {
            case 0: WidthIntrinsicKeyword = keyword; break;
            case 1: HeightIntrinsicKeyword = keyword; break;
            case 2: MinWidthIntrinsicKeyword = keyword; break;
            case 3: MinHeightIntrinsicKeyword = keyword; break;
            case 4: MaxWidthIntrinsicKeyword = keyword; break;
            default: MaxHeightIntrinsicKeyword = keyword; break;
        }
    }

    /// <summary>
    /// Which box edge <c>width</c>/<c>height</c> and min/max sizes describe. CSS starts at
    /// <c>content-box</c>; many modern reset sheets opt into <c>border-box</c>.
    /// </summary>
    public BoxSizing BoxSizing;

    /// <summary>
    /// Whether <c>width</c>/<c>height</c> was set by an author rule (including an explicit
    /// <c>auto</c>).
    /// </summary>
    /// <remarks>
    /// Presentational <c>width</c>/<c>height</c> HTML attributes are a lower priority than
    /// author CSS, so they apply only when these are false; an explicit <c>width:auto</c> must
    /// still suppress a <c>width="408"</c> attribute so the element keeps its aspect-ratio size
    /// instead of the intrinsic one.
    /// </remarks>
    public bool WidthSet;

    public bool HeightSet;

    public Dimension MinWidth;

    public Dimension MinHeight;

    public Dimension MaxWidth;

    public Dimension MaxHeight;

    /// <summary>
    /// Deferred CSS math expressions for width, height, min-width, min-height, max-width, and
    /// max-height, in that order.
    /// </summary>
    /// <remarks>
    /// Functional lengths can depend on the actual viewport, containing block, or computed font
    /// size and cannot be safely collapsed to pixels during stylesheet parsing.
    /// </remarks>
    public ReadOnlySpan<string?> SizeExpressions => m_sizeExpressions ?? s_noStrings6;

    /// <summary>Write one slot of <see cref="SizeExpressions"/>, allocating only for a non-default value.</summary>
    public void SetSizeExpression(int index, string? value) => SetSlot(ref m_sizeExpressions, 6, index, value);

    /// <summary>Reset <see cref="SizeExpressions"/> to all-default, releasing its array.</summary>
    public void ClearSizeExpressions() => m_sizeExpressions = null;

    private string?[]? m_sizeExpressions;

    /// <summary>
    /// Bitmask over the six <see cref="SizeExpressions"/> slots marking the ones declared as
    /// the CSS-wide keyword <c>inherit</c>, to be copied from the parent's computed value in
    /// the top-down pass.
    /// </summary>
    /// <remarks>
    /// DEVIATION: no counterpart in crates/obscura-render, which drops <c>inherit</c> on the
    /// box-size properties entirely. They are not inherited properties, so the keyword has to
    /// copy the parent's computed value explicitly. Tesserae's annotated text editor sizes its
    /// textarea with <c>min-height: inherit</c>. See "Known deviations" in todo.md.
    /// </remarks>
    public byte SizeInherit;

    /// <summary>
    /// Bitmask over the inline-size slots (<c>0</c> width, <c>2</c> min-width, <c>4</c>
    /// max-width) whose declared value is a cyclic percentage that
    /// <c>DomSubgridPasses.DeferCyclicFlexInlineSizes</c> has neutralized for the intrinsic
    /// pass. The size field itself holds the neutral value; this records that the declaration
    /// is still a definite percentage, for the consumers that ask about definiteness rather
    /// than about the value.
    /// </summary>
    /// <remarks>
    /// DEVIATION: no counterpart in crates/obscura-render, which only rewrites the size field
    /// and so cannot tell a neutralized declaration from an authored <c>auto</c>. See "Known
    /// deviations" in todo.md.
    /// </remarks>
    public byte DeferredCyclicInlineSlots;

    /// <summary>
    /// <c>aspect-ratio</c> as width/height, or an image's intrinsic ratio resolved at layout.
    /// </summary>
    /// <remarks>
    /// Lets a replaced element (or a padding-box card) derive the missing dimension from the
    /// given one, so a <c>width:100%</c> image gets a real height instead of collapsing to zero.
    /// </remarks>
    public float? AspectRatio;

    /// <summary>
    /// The authored <c>aspect-ratio</c>, already in CSSOM's <c>W / H</c> form. Only an
    /// authored declaration sets it: a ratio derived from <c>width</c>/<c>height</c>
    /// attributes or from decoded media is not the computed value of the property, and
    /// Chromium reports <c>auto</c> for it.
    /// </summary>
    public string? AspectRatioSpecified;

    /// <summary>
    /// Whether the preferred ratio came from decoded intrinsic media and therefore applies to
    /// the content box regardless of <c>box-sizing</c>.
    /// </summary>
    public bool AspectRatioIsIntrinsic;

    /// <summary>Fetched intrinsic CSS-pixel size for a replaced element.</summary>
    /// <remarks>
    /// Kept alongside <see cref="AspectRatio"/> so its taffy leaf can contribute a real
    /// min/max-content size when percentage dimensions are resolved through an auto-sized
    /// wrapper (<c>img { width:100%; height:auto }</c>).
    /// </remarks>
    public (float Width, float Height)? IntrinsicSize
    {
        get => m_rare?.IntrinsicSize;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.IntrinsicSize = value;
            }
        }
    }

    /// <summary>Per-axis decoded intrinsic metadata used by the replaced sizing path.</summary>
    /// <remarks>
    /// SVG can expose only one dimension or a <c>viewBox</c> ratio, distinctions that the
    /// stable public <see cref="IntrinsicSize"/> tuple cannot represent.
    /// </remarks>
    internal ReplacedIntrinsic? ReplacedIntrinsic
    {
        get => m_rare?.ReplacedIntrinsic;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.ReplacedIntrinsic = value;
            }
        }
    }

    /// <summary>
    /// Definite content-box width available to an auto/auto ratio-only replaced element in
    /// ordinary block flow.
    /// </summary>
    /// <remarks>
    /// Taffy's flex-row stand-in for an inline formatting context asks atomic children for
    /// max-content first, so its measure callback otherwise never sees the definite line width
    /// that CSS replaced sizing uses for this special case.
    /// </remarks>
    internal float? RatioOnlyAvailableWidth;

    /// <summary>
    /// The current ratio came from HTML width/height presentation hints (<c>&lt;img&gt;</c> or
    /// its selected <c>&lt;picture&gt;&lt;source&gt;</c>), rather than authored CSS. A decoded
    /// image's natural ratio replaces this provisional ratio.
    /// </summary>
    public bool AspectRatioIsMapped;

    /// <summary>Whether this element generates a replaced box.</summary>
    /// <remarks>
    /// Replaced elements with <c>display:inline</c> are atomic and therefore keep authored
    /// width/height/min/max sizing. Ordinary inline boxes compute those properties but ignore
    /// them for used layout. This provenance cannot be inferred from intrinsic dimensions
    /// because an unloaded or broken image remains replaced.
    /// </remarks>
    internal bool IsReplacedBox;

    /// <summary>
    /// Whether this element uses the intrinsic replaced-element sizing algorithm. This is
    /// narrower than <see cref="IsReplacedBox"/>: form controls such as buttons are atomic
    /// inline boxes but still use ordinary grid <c>normal</c> stretch behavior in Chromium and
    /// Gecko.
    /// </summary>
    internal bool HasReplacedSizing;

    /// <summary>
    /// A native form control's intrinsic content box, published when its own <c>width</c> cannot
    /// give it one. A control has no child boxes, so once a percentage width fails to resolve
    /// there is nothing left to size it from and it collapses to its padding; this is what the
    /// leaf measure hands back instead.
    /// </summary>
    internal (float Width, float Height)? NativeControlContent
    {
        get => m_rare?.NativeControlContent;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.NativeControlContent = value;
            }
        }
    }

    public Edges Margin;

    /// <summary>Which margin sides are <c>auto</c> (top, right, bottom, left).</summary>
    /// <remarks>
    /// <c>margin: 0 auto</c> / <c>margin-inline: auto</c> centering needs a real Auto margin,
    /// which the float <see cref="Margin"/> cannot express; this flag drives it at taffy
    /// mapping.
    /// </remarks>
    public ReadOnlySpan<bool> MarginAuto => m_marginAuto ?? s_noBools4;

    /// <summary>Write one slot of <see cref="MarginAuto"/>, allocating only for a non-default value.</summary>
    public void SetMarginAuto(int index, bool value) => SetSlot(ref m_marginAuto, 4, index, value);

    /// <summary>Reset <see cref="MarginAuto"/> to all-default, releasing its array.</summary>
    public void ClearMarginAuto() => m_marginAuto = null;

    private bool[]? m_marginAuto;

    /// <summary>
    /// Percentage margin per side (top, right, bottom, left) as a 0..1 fraction, <c>null</c>
    /// when the side is a fixed length.
    /// </summary>
    /// <remarks>
    /// Like padding, every side resolves against the containing block's WIDTH; the float
    /// <see cref="Margin"/> cannot carry a percentage, so this is resolved to px during the DOM
    /// layout top-down pass once the containing-block width is known.
    /// </remarks>
    public ReadOnlySpan<float?> MarginPercent => m_marginPercent ?? s_noFloats4;

    /// <summary>Write one slot of <see cref="MarginPercent"/>, allocating only for a non-default value.</summary>
    public void SetMarginPercent(int index, float? value) => SetSlot(ref m_marginPercent, 4, index, value);

    private float?[]? m_marginPercent;

    /// <summary>
    /// Font- and viewport-relative margin lengths (top, right, bottom, left). These retain
    /// their unit until the top-down pass knows the element font size, root font size, and
    /// viewport dimensions.
    /// </summary>
    public ReadOnlySpan<Dimension?> MarginRelative => m_marginRelative ?? s_noDimensions4;

    /// <summary>Write one slot of <see cref="MarginRelative"/>, allocating only for a non-default value.</summary>
    public void SetMarginRelative(int index, Dimension? value) => SetSlot(ref m_marginRelative, 4, index, value);

    private Dimension?[]? m_marginRelative;

    /// <summary>Deferred <c>calc()</c>/<c>min()</c>/<c>max()</c>/<c>clamp()</c> margin expressions.</summary>
    public ReadOnlySpan<string?> MarginExpressions => m_marginExpressions ?? s_noStrings4;

    /// <summary>Write one slot of <see cref="MarginExpressions"/>, allocating only for a non-default value.</summary>
    public void SetMarginExpression(int index, string? value) => SetSlot(ref m_marginExpressions, 4, index, value);

    private string?[]? m_marginExpressions;

    public Edges Padding;

    /// <summary>
    /// Percentage padding per side (top, right, bottom, left) as a 0..1 fraction, <c>null</c>
    /// when the side is a fixed length.
    /// </summary>
    /// <remarks>
    /// All four sides resolve against the containing block's WIDTH (per CSS, including
    /// top/bottom): this is the responsive aspect-ratio-box trick
    /// (<c>padding-top:56.25%</c> reserves a 16:9 area). The percentage remains typed through
    /// Taffy layout so flex/grid min/max sizing can establish the final containing-block width
    /// first. The DOM layout pass writes Taffy's resolved used pixels back into
    /// <see cref="Padding"/> before paint and geometry consumers inspect the computed layout.
    /// </remarks>
    public ReadOnlySpan<float?> PaddingPercent => m_paddingPercent ?? s_noFloats4;

    /// <summary>Write one slot of <see cref="PaddingPercent"/>, allocating only for a non-default value.</summary>
    public void SetPaddingPercent(int index, float? value) => SetSlot(ref m_paddingPercent, 4, index, value);

    /// <summary>Reset <see cref="PaddingPercent"/> to all-default, releasing its array.</summary>
    public void ClearPaddingPercent() => m_paddingPercent = null;

    private float?[]? m_paddingPercent;

    /// <summary>
    /// Font- and viewport-relative padding lengths (top, right, bottom, left), resolved
    /// alongside <see cref="MarginRelative"/> during the top-down pass.
    /// </summary>
    public ReadOnlySpan<Dimension?> PaddingRelative => m_paddingRelative ?? s_noDimensions4;

    /// <summary>Write one slot of <see cref="PaddingRelative"/>, allocating only for a non-default value.</summary>
    public void SetPaddingRelative(int index, Dimension? value) => SetSlot(ref m_paddingRelative, 4, index, value);

    private Dimension?[]? m_paddingRelative;

    /// <summary>Deferred <c>calc()</c>/<c>min()</c>/<c>max()</c>/<c>clamp()</c> padding expressions.</summary>
    public ReadOnlySpan<string?> PaddingExpressions => m_paddingExpressions ?? s_noStrings4;

    /// <summary>Write one slot of <see cref="PaddingExpressions"/>, allocating only for a non-default value.</summary>
    public void SetPaddingExpression(int index, string? value) => SetSlot(ref m_paddingExpressions, 4, index, value);

    private string?[]? m_paddingExpressions;

    public Edges Border;

    /// <summary>Specified border state.</summary>
    /// <remarks>
    /// <see cref="Border"/> above is the derived used-width view consumed by layout; it is zero
    /// on <c>none</c>/<c>hidden</c> sides while this model retains their specified widths for
    /// later cascade declarations.
    /// </remarks>
    public BorderModel BorderModel = Obscura.Render.BorderModel.Default;

    /// <summary>
    /// Border state before the first cascaded physical/logical border declaration. Logical
    /// sides cannot be mapped until inherited direction is known, so the top-down pass replays
    /// <see cref="BorderCascadeOps"/> over this snapshot in exact cascade order.
    /// </summary>
    internal BorderModel? BorderCascadeBase
    {
        get => m_rare?.BorderCascadeBase;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BorderCascadeBase = value;
            }
        }
    }

    internal IReadOnlyList<BorderCascadeOp> BorderCascadeOps
    {
        get => m_borderCascadeOps ?? (IReadOnlyList<BorderCascadeOp>)Array.Empty<BorderCascadeOp>();
        set => m_borderCascadeOps = value as List<BorderCascadeOp> ?? [.. value];
    }

    /// <summary>The backing list of <see cref="BorderCascadeOps"/>, allocated on first call.</summary>
    internal List<BorderCascadeOp> EnsureBorderCascadeOps() => m_borderCascadeOps ??= [];

    private List<BorderCascadeOp>? m_borderCascadeOps;

    /// <summary>
    /// Outline paint state. It deliberately has no counterpart in Taffy: outlines never
    /// contribute to box geometry.
    /// </summary>
    public OutlineModel Outline
    {
        get => m_rare is null ? OutlineModel.Default : m_rare.Outline;
        set
        {
            if (m_rare is not null || value != OutlineModel.Default)
            {
                Rare.Outline = value;
            }
        }
    }

    /// <summary>
    /// <c>clip-path: polygon(...)</c>, resolved against the final border box at paint time.
    /// <c>null</c> is the computed <c>none</c> value.
    /// </summary>
    public ClipPathPolygon? ClipPath;

    /// <summary>RGBA for the paint step. Parsed always (cheap), used only with paint.</summary>
    public RgbaColor? BackgroundColor;

    /// <summary>
    /// <c>linear-gradient(...)</c> background: angle in degrees clockwise from 12 o'clock per
    /// CSS, plus the list of color stops with optional 0..1 positions.
    /// </summary>
    /// <remarks>
    /// Modern hero sections use gradients heavily; without this they paint white.
    /// </remarks>
    public (float Angle, List<GradientStop> Stops)? BackgroundGradient
    {
        get => m_rare?.BackgroundGradient;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BackgroundGradient = value;
            }
        }
    }

    /// <summary>
    /// First <c>radial-gradient(...)</c> layer: center in box-relative fractions and color
    /// stops. It is painted below the first linear layer, matching the common
    /// <c>linear-gradient(...), radial-gradient(...)</c> hero pattern.
    /// </summary>
    public ((float X, float Y) Center, List<GradientStop> Stops)? BackgroundRadialGradient
    {
        get => m_rare?.BackgroundRadialGradient;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BackgroundRadialGradient = value;
            }
        }
    }

    /// <summary>
    /// Geometry paired with <see cref="BackgroundRadialGradient"/>. The legacy public tuple
    /// above remains unchanged for API compatibility.
    /// </summary>
    internal RadialGradientGeometry? BackgroundRadialGradientGeometry
    {
        get => m_rare?.BackgroundRadialGradientGeometry;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BackgroundRadialGradientGeometry = value;
            }
        }
    }

    /// <summary><c>conic-gradient(...)</c> background.</summary>
    /// <remarks>
    /// The angle is the CSS <c>from</c> angle, the center is a fraction of the border box, and
    /// stops are normalized during paint. Conic gradients commonly provide the color source for
    /// a repeated SVG mask in modern hero artwork.
    /// </remarks>
    public (float Angle, (float X, float Y) Center, List<GradientStop> Stops)? BackgroundConicGradient
    {
        get => m_rare?.BackgroundConicGradient;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BackgroundConicGradient = value;
            }
        }
    }

    /// <summary>
    /// Every parsed gradient in authored background-layer order. The legacy single-kind fields
    /// above remain populated for mask/text fast paths.
    /// </summary>
    public IReadOnlyList<BackgroundGradientLayer> BackgroundGradientLayers
    {
        get => m_backgroundGradientLayers ?? (IReadOnlyList<BackgroundGradientLayer>)Array.Empty<BackgroundGradientLayer>();
        set => m_backgroundGradientLayers = value as List<BackgroundGradientLayer> ?? [.. value];
    }

    private List<BackgroundGradientLayer>? m_backgroundGradientLayers;

    /// <summary>
    /// One entry per <see cref="BackgroundGradientLayers"/> item. Radial entries carry their
    /// authored ending shape; non-radial entries are <c>null</c>.
    /// </summary>
    internal IReadOnlyList<RadialGradientGeometry?> BackgroundGradientLayerRadialGeometries
    {
        get => m_backgroundGradientLayerRadialGeometries ?? (IReadOnlyList<RadialGradientGeometry?>)Array.Empty<RadialGradientGeometry?>();
        set => m_backgroundGradientLayerRadialGeometries = value as List<RadialGradientGeometry?> ?? [.. value];
    }

    private List<RadialGradientGeometry?>? m_backgroundGradientLayerRadialGeometries;

    /// <summary>
    /// The first <c>url(...)</c> reference from <c>background</c>/<c>background-image</c>
    /// (gradients and repeat keywords in the same shorthand are ignored: we paint the
    /// referenced image, not the gradient layer).
    /// </summary>
    public string? BackgroundImage;

    /// <summary>
    /// <c>background-size</c>, in px, when given as explicit length(s) (a bare <c>10px</c>
    /// applies to both axes, matching how small square icons are almost always sized).
    /// </summary>
    public (float Width, float Height)? BackgroundSize
    {
        get => m_rare?.BackgroundSize;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BackgroundSize = value;
            }
        }
    }

    /// <summary>
    /// Raw one/two-axis <c>background-size</c> expression, retained for paint-time resolution
    /// against the final owner box (<c>calc(100% - 2rem) auto</c>).
    /// </summary>
    public string? BackgroundSizeExpression;

    /// <summary>
    /// Keyword <c>background-size</c> behavior. <c>null</c> is CSS <c>auto</c>, which uses the
    /// image's intrinsic dimensions rather than stretching it to the box.
    /// </summary>
    public ObjectFit? BackgroundSizeFit;

    /// <summary>
    /// <c>background-position</c>, retained as a length-plus-percentage per axis. Percentages
    /// apply to the leftover space after resolving explicit, intrinsic, cover, or contain size.
    /// </summary>
    public BackgroundPosition BackgroundPosition
    {
        get => m_rare is null ? default : m_rare.BackgroundPosition;
        set
        {
            if (m_rare is not null || value != default)
            {
                Rare.BackgroundPosition = value;
            }
        }
    }

    /// <summary>
    /// Explicit <c>(repeat-x, repeat-y)</c> choice. <c>null</c> is the CSS initial
    /// <c>repeat</c> in both axes.
    /// </summary>
    public (bool X, bool Y)? BackgroundRepeat;

    /// <summary>
    /// Geometry longhands retained independently: gradients and images are authored in
    /// <c>background-origin</c>, while <c>background-clip</c> only limits which portion becomes
    /// visible.
    /// </summary>
    public BackgroundOrigin BackgroundOrigin = Obscura.Render.BackgroundOrigin.PaddingBox;

    public BackgroundClip BackgroundClip = Obscura.Render.BackgroundClip.BorderBox;

    /// <summary>
    /// <c>background-clip: text</c> / <c>-webkit-background-clip: text</c>: the background
    /// paints only through the element's glyphs, not as a filled box.
    /// </summary>
    /// <remarks>
    /// Combined with a transparent text color this is the common gradient-text technique (hero
    /// headings, buttons like astro.build's "Get Started"); without honoring it those labels
    /// paint invisible. Consumed in the text paint path: the text engine fills glyphs from the
    /// background, and paint suppresses the box fill so the gradient does not paint as a
    /// rectangle.
    /// </remarks>
    public bool BackgroundClipText;

    /// <summary>
    /// <c>mask-image</c>/<c>-webkit-mask-image: url(...)</c>: the ubiquitous "colored, scalable
    /// icon" pattern (an SVG shape used as a stencil, tinted by
    /// <c>background-color</c>/<c>color</c> instead of carrying its own colors). Without this,
    /// every such icon paints as a solid filled square.
    /// </summary>
    public string? MaskImage;

    /// <summary>Explicit <c>mask-size</c> / <c>-webkit-mask-size</c> in CSS px.</summary>
    public (float Width, float Height)? MaskSize
    {
        get => m_rare?.MaskSize;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.MaskSize = value;
            }
        }
    }

    /// <summary>
    /// Explicit <c>(repeat-x, repeat-y)</c> choice. <c>null</c> retains the CSS default
    /// (<c>repeat</c> on both axes) when an explicit tile size exists, while preserving the
    /// legacy fill-box fallback for unsized icon masks.
    /// </summary>
    public (bool X, bool Y)? MaskRepeat;

    /// <summary>Foreground (text) color for the paint step.</summary>
    public RgbaColor? Color;

    /// <summary>
    /// Whether <see cref="Color"/> / <see cref="BackgroundColor"/> were specified in a
    /// non-legacy sRGB notation, which decides only how <c>getComputedStyle</c> serializes
    /// them: <c>color(srgb r g b)</c> rather than <c>rgb()</c>/<c>rgba()</c>.
    /// </summary>
    public bool ColorIsSrgbFunction;

    /// <inheritdoc cref="ColorIsSrgbFunction"/>
    public bool BackgroundColorIsSrgbFunction;

    /// <summary>Computed SVG presentation properties supplied by author CSS.</summary>
    /// <remarks>
    /// Inline SVG is serialized into a standalone document for the SVG rasterizer; without
    /// carrying these values across that boundary, stylesheet-driven icons and text lose their
    /// fill/stroke and can become completely invisible.
    /// </remarks>
    public string? SvgFill;

    public string? SvgStroke;

    public string? SvgStrokeWidth;

    /// <summary>Specified <c>text-anchor</c>, before inheritance.</summary>
    public string? SvgTextAnchor;

    /// <summary>
    /// The four SVG paint properties after inheritance, serialized the way
    /// <c>getComputedStyle</c> reports them.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="SvgFill"/> / <see cref="SvgStroke"/> /
    /// <see cref="SvgStrokeWidth"/>, which stay *specified* values: those three are pushed back
    /// into the serialized SVG document as <c>!important</c> inline declarations, and pushing an
    /// inherited value there would override the SVG rasterizer's own inheritance - a
    /// <c>&lt;use&gt;</c> of a <c>&lt;symbol&gt;</c> in <c>&lt;defs&gt;</c> inherits its fill from
    /// the use site, not from the <c>&lt;svg&gt;</c> root the defs subtree sits under.
    /// <c>null</c> means every property still holds its initial value.
    /// </remarks>
    public SvgPaintValues? SvgPaint;

    /// <summary>
    /// Compatibility mirror for uniform border colors. New code should use
    /// <c>BorderModel.Colors</c>; this remains for programmatic LayoutStyle users.
    /// </summary>
    public RgbaColor? BorderColor;

    /// <summary>
    /// Used color scheme for CSS Color 5 <c>light-dark()</c>. The renderer's current user
    /// preference is light; an inherited <c>color-scheme: dark</c> subtree switches this to
    /// true, while <c>normal</c>, <c>light</c>, or a list that permits light keeps the light
    /// scheme.
    /// </summary>
    public bool ColorSchemeDark;

    public float? FontSize;

    /// <summary>
    /// <c>font-size</c> given in a font/viewport-relative unit, resolved to
    /// <see cref="FontSize"/> (px) during the inheritance pass against the parent and root
    /// font-sizes. <c>null</c> when font-size was absolute or unset.
    /// </summary>
    public Dimension? FontSizeRaw
    {
        get => m_rare?.FontSizeRaw;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.FontSizeRaw = value;
            }
        }
    }

    /// <summary>
    /// Deferred functional <c>font-size</c> (<c>clamp()</c>, <c>min()</c>, <c>max()</c>,
    /// <c>calc()</c>).
    /// </summary>
    /// <remarks>
    /// These expressions must see the live viewport and parent font size; eagerly treating
    /// <c>9vw</c> as the number 9 made responsive headings pin to the minimum arm of their
    /// clamp.
    /// </remarks>
    public string? FontSizeExpression;

    /// <summary>Computed <c>letter-spacing</c> in CSS pixels.</summary>
    /// <remarks>
    /// This inherited property is resolved top-down because <c>em</c> is relative to the
    /// element's own computed font size while <c>rem</c> and viewport units need live context.
    /// </remarks>
    public float? LetterSpacing;

    /// <summary>Non-pixel <c>letter-spacing</c> retained until the inheritance pass.</summary>
    public Dimension? LetterSpacingRaw
    {
        get => m_rare?.LetterSpacingRaw;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.LetterSpacingRaw = value;
            }
        }
    }

    /// <summary>Deferred functional <c>letter-spacing</c> (<c>calc()</c>, <c>min()</c>, <c>clamp()</c>).</summary>
    public string? LetterSpacingExpression;

    /// <summary>
    /// Whether the computed value came from a non-<c>normal</c> declaration. Keeping this
    /// provenance distinguishes an explicit zero from the <c>normal</c> initial value while
    /// resolving the inherited property.
    /// </summary>
    public bool? LetterSpacingNonNormal;

    /// <summary>
    /// Specified CSS font weight during cascade (<c>1..1000</c>, <c>bolder</c>, or
    /// <c>lighter</c>), normalized to its numeric computed value by the inheritance pass before
    /// layout and shaping.
    /// </summary>
    public string? FontWeight;

    /// <summary>The computed <c>font-family</c> list, lowercased. Inherited.</summary>
    /// <remarks>
    /// The text engine resolves it to a bundled face (Liberation Sans/Serif/Mono) the way
    /// Chromium picks a generic family on this host.
    /// </remarks>
    public string? FontFamily;

    /// <summary>
    /// The <c>font-family</c> list as the author spelled it, re-serialized the way a
    /// computed-style query reports it: original casing, one <c>", "</c> between families, and
    /// quotes only where a family does not round-trip as an identifier.
    /// </summary>
    /// <remarks>
    /// <see cref="FontFamily"/> stays lower-cased because every face lookup matches against it
    /// case-insensitively; this is the reporting spelling only, and follows
    /// <see cref="FontFamily"/> everywhere, inheritance included.
    /// DEVIATION: crates/obscura-render keeps only the lower-cased list, so it reports
    /// <c>"plus jakarta sans", "inter"</c> where Chromium reports
    /// <c>"Plus Jakarta Sans", Inter</c>. See "Known deviations" in todo.md.
    /// </remarks>
    public string? FontFamilySpecified;

    /// <summary>
    /// The computed <c>cursor</c> keyword, or null while it still inherits. Inherited, initial
    /// <c>auto</c>.
    /// </summary>
    /// <remarks>
    /// DEVIATION: crates/obscura-render does not model <c>cursor</c> at all, so a
    /// computed-style query falls back to the inline declaration and answers <c>auto</c> for
    /// every element styled by a rule. See "Known deviations" in todo.md.
    /// </remarks>
    public string? Cursor;

    /// <summary>
    /// The computed <c>pointer-events</c> keyword, or null while it still inherits. Inherited,
    /// initial <c>auto</c>.
    /// </summary>
    /// <remarks>
    /// Reporting only: hit testing runs in JavaScript through <c>document.elementFromPoint</c>,
    /// which does not consult this.
    /// DEVIATION: crates/obscura-render does not model <c>pointer-events</c>. See "Known
    /// deviations" in todo.md.
    /// </remarks>
    public string? PointerEvents;

    /// <summary>
    /// Computed inherited <c>font-optical-sizing</c>. <c>null</c> during cascade means inherit;
    /// the top-down pass resolves every element to a value.
    /// </summary>
    public FontOpticalSizing? FontOpticalSizing;

    /// <summary>
    /// Computed inherited <c>font-variation-settings</c>. <c>null</c> during cascade means
    /// inherit, while an empty list is the <c>normal</c> initial value.
    /// </summary>
    public List<FontVariationSetting>? FontVariationSettings;

    /// <summary>
    /// Inherited <c>text-align</c>, represented with the matching horizontal alignment
    /// keywords.
    /// </summary>
    /// <remarks>
    /// Kept separate from flex/grid <c>align-items</c>: using one field for both made
    /// <c>text-align:left</c> shrink-wrap flex children.
    /// </remarks>
    public Layout.AlignItems? TextAlign;

    /// <summary>Computed inherited <c>text-indent</c>.</summary>
    /// <remarks>
    /// Font/viewport-relative lengths are resolved to pixels during the top-down inheritance
    /// pass; percentages remain typed until the final inline-formatting-context width is known.
    /// <c>null</c> during cascade means inherit, while the root resolves to the initial zero
    /// length.
    /// </remarks>
    public Dimension? TextIndent;

    public Layout.AlignItems? AlignItems;

    public Layout.AlignItems? JustifyItems;

    public Layout.AlignItems? AlignSelf;

    public Layout.AlignItems? JustifySelf;

    public Layout.AlignContent? AlignContent;

    public Layout.FlexDirection? FlexDirection;

    public Layout.FlexWrap? FlexWrap;

    public Layout.AlignContent? JustifyContent;

    public float? FlexGrow;

    public float? FlexShrink;

    /// <summary>
    /// Flex/grid item order. The formatting algorithm consumes children in order-modified
    /// document order, with source order breaking ties.
    /// </summary>
    public int Order;

    /// <summary>
    /// <c>flex-basis</c> (longhand, or the length in a <c>flex:</c> shorthand). <c>Auto</c> is
    /// the default. Fixed-basis sidebars/columns (<c>flex: 0 0 260px</c>) collapse to content
    /// width without it.
    /// </summary>
    public Dimension FlexBasis;

    /// <summary>
    /// A <c>flex-basis</c> written as CSS math that depends on the percentage basis, kept
    /// unresolved so the flex algorithm can resolve it against the container's inner main
    /// size the way a bare percentage is resolved.
    /// </summary>
    /// <remarks>
    /// <see cref="FlexBasis"/> stays <c>auto</c> while this is set;
    /// <c>TaffyStyleMapping</c> hands taffy the calc handle instead. The percentage basis is
    /// not known at computed-value time, and flattening it against the initial 16px made
    /// <c>flex: 1 1 calc(50% - 6px)</c> a 2px basis.
    /// </remarks>
    public GridCalcExpression? FlexBasisCalc;

    /// <summary>
    /// The specified <c>flex-basis</c> text when the computed value keeps its math function
    /// (a percentage-dependent <c>calc()</c>), which is what <c>getComputedStyle</c> reports.
    /// </summary>
    public string? FlexBasisSpecified;

    /// <summary>
    /// Whether <c>flex-direction</c> (or <c>flex-flow</c>) was authored, as opposed to
    /// <see cref="FlexDirection"/> carrying the internal table approximation's column.
    /// </summary>
    internal bool FlexDirectionAuthored;

    /// <summary>
    /// Whether <c>align-items</c> (or <c>place-items</c>) was authored, as opposed to
    /// <see cref="AlignItems"/> carrying the internal table approximation's stretch (a table
    /// box) or flex-start (a cell box).
    /// </summary>
    internal bool AlignItemsAuthored;

    /// <summary>
    /// Whether <c>min-width</c> (or <c>min-inline-size</c>) was authored, as opposed to
    /// <see cref="MinWidth"/> carrying the internal table approximation's zero.
    /// </summary>
    internal bool MinWidthAuthored;

    // CSS Grid. Tracks are stored as taffy sizing functions; GridAreas is the parsed
    // `grid-template-areas` matrix (one list per row, "." for a null cell), resolved to line
    // placements on children in a later pass.
    public IReadOnlyList<Layout.GridTemplateComponent> GridTemplateColumns
    {
        get => m_gridTemplateColumns ?? (IReadOnlyList<Layout.GridTemplateComponent>)Array.Empty<Layout.GridTemplateComponent>();
        set => m_gridTemplateColumns = value as List<Layout.GridTemplateComponent> ?? [.. value];
    }

    /// <summary>Reset <see cref="GridTemplateColumns"/> to empty, releasing its list.</summary>
    public void ClearGridTemplateColumns() => m_gridTemplateColumns = null;

    private List<Layout.GridTemplateComponent>? m_gridTemplateColumns;

    public IReadOnlyList<Layout.GridTemplateComponent> GridTemplateRows
    {
        get => m_gridTemplateRows ?? (IReadOnlyList<Layout.GridTemplateComponent>)Array.Empty<Layout.GridTemplateComponent>();
        set => m_gridTemplateRows = value as List<Layout.GridTemplateComponent> ?? [.. value];
    }

    /// <summary>Reset <see cref="GridTemplateRows"/> to empty, releasing its list.</summary>
    public void ClearGridTemplateRows() => m_gridTemplateRows = null;

    private List<Layout.GridTemplateComponent>? m_gridTemplateRows;

    /// <summary>
    /// Rust keeps the opaque <c>calc()</c> handles embedded in grid track sizing functions
    /// alive here until every Taffy layout pass has completed. The managed port does not need
    /// the keepalive (the GC owns those objects), so this exists only to preserve the field
    /// shape for the style port. Allocated lazily so the common element pays nothing.
    /// </summary>
    internal List<object>[]? GridCalcExpressions;

    /// <summary>
    /// Track sizing functions for columns created outside the explicit grid. An empty list is
    /// the CSS initial <c>auto</c> value: taffy supplies one automatic implicit track and
    /// cycles a non-empty authored list.
    /// </summary>
    public IReadOnlyList<Layout.TrackSizingFunction> GridAutoColumns
    {
        get => m_gridAutoColumns ?? (IReadOnlyList<Layout.TrackSizingFunction>)Array.Empty<Layout.TrackSizingFunction>();
        set => m_gridAutoColumns = value as List<Layout.TrackSizingFunction> ?? [.. value];
    }

    private List<Layout.TrackSizingFunction>? m_gridAutoColumns;

    /// <summary>Track sizing functions for rows created outside the explicit grid.</summary>
    public IReadOnlyList<Layout.TrackSizingFunction> GridAutoRows
    {
        get => m_gridAutoRows ?? (IReadOnlyList<Layout.TrackSizingFunction>)Array.Empty<Layout.TrackSizingFunction>();
        set => m_gridAutoRows = value as List<Layout.TrackSizingFunction> ?? [.. value];
    }

    private List<Layout.TrackSizingFunction>? m_gridAutoRows;

    /// <summary>
    /// Explicit CSS-wide <c>inherit</c> markers. The properties are normally non-inherited, so
    /// only the keyword copies the parent's computed list.
    /// </summary>
    internal bool GridAutoColumnsInherit;

    internal bool GridAutoRowsInherit;

    /// <summary>
    /// <c>grid-template-columns: subgrid</c>. Taffy has no native subgrid track component, so
    /// the DOM pass resolves the safe full-span column subset after measuring the
    /// non-subgridded ancestor's intrinsic track contributions.
    /// </summary>
    public bool GridTemplateColumnsSubgrid;

    public Layout.GridAutoFlow? GridAutoFlow;

    public List<List<string>>? GridAreas;

    public string? GridAreaName;

    public Layout.Line<Layout.GridPlacement>? GridColumn
    {
        get => m_rare?.GridColumn;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.GridColumn = value;
            }
        }
    }

    public Layout.Line<Layout.GridPlacement>? GridRow
    {
        get => m_rare?.GridRow;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.GridRow = value;
            }
        }
    }

    /// <summary>
    /// <c>[line-name]</c> to 1-based grid line number, parsed from
    /// <c>grid-template-columns</c>/<c>-rows</c>.
    /// </summary>
    /// <remarks>
    /// taffy has no native named-line support, so children placed by name
    /// (<c>grid-column: content-start / content-end</c>, widely used by the Guardian and other
    /// editorial grids) are resolved to numeric lines against these maps in the DOM grid-area
    /// resolution pass. Created with <see cref="StringComparer.Ordinal"/>.
    /// </remarks>
    public Dictionary<string, short>? GridColLineNames;

    public Dictionary<string, short>? GridRowLineNames;

    /// <summary>
    /// Raw <c>grid-column</c>/<c>grid-row</c> value when it references a named line (so it
    /// cannot be resolved to a taffy line until the parent's line-name map is known). Resolved
    /// in the same later pass; numeric/<c>span</c> values still fill
    /// <see cref="GridColumn"/>/<see cref="GridRow"/> directly at cascade time.
    /// </summary>
    public string? GridColumnRaw;

    public string? GridRowRaw;

    public float? ColumnGap;

    public float? RowGap;

    /// <summary>
    /// Deferred gap values. Font- and viewport-relative units cannot be converted until the
    /// element's computed font-size and the live viewport are known; eagerly treating
    /// <c>rem</c> as 16px breaks pages that customize the root font-size.
    /// </summary>
    public string? ColumnGapExpression;

    public string? RowGapExpression;

    // CSS Multi-column Layout. A count greater than one creates that many equal-width
    // fragmentainer columns during DOM build; the first layout pass measures the in-flow child
    // boxes at their real column width and a bounded balancing pass then distributes them in
    // column-major order. `null` is the initial `auto` column count.
    public ushort? ColumnCount;

    /// <summary>
    /// <c>break-inside: avoid</c> makes this box an atomic balancing unit.
    /// </summary>
    /// <remarks>
    /// The current box-level multicol implementation cannot fragment the inside of a child yet,
    /// but retaining the computed value makes that limitation explicit and lets the balancing
    /// path distinguish authored break avoidance as finer-grained fragmentation is added.
    /// </remarks>
    public bool BreakInsideAvoid;

    /// <summary>
    /// <c>border-spacing: &lt;horizontal&gt; &lt;vertical&gt;?</c> (or the <c>cellspacing</c>
    /// attribute).
    /// </summary>
    /// <remarks>
    /// Only meaningful on a <c>&lt;table&gt;</c>; taffy has no native table display mode, so
    /// the DOM border-spacing propagation distributes this down as the table's own row gap and
    /// each descendant <c>&lt;tr&gt;</c>'s column gap.
    /// </remarks>
    public (float Horizontal, float Vertical)? BorderSpacing
    {
        get => m_rare?.BorderSpacing;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BorderSpacing = value;
            }
        }
    }

    /// <summary>
    /// The declared <c>border-spacing</c> text, kept only when it carries a font-relative
    /// length, so the top-down pass can re-read it once the element's font size exists.
    /// </summary>
    /// <inheritdoc cref="FilterFontRelative" path="/remarks"/>
    internal string? BorderSpacingFontRelative
    {
        get => m_rare?.BorderSpacingFontRelative;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BorderSpacingFontRelative = value;
            }
        }
    }

    /// <summary>
    /// Whether <c>border-spacing</c> was declared as <c>inherit</c> or <c>unset</c>, so the
    /// top-down pass has to copy the parent's computed value onto this element.
    /// </summary>
    /// <remarks>
    /// The property is inherited but nothing carries it down the style tree - only the element
    /// that declared it holds one - so the keyword cannot be answered where the cascade runs.
    /// </remarks>
    internal bool BorderSpacingInherit
    {
        get => m_rare?.BorderSpacingInherit ?? false;
        set
        {
            if (m_rare is not null || value)
            {
                Rare.BorderSpacingInherit = value;
            }
        }
    }

    /// <summary>Computed <c>border-collapse</c>.</summary>
    /// <remarks>
    /// This property is inherited; <c>null</c> means no value was specified on this node yet
    /// and is resolved top-down before table construction. The collapsed-border conflict/paint
    /// model is still approximate, but collapsed tables must at minimum contribute no
    /// border-spacing to their geometry.
    /// </remarks>
    public bool? BorderCollapse;

    /// <summary>
    /// Computed <c>caption-side</c>: <c>true</c> for <c>bottom</c>. The property is inherited,
    /// so <c>null</c> means nothing has been specified on this node yet.
    /// </summary>
    public bool? CaptionSideBottom
    {
        get => m_rare?.CaptionSideBottom;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.CaptionSideBottom = value;
            }
        }
    }

    /// <summary>
    /// The used border of a box in the collapsing border model (CSS 2.1 17.6.2): half the
    /// border resolved for each of its four edges, which is the half that lies inside this box.
    /// <c>null</c> on every box that is not part of a collapsing table.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="Border"/> rather than replacing it because a style object
    /// survives a layout pass when its node's cascade did not change, so a pass that rewrote
    /// <see cref="Border"/> in place would halve it again on the next one. Read it through
    /// <see cref="UsedBorder"/>.
    /// </remarks>
    public Edges? CollapsedBorder
    {
        get => m_rare?.CollapsedBorder;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.CollapsedBorder = value;
            }
        }
    }

    /// <summary>
    /// The border widths that size and position this box: <see cref="CollapsedBorder"/> where
    /// the collapsing table model resolved one, and <see cref="Border"/> otherwise.
    /// </summary>
    public Edges UsedBorder => m_rare?.CollapsedBorder ?? Border;

    // Positioning. `position: absolute|fixed` takes the box out of normal flow.
    public Layout.Position? Position;

    /// <summary>
    /// Distinguishes <c>fixed</c> from <c>absolute</c>; both map to taffy's absolute layout
    /// mode, but fixed boxes use the initial containing block.
    /// </summary>
    public bool PositionFixed;

    /// <summary>
    /// Distinguishes <c>sticky</c> from ordinary relative positioning. Sticky boxes remain in
    /// normal flow; their insets constrain a scroll-time translation and therefore must not be
    /// applied as taffy relative offsets.
    /// </summary>
    public bool PositionSticky;

    /// <summary>Top, right, bottom, left.</summary>
    public ReadOnlySpan<Dimension?> Inset => m_inset ?? s_noDimensions4;

    /// <summary>Write one slot of <see cref="Inset"/>, allocating only for a non-default value.</summary>
    public void SetInset(int index, Dimension? value) => SetSlot(ref m_inset, 4, index, value);

    /// <summary>Reset <see cref="Inset"/> to all-default, releasing its array.</summary>
    public void ClearInset() => m_inset = null;

    private Dimension?[]? m_inset;

    /// <summary>Deferred functional inset expressions in top/right/bottom/left order.</summary>
    public ReadOnlySpan<string?> InsetExpressions => m_insetExpressions ?? s_noStrings4;

    /// <summary>Write one slot of <see cref="InsetExpressions"/>, allocating only for a non-default value.</summary>
    public void SetInsetExpression(int index, string? value) => SetSlot(ref m_insetExpressions, 4, index, value);

    /// <summary>Reset <see cref="InsetExpressions"/> to all-default, releasing its array.</summary>
    public void ClearInsetExpressions() => m_insetExpressions = null;

    private string?[]? m_insetExpressions;

    /// <summary>
    /// The late-resolved form of a percentage-bearing <see cref="InsetExpressions"/> entry,
    /// allocated lazily and in the same top/right/bottom/left order. Non-null only where the
    /// expression's percentage has to be resolved against the used containing block during
    /// layout rather than flattened to px beforehand.
    /// </summary>
    /// <remarks>
    /// DEVIATION: no counterpart in <c>crates/obscura-render</c>, which flattens every
    /// functional inset against the viewport at computed-value time. See the remarks on the
    /// inset loop in <c>LayoutDomComputed.ResolveOneComputedStyle</c>.
    /// </remarks>
    public GridCalcExpression?[]? InsetCalc;

    /// <summary>
    /// The late-resolved form of an inline-axis <c>SizeExpressions</c> entry, allocated lazily
    /// and in the same six-slot order. Non-null only where the expression's percentage has to
    /// be resolved against the used containing block during layout.
    /// </summary>
    /// <remarks>
    /// DEVIATION: no counterpart in <c>crates/obscura-render</c>. See the remarks on the size
    /// loop in <c>LayoutDomComputed.ResolveOneComputedStyle</c>.
    /// </remarks>
    public GridCalcExpression?[]? SizeCalc;

    /// <summary>
    /// <c>overflow-clip-margin</c>, reported only. <c>null</c> is the initial <c>0px</c>; the
    /// UA sheet gives a replaced element <c>content-box</c>.
    /// </summary>
    /// <remarks>
    /// The property has no paint effect here: an element with <c>overflow: clip</c> clips at
    /// its padding box, which is what <c>0px</c> means. A <c>content-box</c> origin (and any
    /// non-zero margin) would move that edge and is not modeled.
    /// </remarks>
    public string? OverflowClipMargin;

    /// <summary>
    /// <c>overflow</c>/-x/-y other than <c>visible</c>: clips this element's descendants to its
    /// border box during paint.
    /// </summary>
    /// <remarks>
    /// This is what makes the ubiquitous "visually-hidden but accessible" pattern (a 1x1
    /// absolutely-positioned, clipped box used for skip-links and screen-reader-only labels)
    /// actually invisible instead of painting its text wherever it lands.
    /// </remarks>
    public bool OverflowHidden;

    /// <summary>
    /// Independent computed overflow clips. <see cref="OverflowHidden"/> remains the aggregate
    /// compatibility/BFC flag; paint and automatic minimum sizing must consult the relevant
    /// axis.
    /// </summary>
    public bool OverflowClipX;

    public bool OverflowClipY;

    internal bool OverflowAxesSet;

    /// <summary>
    /// The specified <c>overflow-x</c> keyword: 0 <c>visible</c>, 1 <c>clip</c>, 2
    /// <c>hidden</c>, 3 <c>scroll</c>, 4 <c>auto</c>. <c>overlay</c> is the legacy alias of
    /// <c>auto</c> and shares its code, and every code from 2 up establishes a scroll
    /// container - the order is what <see cref="ComputedStyle.RecomputeOverflow"/> tests.
    /// </summary>
    /// <remarks>
    /// DEVIATION: crates/obscura-render collapses <c>hidden</c>, <c>scroll</c>, <c>auto</c> and
    /// <c>overlay</c> onto one code, so it cannot report which of them an element specified and
    /// answers `auto` for all four. See "Known deviations" in todo.md.
    /// </remarks>
    internal byte OverflowSpecifiedX;

    internal byte OverflowSpecifiedY;

    /// <summary>
    /// The computed <c>overflow-x</c> keyword, in the same encoding as
    /// <see cref="OverflowSpecifiedX"/>, after the CSS Overflow computed-value coupling has
    /// run. This is the value a computed-style query has to report.
    /// </summary>
    internal byte OverflowComputedX;

    internal byte OverflowComputedY;

    internal bool OverflowInheritX;

    internal bool OverflowInheritY;

    internal bool OverflowScrollX;

    internal bool OverflowScrollY;

    /// <summary>
    /// This element's authored overflow is propagated to the viewport. Its own box therefore
    /// behaves as <c>overflow: visible</c> for layout/BFC purposes while the capture viewport
    /// supplies the paint clip.
    /// </summary>
    internal bool OverflowPropagatedToViewport;

    /// <summary>
    /// Whether computed overflow establishes a scroll container. <c>clip</c> clips paint but
    /// deliberately does not establish one, which matters for selecting the scrollport that
    /// controls a sticky descendant.
    /// </summary>
    public bool OverflowScrollContainer;

    /// <summary>
    /// Number of classic scrollbar gutters reserved by <c>scrollbar-gutter:stable</c> (one) or
    /// <c>stable both-edges</c> (two).
    /// </summary>
    /// <remarks>
    /// Root gutters reduce the initial containing block even when the scrollbar itself is
    /// visually hidden in a headless screenshot.
    /// </remarks>
    public byte ScrollbarGutters;

    /// <summary>
    /// `scrollbar-width`: 0 auto, 1 thin, 2 none. Sizes the gutter that
    /// <see cref="ScrollbarGutters"/> reserves.
    /// </summary>
    /// <remarks>
    /// DEVIATION: crates/obscura-render parses neither this property nor a gutter on any box
    /// but the root, so a nested scroll container reserves nothing. See "Known deviations" in
    /// todo.md.
    /// </remarks>
    public byte ScrollbarWidthKind;

    /// <summary>
    /// Set on the root element, whose stable gutter is already taken out of the initial
    /// containing block, so the taffy mapping does not reserve it a second time.
    /// </summary>
    internal bool GutterReservedByViewport;

    /// <summary>
    /// Width of one stable scrollbar gutter on this box, or 0 when none is reserved. Chromium
    /// reserves it only for a scroll container that asks for it with `scrollbar-gutter: stable`,
    /// and only on the inline axis; an overlay scrollbar with no such declaration takes no space.
    /// </summary>
    internal float StableScrollbarGutter()
    {
        if (ScrollbarGutters == 0 || GutterReservedByViewport || !OverflowScrollContainer)
        {
            return 0f;
        }

        return ScrollbarThickness(vertical: true);
    }

    /// <summary>
    /// The author's <c>::-webkit-scrollbar</c> <c>width</c> in CSS pixels, or <c>null</c> when
    /// the sheet styles no scrollbar box for this element.
    /// </summary>
    /// <remarks>
    /// Chromium sizes a custom scrollbar from that pseudo-element's <c>width</c> (vertical) and
    /// <c>height</c> (horizontal), and the resulting thickness is taken out of the scrollport,
    /// so it is layout input and not decoration. Tesserae asks for 9px on every scroll pane.
    /// </remarks>
    internal float? ScrollbarPseudoWidth;

    /// <summary>The author's <c>::-webkit-scrollbar</c> <c>height</c> in CSS pixels.</summary>
    internal float? ScrollbarPseudoHeight;

    /// <summary>
    /// Width of the vertical scrollbar this box actually reserves, decided after a layout pass
    /// because an <c>overflow: auto</c> axis only gets one when its content overflows.
    /// </summary>
    internal float ReservedScrollbarY;

    /// <summary>Height of the horizontal scrollbar this box actually reserves.</summary>
    internal float ReservedScrollbarX;

    /// <summary>
    /// Thickness of this box's scrollbar on the given axis - <paramref name="vertical"/> for the
    /// one that takes width out of the scrollport.
    /// </summary>
    internal float ScrollbarThickness(bool vertical)
    {
        if (ScrollbarWidthKind == 2)
        {
            return 0f;
        }

        if ((vertical ? ScrollbarPseudoWidth : ScrollbarPseudoHeight) is { } custom)
        {
            return F32.Max(custom, 0f);
        }

        return ScrollbarWidthKind == 1 ? ThinScrollbarGutter : ClassicScrollbarGutter;
    }

    /// <summary>
    /// Whether the given axis is one a classic scrollbar can appear on: <c>scroll</c> always
    /// shows one, <c>auto</c> shows one only when the content overflows, and every other
    /// keyword (including <c>hidden</c>, which clips without a scrollbar) shows none.
    /// </summary>
    internal bool ScrollbarAxisKind(bool vertical, out bool always)
    {
        byte computed = vertical ? OverflowComputedY : OverflowComputedX;
        always = computed == 3;
        return computed is 3 or 4;
    }

    /// <summary>Classic scrollbar gutter width, matching Chromium on this platform.</summary>
    internal const float ClassicScrollbarGutter = 15f;

    /// <summary>`scrollbar-width: thin` gutter width, matching Chromium on this platform.</summary>
    internal const float ThinScrollbarGutter = 10f;

    /// <summary><c>float: left|right</c>.</summary>
    /// <remarks>
    /// True CSS float needs per-line reflow around the float's shape, which taffy's
    /// block/flex/grid modes do not do; see the DOM float-zone grouping for the bounded
    /// approximation this drives.
    /// </remarks>
    public Float? Float;

    /// <summary><c>visibility: hidden|visible</c>, own value.</summary>
    /// <remarks>
    /// <c>null</c> means "inherit the ancestor's computed value" (visibility, unlike most box
    /// properties, is a real inherited CSS property). Resolved into
    /// <see cref="EffectivelyInvisible"/> during the DOM layout inheritance pass.
    /// </remarks>
    public bool? VisibilityHidden;

    /// <summary>
    /// <c>opacity</c>, own (non-inherited) value in 0.0-1.0. <c>null</c> means the default of
    /// 1.0.
    /// </summary>
    public float? Opacity;

    /// <summary>The computed <c>filter</c> list, in application order; <c>null</c> for
    /// <c>none</c> and for a list that failed to parse.</summary>
    /// <remarks>
    /// CSS makes the whole declaration invalid when any one function is, so this is all-or-
    /// nothing rather than the parseable prefix. Treat the array as immutable - it is shared
    /// between the styles that <see cref="Clone"/> produces.
    /// <para>
    /// DEVIATION FROM RUST: <c>crates/obscura-render</c> keeps only a blur sigma here and
    /// drops every other function, so <c>filter</c> never reaches the computed-style snapshot
    /// and the four <c>drop-shadow()</c>s Curiosity outlines its pixel avatar with paint
    /// nothing. The port models the list.
    /// </para>
    /// </remarks>
    public FilterFunction[]? Filter;

    /// <summary>
    /// The <c>filter</c> declaration as written, kept only while it carries an <c>em</c>,
    /// <c>rem</c>, <c>ex</c> or <c>ch</c> length or names <c>currentcolor</c>, so the top-down
    /// pass can re-read it once the element's font size and computed colour exist.
    /// <c>null</c> for every other value.
    /// </summary>
    /// <remarks>
    /// These three are the properties whose lengths the cascade resolves on the spot, where
    /// <c>padding</c>, <c>margin</c>, <c>line-height</c>, <c>letter-spacing</c>, <c>gap</c> and
    /// <c>font-size</c> itself all keep a <see cref="Dimension"/> or an expression and resolve
    /// in the top-down pass. Storing the text rather than adding a fourth kind of deferral is
    /// what keeps the parsers single-pass; nothing but a font-relative value pays for it.
    /// </remarks>
    internal string? FilterFontRelative;

    /// <inheritdoc cref="FilterFontRelative"/>
    internal string? BackdropFilterFontRelative;

    /// <inheritdoc cref="FilterFontRelative"/>
    internal string? BoxShadowFontRelative;

    /// <summary>
    /// <c>backdrop-filter: blur(&lt;length&gt;)</c>, as the standard deviation in CSS
    /// pixels.
    /// </summary>
    /// <remarks>
    /// <c>blur()</c>'s argument <em>is</em> sigma, unlike <c>box-shadow</c>'s blur radius,
    /// which is 2 sigma. Unlike <see cref="Filter"/>, only a blur-only list is recorded:
    /// nothing paints a backdrop sepia, so reducing a mixed list to its blurs would paint a
    /// wrong result where painting none at least matches what <c>@supports</c> advertises.
    /// </remarks>
    public float? BackdropBlur;

    /// <summary>First CSS animation name and its timing contract.</summary>
    /// <remarks>
    /// The stylesheet sampler contributes animated opacity after normal declarations and before
    /// author <c>!important</c>, matching the animation cascade origin.
    /// </remarks>
    public string? AnimationName;

    public AnimationTiming AnimationTiming
    {
        get => m_rare is null ? Obscura.Render.AnimationTiming.Default : m_rare.AnimationTiming;
        set
        {
            if (m_rare is not null || value != Obscura.Render.AnimationTiming.Default)
            {
                Rare.AnimationTiming = value;
            }
        }
    }

    /// <summary>
    /// True when the selected keyframes contain at least one property this renderer can sample.
    /// Unsupported custom-property-only animations must not keep layout or screencast damage
    /// active forever.
    /// </summary>
    public bool AnimationHasRenderEffect;

    /// <summary>
    /// Strongest effect of the selected CSS keyframes. Geometry consumers use this to retain an
    /// older sampled layout only when every live effect is known to be paint-only.
    /// </summary>
    internal AnimationEffectImpact AnimationEffectImpact;

    /// <summary>Local time used for this element's sampled animation instance.</summary>
    public float AnimationLocalTimeMs;

    /// <summary><c>vertical-align</c> for a table cell's content.</summary>
    /// <remarks>
    /// Cells effectively default to <c>middle</c> in browsers (the HTML UA sheet sets it on row
    /// groups and cells inherit it); obscura applies it as main-axis alignment of the cell's
    /// flex-column stand-in. <c>null</c> on non-cell elements.
    /// </remarks>
    public VerticalAlign? VerticalAlign;

    /// <summary><c>vertical-align</c> as an inline box reads it.</summary>
    /// <remarks>
    /// Kept apart from <see cref="VerticalAlign"/>, which is the table-cell reading and cannot
    /// tell <c>top</c> from <c>text-top</c>. <c>null</c> means the property was never declared,
    /// which is the initial <c>baseline</c>.
    /// </remarks>
    public InlineVerticalAlign? InlineVerticalAlign;

    /// <summary><c>z-index</c> on a positioned element.</summary>
    /// <remarks>
    /// <c>null</c> is <c>auto</c> (tree order). A non-zero value lifts the element's whole
    /// subtree into a separate paint layer: negatives under the normal flow, positives above
    /// it, sorted.
    /// </remarks>
    public int? ZIndex;

    /// <summary>
    /// <c>clear</c>, when set: this element moves below preceding floats on the given side(s),
    /// ending their float zone.
    /// </summary>
    public Clear? Clear;

    /// <summary>
    /// Non-inherited CSS counter operations in computed declaration order. Reset operations run
    /// before increments on the same element.
    /// </summary>
    public IReadOnlyList<CounterDirective> CounterReset
    {
        get => m_counterReset ?? (IReadOnlyList<CounterDirective>)Array.Empty<CounterDirective>();
        set => m_counterReset = value as List<CounterDirective> ?? [.. value];
    }

    /// <summary>Reset <see cref="CounterReset"/> to empty, releasing its list.</summary>
    public void ClearCounterReset() => m_counterReset = null;

    private List<CounterDirective>? m_counterReset;

    public IReadOnlyList<CounterDirective> CounterIncrement
    {
        get => m_counterIncrement ?? (IReadOnlyList<CounterDirective>)Array.Empty<CounterDirective>();
        set => m_counterIncrement = value as List<CounterDirective> ?? [.. value];
    }

    private List<CounterDirective>? m_counterIncrement;

    public IReadOnlyList<CounterDirective> CounterSet
    {
        get => m_counterSet ?? (IReadOnlyList<CounterDirective>)Array.Empty<CounterDirective>();
        set => m_counterSet = value as List<CounterDirective> ?? [.. value];
    }

    private List<CounterDirective>? m_counterSet;

    /// <summary>
    /// Resolved during the inheritance pass: true when this element should not be painted at
    /// all, either from its own or an inherited <c>visibility: hidden</c>, or because the
    /// product of its own and every ancestor's <c>opacity</c> is zero. Fractional values remain
    /// paintable and are isolated into composited groups by the paint pass.
    /// </summary>
    public bool EffectivelyInvisible;

    /// <summary>A CSS image supplied by <c>content: url(...)</c> on a replaced element.</summary>
    /// <remarks>
    /// This is distinct from generated pseudo text: on an <c>&lt;img&gt;</c> it becomes the
    /// element's image source and contributes intrinsic dimensions just like an HTML
    /// <c>src</c>.
    /// </remarks>
    public string? ContentImage;

    /// <summary>
    /// Literal text injected by a <c>::before</c>/<c>::after</c> rule with a plain
    /// string-literal <c>content</c>. Rendered as an extra word-run at the start/end of this
    /// element's children, same as if it were real text content.
    /// </summary>
    public string? BeforeContent;

    public string? AfterContent;

    /// <summary>
    /// Typed computed <c>content</c> items retained until the document-order counter pass can
    /// resolve <c>counter()</c> and <c>counters()</c>.
    /// </summary>
    public List<GeneratedContentItem>? GeneratedContent;

    /// <summary>
    /// Computed boxes generated by <c>::before</c>/<c>::after</c>. Text-only pseudos continue
    /// through <see cref="BeforeContent"/>/<see cref="AfterContent"/>; these styles retain
    /// positioned decorative boxes for layout-independent painting.
    /// </summary>
    public LayoutStyle? BeforePseudo;

    public LayoutStyle? AfterPseudo;

    /// <summary>
    /// Computed author style for the native text-control <c>::placeholder</c> pseudo-element.
    /// Its anonymous glyphs are painted by the control.
    /// </summary>
    public LayoutStyle? PlaceholderPseudo;

    /// <summary>
    /// Computed author style for a range input's <c>::-webkit-slider-thumb</c> pseudo-element.
    /// The thumb is a native box the control paints itself; it never enters layout, so this is
    /// the only record of the size, radius, background and border the author gave it.
    /// </summary>
    public LayoutStyle? SliderThumbPseudo;

    /// <summary>
    /// True for <c>inline-block</c>/<c>inline-flex</c>/<c>inline-grid</c>: participates in the
    /// surrounding inline flow from the outside, like plain <c>inline</c> (both currently
    /// collapse to <see cref="Obscura.Render.Display.Inline"/>, since this engine has no
    /// separate inline-block layout mode), but unlike plain <c>inline</c> it must stay a single
    /// atomic box rather than have its own content merge into the parent's line-breaking.
    /// </summary>
    /// <remarks>
    /// The DOM's flattenable-inline check uses this to avoid flattening these away: doing so
    /// would lose the element as its own box (including any <c>::before</c>/<c>::after</c>
    /// content attached to it).
    /// </remarks>
    public bool IsInlineBlock;

    /// <summary>
    /// <c>display: flow-root</c>: generates a normal block box but establishes a new block
    /// formatting context, containing descendant floats and stopping their exclusion bands from
    /// propagating into outside siblings.
    /// </summary>
    public bool FlowRoot;

    /// <summary><c>display: contents</c>: the element generates no box of its own.</summary>
    /// <remarks>
    /// Its children participate in the parent's formatting context directly (the DOM builder
    /// splices them into the parent's child list). Kept as a flag beside <see cref="Display"/>
    /// because the element still carries inherited styles for its subtree and
    /// <c>display:none</c> must still win.
    /// </remarks>
    public bool DisplayContents;

    /// <summary><c>list-style-type</c> (or the <c>list-style</c> shorthand). Inherited.</summary>
    /// <remarks>
    /// <c>null</c> means "not set on this element, inherit". Resolved to a concrete value
    /// during the inheritance pass. Only <c>&lt;li&gt;</c> elements draw a marker from it, but
    /// it is carried on every element because it inherits (a <c>list-style: none</c> on a
    /// <c>&lt;ul&gt;</c> must reach its <c>&lt;li&gt;</c> children, which is how nav menus
    /// suppress bullets).
    /// </remarks>
    public ListStyle? ListStyle;

    /// <summary><c>line-height</c>. Inherited.</summary>
    /// <remarks>
    /// <c>null</c> means "not set, inherit"; resolved to a concrete value in the inheritance
    /// pass. Drives the vertical rhythm of shaped text (a fixed ratio made real-site prose
    /// noticeably tighter than Chromium).
    /// </remarks>
    public LineHeight? LineHeight;

    /// <summary><c>white-space</c>. Inherited.</summary>
    /// <remarks>
    /// <c>null</c> means inherit the nearest ancestor's value (or the initial <c>normal</c>).
    /// The inline shaper uses this to retain author/source newlines in code blocks and to select
    /// wrapping behavior.
    /// </remarks>
    public WhiteSpace? WhiteSpace;

    /// <summary>
    /// Non-inherited <c>text-overflow</c>. This first implementation deliberately models
    /// Chromium's single-value <c>clip|ellipsis</c> syntax; bidi/two-sided markers remain a
    /// separate extension.
    /// </summary>
    public TextOverflow TextOverflow;

    /// <summary>
    /// Non-inherited legacy line count. It affects direct pure-text inline formatting contexts
    /// only; nested descendant line counting needs a real block-line iterator and must not be
    /// approximated by clipping children.
    /// </summary>
    public uint? WebkitLineClamp;

    /// <summary><c>overflow-wrap</c> (legacy alias: <c>word-wrap</c>). Inherited.</summary>
    /// <remarks>
    /// <c>null</c> means inherit the nearest ancestor's value (or the initial <c>normal</c>).
    /// <c>Anywhere</c> contributes its emergency grapheme opportunities to min-content sizing,
    /// while legacy <c>BreakWord</c> uses those opportunities only for actual line layout.
    /// </remarks>
    public OverflowWrap? OverflowWrap;

    /// <summary><c>word-break</c>. Inherited.</summary>
    /// <remarks>
    /// <c>null</c> means inherit the nearest ancestor's value (or the initial <c>normal</c>).
    /// Kept separate from <c>overflow-wrap</c> because <c>break-all</c> adds
    /// typographic-letter opportunities while retaining UAX#14 punctuation constraints, whereas
    /// <c>keep-all</c> suppresses eligible letter/number boundaries without altering text.
    /// </remarks>
    public WordBreak? WordBreak;

    /// <summary><c>text-wrap-style</c>. Inherited.</summary>
    /// <remarks>
    /// <c>null</c> means inherit the nearest ancestor's value (or the initial <c>auto</c>).
    /// <c>Balance</c> keeps the natural line count but tightens the effective line-breaking
    /// width during the inline formatter's final shaping pass.
    /// </remarks>
    public TextWrapStyle? TextWrapStyle;

    /// <summary>
    /// Deferred functional line-height (<c>calc()</c>, <c>min()</c>, <c>clamp()</c>) resolved
    /// after the element font and live viewport are known.
    /// </summary>
    public string? LineHeightExpression;

    /// <summary><c>text-transform</c>. Inherited. Applied to span text before shaping.</summary>
    public TextTransform? TextTransform;

    /// <summary><c>text-decoration-line: underline</c> (or the <c>text-decoration</c> shorthand).</summary>
    /// <remarks>
    /// Not inherited in CSS, but a decoration visually covers descendant inline text, so it is
    /// propagated into the shaped spans of the element's subtree (this is what underlines
    /// links, which are underlined by UA default).
    /// </remarks>
    public bool? Underline;

    /// <summary><c>font-style: italic|oblique</c>. Inherited.</summary>
    /// <remarks>
    /// Selects an available oblique face when shaping; the bundled Linux <c>system-ui</c> face
    /// synthesizes its slant from DejaVu Sans regular/bold to match Chromium. <c>null</c> means
    /// inherit.
    /// </remarks>
    public bool? FontStyleItalic;

    /// <summary><c>object-fit</c> for a replaced element (<c>&lt;img&gt;</c>).</summary>
    /// <remarks>
    /// Controls how the decoded image is scaled into the element's box when their aspect ratios
    /// differ; <c>Fill</c> (default) stretches to the box, the rest preserve aspect ratio. Only
    /// consulted in the image paint path.
    /// </remarks>
    public ObjectFit ObjectFit;

    /// <summary><c>object-position</c> for replaced image content.</summary>
    /// <remarks>
    /// Percentages resolve against the leftover space after <c>object-fit</c>; the CSS initial
    /// value is centered on both axes.
    /// </remarks>
    public ObjectPosition ObjectPosition
    {
        get => m_rare is null ? Obscura.Render.ObjectPosition.Default : m_rare.ObjectPosition;
        set
        {
            if (m_rare is not null || value != Obscura.Render.ObjectPosition.Default)
            {
                Rare.ObjectPosition = value;
            }
        }
    }

    /// <summary>
    /// Ordered operations in the non-inherited <c>transform</c> property. Length percentages
    /// remain unresolved until the final border box is known.
    /// </summary>
    public IReadOnlyList<TransformOp> TransformOps
    {
        get => m_transformOps ?? (IReadOnlyList<TransformOp>)Array.Empty<TransformOp>();
        set => m_transformOps = value as List<TransformOp> ?? [.. value];
    }

    /// <summary>Reset <see cref="TransformOps"/> to empty, releasing its list.</summary>
    public void ClearTransformOps() => m_transformOps = null;

    private List<TransformOp>? m_transformOps;

    /// <summary>Transform value immediately below the Web Animations cascade origin.</summary>
    /// <remarks>
    /// A retained compositor-style sample restores this value before replaying the registered
    /// effects, so sparse keyframes never compound on the previously sampled transform.
    /// </remarks>
    internal WaapiSampleState? WaapiSampleState;

    /// <summary>Individual CSS <c>translate</c> property.</summary>
    /// <remarks>
    /// This composes independently with the legacy <c>transform</c> property, so
    /// <c>transform:none</c> must not clear it. Functional values are retained separately until
    /// the final border box is known because percentages resolve against that box's own axes.
    /// </remarks>
    public (Dimension X, Dimension Y)? IndividualTranslate
    {
        get => m_rare?.IndividualTranslate;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.IndividualTranslate = value;
            }
        }
    }

    public ReadOnlySpan<string?> IndividualTranslateExpressions => m_individualTranslateExpressions ?? s_noStrings2;

    /// <summary>Write one slot of <see cref="IndividualTranslateExpressions"/>, allocating only for a non-default value.</summary>
    public void SetIndividualTranslateExpression(int index, string? value) => SetSlot(ref m_individualTranslateExpressions, 2, index, value);

    private string?[]? m_individualTranslateExpressions;

    /// <summary>
    /// Individual CSS <c>rotate</c> property, in degrees. It composes after individual
    /// translate and before individual scale and <c>transform</c>.
    /// </summary>
    public float? IndividualRotate;

    /// <summary>
    /// Individual CSS <c>scale</c> property. Kept separate from <c>transform</c> so declaration
    /// order cannot accidentally overwrite either property.
    /// </summary>
    public (float X, float Y)? IndividualScale
    {
        get => m_rare?.IndividualScale;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.IndividualScale = value;
            }
        }
    }

    /// <summary>
    /// Independent CSS-property triggers that establish containing blocks for absolute and
    /// fixed descendants. Kept as a bitset so <c>filter:none</c> cannot clear a
    /// transform/containment trigger from another property.
    /// </summary>
    public ushort ContainingBlockTriggers;

    /// <summary>
    /// Authored <c>transform-origin</c>, unresolved so percentages use the final border-box
    /// dimensions. <c>null</c> is the CSS initial value, 50% 50%.
    /// </summary>
    public (Dimension X, Dimension Y)? TransformOrigin
    {
        get => m_rare?.TransformOrigin;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.TransformOrigin = value;
            }
        }
    }

    /// <summary><c>box-shadow</c> (first layer only).</summary>
    /// <remarks>
    /// Painted behind the element's own background/border box: cards, buttons, menus, and
    /// modals across the modern web rely on it for depth, and without it those elements paint
    /// flat.
    /// </remarks>
    public BoxShadow? BoxShadow
    {
        get => m_rare?.BoxShadow;
        set
        {
            if (m_rare is not null || value is not null)
            {
                Rare.BoxShadow = value;
            }
        }
    }

    /// <summary>
    /// Whether CSS box sizes compute normally but do not apply to this box's used geometry. The
    /// display value here is post-blockification, so roots, flex/grid items, floats, and
    /// out-of-flow boxes retain applicable sizes.
    /// </summary>
    internal bool IgnoresUsedBoxSizes() =>
        Display == Obscura.Render.Display.Inline && !IsInlineBlock && !IsReplacedBox;

    /// <summary>
    /// Whether the inline-size <paramref name="slot"/> (<c>0</c> width, <c>2</c> min-width,
    /// <c>4</c> max-width) currently holds a neutralized cyclic percentage.
    /// </summary>
    internal bool HasDeferredCyclicInlineSize(int slot) =>
        (DeferredCyclicInlineSlots & (1 << (slot >> 1))) != 0;

    /// <summary>Record that the inline-size <paramref name="slot"/> was neutralized.</summary>
    internal void MarkDeferredCyclicInlineSize(int slot) =>
        DeferredCyclicInlineSlots |= (byte)(1 << (slot >> 1));

    internal bool EstablishesPositioningContainingBlock() => ContainingBlockTriggers != 0;

    /// <summary>The CSS keyword a computed-style query reports for one overflow axis.</summary>
    public string ComputedOverflowCss(bool horizontal) =>
        (horizontal ? OverflowComputedX : OverflowComputedY) switch
        {
            1 => "clip",
            2 => "hidden",
            3 => "scroll",
            4 => "auto",
            _ => "visible",
        };

    internal bool ClipsOverflowX() => OverflowAxesSet ? OverflowClipX : OverflowHidden;

    internal bool ClipsOverflowY() => OverflowAxesSet ? OverflowClipY : OverflowHidden;

    /// <summary>The Rust <c>#[derive(Clone)]</c> equivalent: an independent deep copy.</summary>
    public LayoutStyle Clone()
    {
        LayoutStyle copy = (LayoutStyle)MemberwiseClone();
        copy.m_rare = m_rare?.Clone();
        copy.m_containerNames = m_containerNames is null ? null : [.. m_containerNames];
        copy.m_sizeExpressions = (string?[]?)m_sizeExpressions?.Clone();
        copy.m_marginAuto = (bool[]?)m_marginAuto?.Clone();
        copy.m_marginPercent = (float?[]?)m_marginPercent?.Clone();
        copy.m_marginRelative = (Dimension?[]?)m_marginRelative?.Clone();
        copy.m_marginExpressions = (string?[]?)m_marginExpressions?.Clone();
        copy.m_paddingPercent = (float?[]?)m_paddingPercent?.Clone();
        copy.m_paddingRelative = (Dimension?[]?)m_paddingRelative?.Clone();
        copy.m_paddingExpressions = (string?[]?)m_paddingExpressions?.Clone();
        copy.m_borderCascadeOps = m_borderCascadeOps is null ? null : [.. m_borderCascadeOps];
        copy.ClipPath = ClipPath?.Clone();

        // Null-guarded rather than unconditional: almost nothing carries a filter, and this
        // runs once per element.
        if (Filter is { } filter)
        {
            copy.Filter = (FilterFunction[])filter.Clone();
        }

        copy.BackgroundGradient = BackgroundGradient is { } linear
            ? (linear.Angle, [.. linear.Stops])
            : null;
        copy.BackgroundRadialGradient = BackgroundRadialGradient is { } radial
            ? (radial.Center, [.. radial.Stops])
            : null;
        copy.BackgroundConicGradient = BackgroundConicGradient is { } conic
            ? (conic.Angle, conic.Center, [.. conic.Stops])
            : null;
        copy.m_backgroundGradientLayers = m_backgroundGradientLayers is null
            ? null
            : [.. m_backgroundGradientLayers.Select(static l => l.DeepClone())];
        copy.m_backgroundGradientLayerRadialGeometries = m_backgroundGradientLayerRadialGeometries is null
            ? null
            : [.. m_backgroundGradientLayerRadialGeometries];
        copy.FontVariationSettings = FontVariationSettings is null ? null : [.. FontVariationSettings];
        copy.m_gridTemplateColumns = m_gridTemplateColumns is null
            ? null
            : [.. m_gridTemplateColumns.Select(static c => c.Clone())];
        copy.m_gridTemplateRows = m_gridTemplateRows is null
            ? null
            : [.. m_gridTemplateRows.Select(static c => c.Clone())];
        copy.GridCalcExpressions = GridCalcExpressions is null
            ? null
            : [.. GridCalcExpressions.Select(static bucket => new List<object>(bucket))];
        copy.m_gridAutoColumns = m_gridAutoColumns is null ? null : [.. m_gridAutoColumns];
        copy.m_gridAutoRows = m_gridAutoRows is null ? null : [.. m_gridAutoRows];
        copy.GridAreas = GridAreas is null ? null : [.. GridAreas.Select(static row => new List<string>(row))];
        copy.GridColLineNames = GridColLineNames is null
            ? null
            : new Dictionary<string, short>(GridColLineNames, StringComparer.Ordinal);
        copy.GridRowLineNames = GridRowLineNames is null
            ? null
            : new Dictionary<string, short>(GridRowLineNames, StringComparer.Ordinal);
        copy.m_inset = (Dimension?[]?)m_inset?.Clone();
        copy.m_insetExpressions = (string?[]?)m_insetExpressions?.Clone();
        copy.InsetCalc = InsetCalc is null ? null : (GridCalcExpression?[])InsetCalc.Clone();
        copy.SizeCalc = SizeCalc is null ? null : (GridCalcExpression?[])SizeCalc.Clone();
        copy.m_counterReset = m_counterReset is null ? null : [.. m_counterReset];
        copy.m_counterIncrement = m_counterIncrement is null ? null : [.. m_counterIncrement];
        copy.m_counterSet = m_counterSet is null ? null : [.. m_counterSet];
        copy.GeneratedContent = GeneratedContent is null ? null : [.. GeneratedContent];
        copy.BeforePseudo = BeforePseudo?.Clone();
        copy.AfterPseudo = AfterPseudo?.Clone();
        copy.PlaceholderPseudo = PlaceholderPseudo?.Clone();
        copy.SliderThumbPseudo = SliderThumbPseudo?.Clone();
        copy.m_transformOps = m_transformOps is null ? null : [.. m_transformOps];
        copy.WaapiSampleState = WaapiSampleState?.Clone();
        copy.m_individualTranslateExpressions = (string?[]?)m_individualTranslateExpressions?.Clone();
        return copy;
    }
}

/// <summary>Free helpers on <see cref="LayoutStyle"/> declared directly in lib.rs.</summary>
public static class LayoutStyleExtensions
{
    /// <summary>
    /// Whether this box has an inline outer display and participates in an inline formatting
    /// context. <see cref="LayoutStyle.Display"/> retains the inner layout mode for
    /// inline-flex/grid, so this cannot be inferred from <see cref="Display.Inline"/> alone.
    /// </summary>
    internal static bool IsInlineLevelBox(LayoutStyle style) =>
        style.Display == Display.Inline || style.IsInlineBlock;

    /// <summary>
    /// Apply CSS Display blockification while preserving the inner display mode. Thus
    /// inline-flex becomes flex and inline-grid becomes grid, while inline-block/plain inline
    /// become block.
    /// </summary>
    internal static void BlockifyOuterDisplay(LayoutStyle style)
    {
        if (!IsInlineLevelBox(style))
        {
            return;
        }

        if (style.Display == Display.Inline)
        {
            style.Display = Display.Block;
        }

        style.IsInlineBlock = false;
    }
}

/// <summary>
/// The inherited SVG paint properties, already serialized as CSSOM computed values.
/// </summary>
/// <remarks>
/// They are ordinary inherited CSS properties in Chromium and apply to every element, not only
/// to the SVG namespace: <c>getComputedStyle(document.body).fill</c> is <c>rgb(0, 0, 0)</c>.
/// One shared immutable instance is threaded down a subtree, so an element that specifies none
/// of them costs no allocation.
/// </remarks>
public sealed record SvgPaintValues(string Fill, string Stroke, string StrokeWidth, string TextAnchor)
{
    /// <summary>The initial values, which are what an element with no SVG paint style reports.</summary>
    public static readonly SvgPaintValues Initial = new("rgb(0, 0, 0)", "none", "1px", "start");
}

/// <summary>
/// Independent CSS-property triggers that establish a containing block for absolutely and
/// fixed-positioned descendants, as a bitset over
/// <see cref="LayoutStyle.ContainingBlockTriggers"/>.
/// </summary>
internal static class ContainingBlockTrigger
{
    public const ushort Transform = 1 << 0;
    public const ushort Filter = 1 << 1;
    public const ushort BackdropFilter = 1 << 2;
    public const ushort Perspective = 1 << 3;
    public const ushort Contain = 1 << 4;
    public const ushort WillChange = 1 << 5;
    public const ushort ContentVisibility = 1 << 6;
    public const ushort Translate = 1 << 7;
    public const ushort Rotate = 1 << 8;
    public const ushort Scale = 1 << 9;
}
