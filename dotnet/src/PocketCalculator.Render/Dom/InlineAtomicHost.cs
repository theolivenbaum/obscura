using PocketCalculator.Dom;
using PocketCalculator.Render.Layout;
using NodeId = PocketCalculator.Dom.NodeId;
using TaffyNodeId = PocketCalculator.Render.Layout.NodeId;
using TaffyTree = PocketCalculator.Render.Layout.TaffyTree<int?>;

namespace PocketCalculator.Render;

/// <summary>
/// Lays the atomic inlines of the document's inline formatting contexts out (see
/// <see cref="IInlineAtomicHost{TNodeContext}"/>): the text engine sizes lines with their margin
/// boxes, and this answers each atomic's baseline from the DOM and its laid-out subtree.
/// </summary>
/// <remarks>
/// Baselines follow CSS 2.1 10.8.1 and Chromium 141: an inline-block's is the baseline of its
/// last line box, or its bottom margin edge when it has none or its <c>overflow</c> is not
/// <c>visible</c>; an inline-flex or inline-grid box takes the first baseline of its content;
/// a replaced element's is its bottom margin edge; a text field, select or button is centred on
/// the line of text it shows.
/// </remarks>
internal sealed class InlineAtomicHost(
    DomTree tree,
    TaffyTree taffyTree,
    IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
    IReadOnlyDictionary<NodeId, LayoutStyle> styles,
    TextEngine engine) : IInlineAtomicHost<int?>
{
    public void SetAtomic(int? context, int index, float width, float height, float? baseline)
    {
        if (context is { } item)
        {
            engine.SetAtomic(item, index, width, height, baseline);
        }
    }

    public (float X, float Y) AtomicPosition(int? context, int index) =>
        context is { } item ? engine.AtomicPosition(item, index) : (0f, 0f);

    public void CommitAtomics(int? context)
    {
        if (context is { } item)
        {
            engine.CommitAtomics(item);
        }
    }

    public float? AtomicBaseline(TaffyNodeId child, Size<float> size)
    {
        if (!idMap.TryGetValue(child, out NodeId dom)
            || !styles.TryGetValue(dom, out LayoutStyle? style)
            || DomTraversal.ElementLocalName(tree, dom) is not { } local)
        {
            return null;
        }

        if (local is "input" or "select" or "textarea" or "button" or "meter" or "progress"
            && !(tree.GetNode(dom) is { } control && PaintImages.IsImageButton(control)))
        {
            if (local == "button")
            {
                if (LastBaseline(child, size, depth: 0) is { } labelBaseline)
                {
                    return labelBaseline;
                }

                // Chromium 141 puts an empty button's baseline at its content-box bottom.
                (Rect<float> padding, Rect<float> border) = OwnEdges(child);
                return size.Height - padding.Bottom - border.Bottom;
            }

            // The line of text the control shows, centred in its box.
            (float ascent, float descent) = engine.InlineFontBoxMetrics(style);
            return ((size.Height - (ascent + descent)) / 2f) + ascent;
        }

        if (Inline.IsReplaced(local))
        {
            return null;
        }

        if (style.ClipsOverflowX() || style.ClipsOverflowY() || style.OverflowScrollContainer)
        {
            return null;
        }

        return style.Display is Display.Flex or Display.Grid && !style.InternalFlexContainer
            ? FirstBaseline(child, size, depth: 0)
            : LastBaseline(child, size, depth: 0);
    }

    /// <summary>The baseline of <paramref name="node"/>'s last line box, from its border-box top.</summary>
    private float? LastBaseline(TaffyNodeId node, Size<float> size, int depth) =>
        Baseline(node, size, OwnEdges(node), depth, last: true);

    private float? FirstBaseline(TaffyNodeId node, Size<float> size, int depth) =>
        Baseline(node, size, OwnEdges(node), depth, last: false);

    /// <summary>
    /// The padding and border of the atomic itself, whose own layout its parent has not stored
    /// yet; percentages are left out.
    /// </summary>
    private (Rect<float> Padding, Rect<float> Border) OwnEdges(TaffyNodeId node)
    {
        Layout.Style style = taffyTree.GetStyle(node);
        CalcResolver none = static (_, _) => 0f;
        return (style.Padding.ResolveOrZero((float?)null, none), style.Border.ResolveOrZero((float?)null, none));
    }

    private float? Baseline(
        TaffyNodeId node,
        Size<float> size,
        (Rect<float> Padding, Rect<float> Border) edges,
        int depth,
        bool last)
    {
        if (depth > 64)
        {
            return null;
        }

        float top = edges.Padding.Top + edges.Border.Top;
        if (taffyTree.GetNodeContext(node) is { } item)
        {
            float contentWidth = F32.Max(
                size.Width - edges.Padding.Left - edges.Padding.Right - edges.Border.Left - edges.Border.Right,
                0f);
            return engine.LineBaselineAt(item, contentWidth, last) is { } line ? top + line : null;
        }

        int count = taffyTree.ChildCount(node);
        for (int step = 0; step < count; step++)
        {
            TaffyNodeId child = taffyTree.ChildAtIndex(node, last ? count - 1 - step : step);
            Layout.Style childStyle = taffyTree.GetStyle(child);
            if (childStyle.Display == Layout.Display.None
                || childStyle.Position == Layout.Position.Absolute
                || childStyle.Float.FloatDirection() is not null)
            {
                continue;
            }

            if (idMap.TryGetValue(child, out NodeId childDom)
                && styles.TryGetValue(childDom, out LayoutStyle? childDomStyle)
                && (childDomStyle.ClipsOverflowX() || childDomStyle.ClipsOverflowY()))
            {
                // A block that clips its overflow lends the box no baseline of its own lines.
                continue;
            }

            Layout.Layout childLayout = taffyTree.GetUnroundedLayout(child);
            if (Baseline(child, childLayout.Size, (childLayout.Padding, childLayout.Border), depth + 1, last) is { } inner)
            {
                return childLayout.Location.Y + inner;
            }
        }

        return null;
    }
}
