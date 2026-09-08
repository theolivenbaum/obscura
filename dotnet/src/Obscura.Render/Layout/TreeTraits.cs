// Port of vendor/taffy/src/tree/traits.rs
//
// These are the interfaces that Obscura's render tree will implement so that the
// layout algorithms can run over it. The surface is kept 1:1 with taffy's traits;
// where taffy uses generic associated types (the style types returned by the
// getters, and the child iterator) the port returns interfaces / IEnumerable.
namespace Obscura.Render.Layout;

/// <summary>
/// Taffy's abstraction for downward tree traversal.
/// </summary>
/// <remarks>
/// This trait does not require access to any nodes other than a single container node's immediate
/// children, unless <see cref="ITraverseTree"/> is also implemented.
/// </remarks>
public interface ITraversePartialTree
{
    /// <summary>Get the list of children IDs for the given node.</summary>
    IEnumerable<NodeId> ChildIds(NodeId parentNodeId);

    /// <summary>Get the number of children for the given node.</summary>
    int ChildCount(NodeId parentNodeId);

    /// <summary>Get a specific child of a node, where the index represents the nth child.</summary>
    NodeId GetChildId(NodeId parentNodeId, int childIndex);
}

/// <summary>
/// A marker interface which extends <see cref="ITraversePartialTree"/> with the guarantee that the
/// child methods can be used to recurse infinitely down the tree.
/// </summary>
public interface ITraverseTree : ITraversePartialTree;

/// <summary>
/// Any type that implements <see cref="ILayoutPartialTree"/> can be laid out using taffy's
/// algorithms.
/// </summary>
public interface ILayoutPartialTree : ITraversePartialTree
{
    /// <summary>Get the core container style for a node.</summary>
    ICoreStyle GetCoreContainerStyle(NodeId nodeId);

    /// <summary>Resolve a <c>calc()</c> value.</summary>
    float ResolveCalcValue(nuint val, float basis) => 0.0f;

    /// <summary>Set the node's unrounded layout.</summary>
    void SetUnroundedLayout(NodeId nodeId, in Layout layout);

    /// <summary>Compute the specified node's size or full layout given the specified constraints.</summary>
    LayoutOutput ComputeChildLayout(NodeId nodeId, LayoutInput inputs);
}

/// <summary>
/// Allows cached layout results to be stored and retrieved. The <see cref="Cache"/> class implements
/// a per-node cache that is compatible with this interface.
/// </summary>
public interface ICacheTree
{
    /// <summary>Try to retrieve a cached result from the cache.</summary>
    LayoutOutput? CacheGet(NodeId nodeId, in LayoutInput input);

    /// <summary>Store a computed size in the cache.</summary>
    void CacheStore(NodeId nodeId, in LayoutInput input, in LayoutOutput layoutOutput);

    /// <summary>Clear all cache entries for the node.</summary>
    void CacheClear(NodeId nodeId);
}

/// <summary>
/// Used by <see cref="Compute.RoundLayout"/>, which snaps a tree of float-valued layouts to the
/// pixel grid.
/// </summary>
public interface IRoundTree : ITraverseTree
{
    /// <summary>Get the node's unrounded layout.</summary>
    Layout GetUnroundedLayout(NodeId nodeId);

    /// <summary>Set the node's final layout.</summary>
    void SetFinalLayout(NodeId nodeId, in Layout layout);
}

/// <summary>Used by the tree printer.</summary>
public interface IPrintTree : ITraverseTree
{
    /// <summary>Get a debug label for the node.</summary>
    string GetDebugLabel(NodeId nodeId);

    /// <summary>Get the node's final layout.</summary>
    Layout GetFinalLayout(NodeId nodeId);
}

/// <summary>
/// Extends <see cref="ILayoutPartialTree"/> with getters for the styles required for Flexbox layout.
/// </summary>
public interface ILayoutFlexboxContainer : ILayoutPartialTree
{
    /// <summary>Get the container's styles.</summary>
    IFlexboxContainerStyle GetFlexboxContainerStyle(NodeId nodeId);

    /// <summary>Get the child's styles.</summary>
    IFlexboxItemStyle GetFlexboxChildStyle(NodeId childNodeId);
}

/// <summary>
/// Extends <see cref="ILayoutPartialTree"/> with getters for the styles required for CSS Grid layout.
/// </summary>
public interface ILayoutGridContainer : ILayoutPartialTree
{
    /// <summary>Get the container's styles.</summary>
    IGridContainerStyle GetGridContainerStyle(NodeId nodeId);

