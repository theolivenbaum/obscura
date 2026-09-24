// Port of vendor/taffy/src/compute/grid/placement.rs
//
// Implements placing items in the grid and resolving the implicit grid.
// https://www.w3.org/TR/css-grid-1/#placement
namespace PocketCalculator.Render.Layout;

/// <summary>8.5. Grid Item Placement Algorithm.</summary>
internal static class GridPlacementAlgorithm
{
    /// <summary>Returns whether placement/search should run in reverse for this axis.</summary>
    private static bool AxisIsReversed(Direction direction, AbsoluteAxis axis) =>
        direction.IsRtl() && axis == AbsoluteAxis.Horizontal;

    /// <summary>Advances the cursor by one track in the active search direction.</summary>
    private static OriginZeroLine AdvancePosition(OriginZeroLine position, bool axisIsReversed) =>
        axisIsReversed
            ? new OriginZeroLine(position.Value - 1)
            : new OriginZeroLine(position.Value + 1);

    /// <summary>Returns the initial search line for sparse/dense placement in the given direction.</summary>
    private static OriginZeroLine SearchStartLine(
        OriginZeroLine gridStartLine,
        OriginZeroLine gridEndLine,
        bool axisIsReversed) => axisIsReversed ? gridEndLine - 1 : gridStartLine;

    /// <summary>Resolves an indefinite span at <paramref name="position"/>, respecting the direction.</summary>
    private static Line<OriginZeroLine> ResolveIndefiniteGridSpan(
        OriginZeroLine position,
        ushort span,
        bool axisIsReversed) =>
        axisIsReversed
            ? new Line<OriginZeroLine>((position - span) + 1, position + 1)
            : new Line<OriginZeroLine>(position, position + span);

    /// <summary>Mirrors a horizontal span around the explicit grid width.</summary>
    private static Line<OriginZeroLine> MirrorHorizontalSpan(
        Line<OriginZeroLine> span,
        ushort explicitColCount)
    {
        int explicitColEndLine = explicitColCount;
        return new Line<OriginZeroLine>(
            new OriginZeroLine(explicitColEndLine - span.End.Value),
            new OriginZeroLine(explicitColEndLine - span.Start.Value));
    }

    /// <summary>Mirrors horizontal spans for RTL while leaving all other spans unchanged.</summary>
    private static Line<OriginZeroLine> MaybeMirrorSpan(
        Line<OriginZeroLine> span,
        AbsoluteAxis axis,
        Direction direction,
        ushort explicitColCount) =>
        axis == AbsoluteAxis.Horizontal && direction.IsRtl()
            ? MirrorHorizontalSpan(span, explicitColCount)
            : span;

