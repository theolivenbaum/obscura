// Port of the table sizing, containing-block width, and float-heuristic helpers in
// crates/obscura-render/src/dom.rs.
using PocketCalculator.Dom;
using TaffyPosition = PocketCalculator.Render.Layout.Position;

namespace PocketCalculator.Render;

internal record struct FixedTableColumn(float Length, float Percentage, bool Specified);

/// <summary>
/// The table-internal role an element's computed <c>display</c> gives it, for the CSS
/// (non-<c>&lt;table&gt;</c>) table path.
/// </summary>
internal enum TableInternalRole : byte
{
    /// <summary>Not a table-internal box.</summary>
    None,

    /// <summary><c>display: table-row</c>.</summary>
    Row,

    /// <summary><c>display: table-row-group</c>.</summary>
    RowGroup,

    /// <summary><c>display: table-header-group</c>.</summary>
    HeaderGroup,

    /// <summary><c>display: table-footer-group</c>.</summary>
    FooterGroup,

    /// <summary><c>display: table-column</c>.</summary>
    Column,

    /// <summary><c>display: table-column-group</c>.</summary>
    ColumnGroup,

    /// <summary><c>display: table-caption</c>.</summary>
    Caption,

    /// <summary><c>display: table-cell</c>.</summary>
    Cell,
}

/// <summary>
/// One cell of a CSS table row: either an authored <c>table-cell</c> box, or the anonymous
/// cell CSS 2.1 17.2.1 generates around a run of consecutive children that are not proper
/// table children.
/// </summary>
internal sealed class CssTableCell
{
    /// <summary>The authored cell element, or <c>null</c> for an anonymous cell.</summary>
    internal NodeId? Element { get; init; }

    /// <summary>
    /// The element whose children this cell came from - the table, row group or row box that
    /// generated it. An anonymous cell takes its inherited properties from it.
    /// </summary>
    internal NodeId Owner { get; init; }

    /// <summary>
    /// The consecutive children an anonymous cell wraps, in source order. Empty for an
    /// authored cell.
    /// </summary>
    internal List<NodeId> AnonymousContent { get; } = [];
}

/// <summary>One row of a CSS table, with the cells it contributes to the table grid.</summary>
internal sealed class CssTableRow
{
    /// <summary>
    /// The element that generated the row, or <c>null</c> for the anonymous row CSS 2.1
    /// 17.2.1 generates around a run of loose cells.
    /// </summary>
    internal NodeId? Element { get; init; }

    /// <summary>The cells the row contributes, in source order.</summary>
    internal List<CssTableCell> Cells { get; } = [];

    /// <summary>
    /// Set when the row generated no cell of its own and its whole content is therefore one
    /// anonymous cell. The element then stands in for that cell as well as for the row.
    /// </summary>
    internal NodeId? WholeRowCell { get; init; }
}

/// <summary>The row, group, caption and column structure of a CSS table.</summary>
internal sealed class CssTableStructure
{
    /// <summary>Rows in used order: header groups first, then the body, then footer groups.</summary>
    internal List<CssTableRow> Rows { get; } = [];

    /// <summary>Row groups, each with the half-open range of <see cref="Rows"/> it covers.</summary>
    internal List<(NodeId Element, int Start, int End)> Groups { get; } = [];

    /// <summary><c>table-caption</c> children.</summary>
    internal List<NodeId> Captions { get; } = [];

    /// <summary><c>table-column</c> elements, in column order.</summary>
    internal List<NodeId> Columns { get; } = [];

    /// <summary>The <c>table-column-group</c> each entry of <see cref="Columns"/> came from.</summary>
    internal List<NodeId?> ColumnGroups { get; } = [];
}

