// Port of the post-cascade style fixups in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using Obscura.Render.Css;
using TaffyAlignItems = Obscura.Render.Layout.AlignItems;
using TaffyDirection = Obscura.Render.Layout.Direction;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyGridAutoFlow = Obscura.Render.Layout.GridAutoFlow;
using TaffyGridPlacement = Obscura.Render.Layout.GridPlacement;
using TaffyGridPlacementKind = Obscura.Render.Layout.GridPlacementKind;
using TaffyGridTemplateComponent = Obscura.Render.Layout.GridTemplateComponent;
using TaffyGridTemplateComponentKind = Obscura.Render.Layout.GridTemplateComponentKind;
using TaffyJustifyContent = Obscura.Render.Layout.AlignContent;
using TaffyLine = Obscura.Render.Layout.Line<Obscura.Render.Layout.GridPlacement>;
using TaffyMaxTrack = Obscura.Render.Layout.MaxTrackSizingFunction;
using TaffyMinTrack = Obscura.Render.Layout.MinTrackSizingFunction;
using TaffyPosition = Obscura.Render.Layout.Position;
using TaffyTrackSizingFunction = Obscura.Render.Layout.TrackSizingFunction;

namespace Obscura.Render;

internal sealed class NativeButtonIntrinsicContent
{
    internal System.Text.StringBuilder Text { get; } = new();

    internal float AtomicWidth { get; set; }
}

internal enum ContainerAutoInlineSize : byte
{
    FillAvailable,
    Intrinsic,
    StretchedGridItem,
}

internal enum ContainerAutoBlockSize : byte
{
    Intrinsic,
    StretchedGridItem,
}

internal enum EffectiveGridChildKind : byte
{
    Dom,
    Generated,
}

internal readonly record struct EffectiveGridChild(
    EffectiveGridChildKind Kind,
    NodeId Node,
    GeneratedBoxKind Generated);

internal static class DomStyleFixups
{
    /// <summary>
    /// Collect the normal-flow content that contributes to an auto-sized <c>&lt;button&gt;</c>'s
    /// intrinsic inline size.
    /// </summary>
    /// <remarks>
    /// DOM <c>textContent</c> is deliberately the wrong abstraction here: it includes text
    /// below <c>display:none</c> boxes and absolutely positioned accessibility labels. Atomic
    /// replaced descendants (most commonly an SVG icon) do contribute their definite outer
    /// inline size.
    /// </remarks>
    internal static NativeButtonIntrinsicContent NativeButtonIntrinsicContent(
        DomTree tree,
        NodeId root,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float fontSize,
        TextEngine engine)
    {
        NativeButtonIntrinsicContent content = new();
        foreach (NodeId child in DomTraversal.RenderedChildren(tree, root))
        {
            NativeButtonWalk(tree, child, styles, fontSize, engine, content);
        }

        return content;
    }

    private static float? DefiniteInlineSize(Dimension dimension, float fontSize)
    {
        float? value = dimension.Kind switch
        {
            DimensionKind.Px => dimension.Value,
            DimensionKind.Em or DimensionKind.Rem => dimension.Value * fontSize,
            DimensionKind.Ex => dimension.Value * fontSize * Dimension.ExPerEm,
            _ => null,
        };
        return value is { } resolved ? F32.Max(resolved, 0f) : null;
    }

