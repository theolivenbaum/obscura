// The SVG rasterizer standing in for `usvg` + `resvg` in
// crates/obscura-render/src/paint.rs (`render_svg`, `render_svg_with_font_database`,
// `svg_font_database`, `svg_font_database_with_web_fonts`).
//
// PORT NOTE. The Rust engine parses with usvg and rasterizes with resvg. There is no managed
// equivalent, and the dependency set is closed, so this is an in-tree renderer over Skia
// covering the SVG subset the engine actually paints: shapes, paths, groups, transforms,
// viewBox/preserveAspectRatio, use/symbol/defs, linear/radial gradients, patterns, clip paths,
// text runs, and the CSS presentation attributes the serializer carries across. Unsupported
// constructs are skipped rather than failing the raster, which matches how the reference
// degrades on markup usvg cannot represent.
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>
/// The deterministic font set shared by every SVG raster, plus any page web fonts.
/// </summary>
public sealed class SvgFontDatabase
{
    private readonly Dictionary<string, SKTypeface> _byFamily = new(StringComparer.OrdinalIgnoreCase);

    private SvgFontDatabase()
    {
    }

    /// <summary>
    /// A deterministic font database shared by every SVG raster in the process, built from the
    /// same embedded browser-generic families the HTML text engine uses.
    /// </summary>
    public static SvgFontDatabase Shared { get; } = BuildShared();

    internal static SvgFontDatabase WithWebFonts(IReadOnlyList<WebFont> webFonts)
    {
        if (webFonts.Count == 0)
        {
            return Shared;
        }

        SvgFontDatabase database = new();
        foreach ((string family, SKTypeface typeface) in Shared._byFamily)
        {
            database._byFamily[family] = typeface;
        }

        foreach (WebFont font in webFonts)
        {
            try
            {
                using SKData data = SKData.CreateCopy(font.Data);
                SKTypeface? typeface = SKTypeface.FromData(data);
                if (typeface is null)
                {
                    continue;
                }

                string family = font.Family ?? typeface.FamilyName;
                if (!string.IsNullOrEmpty(family))
                {
                    database._byFamily[family] = typeface;
                }
            }
            catch (Exception)
            {
                // A page font that will not decode simply does not join the database.
            }
        }

        return database;
    }

    private static SvgFontDatabase BuildShared()
    {
        SvgFontDatabase database = new();
        foreach (string stem in (string[])
        [
            "liberation-sans",
            "liberation-sans-bold",
            "liberation-sans-oblique",
            "liberation-sans-boldoblique",
            "liberation-serif",
            "liberation-mono",

            // The Liberation families stop at Latin/Greek/Cyrillic. DejaVu Sans is the same
            // embedded broad-coverage face the HTML engine keeps for `system-ui`; it only
            // extends what the fallback search can find.
            "dejavu-sans",
        ])
        {
            try
            {
                using SKData data = SKData.CreateCopy(FontAssets.Load(stem));
                SKTypeface? typeface = SKTypeface.FromData(data);
                if (typeface is not null)
                {
                    database._byFamily[stem] = typeface;
                    if (!string.IsNullOrEmpty(typeface.FamilyName))
                    {
                        database._byFamily.TryAdd(typeface.FamilyName, typeface);
                    }
                }
            }
            catch (Exception)
            {
                // A missing embedded face leaves the family unavailable rather than failing.
            }
        }

        return database;
    }

    internal SKTypeface Resolve(string? family, bool bold, bool italic)
    {
        if (family is not null)
        {
            foreach (string raw in family.Split(','))
            {
                string token = raw.Trim().Trim('"', '\'');
                if (token.Length == 0)
                {
                    continue;
                }

                if (_byFamily.TryGetValue(token, out SKTypeface? direct))
                {
                    return direct;
                }

                string stem = DomTextMeasure.FallbackFaceStem(token);
                if (stem == "liberation-sans" && !IsGenericSans(token))
                {
                    continue;
                }

                if (_byFamily.TryGetValue(SansVariant(stem, bold, italic), out SKTypeface? variant))
                {
                    return variant;
                }

                if (_byFamily.TryGetValue(stem, out SKTypeface? resolved))
                {
                    return resolved;
                }
            }
        }

        string fallback = SansVariant("liberation-sans", bold, italic);
        if (_byFamily.TryGetValue(fallback, out SKTypeface? face))
        {
            return face;
        }

        return _byFamily.Values.First();

        static bool IsGenericSans(string token) =>
            token.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)
            || token.Contains("sans", StringComparison.OrdinalIgnoreCase)
            || token.Equals("arial", StringComparison.OrdinalIgnoreCase)
            || token.Equals("helvetica", StringComparison.OrdinalIgnoreCase);

        static string SansVariant(string stem, bool bold, bool italic) => stem switch
        {
            "liberation-sans" when bold && italic => "liberation-sans-boldoblique",
            "liberation-sans" when bold => "liberation-sans-bold",
            "liberation-sans" when italic => "liberation-sans-oblique",
            _ => stem,
        };
    }
}

