// Port of vendor/taffy/src/tree/taffy_tree.rs
//
// The high-level API: an owned tree of nodes plus the algorithm dispatch that
// runs flexbox / block / grid / leaf layout over it.
namespace Obscura.Render.Layout;

/// <summary>The kind of a <see cref="TaffyError"/>.</summary>
public enum TaffyErrorKind : byte
{
    /// <summary>The parent node does not have a child at the requested index.</summary>
    ChildIndexOutOfBounds,

    /// <summary>The parent node was not found in the tree.</summary>
    InvalidParentNode,

    /// <summary>The child node was not found in the tree.</summary>
    InvalidChildNode,

    /// <summary>The supplied node was not found in the tree.</summary>
    InvalidInputNode,
}

/// <summary>An error that occurs while trying to access or modify a node's children.</summary>
public sealed class TaffyError : Exception
{
    private TaffyError(TaffyErrorKind kind, string message, NodeId node, int childIndex, int childCount)
        : base(message)
    {
        Kind = kind;
        Node = node;
        ChildIndex = childIndex;
        ChildCount = childCount;
    }

    /// <summary>The kind of error.</summary>
    public TaffyErrorKind Kind { get; }

    /// <summary>The node the error relates to.</summary>
    public NodeId Node { get; }

    /// <summary>The child index that was looked up (for ChildIndexOutOfBounds).</summary>
    public int ChildIndex { get; }

    /// <summary>The total number of children the parent has (for ChildIndexOutOfBounds).</summary>
    public int ChildCount { get; }

    /// <summary>Create a ChildIndexOutOfBounds error.</summary>
    public static TaffyError ChildIndexOutOfBounds(NodeId parent, int childIndex, int childCount) =>
        new(
            TaffyErrorKind.ChildIndexOutOfBounds,
            $"Index (is {childIndex}) should be < child_count ({childCount}) for parent node {parent}",
            parent,
            childIndex,
            childCount);

    /// <summary>Create an InvalidParentNode error.</summary>
    public static TaffyError InvalidParentNode(NodeId parent) =>
        new(TaffyErrorKind.InvalidParentNode, $"Parent Node {parent} is not in the TaffyTree instance",
            parent, 0, 0);

    /// <summary>Create an InvalidChildNode error.</summary>
    public static TaffyError InvalidChildNode(NodeId child) =>
        new(TaffyErrorKind.InvalidChildNode, $"Child Node {child} is not in the TaffyTree instance",
            child, 0, 0);

    /// <summary>Create an InvalidInputNode error.</summary>
    public static TaffyError InvalidInputNode(NodeId node) =>
        new(TaffyErrorKind.InvalidInputNode, $"Supplied Node {node} is not in the TaffyTree instance",
            node, 0, 0);
}

/// <summary>
/// Measures the intrinsic size of a leaf node, with access to the node id, its context and its style.
/// </summary>
public delegate Size<float> TreeMeasureFunction<TNodeContext>(
    Size<float?> knownDimensions,
    Size<AvailableSpace> availableSpace,
    NodeId nodeId,
    TNodeContext? nodeContext,
    Style style);

/// <summary>Layout information for a given node, stored in a <see cref="TaffyTree{TNodeContext}"/>.</summary>
internal sealed class NodeData(Style style)
{
    /// <summary>The layout strategy used by this node.</summary>
    public Style Style = style;

    /// <summary>The always-unrounded results of the layout computation.</summary>
    public Layout UnroundedLayout = Layout.New();

    /// <summary>The final results of the layout computation (rounded if rounding is enabled).</summary>
    public Layout FinalLayout = Layout.New();

    /// <summary>Whether the node has context data associated with it.</summary>
    public bool HasContext;

    /// <summary>The cached results of the layout computation.</summary>
    public Cache Cache = new();

    /// <summary>The computation result from the layout algorithm.</summary>
    public DetailedLayoutInfo DetailedLayoutInfo = DetailedLayoutInfo.None;

    /// <summary>Marks a node as requiring relayout.</summary>
    public ClearState MarkDirty() => Cache.Clear();
}

/// <summary>An entire tree of UI nodes. The entry point to taffy's high-level API.</summary>
public sealed class TaffyTree<TNodeContext>
{
    private readonly SlotMap<NodeData> _nodes;
    private readonly SlotMap<List<NodeId>> _children;
    private readonly SlotMap<NodeId?> _parents;
    private readonly Dictionary<NodeId, TNodeContext> _nodeContextData = [];
    private bool _useRounding = true;
    private CalcResolver _calcResolver = static (_, _) => 0.0f;

