// Port of the top-down computed-value pass and the native-control sizing pass inside
// `layout_dom_once` in crates/obscura-render/src/dom.rs.
using System.Globalization;
using Obscura.Dom;
using Obscura.Render.Css;
using TaffyAlignItems = Obscura.Render.Layout.AlignItems;
using TaffyDirection = Obscura.Render.Layout.Direction;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyPosition = Obscura.Render.Layout.Position;

namespace Obscura.Render;

public static partial class RenderDom
{
    /// <summary>
    /// Top-down inheritance of the properties CSS inherits by default, plus resolution of
    /// every font/viewport-relative length now that its reference sizes are known.
    /// </summary>
    private static void ResolveComputedValues(
        DomTree tree,
        NodeId rootId,
        Dictionary<NodeId, LayoutStyle> styles,
        HashSet<NodeId>? freshStyles,
        HashSet<NodeId> definiteHeightNodes,
        Inherited rootInherited,
        float rootFs,
        float vw,
        float vh,
        (float Width, float Height) viewport,
        float initialCbWidth)
    {
        List<(NodeId Id, Inherited Inherited)> queue = [(rootId, rootInherited)];
        while (queue.Count > 0)
        {
            (NodeId id, Inherited inh) = queue[^1];
            queue.RemoveAt(queue.Count - 1);

            // Default the child containing-block width to this element's own.
            float childCbWidth = inh.CbWidth;
            bool childCbHeightDefinite = false;
            float childCbHeight = 0f;
            bool childCbHeightKnown = false;
            bool reusedComputedStyle = freshStyles is { } fresh && !fresh.Contains(id);
            if (reusedComputedStyle)
            {
                // Retained styles have already passed through this destructive normalization
                // once. Seed the inherited context from the prior computed values instead.
                if (styles.TryGetValue(id, out LayoutStyle? retainedStyle))
                {
                    float retainedEm = retainedStyle.FontSize ?? inh.FontSize ?? 16f;
                    ComputedStyle.SetGridCalcContext(retainedStyle, retainedEm, rootFs, vw, vh);
                    SetLateCalcContext(retainedStyle.InsetCalc, retainedEm, rootFs, vw, vh);
                    SetLateCalcContext(retainedStyle.SizeCalc, retainedEm, rootFs, vw, vh);
                    inh.Display = retainedStyle.Display;
                    inh.Direction = retainedStyle.Direction ?? inh.Direction;
                    inh.DisplayContents = retainedStyle.DisplayContents;
                    inh.IsInlineBlock = retainedStyle.IsInlineBlock;
                    inh.FlowRoot = retainedStyle.FlowRoot;
                    inh.IsTableBox = retainedStyle.IsTableBox;
                    inh.IsTableCellBox = retainedStyle.IsTableCellBox;
                    inh.AuthoredTableDisplay = retainedStyle.AuthoredTableDisplay;
                    inh.BorderSpacing = retainedStyle.BorderSpacing ?? inh.BorderSpacing;
                    inh.Color = retainedStyle.Color ?? inh.Color;
                    inh.FontSize = retainedStyle.FontSize ?? inh.FontSize;
                    inh.FontWeight = ComputedStyle.UsedFontWeight(retainedStyle);
                    if (retainedStyle.FontFamily is { } family)
                    {
                        inh.FontFamily = family;
                        inh.FontFamilySpecified = retainedStyle.FontFamilySpecified;
                    }

                    if (retainedStyle.FontOpticalSizing is { } optical)
                    {
                        inh.FontOpticalSizing = optical;
                    }

                    if (retainedStyle.FontVariationSettings is { } settings)
                    {
                        inh.FontVariationSettings = [.. settings];
                    }

                    if (retainedStyle.LetterSpacing is { } spacing)
                    {
                        inh.LetterSpacing = spacing;
                    }

                    if (retainedStyle.LetterSpacingNonNormal is { } nonNormal)
                    {
                        inh.LetterSpacingNonNormal = nonNormal;
                    }

                    inh.ContainerType = retainedStyle.ContainerType;
                    inh.ContainerNames = [.. retainedStyle.ContainerNames];
                    if (retainedStyle.TextAlign is { } align)
                    {
                        inh.TextAlign = align;
                        inh.LegacyCenter = retainedStyle.LegacyCenter;
                    }

                    if (retainedStyle.TextIndent is { } indent)
                    {
                        inh.TextIndent = indent;
                    }

                    inh.VisibilityHidden = retainedStyle.VisibilityHidden ?? inh.VisibilityHidden;
                    inh.HasZeroOpacity |= retainedStyle.Opacity is { } opacity && opacity <= 0f;
                    inh.Svg = retainedStyle.SvgPaint ?? inh.Svg;
                    if (retainedStyle.ListStyle is { } listStyle)
                    {
                        inh.ListStyle = listStyle;
                    }

                    if (retainedStyle.LineHeight is { } lineHeight)
                    {
                        inh.LineHeight = lineHeight;
                    }

                    if (retainedStyle.WhiteSpace is { } whiteSpace)
                    {
                        inh.WhiteSpace = whiteSpace;
                    }

                    if (retainedStyle.OverflowWrap is { } overflowWrap)
                    {
                        inh.OverflowWrap = overflowWrap;
                    }

                    if (retainedStyle.WordBreak is { } wordBreak)
                    {
                        inh.WordBreak = wordBreak;
                    }

                    if (retainedStyle.TextWrapStyle is { } textWrapStyle)
                    {
                        inh.TextWrapStyle = textWrapStyle;
                    }

                    if (retainedStyle.TextTransform is { } textTransform)
                    {
                        inh.TextTransform = textTransform;
                    }

                    if (retainedStyle.FontStyleItalic is { } italic)
                    {
                        inh.Italic = italic;
                    }

                    inh.BoxSizing = retainedStyle.BoxSizing;
                    if (retainedStyle.BorderCollapse is { } collapse)
                    {
                        inh.BorderCollapse = collapse;
                    }

                    if (DomTraversal.IsAnyLocal(tree, id, "tbody", "thead", "tfoot", "tr", "td", "th")
                        && retainedStyle.VerticalAlign is { } verticalAlign)
                    {
                        inh.TableVerticalAlign = verticalAlign;
                    }

                    inh.OverflowX = retainedStyle.OverflowComputedX;
                    inh.OverflowY = retainedStyle.OverflowComputedY;
                    childCbHeightDefinite = retainedStyle.Height.Kind
                        is DimensionKind.Px or DimensionKind.Percent;
                    childCbHeight = ContentBoxBlockSize(retainedStyle, inh.CbHeight);
                    childCbHeightKnown = childCbHeightDefinite
                        && (retainedStyle.Height.Kind == DimensionKind.Px || inh.CbHeightKnown);

                    // The flex-sized rule below applies to a retained style too: the box is the
                    // same box, and which containing block its children resolve against cannot
                    // depend on whether this pass happened to recompute its style.
                    if (!childCbHeightDefinite
                        && IsFlexSizedDefiniteBlock(
                            tree, styles, id, retainedStyle, inh.CbHeightDefinite))
                    {
                        childCbHeightDefinite = true;
                        childCbHeightKnown = false;
                    }

                    if (childCbHeightDefinite)
                    {
                        definiteHeightNodes.Add(id);
                    }

                    float retainedCbWidth = inh.CbWidth;
                    float retainedUsedWidth = retainedStyle.Width.Kind switch
                    {
                        DimensionKind.Px => retainedStyle.Width.Value,
                        DimensionKind.Percent => retainedStyle.Width.Value * retainedCbWidth,
                        _ => F32.Max(
                            retainedCbWidth - retainedStyle.Margin.Left - retainedStyle.Margin.Right,
                            0f),
                    };
                    bool retainedDefiniteContentBox =
                        retainedStyle.Width.Kind is DimensionKind.Px or DimensionKind.Percent
                        && retainedStyle.BoxSizing == BoxSizing.ContentBox;
                    childCbWidth = retainedDefiniteContentBox
                        ? F32.Max(retainedUsedWidth, 0f)
                        : F32.Max(
                            retainedUsedWidth
                            - retainedStyle.Padding.Left
                            - retainedStyle.Padding.Right
                            - retainedStyle.Border.Left
                            - retainedStyle.Border.Right,
                            0f);
                }

                inh.CbWidth = childCbWidth;
                inh.CbHeightDefinite = childCbHeightDefinite;
                inh.CbHeight = childCbHeight;
                inh.CbHeightKnown = childCbHeightKnown;
                List<NodeId> retainedChildren = DomTraversal.StyleChildren(tree, id);
                for (int index = retainedChildren.Count - 1; index >= 0; index--)
                {
                    queue.Add((retainedChildren[index], inh.Clone()));
                }

                continue;
            }

            (List<Layout.TrackSizingFunction> Columns,
                List<Layout.TrackSizingFunction> Rows,
                List<object> ColumnCalcs,
                List<object> RowCalcs)? inheritedGridAutoTracks = null;
            if (styles.TryGetValue(id, out LayoutStyle? gridStyle)
                && (gridStyle.GridAutoColumnsInherit || gridStyle.GridAutoRowsInherit))
            {
                if (DomTraversal.RenderedParent(tree, id) is { } gridParent
                    && styles.TryGetValue(gridParent, out LayoutStyle? parentGridStyle))
                {
                    inheritedGridAutoTracks = (
                        [.. parentGridStyle.GridAutoColumns],
                        [.. parentGridStyle.GridAutoRows],
                        [.. GridCalcBucket(parentGridStyle, 2)],
                        [.. GridCalcBucket(parentGridStyle, 3)]);
                }
                else
                {
                    inheritedGridAutoTracks = ([], [], [], []);
                }
            }

            if (styles.TryGetValue(id, out LayoutStyle? style))
            {
                ResolveOneComputedStyle(
                    tree,
                    id,
                    style,
                    styles,
                    inh,
                    inheritedGridAutoTracks,
                    definiteHeightNodes,
                    rootFs,
                    vw,
                    vh,
                    viewport,
                    initialCbWidth,
                    out childCbWidth,
                    out childCbHeightDefinite,
                    out childCbHeight,
                    out childCbHeightKnown);
            }

            inh.CbWidth = childCbWidth;
            inh.CbHeightDefinite = childCbHeightDefinite;
            inh.CbHeight = childCbHeight;
            inh.CbHeightKnown = childCbHeightKnown;
            List<NodeId> children = DomTraversal.StyleChildren(tree, id);
            for (int index = children.Count - 1; index >= 0; index--)
            {
                queue.Add((children[index], inh.Clone()));
            }
        }
    }

