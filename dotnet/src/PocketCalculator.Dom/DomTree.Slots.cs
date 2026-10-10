namespace PocketCalculator.Dom;

/// <summary>
/// Shadow root options and manual slot assignment (<c>attachShadow({ slotAssignment: "manual" })</c>
/// with <c>HTMLSlotElement.assign()</c>).
/// </summary>
/// <remarks>
/// Port addition: crates/obscura-dom assigns slottables by name only and records no root
/// options. Manual assignment lives here, not in bootstrap.js, so the render layer and
/// <c>::slotted()</c>, which ask <see cref="AssignedSlot"/> and <see cref="AssignedNodes"/>,
/// see the same assignment script does.
/// </remarks>
public sealed partial class DomTree
{
    // slot -> its manually assigned nodes, in assign() order; node -> the slot it was assigned to.
    private Dictionary<NodeId, List<NodeId>>? _manualAssigned;
    private Dictionary<NodeId, NodeId>? _manualSlotOf;

    /// <summary>
    /// Record the options a shadow root was created with. Returns false when
    /// <paramref name="root"/> is not a shadow root.
    /// </summary>
    public bool SetShadowRootOptions(NodeId root, bool manualSlotAssignment, bool delegatesFocus, bool clonable, bool serializable)
    {
        if (!_shadowRoots.TryGetValue(root, out var info))
        {
            return false;
        }

        if (info.ManualSlotAssignment != manualSlotAssignment)
        {
            MarkSlotRootDirty(root);
        }

        _shadowRoots[root] = info with
        {
            ManualSlotAssignment = manualSlotAssignment,
            DelegatesFocus = delegatesFocus,
            Clonable = clonable,
            Serializable = serializable,
        };
        return true;
    }

    /// <summary>
    /// HTML's <c>assign(...nodes)</c>: <paramref name="slot"/>'s manually assigned nodes become
    /// <paramref name="nodes"/> (duplicates dropped), and each of them leaves the slot it was
    /// assigned to before. Nodes that are not children of the host are kept, and count once they
    /// are. Returns false when <paramref name="slot"/> is not a slot element.
    /// </summary>
    public bool AssignSlottablesManually(NodeId slot, IReadOnlyList<NodeId> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (!IsHtmlSlotElement(slot))
        {
            return false;
        }

        _manualAssigned ??= [];
        _manualSlotOf ??= [];
        MarkContainingSlotsDirty(slot);
        foreach (var node in nodes)
        {
            if (_manualSlotOf.TryGetValue(node, out var owner) && owner != slot)
            {
                MarkContainingSlotsDirty(owner);
            }
        }

        if (_manualAssigned.Remove(slot, out var previous))
        {
            foreach (var node in previous)
            {
                if (_manualSlotOf.TryGetValue(node, out var owner) && owner == slot)
                {
                    _manualSlotOf.Remove(node);
                }
            }
        }

        var assigned = new List<NodeId>(nodes.Count);
        var seen = new HashSet<NodeId>();
        foreach (var node in nodes)
        {
            if (Slot(node) is null || !seen.Add(node))
            {
                continue;
            }

            if (_manualSlotOf.TryGetValue(node, out var other) && other != slot
                && _manualAssigned.TryGetValue(other, out var otherList))
            {
                otherList.Remove(node);
                if (otherList.Count == 0)
                {
                    _manualAssigned.Remove(other);
                }
            }

            _manualSlotOf[node] = slot;
            assigned.Add(node);
        }

        if (assigned.Count != 0)
        {
            _manualAssigned[slot] = assigned;
        }

        return true;
    }

    /// <summary>A manual-mode root's slot for <paramref name="node"/>: the slot in that root it was assigned to.</summary>
    private NodeId? ManualAssignedSlot(NodeId node, NodeId root)
    {
        if (_manualSlotOf is null || !_manualSlotOf.TryGetValue(node, out var slot))
        {
            return null;
        }

        return IsHtmlSlotElement(slot) && ContainingShadowRoot(slot) == root ? slot : null;
    }

