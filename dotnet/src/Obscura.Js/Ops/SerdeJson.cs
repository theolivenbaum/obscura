using System.Globalization;
using System.Text;

namespace Obscura.Js.Ops;

/// <summary>
/// The byte-exact JSON surface the ops share with the Rust engine.
/// </summary>
/// <remarks>
/// <para>
/// Everything an op returns crosses into <c>bootstrap.js</c> verbatim, so the
/// bytes are a contract. Two Rust formatters are in play and they disagree:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>serde_json</c> (the <c>json!</c> macro) widens every float to <c>f64</c>
/// and prints it with ryu's "pretty" formatter, which always emits a decimal
/// point (<c>5.0</c>, not <c>5</c>) and switches to <c>1e30</c>-style exponents
/// outside a fixed window. Use <see cref="Number(double)"/> /
/// <see cref="NumberF32(float)"/>.
/// </description></item>
/// <item><description>
/// <c>format!("{}", value)</c> uses Rust's <c>Display</c>, which prints the
/// shortest representation that round-trips at the value's *own* width and
/// never uses exponential notation: <c>1280f32</c> is <c>1280</c> and
/// <c>0.1f32</c> is <c>0.1</c>. <c>op_layout_metrics</c>, <c>op_scroll_to</c>,
/// <c>op_scroll_offset</c> and <c>op_element_scroll_to</c> build their JSON this
/// way. Use <see cref="DisplayF32(float)"/>.
/// </description></item>
/// </list>
/// <para>
/// <c>System.Text.Json</c> is unusable for either: it escapes <c>+ &lt; &gt; &amp;</c>
/// and every non-ASCII code point, which changes the bytes.
/// </para>
/// </remarks>
internal static class SerdeJson
{
    /// <summary>A serde_json-compatible string literal, including the quotes.</summary>
    internal static void AppendString(StringBuilder output, string value) =>
        Url.UrlOps.AppendJsonString(output, value);

    /// <summary>A serde_json-compatible string literal, including the quotes.</summary>
    internal static string String(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        AppendString(sb, value);
        return sb.ToString();
    }

    /// <summary>
    /// A <c>serde_json</c> number. Non-finite input has no JSON representation,
    /// and <c>Value::from(f64)</c> maps it to <c>null</c>; this does the same.
    /// </summary>
    internal static string Number(double value) =>
        double.IsFinite(value) ? Ryu(value) : "null";

    /// <summary>An <c>f32</c> field inside a <c>json!</c> literal: widened, then ryu.</summary>
    internal static string NumberF32(float value) => Number(value);

