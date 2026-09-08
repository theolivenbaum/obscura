namespace Obscura.Dom;

/// <summary>
/// An index into <see cref="DomTree"/>'s node arena. Index-based rather than pointer-based
/// because the op layer and the render tree both key off the raw <c>u32</c>.
/// </summary>
public readonly record struct NodeId(uint Value)
{
    public static NodeId New(uint value) => new(value);

    public int Index => (int)Value;

    public uint Raw => Value;

    public override string ToString() => $"NodeId({Value})";
}
