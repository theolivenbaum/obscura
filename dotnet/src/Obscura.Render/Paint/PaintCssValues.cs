// Port of the CSSOM serialization helpers in crates/obscura-render/src/paint.rs
// (`css_number` .. `transform_origin_css`).
using System.Globalization;
using Obscura.Render.Layout;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

internal static class PaintCssValues
{
    internal static string CssNumber(float value) =>
        value == 0f ? "0" : value.ToString(CultureInfo.InvariantCulture);

    internal static string CssPx(float value) => CssNumber(value == 0f ? 0f : value) + "px";

    internal static string DimensionCss(Dimension value, string auto) => value.Kind switch
    {
        DimensionKind.Auto => auto,
        DimensionKind.Px => CssPx(value.Value),
        DimensionKind.Percent => CssNumber(value.Value * 100f) + "%",
        DimensionKind.Em => CssNumber(value.Value) + "em",
        DimensionKind.Ex => CssNumber(value.Value) + "ex",
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
                $"rgba({color.R}, {color.G}, {color.B}, {CssNumber(color.A / 255f)})");

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
}
