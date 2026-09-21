// CSS 2.1 17.6.2 collapsing-border resolution, which crates/obscura-render/src/dom.rs does not
// implement at all - there a collapsing table keeps every box's own border, so the table reserved
// its full border on both edges and each cell reserved its own. Chromium resolves one border per
// edge segment and splits it between the two boxes that meet there, which is what decides a
// collapsing table's content width and every cell rect inside it.
namespace Obscura.Render;

/// <summary>
/// Resolves the borders of a collapsing table (CSS 2.1 17.6.2) into the half that each box
/// carries.
/// </summary>
/// <remarks>
/// Only the <em>width</em> half of the conflict resolution is modelled: the widest border at an
/// edge wins, and a side whose <c>border-style</c> is <c>none</c> or <c>hidden</c> contributes
/// zero because <see cref="LayoutStyle.Border"/> is already the used width. The rest of the
/// cascade in 17.6.2 - <c>hidden</c> suppressing the edge outright, and the
/// cell &gt; row &gt; row group &gt; column &gt; column group &gt; table order between borders of
/// equal width - decides which border is <em>painted</em>, not how wide the edge is, so it does
/// not move a box.
/// </remarks>
internal static class DomTableCollapsedBorders
{
    /// <summary>A cell's grid area and its own specified border widths.</summary>
    internal readonly record struct Cell(int Row, int Column, int RowSpan, int ColSpan, Edges Border);

    /// <summary>
    /// The borders of one band of the table - a row, a row group, a column or a column group -
    /// together with whether the band it belongs to starts or ends here.
    /// </summary>
    internal readonly record struct Band(Edges Border, Edges GroupBorder, bool GroupFirst, bool GroupLast)
    {
        internal static Band None => new(default, default, true, true);
    }

