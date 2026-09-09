// Port of `PreparedRender`, `ResolvedScrollState`, `ElementScrollMetrics`, `CanvasSurface`, and
// `CanvasSurfaceSource` in crates/obscura-render/src/paint.rs.
using Obscura.Dom;
using Obscura.Render.Layout;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;
using TaffyAlignContentKeyword = Obscura.Render.Layout.AlignContentKeyword;
using TaffyAlignItemsKeyword = Obscura.Render.Layout.AlignItemsKeyword;
using TaffyFlexDirection = Obscura.Render.Layout.FlexDirection;
using TaffyFlexWrap = Obscura.Render.Layout.FlexWrap;
using TaffyGridAutoFlow = Obscura.Render.Layout.GridAutoFlow;
using TaffyPosition = Obscura.Render.Layout.Position;

namespace Obscura.Render;

/// <summary>
/// A short-lived view of one JavaScript canvas backing store.
/// </summary>
/// <remarks>
/// The renderer deliberately borrows raw RGBA only for a synchronous paint; retained layout
/// never owns VM-specific resources or pixel copies.
/// </remarks>
public readonly struct CanvasSurface
{
    private CanvasSurface(uint width, uint height, ReadOnlyMemory<byte> rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public uint Width { get; }

    public uint Height { get; }

    internal ReadOnlyMemory<byte> Rgba { get; }

    public static CanvasSurface? FromRgba8(uint width, uint height, ReadOnlyMemory<byte> rgba)
    {
        long expected = (long)width * height * 4;
        return expected == rgba.Length ? new CanvasSurface(width, height, rgba) : null;
    }
}

/// <summary>Paint-time lookup for dynamic canvas pixels.</summary>
public interface ICanvasSurfaceSource
{
    CanvasSurface? Surface(NodeId node);
}

internal sealed class EmptyCanvasSurfaceSource : ICanvasSurfaceSource
{
    internal static readonly EmptyCanvasSurfaceSource Instance = new();

    public CanvasSurface? Surface(NodeId node) => null;
}

/// <summary>CSSOM scrolling metrics for one element box.</summary>
public readonly record struct ElementScrollMetrics(
    (float Width, float Height) ClientSize,
    (float Width, float Height) ContentSize,
    (float X, float Y) Offset,
    (float X, float Y) MaxOffset);

/// <summary>One fully resolved scrolling snapshot shared by geometry and paint.</summary>
public sealed class ResolvedScrollState
{
    internal (float X, float Y) RootOffsetValue { get; init; }

    internal List<(float X, float Y)> ContainerOffsets { get; init; } = [];

    internal List<(float X, float Y)> NodeMovement { get; init; } = [];

    internal List<OverflowClip?> InheritedClips { get; init; } = [];

    public (float X, float Y) RootOffset() => RootOffsetValue;

    internal (float X, float Y) MovementFor(NodeId id) =>
        id.Index < NodeMovement.Count ? NodeMovement[id.Index] : (0f, 0f);

    internal OverflowClip? InheritedClipFor(NodeId id) =>
        id.Index < InheritedClips.Count ? InheritedClips[id.Index] : null;
}

/// <summary>
/// A final image/font-aware document layout retained across viewport paints. The DOM must not
/// be mutated while this value is reused.
/// </summary>
public sealed class PreparedRender
{
    internal (float Width, float Height) ViewportSize { get; set; }

    internal AnimationSample AnimationSampleValue { get; set; }

    internal bool HasActiveWaapiAnimations { get; set; }

    internal AnimationEffectImpact ActiveAnimationImpact { get; set; }

    internal float RootFontSize { get; init; }

    internal string? BaseUrlValue { get; init; }

    internal bool HasDynamicFonts { get; init; }

    internal (float Width, float Height) ContentSizeValue { get; set; }

    internal HashSet<NodeId> ViewportFixed { get; set; } = [];

    internal StickyLayout Sticky { get; set; } = new();

    internal ScrollTree ScrollTree { get; set; } = null!;

    internal Dictionary<NodeId, SelectedImage> SelectedImages { get; set; } = [];

    internal SvgFontDatabase SvgFonts { get; init; } = SvgFontDatabase.Shared;

    /// <summary>The retained final layout (Rust's <c>layout</c> field and <c>layout()</c>).</summary>
    public DomLayout Layout { get; internal set; } = new();

    public (float Width, float Height) Viewport() => ViewportSize;

    public AnimationSampleTime AnimationSampleTime() => AnimationSampleValue.Time;

    public AnimationSample AnimationSample() => AnimationSampleValue;

    /// <summary>
    /// Whether advancing the document timeline can still change a sampled CSS animation.
    /// </summary>
    public bool HasActiveCssAnimations() =>
        HasActiveWaapiAnimations || Layout.Styles.Values.Any(PaintApi.CssAnimationIsActive);

    private bool HasActiveDeclarativeCssAnimations() =>
        Layout.Styles.Values.Any(PaintApi.CssAnimationIsActive);

    /// <summary>
    /// Advance opacity/transform Web Animations without rebuilding normal-flow layout.
    /// </summary>
    internal bool TryAdvanceVisualWaapiSample(
        DomTree tree,
        AnimationSample sample,
        AnimationTimelineState timeline)
    {
        if (HasActiveDeclarativeCssAnimations())
        {
            return false;
        }

        bool Connected(NodeId node) =>
            tree.GetNode(node) is not null
            && (node == tree.Document || tree.Ancestors(node).Contains(tree.Document));

        List<(NodeId Node, Css.ResampledVisualWaapi Sampled)> updates = [];
        bool hasTransformEffect = false;
        bool hasOpacityEffect = false;
        foreach (NodeId node in timeline.WaapiNodes().Where(Connected))
        {
            if (!Layout.Styles.TryGetValue(node, out LayoutStyle? style))
            {
                return false;
            }

            Css.ResampledVisualWaapi? sampled = Css.CssAnimationSampler.ResampleVisualWaapi(
                timeline,
                node,
                style,
                sample);
            if (sampled is null)
            {
                return false;
            }

            bool establishedTransformCb =
                (style.ContainingBlockTriggers & ContainingBlockTrigger.Transform) != 0;
            if (sampled.EstablishesTransformContainingBlock != establishedTransformCb)
            {
                return false;
            }

            hasTransformEffect |= sampled.HasTransformEffect;
            hasOpacityEffect |= sampled.HasOpacityEffect;
            updates.Add((node, sampled));
        }

        if (updates.Count == 0)
        {
            return false;
        }

        foreach ((NodeId node, Css.ResampledVisualWaapi sampled) in updates)
        {
            LayoutStyle style = Layout.Styles[node];
            style.TransformOps = sampled.TransformOps;
            style.Opacity = sampled.Opacity;
        }

        if (hasOpacityEffect)
        {
            Layout.RefreshEffectiveVisibility(tree);
        }

        if (hasTransformEffect)
        {
            Layout.RefreshVisualGeometry(tree, ViewportSize);

            // A retained transform sample may change visual overflow, sticky constraints, and
            // scrolling ranges, but the preflight above has already rejected any
            // containing-block topology change.
            DerivedGeometryState derived = Layout.DerivedGeometryWithFixed(
                tree,
                ViewportSize,
                ViewportFixed);
            ContentSizeValue = derived.ContentSize;
            Sticky = derived.Sticky;
            ScrollTree = derived.ScrollTree;
        }

        AnimationSampleValue = sample;
        HasActiveWaapiAnimations = timeline.HasActiveWaapi(sample.Time);
        ActiveAnimationImpact = timeline.ActiveWaapiEffectImpact(sample.Time);
        return true;
    }

    /// <summary>
    /// Whether a geometry-only consumer may retain this layout while moving to
    /// <paramref name="sample"/>.
    /// </summary>
    public bool CanReuseGeometryForAnimationSample(AnimationSample sample) =>
        sample.Mode == AnimationSampleMode.DocumentTime
        && AnimationSampleValue.Mode == AnimationSampleMode.DocumentTime
        && sample.Time.Milliseconds >= AnimationSampleValue.Time.Milliseconds
        && ActiveAnimationImpact < AnimationEffectImpact.Geometry;

    /// <summary>Advance a frame whose animation cascade is already time-invariant.</summary>
    public bool AdvanceInactiveAnimationSampleTime(AnimationSampleTime sample)
    {
        if (AnimationSampleValue.Mode != AnimationSampleMode.DocumentTime
            || sample.Milliseconds < AnimationSampleValue.Time.Milliseconds
            || HasActiveCssAnimations())
        {
            return false;
        }

        AnimationSampleValue = AnimationSampleValue with { Time = sample };
        return true;
    }

    public string? BaseUrl() => BaseUrlValue;

    public (float Width, float Height) ContentSize() => ContentSizeValue;

    public HashSet<NodeId> ViewportFixedNodes() => ViewportFixed;

    public StickyLayout StickyLayout() => Sticky;

    public (float X, float Y) ClampScroll((float X, float Y) requested) =>
        ClampScrollForViewport(requested, ViewportSize);

    private (float X, float Y) ClampScrollForViewport(
        (float X, float Y) requested,
        (float Width, float Height) viewport)
    {
        static float ClampAxis(float requested, float content, float viewport) =>
            float.IsFinite(requested)
                ? Math.Clamp(
                    RenderMath.QuantizeScrollValue(requested, 1f),
                    0f,
                    RenderMath.QuantizedScrollRange(content, viewport, 1f))
                : 0f;

        return (
            ClampAxis(requested.X, ContentSizeValue.Width, viewport.Width),
            ClampAxis(requested.Y, ContentSizeValue.Height, viewport.Height));
    }

    private (float X, float Y) RootOnlyMovementFor(NodeId id, (float X, float Y) requestedScroll)
    {
        (float X, float Y) root = ClampScroll(requestedScroll);
        List<(float X, float Y)> cumulative = [.. Enumerable.Repeat((0f, 0f), ScrollTree.Containers.Count)];
        cumulative[0] = (-root.X, -root.Y);
        for (int index = 1; index < cumulative.Count; index++)
        {
            ScrollId? parent = ScrollTree.Containers[index].Parent;
            cumulative[index] = parent is { } p ? cumulative[p.Index] : (0f, 0f);
        }

        (float X, float Y) movement = (0f, 0f);
        if (id.Index < ScrollTree.MovementOwner.Length
            && ScrollTree.MovementOwner[id.Index] is { } owner
            && owner.Index < cumulative.Count)
        {
            movement = cumulative[owner.Index];
        }

        (float X, float Y) sticky = Sticky.ResolvedTranslationFor(id, ViewportSize, ScrollTree, cumulative);
        return (movement.X + sticky.X, movement.Y + sticky.Y);
    }

    /// <summary>Element scroll containers in this prepared layout, in stable DOM order.</summary>
    public IEnumerable<NodeId> ScrollContainerNodes()
    {
        for (int index = 1; index < ScrollTree.Containers.Count; index++)
        {
            if (ScrollTree.Containers[index].Node is { } node)
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// Resolve persistent NodeId-keyed offsets into this layout's dense scroll topology.
    /// </summary>
    public ResolvedScrollState ResolveScrollState(
        DomTree tree,
        (float X, float Y) requestedRoot,
        IReadOnlyDictionary<NodeId, (float X, float Y)> requestedElements) =>
        ResolveScrollStateForViewport(tree, requestedRoot, requestedElements, ViewportSize);

    /// <summary>
    /// Resolve scroll-time movement for a virtual capture viewport without mutating the live
    /// page scroll.
    /// </summary>
    public ResolvedScrollState ResolveScrollStateForViewport(
        DomTree tree,
        (float X, float Y) requestedRoot,
        IReadOnlyDictionary<NodeId, (float X, float Y)> requestedElements,
        (float Width, float Height) viewport)
    {
        (float X, float Y) root = ClampScrollForViewport(requestedRoot, viewport);
        List<(float X, float Y)> offsets = [.. Enumerable.Repeat((0f, 0f), ScrollTree.Containers.Count)];
        offsets[0] = root;
        for (int index = 1; index < ScrollTree.Containers.Count; index++)
        {
            ScrollContainer container = ScrollTree.Containers[index];
            (float X, float Y) requested = (0f, 0f);
            if (container.Node is { } node && requestedElements.TryGetValue(node, out (float X, float Y) value))
            {
                requested = value;
            }

            static float Clamp(float value, float max) =>
                float.IsFinite(value) ? Math.Clamp(RenderMath.QuantizeScrollValue(value, 1f), 0f, max) : 0f;

            offsets[index] = (
                Clamp(requested.X, container.MaxOffset.X),
                Clamp(requested.Y, container.MaxOffset.Y));
        }

        List<(float X, float Y)> cumulative = [.. Enumerable.Repeat((0f, 0f), offsets.Count)];
        cumulative[0] = (-root.X, -root.Y);
        for (int index = 1; index < offsets.Count; index++)
        {
            ScrollContainer container = ScrollTree.Containers[index];
            (float X, float Y) inherited = container.Parent is { } parent ? cumulative[parent.Index] : (0f, 0f);
            cumulative[index] = (inherited.X - offsets[index].X, inherited.Y - offsets[index].Y);
        }

        int nodeLen = ScrollTree.MovementOwner.Length;
        List<(float X, float Y)> nodeMovement = [.. Enumerable.Repeat((0f, 0f), nodeLen)];
        for (int index = 0; index < nodeLen; index++)
        {
            if (ScrollTree.MovementOwner[index] is { } owner)
            {
                nodeMovement[index] = cumulative[owner.Index];
            }
        }

        foreach ((NodeId id, (float X, float Y) sticky) in
            Sticky.ResolvedTranslations(viewport, ScrollTree, cumulative))
        {
            if (id.Index < nodeMovement.Count)
            {
                nodeMovement[id.Index] = (
                    nodeMovement[id.Index].X + sticky.X,
                    nodeMovement[id.Index].Y + sticky.Y);
            }
        }

        List<OverflowClip?> inheritedClips = [.. Enumerable.Repeat((OverflowClip?)null, nodeLen)];
        NodeId? rootNode = tree.Descendants(tree.Document)
            .FirstOrDefault(id => tree.GetNode(id)?.IsElement ?? false);
        if (rootNode is { } start && tree.GetNode(start) is not null)
        {
            ResolveClips(tree, Layout, nodeMovement, ViewportFixed, start, null, inheritedClips);
        }

        return new ResolvedScrollState
        {
            RootOffsetValue = root,
            ContainerOffsets = offsets,
            NodeMovement = nodeMovement,
            InheritedClips = inheritedClips,
        };
    }

    private static void ResolveClips(
        DomTree tree,
        DomLayout laid,
        List<(float X, float Y)> movement,
        HashSet<NodeId> viewportFixed,
        NodeId id,
        OverflowClip? inherited,
        List<OverflowClip?> output)
    {
        // A fixed-position box whose containing block is the viewport escapes clips
        // established by ancestors in document space. Only reset at the boundary.
        bool startsViewportFixed = viewportFixed.Contains(id)
            && (DomTraversal.RenderedParent(tree, id) is not { } parent || !viewportFixed.Contains(parent));
        OverflowClip? active = startsViewportFixed ? null : inherited;
        if (id.Index < output.Count)
        {
            output[id.Index] = active?.Clone();
        }

        OverflowClip? next = active;
        if (laid.Styles.TryGetValue(id, out LayoutStyle? style)
            && laid.Rects.TryGetValue(id, out Rect rect)
            && style.OverflowHidden
            && !style.OverflowPropagatedToViewport)
        {
            (float X, float Y) authored = laid.Translates.TryGetValue(id, out (float X, float Y) t) ? t : (0f, 0f);
            (float X, float Y) scroll = id.Index < movement.Count ? movement[id.Index] : (0f, 0f);
            OverflowClip own = OverflowClip.ForBox(
                rect,
                style,
                authored.X + scroll.X,
                authored.Y + scroll.Y);
            next = active is null ? own : active.Intersect(own);
        }

        foreach (NodeId child in DomTraversal.RenderedChildren(tree, id))
        {
            ResolveClips(tree, laid, movement, viewportFixed, child, next?.Clone(), output);
        }
    }

    public ElementScrollMetrics? ElementScrollMetrics(NodeId id, ResolvedScrollState state)
    {
        if (ClientSize(id) is not { } client)
        {
            return null;
        }

        ScrollId? sid = id.Index < ScrollTree.NodeContainer.Length ? ScrollTree.NodeContainer[id.Index] : null;
        if (sid is not { } container)
        {
            (float Width, float Height)? content =
                id.Index < ScrollTree.NodeContentSize.Length ? ScrollTree.NodeContentSize[id.Index] : null;
            return content is { } size
                ? new ElementScrollMetrics(client, size, (0f, 0f), (0f, 0f))
                : null;
        }

        ScrollContainer entry = ScrollTree.Containers[container.Index];
        (float X, float Y) offset = container.Index < state.ContainerOffsets.Count
            ? state.ContainerOffsets[container.Index]
            : (0f, 0f);
        return new ElementScrollMetrics(entry.ClientSize, entry.ContentSize, offset, entry.MaxOffset);
    }

    /// <summary>
    /// Axis-aligned bounds of the transformed border box in immutable document space.
    /// </summary>
    public Rect? DocumentRect(NodeId id)
    {
        if (!Layout.Rects.TryGetValue(id, out Rect rect))
        {
            return null;
        }

        return Layout.Transforms.TryGetValue(id, out Affine2 transform) ? transform.MapRect(rect) : rect;
    }

    /// <summary>Unscaled padding-box size used by CSSOM View's client metrics.</summary>
    public (float Width, float Height)? ClientSize(NodeId id)
    {
        if (!Layout.Rects.TryGetValue(id, out Rect rect)
            || !Layout.Styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        if (style.IgnoresUsedBoxSizes())
        {
            return (0f, 0f);
        }

        return (
            F32.Max(rect.Width - style.Border.Left - style.Border.Right, 0f),
            F32.Max(rect.Height - style.Border.Top - style.Border.Bottom, 0f));
    }

    /// <summary>A compact CSSOM snapshot derived from the same cascade paint uses.</summary>
    public Dictionary<string, string>? ComputedStyle(NodeId id)
    {
        if (!Layout.Styles.TryGetValue(id, out LayoutStyle? style))
        {
            return null;
        }

        Rect? rect = Layout.Rects.TryGetValue(id, out Rect found) ? found : null;
        Dictionary<string, string> output = new(StringComparer.Ordinal);

        bool activeWebkitClamp = style.WebkitBoxDisplay is not null
            && style.WebkitBoxOrientVertical
            && style.WebkitLineClamp is not null;
        string display;
        if (style.DisplayContents)
        {
            display = "contents";
        }
        else if (style.Display == Display.None)
        {
            display = "none";
        }
        else if (activeWebkitClamp && style.WebkitBoxDisplay == false)
        {
            display = "flow-root";
        }
        else if (style.WebkitBoxDisplay == false && !activeWebkitClamp)
        {
            display = "-webkit-box";
        }
        else if (style.WebkitBoxDisplay == true && !activeWebkitClamp)
        {
            display = "-webkit-inline-box";
        }
        else if (style.InternalFlexContainer)
        {
            display = "block";
        }
        else
        {
            display = (style.Display, style.IsInlineBlock) switch
            {
                (Display.Flex, true) => "inline-flex",
                (Display.Grid, true) => "inline-grid",
                (Display.Block, true) => "inline-block",
                (Display.Flex, false) => "flex",
                (Display.Grid, false) => "grid",
                (Display.Inline, true) => "inline-block",
                (Display.Inline, false) => "inline",
                _ => "block",
            };
        }

        output["display"] = display;
        output["float"] = style.Float switch
        {
            Obscura.Render.Float.Left => "left",
            Obscura.Render.Float.Right => "right",
            _ => "none",
        };
        output["clear"] = style.Clear switch
        {
            Obscura.Render.Clear.Left => "left",
            Obscura.Render.Clear.Right => "right",
            Obscura.Render.Clear.Both => "both",
            _ => "none",
        };
        output["position"] = style.PositionFixed
            ? "fixed"
            : style.PositionSticky
                ? "sticky"
                : style.Position switch
                {
                    TaffyPosition.Absolute => "absolute",
                    TaffyPosition.Relative => "relative",
                    _ => "static",
                };
        output["z-index"] = style.ZIndex is { } z
            ? z.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "auto";
        output["visibility"] = style.VisibilityHidden == true ? "hidden" : "visible";
        output["opacity"] = PaintCssValues.CssNumber(style.Opacity ?? 1f);
        output["background-color"] = PaintCssValues.CssColor(style.BackgroundColor ?? new RgbaColor(0, 0, 0, 0));
        output["background-origin"] = style.BackgroundOrigin switch
        {
            BackgroundOrigin.BorderBox => "border-box",
            BackgroundOrigin.PaddingBox => "padding-box",
            _ => "content-box",
        };
        output["background-clip"] = style.BackgroundClip switch
        {
            BackgroundClip.BorderBox => "border-box",
            BackgroundClip.PaddingBox => "padding-box",
            BackgroundClip.ContentBox => "content-box",
            _ => "text",
        };
        output["color"] = PaintCssValues.CssColor(style.Color ?? new RgbaColor(0, 0, 0, 255));
        output["font-size"] = PaintCssValues.CssPx(style.FontSize ?? 16f);
        output["font-weight"] = style.FontWeight ?? "400";
        if (style.FontFamily is { } family)
        {
            output["font-family"] = family;
        }

        output["line-height"] = (style.LineHeight ?? Obscura.Render.LineHeight.Normal) == Obscura.Render.LineHeight.Normal
            ? "normal"
            : PaintCssValues.CssPx(Layout.TextEngine.SelectedLineHeight(style));
        output["letter-spacing"] = (style.LetterSpacing ?? 0f) == 0f
            ? "normal"
            : PaintCssValues.CssPx(style.LetterSpacing ?? 0f);
        output["white-space"] = (style.WhiteSpace ?? Obscura.Render.WhiteSpace.Normal) switch
        {
            Obscura.Render.WhiteSpace.Normal => "normal",
            Obscura.Render.WhiteSpace.NoWrap => "nowrap",
            Obscura.Render.WhiteSpace.Pre => "pre",
            Obscura.Render.WhiteSpace.PreWrap => "pre-wrap",
            Obscura.Render.WhiteSpace.PreLine => "pre-line",
            _ => "break-spaces",
        };
        output["text-overflow"] = style.TextOverflow == TextOverflow.Clip ? "clip" : "ellipsis";
        output["-webkit-line-clamp"] = style.WebkitLineClamp is { } lines
            ? lines.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "none";
        output["-webkit-box-orient"] = style.WebkitBoxOrientVertical ? "vertical" : "horizontal";
        string overflowWrap = (style.OverflowWrap ?? Obscura.Render.OverflowWrap.Normal) switch
        {
            Obscura.Render.OverflowWrap.Normal => "normal",
            Obscura.Render.OverflowWrap.BreakWord => "break-word",
            _ => "anywhere",
        };
        output["overflow-wrap"] = overflowWrap;

        // CSSOM retains the legacy alias as a separately addressable property.
        output["word-wrap"] = overflowWrap;
        output["word-break"] = (style.WordBreak ?? Obscura.Render.WordBreak.Normal) switch
        {
            Obscura.Render.WordBreak.Normal => "normal",
            Obscura.Render.WordBreak.BreakAll => "break-all",
            Obscura.Render.WordBreak.KeepAll => "keep-all",
            _ => "break-word",
        };
        output["text-align"] = style.TextAlign is { } align
            ? align.Keyword switch
            {
                TaffyAlignItemsKeyword.Center => "center",
                TaffyAlignItemsKeyword.FlexEnd or TaffyAlignItemsKeyword.End => "end",
                _ => "start",
            }
            : "start";

        if (style.IgnoresUsedBoxSizes())
        {
            output["width"] = PaintCssValues.DimensionCss(style.Width, "auto");
            output["height"] = PaintCssValues.DimensionCss(style.Height, "auto");
        }
        else if (rect is { } box)
        {
            float horizontal = style.Border.Left + style.Border.Right + style.Padding.Left + style.Padding.Right;
            float vertical = style.Border.Top + style.Border.Bottom + style.Padding.Top + style.Padding.Bottom;
            (float width, float height) = style.BoxSizing == BoxSizing.BorderBox
                ? (box.Width, box.Height)
                : (F32.Max(box.Width - horizontal, 0f), F32.Max(box.Height - vertical, 0f));
            output["width"] = PaintCssValues.CssPx(width);
            output["height"] = PaintCssValues.CssPx(height);
        }
        else
        {
            output["width"] = PaintCssValues.DimensionCss(style.Width, "auto");
            output["height"] = PaintCssValues.DimensionCss(style.Height, "auto");
        }

        output["min-width"] = PaintCssValues.DimensionCss(style.MinWidth, "auto");
        output["min-height"] = PaintCssValues.DimensionCss(style.MinHeight, "auto");
        output["max-width"] = PaintCssValues.DimensionCss(style.MaxWidth, "none");
        output["max-height"] = PaintCssValues.DimensionCss(style.MaxHeight, "none");
        output["box-sizing"] = style.BoxSizing == BoxSizing.BorderBox ? "border-box" : "content-box";

        static string OverflowAxis(byte specified, bool clipped, bool scroll) =>
            scroll ? "auto" : specified == 1 || clipped ? "clip" : "visible";

        output["overflow-x"] = OverflowAxis(style.OverflowSpecifiedX, style.OverflowClipX, style.OverflowScrollX);
        output["overflow-y"] = OverflowAxis(style.OverflowSpecifiedY, style.OverflowClipY, style.OverflowScrollY);

        (string Name, float Value, bool Auto)[] margins =
        [
            ("margin-top", style.Margin.Top, style.MarginAuto[0]),
            ("margin-right", style.Margin.Right, style.MarginAuto[1]),
            ("margin-bottom", style.Margin.Bottom, style.MarginAuto[2]),
            ("margin-left", style.Margin.Left, style.MarginAuto[3]),
        ];
        foreach ((string name, float value, bool auto) in margins)
        {
            output[name] = auto ? "auto" : PaintCssValues.CssPx(value);
        }

        (string Name, float Value)[] lengths =
        [
            ("padding-top", style.Padding.Top),
            ("padding-right", style.Padding.Right),
            ("padding-bottom", style.Padding.Bottom),
            ("padding-left", style.Padding.Left),
            ("border-top-width", style.Border.Top),
            ("border-right-width", style.Border.Right),
            ("border-bottom-width", style.Border.Bottom),
            ("border-left-width", style.Border.Left),
        ];
        foreach ((string name, float value) in lengths)
        {
            output[name] = PaintCssValues.CssPx(value);
        }

        RgbaColor currentColor = style.Color ?? new RgbaColor(0, 0, 0, 255);
        (string Name, RgbaColor? Color)[] borderColors =
        [
            ("border-top-color", style.BorderModel.Colors.Top),
            ("border-right-color", style.BorderModel.Colors.Right),
            ("border-bottom-color", style.BorderModel.Colors.Bottom),
            ("border-left-color", style.BorderModel.Colors.Left),
        ];
        foreach ((string name, RgbaColor? color) in borderColors)
        {
            output[name] = PaintCssValues.CssColor(color ?? style.BorderColor ?? currentColor);
        }

        Sides<BorderStyle> effective = PaintBorders.EffectiveBorderStyles(style);
        (string Name, BorderStyle Style)[] borderStyles =
        [
            ("border-top-style", effective.Top),
            ("border-right-style", effective.Right),
            ("border-bottom-style", effective.Bottom),
            ("border-left-style", effective.Left),
        ];
        foreach ((string name, BorderStyle lineStyle) in borderStyles)
        {
            output[name] = lineStyle.CssName();
        }

        (string Name, CornerRadius Radius)[] radii =
        [
            ("border-top-left-radius", style.BorderModel.Radii.TopLeft),
            ("border-top-right-radius", style.BorderModel.Radii.TopRight),
            ("border-bottom-right-radius", style.BorderModel.Radii.BottomRight),
            ("border-bottom-left-radius", style.BorderModel.Radii.BottomLeft),
        ];
        foreach ((string name, CornerRadius radius) in radii)
        {
            output[name] = PaintCssValues.CornerRadiusCss(radius);
        }

        output["outline-width"] = PaintCssValues.CssPx(style.Outline.UsedWidth());
        output["outline-style"] = style.Outline.Style.CssName();
        output["outline-color"] = PaintCssValues.CssColor(style.Outline.Color ?? currentColor);
        output["outline-offset"] = PaintCssValues.CssPx(style.Outline.Offset);

        output["flex-direction"] = (style.FlexDirection ?? TaffyFlexDirection.Row) switch
        {
            TaffyFlexDirection.Row => "row",
            TaffyFlexDirection.RowReverse => "row-reverse",
            TaffyFlexDirection.Column => "column",
            _ => "column-reverse",
        };
        output["flex-wrap"] = (style.FlexWrap ?? TaffyFlexWrap.NoWrap) switch
        {
            TaffyFlexWrap.NoWrap => "nowrap",
            TaffyFlexWrap.Wrap => "wrap",
            _ => "wrap-reverse",
        };
        output["align-items"] = style.AlignItems is { } alignItems
            ? PaintCssValues.AlignItemsCss(alignItems)
            : "normal";
        output["justify-items"] = style.JustifyItems is { } justifyItems
            ? PaintCssValues.AlignItemsCss(justifyItems)
            : "normal";
        output["justify-content"] = style.JustifyContent is { } justifyContent
            ? PaintCssValues.AlignContentCss(justifyContent)
            : "normal";
        output["align-content"] = style.AlignContent is { } alignContent
            ? PaintCssValues.AlignContentCss(alignContent)
            : "normal";
        output["column-gap"] = style.ColumnGap is { } columnGap ? PaintCssValues.CssPx(columnGap) : "normal";
        output["row-gap"] = style.RowGap is { } rowGap ? PaintCssValues.CssPx(rowGap) : "normal";
        output["grid-auto-flow"] = (style.GridAutoFlow ?? TaffyGridAutoFlow.Row) switch
        {
            TaffyGridAutoFlow.Row => "row",
            TaffyGridAutoFlow.Column => "column",
            TaffyGridAutoFlow.RowDense => "row dense",
            _ => "column dense",
        };

        output["transform"] = PaintCssValues.TransformCss(style, rect, RootFontSize, ViewportSize);
        output["transform-origin"] = PaintCssValues.TransformOriginCss(style, rect);
        output["translate"] = style.IndividualTranslate is { } translate
            ? PaintCssValues.DimensionCss(translate.X, "0px") + " " + PaintCssValues.DimensionCss(translate.Y, "0px")
            : "none";
        output["rotate"] = style.IndividualRotate is { } rotate
            ? PaintCssValues.CssNumber(rotate) + "deg"
            : "none";
        output["scale"] = style.IndividualScale is { } scale
            ? PaintCssValues.CssNumber(scale.X) + " " + PaintCssValues.CssNumber(scale.Y)
            : "none";
        return output;
    }

    /// <summary>Cascaded custom properties exposed by CSSOM.</summary>
    public Dictionary<string, string>? ComputedCustomProperties(NodeId id) =>
        Layout.CustomProperties.TryGetValue(id, out IReadOnlyDictionary<string, string>? properties)
            ? new Dictionary<string, string>(properties, StringComparer.Ordinal)
            : null;

    /// <summary>Border box in the current root viewport.</summary>
    public Rect? ViewportRect(NodeId id, (float X, float Y) requestedScroll)
    {
        if (DocumentRect(id) is not { } rect)
        {
            return null;
        }

        (float X, float Y) movement = RootOnlyMovementFor(id, requestedScroll);
        return rect with { X = rect.X + movement.X, Y = rect.Y + movement.Y };
    }

    /// <summary>Border box in the viewport using a pre-resolved scroll snapshot.</summary>
    public Rect? ViewportRectWithScroll(NodeId id, ResolvedScrollState scroll)
    {
        if (DocumentRect(id) is not { } rect)
        {
            return null;
        }

        (float X, float Y) movement = scroll.MovementFor(id);
        return rect with { X = rect.X + movement.X, Y = rect.Y + movement.Y };
    }

    /// <summary>Every CSS border-box fragment in the current root viewport.</summary>
    public List<Rect>? ViewportClientRects(NodeId id, (float X, float Y) requestedScroll)
    {
        List<Rect>? source = FragmentSource(id);
        if (source is null)
        {
            return null;
        }

        (float X, float Y) movement = RootOnlyMovementFor(id, requestedScroll);
        return MapFragments(id, source, movement);
    }

    public List<Rect>? ViewportClientRectsWithScroll(NodeId id, ResolvedScrollState scroll)
    {
        List<Rect>? source = FragmentSource(id);
        if (source is null)
        {
            return null;
        }

        return MapFragments(id, source, scroll.MovementFor(id));
    }

    private List<Rect>? FragmentSource(NodeId id)
    {
        if (Layout.InlineFragments.TryGetValue(id, out List<Rect>? fragments))
        {
            return [.. fragments];
        }

        return Layout.Rects.TryGetValue(id, out Rect rect) ? [rect] : null;
    }

    private List<Rect> MapFragments(NodeId id, List<Rect> source, (float X, float Y) movement)
    {
        bool hasTransform = Layout.Transforms.TryGetValue(id, out Affine2 transform);
        List<Rect> output = new(source.Count);
        foreach (Rect fragment in source)
        {
            Rect mapped = hasTransform ? transform.MapRect(fragment) : fragment;
            output.Add(mapped with { X = mapped.X + movement.X, Y = mapped.Y + movement.Y });
        }

        return output;
    }

    public SelectedImage? SelectedImage(NodeId id) =>
        SelectedImages.TryGetValue(id, out SelectedImage? selected) ? selected : null;

    /// <summary>
    /// Whether newly available bytes for one selected image can change box geometry.
    /// </summary>
    public bool ImageResourceNeedsGeometry(DomTree tree, string url, ImageRequestProfile profile)
    {
        foreach ((NodeId id, SelectedImage selected) in SelectedImages)
        {
            if (!string.Equals(selected.ResolvedUrl, url, StringComparison.Ordinal)
                || selected.Profile != profile)
            {
                continue;
            }

            bool isImage = tree.GetNode(id)?.AsElement() is { } element
                && string.Equals(element.Name.Local, "img", StringComparison.Ordinal);
            if (!isImage)
            {
                return true;
            }

            if (!Layout.Styles.TryGetValue(id, out LayoutStyle? style))
            {
                return true;
            }

            // CSS replaced content has its own selected intrinsic metadata.
            if (style.ContentImage is not null)
            {
                return true;
            }

            bool fixedBox = style.Width.Kind == DimensionKind.Px
                && style.Height.Kind == DimensionKind.Px
                && style.MinWidth.Kind is DimensionKind.Auto or DimensionKind.Px
                && style.MinHeight.Kind is DimensionKind.Auto or DimensionKind.Px
                && style.MaxWidth.Kind is DimensionKind.Auto or DimensionKind.Px
                && style.MaxHeight.Kind is DimensionKind.Auto or DimensionKind.Px
                && !style.WidthFitContent
                && style.SizeExpressions.All(expression => expression is null);
            if (!fixedBox)
            {
                return true;
            }

            NodeId? parent = DomTraversal.RenderedParent(tree, id);
            while (parent is { } parentId)
            {
                if (!Layout.Styles.TryGetValue(parentId, out LayoutStyle? parentStyle))
                {
                    parent = DomTraversal.RenderedParent(tree, parentId);
                    continue;
                }

                if (parentStyle.DisplayContents)
                {
                    parent = DomTraversal.RenderedParent(tree, parentId);
                    continue;
                }

                return parentStyle.Display == Display.Grid
                    || (parentStyle.Display == Display.Flex && !parentStyle.InternalFlexContainer);
            }
        }

        return false;
    }
}