    /// <summary>
    /// Fold this element's specified SVG paint properties onto the inherited ones and serialize
    /// the result the way <c>getComputedStyle</c> reports it.
    /// </summary>
    /// <remarks>
    /// The inherited instance is returned unchanged when the element specifies none of the four,
    /// which is the overwhelmingly common case, so an ordinary page allocates one record for the
    /// whole document.
    /// </remarks>
    private static SvgPaintValues ResolveSvgPaint(
        LayoutStyle    style,
        SvgPaintValues inherited,
        float          emPx,
        float          rootFs,
        float          vw,
        float          vh)
    {
        if (style.SvgFill is null
            && style.SvgStroke is null
            && style.SvgStrokeWidth is null
            && style.SvgTextAnchor is null)
        {
            return inherited;
        }

        string fill = style.SvgFill is { } specifiedFill
            ? SvgPaintCss(specifiedFill, style, inherited.Fill)
            : inherited.Fill;
        string stroke = style.SvgStroke is { } specifiedStroke
            ? SvgPaintCss(specifiedStroke, style, inherited.Stroke)
            : inherited.Stroke;
        string strokeWidth = style.SvgStrokeWidth is { } specifiedWidth
            ? SvgStrokeWidthCss(specifiedWidth, emPx, rootFs, vw, vh, inherited.StrokeWidth)
            : inherited.StrokeWidth;
        string textAnchor = style.SvgTextAnchor switch
        {
            null => inherited.TextAnchor,
            "initial" => SvgPaintValues.Initial.TextAnchor,
            "start" or "middle" or "end" => style.SvgTextAnchor,
            _ => inherited.TextAnchor,
        };

        return new SvgPaintValues(fill, stroke, strokeWidth, textAnchor);
    }

    /// <summary>Serialize one specified <c>fill</c> / <c>stroke</c> value.</summary>
    /// <remarks>
    /// A paint server (<c>url(#gradient)</c>) and the <c>none</c> / <c>context-*</c> keywords
    /// have no colour to resolve and report their own text; everything else is a colour, and
    /// <c>currentColor</c> resolves against this element's computed <c>color</c>.
    /// </remarks>
    private static string SvgPaintCss(string specified, LayoutStyle style, string inherited)
    {
        string value = specified.Trim();
        string lower = CssText.AsciiLower(value);
        if (lower is "" or "inherit" or "unset")
        {
            return inherited;
        }

        if (lower == "currentcolor")
        {
            return PaintCssValues.CssColor(style.Color ?? new RgbaColor(0, 0, 0, 255));
        }

        if (lower is "none" or "context-fill" or "context-stroke")
        {
            return lower;
        }

        if (lower.StartsWith("url(", StringComparison.Ordinal))
        {
            return value;
        }

        return CssColor.ParseForScheme(value, style.ColorSchemeDark) is { } color
            ? PaintCssValues.CssColor(color)
            : inherited;
    }

    /// <summary>Serialize one specified <c>stroke-width</c> value.</summary>
    private static string SvgStrokeWidthCss(
        string specified,
        float  emPx,
        float  rootFs,
        float  vw,
        float  vh,
        string inherited)
    {
        string value = specified.Trim();
        string lower = CssText.AsciiLower(value);
        if (lower is "" or "inherit" or "unset")
        {
            return inherited;
        }

        if (lower == "initial")
        {
            return SvgPaintValues.Initial.StrokeWidth;
        }

        Dimension dimension = ComputedStyle.DimensionValue(value);
        if (dimension.Kind == DimensionKind.Percent)
        {
            return PaintCssValues.CssNumber(dimension.Value * 100f) + "%";
        }

        Dimension resolved = dimension.Resolve(emPx, rootFs, vw, vh);

        return resolved is { Kind: DimensionKind.Px } px && float.IsFinite(px.Value)
            ? PaintCssValues.CssPx(px.Value)
            : inherited;
    }

