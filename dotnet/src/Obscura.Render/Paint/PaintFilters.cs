// Port of the CSS filter blur kernel in crates/obscura-render/src/paint.rs.
using System.Runtime.InteropServices;

namespace Obscura.Render;

/// <summary>
/// The gaussian blur behind <c>filter: blur()</c> and <c>backdrop-filter: blur()</c>.
/// </summary>
internal static class PaintFilters
{
    /// <summary>What a box-blur window sees when it reaches past the edge of the surface.</summary>
    internal enum BlurEdge
    {
        /// <summary>
        /// Out-of-range samples are transparent. Correct for <c>filter</c>, whose layer really
        /// is empty outside the painted area, and for <c>backdrop-filter</c>, whose partly
        /// transparent edge band is what lets the sharp backdrop show through.
        /// </summary>
        Transparent,

        /// <summary>Out-of-range samples repeat the nearest edge pixel.</summary>
        Clamp,
    }

    /// <summary>
    /// The three box-blur window extents that approximate a gaussian of
    /// <paramref name="sigma"/>, per the algorithm SVG's <c>feGaussianBlur</c> defines
    /// normatively and that CSS <c>blur()</c> is specified in terms of.
    /// </summary>
    /// <remarks>
    /// Each pass is <c>(left, right)</c>, so its window covers <c>left + right + 1</c>
    /// pixels. With <c>d = floor(sigma * 3 * sqrt(2*PI) / 4 + 0.5)</c>: an odd <c>d</c>
    /// uses three windows of <c>d</c> centred on the output pixel; an even <c>d</c> uses
    /// two windows of <c>d</c> centred on the pixel boundaries either side, then one of
    /// <c>d + 1</c> centred.
    /// </remarks>
    internal static (uint Left, uint Right)[]? BoxBlurExtents(float sigma)
    {
        if (!float.IsFinite(sigma) || sigma <= 0f)
        {
            return null;
        }

        float d = MathF.Floor((sigma * 3f * MathF.Sqrt(2f * MathF.PI) / 4f) + 0.5f);
        if (!(d >= 1f))
        {
            return null;
        }

        uint size = Math.Min((uint)d, 1u << 14);
        if (size % 2 == 1)
        {
            uint half = (size - 1) / 2;
            return [(half, half), (half, half), (half, half)];
        }
        else
        {
            uint half = size / 2;
            return [(half, half - 1), (half - 1, half), (half, half)];
        }
    }

    /// <summary>
    /// Blur <paramref name="pixmap"/> in place with the three-pass box approximation of a
    /// gaussian.
    /// </summary>
    /// <remarks>
    /// Operates on the premultiplied bytes, which is what keeps a transparent edge from
    /// bleeding the colour under it into the result.
    /// </remarks>
    internal static void BlurPixmap(Pixmap pixmap, float sigma, BlurEdge edge)
    {
        if (BoxBlurExtents(sigma) is not { } extents)
        {
            return;
        }

        int width = (int)pixmap.Width;
        int height = (int)pixmap.Height;
        if (width == 0 || height == 0)
        {
            return;
        }

        int stride = width * 4;
        Span<byte> pixels = MemoryMarshal.AsBytes(pixmap.Pixels.AsSpan());
        byte[] scratch = new byte[stride * height];
        foreach ((uint left, uint right) in extents)
        {
            // Horizontal, then vertical: a box blur is separable, so six linear passes
            // total rather than one quadratic convolution.
            BoxBlurAxis(pixels, scratch, width, height, stride, true, left, right, edge);
            BoxBlurAxis(scratch, pixels, width, height, stride, false, left, right, edge);
        }
    }

    /// <summary>
    /// One separable box-blur pass over a premultiplied RGBA window, as a running sum so
    /// the cost is independent of the window size.
    /// </summary>
    private static void BoxBlurAxis(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int width,
        int height,
        int stride,
        bool horizontal,
        uint left,
        uint right,
        BlurEdge edge)
    {
        int outer = horizontal ? height : width;
        int inner = horizontal ? width : height;
        int step = horizontal ? 4 : stride;
        if (inner == 0)
        {
            return;
        }

        uint window = left + right + 1;
        int leftExtent = (int)left;
        int rightExtent = (int)right;
        int last = inner - 1;
        Span<uint> sum = stackalloc uint[4];
        for (int o = 0; o < outer; o++)
        {
            int basePixel = horizontal ? o * stride : o * 4;

            sum.Clear();
            for (int k = -leftExtent; k <= rightExtent; k++)
            {
                AddSample(src, sum, basePixel, step, last, k, edge, add: true);
            }

            for (int i = 0; i < inner; i++)
            {
                int at = basePixel + (i * step);
                for (int c = 0; c < 4; c++)
                {
                    dst[at + c] = (byte)((sum[c] + (window / 2)) / window);
                }

                // Slide: drop the sample leaving the window, add the one entering.
                AddSample(src, sum, basePixel, step, last, i - leftExtent, edge, add: false);
                AddSample(src, sum, basePixel, step, last, i + rightExtent + 1, edge, add: true);
            }
        }
    }

