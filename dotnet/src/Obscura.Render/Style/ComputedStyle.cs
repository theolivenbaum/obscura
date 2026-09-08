// compute_style / ua_style / apply_inline and the declaration dispatcher.
using Obscura.Render.Css;

namespace Obscura.Render;

public static partial class ComputedStyle
{
    /// <summary>
    /// Rust <c>compute_style</c>: UA defaults for a tag, overridden by its inline
    /// <c>style="..."</c> declarations.
    /// </summary>
    public static LayoutStyle Compute(string tag, string? inlineCss)
    {
        LayoutStyle style = UaStyle(tag);
        if (inlineCss is not null)
        {
            ApplyInline(style, inlineCss);
        }

        return style;
    }

    /// <summary>Rust <c>ua_style</c>: the built-in UA defaults.</summary>
    public static LayoutStyle UaStyle(string tag)
    {
        LayoutStyle style = new();
        if (tag is "b" or "strong")
        {
            style.FontWeight = "bold";
        }

        style.Display = tag switch
        {
            // Phrasing / inline-level content defaults to inline so a paragraph
            // that mixes these with text stays one inline formatting context.
            "span" or "a" or "b" or "i" or "strong" or "em" or "font" or "code" or "small" or "sub"
                or "sup" or "mark" or "abbr" or "cite" or "var" or "dfn" or "kbd" or "samp" or "q"
                or "time" or "s" or "u" or "del" or "ins" or "tt" or "big" or "bdi" or "bdo" or "br"
                or "wbr" or "data" or "output" or "label" or "ruby" or "rt" or "rp" => Display.Inline,
            "tr" => Display.Flex,
            _ => Display.Block,
        };

        if (tag == "slot")
        {
            // HTML's UA sheet makes a slot transparent to box generation.
            style.Display = Display.Block;
            style.DisplayContents = true;
        }
        else if (tag == "center")
        {
            style.TextAlign = Layout.AlignItems.Center;
            style.LegacyCenter = true;
        }
        else if (tag is "head" or "script" or "style" or "title" or "meta" or "link" or "noscript"
            or "template" or "desc" or "metadata" or "option" or "optgroup" or "source" or "track"
            or "param" or "area")
        {
            style.Display = Display.None;
        }
        else if (tag == "pre")
        {
            style.WhiteSpace = Obscura.Render.WhiteSpace.Pre;
        }
        else if (tag == "body")
        {
            style.Margin = new Edges(8.0f, 8.0f, 8.0f, 8.0f);
        }
        else if (tag == "h1")
        {
            HeadingStyle(style, 2.0f, 0.67f);
        }
        else if (tag == "h2")
        {
            HeadingStyle(style, 1.5f, 0.83f);
        }
        else if (tag == "h3")
        {
            HeadingStyle(style, 1.17f, 1.0f);
        }
        else if (tag == "h4")
        {
            HeadingStyle(style, 1.0f, 1.33f);
        }
        else if (tag == "h5")
        {
            HeadingStyle(style, 0.83f, 1.67f);
        }
        else if (tag == "h6")
        {
            HeadingStyle(style, 0.67f, 2.33f);
        }
        else if (tag is "p" or "dl" or "ul" or "ol" or "menu" or "dir")
        {
            style.MarginRelative[0] = Dimension.Em(1.0f);
            style.MarginRelative[2] = Dimension.Em(1.0f);
            if (tag is "ul" or "menu" or "dir")
            {
                style.ListStyle = Obscura.Render.ListStyle.Disc;
                style.Padding = style.Padding with { Left = 40.0f };
            }
            else if (tag == "ol")
            {
                style.ListStyle = Obscura.Render.ListStyle.Decimal;
                style.Padding = style.Padding with { Left = 40.0f };
            }
        }
        else if (tag is "b" or "strong")
        {
            style.FontWeight = "bold";
        }
        else if (tag is "i" or "em" or "cite" or "var" or "dfn" or "address")
        {
            style.FontStyleItalic = true;
        }
        else if (tag == "a")
        {
            style.Color = new RgbaColor(0, 0, 238, 255);
            style.Underline = true;
        }
        else if (tag == "iframe")
        {
            style.Border = new Edges(2.0f, 2.0f, 2.0f, 2.0f);
            style.BorderModel = style.BorderModel with
            {
                SpecifiedWidths = Sides<float>.All(2.0f),
                Styles = Sides<BorderStyle>.All(BorderStyle.Inset),
            };
        }
        else if (tag == "button")
        {
            style.Display = Display.Inline;
            style.IsInlineBlock = true;
            style.TextAlign = Layout.AlignItems.Center;
            style.BoxSizing = BoxSizing.BorderBox;
            style.Padding = new Edges(1.0f, 6.0f, 1.0f, 6.0f);
        }
        else if (tag == "select")
        {
            style.Display = Display.Inline;
            style.IsInlineBlock = true;
            style.FontSize = 13.333_333f;
            style.FontFamily = "arial";
            style.LineHeight = Obscura.Render.LineHeight.Normal;
            style.Padding = new Edges(1.0f, 20.0f, 1.0f, 2.0f);
            style.Border = new Edges(1.0f, 1.0f, 1.0f, 1.0f);
            style.BorderModel = style.BorderModel with
            {
                SpecifiedWidths = Sides<float>.All(1.0f),
                Styles = Sides<BorderStyle>.All(BorderStyle.Solid),
                Colors = Sides<RgbaColor?>.All(new RgbaColor(118, 118, 118, 255)),
            };
            style.BorderColor = new RgbaColor(118, 118, 118, 255);
            style.BackgroundColor = new RgbaColor(255, 255, 255, 255);
        }
        else if (tag == "input")
        {
            style.Display = Display.Inline;
            style.IsInlineBlock = true;
            style.FontSize = 13.333_333f;
            style.FontFamily = "arial";
            style.LineHeight = Obscura.Render.LineHeight.Normal;
            style.Padding = new Edges(1.0f, 2.0f, 1.0f, 2.0f);
            style.Border = new Edges(2.0f, 2.0f, 2.0f, 2.0f);
            style.BorderModel = style.BorderModel with
            {
                SpecifiedWidths = Sides<float>.All(2.0f),
                Styles = Sides<BorderStyle>.All(BorderStyle.Solid),
                Colors = Sides<RgbaColor?>.All(new RgbaColor(118, 118, 118, 255)),
            };
            style.BorderColor = new RgbaColor(118, 118, 118, 255);
            style.BackgroundColor = new RgbaColor(255, 255, 255, 255);
        }
        else if (tag == "textarea")
        {
            style.Display = Display.Inline;
            style.IsInlineBlock = true;
            style.FontSize = 13.333_333f;
            style.FontFamily = "monospace";
            style.LineHeight = Obscura.Render.LineHeight.Normal;
            style.WhiteSpace = Obscura.Render.WhiteSpace.PreWrap;
            style.BoxSizing = BoxSizing.BorderBox;
            style.Padding = new Edges(2.0f, 2.0f, 2.0f, 2.0f);
            style.Border = new Edges(1.0f, 1.0f, 1.0f, 1.0f);
            style.BorderModel = style.BorderModel with
            {
                SpecifiedWidths = Sides<float>.All(1.0f),
                Styles = Sides<BorderStyle>.All(BorderStyle.Solid),
                Colors = Sides<RgbaColor?>.All(new RgbaColor(118, 118, 118, 255)),
            };
            style.BorderColor = new RgbaColor(118, 118, 118, 255);
            style.BackgroundColor = new RgbaColor(255, 255, 255, 255);
        }
        else if (tag is "table" or "tbody" or "thead" or "tfoot")
        {
            style.Display = Display.Flex;
            style.InternalFlexContainer = true;
            style.FlexDirection = Layout.FlexDirection.Column;
            style.AlignItems = Layout.AlignItems.Stretch;
            style.MinWidth = Dimension.Px(0.0f);
            if (tag == "table")
            {
                style.IsTableBox = true;
                style.BoxSizing = BoxSizing.BorderBox;
                style.BorderSpacing = (2.0f, 2.0f);
                style.BorderCollapse = false;
            }
            else
            {
                style.Width = Dimension.Percent(1.0f);
                style.VerticalAlign = Obscura.Render.VerticalAlign.Middle;
            }
        }
        else if (tag == "tr")
        {
            style.InternalFlexContainer = true;
            style.MinWidth = Dimension.Px(0.0f);
            style.Width = Dimension.Percent(1.0f);
        }
        else if (tag is "td" or "th")
        {
            style.Display = Display.Flex;
            style.InternalFlexContainer = true;
            style.IsTableCellBox = true;
            style.FlexDirection = Layout.FlexDirection.Column;
            style.AlignItems = Layout.AlignItems.FlexStart;
            style.Padding = new Edges(1.0f, 1.0f, 1.0f, 1.0f);
            style.MinWidth = Dimension.Px(0.0f);
            if (tag == "th")
            {
                style.FontWeight = "bold";
            }
        }
        else if (tag == "img")
        {
            style.Display = Display.Inline;
        }

        return style;

        static void HeadingStyle(LayoutStyle style, float fontSizeEm, float marginEm)
        {
            style.FontSize = null;
            style.FontSizeRaw = Dimension.Em(fontSizeEm);
            style.FontWeight = "bold";
            style.MarginRelative[0] = Dimension.Em(marginEm);
            style.MarginRelative[2] = Dimension.Em(marginEm);
        }
    }

