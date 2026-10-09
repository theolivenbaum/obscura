// Port of vendor/taffy/src/tree/taffy_tree.rs
//
// The high-level API: an owned tree of nodes plus the algorithm dispatch that
// runs flexbox / block / grid / leaf layout over it.
namespace PocketCalculator.Render.Layout;

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

/// <summary>
/// Measures an inline formatting context leaf whose line boxes flow around floats.
/// <paramref name="bands"/> are the float exclusions relative to the leaf's content box, or
/// <c>null</c> when none reaches it; <paramref name="runMode"/> tells a final layout (whose line
/// breaks paint) from a sizing pass. Not in vendor/taffy, which has no inline layout.
/// </summary>
public delegate Size<float> ExclusionMeasureFunction<TNodeContext>(
    Size<float?> knownDimensions,
    Size<AvailableSpace> availableSpace,
    NodeId nodeId,
    TNodeContext? nodeContext,
    Style style,
    FloatBands? bands,
    RunMode runMode);

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

    /// <summary>
    /// Not in vendor/taffy. The float-blind layouts of this node computed inside its parent's
    /// block formatting context in a tree with floats (see <c>FloatDependencies</c>), kept apart
    /// from <see cref="Cache"/>: the same node laid out as the root of its own formatting
    /// context (an intrinsic-size probe) answers the same inputs differently. Allocated on the
    /// first such layout.
    /// </summary>
    public Cache? BlockCache;

    /// <summary>Marks a node as requiring relayout.</summary>
    public ClearState MarkDirty()
    {
        ClearState state = Cache.Clear();
        return BlockCache is { } block && block.Clear() == ClearState.Cleared ? ClearState.Cleared : state;
    }

    /// <summary>Whether neither cache holds an entry.</summary>
    public bool CachesEmpty => Cache.IsEmpty() && (BlockCache is null || BlockCache.IsEmpty());

    // Not in vendor/taffy: cross-pass layout reuse (RetainedTaffyLayout). Every flag below
    // describes this pass only; a new tree starts with all of them clear.

    /// <summary>Whether <see cref="Cache"/> was carried over from the previous pass's tree.</summary>
    public bool Transplanted;

    /// <summary>Whether <see cref="Cache"/> was carried over at the start of this pass (diagnostics).</summary>
    public bool CarriedOver;

    /// <summary>
    /// Whether the previous pass computed this node inside a block formatting context it
    /// shared with its parent (a non-null block context), and whether it computed it outside
    /// one. Carried over with a transplanted cache, see <see cref="TaffyTree{T}.TransplantFrom"/>.
    /// </summary>
    public bool PreviousSawBlockContext;

    /// <summary>See <see cref="PreviousSawBlockContext"/>.</summary>
    public bool PreviousSawNoBlockContext;

    /// <summary>This pass asked for this node's layout with a shared block context.</summary>
    public bool SawBlockContext;

    /// <summary>This pass asked for this node's layout without a shared block context.</summary>
    public bool SawNoBlockContext;

    /// <summary>
    /// This pass asked for this node's layout in a block formatting context that had floats
    /// before or after the call, so its results may depend on boxes outside its subtree.
    /// </summary>
    public bool FloatDependent;
}

/// <summary>An entire tree of UI nodes. The entry point to taffy's high-level API.</summary>
public sealed class TaffyTree<TNodeContext>
{
    private readonly SlotMap<NodeData> _nodes;
    private readonly SlotMap<List<NodeId>> _children;
    private readonly SlotMap<NodeId?> _parents;
    private readonly Dictionary<NodeId, TNodeContext> _nodeContextData = [];
    private bool _useRounding = true;

    /// <summary>
    /// Set once any node holds a cache carried over from a previous pass. From then on an
    /// empty cache no longer implies empty ancestors, so <see cref="MarkDirty"/> walks to the
    /// root.
    /// </summary>
    private bool _hasTransplants;

    /// <summary>
    /// Whether any node in the tree is floated. Same-BFC layout then depends on the floats placed
    /// so far, which a cache key cannot see, so it is computed uncached; with no float the cache
    /// and every code path are exactly vendor/taffy's.
    /// </summary>
    public bool HasFloats { get; set; }

