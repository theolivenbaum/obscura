// Port of the plain keyword enums from vendor/taffy/src/style/mod.rs, block.rs,
// flex.rs and float.rs.
using EFloatDirection = Obscura.Render.Layout.FloatDirection;

namespace Obscura.Render.Layout;

/// <summary>Sets the layout used for the children of this node.</summary>
public enum Display : byte
{
    /// <summary>The children will follow the block layout algorithm.</summary>
    Block,

    /// <summary>The children will follow the flexbox layout algorithm.</summary>
    Flex,

    /// <summary>The children will follow the CSS Grid layout algorithm.</summary>
    Grid,

    /// <summary>The node is hidden, and its children will also be hidden.</summary>
    None,
}

/// <summary>
/// An abstracted version of the CSS <c>display</c> property where any value other than "none" is
/// represented by "normal".
/// </summary>
public enum BoxGenerationMode : byte
{
    /// <summary>The node generates a box in the regular way.</summary>
    Normal,

    /// <summary>The node and its descendants generate no boxes (they are hidden).</summary>
    None,
}

/// <summary>The positioning strategy for this item.</summary>
public enum Position : byte
{
    /// <summary>Offset relative to the final position given by the layout algorithm.</summary>
    Relative,

    /// <summary>Offset relative to this item's closest positioned ancestor.</summary>
    Absolute,
}

/// <summary>
/// Specifies whether size styles for this node are assigned to the node's content box or border box.
/// </summary>
public enum BoxSizing : byte
{
    /// <summary>Size styles specify the box's border box.</summary>
    BorderBox,

    /// <summary>Size styles specify the box's content box.</summary>
    ContentBox,
}

/// <summary>How children overflowing their container should affect layout.</summary>
public enum Overflow : byte
{
    /// <summary>Content-based automatic minimum size; overflow contributes to the scroll region.</summary>
    Visible,

    /// <summary>Content-based automatic minimum size; overflow does not contribute.</summary>
    Clip,

    /// <summary>Automatic minimum size is 0; overflow does not contribute.</summary>
    Hidden,

    /// <summary>Automatic minimum size is 0; space is reserved for a scrollbar.</summary>
    Scroll,
}

/// <summary>Helpers over <see cref="Overflow"/>.</summary>
public static class OverflowExtensions
{
    /// <summary>Returns true for overflow modes that contain their contents.</summary>
    public static bool IsScrollContainer(this Overflow self) =>
        self is Overflow.Hidden or Overflow.Scroll;

    /// <summary>
    /// Returns 0 if the overflow mode forces the automatic minimum size of a Flexbox or Grid item
    /// to 0, else <c>null</c>.
    /// </summary>
    public static float? MaybeIntoAutomaticMinSize(this Overflow self) =>
        self.IsScrollContainer() ? 0.0f : null;
}

/// <summary>Sets the direction of text, table and grid columns, and horizontal overflow.</summary>
public enum Direction : byte
{
    /// <summary>Left-to-right.</summary>
    Ltr,

    /// <summary>Right-to-left.</summary>
    Rtl,
}

/// <summary>Helpers over <see cref="Direction"/>.</summary>
public static class DirectionExtensions
{
    /// <summary>Returns true if the direction is right-to-left.</summary>
    public static bool IsRtl(this Direction self) => self == Direction.Rtl;
}

/// <summary>
/// Used by block layout to implement the legacy behaviour of <c>&lt;center&gt;</c> and
/// <c>&lt;div align="left | right | center"&gt;</c>.
/// </summary>
public enum TextAlign : byte
{
    /// <summary>No special legacy text align behaviour.</summary>
    Auto,

    /// <summary>Corresponds to <c>-webkit-left</c> or <c>-moz-left</c> in browsers.</summary>
    LegacyLeft,

    /// <summary>Corresponds to <c>-webkit-right</c> or <c>-moz-right</c> in browsers.</summary>
    LegacyRight,

