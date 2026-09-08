// Port of the post-layout repair passes in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using TaffyAlignContent = Obscura.Render.Layout.AlignContent;
using TaffyAlignItems = Obscura.Render.Layout.AlignItems;
using TaffyAvailableSpace = Obscura.Render.Layout.AvailableSpace;
using TaffyBoxSizing = Obscura.Render.Layout.BoxSizing;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDisplay = Obscura.Render.Layout.Display;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyGridPlacementKind = Obscura.Render.Layout.GridPlacementKind;
using TaffyGridTemplateComponent = Obscura.Render.Layout.GridTemplateComponent;
using TaffyJustifyContent = Obscura.Render.Layout.AlignContent;
using TaffyLayout = Obscura.Render.Layout.Layout;
using TaffyLengthPercentageAuto = Obscura.Render.Layout.LengthPercentageAuto;
using TaffyMaxTrack = Obscura.Render.Layout.MaxTrackSizingFunction;
using TaffyMinTrack = Obscura.Render.Layout.MinTrackSizingFunction;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyPosition = Obscura.Render.Layout.Position;
using TaffyStyle = Obscura.Render.Layout.Style;
using TaffyTrackSizingFunction = Obscura.Render.Layout.TrackSizingFunction;
using TaffyTree = Obscura.Render.Layout.TaffyTree<int?>;

namespace Obscura.Render;

internal readonly record struct StaticPositionCandidate(
    TaffyNodeId Child,
    TaffyNodeId Target,
    bool InlineAxis,
    bool BlockAxis);

internal readonly record struct FloatBand(
    float Top,
    float Bottom,
    float Left,
    float Right,
    Float Side);

internal static class DomPasses
{
    internal static void SyncResolvedPercentagePadding(
        TaffyTree taffyTree,
        TaffyNodeId taffyRoot,
        float initialContainingBlockWidth,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyList<GeneratedBoxBuild> generated,
        Dictionary<NodeId, LayoutStyle> styles)
    {
        bool hasPercentagePadding = false;
        foreach (LayoutStyle style in styles.Values)
        {
            if (AnyPercentagePadding(style)
                || (style.BeforePseudo is { } before && AnyPercentagePadding(before))
                || (style.AfterPseudo is { } after && AnyPercentagePadding(after)))
            {
                hasPercentagePadding = true;
                break;
            }
        }

        if (!hasPercentagePadding)
        {
            return;
        }

        Dictionary<TaffyNodeId, (NodeId Host, GeneratedBoxKind Kind)> generatedMap = [];
        foreach (GeneratedBoxBuild build in generated)
        {
            generatedMap[build.Node] = (build.Host, build.Kind);
        }

        Visit(taffyRoot, initialContainingBlockWidth);

        void Visit(TaffyNodeId node, float containingBlockWidth)
        {
            TaffyLayout layout = taffyTree.GetLayout(node);
            if (idMap.TryGetValue(node, out NodeId domId))
            {
                if (styles.TryGetValue(domId, out LayoutStyle? style))
                {
                    Sync(style, containingBlockWidth);
                }
            }
            else if (generatedMap.TryGetValue(node, out var owner)
                && styles.TryGetValue(owner.Host, out LayoutStyle? hostStyle))
            {
                LayoutStyle? pseudo = owner.Kind == GeneratedBoxKind.Before
                    ? hostStyle.BeforePseudo
                    : hostStyle.AfterPseudo;
                if (pseudo is not null)
                {
                    Sync(pseudo, containingBlockWidth);
                }
            }

            float childContainingBlockWidth = F32.Max(layout.ContentBoxWidth(), 0f);
            foreach (TaffyNodeId child in taffyTree.Children(node))
            {
                Visit(child, childContainingBlockWidth);
            }
        }

        static void Sync(LayoutStyle style, float containingBlockWidth)
        {
            Edges padding = style.Padding;
            if (style.PaddingPercent[0] is { } top)
            {
                padding = padding with { Top = top * containingBlockWidth };
            }

            if (style.PaddingPercent[1] is { } right)
            {
                padding = padding with { Right = right * containingBlockWidth };
            }

            if (style.PaddingPercent[2] is { } bottom)
            {
                padding = padding with { Bottom = bottom * containingBlockWidth };
            }

            if (style.PaddingPercent[3] is { } left)
            {
                padding = padding with { Left = left * containingBlockWidth };
            }

            style.Padding = padding;
        }
    }

