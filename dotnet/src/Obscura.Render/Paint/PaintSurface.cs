// Low-level surface operations standing in for tiny-skia's `Pixmap::fill_path`,
// `Pixmap::stroke_path`, and `Pixmap::draw_pixmap`, implemented on Skia.
using SkiaSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal static class PaintColor
{
    internal static SKColor ToSk(RgbaColor color) => new(color.R, color.G, color.B, color.A);

    /// <summary>paint.rs <c>premultiplied</c>.</summary>
    internal static PremultipliedColor Premultiplied(RgbaColor color)
    {
        uint alpha = color.A;
        return PremultipliedColor.FromRgba(
            (byte)(color.R * alpha / 255),
            (byte)(color.G * alpha / 255),
            (byte)(color.B * alpha / 255),
            color.A);
    }
}

/// <summary>Rasterization primitives shared by every paint path.</summary>
internal static class Surface
{
    internal static SKMatrix ToMatrix(Affine2 transform) =>
        new(transform.A, transform.C, transform.E, transform.B, transform.D, transform.F, 0f, 0f, 1f);

    internal static Affine2 RasterTransform(float scale) => Affine2.Scale(scale, scale);

    /// <summary>Run <paramref name="draw"/> with an optional full-surface coverage mask.</summary>
    internal static void Draw(Pixmap pixmap, Mask? mask, Affine2 transform, Action<SKCanvas> draw)
    {
        if (pixmap.Width == 0 || pixmap.Height == 0)
        {
            return;
        }

        SKCanvas canvas;
        try
        {
            canvas = pixmap.Canvas;
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            if (mask is null || mask.Width != pixmap.Width || mask.Height != pixmap.Height)
            {
                int state = canvas.Save();
                canvas.SetMatrix(ToMatrix(transform));
                draw(canvas);
                canvas.RestoreToCount(state);
                return;
            }

            using SKImage? maskImage = mask.ToImage();
            if (maskImage is null)
            {
                return;
            }

            int outer = canvas.SaveLayer();
            canvas.SetMatrix(ToMatrix(transform));
            draw(canvas);
            canvas.ResetMatrix();
            using SKPaint blend = new() { BlendMode = SKBlendMode.DstIn };
            canvas.DrawImage(maskImage, 0f, 0f, SKSamplingOptions.Default, blend);
            canvas.RestoreToCount(outer);
        }
        catch (Exception)
        {
            // Painting must never throw on malformed input; degrade to not painting.
        }
    }

    internal static void FillPath(
        Pixmap pixmap,
        SKPath path,
        SKPaint paint,
        bool evenOdd,
        Affine2 transform,
        Mask? mask)
    {
        using SKPath copy = new(path)
        {
            FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding,
        };
        SKPath ruled = copy;
        Draw(pixmap, mask, transform, canvas => canvas.DrawPath(ruled, paint));
    }

    internal static void FillPath(
        Pixmap pixmap,
        SKPath path,
        RgbaColor color,
        bool antiAlias,
        Affine2 transform,
        Mask? mask)
    {
        if (color.A == 0)
        {
            return;
        }

        using SKPaint paint = new()
        {
            Color = PaintColor.ToSk(color),
            IsAntialias = antiAlias,
            Style = SKPaintStyle.Fill,
        };
        FillPath(pixmap, path, paint, evenOdd: false, transform, mask);
    }

    internal static void StrokePath(
        Pixmap pixmap,
        SKPath path,
        SKPaint paint,
        Affine2 transform,
        Mask? mask)
    {
        Draw(pixmap, mask, transform, canvas => canvas.DrawPath(path, paint));
    }

    /// <summary>tiny-skia's <c>Pixmap::draw_pixmap</c>.</summary>
    internal static void DrawPixmap(
        Pixmap pixmap,
        int x,
        int y,
        Pixmap source,
        float opacity,
        bool bilinear,
        Affine2 transform,
        Mask? mask,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        if (opacity <= 0f || source.Width == 0 || source.Height == 0)
        {
            return;
        }

        SKImage? image = null;
        try
        {
            image = source.Snapshot();
        }
        catch (Exception)
        {
            return;
        }

        using SKImage owned = image;
        using SKPaint paint = new()
        {
            IsAntialias = false,
            Color = new SKColor(0, 0, 0, (byte)Math.Clamp((int)MathF.Round(opacity * 255f), 0, 255)),
            BlendMode = blend,
        };
        SKSamplingOptions sampling = bilinear
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        Draw(pixmap, mask, transform, canvas => canvas.DrawImage(owned, x, y, sampling, paint));
    }
}
