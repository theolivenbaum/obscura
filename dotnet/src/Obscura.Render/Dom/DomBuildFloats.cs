// Port of `build_children_with_float_zone` in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using TaffyAlignItems = Obscura.Render.Layout.AlignItems;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDisplay = Obscura.Render.Layout.Display;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyFlexWrap = Obscura.Render.Layout.FlexWrap;
using TaffyJustifyContent = Obscura.Render.Layout.AlignContent;
using TaffyLengthPercentageAuto = Obscura.Render.Layout.LengthPercentageAuto;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyStyle = Obscura.Render.Layout.Style;

namespace Obscura.Render;

internal static partial class DomBuild
{
    /// <summary>
    /// Approximate <c>float: left|right</c> without real per-line reflow: place the float
    /// alongside the flow siblings that follow it until their estimated height reaches the
    /// float's estimated bottom.
    /// </summary>
    internal static List<TaffyNodeId> BuildChildrenWithFloatZone(
        BuildContext context,
        NodeId parentId,
        IReadOnlyList<NodeId> domChildren)
    {
        DomTree tree = context.Tree;
        IReadOnlyDictionary<NodeId, LayoutStyle> styles = context.Styles;

        bool IsFloat(NodeId cid) =>
            styles.TryGetValue(cid, out LayoutStyle? style)
            && style.Display != Display.None
            && style.Float is not null;

        // A definite-height block whose only substantive contents are either one full-width
        // float or one percentage float followed by one percentage inline atom has a bounded,
        // single float band.
        bool parentHasDefiniteHeight = styles.TryGetValue(parentId, out LayoutStyle? parent)
            && parent.Height.Kind is DimensionKind.Px or DimensionKind.Percent
            && parent.SizeExpressions[1] is null;
        bool parentHasClearfix = parent is not null
            && parent.AfterPseudo is { } afterPseudo
            && afterPseudo.Clear is Clear.Left or Clear.Both
            && HasInFlowGeneratedPseudo(afterPseudo);
        float? parentMinHeight = parent is not null && parent.MinHeight.Kind == DimensionKind.Px
            ? parent.MinHeight.Value
            : null;

        if (parentHasDefiniteHeight || parentHasClearfix || parentMinHeight is not null)
        {
            static bool FillsAxis(float value) => MathF.Abs(value - 1f) < 0.001f;
            List<NodeId> substantive = [];
            foreach (NodeId cid in domChildren)
            {
                if (tree.GetNode(cid) is not { } node)
                {
                    continue;
                }

                if (node.IsElement)
                {
                    if (styles.TryGetValue(cid, out LayoutStyle? style) && style.Display != Display.None)
                    {
                        substantive.Add(cid);
                    }
                }
                else if (tree.TextContent(cid).Trim().Length != 0)
                {
                    substantive.Add(cid);
                }
            }

            List<NodeId> floated = [];
            foreach (NodeId cid in substantive)
            {
                if (IsFloat(cid))
                {
                    floated.Add(cid);
                }
            }

            if (floated.Count == 1)
            {
                NodeId floatDom = floated[0];
                styles.TryGetValue(floatDom, out LayoutStyle? floatStyle);
                float? floatPercentWidth = floatStyle is not null
                    && floatStyle.Width.Kind == DimensionKind.Percent
                        ? floatStyle.Width.Value
                        : null;
                bool floatFillsHeight = floatStyle is not null
                    && floatStyle.Height.Kind == DimensionKind.Percent
                    && FillsAxis(floatStyle.Height.Value)
                    && floatStyle.SizeExpressions[1] is null;
                float? floatPixelHeight = floatStyle is not null
                    && floatStyle.Height.Kind == DimensionKind.Px
                    && floatStyle.SizeExpressions[1] is null
                        ? floatStyle.Height.Value
                        : null;
                bool floatHasDefiniteHeight = floatPixelHeight is not null || floatFillsHeight;
                bool heightIsAlreadyContained = parentHasDefiniteHeight
                    || parentHasClearfix
                    || (parentMinHeight is { } minimum
                        && floatPixelHeight is { } height
                        && minimum + 0.001f >= height);
                bool soleFullWidthFloat = substantive.Count == 1
                    && floatPercentWidth is { } percent
                    && FillsAxis(percent)
                    && floatHasDefiniteHeight
                    && heightIsAlreadyContained;

                NodeId? splitBandFlow = null;
                if (parentHasDefiniteHeight
                    && substantive.Count == 2
                    && substantive[0] == floatDom
                    && floatStyle?.Float == Float.Left
                    && floatFillsHeight
                    && styles.TryGetValue(substantive[1], out LayoutStyle? flowStyle)
                    && flowStyle.Width.Kind == DimensionKind.Percent)
                {
                    float flowPercentWidth = flowStyle.Width.Value;
                    bool flowFillsHeight = flowStyle.Height.Kind == DimensionKind.Percent
                        && FillsAxis(flowStyle.Height.Value)
                        && flowStyle.SizeExpressions[1] is null;
                    bool widthsFillOneBand = floatPercentWidth is { } floatWidth
                        && floatWidth > 0f
                        && flowPercentWidth > 0f
                        && MathF.Abs(floatWidth + flowPercentWidth - 1f) < 0.001f;
                    if (flowStyle.Display != Display.None
                        && !flowStyle.DisplayContents
                        && flowStyle.IsInlineBlock
                        && flowFillsHeight
                        && widthsFillOneBand)
                    {
                        splitBandFlow = substantive[1];
                    }
                }

                if (soleFullWidthFloat || splitBandFlow is not null)
                {
                    TaffyNodeId? floatNode = Build(context, floatDom);

                    // The split-band guard admits only a boxed inline-block element, so it must
                    // be built as one atomic box.
                    TaffyNodeId? flowNode = splitBandFlow is { } flowDom
                        ? Build(context, flowDom)
                        : null;
                    if (floatNode is { } builtFloat && (soleFullWidthFloat || flowNode is not null))
                    {
                        List<TaffyNodeId> children = [builtFloat];
                        if (flowNode is { } builtFlow)
                        {
                            children.Add(builtFlow);
                        }

                        TaffyStyle bandStyle = TaffyStyle.Default;
                        bandStyle.Display = TaffyDisplay.Flex;
                        bandStyle.FlexDirection = TaffyFlexDirection.Row;
                        bandStyle.FlexWrap = TaffyFlexWrap.NoWrap;
                        bandStyle.AlignItems = TaffyAlignItems.FlexStart;
                        bandStyle.Size = new Layout.Size<TaffyDimension>(
                            TaffyDimension.FromPercent(1f),
                            parentHasDefiniteHeight
                                ? TaffyDimension.FromPercent(1f)
                                : TaffyDimension.Auto);
                        return [context.TaffyTree.NewWithChildren(bandStyle, [.. children])];
                    }
                }
            }
        }

        // A block made entirely from inline-ish flow content and two or more right floats is
        // the classic utility/navigation bar.
        List<NodeId> rightFloats = [];
        foreach (NodeId cid in domChildren)
        {
            if (styles.TryGetValue(cid, out LayoutStyle? style)
                && style.Float == Float.Right
                && style.Display != Display.None)
            {
                rightFloats.Add(cid);
            }
        }

        bool hasLeftFloat = false;
        foreach (NodeId cid in domChildren)
        {
            if (styles.TryGetValue(cid, out LayoutStyle? style)
                && style.Float == Float.Left
                && style.Display != Display.None)
            {
                hasLeftFloat = true;
                break;
            }
        }

        bool flowIsInline = true;
        foreach (NodeId cid in domChildren)
        {
            if (tree.GetNode(cid) is not { } node)
            {
                continue;
            }

            if (!node.IsElement || IsFloat(cid))
            {
                continue;
            }

            if (styles.TryGetValue(cid, out LayoutStyle? style) && style.Display == Display.Block)
            {
                flowIsInline = false;
                break;
            }
        }

        if (rightFloats.Count >= 2 && !hasLeftFloat && flowIsInline)
        {
            // Removing out-of-flow items must not remove the one collapsible space between the
            // inline items on either side of them.
            List<NodeId> flowDom = [];
            NodeId? pendingWhitespace = null;
            bool hasFlowContent = false;
            foreach (NodeId cid in domChildren)
            {
                if (IsFloat(cid))
                {
                    continue;
                }

                bool isWhitespace = tree.GetNode(cid) is { } node
                    && !node.IsElement
                    && tree.TextContent(cid).Trim().Length == 0;
                if (isWhitespace)
                {
                    if (hasFlowContent && pendingWhitespace is null)
                    {
                        pendingWhitespace = cid;
                    }

                    continue;
                }

                if (hasFlowContent && pendingWhitespace is { } whitespace)
                {
                    flowDom.Add(whitespace);
                    pendingWhitespace = null;
                }

                flowDom.Add(cid);
                hasFlowContent = true;
            }

            List<TaffyNodeId> rowChildren = [];
            foreach (NodeId cid in flowDom)
            {
                rowChildren.AddRange(BuildAny(context, cid));
            }

            List<TaffyNodeId> rightChildren = [];
            for (int index = rightFloats.Count - 1; index >= 0; index--)
            {
                if (Build(context, rightFloats[index]) is { } node)
                {
                    rightChildren.Add(node);
                }
            }

            if (rowChildren.Count != 0 && rightChildren.Count != 0)
            {
                TaffyStyle rightGroupStyle = TaffyStyle.Default;
                rightGroupStyle.Display = TaffyDisplay.Flex;
                rightGroupStyle.FlexDirection = TaffyFlexDirection.Row;
                rightGroupStyle.FlexWrap = TaffyFlexWrap.Wrap;
                rightGroupStyle.Margin = new Layout.Rect<TaffyLengthPercentageAuto>(
                    TaffyLengthPercentageAuto.Auto,
                    TaffyLengthPercentageAuto.FromLength(0f),
                    TaffyLengthPercentageAuto.FromLength(0f),
                    TaffyLengthPercentageAuto.FromLength(0f));
                TaffyNodeId rightGroup =
                    context.TaffyTree.NewWithChildren(rightGroupStyle, [.. rightChildren]);
                rowChildren.Add(rightGroup);
                TaffyStyle rowStyle = TaffyStyle.Default;
                rowStyle.Display = TaffyDisplay.Flex;
                rowStyle.FlexDirection = TaffyFlexDirection.Row;
                rowStyle.FlexWrap = TaffyFlexWrap.Wrap;
                rowStyle.AlignItems = TaffyAlignItems.FlexStart;
                rowStyle.Size = new Layout.Size<TaffyDimension>(
                    TaffyDimension.FromPercent(1f),
                    TaffyDimension.Auto);
                return [context.TaffyTree.NewWithChildren(rowStyle, [.. rowChildren])];
            }
        }

        int floatIdx = -1;
        for (int index = 0; index < domChildren.Count; index++)
        {
            if (IsFloat(domChildren[index]))
            {
                floatIdx = index;
                break;
            }
        }

        if (floatIdx < 0)
        {
            List<TaffyNodeId> plain = [];
            foreach (NodeId cid in domChildren)
            {
                plain.AddRange(BuildAny(context, cid));
            }

            return plain;
        }

        List<TaffyNodeId> result = [];
        for (int index = 0; index < floatIdx; index++)
        {
            result.AddRange(BuildAny(context, domChildren[index]));
        }

        Float? floatSide = styles.TryGetValue(domChildren[floatIdx], out LayoutStyle? floatChildStyle)
            ? floatChildStyle.Float
            : null;

        // Opposing header floats share one float band even when an empty legacy compatibility
        // box sits between them.
        bool IsEmptyBridge(NodeId cid)
        {
            if (tree.GetNode(cid) is not { } node)
            {
                return true;
            }

            if (!node.IsElement)
            {
                return tree.TextContent(cid).Trim().Length == 0;
            }

            styles.TryGetValue(cid, out LayoutStyle? style);
            if (style is not null && style.Display == Display.None)
            {
                return true;
            }

            bool noSize = true;
            if (style is not null)
            {
                bool anyPaddingPercent = false;
                foreach (float? value in style.PaddingPercent)
                {
                    if (value is not null)
                    {
                        anyPaddingPercent = true;
                        break;
                    }
                }

                noSize = style.Width.IsAuto
                    && style.Height.IsAuto
                    && style.MinWidth.IsAuto
                    && style.MinHeight.IsAuto
                    && style.MaxWidth.IsAuto
                    && style.MaxHeight.IsAuto
                    && style.Margin == Edges.Zero
                    && style.Padding == Edges.Zero
                    && !anyPaddingPercent
                    && style.Border == Edges.Zero
                    && style.BeforePseudo is null
                    && style.AfterPseudo is null;
            }

            return noSize && tree.TextContent(cid).Trim().Length == 0;
        }

        int oppositeIdx = floatIdx + 1;
        while (oppositeIdx < domChildren.Count && IsEmptyBridge(domChildren[oppositeIdx]))
        {
            oppositeIdx++;
        }

        Float? oppositeSide = null;
        if (oppositeIdx < domChildren.Count
            && styles.TryGetValue(domChildren[oppositeIdx], out LayoutStyle? oppositeStyle)
            && oppositeStyle.Display != Display.None)
        {
            oppositeSide = oppositeStyle.Float;
        }

        if (oppositeSide is not null && oppositeSide != floatSide)
        {
            TaffyNodeId? first = Build(context, domChildren[floatIdx]);
            TaffyNodeId? second = Build(context, domChildren[oppositeIdx]);
            List<TaffyNodeId> rowChildren = [];
            if (floatSide == Float.Left)
            {
                if (first is { } a)
                {
                    rowChildren.Add(a);
                }

                if (second is { } b)
                {
                    rowChildren.Add(b);
                }
            }
            else
            {
                if (second is { } b)
                {
                    rowChildren.Add(b);
                }

                if (first is { } a)
                {
                    rowChildren.Add(a);
                }
            }

            TaffyStyle rowStyle = TaffyStyle.Default;
            rowStyle.Display = TaffyDisplay.Flex;
            rowStyle.FlexDirection = TaffyFlexDirection.Row;
            rowStyle.JustifyContent = TaffyJustifyContent.SpaceBetween;
            rowStyle.AlignItems = TaffyAlignItems.FlexStart;
            rowStyle.Size = new Layout.Size<TaffyDimension>(
                TaffyDimension.FromPercent(1f),
                TaffyDimension.Auto);
            result.Add(context.TaffyTree.NewWithChildren(rowStyle, [.. rowChildren]));
            for (int index = oppositeIdx + 1; index < domChildren.Count; index++)
            {
                result.AddRange(BuildAny(context, domChildren[index]));
            }

            return result;
        }

        // A run of two or more consecutively floated siblings is the classic float-grid idiom.
        int runEnd = floatIdx + 1;
        int floatCount = 1;
        while (runEnd < domChildren.Count)
        {
            NodeId cid = domChildren[runEnd];
            if (IsFloat(cid)
                && styles.TryGetValue(cid, out LayoutStyle? runStyle)
                && runStyle.Float == floatSide)
            {
                floatCount++;
                runEnd++;
            }
            else if (tree.GetNode(cid) is { IsElement: false }
                && tree.TextContent(cid).Trim().Length == 0)
            {
                runEnd++;
            }
            else
            {
                break;
            }
        }

        if (floatCount >= 2)
        {
            List<TaffyNodeId> runChildren = [];
            for (int index = floatIdx; index < runEnd; index++)
            {
                NodeId cid = domChildren[index];

                // Formatting whitespace between floats does not generate an in-flow flex item.
                if (styles.TryGetValue(cid, out LayoutStyle? runStyle) && runStyle.Float == floatSide)
                {
                    runChildren.AddRange(BuildAny(context, cid));
                }
            }

            // A common navigation-bar shape is a run of left floats followed by one right float.
            NodeId? trailingRight = null;
            if (floatSide == Float.Left
                && runEnd < domChildren.Count
                && styles.TryGetValue(domChildren[runEnd], out LayoutStyle? trailingStyle)
                && trailingStyle.Display != Display.None
                && trailingStyle.Float == Float.Right)
            {
                trailingRight = domChildren[runEnd];
            }

            if (trailingRight is { } rightDom && Build(context, rightDom) is { } right)
            {
                TaffyStyle pushedRight = context.TaffyTree.GetStyle(right).Clone();
                Layout.Rect<TaffyLengthPercentageAuto> margin = pushedRight.Margin;
                margin.Left = TaffyLengthPercentageAuto.Auto;
                pushedRight.Margin = margin;
                context.TaffyTree.SetStyle(right, pushedRight);
                runChildren.Add(right);
                TaffyStyle trailingRowStyle = TaffyStyle.Default;
                trailingRowStyle.Display = TaffyDisplay.Flex;
                trailingRowStyle.FlexDirection = TaffyFlexDirection.Row;
                trailingRowStyle.FlexWrap = TaffyFlexWrap.Wrap;
                trailingRowStyle.AlignItems = TaffyAlignItems.FlexStart;
                trailingRowStyle.Size = new Layout.Size<TaffyDimension>(
                    TaffyDimension.FromPercent(1f),
                    TaffyDimension.Auto);
                result.Add(context.TaffyTree.NewWithChildren(trailingRowStyle, [.. runChildren]));
                result.AddRange(BuildChildrenWithFloatZone(
                    context,
                    parentId,
                    domChildren.Skip(runEnd + 1).ToList()));
                return result;
            }

            TaffyStyle runRowStyle = TaffyStyle.Default;
            runRowStyle.Display = TaffyDisplay.Flex;
            runRowStyle.FlexDirection = TaffyFlexDirection.Row;
            runRowStyle.FlexWrap = TaffyFlexWrap.Wrap;
            runRowStyle.AlignItems = TaffyAlignItems.FlexStart;

            // This anonymous row represents the float band's available inline size.
            runRowStyle.Size = new Layout.Size<TaffyDimension>(
                TaffyDimension.FromPercent(1f),
                TaffyDimension.Auto);
            result.Add(context.TaffyTree.NewWithChildren(runRowStyle, [.. runChildren]));
            result.AddRange(BuildChildrenWithFloatZone(
                context,
                parentId,
                domChildren.Skip(runEnd).ToList()));
            return result;
        }

        // Stop growing the zone once the flow siblings collected so far would already fill an
        // estimate of the float's own height.
        float floatHeightBudget =
            DomTableSupport.EstimateFloatHeight(tree, domChildren[floatIdx], styles);
        const float assumedFlowWidth = 500f;

        bool ClearsThisFloat(NodeId cid)
        {
            if (!styles.TryGetValue(cid, out LayoutStyle? style)
                || style.Display == Display.None
                || style.Clear is not { } clear)
            {
                return false;
            }

            return clear == Clear.Both
                || (floatSide == Float.Left && clear == Clear.Left)
                || (floatSide == Float.Right && clear == Clear.Right);
        }

        int zoneEnd = floatIdx + 1;
        float flowHeightEstimate = 0f;
        while (zoneEnd < domChildren.Count
            && !IsFloat(domChildren[zoneEnd])
            && !ClearsThisFloat(domChildren[zoneEnd]))
        {
            flowHeightEstimate += DomTableSupport.EstimateFlowSiblingHeight(
                tree, domChildren[zoneEnd], styles, assumedFlowWidth);
            zoneEnd++;
            if (flowHeightEstimate >= floatHeightBudget)
            {
                break;
            }
        }

        TaffyNodeId? floatTaffy = Build(context, domChildren[floatIdx]);

        // Cap an auto-width float at its widest definite-width descendant.
        if (floatTaffy is { } floatId)
        {
            NodeId floatDom = domChildren[floatIdx];
            bool floatAuto = !styles.TryGetValue(floatDom, out LayoutStyle? autoStyle)
                || autoStyle.Width.IsAuto;
            bool floatIsTable = styles.TryGetValue(floatDom, out LayoutStyle? tableStyle)
                && tableStyle.IsTableBox;
            if (floatAuto
                && floatIsTable
                && DomTableSupport.MaxDefiniteDescendantWidth(tree, floatDom, styles) is { } capWidth)
            {
                TaffyStyle capped = context.TaffyTree.GetStyle(floatId).Clone();
                Layout.Size<TaffyDimension> maxSize = capped.MaxSize;

                // A little slack for the figure's own border/padding.
                maxSize.Width = TaffyDimension.FromLength(capWidth + 12f);
                capped.MaxSize = maxSize;
                context.TaffyTree.SetStyle(floatId, capped);
            }
        }

        if (floatTaffy is { } builtFloatId)
        {
            TaffyStyle flowColumnStyle = TaffyStyle.Default;
            flowColumnStyle.Display = TaffyDisplay.Block;
            flowColumnStyle.FlexGrow = 1f;
            flowColumnStyle.FlexShrink = 1f;
            flowColumnStyle.FlexBasis = TaffyDimension.FromLength(0f);
            flowColumnStyle.MinSize = new Layout.Size<TaffyDimension>(
                TaffyDimension.FromLength(0f),
                TaffyDimension.Auto);

            List<NodeId> flowDom = [];
            for (int index = floatIdx + 1; index < zoneEnd; index++)
            {
                flowDom.Add(domChildren[index]);
            }

            TaffyNodeId? flowColumn;
            if (flowDom.Count == 0)
            {
                flowColumn = context.TaffyTree.NewLeaf(flowColumnStyle);
            }
            else
            {
                // The zone is still an ordinary block formatting context. Reuse the
                // mixed-block builder, but leave this anonymous wrapper out of the DOM id map.
                LayoutStyle flowStyle = styles.TryGetValue(parentId, out LayoutStyle? parentFlow)
                    ? parentFlow.Clone()
                    : new LayoutStyle();
                flowStyle.BeforeContent = null;
                flowStyle.AfterContent = null;
                flowColumn = BuildMixedBlock(context, parentId, flowStyle, flowColumnStyle, flowDom);
                if (flowColumn is { } columnId)
                {
                    context.IdMap.Remove(columnId);
                }
            }

            TaffyStyle rowStyle = TaffyStyle.Default;
            rowStyle.Display = TaffyDisplay.Flex;
            rowStyle.FlexDirection = TaffyFlexDirection.Row;
            rowStyle.AlignItems = TaffyAlignItems.FlexStart;
            rowStyle.Size = new Layout.Size<TaffyDimension>(
                TaffyDimension.FromPercent(1f),
                TaffyDimension.Auto);

            // A float does not contribute to the height of a non-BFC block that contains it.
            bool canEscape = zoneEnd == domChildren.Count
                && styles.TryGetValue(parentId, out LayoutStyle? escapeParent)
                && !DomStyleFixups.EstablishesBlockFormattingContext(escapeParent);
            TaffyNodeId rowFloat = builtFloatId;
            if (canEscape)
            {
                TaffyStyle wrapperStyle = TaffyStyle.Default;
                wrapperStyle.Display = TaffyDisplay.Block;
                wrapperStyle.FlexGrow = 0f;
                wrapperStyle.FlexShrink = 0f;
                wrapperStyle.Size = new Layout.Size<TaffyDimension>(
                    TaffyDimension.Auto,
                    TaffyDimension.FromLength(0f));
                rowFloat = context.TaffyTree.NewWithChildren(wrapperStyle, [builtFloatId]);
            }

            List<TaffyNodeId> rowChildren = [];
            if (floatSide == Float.Left)
            {
                rowChildren.Add(rowFloat);
                if (flowColumn is { } column)
                {
                    rowChildren.Add(column);
                }
            }
            else
            {
                if (flowColumn is { } column)
                {
                    rowChildren.Add(column);
                }

                rowChildren.Add(rowFloat);
            }

            TaffyNodeId row = context.TaffyTree.NewWithChildren(rowStyle, [.. rowChildren]);
            result.Add(row);
            if (canEscape && flowColumn is { } flow && floatSide is { } side)
            {
                context.Ifc.FloatContinuations.Add(
                    new FloatContinuation(parentId, builtFloatId, flow, side));
            }
        }
        else
        {
            // The float itself failed to build; still build its flow siblings so their content
            // is not silently lost.
            for (int index = floatIdx + 1; index < zoneEnd; index++)
            {
                result.AddRange(BuildAny(context, domChildren[index]));
            }
        }

        for (int index = zoneEnd; index < domChildren.Count; index++)
        {
            result.AddRange(BuildAny(context, domChildren[index]));
        }

        return result;
    }
}
