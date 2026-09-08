namespace Obscura.Render.Css;

/// <summary>
/// Typed CSS length math: unit atoms, <c>calc()</c>, <c>min()</c>/<c>max()</c>,
/// <c>clamp()</c> and <c>round()</c>.
/// </summary>
/// <remarks>
/// SHARED CODE: a port of <c>obscura-render/src/style.rs</c>'s
/// <c>resolve_contextual_length</c> family. Media-query breakpoints reach it
/// through <see cref="EvalLengthSum"/>, so the CSS port needs it standalone;
/// the coordinator should reconcile it with the style.rs port.
/// </remarks>
public static class CssLength
{
    private readonly record struct Context(float EmPx, float RemPx, float Vw, float Vh, float PercentBase);

    public static float? ResolveContextual(
        string value,
        float emPx,
        float remPx,
        float vw,
        float vh,
        float percentBase) =>
        Resolve(value, new Context(emPx, remPx, vw, vh, percentBase));

    private static float? Resolve(string value, in Context context)
    {
        value = value.Trim();
        if (value.StartsWith('('))
        {
            var rest = value[1..];
            var end = FindMatchingParen(rest);
            if (end is { } close && close + 2 == value.Length)
            {
                return EvalCalc(rest[..close], context);
            }
        }

        if (value.StartsWith("var(", StringComparison.Ordinal))
        {
            var rest = value[4..];
            if (FindMatchingParen(rest) is not { } end)
            {
                return null;
            }

            var inner = rest[..end];
            var comma = inner.IndexOf(',');
            return comma < 0 ? null : Resolve(inner[(comma + 1)..].Trim(), context);
        }

        if (value.StartsWith("calc(", StringComparison.Ordinal))
        {
            var rest = value[5..];
            return FindMatchingParen(rest) is { } end ? EvalCalc(rest[..end], context) : null;
        }

        if (value.StartsWith("max(", StringComparison.Ordinal) || value.StartsWith("min(", StringComparison.Ordinal))
        {
            var isMax = value.StartsWith("max(", StringComparison.Ordinal);
            var rest = value[4..];
            if (FindMatchingParen(rest) is not { } end)
            {
                return null;
            }

            float? best = null;
            foreach (var argument in SplitTopLevel(rest[..end], ','))
            {
                if (EvalCalc(argument, context) is not { } candidate)
                {
                    continue;
                }

                if (best is null || (isMax ? candidate > best : candidate < best))
                {
                    best = candidate;
                }
            }

            return best;
        }

        if (value.StartsWith("clamp(", StringComparison.Ordinal))
        {
            var rest = value[6..];
            if (FindMatchingParen(rest) is { } end)
            {
                var arguments = SplitTopLevel(rest[..end], ',');
                if (arguments.Count == 3)
                {
                    var low = EvalCalc(arguments[0], context);
                    var preferred = EvalCalc(arguments[1], context);
                    var high = EvalCalc(arguments[2], context);
                    if (low is null || preferred is null || high is null)
                    {
                        return null;
                    }

                    return MathF.Max(MathF.Min(preferred.Value, high.Value), low.Value);
                }
            }
        }

        if (value.StartsWith("round(", StringComparison.Ordinal))
        {
            var rest = value[6..];
            if (FindMatchingParen(rest) is { } end)
            {
                var arguments = SplitTopLevel(rest[..end], ',');
                if (arguments.Count == 0)
                {
                    return null;
                }

                var (strategy, valueIndex) = arguments[0].Trim() switch
                {
                    "nearest" or "up" or "down" or "to-zero" => (arguments[0].Trim(), 1),
                    _ => ("nearest", 0),
                };

                if (valueIndex >= arguments.Count || EvalCalc(arguments[valueIndex].Trim(), context) is not { } resolved)
                {
                    return null;
                }

                var step = 1f;
                if (valueIndex + 1 < arguments.Count)
                {
                    if (EvalCalc(arguments[valueIndex + 1].Trim(), context) is not { } parsedStep)
                    {
                        return null;
                    }

                    step = parsedStep;
                }

                return RoundCssValue(resolved, step, strategy);
            }
        }

        return Atom(value, context);
    }

    private static float? RoundCssValue(float value, float step, string strategy)
    {
        if (!float.IsFinite(value) || !float.IsFinite(step) || step == 0f)
        {
            return null;
        }

        var quotient = value / MathF.Abs(step);
        var rounded = strategy switch
        {
            "up" => MathF.Ceiling(quotient),
            "down" => MathF.Floor(quotient),
            "to-zero" => MathF.Truncate(quotient),
            _ => MathF.Round(quotient, MidpointRounding.AwayFromZero),
        };

        return rounded * MathF.Abs(step);
    }