    private static void ResolveOneComputedStyle(
        DomTree tree,
        NodeId id,
        LayoutStyle style,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        Inherited inh,
        (List<Layout.TrackSizingFunction> Columns,
            List<Layout.TrackSizingFunction> Rows,
            List<object> ColumnCalcs,
            List<object> RowCalcs)? inheritedGridAutoTracks,
        HashSet<NodeId> definiteHeightNodes,
        float rootFs,
        float vw,
        float vh,
        (float Width, float Height) viewport,
        float initialCbWidth,
        out float childCbWidth,
        out bool childCbHeightDefinite,
        out float childCbHeight,
        out bool childCbHeightKnown)
    {
        if (style.GridAutoColumnsInherit)
        {
            style.GridAutoColumns = inheritedGridAutoTracks is { } tracks ? [.. tracks.Columns] : [];
            SetGridCalcBucket(
                style, 2, inheritedGridAutoTracks is { } calc ? [.. calc.ColumnCalcs] : []);
            style.GridAutoColumnsInherit = false;
        }

        if (style.GridAutoRowsInherit)
        {
            style.GridAutoRows = inheritedGridAutoTracks is { } tracks ? [.. tracks.Rows] : [];
            SetGridCalcBucket(
                style, 3, inheritedGridAutoTracks is { } calc ? [.. calc.RowCalcs] : []);
            style.GridAutoRowsInherit = false;
        }

        if (style.DisplayInherit)
        {
            style.Display = inh.Display;
            style.DisplayContents = inh.DisplayContents;
            style.IsInlineBlock = inh.IsInlineBlock;
            style.FlowRoot = inh.FlowRoot;
            style.IsTableBox = inh.IsTableBox;
            style.IsTableCellBox = inh.IsTableCellBox;

            // An internal table display is recorded rather than laid out, so it lives beside
            // the layout display instead of in it and has to be copied with it. Measured on
            // Chromium 141, a `display: inherit` child of a `display: table-row` box reports
            // `table-row`; without this it reported `block`.
            style.AuthoredTableDisplay = inh.AuthoredTableDisplay;

            // Reconstruct the internal cell-content wrapper only when the inherited computed
            // display is table-cell.
            style.InternalFlexContainer = style.IsTableCellBox;
            if (style.IsTableCellBox)
            {
                style.FlexDirection = TaffyFlexDirection.Column;
                style.AlignItems = TaffyAlignItems.FlexStart;
            }

            style.DisplayInherit = false;
        }

        inh.Display = style.Display;
        if (style.Direction is { } direction)
        {
            inh.Direction = direction;
        }
        else
        {
            style.Direction = inh.Direction;
        }

        ComputedStyle.ResolveLogicalBorders(style);
        inh.DisplayContents = style.DisplayContents;
        inh.IsInlineBlock = style.IsInlineBlock;
        inh.FlowRoot = style.FlowRoot;
        inh.IsTableBox = style.IsTableBox;
        inh.IsTableCellBox = style.IsTableCellBox;
        inh.AuthoredTableDisplay = style.AuthoredTableDisplay;

        if (style.Color is { } color)
        {
            inh.Color = color;
        }
        else
        {
            style.Color = inh.Color;
        }

        // Resolve a relative font-size against the PARENT (em/%) or ROOT (rem) font-size
        // before inheriting it downward.
        float parentFs = inh.FontSize ?? 16f;
        if (style.FontSizeExpression is { } fontSizeExpression)
        {
            style.FontSize = ComputedStyle.ResolveContextualLength(
                fontSizeExpression, parentFs, rootFs, vw, vh, parentFs);
        }
        else if (style.FontSizeRaw is { } raw)
        {
            float resolved = raw.Kind switch
            {
                DimensionKind.Percent => parentFs * raw.Value,
                DimensionKind.Em => parentFs * raw.Value,
                _ => raw.Resolve(parentFs, rootFs, vw, vh) is { Kind: DimensionKind.Px } px
                    ? px.Value
                    : parentFs,
            };
            style.FontSize = resolved;
        }

        if (style.FontSize is { } fontSize)
        {
            inh.FontSize = fontSize;
        }
        else
        {
            style.FontSize = inh.FontSize;
        }

        // em in non-font-size properties is relative to this element's OWN computed font-size.
        float emPx = style.FontSize ?? parentFs;

        // `fill` / `stroke` / `stroke-width` / `text-anchor` all inherit, and all four need the
        // element's own computed colour and font-size to serialize, so they resolve here.
        inh.Svg = ResolveSvgPaint(style, inh.Svg, emPx, rootFs, vw, vh);
        style.SvgPaint = inh.Svg;
        ComputedStyle.SetGridCalcContext(style, emPx, rootFs, vw, vh);
        ComputedStyle.ResolveFontRelativeDeclarations(style, emPx, rootFs, vw, vh);

        // `border-spacing` is inherited, and `inherit` / `unset` are the only way an element
        // reads a value it did not declare: nothing else carries one down, so only the
        // declaring element holds one and PreparedRender walks up to find it. This sits after
        // the font-relative re-read so a `border-spacing: 1em` a descendant inherits is the
        // value the element ends up with, not the one the cascade guessed at 16px.
        if (style.BorderSpacingInherit)
        {
            style.BorderSpacing = inh.BorderSpacing;
            style.BorderSpacingInherit = false;
        }

        if (style.BorderSpacing is { } declaredBorderSpacing)
        {
            inh.BorderSpacing = declaredBorderSpacing;
        }
        if (style.LetterSpacingExpression is { } letterSpacingExpression)
        {
            style.LetterSpacing = ComputedStyle.ResolveContextualLength(
                letterSpacingExpression, emPx, rootFs, vw, vh, emPx);
        }
        else if (style.LetterSpacingRaw is { } letterSpacingRaw)
        {
            Dimension resolved = letterSpacingRaw.Resolve(emPx, rootFs, vw, vh);
            style.LetterSpacing = resolved.Kind == DimensionKind.Px && float.IsFinite(resolved.Value)
                ? resolved.Value
                : null;
        }

        if (style.LetterSpacing is { } spacing && float.IsFinite(spacing))
        {
            inh.LetterSpacing = spacing;
        }
        else
        {
            style.LetterSpacing = inh.LetterSpacing;
        }

        if (style.LetterSpacingNonNormal is { } nonNormal)
        {
            inh.LetterSpacingNonNormal = nonNormal;
        }
        else
        {
            style.LetterSpacingNonNormal = inh.LetterSpacingNonNormal;
        }

        if (style.ContainerTypeInherit)
        {
            style.ContainerType = inh.ContainerType;
        }

        if (style.ContainerNamesInherit)
        {
            style.ContainerNames = [.. inh.ContainerNames];
        }

        inh.ContainerType = style.ContainerType;
        inh.ContainerNames = [.. style.ContainerNames];
        if (style.OverflowInheritX)
        {
            style.OverflowSpecifiedX = inh.OverflowX;
            style.OverflowInheritX = false;
        }

        if (style.OverflowInheritY)
        {
            style.OverflowSpecifiedY = inh.OverflowY;
            style.OverflowInheritY = false;
        }

        ComputedStyle.RecomputeOverflow(style);
        inh.OverflowX = style.OverflowComputedX;
        inh.OverflowY = style.OverflowComputedY;
        if (style.RowGapExpression is { } rowGapExpression)
        {
            style.RowGap = ComputedStyle.ResolveContextualLength(
                rowGapExpression, emPx, rootFs, vw, vh, inh.CbWidth);
        }

        if (style.ColumnGapExpression is { } columnGapExpression)
        {
            style.ColumnGap = ComputedStyle.ResolveContextualLength(
                columnGapExpression, emPx, rootFs, vw, vh, inh.CbWidth);
        }

        if (style.LineHeightExpression is { } lineHeightExpression)
        {
            if (ComputedStyle.ResolveContextualLength(
                    lineHeightExpression, emPx, rootFs, vw, vh, emPx) is { } resolvedLineHeight)
            {
                style.LineHeight = ComputedStyle.LineHeightExpressionIsLength(lineHeightExpression)
                    ? LineHeight.Px(resolvedLineHeight)
                    : LineHeight.Ratio(resolvedLineHeight);
            }
        }
        else if (style.LineHeight is { Kind: LineHeightKind.Relative } relativeLineHeight)
        {
            Dimension relative = relativeLineHeight.Length;
            float pixels = relative.Kind == DimensionKind.Percent
                ? emPx * relative.Value
                : relative.Resolve(emPx, rootFs, vw, vh) is { Kind: DimensionKind.Px } resolvedPx
                    ? resolvedPx.Value
                    : emPx;
            style.LineHeight = LineHeight.Px(pixels);
        }

        float cbW = inh.CbWidth;

        // DEVIATION from crates/obscura-render/src/style.rs, which drops the CSS-wide keyword
        // `inherit` on the box-size properties. They are not inherited properties, so the
        // keyword has to copy the parent's computed value explicitly - the parent is already
        // resolved at this point because this pass runs top-down. Tesserae's annotated text
        // editor sizes its textarea with `min-height: inherit` off a per-instance container
        // and got the initial value, so every editor collapsed to one row (58px against
        // Chromium's 160/120/80). See "Known deviations" in todo.md.
        if (style.SizeInherit != 0
            && DomTraversal.RenderedParent(tree, id) is { } sizeInheritParent
            && styles.TryGetValue(sizeInheritParent, out LayoutStyle? sizeInheritFrom))
        {
            for (int index = 0; index < 6; index++)
            {
                if ((style.SizeInherit & (1 << index)) == 0)
                {
                    continue;
                }

                style.SetSizeExpression(index, sizeInheritFrom.SizeExpressions[index]);
                style.SetSizeIntrinsicKeyword(index, sizeInheritFrom.SizeIntrinsicKeyword(index));
                switch (index)
                {
                    case 0: style.Width = sizeInheritFrom.Width; break;
                    case 1: style.Height = sizeInheritFrom.Height; break;
                    case 2: style.MinWidth = sizeInheritFrom.MinWidth; break;
                    case 3: style.MinHeight = sizeInheritFrom.MinHeight; break;
                    case 4: style.MaxWidth = sizeInheritFrom.MaxWidth; break;
                    default: style.MaxHeight = sizeInheritFrom.MaxHeight; break;
                }
            }
        }

        for (int index = 0; index < 6; index++)
        {
            if (style.SizeCalc is { } clearedSize)
            {
                clearedSize[index] = null;
            }

            if (style.SizeExpressions[index] is not { } expression)
            {
                continue;
            }

            // A proper replaced box's cyclic percentage maximum has a distinct
            // intrinsic-sizing rule.
            if (index == 4 && style.HasReplacedSizing
                && ComputedStyle.FunctionalPercentageFactor(expression) is { } percent)
            {
                style.MaxWidth = Dimension.Percent(percent);
                continue;
            }

            // DEVIATION from crates/obscura-render/src/dom.rs, which uses `viewport.1` as
            // the percentage basis for every block-axis slot. A block-axis percentage
            // resolves against the containing block's content-box HEIGHT, and is
            // indefinite when that height is: Chromium computes `height: calc(100% - 4px)`
            // under an auto-height parent to `auto`. Taffy applies both rules itself for a
            // bare percentage, but a functional one has to be flattened to px here, so the
            // rules have to be applied here too. The viewport basis made Tesserae's
            // `.tss-card` (`height: calc(100% - 4px)`) a full viewport tall in every
            // sample. See "Known deviations" in todo.md.
            bool blockAxis = index is 1 or 3 or 5;
            if (blockAxis
                && expression.Contains('%', StringComparison.Ordinal)
                && !inh.CbHeightKnown
                && style.Position != TaffyPosition.Absolute)
            {
                switch (index)
                {
                    case 1: style.Height = Dimension.Auto; break;
                    case 3: style.MinHeight = Dimension.Auto; break;
                    default: style.MaxHeight = Dimension.Auto; break;
                }

                continue;
            }

            // DEVIATION from crates/obscura-render/src/dom.rs, which has no counterpart:
            // an INLINE-axis percentage inside a functional size is handed to taffy as a
            // late-resolved calc() value, the same way a functional inset is above. `cbW` is
            // this pass's own block-flow estimate of the containing block, and it is only an
            // estimate - it is computed before the box it describes has been laid out, so a
            // container that settles at a different width leaves it stale. Tesserae's sidebar
            // resolved `width: calc(100% + 32px)` against 176px while the containing block it
            // reports is 191.141px, which pulled the whole sidebar subtree 15px narrow. Taffy
            // resolves an inline percentage against the used containing block, so the
            // percentage is left for it. The block axis keeps the flattening above: its
            // definite/indefinite rules are applied here, not by taffy. See "Known
            // deviations" in todo.md.
            if (!blockAxis
                && expression.Contains('%', StringComparison.Ordinal)
                && GridCalcExpression.Parse(expression) is { } lateSize)
            {
                lateSize.SetContext(emPx, rootFs, vw, vh);
                (style.SizeCalc ??= new GridCalcExpression?[6])[index] = lateSize;
            }

            float percentBase = blockAxis ? inh.CbHeight : cbW;
            if (ComputedStyle.ResolveContextualLength(
                    expression, emPx, rootFs, vw, vh, percentBase) is not { } px)
            {
                continue;
            }

            Dimension value = Dimension.Px(px);
            switch (index)
            {
                case 0: style.Width = value; break;
                case 1: style.Height = value; break;
                case 2: style.MinWidth = value; break;
                case 3: style.MinHeight = value; break;
                case 4: style.MaxWidth = value; break;
                default: style.MaxHeight = value; break;
            }
        }

        style.Width = style.Width.Resolve(emPx, rootFs, vw, vh);
        style.Height = style.Height.Resolve(emPx, rootFs, vw, vh);
        style.MinWidth = style.MinWidth.Resolve(emPx, rootFs, vw, vh);
        style.MinHeight = style.MinHeight.Resolve(emPx, rootFs, vw, vh);
        style.MaxWidth = style.MaxWidth.Resolve(emPx, rootFs, vw, vh);
        style.MaxHeight = style.MaxHeight.Resolve(emPx, rootFs, vw, vh);
        style.FlexBasis = style.FlexBasis.Resolve(emPx, rootFs, vw, vh);
        // DEVIATION from crates/obscura-render/src/dom.rs, which drops a block-axis
        // percentage whenever the parent box has no definite height. A grid item's
        // containing block is its GRID AREA, not the grid container's content box, so the
        // container's own height says nothing about whether the percentage is resolvable:
        // `grid-template-rows: 24px` gives a definite area under an auto-height container.
        // Taffy resolves a grid item's size against the grid area itself (grid/alignment.rs
        // `align_and_position_item`, and `GridItem.KnownDimensions` passes `None` for a
        // track that is still indefinite), so the percentage has to survive this pass and
        // reach it. The reference computed it to `auto` and every item laid out 0px tall -
        // Tesserae's `.tss-gridpicker` cells (`height: 100%` in a 24px row with
        // `align-items: center`, so no stretch to mask it) collapsed to 24x2. See "Known
        // deviations" in todo.md.
        // Only a percentage height asks the question, and the parent walk is not free, so it
        // stays behind that test: every other box skips it.
        bool isGridItem = style.Height.Kind == DimensionKind.Percent
            && style.Position != TaffyPosition.Absolute
            && DomTraversal.RenderedParent(tree, id) is { } gridAreaParent
            && styles.TryGetValue(gridAreaParent, out LayoutStyle? gridAreaContainer)
            && gridAreaContainer.Display == Display.Grid;

        if (style.Height.Kind == DimensionKind.Percent
            && !inh.CbHeightDefinite
            && !isGridItem
            && style.Position != TaffyPosition.Absolute)
        {
            style.Height = Dimension.Auto;
        }

        childCbHeightDefinite = style.Height.Kind is DimensionKind.Px or DimensionKind.Percent;
        childCbHeight = ContentBoxBlockSize(style, inh.CbHeight);

        // A percentage height only yields a number when its own basis was one. A grid item's
        // basis is the grid area, which this pass cannot see, so it stays definite-but-unknown.
        childCbHeightKnown = childCbHeightDefinite
            && (style.Height.Kind == DimensionKind.Px || (inh.CbHeightKnown && !isGridItem));

        // DEVIATION from crates/obscura-render/src/dom.rs, which calls a box's block size
        // definite only when `height` itself is a length or percentage. CSS Flexbox 9.8 also
        // makes a flex item's main size definite when it has a definite flex basis in a
        // container with a definite main size, and Chromium resolves descendant percentage
        // heights against it. Tesserae's time-histogram bars are `height: 100%` inside a
        // `flex: 1 1 120px` column item, so the reference computed them to `auto` and every
        // bar laid out 0px tall - the chart rendered as an empty box. See "Known deviations"
        // in todo.md.
        if (!childCbHeightDefinite
            && style.FlexBasis.Kind == DimensionKind.Px
            && style.FlexBasis.Value > 0f
            && DomTraversal.RenderedParent(tree, id) is { } flexBasisParent
            && styles.TryGetValue(flexBasisParent, out LayoutStyle? flexBasisContainer)
            && flexBasisContainer.Display == Display.Flex
            && flexBasisContainer.FlexDirection
                is TaffyFlexDirection.Column or TaffyFlexDirection.ColumnReverse)
        {
            childCbHeightDefinite = true;
            childCbHeightKnown = true;
            childCbHeight = style.BoxSizing == BoxSizing.ContentBox
                ? style.FlexBasis.Value
                : F32.Max(
                    style.FlexBasis.Value
                    - style.Padding.Top
                    - style.Padding.Bottom
                    - style.Border.Top
                    - style.Border.Bottom,
                    0f);
        }

        // DEVIATION from crates/obscura-render/src/dom.rs, which calls a box's block size
        // definite only when `height` itself is a length or percentage. A flex item sized by
        // the flex algorithm has no such `height`, so the reference made it an INDEFINITE
        // containing block and every descendant `height: %` under it computed to `auto`.
        // CSS Flexbox 9.8 says otherwise, in two halves that Chromium both implements:
        // a flex item's post-flexing MAIN size is definite whenever the container's main size
        // is, and a stretched item's CROSS size is definite whenever the container's cross
        // size is. The block axis is the main axis of a column container and the cross axis of
        // a row one, so either half can make this box a definite containing block.
        // Tesserae nests a `height: 100%` column inside a `flex-grow: 1` item carrying no
        // height of its own, so the reference collapsed that whole chain to 0 and the
        // connect-apps grid clipped 1676px of cards into an 8px box.
        // The post-flex size is not knowable in this top-down pass - it is decided by the flex
        // algorithm later - so the box is marked definite but NOT known: a bare percentage
        // survives as a percentage and taffy resolves it against the used size, exactly as the
        // grid-item case above does. A functional `calc()` percentage still flattens to `auto`,
        // which is the same residual gap the grid case accepts. See "Known deviations" in
        // todo.md.
        if (!childCbHeightDefinite
            && IsFlexSizedDefiniteBlock(tree, styles, id, style, inh.CbHeightDefinite))
        {
            childCbHeightDefinite = true;
            childCbHeightKnown = false;
        }

        if (childCbHeightDefinite)
        {
            definiteHeightNodes.Add(id);
        }

        // DEVIATION from crates/obscura-render/src/dom.rs, which flattens a functional inset
        // to px here against the viewport height (block axis) or this element's own
        // containing-block width (inline axis). Neither is the basis a box offset resolves
        // against: an absolutely positioned box takes the PADDING BOX of its nearest
        // positioned ancestor, which this top-down style pass cannot know - the ancestor's
        // used size is settled by layout, and for an auto-height ancestor it is not knowable
        // beforehand at all. `top: calc(50% - 5px)` in a 34px-tall relative container came out
        // as 355px (half the 720px viewport) instead of 12px, which put every Tesserae
        // dropdown chevron hundreds of pixels below its combobox. Taffy already resolves a
        // bare percentage inset against the real containing block, and its calc() support
        // resolves an opaque handle the same way, so the expression is handed over as a
        // late-resolved value instead. See "Known deviations" in todo.md.
        //
        // The one basis taffy does not have is the block axis of a RELATIVE box: it resolves
        // those against a hard 0 (BlockLayout's `ZipSize(new Size(containerInnerWidth, 0))`)
        // because the container's height is not final where the offset is applied. So a
        // relative block-axis offset keeps being flattened, against the containing block's
        // content-box height, and becomes `auto` when that height is indefinite - which is
        // what Chromium computes for a percentage offset it cannot resolve.
        bool lateResolvedInsets = style.Position == TaffyPosition.Absolute && !style.PositionSticky;
        for (int index = 0; index < 4; index++)
        {
            if (style.InsetExpressions[index] is not { } expression)
            {
                if (style.InsetCalc is { } cleared)
                {
                    cleared[index] = null;
                }

                continue;
            }

            bool blockAxisInset = index is 0 or 2;
            bool percentBearing = expression.Contains('%', StringComparison.Ordinal);
            GridCalcExpression? late = percentBearing && (lateResolvedInsets || !blockAxisInset)
                ? GridCalcExpression.Parse(expression, allowNegative: true)
                : null;
            late?.SetContext(emPx, rootFs, vw, vh);
            (style.InsetCalc ??= new GridCalcExpression?[4])[index] = late;

            if (percentBearing && blockAxisInset && !lateResolvedInsets && !inh.CbHeightKnown)
            {
                style.SetInset(index, null);
                continue;
            }

            // The flattened value is what layout reads on the paths above that keep it, and
            // what the readers layout does not feed read throughout: sticky offsets, an inline
            // box's relative shift, pseudo-element paint and the computed-style projection.
            float percentBase = blockAxisInset
                ? (inh.CbHeightKnown ? inh.CbHeight : viewport.Height)
                : cbW;
            float? resolved = ComputedStyle.ResolveContextualLength(
                expression, emPx, rootFs, vw, vh, percentBase);
            style.SetInset(index, resolved is { } value ? Dimension.Px(value) : null);
        }

        for (int index = 0; index < 4; index++)
        {
            if (style.Inset[index] is { } inset)
            {
                style.SetInset(index, inset.Resolve(emPx, rootFs, vw, vh));
            }
        }

        // A fixed box is positioned against the initial containing block.
        if (style.PositionFixed)
        {
            if (style.Width.IsAuto
                && style.Inset[1] is { Kind: DimensionKind.Px } right
                && style.Inset[3] is { Kind: DimensionKind.Px } left)
            {
                style.Width = Dimension.Px(F32.Max(initialCbWidth - left.Value - right.Value, 0f));
            }

            if (style.Height.IsAuto
                && style.Inset[0] is { Kind: DimensionKind.Px } top
                && style.Inset[2] is { Kind: DimensionKind.Px } bottom)
            {
                style.Height = Dimension.Px(F32.Max(viewport.Height - top.Value - bottom.Value, 0f));
            }

            childCbHeightDefinite = style.Height.Kind is DimensionKind.Px or DimensionKind.Percent;
            childCbHeight = ContentBoxBlockSize(style, inh.CbHeight);
            childCbHeightKnown = childCbHeightDefinite
                && (style.Height.Kind == DimensionKind.Px || inh.CbHeightKnown);
        }

        ushort computedWeight = ComputedStyle.ComputedFontWeight(style.FontWeight, inh.FontWeight);
        style.FontWeight = computedWeight.ToString(CultureInfo.InvariantCulture);
        inh.FontWeight = computedWeight;
        if (style.FontFamily is { } fontFamily)
        {
            inh.FontFamily = fontFamily;
            inh.FontFamilySpecified = style.FontFamilySpecified;
        }
        else
        {
            style.FontFamily = inh.FontFamily;
            style.FontFamilySpecified = inh.FontFamilySpecified;
        }

        if (style.Cursor is { } cursor)
        {
            inh.Cursor = cursor;
        }
        else
        {
            style.Cursor = inh.Cursor;
        }

        if (style.PointerEvents is { } pointerEvents)
        {
            inh.PointerEvents = pointerEvents;
        }
        else
        {
            style.PointerEvents = inh.PointerEvents;
        }

        if (style.FontOpticalSizing is { } opticalSizing)
        {
            inh.FontOpticalSizing = opticalSizing;
        }
        else
        {
            style.FontOpticalSizing = inh.FontOpticalSizing;
        }

        if (style.FontVariationSettings is { } variationSettings)
        {
            inh.FontVariationSettings = [.. variationSettings];
        }
        else
        {
            style.FontVariationSettings = [.. inh.FontVariationSettings];
        }

        bool isTable = DomTraversal.IsLocal(tree, id, "table");
        if (isTable && inh.LegacyCenter && style.TextAlign is null)
        {
            // The vendor alignment used by <center> centers the table outer box but does not
            // leak into its internal formatting context.
            style.TextAlign = TaffyAlignItems.FlexStart;
            style.LegacyCenter = false;
            inh.TextAlign = style.TextAlign;
            inh.LegacyCenter = false;
        }
        else if (style.TextAlign is { } textAlign)
        {
            inh.TextAlign = textAlign;
            inh.LegacyCenter = style.LegacyCenter;
        }
        else
        {
            style.TextAlign = inh.TextAlign;
            style.LegacyCenter = inh.LegacyCenter;
        }

        if (style.TextIndent is { } textIndent)
        {
            Dimension indent = textIndent.Resolve(emPx, rootFs, vw, vh);
            style.TextIndent = indent;
            inh.TextIndent = indent;
        }
        else
        {
            style.TextIndent = inh.TextIndent;
        }

        inh.VisibilityHidden = style.VisibilityHidden ?? inh.VisibilityHidden;
        inh.HasZeroOpacity |= style.Opacity is { } opacity && opacity <= 0f;
        style.EffectivelyInvisible = inh.VisibilityHidden || inh.HasZeroOpacity;
        if (style.ListStyle is { } listStyle)
        {
            inh.ListStyle = listStyle;
        }
        else
        {
            style.ListStyle = inh.ListStyle;
        }

        if (style.LineHeight is { } lineHeight)
        {
            inh.LineHeight = lineHeight;
        }
        else
        {
            style.LineHeight = inh.LineHeight;
        }

        if (style.WhiteSpace is { } whiteSpace)
        {
            inh.WhiteSpace = whiteSpace;
        }
        else
        {
            style.WhiteSpace = inh.WhiteSpace;
        }

        if (style.OverflowWrap is { } overflowWrap)
        {
            inh.OverflowWrap = overflowWrap;
        }
        else
        {
            style.OverflowWrap = inh.OverflowWrap;
        }

        if (style.WordBreak is { } wordBreak)
        {
            inh.WordBreak = wordBreak;
        }
        else
        {
            style.WordBreak = inh.WordBreak;
        }

        if (style.TextWrapStyle is { } textWrapStyle)
        {
            inh.TextWrapStyle = textWrapStyle;
        }
        else
        {
            style.TextWrapStyle = inh.TextWrapStyle;
        }

        if (style.TextTransform is { } textTransform)
        {
            inh.TextTransform = textTransform;
        }
        else
        {
            style.TextTransform = inh.TextTransform;
        }

        if (style.FontStyleItalic is { } italic)
        {
            inh.Italic = italic;
        }
        else
        {
            style.FontStyleItalic = inh.Italic;
        }

        if (style.BoxSizing == BoxSizing.Inherit)
        {
            style.BoxSizing = inh.BoxSizing;
        }

        inh.BoxSizing = style.BoxSizing;
        if (style.BorderCollapse is { } borderCollapse)
        {
            inh.BorderCollapse = borderCollapse;
        }
        else
        {
            style.BorderCollapse = inh.BorderCollapse;
        }

        if (DomTraversal.IsAnyLocal(tree, id, "tbody", "thead", "tfoot", "tr", "td", "th"))
        {
            if (style.VerticalAlign is { } verticalAlign)
            {
                inh.TableVerticalAlign = verticalAlign;
            }
            else
            {
                style.VerticalAlign = inh.TableVerticalAlign;
            }
        }

        // Resolve font/viewport-relative box edges now that their reference sizes are known.
        Edges padding = style.Padding;
        Edges margin = style.Margin;
        for (int i = 0; i < 4; i++)
        {
            if (style.PaddingExpressions[i] is { } paddingExpression
                && ComputedStyle.ResolveContextualLength(
                    paddingExpression, emPx, rootFs, vw, vh, cbW) is { } paddingPx)
            {
                padding = SetEdge(padding, i, F32.Max(paddingPx, 0f));
            }

            if (style.MarginExpressions[i] is { } marginExpression
                && ComputedStyle.ResolveContextualLength(
                    marginExpression, emPx, rootFs, vw, vh, cbW) is { } marginPx)
            {
                margin = SetEdge(margin, i, marginPx);
            }

            if (style.PaddingRelative[i] is { } paddingRelative
                && paddingRelative.Resolve(emPx, rootFs, vw, vh) is { Kind: DimensionKind.Px } rp)
            {
                padding = SetEdge(padding, i, F32.Max(rp.Value, 0f));
            }

            if (style.MarginRelative[i] is { } marginRelative
                && marginRelative.Resolve(emPx, rootFs, vw, vh) is { Kind: DimensionKind.Px } rm)
            {
                margin = SetEdge(margin, i, rm.Value);
            }

            if (style.MarginPercent[i] is { } marginFraction)
            {
                margin = SetEdge(margin, i, marginFraction * cbW);
            }
        }

        style.Padding = padding;
        style.Margin = margin;

        SettlePseudos(style, inh, emPx, parentFs, rootFs, vw, vh, viewport, cbW);

        // Containing-block width handed to this element's children is its own content-box
        // width.
        float usedW = style.Width.Kind switch
        {
            DimensionKind.Px => style.Width.Value,
            DimensionKind.Percent => style.Width.Value * cbW,
            _ => F32.Max(cbW - style.Margin.Left - style.Margin.Right, 0f),
        };
        bool definiteContentBox = style.Width.Kind is DimensionKind.Px or DimensionKind.Percent
            && style.BoxSizing == BoxSizing.ContentBox;
        childCbWidth = definiteContentBox
            ? F32.Max(usedW, 0f)
            : F32.Max(
                usedW
                - style.Padding.Left
                - style.Padding.Right
                - style.Border.Left
                - style.Border.Right,
                0f);
    }

