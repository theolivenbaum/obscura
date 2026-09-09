using SkiaSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>Whether a rasterized glyph carries coverage or real color.</summary>
public enum GlyphContent
{
    Mask,
    Color,
}

/// <summary>One rasterized glyph image, positioned relative to the glyph origin.</summary>
/// <remarks>
/// <see cref="Left"/> and <see cref="Top"/> are the pixel offsets of the image's top-left
/// corner from the glyph origin, with y increasing downward. This matches what the Rust engine
/// derives from swash's <c>Placement</c> (<c>left</c>, and <c>-top</c>).
/// </remarks>
public sealed class GlyphImage
{
    public required int Left { get; init; }

    public required int Top { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required GlyphContent Content { get; init; }

    /// <summary>Coverage bytes for a mask, or RGBA quads for color.</summary>
    public required byte[] Data { get; init; }
}

/// <summary>
/// Skia-backed glyph rasterization.
/// </summary>
/// <remarks>
/// PORT NOTE. The Rust engine rasterizes with swash
/// (<c>Render::new([ColorOutline, ColorBitmap(BestFit), Outline]).format(Alpha)</c>, hinted,
/// with a 14-degree skew for synthetic italic). This uses Skia's own glyph rasterizer with the
/// same source order (color outline and color bitmap first, then the plain outline, which Skia
/// selects internally), the same subpixel offset binning, and the same skew. Coverage values
/// are Skia's, not swash's: the two anti-alias slightly differently, so per-pixel alpha can
/// differ by a few counts even when geometry agrees. Ink totals and glyph placement are
/// comparable; exact pixel equality is not.
/// </remarks>
public sealed class GlyphRasterizer(FontDatabase database) : IDisposable
{
    private readonly FontDatabase _database = database;
    private readonly Dictionary<(GlyphCacheKey Key, FontVariations? Variations), GlyphImage?> _images = [];
    private readonly Dictionary<string, bool> _colorFaces = new(StringComparer.Ordinal);

    /// <summary>Every cached raster entry's key. Tests inspect the axis tuples it holds.</summary>
    public IEnumerable<(GlyphCacheKey Key, FontVariations? Variations)> CachedKeys => _images.Keys;

    public void Clear() => _images.Clear();

    /// <summary>Rasterize (or fetch) one glyph image.</summary>
    public GlyphImage? Rasterize(GlyphCacheKey key, FontVariations? variations)
    {
        if (_images.TryGetValue((key, variations), out GlyphImage? cached))
        {
            return cached;
        }

        GlyphImage? image = Render(key, variations);
        _images[(key, variations)] = image;
        return image;
    }

    private GlyphImage? Render(GlyphCacheKey key, FontVariations? variations)
    {
        FaceRecord? face = _database.Face(key.FontId);
        if (face is null)
        {
            return null;
        }

        SKTypeface typeface = face.TypefaceFor(variations);
        float size = key.FontSize;
        if (!float.IsFinite(size) || size <= 0f)
        {
            return null;
        }

        using var font = new SKFont(typeface, size)
        {
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Slight,
            LinearMetrics = false,
        };
        if (key.FakeItalic)
        {
            // swash skews synthetic italic by 14 degrees; Skia's SkewX runs the other way.
            font.SkewX = -MathF.Tan(14f * MathF.PI / 180f);
        }

        ushort[] glyphs = [key.GlyphId];
        var bounds = new SKRect[1];
        font.GetGlyphWidths(glyphs, null, bounds);
        SKRect ink = bounds[0];
        float dx = key.XBin.AsFloat();
        float dy = key.YBin.AsFloat();
        if (ink.Width <= 0f || ink.Height <= 0f)
        {
            return null;
        }

        int left = (int)MathF.Floor(ink.Left + dx) - 1;
        int top = (int)MathF.Floor(ink.Top + dy) - 1;
        int right = (int)MathF.Ceiling(ink.Right + dx) + 1;
        int bottom = (int)MathF.Ceiling(ink.Bottom + dy) + 1;
        int width = right - left;
        int height = bottom - top;
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
        {
            return null;
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            using var builder = new SKTextBlobBuilder();
            builder.AddPositionedRun(glyphs, font, [new SKPoint(dx - left, dy - top)]);
            using SKTextBlob? blob = builder.Build();
            if (blob is not null)
            {
                canvas.DrawText(blob, 0, 0, paint);
            }
        }

        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
        bool color = IsColorFace(typeface) && HasColor(pixels);
        if (color)
        {
            return new GlyphImage
            {
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Content = GlyphContent.Color,
                Data = pixels.ToArray(),
            };
        }

        var mask = new byte[width * height];
        for (int i = 0; i < mask.Length; i++)
        {
            mask[i] = pixels[(i * 4) + 3];
        }

        return new GlyphImage
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Content = GlyphContent.Mask,
            Data = mask,
        };
    }

