namespace Obscura.Render;

/// <summary>
/// An axis-aligned rectangle in CSS pixels, relative to the containing block.
/// </summary>
public readonly record struct Rect(float X, float Y, float Width, float Height)
{
    /// <summary>The all-zero rectangle, matching Rust's derived <c>Default</c>.</summary>
    public static readonly Rect Zero = default;

    /// <summary>
    /// The overlap of two rects, or <c>null</c> if they do not intersect (or the overlap is
    /// degenerate). Used to accumulate an ancestor clip chain for <c>overflow: hidden</c>.
    /// </summary>
    public Rect? Intersect(in Rect other)
    {
        float x0 = F32.Max(X, other.X);
        float y0 = F32.Max(Y, other.Y);
        float x1 = F32.Min(X + Width, other.X + other.Width);
        float y1 = F32.Min(Y + Height, other.Y + other.Height);
        if (x1 > x0 && y1 > y0)
        {
            return new Rect(x0, y0, x1 - x0, y1 - y0);
        }

        return null;
    }

    /// <summary>
    /// The smallest rect covering both. Used to derive a table row/section box from its cells,
    /// since <c>&lt;tr&gt;</c>/<c>&lt;tbody&gt;</c> are not laid out as taffy boxes.
    /// </summary>
    public Rect Union(in Rect other)
    {
        float x0 = F32.Min(X, other.X);
        float y0 = F32.Min(Y, other.Y);
        float x1 = F32.Max(X + Width, other.X + other.Width);
        float y1 = F32.Max(Y + Height, other.Y + other.Height);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>
/// One two-dimensional affine transform in CSS pixel coordinates.
/// </summary>
/// <remarks>
/// The six components use the CSS <c>matrix(a,b,c,d,e,f)</c> convention:
/// <c>x' = a*x + c*y + e</c>, <c>y' = b*x + d*y + f</c>. Keeping this renderer-owned type
/// independent of the raster backend lets layout geometry, scrolling overflow, CSSOM, and
/// paint consume exactly the same resolved transform.
/// </remarks>
public readonly record struct Affine2(float A, float B, float C, float D, float E, float F)
{
    public static readonly Affine2 Identity = new(1f, 0f, 0f, 1f, 0f, 0f);

    /// <summary>Rust's <c>Default</c> for this type is <see cref="Identity"/>, not all-zero.</summary>
    public static Affine2 Default => Identity;

    /// <summary>Compose <c>self(other(point))</c>.</summary>
    public Affine2 Then(in Affine2 other) => new(
        (A * other.A) + (C * other.B),
        (B * other.A) + (D * other.B),
        (A * other.C) + (C * other.D),
        (B * other.C) + (D * other.D),
        (A * other.E) + (C * other.F) + E,
        (B * other.E) + (D * other.F) + F);

    public static Affine2 Translate(float x, float y) => new(1f, 0f, 0f, 1f, x, y);

    public static Affine2 Scale(float x, float y) => new(x, 0f, 0f, y, 0f, 0f);

    public static Affine2 Rotate(float degrees)
    {
        float radians = F32.ToRadians(degrees);
        float sin = MathF.Sin(radians);
        float cos = MathF.Cos(radians);
        return new Affine2(cos, sin, -sin, cos, 0f, 0f);
    }

    public static Affine2 Skew(float xDegrees, float yDegrees) => new(
        1f,
        MathF.Tan(F32.ToRadians(yDegrees)),
        MathF.Tan(F32.ToRadians(xDegrees)),
        1f,
        0f,
        0f);

    public Affine2 Around((float X, float Y) origin) =>
        Translate(origin.X, origin.Y).Then(this).Then(Translate(-origin.X, -origin.Y));

    public (float X, float Y) MapPoint(float x, float y) =>
        ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    public Rect MapRect(in Rect rect)
    {
        (float X, float Y)[] points =
        [
            MapPoint(rect.X, rect.Y),
            MapPoint(rect.X + rect.Width, rect.Y),
            MapPoint(rect.X, rect.Y + rect.Height),
            MapPoint(rect.X + rect.Width, rect.Y + rect.Height),
        ];

        float left = float.PositiveInfinity;
        float top = float.PositiveInfinity;
        float right = float.NegativeInfinity;
        float bottom = float.NegativeInfinity;
        foreach ((float x, float y) in points)
        {
            left = F32.Min(left, x);
            top = F32.Min(top, y);
            right = F32.Max(right, x);
            bottom = F32.Max(bottom, y);
        }

        return new Rect(left, top, F32.Max(right - left, 0f), F32.Max(bottom - top, 0f));
    }

    public Affine2? Inverse()
    {
        float determinant = (A * D) - (B * C);
        if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 1.0e-8f)
        {
            return null;
        }

        float inverse = 1f / determinant;
        return new Affine2(
            D * inverse,
            -B * inverse,
            -C * inverse,
            A * inverse,
            ((C * F) - (D * E)) * inverse,
            ((B * E) - (A * F)) * inverse);
    }

    public bool IsIdentity() =>
        MathF.Abs(A - 1f) < float.Epsilon
        && MathF.Abs(B) < float.Epsilon
        && MathF.Abs(C) < float.Epsilon
        && MathF.Abs(D - 1f) < float.Epsilon
        && MathF.Abs(E) < float.Epsilon
        && MathF.Abs(F) < float.Epsilon;

    public bool IsTranslation() =>
        MathF.Abs(A - 1f) < float.Epsilon
        && MathF.Abs(B) < float.Epsilon
        && MathF.Abs(C) < float.Epsilon
        && MathF.Abs(D - 1f) < float.Epsilon;
}

/// <summary>
/// One authored length in a <c>transform</c> function, with its unresolved CSS math
/// expression retained until the final reference box is known.
/// </summary>
public readonly record struct TransformLength(Dimension Value, string? Expression)
{
    public static TransformLength Px(float value) => new(Dimension.Px(value), null);
}

/// <summary>
/// One authored operation in a <c>transform</c> function list. Source order is significant
/// and is intentionally retained until the final reference box resolves percentage
/// translations.
/// </summary>
public abstract record TransformOp
{
    private TransformOp()
    {
    }

    public sealed record Translate(TransformLength X, TransformLength Y) : TransformOp;

    public sealed record Scale(float X, float Y) : TransformOp;

    public sealed record Rotate(float Degrees) : TransformOp;

    public sealed record Skew(float XDegrees, float YDegrees) : TransformOp;

    public sealed record Matrix(Affine2 Value) : TransformOp;
}

/// <summary>Per-edge box values (margin / padding / border) in CSS pixels.</summary>
public readonly record struct Edges(float Top, float Right, float Bottom, float Left)
{
    public static readonly Edges Zero = default;
}

/// <summary>Free functions declared directly in <c>obscura-render/src/lib.rs</c>.</summary>
public static class RenderMath
{
    /// <summary>
    /// Snap a CSS scrolling value to the raster device-pixel grid.
    /// </summary>
    /// <remarks>
    /// Captures currently use one device pixel per CSS pixel, but keeping the scale explicit
    /// makes the same rule usable when page-level device scale is introduced. CSSOM integer
    /// extents and effective offsets must share this quantization or a fractional range can
    /// move geometry without moving a screenshot pixel.
    /// </remarks>
    public static float QuantizeScrollValue(float value, float deviceScaleFactor)
    {
        if (!float.IsFinite(value))
        {
            return 0f;
        }

        float scale = float.IsFinite(deviceScaleFactor) && deviceScaleFactor > 0f
            ? deviceScaleFactor
            : 1f;
        return F32.Round(value * scale) / scale;
    }

    internal static float QuantizedScrollRange(float content, float client, float deviceScaleFactor) =>
        F32.Max(
            QuantizeScrollValue(content, deviceScaleFactor) - QuantizeScrollValue(client, deviceScaleFactor),
            0f);
}
