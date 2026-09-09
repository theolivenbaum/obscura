// Port of the path/mask helpers of crates/obscura-render/src/paint.rs: `rounded_rect_path`,
// `box_clip_mask`, `overflow_clip_mask`, its cache, `intersect_clip_masks`,
// `rounded_box_clip_mask_radii`, and `polygon_clip_mask`.
using SkiaSharp;

namespace Obscura.Render;

internal readonly record struct OverflowClipMaskKey(
    uint BoundsX,
    uint BoundsY,
    uint BoundsWidth,
    uint BoundsHeight,
    int RoundedChain,
    uint RoundedOffsetX,
    uint RoundedOffsetY);

internal sealed class OverflowClipMaskCache
    : Dictionary<OverflowClipMaskKey, (RoundedOverflowClipChain? Chain, Mask Mask)>;

internal static class PaintClips
{
    /// <summary>
    /// Control-point distance, as a fraction of the radius, that makes a cubic Bezier
    /// approximate a quarter circle: <c>4/3 * (sqrt(2) - 1)</c>. The classic constant;
    /// the error against a true arc peaks near 0.02% of the radius.
    /// </summary>
    private const float ArcHandle = 0.55228475f;

    private const int MaxOverflowClipMaskCacheEntries = 2;

    /// <summary>
    /// A closed rounded-rectangle path, corners approximated by quadratic curves.
    /// </summary>
    internal static SKPath? RoundedRectPath(float x, float y, float w, float h, float rx, float ry) =>
        RoundedRectPathRadii(x, y, w, h, new ResolvedBorderRadii((rx, ry), (rx, ry), (rx, ry), (rx, ry)));

    internal static SKPath? RoundedRectPathRadii(
        float x,
        float y,
        float w,
        float h,
        ResolvedBorderRadii radii)
    {
        if (w <= 0f || h <= 0f || !float.IsFinite(x) || !float.IsFinite(y)
            || !float.IsFinite(w) || !float.IsFinite(h))
        {
            return null;
        }

        using SKPathBuilder builder = new();
        if (radii.IsZero())
        {
            builder.AddRect(new SKRect(x, y, x + w, y + h));
            return builder.Detach();
        }

        (float X, float Y) tl = radii.TopLeft;
        (float X, float Y) tr = radii.TopRight;
        (float X, float Y) br = radii.BottomRight;
        (float X, float Y) bl = radii.BottomLeft;

        // Each corner is a cubic approximation of a quarter ellipse, with its control
        // points ArcHandle of the radius along the tangents.
        //
        // These were quadratics whose single control point sat on the corner itself,
        // which is a parabola, not an arc: its midpoint is 6.1% further from the corner
        // centre than the true curve, so every rounded box was a squircle and
        // border-radius:50% drew something 1.2px fat on a 40px circle. The bulge scales
        // with the radius, so it was most visible exactly where a circle was intended.
        static float Cx(float r) => r * (1f - ArcHandle);

        builder.MoveTo(x + tl.X, y);
        builder.LineTo(x + w - tr.X, y);
        builder.CubicTo(x + w - Cx(tr.X), y, x + w, y + Cx(tr.Y), x + w, y + tr.Y);
        builder.LineTo(x + w, y + h - br.Y);
        builder.CubicTo(
            x + w, y + h - Cx(br.Y), x + w - Cx(br.X), y + h, x + w - br.X, y + h);
        builder.LineTo(x + bl.X, y + h);
        builder.CubicTo(x + Cx(bl.X), y + h, x, y + h - Cx(bl.Y), x, y + h - bl.Y);
        builder.LineTo(x, y + tl.Y);
        builder.CubicTo(x, y + Cx(tl.Y), x + Cx(tl.X), y, x + tl.X, y);
        builder.Close();
        return builder.Detach();
    }

