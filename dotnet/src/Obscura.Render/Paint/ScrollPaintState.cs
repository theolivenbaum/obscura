// Port of `ScrollPaintState` and `viewport_fixed_clip_map` in
// crates/obscura-render/src/paint.rs.
using Obscura.Dom;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render;

/// <summary>
/// Per-capture root-scroll and sticky offsets layered over an immutable document-space
/// <see cref="DomLayout"/>.
/// </summary>
internal sealed class ScrollPaintState
{
    private DomTree _tree = null!;
    private HashSet<NodeId> _viewportFixed = null!;
    private Dictionary<NodeId, (float X, float Y)> _sticky = null!;
    private Dictionary<NodeId, (float X, float Y)> _stickyClips = null!;
    private Dictionary<NodeId, OverflowClip?> _viewportFixedClips = null!;
    private ResolvedScrollState? _resolved;
    private NodeId? _clipScopeRoot;
    private (float X, float Y) _scroll;
    private bool _active;

    internal (float Width, float Height) Viewport { get; private set; }

    internal (float Width, float Height)? SurfaceExtent { get; private set; }

    internal (float X, float Y) SurfaceOffset { get; private set; }

    internal Dictionary<NodeId, (float X, float Y)> StickyTranslations => _sticky;

    internal Dictionary<NodeId, (float X, float Y)> StickyClipTranslations => _stickyClips;

    internal Dictionary<NodeId, OverflowClip?> ViewportFixedClips => _viewportFixedClips;

    internal static ScrollPaintState Create(
        DomTree tree,
        (float Width, float Height) viewport,
        (float X, float Y) requested,
        (float Width, float Height) content,
        HashSet<NodeId> viewportFixed,
        StickyLayout stickyLayout,
        ScrollTree scrollTree,
        DomLayout laid,
        ScrollPaintState? shared,
        NodeId? clipScopeRoot,
        (float Width, float Height)? surfaceExtent,
        (float X, float Y) surfaceOffset)
    {
        float scrollX = float.IsFinite(requested.X)
            ? Math.Clamp(
                RenderMath.QuantizeScrollValue(requested.X, 1f),
                0f,
                RenderMath.QuantizedScrollRange(content.Width, viewport.Width, 1f))
            : 0f;
        float scrollY = float.IsFinite(requested.Y)
            ? Math.Clamp(
                RenderMath.QuantizeScrollValue(requested.Y, 1f),
                0f,
                RenderMath.QuantizedScrollRange(content.Height, viewport.Height, 1f))
            : 0f;
        (float X, float Y) scroll = (scrollX, scrollY);
        bool active = scroll != (0f, 0f) || !stickyLayout.IsEmpty;

        Dictionary<NodeId, (float X, float Y)> sticky;
        Dictionary<NodeId, (float X, float Y)> stickyClips;
        Dictionary<NodeId, OverflowClip?> viewportFixedClips;
        if (shared is not null)
        {
            sticky = shared._sticky;
            stickyClips = shared._stickyClips;
            viewportFixedClips = shared._viewportFixedClips;
        }
        else
        {
            sticky = active
                ? stickyLayout.ResolvedRootTranslations(viewport, scrollTree, scroll)
                : [];
            stickyClips = stickyLayout.ClipTranslationsFrom(sticky);
            viewportFixedClips = ViewportFixedClipMap(tree, laid, viewportFixed, sticky);
        }

        return new ScrollPaintState
        {
            _tree = tree,
            Viewport = viewport,
            _scroll = scroll,
            _viewportFixed = viewportFixed,
            _sticky = sticky,
            _stickyClips = stickyClips,
            _viewportFixedClips = viewportFixedClips,
            _resolved = null,
            _clipScopeRoot = clipScopeRoot,
            SurfaceExtent = surfaceExtent,
            SurfaceOffset = surfaceOffset,
            _active = active,
        };
    }

