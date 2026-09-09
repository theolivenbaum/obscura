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

/// <summary>The subset of CSS that influences box layout. Expanded in later phases.</summary>
/// <remarks>
/// Allocated once per element, so this is a class rather than a struct. Every field initializer
/// reproduces the Rust <c>Default</c> exactly; several nested value types (
/// <see cref="Obscura.Render.BorderModel"/>, <see cref="OutlineModel"/>, <see cref="ObjectPosition"/>,
/// <see cref="AnimationTiming"/>) have non-zero Rust defaults and must be seeded from their
/// <c>Default</c> static rather than left as <c>default</c>.
/// </remarks>
public sealed class LayoutStyle
{
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
    public List<string> ContainerNames = [];

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

    /// <summary>The preferred inline size is the intrinsic <c>fit-content</c> keyword.</summary>
    /// <remarks>
    /// Taffy's box-size dimension cannot represent intrinsic sizing keywords, so <c>Width</c>
    /// remains <c>Auto</c> while the DOM layout convergence pass applies the CSS shrink-to-fit
    /// formula from min/max-content measurements.
    /// </remarks>
    public bool WidthFitContent;

    public Dimension Height;

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
    public string?[] SizeExpressions = new string?[6];

    /// <summary>
    /// <c>aspect-ratio</c> as width/height, or an image's intrinsic ratio resolved at layout.
    /// </summary>
    /// <remarks>
    /// Lets a replaced element (or a padding-box card) derive the missing dimension from the
    /// given one, so a <c>width:100%</c> image gets a real height instead of collapsing to zero.
    /// </remarks>
    public float? AspectRatio;

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
    public (float Width, float Height)? IntrinsicSize;

    /// <summary>Per-axis decoded intrinsic metadata used by the replaced sizing path.</summary>
    /// <remarks>
    /// SVG can expose only one dimension or a <c>viewBox</c> ratio, distinctions that the
    /// stable public <see cref="IntrinsicSize"/> tuple cannot represent.
    /// </remarks>
    internal ReplacedIntrinsic? ReplacedIntrinsic;

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

    public Edges Margin;

    /// <summary>Which margin sides are <c>auto</c> (top, right, bottom, left).</summary>
    /// <remarks>
    /// <c>margin: 0 auto</c> / <c>margin-inline: auto</c> centering needs a real Auto margin,
    /// which the float <see cref="Margin"/> cannot express; this flag drives it at taffy
    /// mapping.
    /// </remarks>
    public bool[] MarginAuto = new bool[4];

    /// <summary>
    /// Percentage margin per side (top, right, bottom, left) as a 0..1 fraction, <c>null</c>
    /// when the side is a fixed length.
    /// </summary>
    /// <remarks>
    /// Like padding, every side resolves against the containing block's WIDTH; the float
    /// <see cref="Margin"/> cannot carry a percentage, so this is resolved to px during the DOM
    /// layout top-down pass once the containing-block width is known.
    /// </remarks>
    public float?[] MarginPercent = new float?[4];

    /// <summary>
    /// Font- and viewport-relative margin lengths (top, right, bottom, left). These retain
    /// their unit until the top-down pass knows the element font size, root font size, and
    /// viewport dimensions.
    /// </summary>
    public Dimension?[] MarginRelative = new Dimension?[4];

    /// <summary>Deferred <c>calc()</c>/<c>min()</c>/<c>max()</c>/<c>clamp()</c> margin expressions.</summary>
    public string?[] MarginExpressions = new string?[4];

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
    public float?[] PaddingPercent = new float?[4];

    /// <summary>
    /// Font- and viewport-relative padding lengths (top, right, bottom, left), resolved
    /// alongside <see cref="MarginRelative"/> during the top-down pass.
    /// </summary>
    public Dimension?[] PaddingRelative = new Dimension?[4];

