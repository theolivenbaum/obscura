// Port of the native form-control sizing pass and the table used-width pass inside
// `layout_dom_once` in crates/obscura-render/src/dom.rs.
using System.Globalization;
using PocketCalculator.Dom;
using TaffyAvailableSpace = PocketCalculator.Render.Layout.AvailableSpace;
using TaffyDimension = PocketCalculator.Render.Layout.Dimension;
using TaffyGridPlacementKind = PocketCalculator.Render.Layout.GridPlacementKind;
using TaffyGridTemplateComponent = PocketCalculator.Render.Layout.GridTemplateComponent;
using TaffyMaxTrack = PocketCalculator.Render.Layout.MaxTrackSizingFunction;
using TaffyMinTrack = PocketCalculator.Render.Layout.MinTrackSizingFunction;
using TaffyNodeId = PocketCalculator.Render.Layout.NodeId;
using TaffyStyle = PocketCalculator.Render.Layout.Style;
using TaffyTrackSizingFunction = PocketCalculator.Render.Layout.TrackSizingFunction;
using TaffyTree = PocketCalculator.Render.Layout.TaffyTree<int?>;

namespace PocketCalculator.Render;

public static partial class RenderDom
{
    /// <summary>
    /// Resolve native form-control intrinsic border-box geometry after inheritance and author
    /// cascading.
    /// </summary>
    private static void ApplyNativeControlSizes(
        DomTree tree,
        Dictionary<NodeId, LayoutStyle> styles,
        TextEngine engine)
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
                DomStyleFixups.NativeButtonIntrinsicContent(tree, id, styles, fontSize, engine);
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
                string label = DomStyleFixups.NormalizeControlLabel(content.Text.ToString());
                // Shaped through the inline engine, not TextWidth: the label is laid out
                // by that engine, so sizing the box with a different metric leaves the
                // text too wide for the box it just produced. <select> deliberately keeps
                // TextWidth, because paint synthesises its label with the same
                // height-based scale.
                float contentWidth = engine.MeasureControlLabel(label, style);
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

                // DEVIATION from crates/obscura-render/src/dom.rs, which stores the summed
                // width as-is. Taffy rounds used boxes to whole pixels (Sys.Round), so a box
                // measured at exactly its label's width can round down and wrap the label it
                // was sized for. The parts are also measured separately (label, icon glyph,
                // child edges) and each carries its own sub-pixel error. Round the intrinsic
                // content width up: at most one pixel wide, never a pixel short. Tesserae's
                // "Section Stack" button measured 143px against a 79.5px label and broke it
                // over two lines. See "Known deviations" in todo.md.
                contentWidth = MathF.Ceiling(contentWidth);
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
                // DEVIATION from crates/obscura-render/src/dom.rs, which uses a fixed
                // `cols * fontSize * 0.6075`. Chromium sizes the box from the face's own average
                // character width and then reserves the textarea's scrollbar gutter, which is the
                // whole of the constant term. See "Known deviations" in todo.md.
                (float charWidth, _) = engine.ControlCharacterMetrics(style);
                float intrinsicWidth =
                    MathF.Ceiling(cols * charWidth) + TextareaScrollbarWidth + horizontalEdges;
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

                // DEVIATION from crates/obscura-render/src/dom.rs, which sizes a date/time
                // control from the `size` attribute like a text field and so makes it both too
                // wide and, in a shrink-to-fit container, zero. Chromium fills it with read-only
                // sub-fields (`mm/dd/yyyy` and friends) plus a picker indicator, and that content
                // is what it is sized from. See "Known deviations" in todo.md.
                case "date":
                case "datetime-local":
                case "month":
                case "week":
                case "time":
                {
                    float fieldWidth = MathF.Ceiling(
                        PaintNativeControls.DateFamilyIntrinsicContentWidth(inputType, style, engine));
                    intrinsic = (fieldWidth + inputHorizontalEdges, defaultHeight);

                    // A percentage width resolves against a containing block that does not exist
                    // yet while intrinsic sizes are computed, so the control would contribute
                    // nothing to a shrink-to-fit ancestor and collapse it. Chromium has the
                    // shadow content to contribute instead; this engine has no box for it, so
                    // the intrinsic width is published as a minimum. It differs from Chromium
                    // only where such a control is squeezed below its own content width.
                    if (style.Width.Kind == DimensionKind.Percent && style.MinWidth.IsAuto)
                    {
                        style.MinWidth = Dimension.Px(style.BoxSizing == BoxSizing.ContentBox
                            ? fieldWidth
                            : fieldWidth + inputHorizontalEdges);
                    }

                    break;
                }

