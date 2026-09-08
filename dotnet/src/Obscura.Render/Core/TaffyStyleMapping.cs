// Port of the taffy glue at the bottom of `crates/obscura-render/src/lib.rs`:
// `layout`, `new_taffy_tree`, `build_node`, `read_node`, `to_taffy_style` and its four
// dimension/rect helpers.
using TaffyAlignContent = Obscura.Render.Layout.AlignContent;
using TaffyAlignItems = Obscura.Render.Layout.AlignItems;
using TaffyBoxSizing = Obscura.Render.Layout.BoxSizing;
using TaffyDimension = Obscura.Render.Layout.Dimension;
using TaffyDirection = Obscura.Render.Layout.Direction;
using TaffyDisplay = Obscura.Render.Layout.Display;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyFlexWrap = Obscura.Render.Layout.FlexWrap;
using TaffyLengthPercentage = Obscura.Render.Layout.LengthPercentage;
using TaffyLengthPercentageAuto = Obscura.Render.Layout.LengthPercentageAuto;
using TaffyNodeId = Obscura.Render.Layout.NodeId;
using TaffyOverflow = Obscura.Render.Layout.Overflow;
using TaffyStyle = Obscura.Render.Layout.Style;

namespace Obscura.Render;

internal static class TaffyStyleMapping
{
    internal static Layout.TaffyTree<TNodeContext> NewTaffyTree<TNodeContext>()
    {
        Layout.TaffyTree<TNodeContext> tree = new();
        // Every opaque handle is backed by an expression retained in the LayoutStyle
        // map/input tree, which outlives all computations on this tree. Without this
        // resolver taffy falls back to its default, which returns 0 for every calc()
        // handle, so a grid track like minmax(0, calc(...)) silently collapses to zero
        // width instead of failing.
        tree.SetCalcResolver(ComputedStyle.ResolveGridCalc);
        return tree;
    }

