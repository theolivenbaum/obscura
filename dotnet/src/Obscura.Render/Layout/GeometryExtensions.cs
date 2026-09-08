// Concrete-type helpers for the generic geometry structs. In Rust these are
// `impl` blocks with trait bounds (Add/Sub/Copy) on geometry.rs; C# cannot
// express those bounds, so they are extension methods specialised on the
// element types taffy actually instantiates.
namespace Obscura.Render.Layout;

/// <summary>Arithmetic and axis helpers over the geometry primitives.</summary>
public static class GeometryExtensions
{
    // ---------------------------------------------------------------- Rect

    /// <summary>A rect with 0.0 on all four sides.</summary>
    public static Rect<float> RectZero => new(0f, 0f, 0f, 0f);

    /// <summary>Component-wise addition.</summary>
    public static Rect<float> Add(this Rect<float> a, Rect<float> b) =>
        new(a.Left + b.Left, a.Right + b.Right, a.Top + b.Top, a.Bottom + b.Bottom);

    /// <summary>The sum of the left and right fields. NOT the width of the rectangle.</summary>
    public static float HorizontalAxisSum(this Rect<float> r) => r.Left + r.Right;

    /// <summary>The sum of the top and bottom fields. NOT the height of the rectangle.</summary>
    public static float VerticalAxisSum(this Rect<float> r) => r.Top + r.Bottom;

    /// <summary>Both axis sums as a <see cref="Size{T}"/>.</summary>
    public static Size<float> SumAxes(this Rect<float> r) => new(r.HorizontalAxisSum(), r.VerticalAxisSum());

    /// <summary>Get either the horizontal or vertical sum depending on the axis passed in.</summary>
    public static float GridAxisSum(this Rect<float> r, AbsoluteAxis axis) =>
        axis == AbsoluteAxis.Horizontal ? r.Left + r.Right : r.Top + r.Bottom;

    /// <summary>The sum of the two fields of the rect representing the main axis.</summary>
    public static float MainAxisSum(this Rect<float> r, FlexDirection direction) =>
        direction.IsRow() ? r.HorizontalAxisSum() : r.VerticalAxisSum();

    /// <summary>The sum of the two fields of the rect representing the cross axis.</summary>
    public static float CrossAxisSum(this Rect<float> r, FlexDirection direction) =>
        direction.IsRow() ? r.VerticalAxisSum() : r.HorizontalAxisSum();

    /// <summary>The start value of the rect from the perspective of the main layout axis.</summary>
    public static T MainStart<T>(this Rect<T> r, FlexDirection direction) => direction.IsRow() ? r.Left : r.Top;

    /// <summary>The end value of the rect from the perspective of the main layout axis.</summary>
    public static T MainEnd<T>(this Rect<T> r, FlexDirection direction) => direction.IsRow() ? r.Right : r.Bottom;

    /// <summary>The start value of the rect from the perspective of the cross layout axis.</summary>
    public static T CrossStart<T>(this Rect<T> r, FlexDirection direction) => direction.IsRow() ? r.Top : r.Left;

    /// <summary>The end value of the rect from the perspective of the cross layout axis.</summary>
    public static T CrossEnd<T>(this Rect<T> r, FlexDirection direction) => direction.IsRow() ? r.Bottom : r.Right;

    /// <summary>A rect where all four sides are <see cref="Dimension.Auto"/>.</summary>
    public static Rect<Dimension> RectDimensionAuto =>
        new(Dimension.Auto, Dimension.Auto, Dimension.Auto, Dimension.Auto);

    /// <summary>A rect where all four sides are zero-length dimensions.</summary>
    public static Rect<Dimension> RectDimensionZero =>
        new(Dimension.Zero, Dimension.Zero, Dimension.Zero, Dimension.Zero);

    /// <summary>Create a new rect with length values.</summary>
    public static Rect<Dimension> RectFromLength(float start, float end, float top, float bottom) =>
        new(Dimension.FromLength(start), Dimension.FromLength(end), Dimension.FromLength(top),
            Dimension.FromLength(bottom));

    /// <summary>Create a new rect with percentage values.</summary>
    public static Rect<Dimension> RectFromPercent(float start, float end, float top, float bottom) =>
        new(Dimension.FromPercent(start), Dimension.FromPercent(end), Dimension.FromPercent(top),
            Dimension.FromPercent(bottom));

    // ---------------------------------------------------------------- Line

