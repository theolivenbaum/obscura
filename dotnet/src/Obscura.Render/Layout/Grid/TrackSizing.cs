// Port of vendor/taffy/src/compute/grid/track_sizing.rs
//
// Implements the track sizing algorithm.
// https://www.w3.org/TR/css-grid-1/#layout-algorithm
//
// This is the most numerically delicate code in the layout engine. Sums stay in
// source order, min/max go through Sys.F32Min/Sys.F32Max (Rust's NaN-ignoring
// f32::min/f32::max), and the `max_by(total_cmp)` / `min_by(total_cmp)` reductions
// keep Rust's tie-breaking (last maximum, first minimum).
namespace Obscura.Render.Layout;

/// <summary>
/// Whether it is a minimum or maximum size's space being distributed. This controls the behaviour of
/// the space distribution algorithm when distributing beyond limits.
/// </summary>
internal enum IntrinsicContributionType : byte
{
    /// <summary>It's a minimum size's space being distributed.</summary>
    Minimum,

    /// <summary>It's a maximum size's space being distributed.</summary>
    Maximum,
}

/// <summary>The CSS Grid track sizing algorithm.</summary>
internal static class TrackSizing
{
    /// <summary>
    /// Takes an axis and a list of grid items sorted first by whether they cross a flex track and
    /// then by the number of tracks they cross, and yields batches of them.
    /// </summary>
    private struct ItemBatcher(AbstractAxis axis)
    {
        private readonly AbstractAxis _axis = axis;
        private int _indexOffset = 0;
        private ushort _currentSpan = 1;
        private bool _currentIsFlex = false;

