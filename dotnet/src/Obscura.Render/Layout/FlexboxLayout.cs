// Port of vendor/taffy/src/compute/flexbox.rs
//
// Computes the flexbox layout algorithm according to
// https://www.w3.org/TR/css-flexbox-1/
using AlignSelf = Obscura.Render.Layout.AlignItems;
using JustifyContent = Obscura.Render.Layout.AlignContent;

namespace Obscura.Render.Layout;

/// <summary>The intermediate results of a flexbox calculation for a single item.</summary>
internal sealed class FlexItem
{
    /// <summary>The identifier for the associated node.</summary>
    public NodeId Node;

    /// <summary>The order of the node relative to its siblings.</summary>
    public uint Order;

    /// <summary>The base size of this item.</summary>
    public Size<float?> Size;

    /// <summary>The minimum allowable size of this item.</summary>
    public Size<float?> MinSize;

    /// <summary>The maximum allowable size of this item.</summary>
    public Size<float?> MaxSize;

    /// <summary>The item's preferred aspect ratio.</summary>
    public float? AspectRatio;

    /// <summary>Insets excluded while transferring sizes through the preferred aspect ratio.</summary>
    public Size<float> AspectRatioAdjustment;

    /// <summary>Whether the authored cross-size is auto.</summary>
    public bool CrossSizeIsAuto;

    /// <summary>The cross-alignment of this item.</summary>
    public AlignSelf AlignSelf;

    /// <summary>The overflow style of the item.</summary>
    public Point<Overflow> Overflow;

    /// <summary>The width of the scrollbars (if it has any).</summary>
    public float ScrollbarWidth;

    /// <summary>The flex shrink style of the item.</summary>
    public float FlexShrink;

    /// <summary>The flex grow style of the item.</summary>
    public float FlexGrow;

    /// <summary>
    /// The minimum size of the item, taking content-based automatic minimum sizes into account.
    /// </summary>
    public float ResolvedMinimumMainSize;

    /// <summary>The final offset of this item.</summary>
    public Rect<float?> Inset;

    /// <summary>The margin of this item.</summary>
    public Rect<float> Margin;

    /// <summary>Whether each margin is an auto margin or not.</summary>
    public Rect<bool> MarginIsAuto;

    /// <summary>The padding of this item.</summary>
    public Rect<float> Padding;

    /// <summary>The border of this item.</summary>
    public Rect<float> Border;

    /// <summary>The default size of this item.</summary>
    public float FlexBasis;

    /// <summary>The default size of this item, minus padding and border.</summary>
    public float InnerFlexBasis;

    /// <summary>The amount by which this item has deviated from its target size.</summary>
    public float Violation;

    /// <summary>Is the size of this item locked.</summary>
    public bool Frozen;

    /// <summary>Either the max- or min- content flex fraction.</summary>
    public float ContentFlexFraction;

    /// <summary>The proposed inner size of this item.</summary>
    public Size<float> HypotheticalInnerSize;

    /// <summary>The proposed outer size of this item.</summary>
    public Size<float> HypotheticalOuterSize;

    /// <summary>The size that this item wants to be.</summary>
    public Size<float> TargetSize;

    /// <summary>The size that this item wants to be, plus any padding and border.</summary>
    public Size<float> OuterTargetSize;

    /// <summary>The position of the bottom edge of this item.</summary>
    public float Baseline;

    /// <summary>A temporary value for the main offset.</summary>
    public float OffsetMain;

    /// <summary>A temporary value for the cross offset.</summary>
    public float OffsetCross;

    /// <summary>Returns true if the item is a scroll container.</summary>
    public bool IsScrollContainer() => Overflow.X.IsScrollContainer() || Overflow.Y.IsScrollContainer();
}

/// <summary>A line of <see cref="FlexItem"/> used for intermediate computation.</summary>
internal sealed class FlexLine
{
    /// <summary>The items in this line.</summary>
    public required FlexItem[] Items;

    /// <summary>The dimensions of the cross-axis.</summary>
    public float CrossSize;

    /// <summary>The relative offset of the cross-axis.</summary>
    public float OffsetCross;
}

/// <summary>Values that can be cached during the flexbox algorithm.</summary>
internal sealed class AlgoConstants
{
    /// <summary>The direction of the current segment being laid out.</summary>
    public FlexDirection Dir;

    /// <summary>The layout direction of the current segment being laid out.</summary>
    public Direction LayoutDirection;

    /// <summary>Is this segment a row.</summary>
    public bool IsRow;

    /// <summary>Is this segment a column.</summary>
    public bool IsColumn;

    /// <summary>Is wrapping enabled (in either direction).</summary>
    public bool IsWrap;

    /// <summary>Is the wrap direction inverted.</summary>
    public bool IsWrapReverse;

    /// <summary>The node's min_size style.</summary>
    public Size<float?> MinSize;

    /// <summary>The node's max_size style.</summary>
    public Size<float?> MaxSize;

    /// <summary>The margin of this section.</summary>
    public Rect<float> Margin;

    /// <summary>The border of this section.</summary>
    public Rect<float> Border;

    /// <summary>The space between the content box and the border box.</summary>
    public Rect<float> ContentBoxInset;

    /// <summary>The size reserved for scrollbar gutters in each axis.</summary>
    public Point<float> ScrollbarGutter;

    /// <summary>The gap of this section.</summary>
    public Size<float> Gap;

    /// <summary>The align_items property of this node.</summary>
    public AlignItems AlignItems;

    /// <summary>The align_content property of this node.</summary>
    public AlignContent AlignContent;

    /// <summary>The justify_content property of this node.</summary>
    public JustifyContent? JustifyContent;

    /// <summary>The border-box size of the node being laid out (if known).</summary>
    public Size<float?> NodeOuterSize;

    /// <summary>The content-box size of the node being laid out (if known).</summary>
    public Size<float?> NodeInnerSize;

    /// <summary>The size of the virtual container containing the flex items.</summary>
    public Size<float> ContainerSize;

    /// <summary>The size of the internal container.</summary>
    public Size<float> InnerContainerSize;
}

/// <summary>The flexbox layout algorithm.</summary>
public static class FlexboxLayout
{
    private static AlignSelf ResolveFlexNormal(AlignSelf alignment) =>
        alignment.Keyword == AlignItemsKeyword.Normal ? AlignSelf.Stretch : alignment;

    /// <summary>
    /// Transfer a definite border-box size through a preferred aspect ratio while applying the ratio
    /// to the box selected by CSS <c>box-sizing</c> and intrinsic ratio provenance.
    /// </summary>
    private static Size<float?> MaybeApplyPreferredAspectRatio(
        Size<float?> size,
        float? aspectRatio,
        Size<float> adjustment)
    {
        if (aspectRatio is not { } ratio || !float.IsFinite(ratio) || ratio <= 0.0f)
        {
            return size;
        }

        if (size.Width is { } width && !size.Height.HasValue)
        {
            return new Size<float?>(
                width,
                (Sys.F32Max(width - adjustment.Width, 0.0f) / ratio) + adjustment.Height);
        }

        if (!size.Width.HasValue && size.Height is { } height)
        {
            return new Size<float?>(
                (Sys.F32Max(height - adjustment.Height, 0.0f) * ratio) + adjustment.Width,
                height);
        }

        return size;
    }

    /// <summary>Computes the layout of a box according to the flexbox algorithm.</summary>
    public static LayoutOutput ComputeFlexboxLayout(
        ILayoutFlexboxContainer tree,
        NodeId node,
        LayoutInput inputs)
    {
        var calc = tree.CalcResolver();
        var knownDimensions = inputs.KnownDimensions;
        var parentSize = inputs.ParentSize;
        var runMode = inputs.RunMode;
        var style = tree.GetFlexboxContainerStyle(node);

        float? aspectRatio = style.AspectRatio;
        var padding = style.Padding.ResolveOrZero(parentSize.Width, calc);
        var border = style.Border.ResolveOrZero(parentSize.Width, calc);
        var paddingBorderSum = padding.SumAxes().Add(border.SumAxes());
        var boxSizingAdjustment =
            style.BoxSizing == BoxSizing.ContentBox ? paddingBorderSum : GeometryExtensions.SizeZero;

        var minSize = style.MinSize
            .MaybeResolve(parentSize, calc)
            .MaybeApplyAspectRatio(aspectRatio)
            .MaybeAdd(boxSizingAdjustment);
        var maxSize = style.MaxSize
            .MaybeResolve(parentSize, calc)
            .MaybeApplyAspectRatio(aspectRatio)
            .MaybeAdd(boxSizingAdjustment);
        var clampedStyleSize = inputs.SizingMode == SizingMode.InherentSize
            ? style.Size
                .MaybeResolve(parentSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment)
                .MaybeClamp(minSize, maxSize)
            : GeometryExtensions.SizeNone;

        // If both min and max in a given axis are set and max <= min then this determines the size
        var minMaxDefiniteSize = minSize.ZipMap(
            maxSize,
            static (min, max) => min.HasValue && max.HasValue && max.Value <= min.Value ? min : null);

        // The size of the container should be floored by the padding and border
        var styledBasedKnownDimensions = knownDimensions.Or(
            minMaxDefiniteSize.Or(clampedStyleSize).MaybeMax(paddingBorderSum));

        if (runMode == RunMode.ComputeSize)
        {
            if (styledBasedKnownDimensions.Width is { } w && styledBasedKnownDimensions.Height is { } h)
            {
                return LayoutOutput.FromOuterSize(new Size<float>(w, h));
            }

            if (inputs.Axis == RequestedAxis.Horizontal && styledBasedKnownDimensions.Width is { } width)
            {
                return LayoutOutput.FromOuterSize(new Size<float>(width, 0.0f));
            }
        }

        var innerInputs = inputs;
        innerInputs.KnownDimensions = styledBasedKnownDimensions;
        return ComputePreliminary(tree, node, innerInputs);
    }

