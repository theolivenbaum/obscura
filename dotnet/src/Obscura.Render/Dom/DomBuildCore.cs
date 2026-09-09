// Port of `build` in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using TaffyAlignItems = Obscura.Render.Layout.AlignItems;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDisplay = Obscura.Render.Layout.Display;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyFlexWrap = Obscura.Render.Layout.FlexWrap;
using TaffyJustifyContent = Obscura.Render.Layout.AlignContent;
using TaffyLengthPercentage = Obscura.Render.Layout.LengthPercentage;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyPosition = Obscura.Render.Layout.Position;
using TaffyStyle = Obscura.Render.Layout.Style;

namespace Obscura.Render;

internal static partial class DomBuild
{
    internal static TaffyNodeId? Build(BuildContext context, NodeId id)
    {
        DomTree tree = context.Tree;
        if (tree.GetNode(id) is not { } node || node.AsElement() is not { } element)
        {
            return null;
        }

        if (!context.Styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        if (style.Display == Display.None)
        {
            return null;
        }

        string local = element.Name.Local;

        // Real table layout: every computed table box becomes a CSS grid so columns negotiate
        // across rows and colspan/rowspan map to grid spans.
        if (style.IsTableBox && BuildTable(context, id) is { } tableNode)
        {
            return tableNode;
        }

        TaffyStyle taffyStyle = TaffyStyleMapping.ToTaffyStyle(style);
        DomStyleFixups.ApplyContainerSizeContainment(tree, id, style, context.Styles, taffyStyle);

        // A non-stretched flex item in a column flex container uses fit-content for its auto
        // inline size. Taffy has no fit-content box-size value; a synthetic percentage max has
        // the same final effect.
        if (NeedsColumnFlexTextFitContentCap(tree, id, style, context.Styles))
        {
            Layout.Size<TaffyDimension> maxSize = taffyStyle.MaxSize;
            maxSize.Width = TaffyDimension.FromPercent(1f);
            taffyStyle.MaxSize = maxSize;
        }

        // A grid item's automatic minimum size is clamped by a definite max-width. Taffy's
        // intrinsic track pass cannot resolve a percentage max until the track exists.
        if (style.MaxWidth.Kind == DimensionKind.Percent
            && style.MinWidth.IsAuto
            && DomStyleFixups.IsInFlowGridItem(tree, id, style, context.Styles))
        {
            Layout.Size<TaffyDimension> minSize = taffyStyle.MinSize;
            minSize.Width = TaffyDimension.FromLength(0f);
            taffyStyle.MinSize = minSize;
        }

        // Outside a foldable inline run, BR is a zero-inline-size line participant.
        if (string.Equals(local, "br", StringComparison.Ordinal))
        {
            float height = F32.Max(FontResolution.UsedLineHeight(style), 0f);
            taffyStyle.Size = new Layout.Size<TaffyDimension>(
                TaffyDimension.FromLength(0f),
                TaffyDimension.FromLength(height));
            taffyStyle.FlexGrow = 0f;
            taffyStyle.FlexShrink = 0f;
            TaffyNodeId leaf = context.TaffyTree.NewLeaf(taffyStyle);
            context.IdMap[leaf] = id;
            return leaf;
        }

        // CSS has separate outer and inner display types, while taffy exposes one display
        // value. Apply the block inner mode only in a flex/grid formatting context.
        if (style.IsInlineBlock)
        {
            NodeId? parent = DomTraversal.RenderedParent(tree, id);
            bool flexOrGridItem = false;
            while (true)
            {
                if (parent is not { } parentId)
                {
                    break;
                }

                if (!context.Styles.TryGetValue(parentId, out LayoutStyle? parentStyle))
                {
                    break;
                }

                if (parentStyle.DisplayContents)
                {
                    parent = DomTraversal.RenderedParent(tree, parentId);
                    continue;
                }

                flexOrGridItem = parentStyle.Display is Display.Flex or Display.Grid
                    && !parentStyle.InternalFlexContainer;
                break;
            }

            if (flexOrGridItem)
            {
                taffyStyle.Display = TaffyDisplay.Block;
                taffyStyle.FlexWrap = TaffyFlexWrap.NoWrap;
            }
            else if (style.Width.IsAuto)
            {
                // An auto-width inline-block shrink-fits to its max-content width when that
                // fits the available line.
                bool inlineBlockHasBlockChild = false;
                foreach (NodeId child in DomTraversal.RenderedChildren(tree, id))
                {
                    if (context.Styles.TryGetValue(child, out LayoutStyle? childStyle)
                        && DomStyleFixups.IsInFlowBlockLevel(childStyle))
                    {
                        inlineBlockHasBlockChild = true;
                        break;
                    }
                }

                if (!inlineBlockHasBlockChild)
                {
                    taffyStyle.FlexWrap = TaffyFlexWrap.NoWrap;
                }
            }
        }

        // An inline SVG is an atomic replaced box in the surrounding formatting context.
        if (string.Equals(local, "svg", StringComparison.Ordinal)
            && style.AspectRatio is { } svgRatio
            && float.IsFinite(svgRatio)
            && svgRatio > 0f)
        {
            // A 100% inline size inside an auto grid track is cyclic during intrinsic sizing.
            if (style.Width.Kind == DimensionKind.Percent
                && style.Width.Value == 1f
                && style.Height.IsAuto
                && DomStyleFixups.IsInFlowGridItem(tree, id, style, context.Styles))
            {
                Layout.Size<TaffyDimension> size = taffyStyle.Size;
                size.Width = TaffyDimension.Auto;
                taffyStyle.Size = size;
                Layout.Size<TaffyDimension> minSize = taffyStyle.MinSize;
                minSize.Width = TaffyDimension.FromLength(0f);
                taffyStyle.MinSize = minSize;
                taffyStyle.JustifySelf = TaffyAlignItems.Stretch;
            }

            // A viewBox supplies a ratio but no intrinsic dimensions. 300px is CSS Images'
            // default object size.
            const float defaultSvgWidth = 300f;
            float svgHeight = defaultSvgWidth / svgRatio;
            int svgContext = context.Engine.RegisterReplaced(defaultSvgWidth, svgHeight, style);
            TaffyNodeId svgLeaf = context.TaffyTree.NewLeafWithContext(taffyStyle, svgContext);
            context.IdMap[svgLeaf] = id;
            return svgLeaf;
        }

        // A loaded poster supplies <video>'s replaced-content dimensions before decoded video
        // metadata exists.
        if (!(string.Equals(local, "video", StringComparison.Ordinal)
                && style.ReplacedIntrinsic is not null))
        {
            (float Width, float Height)? defaultIntrinsic = Inline.DefaultReplacedIntrinsicSize(
                local,
                style.FontSize ?? 16f,
                node.GetAttribute("controls") is not null,
                !string.Equals(local, "embed", StringComparison.Ordinal)
                    || (node.GetAttribute("src") is { } src && src.Trim().Length != 0));
            if (defaultIntrinsic is { } intrinsicSize)
            {
                int replacedContext = context.Engine.RegisterReplaced(
                    intrinsicSize.Width, intrinsicSize.Height, style);
                TaffyNodeId replacedLeaf =
                    context.TaffyTree.NewLeafWithContext(taffyStyle, replacedContext);
                context.IdMap[replacedLeaf] = id;
                return replacedLeaf;
            }
        }

        // A replaced image is a measured leaf, even when CSS gives it a percentage width.
        if (local is "img" or "video")
        {
            ReplacedIntrinsic? metadata = style.ReplacedIntrinsic
                ?? (style.IntrinsicSize is { } natural
                    ? ReplacedIntrinsic.FromDimensions(natural.Width, natural.Height)
                    : null);
            if (metadata is { } intrinsic)
            {
                (float width, float height) = intrinsic.NaturalSize() ?? (300f, 150f);
                float intrinsicRatio = intrinsic.Ratio ?? 2f;
                float preferredRatio = style.AspectRatio is { } authored
                    && float.IsFinite(authored)
                    && authored > 0f
                        ? authored
                        : intrinsicRatio;

                // Encode the equivalent min(preferred-width, percentage-max) function by
                // swapping the two operands.
                if (style.MaxWidth.Kind == DimensionKind.Percent)
                {
                    float? preferredWidth = style.Width.Kind switch
                    {
                        DimensionKind.Px => style.Width.Value,
                        DimensionKind.Auto => style.Height.Kind == DimensionKind.Px
                            ? style.Height.Value * preferredRatio
                            : Inline.ConstrainedAutoReplacedSize(width, height, style).Width,
                        _ => null,
                    };
                    if (preferredWidth is { } preferred)
                    {
                        Layout.Size<TaffyDimension> size = taffyStyle.Size;
                        size.Width = TaffyDimension.FromPercent(style.MaxWidth.Value);
                        taffyStyle.Size = size;
                        Layout.Size<TaffyDimension> maxSize = taffyStyle.MaxSize;
                        maxSize.Width = TaffyDimension.FromLength(F32.Max(preferred, 0f));
                        taffyStyle.MaxSize = maxSize;
                    }
                }

                // An auto/auto replaced box starts from its intrinsic width rather than the
                // fill-available width of an ordinary auto-width block.
                bool hasDefiniteConstraint =
                    style.MinWidth.Kind == DimensionKind.Px
                    || style.MinHeight.Kind == DimensionKind.Px
                    || style.MaxWidth.Kind == DimensionKind.Px
                    || style.MaxHeight.Kind == DimensionKind.Px;
                bool hasPercentageConstraint =
                    style.MinWidth.Kind == DimensionKind.Percent
                    || style.MinHeight.Kind == DimensionKind.Percent
                    || style.MaxWidth.Kind == DimensionKind.Percent
                    || style.MaxHeight.Kind == DimensionKind.Percent;
                if (!hasPercentageConstraint)
                {
                    for (int index = 2; index <= 5; index++)
                    {
                        if (style.SizeExpressions[index] is { } expression
                            && expression.Contains('%', StringComparison.Ordinal))
                        {
                            hasPercentageConstraint = true;
                            break;
                        }
                    }
                }

                bool ratioOnly = intrinsic.Width is null
                    && intrinsic.Height is null
                    && intrinsic.Ratio is not null;
                if (style.Width.IsAuto
                    && style.Height.IsAuto
                    && !hasPercentageConstraint
                    && !ratioOnly
                    && !DomStyleFixups.IsInFlowGridItem(tree, id, style, context.Styles))
                {
                    Layout.Size<float> constrained =
                        Inline.ConstrainedAutoReplacedSize(width, height, style);
                    Layout.Size<TaffyDimension> size = taffyStyle.Size;
                    size.Width = TaffyDimension.FromLength(constrained.Width);
                    if (hasDefiniteConstraint)
                    {
                        size.Height = TaffyDimension.FromLength(constrained.Height);
                    }

                    taffyStyle.Size = size;
                }

                // When an auto axis has a definite min/max constraint, the measured replaced
                // leaf must own intrinsic-ratio transfer so it can clamp the derived size.
                bool measuredAxisConstraint =
                    (!style.Height.IsAuto
                        && style.Width.IsAuto
                        && (style.MinWidth.Kind == DimensionKind.Px
                            || style.MaxWidth.Kind == DimensionKind.Px))
                    || (!style.Width.IsAuto
                        && style.Height.IsAuto
                        && (style.MinHeight.Kind == DimensionKind.Px
                            || style.MaxHeight.Kind == DimensionKind.Px));
                if (measuredAxisConstraint)
                {
                    taffyStyle.AspectRatio = null;
                }

                int imageContext = context.Engine.RegisterReplacedIntrinsic(intrinsic, style);
                TaffyNodeId imageLeaf = context.TaffyTree.NewLeafWithContext(taffyStyle, imageContext);
                context.IdMap[imageLeaf] = id;
                return imageLeaf;
            }
        }

        // If this container is a pure-text inline formatting context, collapse its whole
        // subtree to one shaped, line-broken leaf.
        bool isClosedHtmlDetails =
            string.Equals(element.Name.Ns, Namespaces.Html, StringComparison.Ordinal)
            && string.Equals(local, "details", StringComparison.Ordinal)
            && node.GetAttribute("open") is null;
        if (!isClosedHtmlDetails && !context.Ifc.FloatAwareBlocks.Contains(id))
        {
            if (context.Engine.TryBuild(tree, id, context.Styles) is { } item)
            {
                if (style.Display == Display.Block && style.Width.IsAuto)
                {
                    // A pure-text block is still a fill-available block; its shaped inline
                    // context performs text alignment internally.
                    taffyStyle.Display = TaffyDisplay.Block;
                }

                TaffyNodeId leaf = context.TaffyTree.NewLeafWithContext(taffyStyle, item);
                context.IdMap[leaf] = id;
                context.Ifc.Whole[id] = item;
                return leaf;
            }
        }

        // Taffy has no real inline formatting context: approximate one by promoting blocks
        // holding inline-level content to a wrapping flex row.
        bool stacksChildrenVertically = style.Display == Display.Block
            || (style.Display == Display.Flex && style.FlexDirection == TaffyFlexDirection.Column);
        bool hasInlineIshContent = DomStyleFixups.HasInlineContent(tree, id, context.Styles)
            || HasInFlowGeneratedPseudo(style.BeforePseudo)
            || HasInFlowGeneratedPseudo(style.AfterPseudo);

        List<NodeId> domChildren = DomTraversal.RenderedChildren(tree, id);

        // A boxless inline wrapper is transparent to our approximated box tree.
        if (style.Display == Display.Block || style.InternalFlexContainer)
        {
            List<NodeId> flattened = [];
            FlattenBoxlessInlineChildren(tree, domChildren, context.Styles, flattened);
            domChildren = flattened;
        }

        bool internalMixedBlockFlow = false;
        if (style.InternalFlexContainer)
        {
            foreach (NodeId cid in domChildren)
            {
                if (context.Styles.TryGetValue(cid, out LayoutStyle? childStyle)
                    && DomStyleFixups.IsInFlowBlockLevel(childStyle))
                {
                    internalMixedBlockFlow = true;
                    break;
                }
            }
        }

        // In flex and grid formatting contexts, collapsible whitespace-only text between items
        // does not generate an anonymous item.
        if (style.Display is Display.Flex or Display.Grid && !internalMixedBlockFlow)
        {
            List<NodeId> flat = [];
            FlattenContentsChildren(tree, domChildren, context.Styles, flat);
            domChildren = flat;
            domChildren.RemoveAll(cid =>
                tree.GetNode(cid)?.IsElement != true && tree.TextContent(cid).Trim().Length == 0);

            // Flex and grid placement consume the order-modified document order; a stable sort
            // preserves source order for equal values, exactly the CSS tie-break.
            domChildren = [.. domChildren
                .Select((cid, index) => (cid, index))
                .OrderBy(pair => context.Styles.TryGetValue(pair.cid, out LayoutStyle? s) ? s.Order : 0)
                .ThenBy(pair => pair.index)
                .Select(pair => pair.cid)];
        }
        else if (style.Display == Display.Block)
        {
            // Formatting whitespace between block lines does not generate an inline line box.
            if (!hasInlineIshContent)
            {
                domChildren.RemoveAll(cid =>
                    tree.GetNode(cid)?.IsElement != true && tree.TextContent(cid).Trim().Length == 0);
            }

            // A float nested directly in a transparent inline wrapper belongs to the ancestor
            // block formatting context.
            for (int index = 0; index < domChildren.Count; index++)
            {
                if (InlineWrapperFloat(tree, domChildren[index], context.Styles) is { } floatChild)
                {
                    domChildren[index] = floatChild;
                }
            }
        }

        // Main-axis auto margins absorb positive free space before `justify-content` is applied.
        if (style.Display == Display.Flex)
        {
            bool column = style.FlexDirection
                is TaffyFlexDirection.Column or TaffyFlexDirection.ColumnReverse;
            bool hasMainAutoMargin = false;
            foreach (NodeId cid in domChildren)
            {
                if (!context.Styles.TryGetValue(cid, out LayoutStyle? childStyle))
                {
                    continue;
                }

                if (column
                    ? childStyle.MarginAuto[0] || childStyle.MarginAuto[2]
                    : childStyle.MarginAuto[1] || childStyle.MarginAuto[3])
                {
                    hasMainAutoMargin = true;
                    break;
                }
            }

            if (hasMainAutoMargin)
            {
                taffyStyle.JustifyContent = TaffyJustifyContent.FlexStart;
            }
        }

        // `float` has no effect on a flex or grid item.
        bool hasFloatChild = false;
        if (style.Display == Display.Block)
        {
            foreach (NodeId cid in domChildren)
            {
                if (context.Styles.TryGetValue(cid, out LayoutStyle? childStyle)
                    && childStyle.Float is not null)
                {
                    hasFloatChild = true;
                    break;
                }
            }
        }

        bool nativeFloatBand = hasFloatChild
            && DomTableSupport.CanUseNativeFloatBand(tree, style, domChildren, context.Styles);
        bool hasInFlowBlockChild = false;
        foreach (NodeId cid in domChildren)
        {
            if (context.Styles.TryGetValue(cid, out LayoutStyle? childStyle)
                && DomStyleFixups.IsInFlowBlockLevel(childStyle))
            {
                hasInFlowBlockChild = true;
                break;
            }
        }

        bool isInternalTableCell = style.IsTableCellBox && style.InternalFlexContainer;

        // `text-align` affects inline content, never the used width or placement of an in-flow
        // block child. Keep the legacy `<center>` behavior.
        if (style.Display == Display.Block && hasInFlowBlockChild && !style.LegacyCenter)
        {
            taffyStyle.Display = TaffyDisplay.Block;
        }

        // A block with mixed inline + block children keeps real block layout.
        if ((style.Display == Display.Block || (isInternalTableCell && hasInFlowBlockChild))
            && hasInlineIshContent
            && !hasFloatChild)
        {
            return BuildMixedBlock(context, id, style, taffyStyle, domChildren);
        }

        if (stacksChildrenVertically
            && hasInlineIshContent
            && !nativeFloatBand
            && !(hasFloatChild && hasInFlowBlockChild))
        {
            taffyStyle.Display = TaffyDisplay.Flex;
            taffyStyle.FlexDirection = TaffyFlexDirection.Row;

            // The row-flex stand-in is our inline formatting context. A nowrap table cell must
            // keep adjacent atomic inline children on one line.
            taffyStyle.FlexWrap = style.WhiteSpace == Render.WhiteSpace.NoWrap
                ? TaffyFlexWrap.NoWrap
                : TaffyFlexWrap.Wrap;

            // The promoted container's main axis is horizontal, so text alignment belongs on
            // `justify_content`. Real `justify-content` from actual CSS wins if present.
            if (style.JustifyContent is null)
            {
                if (style.TextAlign is { } align)
                {
                    if (align == TaffyAlignItems.FlexEnd)
                    {
                        taffyStyle.JustifyContent = TaffyJustifyContent.FlexEnd;
                    }
                    else if (align == TaffyAlignItems.Center)
                    {
                        taffyStyle.JustifyContent = TaffyJustifyContent.Center;
                    }
                }
            }

            taffyStyle.AlignItems = TaffyAlignItems.FlexStart;
        }

        if (nativeFloatBand)
        {
            // Native float placement requires a real block formatting context on the parent.
            taffyStyle.Display = TaffyDisplay.Block;
        }

        List<TaffyNodeId> childIds;
        if (nativeFloatBand)
        {
            childIds = BuildChildrenWithNativeFloatBand(context, id, style, domChildren);
        }
        else if (hasFloatChild)
        {
            childIds = BuildChildrenWithFloatZone(context, id, domChildren);
        }
        else if (style.Display is Display.Flex or Display.Grid && !style.InternalFlexContainer)
        {
            childIds = BuildFlexGridChildren(context, id);
        }
        else
        {
            childIds = [];
            foreach (NodeId cid in domChildren)
            {
                childIds.AddRange(BuildAny(context, cid));
            }
        }

        if (!nativeFloatBand)
        {
            if (BuildInFlowPseudo(context, id, GeneratedBoxKind.Before, style.BeforePseudo)
                is { } before)
            {
                List<TaffyNodeId> merged = [.. before.Nodes];
                merged.AddRange(childIds);
                childIds = merged;
            }

            if (BuildInFlowPseudo(context, id, GeneratedBoxKind.After, style.AfterPseudo)
                is { } after)
            {
                childIds.AddRange(after.Nodes);
            }
        }

        if (style.InternalFlexContainer
            && taffyStyle.Display == TaffyDisplay.Flex
            && taffyStyle.FlexDirection == TaffyFlexDirection.Column)
        {
            // Table cells and the other native block-layout stand-ins are not genuine CSS flex
            // containers.
            foreach (TaffyNodeId child in childIds)
            {
                bool fillsInlineAxis = context.IdMap.TryGetValue(child, out NodeId domId)
                    && context.Styles.TryGetValue(domId, out LayoutStyle? childStyle)
                    && childStyle.Display == Display.Block
                    && childStyle.Width.IsAuto
                    && childStyle.Float is null
                    && childStyle.Position != TaffyPosition.Absolute;
                TaffyStyle blockItem = context.TaffyTree.GetStyle(child).Clone();
                blockItem.FlexShrink = 0f;
                if (fillsInlineAxis)
                {
                    Layout.Size<TaffyDimension> size = blockItem.Size;
                    size.Width = TaffyDimension.FromPercent(1f);
                    blockItem.Size = size;
                }

                context.TaffyTree.SetStyle(child, blockItem);
            }
        }

        int? multicolCount = style.ColumnCount is { } count && count > 1 ? count : null;

        // Box-level balancing is sound only when every generated direct box is an authored
        // atomic fragment.
        bool atomicMulticolChildren = childIds.Count != 0;
        if (atomicMulticolChildren)
        {
            foreach (TaffyNodeId child in childIds)
            {
                if (!context.IdMap.TryGetValue(child, out NodeId domId)
                    || !context.Styles.TryGetValue(domId, out LayoutStyle? childStyle)
                    || !childStyle.BreakInsideAvoid
                    || childStyle.Float is not null
                    || childStyle.Position == TaffyPosition.Absolute)
                {
                    atomicMulticolChildren = false;
                    break;
                }
            }
        }

        TaffyNodeId taffyId;
        if (multicolCount is { } columnCount && atomicMulticolChildren)
        {
            // A multicol container keeps its ordinary outer/block box, but its inner formatting
            // context is a horizontal sequence of equal-width fragmentainers.
            taffyStyle.Display = TaffyDisplay.Flex;
            taffyStyle.FlexDirection = TaffyFlexDirection.Row;
            taffyStyle.FlexWrap = TaffyFlexWrap.NoWrap;
            if (style.ColumnGap is null)
            {
                Layout.Size<TaffyLengthPercentage> gap = taffyStyle.Gap;
                gap.Width = TaffyLengthPercentage.FromLength(style.FontSize ?? 16f);
                taffyStyle.Gap = gap;
            }

            static TaffyStyle ColumnStyle()
            {
                TaffyStyle columnStyle = TaffyStyle.Default;
                columnStyle.Display = TaffyDisplay.Block;
                columnStyle.FlexGrow = 1f;
                columnStyle.FlexShrink = 1f;
                columnStyle.FlexBasis = TaffyDimension.FromLength(0f);
                columnStyle.MinSize = new Layout.Size<TaffyDimension>(
                    TaffyDimension.FromLength(0f),
                    TaffyDimension.Auto);
                return columnStyle;
            }

            List<TaffyNodeId> columns = new(columnCount)
            {
                context.TaffyTree.NewWithChildren(ColumnStyle(), [.. childIds]),
            };
            for (int index = 1; index < columnCount; index++)
            {
                columns.Add(context.TaffyTree.NewLeaf(ColumnStyle()));
            }

            taffyId = context.TaffyTree.NewWithChildren(taffyStyle, [.. columns]);
            context.Ifc.Multicol.Add(new MulticolBuild { Columns = columns, Children = childIds });
        }
        else if (childIds.Count == 0)
        {
            taffyId = context.TaffyTree.NewLeaf(taffyStyle);
        }
        else
        {
            taffyId = context.TaffyTree.NewWithChildren(taffyStyle, [.. childIds]);
        }

        context.IdMap[taffyId] = id;
        return taffyId;
    }
}
