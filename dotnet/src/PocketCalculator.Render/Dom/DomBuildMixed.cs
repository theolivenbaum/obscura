// Port of `build_mixed_block` in crates/obscura-render/src/dom.rs.
using PocketCalculator.Dom;
using TaffyDimension = PocketCalculator.Render.Layout.Dimension;
using TaffyDisplay = PocketCalculator.Render.Layout.Display;
using TaffyLengthPercentageAuto = PocketCalculator.Render.Layout.LengthPercentageAuto;
using TaffyNodeId = PocketCalculator.Render.Layout.NodeId;
using TaffyPosition = PocketCalculator.Render.Layout.Position;
using TaffyStyle = PocketCalculator.Render.Layout.Style;

namespace PocketCalculator.Render;

internal static partial class DomBuild
{
    private enum SegKind : byte
    {
        Run,
        Block,
        Float,
    }

    private sealed class Seg
    {
        internal SegKind Kind { get; init; }

        internal List<NodeId> Run { get; init; } = [];

        internal NodeId Block { get; init; }

        /// <summary>A run whose floats stay in its inline formatting context as anchors.</summary>
        internal bool Anchored { get; init; }
    }

    /// <summary>
    /// In a document with floats an inline-run wrapper is an anonymous block that avoids them
    /// as a whole (its line of atomic inlines narrows beside a float), so it stretches like a
    /// block instead of taking 100% of the containing block, which would never fit beside one.
    /// </summary>
    private static TaffyStyle FloatFlowWrapper(TaffyStyle style, bool floatFlow)
    {
        if (floatFlow)
        {
            style.Size = new Layout.Size<TaffyDimension>(TaffyDimension.Auto, style.Size.Height);

            // Narrowed beside a float it is still only a line box: an inline-block's
            // `width: 65%` is of the block, as in Chromium (wikipedia.org's footer).
            style.PercentBasisFromContainingBlock = true;
        }

        return style;
    }

    /// <summary>
    /// Decide, for each inline run holding floats, whether the floats stay in the run's inline
    /// formatting context as anchors (the run folds to one shaped context) or are split out as
    /// block-level floats between the pieces of the run (it does not fold, or it holds nothing
    /// but floats).
    /// </summary>
    private static List<Seg> AnchorOrSplitFloats(
        BuildContext context,
        NodeId parent,
        List<Seg> segs,
        bool beforePending,
        bool afterPending)
    {
        DomTree tree = context.Tree;
        bool IsFloat(NodeId cid) =>
            context.Styles.TryGetValue(cid, out LayoutStyle? style)
            && style.Float is not null
            && style.Display != Display.None
            && style.Position != TaffyPosition.Absolute;
        bool IsWhitespace(NodeId cid) =>
            tree.GetNode(cid) is { IsText: true } && tree.TextContent(cid).Trim().Length == 0;

        List<Seg> expanded = new(segs.Count);
        for (int index = 0; index < segs.Count; index++)
        {
            Seg seg = segs[index];
            if (seg.Kind != SegKind.Run || !seg.Run.Exists(IsFloat))
            {
                expanded.Add(seg);
                continue;
            }

            bool onlyFloats = seg.Run.TrueForAll(cid => IsFloat(cid) || IsWhitespace(cid));
            bool joinsPseudo = (beforePending && index == 0) || (afterPending && index + 1 == segs.Count);
            if (!onlyFloats && !joinsPseudo && TextEngine.CanFoldRun(tree, parent, seg.Run, context.Styles))
            {
                expanded.Add(new Seg { Kind = SegKind.Run, Run = seg.Run, Anchored = true });
                continue;
            }

            Seg? current = null;
            foreach (NodeId cid in seg.Run)
            {
                if (IsFloat(cid))
                {
                    if (current is not null)
                    {
                        expanded.Add(current);
                        current = null;
                    }

                    expanded.Add(new Seg { Kind = SegKind.Float, Block = cid });
                }
                else
                {
                    current ??= new Seg { Kind = SegKind.Run };
                    current.Run.Add(cid);
                }
            }

            if (current is not null)
            {
                expanded.Add(current);
            }
        }

        return expanded;
    }

    /// <summary>
    /// Build a block container whose children mix inline-level and block-level content,
    /// preserving real block layout for the block children.
    /// </summary>
    /// <remarks>
    /// With <paramref name="floatFlow"/> this is also the block container of a document with
    /// floats (CSS 2.1 9.5): floated children become native taffy floats placed by the block
    /// formatting context, `clear` reaches the block children, and each inline run is an
    /// inline formatting context leaf in the same BFC, whose line boxes the text engine shortens
    /// around the floats. DEVIATION from crates/obscura-render/src/dom.rs
    /// (<c>build_children_with_float_zone</c>), which laid a float and the siblings after it out
    /// as a flex row, so text never wrapped around a float and nothing below it reflowed.
    /// </remarks>
    internal static TaffyNodeId? BuildMixedBlock(
        BuildContext context,
        NodeId id,
        LayoutStyle style,
        TaffyStyle taffyStyle,
        IReadOnlyList<NodeId> domChildren,
        bool floatFlow = false)
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

