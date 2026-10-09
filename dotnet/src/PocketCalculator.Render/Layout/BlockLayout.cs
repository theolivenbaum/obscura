// Port of vendor/taffy/src/compute/block.rs
//
// Computes the CSS block layout algorithm in the case that the block container
// being laid out contains only block-level boxes.
namespace PocketCalculator.Render.Layout;

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

    /// <summary>
    /// The float exclusions an inline formatting context in this block lays its lines out
    /// around, relative to this block's content box, whose edges sit the given distances inside
    /// its border box. <c>null</c> when no float reaches the content box.
    /// </summary>
    public FloatBands? FloatBandsFor(float contentTop, float contentLeft, float contentRight) =>
        _bfc.FloatContext.Bands(_yOffset + contentTop, _insetLeft + contentLeft, _insetRight + contentRight);

    /// <summary>
    /// The float-free slot for a float-avoiding box (a block formatting context root or a
    /// replaced element) whose border box would span <c>[y, y + height)</c> in this block:
    /// its left edge relative to this block's border box, and its width.
    /// </summary>
    public (float X, float Width) SlotOver(float y, float height)
    {
        (float left, float right) = _bfc.FloatContext.InsetsOver(
            y + _yOffset, height, _contentBoxInsetLeft, _contentBoxInsetRight);
        return (left - _insetLeft, _bfc.FloatContext.AvailableWidth - left - right);
    }

    /// <summary>The next float edge below <paramref name="y"/> (this block's coordinates), or <c>null</c>.</summary>
    public float? NextFloatEdgeBelow(float y) =>
        _bfc.FloatContext.NextEdgeBelow(y + _yOffset) is { } edge ? edge - _yOffset : null;

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

        // DEVIATION from vendor/taffy/src/compute/block.rs, which falls back to
        // `min_size.height` as the children's percentage basis whenever this box has no
        // resolved height of its own. `min-height: 0` is the initial value and says nothing
        // about the used height, so that fallback hands every `height: %` child a basis of
        // 0 and collapses it, where Chromium sizes the box from its content and resolves the
        // percentage against that. Only a POSITIVE minimum carries information - the box will
        // be at least that tall - so a zero one now leaves the basis unresolved, which makes
        // the child content-sized, the same answer Chromium computes.
        // The reference never reached this: crates/obscura-render/src/dom.rs rewrites every
        // percentage height under an indefinite box to `auto`, so no percentage survived to
        // be resolved here. Marking a flex-sized box a definite containing block (the
        // Flexbox 9.8 deviation in Dom/LayoutDomComputed.cs) is what lets them through, and
        // that exposed this. Curiosity Workspace's `#/manage/configure/subscription` nests
        // `height: 100%; min-height: 0` twice under an auto-height flex item; the inner
        // `overflow: hidden auto` stack came out 24px tall instead of 473px and the route
        // rendered blank. See "Known deviations" in todo.md.
        float? percentageResolutionMinHeight =
            minSize.Height is { } minimumHeight && minimumHeight > 0.0f ? minimumHeight : null;
        var containerPercentageResolutionHeight =
            knownDimensions.Height
            ?? size.Height.MaybeMax(minSize.Height)
            ?? percentageResolutionMinHeight;

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
            // The floats' margin boxes end inside the content box; vendor/taffy compared them
            // with the border-box height and so lost the bottom padding and border.
            intrinsicOuterHeight = Sys.F32Max(
                intrinsicOuterHeight,
                blockCtx.FloatedContentHeightContribution()
                    + (tree.HasFloats && blockCtx.HasFloats ? resolvedContentBoxInset.Bottom : 0.0f));
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
            // A float is out of flow: a block holding nothing but floats is still empty, and its
            // margins collapse through it (CSS 2.1 8.3.1). vendor/taffy counted floats here.
            if (item.Position != Position.Absolute && !item.CanBeCollapsedThrough
                && !(tree.HasFloats && item.Float.IsFloated()))
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
                && !isScrollContainer && !childStyle.EstablishesBfc;

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

        // Floats before an in-flow box share its line in a max-content layout (Chromium's
        // block min/max sizes add them to the next in-flow child until a clear). vendor/taffy
        // took the max of the floats and the in-flow content instead.
        var blockTree = tree as ILayoutBlockContainer;
        bool addFloats = blockTree is { HasFloats: true } && availableWidth.Kind != AvailableSpaceKind.MinContent;
        float floatsLeft = 0.0f;
        float floatsRight = 0.0f;

        // The floats anchored in the last inline formatting context share its line too.
        (NodeId Float, int Offset)[]? lineAnchors = null;
        float lineWidth = 0.0f;

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
            if (addFloats)
            {
                if (item.Clear is Clear.Left or Clear.Both)
                {
                    floatsLeft = 0.0f;
                }

                if (item.Clear is Clear.Right or Clear.Both)
                {
                    floatsRight = 0.0f;
                }
            }

            if (floatDirection.HasValue)
            {
                floatContribution.AddFloat(width, floatDirection.Value, item.Clear);
                if (addFloats && lineAnchors is not null && Array.Exists(lineAnchors, a => a.Float == item.NodeId))
                {
                    lineWidth += width;
                    if (availableWidth.Kind == AvailableSpaceKind.Definite)
                    {
                        lineWidth = Sys.F32Min(lineWidth, Sys.F32Max(availableWidth.Unwrap(), 0.0f));
                    }

                    maxChildWidth = Sys.F32Max(maxChildWidth, lineWidth);
                    continue;
                }

                if (addFloats)
                {
                    if (floatDirection.Value == FloatDirection.Left)
                    {
                        floatsLeft += width;
                    }
                    else
                    {
                        floatsRight += width;
                    }
                }

                continue;
            }

            if (addFloats)
            {
                width += floatsLeft + floatsRight;
                if (floatsLeft + floatsRight > 0.0f && availableWidth.Kind == AvailableSpaceKind.Definite)
                {
                    width = Sys.F32Min(width, Sys.F32Max(availableWidth.Unwrap(), 0.0f));
                }

                lineAnchors = blockTree!.InlineFloatAnchors(item.NodeId);
                lineWidth = width;
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

        // Floats anchored in an inline formatting context, placed while laying that out.
        HashSet<NodeId>? placedAnchors = null;

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

                // A float anchored in an earlier inline formatting context was placed with it.
                if (placedAnchors is not null && placedAnchors.Contains(item.NodeId))
                {
                    continue;
                }

                inflowContentSize = inflowContentSize.F32Max(PlaceFloat(
                    tree,
                    item,
                    blockCtx,
                    parentSize,
                    containerInnerWidth,
                    containerOuterWidth,
                    yOffsetForFloat,
                    itemFloatDirection.Value));
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

                if (hasActiveFloats && tree.HasFloats)
                {
                    // Placed by PlaceFloatAvoidingItem below, which needs the box's size.
                    stretchWidth = containerInnerWidth - itemNonAutoXMarginSum;
                    floatAvoidingPosition = new Point<float>(resolvedContentBoxInset.Left, minY);
                    floatAvoidingWidth = containerInnerWidth;
                }
                else if (hasActiveFloats)
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

                float childTop = tree.HasFloats
                    ? SameBfcChildTop(
                        tree,
                        item,
                        itemNonAutoMargin.Top,
                        containerInnerWidth,
                        committedYOffset,
                        activeCollapsibleMarginSet,
                        isCollapsingWithFirstMarginSet && ownMarginsCollapseWithChildren.Start,
                        clearPos)
                    : Sys.F32Max(yOffsetForAbsolute + itemNonAutoMargin.Top, clearPos);
                var childBlockCtx = blockCtx.SubContext(childTop, insetLeft, insetRight);
                if (tree.HasFloats && tree.InlineFloatAnchors(item.NodeId) is { } anchors)
                {
                    hasActiveFloats = true;
                    placedAnchors ??= [];
                    inflowContentSize = inflowContentSize.F32Max(PlaceAnchoredFloats(
                        tree,
                        item,
                        anchors,
                        items,
                        childInputs,
                        childBlockCtx,
                        blockCtx,
                        childTop,
                        parentSize,
                        containerInnerWidth,
                        containerOuterWidth,
                        placedAnchors));
                }

                itemLayout = tree.ComputeBlockChildLayout(item.NodeId, childInputs, childBlockCtx);

                float childContribution = childBlockCtx.FloatedContentHeightContribution();
                blockCtx.AddChildFloatedContentHeightContribution(
                    (tree.HasFloats ? childTop : yOffsetForAbsolute) + childContribution);
            }
            else if (hasActiveFloats && tree.HasFloats)
            {
                (itemLayout, stretchWidth, floatAvoidingPosition, floatAvoidingWidth) = PlaceFloatAvoidingItem(
                    tree,
                    item,
                    blockCtx,
                    childInputs,
                    floatAvoidingPosition.Y,
                    clearPos,
                    containerInnerWidth,
                    itemNonAutoMargin);
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
                    (tree.HasFloats
                        ? ClearedBorderTop(committedYOffset + yMarginOffset, item.Clear, clearPos)
                        : Sys.F32Max(committedYOffset, clearPos) + yMarginOffset) + insetOffset.Y);
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

                // DEVIATION from vendor/taffy/src/compute/block.rs, which places a following
                // float at the previous box's border edge. Chromium 141 (NextBorderEdge in
                // NGBlockLayoutAlgorithm) adds the pending margin: a float after a block with
                // `margin-bottom: 25px` sits 25px below it, level with the next block.
                yOffsetForFloat = tree.HasFloats ? yOffsetForAbsolute : committedYOffset;
            }
        }

        var lastChildBottomMarginSet = activeCollapsibleMarginSet;
        float bottomYMarginOffset =
            ownMarginsCollapseWithChildren.End ? 0.0f : lastChildBottomMarginSet.Resolve();

        committedYOffset += resolvedContentBoxInset.Bottom + bottomYMarginOffset;
        float contentHeight = Sys.F32Max(0.0f, committedYOffset);
        return (inflowContentSize, contentHeight, firstChildTopMarginSet, lastChildBottomMarginSet);
    }

    /// <summary>
    /// Lay a float out (shrink-to-fit unless it has a width) and place it in the BFC no higher
    /// than <paramref name="minY"/> (this block's coordinates, CSS 2.1 9.5.1). Returns its
    /// content-size contribution.
    /// </summary>
    private static Size<float> PlaceFloat(
        ILayoutBlockContainer tree,
        BlockItem item,
        BlockContext blockCtx,
        Size<float?> parentSize,
        float containerInnerWidth,
        float containerOuterWidth,
        float minY,
        FloatDirection direction)
    {
        var calc = tree.CalcResolver();
        var itemNonAutoMargin = item.Margin
            .Map(m => m.ResolveToOption(containerOuterWidth, calc))
            .Map(static m => m ?? 0.0f);
        float itemNonAutoXMarginSum = itemNonAutoMargin.HorizontalAxisSum();
        var scrollbarSize = new Size<float>(
            item.Overflow.Y == Overflow.Scroll ? item.ScrollbarWidth : 0.0f,
            item.Overflow.X == Overflow.Scroll ? item.ScrollbarWidth : 0.0f);

        var floatLayout = LayoutFloat(tree, item, parentSize, containerInnerWidth, itemNonAutoXMarginSum);
        var marginBox = floatLayout.Size.Add(itemNonAutoMargin.SumAxes());

        // A negative margin can make the margin box narrower than nothing; it still
        // occupies no space then, and its border box is placed from the margin edge.
        marginBox = new Size<float>(Sys.F32Max(marginBox.Width, 0.0f), Sys.F32Max(marginBox.Height, 0.0f));

        var floatLocation = blockCtx.PlaceFloatedBox(marginBox, minY, direction, item.Clear);

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

        return ContentSizeHelper.ComputeContentSizeContribution(
            floatLocation, floatLayout.Size, floatLayout.ContentSize, item.Overflow);
    }

    /// <summary>The float's size: its own width, or shrink-to-fit in the containing block.</summary>
    /// <remarks>
    /// DEVIATION from vendor/taffy/src/compute/block.rs, which lays a float out at max-content
    /// with collapsible margins. CSS 2.1 10.3.5: an auto-width float is shrink-to-fit against
    /// its containing block, and it establishes a BFC, so its margins never collapse with its
    /// children. Chromium 141 wraps a float holding a long paragraph at the containing block's
    /// width.
    /// </remarks>
    private static LayoutOutput LayoutFloat(
        ILayoutBlockContainer tree,
        BlockItem item,
        Size<float?> parentSize,
        float containerInnerWidth,
        float marginX)
    {
        float available = Sys.F32Max(containerInnerWidth - marginX, 0.0f);
        var floatAvailable = new Size<AvailableSpace>(AvailableSpace.Definite(available), AvailableSpace.MaxContent);
        var floatKnown = GeometryExtensions.SizeNone;
        if (!item.Size.Width.HasValue && tree.HasFloats)
        {
            floatKnown.Width = ShrinkToFitWidth(tree, item, parentSize, available);
        }

        return tree.PerformChildLayout(
            item.NodeId,
            floatKnown,
            parentSize,
            floatAvailable,
            SizingMode.InherentSize,
            GeometryExtensions.LineFalse);
    }

    /// <summary>
    /// How many anchored floats of one inline formatting context each get a fresh layout of its
    /// lines before being placed; see <see cref="PlaceAnchoredFloats"/>.
    /// </summary>
    private const int MaxAnchorRelayouts = 32;

    /// <summary>
    /// Place the floats anchored in a same-BFC inline formatting context leaf, in order: each
    /// on the line holding its text offset when its margin box fits in what that line has left,
    /// below that line otherwise (CSS 2.1 9.5.1 rules 6 and 8; Chromium's line breaker places
    /// a float when it reaches it). The leaf is laid out again after each, so the lines from
    /// there on see it. Returns the floats' content-size contribution.
    /// </summary>
    /// <remarks>
    /// New behaviour, not a port: vendor/taffy has no inline layout, and
    /// crates/obscura-render/src/dom.rs split the paragraph at the float.
    /// </remarks>
    private static Size<float> PlaceAnchoredFloats(
        ILayoutBlockContainer tree,
        BlockItem leaf,
        (NodeId Float, int Offset)[] anchors,
        List<BlockItem> items,
        LayoutInput leafInputs,
        BlockContext leafCtx,
        BlockContext blockCtx,
        float leafTop,
        Size<float?> parentSize,
        float containerInnerWidth,
        float containerOuterWidth,
        HashSet<NodeId> placed)
    {
        var contribution = GeometryExtensions.SizeZero;
        var calc = tree.CalcResolver();
        float contentTop = leaf.Padding.Top + leaf.Border.Top;
        int relayouts = 0;
        int anchorIndex = 0;
        while (anchorIndex < anchors.Length)
        {
            // Lay the lines out around the floats placed so far, to find the anchors' lines.
            // Each float can move every line after it, so the leaf is laid out again before
            // the next one; past MaxAnchorRelayouts the rest are placed from one layout, which
            // keeps a paragraph of thousands of floats linear.
            tree.ComputeBlockChildLayout(leaf.NodeId, leafInputs, leafCtx);
            bool batch = ++relayouts > MaxAnchorRelayouts;
            int count = batch ? anchors.Length - anchorIndex : 1;
            int[] offsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                offsets[i] = anchors[anchorIndex + i].Offset;
            }

            var lines = tree.InlineAnchorLines(leaf.NodeId, offsets);
            for (int i = 0; i < count; i++)
            {
                NodeId floatNode = anchors[anchorIndex + i].Float;
                BlockItem? floatItem = null;
                foreach (BlockItem candidate in items)
                {
                    if (candidate.NodeId == floatNode)
                    {
                        floatItem = candidate;
                        break;
                    }
                }

                if (floatItem?.Float.FloatDirection() is not { } direction || placed.Contains(floatNode))
                {
                    continue;
                }

                float minY = leafTop + contentTop;
                if (lines[i] is { } line)
                {
                    float marginX = floatItem.Margin
                        .Map(m => m.ResolveToOption(containerOuterWidth, calc))
                        .Map(static m => m ?? 0.0f)
                        .HorizontalAxisSum();
                    var size = LayoutFloat(tree, floatItem, parentSize, containerInnerWidth, marginX);
                    float width = Sys.F32Max(size.Size.Width + marginX, 0.0f);
                    bool fits = line.Used <= 0.01f || line.Used + width <= line.Width + 0.01f;
                    minY += line.Top + (fits ? 0.0f : line.Height);
                }

                contribution = contribution.F32Max(PlaceFloat(
                    tree, floatItem, blockCtx, parentSize, containerInnerWidth, containerOuterWidth, minY, direction));
                placed.Add(floatNode);
            }

            anchorIndex += count;
        }

        return contribution;
    }

    /// <summary>
    /// CSS 2.1 10.3.5: the used width of an auto-width float is
    /// <c>min(max(min-content, available), max-content)</c>, then clamped by its min and max
    /// width. Measuring the box at the available width instead gave the widest line it wrapped
    /// to, narrower than Chromium's box when the text wraps.
    /// </summary>
    private static float ShrinkToFitWidth(
        ILayoutBlockContainer tree,
        BlockItem item,
        Size<float?> parentSize,
        float available)
    {
        float maxContent = tree.MeasureChildSize(
            item.NodeId,
            GeometryExtensions.SizeNone,
            parentSize,
            new Size<AvailableSpace>(AvailableSpace.MaxContent, AvailableSpace.MaxContent),
            SizingMode.ContentSize,
            AbsoluteAxis.Horizontal,
            GeometryExtensions.LineFalse);
        float width = maxContent;
        if (maxContent > available)
        {
            float minContent = tree.MeasureChildSize(
                item.NodeId,
                GeometryExtensions.SizeNone,
                parentSize,
                new Size<AvailableSpace>(AvailableSpace.MinContent, AvailableSpace.MaxContent),
                SizingMode.ContentSize,
                AbsoluteAxis.Horizontal,
                GeometryExtensions.LineFalse);
            width = Sys.F32Max(minContent, available);
        }

        width = Sys.F32Max(width, item.PaddingBorderSum.Width);
        if (item.MaxSize.Width is { } max)
        {
            width = Sys.F32Min(width, max);
        }

        if (item.MinSize.Width is { } min)
        {
            width = Sys.F32Max(width, min);
        }

        return width;
    }

    /// <summary>
    /// The border-top of a box whose hypothetical position (margins collapsed, no clearance)
    /// is <paramref name="hypothetical"/>: CSS 2.1 9.5.2 gives it clearance only when that
    /// position is above the relevant floats' bottom, and then puts its border edge there.
    /// </summary>
    private static float ClearedBorderTop(float hypothetical, Clear clear, float clearPos) =>
        clear != Clear.None && clearPos > hypothetical ? clearPos : hypothetical;

    /// <summary>
    /// The border-top of a same-BFC child relative to its parent's border-top, before the child
    /// is laid out: what its sub-context needs so floats and line boxes inside it line up with
    /// the BFC.
    /// </summary>
    /// <remarks>
    /// DEVIATION from vendor/taffy/src/compute/block.rs, which offsets the sub-context by the
    /// previous margin plus the child's own margin, uncollapsed, and so put a float inside
    /// `&lt;div&gt;&lt;p style="margin-top:30px"&gt;` 30px off the line boxes beside it. The child's
    /// margin collapses with the pending one and with the top margins of its first in-flow
    /// descendants that adjoin it (Chromium resolves the BFC offset only once those are known);
    /// when the child is the first in a parent whose margin it collapses with, all of that
    /// escapes the parent and the child sits at the content top.
    /// </remarks>
    private static float SameBfcChildTop(
        ILayoutBlockContainer tree,
        BlockItem item,
        float ownTopMargin,
        float containerInnerWidth,
        float committedYOffset,
        CollapsibleMarginSet active,
        bool marginsEscapeParent,
        float clearPos)
    {
        if (marginsEscapeParent)
        {
            return ClearedBorderTop(committedYOffset, item.Clear, clearPos);
        }

        var set = active
            .CollapseWithMargin(ownTopMargin)
            .CollapseWithSet(DescendantTopMarginChain(tree, item.NodeId, containerInnerWidth));
        return ClearedBorderTop(committedYOffset + set.Resolve(), item.Clear, clearPos);
    }

    /// <summary>
    /// The top margins of the first in-flow descendants that collapse through
    /// <paramref name="node"/>'s top edge, read from the styles (CSS 2.1 8.3.1). A chain stops at
    /// a top border or padding, a formatting context root or a box that is not a block.
    /// </summary>
    private static CollapsibleMarginSet DescendantTopMarginChain(
        ILayoutBlockContainer tree,
        NodeId node,
        float width)
    {
        var calc = tree.CalcResolver();
        var set = CollapsibleMarginSet.Zero;
        for (int depth = 0; depth < 64; depth++)
        {
            var style = tree.GetBlockContainerStyle(node);
            if (!style.IsBlock
                || style.Overflow.X.IsScrollContainer()
                || style.Overflow.Y.IsScrollContainer()
                || tree.GetBlockChildStyle(node).EstablishesBfc
                || style.Padding.Top.ResolveOrZero((float?)width, calc) > 0.0f
                || style.Border.Top.ResolveOrZero((float?)width, calc) > 0.0f
                || tree.ChildCount(node) == 0)
            {
                return set;
            }

            NodeId? first = null;
            foreach (var child in tree.ChildIds(node))
            {
                var childStyle = tree.GetBlockChildStyle(child);
                if (childStyle.BoxGenerationMode == BoxGenerationMode.None
                    || childStyle.Position == Position.Absolute
                    || childStyle.Float.IsFloated())
                {
                    continue;
                }

                first = child;
                break;
            }

            if (first is not { } next)
            {
                return set;
            }

            var nextStyle = tree.GetBlockChildStyle(next);
            if (nextStyle.Clear != Clear.None)
            {
                return set;
            }

            set = set.CollapseWithMargin(nextStyle.Margin.Top.ResolveOrZero((float?)width, calc));
            if (!nextStyle.IsBlock || nextStyle.IsTable)
            {
                return set;
            }

            node = next;
        }

        return set;
    }

    /// <summary>
    /// Place an in-flow box that must not overlap floats (a block formatting context root, a
    /// table or a replaced box; CSS 2.1 9.5): at the first position at or below
    /// <paramref name="minY"/> where its margin box fits beside the floats over its whole
    /// height, narrowing an auto-width box to the space left there.
    /// </summary>
    /// <remarks>
    /// DEVIATION from vendor/taffy/src/compute/block.rs, which takes the float-free slot at
    /// <paramref name="minY"/> whatever its width, so a 300px `overflow: hidden` block beside a
    /// 150px float in a 400px container overflowed it; Chromium 141 moves it below the float.
    /// </remarks>
    private static (LayoutOutput Layout, float StretchWidth, Point<float> Position, float SlotWidth)
        PlaceFloatAvoidingItem(
            ILayoutBlockContainer tree,
            BlockItem item,
            BlockContext blockCtx,
            LayoutInput childInputs,
            float minY,
            float clearPos,
            float containerInnerWidth,
            Rect<float> margin)
    {
        float y = item.Clear != Clear.None ? Sys.F32Max(minY, clearPos) : minY;
        float marginX = margin.HorizontalAxisSum();
        float marginY = margin.VerticalAxisSum();
        bool autoWidth = !item.IsTable && !item.Size.Width.HasValue;
        LayoutOutput layout = default;
        float stretch = 0.0f;
        (float X, float Width) slot = default;

        for (int attempt = 0; attempt < 256; attempt++)
        {
            PocketCalculator.Dom.WorkCancellation.ThrowIfCancellationRequested();
            slot = blockCtx.SlotOver(y, 0.0f);
            for (int pass = 0; pass < 2; pass++)
            {
                stretch = slot.Width - marginX;
                layout = LayoutAtStretch(tree, item, childInputs, stretch);
                var over = blockCtx.SlotOver(y, layout.Size.Height + marginY);
                if (!autoWidth || over.Width >= slot.Width - 0.01f)
                {
                    slot = over;
                    break;
                }

                slot = over;
            }

            bool intruded = slot.Width < containerInnerWidth - 0.01f;
            if (!intruded || layout.Size.Width + marginX <= slot.Width + 0.01f)
            {
                break;
            }

            if (blockCtx.NextFloatEdgeBelow(y) is not { } next)
            {
                break;
            }

            y = next;
        }

        return (layout, stretch, new Point<float>(slot.X, y), slot.Width);
    }

    private static LayoutOutput LayoutAtStretch(
        ILayoutBlockContainer tree,
        BlockItem item,
        LayoutInput childInputs,
        float stretch)
    {
        if (!item.IsTable)
        {
            var known = childInputs.KnownDimensions;
            known.Width = (item.Size.Width ?? stretch).MaybeClamp(item.MinSize.Width, item.MaxSize.Width);
            childInputs.KnownDimensions = known.MaybeClamp(item.MinSize, item.MaxSize);
        }

        childInputs.AvailableSpace = childInputs.AvailableSpace.MapWidth(_ => AvailableSpace.Definite(stretch));
        return tree.ComputeChildLayout(item.NodeId, childInputs);
    }

    /// <summary>Perform absolute layout on all absolutely positioned children.</summary>
    /// <summary>
    /// The used margins of an absolutely positioned box (CSS 2.1 10.3.7 / 10.6.4, css-position-3
    /// 4.1), given its insets resolved against the containing block area.
    /// </summary>
    /// <remarks>
    /// DEVIATION from vendor/taffy (block.rs, flexbox.rs and grid/alignment.rs), which resolve
    /// auto margins against whatever space the set insets leave, even with an inset auto, and
    /// in block.rs zero a pair of auto margins whenever the declared size is >= the free space
    /// (a comparison of the box against the space excluding the box, so a 760px box in a
    /// 1280px containing block never centred, and a max-width-clamped auto width never did
    /// either). C# follows the spec, as Chromium does: auto margins resolve only when both
    /// insets of an axis are non-auto and are 0 otherwise, against the used (clamped) size. A
    /// pair of inline-axis auto margins that would go negative puts the start margin (per
    /// the containing block's direction) at 0 and the overflow on the end margin; a
    /// block-axis pair splits equally even when negative. See "Known deviations" in todo.md.
    /// </remarks>
    internal static Rect<float> ResolveAbsoluteMargins(
        Rect<float?> margin,
        float? left,
        float? right,
        float? top,
        float? bottom,
        Size<float> areaSize,
        Size<float> finalSize,
        bool rtl)
    {
        (float leftMargin, float rightMargin) = ResolveAbsoluteAxisMargins(
            margin.Left, margin.Right, left, right, areaSize.Width, finalSize.Width, rtl, false);
        (float topMargin, float bottomMargin) = ResolveAbsoluteAxisMargins(
            margin.Top, margin.Bottom, top, bottom, areaSize.Height, finalSize.Height, false, true);
        return new Rect<float>(leftMargin, rightMargin, topMargin, bottomMargin);
    }

    /// <summary>
    /// One axis of <see cref="ResolveAbsoluteMargins"/>. <paramref name="reversed"/> is a
    /// right-to-left containing block in the inline axis; <paramref name="blockAxis"/> lets a
    /// negative pair of auto margins split equally rather than pinning the start margin.
    /// </summary>
    internal static (float Start, float End) ResolveAbsoluteAxisMargins(
        float? marginStart,
        float? marginEnd,
        float? insetStart,
        float? insetEnd,
        float area,
        float size,
        bool reversed,
        bool blockAxis)
    {
        if (!insetStart.HasValue || !insetEnd.HasValue)
        {
            return (marginStart ?? 0.0f, marginEnd ?? 0.0f);
        }

        float free = area - insetStart.Value - insetEnd.Value - size
            - (marginStart ?? 0.0f) - (marginEnd ?? 0.0f);
        if (marginStart.HasValue || marginEnd.HasValue)
        {
            return (marginStart ?? free, marginEnd ?? free);
        }

        if (free >= 0.0f || blockAxis)
        {
            return (free / 2.0f, free / 2.0f);
        }

        return reversed ? (free, 0.0f) : (0.0f, free);
    }

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

            // DEVIATION from vendor/taffy/src/compute/block.rs; see ResolveAbsoluteMargins.
            var resolvedMargin = ResolveAbsoluteMargins(
                margin, left, right, top, bottom, areaSize, finalSize, direction.IsRtl());

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