    private static bool AnyPercentagePadding(LayoutStyle style)
    {
        foreach (float? value in style.PaddingPercent)
        {
            if (value is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reify the block-axis percentage basis of a definite floated/inline-block containing
    /// block after the preliminary layout.
    /// </summary>
    internal static bool ResolveAtomicPercentageHeights(
        DomTree tree,
        TaffyTree taffyTree,
        TaffyNodeId taffyRoot,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        HashSet<NodeId> definiteHeightNodes)
    {
        bool changed = false;
        Visit(taffyRoot, null, 0f);
        return changed;

        void Visit(TaffyNodeId node, NodeId? nearestDomParent, float containingBlockHeight)
        {
            TaffyLayout layout = taffyTree.GetLayout(node);
            float ownContentHeight = F32.Max(layout.ContentBoxHeight(), 0f);
            NodeId? domId = idMap.TryGetValue(node, out NodeId found) ? found : null;

            if (domId is { } dom && nearestDomParent is { } parentId)
            {
                bool isDirectDomChild = DomTraversal.RenderedParent(tree, dom) == parentId;
                float? childPercent = null;
                if (styles.TryGetValue(dom, out LayoutStyle? childStyle)
                    && childStyle.SizeExpressions[1] is null
                    && !childStyle.IgnoresUsedBoxSizes()
                    && childStyle.Position != TaffyPosition.Absolute
                    && childStyle.Height.Kind == DimensionKind.Percent)
                {
                    childPercent = childStyle.Height.Value;
                }

                bool parentIsDefiniteAtomic = styles.TryGetValue(parentId, out LayoutStyle? parentStyle)
                    && (parentStyle.Float is not null || parentStyle.IsInlineBlock)
                    && definiteHeightNodes.Contains(parentId)
                    && parentStyle.SizeExpressions[1] is null;
                if (isDirectDomChild
                    && parentIsDefiniteAtomic
                    && float.IsFinite(containingBlockHeight)
                    && childPercent is { } percent)
                {
                    TaffyStyle resolved = taffyTree.GetStyle(node).Clone();
                    Layout.Size<TaffyDimension> size = resolved.Size;
                    size.Height = TaffyDimension.FromLength(
                        F32.Max(percent * containingBlockHeight, 0f));
                    resolved.Size = size;
                    taffyTree.SetStyle(node, resolved);
                    changed = true;
                }
            }

            NodeId? nextDomParent = domId ?? nearestDomParent;
            float nextContainingBlockHeight = domId is not null
                ? ownContentHeight
                : containingBlockHeight;
            foreach (TaffyNodeId child in taffyTree.Children(node))
            {
                Visit(child, nextDomParent, nextContainingBlockHeight);
            }
        }
    }

    internal static void SyncPositionedPseudoPercentagePadding(
        IReadOnlyDictionary<NodeId, Rect> rects,
        Dictionary<NodeId, LayoutStyle> styles)
    {
        bool hasPositionedPercentagePadding = false;
        foreach (LayoutStyle style in styles.Values)
        {
            foreach (LayoutStyle? pseudo in new[] { style.BeforePseudo, style.AfterPseudo })
            {
                if (pseudo is not null
                    && pseudo.Position == TaffyPosition.Absolute
                    && AnyPercentagePadding(pseudo))
                {
                    hasPositionedPercentagePadding = true;
                    break;
                }
            }

            if (hasPositionedPercentagePadding)
            {
                break;
            }
        }

        if (!hasPositionedPercentagePadding)
        {
            return;
        }

        foreach ((NodeId host, LayoutStyle style) in styles)
        {
            if (!rects.TryGetValue(host, out Rect rect))
            {
                continue;
            }

            float containingBlockWidth =
                F32.Max(rect.Width - style.Border.Left - style.Border.Right, 0f);
            foreach (LayoutStyle? pseudo in new[] { style.BeforePseudo, style.AfterPseudo })
            {
                if (pseudo is null || pseudo.Position != TaffyPosition.Absolute)
                {
                    continue;
                }

                Edges padding = pseudo.Padding;
                for (int index = 0; index < 4; index++)
                {
                    if (pseudo.PaddingPercent[index] is not { } percent)
                    {
                        continue;
                    }

                    float value = percent * containingBlockWidth;
                    padding = index switch
                    {
                        0 => padding with { Top = value },
                        1 => padding with { Right = value },
                        2 => padding with { Bottom = value },
                        _ => padding with { Left = value },
                    };
                }

                pseudo.Padding = padding;
            }
        }
    }

    internal static void ComputeAbsoluteRects(
        TaffyTree taffyTree,
        TaffyNodeId taffyId,
        float absX,
        float absY,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<TaffyNodeId, (NodeId Source, string Word)> words,
        Dictionary<NodeId, Rect> rects,
        Dictionary<NodeId, List<(Rect Rect, string Text)>> textRuns,
        Dictionary<int, Rect> anonRects,
        IReadOnlyDictionary<TaffyNodeId, int> generatedNodes,
        Rect?[] generatedRects)
    {
        TaffyLayout layout = taffyTree.GetLayout(taffyId);
        float x = absX + layout.Location.X;
        float y = absY + layout.Location.Y;
        Rect rect = new(x, y, layout.Size.Width, layout.Size.Height);

        if (idMap.TryGetValue(taffyId, out NodeId domId))
        {
            rects[domId] = rect;
        }
        else if (taffyTree.TryGetNodeContext(taffyId, out int? item) && item is { } index)
        {
            // A taffy leaf with an engine-item context but no DOM id is an anonymous
            // inline-run leaf; record its final rect by item index.
            anonRects[index] = rect;
        }

        if (generatedNodes.TryGetValue(taffyId, out int generatedIndex))
        {
            generatedRects[generatedIndex] = rect;
        }

        // A word leaf's dom id is its owning text node, shared by every other word from the
        // same node, so this appends rather than overwrites.
        if (words.TryGetValue(taffyId, out var word))
        {
            if (!textRuns.TryGetValue(word.Source, out List<(Rect, string)>? runs))
            {
                runs = [];
                textRuns[word.Source] = runs;
            }

            runs.Add((rect, word.Word));
        }

        foreach (TaffyNodeId childId in taffyTree.Children(taffyId))
        {
            ComputeAbsoluteRects(
                taffyTree,
                childId,
                x,
                y,
                idMap,
                words,
                rects,
                textRuns,
                anonRects,
                generatedNodes,
                generatedRects);
        }
    }

    /// <summary>
    /// Attach positioned boxes to their CSS containing block rather than their immediate DOM
    /// parent.
    /// </summary>
    internal static List<StaticPositionCandidate> ReparentInsetPositionedNodes(
        DomTree tree,
        TaffyTree taffyTree,
        TaffyNodeId taffyRoot,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        Dictionary<NodeId, TaffyNodeId> reverse = [];
        foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
        {
            reverse[domId] = taffyId;
        }

        Dictionary<NodeId, TaffyNodeId> nearestAbsCbForChildren = [];
        Dictionary<NodeId, TaffyNodeId> nearestFixedCbForChildren = [];
        List<StaticPositionCandidate> staticCandidates = [];

        foreach (NodeId domId in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            if (!styles.TryGetValue(domId, out LayoutStyle? style))
            {
                continue;
            }

            NodeId? parent = DomTraversal.RenderedParent(tree, domId);
            TaffyNodeId inheritedAbsCb = taffyRoot;
            TaffyNodeId inheritedFixedCb = taffyRoot;
            if (parent is { } parentId)
            {
                if (nearestAbsCbForChildren.TryGetValue(parentId, out TaffyNodeId abs))
                {
                    inheritedAbsCb = abs;
                }

                if (nearestFixedCbForChildren.TryGetValue(parentId, out TaffyNodeId fix))
                {
                    inheritedFixedCb = fix;
                }
            }

            // Record this before any candidate early-exit so all descendants get O(1)
            // nearest-containing-block lookups.
            TaffyNodeId? ownBox = reverse.TryGetValue(domId, out TaffyNodeId own) ? own : null;
            bool establishesCb = style.EstablishesPositioningContainingBlock();
            TaffyNodeId absChildCb = style.Position is not null || establishesCb
                ? ownBox ?? inheritedAbsCb
                : inheritedAbsCb;
            TaffyNodeId fixedChildCb = establishesCb ? ownBox ?? inheritedFixedCb : inheritedFixedCb;
            nearestAbsCbForChildren[domId] = absChildCb;
            nearestFixedCbForChildren[domId] = fixedChildCb;

            if (style.Position != TaffyPosition.Absolute)
            {
                continue;
            }

            bool hasBlockInset = style.Inset[0] is not null || style.Inset[2] is not null;
            bool hasInlineInset = style.Inset[1] is not null || style.Inset[3] is not null;
            if (!reverse.TryGetValue(domId, out TaffyNodeId child))
            {
                continue;
            }

            TaffyNodeId target = style.PositionFixed ? inheritedFixedCb : inheritedAbsCb;
            if (taffyTree.Parent(child) is not { } current)
            {
                continue;
            }

            if (current == target)
            {
                continue;
            }

            if (!hasBlockInset || !hasInlineInset)
            {
                staticCandidates.Add(new StaticPositionCandidate(
                    child, target, !hasInlineInset, !hasBlockInset));
                continue;
            }

            taffyTree.RemoveChild(current, child);
            taffyTree.AddChild(target, child);
        }

        return staticCandidates;
    }

    internal static (float X, float Y)? TaffyGlobalOrigin(TaffyTree taffyTree, TaffyNodeId node)
    {
        TaffyNodeId? current = node;
        float x = 0f;
        float y = 0f;
        while (current is { } id)
        {
            TaffyLayout layout = taffyTree.GetLayout(id);
            x += layout.Location.X;
            y += layout.Location.Y;
            current = taffyTree.Parent(id);
        }

        return (x, y);
    }

    internal static void CollectTaffyGlobalRects(
        TaffyTree taffyTree,
        TaffyNodeId node,
        float parentX,
        float parentY,
        Dictionary<TaffyNodeId, Rect> rects)
    {
        TaffyLayout layout = taffyTree.GetLayout(node);
        float x = parentX + layout.Location.X;
        float y = parentY + layout.Location.Y;
        rects[node] = new Rect(x, y, layout.Size.Width, layout.Size.Height);
        foreach (TaffyNodeId child in taffyTree.Children(node))
        {
            CollectTaffyGlobalRects(taffyTree, child, x, y, rects);
        }
    }

    private static bool NarrowNodeToFloatBand(
        TaffyTree taffyTree,
        TaffyNodeId node,
        IReadOnlyDictionary<TaffyNodeId, Rect> preliminaryRects,
        FloatBand band)
    {
        if (!preliminaryRects.TryGetValue(node, out Rect rect))
        {
            return false;
        }

        if (rect.Y >= band.Bottom || rect.Y + rect.Height <= band.Top || rect.Width <= 0f)
        {
            return false;
        }

        TaffyStyle current = taffyTree.GetStyle(node);
        TaffyLayout layout = taffyTree.GetLayout(node);
        TaffyStyle narrowed = current.Clone();
        float available;
        float leftShift;
        if (band.Side == Float.Right)
        {
            if (band.Left >= rect.X + rect.Width)
            {
                return false;
            }

            available = F32.Max(band.Left - rect.X, 0f);
            leftShift = 0f;
        }
        else
        {
            if (band.Right <= rect.X)
            {
                return false;
            }

            leftShift = F32.Max(band.Right - rect.X, 0f);
            available = F32.Max(rect.Width - leftShift, 0f);
        }

        if (available >= rect.Width - 0.01f)
        {
            return false;
        }

        float specified = current.BoxSizing == TaffyBoxSizing.ContentBox
            ? F32.Max(
                available
                - layout.Padding.Left
                - layout.Padding.Right
                - layout.Border.Left
                - layout.Border.Right,
                0f)
            : available;
        Layout.Size<TaffyDimension> size = narrowed.Size;
        size.Width = TaffyDimension.FromLength(specified);
        narrowed.Size = size;
        Layout.Size<TaffyDimension> maxSize = narrowed.MaxSize;
        maxSize.Width = TaffyDimension.FromLength(specified);
        narrowed.MaxSize = maxSize;
        if (leftShift > 0f)
        {
            Layout.Rect<TaffyLengthPercentageAuto> margin = narrowed.Margin;
            margin.Left = TaffyLengthPercentageAuto.FromLength(layout.Margin.Left + leftShift);
            narrowed.Margin = margin;
        }

        taffyTree.SetStyle(node, narrowed);
        return true;
    }

    private static bool GrowBfcToFloatBottom(
        TaffyTree taffyTree,
        TaffyNodeId node,
        IReadOnlyDictionary<TaffyNodeId, Rect> preliminaryRects,
        float floatBottom)
    {
        if (!preliminaryRects.TryGetValue(node, out Rect rect))
        {
            return false;
        }

        float desiredBorderHeight = F32.Max(floatBottom - rect.Y, 0f);
        if (desiredBorderHeight <= rect.Height + 0.01f)
        {
            return false;
        }

        TaffyStyle current = taffyTree.GetStyle(node);
        TaffyLayout layout = taffyTree.GetLayout(node);
        float specified = current.BoxSizing == TaffyBoxSizing.ContentBox
            ? F32.Max(
                desiredBorderHeight
                - layout.Padding.Top
                - layout.Padding.Bottom
                - layout.Border.Top
                - layout.Border.Bottom,
                0f)
            : desiredBorderHeight;
        TaffyStyle grown = current.Clone();
        Layout.Size<TaffyDimension> minSize = grown.MinSize;
        minSize.Height = TaffyDimension.FromLength(specified);
        grown.MinSize = minSize;
        taffyTree.SetStyle(node, grown);
        return true;
    }

    private static bool NarrowIntersectingDescendants(
        DomTree tree,
        NodeId id,
        TaffyTree taffyTree,
        IReadOnlyDictionary<NodeId, TaffyNodeId> reverse,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IfcRegistry ifc,
        IReadOnlyDictionary<TaffyNodeId, Rect> preliminaryRects,
        FloatBand band)
    {
        if (!reverse.TryGetValue(id, out TaffyNodeId node))
        {
            return false;
        }

        if (!preliminaryRects.TryGetValue(node, out Rect rect))
        {
            return false;
        }

        if (rect.Y >= band.Bottom || rect.Y + rect.Height <= band.Top)
        {
            return false;
        }

        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return false;
        }

        if (style.Display == Display.None
            || style.Float is not null
            || style.Position == TaffyPosition.Absolute)
        {
            return false;
        }

        // Float-avoiding formatting contexts move as one box.
        List<NodeId> inFlowElementChildren = [];
        foreach (NodeId child in tree.Children(id))
        {
            if (styles.TryGetValue(child, out LayoutStyle? childStyle)
                && childStyle.Display != Display.None
                && childStyle.Float is null
                && childStyle.Position != TaffyPosition.Absolute)
            {
                inFlowElementChildren.Add(child);
            }
        }

        if (DomStyleFixups.EstablishesBlockFormattingContext(style)
            || ifc.Whole.ContainsKey(id)
            || inFlowElementChildren.Count == 0)
        {
            return NarrowNodeToFloatBand(taffyTree, node, preliminaryRects, band);
        }

        bool changed = false;
        foreach (NodeId child in inFlowElementChildren)
        {
            changed |= NarrowIntersectingDescendants(
                tree, child, taffyTree, reverse, styles, ifc, preliminaryRects, band);
        }

        return changed;
    }

    internal static bool ApplyFloatContinuations(
        DomTree tree,
        TaffyTree taffyTree,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IfcRegistry ifc)
    {
        if (ifc.FloatContinuations.Count == 0)
        {
            return false;
        }

        Dictionary<NodeId, TaffyNodeId> reverse = [];
        TaffyNodeId? root = null;
        foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
        {
            reverse[domId] = taffyId;
            if (taffyTree.Parent(taffyId) is null)
            {
                root ??= taffyId;
            }
        }

        if (root is not { } rootNode)
        {
            return false;
        }

        Dictionary<TaffyNodeId, Rect> preliminaryRects = new(idMap.Count);
        CollectTaffyGlobalRects(taffyTree, rootNode, 0f, 0f, preliminaryRects);
        bool changed = false;
        foreach (FloatContinuation continuation in ifc.FloatContinuations)
        {
            if (!preliminaryRects.TryGetValue(continuation.Float, out Rect floatRect))
            {
                continue;
            }

            TaffyLayout floatLayout = taffyTree.GetLayout(continuation.Float);
            if (!preliminaryRects.TryGetValue(continuation.Flow, out Rect flowRect))
            {
                continue;
            }

            FloatBand band = new(
                floatRect.Y - floatLayout.Margin.Top,
                floatRect.Y + floatRect.Height + floatLayout.Margin.Bottom,
                floatRect.X - floatLayout.Margin.Left,
                floatRect.X + floatRect.Width + floatLayout.Margin.Right,
                continuation.Side);
            if (band.Bottom <= flowRect.Y + flowRect.Height + 0.01f)
            {
                continue;
            }

            // A non-BFC wrapper is transparent to the BFC's float manager.
            NodeId current = continuation.Owner;
            while (DomTraversal.RenderedParent(tree, current) is { } parent)
            {
                List<NodeId> siblings = DomTraversal.RenderedChildren(tree, parent);
                int index = siblings.IndexOf(current);
                if (index < 0)
                {
                    break;
                }

                for (int sibling = index + 1; sibling < siblings.Count; sibling++)
                {
                    changed |= NarrowIntersectingDescendants(
                        tree,
                        siblings[sibling],
                        taffyTree,
                        reverse,
                        styles,
                        ifc,
                        preliminaryRects,
                        band);
                }

                bool reachedBfc = styles.TryGetValue(parent, out LayoutStyle? parentStyle)
                    && DomStyleFixups.EstablishesBlockFormattingContext(parentStyle);
                if (reachedBfc)
                {
                    if (parentStyle is not null
                        && parentStyle.Height.IsAuto
                        && reverse.TryGetValue(parent, out TaffyNodeId bfcNode))
                    {
                        changed |= GrowBfcToFloatBottom(
                            taffyTree, bfcNode, preliminaryRects, band.Bottom);
                    }

                    break;
                }

                current = parent;
            }
        }

        return changed;
    }

    /// <summary>
    /// Recompute table row tracks from the cells' content at their final column widths.
    /// </summary>
    internal static bool ApplyTableRowGeometry(
        TaffyTree taffyTree,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IfcRegistry ifc)
    {
        bool changed = false;
        foreach ((TaffyNodeId tableNode, List<float?> rowMinimums) in ifc.TableRows)
        {
            int nrows = rowMinimums.Count;
            if (nrows == 0)
            {
                continue;
            }

            List<float> rowHeights = new(nrows);
            foreach (float? height in rowMinimums)
            {
                rowHeights.Add(F32.Max(height ?? 0f, 0f));
            }

            List<(TaffyNodeId Cell, int Row, int Span, float Natural)> cells = [];
            foreach (TaffyNodeId cell in taffyTree.Children(tableNode))
            {
                TaffyStyle cellStyle = taffyTree.GetStyle(cell);
                TaffyLayout layout = taffyTree.GetLayout(cell);
                if (cellStyle.GridRow.Start.Kind != TaffyGridPlacementKind.Line)
                {
                    continue;
                }

                int row = Math.Max(cellStyle.GridRow.Start.LineIndex, (short)1) - 1;
                int span = cellStyle.GridRow.End.Kind == TaffyGridPlacementKind.Span
                    ? Math.Max(cellStyle.GridRow.End.SpanCount, (ushort)1)
                    : 1;
                if (row >= nrows)
                {
                    continue;
                }

                float edges = layout.Padding.Top
                    + layout.Padding.Bottom
                    + layout.Border.Top
                    + layout.Border.Bottom;

                // Taffy's content_size is the furthest content/child overflow in border-box
                // coordinates, so it is already the right natural border-box floor.
                float natural = F32.Max(layout.ContentSize.Height, edges);
                if (idMap.TryGetValue(cell, out NodeId domId)
                    && styles.TryGetValue(domId, out LayoutStyle? style)
                    && style.Height.Kind == DimensionKind.Px)
                {
                    float specified = style.BoxSizing == BoxSizing.ContentBox
                        ? style.Height.Value
                            + style.Padding.Top
                            + style.Padding.Bottom
                            + style.Border.Top
                            + style.Border.Bottom
                        : style.Height.Value;
                    natural = F32.Max(natural, specified);
                }

                cells.Add((cell, row, Math.Min(span, nrows - row), F32.Max(natural, 0f)));
            }

            // Non-spanning cells define their row directly.
            foreach ((_, int row, int span, float natural) in cells)
            {
                if (span == 1)
                {
                    rowHeights[row] = F32.Max(rowHeights[row], natural);
                }
            }

            float rowGap = 0f;
            bool tableHasHeight = false;
            if (idMap.TryGetValue(tableNode, out NodeId tableDom)
                && styles.TryGetValue(tableDom, out LayoutStyle? tableStyle))
            {
                rowGap = DomStyleFixups.TableSpacing(tableStyle).Vertical;
                tableHasHeight = tableStyle.Height.Kind == DimensionKind.Px;
            }

            // A rowspan contributes only the shortfall beyond the rows and gaps it spans.
            foreach ((_, int row, int span, float natural) in cells)
            {
                if (span <= 1)
                {
                    continue;
                }

                int end = row + span;
                float current = 0f;
                for (int index = row; index < end; index++)
                {
                    current += rowHeights[index];
                }

                current += rowGap * Math.Max(span - 1, 0);
                float extra = natural - current;
                if (extra <= 0f)
                {
                    continue;
                }

                List<int> targets = [];
                for (int index = row; index < end; index++)
                {
                    if (rowMinimums[index] is null)
                    {
                        targets.Add(index);
                    }
                }

                if (targets.Count == 0)
                {
                    for (int index = row; index < end; index++)
                    {
                        targets.Add(index);
                    }
                }

                float weight = 0f;
                foreach (int index in targets)
                {
                    weight += rowHeights[index];
                }

                foreach (int index in targets)
                {
                    float share = weight > 0f
                        ? extra * rowHeights[index] / weight
                        : extra / targets.Count;
                    rowHeights[index] += share;
                }
            }

            // A definite table height distributes surplus into rows.
            if (tableHasHeight)
            {
                TaffyLayout tableLayout = taffyTree.GetLayout(tableNode);
                float trackTarget = F32.Max(
                    tableLayout.Size.Height
                    - tableLayout.Padding.Top
                    - tableLayout.Padding.Bottom
                    - tableLayout.Border.Top
                    - tableLayout.Border.Bottom
                    - (rowGap * Math.Max(nrows - 1, 0)),
                    0f);
                float current = 0f;
                foreach (float height in rowHeights)
                {
                    current += height;
                }

                float extra = trackTarget - current;
                if (extra > 0f)
                {
                    List<int> targets = [];
                    for (int index = 0; index < nrows; index++)
                    {
                        if (rowMinimums[index] is null)
                        {
                            targets.Add(index);
                        }
                    }

                    if (targets.Count == 0)
                    {
                        for (int index = 0; index < nrows; index++)
                        {
                            targets.Add(index);
                        }
                    }

                    float weight = 0f;
                    foreach (int index in targets)
                    {
                        weight += rowHeights[index];
                    }

                    foreach (int index in targets)
                    {
                        float share = weight > 0f
                            ? extra * rowHeights[index] / weight
                            : extra / targets.Count;
                        rowHeights[index] += share;
                    }
                }
            }

            TaffyStyle fixedStyle = taffyTree.GetStyle(tableNode).Clone();
            List<TaffyGridTemplateComponent> tracks = new(rowHeights.Count);
            foreach (float height in rowHeights)
            {
                tracks.Add(TaffyGridTemplateComponent.FromSingle(TaffyTrackSizingFunction.MinMax(
                    TaffyMinTrack.FromLength(height),
                    TaffyMaxTrack.FromLength(height))));
            }

            fixedStyle.GridTemplateRows = tracks;
            taffyTree.SetStyle(tableNode, fixedStyle);
            changed = true;

            // A CSS height on a cell is a row minimum, not a smaller final cell box.
            foreach ((TaffyNodeId cell, _, _, _) in cells)
            {
                TaffyStyle stretched = taffyTree.GetStyle(cell).Clone();
                Layout.Size<TaffyDimension> size = stretched.Size;
                size.Height = TaffyDimension.Auto;
                stretched.Size = size;
                taffyTree.SetStyle(cell, stretched);
            }
        }

        return changed;
    }

    /// <summary>Balance the atomic child boxes of each CSS multi-column container.</summary>
    internal static bool ApplyMulticolBalance(TaffyTree taffyTree, IReadOnlyList<MulticolBuild> multicol)
    {
        bool changed = false;
        foreach (MulticolBuild set in multicol)
        {
            int columnCount = set.Columns.Count;
            if (columnCount < 2 || set.Children.Count == 0)
            {
                continue;
            }

            List<float> weights = new(set.Children.Count);
            foreach (TaffyNodeId child in set.Children)
            {
                TaffyLayout layout = taffyTree.GetLayout(child);
                weights.Add(F32.Max(
                    layout.Size.Height + layout.Margin.Top + layout.Margin.Bottom,
                    0f));
            }

            int groups = Math.Min(columnCount, weights.Count);
            float maxWeight = 0f;
            float total = 0f;
            foreach (float weight in weights)
            {
                maxWeight = F32.Max(maxWeight, weight);
                total += weight;
            }

            float low = maxWeight;
            float high = F32.Max(total, low);
            if (high > 0f)
            {
                for (int iteration = 0; iteration < 24; iteration++)
                {
                    float candidate = (low + high) * 0.5f;
                    int used = 1;
                    float current = 0f;
                    foreach (float weight in weights)
                    {
                        if (current > 0f && current + weight > candidate)
                        {
                            used++;
                            current = weight;
                        }
                        else
                        {
                            current += weight;
                        }
                    }

                    if (used <= groups)
                    {
                        high = candidate;
                    }
                    else
                    {
                        low = candidate;
                    }
                }
            }

            // Pack from the end at the smallest feasible height.
            List<(int Start, int End)> ranges = new(groups);
            int rangeEnd = weights.Count;
            for (int remainingGroups = groups; remainingGroups >= 1; remainingGroups--)
            {
                int earliest = remainingGroups - 1;
                int start = rangeEnd;
                float used = 0f;
                while (start > earliest)
                {
                    float next = weights[start - 1];
                    if (start < rangeEnd && used + next > high + 0.001f)
                    {
                        break;
                    }

                    start--;
                    used += next;
                }

                ranges.Add((start, rangeEnd));
                rangeEnd = start;
            }

            ranges.Reverse();

            for (int index = 0; index < set.Columns.Count; index++)
            {
                TaffyNodeId[] children = index < ranges.Count
                    ? [.. set.Children.GetRange(ranges[index].Start, ranges[index].End - ranges[index].Start)]
                    : [];
                taffyTree.SetChildren(set.Columns[index], children);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Table-cell block alignment is a post-row-sizing operation.</summary>
    internal static bool ApplyTableCellBlockAlignment(
        DomTree tree,
        TaffyTree taffyTree,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        bool changed = false;
        foreach ((TaffyNodeId node, NodeId domId) in idMap)
        {
            if (!styles.TryGetValue(domId, out LayoutStyle? style))
            {
                continue;
            }

            if (!style.InternalFlexContainer
                || style.VerticalAlign is not (VerticalAlign.Middle or VerticalAlign.Bottom))
            {
                continue;
            }

            bool isCell = DomTraversal.IsAnyLocal(tree, domId, "td", "th");
            if (!isCell || taffyTree.ChildCount(node) == 0)
            {
                // Pure-text cells are aligned after shaping in the text finalize path.
                continue;
            }

            TaffyStyle current = taffyTree.GetStyle(node);
            TaffyLayout layout = taffyTree.GetLayout(node);
            if (current.Display != TaffyDisplay.Flex)
            {
                continue;
            }

            TaffyStyle aligned = current.Clone();
            float borderPadding = layout.Border.Top
                + layout.Border.Bottom
                + layout.Padding.Top
                + layout.Padding.Bottom;
            float declaredHeight = aligned.BoxSizing == TaffyBoxSizing.ContentBox
                ? F32.Max(layout.Size.Height - borderPadding, 0f)
                : layout.Size.Height;
            Layout.Size<TaffyDimension> size = aligned.Size;
            size.Height = TaffyDimension.FromLength(declaredHeight);
            aligned.Size = size;
            bool isMiddle = style.VerticalAlign == VerticalAlign.Middle;
            if (aligned.FlexDirection == TaffyFlexDirection.Column)
            {
                aligned.JustifyContent = isMiddle
                    ? TaffyJustifyContent.Center
                    : TaffyJustifyContent.FlexEnd;
            }
            else
            {
                aligned.AlignContent = isMiddle
                    ? TaffyAlignContent.Center
                    : TaffyAlignContent.FlexEnd;
            }

            taffyTree.SetStyle(node, aligned);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Repair the intrinsic main-size calculation for column flexboxes containing a negative
    /// main-axis margin.
    /// </summary>
    internal static bool RepairIntrinsicColumnFlexNegativeMargins(
        TaffyTree taffyTree,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        List<(TaffyNodeId Node, float Height)> repairs = [];

        foreach ((TaffyNodeId node, NodeId domId) in idMap)
        {
            if (!styles.TryGetValue(domId, out LayoutStyle? style))
            {
                continue;
            }

            if (style.Display != Display.Flex
                || style.InternalFlexContainer
                || !style.Height.IsAuto
                || DomStyleFixups.EffectiveContainerType(style) == ContainerType.Size
                || style.FlexDirection
                    is not (TaffyFlexDirection.Column or TaffyFlexDirection.ColumnReverse))
            {
                continue;
            }

            int itemCount = 0;
            float contentHeight = 0f;
            bool hasNegativeMainMargin = false;
            foreach (TaffyNodeId child in taffyTree.Children(node))
            {
                TaffyStyle childStyle = taffyTree.GetStyle(child);
                TaffyLayout childLayout = taffyTree.GetLayout(child);
                if (childStyle.Display == TaffyDisplay.None
                    || childStyle.Position == TaffyPosition.Absolute)
                {
                    continue;
                }

                float margin = childLayout.Margin.Top + childLayout.Margin.Bottom;
                hasNegativeMainMargin |=
                    childLayout.Margin.Top < 0f || childLayout.Margin.Bottom < 0f;
                contentHeight += childLayout.Size.Height + margin;
                itemCount++;
            }

            if (!hasNegativeMainMargin || itemCount == 0)
            {
                continue;
            }

            contentHeight += (style.RowGap ?? 0f) * Math.Max(itemCount - 1, 0);
            contentHeight = F32.Max(contentHeight, 0f);

            TaffyLayout layout = taffyTree.GetLayout(node);
            float outerHeight = contentHeight
                + layout.Padding.Top
                + layout.Padding.Bottom
                + layout.Border.Top
                + layout.Border.Bottom;
            if (MathF.Abs(layout.Size.Height - outerHeight) < 0.01f)
            {
                continue;
            }

            float declaredHeight = style.BoxSizing == BoxSizing.ContentBox
                ? contentHeight
                : outerHeight;
            repairs.Add((node, declaredHeight));
        }

        bool changed = false;
        foreach ((TaffyNodeId node, float height) in repairs)
        {
            TaffyStyle repaired = taffyTree.GetStyle(node).Clone();
            Layout.Size<TaffyDimension> size = repaired.Size;
            size.Height = TaffyDimension.FromLength(height);
            repaired.Size = size;
            taffyTree.SetStyle(node, repaired);
            changed = true;
        }

        return changed;
    }

    internal static void ResolveStaticPositionsAndReparent(
        TaffyTree taffyTree,
        IReadOnlyList<StaticPositionCandidate> candidates)
    {
        // Harvest every placeholder coordinate before mutating parent links.
        List<(StaticPositionCandidate Candidate, TaffyStyle Style)> resolved = new(candidates.Count);
        foreach (StaticPositionCandidate candidate in candidates)
        {
            if (TaffyGlobalOrigin(taffyTree, candidate.Child) is not { } childOrigin)
            {
                continue;
            }

            if (TaffyGlobalOrigin(taffyTree, candidate.Target) is not { } targetOrigin)
            {
                continue;
            }

            TaffyLayout childLayout = taffyTree.GetLayout(candidate.Child);
            Layout.Rect<float> childMargin = childLayout.Margin;
            TaffyLayout targetLayout = taffyTree.GetLayout(candidate.Target);
            Layout.Rect<float> targetBorder = targetLayout.Border;
            TaffyStyle style = taffyTree.GetStyle(candidate.Child).Clone();

            Layout.Rect<TaffyLengthPercentageAuto> inset = style.Inset;
            if (candidate.InlineAxis)
            {
                inset.Left = TaffyLengthPercentageAuto.FromLength(
                    childOrigin.X - targetOrigin.X - targetBorder.Left - childMargin.Left);
            }

            if (candidate.BlockAxis)
            {
                inset.Top = TaffyLengthPercentageAuto.FromLength(
                    childOrigin.Y - targetOrigin.Y - targetBorder.Top - childMargin.Top);
            }

            style.Inset = inset;
            resolved.Add((candidate, style));
        }

        foreach ((StaticPositionCandidate candidate, TaffyStyle style) in resolved)
        {
            if (taffyTree.Parent(candidate.Child) is not { } current)
            {
                continue;
            }

            taffyTree.RemoveChild(current, candidate.Child);
            taffyTree.AddChild(candidate.Target, candidate.Child);
            taffyTree.SetStyle(candidate.Child, style);
        }
    }

    /// <summary>Resolve the <c>width: fit-content</c> keyword after the containing inline space is known.</summary>
    internal static bool ApplyFitContentWidths(
        TaffyTree taffyTree,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth,
        Func<TaffyTree, TaffyNodeId, TaffyAvailableSpace, float?> intrinsicWidth)
    {
        List<(TaffyNodeId Node, float Available, float Margin, float InlineEdges, bool ContentBox)>
            candidates = [];

        // Snapshot every containing-space input before intrinsic subtree measurements overwrite
        // cached node layouts.
        foreach ((TaffyNodeId node, NodeId dom) in idMap)
        {
            if (!styles.TryGetValue(dom, out LayoutStyle? style))
            {
                continue;
            }

            if (!style.WidthFitContent || style.IgnoresUsedBoxSizes())
            {
                continue;
            }

            TaffyLayout layout = taffyTree.GetLayout(node);
            float margin = layout.Margin.Left + layout.Margin.Right;
            float inlineEdges = layout.Padding.Left
                + layout.Padding.Right
                + layout.Border.Left
                + layout.Border.Right;

            TaffyNodeId? parent = taffyTree.Parent(node);
            float parentContent = parent is { } parentId
                ? F32.Max(taffyTree.GetLayout(parentId).ContentBoxWidth(), 0f)
                : F32.Max(initialCbWidth, 0f);

            // `auto` stretches in these exact inline-axis situations, so its preliminary
            // margin-box width is the local available space.
            bool usesPreliminaryStretch = false;
            if (parent is { } stretchParent)
            {
                TaffyStyle parentStyle = taffyTree.GetStyle(stretchParent);
                switch (parentStyle.Display)
                {
                    case TaffyDisplay.Block:
                        usesPreliminaryStretch = style.Float is null
                            && style.Position != TaffyPosition.Absolute;
                        break;
                    case TaffyDisplay.Grid:
                    {
                        TaffyStyle childStyle = taffyTree.GetStyle(node);
                        TaffyAlignItems horizontal = childStyle.JustifySelf
                            ?? parentStyle.JustifyItems
                            ?? TaffyAlignItems.Normal;
                        TaffyAlignItems vertical = childStyle.AlignSelf
                            ?? parentStyle.AlignItems
                            ?? TaffyAlignItems.Normal;
                        TaffyAlignItems normal = childStyle.ItemIsReplaced
                            || (childStyle.AspectRatio is not null && vertical == TaffyAlignItems.Stretch)
                                ? TaffyAlignItems.Start
                                : TaffyAlignItems.Stretch;
                        usesPreliminaryStretch =
                            horizontal.ResolveNormal(normal) == TaffyAlignItems.Stretch;
                        break;
                    }

                    case TaffyDisplay.Flex
                        when parentStyle.FlexDirection
                            is TaffyFlexDirection.Column or TaffyFlexDirection.ColumnReverse:
                    {
                        TaffyAlignItems childAlign = (taffyTree.GetStyle(node).AlignSelf
                            ?? parentStyle.AlignItems
                            ?? TaffyAlignItems.Normal).ResolveNormal(TaffyAlignItems.Stretch);
                        usesPreliminaryStretch = childAlign == TaffyAlignItems.Stretch;
                        break;
                    }
                }
            }

            float available = usesPreliminaryStretch
                ? F32.Max(layout.Size.Width + margin, 0f)
                : parentContent;

            candidates.Add((
                node,
                available,
                margin,
                inlineEdges,
                style.BoxSizing == BoxSizing.ContentBox));
        }

        bool changed = false;
        foreach ((TaffyNodeId node, float available, float margin, float inlineEdges, bool contentBox)
            in candidates)
        {
            if (intrinsicWidth(taffyTree, node, TaffyAvailableSpace.MinContent) is not { } minContent)
            {
                continue;
            }

            if (intrinsicWidth(taffyTree, node, TaffyAvailableSpace.MaxContent) is not { } maxContent)
            {
                continue;
            }

            float fill = F32.Max(available - margin, 0f);
            float usedOuter = F32.Max(minContent, F32.Min(maxContent, fill));
            float declaration = contentBox
                ? F32.Max(usedOuter - inlineEdges, 0f)
                : F32.Max(usedOuter, 0f);
            TaffyStyle resolved = taffyTree.GetStyle(node).Clone();
            Layout.Size<TaffyDimension> size = resolved.Size;
            size.Width = TaffyDimension.FromLength(declaration);
            resolved.Size = size;
            taffyTree.SetStyle(node, resolved);
            changed = true;
        }

        return changed;
    }
}