    /// <summary>
    /// Whether the flex algorithm gives this box a definite block size even though its own
    /// <c>height</c> is not a length or a percentage, which makes it a definite containing
    /// block for a descendant's percentage height.
    /// </summary>
    /// <remarks>
    /// CSS Flexbox 9.8 in two halves, both of which Chromium implements: a flex item's
    /// post-flexing MAIN size is definite whenever the container's main size is definite, and
    /// a stretched item's CROSS size is definite whenever the container's cross size is. The
    /// block axis is the main axis of a column container and the cross axis of a row one, so
    /// each half covers one <c>flex-direction</c>. The
    /// <paramref name="containingBlockHeightDefinite"/> flag is the container's content-box
    /// block size definiteness, which is the container size both halves ask about.
    /// </remarks>
    private static bool IsFlexSizedDefiniteBlock(
        DomTree tree,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        NodeId id,
        LayoutStyle style,
        bool containingBlockHeightDefinite)
    {
        // An absolutely positioned box is not a flex item; its containing block is the padding
        // box of its nearest positioned ancestor.
        if (!containingBlockHeightDefinite || style.Position == TaffyPosition.Absolute)
        {
            return false;
        }

        if (DomTraversal.RenderedParent(tree, id) is not { } parent
            || !styles.TryGetValue(parent, out LayoutStyle? container)
            || container.Display != Display.Flex)
        {
            return false;
        }

        return container.FlexDirection
                is TaffyFlexDirection.Column or TaffyFlexDirection.ColumnReverse
            || IsStretchedFlexItem(style, container);
    }

