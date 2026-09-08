// RECONCILIATION NOTE: this file ports `crates/obscura-render/src/border.rs`, which is owned
// by the border component agent. `LayoutStyle` has `BorderModel` and `OutlineModel` fields, so
// the types could not be deferred. The port is complete and faithful (including the border.rs
// unit tests, in RenderCoreTests.cs); the border agent should adopt this file rather than
// adding a second copy under a different folder.
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>Four physical sides in CSS top-right-bottom-left order.</summary>
public readonly record struct Sides<T>(T Top, T Right, T Bottom, T Left)
{
    public static Sides<T> All(T value) => new(value, value, value, value);

    public Sides<TOut> Map<TOut>(Func<T, TOut> f) => new(f(Top), f(Right), f(Bottom), f(Left));

    public T[] AsArray() => [Top, Right, Bottom, Left];
}

/// <summary>Border and outline helpers shared by style, layout, and paint.</summary>
public static class BorderSides
{
    /// <summary>CSS's initial <c>medium</c> border and outline width in CSS pixels.</summary>
    public const float MediumBorderWidth = 3.0f;

    /// <summary>Expand a CSS 1-4 value list in top-right-bottom-left order.</summary>
    public static Sides<T>? ExpandSides<T>(ReadOnlySpan<T> values) => values.Length switch
    {
        1 => Sides<T>.All(values[0]),
        2 => new Sides<T>(values[0], values[1], values[0], values[1]),
        3 => new Sides<T>(values[0], values[1], values[2], values[1]),
        4 => new Sides<T>(values[0], values[1], values[2], values[3]),
        _ => null,
    };

    /// <summary>Expand a CSS 1-4 value list in top-right-bottom-left order.</summary>
    public static Sides<T>? ExpandSides<T>(IReadOnlyList<T> values)
    {
        T[] copy = new T[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            copy[i] = values[i];
        }

        return ExpandSides<T>(copy.AsSpan());
    }

    internal static float UsedWidth(float specified, BorderStyle style) =>
        style.IsVisible() ? F32.Max(specified, 0f) : 0f;
}

/// <summary>The CSS border-line styles represented by the renderer.</summary>
public enum BorderStyle
{
    /// <summary>The default.</summary>
    None = 0,

    Hidden,
    Dotted,
    Dashed,
    Solid,
    Double,
    Groove,
    Ridge,
    Inset,
    Outset,

    /// <summary><c>outline-style:auto</c>; border sides themselves never use this value.</summary>
    Auto,
}

public static class BorderStyleExtensions
{
    /// <summary>Whether this style contributes a used width and visible border paint.</summary>
    public static bool IsVisible(this BorderStyle style) =>
        style is not (BorderStyle.None or BorderStyle.Hidden);

    public static string CssName(this BorderStyle style) => style switch
    {
        BorderStyle.None => "none",
        BorderStyle.Hidden => "hidden",
        BorderStyle.Dotted => "dotted",
        BorderStyle.Dashed => "dashed",
        BorderStyle.Solid => "solid",
        BorderStyle.Double => "double",
        BorderStyle.Groove => "groove",
        BorderStyle.Ridge => "ridge",
        BorderStyle.Inset => "inset",
        BorderStyle.Outset => "outset",
        BorderStyle.Auto => "auto",
        _ => "none",
    };
}

/// <summary>One CSS <c>&lt;length-percentage&gt;</c> component of a corner radius.</summary>
public readonly record struct RadiusValue(float Length, float Percentage)
{
    public static RadiusValue Pixels(float length) => new(length, 0f);

    /// <summary>Fraction of the corresponding border-box axis (<c>0.5</c> is <c>50%</c>).</summary>
    public static RadiusValue FromPercentage(float percentage) => new(0f, percentage);

    public float Resolve(float axis) => F32.Max(Length + (Percentage * axis), 0f);

    public bool IsZero() => Length == 0f && Percentage == 0f;
}

