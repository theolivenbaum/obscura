using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Obscura.Cli.Json;

/// <summary>
/// Writes <see cref="JsonNode"/> the way <c>serde_json</c> writes
/// <c>serde_json::Value</c>.
/// </summary>
/// <remarks>
/// <para>
/// The CLI's stdout is a user-visible contract, so the bytes matter.
/// <c>System.Text.Json</c> cannot produce them: it escapes <c>&lt; &gt; &amp; +</c>
/// and every non-ASCII code point by default, and it prints <c>2</c> where
/// serde_json prints <c>2.0</c>.
/// </para>
/// <para>
/// Numbers are the subtle half. <c>page.evaluate</c> converts a bare JS number
/// straight to an <c>f64</c>, and serde_json renders an f64 with ryu's "pretty"
/// formatter, which always emits a decimal point. A number *inside* an object
/// or array arrives through <c>JSON.stringify</c> and is re-parsed, and
/// serde_json's parser keeps an integer literal an integer. So
/// <c>--eval "1+1"</c> prints <c>2.0</c> while <c>--eval "({a:1})"</c> prints
/// <c>{"a":1}</c>, and both are reproduced here by distinguishing a node backed
/// by a CLR <c>double</c> from one backed by parsed JSON text.
/// </para>
/// </remarks>
public static class SerdeJson
{
    /// <summary>Compact form, matching <c>Value::to_string()</c>.</summary>
    public static string ToJson(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node, indent: -1, depth: 0);
        return sb.ToString();
    }

    /// <summary>Two-space indented form, matching <c>to_string_pretty</c>.</summary>
    public static string ToJsonPretty(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node, indent: 2, depth: 0);
        return sb.ToString();
    }

    /// <summary>A serde_json string literal, including the surrounding quotes.</summary>
    public static string StringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        WriteString(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, int indent, int depth)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
                WriteObject(sb, obj, indent, depth);
                return;
            case JsonArray array:
                WriteArray(sb, array, indent, depth);
                return;
            case JsonValue value:
                WriteValue(sb, value);
                return;
            default:
                sb.Append("null");
                return;
        }
    }

    private static void WriteObject(StringBuilder sb, JsonObject obj, int indent, int depth)
    {
        // serde_json's PrettyFormatter writes `{}` with no newline when empty.
        if (obj.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        sb.Append('{');
        var first = true;
        foreach (var (key, value) in obj)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            NewLine(sb, indent, depth + 1);
            WriteString(sb, key);
            sb.Append(':');
            if (indent >= 0)
            {
                sb.Append(' ');
            }
            Write(sb, value, indent, depth + 1);
        }
        NewLine(sb, indent, depth);
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, JsonArray array, int indent, int depth)
    {
        if (array.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append('[');
        for (var i = 0; i < array.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            NewLine(sb, indent, depth + 1);
            Write(sb, array[i], indent, depth + 1);
        }
        NewLine(sb, indent, depth);
        sb.Append(']');
    }

    private static void NewLine(StringBuilder sb, int indent, int depth)
    {
        if (indent < 0)
        {
            return;
        }
        sb.Append('\n');
        sb.Append(' ', indent * depth);
    }

    private static void WriteValue(StringBuilder sb, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                sb.Append("true");
                return;
            case JsonValueKind.False:
                sb.Append("false");
                return;
            case JsonValueKind.Null:
                sb.Append("null");
                return;
            case JsonValueKind.String:
                WriteString(sb, value.GetValue<string>());
                return;
            case JsonValueKind.Number:
                sb.Append(Number(value));
                return;
            default:
                sb.Append("null");
                return;
        }
    }

    /// <summary>
    /// A number, formatted the way the <c>serde_json::Number</c> behind it would be.
    /// </summary>
    internal static string Number(JsonValue value)
    {
        // The underlying storage is what decides the spelling, so read it rather
        // than a converted view: TryGetValue<double> succeeds for an integer too
        // and would turn every 1 into 1.0.
        object? underlying;
        try
        {
            underlying = value.GetValue<object>();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            underlying = null;
        }

        return underlying switch
        {
            // A node parsed from JSON text keeps serde_json's own int-vs-float
            // split: a literal with no fraction and no exponent is an integer,
            // everything else is an f64.
            JsonElement element when element.ValueKind == JsonValueKind.Number =>
                FromLiteral(element.GetRawText()),
            // An integer built in-process is a serde_json integer.
            sbyte or byte or short or ushort or int or uint or long =>
                Convert.ToInt64(underlying, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            ulong unsigned => unsigned.ToString(CultureInfo.InvariantCulture),
            // Anything built from a CLR floating value is `Value::from(f64)`.
            double number => Ryu(number),
            float number => Ryu(number),
            decimal number => Ryu((double)number),
            _ => value.TryGetValue<double>(out var fallback) ? Ryu(fallback) : "null",
        };
    }

    private static string FromLiteral(string literal)
    {
        var isFloat = literal.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;
        if (!isFloat)
        {
            if (long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
            {
                return signed.ToString(CultureInfo.InvariantCulture);
            }
            if (ulong.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
            {
                return unsigned.ToString(CultureInfo.InvariantCulture);
            }
        }
        // Out-of-range integers and every fractional literal are f64 in
        // serde_json, so they come back through ryu.
        return double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Ryu(value)
            : literal;
    }

    /// <summary>
    /// serde_json's string escaping: only <c>"</c>, <c>\</c> and the C0 controls,
    /// with the five two-character shortcuts. No HTML escaping, no <c>\u</c> for
    /// non-ASCII.
    /// </summary>
    private static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>ryu's "pretty" f64 formatting, which is what serde_json emits.</summary>
    internal static string Ryu(double value)
    {
        if (!double.IsFinite(value))
        {
            // serde_json has no representation for these; `Value::from` maps them to null.
            return "null";
        }
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
            var sb = Start(negative, length + exponent + 2);
            sb.Append(digits);
            sb.Append('0', exponent);
            sb.Append(".0");
            return sb.ToString();
        }
        if (pointPosition > 0 && pointPosition <= 16)
        {
            var sb = Start(negative, length + 1);
            sb.Append(digits, 0, pointPosition);
            sb.Append('.');
            sb.Append(digits, pointPosition, length - pointPosition);
            return sb.ToString();
        }
        if (pointPosition > -5 && pointPosition <= 0)
        {
            var sb = Start(negative, length - pointPosition + 2);
            sb.Append("0.");
            sb.Append('0', -pointPosition);
            sb.Append(digits);
            return sb.ToString();
        }
        if (length == 1)
        {
            var sb = Start(negative, 8);
            sb.Append(digits);
            sb.Append('e');
            sb.Append((pointPosition - 1).ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

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