    /// <summary>Creates a new tree with the default capacity of 16 nodes.</summary>
    public TaffyTree()
        : this(16)
    {
    }

    /// <summary>Creates a new tree that can store <paramref name="capacity"/> nodes.</summary>
    public TaffyTree(int capacity)
    {
        _nodes = new SlotMap<NodeData>(capacity);
        _children = new SlotMap<List<NodeId>>(capacity);
        _parents = new SlotMap<NodeId?>(capacity);
    }

    /// <summary>The number of node slots allocated.</summary>
    internal int NodesCapacity => _nodes.Capacity;

    /// <summary>The number of children slots allocated.</summary>
    internal int ChildrenCapacity => _children.Capacity;

    /// <summary>The number of parent slots allocated.</summary>
    internal int ParentsCapacity => _parents.Capacity;

    /// <summary>Enable rounding of layout values. Rounding is enabled by default.</summary>
    public void EnableRounding() => _useRounding = true;

    /// <summary>Disable rounding of layout values. Rounding is enabled by default.</summary>
    public void DisableRounding() => _useRounding = false;

    /// <summary>Installs the resolver used for opaque <c>calc()</c> handles.</summary>
    public void SetCalcResolver(CalcResolver resolver) => _calcResolver = resolver;

    /// <summary>Creates and adds a new unattached leaf node to the tree.</summary>
    public NodeId NewLeaf(Style layout)
    {
        var id = _nodes.Insert(new NodeData(layout));
        _ = _children.Insert([]);
        _ = _parents.Insert(null);
        return id;
    }

    /// <summary>Creates and adds a new unattached leaf node with a supplied context.</summary>
    public NodeId NewLeafWithContext(Style layout, TNodeContext context)
    {
        var data = new NodeData(layout) { HasContext = true };
        var id = _nodes.Insert(data);
        _nodeContextData[id] = context;
        _ = _children.Insert([]);
        _ = _parents.Insert(null);
        return id;
    }

    /// <summary>Creates and adds a new node, which may have any number of children.</summary>
    public NodeId NewWithChildren(Style layout, ReadOnlySpan<NodeId> children)
    {
        var id = _nodes.Insert(new NodeData(layout));

        foreach (var child in children)
        {
            _parents[child] = id;
        }

        _ = _children.Insert([.. children]);
        _ = _parents.Insert(null);
        return id;
    }

    /// <summary>Drops all nodes in the tree.</summary>
    public void Clear()
    {
        _nodes.Clear();
        _children.Clear();
        _parents.Clear();
        _nodeContextData.Clear();
    }

    /// <summary>Remove a specific node from the tree and drop it, returning its id.</summary>
    public NodeId Remove(NodeId node)
    {
        if (_parents.TryGetValue(node, out var parentSlot) && parentSlot is { } parent
            && _children.TryGetValue(parent, out var parentChildren) && parentChildren is not null)
        {
            parentChildren.RemoveAll(f => f == node);
        }

        // Remove "parent" references to a node when removing that node
        if (_children.TryGetValue(node, out var ownChildren) && ownChildren is not null)
        {
            foreach (var child in ownChildren)
            {
                _parents[child] = null;
            }
        }

        _ = _children.Remove(node);
        _ = _parents.Remove(node);
        _ = _nodes.Remove(node);
        _ = _nodeContextData.Remove(node);

        return node;
    }

    /// <summary>
    /// Sets the context data associated with the node. taffy takes an
    /// <c>Option&lt;NodeContext&gt;</c> here; C# cannot express "none" for an unconstrained value
    /// type, so clearing is <see cref="RemoveNodeContext"/>.
    /// </summary>
    public void SetNodeContext(NodeId node, TNodeContext measure)
    {
        _nodes[node].HasContext = true;
        _nodeContextData[node] = measure;
        MarkDirty(node);
    }

    /// <summary>Clears the context data associated with the node.</summary>
    public void RemoveNodeContext(NodeId node)
    {
        _nodes[node].HasContext = false;
        _ = _nodeContextData.Remove(node);
        MarkDirty(node);
    }

    /// <summary>Gets the context data associated with the node, or the default when unset.</summary>
    public TNodeContext? GetNodeContext(NodeId node) =>
        _nodeContextData.TryGetValue(node, out var value) ? value : default;

    /// <summary>Tries to get the context data associated with the node.</summary>
    public bool TryGetNodeContext(NodeId node, out TNodeContext? value) =>
        _nodeContextData.TryGetValue(node, out value);

