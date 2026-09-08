// Port of vendor/taffy/src/geometry.rs
// Geometric primitives useful for layout.
//
// Rust's Size<T>/Rect<T>/Point<T>/Line<T> are Copy structs with public fields and
// generic arithmetic. C# cannot express the generic arithmetic bounds, so the
// arithmetic lives in extension methods specialised on the concrete element types
// that taffy actually instantiates (float, float?, Dimension, ...).
namespace Obscura.Render.Layout;

/// <summary>The simple absolute horizontal and vertical axis.</summary>
public enum AbsoluteAxis
{
    /// <summary>The horizontal axis.</summary>
    Horizontal,

    /// <summary>The vertical axis.</summary>
    Vertical,
}

/// <summary>
/// The CSS abstract axis.
/// <see href="https://www.w3.org/TR/css-writing-modes-3/#abstract-axes"/>
/// </summary>
public enum AbstractAxis
{
    /// <summary>The axis in the inline dimension.</summary>
    Inline,

    /// <summary>The axis in the block dimension.</summary>
    Block,
}

/// <summary>Helpers over the axis enums.</summary>
public static class AxisExtensions
{
    /// <summary>Returns the other variant of the enum.</summary>
    public static AbsoluteAxis OtherAxis(this AbsoluteAxis axis) =>
        axis == AbsoluteAxis.Horizontal ? AbsoluteAxis.Vertical : AbsoluteAxis.Horizontal;

    /// <summary>Returns the other variant of the enum.</summary>
    public static AbstractAxis Other(this AbstractAxis axis) =>
        axis == AbstractAxis.Inline ? AbstractAxis.Block : AbstractAxis.Inline;

    /// <summary>
    /// Convert an <see cref="AbstractAxis"/> into an <see cref="AbsoluteAxis"/> naively assuming
    /// that the Inline axis is Horizontal.
    /// </summary>
    public static AbsoluteAxis AsAbsNaive(this AbstractAxis axis) =>
        axis == AbstractAxis.Inline ? AbsoluteAxis.Horizontal : AbsoluteAxis.Vertical;
}

/// <summary>Container that holds an item in each absolute axis.</summary>
public struct InBothAbsAxis<T> : IEquatable<InBothAbsAxis<T>>
{
    /// <summary>The item in the horizontal axis.</summary>
    public T Horizontal;

    /// <summary>The item in the vertical axis.</summary>
    public T Vertical;

    /// <summary>Create a new pair.</summary>
    public InBothAbsAxis(T horizontal, T vertical)
    {
        Horizontal = horizontal;
        Vertical = vertical;
    }

    /// <summary>Get the contained item based on the <see cref="AbsoluteAxis"/> passed.</summary>
    public readonly T Get(AbsoluteAxis axis) => axis == AbsoluteAxis.Horizontal ? Horizontal : Vertical;

    /// <inheritdoc/>
    public readonly bool Equals(InBothAbsAxis<T> other) =>
        EqualityComparer<T>.Default.Equals(Horizontal, other.Horizontal)
        && EqualityComparer<T>.Default.Equals(Vertical, other.Vertical);

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is InBothAbsAxis<T> o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Horizontal, Vertical);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(InBothAbsAxis<T> a, InBothAbsAxis<T> b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(InBothAbsAxis<T> a, InBothAbsAxis<T> b) => !a.Equals(b);
}

/// <summary>An axis-aligned UI rectangle.</summary>
public struct Rect<T> : IEquatable<Rect<T>>
{
    /// <summary>The starting (left) edge.</summary>
    public T Left;

    /// <summary>The ending (right) edge.</summary>
    public T Right;

    /// <summary>The top edge.</summary>
    public T Top;

    /// <summary>The bottom edge.</summary>
    public T Bottom;

    /// <summary>Create a new rect. Argument order matches taffy's <c>Rect::new</c>.</summary>
    public Rect(T left, T right, T top, T bottom)
    {
        Left = left;
        Right = right;
        Top = top;
        Bottom = bottom;
    }

