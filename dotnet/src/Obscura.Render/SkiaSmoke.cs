using SkiaSharp;

namespace Obscura.Render;

/// <summary>Startup probe that the native Skia and HarfBuzz assets resolve.</summary>
public static class SkiaSmoke
{
    public static string Probe()
    {
        var lines = new List<string>();
        using var surface = SKSurface.Create(new SKImageInfo(64, 32, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Red, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var builder = new SKPathBuilder();
        builder.MoveTo(4, 4);
        builder.LineTo(60, 8);
        builder.LineTo(30, 28);
        builder.Close();
        using var path = builder.Detach();
        canvas.DrawPath(path, paint);

        using var img = surface.Snapshot();
        using var png = img.Encode(SKEncodedImageFormat.Png, 100);
        lines.Add($"skia loaded     = {typeof(SKSurface).Assembly.GetName().Version}");
        lines.Add($"png encode      = {png.Size} bytes");

        using var bmp = SKBitmap.Decode(png.ToArray());
        lines.Add($"png decode      = {bmp.Width}x{bmp.Height} {bmp.ColorType}");
        lines.Add($"antialiased px  = {bmp.GetPixel(10, 6)}");

        // The engine never uses system fonts: it ships its own faces so
        // rasterization is identical on every host (and works on distroless,
        // where no fontconfig exists). Same set the Rust engine embeds.
        var asm = typeof(SkiaSmoke).Assembly;
        var fontNames = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".ttf", StringComparison.Ordinal)).Order().ToArray();
        lines.Add($"embedded fonts  = {fontNames.Length}");
        using var fontStream = asm.GetManifestResourceStream(
            fontNames.First(n => n.Contains("liberation-sans.ttf", StringComparison.Ordinal)))!;
        using var ms = new MemoryStream();
        fontStream.CopyTo(ms);
        using var data = SKData.CreateCopy(ms.ToArray());
        using var tf = SKTypeface.FromData(data)!;
        using var font = new SKFont(tf, 16);
        lines.Add($"typeface        = {tf.FamilyName}");
        lines.Add($"font measure    = {font.MeasureText("Obscura"):0.##}px");

        using var blob = new SkiaSharp.HarfBuzz.SKShaper(tf);
        lines.Add("harfbuzz        = loaded");
        return string.Join('\n', lines);
    }
}
