// Port of the value-parsing primitives of `crates/obscura-render/src/style.rs`.
//
// The shared helpers that already landed with the CSS parser port are reused
// rather than re-ported: `CssDeclarations.Split`/`.Partition`
// (`split_declarations` / `partition_declarations`), `CssLength.ResolveContextual`
// (`resolve_contextual_length`), and `CssColor.Parse`/`.ParseForScheme`
// (`parse_color`, `parse_color_for_scheme`, `named_color`, `hsl_to_rgba`,
// `oklab_to_rgba`).
using Obscura.Render.Css;

namespace Obscura.Render;

/// <summary>
/// Computed-style lite: the layout-relevant subset of CSS, parsed from a small
/// built-in UA sheet plus already-cascaded declaration blocks.
/// </summary>
/// <remarks>
/// Port of <c>crates/obscura-render/src/style.rs</c>. Every entry point is
/// total: an unparseable declaration is skipped and leaves the previous cascade
/// winner in place, exactly as the Rust implementation does. Nothing here ever
/// throws on author input.
/// </remarks>
public static partial class ComputedStyle
{
    // ------------------------------------------------------------ tokenizing

    /// <summary>
    /// Rust <c>split_ws_paren</c>: split on whitespace at parenthesis depth 0, so a
    /// functional color like <c>rgba(0, 0, 0, .15)</c> stays one token.
    /// </summary>
    internal static List<string> SplitWsParen(string source)
    {
        List<string> output = [];
        int depth = 0;
        int? start = null;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (char.IsWhiteSpace(current) && depth == 0)
            {
                if (start is { } begin)
                {
                    output.Add(source[begin..index]);
                    start = null;
                }

                continue;
            }

            switch (current)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(depth - 1, 0);
                    break;
            }