    private static LayoutOutput ComputePreliminary(
        ILayoutFlexboxContainer tree,
        NodeId node,
        LayoutInput inputs)
    {
        var calc = tree.CalcResolver();
        var knownDimensions = inputs.KnownDimensions;
        var parentSize = inputs.ParentSize;
        var outerAvailableSpace = inputs.AvailableSpace;
        var runMode = inputs.RunMode;

        // Define some general constants we will need for the remainder of the algorithm.
        var constants = ComputeConstants(
            tree, tree.GetFlexboxContainerStyle(node), knownDimensions, parentSize);

        // 9.1. Initial Setup - 1. Generate anonymous flex items
        var flexItems = GenerateAnonymousFlexItems(tree, node, constants);

        // 9.2. Line Length Determination - 2. Determine the available main and cross space
        var availableSpace = DetermineAvailableSpace(knownDimensions, outerAvailableSpace, constants);

        // 3. Determine the flex base size and hypothetical main size of each item
        DetermineFlexBaseSize(tree, constants, availableSpace, flexItems);

        // 9.3. Main Size Determination - 5. Collect flex items into flex lines
        var flexLines = CollectFlexLines(constants, availableSpace, flexItems);

        // If container size is undefined, determine the container's main size and re-resolve gaps
        if (constants.NodeInnerSize.Main(constants.Dir) is { } innerMainSize)
        {
            float outerMainSize = innerMainSize + constants.ContentBoxInset.MainAxisSum(constants.Dir);
            constants.InnerContainerSize.SetMain(constants.Dir, innerMainSize);
            constants.ContainerSize.SetMain(constants.Dir, outerMainSize);
        }
        else
        {
            DetermineContainerMainSize(tree, availableSpace, flexLines, constants);
            constants.NodeInnerSize.SetMain(constants.Dir, constants.InnerContainerSize.Main(constants.Dir));
            constants.NodeOuterSize.SetMain(constants.Dir, constants.ContainerSize.Main(constants.Dir));

            // Re-resolve percentage gaps
            var style = tree.GetFlexboxContainerStyle(node);
            float innerContainerSize = constants.InnerContainerSize.Main(constants.Dir);
            float newGap = style.Gap.Main(constants.Dir).MaybeResolve(innerContainerSize, calc) ?? 0.0f;
            constants.Gap.SetMain(constants.Dir, newGap);
        }

        // 6. Resolve the flexible lengths of all the flex items
        for (int i = 0; i < flexLines.Count; i++)
        {
            ResolveFlexibleLengths(flexLines[i], constants);
        }

        // 9.4. Cross Size Determination - 7. Determine the hypothetical cross size of each item
        for (int i = 0; i < flexLines.Count; i++)
        {
            DetermineHypotheticalCrossSize(tree, flexLines[i], constants, availableSpace);
        }

        // Calculate child baselines (internally smart: only computes them if necessary)
        CalculateChildrenBaseLines(tree, knownDimensions, availableSpace, flexLines, constants);

        // 8. Calculate the cross size of each flex line
        CalculateCrossSize(flexLines, knownDimensions, constants);

        // 9. Handle 'align-content: stretch'
        HandleAlignContentStretch(flexLines, knownDimensions, constants);

        // 10. Collapse visibility:collapse items - not implemented (taffy does not support it either)

        // 11. Determine the used cross size of each flex item
        DetermineUsedCrossSize(tree, flexLines, constants);

        // 9.5. Main-Axis Alignment - 12. Distribute any remaining free space
        DistributeRemainingFreeSpace(flexLines, constants);

        // 9.6. Cross-Axis Alignment - 13/14. Resolve cross-axis auto margins
        ResolveCrossAxisAutoMargins(flexLines, constants);

        // 15. Determine the flex container's used cross size
        float totalLineCrossSize = DetermineContainerCrossSize(flexLines, knownDimensions, constants);

        // If our caller does not care about performing layout we are done now.
        if (runMode == RunMode.ComputeSize)
        {
            return LayoutOutput.FromOuterSize(constants.ContainerSize);
        }

        // 16. Align all flex lines per align-content
        AlignFlexLinesPerAlignContent(flexLines, constants, totalLineCrossSize);

        // Do a final layout pass and gather the resulting layouts
        var inflowContentSize = FinalLayoutPass(tree, flexLines, constants);

        // Perform absolute layout on all absolutely positioned children
        var absoluteContentSize = PerformAbsoluteLayoutOnAbsoluteChildren(tree, node, constants);

        int len = tree.ChildCount(node);
        for (int order = 0; order < len; order++)
        {
            var child = tree.GetChildId(node, order);
            if (tree.GetFlexboxChildStyle(child).BoxGenerationMode == BoxGenerationMode.None)
            {
                var hiddenLayout = Layout.WithOrder((uint)order);
                tree.SetUnroundedLayout(child, in hiddenLayout);
                tree.PerformChildLayout(
                    child,
                    GeometryExtensions.SizeNone,
                    GeometryExtensions.SizeNone,
                    GeometryExtensions.SizeMaxContent,
                    SizingMode.InherentSize,
                    GeometryExtensions.LineFalse);
            }
        }

        // 8.5. Flex Container Baselines: calculate the flex container's first baseline
        float? firstVerticalBaseline = null;
        if (flexLines.Count > 0)
        {
            var items = flexLines[0].Items;
            FlexItem? chosen = null;
            for (int i = 0; i < items.Length; i++)
            {
                if (constants.IsColumn || items[i].AlignSelf == AlignSelf.Baseline)
                {
                    chosen = items[i];
                    break;
                }
            }

            chosen ??= items.Length > 0 ? items[0] : null;
            if (chosen is not null)
            {
                float offsetVertical = constants.IsRow ? chosen.OffsetCross : chosen.OffsetMain;
                firstVerticalBaseline = offsetVertical + chosen.Baseline;
            }
        }

        return LayoutOutput.FromSizesAndBaselines(
            constants.ContainerSize,
            inflowContentSize.F32Max(absoluteContentSize),
            new Point<float?>(null, firstVerticalBaseline));
    }

    /// <summary>Compute constants that can be reused during the flexbox algorithm.</summary>
    private static AlgoConstants ComputeConstants(
        ILayoutFlexboxContainer tree,
        IFlexboxContainerStyle style,
        Size<float?> knownDimensions,
        Size<float?> parentSize)
    {
        var calc = tree.CalcResolver();
        var dir = style.FlexDirection;
        bool isRow = dir.IsRow();
        bool isColumn = dir.IsColumn();
        bool isWrap = style.FlexWrap is FlexWrap.Wrap or FlexWrap.WrapReverse;
        bool isWrapReverse = style.FlexWrap == FlexWrap.WrapReverse;

        float? aspectRatio = style.AspectRatio;
        var margin = style.Margin.ResolveOrZero(parentSize.Width, calc);
        var padding = style.Padding.ResolveOrZero(parentSize.Width, calc);
        var border = style.Border.ResolveOrZero(parentSize.Width, calc);
        var paddingBorderSum = padding.SumAxes().Add(border.SumAxes());
        var boxSizingAdjustment =
            style.BoxSizing == BoxSizing.ContentBox ? paddingBorderSum : GeometryExtensions.SizeZero;

        var alignItems = ResolveFlexNormal(style.AlignItems ?? AlignItems.Stretch);
        var alignContent = style.AlignContent ?? AlignContent.Stretch;
        var justifyContent = style.JustifyContent;
        var layoutDirection = style.Direction;

        // Scrollbar gutters are reserved when overflow is Scroll; axes are transposed.
        var scrollbarGutter = style.Overflow.Transpose().Map(
            o => o == Overflow.Scroll ? style.ScrollbarWidth : 0.0f);
        var contentBoxInset = padding.Add(border);
        contentBoxInset.Bottom += scrollbarGutter.Y;
        if (layoutDirection == Direction.Ltr)
        {
            contentBoxInset.Right += scrollbarGutter.X;
        }
        else
        {
            contentBoxInset.Left += scrollbarGutter.X;
        }

        var nodeOuterSize = knownDimensions;
        var nodeInnerSize = nodeOuterSize.MaybeSub(contentBoxInset.SumAxes());
        var gap = style.Gap.ResolveOrZero(nodeInnerSize.Or(Resolve.SizeOptionZero), calc);

        return new AlgoConstants
        {
            Dir = dir,
            LayoutDirection = layoutDirection,
            IsRow = isRow,
            IsColumn = isColumn,
            IsWrap = isWrap,
            IsWrapReverse = isWrapReverse,
            MinSize = style.MinSize
                .MaybeResolve(parentSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment),
            MaxSize = style.MaxSize
                .MaybeResolve(parentSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment),
            Margin = margin,
            Border = border,
            Gap = gap,
            ContentBoxInset = contentBoxInset,
            ScrollbarGutter = scrollbarGutter,
            AlignItems = alignItems,
            AlignContent = alignContent,
            JustifyContent = justifyContent,
            NodeOuterSize = nodeOuterSize,
            NodeInnerSize = nodeInnerSize,
            ContainerSize = GeometryExtensions.SizeZero,
            InnerContainerSize = GeometryExtensions.SizeZero,
        };
    }

    /// <summary>Generate anonymous flex items.</summary>
    private static List<FlexItem> GenerateAnonymousFlexItems(
        ILayoutFlexboxContainer tree,
        NodeId node,
        AlgoConstants constants)
    {
        var calc = tree.CalcResolver();
        var items = new List<FlexItem>();
        int index = 0;

        foreach (var child in tree.ChildIds(node))
        {
            var childStyle = tree.GetFlexboxChildStyle(child);
            int thisIndex = index;
            index += 1;

            if (childStyle.Position == Position.Absolute)
            {
                continue;
            }

            if (childStyle.BoxGenerationMode == BoxGenerationMode.None)
            {
                continue;
            }

            float? aspectRatio = childStyle.AspectRatio;
            var rawSize = childStyle.Size;
            var padding = childStyle.Padding.ResolveOrZero(constants.NodeInnerSize.Width, calc);
            var border = childStyle.Border.ResolveOrZero(constants.NodeInnerSize.Width, calc);
            var pbSum = padding.Add(border).SumAxes();
            var boxSizingAdjustment =
                childStyle.BoxSizing == BoxSizing.ContentBox ? pbSum : GeometryExtensions.SizeZero;
            var aspectRatioAdjustment =
                childStyle.AspectRatioUsesContentBox ? pbSum : boxSizingAdjustment;

            var size = MaybeApplyPreferredAspectRatio(
                rawSize.MaybeResolve(constants.NodeInnerSize, calc).MaybeAdd(boxSizingAdjustment),
                aspectRatio,
                aspectRatioAdjustment);
            var minSize = MaybeApplyPreferredAspectRatio(
                childStyle.MinSize.MaybeResolve(constants.NodeInnerSize, calc).MaybeAdd(boxSizingAdjustment),
                aspectRatio,
                aspectRatioAdjustment);
            var maxSize = MaybeApplyPreferredAspectRatio(
                childStyle.MaxSize.MaybeResolve(constants.NodeInnerSize, calc).MaybeAdd(boxSizingAdjustment),
                aspectRatio,
                aspectRatioAdjustment);

            items.Add(new FlexItem
            {
                Node = child,
                Order = (uint)thisIndex,
                Size = size,
                MinSize = minSize,
                MaxSize = maxSize,
                AspectRatio = aspectRatio,
                AspectRatioAdjustment = aspectRatioAdjustment,
                CrossSizeIsAuto = rawSize.Cross(constants.Dir).IsAuto,

                Inset = childStyle.Inset.ZipSize(
                    constants.NodeInnerSize, (p, s) => p.MaybeResolve(s, calc)),
                Margin = childStyle.Margin.ResolveOrZero(constants.NodeInnerSize.Width, calc),
                MarginIsAuto = childStyle.Margin.Map(static m => m.IsAuto),
                Padding = padding,
                Border = border,
                AlignSelf = ResolveFlexNormal(childStyle.AlignSelf ?? constants.AlignItems),
                Overflow = childStyle.Overflow,
                ScrollbarWidth = childStyle.ScrollbarWidth,
                FlexGrow = childStyle.FlexGrow,
                FlexShrink = childStyle.FlexShrink,
                FlexBasis = 0.0f,
                InnerFlexBasis = 0.0f,
                Violation = 0.0f,
                Frozen = false,

                ResolvedMinimumMainSize = 0.0f,
                HypotheticalInnerSize = GeometryExtensions.SizeZero,
                HypotheticalOuterSize = GeometryExtensions.SizeZero,
                TargetSize = GeometryExtensions.SizeZero,
                OuterTargetSize = GeometryExtensions.SizeZero,
                ContentFlexFraction = 0.0f,

                Baseline = 0.0f,

                OffsetMain = 0.0f,
                OffsetCross = 0.0f,
            });
        }

        return items;
    }

    /// <summary>Determine the available main and cross space for the flex items.</summary>
    private static Size<AvailableSpace> DetermineAvailableSpace(
        Size<float?> knownDimensions,
        Size<AvailableSpace> outerAvailableSpace,
        AlgoConstants constants)
    {
        var width = knownDimensions.Width is { } nodeWidth
            ? AvailableSpace.Definite(nodeWidth - constants.ContentBoxInset.HorizontalAxisSum())
            : outerAvailableSpace.Width
                .MaybeSub(constants.Margin.HorizontalAxisSum())
                .MaybeSub(constants.ContentBoxInset.HorizontalAxisSum());

        var height = knownDimensions.Height is { } nodeHeight
            ? AvailableSpace.Definite(nodeHeight - constants.ContentBoxInset.VerticalAxisSum())
            : outerAvailableSpace.Height
                .MaybeSub(constants.Margin.VerticalAxisSum())
                .MaybeSub(constants.ContentBoxInset.VerticalAxisSum());

        return new Size<AvailableSpace>(width, height);
    }

