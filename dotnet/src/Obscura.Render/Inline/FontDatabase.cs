using System.Diagnostics.CodeAnalysis;
using SkiaSharp;
using HbFace = HarfBuzzSharp.Face;
using HbFont = HarfBuzzSharp.Font;

namespace Obscura.Render;

/// <summary>Stable identity for one loaded font resource. Stands in for <c>fontdb::ID</c>.</summary>
public readonly record struct FontId(int Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The face style discriminant fontdb exposes.</summary>
public enum FaceStyle
{
    Normal,
    Italic,
    Oblique,
}

/// <summary>
/// Grid-fittable vertical metrics of one face, in font design units.
/// </summary>
/// <remarks>
/// Port of the Rust <c>FaceMetrics</c>. Kept in design units so a used pixel size can be
/// applied and each component rounded independently, the way Chromium's FreeType path does.
/// </remarks>
public readonly record struct FaceMetrics(float Ascent, float Descent, float LineGap, float UnitsPerEm);

/// <summary>One OpenType variation axis and the range it accepts.</summary>
public readonly record struct FontAxis(VariationTag Tag, float Min, float Default, float Max);

/// <summary>
/// One loaded font resource: its bytes, its identity, and the Skia/HarfBuzz handles derived
/// from them.
/// </summary>
/// <remarks>
/// Variation instances are cached per axis tuple, so repeated shaping and rasterization of the
/// same coordinates never rebuilds a face.
/// </remarks>
public sealed class FaceRecord : IDisposable
{
    private readonly Dictionary<FontVariations, SKTypeface> _typefaceInstances = [];
    private readonly Dictionary<FontVariations, HbFont> _hbFontInstances = [];
    private HbFace? _hbFace;
    private SKFont? _coverageFont;
    private HbFont? _hbFont;
    private bool _disposed;

    internal FaceRecord(FontId id, byte[] data, int index, SKTypeface typeface)
    {
        Id = id;
        Data = data;
        Index = index;
        Typeface = typeface;
        FamilyName = typeface.FamilyName ?? FontAssets.SansFamily;
        Weight = (ushort)Math.Clamp(typeface.FontWeight, 1, 1000);
        Style = typeface.FontSlant switch
        {
            SKFontStyleSlant.Italic => FaceStyle.Italic,
            SKFontStyleSlant.Oblique => FaceStyle.Oblique,
            _ => FaceStyle.Normal,
        };
        Metrics = FontTables.ReadFaceMetrics(typeface);
        Axes = FontTables.ReadAxes(typeface);
        IsVariable = Axes.Count > 0;
    }

    public FontId Id { get; }

    public byte[] Data { get; }

    public int Index { get; }

    public SKTypeface Typeface { get; }

    public string FamilyName { get; }

    public ushort Weight { get; }

    public FaceStyle Style { get; }

    public FaceMetrics Metrics { get; }

    public IReadOnlyList<FontAxis> Axes { get; }

    public bool IsVariable { get; }

    /// <summary>The HarfBuzz face, built lazily from the same bytes Skia parsed.</summary>
    public HbFace HarfBuzzFace
    {
        get
        {
            _hbFace ??= FontTables.CreateHarfBuzzFace(Data, Index);

            return _hbFace;
        }
    }

    /// <summary>The default (non-varied) HarfBuzz font, scaled to the design em.</summary>
    public HbFont HarfBuzzFont
    {
        get
        {
            if (_hbFont is null)
            {
                _hbFont = new HbFont(HarfBuzzFace);
                _hbFont.SetFunctionsOpenType();
                int upem = Math.Max(1, HarfBuzzFace.UnitsPerEm);
                _hbFont.SetScale(upem, upem);
            }

            return _hbFont;
        }
    }

    /// <summary>Design-units per em, taken from the face rather than assumed.</summary>
    public float UnitsPerEm => Metrics.UnitsPerEm <= 0f ? 1000f : Metrics.UnitsPerEm;

    /// <summary>
    /// A HarfBuzz font carrying <paramref name="variations"/>. This is the shaping half of the
    /// vendored cosmic-text variable-font fix: the same canonical axis tuple that reaches
    /// rasterization also selects glyphs and advances here.
    /// </summary>
    public HbFont HarfBuzzFontFor(FontVariations? variations)
    {
        if (variations is null || variations.IsEmpty || !IsVariable)
        {
            return HarfBuzzFont;
        }

        if (_hbFontInstances.TryGetValue(variations, out HbFont? cached))
        {
            return cached;
        }

        var font = new HbFont(HarfBuzzFace);
        font.SetFunctionsOpenType();
        int upem = Math.Max(1, HarfBuzzFace.UnitsPerEm);
        font.SetScale(upem, upem);
        var coords = new HarfBuzzSharp.Variation[variations.Count];
        for (int i = 0; i < variations.Count; i++)
        {
            FontVariation variation = variations.Items[i];
            coords[i] = new HarfBuzzSharp.Variation
            {
                Tag = variation.Tag.Raw,
                Value = variation.Value.Value,
            };
        }

        font.SetVariations(coords);
        _hbFontInstances[variations.Clone()] = font;
        return font;
    }

    /// <summary>
    /// A Skia typeface carrying <paramref name="variations"/>. This is the rasterization half
    /// of the vendored fix; the tuple is the identical canonical one used for shaping.
    /// </summary>
    public SKTypeface TypefaceFor(FontVariations? variations)
    {
        if (variations is null || variations.IsEmpty || !IsVariable)
        {
            return Typeface;
        }

        if (_typefaceInstances.TryGetValue(variations, out SKTypeface? cached))
        {
            return cached;
        }

        var coordinates = new SKFontVariationPositionCoordinate[variations.Count];
        for (int i = 0; i < variations.Count; i++)
        {
            FontVariation variation = variations.Items[i];
            coordinates[i] = new SKFontVariationPositionCoordinate
            {
                Axis = new SKFourByteTag(variation.Tag.Raw),
                Value = variation.Value.Value,
            };
        }

        SKTypeface instance = Typeface.Clone(coordinates) ?? Typeface;
        _typefaceInstances[variations.Clone()] = instance;
        return instance;
    }

    /// <summary>Does this face have a real (non-notdef) glyph for <paramref name="codepoint"/>?</summary>
    public bool ContainsCodepoint(int codepoint)
    {
        _coverageFont ??= new SKFont(Typeface, 16f);
        return _coverageFont.GetGlyph(codepoint) != 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SKTypeface instance in _typefaceInstances.Values)
        {
            if (!ReferenceEquals(instance, Typeface))
            {
                instance.Dispose();
            }
        }

        _typefaceInstances.Clear();
        foreach (HbFont font in _hbFontInstances.Values)
        {
            font.Dispose();
        }

        _hbFontInstances.Clear();
        _coverageFont?.Dispose();
        _hbFont?.Dispose();
        _hbFace?.Dispose();
        Typeface.Dispose();
    }
}