    internal static ScrollPaintState FromResolved(
        DomTree tree,
        (float Width, float Height) viewport,
        HashSet<NodeId> viewportFixed,
        ResolvedScrollState resolved,
        NodeId? clipScopeRoot,
        (float Width, float Height)? surfaceExtent,
        (float X, float Y) surfaceOffset) => new()
        {
            _tree = tree,
            Viewport = viewport,
            _scroll = resolved.RootOffset(),
            _viewportFixed = viewportFixed,
            _sticky = [],
            _stickyClips = [],
            _viewportFixedClips = [],
            _resolved = resolved,
            _clipScopeRoot = clipScopeRoot,
            SurfaceExtent = surfaceExtent,
            SurfaceOffset = surfaceOffset,
            _active = true,
        };

    private static Dictionary<NodeId, OverflowClip?> ViewportFixedClipMap(
        DomTree tree,
        DomLayout laid,
        HashSet<NodeId> viewportFixed,
        Dictionary<NodeId, (float X, float Y)> sticky)
    {
        Dictionary<NodeId, OverflowClip?> output = new(viewportFixed.Count);
        foreach (NodeId id in viewportFixed)
        {
            bool startsSubtree = DomTraversal.RenderedParent(tree, id) is not { } parent
                || !viewportFixed.Contains(parent);
            if (startsSubtree)
            {
                Walk(tree, laid, viewportFixed, sticky, id, null, output);
            }
        }

        return output;

        static void Walk(
            DomTree tree,
            DomLayout laid,
            HashSet<NodeId> viewportFixed,
            Dictionary<NodeId, (float X, float Y)> sticky,
            NodeId id,
            OverflowClip? inherited,
            Dictionary<NodeId, OverflowClip?> output)
        {
            output[id] = inherited?.Clone();
            OverflowClip? next = inherited;
            if (laid.Styles.TryGetValue(id, out LayoutStyle? style)
                && laid.Rects.TryGetValue(id, out Rect rect)
                && style.OverflowHidden
                && !style.OverflowPropagatedToViewport)
            {
                (float X, float Y) authored =
                    laid.Translates.TryGetValue(id, out (float X, float Y) t) ? t : (0f, 0f);
                (float X, float Y) movement =
                    sticky.TryGetValue(id, out (float X, float Y) s) ? s : (0f, 0f);
                OverflowClip own = OverflowClip.ForBox(
                    rect,
                    style,
                    authored.X + movement.X,
                    authored.Y + movement.Y);
                next = inherited is null ? own : inherited.Intersect(own);
            }

            foreach (NodeId child in DomTraversal.RenderedChildren(tree, id))
            {
                if (viewportFixed.Contains(child))
                {
                    Walk(tree, laid, viewportFixed, sticky, child, next?.Clone(), output);
                }
            }
        }
    }

    internal (float X, float Y) TranslationFor(DomLayout laid, NodeId id)
    {
        (float X, float Y) baseOffset =
            laid.Translates.TryGetValue(id, out (float X, float Y) t) ? t : (0f, 0f);
        if (_resolved is { } resolved)
        {
            (float X, float Y) movement = resolved.MovementFor(id);
            return (
                baseOffset.X + movement.X + SurfaceOffset.X,
                baseOffset.Y + movement.Y + SurfaceOffset.Y);
        }

        if (!_active)
        {
            return (baseOffset.X + SurfaceOffset.X, baseOffset.Y + SurfaceOffset.Y);
        }

        (float X, float Y) sticky = _sticky.TryGetValue(id, out (float X, float Y) s) ? s : (0f, 0f);
        (float X, float Y) root = _viewportFixed.Contains(id) ? (0f, 0f) : (-_scroll.X, -_scroll.Y);
        return (
            baseOffset.X + sticky.X + root.X + SurfaceOffset.X,
            baseOffset.Y + sticky.Y + root.Y + SurfaceOffset.Y);
    }

