// Port of the CSSOM serialization helpers in crates/obscura-render/src/paint.rs
// (`css_number` .. `transform_origin_css`).
using System.Globalization;
using System.Text;
using Obscura.Render.Layout;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal static class PaintCssValues
{
    /// <summary>
    /// CSSOM number serialization: six significant digits with trailing zeros truncated.
    /// </summary>
    /// <remarks>
    /// DEVIATION FROM RUST: <c>crates/obscura-render/src/paint.rs</c> writes an <c>f32</c> with
    /// Rust's <c>Display</c>, i.e. the shortest decimal that round-trips, so a line-height of
    /// <c>11px * 1.3</c> serializes as <c>14.299999px</c>. Blink serializes a CSS number through
    /// WTF's <c>String::Number</c>, which is <c>%.6g</c> with trailing zeros truncated, and
    /// reports <c>14.3px</c>. Page script string-compares computed values, so the port follows
    /// Chromium here rather than Rust.
    /// </remarks>
    internal static string CssNumber(float value)
    {
        if (value == 0f)
        {
            return "0";
        }

        if (!float.IsFinite(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        string text = value.ToString("G6", CultureInfo.InvariantCulture);
        int exponent = text.IndexOf('E', StringComparison.Ordinal);

        // .NET spells the exponent `E+07`; C's `%g`, and therefore Blink, spells it `e+07`.
        return exponent < 0
            ? text
            : string.Concat(text.AsSpan(0, exponent), "e", text.AsSpan(exponent + 1));
    }

    internal static string CssPx(float value) => CssNumber(value == 0f ? 0f : value) + "px";

    internal static string DimensionCss(Dimension value, string auto) => value.Kind switch
    {
        DimensionKind.Auto => auto,
        DimensionKind.Px => CssPx(value.Value),
        DimensionKind.Percent => CssNumber(value.Value * 100f) + "%",
        DimensionKind.Em => CssNumber(value.Value) + "em",
        DimensionKind.Ex => CssNumber(value.Value) + "ex",
        DimensionKind.Ch => CssNumber(value.Value) + "ch",
        DimensionKind.Rem => CssNumber(value.Value) + "rem",
        DimensionKind.Vw => CssNumber(value.Value) + "vw",
        DimensionKind.Vh => CssNumber(value.Value) + "vh",
        DimensionKind.Vmin => CssNumber(value.Value) + "vmin",
        _ => CssNumber(value.Value) + "vmax",
    };

    internal static string RadiusValueCss(RadiusValue value) => (value.Length, value.Percentage) switch
    {
        (var length, 0f) => CssPx(length),
        (0f, var percentage) => CssNumber(percentage * 100f) + "%",
        var (length, percentage) =>
            "calc(" + CssPx(length) + " + " + CssNumber(percentage * 100f) + "%)",
    };

    internal static string CornerRadiusCss(CornerRadius radius)
    {
        string x = RadiusValueCss(radius.X);
        string y = RadiusValueCss(radius.Y);
        return string.Equals(x, y, StringComparison.Ordinal) ? x : x + " " + y;
    }

    internal static string CssColor(RgbaColor color) =>
        color.A == 255
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"rgb({color.R}, {color.G}, {color.B})")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"rgba({color.R}, {color.G}, {color.B}, {CssAlpha(color.A)})");

    /// <summary>
    /// The <c>color(srgb r g b [/ a])</c> serialization Chromium uses for a colour that is not
    /// in the legacy sRGB space, with channels as 0-1 numbers.
    /// </summary>
    internal static string SrgbFunctionColor(RgbaColor color)
    {
        string channels = CssNumber(color.R / 255f)
            + " " + CssNumber(color.G / 255f)
            + " " + CssNumber(color.B / 255f);

        return color.A == 255
            ? "color(srgb " + channels + ")"
            : "color(srgb " + channels + " / " + CssAlpha(color.A) + ")";
    }

    /// <summary>
    /// Serialize an 8-bit alpha the way Blink's <c>Color::SerializeAsCSSColor</c> does:
    /// the shortest decimal with at most three fraction digits that quantizes back to the
    /// same byte.
    /// </summary>
    /// <remarks>
    /// Storing alpha in 8 bits is correct and matches Blink - Chromium reports
    /// <c>rgba(1, 2, 3, 0.9999)</c> as the opaque <c>rgb(1, 2, 3)</c>, so it quantizes
    /// too. What was wrong was emitting the raw <c>A / 255</c> ratio afterwards, which
    /// turned an authored <c>rgba(4, 67, 211, 0.1)</c> into
    /// <c>rgba(4, 67, 211, 0.10196079)</c>. Searching short decimals first recovers the
    /// authored spelling without having to carry a float alpha through the paint stack.
    ///
    /// Three fraction digits always terminate the search: the 0.001 grid is finer than
    /// the 1/255 step between adjacent byte alphas, so some decimal on it always
    /// round-trips. Verified against Chromium for all 256 values.
    /// </remarks>
    internal static string CssAlpha(byte alpha)
    {
        for (int places = 0, scale = 1; places <= 3; places++, scale *= 10)
        {
            int digits = (int)F32.Round(alpha * scale / 255f);
            if ((int)F32.Round(digits * 255f / scale) != alpha)
            {
                continue;
            }

            if (places == 0)
            {
                return digits.ToString(CultureInfo.InvariantCulture);
            }

            string fraction = digits
                .ToString(CultureInfo.InvariantCulture)
                .PadLeft(places, '0')
                .TrimEnd('0');

            return fraction.Length == 0 ? "0" : "0." + fraction;
        }

        return CssNumber(alpha / 255f);
    }

    internal static string AlignItemsCss(AlignItems value)
    {
        string keyword = value.Keyword switch
        {
            AlignItemsKeyword.Normal => "normal",
            AlignItemsKeyword.Start => "start",
            AlignItemsKeyword.End => "end",
            AlignItemsKeyword.FlexStart => "flex-start",
            AlignItemsKeyword.FlexEnd => "flex-end",
            AlignItemsKeyword.Center => "center",
            AlignItemsKeyword.Baseline => "baseline",
            _ => "stretch",
        };
        return value.Safety == AlignmentSafety.Safe ? "safe " + keyword : keyword;
    }

    internal static string AlignContentCss(AlignContent value)
    {
        string keyword = value.Keyword switch
        {
            AlignContentKeyword.Start => "start",
            AlignContentKeyword.End => "end",
            AlignContentKeyword.FlexStart => "flex-start",
            AlignContentKeyword.FlexEnd => "flex-end",
            AlignContentKeyword.Center => "center",
            AlignContentKeyword.Stretch => "stretch",
            AlignContentKeyword.SpaceBetween => "space-between",
            AlignContentKeyword.SpaceEvenly => "space-evenly",
            _ => "space-around",
        };
        return value.Safety == AlignmentSafety.Safe ? "safe " + keyword : keyword;
    }

    internal static string TransformCss(
        LayoutStyle style,
        Rect? rect,
        float rootFontSize,
        (float Width, float Height) viewport)
    {
        if (style.TransformOps.Count == 0)
        {
            return "none";
        }

        Rect reference = rect ?? default;
        Affine2 matrix = DomTransforms.ResolvedTransformPropertyMatrix(
            style,
            reference,
            rootFontSize,
            viewport);
        return "matrix("
            + CssNumber(matrix.A) + ", "
            + CssNumber(matrix.B) + ", "
            + CssNumber(matrix.C) + ", "
            + CssNumber(matrix.D) + ", "
            + CssNumber(matrix.E) + ", "
            + CssNumber(matrix.F) + ")";
    }

    internal static string TransformOriginCss(LayoutStyle style, Rect? rect)
    {
        (Dimension x, Dimension y) = style.TransformOrigin
            ?? (Dimension.Percent(0.5f), Dimension.Percent(0.5f));
        float width = rect?.Width ?? 0f;
        float height = rect?.Height ?? 0f;
        return Resolve(x, width) + " " + Resolve(y, height);

        static string Resolve(Dimension value, float axis) =>
            value.Kind == DimensionKind.Percent
                ? CssPx(value.Value * axis)
                : DimensionCss(value, "0px");
    }

    /// <summary>
    /// Serialize a computed <c>filter</c> list the way Blink's
    /// <c>ComputedStyleUtils::ValueForFilter</c> does.
    /// </summary>
    /// <remarks>
    /// Three details are Blink's and were measured against Chromium 141 rather than inferred
    /// from the grammar, because all three differ from the authored spelling:
    /// <list type="bullet">
    /// <item>The <c>drop-shadow()</c> colour comes <em>first</em> and is always present, even
    /// when the author wrote it last or omitted it - the computed value has already resolved
    /// <c>currentColor</c>. This is the opposite of the shadow order everywhere else in CSS,
    /// and the reason <c>box-shadow</c> above cannot share this code.</item>
    /// <item>All three <c>drop-shadow()</c> lengths are emitted, so an omitted blur reports as
    /// <c>0px</c>.</item>
    /// <item>The multiplier functions report a number, never a percentage:
    /// <c>brightness(50%)</c> computes to <c>brightness(0.5)</c>. <c>hue-rotate()</c> is the
    /// exception that keeps a unit, always <c>deg</c>.</item>
    /// </list>
    /// </remarks>
    internal static string FilterCss(FilterFunction[]? filter)
    {
        if (filter is null || filter.Length == 0)
        {
            return "none";
        }

        StringBuilder output = new();
        foreach (FilterFunction function in filter)
        {
            if (output.Length != 0)
            {
                output.Append(' ');
            }

            switch (function.Kind)
            {
                case FilterFunctionKind.Blur:
                    output.Append("blur(").Append(CssPx(function.Amount)).Append(')');
                    break;

                case FilterFunctionKind.HueRotate:
                    output.Append("hue-rotate(").Append(CssNumber(function.Amount)).Append("deg)");
                    break;

                case FilterFunctionKind.DropShadow:
                    output.Append("drop-shadow(")
                        .Append(CssColor(function.Color))
                        .Append(' ').Append(CssPx(function.OffsetX))
                        .Append(' ').Append(CssPx(function.OffsetY))
                        .Append(' ').Append(CssPx(function.ShadowBlur))
                        .Append(')');
                    break;

                case FilterFunctionKind.Reference:
                    output.Append("url(\"").Append(function.Reference).Append("\")");
                    break;

                default:
                    output.Append(FilterFunctionName(function.Kind))
                        .Append('(')
                        .Append(CssNumber(function.Amount))
                        .Append(')');
                    break;
            }
        }

        return output.ToString();
    }

    private static string FilterFunctionName(FilterFunctionKind kind) => kind switch
    {
        FilterFunctionKind.Brightness => "brightness",
        FilterFunctionKind.Contrast => "contrast",
        FilterFunctionKind.Grayscale => "grayscale",
        FilterFunctionKind.Invert => "invert",
        FilterFunctionKind.Opacity => "opacity",
        FilterFunctionKind.Saturate => "saturate",
        _ => "sepia",
    };
}
