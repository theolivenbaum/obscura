// Border, outline and border-radius declarations from style.rs, including the
// logical-property replay that resolves `border-inline-*` once the inherited
// `direction` is final.
using Obscura.Render.Css;

namespace Obscura.Render;

public static partial class ComputedStyle
{
    /// <summary>Rust <c>Side</c>.</summary>
    internal enum Side
    {
        Top,
        Right,
        Bottom,
        Left,
    }

    /// <summary>An <c>Option&lt;Option&lt;RgbaColor&gt;&gt;</c>: the inner null is <c>currentcolor</c>.</summary>
    internal readonly record struct OptionalColor(RgbaColor? Value);

    /// <summary>Rust <c>border_style</c>.</summary>
    internal static BorderStyle? BorderStyleKeyword(string value) => CssText.AsciiLower(value.Trim()) switch
    {
        "none" => BorderStyle.None,
        "hidden" => BorderStyle.Hidden,
        "dotted" => BorderStyle.Dotted,
        "dashed" => BorderStyle.Dashed,
        "solid" => BorderStyle.Solid,
        "double" => BorderStyle.Double,
        "groove" => BorderStyle.Groove,
        "ridge" => BorderStyle.Ridge,
        "inset" => BorderStyle.Inset,
        "outset" => BorderStyle.Outset,
        _ => null,
    };

    /// <summary>Rust <c>outline_style</c>.</summary>
    internal static BorderStyle? OutlineStyleKeyword(string value)
    {
        if (CssText.EqualsAscii(value.Trim(), "auto"))
        {
            return BorderStyle.Auto;
        }

        BorderStyle? parsed = BorderStyleKeyword(value);
        return parsed == BorderStyle.Hidden ? null : parsed;
    }

    /// <summary>Rust <c>border_width</c>.</summary>
    internal static float? BorderWidth(string value)
    {
        string trimmed = value.Trim();
        float width;
        switch (CssText.AsciiLower(trimmed))
        {
            case "thin":
                width = 1.0f;
                break;
            case "medium":
                width = BorderSides.MediumBorderWidth;
                break;
            case "thick":
                width = 5.0f;
                break;
            default:
                if (StrictBorderLength(trimmed) is not { } parsed)
                {
                    return null;
                }

                width = parsed;
                break;
        }

        return float.IsFinite(width) && width >= 0f ? width : null;
    }

    /// <summary>Rust <c>strict_border_length</c>.</summary>
    internal static float? StrictBorderLength(string value)
    {
        string trimmed = value.Trim();
        if (trimmed is "0" or "+0" or "-0")
        {
            return 0f;
        }

        string lower = CssText.AsciiLower(trimmed);
        if (lower.Contains('('))
        {
            return lower.Contains('%') ? null : Px(trimmed);
        }

        string? unit = null;
        foreach (string candidate in StrictBorderUnits)
        {
            if (lower.EndsWith(candidate, StringComparison.Ordinal))
            {
                unit = candidate;
                break;
            }
        }

        if (unit is null)
        {
            return null;
        }

        if (ParseF32(lower[..^unit.Length].Trim()) is not { } number || !float.IsFinite(number))
        {
            return null;
        }

        return Px(trimmed);
    }

    private static readonly string[] StrictBorderUnits = ["rem", "px", "pt", "em", "ex"];

    /// <summary>Rust <c>border_color</c>.</summary>
    internal static OptionalColor? BorderColor(string value, bool darkScheme)
    {
        if (CssText.EqualsAscii(value.Trim(), "currentcolor"))
        {
            return new OptionalColor(null);
        }

        return CssColor.ParseForScheme(value, darkScheme) is { } color ? new OptionalColor(color) : null;
    }

    private static Sides<T> SetSide<T>(Sides<T> sides, Side side, T value) => side switch
    {
        Side.Top => sides with { Top = value },
        Side.Right => sides with { Right = value },
        Side.Bottom => sides with { Bottom = value },
        _ => sides with { Left = value },
    };