/// <summary>Horizontal and vertical radii for one corner.</summary>
public readonly record struct CornerRadius(RadiusValue X, RadiusValue Y)
{
    public static CornerRadius Circular(RadiusValue value) => new(value, value);
}

/// <summary>Four elliptical radii in clockwise order from the top-left corner.</summary>
public readonly record struct BorderRadii(
    CornerRadius TopLeft,
    CornerRadius TopRight,
    CornerRadius BottomRight,
    CornerRadius BottomLeft)
{
    public static BorderRadii All(CornerRadius radius) => new(radius, radius, radius, radius);

    /// <summary>
    /// Resolve percentages and apply CSS Backgrounds 3's corner-overlap rule. One common scale
    /// factor preserves every ellipse's aspect ratio.
    /// </summary>
    public ResolvedBorderRadii Resolve(float width, float height)
    {
        ResolvedBorderRadii resolved = new(
            ResolveCorner(TopLeft, width, height),
            ResolveCorner(TopRight, width, height),
            ResolveCorner(BottomRight, width, height),
            ResolveCorner(BottomLeft, width, height));

        static float Ratio(float available, float requested) =>
            requested > 0f ? F32.Max(available, 0f) / requested : 1f;

        float scale = F32.Min(
            F32.Min(
                F32.Min(
                    F32.Min(1f, Ratio(width, resolved.TopLeft.X + resolved.TopRight.X)),
                    Ratio(width, resolved.BottomLeft.X + resolved.BottomRight.X)),
                Ratio(height, resolved.TopLeft.Y + resolved.BottomLeft.Y)),
            Ratio(height, resolved.TopRight.Y + resolved.BottomRight.Y));
        if (scale < 1f)
        {
            resolved = resolved.Scaled(scale);
        }

        return resolved;
    }

    public bool IsZero() =>
        TopLeft.X.IsZero()
        && TopLeft.Y.IsZero()
        && TopRight.X.IsZero()
        && TopRight.Y.IsZero()
        && BottomRight.X.IsZero()
        && BottomRight.Y.IsZero()
        && BottomLeft.X.IsZero()
        && BottomLeft.Y.IsZero();

    private static (float X, float Y) ResolveCorner(CornerRadius radius, float width, float height)
    {
        float x = radius.X.Resolve(width);
        float y = radius.Y.Resolve(height);
        // CSS treats a corner as square when either axis has a zero radius.
        return x <= float.Epsilon || y <= float.Epsilon ? (0f, 0f) : (x, y);
    }
}

