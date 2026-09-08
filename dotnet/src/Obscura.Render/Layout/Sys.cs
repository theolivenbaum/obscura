// Port of vendor/taffy/src/util/sys.rs (the `std` variant, which is what the
// default feature set selects).
namespace Obscura.Render.Layout;

/// <summary>
/// Numeric helpers that reproduce Rust <c>f32</c> semantics exactly.
/// </summary>
/// <remarks>
/// The NaN-ignoring min/max forward to <see cref="F32"/>, the render layer's shared f32 helpers.
/// <see cref="Round"/> deliberately does NOT: taffy defines its own rounding as
/// <c>(value + 0.5).floor()</c> (half towards positive infinity), which differs from Rust's
/// <c>f32::round</c> (half away from zero) for negative midpoints.
/// </remarks>
public static class Sys
{
    /// <summary>Rust's <c>f32::EPSILON</c> (machine epsilon), not .NET's <c>float.Epsilon</c>.</summary>
    public const float F32Epsilon = 1.19209290e-07f;

    /// <summary>Rounds to the nearest whole number, matching taffy's <c>util::sys::round</c>.</summary>
    /// <remarks>
    /// taffy defines this as <c>(value + 0.5).floor()</c>: round half towards positive infinity.
    /// That is neither <see cref="MathF.Round(float)"/> (half to even) nor <c>F32.Round</c>
    /// (half away from zero); <c>round(-2.5)</c> is -2 here and -3 there. Layout rounding must use
    /// this function.
    /// </remarks>
    public static float Round(float value) => MathF.Floor(value + 0.5f);

    /// <summary>Rounds up to the nearest whole number.</summary>
    public static float Ceil(float value) => MathF.Ceiling(value);

    /// <summary>Rounds down to the nearest whole number.</summary>
    public static float Floor(float value) => MathF.Floor(value);

    /// <summary>Computes the absolute value.</summary>
    public static float Abs(float value) => MathF.Abs(value);

    /// <summary>Returns the largest of two f32 values, using Rust <c>f32::max</c> NaN semantics.</summary>
    public static float F32Max(float a, float b) => F32.Max(a, b);

    /// <summary>Returns the smallest of two f32 values, using Rust <c>f32::min</c> NaN semantics.</summary>
    public static float F32Min(float a, float b) => F32.Min(a, b);

    /// <summary>
    /// Rust's <c>f32::total_cmp</c>: a total ordering over all f32 bit patterns.
    /// </summary>
    public static int TotalCmp(float a, float b)
    {
        int left = BitConverter.SingleToInt32Bits(a);
        int right = BitConverter.SingleToInt32Bits(b);
        left ^= (int)((uint)(left >> 31) >> 1);
        right ^= (int)((uint)(right >> 31) >> 1);
        return left.CompareTo(right);
    }
}
