using SkiaSharp;

namespace Obscura.Browser;

/// <summary>
/// The 8-bit RGB raster the PDF exporter passes between decode and JPEG encode,
/// standing in for the <c>image</c> crate's <c>RgbImage</c>.
/// </summary>
internal sealed class RgbImage
{
    private RgbImage(uint width, uint height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    internal uint Width { get; }

    internal uint Height { get; }

    /// <summary>Row-major RGB triples, three bytes per pixel.</summary>
    internal byte[] Pixels { get; }

    internal (byte R, byte G, byte B) GetPixel(uint x, uint y)
    {
        int offset = (int)((y * Width) + x) * 3;
        return (Pixels[offset], Pixels[offset + 1], Pixels[offset + 2]);
    }

    internal static RgbImage FromPixel(uint width, uint height, (byte R, byte G, byte B) color)
    {
        byte[] pixels = new byte[(int)(width * height * 3)];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = color.R;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.B;
        }
        return new RgbImage(width, height, pixels);
    }

    internal static RgbImage FromFunction(
        uint width,
        uint height,
        Func<uint, uint, (byte R, byte G, byte B)> source)
    {
        byte[] pixels = new byte[(int)(width * height * 3)];
        int offset = 0;
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = source(x, y);
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                offset += 3;
            }
        }
        return new RgbImage(width, height, pixels);
    }

    /// <summary>Decode encoded bytes (PNG or JPEG) into straight RGB.</summary>
    internal static RgbImage? Decode(ReadOnlySpan<byte> encoded)
    {
        using SKBitmap? bitmap = SKBitmap.Decode(encoded.ToArray());
        if (bitmap is null)
        {
            return null;
        }
        uint width = (uint)bitmap.Width;
        uint height = (uint)bitmap.Height;
        byte[] pixels = new byte[(int)(width * height * 3)];
        var info = new SKImageInfo((int)width, (int)height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        byte[] rgba = new byte[(int)(width * height * 4)];
        using SKImage image = SKImage.FromBitmap(bitmap);
        unsafe
        {
            fixed (byte* destination = rgba)
            {
                if (!image.ReadPixels(info, (nint)destination, (int)width * 4, 0, 0))
                {
                    return null;
                }
            }
        }
        for (int i = 0, j = 0; i < pixels.Length; i += 3, j += 4)
        {
            pixels[i] = rgba[j];
            pixels[i + 1] = rgba[j + 1];
            pixels[i + 2] = rgba[j + 2];
        }
        return new RgbImage(width, height, pixels);
    }

    /// <summary>Encode as a baseline JPEG at the given quality.</summary>
    internal byte[]? EncodeJpeg(int quality)
    {
        var info = new SKImageInfo((int)Width, (int)Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        byte[] rgba = new byte[(int)(Width * Height * 4)];
        for (int i = 0, j = 0; i < Pixels.Length; i += 3, j += 4)
        {
            rgba[j] = Pixels[i];
            rgba[j + 1] = Pixels[i + 1];
            rgba[j + 2] = Pixels[i + 2];
            rgba[j + 3] = 255;
        }
        unsafe
        {
            fixed (byte* pointer = rgba)
            {
                using SKImage image = SKImage.FromPixelCopy(info, (nint)pointer, (int)Width * 4);
                using SKData? data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                return data?.ToArray();
            }
        }
    }
}
