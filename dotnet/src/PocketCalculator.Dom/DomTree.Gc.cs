using System.Buffers;

namespace PocketCalculator.Dom;

/// <summary>
/// Something outside the tree that holds node ids: a JavaScript realm's wrappers, a CDP
/// agent's node map, a host-side side table. Registered with
/// <see cref="DomTree.AddGcParticipant"/>.
/// </summary>
public interface IDomGcParticipant
{
    /// <summary>
    /// Keep every node this participant can still reach (<see cref="DomCollection.Keep"/>).
    /// Called once per collection, after the tree's own roots are marked, so
    /// <see cref="DomCollection.CandidateCount"/> already excludes what the document holds.
    /// </summary>
    void MarkRoots(DomCollection collection);

    /// <summary>
    /// The nodes the collection is about to free, after every participant marked. Their ids
    /// stop resolving once this returns; drop anything keyed by them.
    /// </summary>
    void OnFreed(DomTree tree, IReadOnlyList<NodeId> freed);
}

/// <summary>What one <see cref="DomTree.CollectGarbage"/> call did.</summary>
public readonly record struct DomCollectionResult(int FreedNodes, long FreedBytes, int LiveNodes);

/// <summary>
/// One collection in progress: the detached subtrees of a tree, grouped into components,
/// and which of them something still holds.
/// </summary>
/// <remarks>
/// A component is everything reachable from a node by DOM navigation: parent and child
/// links, a shadow root and its host, a template and its contents document. Script that
/// holds any node of a component can reach all of it, so the unit that lives or dies is the
/// component, never a single node.
/// </remarks>
public sealed class DomCollection
{
    private readonly int[] _parent;
    private readonly bool[] _live;
    private readonly int _slots;

    internal DomCollection(DomTree tree, bool inOperation, int[] parent, bool[] live, int slots)
    {
        Tree = tree;
        InOperation = inOperation;
        _parent = parent;
        _live = live;
        _slots = slots;
    }

    public DomTree Tree { get; }

    /// <summary>
    /// Whether this collection runs inside an op, with script on the stack. Such a collection
    /// may not consult V8's collector, so a participant keeps every node it has a wrapper
    /// for, dead or not.
    /// </summary>
    public bool InOperation { get; }

    /// <summary>How many components are still free to go.</summary>
    public int CandidateCount { get; private set; }

    /// <summary>
    /// The component <paramref name="node"/> belongs to while it is still a candidate, or -1
    /// when the node is dead, kept, or not in this collection.
    /// </summary>
    public int ComponentOf(NodeId node)
    {
        if (Tree.GetNode(node) is null || node.Index >= _slots)
        {
            return -1;
        }

        var root = Find(node.Index);
        return _live[root] ? -1 : root;
    }

    /// <summary>Keep the component <paramref name="node"/> belongs to.</summary>
    public void Keep(NodeId node)
    {
        if (Tree.GetNode(node) is not null && node.Index < _slots)
        {
            KeepRoot(Find(node.Index));
        }
    }

    /// <summary>Keep a component <see cref="ComponentOf"/> returned.</summary>
    public void KeepComponent(int component)
    {
        if ((uint)component < (uint)_slots)
        {
            KeepRoot(Find(component));
        }
    }

    internal void KeepRoot(int root)
    {
        if (!_live[root])
        {
            _live[root] = true;
            CandidateCount--;
        }
    }

    internal bool IsLiveSlot(int index) => _live[Find(index)];

    internal int Find(int index)
    {
        var parent = _parent;
        while (parent[index] != index)
        {
            // Path halving keeps every later lookup near O(1).
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    internal void Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra == rb)
        {
            return;
        }

        // Keep the smaller index as the root: deterministic, and the document (0) stays its
        // own component's root.
        if (rb < ra)
        {
            (ra, rb) = (rb, ra);
        }

        _parent[rb] = ra;
        _live[ra] |= _live[rb];
    }

    internal void SetCandidateCount(int count) => CandidateCount = count;

    /// <summary>How many slots held a node when the collection started.</summary>
    internal int LiveNodes { get; set; }
}

/// <summary>
/// The DOM garbage collector.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from crates/obscura-dom, which never frees a detached node: <c>remove</c>
/// exists but nothing calls it, so a page that replaces a table with <c>innerHTML</c> once
/// a second grows without bound, and under the per-document budget (M7) it eventually gets
/// <c>QuotaExceededError</c>. Chromium frees a detached node once nothing references it.
/// Here a collection groups the detached nodes into components (see
/// <see cref="DomCollection"/>), keeps every component that holds a connected node, a
/// pinned node, or a node a participant still reaches, and frees the rest: their slots go
/// back on the free list with a new generation (see <see cref="NodeId"/>) and their bytes
/// go back to the budget.
/// </para>
/// <para>
/// A collection is O(slots) and runs only when the nodes created or disconnected since the
/// last one reach the tree's live size (with a floor), so its cost is amortized over the
/// work that made the garbage. Mutations themselves pay nothing but a counter.
/// </para>
/// <para>
/// Two kinds: a full collection at a task boundary, where participants may run V8's
/// collector to learn which wrappers are dead; and an in-operation one, inside an op with
/// script on the stack, which frees only subtrees that were connected once, that no op has
/// handed to script since the last task boundary (<see cref="ExposureEpoch"/>), and that no
/// realm has a wrapper for. Script cannot name such a subtree: it never received an id for
/// it that it could still be holding.
/// </para>
/// </remarks>
public sealed partial class DomTree
{
    /// <summary>The fewest nodes created or disconnected that make a collection due.</summary>
    public const int MinCollectionNodes = 8192;

