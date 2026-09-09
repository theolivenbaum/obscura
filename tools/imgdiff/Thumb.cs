using SkiaSharp;

// Scale a screenshot down and re-encode as JPEG so fifteen of them fit in one
// self-contained HTML page.
internal static class Thumb
{
    internal static int Run(string[] args)
    {
        var (src, dst) = (args[1], args[2]);
        int width = args.Length > 3 ? int.Parse(args[3]) : 620;
        int quality = args.Length > 4 ? int.Parse(args[4]) : 78;
        using var bitmap = SKBitmap.Decode(src);
        if (bitmap is null) { Console.Error.WriteLine($"decode failed: {src}"); return 1; }
        int height = (int)Math.Round(bitmap.Height * (width / (double)bitmap.Width));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var scaled = bitmap.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (scaled is null) { Console.Error.WriteLine("resize failed"); return 1; }
        using var image = SKImage.FromBitmap(scaled);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        using var stream = File.OpenWrite(dst);
        data.SaveTo(stream);
        Console.WriteLine($"{dst} {width}x{height}");
        return 0;
    }
}
