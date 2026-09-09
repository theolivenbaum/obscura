// Port of the `tiny_skia::Pixmap` / `tiny_skia::Mask` surface types used throughout
// `crates/obscura-render/src/paint.rs`, backed by Skia.
//
// RECONCILIATION NOTE: these two types previously lived in `Inline/Pixmap.cs` with a note that
// paint.rs owns them. The paint port has landed and moved them here; there is exactly one
// definition of each. `TextEngine` keeps painting into the same `PremultipliedColor[]`, which is
// also the memory Skia rasterizes into, so glyphs and vector primitives share one surface.
using System.Runtime.InteropServices;
using SkiaSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>One premultiplied RGBA8 pixel.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PremultipliedColor(byte R, byte G, byte B, byte A)
{
    /// <summary>Build from premultiplied components, clamping RGB to the alpha as tiny-skia does.</summary>
    public static PremultipliedColor FromRgba(byte r, byte g, byte b, byte a) =>
        r <= a && g <= a && b <= a ? new PremultipliedColor(r, g, b, a) : new PremultipliedColor(a, a, a, a);

    /// <summary>tiny-skia's <c>PremultipliedColorU8::red()</c> and friends.</summary>
    public byte Red => R;

    public byte Green => G;

    public byte Blue => B;

    public byte Alpha => A;
}

/// <summary>
/// A premultiplied RGBA8 raster target shared by the glyph rasterizer and the Skia canvas.
/// </summary>
/// <remarks>
/// The pixel array is allocated on the pinned object heap so Skia can rasterize straight into
/// it without a copy or a GC handle. The Skia surface is created lazily and released with the
/// pixmap.
/// </remarks>
public sealed class Pixmap : IDisposable
{
    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private bool _disposed;

    public Pixmap(uint width, uint height)
    {
        Width = width;
        Height = height;
        Pixels = GC.AllocateArray<PremultipliedColor>(checked((int)(width * height)), pinned: true);
    }

    /// <summary>tiny-skia's <c>Pixmap::new</c>: <c>None</c> for a zero-sized surface.</summary>
    public static Pixmap? New(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return null;
        }

        long pixels = (long)width * height;
        if (pixels > 512L * 1024 * 1024)
        {
            return null;
        }