    /// <summary>
    /// Resolve every edge of <paramref name="cells"/> and return the half-borders that the table
    /// box and each cell box carry. The returned cell array is parallel to
    /// <paramref name="cells"/>.
    /// </summary>
    internal static (Edges Table, Edges[] Cells) Resolve(
        Edges table,
        int rows,
        int columns,
        IReadOnlyList<Cell> cells,
        IReadOnlyList<Band> rowBands,
        IReadOnlyList<Band> columnBands)
    {
        Edges[] resolved = new Edges[cells.Count];
        if (rows <= 0 || columns <= 0 || cells.Count == 0)
        {
            return (Half(table), resolved);
        }

        // Which cell occupies each slot, so an edge can see the box on its other side.
        int[] occupant = new int[rows * columns];
        Array.Fill(occupant, -1);
        for (int index = 0; index < cells.Count; index++)
        {
            Cell cell = cells[index];
            int rowEnd = Math.Min(cell.Row + cell.RowSpan, rows);
            int columnEnd = Math.Min(cell.Column + cell.ColSpan, columns);
            for (int r = Math.Max(cell.Row, 0); r < rowEnd; r++)
            {
                for (int c = Math.Max(cell.Column, 0); c < columnEnd; c++)
                {
                    occupant[(r * columns) + c] = index;
                }
            }
        }

        Band RowBand(int r) => r >= 0 && r < rowBands.Count ? rowBands[r] : Band.None;
        Band ColumnBand(int c) => c >= 0 && c < columnBands.Count ? columnBands[c] : Band.None;

        Edges tableUsed = Half(table);
        for (int index = 0; index < cells.Count; index++)
        {
            Cell cell = cells[index];
            int rowEnd = Math.Min(cell.Row + cell.RowSpan, rows);
            int columnEnd = Math.Min(cell.Column + cell.ColSpan, columns);
            bool atLeft = cell.Column <= 0;
            bool atRight = columnEnd >= columns;
            bool atTop = cell.Row <= 0;
            bool atBottom = rowEnd >= rows;

            float left = cell.Border.Left;
            float right = cell.Border.Right;
            float top = cell.Border.Top;
            float bottom = cell.Border.Bottom;

            // The cell on the other side of each inline edge.
            for (int r = Math.Max(cell.Row, 0); r < rowEnd; r++)
            {
                if (!atLeft && occupant[(r * columns) + cell.Column - 1] is var westward and >= 0)
                {
                    left = F32.Max(left, cells[westward].Border.Right);
                }

                if (!atRight && occupant[(r * columns) + columnEnd] is var eastward and >= 0)
                {
                    right = F32.Max(right, cells[eastward].Border.Left);
                }
            }

            // The cell on the other side of each block edge.
            for (int c = Math.Max(cell.Column, 0); c < columnEnd; c++)
            {
                if (!atTop && occupant[((cell.Row - 1) * columns) + c] is var northward and >= 0)
                {
                    top = F32.Max(top, cells[northward].Border.Bottom);
                }

                if (!atBottom && occupant[(rowEnd * columns) + c] is var southward and >= 0)
                {
                    bottom = F32.Max(bottom, cells[southward].Border.Top);
                }
            }

            // Columns and column groups bound the inline edges of every row they cross, and the
            // block edges only of the table's first and last row.
            Band ownColumn = ColumnBand(cell.Column);
            Band lastColumn = ColumnBand(columnEnd - 1);
            left = F32.Max(left, ownColumn.Border.Left);
            right = F32.Max(right, lastColumn.Border.Right);
            if (ownColumn.GroupFirst)
            {
                left = F32.Max(left, ownColumn.GroupBorder.Left);
            }

            if (lastColumn.GroupLast)
            {
                right = F32.Max(right, lastColumn.GroupBorder.Right);
            }

            if (!atLeft)
            {
                Band westColumn = ColumnBand(cell.Column - 1);
                left = F32.Max(left, westColumn.Border.Right);
                if (westColumn.GroupLast)
                {
                    left = F32.Max(left, westColumn.GroupBorder.Right);
                }
            }

            if (!atRight)
            {
                Band eastColumn = ColumnBand(columnEnd);
                right = F32.Max(right, eastColumn.Border.Left);
                if (eastColumn.GroupFirst)
                {
                    right = F32.Max(right, eastColumn.GroupBorder.Left);
                }
            }

            // Rows and row groups bound the block edges of every column they cross, and the
            // inline edges only of the table's first and last column.
            Band ownRow = RowBand(cell.Row);
            Band lastRow = RowBand(rowEnd - 1);
            top = F32.Max(top, ownRow.Border.Top);
            bottom = F32.Max(bottom, lastRow.Border.Bottom);
            if (ownRow.GroupFirst)
            {
                top = F32.Max(top, ownRow.GroupBorder.Top);
            }

            if (lastRow.GroupLast)
            {
                bottom = F32.Max(bottom, lastRow.GroupBorder.Bottom);
            }

            if (!atTop)
            {
                Band northRow = RowBand(cell.Row - 1);
                top = F32.Max(top, northRow.Border.Bottom);
                if (northRow.GroupLast)
                {
                    top = F32.Max(top, northRow.GroupBorder.Bottom);
                }
            }

            if (!atBottom)
            {
                Band southRow = RowBand(rowEnd);
                bottom = F32.Max(bottom, southRow.Border.Top);
                if (southRow.GroupFirst)
                {
                    bottom = F32.Max(bottom, southRow.GroupBorder.Top);
                }
            }

            // The table's own border, and the ends of the bands that reach the table's edge.
            if (atLeft)
            {
                left = F32.Max(left, table.Left);
                for (int r = Math.Max(cell.Row, 0); r < rowEnd; r++)
                {
                    Band band = RowBand(r);
                    left = F32.Max(F32.Max(left, band.Border.Left), band.GroupBorder.Left);
                }
            }

            if (atRight)
            {
                right = F32.Max(right, table.Right);
                for (int r = Math.Max(cell.Row, 0); r < rowEnd; r++)
                {
                    Band band = RowBand(r);
                    right = F32.Max(F32.Max(right, band.Border.Right), band.GroupBorder.Right);
                }
            }

            if (atTop)
            {
                top = F32.Max(top, table.Top);
                for (int c = Math.Max(cell.Column, 0); c < columnEnd; c++)
                {
                    Band band = ColumnBand(c);
                    top = F32.Max(F32.Max(top, band.Border.Top), band.GroupBorder.Top);
                }
            }

            if (atBottom)
            {
                bottom = F32.Max(bottom, table.Bottom);
                for (int c = Math.Max(cell.Column, 0); c < columnEnd; c++)
                {
                    Band band = ColumnBand(c);
                    bottom = F32.Max(F32.Max(bottom, band.Border.Bottom), band.GroupBorder.Bottom);
                }
            }

            resolved[index] = Half(new Edges(top, right, bottom, left));

            // The table's border box reaches the widest border on each of its edges; a cell that
            // resolved a narrower one there simply starts at the content edge with less of its
            // own. Chromium 141 with a 9px border-left on one cell of a borderless two-row table
            // puts both rows at x=4.5 and gives only that cell a border.
            if (atLeft)
            {
                tableUsed = tableUsed with { Left = F32.Max(tableUsed.Left, left * 0.5f) };
            }

            if (atRight)
            {
                tableUsed = tableUsed with { Right = F32.Max(tableUsed.Right, right * 0.5f) };
            }

            if (atTop)
            {
                tableUsed = tableUsed with { Top = F32.Max(tableUsed.Top, top * 0.5f) };
            }

            if (atBottom)
            {
                tableUsed = tableUsed with { Bottom = F32.Max(tableUsed.Bottom, bottom * 0.5f) };
            }
        }

        return (tableUsed, resolved);
    }

    private static Edges Half(Edges edges) =>
        new(edges.Top * 0.5f, edges.Right * 0.5f, edges.Bottom * 0.5f, edges.Left * 0.5f);
}