            start ??= index;
        }

        if (start is { } last)
        {
            output.Add(source[last..]);
        }

        return output;
    }

    /// <summary>Rust <c>split_top_level</c>: split on a separator at parenthesis depth 0.</summary>
    internal static List<string> SplitTopLevel(string source, char separator) =>
        CssLength.SplitTopLevel(source, separator);

    /// <summary>Rust <c>find_matching_paren</c>.</summary>
    internal static int? FindMatchingParen(string source) => CssLength.FindMatchingParen(source);

    /// <summary>Rust <c>value.split_whitespace()</c> as a list.</summary>
    internal static List<string> SplitWhitespace(string source)
    {
        List<string> parts = [];
        int index = 0;
        while (index < source.Length)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            int start = index;
            while (index < source.Length && !char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            if (index > start)
            {
                parts.Add(source[start..index]);
            }
        }

        return parts;
    }

    /// <summary>Rust <c>token</c>: the first whitespace-separated token, if any.</summary>
    internal static string? Token(string value)
    {
        List<string> parts = SplitWhitespace(value);
        return parts.Count == 0 ? null : parts[0];
    }

    // ------------------------------------------------------------- numbers

    /// <summary>Rust <c>str::parse::&lt;f32&gt;()</c>.</summary>
    internal static float? ParseF32(string value) => CssNumber.ParseFloat(value);

    private static ulong? ParseDigits(string value, out bool negative, out bool overflow)
    {
        negative = false;
        overflow = false;
        int index = 0;
        if (value.Length > 0 && (value[0] == '+' || value[0] == '-'))
        {
            negative = value[0] == '-';
            index = 1;
        }

        if (index >= value.Length)
        {
            return null;
        }

        ulong accumulator = 0;
        for (; index < value.Length; index++)
        {
            char digit = value[index];
            if (digit is < '0' or > '9')
            {
                return null;
            }

            if (accumulator > (ulong.MaxValue - (ulong)(digit - '0')) / 10)
            {
                overflow = true;
                accumulator = ulong.MaxValue;
                continue;
            }

            accumulator = (accumulator * 10) + (ulong)(digit - '0');
        }

        return accumulator;
    }

    /// <summary>Rust <c>str::parse::&lt;i32&gt;()</c>.</summary>
    internal static int? ParseI32(string value) => ParseSigned(value, int.MinValue, int.MaxValue) is { } parsed
        ? (int)parsed
        : null;

    /// <summary>Rust <c>str::parse::&lt;i16&gt;()</c>.</summary>
    internal static short? ParseI16(string value) => ParseSigned(value, short.MinValue, short.MaxValue) is { } parsed
        ? (short)parsed
        : null;

    private static long? ParseSigned(string value, long min, long max)
    {
        if (ParseDigits(value, out bool negative, out bool overflow) is not { } magnitude || overflow)
        {
            return null;
        }

        if (negative)
        {
            if (magnitude > (ulong)(-(min + 1)) + 1)
            {
                return null;
            }

            return magnitude == 0 ? 0L : -(long)magnitude;
        }

        return magnitude > (ulong)max ? null : (long)magnitude;
    }

    /// <summary>Rust <c>str::parse::&lt;u16&gt;()</c>.</summary>
    internal static ushort? ParseU16(string value) => ParseUnsigned(value, ushort.MaxValue) is { } parsed
        ? (ushort)parsed
        : null;

    /// <summary>Rust <c>str::parse::&lt;u32&gt;()</c>.</summary>
    internal static uint? ParseU32(string value) => ParseUnsigned(value, uint.MaxValue) is { } parsed
        ? (uint)parsed
        : null;

    /// <summary>Rust <c>str::parse::&lt;usize&gt;()</c>, capped at <see cref="int.MaxValue"/>.</summary>
    internal static int? ParseUsize(string value) => ParseUnsigned(value, int.MaxValue) is { } parsed
        ? (int)parsed
        : null;

    private static ulong? ParseUnsigned(string value, ulong max)
    {
        if (ParseDigits(value, out bool negative, out bool overflow) is not { } magnitude
            || overflow
            || (negative && magnitude != 0))
        {
            return null;
        }

        return magnitude > max ? null : magnitude;
    }

    /// <summary>
    /// Rust <c>value.parse::&lt;u64&gt;().unwrap_or(u64::MAX)</c> over an all-digit string.
    /// </summary>
    internal static ulong ParseU64Saturating(string value)
    {
        if (ParseDigits(value, out _, out bool overflow) is not { } magnitude)
        {
            return ulong.MaxValue;
        }

        return overflow ? ulong.MaxValue : magnitude;
    }

    // ------------------------------------------------------------- lengths

    /// <summary>Rust <c>px_value</c>: a bare length token in CSS pixels.</summary>
    internal static float? PxValue(string token)
    {
        string number = token;
        float scale = 1.0f;

        if (number.EndsWith("px", StringComparison.Ordinal))
        {
            number = number[..^2];
        }
        else if (number.EndsWith("pt", StringComparison.Ordinal))
        {
            number = number[..^2];
            scale = 1.333f;
        }
        else if (number.EndsWith("em", StringComparison.Ordinal) || number.EndsWith("rem", StringComparison.Ordinal))
        {
            number = TrimEndAsciiAlphabetic(number);
            scale = 16.0f;
        }
        else if (number.EndsWith("ex", StringComparison.Ordinal))
        {
            number = TrimEndAsciiAlphabetic(number);
            scale = 16.0f * 0.528_320_3f;
        }
        else if (number.EndsWith('%'))
        {
            number = number[..^1];
            scale = 16.0f / 100.0f;
        }
        else
        {
            number = TrimEndAsciiAlphabetic(number);
        }

        foreach (char character in number)
        {
            if (!(CssText.IsAsciiDigit(character) || character == '.' || character == '-'))
            {
                return null;
            }
        }

        return ParseF32(number) is { } parsed ? parsed * scale : null;
    }

    private static string TrimEndAsciiAlphabetic(string value)
    {
        int end = value.Length;
        while (end > 0 && CssText.IsAsciiAlphabetic(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    /// <summary>Rust <c>px</c>: the first length in a value as CSS pixels.</summary>
    internal static float? Px(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Contains('('))
        {
            return ResolveLength(trimmed);
        }

        return Token(value) is { } token ? PxValue(token) : null;
    }

    /// <summary>Rust <c>deferred_length_expression</c>.</summary>
    internal static string? DeferredLengthExpression(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Contains('(') ? trimmed : null;
    }

    /// <summary>
    /// Record whether one of the six box-size slots was declared as the CSS-wide keyword
    /// <c>inherit</c>, so the top-down pass can copy the parent's computed value.
    /// </summary>
    /// <remarks>
    /// DEVIATION: crates/obscura-render has no counterpart and drops <c>inherit</c> on these
    /// properties. They are not inherited, so the keyword has to copy the parent's computed
    /// value explicitly; Tesserae's annotated text editor sizes its textarea with
    /// <c>min-height: inherit</c> and got the initial value instead.
    /// </remarks>
    internal static void SetSizeInherit(LayoutStyle style, int slot, string value)
    {
        byte bit = (byte)(1 << slot);
        if (CssText.EqualsAscii(value.Trim(), "inherit"))
        {
            style.SizeInherit |= bit;
        }
        else
        {
            style.SizeInherit &= (byte)~bit;
        }
    }

    /// <summary>Rust <c>resolve_contextual_length</c>. Delegates to the shared CSS port.</summary>
    public static float? ResolveContextualLength(
        string value,
        float emPx,
        float remPx,
        float vw,
        float vh,
        float percentBase) =>
        CssLength.ResolveContextual(value, emPx, remPx, vw, vh, percentBase);

    /// <summary>Rust <c>functional_percentage_factor</c>.</summary>
    internal static float? FunctionalPercentageFactor(string value)
    {
        if (!value.Contains('%') || !value.Contains('('))
        {
            return null;
        }

        if (ResolveContextualLength(value, 16f, 16f, 1f, 1f, 0f) is not { } atZero
            || ResolveContextualLength(value, 16f, 16f, 1f, 1f, 100f) is not { } atHundred
            || ResolveContextualLength(value, 16f, 16f, 1f, 1f, 200f) is not { } atTwoHundred)
        {
            return null;
        }

        const float tolerance = 0.0001f;
        if (MathF.Abs(atZero) > tolerance
            || MathF.Abs(atTwoHundred - (atHundred * 2f)) > tolerance
            || !float.IsFinite(atHundred))
        {
            return null;
        }

        return atHundred / 100f;
    }

    /// <summary>Rust <c>round_css_value</c>.</summary>
    internal static float? RoundCssValue(float value, float step, string strategy)
    {
        if (!float.IsFinite(value) || !float.IsFinite(step) || step == 0f)
        {
            return null;
        }

        float quotient = value / MathF.Abs(step);
        float rounded = strategy switch
        {
            "up" => MathF.Ceiling(quotient),
            "down" => MathF.Floor(quotient),
            "to-zero" => MathF.Truncate(quotient),
            _ => F32.Round(quotient),
        };

        return rounded * MathF.Abs(step);
    }

    /// <summary>Rust <c>resolve_length</c>: context-free functional length resolution.</summary>
    internal static float? ResolveLength(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('('))
        {
            string rest = trimmed[1..];
            if (FindMatchingParen(rest) is not { } grouped)
            {
                return null;
            }

            if (grouped + 2 == trimmed.Length)
            {
                return EvalCalc(rest[..grouped]);
            }
        }

        if (trimmed.StartsWith("var(", StringComparison.Ordinal))
        {
            string rest = trimmed[4..];
            if (FindMatchingParen(rest) is not { } end)
            {
                return null;
            }

            string inner = rest[..end];
            int comma = inner.IndexOf(',');
            return comma < 0 ? null : ResolveLength(inner[(comma + 1)..].Trim());
        }

        if (trimmed.StartsWith("calc(", StringComparison.Ordinal))
        {
            string rest = trimmed[5..];
            return FindMatchingParen(rest) is { } end ? EvalCalc(rest[..end]) : null;
        }

        if (trimmed.StartsWith("max(", StringComparison.Ordinal) || trimmed.StartsWith("min(", StringComparison.Ordinal))
        {
            bool isMax = trimmed.StartsWith("max(", StringComparison.Ordinal);
            string rest = trimmed[4..];
            if (FindMatchingParen(rest) is not { } end)
            {
                return null;
            }

            float? best = null;
            foreach (string argument in SplitTopLevel(rest[..end], ','))
            {
                if (EvalCalc(argument) is not { } candidate)
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

        if (trimmed.StartsWith("clamp(", StringComparison.Ordinal))
        {
            string rest = trimmed[6..];
            if (FindMatchingParen(rest) is { } end)
            {
                List<string> arguments = SplitTopLevel(rest[..end], ',');
                if (arguments.Count == 3)
                {
                    if (EvalCalc(arguments[0].Trim()) is not { } low
                        || EvalCalc(arguments[1].Trim()) is not { } preferred
                        || EvalCalc(arguments[2].Trim()) is not { } high)
                    {
                        return null;
                    }

                    return F32.Max(F32.Min(preferred, high), low);
                }
            }
        }

        if (trimmed.StartsWith("round(", StringComparison.Ordinal))
        {
            string rest = trimmed[6..];
            if (FindMatchingParen(rest) is { } end)
            {
                List<string> arguments = SplitTopLevel(rest[..end], ',');
                if (arguments.Count == 0)
                {
                    return null;
                }

                (string strategy, int valueIndex) = arguments[0].Trim() switch
                {
                    "nearest" or "up" or "down" or "to-zero" => (arguments[0].Trim(), 1),
                    _ => ("nearest", 0),
                };

                if (valueIndex >= arguments.Count || EvalCalc(arguments[valueIndex].Trim()) is not { } resolved)
                {
                    return null;
                }

                float step = 1f;
                if (valueIndex + 1 < arguments.Count)
                {
                    if (EvalCalc(arguments[valueIndex + 1].Trim()) is not { } parsedStep)
                    {
                        return null;
                    }

                    step = parsedStep;
                }

                return RoundCssValue(resolved, step, strategy);
            }
        }

        if (trimmed.Contains('('))
        {
            // An unhandled function (env(), ...): no safe fallback.
            return null;
        }

        return PxValue(trimmed) ?? ParseF32(trimmed);
    }

    /// <summary>Rust <c>eval_calc</c>.</summary>
    internal static float? EvalCalc(string expression)
    {
        List<(float Sign, string Term)> terms = [];
        float sign = 1.0f;
        System.Text.StringBuilder current = new();
        int depth = 0;
        foreach (char character in expression)
        {
            if (character == '(')
            {
                depth++;
                current.Append(character);
            }
            else if (character == ')')
            {
                depth--;
                current.Append(character);
            }
            else if ((character == '+' || character == '-') && depth == 0)
            {
                if (FollowsProductOperator(current))
                {
                    current.Append(character);
                }
                else
                {
                    if (current.ToString().Trim().Length != 0)
                    {
                        terms.Add((sign, current.ToString()));
                        current.Clear();
                    }

                    sign = character == '-' ? -1.0f : 1.0f;
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.ToString().Trim().Length != 0)
        {
            terms.Add((sign, current.ToString()));
        }

        if (terms.Count == 0)
        {
            return null;
        }

        float total = 0f;
        foreach ((float termSign, string term) in terms)
        {
            if (EvalProduct(term.Trim()) is not { } value)
            {
                return null;
            }

            total += termSign * value;
        }

        return total;
    }

    private static bool FollowsProductOperator(System.Text.StringBuilder builder)
    {
        for (int index = builder.Length - 1; index >= 0; index--)
        {
            char character = builder[index];
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            return character is '*' or '/';
        }

        return false;
    }

    /// <summary>Rust <c>eval_product</c>.</summary>
    internal static float? EvalProduct(string term)
    {
        float? result = null;
        char op = '*';
        int depth = 0;
        System.Text.StringBuilder current = new();
        List<(char Operator, string Factor)> factors = [];
        foreach (char character in term)
        {
            if (character == '(')
            {
                depth++;
                current.Append(character);
            }
            else if (character == ')')
            {
                depth--;
                current.Append(character);
            }
            else if ((character == '*' || character == '/') && depth == 0)
            {
                if (current.ToString().Trim().Length == 0)
                {
                    return null;
                }

                factors.Add((op, current.ToString()));
                current.Clear();
                op = character;
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.ToString().Trim().Length == 0)
        {
            return null;
        }

        factors.Add((op, current.ToString()));

        foreach ((char factorOperator, string factor) in factors)
        {
            if (ResolveLength(factor) is not { } value)
            {
                return null;
            }

            result = result is null
                ? value
                : factorOperator == '/' ? result.Value / value : result.Value * value;
        }

        return result;
    }

    /// <summary>Rust <c>dimension_value</c>.</summary>
    internal static Dimension DimensionValue(string token)
    {
        string value = token.Trim();
        if (CssText.EqualsAscii(value, "auto") || value.Length == 0)
        {
            return Dimension.Auto;
        }

        if (value.Contains('('))
        {
            return Px(value) is { } pixels ? Dimension.Px(pixels) : Dimension.Auto;
        }

        string lower = CssText.AsciiLower(value);

        if (Suffix(lower, "%") is { } percent)
        {
            return Dimension.Percent(percent / 100f);
        }

        if (Suffix(lower, "rem") is { } rem)
        {
            return Dimension.Rem(rem);
        }

        if (Suffix(lower, "em") is { } em)
        {
            return Dimension.Em(em);
        }

        if (Suffix(lower, "ex") is { } ex)
        {
            return Dimension.Ex(ex);
        }

        if (Suffix(lower, "vmin") is { } vmin)
        {
            return Dimension.Vmin(vmin);
        }

        if (Suffix(lower, "vmax") is { } vmax)
        {
            return Dimension.Vmax(vmax);
        }

        if ((Suffix(lower, "dvw") ?? Suffix(lower, "svw") ?? Suffix(lower, "lvw")) is { } logicalVw)
        {
            return Dimension.Vw(logicalVw);
        }

        if ((Suffix(lower, "dvh") ?? Suffix(lower, "svh") ?? Suffix(lower, "lvh")) is { } logicalVh)
        {
            return Dimension.Vh(logicalVh);
        }

        if (Suffix(lower, "vw") is { } vw)
        {
            return Dimension.Vw(vw);
        }

        if (Suffix(lower, "vh") is { } vh)
        {
            return Dimension.Vh(vh);
        }

        if (Suffix(lower, "px") is { } px)
        {
            return Dimension.Px(px);
        }

        if (Suffix(lower, "pt") is { } pt)
        {
            return Dimension.Px(pt * 1.333f);
        }

        // CSS lengths accept a unitless number only when it is zero.
        if (ParseF32(lower.Trim()) is { } bare && bare == 0f)
        {
            return Dimension.Px(bare);
        }

        return Dimension.Auto;

        static float? Suffix(string value, string unit) =>
            value.EndsWith(unit, StringComparison.Ordinal)
                ? ParseF32(value[..^unit.Length].Trim())
                : null;
    }

    /// <summary>Rust <c>percent_fraction</c>.</summary>
    internal static float? PercentFraction(string token)
    {
        string trimmed = token.Trim();
        if (!trimmed.EndsWith('%'))
        {
            return null;
        }

        if (ParseF32(trimmed[..^1].Trim()) is not { } value)
        {
            return null;
        }

        return float.IsFinite(value) ? value / 100f : null;
    }

    /// <summary>Rust <c>two</c>: a 1-or-2 value shorthand split into (start, end).</summary>
    internal static (string Start, string End) Two(string value)
    {
        List<string> values = SplitWsParen(value);
        string first = values.Count > 0 ? values[0] : "0";
        string second = values.Count > 1 ? values[1] : first;
        return (first, second);
    }

    /// <summary>Rust's 1-4 value CSS shorthand expansion (all / v h / t h b / t r b l).</summary>
    internal static (string Top, string Right, string Bottom, string Left)? ExpandBoxShorthand(
        IReadOnlyList<string> tokens) => tokens.Count switch
        {
            0 => null,
            1 => (tokens[0], tokens[0], tokens[0], tokens[0]),
            2 => (tokens[0], tokens[1], tokens[0], tokens[1]),
            3 => (tokens[0], tokens[1], tokens[2], tokens[1]),
            _ => (tokens[0], tokens[1], tokens[2], tokens[3]),
        };

    // -------------------------------------------------------- misc keywords

    /// <summary>Rust <c>list_style_keyword</c>.</summary>
    internal static ListStyle? ListStyleKeyword(string token) => token.Trim() switch
    {
        "none" => ListStyle.None,
        "disc" => ListStyle.Disc,
        "circle" => ListStyle.Circle,
        "square" => ListStyle.Square,
        "decimal" or "decimal-leading-zero" => ListStyle.Decimal,
        _ => null,
    };

    /// <summary>Rust <c>font_size_keyword</c>.</summary>
    internal static float? FontSizeKeyword(string value) => CssText.AsciiLower(value) switch
    {
        "xx-small" => 9.6f,
        "x-small" => 12.0f,
        "small" => 13.3f,
        "medium" => 16.0f,
        "large" => 18.0f,
        "x-large" => 24.0f,
        "xx-large" => 32.0f,
        _ => null,
    };

    /// <summary>Rust <c>is_font_size_token</c>.</summary>
    internal static bool IsFontSizeToken(string value)
    {
        string lower = CssText.AsciiLower(value.Trim());
        if (lower == "0" || FontSizeKeyword(lower) is not null)
        {
            return true;
        }

        if (lower.StartsWith("calc(", StringComparison.Ordinal)
            || lower.StartsWith("min(", StringComparison.Ordinal)
            || lower.StartsWith("max(", StringComparison.Ordinal)
            || lower.StartsWith("clamp(", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (string unit in FontSizeUnits)
        {
            if (lower.EndsWith(unit, StringComparison.Ordinal)
                && ParseF32(lower[..^unit.Length]) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] FontSizeUnits =
    [
        "px", "pt", "em", "ex", "rem", "vw", "vh", "dvw", "dvh", "svw", "svh", "lvw", "lvh",
        "vmin", "vmax", "%",
    ];

    /// <summary>Rust <c>specified_font_weight</c>.</summary>
    internal static string? SpecifiedFontWeight(string value)
    {
        string lower = CssText.AsciiLower(value.Trim());
        switch (lower)
        {
            case "normal":
                return "400";
            case "bold":
                return "700";
            case "bolder":
            case "lighter":
                return lower;
        }

        if (ParseF32(lower) is not { } weight || !float.IsFinite(weight) || weight < 1.0f || weight > 1000.0f)
        {
            return null;
        }

        return FormatRustF32(F32.Round(weight));
    }

    /// <summary>Rust <c>computed_font_weight</c>.</summary>
    public static ushort ComputedFontWeight(string? specified, ushort inherited)
    {
        if (specified is null)
        {
            return inherited;
        }

        switch (specified)
        {
            case "inherit":
            case "unset":
                return inherited;
            case "normal":
            case "initial":
                return 400;
            case "bold":
                return 700;
            case "bolder":
                if (inherited < 100)
                {
                    return 400;
                }

                if (inherited < 350)
                {
                    return 400;
                }

                if (inherited < 550)
                {
                    return 700;
                }

                return inherited < 900 ? (ushort)900 : inherited;
            case "lighter":
                if (inherited < 100)
                {
                    return inherited;
                }

                if (inherited < 350)
                {
                    return 100;
                }

                if (inherited < 550)
                {
                    return 100;
                }

                return inherited < 750 ? (ushort)400 : (ushort)700;
        }

        if (ParseF32(specified) is not { } weight || !float.IsFinite(weight))
        {
            return inherited;
        }

        return (ushort)Math.Clamp(F32.Round(weight), 1.0f, 1000.0f);
    }

    /// <summary>Rust <c>used_font_weight</c>.</summary>
    internal static ushort UsedFontWeight(LayoutStyle style) => ComputedFontWeight(style.FontWeight, 400);

    /// <summary>Rust <c>line_height_expression_is_length</c>.</summary>
    internal static bool LineHeightExpressionIsLength(string value)
    {
        string lower = CssText.AsciiLower(value);
        if (lower.Contains('%'))
        {
            return true;
        }

        foreach (string unit in LineHeightUnits)
        {
            if (lower.Contains(unit, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] LineHeightUnits =
    [
        "px", "pt", "pc", "in", "cm", "mm", "rem", "em", "ex", "vw", "vh", "vmin", "vmax",
    ];

    /// <summary>Rust <c>non_none_value</c>.</summary>
    internal static bool NonNoneValue(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length != 0 && !CssText.EqualsAscii(trimmed, "none");
    }

    /// <summary>Rust <c>scale_number</c>.</summary>
    internal static float? ScaleNumber(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.EndsWith('%'))
        {
            if (ParseF32(trimmed[..^1].Trim()) is not { } percentage)
            {
                return null;
            }

            float scaled = percentage / 100f;
            return float.IsFinite(scaled) ? scaled : null;
        }

        return ParseF32(trimmed) is { } number && float.IsFinite(number) ? number : null;
    }

    /// <summary>Rust <c>angle_degrees</c>.</summary>
    internal static float? AngleDegrees(string value)
    {
        string trimmed = value.Trim();
        if (!trimmed.StartsWith("calc(", StringComparison.Ordinal) || !trimmed.EndsWith(')'))
        {
            float? direct = Primitive(trimmed);
            return direct is { } angle && float.IsFinite(angle) ? angle : null;
        }

        string inner = trimmed[5..^1];
        float? result;
        int star = inner.IndexOf('*');
        int slash = inner.IndexOf('/');
        if (star >= 0)
        {
            if (Primitive(inner[..star]) is not { } angle || ParseF32(inner[(star + 1)..].Trim()) is not { } factor)
            {
                return null;
            }

            result = angle * factor;
        }
        else if (slash >= 0)
        {
            if (ParseF32(inner[(slash + 1)..].Trim()) is not { } divisor)
            {
                return null;
            }

            result = divisor != 0f && Primitive(inner[..slash]) is { } angle ? angle / divisor : null;
        }
        else
        {
            result = Primitive(inner);
        }

        return result is { } value2 && float.IsFinite(value2) ? value2 : null;

        static float? Primitive(string raw)
        {
            string lower = CssText.AsciiLower(raw.Trim());
            if (lower.EndsWith("deg", StringComparison.Ordinal))
            {
                return ParseF32(lower[..^3].Trim());
            }

            if (lower.EndsWith("grad", StringComparison.Ordinal))
            {
                return ParseF32(lower[..^4].Trim()) is { } gradians ? gradians * 0.9f : null;
            }

            if (lower.EndsWith("rad", StringComparison.Ordinal))
            {
                return ParseF32(lower[..^3].Trim()) is { } radians ? radians * (180f / MathF.PI) : null;
            }

            if (lower.EndsWith("turn", StringComparison.Ordinal))
            {
                return ParseF32(lower[..^4].Trim()) is { } turns ? turns * 360f : null;
            }

            return lower == "0" ? 0f : null;
        }
    }

    /// <summary>Rust <c>parse_css_angle</c>.</summary>
    internal static float? ParseCssAngle(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.EndsWith("deg", StringComparison.Ordinal))
        {
            return ParseF32(trimmed[..^3].Trim());
        }

        if (trimmed.EndsWith("turn", StringComparison.Ordinal))
        {
            return ParseF32(trimmed[..^4].Trim()) is { } turns ? turns * 360f : null;
        }

        if (trimmed.EndsWith("grad", StringComparison.Ordinal))
        {
            return ParseF32(trimmed[..^4].Trim()) is { } gradians ? gradians * 0.9f : null;
        }

        if (trimmed.EndsWith("rad", StringComparison.Ordinal))
        {
            return ParseF32(trimmed[..^3].Trim()) is { } radians ? radians * (180f / MathF.PI) : null;
        }

        return null;
    }

    /// <summary>Rust <c>parse_aspect_ratio</c>.</summary>
    internal static float? ParseAspectRatio(string value)
    {
        string trimmed = value.Trim();
        if (CssText.EqualsAscii(trimmed, "auto"))
        {
            return null;
        }

        List<string> kept = [];
        foreach (string token in SplitWhitespace(trimmed))
        {
            if (!CssText.EqualsAscii(token, "auto"))
            {
                kept.Add(token);
            }
        }

        string ratioPart = string.Join(" ", kept);
        int slash = ratioPart.IndexOf('/');
        if (slash >= 0)
        {
            if (ParseF32(ratioPart[..slash].Trim()) is not { } width
                || ParseF32(ratioPart[(slash + 1)..].Trim()) is not { } height)
            {
                return null;
            }

            return height > 0f && width > 0f ? width / height : null;
        }

        if (ParseF32(ratioPart.Trim()) is not { } ratio)
        {
            return null;
        }

        return float.IsFinite(ratio) && ratio > 0f ? ratio : null;
    }

    /// <summary>Rust <c>parse_url</c>.</summary>
    internal static string? ParseUrl(string value)
    {
        int marker = value.IndexOf("url(", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        int start = marker + 4;
        int depth = 1;
        char? quote = null;
        bool escaped = false;
        int? end = null;
        for (int index = start; index < value.Length; index++)
        {
            char character = value[index];
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

            if (quote is { } active)
            {
                if (character == active)
                {
                    quote = null;
                }

                continue;
            }

            switch (character)
            {
                case '\'':
                case '"':
                    quote = character;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        end = index;
                    }

                    break;
            }

            if (end is not null)
            {
                break;
            }
        }

        if (end is not { } close)
        {
            return null;
        }

        string inner = value[start..close].Trim();
        string unquoted = inner.Trim('"', '\'');
        return unquoted.Length == 0 ? null : unquoted;
    }

    /// <summary>Rust <c>parse_column_count</c>.</summary>
    internal static ushort? ParseColumnCount(string value)
    {
        if (ParseU32(value.Trim()) is not { } count || count == 0)
        {
            return null;
        }

        return (ushort)Math.Min(count, 64u);
    }

    /// <summary>Rust <c>valid_counter_name</c>.</summary>
    internal static bool ValidCounterName(string name)
    {
        string lower = CssText.AsciiLower(name);
        if (name.Length == 0
            || lower is "none" or "initial" or "inherit" or "unset" or "revert" or "revert-layer")
        {
            return false;
        }

        foreach (char character in name)
        {
            if (char.IsWhiteSpace(character) || character is '(' or ')' or ',' or '"' or '\'')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Rust <c>parse_counter_directives</c>.</summary>
    internal static List<CounterDirective>? ParseCounterDirectives(string value, int defaultValue)
    {
        string trimmed = value.Trim();
        if (CssText.EqualsAscii(trimmed, "none")
            || CssText.AsciiLower(trimmed) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
        {
            return [];
        }

        List<string> tokens = SplitWsParen(trimmed);
        if (tokens.Count == 0)
        {
            return null;
        }

        List<CounterDirective> result = [];
        int index = 0;
        while (index < tokens.Count)
        {
            string name = tokens[index].Trim();
            if (!ValidCounterName(name))
            {
                return null;
            }

            index++;
            int counterValue = defaultValue;
            if (index < tokens.Count && ParseI32(tokens[index]) is { } parsed)
            {
                counterValue = parsed;
                index++;
            }

            result.Add(new CounterDirective(name, counterValue));
        }

        return result;
    }

    /// <summary>Rust <c>parse_text_indent</c>.</summary>
    internal static Dimension? ParseTextIndent(string value)
    {
        if (SplitWsParen(value).Count != 1)
        {
            return null;
        }

        Dimension dimension = DimensionValue(value);
        return dimension.IsAuto ? null : dimension;
    }

    /// <summary>Rust <c>parse_clip_path_polygon</c>.</summary>
    internal static ClipPathPolygon? ParseClipPathPolygon(string value)
    {
        string trimmed = value.Trim();
        string lower = CssText.AsciiLower(trimmed);
        if (!lower.StartsWith("polygon(", StringComparison.Ordinal))
        {
            return null;
        }

        int close = trimmed.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        string suffix = trimmed[(close + 1)..].Trim();
        if (suffix.Length != 0 && !CssText.EqualsAscii(suffix, "border-box"))
        {
            return null;
        }

        if (close < "polygon(".Length)
        {
            return null;
        }

        string inner = trimmed["polygon(".Length..close].Trim();
        List<string> components = SplitTopLevel(inner, ',');
        ClipPathFillRule fillRule = ClipPathFillRule.Nonzero;
        if (components.Count > 0)
        {
            string first = components[0].Trim();
            if (CssText.EqualsAscii(first, "evenodd"))
            {
                fillRule = ClipPathFillRule.Evenodd;
                components.RemoveAt(0);
            }
            else if (CssText.EqualsAscii(first, "nonzero"))
            {
                components.RemoveAt(0);
            }
        }

        if (components.Count == 0)
        {
            return null;
        }

        List<(Dimension X, Dimension Y)> points = new(components.Count);
        foreach (string component in components)
        {
            List<string> coordinates = SplitWsParen(component);
            if (coordinates.Count != 2)
            {
                return null;
            }

            if (coordinates[0].Contains('(') || coordinates[1].Contains('('))
            {
                return null;
            }

            Dimension x = DimensionValue(coordinates[0]);
            Dimension y = DimensionValue(coordinates[1]);
            if (!Finite(x) || !Finite(y))
            {
                return null;
            }

            points.Add((x, y));
        }

        return new ClipPathPolygon(fillRule, points);

        static bool Finite(Dimension dimension) =>
            !dimension.IsAuto && float.IsFinite(dimension.Value);
    }

    /// <summary>
    /// Rust's <c>{}</c> formatting for an <c>f32</c> that is known to be a rounded integer.
    /// </summary>
    private static string FormatRustF32(float value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
