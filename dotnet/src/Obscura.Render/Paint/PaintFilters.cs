// Port of the CSS filter blur kernel in crates/obscura-render/src/paint.rs.
using System.Runtime.InteropServices;

namespace Obscura.Render;

/// <summary>
/// The gaussian blur behind <c>filter: blur()</c> and <c>backdrop-filter: blur()</c>.
/// </summary>
internal static class PaintFilters
{
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
    internal static void BlurPixmap(Pixmap pixmap, float sigma)
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
            BoxBlurAxis(pixels, scratch, width, height, stride, true, left, right);
            BoxBlurAxis(scratch, pixels, width, height, stride, false, left, right);
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
        uint right)
    {
        int outer = horizontal ? height : width;
        int inner = horizontal ? width : height;
        int step = horizontal ? 4 : stride;
        uint window = left + right + 1;
        int leftExtent = (int)left;
        int rightExtent = (int)right;
        Span<uint> sum = stackalloc uint[4];
        for (int o = 0; o < outer; o++)
        {
            int basePixel = horizontal ? o * stride : o * 4;
            sum.Clear();

            // Seed the running sum with the window at i = 0. Out-of-range samples count as
            // transparent, which is what the filter region needs: the layer is transparent
            // outside the painted area.
            int seed = Math.Min(rightExtent, inner - 1);
            for (int k = 0; k <= seed; k++)
            {
                int p = basePixel + (k * step);
                for (int c = 0; c < 4; c++)
                {
                    sum[c] += src[p + c];
                }
            }

            for (int i = 0; i < inner; i++)
            {
                int at = basePixel + (i * step);
                for (int c = 0; c < 4; c++)
                {
                    dst[at + c] = (byte)((sum[c] + (window / 2)) / window);
                }

                // Slide: drop the sample leaving the window, add the one entering.
                if (i >= leftExtent)
                {
                    int p = basePixel + ((i - leftExtent) * step);
                    for (int c = 0; c < 4; c++)
                    {
                        sum[c] -= src[p + c];
                    }
                }

                int incoming = i + rightExtent + 1;
                if (incoming < inner)
                {
                    int p = basePixel + (incoming * step);
                    for (int c = 0; c < 4; c++)
                    {
                        sum[c] += src[p + c];
                    }
                }
            }
        }
    }
}
