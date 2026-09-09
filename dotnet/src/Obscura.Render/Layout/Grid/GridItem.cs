// Port of vendor/taffy/src/compute/grid/types/grid_item.rs
namespace Obscura.Render.Layout;

/// <summary>Represents a single grid item.</summary>
internal sealed class GridItem
{
    /// <summary>The id of the node that this item represents.</summary>
    public NodeId Node;

    /// <summary>The order of the item in the children array.</summary>
    public ushort SourceOrder;

    /// <summary>The item's definite row-start and row-end, in origin-zero coordinates.</summary>
    public Line<OriginZeroLine> Row;

    /// <summary>The item's definite column-start and column-end, in origin-zero coordinates.</summary>
    public Line<OriginZeroLine> Column;

    /// <summary>Is it a compressible replaced element?</summary>
    public bool IsCompressibleReplaced;

    /// <summary>Whether descendants are ignored for intrinsic size contributions in each axis.</summary>
    public Size<bool> IntrinsicSizeContainment;

    /// <summary>The item's overflow style.</summary>
    public Point<Overflow> Overflow;

    /// <summary>The item's box-sizing style.</summary>
    public BoxSizing BoxSizing;

    /// <summary>The item's size style.</summary>
    public Size<Dimension> Size;

    /// <summary>The item's min-size style.</summary>
    public Size<Dimension> MinSize;

    /// <summary>The item's max-size style.</summary>
    public Size<Dimension> MaxSize;

    /// <summary>The item's aspect-ratio style.</summary>
    public float? AspectRatio;

    /// <summary>Intrinsic ratios apply to the content box even under border-box sizing.</summary>
    public bool AspectRatioUsesContentBox;

    /// <summary>The item's padding style.</summary>
    public Rect<LengthPercentage> Padding;

    /// <summary>The item's border style.</summary>
    public Rect<LengthPercentage> Border;

    /// <summary>The item's margin style.</summary>
    public Rect<LengthPercentageAuto> Margin;

    /// <summary>The item's align-self property, or the parent's align-items if not set.</summary>
    public AlignItems AlignSelf;

    /// <summary>The item's justify-self property, or the parent's justify-items if not set.</summary>
    public AlignItems JustifySelf;

    /// <summary>The item's first baseline (horizontal).</summary>
    public float? Baseline;

    /// <summary>Shim for baseline alignment that acts like an extra top margin.</summary>
    public float BaselineShim;

    /// <summary>The item's row-start and row-end as indexes into the row track vector.</summary>
    public Line<ushort> RowIndexes;

    /// <summary>The item's column-start and column-end as indexes into the column track vector.</summary>
    public Line<ushort> ColumnIndexes;

    /// <summary>Whether the item crosses a flexible row.</summary>
    public bool CrossesFlexibleRow;

    /// <summary>Whether the item crosses a flexible column.</summary>
    public bool CrossesFlexibleColumn;

    /// <summary>Whether the item crosses an intrinsic row.</summary>
    public bool CrossesIntrinsicRow;

    /// <summary>Whether the item crosses an intrinsic column.</summary>
    public bool CrossesIntrinsicColumn;

    /// <summary>Cache for the known_dimensions input to intrinsic sizing computation.</summary>
    public Size<float?>? GridAreaSizeCache;

    /// <summary>Cache for the min-content size.</summary>
    public Size<float?> MinContentContributionCache;

    /// <summary>Cache for the minimum contribution.</summary>
    public Size<float?> MinimumContributionCache;

    /// <summary>Cache for the max-content size.</summary>
    public Size<float?> MaxContentContributionCache;

    /// <summary>Final y position. Used to compute baseline alignment for the container.</summary>
    public float YPosition;

    /// <summary>Final height. Used to compute baseline alignment for the container.</summary>
    public float Height;

