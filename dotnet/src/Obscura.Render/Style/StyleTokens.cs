// The parts of style.rs that reach for `cssparser` directly: container names,
// font-variation-settings, and the <grid-line> grammar.
using Obscura.Render.Css;

namespace Obscura.Render;

public static partial class ComputedStyle
{
    /// <summary>
    /// A restartable view over <see cref="CssTokenizer"/>. cssparser's
    /// <c>Parser::state()</c> / <c>reset()</c> pair is what the grid-line grammar
    /// backtracks with; the port re-slices the input instead, which is equivalent
    /// because the tokenizer is stateless apart from its position.
    /// </summary>
    private struct TokenCursor(string input)
    {
        private readonly string _input = input;
        private int _position;

        public readonly int Position => _position;

        public void Reset(int position) => _position = position;

        /// <summary>cssparser <c>Parser::next</c>: skips whitespace and comments.</summary>
        public bool TryNext(out CssToken token)
        {
            CssTokenizer tokenizer = new(_input.AsSpan(_position));
            bool ok = tokenizer.TryNext(out token);
            _position += tokenizer.Position;
            return ok;
        }

        /// <summary>cssparser <c>Parser::next_including_whitespace_and_comments</c>.</summary>
        public bool TryNextRaw(out CssToken token)
        {
            CssTokenizer tokenizer = new(_input.AsSpan(_position));
            bool ok = tokenizer.TryNextIncludingWhitespaceAndComments(out token);
            _position += tokenizer.Position;
            return ok;
        }

        public readonly bool IsExhausted()
        {
            CssTokenizer tokenizer = new(_input.AsSpan(_position));
            return tokenizer.IsExhausted();
        }

        public readonly string SliceFrom(int start) => _input[start.._position];
    }

    // -------------------------------------------------------- container-*

    /// <summary>Rust <c>parse_container_type</c>.</summary>
    internal static ContainerType? ParseContainerType(string value) =>
        CssText.AsciiLower(value.Trim()) switch
        {
            "normal" => ContainerType.Normal,
            "inline-size" => ContainerType.InlineSize,
            "size" => ContainerType.Size,
            "initial" or "unset" or "revert" or "revert-layer" => ContainerType.Normal,
            _ => null,
        };

    /// <summary>Rust <c>parse_container_names</c>.</summary>
    internal static List<string>? ParseContainerNames(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (CssText.AsciiLower(trimmed) is "none" or "initial" or "unset" or "revert" or "revert-layer")
        {
            return [];
        }

        List<string> names = [];
        TokenCursor cursor = new(trimmed);
        while (!cursor.IsExhausted())
        {
            if (!cursor.TryNext(out CssToken token) || token.Kind != CssTokenKind.Ident)
            {
                return null;
            }

            if (CssText.AsciiLower(token.Value) is "none" or "not" or "and" or "or")
            {
                return null;
            }

            names.Add(token.Value);
        }

        return names.Count == 0 ? null : names;
    }

    /// <summary>Rust <c>parse_container_shorthand</c>.</summary>
    internal static (List<string> Names, ContainerType Kind)? ParseContainerShorthand(string value)
    {
        string trimmed = value.Trim();
        if (CssText.AsciiLower(trimmed) is "initial" or "unset" or "revert" or "revert-layer")
        {
            return ([], ContainerType.Normal);
        }

        List<string> parts = SplitTopLevel(trimmed, '/');
        if (parts.Count == 1)
        {
            return ParseContainerNames(parts[0]) is { } names ? (names, ContainerType.Normal) : null;
        }

        if (parts.Count == 2)
        {
            if (ParseContainerNames(parts[0]) is not { } names || ParseContainerType(parts[1]) is not { } kind)
            {
                return null;
            }

            return (names, kind);
        }

        return null;
    }

    // ----------------------------------------------- font-variation-settings