        try
        {
            return new Pixmap(width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public uint Width { get; }

    public uint Height { get; }

    public PremultipliedColor[] Pixels { get; }

    /// <summary>tiny-skia's <c>Pixmap::pixels()</c>.</summary>
    public PremultipliedColor[] PixelsArray => Pixels;

    /// <summary>tiny-skia's <c>Pixmap::data()</c>: the raw RGBA bytes.</summary>
    public byte[] Data()
    {
        byte[] bytes = new byte[Pixels.Length * 4];
        MemoryMarshal.AsBytes(Pixels.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    /// <summary>tiny-skia's <c>Pixmap::clone_rect()</c>: a copy of one sub-rectangle.</summary>
    /// <remarks>Returns null when the rectangle is empty or reaches outside the surface.</remarks>
    public Pixmap? CloneRect(int x, int y, uint width, uint height)
    {
        if (width == 0 || height == 0 || x < 0 || y < 0
            || (ulong)x + width > Width || (ulong)y + height > Height)
        {
            return null;
        }

        Pixmap? copy = New(width, height);
        if (copy is null)
        {
            return null;
        }

        for (uint row = 0; row < height; row++)
        {
            int source = (int)(((y + row) * Width) + (uint)x);
            Pixels.AsSpan(source, (int)width).CopyTo(copy.Pixels.AsSpan((int)(row * width), (int)width));
        }

        return copy;
    }

    /// <summary>tiny-skia's <c>Pixmap::pixel()</c>.</summary>
    public PremultipliedColor? Pixel(uint x, uint y) =>
        x < Width && y < Height ? Pixels[(int)((y * Width) + x)] : null;

    /// <summary>tiny-skia's <c>Pixmap::fill()</c>.</summary>
    public void Fill(RgbaColor color)
    {
        PremultipliedColor value = PaintColor.Premultiplied(color);
        Pixels.AsSpan().Fill(value);
    }

    /// <summary>
    /// The stable address of the pixel buffer. The array lives on the pinned object heap, so
    /// its address never moves and no GC handle is needed.
    /// </summary>
    internal nint PixelPointer => Marshal.UnsafeAddrOfPinnedArrayElement(Pixels, 0);

    /// <summary>The Skia canvas writing directly into <see cref="Pixels"/>.</summary>
    internal SKCanvas Canvas
    {
        get
        {
            if (_canvas is not null)
            {
                return _canvas;
            }

            var info = new SKImageInfo((int)Width, (int)Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            _bitmap = new SKBitmap();
            _bitmap.InstallPixels(info, PixelPointer, (int)Width * 4);
            _canvas = new SKCanvas(_bitmap);
            return _canvas;
        }
    }

    /// <summary>A short-lived Skia image view over the current pixels.</summary>
    internal SKImage Snapshot()
    {
        var info = new SKImageInfo((int)Width, (int)Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, PixelPointer, (int)Width * 4);
    }

    /// <summary>tiny-skia's <c>Pixmap::encode_png()</c>.</summary>
    public byte[]? EncodePng()
    {
        try
        {
            using SKImage image = Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Copy raw premultiplied RGBA bytes into a new pixmap (tiny-skia's <c>from_vec</c>).</summary>
    public static Pixmap? FromRgbaBytes(ReadOnlySpan<byte> rgba, uint width, uint height)
    {
        if (width == 0 || height == 0 || rgba.Length != (int)(width * height * 4))
        {
            return null;
        }

        Pixmap pixmap = new(width, height);
        rgba.CopyTo(MemoryMarshal.AsBytes(pixmap.Pixels.AsSpan()));
        return pixmap;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _canvas?.Dispose();
        _canvas = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

/// <summary>An 8-bit coverage mask in the destination surface's pixel grid.</summary>
public sealed class Mask(uint width, uint height)
{
    public uint Width { get; } = width;

    public uint Height { get; } = height;

    public byte[] Data { get; } = new byte[(int)(width * height)];

    /// <summary>tiny-skia's <c>Mask::new</c>: <c>None</c> for a zero-sized mask.</summary>
    public static Mask? New(uint width, uint height) =>
        width == 0 || height == 0 || (long)width * height > 512L * 1024 * 1024
            ? null
            : new Mask(width, height);

    public Mask Clone()
    {
        Mask copy = new(Width, Height);
        Data.AsSpan().CopyTo(copy.Data);
        return copy;
    }

    /// <summary>tiny-skia's <c>Mask::invert()</c>.</summary>
    public void Invert()
    {
        for (int i = 0; i < Data.Length; i++)
        {
            Data[i] = (byte)(255 - Data[i]);
        }
    }

    /// <summary>tiny-skia's <c>Mask::fill_path()</c>: replace coverage with this path's.</summary>
    public void FillPath(SKPath path, bool evenOdd, bool antiAlias)
    {
        Rasterize(path, evenOdd, antiAlias, intersect: false);
    }

    /// <summary>tiny-skia's <c>Mask::intersect_path()</c>.</summary>
    public void IntersectPath(SKPath path, bool evenOdd, bool antiAlias)
    {
        Rasterize(path, evenOdd, antiAlias, intersect: true);
    }

    private void Rasterize(SKPath path, bool evenOdd, bool antiAlias, bool intersect)
    {
        if (Width == 0 || Height == 0)
        {
            return;
        }

        var info = new SKImageInfo((int)Width, (int)Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKBitmap bitmap = new(info);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using SKPaint paint = new()
            {
                Color = SKColors.White,
                IsAntialias = antiAlias,
                Style = SKPaintStyle.Fill,
            };
            using SKPath copy = new(path)
            {
                FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding,
            };
            canvas.DrawPath(copy, paint);
        }

        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
        for (int i = 0; i < Data.Length; i++)
        {
            byte coverage = pixels[(i * 4) + 3];
            Data[i] = intersect ? (byte)((Data[i] * coverage + 127) / 255) : coverage;
        }
    }

    /// <summary>An A8 Skia image over this mask's coverage.</summary>
    internal SKImage? ToImage()
    {
        try
        {
            var info = new SKImageInfo((int)Width, (int)Height, SKColorType.Alpha8, SKAlphaType.Premul);
            return SKImage.FromPixelCopy(info, Data, (int)Width);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
