using System.Globalization;
using System.Text;

namespace Obscura.Render.Css;

// ContainerType, GeneratedCounterStyle, GeneratedContentItem and
// AnimationEffectImpact are the shared lib.rs types; they live in the parent
// Obscura.Render namespace (Core/) and resolve here without a using directive.

/// <summary>
/// CSS media type used while selecting conditional author rules.
/// </summary>
/// <remarks>
/// Screen is the normal live-page mode. PDF export temporarily selects Print
/// so <c>@media print</c> and media-gated stylesheet blocks can participate
/// without mutating the document or changing JavaScript's live screen
/// environment.
/// </remarks>
public enum CssMediaType
{
    Screen = 0,
    Print = 1,
}

/// <summary>
/// Which viewport axis a percentage or range comparison resolves against.
/// </summary>
internal enum LengthAxis
{
    Width,
    Height,
}

/// <summary>
/// Value-level CSS helpers: string escapes, quoted/function argument slicing,
/// counter formatting, and generated-content parsing.
/// </summary>
public static class CssValues
{
    /// <summary>
    /// Decode CSS string escapes: <c>\</c> followed by 1-6 hex digits is a
    /// Unicode code point (<c>\200B</c> to U+200B ZERO WIDTH SPACE, ubiquitous
    /// in generated <c>content</c> for accessible section-edit-link brackets),
    /// with a single trailing whitespace character consumed as the escape's own
    /// terminator per the CSS spec rather than treated as literal content;
    /// anything else after a backslash (<c>\"</c>, <c>\\</c>) is a literal
    /// escaped character. Without this, a hex escape prints as its own literal
    /// digits.
    /// </summary>
    public static string UnescapeString(string value)
    {
        var output = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var current = value[index];
            index++;
            if (current != '\\')
            {
                output.Append(current);
                continue;
            }

            var hexStart = index;
            while (index - hexStart < 6 && index < value.Length && CssText.IsAsciiHexDigit(value[index]))
            {
                index++;
            }

            var hexLength = index - hexStart;
            if (hexLength > 0)
            {
                if (index < value.Length && CssText.IsWhitespace(value[index]))
                {
                    index++;
                }

                var codepoint = uint.Parse(value.AsSpan(hexStart, hexLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (codepoint <= 0x10FFFF && codepoint is not (>= 0xD800 and <= 0xDFFF))
                {
                    output.Append(char.ConvertFromUtf32((int)codepoint));
                    continue;
                }
            }

            if (index < value.Length)
            {
                output.Append(value[index]);
                index++;
            }
        }

        return output.ToString();
    }

    internal static bool IsCssIdentChar(char value) =>
        CssText.IsAlphanumeric(value) || value is '-' or '_' or '\\';

    /// <summary>
    /// Split a leading quoted string off <paramref name="value"/>, returning
    /// the raw (still escaped) body and the remainder. Null when the input does
    /// not start with a terminated quoted string.
    /// </summary>
    internal static (string Body, string Remainder)? TakeQuoted(string value)
    {
        if (value.Length == 0 || (value[0] != '"' && value[0] != '\''))
        {
            return null;
        }

        var quote = value[0];
        var escaped = false;
        for (var index = 1; index < value.Length; index++)
        {
            var current = value[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (current == '\\')
            {
                escaped = true;
            }
            else if (current == quote)
            {
                return (value[1..index], value[(index + 1)..]);
            }
        }

        return null;
    }

    /// <summary>
    /// Given text starting at a <c>(</c>, return the argument text and the
    /// remainder after the matching <c>)</c>.
    /// </summary>
    internal static (string Arguments, string Remainder)? TakeFunctionArguments(string value)
    {
        var depth = 0;
        char? quote = null;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (quote is { } open)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == open)
                {
                    quote = null;
                }

                continue;
            }

            switch (current)
            {
                case '"':
                case '\'':
                    quote = current;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (depth == 0)
                    {
                        return null;
                    }

                    depth--;
                    if (depth == 0)
                    {
                        return (value[1..index], value[(index + 1)..]);
                    }

                    break;
            }
        }

