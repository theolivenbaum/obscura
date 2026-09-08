using System.Globalization;
using System.Text;

namespace Obscura.Render.Css;

/// <summary>
/// ASCII-exact string helpers that mirror the Rust reference implementation.
/// </summary>
/// <remarks>
/// Rust's <c>eq_ignore_ascii_case</c> and <c>to_ascii_lowercase</c> fold only
/// <c>A-Z</c>. .NET's <see cref="StringComparison.OrdinalIgnoreCase"/> and
/// <c>ToLowerInvariant</c> apply full Unicode simple case folding, which
/// diverges on Kelvin sign, dotted capital I, and the Cherokee block. CSS
/// keyword matching is defined as ASCII case-insensitive, so every keyword
/// comparison in this port routes through these helpers rather than the
/// framework ones. Ordinal (never culture-sensitive) comparison is used
/// throughout.
/// </remarks>
internal static class CssText
{
    public static char AsciiLower(char value) => value is >= 'A' and <= 'Z' ? (char)(value + 32) : value;

    public static char AsciiUpper(char value) => value is >= 'a' and <= 'z' ? (char)(value - 32) : value;

    public static bool EqualsAscii(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (AsciiLower(left[index]) != AsciiLower(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool EqualsAscii(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return ReferenceEquals(left, right);
        }

        return EqualsAscii(left.AsSpan(), right.AsSpan());
    }

    public static bool StartsWithAscii(ReadOnlySpan<char> value, ReadOnlySpan<char> prefix) =>
        value.Length >= prefix.Length && EqualsAscii(value[..prefix.Length], prefix);

    public static string AsciiLower(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] is >= 'A' and <= 'Z')
            {
                return Fold(value, index);
            }
        }

        return value;

        static string Fold(string value, int from)
        {
            var buffer = new StringBuilder(value.Length);
            buffer.Append(value, 0, from);
            for (var index = from; index < value.Length; index++)
            {
                buffer.Append(AsciiLower(value[index]));
            }

            return buffer.ToString();
        }
    }

    public static string AsciiUpper(string value)
    {
        var buffer = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            buffer.Append(AsciiUpper(character));
        }

        return buffer.ToString();
    }

    public static bool IsAsciiHexDigit(char value) =>
        value is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    public static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    public static bool IsAsciiAlphabetic(char value) => value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    public static bool IsAsciiAlphanumeric(char value) => IsAsciiAlphabetic(value) || IsAsciiDigit(value);

    public static bool IsAsciiWhitespace(char value) => value is ' ' or '\t' or '\n' or '\r' or '\f';

    public static bool IsAscii(char value) => value <= 0x7F;

    /// <summary>Rust's <c>char::is_alphanumeric</c> (Unicode Alphabetic or Nd/Nl/No).</summary>
    public static bool IsAlphanumeric(char value) => char.IsLetterOrDigit(value);

    /// <summary>Rust's <c>char::is_alphabetic</c>.</summary>
    public static bool IsAlphabetic(char value) => char.IsLetter(value);

    /// <summary>Rust's <c>char::is_whitespace</c> (Unicode White_Space).</summary>
    public static bool IsWhitespace(char value) => char.IsWhiteSpace(value);
}

/// <summary>
/// Number parsing and serialization that matches Rust's <c>f32</c>/<c>f64</c>
/// behavior. Everything is invariant culture; CSS numbers are observable
/// through <c>getComputedStyle</c>, so a culture-sensitive decimal separator
/// would be a wire-visible bug.
/// </summary>
internal static class CssNumber
{
    // Rust's `str::parse::<f32>()` rejects surrounding whitespace, so the
    // framework's default `NumberStyles.Float` (which allows it) is too loose.
    private const NumberStyles FloatStyles =
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    public static bool TryParseFloat(ReadOnlySpan<char> value, out float result)
    {
        result = 0f;
        if (value.IsEmpty)
        {
            return false;
        }

        // Rust accepts `inf`, `infinity` and `nan` (any ASCII case) with an
        // optional sign; .NET spells those differently.
        var body = value;
        var sign = 1f;
        if (body[0] is '+' or '-')
        {
            sign = body[0] == '-' ? -1f : 1f;
            body = body[1..];
        }

        if (CssText.EqualsAscii(body, "inf") || CssText.EqualsAscii(body, "infinity"))
        {
            result = sign * float.PositiveInfinity;
            return true;
        }

        if (CssText.EqualsAscii(body, "nan"))
        {
            result = float.NaN;
            return true;
        }

        return float.TryParse(value, FloatStyles, CultureInfo.InvariantCulture, out result);
    }

    public static float? ParseFloat(ReadOnlySpan<char> value) =>
        TryParseFloat(value, out var parsed) ? parsed : null;

    /// <summary>Parse and keep only finite results, the pattern used all over css.rs.</summary>
    public static float? ParseFiniteFloat(ReadOnlySpan<char> value) =>
        TryParseFloat(value, out var parsed) && float.IsFinite(parsed) ? parsed : null;

    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
