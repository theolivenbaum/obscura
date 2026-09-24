namespace PocketCalculator.Dom;

/// <summary>
/// An index into <see cref="DomTree"/>'s node arena. Index-based rather than pointer-based
/// because the op layer and the render tree both key off the raw <c>u32</c>.
/// </summary>
/// <remarks>
/// <para>
/// DEVIATION from crates/obscura-dom, where a node id is the bare slot index. The low
/// <see cref="IndexBits"/> bits are still the slot index (<see cref="Index"/>), and the
/// next seven carry the slot's generation, which goes up each time the slot is freed. The
/// DOM garbage collector (<c>DomTree.Gc.cs</c>) frees detached nodes nothing can reach and
/// hands their slots out again; a stale id held anywhere (a raw nid kept by page script, a
/// host-side side table, a CDP client's backendNodeId) then names a free slot, never the
/// node that took its place, because the generations differ. A slot whose generation would
/// pass <see cref="MaxGeneration"/> is retired instead of reused, so ids stay below
/// 2<sup>31</sup> and survive every <c>int</c> cast on the way to JavaScript.
/// </para>
/// <para>
/// A slot that was never reused has generation 0, so its <see cref="Value"/> equals its
/// index and every id an ordinary page sees is the number Rust would print.
/// </para>
/// </remarks>
public readonly record struct NodeId(uint Value)
{
    /// <summary>How many low bits of <see cref="Value"/> are the slot index.</summary>
    public const int IndexBits = 24;

    /// <summary>The mask that extracts the slot index from <see cref="Value"/>.</summary>
    public const uint IndexMask = (1u << IndexBits) - 1;

    /// <summary>The highest generation a slot reaches before it is retired.</summary>
    public const uint MaxGeneration = 127;

    public static NodeId New(uint value) => new(value);

    /// <summary>The id of generation <paramref name="generation"/> of slot <paramref name="index"/>.</summary>
    public static NodeId FromParts(int index, uint generation) =>
        new(((generation & MaxGeneration) << IndexBits) | ((uint)index & IndexMask));

    /// <summary>The slot index. Arrays keyed by node use this, never <see cref="Value"/>.</summary>
    public int Index => (int)(Value & IndexMask);

    /// <summary>How many times the slot was freed before this node took it.</summary>
    public uint Generation => Value >> IndexBits;

    /// <summary>The wire value: what op results, CDP node ids and the JS side carry.</summary>
    public uint Raw => Value;

    public override string ToString() => $"NodeId({Value})";
}