    /// <summary>Determine the flex base size and hypothetical main size of each item.</summary>
    private static void DetermineFlexBaseSize(
        ILayoutFlexboxContainer tree,
        AlgoConstants constants,
        Size<AvailableSpace> availableSpace,
        List<FlexItem> flexItems)
    {
        var calc = tree.CalcResolver();
        var dir = constants.Dir;

        for (int i = 0; i < flexItems.Count; i++)
        {
            var child = flexItems[i];
            var childStyle = tree.GetFlexboxChildStyle(child.Node);

            // Parent size for child sizing
            float? crossAxisParentSize = constants.NodeInnerSize.Cross(dir);
            var childParentSize = GeometryExtensions.SizeFromCross(dir, crossAxisParentSize);

            // Available space for child sizing
            float crossAxisMarginSum = constants.Margin.CrossAxisSum(dir);
            float? childMinCross = child.MinSize.Cross(dir).MaybeAdd(crossAxisMarginSum);
            float? childMaxCross = child.MaxSize.Cross(dir).MaybeAdd(crossAxisMarginSum);

            // Clamp available space by min- and max- size
            AvailableSpace crossAxisAvailableSpace;
            var availableCross = availableSpace.Cross(dir);
            switch (availableCross.Kind)
            {
                case AvailableSpaceKind.Definite:
                    crossAxisAvailableSpace = AvailableSpace.Definite(
                        (crossAxisParentSize ?? availableCross.Unwrap())
                            .MaybeClamp(childMinCross, childMaxCross));
                    break;
                case AvailableSpaceKind.MinContent:
                    crossAxisAvailableSpace = childMinCross.HasValue
                        ? AvailableSpace.Definite(childMinCross.Value)
                        : AvailableSpace.MinContent;
                    break;
                default:
                    crossAxisAvailableSpace = childMaxCross.HasValue
                        ? AvailableSpace.Definite(childMaxCross.Value)
                        : AvailableSpace.MaxContent;
                    break;
            }

            // Known dimensions for child sizing
            var childKnownDimensions = child.Size.WithMain(dir, null);
            if (child.AlignSelf == AlignSelf.Stretch
                && !child.MarginIsAuto.CrossStart(constants.Dir)
                && !child.MarginIsAuto.CrossEnd(constants.Dir)
                && !childKnownDimensions.Cross(dir).HasValue)
            {
                childKnownDimensions.SetCross(
                    dir,
                    crossAxisAvailableSpace.IntoOption().MaybeSub(child.Margin.CrossAxisSum(dir)));
            }

            float? containerWidth = constants.NodeInnerSize.Main(dir);
            float boxSizingAdjustment = (childStyle.BoxSizing == BoxSizing.ContentBox
                ? childStyle.Padding.ResolveOrZero(containerWidth, calc)
                    .Add(childStyle.Border.ResolveOrZero(containerWidth, calc))
                    .SumAxes()
                : GeometryExtensions.SizeZero).Main(dir);
            float? flexBasis = childStyle.FlexBasis
                .MaybeResolve(containerWidth, calc)
                .MaybeAdd(boxSizingAdjustment);

            // A / B: a definite used flex basis, or a ratio-derived main size
            float? mainSize = child.Size.Main(dir);
            float? definiteBasis = flexBasis ?? mainSize;
            if (definiteBasis.HasValue)
            {
                child.FlexBasis = definiteBasis.Value;
            }
            else
            {
                // E. Size the item into the available space using its used flex basis in place of
                //    its main size, treating a value of content as max-content.
                var childAvailableSpace = GeometryExtensions.SizeMaxContent
                    .WithMain(
                        dir,
                        availableSpace.Main(dir).Kind == AvailableSpaceKind.MinContent
                            ? AvailableSpace.MinContent
                            : AvailableSpace.MaxContent)
                    .WithCross(dir, crossAxisAvailableSpace);

                child.FlexBasis = tree.MeasureChildSize(
                    child.Node,
                    childKnownDimensions,
                    childParentSize,
                    childAvailableSpace,
                    SizingMode.ContentSize,
                    dir.MainAxis(),
                    GeometryExtensions.LineFalse);
            }

            // Floor flex-basis by the padding_border_sum (floors inner_flex_basis at zero).
            // This matches Chrome and Firefox even though it violates the spec.
            float paddingBorderSum =
                child.Padding.MainAxisSum(constants.Dir) + child.Border.MainAxisSum(constants.Dir);
            child.FlexBasis = Sys.F32Max(child.FlexBasis, paddingBorderSum);

            child.InnerFlexBasis = child.FlexBasis
                - child.Padding.MainAxisSum(constants.Dir)
                - child.Border.MainAxisSum(constants.Dir);

            var paddingBorderAxesSums = child.Padding.Add(child.Border).SumAxes().AsOptions();

            // Note: the `parent_size` parameter in the main axis is intentionally not set here as a
            // percentage size in an axis should not contribute to a min-content contribution in
            // that same axis. See https://drafts.csswg.org/css-sizing-3/#min-percentage-contribution
            float? styleMinMainSize = child.MinSize
                .Or(child.Overflow.Map(static o => o.MaybeIntoAutomaticMinSize()).ToSize())
                .Main(dir);

            if (styleMinMainSize.HasValue)
            {
                child.ResolvedMinimumMainSize = styleMinMainSize.Value;
            }
            else
            {
                var childAvailableSpace =
                    GeometryExtensions.SizeMinContent.WithCross(dir, crossAxisAvailableSpace);

                float minContentMainSize = tree.MeasureChildSize(
                    child.Node,
                    childKnownDimensions,
                    childParentSize,
                    childAvailableSpace,
                    SizingMode.ContentSize,
                    dir.MainAxis(),
                    GeometryExtensions.LineFalse);

                // 4.5. Automatic Minimum Size of Flex Items
                float clampedMinContentSize = minContentMainSize
                    .MaybeMin(child.Size.Main(dir))
                    .MaybeMin(child.MaxSize.Main(dir));
                child.ResolvedMinimumMainSize =
                    clampedMinContentSize.MaybeMax(paddingBorderAxesSums.Main(dir));
            }

            float hypotheticalInnerMinMain =
                child.ResolvedMinimumMainSize.MaybeMax(paddingBorderAxesSums.Main(constants.Dir));
            float hypotheticalInnerSize = child.FlexBasis
                .MaybeClamp(hypotheticalInnerMinMain, child.MaxSize.Main(constants.Dir));
            float hypotheticalOuterSize = hypotheticalInnerSize + child.Margin.MainAxisSum(constants.Dir);

            child.HypotheticalInnerSize.SetMain(constants.Dir, hypotheticalInnerSize);
            child.HypotheticalOuterSize.SetMain(constants.Dir, hypotheticalOuterSize);
        }
    }

    /// <summary>Collect flex items into flex lines.</summary>
    private static List<FlexLine> CollectFlexLines(
        AlgoConstants constants,
        Size<AvailableSpace> availableSpace,
        List<FlexItem> flexItems)
    {
        var lines = new List<FlexLine>();

        if (!constants.IsWrap)
        {
            lines.Add(new FlexLine { Items = [.. flexItems], CrossSize = 0.0f, OffsetCross = 0.0f });
            return lines;
        }

        AvailableSpace mainAxisAvailableSpace;
        if (constants.MaxSize.Main(constants.Dir) is { } maxSize)
        {
            mainAxisAvailableSpace = AvailableSpace.Definite(
                (availableSpace.Main(constants.Dir).IntoOption() ?? maxSize)
                    .MaybeMax(constants.MinSize.Main(constants.Dir)));
        }
        else
        {
            mainAxisAvailableSpace = availableSpace.Main(constants.Dir);
        }

        switch (mainAxisAvailableSpace.Kind)
        {
            // Sizing under a max-content constraint: the flex items never wrap
            case AvailableSpaceKind.MaxContent:
                lines.Add(new FlexLine { Items = [.. flexItems], CrossSize = 0.0f, OffsetCross = 0.0f });
                return lines;

            // Sizing under a min-content constraint: take every wrapping opportunity
            case AvailableSpaceKind.MinContent:
                for (int i = 0; i < flexItems.Count; i++)
                {
                    lines.Add(new FlexLine { Items = [flexItems[i]], CrossSize = 0.0f, OffsetCross = 0.0f });
                }

                return lines;

            default:
            {
                float mainAxisAvailable = mainAxisAvailableSpace.Unwrap();
                float mainAxisGap = constants.Gap.Main(constants.Dir);
                int start = 0;
                while (start < flexItems.Count)
                {
                    // Find index of the first item in the next line
                    float lineLength = 0.0f;
                    int index = flexItems.Count - start;
                    for (int idx = 0; start + idx < flexItems.Count; idx++)
                    {
                        float gapContribution = idx == 0 ? 0.0f : mainAxisGap;
                        lineLength += flexItems[start + idx].HypotheticalOuterSize.Main(constants.Dir)
                            + gapContribution;
                        if (lineLength > mainAxisAvailable && idx != 0)
                        {
                            index = idx;
                            break;
                        }
                    }

                    var lineItems = new FlexItem[index];
                    flexItems.CopyTo(start, lineItems, 0, index);
                    lines.Add(new FlexLine { Items = lineItems, CrossSize = 0.0f, OffsetCross = 0.0f });
                    start += index;
                }

                return lines;
            }
        }
    }

    private static float LineTargetSizeSum(FlexLine line, AlgoConstants constants)
    {
        float total = 0.0f;
        for (int i = 0; i < line.Items.Length; i++)
        {
            var child = line.Items[i];
            float paddingBorderSum = child.Padding.Add(child.Border).MainAxisSum(constants.Dir);
            total += Sys.F32Max(
                child.FlexBasis.MaybeMax(child.MinSize.Main(constants.Dir))
                    + child.Margin.MainAxisSum(constants.Dir),
                paddingBorderSum);
        }

        return total;
    }

    private static float LongestLineLength(List<FlexLine> lines, AlgoConstants constants)
    {
        bool any = false;
        float best = 0.0f;
        for (int i = 0; i < lines.Count; i++)
        {
            float lineMainAxisGap = SumAxisGaps(constants.Gap.Main(constants.Dir), lines[i].Items.Length);
            float value = LineTargetSizeSum(lines[i], constants) + lineMainAxisGap;
            if (!any || Sys.TotalCmp(value, best) >= 0)
            {
                best = value;
                any = true;
            }
        }

        return any ? best : 0.0f;
    }

