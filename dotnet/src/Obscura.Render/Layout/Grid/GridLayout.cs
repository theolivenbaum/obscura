// Port of vendor/taffy/src/compute/grid/mod.rs
//
// A partial implementation of the CSS Grid Level 1 specification.
// https://www.w3.org/TR/css-grid-1
//
// The `detailed_layout_info` and `content_size` cargo features are both in taffy's
// default feature set, which is what obscura-render builds against, so the code
// they gate is ported unconditionally.
using System.Runtime.CompilerServices;

namespace Obscura.Render.Layout;

/// <summary>The CSS Grid layout algorithm.</summary>
public static class GridLayout
{
    /// <summary>
    /// Registers <see cref="ComputeGridLayout"/> with <see cref="GridLayoutDispatch"/> so that
    /// <c>TaffyTree</c> routes <c>Display.Grid</c> nodes here.
    /// </summary>
#pragma warning disable CA2255 // The grid algorithm registers itself with the dispatch hook.
    [ModuleInitializer]
    internal static void Register() => GridLayoutDispatch.Compute = ComputeGridLayout;
#pragma warning restore CA2255

    /// <summary>
    /// Resolve grid item <c>normal</c> alignment without losing the authored/default provenance
    /// needed by preferred-aspect-ratio sizing. With an aspect ratio, one implicit stretch axis
    /// supplies the other; an explicit alignment in the opposite axis decides which implicit axis
    /// remains stretchable.
    /// </summary>
    internal static InBothAbsAxis<AlignItems> ResolveItemAlignment(
        AlignItems horizontal,
        AlignItems vertical,
        bool isCompressibleReplaced,
        bool hasPreferredAspectRatio)
    {
        if (isCompressibleReplaced)
        {
            return new InBothAbsAxis<AlignItems>(
                horizontal.ResolveNormal(AlignItems.Start), vertical.ResolveNormal(AlignItems.Start));
        }

        if (!hasPreferredAspectRatio)
        {
            return new InBothAbsAxis<AlignItems>(
                horizontal.ResolveNormal(AlignItems.Stretch), vertical.ResolveNormal(AlignItems.Stretch));
        }

        bool horizontalIsNormal = horizontal == AlignItems.Normal;
        bool verticalIsNormal = vertical == AlignItems.Normal;

        var resolvedHorizontal = horizontalIsNormal
            ? (!verticalIsNormal && vertical == AlignItems.Stretch ? AlignItems.Start : AlignItems.Stretch)
            : horizontal;
        var resolvedVertical = verticalIsNormal
            ? (!horizontalIsNormal && horizontal != AlignItems.Stretch ? AlignItems.Stretch : AlignItems.Start)
            : vertical;

        return new InBothAbsAxis<AlignItems>(resolvedHorizontal, resolvedVertical);
    }

    /// <summary>Apply the preferred aspect ratio to fill in a missing axis.</summary>
    internal static Size<float?> ApplyPreferredAspectRatio(
        Size<float?> size,
        float? aspectRatio,
        Size<float> boxSizingAdjustment)
    {
        if (!aspectRatio.HasValue)
        {
            return size;
        }

        float ratio = aspectRatio.Value;
        if (size.Width.HasValue && !size.Height.HasValue)
        {
            return new Size<float?>(
                size.Width,
                (Sys.F32Max(size.Width.Value - boxSizingAdjustment.Width, 0.0f) / ratio)
                    + boxSizingAdjustment.Height);
        }

        if (!size.Width.HasValue && size.Height.HasValue)
        {
            return new Size<float?>(
                (Sys.F32Max(size.Height.Value - boxSizingAdjustment.Height, 0.0f) * ratio)
                    + boxSizingAdjustment.Width,
                size.Height);
        }

        return size;
    }

