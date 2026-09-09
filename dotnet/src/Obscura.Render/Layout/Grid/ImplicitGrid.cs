// Port of vendor/taffy/src/compute/grid/implicit_grid.rs
//
// This module is not required for spec compliance, but is used as a performance
// optimisation to reduce the number of allocations required when creating a grid.
namespace Obscura.Render.Layout;

/// <summary>Estimation of the number of rows and columns in the implicit grid.</summary>
internal static class ImplicitGrid
{
    /// <summary>
    /// Estimate the number of rows and columns in the grid.
    /// </summary>
    /// <remarks>
    /// The estimates for the explicit and negative implicit track counts are exact. The estimate for
    /// the positive implicit track count is a lower bound, as auto-placement can affect it in ways
    /// which are impossible to predict until the auto-placement algorithm has run.
    /// </remarks>
    public static (TrackCounts Columns, TrackCounts Rows) ComputeGridSizeEstimate(
        ushort explicitColCount,
        ushort explicitRowCount,
        Direction direction,
        IEnumerable<IGridItemStyle> childStylesIter)
    {
        var (colMin, colMax, colMaxSpan, rowMin, rowMax, rowMaxSpan) =
            GetKnownChildPositions(childStylesIter, explicitColCount, explicitRowCount, direction);

        ushort negativeImplicitInlineTracks = colMin.ImpliedNegativeImplicitTracks();
        ushort explicitInlineTracks = explicitColCount;
        ushort positiveImplicitInlineTracks = colMax.ImpliedPositiveImplicitTracks(explicitColCount);
        ushort negativeImplicitBlockTracks = rowMin.ImpliedNegativeImplicitTracks();
        ushort explicitBlockTracks = explicitRowCount;
        ushort positiveImplicitBlockTracks = rowMax.ImpliedPositiveImplicitTracks(explicitRowCount);

        // In each axis, adjust the positive track estimate if any items have a span that does not fit
        // within the total number of tracks in the estimate.
        int totInlineTracks =
            negativeImplicitInlineTracks + explicitInlineTracks + positiveImplicitInlineTracks;
        if (totInlineTracks < colMaxSpan)
        {
            positiveImplicitInlineTracks =
                (ushort)(colMaxSpan - explicitInlineTracks - negativeImplicitInlineTracks);
        }

        int totBlockTracks = negativeImplicitBlockTracks + explicitBlockTracks + positiveImplicitBlockTracks;
        if (totBlockTracks < rowMaxSpan)
        {
            positiveImplicitBlockTracks =
                (ushort)(rowMaxSpan - explicitBlockTracks - negativeImplicitBlockTracks);
        }

        var columnCounts = TrackCounts.FromRaw(
            negativeImplicitInlineTracks, explicitInlineTracks, positiveImplicitInlineTracks);
        var rowCounts = TrackCounts.FromRaw(
            negativeImplicitBlockTracks, explicitBlockTracks, positiveImplicitBlockTracks);

        return (columnCounts, rowCounts);
    }