    /// <summary>Determine the container's main size (if not already known).</summary>
    private static void DetermineContainerMainSize(
        ILayoutFlexboxContainer tree,
        Size<AvailableSpace> availableSpace,
        List<FlexLine> lines,
        AlgoConstants constants)
    {
        var dir = constants.Dir;
        float mainContentBoxInset = constants.ContentBoxInset.MainAxisSum(constants.Dir);

        float outerMainSize;
        if (constants.NodeOuterSize.Main(constants.Dir) is { } knownOuterMainSize)
        {
            outerMainSize = knownOuterMainSize;
        }
        else
        {
            var mainAvailable = availableSpace.Main(dir);
            if (mainAvailable.Kind == AvailableSpaceKind.Definite)
            {
                float mainAxisAvailableSpace = mainAvailable.Unwrap();
                float size = LongestLineLength(lines, constants) + mainContentBoxInset;
                outerMainSize = lines.Count > 1 ? Sys.F32Max(size, mainAxisAvailableSpace) : size;
            }
            else if (mainAvailable.Kind == AvailableSpaceKind.MinContent && constants.IsWrap)
            {
                outerMainSize = LongestLineLength(lines, constants) + mainContentBoxInset;
            }
            else
            {
                // The flex container's max-content size is the largest sum of the item sizes within
                // a single line.
                float mainSize = 0.0f;

                for (int lineIdx = 0; lineIdx < lines.Count; lineIdx++)
                {
                    var line = lines[lineIdx];
                    for (int itemIdx = 0; itemIdx < line.Items.Length; itemIdx++)
                    {
                        var item = line.Items[itemIdx];
                        float? styleMin = item.MinSize.Main(constants.Dir);
                        float? stylePreferred = item.Size.Main(constants.Dir);
                        float? styleMax = item.MaxSize.Main(constants.Dir);

                        // Matches Chrome and Firefox; see the spec discussion in the Rust source.
                        float? clampingBasis = ((float?)item.FlexBasis).MaybeMax(stylePreferred);
                        float? flexBasisMin = item.FlexShrink == 0.0f ? clampingBasis : null;
                        float? flexBasisMax = item.FlexGrow == 0.0f ? clampingBasis : null;

                        float minMainSize = Sys.F32Max(
                            styleMin.MaybeMax(flexBasisMin) ?? flexBasisMin ?? item.ResolvedMinimumMainSize,
                            item.ResolvedMinimumMainSize);
                        float maxMainSize =
                            styleMax.MaybeMin(flexBasisMax) ?? flexBasisMax ?? float.PositiveInfinity;

                        float contentContribution;
                        if (stylePreferred is { } pref && (maxMainSize <= minMainSize || maxMainSize <= pref))
                        {
                            contentContribution = Sys.F32Max(Sys.F32Min(pref, maxMainSize), minMainSize)
                                + item.Margin.MainAxisSum(constants.Dir);
                        }
                        else if (maxMainSize <= minMainSize)
                        {
                            contentContribution = minMainSize + item.Margin.MainAxisSum(constants.Dir);
                        }
                        else if (item.IsScrollContainer())
                        {
                            contentContribution = item.FlexBasis + item.Margin.MainAxisSum(constants.Dir);
                        }
                        else
                        {
                            // Parent size for child sizing
                            float? crossAxisParentSize = constants.NodeInnerSize.Cross(dir);

                            // Available space for child sizing
                            float crossAxisMarginSum = constants.Margin.CrossAxisSum(dir);
                            float? childMinCross = item.MinSize.Cross(dir).MaybeAdd(crossAxisMarginSum);
                            float? childMaxCross = item.MaxSize.Cross(dir).MaybeAdd(crossAxisMarginSum);
                            var crossAxisAvailableSpace = availableSpace
                                .Cross(dir)
                                .MapDefiniteValue(val => crossAxisParentSize ?? val)
                                .MaybeClamp(childMinCross, childMaxCross);

                            var childAvailableSpace = availableSpace.WithCross(dir, crossAxisAvailableSpace);

                            // Known dimensions for child sizing
                            var childKnownDimensions = item.Size.WithMain(dir, null);
                            if (item.AlignSelf == AlignSelf.Stretch
                                && !childKnownDimensions.Cross(dir).HasValue)
                            {
                                childKnownDimensions.SetCross(
                                    dir,
                                    crossAxisAvailableSpace.IntoOption()
                                        .MaybeSub(item.Margin.CrossAxisSum(dir)));
                            }

                            float contentMainSize = tree.MeasureChildSize(
                                item.Node,
                                childKnownDimensions,
                                constants.NodeInnerSize,
                                childAvailableSpace,
                                SizingMode.InherentSize,
                                dir.MainAxis(),
                                GeometryExtensions.LineFalse) + item.Margin.MainAxisSum(constants.Dir);

                            // Asymmetrical between rows and columns; found by matching
                            // Webkit/Firefox output rather than by reading the spec.
                            contentContribution = constants.IsRow
                                ? Sys.F32Max(
                                    contentMainSize.MaybeClamp(styleMin, styleMax), mainContentBoxInset)
                                : Sys.F32Max(
                                    Sys.F32Max(contentMainSize, item.FlexBasis).MaybeClamp(styleMin, styleMax),
                                    mainContentBoxInset);
                        }

                        float diff = contentContribution - item.FlexBasis;
                        if (diff > 0.0f)
                        {
                            item.ContentFlexFraction = diff / Sys.F32Max(1.0f, item.FlexGrow);
                        }
                        else if (diff < 0.0f)
                        {
                            float scaledShrinkFactor = Sys.F32Max(1.0f, item.FlexShrink * item.InnerFlexBasis);
                            item.ContentFlexFraction = diff / scaledShrinkFactor;
                        }
                        else
                        {
                            item.ContentFlexFraction = 0.0f;
                        }
                    }

                    // Add each item's flex base size to the product of its flex factor and the
                    // chosen max-content flex fraction.
                    float itemMainSizeSum = 0.0f;
                    for (int itemIdx = 0; itemIdx < line.Items.Length; itemIdx++)
                    {
                        var item = line.Items[itemIdx];
                        float flexFraction = item.ContentFlexFraction;

                        float flexContribution;
                        if (item.ContentFlexFraction > 0.0f)
                        {
                            flexContribution = Sys.F32Max(1.0f, item.FlexGrow) * flexFraction;
                        }
                        else if (item.ContentFlexFraction < 0.0f)
                        {
                            float scaledShrinkFactor = Sys.F32Max(1.0f, item.FlexShrink) * item.InnerFlexBasis;
                            flexContribution = scaledShrinkFactor * flexFraction;
                        }
                        else
                        {
                            flexContribution = 0.0f;
                        }

                        float size = item.FlexBasis + flexContribution;
                        item.OuterTargetSize.SetMain(constants.Dir, size);
                        item.TargetSize.SetMain(constants.Dir, size);
                        itemMainSizeSum += size;
                    }

                    float gapSum = SumAxisGaps(constants.Gap.Main(constants.Dir), line.Items.Length);
                    mainSize = Sys.F32Max(mainSize, itemMainSizeSum + gapSum);
                }

                outerMainSize = mainSize + mainContentBoxInset;
            }
        }

        outerMainSize = Sys.F32Max(
            outerMainSize.MaybeClamp(
                constants.MinSize.Main(constants.Dir), constants.MaxSize.Main(constants.Dir)),
            mainContentBoxInset - constants.ScrollbarGutter.Main(constants.Dir));

        float innerMainSize = Sys.F32Max(outerMainSize - mainContentBoxInset, 0.0f);
        constants.ContainerSize.SetMain(constants.Dir, outerMainSize);
        constants.InnerContainerSize.SetMain(constants.Dir, innerMainSize);
        constants.NodeInnerSize.SetMain(constants.Dir, innerMainSize);
    }

    /// <summary>Resolve the flexible lengths of the items within a flex line.</summary>
    private static void ResolveFlexibleLengths(FlexLine line, AlgoConstants constants)
    {
        float totalMainAxisGap = SumAxisGaps(constants.Gap.Main(constants.Dir), line.Items.Length);

        // 1. Determine the used flex factor.
        float totalHypotheticalOuterMainSize = 0.0f;
        for (int i = 0; i < line.Items.Length; i++)
        {
            totalHypotheticalOuterMainSize += line.Items[i].HypotheticalOuterSize.Main(constants.Dir);
        }

        float usedFlexFactor = totalMainAxisGap + totalHypotheticalOuterMainSize;
        float innerMain = constants.NodeInnerSize.Main(constants.Dir) ?? 0.0f;
        bool growing = usedFlexFactor < innerMain;
        bool shrinking = usedFlexFactor > innerMain;
        bool exactlySized = !growing && !shrinking;

        // 2. Size inflexible items
        for (int i = 0; i < line.Items.Length; i++)
        {
            var child = line.Items[i];
            float innerTargetSize = child.HypotheticalInnerSize.Main(constants.Dir);
            child.TargetSize.SetMain(constants.Dir, innerTargetSize);

            if (exactlySized
                || (child.FlexGrow == 0.0f && child.FlexShrink == 0.0f)
                || (growing && child.FlexBasis > child.HypotheticalInnerSize.Main(constants.Dir))
                || (shrinking && child.FlexBasis < child.HypotheticalInnerSize.Main(constants.Dir)))
            {
                child.Frozen = true;
                float outerTargetSize = innerTargetSize + child.Margin.MainAxisSum(constants.Dir);
                child.OuterTargetSize.SetMain(constants.Dir, outerTargetSize);
            }
        }

        if (exactlySized)
        {
            return;
        }

        // 3. Calculate initial free space
        float initialUsedSpace = totalMainAxisGap;
        for (int i = 0; i < line.Items.Length; i++)
        {
            var child = line.Items[i];
            initialUsedSpace += child.Frozen
                ? child.OuterTargetSize.Main(constants.Dir)
                : child.FlexBasis + child.Margin.MainAxisSum(constants.Dir);
        }

        float initialFreeSpace =
            constants.NodeInnerSize.Main(constants.Dir).MaybeSub(initialUsedSpace) ?? 0.0f;

        var unfrozen = new List<FlexItem>();

        // 4. Loop
        while (true)
        {
            // a. Check for flexible items
            bool allFrozen = true;
            for (int i = 0; i < line.Items.Length; i++)
            {
                if (!line.Items[i].Frozen)
                {
                    allFrozen = false;
                    break;
                }
            }

            if (allFrozen)
            {
                break;
            }

            // b. Calculate the remaining free space
            float usedSpace = totalMainAxisGap;
            for (int i = 0; i < line.Items.Length; i++)
            {
                var child = line.Items[i];
                usedSpace += child.Frozen
                    ? child.OuterTargetSize.Main(constants.Dir)
                    : child.FlexBasis + child.Margin.MainAxisSum(constants.Dir);
            }

            unfrozen.Clear();
            for (int i = 0; i < line.Items.Length; i++)
            {
                if (!line.Items[i].Frozen)
                {
                    unfrozen.Add(line.Items[i]);
                }
            }

            float sumFlexGrow = 0.0f;
            float sumFlexShrink = 0.0f;
            for (int i = 0; i < unfrozen.Count; i++)
            {
                sumFlexGrow += unfrozen[i].FlexGrow;
                sumFlexShrink += unfrozen[i].FlexShrink;
            }

            float freeSpace;
            if (growing && sumFlexGrow < 1.0f)
            {
                freeSpace = ((initialFreeSpace * sumFlexGrow) - totalMainAxisGap)
                    .MaybeMin(constants.NodeInnerSize.Main(constants.Dir).MaybeSub(usedSpace));
            }
            else if (shrinking && sumFlexShrink < 1.0f)
            {
                freeSpace = ((initialFreeSpace * sumFlexShrink) - totalMainAxisGap)
                    .MaybeMax(constants.NodeInnerSize.Main(constants.Dir).MaybeSub(usedSpace));
            }
            else
            {
                freeSpace = constants.NodeInnerSize.Main(constants.Dir).MaybeSub(usedSpace)
                    ?? (usedFlexFactor - usedSpace);
            }

            // c. Distribute free space proportional to the flex factors
            if (float.IsNormal(freeSpace))
            {
                if (growing && sumFlexGrow > 0.0f)
                {
                    for (int i = 0; i < unfrozen.Count; i++)
                    {
                        var child = unfrozen[i];
                        child.TargetSize.SetMain(
                            constants.Dir,
                            child.FlexBasis + (freeSpace * (child.FlexGrow / sumFlexGrow)));
                    }
                }
                else if (shrinking && sumFlexShrink > 0.0f)
                {
                    float sumScaledShrinkFactor = 0.0f;
                    for (int i = 0; i < unfrozen.Count; i++)
                    {
                        sumScaledShrinkFactor += unfrozen[i].InnerFlexBasis * unfrozen[i].FlexShrink;
                    }

                    if (sumScaledShrinkFactor > 0.0f)
                    {
                        for (int i = 0; i < unfrozen.Count; i++)
                        {
                            var child = unfrozen[i];
                            float scaledShrinkFactor = child.InnerFlexBasis * child.FlexShrink;
                            child.TargetSize.SetMain(
                                constants.Dir,
                                child.FlexBasis + (freeSpace * (scaledShrinkFactor / sumScaledShrinkFactor)));
                        }
                    }
                }
            }

            // d. Fix min/max violations
            float totalViolation = 0.0f;
            for (int i = 0; i < unfrozen.Count; i++)
            {
                var child = unfrozen[i];
                float? resolvedMinMain = child.ResolvedMinimumMainSize;
                float? maxMain = child.MaxSize.Main(constants.Dir);
                float clamped = Sys.F32Max(
                    child.TargetSize.Main(constants.Dir).MaybeClamp(resolvedMinMain, maxMain), 0.0f);
                child.Violation = clamped - child.TargetSize.Main(constants.Dir);
                child.TargetSize.SetMain(constants.Dir, clamped);
                child.OuterTargetSize.SetMain(
                    constants.Dir,
                    child.TargetSize.Main(constants.Dir) + child.Margin.MainAxisSum(constants.Dir));

                totalViolation += child.Violation;
            }

            // e. Freeze over-flexed items
            for (int i = 0; i < unfrozen.Count; i++)
            {
                var child = unfrozen[i];
                if (totalViolation > 0.0f)
                {
                    child.Frozen = child.Violation > 0.0f;
                }
                else if (totalViolation < 0.0f)
                {
                    child.Frozen = child.Violation < 0.0f;
                }
                else
                {
                    child.Frozen = true;
                }
            }
        }
    }

