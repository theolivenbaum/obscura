// Port of vendor/taffy/src/compute/mod.rs
namespace Obscura.Render.Layout;

/// <summary>Low-level access to the layout algorithms themselves.</summary>
public static class Compute
{
    /// <summary>Compute layout for the root node in the tree.</summary>
    public static void ComputeRootLayout(
        ILayoutPartialTree tree,
        NodeId root,
        Size<AvailableSpace> availableSpace)
    {
        var calc = tree.CalcResolver();
        var knownDimensions = GeometryExtensions.SizeNone;

        {
            var parentSize = availableSpace.IntoOptions();
            var blockStyle = tree.GetCoreContainerStyle(root);

            if (blockStyle.IsBlock)
            {
                float? aspectRatio = blockStyle.AspectRatio;
                var margin = blockStyle.Margin.ResolveOrZero(parentSize.Width, calc);
                var padding = blockStyle.Padding.ResolveOrZero(parentSize.Width, calc);
                var border = blockStyle.Border.ResolveOrZero(parentSize.Width, calc);
                var paddingBorderSize = padding.Add(border).SumAxes();
                var boxSizingAdjustment = blockStyle.BoxSizing == BoxSizing.ContentBox
                    ? paddingBorderSize
                    : GeometryExtensions.SizeZero;

                var minSize = blockStyle.MinSize
                    .MaybeResolve(parentSize, calc)
                    .MaybeApplyAspectRatio(aspectRatio)
                    .MaybeAdd(boxSizingAdjustment);
                var maxSize = blockStyle.MaxSize
                    .MaybeResolve(parentSize, calc)
                    .MaybeApplyAspectRatio(aspectRatio)
                    .MaybeAdd(boxSizingAdjustment);
                var clampedStyleSize = blockStyle.Size
                    .MaybeResolve(parentSize, calc)
                    .MaybeApplyAspectRatio(aspectRatio)
                    .MaybeAdd(boxSizingAdjustment)
                    .MaybeClamp(minSize, maxSize);

                // If both min and max in a given axis are set and max <= min then this determines
                // the size in that axis
                var minMaxDefiniteSize = minSize.ZipMap(
                    maxSize,
                    static (min, max) => min.HasValue && max.HasValue && max.Value <= min.Value ? min : null);

                // Block nodes automatically stretch fit their width to fit available space if
                // available space is definite
                var availableSpaceBasedSize = new Size<float?>(
                    availableSpace.Width.IntoOption().MaybeSub(margin.HorizontalAxisSum()),
                    null);

                knownDimensions = knownDimensions
                    .Or(minMaxDefiniteSize)
                    .Or(clampedStyleSize)
                    .Or(availableSpaceBasedSize)
                    .MaybeMax(paddingBorderSize);
            }
        }

        // Recursively compute node layout
        var output = tree.PerformChildLayout(
            root,
            knownDimensions,
            availableSpace.IntoOptions(),
            availableSpace,
            SizingMode.InherentSize,
            GeometryExtensions.LineFalse);

        var style = tree.GetCoreContainerStyle(root);
        var rootPadding = style.Padding.ResolveOrZero(availableSpace.Width.IntoOption(), calc);
        var rootBorder = style.Border.ResolveOrZero(availableSpace.Width.IntoOption(), calc);
        var rootMargin = style.Margin.ResolveOrZero(availableSpace.Width.IntoOption(), calc);
        var scrollbarSize = new Size<float>(
            style.Overflow.Y == Overflow.Scroll ? style.ScrollbarWidth : 0.0f,
            style.Overflow.X == Overflow.Scroll ? style.ScrollbarWidth : 0.0f);
        float locationX = 0.0f;
        if (style.Direction.IsRtl())
        {
            float? availableWidth = availableSpace.Width.IntoOption();
            locationX = availableWidth.HasValue ? availableWidth.Value - output.Size.Width : 0.0f;
        }

        var layout = new Layout
        {
            Order = 0,
            Location = new Point<float>(locationX, 0.0f),
            Size = output.Size,
            ContentSize = output.ContentSize,
            ScrollbarSize = scrollbarSize,
            Padding = rootPadding,
            Border = rootBorder,
            // TODO: support auto margins for root node?
            Margin = rootMargin,
        };
        tree.SetUnroundedLayout(root, in layout);
    }

    /// <summary>
    /// Attempts to find a cached layout for the specified node and layout inputs, computing (and
    /// caching) it with the supplied closure when there is no cache hit.
    /// </summary>
    public static LayoutOutput ComputeCachedLayout<TTree>(
        TTree tree,
        NodeId node,
        LayoutInput inputs,
        Func<TTree, NodeId, LayoutInput, LayoutOutput> computeUncached)
        where TTree : ICacheTree
    {
        var cacheEntry = tree.CacheGet(node, in inputs);
        if (cacheEntry.HasValue)
        {
            return cacheEntry.Value;
        }

        var computed = computeUncached(tree, node, inputs);
        tree.CacheStore(node, in inputs, in computed);
        return computed;
    }

