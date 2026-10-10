namespace PocketCalculator.Render.Layout;

/// <summary>
/// What an inline formatting context's node needs from its owner to lay its atomic inlines out:
/// the context sizes its lines with each atomic's margin box and baseline, and says where on its
/// lines each one landed.
/// </summary>
/// <typeparam name="TNodeContext">The node context type of the tree.</typeparam>
public interface IInlineAtomicHost<in TNodeContext>
{
    /// <summary>
    /// Give atomic <paramref name="index"/> of <paramref name="context"/> its margin box and its
    /// baseline, measured from the margin box's top (<c>null</c>: its bottom margin edge).
    /// </summary>
    void SetAtomic(TNodeContext context, int index, float width, float height, float? baseline);

    /// <summary>
    /// Where atomic <paramref name="index"/>'s margin box sits in the context's last layout,
    /// relative to its content box.
    /// </summary>
    (float X, float Y) AtomicPosition(TNodeContext context, int index);

    /// <summary>The atomics' current boxes are the ones the context's final layout used.</summary>
    void CommitAtomics(TNodeContext context);

    /// <summary>
    /// The baseline of atomic box <paramref name="child"/>, laid out at border-box size
    /// <paramref name="size"/>, measured from its border-box top; <c>null</c> when it has none
    /// (its bottom margin edge serves).
    /// </summary>
    float? AtomicBaseline(NodeId child, Size<float> size);
}

/// <summary>Not in vendor/taffy: inline formatting contexts with atomic inline children.</summary>
public sealed partial class TaffyTree<TNodeContext>
{
    /// <summary>
    /// Lays out the atomic inlines of a node that has a context (an inline formatting context)
    /// and children (its atomic inlines); null leaves such a node to the display-mode algorithms.
    /// </summary>
    public IInlineAtomicHost<TNodeContext>? InlineAtomics { get; set; }

