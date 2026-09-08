// Port of `build_table` in crates/obscura-render/src/dom.rs.
using System.Globalization;
using Obscura.Dom;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDisplay = Obscura.Render.Layout.Display;
using TaffyGridPlacement = Obscura.Render.Layout.GridPlacement;
using TaffyGridTemplateComponent = Obscura.Render.Layout.GridTemplateComponent;
using TaffyLengthPercentage = Obscura.Render.Layout.LengthPercentage;
using TaffyLine = Obscura.Render.Layout.Line<Obscura.Render.Layout.GridPlacement>;
using TaffyMaxTrack = Obscura.Render.Layout.MaxTrackSizingFunction;
using TaffyMinTrack = Obscura.Render.Layout.MinTrackSizingFunction;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyStyle = Obscura.Render.Layout.Style;
using TaffyTrackSizingFunction = Obscura.Render.Layout.TrackSizingFunction;

namespace Obscura.Render;

internal static partial class DomBuild
{
    private const int MaxSpan = 1000;
    private const int MaxCols = 1024;
    private const int MaxRows = 10000;

    /// <summary>
    /// Build a table box as a CSS grid so columns negotiate a shared width across every row.
    /// Returns <c>null</c> (falling back to the generic path) when the table has no cells.
    /// </summary>
    internal static TaffyNodeId? BuildTable(BuildContext context, NodeId id)
    {
        DomTree tree = context.Tree;
        IReadOnlyDictionary<NodeId, LayoutStyle> styles = context.Styles;
        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        bool nativeHtmlTable = DomTraversal.IsLocal(tree, id, "table");
        List<NodeId>? authoredRowChildren = null;
        if (!nativeHtmlTable)
        {
            List<NodeId> flattened = [];
            FlattenContentsChildren(
                tree, DomTraversal.RenderedChildren(tree, id), styles, flattened);

            // Full anonymous-table fixup is not represented yet. Falling back to ordinary box
            // construction preserves every child.
            foreach (NodeId child in flattened)
            {
                bool hidden = styles.TryGetValue(child, out LayoutStyle? childStyle)
                    && childStyle.Display == Display.None;
                bool ignorableWhitespace = tree.GetNode(child) is { IsElement: false }
                    && tree.TextContent(child).Trim().Length == 0;
                bool isCell = styles.TryGetValue(child, out LayoutStyle? cellStyle)
                    && cellStyle.IsTableCellBox;
                if (!hidden && !ignorableWhitespace && !isCell)
                {
                    return null;
                }
            }

            authoredRowChildren = flattened;
        }

        List<(NodeId Row, int GroupEnd)> rows = [];
        if (nativeHtmlTable)
        {
            DomTableSupport.CollectTableRows(tree, id, rows);
            if (rows.Count == 0)
            {
                return null;
            }
        }
        else
        {
            // CSS table fixup inserts an anonymous row around table-cell children.
            rows.Add((id, 1));
        }

        if (rows.Count > MaxRows)
        {
            rows.RemoveRange(MaxRows, rows.Count - MaxRows);
        }

        int nrows = rows.Count;

        int SpanAttr(NodeId cid, string name)
        {
            if (tree.GetNode(cid)?.GetAttribute(name) is { } value
                && int.TryParse(
                    value.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsed)
                && parsed >= 0)
            {
                return parsed;
            }

            return 1;
        }

        HashSet<(int Row, int Column)> occupied = [];
        List<(NodeId Cell, int Row, int Column, int RowSpan, int ColSpan)> placed = [];
        int ncols = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            (NodeId tr, int groupEnd) = rows[r];
            int c = 0;
            List<NodeId> rowChildren = nativeHtmlTable
                ? tree.Children(tr)
                : authoredRowChildren ?? [];
            foreach (NodeId cid in rowChildren)
            {
                bool isCell;
                if (nativeHtmlTable)
                {
                    isCell = DomTraversal.IsAnyLocal(tree, cid, "td", "th");
                }
                else
                {
                    isCell = styles.TryGetValue(cid, out LayoutStyle? cellStyle)
                        && cellStyle.IsTableCellBox;
                }

                if (!isCell)
                {
                    continue;
                }

                // A hidden cell is removed from the table model entirely.
                if (styles.TryGetValue(cid, out LayoutStyle? hiddenStyle)
                    && hiddenStyle.Display == Display.None)
                {
                    continue;
                }

                while (occupied.Contains((r, c)))
                {
                    c++;
                }

                if (c >= MaxCols)
                {
                    break;
                }

                int cs = nativeHtmlTable ? Math.Clamp(SpanAttr(cid, "colspan"), 1, MaxSpan) : 1;

                // rowspan=0 means "span to the end of this row group".
                int rsRaw = nativeHtmlTable ? SpanAttr(cid, "rowspan") : 1;
                int rowsLeftInGroup = Math.Max(Math.Min(groupEnd, nrows) - r, 1);
                int rs = Math.Clamp(rsRaw == 0 ? rowsLeftInGroup : rsRaw, 1, rowsLeftInGroup);
                for (int dr = 0; dr < rs; dr++)
                {
                    for (int dc = 0; dc < cs; dc++)
                    {
                        occupied.Add((r + dr, c + dc));
                    }
                }

                placed.Add((cid, r, c, rs, cs));
                c += cs;
                ncols = Math.Min(Math.Max(ncols, c), MaxCols);
            }
        }