                default:
                {
                    float size = ParsePositiveInt(node.GetAttribute("size")) ?? 20;
                    intrinsic = (
                        SizeBasedContentWidth(engine, style, size) + inputHorizontalEdges,
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

    /// <summary>Chromium's textarea scrollbar gutter, the whole of its intrinsic constant term.</summary>
    private const float TextareaScrollbarWidth = 15f;

    /// <summary>
    /// The content width a text control's <c>size</c> attribute asks for, the way Chromium
    /// computes it: the face's average character width per column, plus whatever the widest
    /// character box costs over one of them.
    /// </summary>
    internal static float SizeBasedContentWidth(TextEngine engine, LayoutStyle style, float size)
    {
        (float charWidth, float maxCharWidth) = engine.ControlCharacterMetrics(style);
        float extra = maxCharWidth > charWidth ? maxCharWidth - charWidth : 0f;

        return MathF.Ceiling((charWidth * size) + extra);
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
        IReadOnlyList<DeferredCyclicInlineSize> deferredInlineSizes,
        Layout.TreeMeasureFunction<int?> measure)
    {
        // `DeferCyclicFlexInlineSizes` has already rewritten a cyclic percentage inline size to a
        // definite `0px`, and it runs before the box tree this pass reads. A table whose authored
        // width is such a percentage would therefore be sized here as if it were `width: 0`, so
        // recover what was authored.
        Dictionary<NodeId, float> deferredPercentWidths = [];
        foreach (DeferredCyclicInlineSize entry in deferredInlineSizes)
        {
            if (entry.Slot == 0 && entry.SourceKind == DeferredCyclicInlineSourceKind.Percent)
            {
                deferredPercentWidths[entry.Node] = entry.Percent;
            }
        }

        // An anonymous table box has no DOM node of its own, so its style comes from the
        // registry rather than from `styles`, and every query below that is about the box
        // (its width, its box-sizing, its border-spacing) has to read that one instead.
        HashSet<NodeId> anonymousOwners = [];
        foreach ((_, (NodeId owner, _)) in ifcItems.AnonymousTables)
        {
            anonymousOwners.Add(owner);
        }

        LayoutStyle? TableStyleOf(TaffyNodeId node, NodeId dom) =>
            ifcItems.AnonymousTables.TryGetValue(node, out (NodeId Owner, LayoutStyle Style) anon)
                ? anon.Style
                : styles.TryGetValue(dom, out LayoutStyle? declared) ? declared : null;

        Dictionary<NodeId, TaffyNodeId> taffyByDom = new(idMap.Count);
        List<(TaffyNodeId Taffy, NodeId Dom, int Depth)> tables = [];
        HashSet<TaffyNodeId> tableNodes = [];
        foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
        {
            taffyByDom[domId] = taffyId;
            if (ifcItems.TableRows.ContainsKey(taffyId))
            {
                tables.Add((
                    taffyId,
                    domId,
                    DomTableSupport.TableAncestorDepth(tree, domId, styles, anonymousOwners)));
                tableNodes.Add(taffyId);
            }
        }

        foreach ((TaffyNodeId taffyId, (NodeId owner, _)) in ifcItems.AnonymousTables)
        {
            if (!ifcItems.TableRows.ContainsKey(taffyId))
            {
                continue;
            }

            tables.Add((
                taffyId,
                owner,
                DomTableSupport.TableAncestorDepth(tree, owner, styles, anonymousOwners)));
            tableNodes.Add(taffyId);
        }

        if (tables.Count == 0)
        {
            return;
        }

        DomTableSupport.DefiniteContentWidthIndex definiteContentWidths = new(tree, styles);

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

            // A percentage whose base cannot be established from style alone - a flex or grid
            // parent, where the base is the container but flex-shrink then has its say; an
            // absolutely positioned table; a wrapper that is itself a float or an inline-block -
            // is left for taffy to resolve, and the snapshot reports what it made of it.
            // An anonymous table's containing block is the element that generated it, not that
            // element's own containing block, so the style chain cannot report the space it has;
            // it always takes the snapshot.
            bool WantsSnapshot(TaffyNodeId node, NodeId dom, LayoutStyle style) =>
                ifcItems.AnonymousTables.ContainsKey(node)
                || (style.Width.IsAuto
                    && (depth > 0
                        || DomTableSupport.ReliableTableAvailableWidth(
                            tree, dom, styles, initialCbWidth) is null))
                || (style.Width.Kind == DimensionKind.Percent
                    && DomTableSupport.ReliableTablePercentageBase(
                        tree, dom, styles, initialCbWidth) is null);

            foreach ((TaffyNodeId tnode, NodeId dom, _) in group)
            {
                if (TableStyleOf(tnode, dom) is { } style && WantsSnapshot(tnode, dom, style))
                {
                    needsLayoutSnapshot = true;
                    break;
                }
            }

            if (needsLayoutSnapshot)
            {
                // This pass is gated to layout-dependent containing blocks and nested auto
                // tables.
                //
                // Deviation from crates/obscura-render/src/dom.rs, which lays out with rounding
                // here and in every measurement below: this pass reads unrounded sizes only, and
                // the whole tree is laid out (and rounded) again after it, so the rounding walk
                // was pure cost. It walked the subtree of every measured node, which made a page
                // of nested tables quadratic in its depth.
                taffyTree.ComputeUnroundedLayoutWithMeasure(taffyRoot, available, measure);
                foreach ((TaffyNodeId tnode, NodeId dom, _) in group)
                {
                    if (TableStyleOf(tnode, dom) is { } style && WantsSnapshot(tnode, dom, style))
                    {
                        availableWidths[tnode] =
                            F32.Max(taffyTree.GetUnroundedLayout(tnode).Size.Width, 0f);
                    }
                }
            }

            // Every width below is an intrinsic measurement, and `DeferCyclicFlexInlineSizes`
            // has flattened the cyclic percentages inside these tables to a definite `0px`.
            // Measuring through that reports a table's min-content as the width of whatever is
            // not percentage-sized, which is what let a code-diff table sit at its container's
            // width while every line inside it overflowed. The tables themselves are excluded so
            // the used widths chosen below survive the exit.
            List<(TaffyNodeId Node, TaffyStyle Style)> typedPercentages =
                DomSubgridPasses.EnterTypedPercentageScope(
                    taffyTree, taffyByDom, styles, deferredInlineSizes, tableNodes);

            foreach ((TaffyNodeId tnode, NodeId dom, _) in group)
            {
                if (TableStyleOf(tnode, dom) is not { } tableStyle)
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
                    float declarationEdgesFixed =
                        DomStyleFixups.TableWidthDeclarationEdges(tableStyle);
                    (float horizontalSpacing, _) = DomStyleFixups.TableSpacing(tableStyle);
                    float usedOuterFixed;
                    switch (tableStyle.Width.Kind)
                    {
                        case DimensionKind.Px when tableStyle.BoxSizing == BoxSizing.ContentBox:
                            // Border spacing lives inside a CSS table's content box, and only the
                            // outer half of a collapsed border is outside it.
                            usedOuterFixed = F32.Max(tableStyle.Width.Value, 0f)
                                + declarationEdgesFixed;
                            break;
                        case DimensionKind.Px:
                            usedOuterFixed = F32.Max(tableStyle.Width.Value, 0f);
                            break;
                        case DimensionKind.Percent
                            when DomTableSupport.ReliableTablePercentageBase(
                                tree, dom, styles, initialCbWidth) is { } percentBasis:
                            // Taffy resolves a float's percentage against the width the float
                            // shrank to, so a floated `table-layout: fixed; width: 100%` came out
                            // at 0. Resolve it against the containing block where that is known.
                            usedOuterFixed = tableStyle.BoxSizing == BoxSizing.ContentBox
                                ? (percentBasis * tableStyle.Width.Value) + declarationEdgesFixed
                                : percentBasis * tableStyle.Width.Value;
                            break;
                        case DimensionKind.Percent:
                            usedOuterFixed = availableWidths.TryGetValue(tnode, out float measured)
                                ? measured
                                : initialCbWidth;
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
                Dimension widthStyle =
                    !ifcItems.AnonymousTables.ContainsKey(tnode)
                    && deferredPercentWidths.TryGetValue(dom, out float authored)
                        ? Dimension.Percent(authored)
                        : tableStyle.Width;
                // A percentage table needs a definite used width here, or the tracks stay `auto`
                // and the grid fills the table by growing them *equally*: a 600px `width: 100%`
                // table of `alpha` / `beta gamma delta` came out 261/339 where Chromium, which
                // distributes what a column does not claim in proportion to max-content, gives
                // 141/459. Two ways to get one, in this order:
                //  - the base the percentage resolves against, when style alone establishes it
                //    (`ReliableTablePercentageBase`); this also covers a float and an
                //    inline-block, whose percentage taffy otherwise resolves against the width
                //    they shrank to - a floated `width: 100%` table came out at max-content;
                //  - otherwise the width taffy already resolved for the table, from the layout
                //    snapshot above. That is the used width, not the base, and it is what a flex
                //    or grid parent needs: `width: 150%` in a 600px flex row is 600 in Chromium,
                //    not 900, because the item is flex-shrunk back.
                float? percentBase = null;
                float? percentUsedOuter = null;
                if (widthStyle.Kind == DimensionKind.Percent)
                {
                    percentBase = DomTableSupport.ReliableTablePercentageBase(
                        tree, dom, styles, initialCbWidth);
                    if (percentBase is null
                        && availableWidths.TryGetValue(tnode, out float percentSnapshot))
                    {
                        percentUsedOuter = percentSnapshot;
                    }
                }

                if (widthStyle.Kind == DimensionKind.Percent
                    && percentBase is null
                    && percentUsedOuter is null)
                {
                    // Deviation from crates/obscura-render/src/dom.rs, which stops here. CSS 2.1
                    // 17.5.2 makes a table's used width the greater of its specified width and
                    // what its columns need, so a percentage resolving narrower than the content
                    // must overflow its container rather than wrap every cell. Floor the box with
                    // the table's min-content width and let taffy resolve the percentage.
                    taffyTree.ComputeUnroundedLayoutWithMeasure(
                        tnode,
                        new Layout.Size<TaffyAvailableSpace>(
                            TaffyAvailableSpace.MinContent,
                            TaffyAvailableSpace.MaxContent),
                        measure);
                    float percentMin = F32.Max(
                        F32.Max(taffyTree.GetUnroundedLayout(tnode).Size.Width, 0f),
                        definiteContentWidths.Get(dom) ?? 0f);
                    if (tableStyle.BoxSizing == BoxSizing.ContentBox)
                    {
                        percentMin = F32.Max(
                            percentMin - DomStyleFixups.TableInlineOuterEdges(tableStyle), 0f);
                    }

                    TaffyStyle percentStyle = taffyTree.GetStyle(tnode);
                    if (percentMin > (percentStyle.MinSize.Width.IntoOption() ?? 0f))
                    {
                        TaffyStyle floored = percentStyle.Clone();
                        Layout.Size<TaffyDimension> percentMinSize = floored.MinSize;
                        percentMinSize.Width = TaffyDimension.FromLength(percentMin);
                        floored.MinSize = percentMinSize;
                        taffyTree.SetStyle(tnode, floored);
                    }

                    continue;
                }

                // A caption spans every column, so taffy would grow the tracks to its
                // max-content. Chromium 141 does the opposite - it wraps a caption three times
                // the table's width rather than widening the table - and floors the table only
                // by the caption's *min-content*. Taking the captions out of track sizing for
                // the two intrinsic measurements is what separates the two.
                List<TaffyNodeId> measuredCaptions = SuspendCaptions(taffyTree, ifcItems, tnode);
                taffyTree.ComputeUnroundedLayoutWithMeasure(
                    tnode,
                    new Layout.Size<TaffyAvailableSpace>(
                        TaffyAvailableSpace.MinContent,
                        TaffyAvailableSpace.MaxContent),
                    measure);
                // Intrinsic widths are read unrounded. Taffy rounds a final layout to whole
                // pixels, and a collapsing border puts half a pixel on a cell edge, so the
                // rounded table total and the rounded per-cell totals disagreed by up to a
                // pixel - which read as the columns not fitting their own max-content, and a
                // `border: 5px; border-collapse: collapse` auto table wrapped every cell.
                float minC = taffyTree.GetUnroundedLayout(tnode).Size.Width;

                // A table can never be narrower than an unshrinkable fixed-width descendant.
                minC = F32.Max(
                    minC,
                    definiteContentWidths.Get(dom) ?? 0f);

                taffyTree.ComputeUnroundedLayoutWithMeasure(
                    tnode,
                    new Layout.Size<TaffyAvailableSpace>(
                        TaffyAvailableSpace.MaxContent,
                        TaffyAvailableSpace.MaxContent),
                    measure);
                float maxC = taffyTree.GetUnroundedLayout(tnode).Size.Width;
                foreach (TaffyNodeId caption in measuredCaptions)
                {
                    taffyTree.ComputeUnroundedLayoutWithMeasure(
                        caption,
                        new Layout.Size<TaffyAvailableSpace>(
                            TaffyAvailableSpace.MinContent,
                            TaffyAvailableSpace.MaxContent),
                        measure);
                    minC = F32.Max(minC, taffyTree.GetUnroundedLayout(caption).Size.Width);
                }

                RestoreCaptions(taffyTree, measuredCaptions);

                float inlineEdges = DomStyleFixups.TableInlineOuterEdges(tableStyle);
                float declarationEdges = DomStyleFixups.TableWidthDeclarationEdges(tableStyle);
                float availableOuter = percentBase
                    ?? (availableWidths.TryGetValue(tnode, out float snapshot)
                        ? snapshot
                        : DomTableSupport.ReliableTableAvailableWidth(
                            tree, dom, styles, initialCbWidth)
                            ?? initialCbWidth);

                // A snapshot is already a used border-box width, so no box-sizing arithmetic.
                float percentOuter = percentUsedOuter
                    ?? ((widthStyle.Value * (percentBase ?? 0f))
                        + (tableStyle.BoxSizing == BoxSizing.ContentBox ? declarationEdges : 0f));
                float preferredOuter = widthStyle.Kind switch
                {
                    DimensionKind.Px when tableStyle.BoxSizing == BoxSizing.ContentBox =>
                        widthStyle.Value + declarationEdges,
                    DimensionKind.Px => widthStyle.Value,
                    DimensionKind.Percent => percentOuter,
                    _ => maxC,
                };
                // A table box is a grid or flex item like any other, so an `auto` width that its
                // parent stretches fills the item area instead of shrinking to fit: Chromium 141
                // makes an auto table in a 600px `display: grid` block 600 wide where this used
                // to leave it at its 147.5 max-content.
                bool stretchesToItemArea =
                    !ifcItems.AnonymousTables.ContainsKey(tnode)
                    && widthStyle.IsAuto
                    && DomStyleFixups.StretchesInlineToItsItemArea(tree, dom, tableStyle, styles);
                float usedOuter = widthStyle.Kind is DimensionKind.Px or DimensionKind.Percent
                    // A definite table width is not clamped to its containing block, and a
                    // percentage that resolves narrower than the content overflows it rather
                    // than wrapping every cell (CSS 2.1 17.5.2).
                    ? F32.Max(preferredOuter, minC)
                    : stretchesToItemArea
                        ? F32.Max(
                            ClampToInlineSizeLimits(tableStyle, availableOuter, availableOuter),
                            minC)
                        : F32.Min(F32.Max(preferredOuter, minC), F32.Max(availableOuter, minC));
                float usedDeclaration = tableStyle.BoxSizing == BoxSizing.ContentBox
                    ? F32.Max(usedOuter - inlineEdges, 0f)
                    : usedOuter;

                // Distribute the used track space proportionally between each column's own
                // min-content and max-content width.
                List<(TaffyNodeId Cell, int Column, int Span)> cells = [];
                foreach (TaffyNodeId cell in taffyTree.Children(tnode))
                {
                    // A caption spans every column but sizes none of them: Chromium 141 leaves
                    // an auto table at its cells' 67.31 and wraps a caption whose max-content is
                    // three times that.
                    if (ifcItems.TableCaptions.ContainsKey(cell))
                    {
                        continue;
                    }

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
                        taffyTree.ComputeUnroundedLayoutWithMeasure(
                            cell,
                            new Layout.Size<TaffyAvailableSpace>(
                                TaffyAvailableSpace.MinContent,
                                TaffyAvailableSpace.MaxContent),
                            measure);
                        float cmin = taffyTree.GetUnroundedLayout(cell).Size.Width;
                        taffyTree.ComputeUnroundedLayoutWithMeasure(
                            cell,
                            new Layout.Size<TaffyAvailableSpace>(
                                TaffyAvailableSpace.MaxContent,
                                TaffyAvailableSpace.MaxContent),
                            measure);
                        float cmax = taffyTree.GetUnroundedLayout(cell).Size.Width;
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

                        // The clamp is the shrink-to-fit limit of an `auto` table, so it must not
                        // pull a percentage table back to its containing block: Chromium 141
                        // gives `width: 150%` in a 600px block a 900px table, not a 600px one.
                        usedOuter = F32.Min(
                            F32.Max(usedOuter, percentageOuter),
                            F32.Max(F32.Max(availableOuter, minC), usedOuter));
                        usedDeclaration = tableStyle.BoxSizing == BoxSizing.ContentBox
                            ? F32.Max(usedOuter - inlineEdges, 0f)
                            : usedOuter;
                    }

                    // CSS 2.1 17.5.2.2: an `auto` table is only as wide as it needs to be, but
                    // it does have to be wide enough that each percentage column's share covers
                    // that column's *max-content*. Chromium 141 makes an auto table holding a
                    // `width: 30%` cell 161.2 of columns (48.36 / 112.84), because the auto
                    // column's 112.84 has to fit in the 70% it is left with; the min-content
                    // floor above only asked for 47.09 there, so the table stayed at its
                    // max-content 147.5 and the percentage took 30% of that. A definite width is
                    // not widened this way - Chromium keeps a `width: 120px` table holding a 30%
                    // cell at 120 - so this arm is `auto` only.
                    bool anyPercentageColumn = false;
                    foreach (float? declared in percentages)
                    {
                        if (declared is not null)
                        {
                            anyPercentageColumn = true;
                            break;
                        }
                    }

                    if (anyPercentageColumn && widthStyle.IsAuto)
                    {
                        float preferredFloor =
                            DomTableSupport.AutoTablePercentageIntrinsicFloor(colMax, percentages);
                        usedOuter = F32.Min(
                            F32.Max(usedOuter, preferredFloor + inlineEdges + interiorSpacing),
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

            DomSubgridPasses.ExitTypedPercentageScope(taffyTree, typedPercentages);
            tableIndex = groupEnd;
        }
    }

    /// <summary>
    /// Take a table's captions out of its grid's track sizing by positioning them absolutely,
    /// and answer the ones that were moved so <see cref="RestoreCaptions"/> can put them back.
    /// </summary>
    private static List<TaffyNodeId> SuspendCaptions(
        TaffyTree taffyTree,
        IfcRegistry ifcItems,
        TaffyNodeId tableNode)
    {
        List<TaffyNodeId> suspended = [];
        if (ifcItems.TableCaptions.Count == 0)
        {
            return suspended;
        }

        foreach (TaffyNodeId child in taffyTree.Children(tableNode))
        {
            if (!ifcItems.TableCaptions.ContainsKey(child))
            {
                continue;
            }

            TaffyStyle captionStyle = taffyTree.GetStyle(child).Clone();
            captionStyle.Position = Layout.Position.Absolute;
            taffyTree.SetStyle(child, captionStyle);
            suspended.Add(child);
        }

        return suspended;
    }

    private static void RestoreCaptions(TaffyTree taffyTree, IReadOnlyList<TaffyNodeId> suspended)
    {
        foreach (TaffyNodeId caption in suspended)
        {
            TaffyStyle captionStyle = taffyTree.GetStyle(caption).Clone();
            captionStyle.Position = Layout.Position.Relative;
            taffyTree.SetStyle(caption, captionStyle);
        }
    }

    /// <summary>
    /// Clamp a stretched inline size by the box's own <c>min-width</c> / <c>max-width</c>.
    /// A stretch is a used size, so unlike a shrink-to-fit it is not already inside them:
    /// Chromium 141 gives a `max-width: 200px` table in a 600px grid 200, not 600.
    /// </summary>
    private static float ClampToInlineSizeLimits(LayoutStyle style, float value, float percentBase)
    {
        static float? Resolve(Dimension dimension, float percentBase) => dimension.Kind switch
        {
            DimensionKind.Px => dimension.Value,
            DimensionKind.Percent => dimension.Value * percentBase,
            _ => null,
        };

        float clamped = value;
        if (Resolve(style.MaxWidth, percentBase) is { } maximum && maximum >= 0f)
        {
            clamped = F32.Min(clamped, maximum);
        }

        if (Resolve(style.MinWidth, percentBase) is { } minimum && minimum >= 0f)
        {
            clamped = F32.Max(clamped, minimum);
        }

        return clamped;
    }
}
