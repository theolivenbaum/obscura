using PocketCalculator.Dom;

namespace PocketCalculator.Render;

/// <summary><c>Range.getClientRects()</c> from the line fragments of the laid-out text.</summary>
public sealed partial class PreparedRender
{
    private (DomLayout Layout, Dictionary<NodeId, (int Item, TextNodeChunk Chunk)> Chunks)? _textChunks;

    /// <summary>
    /// The client rects of the DOM range (<paramref name="startContainer"/>,
    /// <paramref name="startOffset"/>)..(<paramref name="endContainer"/>,
    /// <paramref name="endOffset"/>), in the viewport, as CSSOM View defines them: the border
    /// boxes (<c>getClientRects()</c>) of every element the range contains whose parent it does
    /// not, and for every text node it contains or partly contains, one rect per line for the
    /// selected text (a zero-width one at a collapsed boundary in a text node), in tree order.
    /// </summary>
    /// <remarks>
    /// Not in crates/obscura-render; the Rust shim's <c>Range.getClientRects()</c> is the common
    /// ancestor element's box. A text node laid out word by word (no shaped context) reports its
    /// whole words.
    /// </remarks>
    public List<Rect> RangeClientRects(
        DomTree tree,
        ResolvedScrollState scroll,
        NodeId startContainer,
        int startOffset,
        NodeId endContainer,
        int endOffset)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(scroll);
        List<Rect> output = [];
        if (tree.GetNode(startContainer) is not { } startNode || tree.GetNode(endContainer) is not { } endNode)
        {
            return output;
        }

        if (startContainer == endContainer && startNode.Data is TextData)
        {
            AppendTextRects(tree, scroll, startContainer, startOffset, endOffset, output);
            return output;
        }

        if (startNode.Data is TextData startText)
        {
            AppendTextRects(tree, scroll, startContainer, startOffset, startText.Contents.Length, output);
        }

        NodeId root = startContainer;
        int bound = tree.SlotCount;
        for (int i = 0; tree.GetNode(root)?.Parent is { } parent && i <= bound; i++)
        {
            root = parent;
        }

        NodeId? first = startNode.Data is TextData
            ? tree.NextAfterSubtree(root, startContainer)
            : ChildAt(tree, startContainer, startOffset) ?? tree.NextAfterSubtree(root, startContainer);
        NodeId? stop = endNode.Data is TextData
            ? endContainer
            : ChildAt(tree, endContainer, endOffset) ?? tree.NextAfterSubtree(root, endContainer);

        // A node the walk reaches is contained unless the end boundary is inside it.
        HashSet<NodeId> endAncestors = [];
        for (NodeId? up = endContainer; up is { } id && endAncestors.Count <= bound; up = tree.GetNode(id)?.Parent)
        {
            endAncestors.Add(id);
        }

        HashSet<NodeId> contained = [];
        int steps = 0;
        for (NodeId? node = first; node is { } id && id != stop; node = tree.NextInSubtree(root, id))
        {
            if ((++steps & 1023) == 0)
            {
                WorkCancellation.ThrowIfCancellationRequested();
            }

            if (steps > bound || tree.GetNode(id) is not { } current)
            {
                break;
            }

            if (endAncestors.Contains(id))
            {
                continue;
            }

            contained.Add(id);
            if (current.Data is TextData text)
            {
                AppendTextRects(tree, scroll, id, 0, text.Contents.Length, output);
            }
            else if (current.IsElement
                && !(current.Parent is { } parent && contained.Contains(parent))
                && ViewportClientRectsWithScroll(id, scroll) is { } rects)
            {
                output.AddRange(rects);
            }
        }

        if (endNode.Data is TextData)
        {
            AppendTextRects(tree, scroll, endContainer, 0, endOffset, output);
        }

        return output;
    }

    private static NodeId? ChildAt(DomTree tree, NodeId parent, int index)
    {
        if (index < 0)
        {
            return null;
        }

        NodeId? child = tree.GetNode(parent)?.FirstChild;
        for (int i = 0; child is { } id && i < index; i++)
        {
            child = tree.GetNode(id)?.NextSibling;
        }

        return child;
    }

    private void AppendTextRects(
        DomTree tree,
        ResolvedScrollState scroll,
        NodeId text,
        int from,
        int to,
        List<Rect> output)
    {
        if (tree.GetNode(text)?.Data is not TextData data)
        {
            return;
        }

        from = Math.Clamp(from, 0, data.Contents.Length);
        to = Math.Clamp(to, from, data.Contents.Length);
        NodeId? owner = DomTraversal.RenderedParent(tree, text);
        int before = output.Count;
        if (TextChunks().TryGetValue(text, out (int Item, TextNodeChunk Chunk) found)
            && owner is { } parentId
            && Layout.Styles.TryGetValue(parentId, out LayoutStyle? style))
        {
            int start = Inline.CollectedOffset(data.Contents, found.Chunk, from);
            int end = Inline.CollectedOffset(data.Contents, found.Chunk, to);
            if (from == to || end > start)
            {
                Layout.TextEngine.AppendRangeRects(
                    found.Item,
                    start,
                    end,
                    from == to,
                    Layout.TextEngine.InlineFontBoxMetrics(style),
                    output);
            }
        }
        else if (Layout.TextRuns.TryGetValue(text, out List<(Rect Rect, string Text)>? words) && from < to)
        {
            foreach ((Rect rect, _) in words)
            {
                output.Add(rect);
            }
        }

        if (owner is not { } mapping)
        {
            return;
        }

        bool transformed = Layout.Transforms.TryGetValue(mapping, out Affine2 transform);
        (float X, float Y) movement = scroll.MovementFor(mapping);
        for (int i = before; i < output.Count; i++)
        {
            Rect rect = transformed ? transform.MapRect(output[i]) : output[i];
            output[i] = rect with { X = rect.X + movement.X, Y = rect.Y + movement.Y };
        }
    }

    /// <summary>Each text node of a shaped inline formatting context, and the item holding it.</summary>
    private Dictionary<NodeId, (int Item, TextNodeChunk Chunk)> TextChunks()
    {
        if (_textChunks is { } cached && ReferenceEquals(cached.Layout, Layout))
        {
            return cached.Chunks;
        }

        Dictionary<NodeId, (int Item, TextNodeChunk Chunk)> chunks = [];
        void Add(int item)
        {
            foreach (TextNodeChunk chunk in Layout.TextEngine.TextNodeChunks(item))
            {
                chunks[chunk.Node] = (item, chunk);
            }
        }

        foreach (int item in Layout.IfcItems.Values)
        {
            Add(item);
        }

        foreach (List<int> items in Layout.RunIfcItems.Values)
        {
            foreach (int item in items)
            {
                Add(item);
            }
        }

        _textChunks = (Layout, chunks);
        return chunks;
    }
}