    /// <summary>Rust <c>apply_inline</c>.</summary>
    public static void ApplyInline(LayoutStyle style, string css)
    {
        (string normal, string important) = CssDeclarations.Partition(css);
        bool inheritedScheme = style.ColorSchemeDark;
        ApplyColorSchemeDeclarationsFrom(style, normal, inheritedScheme);
        ApplyColorSchemeDeclarationsFrom(style, important, inheritedScheme);
        ApplyDeclarationsWithLockedColorScheme(style, normal);
        ApplyDeclarationsWithLockedColorScheme(style, important);
    }

    /// <summary>Rust <c>apply_color_scheme_declarations_from</c>.</summary>
    public static void ApplyColorSchemeDeclarationsFrom(LayoutStyle style, string css, bool inheritedScheme)
    {
        foreach (string raw in CssDeclarations.Split(css))
        {
            string declaration = raw.Trim();
            int separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            if (CssText.EqualsAscii(declaration[..separator].Trim(), "color-scheme"))
            {
                ApplyColorScheme(style, declaration[(separator + 1)..].Trim(), inheritedScheme);
            }
        }
    }

    /// <summary>Rust <c>apply_declarations_with_locked_color_scheme</c>.</summary>
    public static void ApplyDeclarationsWithLockedColorScheme(LayoutStyle style, string css)
    {
        foreach (string raw in CssDeclarations.Split(css))
        {
            string declaration = raw.Trim();
            if (declaration.Length == 0)
            {
                continue;
            }

            int separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string name = CssText.AsciiLower(declaration[..separator].Trim());
            if (name != "color-scheme")
            {
                ApplyValue(style, name, declaration[(separator + 1)..].Trim());
            }
        }
    }

    /// <summary>Rust <c>apply_animation_declarations</c>.</summary>
    public static void ApplyAnimationDeclarations(LayoutStyle style, string css)
    {
        foreach (string raw in CssDeclarations.Split(css))
        {
            string declaration = raw.Trim();
            int separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            string name = CssText.AsciiLower(declaration[..separator].Trim());
            if (name == "animation" || name.StartsWith("animation-", StringComparison.Ordinal))
            {
                ApplyValue(style, name, declaration[(separator + 1)..].Trim());
            }
        }
    }

    /// <summary>Rust <c>apply_animation_property_value</c>.</summary>
    public static void ApplyAnimationPropertyValue(LayoutStyle style, string name, string value) =>
        ApplyValue(style, name, value);

    /// <summary>Rust <c>apply_color_scheme</c>.</summary>
    internal static void ApplyColorScheme(LayoutStyle style, string value, bool inheritedScheme)
    {
        List<string> tokens = [];
        foreach (string token in SplitWhitespace(value))
        {
            tokens.Add(CssText.AsciiLower(token));
        }

        if (tokens.Contains("inherit") || tokens.Contains("unset"))
        {
            style.ColorSchemeDark = inheritedScheme;
            return;
        }

        if (tokens.Contains("initial") || tokens.Contains("revert") || tokens.Contains("revert-layer"))
        {
            style.ColorSchemeDark = false;
            return;
        }

        // The current browser/user preference is light. A scheme list that admits
        // light therefore uses light; a dark-only list uses dark.
        if (tokens.Contains("light") || tokens.Contains("normal"))
        {
            style.ColorSchemeDark = false;
        }
        else if (tokens.Contains("dark"))
        {
            style.ColorSchemeDark = true;
        }
    }

    // ------------------------------------------------------------- overflow

    private readonly record struct ParsedOverflowAxis(byte Specified, bool Inherit);

    private static ParsedOverflowAxis? ParseOverflowAxis(string value) =>
        CssText.AsciiLower(value.Trim()) switch
        {
            "visible" => new ParsedOverflowAxis(0, false),
            "clip" => new ParsedOverflowAxis(1, false),
            "hidden" or "scroll" or "auto" or "overlay" => new ParsedOverflowAxis(2, false),
            "inherit" => new ParsedOverflowAxis(0, true),
            "initial" or "unset" or "revert" or "revert-layer" => new ParsedOverflowAxis(0, false),
            _ => null,
        };

    private static (ParsedOverflowAxis? X, ParsedOverflowAxis? Y, bool Valid) ParseOverflowDeclaration(
        string name,
        string value)
    {
        switch (name)
        {
            case "overflow-x":
                return ParseOverflowAxis(value) is { } x ? (x, null, true) : (null, null, false);
            case "overflow-y":
                return ParseOverflowAxis(value) is { } y ? (null, y, true) : (null, null, false);
            case "overflow":
            {
                List<string> values = SplitWsParen(value);
                if (values.Count == 0 || values.Count > 2)
                {
                    return (null, null, false);
                }

                if (ParseOverflowAxis(values[0]) is not { } first)
                {
                    return (null, null, false);
                }

                // CSS-wide keywords apply to the whole shorthand.
                if (first.Inherit
                    || CssText.AsciiLower(values[0]) is "initial" or "unset" or "revert" or "revert-layer")
                {
                    return values.Count == 1 ? (first, first, true) : (null, null, false);
                }

                ParsedOverflowAxis second;
                if (values.Count > 1)
                {
                    if (ParseOverflowAxis(values[1]) is not { } parsed)
                    {
                        return (null, null, false);
                    }

                    if (parsed.Inherit
                        || CssText.AsciiLower(values[1]) is "initial" or "unset" or "revert" or "revert-layer")
                    {
                        return (null, null, false);
                    }

                    second = parsed;
                }
                else
                {
                    second = first;
                }

                return (first, second, true);
            }

            default:
                return (null, null, false);
        }
    }

    /// <summary>Rust <c>recompute_overflow</c>.</summary>
    public static void RecomputeOverflow(LayoutStyle style)
    {
        // CSS Overflow computed-value coupling: if exactly one axis is scrollable,
        // `visible` on the other computes to `auto` and `clip` computes to `hidden`.
        byte computedX = style.OverflowSpecifiedX;
        byte computedY = style.OverflowSpecifiedY;
        if ((computedX == 2) != (computedY == 2))
        {
            if (computedX == 2)
            {
                computedY = 2;
            }
            else
            {
                computedX = 2;
            }
        }

        style.OverflowClipX = computedX != 0;
        style.OverflowClipY = computedY != 0;
        style.OverflowScrollX = computedX == 2;
        style.OverflowScrollY = computedY == 2;
        style.OverflowHidden = style.OverflowClipX || style.OverflowClipY;
        style.OverflowScrollContainer = style.OverflowScrollX || style.OverflowScrollY;
    }

    /// <summary>Rust <c>normalize_webkit_line_clamp_display</c>.</summary>
    public static void NormalizeWebkitLineClampDisplay(LayoutStyle style)
    {
        if (style.WebkitBoxDisplay is not { } inline)
        {
            return;
        }

        if (style.WebkitBoxOrientVertical && style.WebkitLineClamp is not null)
        {
            style.Display = Display.Block;
            style.IsInlineBlock = inline;
            style.FlowRoot = true;
        }
        else
        {
            style.Display = Display.Flex;
            style.IsInlineBlock = inline;
            style.FlowRoot = false;
        }
    }

    /// <summary>Rust <c>webkit_line_clamp_value</c>.</summary>
    internal static (bool Valid, uint? Lines) WebkitLineClampValue(string value)
    {
        string trimmed = value.Trim();
        if (CssText.EqualsAscii(trimmed, "none")
            || CssText.AsciiLower(trimmed) is "initial" or "unset" or "revert" or "revert-layer")
        {
            return (true, null);
        }

        if (trimmed.Length == 0)
        {
            return (false, null);
        }

        foreach (char character in trimmed)
        {
            if (!CssText.IsAsciiDigit(character))
            {
                return (false, null);
            }
        }

        bool hasNonZero = false;
        foreach (char character in trimmed)
        {
            if (character != '0')
            {
                hasNonZero = true;
                break;
            }
        }

        if (!hasNonZero)
        {
            return (false, null);
        }

        ulong lines = Math.Min(ParseU64Saturating(trimmed), int.MaxValue);
        return (true, (uint)lines);
    }

    // ------------------------------------------------------ box edge setters

    /// <summary>Rust <c>inset_dim</c>.</summary>
    private static Dimension? InsetDim(string value)
    {
        Dimension dimension = DimensionValue(value);
        return dimension.IsAuto ? null : dimension;
    }

    /// <summary>Rust <c>set_inset_side</c>.</summary>
    internal static void SetInsetSide(LayoutStyle style, int index, string value)
    {
        string trimmed = value.Trim();
        if (DeferredLengthExpression(trimmed) is { } expression)
        {
            style.Inset[index] = null;
            style.InsetExpressions[index] = expression;
            return;
        }

        Dimension? inset = InsetDim(trimmed);
        style.Inset[index] = inset;
        style.InsetExpressions[index] = inset is { } dimension
            && dimension.Kind is DimensionKind.Vw or DimensionKind.Vh
                or DimensionKind.Vmin or DimensionKind.Vmax
            ? trimmed
            : null;
    }

    private static Edges SetEdge(Edges edges, int index, float value) => index switch
    {
        0 => edges with { Top = value },
        1 => edges with { Right = value },
        2 => edges with { Bottom = value },
        3 => edges with { Left = value },
        _ => edges,
    };

