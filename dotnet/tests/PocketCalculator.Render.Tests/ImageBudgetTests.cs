using System.Buffers.Binary;
using System.IO.Compression;
using PocketCalculator.Render.Layout;
using SkiaSharp;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// H7: raster decode is checked against a pixel budget before any pixel is allocated, an
/// image rasterizes only the part that lands on the surface, and no pixmap can be sized
/// far past the capture budget.
/// </summary>
public class ImageBudgetTests
{
    [Fact]
    public void HugeIntrinsicPngIsRefusedBeforeDecoding()
    {
        // 10000x10000 1-bit greyscale: about 120 KB on the wire, 400 MB decoded.
        byte[] png = OneBitPng(10_000, 10_000);
        Assert.True(png.Length < 1024 * 1024);

        Assert.Null(PaintResources.RasterToPixmap(png, 100, 100));
    }

    [Fact]
    public void ImagesInsideTheBudgetStillDecode()
    {
        byte[] png = OneBitPng(64, 64);
        using Pixmap? decoded = PaintResources.RasterToPixmap(png, 32, 32);
        Assert.NotNull(decoded);
        Assert.Equal(255, decoded!.Pixels[0].A);
    }

    [Fact]
    public void OversizedDestinationRasterizesOnlyTheSurfaceWindow()
    {
        byte[] png = SolidPng(new SKColor(255, 0, 0));
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => png);
        using Pixmap pixmap = Pixmap.New(100, 100)!;
        Rect rect = new() { X = -3000f, Y = -2000f, Width = 8000f, Height = 8000f };

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool painted = PaintImages.PaintImage(
            "https://example.test/red.png",
            null,
            rect,
            rect,
            ObjectFit.Fill,
            default,
            pixmap,
            resources,
            null,
            null,
            default,
            null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(painted);
        PremultipliedColor pixel = pixmap.Pixels[(50 * 100) + 50];
        Assert.Equal((255, 0, 0, 255), (pixel.R, pixel.G, pixel.B, pixel.A));
        Assert.True(allocated < 16 * 1024 * 1024, $"painting allocated {allocated} bytes");
    }

    [Fact]
    public void WindowedRasterMatchesTheSameRegionOfTheFullRaster()
    {
        using var bitmap = new SKBitmap(7, 5, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 36), (byte)(y * 60), (byte)((x + y) * 20)));
            }
        }

        byte[] png = bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        using Pixmap full = PaintResources.RasterToPixmap(png, 60, 40)!;
        using Pixmap window = PaintResources.RasterToPixmap(png, 60, 40, 13, 9, 20, 17)!;
        for (int y = 0; y < 17; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                Assert.Equal(full.Pixels[((y + 9) * 60) + x + 13], window.Pixels[(y * 20) + x]);
            }
        }
    }

    [Fact]
    public void PixmapsFarPastTheCaptureBudgetAreRefused()
    {
        Assert.Null(Pixmap.New(10_000, 10_000));
        Assert.Null(Mask.New(10_000, 10_000));
        using Pixmap? capture = Pixmap.New(4096, 4096);
        Assert.NotNull(capture);
    }

    private static byte[] SolidPng(SKColor color)
    {
        using var bitmap = new SKBitmap(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        return bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>A 1-bit greyscale PNG of zeros, written by hand so no bitmap is allocated.</summary>
    private static byte[] OneBitPng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 1; // bit depth
        header[9] = 0; // greyscale
        WriteChunk(output, "IHDR", header);

        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            byte[] row = new byte[1 + ((width + 7) / 8)];
            for (int y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT", idat.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typed = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }

        data.CopyTo(typed, 4);
        output.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        output.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
