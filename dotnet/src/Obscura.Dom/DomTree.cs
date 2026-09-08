namespace Obscura.Dom;

/// <summary>
/// The arena DOM tree. Nodes live in a slot vector and are addressed by <see cref="NodeId"/>;
/// the layout is index-based because op_dom and the render layer both depend on it.
/// </summary>
public sealed partial class DomTree
{
    private readonly List<Node?> _nodes = [];
    private readonly List<uint> _freeList = [];
    private readonly Dictionary<string, NodeId> _idIndex = new(StringComparer.Ordinal);

    /// <summary>
    /// Shadow roots are arena nodes with their own child list. They are kept outside the ordinary
    /// parent links so light-tree traversal never crosses into a shadow tree by accident.
    /// </summary>
    private readonly Dictionary<NodeId, ShadowRoot> _shadowRoots = [];

    private readonly Dictionary<NodeId, NodeId> _shadowRootsByHost = [];

    /// <summary>
    /// Full-document HTML parsing enables declarative shadow roots. Fragment parsing (including
    /// innerHTML) deliberately leaves this false.
    /// </summary>
    private bool _allowDeclarativeShadowRoots;

    /// <summary>
    /// Whether the document was parsed in (full) quirks mode. In quirks mode CSS class and id
    /// selectors match ASCII-case-insensitively.
    /// </summary>
    private bool _quirks;

    public DomTree()
    {
        var doc = new Node(new NodeId(0), NodeData.Document) { Connected = true };
        _nodes.Add(doc);
        Document = new NodeId(0);
    }

    public NodeId Document { get; }

    /// <summary>Record whether the document was parsed in (full) quirks mode.</summary>
    public void SetQuirks(bool quirks) => _quirks = quirks;

    /// <summary>
    /// Whether the document is in (full) quirks mode, in which CSS class and id selectors match
    /// ASCII-case-insensitively.
    /// </summary>
    public bool IsQuirks => _quirks;

    internal void SetAllowDeclarativeShadowRoots(bool allow) => _allowDeclarativeShadowRoots = allow;

    internal bool AllowsDeclarativeShadowRoots => _allowDeclarativeShadowRoots;

    /// <summary>
    /// Number of node slots (live plus freed), i.e. the same upper bound <see cref="Descendants"/>
    /// uses to cap a tree walk. A well-formed subtree has at most this many nodes, so it is a safe
    /// ceiling for iterative walkers that need a cycle backstop.
    /// </summary>
    internal int NodeSlotCount => _nodes.Count;

    private Node? Slot(NodeId id)
    {
        var index = id.Index;
        return (uint)index < (uint)_nodes.Count ? _nodes[index] : null;
    }

    // ---------------------------------------------------------------- shadow roots

    /// <summary>Create and attach a native shadow-root node to <paramref name="host"/>.</summary>
    public NodeId AttachShadowRoot(NodeId host, ShadowRootMode mode)
    {
        var error = TryAttachShadowRoot(host, mode, out var root);
        if (error != AttachShadowError.None)
        {
            throw new AttachShadowException(error);
        }

        return root;
    }

    /// <summary>
    /// Create and attach a native shadow-root node to <paramref name="host"/>, reporting failure
    /// rather than throwing. Returns <see cref="AttachShadowError.None"/> on success.
    /// </summary>
    public AttachShadowError TryAttachShadowRoot(NodeId host, ShadowRootMode mode, out NodeId root)
    {
        root = default;
        var hostNode = Slot(host);
        if (hostNode is null || !hostNode.IsElement)
        {
            return AttachShadowError.HostIsNotElement;
        }

        if (_shadowRootsByHost.ContainsKey(host))
        {
            return AttachShadowError.HostAlreadyHasShadowRoot;
        }

        var candidate = NewNode(NodeData.Document);
        // A freshly allocated document-fragment backing node satisfies every invariant below. If
        // this ever fails, remove it so the arena does not retain an unreachable allocation.
        var error = AttachShadowRootNode(host, candidate, mode);
        if (error != AttachShadowError.None)
        {
            Remove(candidate);
            return error;
        }

        root = candidate;
        return AttachShadowError.None;
    }

    /// <summary>
    /// Attach an existing detached fragment node as a shadow root. The declarative-shadow hook in
    /// the HTML parser supplies the template-contents fragment through this path.
    /// </summary>
    internal AttachShadowError AttachShadowRootNode(NodeId host, NodeId root, ShadowRootMode mode)
    {
        var hostNode = Slot(host);
        if (hostNode is null || !hostNode.IsElement)
        {
            return AttachShadowError.HostIsNotElement;
        }

        if (_shadowRootsByHost.ContainsKey(host))
        {
            return AttachShadowError.HostAlreadyHasShadowRoot;
        }

        var rootNode = Slot(root);
        var validRoot = root != Document
            && !_shadowRoots.ContainsKey(root)
            && rootNode is { Data: DocumentData, Parent: null, PrevSibling: null, NextSibling: null };
        if (!validRoot)
        {
            return AttachShadowError.InvalidShadowRoot;
        }

        _shadowRoots[root] = new ShadowRoot(root, host, mode);
        _shadowRootsByHost[host] = root;
        SetSubtreeConnected(root, hostNode.Connected);
        return AttachShadowError.None;
    }