    /// <summary>Rust <c>sync_used_border</c>.</summary>
    internal static void SyncUsedBorder(LayoutStyle style)
    {
        Sides<float> used = style.BorderModel.UsedWidths();
        style.Border = new Edges(used.Top, used.Right, used.Bottom, used.Left);
    }

    private static void SyncUniformBorderColor(LayoutStyle style)
    {
        Sides<RgbaColor?> colors = style.BorderModel.Colors;
        style.BorderColor = colors.Top == colors.Right && colors.Right == colors.Bottom
            && colors.Bottom == colors.Left
            ? colors.Top
            : null;
    }

    /// <summary>Rust <c>for_each_border_cascade_side</c>.</summary>
    private static void ForEachBorderCascadeSide(
        BorderCascadeSide side,
        Layout.Direction direction,
        Action<Side> apply)
    {
        switch (side)
        {
            case BorderCascadeSide.Top:
                apply(Side.Top);
                break;
            case BorderCascadeSide.Right:
                apply(Side.Right);
                break;
            case BorderCascadeSide.Bottom:
                apply(Side.Bottom);
                break;
            case BorderCascadeSide.Left:
                apply(Side.Left);
                break;
            case BorderCascadeSide.InlineStart:
                apply(direction == Layout.Direction.Rtl ? Side.Right : Side.Left);
                break;
            case BorderCascadeSide.InlineEnd:
                apply(direction == Layout.Direction.Rtl ? Side.Left : Side.Right);
                break;
            case BorderCascadeSide.BlockStart:
                apply(Side.Top);
                break;
            case BorderCascadeSide.BlockEnd:
                apply(Side.Bottom);
                break;
            case BorderCascadeSide.Inline:
                apply(Side.Left);
                apply(Side.Right);
                break;
            case BorderCascadeSide.Block:
                apply(Side.Top);
                apply(Side.Bottom);
                break;
            case BorderCascadeSide.All:
                apply(Side.Top);
                apply(Side.Right);
                apply(Side.Bottom);
                apply(Side.Left);
                break;
        }
    }

    /// <summary>Rust <c>record_physical_border_component</c>.</summary>
    private static void RecordPhysicalBorderComponent(
        LayoutStyle style,
        BorderCascadeSide side,
        float? width,
        BorderStyle? lineStyle,
        OptionalColor? color)
    {
        // The physical model is already final until a logical property appears.
        if (style.BorderCascadeBase is null)
        {
            return;
        }

        style.BorderCascadeOps.Add(
            new BorderCascadeOp(side, width, lineStyle, color is not null, color?.Value));
    }

    /// <summary>Rust <c>record_logical_border_component</c>.</summary>
    private static void RecordLogicalBorderComponent(
        LayoutStyle style,
        BorderCascadeSide side,
        float? width,
        BorderStyle? lineStyle,
        OptionalColor? color)
    {
        style.BorderCascadeBase ??= style.BorderModel;
        style.BorderCascadeOps.Add(
            new BorderCascadeOp(side, width, lineStyle, color is not null, color?.Value));
    }

    private static BorderCascadeSide PhysicalBorderCascadeSide(Side side) => side switch
    {
        Side.Top => BorderCascadeSide.Top,
        Side.Right => BorderCascadeSide.Right,
        Side.Bottom => BorderCascadeSide.Bottom,
        _ => BorderCascadeSide.Left,
    };

