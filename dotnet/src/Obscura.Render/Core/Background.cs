using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render;

/// <summary>One axis of a CSS <c>background-position</c>.</summary>
/// <remarks>
/// CSS positions combine an absolute offset with a percentage of the space left after sizing
/// the background image. Keeping both terms is necessary for sprite sheets: <c>-24px</c> is an
/// offset from the start edge, while <c>right 10px</c> is equivalent to <c>100% - 10px</c>.
/// The two components are crate-private in Rust, so they are <c>internal</c> here.
/// </remarks>
public readonly record struct BackgroundPositionAxis
{
    private readonly float _length;
    private readonly float _percentage;

    private BackgroundPositionAxis(float length, float percentage)
    {
        _length = length;
        _percentage = percentage;
    }

    /// <summary>The absolute offset term (Rust field <c>length</c>).</summary>
    internal float LengthPart => _length;

    /// <summary>The 0.0-1.0 leftover-space fraction (Rust field <c>percentage</c>).</summary>
    internal float PercentagePart => _percentage;

    public static BackgroundPositionAxis Pixels(float length) => new(length, 0f);

    /// <summary>A percentage of the leftover space, as a 0.0-1.0 fraction.</summary>
    public static BackgroundPositionAxis Percentage(float percentage) => new(0f, percentage);

    public static BackgroundPositionAxis LengthPercentage(float length, float percentage) =>
        new(length, percentage);

    /// <summary>The <c>right 10px</c> / <c>bottom 10px</c> form: <c>100% - offset</c>.</summary>
    public static BackgroundPositionAxis FromEndOffset(BackgroundPositionAxis offset) =>
        new(-offset._length, 1f - offset._percentage);

    public float Resolve(float leftoverSpace) => _length + (_percentage * leftoverSpace);

    internal BackgroundPositionAxis Interpolate(BackgroundPositionAxis other, float position) =>
        new(
            _length + ((other._length - _length) * position),
            _percentage + ((other._percentage - _percentage) * position));
}

/// <summary>The two axes of CSS <c>background-position</c>.</summary>
/// <remarks>
/// The default is the CSS initial value <c>0% 0%</c>. A one-value longhand such as
/// <c>background-position: 0</c> is parsed as <c>0 center</c>.
/// </remarks>
public readonly record struct BackgroundPosition(BackgroundPositionAxis X, BackgroundPositionAxis Y)
{
    /// <summary>The CSS initial value, <c>0% 0%</c>; identical to <c>default</c>.</summary>
    public static readonly BackgroundPosition Zero = default;

    public static BackgroundPosition New(BackgroundPositionAxis x, BackgroundPositionAxis y) =>
        new(x, y);

    internal BackgroundPosition Interpolate(BackgroundPosition other, float position) =>
        new(X.Interpolate(other.X, position), Y.Interpolate(other.Y, position));
}

/// <summary>Box used to establish a background layer's positioning area.</summary>
public enum BackgroundOrigin
{
    BorderBox,

    /// <summary>The default.</summary>
    PaddingBox,

    ContentBox,
}

/// <summary>Box (or glyph shape) that limits background painting.</summary>
public enum BackgroundClip
{
    /// <summary>The default.</summary>
    BorderBox,

    PaddingBox,
    ContentBox,
    Text,
}

/// <summary>Fill rule for a CSS <c>clip-path: polygon(...)</c>.</summary>
public enum ClipPathFillRule
{
    /// <summary>The default.</summary>
    Nonzero,

    Evenodd,
}

/// <summary>The supported CSS basic-shape clip.</summary>
/// <remarks>
/// Coordinates retain their CSS length/percentage unit until paint, where percentages resolve
/// independently against the border box width and height. This mirrors the reference-box
/// resolution used by browser engines and avoids baking responsive polygon geometry into
/// computed style.
/// </remarks>
public sealed class ClipPathPolygon
{
    public ClipPathPolygon()
    {
    }

    public ClipPathPolygon(ClipPathFillRule fillRule, List<(Dimension X, Dimension Y)> points)
    {
        FillRule = fillRule;
        Points = points;
    }

    public ClipPathFillRule FillRule;

    public List<(Dimension X, Dimension Y)> Points = [];

    public ClipPathPolygon Clone() => new(FillRule, [.. Points]);
}

