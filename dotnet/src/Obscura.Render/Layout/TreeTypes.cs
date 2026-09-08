// Port of vendor/taffy/src/tree/node.rs and vendor/taffy/src/tree/layout.rs
namespace Obscura.Render.Layout;

/// <summary>A type representing the id of a single node in a tree of nodes.</summary>
public readonly record struct NodeId(ulong Value)
{
    /// <summary>Create a new NodeId from a ulong value.</summary>
    public static NodeId New(ulong val) => new(val);

    /// <summary>Create a NodeId from an index.</summary>
    public static NodeId FromIndex(int index) => new((ulong)index);

    /// <summary>The raw value as an index.</summary>
    public int AsIndex() => (int)Value;

    /// <summary>Implicit conversion from a ulong.</summary>
    public static implicit operator NodeId(ulong raw) => new(raw);

    /// <summary>Implicit conversion to a ulong.</summary>
    public static implicit operator ulong(NodeId id) => id.Value;

    /// <inheritdoc/>
    public override string ToString() => $"NodeId({Value})";
}

/// <summary>Whether we are performing a full layout, or we merely need to size the node.</summary>
public enum RunMode : byte
{
    /// <summary>A full layout for this node and all children should be computed.</summary>
    PerformLayout,

    /// <summary>Only an accurate container size for the node is required.</summary>
    ComputeSize,

    /// <summary>This node should have a null layout set as it has been hidden.</summary>
    PerformHiddenLayout,
}

/// <summary>Whether styles should be taken into account when computing size.</summary>
public enum SizingMode : byte
{
    /// <summary>Only content contributions should be taken into account.</summary>
    ContentSize,

    /// <summary>Inherent size styles should be taken into account as well.</summary>
    InherentSize,
}

/// <summary>An axis that layout algorithms can be requested to compute a size for.</summary>
public enum RequestedAxis : byte
{
    /// <summary>The horizontal axis.</summary>
    Horizontal,

    /// <summary>The vertical axis.</summary>
    Vertical,

    /// <summary>Both axes.</summary>
    Both,
}

/// <summary>Conversions between <see cref="AbsoluteAxis"/> and <see cref="RequestedAxis"/>.</summary>
public static class RequestedAxisExtensions
{
    /// <summary>Widen an <see cref="AbsoluteAxis"/> into a <see cref="RequestedAxis"/>.</summary>
    public static RequestedAxis ToRequestedAxis(this AbsoluteAxis axis) =>
        axis == AbsoluteAxis.Horizontal ? RequestedAxis.Horizontal : RequestedAxis.Vertical;

    /// <summary>Narrow a <see cref="RequestedAxis"/>; returns null for <c>Both</c>.</summary>
    public static AbsoluteAxis? TryToAbsoluteAxis(this RequestedAxis axis) => axis switch
    {
        RequestedAxis.Horizontal => AbsoluteAxis.Horizontal,
        RequestedAxis.Vertical => AbsoluteAxis.Vertical,
        _ => null,
    };
}

/// <summary>
/// A set of margins that are available for collapsing with, for block layout's margin collapsing.
/// </summary>
public readonly record struct CollapsibleMarginSet(float Positive, float Negative)
{
    /// <summary>A default margin set with no collapsible margins.</summary>
    public static readonly CollapsibleMarginSet Zero = new(0.0f, 0.0f);

    /// <summary>Create a set from a single margin.</summary>
    public static CollapsibleMarginSet FromMargin(float margin) =>
        margin >= 0.0f ? new CollapsibleMarginSet(margin, 0.0f) : new CollapsibleMarginSet(0.0f, margin);

    /// <summary>Collapse a single margin with this set.</summary>
    public CollapsibleMarginSet CollapseWithMargin(float margin) =>
        margin >= 0.0f
            ? new CollapsibleMarginSet(Sys.F32Max(Positive, margin), Negative)
            : new CollapsibleMarginSet(Positive, Sys.F32Min(Negative, margin));

    /// <summary>Collapse another margin set with this set.</summary>
    public CollapsibleMarginSet CollapseWithSet(CollapsibleMarginSet other) =>
        new(Sys.F32Max(Positive, other.Positive), Sys.F32Min(Negative, other.Negative));

    /// <summary>Resolve the resultant margin from this set.</summary>
    public float Resolve() => Positive + Negative;
}

/// <summary>
/// The inputs/constraints for laying out a node, passed in by the parent.
/// </summary>
public struct LayoutInput : IEquatable<LayoutInput>
{
    /// <summary>Whether we only need to know the node's size, or need to perform a full layout.</summary>
    public RunMode RunMode;