    /// <summary>Get the child's styles.</summary>
    IGridItemStyle GetGridChildStyle(NodeId childNodeId);

    /// <summary>Set the node's detailed grid information. Implementing this is optional.</summary>
    void SetDetailedGridInfo(NodeId nodeId, DetailedLayoutInfo detailedGridInfo)
    {
        // Default: discard.
    }
}

/// <summary>
/// Extends <see cref="ILayoutPartialTree"/> with getters for the styles required for CSS Block layout.
/// </summary>
public interface ILayoutBlockContainer : ILayoutPartialTree
{
    /// <summary>Get the container's styles.</summary>
    IBlockContainerStyle GetBlockContainerStyle(NodeId nodeId);

    /// <summary>Get the child's styles.</summary>
    IBlockItemStyle GetBlockChildStyle(NodeId childNodeId);

    /// <summary>Compute the specified node's size or full layout given the specified constraints.</summary>
    LayoutOutput ComputeBlockChildLayout(NodeId nodeId, LayoutInput inputs, BlockContext? blockCtx) =>
        ComputeChildLayout(nodeId, inputs);
}

/// <summary>
/// Convenience methods over <see cref="ILayoutPartialTree"/>. taffy declares these as the private
/// <c>LayoutPartialTreeExt</c> trait.
/// </summary>
public static class LayoutPartialTreeExt
{
    /// <summary>Compute the size of the node given the specified constraints.</summary>
    public static float MeasureChildSize(
        this ILayoutPartialTree tree,
        NodeId nodeId,
        Size<float?> knownDimensions,
        Size<float?> parentSize,
        Size<AvailableSpace> availableSpace,
        SizingMode sizingMode,
        AbsoluteAxis axis,
        Line<bool> verticalMarginsAreCollapsible) =>
        tree.ComputeChildLayout(
            nodeId,
            new LayoutInput
            {
                KnownDimensions = knownDimensions,
                ParentSize = parentSize,
                AvailableSpace = availableSpace,
                SizingMode = sizingMode,
                Axis = axis.ToRequestedAxis(),
                RunMode = RunMode.ComputeSize,
                VerticalMarginsAreCollapsible = verticalMarginsAreCollapsible,
            }).Size.GetAbs(axis);

    /// <summary>Compute the size of the node in both axes given the specified constraints.</summary>
    public static Size<float> MeasureChildSizeBoth(
        this ILayoutPartialTree tree,
        NodeId nodeId,
        Size<float?> knownDimensions,
        Size<float?> parentSize,
        Size<AvailableSpace> availableSpace,
        SizingMode sizingMode,
        Line<bool> verticalMarginsAreCollapsible) =>
        tree.ComputeChildLayout(
            nodeId,
            new LayoutInput
            {
                KnownDimensions = knownDimensions,
                ParentSize = parentSize,
                AvailableSpace = availableSpace,
                SizingMode = sizingMode,
                Axis = RequestedAxis.Both,
                RunMode = RunMode.ComputeSize,
                VerticalMarginsAreCollapsible = verticalMarginsAreCollapsible,
            }).Size;

    /// <summary>Perform a full layout on the node given the specified constraints.</summary>
    public static LayoutOutput PerformChildLayout(
        this ILayoutPartialTree tree,
        NodeId nodeId,
        Size<float?> knownDimensions,
        Size<float?> parentSize,
        Size<AvailableSpace> availableSpace,
        SizingMode sizingMode,
        Line<bool> verticalMarginsAreCollapsible) =>
        tree.ComputeChildLayout(
            nodeId,
            new LayoutInput
            {
                KnownDimensions = knownDimensions,
                ParentSize = parentSize,
                AvailableSpace = availableSpace,
                SizingMode = sizingMode,
                Axis = RequestedAxis.Both,
                RunMode = RunMode.PerformLayout,
                VerticalMarginsAreCollapsible = verticalMarginsAreCollapsible,
            });

    /// <summary>Alias for <see cref="ILayoutPartialTree.ResolveCalcValue"/> with a shorter name.</summary>
    public static float Calc(this ILayoutPartialTree tree, nuint val, float basis) =>
        tree.ResolveCalcValue(val, basis);

    /// <summary>A <see cref="CalcResolver"/> bound to this tree.</summary>
    public static CalcResolver CalcResolver(this ILayoutPartialTree tree) => tree.ResolveCalcValue;
}
