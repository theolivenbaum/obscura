// Port of `collect_web_fonts` and the @font-face descriptor parsing of
// crates/obscura-render/src/paint.rs.
using System.Globalization;
using PocketCalculator.Dom;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render;

internal static class PaintFonts
{
    private sealed class FontRule
    {
        internal required List<(string Key, string Source)> Sources { get; init; }

        internal required string? Family { get; init; }

        internal required (ushort Min, ushort Max)? Weight { get; init; }

        internal required bool? Italic { get; init; }
    }

    /// <summary>
    /// Fetch the Latin/ASCII face from each authored <c>@font-face</c> rule and decode
    /// WOFF/WOFF2 into the sfnt bytes the shaping database consumes.
    /// </summary>
    internal static List<WebFont> CollectWebFonts(
        DomTree tree,
        string? baseUrl,
        RenderResourceCache cache,
        IReadOnlyList<DynamicFontFace> dynamicFonts)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<WebFont> fonts = [];
        List<FontRule> rules = [];

        // Critical web fonts are normally preloaded from the document with a URL already
        // resolved relative to the HTML; collected in the same walk as the sheets.
        List<string> preloads = [];
        foreach (NodeId nid in DomTraversal.RenderedDescendants(tree, tree.Document))
        {
            if (tree.GetNode(nid) is not { } node || node.AsElement() is not { } element)
            {
                continue;
            }

            if (string.Equals(element.Name.Local, "link", StringComparison.Ordinal))
            {
                string rel = node.GetAttribute("rel") ?? string.Empty;
                string asValue = node.GetAttribute("as") ?? string.Empty;
                bool isPreload = rel.Split((char[])[' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(token => token.Equals("preload", StringComparison.OrdinalIgnoreCase));
                if (isPreload && asValue.Equals("font", StringComparison.OrdinalIgnoreCase)
                    && node.GetAttribute("href") is { } href)
                {
                    preloads.Add(href);
                }
            }

            // A @font-face reaches here from a <style>'s own text, or from the CSS an element
            // contributes ahead of it - a fetched <link> sheet or an @import. DEVIATION from
            // crates/obscura-render, which reads only <style> text because the Rust browser
            // materializes a fetched <link> sheet as a <style> element; Chromium 141 creates no
            // element for one. Without this arm every @font-face in a linked sheet disappears
            // and the face silently falls back. See "Known deviations" in todo.md.
            bool isStyle = string.Equals(element.Name.Local, "style", StringComparison.Ordinal);
            string? external = tree.ExternalStylesheetCss(nid);
            if (!isStyle && external is null)
            {
                continue;
            }

            string css = external ?? string.Empty;
            if (isStyle)
            {
                string own = tree.TextContent(nid);
                css = css.Length == 0 ? own : css + "\n" + own;
            }

            foreach (FontFaceDescriptors face in FontFacesOf(css))
            {
                rules.Add(new FontRule
                {
                    Sources = [.. face.Sources.Select(src => (FontResourceKey(src, baseUrl), src))],
                    Family = face.Family,
                    Weight = face.Weight,
                    Italic = face.Italic,
                });
            }
        }

        foreach (DynamicFontFace face in dynamicFonts)
        {
            string descriptorBlock = string.Create(
                CultureInfo.InvariantCulture,
                $"src:{face.Source};font-weight:{face.Weight};font-style:{face.Style};unicode-range:{face.UnicodeRange}");
            if (!FontFaceCoversAscii(descriptorBlock))
            {
                continue;
            }

            List<(string Key, string Source)> sources =
            [
                .. FontFaceUrls(descriptorBlock)
                    .Where(FontSourceMayBeSupported)
                    .Select(src => (FontResourceKey(src, baseUrl), src)),
            ];
            if (sources.Count == 0)
            {
                continue;
            }

            rules.Add(new FontRule
            {
                Sources = sources,
                Family = face.Family.Length > 0 ? face.Family : null,
                Weight = FontFaceWeight(descriptorBlock),
                Italic = FontFaceItalic(descriptorBlock),
            });
        }

        foreach (string src in preloads.Take(16))
        {
            string key = FontResourceKey(src, baseUrl);
            if (!seen.Add(key))
            {
                continue;
            }

            byte[]? decoded = FetchAndDecodeFont(src, baseUrl, cache);
            if (decoded is null)
            {
                continue;
            }

            FontRule? metadata = rules.FirstOrDefault(rule =>
                rule.Sources.Any(source => string.Equals(source.Key, key, StringComparison.Ordinal)));
            fonts.Add(new WebFont
            {
                Data = decoded,
                Family = metadata?.Family,
                Weight = metadata?.Weight,
                Italic = metadata?.Italic,
            });
        }

        foreach (FontRule rule in rules)
        {
            if (fonts.Count >= 16)
            {
                break;
            }

            foreach ((string key, string src) in rule.Sources)
            {
                if (!seen.Add(key))
                {
                    continue;
                }

                byte[]? decoded = FetchAndDecodeFont(src, baseUrl, cache);
                if (decoded is not null)
                {
                    fonts.Add(new WebFont
                    {
                        Data = decoded,
                        Family = rule.Family,
                        Weight = rule.Weight,
                        Italic = rule.Italic,
                    });
                    break;
                }
            }
        }

        return fonts;
    }

    /// <summary>The descriptors of one <c>@font-face</c> rule that covers ASCII and has a usable source.</summary>
    private sealed record FontFaceDescriptors(
        List<string> Sources,
        string? Family,
        (ushort Min, ushort Max)? Weight,
        bool? Italic);

    /// <summary>
    /// The usable <c>@font-face</c> rules of each style sheet text, by the identity of the
    /// string: an unchanged <c>&lt;style&gt;</c> or fetched sheet hands every pass the same
    /// instance, and its rules are a function of its text alone.
    /// </summary>
    /// <remarks>
    /// Not in crates/obscura-render, which scans every sheet's text again on every pass. On
    /// nvidia.com that was a lower-cased copy of 2 MB of CSS per forced read.
    /// </remarks>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, List<FontFaceDescriptors>> FontFaceMemo = new();

    private static List<FontFaceDescriptors> FontFacesOf(string css)
    {
        if (css.Length == 0)
        {
            return [];
        }

        if (FontFaceMemo.TryGetValue(css, out List<FontFaceDescriptors>? memoized))
        {
            return memoized;
        }

        List<FontFaceDescriptors> faces = [];
        foreach (string face in FontFaceBlocks(css))
        {
            if (!FontFaceCoversAscii(face))
            {
                continue;
            }

            List<string> sources = [.. FontFaceUrls(face).Where(FontSourceMayBeSupported)];
            if (sources.Count == 0)
            {
                continue;
            }

            faces.Add(new FontFaceDescriptors(sources, FontFaceFamily(face), FontFaceWeight(face), FontFaceItalic(face)));
        }

        FontFaceMemo.AddOrUpdate(css, faces);
        return faces;
    }

    /// <summary>Exclude source formats the font database cannot consume before requesting.</summary>
    internal static bool FontSourceMayBeSupported(string src)
    {
        int cut = src.IndexOfAny(['?', '#']);
        string path = (cut < 0 ? src : src[..cut]).ToLowerInvariant();
        return !path.EndsWith(".eot", StringComparison.Ordinal)
            && !path.EndsWith(".svg", StringComparison.Ordinal);
    }

    internal static string FontResourceKey(string src, string? baseUrl)
    {
        if (Uri.TryCreate(src, UriKind.Absolute, out Uri? absolute))
        {
            return absolute.AbsoluteUri;
        }

        if (baseUrl is not null
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
            && Uri.TryCreate(baseUri, src, out Uri? joined))
        {
            return joined.AbsoluteUri;
        }

        return src;
    }

    internal static byte[]? FetchAndDecodeFont(string src, string? baseUrl, RenderResourceCache cache)
    {
        byte[]? compressed = PaintResources.FetchFontBytes(src, baseUrl, cache);
        if (compressed is null || compressed.Length > 8 * 1024 * 1024 || compressed.Length < 4)
        {
            return null;
        }

        // See RenderResourceCache.TryGetDecodedFont for why the port memoizes a decode
        // that the Rust reference repeats.
        string cacheKey = FontResourceKey(src, baseUrl);
        if (cache.TryGetDecodedFont(cacheKey, compressed, out byte[]? memoized))
        {
            return memoized;
        }

        byte[]? decoded;
        if (compressed[0] == 'w' && compressed[1] == 'O' && compressed[2] == 'F'
            && (compressed[3] == '2' || compressed[3] == 'F'))
        {
            decoded = Woff.TryDecode(compressed, out byte[]? sfnt) ? sfnt : null;
        }
        else if ((compressed[0] == 0 && compressed[1] == 1 && compressed[2] == 0 && compressed[3] == 0)
            || (compressed[0] == 'O' && compressed[1] == 'T' && compressed[2] == 'T' && compressed[3] == 'O')
            || (compressed[0] == 't' && compressed[1] == 't' && compressed[2] == 'c' && compressed[3] == 'f')
            || (compressed[0] == 't' && compressed[1] == 'r' && compressed[2] == 'u' && compressed[3] == 'e'))
        {
            // TrueType/OpenType collections and raw sfnt fonts already have the representation
            // the font database expects.
            decoded = compressed;
        }
        else
        {
            decoded = null;
        }

        if (decoded is null || decoded.Length > 32 * 1024 * 1024)
        {
            return null;
        }

        cache.StoreDecodedFont(cacheKey, compressed, decoded);
        return decoded;
    }

    internal static List<string> FontFaceBlocks(string css)
    {
        string lower = css.ToLowerInvariant();
        List<string> output = [];
        int cursor = 0;
        while (true)
        {
            int at = lower.IndexOf("@font-face", cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            int open = lower.IndexOf('{', at);
            if (open < 0)
            {
                break;
            }

            int depth = 1;
            char? quote = null;
            bool escaped = false;
            int? close = null;
            for (int index = open + 1; index < css.Length; index++)
            {
                char ch = css[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (quote is { } active)
                {
                    if (ch == active)
                    {
                        quote = null;
                    }

                    continue;
                }

                if (ch is '"' or '\'')
                {
                    quote = ch;
                    continue;
                }

                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = index;
                        break;
                    }
                }
            }

            if (close is not { } end)
            {
                break;
            }

            output.Add(css[(open + 1)..end]);
            cursor = end + 1;
        }

        return output;
    }

    internal static string? FontFaceDeclaration(string face, string name)
    {
        string? result = null;
        foreach (string declaration in SplitCssTopLevel(face, ';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            if (declaration[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                result = declaration[(colon + 1)..].Trim();
            }
        }

        return result;
    }

    internal static string? FontFaceFamily(string face)
    {
        string? family = FontFaceDeclaration(face, "font-family")?.Trim().Trim('"', '\'');
        return string.IsNullOrEmpty(family) ? null : family;
    }

    internal static (ushort Min, ushort Max)? FontFaceWeight(string face)
    {
        static ushort? Parse(string value)
        {
            string lower = value.ToLowerInvariant();
            if (lower == "normal")
            {
                return 400;
            }

            if (lower == "bold")
            {
                return 700;
            }

            return float.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out float weight)
                && float.IsFinite(weight) && weight >= 1f && weight <= 1000f
                ? (ushort)F32.Round(weight)
                : null;
        }

        string? declaration = FontFaceDeclaration(face, "font-weight");
        if (declaration is null)
        {
            return null;
        }

        List<ushort> values = [];
        foreach (string token in declaration.Split(
            (char[])[' ', '\t', '\n', '\r', '\f'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (Parse(token) is { } parsed)
            {
                values.Add(parsed);
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        ushort first = values[0];
        ushort second = values.Count > 1 ? values[1] : first;
        return (Math.Min(first, second), Math.Max(first, second));
    }

    internal static bool? FontFaceItalic(string face)
    {
        string? style = FontFaceDeclaration(face, "font-style")?.Trim().ToLowerInvariant();
        if (style is null)
        {
            return null;
        }

        if (style == "normal")
        {
            return false;
        }

        return style == "italic" || style.StartsWith("oblique", StringComparison.Ordinal) ? true : null;
    }

    internal static bool FontFaceCoversAscii(string face)
    {
        string? range = FontFaceDeclaration(face, "unicode-range");
        if (range is null)
        {
            return true;
        }

        foreach (string part in range.Split(','))
        {
            string token = part.Trim().ToLowerInvariant();
            if (!token.StartsWith("u+", StringComparison.Ordinal))
            {
                continue;
            }

            string value = token[2..];
            uint? start;
            uint? end;
            if (value.Contains('?', StringComparison.Ordinal))
            {
                start = TryHex(value.Replace('?', '0'));
                end = TryHex(value.Replace('?', 'f'));
            }
            else if (value.IndexOf('-') is int dash && dash >= 0)
            {
                start = TryHex(value[..dash]);
                end = TryHex(value[(dash + 1)..]);
            }
            else
            {
                start = TryHex(value);
                end = start;
            }

            if (start is { } s && end is { } e && s <= 0x7e && e >= 0x20)
            {
                return true;
            }
        }

        return false;

        static uint? TryHex(string value) =>
            uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)
                ? parsed
                : null;
    }

    internal static List<string> FontFaceUrls(string face)
    {
        string? src = FontFaceDeclaration(face, "src");
        if (src is null)
        {
            return [];
        }

        string lower = src.ToLowerInvariant();
        List<string> output = [];
        int cursor = 0;
        while (true)
        {
            int relative = lower.IndexOf("url(", cursor, StringComparison.Ordinal);
            if (relative < 0)
            {
                break;
            }

            int start = relative + 4;
            int end = src.IndexOf(')', start);
            if (end < 0)
            {
                break;
            }

            string value = src[start..end].Trim().Trim('"', '\'').Trim();
            if (value.Length > 0)
            {
                output.Add(value);
            }

            cursor = end + 1;
        }

        return output;
    }

    internal static List<string> SplitCssTopLevel(string value, char separator)
    {
        List<string> output = [];
        int start = 0;
        int depth = 0;
        char? quote = null;
        bool escaped = false;
        for (int index = 0; index < value.Length; index++)
        {
            char ch = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote is { } active)
            {
                if (ch == active)
                {
                    quote = null;
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth = Math.Max(depth - 1, 0);
            }
            else if (ch == separator && depth == 0)
            {
                output.Add(value[start..index]);
                start = index + 1;
            }
        }

        output.Add(value[start..]);
        return output;
    }
}
