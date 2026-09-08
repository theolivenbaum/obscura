// Port of vendor/taffy/src/style/mod.rs, plus the style traits from block.rs,
// flex.rs and grid.rs.
//
// DEVIATION: taffy's `Style<S: CheapCloneStr>` is generic over the string type used
// for custom identifiers (named grid lines/areas). Obscura only ever instantiates it
// with taffy's own `DefaultCheapStr` (= String), so the port fixes S to `string`.
using EAlignContent = Obscura.Render.Layout.AlignContent;
using EAlignItems = Obscura.Render.Layout.AlignItems;
using EBoxGenerationMode = Obscura.Render.Layout.BoxGenerationMode;
using EBoxSizing = Obscura.Render.Layout.BoxSizing;
using EClear = Obscura.Render.Layout.Clear;
using EDirection = Obscura.Render.Layout.Direction;
using EDisplay = Obscura.Render.Layout.Display;
using EFlexDirection = Obscura.Render.Layout.FlexDirection;
using EFlexWrap = Obscura.Render.Layout.FlexWrap;
using EFloat = Obscura.Render.Layout.Float;
using EGridAutoFlow = Obscura.Render.Layout.GridAutoFlow;
using EGridPlacement = Obscura.Render.Layout.GridPlacement;
using EOverflow = Obscura.Render.Layout.Overflow;
using EPosition = Obscura.Render.Layout.Position;
using ETextAlign = Obscura.Render.Layout.TextAlign;

namespace Obscura.Render.Layout;

/// <summary>
/// The core set of styles that are shared between all CSS layout nodes.
/// </summary>
/// <remarks>
/// Every member has a default implementation returning taffy's default value, so an implementation
/// only needs to override the properties it actually supports.
/// </remarks>
public interface ICoreStyle
{
    /// <summary>Which box generation mode should be used.</summary>
    EBoxGenerationMode BoxGenerationMode => EBoxGenerationMode.Normal;

    /// <summary>Is block layout?</summary>
    bool IsBlock => false;

    /// <summary>
    /// Is it a compressible replaced element?
    /// <see href="https://drafts.csswg.org/css-sizing-3/#min-content-zero"/>
    /// </summary>
    bool IsCompressibleReplaced => false;

    /// <summary>
    /// Whether the preferred aspect ratio is intrinsic and therefore always applies to the content
    /// box, independently of <c>box-sizing</c>.
    /// </summary>
    bool AspectRatioUsesContentBox => false;

    /// <summary>Which box do size styles apply to.</summary>
    EBoxSizing BoxSizing => EBoxSizing.BorderBox;

    /// <summary>The direction of text, table and grid columns, and horizontal overflow.</summary>
    EDirection Direction => EDirection.Ltr;

    /// <summary>How children overflowing their container should affect layout.</summary>
    Point<EOverflow> Overflow => new(EOverflow.Visible, EOverflow.Visible);

    /// <summary>How much space should be reserved for scrollbars.</summary>
    float ScrollbarWidth => 0.0f;

    /// <summary>What should the <c>position</c> value of this struct use as a base offset?</summary>
    EPosition Position => EPosition.Relative;

    /// <summary>How should the position of this element be tweaked relative to the layout defined?</summary>
    Rect<LengthPercentageAuto> Inset => new(
        LengthPercentageAuto.Auto, LengthPercentageAuto.Auto, LengthPercentageAuto.Auto, LengthPercentageAuto.Auto);

    /// <summary>Sets the initial size of the item.</summary>
    Size<Dimension> Size => GeometryExtensions.SizeDimensionAuto;

    /// <summary>Controls the minimum size of the item.</summary>
    Size<Dimension> MinSize => GeometryExtensions.SizeDimensionAuto;

    /// <summary>Controls the maximum size of the item.</summary>
    Size<Dimension> MaxSize => GeometryExtensions.SizeDimensionAuto;

    /// <summary>Sets the preferred aspect ratio for the item (width divided by height).</summary>
    float? AspectRatio => null;