    /// <summary>Create a new item given a concrete placement in both axes.</summary>
    public static GridItem NewWithPlacementStyleAndOrder(
        NodeId node,
        Line<OriginZeroLine> colSpan,
        Line<OriginZeroLine> rowSpan,
        IGridItemStyle style,
        AlignItems parentAlignItems,
        AlignItems parentJustifyItems,
        ushort sourceOrder) => new()
        {
            Node = node,
            SourceOrder = sourceOrder,
            Row = rowSpan,
            Column = colSpan,
            IsCompressibleReplaced = style.IsCompressibleReplaced,
            IntrinsicSizeContainment = style.IntrinsicSizeContainment,
            Overflow = style.Overflow,
            BoxSizing = style.BoxSizing,
            Size = style.Size,
            MinSize = style.MinSize,
            MaxSize = style.MaxSize,
            AspectRatio = style.AspectRatio,
            AspectRatioUsesContentBox = style.AspectRatioUsesContentBox,
            Padding = style.Padding,
            Border = style.Border,
            Margin = style.Margin,
            AlignSelf = style.AlignSelf ?? parentAlignItems,
            JustifySelf = style.JustifySelf ?? parentJustifyItems,
            Baseline = null,
            BaselineShim = 0.0f,
            RowIndexes = new Line<ushort>(0, 0),
            ColumnIndexes = new Line<ushort>(0, 0),
            CrossesFlexibleRow = false,
            CrossesFlexibleColumn = false,
            CrossesIntrinsicRow = false,
            CrossesIntrinsicColumn = false,
            GridAreaSizeCache = null,
            MinContentContributionCache = GeometryExtensions.SizeNone,
            MaxContentContributionCache = GeometryExtensions.SizeNone,
            MinimumContributionCache = GeometryExtensions.SizeNone,
            YPosition = 0.0f,
            Height = 0.0f,
        };

    /// <summary>This item's placement in the specified axis in OriginZero coordinates.</summary>
    public Line<OriginZeroLine> Placement(AbstractAxis axis) => axis == AbstractAxis.Block ? Row : Column;

    /// <summary>This item's placement in the specified axis as track vector indices.</summary>
    public Line<ushort> PlacementIndexes(AbstractAxis axis) =>
        axis == AbstractAxis.Block ? RowIndexes : ColumnIndexes;

    /// <summary>The start of the track range spanned by this item, excluding bounding lines.</summary>
    public int TrackRangeStart(AbstractAxis axis) => PlacementIndexes(axis).Start + 1;

    /// <summary>The end of the track range spanned by this item, excluding bounding lines.</summary>
    public int TrackRangeEnd(AbstractAxis axis) => PlacementIndexes(axis).End;

    /// <summary>The tracks and lines this item spans, excluding the lines that bound it.</summary>
    public TrackSlice TrackRangeExcludingLines(AbstractAxis axis, TrackSlice axisTracks) =>
        axisTracks.Range(TrackRangeStart(axis), TrackRangeEnd(axis));

    /// <summary>Returns the number of tracks that this item spans in the specified axis.</summary>
    public ushort Span(AbstractAxis axis) => axis == AbstractAxis.Block ? Row.Span() : Column.Span();

    /// <summary>Whether the grid item crosses a flexible track in the specified axis.</summary>
    public bool CrossesFlexibleTrack(AbstractAxis axis) =>
        axis == AbstractAxis.Inline ? CrossesFlexibleColumn : CrossesFlexibleRow;

    /// <summary>Whether the grid item crosses an intrinsic track in the specified axis.</summary>
    public bool CrossesIntrinsicTrack(AbstractAxis axis) =>
        axis == AbstractAxis.Inline ? CrossesIntrinsicColumn : CrossesIntrinsicRow;

    /// <summary>
    /// For an item spanning multiple tracks, the upper limit used to calculate its limited
    /// min-/max-content contribution: the sum of the fixed max track sizing functions of any tracks
    /// it spans, applied only if it spans only such tracks.
    /// </summary>
    public float? SpannedTrackLimit(
        AbstractAxis axis,
        TrackSlice axisTracks,
        float? axisParentSize,
        CalcResolver calc)
    {
        var spannedTracks = TrackRangeExcludingLines(axis, axisTracks);
        bool tracksAllFixed = spannedTracks.All(
            track => track.MaxTrackSizingFunction.DefiniteLimit(axisParentSize, calc).HasValue);
        if (!tracksAllFixed)
        {
            return null;
        }

        float limit = 0.0f;
        for (int i = 0; i < spannedTracks.Count; i++)
        {
            limit += spannedTracks[i].MaxTrackSizingFunction.DefiniteLimit(axisParentSize, calc)!.Value;
        }

        return limit;
    }

    /// <summary>
    /// Similar to <see cref="SpannedTrackLimit"/>, but excludes fit-content() arguments from the
    /// limit. Used to clamp the automatic minimum contributions of an item.
    /// </summary>
    public float? SpannedFixedTrackLimit(
        AbstractAxis axis,
        TrackSlice axisTracks,
        float? axisParentSize,
        CalcResolver calc)
    {
        var spannedTracks = TrackRangeExcludingLines(axis, axisTracks);
        bool tracksAllFixed = spannedTracks.All(
            track => track.MaxTrackSizingFunction.DefiniteValue(axisParentSize, calc).HasValue);
        if (!tracksAllFixed)
        {
            return null;
        }

        float limit = 0.0f;
        for (int i = 0; i < spannedTracks.Count; i++)
        {
            limit += spannedTracks[i].MaxTrackSizingFunction.DefiniteValue(axisParentSize, calc)!.Value;
        }

        return limit;
    }