        if (placed.Count == 0 || ncols == 0)
        {
            return null;
        }

        // Build each cell and pin it to its grid area.
        List<TaffyNodeId> children = [];
        foreach ((NodeId cid, int r, int c, int rs, int cs) in placed)
        {
            if (Build(context, cid) is not { } cellNode)
            {
                continue;
            }

            TaffyStyle cellStyle = context.TaffyTree.GetStyle(cellNode).Clone();
            cellStyle.GridRow = new TaffyLine(
                TaffyGridPlacement.FromLineIndex((short)(r + 1)),
                TaffyGridPlacement.FromSpan((ushort)rs));
            cellStyle.GridColumn = new TaffyLine(
                TaffyGridPlacement.FromLineIndex((short)(c + 1)),
                TaffyGridPlacement.FromSpan((ushort)cs));

            // Grid does the sizing; a leftover flex_grow from the flex-table heuristic is
            // ignored anyway, but clear it to be explicit.
            cellStyle.FlexGrow = 0f;

            // A cell's specified width sizes its COLUMN; the cell box itself always fills its
            // grid area.
            Layout.Size<TaffyDimension> size = cellStyle.Size;
            size.Width = TaffyDimension.Auto;
            cellStyle.Size = size;

            // Taffy's grid-item automatic minimum is an engine artifact here.
            if (styles.TryGetValue(cid, out LayoutStyle? cellLayoutStyle)
                && cellLayoutStyle.MinWidth.IsAuto)
            {
                Layout.Size<TaffyDimension> minSize = cellStyle.MinSize;
                minSize.Width = TaffyDimension.FromLength(0f);
                cellStyle.MinSize = minSize;
            }

            context.TaffyTree.SetStyle(cellNode, cellStyle);
            children.Add(cellNode);
        }

        if (children.Count == 0)
        {
            return null;
        }

        // Column sizing pre-pass: specified widths on <col> elements and on colspan-1 cells
        // feed the tracks.
        List<float?> colPx = [];
        List<float?> colPct = [];
        for (int index = 0; index < ncols; index++)
        {
            colPx.Add(null);
            colPct.Add(null);
        }

        bool fixedLayout = style.TableLayoutFixed
            && style.Width.Kind is DimensionKind.Px or DimensionKind.Percent;
        List<FixedTableColumn> fixedColumns = [];
        for (int index = 0; index < ncols; index++)
        {
            fixedColumns.Add(default);
        }

        (float? Px, float? Percent) AttrWidth(NodeId cid)
        {
            if (tree.GetNode(cid)?.GetAttribute("width") is not { } raw)
            {
                return (null, null);
            }

            string v = raw.Trim();
            if (v.EndsWith('%')
                && float.TryParse(
                    v[..^1].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float percent))
            {
                return (null, percent / 100f);
            }

            string numeric = v.EndsWith("px", StringComparison.Ordinal) ? v[..^2].Trim() : v;
            return float.TryParse(
                numeric,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float px)
                ? (px, null)
                : (null, null);
        }

        (float? Px, float? Percent) StyleWidth(NodeId cid)
        {
            if (styles.TryGetValue(cid, out LayoutStyle? cellStyle))
            {
                if (cellStyle.Width.Kind == DimensionKind.Px && cellStyle.Width.Value > 0f)
                {
                    return (cellStyle.Width.Value, null);
                }

                if (cellStyle.Width.Kind == DimensionKind.Percent && cellStyle.Width.Value > 0f)
                {
                    return (null, cellStyle.Width.Value);
                }
            }

            return AttrWidth(cid);
        }