/// <summary>
/// The engine's font database: embedded faces plus page-provided webfonts, and nothing else.
/// </summary>
/// <remarks>
/// This stands in for <c>cosmic_text::fontdb::Database</c> plus <c>FontSystem</c>. It never
/// consults the host: the engine ships its own faces so layout is byte-for-byte deterministic
/// across machines and works where no fontconfig exists. Faces are resolved with
/// <see cref="SKTypeface.FromData(SKData, int)"/>, never by family name.
/// </remarks>
public sealed class FontDatabase : IDisposable
{
    private readonly List<FaceRecord> _faces = [];
    private int _nextId;

    /// <summary>Every live face, in load order. Load order is the font-fallback order.</summary>
    public IReadOnlyList<FaceRecord> Faces => _faces;

    /// <summary>Load one font resource; returns the ids of the faces it contributed.</summary>
    public List<FontId> LoadFontSource(byte[] data)
    {
        List<FontId> ids = [];
        byte[] decoded = Woff.TryDecode(data, out byte[]? sfnt) ? sfnt! : data;
        using SKData skData = SKData.CreateCopy(decoded);
        for (int index = 0; ; index++)
        {
            SKTypeface? typeface = SKTypeface.FromData(skData, index);
            if (typeface is null)
            {
                break;
            }

            var record = new FaceRecord(new FontId(_nextId++), decoded, index, typeface);
            _faces.Add(record);
            ids.Add(record.Id);

            // Only TrueType collections carry more than one face, and Skia returns null past
            // the end. Guard against a driver that returns face 0 forever.
            if (index > 32)
            {
                break;
            }
        }

        return ids;
    }

    public FaceRecord? Face(FontId id)
    {
        foreach (FaceRecord face in _faces)
        {
            if (face.Id == id)
            {
                return face;
            }
        }

        return null;
    }

    public bool TryGetFace(FontId id, [NotNullWhen(true)] out FaceRecord? face)
    {
        face = Face(id);
        return face is not null;
    }

    public void RemoveFace(FontId id)
    {
        for (int i = 0; i < _faces.Count; i++)
        {
            if (_faces[i].Id == id)
            {
                _faces[i].Dispose();
                _faces.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>
    /// Faces to try for a cluster, in order: the requested face, then any other face declaring
    /// the same family, then everything else in load order.
    /// </summary>
    public IEnumerable<FaceRecord> FallbackOrder(FontId? preferred, string? familyName)
    {
        FaceRecord? first = preferred is { } id ? Face(id) : null;
        if (first is not null)
        {
            yield return first;
        }

        if (familyName is not null)
        {
            foreach (FaceRecord face in _faces)
            {
                if (!ReferenceEquals(face, first)
                    && string.Equals(face.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return face;
                }
            }
        }

        foreach (FaceRecord face in _faces)
        {
            if (ReferenceEquals(face, first))
            {
                continue;
            }

            if (familyName is not null
                && string.Equals(face.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return face;
        }
    }

    public void Dispose()
    {
        foreach (FaceRecord face in _faces)
        {
            face.Dispose();
        }

        _faces.Clear();
    }
}
