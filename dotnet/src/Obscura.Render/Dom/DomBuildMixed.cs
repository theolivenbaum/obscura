// Port of `build_mixed_block` in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDisplay = Obscura.Render.Layout.Display;
using TaffyLengthPercentageAuto = Obscura.Render.Layout.LengthPercentageAuto;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyPosition = Obscura.Render.Layout.Position;
using TaffyStyle = Obscura.Render.Layout.Style;

namespace Obscura.Render;

internal static partial class DomBuild
{
    private enum SegKind : byte
    {
        Run,
        Block,
    }

    private sealed class Seg
    {
        internal SegKind Kind { get; init; }

        internal List<NodeId> Run { get; init; } = [];

        internal NodeId Block { get; init; }
    }

    /// <summary>
    /// Build a block container whose children mix inline-level and block-level content,
    /// preserving real block layout for the block children.
    /// </summary>
    internal static TaffyNodeId? BuildMixedBlock(
        BuildContext context,
        NodeId id,
        LayoutStyle style,
        TaffyStyle taffyStyle,
        IReadOnlyList<NodeId> domChildren)
    {
        DomTree tree = context.Tree;

        // The parent is a genuine block container. `ToTaffyStyle` may have promoted it to a
        // flex column for `text-align`; undo that.
        taffyStyle.Display = TaffyDisplay.Block;

        // Splice display:contents children and drop nothing else.
        List<NodeId> flat = [];
        FlattenContentsChildren(tree, domChildren, context.Styles, flat);

        List<Seg> segs = [];
        List<NodeId> outOfFlow = [];
        foreach (NodeId cid in flat)
        {
            if (tree.GetNode(cid) is not { } node)
            {
                continue;
            }

            bool isText = node.IsText;

            // Comments, doctypes, and other non-rendered DOM nodes generate no CSS box and
            // cannot interrupt an inline formatting context.
            if (!isText && !node.IsElement)
            {
                continue;
            }

            bool inlineLevel;
            if (isText)
            {
                inlineLevel = true;
            }
            else if (context.Styles.TryGetValue(cid, out LayoutStyle? childStyle))
            {
                if (childStyle.Display == Display.None)
                {
                    continue;
                }

                if (childStyle.Position == TaffyPosition.Absolute)
                {
                    outOfFlow.Add(cid);
                    continue;
                }

                bool isForcedBreak = node.AsElement() is { } element
                    && string.Equals(element.Name.Local, "br", StringComparison.Ordinal);
                inlineLevel = isForcedBreak || LayoutStyleExtensions.IsInlineLevelBox(childStyle);
            }
            else
            {
                inlineLevel = false;
            }

            if (inlineLevel)
            {
                if (segs.Count > 0 && segs[^1].Kind == SegKind.Run)
                {
                    segs[^1].Run.Add(cid);
                }
                else
                {
                    segs.Add(new Seg { Kind = SegKind.Run, Run = [cid] });
                }
            }
            else
            {
                segs.Add(new Seg { Kind = SegKind.Block, Block = cid });
            }
        }

        // Inline pseudos join the first/last inline run. A block-level pseudo is a real direct
        // child of this block.
        var before = BuildInFlowPseudo(context, id, GeneratedBoxKind.Before, style.BeforePseudo);
        var after = BuildInFlowPseudo(context, id, GeneratedBoxKind.After, style.AfterPseudo);
        List<TaffyNodeId> beforeLeaves = before?.Nodes ?? [];
        bool beforeBlock = before?.BlockLevel ?? false;
        List<TaffyNodeId> afterLeaves = after?.Nodes ?? [];
        bool afterBlock = after?.BlockLevel ?? false;
        bool beforePending = !beforeBlock && beforeLeaves.Count != 0;
        bool afterPending = !afterBlock && afterLeaves.Count != 0;

        int segCount = segs.Count;
        List<TaffyNodeId> childIds = [];
        if (beforeBlock)
        {
            childIds.AddRange(beforeLeaves);
        }

        for (int i = 0; i < segs.Count; i++)
        {
            Seg seg = segs[i];
            if (seg.Kind == SegKind.Block)
            {
                List<TaffyNodeId> built = BuildAny(context, seg.Block);
                if (style.LegacyCenter)
                {
                    bool hasDefaultHorizontalMargins =
                        context.Styles.TryGetValue(seg.Block, out LayoutStyle? child)
                        && child.Margin.Left == 0f
                        && child.Margin.Right == 0f
                        && !child.MarginAuto[1]
                        && !child.MarginAuto[3];
                    if (hasDefaultHorizontalMargins)
                    {
                        foreach (TaffyNodeId node in built)
                        {
                            TaffyStyle centered = context.TaffyTree.GetStyle(node).Clone();
                            Layout.Rect<TaffyLengthPercentageAuto> margin = centered.Margin;
                            margin.Left = TaffyLengthPercentageAuto.Auto;
                            margin.Right = TaffyLengthPercentageAuto.Auto;
                            centered.Margin = margin;
                            context.TaffyTree.SetStyle(node, centered);
                        }
                    }
                }

                childIds.AddRange(built);
                continue;
            }

            bool hasTextStrut = false;
            foreach (NodeId cid in seg.Run)
            {
                if (DomTraversal.IsTextNode(tree, cid))
                {
                    hasTextStrut = true;
                    break;
                }
            }

            // Collapsible source formatting at the start/end of an inline run does not create
            // line width.
            bool IsWhitespaceText(NodeId cid) =>
                tree.GetNode(cid) is { } node
                && node.IsText
                && tree.TextContent(cid).Trim().Length == 0;

            int start = seg.Run.Count;
            for (int index = 0; index < seg.Run.Count; index++)
            {
                if (!IsWhitespaceText(seg.Run[index]))
                {
                    start = index;
                    break;
                }
            }

            int end = start;
            for (int index = seg.Run.Count - 1; index >= 0; index--)
            {
                if (!IsWhitespaceText(seg.Run[index]))
                {
                    end = index + 1;
                    break;
                }
            }

            List<NodeId> run = start < end ? seg.Run.GetRange(start, end - start) : [];
            bool joinBefore = beforePending && i == 0;
            bool joinAfter = afterPending && i + 1 == segCount;

            // Fast path: the whole run folds to one shaped leaf.
            if (!joinBefore && !joinAfter
                && context.Engine.TryBuildRun(tree, id, run, context.Styles) is { } item)
            {
                TaffyNodeId leaf = context.TaffyTree.NewLeafWithContext(RunLeafStyle(), item);
                if (!context.Ifc.Runs.TryGetValue(id, out List<int>? items))
                {
                    items = [];
                    context.Ifc.Runs[id] = items;
                }

                items.Add(item);
                childIds.Add(leaf);
                continue;
            }

            List<TaffyNodeId> atoms = [];
            if (joinBefore)
            {
                atoms.AddRange(beforeLeaves);
                beforePending = false;
            }

            foreach (NodeId rc in run)
            {
                bool isForcedBreak = DomTraversal.IsLocal(tree, rc, "br");
                if (isForcedBreak)
                {
                    // A BR participates in the current line with zero inline size, then forces
                    // the following content onto a new line.
                    atoms.AddRange(BuildAny(context, rc));
                    TaffyStyle breakerStyle = TaffyStyle.Default;
                    breakerStyle.FlexGrow = 0f;
                    breakerStyle.FlexShrink = 0f;
                    breakerStyle.FlexBasis = TaffyDimension.FromPercent(1f);
                    breakerStyle.Size = new Layout.Size<TaffyDimension>(
                        TaffyDimension.FromPercent(1f),
                        TaffyDimension.FromLength(0f));
                    atoms.Add(context.TaffyTree.NewLeaf(breakerStyle));
                }
                else
                {
                    atoms.AddRange(BuildAny(context, rc));
                }
            }

            if (joinAfter)
            {
                atoms.AddRange(afterLeaves);
                afterPending = false;
            }

            if (atoms.Count == 0)
            {
                // Whitespace-only run between blocks: no anonymous box.
                continue;
            }

            TaffyNodeId wrapper = context.TaffyTree.NewWithChildren(
                RunWrapperStyle(style, hasTextStrut), [.. atoms]);
            childIds.Add(wrapper);
        }

        // Pseudo content that found no adjacent run to join.
        if (beforePending)
        {
            TaffyNodeId wrapper = context.TaffyTree.NewWithChildren(
                RunWrapperStyle(style, true), [.. beforeLeaves]);
            childIds.Insert(0, wrapper);
        }

        if (afterPending)
        {
            TaffyNodeId wrapper = context.TaffyTree.NewWithChildren(
                RunWrapperStyle(style, true), [.. afterLeaves]);
            childIds.Add(wrapper);
        }

        if (afterBlock)
        {
            childIds.AddRange(afterLeaves);
        }

        foreach (NodeId cid in outOfFlow)
        {
            childIds.AddRange(BuildAny(context, cid));
        }

        TaffyNodeId taffyId = childIds.Count == 0
            ? context.TaffyTree.NewLeaf(taffyStyle)
            : context.TaffyTree.NewWithChildren(taffyStyle, [.. childIds]);
        context.IdMap[taffyId] = id;
        return taffyId;
    }
}