/// <summary>One parsed standalone SVG document.</summary>
internal sealed class SvgDocument
{
    private SvgDocument(XElement root)
    {
        Root = root;
        ById = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement element in root.DescendantsAndSelf())
        {
            string? id = element.Attribute("id")?.Value;
            if (id is not null)
            {
                ById.TryAdd(id, element);
            }
        }
    }

    internal XElement Root { get; }

    internal Dictionary<string, XElement> ById { get; }

    internal static SvgDocument? Parse(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = false,
                MaxCharactersFromEntities = 1024,
                CheckCharacters = false,
            };
            using MemoryStream stream = new(bytes);
            using XmlReader reader = XmlReader.Create(stream, settings);
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement? root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.Ordinal))
            {
                return null;
            }

            return new SvgDocument(root);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal (float Width, float Height)? DefaultSize()
    {
        float? width = SvgRenderer.ParseLength(Root.Attribute("width")?.Value);
        float? height = SvgRenderer.ParseLength(Root.Attribute("height")?.Value);
        (float X, float Y, float Width, float Height)? viewBox =
            SvgRenderer.ParseViewBox(Root.Attribute("viewBox")?.Value);
        if (width is { } w && height is { } h)
        {
            return (w, h);
        }

        if (viewBox is { } box && box.Width > 0f && box.Height > 0f)
        {
            return (width ?? box.Width, height ?? box.Height);
        }

        return width is { } onlyWidth
            ? (onlyWidth, 100f)
            : height is { } onlyHeight ? (100f, onlyHeight) : (100f, 100f);
    }
}

internal static class SvgRenderer
{
    /// <summary>Rasterize SVG bytes to a <paramref name="width"/> x <paramref name="height"/> pixmap.</summary>
    internal static Pixmap? Render(byte[] bytes, uint width, uint height) =>
        RenderWithFontDatabase(bytes, width, height, SvgFontDatabase.Shared);

    internal static Pixmap? RenderWithFontDatabase(
        byte[] bytes,
        uint width,
        uint height,
        SvgFontDatabase fonts)
    {
        if (width == 0 || height == 0)
        {
            return null;
        }

        // The outer replaced element supplies the SVG document viewport. Force that used CSS
        // size onto the root before `viewBox` is resolved.
        byte[]? viewportSvg = PaintSvg.SvgWithRootViewport(bytes, width, height) ?? bytes;
        SvgDocument? document = SvgDocument.Parse(viewportSvg);
        if (document is null)
        {
            return null;
        }

        Pixmap? pixmap = Pixmap.New(width, height);
        if (pixmap is null)
        {
            return null;
        }

        try
        {
            SKCanvas canvas = pixmap.Canvas;
            canvas.Save();
            canvas.ResetMatrix();
            (float X, float Y, float Width, float Height)? viewBox =
                ParseViewBox(document.Root.Attribute("viewBox")?.Value);
            if (viewBox is { } box && box.Width > 0f && box.Height > 0f)
            {
                SKMatrix fit = ViewBoxMatrix(
                    box,
                    width,
                    height,
                    document.Root.Attribute("preserveAspectRatio")?.Value);
                canvas.Concat(fit);
            }

            SvgPaintState state = SvgPaintState.Initial(fonts, document);
            foreach (XElement child in document.Root.Elements())
            {
                RenderElement(canvas, child, state.Inherit(document.Root), document, fonts);
            }

            canvas.Restore();
            return pixmap;
        }
        catch (Exception)
        {
            pixmap.Dispose();
            return null;
        }
    }