    /// <summary>
    /// The Grid layout algorithm. This consists of a few phases:
    /// resolving the explicit grid; placing items (which also resolves the implicit grid); track
    /// (row/column) sizing; and alignment and final item placement.
    /// </summary>
    public static LayoutOutput ComputeGridLayout(ILayoutGridContainer tree, NodeId node, LayoutInput inputs)
    {
        var knownDimensions = inputs.KnownDimensions;
        var parentSize = inputs.ParentSize;
        var availableSpace = inputs.AvailableSpace;
        var runMode = inputs.RunMode;

        var calc = tree.CalcResolver();
        var style = tree.GetGridContainerStyle(node);
        var direction = style.Direction;

        // 1. Compute "available grid space"
        // https://www.w3.org/TR/css-grid-1/#available-grid-space
        float? aspectRatio = style.AspectRatio;
        var padding = style.Padding.ResolveOrZero(parentSize.Width, calc);
        var border = style.Border.ResolveOrZero(parentSize.Width, calc);
        var paddingBorder = padding.Add(border);
        var paddingBorderSize = paddingBorder.SumAxes();
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
        var preferredSize = inputs.SizingMode == SizingMode.InherentSize
            ? style.Size
                .MaybeResolve(parentSize, calc)
                .MaybeApplyAspectRatio(style.AspectRatio)
                .MaybeAdd(boxSizingAdjustment)
            : GeometryExtensions.SizeNone;

        // Scrollbar gutters are reserved when `overflow` is Scroll. The axes are transposed because a
        // node that scrolls vertically needs *horizontal* space reserved for a scrollbar.
        var scrollbarGutter = style.Overflow.Transpose().Map(
            overflow => overflow == Overflow.Scroll ? style.ScrollbarWidth : 0.0f);
        var contentBoxInset = paddingBorder;
        contentBoxInset.Bottom += scrollbarGutter.Y;
        if (direction == Direction.Ltr)
        {
            contentBoxInset.Right += scrollbarGutter.X;
        }
        else
        {
            contentBoxInset.Left += scrollbarGutter.X;
        }

        var alignContent = style.AlignContent ?? AlignContent.Stretch;
        var justifyContent = style.JustifyContent ?? AlignContent.Stretch;
        var alignItems = style.AlignItems;
        var justifyItems = style.JustifyItems;

        var sizeOrPreferred = knownDimensions.Or(preferredSize);
        var constrainedAvailableSpace = new Size<AvailableSpace>(
                sizeOrPreferred.Width.HasValue
                    ? AvailableSpace.Definite(sizeOrPreferred.Width.Value)
                    : availableSpace.Width,
                sizeOrPreferred.Height.HasValue
                    ? AvailableSpace.Definite(sizeOrPreferred.Height.Value)
                    : availableSpace.Height)
            .MaybeClamp(minSize, maxSize)
            .MaybeMax(paddingBorderSize);

        var availableGridSpace = new Size<AvailableSpace>(
            constrainedAvailableSpace.Width.MapDefiniteValue(
                space => space - contentBoxInset.HorizontalAxisSum()),
            constrainedAvailableSpace.Height.MapDefiniteValue(
                space => space - contentBoxInset.VerticalAxisSum()));

        var outerNodeSize = knownDimensions
            .Or(preferredSize)
            .MaybeClamp(minSize, maxSize)
            .MaybeMax(paddingBorderSize);
        var innerNodeSize = new Size<float?>(
            outerNodeSize.Width.HasValue
                ? outerNodeSize.Width.Value - contentBoxInset.HorizontalAxisSum()
                : null,
            outerNodeSize.Height.HasValue
                ? outerNodeSize.Height.Value - contentBoxInset.VerticalAxisSum()
                : null);

        // Short-circuit layout if the container's size is fully determined and the run mode is
        // ComputeSize (and thus the container's size is all we are interested in).
        if (runMode == RunMode.ComputeSize)
        {
            if (outerNodeSize.Width.HasValue && outerNodeSize.Height.HasValue)
            {
                return LayoutOutput.FromOuterSize(
                    new Size<float>(outerNodeSize.Width.Value, outerNodeSize.Height.Value));
            }

            // We can also short-circuit if the width is known and only the width has been requested.
            if (inputs.Axis == RequestedAxis.Horizontal && outerNodeSize.Width.HasValue)
            {
                return LayoutOutput.FromOuterSize(new Size<float>(outerNodeSize.Width.Value, 0.0f));
            }
        }

        int childCount = tree.ChildCount(node);
        var allChildStyles = new List<IGridItemStyle>(childCount);
        for (int i = 0; i < childCount; i++)
        {
            allChildStyles.Add(tree.GetGridChildStyle(tree.GetChildId(node, i)));
        }

        // 2. Resolve the explicit grid

        // Very similar to inner_node_size, except that if inner_node_size is not definite but the node
        // has a min- or max- size style then that is used in its place.
        var autoFitContainerSize = outerNodeSize
            .Or(maxSize)
            .Or(minSize)
            .MaybeClamp(minSize, maxSize)
            .MaybeMax(paddingBorderSize)
            .MaybeSub(contentBoxInset.SumAxes());

        // If the grid container has a definite size or max size in the relevant axis then the number
        // of repetitions is the largest possible positive integer that does not cause the grid to
        // overflow the content box of its grid container. Otherwise, if the grid container has a
        // definite min size in the relevant axis, the number of repetitions is the smallest possible
        // positive integer that fulfills that minimum requirement. Otherwise the specified track list
        // repeats only once.
        var outerOrMax = outerNodeSize.Or(maxSize);
        var autoRepeatFitStrategy = new Size<AutoRepeatStrategy>(
            outerOrMax.Width.HasValue
                ? AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow
                : AutoRepeatStrategy.MinRepetitionsThatDoOverflow,
            outerOrMax.Height.HasValue
                ? AutoRepeatStrategy.MaxRepetitionsThatDoNotOverflow
                : AutoRepeatStrategy.MinRepetitionsThatDoOverflow);

        // Compute the number of rows and columns in the explicit grid *template*
        var (colAutoRepetitionCount, gridTemplateColCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            style, autoFitContainerSize.Width, autoRepeatFitStrategy.Width, calc, AbsoluteAxis.Horizontal);
        var (rowAutoRepetitionCount, gridTemplateRowCount) = ExplicitGrid.ComputeExplicitGridSizeInAxis(
            style, autoFitContainerSize.Height, autoRepeatFitStrategy.Height, calc, AbsoluteAxis.Vertical);

        var nameResolver = new NamedLineResolver(style, colAutoRepetitionCount, rowAutoRepetitionCount);

        ushort explicitColCount = Math.Max(gridTemplateColCount, nameResolver.AreaColumnCount);
        ushort explicitRowCount = Math.Max(gridTemplateRowCount, nameResolver.AreaRowCount);

        nameResolver.SetExplicitColumnCount(explicitColCount);
        nameResolver.SetExplicitRowCount(explicitRowCount);

        // 3. Implicit Grid: estimate track counts
        var (estColCounts, estRowCounts) = ImplicitGrid.ComputeGridSizeEstimate(
            explicitColCount, explicitRowCount, direction, allChildStyles);

        // 4. Grid Item Placement
        var items = new List<GridItem>(childCount);
        var cellOccupancyMatrix = CellOccupancyMatrix.WithTrackCounts(estColCounts, estRowCounts);
        var inFlowChildren = new List<(int Index, NodeId Node, IGridItemStyle Style)>(childCount);
        for (int index = 0; index < childCount; index++)
        {
            var childStyle = allChildStyles[index];
            if (childStyle.BoxGenerationMode != BoxGenerationMode.None
                && childStyle.Position != Position.Absolute)
            {
                inFlowChildren.Add((index, tree.GetChildId(node, index), childStyle));
            }
        }

        GridPlacementAlgorithm.PlaceGridItems(
            cellOccupancyMatrix,
            items,
            inFlowChildren,
            direction,
            style.GridAutoFlow,
            alignItems ?? AlignItems.Normal,
            justifyItems ?? AlignItems.Normal,
            nameResolver);

        // Extract track counts from the previous step (auto-placement can expand the track counts)
        var finalColCounts = cellOccupancyMatrix.TrackCountsFor(AbsoluteAxis.Horizontal);
        var finalRowCounts = cellOccupancyMatrix.TrackCountsFor(AbsoluteAxis.Vertical);

        // 5. Initialize Tracks
        var columns = new List<GridTrack>();
        var rows = new List<GridTrack>();
        var columnTrackCountsForInit = finalColCounts;
        if (direction.IsRtl() && finalColCounts.Explicit <= 1)
        {
            columnTrackCountsForInit = columnTrackCountsForInit with
            {
                NegativeImplicit = finalColCounts.PositiveImplicit,
                PositiveImplicit = finalColCounts.NegativeImplicit,
            };
        }

        ExplicitGrid.InitializeGridTracks(
            columns,
            columnTrackCountsForInit,
            style,
            AbsoluteAxis.Horizontal,
            columnIndex =>
            {
                int occupancyIndex = direction.IsRtl()
                    ? RtlColumnOccupancyIndexForInitialization(columnIndex, finalColCounts)
                    : columnIndex;
                return cellOccupancyMatrix.ColumnIsOccupied(occupancyIndex);
            });
        ExplicitGrid.InitializeGridTracks(
            rows, finalRowCounts, style, AbsoluteAxis.Vertical, cellOccupancyMatrix.RowIsOccupied);
        if (direction.IsRtl())
        {
            ReverseNonGutterTracks(columns, finalColCounts);
        }

        // 6. Track Sizing

        // Convert grid placements in origin-zero coordinates into indexes into the track vectors.
        TrackSizing.ResolveItemTrackIndexes(items, finalColCounts, finalRowCounts);
        // For each item, in each axis, determine whether the item crosses a flexible (fr) track.
        TrackSizing.DetermineIfItemCrossesFlexibleOrIntrinsicTracks(items, columns, rows);

        // Determine if the grid has any baseline-aligned items
        bool hasBaselineAlignedItem = false;
        foreach (var item in items)
        {
            if (item.AlignSelf == AlignItems.Baseline)
            {
                hasBaselineAlignedItem = true;
                break;
            }
        }

        // Run the track sizing algorithm for the Inline axis
        TrackSizing.TrackSizingAlgorithm(
            tree,
            AbstractAxis.Inline,
            minSize.Get(AbstractAxis.Inline),
            maxSize.Get(AbstractAxis.Inline),
            justifyContent,
            alignContent,
            availableGridSpace,
            innerNodeSize,
            columns,
            rows,
            items,
            (track, parentSizeArg) => track.MaxTrackSizingFunction.DefiniteValue(parentSizeArg, calc),
            hasBaselineAlignedItem);
        float initialColumnSum = 0.0f;
        foreach (var track in columns)
        {
            initialColumnSum += track.BaseSize;
        }

        innerNodeSize.Width ??= initialColumnSum;

        foreach (var item in items)
        {
            item.GridAreaSizeCache = null;
        }

        // Run the track sizing algorithm for the Block axis
        TrackSizing.TrackSizingAlgorithm(
            tree,
            AbstractAxis.Block,
            minSize.Get(AbstractAxis.Block),
            maxSize.Get(AbstractAxis.Block),
            alignContent,
            justifyContent,
            availableGridSpace,
            innerNodeSize,
            rows,
            columns,
            items,
            static (track, _) => track.BaseSize,
            false); // TODO: Support baseline alignment in the vertical axis
        float initialRowSum = 0.0f;
        foreach (var track in rows)
        {
            initialRowSum += track.BaseSize;
        }

        innerNodeSize.Height ??= initialRowSum;

        // 6. Compute container size
        var resolvedStyleSize = knownDimensions.Or(preferredSize);
        var containerBorderBox = new Size<float>(
            (resolvedStyleSize.Get(AbstractAxis.Inline)
                ?? (initialColumnSum + contentBoxInset.HorizontalAxisSum()))
                .MaybeClamp(minSize.Width, maxSize.Width)
                .Let(v => Sys.F32Max(v, paddingBorderSize.Width)),
            (resolvedStyleSize.Get(AbstractAxis.Block)
                ?? (initialRowSum + contentBoxInset.VerticalAxisSum()))
                .MaybeClamp(minSize.Height, maxSize.Height)
                .Let(v => Sys.F32Max(v, paddingBorderSize.Height)));
        var containerContentBox = new Size<float>(
            Sys.F32Max(0.0f, containerBorderBox.Width - contentBoxInset.HorizontalAxisSum()),
            Sys.F32Max(0.0f, containerBorderBox.Height - contentBoxInset.VerticalAxisSum()));

        // If only the container's size has been requested
        if (runMode == RunMode.ComputeSize)
        {
            return LayoutOutput.FromOuterSize(containerBorderBox);
        }

        // 7. Resolve percentage track base sizes.
        // For an indefinitely sized container these resolve to zero during the "Initialise Tracks"
        // step and therefore need to be re-resolved here based on the content-sized content box.
        if (!availableGridSpace.Width.IsDefinite)
        {
            foreach (var column in columns)
            {
                float? min = column.MinTrackSizingFunction
                    .ResolvedPercentageSize(containerContentBox.Width, calc);
                float? max = column.MaxTrackSizingFunction
                    .ResolvedPercentageSize(containerContentBox.Width, calc);
                column.BaseSize = column.BaseSize.MaybeClamp(min, max);
            }
        }

        if (!availableGridSpace.Height.IsDefinite)
        {
            foreach (var row in rows)
            {
                float? min = row.MinTrackSizingFunction
                    .ResolvedPercentageSize(containerContentBox.Height, calc);
                float? max = row.MaxTrackSizingFunction
                    .ResolvedPercentageSize(containerContentBox.Height, calc);
                row.BaseSize = row.BaseSize.MaybeClamp(min, max);
            }
        }

        // Column sizing must be re-run (once) if the grid container's width was initially indefinite
        // and there are any columns with percentage track sizing functions, or if any grid item
        // crossing an intrinsically sized track's min-content contribution width has changed.
        bool intrinsicColumnContributionChanged = false;

        bool hasPercentageColumn = false;
        foreach (var track in columns)
        {
            if (track.UsesPercentage())
            {
                hasPercentageColumn = true;
                break;
            }
        }

        bool hasPercentageRow = false;
        foreach (var track in rows)
        {
            if (track.UsesPercentage())
            {
                hasPercentageRow = true;
                break;
            }
        }

        bool parentWidthIndefinite = !availableSpace.Width.IsDefinite;
        bool rerunColumnSizing = parentWidthIndefinite && hasPercentageColumn;

        var columnSlice = new TrackSlice(columns);
        var rowSlice = new TrackSlice(rows);

        if (!rerunColumnSizing)
        {
            // Note: Rust's `.any()` short-circuits, and the closure has side effects, so the loop
            // must stop at the first item whose contribution changed.
            foreach (var item in items)
            {
                if (!item.CrossesIntrinsicColumn)
                {
                    continue;
                }

                var gridAreaSize = item.GridAreaSize(
                    AbstractAxis.Inline,
                    columnSlice,
                    rowSlice,
                    innerNodeSize,
                    static (track, _) => track.BaseSize,
                    calc);
                var itemAvailableSpace = gridAreaSize.With(AbstractAxis.Inline, null);
                float newMinContentContribution = item.MinContentContribution(
                    AbstractAxis.Inline, tree, gridAreaSize, itemAvailableSpace);

                bool hasChanged = item.MinContentContributionCache.Width != newMinContentContribution;

                item.GridAreaSizeCache = gridAreaSize;
                item.MinContentContributionCache.Width = newMinContentContribution;
                item.MaxContentContributionCache.Width = null;
                item.MinimumContributionCache.Width = null;

                if (hasChanged)
                {
                    intrinsicColumnContributionChanged = true;
                    break;
                }
            }

            rerunColumnSizing = intrinsicColumnContributionChanged;
        }
        else
        {
            // Clear intrinsic width caches
            foreach (var item in items)
            {
                item.GridAreaSizeCache = null;
                item.MinContentContributionCache.Width = null;
                item.MaxContentContributionCache.Width = null;
                item.MinimumContributionCache.Width = null;
            }
        }

        bool intrinsicRowContributionChanged = false;

        if (rerunColumnSizing)
        {
            // Re-run the track sizing algorithm for the Inline axis
            TrackSizing.TrackSizingAlgorithm(
                tree,
                AbstractAxis.Inline,
                minSize.Get(AbstractAxis.Inline),
                maxSize.Get(AbstractAxis.Inline),
                justifyContent,
                alignContent,
                availableGridSpace,
                innerNodeSize,
                columns,
                rows,
                items,
                static (track, _) => track.BaseSize,
                hasBaselineAlignedItem);

            bool parentHeightIndefinite = !availableSpace.Height.IsDefinite;
            bool rerunRowSizing = parentHeightIndefinite && hasPercentageRow;

            if (!rerunRowSizing)
            {
                foreach (var item in items)
                {
                    if (!item.CrossesIntrinsicColumn)
                    {
                        continue;
                    }

                    var gridAreaSize = item.GridAreaSize(
                        AbstractAxis.Block,
                        rowSlice,
                        columnSlice,
                        innerNodeSize,
                        static (track, _) => track.BaseSize,
                        calc);
                    var itemAvailableSpace = gridAreaSize.With(AbstractAxis.Block, null);
                    float newMinContentContribution = item.MinContentContribution(
                        AbstractAxis.Block, tree, gridAreaSize, itemAvailableSpace);

                    bool hasChanged = item.MinContentContributionCache.Height != newMinContentContribution;

                    item.GridAreaSizeCache = gridAreaSize;
                    item.MinContentContributionCache.Height = newMinContentContribution;
                    item.MaxContentContributionCache.Height = null;
                    item.MinimumContributionCache.Height = null;

                    if (hasChanged)
                    {
                        intrinsicRowContributionChanged = true;
                        break;
                    }
                }

                rerunRowSizing = intrinsicRowContributionChanged;
            }
            else
            {
                foreach (var item in items)
                {
                    item.GridAreaSizeCache = null;
                    item.MinContentContributionCache.Height = null;
                    item.MaxContentContributionCache.Height = null;
                    item.MinimumContributionCache.Height = null;
                }
            }

            if (rerunRowSizing)
            {
                // Re-run the track sizing algorithm for the Block axis
                TrackSizing.TrackSizingAlgorithm(
                    tree,
                    AbstractAxis.Block,
                    minSize.Get(AbstractAxis.Block),
                    maxSize.Get(AbstractAxis.Block),
                    alignContent,
                    justifyContent,
                    availableGridSpace,
                    innerNodeSize,
                    rows,
                    columns,
                    items,
                    static (track, _) => track.BaseSize,
                    false); // TODO: Support baseline alignment in the vertical axis
            }
        }

        if ((intrinsicColumnContributionChanged && !hasPercentageColumn)
            || (intrinsicRowContributionChanged && !hasPercentageRow))
        {
            float finalColumnSum = 0.0f;
            foreach (var track in columns)
            {
                finalColumnSum += track.BaseSize;
            }

            float finalRowSum = 0.0f;
            foreach (var track in rows)
            {
                finalRowSum += track.BaseSize;
            }

            if (intrinsicColumnContributionChanged && !hasPercentageColumn)
            {
                containerBorderBox.Width =
                    (resolvedStyleSize.Get(AbstractAxis.Inline)
                        ?? (finalColumnSum + contentBoxInset.HorizontalAxisSum()))
                        .MaybeClamp(minSize.Width, maxSize.Width)
                        .Let(v => Sys.F32Max(v, paddingBorderSize.Width));
                containerContentBox.Width = Sys.F32Max(
                    0.0f, containerBorderBox.Width - contentBoxInset.HorizontalAxisSum());
            }

            if (intrinsicRowContributionChanged && !hasPercentageRow)
            {
                containerBorderBox.Height =
                    (resolvedStyleSize.Get(AbstractAxis.Block)
                        ?? (finalRowSum + contentBoxInset.VerticalAxisSum()))
                        .MaybeClamp(minSize.Height, maxSize.Height)
                        .Let(v => Sys.F32Max(v, paddingBorderSize.Height));
                containerContentBox.Height = Sys.F32Max(
                    0.0f, containerBorderBox.Height - contentBoxInset.VerticalAxisSum());
            }
        }

        // If only the container's size has been requested
        if (runMode == RunMode.ComputeSize)
        {
            return LayoutOutput.FromOuterSize(containerBorderBox);
        }

        // 8. Track Alignment

        // Align columns
        float inlineSizeWithoutScrollbar =
            Sys.F32Max(containerBorderBox.Width - paddingBorderSize.Width, 0.0f);
        float inlineScrollbarGutterForAlignment =
            Sys.F32Min(scrollbarGutter.X, inlineSizeWithoutScrollbar);
        GridAlignment.AlignTracks(
            containerContentBox.Get(AbstractAxis.Inline),
            new Line<float>(
                padding.Left + (direction.IsRtl() ? inlineScrollbarGutterForAlignment : 0.0f),
                padding.Right + (direction.IsRtl() ? 0.0f : inlineScrollbarGutterForAlignment)),
            new Line<float>(border.Left, border.Right),
            columns,
            justifyContent,
            direction.IsRtl());

        // Align rows
        GridAlignment.AlignTracks(
            containerContentBox.Get(AbstractAxis.Block),
            new Line<float>(padding.Top, padding.Bottom),
            new Line<float>(border.Top, border.Bottom),
            rows,
            alignContent,
            false);

        // 9. Size, Align, and Position Grid Items

        var itemContentSizeContribution = GeometryExtensions.SizeZero;

        // Sort items back into their original order so they can be matched up with styles
        GridSort.StableSort(items, static (a, b) => a.SourceOrder.CompareTo(b.SourceOrder));

        var containerAlignmentStyles = new InBothAbsAxis<AlignItems?>(justifyItems, alignItems);

        // Position in-flow children (stored in the items list)
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var gridArea = new Rect<float>(
                columns[item.ColumnIndexes.Start + 1].Offset,
                columns[item.ColumnIndexes.End].Offset,
                rows[item.RowIndexes.Start + 1].Offset,
                rows[item.RowIndexes.End].Offset);

            var (contentSizeContribution, yPosition, height) = GridAlignment.AlignAndPositionItem(
                tree,
                item.Node,
                (uint)index,
                gridArea,
                containerAlignmentStyles,
                item.BaselineShim,
                direction);
            item.YPosition = yPosition;
            item.Height = height;

            itemContentSizeContribution = itemContentSizeContribution.F32Max(contentSizeContribution);
        }