    /// <summary>
    /// Whether a row flex container stretches this item across the cross (block) axis, which
    /// is what CSS Flexbox 9.8 makes its cross size definite. The three conditions are the
    /// ones <c>FlexboxLayout.DetermineUsedCrossSize</c> itself applies, so a box counted here
    /// is a box taffy will actually stretch.
    /// </summary>
    private static bool IsStretchedFlexItem(LayoutStyle style, LayoutStyle container)
    {
        var alignSelf = style.AlignSelf ?? container.AlignItems ?? TaffyAlignItems.Stretch;

        return alignSelf.Keyword
                is Layout.AlignItemsKeyword.Stretch or Layout.AlignItemsKeyword.Normal
            && style.Height.IsAuto
            && !style.MarginAuto[0]
            && !style.MarginAuto[2];
    }

    /// <summary>
    /// The content-box block size a definite-height box hands its children as a
    /// percentage basis. Mirrors the inline-axis rule: a definite content-box height is
    /// already the value, and a border-box or auto height includes padding and border,
    /// which must come off. An indefinite height yields 0, which callers only read behind
    /// <c>CbHeightDefinite</c>.
    /// </summary>
    /// <remarks>
    /// DEVIATION: no counterpart in crates/obscura-render/src/dom.rs. See the remarks on
    /// <c>Inherited.CbHeight</c>.
    /// </remarks>
    private static float ContentBoxBlockSize(LayoutStyle style, float parentCbHeight)
    {
        float used = style.Height.Kind switch
        {
            DimensionKind.Px => style.Height.Value,
            DimensionKind.Percent => style.Height.Value * parentCbHeight,
            _ => float.NaN,
        };
        if (float.IsNaN(used))
        {
            return 0f;
        }

        return style.BoxSizing == BoxSizing.ContentBox
            ? F32.Max(used, 0f)
            : F32.Max(
                used
                - style.Padding.Top
                - style.Padding.Bottom
                - style.Border.Top
                - style.Border.Bottom,
                0f);
    }

