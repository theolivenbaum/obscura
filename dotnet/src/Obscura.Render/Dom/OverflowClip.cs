// Port of the accumulated overflow clip in crates/obscura-render/src/dom.rs.
namespace Obscura.Render;

/// <summary>One accumulated rounded clip in a chain.</summary>
internal readonly record struct RoundedOverflowClip(Rect Rect, ResolvedBorderRadii Radii);

/// <summary>A rounded clip plus the enclosing chain it intersects with.</summary>
internal sealed record RoundedOverflowClipChain(
    RoundedOverflowClip Clip,
    RoundedOverflowClipChain? Parent);

/// <summary>
/// One accumulated overflow clip with independent physical axes.
/// </summary>
/// <remarks>
/// <c>null</c> on an axis means unbounded on that axis. This avoids representing
/// <c>overflow-x:clip</c> with an artificial enormous Y rectangle, which can corrupt
/// scrolling overflow and transformed clip intersections.
/// </remarks>
public sealed class OverflowClip
{
    private (float Start, float End)? _x;
    private (float Start, float End)? _y;
    private RoundedOverflowClipChain? _rounded;
    private (float X, float Y) _roundedOffset;

    /// <summary>An unbounded clip.</summary>
    public OverflowClip()
    {
    }

    private OverflowClip(
        (float Start, float End)? x,
        (float Start, float End)? y,
        RoundedOverflowClipChain? rounded,
        (float X, float Y) roundedOffset)
    {
        _x = x;
        _y = y;
        _rounded = rounded;
        _roundedOffset = roundedOffset;
    }

    internal static OverflowClip ForBox(in Rect rect, LayoutStyle style, float tx, float ty)
    {
        float left = rect.X + tx + style.Border.Left;
        float top = rect.Y + ty + style.Border.Top;
        float right = F32.Max(rect.X + tx + rect.Width - style.Border.Right, left);
        float bottom = F32.Max(rect.Y + ty + rect.Height - style.Border.Bottom, top);
        bool clipsX = style.ClipsOverflowX();
        bool clipsY = style.ClipsOverflowY();
        Rect paddingRect = new(left, top, F32.Max(right - left, 0f), F32.Max(bottom - top, 0f));
        ResolvedBorderRadii radii = style.BorderModel.Radii
            .Resolve(rect.Width, rect.Height)
            .Inset(new Sides<float>(
                style.Border.Top,
                style.Border.Right,
                style.Border.Bottom,
                style.Border.Left));

        // A rounded corner constrains both axes. Keep the existing independent rectangular
        // representation for one-axis overflow clips; applying a closed rounded path there
        // would incorrectly bound the visible axis.
        RoundedOverflowClipChain? rounded = clipsX && clipsY && !radii.IsZero()
            ? new RoundedOverflowClipChain(new RoundedOverflowClip(paddingRect, radii), null)
            : null;

        return new OverflowClip(
            clipsX ? (left, right) : null,
            clipsY ? (top, bottom) : null,
            rounded,
            (0f, 0f));
    }

    internal OverflowClip Clone() => new(_x, _y, _rounded, _roundedOffset);

    internal OverflowClip Intersect(OverflowClip other)
    {
        static (float Start, float End)? Axis((float Start, float End)? a, (float Start, float End)? b)
        {
            if (a is { } left && b is { } right)
            {
                float start = F32.Max(left.Start, right.Start);
                return (start, F32.Max(F32.Min(left.End, right.End), start));
            }

            return a ?? b;
        }

        RoundedOverflowClipChain? rounded = _rounded;
        List<RoundedOverflowClip> otherClips = [];
        RoundedOverflowClipChain? current = other._rounded;
        while (current is not null)
        {
            RoundedOverflowClip clip = current.Clip with
            {
                Rect = current.Clip.Rect with
                {
                    X = current.Clip.Rect.X + other._roundedOffset.X - _roundedOffset.X,
                    Y = current.Clip.Rect.Y + other._roundedOffset.Y - _roundedOffset.Y,
                },
            };
            otherClips.Add(clip);
            current = current.Parent;
        }

        for (int index = otherClips.Count - 1; index >= 0; index--)
        {
            rounded = new RoundedOverflowClipChain(otherClips[index], rounded);
        }

        return new OverflowClip(Axis(_x, other._x), Axis(_y, other._y), rounded, _roundedOffset);
    }

    internal Rect? IntersectRect(in Rect rect)
    {
        float left = _x is { } x0 ? F32.Max(rect.X, x0.Start) : rect.X;
        float right = _x is { } x1 ? F32.Min(rect.X + rect.Width, x1.End) : rect.X + rect.Width;
        float top = _y is { } y0 ? F32.Max(rect.Y, y0.Start) : rect.Y;
        float bottom = _y is { } y1 ? F32.Min(rect.Y + rect.Height, y1.End) : rect.Y + rect.Height;
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : null;
    }

    internal void Translate(float dx, float dy)
    {
        if (_x is { } x)
        {
            _x = (x.Start + dx, x.End + dx);
        }

        if (_y is { } y)
        {
            _y = (y.Start + dy, y.End + dy);
        }

        _roundedOffset = (_roundedOffset.X + dx, _roundedOffset.Y + dy);
    }

    internal RoundedOverflowClipChain? RoundedChain() => _rounded;

    internal (float X, float Y) RoundedOffset() => _roundedOffset;

    internal Rect ViewportRect((float Width, float Height) viewport)
    {
        (float left, float right) = _x ?? (0f, viewport.Width);
        (float top, float bottom) = _y ?? (0f, viewport.Height);
        return new Rect(left, top, F32.Max(right - left, 0f), F32.Max(bottom - top, 0f));
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is OverflowClip other
        && Nullable.Equals(_x, other._x)
        && Nullable.Equals(_y, other._y)
        && _roundedOffset == other._roundedOffset
        && Equals(_rounded, other._rounded);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_x, _y, _roundedOffset);
}
