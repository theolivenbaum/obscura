// Port of vendor/taffy/src/compute/grid/alignment.rs
//
// Alignment of tracks and final positioning of items.
//
// NOTE: `AlignAndPositionItem` carries the vendored grid shrink-to-fit
// correction (the `usesInlineFitContent` path). See the comment on that block.
namespace Obscura.Render.Layout;

/// <summary>Track alignment and final grid item placement.</summary>
internal static class GridAlignment
{
    /// <summary>
    /// Align the grid tracks within the grid according to the align-content (rows) or
    /// justify-content (columns) property. This only does anything if the size of the grid is not
    /// equal to the size of the grid container in the axis being aligned.
    /// </summary>
    public static void AlignTracks(
        float gridContainerContentBoxSize,
        Line<float> padding,
        Line<float> border,
        List<GridTrack> tracks,
        AlignContent trackAlignmentStyle,
        bool axisIsReversed)
    {
        float usedSize = 0.0f;
        foreach (var track in tracks)
        {
            usedSize += track.BaseSize;
        }

        float freeSpace = gridContainerContentBoxSize - usedSize;
        float origin = padding.Start + border.Start;

        // Count the number of non-collapsed tracks (not counting gutters)
        int numTracks = 0;
        for (int i = 1; i < tracks.Count; i += 2)
        {
            if (!tracks[i].IsCollapsed)
            {
                numTracks++;
            }
        }

        // Grid layout treats gaps as full tracks rather than applying them at alignment, so we
        // simply pass zero here. Grid layout is never reversed.
        const float Gap = 0.0f;
        const bool LayoutIsReversed = false;
        var trackAlignment = CommonAlignment.ApplyAlignmentFallback(freeSpace, numTracks, trackAlignmentStyle);
        if (axisIsReversed)
        {
            trackAlignment = trackAlignment.Reversed();
        }

        // Compute offsets
        float totalOffset = origin;
        bool seenNonCollapsedTrack = false;
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];

            // Odd tracks are gutters (but lists are zero-indexed, so odd tracks have even indices)
            bool isGutter = i % 2 == 0;
            bool isNonCollapsedTrack = !isGutter && !track.IsCollapsed;

            // Alignment offsets should be applied only to non-collapsed tracks.
            bool isFirst = isNonCollapsedTrack && !seenNonCollapsedTrack;

            float offset = isNonCollapsedTrack
                ? CommonAlignment.ComputeAlignmentOffset(
                    freeSpace, numTracks, Gap, trackAlignment, LayoutIsReversed, isFirst)
                : 0.0f;

