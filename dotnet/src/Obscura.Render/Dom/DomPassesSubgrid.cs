// Port of the full-span column-subgrid reduction and the deferred cyclic flex inline-size
// resolution in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDirection = Obscura.Render.Layout.Direction;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyGridTemplateComponent = Obscura.Render.Layout.GridTemplateComponent;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyPosition = Obscura.Render.Layout.Position;
using TaffyStyle = Obscura.Render.Layout.Style;
using TaffyTree = Obscura.Render.Layout.TaffyTree<int?>;

namespace Obscura.Render;

internal readonly record struct ColumnSubgridWrapper(
    TaffyNodeId Node,
    float Gap,
    float StartMbp,
    float EndMbp);

internal readonly record struct ColumnSubgridLeaf(
    TaffyNodeId Node,
    NodeId Dom,
    int Column,
    float Gap,
    float StartMbp,
    float EndMbp);

internal sealed class ColumnSubgridPlan
{
    internal required TaffyNodeId Parent { get; init; }

    internal required int TrackCount { get; init; }

    internal required float ParentGap { get; init; }

    internal required List<ColumnSubgridWrapper> Wrappers { get; init; }

    internal required List<ColumnSubgridLeaf> Leaves { get; init; }
}

internal enum DeferredCyclicInlineSourceKind : byte
{
    Expression,
    Percent,
}

internal sealed record DeferredCyclicInlineSize(
    NodeId Node,
    NodeId FlexItem,
    int Slot,
    DeferredCyclicInlineSourceKind SourceKind,
    string? Expression,
    float Percent);

internal enum DeferredFlexReflowPhase : byte
{
    Layout,
    FitContent,
}

internal static class DomSubgridPasses
{
    /// <summary>
    /// Collect the deliberately bounded Grid Level 2 subset used by "aligned rows" components.
    /// </summary>
    private static bool CollectColumnSubgridDescendants(
        DomTree tree,
        NodeId dom,
        int trackCount,
        IReadOnlyDictionary<NodeId, TaffyNodeId> taffyByDom,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float startMbp,
        float endMbp,
        int depth,
        List<ColumnSubgridWrapper> wrappers,
        List<ColumnSubgridLeaf> leaves)
    {
        if (depth > 8)
        {
            return false;
        }

        if (!styles.TryGetValue(dom, out LayoutStyle? style))
        {
            return false;
        }

        if (!taffyByDom.TryGetValue(dom, out TaffyNodeId node))
        {
            return false;
        }

        startMbp += style.Margin.Left + style.Border.Left + style.Padding.Left;
        endMbp += style.Margin.Right + style.Border.Right + style.Padding.Right;
        float gap = style.ColumnGap ?? 0f;
        wrappers.Add(new ColumnSubgridWrapper(node, gap, startMbp, endMbp));

        List<NodeId> children = [];
        foreach (NodeId child in tree.Children(dom))
        {
            if (styles.TryGetValue(child, out LayoutStyle? childStyle)
                && childStyle.Display != Display.None)
            {
                children.Add(child);
            }
        }

        if (children.Count == 0)
        {
            return false;
        }

        int nested = 0;
        foreach (NodeId child in children)
        {
            if (styles.TryGetValue(child, out LayoutStyle? childStyle)
                && DomStyleFixups.IsFullSpanColumnSubgrid(childStyle))
            {
                nested++;
            }
        }

        if (nested > 0)
        {
            if (nested != children.Count)
            {
                return false;
            }

            foreach (NodeId child in children)
            {
                if (!CollectColumnSubgridDescendants(
                        tree,
                        child,
                        trackCount,
                        taffyByDom,
                        styles,
                        startMbp,
                        endMbp,
                        depth + 1,
                        wrappers,
                        leaves))
                {
                    return false;
                }
            }

            return true;
        }

        if (!DomStyleFixups.GridAutoFlowIsRow(style))
        {
            return false;
        }

        for (int index = 0; index < children.Count; index++)
        {
            NodeId child = children[index];
            if (!styles.TryGetValue(child, out LayoutStyle? childStyle))
            {
                return false;
            }

            // This first subset intentionally excludes spanning and explicitly placed items.
            if (!DomStyleFixups.HasOnlyAutoGridPlacement(childStyle)
                || childStyle.Position == TaffyPosition.Absolute
                || childStyle.MarginAuto[1]
                || childStyle.MarginAuto[3])
            {
                return false;
            }

            if (!taffyByDom.TryGetValue(child, out TaffyNodeId childNode))
            {
                return false;
            }

            leaves.Add(new ColumnSubgridLeaf(
                childNode, child, index % trackCount, gap, startMbp, endMbp));
        }

        return true;
    }