    /// <summary>How large should the margin be on each side?</summary>
    Rect<LengthPercentageAuto> Margin => new(
        LengthPercentageAuto.Zero, LengthPercentageAuto.Zero, LengthPercentageAuto.Zero, LengthPercentageAuto.Zero);

    /// <summary>How large should the padding be on each side?</summary>
    Rect<LengthPercentage> Padding => new(
        LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero);

    /// <summary>How large should the border be on each side?</summary>
    Rect<LengthPercentage> Border => new(
        LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero);
}

/// <summary>The set of styles required for a Block layout container.</summary>
public interface IBlockContainerStyle : ICoreStyle
{
    /// <summary>How items should be aligned in the inline axis.</summary>
    ETextAlign TextAlign => ETextAlign.Auto;

    /// <summary>How children of this block container are aligned in the block (cross) axis.</summary>
    EAlignContent? AlignContent => null;
}

/// <summary>The set of styles required for a Block layout item.</summary>
public interface IBlockItemStyle : ICoreStyle
{
    /// <summary>Whether the item is a table. Table children are handled specially in block layout.</summary>
    bool IsTable => false;

    /// <summary>Whether the item is floated.</summary>
    EFloat Float => EFloat.None;

    /// <summary>Whether the item clears floats.</summary>
    EClear Clear => EClear.None;
}

/// <summary>The set of styles required for a Flexbox container.</summary>
public interface IFlexboxContainerStyle : ICoreStyle
{
    /// <summary>Which direction does the main axis flow in?</summary>
    EFlexDirection FlexDirection => EFlexDirection.Row;

    /// <summary>Should elements wrap, or stay in a single line?</summary>
    EFlexWrap FlexWrap => EFlexWrap.NoWrap;

    /// <summary>How large should the gaps between items be?</summary>
    Size<LengthPercentage> Gap => GeometryExtensions.SizeLengthPercentageZero;

    /// <summary>How should content contained within this item be aligned in the cross/block axis.</summary>
    EAlignContent? AlignContent => null;

    /// <summary>How are this node's children aligned in the cross/block axis?</summary>
    EAlignItems? AlignItems => null;

    /// <summary>How should this node's children be aligned in the main axis.</summary>
    EAlignContent? JustifyContent => null;
}

/// <summary>The set of styles required for a Flexbox item.</summary>
public interface IFlexboxItemStyle : ICoreStyle
{
    /// <summary>Sets the initial main axis size of the item.</summary>
    Dimension FlexBasis => Dimension.Auto;

    /// <summary>The relative rate at which this item grows when expanding to fill space.</summary>
    float FlexGrow => 0.0f;

    /// <summary>The relative rate at which this item shrinks when contracting to fit into space.</summary>
    float FlexShrink => 1.0f;

    /// <summary>How this node should be aligned in the cross/block axis.</summary>
    EAlignItems? AlignSelf => null;
}

/// <summary>
/// The set of styles required for a CSS Grid container.
/// </summary>
/// <remarks>
/// DEVIATION: taffy exposes the track lists through associated iterator types
/// (<c>TemplateTrackList</c>, <c>AutoTrackList</c>, ...). C# has no generic associated types, so the
/// port exposes read-only lists instead. Values and ordering are unchanged.
/// </remarks>
public interface IGridContainerStyle : ICoreStyle
{
    /// <summary>Defines the track sizing functions (heights) of the grid rows.</summary>
    IReadOnlyList<GridTemplateComponent>? GridTemplateRows => null;

    /// <summary>Defines the track sizing functions (widths) of the grid columns.</summary>
    IReadOnlyList<GridTemplateComponent>? GridTemplateColumns => null;

    /// <summary>Defines the size of implicitly created rows.</summary>
    IReadOnlyList<TrackSizingFunction> GridAutoRows => [];

    /// <summary>Defines the size of implicitly created columns.</summary>
    IReadOnlyList<TrackSizingFunction> GridAutoColumns => [];

    /// <summary>Named grid areas.</summary>
    IReadOnlyList<GridTemplateArea>? GridTemplateAreas => null;

    /// <summary>Defines the line names for column lines.</summary>
    IReadOnlyList<IReadOnlyList<string>>? GridTemplateColumnNames => null;