    /// <summary>
    /// Add or subtract one sample of the running box-blur sum, with the edge rule applied.
    /// Out of range contributes nothing under <see cref="BlurEdge.Transparent"/>, and the
    /// nearest edge pixel under <see cref="BlurEdge.Clamp"/>.
    /// </summary>
    /// <remarks>
    /// A separate method rather than a local function because a <c>ReadOnlySpan</c>
    /// parameter cannot be captured by one.
    /// </remarks>
    private static void AddSample(
        ReadOnlySpan<byte> src,
        Span<uint> sum,
        int basePixel,
        int step,
        int last,
        int index,
        BlurEdge edge,
        bool add)
    {
        int at;
        if (index >= 0 && index <= last)
        {
            at = basePixel + (index * step);
        }
        else if (edge == BlurEdge.Clamp)
        {
            at = basePixel + (Math.Clamp(index, 0, last) * step);
        }
        else
        {
            return;
        }

        if (add)
        {
            for (int c = 0; c < 4; c++)
            {
                sum[c] += src[at + c];
            }
        }
        else
        {
            for (int c = 0; c < 4; c++)
            {
                sum[c] -= src[at + c];
            }
        }
    }

    /// <summary>
    /// Blur the already-painted backdrop under <paramref name="rect"/> and put it back,
    /// clipped to the element's own rounded border box.
    /// </summary>
    /// <remarks>
    /// <c>backdrop-filter</c> reads what is behind the element, so it runs against the
    /// surface as it stands before the element paints anything of its own.
    /// <para>
    /// Two details decide how the edges look, and both were measured against Chromium on a
    /// blurred panel over a 45-degree stripe backdrop rather than reasoned about. The
    /// filter region is the element's own border box, so the blur averages only backdrop
    /// from inside it; reaching 3 sigma further out to find "real" backdrop reads 13.07
    /// mean abs against Chromium and cropping to the box reads 12.07, both wrong at the
    /// edges. And out-of-region samples are transparent rather than edge-duplicated, with
    /// the filtered backdrop composited <em>over</em> the sharp original rather than
    /// replacing it: the blurred copy is partly transparent in a band about 3 sigma wide
    /// and the unblurred backdrop shows through, which is what produces Chromium's
    /// gradient from the local colour at the very edge to the blurred average further in.
    /// That reads 3.97, against 97.46 for not implementing the property at all.
    /// </para>
    /// </remarks>
    internal static void PaintBackdropFilter(
        Pixmap pixmap,
        in Rect rect,
        BorderRadii borderRadius,
        float sigma,
        Mask? ancestorClip)
    {
        if (!float.IsFinite(sigma) || sigma <= 0f || rect.Width <= 0f || rect.Height <= 0f)
        {
            return;
        }

        if (!PaintDomPainter.RectIntersectsPaintSurface(rect, pixmap, 1f))
        {
            return;
        }

        int x0 = (int)F32.Max(MathF.Floor(rect.X), 0f);
        int y0 = (int)F32.Max(MathF.Floor(rect.Y), 0f);
        int x1 = (int)Math.Clamp(MathF.Ceiling(rect.X + rect.Width), 0f, pixmap.Width);
        int y1 = (int)Math.Clamp(MathF.Ceiling(rect.Y + rect.Height), 0f, pixmap.Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        Pixmap? sampled = pixmap.CloneRect(x0, y0, (uint)(x1 - x0), (uint)(y1 - y0));
        if (sampled is null)
        {
            return;
        }

        using Pixmap backdrop = sampled;
        BlurPixmap(backdrop, sigma, BlurEdge.Transparent);

        ResolvedBorderRadii radii = borderRadius.Resolve(rect.Width, rect.Height);
        Mask? boxMask = PaintClips.RoundedBoxClipMaskRadii(
            pixmap.Width, pixmap.Height, rect, radii);
        if (boxMask is null)
        {
            return;
        }

        Mask? clip = PaintClips.IntersectClipMasks(ancestorClip?.Clone(), boxMask);
        Surface.DrawPixmap(pixmap, x0, y0, backdrop, 1f, false, Affine2.Identity, clip);
    }
}