    internal static SKMatrix ViewBoxMatrix(
        (float X, float Y, float Width, float Height) viewBox,
        float width,
        float height,
        string? preserveAspectRatio)
    {
        string spec = (preserveAspectRatio ?? "xMidYMid meet").Trim();
        bool slice = spec.EndsWith("slice", StringComparison.OrdinalIgnoreCase);
        string align = spec.Split(
            (char[])[' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "xMidYMid";
        float scaleX = width / viewBox.Width;
        float scaleY = height / viewBox.Height;
        if (!align.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            float uniform = slice ? F32.Max(scaleX, scaleY) : F32.Min(scaleX, scaleY);
            scaleX = uniform;
            scaleY = uniform;
        }

        float translateX = -viewBox.X * scaleX;
        float translateY = -viewBox.Y * scaleY;
        float extraX = width - (viewBox.Width * scaleX);
        float extraY = height - (viewBox.Height * scaleY);
        if (align.Contains("xMid", StringComparison.Ordinal))
        {
            translateX += extraX / 2f;
        }
        else if (align.Contains("xMax", StringComparison.Ordinal))
        {
            translateX += extraX;
        }

        if (align.Contains("YMid", StringComparison.Ordinal))
        {
            translateY += extraY / 2f;
        }
        else if (align.Contains("YMax", StringComparison.Ordinal))
        {
            translateY += extraY;
        }

        return new SKMatrix(scaleX, 0f, translateX, 0f, scaleY, translateY, 0f, 0f, 1f);
    }

    private static void RenderElement(
        SKCanvas canvas,
        XElement element,
        SvgPaintState inherited,
        SvgDocument document,
        SvgFontDatabase fonts,
        int depth = 0)
    {
        if (depth > 24)
        {
            return;
        }

        string tag = element.Name.LocalName;
        if (tag is "defs" or "symbol" or "clipPath" or "mask" or "pattern" or "linearGradient"
            or "radialGradient" or "style" or "title" or "desc" or "metadata" or "filter")
        {
            return;
        }

        SvgPaintState state = inherited.Inherit(element);
        if (state.Display == "none" || state.Visibility == "hidden")
        {
            return;
        }

        int saved = canvas.Save();
        try
        {
            if (ParseTransform(element.Attribute("transform")?.Value) is { } transform)
            {
                canvas.Concat(transform);
            }

            if (state.ClipPathRef is { } clipId
                && document.ById.TryGetValue(clipId, out XElement? clipElement)
                && string.Equals(clipElement.Name.LocalName, "clipPath", StringComparison.Ordinal))
            {
                using SKPathBuilder builder = new();
                foreach (XElement shape in clipElement.Elements())
                {
                    SKPath? part = ShapePath(shape);
                    if (part is null)
                    {
                        continue;
                    }

                    builder.AddPath(part, SKPathAddMode.Append);
                    part.Dispose();
                }

                using SKPath clipPath = builder.Detach();
                canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
            }

            if (state.Opacity < 1f)
            {
                using SKPaint layerPaint = new()
                {
                    Color = new SKColor(0, 0, 0, (byte)Math.Clamp((int)MathF.Round(state.Opacity * 255f), 0, 255)),
                };
                canvas.SaveLayer(layerPaint);
            }

            switch (tag)
            {
                case "svg":
                case "g":
                case "a":
                case "switch":
                    foreach (XElement child in element.Elements())
                    {
                        RenderElement(canvas, child, state, document, fonts, depth + 1);
                    }

                    break;
                case "use":
                    RenderUse(canvas, element, state, document, fonts, depth);
                    break;
                case "text":
                    RenderText(canvas, element, state, document, fonts);
                    break;
                case "image":
                    RenderImage(canvas, element);
                    break;
                default:
                {
                    SKPath? path = ShapePath(element);
                    if (path is not null)
                    {
                        using SKPath owned = path;
                        FillAndStroke(canvas, owned, state, document, fonts);
                    }

                    break;
                }
            }

            if (state.Opacity < 1f)
            {
                canvas.Restore();
            }
        }
        catch (Exception)
        {
            // A malformed element must never abort the whole raster.
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    private static void RenderUse(
        SKCanvas canvas,
        XElement element,
        SvgPaintState state,
        SvgDocument document,
        SvgFontDatabase fonts,
        int depth)
    {
        string? href = element.Attribute("href")?.Value
            ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
        if (href is null || !href.StartsWith('#'))
        {
            return;
        }

        if (!document.ById.TryGetValue(href[1..], out XElement? target))
        {
            return;
        }

        float x = ParseLength(element.Attribute("x")?.Value) ?? 0f;
        float y = ParseLength(element.Attribute("y")?.Value) ?? 0f;
        int saved = canvas.Save();
        canvas.Translate(x, y);
        if (string.Equals(target.Name.LocalName, "symbol", StringComparison.Ordinal)
            || string.Equals(target.Name.LocalName, "svg", StringComparison.Ordinal))
        {
            SvgPaintState symbolState = state.Inherit(target);
            float width = ParseLength(element.Attribute("width")?.Value)
                ?? ParseLength(target.Attribute("width")?.Value)
                ?? 0f;
            float height = ParseLength(element.Attribute("height")?.Value)
                ?? ParseLength(target.Attribute("height")?.Value)
                ?? 0f;
            if (ParseViewBox(target.Attribute("viewBox")?.Value) is { } box
                && box.Width > 0f && box.Height > 0f
                && width > 0f && height > 0f)
            {
                canvas.Concat(ViewBoxMatrix(
                    box,
                    width,
                    height,
                    target.Attribute("preserveAspectRatio")?.Value));
            }

            foreach (XElement child in target.Elements())
            {
                RenderElement(canvas, child, symbolState, document, fonts, depth + 1);
            }
        }
        else
        {
            RenderElement(canvas, target, state, document, fonts, depth + 1);
        }

        canvas.RestoreToCount(saved);
    }

    private static void RenderImage(SKCanvas canvas, XElement element)
    {
        string? href = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
        if (href is null || !href.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }

        RenderResourceCache scratch = RenderResourceCache.WithLoaderAndLimits(_ => null, 0, 0);
        byte[]? bytes = PaintResources.FetchBytes(href, null, scratch);
        if (bytes is null)
        {
            return;
        }

        float x = ParseLength(element.Attribute("x")?.Value) ?? 0f;
        float y = ParseLength(element.Attribute("y")?.Value) ?? 0f;
        float width = ParseLength(element.Attribute("width")?.Value) ?? 0f;
        float height = ParseLength(element.Attribute("height")?.Value) ?? 0f;
        if (width <= 0f || height <= 0f)
        {
            return;
        }

        try
        {
            using SKImage? image = SKImage.FromEncodedData(bytes);
            if (image is not null)
            {
                canvas.DrawImage(
                    image,
                    new SKRect(x, y, x + width, y + height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                    null);
            }
        }
        catch (Exception)
        {
            // An embedded image that will not decode simply does not paint.
        }
    }

    private static void RenderText(
        SKCanvas canvas,
        XElement element,
        SvgPaintState state,
        SvgDocument document,
        SvgFontDatabase fonts)
    {
        string content = TextContent(element);
        if (content.Trim().Length == 0)
        {
            return;
        }

        float x = ParseLength(element.Attribute("x")?.Value) ?? 0f;
        float y = ParseLength(element.Attribute("y")?.Value) ?? 0f;
        bool bold = state.FontWeight >= 600;
        SKTypeface typeface = fonts.Resolve(state.FontFamily, bold, state.FontItalic);
        using SKFont font = new(typeface, state.FontSize)
        {
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.None,
        };
        float advance = font.MeasureText(content);
        float originX = state.TextAnchor switch
        {
            "middle" => x - (advance / 2f),
            "end" => x - advance,
            _ => x,
        };

        if (state.Fill is { } fill)
        {
            using SKPaint paint = new()
            {
                Color = WithOpacity(fill, state.FillOpacity),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawText(content, originX, y, SKTextAlign.Left, font, paint);
        }

        if (state.Stroke is { } stroke && state.StrokeWidth > 0f)
        {
            using SKPaint paint = new()
            {
                Color = WithOpacity(stroke, state.StrokeOpacity),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = state.StrokeWidth,
            };
            canvas.DrawText(content, originX, y, SKTextAlign.Left, font, paint);
        }
    }

    private static string TextContent(XElement element)
    {
        StringBuilder buffer = new();
        foreach (XNode node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    buffer.Append(text.Value);
                    break;
                case XElement child:
                    buffer.Append(TextContent(child));
                    break;
                default:
                    break;
            }
        }

        return buffer.ToString();
    }

    private static void FillAndStroke(
        SKCanvas canvas,
        SKPath path,
        SvgPaintState state,
        SvgDocument document,
        SvgFontDatabase fonts)
    {
        path.FillType = state.FillRule == "evenodd" ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
        if (state.FillRef is { } fillRef)
        {
            using SKShader? shader = ResolvePaintServer(fillRef, path.Bounds, state, document, fonts);
            if (shader is not null)
            {
                using SKPaint paint = new()
                {
                    Shader = shader,
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                };
                canvas.DrawPath(path, paint);
            }
        }
        else if (state.Fill is { } fill)
        {
            using SKPaint paint = new()
            {
                Color = WithOpacity(fill, state.FillOpacity),
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawPath(path, paint);
        }

        if (state.StrokeWidth <= 0f)
        {
            return;
        }

        SKShader? strokeShader = state.StrokeRef is { } strokeRef
            ? ResolvePaintServer(strokeRef, path.Bounds, state, document, fonts)
            : null;
        if (strokeShader is null && state.Stroke is null)
        {
            return;
        }

        using SKPaint strokePaint = new()
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = state.StrokeWidth,
            StrokeCap = state.StrokeLineCap switch
            {
                "round" => SKStrokeCap.Round,
                "square" => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt,
            },
            StrokeJoin = state.StrokeLineJoin switch
            {
                "round" => SKStrokeJoin.Round,
                "bevel" => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter,
            },
        };
        if (strokeShader is not null)
        {
            strokePaint.Shader = strokeShader;
        }
        else if (state.Stroke is { } stroke)
        {
            strokePaint.Color = WithOpacity(stroke, state.StrokeOpacity);
        }

        if (state.StrokeDashArray is { Length: > 0 } dashes)
        {
            float[] pattern = dashes.Length % 2 == 0 ? dashes : [.. dashes, .. dashes];
            strokePaint.PathEffect = SKPathEffect.CreateDash(pattern, state.StrokeDashOffset);
        }

        canvas.DrawPath(path, strokePaint);
        strokePaint.PathEffect?.Dispose();
        strokeShader?.Dispose();
    }

    private static SKShader? ResolvePaintServer(
        string id,
        SKRect bounds,
        SvgPaintState state,
        SvgDocument document,
        SvgFontDatabase fonts)
    {
        if (!document.ById.TryGetValue(id, out XElement? server))
        {
            return null;
        }

        return server.Name.LocalName switch
        {
            "linearGradient" => LinearGradientShader(server, bounds, state, document),
            "radialGradient" => RadialGradientShader(server, bounds, state, document),
            "pattern" => PatternShader(server, bounds, state, document, fonts),
            _ => null,
        };
    }

    private static List<(float Offset, SKColor Color)> GradientStops(
        XElement gradient,
        SvgPaintState state,
        SvgDocument document)
    {
        List<(float, SKColor)> stops = [];
        XElement source = gradient;
        if (!gradient.Elements().Any(child => child.Name.LocalName == "stop")
            && gradient.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value
                is { } href
            && href.StartsWith('#')
            && document.ById.TryGetValue(href[1..], out XElement? inherited))
        {
            source = inherited;
        }

        foreach (XElement stop in source.Elements().Where(child => child.Name.LocalName == "stop"))
        {
            float offset = ParseNumberOrPercent(
                SvgPaintState.Property(stop, "offset") ?? "0",
                1f) ?? 0f;
            RgbaColor color = SvgPaintState.ResolveColor(
                SvgPaintState.Property(stop, "stop-color") ?? "black",
                state.CurrentColor) ?? new RgbaColor(0, 0, 0, 255);
            float opacity = ParseNumberOrPercent(
                SvgPaintState.Property(stop, "stop-opacity") ?? "1",
                1f) ?? 1f;
            stops.Add((Math.Clamp(offset, 0f, 1f), WithOpacity(color, opacity)));
        }

        return stops;
    }

    private static SKMatrix UnitsMatrix(XElement gradient, SKRect bounds, string attribute)
    {
        string units = gradient.Attribute(attribute)?.Value ?? "objectBoundingBox";
        SKMatrix local = string.Equals(units, "userSpaceOnUse", StringComparison.Ordinal)
            ? SKMatrix.Identity
            : SKMatrix.CreateTranslation(bounds.Left, bounds.Top)
                .PreConcat(SKMatrix.CreateScale(bounds.Width, bounds.Height));
        if (ParseTransform(gradient.Attribute("gradientTransform")?.Value
                ?? gradient.Attribute("patternTransform")?.Value) is { } transform)
        {
            local = local.PreConcat(transform);
        }

        return local;
    }

    private static SKShader? LinearGradientShader(
        XElement gradient,
        SKRect bounds,
        SvgPaintState state,
        SvgDocument document)
    {
        List<(float Offset, SKColor Color)> stops = GradientStops(gradient, state, document);
        if (stops.Count == 0)
        {
            return null;
        }

        bool userSpace = string.Equals(
            gradient.Attribute("gradientUnits")?.Value,
            "userSpaceOnUse",
            StringComparison.Ordinal);
        float x1 = ParseNumberOrPercent(gradient.Attribute("x1")?.Value ?? "0", userSpace ? 1f : 1f) ?? 0f;
        float y1 = ParseNumberOrPercent(gradient.Attribute("y1")?.Value ?? "0", 1f) ?? 0f;
        float x2 = ParseNumberOrPercent(gradient.Attribute("x2")?.Value ?? (userSpace ? "0" : "1"), 1f) ?? 1f;
        float y2 = ParseNumberOrPercent(gradient.Attribute("y2")?.Value ?? "0", 1f) ?? 0f;
        if (x1 == x2 && y1 == y2)
        {
            return SKShader.CreateColor(stops[^1].Color);
        }

        return SKShader.CreateLinearGradient(
            new SKPoint(x1, y1),
            new SKPoint(x2, y2),
            [.. stops.Select(stop => stop.Color)],
            [.. stops.Select(stop => stop.Offset)],
            SKShaderTileMode.Clamp,
            UnitsMatrix(gradient, bounds, "gradientUnits"));
    }

    private static SKShader? RadialGradientShader(
        XElement gradient,
        SKRect bounds,
        SvgPaintState state,
        SvgDocument document)
    {
        List<(float Offset, SKColor Color)> stops = GradientStops(gradient, state, document);
        if (stops.Count == 0)
        {
            return null;
        }

        float cx = ParseNumberOrPercent(gradient.Attribute("cx")?.Value ?? "0.5", 1f) ?? 0.5f;
        float cy = ParseNumberOrPercent(gradient.Attribute("cy")?.Value ?? "0.5", 1f) ?? 0.5f;
        float r = ParseNumberOrPercent(gradient.Attribute("r")?.Value ?? "0.5", 1f) ?? 0.5f;
        if (r <= 0f)
        {
            return SKShader.CreateColor(stops[^1].Color);
        }

        return SKShader.CreateRadialGradient(
            new SKPoint(cx, cy),
            r,
            [.. stops.Select(stop => stop.Color)],
            [.. stops.Select(stop => stop.Offset)],
            SKShaderTileMode.Clamp,
            UnitsMatrix(gradient, bounds, "gradientUnits"));
    }

    private static SKShader? PatternShader(
        XElement pattern,
        SKRect bounds,
        SvgPaintState state,
        SvgDocument document,
        SvgFontDatabase fonts)
    {
        bool userSpace = string.Equals(
            pattern.Attribute("patternUnits")?.Value,
            "userSpaceOnUse",
            StringComparison.Ordinal);
        float width = ParseNumberOrPercent(pattern.Attribute("width")?.Value, userSpace ? 1f : bounds.Width) ?? 0f;
        float height = ParseNumberOrPercent(pattern.Attribute("height")?.Value, userSpace ? 1f : bounds.Height) ?? 0f;
        if (width <= 0f || height <= 0f || width > 4096f || height > 4096f)
        {
            return null;
        }

        if (state.ServerDepth >= 4)
        {
            // A pattern whose own content resolves back to a paint server would recurse
            // forever; the reference stops the chain the same way usvg's loop detection does.
            return null;
        }

        int tileWidth = Math.Max((int)MathF.Ceiling(width), 1);
        int tileHeight = Math.Max((int)MathF.Ceiling(height), 1);
        var info = new SKImageInfo(tileWidth, tileHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using SKBitmap bitmap = new(info);
        using (SKCanvas tileCanvas = new(bitmap))
        {
            tileCanvas.Clear(SKColors.Transparent);
            // Pattern content is a fresh paint context: the referencing element's own
            // fill/stroke server must not leak into it.
            SvgPaintState tileState = (state with
            {
                Fill = new RgbaColor(0, 0, 0, 255),
                FillRef = null,
                Stroke = null,
                StrokeRef = null,
                ServerDepth = state.ServerDepth + 1,
            }).Inherit(pattern);
            foreach (XElement child in pattern.Elements())
            {
                RenderElement(tileCanvas, child, tileState, document, fonts, 1);
            }
        }

        using SKImage image = SKImage.FromBitmap(bitmap);
        float x = ParseNumberOrPercent(pattern.Attribute("x")?.Value ?? "0", 1f) ?? 0f;
        float y = ParseNumberOrPercent(pattern.Attribute("y")?.Value ?? "0", 1f) ?? 0f;
        SKMatrix local = SKMatrix.CreateTranslation(x, y);
        if (ParseTransform(pattern.Attribute("patternTransform")?.Value) is { } transform)
        {
            local = local.PreConcat(transform);
        }

        return SKShader.CreateImage(
            image,
            SKShaderTileMode.Repeat,
            SKShaderTileMode.Repeat,
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
            local);
    }

    private static SKColor WithOpacity(RgbaColor color, float opacity)
    {
        float alpha = color.A / 255f * Math.Clamp(opacity, 0f, 1f);
        return new SKColor(color.R, color.G, color.B, (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255));
    }

    private static SKColor WithOpacity(SKColor color, float opacity)
    {
        float alpha = color.Alpha / 255f * Math.Clamp(opacity, 0f, 1f);
        return color.WithAlpha((byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255));
    }

    internal static SKPath? ShapePath(XElement element)
    {
        using SKPathBuilder builder = new();
        switch (element.Name.LocalName)
        {
            case "path":
            {
                string? d = element.Attribute("d")?.Value;
                if (d is null || d.Contains("var(", StringComparison.Ordinal))
                {
                    return null;
                }

                SKPath? parsed = SKPath.ParseSvgPathData(d);
                return parsed;
            }

            case "rect":
            {
                float x = ParseLength(element.Attribute("x")?.Value) ?? 0f;
                float y = ParseLength(element.Attribute("y")?.Value) ?? 0f;
                float width = ParseNumberOrPercent(element.Attribute("width")?.Value, 100f) ?? 0f;
                float height = ParseNumberOrPercent(element.Attribute("height")?.Value, 100f) ?? 0f;
                if (width <= 0f || height <= 0f)
                {
                    return null;
                }

                float rx = ParseLength(element.Attribute("rx")?.Value) ?? 0f;
                float ry = ParseLength(element.Attribute("ry")?.Value) ?? rx;
                if (rx > 0f || ry > 0f)
                {
                    builder.AddRoundRect(new SKRect(x, y, x + width, y + height), rx, ry, SKPathDirection.Clockwise);
                }
                else
                {
                    builder.AddRect(new SKRect(x, y, x + width, y + height), SKPathDirection.Clockwise);
                }

                return builder.Detach();
            }

            case "circle":
            {
                float cx = ParseLength(element.Attribute("cx")?.Value) ?? 0f;
                float cy = ParseLength(element.Attribute("cy")?.Value) ?? 0f;
                float r = ParseLength(element.Attribute("r")?.Value) ?? 0f;
                if (r <= 0f)
                {
                    return null;
                }

                builder.AddCircle(cx, cy, r, SKPathDirection.Clockwise);
                return builder.Detach();
            }

            case "ellipse":
            {
                float cx = ParseLength(element.Attribute("cx")?.Value) ?? 0f;
                float cy = ParseLength(element.Attribute("cy")?.Value) ?? 0f;
                float rx = ParseLength(element.Attribute("rx")?.Value) ?? 0f;
                float ry = ParseLength(element.Attribute("ry")?.Value) ?? 0f;
                if (rx <= 0f || ry <= 0f)
                {
                    return null;
                }

                builder.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry), SKPathDirection.Clockwise);
                return builder.Detach();
            }

            case "line":
            {
                float x1 = ParseLength(element.Attribute("x1")?.Value) ?? 0f;
                float y1 = ParseLength(element.Attribute("y1")?.Value) ?? 0f;
                float x2 = ParseLength(element.Attribute("x2")?.Value) ?? 0f;
                float y2 = ParseLength(element.Attribute("y2")?.Value) ?? 0f;
                builder.MoveTo(x1, y1);
                builder.LineTo(x2, y2);
                return builder.Detach();
            }

            case "polyline":
            case "polygon":
            {
                string? points = element.Attribute("points")?.Value;
                if (points is null)
                {
                    return null;
                }

                List<float> numbers = ParseNumberList(points);
                if (numbers.Count < 4)
                {
                    return null;
                }

                builder.MoveTo(numbers[0], numbers[1]);
                for (int index = 2; index + 1 < numbers.Count; index += 2)
                {
                    builder.LineTo(numbers[index], numbers[index + 1]);
                }

                if (element.Name.LocalName == "polygon")
                {
                    builder.Close();
                }

                return builder.Detach();
            }

            default:
                return null;
        }
    }

    internal static List<float> ParseNumberList(string value)
    {
        List<float> numbers = [];
        foreach (string token in value.Split(
            (char[])[' ', '\t', '\n', '\r', '\f', ','],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }

    internal static float? ParseLength(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.EndsWith('%'))
        {
            return null;
        }

        int split = 0;
        while (split < trimmed.Length
            && (char.IsAsciiDigit(trimmed[split]) || trimmed[split] is '+' or '-' or '.' or 'e' or 'E'))
        {
            split++;
        }

        if (!float.TryParse(
                trimmed[..split],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float number))
        {
            return null;
        }

        float factor = trimmed[split..].Trim().ToLowerInvariant() switch
        {
            "" or "px" => 1f,
            "in" => 96f,
            "cm" => 96f / 2.54f,
            "mm" => 96f / 25.4f,
            "q" => 96f / 101.6f,
            "pt" => 96f / 72f,
            "pc" => 16f,
            "em" => 16f,
            "rem" => 16f,
            _ => 1f,
        };
        return number * factor;
    }

    internal static float? ParseNumberOrPercent(string? value, float basis)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.EndsWith('%'))
        {
            return float.TryParse(
                    trimmed[..^1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float percent)
                ? percent / 100f * basis
                : null;
        }

        return ParseLength(trimmed);
    }

    internal static (float X, float Y, float Width, float Height)? ParseViewBox(string? value)
    {
        if (value is null)
        {
            return null;
        }

        List<float> numbers = ParseNumberList(value);
        return numbers.Count == 4 ? (numbers[0], numbers[1], numbers[2], numbers[3]) : null;
    }

    internal static SKMatrix? ParseTransform(string? value)
    {
        if (value is null || value.Trim().Length == 0)
        {
            return null;
        }

        SKMatrix matrix = SKMatrix.Identity;
        bool any = false;
        int cursor = 0;
        while (cursor < value.Length)
        {
            int open = value.IndexOf('(', cursor);
            if (open < 0)
            {
                break;
            }

            int close = value.IndexOf(')', open);
            if (close < 0)
            {
                break;
            }

            string name = value[cursor..open].Trim().TrimStart(',').Trim();
            List<float> args = ParseNumberList(value[(open + 1)..close]);
            SKMatrix? step = name switch
            {
                "translate" when args.Count >= 1 =>
                    SKMatrix.CreateTranslation(args[0], args.Count > 1 ? args[1] : 0f),
                "scale" when args.Count >= 1 =>
                    SKMatrix.CreateScale(args[0], args.Count > 1 ? args[1] : args[0]),
                "rotate" when args.Count >= 3 =>
                    SKMatrix.CreateRotationDegrees(args[0], args[1], args[2]),
                "rotate" when args.Count >= 1 => SKMatrix.CreateRotationDegrees(args[0]),
                "skewX" when args.Count >= 1 => SKMatrix.CreateSkew(MathF.Tan(F32.ToRadians(args[0])), 0f),
                "skewY" when args.Count >= 1 => SKMatrix.CreateSkew(0f, MathF.Tan(F32.ToRadians(args[0]))),
                "matrix" when args.Count >= 6 =>
                    new SKMatrix(args[0], args[2], args[4], args[1], args[3], args[5], 0f, 0f, 1f),
                _ => null,
            };
            if (step is { } concat)
            {
                matrix = matrix.PreConcat(concat);
                any = true;
            }

            cursor = close + 1;
        }

        return any ? matrix : null;
    }
}

/// <summary>The inherited SVG presentation state for one element.</summary>
internal sealed record SvgPaintState
{
    internal required RgbaColor? Fill { get; init; }

    internal required string? FillRef { get; init; }

    internal required RgbaColor? Stroke { get; init; }

    internal required string? StrokeRef { get; init; }

    internal required float StrokeWidth { get; init; }

    internal required float FillOpacity { get; init; }

    internal required float StrokeOpacity { get; init; }

    internal required float Opacity { get; init; }

    internal required RgbaColor CurrentColor { get; init; }

    internal required string FillRule { get; init; }

    internal required string StrokeLineCap { get; init; }

    internal required string StrokeLineJoin { get; init; }

    internal required float[]? StrokeDashArray { get; init; }

    internal required float StrokeDashOffset { get; init; }

    internal required string? FontFamily { get; init; }

    internal required float FontSize { get; init; }

    internal required int FontWeight { get; init; }

    internal required bool FontItalic { get; init; }

    internal required string TextAnchor { get; init; }

    internal required string Display { get; init; }

    internal required string Visibility { get; init; }

    internal required string? ClipPathRef { get; init; }

    /// <summary>Nesting depth through paint servers, bounding pattern self-reference.</summary>
    internal int ServerDepth { get; init; }

    internal static SvgPaintState Initial(SvgFontDatabase fonts, SvgDocument document) => new()
    {
        Fill = new RgbaColor(0, 0, 0, 255),
        FillRef = null,
        Stroke = null,
        StrokeRef = null,
        StrokeWidth = 1f,
        FillOpacity = 1f,
        StrokeOpacity = 1f,
        Opacity = 1f,
        CurrentColor = new RgbaColor(0, 0, 0, 255),
        FillRule = "nonzero",
        StrokeLineCap = "butt",
        StrokeLineJoin = "miter",
        StrokeDashArray = null,
        StrokeDashOffset = 0f,
        FontFamily = null,
        FontSize = 16f,
        FontWeight = 400,
        FontItalic = false,
        TextAnchor = "start",
        Display = "inline",
        Visibility = "visible",
        ClipPathRef = null,
    };

    /// <summary>One presentation property, preferring the inline <c>style</c> declaration.</summary>
    internal static string? Property(XElement element, string name)
    {
        if (element.Attribute("style")?.Value is { } style)
        {
            foreach (string declaration in style.Split(';'))
            {
                int colon = declaration.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                if (declaration[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    string value = declaration[(colon + 1)..].Trim();
                    if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
                    {
                        value = value[..^"!important".Length].Trim();
                    }

                    return value;
                }
            }
        }

        return element.Attributes().FirstOrDefault(a =>
            string.Equals(a.Name.LocalName, name, StringComparison.Ordinal))?.Value;
    }

    internal static RgbaColor? ResolveColor(string value, RgbaColor currentColor)
    {
        string trimmed = value.Trim();
        if (trimmed.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            return currentColor;
        }

        return Css.CssColor.Parse(trimmed);
    }

    internal SvgPaintState Inherit(XElement element)
    {
        RgbaColor currentColor = CurrentColor;
        if (Property(element, "color") is { } colorValue
            && ResolveColor(colorValue, CurrentColor) is { } parsedColor)
        {
            currentColor = parsedColor;
        }

        RgbaColor? fill = Fill;
        string? fillRef = FillRef;
        if (Property(element, "fill") is { } fillValue)
        {
            (fill, fillRef) = ParsePaint(fillValue, currentColor, Fill, FillRef);
        }

        RgbaColor? stroke = Stroke;
        string? strokeRef = StrokeRef;
        if (Property(element, "stroke") is { } strokeValue)
        {
            (stroke, strokeRef) = ParsePaint(strokeValue, currentColor, Stroke, StrokeRef);
        }

        float strokeWidth = StrokeWidth;
        if (Property(element, "stroke-width") is { } strokeWidthValue
            && SvgRenderer.ParseNumberOrPercent(strokeWidthValue, 1f) is { } parsedStrokeWidth)
        {
            strokeWidth = parsedStrokeWidth;
        }

        float fontSize = FontSize;
        if (Property(element, "font-size") is { } fontSizeValue
            && SvgRenderer.ParseLength(fontSizeValue) is { } parsedFontSize && parsedFontSize > 0f)
        {
            fontSize = parsedFontSize;
        }

        int fontWeight = FontWeight;
        if (Property(element, "font-weight") is { } fontWeightValue)
        {
            fontWeight = fontWeightValue.Trim().ToLowerInvariant() switch
            {
                "bold" or "bolder" => 700,
                "normal" or "lighter" => 400,
                var text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    => parsed,
                _ => fontWeight,
            };
        }

        float[]? dashArray = StrokeDashArray;
        if (Property(element, "stroke-dasharray") is { } dashValue)
        {
            dashArray = dashValue.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                ? null
                : [.. SvgRenderer.ParseNumberList(dashValue)];
            if (dashArray is { Length: 0 })
            {
                dashArray = null;
            }
        }

        return this with
        {
            CurrentColor = currentColor,
            Fill = fill,
            FillRef = fillRef,
            Stroke = stroke,
            StrokeRef = strokeRef,
            StrokeWidth = strokeWidth,
            FillOpacity = Number(element, "fill-opacity", FillOpacity),

            // `opacity` is a group property: it does not inherit, it composites.
            Opacity = Property(element, "opacity") is { } opacityValue
                ? Math.Clamp(SvgRenderer.ParseNumberOrPercent(opacityValue, 1f) ?? 1f, 0f, 1f)
                : 1f,
            StrokeOpacity = Number(element, "stroke-opacity", StrokeOpacity),
            FillRule = Property(element, "fill-rule")?.Trim().ToLowerInvariant() ?? FillRule,
            StrokeLineCap = Property(element, "stroke-linecap")?.Trim().ToLowerInvariant() ?? StrokeLineCap,
            StrokeLineJoin = Property(element, "stroke-linejoin")?.Trim().ToLowerInvariant() ?? StrokeLineJoin,
            StrokeDashArray = dashArray,
            StrokeDashOffset = Number(element, "stroke-dashoffset", StrokeDashOffset),
            FontFamily = Property(element, "font-family") ?? FontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            FontItalic = Property(element, "font-style")?.Trim().ToLowerInvariant() is { } style
                ? style is "italic" or "oblique"
                : FontItalic,
            TextAnchor = Property(element, "text-anchor")?.Trim().ToLowerInvariant() ?? TextAnchor,
            Display = Property(element, "display")?.Trim().ToLowerInvariant() ?? "inline",
            Visibility = Property(element, "visibility")?.Trim().ToLowerInvariant() ?? Visibility,
            ClipPathRef = Property(element, "clip-path") is { } clipValue ? ReferenceId(clipValue) : null,
        };

        static float Number(XElement element, string name, float fallback) =>
            Property(element, name) is { } value
                ? Math.Clamp(SvgRenderer.ParseNumberOrPercent(value, 1f) ?? fallback, 0f, 1f)
                : fallback;
    }

    private static (RgbaColor? Color, string? Reference) ParsePaint(
        string value,
        RgbaColor currentColor,
        RgbaColor? inheritedColor,
        string? inheritedReference)
    {
        string trimmed = value.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (trimmed.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            return (inheritedColor, inheritedReference);
        }

        if (ReferenceId(trimmed) is { } reference)
        {
            return (null, reference);
        }

        RgbaColor? parsed = ResolveColor(trimmed, currentColor);
        return parsed is { } color ? (color, null) : (inheritedColor, inheritedReference);
    }

    private static string? ReferenceId(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int close = trimmed.IndexOf(')');
        if (close < 0)
        {
            return null;
        }

        string inner = trimmed[4..close].Trim().Trim('"', '\'');
        return inner.StartsWith('#') ? inner[1..] : null;
    }
}