    /// <summary>Whether a node's style sizes should be taken into account or ignored.</summary>
    public SizingMode SizingMode;

    /// <summary>Which axis we need the size of.</summary>
    public RequestedAxis Axis;

    /// <summary>Dimensions which should be taken as fixed when performing layout.</summary>
    public Size<float?> KnownDimensions;

    /// <summary>Parent size dimensions, used for percentage resolution.</summary>
    public Size<float?> ParentSize;

    /// <summary>An amount of space to lay out into; a soft constraint used for wrapping.</summary>
    public Size<AvailableSpace> AvailableSpace;

    /// <summary>Specific to CSS Block layout; used for correctly computing margin collapsing.</summary>
    public Line<bool> VerticalMarginsAreCollapsible;

    /// <summary>A <see cref="LayoutInput"/> that can be used to request hidden layout.</summary>
    public static LayoutInput Hidden => new()
    {
        RunMode = RunMode.PerformHiddenLayout,
        KnownDimensions = GeometryExtensions.SizeNone,
        ParentSize = GeometryExtensions.SizeNone,
        AvailableSpace = GeometryExtensions.SizeMaxContent,
        SizingMode = SizingMode.InherentSize,
        Axis = RequestedAxis.Both,
        VerticalMarginsAreCollapsible = GeometryExtensions.LineFalse,
    };

    /// <inheritdoc/>
    public readonly bool Equals(LayoutInput other) =>
        RunMode == other.RunMode
        && SizingMode == other.SizingMode
        && Axis == other.Axis
        && KnownDimensions == other.KnownDimensions
        && ParentSize == other.ParentSize
        && AvailableSpace == other.AvailableSpace
        && VerticalMarginsAreCollapsible == other.VerticalMarginsAreCollapsible;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is LayoutInput o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() =>
        HashCode.Combine(RunMode, SizingMode, Axis, KnownDimensions, ParentSize, AvailableSpace);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(LayoutInput a, LayoutInput b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(LayoutInput a, LayoutInput b) => !a.Equals(b);
}

/// <summary>
/// The result of laying out a single node, returned up to the parent node.
/// </summary>
public struct LayoutOutput : IEquatable<LayoutOutput>
{
    /// <summary>The size of the node.</summary>
    public Size<float> Size;

    /// <summary>The size of the content within the node.</summary>
    public Size<float> ContentSize;

    /// <summary>The first baseline of the node in each dimension, if any.</summary>
    public Point<float?> FirstBaselines;

    /// <summary>Top margin that can be collapsed with.</summary>
    public CollapsibleMarginSet TopMargin;

    /// <summary>Bottom margin that can be collapsed with.</summary>
    public CollapsibleMarginSet BottomMargin;

    /// <summary>Whether margins can be collapsed through this node.</summary>
    public bool MarginsCanCollapseThrough;

    /// <summary>An all-zero <see cref="LayoutOutput"/> for hidden nodes.</summary>
    public static LayoutOutput Hidden => new()
    {
        Size = GeometryExtensions.SizeZero,
        ContentSize = GeometryExtensions.SizeZero,
        FirstBaselines = GeometryExtensions.PointNone,
        TopMargin = CollapsibleMarginSet.Zero,
        BottomMargin = CollapsibleMarginSet.Zero,
        MarginsCanCollapseThrough = false,
    };

    /// <summary>A blank layout output.</summary>
    public static LayoutOutput Default => Hidden;

    /// <summary>Create a <see cref="LayoutOutput"/> from sizes and baselines.</summary>
    public static LayoutOutput FromSizesAndBaselines(
        Size<float> size,
        Size<float> contentSize,
        Point<float?> firstBaselines) => new()
        {
            Size = size,
            ContentSize = contentSize,
            FirstBaselines = firstBaselines,
            TopMargin = CollapsibleMarginSet.Zero,
            BottomMargin = CollapsibleMarginSet.Zero,
            MarginsCanCollapseThrough = false,
        };

    /// <summary>Create a <see cref="LayoutOutput"/> from the container and content sizes.</summary>
    public static LayoutOutput FromSizes(Size<float> size, Size<float> contentSize) =>
        FromSizesAndBaselines(size, contentSize, GeometryExtensions.PointNone);

    /// <summary>Create a <see cref="LayoutOutput"/> from just the container's size.</summary>
    public static LayoutOutput FromOuterSize(Size<float> size) =>
        FromSizes(size, GeometryExtensions.SizeZero);