    /// <summary>Rust <c>parse_font_variation_settings</c>.</summary>
    internal static List<FontVariationSetting>? ParseFontVariationSettings(string value)
    {
        string trimmed = value.Trim();
        TokenCursor cursor = new(trimmed);
        if (cursor.IsExhausted())
        {
            return null;
        }

        // A sorted map both implements CSS's "last duplicate wins" rule and gives
        // the inline engine a stable tag order independent of author ordering.
        // The tag sorts as its four ASCII bytes, so ordinal string order matches
        // Rust's `BTreeMap<[u8; 4], f32>`.
        SortedDictionary<string, float> settings = new(StringComparer.Ordinal);
        while (true)
        {
            if (!cursor.TryNext(out CssToken tag) || tag.Kind != CssTokenKind.QuotedString)
            {
                return null;
            }

            string name = tag.Value;
            if (name.Length != 4)
            {
                return null;
            }

            foreach (char character in name)
            {
                if (character is < (char)0x20 or > (char)0x7e)
                {
                    return null;
                }
            }

            if (ParseFontVariationNumber(ref cursor) is not { } number || !float.IsFinite(number))
            {
                return null;
            }

            // Keep numerically equivalent signed zeroes canonical for stable cache keys.
            settings[name] = number == 0f ? 0f : number;
            if (cursor.IsExhausted())
            {
                break;
            }

            if (!cursor.TryNext(out CssToken comma) || comma.Kind != CssTokenKind.Comma)
            {
                return null;
            }

            if (cursor.IsExhausted())
            {
                return null;
            }
        }

        List<FontVariationSetting> result = [];
        foreach ((string tag, float number) in settings)
        {
            result.Add(new FontVariationSetting(tag, number));
        }

        return result;
    }

    private static float? ParseFontVariationNumber(ref TokenCursor cursor)
    {
        int state = cursor.Position;
        if (cursor.TryNext(out CssToken numeric) && numeric.Kind == CssTokenKind.Number)
        {
            float parsed = (float)numeric.Number;
            return float.IsFinite(parsed) ? parsed : null;
        }

        cursor.Reset(state);
        int start = cursor.Position;
        if (!cursor.TryNext(out CssToken token) || token.Kind != CssTokenKind.Function)
        {
            return null;
        }

        if (CssText.AsciiLower(token.Value) is not ("calc" or "min" or "max" or "clamp" or "round"))
        {
            return null;
        }

        if (!UnitlessMathTokens(ref cursor, 0))
        {
            return null;
        }

        string expression = cursor.SliceFrom(start);
        float? resolved = ResolveContextualLength(expression, 0f, 0f, 0f, 0f, 0f);
        return resolved is { } value && float.IsFinite(value) ? value : null;
    }