        // Position hidden and absolutely positioned children
        uint order = (uint)items.Count;
        for (int index = 0; index < childCount; index++)
        {
            var child = tree.GetChildId(node, index);
            var childStyle = allChildStyles[index];

            // Position hidden child
            if (childStyle.BoxGenerationMode == BoxGenerationMode.None)
            {
                var hiddenLayout = Layout.WithOrder(order);
                tree.SetUnroundedLayout(child, in hiddenLayout);
                tree.PerformChildLayout(
                    child,
                    GeometryExtensions.SizeNone,
                    GeometryExtensions.SizeNone,
                    GeometryExtensions.SizeMaxContent,
                    SizingMode.InherentSize,
                    GeometryExtensions.LineFalse);
                order += 1;
                continue;
            }

            if (childStyle.Position != Position.Absolute)
            {
                continue;
            }

            // Convert grid-column-{start/end} into indexes into the columns list. The value is null
            // if the style property is Auto or an unresolvable Span.
            var rawColIndexes = nameResolver
                .ResolveColumnNames(childStyle.GridColumn)
                .IntoOriginZero(finalColCounts.Explicit)
                .ResolveAbsolutelyPositionedGridTracks()
                .Map(maybeGridLine =>
                {
                    if (!maybeGridLine.HasValue)
                    {
                        return (int?)null;
                    }

                    var line = direction.IsRtl()
                        ? new OriginZeroLine((short)((short)finalColCounts.Explicit - maybeGridLine.Value.Value))
                        : maybeGridLine.Value;
                    return line.TryIntoTrackVecIndex(finalColCounts);
                });
            var maybeColIndexes = direction.IsRtl()
                ? new Line<int?>(rawColIndexes.End, rawColIndexes.Start)
                : rawColIndexes;

            // Convert grid-row-{start/end} into indexes into the rows list.
            var maybeRowIndexes = nameResolver
                .ResolveRowNames(childStyle.GridRow)
                .IntoOriginZero(finalRowCounts.Explicit)
                .ResolveAbsolutelyPositionedGridTracks()
                .Map(maybeGridLine => maybeGridLine.HasValue
                    ? maybeGridLine.Value.TryIntoTrackVecIndex(finalRowCounts)
                    : null);

            var absGridArea = new Rect<float>(
                maybeColIndexes.Start.HasValue
                    ? columns[maybeColIndexes.Start.Value].Offset
                    : direction.IsRtl() ? border.Left + scrollbarGutter.X : border.Left,
                maybeColIndexes.End.HasValue
                    ? columns[maybeColIndexes.End.Value].Offset
                    : direction.IsRtl()
                        ? containerBorderBox.Width - border.Right
                        : containerBorderBox.Width - border.Right - scrollbarGutter.X,
                maybeRowIndexes.Start.HasValue ? rows[maybeRowIndexes.Start.Value].Offset : border.Top,
                maybeRowIndexes.End.HasValue
                    ? rows[maybeRowIndexes.End.Value].Offset
                    : containerBorderBox.Height - border.Bottom - scrollbarGutter.Y);

            // TODO: Baseline alignment support for absolutely positioned items
            var (contentSizeContribution, _, _) = GridAlignment.AlignAndPositionItem(
                tree, child, order, absGridArea, containerAlignmentStyles, 0.0f, direction);
            itemContentSizeContribution = itemContentSizeContribution.F32Max(contentSizeContribution);

            order += 1;
        }