    private static void NativeButtonWalk(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        float fontSize,
        TextEngine engine,
        NativeButtonIntrinsicContent content)
    {
        if (tree.GetNode(id) is not { } node)
        {
            return;
        }

        if (node.TextContentOfTextNode is { } text)
        {
            content.Text.Append(text);
            return;
        }

        if (node.AsElement() is not { } element)
        {
            return;
        }

        styles.TryGetValue(id, out LayoutStyle? style);
        if (style is not null
            && (style.Display == Display.None || style.Position == TaffyPosition.Absolute))
        {
            return;
        }

        bool isAtomic = element.Name.Local
            is "svg" or "img" or "video" or "canvas" or "iframe" or "embed" or "object";
        if (isAtomic)
        {
            if (style is not null && DefiniteInlineSize(style.Width, fontSize) is { } width)
            {
                float horizontalEdges = style.Padding.Left
                    + style.Padding.Right
                    + style.Border.Left
                    + style.Border.Right;
                float borderBox = style.BoxSizing == BoxSizing.ContentBox
                    ? width + horizontalEdges
                    : F32.Max(width, horizontalEdges);
                content.AtomicWidth += borderBox
                    + F32.Max(style.Margin.Left, 0f)
                    + F32.Max(style.Margin.Right, 0f);
            }

            return;
        }

        // DEVIATION from crates/obscura-render/src/dom.rs `native_button_intrinsic_content`,
        // which recurses straight past every non-replaced element and counts only its text.
        // A button laid out as a flex row is ordinarily sized by CSS intrinsic sizing, which
        // sums each item's outer size, so dropping element boxes and their margins makes the
        // shortcut narrower than the content it will hold. Tesserae's toolbar buttons are
        // `<i class="fi-rr-*" style="width:12px"></i><span style="margin-left:10px">Label</span>`
        // and came out 22px short of Chromium on every one, which then shrank the label span
        // and wrapped it. Count a definite-width child as its own outer box (as the replaced
        // branch above already does) and carry every child's horizontal edges. See
        // "Known deviations" in todo.md.
        float childEdges = 0f;
        if (style is not null)
        {
            float horizontal = style.Padding.Left
                + style.Padding.Right
                + style.Border.Left
                + style.Border.Right;
            float margins = F32.Max(style.Margin.Left, 0f) + F32.Max(style.Margin.Right, 0f);
            if (DefiniteInlineSize(style.Width, fontSize) is { } childWidth)
            {
                float childBorderBox = style.BoxSizing == BoxSizing.ContentBox
                    ? childWidth + horizontal
                    : F32.Max(childWidth, horizontal);
                content.AtomicWidth += childBorderBox + margins;

                // A definite inline size is the whole contribution; its own text is laid out
                // inside it and cannot widen the button further.
                return;
            }

            childEdges = horizontal + margins;
        }

        content.AtomicWidth += childEdges;

        // An icon font renders through ::before, so a descendant with no element or text
        // children can still be 12px wide. Shape the generated content with the pseudo's own
        // style, not the button's: the glyph comes from the icon face.
        if (style is not null)
        {
            if (style.BeforeContent is { Length: > 0 } beforeContent)
            {
                content.AtomicWidth += engine.MeasureControlLabel(
                    beforeContent, style.BeforePseudo ?? style);
            }

            if (style.AfterContent is { Length: > 0 } afterContent)
            {
                content.AtomicWidth += engine.MeasureControlLabel(
                    afterContent, style.AfterPseudo ?? style);
            }
        }

        foreach (NodeId child in DomTraversal.RenderedChildren(tree, id))
        {
            NativeButtonWalk(tree, child, styles, fontSize, engine, content);
        }
    }