        (float? Px, float? Percent) FixedStyleWidth(NodeId cid)
        {
            if (styles.TryGetValue(cid, out LayoutStyle? cellStyle))
            {
                if (cellStyle.Width.Kind == DimensionKind.Px && cellStyle.Width.Value >= 0f)
                {
                    return (cellStyle.Width.Value, null);
                }

                if (cellStyle.Width.Kind == DimensionKind.Percent && cellStyle.Width.Value >= 0f)
                {
                    return (null, cellStyle.Width.Value);
                }
            }

            return AttrWidth(cid);
        }

        // <col> elements (direct or under <colgroup>), each spanning `span` columns.
        int nextCol = 0;
        List<NodeId> colElems = [];
        foreach (NodeId cid in tree.Children(id))
        {
            switch (DomTraversal.ElementLocalName(tree, cid))
            {
                case "col":
                    colElems.Add(cid);
                    break;
                case "colgroup":
                    foreach (NodeId gc in tree.Children(cid))
                    {
                        if (DomTraversal.IsLocal(tree, gc, "col"))
                        {
                            colElems.Add(gc);
                        }
                    }

                    break;
            }
        }

        foreach (NodeId colEl in colElems)
        {
            int span = Math.Clamp(SpanAttr(colEl, "span"), 1, MaxSpan);
            (float? px, float? pct) = StyleWidth(colEl);
            (float? fixedPx, float? fixedPct) = FixedStyleWidth(colEl);
            for (int index = 0; index < span; index++)
            {
                if (nextCol >= ncols)
                {
                    break;
                }

                colPx[nextCol] = px;
                colPct[nextCol] = pct;
                if (fixedLayout && (fixedPx is not null || fixedPct is not null))
                {
                    fixedColumns[nextCol] = new FixedTableColumn(
                        fixedPx ?? 0f, fixedPct ?? 0f, true);
                }

                nextCol++;
            }
        }

        // colspan-1 cells override <col> (they are closer to the content).
        foreach ((NodeId cid, int _, int c, int _, int cs) in placed)
        {
            if (cs != 1 || c >= ncols)
            {
                continue;
            }

            (float? px, float? pct) = StyleWidth(cid);

            // A fixed width declared on a cell describes its content box unless the author
            // opted into border-box; grid tracks describe the outer border box.
            if (px is { } width && styles.TryGetValue(cid, out LayoutStyle? cellStyle)
                && cellStyle.BoxSizing == BoxSizing.ContentBox)
            {
                px = width
                    + cellStyle.Padding.Left
                    + cellStyle.Padding.Right
                    + cellStyle.Border.Left
                    + cellStyle.Border.Right;
            }

            if (px is { } resolvedPx)
            {
                colPx[c] = colPx[c] is { } current ? F32.Max(current, resolvedPx) : resolvedPx;
            }

            if (pct is { } resolvedPct)
            {
                colPct[c] = colPct[c] is { } current ? F32.Max(current, resolvedPct) : resolvedPct;
            }
        }

        // Under fixed table layout only the first row contributes cell widths.
        if (fixedLayout)
        {
            (float horizontalSpacing, _) = DomStyleFixups.TableSpacing(style);
            foreach ((NodeId cid, int row, int start, int _, int colspan) in placed)
            {
                if (row != 0 || start >= ncols)
                {
                    continue;
                }

                (float? px, float? pct) = FixedStyleWidth(cid);
                if (px is null && pct is null)
                {
                    continue;
                }

                int span = Math.Max(Math.Min(colspan, ncols - start), 1);
                float edges = 0f;
                if (styles.TryGetValue(cid, out LayoutStyle? cellStyle)
                    && cellStyle.BoxSizing == BoxSizing.ContentBox)
                {
                    edges = cellStyle.Padding.Left
                        + cellStyle.Padding.Right
                        + cellStyle.Border.Left
                        + cellStyle.Border.Right;
                }

                float outerLength = (px ?? 0f) + edges;
                float perColumnLength = F32.Max(
                    ((outerLength + horizontalSpacing) / span) - horizontalSpacing,
                    0f);
                float perColumnPercentage = F32.Max(pct ?? 0f, 0f) / span;
                for (int index = start; index < start + span; index++)
                {
                    if (!fixedColumns[index].Specified)
                    {
                        fixedColumns[index] = new FixedTableColumn(
                            perColumnLength, perColumnPercentage, true);
                    }
                }
            }
        }

        // Row sizing: a `height` on the row or a rowspan-1 cell is a MINIMUM.
        List<float?> rowMin = [];
        for (int index = 0; index < nrows; index++)
        {
            rowMin.Add(null);
        }

