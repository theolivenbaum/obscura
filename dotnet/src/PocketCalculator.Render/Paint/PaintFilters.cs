// Port of the CSS filter blur kernel in crates/obscura-render/src/paint.rs.
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PocketCalculator.Render;

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

    /// <summary>
    /// Whether a computed <c>filter</c> list changes any pixel, and so is worth the group
    /// layer a filter has to paint into.
    /// </summary>
    /// <remarks>
    /// An identity function is common enough to be worth testing for - a stylesheet that
    /// animates <c>blur()</c> or <c>grayscale()</c> spends most frames at the identity end,
    /// and a layer allocated for it would be a full-surface pixmap plus a full subtree repaint.
    /// <para>
    /// A <c>url()</c> reference answers <c>false</c>: SVG filter elements are not modeled, so
    /// it is reported through <c>getComputedStyle</c> but paints nothing. A list of nothing but
    /// references therefore takes no layer at all rather than an empty one.
    /// </para>
    /// </remarks>
    internal static bool HasVisibleEffect(FilterFunction[]? filter)
    {
        if (filter is null)
        {
            return false;
        }

        foreach (FilterFunction function in filter)
        {
            bool visible = function.Kind switch
            {
                FilterFunctionKind.Blur => float.IsFinite(function.Amount) && function.Amount > 0f,
                FilterFunctionKind.HueRotate =>
                    float.IsFinite(function.Amount) && MathF.IEEERemainder(function.Amount, 360f) != 0f,
                FilterFunctionKind.DropShadow => function.Color.A != 0,
                FilterFunctionKind.Grayscale or FilterFunctionKind.Invert
                    or FilterFunctionKind.Sepia => function.Amount > 0f,
                FilterFunctionKind.Brightness or FilterFunctionKind.Contrast
                    or FilterFunctionKind.Opacity or FilterFunctionKind.Saturate =>
                        function.Amount != 1f,
                _ => false,
            };

            if (visible) return true;
        }

        return false;
    }

    /// <summary>
    /// Apply a computed <c>filter</c> list to a finished group layer, in place and in order.
    /// </summary>
    /// <remarks>
    /// The whole chain runs on the premultiplied surface the rest of the paint stack uses, so
    /// this mirrors <see cref="BlurPixmap"/> rather than reaching for Skia's
    /// <c>SKImageFilter</c>: the group layer is already a <see cref="Pixmap"/>, and the
    /// colour-matrix functions are defined on the same 8-bit sRGB values it stores. Filter
    /// Effects specifies these with <c>color-interpolation-filters: sRGB</c>, so there is no
    /// linearization step.
    /// </remarks>
    internal static void ApplyFilterChain(Pixmap layer, FilterFunction[] filter)
    {
        foreach (FilterFunction function in filter)
        {
            switch (function.Kind)
            {
                case FilterFunctionKind.Blur:
                    if (float.IsFinite(function.Amount) && function.Amount > 0f)
                    {
                        BlurPixmap(layer, function.Amount, BlurEdge.Transparent);
                    }

                    break;

                case FilterFunctionKind.DropShadow:
                    DrawDropShadowUnder(layer, function);
                    break;

                case FilterFunctionKind.Opacity:
                    ScaleAlpha(layer, function.Amount);
                    break;

                // url() is reported but never painted: SVG filter elements are not modeled.
                case FilterFunctionKind.Reference:
                    break;

                default:
                    ApplyColorMatrix(layer, ColorMatrixFor(function));
                    break;
            }
        }
    }

    /// <summary>
    /// A 3x3 sRGB colour matrix plus the offset the shorthand filters add equally to all three
    /// channels. Alpha is untouched by every function that uses one.
    /// </summary>
    private readonly record struct ColorMatrix(
        float M00, float M01, float M02,
        float M10, float M11, float M12,
        float M20, float M21, float M22,
        float Offset);

    /// <summary>
    /// The <c>feColorMatrix</c> each shorthand filter is defined as, from Filter Effects
    /// &#xA7;<c>filter-effects-1</c>.
    /// </summary>
    /// <remarks>
    /// <c>grayscale(a)</c> is not its own matrix: the spec defines it as
    /// <c>saturate(1 - a)</c>, so it is forwarded rather than duplicated. The luminance
    /// weights here are <c>feColorMatrix</c>'s historical 0.213 / 0.715 / 0.072, which are not
    /// quite the 0.2126 / 0.7152 / 0.0722 of Rec. 709; Chromium uses the spec's.
    /// </remarks>
    private static ColorMatrix ColorMatrixFor(FilterFunction function)
    {
        float amount = function.Amount;
        switch (function.Kind)
        {
            case FilterFunctionKind.Brightness:
                return new ColorMatrix(amount, 0f, 0f, 0f, amount, 0f, 0f, 0f, amount, 0f);

            case FilterFunctionKind.Contrast:
                return new ColorMatrix(
                    amount, 0f, 0f,
                    0f, amount, 0f,
                    0f, 0f, amount,
                    (1f - amount) * 0.5f);

            case FilterFunctionKind.Invert:
            {
                float slope = 1f - (2f * amount);
                return new ColorMatrix(slope, 0f, 0f, 0f, slope, 0f, 0f, 0f, slope, amount);
            }

            case FilterFunctionKind.Sepia:
            {
                float s = 1f - amount;
                return new ColorMatrix(
                    0.393f + (0.607f * s), 0.769f - (0.769f * s), 0.189f - (0.189f * s),
                    0.349f - (0.349f * s), 0.686f + (0.314f * s), 0.168f - (0.168f * s),
                    0.272f - (0.272f * s), 0.534f - (0.534f * s), 0.131f + (0.869f * s),
                    0f);
            }

            case FilterFunctionKind.HueRotate:
            {
                float radians = amount * (MathF.PI / 180f);
                float cos = MathF.Cos(radians);
                float sin = MathF.Sin(radians);
                return new ColorMatrix(
                    0.213f + (cos * 0.787f) - (sin * 0.213f),
                    0.715f - (cos * 0.715f) - (sin * 0.715f),
                    0.072f - (cos * 0.072f) + (sin * 0.928f),
                    0.213f - (cos * 0.213f) + (sin * 0.143f),
                    0.715f + (cos * 0.285f) + (sin * 0.140f),
                    0.072f - (cos * 0.072f) - (sin * 0.283f),
                    0.213f - (cos * 0.213f) - (sin * 0.787f),
                    0.715f - (cos * 0.715f) + (sin * 0.715f),
                    0.072f + (cos * 0.928f) + (sin * 0.072f),
                    0f);
            }

            // grayscale(a) is saturate(1 - a).
            case FilterFunctionKind.Grayscale:
                amount = 1f - amount;
                goto case FilterFunctionKind.Saturate;

            case FilterFunctionKind.Saturate:
            default:
                return new ColorMatrix(
                    0.213f + (0.787f * amount), 0.715f - (0.715f * amount), 0.072f - (0.072f * amount),
                    0.213f - (0.213f * amount), 0.715f + (0.285f * amount), 0.072f - (0.072f * amount),
                    0.213f - (0.213f * amount), 0.715f - (0.715f * amount), 0.072f + (0.928f * amount),
                    0f);
        }
    }

    /// <summary>
    /// Apply a colour matrix in place, unpremultiplying each pixel and premultiplying the
    /// result back.
    /// </summary>
    /// <remarks>
    /// A fully transparent pixel is skipped rather than transformed: an offset matrix
    /// (<c>contrast</c>, <c>invert</c>) gives transparent black a non-zero colour, which
    /// premultiplying by a zero alpha discards again. Skipping is both the same answer and the
    /// common case, since a group layer is surface-sized and mostly empty.
    /// </remarks>
    private static void ApplyColorMatrix(Pixmap pixmap, in ColorMatrix matrix)
    {
        Span<PremultipliedColor> pixels = pixmap.Pixels.AsSpan();
        for (int index = 0; index < pixels.Length; index++)
        {
            PremultipliedColor pixel = pixels[index];
            if (pixel.A == 0)
            {
                continue;
            }

            float inverse = 1f / pixel.A;
            float r = pixel.R * inverse;
            float g = pixel.G * inverse;
            float b = pixel.B * inverse;

            float outR = (matrix.M00 * r) + (matrix.M01 * g) + (matrix.M02 * b) + matrix.Offset;
            float outG = (matrix.M10 * r) + (matrix.M11 * g) + (matrix.M12 * b) + matrix.Offset;
            float outB = (matrix.M20 * r) + (matrix.M21 * g) + (matrix.M22 * b) + matrix.Offset;

            pixels[index] = new PremultipliedColor(
                PremultiplyClamped(outR, pixel.A),
                PremultiplyClamped(outG, pixel.A),
                PremultiplyClamped(outB, pixel.A),
                pixel.A);
        }
    }

    /// <summary>
    /// Clamp a straight-alpha channel to the unit range and premultiply it, which is what keeps
    /// the surface's <c>colour &lt;= alpha</c> invariant across an out-of-gamut matrix result.
    /// </summary>
    private static byte PremultiplyClamped(float channel, byte alpha) =>
        (byte)F32.Round(Math.Clamp(channel, 0f, 1f) * alpha);

    /// <summary><c>opacity()</c>: scale the whole premultiplied pixel.</summary>
    private static void ScaleAlpha(Pixmap pixmap, float amount)
    {
        float scale = Math.Clamp(amount, 0f, 1f);
        if (scale >= 1f)
        {
            return;
        }

        Span<PremultipliedColor> pixels = pixmap.Pixels.AsSpan();
        for (int index = 0; index < pixels.Length; index++)
        {
            PremultipliedColor pixel = pixels[index];
            if (pixel.A == 0)
            {
                continue;
            }

            pixels[index] = new PremultipliedColor(
                (byte)F32.Round(pixel.R * scale),
                (byte)F32.Round(pixel.G * scale),
                (byte)F32.Round(pixel.B * scale),
                (byte)F32.Round(pixel.A * scale));
        }
    }

    /// <summary>
    /// <c>drop-shadow()</c>: the layer's own alpha, blurred and tinted, composited back
    /// underneath it at the shadow's offset.
    /// </summary>
    /// <remarks>
    /// Compositing is <c>DstOver</c> onto the layer itself, so one scratch pixmap is enough and
    /// the layer is never replaced. That matters for the case this was written for - Curiosity
    /// outlines its avatar with four drop-shadows, and a second surface-sized pixmap per
    /// function would be four more megabyte allocations per paint.
    /// <para>
    /// The shadow is built premultiplied straight away rather than as an alpha mask that is
    /// tinted afterwards. Both give the same answer - the tint is a constant, and a box blur is
    /// linear - but building it premultiplied lets <see cref="BlurPixmap"/> run over it
    /// unchanged.
    /// </para>
    /// <para>
    /// The blur argument is a <em>radius</em>, twice the sigma, which is <c>box-shadow</c>'s
    /// convention and the opposite of <c>blur()</c>, whose argument is sigma itself. Filter
    /// Effects defines <c>drop-shadow()</c> as <c>feDropShadow</c> with
    /// <c>stdDeviation = radius / 2</c>.
    /// </para>
    /// <para>
    /// Offsetting is done at composite time rather than when the shadow is built, because a box
    /// blur commutes with a translation and doing it here costs no extra pass.
    /// </para>
    /// </remarks>
    private static void DrawDropShadowUnder(Pixmap layer, FilterFunction shadow)
    {
        if (shadow.Color.A == 0)
        {
            return;
        }

        Pixmap? scratch = Pixmap.New(layer.Width, layer.Height);
        if (scratch is null)
        {
            return;
        }

        using Pixmap owned = scratch;

        // Premultiplied tint of a fully opaque source pixel; every shadow pixel is this scaled
        // by the source's coverage.
        float alphaScale = shadow.Color.A / 255f;
        Span<PremultipliedColor> shadowPixels = owned.Pixels.AsSpan();
        ReadOnlySpan<PremultipliedColor> sourcePixels = layer.Pixels.AsSpan();
        for (int index = 0; index < sourcePixels.Length; index++)
        {
            byte coverage = sourcePixels[index].A;
            if (coverage == 0)
            {
                continue;
            }

            float alpha = coverage * alphaScale;
            shadowPixels[index] = new PremultipliedColor(
                (byte)F32.Round(shadow.Color.R * alpha / 255f),
                (byte)F32.Round(shadow.Color.G * alpha / 255f),
                (byte)F32.Round(shadow.Color.B * alpha / 255f),
                (byte)F32.Round(alpha));
        }

        if (shadow.ShadowBlur > 0f)
        {
            BlurPixmap(owned, shadow.ShadowBlur * 0.5f, BlurEdge.Transparent);
        }

        Surface.DrawPixmap(
            layer,
            0,
            0,
            owned,
            1f,
            false,
            Affine2.Translate(shadow.OffsetX, shadow.OffsetY),
            null,
            SKBlendMode.DstOver);
    }
}
