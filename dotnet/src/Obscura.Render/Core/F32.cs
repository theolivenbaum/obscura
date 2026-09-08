namespace Obscura.Render;

/// <summary>
/// Float helpers whose semantics match Rust's <c>f32</c> inherent methods.
/// </summary>
/// <remarks>
/// Rust's <c>f32::min</c>/<c>f32::max</c> are IEEE <c>minNum</c>/<c>maxNum</c>: a NaN operand
/// is ignored and the other value returned. .NET's <see cref="MathF.Min"/> and
/// <see cref="MathF.Max"/> propagate NaN instead, and <see cref="MathF.Round(float)"/>
/// rounds half to even while Rust's <c>f32::round</c> rounds half away from zero.
/// The render layer compares numerically against the Rust engine, so those three
/// differences are corrected here rather than at each call site.
/// </remarks>
internal static class F32
{
    /// <summary>Radians per degree, matching Rust's <c>f32::to_radians</c>.</summary>
    public const float DegreesToRadians = MathF.PI / 180f;

    /// <summary>Rust <c>f32::min</c>: NaN-ignoring minimum.</summary>
    public static float Min(float a, float b)
    {
        if (float.IsNaN(a))
        {
            return b;
        }

        return float.IsNaN(b) ? a : (a < b ? a : b);
    }

    /// <summary>Rust <c>f32::max</c>: NaN-ignoring maximum.</summary>
    public static float Max(float a, float b)
    {
        if (float.IsNaN(a))
        {
            return b;
        }

        return float.IsNaN(b) ? a : (a > b ? a : b);
    }

    /// <summary>Rust <c>f32::round</c>: half away from zero.</summary>
    public static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>Rust <c>f32::to_radians</c>.</summary>
    public static float ToRadians(float degrees) => degrees * DegreesToRadians;
}