    /// <summary>A manual-mode slot's assigned nodes: its manually assigned nodes that are slottable children of <paramref name="host"/>.</summary>
    private List<NodeId> ManualAssignedNodes(NodeId slot, NodeId host)
    {
        var result = new List<NodeId>();
        if (_manualAssigned is null || !_manualAssigned.TryGetValue(slot, out var assigned))
        {
            return result;
        }

        foreach (var node in assigned)
        {
            if (Slot(node) is { } live && live.Parent == host
                && (live.IsElement || live.TextContentOfTextNode is not null))
            {
                result.Add(node);
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- slotchange

    // Each shadow root's slot assignment as of its last slotchange check, taken (before the
    // mutation) the first time the root is marked dirty, and the roots marked since.
    private Dictionary<NodeId, Dictionary<NodeId, List<NodeId>>>? _slotBaselines;
    private List<NodeId>? _dirtySlotRoots;
    private HashSet<NodeId>? _dirtySlotRootSet;

    // How many nodes the subtree probe of a moved node looks at before it assumes a slot.
    private const int SlotProbeLimit = 64;

    /// <summary>
    /// Note, before it happens, that a mutation may change the slot assignment of
    /// <paramref name="root"/>'s shadow tree. <see cref="TakeSlotChanges"/> later compares the
    /// assignment with the one recorded here.
    /// </summary>
    public void MarkSlotRootDirty(NodeId root)
    {
        if (!_shadowRoots.ContainsKey(root))
        {
            return;
        }

        _dirtySlotRootSet ??= [];
        if (!_dirtySlotRootSet.Add(root))
        {
            return;
        }

        (_dirtySlotRoots ??= []).Add(root);
        _slotBaselines ??= [];
        if (!_slotBaselines.ContainsKey(root))
        {
            _slotBaselines[root] = SlotAssignmentSnapshot(root);
        }
    }

    /// <summary>Mark the shadow root hosted by <paramref name="parent"/>, if any.</summary>
    public void MarkHostSlotsDirty(NodeId? parent)
    {
        if (parent is { } host && _shadowRootsByHost.TryGetValue(host, out var root))
        {
            MarkSlotRootDirty(root);
        }
    }

    /// <summary>Mark the shadow tree <paramref name="node"/> is in, if any.</summary>
    public void MarkContainingSlotsDirty(NodeId? node)
    {
        if (node is { } id && _shadowRoots.Count != 0 && ContainingShadowRoot(id) is { } root)
        {
            MarkSlotRootDirty(root);
        }
    }

    /// <summary>
    /// Whether moving <paramref name="node"/> can move a slot: it is one, or its subtree has one.
    /// Gives up (answering true) after <see cref="SlotProbeLimit"/> nodes, so a large subtree costs
    /// the caller an ancestor walk rather than a full subtree walk.
    /// </summary>
    public bool MayCarrySlot(NodeId node)
    {
        if (_shadowRoots.Count == 0)
        {
            return false;
        }

        var seen = 0;
        var stack = new Stack<NodeId>();
        stack.Push(node);
        while (stack.TryPop(out var id))
        {
            if (++seen > SlotProbeLimit)
            {
                return true;
            }

            if (IsHtmlSlotElement(id))
            {
                return true;
            }

            for (var child = Slot(id)?.FirstChild; child is { } c; child = Slot(c)?.NextSibling)
            {
                stack.Push(c);
            }
        }

        return false;
    }

    /// <summary>
    /// The slots whose assigned nodes changed since their shadow root was marked dirty, roots in
    /// the order they were marked and slots in tree order, then slots that left the root with
    /// nodes assigned. Clears the dirty set.
    /// </summary>
    public List<NodeId> TakeSlotChanges()
    {
        var changed = new List<NodeId>();
        if (_dirtySlotRoots is null || _dirtySlotRoots.Count == 0)
        {
            return changed;
        }

        var roots = _dirtySlotRoots.ToArray();
        _dirtySlotRoots.Clear();
        _dirtySlotRootSet!.Clear();
        foreach (var root in roots)
        {
            if (!_shadowRoots.ContainsKey(root))
            {
                _slotBaselines?.Remove(root);
                continue;
            }

            var before = _slotBaselines != null && _slotBaselines.TryGetValue(root, out var b) ? b : [];
            var after = SlotAssignmentSnapshot(root);
            var slots = new List<NodeId>();
            foreach (var slot in Descendants(root))
            {
                if (IsHtmlSlotElement(slot))
                {
                    slots.Add(slot);
                }
            }

            // A slot removed with nodes assigned is signalled by the removal itself, before the
            // tree's assignment runs again, so it comes first, as in Chromium.
            var present = new HashSet<NodeId>(slots);
            foreach (var (slot, was) in before)
            {
                if (!present.Contains(slot) && was.Count != 0 && Slot(slot) is not null)
                {
                    changed.Add(slot);
                }
            }

            foreach (var slot in slots)
            {
                before.TryGetValue(slot, out var was);
                after.TryGetValue(slot, out var now);
                if (!SameNodes(was, now))
                {
                    changed.Add(slot);
                }
            }

            (_slotBaselines ??= [])[root] = after;
        }

        return changed;
    }

    private static bool SameNodes(List<NodeId>? a, List<NodeId>? b)
    {
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        for (var i = 0; i < countA; i++)
        {
            if (a![i] != b![i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every slot of <paramref name="root"/>'s tree with its assigned nodes, in one pass over the tree.</summary>
    private Dictionary<NodeId, List<NodeId>> SlotAssignmentSnapshot(NodeId root)
    {
        var result = new Dictionary<NodeId, List<NodeId>>();
        if (!_shadowRoots.TryGetValue(root, out var info))
        {
            return result;
        }

        if (info.ManualSlotAssignment)
        {
            foreach (var slot in Descendants(root))
            {
                if (IsHtmlSlotElement(slot) && ManualAssignedNodes(slot, info.Host) is { Count: > 0 } manual)
                {
                    result[slot] = manual;
                }
            }

            return result;
        }

        Dictionary<string, NodeId>? firstByName = null;
        foreach (var slot in Descendants(root))
        {
            if (IsHtmlSlotElement(slot))
            {
                (firstByName ??= new(StringComparer.Ordinal)).TryAdd(Slot(slot)?.GetAttribute("name") ?? "", slot);
            }
        }

        if (firstByName is null)
        {
            return result;
        }

        for (var child = Slot(info.Host)?.FirstChild; child is { } c; child = Slot(c)?.NextSibling)
        {
            var node = Slot(c);
            string name;
            if (node is null)
            {
                break;
            }
            else if (node.IsElement)
            {
                name = node.GetAttribute("slot") ?? "";
            }
            else if (node.TextContentOfTextNode is not null)
            {
                name = "";
            }
            else
            {
                continue;
            }

            if (firstByName.TryGetValue(name, out var target))
            {
                if (!result.TryGetValue(target, out var list))
                {
                    result[target] = list = [];
                }

                list.Add(c);
            }
        }

        return result;
    }

    /// <summary>Drop a freed node's manual assignment, as a slot and as an assigned node.</summary>
    private void ForgetManualSlotAssignment(NodeId id)
    {
        _slotBaselines?.Remove(id);
        if (_dirtySlotRootSet is not null && _dirtySlotRootSet.Remove(id))
        {
            _dirtySlotRoots?.Remove(id);
        }

        if (_manualSlotOf is null || _manualAssigned is null)
        {
            return;
        }

        if (_manualAssigned.Remove(id, out var assigned))
        {
            foreach (var node in assigned)
            {
                if (_manualSlotOf.TryGetValue(node, out var owner) && owner == id)
                {
                    _manualSlotOf.Remove(node);
                }
            }
        }

        if (_manualSlotOf.Remove(id, out var slot) && _manualAssigned.TryGetValue(slot, out var list))
        {
            list.Remove(id);
            if (list.Count == 0)
            {
                _manualAssigned.Remove(slot);
            }
        }
    }
}