    /// <summary>Determine the hypothetical cross size of each item.</summary>
    private static void DetermineHypotheticalCrossSize(
        ILayoutFlexboxContainer tree,
        FlexLine line,
        AlgoConstants constants,
        Size<AvailableSpace> availableSpace)
    {
        for (int i = 0; i < line.Items.Length; i++)
        {
            var child = line.Items[i];
            float paddingBorderSum = child.Padding.Add(child.Border).CrossAxisSum(constants.Dir);

            var childKnownMain = AvailableSpace.Definite(constants.ContainerSize.Main(constants.Dir));

            // Transfer the final target main size through the preferred aspect ratio; the
            // provisional value computed while generating items is stale after flexible-length
            // resolution changed the main size.
            float? ratioCross = null;
            if (child.CrossSizeIsAuto && child.AspectRatio is { } ratio
                && float.IsFinite(ratio) && ratio > 0.0f)
            {
                float ratioMain = Sys.F32Max(
                    child.TargetSize.Main(constants.Dir)
                        - child.AspectRatioAdjustment.Main(constants.Dir),
                    0.0f);
                float crossFromRatio = constants.IsRow ? ratioMain / ratio : ratioMain * ratio;
                ratioCross = crossFromRatio + child.AspectRatioAdjustment.Cross(constants.Dir);
            }

            float? childCross = (ratioCross ?? child.Size.Cross(constants.Dir))
                .MaybeClamp(child.MinSize.Cross(constants.Dir), child.MaxSize.Cross(constants.Dir))
                .MaybeMax(paddingBorderSum);

            var childAvailableCross = availableSpace
                .Cross(constants.Dir)
                .MaybeClamp(child.MinSize.Cross(constants.Dir), child.MaxSize.Cross(constants.Dir))
                .MaybeMax(paddingBorderSum);

            float childInnerCross;
            if (childCross.HasValue)
            {
                childInnerCross = childCross.Value;
            }
            else
            {
                var measureKnown = new Size<float?>(
                    constants.IsRow ? child.TargetSize.Width : childCross,
                    constants.IsRow ? childCross : child.TargetSize.Height);
                var measureAvailable = new Size<AvailableSpace>(
                    constants.IsRow ? childKnownMain : childAvailableCross,
                    constants.IsRow ? childAvailableCross : childKnownMain);

                childInnerCross = Sys.F32Max(
                    tree.MeasureChildSize(
                        child.Node,
                        measureKnown,
                        constants.NodeInnerSize,
                        measureAvailable,
                        SizingMode.ContentSize,
                        constants.Dir.CrossAxis(),
                        GeometryExtensions.LineFalse)
                        .MaybeClamp(child.MinSize.Cross(constants.Dir), child.MaxSize.Cross(constants.Dir)),
                    paddingBorderSum);
            }

            float childOuterCross = childInnerCross + child.Margin.CrossAxisSum(constants.Dir);

            child.HypotheticalInnerSize.SetCross(constants.Dir, childInnerCross);
            child.HypotheticalOuterSize.SetCross(constants.Dir, childOuterCross);
        }
    }

    /// <summary>Calculate the baselines of the children.</summary>
    private static void CalculateChildrenBaseLines(
        ILayoutFlexboxContainer tree,
        Size<float?> nodeSize,
        Size<AvailableSpace> availableSpace,
        List<FlexLine> flexLines,
        AlgoConstants constants)
    {
        // Only compute baselines for flex rows because we only support baseline alignment in the
        // cross axis where that axis is also the inline axis.
        if (!constants.IsRow)
        {
            return;
        }

        for (int lineIdx = 0; lineIdx < flexLines.Count; lineIdx++)
        {
            var line = flexLines[lineIdx];

            int lineBaselineChildCount = 0;
            for (int i = 0; i < line.Items.Length; i++)
            {
                if (line.Items[i].AlignSelf == AlignSelf.Baseline)
                {
                    lineBaselineChildCount += 1;
                }
            }

            if (lineBaselineChildCount <= 1)
            {
                continue;
            }

            for (int i = 0; i < line.Items.Length; i++)
            {
                var child = line.Items[i];
                if (child.AlignSelf != AlignSelf.Baseline)
                {
                    continue;
                }

                var measuredSizeAndBaselines = tree.PerformChildLayout(
                    child.Node,
                    new Size<float?>(
                        constants.IsRow ? child.TargetSize.Width : child.HypotheticalInnerSize.Width,
                        constants.IsRow ? child.HypotheticalInnerSize.Height : child.TargetSize.Height),
                    constants.NodeInnerSize,
                    new Size<AvailableSpace>(
                        constants.IsRow
                            ? AvailableSpace.Definite(constants.ContainerSize.Width)
                            : availableSpace.Width.MaybeSet(nodeSize.Width),
                        constants.IsRow
                            ? availableSpace.Height.MaybeSet(nodeSize.Height)
                            : AvailableSpace.Definite(constants.ContainerSize.Height)),
                    SizingMode.ContentSize,
                    GeometryExtensions.LineFalse);

                float? baseline = measuredSizeAndBaselines.FirstBaselines.Y;
                float height = measuredSizeAndBaselines.Size.Height;

                child.Baseline = (baseline ?? height) + child.Margin.Top;
            }
        }
    }

    /// <summary>Calculate the cross size of each flex line.</summary>
    private static void CalculateCrossSize(
        List<FlexLine> flexLines,
        Size<float?> nodeSize,
        AlgoConstants constants)
    {
        // If the flex container is single-line and has a definite cross size, the cross size of the
        // flex line is the flex container's inner cross size.
        if (!constants.IsWrap && nodeSize.Cross(constants.Dir).HasValue)
        {
            float crossAxisPaddingBorder = constants.ContentBoxInset.CrossAxisSum(constants.Dir);
            float? crossMinSize = constants.MinSize.Cross(constants.Dir);
            float? crossMaxSize = constants.MaxSize.Cross(constants.Dir);
            flexLines[0].CrossSize = nodeSize
                .Cross(constants.Dir)
                .MaybeClamp(crossMinSize, crossMaxSize)
                .MaybeSub(crossAxisPaddingBorder)
                .MaybeMax(0.0f) ?? 0.0f;
            return;
        }

        for (int lineIdx = 0; lineIdx < flexLines.Count; lineIdx++)
        {
            var line = flexLines[lineIdx];
            float maxBaseline = 0.0f;
            for (int i = 0; i < line.Items.Length; i++)
            {
                maxBaseline = Sys.F32Max(maxBaseline, line.Items[i].Baseline);
            }

            float crossSize = 0.0f;
            for (int i = 0; i < line.Items.Length; i++)
            {
                var child = line.Items[i];
                float value;
                if (child.AlignSelf == AlignSelf.Baseline
                    && !child.MarginIsAuto.CrossStart(constants.Dir)
                    && !child.MarginIsAuto.CrossEnd(constants.Dir))
                {
                    value = maxBaseline - child.Baseline + child.HypotheticalOuterSize.Cross(constants.Dir);
                }
                else
                {
                    value = child.HypotheticalOuterSize.Cross(constants.Dir);
                }

                crossSize = Sys.F32Max(crossSize, value);
            }

            line.CrossSize = crossSize;
        }

        // If the flex container is single-line, clamp the line's cross-size to be within the
        // container's computed min and max cross sizes.
        if (!constants.IsWrap)
        {
            float crossAxisPaddingBorder = constants.ContentBoxInset.CrossAxisSum(constants.Dir);
            float? crossMinSize = constants.MinSize.Cross(constants.Dir);
            float? crossMaxSize = constants.MaxSize.Cross(constants.Dir);
            flexLines[0].CrossSize = flexLines[0].CrossSize.MaybeClamp(
                crossMinSize.MaybeSub(crossAxisPaddingBorder),
                crossMaxSize.MaybeSub(crossAxisPaddingBorder));
        }
    }

    /// <summary>Handle <c>align-content: stretch</c>.</summary>
    private static void HandleAlignContentStretch(
        List<FlexLine> flexLines,
        Size<float?> nodeSize,
        AlgoConstants constants)
    {
        if (constants.AlignContent != AlignContent.Stretch)
        {
            return;
        }

        float crossAxisPaddingBorder = constants.ContentBoxInset.CrossAxisSum(constants.Dir);
        float? crossMinSize = constants.MinSize.Cross(constants.Dir);
        float? crossMaxSize = constants.MaxSize.Cross(constants.Dir);
        float containerMinInnerCross = (nodeSize.Cross(constants.Dir) ?? crossMinSize)
            .MaybeClamp(crossMinSize, crossMaxSize)
            .MaybeSub(crossAxisPaddingBorder)
            .MaybeMax(0.0f) ?? 0.0f;

        float totalCrossAxisGap = SumAxisGaps(constants.Gap.Cross(constants.Dir), flexLines.Count);
        float linesTotalCross = 0.0f;
        for (int i = 0; i < flexLines.Count; i++)
        {
            linesTotalCross += flexLines[i].CrossSize;
        }

        linesTotalCross += totalCrossAxisGap;

        if (linesTotalCross < containerMinInnerCross)
        {
            float remaining = containerMinInnerCross - linesTotalCross;
            float addition = remaining / flexLines.Count;
            for (int i = 0; i < flexLines.Count; i++)
            {
                flexLines[i].CrossSize += addition;
            }
        }
    }

