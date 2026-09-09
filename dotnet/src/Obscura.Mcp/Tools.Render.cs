using System.Text.Json.Nodes;
using Obscura.Browser;

namespace Obscura.Mcp;

/// <summary>
/// The two capture tools. Rust gates these behind <c>#[cfg(feature = "render")]</c>;
/// the C# port always compiles rendering in, so they are always available.
/// </summary>
internal static partial class Tools
{
    private static void ValidateToolOptions(JsonNode? args, params string[] allowed)
    {
        if (args.IsNull())
        {
            return;
        }

        var obj = args.ObjectOrNull() ?? throw new ToolException("tool arguments must be an object");
        foreach (var pair in obj)
        {
            if (Array.IndexOf(allowed, pair.Key) < 0)
            {
                throw new ToolException($"unsupported option '{pair.Key}'");
            }
        }
    }

    private static float? OptionalNumber(JsonNode? args, string name)
    {
        if (!args.Has(name))
        {
            return null;
        }

        var value = args.Get(name).AsF64() ?? throw new ToolException($"'{name}' must be a number");
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
        {
            throw new ToolException($"'{name}' must be a finite number");
        }

        return (float)value;
    }

    private static bool? OptionalBool(JsonNode? args, string name)
    {
        if (!args.Has(name))
        {
            return null;
        }

        return args.Get(name).AsBool() ?? throw new ToolException($"'{name}' must be a boolean");
    }

    private static void ValidateScreenshotViewport((float Width, float Height) viewport)
    {
        const float maxDimension = 32_768.0f;
        const double maxPixels = 16 * 1024 * 1024;
        var (width, height) = viewport;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0.0f || height <= 0.0f
            || width > maxDimension || height > maxDimension)
        {
            throw new ToolException(
                "screenshot dimensions must be finite, positive, and at most 32768 CSS pixels");
        }

        if ((double)MathF.Ceiling(width) * MathF.Ceiling(height) > maxPixels)
        {
            throw new ToolException("screenshot dimensions exceed the 16-megapixel capture limit");
        }
    }

    internal static async Task<JsonNode> ScreenshotAsync(JsonNode? args, BrowserState state)
    {
        ValidateToolOptions(args, "width", "height");
        var current = state.PageMut().Viewport;
        var viewport = (
            OptionalNumber(args, "width") ?? current.Width,
            OptionalNumber(args, "height") ?? current.Height);
        ValidateScreenshotViewport(viewport);

        var page = state.PageMut();
        _ = await page.PrepareScreenshotResourcesAsync(1_000).ConfigureAwait(false);
        var png = page.Screenshot(viewport)
            ?? throw new ToolException("the current page has no renderable viewport");
        return new JsonObject
        {
            ["type"] = "image",
            ["data"] = Convert.ToBase64String(png),
            ["mimeType"] = "image/png",
        };
    }

    internal static async Task<JsonNode> PdfAsync(JsonNode? args, BrowserState state)
    {
        ValidateToolOptions(
            args,
            "landscape", "print_background", "scale", "paper_width", "paper_height",
            "margin_top", "margin_bottom", "margin_left", "margin_right");
        var options = new RasterPdfOptions();
        if (OptionalBool(args, "landscape") is { } landscape)
        {
            options.Landscape = landscape;
        }

        if (OptionalBool(args, "print_background") is { } printBackground)
        {
            options.PrintBackground = printBackground;
        }

        if (OptionalNumber(args, "scale") is { } scale)
        {
            options.Scale = scale;
        }

        if (OptionalNumber(args, "paper_width") is { } paperWidth)
        {
            options.PaperWidthIn = paperWidth;
        }

        if (OptionalNumber(args, "paper_height") is { } paperHeight)
        {
            options.PaperHeightIn = paperHeight;
        }

        if (OptionalNumber(args, "margin_top") is { } marginTop)
        {
            options.MarginTopIn = marginTop;
        }

        if (OptionalNumber(args, "margin_bottom") is { } marginBottom)
        {
            options.MarginBottomIn = marginBottom;
        }

        if (OptionalNumber(args, "margin_left") is { } marginLeft)
        {
            options.MarginLeftIn = marginLeft;
        }

        if (OptionalNumber(args, "margin_right") is { } marginRight)
        {
            options.MarginRightIn = marginRight;
        }

        var page = state.PageMut();
        _ = await page.PrepareScreenshotResourcesAsync(1_000).ConfigureAwait(false);
        byte[] pdf;
        try
        {
            pdf = page.RasterPdf(options);
        }
        catch (Exception error) when (error is not ToolException)
        {
            throw new ToolException(error.Message);
        }

        return new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "obscura://capture/current-page.pdf",
                ["mimeType"] = "application/pdf",
                ["blob"] = Convert.ToBase64String(pdf),
            },
        };
    }
}