    /// <summary>
    /// Compute the known_dimensions to be passed to the child sizing functions. The key thing being
    /// done here is applying stretch alignment, which is necessary to allow percentage sizes further
    /// down the tree to resolve properly in some cases.
    /// </summary>
    private Size<float?> KnownDimensions(ILayoutPartialTree tree, Size<float?> gridAreaSize)
    {
        var calc = tree.CalcResolver();
        var margins = MarginsAxisSumsWithBaselineShims(gridAreaSize.Width, tree);

        float? aspectRatio = AspectRatio;

        // CSS resolves percentage padding and border against the inline size of the containing
        // block. For a grid item under intrinsic measurement, that inline-size basis is the grid
        // area's width when it is definite.
        var padding = Padding.ResolveOrZero(gridAreaSize.Width, calc);
        var border = Border.ResolveOrZero(gridAreaSize.Width, calc);
        var paddingBorderSize = padding.Add(border).SumAxes();
        var boxSizingAdjustment =
            BoxSizing == BoxSizing.ContentBox ? paddingBorderSize : GeometryExtensions.SizeZero;
        var aspectRatioAdjustment = AspectRatioUsesContentBox ? paddingBorderSize : boxSizingAdjustment;

        var inherentSize = Size.MaybeResolve(gridAreaSize, calc).MaybeAdd(boxSizingAdjustment);
        var minSize = MinSize
            .MaybeResolve(gridAreaSize, calc)
            .MaybeApplyAspectRatio(aspectRatio)
            .MaybeAdd(boxSizingAdjustment);
        var maxSize = MaxSize
            .MaybeResolve(gridAreaSize, calc)
            .MaybeApplyAspectRatio(aspectRatio)
            .MaybeAdd(boxSizingAdjustment);

        var gridAreaMinusItemMarginsSize = gridAreaSize.MaybeSub(margins);
        var alignment = GridLayout.ResolveItemAlignment(
            JustifySelf,
            AlignSelf,
            IsCompressibleReplaced,
            aspectRatio.HasValue);

        float? width = inherentSize.Width;
        if (!width.HasValue
            && !Margin.Left.IsAuto
            && !Margin.Right.IsAuto
            && alignment.Horizontal == AlignItems.Stretch)
        {
            width = gridAreaMinusItemMarginsSize.Width;
        }

        float? height = inherentSize.Height;
        if (!height.HasValue
            && !Margin.Top.IsAuto
            && !Margin.Bottom.IsAuto
            && alignment.Vertical == AlignItems.Stretch)
        {
            height = gridAreaMinusItemMarginsSize.Height;
        }

        var size = GridLayout.ApplyPreferredAspectRatio(
            new Size<float?>(width, height),
            aspectRatio,
            aspectRatioAdjustment);

        return size.MaybeClamp(minSize, maxSize);
    }

    /// <summary>
    /// Returns the grid area's size in the specified axis when every spanned track has a definite
    /// fixed size, and the estimated size in the other axis.
    /// </summary>
    public Size<float?> GridAreaSize(
        AbstractAxis axis,
        TrackSlice axisTracks,
        TrackSlice otherAxisTracks,
        Size<float?> availableSpace,
        Func<GridTrack, float?, float?> getTrackSizeEstimate,
        CalcResolver calc)
    {
        var size = GeometryExtensions.SizeNone;

        var spanned = TrackRangeExcludingLines(axis, axisTracks);
        float? axisTotal = spanned.SumOption(track =>
        {
            float? minSize = track.MinTrackSizingFunction.DefiniteValue(availableSpace.Get(axis), calc);
            if (!minSize.HasValue)
            {
                return null;
            }

            float? maxSize = track.MaxTrackSizingFunction.DefiniteValue(availableSpace.Get(axis), calc);
            if (!maxSize.HasValue)
            {
                return null;
            }

            return minSize.Value == maxSize.Value ? track.BaseSize : null;
        });
        size.Set(axis, axisTotal);

        var otherAxis = axis.Other();
        var otherSpanned = TrackRangeExcludingLines(otherAxis, otherAxisTracks);
        float? otherTotal = otherSpanned.SumOption(track =>
        {
            float? estimate = getTrackSizeEstimate(track, availableSpace.Get(otherAxis));
            return estimate.HasValue ? estimate.Value + track.ContentAlignmentAdjustment : null;
        });
        size.Set(otherAxis, otherTotal);

        return size;
    }