/// <summary>
/// Final used radii in CSS pixels after percentage resolution and overlap scaling.
/// </summary>
public readonly record struct ResolvedBorderRadii(
    (float X, float Y) TopLeft,
    (float X, float Y) TopRight,
    (float X, float Y) BottomRight,
    (float X, float Y) BottomLeft)
{
    public ResolvedBorderRadii Scaled(float scale)
    {
        static (float X, float Y) ScaleCorner((float X, float Y) corner, float scale) =>
            (corner.X * scale, corner.Y * scale);

        return new ResolvedBorderRadii(
            ScaleCorner(TopLeft, scale),
            ScaleCorner(TopRight, scale),
            ScaleCorner(BottomRight, scale),
            ScaleCorner(BottomLeft, scale));
    }

    public bool IsZero() =>
        TopLeft == (0f, 0f)
        && TopRight == (0f, 0f)
        && BottomRight == (0f, 0f)
        && BottomLeft == (0f, 0f);

    public bool IsUniform() =>
        TopLeft == TopRight && TopRight == BottomRight && BottomRight == BottomLeft;

    /// <summary>Inner radii after removing the used border widths on each axis.</summary>
    public ResolvedBorderRadii Inset(Sides<float> widths) => new(
        ShrinkCorner(TopLeft, widths.Left, widths.Top),
        ShrinkCorner(TopRight, widths.Right, widths.Top),
        ShrinkCorner(BottomRight, widths.Right, widths.Bottom),
        ShrinkCorner(BottomLeft, widths.Left, widths.Bottom));

    /// <summary>Outer radii for an outline or shadow expanded from the border edge.</summary>
    public ResolvedBorderRadii Outset(Sides<float> amounts) => new(
        GrowCorner(TopLeft, amounts.Left, amounts.Top),
        GrowCorner(TopRight, amounts.Right, amounts.Top),
        GrowCorner(BottomRight, amounts.Right, amounts.Bottom),
        GrowCorner(BottomLeft, amounts.Left, amounts.Bottom));

    private static (float X, float Y) ShrinkCorner(
        (float X, float Y) corner,
        float horizontal,
        float vertical)
    {
        float x = F32.Max(corner.X - horizontal, 0f);
        float y = F32.Max(corner.Y - vertical, 0f);
        return x <= float.Epsilon || y <= float.Epsilon ? (0f, 0f) : (x, y);
    }

    private static (float X, float Y) GrowCorner(
        (float X, float Y) corner,
        float horizontal,
        float vertical)
    {
        // Expanding a square corner must keep it square. Only an existing curve receives the
        // extra outline/shadow distance on each axis.
        if (corner.X <= float.Epsilon || corner.Y <= float.Epsilon)
        {
            return (0f, 0f);
        }

        float x = F32.Max(corner.X + horizontal, 0f);
        float y = F32.Max(corner.Y + vertical, 0f);
        return x <= float.Epsilon || y <= float.Epsilon ? (0f, 0f) : (x, y);
    }
}

/// <summary>Four independent specified border widths, styles, and colors.</summary>
/// <remarks>
/// A <c>null</c> color means CSS <c>currentcolor</c>, not transparent or absent. Rust's
/// <c>Default</c> seeds every specified width to <see cref="BorderSides.MediumBorderWidth"/>,
/// so <c>default(BorderModel)</c> is NOT the CSS initial value; use <see cref="Default"/>.
/// </remarks>
public readonly record struct BorderModel(
    Sides<float> SpecifiedWidths,
    Sides<BorderStyle> Styles,
    Sides<RgbaColor?> Colors,
    BorderRadii Radii)
{
    /// <summary>The CSS initial border state: <c>medium none currentcolor</c> on all sides.</summary>
    public static readonly BorderModel Default = new(
        Sides<float>.All(BorderSides.MediumBorderWidth),
        Sides<BorderStyle>.All(BorderStyle.None),
        Sides<RgbaColor?>.All(null),
        default);

    /// <summary>Used widths after applying CSS Backgrounds 3's <c>none</c>/<c>hidden</c> rule.</summary>
    public Sides<float> UsedWidths() => new(
        BorderSides.UsedWidth(SpecifiedWidths.Top, Styles.Top),
        BorderSides.UsedWidth(SpecifiedWidths.Right, Styles.Right),
        BorderSides.UsedWidth(SpecifiedWidths.Bottom, Styles.Bottom),
        BorderSides.UsedWidth(SpecifiedWidths.Left, Styles.Left));
}

/// <summary>
/// Uniform outline state. Outlines paint outside the border edge and never participate in
/// layout geometry.
/// </summary>
/// <remarks>
/// Rust's <c>Default</c> seeds the specified width to
/// <see cref="BorderSides.MediumBorderWidth"/>, so <c>default(OutlineModel)</c> is NOT the CSS
/// initial value; use <see cref="Default"/>.
/// </remarks>
public readonly record struct OutlineModel(
    float SpecifiedWidth,
    BorderStyle Style,
    RgbaColor? Color,
    float Offset)
{
    /// <summary>The CSS initial outline state.</summary>
    public static readonly OutlineModel Default =
        new(BorderSides.MediumBorderWidth, BorderStyle.None, null, 0f);

    public float UsedWidth() => BorderSides.UsedWidth(SpecifiedWidth, Style);
}
