namespace Obscura.Render;

/// <summary>One premultiplied RGBA8 pixel.</summary>
public readonly record struct PremultipliedColor(byte R, byte G, byte B, byte A)
{
    /// <summary>Build from premultiplied components, clamping RGB to the alpha as tiny-skia does.</summary>
    public static PremultipliedColor FromRgba(byte r, byte g, byte b, byte a) =>
        r <= a && g <= a && b <= a ? new PremultipliedColor(r, g, b, a) : new PremultipliedColor(a, a, a, a);
}

/// <summary>
/// A premultiplied RGBA8 raster target.
/// </summary>
/// <remarks>
/// RECONCILIATION NOTE: this is the inline layer's stand-in for <c>tiny_skia::Pixmap</c>, which
/// <c>paint.rs</c> owns in the Rust tree. It exists here because glyph rasterization needs a
/// destination surface and the paint port has not landed. The paint agent should replace it
/// with (or fold it into) the real surface type rather than declaring a second one.
/// </remarks>
public sealed class Pixmap
{
    public Pixmap(uint width, uint height)
    {
        Width = width;
        Height = height;
        Pixels = new PremultipliedColor[(int)(width * height)];
    }

    public uint Width { get; }

    public uint Height { get; }

    public PremultipliedColor[] Pixels { get; }
}

/// <summary>An 8-bit coverage mask in the destination surface's pixel grid.</summary>
public sealed class Mask(uint width, uint height)
{
    public uint Width { get; } = width;

    public uint Height { get; } = height;

    public byte[] Data { get; } = new byte[(int)(width * height)];
}
