// No counterpart in crates/obscura-render: the reference hands every inline <svg> to resvg,
// which rasterizes it as an opaque image, so the Rust engine has no per-element SVG geometry
// either. CSSOM View needs it - Chromium answers getBoundingClientRect() on an SVG shape with
// the element's object bounding box mapped through its CTM - so the port computes it here.
// See "Known deviations" in todo.md.
using PocketCalculator.Dom;
using SkiaSharp;

namespace PocketCalculator.Render;

/// <summary>
/// Object bounding boxes for the descendants of an inline <c>&lt;svg&gt;</c>, in document
/// coordinates. An <c>&lt;svg&gt;</c> is an atomic replaced box, so its children never become
/// taffy nodes and <see cref="DomLayout.Rects"/> has nothing for them; this pass walks the SVG
/// rendering tree instead and resolves each element's user-space box through the viewport and
/// <c>transform</c> chain.
/// </summary>
internal static class SvgBoxes
{
    /// <summary>Matches the raster walk's recursion guard in <c>SvgRenderer</c>.</summary>
    private const int MAX_DEPTH = 24;

    /// <summary>
    /// Elements outside the SVG rendering model. Neither they nor their descendants have a box,
    /// which is what makes Chromium answer <c>[0, 0, 0, 0]</c> for a <c>&lt;defs&gt;</c> or the
    /// <c>&lt;rect&gt;</c> inside a <c>&lt;clipPath&gt;</c>.
    /// </summary>
    private static bool IsNonRendered(string tag) =>
        tag is "defs" or "symbol" or "clipPath" or "mask" or "pattern" or "marker"
            or "linearGradient" or "radialGradient" or "filter" or "style" or "script"
            or "title" or "desc" or "metadata";

    internal static void Measure(
        DomTree                                  tree,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        IReadOnlyDictionary<NodeId, Rect>        rects,
        Dictionary<NodeId, Rect>                 output)
    {
        foreach ((NodeId id, Rect rect) in rects)
        {
            if (rect.Width <= 0f && rect.Height <= 0f)
            {
                continue;
            }

            if (tree.GetNode(id) is not { } node || node.AsElement() is not { } element)
            {
                continue;
            }

            if (!IsSvg(element.Name) || !string.Equals(element.Name.Local, "svg", StringComparison.Ordinal))
            {
                continue;
            }

            // A nested <svg> is reached through its outermost one, which supplies the viewport
            // its coordinates are relative to.
            if (tree.GetNode(id)?.Parent is { } parent
                && tree.GetNode(parent)?.AsElement() is { } parentElement
                && IsSvg(parentElement.Name))
            {
                continue;
            }

            styles.TryGetValue(id, out LayoutStyle? style);
            Rect content = ContentBox(rect, style);
            MeasureViewport(
                tree,
                styles,
                id,
                SKMatrix.CreateTranslation(content.X, content.Y),
                content.Width,
                content.Height,
                output,
                0);
        }
    }

    private static bool IsSvg(QualName name) =>
        string.Equals(name.Ns, Namespaces.Svg, StringComparison.Ordinal);

    /// <summary>The SVG viewport an <c>&lt;svg&gt;</c> establishes: its CSS content box.</summary>
    private static Rect ContentBox(Rect border, LayoutStyle? style)
    {
        if (style is null)
        {
            return border;
        }

        float left = style.Border.Left + style.Padding.Left;
        float top = style.Border.Top + style.Padding.Top;
        float right = style.Border.Right + style.Padding.Right;
        float bottom = style.Border.Bottom + style.Padding.Bottom;

        return new Rect(
            border.X + left,
            border.Y + top,
            F32.Max(border.Width - left - right, 0f),
            F32.Max(border.Height - top - bottom, 0f));
    }