    internal static TaffyStyle ToTaffyStyle(LayoutStyle style)
    {
        TaffyStyle s = TaffyStyle.Default;
        s.Direction = style.Direction ?? TaffyDirection.Ltr;
        s.ItemIsReplaced = style.HasReplacedSizing;
        s.ItemAspectRatioIsIntrinsic = style.AspectRatioIsIntrinsic;
        s.BoxSizing = style.BoxSizing switch
        {
            BoxSizing.ContentBox => TaffyBoxSizing.ContentBox,
            BoxSizing.BorderBox => TaffyBoxSizing.BorderBox,
            // Programmatic LayoutStyle users do not have a DOM inheritance pass; fall back to
            // the property's CSS initial value in that case.
            _ => TaffyBoxSizing.ContentBox,
        };

        // A block box with centered/right inline content needs a flex-column stand-in because
        // taffy's native block algorithm has no line alignment. `TextAlign` is separate from
        // real flex/grid `align-items`, so a text-align declaration never changes how flex
        // children are sized.
        bool promoteForAlignment = style.Display == Display.Block
            && (style.TextAlign == TaffyAlignItems.Center || style.TextAlign == TaffyAlignItems.FlexEnd);

        s.Display = style.Display switch
        {
            Display.Block when promoteForAlignment => TaffyDisplay.Flex,
            Display.Block => TaffyDisplay.Block,
            Display.Flex => TaffyDisplay.Flex,
            Display.Grid => TaffyDisplay.Grid,
            Display.Inline => TaffyDisplay.Flex,
            _ => TaffyDisplay.None,
        };
        if (promoteForAlignment)
        {
            s.FlexDirection = TaffyFlexDirection.Column;
            s.AlignItems = style.TextAlign;
        }

        if (style.FlexDirection is { } flexDirection)
        {
            s.FlexDirection = flexDirection;
        }

        if (style.FlexWrap is { } flexWrap)
        {
            s.FlexWrap = flexWrap;
        }
        else if (style.Display == Display.Inline)
        {
            s.FlexDirection = TaffyFlexDirection.Row;
            s.FlexWrap = TaffyFlexWrap.Wrap;
        }
        else
        {
            s.FlexWrap = TaffyFlexWrap.NoWrap;
        }

        s.Size = new Layout.Size<TaffyDimension>(ToDimension(style.Width), ToDimension(style.Height));

        // Tell taffy's layout algorithm about computed overflow, not just our paint-time clips:
        // a flex/grid automatic minimum depends on the relevant axis. Overflow propagated from
        // html/body belongs to the viewport and must leave the source element's own layout
        // overflow visible.
        if (style.OverflowHidden && !style.OverflowPropagatedToViewport)
        {
            s.Overflow = new Layout.Point<TaffyOverflow>(
                style.OverflowScrollX
                    ? TaffyOverflow.Hidden
                    : style.ClipsOverflowX() ? TaffyOverflow.Clip : TaffyOverflow.Visible,
                style.OverflowScrollY
                    ? TaffyOverflow.Hidden
                    : style.ClipsOverflowY() ? TaffyOverflow.Clip : TaffyOverflow.Visible);
        }

        s.MinSize = new Layout.Size<TaffyDimension>(ToDimension(style.MinWidth), ToDimension(style.MinHeight));
        s.MaxSize = new Layout.Size<TaffyDimension>(ToDimension(style.MaxWidth), ToDimension(style.MaxHeight));
        if (style.AspectRatio is { } aspectRatio && float.IsFinite(aspectRatio) && aspectRatio > 0f)
        {
            s.AspectRatio = aspectRatio;
        }

        if (style.IgnoresUsedBoxSizes())
        {
            s.Size = new Layout.Size<TaffyDimension>(TaffyDimension.Auto, TaffyDimension.Auto);
            s.MinSize = new Layout.Size<TaffyDimension>(TaffyDimension.Auto, TaffyDimension.Auto);
            s.MaxSize = new Layout.Size<TaffyDimension>(TaffyDimension.Auto, TaffyDimension.Auto);
            s.AspectRatio = null;
        }

        if (style.Display != Display.Block)
        {
            if (style.AlignItems is { } alignItems)
            {
                s.AlignItems = alignItems;
            }
        }
        else if (!promoteForAlignment)
        {
            // `align-items` has no effect on a block formatting context.
            s.AlignItems = null;
        }

        s.JustifyItems = style.JustifyItems;
        s.AlignSelf = style.AlignSelf;
        s.JustifySelf = style.JustifySelf;
        s.AlignContent = style.AlignContent;
        if (style.JustifyContent is { } justifyContent)
        {
            s.JustifyContent = justifyContent;
        }
        else if (style.IsInlineBlock)
        {
            // Taffy's inline-box stand-in is a wrapping flex row. `text-align` aligns each line
            // of an inline-block's internal formatting context; map that inherited value onto
            // the row axis without affecting how the atomic box itself participates in its
            // parent's inline flow.
            s.JustifyContent = style.TextAlign switch
            {
                { } align when align == TaffyAlignItems.Center => TaffyAlignContent.Center,
                { } align when align == TaffyAlignItems.FlexEnd => TaffyAlignContent.FlexEnd,
                _ => null,
            };
        }

        if (style.FlexGrow is { } flexGrow)
        {
            s.FlexGrow = flexGrow;
        }

        if (style.FlexShrink is { } flexShrink)
        {
            s.FlexShrink = flexShrink;
        }

        if (!style.FlexBasis.IsAuto)
        {
            s.FlexBasis = ToDimension(style.FlexBasis);
        }

        // Grid container tracks and gaps. Numeric repeat() values are expanded during parsing,
        // while auto-fill/auto-fit remain native taffy repetition components so their count can
        // use the final container size.
        if (style.Display == Display.Grid)
        {
            if (style.GridTemplateColumns.Count != 0)
            {
                s.GridTemplateColumns = [.. style.GridTemplateColumns];
            }

            if (style.GridTemplateRows.Count != 0)
            {
                s.GridTemplateRows = [.. style.GridTemplateRows];
            }

            if (style.GridAutoColumns.Count != 0)
            {
                s.GridAutoColumns = [.. style.GridAutoColumns];
            }

            if (style.GridAutoRows.Count != 0)
            {
                s.GridAutoRows = [.. style.GridAutoRows];
            }

            if (style.GridAutoFlow is { } gridAutoFlow)
            {
                s.GridAutoFlow = gridAutoFlow;
            }
        }

        float columnGap = style.ColumnGap ?? 0f;
        float rowGap = style.RowGap ?? 0f;
        s.Gap = new Layout.Size<TaffyLengthPercentage>(
            TaffyLengthPercentage.FromLength(columnGap),
            TaffyLengthPercentage.FromLength(rowGap));

        // Grid item placement (resolved from grid-area names or explicit lines).
        if (style.GridColumn is { } gridColumn)
        {
            s.GridColumn = gridColumn;
        }

        if (style.GridRow is { } gridRow)
        {
            s.GridRow = gridRow;
        }

        // Positioning. Absolute/fixed take the box out of flow.
        if (style.Position is { } position)
        {
            s.Position = position;
            if (!style.PositionSticky)
            {
                s.Inset = new Layout.Rect<TaffyLengthPercentageAuto>(
                    InsetLpa(style.Inset[3]),
                    InsetLpa(style.Inset[1]),
                    InsetLpa(style.Inset[0]),
                    InsetLpa(style.Inset[2]));
            }
        }

        s.Margin = RectAuto(style.Margin, style.MarginAuto);
        s.Padding = RectLpPercent(style.Padding, style.PaddingPercent);
        s.Border = RectLp(style.Border);
        if (style.IgnoresUsedBoxSizes())
        {
            // Block-axis margins on an ordinary non-replaced inline neither move nor size its
            // fragment. Padding and border do paint around the raw font box, but they protrude
            // outside the CSS line-height rather than increasing line advance. Keep them in
            // LayoutStyle for fragment synthesis/paint and remove them only from this Taffy
            // line surrogate.
            Layout.Rect<TaffyLengthPercentageAuto> margin = s.Margin;
            margin.Top = TaffyLengthPercentageAuto.FromLength(0f);
            margin.Bottom = TaffyLengthPercentageAuto.FromLength(0f);
            s.Margin = margin;

            Layout.Rect<TaffyLengthPercentage> padding = s.Padding;
            padding.Top = TaffyLengthPercentage.FromLength(0f);
            padding.Bottom = TaffyLengthPercentage.FromLength(0f);
            s.Padding = padding;

            Layout.Rect<TaffyLengthPercentage> border = s.Border;
            border.Top = TaffyLengthPercentage.FromLength(0f);
            border.Bottom = TaffyLengthPercentage.FromLength(0f);
            s.Border = border;
        }

        return s;
    }