    private static float? Atom(string value, in Context context)
    {
        var lower = CssText.AsciiLower(value.Trim());

        if (Suffix(lower, "rem") is { } rem)
        {
            return rem * context.RemPx;
        }

        if (Suffix(lower, "em") is { } em)
        {
            return em * context.EmPx;
        }

        if (Suffix(lower, "ex") is { } ex)
        {
            return ex * context.EmPx * 0.528_320_3f;
        }

        if (Suffix(lower, "vmin") is { } vmin)
        {
            return vmin * MathF.Min(context.Vw, context.Vh);
        }

        if (Suffix(lower, "vmax") is { } vmax)
        {
            return vmax * MathF.Max(context.Vw, context.Vh);
        }

        if ((Suffix(lower, "dvw") ?? Suffix(lower, "svw") ?? Suffix(lower, "lvw")) is { } viewportWidth)
        {
            return viewportWidth * context.Vw;
        }

        if ((Suffix(lower, "dvh") ?? Suffix(lower, "svh") ?? Suffix(lower, "lvh")) is { } viewportHeight)
        {
            return viewportHeight * context.Vh;
        }

        if (Suffix(lower, "vw") is { } vw)
        {
            return vw * context.Vw;
        }

        if (Suffix(lower, "vh") is { } vh)
        {
            return vh * context.Vh;
        }

        if (Suffix(lower, "%") is { } percent)
        {
            return percent * context.PercentBase / 100f;
        }

        if (Suffix(lower, "px") is { } px)
        {
            return px;
        }

        if (Suffix(lower, "pt") is { } pt)
        {
            return pt * 1.333f;
        }

        return CssNumber.ParseFloat(lower.Trim());

        static float? Suffix(string value, string unit) =>
            value.EndsWith(unit, StringComparison.Ordinal)
                ? CssNumber.ParseFloat(value[..^unit.Length].Trim())
                : null;
    }

    private static float? EvalCalc(string expression, in Context context)
    {
        var terms = new List<(float Sign, string Term)>();
        var sign = 1f;
        var current = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var character in expression)
        {
            if (character == '(')
            {
                depth++;
                current.Append(character);
                continue;
            }

            if (character == ')')
            {
                depth--;
                current.Append(character);
                continue;
            }

            if (depth == 0 && character is '+' or '-')
            {
                if (FollowsProductOperator(current))
                {
                    current.Append(character);
                    continue;
                }

                if (current.ToString().Trim().Length != 0)
                {
                    terms.Add((sign, current.ToString()));
                    current.Clear();
                }

                sign = character == '-' ? -1f : 1f;
                continue;
            }

            current.Append(character);
        }

        if (current.ToString().Trim().Length != 0)
        {
            terms.Add((sign, current.ToString()));
        }

        if (terms.Count == 0)
        {
            return null;
        }

        var total = 0f;
        foreach (var (termSign, term) in terms)
        {
            if (EvalProduct(term.Trim(), context) is not { } value)
            {
                return null;
            }

            total += termSign * value;
        }

        return total;