    internal OverflowClip? OverflowClipFor(DomLayout laid, NodeId id)
    {
        if (_clipScopeRoot is null)
        {
            if (_resolved is { } resolvedState)
            {
                if (resolvedState.InheritedClipFor(id) is not { } inherited)
                {
                    return null;
                }

                OverflowClip clip = inherited.Clone();
                clip.Translate(SurfaceOffset.X, SurfaceOffset.Y);
                return clip;
            }

            if (_viewportFixedClips.TryGetValue(id, out OverflowClip? fixedClip))
            {
                if (fixedClip is null)
                {
                    return null;
                }

                OverflowClip clip = fixedClip.Clone();
                clip.Translate(SurfaceOffset.X, SurfaceOffset.Y);
                return clip;
            }
        }

        bool inViewportFixedSubtree = _viewportFixed.Contains(id);
        if (_clipScopeRoot is not null)
        {
            if (_clipScopeRoot == id)
            {
                return null;
            }

            List<NodeId> owners = [];
            NodeId? current = DomTraversal.RenderedParent(_tree, id);
            bool foundScope = false;
            bool foundFixedBoundary = false;
            while (current is { } owner)
            {
                // Crossing out of a viewport-fixed subtree would reintroduce a document-space
                // clip that fixed positioning escapes.
                if (inViewportFixedSubtree && !_viewportFixed.Contains(owner))
                {
                    foundFixedBoundary = true;
                    break;
                }

                owners.Add(owner);
                if (_clipScopeRoot == owner)
                {
                    foundScope = true;
                    break;
                }

                if (inViewportFixedSubtree
                    && (DomTraversal.RenderedParent(_tree, owner) is not { } parent
                        || !_viewportFixed.Contains(parent)))
                {
                    foundFixedBoundary = true;
                    break;
                }

                current = DomTraversal.RenderedParent(_tree, owner);
            }

            if (foundScope || foundFixedBoundary)
            {
                OverflowClip? clip = null;
                for (int index = owners.Count - 1; index >= 0; index--)
                {
                    NodeId owner = owners[index];
                    if (!laid.Styles.TryGetValue(owner, out LayoutStyle? ownerStyle)
                        || !laid.Rects.TryGetValue(owner, out Rect ownerRect))
                    {
                        continue;
                    }

                    if (!ownerStyle.OverflowHidden || ownerStyle.OverflowPropagatedToViewport)
                    {
                        continue;
                    }

                    (float x, float y) = TranslationFor(laid, owner);
                    OverflowClip own = OverflowClip.ForBox(ownerRect, ownerStyle, x, y);
                    clip = clip is null ? own : clip.Intersect(own);
                }

                return clip;
            }
        }

        if (_resolved is { } resolved)
        {
            if (resolved.InheritedClipFor(id) is not { } inherited)
            {
                return null;
            }

            OverflowClip clip = inherited.Clone();
            clip.Translate(SurfaceOffset.X, SurfaceOffset.Y);
            return clip;
        }

        if (!laid.ClipRects.TryGetValue(id, out OverflowClip? stored) || stored is null)
        {
            return null;
        }

        OverflowClip result = stored.Clone();
        if (_active && !_viewportFixed.Contains(id))
        {
            (float X, float Y) sticky =
                _stickyClips.TryGetValue(id, out (float X, float Y) s) ? s : (0f, 0f);
            result.Translate(sticky.X - _scroll.X, sticky.Y - _scroll.Y);
        }

        result.Translate(SurfaceOffset.X, SurfaceOffset.Y);
        return result;
    }

    internal OverflowClip? DescendantOverflowClipFor(DomLayout laid, NodeId id)
    {
        OverflowClip? inherited = OverflowClipFor(laid, id);
        if (!laid.Styles.TryGetValue(id, out LayoutStyle? style))
        {
            return inherited;
        }

        if (!style.OverflowHidden || style.OverflowPropagatedToViewport)
        {
            return inherited;
        }

        if (!laid.Rects.TryGetValue(id, out Rect rect))
        {
            return inherited;
        }

        (float ox, float oy) = TranslationFor(laid, id);
        OverflowClip own = OverflowClip.ForBox(rect, style, ox, oy);
        return inherited is null ? own : inherited.Intersect(own);
    }

    internal OverflowClip? ShapedTextOverflowClipFor(DomLayout laid, NodeId id) =>
        DescendantOverflowClipFor(laid, id);
}