    /// <summary>Adds a child node under the supplied parent.</summary>
    public void AddChild(NodeId parent, NodeId child)
    {
        _parents[child] = parent;
        _children[parent].Add(child);
        MarkDirty(parent);
    }

    /// <summary>Inserts a child node at the given index under the supplied parent.</summary>
    public void InsertChildAtIndex(NodeId parent, int childIndex, NodeId child)
    {
        int childCount = _children[parent].Count;
        if (childIndex > childCount)
        {
            throw TaffyError.ChildIndexOutOfBounds(parent, childIndex, childCount);
        }

        _parents[child] = parent;
        _children[parent].Insert(childIndex, child);
        MarkDirty(parent);
    }

    /// <summary>Directly sets the children of the supplied parent.</summary>
    public void SetChildren(NodeId parent, ReadOnlySpan<NodeId> children)
    {
        // Remove node as parent from all its current children.
        foreach (var child in _children[parent])
        {
            _parents[child] = null;
        }

        // Build up the relation node <-> child
        foreach (var child in children)
        {
            if (_parents[child] is { } previousParent)
            {
                RemoveChild(previousParent, child);
            }

            _parents[child] = parent;
        }

        var parentChildren = _children[parent];
        parentChildren.Clear();
        foreach (var child in children)
        {
            parentChildren.Add(child);
        }

        MarkDirty(parent);
    }

    /// <summary>Removes a child of the parent node.</summary>
    public NodeId RemoveChild(NodeId parent, NodeId child)
    {
        int index = _children[parent].IndexOf(child);
        if (index < 0)
        {
            throw TaffyError.InvalidChildNode(child);
        }

        return RemoveChildAtIndex(parent, index);
    }

    /// <summary>Removes the child at the given index from the parent.</summary>
    public NodeId RemoveChildAtIndex(NodeId parent, int childIndex)
    {
        var parentChildren = _children[parent];
        int childCount = parentChildren.Count;
        if (childIndex >= childCount)
        {
            throw TaffyError.ChildIndexOutOfBounds(parent, childIndex, childCount);
        }

        var child = parentChildren[childIndex];
        parentChildren.RemoveAt(childIndex);
        _parents[child] = null;

        MarkDirty(parent);

        return child;
    }

    /// <summary>Removes children in the given half-open range [start, endExclusive) from the parent.</summary>
    public void RemoveChildrenRange(NodeId parent, int start, int endExclusive)
    {
        var parentChildren = _children[parent];
        for (int i = start; i < endExclusive; i++)
        {
            _parents[parentChildren[i]] = null;
        }

        parentChildren.RemoveRange(start, endExclusive - start);
        MarkDirty(parent);
    }

    /// <summary>Replaces the child at the given index with a new child, returning the old child.</summary>
    public NodeId ReplaceChildAtIndex(NodeId parent, int childIndex, NodeId newChild)
    {
        var parentChildren = _children[parent];
        int childCount = parentChildren.Count;
        if (childIndex >= childCount)
        {
            throw TaffyError.ChildIndexOutOfBounds(parent, childIndex, childCount);
        }

        _parents[newChild] = parent;
        var oldChild = parentChildren[childIndex];
        parentChildren[childIndex] = newChild;
        _parents[oldChild] = null;

        MarkDirty(parent);

        return oldChild;
    }

    /// <summary>Returns the child node of the parent at the provided index.</summary>
    public NodeId ChildAtIndex(NodeId parent, int childIndex)
    {
        var parentChildren = _children[parent];
        int childCount = parentChildren.Count;
        if (childIndex >= childCount)
        {
            throw TaffyError.ChildIndexOutOfBounds(parent, childIndex, childCount);
        }

        return parentChildren[childIndex];
    }

    /// <summary>Returns the total number of nodes in the tree.</summary>
    public int TotalNodeCount() => _nodes.Count;

    /// <summary>Returns the parent of the specified node, if it has one.</summary>
    public NodeId? Parent(NodeId childId) => _parents[childId];

    /// <summary>Returns the list of children that belong to the parent node.</summary>
    public List<NodeId> Children(NodeId parent) => [.. _children[parent]];

    /// <summary>Returns the number of children of the given node.</summary>
    public int ChildCount(NodeId parent) => _children[parent].Count;

    /// <summary>Sets the style of the provided node.</summary>
    public void SetStyle(NodeId node, Style style)
    {
        _nodes[node].Style = style;
        MarkDirty(node);
    }