    /// <summary>Deferred <c>calc()</c>/<c>min()</c>/<c>max()</c>/<c>clamp()</c> padding expressions.</summary>
    public string?[] PaddingExpressions = new string?[4];

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
    internal BorderModel? BorderCascadeBase;

    internal List<BorderCascadeOp> BorderCascadeOps = [];

    /// <summary>
    /// Outline paint state. It deliberately has no counterpart in Taffy: outlines never
    /// contribute to box geometry.
    /// </summary>
    public OutlineModel Outline = OutlineModel.Default;

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
    public (float Angle, List<GradientStop> Stops)? BackgroundGradient;

    /// <summary>
    /// First <c>radial-gradient(...)</c> layer: center in box-relative fractions and color
    /// stops. It is painted below the first linear layer, matching the common
    /// <c>linear-gradient(...), radial-gradient(...)</c> hero pattern.
    /// </summary>
    public ((float X, float Y) Center, List<GradientStop> Stops)? BackgroundRadialGradient;

    /// <summary>
    /// Geometry paired with <see cref="BackgroundRadialGradient"/>. The legacy public tuple
    /// above remains unchanged for API compatibility.
    /// </summary>
    internal RadialGradientGeometry? BackgroundRadialGradientGeometry;

    /// <summary><c>conic-gradient(...)</c> background.</summary>
    /// <remarks>
    /// The angle is the CSS <c>from</c> angle, the center is a fraction of the border box, and
    /// stops are normalized during paint. Conic gradients commonly provide the color source for
    /// a repeated SVG mask in modern hero artwork.
    /// </remarks>
    public (float Angle, (float X, float Y) Center, List<GradientStop> Stops)? BackgroundConicGradient;

    /// <summary>
    /// Every parsed gradient in authored background-layer order. The legacy single-kind fields
    /// above remain populated for mask/text fast paths.
    /// </summary>
    public List<BackgroundGradientLayer> BackgroundGradientLayers = [];

    /// <summary>
    /// One entry per <see cref="BackgroundGradientLayers"/> item. Radial entries carry their
    /// authored ending shape; non-radial entries are <c>null</c>.
    /// </summary>
    internal List<RadialGradientGeometry?> BackgroundGradientLayerRadialGeometries = [];

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
    public (float Width, float Height)? BackgroundSize;

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
    public BackgroundPosition BackgroundPosition;

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
    public (float Width, float Height)? MaskSize;

    /// <summary>
    /// Explicit <c>(repeat-x, repeat-y)</c> choice. <c>null</c> retains the CSS default
    /// (<c>repeat</c> on both axes) when an explicit tile size exists, while preserving the
    /// legacy fill-box fallback for unsized icon masks.
    /// </summary>
    public (bool X, bool Y)? MaskRepeat;

    /// <summary>Foreground (text) color for the paint step.</summary>
    public RgbaColor? Color;

    /// <summary>Computed SVG presentation properties supplied by author CSS.</summary>
    /// <remarks>
    /// Inline SVG is serialized into a standalone document for the SVG rasterizer; without
    /// carrying these values across that boundary, stylesheet-driven icons and text lose their
    /// fill/stroke and can become completely invisible.
    /// </remarks>
    public string? SvgFill;

    public string? SvgStroke;

    public string? SvgStrokeWidth;

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
    public Dimension? FontSizeRaw;

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
    public Dimension? LetterSpacingRaw;

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

    // CSS Grid. Tracks are stored as taffy sizing functions; GridAreas is the parsed
    // `grid-template-areas` matrix (one list per row, "." for a null cell), resolved to line
    // placements on children in a later pass.
    public List<Layout.GridTemplateComponent> GridTemplateColumns = [];

    public List<Layout.GridTemplateComponent> GridTemplateRows = [];

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
    public List<Layout.TrackSizingFunction> GridAutoColumns = [];

    /// <summary>Track sizing functions for rows created outside the explicit grid.</summary>
    public List<Layout.TrackSizingFunction> GridAutoRows = [];

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