        // Set detailed grid information
        var detailedItems = new List<DetailedGridItemsInfo>(items.Count);
        foreach (var item in items)
        {
            detailedItems.Add(DetailedGridItemsInfo.FromGridItem(item));
        }

        tree.SetDetailedGridInfo(
            node,
            new DetailedGridInfo
            {
                Rows = DetailedGridTracksInfo.FromGridTracksAndTrackCount(finalRowCounts, rows),
                Columns = DetailedGridTracksInfo.FromGridTracksAndTrackCount(finalColCounts, columns),
                Items = detailedItems,
            });

        // If there are no items then return just the container size (no baseline)
        if (items.Count == 0)
        {
            return LayoutOutput.FromOuterSize(containerBorderBox);
        }

        // Determine the grid container baseline (currently we only compute the first baseline)
        GridSort.StableSort(items, static (a, b) => a.RowIndexes.Start.CompareTo(b.RowIndexes.Start));

        ushort firstRow = items[0].RowIndexes.Start;
        int firstRowEnd = items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].RowIndexes.Start != firstRow)
            {
                firstRowEnd = i;
                break;
            }
        }

        GridItem? baselineItem = null;
        for (int i = 0; i < firstRowEnd; i++)
        {
            if (items[i].AlignSelf == AlignItems.Baseline)
            {
                baselineItem = items[i];
                break;
            }
        }

        baselineItem ??= items[0];
        float gridContainerBaseline = baselineItem.YPosition + (baselineItem.Baseline ?? baselineItem.Height);

        return LayoutOutput.FromSizesAndBaselines(
            containerBorderBox,
            itemContentSizeContribution,
            new Point<float?>(null, gridContainerBaseline));
    }

    /// <summary>Reverses only non-gutter column tracks in place while preserving line/gutter slots.</summary>
    private static void ReverseNonGutterTracks(List<GridTrack> tracks, TrackCounts trackCounts)
    {
        // When the explicit grid has 0/1 tracks, visual RTL mirroring is entirely determined by
        // implicit tracks. Reverse all non-gutter tracks in that case.
        if (trackCounts.Explicit <= 1)
        {
            const int MinTrackVecLenToReverseColumns = 5;
            if (tracks.Count < MinTrackVecLenToReverseColumns)
            {
                return;
            }

            int left = 1;
            int right = tracks.Count - 2;
            while (left < right)
            {
                (tracks[left], tracks[right]) = (tracks[right], tracks[left]);
                left += 2;
                right = right >= 2 ? right - 2 : 0;
            }

            return;
        }

        int explicitTrackCount = trackCounts.Explicit;
        if (explicitTrackCount < 2)
        {
            return;
        }

        int l = trackCounts.NegativeImplicit;
        int r = l + explicitTrackCount - 1;
        while (l < r)
        {
            int li = (2 * l) + 1;
            int ri = (2 * r) + 1;
            (tracks[li], tracks[ri]) = (tracks[ri], tracks[li]);
            l += 1;
            r = r >= 1 ? r - 1 : 0;
        }
    }

    /// <summary>Maps initialized column indexes to occupancy-matrix indexes for auto-fit in RTL.</summary>
    private static int RtlColumnOccupancyIndexForInitialization(int columnIndex, TrackCounts trackCounts)
    {
        if (trackCounts.Explicit <= 1)
        {
            return trackCounts.Len() - columnIndex - 1;
        }

        int explicitStart = trackCounts.NegativeImplicit;
        int explicitEnd = explicitStart + trackCounts.Explicit;
        if (columnIndex >= explicitStart && columnIndex < explicitEnd)
        {
            return explicitStart + (explicitEnd - columnIndex - 1);
        }

        return columnIndex;
    }
}

