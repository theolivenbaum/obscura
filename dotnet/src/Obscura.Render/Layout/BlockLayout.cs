// Port of vendor/taffy/src/compute/block.rs
//
// Computes the CSS block layout algorithm in the case that the block container
// being laid out contains only block-level boxes.
namespace Obscura.Render.Layout;

/// <summary>Context for positioning Block and Float boxes within a Block Formatting Context.</summary>
public sealed class BlockFormattingContext
{
    /// <summary>The float positioning context for this Block Formatting Context.</summary>
    internal FloatContext FloatContext { get; } = new();

    /// <summary>Create an initial <see cref="BlockContext"/> for this formatting context.</summary>
    public BlockContext RootBlockContext() => new(this, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, isRoot: true);
}

/// <summary>
/// Context for each individual block within a Block Formatting Context.
/// </summary>
public sealed class BlockContext
{
    private readonly BlockFormattingContext _bfc;

    /// <summary>
    /// The y-offset of the border-top of the block node, relative to the border-top of the root
    /// node of the Block Formatting Context it belongs to.
    /// </summary>
    private readonly float _yOffset;

    /// <summary>The left x-inset of the border-box relative to the BFC root.</summary>
    private readonly float _insetLeft;

    /// <summary>The right x-inset of the border-box relative to the BFC root.</summary>
    private readonly float _insetRight;

    private float _contentBoxInsetLeft;
    private float _contentBoxInsetRight;
    private float _floatContentContribution;
    private readonly bool _isRoot;

    internal BlockContext(
        BlockFormattingContext bfc,
        float yOffset,
        float insetLeft,
        float insetRight,
        float contentBoxInsetLeft,
        float contentBoxInsetRight,
        bool isRoot)
    {
        _bfc = bfc;
        _yOffset = yOffset;
        _insetLeft = insetLeft;
        _insetRight = insetRight;
        _contentBoxInsetLeft = contentBoxInsetLeft;
        _contentBoxInsetRight = contentBoxInsetRight;
        _floatContentContribution = 0.0f;
        _isRoot = isRoot;
    }

    /// <summary>Create a sub-context for a child block node.</summary>
    public BlockContext SubContext(float additionalYOffset, float insetLeft, float insetRight)
    {
        float newInsetLeft = _insetLeft + insetLeft;
        float newInsetRight = _insetRight + insetRight;
        return new BlockContext(
            _bfc, _yOffset + additionalYOffset, newInsetLeft, newInsetRight, newInsetLeft, newInsetRight,
            isRoot: false);
    }

    /// <summary>Returns whether this block is the root block of its Block Formatting Context.</summary>
    public bool IsBfcRoot => _isRoot;

    /// <summary>
    /// Set the width of the overall Block Formatting Context, used to resolve right-relative
    /// positions such as right-floated boxes.
    /// </summary>
    public void SetWidth(float availableWidth) => _bfc.FloatContext.SetWidth(availableWidth);

    /// <summary>Set the x-axis content-box insets of the block context.</summary>
    public void ApplyContentBoxInset(float contentBoxXInsetLeft, float contentBoxXInsetRight)
    {
        _contentBoxInsetLeft = _insetLeft + contentBoxXInsetLeft;
        _contentBoxInsetRight = _insetRight + contentBoxXInsetRight;
    }

    /// <summary>Whether the float context contains any floats.</summary>
    public bool HasFloats => _bfc.FloatContext.HasFloats;

    /// <summary>Whether the float context contains any floats that extend to or below min_y.</summary>
    public bool HasActiveFloats(float minY) => _bfc.FloatContext.HasActiveFloats(minY + _yOffset);

    /// <summary>Position a floated box within the context.</summary>
    public Point<float> PlaceFloatedBox(Size<float> floatedBox, float minY, FloatDirection direction, Clear clear)
    {
        var pos = _bfc.FloatContext.PlaceFloatedBox(
            floatedBox, minY + _yOffset, _contentBoxInsetLeft, _contentBoxInsetRight, direction, clear);
        pos.Y -= _yOffset;
        pos.X -= _insetLeft;

        _floatContentContribution = Sys.F32Max(_floatContentContribution, pos.Y + floatedBox.Height);

        return pos;
    }

    /// <summary>Search for a space suitable for laying out non-floated content into.</summary>
    public ContentSlot FindContentSlot(float minY, Clear clear, int? after)
    {
        var slot = _bfc.FloatContext.FindContentSlot(
            minY + _yOffset, _contentBoxInsetLeft, _contentBoxInsetRight, clear, after);
        slot.Y -= _yOffset;
        slot.X -= _insetLeft;
        return slot;
    }

    /// <summary>Get the bottom of the lowest relevant float for the specified clear property.</summary>
    public float? ClearedThreshold(Clear clear)
    {
        float? threshold = _bfc.FloatContext.ClearedThreshold(clear);
        return threshold.HasValue ? threshold.Value - _yOffset : null;
    }

    /// <summary>Update the height that descendant floats consume within a particular child.</summary>
    internal void AddChildFloatedContentHeightContribution(float childContribution) =>
        _floatContentContribution = Sys.F32Max(_floatContentContribution, childContribution);

    /// <summary>Returns the height that descendant floats consume.</summary>
    public float FloatedContentHeightContribution() => _floatContentContribution;
}

/// <summary>Per-child data that is accumulated and modified over the course of block layout.</summary>
internal sealed class BlockItem
{
    /// <summary>The identifier for the associated node.</summary>
    public NodeId NodeId;

    /// <summary>The "source order" of the item.</summary>
    public uint Order;

    /// <summary>Items that are tables don't have stretch sizing applied to them.</summary>
    public bool IsTable;

    /// <summary>Whether the child is a non-independent block or inline node.</summary>
    public bool IsInSameBfc;

    /// <summary>The <c>float</c> style of the node.</summary>
    public Float Float;

    /// <summary>The <c>clear</c> style of the node.</summary>
    public Clear Clear;

    /// <summary>The base size of this item.</summary>
    public Size<float?> Size;

    /// <summary>The minimum allowable size of this item.</summary>
    public Size<float?> MinSize;

    /// <summary>The maximum allowable size of this item.</summary>
    public Size<float?> MaxSize;

    /// <summary>The overflow style of the item.</summary>
    public Point<Overflow> Overflow;

