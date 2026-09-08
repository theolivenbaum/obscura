// Port of the table sizing, containing-block width, and float-heuristic helpers in
// crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using TaffyPosition = Obscura.Render.Layout.Position;

namespace Obscura.Render;

internal record struct FixedTableColumn(float Length, float Percentage, bool Specified);

internal static class DomTableSupport
{
    /// <summary>Number of ancestor tables containing <paramref name="id"/>.</summary>
    internal static int TableAncestorDepth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        int depth = 0;
        NodeId current = id;
        while (DomTraversal.RenderedParent(tree, current) is { } parent)
        {
            if (styles.TryGetValue(parent, out LayoutStyle? style) && style.IsTableBox)
            {
                depth++;
            }

            current = parent;
            if (depth > 4096)
            {
                break;
            }
        }

        return depth;
    }

    /// <summary>
    /// Resolve a content-box width without running layout when the complete containing-block
    /// chain is ordinary block flow.
    /// </summary>
    internal static float? ReliableNormalFlowContentWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth,
        int depth)
    {
        if (depth > 4096)
        {
            return null;
        }

        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        if (style.Float is not null
            || style.Position == TaffyPosition.Absolute
            || style.IsInlineBlock
            || style.WidthFitContent
            || style.SizeExpressions[0] is not null)
        {
            return null;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (parent is { } parentId
            && styles.TryGetValue(parentId, out LayoutStyle? transparent)
            && transparent.DisplayContents)
        {
            parent = DomTraversal.RenderedParent(tree, parentId);
        }

        float containingWidth;
        if (parent is { } resolvedParent)
        {
            if (styles.TryGetValue(resolvedParent, out LayoutStyle? parentStyle))
            {
                if (parentStyle.Display is Display.Flex or Display.Grid
                    || parentStyle.InternalFlexContainer
                    || parentStyle.ColumnCount is not null)
                {
                    return null;
                }

                if (ReliableNormalFlowContentWidth(
                        tree, resolvedParent, styles, initialCbWidth, depth + 1) is not { } width)
                {
                    return null;
                }

                containingWidth = width;
            }
            else
            {
                containingWidth = initialCbWidth;
            }
        }
        else
        {
            containingWidth = initialCbWidth;
        }

        float horizontalEdges =
            style.Padding.Left + style.Padding.Right + style.Border.Left + style.Border.Right;

        float? DeclaredContent(Dimension dimension) => dimension.Kind switch
        {
            DimensionKind.Px => style.BoxSizing == BoxSizing.ContentBox
                ? dimension.Value
                : F32.Max(dimension.Value - horizontalEdges, 0f),
            DimensionKind.Percent => style.BoxSizing == BoxSizing.ContentBox
                ? containingWidth * dimension.Value
                : F32.Max((containingWidth * dimension.Value) - horizontalEdges, 0f),
            _ => null,
        };

        float contentWidth = DeclaredContent(style.Width)
            ?? F32.Max(containingWidth - style.Margin.Left - style.Margin.Right - horizontalEdges, 0f);
        if (DeclaredContent(style.MinWidth) is { } minimum)
        {
            contentWidth = F32.Max(contentWidth, minimum);
        }

        if (DeclaredContent(style.MaxWidth) is { } maximum)
        {
            contentWidth = F32.Min(contentWidth, maximum);
        }

        return F32.Max(contentWidth, 0f);
    }

    /// <summary>
    /// Resolve an authored definite width even when the box's placement is owned by float or
    /// positioned layout.
    /// </summary>
    internal static float? ReliableDeclaredContentWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth,
        int depth)
    {
        if (depth > 4096)
        {
            return null;
        }

        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        if (style.WidthFitContent
            || style.SizeExpressions[0] is not null
            || style.SizeExpressions[2] is not null
            || style.SizeExpressions[4] is not null)
        {
            return null;
        }

        bool needsContainingWidth = style.Width.Kind == DimensionKind.Percent
            || style.MinWidth.Kind == DimensionKind.Percent
            || style.MaxWidth.Kind == DimensionKind.Percent;
        float? containingWidth = null;
        if (needsContainingWidth)
        {
            NodeId? parent = DomTraversal.RenderedParent(tree, id);
            while (parent is { } parentId
                && styles.TryGetValue(parentId, out LayoutStyle? transparent)
                && transparent.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
            }

            float? resolved = parent is { } resolvedParent
                ? ReliableNormalFlowContentWidth(tree, resolvedParent, styles, initialCbWidth, depth + 1)
                    ?? ReliableDeclaredContentWidth(tree, resolvedParent, styles, initialCbWidth, depth + 1)
                : initialCbWidth;
            if (resolved is null)
            {
                return null;
            }

            containingWidth = resolved;
        }

        float horizontalEdges =
            style.Padding.Left + style.Padding.Right + style.Border.Left + style.Border.Right;

        float? DeclaredContent(Dimension dimension)
        {
            switch (dimension.Kind)
            {
                case DimensionKind.Px:
                    return style.BoxSizing == BoxSizing.ContentBox
                        ? dimension.Value
                        : F32.Max(dimension.Value - horizontalEdges, 0f);
                case DimensionKind.Percent:
                    if (containingWidth is not { } basis)
                    {
                        return null;
                    }

                    float value = basis * dimension.Value;
                    return style.BoxSizing == BoxSizing.ContentBox
                        ? value
                        : F32.Max(value - horizontalEdges, 0f);
                default:
                    return null;
            }
        }

        if (DeclaredContent(style.Width) is not { } contentWidth)
        {
            return null;
        }

        if (DeclaredContent(style.MinWidth) is { } minimum)
        {
            contentWidth = F32.Max(contentWidth, minimum);
        }

        if (DeclaredContent(style.MaxWidth) is { } maximum)
        {
            contentWidth = F32.Min(contentWidth, maximum);
        }

        return F32.Max(contentWidth, 0f);
    }

    /// <summary>
    /// Resolve the containing-block content width used by CSS's ratio-only auto/auto replaced
    /// sizing branch.
    /// </summary>
    internal static float? ReliableRatioOnlyAvailableWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth)
    {
        if (!styles.TryGetValue(id, out LayoutStyle? image))
        {
            return null;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (parent is { } parentId)
        {
            if (!styles.TryGetValue(parentId, out LayoutStyle? parentStyle))
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            if (parentStyle.DisplayContents
                || (parentStyle.Display == Display.Inline && !parentStyle.IsInlineBlock))
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            break;
        }

        float containingWidth;
        if (parent is { } resolvedParent)
        {
            float? resolved =
                ReliableNormalFlowContentWidth(tree, resolvedParent, styles, initialCbWidth, 0)
                ?? ReliableDeclaredContentWidth(tree, resolvedParent, styles, initialCbWidth, 0);
            if (resolved is not { } width)
            {
                return null;
            }

            containingWidth = width;
        }
        else
        {
            containingWidth = initialCbWidth;
        }

        float horizontalEdges =
            image.Padding.Left + image.Padding.Right + image.Border.Left + image.Border.Right;
        return F32.Max(
            containingWidth - image.Margin.Left - image.Margin.Right - horizontalEdges,
            0f);
    }

    internal static float? ReliableTableAvailableWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth)
    {
        if (!styles.TryGetValue(id, out LayoutStyle? tableStyle))
        {
            return null;
        }

        if (tableStyle.Float is not null
            || tableStyle.Position == TaffyPosition.Absolute
            || tableStyle.IsInlineBlock)
        {
            return null;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (parent is { } parentId
            && styles.TryGetValue(parentId, out LayoutStyle? transparent)
            && transparent.DisplayContents)
        {
            parent = DomTraversal.RenderedParent(tree, parentId);
        }

        float containingWidth;
        if (parent is { } resolvedParent && styles.TryGetValue(resolvedParent, out LayoutStyle? parentStyle))
        {
            if (parentStyle.Display is Display.Flex or Display.Grid
                || parentStyle.InternalFlexContainer
                || parentStyle.ColumnCount is not null)
            {
                return null;
            }

            if (ReliableNormalFlowContentWidth(tree, resolvedParent, styles, initialCbWidth, 0)
                is not { } width)
            {
                return null;
            }

            containingWidth = width;
        }
        else
        {
            containingWidth = initialCbWidth;
        }

        return F32.Max(containingWidth - tableStyle.Margin.Left - tableStyle.Margin.Right, 0f);
    }

    /// <summary>
    /// Collect rows together with the exclusive end of their originating row group.
    /// </summary>
    internal static void CollectTableRows(DomTree tree, NodeId id, List<(NodeId Row, int GroupEnd)> rows)
    {
        int? directStart = null;
        foreach (NodeId cid in tree.Children(id))
        {
            string? local = DomTraversal.ElementLocalName(tree, cid);
            switch (local)
            {
                case "tr":
                    directStart ??= rows.Count;
                    rows.Add((cid, 0));
                    break;
                case "thead":
                case "tbody":
                case "tfoot":
                {
                    if (directStart is { } start)
                    {
                        int groupEnd = rows.Count;
                        for (int index = start; index < groupEnd; index++)
                        {
                            rows[index] = (rows[index].Row, groupEnd);
                        }

                        directStart = null;
                    }

                    int sectionStart = rows.Count;
                    foreach (NodeId row in tree.Children(cid))
                    {
                        if (DomTraversal.IsLocal(tree, row, "tr"))
                        {
                            rows.Add((row, 0));
                        }
                    }

                    int sectionEnd = rows.Count;
                    for (int index = sectionStart; index < sectionEnd; index++)
                    {
                        rows[index] = (rows[index].Row, sectionEnd);
                    }

                    break;
                }
            }
        }

        if (directStart is { } trailing)
        {
            int end = rows.Count;
            for (int index = trailing; index < end; index++)
            {
                rows[index] = (rows[index].Row, end);
            }
        }
    }

    /// <summary>Minimum track width implied by percentage columns in an auto-width table.</summary>
    internal static float AutoTablePercentageIntrinsicFloor(
        IReadOnlyList<float> minimums,
        IReadOnlyList<float?> percentages)
    {
        if (minimums.Count != percentages.Count || minimums.Count == 0)
        {
            return 0f;
        }

        float remaining = 1f;
        float autoMinimum = 0f;
        float required = 0f;
        for (int index = 0; index < minimums.Count; index++)
        {
            float minimum = F32.Max(minimums[index], 0f);
            if (percentages[index] is not { } percentage)
            {
                autoMinimum += minimum;
                continue;
            }

            float effective = F32.Min(F32.Max(percentage, 0f), remaining);
            remaining = F32.Max(remaining - effective, 0f);
            if (minimum > 0f)
            {
                if (effective <= float.Epsilon)
                {
                    return float.PositiveInfinity;
                }

                required = F32.Max(required, minimum / effective);
            }
        }

        if (autoMinimum > 0f)
        {
            if (remaining <= float.Epsilon)
            {
                return float.PositiveInfinity;
            }

            required = F32.Max(required, autoMinimum / remaining);
        }

        return required;
    }

    /// <summary>Resolve auto-table column constraints into final track widths.</summary>
    internal static List<float> DistributeAutoTableColumns(
        float target,
        IReadOnlyList<float> minimums,
        IReadOnlyList<float> preferreds,
        IReadOnlyList<float?> fixedWidths,
        IReadOnlyList<float?> percentages)
    {
        int count = minimums.Count;
        if (count == 0
            || preferreds.Count != count
            || fixedWidths.Count != count
            || percentages.Count != count)
        {
            return [.. minimums];
        }

        target = F32.Max(target, 0f);
        List<float> mins = new(count);
        foreach (float value in minimums)
        {
            mins.Add(F32.Max(value, 0f));
        }

        List<float> prefs = new(count);
        for (int index = 0; index < count; index++)
        {
            prefs.Add(F32.Max(preferreds[index], mins[index]));
        }

        // Percentage column constraints are cumulatively clamped to 100% in source order.
        float remaining = 1f;
        List<float?> effectivePercentages = new(count);
        foreach (float? percentage in percentages)
        {
            if (percentage is { } value)
            {
                float used = F32.Min(F32.Max(value, 0f), remaining);
                remaining = F32.Max(remaining - used, 0f);
                effectivePercentages.Add(used);
            }
            else
            {
                effectivePercentages.Add(null);
            }
        }

        List<float> guessMin = [.. mins];
        List<float> guessMinPct = new(count);
        for (int index = 0; index < count; index++)
        {
            guessMinPct.Add(effectivePercentages[index] is { } percentage
                ? F32.Max(percentage * target, mins[index])
                : mins[index]);
        }

        List<float> guessMinSpec = new(count);
        for (int index = 0; index < count; index++)
        {
            guessMinSpec.Add(effectivePercentages[index] is not null
                ? guessMinPct[index]
                : fixedWidths[index] is not null ? prefs[index] : mins[index]);
        }

        List<float> guessPref = new(count);
        for (int index = 0; index < count; index++)
        {
            guessPref.Add(effectivePercentages[index] is not null ? guessMinPct[index] : prefs[index]);
        }

        static float Total(IReadOnlyList<float> values)
        {
            float sum = 0f;
            foreach (float value in values)
            {
                sum += value;
            }

            return sum;
        }

        float minTotal = Total(guessMin);
        float minPctTotal = Total(guessMinPct);
        float minSpecTotal = Total(guessMinSpec);
        float prefTotal = Total(guessPref);

        static List<float> Interpolate(IReadOnlyList<float> lower, IReadOnlyList<float> upper, float wanted)
        {
            float lowerTotal = 0f;
            float upperTotal = 0f;
            foreach (float value in lower)
            {
                lowerTotal += value;
            }

            foreach (float value in upper)
            {
                upperTotal += value;
            }

            float deltaTotal = F32.Max(upperTotal - lowerTotal, 0f);
            if (deltaTotal <= float.Epsilon)
            {
                return [.. lower];
            }

            float scale = Math.Clamp((wanted - lowerTotal) / deltaTotal, 0f, 1f);
            List<float> result = new(lower.Count);
            for (int index = 0; index < lower.Count; index++)
            {
                result.Add(lower[index] + (F32.Max(upper[index] - lower[index], 0f) * scale));
            }

            return result;
        }

        if (target <= minTotal)
        {
            return guessMin;
        }

        if (target < minPctTotal)
        {
            return Interpolate(guessMin, guessMinPct, target);
        }

        if (target < minSpecTotal)
        {
            return Interpolate(guessMinPct, guessMinSpec, target);
        }

        if (target < prefTotal)
        {
            return Interpolate(guessMinSpec, guessPref, target);
        }

        List<float> result2 = guessPref;
        float extra = target - prefTotal;
        if (extra <= float.Epsilon)
        {
            return result2;
        }

        List<(int Index, float Weight)> candidates = [];
        for (int index = 0; index < count; index++)
        {
            if (effectivePercentages[index] is null && fixedWidths[index] is null && prefs[index] > 0f)
            {
                candidates.Add((index, prefs[index]));
            }
        }

        if (candidates.Count == 0)
        {
            for (int index = 0; index < count; index++)
            {
                if (effectivePercentages[index] is null && fixedWidths[index] is null)
                {
                    candidates.Add((index, 1f));
                }
            }
        }

        if (candidates.Count == 0)
        {
            for (int index = 0; index < count; index++)
            {
                if (effectivePercentages[index] is null && fixedWidths[index] is not null)
                {
                    candidates.Add((index, F32.Max(prefs[index], 0f)));
                }
            }
        }

        if (candidates.Count == 0)
        {
            for (int index = 0; index < count; index++)
            {
                if (effectivePercentages[index] is { } percentage && percentage > 0f)
                {
                    candidates.Add((index, percentage));
                }
            }
        }

        if (candidates.Count == 0)
        {
            for (int index = 0; index < count; index++)
            {
                candidates.Add((index, 1f));
            }
        }

        float weight = 0f;
        foreach ((_, float value) in candidates)
        {
            weight += value;
        }

        float candidateCount = candidates.Count;
        foreach ((int index, float value) in candidates)
        {
            result2[index] += weight > 0f ? extra * value / weight : extra / candidateCount;
        }

        return result2;
    }

    /// <summary>Resolve CSS 2 fixed-layout column constraints into exact track widths.</summary>
    internal static List<float> DistributeFixedTableColumns(
        float target,
        IReadOnlyList<FixedTableColumn> columns)
    {
        if (columns.Count == 0)
        {
            return [];
        }

        target = F32.Max(target, 0f);
        List<float> widths = new(columns.Count);
        foreach (FixedTableColumn column in columns)
        {
            widths.Add(column.Specified
                ? F32.Max(column.Length, 0f) + (F32.Max(column.Percentage, 0f) * target)
                : 0f);
        }

        float total = 0f;
        foreach (float width in widths)
        {
            total += width;
        }

        // Percentage columns are the flexible part of an over-constrained fixed table.
        if (total > target)
        {
            float percentageTotal = 0f;
            foreach (FixedTableColumn column in columns)
            {
                percentageTotal += F32.Max(column.Percentage, 0f) * target;
            }

            float shrink = F32.Min(total - target, percentageTotal);
            if (shrink > 0f && percentageTotal > 0f)
            {
                for (int index = 0; index < widths.Count; index++)
                {
                    float contribution = F32.Max(columns[index].Percentage, 0f) * target;
                    widths[index] = F32.Max(
                        widths[index] - (shrink * contribution / percentageTotal),
                        0f);
                }

                total -= shrink;
            }
        }

        float remaining = F32.Max(target - total, 0f);
        if (remaining <= float.Epsilon)
        {
            return widths;
        }

        List<int> unresolved = [];
        for (int index = 0; index < columns.Count; index++)
        {
            if (!columns[index].Specified)
            {
                unresolved.Add(index);
            }
        }

        if (unresolved.Count != 0)
        {
            float share = remaining / unresolved.Count;
            foreach (int index in unresolved)
            {
                widths[index] = share;
            }

            return widths;
        }

        List<float> weights = new(columns.Count);
        foreach (FixedTableColumn column in columns)
        {
            weights.Add(F32.Max(column.Length, 0f));
        }

        float weight = 0f;
        foreach (float value in weights)
        {
            weight += value;
        }

        if (weight <= float.Epsilon)
        {
            weights.Clear();
            foreach (FixedTableColumn column in columns)
            {
                weights.Add(F32.Max(column.Percentage, 0f));
            }

            weight = 0f;
            foreach (float value in weights)
            {
                weight += value;
            }
        }

        if (weight <= float.Epsilon)
        {
            for (int index = 0; index < weights.Count; index++)
            {
                weights[index] = 1f;
            }

            weight = columns.Count;
        }

        for (int index = 0; index < widths.Count; index++)
        {
            widths[index] += remaining * weights[index] / weight;
        }

        return widths;
    }

    /// <summary>
    /// Give each row/section its CSS table-grid band. In the grid table model these wrappers
    /// are not taffy boxes, so without this their backgrounds, borders, and DOM geometry would
    /// disappear.
    /// </summary>
    internal static void SynthesizeRowRects(DomTree tree, Dictionary<NodeId, Rect> rects)
    {
        List<NodeId> rows = [];
        List<NodeId> sections = [];
        Dictionary<NodeId, Rect> tableInline = [];
        foreach (NodeId id in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            string? local = DomTraversal.ElementLocalName(tree, id);
            switch (local)
            {
                case "tr":
                    rows.Add(id);
                    break;
                case "tbody":
                case "thead":
                case "tfoot":
                    sections.Add(id);
                    break;
                case "td":
                case "th":
                {
                    NodeId? ancestor = DomTraversal.RenderedParent(tree, id);
                    while (ancestor is { } parent)
                    {
                        if (DomTraversal.IsLocal(tree, parent, "table"))
                        {
                            if (rects.TryGetValue(id, out Rect cellRect))
                            {
                                tableInline[parent] = tableInline.TryGetValue(parent, out Rect current)
                                    ? current.Union(cellRect)
                                    : cellRect;
                            }

                            break;
                        }

                        ancestor = DomTraversal.RenderedParent(tree, parent);
                    }

                    break;
                }
            }
        }

        foreach (NodeId id in rows)
        {
            if (rects.ContainsKey(id))
            {
                continue;
            }

            Rect? inline = null;
            Rect? block = null;
            foreach (NodeId cell in tree.Children(id))
            {
                if (!DomTraversal.IsAnyLocal(tree, cell, "td", "th"))
                {
                    continue;
                }

                if (!rects.TryGetValue(cell, out Rect cellRect))
                {
                    continue;
                }

                inline = inline is { } currentInline ? currentInline.Union(cellRect) : cellRect;
                int rowspan = 1;
                if (tree.GetNode(cell)?.GetAttribute("rowspan") is { } span
                    && int.TryParse(
                        span.Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int parsed))
                {
                    rowspan = parsed;
                }

                if (rowspan == 1)
                {
                    block = block is { } currentBlock ? currentBlock.Union(cellRect) : cellRect;
                }
            }

            if (inline is not { } inlineBand)
            {
                continue;
            }

            NodeId? tableAncestor = DomTraversal.RenderedParent(tree, id);
            while (tableAncestor is { } parent)
            {
                if (tableInline.TryGetValue(parent, out Rect tableBand))
                {
                    inlineBand = inlineBand with { X = tableBand.X, Width = tableBand.Width };
                    break;
                }

                tableAncestor = DomTraversal.RenderedParent(tree, parent);
            }

            Rect blockBand = block ?? inlineBand;
            rects[id] = new Rect(inlineBand.X, blockBand.Y, inlineBand.Width, blockBand.Height);
        }

        foreach (NodeId id in sections)
        {
            if (rects.ContainsKey(id))
            {
                continue;
            }

            Rect? section = null;
            foreach (NodeId row in tree.Children(id))
            {
                if (!DomTraversal.IsLocal(tree, row, "tr"))
                {
                    continue;
                }

                if (rects.TryGetValue(row, out Rect rowRect))
                {
                    section = section is { } current ? current.Union(rowRect) : rowRect;
                }
            }

            if (section is { } sectionRect)
            {
                rects[id] = sectionRect;
            }
        }
    }

    /// <summary>Largest definite (px) width among <paramref name="id"/> and its descendants.</summary>
    internal static float? MaxDefiniteDescendantWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        float? best = null;
        foreach (NodeId d in tree.Descendants(id))
        {
            if (styles.TryGetValue(d, out LayoutStyle? style)
                && style.Width.Kind == DimensionKind.Px
                && style.Width.Value > 0f)
            {
                best = best is { } current ? F32.Max(current, style.Width.Value) : style.Width.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// Fixed content descendants can floor a table's intrinsic minimum. Width hints on
    /// cells/rows/columns are different and must remain shrinkable to the content minimum.
    /// </summary>
    internal static float? MaxDefiniteTableContentWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        float? best = null;
        foreach (NodeId descendant in tree.Descendants(id))
        {
            bool structural = (styles.TryGetValue(descendant, out LayoutStyle? style)
                    && style.IsTableCellBox)
                || DomTraversal.IsAnyLocal(
                    tree,
                    descendant,
                    "caption", "col", "colgroup", "thead", "tbody", "tfoot", "tr", "td", "th");
            if (structural)
            {
                continue;
            }

            if (style is not null && style.Width.Kind == DimensionKind.Px && style.Width.Value > 0f)
            {
                best = best is { } current ? F32.Max(current, style.Width.Value) : style.Width.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// Rough height budget for a float with no explicit size and no images.
    /// </summary>
    internal const float DefaultFloatHeightEstimate = 200f;

    /// <summary>Estimate a float's rendered height in CSS px.</summary>
    internal static float EstimateFloatHeight(
        DomTree tree,
        NodeId floatId,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (styles.TryGetValue(floatId, out LayoutStyle? floatStyle)
            && floatStyle.Height.Kind == DimensionKind.Px)
        {
            return floatStyle.Height.Value;
        }

        float imageHeight = 0f;
        foreach (NodeId id in tree.Descendants(floatId))
        {
            if (!DomTraversal.IsLocal(tree, id, "img"))
            {
                continue;
            }

            if (styles.TryGetValue(id, out LayoutStyle? imageStyle)
                && imageStyle.Height.Kind == DimensionKind.Px)
            {
                imageHeight += imageStyle.Height.Value;
            }
        }

        const float assumedFloatWidth = 280f;
        float textHeight = EstimateTextHeight(tree, floatId, styles, assumedFloatWidth);

        // Flattened character count misses forced rows; add one line per structural row.
        float structuralHeight = 0f;
        foreach (NodeId id in tree.Descendants(floatId))
        {
            if (!DomTraversal.IsAnyLocal(
                    tree, id, "li", "tr", "dt", "dd", "p", "figcaption", "h1", "h2", "h3", "h4", "h5", "h6"))
            {
                continue;
            }

            float fontSize = styles.TryGetValue(id, out LayoutStyle? rowStyle) && rowStyle.FontSize is { } size
                ? size
                : 16f;
            structuralHeight += fontSize * 1.2f;
        }

        return F32.Max(imageHeight + textHeight + structuralHeight, DefaultFloatHeightEstimate);
    }

    /// <summary>
    /// Estimate how tall a subtree's text would render at <paramref name="assumedWidth"/>,
    /// using the same average-character-width heuristic as the layout-only text fallback.
    /// </summary>
    internal static float EstimateTextHeight(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float assumedWidth)
    {
        float charCount = 0f;
        foreach (char c in tree.TextContent(id))
        {
            if (!char.IsWhiteSpace(c))
            {
                charCount++;
            }
        }

        if (charCount == 0f)
        {
            return 0f;
        }

        float fontSize = styles.TryGetValue(id, out LayoutStyle? style) && style.FontSize is { } size
            ? size
            : 16f;
        const float avgCharWidthEm = 0.55f;
        float charsPerLine = F32.Max(assumedWidth / (fontSize * avgCharWidthEm), 1f);
        float lines = F32.Max(MathF.Ceiling(charCount / charsPerLine), 1f);
        return (lines * fontSize * 1.2f) + 16f;
    }

    /// <summary>Estimate the normal-flow height consumed by one sibling alongside a float.</summary>
    internal static float EstimateFlowSiblingHeight(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float assumedWidth)
    {
        styles.TryGetValue(id, out LayoutStyle? style);
        if (style is not null
            && (style.Display == Display.None || style.Position == TaffyPosition.Absolute))
        {
            return 0f;
        }

        float explicitHeight = style is not null && style.Height.Kind == DimensionKind.Px
            ? F32.Max(style.Height.Value, 0f)
            : 0f;
        float descendantImageHeight = 0f;
        foreach (NodeId descendant in tree.Descendants(id))
        {
            if (!DomTraversal.IsLocal(tree, descendant, "img"))
            {
                continue;
            }

            if (styles.TryGetValue(descendant, out LayoutStyle? imageStyle)
                && imageStyle.Height.Kind == DimensionKind.Px)
            {
                descendantImageHeight += F32.Max(imageStyle.Height.Value, 0f);
            }
        }

        float ownImageHeight = DomTraversal.IsLocal(tree, id, "img") ? explicitHeight : 0f;
        float contentHeight = F32.Max(
            F32.Max(EstimateTextHeight(tree, id, styles, assumedWidth), explicitHeight),
            descendantImageHeight + ownImageHeight);
        float margins = style is not null
            ? F32.Max(style.Margin.Top + style.Margin.Bottom, 0f)
            : 0f;
        return contentHeight + margins;
    }

    internal static bool IsStructuralNativeClearBox(DomTree tree, NodeId id, LayoutStyle style) =>
        style.Display == Display.Block
        && !style.DisplayContents
        && !style.IsTableBox
        && !style.IsInlineBlock
        && !style.FlowRoot
        && !style.OverflowHidden
        && DomStyleFixups.EffectiveContainerType(style) == ContainerType.Normal
        && style.Float is null
        && style.Clear is not null
        && style.Position != TaffyPosition.Absolute
        && tree.TextContent(id).Trim().Length == 0
        && style.BeforePseudo is null
        && style.AfterPseudo is null;

    internal static bool IsStructuralNativeFloatPseudo(LayoutStyle style) =>
        style.Display != Display.None
        && style.Display != Display.Inline
        && !style.DisplayContents
        && !style.IsInlineBlock
        && (!style.FlowRoot || style.IsTableBox)
        && !style.OverflowHidden
        && DomStyleFixups.EffectiveContainerType(style) == ContainerType.Normal
        && style.Float is null
        && style.Position != TaffyPosition.Absolute
        && (style.BeforeContent is null || style.BeforeContent.Trim().Length == 0);

    /// <summary>
    /// Native taffy floats are currently sound for a deliberately small structural subset.
    /// </summary>
    internal static bool CanUseNativeFloatBand(
        DomTree tree,
        LayoutStyle parentStyle,
        IReadOnlyList<NodeId> domChildren,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (parentStyle.Display != Display.Block
            || parentStyle.InternalFlexContainer
            || parentStyle.IsInlineBlock
            || parentStyle.IsTableBox
            || parentStyle.Position == TaffyPosition.Absolute
            || parentStyle.Width.Kind is not (DimensionKind.Auto or DimensionKind.Px or DimensionKind.Percent)
            || parentStyle.SizeExpressions[0] is not null)
        {
            return false;
        }

        bool hasLeft = false;
        bool hasRight = false;
        bool sawFloat = false;
        bool sawClearAfterFloats = false;
        bool sawFlowAfterFloats = false;

        if (parentStyle.BeforePseudo is { } before && !IsStructuralNativeFloatPseudo(before))
        {
            return false;
        }

        foreach (NodeId id in domChildren)
        {
            if (tree.GetNode(id) is not { } node)
            {
                continue;
            }

            if (!node.IsElement)
            {
                // Comments, doctypes, and processing instructions generate no formatting box.
                if (node.TextContentOfTextNode is { } contents && contents.Trim().Length != 0)
                {
                    return false;
                }

                continue;
            }

            if (!styles.TryGetValue(id, out LayoutStyle? style))
            {
                return false;
            }

            if (style.Display == Display.None)
            {
                continue;
            }

            if (style.Float is { } side)
            {
                if (sawClearAfterFloats
                    || sawFlowAfterFloats
                    || style.DisplayContents
                    || style.IsTableBox
                    || style.Position == TaffyPosition.Absolute
                    || style.Width.Kind is not (DimensionKind.Auto or DimensionKind.Px or DimensionKind.Percent)
                    || style.SizeExpressions[0] is not null
                    || DomStyleFixups.HasDeferredOrAutoMargin(style))
                {
                    return false;
                }

                sawFloat = true;
                hasLeft |= side == Float.Left;
                hasRight |= side == Float.Right;
            }
            else if (IsStructuralNativeClearBox(tree, id, style))
            {
                if (!sawFloat)
                {
                    return false;
                }

                sawClearAfterFloats |= DomStyleFixups.ClearMatchesFloatSides(
                    style.Clear!.Value, hasLeft, hasRight);
            }
            else if (sawFloat
                && style.Display == Display.Block
                && !style.DisplayContents
                && !style.IsInlineBlock
                && !style.IsTableBox
                && !style.InternalFlexContainer
                && style.Position != TaffyPosition.Absolute)
            {
                // Gecko keeps an ordinary block's border box at the BFC's full inline size and
                // narrows only its descendant line boxes.
                sawFlowAfterFloats = true;
            }
            else
            {
                return false;
            }
        }

        if (parentStyle.AfterPseudo is { } after)
        {
            if (!IsStructuralNativeFloatPseudo(after))
            {
                return false;
            }

            if (after.Clear is { } clear)
            {
                sawClearAfterFloats |= DomStyleFixups.ClearMatchesFloatSides(clear, hasLeft, hasRight);
            }
        }

        // Taffy represents scroll-container overflow as a real BFC root. Plain `clip` does not
        // establish a BFC, and viewport-propagated overflow leaves its source box visible.
        bool parentIsNativeBfc = parentStyle.OverflowScrollContainer
            && !parentStyle.OverflowPropagatedToViewport;
        return sawFloat && (sawFlowAfterFloats || sawClearAfterFloats || parentIsNativeBfc);
    }
}