    /// <summary>Rust <c>resolve_logical_borders</c>.</summary>
    internal static void ResolveLogicalBorders(LayoutStyle style)
    {
        if (style.BorderCascadeBase is not { } baseModel)
        {
            return;
        }

        // Border radius is an independent family and may have cascaded after the
        // base snapshot was taken.
        BorderModel model = baseModel with { Radii = style.BorderModel.Radii };
        Layout.Direction direction = style.Direction ?? Layout.Direction.Ltr;
        foreach (BorderCascadeOp op in style.BorderCascadeOps)
        {
            BorderCascadeOp current = op;
            BorderModel working = model;
            ForEachBorderCascadeSide(current.Side, direction, side =>
            {
                if (current.Width is { } width)
                {
                    working = working with { SpecifiedWidths = SetSide(working.SpecifiedWidths, side, width) };
                }

                if (current.Style is { } lineStyle)
                {
                    working = working with { Styles = SetSide(working.Styles, side, lineStyle) };
                }

                if (current.ColorSet)
                {
                    working = working with { Colors = SetSide(working.Colors, side, current.Color) };
                }
            });
            model = working;
        }

        style.BorderModel = model;
        SyncUniformBorderColor(style);
        SyncUsedBorder(style);
    }

    /// <summary>Rust <c>apply_border_widths</c>.</summary>
    internal static void ApplyBorderWidths(LayoutStyle style, string value)
    {
        Sides<float> values;
        if (CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer")
        {
            values = Sides<float>.All(BorderSides.MediumBorderWidth);
        }
        else
        {
            List<string> tokens = SplitWsParen(value);
            float[] parsed = new float[tokens.Count];
            for (int index = 0; index < tokens.Count; index++)
            {
                if (BorderWidth(tokens[index]) is not { } width)
                {
                    return;
                }

                parsed[index] = width;
            }

            if (BorderSides.ExpandSides<float>(parsed) is not { } expanded)
            {
                return;
            }

            values = expanded;
        }

        RecordPhysicalBorderComponent(style, BorderCascadeSide.Top, values.Top, null, null);
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Right, values.Right, null, null);
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Bottom, values.Bottom, null, null);
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Left, values.Left, null, null);
        style.BorderModel = style.BorderModel with { SpecifiedWidths = values };
        SyncUsedBorder(style);
    }

    /// <summary>Rust <c>set_border_width</c>.</summary>
    internal static void SetBorderWidth(LayoutStyle style, Side side, string value)
    {
        float? width = CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer"
            ? BorderSides.MediumBorderWidth
            : BorderWidth(value);
        if (width is not { } parsed)
        {
            return;
        }

        RecordPhysicalBorderComponent(style, PhysicalBorderCascadeSide(side), parsed, null, null);
        style.BorderModel = style.BorderModel with
        {
            SpecifiedWidths = SetSide(style.BorderModel.SpecifiedWidths, side, parsed),
        };
        SyncUsedBorder(style);
    }

    /// <summary>Rust <c>apply_border_styles</c>.</summary>
    internal static void ApplyBorderStyles(LayoutStyle style, string value)
    {
        Sides<BorderStyle> values;
        if (CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer")
        {
            values = Sides<BorderStyle>.All(BorderStyle.None);
        }
        else
        {
            List<string> tokens = SplitWsParen(value);
            BorderStyle[] parsed = new BorderStyle[tokens.Count];
            for (int index = 0; index < tokens.Count; index++)
            {
                if (BorderStyleKeyword(tokens[index]) is not { } lineStyle)
                {
                    return;
                }

                parsed[index] = lineStyle;
            }

            if (BorderSides.ExpandSides<BorderStyle>(parsed) is not { } expanded)
            {
                return;
            }

            values = expanded;
        }

        RecordPhysicalBorderComponent(style, BorderCascadeSide.Top, null, values.Top, null);
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Right, null, values.Right, null);
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Bottom, null, values.Bottom, null);
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Left, null, values.Left, null);
        style.BorderModel = style.BorderModel with { Styles = values };
        SyncUsedBorder(style);
    }

    /// <summary>Rust <c>set_border_style</c>.</summary>
    internal static void SetBorderStyle(LayoutStyle style, Side side, string value)
    {
        BorderStyle? lineStyle = CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer"
            ? BorderStyle.None
            : BorderStyleKeyword(value);
        if (lineStyle is not { } parsed)
        {
            return;
        }

        RecordPhysicalBorderComponent(style, PhysicalBorderCascadeSide(side), null, parsed, null);
        style.BorderModel = style.BorderModel with
        {
            Styles = SetSide(style.BorderModel.Styles, side, parsed),
        };
        SyncUsedBorder(style);
    }

    /// <summary>Rust <c>parse_border_colors</c>.</summary>
    internal static Sides<RgbaColor?>? ParseBorderColors(string value, bool darkScheme)
    {
        List<string> tokens = SplitWsParen(value);
        RgbaColor?[] values = new RgbaColor?[tokens.Count];
        for (int index = 0; index < tokens.Count; index++)
        {
            if (BorderColor(tokens[index], darkScheme) is not { } color)
            {
                return null;
            }

            values[index] = color.Value;
        }

        return BorderSides.ExpandSides<RgbaColor?>(values);
    }

    /// <summary>Rust <c>apply_border_colors</c>.</summary>
    internal static void ApplyBorderColors(LayoutStyle style, string value)
    {
        Sides<RgbaColor?> colors;
        if (CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer")
        {
            colors = Sides<RgbaColor?>.All(null);
        }
        else if (ParseBorderColors(value, style.ColorSchemeDark) is { } parsed)
        {
            colors = parsed;
        }
        else
        {
            return;
        }

        RecordPhysicalBorderComponent(style, BorderCascadeSide.Top, null, null, new OptionalColor(colors.Top));
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Right, null, null, new OptionalColor(colors.Right));
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Bottom, null, null, new OptionalColor(colors.Bottom));
        RecordPhysicalBorderComponent(style, BorderCascadeSide.Left, null, null, new OptionalColor(colors.Left));
        style.BorderModel = style.BorderModel with { Colors = colors };
        SyncUniformBorderColor(style);
    }

    /// <summary>Rust <c>set_border_color</c>.</summary>
    internal static void SetBorderColor(LayoutStyle style, Side side, string value)
    {
        OptionalColor? color = CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer"
            ? new OptionalColor(null)
            : BorderColor(value, style.ColorSchemeDark);
        if (color is not { } parsed)
        {
            return;
        }

        RecordPhysicalBorderComponent(style, PhysicalBorderCascadeSide(side), null, null, parsed);
        style.BorderModel = style.BorderModel with
        {
            Colors = SetSide(style.BorderModel.Colors, side, parsed.Value),
        };
        SyncUniformBorderColor(style);
    }

    /// <summary>Rust <c>BorderShorthand</c>.</summary>
    internal readonly record struct BorderShorthand(float Width, BorderStyle Style, RgbaColor? Color);

    /// <summary>Rust <c>parse_border_shorthand</c>.</summary>
    internal static BorderShorthand? ParseBorderShorthand(string value, bool darkScheme)
    {
        string lower = CssText.AsciiLower(value.Trim());
        if (lower is "initial" or "unset" or "revert" or "revert-layer")
        {
            return new BorderShorthand(BorderSides.MediumBorderWidth, BorderStyle.None, null);
        }

        if (value.Trim().Length == 0 || lower == "inherit")
        {
            return null;
        }

        float? width = null;
        BorderStyle? lineStyle = null;
        RgbaColor? color = null;
        bool sawColor = false;
        foreach (string token in SplitWsParen(value))
        {
            if (width is null && BorderWidth(token) is { } parsedWidth)
            {
                width = parsedWidth;
                continue;
            }

            if (lineStyle is null && BorderStyleKeyword(token) is { } parsedStyle)
            {
                lineStyle = parsedStyle;
                continue;
            }

            if (!sawColor && BorderColor(token, darkScheme) is { } parsedColor)
            {
                color = parsedColor.Value;
                sawColor = true;
                continue;
            }

            return null;
        }

        return new BorderShorthand(
            width ?? BorderSides.MediumBorderWidth,
            lineStyle ?? BorderStyle.None,
            color);
    }

    private static (T Start, T End)? LogicalBorderPair<T>(string value, T initial, Func<string, T?> parse)
        where T : struct
    {
        if (CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer")
        {
            return (initial, initial);
        }

        List<string> tokens = SplitWsParen(value);
        if (tokens.Count == 1)
        {
            return parse(tokens[0]) is { } single ? (single, single) : null;
        }

        if (tokens.Count == 2)
        {
            if (parse(tokens[0]) is not { } start || parse(tokens[1]) is not { } end)
            {
                return null;
            }

            return (start, end);
        }

        return null;
    }

    private static T? LogicalBorderSingle<T>(string value, T initial, Func<string, T?> parse)
        where T : struct
    {
        if (CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer")
        {
            return initial;
        }

        List<string> tokens = SplitWsParen(value);
        return tokens.Count == 1 ? parse(tokens[0]) : null;
    }

    private static (BorderCascadeSide Start, BorderCascadeSide End)? LogicalBorderSides(string name) => name switch
    {
        "border-inline-width" or "border-inline-style" or "border-inline-color" =>
            (BorderCascadeSide.InlineStart, BorderCascadeSide.InlineEnd),
        "border-block-width" or "border-block-style" or "border-block-color" =>
            (BorderCascadeSide.BlockStart, BorderCascadeSide.BlockEnd),
        _ => null,
    };

    private static BorderCascadeSide? LogicalBorderSingleSide(string name)
    {
        if (name.StartsWith("border-inline-start", StringComparison.Ordinal))
        {
            return BorderCascadeSide.InlineStart;
        }

        if (name.StartsWith("border-inline-end", StringComparison.Ordinal))
        {
            return BorderCascadeSide.InlineEnd;
        }

        if (name.StartsWith("border-block-start", StringComparison.Ordinal))
        {
            return BorderCascadeSide.BlockStart;
        }

        if (name.StartsWith("border-block-end", StringComparison.Ordinal))
        {
            return BorderCascadeSide.BlockEnd;
        }

        return null;
    }

    /// <summary>Rust <c>apply_logical_border</c>.</summary>
    internal static void ApplyLogicalBorder(LayoutStyle style, string name, string value)
    {
        BorderCascadeSide? shorthandSide = name switch
        {
            "border-inline" => BorderCascadeSide.Inline,
            "border-block" => BorderCascadeSide.Block,
            "border-inline-start" or "border-inline-end" or "border-block-start" or "border-block-end" =>
                LogicalBorderSingleSide(name),
            _ => null,
        };
        if (shorthandSide is { } side)
        {
            if (ParseBorderShorthand(value, style.ColorSchemeDark) is not { } parsed)
            {
                return;
            }

            RecordLogicalBorderComponent(
                style,
                side,
                parsed.Width,
                parsed.Style,
                new OptionalColor(parsed.Color));
            ResolveLogicalBorders(style);
            return;
        }

        (BorderCascadeSide Start, BorderCascadeSide End)? pairSides = LogicalBorderSides(name);
        BorderCascadeSide? singleSide = LogicalBorderSingleSide(name);
        if (name.EndsWith("-width", StringComparison.Ordinal))
        {
            float start;
            float end;
            if (pairSides is not null)
            {
                if (LogicalBorderPair(value, BorderSides.MediumBorderWidth, BorderWidth) is not { } values)
                {
                    return;
                }

                (start, end) = values;
            }
            else
            {
                if (LogicalBorderSingle(value, BorderSides.MediumBorderWidth, BorderWidth) is not { } single)
                {
                    return;
                }

                start = single;
                end = single;
            }

            if (pairSides is { } sides)
            {
                RecordLogicalBorderComponent(style, sides.Start, start, null, null);
                RecordLogicalBorderComponent(style, sides.End, end, null, null);
            }
            else if (singleSide is { } only)
            {
                RecordLogicalBorderComponent(style, only, start, null, null);
            }
        }
        else if (name.EndsWith("-style", StringComparison.Ordinal))
        {
            BorderStyle start;
            BorderStyle end;
            if (pairSides is not null)
            {
                if (LogicalBorderPair(value, BorderStyle.None, BorderStyleKeyword) is not { } values)
                {
                    return;
                }

                (start, end) = values;
            }
            else
            {
                if (LogicalBorderSingle(value, BorderStyle.None, BorderStyleKeyword) is not { } single)
                {
                    return;
                }

                start = single;
                end = single;
            }

            if (pairSides is { } sides)
            {
                RecordLogicalBorderComponent(style, sides.Start, null, start, null);
                RecordLogicalBorderComponent(style, sides.End, null, end, null);
            }
            else if (singleSide is { } only)
            {
                RecordLogicalBorderComponent(style, only, null, start, null);
            }
        }
        else if (name.EndsWith("-color", StringComparison.Ordinal))
        {
            bool darkScheme = style.ColorSchemeDark;
            OptionalColor start;
            OptionalColor end;
            if (pairSides is not null)
            {
                if (LogicalBorderPair(
                        value,
                        new OptionalColor(null),
                        token => BorderColor(token, darkScheme)) is not { } values)
                {
                    return;
                }

                (start, end) = values;
            }
            else
            {
                if (LogicalBorderSingle(
                        value,
                        new OptionalColor(null),
                        token => BorderColor(token, darkScheme)) is not { } single)
                {
                    return;
                }

                start = single;
                end = single;
            }

            if (pairSides is { } sides)
            {
                RecordLogicalBorderComponent(style, sides.Start, null, null, start);
                RecordLogicalBorderComponent(style, sides.End, null, null, end);
            }
            else if (singleSide is { } only)
            {
                RecordLogicalBorderComponent(style, only, null, null, start);
            }
        }
        else
        {
            return;
        }

        ResolveLogicalBorders(style);
    }

    /// <summary>Rust <c>supports_logical_border_declaration</c>.</summary>
    internal static bool SupportsLogicalBorderDeclaration(string name, string value)
    {
        if (name is "border-inline" or "border-block" or "border-inline-start" or "border-inline-end"
            or "border-block-start" or "border-block-end")
        {
            return ParseBorderShorthand(value, false) is not null;
        }

        bool isPair = name is "border-inline-width" or "border-inline-style" or "border-inline-color"
            or "border-block-width" or "border-block-style" or "border-block-color";
        List<string> tokens = SplitWsParen(value);
        if (tokens.Count == 0 || tokens.Count > (isPair ? 2 : 1))
        {
            return false;
        }

        if (name.EndsWith("-width", StringComparison.Ordinal))
        {
            return tokens.TrueForAll(token => BorderWidth(token) is not null);
        }

        if (name.EndsWith("-style", StringComparison.Ordinal))
        {
            return tokens.TrueForAll(token => BorderStyleKeyword(token) is not null);
        }

        if (name.EndsWith("-color", StringComparison.Ordinal))
        {
            return tokens.TrueForAll(token => BorderColor(token, false) is not null);
        }

        return false;
    }

    /// <summary>Rust <c>apply_border_shorthand</c>.</summary>
    internal static void ApplyBorderShorthand(LayoutStyle style, Side? side, string value)
    {
        if (ParseBorderShorthand(value, style.ColorSchemeDark) is not { } parsed)
        {
            return;
        }

        RecordPhysicalBorderComponent(
            style,
            side is { } physical ? PhysicalBorderCascadeSide(physical) : BorderCascadeSide.All,
            parsed.Width,
            parsed.Style,
            new OptionalColor(parsed.Color));
        if (side is { } target)
        {
            style.BorderModel = style.BorderModel with
            {
                SpecifiedWidths = SetSide(style.BorderModel.SpecifiedWidths, target, parsed.Width),
                Styles = SetSide(style.BorderModel.Styles, target, parsed.Style),
                Colors = SetSide(style.BorderModel.Colors, target, parsed.Color),
            };
        }
        else
        {
            style.BorderModel = style.BorderModel with
            {
                SpecifiedWidths = Sides<float>.All(parsed.Width),
                Styles = Sides<BorderStyle>.All(parsed.Style),
                Colors = Sides<RgbaColor?>.All(parsed.Color),
            };
            style.BorderColor = parsed.Color;
        }

        SyncUniformBorderColor(style);
        SyncUsedBorder(style);
    }

    /// <summary>Rust <c>radius_value</c>.</summary>
    internal static RadiusValue? RadiusValueOf(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.EndsWith('%'))
        {
            if (ParseF32(trimmed[..^1].Trim()) is not { } number)
            {
                return null;
            }

            float percentage = number / 100f;
            return float.IsFinite(percentage) && percentage >= 0f
                ? RadiusValue.FromPercentage(percentage)
                : null;
        }

        if (StrictBorderLength(trimmed) is not { } length)
        {
            return null;
        }

        return float.IsFinite(length) && length >= 0f ? RadiusValue.Pixels(length) : null;
    }

    /// <summary>Rust <c>parsed_border_radii</c>.</summary>
    internal static BorderRadii? ParsedBorderRadii(string value)
    {
        List<string> axes = SplitTopLevel(value, '/');
        if (axes.Count == 0 || axes.Count > 2)
        {
            return null;
        }

        if (ExpandRadii(axes[0]) is not { } horizontal)
        {
            return null;
        }

        Sides<RadiusValue> vertical = horizontal;
        if (axes.Count == 2)
        {
            if (ExpandRadii(axes[1]) is not { } parsed)
            {
                return null;
            }

            vertical = parsed;
        }

        return new BorderRadii(
            new CornerRadius(horizontal.Top, vertical.Top),
            new CornerRadius(horizontal.Right, vertical.Right),
            new CornerRadius(horizontal.Bottom, vertical.Bottom),
            new CornerRadius(horizontal.Left, vertical.Left));

        static Sides<RadiusValue>? ExpandRadii(string axis)
        {
            List<string> tokens = SplitWsParen(axis);
            RadiusValue[] values = new RadiusValue[tokens.Count];
            for (int index = 0; index < tokens.Count; index++)
            {
                if (RadiusValueOf(tokens[index]) is not { } radius)
                {
                    return null;
                }

                values[index] = radius;
            }

            return BorderSides.ExpandSides<RadiusValue>(values);
        }
    }

    /// <summary>Rust <c>apply_border_radius_shorthand</c>.</summary>
    internal static void ApplyBorderRadiusShorthand(LayoutStyle style, string value)
    {
        if (CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer")
        {
            style.BorderModel = style.BorderModel with { Radii = default };
        }
        else if (ParsedBorderRadii(value) is { } radii)
        {
            style.BorderModel = style.BorderModel with { Radii = radii };
        }
    }

    /// <summary>Rust <c>set_corner_radius</c>.</summary>
    internal static void SetCornerRadius(LayoutStyle style, int corner, string value)
    {
        CornerRadius? parsed;
        if (CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer")
        {
            parsed = default(CornerRadius);
        }
        else
        {
            List<string> tokens = SplitWsParen(value);
            parsed = tokens.Count switch
            {
                1 => RadiusValueOf(tokens[0]) is { } x ? CornerRadius.Circular(x) : null,
                2 => RadiusValueOf(tokens[0]) is { } x && RadiusValueOf(tokens[1]) is { } y
                    ? new CornerRadius(x, y)
                    : null,
                _ => null,
            };
        }

        if (parsed is not { } radius)
        {
            return;
        }

        BorderRadii radii = style.BorderModel.Radii;
        radii = corner switch
        {
            0 => radii with { TopLeft = radius },
            1 => radii with { TopRight = radius },
            2 => radii with { BottomRight = radius },
            3 => radii with { BottomLeft = radius },
            _ => radii,
        };
        style.BorderModel = style.BorderModel with { Radii = radii };
    }

    /// <summary>Rust <c>parse_outline_shorthand</c>.</summary>
    internal static OutlineModel? ParseOutlineShorthand(string value, bool darkScheme)
    {
        string lower = CssText.AsciiLower(value.Trim());
        if (lower is "initial" or "unset" or "revert" or "revert-layer")
        {
            return OutlineModel.Default;
        }

        if (value.Trim().Length == 0 || lower == "inherit")
        {
            return null;
        }

        OutlineModel outline = OutlineModel.Default;
        bool sawWidth = false;
        bool sawStyle = false;
        bool sawColor = false;
        foreach (string token in SplitWsParen(value))
        {
            if (!sawWidth && BorderWidth(token) is { } width)
            {
                outline = outline with { SpecifiedWidth = width };
                sawWidth = true;
                continue;
            }

            if (!sawStyle && OutlineStyleKeyword(token) is { } lineStyle)
            {
                outline = outline with { Style = lineStyle };
                sawStyle = true;
                continue;
            }

            if (!sawColor && BorderColor(token, darkScheme) is { } color)
            {
                outline = outline with { Color = color.Value };
                sawColor = true;
                continue;
            }

            return null;
        }

        return outline;
    }

    /// <summary>Rust <c>apply_outline_shorthand</c>.</summary>
    internal static void ApplyOutlineShorthand(LayoutStyle style, string value)
    {
        if (ParseOutlineShorthand(value, style.ColorSchemeDark) is { } outline)
        {
            style.Outline = outline;
        }
    }

    /// <summary>Rust <c>set_outline_width</c>.</summary>
    internal static void SetOutlineWidth(LayoutStyle style, string value)
    {
        float? width = CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer"
            ? BorderSides.MediumBorderWidth
            : BorderWidth(value);
        if (width is { } parsed)
        {
            style.Outline = style.Outline with { SpecifiedWidth = parsed };
        }
    }

    /// <summary>Rust <c>set_outline_style</c>.</summary>
    internal static void SetOutlineStyle(LayoutStyle style, string value)
    {
        BorderStyle? lineStyle = CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer"
            ? BorderStyle.None
            : OutlineStyleKeyword(value);
        if (lineStyle is { } parsed)
        {
            style.Outline = style.Outline with { Style = parsed };
        }
    }

    /// <summary>Rust <c>set_outline_color</c>.</summary>
    internal static void SetOutlineColor(LayoutStyle style, string value)
    {
        OptionalColor? color = CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer"
            ? new OptionalColor(null)
            : BorderColor(value, style.ColorSchemeDark);
        if (color is { } parsed)
        {
            style.Outline = style.Outline with { Color = parsed.Value };
        }
    }

    /// <summary>Rust <c>set_outline_offset</c>.</summary>
    internal static void SetOutlineOffset(LayoutStyle style, string value)
    {
        float? offset;
        if (CssText.AsciiLower(value.Trim()) is "initial" or "unset" or "revert" or "revert-layer")
        {
            offset = 0f;
        }
        else
        {
            offset = StrictBorderLength(value);
            if (offset is { } candidate && !float.IsFinite(candidate))
            {
                offset = null;
            }
        }

        if (offset is { } parsed)
        {
            style.Outline = style.Outline with { Offset = parsed };
        }
    }

    /// <summary>Rust <c>resolve_svg_presentation_color</c>.</summary>
    internal static string ResolveSvgPresentationColor(string value, bool darkScheme)
    {
        string raw = value.Trim();
        if (CssText.AsciiLower(raw).Contains("light-dark(", StringComparison.Ordinal)
            && CssColor.ParseForScheme(raw, darkScheme) is { } color)
        {
            return $"#{color.R:x2}{color.G:x2}{color.B:x2}{color.A:x2}";
        }

        return raw;
    }
}