    /// <summary>Gets the style of the provided node.</summary>
    public Style GetStyle(NodeId node) => _nodes[node].Style;

    /// <summary>Returns this node's layout relative to its parent.</summary>
    public Layout GetLayout(NodeId node) =>
        _useRounding ? _nodes[node].FinalLayout : _nodes[node].UnroundedLayout;

    /// <summary>Returns this node's layout with unrounded values relative to its parent.</summary>
    public Layout GetUnroundedLayout(NodeId node) => _nodes[node].UnroundedLayout;

    /// <summary>Get the "detailed layout info" for a node.</summary>
    public DetailedLayoutInfo GetDetailedLayoutInfo(NodeId nodeId) => _nodes[nodeId].DetailedLayoutInfo;

    /// <summary>Marks the layout of this node and its ancestors as outdated.</summary>
    public void MarkDirty(NodeId node)
    {
        var current = node;
        while (true)
        {
            if (_nodes[current].MarkDirty() == ClearState.AlreadyEmpty)
            {
                // Node was already marked as dirty; its ancestors are dirty too.
                return;
            }

            if (!_parents.TryGetValue(current, out var parentSlot) || parentSlot is not { } parent)
            {
                return;
            }

            current = parent;
        }
    }

    /// <summary>Indicates whether the layout of this node needs to be recomputed.</summary>
    public bool IsDirty(NodeId node) => _nodes[node].Cache.IsEmpty();

    /// <summary>Updates the stored layout of the provided node and its children.</summary>
    public void ComputeLayoutWithMeasure(
        NodeId nodeId,
        Size<AvailableSpace> availableSpace,
        TreeMeasureFunction<TNodeContext> measureFunction)
    {
        bool useRounding = _useRounding;
        var view = new TaffyView(this, measureFunction);
        Compute.ComputeRootLayout(view, nodeId, availableSpace);
        if (useRounding)
        {
            Compute.RoundLayout(view, nodeId);
        }
    }

    /// <summary>Updates the stored layout of the provided node and its children.</summary>
    public void ComputeLayout(NodeId node, Size<AvailableSpace> availableSpace) =>
        ComputeLayoutWithMeasure(
            node, availableSpace, static (_, _, _, _, _) => GeometryExtensions.SizeZero);

    /// <summary>Returns a printable, layout-capable view over this tree.</summary>
    public IPrintTree AsPrintTree() =>
        new TaffyView(this, static (_, _, _, _, _) => GeometryExtensions.SizeZero);

    /// <summary>Returns a view over the tree implementing the low-level layout interfaces.</summary>
    internal TaffyView AsLayoutTree() =>
        new(this, static (_, _, _, _, _) => GeometryExtensions.SizeZero);