    /// <summary>
    /// Walk the children of an <c>&lt;svg&gt;</c> under the viewport it establishes.
    /// <paramref name="viewportCtm"/> maps the viewport's own top-left-origin space to document
    /// coordinates, and <paramref name="width"/> / <paramref name="height"/> are its size in
    /// that space - which is what a <c>viewBox</c> is fitted into.
    /// </summary>
    private static void MeasureViewport(
        DomTree                                  tree,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        NodeId                                   svgId,
        SKMatrix                                 viewportCtm,
        float                                    width,
        float                                    height,
        Dictionary<NodeId, Rect>                 output,
        int                                      depth)
    {
        SKMatrix ctm = viewportCtm;
        Node? node = tree.GetNode(svgId);
        string? viewBox = node?.GetAttribute("viewBox") ?? node?.GetAttribute("viewbox");
        if (SvgRenderer.ParseViewBox(viewBox) is { } parsed && parsed.Width > 0f && parsed.Height > 0f)
        {
            ctm = ctm.PreConcat(SvgRenderer.ViewBoxMatrix(
                parsed,
                width,
                height,
                node?.GetAttribute("preserveAspectRatio")));
        }

        foreach (NodeId child in DomTraversal.ElementChildren(tree, svgId))
        {
            Visit(tree, styles, child, ctm, output, depth + 1);
        }
    }

    /// <summary>
    /// Measure one element. Returns its bounding box in its <em>parent's</em> user space (its own
    /// box already mapped through its <c>transform</c>), which is the coordinate system a
    /// container unions its children in.
    /// </summary>
    private static SKRect? Visit(
        DomTree                                  tree,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        NodeId                                   id,
        SKMatrix                                 parentCtm,
        Dictionary<NodeId, Rect>                 output,
        int                                      depth)
    {
        if (depth > MAX_DEPTH
            || tree.GetNode(id) is not { } node
            || node.AsElement() is not { } element
            || !IsSvg(element.Name))
        {
            return null;
        }

        string tag = element.Name.Local;
        if (IsNonRendered(tag))
        {
            return null;
        }

        styles.TryGetValue(id, out LayoutStyle? style);
        if (style?.Display == Display.None)
        {
            return null;
        }

        SKMatrix own = parentCtm;
        SKMatrix? transform = SvgRenderer.ParseTransform(node.GetAttribute("transform"));
        if (transform is { } local)
        {
            own = parentCtm.PreConcat(local);
        }

        SKRect? box = tag switch
        {
            "g" or "a" or "switch" => Container(tree, styles, id, own, output, depth),
            "svg" => NestedViewport(tree, styles, id, own, output, depth),
            "text" => TextBox(tree, id, node, style),
            "image" or "foreignObject" => ViewportBox(node),
            _ => ShapeBox(node, tag),
        };

        if (box is not { } measured)
        {
            return null;
        }

        SKRect screen = own.MapRect(measured);
        output[id] = new Rect(screen.Left, screen.Top, screen.Width, screen.Height);

        return transform is { } applied ? applied.MapRect(measured) : measured;
    }

    /// <summary>
    /// A container's box is the union of its children's, in the container's own user space. An
    /// empty container keeps a zero box at that space's origin, which is what Chromium reports.
    /// </summary>
    private static SKRect Container(
        DomTree                                  tree,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        NodeId                                   id,
        SKMatrix                                 ctm,
        Dictionary<NodeId, Rect>                 output,
        int                                      depth)
    {
        SKRect union = SKRect.Empty;
        bool any = false;
        foreach (NodeId child in DomTraversal.ElementChildren(tree, id))
        {
            if (Visit(tree, styles, child, ctm, output, depth + 1) is not { } childBox)
            {
                continue;
            }

            union = any ? SKRect.Union(union, childBox) : childBox;
            any = true;
        }

        return union;
    }

    /// <summary>
    /// A nested <c>&lt;svg&gt;</c> contributes its own viewport rectangle and re-bases its
    /// children on it.
    /// </summary>
    private static SKRect? NestedViewport(
        DomTree                                  tree,
        IReadOnlyDictionary<NodeId, LayoutStyle> styles,
        NodeId                                   id,
        SKMatrix                                 ctm,
        Dictionary<NodeId, Rect>                 output,
        int                                      depth)
    {
        if (ViewportBox(tree.GetNode(id)) is not { } viewport)
        {
            return null;
        }

        // `x`/`y` move the nested viewport's origin inside the parent's user space; the scale
        // the parent's own viewBox imposes is already in `ctm` and has to stay there.
        MeasureViewport(
            tree,
            styles,
            id,
            ctm.PreConcat(SKMatrix.CreateTranslation(viewport.Left, viewport.Top)),
            viewport.Width,
            viewport.Height,
            output,
            depth);

        return viewport;
    }

