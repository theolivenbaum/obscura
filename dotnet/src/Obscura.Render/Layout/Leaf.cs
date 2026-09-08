// Port of vendor/taffy/src/compute/leaf.rs
namespace Obscura.Render.Layout;

/// <summary>
/// Measures the intrinsic size of a leaf node.
/// </summary>
/// <param name="knownDimensions">Dimensions that are already fixed.</param>
/// <param name="availableSpace">The space available to lay out into.</param>
public delegate Size<float> MeasureFunction(Size<float?> knownDimensions, Size<AvailableSpace> availableSpace);

/// <summary>Computes size using styles and measure functions.</summary>
public static class Leaf
{
    /// <summary>Compute the size of a leaf node (a node with no children).</summary>
    public static LayoutOutput ComputeLeafLayout(
        LayoutInput inputs,
        ICoreStyle style,
        CalcResolver resolveCalcValue,
        MeasureFunction measureFunction)
    {
        var knownDimensions = inputs.KnownDimensions;
        var parentSize = inputs.ParentSize;
        var availableSpaceIn = inputs.AvailableSpace;
        var sizingMode = inputs.SizingMode;
        var runMode = inputs.RunMode;

        // Note: both horizontal and vertical percentage padding/borders are resolved against the
        // container's inline size (i.e. width). This is how CSS is specified.
        var margin = style.Margin.ResolveOrZero(parentSize.Width, resolveCalcValue);
        var padding = style.Padding.ResolveOrZero(parentSize.Width, resolveCalcValue);
        var border = style.Border.ResolveOrZero(parentSize.Width, resolveCalcValue);
        var paddingBorder = padding.Add(border);
        var pbSum = paddingBorder.SumAxes();
        var boxSizingAdjustment =
            style.BoxSizing == BoxSizing.ContentBox ? pbSum : GeometryExtensions.SizeZero;

        Size<float?> nodeSize;
        Size<float?> nodeMinSize;
        Size<float?> nodeMaxSize;
        float? aspectRatio;

        if (sizingMode == SizingMode.ContentSize)
        {
            nodeSize = knownDimensions;
            nodeMinSize = GeometryExtensions.SizeNone;
            nodeMaxSize = GeometryExtensions.SizeNone;
            aspectRatio = null;
        }
        else
        {
            aspectRatio = style.AspectRatio;
            var styleSize = style.Size
                .MaybeResolve(parentSize, resolveCalcValue)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment);
            var styleMinSize = style.MinSize
                .MaybeResolve(parentSize, resolveCalcValue)
                .MaybeApplyAspectRatio(aspectRatio)
                .MaybeAdd(boxSizingAdjustment);
            var styleMaxSize = style.MaxSize
                .MaybeResolve(parentSize, resolveCalcValue)
                .MaybeAdd(boxSizingAdjustment);

            nodeSize = knownDimensions.Or(styleSize);
            nodeMinSize = styleMinSize;
            nodeMaxSize = styleMaxSize;
        }

        // Scrollbar gutters are reserved when the `overflow` property is Scroll. The axes are
        // transposed because a node that scrolls vertically needs *horizontal* space reserved.
        var scrollbarGutter = style.Overflow.Transpose().Map(
            overflow => overflow == Overflow.Scroll ? style.ScrollbarWidth : 0.0f);
        var contentBoxInset = paddingBorder;
        contentBoxInset.Right += scrollbarGutter.X;
        contentBoxInset.Bottom += scrollbarGutter.Y;

        bool hasStylesPreventingBeingCollapsedThrough = !style.IsBlock
            || style.Overflow.X.IsScrollContainer()
            || style.Overflow.Y.IsScrollContainer()
            || style.Position == Position.Absolute
            || padding.Top > 0.0f
            || padding.Bottom > 0.0f
            || border.Top > 0.0f
            || border.Bottom > 0.0f
            || (nodeSize.Height is { } nh && nh > 0.0f)
            || (nodeMinSize.Height is { } nmh && nmh > 0.0f);

        // Return early if both width and height are known
        if (runMode == RunMode.ComputeSize && hasStylesPreventingBeingCollapsedThrough
            && nodeSize.Width is { } knownWidth && nodeSize.Height is { } knownHeight)
        {
            var earlySize = new Size<float>(knownWidth, knownHeight)
                .MaybeClamp(nodeMinSize, nodeMaxSize)
                .MaybeMax(paddingBorder.SumAxes().AsOptions());
            return new LayoutOutput
            {
                Size = earlySize,
                ContentSize = GeometryExtensions.SizeZero,
                FirstBaselines = GeometryExtensions.PointNone,
                TopMargin = CollapsibleMarginSet.Zero,
                BottomMargin = CollapsibleMarginSet.Zero,
                MarginsCanCollapseThrough = false,
            };
        }

        // Compute available space
        var availableSpace = new Size<AvailableSpace>(
            (knownDimensions.Width.HasValue
                ? AvailableSpace.Definite(knownDimensions.Width.Value)
                : availableSpaceIn.Width)
                .MaybeSub(margin.HorizontalAxisSum())
                .MaybeSet(knownDimensions.Width)
                .MaybeSet(nodeSize.Width)
                .MapDefiniteValue(size =>
                    size.MaybeClamp(nodeMinSize.Width, nodeMaxSize.Width) - contentBoxInset.HorizontalAxisSum()),
            (knownDimensions.Height.HasValue
                ? AvailableSpace.Definite(knownDimensions.Height.Value)
                : availableSpaceIn.Height)
                .MaybeSub(margin.VerticalAxisSum())
                .MaybeSet(knownDimensions.Height)
                .MaybeSet(nodeSize.Height)
                .MapDefiniteValue(size =>
                    size.MaybeClamp(nodeMinSize.Height, nodeMaxSize.Height) - contentBoxInset.VerticalAxisSum()));

        // Measure node
        var measureKnown = runMode switch
        {
            RunMode.ComputeSize => knownDimensions,
            RunMode.PerformLayout => GeometryExtensions.SizeNone,
            _ => throw new InvalidOperationException("compute_leaf_layout called with PerformHiddenLayout"),
        };
        var measuredSize = measureFunction(measureKnown, availableSpace);

        var clampedSize = knownDimensions
            .Or(nodeSize)
            .UnwrapOr(measuredSize.Add(contentBoxInset.SumAxes()))
            .MaybeClamp(nodeMinSize, nodeMaxSize);
        var size = new Size<float>(
            clampedSize.Width,
            Sys.F32Max(clampedSize.Height, aspectRatio.HasValue ? clampedSize.Width / aspectRatio.Value : 0.0f));
        size = size.MaybeMax(paddingBorder.SumAxes().AsOptions());

        return new LayoutOutput
        {
            Size = size,
            ContentSize = measuredSize.Add(padding.SumAxes()),
            FirstBaselines = GeometryExtensions.PointNone,
            TopMargin = CollapsibleMarginSet.Zero,
            BottomMargin = CollapsibleMarginSet.Zero,
            MarginsCanCollapseThrough = !hasStylesPreventingBeingCollapsedThrough
                && size.Height == 0.0f
                && measuredSize.Height == 0.0f,
        };
    }
}
