using System.Buffers.Binary;
using System.IO.Compression;
using SkiaSharp;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// L11: <see cref="RgbImage"/> sizes its buffers in checked 64-bit arithmetic, and refuses a
/// raster over its pixel budget from the header, before decoding it.
/// </summary>
public class RgbImageTests
{
    [Fact]
    public void BufferLengthRefusesSizesThatWouldWrap()
    {
        // 65536 * 16384 * 4 is 2^32, which wrapped to zero in the old uint arithmetic.
        Assert.Throws<OverflowException>(() => RgbImage.BufferLength(65_536, 16_384, 4));
        Assert.Throws<OverflowException>(() => RgbImage.BufferLength(uint.MaxValue, uint.MaxValue, 4));
        Assert.Equal(12, RgbImage.BufferLength(2, 2, 3));
    }

    [Fact]
    public void DecodeRefusesAnOversizedHeaderBeforeDecoding()
    {
        byte[] png = OneBitPng(9_000, 8_000);
        Assert.Null(RgbImage.Decode(png));
    }

    [Fact]
    public void DecodeStillReadsOrdinaryImages()
    {
        using var bitmap = new SKBitmap(3, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(10, 200, 30));
        byte[] png = bitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        RgbImage decoded = RgbImage.Decode(png)!;
        Assert.Equal((3u, 2u), (decoded.Width, decoded.Height));
        Assert.Equal(((byte)10, (byte)200, (byte)30), decoded.GetPixel(2, 1));
    }

    private static byte[] OneBitPng(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 1;
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
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typed = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }

        data.CopyTo(typed, 4);
        output.Write(typed);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typed)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        byte[] checksum = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        output.Write(checksum);
    }
}
