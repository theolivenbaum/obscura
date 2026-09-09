// Port of the sticky-position solver in crates/obscura-render/src/dom.rs.
using Obscura.Dom;

namespace Obscura.Render;

internal sealed class StickyFrame
{
    internal required NodeId Id { get; init; }

    internal required NodeId? ParentSticky { get; init; }

    internal required ScrollId ScrollOwner { get; init; }

    internal required NodeId? ScrollportNode { get; init; }

    internal required Rect Scrollport { get; init; }

    internal required Rect Normal { get; init; }

    internal required Rect Containing { get; init; }

    internal required bool ContainingIsScrollport { get; init; }

    internal required Edges Margin { get; init; }

    internal required Dimension?[] Inset { get; init; }

    internal required string?[] InsetExpressions { get; init; }

    internal required float FontSize { get; init; }

    internal required float RootFontSize { get; init; }

    internal required bool RtlInline { get; init; }
}

/// <summary>
/// Root-scroll sticky-position constraints captured from normal-flow layout.
/// </summary>
/// <remarks>
/// The normal boxes stay immutable in the layout cache. A scroll offset is resolved into one
/// accumulated translation per affected node, which keeps JS geometry and screenshot paint on
/// the same path.
/// </remarks>
public sealed class StickyLayout
{
    internal List<StickyFrame> Frames { get; } = [];

    internal Dictionary<NodeId, NodeId> Owners { get; } = [];

    internal Dictionary<NodeId, NodeId> ClipOwners { get; } = [];

    // Only populated by the public compatibility constructor. Production prepared renders own
    // the topology separately and avoid duplicating its dense node vectors.
    internal ScrollTree? CompatibilityScrollTree { get; set; }

    /// <summary>Whether no sticky frame was captured.</summary>
    public bool IsEmpty => Frames.Count == 0;

    private Dictionary<NodeId, (float X, float Y)> FrameOffsets(
        (float Width, float Height) viewport,
        (float X, float Y) scroll)
    {
        if (CompatibilityScrollTree is { } scrollTree)
        {
            List<(float X, float Y)> cumulative = RootOnlyCumulativeScroll(scrollTree, scroll);
            return ResolvedFrameOffsets(viewport, scrollTree, cumulative);
        }

        Dictionary<NodeId, (float X, float Y)> frameOffsets = new(Frames.Count);
        foreach (StickyFrame frame in Frames)
        {
            (float X, float Y) inherited = (0f, 0f);
            if (frame.ParentSticky is { } parent && frameOffsets.TryGetValue(parent, out var found))
            {
                inherited = found;
            }

            if (frame.ScrollOwner != ScrollId.Root)
            {
                // Tuple-only callers carry no element scroll state. A nested sticky frame
                // therefore has no local movement, but it still inherits an outer root-sticky
                // frame when one exists.
                frameOffsets[frame.Id] = inherited;
                continue;
            }

            Rect normal = frame.Normal with
            {
                X = frame.Normal.X + inherited.X,
                Y = frame.Normal.Y + inherited.Y,
            };
            Rect containing = frame.Containing with
            {
                X = frame.Containing.X + inherited.X,
                Y = frame.Containing.Y + inherited.Y,
            };
            float x = StickyAxisPosition(
                normal.X,
                normal.Width,
                containing.X + frame.Margin.Left,
                containing.X + containing.Width - frame.Margin.Right - normal.Width,
                scroll.X,
                viewport.Width,
                ResolveFrameStickyInset(frame, 3, viewport.Width, viewport),
                ResolveFrameStickyInset(frame, 1, viewport.Width, viewport),
                frame.RtlInline);
            float y = StickyAxisPosition(
                normal.Y,
                normal.Height,
                containing.Y + frame.Margin.Top,
                containing.Y + containing.Height - frame.Margin.Bottom - normal.Height,
                scroll.Y,
                viewport.Height,
                ResolveFrameStickyInset(frame, 0, viewport.Height, viewport),
                ResolveFrameStickyInset(frame, 2, viewport.Height, viewport),
                false);
            frameOffsets[frame.Id] = (inherited.X + x - normal.X, inherited.Y + y - normal.Y);
        }

        return frameOffsets;
    }