    /// <summary>Determine the used cross size of each flex item.</summary>
    private static void DetermineUsedCrossSize(
        ILayoutFlexboxContainer tree,
        List<FlexLine> flexLines,
        AlgoConstants constants)
    {
        var calc = tree.CalcResolver();

        for (int lineIdx = 0; lineIdx < flexLines.Count; lineIdx++)
        {
            var line = flexLines[lineIdx];
            float lineCrossSize = line.CrossSize;

            for (int i = 0; i < line.Items.Length; i++)
            {
                var child = line.Items[i];
                var childStyle = tree.GetFlexboxChildStyle(child.Node);

                float crossTarget;
                if (child.AlignSelf == AlignSelf.Stretch
                    && !child.MarginIsAuto.CrossStart(constants.Dir)
                    && !child.MarginIsAuto.CrossEnd(constants.Dir)
                    && childStyle.Size.Cross(constants.Dir).IsAuto)
                {
                    // max_size here intentionally does NOT transfer through the aspect ratio; both
                    // Chrome and Firefox agree on this.
                    var padding = childStyle.Padding.ResolveOrZero(constants.NodeInnerSize, calc);
                    var border = childStyle.Border.ResolveOrZero(constants.NodeInnerSize, calc);
                    var pbSum = padding.Add(border).SumAxes();
                    var boxSizingAdjustment =
                        childStyle.BoxSizing == BoxSizing.ContentBox ? pbSum : GeometryExtensions.SizeZero;

                    var maxSizeIgnoringAspectRatio = childStyle.MaxSize
                        .MaybeResolve(constants.NodeInnerSize, calc)
                        .MaybeAdd(boxSizingAdjustment);

                    crossTarget = (lineCrossSize - child.Margin.CrossAxisSum(constants.Dir)).MaybeClamp(
                        child.MinSize.Cross(constants.Dir),
                        maxSizeIgnoringAspectRatio.Cross(constants.Dir));
                }
                else
                {
                    crossTarget = child.HypotheticalInnerSize.Cross(constants.Dir);
                }

                child.TargetSize.SetCross(constants.Dir, crossTarget);
                child.OuterTargetSize.SetCross(
                    constants.Dir,
                    child.TargetSize.Cross(constants.Dir) + child.Margin.CrossAxisSum(constants.Dir));
            }
        }
    }

    /// <summary>Distribute any remaining free space.</summary>
    private static void DistributeRemainingFreeSpace(List<FlexLine> flexLines, AlgoConstants constants)
    {
        for (int lineIdx = 0; lineIdx < flexLines.Count; lineIdx++)
        {
            var line = flexLines[lineIdx];
            float totalMainAxisGap = SumAxisGaps(constants.Gap.Main(constants.Dir), line.Items.Length);
            float usedSpace = totalMainAxisGap;
            for (int i = 0; i < line.Items.Length; i++)
            {
                usedSpace += line.Items[i].OuterTargetSize.Main(constants.Dir);
            }

            float freeSpace = constants.InnerContainerSize.Main(constants.Dir) - usedSpace;
            int numAutoMargins = 0;

            for (int i = 0; i < line.Items.Length; i++)
            {
                var child = line.Items[i];
                if (child.MarginIsAuto.MainStart(constants.Dir))
                {
                    numAutoMargins += 1;
                }

                if (child.MarginIsAuto.MainEnd(constants.Dir))
                {
                    numAutoMargins += 1;
                }
            }

            if (freeSpace > 0.0f && numAutoMargins > 0)
            {
                float margin = freeSpace / numAutoMargins;

                for (int i = 0; i < line.Items.Length; i++)
                {
                    var child = line.Items[i];
                    if (child.MarginIsAuto.MainStart(constants.Dir))
                    {
                        if (constants.IsRow)
                        {
                            child.Margin.Left = margin;
                        }
                        else
                        {
                            child.Margin.Top = margin;
                        }
                    }

                    if (child.MarginIsAuto.MainEnd(constants.Dir))
                    {
                        if (constants.IsRow)
                        {
                            child.Margin.Right = margin;
                        }
                        else
                        {
                            child.Margin.Bottom = margin;
                        }
                    }
                }
            }

            int numItems = line.Items.Length;
            bool layoutReverse = constants.Dir.IsReverse();
            float gap = constants.Gap.Main(constants.Dir);
            var rawJustifyContentMode = constants.JustifyContent ?? JustifyContent.FlexStart;
            var justifyContentMode =
                CommonAlignment.ApplyAlignmentFallback(freeSpace, numItems, rawJustifyContentMode);

            for (int i = 0; i < numItems; i++)
            {
                var child = layoutReverse ? line.Items[numItems - 1 - i] : line.Items[i];
                child.OffsetMain = CommonAlignment.ComputeAlignmentOffset(
                    freeSpace, numItems, gap, justifyContentMode, layoutReverse, i == 0);
            }
        }
    }

    /// <summary>Resolve cross-axis auto margins.</summary>
    private static void ResolveCrossAxisAutoMargins(List<FlexLine> flexLines, AlgoConstants constants)
    {
        for (int lineIdx = 0; lineIdx < flexLines.Count; lineIdx++)
        {
            var line = flexLines[lineIdx];
            float lineCrossSize = line.CrossSize;
            float maxBaseline = 0.0f;
            for (int i = 0; i < line.Items.Length; i++)
            {
                maxBaseline = Sys.F32Max(maxBaseline, line.Items[i].Baseline);
            }

            for (int i = 0; i < line.Items.Length; i++)
            {
                var child = line.Items[i];
                float freeSpace = lineCrossSize - child.OuterTargetSize.Cross(constants.Dir);

                if (child.MarginIsAuto.CrossStart(constants.Dir)
                    && child.MarginIsAuto.CrossEnd(constants.Dir))
                {
                    if (constants.IsRow)
                    {
                        child.Margin.Top = freeSpace / 2.0f;
                        child.Margin.Bottom = freeSpace / 2.0f;
                    }
                    else
                    {
                        child.Margin.Left = freeSpace / 2.0f;
                        child.Margin.Right = freeSpace / 2.0f;
                    }
                }
                else if (child.MarginIsAuto.CrossStart(constants.Dir))
                {
                    if (constants.IsRow)
                    {
                        child.Margin.Top = freeSpace;
                    }
                    else
                    {
                        child.Margin.Left = freeSpace;
                    }
                }
                else if (child.MarginIsAuto.CrossEnd(constants.Dir))
                {
                    if (constants.IsRow)
                    {
                        child.Margin.Bottom = freeSpace;
                    }
                    else
                    {
                        child.Margin.Right = freeSpace;
                    }
                }
                else
                {
                    // 14. Align all flex items along the cross-axis.
                    child.OffsetCross =
                        AlignFlexItemsAlongCrossAxis(child, freeSpace, maxBaseline, constants);
                }
            }
        }
    }

    /// <summary>Align all flex items along the cross-axis.</summary>
    private static float AlignFlexItemsAlongCrossAxis(
        FlexItem child,
        float freeSpace,
        float maxBaseline,
        AlgoConstants constants)
    {
        bool crossAxisShouldReverse = constants.IsColumn && constants.LayoutDirection == Direction.Rtl;

        // If align-self uses a "safe" overflow-position keyword and the item would overflow its line
        // cross size, fall back to logical Start to avoid data loss.
        var alignKeyword = child.AlignSelf.IsSafe && freeSpace < 0.0f
            ? AlignItemsKeyword.Start
            : child.AlignSelf.Keyword;

        switch (alignKeyword)
        {
            case AlignItemsKeyword.Start:
                return crossAxisShouldReverse ? freeSpace : 0.0f;
            case AlignItemsKeyword.FlexStart:
                return constants.IsWrapReverse ^ crossAxisShouldReverse ? freeSpace : 0.0f;
            case AlignItemsKeyword.End:
                return crossAxisShouldReverse ? 0.0f : freeSpace;
            case AlignItemsKeyword.FlexEnd:
                return constants.IsWrapReverse ^ crossAxisShouldReverse ? 0.0f : freeSpace;
            case AlignItemsKeyword.Center:
                return freeSpace / 2.0f;
            case AlignItemsKeyword.Baseline:
                if (constants.IsRow)
                {
                    return maxBaseline - child.Baseline;
                }
                else
                {
                    // Until vertical writing modes are supported, baseline alignment only makes
                    // sense in rows, so it is treated as flex-start alignment in columns.
                    bool baselineColumnShouldReverse = crossAxisShouldReverse && !constants.IsWrap;
                    return constants.IsWrapReverse ^ baselineColumnShouldReverse ? freeSpace : 0.0f;
                }

            default:
                // Normal | Stretch
                return constants.IsWrapReverse ^ crossAxisShouldReverse ? freeSpace : 0.0f;
        }
    }

    /// <summary>Determine the flex container's used cross size.</summary>
    private static float DetermineContainerCrossSize(
        List<FlexLine> flexLines,
        Size<float?> nodeSize,
        AlgoConstants constants)
    {
        float totalCrossAxisGap = SumAxisGaps(constants.Gap.Cross(constants.Dir), flexLines.Count);
        float totalLineCrossSize = 0.0f;
        for (int i = 0; i < flexLines.Count; i++)
        {
            totalLineCrossSize += flexLines[i].CrossSize;
        }

        float paddingBorderSum = constants.ContentBoxInset.CrossAxisSum(constants.Dir);
        float crossScrollbarGutter = constants.ScrollbarGutter.Cross(constants.Dir);
        float? minCrossSize = constants.MinSize.Cross(constants.Dir);
        float? maxCrossSize = constants.MaxSize.Cross(constants.Dir);
        float outerContainerSize = Sys.F32Max(
            (nodeSize.Cross(constants.Dir) ?? (totalLineCrossSize + totalCrossAxisGap + paddingBorderSum))
                .MaybeClamp(minCrossSize, maxCrossSize),
            paddingBorderSum - crossScrollbarGutter);
        float innerContainerSize = Sys.F32Max(outerContainerSize - paddingBorderSum, 0.0f);

        constants.ContainerSize.SetCross(constants.Dir, outerContainerSize);
        constants.InnerContainerSize.SetCross(constants.Dir, innerContainerSize);

        return totalLineCrossSize;
    }

    /// <summary>Align all flex lines per <c>align-content</c>.</summary>
    private static void AlignFlexLinesPerAlignContent(
        List<FlexLine> flexLines,
        AlgoConstants constants,
        float totalCrossSize)
    {
        int numLines = flexLines.Count;
        float gap = constants.Gap.Cross(constants.Dir);
        float totalCrossAxisGap = SumAxisGaps(gap, numLines);
        float freeSpace =
            constants.InnerContainerSize.Cross(constants.Dir) - totalCrossSize - totalCrossAxisGap;

        var alignContentMode =
            CommonAlignment.ApplyAlignmentFallback(freeSpace, numLines, constants.AlignContent);

        for (int i = 0; i < numLines; i++)
        {
            var line = constants.IsWrapReverse ? flexLines[numLines - 1 - i] : flexLines[i];
            line.OffsetCross = CommonAlignment.ComputeAlignmentOffset(
                freeSpace, numLines, gap, alignContentMode, constants.IsWrapReverse, i == 0);
        }
    }

