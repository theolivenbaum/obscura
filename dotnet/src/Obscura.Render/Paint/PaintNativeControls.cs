// The native form-control widgets Chromium draws from its own code rather than from the
// document: a range input's thumb, a checkbox/radio box, and the read-only field text a
// date/time input shows in place of a value.
//
// DEVIATION from crates/obscura-render/src/paint.rs, which paints none of these. In the Rust
// engine an unstyled checkbox is an empty box, a slider has no knob, and a date field is blank
// and sized as though it were a 20-character text field. All three are visible against
// Chromium 141 on ordinary pages. See "Known deviations" in todo.md.
using Obscura.Dom;
using Obscura.Render.Css;
using SkiaSharp;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal static class PaintNativeControls
{
    /// <summary>Chromium's <c>ButtonBorder</c> on a form control in the light color scheme.</summary>
    private static readonly RgbaColor ControlBorder = new(118, 118, 118, 255);

    /// <summary>Chromium's default <c>accent-color</c>.</summary>
    private static readonly RgbaColor Accent = new(0, 117, 255, 255);

    private static readonly RgbaColor ControlFill = new(255, 255, 255, 255);

    /// <summary>Chromium's slider track, and the unfilled part of a native range.</summary>
    private static readonly RgbaColor SliderTrack = new(239, 239, 239, 255);

    /// <summary>
    /// The date/time input types, each of which Chromium fills with a read-only set of editable
    /// sub-fields. They are the input types whose intrinsic width comes from that content rather
    /// than from the <c>size</c> attribute.
    /// </summary>
    internal static bool IsDateFamily(string inputType) =>
        inputType is "date" or "datetime-local" or "month" or "week" or "time";

    /// <summary>
    /// The text a date/time input shows while it holds no value: Chromium's en-US field
    /// placeholders, which are what a page with no <c>lang</c> gets.
    /// </summary>
    internal static string EmptyFieldText(string inputType) => inputType switch
    {
        "date" => "mm/dd/yyyy",
        "datetime-local" => "mm/dd/yyyy, --:-- --",
        "month" => "--------- ----",
        "week" => "Week --, ----",
        "time" => "--:-- --",
        _ => string.Empty,
    };

    /// <summary>
    /// Everything in a date/time control's content box that is not the field text: 1px of
    /// padding on each side of every sub-field, plus the picker indicator. Both are fixed
    /// pixel sizes in Chromium, so this does not scale with the font.
    /// </summary>
    /// <remarks>
    /// Read off Chromium 141: a <c>date</c> reports <c>width: 120.328px</c> for a
    /// ten-character field text that measures 80px at the UA's 13.333px monospace, and the
    /// same 34.33px indicator falls out of <c>datetime-local</c>, <c>month</c> and
    /// <c>week</c> once their sub-field count is taken off.
    /// </remarks>
    internal static float FieldChromeWidth(string inputType) => inputType switch
    {
        "date" => 40.33f,
        "datetime-local" => 46.33f,
        "month" => 38.33f,
        "week" => 38.33f,
        "time" => 35f,
        _ => 0f,
    };

    /// <summary>
    /// The content-box width a date/time input is intrinsically sized to. The field text is
    /// shaped through the inline engine for the same reason a button label is: sizing the box
    /// with a different metric from the one that lays the text out leaves the two disagreeing.
    /// </summary>
    internal static float DateFamilyIntrinsicContentWidth(
        string inputType,
        LayoutStyle style,
        TextEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.MeasureControlLabel(EmptyFieldText(inputType), style)
            + FieldChromeWidth(inputType);
    }

    /// <summary>
    /// What a date/time input shows: its value rendered the way Chromium's en-US fields do, or
    /// the empty-field placeholder when there is no value or it does not parse. HTML's value
    /// formats are all ASCII and fixed, so this reads them directly rather than through
    /// <c>DateTime</c>, whose parsing is locale- and calendar-sensitive.
    /// </summary>
    internal static string FieldText(string inputType, string? value)
    {
        string empty = EmptyFieldText(inputType);
        if (value is null || value.Length == 0)
        {
            return empty;
        }

        return (inputType switch
        {
            "date" => FormatDate(value),
            "datetime-local" => FormatDateTimeLocal(value),
            "month" => FormatMonth(value),
            "week" => FormatWeek(value),
            "time" => FormatTime(value),
            _ => null,
        }) ?? empty;
    }

    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private static string? FormatDate(string value)
    {
        // yyyy-mm-dd
        if (value.Length != 10 || value[4] != '-' || value[7] != '-')
        {
            return null;
        }

        if (!AllDigits(value, 0, 4) || !AllDigits(value, 5, 2) || !AllDigits(value, 8, 2))
        {
            return null;
        }

        return value.Substring(5, 2) + "/" + value.Substring(8, 2) + "/" + value[..4];
    }

    private static string? FormatTime(string value)
    {
        // hh:mm, optionally with seconds Chromium then shows as a third field.
        if (value.Length < 5 || value[2] != ':' || !AllDigits(value, 0, 2) || !AllDigits(value, 3, 2))
        {
            return null;
        }

        int hour = ((value[0] - '0') * 10) + (value[1] - '0');
        if (hour > 23)
        {
            return null;
        }

        string meridiem = hour < 12 ? "AM" : "PM";
        int shown = hour % 12 == 0 ? 12 : hour % 12;
        string seconds = value.Length >= 8 && value[5] == ':' && AllDigits(value, 6, 2)
            ? ":" + value.Substring(6, 2)
            : string.Empty;
        return $"{shown:00}:{value.Substring(3, 2)}{seconds} {meridiem}";
    }

    private static string? FormatDateTimeLocal(string value)
    {
        int split = value.IndexOf('T');
        if (split < 0 || FormatDate(value[..split]) is not { } date
            || FormatTime(value[(split + 1)..]) is not { } time)
        {
            return null;
        }

        return date + ", " + time;
    }

    private static string? FormatMonth(string value)
    {
        // yyyy-mm
        if (value.Length != 7 || value[4] != '-' || !AllDigits(value, 0, 4) || !AllDigits(value, 5, 2))
        {
            return null;
        }

        int month = ((value[5] - '0') * 10) + (value[6] - '0');
        return month is >= 1 and <= 12
            ? MonthNames[month - 1] + " " + value[..4]
            : null;
    }

    private static string? FormatWeek(string value)
    {
        // yyyy-Www
        if (value.Length != 8 || value[4] != '-' || (value[5] is not ('W' or 'w'))
            || !AllDigits(value, 0, 4) || !AllDigits(value, 6, 2))
        {
            return null;
        }

        return "Week " + value.Substring(6, 2) + ", " + value[..4];
    }

    private static bool AllDigits(string value, int start, int count)
    {
        for (int i = start; i < start + count; i++)
        {
            if (!char.IsAsciiDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Paint a date/time input's field text and its picker indicator. The indicator is drawn as
    /// a small calendar (or clock) outline rather than reproducing Chromium's icon asset.
    /// </summary>
    internal static void PaintDateFields(
        Pixmap pixmap,
        string inputType,
        string text,
        in Rect rect,
        LayoutStyle style,
        Rect? clip,
        Mask? clipMask,
        float rasterScale)
    {
        Rect content = ContentBox(rect, style);
        if (content.Width <= 0f || content.Height <= 0f)
        {
            return;
        }

        float fontSize = F32.Max(style.FontSize ?? 13.333_333f, 1f);
        RgbaColor color = style.Color ?? new RgbaColor(0, 0, 0, 255);
        float lineHeight = FontResolution.UsedLineHeight(style);
        float textY = content.Y + ((content.Height - lineHeight) / 2f);
        PaintText.DrawText(
            pixmap,
            text,
            content.X + 1f,
            textY,
            color,
            fontSize,
            isBold: false,
            style.FontFamily,
            style.LetterSpacing ?? 0f,
            clip,
            clipMask,
            rasterScale);

        float glyph = F32.Min(13f, F32.Min(content.Width, content.Height));
        if (glyph < 6f)
        {
            return;
        }

        float gx = content.X + content.Width - glyph - 2f;
        float gy = content.Y + ((content.Height - glyph) / 2f);
        if (gx < content.X)
        {
            return;
        }

        if (string.Equals(inputType, "time", StringComparison.Ordinal))
        {
            PaintClockIndicator(pixmap, gx, gy, glyph, color, clipMask, rasterScale);
        }
        else
        {
            PaintCalendarIndicator(pixmap, gx, gy, glyph, color, clipMask, rasterScale);
        }
    }

    private static void PaintCalendarIndicator(
        Pixmap pixmap,
        float x,
        float y,
        float size,
        RgbaColor color,
        Mask? clipMask,
        float rasterScale)
    {
        using SKPathBuilder outline = new();
        outline.AddRect(new SKRect(x, y + (size * 0.12f), x + size, y + size));
        outline.AddRect(new SKRect(
            x + 1f, y + (size * 0.12f) + 1f, x + size - 1f, y + size - 1f));
        using SKPath frame = outline.Detach();
        FillEvenOdd(pixmap, frame, color, clipMask, rasterScale);

        using SKPathBuilder marks = new();

        // The two hangers above the frame and the filled title bar inside it.
        marks.AddRect(new SKRect(x + (size * 0.24f), y, x + (size * 0.36f), y + (size * 0.3f)));
        marks.AddRect(new SKRect(x + (size * 0.64f), y, x + (size * 0.76f), y + (size * 0.3f)));
        marks.AddRect(new SKRect(
            x + 1f, y + (size * 0.34f), x + size - 1f, y + (size * 0.46f)));
        using SKPath marksPath = marks.Detach();
        Surface.FillPath(
            pixmap, marksPath, color, true, Surface.RasterTransform(rasterScale), clipMask);
    }

    /// <summary>An outline shape: two nested paths filled with the even-odd rule.</summary>
    private static void FillEvenOdd(
        Pixmap pixmap,
        SKPath path,
        RgbaColor color,
        Mask? clipMask,
        float rasterScale)
    {
        if (color.A == 0)
        {
            return;
        }

        using SKPaint paint = new()
        {
            Color = PaintColor.ToSk(color),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        Surface.FillPath(pixmap, path, paint, true, Surface.RasterTransform(rasterScale), clipMask);
    }

    private static void PaintClockIndicator(
        Pixmap pixmap,
        float x,
        float y,
        float size,
        RgbaColor color,
        Mask? clipMask,
        float rasterScale)
    {
        float radius = size / 2f;
        using SKPathBuilder ring = new();
        ring.AddOval(new SKRect(x, y, x + size, y + size));
        ring.AddOval(new SKRect(x + 1f, y + 1f, x + size - 1f, y + size - 1f));
        using SKPath ringPath = ring.Detach();
        FillEvenOdd(pixmap, ringPath, color, clipMask, rasterScale);

        using SKPathBuilder hands = new();
        hands.AddRect(new SKRect(
            x + radius - 0.5f, y + (size * 0.25f), x + radius + 0.5f, y + radius + 0.5f));
        hands.AddRect(new SKRect(
            x + radius - 0.5f, y + radius - 0.5f, x + (size * 0.78f), y + radius + 0.5f));
        using SKPath handsPath = hands.Detach();
        Surface.FillPath(
            pixmap, handsPath, color, true, Surface.RasterTransform(rasterScale), clipMask);
    }

    /// <summary>Paint an unstyled checkbox or radio the way Chromium's native painter does.</summary>
    internal static void PaintToggle(
        Pixmap pixmap,
        bool isRadio,
        bool isChecked,
        in Rect rect,
        Mask? clipMask,
        float rasterScale)
    {
        if (rect.Width < 2f || rect.Height < 2f)
        {
            return;
        }

        float x = rect.X;
        float y = rect.Y;
        float w = rect.Width;
        float h = rect.Height;
        Affine2 transform = Surface.RasterTransform(rasterScale);
        if (isRadio)
        {
            using SKPathBuilder disc = new();
            disc.AddOval(new SKRect(x, y, x + w, y + h));
            using SKPath discPath = disc.Detach();
            Surface.FillPath(pixmap, discPath, isChecked ? Accent : ControlBorder, true, transform, clipMask);

            float inset = isChecked ? F32.Max(w * 0.2f, 1f) : 1f;
            using SKPathBuilder inner = new();
            inner.AddOval(new SKRect(x + inset, y + inset, x + w - inset, y + h - inset));
            using SKPath innerPath = inner.Detach();
            Surface.FillPath(pixmap, innerPath, ControlFill, true, transform, clipMask);

            if (isChecked)
            {
                float dot = F32.Max(w * 0.3f, 1f);
                using SKPathBuilder centre = new();
                centre.AddOval(new SKRect(
                    x + ((w - dot) / 2f), y + ((h - dot) / 2f),
                    x + ((w + dot) / 2f), y + ((h + dot) / 2f)));
                using SKPath centrePath = centre.Detach();
                Surface.FillPath(pixmap, centrePath, Accent, true, transform, clipMask);
            }

            return;
        }

        float radius = F32.Min(2f, F32.Min(w, h) / 2f);
        if (isChecked)
        {
            if (PaintClips.RoundedRectPath(x, y, w, h, radius, radius) is { } filled)
            {
                Surface.FillPath(pixmap, filled, Accent, true, transform, clipMask);
                filled.Dispose();
            }

            // The tick, as a stroked polyline in the box's own proportions.
            using SKPaint tick = new()
            {
                Color = PaintColor.ToSk(ControlFill),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = F32.Max(w * 0.14f, 1f),
                StrokeCap = SKStrokeCap.Butt,
                StrokeJoin = SKStrokeJoin.Miter,
            };
            using SKPathBuilder mark = new();
            mark.MoveTo(x + (w * 0.22f), y + (h * 0.52f));
            mark.LineTo(x + (w * 0.42f), y + (h * 0.72f));
            mark.LineTo(x + (w * 0.78f), y + (h * 0.28f));
            using SKPath markPath = mark.Detach();
            Surface.StrokePath(pixmap, markPath, tick, transform, clipMask);
            return;
        }

        if (PaintClips.RoundedRectPath(x, y, w, h, radius, radius) is { } outer)
        {
            Surface.FillPath(pixmap, outer, ControlBorder, true, transform, clipMask);
            outer.Dispose();
        }

        if (w > 2f && h > 2f
            && PaintClips.RoundedRectPath(
                x + 1f, y + 1f, w - 2f, h - 2f, F32.Max(radius - 1f, 0f), F32.Max(radius - 1f, 0f))
                is { } inside)
        {
            Surface.FillPath(pixmap, inside, ControlFill, true, transform, clipMask);
            inside.Dispose();
        }
    }

    /// <summary>
    /// Paint a range input's thumb, and the native track when the page did not restyle the
    /// control. The thumb's position along the track comes from <c>value</c> against
    /// <c>min</c>/<c>max</c>, exactly as Chromium's slider does.
    /// </summary>
    internal static void PaintRange(
        Pixmap pixmap,
        DomTree tree,
        NodeId nid,
        in Rect rect,
        LayoutStyle style,
        Rect? clip,
        Mask? clipMask,
        float rasterScale)
    {
        Rect content = ContentBox(rect, style);
        if (content.Width <= 0f)
        {
            return;
        }

        LayoutStyle? thumb = style.SliderThumbPseudo;
        Affine2 transform = Surface.RasterTransform(rasterScale);
        if (thumb is null)
        {
            PaintNativeRange(pixmap, content, RangeFraction(tree, nid), transform, clipMask);
            return;
        }

        if (thumb.EffectivelyInvisible
            || !TryThumbRect(tree, nid, content, style, out Rect thumbRect))
        {
            return;
        }

        if (clip is { } bounds && thumbRect.Intersect(bounds) is null)
        {
            return;
        }

        BackgroundGeometry background = PaintGradients.BackgroundGeometryFor(thumbRect, thumb);
        SKPath? backgroundPath = PaintGradients.BackgroundClipPath(background);
        if (backgroundPath is not null)
        {
            if (thumb.BackgroundColor is { } fill)
            {
                Surface.FillPath(pixmap, backgroundPath, fill, true, transform, clipMask);
            }

            backgroundPath.Dispose();
        }

        PaintBorders.PaintCssBorder(pixmap, thumbRect, thumb, clipMask, rasterScale);
    }

    /// <summary>
    /// The ink a native widget puts outside the control's own border box, or null when it has
    /// none. Only a range input does: its thumb is taller than the track and a page that styles
    /// the track itself commonly leaves the input with no height at all.
    /// </summary>
    internal static Rect? NativeInkBounds(DomTree tree, NodeId nid, in Rect rect, LayoutStyle style)
    {
        if (style.SliderThumbPseudo is null
            || tree.GetNode(nid)?.AsElement() is not { } element
            || !string.Equals(element.Name.Local, "input", StringComparison.Ordinal))
        {
            return null;
        }

        Rect content = ContentBox(rect, style);
        return content.Width > 0f && TryThumbRect(tree, nid, content, style, out Rect thumb)
            ? thumb
            : null;
    }

    /// <summary>
    /// Where the thumb's border box sits over the track. Chromium keeps the whole thumb inside
    /// the track, so the travel is the track minus the thumb: <c>value == min</c> puts its left
    /// edge on the track's left edge and <c>value == max</c> its right edge on the right edge.
    /// </summary>
    private static bool TryThumbRect(
        DomTree tree,
        NodeId nid,
        in Rect content,
        LayoutStyle style,
        out Rect thumbRect)
    {
        thumbRect = default;
        if (style.SliderThumbPseudo is not { } thumb)
        {
            return false;
        }

        float fontSize = F32.Max(thumb.FontSize ?? style.FontSize ?? 13.333_333f, 1f);
        float width = ResolveThumbLength(thumb.Width, fontSize, content.Width, 16f);
        float height = ResolveThumbLength(thumb.Height, fontSize, content.Height, 16f);
        if (thumb.BoxSizing == BoxSizing.ContentBox)
        {
            width += thumb.Border.Left + thumb.Border.Right + thumb.Padding.Left + thumb.Padding.Right;
            height += thumb.Border.Top + thumb.Border.Bottom + thumb.Padding.Top + thumb.Padding.Bottom;
        }

        if (width <= 0f || height <= 0f)
        {
            return false;
        }

        float travel = F32.Max(content.Width - width, 0f);
        thumbRect = new Rect(
            content.X + (travel * RangeFraction(tree, nid)),
            content.Y + ((content.Height - height) / 2f),
            width,
            height);
        return true;
    }

    private static void PaintNativeRange(
        Pixmap pixmap,
        in Rect content,
        float fraction,
        Affine2 transform,
        Mask? clipMask)
    {
        float thumbSize = F32.Min(14f, content.Height > 0f ? content.Height : 14f);
        float trackHeight = F32.Min(4f, F32.Max(content.Height, 1f));
        float trackY = content.Y + ((content.Height - trackHeight) / 2f);
        float radius = trackHeight / 2f;
        if (PaintClips.RoundedRectPath(content.X, trackY, content.Width, trackHeight, radius, radius)
            is { } track)
        {
            Surface.FillPath(pixmap, track, SliderTrack, true, transform, clipMask);
            track.Dispose();
        }

        float travel = F32.Max(content.Width - thumbSize, 0f);
        float centre = content.X + (travel * fraction) + (thumbSize / 2f);
        if (centre > content.X
            && PaintClips.RoundedRectPath(
                content.X, trackY, centre - content.X, trackHeight, radius, radius) is { } filled)
        {
            Surface.FillPath(pixmap, filled, Accent, true, transform, clipMask);
            filled.Dispose();
        }

        using SKPathBuilder knob = new();
        knob.AddOval(new SKRect(
            centre - (thumbSize / 2f),
            content.Y + ((content.Height - thumbSize) / 2f),
            centre + (thumbSize / 2f),
            content.Y + ((content.Height + thumbSize) / 2f)));
        using SKPath knobPath = knob.Detach();
        Surface.FillPath(pixmap, knobPath, Accent, true, transform, clipMask);
    }

    private static float ResolveThumbLength(
        Dimension dimension,
        float fontSize,
        float basis,
        float fallback) => dimension.Kind switch
        {
            DimensionKind.Px => F32.Max(dimension.Value, 0f),
            DimensionKind.Em => F32.Max(dimension.Value * fontSize, 0f),
            DimensionKind.Rem => F32.Max(dimension.Value * fontSize, 0f),
            DimensionKind.Percent => F32.Max(dimension.Value * basis, 0f),
            _ => fallback,
        };

    /// <summary>
    /// Where the thumb sits, in 0..1. HTML's default range is 0..100 and a value outside
    /// <c>min</c>..<c>max</c> is clamped into it; a reversed or degenerate range has no travel.
    /// </summary>
    internal static float RangeFraction(DomTree tree, NodeId nid)
    {
        Node? node = tree.GetNode(nid);
        if (node is null)
        {
            return 0f;
        }

        float min = ParseNumber(node.GetAttribute("min")) ?? 0f;
        float max = ParseNumber(node.GetAttribute("max")) ?? 100f;
        string? raw = tree.TryGetDirtyFormValue(nid, out string dirty) ? dirty : node.GetAttribute("value");
        float span = max - min;
        if (!float.IsFinite(span) || span <= 0f)
        {
            return 0f;
        }

        // With no value at all HTML puts a range at the midpoint of its span.
        float value = ParseNumber(raw) ?? (min + (span / 2f));
        return F32.Max(F32.Min((value - min) / span, 1f), 0f);
    }

    private static float? ParseNumber(string? value) =>
        value is not null
        && float.TryParse(
            value.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float parsed)
        && float.IsFinite(parsed)
            ? parsed
            : null;

    internal static Rect ContentBox(in Rect rect, LayoutStyle style) => new(
        rect.X + style.Border.Left + style.Padding.Left,
        rect.Y + style.Border.Top + style.Padding.Top,
        F32.Max(
            rect.Width - style.Border.Left - style.Border.Right
                - style.Padding.Left - style.Padding.Right,
            0f),
        F32.Max(
            rect.Height - style.Border.Top - style.Border.Bottom
                - style.Padding.Top - style.Padding.Bottom,
            0f));

    /// <summary>
    /// The value a text control actually shows: the dirty <c>value</c> IDL attribute when script
    /// set one, otherwise the <c>value</c> content attribute.
    /// </summary>
    internal static string? ShownValue(DomTree tree, NodeId nid, Node node) =>
        tree.TryGetDirtyFormValue(nid, out string dirty) ? dirty : node.GetAttribute("value");

    /// <summary>Whether a control is checked, taking a script-assigned state first.</summary>
    internal static bool IsChecked(DomTree tree, NodeId nid, Node node) =>
        tree.TryGetDirtyFormChecked(nid, out bool dirty)
            ? dirty
            : node.GetAttribute("checked") is not null;

    /// <summary>
    /// The input types that show a text value: everything with an editable text field. Chromium
    /// paints no value text on the widget types, which is the bug the general
    /// <c>&lt;input&gt;</c> arm used to have - it painted "50" beside a slider.
    /// </summary>
    internal static bool ShowsTextValue(string inputType) =>
        inputType is "text" or "search" or "url" or "tel" or "email" or "password" or "number"
            or "submit" or "reset" or "button";

    /// <summary>Whether this type renders a placeholder at all.</summary>
    internal static bool ShowsPlaceholder(string inputType) =>
        inputType is "text" or "search" or "url" or "tel" or "email" or "password" or "number";
}