    /// <summary>Defines the line names for row lines.</summary>
    IReadOnlyList<IReadOnlyList<string>>? GridTemplateRowNames => null;

    /// <summary>Controls how items get placed into the grid for auto-placed items.</summary>
    EGridAutoFlow GridAutoFlow => EGridAutoFlow.Row;

    /// <summary>How large should the gaps between items be?</summary>
    Size<LengthPercentage> Gap => GeometryExtensions.SizeLengthPercentageZero;

    /// <summary>How should content contained within this item be aligned in the cross/block axis.</summary>
    EAlignContent? AlignContent => null;

    /// <summary>How should content contained within this item be aligned in the main/inline axis.</summary>
    EAlignContent? JustifyContent => null;

    /// <summary>How are this node's children aligned in the cross/block axis?</summary>
    EAlignItems? AlignItems => null;

    /// <summary>How should this node's children be aligned in the inline axis?</summary>
    EAlignItems? JustifyItems => null;

    /// <summary>Get the track list for the given axis.</summary>
    IReadOnlyList<GridTemplateComponent>? GridTemplateTracks(AbsoluteAxis axis) =>
        axis == AbsoluteAxis.Horizontal ? GridTemplateColumns : GridTemplateRows;

    /// <summary>Get the container's align-content or justify-content depending on the axis passed.</summary>
    EAlignContent GridAlignContent(AbstractAxis axis) =>
        axis == AbstractAxis.Inline
            ? JustifyContent ?? EAlignContent.Stretch
            : AlignContent ?? EAlignContent.Stretch;
}

/// <summary>The set of styles required for a CSS Grid item.</summary>
public interface IGridItemStyle : ICoreStyle
{
    /// <summary>Whether descendants are ignored for intrinsic size contributions in each axis.</summary>
    Size<bool> IntrinsicSizeContainment => new(false, false);

    /// <summary>Defines which row in the grid the item should start and end at.</summary>
    Line<GridPlacement> GridRow => new(EGridPlacement.Auto, EGridPlacement.Auto);

    /// <summary>Defines which column in the grid the item should start and end at.</summary>
    Line<GridPlacement> GridColumn => new(EGridPlacement.Auto, EGridPlacement.Auto);

    /// <summary>How this node should be aligned in the cross/block axis.</summary>
    EAlignItems? AlignSelf => null;

    /// <summary>How this node should be aligned in the inline axis.</summary>
    EAlignItems? JustifySelf => null;

    /// <summary>Get a grid item's row or column placement depending on the axis passed.</summary>
    Line<GridPlacement> GetGridPlacement(AbsoluteAxis axis) =>
        axis == AbsoluteAxis.Horizontal ? GridColumn : GridRow;
}

