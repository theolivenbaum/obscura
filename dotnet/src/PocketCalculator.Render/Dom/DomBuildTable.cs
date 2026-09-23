// Port of `build_table` in crates/obscura-render/src/dom.rs.
using System.Globalization;
using PocketCalculator.Dom;
using TaffyAlignItems = PocketCalculator.Render.Layout.AlignItems;
using TaffyDimension = PocketCalculator.Render.Layout.Dimension;
using TaffyDisplay = PocketCalculator.Render.Layout.Display;
using TaffyFlexDirection = PocketCalculator.Render.Layout.FlexDirection;
using TaffyGridPlacement = PocketCalculator.Render.Layout.GridPlacement;
using TaffyGridTemplateComponent = PocketCalculator.Render.Layout.GridTemplateComponent;
using TaffyLengthPercentage = PocketCalculator.Render.Layout.LengthPercentage;
using TaffyLengthPercentageAuto = PocketCalculator.Render.Layout.LengthPercentageAuto;
using TaffyLine = PocketCalculator.Render.Layout.Line<PocketCalculator.Render.Layout.GridPlacement>;
using TaffyMaxTrack = PocketCalculator.Render.Layout.MaxTrackSizingFunction;
using TaffyMinTrack = PocketCalculator.Render.Layout.MinTrackSizingFunction;
using TaffyNodeId = PocketCalculator.Render.Layout.NodeId;
using TaffyPosition = PocketCalculator.Render.Layout.Position;
using TaffyStyle = PocketCalculator.Render.Layout.Style;
using TaffyTrackSizingFunction = PocketCalculator.Render.Layout.TrackSizingFunction;

namespace PocketCalculator.Render;

internal static partial class DomBuild
{
    private const int MaxSpan = 1000;
    private const int MaxCols = 1024;
    private const int MaxRows = 10000;

    /// <summary>A cell placed in the table grid, with the area it occupies.</summary>
    private readonly record struct PlacedCell(
        CssTableCell Cell,
        int Row,
        int Column,
        int RowSpan,
        int ColSpan);

    /// <summary>
    /// Build the anonymous table cell CSS 2.1 17.2.1 generates around <paramref name="content"/>
    /// - a run of consecutive children of <paramref name="owner"/> that are not proper table
    /// children. The box has no DOM node, so it gets no <see cref="BuildContext.IdMap"/> entry
    /// and nothing reports it.
    /// </summary>
    /// <remarks>
    /// New behaviour, not a port: crates/obscura-render/src/dom.rs generates no anonymous cell
    /// at all, so a table box holding anything that is not a row, group, column or cell was not
    /// built as a table and fell back to ordinary block layout - `&lt;div style="display:table"&gt;aa&lt;/div&gt;`
    /// came out 600 wide where Chromium 141 shrink-to-fits it to 19.2 in
    /// `16px/18px "Liberation Mono"`, and a `table-cell` beside a plain div stacked instead of
    /// sharing one 38.41 row.
    /// <para>
    /// The cell's own box is a block container: its inline runs fold through the text engine
    /// the way <see cref="BuildMixedBlock"/> folds them, under <paramref name="owner"/>'s
    /// inline-formatting-context key, since the anonymous box has no key of its own. An
    /// anonymous box takes only inherited properties, so it carries no margin, padding or
    /// border and no sizing.
    /// </para>
    /// </remarks>
    private static TaffyNodeId? BuildAnonymousCell(
        BuildContext context,
        NodeId owner,
        LayoutStyle ownerStyle,
        IReadOnlyList<NodeId> content)
    {
        DomTree tree = context.Tree;

        List<TaffyNodeId> childIds = [];
        List<NodeId> outOfFlow = [];
        List<NodeId> run = [];
        bool hasBlockChild = false;
        bool hasInlineContent = false;

        void FlushRun()
        {
            // Collapsible source formatting at the edges of an inline run creates no line width.
            bool IsWhitespaceText(NodeId cid) =>
                tree.GetNode(cid) is { IsText: true } && tree.TextContent(cid).Trim().Length == 0;

            while (run.Count != 0 && IsWhitespaceText(run[0]))
            {
                run.RemoveAt(0);
            }

            while (run.Count != 0 && IsWhitespaceText(run[^1]))
            {
                run.RemoveAt(run.Count - 1);
            }

            if (run.Count == 0)
            {
                return;
            }

            hasInlineContent = true;
            if (context.Engine.TryBuildRun(tree, owner, run, context.Styles) is { } item)
            {
                TaffyNodeId leaf = context.TaffyTree.NewLeafWithContext(RunLeafStyle(), item);
                if (!context.Ifc.Runs.TryGetValue(owner, out List<int>? items))
                {
                    items = [];
                    context.Ifc.Runs[owner] = items;
                }

                items.Add(item);
                childIds.Add(leaf);
                run.Clear();
                return;
            }

            bool hasTextStrut = false;
            List<TaffyNodeId> atoms = [];
            foreach (NodeId cid in run)
            {
                hasTextStrut |= DomTraversal.IsTextNode(tree, cid);
                atoms.AddRange(BuildAny(context, cid));
            }

            run.Clear();
            if (atoms.Count == 0)
            {
                return;
            }

            childIds.Add(context.TaffyTree.NewWithChildren(
                RunWrapperStyle(ownerStyle, hasTextStrut), [.. atoms]));
        }

        foreach (NodeId cid in content)
        {
            if (tree.GetNode(cid) is not { } node)
            {
                continue;
            }

            bool isText = node.IsText;
            if (!isText && !node.IsElement)
            {
                continue;
            }

            bool inlineLevel = true;
            if (!isText)
            {
                if (!context.Styles.TryGetValue(cid, out LayoutStyle? childStyle))
                {
                    inlineLevel = false;
                }
                else if (childStyle.Display == Display.None)
                {
                    continue;
                }
                else if (childStyle.Position == TaffyPosition.Absolute)
                {
                    outOfFlow.Add(cid);
                    continue;
                }
                else
                {
                    inlineLevel = DomTraversal.IsLocal(tree, cid, "br")
                        || LayoutStyleExtensions.IsInlineLevelBox(childStyle);
                }
            }

            if (inlineLevel)
            {
                run.Add(cid);
                continue;
            }

            FlushRun();
            hasBlockChild = true;
            childIds.AddRange(BuildAny(context, cid));
        }

        FlushRun();
        foreach (NodeId cid in outOfFlow)
        {
            childIds.AddRange(BuildAny(context, cid));
        }

        if (childIds.Count == 0)
        {
            return null;
        }

        TaffyStyle cellStyle = TaffyStyle.Default;
        cellStyle.FlexGrow = 0f;
        cellStyle.FlexShrink = 0f;
        cellStyle.MinSize = new Layout.Size<TaffyDimension>(
            TaffyDimension.FromLength(0f), TaffyDimension.Auto);

        // The same three shapes an authored cell takes in `Build`: block-level children only
        // become the column-flex stand-in this engine lays a cell's block children out with,
        // and a cell mixing the two is real block layout. Block layout is what collapses a
        // child's margins out through the cell, which a cell's block formatting context must
        // not do - a `display: table` div holding one `margin: 10px` block is 38 tall in
        // Chromium 141, and was 28 with the cell laid out as a block.
        if (hasBlockChild && !hasInlineContent)
        {
            cellStyle.Display = TaffyDisplay.Flex;
            cellStyle.FlexDirection = TaffyFlexDirection.Column;
            cellStyle.AlignItems = TaffyAlignItems.FlexStart;
            foreach (TaffyNodeId child in childIds)
            {
                bool fillsInlineAxis = context.IdMap.TryGetValue(child, out NodeId domId)
                    && context.Styles.TryGetValue(domId, out LayoutStyle? childStyle)
                    && childStyle.Display == Display.Block
                    && childStyle.Width.IsAuto
                    && childStyle.Float is null
                    && childStyle.Position != TaffyPosition.Absolute;
                TaffyStyle blockItem = context.TaffyTree.GetStyle(child).Clone();
                blockItem.FlexShrink = 0f;
                if (fillsInlineAxis)
                {
                    Layout.Size<TaffyDimension> size = blockItem.Size;
                    size.Width = TaffyDimension.FromPercent(1f);
                    blockItem.Size = size;
                }

                context.TaffyTree.SetStyle(child, blockItem);
            }
        }
        else
        {
            cellStyle.Display = TaffyDisplay.Block;
        }

        return context.TaffyTree.NewWithChildren(cellStyle, [.. childIds]);
    }