    /// <summary>
    /// A view over the tree that holds the measure function, so its lifetime is independent of the
    /// tree's.
    /// </summary>
    internal sealed class TaffyView(TaffyTree<TNodeContext> taffy, TreeMeasureFunction<TNodeContext> measureFunction)
        : ILayoutPartialTree, ITraverseTree, ICacheTree, IRoundTree, IPrintTree,
          ILayoutFlexboxContainer, ILayoutBlockContainer, ILayoutGridContainer
    {
        private readonly TaffyTree<TNodeContext> _taffy = taffy;
        private readonly TreeMeasureFunction<TNodeContext> _measureFunction = measureFunction;

        public IEnumerable<NodeId> ChildIds(NodeId parentNodeId) => _taffy._children[parentNodeId];

        public int ChildCount(NodeId parentNodeId) => _taffy._children[parentNodeId].Count;

        public NodeId GetChildId(NodeId parentNodeId, int childIndex) =>
            _taffy._children[parentNodeId][childIndex];

        public ICoreStyle GetCoreContainerStyle(NodeId nodeId) => _taffy._nodes[nodeId].Style;

        public float ResolveCalcValue(nuint val, float basis) => _taffy._calcResolver(val, basis);

        public void SetUnroundedLayout(NodeId nodeId, in Layout layout) =>
            _taffy._nodes[nodeId].UnroundedLayout = layout;

        public LayoutOutput ComputeChildLayout(NodeId nodeId, LayoutInput inputs) =>
            ComputeChildLayoutInner(nodeId, inputs, null);

        public LayoutOutput ComputeBlockChildLayout(NodeId nodeId, LayoutInput inputs, BlockContext? blockCtx) =>
            ComputeChildLayoutInner(nodeId, inputs, blockCtx);

        private LayoutOutput ComputeChildLayoutInner(NodeId nodeId, LayoutInput inputs, BlockContext? blockCtx)
        {
            // If RunMode is PerformHiddenLayout then an ancestor node is Display::None and this
            // node must be laid out using hidden layout regardless of its own display style.
            if (inputs.RunMode == RunMode.PerformHiddenLayout)
            {
                return Compute.ComputeHiddenLayout(this, nodeId);
            }

            return Compute.ComputeCachedLayout(
                this,
                nodeId,
                inputs,
                (tree, node, layoutInputs) =>
                {
                    var displayMode = tree._taffy._nodes[node].Style.Display;
                    bool hasChildren = tree.ChildCount(node) > 0;

                    if (displayMode == Display.None)
                    {
                        return Compute.ComputeHiddenLayout(tree, node);
                    }

                    if (hasChildren)
                    {
                        switch (displayMode)
                        {
                            case Display.Block:
                                return BlockLayout.ComputeBlockLayout(tree, node, layoutInputs, blockCtx);
                            case Display.Flex:
                                return FlexboxLayout.ComputeFlexboxLayout(tree, node, layoutInputs);
                            case Display.Grid:
                                return GridLayoutDispatch.Compute is { } computeGrid
                                    ? computeGrid(tree, node, layoutInputs)
                                    : throw new NotSupportedException(
                                        "CSS Grid layout has not been registered; "
                                        + "see GridLayoutDispatch.Compute");
                            default:
                                break;
                        }
                    }

                    var nodeData = tree._taffy._nodes[node];
                    var style = nodeData.Style;
                    TNodeContext? nodeContext = default;
                    if (nodeData.HasContext)
                    {
                        _ = tree._taffy._nodeContextData.TryGetValue(node, out nodeContext);
                    }

                    return Leaf.ComputeLeafLayout(
                        layoutInputs,
                        style,
                        static (_, _) => 0.0f,
                        (knownDimensions, availableSpace) =>
                            tree._measureFunction(knownDimensions, availableSpace, node, nodeContext, style));
                });
        }

        public LayoutOutput? CacheGet(NodeId nodeId, in LayoutInput input) =>
            _taffy._nodes[nodeId].Cache.Get(in input);

        public void CacheStore(NodeId nodeId, in LayoutInput input, in LayoutOutput layoutOutput) =>
            _taffy._nodes[nodeId].Cache.Store(in input, in layoutOutput);

        public void CacheClear(NodeId nodeId) => _taffy._nodes[nodeId].Cache.Clear();

        public Layout GetUnroundedLayout(NodeId nodeId) => _taffy._nodes[nodeId].UnroundedLayout;

        public void SetFinalLayout(NodeId nodeId, in Layout layout) =>
            _taffy._nodes[nodeId].FinalLayout = layout;

        public string GetDebugLabel(NodeId nodeId)
        {
            var node = _taffy._nodes[nodeId];
            var display = node.Style.Display;
            int numChildren = ChildCount(nodeId);

            if (display == Display.None)
            {
                return "NONE";
            }

            if (numChildren == 0)
            {
                return "LEAF";
            }

            return display switch
            {
                Display.Block => "BLOCK",
                Display.Flex => node.Style.FlexDirection is FlexDirection.Row or FlexDirection.RowReverse
                    ? "FLEX ROW"
                    : "FLEX COL",
                _ => "GRID",
            };
        }

        public Layout GetFinalLayout(NodeId nodeId) =>
            _taffy._useRounding ? _taffy._nodes[nodeId].FinalLayout : _taffy._nodes[nodeId].UnroundedLayout;

        public IFlexboxContainerStyle GetFlexboxContainerStyle(NodeId nodeId) => _taffy._nodes[nodeId].Style;

        public IFlexboxItemStyle GetFlexboxChildStyle(NodeId childNodeId) => _taffy._nodes[childNodeId].Style;

        public IBlockContainerStyle GetBlockContainerStyle(NodeId nodeId) => _taffy._nodes[nodeId].Style;

        public IBlockItemStyle GetBlockChildStyle(NodeId childNodeId) => _taffy._nodes[childNodeId].Style;

        public IGridContainerStyle GetGridContainerStyle(NodeId nodeId) => _taffy._nodes[nodeId].Style;

        public IGridItemStyle GetGridChildStyle(NodeId childNodeId) => _taffy._nodes[childNodeId].Style;

        public void SetDetailedGridInfo(NodeId nodeId, DetailedLayoutInfo detailedGridInfo) =>
            _taffy._nodes[nodeId].DetailedLayoutInfo = detailedGridInfo;
    }
}
