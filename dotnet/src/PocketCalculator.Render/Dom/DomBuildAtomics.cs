using PocketCalculator.Dom;
using TaffyDisplay = PocketCalculator.Render.Layout.Display;
using TaffyNodeId = PocketCalculator.Render.Layout.NodeId;
using TaffyStyle = PocketCalculator.Render.Layout.Style;

namespace PocketCalculator.Render;

internal static partial class DomBuild
{
    /// <summary>
    /// Build the boxes of inline item <paramref name="item"/>'s atomic inlines as children of its
    /// leaf <paramref name="leaf"/>, in order: the leaf is then laid out by the tree's inline
    /// layout (TaffyTreeInline), which sizes them and places each where its line put it.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-render/src/dom.rs, which builds a run holding an atomic
    /// inline as a wrapping flex row of word boxes and atomics: baselines were not aligned, line
    /// boxes did not grow around them, text-align and justification did not reach them, white
    /// space around them was lost and a right-to-left line ordered them item by item. The atomic
    /// is built as its own box (<see cref="Build"/>, without the flex-item adjustments
    /// <see cref="BuildAny"/> makes for the old row); one that builds no box keeps its place
    /// with an empty hidden one, so the children stay index-aligned with the item's atomics.
    /// </remarks>
    internal static void AttachAtomics(BuildContext context, TaffyNodeId leaf, int item)
    {
        IReadOnlyList<AtomicInline> atomics = context.Engine.AtomicsOf(item);
        if (atomics.Count == 0)
        {
            return;
        }

        foreach (AtomicInline atomic in atomics)
        {
            TaffyNodeId? built = null;
            if (context.TryEnterLevel())
            {
                try
                {
                    built = Build(context, atomic.Node);
                }
                finally
                {
                    context.ExitLevel();
                }
            }

            if (built is not { } node)
            {
                TaffyStyle hidden = TaffyStyle.Default;
                hidden.Display = TaffyDisplay.None;
                node = context.TaffyTree.NewLeaf(hidden);
            }

            context.TaffyTree.AddChild(leaf, node);
        }
    }
}