    private static bool HasColor(ReadOnlySpan<byte> pixels)
    {
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
            {
                continue;
            }

            if (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsColorFace(SKTypeface typeface)
    {
        string name = typeface.FamilyName ?? string.Empty;
        if (_colorFaces.TryGetValue(name, out bool cached))
        {
            return cached;
        }

        bool color = false;
        foreach (uint tag in typeface.GetTableTags())
        {
            // CBDT, sbix, COLR: the three color-glyph carriers Skia rasterizes.
            if (tag is 0x43424454 or 0x73626978 or 0x434F4C52)
            {
                color = true;
                break;
            }
        }

        _colorFaces[name] = color;
        return color;
    }

    public void Dispose() => _images.Clear();
}

/// <summary>
/// Variable-instance glyph cache.
/// </summary>
/// <remarks>
/// The ordinary glyph cache key intentionally has no variation coordinates (cosmic-text's
/// <c>CacheKey</c> does not carry them either). Variable instances therefore live in their own
/// cache so different axis tuples can never share an outline and repeated paint stays O(1)
/// after the first raster. <see cref="EffectiveVariations"/> is the canonicalization step: it
/// resolves the span's authored and automatic intent against the axes the <em>actually
/// selected</em> face provides, so unsupported axes and values that clamp to the same endpoint
/// share one raster entry.
/// </remarks>
public sealed class VariableGlyphCache(FontDatabase database)
{
    private readonly FontDatabase _database = database;

    private readonly Dictionary<
        (FontId FontId, VariationValue? Weight, VariationValue? Optical, bool Italic, FontVariations? Explicit),
        FontVariations?> _instances = [];

    public FontVariations? EffectiveVariations(
        FontId fontId,
        float? weight,
        float? opticalSize,
        bool italic,
        FontVariations? explicitSettings)
    {
        VariationValue? weightKey = weight is { } w && float.IsFinite(w) ? new VariationValue(w) : null;
        VariationValue? opticalKey = opticalSize is { } o && float.IsFinite(o) ? new VariationValue(o) : null;
        var key = (fontId, weightKey, opticalKey, italic, explicitSettings);
        if (_instances.TryGetValue(key, out FontVariations? cached))
        {
            return cached;
        }

        FontVariations? resolved = null;
        if (_database.Face(fontId) is { } face)
        {
            VariationTag weightTag = VariationTag.FromAscii("wght");
            VariationTag opticalTag = VariationTag.FromAscii("opsz");
            VariationTag italicTag = VariationTag.FromAscii("ital");
            VariationTag slantTag = VariationTag.FromAscii("slnt");
            bool hasItalicAxis = false;
            foreach (FontAxis axis in face.Axes)
            {
                if (axis.Tag == italicTag)
                {
                    hasItalicAxis = true;
                    break;
                }
            }

            var variations = new FontVariations();
            foreach (FontAxis axis in face.Axes)
            {
                float? explicitValue = explicitSettings?.Find(axis.Tag);
                float? automatic = null;
                if (axis.Tag == weightTag)
                {
                    automatic = weightKey?.Value;
                }
                else if (axis.Tag == opticalTag)
                {
                    automatic = opticalKey?.Value;
                }
                else if (italic && axis.Tag == italicTag)
                {
                    automatic = 1f;
                }
                else if (italic && !hasItalicAxis && axis.Tag == slantTag)
                {
                    automatic = -14f;
                }

                if ((explicitValue ?? automatic) is not { } value || !float.IsFinite(value))
                {
                    continue;
                }

                variations.Set(axis.Tag, Math.Clamp(value, axis.Min, axis.Max) + 0f);
            }

            resolved = variations.IsEmpty ? null : variations;
        }

        _instances[key] = resolved;
        return resolved;
    }
}