        /// <summary>
        /// Manual version of Rust's <c>Iterator::next</c>, which passes <paramref name="items"/> in
        /// on each iteration to work around the borrow checker.
        /// </summary>
        public bool Next(List<GridItem> items, out int batchStart, out int batchEnd, out bool isFlex)
        {
            batchStart = 0;
            batchEnd = 0;
            isFlex = false;

            if (_currentIsFlex || _indexOffset >= items.Count)
            {
                return false;
            }

            var item = items[_indexOffset];
            _currentSpan = item.Span(_axis);
            _currentIsFlex = item.CrossesFlexibleTrack(_axis);

            int nextIndexOffset;
            if (_currentIsFlex)
            {
                nextIndexOffset = items.Count;
            }
            else
            {
                nextIndexOffset = items.Count;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].CrossesFlexibleTrack(_axis) || items[i].Span(_axis) > _currentSpan)
                    {
                        nextIndexOffset = i;
                        break;
                    }
                }
            }

            batchStart = _indexOffset;
            batchEnd = nextIndexOffset;
            _indexOffset = nextIndexOffset;
            isFlex = _currentIsFlex;
            return true;
        }
    }

    /// <summary>
    /// Captures the variables used to compute the intrinsic sizes of children, and implements the
    /// intrinsic sizing computations.
    /// </summary>
    private sealed class IntrinsicSizeMeasurer(
        ILayoutPartialTree tree,
        TrackSlice otherAxisTracks,
        Func<GridTrack, float?, float?> getTrackSizeEstimate,
        AbstractAxis axis,
        Size<float?> innerNodeSize)
    {
        public ILayoutPartialTree Tree { get; } = tree;

        public CalcResolver Calc { get; } = tree.CalcResolver();

        /// <summary>Compute the available space to be passed to the child sizing functions.</summary>
        public Size<float?> GridAreaSize(GridItem item, TrackSlice axisTracks) =>
            item.GridAreaSizeCached(axis, axisTracks, otherAxisTracks, innerNodeSize, getTrackSizeEstimate, Calc);

        /// <summary>Compute the item's resolved margins for size contributions.</summary>
        public Size<float> MarginsAxisSumsWithBaselineShims(GridItem item, float? percentageBasis) =>
            item.MarginsAxisSumsWithBaselineShims(percentageBasis, Tree);

        /// <summary>Retrieve the item's min-content contribution from the cache or compute it.</summary>
        public float MinContentContribution(GridItem item, TrackSlice axisTracks)
        {
            var gridAreaSize = GridAreaSize(item, axisTracks);
            var availableSpace = gridAreaSize.With(axis, null);
            var marginAxisSums = MarginsAxisSumsWithBaselineShims(item, availableSpace.Width);
            float contribution =
                item.MinContentContributionCached(axis, Tree, gridAreaSize, availableSpace);
            return contribution + marginAxisSums.Get(axis);
        }

        /// <summary>Retrieve the item's max-content contribution from the cache or compute it.</summary>
        public float MaxContentContribution(GridItem item, TrackSlice axisTracks)
        {
            var gridAreaSize = GridAreaSize(item, axisTracks);
            var availableSpace = gridAreaSize.With(axis, null);
            var marginAxisSums = MarginsAxisSumsWithBaselineShims(item, availableSpace.Width);
            float contribution =
                item.MaxContentContributionCached(axis, Tree, gridAreaSize, availableSpace);
            return contribution + marginAxisSums.Get(axis);
        }

        /// <summary>The minimum contribution of an item is the smallest outer size it can have.</summary>
        public float MinimumContribution(GridItem item, TrackSlice axisTracks)
        {
            var gridAreaSize = GridAreaSize(item, axisTracks);
            var availableSpace = gridAreaSize.With(axis, null);
            var marginAxisSums = MarginsAxisSumsWithBaselineShims(item, availableSpace.Width);
            float contribution =
                item.MinimumContributionCached(Tree, axis, axisTracks, gridAreaSize, innerNodeSize);
            return contribution + marginAxisSums.Get(axis);
        }
    }

    /// <summary>
    /// Order grid items firstly by whether they cross a flex track (items that don't come first) and
    /// then by the number of tracks they cross (ascending), then by start position.
    /// </summary>
    public static Comparison<GridItem> CmpByCrossFlexThenSpanThenStart(AbstractAxis axis) => (itemA, itemB) =>
    {
        bool aFlex = itemA.CrossesFlexibleTrack(axis);
        bool bFlex = itemB.CrossesFlexibleTrack(axis);
        if (!aFlex && bFlex)
        {
            return -1;
        }

        if (aFlex && !bFlex)
        {
            return 1;
        }

        var placementA = itemA.Placement(axis);
        var placementB = itemB.Placement(axis);
        int spanCmp = placementA.Span().CompareTo(placementB.Span());
        if (spanCmp != 0)
        {
            return spanCmp;
        }

        return placementA.Start.CompareTo(placementB.Start);
    };

    /// <summary>
    /// When estimating the size in the other axis for content sizing items we should take
    /// align-content/justify-content into account if both the grid container and all items in the
    /// other axis have definite sizes. This computes the per-gutter additional size adjustment.
    /// </summary>
    public static float ComputeAlignmentGutterAdjustment(
        AlignContent alignment,
        float? axisInnerNodeSize,
        Func<GridTrack, float?, float?> getTrackSizeEstimate,
        TrackSlice tracks)
    {
        if (tracks.Count <= 1)
        {
            return 0.0f;
        }

        // As items never cross the outermost gutters in a grid, we can simplify our calculations by
        // treating Start and End the same. The safety modifier doesn't influence gutter weight;
        // overflow fallback is handled when offsets are computed.
        int outerGutterWeight = alignment.GetKeyword() switch
        {
            AlignContentKeyword.Start => 1,
            AlignContentKeyword.FlexStart => 1,
            AlignContentKeyword.End => 1,
            AlignContentKeyword.FlexEnd => 1,
            AlignContentKeyword.Center => 1,
            AlignContentKeyword.Stretch => 0,
            AlignContentKeyword.SpaceBetween => 0,
            AlignContentKeyword.SpaceAround => 1,
            _ => 1,
        };

        int innerGutterWeight = alignment.GetKeyword() switch
        {
            AlignContentKeyword.FlexStart => 0,
            AlignContentKeyword.Start => 0,
            AlignContentKeyword.FlexEnd => 0,
            AlignContentKeyword.End => 0,
            AlignContentKeyword.Center => 0,
            AlignContentKeyword.Stretch => 0,
            AlignContentKeyword.SpaceBetween => 1,
            AlignContentKeyword.SpaceAround => 2,
            _ => 1,
        };

        if (innerGutterWeight == 0)
        {
            return 0.0f;
        }

        if (axisInnerNodeSize.HasValue)
        {
            float size = axisInnerNodeSize.Value;
            float? trackSizeSum = tracks.SumOption(track => getTrackSizeEstimate(track, size));
            float freeSpace = trackSizeSum.HasValue ? Sys.F32Max(0.0f, size - trackSizeSum.Value) : 0.0f;

            int weightedTrackCount = (((tracks.Count - 3) / 2) * innerGutterWeight) + (2 * outerGutterWeight);

            return (freeSpace / weightedTrackCount) * innerGutterWeight;
        }

        return 0.0f;
    }

    /// <summary>Convert origin-zero coordinate track placements into grid track vector indexes.</summary>
    public static void ResolveItemTrackIndexes(
        List<GridItem> items,
        TrackCounts columnCounts,
        TrackCounts rowCounts)
    {
        foreach (var item in items)
        {
            item.ColumnIndexes = new Line<ushort>(
                (ushort)item.Column.Start.IntoTrackVecIndex(columnCounts),
                (ushort)item.Column.End.IntoTrackVecIndex(columnCounts));
            item.RowIndexes = new Line<ushort>(
                (ushort)item.Row.Start.IntoTrackVecIndex(rowCounts),
                (ushort)item.Row.End.IntoTrackVecIndex(rowCounts));
        }
    }

    /// <summary>Determine (in each axis) whether the item crosses any flexible or intrinsic tracks.</summary>
    public static void DetermineIfItemCrossesFlexibleOrIntrinsicTracks(
        List<GridItem> items,
        List<GridTrack> columns,
        List<GridTrack> rows)
    {
        var columnSlice = new TrackSlice(columns);
        var rowSlice = new TrackSlice(rows);
        foreach (var item in items)
        {
            var columnRange = item.TrackRangeExcludingLines(AbstractAxis.Inline, columnSlice);
            item.CrossesFlexibleColumn = columnRange.Any(static track => track.IsFlexible());
            item.CrossesIntrinsicColumn = columnRange.Any(static track => track.HasIntrinsicSizingFunction());

            var rowRange = item.TrackRangeExcludingLines(AbstractAxis.Block, rowSlice);
            item.CrossesFlexibleRow = rowRange.Any(static track => track.IsFlexible());
            item.CrossesIntrinsicRow = rowRange.Any(static track => track.HasIntrinsicSizingFunction());
        }
    }

    /// <summary>
    /// The track sizing algorithm. Gutters are treated as empty fixed-size tracks for the purpose of
    /// the algorithm.
    /// </summary>
    public static void TrackSizingAlgorithm(
        ILayoutPartialTree tree,
        AbstractAxis axis,
        float? axisMinSize,
        float? axisMaxSize,
        AlignContent axisAlignment,
        AlignContent otherAxisAlignment,
        Size<AvailableSpace> availableGridSpace,
        Size<float?> innerNodeSize,
        List<GridTrack> axisTracksList,
        List<GridTrack> otherAxisTracksList,
        List<GridItem> items,
        Func<GridTrack, float?, float?> getTrackSizeEstimate,
        bool hasBaselineAlignedItem)
    {
        var axisTracks = new TrackSlice(axisTracksList);
        var otherAxisTracks = new TrackSlice(otherAxisTracksList);

        // 11.4 Initialise Track sizes
        float? percentageBasis = innerNodeSize.Get(axis) ?? axisMinSize;
        InitializeTrackSizes(tree, axisTracks, percentageBasis);

        // 11.5.1 Shim item baselines
        if (hasBaselineAlignedItem)
        {
            ResolveItemBaselines(tree, axis, items, innerNodeSize);
        }

        // If all tracks have base_size == growth_limit, then skip the rest of this function.
        if (axisTracks.All(static track => track.BaseSize == track.GrowthLimit))
        {
            return;
        }

        // Pre-computations for 11.5 Resolve Intrinsic Track Sizes.
        float gutterAlignmentAdjustment = ComputeAlignmentGutterAdjustment(
            otherAxisAlignment,
            innerNodeSize.Get(axis.Other()),
            getTrackSizeEstimate,
            otherAxisTracks);
        if (otherAxisTracks.Count > 3)
        {
            for (int i = 2; i < otherAxisTracks.Count; i += 2)
            {
                otherAxisTracks[i].ContentAlignmentAdjustment = gutterAlignmentAdjustment;
            }
        }

        // 11.5 Resolve Intrinsic Track Sizes
        ResolveIntrinsicTrackSizes(
            tree,
            axis,
            axisTracks,
            otherAxisTracks,
            items,
            availableGridSpace.Get(axis),
            innerNodeSize,
            getTrackSizeEstimate);

        // 11.6 Maximise Tracks
        MaximiseTracks(axisTracks, innerNodeSize.Get(axis), availableGridSpace.Get(axis));

        // For the purpose of the final two expansion steps we only want to expand into space
        // generated by the grid container's size, not just any available space. To do this we map
        // definite available space to MaxContent when inner_node_size is None.
        AvailableSpace axisAvailableSpaceForExpansion;
        if (innerNodeSize.Get(axis) is { } availableSpaceValue)
        {
            axisAvailableSpaceForExpansion = AvailableSpace.Definite(availableSpaceValue);
        }
        else
        {
            axisAvailableSpaceForExpansion =
                availableGridSpace.Get(axis).Kind == AvailableSpaceKind.MinContent
                    ? AvailableSpace.MinContent
                    : AvailableSpace.MaxContent;
        }

        // 11.7 Expand Flexible Tracks
        ExpandFlexibleTracks(
            tree, axis, axisTracks, items, axisMinSize, axisMaxSize, axisAvailableSpaceForExpansion);

        // 11.8 Stretch auto Tracks
        if (axisAlignment == AlignContent.Stretch)
        {
            StretchAutoTracks(axisTracks, axisMinSize, axisAvailableSpaceForExpansion);
        }
    }

    /// <summary>
    /// Add any planned base size increases to the base size after a round of distributing space, and
    /// reset the planned increase ready for the next round.
    /// </summary>
    private static void FlushPlannedBaseSizeIncreases(TrackSlice tracks)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            track.BaseSize += track.BaseSizePlannedIncrease;
            track.BaseSizePlannedIncrease = 0.0f;
        }
    }

    /// <summary>
    /// Add any planned growth limit increases to the growth limit after a round of distributing
    /// space, and reset the planned increase ready for the next round.
    /// </summary>
    private static void FlushPlannedGrowthLimitIncreases(TrackSlice tracks, bool setInfinitelyGrowable)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (track.GrowthLimitPlannedIncrease > 0.0f)
            {
                track.GrowthLimit = float.IsPositiveInfinity(track.GrowthLimit)
                    ? track.BaseSize + track.GrowthLimitPlannedIncrease
                    : track.GrowthLimit + track.GrowthLimitPlannedIncrease;
                track.InfinitelyGrowable = setInfinitelyGrowable;
            }
            else
            {
                track.InfinitelyGrowable = false;
            }

            track.GrowthLimitPlannedIncrease = 0.0f;
        }
    }

    /// <summary>11.4 Initialise Track sizes: initialize each track's base size and growth limit.</summary>
    private static void InitializeTrackSizes(
        ILayoutPartialTree tree,
        TrackSlice axisTracks,
        float? axisInnerNodeSize)
    {
        var calc = tree.CalcResolver();
        for (int i = 0; i < axisTracks.Count; i++)
        {
            var track = axisTracks[i];

            // For each track, if the track's min track sizing function is a fixed sizing function,
            // resolve it to an absolute length and use that as the initial base size. For an
            // intrinsic sizing function use an initial base size of zero.
            track.BaseSize = track.MinTrackSizingFunction.DefiniteValue(axisInnerNodeSize, calc) ?? 0.0f;

            // For each track, if the track's max track sizing function is a fixed sizing function,
            // resolve it to an absolute length and use that as the initial growth limit. For an
            // intrinsic or flexible sizing function use an initial growth limit of infinity.
            track.GrowthLimit = track.MaxTrackSizingFunction.DefiniteValue(axisInnerNodeSize, calc)
                ?? float.PositiveInfinity;

            // In all cases, if the growth limit is less than the base size, increase it to match.
            if (track.GrowthLimit < track.BaseSize)
            {
                track.GrowthLimit = track.BaseSize;
            }
        }
    }

    /// <summary>
    /// 11.5.1 Shim baseline-aligned items so their intrinsic size contributions reflect their
    /// baseline alignment.
    /// </summary>
    private static void ResolveItemBaselines(
        ILayoutPartialTree tree,
        AbstractAxis axis,
        List<GridItem> items,
        Size<float?> innerNodeSize)
    {
        var calc = tree.CalcResolver();

        // Sort items by track in the other axis (row) start position so that we can iterate items in
        // groups which are in the same track in the other axis.
        var otherAxis = axis.Other();
        GridSort.StableSort(items, (a, b) => a.Placement(otherAxis).Start.CompareTo(b.Placement(otherAxis).Start));

        int cursor = 0;
        while (cursor < items.Count)
        {
            var currentRow = items[cursor].Placement(otherAxis).Start;

            int rowEnd = items.Count;
            for (int i = cursor; i < items.Count; i++)
            {
                if (items[i].Placement(otherAxis).Start != currentRow)
                {
                    rowEnd = i;
                    break;
                }
            }

            int rowStart = cursor;
            cursor = rowEnd;

            // If a row has one or zero items participating in baseline alignment then baseline
            // alignment is a no-op for those items, so skip further computation for that row.
            int rowBaselineItemCount = 0;
            for (int i = rowStart; i < rowEnd; i++)
            {
                if (items[i].AlignSelf == AlignItems.Baseline)
                {
                    rowBaselineItemCount++;
                }
            }

            if (rowBaselineItemCount <= 1)
            {
                continue;
            }

            // Compute the baselines of all items in the row
            for (int i = rowStart; i < rowEnd; i++)
            {
                var item = items[i];
                var measuredSizeAndBaselines = tree.PerformChildLayout(
                    item.Node,
                    GeometryExtensions.SizeNone,
                    innerNodeSize,
                    GeometryExtensions.SizeMinContent,
                    SizingMode.InherentSize,
                    GeometryExtensions.LineFalse);

                float? baseline = measuredSizeAndBaselines.FirstBaselines.Y;
                float height = measuredSizeAndBaselines.Size.Height;

                item.Baseline = (baseline ?? height)
                    + item.Margin.Top.ResolveOrZero(innerNodeSize.Width, calc);
            }

            // Compute the max baseline of all items in the row
            float rowMaxBaseline = 0.0f;
            bool seen = false;
            for (int i = rowStart; i < rowEnd; i++)
            {
                float value = items[i].Baseline ?? 0.0f;
                if (!seen || Sys.TotalCmp(value, rowMaxBaseline) >= 0)
                {
                    rowMaxBaseline = value;
                    seen = true;
                }
            }

            // Compute the baseline shim for each item in the row
            for (int i = rowStart; i < rowEnd; i++)
            {
                items[i].BaselineShim = rowMaxBaseline - (items[i].Baseline ?? 0.0f);
            }
        }
    }

    /// <summary>11.5 Resolve Intrinsic Track Sizes.</summary>
    private static void ResolveIntrinsicTrackSizes(
        ILayoutPartialTree tree,
        AbstractAxis axis,
        TrackSlice axisTracks,
        TrackSlice otherAxisTracks,
        List<GridItem> items,
        AvailableSpace axisAvailableGridSpace,
        Size<float?> innerNodeSize,
        Func<GridTrack, float?, float?> getTrackSizeEstimate)
    {
        // Step 1 (shimming baseline-aligned items) is already done. See ResolveItemBaselines.

        // Step 2. The track sizing algorithm requires us to iterate through the items in ascending
        // order of the number of tracks they span. Pre-sort them into this order.
        GridSort.StableSort(items, CmpByCrossFlexThenSpanThenStart(axis));

        float? axisInnerNodeSize = innerNodeSize.Get(axis);
        float flexFactorSum = axisTracks.Sum(static track => track.FlexFactor());
        var itemSizer = new IntrinsicSizeMeasurer(
            tree, otherAxisTracks, getTrackSizeEstimate, axis, innerNodeSize);
        var calc = itemSizer.Calc;

        var batchedItemIterator = new ItemBatcher(axis);
        while (batchedItemIterator.Next(items, out int batchStart, out int batchEnd, out bool isFlex))
        {
            // 2. Size tracks to fit non-spanning items.
            ushort batchSpan = items[batchStart].Placement(axis).Span();
            if (!isFlex && batchSpan == 1)
            {
                for (int itemIndex = batchStart; itemIndex < batchEnd; itemIndex++)
                {
                    var item = items[itemIndex];
                    int trackIndex = item.PlacementIndexes(axis).Start + 1;
                    var track = axisTracks[trackIndex];

                    // Handle base sizes
                    ulong minTag = track.MinTrackSizingFunction.IntoRaw().Tag;
                    float newBaseSize;
                    if (minTag == CompactLength.MinContentTag)
                    {
                        newBaseSize = Sys.F32Max(
                            track.BaseSize, itemSizer.MinContentContribution(item, axisTracks));
                    }
                    else if (minTag == CompactLength.PercentTag)
                    {
                        // If the container size is indefinite and has not yet been resolved then
                        // percentage sized tracks should be treated as min-content (this matches
                        // Chrome's behaviour and seems sensible).
                        newBaseSize = !axisInnerNodeSize.HasValue
                            ? Sys.F32Max(track.BaseSize, itemSizer.MinContentContribution(item, axisTracks))
                            : track.BaseSize;
                    }
                    else if (minTag == CompactLength.MaxContentTag)
                    {
                        newBaseSize = Sys.F32Max(
                            track.BaseSize, itemSizer.MaxContentContribution(item, axisTracks));
                    }
                    else if (minTag == CompactLength.AutoTag)
                    {
                        float space;
                        if (axisAvailableGridSpace.Kind
                                is AvailableSpaceKind.MinContent or AvailableSpaceKind.MaxContent
                            && !item.Overflow.Get(axis).IsScrollContainer())
                        {
                            // QUIRK: the spec says to use the items' limited min-content
                            // contributions in place of their minimum contributions when the grid
                            // container is being sized under a min- or max-content constraint.
                            // In practice browsers only apply this rule if the item is not a scroll
                            // container, giving the automatic minimum size of scroll containers
                            // (zero) precedence over the min-content contributions.
                            float axisMinimumSize = itemSizer.MinimumContribution(item, axisTracks);
                            float axisMinContentSize = itemSizer.MinContentContribution(item, axisTracks);
                            float? limit = track.MaxTrackSizingFunction.DefiniteLimit(axisInnerNodeSize, calc);
                            space = Sys.F32Max(axisMinContentSize.MaybeMin(limit), axisMinimumSize);
                        }
                        else
                        {
                            space = itemSizer.MinimumContribution(item, axisTracks);
                        }

                        newBaseSize = Sys.F32Max(track.BaseSize, space);
                    }
                    else if (minTag == CompactLength.LengthTag)
                    {
                        // Do nothing as it's not an intrinsic track sizing function
                        newBaseSize = track.BaseSize;
                    }
                    else if (track.MinTrackSizingFunction.IntoRaw().IsCalc)
                    {
                        // Handle calc() like percentage
                        newBaseSize = !axisInnerNodeSize.HasValue
                            ? Sys.F32Max(track.BaseSize, itemSizer.MinContentContribution(item, axisTracks))
                            : track.BaseSize;
                    }
                    else
                    {
                        throw new InvalidOperationException("Unreachable min track sizing function");
                    }

                    float? growthLimitMinContentContribution = !item.Overflow.Get(axis).IsScrollContainer()
                        ? itemSizer.MinContentContribution(item, axisTracks)
                        : null;
                    float growthLimitMaxContentContribution =
                        itemSizer.MaxContentContribution(item, axisTracks);
                    float growthLimitIntrinsicMinContentContribution =
                        itemSizer.MinContentContribution(item, axisTracks);

                    track.BaseSize = newBaseSize;

                    // Handle growth limits
                    if (track.MaxTrackSizingFunction.IsFitContent())
                    {
                        // If the item is not a scroll container, increase the growth limit to at
                        // least the size of the min-content contribution.
                        if (growthLimitMinContentContribution.HasValue)
                        {
                            track.GrowthLimitPlannedIncrease = Sys.F32Max(
                                track.GrowthLimitPlannedIncrease, growthLimitMinContentContribution.Value);
                        }

                        // Always increase the growth limit to at least the size of the *fit-content
                        // limited* max-content contribution.
                        float fitContentLimit = track.FitContentLimit(axisInnerNodeSize);
                        float maxContentContribution =
                            Sys.F32Min(growthLimitMaxContentContribution, fitContentLimit);
                        track.GrowthLimitPlannedIncrease =
                            Sys.F32Max(track.GrowthLimitPlannedIncrease, maxContentContribution);
                    }
                    else if (track.MaxTrackSizingFunction.IsMaxContentAlike()
                        || (track.MaxTrackSizingFunction.UsesPercentage() && !axisInnerNodeSize.HasValue))
                    {
                        // If the container size is indefinite and has not yet been resolved then
                        // percentage sized tracks should be treated as auto.
                        track.GrowthLimitPlannedIncrease = Sys.F32Max(
                            track.GrowthLimitPlannedIncrease, growthLimitMaxContentContribution);
                    }
                    else if (track.MaxTrackSizingFunction.IsIntrinsic())
                    {
                        track.GrowthLimitPlannedIncrease = Sys.F32Max(
                            track.GrowthLimitPlannedIncrease, growthLimitIntrinsicMinContentContribution);
                    }
                }

                for (int i = 0; i < axisTracks.Count; i++)
                {
                    var track = axisTracks[i];
                    if (track.GrowthLimitPlannedIncrease > 0.0f)
                    {
                        track.GrowthLimit = float.IsPositiveInfinity(track.GrowthLimit)
                            ? track.GrowthLimitPlannedIncrease
                            : Sys.F32Max(track.GrowthLimit, track.GrowthLimitPlannedIncrease);
                    }

                    track.InfinitelyGrowable = false;
                    track.GrowthLimitPlannedIncrease = 0.0f;
                    if (track.GrowthLimit < track.BaseSize)
                    {
                        track.GrowthLimit = track.BaseSize;
                    }
                }

                continue;
            }

            bool useFlexFactorForDistribution = isFlex && flexFactorSum != 0.0f;

            // 1. For intrinsic minimums: first increase the base size of tracks with an intrinsic min
            //    track sizing function.
            for (int itemIndex = batchStart; itemIndex < batchEnd; itemIndex++)
            {
                var item = items[itemIndex];
                if (!item.CrossesIntrinsicTrack(axis))
                {
                    continue;
                }

                float space;
                if (axisAvailableGridSpace.Kind is AvailableSpaceKind.MinContent or AvailableSpaceKind.MaxContent
                    && !item.Overflow.Get(axis).IsScrollContainer())
                {
                    float axisMinimumSize = itemSizer.MinimumContribution(item, axisTracks);
                    float axisMinContentSize = itemSizer.MinContentContribution(item, axisTracks);
                    float? limit = item.SpannedTrackLimit(axis, axisTracks, axisInnerNodeSize, calc);
                    space = Sys.F32Max(axisMinContentSize.MaybeMin(limit), axisMinimumSize);
                }
                else
                {
                    space = itemSizer.MinimumContribution(item, axisTracks);
                }

                var tracks = item.TrackRangeExcludingLines(axis, axisTracks);
                if (space > 0.0f)
                {
                    bool HasIntrinsicMinTrackSizingFunction(GridTrack track) =>
                        !track.MinTrackSizingFunction.DefiniteValue(axisInnerNodeSize, calc).HasValue;

                    if (item.Overflow.Get(axis).IsScrollContainer())
                    {
                        DistributeItemSpaceToBaseSize(
                            isFlex,
                            useFlexFactorForDistribution,
                            space,
                            tracks,
                            HasIntrinsicMinTrackSizingFunction,
                            track => track.FitContentLimitedGrowthLimit(axisInnerNodeSize),
                            IntrinsicContributionType.Minimum);
                    }
                    else
                    {
                        DistributeItemSpaceToBaseSize(
                            isFlex,
                            useFlexFactorForDistribution,
                            space,
                            tracks,
                            HasIntrinsicMinTrackSizingFunction,
                            static track => track.GrowthLimit,
                            IntrinsicContributionType.Minimum);
                    }
                }
            }

            FlushPlannedBaseSizeIncreases(axisTracks);

            // 2. For content-based minimums.
            for (int itemIndex = batchStart; itemIndex < batchEnd; itemIndex++)
            {
                var item = items[itemIndex];
                float space = itemSizer.MinContentContribution(item, axisTracks);
                var tracks = item.TrackRangeExcludingLines(axis, axisTracks);
                if (space > 0.0f)
                {
                    if (item.Overflow.Get(axis).IsScrollContainer())
                    {
                        DistributeItemSpaceToBaseSize(
                            isFlex,
                            useFlexFactorForDistribution,
                            space,
                            tracks,
                            static track => track.MinTrackSizingFunction.IsMinOrMaxContent(),
                            track => track.FitContentLimitedGrowthLimit(axisInnerNodeSize),
                            IntrinsicContributionType.Minimum);
                    }
                    else
                    {
                        DistributeItemSpaceToBaseSize(
                            isFlex,
                            useFlexFactorForDistribution,
                            space,
                            tracks,
                            static track => track.MinTrackSizingFunction.IsMinOrMaxContent(),
                            static track => track.GrowthLimit,
                            IntrinsicContributionType.Minimum);
                    }
                }
            }

            FlushPlannedBaseSizeIncreases(axisTracks);

            // 3. For max-content minimums.
            if (axisAvailableGridSpace.Kind == AvailableSpaceKind.MaxContent)
            {
                for (int itemIndex = batchStart; itemIndex < batchEnd; itemIndex++)
                {
                    var item = items[itemIndex];
                    float axisMaxContentSize = itemSizer.MaxContentContribution(item, axisTracks);
                    float? limit = item.SpannedTrackLimit(axis, axisTracks, axisInnerNodeSize, calc);
                    float space = axisMaxContentSize.MaybeMin(limit);
                    var tracks = item.TrackRangeExcludingLines(axis, axisTracks);
                    if (space > 0.0f)
                    {
                        // If any of the tracks spanned by the item have a MaxContent min track sizing
                        // function then distribute space only to those tracks. Otherwise distribute
                        // space to tracks with an Auto min track sizing function.
                        //
                        // This prioritisation of MaxContent over Auto is not in the spec, but both
                        // Chrome and Firefox implement it, so we do too.
                        if (tracks.Any(HasMaxContentMinTrackSizingFunction))
                        {
                            DistributeItemSpaceToBaseSize(
                                isFlex,
                                useFlexFactorForDistribution,
                                space,
                                tracks,
                                HasMaxContentMinTrackSizingFunction,
                                static _ => float.PositiveInfinity,
                                IntrinsicContributionType.Maximum);
                        }
                        else
                        {
                            DistributeItemSpaceToBaseSize(
                                isFlex,
                                useFlexFactorForDistribution,
                                space,
                                tracks,
                                HasAutoMinTrackSizingFunction,
                                track => track.FitContentLimitedGrowthLimit(axisInnerNodeSize),
                                IntrinsicContributionType.Maximum);
                        }
                    }
                }

                FlushPlannedBaseSizeIncreases(axisTracks);
            }

            // In all cases, continue to increase the base size of tracks with a min track sizing
            // function of max-content by distributing extra space as needed to account for these
            // items' max-content contributions.
            for (int itemIndex = batchStart; itemIndex < batchEnd; itemIndex++)
            {
                var item = items[itemIndex];
                float space = itemSizer.MaxContentContribution(item, axisTracks);
                var tracks = item.TrackRangeExcludingLines(axis, axisTracks);
                if (space > 0.0f)
                {
                    DistributeItemSpaceToBaseSize(
                        isFlex,
                        useFlexFactorForDistribution,
                        space,
                        tracks,
                        static track => track.MinTrackSizingFunction.IsMaxContent(),
                        static track => track.GrowthLimit,
                        IntrinsicContributionType.Maximum);
                }
            }

            FlushPlannedBaseSizeIncreases(axisTracks);

            // 4. If at this point any track's growth limit is now less than its base size, increase
            //    its growth limit to match its base size.
            for (int i = 0; i < axisTracks.Count; i++)
            {
                var track = axisTracks[i];
                if (track.GrowthLimit < track.BaseSize)
                {
                    track.GrowthLimit = track.BaseSize;
                }
            }

            // If a track is a flexible track, then it has a flexible max track sizing function. It
            // cannot also have an intrinsic max track sizing function, so these steps do not apply.
            if (!isFlex)
            {
                // 5. For intrinsic maximums.
                bool HasIntrinsicMaxTrackSizingFunction(GridTrack track) =>
                    !track.MaxTrackSizingFunction.HasDefiniteValue(axisInnerNodeSize);
                for (int itemIndex = batchStart; itemIndex < batchEnd; itemIndex++)
                {
                    var item = items[itemIndex];
                    float space = itemSizer.MinContentContribution(item, axisTracks);
                    var tracks = item.TrackRangeExcludingLines(axis, axisTracks);
                    if (space > 0.0f)
                    {
                        DistributeItemSpaceToGrowthLimit(
                            space, tracks, HasIntrinsicMaxTrackSizingFunction, innerNodeSize.Get(axis));
                    }
                }

                // Mark any tracks whose growth limit changed from infinite to finite in this step as
                // infinitely growable for the next step.
                FlushPlannedGrowthLimitIncreases(axisTracks, true);

                // 6. For max-content maximums.
                bool HasMaxContentMaxTrackSizingFunction(GridTrack track) =>
                    track.MaxTrackSizingFunction.IsMaxContentAlike()
                    || (track.MaxTrackSizingFunction.UsesPercentage() && !axisInnerNodeSize.HasValue);
                for (int itemIndex = batchStart; itemIndex < batchEnd; itemIndex++)
                {
                    var item = items[itemIndex];
                    float space = itemSizer.MaxContentContribution(item, axisTracks);
                    var tracks = item.TrackRangeExcludingLines(axis, axisTracks);
                    if (space > 0.0f)
                    {
                        DistributeItemSpaceToGrowthLimit(
                            space, tracks, HasMaxContentMaxTrackSizingFunction, innerNodeSize.Get(axis));
                    }
                }

                FlushPlannedGrowthLimitIncreases(axisTracks, false);
            }
        }

        // Step 5. If any track still has an infinite growth limit, set its growth limit to its base
        // size. This is important to ensure that "Maximise Tracks" doesn't affect flexible tracks.
        for (int i = 0; i < axisTracks.Count; i++)
        {
            var track = axisTracks[i];
            if (float.IsPositiveInfinity(track.GrowthLimit))
            {
                track.GrowthLimit = track.BaseSize;
            }
        }
    }

    /// <summary>
    /// Whether a track has an Auto min track sizing function and does not have a MinContent max
    /// track sizing function. The latter condition was added to match Chrome.
    /// </summary>
    private static bool HasAutoMinTrackSizingFunction(GridTrack track) =>
        track.MinTrackSizingFunction.IsAuto() && !track.MaxTrackSizingFunction.IsMinContent();

    /// <summary>Whether a track has a MaxContent min track sizing function.</summary>
    private static bool HasMaxContentMinTrackSizingFunction(GridTrack track) =>
        track.MinTrackSizingFunction.IsMaxContent();

    /// <summary>
    /// 11.5.1 Distributing Extra Space Across Spanned Tracks.
    /// <see href="https://www.w3.org/TR/css-grid-1/#extra-space"/>
    /// </summary>
    private static void DistributeItemSpaceToBaseSize(
        bool isFlex,
        bool useFlexFactorForDistribution,
        float space,
        TrackSlice tracks,
        Func<GridTrack, bool> trackIsAffected,
        Func<GridTrack, float> trackLimit,
        IntrinsicContributionType intrinsicContributionType)
    {
        if (isFlex)
        {
            bool Filter(GridTrack track) => track.IsFlexible() && trackIsAffected(track);
            if (useFlexFactorForDistribution)
            {
                DistributeItemSpaceToBaseSizeInner(
                    space, tracks, Filter, static track => track.FlexFactor(), trackLimit,
                    intrinsicContributionType);
            }
            else
            {
                DistributeItemSpaceToBaseSizeInner(
                    space, tracks, Filter, static _ => 1.0f, trackLimit, intrinsicContributionType);
            }
        }
        else
        {
            DistributeItemSpaceToBaseSizeInner(
                space, tracks, trackIsAffected, static _ => 1.0f, trackLimit, intrinsicContributionType);
        }
    }

    /// <summary>Inner function that doesn't account for differences due to distributing to flex items.</summary>
    private static void DistributeItemSpaceToBaseSizeInner(
        float space,
        TrackSlice tracks,
        Func<GridTrack, bool> trackIsAffected,
        Func<GridTrack, float> trackDistributionProportion,
        Func<GridTrack, float> trackLimit,
        IntrinsicContributionType intrinsicContributionType)
    {
        // Skip this distribution if there is either no space to distribute or no affected tracks.
        if (space == 0.0f || !tracks.Any(trackIsAffected))
        {
            return;
        }

        // 1. Find the space to distribute
        float trackSizes = tracks.Sum(static track => track.BaseSize);
        float extraSpace = Sys.F32Max(0.0f, space - trackSizes);

        // A small constant to avoid infinite loops due to rounding errors.
        const float Threshold = 0.000001f;

        // 2. Distribute space up to limits
        extraSpace = DistributeSpaceUpToLimits(
            extraSpace,
            tracks,
            trackIsAffected,
            trackDistributionProportion,
            static track => track.BaseSize,
            trackLimit);

        // 3. Distribute remaining space beyond limits (if any)
        if (extraSpace > Threshold)
        {
            // When accommodating minimum contributions or min-content contributions: any affected
            // track that happens to also have an intrinsic max track sizing function.
            // When accommodating max-content contributions: any affected track that happens to also
            // have a max-content max track sizing function.
            Func<GridTrack, bool> filter = intrinsicContributionType == IntrinsicContributionType.Minimum
                ? static track => track.MaxTrackSizingFunction.IsIntrinsic()
                : static track => track.MinTrackSizingFunction.IsMaxContent()
                    || track.MaxTrackSizingFunction.IsMaxOrFitContent();

            // If there are no such tracks, then use all affected tracks.
            int numberOfTracks = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (trackIsAffected(tracks[i]) && filter(tracks[i]))
                {
                    numberOfTracks++;
                }
            }

            if (numberOfTracks == 0)
            {
                filter = static _ => true;
            }

            DistributeSpaceUpToLimits(
                extraSpace,
                tracks,
                filter,
                trackDistributionProportion,
                static track => track.BaseSize,
                trackLimit);
        }

        // 4. For each affected track, if the track's item-incurred increase is larger than the
        //    track's planned increase, set the track's planned increase to that value.
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (track.ItemIncurredIncrease > track.BaseSizePlannedIncrease)
            {
                track.BaseSizePlannedIncrease = track.ItemIncurredIncrease;
            }

            track.ItemIncurredIncrease = 0.0f;
        }
    }

    /// <summary>
    /// 11.5.1 Distributing Extra Space Across Spanned Tracks: the simplified (and faster) version of
    /// the algorithm for growth limits.
    /// </summary>
    private static void DistributeItemSpaceToGrowthLimit(
        float space,
        TrackSlice tracks,
        Func<GridTrack, bool> trackIsAffected,
        float? axisInnerNodeSize)
    {
        // Skip this distribution if there is either no space to distribute or no affected tracks.
        if (space == 0.0f || tracks.CountWhere(trackIsAffected) == 0)
        {
            return;
        }

        // 1. Find the space to distribute
        float trackSizes = tracks.Sum(static track =>
            float.IsPositiveInfinity(track.GrowthLimit) ? track.BaseSize : track.GrowthLimit);
        float extraSpace = Sys.F32Max(0.0f, space - trackSizes);

        // 2. Distribute space up to limits. For growth limits the limit is either infinity or the
        // growth limit itself, which means that if there are any tracks with infinite limits all
        // space is distributed to those tracks, and otherwise no space is distributed in this step.
        bool IsGrowable(GridTrack track) =>
            track.InfinitelyGrowable
            || float.IsPositiveInfinity(track.FitContentLimitedGrowthLimit(axisInnerNodeSize));

        int numberOfGrowableTracks = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (trackIsAffected(tracks[i]) && IsGrowable(tracks[i]))
            {
                numberOfGrowableTracks++;
            }
        }

        if (numberOfGrowableTracks > 0)
        {
            float itemIncurredIncrease = extraSpace / numberOfGrowableTracks;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (trackIsAffected(track) && IsGrowable(track))
                {
                    track.ItemIncurredIncrease = itemIncurredIncrease;
                }
            }
        }
        else
        {
            // 3. Distribute space beyond limits: when handling any intrinsic growth limit, all
            //    affected tracks are unfrozen.
            DistributeSpaceUpToLimits(
                extraSpace,
                tracks,
                trackIsAffected,
                static _ => 1.0f,
                static track => float.IsPositiveInfinity(track.GrowthLimit) ? track.BaseSize : track.GrowthLimit,
                track => track.FitContentLimit(axisInnerNodeSize));
        }

        // 4. For each affected track, if the track's item-incurred increase is larger than the
        //    track's planned increase, set the track's planned increase to that value.
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (track.ItemIncurredIncrease > track.GrowthLimitPlannedIncrease)
            {
                track.GrowthLimitPlannedIncrease = track.ItemIncurredIncrease;
            }

            track.ItemIncurredIncrease = 0.0f;
        }
    }

    /// <summary>
    /// 11.6 Maximise Tracks: distribute free space (if any) to tracks with FINITE growth limits, up
    /// to their limits.
    /// </summary>
    private static void MaximiseTracks(
        TrackSlice axisTracks,
        float? axisInnerNodeSize,
        AvailableSpace axisAvailableGridSpace)
    {
        float usedSpace = axisTracks.Sum(static track => track.BaseSize);
        float freeSpace = axisAvailableGridSpace.ComputeFreeSpace(usedSpace);
        if (float.IsPositiveInfinity(freeSpace))
        {
            for (int i = 0; i < axisTracks.Count; i++)
            {
                axisTracks[i].BaseSize = axisTracks[i].GrowthLimit;
            }
        }
        else if (freeSpace > 0.0f)
        {
            DistributeSpaceUpToLimits(
                freeSpace,
                axisTracks,
                static _ => true,
                static _ => 1.0f,
                static track => track.BaseSize,
                track => track.FitContentLimitedGrowthLimit(axisInnerNodeSize));
            for (int i = 0; i < axisTracks.Count; i++)
            {
                var track = axisTracks[i];
                track.BaseSize += track.ItemIncurredIncrease;
                track.ItemIncurredIncrease = 0.0f;
            }
        }
    }

    /// <summary>
    /// 11.7 Expand Flexible Tracks: size flexible tracks using the largest value that can be assigned
    /// to an fr without exceeding the available space.
    /// </summary>
    private static void ExpandFlexibleTracks(
        ILayoutPartialTree tree,
        AbstractAxis axis,
        TrackSlice axisTracks,
        List<GridItem> items,
        float? axisMinSize,
        float? axisMaxSize,
        AvailableSpace axisAvailableSpaceForExpansion)
    {
        float flexFraction;
        if (axisAvailableSpaceForExpansion.Kind == AvailableSpaceKind.Definite)
        {
            // If the free space is zero the used flex fraction is zero; otherwise it is the result of
            // finding the size of an fr using all of the grid tracks and the available grid space.
            float availableSpace = axisAvailableSpaceForExpansion.Unwrap();
            float usedSpace = axisTracks.Sum(static track => track.BaseSize);
            float freeSpace = availableSpace - usedSpace;
            flexFraction = freeSpace <= 0.0f ? 0.0f : FindSizeOfFr(axisTracks, availableSpace);
        }
        else if (axisAvailableSpaceForExpansion.Kind == AvailableSpaceKind.MinContent)
        {
            // If sizing the grid container under a min-content constraint the used flex fraction is
            // zero.
            flexFraction = 0.0f;
        }
        else
        {
            // Otherwise, if the free space is an indefinite length, the used flex fraction is the
            // maximum of:
            //
            // For each flexible track, if the flexible track's flex factor is greater than one, the
            // result of dividing the track's base size by its flex factor; otherwise the track's
            // base size.
            float trackMax = 0.0f;
            bool seenTrack = false;
            for (int i = 0; i < axisTracks.Count; i++)
            {
                var track = axisTracks[i];
                if (!track.MaxTrackSizingFunction.IsFr())
                {
                    continue;
                }

                float flexFactor = track.FlexFactor();
                float value = flexFactor > 1.0f ? track.BaseSize / flexFactor : track.BaseSize;
                if (!seenTrack || Sys.TotalCmp(value, trackMax) >= 0)
                {
                    trackMax = value;
                    seenTrack = true;
                }
            }

            if (!seenTrack)
            {
                trackMax = 0.0f;
            }

            // For each grid item that crosses a flexible track, the result of finding the size of an
            // fr using all the grid tracks that the item crosses and a space to fill of the item's
            // max-content contribution.
            float itemMax = 0.0f;
            bool seenItem = false;
            foreach (var item in items)
            {
                if (!item.CrossesFlexibleTrack(axis))
                {
                    continue;
                }

                var tracks = item.TrackRangeExcludingLines(axis, axisTracks);
                float maxContentContribution = item.MaxContentContributionCached(
                    axis, tree, GeometryExtensions.SizeNone, GeometryExtensions.SizeNone);
                float value = FindSizeOfFr(tracks, maxContentContribution);
                if (!seenItem || Sys.TotalCmp(value, itemMax) >= 0)
                {
                    itemMax = value;
                    seenItem = true;
                }
            }

            if (!seenItem)
            {
                itemMax = 0.0f;
            }

            flexFraction = Sys.F32Max(trackMax, itemMax);

            // If using this flex fraction would cause the grid to be smaller than the grid
            // container's min-width/height (or larger than its max-width/height) then redo this step,
            // treating the free space as definite and the available grid space as equal to the grid
            // container's inner size when it's sized to its min-width/height (max-width/height).
            // (Note: min_size takes precedence over max_size.)
            float hypotheticalGridSize = 0.0f;
            for (int i = 0; i < axisTracks.Count; i++)
            {
                var track = axisTracks[i];
                hypotheticalGridSize += track.MaxTrackSizingFunction.IsFr()
                    ? Sys.F32Max(track.BaseSize, track.MaxTrackSizingFunction.RawValue() * flexFraction)
                    : track.BaseSize;
            }

            float resolvedMinSize = axisMinSize ?? 0.0f;
            float resolvedMaxSize = axisMaxSize ?? float.PositiveInfinity;
            if (hypotheticalGridSize < resolvedMinSize)
            {
                flexFraction = FindSizeOfFr(axisTracks, resolvedMinSize);
            }
            else if (hypotheticalGridSize > resolvedMaxSize)
            {
                flexFraction = FindSizeOfFr(axisTracks, resolvedMaxSize);
            }
        }

        // For each flexible track, if the product of the used flex fraction and the track's flex
        // factor is greater than the track's base size, set its base size to that product.
        for (int i = 0; i < axisTracks.Count; i++)
        {
            var track = axisTracks[i];
            if (!track.MaxTrackSizingFunction.IsFr())
            {
                continue;
            }

            track.BaseSize = Sys.F32Max(track.BaseSize, track.MaxTrackSizingFunction.RawValue() * flexFraction);
        }
    }

    /// <summary>
    /// 11.7.1 Find the Size of an fr: the largest size that an fr unit can be without exceeding the
    /// target size.
    /// </summary>
    private static float FindSizeOfFr(TrackSlice tracks, float spaceToFill)
    {
        // Handle the trivial case where there is no space to fill. Do not remove: otherwise the loop
        // below will loop infinitely.
        if (spaceToFill == 0.0f)
        {
            return 0.0f;
        }

        float hypotheticalFrSize = float.PositiveInfinity;
        float previousIterHypotheticalFrSize;
        while (true)
        {
            // Let leftover space be the space to fill minus the base sizes of the non-flexible grid
            // tracks. Let flex factor sum be the sum of the flex factors of the flexible tracks; if
            // it is less than 1, set it to 1 instead.
            float usedSpace = 0.0f;
            float naiveFlexFactorSum = 0.0f;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];

                // Tracks for which flex_factor * hypothetical_fr_size < base_size are inflexible.
                if (track.MaxTrackSizingFunction.IsFr()
                    && track.MaxTrackSizingFunction.RawValue() * hypotheticalFrSize >= track.BaseSize)
                {
                    naiveFlexFactorSum += track.MaxTrackSizingFunction.RawValue();
                }
                else
                {
                    usedSpace += track.BaseSize;
                }
            }

            float leftoverSpace = spaceToFill - usedSpace;
            float flexFactor = Sys.F32Max(naiveFlexFactorSum, 1.0f);

            previousIterHypotheticalFrSize = hypotheticalFrSize;
            hypotheticalFrSize = leftoverSpace / flexFactor;

            // If the product of the hypothetical fr size and a flexible track's flex factor is less
            // than the track's base size, restart this algorithm treating all such tracks as
            // inflexible.
            bool hypotheticalFrSizeIsValid = true;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (!track.MaxTrackSizingFunction.IsFr())
                {
                    continue;
                }

                float trackFlexFactor = track.MaxTrackSizingFunction.RawValue();
                if (!((trackFlexFactor * hypotheticalFrSize >= track.BaseSize)
                    || (trackFlexFactor * previousIterHypotheticalFrSize < track.BaseSize)))
                {
                    hypotheticalFrSizeIsValid = false;
                    break;
                }
            }

            if (hypotheticalFrSizeIsValid)
            {
                break;
            }
        }

        return hypotheticalFrSize;
    }

    /// <summary>
    /// 11.8 Stretch auto Tracks: expand tracks that have an auto max track sizing function by
    /// dividing any remaining positive, definite free space equally amongst them.
    /// </summary>
    private static void StretchAutoTracks(
        TrackSlice axisTracks,
        float? axisMinSize,
        AvailableSpace axisAvailableSpaceForExpansion)
    {
        int numAutoTracks = axisTracks.CountWhere(static track => track.MaxTrackSizingFunction.IsAuto());
        if (numAutoTracks <= 0)
        {
            return;
        }

        float usedSpace = axisTracks.Sum(static track => track.BaseSize);

        // If the free space is indefinite, but the grid container has a definite min-width/height,
        // use that size to calculate the free space for this step instead.
        float freeSpace;
        if (axisAvailableSpaceForExpansion.IsDefinite)
        {
            freeSpace = axisAvailableSpaceForExpansion.ComputeFreeSpace(usedSpace);
        }
        else
        {
            freeSpace = axisMinSize.HasValue ? axisMinSize.Value - usedSpace : 0.0f;
        }

        if (freeSpace > 0.0f)
        {
            float extraSpacePerAutoTrack = freeSpace / numAutoTracks;
            for (int i = 0; i < axisTracks.Count; i++)
            {
                var track = axisTracks[i];
                if (track.MaxTrackSizingFunction.IsAuto())
                {
                    track.BaseSize += extraSpacePerAutoTrack;
                }
            }
        }
    }

    /// <summary>Helper function for distributing space to tracks evenly.</summary>
    private static float DistributeSpaceUpToLimits(
        float spaceToDistribute,
        TrackSlice tracks,
        Func<GridTrack, bool> trackIsAffected,
        Func<GridTrack, float> trackDistributionProportion,
        Func<GridTrack, float> trackAffectedProperty,
        Func<GridTrack, float> trackLimit)
    {
        // A small constant to avoid infinite loops due to rounding errors.
        const float Threshold = 0.01f;

        while (spaceToDistribute > Threshold)
        {
            float trackDistributionProportionSum = 0.0f;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (trackAffectedProperty(track) + track.ItemIncurredIncrease < trackLimit(track)
                    && trackIsAffected(track))
                {
                    trackDistributionProportionSum += trackDistributionProportion(track);
                }
            }

            if (trackDistributionProportionSum == 0.0f)
            {
                break;
            }

            // Compute the item-incurred increase for this iteration. Rust's min_by returns the FIRST
            // minimum on ties, so the comparison here is strict.
            float minIncreaseLimit = 0.0f;
            bool seen = false;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (!(trackAffectedProperty(track) + track.ItemIncurredIncrease < trackLimit(track))
                    || !trackIsAffected(track))
                {
                    continue;
                }

                float value = (trackLimit(track) - trackAffectedProperty(track) - track.ItemIncurredIncrease)
                    / trackDistributionProportion(track);
                if (!seen || Sys.TotalCmp(value, minIncreaseLimit) < 0)
                {
                    minIncreaseLimit = value;
                    seen = true;
                }
            }

            float iterationItemIncurredIncrease = Sys.F32Min(
                minIncreaseLimit, spaceToDistribute / trackDistributionProportionSum);

            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (!trackIsAffected(track))
                {
                    continue;
                }

                float currentSize = trackAffectedProperty(track) + track.ItemIncurredIncrease;
                float limit = trackLimit(track);
                if (currentSize >= limit)
                {
                    continue;
                }

                float increase = iterationItemIncurredIncrease * trackDistributionProportion(track);
                if (increase > 0.0f && currentSize + increase <= limit + Threshold)
                {
                    track.ItemIncurredIncrease += increase;
                    spaceToDistribute -= increase;
                }
            }
        }

        return spaceToDistribute;
    }
}

/// <summary>Stable sorting helpers matching Rust's <c>slice::sort_by</c>.</summary>
internal static class GridSort
{
    /// <summary>
    /// Sort in place with a stable sort. .NET's <c>List{T}.Sort</c> is unstable, while Rust's
    /// <c>sort_by</c>/<c>sort_by_key</c> are stable, and the grid algorithm depends on that.
    /// </summary>
    public static void StableSort<T>(List<T> list, Comparison<T> comparison)
    {
        int count = list.Count;
        if (count < 2)
        {
            return;
        }

        var buffer = new (T Item, int Index)[count];
        for (int i = 0; i < count; i++)
        {
            buffer[i] = (list[i], i);
        }

        Array.Sort(buffer, (a, b) =>
        {
            int result = comparison(a.Item, b.Item);
            return result != 0 ? result : a.Index.CompareTo(b.Index);
        });

        for (int i = 0; i < count; i++)
        {
            list[i] = buffer[i].Item;
        }
    }
}