    /// <summary>
    /// Rounds the calculated layout to exact pixel values.
    /// </summary>
    /// <remarks>
    /// Rounding is done on cumulative (viewport-relative) coordinates and widths/heights are
    /// derived from rounded edges, so that no gaps are introduced. Reads come from the unrounded
    /// layout and writes go to the final layout so already-rounded values are never re-rounded.
    /// </remarks>
    public static void RoundLayout(IRoundTree tree, NodeId nodeId) => RoundLayoutInner(tree, nodeId, 0.0f, 0.0f);

    private static void RoundLayoutInner(IRoundTree tree, NodeId nodeId, float cumulativeX, float cumulativeY)
    {
        var unroundedLayout = tree.GetUnroundedLayout(nodeId);
        var layout = unroundedLayout;

        cumulativeX += unroundedLayout.Location.X;
        cumulativeY += unroundedLayout.Location.Y;

        layout.Location.X = Sys.Round(unroundedLayout.Location.X);
        layout.Location.Y = Sys.Round(unroundedLayout.Location.Y);
        layout.Size.Width = Sys.Round(cumulativeX + unroundedLayout.Size.Width) - Sys.Round(cumulativeX);
        layout.Size.Height = Sys.Round(cumulativeY + unroundedLayout.Size.Height) - Sys.Round(cumulativeY);
        layout.ScrollbarSize.Width = Sys.Round(unroundedLayout.ScrollbarSize.Width);
        layout.ScrollbarSize.Height = Sys.Round(unroundedLayout.ScrollbarSize.Height);
        layout.Border.Left = Sys.Round(cumulativeX + unroundedLayout.Border.Left) - Sys.Round(cumulativeX);
        layout.Border.Right = Sys.Round(cumulativeX + unroundedLayout.Size.Width)
            - Sys.Round(cumulativeX + unroundedLayout.Size.Width - unroundedLayout.Border.Right);
        layout.Border.Top = Sys.Round(cumulativeY + unroundedLayout.Border.Top) - Sys.Round(cumulativeY);
        layout.Border.Bottom = Sys.Round(cumulativeY + unroundedLayout.Size.Height)
            - Sys.Round(cumulativeY + unroundedLayout.Size.Height - unroundedLayout.Border.Bottom);
        layout.Padding.Left = Sys.Round(cumulativeX + unroundedLayout.Padding.Left) - Sys.Round(cumulativeX);
        layout.Padding.Right = Sys.Round(cumulativeX + unroundedLayout.Size.Width)
            - Sys.Round(cumulativeX + unroundedLayout.Size.Width - unroundedLayout.Padding.Right);
        layout.Padding.Top = Sys.Round(cumulativeY + unroundedLayout.Padding.Top) - Sys.Round(cumulativeY);
        layout.Padding.Bottom = Sys.Round(cumulativeY + unroundedLayout.Size.Height)
            - Sys.Round(cumulativeY + unroundedLayout.Size.Height - unroundedLayout.Padding.Bottom);

        layout.ContentSize.Width =
            Sys.Round(cumulativeX + unroundedLayout.ContentSize.Width) - Sys.Round(cumulativeX);
        layout.ContentSize.Height =
            Sys.Round(cumulativeY + unroundedLayout.ContentSize.Height) - Sys.Round(cumulativeY);

        tree.SetFinalLayout(nodeId, in layout);

        int childCount = tree.ChildCount(nodeId);
        for (int index = 0; index < childCount; index++)
        {
            var child = tree.GetChildId(nodeId, index);
            RoundLayoutInner(tree, child, cumulativeX, cumulativeY);
        }
    }

    /// <summary>
    /// Creates a layout for this node and its children, recursively. Each hidden node has zero size
    /// and is placed at the origin.
    /// </summary>
    public static LayoutOutput ComputeHiddenLayout<TTree>(TTree tree, NodeId node)
        where TTree : ILayoutPartialTree, ICacheTree
    {
        tree.CacheClear(node);
        var zero = Layout.WithOrder(0);
        tree.SetUnroundedLayout(node, in zero);

        int childCount = tree.ChildCount(node);
        for (int index = 0; index < childCount; index++)
        {
            var childId = tree.GetChildId(node, index);
            tree.ComputeChildLayout(childId, LayoutInput.Hidden);
        }

        return LayoutOutput.Hidden;
    }
}