    /// <summary>Applies the function <paramref name="f"/> to all four sides of the rect.</summary>
    public readonly Rect<TR> Map<TR>(Func<T, TR> f) => new(f(Left), f(Right), f(Top), f(Bottom));

    /// <summary>
    /// Applies <paramref name="f"/> to all four sides of the rect, passing width for the left and
    /// right sides and height for the top and bottom sides.
    /// </summary>
    public readonly Rect<TR> ZipSize<TU, TR>(Size<TU> size, Func<T, TU, TR> f) =>
        new(f(Left, size.Width), f(Right, size.Width), f(Top, size.Height), f(Bottom, size.Height));

    /// <summary>Returns a <see cref="Line{T}"/> representing the left and right properties.</summary>
    public readonly Line<T> HorizontalComponents() => new(Left, Right);

    /// <summary>Returns a <see cref="Line{T}"/> representing the top and bottom properties.</summary>
    public readonly Line<T> VerticalComponents() => new(Top, Bottom);

    /// <inheritdoc/>
    public readonly bool Equals(Rect<T> other)
    {
        var cmp = EqualityComparer<T>.Default;
        return cmp.Equals(Left, other.Left)
            && cmp.Equals(Right, other.Right)
            && cmp.Equals(Top, other.Top)
            && cmp.Equals(Bottom, other.Bottom);
    }

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is Rect<T> o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Left, Right, Top, Bottom);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Rect<T> a, Rect<T> b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Rect<T> a, Rect<T> b) => !a.Equals(b);

    /// <inheritdoc/>
    public readonly override string ToString() =>
        $"Rect {{ left: {Left}, right: {Right}, top: {Top}, bottom: {Bottom} }}";
}

/// <summary>An abstract "line". Represents any type that has a start and an end.</summary>
public struct Line<T> : IEquatable<Line<T>>
{
    /// <summary>The start position of a line.</summary>
    public T Start;

    /// <summary>The end position of a line.</summary>
    public T End;

    /// <summary>Create a new line.</summary>
    public Line(T start, T end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Applies the function <paramref name="f"/> to both the start and the end.</summary>
    public readonly Line<TR> Map<TR>(Func<T, TR> f) => new(f(Start), f(End));

    /// <inheritdoc/>
    public readonly bool Equals(Line<T> other) =>
        EqualityComparer<T>.Default.Equals(Start, other.Start)
        && EqualityComparer<T>.Default.Equals(End, other.End);

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is Line<T> o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Start, End);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Line<T> a, Line<T> b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Line<T> a, Line<T> b) => !a.Equals(b);

    /// <inheritdoc/>
    public readonly override string ToString() => $"Line {{ start: {Start}, end: {End} }}";
}

/// <summary>The width and height of a <see cref="Rect{T}"/>.</summary>
public struct Size<T> : IEquatable<Size<T>>
{
    /// <summary>The x extent of the rectangle.</summary>
    public T Width;

    /// <summary>The y extent of the rectangle.</summary>
    public T Height;

    /// <summary>Create a new size.</summary>
    public Size(T width, T height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>Applies the function <paramref name="f"/> to both the width and height.</summary>
    public readonly Size<TR> Map<TR>(Func<T, TR> f) => new(f(Width), f(Height));

    /// <summary>Applies the function <paramref name="f"/> to the width.</summary>
    public readonly Size<T> MapWidth(Func<T, T> f) => new(f(Width), Height);

    /// <summary>Applies the function <paramref name="f"/> to the height.</summary>
    public readonly Size<T> MapHeight(Func<T, T> f) => new(Width, f(Height));

    /// <summary>Applies <paramref name="f"/> to both components of this and another size.</summary>
    public readonly Size<TR> ZipMap<TOther, TR>(Size<TOther> other, Func<T, TOther, TR> f) =>
        new(f(Width, other.Width), f(Height, other.Height));

    /// <summary>Get either the width or the height depending on the axis passed in.</summary>
    public readonly T GetAbs(AbsoluteAxis axis) => axis == AbsoluteAxis.Horizontal ? Width : Height;

    /// <summary>Get the extent of the specified layout axis.</summary>
    public readonly T Get(AbstractAxis axis) => axis == AbstractAxis.Inline ? Width : Height;

    /// <summary>Set the extent of the specified layout axis.</summary>
    public void Set(AbstractAxis axis, T value)
    {
        if (axis == AbstractAxis.Inline)
        {
            Width = value;
        }
        else
        {
            Height = value;
        }
    }

    /// <summary>Return a copy with the extent of the specified layout axis replaced.</summary>
    public readonly Size<T> With(AbstractAxis axis, T value)
    {
        var copy = this;
        copy.Set(axis, value);
        return copy;
    }

    /// <inheritdoc/>
    public readonly bool Equals(Size<T> other) =>
        EqualityComparer<T>.Default.Equals(Width, other.Width)
        && EqualityComparer<T>.Default.Equals(Height, other.Height);

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is Size<T> o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Width, Height);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Size<T> a, Size<T> b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Size<T> a, Size<T> b) => !a.Equals(b);

    /// <inheritdoc/>
    public readonly override string ToString() => $"Size {{ width: {Width}, height: {Height} }}";
}