    public Layout.Line<Layout.GridPlacement>? GridColumn;

    public Layout.Line<Layout.GridPlacement>? GridRow;

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
    public (float Horizontal, float Vertical)? BorderSpacing;

    /// <summary>Computed <c>border-collapse</c>.</summary>
    /// <remarks>
    /// This property is inherited; <c>null</c> means no value was specified on this node yet
    /// and is resolved top-down before table construction. The collapsed-border conflict/paint
    /// model is still approximate, but collapsed tables must at minimum contribute no
    /// border-spacing to their geometry.
    /// </remarks>
    public bool? BorderCollapse;

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
    public Dimension?[] Inset = new Dimension?[4];

    /// <summary>Deferred functional inset expressions in top/right/bottom/left order.</summary>
    public string?[] InsetExpressions = new string?[4];

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

    internal byte OverflowSpecifiedX;

    internal byte OverflowSpecifiedY;

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

    /// <summary>
    /// <c>filter: blur(&lt;length&gt;)</c>, as the standard deviation in CSS pixels.
    /// </summary>
    /// <remarks>
    /// <c>blur()</c>'s argument <em>is</em> sigma, unlike <c>box-shadow</c>'s blur radius,
    /// which is 2 sigma. Only a blur-only filter list is recorded: a list carrying any
    /// other function stays unimplemented rather than being silently reduced to its
    /// blurs, which would paint a wrong result instead of no result.
    /// </remarks>
    public float? FilterBlur;

    /// <summary>
    /// <c>backdrop-filter: blur(&lt;length&gt;)</c>, as the standard deviation in CSS
    /// pixels. Same restriction as <see cref="FilterBlur"/>.
    /// </summary>
    public float? BackdropBlur;

    /// <summary>First CSS animation name and its timing contract.</summary>
    /// <remarks>
    /// The stylesheet sampler contributes animated opacity after normal declarations and before
    /// author <c>!important</c>, matching the animation cascade origin.
    /// </remarks>
    public string? AnimationName;

    public AnimationTiming AnimationTiming = Obscura.Render.AnimationTiming.Default;

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
    public List<CounterDirective> CounterReset = [];

    public List<CounterDirective> CounterIncrement = [];

    public List<CounterDirective> CounterSet = [];

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
    public ObjectPosition ObjectPosition = Obscura.Render.ObjectPosition.Default;

    /// <summary>
    /// Ordered operations in the non-inherited <c>transform</c> property. Length percentages
    /// remain unresolved until the final border box is known.
    /// </summary>
    public List<TransformOp> TransformOps = [];

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
    public (Dimension X, Dimension Y)? IndividualTranslate;

    public string?[] IndividualTranslateExpressions = new string?[2];

    /// <summary>
    /// Individual CSS <c>rotate</c> property, in degrees. It composes after individual
    /// translate and before individual scale and <c>transform</c>.
    /// </summary>
    public float? IndividualRotate;

    /// <summary>
    /// Individual CSS <c>scale</c> property. Kept separate from <c>transform</c> so declaration
    /// order cannot accidentally overwrite either property.
    /// </summary>
    public (float X, float Y)? IndividualScale;

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
    public (Dimension X, Dimension Y)? TransformOrigin;

    /// <summary><c>box-shadow</c> (first layer only).</summary>
    /// <remarks>
    /// Painted behind the element's own background/border box: cards, buttons, menus, and
    /// modals across the modern web rely on it for depth, and without it those elements paint
    /// flat.
    /// </remarks>
    public BoxShadow? BoxShadow;

    /// <summary>
    /// Whether CSS box sizes compute normally but do not apply to this box's used geometry. The
    /// display value here is post-blockification, so roots, flex/grid items, floats, and
    /// out-of-flow boxes retain applicable sizes.
    /// </summary>
    internal bool IgnoresUsedBoxSizes() =>
        Display == Obscura.Render.Display.Inline && !IsInlineBlock && !IsReplacedBox;

