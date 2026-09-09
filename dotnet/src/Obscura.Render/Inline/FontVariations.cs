namespace Obscura.Render;

/// <summary>A four-byte OpenType variation axis tag.</summary>
/// <remarks>
/// Port of the vendored cosmic-text <c>VariationTag</c>. Ordering is by the four bytes
/// big-endian, which is what keeps <see cref="FontVariations"/> in a canonical order.
/// </remarks>
public readonly struct VariationTag : IEquatable<VariationTag>, IComparable<VariationTag>
{
    private readonly uint _bits;

    public VariationTag(ReadOnlySpan<byte> tag)
    {
        if (tag.Length != 4)
        {
            throw new ArgumentException("a variation tag is exactly four bytes", nameof(tag));
        }

        _bits = ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];
    }

    private VariationTag(uint bits) => _bits = bits;

    /// <summary>Build from the four ASCII characters of a CSS axis name.</summary>
    public static VariationTag FromAscii(string tag)
    {
        if (tag.Length != 4)
        {
            throw new ArgumentException("a variation tag is exactly four characters", nameof(tag));
        }

        return new VariationTag(
            ((uint)(byte)tag[0] << 24) | ((uint)(byte)tag[1] << 16) | ((uint)(byte)tag[2] << 8) | (byte)tag[3]);
    }

    /// <summary>The tag's raw big-endian representation, as HarfBuzz and Skia want it.</summary>
    public uint Raw => _bits;

    public static VariationTag FromRaw(uint raw) => new(raw);

    public byte[] ToBytes() => [(byte)(_bits >> 24), (byte)(_bits >> 16), (byte)(_bits >> 8), (byte)_bits];

    public bool Equals(VariationTag other) => _bits == other._bits;

    public override bool Equals(object? obj) => obj is VariationTag other && Equals(other);

    public override int GetHashCode() => (int)_bits;

    public int CompareTo(VariationTag other) => _bits.CompareTo(other._bits);

    public override string ToString() =>
        new([(char)(byte)(_bits >> 24), (char)(byte)(_bits >> 16), (char)(byte)(_bits >> 8), (char)(byte)_bits]);

    public static bool operator ==(VariationTag left, VariationTag right) => left.Equals(right);

    public static bool operator !=(VariationTag left, VariationTag right) => !left.Equals(right);
}

/// <summary>A variation coordinate with stable equality and hashing for shape caches.</summary>
/// <remarks>
/// NaN compares equal to NaN and hashes to one canonical bit pattern; -0.0 canonicalizes to
/// +0.0. Both are required so a cached raster entry can never be missed (or duplicated) by a
/// coordinate that is numerically the same.
/// </remarks>
public readonly struct VariationValue(float value) : IEquatable<VariationValue>
{
    private const uint CanonicalNanBits = 0x7fc0_0000;

    public float Value { get; } = value;

    public bool Equals(VariationValue other) =>
        float.IsNaN(Value) ? float.IsNaN(other.Value) : Value.Equals(other.Value);

    public override bool Equals(object? obj) => obj is VariationValue other && Equals(other);

    public override int GetHashCode()
    {
        uint bits = float.IsNaN(Value)
            ? CanonicalNanBits
            : BitConverter.SingleToUInt32Bits(Value + 0f);
        return (int)bits;
    }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator ==(VariationValue left, VariationValue right) => left.Equals(right);

    public static bool operator !=(VariationValue left, VariationValue right) => !left.Equals(right);
}

/// <summary>One OpenType variation axis coordinate.</summary>
public readonly record struct FontVariation(VariationTag Tag, VariationValue Value);

/// <summary>
/// Variation coordinates applied while shaping and rasterizing a text span.
/// </summary>
/// <remarks>
/// Coordinates are stored in tag order with duplicate tags collapsed (last value wins),
/// matching CSS <c>font-variation-settings</c>. Canonical ordering is what lets shaping and
/// rasterization agree on a cache identity for the same axis tuple.
/// </remarks>
public sealed class FontVariations : IEquatable<FontVariations>
{
    private readonly List<FontVariation> _variations = [];

    public FontVariations()
    {
    }

    private FontVariations(List<FontVariation> variations) => _variations = variations;

    public static readonly FontVariations Empty = new();

    /// <summary>Set an axis coordinate; setting the same tag again replaces its value.</summary>
    public FontVariations Set(VariationTag tag, float value)
    {
        int lo = 0;
        int hi = _variations.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            int cmp = _variations[mid].Tag.CompareTo(tag);
            if (cmp == 0)
            {
                _variations[mid] = new FontVariation(tag, new VariationValue(value));
                return this;
            }

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        _variations.Insert(lo, new FontVariation(tag, new VariationValue(value)));
        return this;
    }

    public bool IsEmpty => _variations.Count == 0;

    public int Count => _variations.Count;

    public IReadOnlyList<FontVariation> Items => _variations;

    public float? Find(VariationTag tag)
    {
        foreach (FontVariation variation in _variations)
        {
            if (variation.Tag == tag)
            {
                return variation.Value.Value;
            }
        }

        return null;
    }

    public FontVariations Clone() => new([.. _variations]);

    public bool Equals(FontVariations? other)
    {
        if (other is null || other._variations.Count != _variations.Count)
        {
            return false;
        }

        for (int i = 0; i < _variations.Count; i++)
        {
            if (_variations[i] != other._variations[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as FontVariations);

    public override int GetHashCode()
    {
        HashCode hash = default;
        foreach (FontVariation variation in _variations)
        {
            hash.Add(variation.Tag);
            hash.Add(variation.Value);
        }

        return hash.ToHashCode();
    }
}