            track.Offset = totalOffset + offset;
            totalOffset = totalOffset + offset + track.BaseSize;
            if (isNonCollapsedTrack)
            {
                seenNonCollapsedTrack = true;
            }
        }
    }

    /// <summary>Align and size a grid item into its final position.</summary>
    /// <returns>The content size contribution, the item's y position and its height.</returns>
    public static (Size<float> Contribution, float YPosition, float Height) AlignAndPositionItem(
        ILayoutGridContainer tree,
        NodeId node,
        uint order,
        Rect<float> gridArea,
        InBothAbsAxis<AlignItems?> containerAlignmentStyles,
        float baselineShim,
        Direction direction)
    {
        var calc = tree.CalcResolver();
        var gridAreaSize = new Size<float>(
            gridArea.Right - gridArea.Left, gridArea.Bottom - gridArea.Top);

        var style = tree.GetGridChildStyle(node);

        var overflow = style.Overflow;
        float scrollbarWidth = style.ScrollbarWidth;
        float? aspectRatio = style.AspectRatio;
        var justifySelf = style.JustifySelf;
        var alignSelf = style.AlignSelf;

        var position = style.Position;
        var insetHorizontal = style.Inset
            .HorizontalComponents()
            .Map(size => size.ResolveToOption(gridAreaSize.Width, calc));
        var insetVertical = style.Inset
            .VerticalComponents()
            .Map(size => size.ResolveToOption(gridAreaSize.Height, calc));
        var padding = style.Padding.ResolveOrZero((float?)gridAreaSize.Width, calc);
        var border = style.Border.ResolveOrZero((float?)gridAreaSize.Width, calc);
        var paddingBorderSize = padding.Add(border).SumAxes();

        var boxSizingAdjustment =
            style.BoxSizing == BoxSizing.ContentBox ? paddingBorderSize : GeometryExtensions.SizeZero;
        var aspectRatioAdjustment =
            style.AspectRatioUsesContentBox ? paddingBorderSize : boxSizingAdjustment;

        var gridAreaSizeOption = gridAreaSize.AsOptions();
        var inherentSize = style.Size.MaybeResolve(gridAreaSizeOption, calc).MaybeAdd(boxSizingAdjustment);
        var minSize = style.MinSize
            .MaybeResolve(gridAreaSizeOption, calc)
            .MaybeAdd(boxSizingAdjustment)
            .Or(new Size<float?>(paddingBorderSize.Width, paddingBorderSize.Height))
            .MaybeMax(paddingBorderSize)
            .MaybeApplyAspectRatio(aspectRatio);
        var maxSize = style.MaxSize
            .MaybeResolve(gridAreaSizeOption, calc)
            .MaybeApplyAspectRatio(aspectRatio)
            .MaybeAdd(boxSizingAdjustment);

        // Preserve `normal` provenance until both axes and the preferred aspect ratio are known.
        // Explicit stretch in one axis can make the other normal axis ratio-derived, while two normal
        // axes prefer inline-axis stretch.
        var alignmentStyles = GridLayout.ResolveItemAlignment(
            justifySelf ?? containerAlignmentStyles.Horizontal ?? AlignItems.Normal,
            alignSelf ?? containerAlignmentStyles.Vertical ?? AlignItems.Normal,
            style.IsCompressibleReplaced,
            aspectRatio.HasValue);

        // Note: this is not a bug. It is part of the CSS spec that both horizontal and vertical
        // margins resolve against the WIDTH of the grid area.
        var margin = style.Margin.Map(m => m.ResolveToOption(gridAreaSize.Width, calc));

        var gridAreaMinusItemMarginsSize = new Size<float>(
            gridAreaSize.Width.MaybeSub(margin.Left).MaybeSub(margin.Right),
            gridAreaSize.Height.MaybeSub(margin.Top).MaybeSub(margin.Bottom) - baselineShim);

        // If the node is absolutely positioned and width is not set explicitly, deduce it from left,
        // right and the container content box if both are set.
        float? width = inherentSize.Width;
        if (!width.HasValue)
        {
            if (position == Position.Absolute
                && insetHorizontal.Start.HasValue && insetHorizontal.End.HasValue)
            {
                width = Sys.F32Max(
                    gridAreaMinusItemMarginsSize.Width - insetHorizontal.Start.Value
                        - insetHorizontal.End.Value,
                    0.0f);
            }
            else if (margin.Left.HasValue
                && margin.Right.HasValue
                && alignmentStyles.Horizontal == AlignItems.Stretch
                && position != Position.Absolute)
            {
                // Apply width based on stretch alignment if the alignment style is "stretch", the
                // node is not absolutely positioned and it has no auto margins in this axis.
                width = gridAreaMinusItemMarginsSize.Width;
            }
        }

        float? height = inherentSize.Height;
        if (!height.HasValue)
        {
            if (position == Position.Absolute && insetVertical.Start.HasValue && insetVertical.End.HasValue)
            {
                height = Sys.F32Max(
                    gridAreaMinusItemMarginsSize.Height - insetVertical.Start.Value
                        - insetVertical.End.Value,
                    0.0f);
            }
            else if (margin.Top.HasValue
                && margin.Bottom.HasValue
                && alignmentStyles.Vertical == AlignItems.Stretch
                && position != Position.Absolute)
            {
                height = gridAreaMinusItemMarginsSize.Height;
            }
        }

        // Stretch is resolved before preferred-ratio transfer. If both axes are stretched (or
        // otherwise definite), the ratio does not overwrite either one. If only one axis is definite,
        // it supplies the other through the ratio. This matches replaced grid sizing in Blink/Gecko.
        var ratioApplied = GridLayout.ApplyPreferredAspectRatio(
            new Size<float?>(width, height), aspectRatio, aspectRatioAdjustment);

        // Clamp size by min and max width/height
        var clamped = ratioApplied.MaybeClamp(minSize, maxSize);
        width = clamped.Width;
        height = clamped.Height;

        // ------------------------------------------------------------------
        // VENDORED FIX (grid shrink-to-fit correction).
        //
        // Auto margins disable self-alignment in their axis. In particular, an auto inline margin
        // disables the default `stretch`, so an auto-width in-flow item must use fit-content sizing
        // even though its resolved justify-self value is still `stretch`.
        //
        // At this point a genuinely stretched (or otherwise definite) width is Some, while an auto
        // width whose effective alignment is non-stretch is None. Absolutely-positioned items use
        // their separate shrink-to-fit path below.
        // ------------------------------------------------------------------
        bool usesInlineFitContent = position != Position.Absolute && !width.HasValue;

        // Layout the node
        Size<float?> size;
        if (position == Position.Absolute && (!width.HasValue || !height.HasValue))
        {
            size = tree.MeasureChildSizeBoth(
                node,
                new Size<float?>(width, height),
                gridAreaSizeOption,
                gridAreaMinusItemMarginsSize.Map(static v => AvailableSpace.Definite(v)),
                SizingMode.InherentSize,
                GeometryExtensions.LineFalse).AsOptions();
        }
        else
        {
            size = new Size<float?>(width, height);
        }

        var layoutOutput = tree.PerformChildLayout(
            node,
            size,
            gridAreaSizeOption,
            gridAreaMinusItemMarginsSize.Map(static v => AvailableSpace.Definite(v)),
            SizingMode.InherentSize,
            GeometryExtensions.LineFalse);

        // Resolve final size
        var resolvedSize = size.UnwrapOr(layoutOutput.Size).MaybeClamp(minSize, maxSize);

        // ------------------------------------------------------------------
        // VENDORED FIX (grid shrink-to-fit correction), continued.
        //
        // An auto-sized grid item that is not effectively stretched uses fit-content sizing in the
        // inline axis:
        //
        //   max(min-content, min(available, max-content))
        //
        // The inherent-size layout above obtains the max-content size. Only pay for a min-content
        // measurement when the available width would actually clamp it. This preserves intrinsic
        // overflow for unbreakable content, while breakable content is laid out again at the
        // available width.
        // ------------------------------------------------------------------
        if (usesInlineFitContent && resolvedSize.Width > gridAreaMinusItemMarginsSize.Width)
        {
            float minContentWidth = tree.MeasureChildSize(
                node,
                new Size<float?>(null, size.Height),
                gridAreaSizeOption,
                new Size<AvailableSpace>(
                    AvailableSpace.MinContent,
                    AvailableSpace.Definite(gridAreaMinusItemMarginsSize.Height)),
                SizingMode.InherentSize,
                AbsoluteAxis.Horizontal,
                GeometryExtensions.LineFalse);
            float fitContentWidth = Sys.F32Max(
                minContentWidth,
                Sys.F32Min(gridAreaMinusItemMarginsSize.Width, resolvedSize.Width))
                .MaybeClamp(minSize.Width, maxSize.Width);
            var constrainedSize = new Size<float?>(fitContentWidth, size.Height);
            layoutOutput = tree.PerformChildLayout(
                node,
                constrainedSize,
                gridAreaSizeOption,
                gridAreaMinusItemMarginsSize.Map(static v => AvailableSpace.Definite(v)),
                SizingMode.InherentSize,
                GeometryExtensions.LineFalse);
            resolvedSize = constrainedSize.UnwrapOr(layoutOutput.Size).MaybeClamp(minSize, maxSize);
        }

        float finalWidth = resolvedSize.Width;
        float finalHeight = resolvedSize.Height;

        var (x, xMargin) = AlignItemWithinArea(
            new Line<float>(gridArea.Left, gridArea.Right),
            alignmentStyles.Horizontal,
            finalWidth,
            position,
            insetHorizontal,
            margin.HorizontalComponents(),
            0.0f,
            direction);
        var (y, yMargin) = AlignItemWithinArea(
            new Line<float>(gridArea.Top, gridArea.Bottom),
            alignmentStyles.Vertical,
            finalHeight,
            position,
            insetVertical,
            margin.VerticalComponents(),
            baselineShim,
            Direction.Ltr);

        var scrollbarSize = new Size<float>(
            overflow.Y == Overflow.Scroll ? scrollbarWidth : 0.0f,
            overflow.X == Overflow.Scroll ? scrollbarWidth : 0.0f);

        var resolvedMargin = new Rect<float>(xMargin.Start, xMargin.End, yMargin.Start, yMargin.End);

        var layout = new Layout
        {
            Order = order,
            Location = new Point<float>(x, y),
            Size = new Size<float>(finalWidth, finalHeight),
            ContentSize = layoutOutput.ContentSize,
            ScrollbarSize = scrollbarSize,
            Padding = padding,
            Border = border,
            Margin = resolvedMargin,
        };
        tree.SetUnroundedLayout(node, in layout);

        var contribution = ContentSizeHelper.ComputeContentSizeContribution(
            new Point<float>(x - gridArea.Left, y - gridArea.Top),
            new Size<float>(finalWidth, finalHeight),
            layoutOutput.ContentSize,
            overflow);

        return (contribution, y, finalHeight);
    }

    /// <summary>Align and size a grid item along a single axis.</summary>
    public static (float Start, Line<float> Margin) AlignItemWithinArea(
        Line<float> gridArea,
        AlignItems alignmentStyle,
        float resolvedSize,
        Position position,
        Line<float?> inset,
        Line<float?> margin,
        float baselineShim,
        Direction direction)
    {
        // Calculate the grid area dimension in the axis
        var nonAutoMargin = new Line<float>(
            (margin.Start ?? 0.0f) + baselineShim, margin.End ?? 0.0f);
        float gridAreaSize = Sys.F32Max(gridArea.End - gridArea.Start, 0.0f);
        float freeSpace = Sys.F32Max(gridAreaSize - resolvedSize - nonAutoMargin.Sum(), 0.0f);

        // Expand auto margins to fill the available space
        int autoMarginCount = (margin.Start.HasValue ? 0 : 1) + (margin.End.HasValue ? 0 : 1);
        float autoMarginSize = autoMarginCount > 0 ? freeSpace / autoMarginCount : 0.0f;
        var resolvedMargin = new Line<float>(
            (margin.Start ?? autoMarginSize) + baselineShim,
            margin.End ?? autoMarginSize);

        bool overflows = resolvedSize + nonAutoMargin.Sum() > gridAreaSize;

        // In-flow auto margins take precedence over self-alignment and are always safe. When the item
        // overflows there is no positive free space for the auto margins to absorb, so safe alignment
        // falls back to the logical start edge rather than honoring an authored unsafe center/end
        // alignment. Absolutely positioned items use their authored self-alignment here; their auto
        // margins are resolved by the abs-pos constraint equation.
        var alignmentKeyword = position != Position.Absolute && autoMarginCount > 0 && overflows
            ? AlignItemsKeyword.Start
            : CommonAlignment.ResolveSelfAlignmentSafety(alignmentStyle, overflows);

        // Compute the offset in the axis
        float alignmentBasedOffset = alignmentKeyword switch
        {
            // TODO: Add support for baseline alignment. For now we treat it as "start".
            AlignItemsKeyword.Normal or AlignItemsKeyword.Start or AlignItemsKeyword.FlexStart
                or AlignItemsKeyword.Baseline or AlignItemsKeyword.Stretch =>
                direction.IsRtl()
                    ? gridAreaSize - resolvedSize - resolvedMargin.End
                    : resolvedMargin.Start,
            AlignItemsKeyword.End or AlignItemsKeyword.FlexEnd =>
                direction.IsRtl()
                    ? resolvedMargin.Start
                    : gridAreaSize - resolvedSize - resolvedMargin.End,
            _ => (gridAreaSize - resolvedSize + resolvedMargin.Start - resolvedMargin.End) / 2.0f,
        };

        float offsetWithinArea;
        if (position == Position.Absolute)
        {
            if (inset.Start.HasValue && inset.End.HasValue)
            {
                offsetWithinArea = direction.IsRtl()
                    ? gridAreaSize - inset.End.Value - resolvedSize - nonAutoMargin.End
                    : inset.Start.Value + nonAutoMargin.Start;
            }
            else if (inset.Start.HasValue)
            {
                offsetWithinArea = inset.Start.Value + nonAutoMargin.Start;
            }
            else if (inset.End.HasValue)
            {
                offsetWithinArea = gridAreaSize - inset.End.Value - resolvedSize - nonAutoMargin.End;
            }
            else
            {
                offsetWithinArea = alignmentBasedOffset;
            }
        }
        else
        {
            offsetWithinArea = alignmentBasedOffset;
        }

        float start = gridArea.Start + offsetWithinArea;
        if (position == Position.Relative)
        {
            float? relativeInset = direction.IsRtl()
                ? (inset.End.HasValue ? (float?)(-inset.End.Value) : inset.Start)
                : (inset.Start ?? (inset.End.HasValue ? (float?)(-inset.End.Value) : null));
            start += relativeInset ?? 0.0f;
        }

        return (start, resolvedMargin);
    }
}