    /// <summary>A line with both start and end set to true.</summary>
    public static Line<bool> LineTrue => new(true, true);

    /// <summary>A line with both start and end set to false.</summary>
    public static Line<bool> LineFalse => new(false, false);

    /// <summary>Adds the start and end values together.</summary>
    public static float Sum(this Line<float> line) => line.Start + line.End;

    // ---------------------------------------------------------------- Size

    /// <summary>A size with zero width and height.</summary>
    public static Size<float> SizeZero => new(0f, 0f);

    /// <summary>A size with null width and height.</summary>
    public static Size<float?> SizeNone => new(null, null);

    /// <summary>A size with both axes set to <see cref="AvailableSpace.MaxContent"/>.</summary>
    public static Size<AvailableSpace> SizeMaxContent =>
        new(AvailableSpace.MaxContent, AvailableSpace.MaxContent);

    /// <summary>A size with both axes set to <see cref="AvailableSpace.MinContent"/>.</summary>
    public static Size<AvailableSpace> SizeMinContent =>
        new(AvailableSpace.MinContent, AvailableSpace.MinContent);

    /// <summary>A size where both axes are <see cref="Dimension.Auto"/>.</summary>
    public static Size<Dimension> SizeDimensionAuto => new(Dimension.Auto, Dimension.Auto);

    /// <summary>A size where both axes are zero-length dimensions.</summary>
    public static Size<Dimension> SizeDimensionZero => new(Dimension.Zero, Dimension.Zero);

    /// <summary>A size where both axes are zero-length length-percentages.</summary>
    public static Size<LengthPercentage> SizeLengthPercentageZero =>
        new(LengthPercentage.Zero, LengthPercentage.Zero);

    /// <summary>Generates a size using length values.</summary>
    public static Size<Dimension> SizeFromLengths(float width, float height) =>
        new(Dimension.FromLength(width), Dimension.FromLength(height));

    /// <summary>Generates a size using percentage values.</summary>
    public static Size<Dimension> SizeFromPercent(float width, float height) =>
        new(Dimension.FromPercent(width), Dimension.FromPercent(height));

    /// <summary>A size with both axes set.</summary>
    public static Size<float?> SizeSome(float width, float height) => new(width, height);

    /// <summary>Component-wise addition.</summary>
    public static Size<float> Add(this Size<float> a, Size<float> b) =>
        new(a.Width + b.Width, a.Height + b.Height);

    /// <summary>Component-wise subtraction.</summary>
    public static Size<float> Sub(this Size<float> a, Size<float> b) =>
        new(a.Width - b.Width, a.Height - b.Height);

    /// <summary>Applies <see cref="Sys.F32Max"/> to each component separately.</summary>
    public static Size<float> F32Max(this Size<float> a, Size<float> b) =>
        new(Sys.F32Max(a.Width, b.Width), Sys.F32Max(a.Height, b.Height));

    /// <summary>Applies <see cref="Sys.F32Min"/> to each component separately.</summary>
    public static Size<float> F32Min(this Size<float> a, Size<float> b) =>
        new(Sys.F32Min(a.Width, b.Width), Sys.F32Min(a.Height, b.Height));

    /// <summary>Return true if both width and height are greater than 0.</summary>
    public static bool HasNonZeroArea(this Size<float> s) => s.Width > 0.0f && s.Height > 0.0f;

    /// <summary>Wrap each component in a nullable.</summary>
    public static Size<float?> AsOptions(this Size<float> s) => new(s.Width, s.Height);

    /// <summary>Gets the extent of the main layout axis.</summary>
    public static T Main<T>(this Size<T> s, FlexDirection direction) => direction.IsRow() ? s.Width : s.Height;

    /// <summary>Gets the extent of the cross layout axis.</summary>
    public static T Cross<T>(this Size<T> s, FlexDirection direction) => direction.IsRow() ? s.Height : s.Width;

    /// <summary>Sets the extent of the main layout axis.</summary>
    public static void SetMain<T>(this ref Size<T> s, FlexDirection direction, T value)
    {
        if (direction.IsRow())
        {
            s.Width = value;
        }
        else
        {
            s.Height = value;
        }
    }

    /// <summary>Sets the extent of the cross layout axis.</summary>
    public static void SetCross<T>(this ref Size<T> s, FlexDirection direction, T value)
    {
        if (direction.IsRow())
        {
            s.Height = value;
        }
        else
        {
            s.Width = value;
        }
    }