    /// <summary>
    /// Whether a same-BFC layout in a tree with floats may be answered from its float-blind
    /// cache (see <c>FloatDependencies</c>). Off, every such layout is computed, as it was before
    /// that cache existed; the reference a forced full relayout compares against.
    /// </summary>
    public bool FloatBlindCache { get; set; } = true;

    /// <summary>
    /// Measures leaves in a block formatting context with floats; see
    /// <see cref="ExclusionMeasureFunction{TNodeContext}"/>. <c>null</c> leaves every leaf to
    /// the ordinary measure function.
    /// </summary>
    public ExclusionMeasureFunction<TNodeContext>? ExclusionMeasure { get; set; }

    /// <summary>Forgets the exclusions a leaf's last final layout recorded.</summary>
    public Action<TNodeContext>? ExclusionReset { get; set; }

    /// <summary>Floats anchored in inline formatting context leaves; see <c>InlineFloatAnchors</c>.</summary>
    public IReadOnlyDictionary<NodeId, (NodeId Float, int Offset)[]>? FloatAnchors { get; set; }

    /// <summary>Answers <c>InlineAnchorLines</c> for a leaf's node context.</summary>
    public Func<TNodeContext, int[], (float Top, float Height, float Width, float Used)?[]>? AnchorLines { get; set; }
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
            if (_nodes[current].MarkDirty() == ClearState.AlreadyEmpty && !_hasTransplants && !HasFloats)
            {
                // Node was already marked as dirty; its ancestors are dirty too. Not so with
                // floats: a same-BFC node never stores a cache entry (ComputeChildLayoutInner),
                // so an empty cache says nothing about the BFC root above it. Nor once a previous
                // pass's caches were carried over: a display:none node's cache is always empty
                // while its transplanted ancestors' are not.
                return;
            }

            _nodes[current].Transplanted = false;

            if (!_parents.TryGetValue(current, out var parentSlot) || parentSlot is not { } parent)
            {
                return;
            }