    /// <summary>
    /// HTML table auto-layout approximation: for each <c>&lt;tr&gt;</c>, the last
    /// <c>&lt;td&gt;</c>/<c>&lt;th&gt;</c> child with no explicit width absorbs the row's
    /// leftover space.
    /// </summary>
    internal static void GrowTrailingAutoCells(DomTree tree, Dictionary<NodeId, LayoutStyle> styles)
    {
        foreach (NodeId tr in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            if (!DomTraversal.IsLocal(tree, tr, "tr"))
            {
                continue;
            }

            List<NodeId> children = tree.Children(tr);
            for (int index = children.Count - 1; index >= 0; index--)
            {
                NodeId cid = children[index];
                if (!DomTraversal.IsAnyLocal(tree, cid, "td", "th"))
                {
                    continue;
                }

                if (styles.TryGetValue(cid, out LayoutStyle? style)
                    && style.Width.IsAuto
                    && style.FlexGrow is null)
                {
                    style.FlexGrow = 1f;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Distribute a table's <c>border-spacing</c> down as its own row gap and every descendant
    /// <c>&lt;tr&gt;</c>'s column gap, without crossing into a nested table's scope.
    /// </summary>
    internal static void PropagateBorderSpacing(DomTree tree, Dictionary<NodeId, LayoutStyle> styles)
    {
        foreach (NodeId id in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            if (!DomTraversal.IsLocal(tree, id, "table"))
            {
                continue;
            }

            if (!styles.TryGetValue(id, out LayoutStyle? tableStyle))
            {
                continue;
            }

            (float horizontal, float vertical) = TableSpacing(tableStyle);
            tableStyle.RowGap = vertical;
            tableStyle.RowGapExpression = null;
            ApplySpacingToRows(tree, id, horizontal, vertical, styles);
        }
    }

    private static void ApplySpacingToRows(
        DomTree tree,
        NodeId id,
        float horizontal,
        float vertical,
        Dictionary<NodeId, LayoutStyle> styles)
    {
        foreach (NodeId cid in tree.Children(id))
        {
            if (DomTraversal.IsLocal(tree, cid, "table"))
            {
                continue;
            }

            if (DomTraversal.IsLocal(tree, cid, "tr") && styles.TryGetValue(cid, out LayoutStyle? style))
            {
                style.ColumnGap = horizontal;
                style.RowGap = vertical;
                style.ColumnGapExpression = null;
                style.RowGapExpression = null;
            }

            ApplySpacingToRows(tree, cid, horizontal, vertical, styles);
        }
    }

    internal static void CollectEffectiveGridChildren(
        DomTree tree,
        IReadOnlyList<NodeId> children,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        List<EffectiveGridChild> output)
    {
        foreach (NodeId child in children)
        {
            bool transparent = styles.TryGetValue(child, out LayoutStyle? style)
                && style.DisplayContents
                && style.Display != Display.None;
            if (!transparent)
            {
                output.Add(new EffectiveGridChild(EffectiveGridChildKind.Dom, child, default));
                continue;
            }

            if (style?.BeforePseudo is not null)
            {
                output.Add(new EffectiveGridChild(
                    EffectiveGridChildKind.Generated, child, GeneratedBoxKind.Before));
            }

            CollectEffectiveGridChildren(
                tree, DomTraversal.RenderedChildren(tree, child), styles, output);

            if (style?.AfterPseudo is not null)
            {
                output.Add(new EffectiveGridChild(
                    EffectiveGridChildKind.Generated, child, GeneratedBoxKind.After));
            }
        }
    }

    internal static LayoutStyle? EffectiveGridChildStyle(
        EffectiveGridChild child,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (child.Kind == EffectiveGridChildKind.Dom)
        {
            return styles.TryGetValue(child.Node, out LayoutStyle? style) ? style : null;
        }

        if (!styles.TryGetValue(child.Node, out LayoutStyle? host))
        {
            return null;
        }

        return child.Generated == GeneratedBoxKind.Before ? host.BeforePseudo : host.AfterPseudo;
    }

    /// <summary>
    /// Resolve each grid child's <c>grid-area</c> name and named-line placement to taffy lines.
    /// </summary>
    internal static void ResolveGridAreas(
        DomTree tree,
        NodeId root,
        Dictionary<NodeId, LayoutStyle> styles)
    {
        List<NodeId> stack = [root];
        while (stack.Count > 0)
        {
            NodeId id = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            foreach (NodeId cid in tree.Children(id))
            {
                stack.Add(cid);
            }

            if (!styles.TryGetValue(id, out LayoutStyle? style) || style.Display != Display.Grid)
            {
                continue;
            }

            List<List<string>>? areas = style.GridAreas is { Count: > 0 } gridAreas ? gridAreas : null;
            Dictionary<string, short>? colLines = style.GridColLineNames;
            Dictionary<string, short>? rowLines = style.GridRowLineNames;
            if (areas is null && colLines is null && rowLines is null)
            {
                continue;
            }

            List<EffectiveGridChild> gridChildren = [];
            CollectEffectiveGridChildren(
                tree, DomTraversal.RenderedChildren(tree, id), styles, gridChildren);

            if (areas is not null)
            {
                // name -> (rowStart, rowEnd, colStart, colEnd) in 0-based track indices.
                Dictionary<string, (int R0, int R1, int C0, int C1)> spans = new(StringComparer.Ordinal);
                for (int r = 0; r < areas.Count; r++)
                {
                    List<string> row = areas[r];
                    for (int c = 0; c < row.Count; c++)
                    {
                        string name = row[c];
                        if (string.Equals(name, ".", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (spans.TryGetValue(name, out var span))
                        {
                            spans[name] = (
                                Math.Min(span.R0, r),
                                Math.Max(span.R1, r),
                                Math.Min(span.C0, c),
                                Math.Max(span.C1, c));
                        }
                        else
                        {
                            spans[name] = (r, r, c, c);
                        }
                    }
                }

                foreach (EffectiveGridChild child in gridChildren)
                {
                    if (EffectiveGridChildStyle(child, styles) is not { } childStyle)
                    {
                        continue;
                    }

                    if (childStyle.GridAreaName is not { } name)
                    {
                        continue;
                    }

                    if (spans.TryGetValue(name, out var span))
                    {
                        childStyle.GridRow = new TaffyLine(
                            TaffyGridPlacement.FromLineIndex((short)(span.R0 + 1)),
                            TaffyGridPlacement.FromLineIndex((short)(span.R1 + 2)));
                        childStyle.GridColumn = new TaffyLine(
                            TaffyGridPlacement.FromLineIndex((short)(span.C0 + 1)),
                            TaffyGridPlacement.FromLineIndex((short)(span.C1 + 2)));
                    }
                }
            }

            if (colLines is not null || rowLines is not null)
            {
                foreach (EffectiveGridChild child in gridChildren)
                {
                    if (EffectiveGridChildStyle(child, styles) is not { } childStyle)
                    {
                        continue;
                    }

                    if (childStyle.GridColumnRaw is { } columnRaw
                        && colLines is not null
                        && ResolveNamedPlacement(columnRaw, colLines) is { } column)
                    {
                        childStyle.GridColumn = column;
                    }

                    if (childStyle.GridRowRaw is { } rowRaw
                        && rowLines is not null
                        && ResolveNamedPlacement(rowRaw, rowLines) is { } line)
                    {
                        childStyle.GridRow = line;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolve a raw <c>grid-column</c>/<c>grid-row</c> value that names grid lines into a
    /// numeric taffy line. Returns <c>null</c> when a referenced name is absent.
    /// </summary>
    internal static TaffyLine? ResolveNamedPlacement(string raw, Dictionary<string, short> map)
    {
        TaffyGridPlacement? Side(string token, bool isStart)
        {
            string t = token.Trim();
            if (t.Length == 0 || string.Equals(t, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return TaffyGridPlacement.Auto;
            }

            if (t.StartsWith("span", StringComparison.Ordinal)
                && ushort.TryParse(
                    t[4..].Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out ushort span))
            {
                return TaffyGridPlacement.FromSpan(span);
            }

            if (short.TryParse(
                t,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out short index))
            {
                return TaffyGridPlacement.FromLineIndex(index);
            }

            if (map.TryGetValue(t, out short direct))
            {
                return TaffyGridPlacement.FromLineIndex(direct);
            }

            string suffixed = t + (isStart ? "-start" : "-end");
            return map.TryGetValue(suffixed, out short suffix)
                ? TaffyGridPlacement.FromLineIndex(suffix)
                : null;
        }

        int slash = raw.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            TaffyGridPlacement? start = Side(raw[..slash], true);
            TaffyGridPlacement? end = Side(raw[(slash + 1)..], false);
            return start is { } s && end is { } e ? new TaffyLine(s, e) : null;
        }

        string name = raw.Trim();
        if (map.TryGetValue(name + "-start", out short areaStart))
        {
            TaffyGridPlacement end = map.TryGetValue(name + "-end", out short areaEnd)
                ? TaffyGridPlacement.FromLineIndex(areaEnd)
                : TaffyGridPlacement.Auto;
            return new TaffyLine(TaffyGridPlacement.FromLineIndex(areaStart), end);
        }

        return map.TryGetValue(name, out short only)
            ? new TaffyLine(TaffyGridPlacement.FromLineIndex(only), TaffyGridPlacement.Auto)
            : null;
    }

    /// <summary>
    /// Does <paramref name="id"/> have any direct rendered child that is inline-level?
    /// </summary>
    internal static bool HasInlineContent(
        DomTree tree,
        NodeId id,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        foreach (NodeId cid in DomTraversal.RenderedChildren(tree, id))
        {
            if (tree.GetNode(cid) is not { } node)
            {
                continue;
            }

            if (node.TextContentOfTextNode is { } contents)
            {
                if (contents.Trim().Length != 0)
                {
                    return true;
                }

                continue;
            }

            if (!styles.TryGetValue(cid, out LayoutStyle? style))
            {
                continue;
            }

            // A display:contents wrapper is transparent: whether it reads as inline content
            // depends on what it splices in.
            if (style.DisplayContents && style.Display != Display.None)
            {
                if (HasInlineContent(tree, cid, styles))
                {
                    return true;
                }

                continue;
            }

            // Out-of-flow boxes are not inline content.
            if (LayoutStyleExtensions.IsInlineLevelBox(style)
                && style.Position != TaffyPosition.Absolute
                && style.Float is null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a computed box participates as an in-flow block-level child.</summary>
    internal static bool IsInFlowBlockLevel(LayoutStyle style) =>
        style.Display is Display.Block or Display.Flex or Display.Grid
        && !style.IsInlineBlock
        && !style.DisplayContents
        && style.Float is null
        && style.Position != TaffyPosition.Absolute;

    /// <summary>Blockify the generated layout children of a flex/grid container.</summary>
    internal static void BlockifyLayoutChildren(
        DomTree tree,
        NodeId parent,
        Dictionary<NodeId, LayoutStyle> styles)
    {
        foreach (NodeId child in DomTraversal.RenderedChildren(tree, parent))
        {
            styles.TryGetValue(child, out LayoutStyle? style);
            bool transparent = style is not null
                && style.DisplayContents
                && style.Display != Display.None;
            if (transparent)
            {
                foreach (LayoutStyle? pseudo in new[] { style!.BeforePseudo, style.AfterPseudo })
                {
                    if (pseudo is not null)
                    {
                        LayoutStyleExtensions.BlockifyOuterDisplay(pseudo);
                    }
                }

                BlockifyLayoutChildren(tree, child, styles);
            }
            else if (style is not null)
            {
                LayoutStyleExtensions.BlockifyOuterDisplay(style);
            }
        }
    }

    /// <summary>
    /// Generated <c>::before</c>/<c>::after</c> boxes participate in the same display fixups as
    /// real elements.
    /// </summary>
    internal static void BlockifyGeneratedPseudos(Dictionary<NodeId, LayoutStyle> styles)
    {
        foreach (LayoutStyle host in styles.Values)
        {
            bool hostIsItemContainer = host.Display is Display.Flex or Display.Grid
                && !host.InternalFlexContainer;
            foreach (LayoutStyle? pseudo in new[] { host.BeforePseudo, host.AfterPseudo })
            {
                if (pseudo is null)
                {
                    continue;
                }

                if (hostIsItemContainer
                    || pseudo.Position == TaffyPosition.Absolute
                    || pseudo.Float is not null)
                {
                    LayoutStyleExtensions.BlockifyOuterDisplay(pseudo);
                }
            }
        }
    }

    /// <summary>Effective size-query containment for this generated box.</summary>
    internal static ContainerType EffectiveContainerType(LayoutStyle style) =>
        style.InternalFlexContainer
        || style.IsTableBox
        || (style.Display == Display.Inline && !style.IsInlineBlock)
            ? ContainerType.Normal
            : style.ContainerType;

    internal static TaffyAlignItems UsedFlexAlignment(
        TaffyAlignItems? value,
        TaffyAlignItems? parent) =>
        (value ?? parent ?? TaffyAlignItems.Normal).ResolveNormal(TaffyAlignItems.Stretch);

    internal static (TaffyAlignItems Horizontal, TaffyAlignItems Vertical) UsedGridAlignments(
        LayoutStyle style,
        LayoutStyle parent)
    {
        TaffyAlignItems horizontal = style.JustifySelf ?? parent.JustifyItems ?? TaffyAlignItems.Normal;
        TaffyAlignItems vertical = style.AlignSelf ?? parent.AlignItems ?? TaffyAlignItems.Normal;
        if (style.HasReplacedSizing)
        {
            return (
                horizontal.ResolveNormal(TaffyAlignItems.Start),
                vertical.ResolveNormal(TaffyAlignItems.Start));
        }

        if (style.AspectRatio is null)
        {
            return (
                horizontal.ResolveNormal(TaffyAlignItems.Stretch),
                vertical.ResolveNormal(TaffyAlignItems.Stretch));
        }

        bool horizontalIsNormal = horizontal == TaffyAlignItems.Normal;
        bool verticalIsNormal = vertical == TaffyAlignItems.Normal;
        TaffyAlignItems usedHorizontal = horizontalIsNormal
            ? (!verticalIsNormal && vertical == TaffyAlignItems.Stretch
                ? TaffyAlignItems.Start
                : TaffyAlignItems.Stretch)
            : horizontal;
        TaffyAlignItems usedVertical = verticalIsNormal
            ? (!horizontalIsNormal && horizontal != TaffyAlignItems.Stretch
                ? TaffyAlignItems.Stretch
                : TaffyAlignItems.Start)
            : vertical;
        return (usedHorizontal, usedVertical);
    }

    /// <summary>Assign a native control's intrinsic border-box size to its auto axes.</summary>
    internal static void AssignNativeControlSize(
        LayoutStyle style,
        (bool Inline, bool Block) stretchedGridItem,
        float intrinsicWidth,
        float intrinsicHeight,
        float horizontalEdges,
        float verticalEdges)
    {
        bool contentBox = style.BoxSizing == BoxSizing.ContentBox;
        if (style.Width.IsAuto && !stretchedGridItem.Inline)
        {
            style.Width = Dimension.Px(contentBox
                ? F32.Max(intrinsicWidth - horizontalEdges, 0f)
                : intrinsicWidth);
        }

        if (style.Height.IsAuto && !stretchedGridItem.Block)
        {
            style.Height = Dimension.Px(contentBox
                ? F32.Max(intrinsicHeight - verticalEdges, 0f)
                : intrinsicHeight);
        }
    }

    /// <summary>Classify how an auto inline-size is resolved.</summary>
    internal static ContainerAutoInlineSize ContainerAutoInlineSizeOf(
        DomTree tree,
        NodeId id,
        LayoutStyle style,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        // Grid items are blockified and their float is ignored.
        if ((style.IsInlineBlock || style.Float is not null)
            && !IsInFlowGridItem(tree, id, style, styles))
        {
            return ContainerAutoInlineSize.Intrinsic;
        }

        if (style.Position == TaffyPosition.Absolute)
        {
            // An absolutely positioned auto width is fill-available only when both inline
            // insets are definite.
            return style.Inset[1] is null || style.Inset[3] is null
                ? ContainerAutoInlineSize.Intrinsic
                : ContainerAutoInlineSize.FillAvailable;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        LayoutStyle parentStyle;
        while (true)
        {
            if (parent is not { } parentId)
            {
                return ContainerAutoInlineSize.FillAvailable;
            }

            if (!styles.TryGetValue(parentId, out LayoutStyle? found))
            {
                return ContainerAutoInlineSize.FillAvailable;
            }

            if (found.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            parentStyle = found;
            break;
        }

        switch (parentStyle.Display)
        {
            case Display.Flex:
            {
                bool row = parentStyle.FlexDirection
                    is not (TaffyFlexDirection.Column or TaffyFlexDirection.ColumnReverse);
                if (row)
                {
                    return ContainerAutoInlineSize.Intrinsic;
                }

                return UsedFlexAlignment(style.AlignSelf, parentStyle.AlignItems) == TaffyAlignItems.Stretch
                    ? ContainerAutoInlineSize.FillAvailable
                    : ContainerAutoInlineSize.Intrinsic;
            }

            case Display.Grid:
                return UsedGridAlignments(style, parentStyle).Horizontal == TaffyAlignItems.Stretch
                    && !style.MarginAuto[1]
                    && !style.MarginAuto[3]
                        ? ContainerAutoInlineSize.StretchedGridItem
                        : ContainerAutoInlineSize.Intrinsic;
            case Display.Inline:
                return ContainerAutoInlineSize.Intrinsic;
            default:
                return ContainerAutoInlineSize.FillAvailable;
        }
    }

    /// <summary>Classify an auto block-size for size containment.</summary>
    internal static ContainerAutoBlockSize ContainerAutoBlockSizeOf(
        DomTree tree,
        NodeId id,
        LayoutStyle style,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        // Absolutely positioned descendants are not grid items.
        if (style.Position == TaffyPosition.Absolute)
        {
            return ContainerAutoBlockSize.Intrinsic;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        LayoutStyle parentStyle;
        while (true)
        {
            if (parent is not { } parentId)
            {
                return ContainerAutoBlockSize.Intrinsic;
            }

            if (!styles.TryGetValue(parentId, out LayoutStyle? found))
            {
                return ContainerAutoBlockSize.Intrinsic;
            }

            if (found.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            parentStyle = found;
            break;
        }

        return parentStyle.Display == Display.Grid
            && UsedGridAlignments(style, parentStyle).Vertical == TaffyAlignItems.Stretch
            && !style.MarginAuto[0]
            && !style.MarginAuto[2]
                ? ContainerAutoBlockSize.StretchedGridItem
                : ContainerAutoBlockSize.Intrinsic;
    }

    /// <summary>Apply the used-size part of <c>container-type</c>'s implicit size containment.</summary>
    internal static void ApplyContainerSizeContainment(
        DomTree tree,
        NodeId id,
        LayoutStyle style,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        Layout.Style taffyStyle)
    {
        ContainerType kind = EffectiveContainerType(style);
        if (kind == ContainerType.Normal)
        {
            return;
        }

        if (style.MinWidth.IsAuto)
        {
            Layout.Size<Layout.Dimension> minSize = taffyStyle.MinSize;
            minSize.Width = Layout.Dimension.FromLength(0f);
            taffyStyle.MinSize = minSize;
        }

        bool ratioTransfersInlineSize = style.AspectRatio is not null && !style.Height.IsAuto;
        if (style.Width.IsAuto && !ratioTransfersInlineSize)
        {
            switch (ContainerAutoInlineSizeOf(tree, id, style, styles))
            {
                case ContainerAutoInlineSize.Intrinsic:
                {
                    Layout.Size<Layout.Dimension> size = taffyStyle.Size;
                    size.Width = Layout.Dimension.FromLength(0f);
                    taffyStyle.Size = size;
                    break;
                }

                case ContainerAutoInlineSize.StretchedGridItem:
                {
                    Layout.Size<bool> containment = taffyStyle.IntrinsicSizeContainment;
                    containment.Width = true;
                    taffyStyle.IntrinsicSizeContainment = containment;
                    break;
                }
            }
        }

        if (kind != ContainerType.Size)
        {
            return;
        }

        if (style.MinHeight.IsAuto)
        {
            Layout.Size<Layout.Dimension> minSize = taffyStyle.MinSize;
            minSize.Height = Layout.Dimension.FromLength(0f);
            taffyStyle.MinSize = minSize;
        }

        bool ratioTransfersBlockSize = style.AspectRatio is not null && !style.Width.IsAuto;
        if (style.Height.IsAuto && !ratioTransfersBlockSize)
        {
            switch (ContainerAutoBlockSizeOf(tree, id, style, styles))
            {
                case ContainerAutoBlockSize.Intrinsic:
                {
                    Layout.Size<Layout.Dimension> size = taffyStyle.Size;
                    size.Height = Layout.Dimension.FromLength(0f);
                    taffyStyle.Size = size;
                    break;
                }

                case ContainerAutoBlockSize.StretchedGridItem:
                {
                    Layout.Size<bool> containment = taffyStyle.IntrinsicSizeContainment;
                    containment.Height = true;
                    taffyStyle.IntrinsicSizeContainment = containment;
                    break;
                }
            }
        }
    }

    internal static bool IsInFlowGridItem(
        DomTree tree,
        NodeId id,
        LayoutStyle style,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles)
    {
        if (style.Position == TaffyPosition.Absolute)
        {
            return false;
        }

        NodeId? parent = DomTraversal.RenderedParent(tree, id);
        while (true)
        {
            if (parent is not { } parentId)
            {
                return false;
            }

            if (!styles.TryGetValue(parentId, out LayoutStyle? parentStyle))
            {
                return false;
            }

            if (parentStyle.DisplayContents)
            {
                parent = DomTraversal.RenderedParent(tree, parentId);
                continue;
            }

            return parentStyle.Display == Display.Grid;
        }
    }

    internal static bool EstablishesBlockFormattingContext(LayoutStyle style) =>
        style.Display is Display.Flex or Display.Grid
        || EffectiveContainerType(style) != ContainerType.Normal
        || style.FlowRoot
        || (style.OverflowScrollContainer && !style.OverflowPropagatedToViewport)
        || style.IsInlineBlock
        || style.Float is not null
        || style.Position == TaffyPosition.Absolute;

    internal static bool ClearMatchesFloatSides(Clear clear, bool hasLeft, bool hasRight) =>
        (!hasLeft || clear is Clear.Left or Clear.Both)
        && (!hasRight || clear is Clear.Right or Clear.Both);

    internal static bool HasDeferredOrAutoMargin(LayoutStyle style)
    {
        foreach (bool value in style.MarginAuto)
        {
            if (value)
            {
                return true;
            }
        }

        foreach (float? value in style.MarginPercent)
        {
            if (value is not null)
            {
                return true;
            }
        }

        foreach (Dimension? value in style.MarginRelative)
        {
            if (value is not null)
            {
                return true;
            }
        }

        foreach (string? value in style.MarginExpressions)
        {
            if (value is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Effective separate-border spacing.</summary>
    internal static (float Horizontal, float Vertical) TableSpacing(LayoutStyle style) =>
        style.BorderCollapse == true ? (0f, 0f) : style.BorderSpacing ?? (0f, 0f);

    /// <summary>
    /// Horizontal non-track area in the table's border box, excluding the gaps between columns.
    /// </summary>
    internal static float TableInlineOuterEdges(LayoutStyle style)
    {
        (float spacing, _) = TableSpacing(style);
        return style.Border.Left
            + style.Border.Right
            + style.Padding.Left
            + style.Padding.Right
            + (spacing * 2f);
    }

    internal static ContainerSnapshot ContainerSnapshotOf(DomTree tree, DomLayout layout)
    {
        float rootFontSize = 16f;
        foreach (NodeId id in tree.Descendants(tree.Document))
        {
            if (tree.GetNode(id)?.IsElement != true)
            {
                continue;
            }

            if (layout.Styles.TryGetValue(id, out LayoutStyle? rootStyle)
                && rootStyle.FontSize is { } size
                && float.IsFinite(size)
                && size > 0f)
            {
                rootFontSize = size;
            }

            break;
        }

        ContainerSnapshot snapshot = new() { RootFontSize = rootFontSize };
        foreach ((NodeId id, LayoutStyle style) in layout.Styles)
        {
            ContainerType availableType = style.DisplayContents
                ? ContainerType.Normal
                : EffectiveContainerType(style);
            if ((style.ContainerType == ContainerType.Normal && style.ContainerNames.Count == 0)
                || style.Display == Display.None)
            {
                continue;
            }

            Rect? rect = layout.Rects.TryGetValue(id, out Rect found) ? found : null;

            // An inline box that computes to `container-type` is still the nearest matching
            // query container even when size containment cannot apply to it.
            if (rect is null && availableType != ContainerType.Normal)
            {
                continue;
            }

            float horizontalEdges = style.Border.Left
                + style.Border.Right
                + style.Padding.Left
                + style.Padding.Right;
            float verticalEdges = style.Border.Top
                + style.Border.Bottom
                + style.Padding.Top
                + style.Padding.Bottom;
            snapshot.Boxes[id] = new ContainerBox
            {
                ContainerType = style.ContainerType,
                AvailableType = availableType,
                Names = [.. style.ContainerNames],
                ContentWidth = rect is { } box ? F32.Max(box.Width - horizontalEdges, 0f) : 0f,
                ContentHeight = rect is { } area ? F32.Max(area.Height - verticalEdges, 0f) : 0f,
                FontSize = style.FontSize is { } fontSize && float.IsFinite(fontSize) && fontSize > 0f
                    ? fontSize
                    : 16f,
            };
        }

        return snapshot;
    }

    internal static bool IsFullSpanColumnSubgrid(LayoutStyle style)
    {
        if (style.Display != Display.Grid
            || !style.GridTemplateColumnsSubgrid
            || style.OverflowHidden
            || style.Position == TaffyPosition.Absolute
            || !style.Width.IsAuto
            || (style.JustifySelf is { } justify
                && justify.ResolveNormal(TaffyAlignItems.Stretch) != TaffyAlignItems.Stretch)
            || style.MarginAuto[1]
            || style.MarginAuto[3])
        {
            return false;
        }

        if (style.GridColumn is not { } line)
        {
            return false;
        }

        return line.Start.Kind == TaffyGridPlacementKind.Line
            && line.End.Kind == TaffyGridPlacementKind.Line
            && line.Start.LineIndex == 1
            && line.End.LineIndex == -1;
    }

    /// <summary>Whether an item is wholly eligible for ordinary grid auto-placement.</summary>
    internal static bool HasOnlyAutoGridPlacement(LayoutStyle style)
    {
        if (style.GridColumnRaw is not null || style.GridRowRaw is not null)
        {
            return false;
        }

        static bool AxisIsAuto(TaffyLine? line) =>
            line is not { } value
            || (value.Start.Kind == TaffyGridPlacementKind.Auto
                && value.End.Kind == TaffyGridPlacementKind.Auto);

        return AxisIsAuto(style.GridColumn) && AxisIsAuto(style.GridRow);
    }

    internal static List<TaffyGridTemplateComponent> FixedGridTracks(IReadOnlyList<float> widths)
    {
        List<TaffyGridTemplateComponent> tracks = new(widths.Count);
        foreach (float width in widths)
        {
            float used = F32.Max(width, 0f);
            tracks.Add(TaffyGridTemplateComponent.FromSingle(TaffyTrackSizingFunction.MinMax(
                TaffyMinTrack.FromLength(used),
                TaffyMaxTrack.FromLength(used))));
        }

        return tracks;
    }

    internal static bool GridTrackIsAllAuto(IReadOnlyList<TaffyGridTemplateComponent> tracks)
    {
        if (tracks.Count == 0)
        {
            return false;
        }

        foreach (TaffyGridTemplateComponent track in tracks)
        {
            if (track.Kind != TaffyGridTemplateComponentKind.Single
                || !track.Single.Min.IntoRaw().IsAuto
                || !track.Single.Max.IntoRaw().IsAuto)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool GridAutoFlowIsRow(LayoutStyle style) =>
        (style.GridAutoFlow ?? TaffyGridAutoFlow.Row)
            is TaffyGridAutoFlow.Row or TaffyGridAutoFlow.RowDense;

    internal static bool JustifyContentIsStretch(TaffyJustifyContent? value) =>
        value is null || value == TaffyJustifyContent.Stretch;

    internal static bool DirectionIsRtl(LayoutStyle style) => style.Direction == TaffyDirection.Rtl;
}