    /// <summary>The fewest bytes charged that make a collection due.</summary>
    public const long MinCollectionBytes = 4L * 1024 * 1024;

    private readonly List<IDomGcParticipant> _gcParticipants = [];
    private DomPins? _pins;
    private long _gcCreatedSinceSweep;
    private long _gcDisconnectedSinceSweep;
    private long _gcBytesSinceSweep;
    private long _gcWorkAtOpSweep;
    private long _gcBytesAtOpSweep;
    private long _gcLiveAfterSweep;
    private long _gcBytesAfterSweep;
    private bool _collecting;

    /// <summary>
    /// Whether ops may collect this tree on their own (thresholds and a budget overrun). Set
    /// by the runtime that owns the tree's realms; a bare tree never collects unless asked.
    /// </summary>
    public bool AutomaticCollection { get; set; }

    /// <summary>
    /// Advanced by the host each time script yields (a task boundary). An op that returns a
    /// node id to script stamps the node with the current value (<see cref="NoteExposed"/>).
    /// </summary>
    public uint ExposureEpoch { get; private set; } = 1;

    /// <summary>Arena slots: live, free and retired. What a leak grows.</summary>
    public int SlotCount => _nodes.Count;

    /// <summary>How many collections freed something, for tests and diagnostics.</summary>
    public int CollectionCount { get; private set; }

    /// <summary>Script has yielded: ids handed to it before now are held only through wrappers.</summary>
    public void AdvanceExposureEpoch() => ExposureEpoch = ExposureEpoch == uint.MaxValue ? 1 : ExposureEpoch + 1;

    /// <summary>Record that an op is handing <paramref name="id"/> to script.</summary>
    public void NoteExposed(NodeId id)
    {
        if (Slot(id) is { } node)
        {
            node.ExposedEpoch = ExposureEpoch;
        }
    }