        return null;
    }

    internal static List<string> SplitFunctionArguments(string value)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        char? quote = null;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (quote is { } open)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == open)
                {
                    quote = null;
                }

                continue;
            }

            switch (current)
            {
                case '"':
                case '\'':
                    quote = current;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(depth - 1, 0);
                    break;
                case ',' when depth == 0:
                    result.Add(value[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        result.Add(value[start..].Trim());
        return result;
    }

    internal static bool IsValidGeneratedCounterName(string name) =>
        name.Length > 0 && name.All(IsCssIdentChar);

    internal static GeneratedCounterStyle? ParseGeneratedCounterStyle(string value) =>
        CssText.AsciiLower(value) switch
        {
            "decimal" => GeneratedCounterStyle.Decimal,
            "decimal-leading-zero" => GeneratedCounterStyle.DecimalLeadingZero,
            "lower-alpha" or "lower-latin" => GeneratedCounterStyle.LowerAlpha,
            "upper-alpha" or "upper-latin" => GeneratedCounterStyle.UpperAlpha,
            "lower-roman" => GeneratedCounterStyle.LowerRoman,
            "upper-roman" => GeneratedCounterStyle.UpperRoman,
            _ => null,
        };

    public static string FormatCounterValue(int value, GeneratedCounterStyle style) => style switch
    {
        GeneratedCounterStyle.Decimal => CssNumber.Format(value),
        GeneratedCounterStyle.DecimalLeadingZero when value is >= -9 and <= 9 =>
            value < 0
                ? "-" + Math.Abs((long)value).ToString("00", CultureInfo.InvariantCulture)
                : value.ToString("00", CultureInfo.InvariantCulture),
        GeneratedCounterStyle.DecimalLeadingZero => CssNumber.Format(value),
        GeneratedCounterStyle.LowerAlpha => AlphaCounter(value, uppercase: false),
        GeneratedCounterStyle.UpperAlpha => AlphaCounter(value, uppercase: true),
        GeneratedCounterStyle.LowerRoman => RomanCounter(value, uppercase: false),
        GeneratedCounterStyle.UpperRoman => RomanCounter(value, uppercase: true),
        _ => CssNumber.Format(value),
    };

    internal static string AlphaCounter(int value, bool uppercase)
    {
        if (value <= 0)
        {
            return CssNumber.Format(value);
        }

        var remaining = (uint)value;
        var digits = new List<char>();
        while (remaining > 0)
        {
            remaining -= 1;
            digits.Add((char)('a' + (remaining % 26)));
            remaining /= 26;
        }

        digits.Reverse();
        var result = new string([.. digits]);
        return uppercase ? CssText.AsciiUpper(result) : result;
    }

    internal static string RomanCounter(int value, bool uppercase)
    {
        if (value is < 1 or > 3999)
        {
            return CssNumber.Format(value);
        }

        ReadOnlySpan<(int Amount, string Numeral)> numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];

        var remaining = value;
        var result = new StringBuilder();
        foreach (var (amount, numeral) in numerals)
        {
            while (remaining >= amount)
            {
                remaining -= amount;
                result.Append(numeral);
            }
        }

        var text = result.ToString();
        return uppercase ? text : CssText.AsciiLower(text);
    }

    /// <summary>
    /// Parse a <c>content</c> value into generated-content items.
    /// </summary>
    /// <param name="value">The declaration value.</param>
    /// <param name="attributeLookup">
    /// Resolves <c>attr()</c> against the originating element. The Rust
    /// original reads the DOM node directly; the DOM is behind a delegate here
    /// so this file has no dependency on the tree port.
    /// </param>
    public static List<GeneratedContentItem>? ParseGeneratedContentItems(
        string value,
        Func<string, string?> attributeLookup)
    {
        var items = new List<GeneratedContentItem>();
        var rest = value.Trim();
        while (rest.Length != 0)
        {
            rest = rest.TrimStart();
            if (rest.Length == 0)
            {
                break;
            }

            var first = rest[0];
            if (first is '"' or '\'')
            {
                if (TakeQuoted(rest) is not { } quoted)
                {
                    return null;
                }

                items.Add(new GeneratedContentItem.Text(UnescapeString(quoted.Body)));
                rest = quoted.Remainder;
                continue;
            }

            var nameEnd = 0;
            while (nameEnd < rest.Length && IsCssIdentChar(rest[nameEnd]))
            {
                nameEnd++;
            }

            var name = rest[..nameEnd];
            var afterName = rest[nameEnd..].TrimStart();
            if (!afterName.StartsWith('('))
            {
                // Quote-control keywords are valid generated-content items, but
                // they do not contribute text in the current renderer.
                if (CssText.AsciiLower(name) is "open-quote" or "close-quote" or "no-open-quote" or "no-close-quote")
                {
                    rest = afterName;
                    continue;
                }

                return null;
            }

            if (TakeFunctionArguments(afterName) is not { } function)
            {
                return null;
            }

            var arguments = function.Arguments;

            switch (CssText.AsciiLower(name))
            {
                case "attr":
                {
                    var attribute = arguments
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();
                    if (string.IsNullOrEmpty(attribute))
                    {
                        return null;
                    }

                    items.Add(new GeneratedContentItem.Text(attributeLookup(attribute) ?? string.Empty));
                    break;
                }

                case "counter":
                {
                    var parts = SplitFunctionArguments(arguments);
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    var counterName = parts[0].Trim();
                    if (!IsValidGeneratedCounterName(counterName) || parts.Count > 2)
                    {
                        return null;
                    }

                    var style = GeneratedCounterStyle.Decimal;
                    if (parts.Count > 1)
                    {
                        if (ParseGeneratedCounterStyle(parts[1].Trim()) is not { } parsed)
                        {
                            return null;
                        }

                        style = parsed;
                    }

                    items.Add(new GeneratedContentItem.Counter(counterName, style));
                    break;
                }

                case "counters":
                {
                    var parts = SplitFunctionArguments(arguments);
                    if (parts.Count is < 2 or > 3)
                    {
                        return null;
                    }

                    var counterName = parts[0].Trim();
                    if (!IsValidGeneratedCounterName(counterName))
                    {
                        return null;
                    }

                    if (TakeQuoted(parts[1].Trim()) is not { } separatorToken)
                    {
                        return null;
                    }

                    if (separatorToken.Remainder.Trim().Length != 0)
                    {
                        return null;
                    }

                    var style = GeneratedCounterStyle.Decimal;
                    if (parts.Count > 2)
                    {
                        if (ParseGeneratedCounterStyle(parts[2].Trim()) is not { } parsed)
                        {
                            return null;
                        }

                        style = parsed;
                    }

                    items.Add(new GeneratedContentItem.Counters(counterName, UnescapeString(separatorToken.Body), style));
                    break;
                }

                default:
                    return null;
            }

            rest = function.Remainder;
        }

        return items.Count != 0 ? items : null;
    }

    /// <summary>
    /// Return the final valid <c>content</c> declaration in a declaration list.
    /// </summary>
    /// <returns>
    /// <c>null</c> when no declaration was found. A non-null result whose value
    /// is <c>null</c> means the pseudo is suppressed (<c>none</c>/<c>normal</c>,
    /// or an image-valued declaration whose text view is cleared).
    /// </returns>
    public static (bool Found, List<GeneratedContentItem>? Items) ExtractContent(
        string declarations,
        Func<string, string?> attributeLookup)
    {
        var result = (Found: false, Items: (List<GeneratedContentItem>?)null);
        foreach (var raw in CssDeclarations.Split(declarations))
        {
            var separator = raw.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            if (!CssText.EqualsAscii(raw[..separator].Trim(), "content"))
            {
                continue;
            }

            var value = raw[(separator + 1)..].Trim();
            if (CssText.EqualsAscii(value, "none") || CssText.EqualsAscii(value, "normal"))
            {
                result = (true, null);
                continue;
            }

            var parsed = ParseGeneratedContentItems(value, attributeLookup);
            if (parsed is not null)
            {
                result = (true, parsed);
            }
            else if (CssText.StartsWithAscii(value.TrimStart(), "url("))
            {
                // An image-valued content declaration supersedes any earlier
                // string declaration. The image itself is retained on the
                // computed style by the declaration applier; clearing the text
                // here keeps the two views of the winning declaration in sync
                // and, importantly, keeps the pseudo alive.
                result = (true, null);
            }
        }

        return result;
    }

    public static string GeneratedContentWithZeroCounters(IReadOnlyList<GeneratedContentItem> items)
    {
        var text = new StringBuilder();
        foreach (var item in items)
        {
            switch (item)
            {
                case GeneratedContentItem.Text value:
                    text.Append(value.Value);
                    break;
                case GeneratedContentItem.Counter counter:
                    text.Append(FormatCounterValue(0, counter.Style));
                    break;
                case GeneratedContentItem.Counters counters:
                    text.Append(FormatCounterValue(0, counters.Style));
                    break;
            }
        }

        return text.ToString();
    }
}