            current = parent;
        }
    }

    /// <summary>Indicates whether the layout of this node needs to be recomputed.</summary>
    public bool IsDirty(NodeId node) => _nodes[node].CachesEmpty;

    /// <summary>
    /// Not in vendor/taffy. Carries <paramref name="sourceNode"/>'s layout cache and stored
    /// layouts over from a previous pass's tree onto <paramref name="node"/>, whose subtree the
    /// caller has proven lays out identically. The source tree is consumed: it is never laid
    /// out again, so its node keeps the moved cache rather than a fresh empty one (one
    /// allocation per carried box, every pass).
    /// </summary>
    internal void TransplantFrom(NodeId node, TaffyTree<TNodeContext> source, NodeId sourceNode)
    {
        NodeData to = _nodes[node];
        NodeData from = source._nodes[sourceNode];
        to.Cache = from.Cache;
        to.Cache.MarkCarried();
        to.BlockCache = from.BlockCache;
        to.UnroundedLayout = from.UnroundedLayout;
        to.FinalLayout = from.FinalLayout;
        to.DetailedLayoutInfo = from.DetailedLayoutInfo;
        to.Transplanted = true;
        to.CarriedOver = true;
        to.PreviousSawBlockContext = from.SawBlockContext;
        to.PreviousSawNoBlockContext = from.SawNoBlockContext;
        _hasTransplants = true;
        TransplantCount++;
    }

    /// <summary>Whether <paramref name="node"/> still holds a cache carried over from the previous pass.</summary>
    internal bool IsTransplanted(NodeId node) => _nodes[node].Transplanted;

    /// <summary>Whether <paramref name="node"/> received a carried-over cache at the start of this pass.</summary>
    internal bool WasCarriedOver(NodeId node) => _nodes[node].CarriedOver;

    /// <summary>Whether this pass's layout of <paramref name="node"/> may have seen floats.</summary>
    internal bool IsFloatDependent(NodeId node) => _nodes[node].FloatDependent;

    /// <summary>
    /// Whether this pass only ever laid <paramref name="node"/> out inside its parent's block
    /// formatting context, which a tree with floats computes uncached.
    /// </summary>
    internal bool OnlyInSharedBlockContext(NodeId node) =>
        _nodes[node].SawBlockContext && !_nodes[node].SawNoBlockContext;

    /// <summary>Whether <paramref name="node"/> holds any cached layout result.</summary>
    internal bool HasCachedLayout(NodeId node) => !_nodes[node].CachesEmpty;

    /// <summary>Whether <paramref name="node"/> still exists in this tree.</summary>
    internal bool Contains(NodeId node) => _nodes.ContainsKey(node);

    /// <summary>Whether <paramref name="node"/> has a measure context.</summary>
    internal bool HasNodeContext(NodeId node) => _nodes[node].HasContext;

    /// <summary>The children of <paramref name="parent"/>, without copying them.</summary>
    internal IReadOnlyList<NodeId> ChildrenView(NodeId parent) => _children[parent];

    /// <summary>
    /// <see cref="ChildrenView"/> as the list itself, for a hot loop that indexes it (no
    /// interface dispatch per child). The caller must not change it.
    /// </summary>
    internal List<NodeId> ChildrenList(NodeId parent) => _children[parent];

    /// <summary>Number of nodes that received a cache carried over from the previous pass.</summary>
    internal int TransplantCount { get; private set; }

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

    /// <summary>
    /// <see cref="ComputeLayoutWithMeasure"/> without the rounding pass, for a measurement whose
    /// caller reads only <see cref="GetUnroundedLayout"/> and lays the tree out again afterwards.
    /// Not in vendor/taffy: rounding walks the whole subtree, so measuring every nested table
    /// with it rounded each subtree once per ancestor, quadratic in the nesting depth.
    /// </summary>
    public void ComputeUnroundedLayoutWithMeasure(
        NodeId nodeId,
        Size<AvailableSpace> availableSpace,
        TreeMeasureFunction<TNodeContext> measureFunction) =>
        Compute.ComputeRootLayout(new TaffyView(this, measureFunction), nodeId, availableSpace);

    /// <summary>
    /// The size <see cref="ComputeUnroundedLayoutWithMeasure"/> would give the node, computed
    /// without laying it out: no stored layout changes. Not in vendor/taffy; see
    /// <see cref="Compute.MeasureRootSize"/>.
    /// </summary>
    public Size<float> MeasureUnroundedWithMeasure(
        NodeId nodeId,
        Size<AvailableSpace> availableSpace,
        TreeMeasureFunction<TNodeContext> measureFunction) =>
        Compute.MeasureRootSize(new TaffyView(this, measureFunction), nodeId, availableSpace);

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
            // The box tree's depth is bounded when it is built (BuildContext.MaxBoxDepth); this
            // turns anything that still runs the stack out into an exception the caller can
            // contain, rather than an uncatchable overflow (SECURITY.md C5).
            StackGuard.Ensure();

            // If RunMode is PerformHiddenLayout then an ancestor node is Display::None and this
            // node must be laid out using hidden layout regardless of its own display style.
            if (inputs.RunMode == RunMode.PerformHiddenLayout)
            {
                return Compute.ComputeHiddenLayout(this, nodeId);
            }

            // Not in vendor/taffy: a cache carried over from the previous pass (see
            // TaffyTree.TransplantFrom) holds results computed in one block-context mode, which
            // taffy's cache key does not know, so a request in the other mode drops the carried
            // results and computes afresh, as a pass with no carried cache would. What a carried
            // float-blind entry read of the floats is checked per entry (FloatDependencies).
            NodeData data = _taffy._nodes[nodeId];
            bool sharedContext = blockCtx is not null;
            if (data.Transplanted
                && (sharedContext ? data.PreviousSawNoBlockContext : data.PreviousSawBlockContext))
            {
                data.Transplanted = false;
                data.Cache.Clear();
                data.BlockCache?.Clear();
            }

            if (sharedContext)
            {
                data.SawBlockContext = true;
            }
            else
            {
                data.SawNoBlockContext = true;
            }

            // DEVIATION from vendor/taffy/src/tree/taffy_tree.rs, which caches a same-BFC child
            // like any other. Its layout reads, and its floats write, the BFC's shared float
            // context, so the same inputs can give a different answer: with floats in the tree
            // it is cached only when it read nothing of the floats, and only answered from
            // that cache where the floats give it the same answers (ComputeInFloatContext).
            if (blockCtx is not null && _taffy.HasFloats)
            {
                return _taffy.FloatBlindCache
                    ? ComputeInFloatContext(nodeId, data, inputs, blockCtx)
                    : ComputeUncachedInner(this, nodeId, inputs, blockCtx);
            }

            return ComputeCachedLayoutForNode(nodeId, inputs, blockCtx);
        }

        /// <summary>
        /// Not in vendor/taffy. The layout of a box in its parent's block formatting context in
        /// a tree with floats: answered from the node's float-blind cache when the floats placed
        /// so far give every question it asked the same answer (see <c>FloatDependencies</c>),
        /// computed otherwise, and cached when that computation read nothing of the floats.
        /// </summary>
        private LayoutOutput ComputeInFloatContext(NodeId nodeId, NodeData data, LayoutInput inputs, BlockContext blockCtx)
        {
            BlockFormattingContext bfc = blockCtx.FormattingContext;
            FloatDependencies? outer = bfc.Recording;

            // A leaf holding anchored floats is laid out again after each one is placed, and the
            // caller reads the lines of that very layout (InlineAnchorLines): never a cache hit.
            bool anchored = _taffy.FloatAnchors is { } anchors && anchors.ContainsKey(nodeId);
            if (!anchored
                && data.BlockCache is { } cache
                && cache.GetFloatBlind(in inputs, out float contribution, out float minY, out byte clearSides) is { } hit
                && FloatDependencies.Admits(bfc.FloatContext, blockCtx.YOffset + minY, clearSides))
            {
                blockCtx.AddChildFloatedContentHeightContribution(contribution);
                outer?.Absorb(blockCtx.YOffset + minY, clearSides, false);
                return hit;
            }

            FloatDependencies recording = new();
            bfc.Recording = recording;
            LayoutOutput output;
            try
            {
                output = ComputeUncachedInner(this, nodeId, inputs, blockCtx);
            }
            finally
            {
                bfc.Recording = outer;
            }

            bool sawFloats = recording.SawFloats || anchored;
            outer?.Absorb(recording.MinY, recording.ClearSides, sawFloats);

            // A final layout replaces the stored layouts of the whole subtree, which the final
            // entry of the other cache (laid out as its own formatting context) described.
            if (inputs.RunMode == RunMode.PerformLayout)
            {
                data.Cache.ClearFinalLayout();
            }

            if (sawFloats)
            {
                data.FloatDependent = true;
                if (inputs.RunMode == RunMode.PerformLayout)
                {
                    data.BlockCache?.ClearFinalLayout();
                }
            }
            else
            {
                (data.BlockCache ??= new Cache()).StoreFloatBlind(
                    in inputs,
                    in output,
                    blockCtx.FloatedContentHeightContribution(),
                    recording.MinY - blockCtx.YOffset,
                    recording.ClearSides);
            }

            return output;
        }

        private LayoutOutput ComputeCachedLayoutForNode(NodeId nodeId, LayoutInput inputs, BlockContext? blockCtx)
        {
            // Compute.ComputeCachedLayout, inlined so a hit on a carried-over entry can hand
            // the shared block context the float contribution its computation left behind, and
            // a computed entry can keep it (see Cache.GetCarried). Not in vendor/taffy.
            Cache cache = _taffy._nodes[nodeId].Cache;
            if (cache.GetCarried(in inputs, out float? carried) is { } hit)
            {
                if (carried is { } contribution && blockCtx is not null)
                {
                    blockCtx.AddChildFloatedContentHeightContribution(contribution);
                }

                return hit;
            }

            LayoutOutput computed = ComputeUncachedInner(this, nodeId, inputs, blockCtx);
            if (inputs.RunMode == RunMode.PerformLayout)
            {
                // See ComputeInFloatContext: the subtree's stored layouts are this computation's now.
                _taffy._nodes[nodeId].BlockCache?.ClearFinalLayout();
            }

            _taffy._nodes[nodeId].Cache.Store(
                in inputs,
                in computed,
                blockCtx?.FloatedContentHeightContribution() ?? 0f);
            return computed;
        }

        private static LayoutOutput ComputeUncachedInner(
            TaffyView tree,
            NodeId node,
            LayoutInput layoutInputs,
            BlockContext? blockCtx)
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

            if (blockCtx is not null
                && nodeData.HasContext
                && tree._taffy.ExclusionMeasure is { } exclusionMeasure)
            {
                // An inline formatting context in a BFC with floats: hand the line
                // breaker the exclusions relative to the leaf's content box.
                var calc = tree.CalcResolver();
                float basis = layoutInputs.ParentSize.Width ?? 0.0f;
                var padding = style.Padding.ResolveOrZero((float?)basis, calc);
                var border = style.Border.ResolveOrZero((float?)basis, calc);
                FloatBands? bands = blockCtx.FloatBandsFor(
                    padding.Top + border.Top,
                    padding.Left + border.Left,
                    padding.Right + border.Right);
                RunMode runMode = layoutInputs.RunMode;
                if (bands is not null)
                {
                    return Leaf.ComputeLeafLayout(
                        layoutInputs,
                        style,
                        static (_, _) => 0.0f,
                        (knownDimensions, availableSpace) => exclusionMeasure(
                            knownDimensions, availableSpace, node, nodeContext, style, bands, runMode));
                }

                // No float reaches it: the ordinary measure function (whichever this view was
                // given), after forgetting the exclusions of an earlier final layout.
                if (runMode == RunMode.PerformLayout && nodeContext is not null)
                {
                    tree._taffy.ExclusionReset?.Invoke(nodeContext);
                }
            }

            return Leaf.ComputeLeafLayout(
                layoutInputs,
                style,
                static (_, _) => 0.0f,
                (knownDimensions, availableSpace) =>
                    tree._measureFunction(knownDimensions, availableSpace, node, nodeContext, style));
        }

        public LayoutOutput? CacheGet(NodeId nodeId, in LayoutInput input) =>
            _taffy._nodes[nodeId].Cache.Get(in input);

        public void CacheStore(NodeId nodeId, in LayoutInput input, in LayoutOutput layoutOutput) =>
            _taffy._nodes[nodeId].Cache.Store(in input, in layoutOutput);

        public void CacheClear(NodeId nodeId) => _taffy._nodes[nodeId].MarkDirty();

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

        public bool HasFloats => _taffy.HasFloats;

        public (NodeId Float, int Offset)[]? InlineFloatAnchors(NodeId leaf) =>
            _taffy.FloatAnchors is { } anchors && anchors.TryGetValue(leaf, out var list) ? list : null;

        public (float Top, float Height, float Width, float Used)?[] InlineAnchorLines(NodeId leaf, int[] offsets) =>
            _taffy.AnchorLines is { } query
                && _taffy._nodes[leaf].HasContext
                && _taffy._nodeContextData.TryGetValue(leaf, out TNodeContext? context)
                && context is not null
                    ? query(context, offsets)
                    : new (float Top, float Height, float Width, float Used)?[offsets.Length];

        public IBlockItemStyle GetBlockChildStyle(NodeId childNodeId) => _taffy._nodes[childNodeId].Style;

        public IGridContainerStyle GetGridContainerStyle(NodeId nodeId) => _taffy._nodes[nodeId].Style;

        public IGridItemStyle GetGridChildStyle(NodeId childNodeId) => _taffy._nodes[childNodeId].Style;

        public void SetDetailedGridInfo(NodeId nodeId, DetailedLayoutInfo detailedGridInfo) =>
            _taffy._nodes[nodeId].DetailedLayoutInfo = detailedGridInfo;
    }
}