    /// <summary>The <c>x</c>/<c>y</c>/<c>width</c>/<c>height</c> rectangle of a viewport-ish element.</summary>
    private static SKRect? ViewportBox(Node? node)
    {
        if (node is null)
        {
            return null;
        }

        float x = SvgRenderer.ParseLength(node.GetAttribute("x")) ?? 0f;
        float y = SvgRenderer.ParseLength(node.GetAttribute("y")) ?? 0f;
        float width = SvgRenderer.ParseLength(node.GetAttribute("width")) ?? 0f;
        float height = SvgRenderer.ParseLength(node.GetAttribute("height")) ?? 0f;

        return width > 0f && height > 0f ? new SKRect(x, y, x + width, y + height) : null;
    }

    /// <summary>
    /// A shape's object bounding box: the tight bounds of its geometry, with no stroke. Chromium
    /// reports a horizontal <c>&lt;line&gt;</c> as zero-height however thick its stroke is.
    /// </summary>
    private static SKRect? ShapeBox(Node node, string tag)
    {
        using SKPath? path = SvgRenderer.ShapePath(tag, name => node.GetAttribute(name));

        return path is null ? null : path.ComputeTightBounds();
    }

    /// <summary>
    /// A single-run approximation of an SVG text chunk's box, mirroring how the raster places it:
    /// the anchored advance horizontally, and the font's rounded ascent/descent vertically.
    /// </summary>
    private static SKRect? TextBox(DomTree tree, NodeId id, Node node, LayoutStyle? style)
    {
        System.Text.StringBuilder buffer = new();
        AppendTextContent(tree, id, buffer, 0);
        string content = buffer.ToString();
        if (content.Trim().Length == 0)
        {
            return null;
        }

        float x = SvgRenderer.ParseLength(node.GetAttribute("x")) ?? 0f;
        float y = SvgRenderer.ParseLength(node.GetAttribute("y")) ?? 0f;
        float size = style?.FontSize ?? 16f;
        if (size <= 0f)
        {
            return null;
        }

        bool bold = style?.FontWeight is { } weight
            && (weight is "bold" or "bolder"
                || (int.TryParse(weight, out int numeric) && numeric >= 600));
        SKTypeface typeface = SvgFontDatabase.Shared.Resolve(
            style?.FontFamilySpecified ?? style?.FontFamily,
            bold,
            style?.FontStyleItalic == true);
        using SKFont font = new(typeface, size)
        {
            Subpixel = true,
            Edging   = SKFontEdging.Antialias,
            Hinting  = SKFontHinting.None,
        };
        float advance = font.MeasureText(content);
        SKFontMetrics metrics = font.Metrics;

        // Chromium normalizes a face's ascent and descent to whole pixels before it builds the
        // text fragment, so an 11px Liberation Sans run is 10 above the baseline and 2 below.
        float ascent = F32.Round(-metrics.Ascent);
        float descent = F32.Round(metrics.Descent);
        float originX = (style?.SvgPaint?.TextAnchor ?? style?.SvgTextAnchor) switch
        {
            "middle" => x - (advance / 2f),
            "end"    => x - advance,
            _        => x,
        };

        return new SKRect(originX, y - ascent, originX + advance, y + descent);
    }

    /// <summary>The rendered character data of a text element, skipping non-rendered children.</summary>
    private static void AppendTextContent(DomTree tree, NodeId id, System.Text.StringBuilder buffer, int depth)
    {
        if (!StackGuard.CanDescend())
        {
            return;
        }

        if (depth > MAX_DEPTH || tree.GetNode(id) is not { } node)
        {
            return;
        }

        if (node.TextContentOfTextNode is { } text)
        {
            buffer.Append(text);
            return;
        }

        if (node.AsElement() is { } element && IsNonRendered(element.Name.Local))
        {
            return;
        }

        foreach (NodeId child in tree.Children(id))
        {
            AppendTextContent(tree, child, buffer, depth + 1);
        }
    }
}
