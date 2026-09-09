using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Obscura.Cdp;

/// <summary>
/// The byte-exact JSON surface the CDP wire shares with the Rust engine.
/// </summary>
/// <remarks>
/// <para>
/// Every frame this server writes crosses to Puppeteer, Playwright and
/// chromiumoxide verbatim, so the bytes are a contract with the Rust
/// implementation. <c>System.Text.Json</c>'s own writer cannot produce them:
/// </para>
/// <list type="bullet">
/// <item><description>
/// its default encoder escapes <c>+ &lt; &gt; &amp; '</c> and every non-ASCII
/// code point, while <c>serde_json</c> escapes only <c>"</c>, <c>\</c> and the
/// C0 controls;
/// </description></item>
/// <item><description>
/// object keys go out in <em>insertion</em> order. <c>serde_json::Map</c> is a
/// <c>BTreeMap</c> by default, which would sort them, but <c>deno_core</c>
/// turns on <c>preserve_order</c> and cargo unifies that across the whole
/// workspace, so every <c>json!</c> literal in the CDP crate reaches the wire
/// in the order it was written. A standalone crate compiled against plain
/// <c>serde_json</c> sorts instead, so this is only observable against the real
/// binary - which is where it was caught;
/// </description></item>
/// <item><description>
/// a <c>serde_json</c> float always carries a decimal point (<c>5.0</c>, never
/// <c>5</c>) because it is printed with ryu's "pretty" formatter, and .NET
/// prints <c>5</c>.
/// </description></item>
/// </list>
/// <para>
/// This is the same problem <c>Obscura.Js.Ops.SerdeJson</c> solves for the op
/// boundary; the number formatting below is the same ryu reimplementation,
/// duplicated because that type is internal to <c>Obscura.Js</c>.
/// </para>
/// </remarks>
public static class CdpJson
{
    /// <summary>Serialize a value the way <c>serde_json::to_string(&amp;Value)</c> does.</summary>
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder(64);
        Write(sb, node);
        return sb.ToString();
    }

    /// <summary>
    /// Serialize the way <c>serde_json::to_string_pretty</c> does: two-space
    /// indent, <c>": "</c> between key and value, and an empty object or array
    /// left on one line. The <c>/json/*</c> endpoints hand this straight to
    /// DevTools clients, so the whitespace is part of the response body they
    /// compare against Chrome's.
    /// </summary>
    public static string SerializePretty(JsonNode? node)
    {
        var sb = new StringBuilder(128);
        WritePretty(sb, node, 0);
        return sb.ToString();
    }

    private static void WritePretty(StringBuilder output, JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.Count == 0)
                {
                    output.Append("{}");
                    return;
                }

                output.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    output.Append(first ? "\n" : ",\n");
                    first = false;
                    Indent(output, depth + 1);
                    AppendString(output, key);
                    output.Append(": ");
                    WritePretty(output, value, depth + 1);
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
                Write(output, node);
                return;
        }
    }

    private static void Indent(StringBuilder output, int depth) => output.Append(' ', depth * 2);

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
                // Insertion order, not sorted: serde_json is built with
                // `preserve_order` here, so its Map is an IndexMap.
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    first = false;
                    AppendString(output, key);
                    output.Append(':');
                    Write(output, value);
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
    /// Parse a CDP frame. Returns null for anything <c>serde_json::from_str</c>
    /// would reject, which is how every caller in the Rust server branches.
    /// </summary>
    public static JsonNode? Parse(string text, out bool ok)
    {
        try
        {
            var node = JsonNode.Parse(
                text,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                });
            ok = true;
            return node;
        }
        catch (JsonException)
        {
            ok = false;
            return null;
        }
        catch (ArgumentException)
        {
            ok = false;
            return null;
        }
    }

    /// <summary>Parse, treating any failure as "not a JSON document".</summary>
    public static JsonNode? Parse(string text) => Parse(text, out _);

    /// <summary>
    /// A <c>serde_json</c> string literal, quotes included. <c>+ &lt; &gt; &amp; '</c>,
    /// DEL and every non-ASCII code point stay raw; only <c>"</c>, <c>\</c> and the
    /// C0 controls are escaped.
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

    /// <summary>A <c>serde_json</c> string literal, quotes included.</summary>
    public static string String(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        AppendString(sb, value);
        return sb.ToString();
    }

    /// <summary>
    /// A <c>serde_json</c> float: ryu's "pretty" formatting, which always writes
    /// a decimal point and switches to exponents outside a fixed window.
    /// Non-finite input has no JSON form and becomes <c>null</c>, as
    /// <c>Value::from(f64)</c> does.
    /// </summary>
    public static string Number(double value) => double.IsFinite(value) ? Ryu(value) : "null";

    private static void WriteScalar(StringBuilder output, JsonNode node)
    {
        var value = node.AsValue();
        switch (node.GetValueKind())
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
                output.Append(FormatNumber(value));
                return;
            default:
                output.Append(node.ToJsonString());
                return;
        }
    }

    /// <summary>
    /// Reproduce <c>serde_json::Number</c>'s three arms: <c>u64</c>, <c>i64</c>
    /// and <c>f64</c>. A parsed literal with no fraction or exponent that fits an
    /// integer stays an integer and prints unchanged; anything else is an
    /// <c>f64</c> and goes through ryu.
    /// </summary>
    private static string FormatNumber(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            var raw = element.GetRawText();
            if (raw.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
            {
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64))
                {
                    return i64.ToString(CultureInfo.InvariantCulture);
                }

                if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u64))
                {
                    return u64.ToString(CultureInfo.InvariantCulture);
                }
            }

            return element.TryGetDouble(out var parsed) ? Number(parsed) : raw;
        }

        // Backed by a CLR value: the declared type decides integer vs float,
        // exactly as the Rust `json!` macro's `From` impls do.
        return value.GetValue<object>() switch
        {
            int v => v.ToString(CultureInfo.InvariantCulture),
            long v => v.ToString(CultureInfo.InvariantCulture),
            uint v => v.ToString(CultureInfo.InvariantCulture),
            ulong v => v.ToString(CultureInfo.InvariantCulture),
            short v => v.ToString(CultureInfo.InvariantCulture),
            ushort v => v.ToString(CultureInfo.InvariantCulture),
            byte v => v.ToString(CultureInfo.InvariantCulture),
            sbyte v => v.ToString(CultureInfo.InvariantCulture),
            double v => Number(v),
            float v => Number(v),
            decimal v => Number((double)v),
            var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? "null",
        };
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
            AppendExponent(sb, pointPosition - 1);
            return sb.ToString();
        }

        var scientific = Start(negative, length + 8);
        scientific.Append(digits[0]);
        scientific.Append('.');
        scientific.Append(digits, 1, length - 1);
        AppendExponent(scientific, pointPosition - 1);
        return scientific.ToString();
    }

    /// <summary>
    /// ryu writes an explicit sign on the exponent in both directions:
    /// <c>1e30</c> comes out as <c>1e+30</c>, not <c>1e30</c>. Dropping the plus
    /// still parses, but it is a different byte string from the reference.
    /// </summary>
    private static void AppendExponent(StringBuilder output, int exponent)
    {
        output.Append('e');
        if (exponent >= 0)
        {
            output.Append('+');
        }

        output.Append(exponent.ToString(CultureInfo.InvariantCulture));
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
    /// Split a .NET round-trip literal into sign, significant digits with no
    /// leading or trailing zeros, and the power of ten those digits scale by.
    /// .NET's shortest round-trip digits are the digits ryu computes.
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
