// Port of the native form-control sizing pass and the table used-width pass inside
// `layout_dom_once` in crates/obscura-render/src/dom.rs.
using System.Globalization;
using Obscura.Dom;
using TaffyAvailableSpace = Obscura.Render.Layout.AvailableSpace;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyGridPlacementKind = Obscura.Render.Layout.GridPlacementKind;
using TaffyGridTemplateComponent = Obscura.Render.Layout.GridTemplateComponent;
using TaffyMaxTrack = Obscura.Render.Layout.MaxTrackSizingFunction;
using TaffyMinTrack = Obscura.Render.Layout.MinTrackSizingFunction;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyStyle = Obscura.Render.Layout.Style;
using TaffyTrackSizingFunction = Obscura.Render.Layout.TrackSizingFunction;
using TaffyTree = Obscura.Render.Layout.TaffyTree<int?>;

namespace Obscura.Render;

public static partial class RenderDom
{
    /// <summary>
    /// Resolve native form-control intrinsic border-box geometry after inheritance and author
    /// cascading.
    /// </summary>
    private static void ApplyNativeControlSizes(DomTree tree, Dictionary<NodeId, LayoutStyle> styles)
    {
        Dictionary<NodeId, NativeButtonIntrinsicContent> nativeButtonContents = [];
        foreach ((NodeId id, LayoutStyle style) in styles)
        {
            if (!DomTraversal.IsLocal(tree, id, "button")
                || !style.Width.IsAuto
                || DomStyleFixups.ContainerAutoInlineSizeOf(tree, id, style, styles)
                    == ContainerAutoInlineSize.StretchedGridItem)
            {
                continue;
            }

            float fontSize = F32.Max(style.FontSize ?? 13.333333f, 1f);
            nativeButtonContents[id] =
                DomStyleFixups.NativeButtonIntrinsicContent(tree, id, styles, fontSize);
        }

        Dictionary<NodeId, (bool Inline, bool Block)> nativeControlGridStretch = [];
        foreach ((NodeId id, LayoutStyle style) in styles)
        {
            if (!DomTraversal.IsAnyLocal(tree, id, "input", "select", "textarea"))
            {
                continue;
            }

            nativeControlGridStretch[id] = (
                DomStyleFixups.ContainerAutoInlineSizeOf(tree, id, style, styles)
                    == ContainerAutoInlineSize.StretchedGridItem,
                DomStyleFixups.ContainerAutoBlockSizeOf(tree, id, style, styles)
                    == ContainerAutoBlockSize.StretchedGridItem);
        }

        foreach ((NodeId id, LayoutStyle style) in styles)
        {
            if (tree.GetNode(id) is not { } node || node.AsElement() is not { } element)
            {
                continue;
            }

            string local = element.Name.Local;
            (bool Inline, bool Block) stretch =
                nativeControlGridStretch.TryGetValue(id, out var found) ? found : (false, false);

            if (string.Equals(local, "button", StringComparison.Ordinal)
                && style.Width.IsAuto
                && nativeButtonContents.TryGetValue(id, out NativeButtonIntrinsicContent? content))
            {
                // Buttons remain intrinsically sized form controls even when author CSS
                // changes their inner display to flex/grid.
                float fontSize = F32.Max(style.FontSize ?? 13.333333f, 1f);
                bool bold = ComputedStyle.UsedFontWeight(style) >= 600;
                string label = string.Join(
                    ' ',
                    content.Text.ToString().Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries));
                float contentWidth = DomTextMeasure.TextWidth(
                    label, fontSize, bold, style.FontFamily, style.LetterSpacing ?? 0f);
                contentWidth += content.AtomicWidth;

                float PseudoWidth(LayoutStyle? pseudo)
                {
                    if (pseudo is null)
                    {
                        return 0f;
                    }

                    float pseudoContent = pseudo.Width.Kind switch
                    {
                        DimensionKind.Px => F32.Max(pseudo.Width.Value, 0f),
                        DimensionKind.Em or DimensionKind.Rem =>
                            F32.Max(pseudo.Width.Value * fontSize, 0f),
                        DimensionKind.Percent => F32.Max(pseudo.Width.Value * fontSize, 0f),
                        _ => pseudo.MaskImage is not null || pseudo.BackgroundImage is not null
                            ? fontSize
                            : 0f,
                    };
                    float horizontal = pseudo.Padding.Left
                        + pseudo.Padding.Right
                        + pseudo.Border.Left
                        + pseudo.Border.Right;
                    float borderBox = pseudo.BoxSizing == BoxSizing.ContentBox
                        ? pseudoContent + horizontal
                        : F32.Max(pseudoContent, horizontal);
                    return borderBox
                        + F32.Max(pseudo.Margin.Left, 0f)
                        + F32.Max(pseudo.Margin.Right, 0f);
                }

                float before = PseudoWidth(style.BeforePseudo);
                float after = PseudoWidth(style.AfterPseudo);
                int extraParts = (before > 0f ? 1 : 0) + (after > 0f ? 1 : 0);
                contentWidth += before + after;
                if (extraParts > 0 && label.Length != 0)
                {
                    contentWidth += (style.ColumnGap ?? 0f) * extraParts;
                }

                float horizontalEdges = style.Padding.Left
                    + style.Padding.Right
                    + style.Border.Left
                    + style.Border.Right;
                style.Width = Dimension.Px(style.BoxSizing == BoxSizing.ContentBox
                    ? contentWidth
                    : contentWidth + horizontalEdges);
            }

            if (string.Equals(local, "select", StringComparison.Ordinal))
            {
                float fontSize = F32.Max(style.FontSize ?? 13.333333f, 1f);
                bool bold = ComputedStyle.UsedFontWeight(style) >= 600;
                float labelWidth = 0f;
                foreach (NodeId optionId in tree.Descendants(id))
                {
                    if (!DomTraversal.IsLocal(tree, optionId, "option"))
                    {
                        continue;
                    }

                    string label = tree.TextContent(optionId).Trim();
                    labelWidth = F32.Max(
                        labelWidth,
                        DomTextMeasure.TextWidth(
                            label, fontSize, bold, style.FontFamily, style.LetterSpacing ?? 0f));
                }

                float horizontalEdges = style.Padding.Left
                    + style.Padding.Right
                    + style.Border.Left
                    + style.Border.Right;
                float verticalEdges = style.Padding.Top
                    + style.Padding.Bottom
                    + style.Border.Top
                    + style.Border.Bottom;
                float rows = 1f;
                if (node.GetAttribute("size") is { } sizeAttribute
                    && int.TryParse(
                        sizeAttribute,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsedRows)
                    && parsedRows > 1)
                {
                    rows = parsedRows;
                }

                float intrinsicWidth = labelWidth + horizontalEdges;
                float intrinsicHeight =
                    (F32.Max(FontResolution.UsedLineHeight(style), 1f) * rows) + verticalEdges;
                DomStyleFixups.AssignNativeControlSize(
                    style, stretch, intrinsicWidth, intrinsicHeight, horizontalEdges, verticalEdges);
                continue;
            }

            if (string.Equals(local, "textarea", StringComparison.Ordinal))
            {
                // A native textarea's intrinsic border box comes from the rows/cols content
                // attributes (HTML defaults 2 and 20), not from its text content.
                float rows = ParsePositiveInt(node.GetAttribute("rows")) ?? 2;
                float cols = ParsePositiveInt(node.GetAttribute("cols")) ?? 20;
                float fontSize = F32.Max(style.FontSize ?? 13.333333f, 1f);
                float horizontalEdges = style.Padding.Left
                    + style.Padding.Right
                    + style.Border.Left
                    + style.Border.Right;
                float verticalEdges = style.Padding.Top
                    + style.Padding.Bottom
                    + style.Border.Top
                    + style.Border.Bottom;
                float intrinsicWidth = (cols * fontSize * 0.6075f) + horizontalEdges;
                float intrinsicHeight =
                    (F32.Max(FontResolution.UsedLineHeight(style), 1f) * rows) + verticalEdges;
                DomStyleFixups.AssignNativeControlSize(
                    style, stretch, intrinsicWidth, intrinsicHeight, horizontalEdges, verticalEdges);
                continue;
            }

            if (!string.Equals(local, "input", StringComparison.Ordinal))
            {
                continue;
            }

            string inputType = (node.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();
            if (string.Equals(inputType, "hidden", StringComparison.Ordinal))
            {
                style.Display = Display.None;
                continue;
            }

            float inputFontSize = F32.Max(style.FontSize ?? 13.333333f, 1f);
            float inputHorizontalEdges = style.Padding.Left
                + style.Padding.Right
                + style.Border.Left
                + style.Border.Right;
            float inputVerticalEdges = style.Padding.Top
                + style.Padding.Bottom
                + style.Border.Top
                + style.Border.Bottom;
            float defaultHeight =
                F32.Max(FontResolution.UsedLineHeight(style), 1f) + inputVerticalEdges;

            (float Width, float Height) intrinsic;
            switch (inputType)
            {
                case "checkbox":
                case "radio":
                    intrinsic = (13f, 13f);
                    break;
                case "range":
                    intrinsic = (129f, 16f);
                    break;
                case "color":
                    intrinsic = (50f, 27f);
                    break;
                case "file":
                    intrinsic = (253f, F32.Max(defaultHeight, 22f));
                    break;
                case "submit":
                case "reset":
                case "button":
                {
                    string fallback = inputType switch
                    {
                        "reset" => "Reset",
                        "button" => string.Empty,
                        _ => "Submit Query",
                    };
                    string label = node.GetAttribute("value") ?? fallback;
                    // Rust counts scalar values (`chars().count()`), not UTF-16 code units.
                    int labelChars = 0;
                    foreach (System.Text.Rune _ in label.EnumerateRunes())
                    {
                        labelChars++;
                    }

                    intrinsic = (
                        F32.Max(
                            (labelChars * inputFontSize * 0.55f)
                            + (inputFontSize * 1.5f)
                            + inputHorizontalEdges,
                            20f),
                        defaultHeight);
                    break;
                }

                case "image":
                    intrinsic = (inputHorizontalEdges, inputVerticalEdges);
                    break;
                default:
                {
                    float size = ParsePositiveInt(node.GetAttribute("size")) ?? 20;
                    intrinsic = (
                        (size * inputFontSize * 0.6f) + (inputFontSize * 0.675f) + inputHorizontalEdges,
                        defaultHeight);
                    break;
                }
            }

            DomStyleFixups.AssignNativeControlSize(
                style,
                stretch,
                intrinsic.Width,
                intrinsic.Height,
                inputHorizontalEdges,
                inputVerticalEdges);
        }
    }

