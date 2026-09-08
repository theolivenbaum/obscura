// Port of the flattened-tree traversal helpers in crates/obscura-render/src/dom.rs.
using Obscura.Dom;

namespace Obscura.Render;

/// <summary>
/// Flattened (composed) tree traversal used by render-tree construction, paint, and
/// resource discovery.
/// </summary>
internal static class DomTraversal
{
    /// <summary>
    /// Return the flattened-tree children that generate boxes for <paramref name="id"/>.
    /// </summary>
    /// <remarks>
    /// The single implementation lives in <see cref="Inline.RenderedChildren"/>; the inline
    /// layer landed first and dom.rs adopts it rather than keeping a second copy.
    /// </remarks>
    internal static List<NodeId> RenderedChildren(DomTree tree, NodeId id) =>
        Inline.RenderedChildren(tree, id);

    /// <summary>The parent of <paramref name="id"/> in the flattened rendering tree.</summary>
    /// <remarks>
    /// Shadow-root children are parented to the host for layout/paint ancestry; an assigned
    /// light child is parented to its slot; and an unslotted light child has no rendered
    /// parent at all.
    /// </remarks>
    internal static NodeId? RenderedParent(DomTree tree, NodeId id)
    {
        if (tree.GetNode(id)?.Parent is not { } parent)
        {
            return null;
        }

        if (tree.ShadowRootInfo(parent) is { } root)
        {
            return root.Host;
        }

        if (tree.ShadowRootOf(parent) is null)
        {
            return parent;
        }

        return tree.AssignedSlot(id);
    }

    /// <summary>Flattened-tree descendants in preorder, excluding <paramref name="root"/>.</summary>
    /// <remarks>
    /// The live-node bound and visited set are defense in depth against a corrupt
    /// assignment/tree graph; a valid flat tree visits every generated node once.
    /// </remarks>
    internal static List<NodeId> RenderedDescendants(DomTree tree, NodeId root)
    {
        int limit = tree.Count;
        List<NodeId> result = [];
        HashSet<NodeId> visited = [];
        List<NodeId> stack = RenderedChildren(tree, root);
        stack.Reverse();
        while (stack.Count > 0)
        {
            NodeId id = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (!visited.Add(id))
            {
                continue;
            }

            result.Add(id);
            if (result.Count >= limit)
            {
                break;
            }

            List<NodeId> children = RenderedChildren(tree, id);
            for (int index = children.Count - 1; index >= 0; index--)
            {
                stack.Add(children[index]);
            }
        }

        return result;
    }

    /// <summary>Children for computed-value inheritance traversal.</summary>
    /// <remarks>
    /// Assigned light children inherit through their flattened parent slot, while unslotted
    /// light DOM still receives computed CSSOM styles through its host. Fallback children
    /// remain in the style traversal even when not rendered.
    /// </remarks>
    internal static List<NodeId> StyleChildren(DomTree tree, NodeId id)
    {
        List<NodeId> children = tree.Children(id);
        if (tree.ShadowChildren(id) is { } shadowChildren)
        {
            children.RemoveAll(child => tree.AssignedSlot(child) is not null);
            children.AddRange(shadowChildren);
        }

        if (tree.AssignedNodes(id) is { } assigned)
        {
            children.AddRange(assigned);
        }

        return children;
    }

    internal static List<NodeId> ElementChildren(DomTree tree, NodeId parent)
    {
        List<NodeId> result = [];
        foreach (NodeId child in tree.Children(parent))
        {
            if (tree.GetNode(child)?.IsElement == true)
            {
                result.Add(child);
            }
        }

        return result;
    }

    internal static string? ElementLocalName(DomTree tree, NodeId node) =>
        tree.GetNode(node)?.AsElement()?.Name.Local;

    internal static bool IsLocal(DomTree tree, NodeId id, string local) =>
        string.Equals(ElementLocalName(tree, id), local, StringComparison.Ordinal);

    internal static bool IsAnyLocal(DomTree tree, NodeId id, params string[] locals)
    {
        string? local = ElementLocalName(tree, id);
        if (local is null)
        {
            return false;
        }

        foreach (string candidate in locals)
        {
            if (string.Equals(local, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text contents of a text node, or <c>null</c> for any other node kind.</summary>
    internal static string? TextContentOfTextNode(DomTree tree, NodeId id) =>
        tree.GetNode(id)?.TextContentOfTextNode;

    internal static bool IsTextNode(DomTree tree, NodeId id) =>
        tree.GetNode(id)?.IsText == true;
}