                // A float joins the inline run around it: anchored to its line when the run
                // folds to one inline formatting context, a block-level float otherwise.
                inlineLevel = isForcedBreak
                    || LayoutStyleExtensions.IsInlineLevelBox(childStyle)
                    || (floatFlow && childStyle.Float is not null);
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

        if (floatFlow)
        {
            segs = AnchorOrSplitFloats(context, id, segs, beforePending, afterPending);
        }

        int segCount = segs.Count;
        List<TaffyNodeId> childIds = [];
        if (beforeBlock)
        {
            if (floatFlow && style.BeforePseudo is { } beforeStyle)
            {
                foreach (TaffyNodeId node in beforeLeaves)
                {
                    SetNativeFloatClear(context, node, beforeStyle, true);
                }
            }

            childIds.AddRange(beforeLeaves);
        }

        for (int i = 0; i < segs.Count; i++)
        {
            Seg seg = segs[i];
            if (seg.Kind == SegKind.Float)
            {
                if (context.Styles.TryGetValue(seg.Block, out LayoutStyle? floatStyle))
                {
                    foreach (TaffyNodeId node in BuildAny(context, seg.Block))
                    {
                        SetNativeFloatClear(context, node, floatStyle, false);
                        childIds.Add(node);
                    }
                }

                continue;
            }

            if (seg.Kind == SegKind.Block)
            {
                List<TaffyNodeId> built = BuildAny(context, seg.Block);
                if (floatFlow
                    && context.Styles.TryGetValue(seg.Block, out LayoutStyle? clearStyle)
                    && clearStyle.Clear is not null)
                {
                    foreach (TaffyNodeId node in built)
                    {
                        SetNativeFloatClear(context, node, clearStyle, false);
                    }
                }

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
                && context.Engine.TryBuildRun(tree, id, run, context.Styles, seg.Anchored) is { } item)
            {
                TaffyStyle leafStyle = RunLeafStyle();
                if (floatFlow)
                {
                    // An anonymous block box in the same BFC, so its line boxes see the floats.
                    leafStyle.Display = TaffyDisplay.Block;
                }

                TaffyNodeId leaf = context.TaffyTree.NewLeafWithContext(leafStyle, item);
                if (!context.Ifc.Runs.TryGetValue(id, out List<int>? items))
                {
                    items = [];
                    context.Ifc.Runs[id] = items;
                }

                items.Add(item);
                childIds.Add(leaf);
                if (seg.Anchored && context.Engine.FloatAnchorsOf(item) is { Count: > 0 } anchors)
                {
                    // The floats follow their inline formatting context, which block layout
                    // places them from (BlockLayout's anchored floats).
                    List<(TaffyNodeId Float, int Offset)> anchored = [];
                    foreach ((NodeId floatDom, int offset) in anchors)
                    {
                        if (!context.Styles.TryGetValue(floatDom, out LayoutStyle? floatStyle))
                        {
                            continue;
                        }

                        foreach (TaffyNodeId node in BuildAny(context, floatDom))
                        {
                            SetNativeFloatClear(context, node, floatStyle, false);
                            childIds.Add(node);
                            anchored.Add((node, offset));
                        }
                    }

                    if (anchored.Count != 0)
                    {
                        context.Ifc.FloatAnchors[leaf] = [.. anchored];
                    }
                }

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
                FloatFlowWrapper(RunWrapperStyle(style, hasTextStrut), floatFlow), [.. atoms]);
            childIds.Add(wrapper);
        }

        // Pseudo content that found no adjacent run to join.
        if (beforePending)
        {
            TaffyNodeId wrapper = context.TaffyTree.NewWithChildren(
                FloatFlowWrapper(RunWrapperStyle(style, true), floatFlow), [.. beforeLeaves]);
            childIds.Insert(0, wrapper);
        }

        if (afterPending)
        {
            TaffyNodeId wrapper = context.TaffyTree.NewWithChildren(
                FloatFlowWrapper(RunWrapperStyle(style, true), floatFlow), [.. afterLeaves]);
            childIds.Add(wrapper);
        }

        if (afterBlock)
        {
            if (floatFlow && style.AfterPseudo is { } afterStyle)
            {
                foreach (TaffyNodeId node in afterLeaves)
                {
                    SetNativeFloatClear(context, node, afterStyle, true);
                }
            }

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