/// <summary>
/// A typed representation of the CSS style information for a single node.
/// </summary>
public sealed class Style
    : ICoreStyle, IBlockContainerStyle, IBlockItemStyle, IFlexboxContainerStyle, IFlexboxItemStyle,
      IGridContainerStyle, IGridItemStyle, IEquatable<Style>
{
    /// <summary>What layout strategy should be used?</summary>
    public EDisplay Display { get; set; } = EDisplay.Flex;

    /// <summary>Whether a child is display:table or not. This affects children of block layouts.</summary>
    public bool ItemIsTable { get; set; }

    /// <summary>Is it a replaced element like an image or form field?</summary>
    public bool ItemIsReplaced { get; set; }

    /// <summary>Whether <see cref="AspectRatio"/> came from intrinsic media metadata.</summary>
    public bool ItemAspectRatioIsIntrinsic { get; set; }

    /// <summary>Whether descendants are ignored for intrinsic size contributions in each axis.</summary>
    public Size<bool> IntrinsicSizeContainment { get; set; } = new(false, false);

    /// <summary>Should size styles apply to the content box or the border box of the node.</summary>
    public EBoxSizing BoxSizing { get; set; } = EBoxSizing.BorderBox;

    /// <summary>Sets the direction of text, table and grid columns, and horizontal overflow.</summary>
    public EDirection Direction { get; set; } = EDirection.Ltr;

    /// <summary>How children overflowing their container should affect layout.</summary>
    public Point<EOverflow> Overflow { get; set; } = new(EOverflow.Visible, EOverflow.Visible);

    /// <summary>How much space should be reserved for scrollbars.</summary>
    public float ScrollbarWidth { get; set; }

    /// <summary>Should the box be floated.</summary>
    public EFloat Float { get; set; } = EFloat.None;

    /// <summary>Should the box clear floats.</summary>
    public EClear Clear { get; set; } = EClear.None;

    /// <summary>What should the <c>position</c> value of this struct use as a base offset?</summary>
    public EPosition Position { get; set; } = EPosition.Relative;

    /// <summary>How should the position of this element be tweaked relative to the layout defined?</summary>
    public Rect<LengthPercentageAuto> Inset { get; set; } = new(
        LengthPercentageAuto.Auto, LengthPercentageAuto.Auto, LengthPercentageAuto.Auto, LengthPercentageAuto.Auto);

    /// <summary>Sets the initial size of the item.</summary>
    public Size<Dimension> Size { get; set; } = GeometryExtensions.SizeDimensionAuto;

    /// <summary>Controls the minimum size of the item.</summary>
    public Size<Dimension> MinSize { get; set; } = GeometryExtensions.SizeDimensionAuto;

    /// <summary>Controls the maximum size of the item.</summary>
    public Size<Dimension> MaxSize { get; set; } = GeometryExtensions.SizeDimensionAuto;

    /// <summary>Sets the preferred aspect ratio for the item (width divided by height).</summary>
    public float? AspectRatio { get; set; }

    /// <summary>How large should the margin be on each side?</summary>
    public Rect<LengthPercentageAuto> Margin { get; set; } = new(
        LengthPercentageAuto.Zero, LengthPercentageAuto.Zero, LengthPercentageAuto.Zero, LengthPercentageAuto.Zero);

    /// <summary>How large should the padding be on each side?</summary>
    public Rect<LengthPercentage> Padding { get; set; } = new(
        LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero);

    /// <summary>How large should the border be on each side?</summary>
    public Rect<LengthPercentage> Border { get; set; } = new(
        LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero, LengthPercentage.Zero);

    /// <summary>How are this node's children aligned in the cross/block axis?</summary>
    public EAlignItems? AlignItems { get; set; }

    /// <summary>How this node should be aligned in the cross/block axis.</summary>
    public EAlignItems? AlignSelf { get; set; }

    /// <summary>How should this node's children be aligned in the inline axis?</summary>
    public EAlignItems? JustifyItems { get; set; }

    /// <summary>How this node should be aligned in the inline axis.</summary>
    public EAlignItems? JustifySelf { get; set; }

    /// <summary>How should content contained within this item be aligned in the cross/block axis.</summary>
    public EAlignContent? AlignContent { get; set; }

    /// <summary>How should content contained within this item be aligned in the main/inline axis.</summary>
    public EAlignContent? JustifyContent { get; set; }

    /// <summary>How large should the gaps between items in a grid or flex container be?</summary>
    public Size<LengthPercentage> Gap { get; set; } = GeometryExtensions.SizeLengthPercentageZero;

    /// <summary>How should items be aligned in the inline axis (legacy text-align).</summary>
    public ETextAlign TextAlign { get; set; } = ETextAlign.Auto;

    /// <summary>Which direction does the main axis flow in?</summary>
    public EFlexDirection FlexDirection { get; set; } = EFlexDirection.Row;

    /// <summary>Should elements wrap, or stay in a single line?</summary>
    public EFlexWrap FlexWrap { get; set; } = EFlexWrap.NoWrap;

    /// <summary>Sets the initial main axis size of the item.</summary>
    public Dimension FlexBasis { get; set; } = Dimension.Auto;

    /// <summary>The relative rate at which this item grows when expanding to fill space.</summary>
    public float FlexGrow { get; set; }

    /// <summary>The relative rate at which this item shrinks when contracting to fit into space.</summary>
    public float FlexShrink { get; set; } = 1.0f;

    /// <summary>Defines the track sizing functions (heights) of the grid rows.</summary>
    public List<GridTemplateComponent> GridTemplateRows { get; set; } = [];

    /// <summary>Defines the track sizing functions (widths) of the grid columns.</summary>
    public List<GridTemplateComponent> GridTemplateColumns { get; set; } = [];

    /// <summary>Defines the size of implicitly created rows.</summary>
    public List<TrackSizingFunction> GridAutoRows { get; set; } = [];

    /// <summary>Defines the size of implicitly created columns.</summary>
    public List<TrackSizingFunction> GridAutoColumns { get; set; } = [];

    /// <summary>Controls how items get placed into the grid for auto-placed items.</summary>
    public EGridAutoFlow GridAutoFlow { get; set; } = EGridAutoFlow.Row;

    /// <summary>Defines the rectangular grid areas.</summary>
    public List<GridTemplateArea> GridTemplateAreas { get; set; } = [];

    /// <summary>The named lines between the columns.</summary>
    public List<List<string>> GridTemplateColumnNames { get; set; } = [];

    /// <summary>The named lines between the rows.</summary>
    public List<List<string>> GridTemplateRowNames { get; set; } = [];

    /// <summary>Defines which row in the grid the item should start and end at.</summary>
    public Line<GridPlacement> GridRow { get; set; } = new(EGridPlacement.Auto, EGridPlacement.Auto);

    /// <summary>Defines which column in the grid the item should start and end at.</summary>
    public Line<GridPlacement> GridColumn { get; set; } = new(EGridPlacement.Auto, EGridPlacement.Auto);

    /// <summary>The default style, matching taffy's <c>Style::DEFAULT</c>.</summary>
    public static Style Default => new();

    // ------------------------------------------------------------- ICoreStyle

    EBoxGenerationMode ICoreStyle.BoxGenerationMode =>
        Display == EDisplay.None ? EBoxGenerationMode.None : EBoxGenerationMode.Normal;

    bool ICoreStyle.IsBlock => Display == EDisplay.Block;

    bool ICoreStyle.IsCompressibleReplaced => ItemIsReplaced;

    bool ICoreStyle.AspectRatioUsesContentBox => ItemAspectRatioIsIntrinsic;

    // -------------------------------------------------- IGridContainerStyle

    IReadOnlyList<GridTemplateComponent>? IGridContainerStyle.GridTemplateRows => GridTemplateRows;

    IReadOnlyList<GridTemplateComponent>? IGridContainerStyle.GridTemplateColumns => GridTemplateColumns;

    IReadOnlyList<TrackSizingFunction> IGridContainerStyle.GridAutoRows => GridAutoRows;

    IReadOnlyList<TrackSizingFunction> IGridContainerStyle.GridAutoColumns => GridAutoColumns;

    IReadOnlyList<GridTemplateArea>? IGridContainerStyle.GridTemplateAreas => GridTemplateAreas;

    IReadOnlyList<IReadOnlyList<string>>? IGridContainerStyle.GridTemplateColumnNames => GridTemplateColumnNames;

    IReadOnlyList<IReadOnlyList<string>>? IGridContainerStyle.GridTemplateRowNames => GridTemplateRowNames;

    // ----------------------------------------------------- IBlockItemStyle

    bool IBlockItemStyle.IsTable => ItemIsTable;

    /// <summary>Create a deep copy of this style.</summary>
    public Style Clone() => new()
    {
        Display = Display,
        ItemIsTable = ItemIsTable,
        ItemIsReplaced = ItemIsReplaced,
        ItemAspectRatioIsIntrinsic = ItemAspectRatioIsIntrinsic,
        IntrinsicSizeContainment = IntrinsicSizeContainment,
        BoxSizing = BoxSizing,
        Direction = Direction,
        Overflow = Overflow,
        ScrollbarWidth = ScrollbarWidth,
        Float = Float,
        Clear = Clear,
        Position = Position,
        Inset = Inset,
        Size = Size,
        MinSize = MinSize,
        MaxSize = MaxSize,
        AspectRatio = AspectRatio,
        Margin = Margin,
        Padding = Padding,
        Border = Border,
        AlignItems = AlignItems,
        AlignSelf = AlignSelf,
        JustifyItems = JustifyItems,
        JustifySelf = JustifySelf,
        AlignContent = AlignContent,
        JustifyContent = JustifyContent,
        Gap = Gap,
        TextAlign = TextAlign,
        FlexDirection = FlexDirection,
        FlexWrap = FlexWrap,
        FlexBasis = FlexBasis,
        FlexGrow = FlexGrow,
        FlexShrink = FlexShrink,
        GridTemplateRows = [.. GridTemplateRows.Select(static c => c.Clone())],
        GridTemplateColumns = [.. GridTemplateColumns.Select(static c => c.Clone())],
        GridAutoRows = [.. GridAutoRows],
        GridAutoColumns = [.. GridAutoColumns],
        GridAutoFlow = GridAutoFlow,
        GridTemplateAreas = [.. GridTemplateAreas],
        GridTemplateColumnNames = [.. GridTemplateColumnNames.Select(static n => new List<string>(n))],
        GridTemplateRowNames = [.. GridTemplateRowNames.Select(static n => new List<string>(n))],
        GridRow = GridRow,
        GridColumn = GridColumn,
    };

    /// <inheritdoc/>
    public bool Equals(Style? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Display == other.Display
            && ItemIsTable == other.ItemIsTable
            && ItemIsReplaced == other.ItemIsReplaced
            && ItemAspectRatioIsIntrinsic == other.ItemAspectRatioIsIntrinsic
            && IntrinsicSizeContainment == other.IntrinsicSizeContainment
            && BoxSizing == other.BoxSizing
            && Direction == other.Direction
            && Overflow == other.Overflow
            && ScrollbarWidth.Equals(other.ScrollbarWidth)
            && Float == other.Float
            && Clear == other.Clear
            && Position == other.Position
            && Inset == other.Inset
            && Size == other.Size
            && MinSize == other.MinSize
            && MaxSize == other.MaxSize
            && Nullable.Equals(AspectRatio, other.AspectRatio)
            && Margin == other.Margin
            && Padding == other.Padding
            && Border == other.Border
            && Nullable.Equals(AlignItems, other.AlignItems)
            && Nullable.Equals(AlignSelf, other.AlignSelf)
            && Nullable.Equals(JustifyItems, other.JustifyItems)
            && Nullable.Equals(JustifySelf, other.JustifySelf)
            && Nullable.Equals(AlignContent, other.AlignContent)
            && Nullable.Equals(JustifyContent, other.JustifyContent)
            && Gap == other.Gap
            && TextAlign == other.TextAlign
            && FlexDirection == other.FlexDirection
            && FlexWrap == other.FlexWrap
            && FlexBasis == other.FlexBasis
            && FlexGrow.Equals(other.FlexGrow)
            && FlexShrink.Equals(other.FlexShrink)
            && GridTemplateRows.SequenceEqual(other.GridTemplateRows)
            && GridTemplateColumns.SequenceEqual(other.GridTemplateColumns)
            && GridAutoRows.SequenceEqual(other.GridAutoRows)
            && GridAutoColumns.SequenceEqual(other.GridAutoColumns)
            && GridAutoFlow == other.GridAutoFlow
            && GridTemplateAreas.SequenceEqual(other.GridTemplateAreas)
            && NamesEqual(GridTemplateColumnNames, other.GridTemplateColumnNames)
            && NamesEqual(GridTemplateRowNames, other.GridTemplateRowNames)
            && GridRow == other.GridRow
            && GridColumn == other.GridColumn;
    }

    private static bool NamesEqual(List<List<string>> a, List<List<string>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].SequenceEqual(b[i], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Style);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Display);
        hash.Add(BoxSizing);
        hash.Add(Position);
        hash.Add(Size);
        hash.Add(MinSize);
        hash.Add(MaxSize);
        hash.Add(FlexDirection);
        hash.Add(FlexWrap);
        hash.Add(FlexGrow);
        hash.Add(FlexShrink);
        return hash.ToHashCode();
    }

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Style? a, Style? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Style? a, Style? b) => !(a == b);
}