    /// <summary>Calculates the layout for a flex item.</summary>
    private static void CalculateFlexItem(
        ILayoutFlexboxContainer tree,
        FlexItem item,
        ref float totalOffsetMain,
        float totalOffsetCross,
        float lineOffsetCross,
        ref Size<float> totalContentSize,
        Size<float> containerSize,
        Size<float?> nodeInnerSize,
        FlexDirection direction,
        Direction layoutDirection)
    {
        var layoutOutput = tree.PerformChildLayout(
            item.Node,
            item.TargetSize.AsOptions(),
            nodeInnerSize,
            new Size<AvailableSpace>(
                AvailableSpace.Definite(containerSize.Width),
                AvailableSpace.Definite(containerSize.Height)),
            SizingMode.ContentSize,
            GeometryExtensions.LineFalse);

        var size = layoutOutput.Size;
        var contentSize = layoutOutput.ContentSize;

        bool isRtlRow = direction.IsRow() && layoutDirection.IsRtl();
        bool isRtlColumn = direction.IsColumn() && layoutDirection.IsRtl();

        float mainRelativeInset;
        if (isRtlRow)
        {
            float? mainStart = item.Inset.MainStart(direction);
            mainRelativeInset = item.Inset.MainEnd(direction)
                ?? (mainStart.HasValue ? -mainStart.Value : (float?)null)
                ?? 0.0f;
        }
        else
        {
            float? mainEnd = item.Inset.MainEnd(direction);
            mainRelativeInset = item.Inset.MainStart(direction)
                ?? (mainEnd.HasValue ? -mainEnd.Value : (float?)null)
                ?? 0.0f;
        }

        float crossRelativeInset;
        if (isRtlColumn)
        {
            float? crossEnd = item.Inset.CrossEnd(direction);
            crossRelativeInset = (crossEnd.HasValue ? -crossEnd.Value : (float?)null)
                ?? item.Inset.CrossStart(direction)
                ?? 0.0f;
        }
        else
        {
            float? crossEnd = item.Inset.CrossEnd(direction);
            crossRelativeInset = item.Inset.CrossStart(direction)
                ?? (crossEnd.HasValue ? -crossEnd.Value : (float?)null)
                ?? 0.0f;
        }

        float effectiveLineOffsetCross = isRtlColumn ? 0.0f : lineOffsetCross;

        float offsetMain = isRtlRow
            ? totalOffsetMain - item.OffsetMain - item.Margin.MainEnd(direction) - mainRelativeInset
                - size.Width
            : totalOffsetMain + item.OffsetMain + item.Margin.MainStart(direction) + mainRelativeInset;

        float offsetCross = totalOffsetCross
            + item.OffsetCross
            + effectiveLineOffsetCross
            + item.Margin.CrossStart(direction)
            + crossRelativeInset;

        if (direction.IsRow())
        {
            float baselineOffsetCross = totalOffsetCross + item.OffsetCross + effectiveLineOffsetCross
                + item.Margin.CrossStart(direction);
            float innerBaseline = layoutOutput.FirstBaselines.Y ?? size.Height;
            item.Baseline = baselineOffsetCross + innerBaseline;
        }
        else
        {
            float baselineOffsetMain =
                totalOffsetMain + item.OffsetMain + item.Margin.MainStart(direction);
            float innerBaseline = layoutOutput.FirstBaselines.Y ?? size.Height;
            item.Baseline = baselineOffsetMain + innerBaseline;
        }

        var location = direction.IsRow()
            ? new Point<float>(offsetMain, offsetCross)
            : new Point<float>(offsetCross, offsetMain);
        var scrollbarSize = new Size<float>(
            item.Overflow.Y == Overflow.Scroll ? item.ScrollbarWidth : 0.0f,
            item.Overflow.X == Overflow.Scroll ? item.ScrollbarWidth : 0.0f);

        var itemLayout = new Layout
        {
            Order = item.Order,
            Size = size,
            ContentSize = contentSize,
            ScrollbarSize = scrollbarSize,
            Location = location,
            Padding = item.Padding,
            Border = item.Border,
            Margin = item.Margin,
        };
        tree.SetUnroundedLayout(item.Node, in itemLayout);

        if (isRtlRow)
        {
            totalOffsetMain -=
                item.OffsetMain + item.Margin.MainAxisSum(direction) + size.Main(direction);
        }
        else
        {
            totalOffsetMain +=
                item.OffsetMain + item.Margin.MainAxisSum(direction) + size.Main(direction);
        }

        var contributionLocation = layoutDirection.IsRtl()
            ? new Point<float>(containerSize.Width - (location.X + size.Width), location.Y)
            : location;
        totalContentSize = totalContentSize.F32Max(
            ContentSizeHelper.ComputeContentSizeContribution(
                contributionLocation, size, contentSize, item.Overflow));
    }

    /// <summary>Calculates the layout of a flex line.</summary>
    private static void CalculateLayoutLine(
        ILayoutFlexboxContainer tree,
        FlexLine line,
        ref float totalOffsetCross,
        ref Size<float> contentSize,
        Size<float> containerSize,
        Size<float?> nodeInnerSize,
        Rect<float> paddingBorder,
        FlexDirection direction,
        Direction layoutDirection)
    {
        float totalOffsetMain = layoutDirection.IsRtl() && direction.IsRow()
            ? containerSize.Width - paddingBorder.MainEnd(direction)
            : paddingBorder.MainStart(direction);
        float lineOffsetCross = line.OffsetCross;

        bool isRtlColumn = layoutDirection.IsRtl() && direction.IsColumn();
        if (isRtlColumn)
        {
            totalOffsetCross -= lineOffsetCross + line.CrossSize;
        }

        if (direction.IsReverse())
        {
            for (int i = line.Items.Length - 1; i >= 0; i--)
            {
                CalculateFlexItem(
                    tree, line.Items[i], ref totalOffsetMain, totalOffsetCross, lineOffsetCross,
                    ref contentSize, containerSize, nodeInnerSize, direction, layoutDirection);
            }
        }
        else
        {
            for (int i = 0; i < line.Items.Length; i++)
            {
                CalculateFlexItem(
                    tree, line.Items[i], ref totalOffsetMain, totalOffsetCross, lineOffsetCross,
                    ref contentSize, containerSize, nodeInnerSize, direction, layoutDirection);
            }
        }

        if (!isRtlColumn)
        {
            totalOffsetCross += lineOffsetCross + line.CrossSize;
        }
    }

    /// <summary>Do a final layout pass and collect the resulting layouts.</summary>
    private static Size<float> FinalLayoutPass(
        ILayoutFlexboxContainer tree,
        List<FlexLine> flexLines,
        AlgoConstants constants)
    {
        float totalOffsetCross = constants.IsColumn && constants.LayoutDirection.IsRtl()
            ? constants.ContainerSize.Width - constants.ContentBoxInset.CrossEnd(constants.Dir)
            : constants.ContentBoxInset.CrossStart(constants.Dir);

        var contentSize = GeometryExtensions.SizeZero;

        if (constants.IsWrapReverse)
        {
            for (int i = flexLines.Count - 1; i >= 0; i--)
            {
                CalculateLayoutLine(
                    tree, flexLines[i], ref totalOffsetCross, ref contentSize, constants.ContainerSize,
                    constants.NodeInnerSize, constants.ContentBoxInset, constants.Dir,
                    constants.LayoutDirection);
            }
        }
        else
        {
            for (int i = 0; i < flexLines.Count; i++)
            {
                CalculateLayoutLine(
                    tree, flexLines[i], ref totalOffsetCross, ref contentSize, constants.ContainerSize,
                    constants.NodeInnerSize, constants.ContentBoxInset, constants.Dir,
                    constants.LayoutDirection);
            }
        }

        contentSize.Width += constants.LayoutDirection.IsRtl()
            ? constants.ContentBoxInset.Left - constants.Border.Left - constants.ScrollbarGutter.X
            : constants.ContentBoxInset.Right - constants.Border.Right - constants.ScrollbarGutter.X;
        contentSize.Height +=
            constants.ContentBoxInset.Bottom - constants.Border.Bottom - constants.ScrollbarGutter.Y;

        return contentSize;
    }