    /// <summary>
    /// Whether <paramref name="id"/> is an element with table-internal children but a
    /// <c>display</c> that is not a table type, so CSS table fixup has to generate an anonymous
    /// table box around them.
    /// </summary>
    /// <remarks>
    /// Deviation from crates/obscura-render/src/dom.rs, which builds a table only for a computed
    /// table box and lays the rows out in the element's own formatting context otherwise.
    /// Chromium generates the anonymous table required by CSS 2.1 17.2.1: the element keeps its
    /// declared width and the anonymous table inside is <c>width: auto</c>, so a 600px
    /// `display: inline-block; width: 100%` table of `alpha` / `beta gamma delta` is 600 wide
    /// with 34.66 / 112.84 columns rather than 35 / 565.
    /// </remarks>
    internal static bool WantsAnonymousTableBox(BuildContext context, NodeId id, LayoutStyle style) =>
        WantsAnonymousTableBox(context.Tree, id, style, context.Styles);

    /// <summary>
    /// The same question, for the box-tree flattening passes, which carry the tree and the
    /// style map rather than a <see cref="BuildContext"/>.
    /// </summary>
    internal static bool WantsAnonymousTableBox(
        DomTree tree,
        NodeId id,
        LayoutStyle style,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (style.IsTableBox || style.DisplayContents)
        {
            return false;
        }

        // A cell is deliberately NOT excluded here. It used to be, alongside the two above,
        // which answered false before the child loop ran and so gave a `table-cell` parent no
        // anonymous table for table-internal children of its own: two `display: table-cell`
        // divs inside one stacked 20 wide and 36 tall where Chromium 141 puts them in a single
        // 38.41 x 18 anonymous row, and the same two inside a `<td>` did too. A cell is an
        // ordinary non-table box as far as CSS 2.1 17.2.1 is concerned, and the cells this
        // engine generates for its own internal flex containers are the cells of a table this
        // function is never asked about - `BuildTable` builds them from the table's structure
        // and reaches their elements through `Build`, which asks about the element's own
        // children, not about the generated box.

        // A `<table>` whose authored display cleared the UA table box. Its rows are still
        // table-internal, and BuildTable answers null when it holds no cells at all.
        if (DomTraversal.IsLocal(tree, id, "table"))
        {
            return true;
        }

        // A flex or grid container blockifies its items, so an authored internal-table display
        // on one of them is not an internal-table box and generates no anonymous table:
        // Chromium 141 reports `block` for a `display: table-row` flex item. The cell arm below
        // is deliberately outside that rule, because this engine builds every table out of
        // internal flex containers and `IsTableCellBox` is how it marks their cells.
        bool blockifiesItems = style.Display is Display.Flex or Display.Grid
            && !style.InternalFlexContainer;

        // Every table-internal child except a column generates the anonymous table: a column
        // has nothing to size on its own, and a table holding only columns has no cells, so
        // BuildTable would answer null and the fallback would render the columns' contents.
        foreach (NodeId child in DomTraversal.RenderedChildren(tree, id))
        {
            if (!styles.TryGetValue(child, out LayoutStyle? childStyle)
                || childStyle.Display == Display.None)
            {
                continue;
            }

            switch (DomTableSupport.BlockifiableRoleOf(childStyle))
            {
                case TableInternalRole.Cell:
                    return true;

                case TableInternalRole.Row:
                case TableInternalRole.RowGroup:
                case TableInternalRole.HeaderGroup:
                case TableInternalRole.FooterGroup:
                case TableInternalRole.Caption:
                    if (!blockifiesItems)
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// The style of the anonymous table box generated inside <paramref name="style"/>'s element.
    /// </summary>
    /// <remarks>
    /// CSS 2.1 17.2.1: an anonymous box takes only the inherited properties of the element that
    /// generated it. `border-collapse` and `border-spacing` inherit and so travel - Chromium
    /// puts the first cell of a `border-spacing: 8px` table at x=8 inside a `display: block`
    /// table - while the element's own box decorations, sizing, and flex/grid item properties
    /// stay on the element.
    /// </remarks>
    internal static LayoutStyle AnonymousTableStyle(LayoutStyle style)
    {
        LayoutStyle anonymous = style.Clone();
        anonymous.Display = Display.Block;
        anonymous.FlowRoot = true;
        anonymous.IsTableBox = true;
        anonymous.IsTableCellBox = false;
        anonymous.IsInlineBlock = false;
        anonymous.InternalFlexContainer = false;
        anonymous.DisplayContents = false;
        anonymous.DisplayInherit = false;
        anonymous.WebkitBoxDisplay = null;
        anonymous.BoxSizing = BoxSizing.BorderBox;

        // `table-layout` does not inherit, so it stays on the element that declared it.
        anonymous.TableLayoutFixed = false;

        anonymous.Width = Dimension.Auto;
        anonymous.Height = Dimension.Auto;
        anonymous.MinWidth = Dimension.Auto;
        anonymous.MinHeight = Dimension.Auto;
        anonymous.MaxWidth = Dimension.Auto;
        anonymous.MaxHeight = Dimension.Auto;
        anonymous.ClearSizeExpressions();
        anonymous.SizeCalc = null;
        anonymous.AspectRatio = null;

        anonymous.Margin = default;
        anonymous.ClearMarginAuto();
        anonymous.Padding = default;
        anonymous.ClearPaddingPercent();
        anonymous.Border = default;

        anonymous.Position = null;
        anonymous.ClearInset();
        anonymous.ClearInsetExpressions();
        anonymous.InsetCalc = null;
        anonymous.Float = null;

        anonymous.FlexGrow = null;
        anonymous.FlexShrink = null;
        anonymous.FlexBasis = Dimension.Auto;
        anonymous.FlexBasisCalc = null;
        anonymous.FlexBasisSpecified = null;
        anonymous.AlignSelf = null;
        anonymous.JustifySelf = null;
        anonymous.Order = 0;
        anonymous.GridColumn = null;
        anonymous.GridRow = null;
        anonymous.GridColumnRaw = null;
        anonymous.GridRowRaw = null;

        return anonymous;
    }

    /// <summary>
    /// Build a table box as a CSS grid so columns negotiate a shared width across every row.
    /// Returns <c>null</c> (falling back to the generic path) when the table has no cells.
    /// </summary>
    /// <param name="anonymousStyle">
    /// When set, the grid is an anonymous table box generated inside <paramref name="id"/>
    /// rather than <paramref name="id"/>'s own box, and this is the anonymous box's style. The
    /// node is then registered in <see cref="IfcRegistry.AnonymousTables"/> instead of
    /// <see cref="BuildContext.IdMap"/>.
    /// </param>

    /// <summary>
    /// Resolve the collapsing border model over a built table and record each box's half of it
    /// in <see cref="LayoutStyle.CollapsedBorder"/>, which <see cref="LayoutStyle.UsedBorder"/>
    /// then hands to layout and paint.
    /// </summary>
    /// <remarks>
    /// Deviation from crates/obscura-render/src/dom.rs, which has no collapsing border model:
    /// there a collapsing table reserves its whole border inside its border box and every cell
    /// reserves its own, so a `border: 5px; border-collapse: collapse` table 600px wide laid its
    /// cells out across 590px where Chromium 141 gives them 595 and starts the first at x=2.5.
    /// The rows, row groups and columns of a collapsing table keep no border of their own: what
    /// they contributed is already inside the cells, so painting it again would double it.
    /// </remarks>
    private static void ResolveCollapsedBorders(
        BuildContext context,
        NodeId id,
        LayoutStyle style,
        bool nativeHtmlTable,
        List<(NodeId Row, int GroupEnd)> rows,
        List<PlacedCell> placed,
        int ncols,
        List<NodeId> colElems,
        List<NodeId?> colGroupOf)
    {
        DomTree tree = context.Tree;
        IReadOnlyDictionary<NodeId, LayoutStyle> styles = context.Styles;

        LayoutStyle? StyleOf(NodeId node) =>
            styles.TryGetValue(node, out LayoutStyle? found) ? found : null;

        if (style.BorderCollapse != true)
        {
            style.CollapsedBorder = null;
            foreach (PlacedCell placedCell in placed)
            {
                if (placedCell.Cell.Element is { } cell && StyleOf(cell) is { } cellStyle)
                {
                    cellStyle.CollapsedBorder = null;
                }
            }

            return;
        }

        // Bands. A non-native table has one synthesized row whose "row element" is the element
        // that generated the anonymous table box, and that element's border belongs to its own
        // box, not to the row.
        List<DomTableCollapsedBorders.Band> rowBands = new(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            if (!nativeHtmlTable)
            {
                rowBands.Add(DomTableCollapsedBorders.Band.None);
                continue;
            }

            NodeId row = rows[index].Row;
            NodeId? group = DomTraversal.RenderedParent(tree, row) is { } parent
                && DomTraversal.IsAnyLocal(tree, parent, "thead", "tbody", "tfoot")
                    ? parent
                    : null;
            int groupEnd = Math.Min(rows[index].GroupEnd, rows.Count);
            bool groupFirst = index == 0 || rows[index - 1].GroupEnd != rows[index].GroupEnd;
            rowBands.Add(new DomTableCollapsedBorders.Band(
                StyleOf(row)?.Border ?? default,
                group is { } groupId ? StyleOf(groupId)?.Border ?? default : default,
                groupFirst,
                index + 1 >= groupEnd));
        }

        List<DomTableCollapsedBorders.Band> columnBands = new(ncols);
        for (int index = 0; index < ncols; index++)
        {
            columnBands.Add(DomTableCollapsedBorders.Band.None);
        }

        int column = 0;
        for (int index = 0; index < colElems.Count && column < ncols; index++)
        {
            NodeId colEl = colElems[index];

            // `span` is an HTML attribute; a CSS column is always one track. Same rule as the
            // column pre-pass below.
            int span = nativeHtmlTable
                ? Math.Clamp(SpanAttribute(tree, colEl, "span"), 1, MaxSpan)
                : 1;
            Edges own = StyleOf(colEl)?.Border ?? default;
            NodeId? group = colGroupOf[index];
            Edges groupBorder = group is { } groupId ? StyleOf(groupId)?.Border ?? default : default;
            bool groupFirst = group is null || index == 0 || colGroupOf[index - 1] != group;
            for (int step = 0; step < span && column < ncols; step++, column++)
            {
                bool groupLast = group is null
                    || step + 1 >= span
                    || index + 1 >= colElems.Count
                    || colGroupOf[index + 1] != group;
                columnBands[column] = new DomTableCollapsedBorders.Band(
                    own, groupBorder, groupFirst && step == 0, groupLast);
            }
        }

        List<DomTableCollapsedBorders.Cell> cells = new(placed.Count);
        foreach (PlacedCell placedCell in placed)
        {
            // An anonymous cell has no border of its own to contribute to the collapse, and
            // nothing to write the resolved half back to.
            Edges border = placedCell.Cell.Element is { } cell
                ? StyleOf(cell)?.Border ?? default
                : default;
            cells.Add(new DomTableCollapsedBorders.Cell(
                placedCell.Row,
                placedCell.Column,
                placedCell.RowSpan,
                placedCell.ColSpan,
                border));
        }

        (Edges tableBorder, Edges[] cellBorders) = DomTableCollapsedBorders.Resolve(
            style.Border, rows.Count, ncols, cells, rowBands, columnBands);

        style.CollapsedBorder = tableBorder;
        for (int index = 0; index < placed.Count; index++)
        {
            if (placed[index].Cell.Element is { } cell && StyleOf(cell) is { } cellStyle)
            {
                cellStyle.CollapsedBorder = cellBorders[index];
            }
        }

        if (!nativeHtmlTable)
        {
            return;
        }

        foreach ((NodeId row, int _) in rows)
        {
            if (StyleOf(row) is { } rowStyle)
            {
                rowStyle.CollapsedBorder = Edges.Zero;
            }

            if (DomTraversal.RenderedParent(tree, row) is { } parent
                && DomTraversal.IsAnyLocal(tree, parent, "thead", "tbody", "tfoot")
                && StyleOf(parent) is { } groupStyle)
            {
                groupStyle.CollapsedBorder = Edges.Zero;
            }
        }

        foreach (NodeId colEl in colElems)
        {
            if (StyleOf(colEl) is { } colStyle)
            {
                colStyle.CollapsedBorder = Edges.Zero;
            }
        }
    }

    private static int SpanAttribute(DomTree tree, NodeId id, string name)
    {
        if (tree.GetNode(id)?.GetAttribute(name) is { } value
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

    /// <summary>
    /// The maximal run of consecutive table-internal siblings around <paramref name="id"/> when
    /// its parent has to generate one anonymous table per run rather than one over all of its
    /// children, or <c>null</c> when <paramref name="id"/> is not in such a run.
    /// </summary>
    /// <remarks>
    /// CSS 2.1 17.2.1 wraps each maximal run of consecutive table-internal siblings in its own
    /// anonymous table, and this engine generates at most one per element. That is the whole
    /// answer when every child is table-internal, and wrong as soon as one is not: Chromium 141
    /// shrink-wraps a `display: table-row` div to 19.27 whatever its siblings are, where an
    /// element-wide table cannot be built at all and every row fell back to a full-width block.
    /// <para>
    /// A `display: table` parent is excluded on purpose: a non-table child of a table box is
    /// wrapped in an anonymous row and cell *inside* that table, not in a table of its own -
    /// which is what `BuildAnonymousCell` now does, so such a parent never reaches here. So is
    /// a flex or grid container, whose items are blockified.
    /// </para>
    /// </remarks>
    internal static List<NodeId>? AnonymousTableRun(BuildContext context, NodeId id)
    {
        DomTree tree = context.Tree;
        IReadOnlyDictionary<NodeId, LayoutStyle> styles = context.Styles;
        if (!styles.TryGetValue(id, out LayoutStyle? style)
            || DomTableSupport.BlockifiableRoleOf(style) == TableInternalRole.None)
        {
            return null;
        }

        // A flex or grid container is excluded for a second reason beyond blockification: it
        // builds its children in `order`, so the run's first element is not necessarily built
        // first and could not consume the rest.
        if (DomTraversal.RenderedParent(tree, id) is not { } parent
            || !context.WholeElementAnonymousTableFailed.Contains(parent)
            || !styles.TryGetValue(parent, out LayoutStyle? parentStyle)
            || parentStyle.IsTableBox
            || (parentStyle.Display is Display.Flex or Display.Grid
                && !parentStyle.InternalFlexContainer))
        {
            return null;
        }

        List<NodeId> children = [];
        FlattenContentsChildren(
            tree, DomTraversal.RenderedChildren(tree, parent), styles, children);

        List<NodeId> run = [];
        bool found = false;
        foreach (NodeId child in children)
        {
            bool internalBox = styles.TryGetValue(child, out LayoutStyle? childStyle)
                && childStyle.Display != Display.None
                && DomTableSupport.BlockifiableRoleOf(childStyle) != TableInternalRole.None;
            if (internalBox)
            {
                run.Add(child);
                found |= child == id;
                continue;
            }

            // Source formatting between two table-internal boxes does not break the run.
            if (run.Count != 0
                && tree.GetNode(child) is { IsElement: false }
                && tree.TextContent(child).Trim().Length == 0)
            {
                continue;
            }

            if (found)
            {
                return run;
            }

            run.Clear();
        }

        return found ? run : null;
    }

    internal static TaffyNodeId? BuildTable(
        BuildContext context,
        NodeId id,
        LayoutStyle? anonymousStyle = null,
        IReadOnlyList<NodeId>? tableChildren = null)
    {
        DomTree tree = context.Tree;
        IReadOnlyDictionary<NodeId, LayoutStyle> styles = context.Styles;
        if (!styles.TryGetValue(id, out LayoutStyle? elementStyle))
        {
            return null;
        }

        LayoutStyle style = anonymousStyle ?? elementStyle;

        bool nativeHtmlTable = DomTraversal.IsLocal(tree, id, "table");

        // A CSS table - a `display: table` box that is not a `<table>`, or an anonymous table
        // box - takes its rows, row groups, captions and columns from the computed `display` of
        // its descendants. A `null` structure means it holds something this engine does not
        // model, and falling back to ordinary box construction preserves every child.
        // Only a genuine table box wraps its own loose children in anonymous cells. An
        // anonymous table box generated inside an element that is not a table covers just the
        // run of table-internal children (`AnonymousTableRun`); everything else stays in the
        // element's own formatting context, which is where Chromium 141 leaves it - the `xx`
        // of a `table-cell` div holding `xx` plus two cells is a line of its own above a
        // 38.41 x 18 anonymous table, not a third cell beside them.
        CssTableStructure? cssTable = nativeHtmlTable
            ? null
            : DomTableSupport.CollectCssTableStructure(
                tree, id, styles, tableChildren, genuineTableBox: anonymousStyle is null);
        if (!nativeHtmlTable && cssTable is null)
        {
            return null;
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
            foreach (CssTableRow cssRow in cssTable!.Rows)
            {
                // `GroupEnd` only exists to bound `rowspan="0"`, which is an HTML attribute on
                // a `<td>`; a CSS cell never spans, so every row is its own group here.
                rows.Add((cssRow.Element ?? id, rows.Count + 1));
            }

            if (rows.Count == 0)
            {
                return null;
            }
        }

        if (rows.Count > MaxRows)
        {
            rows.RemoveRange(MaxRows, rows.Count - MaxRows);
        }

        int nrows = rows.Count;

        int SpanAttr(NodeId cid, string name) => SpanAttribute(tree, cid, name);

        HashSet<(int Row, int Column)> occupied = [];
        List<PlacedCell> placed = [];
        int ncols = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            (NodeId tr, int groupEnd) = rows[r];
            int c = 0;
            List<CssTableCell> rowChildren;
            if (nativeHtmlTable)
            {
                rowChildren = [];
                foreach (NodeId cid in tree.Children(tr))
                {
                    rowChildren.Add(new CssTableCell { Element = cid, Owner = tr });
                }
            }
            else
            {
                CssTableRow cssRow = cssTable!.Rows[r];

                // A row that generated no cell of its own contributes one anonymous cell
                // holding its whole content, and the row element stands in for that cell.
                rowChildren = cssRow.WholeRowCell is { } wholeRow
                    ? [new CssTableCell { Element = wholeRow, Owner = wholeRow }]
                    : cssRow.Cells;
            }

            foreach (CssTableCell cell in rowChildren)
            {
                NodeId? element = cell.Element;
                if (element is { } cid)
                {
                    if (nativeHtmlTable && !DomTraversal.IsAnyLocal(tree, cid, "td", "th"))
                    {
                        continue;
                    }

                    // A hidden cell is removed from the table model entirely.
                    if (styles.TryGetValue(cid, out LayoutStyle? hiddenStyle)
                        && hiddenStyle.Display == Display.None)
                    {
                        continue;
                    }
                }

                while (occupied.Contains((r, c)))
                {
                    c++;
                }

                if (c >= MaxCols)
                {
                    break;
                }

                // `colspan` / `rowspan` are HTML attributes on a `<td>`; a CSS cell, authored
                // or anonymous, always occupies one slot.
                int cs = nativeHtmlTable && element is { } spanCell
                    ? Math.Clamp(SpanAttr(spanCell, "colspan"), 1, MaxSpan)
                    : 1;

                // rowspan=0 means "span to the end of this row group".
                int rsRaw = nativeHtmlTable && element is { } rowSpanCell
                    ? SpanAttr(rowSpanCell, "rowspan")
                    : 1;
                int rowsLeftInGroup = Math.Max(Math.Min(groupEnd, nrows) - r, 1);
                int rs = Math.Clamp(rsRaw == 0 ? rowsLeftInGroup : rsRaw, 1, rowsLeftInGroup);
                for (int dr = 0; dr < rs; dr++)
                {
                    for (int dc = 0; dc < cs; dc++)
                    {
                        occupied.Add((r + dr, c + dc));
                    }
                }

                placed.Add(new PlacedCell(cell, r, c, rs, cs));
                c += cs;
                ncols = Math.Min(Math.Max(ncols, c), MaxCols);
            }
        }

        if (placed.Count == 0 || ncols == 0)
        {
            return null;
        }

        // <col> elements (direct or under <colgroup>), each spanning `span` columns. Gathered
        // here rather than beside the column pre-pass below because the collapsing border model
        // needs them before the cells are built.
        List<NodeId> colElems = [];
        List<NodeId?> colGroupOf = [];
        if (nativeHtmlTable)
        {
            foreach (NodeId cid in tree.Children(id))
            {
                switch (DomTraversal.ElementLocalName(tree, cid))
                {
                    case "col":
                        colElems.Add(cid);
                        colGroupOf.Add(null);
                        break;
                    case "colgroup":
                        foreach (NodeId gc in tree.Children(cid))
                        {
                            if (DomTraversal.IsLocal(tree, gc, "col"))
                            {
                                colElems.Add(gc);
                                colGroupOf.Add(cid);
                            }
                        }

                        break;
                }
            }
        }
        else
        {
            colElems.AddRange(cssTable!.Columns);
            colGroupOf.AddRange(cssTable.ColumnGroups);
        }

        ResolveCollapsedBorders(
            context, id, style, nativeHtmlTable, rows, placed, ncols, colElems, colGroupOf);

        // <caption> children, which are laid out in their own full-width rows above and below
        // the cells rather than in the table's own formatting context. Deviation from
        // crates/obscura-render/src/dom.rs, which builds no box for a caption at all - Chromium
        // 141 reports one, the width of the table's border box, and counts its height in the
        // table's.
        List<(NodeId Node, bool Bottom)> captions = [];
        foreach (NodeId child in nativeHtmlTable ? tree.Children(id) : cssTable!.Captions)
        {
            if (nativeHtmlTable && !DomTraversal.IsLocal(tree, child, "caption"))
            {
                continue;
            }

            if (!styles.TryGetValue(child, out LayoutStyle? captionStyle)
                || captionStyle.Display == Display.None)
            {
                continue;
            }

            captions.Add((
                child,
                captionStyle.CaptionSideBottom ?? style.CaptionSideBottom ?? false));
        }

        int topCaptions = 0;
        foreach ((NodeId _, bool bottom) in captions)
        {
            if (!bottom)
            {
                topCaptions++;
            }
        }

        int bottomCaptions = captions.Count - topCaptions;

        // The rows whose whole content is one anonymous cell, so the cell built out of the row
        // element does not also claim the row's rect - the band below is what reports that.
        HashSet<NodeId> wholeRowCells = [];
        if (!nativeHtmlTable)
        {
            foreach (CssTableRow cssRow in cssTable!.Rows)
            {
                if (cssRow.WholeRowCell is { } wholeRow)
                {
                    wholeRowCells.Add(wholeRow);
                }
            }
        }

        // Build each cell and pin it to its grid area.
        List<TaffyNodeId> children = [];
        foreach (PlacedCell placedCell in placed)
        {
            (CssTableCell cell, int r, int c, int rs, int cs) = placedCell;
            TaffyNodeId? built = cell.Element is { } cellElement
                ? Build(context, cellElement)
                : BuildAnonymousCell(context, cell.Owner, style, cell.AnonymousContent);
            if (built is not { } cellNode)
            {
                continue;
            }

            TaffyStyle cellStyle = context.TaffyTree.GetStyle(cellNode).Clone();
            if (cell.Element is { } cid && wholeRowCells.Contains(cid))
            {
                // The box is the row's anonymous cell, not the row. CSS 2.1 17.2.1 gives an
                // anonymous box only inherited properties, and `margin` and `padding` do not
                // apply to a row in the first place - Chromium 141 lays a
                // `display: table-row; margin: 10px; padding: 10px; border: 3px` div holding
                // `Hello world` out at 105.97 x 19 at the parent's origin, exactly as it lays
                // out the same div with none of the three.
                cellStyle.Margin = default;
                cellStyle.Padding = default;
                cellStyle.Border = default;
                context.IdMap.Remove(cellNode);
            }

            cellStyle.GridRow = new TaffyLine(
                TaffyGridPlacement.FromLineIndex((short)(r + 1 + topCaptions)),
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

            // Taffy's grid-item automatic minimum is an engine artifact here. An anonymous
            // cell has no `min-width` of its own, so it always takes the zero.
            if (cell.Element is not { } minWidthCell
                || !styles.TryGetValue(minWidthCell, out LayoutStyle? cellLayoutStyle)
                || cellLayoutStyle.MinWidth.IsAuto)
            {
                Layout.Size<TaffyDimension> minSize = cellStyle.MinSize;
                minSize.Width = TaffyDimension.FromLength(0f);
                cellStyle.MinSize = minSize;
            }

            context.TaffyTree.SetStyle(cellNode, cellStyle);
            context.Ifc.TableGridCells.Add(cellNode);
            children.Add(cellNode);
        }

        if (children.Count == 0)
        {
            return null;
        }

        // Rows and row groups are not boxes in this table model - the grid's children are the
        // cells - so a CSS table adds one empty item per row and row group, spanning every
        // column of the rows it covers, purely so the element has a box to report. An empty
        // leaf contributes nothing to any track, and Chromium 141 reports exactly that band as
        // the element's border box: a `display: table-row` whose only content is one anonymous
        // cell in the first of two columns is still the full 192.66 of a 154.13 + 38.53 table,
        // not the 154.13 of the cell.
        if (!nativeHtmlTable)
        {
            List<TaffyNodeId> bands = [];

            // A band carries no box of its own, but `position: relative` still offsets it -
            // Chromium 141 puts a `display: table-row; position: relative; top: 5px` row at
            // y=5 - so the mapped position and inset travel and nothing else does.
            TaffyStyle BandStyle(NodeId element)
            {
                TaffyStyle band = TaffyStyle.Default;
                if (styles.TryGetValue(element, out LayoutStyle? elementStyleForBand)
                    && elementStyleForBand.Position == TaffyPosition.Relative)
                {
                    TaffyStyle mapped = TaffyStyleMapping.ToTaffyStyle(elementStyleForBand);
                    band.Position = mapped.Position;
                    band.Inset = mapped.Inset;
                }

                band.FlexGrow = 0f;
                return band;
            }

            void AddBand(NodeId element, int firstRow, int rowSpan)
            {
                if (firstRow < 0 || firstRow >= nrows || rowSpan <= 0)
                {
                    return;
                }

                TaffyStyle bandStyle = BandStyle(element);
                bandStyle.GridRow = new TaffyLine(
                    TaffyGridPlacement.FromLineIndex((short)(firstRow + 1 + topCaptions)),
                    TaffyGridPlacement.FromSpan((ushort)Math.Min(rowSpan, nrows - firstRow)));
                bandStyle.GridColumn = new TaffyLine(
                    TaffyGridPlacement.FromLineIndex(1),
                    TaffyGridPlacement.FromSpan((ushort)ncols));
                TaffyNodeId band = context.TaffyTree.NewLeaf(bandStyle);
                context.IdMap[band] = element;
                bands.Add(band);
            }

            for (int r = 0; r < rows.Count; r++)
            {
                if (cssTable!.Rows[r].Element is { } rowElement)
                {
                    AddBand(rowElement, r, 1);
                }
            }

            foreach ((NodeId groupElement, int start, int end) in cssTable!.Groups)
            {
                AddBand(groupElement, start, end - start);
            }

            // A column and a column group get the same treatment across the other axis: every
            // row of the table, and the columns they cover. Chromium 141 reports a
            // `display: table-column; width: 200px` div in a two-column table as 0,0 200x19 -
            // its track and the table's rows - not as the empty box it is outside a table.
            void AddColumnBand(NodeId element, int firstColumn, int columnSpan)
            {
                if (firstColumn < 0 || firstColumn >= ncols || columnSpan <= 0)
                {
                    return;
                }

                TaffyStyle bandStyle = BandStyle(element);
                bandStyle.GridRow = new TaffyLine(
                    TaffyGridPlacement.FromLineIndex((short)(topCaptions + 1)),
                    TaffyGridPlacement.FromSpan((ushort)nrows));
                bandStyle.GridColumn = new TaffyLine(
                    TaffyGridPlacement.FromLineIndex((short)(firstColumn + 1)),
                    TaffyGridPlacement.FromSpan((ushort)Math.Min(columnSpan, ncols - firstColumn)));
                TaffyNodeId band = context.TaffyTree.NewLeaf(bandStyle);
                context.IdMap[band] = element;
                bands.Add(band);
            }

            Dictionary<NodeId, (int Start, int End)> columnGroupSpans = [];
            for (int index = 0; index < colElems.Count && index < ncols; index++)
            {
                AddColumnBand(colElems[index], index, 1);
                if (colGroupOf[index] is not { } columnGroup)
                {
                    continue;
                }

                columnGroupSpans[columnGroup] =
                    columnGroupSpans.TryGetValue(columnGroup, out (int Start, int End) span)
                        ? (span.Start, index + 1)
                        : (index, index + 1);
            }

            foreach ((NodeId columnGroup, (int start, int end)) in columnGroupSpans)
            {
                AddColumnBand(columnGroup, start, end - start);
            }

            // Bands come first so a row or group background paints behind its cells.
            children.InsertRange(0, bands);
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

        // `DeferCyclicFlexInlineSizes` neutralizes a percentage inline size under an indefinite
        // flex item to a definite `0px` before this box tree is built. A cell's own box never
        // uses its declared width - it always fills its grid area - but its COLUMN does, so the
        // authored percentage has to be read back here or the column loses it: a `width: 30%`
        // cell in a table inside a flex row came out 43/458 where Chromium gives 180/420.
        Dimension DeclaredWidth(NodeId cid, LayoutStyle cellStyle) =>
            context.DeferredInlineWidths.TryGetValue(cid, out Dimension authored)
                ? authored
                : cellStyle.Width;

        (float? Px, float? Percent) StyleWidth(NodeId cid)
        {
            if (styles.TryGetValue(cid, out LayoutStyle? cellStyle))
            {
                Dimension width = DeclaredWidth(cid, cellStyle);
                if (width.Kind == DimensionKind.Px && width.Value > 0f)
                {
                    return (width.Value, null);
                }

                if (width.Kind == DimensionKind.Percent && width.Value > 0f)
                {
                    return (null, width.Value);
                }
            }

            return AttrWidth(cid);
        }

        (float? Px, float? Percent) FixedStyleWidth(NodeId cid)
        {
            if (styles.TryGetValue(cid, out LayoutStyle? cellStyle))
            {
                Dimension width = DeclaredWidth(cid, cellStyle);
                if (width.Kind == DimensionKind.Px && width.Value >= 0f)
                {
                    return (width.Value, null);
                }

                if (width.Kind == DimensionKind.Percent && width.Value >= 0f)
                {
                    return (null, width.Value);
                }
            }

            return AttrWidth(cid);
        }

        int nextCol = 0;
        foreach (NodeId colEl in colElems)
        {
            // `span` is an HTML attribute on `<col>` / `<colgroup>`, and Chromium 141 ignores it
            // on anything else: a `display: table-column; width: 150px` div carrying `span=2` in
            // a 600px two-column table sizes only the first column, leaving 150 / 450.
            int span = nativeHtmlTable ? Math.Clamp(SpanAttr(colEl, "span"), 1, MaxSpan) : 1;
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
        foreach (PlacedCell placedCell in placed)
        {
            (CssTableCell cell, int _, int c, int _, int cs) = placedCell;

            // An anonymous cell carries no declared width, so it sizes no column.
            if (cs != 1 || c >= ncols || cell.Element is not { } cid)
            {
                continue;
            }

            // An anonymous cell carries no width of its own, so the `width` on the row element
            // standing in for it sizes nothing. Chromium 141 leaves a
            // `display: table-row; width: 300px` div holding `Hello world` at its 105.97
            // shrink-to-fit width.
            if (wholeRowCells.Contains(cid))
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
            foreach (PlacedCell placedCell in placed)
            {
                (CssTableCell cell, int row, int start, int _, int colspan) = placedCell;
                if (row != 0 || start >= ncols || cell.Element is not { } cid)
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

        for (int r = 0; r < rows.Count; r++)
        {
            // A CSS anonymous row has no element, and `rows[r].Row` is then the table itself,
            // whose own height is not a row minimum.
            NodeId? rowElement = nativeHtmlTable ? rows[r].Row : cssTable!.Rows[r].Element;
            if (rowElement is { } element
                && styles.TryGetValue(element, out LayoutStyle? rowStyle)
                && rowStyle.Height.Kind == DimensionKind.Px
                && rowStyle.Height.Value > 0f)
            {
                rowMin[r] = rowStyle.Height.Value;
            }
        }

        foreach (PlacedCell placedCell in placed)
        {
            (CssTableCell cell, int r, int _, int rs, int _) = placedCell;
            if (rs != 1 || cell.Element is not { } cid)
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

        // In the separate-border model, border-spacing also exists between the table edge and
        // the first/last row and column. Grid `gap` only covers interior tracks.
        (float horizontalGap, float verticalGap) = DomStyleFixups.TableSpacing(style);

        // CSS 2.1 17.6.2: the collapsing border model ignores the table's own padding.
        Edges tablePadding = DomStyleFixups.TableUsedPadding(style);

        // A caption is a full-width row of its own, but it sits *outside* everything between
        // the table's border box and its first row - the border, the padding and the leading
        // border-spacing. Negative margins of exactly that inset are what take it back out,
        // and the one on the side facing the cells is added back so the grid's own row gap
        // does not double it. Chromium 141 on a `border: 5px` separate table with a caption:
        // the caption is the table's full 600 at y=0, the first row is at y=25 (18 caption +
        // 5 border + 2 spacing), and the table is 50 tall.
        Edges captionInset = new(
            style.UsedBorder.Top + tablePadding.Top + verticalGap,
            style.UsedBorder.Right + tablePadding.Right + horizontalGap,
            style.UsedBorder.Bottom + tablePadding.Bottom + verticalGap,
            style.UsedBorder.Left + tablePadding.Left + horizontalGap);
        List<float?> captionRowMin = [];
        int nextTopCaption = 0;
        int nextBottomCaption = 0;
        foreach ((NodeId captionId, bool bottom) in captions)
        {
            if (Build(context, captionId) is not { } captionNode)
            {
                continue;
            }

            int index = bottom ? nextBottomCaption++ : nextTopCaption++;
            int count = bottom ? bottomCaptions : topCaptions;
            float outer = bottom ? captionInset.Bottom : captionInset.Top;
            float marginTop = bottom
                ? (index == 0 ? outer - verticalGap : -verticalGap)
                : (index == 0 ? -outer : -verticalGap);
            float marginBottom = bottom
                ? (index + 1 == count ? -outer : 0f)
                : (index + 1 == count ? outer - verticalGap : 0f);

            int row = bottom ? topCaptions + nrows + index : index;
            TaffyStyle captionTaffy = context.TaffyTree.GetStyle(captionNode).Clone();
            captionTaffy.GridRow = new TaffyLine(
                TaffyGridPlacement.FromLineIndex((short)(row + 1)),
                TaffyGridPlacement.FromSpan(1));
            captionTaffy.GridColumn = new TaffyLine(
                TaffyGridPlacement.FromLineIndex(1),
                TaffyGridPlacement.FromSpan((ushort)ncols));
            captionTaffy.FlexGrow = 0f;
            Layout.Rect<TaffyLengthPercentageAuto> captionMargin = captionTaffy.Margin;
            captionMargin.Top = TaffyLengthPercentageAuto.FromLength(marginTop);
            captionMargin.Bottom = TaffyLengthPercentageAuto.FromLength(marginBottom);
            captionMargin.Left = TaffyLengthPercentageAuto.FromLength(-captionInset.Left);
            captionMargin.Right = TaffyLengthPercentageAuto.FromLength(-captionInset.Right);
            captionTaffy.Margin = captionMargin;
            context.TaffyTree.SetStyle(captionNode, captionTaffy);
            context.Ifc.TableCaptions[captionNode] = marginTop + marginBottom;
            children.Add(captionNode);
            captionRowMin.Add(null);
        }

        int captionRows = captionRowMin.Count;
        int ntracks = nrows + captionRows;

        TaffyGridTemplateComponent RowTrack(int r)
        {
            int source = r - nextTopCaption;
            TaffyMinTrack min = source >= 0 && source < nrows && rowMin[source] is { } height
                ? TaffyMinTrack.FromLength(height)
                : TaffyMinTrack.Auto;
            return TaffyGridTemplateComponent.FromSingle(
                TaffyTrackSizingFunction.MinMax(min, TaffyMaxTrack.Auto));
        }

        LayoutStyle gridStyle = style.Clone();
        gridStyle.Padding = new Edges(
            tablePadding.Top + verticalGap,
            tablePadding.Right + horizontalGap,
            tablePadding.Bottom + verticalGap,
            tablePadding.Left + horizontalGap);
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

        List<TaffyGridTemplateComponent> rowTracks = new(ntracks);
        for (int index = 0; index < ntracks; index++)
        {
            rowTracks.Add(RowTrack(index));
        }

        tstyle.GridTemplateColumns = columnTracks;
        tstyle.GridTemplateRows = rowTracks;
        tstyle.Gap = new Layout.Size<TaffyLengthPercentage>(
            TaffyLengthPercentage.FromLength(horizontalGap),
            TaffyLengthPercentage.FromLength(verticalGap));

        TaffyNodeId tableNode = context.TaffyTree.NewWithChildren(tstyle, [.. children]);
        if (anonymousStyle is null)
        {
            context.IdMap[tableNode] = id;
        }
        else
        {
            context.Ifc.AnonymousTables[tableNode] = (id, anonymousStyle);
        }

        // The row-height pass indexes by grid row, so the caption rows are part of the list.
        List<float?> trackMin = new(ntracks);
        for (int index = 0; index < ntracks; index++)
        {
            int source = index - nextTopCaption;
            trackMin.Add(source >= 0 && source < nrows ? rowMin[source] : null);
        }

        context.Ifc.TableRows[tableNode] = trackMin;
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
