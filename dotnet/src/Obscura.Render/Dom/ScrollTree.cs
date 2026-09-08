// Port of the scrolling topology in crates/obscura-render/src/dom.rs.
using Obscura.Dom;

namespace Obscura.Render;

/// <summary>
/// Dense identifier for one scrolling area in a prepared render. Index zero is always the root
/// viewport; element scroll containers follow in DOM order.
/// </summary>
/// <remarks>
/// The ids are rebuild-local and must never be persisted by an embedding runtime (persist
/// element offsets by <see cref="NodeId"/> instead).
/// </remarks>
public readonly record struct ScrollId(uint Value)
{
    internal static readonly ScrollId Root = new(0);

    internal int Index => (int)Value;
}

internal readonly record struct ScrollContainer
{
    internal required NodeId? Node { get; init; }

    internal required ScrollId? Parent { get; init; }

    internal required (float Width, float Height) ClientSize { get; init; }

    internal required (float Width, float Height) ContentSize { get; init; }

    internal required (float X, float Y) MaxOffset { get; init; }
}

/// <summary>
/// Immutable scrolling topology derived from one final layout. Hot paint and geometry paths
/// index the node vectors directly by <see cref="NodeId"/>.
/// </summary>
internal sealed class ScrollTree
{
    internal required List<ScrollContainer> Containers { get; init; }

    /// <summary>Scrolling area established by this node, if any.</summary>
    internal required ScrollId?[] NodeContainer { get; init; }

    /// <summary>
    /// CSSOM scrolling-overflow size for every node with a layout box. Populated even for
    /// <c>overflow:visible</c> and <c>overflow:clip</c>.
    /// </summary>
    internal required (float Width, float Height)?[] NodeContentSize { get; init; }

    /// <summary>
    /// Area whose content movement applies to this node's own box. A scroll container's
    /// border/background therefore retain the parent's owner, while its descendants use the
    /// container itself.
    /// </summary>
    internal required ScrollId?[] MovementOwner { get; init; }
}
