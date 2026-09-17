// Classic (non-overlay) scrollbar reservation. No counterpart in crates/obscura-render, which
// reserves a gutter only out of the initial containing block; see "Known deviations" in todo.md.
using Obscura.Dom;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyOverflow = Obscura.Render.Layout.Overflow;
using TaffyStyle = Obscura.Render.Layout.Style;
using TaffyTree = Obscura.Render.Layout.TaffyTree<int?>;

namespace Obscura.Render;

internal static class DomScrollbarPasses
{
    /// <summary>
    /// Clear what the previous layout reserved. A retained style outlives the tree it was laid out
    /// in, and whether its box still shows a scrollbar is a property of that layout, not of it.
    /// </summary>
    internal static void ResetScrollbarGutters(Dictionary<NodeId, LayoutStyle> styles)
    {
        foreach (LayoutStyle style in styles.Values)
        {
            style.ReservedScrollbarX = 0f;
            style.ReservedScrollbarY = 0f;
        }
    }

    /// <summary>
    /// Give every scroll container the scrollbar it actually shows, taken out of its scrollport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Chromium on this platform draws classic scrollbars, which occupy layout space: a vertical
    /// one narrows the content box by its thickness and a horizontal one shortens it. An
    /// <c>overflow: scroll</c> axis always has one; an <c>overflow: auto</c> axis only once its
    /// content overflows, which is why this runs against a completed layout instead of in the
    /// style mapping.
    /// </para>
    /// <para>
    /// Reservation only ever grows - narrowing a box can make its content taller and so overflow
    /// harder, never less - so repeating the pass terminates.
    /// </para>
    /// </remarks>
    /// <returns>Whether any box changed and the tree therefore needs another layout.</returns>
    internal static bool ApplyScrollbarGutters(
        TaffyTree taffyTree,
        Dictionary<TaffyNodeId, NodeId> idMap,
        Dictionary<NodeId, LayoutStyle> styles)
    {
        bool changed = false;
        foreach ((TaffyNodeId taffyId, NodeId domId) in idMap)
        {
            if (!styles.TryGetValue(domId, out LayoutStyle? style)
                || !style.OverflowScrollContainer
                || style.OverflowPropagatedToViewport
                || style.ScrollbarGutters != 0
                || style.DisplayContents)
            {
                continue;
            }

            float wantY = WantedThickness(taffyTree, taffyId, style, vertical: true);
            float wantX = WantedThickness(taffyTree, taffyId, style, vertical: false);
            if (wantY <= style.ReservedScrollbarY && wantX <= style.ReservedScrollbarX)
            {
                continue;
            }

            style.ReservedScrollbarY = F32.Max(style.ReservedScrollbarY, wantY);
            style.ReservedScrollbarX = F32.Max(style.ReservedScrollbarX, wantX);

            TaffyStyle adjusted = taffyTree.GetStyle(taffyId).Clone();
            adjusted.ScrollbarWidth = F32.Max(style.ReservedScrollbarX, style.ReservedScrollbarY);
            adjusted.Overflow = new Layout.Point<TaffyOverflow>(
                style.ReservedScrollbarX > 0f ? TaffyOverflow.Scroll : adjusted.Overflow.X,
                style.ReservedScrollbarY > 0f ? TaffyOverflow.Scroll : adjusted.Overflow.Y);
            taffyTree.SetStyle(taffyId, adjusted);
            changed = true;
        }

        return changed;
    }

    private static float WantedThickness(
        TaffyTree taffyTree,
        TaffyNodeId taffyId,
        LayoutStyle style,
        bool vertical)
    {
        if (!style.ScrollbarAxisKind(vertical, out bool always))
        {
            return 0f;
        }

        float thickness = style.ScrollbarThickness(vertical);
        if (thickness <= 0f)
        {
            return 0f;
        }

        if (always)
        {
            return thickness;
        }

        Layout.Layout layout = taffyTree.GetLayout(taffyId);

        return (vertical ? layout.ScrollHeight() : layout.ScrollWidth()) > OverflowEpsilon
            ? thickness
            : 0f;
    }

    /// <summary>
    /// Slack before an `auto` axis is called overflowing. Chromium decides this in LayoutUnits
    /// (1/64px); a whole-pixel tolerance keeps a box whose content rounds a hair past its
    /// scrollport from growing a scrollbar this engine's text metrics invented.
    /// </summary>
    private const float OverflowEpsilon = 1f;
}
