// Port of vendor/taffy/src/util/sys.rs (the `std` variant, which is what the
// default feature set selects).
namespace Obscura.Render.Layout;

/// <summary>
/// Numeric helpers that reproduce Rust <c>f32</c> semantics exactly.
/// </summary>
/// <remarks>
/// <see cref="MathF.Min(float, float)"/> and <see cref="MathF.Max(float, float)"/> propagate NaN,
/// whereas Rust's <c>f32::min</c>/<c>f32::max</c> return the non-NaN operand. Layout compares against
/// the Rust engine, so the Rust behaviour is reproduced here rather than deferring to
/// <see cref="MathF"/>.
/// </remarks>
public static class Sys
{
    /// <summary>Rust's <c>f32::EPSILON</c> (machine epsilon), not .NET's <c>float.Epsilon</c>.</summary>
    public const float F32Epsilon = 1.19209290e-07f;

    /// <summary>Rounds to the nearest whole number, matching taffy's <c>round</c>.</summary>
    /// <remarks>taffy defines this as <c>(value + 0.5).floor()</c>, i.e. round-half-up (towards
    /// positive infinity for exact .5), not banker's rounding and not away-from-zero.</remarks>
    public static float Round(float value) => MathF.Floor(value + 0.5f);

    /// <summary>Rounds up to the nearest whole number.</summary>
    public static float Ceil(float value) => MathF.Ceiling(value);

    /// <summary>Rounds down to the nearest whole number.</summary>
    public static float Floor(float value) => MathF.Floor(value);

    /// <summary>Computes the absolute value.</summary>
    public static float Abs(float value) => MathF.Abs(value);

    /// <summary>Returns the largest of two f32 values, using Rust <c>f32::max</c> NaN semantics.</summary>
    public static float F32Max(float a, float b)
    {
        if (float.IsNaN(a))
        {
            return b;
        }

        if (float.IsNaN(b))
        {
            return a;
        }

        return a > b ? a : b;
    }

    /// <summary>Returns the smallest of two f32 values, using Rust <c>f32::min</c> NaN semantics.</summary>
    public static float F32Min(float a, float b)
    {
        if (float.IsNaN(a))
        {
            return b;
        }

        if (float.IsNaN(b))
        {
            return a;
        }

        return a < b ? a : b;
    }

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