    /// <summary>The width of the item's scrollbars.</summary>
    public float ScrollbarWidth;

    /// <summary>The position style of the item.</summary>
    public Position Position;

    /// <summary>The final offset of this item.</summary>
    public Rect<LengthPercentageAuto> Inset;

    /// <summary>The margin of this item.</summary>
    public Rect<LengthPercentageAuto> Margin;

    /// <summary>The resolved padding of this item.</summary>
    public Rect<float> Padding;

    /// <summary>The resolved border of this item.</summary>
    public Rect<float> Border;

    /// <summary>The sum of padding and border for this item.</summary>
    public Size<float> PaddingBorderSum;

    /// <summary>The computed border box size of this item.</summary>
    public Size<float> ComputedSize;

    /// <summary>The computed "static position" of this item.</summary>
    public Point<float> StaticPosition;

    /// <summary>Whether margins can be collapsed through this item.</summary>
    public bool CanBeCollapsedThrough;

    /// <summary>
    /// Pending layout for in-flow non-floated items, held back from <c>SetUnroundedLayout</c> so the
    /// post-loop align-content pass can shift <c>Location.Y</c> before commit.
    /// </summary>
    public Layout? FinalLayout;
}

/// <summary>The CSS block layout algorithm.</summary>
public static class BlockLayout
{
    /// <summary>Computes the layout of a block container according to the block layout algorithm.</summary>
    public static LayoutOutput ComputeBlockLayout(
        ILayoutBlockContainer tree,
        NodeId nodeId,
        LayoutInput inputs,
        BlockContext? blockCtx)
    {
        var calc = tree.CalcResolver();
        var knownDimensions = inputs.KnownDimensions;
        var parentSize = inputs.ParentSize;
        var runMode = inputs.RunMode;
        var style = tree.GetBlockContainerStyle(nodeId);

        var overflow = style.Overflow;
        bool isScrollContainer = overflow.X.IsScrollContainer() || overflow.Y.IsScrollContainer();
        float? aspectRatio = style.AspectRatio;
        var padding = style.Padding.ResolveOrZero(parentSize.Width, calc);
        var border = style.Border.ResolveOrZero(parentSize.Width, calc);
        var paddingBorderSize = padding.Add(border).SumAxes();
        var boxSizingAdjustment =
            style.BoxSizing == BoxSizing.ContentBox ? paddingBorderSize : GeometryExtensions.SizeZero;

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

        var styledBasedKnownDimensions = knownDimensions
            .Or(minMaxDefiniteSize)
            .Or(clampedStyleSize)
            .MaybeMax(paddingBorderSize);

        // Short-circuit layout if the container's size is fully determined and we only need a size
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

        if (blockCtx is not null && !isScrollContainer)
        {
            return ComputeInner(tree, nodeId, innerInputs, blockCtx);
        }

        var rootBfc = new BlockFormattingContext();
        var rootCtx = rootBfc.RootBlockContext();
        return ComputeInner(tree, nodeId, innerInputs, rootCtx);
    }

