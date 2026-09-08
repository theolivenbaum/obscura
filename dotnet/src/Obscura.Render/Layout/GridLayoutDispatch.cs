// Dispatch hook for the CSS Grid algorithm.
//
// taffy calls `compute_grid_layout` directly from `TaffyView::compute_child_layout`.
// The grid algorithm (vendor/taffy/src/compute/grid/**) is ported separately from
// this foundation, so the call site goes through this hook instead of a direct
// reference. The grid port registers itself here (e.g. from a
// [ModuleInitializer]) and can then replace this indirection with a direct call
// once both halves live in the same assembly.
namespace Obscura.Render.Layout;

/// <summary>Registration point for the CSS Grid layout algorithm.</summary>
public static class GridLayoutDispatch
{
    /// <summary>
    /// The grid layout entry point, equivalent to taffy's <c>compute_grid_layout</c>. Null until the
    /// grid algorithm registers itself; a <c>Display.Grid</c> node with children then throws.
    /// </summary>
    public static Func<ILayoutGridContainer, NodeId, LayoutInput, LayoutOutput>? Compute { get; set; }
}