/// <summary>Information from the computation of a grid.</summary>
public sealed class DetailedGridInfo : DetailedLayoutInfo
{
    /// <summary><see href="https://drafts.csswg.org/css-grid-1/#grid-row"/></summary>
    public required DetailedGridTracksInfo Rows { get; init; }

    /// <summary><see href="https://drafts.csswg.org/css-grid-1/#grid-column"/></summary>
    public required DetailedGridTracksInfo Columns { get; init; }

    /// <summary><see href="https://drafts.csswg.org/css-grid-1/#grid-items"/></summary>
    public required List<DetailedGridItemsInfo> Items { get; init; }
}

/// <summary>Information from the computation of a grid's tracks.</summary>
public sealed class DetailedGridTracksInfo
{
    /// <summary>Number of leading implicit grid tracks.</summary>
    public required ushort NegativeImplicitTracks { get; init; }

    /// <summary>Number of explicit grid tracks.</summary>
    public required ushort ExplicitTracks { get; init; }

    /// <summary>Number of trailing implicit grid tracks.</summary>
    public required ushort PositiveImplicitTracks { get; init; }

    /// <summary>Gutters between tracks.</summary>
    public required List<float> Gutters { get; init; }

    /// <summary>The used size of the tracks.</summary>
    public required List<float> Sizes { get; init; }