    /// <summary>A JSON array of integers, as <c>serde_json::to_string(&amp;Vec&lt;i32&gt;)</c> writes it.</summary>
    internal static string IntArray(IReadOnlyList<int> values)
    {
        var sb = new StringBuilder(values.Count * 4 + 2);
        sb.Append('[');
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>A JSON array of strings, as <c>serde_json::to_string(&amp;Vec&lt;String&gt;)</c> writes it.</summary>
    internal static string StringArray(IReadOnlyList<string> values)
    {
        var sb = new StringBuilder(values.Count * 8 + 2);
        sb.Append('[');
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            AppendString(sb, values[i]);
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Rust's <c>Display</c> for <c>f32</c>: the shortest decimal that round-trips
    /// back to the same <c>f32</c>, written positionally with no exponent and no
    /// gratuitous trailing <c>.0</c>.
    /// </summary>
    internal static string DisplayF32(float value)
    {
        if (float.IsNaN(value))
        {
            return "NaN";
        }

        if (float.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (float.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        if (value == 0f)
        {
            return float.IsNegative(value) ? "-0" : "0";
        }

        var (negative, digits, exponent) = Decompose(value.ToString("R", CultureInfo.InvariantCulture));
        return Positional(negative, digits, exponent, alwaysFraction: false);
    }

    /// <summary>ryu's "pretty" <c>f64</c> formatting, which is what serde_json emits.</summary>
    private static string Ryu(double value)
    {
        if (value == 0d)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        var (negative, digits, exponent) = Decompose(value.ToString("R", CultureInfo.InvariantCulture));
        var length = digits.Length;
        var pointPosition = length + exponent;

        // The four positional branches of ryu::pretty::format64, in its order.
        if (exponent >= 0 && pointPosition <= 16)
        {
            // 1234e7 -> 12340000000.0
            var sb = Start(negative, length + exponent + 2);
            sb.Append(digits);
            sb.Append('0', exponent);
            sb.Append(".0");
            return sb.ToString();
        }

        if (pointPosition > 0 && pointPosition <= 16)
        {
            // 1234e-2 -> 12.34
            var sb = Start(negative, length + 1);
            sb.Append(digits, 0, pointPosition);
            sb.Append('.');
            sb.Append(digits, pointPosition, length - pointPosition);
            return sb.ToString();
        }

        if (pointPosition > -5 && pointPosition <= 0)
        {
            // 1234e-6 -> 0.001234
            var sb = Start(negative, length - pointPosition + 2);
            sb.Append("0.");
            sb.Append('0', -pointPosition);
            sb.Append(digits);
            return sb.ToString();
        }

        if (length == 1)
        {
            // 1e30
            var sb = Start(negative, 8);
            sb.Append(digits);
            sb.Append('e');
            sb.Append((pointPosition - 1).ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // 1234e30 -> 1.234e33
        var scientific = Start(negative, length + 8);
        scientific.Append(digits[0]);
        scientific.Append('.');
        scientific.Append(digits, 1, length - 1);
        scientific.Append('e');
        scientific.Append((pointPosition - 1).ToString(CultureInfo.InvariantCulture));
        return scientific.ToString();
    }

    private static StringBuilder Start(bool negative, int capacity)
    {
        var sb = new StringBuilder(capacity + 1);
        if (negative)
        {
            sb.Append('-');
        }

        return sb;
    }

    /// <summary>
    /// Positional expansion of a decomposed value, which is what Rust's
    /// <c>Display</c> produces for every finite magnitude.
    /// </summary>
    private static string Positional(bool negative, string digits, int exponent, bool alwaysFraction)
    {
        var length = digits.Length;
        var pointPosition = length + exponent;
        StringBuilder sb;
        if (exponent >= 0)
        {
            sb = Start(negative, length + exponent + 2);
            sb.Append(digits);
            sb.Append('0', exponent);
            if (alwaysFraction)
            {
                sb.Append(".0");
            }
        }
        else if (pointPosition > 0)
        {
            sb = Start(negative, length + 1);
            sb.Append(digits, 0, pointPosition);
            sb.Append('.');
            sb.Append(digits, pointPosition, length - pointPosition);
        }
        else
        {
            sb = Start(negative, length - pointPosition + 2);
            sb.Append("0.");
            sb.Append('0', -pointPosition);
            sb.Append(digits);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits a .NET round-trip literal into sign, significant digits with no
    /// leading or trailing zeros, and the power of ten those digits scale by.
    /// .NET's shortest round-trip digits are the same digits ryu computes.
    /// </summary>
    private static (bool Negative, string Digits, int Exponent) Decompose(string literal)
    {
        var negative = literal.Length > 0 && literal[0] == '-';
        var body = negative ? literal[1..] : literal;

        var exponent = 0;
        var e = body.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(body[(e + 1)..], CultureInfo.InvariantCulture);
            body = body[..e];
        }

        var dot = body.IndexOf('.');
        string all;
        if (dot >= 0)
        {
            all = string.Concat(body.AsSpan(0, dot), body.AsSpan(dot + 1));
            exponent -= body.Length - dot - 1;
        }
        else
        {
            all = body;
        }

        var start = 0;
        while (start < all.Length - 1 && all[start] == '0')
        {
            start++;
        }

        var end = all.Length;
        while (end - start > 1 && all[end - 1] == '0')
        {
            end--;
            exponent++;
        }

        return (negative, all[start..end], exponent);
    }
}
