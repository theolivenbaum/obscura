using System.Globalization;
using PocketCalculator.Dom;
using SkiaSharp;
using RgbaColor = PocketCalculator.Render.Css.RgbaColor;

namespace PocketCalculator.Render;

/// <summary>A list item's marker: a symbol Blink draws as a shape, or text.</summary>
internal readonly record struct ListMarker(ListStyle Kind, string? Text)
{
    internal bool IsSymbol => Kind is ListStyle.Disc or ListStyle.Circle or ListStyle.Square;
}

/// <summary>
/// List-item markers placed and drawn as Chromium 141 does (Blink's <c>ListMarker</c>).
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-render/src/paint.rs, which draws every marker as text ("•",
/// "◦", "▪", "1.") 6px left of the item's padding edge, at the top of its content box, with the
/// static face, and ignores <c>list-style-position</c>. Measured against Chromium 141 by the ink
/// of the markers at 16, 32 and 64px (`render-repros/list-markers.html`):
/// <list type="bullet">
/// <item>disc, circle and square are shapes, not glyphs: a square of side
/// <c>(a*2/3 + 1)/2</c> for the font's rounded ascent <c>a</c> (integer arithmetic), 1px into a
/// box <c>(a*2/3+1)/2 + 2</c> wide whose top is the baseline less the ascent, at
/// <c>3*(a - a*2/3)/2</c> below it; a disc is a filled ellipse, a circle a 1px stroke, a square
/// a filled rect (<c>RelativeSymbolMarkerRect</c>, <c>PaintSymbol</c>);</item>
/// <item>outside, the symbol box's inline-start margin is <c>-(a*2/3) - 8</c> from the content
/// edge (<c>InlineMarginsForOutside</c>), on the right in a right-to-left item; text ("1. ",
/// its suffix space included) ends at the content edge;</item>
/// <item>inside, the marker is the first thing on the first line, which it indents: a symbol
/// takes its box less 1px plus 1em, text its own width (<c>InlineMarginsForInside</c>);</item>
/// <item>the marker sits on the first line's baseline, not at the content box's top, and text
/// is shaped in the item's font with its direction.</item>
/// </list>
/// </remarks>
internal static class ListMarkers
{
    /// <summary>The marker <paramref name="li"/> draws, or <c>null</c> for none.</summary>
    internal static ListMarker? For(DomTree tree, NodeId li, LayoutStyle style)
    {
        // Only a list-item box draws a marker, and a `::marker { content }` string replaces
        // it (an empty one removes it), as in Chromium 141.
        if (!DomTraversal.IsLocal(tree, li, "li") || style.Display == Display.None || !style.ListItemDisplay)
        {
            return null;
        }

        if (style.MarkerText is { } markerText)
        {
            return markerText.Length > 0 ? new ListMarker(ListStyle.Decimal, markerText) : null;
        }

        return style.ListStyle switch
        {
            ListStyle.Disc or ListStyle.Circle or ListStyle.Square => new ListMarker(style.ListStyle.Value, null),
            ListStyle.Decimal => new ListMarker(ListStyle.Decimal, Ordinal(tree, li).ToString(CultureInfo.InvariantCulture) + ". "),
            _ => null,
        };
    }

    /// <summary>
    /// The item's number: its position among the <c>li</c> siblings before it, counted from the
    /// list's <c>start</c>, or from the last <c>value</c> before it.
    /// </summary>
    internal static int Ordinal(DomTree tree, NodeId li)
    {
        int steps = 0;
        NodeId? current = li;
        while (current is { } id)
        {
            if (tree.GetNode(id) is { } node && node.AsElement() is { } element
                && string.Equals(element.Name.Local, "li", StringComparison.Ordinal))
            {
                if (node.GetAttribute("value") is { } value
                    && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int explicitValue))
                {
                    return explicitValue + steps;
                }

                steps++;
            }

            current = tree.GetNode(id)?.PrevSibling;
        }

        int start = 1;
        if (tree.GetNode(li)?.Parent is { } parent
            && tree.GetNode(parent) is { } list
            && list.GetAttribute("start") is { } startValue
            && int.TryParse(startValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            start = parsed;
        }

        return start + steps - 1;
    }

    private static int Ascent(TextEngine engine, LayoutStyle style) =>
        (int)F32.Round(engine.InlineFontBoxMetrics(style).Ascent);

    /// <summary>The marker's style: the item's font, without its text indent.</summary>
    private static LayoutStyle MarkerStyle(LayoutStyle style)
    {
        if (style.TextIndent is null)
        {
            return style;
        }

        LayoutStyle copy = style.Clone();
        copy.TextIndent = null;
        return copy;
    }

    /// <summary>How far an inside marker indents the first line.</summary>
    internal static float InsideAdvance(TextEngine engine, ListMarker marker, LayoutStyle style)
    {
        if (marker.IsSymbol)
        {
            int ascent = Ascent(engine, style);
            int bullet = ((ascent * 2 / 3) + 1) / 2;
            return bullet + 2 - 1 + (style.FontSize ?? 16f);
        }

        return marker.Text is { } text ? engine.MeasureMarkerText(text, MarkerStyle(style)) : 0f;
    }

    /// <summary>
    /// Indent a list item's own first line by its inside marker; called by the box build for
    /// the context that holds the item's first line.
    /// </summary>
    internal static void ApplyInsideMarker(TextEngine engine, DomTree tree, NodeId li, LayoutStyle style, int item)
    {
        if (style.ListStyleInside == true && For(tree, li, style) is { } marker)
        {
            engine.SetMarkerIndent(item, InsideAdvance(engine, marker, style));
        }
    }

    /// <summary>The context holding the item's first line: its own, or its first block's.</summary>
    private static (int Item, bool Own)? FirstLineItem(DomTree tree, DomLayout laid, NodeId li)
    {
        NodeId current = li;
        for (int depth = 0; depth < 8; depth++)
        {
            if (laid.IfcItems.TryGetValue(current, out int whole) && whole >= 0)
            {
                return (whole, current == li);
            }

            if (laid.RunIfcItems.TryGetValue(current, out List<int>? runs) && runs.Count > 0)
            {
                return (runs[0], current == li);
            }

            NodeId? next = null;
            foreach (NodeId child in DomTraversal.RenderedChildren(tree, current))
            {
                if (tree.GetNode(child) is not { } node)
                {
                    continue;
                }

                if (node.IsText)
                {
                    if (tree.TextContent(child).AsSpan().Trim().Length != 0)
                    {
                        return null;
                    }

                    continue;
                }

                if (!laid.Styles.TryGetValue(child, out LayoutStyle? childStyle)
                    || childStyle.Display == Display.None
                    || childStyle.Position == Layout.Position.Absolute
                    || childStyle.Float is not null)
                {
                    continue;
                }

                if (childStyle.Display == Display.Inline)
                {
                    return null;
                }

                next = child;
                break;
            }

            if (next is not { } block)
            {
                return null;
            }

            current = block;
        }

        return null;
    }

    /// <summary>
    /// Paint <paramref name="li"/>'s marker. <paramref name="rect"/> is its border box and
    /// (<paramref name="ox"/>, <paramref name="oy"/>) the translation from document to surface
    /// coordinates.
    /// </summary>
    internal static void Paint(
        Pixmap pixmap,
        DomTree tree,
        DomLayout laid,
        NodeId li,
        LayoutStyle style,
        in Rect rect,
        float ox,
        float oy,
        Rect? clip,
        Mask? clipMask,
        float rasterScale,
        bool printEconomy)
    {
        if (For(tree, li, style) is not { } marker)
        {
            return;
        }

        TextEngine engine = laid.TextEngine;
        Edges border = style.UsedBorder;
        float contentLeft = rect.X + border.Left + style.Padding.Left;
        float contentRight = rect.X + rect.Width - border.Right - style.Padding.Right;
        float contentTop = rect.Y + border.Top + style.Padding.Top;
        (float ascent, float descent) = engine.InlineFontBoxMetrics(style);
        bool rtl = style.Direction == Layout.Direction.Rtl;

        float baseline;
        Rect? firstLine = null;
        bool own = false;
        if (FirstLineItem(tree, laid, li) is { } first && engine.FirstLineBaseline(first.Item) is { } itemBaseline)
        {
            baseline = itemBaseline + oy;
            own = first.Own;
            List<Rect> lines = [];
            engine.AppendLineContentRects(first.Item, lines);
            if (lines.Count > 0)
            {
                firstLine = lines[0] with { X = lines[0].X + ox, Y = lines[0].Y + oy };
            }
        }
        else
        {
            float lineHeight = FontResolution.UsedLineHeight(style);
            baseline = contentTop + MathF.Floor((lineHeight - (ascent + descent)) / 2f) + ascent;
        }

        bool inside = style.ListStyleInside == true;
        float advance = inside ? InsideAdvance(engine, marker, style) : 0f;

        // The inside marker's slot: before the first line's text when the indent made room
        // for it, at the content edge otherwise.
        float slotLeft;
        float slotRight;
        if (inside && own && firstLine is { } line)
        {
            slotLeft = rtl ? line.X + line.Width : line.X - advance;
            slotRight = slotLeft + advance;
        }
        else
        {
            slotLeft = rtl ? contentRight - advance : contentLeft;
            slotRight = slotLeft + advance;
        }

        RgbaColor color = style.Color ?? new RgbaColor(0, 0, 0, 255);
        if (printEconomy)
        {
            color = RenderPaint.PrintEconomyColor(color);
        }

        if (marker.IsSymbol)
        {
            int a = Ascent(engine, style);
            int offset = a * 2 / 3;
            int bullet = (offset + 1) / 2;
            float boxLeft = inside
                ? (rtl ? slotRight + 1f - (bullet + 2) : slotLeft - 1f)
                : (rtl ? contentRight + offset + 8 - (bullet + 2) : contentLeft - (offset + 8));
            float x = boxLeft + 1f;
            float y = baseline - a + (3 * (a - offset) / 2);
            PaintSymbol(pixmap, marker.Kind, x, y, bullet, color, clipMask, rasterScale);
            return;
        }

        if (marker.Text is not { } text)
        {
            return;
        }

        LayoutStyle markerStyle = MarkerStyle(style);
        float width = inside ? advance : engine.MeasureMarkerText(text, markerStyle);
        float left = inside
            ? slotLeft
            : (rtl ? contentRight : contentLeft - width);
        engine.PaintTransientText(text, markerStyle, left, baseline, pixmap, clip, clipMask, rasterScale, printEconomy);
    }

    private static void PaintSymbol(
        Pixmap pixmap,
        ListStyle kind,
        float x,
        float y,
        int size,
        RgbaColor color,
        Mask? clipMask,
        float rasterScale)
    {
        if (size <= 0 || color.A == 0)
        {
            return;
        }

        Affine2 transform = Surface.RasterTransform(rasterScale);
        var box = new SKRect(x, y, x + size, y + size);
        using SKPathBuilder builder = new();
        switch (kind)
        {
            case ListStyle.Disc:
                builder.AddOval(box);
                break;
            case ListStyle.Square:
                builder.AddRect(box);
                break;
            default:
                // A 1px stroke centred on the ellipse.
                builder.AddOval(new SKRect(box.Left - 0.5f, box.Top - 0.5f, box.Right + 0.5f, box.Bottom + 0.5f));
                builder.AddOval(new SKRect(box.Left + 0.5f, box.Top + 0.5f, box.Right - 0.5f, box.Bottom - 0.5f));
                using (SKPath ring = builder.Detach())
                using (SKPaint paint = new() { Color = PaintColor.ToSk(color), IsAntialias = true, Style = SKPaintStyle.Fill })
                {
                    Surface.FillPath(pixmap, ring, paint, evenOdd: true, transform, clipMask);
                }

                return;
        }

        using SKPath path = builder.Detach();
        Surface.FillPath(pixmap, path, color, true, transform, clipMask);
    }
}