    /// <summary>Retrieve the grid area size from the cache or compute it.</summary>
    public Size<float?> GridAreaSizeCached(
        AbstractAxis axis,
        TrackSlice axisTracks,
        TrackSlice otherAxisTracks,
        Size<float?> availableSpace,
        Func<GridTrack, float?, float?> getTrackSizeEstimate,
        CalcResolver calc)
    {
        if (GridAreaSizeCache.HasValue)
        {
            return GridAreaSizeCache.Value;
        }

        var gridAreaSize = GridAreaSize(
            axis, axisTracks, otherAxisTracks, availableSpace, getTrackSizeEstimate, calc);
        GridAreaSizeCache = gridAreaSize;
        return gridAreaSize;
    }

    /// <summary>
    /// Compute the item's resolved margins for size contributions. Horizontal percentage margins
    /// always resolve to zero if the container size is indefinite as otherwise this would introduce
    /// a cyclic dependency.
    /// </summary>
    public Size<float> MarginsAxisSumsWithBaselineShims(float? innerNodeWidth, ILayoutPartialTree tree)
    {
        var calc = tree.CalcResolver();
        return new Rect<float>(
            Margin.Left.ResolveOrZero(0.0f, calc),
            Margin.Right.ResolveOrZero(0.0f, calc),
            Margin.Top.ResolveOrZero(innerNodeWidth, calc) + BaselineShim,
            Margin.Bottom.ResolveOrZero(innerNodeWidth, calc)).SumAxes();
    }

    /// <summary>Compute the item's min-content contribution from the provided parameters.</summary>
    public float MinContentContribution(
        AbstractAxis axis,
        ILayoutPartialTree tree,
        Size<float?> gridAreaSize,
        Size<float?> availableSpace)
    {
        var knownDimensions = KnownDimensions(tree, gridAreaSize);
        if (IntrinsicSizeContainment.Get(axis))
        {
            var calc = tree.CalcResolver();
            var padding = Padding.ResolveOrZero(gridAreaSize.Width, calc);
            var border = Border.ResolveOrZero(gridAreaSize.Width, calc);
            return knownDimensions.Get(axis) ?? padding.Add(border).SumAxes().Get(axis);
        }

        return tree.MeasureChildSize(
            Node,
            knownDimensions,
            gridAreaSize,
            availableSpace.Map(static opt => opt.HasValue
                ? AvailableSpace.Definite(opt.Value)
                : AvailableSpace.MinContent),
            SizingMode.InherentSize,
            axis.AsAbsNaive(),
            GeometryExtensions.LineFalse);
    }

    /// <summary>Retrieve the item's min-content contribution from the cache or compute it.</summary>
    public float MinContentContributionCached(
        AbstractAxis axis,
        ILayoutPartialTree tree,
        Size<float?> gridAreaSize,
        Size<float?> availableSpace)
    {
        float? cached = MinContentContributionCache.Get(axis);
        if (cached.HasValue)
        {
            return cached.Value;
        }

        float size = MinContentContribution(axis, tree, gridAreaSize, availableSpace);
        MinContentContributionCache.Set(axis, size);
        return size;
    }

    /// <summary>Compute the item's max-content contribution from the provided parameters.</summary>
    public float MaxContentContribution(
        AbstractAxis axis,
        ILayoutPartialTree tree,
        Size<float?> gridAreaSize,
        Size<float?> availableSpace)
    {
        var knownDimensions = KnownDimensions(tree, gridAreaSize);
        if (IntrinsicSizeContainment.Get(axis))
        {
            var calc = tree.CalcResolver();
            var padding = Padding.ResolveOrZero(gridAreaSize.Width, calc);
            var border = Border.ResolveOrZero(gridAreaSize.Width, calc);
            return knownDimensions.Get(axis) ?? padding.Add(border).SumAxes().Get(axis);
        }

        return tree.MeasureChildSize(
            Node,
            knownDimensions,
            gridAreaSize,
            availableSpace.Map(static opt => opt.HasValue
                ? AvailableSpace.Definite(opt.Value)
                : AvailableSpace.MaxContent),
            SizingMode.InherentSize,
            axis.AsAbsNaive(),
            GeometryExtensions.LineFalse);
    }