    /// <summary>
    /// Return the native root hosted by <paramref name="host"/>, including closed roots. Web API
    /// visibility is intentionally left to the caller.
    /// </summary>
    public NodeId? ShadowRootOf(NodeId host) =>
        _shadowRootsByHost.TryGetValue(host, out var root) ? root : null;

    public ShadowRoot? ShadowRootInfo(NodeId root) =>
        _shadowRoots.TryGetValue(root, out var info) ? info : null;

    public bool IsShadowRoot(NodeId node) => _shadowRoots.ContainsKey(node);

    /// <summary>
    /// Return the root of <paramref name="node"/>'s local tree scope. This follows ordinary parent
    /// links only, so a shadow descendant resolves to its ShadowRoot and a light descendant
    /// resolves to its document or detached subtree root.
    /// </summary>
    public NodeId? TreeScopeRoot(NodeId node)
    {
        var current = node;
        for (var i = 0; i <= _nodes.Count; i++)
        {
            var currentNode = Slot(current);
            if (currentNode is null)
            {
                return null;
            }

            if (currentNode.Parent is { } parent)
            {
                current = parent;
            }
            else
            {
                return current;
            }
        }

        return null;
    }

    public NodeId? ContainingShadowRoot(NodeId node)
    {
        var root = TreeScopeRoot(node);
        return root is { } id && IsShadowRoot(id) ? id : null;
    }