    /// <summary>Rust <c>set_margin_side</c>.</summary>
    internal static void SetMarginSide(LayoutStyle style, int index, string value)
    {
        string trimmed = value.Trim();
        bool isAuto = CssText.EqualsAscii(trimmed, "auto");
        if (DeferredLengthExpression(trimmed) is { } expression)
        {
            style.MarginExpressions[index] = expression;
            style.MarginPercent[index] = null;
            style.MarginRelative[index] = null;
            style.MarginAuto[index] = false;
            style.Margin = SetEdge(style.Margin, index, 0f);
            return;
        }

        style.MarginExpressions[index] = null;
        if (PercentFraction(trimmed) is { } fraction)
        {
            style.MarginPercent[index] = fraction;
            style.MarginRelative[index] = null;
            style.Margin = SetEdge(style.Margin, index, 0f);
            style.MarginAuto[index] = false;
            return;
        }

        Dimension dimension = DimensionValue(trimmed);
        switch (dimension.Kind)
        {
            case DimensionKind.Px:
                style.Margin = SetEdge(style.Margin, index, dimension.Value);
                style.MarginRelative[index] = null;
                break;
            case DimensionKind.Em:
            case DimensionKind.Ex:
            case DimensionKind.Rem:
            case DimensionKind.Vw:
            case DimensionKind.Vh:
            case DimensionKind.Vmin:
            case DimensionKind.Vmax:
                style.Margin = SetEdge(style.Margin, index, 0f);
                style.MarginRelative[index] = dimension;
                break;
            default:
                style.Margin = SetEdge(style.Margin, index, 0f);
                style.MarginRelative[index] = null;
                break;
        }

        style.MarginAuto[index] = isAuto;
        style.MarginPercent[index] = null;
    }

    /// <summary>Rust <c>set_padding_side</c>.</summary>
    internal static void SetPaddingSide(LayoutStyle style, int index, string value)
    {
        string trimmed = value.Trim();
        if (DeferredLengthExpression(trimmed) is { } expression)
        {
            style.PaddingExpressions[index] = expression;
            style.PaddingPercent[index] = null;
            style.PaddingRelative[index] = null;
            style.Padding = SetEdge(style.Padding, index, 0f);
            return;
        }

        style.PaddingExpressions[index] = null;
        if (PercentFraction(trimmed) is { } fraction)
        {
            style.PaddingPercent[index] = fraction;
            style.PaddingRelative[index] = null;
            style.Padding = SetEdge(style.Padding, index, 0f);
            return;
        }

        Dimension dimension = DimensionValue(trimmed);
        switch (dimension.Kind)
        {
            case DimensionKind.Px:
                style.Padding = SetEdge(style.Padding, index, dimension.Value);
                style.PaddingRelative[index] = null;
                style.PaddingPercent[index] = null;
                break;
            case DimensionKind.Em:
            case DimensionKind.Ex:
            case DimensionKind.Rem:
            case DimensionKind.Vw:
            case DimensionKind.Vh:
            case DimensionKind.Vmin:
            case DimensionKind.Vmax:
                style.Padding = SetEdge(style.Padding, index, 0f);
                style.PaddingRelative[index] = dimension;
                style.PaddingPercent[index] = null;
                break;
        }
    }

    /// <summary>Rust <c>apply_padding_shorthand</c>.</summary>
    internal static void ApplyPaddingShorthand(LayoutStyle style, string value)
    {
        if (ExpandBoxShorthand(SplitWsParen(value)) is not { } sides)
        {
            return;
        }

        SetPaddingSide(style, 0, sides.Top);
        SetPaddingSide(style, 1, sides.Right);
        SetPaddingSide(style, 2, sides.Bottom);
        SetPaddingSide(style, 3, sides.Left);
    }

    /// <summary>Rust <c>apply_margin_shorthand</c>.</summary>
    internal static void ApplyMarginShorthand(LayoutStyle style, string value)
    {
        if (ExpandBoxShorthand(SplitWsParen(value)) is not { } sides)
        {
            return;
        }

        SetMarginSide(style, 0, sides.Top);
        SetMarginSide(style, 1, sides.Right);
        SetMarginSide(style, 2, sides.Bottom);
        SetMarginSide(style, 3, sides.Left);
    }

    // --------------------------------------------------------------- fonts

    /// <summary>Rust <c>apply_font_size</c>.</summary>
    internal static void ApplyFontSize(LayoutStyle style, string value)
    {
        string trimmed = value.Trim();
        switch (CssText.AsciiLower(trimmed))
        {
            // font-size is inherited; unset therefore has inherit semantics.
            case "inherit":
            case "unset":
                style.FontSize = null;
                style.FontSizeRaw = null;
                style.FontSizeExpression = null;
                return;
            case "initial":
                style.FontSize = 16.0f;
                style.FontSizeRaw = null;
                style.FontSizeExpression = null;
                return;
            // The compact cascade does not retain origin/layer history.
            case "revert":
            case "revert-layer":
                return;
        }

        if (trimmed.Contains('('))
        {
            style.FontSize = null;
            style.FontSizeRaw = null;
            style.FontSizeExpression = trimmed;
            return;
        }

        style.FontSizeExpression = null;
        Dimension dimension = DimensionValue(trimmed);
        if (dimension.Kind == DimensionKind.Px)
        {
            style.FontSize = dimension.Value;
            style.FontSizeRaw = null;
        }
        else if (dimension.IsAuto)
        {
            if (FontSizeKeyword(trimmed) is { } keyword)
            {
                style.FontSize = keyword;
                style.FontSizeRaw = null;
            }
        }
        else
        {
            style.FontSize = null;
            style.FontSizeRaw = dimension;
        }
    }

    /// <summary>Rust <c>apply_letter_spacing</c>.</summary>
    internal static void ApplyLetterSpacing(LayoutStyle style, string value)
    {
        string trimmed = value.Trim();
        string lower = CssText.AsciiLower(trimmed);
        if (lower is "normal" or "initial" or "revert" or "revert-layer")
        {
            style.LetterSpacing = 0f;
            style.LetterSpacingRaw = null;
            style.LetterSpacingExpression = null;
            style.LetterSpacingNonNormal = false;
            return;
        }

        // `letter-spacing` inherits, so `unset` behaves like `inherit`.
        if (lower is "inherit" or "unset" || trimmed.Length == 0)
        {
            return;
        }

        // Percentages are invalid even inside CSS math.
        if (trimmed.Contains('%'))
        {
            return;
        }

        style.LetterSpacingNonNormal = true;
        if (trimmed.Contains('('))
        {
            style.LetterSpacing = null;
            style.LetterSpacingRaw = null;
            style.LetterSpacingExpression = trimmed;
            return;
        }

        style.LetterSpacingExpression = null;
        Dimension dimension = DimensionValue(trimmed);
        if (dimension.Kind == DimensionKind.Px && float.IsFinite(dimension.Value))
        {
            style.LetterSpacing = dimension.Value;
            style.LetterSpacingRaw = null;
        }
        else if (dimension.Kind is DimensionKind.Percent or DimensionKind.Auto)
        {
            style.LetterSpacing = null;
            style.LetterSpacingRaw = null;
            style.LetterSpacingNonNormal = null;
        }
        else
        {
            style.LetterSpacing = null;
            style.LetterSpacingRaw = dimension;
        }
    }

    /// <summary>Rust <c>apply_text_indent</c>.</summary>
    internal static void ApplyTextIndent(LayoutStyle style, string value)
    {
        switch (CssText.AsciiLower(value.Trim()))
        {
            case "initial":
                style.TextIndent = Dimension.Px(0f);
                break;
            case "inherit":
            case "unset":
            case "revert":
            case "revert-layer":
                style.TextIndent = null;
                break;
            default:
                if (ParseTextIndent(value) is { } indent)
                {
                    style.TextIndent = indent;
                }

                break;
        }
    }

    /// <summary>Rust <c>apply_gap_value</c>.</summary>
    internal static void ApplyGapValue(LayoutStyle style, bool row, string value)
    {
        string trimmed = value.Trim();
        string lower = CssText.AsciiLower(trimmed);
        bool contextual = lower.Contains('(') || lower.EndsWith('%');
        if (!contextual)
        {
            foreach (string unit in GapUnits)
            {
                if (lower.EndsWith(unit, StringComparison.Ordinal))
                {
                    contextual = true;
                    break;
                }
            }
        }

        string? expression = CssText.EqualsAscii(trimmed, "normal") || trimmed.Length == 0 || !contextual
            ? null
            : trimmed;
        float? immediate = CssText.EqualsAscii(trimmed, "normal") || trimmed.Length == 0
            ? null
            : Px(trimmed);
        if (row)
        {
            style.RowGap = immediate;
            style.RowGapExpression = expression;
        }
        else
        {
            style.ColumnGap = immediate;
            style.ColumnGapExpression = expression;
        }
    }

    private static readonly string[] GapUnits = ["rem", "em", "ex", "vw", "vh", "vmin", "vmax"];

