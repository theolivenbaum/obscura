// A minimal generational arena standing in for the `slotmap` crate that taffy's
// TaffyTree is built on. NodeId packs the generation into the high 32 bits and
// the slot index into the low 32 bits, matching slotmap's `KeyData::as_ffi`.
namespace Obscura.Render.Layout;

/// <summary>A generational arena keyed by <see cref="NodeId"/>.</summary>
internal sealed class SlotMap<T>
{
    private struct Slot
    {
        public uint Version;
        public bool Occupied;
        public T? Value;
    }

    private readonly List<Slot> _slots;
    private readonly Stack<int> _free = new();
    private int _count;

    public SlotMap(int capacity) => _slots = new List<Slot>(capacity);

    /// <summary>The number of live entries.</summary>
    public int Count => _count;

    /// <summary>The number of slots currently allocated.</summary>
    public int Capacity => Math.Max(_slots.Capacity, _slots.Count);

    /// <summary>Insert a value and return its key.</summary>
    public NodeId Insert(T value)
    {
        if (_free.Count > 0)
        {
            int idx = _free.Pop();
            var slot = _slots[idx];
            slot.Occupied = true;
            slot.Value = value;
            _slots[idx] = slot;
            _count += 1;
            return new NodeId(((ulong)slot.Version << 32) | (uint)idx);
        }

        int newIdx = _slots.Count;
        _slots.Add(new Slot { Version = 1, Occupied = true, Value = value });
        _count += 1;
        return new NodeId((1UL << 32) | (uint)newIdx);
    }

    /// <summary>Remove the value for a key, returning whether anything was removed.</summary>
    public bool Remove(NodeId id)
    {
        int idx = Index(id);
        if (idx < 0 || idx >= _slots.Count)
        {
            return false;
        }

        var slot = _slots[idx];
        if (!slot.Occupied || slot.Version != Version(id))
        {
            return false;
        }

        slot.Occupied = false;
        slot.Value = default;
        slot.Version += 1;
        _slots[idx] = slot;
        _free.Push(idx);
        _count -= 1;
        return true;
    }

    /// <summary>Whether the key refers to a live entry.</summary>
    public bool ContainsKey(NodeId id)
    {
        int idx = Index(id);
        if (idx < 0 || idx >= _slots.Count)
        {
            return false;
        }

        var slot = _slots[idx];
        return slot.Occupied && slot.Version == Version(id);
    }

    /// <summary>Get or set the value for a key. Throws if the key is not live.</summary>
    public T this[NodeId id]
    {
        get
        {
            int idx = Index(id);
            if (idx < 0 || idx >= _slots.Count)
            {
                throw new KeyNotFoundException($"Node {id} is not in the tree");
            }

            var slot = _slots[idx];
            if (!slot.Occupied || slot.Version != Version(id))
            {
                throw new KeyNotFoundException($"Node {id} is not in the tree");
            }

            return slot.Value!;
        }

        set
        {
            int idx = Index(id);
            if (idx < 0 || idx >= _slots.Count)
            {
                throw new KeyNotFoundException($"Node {id} is not in the tree");
            }

            var slot = _slots[idx];
            if (!slot.Occupied || slot.Version != Version(id))
            {
                throw new KeyNotFoundException($"Node {id} is not in the tree");
            }

            slot.Value = value;
            _slots[idx] = slot;
        }
    }

    /// <summary>Try to get the value for a key.</summary>
    public bool TryGetValue(NodeId id, out T? value)
    {
        int idx = Index(id);
        if (idx >= 0 && idx < _slots.Count)
        {
            var slot = _slots[idx];
            if (slot.Occupied && slot.Version == Version(id))
            {
                value = slot.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Remove every entry.</summary>
    public void Clear()
    {
        _slots.Clear();
        _free.Clear();
        _count = 0;
    }

    private static int Index(NodeId id) => (int)(uint)(id.Value & 0xFFFF_FFFFUL);

    private static uint Version(NodeId id) => (uint)(id.Value >> 32);
}