    /// <summary>
    /// Refresh the viewport basis of the late-resolved expressions a retained style carries.
    /// A retained style skips <c>ResolveOneComputedStyle</c>, so this is the only place a
    /// `calc(50% - 2vh)` inset hears about a new layout viewport. Mirrors what
    /// <c>ComputedStyle.SetGridCalcContext</c> does for the grid track buckets.
    /// </summary>
    private static void SetLateCalcContext(
        GridCalcExpression?[]? expressions,
        float emPx,
        float remPx,
        float vw,
        float vh)
    {
        if (expressions is null)
        {
            return;
        }

        foreach (GridCalcExpression? expression in expressions)
        {
            expression?.SetContext(emPx, remPx, vw, vh);
        }
    }

    private static Edges SetEdge(Edges edges, int index, float value) => index switch
    {
        0 => edges with { Top = value },
        1 => edges with { Right = value },
        2 => edges with { Bottom = value },
        _ => edges with { Left = value },
    };

    /// <summary>
    /// Pseudo-elements inherit the originating element's COMPUTED values, so resolve their
    /// authored relative values against the host and then fill every omitted inherited
    /// property from the host's now-final computed style.
    /// </summary>
    private static void SettlePseudos(
        LayoutStyle style,
        Inherited inh,
        float emPx,
        float parentFs,
        float rootFs,
        float vw,
        float vh,
        (float Width, float Height) viewport,
        float cbW)
    {
        _ = inh;
        RgbaColor? hostColor = style.Color;
        float hostFontSize = style.FontSize ?? parentFs;
        float hostLetterSpacing = style.LetterSpacing ?? 0f;
        bool hostLetterSpacingNonNormal = style.LetterSpacingNonNormal ?? false;
        ushort hostWeight = ComputedStyle.UsedFontWeight(style);
        string? hostFamily = style.FontFamily;
        string? hostFamilySpecified = style.FontFamilySpecified;
        string? hostCursor = style.Cursor;
        string? hostPointerEvents = style.PointerEvents;
        FontOpticalSizing? hostOpticalSizing = style.FontOpticalSizing;
        List<FontVariationSetting>? hostVariationSettings = style.FontVariationSettings;
        LineHeight? hostLineHeight = style.LineHeight;
        WhiteSpace? hostWhiteSpace = style.WhiteSpace;
        OverflowWrap? hostOverflowWrap = style.OverflowWrap;
        WordBreak? hostWordBreak = style.WordBreak;
        TextWrapStyle? hostTextWrapStyle = style.TextWrapStyle;
        TextTransform? hostTransform = style.TextTransform;
        bool? hostItalic = style.FontStyleItalic;
        TaffyAlignItems? hostTextAlign = style.TextAlign;
        Dimension? hostTextIndent = style.TextIndent;
        bool hostInvisible = style.EffectivelyInvisible;
        Display hostDisplay = style.Display;
        TaffyDirection hostDirection = style.Direction ?? TaffyDirection.Ltr;
        bool hostDisplayContents = style.DisplayContents;
        bool hostIsInlineBlock = style.IsInlineBlock;
        bool hostFlowRoot = style.FlowRoot;
        bool hostIsTableBox = style.IsTableBox;
        bool hostIsTableCellBox = style.IsTableCellBox;
        TableInternalDisplay hostAuthoredTableDisplay = style.AuthoredTableDisplay;
        List<Layout.TrackSizingFunction> hostGridAutoColumns = [.. style.GridAutoColumns];
        List<Layout.TrackSizingFunction> hostGridAutoRows = [.. style.GridAutoRows];
        List<object> hostGridAutoColumnCalcs = [.. GridCalcBucket(style, 2)];
        List<object> hostGridAutoRowCalcs = [.. GridCalcBucket(style, 3)];
        byte hostOverflowX = style.OverflowComputedX;
        byte hostOverflowY = style.OverflowComputedY;

        void Settle(LayoutStyle pseudo)
        {
            pseudo.Direction ??= hostDirection;
            ComputedStyle.ResolveLogicalBorders(pseudo);
            if (pseudo.GridAutoColumnsInherit)
            {
                pseudo.GridAutoColumns = [.. hostGridAutoColumns];
                SetGridCalcBucket(pseudo, 2, [.. hostGridAutoColumnCalcs]);
                pseudo.GridAutoColumnsInherit = false;
            }

            if (pseudo.GridAutoRowsInherit)
            {
                pseudo.GridAutoRows = [.. hostGridAutoRows];
                SetGridCalcBucket(pseudo, 3, [.. hostGridAutoRowCalcs]);
                pseudo.GridAutoRowsInherit = false;
            }

            if (pseudo.DisplayInherit)
            {
                pseudo.Display = hostDisplay;
                pseudo.DisplayContents = hostDisplayContents;
                pseudo.IsInlineBlock = hostIsInlineBlock;
                pseudo.FlowRoot = hostFlowRoot;
                pseudo.IsTableBox = hostIsTableBox;
                pseudo.IsTableCellBox = hostIsTableCellBox;
                pseudo.AuthoredTableDisplay = hostAuthoredTableDisplay;
                pseudo.InternalFlexContainer = pseudo.IsTableCellBox;
                if (pseudo.IsTableCellBox)
                {
                    pseudo.FlexDirection = TaffyFlexDirection.Column;
                    pseudo.AlignItems = TaffyAlignItems.FlexStart;
                }

                pseudo.DisplayInherit = false;
            }

            if (pseudo.OverflowInheritX)
            {
                pseudo.OverflowSpecifiedX = hostOverflowX;
                pseudo.OverflowInheritX = false;
            }

            if (pseudo.OverflowInheritY)
            {
                pseudo.OverflowSpecifiedY = hostOverflowY;
                pseudo.OverflowInheritY = false;
            }

            ComputedStyle.RecomputeOverflow(pseudo);
            if (pseudo.FontSizeExpression is { } fontSizeExpression)
            {
                pseudo.FontSize = ComputedStyle.ResolveContextualLength(
                    fontSizeExpression, hostFontSize, rootFs, vw, vh, hostFontSize);
            }
            else if (pseudo.FontSizeRaw is { } raw)
            {
                pseudo.FontSize = raw.Kind switch
                {
                    DimensionKind.Percent => hostFontSize * raw.Value,
                    DimensionKind.Em => hostFontSize * raw.Value,
                    _ => raw.Resolve(hostFontSize, rootFs, vw, vh) is { Kind: DimensionKind.Px } px
                        ? px.Value
                        : hostFontSize,
                };
            }
            else
            {
                pseudo.FontSize ??= hostFontSize;
            }

            float pseudoEm = pseudo.FontSize ?? hostFontSize;
            ComputedStyle.SetGridCalcContext(pseudo, pseudoEm, rootFs, vw, vh);
            ComputedStyle.ResolveFontRelativeDeclarations(pseudo, pseudoEm, rootFs, vw, vh);
            if (pseudo.LetterSpacingExpression is { } letterSpacingExpression)
            {
                pseudo.LetterSpacing = ComputedStyle.ResolveContextualLength(
                    letterSpacingExpression, pseudoEm, rootFs, vw, vh, pseudoEm);
            }
            else if (pseudo.LetterSpacingRaw is { } letterSpacingRaw)
            {
                Dimension resolved = letterSpacingRaw.Resolve(pseudoEm, rootFs, vw, vh);
                pseudo.LetterSpacing =
                    resolved.Kind == DimensionKind.Px && float.IsFinite(resolved.Value)
                        ? resolved.Value
                        : null;
            }
            else
            {
                pseudo.LetterSpacing ??= hostLetterSpacing;
            }

            pseudo.LetterSpacingNonNormal ??= hostLetterSpacingNonNormal;

            for (int index = 0; index < 6; index++)
            {
                if (pseudo.SizeExpressions[index] is not { } expression)
                {
                    continue;
                }

                float percentBase = index is 1 or 3 or 5 ? viewport.Height : cbW;
                if (ComputedStyle.ResolveContextualLength(
                        expression, pseudoEm, rootFs, vw, vh, percentBase) is not { } px)
                {
                    continue;
                }

                Dimension value = Dimension.Px(px);
                switch (index)
                {
                    case 0: pseudo.Width = value; break;
                    case 1: pseudo.Height = value; break;
                    case 2: pseudo.MinWidth = value; break;
                    case 3: pseudo.MinHeight = value; break;
                    case 4: pseudo.MaxWidth = value; break;
                    default: pseudo.MaxHeight = value; break;
                }
            }

            pseudo.Width = pseudo.Width.Resolve(pseudoEm, rootFs, vw, vh);
            pseudo.Height = pseudo.Height.Resolve(pseudoEm, rootFs, vw, vh);
            pseudo.MinWidth = pseudo.MinWidth.Resolve(pseudoEm, rootFs, vw, vh);
            pseudo.MinHeight = pseudo.MinHeight.Resolve(pseudoEm, rootFs, vw, vh);
            pseudo.MaxWidth = pseudo.MaxWidth.Resolve(pseudoEm, rootFs, vw, vh);
            pseudo.MaxHeight = pseudo.MaxHeight.Resolve(pseudoEm, rootFs, vw, vh);

            Edges pseudoPadding = pseudo.Padding;
            Edges pseudoMargin = pseudo.Margin;
            for (int index = 0; index < 4; index++)
            {
                if (pseudo.PaddingRelative[index] is { } paddingRelative
                    && paddingRelative.Resolve(pseudoEm, rootFs, vw, vh) is { Kind: DimensionKind.Px } rp)
                {
                    pseudoPadding = SetEdge(pseudoPadding, index, F32.Max(rp.Value, 0f));
                }

                if (pseudo.MarginRelative[index] is { } marginRelative
                    && marginRelative.Resolve(pseudoEm, rootFs, vw, vh) is { Kind: DimensionKind.Px } rm)
                {
                    pseudoMargin = SetEdge(pseudoMargin, index, rm.Value);
                }

                if (pseudo.MarginPercent[index] is { } percent)
                {
                    pseudoMargin = SetEdge(pseudoMargin, index, percent * cbW);
                }

                if (pseudo.Inset[index] is { } inset)
                {
                    pseudo.SetInset(index, inset.Resolve(pseudoEm, rootFs, vw, vh));
                }
            }

            pseudo.Padding = pseudoPadding;
            pseudo.Margin = pseudoMargin;

            ushort weight = ComputedStyle.ComputedFontWeight(pseudo.FontWeight, hostWeight);
            pseudo.FontWeight = weight.ToString(CultureInfo.InvariantCulture);
            if (pseudo.FontFamily is null)
            {
                pseudo.FontFamily = hostFamily;
                pseudo.FontFamilySpecified = hostFamilySpecified;
            }

            pseudo.Cursor ??= hostCursor;
            pseudo.PointerEvents ??= hostPointerEvents;
            pseudo.FontOpticalSizing ??= hostOpticalSizing;
            pseudo.FontVariationSettings ??= hostVariationSettings is null
                ? null
                : [.. hostVariationSettings];
            if (pseudo.LineHeightExpression is { } lineHeightExpression)
            {
                if (ComputedStyle.ResolveContextualLength(
                        lineHeightExpression, pseudoEm, rootFs, vw, vh, pseudoEm) is { } resolved)
                {
                    pseudo.LineHeight =
                        ComputedStyle.LineHeightExpressionIsLength(lineHeightExpression)
                            ? LineHeight.Px(resolved)
                            : LineHeight.Ratio(resolved);
                }
            }
            else if (pseudo.LineHeight is { Kind: LineHeightKind.Relative } relativeLineHeight)
            {
                Dimension relative = relativeLineHeight.Length;
                float pixels = relative.Kind == DimensionKind.Percent
                    ? pseudoEm * relative.Value
                    : relative.Resolve(pseudoEm, rootFs, vw, vh) is { Kind: DimensionKind.Px } px
                        ? px.Value
                        : pseudoEm;
                pseudo.LineHeight = LineHeight.Px(pixels);
            }
            else
            {
                pseudo.LineHeight ??= hostLineHeight;
            }

            pseudo.WhiteSpace ??= hostWhiteSpace;
            pseudo.OverflowWrap ??= hostOverflowWrap;
            pseudo.WordBreak ??= hostWordBreak;
            pseudo.TextWrapStyle ??= hostTextWrapStyle;
            pseudo.Color ??= hostColor;
            pseudo.TextTransform ??= hostTransform;
            pseudo.FontStyleItalic ??= hostItalic;
            pseudo.TextAlign ??= hostTextAlign;
            if (pseudo.TextIndent is { } indent)
            {
                pseudo.TextIndent = indent.Resolve(pseudoEm, rootFs, vw, vh);
            }
            else
            {
                pseudo.TextIndent = hostTextIndent;
            }

            pseudo.EffectivelyInvisible = hostInvisible;
        }

        if (style.BeforePseudo is { } before)
        {
            Settle(before);
        }

        if (style.AfterPseudo is { } after)
        {
            Settle(after);
        }

        _ = emPx;
    }
}