    internal bool EstablishesPositioningContainingBlock() => ContainingBlockTriggers != 0;

    internal bool ClipsOverflowX() => OverflowAxesSet ? OverflowClipX : OverflowHidden;

    internal bool ClipsOverflowY() => OverflowAxesSet ? OverflowClipY : OverflowHidden;

    /// <summary>The Rust <c>#[derive(Clone)]</c> equivalent: an independent deep copy.</summary>
    public LayoutStyle Clone()
    {
        LayoutStyle copy = (LayoutStyle)MemberwiseClone();
        copy.ContainerNames = [.. ContainerNames];
        copy.SizeExpressions = (string?[])SizeExpressions.Clone();
        copy.MarginAuto = (bool[])MarginAuto.Clone();
        copy.MarginPercent = (float?[])MarginPercent.Clone();
        copy.MarginRelative = (Dimension?[])MarginRelative.Clone();
        copy.MarginExpressions = (string?[])MarginExpressions.Clone();
        copy.PaddingPercent = (float?[])PaddingPercent.Clone();
        copy.PaddingRelative = (Dimension?[])PaddingRelative.Clone();
        copy.PaddingExpressions = (string?[])PaddingExpressions.Clone();
        copy.BorderCascadeOps = [.. BorderCascadeOps];
        copy.ClipPath = ClipPath?.Clone();
        copy.BackgroundGradient = BackgroundGradient is { } linear
            ? (linear.Angle, [.. linear.Stops])
            : null;
        copy.BackgroundRadialGradient = BackgroundRadialGradient is { } radial
            ? (radial.Center, [.. radial.Stops])
            : null;
        copy.BackgroundConicGradient = BackgroundConicGradient is { } conic
            ? (conic.Angle, conic.Center, [.. conic.Stops])
            : null;
        copy.BackgroundGradientLayers = [.. BackgroundGradientLayers.Select(static l => l.DeepClone())];
        copy.BackgroundGradientLayerRadialGeometries = [.. BackgroundGradientLayerRadialGeometries];
        copy.FontVariationSettings = FontVariationSettings is null ? null : [.. FontVariationSettings];
        copy.GridTemplateColumns = [.. GridTemplateColumns.Select(static c => c.Clone())];
        copy.GridTemplateRows = [.. GridTemplateRows.Select(static c => c.Clone())];
        copy.GridCalcExpressions = GridCalcExpressions is null
            ? null
            : [.. GridCalcExpressions.Select(static bucket => new List<object>(bucket))];
        copy.GridAutoColumns = [.. GridAutoColumns];
        copy.GridAutoRows = [.. GridAutoRows];
        copy.GridAreas = GridAreas is null ? null : [.. GridAreas.Select(static row => new List<string>(row))];
        copy.GridColLineNames = GridColLineNames is null
            ? null
            : new Dictionary<string, short>(GridColLineNames, StringComparer.Ordinal);
        copy.GridRowLineNames = GridRowLineNames is null
            ? null
            : new Dictionary<string, short>(GridRowLineNames, StringComparer.Ordinal);
        copy.Inset = (Dimension?[])Inset.Clone();
        copy.InsetExpressions = (string?[])InsetExpressions.Clone();
        copy.CounterReset = [.. CounterReset];
        copy.CounterIncrement = [.. CounterIncrement];
        copy.CounterSet = [.. CounterSet];
        copy.GeneratedContent = GeneratedContent is null ? null : [.. GeneratedContent];
        copy.BeforePseudo = BeforePseudo?.Clone();
        copy.AfterPseudo = AfterPseudo?.Clone();
        copy.PlaceholderPseudo = PlaceholderPseudo?.Clone();
        copy.TransformOps = [.. TransformOps];
        copy.WaapiSampleState = WaapiSampleState?.Clone();
        copy.IndividualTranslateExpressions = (string?[])IndividualTranslateExpressions.Clone();
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