    /// <summary>Perform absolute layout on all absolutely positioned children.</summary>
    private static Size<float> PerformAbsoluteLayoutOnAbsoluteChildren(
        ILayoutFlexboxContainer tree,
        NodeId node,
        AlgoConstants constants)
    {
        var calc = tree.CalcResolver();
        float containerWidth = constants.ContainerSize.Width;
        float containerHeight = constants.ContainerSize.Height;
        var insetRelativeSize = constants.ContainerSize
            .Sub(constants.Border.SumAxes())
            .Sub(constants.ScrollbarGutter.ToSize());

        var contentSize = GeometryExtensions.SizeZero;

        int childCount = tree.ChildCount(node);
        for (int order = 0; order < childCount; order++)
        {
            var child = tree.GetChildId(node, order);
            var childStyle = tree.GetFlexboxChildStyle(child);

            // Skip items that are display:none or are not position:absolute
            if (childStyle.BoxGenerationMode == BoxGenerationMode.None
                || childStyle.Position != Position.Absolute)
            {
                continue;
            }

            var overflow = childStyle.Overflow;
            float scrollbarWidth = childStyle.ScrollbarWidth;
            float? aspectRatio = childStyle.AspectRatio;
            var alignSelf = ResolveFlexNormal(childStyle.AlignSelf ?? constants.AlignItems);
            var margin = childStyle.Margin.Map(m => m.ResolveToOption(insetRelativeSize.Width, calc));
            var padding = childStyle.Padding.ResolveOrZero((float?)insetRelativeSize.Width, calc);
            var border = childStyle.Border.ResolveOrZero((float?)insetRelativeSize.Width, calc);
            var paddingBorderSum = padding.Add(border).SumAxes();
            var boxSizingAdjustment =
                childStyle.BoxSizing == BoxSizing.ContentBox ? paddingBorderSum : GeometryExtensions.SizeZero;

            // Insets are resolved against the container size minus border
            float? left = childStyle.Inset.Left.MaybeResolve(insetRelativeSize.Width, calc);
            float? right = childStyle.Inset.Right.MaybeResolve(insetRelativeSize.Width, calc);
            float? top = childStyle.Inset.Top.MaybeResolve(insetRelativeSize.Height, calc);
            float? bottom = childStyle.Inset.Bottom.MaybeResolve(insetRelativeSize.Height, calc);

            // Compute known dimensions from min/max/inherent size styles
            var styleSize = childStyle.Size
                .MaybeResolve(insetRelativeSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment);
            var minSize = childStyle.MinSize
                .MaybeResolve(insetRelativeSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment)
                .Or(paddingBorderSum.AsOptions())
                .MaybeMax(paddingBorderSum);
            var maxSize = childStyle.MaxSize
                .MaybeResolve(insetRelativeSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment);
            var knownDimensions = styleSize.MaybeClamp(minSize, maxSize);

            // Fill in width from left/right and reapply aspect ratio
            if (!knownDimensions.Width.HasValue && left.HasValue && right.HasValue)
            {
                float newWidthRaw = insetRelativeSize.Width.MaybeSub(margin.Left).MaybeSub(margin.Right)
                    - left.Value - right.Value;
                knownDimensions.Width = Sys.F32Max(newWidthRaw, 0.0f);
                knownDimensions = knownDimensions.MaybeApplyAspectRatio(aspectRatio).MaybeClamp(minSize, maxSize);
            }

            // Fill in height from top/bottom and reapply aspect ratio
            if (!knownDimensions.Height.HasValue && top.HasValue && bottom.HasValue)
            {
                float newHeightRaw = insetRelativeSize.Height.MaybeSub(margin.Top).MaybeSub(margin.Bottom)
                    - top.Value - bottom.Value;
                knownDimensions.Height = Sys.F32Max(newHeightRaw, 0.0f);
                knownDimensions = knownDimensions.MaybeApplyAspectRatio(aspectRatio).MaybeClamp(minSize, maxSize);
            }

            var childAvailableSpace = new Size<AvailableSpace>(
                AvailableSpace.Definite(containerWidth.MaybeClamp(minSize.Width, maxSize.Width)),
                AvailableSpace.Definite(containerHeight.MaybeClamp(minSize.Height, maxSize.Height)));

            var measuredSize = tree.MeasureChildSizeBoth(
                child,
                knownDimensions,
                constants.NodeInnerSize,
                childAvailableSpace,
                SizingMode.InherentSize,
                GeometryExtensions.LineFalse);
            var finalSize = knownDimensions.UnwrapOr(measuredSize).MaybeClamp(minSize, maxSize);

            var layoutOutput = tree.PerformChildLayout(
                child,
                finalSize.AsOptions(),
                constants.NodeInnerSize,
                childAvailableSpace,
                SizingMode.InherentSize,
                GeometryExtensions.LineFalse);

            var nonAutoMargin = margin.Map(static m => m ?? 0.0f);

            var freeSpace = new Size<float>(
                constants.ContainerSize.Width - finalSize.Width - nonAutoMargin.HorizontalAxisSum(),
                constants.ContainerSize.Height - finalSize.Height - nonAutoMargin.VerticalAxisSum())
                .F32Max(GeometryExtensions.SizeZero);

            // Expand auto margins to fill available space
            int autoMarginCountWidth = (margin.Left.HasValue ? 0 : 1) + (margin.Right.HasValue ? 0 : 1);
            float autoMarginWidth = autoMarginCountWidth > 0 ? freeSpace.Width / autoMarginCountWidth : 0.0f;
            int autoMarginCountHeight = (margin.Top.HasValue ? 0 : 1) + (margin.Bottom.HasValue ? 0 : 1);
            float autoMarginHeight =
                autoMarginCountHeight > 0 ? freeSpace.Height / autoMarginCountHeight : 0.0f;

            var resolvedMargin = new Rect<float>(
                margin.Left ?? autoMarginWidth,
                margin.Right ?? autoMarginWidth,
                margin.Top ?? autoMarginHeight,
                margin.Bottom ?? autoMarginHeight);

            // Determine flex-relative insets
            float? startMain = constants.IsRow ? left : top;
            float? endMain = constants.IsRow ? right : bottom;
            float? startCross = constants.IsRow ? top : left;
            float? endCross = constants.IsRow ? bottom : right;
            bool mainAxisIsHorizontal = constants.IsRow;
            bool crossAxisIsHorizontal = !constants.IsRow;
            bool mainIsRtl = mainAxisIsHorizontal && constants.LayoutDirection.IsRtl();
            bool crossIsRtl = crossAxisIsHorizontal && constants.LayoutDirection.IsRtl();
            bool mainAxisFlexStartReversed = constants.Dir.IsReverse() ^ mainIsRtl;
            bool crossAxisFlexStartReversed = constants.IsWrapReverse ^ crossIsRtl;
            float mainStartScrollbarOffset = mainIsRtl ? constants.ScrollbarGutter.Main(constants.Dir) : 0.0f;
            float crossStartScrollbarOffset =
                crossIsRtl ? constants.ScrollbarGutter.Cross(constants.Dir) : 0.0f;
            float mainEndScrollbarOffset = mainIsRtl ? 0.0f : constants.ScrollbarGutter.Main(constants.Dir);
            float crossEndScrollbarOffset =
                crossIsRtl ? 0.0f : constants.ScrollbarGutter.Cross(constants.Dir);

            // Apply main-axis alignment
            float offsetMain;
            if (startMain.HasValue || endMain.HasValue)
            {
                if (mainIsRtl && endMain.HasValue)
                {
                    offsetMain = constants.ContainerSize.Main(constants.Dir)
                        - constants.Border.MainEnd(constants.Dir)
                        - mainEndScrollbarOffset
                        - finalSize.Main(constants.Dir)
                        - (endMain ?? 0.0f)
                        - resolvedMargin.MainEnd(constants.Dir);
                }
                else if (startMain.HasValue)
                {
                    offsetMain = startMain.Value
                        + constants.Border.MainStart(constants.Dir)
                        + mainStartScrollbarOffset
                        + resolvedMargin.MainStart(constants.Dir);
                }
                else
                {
                    offsetMain = constants.ContainerSize.Main(constants.Dir)
                        - constants.Border.MainEnd(constants.Dir)
                        - mainEndScrollbarOffset
                        - finalSize.Main(constants.Dir)
                        - (endMain ?? 0.0f)
                        - resolvedMargin.MainEnd(constants.Dir);
                }
            }
            else
            {
                // Stretch is an invalid value for justify_content in flexbox, so it is treated as
                // FlexStart. The `safe` keyword is intentionally NOT applied here: Chrome does not
                // apply safe fallback to justify-content on abs-positioned flex items.
                float mainStartOffset = constants.ContentBoxInset.MainStart(constants.Dir)
                    + resolvedMargin.MainStart(constants.Dir);
                float mainEndOffset = constants.ContainerSize.Main(constants.Dir)
                    - constants.ContentBoxInset.MainEnd(constants.Dir)
                    - finalSize.Main(constants.Dir)
                    - resolvedMargin.MainEnd(constants.Dir);
                float mainCenterOffset = (constants.ContainerSize.Main(constants.Dir)
                    + constants.ContentBoxInset.MainStart(constants.Dir)
                    - constants.ContentBoxInset.MainEnd(constants.Dir)
                    - finalSize.Main(constants.Dir)
                    + resolvedMargin.MainStart(constants.Dir)
                    - resolvedMargin.MainEnd(constants.Dir)) / 2.0f;

                var justifyKeyword = (constants.JustifyContent ?? JustifyContent.Start).GetKeyword();
                offsetMain = justifyKeyword switch
                {
                    AlignContentKeyword.SpaceBetween => mainStartOffset,
                    AlignContentKeyword.Stretch => mainAxisFlexStartReversed ? mainEndOffset : mainStartOffset,
                    AlignContentKeyword.FlexStart => mainAxisFlexStartReversed ? mainEndOffset : mainStartOffset,
                    AlignContentKeyword.FlexEnd => mainAxisFlexStartReversed ? mainStartOffset : mainEndOffset,
                    AlignContentKeyword.Start => mainAxisFlexStartReversed ? mainEndOffset : mainStartOffset,
                    AlignContentKeyword.End => mainAxisFlexStartReversed ? mainStartOffset : mainEndOffset,
                    _ => mainCenterOffset,
                };
            }

            // Apply cross-axis alignment
            float offsetCross;
            if (startCross.HasValue || endCross.HasValue)
            {
                if (crossIsRtl && endCross.HasValue)
                {
                    offsetCross = constants.ContainerSize.Cross(constants.Dir)
                        - constants.Border.CrossEnd(constants.Dir)
                        - crossEndScrollbarOffset
                        - finalSize.Cross(constants.Dir)
                        - (endCross ?? 0.0f)
                        - resolvedMargin.CrossEnd(constants.Dir);
                }
                else if (startCross.HasValue)
                {
                    offsetCross = startCross.Value
                        + constants.Border.CrossStart(constants.Dir)
                        + crossStartScrollbarOffset
                        + resolvedMargin.CrossStart(constants.Dir);
                }
                else
                {
                    offsetCross = constants.ContainerSize.Cross(constants.Dir)
                        - constants.Border.CrossEnd(constants.Dir)
                        - crossEndScrollbarOffset
                        - finalSize.Cross(constants.Dir)
                        - (endCross ?? 0.0f)
                        - resolvedMargin.CrossEnd(constants.Dir);
                }
            }
            else
            {
                bool crossOverflows =
                    finalSize.Cross(constants.Dir) + resolvedMargin.CrossAxisSum(constants.Dir)
                    > constants.ContainerSize.Cross(constants.Dir)
                        - constants.ContentBoxInset.CrossAxisSum(constants.Dir);
                var crossKeyword = CommonAlignment.ResolveSelfAlignmentSafety(alignSelf, crossOverflows);

                float crossStartOffset = constants.ContentBoxInset.CrossStart(constants.Dir)
                    + resolvedMargin.CrossStart(constants.Dir);
                float crossEndOffset = constants.ContainerSize.Cross(constants.Dir)
                    - constants.ContentBoxInset.CrossEnd(constants.Dir)
                    - finalSize.Cross(constants.Dir)
                    - resolvedMargin.CrossEnd(constants.Dir);
                float crossCenterOffset = (constants.ContainerSize.Cross(constants.Dir)
                    + constants.ContentBoxInset.CrossStart(constants.Dir)
                    - constants.ContentBoxInset.CrossEnd(constants.Dir)
                    - finalSize.Cross(constants.Dir)
                    + resolvedMargin.CrossStart(constants.Dir)
                    - resolvedMargin.CrossEnd(constants.Dir)) / 2.0f;

                // Stretch alignment does not apply to absolutely positioned items.
                offsetCross = crossKeyword switch
                {
                    AlignItemsKeyword.Start => crossAxisFlexStartReversed ? crossEndOffset : crossStartOffset,
                    AlignItemsKeyword.End => crossAxisFlexStartReversed ? crossStartOffset : crossEndOffset,
                    AlignItemsKeyword.FlexEnd => crossAxisFlexStartReversed ? crossStartOffset : crossEndOffset,
                    AlignItemsKeyword.Center => crossCenterOffset,
                    // Normal | Baseline | Stretch | FlexStart
                    _ => crossAxisFlexStartReversed ? crossEndOffset : crossStartOffset,
                };
            }

            var location = constants.IsRow
                ? new Point<float>(offsetMain, offsetCross)
                : new Point<float>(offsetCross, offsetMain);
            var scrollbarSize = new Size<float>(
                overflow.Y == Overflow.Scroll ? scrollbarWidth : 0.0f,
                overflow.X == Overflow.Scroll ? scrollbarWidth : 0.0f);

            var absLayout = new Layout
            {
                Order = (uint)order,
                Size = finalSize,
                ContentSize = layoutOutput.ContentSize,
                ScrollbarSize = scrollbarSize,
                Location = location,
                Padding = padding,
                Border = border,
                Margin = resolvedMargin,
            };
            tree.SetUnroundedLayout(child, in absLayout);

            var sizeContentSizeContribution = new Size<float>(
                overflow.X == Overflow.Visible
                    ? Sys.F32Max(finalSize.Width, layoutOutput.ContentSize.Width)
                    : finalSize.Width,
                overflow.Y == Overflow.Visible
                    ? Sys.F32Max(finalSize.Height, layoutOutput.ContentSize.Height)
                    : finalSize.Height);
            if (sizeContentSizeContribution.HasNonZeroArea())
            {
                var absoluteAreaOffset = new Point<float>(
                    constants.Border.Left
                        + (constants.LayoutDirection.IsRtl() ? constants.ScrollbarGutter.X : 0.0f),
                    constants.Border.Top);
                var relativeLocation = new Point<float>(
                    location.X - absoluteAreaOffset.X, location.Y - absoluteAreaOffset.Y);
                Size<float> contentSizeContribution;
                if (constants.LayoutDirection.IsRtl())
                {
                    float overflowExtraWidth =
                        Sys.F32Max(sizeContentSizeContribution.Width - finalSize.Width, 0.0f);
                    contentSizeContribution = new Size<float>(
                        Sys.F32Max(insetRelativeSize.Width - relativeLocation.X, 0.0f) + overflowExtraWidth,
                        relativeLocation.Y + sizeContentSizeContribution.Height);
                }
                else
                {
                    contentSizeContribution = new Size<float>(
                        relativeLocation.X + sizeContentSizeContribution.Width,
                        relativeLocation.Y + sizeContentSizeContribution.Height);
                }

                contentSize = contentSize.F32Max(contentSizeContribution);
            }
        }

        return contentSize;
    }

    /// <summary>
    /// Computes the total space taken up by gaps in an axis given the gap size and the number of
    /// items between which there are gaps.
    /// </summary>
    private static float SumAxisGaps(float gap, int numItems) =>
        numItems <= 1 ? 0.0f : gap * (numItems - 1);
}
