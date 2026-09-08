// `supports_declaration` and its helpers: the single oracle behind CSS.supports()
// and stylesheet `@supports`.
using Obscura.Render.Css;

namespace Obscura.Render;

public static partial class ComputedStyle
{
    /// <summary>
    /// Rust <c>supports_declaration</c>: whether the renderer accepts and
    /// faithfully implements a CSS declaration.
    /// </summary>
    /// <remarks>
    /// Modern framework sheets use negative feature probes to isolate legacy
    /// fallbacks, so treating every non-empty declaration as valid corrupts the
    /// modern cascade. This is also exposed to the JavaScript runtime so
    /// <c>CSS.supports()</c> and <c>@supports</c> cannot drift.
    /// </remarks>
    public static bool SupportsDeclaration(string rawName, string rawValue)
    {
        string name = CssText.AsciiLower(rawName.Trim());
        string value = rawValue.Trim();
        if (value.Length == 0 || HasInvalidSupportsValueSyntax(value))
        {
            return false;
        }

        VariableSubstitutionSyntax variableSyntax = SupportsVariableSubstitutionSyntax(value);
        if (variableSyntax == VariableSubstitutionSyntax.Invalid)
        {
            return false;
        }

        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            return ValidCustomPropertyName(name);
        }

        string lower = CssText.AsciiLower(value);
        bool cssWide = lower is "initial" or "inherit" or "unset" or "revert" or "revert-layer";
        if (!KnownProperties.Contains(name))
        {
            return false;
        }

        // A syntactically valid var() makes the declaration valid at parse time.
        // Keep the deliberately unadvertised effect stubs false.
        if (variableSyntax == VariableSubstitutionSyntax.Valid
            && name is not ("filter" or "backdrop-filter" or "-webkit-backdrop-filter" or "perspective"
                or "contain" or "content-visibility"))
        {
            return true;
        }

        if (cssWide)
        {
            return true;
        }