    private static TaffyLengthPercentageAuto InsetLpa(Dimension? value) => value?.Kind switch
    {
        DimensionKind.Px => TaffyLengthPercentageAuto.FromLength(value!.Value.Value),
        DimensionKind.Percent => TaffyLengthPercentageAuto.FromPercent(value!.Value.Value),
        // Relative units are resolved to Px before layout; unresolved leftovers and
        // `auto`/absent both map to Auto.
        _ => TaffyLengthPercentageAuto.Auto,
    };

    internal static TaffyDimension ToDimension(Dimension value) => value.Kind switch
    {
        DimensionKind.Px => TaffyDimension.FromLength(value.Value),
        DimensionKind.Percent => TaffyDimension.FromPercent(value.Value),
        DimensionKind.Auto => TaffyDimension.Auto,
        // Relative units are resolved to Px before layout; if one slips through unresolved,
        // fall back to its raw magnitude (em/rem ~16px) rather than panicking.
        DimensionKind.Em or DimensionKind.Rem => TaffyDimension.FromLength(value.Value * 16f),
        DimensionKind.Ex => TaffyDimension.FromLength(value.Value * 16f * Dimension.ExPerEm),
        _ => TaffyDimension.FromLength(value.Value),
    };

    private static Layout.Rect<TaffyLengthPercentage> RectLp(Edges e) => new(
        TaffyLengthPercentage.FromLength(e.Left),
        TaffyLengthPercentage.FromLength(e.Right),
        TaffyLengthPercentage.FromLength(e.Top),
        TaffyLengthPercentage.FromLength(e.Bottom));