    internal sealed partial class TaffyView
    {
        /// <summary>
        /// The layout of an inline formatting context whose children are its atomic inlines.
        /// </summary>
        /// <remarks>
        /// DEVIATION from vendor/taffy, which has no inline layout, and from
        /// crates/obscura-render/src/dom.rs, which lays a run holding an atomic inline out as a
        /// wrapping flex row of word boxes and atomics. The node is measured as a leaf whose
        /// measure function first lays each atomic out (shrink-to-fit in the content box when
        /// its width is auto, CSS 2.1 10.3.9) and hands its margin box and baseline to the
        /// context, so the line breaker sees each as one unit of its line; a final layout then
        /// places each atomic where its line put it.
        /// </remarks>
        private static LayoutOutput ComputeInlineAtomicLayout(
            TaffyView tree,
            NodeId node,
            LayoutInput layoutInputs,
            BlockContext? blockCtx,
            IInlineAtomicHost<TNodeContext> host)
        {
            var nodeData = tree._taffy._nodes[node];
            var style = nodeData.Style;
            _ = tree._taffy._nodeContextData.TryGetValue(node, out TNodeContext? nodeContext);
            var calc = tree.CalcResolver();
            List<NodeId> children = tree._taffy._children[node];
            var sizes = new Size<float>[children.Count];
            var margins = new Rect<float>[children.Count];

            // DEVIATION from crates/obscura-render (no inline layout of atomics; its flex
            // stand-in resolved them against the row): a percentage height on an atomic inline
            // resolves against the block that holds the line, when its height is definite.
            // capcut.com's card images (`height: 100%` in a 104px box, object-fit: cover) were
            // laid out at their ratio height, 99px; Chromium 141: 104px.
            float? heightBasis = null;
            {
                float widthBasis = layoutInputs.ParentSize.Width ?? 0.0f;
                var ownPadding = style.Padding.ResolveOrZero((float?)widthBasis, calc);
                var ownBorder = style.Border.ResolveOrZero((float?)widthBasis, calc);
                float edges = ownPadding.Top + ownPadding.Bottom + ownBorder.Top + ownBorder.Bottom;
                if (layoutInputs.KnownDimensions.Height is { } knownHeight)
                {
                    heightBasis = Sys.F32Max(knownHeight - edges, 0.0f);
                }
                else if (style.Size.Height.MaybeResolve(layoutInputs.ParentSize.Height, calc) is { } styled)
                {
                    heightBasis = style.BoxSizing == BoxSizing.BorderBox
                        ? Sys.F32Max(styled - edges, 0.0f)
                        : styled;
                }
            }

            void SizeAtomics(Size<AvailableSpace> available)
            {
                AvailableSpace width = available.Width;
                float? basis = width.IntoOption();
                for (int i = 0; i < children.Count; i++)
                {
                    NodeId child = children[i];
                    Style childStyle = tree._taffy._nodes[child].Style;
                    Rect<float> margin = childStyle.Margin.ResolveOrZero(basis, calc);
                    float marginX = margin.Left + margin.Right;
                    AvailableSpace childAvailable = width.MaybeSub(marginX);
                    float? knownWidth = null;
                    if (tree.ChildCount(child) > 0 && childStyle.Size.Width.IsAuto)
                    {
                        // CSS 2.1 10.3.9: an auto-width atomic is shrink-to-fit,
                        // min(max(min-content, available), max-content).
                        Size<float?> parentSize = new(basis, heightBasis);
                        float maxContent = tree.MeasureChildSize(
                            child,
                            GeometryExtensions.SizeNone,
                            parentSize,
                            new Size<AvailableSpace>(AvailableSpace.MaxContent, AvailableSpace.MaxContent),
                            SizingMode.InherentSize,
                            AbsoluteAxis.Horizontal,
                            GeometryExtensions.LineFalse);
                        float used = maxContent;
                        bool needsMin = childAvailable.Kind == AvailableSpaceKind.MinContent
                            || (childAvailable.IsDefinite && maxContent > childAvailable.Unwrap());
                        if (needsMin)
                        {
                            float minContent = tree.MeasureChildSize(
                                child,
                                GeometryExtensions.SizeNone,
                                parentSize,
                                new Size<AvailableSpace>(AvailableSpace.MinContent, AvailableSpace.MaxContent),
                                SizingMode.InherentSize,
                                AbsoluteAxis.Horizontal,
                                GeometryExtensions.LineFalse);
                            used = childAvailable.IsDefinite
                                ? Sys.F32Max(minContent, childAvailable.Unwrap())
                                : minContent;
                            used = Sys.F32Min(used, Sys.F32Max(maxContent, minContent));
                        }

                        knownWidth = used;
                    }

                    LayoutOutput output = tree.PerformChildLayout(
                        child,
                        new Size<float?>(knownWidth, null),
                        new Size<float?>(basis, heightBasis),
                        new Size<AvailableSpace>(childAvailable, AvailableSpace.MaxContent),
                        SizingMode.InherentSize,
                        GeometryExtensions.LineFalse);
                    sizes[i] = output.Size;
                    margins[i] = margin;
                    float? baseline = host.AtomicBaseline(child, output.Size);
                    if (nodeContext is not null)
                    {
                        host.SetAtomic(
                            nodeContext,
                            i,
                            output.Size.Width + marginX,
                            output.Size.Height + margin.Top + margin.Bottom,
                            baseline is { } b ? margin.Top + b : null);
                    }
                }
            }

            LayoutOutput result;
            if (blockCtx is not null && tree._taffy.ExclusionMeasure is { } exclusionMeasure)
            {
                float basis = layoutInputs.ParentSize.Width ?? 0.0f;
                var padding = style.Padding.ResolveOrZero((float?)basis, calc);
                var border = style.Border.ResolveOrZero((float?)basis, calc);
                FloatBands? bands = blockCtx.FloatBandsFor(
                    padding.Top + border.Top,
                    padding.Left + border.Left,
                    padding.Right + border.Right);
                RunMode runMode = layoutInputs.RunMode;
                if (bands is not null)
                {
                    result = Leaf.ComputeLeafLayout(
                        layoutInputs,
                        style,
                        static (_, _) => 0.0f,
                        (knownDimensions, availableSpace) =>
                        {
                            SizeAtomics(availableSpace);
                            return exclusionMeasure(
                                knownDimensions, availableSpace, node, nodeContext, style, bands, runMode);
                        });
                    PlaceAtomics(result);
                    return result;
                }

                if (runMode == RunMode.PerformLayout && nodeContext is not null)
                {
                    tree._taffy.ExclusionReset?.Invoke(nodeContext);
                }
            }

            result = Leaf.ComputeLeafLayout(
                layoutInputs,
                style,
                static (_, _) => 0.0f,
                (knownDimensions, availableSpace) =>
                {
                    SizeAtomics(availableSpace);
                    return tree._measureFunction(knownDimensions, availableSpace, node, nodeContext, style);
                });
            PlaceAtomics(result);
            return result;

            void PlaceAtomics(in LayoutOutput output)
            {
                if (layoutInputs.RunMode != RunMode.PerformLayout || nodeContext is null)
                {
                    return;
                }

                host.CommitAtomics(nodeContext);
                float basis = layoutInputs.ParentSize.Width ?? 0.0f;
                var padding = style.Padding.ResolveOrZero((float?)basis, calc);
                var border = style.Border.ResolveOrZero((float?)basis, calc);
                for (int i = 0; i < children.Count; i++)
                {
                    (float x, float y) = host.AtomicPosition(nodeContext, i);
                    Rect<float> margin = margins[i];
                    var childStyle = tree._taffy._nodes[children[i]].Style;
                    var layout = new Layout
                    {
                        Order = (uint)i,
                        Size = sizes[i],
                        ContentSize = sizes[i],
                        Location = new Point<float>(
                            padding.Left + border.Left + x + margin.Left,
                            padding.Top + border.Top + y + margin.Top),
                        Padding = childStyle.Padding.ResolveOrZero((float?)basis, calc),
                        Border = childStyle.Border.ResolveOrZero((float?)basis, calc),
                        Margin = margin,
                    };
                    tree.SetUnroundedLayout(children[i], in layout);
                }
            }
        }
    }
}
