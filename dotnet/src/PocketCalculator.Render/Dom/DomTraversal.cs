// Port of the flattened-tree traversal helpers in crates/obscura-render/src/dom.rs.
using PocketCalculator.Dom;

namespace PocketCalculator.Render;

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

    /// <summary>
    /// <see cref="RenderedChildren"/> without the list: a node that is not a shadow host, a slot
    /// or a <c>details</c> renders exactly its DOM children, read off the sibling chain, and only
    /// the others build the list. For the whole-document walks of every pass, which allocated a
    /// list per node.
    /// </summary>
    internal static RenderedChildEnumerable EachRenderedChild(DomTree tree, NodeId id)
    {
        if (tree.GetNode(id) is not { } node)
        {
            return default;
        }

        bool plain = tree.ShadowRootOf(id) is null
            && !(node.AsElement() is { } element
                && string.Equals(element.Name.Ns, Namespaces.Html, StringComparison.Ordinal)
                && element.Name.Local is "slot" or "details");
        return plain
            ? new RenderedChildEnumerable(tree, node.FirstChild, null)
            : new RenderedChildEnumerable(tree, null, RenderedChildren(tree, id));
    }

    internal readonly struct RenderedChildEnumerable(DomTree? tree, NodeId? first, List<NodeId>? list)
    {
        public Enumerator GetEnumerator() => new(tree, first, list);

        internal struct Enumerator(DomTree? tree, NodeId? first, List<NodeId>? list)
        {
            private NodeId? _next = first;
            private int _index = -1;
            private int _steps;

            public NodeId Current { get; private set; }

            public bool MoveNext()
            {
                if (list is not null)
                {
                    if (++_index < list.Count)
                    {
                        Current = list[_index];
                        return true;
                    }

                    return false;
                }

                // The sibling-chain bound of DomTree.Children.
                if (_next is not { } id || tree is null || ++_steps > tree.SlotCount)
                {
                    return false;
                }

                Current = id;
                _next = tree.GetNode(id)?.NextSibling;
                return true;
            }
        }
    }

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
        if (root == tree.Document && ReferenceEquals(t_walkTree, tree))
        {
            return t_documentWalk ??= WalkRenderedDescendants(tree, root);
        }

        return WalkRenderedDescendants(tree, root);
    }

    // The document's flat tree for the prepare running on this thread; see DocumentWalkScope.
    [ThreadStatic]
    private static DomTree? t_walkTree;

    [ThreadStatic]
    private static List<NodeId>? t_documentWalk;

    /// <summary>
    /// Within the scope, <see cref="RenderedDescendants"/> of <paramref name="tree"/>'s document
    /// is walked once and the same list handed to every caller, which only reads it.
    /// </summary>
    /// <remarks>
    /// Not in crates/obscura-render. A prepare walks the whole flat tree for images, fonts,
    /// the retained-style plan, fixed and sticky boxes and the scroll tree, each allocating its
    /// own list and visited set (nvidia.com: eight walks of 7,000 nodes a forced read). Nothing
    /// mutates the DOM while a prepare runs, so they all walk the same tree.
    /// </remarks>
    internal static DocumentWalkScope ShareDocumentWalk(DomTree tree)
    {
        DocumentWalkScope scope = new(t_walkTree, t_documentWalk);
        t_walkTree = tree;
        t_documentWalk = null;
        return scope;
    }

    internal readonly struct DocumentWalkScope(DomTree? savedTree, List<NodeId>? savedWalk) : IDisposable
    {
        public void Dispose()
        {
            t_walkTree = savedTree;
            t_documentWalk = savedWalk;
        }
    }

    private static List<NodeId> WalkRenderedDescendants(DomTree tree, NodeId root)
    {
        int limit = tree.Count;
        List<NodeId> result = root == tree.Document ? new(limit) : [];

        // Indexed by arena slot: a live node owns its slot, so this is the same visited set as
        // one keyed by id, without hashing every node of the document.
        bool[] visited = new bool[tree.SlotCount];
        List<NodeId> stack = [];
        List<NodeId> scratch = [];
        PushRenderedChildrenReversed(tree, root, stack, scratch);
        while (stack.Count > 0)
        {
            NodeId id = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            int slot = id.Index;
            if ((uint)slot < (uint)visited.Length)
            {
                if (visited[slot])
                {
                    continue;
                }

                visited[slot] = true;
            }

            result.Add(id);
            if (result.Count >= limit)
            {
                break;
            }

            PushRenderedChildrenReversed(tree, id, stack, scratch);
        }

        return result;
    }

    /// <summary>
    /// Push <see cref="RenderedChildren"/> of <paramref name="id"/> onto
    /// <paramref name="stack"/> in reverse order.
    /// </summary>
    /// <remarks>
    /// An element that is not a shadow host, a slot or a <c>details</c> renders exactly its
    /// DOM children, which are read off the sibling chain here instead of being copied into a
    /// fresh list per node; every other node takes <see cref="RenderedChildren"/> itself.
    /// </remarks>
    private static void PushRenderedChildrenReversed(
        DomTree tree,
        NodeId id,
        List<NodeId> stack,
        List<NodeId> scratch)
    {
        if (tree.GetNode(id) is not { } node)
        {
            return;
        }

        bool plain = tree.ShadowRootOf(id) is null
            && !(node.AsElement() is { } element
                && string.Equals(element.Name.Ns, Namespaces.Html, StringComparison.Ordinal)
                && element.Name.Local is "slot" or "details");
        if (!plain)
        {
            List<NodeId> children = RenderedChildren(tree, id);
            for (int index = children.Count - 1; index >= 0; index--)
            {
                stack.Add(children[index]);
            }

            return;
        }

        scratch.Clear();
        int cap = tree.SlotCount;
        for (NodeId? child = node.FirstChild; child is { } cid; child = tree.GetNode(cid)?.NextSibling)
        {
            scratch.Add(cid);

            // The sibling-chain bound of DomTree.Children.
            if (scratch.Count > cap)
            {
                break;
            }
        }

        for (int index = scratch.Count - 1; index >= 0; index--)
        {
            stack.Add(scratch[index]);
        }
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

    /// <summary>
    /// <c>tree.QuerySelector("html")</c>: the first <c>html</c> element in document order,
    /// which is the document element whenever that is one - read off the document's children
    /// rather than matched against every element of the document, as the selector did on every
    /// prepare.
    /// </summary>
    internal static NodeId? HtmlElement(DomTree tree)
    {
        foreach (NodeId child in tree.Children(tree.Document))
        {
            if (tree.GetNode(child)?.AsElement() is { } element)
            {
                return string.Equals(element.Name.Local, "html", StringComparison.Ordinal)
                    ? child
                    : tree.QuerySelector("html");
            }
        }

        return tree.QuerySelector("html");
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

    internal static bool IsAnyLocal(DomTree tree, NodeId id, params ReadOnlySpan<string> locals)
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