    /// <inheritdoc/>
    public readonly bool Equals(LayoutOutput other) =>
        Size == other.Size
        && ContentSize == other.ContentSize
        && FirstBaselines == other.FirstBaselines
        && TopMargin == other.TopMargin
        && BottomMargin == other.BottomMargin
        && MarginsCanCollapseThrough == other.MarginsCanCollapseThrough;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is LayoutOutput o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() =>
        HashCode.Combine(Size, ContentSize, FirstBaselines, TopMargin, BottomMargin, MarginsCanCollapseThrough);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(LayoutOutput a, LayoutOutput b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(LayoutOutput a, LayoutOutput b) => !a.Equals(b);
}

/// <summary>The final result of a layout algorithm for a single node.</summary>
public struct Layout : IEquatable<Layout>
{
    /// <summary>The relative ordering of the node.</summary>
    public uint Order;

    /// <summary>The top-left corner of the node.</summary>
    public Point<float> Location;

    /// <summary>The width and height of the node.</summary>
    public Size<float> Size;

    /// <summary>The width and height of the content inside the node.</summary>
    public Size<float> ContentSize;

    /// <summary>The size of the scrollbars in each dimension.</summary>
    public Size<float> ScrollbarSize;

    /// <summary>The size of the borders of the node.</summary>
    public Rect<float> Border;

    /// <summary>The size of the padding of the node.</summary>
    public Rect<float> Padding;

    /// <summary>The size of the margin of the node.</summary>
    public Rect<float> Margin;

    /// <summary>Creates a new zero layout.</summary>
    public static Layout New() => new()
    {
        Order = 0,
        Location = GeometryExtensions.PointZero,
        Size = GeometryExtensions.SizeZero,
        ContentSize = GeometryExtensions.SizeZero,
        ScrollbarSize = GeometryExtensions.SizeZero,
        Border = GeometryExtensions.RectZero,
        Padding = GeometryExtensions.RectZero,
        Margin = GeometryExtensions.RectZero,
    };

    /// <summary>Creates a new zero layout with the supplied order value.</summary>
    public static Layout WithOrder(uint order)
    {
        var layout = New();
        layout.Order = order;
        return layout;
    }

    /// <summary>Get the width of the node's content box.</summary>
    public readonly float ContentBoxWidth() =>
        Size.Width - Padding.Left - Padding.Right - Border.Left - Border.Right;

    /// <summary>Get the height of the node's content box.</summary>
    public readonly float ContentBoxHeight() =>
        Size.Height - Padding.Top - Padding.Bottom - Border.Top - Border.Bottom;

    /// <summary>Get the size of the node's content box.</summary>
    public readonly Size<float> ContentBoxSize() => new(ContentBoxWidth(), ContentBoxHeight());

    /// <summary>Get the x offset of the node's content box relative to its parent's border box.</summary>
    public readonly float ContentBoxX() => Location.X + Border.Left + Padding.Left;

    /// <summary>Get the y offset of the node's content box relative to its parent's border box.</summary>
    public readonly float ContentBoxY() => Location.Y + Border.Top + Padding.Top;

    /// <summary>Return the scroll width of the node.</summary>
    public readonly float ScrollWidth() => Sys.F32Max(
        0.0f,
        ContentSize.Width + Sys.F32Min(ScrollbarSize.Width, Size.Width) - Size.Width + Border.Right);

    /// <summary>Return the scroll height of the node.</summary>
    public readonly float ScrollHeight() => Sys.F32Max(
        0.0f,
        ContentSize.Height + Sys.F32Min(ScrollbarSize.Height, Size.Height) - Size.Height + Border.Bottom);

    /// <inheritdoc/>
    public readonly bool Equals(Layout other) =>
        Order == other.Order
        && Location == other.Location
        && Size == other.Size
        && ContentSize == other.ContentSize
        && ScrollbarSize == other.ScrollbarSize
        && Border == other.Border
        && Padding == other.Padding
        && Margin == other.Margin;

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is Layout o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() =>
        HashCode.Combine(Order, Location, Size, ContentSize, ScrollbarSize, Border, Padding, Margin);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Layout a, Layout b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Layout a, Layout b) => !a.Equals(b);
}

/// <summary>The additional information produced by a layout algorithm.</summary>
public abstract class DetailedLayoutInfo
{
    /// <summary>For a node that has no detailed information yet.</summary>
    public static readonly DetailedLayoutInfo None = new NoneInfo();

    private sealed class NoneInfo : DetailedLayoutInfo;
}
