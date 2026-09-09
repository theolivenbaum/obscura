// Port of vendor/taffy/src/style/available_space.rs
namespace Obscura.Render.Layout;

/// <summary>The kind of an <see cref="AvailableSpace"/> value.</summary>
public enum AvailableSpaceKind : byte
{
    /// <summary>The amount of space available is the specified number of pixels.</summary>
    Definite,

    /// <summary>Indefinite; lay out under a min-content constraint.</summary>
    MinContent,

    /// <summary>Indefinite; lay out under a max-content constraint.</summary>
    MaxContent,
}

/// <summary>
/// The amount of space available to a node in a given axis.
/// <see href="https://www.w3.org/TR/css-sizing-3/#available"/>
/// </summary>
public readonly record struct AvailableSpace
{
    private readonly float _value;

    private AvailableSpace(AvailableSpaceKind kind, float value)
    {
        Kind = kind;
        _value = value;
    }

    /// <summary>Which variant this value is.</summary>
    public AvailableSpaceKind Kind { get; }

    /// <summary>The amount of space available is the specified number of pixels.</summary>
    public static AvailableSpace Definite(float value) => new(AvailableSpaceKind.Definite, value);

    /// <summary>Indefinite; min-content constraint.</summary>
    public static readonly AvailableSpace MinContent = new(AvailableSpaceKind.MinContent, 0f);

    /// <summary>Indefinite; max-content constraint.</summary>
    public static readonly AvailableSpace MaxContent = new(AvailableSpaceKind.MaxContent, 0f);

    /// <summary>Zero definite pixels.</summary>
    public static AvailableSpace Zero => Definite(0f);

    /// <summary>Returns true for definite values, else false.</summary>
    public bool IsDefinite => Kind == AvailableSpaceKind.Definite;

    /// <summary>Definite values become the value; constraints become <c>null</c>.</summary>
    public float? IntoOption() => Kind == AvailableSpaceKind.Definite ? _value : null;

    /// <summary>Return the definite value or a default value.</summary>
    public float UnwrapOr(float @default) => IntoOption() ?? @default;

    /// <summary>Return the definite value. Throws if the value is not definite.</summary>
    public float Unwrap() =>
        Kind == AvailableSpaceKind.Definite
            ? _value
            : throw new InvalidOperationException("AvailableSpace is not definite");

    /// <summary>Return self if definite, else the default value.</summary>
    public AvailableSpace Or(AvailableSpace @default) => IsDefinite ? this : @default;

    /// <summary>Return self if definite, else the result of the callback.</summary>
    public AvailableSpace OrElse(Func<AvailableSpace> defaultCb) => IsDefinite ? this : defaultCb();

    /// <summary>Return the definite value or the result of the callback.</summary>
    public float UnwrapOrElse(Func<float> defaultCb) => IntoOption() ?? defaultCb();

    /// <summary>If the passed value is non-null return a Definite of it, else return self.</summary>
    public AvailableSpace MaybeSet(float? value) => value.HasValue ? Definite(value.Value) : this;

    /// <summary>Map the definite value, leaving constraints alone.</summary>
    public AvailableSpace MapDefiniteValue(Func<float, float> mapFunction) =>
        Kind == AvailableSpaceKind.Definite ? Definite(mapFunction(_value)) : this;

    /// <summary>Compute free space given the passed used space.</summary>
    public float ComputeFreeSpace(float usedSpace) => Kind switch
    {
        AvailableSpaceKind.MaxContent => float.PositiveInfinity,
        AvailableSpaceKind.MinContent => 0.0f,
        _ => _value - usedSpace,
    };

    /// <summary>
    /// Compare equality with another <see cref="AvailableSpace"/>, treating definite values within
    /// f32 epsilon of each other as equal.
    /// </summary>
    public bool IsRoughlyEqual(AvailableSpace other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind != AvailableSpaceKind.Definite || Sys.Abs(_value - other._value) < Sys.F32Epsilon;
    }

    /// <summary>Create a definite available space from a float.</summary>
    public static AvailableSpace From(float value) => Definite(value);

    /// <summary>Non-null becomes Definite; null becomes MaxContent.</summary>
    public static AvailableSpace From(float? option) => option.HasValue ? Definite(option.Value) : MaxContent;

    /// <summary>Implicit conversion from a float, matching Rust's <c>From&lt;f32&gt;</c>.</summary>
    public static implicit operator AvailableSpace(float value) => Definite(value);

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        AvailableSpaceKind.Definite => $"Definite({_value})",
        AvailableSpaceKind.MinContent => "MinContent",
        _ => "MaxContent",
    };
}