    /// <summary>
    /// Rust <c>unitless_math_tokens</c>. Consumes the current function/parenthesis
    /// block, returning false when it contains anything but numbers and math
    /// operators.
    /// </summary>
    private static bool UnitlessMathTokens(ref TokenCursor cursor, int depth)
    {
        if (depth >= 64)
        {
            return false;
        }

        while (cursor.TryNextRaw(out CssToken token))
        {
            switch (token.Kind)
            {
                case CssTokenKind.CloseParenthesis:
                    return true;
                case CssTokenKind.Number:
                case CssTokenKind.Whitespace:
                case CssTokenKind.Comment:
                case CssTokenKind.Comma:
                    break;
                case CssTokenKind.Delim when token.Delim is '+' or '-' or '*' or '/':
                    break;
                case CssTokenKind.Function
                    when CssText.AsciiLower(token.Value) is "calc" or "min" or "max" or "clamp" or "round":
                    if (!UnitlessMathTokens(ref cursor, depth + 1))
                    {
                        return false;
                    }

                    break;
                case CssTokenKind.ParenthesisBlock:
                    if (!UnitlessMathTokens(ref cursor, depth + 1))
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        // cssparser closes an unterminated block at end of input.
        return true;
    }

    // ------------------------------------------------------------ grid-line

    /// <summary>Rust <c>GridLineKind</c>.</summary>
    internal enum GridLineKind
    {
        IdentOnly,
        Other,
    }

    /// <summary>Rust <c>parse_grid_line_kind</c>.</summary>
    internal static GridLineKind? ParseGridLineKind(string value)
    {
        TokenCursor cursor = new(value.Trim());

        // auto
        int state = cursor.Position;
        if (cursor.TryNext(out CssToken token)
            && token.Kind == CssTokenKind.Ident
            && CssText.EqualsAscii(token.Value, "auto")
            && cursor.IsExhausted())
        {
            return GridLineKind.Other;
        }

        cursor.Reset(state);

        // span && [ <positive-integer> || <custom-ident> ]
        state = cursor.Position;
        if (cursor.TryNext(out CssToken spanToken)
            && spanToken.Kind == CssTokenKind.Ident
            && CssText.EqualsAscii(spanToken.Value, "span"))
        {
            bool sawInteger = ConsumeGridInteger(ref cursor, positiveOnly: true);
            bool sawIdent = ConsumeGridCustomIdent(ref cursor);
            if (!sawInteger)
            {
                sawInteger = ConsumeGridInteger(ref cursor, positiveOnly: true);
            }

            if (!sawIdent)
            {
                sawIdent = ConsumeGridCustomIdent(ref cursor);
            }

            if ((sawInteger || sawIdent) && cursor.IsExhausted())
            {
                return GridLineKind.Other;
            }
        }

        cursor.Reset(state);

        // [ <non-zero-integer> && <custom-ident>? ], in either order.
        state = cursor.Position;
        if (ConsumeGridInteger(ref cursor, positiveOnly: false))
        {
            ConsumeGridCustomIdent(ref cursor);
            if (cursor.IsExhausted())
            {
                return GridLineKind.Other;
            }
        }

        cursor.Reset(state);
        state = cursor.Position;
        if (ConsumeGridCustomIdent(ref cursor))
        {
            if (cursor.IsExhausted())
            {
                return GridLineKind.IdentOnly;
            }

            if (ConsumeGridInteger(ref cursor, positiveOnly: false) && cursor.IsExhausted())
            {
                return GridLineKind.Other;
            }
        }

        cursor.Reset(state);

        // An integer-valued math function is the numeric half of a grid line.
        if (ConsumeGridIntegerMath(ref cursor))
        {
            ConsumeGridCustomIdent(ref cursor);
            if (cursor.IsExhausted())
            {
                return GridLineKind.Other;
            }
        }

        return null;
    }

    private static bool ConsumeGridInteger(ref TokenCursor cursor, bool positiveOnly)
    {
        int state = cursor.Position;
        bool valid = cursor.TryNext(out CssToken token)
            && token.Kind == CssTokenKind.Number
            && token.IsInteger
            && (positiveOnly ? token.Number > 0 : token.Number != 0);
        if (!valid)
        {
            cursor.Reset(state);
        }

        return valid;
    }

    private static bool ConsumeGridCustomIdent(ref TokenCursor cursor)
    {
        int state = cursor.Position;
        bool valid = cursor.TryNext(out CssToken token)
            && token.Kind == CssTokenKind.Ident
            && CssText.AsciiLower(token.Value)
                is not ("auto" or "span" or "initial" or "inherit" or "unset" or "revert" or "revert-layer");
        if (!valid)
        {
            cursor.Reset(state);
        }

        return valid;
    }

    private static bool ConsumeGridIntegerMath(ref TokenCursor cursor)
    {
        int state = cursor.Position;
        bool isMath = cursor.TryNext(out CssToken token)
            && token.Kind == CssTokenKind.Function
            && CssText.AsciiLower(token.Value) is "calc" or "min" or "max" or "clamp";
        if (!isMath)
        {
            cursor.Reset(state);
            return false;
        }

        // `parse_nested_block` requires the block to hold at least one token.
        bool sawToken = false;
        int depth = 1;
        while (cursor.TryNextRaw(out CssToken inner))
        {
            if (inner.Kind == CssTokenKind.CloseParenthesis)
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }

                sawToken = true;
                continue;
            }

            if (inner.Kind is CssTokenKind.Function or CssTokenKind.ParenthesisBlock)
            {
                depth++;
            }

            sawToken = true;
        }

        if (!sawToken)
        {
            cursor.Reset(state);
            return false;
        }

        return true;
    }
}