        switch (name)
        {
            case "display":
                return lower is "none" or "flex" or "inline-flex" or "inline" or "inline-block" or "grid"
                    or "inline-grid" or "block" or "flow-root" or "table" or "inline-table"
                    or "-webkit-box" or "-webkit-inline-box" or "contents";
            case "direction":
                return lower is "ltr" or "rtl";
            case "position":
                return lower is "static" or "relative" or "absolute" or "fixed" or "sticky";
            case "box-sizing":
                return lower is "content-box" or "border-box";
            case "table-layout":
                return lower is "auto" or "fixed";
            case "container-type":
                return ParseContainerType(value) is not null;
            case "container-name":
                return ParseContainerNames(value) is not null;
            case "container":
                return ParseContainerShorthand(value) is not null;
            case "font-optical-sizing":
                return lower is "auto" or "none";
            case "font-variation-settings":
                return CssText.EqualsAscii(value, "normal") || ParseFontVariationSettings(value) is not null;
            case "white-space":
                return lower is "normal" or "nowrap" or "pre" or "pre-wrap" or "pre-line" or "break-spaces";
            case "text-overflow":
                return lower is "clip" or "ellipsis";
            case "-webkit-line-clamp":
                return WebkitLineClampValue(value).Valid;
            case "-webkit-box-orient":
                return lower is "horizontal" or "vertical" or "inline-axis" or "block-axis";
            case "overflow-wrap":
            case "word-wrap":
                return lower is "normal" or "break-word" or "anywhere";
            case "word-break":
                return lower is "normal" or "break-all" or "keep-all" or "break-word";
            case "text-wrap":
                return lower is "auto" or "wrap" or "balance" or "wrap balance" or "balance wrap";
            case "text-wrap-style":
                return lower is "auto" or "balance";
            case "text-indent":
                return ParseTextIndent(value) is not null;
            case "animation-duration":
                return ParseAnimationTimeMs(value) is { } duration && duration >= 0f;
            case "animation-delay":
                return ParseAnimationTimeMs(value) is not null;
            case "animation-iteration-count":
                return ParseAnimationIterationCount(value) is not null;
            case "animation-direction":
                return ParseAnimationDirection(value) is not null;
            case "clip-path":
            case "-webkit-clip-path":
                return CssText.EqualsAscii(value, "none") || ParseClipPathPolygon(value) is not null;
            // These properties currently participate only in containing-block
            // bookkeeping. Advertising an unpainted effect is worse than a
            // conservative false result.
            case "filter":
            case "backdrop-filter":
            case "-webkit-backdrop-filter":
            case "perspective":
                return CssText.EqualsAscii(value, "none");
            case "contain":
                return CssText.EqualsAscii(value, "none");
            case "content-visibility":
                return CssText.EqualsAscii(value, "visible");
            case "content":
                return SupportsContentValue(value);
            case "animation-fill-mode":
                return ParseAnimationFillMode(value) is not null;
            case "animation-play-state":
                return ParseAnimationPlayState(value) is not null;
            case "float":
                return lower is "none" or "left" or "right";
            case "object-fit":
                return lower is "fill" or "contain" or "cover" or "none" or "scale-down";
            case "object-position":
            {
                List<string> tokens = SplitWsParen(value);
                if (tokens.Count == 0 || tokens.Count > 4)
                {
                    return false;
                }

                foreach (string token in tokens)
                {
                    bool ok = token is "left" or "right" or "top" or "bottom" or "center";
                    if (!ok && token.EndsWith('%'))
                    {
                        ok = ParseF32(token[..^1]) is { } percentage && float.IsFinite(percentage);
                    }

                    if (!ok)
                    {
                        ok = PxValue(token) is { } length && float.IsFinite(length);
                    }

                    if (!ok)
                    {
                        return false;
                    }
                }

                return true;
            }

            case "visibility":
                return lower is "visible" or "hidden" or "collapse";
            case "scrollbar-gutter":
                return lower is "auto" or "stable" or "stable both-edges";
            case "overflow":
            case "overflow-x":
            case "overflow-y":
                return ParseOverflowDeclaration(name, value).Valid;
            case "grid-area":
            {
                List<string> parts = [];
                foreach (string part in SplitTopLevel(value, '/'))
                {
                    parts.Add(part.Trim());
                }

                if (parts.Count == 0 || parts.Count > 4)
                {
                    return false;
                }

                foreach (string part in parts)
                {
                    if (part.Length == 0 || ParseGridLineKind(part) is null)
                    {
                        return false;
                    }
                }

                return true;
            }

            case "grid-auto-columns":
            case "grid-auto-rows":
                return ParseGridAutoTrackList(value) is not null;
            case "border":
            case "border-top":
            case "border-right":
            case "border-bottom":
            case "border-left":
                return ParseBorderShorthand(value, false) is not null;
            case "border-inline":
            case "border-block":
            case "border-inline-start":
            case "border-inline-end":
            case "border-block-start":
            case "border-block-end":
            case "border-inline-width":
            case "border-inline-style":
            case "border-inline-color":
            case "border-block-width":
            case "border-block-style":
            case "border-block-color":
            case "border-inline-start-width":
            case "border-inline-start-style":
            case "border-inline-start-color":
            case "border-inline-end-width":
            case "border-inline-end-style":
            case "border-inline-end-color":
            case "border-block-start-width":
            case "border-block-start-style":
            case "border-block-start-color":
            case "border-block-end-width":
            case "border-block-end-style":
            case "border-block-end-color":
                return SupportsLogicalBorderDeclaration(name, value);
            case "border-width":
            {
                List<string> tokens = SplitWsParen(value);
                float[] widths = new float[tokens.Count];
                for (int index = 0; index < tokens.Count; index++)
                {
                    if (BorderWidth(tokens[index]) is not { } width)
                    {
                        return false;
                    }

                    widths[index] = width;
                }

                return BorderSides.ExpandSides<float>(widths) is not null;
            }

            case "border-top-width":
            case "border-right-width":
            case "border-bottom-width":
            case "border-left-width":
            case "outline-width":
                return BorderWidth(value) is not null;
            case "border-style":
            {
                List<string> tokens = SplitWsParen(value);
                BorderStyle[] styles = new BorderStyle[tokens.Count];
                for (int index = 0; index < tokens.Count; index++)
                {
                    if (BorderStyleKeyword(tokens[index]) is not { } lineStyle)
                    {
                        return false;
                    }

                    styles[index] = lineStyle;
                }

                return BorderSides.ExpandSides<BorderStyle>(styles) is not null;
            }

            case "border-top-style":
            case "border-right-style":
            case "border-bottom-style":
            case "border-left-style":
                return BorderStyleKeyword(value) is not null;
            case "border-color":
                return ParseBorderColors(value, false) is not null;
            case "border-top-color":
            case "border-right-color":
            case "border-bottom-color":
            case "border-left-color":
            case "outline-color":
                return BorderColor(value, false) is not null;
            case "border-radius":
                return ParsedBorderRadii(value) is not null;
            case "border-top-left-radius":
            case "border-top-right-radius":
            case "border-bottom-right-radius":
            case "border-bottom-left-radius":
            {
                List<string> tokens = SplitWsParen(value);
                return (tokens.Count == 1 && RadiusValueOf(tokens[0]) is not null)
                    || (tokens.Count == 2 && RadiusValueOf(tokens[0]) is not null
                        && RadiusValueOf(tokens[1]) is not null);
            }

            case "outline":
                return ParseOutlineShorthand(value, false) is not null;
            case "outline-style":
                return OutlineStyleKeyword(value) is not null;
            case "outline-offset":
                return StrictBorderLength(value) is not null;
            case "color":
            case "-webkit-text-fill-color":
            case "background-color":
                return CssColor.Parse(value) is not null;
            case "color-scheme":
            {
                List<string> tokens = SplitWhitespace(value);
                if (tokens.Count == 0)
                {
                    return false;
                }

                bool sawScheme = false;
                foreach (string token in tokens)
                {
                    string keyword = CssText.AsciiLower(token);
                    if (keyword is not ("normal" or "light" or "dark" or "only"))
                    {
                        return false;
                    }

                    if (keyword is "normal" or "light" or "dark")
                    {
                        sawScheme = true;
                    }
                }

                return sawScheme;
            }

            default:
                return SupportsConservativeKnownValue(name, value);
        }
    }

    /// <summary>Rust <c>has_invalid_supports_value_syntax</c>.</summary>
    internal static bool HasInvalidSupportsValueSyntax(string value)
    {
        int depth = 0;
        char? quote = null;
        int index = 0;
        while (index < value.Length)
        {
            char character = value[index];
            if (quote is { } active)
            {
                if (character == '\\')
                {
                    index += 2;
                    continue;
                }

                if (character == active)
                {
                    quote = null;
                }

                index++;
                continue;
            }

            switch (character)
            {
                case '\\':
                    index++;
                    break;
                case '\'':
                case '"':
                    quote = character;
                    break;
                case '(':
                case '[':
                    depth++;
                    break;
                case ')':
                case ']':
                    depth--;
                    if (depth < 0)
                    {
                        return true;
                    }

                    break;
                case ';':
                case '{':
                case '}':
                    if (depth == 0)
                    {
                        return true;
                    }

                    break;
                case '!':
                    if (depth == 0)
                    {
                        string rest = value[(index + 1)..].TrimStart();
                        if (rest.Length >= 9 && CssText.EqualsAscii(rest[..9], "important"))
                        {
                            return true;
                        }
                    }

                    break;
            }

            index++;
        }

        return depth != 0 || quote is not null;
    }

    /// <summary>Rust <c>VariableSubstitutionSyntax</c>.</summary>
    internal enum VariableSubstitutionSyntax
    {
        None,
        Valid,
        Invalid,
    }

    /// <summary>Rust <c>supports_variable_substitution_syntax</c>.</summary>
    internal static VariableSubstitutionSyntax SupportsVariableSubstitutionSyntax(string value)
    {
        bool found = false;
        bool ok = Scan(value, ref found);
        if (!ok)
        {
            return VariableSubstitutionSyntax.Invalid;
        }

        return found ? VariableSubstitutionSyntax.Valid : VariableSubstitutionSyntax.None;

        static bool Scan(string value, ref bool found)
        {
            int index = 0;
            while (index < value.Length)
            {
                char character = value[index];
                if (character is '\'' or '"')
                {
                    char quote = character;
                    index++;
                    while (index < value.Length)
                    {
                        if (value[index] == '\\')
                        {
                            index += 2;
                        }
                        else if (value[index] == quote)
                        {
                            index++;
                            break;
                        }
                        else
                        {
                            index++;
                        }
                    }

                    continue;
                }

                if (character == '\\')
                {
                    index += 2;
                    continue;
                }

                if (character != '(')
                {
                    index++;
                    continue;
                }

                int open = index;
                int identStart = open;
                while (identStart > 0 && IsIdentByte(value[identStart - 1]))
                {
                    identStart--;
                }

                if (MatchingParenthesis(value, open) is not { } close)
                {
                    return false;
                }

                string arguments = value[(open + 1)..close];
                if (CssText.EqualsAscii(value[identStart..open], "var"))
                {
                    if (SplitVariableArguments(arguments) is not { } split)
                    {
                        return false;
                    }

                    if (!ValidCustomPropertyName(split.Name.Trim()))
                    {
                        return false;
                    }

                    if (split.Fallback is { } fallback)
                    {
                        if (HasTopLevelVariableForbiddenToken(fallback) != false || !Scan(fallback, ref found))
                        {
                            return false;
                        }
                    }

                    found = true;
                }
                else if (!Scan(arguments, ref found))
                {
                    return false;
                }

                index = close + 1;
            }

            return true;
        }

        static bool IsIdentByte(char character) =>
            CssText.IsAsciiAlphanumeric(character) || character == '-' || character == '_';
    }

    /// <summary>Rust <c>matching_parenthesis</c>.</summary>
    internal static int? MatchingParenthesis(string value, int open)
    {
        int depth = 1;
        char? quote = null;
        int index = open + 1;
        while (index < value.Length)
        {
            char character = value[index];
            if (quote is { } active)
            {
                if (character == '\\')
                {
                    index += 2;
                    continue;
                }

                if (character == active)
                {
                    quote = null;
                }
            }
            else
            {
                switch (character)
                {
                    case '\'':
                    case '"':
                        quote = character;
                        break;
                    case '\\':
                        index += 2;
                        continue;
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

            index++;
        }

        return null;
    }

    /// <summary>Rust <c>split_variable_arguments</c>.</summary>
    internal static (string Name, string? Fallback)? SplitVariableArguments(string arguments)
    {
        Stack<char> stack = new();
        char? quote = null;
        int index = 0;
        while (index < arguments.Length)
        {
            char character = arguments[index];
            if (quote is { } active)
            {
                if (character == '\\')
                {
                    index += 2;
                    continue;
                }

                if (character == active)
                {
                    quote = null;
                }
            }
            else
            {
                switch (character)
                {
                    case '\'':
                    case '"':
                        quote = character;
                        break;
                    case '\\':
                        index += 2;
                        continue;
                    case '(':
                    case '[':
                    case '{':
                        stack.Push(character);
                        break;
                    case ')':
                    case ']':
                    case '}':
                    {
                        char expected = character switch
                        {
                            ')' => '(',
                            ']' => '[',
                            _ => '{',
                        };
                        if (stack.Count == 0 || stack.Pop() != expected)
                        {
                            return null;
                        }

                        break;
                    }

                    case ',' when stack.Count == 0:
                        return (arguments[..index], arguments[(index + 1)..]);
                }
            }

            index++;
        }

        return stack.Count == 0 ? (arguments, (string?)null) : null;
    }

    /// <summary>Rust <c>has_top_level_variable_forbidden_token</c>.</summary>
    internal static bool? HasTopLevelVariableForbiddenToken(string value)
    {
        Stack<char> stack = new();
        char? quote = null;
        int index = 0;
        while (index < value.Length)
        {
            char character = value[index];
            if (quote is { } active)
            {
                if (character == '\\')
                {
                    index += 2;
                    continue;
                }

                if (character == active)
                {
                    quote = null;
                }
            }
            else
            {
                switch (character)
                {
                    case '\'':
                    case '"':
                        quote = character;
                        break;
                    case '\\':
                        index += 2;
                        continue;
                    case '(':
                    case '[':
                    case '{':
                        stack.Push(character);
                        break;
                    case ')':
                    case ']':
                    case '}':
                    {
                        char expected = character switch
                        {
                            ')' => '(',
                            ']' => '[',
                            _ => '{',
                        };
                        if (stack.Count == 0 || stack.Pop() != expected)
                        {
                            return null;
                        }

                        break;
                    }

                    case '!':
                    case ';':
                        if (stack.Count == 0)
                        {
                            return true;
                        }

                        break;
                }
            }

            index++;
        }

        return stack.Count == 0 ? false : null;
    }

    /// <summary>Rust <c>valid_custom_property_name</c>.</summary>
    internal static bool ValidCustomPropertyName(string name)
    {
        if (!name.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string rest = name[2..];
        if (rest.Length == 0)
        {
            return false;
        }

        bool escaped = false;
        foreach (char character in rest)
        {
            if (escaped)
            {
                if (character is '\n' or '\r' or '\f')
                {
                    return false;
                }

                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (!(char.IsLetterOrDigit(character) || character == '-' || character == '_'
                || !CssText.IsAscii(character)))
            {
                return false;
            }
        }

        return !escaped;
    }

    /// <summary>Rust <c>supports_content_value</c>.</summary>
    internal static bool SupportsContentValue(string value)
    {
        string lower = CssText.AsciiLower(value.Trim());
        if (lower is "none" or "normal" || SupportsSingleUrl(value))
        {
            return true;
        }

        string rest = value.Trim();
        bool found = false;
        while (rest.Length != 0)
        {
            rest = rest.TrimStart();
            if (rest.Length == 0)
            {
                break;
            }

            char first = rest[0];
            if (first is '\'' or '"')
            {
                bool escaped = false;
                int? end = null;
                for (int offset = 1; offset < rest.Length; offset++)
                {
                    char character = rest[offset];
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == first)
                    {
                        end = offset + 1;
                        break;
                    }
                }

                if (end is not { } quoteEnd)
                {
                    return false;
                }

                rest = rest[quoteEnd..];
                found = true;
                continue;
            }

            int nameEnd = rest.Length;
            for (int index = 0; index < rest.Length; index++)
            {
                char character = rest[index];
                if (!char.IsLetterOrDigit(character) && character is not ('-' or '_' or '\\'))
                {
                    nameEnd = index;
                    break;
                }
            }

            string name = CssText.AsciiLower(rest[..nameEnd]);
            string tail = rest[nameEnd..].TrimStart();
            if (name is "open-quote" or "close-quote" or "no-open-quote" or "no-close-quote")
            {
                rest = tail;
                found = true;
                continue;
            }

            if (name is not ("attr" or "counter" or "counters") || !tail.StartsWith('('))
            {
                return false;
            }

            int depth = 0;
            char? quote = null;
            bool inEscape = false;
            int? functionEnd = null;
            for (int offset = 0; offset < tail.Length; offset++)
            {
                char character = tail[offset];
                if (inEscape)
                {
                    inEscape = false;
                    continue;
                }

                if (character == '\\')
                {
                    inEscape = true;
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
                            functionEnd = offset + 1;
                        }

                        break;
                }

                if (functionEnd is not null)
                {
                    break;
                }
            }

            if (functionEnd is not { } close)
            {
                return false;
            }

            string arguments = tail[1..(close - 1)].Trim();
            if (!SupportsContentFunction(name, arguments))
            {
                return false;
            }

            rest = tail[close..];
            found = true;
        }

        return found;
    }

    /// <summary>Rust <c>supports_single_url</c>.</summary>
    internal static bool SupportsSingleUrl(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < 4 || !CssText.EqualsAscii(trimmed[..4], "url(") || !trimmed.EndsWith(')'))
        {
            return false;
        }

        int depth = 0;
        char? quote = null;
        bool escaped = false;
        for (int index = 0; index < trimmed.Length; index++)
        {
            char character = trimmed[index];
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
                        return index + 1 == trimmed.Length && ParseUrl(trimmed) is not null;
                    }

                    break;
            }
        }

        return false;
    }

    /// <summary>Rust <c>supports_content_function</c>.</summary>
    internal static bool SupportsContentFunction(string name, string rawArguments)
    {
        List<string> arguments = SplitTopLevel(rawArguments, ',');
        switch (name)
        {
            case "attr":
            {
                if (arguments.Count != 1)
                {
                    return false;
                }

                List<string> words = SplitWhitespace(arguments[0]);
                return words.Count > 0 && IsIdent(words[0]);
            }

            case "counter":
                return arguments.Count is >= 1 and <= 2
                    && IsIdent(arguments[0])
                    && (arguments.Count < 2 || IsCounterStyle(arguments[1]));

            case "counters":
            {
                if (arguments.Count is < 2 or > 3 || !IsIdent(arguments[0]))
                {
                    return false;
                }

                string separator = arguments[1].Trim();
                if (separator.Length < 2 || (separator[0] != '\'' && separator[0] != '"')
                    || separator[^1] != separator[0])
                {
                    return false;
                }

                return arguments.Count < 3 || IsCounterStyle(arguments[2]);
            }

            default:
                return false;
        }

        static bool IsIdent(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            foreach (char character in trimmed)
            {
                if (!char.IsLetterOrDigit(character) && character is not ('-' or '_' or '\\'))
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsCounterStyle(string value) => CssText.AsciiLower(value.Trim())
            is "decimal" or "decimal-leading-zero" or "lower-alpha" or "lower-latin"
                or "upper-alpha" or "upper-latin" or "lower-roman" or "upper-roman";
    }

    /// <summary>Rust <c>supports_flex_value</c>.</summary>
    internal static bool SupportsFlexValue(string value)
    {
        string lower = CssText.AsciiLower(value.Trim());
        if (lower is "none" or "auto" or "initial")
        {
            return true;
        }

        List<string> tokens = SplitWsParen(value);
        if (tokens.Count == 0 || tokens.Count > 3)
        {
            return false;
        }

        int numbers = 0;
        foreach (string token in tokens)
        {
            if (ParseF32(token) is { } number && float.IsFinite(number) && number >= 0f)
            {
                numbers++;
            }
            else if (DimensionValue(token).IsAuto && !CssText.EqualsAscii(token, "auto"))
            {
                return false;
            }
        }

        return numbers <= 2;
    }

    /// <summary>Rust <c>supports_transform_value</c>.</summary>
    internal static bool SupportsTransformValue(string value) =>
        CssText.EqualsAscii(value.Trim(), "none") || ParseTransformOps(value) is not null;

    /// <summary>Rust <c>supports_conservative_known_value</c>.</summary>
    internal static bool SupportsConservativeKnownValue(string name, string value)
    {
        string lower = CssText.AsciiLower(value.Trim());
        switch (name)
        {
            case "width":
            case "inline-size":
                return lower == "fit-content" || DimensionSupported(value, true);
            case "height":
            case "block-size":
            case "min-width":
            case "min-inline-size":
            case "min-height":
            case "min-block-size":
            case "max-width":
            case "max-inline-size":
            case "max-height":
            case "max-block-size":
            case "flex-basis":
                return DimensionSupported(value, true);
            case "margin":
            case "margin-inline":
            case "margin-block":
                return DimensionsSupported(value, true, 4);
            case "margin-top":
            case "margin-right":
            case "margin-bottom":
            case "margin-left":
            case "margin-inline-start":
            case "margin-inline-end":
            case "margin-block-start":
            case "margin-block-end":
                return DimensionSupported(value, true);
            case "padding":
            case "padding-inline":
            case "padding-block":
            case "inset":
                return DimensionsSupported(value, false, 4);
            case "padding-top":
            case "padding-right":
            case "padding-bottom":
            case "padding-left":
            case "padding-inline-start":
            case "padding-inline-end":
            case "padding-block-start":
            case "padding-block-end":
                return DimensionSupported(value, false);
            case "top":
            case "right":
            case "bottom":
            case "left":
            case "inset-inline":
            case "inset-block":
                return DimensionsSupported(value, true, 2);
            case "inset-inline-start":
            case "inset-inline-end":
            case "inset-block-start":
            case "inset-block-end":
                return DimensionSupported(value, true);
            case "aspect-ratio":
                return lower == "auto" || ParseAspectRatio(value) is not null;
            case "align-items":
            case "justify-items":
            case "align-self":
            case "justify-self":
                return SelfAlignmentValue(value).Valid;
            case "place-items":
            case "place-self":
                return SelfAlignmentPair(value).Valid;
            case "align-content":
                return ContentAlignmentValue(value) is not null;
            case "justify-content":
                return lower is "left" or "right" || ContentAlignmentValue(value) is not null;
            case "place-content":
                return ContentAlignmentPair(value) is not null;
            case "flex-flow":
                return ParseFlexFlowShorthand(value) is not null;
            case "flex-direction":
                return lower is "row" or "row-reverse" or "column" or "column-reverse";
            case "flex-wrap":
                return lower is "nowrap" or "wrap" or "wrap-reverse";
            case "flex-grow":
            case "flex-shrink":
                return ParseF32(value.Trim()) is { } number && float.IsFinite(number) && number >= 0f;
            case "flex":
                return SupportsFlexValue(value);
            case "order":
                return ParseI32(value.Trim()) is not null;
            case "opacity":
                return ParseF32(value.Trim()) is { } opacity && float.IsFinite(opacity);
            case "z-index":
                return lower == "auto" || ParseI32(value.Trim()) is not null;
            case "clear":
                return lower is "none" or "left" or "right" or "both" or "inline-start" or "inline-end";
            case "vertical-align":
                return lower is "top" or "baseline" or "text-top" or "middle" or "bottom" or "text-bottom";
            case "border-collapse":
                return lower is "collapse" or "separate";
            case "border-spacing":
                return DimensionsSupported(value, false, 2);
            case "column-count":
            case "-webkit-column-count":
                return lower == "auto" || ParseColumnCount(value) is not null;
            case "columns":
            case "-webkit-columns":
            {
                foreach (string token in SplitWsParen(value))
                {
                    if (ParseColumnCount(token) is not null)
                    {
                        return true;
                    }
                }

                return false;
            }

            case "break-inside":
                return lower is "auto" or "avoid" or "avoid-column";
            case "-webkit-column-break-inside":
                return lower is "auto" or "avoid";
            case "grid-auto-flow":
            {
                List<string> tokens = SplitWhitespace(lower);
                if (tokens.Count == 0 || tokens.Count > 2)
                {
                    return false;
                }

                foreach (string token in tokens)
                {
                    if (token is not ("row" or "column" or "dense"))
                    {
                        return false;
                    }
                }

                return true;
            }

            case "grid-template-columns":
            case "grid-template-rows":
            {
                (List<Layout.GridTemplateComponent> tracks, _, _) = ParseTrackListNamed(value);
                return tracks.Count != 0 || lower.StartsWith("subgrid", StringComparison.Ordinal);
            }

            case "grid-template-areas":
                return ParseGridAreas(value).Count != 0 || lower == "none";
            case "grid-column":
            case "grid-row":
            {
                foreach (string part in SplitTopLevel(value, '/'))
                {
                    if (ParseGridLineKind(part.Trim()) is null)
                    {
                        return false;
                    }
                }

                return true;
            }

            case "grid-column-start":
            case "grid-column-end":
            case "grid-row-start":
            case "grid-row-end":
                return ParseGridLineKind(value) is not null;
            case "transform-origin":
                return ParseTransformOrigin(value) is not null;
            case "translate":
                return lower == "none" || DimensionsSupported(value, false, 3);
            case "rotate":
                return lower == "none" || AngleDegrees(value) is not null;
            case "scale":
            {
                if (lower == "none")
                {
                    return true;
                }

                List<string> values = SplitWhitespace(value);
                if (values.Count == 0 || values.Count > 2)
                {
                    return false;
                }

                foreach (string token in values)
                {
                    if (ScaleNumber(token) is null)
                    {
                        return false;
                    }
                }

                return true;
            }

            case "transform":
                return SupportsTransformValue(value);
            case "background-color":
            case "color":
            case "-webkit-text-fill-color":
                return CssColor.Parse(value) is not null;
            case "background-image":
            case "mask-image":
            case "-webkit-mask-image":
                return lower == "none"
                    || ParseUrl(value) is not null
                    || ParseBackgroundGradientLayers(value, false).Layers.Count != 0;
            case "background-repeat":
            case "mask-repeat":
            case "-webkit-mask-repeat":
                return ParseImageRepeat(value) is not null;
            case "background-size":
            case "mask-size":
            case "-webkit-mask-size":
                return lower is "auto" or "cover" or "contain" || ParseBackgroundSize(value) is not null;
            case "background-origin":
                return ParseBackgroundOrigin(value) is not null;
            case "background-clip":
            case "-webkit-background-clip":
                return ParseBackgroundClip(value) is not null;
            case "font-size":
                return IsFontSizeToken(value);
            case "font-weight":
                return SpecifiedFontWeight(value) is not null;
            case "font-family":
                return value.Trim().Length != 0;
            case "font-style":
                return lower is "normal" or "italic" || lower.StartsWith("oblique", StringComparison.Ordinal);
            case "text-align":
                return lower is "left" or "right" or "start" or "end" or "center" or "justify";
            case "text-transform":
                return lower is "none" or "uppercase" or "lowercase" or "capitalize";
            case "text-decoration":
            case "text-decoration-line":
            {
                foreach (string token in SplitWhitespace(lower))
                {
                    if (token is not ("none" or "underline"))
                    {
                        return false;
                    }
                }

                return true;
            }

            case "line-height":
                return lower == "normal"
                    || (ParseF32(value.Trim()) is { } ratio && float.IsFinite(ratio))
                    || DimensionSupported(value, false);
            case "gap":
            case "grid-gap":
                return DimensionsSupported(value, false, 2);
            case "row-gap":
            case "grid-row-gap":
            case "column-gap":
            case "grid-column-gap":
            case "-webkit-column-gap":
                return lower == "normal" || DimensionSupported(value, false);
            case "counter-reset":
            case "counter-increment":
            case "counter-set":
                return ParseCounterDirectives(value, 0) is not null;
            case "list-style-type":
                return ListStyleKeyword(value.Trim()) is not null;
            case "list-style":
            {
                foreach (string token in SplitWhitespace(value))
                {
                    if (ListStyleKeyword(token) is not null)
                    {
                        return true;
                    }
                }

                return false;
            }

            case "animation":
            case "animation-name":
                return value.Trim().Length != 0;
            case "background":
            case "font":
            case "grid-template":
            case "grid":
            case "box-shadow":
            case "-webkit-box-shadow":
            case "fill":
            case "stroke":
            case "stroke-width":
            case "letter-spacing":
            case "will-change":
                return false;
            default:
                return false;
        }
    }

    private static bool DimensionSupported(string input, bool auto)
    {
        string trimmed = input.Trim();
        if (auto && CssText.EqualsAscii(trimmed, "auto"))
        {
            return true;
        }

        if (!DimensionValue(trimmed).IsAuto
            && ResolveContextualLength(trimmed, 16f, 16f, 1f, 1f, 100f) is not null)
        {
            return true;
        }

        return trimmed.Contains('(')
            && ResolveContextualLength(trimmed, 16f, 16f, 1f, 1f, 100f) is not null;
    }

    private static bool DimensionsSupported(string input, bool auto, int max)
    {
        List<string> tokens = SplitWsParen(input);
        if (tokens.Count == 0 || tokens.Count > max)
        {
            return false;
        }

        foreach (string token in tokens)
        {
            if (!DimensionSupported(token, auto))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "display", "direction", "width", "inline-size", "height", "block-size", "min-width",
        "min-inline-size", "min-height", "min-block-size", "max-width", "max-inline-size",
        "max-height", "max-block-size", "box-sizing", "container", "container-type",
        "container-name", "aspect-ratio", "margin", "margin-top", "margin-right", "margin-bottom",
        "margin-left", "margin-inline", "margin-inline-start", "margin-inline-end", "margin-block",
        "margin-block-start", "margin-block-end", "padding", "padding-top", "padding-right",
        "padding-bottom", "padding-left", "padding-inline", "padding-inline-start",
        "padding-inline-end", "padding-block", "padding-block-start", "padding-block-end",
        "border-radius", "border-top-left-radius", "border-top-right-radius",
        "border-bottom-right-radius", "border-bottom-left-radius", "clip-path", "-webkit-clip-path",
        "border", "border-width", "border-top-width", "border-right-width", "border-bottom-width",
        "border-left-width", "border-style", "border-top-style", "border-right-style",
        "border-bottom-style", "border-left-style", "border-top-color", "border-right-color",
        "border-bottom-color", "border-left-color", "border-top", "border-right", "border-bottom",
        "border-left", "border-inline", "border-block", "border-inline-start", "border-inline-end",
        "border-block-start", "border-block-end", "border-inline-width", "border-inline-style",
        "border-inline-color", "border-block-width", "border-block-style", "border-block-color",
        "border-inline-start-width", "border-inline-start-style", "border-inline-start-color",
        "border-inline-end-width", "border-inline-end-style", "border-inline-end-color",
        "border-block-start-width", "border-block-start-style", "border-block-start-color",
        "border-block-end-width", "border-block-end-style", "border-block-end-color", "background",
        "background-color", "background-image", "background-size", "background-position",
        "background-repeat", "background-origin", "background-clip", "-webkit-background-clip",
        "mask-image", "-webkit-mask-image", "mask-size", "-webkit-mask-size", "mask-repeat",
        "-webkit-mask-repeat", "color", "content", "-webkit-text-fill-color", "fill", "stroke",
        "stroke-width", "border-color", "outline", "outline-width", "outline-style", "outline-color",
        "outline-offset", "color-scheme", "font-size", "letter-spacing", "font", "font-weight",
        "font-family", "font-style", "font-optical-sizing", "font-variation-settings", "text-align",
        "text-indent", "text-transform", "text-decoration", "text-decoration-line", "line-height",
        "white-space", "text-overflow", "-webkit-line-clamp", "-webkit-box-orient", "overflow-wrap",
        "word-wrap", "word-break", "text-wrap", "text-wrap-style", "align-items", "justify-items",
        "place-items", "align-self", "justify-self", "place-self", "align-content",
        "justify-content", "place-content", "flex-flow", "flex-direction", "flex-wrap", "flex-grow",
        "flex-shrink", "flex-basis", "flex", "order", "position", "float", "counter-reset",
        "counter-increment", "counter-set", "object-fit", "object-position", "top", "right",
        "bottom", "left", "inset", "inset-inline", "inset-inline-start", "inset-inline-end",
        "inset-block", "inset-block-start", "inset-block-end", "overflow", "overflow-x",
        "overflow-y", "scrollbar-gutter", "visibility", "opacity", "animation", "animation-name",
        "animation-duration", "animation-delay", "animation-fill-mode", "animation-iteration-count",
        "animation-direction", "animation-play-state", "z-index", "clear", "vertical-align",
        "list-style", "list-style-type", "gap", "grid-gap", "row-gap", "grid-row-gap", "column-gap",
        "grid-column-gap", "-webkit-column-gap", "column-count", "-webkit-column-count", "columns",
        "-webkit-columns", "break-inside", "-webkit-column-break-inside", "border-spacing",
        "border-collapse", "table-layout", "grid-template-columns", "grid-template-rows",
        "grid-auto-columns", "grid-auto-rows", "grid-template-areas", "grid-template", "grid",
        "grid-auto-flow", "grid-area", "grid-column", "grid-row", "grid-column-start",
        "grid-column-end", "grid-row-start", "grid-row-end", "transform", "transform-origin",
        "translate", "rotate", "scale", "filter", "backdrop-filter", "-webkit-backdrop-filter",
        "perspective", "contain", "will-change", "content-visibility", "box-shadow",
        "-webkit-box-shadow",
    };
}