        static bool FollowsProductOperator(System.Text.StringBuilder builder)
        {
            for (var index = builder.Length - 1; index >= 0; index--)
            {
                var character = builder[index];
                if (CssText.IsWhitespace(character))
                {
                    continue;
                }

                return character is '*' or '/';
            }

            return false;
        }
    }

    private static float? EvalProduct(string term, in Context context)
    {
        var factors = new List<(char Operator, string Factor)>();
        var op = '*';
        var depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (var character in term)
        {
            switch (character)
            {
                case '(':
                    depth++;
                    current.Append(character);
                    break;
                case ')':
                    depth--;
                    current.Append(character);
                    break;
                case '*':
                case '/':
                    if (depth != 0)
                    {
                        current.Append(character);
                        break;
                    }

                    if (current.ToString().Trim().Length == 0)
                    {
                        return null;
                    }

                    factors.Add((op, current.ToString()));
                    current.Clear();
                    op = character;
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.ToString().Trim().Length == 0)
        {
            return null;
        }

        factors.Add((op, current.ToString()));

        float? result = null;
        foreach (var (factorOperator, factor) in factors)
        {
            if (Resolve(factor, context) is not { } value)
            {
                return null;
            }

            result = result is null
                ? value
                : factorOperator == '/' ? result.Value / value : result.Value * value;
        }

        return result;
    }

    internal static int? FindMatchingParen(string value)
    {
        var depth = 1;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>Split on <paramref name="separator"/> at paren depth zero only.</summary>
    internal static List<string> SplitTopLevel(string value, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            switch (character)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                    if (character == separator && depth == 0)
                    {
                        parts.Add(value[start..index]);
                        start = index + 1;
                    }

                    break;
            }
        }

        parts.Add(value[start..]);
        return parts;
    }

    /// <summary>
    /// Find the closing parenthesis paired with an opening parenthesis
    /// immediately before <paramref name="input"/>. Nested CSS math groups must
    /// not terminate the outer calc.
    /// </summary>
    internal static int? MatchingParenEnd(string input)
    {
        var depth = 0;
        for (var index = 0; index < input.Length; index++)
        {
            switch (input[index])
            {
                case '(':
                    depth++;
                    break;
                case ')' when depth == 0:
                    return index;
                case ')':
                    depth--;
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Read a CSS length immediately following <paramref name="property"/>.
    /// Media-query <c>em</c> and <c>rem</c> units resolve against the initial
    /// font size (16 CSS px), not an element's computed font. Modern utility
    /// frameworks deliberately use those units for breakpoints, so treating only
    /// <c>px</c> as typed made every <c>min-width:64rem</c> desktop rule
    /// unconditional.
    /// </summary>
    internal static float? ExtractLength(string source, string property, (float Width, float Height) viewport, LengthAxis axis)
    {
        var found = source.IndexOf(property, StringComparison.Ordinal);
        if (found < 0)
        {
            return null;
        }

        var rest = source[(found + property.Length)..];
        if (rest.StartsWith("calc(", StringComparison.Ordinal))
        {
            var inner = rest[5..];
            return MatchingParenEnd(inner) is { } end ? EvalLengthSum(inner[..end], viewport, axis) : null;
        }

        return ParseLengthPrefix(rest, viewport, axis);
    }

    /// <summary>Read the length immediately before a range marker (<c>64rem&lt;=width</c>).</summary>
    internal static float? ExtractLengthBefore(string source, string marker, (float Width, float Height) viewport, LengthAxis axis)
    {
        var end = source.IndexOf(marker, StringComparison.Ordinal);
        if (end < 0)
        {
            return null;
        }

        var prefix = source[..end];
        var depth = 0;
        var start = 0;
        for (var index = prefix.Length - 1; index >= 0; index--)
        {
            var character = prefix[index];
            if (character == ')')
            {
                depth++;
                continue;
            }

            if (character == '(' && depth > 0)
            {
                depth--;
                continue;
            }

            if (character is '(' or ':' or ',' && depth == 0)
            {
                start = index + 1;
                break;
            }
        }

        var value = prefix[start..].Trim();
        if (value.StartsWith("calc(", StringComparison.Ordinal))
        {
            var inner = value[5..];
            return MatchingParenEnd(inner) is { } close ? EvalLengthSum(inner[..close], viewport, axis) : null;
        }

        return ParseLengthPrefix(value, viewport, axis);
    }

    internal static float? ParseLengthPrefix(string input, (float Width, float Height) viewport, LengthAxis axis)
    {
        var numericLength = 0;
        while (numericLength < input.Length
            && (CssText.IsAsciiDigit(input[numericLength]) || input[numericLength] is '.' or '+' or '-'))
        {
            numericLength++;
        }

        if (numericLength == 0)
        {
            return null;
        }

        if (CssNumber.ParseFloat(input.AsSpan(0, numericLength)) is not { } value)
        {
            return null;
        }

        var unitEnd = numericLength;
        while (unitEnd < input.Length && (CssText.IsAsciiAlphabetic(input[unitEnd]) || input[unitEnd] == '%'))
        {
            unitEnd++;
        }

        var unit = input[numericLength..unitEnd];
        float px;
        switch (unit)
        {
            case "":
            case "px":
                px = value;
                break;
            case "em":
            case "rem":
                px = value * 16f;
                break;
            case "vw":
                px = value * viewport.Width / 100f;
                break;
            case "vh":
                px = value * viewport.Height / 100f;
                break;
            case "vmin":
                px = value * MathF.Min(viewport.Width, viewport.Height) / 100f;
                break;
            case "vmax":
                px = value * MathF.Max(viewport.Width, viewport.Height) / 100f;
                break;
            case "in":
                px = value * 96f;
                break;
            case "cm":
                px = value * 96f / 2.54f;
                break;
            case "mm":
                px = value * 96f / 25.4f;
                break;
            case "q":
                px = value * 96f / 101.6f;
                break;
            case "pt":
                px = value * 96f / 72f;
                break;
            case "pc":
                px = value * 16f;
                break;
            case "%":
                px = axis == LengthAxis.Width
                    ? value * viewport.Width / 100f
                    : value * viewport.Height / 100f;
                break;
            default:
                return null;
        }

        return float.IsFinite(px) ? px : null;
    }

    /// <summary>
    /// Resolve media-query <c>calc()</c> lengths with the same typed CSS math
    /// used by layout. Real responsive breakpoints combine grouping and scalar
    /// arithmetic (for example <c>calc(1rem * 2 + (15rem + 2rem) * 2 + 31rem)</c>),
    /// so a flat plus/minus scanner silently turns those queries into
    /// unconditional rules.
    /// </summary>
    internal static float? EvalLengthSum(string expression, (float Width, float Height) viewport, LengthAxis axis)
    {
        var percentBase = axis == LengthAxis.Width ? viewport.Width : viewport.Height;
        return ResolveContextual(
            $"calc({expression})",
            16f,
            16f,
            viewport.Width / 100f,
            viewport.Height / 100f,
            percentBase);
    }
}