/// <summary>A 2-dimensional coordinate.</summary>
public struct Point<T> : IEquatable<Point<T>>
{
    /// <summary>The x-coordinate.</summary>
    public T X;

    /// <summary>The y-coordinate.</summary>
    public T Y;

    /// <summary>Create a new point.</summary>
    public Point(T x, T y)
    {
        X = x;
        Y = y;
    }

    /// <summary>Applies the function <paramref name="f"/> to both x and y.</summary>
    public readonly Point<TR> Map<TR>(Func<T, TR> f) => new(f(X), f(Y));

    /// <summary>Get the component of the specified layout axis.</summary>
    public readonly T Get(AbstractAxis axis) => axis == AbstractAxis.Inline ? X : Y;

    /// <summary>Set the component of the specified layout axis.</summary>
    public void Set(AbstractAxis axis, T value)
    {
        if (axis == AbstractAxis.Inline)
        {
            X = value;
        }
        else
        {
            Y = value;
        }
    }

    /// <summary>Swap x and y components.</summary>
    public readonly Point<T> Transpose() => new(Y, X);

    /// <summary>Convert into a <see cref="Size{T}"/>.</summary>
    public readonly Size<T> ToSize() => new(X, Y);

    /// <inheritdoc/>
    public readonly bool Equals(Point<T> other) =>
        EqualityComparer<T>.Default.Equals(X, other.X) && EqualityComparer<T>.Default.Equals(Y, other.Y);

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is Point<T> o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(X, Y);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Point<T> a, Point<T> b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Point<T> a, Point<T> b) => !a.Equals(b);

    /// <inheritdoc/>
    public readonly override string ToString() => $"Point {{ x: {X}, y: {Y} }}";
}

/// <summary>Generic struct which holds a "min" value and a "max" value.</summary>
public struct MinMax<TMin, TMax> : IEquatable<MinMax<TMin, TMax>>
{
    /// <summary>The value representing the minimum.</summary>
    public TMin Min;

    /// <summary>The value representing the maximum.</summary>
    public TMax Max;

    /// <summary>Create a new min/max pair.</summary>
    public MinMax(TMin min, TMax max)
    {
        Min = min;
        Max = max;
    }

    /// <inheritdoc/>
    public readonly bool Equals(MinMax<TMin, TMax> other) =>
        EqualityComparer<TMin>.Default.Equals(Min, other.Min)
        && EqualityComparer<TMax>.Default.Equals(Max, other.Max);

    /// <inheritdoc/>
    public readonly override bool Equals(object? obj) => obj is MinMax<TMin, TMax> o && Equals(o);

    /// <inheritdoc/>
    public readonly override int GetHashCode() => HashCode.Combine(Min, Max);

    /// <summary>Equality operator.</summary>
    public static bool operator ==(MinMax<TMin, TMax> a, MinMax<TMin, TMax> b) => a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(MinMax<TMin, TMax> a, MinMax<TMin, TMax> b) => !a.Equals(b);
}