    private static Layout.Rect<TaffyLengthPercentage> RectLpPercent(Edges e, float?[] percent)
    {
        static TaffyLengthPercentage Side(float value, float? percent) => percent is { } fraction
            ? TaffyLengthPercentage.FromPercent(fraction)
            : TaffyLengthPercentage.FromLength(value);

        return new Layout.Rect<TaffyLengthPercentage>(
            Side(e.Left, percent[3]),
            Side(e.Right, percent[1]),
            Side(e.Top, percent[0]),
            Side(e.Bottom, percent[2]));
    }

    private static Layout.Rect<TaffyLengthPercentageAuto> RectAuto(Edges e, bool[] auto)
    {
        static TaffyLengthPercentageAuto Side(float value, bool isAuto) => isAuto
            ? TaffyLengthPercentageAuto.Auto
            : TaffyLengthPercentageAuto.FromLength(value);

        return new Layout.Rect<TaffyLengthPercentageAuto>(
            Side(e.Left, auto[3]),
            Side(e.Right, auto[1]),
            Side(e.Top, auto[0]),
            Side(e.Bottom, auto[2]));
    }

    internal static NodeRect LayoutRoot(LayoutNode root, (float Width, float Height) viewport)
    {
        // Grid calc() handles carry no units of their own; they are evaluated
        // against an em/rem/vw/vh context that has to be installed on every
        // style in the tree before layout runs. Skipping this does not fail
        // loudly: viewport-relative terms simply evaluate to zero, so a track
        // like calc((100% - (50rem + 20vw))/2) comes out too wide.
        var rootFontSize = root.Style.FontSize ?? 16f;
        InitializeGridCalcContexts(root, rootFontSize, rootFontSize, viewport.Width / 100f, viewport.Height / 100f);

        Layout.TaffyTree<object> tree = NewTaffyTree<object>();
        TaffyNodeId rootId = BuildNode(tree, root);
        tree.ComputeLayout(
            rootId,
            new Layout.Size<Layout.AvailableSpace>(
                Layout.AvailableSpace.Definite(viewport.Width),
                Layout.AvailableSpace.Definite(viewport.Height)));
        return ReadNode(tree, rootId);
    }

    private static void InitializeGridCalcContexts(
        LayoutNode node,
        float inheritedFontSize,
        float rootFontSize,
        float vw,
        float vh)
    {
        var fontSize = node.Style.FontSize ?? inheritedFontSize;
        ComputedStyle.SetGridCalcContext(node.Style, fontSize, rootFontSize, vw, vh);
        foreach (var child in node.Children)
        {
            InitializeGridCalcContexts(child, fontSize, rootFontSize, vw, vh);
        }
    }

    private static TaffyNodeId BuildNode(Layout.TaffyTree<object> tree, LayoutNode node)
    {
        TaffyStyle style = ToTaffyStyle(node.Style);
        if (node.Children.Count == 0)
        {
            return tree.NewLeaf(style);
        }

        TaffyNodeId[] childIds = new TaffyNodeId[node.Children.Count];
        for (int i = 0; i < node.Children.Count; i++)
        {
            childIds[i] = BuildNode(tree, node.Children[i]);
        }

        return tree.NewWithChildren(style, childIds);
    }

    private static NodeRect ReadNode(Layout.TaffyTree<object> tree, TaffyNodeId id)
    {
        Layout.Layout layout = tree.GetLayout(id);
        NodeRect result = new()
        {
            BorderBox = new Rect(layout.Location.X, layout.Location.Y, layout.Size.Width, layout.Size.Height),
        };
        foreach (TaffyNodeId childId in tree.Children(id))
        {
            result.Children.Add(ReadNode(tree, childId));
        }

        return result;
    }
}
