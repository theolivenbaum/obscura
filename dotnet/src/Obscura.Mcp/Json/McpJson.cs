using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Obscura.Mcp;

/// <summary>
/// The byte-exact JSON surface the MCP wire shares with the Rust engine.
/// </summary>
/// <remarks>
/// <para>
/// Every JSON-RPC frame this server writes reaches an MCP client verbatim, so the
/// bytes are the contract. <c>System.Text.Json</c>'s own writer cannot produce
/// them:
/// </para>
/// <list type="bullet">
/// <item><description>
/// its default encoder escapes <c>+ &lt; &gt; &amp; '</c> and every non-ASCII
/// code point, while <c>serde_json</c> escapes only <c>"</c>, <c>\</c> and the C0
/// controls;
/// </description></item>
/// <item><description>
/// a <c>serde_json</c> float always carries a decimal point (<c>5.0</c>, never
/// <c>5</c>) because it is printed with ryu's "pretty" formatter, and .NET prints
/// <c>5</c>;
/// </description></item>
/// <item><description>
/// <c>serde_json::Map</c> is an <c>IndexMap</c> in this workspace - <c>deno_core</c>
/// turns on <c>serde_json/preserve_order</c> and Cargo unifies that across every
/// crate here - so an object's keys go out in insertion order, which is what
/// <see cref="JsonObject"/> keeps. Confirmed with
/// <c>cargo tree -e features -i serde_json</c>.
/// </description></item>
/// </list>
/// <para>
/// Same problem <c>Obscura.Js.Ops.SerdeJson</c> solves for the op boundary; the
/// number formatting below is the same ryu reimplementation, duplicated because
/// that type is internal to <c>Obscura.Js</c>.
/// </para>
/// </remarks>
public static class McpJson
{
    /// <summary>Serialize a value the way <c>serde_json::to_string(&amp;Value)</c> does.</summary>
    public static string Serialize(JsonNode? node)
    {
        var output = new StringBuilder(64);
        Write(output, node);
        return output.ToString();
    }

    /// <summary>Serialize the way <c>serde_json::to_string_pretty(&amp;Value)</c> does.</summary>
    public static string SerializePretty(JsonNode? node)
    {
        var output = new StringBuilder(128);
        WritePretty(output, node, 0);
        return output.ToString();
    }

    /// <summary>UTF-8 bytes of <see cref="Serialize"/>, matching <c>serde_json::to_vec</c>.</summary>
    public static byte[] SerializeToUtf8(JsonNode? node) =>
        Encoding.UTF8.GetBytes(Serialize(node));

    /// <summary>Append a value the way <c>serde_json::to_string(&amp;Value)</c> writes it.</summary>
    public static void Write(StringBuilder output, JsonNode? node)
    {
        switch (node)
        {
            case null:
                output.Append("null");
                return;

            case JsonObject obj:
            {
                output.Append('{');
                var first = true;
                foreach (var pair in obj)
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    first = false;
                    AppendString(output, pair.Key);
                    output.Append(':');
                    Write(output, pair.Value);
                }

                output.Append('}');
                return;
            }

            case JsonArray array:
            {
                output.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Append(',');
                    }

                    Write(output, array[i]);
                }

                output.Append(']');
                return;
            }

            default:
                WriteScalar(output, node);
                return;
        }
    }

    /// <summary>
    /// serde_json's <c>PrettyFormatter</c>: two-space indent, <c>": "</c> between a
    /// key and its value, and an empty container printed as <c>{}</c> / <c>[]</c>.
    /// </summary>
    private static void WritePretty(StringBuilder output, JsonNode? node, int depth)
    {
        switch (node)
        {
            case null:
                output.Append("null");
                return;

            case JsonObject obj:
            {
                if (obj.Count == 0)
                {
                    output.Append("{}");
                    return;
                }

                output.Append('{');
                var first = true;
                foreach (var pair in obj)
                {
                    output.Append(first ? "\n" : ",\n");
                    first = false;
                    Indent(output, depth + 1);
                    AppendString(output, pair.Key);
                    output.Append(": ");
                    WritePretty(output, pair.Value, depth + 1);
                }

                output.Append('\n');
                Indent(output, depth);
                output.Append('}');
                return;
            }

            case JsonArray array:
            {
                if (array.Count == 0)
                {
                    output.Append("[]");
                    return;
                }

                output.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    output.Append(i == 0 ? "\n" : ",\n");
                    Indent(output, depth + 1);
                    WritePretty(output, array[i], depth + 1);
                }

                output.Append('\n');
                Indent(output, depth);
                output.Append(']');
                return;
            }

            default:
                WriteScalar(output, node);
                return;
        }
    }

    private static void Indent(StringBuilder output, int depth) => output.Append(' ', depth * 2);

    private static void WriteScalar(StringBuilder output, JsonNode node)
    {
        var value = (JsonValue)node;
        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
                output.Append("true");
                return;
            case JsonValueKind.False:
                output.Append("false");
                return;
            case JsonValueKind.Null:
                output.Append("null");
                return;
            case JsonValueKind.String:
                AppendString(output, value.GetValue<string>());
                return;
            case JsonValueKind.Number:
                // A parsed number keeps the client's own literal, which is what
                // serde_json does: `2` stays an integer and `2.0` stays a float.
                // A number this port built itself is a CLR double, i.e. the `f64`
                // a `json!` literal or a V8 number primitive carries, so it goes
                // through ryu-pretty and always keeps its decimal point.
                if (value.TryGetValue<JsonElement>(out var element))
                {
                    output.Append(element.GetRawText());
                }
                else if (value.TryGetValue<double>(out var number))
                {
                    output.Append(Number(number));
                }
                else
                {
                    output.Append(value.ToJsonString());
                }

                return;
            default:
                output.Append(value.ToJsonString());
                return;
        }
    }

    /// <summary>
    /// A <c>serde_json</c>-compatible string literal, including the quotes. serde
    /// escapes only <c>"</c>, <c>\</c> and the C0 controls and emits everything else
    /// as raw UTF-8.
    /// </summary>
    public static void AppendString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        output.Append("\\u00");
                        output.Append("0123456789abcdef"[c >> 4]);
                        output.Append("0123456789abcdef"[c & 0xF]);
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        output.Append('"');
    }

    /// <summary>A <c>serde_json</c> string literal, including the quotes.</summary>
    public static string String(string value)
    {
        var output = new StringBuilder(value.Length + 2);
        AppendString(output, value);
        return output.ToString();
    }

    /// <summary>
    /// A <c>serde_json</c> number. Non-finite input has no JSON representation and
    /// <c>Value::from(f64)</c> maps it to <c>null</c>; this does the same.
    /// </summary>
    public static string Number(double value) => double.IsFinite(value) ? Ryu(value) : "null";

    /// <summary>
    /// Rust's <c>Display</c> for <c>f64</c>: the shortest decimal that round-trips,
    /// written positionally with no exponent and no gratuitous trailing <c>.0</c>.
    /// This is what <c>format!("{}", value)</c> produces where the MCP tools splice
    /// a number into generated JavaScript.
    /// </summary>
    public static string Display(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        if (value == 0d)
        {
            return double.IsNegative(value) ? "-0" : "0";
        }

        var (negative, digits, exponent) = Decompose(value.ToString("R", CultureInfo.InvariantCulture));
        var length = digits.Length;
        var pointPosition = length + exponent;
        StringBuilder sb;
        if (exponent >= 0)
        {
            sb = Start(negative, length + exponent + 1);
            sb.Append(digits);
            sb.Append('0', exponent);
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
