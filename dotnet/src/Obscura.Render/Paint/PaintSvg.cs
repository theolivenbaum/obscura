// Port of the SVG serialization, sprite injection, presentation-attribute resolution, and
// intrinsic-metadata helpers of crates/obscura-render/src/paint.rs.
using System.Globalization;
using System.Text;
using Obscura.Dom;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal static class PaintSvg
{
    /// <summary>Sniff SVG content: an XML/SVG prolog, or a bare <c>&lt;svg</c> root tag.</summary>
    internal static bool IsSvg(byte[] bytes)
    {
        int length = Math.Min(bytes.Length, 256);
        string text = Encoding.UTF8.GetString(bytes, 0, length);
        string trimmed = text.TrimStart('﻿').TrimStart();
        return trimmed.StartsWith("<?xml", StringComparison.Ordinal)
            || trimmed.StartsWith("<svg", StringComparison.Ordinal);
    }

    /// <summary>The intrinsic size of an SVG image from its size/<c>viewBox</c>.</summary>
    internal static (float Width, float Height)? SvgIntrinsic(byte[] bytes)
    {
        SvgDocument? document = SvgDocument.Parse(bytes);
        if (document is null)
        {
            return null;
        }

        (float Width, float Height)? size = document.DefaultSize();
        return size is { } value && value.Width > 0f && value.Height > 0f ? value : null;
    }

    /// <summary>
    /// Read intrinsic SVG dimensions without treating <c>viewBox</c> user-space coordinates as
    /// CSS-pixel dimensions.
    /// </summary>
    internal static ReplacedIntrinsic? SvgImageIntrinsicMetadata(byte[] bytes)
    {
        // The lightweight attribute pass below preserves missing/percentage axes. It must not,
        // however, turn a malformed XML prefix that merely resembles an SVG root into
        // successful image metadata: use the same parser as paint as the validity gate first.
        if (SvgDocument.Parse(bytes) is null)
        {
            return null;
        }

        string source;
        try
        {
            source = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }

        string remaining = source.TrimStart('﻿').TrimStart();
        string tail;
        while (true)
        {
            if (remaining.StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = remaining.IndexOf("-->", StringComparison.Ordinal);
                if (end < 0)
                {
                    return null;
                }

                remaining = remaining[(end + 3)..].TrimStart();
                continue;
            }

            if (remaining.StartsWith("<?", StringComparison.Ordinal))
            {
                int end = remaining.IndexOf("?>", StringComparison.Ordinal);
                if (end < 0)
                {
                    return null;
                }

                remaining = remaining[(end + 2)..].TrimStart();
                continue;
            }

            if (remaining.StartsWith("<!", StringComparison.Ordinal))
            {
                // Skip a validated DOCTYPE/declaration, including an internal subset.
                char? quote = null;
                int subsetDepth = 0;
                int? found = null;
                for (int index = 0; index < remaining.Length; index++)
                {
                    char ch = remaining[index];
                    if (quote is { } open)
                    {
                        if (ch == open)
                        {
                            quote = null;
                        }

                        continue;
                    }

                    if (ch is '\'' or '"')
                    {
                        quote = ch;
                    }
                    else if (ch == '[')
                    {
                        subsetDepth++;
                    }
                    else if (ch == ']')
                    {
                        subsetDepth = Math.Max(subsetDepth - 1, 0);
                    }
                    else if (ch == '>' && subsetDepth == 0)
                    {
                        found = index;
                        break;
                    }
                }

                if (found is not { } declarationEnd)
                {
                    return null;
                }

                remaining = remaining[(declarationEnd + 1)..].TrimStart();
                continue;
            }

            if (!remaining.StartsWith('<'))
            {
                return null;
            }

            string afterOpen = remaining[1..];
            int nameEnd = afterOpen.AsSpan().IndexOfAny([' ', '\t', '\n', '\r', '\f', '/', '>']);
            if (nameEnd < 0)
            {
                nameEnd = afterOpen.Length;
            }

            string rootName = afterOpen[..nameEnd];
            int colon = rootName.LastIndexOf(':');
            string local = colon >= 0 ? rootName[(colon + 1)..] : rootName;
            if (!string.Equals(local, "svg", StringComparison.Ordinal))
            {
                return null;
            }

            tail = afterOpen[nameEnd..];
            break;
        }

        char? attributeQuote = null;
        int? tagEnd = null;
        for (int index = 0; index < tail.Length; index++)
        {
            char ch = tail[index];
            if (attributeQuote is { } open)
            {
                if (ch == open)
                {
                    attributeQuote = null;
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                attributeQuote = ch;
            }
            else if (ch == '>')
            {
                tagEnd = index;
                break;
            }
        }

        if (tagEnd is not { } close)
        {
            return null;
        }

        string attributes = tail[..close];
        string? Attribute(string name) => RootAttribute(attributes, name);

        float? Length(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0
                || trimmed.EndsWith('%')
                || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
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

            float? factor = trimmed[split..].Trim().ToLowerInvariant() switch
            {
                "" or "px" => 1f,
                "in" => 96f,
                "cm" => 96f / 2.54f,
                "mm" => 96f / 25.4f,
                "q" => 96f / 101.6f,
                "pt" => 96f / 72f,
                "pc" => 16f,
                _ => null,
            };
            if (factor is not { } scale)
            {
                return null;
            }

            float result = number * scale;
            return float.IsFinite(result) && result > 0f ? result : null;
        }

        float? width = Attribute("width") is { } widthValue ? Length(widthValue) : null;
        float? height = Attribute("height") is { } heightValue ? Length(heightValue) : null;
        float? viewBoxRatio = null;
        if (Attribute("viewBox") is { } viewBox)
        {
            string[] parts = viewBox.Split(
                (char[])[' ', '\t', '\n', '\r', '\f', ','],
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4)
            {
                float[] values = new float[4];
                bool ok = true;
                for (int index = 0; index < 4; index++)
                {
                    if (!float.TryParse(
                            parts[index],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out values[index]))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok && float.IsFinite(values[2]) && float.IsFinite(values[3])
                    && values[2] > 0f && values[3] > 0f)
                {
                    viewBoxRatio = values[2] / values[3];
                }
            }
        }

        float? ratio = width is { } w && height is { } h ? w / h : viewBoxRatio;
        return new ReplacedIntrinsic(width, height, ratio);
    }

    private static string? RootAttribute(string attributes, string name)
    {
        int cursor = 0;
        while (cursor < attributes.Length)
        {
            while (cursor < attributes.Length
                && (char.IsWhiteSpace(attributes[cursor]) || attributes[cursor] == '/'))
            {
                cursor++;
            }

            int nameStart = cursor;
            while (cursor < attributes.Length
                && !char.IsWhiteSpace(attributes[cursor])
                && attributes[cursor] != '=')
            {
                cursor++;
            }

            string found = attributes[nameStart..cursor];
            while (cursor < attributes.Length && char.IsWhiteSpace(attributes[cursor]))
            {
                cursor++;
            }

            if (cursor >= attributes.Length || attributes[cursor] != '=')
            {
                if (nameStart == cursor)
                {
                    cursor++;
                }

                continue;
            }

            cursor++;
            while (cursor < attributes.Length && char.IsWhiteSpace(attributes[cursor]))
            {
                cursor++;
            }

            if (cursor >= attributes.Length || (attributes[cursor] != '\'' && attributes[cursor] != '"'))
            {
                continue;
            }

            char quote = attributes[cursor];
            cursor++;
            int valueStart = cursor;
            while (cursor < attributes.Length && attributes[cursor] != quote)
            {
                cursor++;
            }

            string value = attributes[valueStart..Math.Min(cursor, attributes.Length)];
            cursor = Math.Min(cursor + 1, attributes.Length);
            if (string.Equals(found, name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    internal static bool HasInlineSvgText(DomTree tree) =>
        DomTraversal.RenderedDescendants(tree, tree.Document).Any(nid =>
            tree.GetNode(nid)?.AsElement() is { } element
            && element.Name.Local is "text" or "tspan" or "textPath");

    /// <summary>Return SVG XML whose root <c>width</c>/<c>height</c> are the CSS viewport.</summary>
    internal static byte[]? SvgWithRootViewport(byte[] bytes, uint width, uint height)
    {
        string source;
        try
        {
            source = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }

        int start = source.IndexOf("<svg", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        string tail = source[start..];
        char? quote = null;
        int? tagEnd = null;
        for (int index = 0; index < tail.Length; index++)
        {
            char ch = tail[index];
            if (quote is { } open)
            {
                if (ch == open)
                {
                    quote = null;
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                tagEnd = start + index;
                break;
            }
        }

        if (tagEnd is not { } end)
        {
            return null;
        }

        StringBuilder root = new(source[start..(end + 1)]);
        foreach ((string name, uint value) in (( string, uint)[])[("width", width), ("height", height)])
        {
            string rootText = root.ToString();
            if (SvgRootAttrValueRange(rootText, name) is { } range)
            {
                root.Remove(range.Start, range.End - range.Start);
                root.Insert(range.Start, value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                root.Insert(
                    root.Length - 1,
                    string.Create(CultureInfo.InvariantCulture, $" {name}=\"{value}\""));
            }
        }

        StringBuilder output = new(source.Length + 32);
        output.Append(source, 0, start);
        output.Append(root);
        output.Append(source, end + 1, source.Length - end - 1);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// <summary>Value range for one attribute in an <c>&lt;svg ...&gt;</c> start tag.</summary>
    internal static (int Start, int End)? SvgRootAttrValueRange(string tag, string wanted)
    {
        int index = "<svg".Length;
        while (index < tag.Length)
        {
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            if (index >= tag.Length || tag[index] == '>' || tag[index] == '/')
            {
                return null;
            }

            int nameStart = index;
            while (index < tag.Length
                && !char.IsWhiteSpace(tag[index])
                && tag[index] != '='
                && tag[index] != '>'
                && tag[index] != '/')
            {
                index++;
            }

            int nameEnd = index;
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            if (index >= tag.Length || tag[index] != '=')
            {
                continue;
            }

            index++;
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            int valueStart;
            int valueEnd;
            if (index < tag.Length && (tag[index] == '"' || tag[index] == '\''))
            {
                char delimiter = tag[index];
                index++;
                valueStart = index;
                while (index < tag.Length && tag[index] != delimiter)
                {
                    index++;
                }

                valueEnd = index;
                index = Math.Min(index + 1, tag.Length);
            }
            else
            {
                valueStart = index;
                while (index < tag.Length
                    && !char.IsWhiteSpace(tag[index])
                    && tag[index] != '>'
                    && tag[index] != '/')
                {
                    index++;
                }

                valueEnd = index;
            }

            if (string.Equals(tag[nameStart..nameEnd], wanted, StringComparison.Ordinal))
            {
                return (valueStart, valueEnd);
            }
        }

        return null;
    }

    /// <summary>Serialize an inline <c>&lt;svg&gt;</c> subtree to a standalone SVG document.</summary>
    internal static string SerializeSvg(DomTree tree, NodeId root)
    {
        StringBuilder buffer = new();
        SerializeSvgNode(tree, root, true, null, null, null, buffer);
        return buffer.ToString();
    }

    /// <summary>Serialize an inline SVG while carrying the page's computed author styling.</summary>
    internal static string SerializeSvgStyled(
        DomTree tree,
        NodeId root,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IReadOnlyDictionary<NodeId, IReadOnlyDictionary<string, string>> customProperties,
        NodeId? suppressOpacityFor)
    {
        StringBuilder buffer = new();
        SerializeSvgNode(tree, root, true, styles, customProperties, suppressOpacityFor, buffer);
        return buffer.ToString();
    }

    internal static string InjectSvgCurrentColor(string markup, RgbaColor color)
    {
        int start = markup.IndexOf("<svg", StringComparison.Ordinal);
        if (start < 0)
        {
            return markup;
        }

        int end = markup.IndexOf('>', start);
        if (end < 0)
        {
            return markup;
        }

        string root = markup[start..end];

        // An explicit presentation attribute already survives serialization.
        if (root.Contains(" color=", StringComparison.Ordinal))
        {
            return markup;
        }

        string attribute = string.Create(
            CultureInfo.InvariantCulture,
            $" color=\"#{color.R:x2}{color.G:x2}{color.B:x2}\"");
        return markup.Insert(start + "<svg".Length, attribute);
    }

    private static void SerializeSvgNode(
        DomTree tree,
        NodeId nid,
        bool isRoot,
        IReadOnlyDictionary<NodeId, LayoutStyle>? styles,
        IReadOnlyDictionary<NodeId, IReadOnlyDictionary<string, string>>? customProperties,
        NodeId? suppressOpacityFor,
        StringBuilder buffer)
    {
        Node? node = tree.GetNode(nid);
        if (node is null)
        {
            return;
        }

        if (node.TextContentOfTextNode is { } text)
        {
            SvgEscapeText(text, buffer);
            return;
        }

        if (node.AsElement() is not { } element)
        {
            // Document/comment/PI: no tag of its own, emit only element children.
            foreach (NodeId child in tree.Children(nid))
            {
                SerializeSvgNode(tree, child, false, styles, customProperties, suppressOpacityFor, buffer);
            }

            return;
        }

        string tag = element.Name.Local;
        buffer.Append('<').Append(tag);
        bool hasXmlns = false;
        string? sourceStyle = null;
        if (node.Attrs is { } attrs)
        {
            foreach (Obscura.Dom.Attribute attribute in attrs)
            {
                // Emit the local name only, dropping any prefix (`xlink:href` -> `href`).
                string name = attribute.Name.Local;

                // HTML frameworks commonly stamp hydration attributes onto inline SVG; our
                // standalone XML serialization has no matching namespace declaration.
                if (name.Contains(':', StringComparison.Ordinal)
                    || attribute.Name.Prefix is not null)
                {
                    continue;
                }

                if (string.Equals(name, "xmlns", StringComparison.Ordinal))
                {
                    hasXmlns = true;
                }

                if (styles is not null && string.Equals(name, "style", StringComparison.Ordinal))
                {
                    sourceStyle = attribute.Value;
                    continue;
                }

                string value;
                if (styles is not null && SvgCssPresentationAttribute(name))
                {
                    IReadOnlyDictionary<string, string> properties =
                        customProperties is not null
                        && customProperties.TryGetValue(nid, out IReadOnlyDictionary<string, string>? owned)
                            ? owned
                            : EmptyProperties;
                    if (ResolveSvgPresentationValue(name, attribute.Value, properties) is not { } resolved)
                    {
                        // A var() failure makes the declaration invalid at computed value time.
                        continue;
                    }

                    value = resolved;
                }
                else
                {
                    value = attribute.Value;
                }

                buffer.Append(' ').Append(name).Append("=\"");
                SvgEscapeAttr(value, buffer);
                buffer.Append('"');
            }
        }

        if (styles is not null)
        {
            StringBuilder declarations = new();
            if (sourceStyle is { } source)
            {
                declarations.Append(source.Trim());
                if (declarations.Length > 0 && declarations[^1] != ';')
                {
                    declarations.Append(';');
                }
            }

            void Append(string name, string value)
            {
                if (value.Trim().Length == 0)
                {
                    return;
                }

                declarations.Append(name).Append(':').Append(value).Append("!important;");
            }

            if (styles.TryGetValue(nid, out LayoutStyle? computed))
            {
                IReadOnlyDictionary<string, string> properties =
                    customProperties is not null
                    && customProperties.TryGetValue(nid, out IReadOnlyDictionary<string, string>? owned)
                        ? owned
                        : EmptyProperties;
                if (computed.SvgFill is { } fillValue
                    && ResolveSvgPresentationValue("fill", fillValue, properties) is { } fill)
                {
                    Append("fill", fill.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)
                        ? "currentColor"
                        : fill);
                }

                if (computed.SvgStroke is { } strokeValue
                    && ResolveSvgPresentationValue("stroke", strokeValue, properties) is { } stroke)
                {
                    Append("stroke", stroke.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)
                        ? "currentColor"
                        : stroke);
                }

                if (computed.SvgStrokeWidth is { } strokeWidthValue
                    && ResolveSvgPresentationValue("stroke-width", strokeWidthValue, properties)
                        is { } strokeWidth)
                {
                    Append("stroke-width", strokeWidth);
                }

                if (tag is "svg" or "text" or "textPath" or "textpath" or "tspan")
                {
                    if (computed.FontSize is { } fontSize)
                    {
                        Append("font-size", string.Create(CultureInfo.InvariantCulture, $"{fontSize}px"));
                    }

                    if (computed.FontWeight is { } fontWeight)
                    {
                        Append("font-weight", fontWeight);
                    }

                    if (computed.FontFamily is { } fontFamily)
                    {
                        Append("font-family", fontFamily);
                    }

                    if (computed.FontStyleItalic == true)
                    {
                        Append("font-style", "italic");
                    }
                }

                if (computed.Color is { } color)
                {
                    Append(
                        "color",
                        string.Create(CultureInfo.InvariantCulture, $"#{color.R:x2}{color.G:x2}{color.B:x2}"));
                }

                if (suppressOpacityFor == nid)
                {
                    // The HTML paint layer applies this SVG root's opacity after rasterization.
                    Append("opacity", "1");
                }
                else if (computed.Opacity is { } opacity)
                {
                    Append("opacity", PaintCssValues.CssNumber(opacity));
                }
            }

            if (declarations.Length > 0)
            {
                buffer.Append(" style=\"");
                SvgEscapeAttr(declarations.ToString(), buffer);
                buffer.Append('"');
            }
        }

        if (isRoot && !hasXmlns)
        {
            buffer.Append(" xmlns=\"http://www.w3.org/2000/svg\"");
        }

        buffer.Append('>');
        foreach (NodeId child in tree.Children(nid))
        {
            SerializeSvgNode(tree, child, false, styles, customProperties, suppressOpacityFor, buffer);
        }

        buffer.Append("</").Append(tag).Append('>');
    }

    private static readonly Dictionary<string, string> EmptyProperties = new(StringComparer.Ordinal);

    /// <summary>SVG attributes which participate in the CSS cascade.</summary>
    internal static bool SvgCssPresentationAttribute(string name) => name is
        "alignment-baseline" or "baseline-shift" or "buffered-rendering" or "clip" or "clip-path"
        or "clip-rule" or "color" or "color-interpolation" or "color-interpolation-filters"
        or "color-rendering" or "cursor" or "direction" or "display" or "dominant-baseline"
        or "fill" or "fill-opacity" or "fill-rule" or "filter" or "flood-color"
        or "flood-opacity" or "font-family" or "font-size" or "font-stretch" or "font-style"
        or "font-variant" or "font-weight" or "image-rendering" or "letter-spacing"
        or "lighting-color" or "marker-end" or "marker-mid" or "marker-start" or "mask"
        or "mask-type" or "opacity" or "overflow" or "paint-order" or "pointer-events"
        or "shape-rendering" or "stop-color" or "stop-opacity" or "stroke" or "stroke-dasharray"
        or "stroke-dashoffset" or "stroke-linecap" or "stroke-linejoin" or "stroke-miterlimit"
        or "stroke-opacity" or "stroke-width" or "text-anchor" or "text-decoration"
        or "text-rendering" or "transform-origin" or "unicode-bidi" or "vector-effect"
        or "visibility" or "word-spacing" or "writing-mode"
        or "x" or "y" or "cx" or "cy" or "r" or "rx" or "ry" or "width" or "height";

    internal static string? ResolveSvgPresentationValue(
        string name,
        string value,
        IReadOnlyDictionary<string, string> properties)
    {
        if (!value.Contains("var(", StringComparison.Ordinal))
        {
            return value;
        }

        string? resolved = Css.CssVariables.SubstituteVarValue(value, properties, 0);
        if (resolved is null
            || resolved.Trim().Length == 0
            || SvgPresentationSubstitutionIsGuaranteedInvalid(name, resolved))
        {
            return null;
        }

        return resolved;
    }

    /// <summary>Detect only values whose post-substitution grammar is unambiguously invalid.</summary>
    internal static bool SvgPresentationSubstitutionIsGuaranteedInvalid(string name, string value)
    {
        if (name is not ("color" or "fill" or "flood-color" or "lighting-color"
            or "stop-color" or "stroke"))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0
            || !trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')
            || Css.CssColor.Parse(trimmed) is not null)
        {
            return false;
        }

        string lower = trimmed.ToLowerInvariant();
        if (name is "fill" or "stroke" && lower is "none" or "context-fill" or "context-stroke")
        {
            return false;
        }

        return lower is not ("currentcolor" or "inherit" or "initial" or "unset" or "revert"
            or "revert-layer" or "accentcolor" or "accentcolortext" or "activetext"
            or "buttonborder" or "buttonface" or "buttontext" or "canvas" or "canvastext"
            or "field" or "fieldtext" or "graytext" or "highlight" or "highlighttext"
            or "linktext" or "mark" or "marktext" or "selecteditem" or "selecteditemtext"
            or "visitedtext");
    }

    internal static void SvgEscapeText(string s, StringBuilder buffer)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '&':
                    buffer.Append("&amp;");
                    break;
                case '<':
                    buffer.Append("&lt;");
                    break;
                case '>':
                    buffer.Append("&gt;");
                    break;
                default:
                    buffer.Append(c);
                    break;
            }
        }
    }

    internal static void SvgEscapeAttr(string s, StringBuilder buffer)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '&':
                    buffer.Append("&amp;");
                    break;
                case '<':
                    buffer.Append("&lt;");
                    break;
                case '"':
                    buffer.Append("&quot;");
                    break;
                default:
                    buffer.Append(c);
                    break;
            }
        }
    }

    internal static string SvgEscapeAttrStr(string s)
    {
        StringBuilder buffer = new();
        SvgEscapeAttr(s, buffer);
        return buffer.ToString();
    }

    /// <summary>
    /// Resolve <c>&lt;use&gt;</c> elements against a document-level or external sprite,
    /// splicing the referenced symbol into the standalone SVG handed to the rasterizer.
    /// </summary>
    internal static string InjectExternalSprites(
        DomTree tree,
        NodeId root,
        IReadOnlyDictionary<NodeId, LayoutStyle>? styles,
        IReadOnlyDictionary<NodeId, IReadOnlyDictionary<string, string>>? customProperties,
        string? baseUrl,
        string markup,
        RenderResourceCache cache,
        Dictionary<string, string?> spriteCache)
    {
        // Distinct external references (full href, url, fragment id), in first-seen order.
        List<NodeId> rootDescendants = tree.Descendants(root);
        List<(string Href, string Url, string Fragment)> refs = [];
        List<string> localFragments = [];
        foreach (NodeId nid in rootDescendants)
        {
            Node? node = tree.GetNode(nid);
            if (node?.AsElement() is not { } element
                || !string.Equals(element.Name.Local, "use", StringComparison.Ordinal))
            {
                continue;
            }

            string? href = node.GetAttribute("href") ?? node.GetAttribute("xlink:href");
            if (href is null)
            {
                continue;
            }

            int hash = href.IndexOf('#');
            if (hash < 0)
            {
                continue;
            }

            string url = href[..hash];
            string fragment = href[(hash + 1)..];
            if (fragment.Length == 0)
            {
                continue;
            }

            if (url.Length == 0)
            {
                if (!localFragments.Contains(fragment, StringComparer.Ordinal))
                {
                    localFragments.Add(fragment);
                }

                continue;
            }

            (string, string, string) entry = (href, url, fragment);
            if (!refs.Contains(entry))
            {
                refs.Add(entry);
            }
        }

        StringBuilder defs = new();
        List<(string From, string To)> rewrites = [];
        HashSet<string> wantedLocal = new(localFragments, StringComparer.Ordinal);
        Dictionary<string, NodeId> localNodes = new(StringComparer.Ordinal);
        if (wantedLocal.Count > 0)
        {
            foreach (NodeId nid in tree.Descendants(tree.Document))
            {
                if (tree.GetNode(nid)?.GetAttribute("id") is not { } id)
                {
                    continue;
                }

                if (wantedLocal.Contains(id))
                {
                    localNodes.TryAdd(id, nid);
                }
            }
        }

        foreach (string fragment in localFragments)
        {
            if (!localNodes.TryGetValue(fragment, out NodeId symbolId))
            {
                continue;
            }

            if (symbolId == root || rootDescendants.Contains(symbolId))
            {
                continue;
            }

            SerializeSvgNode(tree, symbolId, false, styles, customProperties, null, defs);
        }

        foreach ((string href, string url, string fragment) in refs)
        {
            string key = url + "#" + fragment;
            if (!spriteCache.TryGetValue(key, out string? symbol))
            {
                byte[]? bytes = PaintResources.FetchBytes(url, baseUrl, cache);
                symbol = bytes is null
                    ? null
                    : ExtractSvgElementById(Encoding.UTF8.GetString(bytes), fragment)
                        ?.Replace("xlink:href", "href", StringComparison.Ordinal);
                spriteCache[key] = symbol;
            }

            if (symbol is null)
            {
                continue;
            }

            IReadOnlyDictionary<string, string> properties =
                customProperties is not null
                && customProperties.TryGetValue(root, out IReadOnlyDictionary<string, string>? owned)
                    ? owned
                    : EmptyProperties;
            defs.Append(ResolveSvgMarkupPresentationVars(symbol, properties));
            rewrites.Add((href, "#" + fragment));
        }

        if (defs.Length == 0)
        {
            return markup;
        }

        // Splice the fetched symbols into a `<defs>` immediately after the opening `<svg ...>`.
        int gt = markup.IndexOf('>');
        if (gt >= 0)
        {
            markup = markup.Insert(gt + 1, "<defs>" + defs + "</defs>");
        }

        // Point each external `<use>` at the injected local symbol.
        foreach ((string href, string local) in rewrites)
        {
            string from = "href=\"" + SvgEscapeAttrStr(href) + "\"";
            string to = "href=\"" + SvgEscapeAttrStr(local) + "\"";
            markup = markup.Replace(from, to, StringComparison.Ordinal);
        }

        return markup;
    }

    /// <summary>
    /// Resolve CSS-variable presentation attributes in fetched sprite markup against the
    /// referencing SVG's inherited custom properties.
    /// </summary>
    internal static string ResolveSvgMarkupPresentationVars(
        string markup,
        IReadOnlyDictionary<string, string> properties)
    {
        if (!markup.Contains("var(", StringComparison.Ordinal))
        {
            return markup;
        }

        StringBuilder output = new(markup.Length);
        int cursor = 0;
        while (true)
        {
            int start = markup.IndexOf('<', cursor);
            if (start < 0)
            {
                break;
            }

            output.Append(markup, cursor, start - cursor);
            if (SvgMarkupTagEnd(markup, start) is not { } end)
            {
                output.Append(markup, start, markup.Length - start);
                return output.ToString();
            }

            output.Append(ResolveSvgTagPresentationVars(markup[start..(end + 1)], properties));
            cursor = end + 1;
        }

        output.Append(markup, cursor, markup.Length - cursor);
        return output.ToString();
    }

    private static int? SvgMarkupTagEnd(string markup, int start)
    {
        char? quote = null;
        for (int cursor = start + 1; cursor < markup.Length; cursor++)
        {
            char ch = markup[cursor];
            if (quote is { } active)
            {
                if (ch == active)
                {
                    quote = null;
                }
            }
            else if (ch is '\'' or '"')
            {
                quote = ch;
            }
            else if (ch == '>')
            {
                return cursor;
            }
        }

        return null;
    }

    private static string ResolveSvgTagPresentationVars(
        string tag,
        IReadOnlyDictionary<string, string> properties)
    {
        if (tag.Length < 3 || tag[0] != '<' || tag[1] is '/' or '!' or '?')
        {
            return tag;
        }

        int cursor = 1;
        while (cursor < tag.Length && !char.IsWhiteSpace(tag[cursor]) && tag[cursor] is not ('/' or '>'))
        {
            cursor++;
        }

        List<(int Start, int End, string Replacement)> replacements = [];
        while (cursor < tag.Length)
        {
            int attributeStart = cursor;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length || tag[cursor] is '/' or '>')
            {
                break;
            }

            int nameStart = cursor;
            while (cursor < tag.Length
                && !char.IsWhiteSpace(tag[cursor])
                && tag[cursor] is not ('=' or '/' or '>'))
            {
                cursor++;
            }

            int nameEnd = cursor;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length || tag[cursor] != '=')
            {
                continue;
            }

            cursor++;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length)
            {
                break;
            }

            char? quote = tag[cursor] is '\'' or '"' ? tag[cursor] : null;
            if (quote is not null)
            {
                cursor++;
            }

            int valueStart = cursor;
            while (cursor < tag.Length
                && (quote is { } q
                    ? tag[cursor] != q
                    : !char.IsWhiteSpace(tag[cursor]) && tag[cursor] is not ('/' or '>')))
            {
                cursor++;
            }

            int valueEnd = cursor;
            if (quote is not null && cursor < tag.Length)
            {
                cursor++;
            }

            string name = tag[nameStart..nameEnd];
            string value = tag[valueStart..valueEnd];
            if (SvgCssPresentationAttribute(name) && value.Contains("var(", StringComparison.Ordinal))
            {
                string? resolved = ResolveSvgPresentationValue(name, value, properties);
                if (resolved is null)
                {
                    replacements.Add((attributeStart, cursor, string.Empty));
                }
                else if (!string.Equals(resolved, value, StringComparison.Ordinal))
                {
                    replacements.Add((valueStart, valueEnd, resolved));
                }
            }
        }

        if (replacements.Count == 0)
        {
            return tag;
        }

        StringBuilder resolvedTag = new(tag);
        for (int index = replacements.Count - 1; index >= 0; index--)
        {
            (int start, int end, string replacement) = replacements[index];
            resolvedTag.Remove(start, end - start);
            resolvedTag.Insert(start, replacement);
        }

        return resolvedTag.ToString();
    }

    /// <summary>Pull the element carrying <c>id="id"</c> out of an external sprite document.</summary>
    internal static string? ExtractSvgElementById(string sprite, string id)
    {
        int i = 0;
        while (i < sprite.Length)
        {
            ReadOnlySpan<char> rest = sprite.AsSpan(i);
            if (rest[0] != '<')
            {
                int next = rest.IndexOf('<');
                if (next < 0)
                {
                    return null;
                }

                i += next;
                continue;
            }

            if (rest.StartsWith("<!--"))
            {
                int end = rest.IndexOf("-->".AsSpan());
                if (end < 0)
                {
                    return null;
                }

                i += end + 3;
                continue;
            }

            if (rest.StartsWith("<![CDATA["))
            {
                int end = rest.IndexOf("]]>".AsSpan());
                if (end < 0)
                {
                    return null;
                }

                i += end + 3;
                continue;
            }

            if (rest.StartsWith("<!") || rest.StartsWith("<?") || rest.StartsWith("</"))
            {
                int end = rest.IndexOf('>');
                if (end < 0)
                {
                    return null;
                }

                i += end + 1;
                continue;
            }

            int close = rest.IndexOf('>');
            if (close < 0)
            {
                return null;
            }

            int gt = i + close;
            string inner = sprite[(i + 1)..gt];
            if (string.Equals(TagAttr(inner, "id"), id, StringComparison.Ordinal))
            {
                if (inner.TrimEnd().EndsWith('/'))
                {
                    return sprite[i..(gt + 1)];
                }

                string name = TagName(inner);
                if (ElementEnd(sprite, gt + 1, name) is not { } end)
                {
                    return null;
                }

                return sprite[i..end];
            }

            i = gt + 1;
        }

        return null;
    }

    /// <summary>The tag name from a tag's inner text.</summary>
    internal static string TagName(string inner)
    {
        string trimmed = inner.TrimStart().TrimStart('/');
        int end = trimmed.AsSpan().IndexOfAny([' ', '\t', '\n', '\r', '\f', '/']);
        return end < 0 ? trimmed : trimmed[..end];
    }

    /// <summary>The value of attribute <paramref name="want"/> in a tag's inner text.</summary>
    internal static string? TagAttr(string inner, string want)
    {
        int i = 0;

        // Skip the tag name.
        while (i < inner.Length && !char.IsWhiteSpace(inner[i]))
        {
            i++;
        }

        while (i < inner.Length)
        {
            while (i < inner.Length && char.IsWhiteSpace(inner[i]))
            {
                i++;
            }

            if (i >= inner.Length || inner[i] == '/')
            {
                break;
            }

            int nameStart = i;
            while (i < inner.Length && inner[i] != '=' && !char.IsWhiteSpace(inner[i]) && inner[i] != '/')
            {
                i++;
            }

            string name = inner[nameStart..i];
            while (i < inner.Length && char.IsWhiteSpace(inner[i]))
            {
                i++;
            }

            if (i < inner.Length && inner[i] == '=')
            {
                i++;
                while (i < inner.Length && char.IsWhiteSpace(inner[i]))
                {
                    i++;
                }

                string value;
                if (i < inner.Length && (inner[i] == '"' || inner[i] == '\''))
                {
                    char quote = inner[i];
                    i++;
                    int valueStart = i;
                    while (i < inner.Length && inner[i] != quote)
                    {
                        i++;
                    }

                    value = inner[valueStart..Math.Min(i, inner.Length)];
                    if (i < inner.Length)
                    {
                        i++;
                    }
                }
                else
                {
                    int valueStart = i;
                    while (i < inner.Length && !char.IsWhiteSpace(inner[i]) && inner[i] != '/')
                    {
                        i++;
                    }

                    value = inner[valueStart..i];
                }

                if (string.Equals(name, want, StringComparison.Ordinal))
                {
                    return value;
                }
            }
            else if (string.Equals(name, want, StringComparison.Ordinal))
            {
                // Valueless (boolean) attribute.
                return string.Empty;
            }
        }

        return null;
    }

    /// <summary>The byte offset just past the <c>&lt;/name&gt;</c> that closes an element.</summary>
    internal static int? ElementEnd(string sprite, int start, string name)
    {
        int i = start;
        int depth = 1;
        while (i < sprite.Length)
        {
            ReadOnlySpan<char> rest = sprite.AsSpan(i);
            if (rest[0] != '<')
            {
                int next = rest.IndexOf('<');
                if (next < 0)
                {
                    return null;
                }

                i += next;
                continue;
            }

            if (rest.StartsWith("<!--"))
            {
                int end = rest.IndexOf("-->".AsSpan());
                if (end < 0)
                {
                    return null;
                }

                i += end + 3;
                continue;
            }

            if (rest.StartsWith("<![CDATA["))
            {
                int end = rest.IndexOf("]]>".AsSpan());
                if (end < 0)
                {
                    return null;
                }

                i += end + 3;
                continue;
            }

            if (rest.StartsWith("<!") || rest.StartsWith("<?"))
            {
                int end = rest.IndexOf('>');
                if (end < 0)
                {
                    return null;
                }

                i += end + 1;
                continue;
            }

            int close = rest.IndexOf('>');
            if (close < 0)
            {
                return null;
            }

            int gt = i + close;
            string inner = sprite[(i + 1)..gt];
            if (rest.StartsWith("</"))
            {
                if (string.Equals(TagName(inner), name, StringComparison.Ordinal))
                {
                    depth--;
                    if (depth == 0)
                    {
                        return gt + 1;
                    }
                }
            }
            else if (string.Equals(TagName(inner), name, StringComparison.Ordinal)
                && !inner.TrimEnd().EndsWith('/'))
            {
                depth++;
            }

            i = gt + 1;
        }

        return null;
    }
}