    private static int? ParsePositiveInt(string? value) =>
        value is not null
        && int.TryParse(
            value.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed)
        && parsed > 0
            ? parsed
            : null;

    /// <summary>
    /// Table used-width pass: choose each table's used width the way CSS does, then distribute
    /// the used track space proportionally between each column's min-content and max-content.
    /// </summary>
    private static void ApplyTableUsedWidths(
        DomTree tree,
        TaffyTree taffyTree,
        TaffyNodeId taffyRoot,
        IReadOnlyDictionary<TaffyNodeId, NodeId> idMap,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IfcRegistry ifcItems,
        float initialCbWidth,
        Layout.Size<TaffyAvailableSpace> available,
        Layout.TreeMeasureFunction<int?> measure)
    {
        List<(TaffyNodeId Taffy, NodeId Dom, int Depth)> tables = [];
        foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
        {
            if (ifcItems.TableRows.ContainsKey(taffyId))
            {
                tables.Add((taffyId, domId, DomTableSupport.TableAncestorDepth(tree, domId, styles)));
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        // Outer tables consume their nested tables' intrinsic sizes.
        tables = [.. tables.OrderBy(entry => entry.Depth)];
        int tableIndex = 0;
        while (tableIndex < tables.Count)
        {
            int depth = tables[tableIndex].Depth;
            int groupEnd = tableIndex + 1;
            while (groupEnd < tables.Count && tables[groupEnd].Depth == depth)
            {
                groupEnd++;
            }

            List<(TaffyNodeId Taffy, NodeId Dom, int Depth)> group =
                tables.GetRange(tableIndex, groupEnd - tableIndex);
            Dictionary<TaffyNodeId, float> availableWidths = [];
            bool needsLayoutSnapshot = false;
            foreach ((_, NodeId dom, _) in group)
            {
                if (!styles.TryGetValue(dom, out LayoutStyle? style))
                {
                    continue;
                }

                bool autoNeedsLayout = style.Width.IsAuto
                    && (depth > 0
                        || DomTableSupport.ReliableTableAvailableWidth(
                            tree, dom, styles, initialCbWidth) is null);
                bool fixedPercentNeedsLayout = style.TableLayoutFixed
                    && style.Width.Kind == DimensionKind.Percent;
                if (autoNeedsLayout || fixedPercentNeedsLayout)
                {
                    needsLayoutSnapshot = true;
                    break;
                }
            }

            if (needsLayoutSnapshot)
            {
                // This pass is gated to layout-dependent containing blocks and nested auto
                // tables.
                taffyTree.ComputeLayoutWithMeasure(taffyRoot, available, measure);
                foreach ((TaffyNodeId tnode, NodeId dom, _) in group)
                {
                    if (styles.TryGetValue(dom, out LayoutStyle? style)
                        && (style.Width.IsAuto
                            || (style.TableLayoutFixed
                                && style.Width.Kind == DimensionKind.Percent)))
                    {
                        availableWidths[tnode] = F32.Max(taffyTree.GetLayout(tnode).Size.Width, 0f);
                    }
                }
            }

            foreach ((TaffyNodeId tnode, NodeId dom, _) in group)
            {
                if (!styles.TryGetValue(dom, out LayoutStyle? tableStyle))
                {
                    continue;
                }

                if (ifcItems.FixedTableCols.TryGetValue(tnode, out List<FixedTableColumn>? columns))
                {
                    int fixedNcols = columns.Count;
                    if (fixedNcols == 0)
                    {
                        continue;
                    }

                    float inlineOuterEdges = DomStyleFixups.TableInlineOuterEdges(tableStyle);
                    (float horizontalSpacing, _) = DomStyleFixups.TableSpacing(tableStyle);
                    float usedOuterFixed;
                    switch (tableStyle.Width.Kind)
                    {
                        case DimensionKind.Px when tableStyle.BoxSizing == BoxSizing.ContentBox:
                            // Border spacing lives inside a CSS table's content box.
                            usedOuterFixed = F32.Max(tableStyle.Width.Value, 0f)
                                + F32.Max(inlineOuterEdges - (horizontalSpacing * 2f), 0f);
                            break;
                        case DimensionKind.Px:
                            usedOuterFixed = F32.Max(tableStyle.Width.Value, 0f);
                            break;
                        case DimensionKind.Percent:
                            usedOuterFixed = availableWidths.TryGetValue(tnode, out float measured)
                                ? measured
                                : DomTableSupport.ReliableTableAvailableWidth(
                                    tree, dom, styles, initialCbWidth) ?? initialCbWidth;
                            break;
                        default:
                            continue;
                    }

                    float interiorSpacingFixed = horizontalSpacing * Math.Max(fixedNcols - 1, 0);
                    float targetFixed = F32.Max(
                        usedOuterFixed - inlineOuterEdges - interiorSpacingFixed,
                        0f);
                    List<float> fixedWidths =
                        DomTableSupport.DistributeFixedTableColumns(targetFixed, columns);
                    float requiredOuter = inlineOuterEdges + interiorSpacingFixed;
                    foreach (float width in fixedWidths)
                    {
                        requiredOuter += width;
                    }

                    usedOuterFixed = F32.Max(usedOuterFixed, requiredOuter);
                    float usedDeclarationFixed = tableStyle.BoxSizing == BoxSizing.ContentBox
                        ? F32.Max(usedOuterFixed - inlineOuterEdges, 0f)
                        : usedOuterFixed;
                    TaffyStyle fixedTableStyle = taffyTree.GetStyle(tnode).Clone();
                    Layout.Size<TaffyDimension> fixedSize = fixedTableStyle.Size;
                    fixedSize.Width = TaffyDimension.FromLength(usedDeclarationFixed);
                    fixedTableStyle.Size = fixedSize;
                    fixedTableStyle.GridTemplateColumns = DomStyleFixups.FixedGridTracks(fixedWidths);
                    taffyTree.SetStyle(tnode, fixedTableStyle);
                    continue;
                }

                // A percentage-width table resolves against its container, so leave taffy's
                // percentage handling in place.
                Dimension widthStyle = tableStyle.Width;
                if (widthStyle.Kind == DimensionKind.Percent)
                {
                    continue;
                }

                taffyTree.ComputeLayoutWithMeasure(
                    tnode,
                    new Layout.Size<TaffyAvailableSpace>(
                        TaffyAvailableSpace.MinContent,
                        TaffyAvailableSpace.MaxContent),
                    measure);
                float minC = taffyTree.GetLayout(tnode).Size.Width;

                // A table can never be narrower than an unshrinkable fixed-width descendant.
                minC = F32.Max(
                    minC,
                    DomTableSupport.MaxDefiniteTableContentWidth(tree, dom, styles) ?? 0f);

                taffyTree.ComputeLayoutWithMeasure(
                    tnode,
                    new Layout.Size<TaffyAvailableSpace>(
                        TaffyAvailableSpace.MaxContent,
                        TaffyAvailableSpace.MaxContent),
                    measure);
                float maxC = taffyTree.GetLayout(tnode).Size.Width;

                float inlineEdges = DomStyleFixups.TableInlineOuterEdges(tableStyle);
                float preferredOuter = widthStyle.Kind switch
                {
                    DimensionKind.Px when tableStyle.BoxSizing == BoxSizing.ContentBox =>
                        widthStyle.Value + inlineEdges,
                    DimensionKind.Px => widthStyle.Value,
                    _ => maxC,
                };
                float availableOuter = availableWidths.TryGetValue(tnode, out float snapshot)
                    ? snapshot
                    : DomTableSupport.ReliableTableAvailableWidth(tree, dom, styles, initialCbWidth)
                        ?? initialCbWidth;
                float usedOuter = widthStyle.Kind == DimensionKind.Px
                    // A definite table width is not clamped to its containing block.
                    ? F32.Max(preferredOuter, minC)
                    : F32.Min(F32.Max(preferredOuter, minC), F32.Max(availableOuter, minC));
                float usedDeclaration = tableStyle.BoxSizing == BoxSizing.ContentBox
                    ? F32.Max(usedOuter - inlineEdges, 0f)
                    : usedOuter;

                // Distribute the used track space proportionally between each column's own
                // min-content and max-content width.
                List<(TaffyNodeId Cell, int Column, int Span)> cells = [];
                foreach (TaffyNodeId cell in taffyTree.Children(tnode))
                {
                    TaffyStyle cellStyle = taffyTree.GetStyle(cell);
                    if (cellStyle.GridColumn.Start.Kind != TaffyGridPlacementKind.Line)
                    {
                        continue;
                    }

                    int col = Math.Max(cellStyle.GridColumn.Start.LineIndex, (short)1) - 1;
                    int span = cellStyle.GridColumn.End.Kind == TaffyGridPlacementKind.Span
                        ? Math.Max(cellStyle.GridColumn.End.SpanCount, (ushort)1)
                        : 1;
                    cells.Add((cell, col, span));
                }

                int ncols = taffyTree.GetStyle(tnode).GridTemplateColumns.Count;

                // Bound the extra per-cell measurement work.
                if (ncols > 0 && cells.Count <= 4096)
                {
                    List<(int Column, int Span, float Min, float Max)> measured = new(cells.Count);
                    foreach ((TaffyNodeId cell, int col, int span) in cells)
                    {
                        taffyTree.ComputeLayoutWithMeasure(
                            cell,
                            new Layout.Size<TaffyAvailableSpace>(
                                TaffyAvailableSpace.MinContent,
                                TaffyAvailableSpace.MaxContent),
                            measure);
                        float cmin = taffyTree.GetLayout(cell).Size.Width;
                        taffyTree.ComputeLayoutWithMeasure(
                            cell,
                            new Layout.Size<TaffyAvailableSpace>(
                                TaffyAvailableSpace.MaxContent,
                                TaffyAvailableSpace.MaxContent),
                            measure);
                        float cmax = taffyTree.GetLayout(cell).Size.Width;
                        measured.Add((col, span, cmin, F32.Max(cmax, cmin)));
                    }

                    float[] colMin = new float[ncols];
                    float[] colMax = new float[ncols];

                    // Pass 1: single-column cells set each column's floor.
                    foreach ((int col, int span, float cmin, float cmax) in measured)
                    {
                        if (span == 1 && col < ncols)
                        {
                            colMin[col] = F32.Max(colMin[col], cmin);
                            colMax[col] = F32.Max(colMax[col], cmax);
                        }
                    }

                    // Pass 2: a spanning cell that needs more than its columns currently give
                    // grows them, splitting the shortfall evenly.
                    foreach ((int col, int span, float cmin, float cmax) in measured)
                    {
                        if (span <= 1 || col >= ncols)
                        {
                            continue;
                        }

                        int end = Math.Min(col + span, ncols);
                        float n = end - col;
                        float curMin = 0f;
                        for (int index = col; index < end; index++)
                        {
                            curMin += colMin[index];
                        }

                        if (cmin > curMin)
                        {
                            float add = (cmin - curMin) / n;
                            for (int index = col; index < end; index++)
                            {
                                colMin[index] += add;
                            }
                        }

                        float curMax = 0f;
                        for (int index = col; index < end; index++)
                        {
                            curMax += colMax[index];
                        }

                        if (cmax > curMax)
                        {
                            float add = (cmax - curMax) / n;
                            for (int index = col; index < end; index++)
                            {
                                colMax[index] += add;
                            }
                        }
                    }

                    for (int j = 0; j < ncols; j++)
                    {
                        if (colMax[j] < colMin[j])
                        {
                            colMax[j] = colMin[j];
                        }
                    }

                    (float horizontalSpacing, _) = DomStyleFixups.TableSpacing(tableStyle);
                    float interiorSpacing = horizontalSpacing * Math.Max(ncols - 1, 0);

                    // A specified length on a cell/column is a preferred contribution, not a
                    // min-content floor.
                    ifcItems.TableCols.TryGetValue(tnode, out var specifiedColumns);
                    List<float?> fixedWidths = new(ncols);
                    List<float?> percentages = new(ncols);
                    for (int index = 0; index < ncols; index++)
                    {
                        fixedWidths.Add(
                            specifiedColumns.Px is { } px && index < px.Count ? px[index] : null);
                        percentages.Add(
                            specifiedColumns.Percent is { } pct && index < pct.Count
                                ? pct[index]
                                : null);
                    }

                    for (int j = 0; j < ncols; j++)
                    {
                        if (fixedWidths[j] is { } width)
                        {
                            colMax[j] = F32.Max(width, colMin[j]);
                        }
                    }

                    if (widthStyle.Kind != DimensionKind.Px)
                    {
                        float percentageFloor = DomTableSupport.AutoTablePercentageIntrinsicFloor(
                            colMin, percentages);
                        float percentageOuter = percentageFloor + inlineEdges + interiorSpacing;
                        usedOuter = F32.Min(
                            F32.Max(usedOuter, percentageOuter),
                            F32.Max(availableOuter, minC));
                        usedDeclaration = tableStyle.BoxSizing == BoxSizing.ContentBox
                            ? F32.Max(usedOuter - inlineEdges, 0f)
                            : usedOuter;
                    }

                    float target = F32.Max(usedOuter - inlineEdges - interiorSpacing, 0f);
                    List<float> widths = DomTableSupport.DistributeAutoTableColumns(
                        target, colMin, colMax, fixedWidths, percentages);
                    TaffyStyle autoTableStyle = taffyTree.GetStyle(tnode).Clone();
                    Layout.Size<TaffyDimension> autoSize = autoTableStyle.Size;
                    autoSize.Width = TaffyDimension.FromLength(usedDeclaration);
                    autoTableStyle.Size = autoSize;
                    autoTableStyle.GridTemplateColumns = DomStyleFixups.FixedGridTracks(widths);
                    taffyTree.SetStyle(tnode, autoTableStyle);
                }
                else
                {
                    TaffyStyle plainTableStyle = taffyTree.GetStyle(tnode).Clone();
                    Layout.Size<TaffyDimension> plainSize = plainTableStyle.Size;
                    plainSize.Width = TaffyDimension.FromLength(usedDeclaration);
                    plainTableStyle.Size = plainSize;
                    taffyTree.SetStyle(tnode, plainTableStyle);
                }
            }

            tableIndex = groupEnd;
        }
    }
}