    internal Dictionary<NodeId, (float X, float Y)> ResolvedTranslations(
        (float Width, float Height) viewport,
        ScrollTree scrollTree,
        IReadOnlyList<(float X, float Y)> cumulativeScroll)
    {
        Dictionary<NodeId, (float X, float Y)> frameOffsets =
            ResolvedFrameOffsets(viewport, scrollTree, cumulativeScroll);
        Dictionary<NodeId, (float X, float Y)> result = [];
        foreach ((NodeId id, NodeId owner) in Owners)
        {
            if (frameOffsets.TryGetValue(owner, out var offset))
            {
                result[id] = offset;
            }
        }

        return result;
    }

    internal Dictionary<NodeId, (float X, float Y)> ResolvedRootTranslations(
        (float Width, float Height) viewport,
        ScrollTree scrollTree,
        (float X, float Y) scroll) =>
        ResolvedTranslations(viewport, scrollTree, RootOnlyCumulativeScroll(scrollTree, scroll));

    private Dictionary<NodeId, (float X, float Y)> ResolvedFrameOffsets(
        (float Width, float Height) viewport,
        ScrollTree scrollTree,
        IReadOnlyList<(float X, float Y)> cumulativeScroll)
    {
        Dictionary<NodeId, (float X, float Y)> frameOffsets = new(Frames.Count);
        foreach (StickyFrame frame in Frames)
        {
            (float X, float Y) inherited = (0f, 0f);
            if (frame.ParentSticky is { } parentSticky
                && frameOffsets.TryGetValue(parentSticky, out var inheritedOffset))
            {
                inherited = inheritedOffset;
            }

            ScrollContainer container = scrollTree.Containers[frame.ScrollOwner.Index];
            (float X, float Y) contentMove = frame.ScrollOwner.Index < cumulativeScroll.Count
                ? cumulativeScroll[frame.ScrollOwner.Index]
                : (0f, 0f);
            (float X, float Y) portParentMove = (0f, 0f);
            if (container.Parent is { } parentScroll && parentScroll.Index < cumulativeScroll.Count)
            {
                portParentMove = cumulativeScroll[parentScroll.Index];
            }

            (float X, float Y) portSticky = (0f, 0f);
            if (frame.ScrollportNode is { } portNode
                && Owners.TryGetValue(portNode, out NodeId portOwner)
                && frameOffsets.TryGetValue(portOwner, out var portOffset))
            {
                portSticky = portOffset;
            }

            Rect normal = frame.Normal with
            {
                X = frame.Normal.X + contentMove.X + inherited.X,
                Y = frame.Normal.Y + contentMove.Y + inherited.Y,
            };
            Rect scrollport = frame.ScrollOwner == ScrollId.Root
                ? new Rect(0f, 0f, viewport.Width, viewport.Height)
                : frame.Scrollport with
                {
                    X = frame.Scrollport.X + portParentMove.X + portSticky.X,
                    Y = frame.Scrollport.Y + portParentMove.Y + portSticky.Y,
                };
            (float X, float Y) containingMove = frame.ContainingIsScrollport
                ? (portParentMove.X + portSticky.X, portParentMove.Y + portSticky.Y)
                : (contentMove.X + inherited.X, contentMove.Y + inherited.Y);
            Rect containing = frame.Containing with
            {
                X = frame.Containing.X + containingMove.X,
                Y = frame.Containing.Y + containingMove.Y,
            };
            float x = StickyAxisPosition(
                normal.X,
                normal.Width,
                containing.X + frame.Margin.Left,
                containing.X + containing.Width - frame.Margin.Right - normal.Width,
                scrollport.X,
                scrollport.Width,
                ResolveFrameStickyInset(frame, 3, scrollport.Width, viewport),
                ResolveFrameStickyInset(frame, 1, scrollport.Width, viewport),
                frame.RtlInline);
            float y = StickyAxisPosition(
                normal.Y,
                normal.Height,
                containing.Y + frame.Margin.Top,
                containing.Y + containing.Height - frame.Margin.Bottom - normal.Height,
                scrollport.Y,
                scrollport.Height,
                ResolveFrameStickyInset(frame, 0, scrollport.Height, viewport),
                ResolveFrameStickyInset(frame, 2, scrollport.Height, viewport),
                false);
            frameOffsets[frame.Id] = (inherited.X + x - normal.X, inherited.Y + y - normal.Y);
        }

        return frameOffsets;
    }

    internal (float X, float Y) ResolvedTranslationFor(
        NodeId id,
        (float Width, float Height) viewport,
        ScrollTree scrollTree,
        IReadOnlyList<(float X, float Y)> cumulativeScroll)
    {
        if (!Owners.TryGetValue(id, out NodeId owner))
        {
            return (0f, 0f);
        }

        return ResolvedFrameOffsets(viewport, scrollTree, cumulativeScroll)
            .TryGetValue(owner, out var offset) ? offset : (0f, 0f);
    }

