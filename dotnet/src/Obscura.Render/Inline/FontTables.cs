using System.Buffers.Binary;
using SkiaSharp;
using HbFace = HarfBuzzSharp.Face;

namespace Obscura.Render;

/// <summary>
/// The few OpenType tables the inline layer reads directly.
/// </summary>
/// <remarks>
/// The Rust engine reads these through <c>ttf-parser</c>. Skia exposes the raw table bytes, so
/// the port parses the same fields rather than trusting Skia's own (differently derived)
/// metric accessors. In particular <c>hhea</c> versus OS/2 typographic metrics must follow
/// ttf-parser's rule, or <c>line-height: normal</c> silently changes.
/// </remarks>
internal static class FontTables
{
    private const uint HheaTag = 0x68686561; // 'hhea'
    private const uint HeadTag = 0x68656164; // 'head'
    private const uint Os2Tag = 0x4F532F32;  // 'OS/2'
    private const uint FvarTag = 0x66766172; // 'fvar'

    /// <summary>fsSelection bit 7: prefer the OS/2 typographic metrics over hhea.</summary>
    private const ushort UseTypoMetrics = 1 << 7;

    /// <summary>
    /// Build a HarfBuzz face over a private copy of the font bytes.
    /// </summary>
    /// <remarks>
    /// The copy is not optional: HarfBuzz keeps the blob for the life of the face, and handing
    /// it managed memory makes shaping depend on when the GC runs. That shows up as
    /// intermittently different glyph selection, not as a crash.
    /// </remarks>
    public static HbFace CreateHarfBuzzFace(byte[] data, int index)
    {
        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(data, 0, buffer, data.Length);
            using var blob = new HarfBuzzSharp.Blob(
                buffer,
                data.Length,
                HarfBuzzSharp.MemoryMode.Duplicate);
            blob.MakeImmutable();
            return new HbFace(blob, index);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Face ascent, descent, line gap, and units-per-em, matching <c>ttf_parser::Face</c>.
    /// </summary>
    public static FaceMetrics ReadFaceMetrics(SKTypeface typeface)
    {
        float unitsPerEm = typeface.UnitsPerEm > 0 ? typeface.UnitsPerEm : 1000f;
        byte[]? head = TryGetTable(typeface, HeadTag);
        if (head is { Length: >= 20 })
        {
            ushort upem = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18));
            if (upem > 0)
            {
                unitsPerEm = upem;
            }
        }

        short ascender = 0;
        short descender = 0;
        short lineGap = 0;
        byte[]? hhea = TryGetTable(typeface, HheaTag);
        if (hhea is { Length: >= 10 })
        {
            ascender = BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(4));
            descender = BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(6));
            lineGap = BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(8));
        }

        byte[]? os2 = TryGetTable(typeface, Os2Tag);
        if (os2 is { Length: >= 78 })
        {
            ushort version = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(0));
            ushort fsSelection = BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(62));
            if (version >= 4 && (fsSelection & UseTypoMetrics) != 0)
            {
                ascender = BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(68));
                descender = BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(70));
                lineGap = BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(72));
            }
        }

        return new FaceMetrics(
            Math.Max(ascender, (short)0),
            -Math.Min(descender, (short)0),
            Math.Max(lineGap, (short)0),
            unitsPerEm);
    }

    /// <summary>The face's variation axes, straight from <c>fvar</c>.</summary>
    public static IReadOnlyList<FontAxis> ReadAxes(SKTypeface typeface)
    {
        byte[]? fvar = TryGetTable(typeface, FvarTag);
        if (fvar is null || fvar.Length < 16)
        {
            return [];
        }

        ushort axesArrayOffset = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(4));
        ushort axisCount = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(8));
        ushort axisSize = BinaryPrimitives.ReadUInt16BigEndian(fvar.AsSpan(10));
        if (axisSize < 20)
        {
            return [];
        }

        List<FontAxis> axes = new(axisCount);
        for (int i = 0; i < axisCount; i++)
        {
            int offset = axesArrayOffset + (i * axisSize);
            if (offset + 20 > fvar.Length)
            {
                break;
            }

            uint tag = BinaryPrimitives.ReadUInt32BigEndian(fvar.AsSpan(offset));
            float min = ReadFixed(fvar, offset + 4);
            float @default = ReadFixed(fvar, offset + 8);
            float max = ReadFixed(fvar, offset + 12);
            axes.Add(new FontAxis(VariationTag.FromRaw(tag), min, @default, max));
        }

        return axes;
    }

    /// <summary>Skia throws for a table the face does not have; absence is not an error here.</summary>
    private static byte[]? TryGetTable(SKTypeface typeface, uint tag)
    {
        foreach (uint present in typeface.GetTableTags())
        {
            if (present == tag)
            {
                try
                {
                    return typeface.GetTableData(tag);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static float ReadFixed(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset)) / 65536f;
}