    /// <summary>Corresponds to <c>-webkit-center</c> or <c>-moz-center</c> in browsers.</summary>
    LegacyCenter,
}

/// <summary>Floats a box to the left or right. Only applies to children of a block layout.</summary>
public enum Float : byte
{
    /// <summary>The box is floated to the left.</summary>
    Left,

    /// <summary>The box is floated to the right.</summary>
    Right,

    /// <summary>The box is not floated.</summary>
    None,
}

/// <summary>Whether a definitely-floated box is floated left or right.</summary>
public enum FloatDirection : byte
{
    /// <summary>The box is floated to the left.</summary>
    Left = 0,

    /// <summary>The box is floated to the right.</summary>
    Right = 1,
}

/// <summary>Helpers over <see cref="Float"/>.</summary>
public static class FloatExtensions
{
    /// <summary>Whether the box is floated.</summary>
    public static bool IsFloated(this Float self) => self is Float.Left or Float.Right;

    /// <summary>Converts <see cref="Float"/> into a nullable <see cref="FloatDirection"/>.</summary>
    public static EFloatDirection? FloatDirection(this Float self) => self switch
    {
        Float.Left => EFloatDirection.Left,
        Float.Right => EFloatDirection.Right,
        _ => null,
    };
}

/// <summary>
/// Gives a box "clearance", moving it below floated boxes which precede it in the tree.
/// </summary>
public enum Clear : byte
{
    /// <summary>The box clears left-floated boxes.</summary>
    Left,

    /// <summary>The box clears right-floated boxes.</summary>
    Right,

    /// <summary>The box clears boxes floated in either direction.</summary>
    Both,

    /// <summary>The box does not clear floated boxes.</summary>
    None,
}

/// <summary>Controls whether flex items are forced onto one line or can wrap onto multiple lines.</summary>
public enum FlexWrap : byte
{
    /// <summary>Items will not wrap and stay on a single line.</summary>
    NoWrap,

    /// <summary>Items will wrap according to this item's <see cref="FlexDirection"/>.</summary>
    Wrap,

    /// <summary>Items will wrap in the opposite direction to this item's <see cref="FlexDirection"/>.</summary>
    WrapReverse,
}

/// <summary>The direction of the flexbox layout main axis.</summary>
public enum FlexDirection : byte
{
    /// <summary>Defines +x as the main axis.</summary>
    Row,

    /// <summary>Defines +y as the main axis.</summary>
    Column,

    /// <summary>Defines -x as the main axis.</summary>
    RowReverse,

    /// <summary>Defines -y as the main axis.</summary>
    ColumnReverse,
}

/// <summary>Helpers over <see cref="FlexDirection"/>.</summary>
public static class FlexDirectionExtensions
{
    /// <summary>Is the direction Row or RowReverse?</summary>
    public static bool IsRow(this FlexDirection self) =>
        self is FlexDirection.Row or FlexDirection.RowReverse;

    /// <summary>Is the direction Column or ColumnReverse?</summary>
    public static bool IsColumn(this FlexDirection self) =>
        self is FlexDirection.Column or FlexDirection.ColumnReverse;

    /// <summary>Is the direction RowReverse or ColumnReverse?</summary>
    public static bool IsReverse(this FlexDirection self) =>
        self is FlexDirection.RowReverse or FlexDirection.ColumnReverse;

    /// <summary>The <see cref="AbsoluteAxis"/> that corresponds to the main axis.</summary>
    public static AbsoluteAxis MainAxis(this FlexDirection self) =>
        self.IsRow() ? AbsoluteAxis.Horizontal : AbsoluteAxis.Vertical;

    /// <summary>The <see cref="AbsoluteAxis"/> that corresponds to the cross axis.</summary>
    public static AbsoluteAxis CrossAxis(this FlexDirection self) =>
        self.IsRow() ? AbsoluteAxis.Vertical : AbsoluteAxis.Horizontal;
}
