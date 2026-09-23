using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// H6: WOFF and WOFF2 decoding is bounded by the declared table lengths and by
/// <see cref="Woff.MaxSfntSize"/>, so a small webfont cannot inflate into gigabytes.
/// </summary>
public class WoffBudgetTests
{
    private const int BombSize = 64 * 1024 * 1024;

    [Fact]
    public void Woff1TableInflatingPastOrigLengthIsRejectedWithoutInflatingIt()
    {
        byte[] font = Woff1([(Tag("name"), Zlib(new byte[BombSize]), 1024 * 1024)]);
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.False(Woff.TryDecode(font, out _));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8 * 1024 * 1024, $"decode allocated {allocated} bytes");
    }

    [Fact]
    public void Woff1DeclaredLengthsOverTheCapAreRejectedBeforeAllocating()
    {
        byte[] font = Woff1([(Tag("name"), Zlib(new byte[16]), Woff.MaxSfntSize + 1)]);
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.False(Woff.TryDecode(font, out _));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8 * 1024 * 1024, $"decode allocated {allocated} bytes");
    }

    [Fact]
    public void Woff2BrotliStreamLongerThanItsTablesIsRejectedWithoutInflatingIt()
    {
        byte[] font = Woff2Single(tagIndex: 5 /* name */, originalLength: 100, Brotli(new byte[BombSize]));
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.False(Woff.TryDecode(font, out _));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8 * 1024 * 1024, $"decode allocated {allocated} bytes");
    }

    [Fact]
    public void Woff1RoundTripOfARealFontStillDecodes()
    {
        byte[] ttf = FontAssets.Load("liberation-sans");
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(4));
        var tables = new List<(uint Tag, byte[] Payload, int OrigLength)>();
        for (int i = 0; i < numTables; i++)
        {
            int entry = 12 + (i * 16);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(entry));
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(entry + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(entry + 12));
            byte[] raw = ttf.AsSpan(offset, length).ToArray();
            byte[] packed = Zlib(raw);
            tables.Add((tag, packed.Length < raw.Length ? packed : raw, length));
        }

        Assert.True(Woff.TryDecode(Woff1(tables), out byte[]? sfnt));
        using var database = new FontDatabase();
        Assert.NotEmpty(database.LoadFontSource(Woff1(tables)));
        Assert.NotNull(sfnt);
    }

    [Fact]
    public void RefusedWoffIsAFailedLoadNotRawBytesForTheBackend()
    {
        byte[] font = Woff1([(Tag("name"), Zlib(new byte[BombSize]), 1024 * 1024)]);
        using var database = new FontDatabase();
        Assert.Empty(database.LoadFontSource(font));
    }

    private static uint Tag(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    private static byte[] Zlib(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }

    private static byte[] Brotli(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(raw);
        }

        return output.ToArray();
    }

    private static byte[] Woff1(List<(uint Tag, byte[] Payload, int OrigLength)> tables)
    {
        int directory = 44 + (tables.Count * 20);
        int length = directory;
        foreach ((_, byte[] payload, _) in tables)
        {
            length += (payload.Length + 3) & ~3;
        }

        byte[] data = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x774F4646);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)length);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), (ushort)tables.Count);
        int offset = directory;
        for (int i = 0; i < tables.Count; i++)
        {
            int entry = 44 + (i * 20);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry), tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 8), (uint)tables[i].Payload.Length);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 12), (uint)tables[i].OrigLength);
            tables[i].Payload.CopyTo(data, offset);
            offset += (tables[i].Payload.Length + 3) & ~3;
        }

        return data;
    }

    private static byte[] Woff2Single(int tagIndex, byte originalLength, byte[] compressed)
    {
        byte[] data = new byte[48 + 2 + compressed.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x774F4632);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)compressed.Length);
        data[48] = (byte)tagIndex;
        data[49] = originalLength;
        compressed.CopyTo(data, 50);
        return data;
    }
}