    /// <summary>
    /// Place items into the grid, generating new rows/columns into the implicit grid as required.
    /// <see href="https://www.w3.org/TR/css-grid-2/#auto-placement-algo"/>
    /// </summary>
    public static void PlaceGridItems(
        CellOccupancyMatrix cellOccupancyMatrix,
        List<GridItem> items,
        IReadOnlyList<(int Index, NodeId Node, IGridItemStyle Style)> children,
        Direction direction,
        GridAutoFlow gridAutoFlow,
        AlignItems alignItems,
        AlignItems justifyItems,
        NamedLineResolver namedLineResolver)
    {
        var primaryAxis = gridAutoFlow.PrimaryAxis();
        var secondaryAxis = primaryAxis.OtherAxis();
        ushort explicitColCount = cellOccupancyMatrix.TrackCountsFor(AbsoluteAxis.Horizontal).Explicit;
        ushort explicitRowCount = cellOccupancyMatrix.TrackCountsFor(AbsoluteAxis.Vertical).Explicit;

        InBothAbsAxis<Line<OriginZeroGridPlacement>> MapChildStyleToOriginZeroPlacement(IGridItemStyle style) =>
            new(
                namedLineResolver.ResolveColumnNames(style.GridColumn).IntoOriginZero(explicitColCount),
                namedLineResolver.ResolveRowNames(style.GridRow).IntoOriginZero(explicitRowCount));

        // Precompute placements once. taffy re-invokes the children iterator three times; the values
        // it produces are pure functions of the child styles, so computing them once is equivalent.
        int childCount = children.Count;
        var placements = new InBothAbsAxis<Line<OriginZeroGridPlacement>>[childCount];
        for (int i = 0; i < childCount; i++)
        {
            placements[i] = ClampPlacementSpans(MapChildStyleToOriginZeroPlacement(children[i].Style));
        }

        // Deviation from taffy, which places items at any line its 16-bit coordinates reach and
        // grows the occupancy matrix (rows x columns) to match: every span is pulled into a
        // window of GridLimits.MaxAxisTracks tracks per axis, as Chromium pulls items placed past
        // kGridMaxTracks back into the last track.
        var windows = new InBothAbsAxis<GridWindow>(
            PlacementWindow(cellOccupancyMatrix, placements, AbsoluteAxis.Horizontal, direction, explicitColCount),
            PlacementWindow(cellOccupancyMatrix, placements, AbsoluteAxis.Vertical, direction, explicitColCount));

        // 1. Place children with definite positions
        for (int i = 0; i < childCount; i++)
        {
            var placement = placements[i];
            if (!placement.Horizontal.IsDefinite() || !placement.Vertical.IsDefinite())
            {
                continue;
            }

            var (rowSpan, colSpan) =
                PlaceDefiniteGridItem(placement, primaryAxis, direction, explicitColCount, windows);
            RecordGridPlacement(
                cellOccupancyMatrix,
                items,
                children[i].Node,
                children[i].Index,
                children[i].Style,
                alignItems,
                justifyItems,
                primaryAxis,
                rowSpan,
                colSpan,
                CellOccupancyState.DefinitelyPlaced);
        }

        // 2. Place remaining children with definite secondary axis positions
        //
        // Deviation from taffy, which starts the sparse search at the last auto-placed cell of
        // the item's first row (so after any step 2 item that merely spans into that row) and
        // reads that cell back with the other axis's track counts. Chromium keeps one cursor per
        // start line, the end of the last item this step placed with that start line, which is
        // the spec's "past any grid items previously placed in this row by this step".
        Dictionary<int, OriginZeroLine>? cursors = gridAutoFlow.IsDense() ? null : [];
        bool primaryIsReversed = AxisIsReversed(direction, primaryAxis);
        for (int i = 0; i < childCount; i++)
        {
            var placement = placements[i];
            if (!placement.Get(secondaryAxis).IsDefinite() || placement.Get(primaryAxis).IsDefinite())
            {
                continue;
            }

            var (primarySpan, secondarySpan) = PlaceDefiniteSecondaryAxisItem(
                cellOccupancyMatrix, placement, gridAutoFlow, direction, explicitColCount, windows, cursors);
            if (cursors is not null)
            {
                cursors[secondarySpan.Start.Value] = primaryIsReversed ? primarySpan.Start - 1 : primarySpan.End;
            }

            RecordGridPlacement(
                cellOccupancyMatrix,
                items,
                children[i].Node,
                children[i].Index,
                children[i].Style,
                alignItems,
                justifyItems,
                primaryAxis,
                primarySpan,
                secondarySpan,
                CellOccupancyState.AutoPlaced);
        }

        // 3. Determining the number of columns in the implicit grid is already accounted for by the
        //    grid size estimate and by expand_to_fit_range.

        // 4. Position the remaining grid items
        var primaryAxisGridStartLine = cellOccupancyMatrix.TrackCountsFor(primaryAxis).ImplicitStartLine();
        var primaryAxisGridEndLine = cellOccupancyMatrix.TrackCountsFor(primaryAxis).ImplicitEndLine();
        var secondaryAxisGridStartLine = cellOccupancyMatrix.TrackCountsFor(secondaryAxis).ImplicitStartLine();
        var secondaryAxisGridEndLine = cellOccupancyMatrix.TrackCountsFor(secondaryAxis).ImplicitEndLine();
        bool primaryAxisIsReversed = AxisIsReversed(direction, primaryAxis);
        var gridStartPosition = (
            Primary: SearchStartLine(primaryAxisGridStartLine, primaryAxisGridEndLine, primaryAxisIsReversed),
            Secondary: SearchStartLine(
                secondaryAxisGridStartLine,
                secondaryAxisGridEndLine,
                AxisIsReversed(direction, secondaryAxis)));
        var gridPosition = gridStartPosition;

        for (int i = 0; i < childCount; i++)
        {
            var placement = placements[i];
            if (placement.Get(secondaryAxis).IsDefinite())
            {
                continue;
            }

            var (primarySpan, secondarySpan) = PlaceIndefinitelyPositionedItem(
                cellOccupancyMatrix, placement, gridAutoFlow, gridPosition, direction, explicitColCount, windows);

            RecordGridPlacement(
                cellOccupancyMatrix,
                items,
                children[i].Node,
                children[i].Index,
                children[i].Style,
                alignItems,
                justifyItems,
                primaryAxis,
                primarySpan,
                secondarySpan,
                CellOccupancyState.AutoPlaced);

            // If using the "dense" placement algorithm then reset the grid position back to the start
            // ready for the next item. Otherwise set it to the position of the current item so that
            // the next item is placed after it.
            gridPosition = gridAutoFlow.IsDense()
                ? gridStartPosition
                : primaryAxisIsReversed
                    ? (primarySpan.Start, secondarySpan.Start)
                    : (primarySpan.End, secondarySpan.Start);
        }
    }