    /// <summary>Resolve a safe full-span column-subgrid subset in two passes.</summary>
    internal static bool ApplyFullSpanColumnSubgrids(
        DomTree tree,
        TaffyTree taffyTree,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        Func<TaffyTree, TaffyNodeId, float?> measureMaxContent)
    {
        Dictionary<NodeId, TaffyNodeId> taffyByDom = [];
        foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
        {
            taffyByDom[domId] = taffyId;
        }

        List<ColumnSubgridPlan> plans = [];

        foreach ((NodeId dom, LayoutStyle style) in styles)
        {
            if (style.Display != Display.Grid
                || style.Direction == TaffyDirection.Rtl
                || style.GridTemplateColumnsSubgrid
                || style.Width.Kind is not (DimensionKind.Px or DimensionKind.Percent)
                || !DomStyleFixups.JustifyContentIsStretch(style.JustifyContent))
            {
                continue;
            }

            if (!DomStyleFixups.GridTrackIsAllAuto(style.GridTemplateColumns)
                || style.GridTemplateColumns.Count > 32)
            {
                continue;
            }

            int trackCount = style.GridTemplateColumns.Count;
            List<NodeId> direct = [];
            foreach (NodeId child in tree.Children(dom))
            {
                if (styles.TryGetValue(child, out LayoutStyle? childStyle)
                    && childStyle.Display != Display.None)
                {
                    direct.Add(child);
                }
            }

            if (direct.Count == 0)
            {
                continue;
            }

            bool allFullSpan = true;
            foreach (NodeId child in direct)
            {
                if (!styles.TryGetValue(child, out LayoutStyle? childStyle)
                    || !DomStyleFixups.IsFullSpanColumnSubgrid(childStyle))
                {
                    allFullSpan = false;
                    break;
                }
            }

            if (!allFullSpan)
            {
                continue;
            }

            if (!taffyByDom.TryGetValue(dom, out TaffyNodeId parent))
            {
                continue;
            }

            List<ColumnSubgridWrapper> wrappers = [];
            List<ColumnSubgridLeaf> leaves = [];
            bool collected = true;
            foreach (NodeId child in direct)
            {
                if (!CollectColumnSubgridDescendants(
                        tree, child, trackCount, taffyByDom, styles, 0f, 0f, 0, wrappers, leaves))
                {
                    collected = false;
                    break;
                }
            }

            if (!collected || leaves.Count == 0)
            {
                continue;
            }

            plans.Add(new ColumnSubgridPlan
            {
                Parent = parent,
                TrackCount = trackCount,
                ParentGap = style.ColumnGap ?? 0f,
                Wrappers = wrappers,
                Leaves = leaves,
            });
        }

        bool changed = false;
        foreach (ColumnSubgridPlan plan in plans)
        {
            if (taffyTree.GetDetailedLayoutInfo(plan.Parent) is not Layout.DetailedGridInfo info
                || info.Columns.NegativeImplicitTracks != 0
                || info.Columns.PositiveImplicitTracks != 0
                || info.Columns.ExplicitTracks != plan.TrackCount)
            {
                continue;
            }

            float targetTrackSum = 0f;
            foreach (float size in info.Columns.Sizes)
            {
                targetTrackSum += size;
            }

            if (!float.IsFinite(targetTrackSum) || targetTrackSum <= 0f)
            {
                continue;
            }

            float[] maxContent = new float[plan.TrackCount];
            bool valid = true;
            foreach (ColumnSubgridLeaf leaf in plan.Leaves)
            {
                if (measureMaxContent(taffyTree, leaf.Node) is not { } measured)
                {
                    valid = false;
                    break;
                }

                if (!styles.TryGetValue(leaf.Dom, out LayoutStyle? leafStyle))
                {
                    valid = false;
                    break;
                }

                float contribution = measured + leafStyle.Margin.Left + leafStyle.Margin.Right;
                if (plan.TrackCount > 1)
                {
                    float gapDelta = leaf.Gap - plan.ParentGap;
                    contribution += leaf.Column == 0 || leaf.Column + 1 == plan.TrackCount
                        ? gapDelta / 2f
                        : gapDelta;
                }

                if (leaf.Column == 0)
                {
                    contribution += leaf.StartMbp;
                }

                if (leaf.Column + 1 == plan.TrackCount)
                {
                    contribution += leaf.EndMbp;
                }

                maxContent[leaf.Column] = F32.Max(maxContent[leaf.Column], F32.Max(contribution, 0f));
            }

            float maxSum = 0f;
            foreach (float width in maxContent)
            {
                maxSum += width;
            }

            if (!valid || maxSum > targetTrackSum + 0.01f)
            {
                continue;
            }

            float stretch = (targetTrackSum - maxSum) / plan.TrackCount;
            List<float> used = new(plan.TrackCount);
            bool usedValid = true;
            foreach (float width in maxContent)
            {
                float value = width + stretch;
                if (!float.IsFinite(value) || value < 0f)
                {
                    usedValid = false;
                    break;
                }

                used.Add(value);
            }

            if (!usedValid)
            {
                continue;
            }

            // Validate the entire copied chain before mutating anything.
            List<(TaffyNodeId Node, List<float> Widths)> copiedWrappers = new(plan.Wrappers.Count);
            foreach (ColumnSubgridWrapper wrapper in plan.Wrappers)
            {
                List<float> copied = [.. used];
                if (plan.TrackCount > 1)
                {
                    float rootHalf = plan.ParentGap / 2f;
                    float childHalf = wrapper.Gap / 2f;
                    for (int index = 0; index < copied.Count; index++)
                    {
                        copied[index] += index == 0 || index + 1 == plan.TrackCount
                            ? rootHalf - childHalf
                            : plan.ParentGap - wrapper.Gap;
                    }
                }

                copied[0] -= wrapper.StartMbp;
                copied[plan.TrackCount - 1] -= wrapper.EndMbp;
                bool copiedValid = true;
                foreach (float width in copied)
                {
                    if (!float.IsFinite(width) || width < 0f)
                    {
                        copiedValid = false;
                        break;
                    }
                }

                if (!copiedValid)
                {
                    valid = false;
                    break;
                }

                copiedWrappers.Add((wrapper.Node, copied));
            }

            if (!valid)
            {
                continue;
            }

            TaffyStyle parentStyle = taffyTree.GetStyle(plan.Parent).Clone();
            parentStyle.GridTemplateColumns = DomStyleFixups.FixedGridTracks(used);
            taffyTree.SetStyle(plan.Parent, parentStyle);
            foreach ((TaffyNodeId node, List<float> copied) in copiedWrappers)
            {
                TaffyStyle wrapperStyle = taffyTree.GetStyle(node).Clone();
                wrapperStyle.GridTemplateColumns = DomStyleFixups.FixedGridTracks(copied);
                taffyTree.SetStyle(node, wrapperStyle);
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Keep the non-percentage part of a cyclic flex descendant inline size for the intrinsic
    /// pass, and remember enough to resolve the complete expression afterwards.
    /// </summary>
    internal static List<DeferredCyclicInlineSize> DeferCyclicFlexInlineSizes(
        DomTree tree,
        Dictionary<NodeId, LayoutStyle> styles,
        float rootFs,
        float vw,
        float vh)
    {
        List<(NodeId Node, int Slot, DeferredCyclicInlineSourceKind Kind, string? Expression, float Percent)>
            candidates = [];
        foreach ((NodeId id, LayoutStyle style) in styles)
        {
            if (style.IgnoresUsedBoxSizes())
            {
                continue;
            }

            foreach (int slot in new[] { 0, 2, 4 })
            {
                string? functional = style.SizeExpressions[slot];
                if (functional is not null
                    && functional.Contains('%', StringComparison.Ordinal)
                    && (slot != 4 || !style.HasReplacedSizing))
                {
                    candidates.Add((
                        id, slot, DeferredCyclicInlineSourceKind.Expression, functional, 0f));
                    continue;
                }

                Dimension dimension = slot switch
                {
                    0 => style.Width,
                    2 => style.MinWidth,
                    _ => style.MaxWidth,
                };
                if (dimension.Kind == DimensionKind.Percent && (slot != 4 || !style.HasReplacedSizing))
                {
                    candidates.Add((
                        id, slot, DeferredCyclicInlineSourceKind.Percent, null, dimension.Value));
                }
            }
        }

        List<DeferredCyclicInlineSize> deferred = [];

        foreach ((NodeId id, int slot, DeferredCyclicInlineSourceKind kind, string? expression, float percent)
            in candidates)
        {
            // Start at the expression's containing-box chain, not the sized node itself.
            NodeId? candidate = DomTraversal.RenderedParent(tree, id);
            NodeId? flexItem = null;
            while (candidate is { } item)
            {
                NodeId? parent = DomTraversal.RenderedParent(tree, item);
                while (parent is { } parentId)
                {
                    if (!styles.TryGetValue(parentId, out LayoutStyle? parentStyle))
                    {
                        break;
                    }

                    if (parentStyle.DisplayContents)
                    {
                        parent = DomTraversal.RenderedParent(tree, parentId);
                        continue;
                    }

                    bool rowFlex = parentStyle.Display == Display.Flex
                        && !parentStyle.InternalFlexContainer
                        && parentStyle.FlexDirection
                            is not (TaffyFlexDirection.Column or TaffyFlexDirection.ColumnReverse);
                    // A declared inline size is only the item's used inline size when the flex
                    // algorithm cannot move it. `flex-grow` above zero, or the default
                    // `flex-shrink: 1`, both make the used width depend on the line's free
                    // space, so a descendant percentage resolved against the declaration
                    // samples the wrong containing block (Tesserae's
                    // `width: 1px; min-width: 0; flex-grow: 1` panel idiom collapsed every
                    // `calc(100% - 4px)` card inside it to 0).
                    bool itemIsIndefinite = styles.TryGetValue(item, out LayoutStyle? itemStyle)
                        && (itemStyle.Width.Kind != DimensionKind.Px
                            || (itemStyle.FlexGrow ?? 0f) > 0f
                            || (itemStyle.FlexShrink ?? 1f) != 0f
                            || (itemStyle.SizeExpressions[0] is { } itemExpression
                                && itemExpression.Contains('%', StringComparison.Ordinal)))
                        && itemStyle.Float is null
                        && itemStyle.Position != TaffyPosition.Absolute;
                    if (rowFlex && itemIsIndefinite)
                    {
                        flexItem = item;
                    }

                    break;
                }

                if (flexItem is not null)
                {
                    break;
                }

                candidate = DomTraversal.RenderedParent(tree, item);
            }

            if (flexItem is not { } resolvedFlexItem)
            {
                continue;
            }

            float em = styles.TryGetValue(id, out LayoutStyle? nodeStyle) && nodeStyle.FontSize is { } size
                ? size
                : rootFs;
            float? intrinsic = kind == DeferredCyclicInlineSourceKind.Expression
                ? ComputedStyle.ResolveContextualLength(expression!, em, rootFs, vw, vh, 0f)
                : 0f;
            if (intrinsic is not { } intrinsicValue)
            {
                continue;
            }

            if (styles.TryGetValue(id, out LayoutStyle? style))
            {
                // DEVIATION from crates/obscura-render/src/dom.rs, which neutralizes a cyclic
                // inline size to a definite `Px(max(value, 0))` - `0px` for the common
                // `width: 100%` and `calc(100% - Npx)`. CSS Sizing 3 says a cyclic percentage
                // behaves as `auto` for intrinsic contribution, and a definite zero instead
                // collapses the box for the whole intrinsic pass, which then pins its flex
                // item to the collapsed measurement. Tesserae's code-diff panel is two
                // `flex: 1 1 auto` items whose content is percentage-sized: with zero bases
                // they split the row evenly at 462px each instead of measuring 133px and
                // 791px, and the diff table then wrapped to three times its height. See
                // "Known deviations" in todo.md.
                Dimension value = kind == DeferredCyclicInlineSourceKind.Expression
                    ? Dimension.Auto
                    : Dimension.Px(F32.Max(intrinsicValue, 0f));
                switch (slot)
                {
                    case 0:
                        style.Width = value;
                        break;
                    case 2:
                        style.MinWidth = value;
                        break;
                    default:
                        // A cyclic percentage max-size behaves as its initial value during
                        // intrinsic contribution sizing.
                        style.MaxWidth = Dimension.Auto;
                        break;
                }
            }

            deferred.Add(new DeferredCyclicInlineSize(
                id, resolvedFlexItem, slot, kind, expression, percent));
        }

        return deferred;
    }

    internal static bool ResolveDeferredFlexInlineSizes(
        DomTree tree,
        TaffyTree taffyTree,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        Dictionary<NodeId, LayoutStyle> styles,
        IReadOnlyList<DeferredCyclicInlineSize> deferred,
        float rootFs,
        float vw,
        float vh,
        Action<TaffyTree, Dictionary<NodeId, LayoutStyle>, DeferredFlexReflowPhase> relayout)
    {
        if (deferred.Count == 0)
        {
            return false;
        }

        Dictionary<NodeId, TaffyNodeId> taffyByDom = [];
        foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
        {
            taffyByDom[domId] = taffyId;
        }

        // This is the final-reflow boundary: preserve the main size selected by the outer flex
        // algorithm while descendants are resolved against it.
        //
        // DEVIATION from crates/obscura-render/src/dom.rs
        // `resolve_deferred_flex_inline_sizes`, which pins every affected flex item in one
        // pass off the intrinsic-neutral layout and only then restores percentages. That is
        // right for an outermost item, whose used size the flex algorithm has already chosen,
        // but wrong for a nested one: its measurement is taken inside ancestors whose own
        // percentage widths are still neutralized to zero, so it gets pinned to its
        // min-content. On Tesserae's Stack sample an `.tss-stack{width:100%}` panel sat at 0
        // while the option row below it was pinned to 448px instead of 544px, wrapping every
        // two-word radio label. Pin outermost-first instead, restoring each level's
        // percentages and reflowing before measuring the next level down, so a nested item is
        // always measured under ancestors that already have their real width. See
        // "Known deviations" in todo.md.
        HashSet<NodeId> flexItems = [];
        foreach (DeferredCyclicInlineSize entry in deferred)
        {
            flexItems.Add(entry.FlexItem);
        }

        Dictionary<NodeId, int> renderedDepth = [];
        int DepthOf(NodeId node)
        {
            List<NodeId> chain = [];
            NodeId current = node;
            while (!renderedDepth.ContainsKey(current))
            {
                chain.Add(current);
                if (DomTraversal.RenderedParent(tree, current) is not { } parent)
                {
                    renderedDepth[current] = 0;
                    chain.RemoveAt(chain.Count - 1);
                    break;
                }

                current = parent;
            }

            for (int index = chain.Count - 1; index >= 0; index--)
            {
                NodeId step = chain[index];
                renderedDepth[step] = DomTraversal.RenderedParent(tree, step) is { } parent
                    ? renderedDepth[parent] + 1
                    : 0;
            }

            return renderedDepth.TryGetValue(node, out int depth) ? depth : 0;
        }

        List<NodeId> orderedFlexItems = [.. flexItems];
        orderedFlexItems.Sort((left, right) => DepthOf(left).CompareTo(DepthOf(right)));

        int levelStart = 0;
        while (levelStart < orderedFlexItems.Count)
        {
            int levelDepth = DepthOf(orderedFlexItems[levelStart]);
            int levelEnd = levelStart + 1;
            while (levelEnd < orderedFlexItems.Count
                && DepthOf(orderedFlexItems[levelEnd]) == levelDepth)
            {
                levelEnd++;
            }

            HashSet<NodeId> level = [];
            for (int index = levelStart; index < levelEnd; index++)
            {
                level.Add(orderedFlexItems[index]);
            }

            PinFlexItems(tree, taffyTree, taffyByDom, styles, deferred, level);
            RestoreTypedPercentages(taffyTree, taffyByDom, styles, deferred, level);
            relayout(taffyTree, styles, DeferredFlexReflowPhase.Layout);
            levelStart = levelEnd;
        }

        // Shrink-to-fit ancestors must be finalized while functional descendant widths still
        // have their intrinsic-neutral declarations.
        relayout(taffyTree, styles, DeferredFlexReflowPhase.FitContent);

        return ResolveFunctionalInlineSizes(
            tree, taffyTree, taffyByDom, styles, deferred, rootFs, vw, vh, relayout);
    }

    /// <summary>
    /// Pin each flex item in <paramref name="level"/> to the inline size the flex algorithm
    /// selected for it, so descendant percentages resolve against a definite basis.
    /// </summary>
    private static void PinFlexItems(
        DomTree tree,
        TaffyTree taffyTree,
        Dictionary<NodeId, TaffyNodeId> taffyByDom,
        Dictionary<NodeId, LayoutStyle> styles,
        IReadOnlyList<DeferredCyclicInlineSize> deferred,
        HashSet<NodeId> level)
    {
        foreach (NodeId flexItem in level)
        {
            if (!taffyByDom.TryGetValue(flexItem, out TaffyNodeId taffyId))
            {
                continue;
            }

            Layout.Layout layout = taffyTree.GetLayout(taffyId);
            if (!styles.TryGetValue(flexItem, out LayoutStyle? style))
            {
                continue;
            }

            float horizontalEdges = style.Padding.Left
                + style.Padding.Right
                + style.Border.Left
                + style.Border.Right;
            float usedDeclaration = style.BoxSizing == BoxSizing.ContentBox
                ? F32.Max(layout.Size.Width - horizontalEdges, 0f)
                : layout.Size.Width;

            // A content-sized flex item whose only definite inline content was a cyclic
            // percentage image measured 0 during the intrinsic pass: lift it to its deferred
            // images' natural width before pinning.
            if (style.Width.IsAuto && style.FlexBasis.IsAuto)
            {
                float naturalFloor = 0f;
                foreach (DeferredCyclicInlineSize entry in deferred)
                {
                    if (entry.FlexItem != flexItem || entry.Slot != 0)
                    {
                        continue;
                    }

                    // DEVIATION from crates/obscura-render/src/dom.rs, which floors the item
                    // at every deferred image's natural width unconditionally. That is only
                    // sound when the image can actually reach that size: an icon boxed by an
                    // ancestor with a definite inline size cannot. Tesserae's inline labels
                    // wrap a `width: 100%` SVG in a `width: 14px` span, and the reference
                    // lifted the whole 60px label to the SVG's natural width - 150px for a
                    // viewBox-only SVG (the 300x150 default object size at its ratio) and
                    // 512px for one with explicit dimensions. See "Known deviations" in
                    // todo.md.
                    if (styles.TryGetValue(entry.Node, out LayoutStyle? entryStyle)
                        && entryStyle.ReplacedIntrinsic is { } metadata
                        && metadata.NaturalSize() is { } natural
                        && float.IsFinite(natural.Width)
                        && natural.Width > 0f
                        && !HasDefiniteInlineAncestorBelow(tree, styles, entry.Node, flexItem))
                    {
                        naturalFloor = F32.Max(naturalFloor, natural.Width);
                    }
                }

                usedDeclaration = F32.Max(usedDeclaration, naturalFloor);
            }

            TaffyStyle pinned = taffyTree.GetStyle(taffyId).Clone();
            Layout.Size<TaffyDimension> pinnedSize = pinned.Size;
            pinnedSize.Width = TaffyDimension.FromLength(usedDeclaration);
            pinned.Size = pinnedSize;
            taffyTree.SetStyle(taffyId, pinned);
        }
    }

    /// <summary>
    /// Whether any box strictly between <paramref name="node"/> and <paramref name="flexItem"/>
    /// already has a definite inline size. Such a box caps what the descendant can contribute,
    /// so the descendant's natural size must not float the flex item.
    /// </summary>
    private static bool HasDefiniteInlineAncestorBelow(
        DomTree tree,
        Dictionary<NodeId, LayoutStyle> styles,
        NodeId node,
        NodeId flexItem)
    {
        NodeId? current = DomTraversal.RenderedParent(tree, node);
        for (int depth = 0; current is { } id && !id.Equals(flexItem) && depth < 64; depth++)
        {
            if (styles.TryGetValue(id, out LayoutStyle? style)
                && (style.Width.Kind is DimensionKind.Px or DimensionKind.Percent
                    || style.MaxWidth.Kind is DimensionKind.Px or DimensionKind.Percent))
            {
                return true;
            }

            current = DomTraversal.RenderedParent(tree, id);
        }

        return false;
    }

    /// <summary>
    /// Once a flex item's used inline size is definite, the plain percentages under it are no
    /// longer cyclic. Give them back to Taffy typed instead of flattened to pixels, so later
    /// ancestor changes propagate through nested 100%/400% descendants.
    /// </summary>
    private static void RestoreTypedPercentages(
        TaffyTree taffyTree,
        Dictionary<NodeId, TaffyNodeId> taffyByDom,
        Dictionary<NodeId, LayoutStyle> styles,
        IReadOnlyList<DeferredCyclicInlineSize> deferred,
        HashSet<NodeId> level)
    {
        foreach (DeferredCyclicInlineSize entry in deferred)
        {
            if (entry.SourceKind != DeferredCyclicInlineSourceKind.Percent
                || !level.Contains(entry.FlexItem))
            {
                continue;
            }

            TaffyDimension? restoreMaximum = null;
            if (styles.TryGetValue(entry.Node, out LayoutStyle? style))
            {
                Dimension value = Dimension.Percent(entry.Percent);
                switch (entry.Slot)
                {
                    case 0:
                        style.Width = value;

                        // The measured-leaf build encodes a percentage or fixed maximum inline
                        // size as `min(preferred, maximum)`, and the preferred width it sampled
                        // was the deferred zero.
                        restoreMaximum = style.MaxWidth.Kind switch
                        {
                            DimensionKind.Percent => TaffyDimension.FromPercent(style.MaxWidth.Value),
                            DimensionKind.Px => TaffyDimension.FromLength(
                                F32.Max(style.MaxWidth.Value, 0f)),
                            _ => TaffyDimension.Auto,
                        };
                        break;
                    case 2:
                        style.MinWidth = value;
                        break;
                    default:
                        style.MaxWidth = value;
                        break;
                }
            }

            if (!taffyByDom.TryGetValue(entry.Node, out TaffyNodeId nodeId))
            {
                continue;
            }

            TaffyStyle restored = taffyTree.GetStyle(nodeId).Clone();
            TaffyDimension percentValue = TaffyDimension.FromPercent(entry.Percent);
            switch (entry.Slot)
            {
                case 0:
                {
                    Layout.Size<TaffyDimension> size = restored.Size;
                    size.Width = percentValue;
                    restored.Size = size;
                    break;
                }

                case 2:
                {
                    Layout.Size<TaffyDimension> minSize = restored.MinSize;
                    minSize.Width = percentValue;
                    restored.MinSize = minSize;
                    break;
                }

                default:
                {
                    Layout.Size<TaffyDimension> maxSize = restored.MaxSize;
                    maxSize.Width = percentValue;
                    restored.MaxSize = maxSize;
                    break;
                }
            }

            if (restoreMaximum is { } maximum)
            {
                Layout.Size<TaffyDimension> maxSize = restored.MaxSize;
                maxSize.Width = maximum;
                restored.MaxSize = maxSize;
            }

            taffyTree.SetStyle(nodeId, restored);
        }
    }

    /// <summary>
    /// Taffy cannot retain an arbitrary calc()/min()/max()/clamp() expression in a box-size
    /// field. Resolve those expressions only after their parent has final geometry.
    /// </summary>
    private static bool ResolveFunctionalInlineSizes(
        DomTree tree,
        TaffyTree taffyTree,
        Dictionary<NodeId, TaffyNodeId> taffyByDom,
        Dictionary<NodeId, LayoutStyle> styles,
        IReadOnlyList<DeferredCyclicInlineSize> deferred,
        float rootFs,
        float vw,
        float vh,
        Action<TaffyTree, Dictionary<NodeId, LayoutStyle>, DeferredFlexReflowPhase> relayout)
    {
        HashSet<NodeId> functionalNodes = [];
        foreach (DeferredCyclicInlineSize entry in deferred)
        {
            if (entry.SourceKind == DeferredCyclicInlineSourceKind.Expression)
            {
                functionalNodes.Add(entry.Node);
            }
        }

        Dictionary<NodeId, int> rankCache = [];
        List<(int Rank, DeferredCyclicInlineSize Entry)> functional = [];
        foreach (DeferredCyclicInlineSize entry in deferred)
        {
            if (entry.SourceKind != DeferredCyclicInlineSourceKind.Expression)
            {
                continue;
            }

            List<NodeId> chain = [];
            NodeId current = entry.Node;
            while (!rankCache.ContainsKey(current))
            {
                chain.Add(current);
                if (DomTraversal.RenderedParent(tree, current) is not { } parent)
                {
                    break;
                }

                current = parent;
            }

            for (int index = chain.Count - 1; index >= 0; index--)
            {
                NodeId node = chain[index];
                int rank = 0;
                if (DomTraversal.RenderedParent(tree, node) is { } parent)
                {
                    rank = (rankCache.TryGetValue(parent, out int parentRank) ? parentRank : 0)
                        + (functionalNodes.Contains(parent) ? 1 : 0);
                }

                rankCache[node] = rank;
            }

            functional.Add((rankCache.TryGetValue(entry.Node, out int entryRank) ? entryRank : 0, entry));
        }

        functional = [.. functional.OrderBy(pair => pair.Rank)];

        int start = 0;
        while (start < functional.Count)
        {
            int currentRank = functional[start].Rank;
            int end = start + 1;
            while (end < functional.Count && functional[end].Rank == currentRank)
            {
                end++;
            }

            for (int index = start; index < end; index++)
            {
                DeferredCyclicInlineSize entry = functional[index].Entry;
                NodeId? containing = DomTraversal.RenderedParent(tree, entry.Node);
                float? basis = null;
                while (true)
                {
                    if (containing is not { } parent)
                    {
                        break;
                    }

                    if (styles.TryGetValue(parent, out LayoutStyle? parentStyle)
                        && parentStyle.DisplayContents)
                    {
                        containing = DomTraversal.RenderedParent(tree, parent);
                        continue;
                    }

                    if (!taffyByDom.TryGetValue(parent, out TaffyNodeId parentNode))
                    {
                        containing = DomTraversal.RenderedParent(tree, parent);
                        continue;
                    }

                    basis = F32.Max(taffyTree.GetLayout(parentNode).ContentBoxWidth(), 0f);
                    break;
                }

                if (basis is not { } resolvedBasis)
                {
                    continue;
                }

                float em = styles.TryGetValue(entry.Node, out LayoutStyle? entryStyle)
                    && entryStyle.FontSize is { } entryFontSize
                        ? entryFontSize
                        : rootFs;
                if (ComputedStyle.ResolveContextualLength(
                        entry.Expression!, em, rootFs, vw, vh, resolvedBasis) is not { } resolved)
                {
                    continue;
                }

                float value = F32.Max(resolved, 0f);
                if (styles.TryGetValue(entry.Node, out LayoutStyle? updated))
                {
                    Dimension dimension = Dimension.Px(value);
                    switch (entry.Slot)
                    {
                        case 0:
                            updated.Width = dimension;
                            break;
                        case 2:
                            updated.MinWidth = dimension;
                            break;
                        default:
                            updated.MaxWidth = dimension;
                            break;
                    }
                }

                if (!taffyByDom.TryGetValue(entry.Node, out TaffyNodeId nodeId))
                {
                    continue;
                }

                TaffyStyle resolvedStyle = taffyTree.GetStyle(nodeId).Clone();
                TaffyDimension length = TaffyDimension.FromLength(value);
                switch (entry.Slot)
                {
                    case 0:
                    {
                        Layout.Size<TaffyDimension> size = resolvedStyle.Size;
                        size.Width = length;
                        resolvedStyle.Size = size;
                        break;
                    }

                    case 2:
                    {
                        Layout.Size<TaffyDimension> minSize = resolvedStyle.MinSize;
                        minSize.Width = length;
                        resolvedStyle.MinSize = minSize;
                        break;
                    }

                    default:
                    {
                        Layout.Size<TaffyDimension> maxSize = resolvedStyle.MaxSize;
                        maxSize.Width = length;
                        resolvedStyle.MaxSize = maxSize;
                        break;
                    }
                }

                taffyTree.SetStyle(nodeId, resolvedStyle);
            }

            relayout(taffyTree, styles, DeferredFlexReflowPhase.Layout);
            start = end;
        }

        return true;
    }
}