/// <summary>
/// One color stop of a gradient: the resolved RGBA plus its optional authored 0.0-1.0 position.
/// </summary>
/// <remarks>
/// Rust models this as the tuple <c>([u8; 4], Option&lt;f32&gt;)</c>; the port names it so the
/// nested collection types stay readable.
/// </remarks>
public readonly record struct GradientStop(RgbaColor Color, float? Position);

/// <summary>One CSS gradient from the ordered <c>background-image</c> layer list.</summary>
/// <remarks>
/// CSS paints the first authored layer closest to the user, so paint walks this list in reverse
/// over the background color. Keeping the authored order is essential when a hero combines a
/// linear fade with several partially transparent radial highlights.
/// </remarks>
public abstract record BackgroundGradientLayer
{
    private BackgroundGradientLayer()
    {
    }

    /// <summary>Rust's derived <c>Clone</c>: an independent copy including the stop lists.</summary>
    public abstract BackgroundGradientLayer DeepClone();

    /// <summary>
    /// A <c>linear-gradient()</c> layer. <c>StopPositions</c> holds the authored stop
    /// positions, retained until paint so absolute lengths can resolve against the final
    /// gradient-line length. <c>Repeating</c> marks
    /// <c>repeating-linear-gradient()</c>, which repeats the interval from its first resolved
    /// stop through its last, independently of background tiling.
    /// </summary>
    public sealed record Linear(
        float Angle,
        List<GradientStop> Stops,
        List<string?> StopPositions,
        bool Repeating) : BackgroundGradientLayer
    {
        public override BackgroundGradientLayer DeepClone() =>
            new Linear(Angle, [.. Stops], [.. StopPositions], Repeating);
    }

    public sealed record Radial((float X, float Y) Center, List<GradientStop> Stops)
        : BackgroundGradientLayer
    {
        public override BackgroundGradientLayer DeepClone() => new Radial(Center, [.. Stops]);
    }

    public sealed record Conic(float Angle, (float X, float Y) Center, List<GradientStop> Stops)
        : BackgroundGradientLayer
    {
        public override BackgroundGradientLayer DeepClone() => new Conic(Angle, Center, [.. Stops]);
    }
}

/// <summary>The ending-shape kind of one CSS radial gradient.</summary>
internal enum RadialGradientShape
{
    Circle,
    Ellipse,
}

/// <summary>Which CSS sizing keyword (or explicit radii) sizes a radial gradient.</summary>
internal enum RadialGradientSizeKind : byte
{
    ClosestSide,
    ClosestCorner,
    FarthestSide,

    /// <summary>The CSS initial ending-shape size.</summary>
    FarthestCorner,

    /// <summary>
    /// Horizontal and vertical radii. Percentages resolve against the corresponding axis of
    /// the gradient box, per CSS Images.
    /// </summary>
    Explicit,
}

/// <summary>Ending-shape size for one CSS radial gradient.</summary>
internal readonly record struct RadialGradientSize(
    RadialGradientSizeKind Kind,
    Dimension X,
    Dimension Y)
{
    public static readonly RadialGradientSize ClosestSide = new(RadialGradientSizeKind.ClosestSide, default, default);

    public static readonly RadialGradientSize ClosestCorner = new(RadialGradientSizeKind.ClosestCorner, default, default);

    public static readonly RadialGradientSize FarthestSide = new(RadialGradientSizeKind.FarthestSide, default, default);

    public static readonly RadialGradientSize FarthestCorner = new(RadialGradientSizeKind.FarthestCorner, default, default);

    public static RadialGradientSize Explicit(Dimension x, Dimension y) =>
        new(RadialGradientSizeKind.Explicit, x, y);
}

/// <summary>Parsed ending-shape geometry for one CSS radial gradient.</summary>
/// <remarks>
/// This is kept alongside <see cref="BackgroundGradientLayer"/> rather than adding fields to
/// its public <c>Radial</c> case, preserving the existing construction API for embedders while
/// allowing authored circle/ellipse sizing to survive to paint. Note that Rust's
/// <c>Default</c> is <c>Ellipse</c> + <c>FarthestCorner</c>, which is NOT
/// <c>default(RadialGradientGeometry)</c>; use <see cref="Default"/>.
/// </remarks>
internal readonly record struct RadialGradientGeometry(
    RadialGradientShape Shape,
    RadialGradientSize Size)
{
    public static readonly RadialGradientGeometry Default =
        new(RadialGradientShape.Ellipse, RadialGradientSize.FarthestCorner);
}