    internal static DetailedGridTracksInfo FromGridTracksAndTrackCount(
        TrackCounts trackCount,
        List<GridTrack> gridTracks)
    {
        var gutters = new List<float>();
        var sizes = new List<float>();
        foreach (var track in gridTracks)
        {
            if (track.Kind == GridTrackKind.Gutter)
            {
                gutters.Add(track.BaseSize);
            }
            else
            {
                sizes.Add(track.BaseSize);
            }
        }

        return new DetailedGridTracksInfo
        {
            NegativeImplicitTracks = trackCount.NegativeImplicit,
            ExplicitTracks = trackCount.Explicit,
            PositiveImplicitTracks = trackCount.PositiveImplicit,
            Gutters = gutters,
            Sizes = sizes,
        };
    }
}

/// <summary>
/// Grid area information from the placement algorithm, as 1-indexed grid line numbers bounding the
/// area. This matches Chrome's and Firefox's format.
/// </summary>
public readonly record struct DetailedGridItemsInfo(
    ushort RowStart,
    ushort RowEnd,
    ushort ColumnStart,
    ushort ColumnEnd)
{
    internal static DetailedGridItemsInfo FromGridItem(GridItem gridItem) => new(
        ToOneIndexedGridLine(gridItem.RowIndexes.Start),
        ToOneIndexedGridLine(gridItem.RowIndexes.End),
        ToOneIndexedGridLine(gridItem.ColumnIndexes.Start),
        ToOneIndexedGridLine(gridItem.ColumnIndexes.End));

    private static ushort ToOneIndexedGridLine(ushort gridTrackIndex) => (ushort)((gridTrackIndex / 2) + 1);
}

/// <summary>Size helpers used by the grid algorithm that the shared geometry helpers do not carry.</summary>
internal static class GridSizeExtensions
{
    /// <summary>Component-wise <c>AvailableSpace::maybe_clamp</c>.</summary>
    public static Size<AvailableSpace> MaybeClamp(
        this Size<AvailableSpace> self,
        Size<float?> min,
        Size<float?> max) =>
        new(self.Width.MaybeClamp(min.Width, max.Width), self.Height.MaybeClamp(min.Height, max.Height));

    /// <summary>Component-wise <c>AvailableSpace::maybe_max</c>.</summary>
    public static Size<AvailableSpace> MaybeMax(this Size<AvailableSpace> self, Size<float> rhs) =>
        new(self.Width.MaybeMax(rhs.Width), self.Height.MaybeMax(rhs.Height));

    /// <summary>Apply a function to a value. Stands in for Rust's method-chaining on scalars.</summary>
    public static float Let(this float self, Func<float, float> f) => f(self);
}