    /// <summary>A full-pixmap clip mask admitting only the pixels inside <paramref name="rect"/>.</summary>
    internal static Mask? BoxClipMask(uint pw, uint ph, in Rect rect)
    {
        Mask? mask = Mask.New(pw, ph);
        if (mask is null || rect.Width <= 0f || rect.Height <= 0f
            || !float.IsFinite(rect.X) || !float.IsFinite(rect.Y))
        {
            return null;
        }

        using SKPathBuilder builder = new();
        builder.AddRect(new SKRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height));
        using SKPath path = builder.Detach();
        mask.FillPath(path, evenOdd: false, antiAlias: true);
        return mask;
    }

    /// <summary>Rasterize the complete inherited overflow clip chain.</summary>
    internal static Mask? OverflowClipMask(
        uint pw,
        uint ph,
        OverflowClip clip,
        (float Width, float Height) viewport)
    {
        Rect bounds = clip.ViewportRect(viewport);
        Mask? mask = BoxClipMask(pw, ph, bounds);
        if (mask is null)
        {
            return null;
        }

        (float X, float Y) offset = clip.RoundedOffset();
        RoundedOverflowClipChain? current = clip.RoundedChain();
        while (current is not null)
        {
            Rect rect = current.Clip.Rect with
            {
                X = current.Clip.Rect.X + offset.X,
                Y = current.Clip.Rect.Y + offset.Y,
            };
            SKPath? path = RoundedRectPathRadii(
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                current.Clip.Radii);
            if (path is not null)
            {
                mask.IntersectPath(path, evenOdd: false, antiAlias: true);
                path.Dispose();
            }

            current = current.Parent;
        }

        return mask;
    }

    private static OverflowClipMaskKey MaskKey(OverflowClip clip, (float Width, float Height) viewport)
    {
        Rect bounds = clip.ViewportRect(viewport);
        (float X, float Y) offset = clip.RoundedOffset();
        return new OverflowClipMaskKey(
            BitConverter.SingleToUInt32Bits(bounds.X),
            BitConverter.SingleToUInt32Bits(bounds.Y),
            BitConverter.SingleToUInt32Bits(bounds.Width),
            BitConverter.SingleToUInt32Bits(bounds.Height),
            clip.RoundedChain() is { } chain ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(chain) : 0,
            BitConverter.SingleToUInt32Bits(offset.X),
            BitConverter.SingleToUInt32Bits(offset.Y));
    }

    internal static Mask? CachedOverflowClipMask(
        OverflowClipMaskCache cache,
        uint pw,
        uint ph,
        OverflowClip clip,
        (float Width, float Height) viewport)
    {
        OverflowClipMaskKey key = MaskKey(clip, viewport);
        if (cache.TryGetValue(key, out (RoundedOverflowClipChain? Chain, Mask Mask) cached))
        {
            return cached.Mask;
        }

        Mask? mask = OverflowClipMask(pw, ph, clip, viewport);
        if (mask is null)
        {
            return null;
        }

        // A mask is one byte per output pixel. Two entries keep the shared scrollport fast
        // without turning a page with many distinct clips into an unbounded memory cache.
        if (cache.Count >= MaxOverflowClipMaskCacheEntries)
        {
            cache.Clear();
        }

        cache[key] = (clip.RoundedChain(), mask);
        return mask;
    }

    internal static Mask? IntersectClipMasks(Mask? first, Mask? second)
    {
        if (first is not null && second is not null)
        {
            int count = Math.Min(first.Data.Length, second.Data.Length);
            for (int i = 0; i < count; i++)
            {
                first.Data[i] = (byte)(((first.Data[i] * second.Data[i]) + 127) / 255);
            }

            return first;
        }

        return first ?? second?.Clone();
    }

    internal static Mask? RoundedBoxClipMaskRadii(
        uint pw,
        uint ph,
        in Rect rect,
        ResolvedBorderRadii radii)
    {
        if (radii.IsZero())
        {
            return BoxClipMask(pw, ph, rect);
        }

        Mask? mask = Mask.New(pw, ph);
        SKPath? path = RoundedRectPathRadii(rect.X, rect.Y, rect.Width, rect.Height, radii);
        if (mask is null || path is null)
        {
            path?.Dispose();
            return null;
        }

        mask.FillPath(path, evenOdd: false, antiAlias: true);
        path.Dispose();
        return mask;
    }

    /// <summary>
    /// Resolve a CSS polygon against its border-box reference and rasterize it as a
    /// full-surface alpha clip.
    /// </summary>
    internal static Mask? PolygonClipMask(
        uint pw,
        uint ph,
        ClipPathPolygon polygon,
        in Rect rect,
        float em,
        float rem,
        (float Width, float Height) viewport)
    {
        float? Resolve(Dimension coordinate, float basis)
        {
            Dimension resolved = coordinate.Resolve(em, rem, viewport.Width / 100f, viewport.Height / 100f);
            return resolved.Kind switch
            {
                DimensionKind.Px => resolved.Value,
                DimensionKind.Percent => resolved.Value * basis,
                _ => null,
            };
        }

        using SKPathBuilder builder = new();
        for (int index = 0; index < polygon.Points.Count; index++)
        {
            (Dimension px, Dimension py) = polygon.Points[index];
            if (Resolve(px, rect.Width) is not { } x || Resolve(py, rect.Height) is not { } y)
            {
                return null;
            }

            if (index == 0)
            {
                builder.MoveTo(rect.X + x, rect.Y + y);
            }
            else
            {
                builder.LineTo(rect.X + x, rect.Y + y);
            }
        }

        builder.Close();
        using SKPath path = builder.Detach();
        Mask? mask = Mask.New(pw, ph);
        if (mask is null)
        {
            return null;
        }

        // Degenerate polygons are valid CSS basic shapes but have no enclosed area. Keep the
        // newly zeroed mask in that case.
        if (polygon.Points.Count > 0)
        {
            mask.FillPath(path, polygon.FillRule == ClipPathFillRule.Evenodd, antiAlias: true);
        }

        return mask;
    }
}