internal static class DomTableSupport
{
    /// <summary>Number of ancestor tables containing <paramref name="id"/>.</summary>
    internal static int TableAncestorDepth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IReadOnlySet<NodeId>? anonymousTableOwners = null)
    {
        int depth = 0;
        NodeId current = id;
        while (DomTraversal.RenderedParent(tree, current) is { } parent)
        {
            // An element that generated an anonymous table box is not itself a table box, but
            // everything below it is nested one table deeper all the same.
            if ((styles.TryGetValue(parent, out LayoutStyle? style) && style.IsTableBox)
                || anonymousTableOwners?.Contains(parent) == true)
            {
                depth++;
            }

            current = parent;
            if (depth > 4096)
            {
                break;
            }
        }

        return depth;
    }

    /// <summary>
    /// Resolve a content-box width without running layout when the complete containing-block
    /// chain is ordinary block flow.
    /// </summary>
    internal static float? ReliableNormalFlowContentWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth,
        int depth)
    {
        if (depth > 4096)
        {
            return null;
        }

        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        if (style.Float is not null
            || style.Position == TaffyPosition.Absolute
            || style.IsInlineBlock
            || style.WidthFitContent
            || style.SizeExpressions[0] is not null)
        {
            return null;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (parent is { } parentId
            && styles.TryGetValue(parentId, out LayoutStyle? transparent)
            && transparent.DisplayContents)
        {
            parent = DomTraversal.RenderedParent(tree, parentId);
        }

        float containingWidth;
        if (parent is { } resolvedParent)
        {
            if (styles.TryGetValue(resolvedParent, out LayoutStyle? parentStyle))
            {
                if (parentStyle.Display is Display.Flex or Display.Grid
                    || parentStyle.InternalFlexContainer
                    || parentStyle.ColumnCount is not null)
                {
                    return null;
                }

                if (ReliableNormalFlowContentWidth(
                        tree, resolvedParent, styles, initialCbWidth, depth + 1) is not { } width)
                {
                    return null;
                }

                containingWidth = width;
            }
            else
            {
                containingWidth = initialCbWidth;
            }
        }
        else
        {
            containingWidth = initialCbWidth;
        }

        float horizontalEdges =
            style.Padding.Left + style.Padding.Right + style.Border.Left + style.Border.Right;

        float? DeclaredContent(Dimension dimension) => dimension.Kind switch
        {
            DimensionKind.Px => style.BoxSizing == BoxSizing.ContentBox
                ? dimension.Value
                : F32.Max(dimension.Value - horizontalEdges, 0f),
            DimensionKind.Percent => style.BoxSizing == BoxSizing.ContentBox
                ? containingWidth * dimension.Value
                : F32.Max((containingWidth * dimension.Value) - horizontalEdges, 0f),
            _ => null,
        };

        float contentWidth = DeclaredContent(style.Width)
            ?? F32.Max(containingWidth - style.Margin.Left - style.Margin.Right - horizontalEdges, 0f);
        if (DeclaredContent(style.MinWidth) is { } minimum)
        {
            contentWidth = F32.Max(contentWidth, minimum);
        }

        if (DeclaredContent(style.MaxWidth) is { } maximum)
        {
            contentWidth = F32.Min(contentWidth, maximum);
        }

        return F32.Max(contentWidth, 0f);
    }

    /// <summary>
    /// Resolve an authored definite width even when the box's placement is owned by float or
    /// positioned layout.
    /// </summary>
    internal static float? ReliableDeclaredContentWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth,
        int depth)
    {
        if (depth > 4096)
        {
            return null;
        }

        if (!styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        if (style.WidthFitContent
            || style.SizeExpressions[0] is not null
            || style.SizeExpressions[2] is not null
            || style.SizeExpressions[4] is not null)
        {
            return null;
        }

        bool needsContainingWidth = style.Width.Kind == DimensionKind.Percent
            || style.MinWidth.Kind == DimensionKind.Percent
            || style.MaxWidth.Kind == DimensionKind.Percent;
        float? containingWidth = null;
        if (needsContainingWidth)
        {
            NodeId? parent = DomTraversal.RenderedParent(tree, id);
            while (parent is { } parentId
                && styles.TryGetValue(parentId, out LayoutStyle? transparent)
                && transparent.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
            }

            float? resolved = parent is { } resolvedParent
                ? ReliableNormalFlowContentWidth(tree, resolvedParent, styles, initialCbWidth, depth + 1)
                    ?? ReliableDeclaredContentWidth(tree, resolvedParent, styles, initialCbWidth, depth + 1)
                : initialCbWidth;
            if (resolved is null)
            {
                return null;
            }

            containingWidth = resolved;
        }

        float horizontalEdges =
            style.Padding.Left + style.Padding.Right + style.Border.Left + style.Border.Right;

        float? DeclaredContent(Dimension dimension)
        {
            switch (dimension.Kind)
            {
                case DimensionKind.Px:
                    return style.BoxSizing == BoxSizing.ContentBox
                        ? dimension.Value
                        : F32.Max(dimension.Value - horizontalEdges, 0f);
                case DimensionKind.Percent:
                    if (containingWidth is not { } basis)
                    {
                        return null;
                    }

                    float value = basis * dimension.Value;
                    return style.BoxSizing == BoxSizing.ContentBox
                        ? value
                        : F32.Max(value - horizontalEdges, 0f);
                default:
                    return null;
            }
        }

        if (DeclaredContent(style.Width) is not { } contentWidth)
        {
            return null;
        }

        if (DeclaredContent(style.MinWidth) is { } minimum)
        {
            contentWidth = F32.Max(contentWidth, minimum);
        }

        if (DeclaredContent(style.MaxWidth) is { } maximum)
        {
            contentWidth = F32.Min(contentWidth, maximum);
        }

        return F32.Max(contentWidth, 0f);
    }

    /// <summary>
    /// Resolve the containing-block content width used by CSS's ratio-only auto/auto replaced
    /// sizing branch.
    /// </summary>
    internal static float? ReliableRatioOnlyAvailableWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth)
    {
        if (!styles.TryGetValue(id, out LayoutStyle? image))
        {
            return null;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (parent is { } parentId)
        {
            if (!styles.TryGetValue(parentId, out LayoutStyle? parentStyle))
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            if (parentStyle.DisplayContents
                || (parentStyle.Display == Display.Inline && !parentStyle.IsInlineBlock))
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            break;
        }

        float containingWidth;
        if (parent is { } resolvedParent)
        {
            float? resolved =
                ReliableNormalFlowContentWidth(tree, resolvedParent, styles, initialCbWidth, 0)
                ?? ReliableDeclaredContentWidth(tree, resolvedParent, styles, initialCbWidth, 0);
            if (resolved is not { } width)
            {
                return null;
            }

            containingWidth = width;
        }
        else
        {
            containingWidth = initialCbWidth;
        }

        float horizontalEdges =
            image.Padding.Left + image.Padding.Right + image.Border.Left + image.Border.Right;
        return F32.Max(
            containingWidth - image.Margin.Left - image.Margin.Right - horizontalEdges,
            0f);
    }

    /// <summary>
    /// Width a normal-flow table has available to fill, when the whole containing-block chain
    /// is ordinary block flow.
    /// </summary>
    internal static float? ReliableTableAvailableWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth) =>
        TableContainingBlockWidth(tree, id, styles, initialCbWidth, forPercentage: false);

    /// <summary>
    /// Containing-block width a table's percentage width resolves against, when it can be
    /// established without running layout.
    /// </summary>
    internal static float? ReliableTablePercentageBase(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth) =>
        TableContainingBlockWidth(tree, id, styles, initialCbWidth, forPercentage: true);

    private static float? TableContainingBlockWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float initialCbWidth,
        bool forPercentage)
    {
        if (!styles.TryGetValue(id, out LayoutStyle? tableStyle))
        {
            return null;
        }

        if (tableStyle.Position == TaffyPosition.Absolute)
        {
            return null;
        }

        // How much room a box has to fill and what its percentage width resolves against are two
        // different questions, and they part company exactly here. A float or an inline-block
        // shrinks to fit the room left on its line, which sibling floats take away, so the first
        // question cannot be answered from style alone. The second can: a percentage width
        // resolves against the containing block whatever the box's own float or inline-block-ness
        // (CSS 2.1 10.2), which is why a floated `width: 100%` table is as wide as its container.
        if (!forPercentage && (tableStyle.Float is not null || tableStyle.IsInlineBlock))
        {
            return null;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (parent is { } parentId
            && styles.TryGetValue(parentId, out LayoutStyle? transparent)
            && transparent.DisplayContents)
        {
            parent = DomTraversal.RenderedParent(tree, parentId);
        }

        float containingWidth;
        if (parent is { } resolvedParent && styles.TryGetValue(resolvedParent, out LayoutStyle? parentStyle))
        {
            if (parentStyle.Display is Display.Flex or Display.Grid
                || parentStyle.InternalFlexContainer
                || parentStyle.ColumnCount is not null)
            {
                return null;
            }

            if (ReliableNormalFlowContentWidth(tree, resolvedParent, styles, initialCbWidth, 0)
                is not { } width)
            {
                return null;
            }

            containingWidth = width;
        }
        else
        {
            containingWidth = initialCbWidth;
        }

        // The table's own margins come off the room it has to fill, but not off the base its
        // percentage resolves against: Chromium 141 gives `width: 100%; margin: 0 50px` in a
        // 600px block a 600px table, which overflows, rather than a 500px one.
        return forPercentage
            ? F32.Max(containingWidth, 0f)
            : F32.Max(containingWidth - tableStyle.Margin.Left - tableStyle.Margin.Right, 0f);
    }

    /// <summary>
    /// The table-internal role <paramref name="style"/> gives a box.
    /// </summary>
    /// <remarks>
    /// The authored keyword is read before <see cref="LayoutStyle.IsTableCellBox"/> because
    /// <c>ApplyDisplay</c> records one of the seven internal values without clearing the UA
    /// approximation underneath it, so a <c>&lt;td style="display:table-row"&gt;</c> still
    /// carries <c>IsTableCellBox</c> while Chromium 141 reports - and lays it out as - a row.
    /// </remarks>
    internal static TableInternalRole RoleOf(LayoutStyle style) => style.AuthoredTableDisplay switch
    {
        TableInternalDisplay.Row => TableInternalRole.Row,
        TableInternalDisplay.RowGroup => TableInternalRole.RowGroup,
        TableInternalDisplay.HeaderGroup => TableInternalRole.HeaderGroup,
        TableInternalDisplay.FooterGroup => TableInternalRole.FooterGroup,
        TableInternalDisplay.Column => TableInternalRole.Column,
        TableInternalDisplay.ColumnGroup => TableInternalRole.ColumnGroup,
        TableInternalDisplay.Caption => TableInternalRole.Caption,
        _ => style.IsTableCellBox ? TableInternalRole.Cell : TableInternalRole.None,
    };

    /// <summary>
    /// The table-internal role of a node, or <see cref="TableInternalRole.None"/> for a text
    /// node or an element with no computed style.
    /// </summary>
    internal static TableInternalRole RoleOf(
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles) =>
        styles.TryGetValue(id, out LayoutStyle? style) ? RoleOf(style) : TableInternalRole.None;

    /// <summary>
    /// Is the box floated or absolutely positioned, so that CSS Display 3 blockifies its outer
    /// display and an authored internal-table <c>display</c> on it means nothing?
    /// </summary>
    /// <remarks>
    /// Chromium 141 reports <c>block</c> for a <c>display: table-row; float: left</c> div and
    /// takes it out of flow - its 600px parent is 0 tall - rather than making it a row of an
    /// anonymous table. The flex- and grid-item half of the same rule is checked where the
    /// container is in hand, in <c>DomBuild.WantsAnonymousTableBox</c>.
    /// <para>
    /// This governs the authored keywords only. <see cref="LayoutStyle.IsTableCellBox"/> is
    /// deliberately left alone: it is also how this engine marks a <c>&lt;td&gt;</c>, a floated
    /// cell has been part of a table here since before any of the keywords were laid out, and
    /// Bootstrap's <c>display: table-cell; float: left</c> input group depends on it.
    /// </para>
    /// </remarks>
    internal static bool IsBlockifiedOutOfFlow(LayoutStyle style) =>
        style.Float is not null
        || style.Position == TaffyPosition.Absolute
        || style.PositionFixed;

    /// <summary>
    /// <see cref="RoleOf(LayoutStyle)"/> with the authored keywords dropped for a box whose
    /// outer display is blockified out from under them.
    /// </summary>
    internal static TableInternalRole BlockifiableRoleOf(LayoutStyle style)
    {
        TableInternalRole role = RoleOf(style);
        return role is TableInternalRole.None or TableInternalRole.Cell
                || !IsBlockifiedOutOfFlow(style)
            ? role
            : TableInternalRole.None;
    }

    /// <summary>
    /// Does a child of a table box generate no box at all, so that it neither becomes part of
    /// the table nor forces the table builder to give up?
    /// </summary>
    private static bool IsIgnorableTableChild(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (styles.TryGetValue(id, out LayoutStyle? style) && style.Display == Display.None)
        {
            return true;
        }

        // Source formatting between table-internal boxes is not content; CSS 2.1 17.2.1 drops
        // it rather than wrapping it in an anonymous cell.
        return tree.GetNode(id) is { IsElement: false } && tree.TextContent(id).Trim().Length == 0;
    }

    /// <summary>The same, for a node.</summary>
    private static TableInternalRole BlockifiableRoleOf(
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles) =>
        styles.TryGetValue(id, out LayoutStyle? style)
            ? BlockifiableRoleOf(style)
            : TableInternalRole.None;

    /// <summary>
    /// Read the row, group, caption and column structure of a CSS table - a <c>display: table</c>
    /// box that is not a <c>&lt;table&gt;</c> element, or an anonymous table box - from the
    /// computed <c>display</c> of its descendants. Answers <c>null</c> when the table holds
    /// something this engine does not model, so the caller can fall back to ordinary boxes.
    /// </summary>
    /// <remarks>
    /// New behaviour, not a port: crates/obscura-render/src/dom.rs builds no table for any of
    /// the seven internal <c>display</c> values, and this engine previously modelled a CSS
    /// table as exactly one anonymous row of <c>table-cell</c> children. Chromium 141 generates
    /// the boxes CSS 2.1 17.2.1 asks for, which is what this reconstructs: rows, row groups
    /// (nested, and with header groups hoisted to the front and footer groups pushed to the
    /// back), anonymous rows around loose cells, and captions.
    /// <para>
    /// A run of consecutive children that are not proper table children becomes one anonymous
    /// cell (<c>DomBuild.BuildAnonymousCell</c>), which is a box with no DOM node. Rust
    /// generates none, so such a table was not built at all and the caller fell back to
    /// ordinary blocks; Chromium 141 shrink-to-fits
    /// <c>&lt;div style="display:table"&gt;aa&lt;/div&gt;</c> to 19.2 rather than the 600 that
    /// produced. A table box holding no cell at all - only a caption, or only columns, or only
    /// collapsible whitespace - still answers <c>null</c>. See "Known deviations" in todo.md.
    /// </para>
    /// </remarks>
    /// <param name="tableChildren">
    /// The children the table is built from, when it is an anonymous table over one run of a
    /// parent's children rather than over an element's whole child list.
    /// </param>
    /// <param name="genuineTableBox">
    /// Whether <paramref name="table"/> is a table box in its own right. Only then does a
    /// child that is not a proper table child generate an anonymous cell inside the table; a
    /// generated anonymous table box covers only the run of table-internal children, so such a
    /// child means the whole-element table cannot be built and the caller falls back.
    /// </param>
    internal static CssTableStructure? CollectCssTableStructure(
        DomTree tree,
        NodeId table,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IReadOnlyList<NodeId>? tableChildren = null,
        bool genuineTableBox = true)
    {
        CssTableStructure structure = new();

        // Header and footer groups are laid out first and last whatever their source order, so
        // the three buckets are filled separately and concatenated at the end.
        List<CssTableRow>[] rowBuckets = [[], [], []];
        List<(NodeId Element, int Start, int End)>[] groupBuckets = [[], [], []];

        bool Fill(NodeId parent, int bucket, bool isGroup)
        {
            List<NodeId> children = [];
            if (tableChildren is not null && parent == table)
            {
                children.AddRange(tableChildren);
            }
            else
            {
                DomBuild.FlattenContentsChildren(
                    tree, DomTraversal.RenderedChildren(tree, parent), styles, children);
            }

            List<CssTableRow> rows = rowBuckets[bucket];

            // A row group holding no table-internal box at all is one anonymous row around one
            // anonymous cell, and the group element stands in for the cell the way a row
            // element does. Chromium 141 shrink-to-fits a `display: table-row-group` div
            // holding `TEXT` to 38.53 rather than laying it out at the parent's width.
            if (isGroup)
            {
                bool anyInternal = false;
                bool anyContent = false;
                foreach (NodeId child in children)
                {
                    if (IsIgnorableTableChild(tree, child, styles))
                    {
                        continue;
                    }

                    anyContent = true;
                    if (BlockifiableRoleOf(child, styles) != TableInternalRole.None)
                    {
                        anyInternal = true;
                        break;
                    }
                }

                if (anyContent && !anyInternal)
                {
                    rows.Add(new CssTableRow { WholeRowCell = parent });
                    return true;
                }
            }

            List<CssTableCell> pendingCells = [];
            List<NodeId> pendingAnonymous = [];

            // CSS 2.1 17.2.1 wraps each maximal run of consecutive children that are not
            // proper table children in one anonymous cell, which then joins the anonymous row
            // its neighbouring loose cells form. Chromium 141 lays
            // `display: table` over `table-cell aa` + `div bb` out as one 38.41 row of two
            // 19.2 cells, not as two stacked blocks.
            void FlushAnonymousCell()
            {
                if (pendingAnonymous.Count == 0)
                {
                    return;
                }

                CssTableCell cell = new() { Owner = parent };
                cell.AnonymousContent.AddRange(pendingAnonymous);
                pendingCells.Add(cell);
                pendingAnonymous.Clear();
            }

            void FlushPendingCells()
            {
                FlushAnonymousCell();
                if (pendingCells.Count == 0)
                {
                    return;
                }

                CssTableRow anonymous = new();
                anonymous.Cells.AddRange(pendingCells);
                rows.Add(anonymous);
                pendingCells.Clear();
            }

            foreach (NodeId child in children)
            {
                if (IsIgnorableTableChild(tree, child, styles))
                {
                    continue;
                }

                switch (BlockifiableRoleOf(child, styles))
                {
                    case TableInternalRole.Cell:
                        FlushAnonymousCell();
                        pendingCells.Add(new CssTableCell { Element = child, Owner = parent });
                        continue;

                    case TableInternalRole.Row:
                        FlushPendingCells();
                        if (CollectCssTableRow(tree, child, styles) is not { } row)
                        {
                            return false;
                        }

                        rows.Add(row);
                        continue;

                    case TableInternalRole.HeaderGroup:
                    case TableInternalRole.FooterGroup:
                    case TableInternalRole.RowGroup:
                    {
                        FlushPendingCells();

                        // A nested group stays in the bucket its outermost group chose: only a
                        // group that is a child of the table itself is hoisted or pushed.
                        int target = bucket != 1
                            ? bucket
                            : RoleOf(child, styles) switch
                            {
                                TableInternalRole.HeaderGroup => 0,
                                TableInternalRole.FooterGroup => 2,
                                _ => 1,
                            };
                        int start = rowBuckets[target].Count;
                        if (!Fill(child, target, true))
                        {
                            return false;
                        }

                        groupBuckets[target].Add((child, start, rowBuckets[target].Count));
                        continue;
                    }

                    case TableInternalRole.Caption:
                        FlushPendingCells();
                        structure.Captions.Add(child);
                        continue;

                    case TableInternalRole.Column:
                        FlushPendingCells();
                        structure.Columns.Add(child);
                        structure.ColumnGroups.Add(null);
                        continue;

                    case TableInternalRole.ColumnGroup:
                    {
                        FlushPendingCells();
                        List<NodeId> columns = [];
                        DomBuild.FlattenContentsChildren(
                            tree, DomTraversal.RenderedChildren(tree, child), styles, columns);
                        foreach (NodeId column in columns)
                        {
                            if (!IsIgnorableTableChild(tree, column, styles)
                                && BlockifiableRoleOf(column, styles) == TableInternalRole.Column)
                            {
                                structure.Columns.Add(column);
                                structure.ColumnGroups.Add(child);
                            }
                        }

                        continue;
                    }

                    default:
                        if (!genuineTableBox && parent == table)
                        {
                            return false;
                        }

                        pendingAnonymous.Add(child);
                        continue;
                }
            }

            FlushPendingCells();
            return true;
        }

        if (!Fill(table, 1, false))
        {
            return null;
        }

        int offset = 0;
        for (int bucket = 0; bucket < rowBuckets.Length; bucket++)
        {
            structure.Rows.AddRange(rowBuckets[bucket]);
            foreach ((NodeId element, int start, int end) in groupBuckets[bucket])
            {
                structure.Groups.Add((element, start + offset, end + offset));
            }

            offset += rowBuckets[bucket].Count;
        }

        return structure;
    }

    /// <summary>
    /// Read one CSS table row, wrapping each run of children that are not cells in an
    /// anonymous cell of its own.
    /// </summary>
    private static CssTableRow? CollectCssTableRow(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        List<NodeId> children = [];
        DomBuild.FlattenContentsChildren(
            tree, DomTraversal.RenderedChildren(tree, id), styles, children);

        List<CssTableCell> cells = [];
        List<NodeId> pendingAnonymous = [];
        bool anyAuthoredCell = false;

        void FlushAnonymousCell()
        {
            if (pendingAnonymous.Count == 0)
            {
                return;
            }

            CssTableCell cell = new() { Owner = id };
            cell.AnonymousContent.AddRange(pendingAnonymous);
            cells.Add(cell);
            pendingAnonymous.Clear();
        }

        foreach (NodeId child in children)
        {
            if (IsIgnorableTableChild(tree, child, styles))
            {
                continue;
            }

            if (BlockifiableRoleOf(child, styles) == TableInternalRole.Cell)
            {
                FlushAnonymousCell();
                cells.Add(new CssTableCell { Element = child, Owner = id });
                anyAuthoredCell = true;
            }
            else
            {
                // CSS 2.1 17.2.1: a run of children that are not cells becomes one anonymous
                // cell beside the authored ones. Chromium 141 puts the `bb` of a row holding
                // `table-cell aa` + `div bb` at x=19.2 in the same 38.41 row.
                pendingAnonymous.Add(child);
            }
        }

        if (!anyAuthoredCell)
        {
            // The row's whole content is one anonymous cell, which fills the row, so the row
            // element stands in for it: an anonymous box takes only inherited properties and a
            // row's own margin, padding and border do not apply, leaving the two boxes with
            // identical geometry and nothing to tell apart.
            return new CssTableRow { Element = id, WholeRowCell = id };
        }

        FlushAnonymousCell();

        CssTableRow row = new() { Element = id };
        row.Cells.AddRange(cells);
        return row;
    }

    /// <summary>
    /// Collect rows together with the exclusive end of their originating row group.
    /// </summary>
    internal static void CollectTableRows(DomTree tree, NodeId id, List<(NodeId Row, int GroupEnd)> rows)
    {
        int? directStart = null;
        foreach (NodeId cid in tree.Children(id))
        {
            string? local = DomTraversal.ElementLocalName(tree, cid);
            switch (local)
            {
                case "tr":
                    directStart ??= rows.Count;
                    rows.Add((cid, 0));
                    break;
                case "thead":
                case "tbody":
                case "tfoot":
                {
                    if (directStart is { } start)
                    {
                        int groupEnd = rows.Count;
                        for (int index = start; index < groupEnd; index++)
                        {
                            rows[index] = (rows[index].Row, groupEnd);
                        }

                        directStart = null;
                    }

                    int sectionStart = rows.Count;
                    foreach (NodeId row in tree.Children(cid))
                    {
                        if (DomTraversal.IsLocal(tree, row, "tr"))
                        {
                            rows.Add((row, 0));
                        }
                    }

                    int sectionEnd = rows.Count;
                    for (int index = sectionStart; index < sectionEnd; index++)
                    {
                        rows[index] = (rows[index].Row, sectionEnd);
                    }

                    break;
                }
            }
        }

        if (directStart is { } trailing)
        {
            int end = rows.Count;
            for (int index = trailing; index < end; index++)
            {
                rows[index] = (rows[index].Row, end);
            }
        }
    }

    /// <summary>Minimum track width implied by percentage columns in an auto-width table.</summary>
    internal static float AutoTablePercentageIntrinsicFloor(
        IReadOnlyList<float> minimums,
        IReadOnlyList<float?> percentages)
    {
        if (minimums.Count != percentages.Count || minimums.Count == 0)
        {
            return 0f;
        }

        float remaining = 1f;
        float autoMinimum = 0f;
        float required = 0f;
        for (int index = 0; index < minimums.Count; index++)
        {
            float minimum = F32.Max(minimums[index], 0f);
            if (percentages[index] is not { } percentage)
            {
                autoMinimum += minimum;
                continue;
            }

            float effective = F32.Min(F32.Max(percentage, 0f), remaining);
            remaining = F32.Max(remaining - effective, 0f);
            if (minimum > 0f)
            {
                if (effective <= float.Epsilon)
                {
                    return float.PositiveInfinity;
                }

                required = F32.Max(required, minimum / effective);
            }
        }

        if (autoMinimum > 0f)
        {
            if (remaining <= float.Epsilon)
            {
                return float.PositiveInfinity;
            }

            required = F32.Max(required, autoMinimum / remaining);
        }

        return required;
    }

    /// <summary>Resolve auto-table column constraints into final track widths.</summary>
    internal static List<float> DistributeAutoTableColumns(
        float target,
        IReadOnlyList<float> minimums,
        IReadOnlyList<float> preferreds,
        IReadOnlyList<float?> fixedWidths,
        IReadOnlyList<float?> percentages)
    {
        int count = minimums.Count;
        if (count == 0
            || preferreds.Count != count
            || fixedWidths.Count != count
            || percentages.Count != count)
        {
            return [.. minimums];
        }

        target = F32.Max(target, 0f);
        List<float> mins = new(count);
        foreach (float value in minimums)
        {
            mins.Add(F32.Max(value, 0f));
        }

        List<float> prefs = new(count);
        for (int index = 0; index < count; index++)
        {
            prefs.Add(F32.Max(preferreds[index], mins[index]));
        }

        // Percentage column constraints are cumulatively clamped to 100% in source order.
        float remaining = 1f;
        List<float?> effectivePercentages = new(count);
        foreach (float? percentage in percentages)
        {
            if (percentage is { } value)
            {
                float used = F32.Min(F32.Max(value, 0f), remaining);
                remaining = F32.Max(remaining - used, 0f);
                effectivePercentages.Add(used);
            }
            else
            {
                effectivePercentages.Add(null);
            }
        }

        List<float> guessMin = [.. mins];
        List<float> guessMinPct = new(count);
        for (int index = 0; index < count; index++)
        {
            guessMinPct.Add(effectivePercentages[index] is { } percentage
                ? F32.Max(percentage * target, mins[index])
                : mins[index]);
        }

        List<float> guessMinSpec = new(count);
        for (int index = 0; index < count; index++)
        {
            guessMinSpec.Add(effectivePercentages[index] is not null
                ? guessMinPct[index]
                : fixedWidths[index] is not null ? prefs[index] : mins[index]);
        }

        List<float> guessPref = new(count);
        for (int index = 0; index < count; index++)
        {
            guessPref.Add(effectivePercentages[index] is not null ? guessMinPct[index] : prefs[index]);
        }

        static float Total(IReadOnlyList<float> values)
        {
            float sum = 0f;
            foreach (float value in values)
            {
                sum += value;
            }

            return sum;
        }

        float minTotal = Total(guessMin);
        float minPctTotal = Total(guessMinPct);
        float minSpecTotal = Total(guessMinSpec);
        float prefTotal = Total(guessPref);

        static List<float> Interpolate(IReadOnlyList<float> lower, IReadOnlyList<float> upper, float wanted)
        {
            float lowerTotal = 0f;
            float upperTotal = 0f;
            foreach (float value in lower)
            {
                lowerTotal += value;
            }

            foreach (float value in upper)
            {
                upperTotal += value;
            }

            float deltaTotal = F32.Max(upperTotal - lowerTotal, 0f);
            if (deltaTotal <= float.Epsilon)
            {
                return [.. lower];
            }

            float scale = Math.Clamp((wanted - lowerTotal) / deltaTotal, 0f, 1f);
            List<float> result = new(lower.Count);
            for (int index = 0; index < lower.Count; index++)
            {
                result.Add(lower[index] + (F32.Max(upper[index] - lower[index], 0f) * scale));
            }

            return result;
        }

        if (target <= minTotal)
        {
            return guessMin;
        }

        if (target < minPctTotal)
        {
            return Interpolate(guessMin, guessMinPct, target);
        }

        if (target < minSpecTotal)
        {
            return Interpolate(guessMinPct, guessMinSpec, target);
        }

        if (target < prefTotal)
        {
            return Interpolate(guessMinSpec, guessPref, target);
        }

        List<float> result2 = guessPref;
        float extra = target - prefTotal;
        if (extra <= float.Epsilon)
        {
            return result2;
        }

        List<(int Index, float Weight)> candidates = [];
        for (int index = 0; index < count; index++)
        {
            if (effectivePercentages[index] is null && fixedWidths[index] is null && prefs[index] > 0f)
            {
                candidates.Add((index, prefs[index]));
            }
        }

        if (candidates.Count == 0)
        {
            for (int index = 0; index < count; index++)
            {
                if (effectivePercentages[index] is null && fixedWidths[index] is null)
                {
                    candidates.Add((index, 1f));
                }
            }
        }

        if (candidates.Count == 0)
        {
            for (int index = 0; index < count; index++)
            {
                if (effectivePercentages[index] is null && fixedWidths[index] is not null)
                {
                    candidates.Add((index, F32.Max(prefs[index], 0f)));
                }
            }
        }

        if (candidates.Count == 0)
        {
            for (int index = 0; index < count; index++)
            {
                if (effectivePercentages[index] is { } percentage && percentage > 0f)
                {
                    candidates.Add((index, percentage));
                }
            }
        }

        if (candidates.Count == 0)
        {
            for (int index = 0; index < count; index++)
            {
                candidates.Add((index, 1f));
            }
        }

        float weight = 0f;
        foreach ((_, float value) in candidates)
        {
            weight += value;
        }

        float candidateCount = candidates.Count;
        foreach ((int index, float value) in candidates)
        {
            result2[index] += weight > 0f ? extra * value / weight : extra / candidateCount;
        }

        return result2;
    }

    /// <summary>Resolve CSS 2 fixed-layout column constraints into exact track widths.</summary>
    internal static List<float> DistributeFixedTableColumns(
        float target,
        IReadOnlyList<FixedTableColumn> columns)
    {
        if (columns.Count == 0)
        {
            return [];
        }

        target = F32.Max(target, 0f);
        List<float> widths = new(columns.Count);
        foreach (FixedTableColumn column in columns)
        {
            widths.Add(column.Specified
                ? F32.Max(column.Length, 0f) + (F32.Max(column.Percentage, 0f) * target)
                : 0f);
        }

        float total = 0f;
        foreach (float width in widths)
        {
            total += width;
        }

        // Percentage columns are the flexible part of an over-constrained fixed table.
        if (total > target)
        {
            float percentageTotal = 0f;
            foreach (FixedTableColumn column in columns)
            {
                percentageTotal += F32.Max(column.Percentage, 0f) * target;
            }

            float shrink = F32.Min(total - target, percentageTotal);
            if (shrink > 0f && percentageTotal > 0f)
            {
                for (int index = 0; index < widths.Count; index++)
                {
                    float contribution = F32.Max(columns[index].Percentage, 0f) * target;
                    widths[index] = F32.Max(
                        widths[index] - (shrink * contribution / percentageTotal),
                        0f);
                }

                total -= shrink;
            }
        }

        float remaining = F32.Max(target - total, 0f);
        if (remaining <= float.Epsilon)
        {
            return widths;
        }

        List<int> unresolved = [];
        for (int index = 0; index < columns.Count; index++)
        {
            if (!columns[index].Specified)
            {
                unresolved.Add(index);
            }
        }

        if (unresolved.Count != 0)
        {
            float share = remaining / unresolved.Count;
            foreach (int index in unresolved)
            {
                widths[index] = share;
            }

            return widths;
        }

        List<float> weights = new(columns.Count);
        foreach (FixedTableColumn column in columns)
        {
            weights.Add(F32.Max(column.Length, 0f));
        }

        float weight = 0f;
        foreach (float value in weights)
        {
            weight += value;
        }

        if (weight <= float.Epsilon)
        {
            weights.Clear();
            foreach (FixedTableColumn column in columns)
            {
                weights.Add(F32.Max(column.Percentage, 0f));
            }

            weight = 0f;
            foreach (float value in weights)
            {
                weight += value;
            }
        }

        if (weight <= float.Epsilon)
        {
            for (int index = 0; index < weights.Count; index++)
            {
                weights[index] = 1f;
            }

            weight = columns.Count;
        }

        for (int index = 0; index < widths.Count; index++)
        {
            widths[index] += remaining * weights[index] / weight;
        }

        return widths;
    }

    /// <summary>
    /// Give each row/section its CSS table-grid band. In the grid table model these wrappers
    /// are not taffy boxes, so without this their backgrounds, borders, and DOM geometry would
    /// disappear.
    /// </summary>
    internal static void SynthesizeRowRects(DomTree tree, Dictionary<NodeId, Rect> rects)
    {
        List<NodeId> rows = [];
        List<NodeId> sections = [];
        Dictionary<NodeId, Rect> tableInline = [];
        foreach (NodeId id in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            string? local = DomTraversal.ElementLocalName(tree, id);
            switch (local)
            {
                case "tr":
                    rows.Add(id);
                    break;
                case "tbody":
                case "thead":
                case "tfoot":
                    sections.Add(id);
                    break;
                case "td":
                case "th":
                {
                    NodeId? ancestor = DomTraversal.RenderedParent(tree, id);
                    while (ancestor is { } parent)
                    {
                        if (DomTraversal.IsLocal(tree, parent, "table"))
                        {
                            if (rects.TryGetValue(id, out Rect cellRect))
                            {
                                tableInline[parent] = tableInline.TryGetValue(parent, out Rect current)
                                    ? current.Union(cellRect)
                                    : cellRect;
                            }

                            break;
                        }

                        ancestor = DomTraversal.RenderedParent(tree, parent);
                    }

                    break;
                }
            }
        }

        foreach (NodeId id in rows)
        {
            if (rects.ContainsKey(id))
            {
                continue;
            }

            Rect? inline = null;
            Rect? block = null;
            foreach (NodeId cell in tree.Children(id))
            {
                if (!DomTraversal.IsAnyLocal(tree, cell, "td", "th"))
                {
                    continue;
                }

                if (!rects.TryGetValue(cell, out Rect cellRect))
                {
                    continue;
                }

                inline = inline is { } currentInline ? currentInline.Union(cellRect) : cellRect;
                int rowspan = 1;
                if (tree.GetNode(cell)?.GetAttribute("rowspan") is { } span
                    && int.TryParse(
                        span.Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int parsed))
                {
                    rowspan = parsed;
                }

                if (rowspan == 1)
                {
                    block = block is { } currentBlock ? currentBlock.Union(cellRect) : cellRect;
                }
            }

            if (inline is not { } inlineBand)
            {
                continue;
            }

            NodeId? tableAncestor = DomTraversal.RenderedParent(tree, id);
            while (tableAncestor is { } parent)
            {
                if (tableInline.TryGetValue(parent, out Rect tableBand))
                {
                    inlineBand = inlineBand with { X = tableBand.X, Width = tableBand.Width };
                    break;
                }

                tableAncestor = DomTraversal.RenderedParent(tree, parent);
            }

            Rect blockBand = block ?? inlineBand;
            rects[id] = new Rect(inlineBand.X, blockBand.Y, inlineBand.Width, blockBand.Height);
        }

        foreach (NodeId id in sections)
        {
            if (rects.ContainsKey(id))
            {
                continue;
            }

            Rect? section = null;
            foreach (NodeId row in tree.Children(id))
            {
                if (!DomTraversal.IsLocal(tree, row, "tr"))
                {
                    continue;
                }

                if (rects.TryGetValue(row, out Rect rowRect))
                {
                    section = section is { } current ? current.Union(rowRect) : rowRect;
                }
            }

            if (section is { } sectionRect)
            {
                rects[id] = sectionRect;
            }
        }
    }

    /// <summary>Largest definite (px) width among <paramref name="id"/> and its descendants.</summary>
    internal static float? MaxDefiniteDescendantWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        float? best = null;
        foreach (NodeId d in tree.Descendants(id))
        {
            if (styles.TryGetValue(d, out LayoutStyle? style)
                && style.Width.Kind == DimensionKind.Px
                && style.Width.Value > 0f)
            {
                best = best is { } current ? F32.Max(current, style.Width.Value) : style.Width.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// Fixed content descendants can floor a table's intrinsic minimum. Width hints on
    /// cells/rows/columns are different and must remain shrinkable to the content minimum.
    /// </summary>
    internal static float? MaxDefiniteTableContentWidth(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        float? best = null;
        foreach (NodeId descendant in tree.Descendants(id))
        {
            // An authored internal-table `display` is structural for the same reason the
            // element names are: Chromium 141 leaves a `display: table-row; width: 300px` div
            // holding `Hello world` at its 105.97 shrink-to-fit width, so the 300 is a hint
            // about a band, not a definite content box that can floor the table.
            bool structural = (styles.TryGetValue(descendant, out LayoutStyle? style)
                    && RoleOf(style) != TableInternalRole.None)
                || DomTraversal.IsAnyLocal(
                    tree,
                    descendant,
                    "caption", "col", "colgroup", "thead", "tbody", "tfoot", "tr", "td", "th");
            if (structural)
            {
                continue;
            }

            if (style is not null && style.Width.Kind == DimensionKind.Px && style.Width.Value > 0f)
            {
                best = best is { } current ? F32.Max(current, style.Width.Value) : style.Width.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// Rough height budget for a float with no explicit size and no images.
    /// </summary>
    internal const float DefaultFloatHeightEstimate = 200f;

    /// <summary>Estimate a float's rendered height in CSS px.</summary>
    internal static float EstimateFloatHeight(
        DomTree tree,
        NodeId floatId,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (styles.TryGetValue(floatId, out LayoutStyle? floatStyle)
            && floatStyle.Height.Kind == DimensionKind.Px)
        {
            return floatStyle.Height.Value;
        }

        float imageHeight = 0f;
        foreach (NodeId id in tree.Descendants(floatId))
        {
            if (!DomTraversal.IsLocal(tree, id, "img"))
            {
                continue;
            }

            if (styles.TryGetValue(id, out LayoutStyle? imageStyle)
                && imageStyle.Height.Kind == DimensionKind.Px)
            {
                imageHeight += imageStyle.Height.Value;
            }
        }

        const float assumedFloatWidth = 280f;
        float textHeight = EstimateTextHeight(tree, floatId, styles, assumedFloatWidth);

        // Flattened character count misses forced rows; add one line per structural row.
        float structuralHeight = 0f;
        foreach (NodeId id in tree.Descendants(floatId))
        {
            if (!DomTraversal.IsAnyLocal(
                    tree, id, "li", "tr", "dt", "dd", "p", "figcaption", "h1", "h2", "h3", "h4", "h5", "h6"))
            {
                continue;
            }

            float fontSize = styles.TryGetValue(id, out LayoutStyle? rowStyle) && rowStyle.FontSize is { } size
                ? size
                : 16f;
            structuralHeight += fontSize * 1.2f;
        }

        return F32.Max(imageHeight + textHeight + structuralHeight, DefaultFloatHeightEstimate);
    }

    /// <summary>
    /// Estimate how tall a subtree's text would render at <paramref name="assumedWidth"/>,
    /// using the same average-character-width heuristic as the layout-only text fallback.
    /// </summary>
    internal static float EstimateTextHeight(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float assumedWidth)
    {
        float charCount = 0f;
        foreach (char c in tree.TextContent(id))
        {
            if (!char.IsWhiteSpace(c))
            {
                charCount++;
            }
        }

        if (charCount == 0f)
        {
            return 0f;
        }

        float fontSize = styles.TryGetValue(id, out LayoutStyle? style) && style.FontSize is { } size
            ? size
            : 16f;
        const float avgCharWidthEm = 0.55f;
        float charsPerLine = F32.Max(assumedWidth / (fontSize * avgCharWidthEm), 1f);
        float lines = F32.Max(MathF.Ceiling(charCount / charsPerLine), 1f);
        return (lines * fontSize * 1.2f) + 16f;
    }

    /// <summary>Estimate the normal-flow height consumed by one sibling alongside a float.</summary>
    internal static float EstimateFlowSiblingHeight(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float assumedWidth)
    {
        styles.TryGetValue(id, out LayoutStyle? style);
        if (style is not null
            && (style.Display == Display.None || style.Position == TaffyPosition.Absolute))
        {
            return 0f;
        }

        float explicitHeight = style is not null && style.Height.Kind == DimensionKind.Px
            ? F32.Max(style.Height.Value, 0f)
            : 0f;
        float descendantImageHeight = 0f;
        foreach (NodeId descendant in tree.Descendants(id))
        {
            if (!DomTraversal.IsLocal(tree, descendant, "img"))
            {
                continue;
            }

            if (styles.TryGetValue(descendant, out LayoutStyle? imageStyle)
                && imageStyle.Height.Kind == DimensionKind.Px)
            {
                descendantImageHeight += F32.Max(imageStyle.Height.Value, 0f);
            }
        }

        float ownImageHeight = DomTraversal.IsLocal(tree, id, "img") ? explicitHeight : 0f;
        float contentHeight = F32.Max(
            F32.Max(EstimateTextHeight(tree, id, styles, assumedWidth), explicitHeight),
            descendantImageHeight + ownImageHeight);
        float margins = style is not null
            ? F32.Max(style.Margin.Top + style.Margin.Bottom, 0f)
            : 0f;
        return contentHeight + margins;
    }

    internal static bool IsStructuralNativeClearBox(DomTree tree, NodeId id, LayoutStyle style) =>
        style.Display == Display.Block
        && !style.DisplayContents
        && !style.IsTableBox
        && !style.IsInlineBlock
        && !style.FlowRoot
        && !style.OverflowHidden
        && DomStyleFixups.EffectiveContainerType(style) == ContainerType.Normal
        && style.Float is null
        && style.Clear is not null
        && style.Position != TaffyPosition.Absolute
        && tree.TextContent(id).Trim().Length == 0
        && style.BeforePseudo is null
        && style.AfterPseudo is null;

    internal static bool IsStructuralNativeFloatPseudo(LayoutStyle style) =>
        style.Display != Display.None
        && style.Display != Display.Inline
        && !style.DisplayContents
        && !style.IsInlineBlock
        && (!style.FlowRoot || style.IsTableBox)
        && !style.OverflowHidden
        && DomStyleFixups.EffectiveContainerType(style) == ContainerType.Normal
        && style.Float is null
        && style.Position != TaffyPosition.Absolute
        && (style.BeforeContent is null || style.BeforeContent.Trim().Length == 0);

    /// <summary>
    /// Native taffy floats are currently sound for a deliberately small structural subset.
    /// </summary>
    internal static bool CanUseNativeFloatBand(
        DomTree tree,
        LayoutStyle parentStyle,
        IReadOnlyList<NodeId> domChildren,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (parentStyle.Display != Display.Block
            || parentStyle.InternalFlexContainer
            || parentStyle.IsInlineBlock
            || parentStyle.IsTableBox
            || parentStyle.Position == TaffyPosition.Absolute
            || parentStyle.Width.Kind is not (DimensionKind.Auto or DimensionKind.Px or DimensionKind.Percent)
            || parentStyle.SizeExpressions[0] is not null)
        {
            return false;
        }

        bool hasLeft = false;
        bool hasRight = false;
        bool sawFloat = false;
        bool sawClearAfterFloats = false;
        bool sawFlowAfterFloats = false;

        if (parentStyle.BeforePseudo is { } before && !IsStructuralNativeFloatPseudo(before))
        {
            return false;
        }

        foreach (NodeId id in domChildren)
        {
            if (tree.GetNode(id) is not { } node)
            {
                continue;
            }

            if (!node.IsElement)
            {
                // Comments, doctypes, and processing instructions generate no formatting box.
                if (node.TextContentOfTextNode is { } contents && contents.Trim().Length != 0)
                {
                    return false;
                }

                continue;
            }

            if (!styles.TryGetValue(id, out LayoutStyle? style))
            {
                return false;
            }

            if (style.Display == Display.None)
            {
                continue;
            }

            if (style.Float is { } side)
            {
                if (sawClearAfterFloats
                    || sawFlowAfterFloats
                    || style.DisplayContents
                    || style.IsTableBox
                    || style.Position == TaffyPosition.Absolute
                    || style.Width.Kind is not (DimensionKind.Auto or DimensionKind.Px or DimensionKind.Percent)
                    || style.SizeExpressions[0] is not null
                    || DomStyleFixups.HasDeferredOrAutoMargin(style))
                {
                    return false;
                }

                sawFloat = true;
                hasLeft |= side == Float.Left;
                hasRight |= side == Float.Right;
            }
            else if (IsStructuralNativeClearBox(tree, id, style))
            {
                if (!sawFloat)
                {
                    return false;
                }

                sawClearAfterFloats |= DomStyleFixups.ClearMatchesFloatSides(
                    style.Clear!.Value, hasLeft, hasRight);
            }
            else if (sawFloat
                && style.Display == Display.Block
                && !style.DisplayContents
                && !style.IsInlineBlock
                && !style.IsTableBox
                && !style.InternalFlexContainer
                && style.Position != TaffyPosition.Absolute)
            {
                // Gecko keeps an ordinary block's border box at the BFC's full inline size and
                // narrows only its descendant line boxes.
                sawFlowAfterFloats = true;
            }
            else
            {
                return false;
            }
        }

        if (parentStyle.AfterPseudo is { } after)
        {
            if (!IsStructuralNativeFloatPseudo(after))
            {
                return false;
            }

            if (after.Clear is { } clear)
            {
                sawClearAfterFloats |= DomStyleFixups.ClearMatchesFloatSides(clear, hasLeft, hasRight);
            }
        }

        // Taffy represents scroll-container overflow as a real BFC root. Plain `clip` does not
        // establish a BFC, and viewport-propagated overflow leaves its source box visible.
        bool parentIsNativeBfc = parentStyle.OverflowScrollContainer
            && !parentStyle.OverflowPropagatedToViewport;
        return sawFloat && (sawFlowAfterFloats || sawClearAfterFloats || parentIsNativeBfc);
    }
}