    /// <summary>Place a single definitely placed item into the grid.</summary>
    private static (Line<OriginZeroLine> Primary, Line<OriginZeroLine> Secondary) PlaceDefiniteGridItem(
        InBothAbsAxis<Line<OriginZeroGridPlacement>> placement,
        AbsoluteAxis primaryAxis,
        Direction direction,
        ushort explicitColCount,
        InBothAbsAxis<GridWindow> windows)
    {
        var primarySpan = MaybeMirrorSpan(
            placement.Get(primaryAxis).ResolveDefiniteGridLines(), primaryAxis, direction, explicitColCount);
        var secondarySpan = MaybeMirrorSpan(
            placement.Get(primaryAxis.OtherAxis()).ResolveDefiniteGridLines(),
            primaryAxis.OtherAxis(),
            direction,
            explicitColCount);

        return (windows.Get(primaryAxis).Clamp(primarySpan),
            windows.Get(primaryAxis.OtherAxis()).Clamp(secondarySpan));
    }

    /// <summary>Step 2. Place remaining children with definite secondary axis positions.</summary>
    private static (Line<OriginZeroLine> Primary, Line<OriginZeroLine> Secondary)
        PlaceDefiniteSecondaryAxisItem(
            CellOccupancyMatrix cellOccupancyMatrix,
            InBothAbsAxis<Line<OriginZeroGridPlacement>> placement,
            GridAutoFlow autoFlow,
            Direction direction,
            ushort explicitColCount,
            InBothAbsAxis<GridWindow> windows,
            Dictionary<int, OriginZeroLine>? cursors)
    {
        var primaryAxis = autoFlow.PrimaryAxis();
        var secondaryAxis = primaryAxis.OtherAxis();
        var primaryWindow = windows.Get(primaryAxis);
        bool primaryAxisIsReversed = AxisIsReversed(direction, primaryAxis);
        var primaryAxisGridStartLine = cellOccupancyMatrix.TrackCountsFor(primaryAxis).ImplicitStartLine();
        var primaryAxisGridEndLine = cellOccupancyMatrix.TrackCountsFor(primaryAxis).ImplicitEndLine();

        var secondaryAxisPlacement = windows.Get(secondaryAxis).Clamp(MaybeMirrorSpan(
            placement.Get(secondaryAxis).ResolveDefiniteGridLines(),
            secondaryAxis,
            direction,
            explicitColCount));

        var startingPosition =
            cursors is not null && cursors.TryGetValue(secondaryAxisPlacement.Start.Value, out var cursor)
                ? cursor
                : SearchStartLine(primaryAxisGridStartLine, primaryAxisGridEndLine, primaryAxisIsReversed);

        ushort primaryAxisSpan = placement.Get(primaryAxis).IndefiniteSpan();

        var position = startingPosition;
        while (true)
        {
            var primaryAxisPlacement =
                ResolveIndefiniteGridSpan(position, primaryAxisSpan, primaryAxisIsReversed);

            // Cells past the window are never occupied, so the search ends there at the latest;
            // a position found past it is pulled into the last track, as Chromium pulls one
            // found past kGridMaxTracks.
            //
            // taffy steps one track and re-tests. As in step 4, every candidate that still
            // contains the furthest occupied track fails, and so does one whose own track is
            // occupied, so jump past both: the same position is found in fewer probes.
            if (cellOccupancyMatrix.OccupiedPrimaryTrackBounds(
                    primaryAxis, primaryAxisPlacement, secondaryAxisPlacement) is not { } occupied)
            {
                return (primaryWindow.Clamp(primaryAxisPlacement), secondaryAxisPlacement);
            }

            position = cellOccupancyMatrix.NextFreePrimaryTrack(
                primaryAxis,
                primaryAxisIsReversed ? occupied.First - 1 : occupied.Last + 1,
                secondaryAxisPlacement,
                primaryAxisIsReversed);
        }
    }