    /// <summary>Retrieve the item's max-content contribution from the cache or compute it.</summary>
    public float MaxContentContributionCached(
        AbstractAxis axis,
        ILayoutPartialTree tree,
        Size<float?> gridAreaSize,
        Size<float?> availableSpace)
    {
        float? cached = MaxContentContributionCache.Get(axis);
        if (cached.HasValue)
        {
            return cached.Value;
        }

        float size = MaxContentContribution(axis, tree, gridAreaSize, availableSpace);
        MaxContentContributionCache.Set(axis, size);
        return size;
    }

    /// <summary>
    /// The minimum contribution of an item is the smallest outer size it can have.
    /// <see href="https://www.w3.org/TR/css-grid-1/#min-size-auto"/>
    /// </summary>
    public float MinimumContribution(
        ILayoutPartialTree tree,
        AbstractAxis axis,
        TrackSlice axisTracks,
        Size<float?> gridAreaSize,
        Size<float?> innerNodeSize)
    {
        var calc = tree.CalcResolver();
        var padding = Padding.ResolveOrZero(gridAreaSize.Width, calc);
        var border = Border.ResolveOrZero(gridAreaSize.Width, calc);
        var paddingBorderSize = padding.Add(border).SumAxes();
        var boxSizingAdjustment =
            BoxSizing == BoxSizing.ContentBox ? paddingBorderSize : GeometryExtensions.SizeZero;

        float? size = Size
            .MaybeResolve(gridAreaSize, calc)
            .MaybeApplyAspectRatio(AspectRatio)
            .MaybeAdd(boxSizingAdjustment)
            .Get(axis);

        size ??= MinSize
            .MaybeResolve(gridAreaSize, calc)
            .MaybeApplyAspectRatio(AspectRatio)
            .MaybeAdd(boxSizingAdjustment)
            .Get(axis);

        size ??= Overflow.Get(axis).MaybeIntoAutomaticMinSize();

        if (!size.HasValue)
        {
            // Automatic minimum size. See https://www.w3.org/TR/css-grid-1/#min-size-auto
            var itemAxisTracks = TrackRangeExcludingLines(axis, axisTracks);

            // It spans at least one track in that axis whose min track sizing function is auto.
            bool spansAutoMinTrack = axisTracks.Any(static track => track.MinTrackSizingFunction.IsAuto());

            // If it spans more than one track in that axis, none of those tracks are flexible.
            bool onlySpanOneTrack = itemAxisTracks.Count == 1;
            bool spansAFlexibleTrack = axisTracks.Any(static track => track.MaxTrackSizingFunction.IsFr());

            bool useContentBasedMinimum = spansAutoMinTrack && (onlySpanOneTrack || !spansAFlexibleTrack);

            if (useContentBasedMinimum)
            {
                float minimumContribution =
                    MinContentContributionCached(axis, tree, gridAreaSize, gridAreaSize);

                // If the item is a compressible replaced element, and has a definite preferred size
                // or maximum size in the relevant axis, the size suggestion is capped by those
                // sizes; indefinite percentages are resolved against zero.
                if (IsCompressibleReplaced)
                {
                    float? preferred = Size.Get(axis).MaybeResolve(0.0f, calc);
                    float? maximum = MaxSize.Get(axis).MaybeResolve(0.0f, calc);
                    minimumContribution = minimumContribution.MaybeMin(preferred).MaybeMin(maximum);
                }

                size = minimumContribution;
            }
            else
            {
                size = 0.0f;
            }
        }

        // In all cases, the size suggestion is additionally clamped by the maximum size in the
        // affected axis, if it's definite. Note: the argument to fit-content() does not clamp the
        // content-based minimum size in the same way as a fixed max track sizing function.
        float? limit = SpannedFixedTrackLimit(axis, axisTracks, innerNodeSize.Get(axis), calc);
        return size.Value.MaybeMin(limit);
    }

    /// <summary>Retrieve the item's minimum contribution from the cache or compute it.</summary>
    public float MinimumContributionCached(
        ILayoutPartialTree tree,
        AbstractAxis axis,
        TrackSlice axisTracks,
        Size<float?> gridAreaSize,
        Size<float?> innerNodeSize)
    {
        float? cached = MinimumContributionCache.Get(axis);
        if (cached.HasValue)
        {
            return cached.Value;
        }

        float size = MinimumContribution(tree, axis, axisTracks, gridAreaSize, innerNodeSize);
        MinimumContributionCache.Set(axis, size);
        return size;
    }
}
