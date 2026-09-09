using System.Buffers.Binary;
using System.IO.Compression;

namespace Obscura.Render;

/// <summary>
/// WOFF1 and WOFF2 to bare-SFNT decoding.
/// </summary>
/// <remarks>
/// The Rust engine calls the <c>wuff</c> crate for this; .NET has no managed equivalent, and
/// the dependency set is closed, so the decoder lives here. Only the pieces real webfonts use
/// are implemented: zlib table decompression for WOFF1, and Brotli plus the <c>glyf</c>/
/// <c>loca</c> transform for WOFF2. A resource this cannot decode is passed through unchanged,
/// which is what a font loader should do with bytes it does not recognize.
/// </remarks>
internal static class Woff
{
    private const uint Woff1Signature = 0x774F4646; // 'wOFF'
    private const uint Woff2Signature = 0x774F4632; // 'wOF2'

    private static readonly string[] KnownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm",
        "glyf", "loca", "prep", "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern",
        "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC",
        "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar",
        "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty",
        "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat",
        "Gloc", "Feat", "Sill",
    ];

    /// <summary>Decode a WOFF/WOFF2 resource; false means the bytes are not a WOFF container.</summary>
    public static bool TryDecode(byte[] data, out byte[]? sfnt)
    {
        sfnt = null;
        if (data.Length < 4)
        {
            return false;
        }

        uint signature = BinaryPrimitives.ReadUInt32BigEndian(data);
        try
        {
            sfnt = signature switch
            {
                Woff1Signature => DecodeWoff1(data),
                Woff2Signature => DecodeWoff2(data),
                _ => null,
            };
        }
        catch (Exception)
        {
            // A malformed webfont must not take the page down; fall back to the raw bytes and
            // let the font backend reject them.
            sfnt = null;
        }

        return sfnt is not null;
    }

    private static byte[] DecodeWoff1(byte[] data)
    {
        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12));
        uint flavor = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        var tables = new List<(uint Tag, byte[] Data)>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int entry = 44 + (i * 20);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry));
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 4));
            int compLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 8));
            int origLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 12));
            byte[] payload;
            if (compLength < origLength)
            {
                using var input = new MemoryStream(data, offset, compLength, writable: false);
                using var inflate = new ZLibStream(input, CompressionMode.Decompress);
                var output = new MemoryStream(origLength);
                inflate.CopyTo(output);
                payload = output.ToArray();
            }
            else
            {
                payload = data.AsSpan(offset, origLength).ToArray();
            }

            tables.Add((tag, payload));
        }

        return BuildSfnt(flavor, tables);
    }

    private static byte[] DecodeWoff2(byte[] data)
    {
        uint flavor = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12));
        int totalCompressedSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));

        int cursor = 48;
        var entries = new List<Woff2Entry>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            byte flags = data[cursor++];
            int tagIndex = flags & 0x3f;
            int transform = (flags >> 6) & 0x3;
            uint tag;
            if (tagIndex == 0x3f)
            {
                tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor));
                cursor += 4;
            }
            else
            {
                tag = Tag(KnownTags[tagIndex]);
            }

            uint originalLength = ReadBase128(data, ref cursor);
            uint transformLength = originalLength;
            bool isGlyfOrLoca = tag is 0x676C7966 or 0x6C6F6361;
            bool transformed = isGlyfOrLoca ? transform == 0 : transform != 0;
            if (transformed)
            {
                transformLength = ReadBase128(data, ref cursor);
            }

            entries.Add(new Woff2Entry(tag, transformed, originalLength, transformLength));
        }

        byte[] decompressed;
        using (var input = new MemoryStream(data, cursor, totalCompressedSize, writable: false))
        using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
        {
            var output = new MemoryStream();
            brotli.CopyTo(output);
            decompressed = output.ToArray();
        }

        var tables = new List<(uint Tag, byte[] Data)>(numTables);
        int position = 0;
        byte[]? transformedGlyf = null;
        int glyfIndex = -1;
        int locaIndex = -1;
        foreach (Woff2Entry entry in entries)
        {
            int length = (int)entry.TransformLength;
            byte[] payload = decompressed.AsSpan(position, length).ToArray();
            position += length;
            if (entry.Tag == 0x676C7966 && entry.Transformed)
            {
                transformedGlyf = payload;
                glyfIndex = tables.Count;
                tables.Add((entry.Tag, []));
                continue;
            }

            if (entry.Tag == 0x6C6F6361 && entry.Transformed)
            {
                locaIndex = tables.Count;
                tables.Add((entry.Tag, []));
                continue;
            }

            tables.Add((entry.Tag, payload));
        }

        if (transformedGlyf is not null)
        {
            (byte[] glyf, byte[] loca) = ReconstructGlyf(transformedGlyf);
            tables[glyfIndex] = (0x676C7966, glyf);
            if (locaIndex >= 0)
            {
                tables[locaIndex] = (0x6C6F6361, loca);
            }

            // The reconstructed loca is always the long form, so `head` must say so.
            for (int i = 0; i < tables.Count; i++)
            {
                if (tables[i].Tag == 0x68656164 && tables[i].Data.Length >= 52)
                {
                    byte[] head = tables[i].Data;
                    BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
                    tables[i] = (tables[i].Tag, head);
                }
            }
        }

        return BuildSfnt(flavor, tables);
    }

    private readonly record struct Woff2Entry(uint Tag, bool Transformed, uint OriginalLength, uint TransformLength);

    private static (byte[] Glyf, byte[] Loca) ReconstructGlyf(byte[] source)
    {
        int cursor = 0;
        cursor += 4; // version / reserved
        int numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(cursor));
        cursor += 2;
        cursor += 2; // indexFormat; the output always uses the long form
        int nContourSize = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(cursor));
        int nPointsSize = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(cursor + 4));
        int flagSize = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(cursor + 8));
        int glyphSize = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(cursor + 12));
        int compositeSize = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(cursor + 16));
        int bboxSize = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(cursor + 20));
        int instructionSize = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(cursor + 24));
        cursor += 28;

        var nContour = new Reader(source, cursor, nContourSize);
        cursor += nContourSize;
        var nPoints = new Reader(source, cursor, nPointsSize);
        cursor += nPointsSize;
        var flagStream = new Reader(source, cursor, flagSize);
        cursor += flagSize;
        var glyphStream = new Reader(source, cursor, glyphSize);
        cursor += glyphSize;
        var compositeStream = new Reader(source, cursor, compositeSize);
        cursor += compositeSize;
        int bboxBitmapSize = (4 * ((numGlyphs + 31) / 32));
        var bboxBitmap = new Reader(source, cursor, bboxBitmapSize);
        var bboxStream = new Reader(source, cursor + bboxBitmapSize, bboxSize - bboxBitmapSize);
        cursor += bboxSize;
        var instructionStream = new Reader(source, cursor, instructionSize);

        var glyf = new MemoryStream();
        var loca = new byte[(numGlyphs + 1) * 4];
        for (int glyphIndex = 0; glyphIndex < numGlyphs; glyphIndex++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyphIndex * 4), (uint)glyf.Length);
            short contours = nContour.ReadInt16();
            bool hasBbox = (bboxBitmap.Peek(glyphIndex / 8) & (1 << (7 - (glyphIndex % 8)))) != 0;
            if (contours == 0)
            {
                continue;
            }

            if (contours > 0)
            {
                WriteSimpleGlyph(glyf, contours, hasBbox, nPoints, flagStream, glyphStream, instructionStream, bboxStream);
            }
            else
            {
                WriteCompositeGlyph(glyf, compositeStream, glyphStream, instructionStream, bboxStream);
            }

            while (glyf.Length % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(numGlyphs * 4), (uint)glyf.Length);
        return (glyf.ToArray(), loca);
    }

    private static void WriteSimpleGlyph(
        MemoryStream glyf,
        short contours,
        bool hasBbox,
        Reader nPoints,
        Reader flagStream,
        Reader glyphStream,
        Reader instructionStream,
        Reader bboxStream)
    {
        var endPoints = new int[contours];
        int total = 0;
        for (int i = 0; i < contours; i++)
        {
            total += nPoints.Read255UInt16();
            endPoints[i] = total - 1;
        }

        var xs = new int[total];
        var ys = new int[total];
        var onCurve = new bool[total];
        int x = 0;
        int y = 0;
        for (int i = 0; i < total; i++)
        {
            byte flag = flagStream.ReadByte();
            onCurve[i] = (flag & 0x80) == 0;
            (int dx, int dy) = DecodeTriplet((byte)(flag & 0x7f), glyphStream);
            x += dx;
            y += dy;
            xs[i] = x;
            ys[i] = y;
        }

        int instructionLength = glyphStream.Read255UInt16();
        byte[] instructions = instructionStream.ReadBytes(instructionLength);

        short xMin;
        short yMin;
        short xMax;
        short yMax;
        if (hasBbox)
        {
            xMin = bboxStream.ReadInt16();
            yMin = bboxStream.ReadInt16();
            xMax = bboxStream.ReadInt16();
            yMax = bboxStream.ReadInt16();
        }
        else
        {
            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;
            for (int i = 0; i < total; i++)
            {
                minX = Math.Min(minX, xs[i]);
                minY = Math.Min(minY, ys[i]);
                maxX = Math.Max(maxX, xs[i]);
                maxY = Math.Max(maxY, ys[i]);
            }

            xMin = (short)(total == 0 ? 0 : minX);
            yMin = (short)(total == 0 ? 0 : minY);
            xMax = (short)(total == 0 ? 0 : maxX);
            yMax = (short)(total == 0 ? 0 : maxY);
        }

        WriteInt16(glyf, contours);
        WriteInt16(glyf, xMin);
        WriteInt16(glyf, yMin);
        WriteInt16(glyf, xMax);
        WriteInt16(glyf, yMax);
        foreach (int end in endPoints)
        {
            WriteUInt16(glyf, (ushort)end);
        }

        WriteUInt16(glyf, (ushort)instructions.Length);
        glyf.Write(instructions, 0, instructions.Length);

        // Emit the plainest legal encoding: one flag byte per point, no repeats, and both
        // coordinate deltas as signed 16-bit values.
        for (int i = 0; i < total; i++)
        {
            glyf.WriteByte((byte)(onCurve[i] ? 0x01 : 0x00));
        }

        int previous = 0;
        for (int i = 0; i < total; i++)
        {
            WriteInt16(glyf, (short)(xs[i] - previous));
            previous = xs[i];
        }

        previous = 0;
        for (int i = 0; i < total; i++)
        {
            WriteInt16(glyf, (short)(ys[i] - previous));
            previous = ys[i];
        }
    }

    private static void WriteCompositeGlyph(
        MemoryStream glyf,
        Reader compositeStream,
        Reader glyphStream,
        Reader instructionStream,
        Reader bboxStream)
    {
        short xMin = bboxStream.ReadInt16();
        short yMin = bboxStream.ReadInt16();
        short xMax = bboxStream.ReadInt16();
        short yMax = bboxStream.ReadInt16();
        WriteInt16(glyf, -1);
        WriteInt16(glyf, xMin);
        WriteInt16(glyf, yMin);
        WriteInt16(glyf, xMax);
        WriteInt16(glyf, yMax);

        bool haveInstructions = false;
        bool more = true;
        while (more)
        {
            ushort flags = compositeStream.ReadUInt16();
            ushort glyphIndex = compositeStream.ReadUInt16();
            WriteUInt16(glyf, flags);
            WriteUInt16(glyf, glyphIndex);
            more = (flags & 0x0020) != 0;
            haveInstructions |= (flags & 0x0100) != 0;
            int argumentBytes = (flags & 0x0001) != 0 ? 4 : 2;
            byte[] arguments = compositeStream.ReadBytes(argumentBytes);
            glyf.Write(arguments, 0, arguments.Length);
            int transformBytes = 0;
            if ((flags & 0x0008) != 0)
            {
                transformBytes = 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                transformBytes = 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                transformBytes = 8;
            }

            if (transformBytes > 0)
            {
                byte[] transform = compositeStream.ReadBytes(transformBytes);
                glyf.Write(transform, 0, transform.Length);
            }
        }

        if (haveInstructions)
        {
            int length = glyphStream.Read255UInt16();
            byte[] instructions = instructionStream.ReadBytes(length);
            WriteUInt16(glyf, (ushort)length);
            glyf.Write(instructions, 0, instructions.Length);
        }
    }

    private static (int Dx, int Dy) DecodeTriplet(byte flag, Reader stream)
    {
        static int Signed(int flag, int value) => (flag & 1) != 0 ? value : -value;

        if (flag < 10)
        {
            int b0 = stream.ReadByte();
            return (0, Signed(flag, ((flag & 14) << 7) + b0));
        }

        if (flag < 20)
        {
            int b0 = stream.ReadByte();
            return (Signed(flag, (((flag - 10) & 14) << 7) + b0), 0);
        }

        if (flag < 84)
        {
            int b0 = stream.ReadByte();
            int b = flag - 20;
            return (
                Signed(flag, 1 + (b & 0x30) + (b0 >> 4)),
                Signed(flag >> 1, 1 + ((b & 0x0c) << 2) + (b0 & 0x0f)));
        }

        if (flag < 120)
        {
            int b0 = stream.ReadByte();
            int b1 = stream.ReadByte();
            int b = flag - 84;
            return (
                Signed(flag, 1 + ((b / 12) << 8) + b0),
                Signed(flag >> 1, 1 + (((b % 12) >> 2) << 8) + b1));
        }

        if (flag < 124)
        {
            int b0 = stream.ReadByte();
            int b1 = stream.ReadByte();
            int b2 = stream.ReadByte();
            return (
                Signed(flag, (b0 << 4) + (b1 >> 4)),
                Signed(flag >> 1, ((b1 & 0x0f) << 8) + b2));
        }

        {
            int b0 = stream.ReadByte();
            int b1 = stream.ReadByte();
            int b2 = stream.ReadByte();
            int b3 = stream.ReadByte();
            return (Signed(flag, (b0 << 8) + b1), Signed(flag >> 1, (b2 << 8) + b3));
        }
    }

    private static byte[] BuildSfnt(uint flavor, List<(uint Tag, byte[] Data)> tables)
    {
        tables.Sort(static (a, b) => a.Tag.CompareTo(b.Tag));
        int count = tables.Count;
        int headerSize = 12 + (count * 16);
        int offset = headerSize;
        var output = new MemoryStream();
        var header = new byte[headerSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), flavor);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)count);
        int entrySelector = 0;
        while (1 << (entrySelector + 1) <= count)
        {
            entrySelector++;
        }

        int searchRange = (1 << entrySelector) * 16;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)((count * 16) - searchRange));
        for (int i = 0; i < count; i++)
        {
            int entry = 12 + (i * 16);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry), tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 4), Checksum(tables[i].Data));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(entry + 12), (uint)tables[i].Data.Length);
            offset += (tables[i].Data.Length + 3) & ~3;
        }

        output.Write(header, 0, header.Length);
        foreach ((uint _, byte[] data) in tables)
        {
            output.Write(data, 0, data.Length);
            int padding = ((data.Length + 3) & ~3) - data.Length;
            for (int i = 0; i < padding; i++)
            {
                output.WriteByte(0);
            }
        }

        return output.ToArray();
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        int whole = data.Length & ~3;
        for (int i = 0; i < whole; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
        }

        if (whole < data.Length)
        {
            uint tail = 0;
            for (int i = whole; i < data.Length; i++)
            {
                tail |= (uint)data[i] << (24 - (8 * (i - whole)));
            }

            sum += tail;
        }

        return sum;
    }

    private static uint ReadBase128(byte[] data, ref int cursor)
    {
        uint value = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = data[cursor++];
            value = (value << 7) | (uint)(b & 0x7f);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("malformed UIntBase128");
    }

    private static uint Tag(string tag) =>
        ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

    private static void WriteInt16(MemoryStream stream, short value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt16(MemoryStream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        private readonly int _start;
        private readonly int _end;
        private int _position;

        public Reader(byte[] data, int start, int length)
        {
            _data = data;
            _start = start;
            _end = start + length;
            _position = start;
        }

        public byte Peek(int offset) => _data[_start + offset];

        public byte ReadByte() => _position < _end ? _data[_position++] : (byte)0;

        public short ReadInt16()
        {
            short value = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(_position));
            _position += 2;
            return value;
        }

        public ushort ReadUInt16()
        {
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_position));
            _position += 2;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            byte[] result = _data.AsSpan(_position, count).ToArray();
            _position += count;
            return result;
        }

        public int Read255UInt16()
        {
            byte code = ReadByte();
            return code switch
            {
                253 => ReadUInt16(),
                254 => ReadByte() + (253 * 2),
                255 => ReadByte() + 253,
                _ => code,
            };
        }
    }
}