    /// <summary>Step 4. Position the remaining grid items.</summary>
    private static (Line<OriginZeroLine> Primary, Line<OriginZeroLine> Secondary)
        PlaceIndefinitelyPositionedItem(
            CellOccupancyMatrix cellOccupancyMatrix,
            InBothAbsAxis<Line<OriginZeroGridPlacement>> placement,
            GridAutoFlow autoFlow,
            (OriginZeroLine Primary, OriginZeroLine Secondary) gridPosition,
            Direction direction,
            ushort explicitColCount,
            InBothAbsAxis<GridWindow> windows)
    {
        var primaryAxis = autoFlow.PrimaryAxis();
        var secondaryAxis = primaryAxis.OtherAxis();
        var primaryWindow = windows.Get(primaryAxis);
        var secondaryWindow = windows.Get(secondaryAxis);
        bool primaryAxisIsReversed = AxisIsReversed(direction, primaryAxis);
        bool secondaryAxisIsReversed = AxisIsReversed(direction, secondaryAxis);

        var primaryPlacementStyle = placement.Get(primaryAxis);
        var secondaryPlacementStyle = placement.Get(secondaryAxis);

        ushort secondarySpanCount = secondaryPlacementStyle.IndefiniteSpan();
        bool hasDefinitePrimaryAxisPosition = primaryPlacementStyle.IsDefinite();
        var primaryAxisGridStartLine = cellOccupancyMatrix.TrackCountsFor(primaryAxis).ImplicitStartLine();
        var primaryAxisGridEndLine = cellOccupancyMatrix.TrackCountsFor(primaryAxis).ImplicitEndLine();
        var secondaryAxisGridStartLine = cellOccupancyMatrix.TrackCountsFor(secondaryAxis).ImplicitStartLine();
        var secondaryAxisGridEndLine = cellOccupancyMatrix.TrackCountsFor(secondaryAxis).ImplicitEndLine();
        var primaryStartPosition =
            SearchStartLine(primaryAxisGridStartLine, primaryAxisGridEndLine, primaryAxisIsReversed);
        var secondaryStartPosition =
            SearchStartLine(secondaryAxisGridStartLine, secondaryAxisGridEndLine, secondaryAxisIsReversed);

        bool LineAreaIsOccupied(Line<OriginZeroLine> primary, Line<OriginZeroLine> secondary) =>
            !cellOccupancyMatrix.LineAreaIsUnoccupied(primaryAxis, primary, secondary);

        var primaryIdx = gridPosition.Primary;
        var secondaryIdx = gridPosition.Secondary;

        if (hasDefinitePrimaryAxisPosition)
        {
            var primarySpan = primaryWindow.Clamp(MaybeMirrorSpan(
                primaryPlacementStyle.ResolveDefiniteGridLines(), primaryAxis, direction, explicitColCount));

            // Compute the secondary axis starting position for the search.
            if (autoFlow.IsDense())
            {
                secondaryIdx = secondaryStartPosition;
            }
            else
            {
                bool shouldAdvanceSecondary = primaryAxisIsReversed
                    ? primarySpan.Start > primaryIdx
                    : primarySpan.Start < primaryIdx;
                if (shouldAdvanceSecondary)
                {
                    secondaryIdx = AdvancePosition(secondaryIdx, secondaryAxisIsReversed);
                }
            }

            // Item has a fixed primary axis position: increment the secondary axis position until we
            // find a space that the item fits in.
            while (true)
            {
                var secondarySpan =
                    ResolveIndefiniteGridSpan(secondaryIdx, secondarySpanCount, secondaryAxisIsReversed);

                if (LineAreaIsOccupied(primarySpan, secondarySpan))
                {
                    secondaryIdx = AdvancePosition(secondaryIdx, secondaryAxisIsReversed);
                    continue;
                }

                // A position past the window is pulled into its last track.
                return (primarySpan, secondaryWindow.Clamp(secondarySpan));
            }
        }
        else
        {
            ushort primarySpanCount = primaryPlacementStyle.IndefiniteSpan();

            // The item has no fixed axis, so search along the primary axis until we hit the end of the
            // already existent tracks, then reset the primary axis and increment the secondary axis.
            while (true)
            {
                var primarySpan =
                    ResolveIndefiniteGridSpan(primaryIdx, primarySpanCount, primaryAxisIsReversed);
                var secondarySpan =
                    ResolveIndefiniteGridSpan(secondaryIdx, secondarySpanCount, secondaryAxisIsReversed);

                bool primaryOutOfBounds = primaryAxisIsReversed
                    ? primarySpan.Start < primaryAxisGridStartLine
                    : primarySpan.End > primaryAxisGridEndLine;
                if (primaryOutOfBounds)
                {
                    // Past the window every row is empty, so a span that still does not fit
                    // the primary axis there never will: stop in the window's last track.
                    if (secondaryAxisIsReversed
                            ? secondarySpan.End.Value <= secondaryWindow.Start
                            : secondarySpan.Start.Value >= secondaryWindow.End)
                    {
                        return (primaryWindow.Clamp(primarySpan), secondaryWindow.Clamp(secondarySpan));
                    }

                    secondaryIdx = AdvancePosition(secondaryIdx, secondaryAxisIsReversed);
                    primaryIdx = primaryStartPosition;
                    continue;
                }

                // taffy steps one track and re-tests. Every candidate that still contains an
                // occupied track fails the same way, so jump straight past the furthest one: the
                // same position is found, without probing a full row track by track (which made a
                // grid filled by one spanning item cost rows x columns probes per auto item).
                if (cellOccupancyMatrix.OccupiedPrimaryTrackBounds(primaryAxis, primarySpan, secondarySpan)
                    is { } occupied)
                {
                    var from = primaryAxisIsReversed
                        ? new OriginZeroLine(occupied.First.Value - 1)
                        : new OriginZeroLine(occupied.Last.Value + 1);
                    primaryIdx = cellOccupancyMatrix.NextFreePrimaryTrack(
                        primaryAxis, from, secondarySpan, primaryAxisIsReversed);
                    continue;
                }

                // A position past the window is pulled into its last track.
                return (primaryWindow.Clamp(primarySpan), secondaryWindow.Clamp(secondarySpan));
            }
        }
    }