        if (nativeHtmlTable)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                if (styles.TryGetValue(rows[r].Row, out LayoutStyle? rowStyle)
                    && rowStyle.Height.Kind == DimensionKind.Px
                    && rowStyle.Height.Value > 0f)
                {
                    rowMin[r] = rowStyle.Height.Value;
                }
            }
        }

        foreach ((NodeId cid, int r, int _, int rs, int _) in placed)
        {
            if (rs != 1)
            {
                continue;
            }

            if (styles.TryGetValue(cid, out LayoutStyle? cellStyle)
                && cellStyle.Height.Kind == DimensionKind.Px
                && cellStyle.Height.Value > 0f)
            {
                float h = cellStyle.Height.Value;
                rowMin[r] = rowMin[r] is { } current ? F32.Max(current, h) : h;
            }
        }

        TaffyGridTemplateComponent Col(int i)
        {
            if (fixedLayout)
            {
                return TaffyGridTemplateComponent.FromSingle(TaffyTrackSizingFunction.MinMax(
                    TaffyMinTrack.FromLength(0f),
                    TaffyMaxTrack.FromLength(0f)));
            }

            TaffyMaxTrack max = colPct[i] is { } percent
                ? TaffyMaxTrack.FromPercent(percent)
                : colPx[i] is { } px
                    ? TaffyMaxTrack.FromLength(px)
                    : TaffyMaxTrack.Auto;
            return TaffyGridTemplateComponent.FromSingle(
                TaffyTrackSizingFunction.MinMax(TaffyMinTrack.MinContent, max));
        }

        TaffyGridTemplateComponent RowTrack(int r)
        {
            TaffyMinTrack min = rowMin[r] is { } height
                ? TaffyMinTrack.FromLength(height)
                : TaffyMinTrack.Auto;
            return TaffyGridTemplateComponent.FromSingle(
                TaffyTrackSizingFunction.MinMax(min, TaffyMaxTrack.Auto));
        }

        // In the separate-border model, border-spacing also exists between the table edge and
        // the first/last row and column. Grid `gap` only covers interior tracks.
        (float horizontalGap, float verticalGap) = DomStyleFixups.TableSpacing(style);
        LayoutStyle gridStyle = style.Clone();
        gridStyle.Padding = new Edges(
            gridStyle.Padding.Top + verticalGap,
            gridStyle.Padding.Right + horizontalGap,
            gridStyle.Padding.Bottom + verticalGap,
            gridStyle.Padding.Left + horizontalGap);
        TaffyStyle tstyle = TaffyStyleMapping.ToTaffyStyle(gridStyle);
        tstyle.Display = TaffyDisplay.Grid;

        // A percentage width resolves against the container, so keep it and let the used-width
        // pass leave it to taffy. Any other width is forced to auto.
        if (style.Width.Kind != DimensionKind.Percent)
        {
            Layout.Size<TaffyDimension> size = tstyle.Size;
            size.Width = TaffyDimension.Auto;
            tstyle.Size = size;
        }

        List<TaffyGridTemplateComponent> columnTracks = new(ncols);
        for (int index = 0; index < ncols; index++)
        {
            columnTracks.Add(Col(index));
        }

        List<TaffyGridTemplateComponent> rowTracks = new(nrows);
        for (int index = 0; index < nrows; index++)
        {
            rowTracks.Add(RowTrack(index));
        }

        tstyle.GridTemplateColumns = columnTracks;
        tstyle.GridTemplateRows = rowTracks;
        tstyle.Gap = new Layout.Size<TaffyLengthPercentage>(
            TaffyLengthPercentage.FromLength(horizontalGap),
            TaffyLengthPercentage.FromLength(verticalGap));

        TaffyNodeId tableNode = context.TaffyTree.NewWithChildren(tstyle, [.. children]);
        context.IdMap[tableNode] = id;
        context.Ifc.TableRows[tableNode] = rowMin;
        if (fixedLayout)
        {
            context.Ifc.FixedTableCols[tableNode] = fixedColumns;
        }

        bool anySpecified = false;
        foreach (float? value in colPx)
        {
            if (value is not null)
            {
                anySpecified = true;
                break;
            }
        }

        if (!anySpecified)
        {
            foreach (float? value in colPct)
            {
                if (value is not null)
                {
                    anySpecified = true;
                    break;
                }
            }
        }

        if (anySpecified)
        {
            context.Ifc.TableCols[tableNode] = (colPx, colPct);
        }

        return tableNode;
    }
}
