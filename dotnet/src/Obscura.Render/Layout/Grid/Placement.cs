// Port of vendor/taffy/src/compute/grid/placement.rs
//
// Implements placing items in the grid and resolving the implicit grid.
// https://www.w3.org/TR/css-grid-1/#placement
namespace Obscura.Render.Layout;

/// <summary>8.5. Grid Item Placement Algorithm.</summary>
internal static class GridPlacementAlgorithm
{
    /// <summary>Returns whether placement/search should run in reverse for this axis.</summary>
    private static bool AxisIsReversed(Direction direction, AbsoluteAxis axis) =>
        direction.IsRtl() && axis == AbsoluteAxis.Horizontal;

    /// <summary>Advances the cursor by one track in the active search direction.</summary>
    private static OriginZeroLine AdvancePosition(OriginZeroLine position, bool axisIsReversed) =>
        axisIsReversed
            ? new OriginZeroLine((short)(position.Value - 1))
            : new OriginZeroLine((short)(position.Value + 1));

    /// <summary>Returns the initial search line for sparse/dense placement in the given direction.</summary>
    private static OriginZeroLine SearchStartLine(
        OriginZeroLine gridStartLine,
        OriginZeroLine gridEndLine,
        bool axisIsReversed) => axisIsReversed ? gridEndLine - (ushort)1 : gridStartLine;

    /// <summary>Resolves an indefinite span at <paramref name="position"/>, respecting the direction.</summary>
    private static Line<OriginZeroLine> ResolveIndefiniteGridSpan(
        OriginZeroLine position,
        ushort span,
        bool axisIsReversed) =>
        axisIsReversed
            ? new Line<OriginZeroLine>((position - span) + (ushort)1, position + (ushort)1)
            : new Line<OriginZeroLine>(position, position + span);

    /// <summary>Mirrors a horizontal span around the explicit grid width.</summary>
    private static Line<OriginZeroLine> MirrorHorizontalSpan(
        Line<OriginZeroLine> span,
        ushort explicitColCount)
    {
        short explicitColEndLine = (short)explicitColCount;
        return new Line<OriginZeroLine>(
            new OriginZeroLine((short)(explicitColEndLine - span.End.Value)),
            new OriginZeroLine((short)(explicitColEndLine - span.Start.Value)));
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
            placements[i] = MapChildStyleToOriginZeroPlacement(children[i].Style);
        }

        // 1. Place children with definite positions
        for (int i = 0; i < childCount; i++)
        {
            var placement = placements[i];
            if (!placement.Horizontal.IsDefinite() || !placement.Vertical.IsDefinite())
            {
                continue;
            }

            var (rowSpan, colSpan) =
                PlaceDefiniteGridItem(placement, primaryAxis, direction, explicitColCount);
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
        for (int i = 0; i < childCount; i++)
        {
            var placement = placements[i];
            if (!placement.Get(secondaryAxis).IsDefinite() || placement.Get(primaryAxis).IsDefinite())
            {
                continue;
            }

            var (primarySpan, secondarySpan) = PlaceDefiniteSecondaryAxisItem(
                cellOccupancyMatrix, placement, gridAutoFlow, direction, explicitColCount);

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
                cellOccupancyMatrix, placement, gridAutoFlow, gridPosition, direction, explicitColCount);

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
        ushort explicitColCount)
    {
        var primarySpan = MaybeMirrorSpan(
            placement.Get(primaryAxis).ResolveDefiniteGridLines(), primaryAxis, direction, explicitColCount);
        var secondarySpan = MaybeMirrorSpan(
            placement.Get(primaryAxis.OtherAxis()).ResolveDefiniteGridLines(),
            primaryAxis.OtherAxis(),
            direction,
            explicitColCount);

        return (primarySpan, secondarySpan);
    }

    /// <summary>Step 2. Place remaining children with definite secondary axis positions.</summary>
    private static (Line<OriginZeroLine> Primary, Line<OriginZeroLine> Secondary)
        PlaceDefiniteSecondaryAxisItem(
            CellOccupancyMatrix cellOccupancyMatrix,
            InBothAbsAxis<Line<OriginZeroGridPlacement>> placement,
            GridAutoFlow autoFlow,
            Direction direction,
            ushort explicitColCount)
    {
        var primaryAxis = autoFlow.PrimaryAxis();
        var secondaryAxis = primaryAxis.OtherAxis();
        bool primaryAxisIsReversed = AxisIsReversed(direction, primaryAxis);
        var primaryAxisGridStartLine = cellOccupancyMatrix.TrackCountsFor(primaryAxis).ImplicitStartLine();
        var primaryAxisGridEndLine = cellOccupancyMatrix.TrackCountsFor(primaryAxis).ImplicitEndLine();

        var secondaryAxisPlacement = MaybeMirrorSpan(
            placement.Get(secondaryAxis).ResolveDefiniteGridLines(),
            secondaryAxis,
            direction,
            explicitColCount);

        OriginZeroLine startingPosition;
        if (autoFlow.IsDense())
        {
            startingPosition =
                SearchStartLine(primaryAxisGridStartLine, primaryAxisGridEndLine, primaryAxisIsReversed);
        }
        else
        {
            var lookupResult = primaryAxisIsReversed
                ? cellOccupancyMatrix.FirstOfType(
                    primaryAxis, secondaryAxisPlacement.Start, CellOccupancyState.AutoPlaced)
                : cellOccupancyMatrix.LastOfType(
                    primaryAxis, secondaryAxisPlacement.Start, CellOccupancyState.AutoPlaced);
            startingPosition = lookupResult
                ?? SearchStartLine(primaryAxisGridStartLine, primaryAxisGridEndLine, primaryAxisIsReversed);
        }

        ushort primaryAxisSpan = placement.Get(primaryAxis).IndefiniteSpan();

        var position = startingPosition;
        while (true)
        {
            var primaryAxisPlacement =
                ResolveIndefiniteGridSpan(position, primaryAxisSpan, primaryAxisIsReversed);

            bool doesFit = cellOccupancyMatrix.LineAreaIsUnoccupied(
                primaryAxis, primaryAxisPlacement, secondaryAxisPlacement);

            if (doesFit)
            {
                return (primaryAxisPlacement, secondaryAxisPlacement);
            }

            position = AdvancePosition(position, primaryAxisIsReversed);
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
            ushort explicitColCount)
    {
        var primaryAxis = autoFlow.PrimaryAxis();
        var secondaryAxis = primaryAxis.OtherAxis();
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
            var primarySpan = MaybeMirrorSpan(
                primaryPlacementStyle.ResolveDefiniteGridLines(), primaryAxis, direction, explicitColCount);

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

                return (primarySpan, secondarySpan);
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
                    secondaryIdx = AdvancePosition(secondaryIdx, secondaryAxisIsReversed);
                    primaryIdx = primaryStartPosition;
                    continue;
                }

                if (LineAreaIsOccupied(primarySpan, secondarySpan))
                {
                    primaryIdx = AdvancePosition(primaryIdx, primaryAxisIsReversed);
                    continue;
                }

                return (primarySpan, secondarySpan);
            }
        }
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
            node, colSpan, rowSpan, style, parentAlignItems, parentJustifyItems, (ushort)index));
    }
}
