// Shared helpers for the dom.rs port that several of its files need.
using Obscura.Dom;

namespace Obscura.Render;

internal static class LayoutDomInternals
{
    internal static Rect? ShapedItemClip(
        NodeId owner,
        IReadOnlyDictionary<NodeId, Rect> rects,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IReadOnlyDictionary<NodeId, OverflowClip?> clipRects,
        IReadOnlyDictionary<NodeId, (float X, float Y)> translates,
        (float Width, float Height) viewport)
    {
        OverflowClip? inherited = clipRects.TryGetValue(owner, out OverflowClip? found)
            ? found?.Clone()
            : null;
        OverflowClip? clip = inherited;
        if (styles.TryGetValue(owner, out LayoutStyle? style)
            && rects.TryGetValue(owner, out Rect rect)
            && style.OverflowHidden)
        {
            (float tx, float ty) = translates.TryGetValue(owner, out var translate) ? translate : (0f, 0f);
            OverflowClip own = OverflowClip.ForBox(rect, style, tx, ty);
            clip = inherited is null ? own : inherited.Intersect(own);
        }

        return clip?.ViewportRect(viewport);
    }
}