    private static LayoutOutput ComputeInner(
        ILayoutBlockContainer tree,
        NodeId nodeId,
        LayoutInput inputs,
        BlockContext blockCtx)
    {
        var calc = tree.CalcResolver();
        var knownDimensions = inputs.KnownDimensions;
        var parentSize = inputs.ParentSize;
        var availableSpace = inputs.AvailableSpace;
        var runMode = inputs.RunMode;
        var verticalMarginsAreCollapsible = inputs.VerticalMarginsAreCollapsible;

        var style = tree.GetBlockContainerStyle(nodeId);
        var rawPadding = style.Padding;
        var rawBorder = style.Border;
        var rawMargin = style.Margin;
        float? aspectRatio = style.AspectRatio;
        var padding = rawPadding.ResolveOrZero(parentSize.Width, calc);
        var border = rawBorder.ResolveOrZero(parentSize.Width, calc);
        var direction = style.Direction;

        // Scrollbar gutters are reserved when overflow is Scroll. The axes are transposed because a
        // node that scrolls vertically needs *horizontal* space reserved for a scrollbar.
        var offsets = style.Overflow.Transpose().Map(
            o => o == Overflow.Scroll ? style.ScrollbarWidth : 0.0f);
        var scrollbarGutter = direction == Direction.Ltr
            ? new Rect<float>(0.0f, offsets.X, 0.0f, offsets.Y)
            : new Rect<float>(offsets.X, 0.0f, 0.0f, offsets.Y);
        var paddingBorder = padding.Add(border);
        var paddingBorderSize = paddingBorder.SumAxes();
        var contentBoxInset = paddingBorder.Add(scrollbarGutter);

        blockCtx.ApplyContentBoxInset(contentBoxInset.Left, contentBoxInset.Right);

        var boxSizingAdjustment =
            style.BoxSizing == BoxSizing.ContentBox ? paddingBorderSize : GeometryExtensions.SizeZero;
        var size = style.Size
            .MaybeResolve(parentSize, calc)
            .MaybeApplyAspectRatio(aspectRatio)
            .MaybeAdd(boxSizingAdjustment);
        var minSize = style.MinSize
            .MaybeResolve(parentSize, calc)
            .MaybeApplyAspectRatio(aspectRatio)
            .MaybeAdd(boxSizingAdjustment);
        var maxSize = style.MaxSize
            .MaybeResolve(parentSize, calc)
            .MaybeApplyAspectRatio(aspectRatio)
            .MaybeAdd(boxSizingAdjustment);

        // css-sizing-4: a definite size in one axis transfers through `aspect-ratio` to make the
        // other definite. Only a newly-filled axis is adopted (and clamped); an incoming known size
        // is left as the parent resolved it.
        {
            var derived = knownDimensions.MaybeApplyAspectRatio(aspectRatio).MaybeClamp(minSize, maxSize);
            knownDimensions = new Size<float?>(
                knownDimensions.Width ?? derived.Width,
                knownDimensions.Height ?? derived.Height);
        }

        var containerContentBoxSize = knownDimensions.MaybeSub(contentBoxInset.SumAxes());

        var overflow = style.Overflow;
        bool isScrollContainer = overflow.X.IsScrollContainer() || overflow.Y.IsScrollContainer();

        // Determine margin collapsing behaviour
        var ownMarginsCollapseWithChildren = new Line<bool>(
            verticalMarginsAreCollapsible.Start
                && !isScrollContainer
                && style.Position == Position.Relative
                && padding.Top == 0.0f
                && border.Top == 0.0f,
            verticalMarginsAreCollapsible.End
                && !isScrollContainer
                && style.Position == Position.Relative
                && padding.Bottom == 0.0f
                && border.Bottom == 0.0f
                && !size.Height.HasValue);

        bool hasStylesPreventingBeingCollapsedThrough = !style.IsBlock
            || blockCtx.IsBfcRoot
            || isScrollContainer
            || style.Position == Position.Absolute
            || padding.Top > 0.0f
            || padding.Bottom > 0.0f
            || border.Top > 0.0f
            || border.Bottom > 0.0f
            || (size.Height is { } sh && sh > 0.0f)
            || (minSize.Height is { } mh && mh > 0.0f);

        var textAlign = style.TextAlign;
        var alignContent = style.AlignContent;

        // 1. Generate items
        var items = GenerateItemList(tree, nodeId, containerContentBoxSize);

        // 2. Compute container width
        float containerOuterWidth;
        if (knownDimensions.Width.HasValue)
        {
            containerOuterWidth = knownDimensions.Width.Value;
        }
        else
        {
            var availableWidth = availableSpace.Width.MaybeSub(contentBoxInset.HorizontalAxisSum());
            float intrinsicWidth = DetermineContentBasedContainerWidth(tree, items, availableWidth)
                + contentBoxInset.HorizontalAxisSum();
            containerOuterWidth = intrinsicWidth
                .MaybeClamp(minSize.Width, maxSize.Width)
                .MaybeMax(paddingBorderSize.Width);
        }

        // Short-circuit if computing size and both dimensions known
        if (runMode == RunMode.ComputeSize && knownDimensions.Height is { } containerOuterHeightKnown)
        {
            return LayoutOutput.FromOuterSize(new Size<float>(containerOuterWidth, containerOuterHeightKnown));
        }

        // We can also short-circuit if the width is known and only the width has been requested
        if (runMode == RunMode.ComputeSize && inputs.Axis == RequestedAxis.Horizontal)
        {
            return LayoutOutput.FromOuterSize(new Size<float>(containerOuterWidth, 0.0f));
        }

        var containerPercentageResolutionHeight =
            knownDimensions.Height ?? size.Height.MaybeMax(minSize.Height) ?? minSize.Height;

        // 3. Perform final item layout and return content height
        var resolvedPadding = rawPadding.ResolveOrZero((float?)containerOuterWidth, calc);
        var resolvedBorder = rawBorder.ResolveOrZero((float?)containerOuterWidth, calc);
        var resolvedContentBoxInset = resolvedPadding.Add(resolvedBorder).Add(scrollbarGutter);

        var (inflowContentSize, intrinsicOuterHeight, firstChildTopMarginSet, lastChildBottomMarginSet) =
            PerformFinalLayoutOnInFlowChildren(
                tree,
                runMode,
                items,
                containerOuterWidth,
                containerPercentageResolutionHeight,
                contentBoxInset,
                resolvedContentBoxInset,
                textAlign,
                direction,
                ownMarginsCollapseWithChildren,
                blockCtx);

        // Root BFCs contain floats
        if (blockCtx.IsBfcRoot || isScrollContainer)
        {
            intrinsicOuterHeight =
                Sys.F32Max(intrinsicOuterHeight, blockCtx.FloatedContentHeightContribution());
        }

        float containerOuterHeight =
            (knownDimensions.Height ?? intrinsicOuterHeight.MaybeClamp(minSize.Height, maxSize.Height))
            .MaybeMax(paddingBorderSize.Height);
        var finalOuterSize = new Size<float>(containerOuterWidth, containerOuterHeight);

        // Apply `align-content` to in-flow non-floated items if requested. For block layout the
        // entire stack of in-flow children is a single alignment subject, so the distribution
        // keywords must invoke the single-subject fallback unconditionally (num_items = 1).
        if (alignContent.HasValue)
        {
            float containerInnerHeight = containerOuterHeight - resolvedContentBoxInset.VerticalAxisSum();
            float inflowContentHeight = intrinsicOuterHeight - resolvedContentBoxInset.VerticalAxisSum();
            float freeSpace = containerInnerHeight - inflowContentHeight;

            bool anyInFlow = false;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].FinalLayout.HasValue)
                {
                    anyInFlow = true;
                    break;
                }
            }

            if (anyInFlow)
            {
                var keyword = CommonAlignment.ApplyAlignmentFallback(freeSpace, 1, alignContent.Value);
                float groupOffset = CommonAlignment.ComputeAlignmentOffset(freeSpace, 1, 0.0f, keyword, false, true);
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].FinalLayout is { } layout)
                    {
                        layout.Location.Y += groupOffset;
                        items[i].FinalLayout = layout;
                    }
                }

                inflowContentSize = GeometryExtensions.SizeZero;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.FinalLayout is { } layout)
                    {
                        inflowContentSize = inflowContentSize.F32Max(
                            ContentSizeHelper.ComputeContentSizeContribution(
                                layout.Location.Add(new Point<float>(
                                    -resolvedContentBoxInset.Left, -resolvedContentBoxInset.Top)),
                                layout.Size,
                                layout.ContentSize,
                                item.Overflow));
                    }
                }
            }
        }

        // Margin-collapsing metadata is part of a block's intrinsic contribution, not only its final
        // child placement, so it must survive ComputeSize.
        bool allInFlowChildrenCanBeCollapsedThrough = true;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Position != Position.Absolute && !item.CanBeCollapsedThrough)
            {
                allInFlowChildrenCanBeCollapsedThrough = false;
                break;
            }
        }

        bool canBeCollapsedThrough =
            !hasStylesPreventingBeingCollapsedThrough && allInFlowChildrenCanBeCollapsedThrough;

        var topMargin = ownMarginsCollapseWithChildren.Start
            ? firstChildTopMarginSet
            : CollapsibleMarginSet.FromMargin(rawMargin.Top.ResolveOrZero(parentSize.Width, calc));
        var bottomMargin = ownMarginsCollapseWithChildren.End
            ? lastChildBottomMarginSet
            : CollapsibleMarginSet.FromMargin(rawMargin.Bottom.ResolveOrZero(parentSize.Width, calc));

        // Short-circuit if computing size
        if (runMode == RunMode.ComputeSize)
        {
            var sizeOnly = LayoutOutput.FromOuterSize(finalOuterSize);
            sizeOnly.TopMargin = topMargin;
            sizeOnly.BottomMargin = bottomMargin;
            sizeOnly.MarginsCanCollapseThrough = canBeCollapsedThrough;
            return sizeOnly;
        }

        // Commit deferred in-flow layouts to the tree. Floated items already wrote their own.
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].FinalLayout is { } layout)
            {
                tree.SetUnroundedLayout(items[i].NodeId, in layout);
            }
        }

        // 4. Layout absolutely positioned children
        var absolutePositionInset = resolvedBorder.Add(scrollbarGutter);
        var absolutePositionArea = finalOuterSize.Sub(absolutePositionInset.SumAxes());
        var absolutePositionOffset = new Point<float>(absolutePositionInset.Left, absolutePositionInset.Top);
        var absoluteContentSize = PerformAbsoluteLayoutOnAbsoluteChildren(
            tree, items, absolutePositionArea, absolutePositionOffset, direction);

        // 5. Perform hidden layout on hidden children
        int len = tree.ChildCount(nodeId);
        for (int order = 0; order < len; order++)
        {
            var child = tree.GetChildId(nodeId, order);
            var childStyle = tree.GetBlockChildStyle(child);
            if (childStyle.BoxGenerationMode == BoxGenerationMode.None)
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

        var contentSize = inflowContentSize.F32Max(absoluteContentSize);

        return new LayoutOutput
        {
            Size = finalOuterSize,
            ContentSize = contentSize,
            FirstBaselines = GeometryExtensions.PointNone,
            TopMargin = topMargin,
            BottomMargin = bottomMargin,
            MarginsCanCollapseThrough = canBeCollapsedThrough,
        };
    }

    /// <summary>Create the list of <see cref="BlockItem"/>s for the children of the current node.</summary>
    private static List<BlockItem> GenerateItemList(
        ILayoutBlockContainer tree,
        NodeId node,
        Size<float?> nodeInnerSize)
    {
        var calc = tree.CalcResolver();
        var items = new List<BlockItem>();
        uint order = 0;

        foreach (var childNodeId in tree.ChildIds(node))
        {
            var childStyle = tree.GetBlockChildStyle(childNodeId);
            if (childStyle.BoxGenerationMode == BoxGenerationMode.None)
            {
                continue;
            }

            float? aspectRatio = childStyle.AspectRatio;
            var padding = childStyle.Padding.ResolveOrZero(nodeInnerSize, calc);
            var border = childStyle.Border.ResolveOrZero(nodeInnerSize, calc);
            var pbSum = padding.Add(border).SumAxes();
            var boxSizingAdjustment =
                childStyle.BoxSizing == BoxSizing.ContentBox ? pbSum : GeometryExtensions.SizeZero;

            var position = childStyle.Position;
            var overflow = childStyle.Overflow;

            var floatStyle = childStyle.Float;
            bool isNotFloated = floatStyle == Float.None;

            bool isBlock = childStyle.IsBlock;
            bool isTable = childStyle.IsTable;
            bool isScrollContainer = overflow.X.IsScrollContainer() || overflow.Y.IsScrollContainer();

            bool isInSameBfc = isBlock && !isTable && position != Position.Absolute && isNotFloated
                && !isScrollContainer;

            items.Add(new BlockItem
            {
                NodeId = childNodeId,
                Order = order,
                IsTable = isTable,
                IsInSameBfc = isInSameBfc,
                Float = floatStyle,
                Clear = childStyle.Clear,
                Size = childStyle.Size
                    .MaybeResolve(nodeInnerSize, calc)
                    .MaybeApplyAspectRatio(aspectRatio)
                    .MaybeAdd(boxSizingAdjustment),
                MinSize = childStyle.MinSize
                    .MaybeResolve(nodeInnerSize, calc)
                    .MaybeApplyAspectRatio(aspectRatio)
                    .MaybeAdd(boxSizingAdjustment),
                MaxSize = childStyle.MaxSize
                    .MaybeResolve(nodeInnerSize, calc)
                    .MaybeApplyAspectRatio(aspectRatio)
                    .MaybeAdd(boxSizingAdjustment),
                Overflow = overflow,
                ScrollbarWidth = childStyle.ScrollbarWidth,
                Position = position,
                Inset = childStyle.Inset,
                Margin = childStyle.Margin,
                Padding = padding,
                Border = border,
                PaddingBorderSum = pbSum,
                ComputedSize = GeometryExtensions.SizeZero,
                StaticPosition = GeometryExtensions.PointZero,
                CanBeCollapsedThrough = false,
                FinalLayout = null,
            });

            order += 1;
        }

        return items;
    }

    /// <summary>Compute the content-based width when the width of the container is not known.</summary>
    private static float DetermineContentBasedContainerWidth(
        ILayoutPartialTree tree,
        List<BlockItem> items,
        AvailableSpace availableWidth)
    {
        var calc = tree.CalcResolver();
        var availableSpace = new Size<AvailableSpace>(availableWidth, AvailableSpace.MinContent);

        float maxChildWidth = 0.0f;
        var floatContribution = new FloatIntrinsicWidthCalculator(availableWidth);

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Position == Position.Absolute)
            {
                continue;
            }

            var knownDimensions = item.Size.MaybeClamp(item.MinSize, item.MaxSize);

            float itemXMarginSum = item.Margin
                .ResolveOrZero(availableSpace.Width.IntoOption(), calc)
                .HorizontalAxisSum();

            float width = knownDimensions.Width ?? tree.MeasureChildSize(
                item.NodeId,
                knownDimensions,
                GeometryExtensions.SizeNone,
                availableSpace.MapWidth(w => w.MaybeSub(itemXMarginSum)),
                SizingMode.InherentSize,
                AbsoluteAxis.Horizontal,
                GeometryExtensions.LineTrue);

            width = Sys.F32Max(width, item.PaddingBorderSum.Width) + itemXMarginSum;

            var floatDirection = item.Float.FloatDirection();
            if (floatDirection.HasValue)
            {
                floatContribution.AddFloat(width, floatDirection.Value, item.Clear);
                continue;
            }

            maxChildWidth = Sys.F32Max(maxChildWidth, width);
        }

        maxChildWidth = Sys.F32Max(maxChildWidth, floatContribution.Result());

        return maxChildWidth;
    }

    /// <summary>Compute each child's final size and position.</summary>
    private static (Size<float> InflowContentSize, float ContentHeight, CollapsibleMarginSet FirstTopMargin,
        CollapsibleMarginSet LastBottomMargin) PerformFinalLayoutOnInFlowChildren(
        ILayoutBlockContainer tree,
        RunMode runMode,
        List<BlockItem> items,
        float containerOuterWidth,
        float? containerPercentageResolutionHeight,
        Rect<float> contentBoxInset,
        Rect<float> resolvedContentBoxInset,
        TextAlign textAlign,
        Direction direction,
        Line<bool> ownMarginsCollapseWithChildren,
        BlockContext blockCtx)
    {
        var calc = tree.CalcResolver();

        float containerInnerWidth = containerOuterWidth - resolvedContentBoxInset.HorizontalAxisSum();
        containerPercentageResolutionHeight =
            containerPercentageResolutionHeight.MaybeSub(resolvedContentBoxInset.VerticalAxisSum());
        var parentSize = new Size<float?>(containerInnerWidth, containerPercentageResolutionHeight);

        // Vertical available space in block flow is indefinite, NOT a min-content constraint:
        // MaxContent is taffy's representation of "indefinite".
        var availableSpace = new Size<AvailableSpace>(
            AvailableSpace.Definite(containerInnerWidth), AvailableSpace.MaxContent);

        if (blockCtx.IsBfcRoot)
        {
            blockCtx.SetWidth(containerOuterWidth);
            blockCtx.ApplyContentBoxInset(resolvedContentBoxInset.Left, resolvedContentBoxInset.Right);
        }

        var inflowContentSize = GeometryExtensions.SizeZero;
        float committedYOffset = resolvedContentBoxInset.Top;
        float yOffsetForAbsolute = resolvedContentBoxInset.Top;
        var firstChildTopMarginSet = CollapsibleMarginSet.Zero;
        var activeCollapsibleMarginSet = CollapsibleMarginSet.Zero;
        bool isCollapsingWithFirstMarginSet = true;

        bool hasActiveFloats = blockCtx.HasActiveFloats(committedYOffset);
        float yOffsetForFloat = resolvedContentBoxInset.Top;

        for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var item = items[itemIndex];
            if (item.Position == Position.Absolute)
            {
                float staticX = direction == Direction.Ltr
                    ? resolvedContentBoxInset.Left
                    : containerOuterWidth - resolvedContentBoxInset.Right;
                item.StaticPosition = new Point<float>(staticX, yOffsetForAbsolute);
                continue;
            }

            var itemMargin = item.Margin.Map(m => m.ResolveToOption(containerOuterWidth, calc));
            var itemNonAutoMargin = itemMargin.Map(static m => m ?? 0.0f);
            float itemNonAutoXMarginSum = itemNonAutoMargin.HorizontalAxisSum();

            var scrollbarSize = new Size<float>(
                item.Overflow.Y == Overflow.Scroll ? item.ScrollbarWidth : 0.0f,
                item.Overflow.X == Overflow.Scroll ? item.ScrollbarWidth : 0.0f);

            // Handle floated boxes
            var itemFloatDirection = item.Float.FloatDirection();
            if (itemFloatDirection.HasValue)
            {
                hasActiveFloats = true;

                var floatLayout = tree.PerformChildLayout(
                    item.NodeId,
                    GeometryExtensions.SizeNone,
                    parentSize,
                    GeometryExtensions.SizeMaxContent,
                    SizingMode.InherentSize,
                    GeometryExtensions.LineTrue);
                var marginBox = floatLayout.Size.Add(itemNonAutoMargin.SumAxes());

                var floatLocation = blockCtx.PlaceFloatedBox(
                    marginBox, yOffsetForFloat, itemFloatDirection.Value, item.Clear);

                // Convert the margin-box location returned by float placement into a border-box
                // location for the output Layout
                floatLocation.Y += itemNonAutoMargin.Top;
                floatLocation.X += itemNonAutoMargin.Left;

                var floatFinalLayout = new Layout
                {
                    Order = item.Order,
                    Size = floatLayout.Size,
                    ContentSize = floatLayout.ContentSize,
                    ScrollbarSize = scrollbarSize,
                    Location = floatLocation,
                    Padding = item.Padding,
                    Border = item.Border,
                    Margin = itemNonAutoMargin,
                };
                tree.SetUnroundedLayout(item.NodeId, in floatFinalLayout);

                inflowContentSize = inflowContentSize.F32Max(
                    ContentSizeHelper.ComputeContentSizeContribution(
                        floatLocation, floatLayout.Size, floatLayout.ContentSize, item.Overflow));

                continue;
            }

            // Handle non-floated boxes
            float yMarginOffset = 0.0f;
            float stretchWidth;
            Point<float> floatAvoidingPosition;
            float floatAvoidingWidth;

            if (item.IsInSameBfc)
            {
                stretchWidth = containerInnerWidth - itemNonAutoXMarginSum;
                floatAvoidingPosition = new Point<float>(0.0f, 0.0f);
                floatAvoidingWidth = 0.0f;
            }
            else
            {
                if (!isCollapsingWithFirstMarginSet || !ownMarginsCollapseWithChildren.Start)
                {
                    yMarginOffset = activeCollapsibleMarginSet
                        .CollapseWithMargin(itemNonAutoMargin.Top)
                        .Resolve();
                }

                float minY = committedYOffset + yMarginOffset;

                if (hasActiveFloats)
                {
                    var slot = blockCtx.FindContentSlot(minY, item.Clear, null);
                    hasActiveFloats = slot.SegmentId.HasValue;
                    stretchWidth = slot.Width - itemNonAutoXMarginSum;
                    floatAvoidingPosition = new Point<float>(slot.X, slot.Y);
                    floatAvoidingWidth = slot.Width;
                }
                else
                {
                    stretchWidth = containerInnerWidth - itemNonAutoXMarginSum;
                    floatAvoidingPosition = new Point<float>(resolvedContentBoxInset.Left, minY);
                    floatAvoidingWidth = containerInnerWidth;
                }
            }

            Size<float?> knownDimensions;
            if (item.IsTable)
            {
                knownDimensions = GeometryExtensions.SizeNone;
            }
            else
            {
                float capturedStretchWidth = stretchWidth;
                var itemMinWidth = item.MinSize.Width;
                var itemMaxWidth = item.MaxSize.Width;
                knownDimensions = item.Size
                    .MapWidth(width => (width ?? capturedStretchWidth).MaybeClamp(itemMinWidth, itemMaxWidth))
                    .MaybeClamp(item.MinSize, item.MaxSize);
            }

            var childInputs = new LayoutInput
            {
                RunMode = runMode,
                SizingMode = SizingMode.InherentSize,
                Axis = RequestedAxis.Both,
                KnownDimensions = knownDimensions,
                ParentSize = parentSize,
                AvailableSpace = availableSpace.MapWidth(_ => AvailableSpace.Definite(stretchWidth)),
                VerticalMarginsAreCollapsible =
                    item.IsInSameBfc ? GeometryExtensions.LineTrue : GeometryExtensions.LineFalse,
            };

            float clearPos = blockCtx.ClearedThreshold(item.Clear) ?? 0.0f;

            LayoutOutput itemLayout;
            if (item.IsInSameBfc)
            {
                float width = knownDimensions.Width
                    ?? throw new InvalidOperationException(
                        "Same-bfc child will always have defined width due to stretch sizing");

                // TODO: account for auto margins
                float insetLeft = itemNonAutoMargin.Left + contentBoxInset.Left;
                float insetRight = containerOuterWidth - width - insetLeft;

                var childBlockCtx = blockCtx.SubContext(
                    Sys.F32Max(yOffsetForAbsolute + itemNonAutoMargin.Top, clearPos), insetLeft, insetRight);
                itemLayout = tree.ComputeBlockChildLayout(item.NodeId, childInputs, childBlockCtx);

                float childContribution = childBlockCtx.FloatedContentHeightContribution();
                blockCtx.AddChildFloatedContentHeightContribution(yOffsetForAbsolute + childContribution);
            }
            else
            {
                itemLayout = tree.ComputeChildLayout(item.NodeId, childInputs);
            }

            var finalSize = itemLayout.Size;

            var topMarginSet = itemLayout.TopMargin.CollapseWithMargin(itemMargin.Top ?? 0.0f);
            var bottomMarginSet = itemLayout.BottomMargin.CollapseWithMargin(itemMargin.Bottom ?? 0.0f);

            // Expand auto margins to fill available space. Vertical auto-margins for relatively
            // positioned block items simply resolve to 0.
            float freeXSpace = Sys.F32Max(0.0f, stretchWidth - finalSize.Width);
            int autoMarginCount = (itemMargin.Left.HasValue ? 0 : 1) + (itemMargin.Right.HasValue ? 0 : 1);
            float xAxisAutoMarginSize = autoMarginCount > 0 ? freeXSpace / autoMarginCount : 0.0f;

            var resolvedMargin = new Rect<float>(
                itemMargin.Left ?? xAxisAutoMarginSize,
                itemMargin.Right ?? xAxisAutoMarginSize,
                topMarginSet.Resolve(),
                bottomMarginSet.Resolve());

            // Resolve item inset
            var inset = item.Inset.ZipSize(
                new Size<float>(containerInnerWidth, 0.0f),
                (p, s) => p.MaybeResolve(s, calc));
            float? negatedRight = inset.Right.HasValue ? -inset.Right.Value : null;
            float? negatedBottom = inset.Bottom.HasValue ? -inset.Bottom.Value : null;
            float insetOffsetX = direction.IsRtl()
                ? negatedRight ?? inset.Left ?? 0.0f
                : inset.Left ?? negatedRight ?? 0.0f;
            var insetOffset = new Point<float>(insetOffsetX, inset.Top ?? negatedBottom ?? 0.0f);

            // Set y_margin_offset (same bfc child)
            if (item.IsInSameBfc
                && (!isCollapsingWithFirstMarginSet || !ownMarginsCollapseWithChildren.Start))
            {
                yMarginOffset = activeCollapsibleMarginSet.CollapseWithMargin(resolvedMargin.Top).Resolve();
            }

            bool floatOrNotClear = item.Float.IsFloated() || item.Clear == Clear.None;

            item.ComputedSize = itemLayout.Size;
            item.CanBeCollapsedThrough = itemLayout.MarginsCanCollapseThrough && floatOrNotClear;
            if (item.IsInSameBfc)
            {
                float unclearedY = committedYOffset + activeCollapsibleMarginSet.Resolve();
                item.StaticPosition = new Point<float>(
                    direction == Direction.Ltr
                        ? resolvedContentBoxInset.Left
                        : containerOuterWidth - resolvedContentBoxInset.Right - finalSize.Width,
                    Sys.F32Max(unclearedY, clearPos));
            }
            else
            {
                // TODO: handle inset and margins
                item.StaticPosition = new Point<float>(
                    direction == Direction.Ltr
                        ? floatAvoidingPosition.X
                        : floatAvoidingPosition.X + floatAvoidingWidth - finalSize.Width,
                    floatAvoidingPosition.Y);
            }

            Point<float> location;
            if (item.IsInSameBfc)
            {
                location = new Point<float>(
                    direction == Direction.Ltr
                        ? resolvedContentBoxInset.Left + insetOffset.X + resolvedMargin.Left
                        : containerOuterWidth
                            - resolvedContentBoxInset.Right
                            - finalSize.Width
                            - resolvedMargin.Right
                            + insetOffset.X,
                    Sys.F32Max(committedYOffset, clearPos) + yMarginOffset + insetOffset.Y);
            }
            else
            {
                // TODO: handle inset and margins
                location = new Point<float>(
                    direction == Direction.Ltr
                        ? floatAvoidingPosition.X + resolvedMargin.Left + insetOffset.X
                        : floatAvoidingPosition.X + floatAvoidingWidth - finalSize.Width - resolvedMargin.Right
                            + insetOffset.X,
                    floatAvoidingPosition.Y + insetOffset.Y);
            }

            // Apply alignment
            float itemOuterWidth = itemLayout.Size.Width + resolvedMargin.HorizontalAxisSum();
            if (itemOuterWidth < containerInnerWidth)
            {
                float freeXSpaceForAlign = containerInnerWidth - itemOuterWidth;
                switch (textAlign)
                {
                    case TextAlign.Auto:
                        break;
                    case TextAlign.LegacyLeft:
                        if (direction == Direction.Rtl)
                        {
                            location.X -= freeXSpaceForAlign;
                        }

                        break;
                    case TextAlign.LegacyRight:
                        if (direction == Direction.Ltr)
                        {
                            location.X += freeXSpaceForAlign;
                        }

                        break;
                    case TextAlign.LegacyCenter:
                        location.X += direction == Direction.Ltr
                            ? freeXSpaceForAlign / 2.0f
                            : -(freeXSpaceForAlign / 2.0f);
                        break;
                    default:
                        break;
                }
            }

            // Defer SetUnroundedLayout to the post-loop pass in ComputeInner so that align-content
            // can shift Location.Y before the layout is committed to the tree.
            item.FinalLayout = new Layout
            {
                Order = item.Order,
                Size = itemLayout.Size,
                ContentSize = itemLayout.ContentSize,
                ScrollbarSize = scrollbarSize,
                Location = location,
                Padding = item.Padding,
                Border = item.Border,
                Margin = resolvedMargin,
            };

            inflowContentSize = inflowContentSize.F32Max(
                ContentSizeHelper.ComputeContentSizeContribution(
                    location.Add(new Point<float>(-resolvedContentBoxInset.Left, -resolvedContentBoxInset.Top)),
                    finalSize,
                    itemLayout.ContentSize,
                    item.Overflow));

            // Update first_child_top_margin_set
            if (isCollapsingWithFirstMarginSet)
            {
                if (item.CanBeCollapsedThrough)
                {
                    firstChildTopMarginSet = firstChildTopMarginSet
                        .CollapseWithSet(topMarginSet)
                        .CollapseWithSet(bottomMarginSet);
                }
                else
                {
                    firstChildTopMarginSet = firstChildTopMarginSet.CollapseWithSet(topMarginSet);
                    isCollapsingWithFirstMarginSet = false;
                }
            }

            // Update active_collapsible_margin_set
            if (item.CanBeCollapsedThrough)
            {
                activeCollapsibleMarginSet = activeCollapsibleMarginSet
                    .CollapseWithSet(topMarginSet)
                    .CollapseWithSet(bottomMarginSet);
                yOffsetForAbsolute = committedYOffset + itemLayout.Size.Height + yMarginOffset;
                yOffsetForFloat = committedYOffset + itemLayout.Size.Height + yMarginOffset;
            }
            else
            {
                committedYOffset = location.Y - insetOffset.Y + itemLayout.Size.Height;
                activeCollapsibleMarginSet = bottomMarginSet;
                yOffsetForAbsolute = committedYOffset + activeCollapsibleMarginSet.Resolve();
                yOffsetForFloat = committedYOffset;
            }
        }

        var lastChildBottomMarginSet = activeCollapsibleMarginSet;
        float bottomYMarginOffset =
            ownMarginsCollapseWithChildren.End ? 0.0f : lastChildBottomMarginSet.Resolve();

        committedYOffset += resolvedContentBoxInset.Bottom + bottomYMarginOffset;
        float contentHeight = Sys.F32Max(0.0f, committedYOffset);
        return (inflowContentSize, contentHeight, firstChildTopMarginSet, lastChildBottomMarginSet);
    }

    /// <summary>Perform absolute layout on all absolutely positioned children.</summary>
    private static Size<float> PerformAbsoluteLayoutOnAbsoluteChildren(
        ILayoutBlockContainer tree,
        List<BlockItem> items,
        Size<float> areaSize,
        Point<float> areaOffset,
        Direction direction)
    {
        var calc = tree.CalcResolver();
        float areaWidth = areaSize.Width;
        float areaHeight = areaSize.Height;

        var absoluteContentSize = GeometryExtensions.SizeZero;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Position != Position.Absolute)
            {
                continue;
            }

            var childStyle = tree.GetBlockChildStyle(item.NodeId);

            // Skip items that are display:none or are not position:absolute
            if (childStyle.BoxGenerationMode == BoxGenerationMode.None
                || childStyle.Position != Position.Absolute)
            {
                continue;
            }

            float? aspectRatio = childStyle.AspectRatio;
            var margin = childStyle.Margin.Map(m => m.ResolveToOption(areaWidth, calc));
            var padding = childStyle.Padding.ResolveOrZero((float?)areaWidth, calc);
            var border = childStyle.Border.ResolveOrZero((float?)areaWidth, calc);
            var paddingBorderSum = padding.Add(border).SumAxes();
            var boxSizingAdjustment =
                childStyle.BoxSizing == BoxSizing.ContentBox ? paddingBorderSum : GeometryExtensions.SizeZero;

            // Resolve inset
            float? left = childStyle.Inset.Left.MaybeResolve(areaWidth, calc);
            float? right = childStyle.Inset.Right.MaybeResolve(areaWidth, calc);
            float? top = childStyle.Inset.Top.MaybeResolve(areaHeight, calc);
            float? bottom = childStyle.Inset.Bottom.MaybeResolve(areaHeight, calc);

            // Compute known dimensions from min/max/inherent size styles
            var styleSize = childStyle.Size
                .MaybeResolve(areaSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment);
            var minSize = childStyle.MinSize
                .MaybeResolve(areaSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment)
                .Or(paddingBorderSum.AsOptions())
                .MaybeMax(paddingBorderSum);
            var maxSize = childStyle.MaxSize
                .MaybeResolve(areaSize, calc)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment);
            var knownDimensions = styleSize.MaybeClamp(minSize, maxSize);

            // Fill in width from left/right and reapply aspect ratio
            if (!knownDimensions.Width.HasValue && left.HasValue && right.HasValue)
            {
                float newWidthRaw = areaWidth.MaybeSub(margin.Left).MaybeSub(margin.Right)
                    - left.Value - right.Value;
                knownDimensions.Width = Sys.F32Max(newWidthRaw, 0.0f);
                knownDimensions = knownDimensions.MaybeApplyAspectRatio(aspectRatio).MaybeClamp(minSize, maxSize);
            }

            // Fill in height from top/bottom and reapply aspect ratio
            if (!knownDimensions.Height.HasValue && top.HasValue && bottom.HasValue)
            {
                float newHeightRaw = areaHeight.MaybeSub(margin.Top).MaybeSub(margin.Bottom)
                    - top.Value - bottom.Value;
                knownDimensions.Height = Sys.F32Max(newHeightRaw, 0.0f);
                knownDimensions = knownDimensions.MaybeApplyAspectRatio(aspectRatio).MaybeClamp(minSize, maxSize);
            }

            var childAvailableSpace = new Size<AvailableSpace>(
                AvailableSpace.Definite(areaWidth.MaybeClamp(minSize.Width, maxSize.Width)),
                AvailableSpace.Definite(areaHeight.MaybeClamp(minSize.Height, maxSize.Height)));

            var measuredSize = tree.MeasureChildSizeBoth(
                item.NodeId,
                knownDimensions,
                areaSize.AsOptions(),
                childAvailableSpace,
                SizingMode.ContentSize,
                GeometryExtensions.LineFalse);

            var finalSize = knownDimensions.UnwrapOr(measuredSize).MaybeClamp(minSize, maxSize);

            var layoutOutput = tree.PerformChildLayout(
                item.NodeId,
                finalSize.AsOptions(),
                areaSize.AsOptions(),
                childAvailableSpace,
                SizingMode.ContentSize,
                GeometryExtensions.LineFalse);

            var nonAutoMargin = new Rect<float>(
                left.HasValue ? margin.Left ?? 0.0f : 0.0f,
                right.HasValue ? margin.Right ?? 0.0f : 0.0f,
                top.HasValue ? margin.Top ?? 0.0f : 0.0f,
                bottom.HasValue ? margin.Bottom ?? 0.0f : 0.0f);

            // Expand auto margins to fill available space.
            // Auto margins for absolutely positioned elements in block containers only resolve if
            // inset is set. Otherwise they resolve to 0.
            var absoluteAutoMarginSpace = new Point<float>(
                right.HasValue ? areaSize.Width - right.Value - (left ?? 0.0f) : finalSize.Width,
                bottom.HasValue ? areaSize.Height - bottom.Value - (top ?? 0.0f) : finalSize.Height);
            var freeSpace = new Size<float>(
                absoluteAutoMarginSpace.X - finalSize.Width - nonAutoMargin.HorizontalAxisSum(),
                absoluteAutoMarginSpace.Y - finalSize.Height - nonAutoMargin.VerticalAxisSum());

            float autoMarginWidth;
            {
                int autoMarginCount = (margin.Left.HasValue ? 0 : 1) + (margin.Right.HasValue ? 0 : 1);
                if (autoMarginCount == 2
                    && (!styleSize.Width.HasValue || styleSize.Width.Value >= freeSpace.Width))
                {
                    autoMarginWidth = 0.0f;
                }
                else if (autoMarginCount > 0)
                {
                    autoMarginWidth = freeSpace.Width / autoMarginCount;
                }
                else
                {
                    autoMarginWidth = 0.0f;
                }
            }

            float autoMarginHeight;
            {
                int autoMarginCount = (margin.Top.HasValue ? 0 : 1) + (margin.Bottom.HasValue ? 0 : 1);
                if (autoMarginCount == 2
                    && (!styleSize.Height.HasValue || styleSize.Height.Value >= freeSpace.Height))
                {
                    autoMarginHeight = 0.0f;
                }
                else if (autoMarginCount > 0)
                {
                    autoMarginHeight = freeSpace.Height / autoMarginCount;
                }
                else
                {
                    autoMarginHeight = 0.0f;
                }
            }

            var autoMargin = new Rect<float>(
                margin.Left.HasValue ? 0.0f : autoMarginWidth,
                margin.Right.HasValue ? 0.0f : autoMarginWidth,
                margin.Top.HasValue ? 0.0f : autoMarginHeight,
                margin.Bottom.HasValue ? 0.0f : autoMarginHeight);

            var resolvedMargin = new Rect<float>(
                margin.Left ?? autoMargin.Left,
                margin.Right ?? autoMargin.Right,
                margin.Top ?? autoMargin.Top,
                margin.Bottom ?? autoMargin.Bottom);

            float xOffset;
            if (left.HasValue && right.HasValue)
            {
                xOffset = direction.IsRtl()
                    ? areaSize.Width - finalSize.Width - right.Value - resolvedMargin.Right
                    : left.Value + resolvedMargin.Left;
            }
            else if (left.HasValue)
            {
                xOffset = left.Value + resolvedMargin.Left;
            }
            else if (right.HasValue)
            {
                xOffset = areaSize.Width - finalSize.Width - right.Value - resolvedMargin.Right;
            }
            else
            {
                xOffset = direction.IsRtl()
                    ? item.StaticPosition.X - finalSize.Width - resolvedMargin.Right - areaOffset.X
                    : item.StaticPosition.X + resolvedMargin.Left - areaOffset.X;
            }

            float? yFromInset = top.HasValue
                ? top.Value + resolvedMargin.Top
                : bottom.HasValue
                    ? areaSize.Height - finalSize.Height - bottom.Value - resolvedMargin.Bottom
                    : null;
            float locationY = yFromInset.HasValue
                ? yFromInset.Value + areaOffset.Y
                : item.StaticPosition.Y + resolvedMargin.Top;

            var location = new Point<float>(xOffset + areaOffset.X, locationY);

            // Note: axes intentionally switched here as scrollbars take up space in the opposite
            // axis to the axis in which scrolling is enabled.
            var scrollbarSize = new Size<float>(
                item.Overflow.Y == Overflow.Scroll ? item.ScrollbarWidth : 0.0f,
                item.Overflow.X == Overflow.Scroll ? item.ScrollbarWidth : 0.0f);

            var absLayout = new Layout
            {
                Order = item.Order,
                Size = finalSize,
                ContentSize = layoutOutput.ContentSize,
                ScrollbarSize = scrollbarSize,
                Location = location,
                Padding = padding,
                Border = border,
                Margin = resolvedMargin,
            };
            tree.SetUnroundedLayout(item.NodeId, in absLayout);

            var relativeLocation = new Point<float>(location.X - areaOffset.X, location.Y - areaOffset.Y);
            absoluteContentSize = absoluteContentSize.F32Max(
                ContentSizeHelper.ComputeContentSizeContribution(
                    relativeLocation, finalSize, layoutOutput.ContentSize, item.Overflow));
        }

        return absoluteContentSize;
    }
}