    /// <summary>
    /// Return the topmost root after crossing ShadowRoot-to-host edges. This is the native
    /// counterpart of <c>getRootNode({ composed: true })</c>.
    /// </summary>
    public NodeId? ShadowIncludingRoot(NodeId node)
    {
        var current = node;
        for (var i = 0; i <= _nodes.Count; i++)
        {
            var currentNode = Slot(current);
            if (currentNode is null)
            {
                return null;
            }

            if (currentNode.Parent is { } parent)
            {
                current = parent;
            }
            else if (_shadowRoots.TryGetValue(current, out var root))
            {
                current = root.Host;
            }
            else
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Constant-time shadow-including connectivity. The bit is propagated over ordinary children
    /// and hosted shadow roots whenever a subtree moves.
    /// </summary>
    public bool IsConnected(NodeId node) => Slot(node)?.Connected ?? false;

    private void SetSubtreeConnected(NodeId root, bool connected)
    {
        // Fresh parser/framework insertions are overwhelmingly leaves. Avoid allocating traversal
        // state for the one-node case.
        var rootNode = Slot(root);
        if (rootNode is { FirstChild: null } && !_shadowRootsByHost.ContainsKey(root))
        {
            rootNode.Connected = connected;
            return;
        }

        var stack = new Stack<NodeId>();
        var seen = new HashSet<NodeId>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var nodeId = stack.Pop();
            if (!seen.Add(nodeId))
            {
                continue;
            }

            var node = Slot(nodeId);
            if (node is null)
            {
                continue;
            }

            node.Connected = connected;
            var child = node.FirstChild;
            if (_shadowRootsByHost.TryGetValue(nodeId, out var hosted))
            {
                stack.Push(hosted);
            }

            // Valid trees terminate naturally. The bound is defense in depth against a corrupt
            // sibling cycle, which must not spin here.
            for (var i = 0; i <= _nodes.Count; i++)
            {
                if (child is not { } childId)
                {
                    break;
                }

                stack.Push(childId);
                child = Slot(childId)?.NextSibling;
            }
        }
    }

    private NodeId? HostIncludingParent(NodeId node)
    {
        var parent = Slot(node)?.Parent;
        if (parent is not null)
        {
            return parent;
        }

        return _shadowRoots.TryGetValue(node, out var root) ? root.Host : null;
    }

    /// <summary>
    /// DOM insertion rejects a node when it is a host-including inclusive ancestor of the
    /// destination parent. Ordinary parent links are not enough for this check because a
    /// ShadowRoot's parent is intentionally null.
    /// </summary>
    private bool WouldCreateHostIncludingCycle(NodeId parent, NodeId child)
    {
        var childCanBeAncestor = Slot(child)?.FirstChild is not null
            || _shadowRootsByHost.ContainsKey(child);
        if (!childCanBeAncestor)
        {
            return false;
        }

        NodeId? current = parent;
        for (var i = 0; i <= _nodes.Count; i++)
        {
            if (current is not { } node)
            {
                return false;
            }

            if (node == child)
            {
                return true;
            }

            current = HostIncludingParent(node);
        }

        // A valid host-including chain cannot be longer than the arena. Refuse mutation if
        // pre-existing corruption ever violates that invariant.
        return true;
    }

    // ---------------------------------------------------------------- allocation

    public NodeId NewNode(NodeData data)
    {
        NodeId id;
        if (_freeList.Count > 0)
        {
            id = new NodeId(_freeList[^1]);
            _freeList.RemoveAt(_freeList.Count - 1);
        }
        else
        {
            id = new NodeId((uint)_nodes.Count);
            _nodes.Add(null);
        }

        if (data is ElementData element)
        {
            foreach (var attr in element.Attrs)
            {
                if (string.Equals(attr.Name.Local, "id", StringComparison.Ordinal))
                {
                    // Keep the FIRST element created with a given id. Parse order is document
                    // order, so getElementById / querySelector('#id') return the first-in-tree-order
                    // element on duplicate ids, per spec.
                    _idIndex.TryAdd(attr.Value, id);
                    break;
                }
            }
        }

        _nodes[id.Index] = new Node(id, data);
        return id;
    }

    /// <summary>The live node for <paramref name="id"/>, or null if the slot is free.</summary>
    public Node? GetNode(NodeId id) => Slot(id);

    // ---------------------------------------------------------------- mutation

    public void AppendChild(NodeId parentId, NodeId childId)
    {
        // Per DOM spec, appending a node to itself is a HierarchyRequestError; here we treat it as
        // a no-op rather than throw. Without this the sibling-pointer fixup below sets the node's
        // prev_sibling to itself and every later child-walk loops forever (same failure mode that
        // InsertBefore's self-cycle guard was added to prevent).
        if (parentId == childId)
        {
            return;
        }

        // A ShadowRoot is never itself an ordinary child, and moving a host below its own root
        // would create a cycle even though the root's parent pointer is null. Follow both ordinary
        // parents and root-to-host edges.
        var parentNode = Slot(parentId);
        var childNode = Slot(childId);
        if (parentNode is null || childNode is null || _shadowRoots.ContainsKey(childId))
        {
            return;
        }

        // A leaf which is not a shadow host cannot be an inclusive ancestor of the destination
        // parent. Detached framework tree construction appends thousands of freshly-created leaves;
        // doing a complete parent walk for each one makes a deep chain O(n^2). Non-leaves and
        // shadow hosts retain the full host-including cycle check, where reparenting really can
        // create a cycle.
        var childCanBeAncestor = childNode.FirstChild is not null
            || _shadowRootsByHost.ContainsKey(childId);
        if (childCanBeAncestor && WouldCreateHostIncludingCycle(parentId, childId))
        {
            return;
        }

        var parentConnected = parentNode.Connected;
        var childConnected = childNode.Connected;

        DetachForReparent(childId, childConnected && !parentConnected);

        var oldLast = parentNode.LastChild;

        childNode.Parent = parentId;
        childNode.PrevSibling = oldLast;
        childNode.NextSibling = null;

        if (oldLast is { } oldLastId && Slot(oldLastId) is { } oldLastNode)
        {
            oldLastNode.NextSibling = childId;
        }

        parentNode.FirstChild ??= childId;
        parentNode.LastChild = childId;

        if (parentConnected && !childConnected)
        {
            SetSubtreeConnected(childId, true);
        }
    }

    public void InsertBefore(NodeId existingId, NodeId newSiblingId)
    {
        // Per DOM spec: if the node being inserted IS the reference node, the operation is a no-op
        // (the node is already in its target position). Without this, the linked-list fixup below
        // sets the node's prev_sibling and next_sibling to itself, creating a cycle -- every later
        // traversal (childNodes, querySelectorAll, etc) then loops forever and the test page hangs
        // while obscura burns RAM.
        if (existingId == newSiblingId)
        {
            return;
        }

        if (Slot(existingId)?.Parent is not { } parentId)
        {
            return;
        }

        var parentNode = Slot(parentId);
        var parentConnected = parentNode?.Connected ?? false;

        // Apply the same host-including cycle and root-node constraints as AppendChild. A leaf host
        // still needs this check because its hosted root is not present in the ordinary child list.
        if (Slot(newSiblingId) is null
            || _shadowRoots.ContainsKey(newSiblingId)
            || WouldCreateHostIncludingCycle(parentId, newSiblingId))
        {
            return;
        }

        var childConnected = IsConnected(newSiblingId);
        DetachForReparent(newSiblingId, childConnected && !parentConnected);

        // Read existing's prev AFTER detaching new. If new was existing's immediate previous
        // sibling, detach moved that pointer; using the pre-detach value would splice
        // new.next_sibling = new (a self-cycle) and hang every later sibling walk. This is what
        // hung ebay.com.
        var prevId = Slot(existingId)?.PrevSibling;

        if (Slot(newSiblingId) is { } newNode)
        {
            newNode.Parent = parentId;
            newNode.PrevSibling = prevId;
            newNode.NextSibling = existingId;
        }

        if (Slot(existingId) is { } existingNode)
        {
            existingNode.PrevSibling = newSiblingId;
        }

        if (prevId is { } prev)
        {
            if (Slot(prev) is { } prevNode)
            {
                prevNode.NextSibling = newSiblingId;
            }
        }
        else if (parentNode is not null)
        {
            parentNode.FirstChild = newSiblingId;
        }

        if (parentConnected && !childConnected)
        {
            SetSubtreeConnected(newSiblingId, true);
        }
    }

    public void Detach(NodeId nodeId) => DetachForReparent(nodeId, true);

    private void DetachForReparent(NodeId nodeId, bool disconnect)
    {
        // The document and registered ShadowRoots have no ordinary parent and cannot be detached
        // through light-tree mutation APIs.
        if (nodeId == Document || _shadowRoots.ContainsKey(nodeId))
        {
            return;
        }

        var node = Slot(nodeId);
        if (node is null)
        {
            return;
        }

        var parentId = node.Parent;
        var prevId = node.PrevSibling;
        var nextId = node.NextSibling;

        if (disconnect && node.Connected)
        {
            SetSubtreeConnected(nodeId, false);
        }

        if (prevId is { } prev)
        {
            if (Slot(prev) is { } prevNode)
            {
                prevNode.NextSibling = nextId;
            }
        }
        else if (parentId is { } parentA && Slot(parentA) is { } parentNodeA)
        {
            parentNodeA.FirstChild = nextId;
        }

        if (nextId is { } next)
        {
            if (Slot(next) is { } nextNode)
            {
                nextNode.PrevSibling = prevId;
            }
        }
        else if (parentId is { } parentB && Slot(parentB) is { } parentNodeB)
        {
            parentNodeB.LastChild = prevId;
        }

        node.Parent = null;
        node.PrevSibling = null;
        node.NextSibling = null;
    }

    /// <summary>
    /// Detach a node from its parent AND remove it (and all descendants) from the id-index so that
    /// getElementById no longer returns them. Unlike <see cref="Remove"/>, this does NOT free the
    /// nodes: the JS side may still hold references to the wrappers.
    /// </summary>
    public void RemoveChild(NodeId nodeId)
    {
        // Collect all id attribute values in the subtree. We snapshot them before detaching so
        // GetAttribute can still see the tree.
        var idsToRemove = new List<string>();
        if (Slot(nodeId)?.GetAttribute("id") is { } ownId)
        {
            idsToRemove.Add(ownId);
        }

        foreach (var descId in Descendants(nodeId))
        {
            if (Slot(descId)?.GetAttribute("id") is { } descIdValue)
            {
                idsToRemove.Add(descIdValue);
            }
        }

        Detach(nodeId);

        foreach (var id in idsToRemove)
        {
            _idIndex.Remove(id);
        }
    }

    public void Remove(NodeId nodeId)
    {
        var nodesToRemove = InclusiveOwnedSubtrees(nodeId);
        if (nodesToRemove.Count == 0)
        {
            return;
        }

        Detach(nodeId);

        var idsToRemove = new List<string>();
        foreach (var id in nodesToRemove)
        {
            if (Slot(id)?.GetAttribute("id") is { } value)
            {
                idsToRemove.Add(value);
            }
        }

        foreach (var id in idsToRemove)
        {
            _idIndex.Remove(id);
        }

        // Remove both directions before freeing any arena slot. Otherwise a reused NodeId could
        // inherit an old host/root relationship.
        foreach (var id in nodesToRemove)
        {
            if (_shadowRoots.Remove(id, out var root))
            {
                if (_shadowRootsByHost.TryGetValue(root.Host, out var hosted) && hosted == id)
                {
                    _shadowRootsByHost.Remove(root.Host);
                }
            }

            if (_shadowRootsByHost.Remove(id, out var rootId))
            {
                _shadowRoots.Remove(rootId);
            }
        }

        // Only free slots that are currently live. Freeing an out-of-range id would throw on direct
        // indexing, and freeing an already-freed slot would push it onto the free list a second
        // time -- later handing the same NodeId to two live nodes (aliasing).
        foreach (var id in nodesToRemove)
        {
            if (Slot(id) is not null)
            {
                _nodes[id.Index] = null;
                _freeList.Add(id.Value);
            }
        }
    }

    /// <summary>
    /// Collect an ordinary subtree plus every shadow tree owned by a host in that subtree. This is
    /// used only by the arena-freeing path; normal DOM traversal must remain tree-scoped and
    /// therefore never follows host edges.
    /// </summary>
    private List<NodeId> InclusiveOwnedSubtrees(NodeId nodeId)
    {
        var result = new List<NodeId>();
        if (Slot(nodeId) is null)
        {
            return result;
        }

        var seen = new HashSet<NodeId>();
        var stack = new Stack<NodeId>();
        stack.Push(nodeId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            var node = Slot(current);
            if (node is null)
            {
                continue;
            }

            result.Add(current);

            if (_shadowRootsByHost.TryGetValue(current, out var hosted))
            {
                stack.Push(hosted);
            }

            var children = new List<NodeId>();
            var child = node.FirstChild;
            while (child is { } childId)
            {
                children.Add(childId);
                if (children.Count > _nodes.Count)
                {
                    break;
                }

                child = Slot(childId)?.NextSibling;
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- traversal

    public List<NodeId> Children(NodeId nodeId)
    {
        var result = new List<NodeId>();
        var current = Slot(nodeId)?.FirstChild;
        while (current is { } childId)
        {
            result.Add(childId);
            // Defense in depth: a valid sibling chain is at most nodes.Count long. Exceeding that
            // means NextSibling forms a cycle (which the AppendChild / InsertBefore guards
            // prevent); stop rather than loop forever. On a valid tree this bound is never reached.
            if (result.Count > _nodes.Count)
            {
                break;
            }

            current = Slot(childId)?.NextSibling;
        }

        return result;
    }

    /// <summary>
    /// Snapshot the direct children of <paramref name="host"/>'s shadow root. Ordinary
    /// <see cref="Children"/> continues to return only light children.
    /// </summary>
    public List<NodeId>? ShadowChildren(NodeId host) =>
        ShadowRootOf(host) is { } root ? Children(root) : null;

    public List<NodeId> Descendants(NodeId nodeId)
    {
        var result = new List<NodeId>();
        var stack = new Stack<NodeId>();

        PushChildrenReversed(nodeId, stack);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            result.Add(current);
            // Defense in depth: a well-formed subtree has at most nodes.Count descendants.
            // Exceeding that means the parent/child graph is cyclic (which the AppendChild /
            // InsertBefore guards prevent); stop rather than grow the stack and result forever and
            // wedge the engine. On a valid tree this bound is never reached, so the hot path is
            // unchanged.
            if (result.Count > _nodes.Count)
            {
                Console.Error.WriteLine(
                    $"obscura: descendants() cap hit at node {nodeId.Index} ({_nodes.Count} nodes) - tree has a cycle");
                break;
            }

            PushChildrenReversed(current, stack);
        }

        return result;
    }

    private void PushChildrenReversed(NodeId nodeId, Stack<NodeId> stack)
    {
        var childrenToPush = new List<NodeId>();
        var child = Slot(nodeId)?.FirstChild;
        while (child is { } childId)
        {
            childrenToPush.Add(childId);
            if (childrenToPush.Count > _nodes.Count)
            {
                Console.Error.WriteLine($"obscura: sibling-chain cap hit at node {nodeId.Index} - cycle");
                break;
            }

            child = Slot(childId)?.NextSibling;
        }

        for (var i = childrenToPush.Count - 1; i >= 0; i--)
        {
            stack.Push(childrenToPush[i]);
        }
    }

    /// <summary>
    /// Snapshot the descendants of <paramref name="host"/>'s shadow tree without including the
    /// ShadowRoot node itself.
    /// </summary>
    public List<NodeId>? ShadowDescendants(NodeId host) =>
        ShadowRootOf(host) is { } root ? Descendants(root) : null;

    /// <summary>
    /// Whether <paramref name="node"/> is an HTML <c>&lt;slot&gt;</c> element. Slot assignment is
    /// defined only for HTML slots; same-local-name elements in other namespaces do not participate
    /// in the flattened tree.
    /// </summary>
    public bool IsHtmlSlotElement(NodeId node) =>
        Slot(node)?.ElementName is { } name
        && string.Equals(name.Ns, Namespaces.Html, StringComparison.Ordinal)
        && string.Equals(name.Local, "slot", StringComparison.Ordinal);

    /// <summary>
    /// Return the first slot to which <paramref name="node"/> is assigned.
    ///
    /// The node must be a direct light child of a shadow host. Element slot names and slot
    /// <c>name</c> values compare as exact strings; text nodes use the empty/default name. The
    /// first same-name slot in shadow-tree order wins, matching the HTML slot assignment algorithm.
    /// </summary>
    public NodeId? AssignedSlot(NodeId node)
    {
        var nodeRef = Slot(node);
        if (nodeRef?.Parent is not { } parent)
        {
            return null;
        }

        string name;
        if (nodeRef.IsElement)
        {
            name = nodeRef.GetAttribute("slot") ?? "";
        }
        else if (nodeRef.TextContentOfTextNode is not null)
        {
            name = "";
        }
        else
        {
            return null;
        }

        if (ShadowRootOf(parent) is not { } root)
        {
            return null;
        }

        foreach (var candidate in Descendants(root))
        {
            if (IsHtmlSlotElement(candidate)
                && string.Equals(Slot(candidate)?.GetAttribute("name") ?? "", name, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Nodes directly assigned to an HTML slot. The first same-name slot wins; later duplicate
    /// slots and slots with no matching light children return an empty list. Null means
    /// <paramref name="slot"/> is not a slot in a shadow tree.
    /// </summary>
    public List<NodeId>? AssignedNodes(NodeId slot)
    {
        if (!IsHtmlSlotElement(slot))
        {
            return null;
        }

        if (ContainingShadowRoot(slot) is not { } root || ShadowRootInfo(root) is not { } info)
        {
            return null;
        }

        var host = info.Host;
        var name = Slot(slot)?.GetAttribute("name") ?? "";

        bool IsSameNameSlot(NodeId candidate) =>
            IsHtmlSlotElement(candidate)
            && string.Equals(Slot(candidate)?.GetAttribute("name") ?? "", name, StringComparison.Ordinal);

        foreach (var candidate in Descendants(root))
        {
            if (candidate == slot)
            {
                break;
            }

            if (IsSameNameSlot(candidate))
            {
                return [];
            }
        }

        var result = new List<NodeId>();
        foreach (var candidate in Children(host))
        {
            var node = Slot(candidate);
            if (node is null)
            {
                continue;
            }

            string candidateName;
            if (node.IsElement)
            {
                candidateName = node.GetAttribute("slot") ?? "";
            }
            else if (node.TextContentOfTextNode is not null)
            {
                candidateName = "";
            }
            else
            {
                continue;
            }

            if (string.Equals(candidateName, name, StringComparison.Ordinal))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// Flattened children for an HTML slot. Assigned nodes replace fallback children; an unassigned
    /// slot exposes its ordinary child list.
    /// </summary>
    public List<NodeId>? SlotRenderedChildren(NodeId slot)
    {
        var assigned = AssignedNodes(slot);
        if (assigned is null)
        {
            return null;
        }

        return assigned.Count == 0 ? Children(slot) : assigned;
    }

    /// <summary>
    /// Returns the node after <paramref name="current"/> in document order, without leaving the
    /// subtree rooted at <paramref name="root"/>.
    ///
    /// Keeping the ancestor climb inside the DOM avoids one JS/native crossing per ancestor when a
    /// TreeWalker reaches a deep leaf.
    /// </summary>
    public NodeId? NextInSubtree(NodeId root, NodeId current)
    {
        var currentNode = Slot(current);
        if (currentNode is null)
        {
            return null;
        }

        if (currentNode.FirstChild is { } child)
        {
            return child;
        }

        return ClimbToNextSibling(root, current);
    }

    /// <summary>
    /// Returns the node after the whole subtree rooted at <paramref name="current"/>, in document
    /// order, without leaving the subtree rooted at <paramref name="root"/>.
    ///
    /// This is <see cref="NextInSubtree"/> minus the descend-into-children step, which is what
    /// NodeFilter.FILTER_REJECT needs: it rejects a node *and* its descendants, unlike FILTER_SKIP,
    /// which only skips the node itself and is served by <see cref="NextInSubtree"/>.
    /// </summary>
    public NodeId? NextAfterSubtree(NodeId root, NodeId current) => ClimbToNextSibling(root, current);

    /// <summary>
    /// Returns the node before <paramref name="current"/> in document order, without leaving the
    /// subtree rooted at <paramref name="root"/>. The root has no predecessor within its own
    /// subtree, but it is itself reachable as one: a NodeIterator can return its root, unlike a
    /// TreeWalker.
    /// </summary>
    public NodeId? PrevInSubtree(NodeId root, NodeId current)
    {
        if (current == root)
        {
            return null;
        }

        var currentNode = Slot(current);
        if (currentNode is null)
        {
            return null;
        }

        if (currentNode.PrevSibling is not { } prev)
        {
            // No previous sibling: the parent immediately precedes `current`.
            return currentNode.Parent;
        }

        // Otherwise it is the previous sibling's deepest last descendant.
        var nodeId = prev;
        for (var i = 0; i <= _nodes.Count; i++)
        {
            var node = Slot(nodeId);
            if (node is null)
            {
                return null;
            }

            if (node.LastChild is { } child)
            {
                nodeId = child;
            }
            else
            {
                return nodeId;
            }
        }

        // Same defense in depth as the forward walk: a malformed tree must not spin here.
        return null;
    }

    /// <summary>
    /// Follow <paramref name="current"/>'s next sibling, climbing ancestors until one has a next
    /// sibling, without stepping outside <paramref name="root"/>.
    /// </summary>
    private NodeId? ClimbToNextSibling(NodeId root, NodeId current)
    {
        var nodeId = current;
        for (var i = 0; i <= _nodes.Count; i++)
        {
            if (nodeId == root)
            {
                return null;
            }

            var node = Slot(nodeId);
            if (node is null)
            {
                return null;
            }

            if (node.NextSibling is { } sibling)
            {
                return sibling;
            }

            if (node.Parent is not { } parent)
            {
                return null;
            }

            nodeId = parent;
        }

        // Parent cycles are prevented by the mutation APIs. Keep a hard bound here as defense in
        // depth for a malformed tree.
        return null;
    }

    /// <summary>
    /// The node holding a <c>&lt;template&gt;</c> element's contents.
    ///
    /// The parser puts template children in a separate contents document rather than under the
    /// element (HTML spec), so this is the only way to reach them. Templates built with
    /// createElement have no contents node yet, so one is allocated on demand: <c>.content</c> must
    /// be usable either way. Returns null for a non-element node.
    /// </summary>
    public NodeId? TemplateContents(NodeId nodeId)
    {
        if (Slot(nodeId)?.Data is not ElementData element)
        {
            return null;
        }

        if (element.TemplateContents is { } existing)
        {
            return existing;
        }

        // Matches what the tree sink allocates for a parsed template.
        var contents = NewNode(NodeData.Document);
        if (Slot(nodeId)?.Data is ElementData current)
        {
            current.TemplateContents = contents;
            return contents;
        }

        return null;
    }

    public List<NodeId> Ancestors(NodeId nodeId)
    {
        var result = new List<NodeId>();
        var current = Slot(nodeId)?.Parent;
        while (current is { } parentId)
        {
            result.Add(parentId);
            // Defense in depth: a valid parent chain is at most nodes.Count long. Exceeding that
            // means Parent forms a cycle (which the reparenting guards prevent); stop rather than
            // loop forever.
            if (result.Count > _nodes.Count)
            {
                break;
            }

            current = Slot(parentId)?.Parent;
        }

        return result;
    }

    public NodeId? GetElementById(string id)
    {
        NodeId? indexed = _idIndex.TryGetValue(id, out var found) ? found : null;
        if (indexed is { } node && ContainingShadowRoot(node) is null)
        {
            return indexed;
        }

        // Creation happens before insertion, so the O(1) best-effort index can point at a shadow
        // descendant. Never expose that node through document.getElementById; recover the first
        // matching light-tree element in document order instead. Detached and template-content
        // nodes retain the legacy best-effort lookup behavior used internally.
        foreach (var nodeId in Descendants(Document))
        {
            if (string.Equals(Slot(nodeId)?.GetAttribute("id"), id, StringComparison.Ordinal))
            {
                return nodeId;
            }
        }

        return null;
    }

    public string TextContent(NodeId nodeId)
    {
        // Per DOM spec, calling textContent ON a CharacterData node (Text, Comment,
        // ProcessingInstruction) returns its .data. Calling textContent on an Element walks
        // descendants and concatenates Text node content only (Comment + PI are skipped). Handle
        // the direct-CharacterData case here so the descent helper can keep its element-centric
        // behavior.
        switch (Slot(nodeId)?.Data)
        {
            case TextData text:
                return text.Contents;
            case CommentData comment:
                return comment.Contents;
            case ProcessingInstructionData pi:
                return pi.Data;
        }

        var buf = new System.Text.StringBuilder();
        CollectText(nodeId, buf);
        return buf.ToString();
    }

    private void CollectText(NodeId nodeId, System.Text.StringBuilder buf)
    {
        // Iterative pre-order walk on an explicit heap stack. The recursive form overflowed the
        // thread stack and aborted the process on deeply nested trees; Descendants() is iterative +
        // capped for the same reason. A valid subtree visits at most nodes.Count nodes, so
        // exceeding that means the graph is cyclic (prevented by the AppendChild / InsertBefore
        // guards); stop rather than spin forever.
        var maxSteps = _nodes.Count + 16;
        var steps = 0;
        var stack = new Stack<NodeId>();
        stack.Push(nodeId);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            steps++;
            if (steps > maxSteps)
            {
                Console.Error.WriteLine("obscura: collect_text_inner cap hit - tree has a cycle");
                break;
            }

            var node = Slot(id);
            if (node is null)
            {
                continue;
            }

            if (node.Data is TextData text)
            {
                buf.Append(text.Contents);
                continue;
            }

            // Comment and ProcessingInstruction are intentionally NOT appended when traversing
            // descendants: per spec, textContent on an Element only includes Text descendants.
            // Direct textContent on a Comment/PI is handled by the caller.
            var kids = new List<NodeId>();
            var child = node.FirstChild;
            while (child is { } childId)
            {
                kids.Add(childId);
                if (kids.Count > _nodes.Count)
                {
                    Console.Error.WriteLine("obscura: collect_text_inner sibling cap hit - cycle");
                    break;
                }

                child = Slot(childId)?.NextSibling;
            }

            for (var i = kids.Count - 1; i >= 0; i--)
            {
                stack.Push(kids[i]);
            }
        }
    }

    public void AppendText(NodeId parentId, string text)
    {
        var lastChildId = Slot(parentId)?.LastChild;
        if (lastChildId is { } lastId && Slot(lastId)?.Data is TextData lastText)
        {
            lastText.Contents += text;
            return;
        }

        var textId = NewNode(NodeData.Text(text));
        AppendChild(parentId, textId);
    }

    /// <summary>
    /// The node whose children are a parsed fragment's top-level nodes: the synthetic root
    /// <c>&lt;html&gt;</c> element a fragment is wrapped in, or the document if there is none.
    /// Importing this node's children reproduces the fragment as written.
    ///
    /// This must NOT descend into a synthesized <c>&lt;body&gt;</c>. A <c>&lt;body&gt;</c> child of
    /// the root only appears when the fragment is parsed in the <c>&lt;html&gt;</c> context
    /// (documentElement.innerHTML), where the "before head" mode synthesizes both
    /// <c>&lt;head&gt;</c> and <c>&lt;body&gt;</c>; returning the body there dropped the head
    /// siblings. Every other element context leaves the parsed content directly under the root, so
    /// it is unaffected.
    /// </summary>
    public NodeId FragmentRoot()
    {
        var doc = Document;
        foreach (var child in Children(doc))
        {
            if (Slot(child)?.ElementName is { } name
                && string.Equals(name.Local, "html", StringComparison.Ordinal))
            {
                return child;
            }
        }

        return doc;
    }

    public void ImportChildrenFrom(NodeId parentId, DomTree source, NodeId sourceNode)
    {
        foreach (var sourceChildId in source.Children(sourceNode))
        {
            ImportNodeFrom(parentId, source, sourceChildId);
        }
    }

    /// <summary>
    /// Clone one node within this tree without attaching the clone.
    ///
    /// This operates on node data directly instead of serializing and parsing HTML. Besides
    /// avoiding context-sensitive fragment parsing (<c>&lt;html&gt;</c>, table children, and
    /// foreign content), it preserves the cloned root's element type and namespace. Template
    /// contents are stored in a separate document node and therefore need their own remapped clone.
    /// </summary>
    public NodeId? CloneNode(NodeId sourceNodeId, bool deep)
    {
        // DOM cloneNode is not defined for ShadowRoot nodes. A host clone keeps its light subtree
        // only; the separate registry means the shadow root is naturally omitted from that
        // traversal.
        if (IsShadowRoot(sourceNodeId))
        {
            return null;
        }

        var sourceNode = Slot(sourceNodeId);
        if (sourceNode is null)
        {
            return null;
        }

        var clonedRoot = NewNode(sourceNode.Data.Clone());
        var stack = new Stack<(NodeId DestParent, NodeId SourceNode)>();
        PrepareClonedChildren(sourceNodeId, clonedRoot, deep, stack);

        while (stack.Count > 0)
        {
            var (destParent, sourceChild) = stack.Pop();
            var childNode = Slot(sourceChild);
            if (childNode is null)
            {
                continue;
            }

            var clonedNode = NewNode(childNode.Data.Clone());
            AppendChild(destParent, clonedNode);
            PrepareClonedChildren(sourceChild, clonedNode, true, stack);
        }

        return clonedRoot;
    }

    private void PrepareClonedChildren(
        NodeId sourceNode,
        NodeId clonedNode,
        bool deep,
        Stack<(NodeId DestParent, NodeId SourceNode)> stack)
    {
        var sourceContents = (Slot(sourceNode)?.Data as ElementData)?.TemplateContents;

        if (sourceContents is { } contents)
        {
            var clonedContents = NewNode(NodeData.Document);
            if (Slot(clonedNode)?.Data is ElementData clonedElement)
            {
                clonedElement.TemplateContents = clonedContents;
            }

            if (deep)
            {
                var children = Children(contents);
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push((clonedContents, children[i]));
                }
            }
        }

        if (deep)
        {
            var children = Children(sourceNode);
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((clonedNode, children[i]));
            }
        }
    }

    /// <summary>
    /// Copies a node together with its subtree from <paramref name="source"/> and attaches it to
    /// <paramref name="parentId"/>. Returns the copy of <paramref name="sourceNodeId"/> itself, so
    /// that a caller that keeps parsing into <paramref name="source"/> can map the source to its
    /// copy.
    /// </summary>
    public NodeId? ImportNodeFrom(NodeId parentId, DomTree source, NodeId sourceNodeId)
    {
        // Iterative DFS with an explicit (destParent, sourceNode) stack so a deeply nested source
        // tree cannot overflow the thread stack and abort the process. Children are pushed in
        // reverse so they are appended in document order (AppendChild always appends to the end, so
        // each level keeps the source ordering).
        NodeId? importedRoot = null;
        var stack = new Stack<(NodeId DestParent, NodeId SourceId)>();
        stack.Push((parentId, sourceNodeId));
        while (stack.Count > 0)
        {
            var (destParent, srcId) = stack.Pop();
            var sourceNode = source.Slot(srcId);
            if (sourceNode is null)
            {
                continue;
            }

            var newId = NewNode(sourceNode.Data.Clone());
            AppendChild(destParent, newId);
            // The first node off the stack is sourceNodeId itself.
            importedRoot ??= newId;

            // A <template>'s children hang off a separate contents document, so the child walk
            // below never reaches them. Worse, the cloned data carries the *source* tree's contents
            // NodeId, which here indexes whatever unrelated node occupies that slot. Allocate a
            // real contents node and queue the source contents' children into it, so the reference
            // is remapped rather than left dangling (issue #463).
            var srcContents = (Slot(newId)?.Data as ElementData)?.TemplateContents;
            if (srcContents is { } contents)
            {
                var destContents = NewNode(NodeData.Document);
                if (Slot(newId)?.Data is ElementData element)
                {
                    element.TemplateContents = destContents;
                }

                // Onto the same stack, so nested templates stay iterative.
                var contentChildren = source.Children(contents);
                for (var i = contentChildren.Count - 1; i >= 0; i--)
                {
                    stack.Push((destContents, contentChildren[i]));
                }
            }

            var children = source.Children(srcId);
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((newId, children[i]));
            }
        }

        return importedRoot;
    }

    /// <summary>The number of live nodes.</summary>
    public int Count
    {
        get
        {
            var live = 0;
            foreach (var node in _nodes)
            {
                if (node is not null)
                {
                    live++;
                }
            }

            return live;
        }
    }

    public bool IsEmpty => Count <= 1;

    public void UpdateIdIndex(NodeId nodeId, string? oldId, string? newId)
    {
        if (oldId is not null)
        {
            _idIndex.Remove(oldId);
        }

        if (newId is not null)
        {
            _idIndex[newId] = nodeId;
        }
    }
}