    /// <summary>Register something that holds node ids. It is asked on every collection.</summary>
    public void AddGcParticipant(IDomGcParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!_gcParticipants.Contains(participant))
        {
            _gcParticipants.Add(participant);
        }
    }

    public void RemoveGcParticipant(IDomGcParticipant participant) => _gcParticipants.Remove(participant);

    /// <summary>
    /// Keep <paramref name="id"/> (and its component) alive on behalf of
    /// <paramref name="owner"/> until <see cref="UnpinAll"/>. CDP and MCP use this for the
    /// node ids they hand to a client, which the host cannot see the client drop.
    /// </summary>
    public void Pin(object owner, NodeId id)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (Slot(id) is not null)
        {
            (_pins ??= new DomPins()).Add(owner, id);
        }
    }

    /// <summary>Drop every pin <paramref name="owner"/> holds.</summary>
    public void UnpinAll(object owner) => _pins?.Clear(owner);

    /// <summary>
    /// Whether enough work happened since the last collection to make another worthwhile.
    /// <paramref name="inOperation"/> asks for the in-operation kind, which counts from the
    /// last collection of either kind.
    /// </summary>
    public bool CollectionDue(bool inOperation)
    {
        var work = _gcCreatedSinceSweep + _gcDisconnectedSinceSweep;
        var bytes = _gcBytesSinceSweep;
        if (inOperation)
        {
            work -= _gcWorkAtOpSweep;
            bytes -= _gcBytesAtOpSweep;
        }

        return work >= Math.Max(MinCollectionNodes, _gcLiveAfterSweep)
            || bytes >= Math.Max(MinCollectionBytes, _gcBytesAfterSweep);
    }

    /// <summary>
    /// Free every detached component nothing holds. Returns what was freed. Re-entrant
    /// calls (a participant that mutates the tree) return an empty result.
    /// </summary>
    public DomCollectionResult CollectGarbage(bool inOperation = false)
    {
        if (_collecting)
        {
            return default;
        }

        _collecting = true;
        var slots = _nodes.Count;
        var parent = ArrayPool<int>.Shared.Rent(Math.Max(1, slots));
        var live = ArrayPool<bool>.Shared.Rent(Math.Max(1, slots));
        try
        {
            var collection = Plan(inOperation, parent, live, slots);
            if (collection.CandidateCount > 0)
            {
                foreach (var participant in _gcParticipants.ToArray())
                {
                    participant.MarkRoots(collection);
                    if (collection.CandidateCount == 0)
                    {
                        break;
                    }
                }
            }

            var result = collection.CandidateCount > 0 ? Sweep(collection, slots) : default;
            var liveNodes = collection.LiveNodes - result.FreedNodes;
            FinishCycle(inOperation, liveNodes);
            if (result.FreedNodes > 0)
            {
                CollectionCount++;
            }

            return result with { LiveNodes = liveNodes };
        }
        finally
        {
            ArrayPool<int>.Shared.Return(parent);
            ArrayPool<bool>.Shared.Return(live);
            _collecting = false;
        }
    }

    private void FinishCycle(bool inOperation, long liveNodes)
    {
        if (inOperation)
        {
            _gcWorkAtOpSweep = _gcCreatedSinceSweep + _gcDisconnectedSinceSweep;
            _gcBytesAtOpSweep = _gcBytesSinceSweep;
        }
        else
        {
            _gcCreatedSinceSweep = 0;
            _gcDisconnectedSinceSweep = 0;
            _gcBytesSinceSweep = 0;
            _gcWorkAtOpSweep = 0;
            _gcBytesAtOpSweep = 0;
        }

        _gcLiveAfterSweep = liveNodes;
        _gcBytesAfterSweep = _contentBytes;
    }

    private DomCollection Plan(bool inOperation, int[] parent, bool[] live, int slots)
    {
        var collection = new DomCollection(this, inOperation, parent, live, slots);
        var liveNodes = 0;
        for (var i = 0; i < slots; i++)
        {
            parent[i] = i;
            var node = _nodes[i];
            if (node is not null)
            {
                liveNodes++;
            }

            // A free slot is its own component and "live", so it is never swept twice.
            live[i] = node is null
                || node.Connected
                || (inOperation && (!node.EverConnected || node.ExposedEpoch == ExposureEpoch));
        }

        for (var i = 0; i < slots; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                WorkCancellation.ThrowIfCancellationRequested();
            }

            if (_nodes[i] is not { } node)
            {
                continue;
            }

            if (node.Parent is { } p && Slot(p) is not null && p.Index < slots)
            {
                collection.Union(i, p.Index);
            }

            if (node.Data is ElementData { TemplateContents: { } contents } && Slot(contents) is not null && contents.Index < slots)
            {
                collection.Union(i, contents.Index);
            }
        }

        foreach (var (rootId, info) in _shadowRoots)
        {
            if (Slot(rootId) is not null && Slot(info.Host) is not null && rootId.Index < slots && info.Host.Index < slots)
            {
                collection.Union(rootId.Index, info.Host.Index);
            }
        }

        _pins?.MarkRoots(collection);

        var candidates = 0;
        for (var i = 0; i < slots; i++)
        {
            if (parent[i] == i && !live[i])
            {
                candidates++;
            }
        }

        collection.SetCandidateCount(candidates);
        collection.LiveNodes = liveNodes;
        return collection;
    }

    private DomCollectionResult Sweep(DomCollection collection, int slots)
    {
        var freed = new List<NodeId>();
        for (var i = 0; i < slots; i++)
        {
            if (_nodes[i] is { } node && !collection.IsLiveSlot(i))
            {
                freed.Add(node.Id);
            }
        }

        if (freed.Count == 0)
        {
            return default;
        }

        foreach (var participant in _gcParticipants.ToArray())
        {
            participant.OnFreed(this, freed);
        }

        var before = _contentBytes;
        foreach (var id in freed)
        {
            // Every link of a freed node points inside its own component, which goes as a
            // whole, so no live node is left pointing at a freed slot.
            if (_shadowRoots.Remove(id, out var root) && _shadowRootsByHost.TryGetValue(root.Host, out var hosted) && hosted == id)
            {
                _shadowRootsByHost.Remove(root.Host);
            }

            if (_shadowRootsByHost.Remove(id, out var rootId))
            {
                _shadowRoots.Remove(rootId);
            }

            if (Slot(id) is not { } node)
            {
                continue;
            }

            if (node.GetAttribute("id") is { } idValue
                && _idIndex.TryGetValue(idValue, out var indexed)
                && indexed == id)
            {
                _idIndex.Remove(idValue);
            }
        }

        foreach (var id in freed)
        {
            if (Slot(id) is { } node)
            {
                FreeSlot(node);
            }
        }

        return new DomCollectionResult(freed.Count, before - _contentBytes, 0);
    }
}

/// <summary>Node ids held on behalf of an owner (a CDP session, an MCP state).</summary>
internal sealed class DomPins
{
    private readonly Dictionary<object, HashSet<NodeId>> _byOwner = new(ReferenceEqualityComparer.Instance);

    public void Add(object owner, NodeId id)
    {
        if (!_byOwner.TryGetValue(owner, out var set))
        {
            set = [];
            _byOwner[owner] = set;
        }

        set.Add(id);
    }

    public void Clear(object owner) => _byOwner.Remove(owner);

    public void ForgetNode(NodeId id)
    {
        foreach (var set in _byOwner.Values)
        {
            set.Remove(id);
        }
    }

    public void MarkRoots(DomCollection collection)
    {
        foreach (var set in _byOwner.Values)
        {
            foreach (var id in set)
            {
                collection.Keep(id);
            }
        }
    }
}