    /// <summary>Clamp the spans of a placement to <see cref="GridLimits.MaxTracks"/>.</summary>
    private static InBothAbsAxis<Line<OriginZeroGridPlacement>> ClampPlacementSpans(
        InBothAbsAxis<Line<OriginZeroGridPlacement>> placement) =>
        new(ClampLineSpans(placement.Horizontal), ClampLineSpans(placement.Vertical));

    private static Line<OriginZeroGridPlacement> ClampLineSpans(Line<OriginZeroGridPlacement> line) =>
        new(ClampSpan(line.Start), ClampSpan(line.End));

    private static OriginZeroGridPlacement ClampSpan(OriginZeroGridPlacement placement) =>
        placement.Kind == GenericGridPlacementKind.Span && placement.SpanValue > GridLimits.MaxTracks
            ? OriginZeroGridPlacement.FromSpan(GridLimits.ClampSpan(placement.SpanValue))
            : placement;

    /// <summary>
    /// The window an axis's items are pulled into: its negative implicit tracks are the ones the
    /// earliest definitely placed item (or the size estimate) asks for, as far as the limit
    /// allows, and the rest of the limit lies after them.
    /// </summary>
    private static GridWindow PlacementWindow(
        CellOccupancyMatrix cellOccupancyMatrix,
        InBothAbsAxis<Line<OriginZeroGridPlacement>>[] placements,
        AbsoluteAxis axis,
        Direction direction,
        ushort explicitColCount)
    {
        var counts = cellOccupancyMatrix.TrackCountsFor(axis);
        int explicitCount = counts.Explicit;

        // A reversed axis (the columns of an RTL grid) auto-places towards its start, so there
        // the window is anchored at the far end instead: work in unmirrored lines and mirror
        // the window back.
        bool reversed = AxisIsReversed(direction, axis);
        int earliest = reversed ? -counts.PositiveImplicit : -counts.NegativeImplicit;
        foreach (var placement in placements)
        {
            var line = placement.Get(axis);
            if (!line.IsDefinite())
            {
                continue;
            }

            var span = MaybeMirrorSpan(line.ResolveDefiniteGridLines(), axis, direction, explicitColCount);
            earliest = Math.Min(earliest, reversed ? explicitCount - span.End.Value : span.Start.Value);
        }

        var window = GridLimits.WindowFor(earliest, explicitCount);
        return reversed ? new GridWindow(explicitCount - window.End, explicitCount - window.Start) : window;
    }

    /// <summary>
    /// Record the grid item in both the CellOccupancyMatrix and the grid items list once a definite
    /// placement has been determined.
    /// </summary>
    private static void RecordGridPlacement(
        CellOccupancyMatrix cellOccupancyMatrix,
        List<GridItem> items,
        NodeId node,
        int index,
        IGridItemStyle style,
        AlignItems parentAlignItems,
        AlignItems parentJustifyItems,
        AbsoluteAxis primaryAxis,
        Line<OriginZeroLine> primarySpan,
        Line<OriginZeroLine> secondarySpan,
        CellOccupancyState placementType)
    {
        // Mark the area of the grid as occupied
        cellOccupancyMatrix.MarkAreaAs(primaryAxis, primarySpan, secondarySpan, placementType);

        var colSpan = primaryAxis == AbsoluteAxis.Horizontal ? primarySpan : secondarySpan;
        var rowSpan = primaryAxis == AbsoluteAxis.Horizontal ? secondarySpan : primarySpan;

        items.Add(GridItem.NewWithPlacementStyleAndOrder(
            node, colSpan, rowSpan, style, parentAlignItems, parentJustifyItems, index));
    }
}