    /// <summary>
    /// Resolve a single geometry query without materializing an entry for every descendant in
    /// every sticky subtree. This is O(sticky frames), not O(DOM).
    /// </summary>
    public (float X, float Y) TranslationFor(
        NodeId id,
        (float Width, float Height) viewport,
        (float X, float Y) scroll)
    {
        if (!Owners.TryGetValue(id, out NodeId owner))
        {
            return (0f, 0f);
        }

        return FrameOffsets(viewport, scroll).TryGetValue(owner, out var offset) ? offset : (0f, 0f);
    }

    /// <summary>Accumulated sticky translation for every node in a sticky subtree.</summary>
    public Dictionary<NodeId, (float X, float Y)> Translations(
        (float Width, float Height) viewport,
        (float X, float Y) scroll)
    {
        Dictionary<NodeId, (float X, float Y)> frameOffsets = FrameOffsets(viewport, scroll);
        Dictionary<NodeId, (float X, float Y)> result = [];
        foreach ((NodeId id, NodeId owner) in Owners)
        {
            if (frameOffsets.TryGetValue(owner, out var offset))
            {
                result[id] = offset;
            }
        }

        return result;
    }

    /// <summary>
    /// Sticky-space movement of the ancestor that owns a node's inherited overflow clip.
    /// </summary>
    public Dictionary<NodeId, (float X, float Y)> ClipTranslationsFrom(
        IReadOnlyDictionary<NodeId, (float X, float Y)> translations)
    {
        ArgumentNullException.ThrowIfNull(translations);
        Dictionary<NodeId, (float X, float Y)> result = [];
        foreach ((NodeId id, NodeId owner) in ClipOwners)
        {
            if (translations.TryGetValue(owner, out var offset))
            {
                result[id] = offset;
            }
        }

        return result;
    }

    internal static List<(float X, float Y)> RootOnlyCumulativeScroll(
        ScrollTree scrollTree,
        (float X, float Y) scroll)
    {
        List<(float X, float Y)> cumulative = [];
        for (int index = 0; index < scrollTree.Containers.Count; index++)
        {
            cumulative.Add((0f, 0f));
        }

        if (cumulative.Count > 0)
        {
            cumulative[0] = (-scroll.X, -scroll.Y);
        }

        for (int index = 1; index < cumulative.Count; index++)
        {
            cumulative[index] = scrollTree.Containers[index].Parent is { } parent
                ? cumulative[parent.Index]
                : (0f, 0f);
        }

        return cumulative;
    }

    internal static float? ResolveStickyInset(Dimension? value, float basis) => value switch
    {
        { Kind: DimensionKind.Px } px => px.Value,
        { Kind: DimensionKind.Percent } percent => percent.Value * basis,
        _ => null,
    };

    internal static float? ResolveFrameStickyInset(
        StickyFrame frame,
        int index,
        float percentBasis,
        (float Width, float Height) viewport)
    {
        if (frame.InsetExpressions[index] is { } expression)
        {
            return ComputedStyle.ResolveContextualLength(
                expression,
                frame.FontSize,
                frame.RootFontSize,
                viewport.Width / 100f,
                viewport.Height / 100f,
                percentBasis);
        }

        return ResolveStickyInset(frame.Inset[index], percentBasis);
    }

    internal static float StickyAxisPosition(
        float normal,
        float size,
        float containMin,
        float containMax,
        float scroll,
        float viewport,
        float? start,
        float? end,
        bool endIsInlineStart)
    {
        float? stickStart = start is { } startInset ? scroll + startInset : null;
        float? stickEnd = end is { } endInset ? scroll + viewport - endInset - size : null;

        // When both insets leave a sticky view rectangle smaller than the box, the physical end
        // inset is reduced in LTR, while the physical start inset is reduced in RTL so the
        // logical inline-start edge wins.
        if (stickStart is { } s && stickEnd is { } e && e < s)
        {
            if (endIsInlineStart)
            {
                stickStart = e;
            }
            else
            {
                stickEnd = s;
            }
        }

        float position = normal;
        if (stickStart is { } resolvedStart)
        {
            position = F32.Max(position, F32.Min(resolvedStart, containMax));
        }

        if (stickEnd is { } resolvedEnd)
        {
            position = F32.Min(position, F32.Max(resolvedEnd, containMin));
        }

        return position;
    }
}