    /// <summary>
    /// Iterate over children, producing an estimate of the min and max grid lines (in origin-zero
    /// coordinates) along with the span of each item.
    /// </summary>
    private static (
        OriginZeroLine ColMin,
        OriginZeroLine ColMax,
        ushort ColMaxSpan,
        OriginZeroLine RowMin,
        OriginZeroLine RowMax,
        ushort RowMaxSpan) GetKnownChildPositions(
        IEnumerable<IGridItemStyle> childrenIter,
        ushort explicitColCount,
        ushort explicitRowCount,
        Direction direction)
    {
        var colMin = new OriginZeroLine(0);
        var colMax = new OriginZeroLine(0);
        ushort colMaxSpan = 0;
        var rowMin = new OriginZeroLine(0);
        var rowMax = new OriginZeroLine(0);
        ushort rowMaxSpan = 0;

        foreach (var childStyle in childrenIter)
        {
            var colLine = childStyle.GridColumn;
            var rowLine = childStyle.GridRow;

            var (childColMin, childColMax, childColSpan) =
                ChildMinLineMaxLineSpan(colLine, explicitColCount);
            var (childRowMin, childRowMax, childRowSpan) =
                ChildMinLineMaxLineSpan(rowLine, explicitRowCount);

            // Placement mirrors horizontal spans in RTL, so mirror known column line bounds here to
            // keep implicit-grid pre-sizing consistent with actual placement.
            if (direction.IsRtl()
                && (childColMin != new OriginZeroLine(0) || childColMax != new OriginZeroLine(0)))
            {
                short explicitColEndLine = (short)explicitColCount;
                var mirroredMin = new OriginZeroLine((short)(explicitColEndLine - childColMax.Value));
                var mirroredMax = new OriginZeroLine((short)(explicitColEndLine - childColMin.Value));
                childColMin = mirroredMin;
                childColMax = mirroredMax;
            }

            colMin = OriginZeroLine.Min(colMin, childColMin);
            colMax = OriginZeroLine.Max(colMax, childColMax);
            colMaxSpan = Math.Max(colMaxSpan, childColSpan);
            rowMin = OriginZeroLine.Min(rowMin, childRowMin);
            rowMax = OriginZeroLine.Max(rowMax, childRowMax);
            rowMaxSpan = Math.Max(rowMaxSpan, childRowSpan);
        }

        return (colMin, colMax, colMaxSpan, rowMin, rowMax, rowMaxSpan);
    }

    /// <summary>
    /// Produces a conservative estimate of the greatest and smallest grid lines used by a single
    /// grid item, in origin-zero coordinates.
    /// </summary>
    internal static (OriginZeroLine Min, OriginZeroLine Max, ushort Span) ChildMinLineMaxLineSpan(
        Line<GridPlacement> line,
        ushort explicitTrackCount)
    {
        // 8.3.1. Grid Placement Conflict Handling
        // A. If the placement contains two lines and the start line is further end-ward than the end
        //    line, swap the two lines.
        // B. If the start line is equal to the end line, remove the end line.
        // C. If the placement contains two spans, remove the one contributed by the end property.
        // D. If the placement contains only a span for a named line, replace it with a span of 1.
        //
        // Named lines are ignored here as they are accounted for separately.
        var ozLine = line.IntoOriginZeroIgnoringNamed(explicitTrackCount);
        var start = ozLine.Start;
        var end = ozLine.End;

        bool startIsLine = start.Kind == GenericGridPlacementKind.Line;
        bool endIsLine = end.Kind == GenericGridPlacementKind.Line;

        OriginZeroLine min;
        if (startIsLine && endIsLine)
        {
            min = start.LineValue == end.LineValue
                ? start.LineValue
                : OriginZeroLine.Min(start.LineValue, end.LineValue);
        }
        else if (startIsLine)
        {
            // (Line, Auto) and (Line, Span)
            min = start.LineValue;
        }
        else if (endIsLine)
        {
            min = start.Kind == GenericGridPlacementKind.Span
                ? end.LineValue - start.SpanValue
                : end.LineValue;
        }
        else
        {
            min = new OriginZeroLine(0);
        }

        OriginZeroLine max;
        if (startIsLine && endIsLine)
        {
            max = start.LineValue == end.LineValue
                ? start.LineValue + (ushort)1
                : OriginZeroLine.Max(start.LineValue, end.LineValue);
        }
        else if (startIsLine)
        {
            max = end.Kind == GenericGridPlacementKind.Span
                ? start.LineValue + end.SpanValue
                : start.LineValue + (ushort)1;
        }
        else if (endIsLine)
        {
            // (Auto, Line) and (Span, Line)
            max = end.LineValue;
        }
        else
        {
            max = new OriginZeroLine(0);
        }

        // Calculate span only for indefinitely placed items; the space required by other items is
        // taken into account by min and max.
        ushort span = !startIsLine && !endIsLine ? ozLine.IndefiniteSpan() : (ushort)1;

        return (min, max, span);
    }
}