    /// <summary>Rust <c>apply_font_shorthand</c>.</summary>
    internal static void ApplyFontShorthand(LayoutStyle style, string value)
    {
        List<string> tokens = SplitWsParen(value);
        int sizeIndex = -1;
        string size = string.Empty;
        string attachedLineHeight = string.Empty;
        for (int index = 0; index < tokens.Count; index++)
        {
            string token = tokens[index];
            int slash = token.IndexOf('/');
            string candidate = slash >= 0 ? token[..slash] : token;
            string lineHeight = slash >= 0 ? token[(slash + 1)..] : string.Empty;
            if (IsFontSizeToken(candidate))
            {
                sizeIndex = index;
                size = candidate;
                attachedLineHeight = lineHeight;
                break;
            }
        }

        if (sizeIndex < 0)
        {
            return;
        }

        int familyIndex = sizeIndex + 1;
        string? lineHeightValue = attachedLineHeight.Length != 0 ? attachedLineHeight : null;
        if (lineHeightValue is null && familyIndex < tokens.Count)
        {
            if (tokens[familyIndex] == "/")
            {
                familyIndex++;
                if (familyIndex < tokens.Count)
                {
                    lineHeightValue = tokens[familyIndex];
                    familyIndex++;
                }
            }
            else if (tokens[familyIndex].StartsWith('/'))
            {
                string afterSlash = tokens[familyIndex][1..];
                if (afterSlash.Length != 0)
                {
                    lineHeightValue = afterSlash;
                }

                familyIndex++;
            }
        }

        if (familyIndex >= tokens.Count)
        {
            return;
        }

        // The shorthand resets every constituent before applying supplied values.
        style.FontStyleItalic = false;
        style.FontWeight = "400";
        style.FontOpticalSizing = Obscura.Render.FontOpticalSizing.Auto;
        style.FontVariationSettings = [];
        style.LineHeight = Obscura.Render.LineHeight.Normal;
        style.LineHeightExpression = null;
        for (int index = 0; index < sizeIndex; index++)
        {
            string lower = CssText.AsciiLower(tokens[index]);
            if (lower == "italic" || lower.StartsWith("oblique", StringComparison.Ordinal))
            {
                style.FontStyleItalic = true;
            }
            else if (SpecifiedFontWeight(lower) is { } weight)
            {
                style.FontWeight = weight;
            }
        }

        ApplyFontSize(style, size);
        if (lineHeightValue is { } resolvedLineHeight)
        {
            ApplyValue(style, "line-height", resolvedLineHeight);
        }

        style.FontFamily = CssText.AsciiLower(
            string.Join(" ", tokens.GetRange(familyIndex, tokens.Count - familyIndex)));
    }

    // -------------------------------------------------- declaration dispatch

    /// <summary>Rust <c>apply_value</c>: the property dispatcher.</summary>
    internal static void ApplyValue(LayoutStyle style, string name, string value)
    {
        // Rust's `apply_value` returns early from four arms, skipping the
        // trailing line-clamp adjustment; `false` reproduces that exactly.
        if (ApplyValueInner(style, name, value))
        {
            NormalizeWebkitLineClampDisplay(style);
        }
    }