    /// <summary>Returns a copy with the main axis set to the value provided.</summary>
    public static Size<T> WithMain<T>(this Size<T> s, FlexDirection direction, T value)
    {
        var copy = s;
        copy.SetMain(direction, value);
        return copy;
    }

    /// <summary>Returns a copy with the cross axis set to the value provided.</summary>
    public static Size<T> WithCross<T>(this Size<T> s, FlexDirection direction, T value)
    {
        var copy = s;
        copy.SetCross(direction, value);
        return copy;
    }

    /// <summary>Returns a copy with the main axis modified by the callback provided.</summary>
    public static Size<T> MapMain<T>(this Size<T> s, FlexDirection direction, Func<T, T> mapper)
    {
        var copy = s;
        if (direction.IsRow())
        {
            copy.Width = mapper(copy.Width);
        }
        else
        {
            copy.Height = mapper(copy.Height);
        }

        return copy;
    }

    /// <summary>Returns a copy with the cross axis modified by the callback provided.</summary>
    public static Size<T> MapCross<T>(this Size<T> s, FlexDirection direction, Func<T, T> mapper)
    {
        var copy = s;
        if (direction.IsRow())
        {
            copy.Height = mapper(copy.Height);
        }
        else
        {
            copy.Width = mapper(copy.Width);
        }

        return copy;
    }

    /// <summary>Creates a size with either the width or height set based on the provided direction.</summary>
    public static Size<float?> SizeFromCross(FlexDirection direction, float? value)
    {
        var result = SizeNone;
        if (direction.IsRow())
        {
            result.Height = value;
        }
        else
        {
            result.Width = value;
        }

        return result;
    }

    /// <summary>Performs a null-coalesce on each component separately.</summary>
    public static Size<T> UnwrapOr<T>(this Size<T?> s, Size<T> alt)
        where T : struct =>
        new(s.Width ?? alt.Width, s.Height ?? alt.Height);

    /// <summary>Performs an "or" on each component separately.</summary>
    public static Size<T?> Or<T>(this Size<T?> s, Size<T?> alt)
        where T : struct =>
        new(s.Width ?? alt.Width, s.Height ?? alt.Height);

    /// <summary>Return true if both components are non-null.</summary>
    public static bool BothAxisDefined<T>(this Size<T?> s)
        where T : struct =>
        s.Width.HasValue && s.Height.HasValue;

    /// <summary>
    /// Applies the aspect ratio (if one is supplied) to the size: a known width fills in the
    /// height and vice versa.
    /// </summary>
    public static Size<float?> MaybeApplyAspectRatio(this Size<float?> s, float? aspectRatio)
    {
        if (!aspectRatio.HasValue)
        {
            return s;
        }

        float ratio = aspectRatio.Value;
        if (s.Width.HasValue && !s.Height.HasValue)
        {
            return new Size<float?>(s.Width, s.Width!.Value / ratio);
        }

        if (!s.Width.HasValue && s.Height.HasValue)
        {
            return new Size<float?>(s.Height!.Value * ratio, s.Height);
        }

        return s;
    }

    /// <summary>Convert a <c>Size&lt;AvailableSpace&gt;</c> into a <c>Size&lt;float?&gt;</c>.</summary>
    public static Size<float?> IntoOptions(this Size<AvailableSpace> s) =>
        new(s.Width.IntoOption(), s.Height.IntoOption());

    /// <summary>Per-component <see cref="AvailableSpace.MaybeSet(float?)"/>.</summary>
    public static Size<AvailableSpace> MaybeSet(this Size<AvailableSpace> s, Size<float?> value) =>
        new(s.Width.MaybeSet(value.Width), s.Height.MaybeSet(value.Height));

    // ---------------------------------------------------------------- Point

    /// <summary>A point with values (0,0), representing the origin.</summary>
    public static Point<float> PointZero => new(0f, 0f);

    /// <summary>A point with values (null, null).</summary>
    public static Point<float?> PointNone => new(null, null);

    /// <summary>Component-wise addition.</summary>
    public static Point<float> Add(this Point<float> a, Point<float> b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Gets the component in the main layout axis.</summary>
    public static T Main<T>(this Point<T> p, FlexDirection direction) => direction.IsRow() ? p.X : p.Y;

    /// <summary>Gets the component in the cross layout axis.</summary>
    public static T Cross<T>(this Point<T> p, FlexDirection direction) => direction.IsRow() ? p.Y : p.X;
}