    private static bool ApplyValueInner(LayoutStyle style, string name, string value)
    {
        switch (name)
        {
            case "direction":
                switch (CssText.AsciiLower(value.Trim()))
                {
                    case "ltr":
                    case "initial":
                    case "revert":
                    case "revert-layer":
                        style.Direction = Layout.Direction.Ltr;
                        break;
                    case "rtl":
                        style.Direction = Layout.Direction.Rtl;
                        break;
                    case "inherit":
                    case "unset":
                        style.Direction = null;
                        break;
                    default:
                        return false;
                }

                ResolveLogicalBorders(style);
                return true;

            case "display":
                ApplyDisplay(style, value);
                return true;

            case "container-type":
                if (CssText.EqualsAscii(value, "inherit"))
                {
                    style.ContainerType = ContainerType.Normal;
                    style.ContainerTypeInherit = true;
                }
                else if (ParseContainerType(value) is { } containerKind)
                {
                    style.ContainerType = containerKind;
                    style.ContainerTypeInherit = false;
                }

                return true;

            case "container-name":
                if (CssText.EqualsAscii(value, "inherit"))
                {
                    style.ContainerNames.Clear();
                    style.ContainerNamesInherit = true;
                }
                else if (ParseContainerNames(value) is { } names)
                {
                    style.ContainerNames = names;
                    style.ContainerNamesInherit = false;
                }

                return true;

            case "container":
                if (CssText.EqualsAscii(value, "inherit"))
                {
                    style.ContainerNames.Clear();
                    style.ContainerType = ContainerType.Normal;
                    style.ContainerNamesInherit = true;
                    style.ContainerTypeInherit = true;
                }
                else if (ParseContainerShorthand(value) is { } shorthand)
                {
                    style.ContainerNames = shorthand.Names;
                    style.ContainerType = shorthand.Kind;
                    style.ContainerNamesInherit = false;
                    style.ContainerTypeInherit = false;
                }

                return true;

            // The renderer supports the initial horizontal-tb writing mode, where
            // logical inline/block sizing is exactly the physical width/height pair.
            case "width":
            case "inline-size":
                style.Width = DimensionValue(value);
                style.WidthFitContent = CssText.EqualsAscii(value.Trim(), "fit-content");
                style.SizeExpressions[0] = DeferredLengthExpression(value);
                style.WidthSet = true;
                return true;

            case "height":
            case "block-size":
                style.Height = DimensionValue(value);
                style.SizeExpressions[1] = DeferredLengthExpression(value);
                style.HeightSet = true;
                return true;

            case "box-sizing":
            {
                string trimmed = value.Trim();
                if (CssText.EqualsAscii(trimmed, "border-box"))
                {
                    style.BoxSizing = BoxSizing.BorderBox;
                }
                else if (CssText.EqualsAscii(trimmed, "content-box")
                    || CssText.EqualsAscii(trimmed, "initial")
                    || CssText.EqualsAscii(trimmed, "unset")
                    || CssText.EqualsAscii(trimmed, "revert")
                    || CssText.EqualsAscii(trimmed, "revert-layer"))
                {
                    style.BoxSizing = BoxSizing.ContentBox;
                }
                else if (CssText.EqualsAscii(trimmed, "inherit"))
                {
                    style.BoxSizing = BoxSizing.Inherit;
                }

                return true;
            }

            case "min-width":
            case "min-inline-size":
                style.MinWidth = DimensionValue(value);
                style.SizeExpressions[2] = DeferredLengthExpression(value);
                return true;

            case "min-height":
            case "min-block-size":
                style.MinHeight = DimensionValue(value);
                style.SizeExpressions[3] = DeferredLengthExpression(value);
                return true;

            case "max-width":
            case "max-inline-size":
                style.MaxWidth = DimensionValue(value);
                style.SizeExpressions[4] = DeferredLengthExpression(value);
                return true;

            case "max-height":
            case "max-block-size":
                style.MaxHeight = DimensionValue(value);
                style.SizeExpressions[5] = DeferredLengthExpression(value);
                return true;

            case "aspect-ratio":
                style.AspectRatio = ParseAspectRatio(value);
                style.AspectRatioIsMapped = false;
                style.AspectRatioIsIntrinsic = false;
                return true;

            case "margin":
                ApplyMarginShorthand(style, value);
                return true;
            case "margin-top":
                SetMarginSide(style, 0, value);
                return true;
            case "margin-right":
                SetMarginSide(style, 1, value);
                return true;
            case "margin-bottom":
                SetMarginSide(style, 2, value);
                return true;
            case "margin-left":
                SetMarginSide(style, 3, value);
                return true;
            case "margin-inline":
            {
                (string start, string end) = Two(value);
                SetMarginSide(style, 3, start);
                SetMarginSide(style, 1, end);
                return true;
            }

            case "margin-inline-start":
                SetMarginSide(style, 3, value);
                return true;
            case "margin-inline-end":
                SetMarginSide(style, 1, value);
                return true;
            case "margin-block":
            {
                (string start, string end) = Two(value);
                SetMarginSide(style, 0, start);
                SetMarginSide(style, 2, end);
                return true;
            }

            case "margin-block-start":
                SetMarginSide(style, 0, value);
                return true;
            case "margin-block-end":
                SetMarginSide(style, 2, value);
                return true;

            case "padding":
                ApplyPaddingShorthand(style, value);
                return true;
            case "padding-top":
                SetPaddingSide(style, 0, value);
                return true;
            case "padding-right":
                SetPaddingSide(style, 1, value);
                return true;
            case "padding-bottom":
                SetPaddingSide(style, 2, value);
                return true;
            case "padding-left":
                SetPaddingSide(style, 3, value);
                return true;
            case "padding-inline":
            {
                (string start, string end) = Two(value);
                SetPaddingSide(style, 3, start);
                SetPaddingSide(style, 1, end);
                return true;
            }

            case "padding-inline-start":
                SetPaddingSide(style, 3, value);
                return true;
            case "padding-inline-end":
                SetPaddingSide(style, 1, value);
                return true;
            case "padding-block":
            {
                (string start, string end) = Two(value);
                SetPaddingSide(style, 0, start);
                SetPaddingSide(style, 2, end);
                return true;
            }

            case "padding-block-start":
                SetPaddingSide(style, 0, value);
                return true;
            case "padding-block-end":
                SetPaddingSide(style, 2, value);
                return true;

            case "border-radius":
                ApplyBorderRadiusShorthand(style, value);
                return true;
            case "border-top-left-radius":
                SetCornerRadius(style, 0, value);
                return true;
            case "border-top-right-radius":
                SetCornerRadius(style, 1, value);
                return true;
            case "border-bottom-right-radius":
                SetCornerRadius(style, 2, value);
                return true;
            case "border-bottom-left-radius":
                SetCornerRadius(style, 3, value);
                return true;

            case "clip-path":
            case "-webkit-clip-path":
                if (CssText.AsciiLower(value.Trim()) is "none" or "initial" or "unset" or "revert" or "revert-layer")
                {
                    style.ClipPath = null;
                }
                else if (ParseClipPathPolygon(value) is { } polygon)
                {
                    style.ClipPath = polygon;
                }

                return true;

            case "border":
                ApplyBorderShorthand(style, null, value);
                return true;
            case "border-top":
                ApplyBorderShorthand(style, Side.Top, value);
                return true;
            case "border-right":
                ApplyBorderShorthand(style, Side.Right, value);
                return true;
            case "border-bottom":
                ApplyBorderShorthand(style, Side.Bottom, value);
                return true;
            case "border-left":
                ApplyBorderShorthand(style, Side.Left, value);
                return true;
            case "border-width":
                ApplyBorderWidths(style, value);
                return true;
            case "border-top-width":
                SetBorderWidth(style, Side.Top, value);
                return true;
            case "border-right-width":
                SetBorderWidth(style, Side.Right, value);
                return true;
            case "border-bottom-width":
                SetBorderWidth(style, Side.Bottom, value);
                return true;
            case "border-left-width":
                SetBorderWidth(style, Side.Left, value);
                return true;
            case "border-style":
                ApplyBorderStyles(style, value);
                return true;
            case "border-top-style":
                SetBorderStyle(style, Side.Top, value);
                return true;
            case "border-right-style":
                SetBorderStyle(style, Side.Right, value);
                return true;
            case "border-bottom-style":
                SetBorderStyle(style, Side.Bottom, value);
                return true;
            case "border-left-style":
                SetBorderStyle(style, Side.Left, value);
                return true;
            case "border-color":
                ApplyBorderColors(style, value);
                return true;
            case "border-top-color":
                SetBorderColor(style, Side.Top, value);
                return true;
            case "border-right-color":
                SetBorderColor(style, Side.Right, value);
                return true;
            case "border-bottom-color":
                SetBorderColor(style, Side.Bottom, value);
                return true;
            case "border-left-color":
                SetBorderColor(style, Side.Left, value);
                return true;

            case "border-inline":
            case "border-block":
            case "border-inline-start":
            case "border-inline-end":
            case "border-block-start":
            case "border-block-end":
            case "border-inline-width":
            case "border-inline-style":
            case "border-inline-color":
            case "border-block-width":
            case "border-block-style":
            case "border-block-color":
            case "border-inline-start-width":
            case "border-inline-start-style":
            case "border-inline-start-color":
            case "border-inline-end-width":
            case "border-inline-end-style":
            case "border-inline-end-color":
            case "border-block-start-width":
            case "border-block-start-style":
            case "border-block-start-color":
            case "border-block-end-width":
            case "border-block-end-style":
            case "border-block-end-color":
                ApplyLogicalBorder(style, name, value);
                return true;

            case "outline":
                ApplyOutlineShorthand(style, value);
                return true;
            case "outline-width":
                SetOutlineWidth(style, value);
                return true;
            case "outline-style":
                SetOutlineStyle(style, value);
                return true;
            case "outline-color":
                SetOutlineColor(style, value);
                return true;
            case "outline-offset":
                SetOutlineOffset(style, value);
                return true;

            case "background-color":
                style.BackgroundColor = CssColor.ParseForScheme(value, style.ColorSchemeDark);
                return true;

            case "background":
                // A shorthand resets every omitted background longhand. An empty
                // value is invalid (an unresolved var()), so keep the prior winner.
                if (value.Trim().Length != 0)
                {
                    style.BackgroundColor = null;
                    SetBackgroundGradients(style, value);
                    if (style.BackgroundGradient is null
                        && style.BackgroundRadialGradient is null
                        && style.BackgroundConicGradient is null)
                    {
                        style.BackgroundColor = CssColor.ParseForScheme(value, style.ColorSchemeDark);
                    }

                    style.BackgroundImage = ParseUrl(value);
                    style.BackgroundSize = null;
                    style.BackgroundSizeExpression = BackgroundSizeExpression(value);
                    style.BackgroundSizeFit = ParseBackgroundSizeFit(value);
                    style.BackgroundPosition = BackgroundPosition.Zero;
                    style.BackgroundRepeat = ParseImageRepeat(value);
                    style.BackgroundOrigin = BackgroundOrigin.PaddingBox;
                    style.BackgroundClip = BackgroundClip.BorderBox;
                    if (ParseBackgroundBoxShorthand(value) is { } boxes)
                    {
                        style.BackgroundOrigin = boxes.Origin;
                        style.BackgroundClip = boxes.Clip;
                    }

                    style.BackgroundClipText = style.BackgroundClip == BackgroundClip.Text;
                }

                return true;

            case "background-image":
                SetBackgroundGradients(style, value);
                style.BackgroundImage = ParseUrl(value);
                return true;

            case "background-size":
                style.BackgroundSize = ParseBackgroundSize(value);
                style.BackgroundSizeExpression = value.Trim().Length != 0 ? value.Trim() : null;
                style.BackgroundSizeFit = ParseBackgroundSizeFit(value);
                return true;

            case "background-position":
                style.BackgroundPosition = ParseBackgroundPosition(value);
                return true;

            case "background-repeat":
                style.BackgroundRepeat = ParseImageRepeat(value);
                return true;

            case "background-origin":
                style.BackgroundOrigin = ParseBackgroundOrigin(value) ?? BackgroundOrigin.PaddingBox;
                return true;

            // On replaced elements, an image-valued `content` replaces the source.
            case "content":
                style.ContentImage = ParseUrl(value);
                return true;

            case "mask-image":
            case "-webkit-mask-image":
                style.MaskImage = ParseUrl(value);
                return true;
            case "mask-size":
            case "-webkit-mask-size":
                style.MaskSize = ParseBackgroundSize(value);
                return true;
            case "mask-repeat":
            case "-webkit-mask-repeat":
                style.MaskRepeat = ParseImageRepeat(value);
                return true;

            case "background-clip":
            case "-webkit-background-clip":
                style.BackgroundClip = ParseBackgroundClip(value) ?? BackgroundClip.BorderBox;
                style.BackgroundClipText = style.BackgroundClip == BackgroundClip.Text;
                return true;

            // Blink/WebKit gradient text makes the glyph fill transparent through
            // this inherited property, so the vendor fill color wins at paint time.
            case "color":
            case "-webkit-text-fill-color":
                style.Color = CssColor.ParseForScheme(value, style.ColorSchemeDark);
                return true;

            case "fill":
                style.SvgFill = ResolveSvgPresentationColor(value, style.ColorSchemeDark);
                return true;
            case "stroke":
                style.SvgStroke = ResolveSvgPresentationColor(value, style.ColorSchemeDark);
                return true;
            case "stroke-width":
                style.SvgStrokeWidth = value.Trim();
                return true;

            case "font-size":
                ApplyFontSize(style, value);
                return true;
            case "letter-spacing":
                ApplyLetterSpacing(style, value);
                return true;
            case "font":
                ApplyFontShorthand(style, value);
                return true;

            case "font-weight":
                switch (CssText.AsciiLower(value.Trim()))
                {
                    case "inherit":
                    case "unset":
                        style.FontWeight = "inherit";
                        break;
                    case "initial":
                        style.FontWeight = "400";
                        break;
                    case "revert":
                    case "revert-layer":
                        break;
                    default:
                        if (SpecifiedFontWeight(CssText.AsciiLower(value.Trim())) is { } weight)
                        {
                            style.FontWeight = weight;
                        }

                        break;
                }

                return true;

            case "font-family":
            {
                string family = CssText.AsciiLower(value.Trim());
                if (family.Length != 0 && family != "inherit")
                {
                    style.FontFamily = family;
                }

                return true;
            }

            case "font-optical-sizing":
                switch (CssText.AsciiLower(value.Trim()))
                {
                    case "inherit":
                    case "unset":
                    case "revert":
                    case "revert-layer":
                        style.FontOpticalSizing = null;
                        break;
                    case "auto":
                    case "initial":
                        style.FontOpticalSizing = Obscura.Render.FontOpticalSizing.Auto;
                        break;
                    case "none":
                        style.FontOpticalSizing = Obscura.Render.FontOpticalSizing.None;
                        break;
                }

                return true;

            case "font-variation-settings":
            {
                string lower = CssText.AsciiLower(value.Trim());
                if (lower is "inherit" or "unset" or "revert" or "revert-layer")
                {
                    style.FontVariationSettings = null;
                }
                else if (lower is "normal" or "initial")
                {
                    style.FontVariationSettings = [];
                }
                else if (ParseFontVariationSettings(value) is { } settings)
                {
                    style.FontVariationSettings = settings;
                }

                return true;
            }

            case "text-align":
                switch (value)
                {
                    case "right":
                    case "end":
                        style.TextAlign = Layout.AlignItems.FlexEnd;
                        style.LegacyCenter = false;
                        break;
                    case "center":
                        style.TextAlign = Layout.AlignItems.Center;
                        style.LegacyCenter = false;
                        break;
                    case "left":
                    case "start":
                    case "justify":
                        style.TextAlign = Layout.AlignItems.FlexStart;
                        style.LegacyCenter = false;
                        break;
                }

                return true;

            case "text-indent":
                ApplyTextIndent(style, value);
                return true;

            case "align-items":
            {
                (Layout.AlignItems? alignment, bool valid) = SelfAlignmentValue(value);
                if (valid && alignment is { } parsed)
                {
                    style.AlignItems = parsed;
                }

                return true;
            }

            case "justify-items":
            {
                (Layout.AlignItems? alignment, bool valid) = SelfAlignmentValue(value);
                if (valid && alignment is { } parsed)
                {
                    style.JustifyItems = parsed;
                }

                return true;
            }

            case "place-items":
            {
                (Layout.AlignItems? align, Layout.AlignItems? justify, bool valid) = SelfAlignmentPair(value);
                if (valid && align is { } parsedAlign && justify is { } parsedJustify)
                {
                    style.AlignItems = parsedAlign;
                    style.JustifyItems = parsedJustify;
                }

                return true;
            }

            case "align-self":
            {
                (Layout.AlignItems? alignment, bool valid) = SelfAlignmentValue(value);
                if (valid)
                {
                    style.AlignSelf = alignment;
                }

                return true;
            }

            case "justify-self":
            {
                (Layout.AlignItems? alignment, bool valid) = SelfAlignmentValue(value);
                if (valid)
                {
                    style.JustifySelf = alignment;
                }

                return true;
            }

            case "place-self":
            {
                (Layout.AlignItems? align, Layout.AlignItems? justify, bool valid) = SelfAlignmentPair(value);
                if (valid)
                {
                    style.AlignSelf = align;
                    style.JustifySelf = justify;
                }

                return true;
            }

            case "align-content":
                if (ContentAlignmentValue(value) is { } alignContent)
                {
                    style.AlignContent = alignContent;
                }

                return true;

            case "justify-content":
            {
                Layout.AlignContent? justifyContent = CssText.AsciiLower(value.Trim()) switch
                {
                    "left" => Layout.AlignContent.Start,
                    "right" => Layout.AlignContent.End,
                    _ => ContentAlignmentValue(value),
                };
                if (justifyContent is { } parsed)
                {
                    style.JustifyContent = parsed;
                }

                return true;
            }

            case "place-content":
                if (ContentAlignmentPair(value) is { } pair)
                {
                    style.AlignContent = pair.Align;
                    style.JustifyContent = pair.Justify;
                }

                return true;

            case "flex-flow":
                if (ParseFlexFlowShorthand(value) is { } flow)
                {
                    // A shorthand always assigns both longhands.
                    style.FlexDirection = flow.Direction;
                    style.FlexWrap = flow.Wrap;
                }

                return true;

            case "flex-direction":
                switch (value)
                {
                    case "row":
                        style.FlexDirection = Layout.FlexDirection.Row;
                        break;
                    case "row-reverse":
                        style.FlexDirection = Layout.FlexDirection.RowReverse;
                        break;
                    case "column":
                        style.FlexDirection = Layout.FlexDirection.Column;
                        break;
                    case "column-reverse":
                        style.FlexDirection = Layout.FlexDirection.ColumnReverse;
                        break;
                }

                return true;

            case "flex-wrap":
                switch (value)
                {
                    case "wrap":
                        style.FlexWrap = Layout.FlexWrap.Wrap;
                        break;
                    case "nowrap":
                        style.FlexWrap = Layout.FlexWrap.NoWrap;
                        break;
                    case "wrap-reverse":
                        style.FlexWrap = Layout.FlexWrap.WrapReverse;
                        break;
                }

                return true;

            case "flex-grow":
                if (Token(value) is { } growToken && ParseF32(growToken) is { } grow)
                {
                    style.FlexGrow = grow;
                }

                return true;

            case "flex-shrink":
                if (Token(value) is { } shrinkToken && ParseF32(shrinkToken) is { } shrink)
                {
                    style.FlexShrink = shrink;
                }

                return true;

            case "order":
                if (ParseI32(value.Trim()) is { } order)
                {
                    style.Order = order;
                }

                return true;

            case "flex-basis":
                style.FlexBasis = DimensionValue(value.Trim());
                return true;

            case "flex":
                ParseFlexShorthand(style, value);
                return true;

            case "position":
                switch (value)
                {
                    case "absolute":
                        style.Position = Layout.Position.Absolute;
                        style.PositionFixed = false;
                        style.PositionSticky = false;
                        break;
                    case "fixed":
                        style.Position = Layout.Position.Absolute;
                        style.PositionFixed = true;
                        style.PositionSticky = false;
                        break;
                    case "relative":
                        style.Position = Layout.Position.Relative;
                        style.PositionFixed = false;
                        style.PositionSticky = false;
                        break;
                    case "sticky":
                        style.Position = Layout.Position.Relative;
                        style.PositionFixed = false;
                        style.PositionSticky = true;
                        break;
                    case "static":
                        style.Position = null;
                        style.PositionFixed = false;
                        style.PositionSticky = false;
                        break;
                }

                return true;

            case "float":
                switch (value)
                {
                    case "left":
                        style.Float = Obscura.Render.Float.Left;
                        break;
                    case "right":
                        style.Float = Obscura.Render.Float.Right;
                        break;
                    case "none":
                        style.Float = null;
                        break;
                }

                return true;

            case "counter-reset":
                if (ParseCounterDirectives(value, 0) is { } counterReset)
                {
                    style.CounterReset = counterReset;
                }

                return true;

            case "counter-increment":
                if (ParseCounterDirectives(value, 1) is { } counterIncrement)
                {
                    style.CounterIncrement = counterIncrement;
                }

                return true;

            case "counter-set":
                if (ParseCounterDirectives(value, 0) is { } counterSet)
                {
                    style.CounterSet = counterSet;
                }

                return true;

            case "object-fit":
                switch (CssText.AsciiLower(value.Trim()))
                {
                    case "fill":
                        style.ObjectFit = ObjectFit.Fill;
                        break;
                    case "contain":
                        style.ObjectFit = ObjectFit.Contain;
                        break;
                    case "cover":
                        style.ObjectFit = ObjectFit.Cover;
                        break;
                    case "scale-down":
                        style.ObjectFit = ObjectFit.ScaleDown;
                        break;
                    case "none":
                        style.ObjectFit = ObjectFit.None;
                        break;
                }

                return true;

            case "object-position":
            {
                BackgroundPosition position = ParseBackgroundPosition(value);
                style.ObjectPosition = new ObjectPosition(position.X, position.Y);
                return true;
            }

            case "top":
                SetInsetSide(style, 0, value);
                return true;
            case "right":
                SetInsetSide(style, 1, value);
                return true;
            case "bottom":
                SetInsetSide(style, 2, value);
                return true;
            case "left":
                SetInsetSide(style, 3, value);
                return true;

            case "inset-inline":
            {
                (string start, string end) = Two(value);
                SetInsetSide(style, 3, start);
                SetInsetSide(style, 1, end);
                return true;
            }

            case "inset-inline-start":
                SetInsetSide(style, 3, value);
                return true;
            case "inset-inline-end":
                SetInsetSide(style, 1, value);
                return true;
            case "inset-block":
            {
                (string start, string end) = Two(value);
                SetInsetSide(style, 0, start);
                SetInsetSide(style, 2, end);
                return true;
            }

            case "inset-block-start":
                SetInsetSide(style, 0, value);
                return true;
            case "inset-block-end":
                SetInsetSide(style, 2, value);
                return true;

            case "inset":
            {
                if (ExpandBoxShorthand(SplitWsParen(value)) is not { } sides)
                {
                    return false;
                }

                SetInsetSide(style, 0, sides.Top);
                SetInsetSide(style, 1, sides.Right);
                SetInsetSide(style, 2, sides.Bottom);
                SetInsetSide(style, 3, sides.Left);
                return true;
            }

            case "overflow":
            case "overflow-x":
            case "overflow-y":
            {
                (ParsedOverflowAxis? x, ParsedOverflowAxis? y, bool valid) =
                    ParseOverflowDeclaration(name, value);
                if (!valid)
                {
                    return false;
                }

                if (x is { } axisX)
                {
                    style.OverflowSpecifiedX = axisX.Specified;
                    style.OverflowInheritX = axisX.Inherit;
                }

                if (y is { } axisY)
                {
                    style.OverflowSpecifiedY = axisY.Specified;
                    style.OverflowInheritY = axisY.Inherit;
                }

                style.OverflowAxesSet = true;
                RecomputeOverflow(style);
                return true;
            }

            case "scrollbar-gutter":
            {
                List<string> tokens = SplitWhitespace(CssText.AsciiLower(value));
                style.ScrollbarGutters = tokens.Contains("stable")
                    ? tokens.Contains("both-edges") ? (byte)2 : (byte)1
                    : (byte)0;
                return true;
            }

            case "visibility":
                style.VisibilityHidden = CssText.EqualsAscii(value, "hidden");
                return true;

            case "opacity":
                style.Opacity = ParseF32(value.Trim());
                return true;

            case "animation":
                ApplyAnimationShorthand(style, value);
                return true;

            case "animation-name":
            {
                string first = FirstAnimationValue(value);
                style.AnimationName = first.Length == 0
                    || CssText.AsciiLower(first) is "none" or "initial" or "inherit" or "unset"
                        or "revert" or "revert-layer"
                    ? null
                    : first;
                return true;
            }

            case "animation-duration":
            {
                string first = FirstAnimationValue(value);
                if (CssText.AsciiLower(first) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
                {
                    style.AnimationTiming = style.AnimationTiming with { DurationMs = 0f };
                }
                else if (ParseAnimationTimeMs(first) is { } milliseconds && milliseconds >= 0f)
                {
                    style.AnimationTiming = style.AnimationTiming with { DurationMs = milliseconds };
                }

                return true;
            }

            case "animation-delay":
            {
                string first = FirstAnimationValue(value);
                if (CssText.AsciiLower(first) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
                {
                    style.AnimationTiming = style.AnimationTiming with { DelayMs = 0f };
                }
                else if (ParseAnimationTimeMs(first) is { } milliseconds)
                {
                    style.AnimationTiming = style.AnimationTiming with { DelayMs = milliseconds };
                }

                return true;
            }

            case "animation-fill-mode":
            {
                string first = FirstAnimationValue(value);
                if (CssText.AsciiLower(first) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
                {
                    style.AnimationTiming = style.AnimationTiming with { FillMode = AnimationFillMode.None };
                }
                else if (ParseAnimationFillMode(first) is { } fill)
                {
                    style.AnimationTiming = style.AnimationTiming with { FillMode = fill };
                }

                return true;
            }

            case "animation-iteration-count":
            {
                string first = FirstAnimationValue(value);
                if (CssText.AsciiLower(first) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
                {
                    style.AnimationTiming = style.AnimationTiming with { IterationCount = 1f };
                }
                else if (ParseAnimationIterationCount(first) is { } iterations)
                {
                    style.AnimationTiming = style.AnimationTiming with { IterationCount = iterations };
                }

                return true;
            }

            case "animation-direction":
            {
                string first = FirstAnimationValue(value);
                if (CssText.AsciiLower(first) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
                {
                    style.AnimationTiming = style.AnimationTiming with { Direction = AnimationDirection.Normal };
                }
                else if (ParseAnimationDirection(first) is { } direction)
                {
                    style.AnimationTiming = style.AnimationTiming with { Direction = direction };
                }

                return true;
            }

            case "animation-play-state":
            {
                string first = FirstAnimationValue(value);
                if (CssText.AsciiLower(first) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
                {
                    style.AnimationTiming = style.AnimationTiming with { PlayState = AnimationPlayState.Running };
                }
                else if (ParseAnimationPlayState(first) is { } playState)
                {
                    style.AnimationTiming = style.AnimationTiming with { PlayState = playState };
                }

                return true;
            }

            case "z-index":
                style.ZIndex = value.Trim() switch
                {
                    "auto" or "inherit" or "initial" => null,
                    var text => ParseI32(text),
                };
                return true;

            case "clear":
                style.Clear = CssText.AsciiLower(value.Trim()) switch
                {
                    "left" or "inline-start" => Obscura.Render.Clear.Left,
                    "right" or "inline-end" => Obscura.Render.Clear.Right,
                    "both" => Obscura.Render.Clear.Both,
                    _ => null,
                };
                return true;

            case "vertical-align":
                style.VerticalAlign = CssText.AsciiLower(value.Trim()) switch
                {
                    "top" or "baseline" or "text-top" => Obscura.Render.VerticalAlign.Top,
                    "middle" => Obscura.Render.VerticalAlign.Middle,
                    "bottom" or "text-bottom" => Obscura.Render.VerticalAlign.Bottom,
                    // sub/super/lengths are text-level; leave the cell default.
                    _ => style.VerticalAlign,
                };
                return true;

            case "list-style-type":
                style.ListStyle = ListStyleKeyword(value.Trim()) ?? Obscura.Render.ListStyle.Disc;
                return true;

            case "list-style":
                // Shorthand: type | position | image in any order.
                foreach (string token in SplitWhitespace(value))
                {
                    if (ListStyleKeyword(token) is { } listStyle)
                    {
                        style.ListStyle = listStyle;
                    }
                }

                return true;

            case "line-height":
            {
                string trimmed = value.Trim();
                if (trimmed.Contains('('))
                {
                    style.LineHeight = null;
                    style.LineHeightExpression = trimmed;
                    return false;
                }

                style.LineHeightExpression = null;
                if (CssText.EqualsAscii(trimmed, "normal"))
                {
                    style.LineHeight = Obscura.Render.LineHeight.Normal;
                }
                else if (trimmed.EndsWith('%'))
                {
                    style.LineHeight = ParseF32(trimmed[..^1].Trim()) is { } percentage
                        ? Obscura.Render.LineHeight.Relative(Dimension.Percent(percentage / 100f))
                        : null;
                }
                else if (trimmed.EndsWith("px", StringComparison.Ordinal)
                    || trimmed.EndsWith("pt", StringComparison.Ordinal))
                {
                    style.LineHeight = PxValue(trimmed) is { } pixels
                        ? Obscura.Render.LineHeight.Px(pixels)
                        : null;
                }
                else if (EndsWithLineHeightUnit(trimmed))
                {
                    style.LineHeight = Obscura.Render.LineHeight.Relative(DimensionValue(trimmed));
                }
                else
                {
                    // Unitless number: a multiple of font-size.
                    style.LineHeight = ParseF32(trimmed) is { } ratio
                        ? Obscura.Render.LineHeight.Ratio(ratio)
                        : null;
                }

                return true;
            }

            case "white-space":
                style.WhiteSpace = CssText.AsciiLower(value.Trim()) switch
                {
                    "normal" or "initial" or "revert" or "revert-layer" => Obscura.Render.WhiteSpace.Normal,
                    "nowrap" => Obscura.Render.WhiteSpace.NoWrap,
                    "pre" => Obscura.Render.WhiteSpace.Pre,
                    "pre-wrap" => Obscura.Render.WhiteSpace.PreWrap,
                    "pre-line" => Obscura.Render.WhiteSpace.PreLine,
                    "break-spaces" => Obscura.Render.WhiteSpace.BreakSpaces,
                    // `white-space` inherits, so unset behaves as inherit.
                    "inherit" or "unset" => null,
                    _ => style.WhiteSpace,
                };
                return true;

            case "text-overflow":
                style.TextOverflow = CssText.AsciiLower(value.Trim()) switch
                {
                    "ellipsis" => TextOverflow.Ellipsis,
                    "clip" or "initial" or "unset" or "revert" or "revert-layer" => TextOverflow.Clip,
                    _ => style.TextOverflow,
                };
                return true;

            case "-webkit-line-clamp":
            {
                (bool valid, uint? lines) = WebkitLineClampValue(value);
                if (valid)
                {
                    style.WebkitLineClamp = lines;
                }

                return true;
            }

            case "-webkit-box-orient":
                style.WebkitBoxOrientVertical = CssText.AsciiLower(value.Trim()) switch
                {
                    "vertical" or "block-axis" => true,
                    "horizontal" or "inline-axis" or "initial" or "unset" or "revert" or "revert-layer" => false,
                    _ => style.WebkitBoxOrientVertical,
                };
                return true;

            case "overflow-wrap":
            case "word-wrap":
                style.OverflowWrap = CssText.AsciiLower(value.Trim()) switch
                {
                    "normal" or "initial" => OverflowWrap.Normal,
                    "break-word" => OverflowWrap.BreakWord,
                    "anywhere" => OverflowWrap.Anywhere,
                    "inherit" or "unset" or "revert" or "revert-layer" => null,
                    _ => style.OverflowWrap,
                };
                return true;

            case "word-break":
                style.WordBreak = CssText.AsciiLower(value.Trim()) switch
                {
                    "normal" or "initial" => WordBreak.Normal,
                    "break-all" => WordBreak.BreakAll,
                    "keep-all" => WordBreak.KeepAll,
                    "break-word" => WordBreak.BreakWord,
                    "inherit" or "unset" or "revert" or "revert-layer" => null,
                    _ => style.WordBreak,
                };
                return true;

            case "text-wrap":
                style.TextWrapStyle = CssText.AsciiLower(value.Trim()) switch
                {
                    "auto" or "wrap" or "initial" or "revert" or "revert-layer" => TextWrapStyle.Auto,
                    "balance" or "wrap balance" or "balance wrap" => TextWrapStyle.Balance,
                    "inherit" or "unset" => null,
                    _ => style.TextWrapStyle,
                };
                return true;

            case "text-wrap-style":
                style.TextWrapStyle = CssText.AsciiLower(value.Trim()) switch
                {
                    "auto" or "initial" or "revert" or "revert-layer" => TextWrapStyle.Auto,
                    "balance" => TextWrapStyle.Balance,
                    "inherit" or "unset" => null,
                    _ => style.TextWrapStyle,
                };
                return true;

            case "font-style":
            {
                string lower = CssText.AsciiLower(value.Trim());
                style.FontStyleItalic = lower.StartsWith("italic", StringComparison.Ordinal)
                    || lower.StartsWith("oblique", StringComparison.Ordinal);
                return true;
            }

            case "text-transform":
                style.TextTransform = CssText.AsciiLower(value.Trim()) switch
                {
                    "uppercase" => TextTransform.Uppercase,
                    "lowercase" => TextTransform.Lowercase,
                    "capitalize" => TextTransform.Capitalize,
                    _ => TextTransform.None,
                };
                return true;

            case "text-decoration":
            case "text-decoration-line":
            {
                // We only model the underline line.
                bool underline = false;
                bool none = false;
                foreach (string token in SplitWhitespace(value))
                {
                    string lower = CssText.AsciiLower(token);
                    if (lower == "underline")
                    {
                        underline = true;
                    }
                    else if (lower == "none")
                    {
                        none = true;
                    }
                }

                style.Underline = underline && !none;
                return true;
            }

            case "gap":
            case "grid-gap":
            {
                List<string> values = SplitWsParen(value);
                if (values.Count > 0)
                {
                    ApplyGapValue(style, true, values[0]);
                    ApplyGapValue(style, false, values.Count > 1 ? values[1] : values[0]);
                }

                return true;
            }

            case "row-gap":
            case "grid-row-gap":
                ApplyGapValue(style, true, value);
                return true;

            case "column-gap":
            case "grid-column-gap":
            case "-webkit-column-gap":
                ApplyGapValue(style, false, value);
                return true;

            case "column-count":
            case "-webkit-column-count":
                style.ColumnCount = ParseColumnCount(value);
                return true;

            case "columns":
            case "-webkit-columns":
            {
                // `columns` is `column-width || column-count`.
                ushort? count = null;
                foreach (string token in SplitWsParen(value))
                {
                    if (ParseColumnCount(token) is { } parsed)
                    {
                        count = parsed;
                        break;
                    }
                }

                style.ColumnCount = count;
                return true;
            }

            case "break-inside":
                style.BreakInsideAvoid = CssText.AsciiLower(value.Trim()) is "avoid" or "avoid-column";
                return true;

            case "-webkit-column-break-inside":
                style.BreakInsideAvoid = CssText.EqualsAscii(value.Trim(), "avoid");
                return true;

            case "border-spacing":
            {
                List<float> dimensions = [];
                foreach (string token in SplitWhitespace(value))
                {
                    if (PxValue(token) is { } pixels)
                    {
                        dimensions.Add(pixels);
                    }
                }

                if (dimensions.Count > 0)
                {
                    style.BorderSpacing = (dimensions[0], dimensions.Count > 1 ? dimensions[1] : dimensions[0]);
                }

                return true;
            }

            case "border-collapse":
                style.BorderCollapse = CssText.AsciiLower(value.Trim()) switch
                {
                    "collapse" => true,
                    "separate" or "initial" or "revert" or "revert-layer" => false,
                    "inherit" or "unset" => null,
                    _ => style.BorderCollapse,
                };
                return true;

            case "table-layout":
                switch (CssText.AsciiLower(value.Trim()))
                {
                    case "fixed":
                        style.TableLayoutFixed = true;
                        break;
                    case "auto":
                    case "initial":
                    case "unset":
                    case "revert":
                    case "revert-layer":
                        style.TableLayoutFixed = false;
                        break;
                }

                return true;

            case "grid-template-columns":
            {
                (List<Layout.GridTemplateComponent> tracks,
                    List<(string Name, short Line)> names,
                    List<object> calcExpressions) = ParseTrackListNamed(value);
                style.GridTemplateColumnsSubgrid = IsSubgridTrackList(value);
                style.GridTemplateColumns = tracks;
                GridCalcBuckets(style)[0] = calcExpressions;
                style.GridColLineNames = names.Count != 0 ? BuildLineMap(names) : null;
                return true;
            }

            case "grid-template-rows":
            {
                (List<Layout.GridTemplateComponent> tracks,
                    List<(string Name, short Line)> names,
                    List<object> calcExpressions) = ParseTrackListNamed(value);
                style.GridTemplateRows = tracks;
                GridCalcBuckets(style)[1] = calcExpressions;
                style.GridRowLineNames = names.Count != 0 ? BuildLineMap(names) : null;
                return true;
            }

            case "grid-auto-columns":
                ApplyGridAutoTracks(style, value, true);
                return true;
            case "grid-auto-rows":
                ApplyGridAutoTracks(style, value, false);
                return true;
            case "grid-template-areas":
                style.GridAreas = ParseGridAreas(value);
                return true;
            case "grid-template":
                ParseGridTemplate(style, value);
                return true;
            case "grid":
                ParseGridShorthand(style, value);
                return true;
            case "grid-auto-flow":
                style.GridAutoFlow = ParseGridAutoFlow(value);
                return true;
            case "grid-area":
                SetGridArea(style, value);
                return true;
            case "grid-column":
                SetGridPlacement(style, value, true);
                return true;
            case "grid-row":
                SetGridPlacement(style, value, false);
                return true;
            case "grid-column-start":
                SetGridPlacementSide(style, value, true, true);
                return true;
            case "grid-column-end":
                SetGridPlacementSide(style, value, true, false);
                return true;
            case "grid-row-start":
                SetGridPlacementSide(style, value, false, true);
                return true;
            case "grid-row-end":
                SetGridPlacementSide(style, value, false, false);
                return true;

            case "transform":
                ParseTransform(style, value);
                return true;
            case "transform-origin":
                style.TransformOrigin = ParseTransformOrigin(value);
                return true;
            case "translate":
                ParseIndividualTranslate(style, value);
                return true;
            case "rotate":
                ParseIndividualRotate(style, value);
                return true;
            case "scale":
                ParseIndividualScale(style, value);
                return true;

            case "filter":
                SetContainingBlockTrigger(style, ContainingBlockTrigger.Filter, NonNoneValue(value));
                return true;
            case "backdrop-filter":
            case "-webkit-backdrop-filter":
                SetContainingBlockTrigger(style, ContainingBlockTrigger.BackdropFilter, NonNoneValue(value));
                return true;
            case "perspective":
                SetContainingBlockTrigger(style, ContainingBlockTrigger.Perspective, NonNoneValue(value));
                return true;

            case "contain":
            {
                bool establishes = false;
                foreach (string token in SplitWhitespace(value))
                {
                    if (CssText.AsciiLower(token) is "layout" or "paint" or "strict" or "content")
                    {
                        establishes = true;
                        break;
                    }
                }

                SetContainingBlockTrigger(style, ContainingBlockTrigger.Contain, establishes);
                return true;
            }

            case "will-change":
            {
                bool establishes = false;
                foreach (string token in value.Split([',', ' ']))
                {
                    if (CssText.AsciiLower(token.Trim())
                        is "transform" or "filter" or "backdrop-filter" or "perspective" or "contain")
                    {
                        establishes = true;
                        break;
                    }
                }

                SetContainingBlockTrigger(style, ContainingBlockTrigger.WillChange, establishes);
                return true;
            }

            case "content-visibility":
                SetContainingBlockTrigger(
                    style,
                    ContainingBlockTrigger.ContentVisibility,
                    CssText.EqualsAscii(value.Trim(), "auto"));
                return true;

            case "box-shadow":
            case "-webkit-box-shadow":
                style.BoxShadow = ParseBoxShadow(value, style.Color, style.ColorSchemeDark);
                return true;

            default:
                return true;
        }
    }

    private static bool EndsWithLineHeightUnit(string value)
    {
        foreach (string unit in RelativeLineHeightUnits)
        {
            if (value.EndsWith(unit, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] RelativeLineHeightUnits =
        ["rem", "em", "ex", "vw", "vh", "vmin", "vmax"];

    private static string FirstAnimationValue(string value)
    {
        List<string> layers = SplitTopLevel(value, ',');
        return (layers.Count > 0 ? layers[0] : string.Empty).Trim();
    }

    private static void ApplyDisplay(LayoutStyle style, string rawValue)
    {
        string value = CssText.AsciiLower(rawValue.Trim());
        if (value is "none" or "flex" or "inline-flex" or "inline" or "inline-block" or "grid"
            or "inline-grid" or "block" or "flow-root" or "table" or "inline-table" or "table-cell"
            or "-webkit-box" or "-webkit-inline-box" or "contents" or "inherit" or "initial" or "unset")
        {
            // Every valid authored display value replaces the complete outer/inner
            // display pair, including the UA table/control approximation.
            style.InternalFlexContainer = false;
            style.IsTableBox = false;
            style.IsTableCellBox = false;
            style.IsInlineBlock = false;
            style.FlowRoot = false;
            style.DisplayContents = false;
            style.DisplayInherit = false;
            style.WebkitBoxDisplay = null;
        }

        switch (value)
        {
            case "none":
                style.Display = Display.None;
                break;
            case "flex":
                style.Display = Display.Flex;
                break;
            case "inline-flex":
                style.Display = Display.Flex;
                style.IsInlineBlock = true;
                break;
            case "inline":
                style.Display = Display.Inline;
                break;
            case "inline-block":
                style.Display = Display.Inline;
                style.IsInlineBlock = true;
                break;
            case "grid":
                style.Display = Display.Grid;
                break;
            case "inline-grid":
                style.Display = Display.Grid;
                style.IsInlineBlock = true;
                break;
            case "block":
                style.Display = Display.Block;
                break;
            case "flow-root":
                style.Display = Display.Block;
                style.FlowRoot = true;
                break;
            // taffy has no table formatting mode; preserve the outer-display and
            // BFC semantics instead of retaining a stale earlier winner.
            case "table":
                style.Display = Display.Block;
                style.FlowRoot = true;
                style.IsTableBox = true;
                break;
            case "inline-table":
                style.Display = Display.Inline;
                style.IsInlineBlock = true;
                style.FlowRoot = true;
                style.IsTableBox = true;
                break;
            case "table-cell":
                style.Display = Display.Flex;
                style.InternalFlexContainer = true;
                style.FlexDirection = Layout.FlexDirection.Column;
                style.AlignItems = Layout.AlignItems.FlexStart;
                style.IsTableCellBox = true;
                break;
            case "-webkit-box":
                style.Display = Display.Flex;
                style.WebkitBoxDisplay = false;
                break;
            case "-webkit-inline-box":
                style.Display = Display.Flex;
                style.IsInlineBlock = true;
                style.WebkitBoxDisplay = true;
                break;
            case "contents":
                // `display:contents` can override an earlier `display:none`.
                style.Display = Display.Block;
                style.DisplayContents = true;
                break;
            case "inherit":
                style.Display = Display.Inline;
                style.DisplayInherit = true;
                break;
            case "initial":
            case "unset":
                // `display` is not inherited and its CSS initial value is `inline`.
                style.Display = Display.Inline;
                break;
        }
    }
}
