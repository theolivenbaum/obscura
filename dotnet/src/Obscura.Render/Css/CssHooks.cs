namespace Obscura.Render.Css;

/// <summary>
/// The two capabilities css.rs borrows from outside its own file: the CSS
/// property/value support oracle (Rust <c>crate::style::supports_declaration</c>)
/// and selector parsing (Rust <c>obscura_dom::selector::parse_selector</c>).
/// </summary>
/// <remarks>
/// SHARED SEAM: both are delegates so this port has no dependency on the
/// style.rs or dom.rs ports. The defaults are self-contained approximations;
/// the coordinator should install the real implementations once those
/// components land, at which point <c>@supports</c> behavior becomes exact.
/// </remarks>
public static class CssHost
{
    /// <summary>
    /// Whether the renderer accepts and faithfully implements a CSS
    /// declaration. Drives <c>@supports</c> and keyframe declaration filtering.
    /// </summary>
    public static Func<string, string, bool> SupportsDeclaration { get; set; } =
        CssSupportsOracle.SupportsDeclaration;

    /// <summary>Whether a selector parses, driving <c>@supports selector(...)</c>.</summary>
    public static Func<string, bool> SelectorParses { get; set; } = CssSupportsOracle.SelectorParses;

    /// <summary>Restore the built-in defaults. Used by tests.</summary>
    public static void ResetToDefaults()
    {
        SupportsDeclaration = CssSupportsOracle.SupportsDeclaration;
        SelectorParses = CssSupportsOracle.SelectorParses;
    }
}

/// <summary>
/// The built-in support oracle.
/// </summary>
/// <remarks>
/// A partial port of <c>style.rs</c>: the property-name gate and the CSS-wide /
/// variable rules are exact, and the value validators cover the length, color
/// and keyword families this port can decide on its own. Properties whose
/// validator lives in the unported part of style.rs answer <c>false</c>, which
/// is the same direction css.rs already takes for unknown syntax: a feature
/// query must never optimistically enable a fallback branch.
/// </remarks>
internal static class CssSupportsOracle
{
    private enum VariableSyntax
    {
        None,
        Valid,
        Invalid,
    }

    public static bool SelectorParses(string selector)
    {
        selector = selector.Trim();
        if (selector.Length == 0)
        {
            return false;
        }

        // A syntax-level check standing in for the selector engine: balanced
        // delimiters, no empty compound around a combinator, no trailing
        // combinator.
        var depthParen = 0;
        var depthBracket = 0;
        char? quote = null;
        var previousSignificant = '\0';
        for (var index = 0; index < selector.Length; index++)
        {
            var character = selector[index];
            if (quote is { } active)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == active)
                {
                    quote = null;
                }

                continue;
            }

            switch (character)
            {
                case '\\':
                    index++;
                    break;
                case '"':
                case '\'':
                    quote = character;
                    break;
                case '(':
                    depthParen++;
                    break;
                case ')':
                    depthParen--;
                    if (depthParen < 0)
                    {
                        return false;
                    }

                    break;
                case '[':
                    depthBracket++;
                    break;
                case ']':
                    depthBracket--;
                    if (depthBracket < 0)
                    {
                        return false;
                    }

                    break;
                case '>':
                case '+':
                case '~':
                    if (depthParen == 0 && depthBracket == 0
                        && previousSignificant is '>' or '+' or '~' or ',')
                    {
                        return false;
                    }

                    break;
            }

            if (!CssText.IsWhitespace(character))
            {
                previousSignificant = character;
            }
        }

        return quote is null
            && depthParen == 0
            && depthBracket == 0
            && previousSignificant is not ('>' or '+' or '~' or ',');
    }

    public static bool SupportsDeclaration(string rawName, string rawValue)
    {
        var name = CssText.AsciiLower(rawName.Trim());
        var value = rawValue.Trim();
        if (value.Length == 0 || HasInvalidValueSyntax(value))
        {
            return false;
        }

        var variableSyntax = VariableSubstitutionSyntax(value);
        if (variableSyntax == VariableSyntax.Invalid)
        {
            return false;
        }

        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            return ValidCustomPropertyName(name);
        }

        var lower = CssText.AsciiLower(value);
        var cssWide = lower is "initial" or "inherit" or "unset" or "revert" or "revert-layer";

        if (!KnownProperties.Contains(name))
        {
            return false;
        }

        // A syntactically valid var() makes the declaration valid at parse time;
        // its substituted value is checked later at computed-value time. Keep
        // the deliberately unadvertised effect stubs false: accepting a variable
        // for those properties would activate framework branches that we cannot
        // paint.
        if (variableSyntax == VariableSyntax.Valid
            && name is not ("filter" or "backdrop-filter" or "-webkit-backdrop-filter"
                or "perspective" or "contain" or "content-visibility"))
        {
            return true;
        }

        if (cssWide)
        {
            return true;
        }

        return ValueIsSupported(name, value, lower);
    }

    private static bool ValueIsSupported(string name, string value, string lower) => name switch
    {
        "display" => lower is "none" or "flex" or "inline-flex" or "inline" or "inline-block"
            or "grid" or "inline-grid" or "block" or "flow-root" or "table" or "inline-table"
            or "-webkit-box" or "-webkit-inline-box" or "contents",
        "direction" => lower is "ltr" or "rtl",
        "position" => lower is "static" or "relative" or "absolute" or "fixed" or "sticky",
        "box-sizing" => lower is "content-box" or "border-box",
        "table-layout" => lower is "auto" or "fixed",
        "container-type" => lower is "normal" or "inline-size" or "size",
        "container-name" => ContainerNamesValid(value),
        "container" => ContainerShorthandValid(value),
        "font-optical-sizing" => lower is "auto" or "none",
        "white-space" => lower is "normal" or "nowrap" or "pre" or "pre-wrap" or "pre-line" or "break-spaces",
        "text-overflow" => lower is "clip" or "ellipsis",
        "-webkit-box-orient" => lower is "horizontal" or "vertical" or "inline-axis" or "block-axis",
        "overflow-wrap" or "word-wrap" => lower is "normal" or "break-word" or "anywhere",
        "word-break" => lower is "normal" or "break-all" or "keep-all" or "break-word",
        "text-wrap" => lower is "wrap" or "nowrap" or "balance" or "pretty" or "stable",
        "text-wrap-style" => lower is "auto" or "balance",
        "clip-path" or "-webkit-clip-path" => CssText.EqualsAscii(value, "none") || ClipPathPolygonValid(value),
        "filter" or "backdrop-filter" or "-webkit-backdrop-filter" or "perspective" =>
            CssText.EqualsAscii(value, "none"),
        "contain" => CssText.EqualsAscii(value, "none"),
        "content-visibility" => CssText.EqualsAscii(value, "visible"),
        "float" => lower is "none" or "left" or "right",
        "object-fit" => lower is "fill" or "contain" or "cover" or "none" or "scale-down",
        "visibility" => lower is "visible" or "hidden" or "collapse",
        "scrollbar-gutter" => lower is "auto" or "stable" or "stable both-edges",
        "overflow" or "overflow-x" or "overflow-y" =>
            lower is "visible" or "hidden" or "clip" or "scroll" or "auto"
            || (name == "overflow" && lower.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 2 } pair
                && pair.All(token => token is "visible" or "hidden" or "clip" or "scroll" or "auto")),
        "color" or "-webkit-text-fill-color" or "background-color" => CssColor.Parse(value) is not null,
        "color-scheme" => ColorSchemeValid(value),
        "flex-direction" => lower is "row" or "row-reverse" or "column" or "column-reverse",
        "flex-wrap" => lower is "nowrap" or "wrap" or "wrap-reverse",
        "text-align" => lower is "left" or "right" or "start" or "end" or "center" or "justify",
        "text-transform" => lower is "none" or "uppercase" or "lowercase" or "capitalize",
        "text-decoration" or "text-decoration-line" =>
            lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(token => token is "none" or "underline"),
        "font-style" => lower is "normal" or "italic" || lower.StartsWith("oblique", StringComparison.Ordinal),
        "font-family" => value.Trim().Length != 0,
        "clear" => lower is "none" or "left" or "right" or "both" or "inline-start" or "inline-end",
        "vertical-align" => lower is "top" or "baseline" or "text-top" or "middle" or "bottom" or "text-bottom",
        "border-collapse" => lower is "collapse" or "separate",
        "break-inside" => lower is "auto" or "avoid" or "avoid-column",
        "-webkit-column-break-inside" => lower is "auto" or "avoid",
        "opacity" => FiniteNumber(value),
        "order" => int.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _),
        "z-index" => lower == "auto"
            || int.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _),
        "flex-grow" or "flex-shrink" => FiniteNumber(value) && CssNumber.ParseFloat(value.Trim()) >= 0f,
        "width" or "inline-size" => lower == "fit-content" || Dimension(value, auto: true),
        "height" or "block-size" or "min-width" or "min-inline-size" or "min-height"
            or "min-block-size" or "max-width" or "max-inline-size" or "max-height"
            or "max-block-size" or "flex-basis" => Dimension(value, auto: true),
        "margin" or "margin-inline" or "margin-block" => Dimensions(value, auto: true, max: 4),
        "margin-top" or "margin-right" or "margin-bottom" or "margin-left"
            or "margin-inline-start" or "margin-inline-end"
            or "margin-block-start" or "margin-block-end" => Dimension(value, auto: true),
        "padding" or "padding-inline" or "padding-block" or "inset" => Dimensions(value, auto: false, max: 4),
        "padding-top" or "padding-right" or "padding-bottom" or "padding-left"
            or "padding-inline-start" or "padding-inline-end"
            or "padding-block-start" or "padding-block-end" => Dimension(value, auto: false),
        "top" or "right" or "bottom" or "left" or "inset-inline" or "inset-block" =>
            Dimensions(value, auto: true, max: 2),
        "inset-inline-start" or "inset-inline-end" or "inset-block-start" or "inset-block-end" =>
            Dimension(value, auto: true),
        "gap" or "grid-gap" => Dimensions(value, auto: false, max: 2),
        "row-gap" or "grid-row-gap" or "column-gap" or "grid-column-gap" or "-webkit-column-gap" =>
            lower == "normal" || Dimension(value, auto: false),
        "line-height" => lower == "normal" || FiniteNumber(value) || Dimension(value, auto: false),
        "border-spacing" => Dimensions(value, auto: false, max: 2),
        "animation" or "animation-name" => value.Trim().Length != 0,

        // Everything else needs a validator that lives in the unported part of
        // style.rs. Answer conservatively rather than optimistically.
        _ => false,
    };

    private static bool FiniteNumber(string value) => CssNumber.ParseFiniteFloat(value.Trim()) is not null;

    private static bool Dimension(string value, bool auto)
    {
        value = value.Trim();
        if (auto && CssText.EqualsAscii(value, "auto"))
        {
            return true;
        }

        if (CssText.EqualsAscii(value, "auto"))
        {
            return false;
        }

        return CssLength.ResolveContextual(value, 16f, 16f, 1f, 1f, 100f) is not null;
    }

    private static bool Dimensions(string value, bool auto, int max)
    {
        var tokens = SplitWhitespaceRespectingParens(value);
        return tokens.Count != 0 && tokens.Count <= max && tokens.All(token => Dimension(token, auto));
    }

    private static List<string> SplitWhitespaceRespectingParens(string value)
    {
        var tokens = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth = Math.Max(depth - 1, 0);
            }
            else if (CssText.IsWhitespace(character) && depth == 0)
            {
                if (index > start)
                {
                    tokens.Add(value[start..index]);
                }

                start = index + 1;
            }
        }

        if (value.Length > start)
        {
            tokens.Add(value[start..]);
        }

        return tokens;
    }

    private static bool ContainerNamesValid(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (CssText.AsciiLower(value) is "none" or "initial" or "unset" or "revert" or "revert-layer")
        {
            return true;
        }

        var names = 0;
        var tokenizer = new CssTokenizer(value.AsSpan());
        while (tokenizer.TryNext(out var token))
        {
            if (token.Kind != CssTokenKind.Ident)
            {
                return false;
            }

            if (CssText.AsciiLower(token.Value) is "none" or "not" or "and" or "or")
            {
                return false;
            }

            names++;
        }

        return names != 0;
    }

    private static bool ContainerShorthandValid(string value)
    {
        var slash = value.IndexOf('/');
        if (slash < 0)
        {
            return ContainerNamesValid(value);
        }

        var names = value[..slash].Trim();
        var type = CssText.AsciiLower(value[(slash + 1)..].Trim());
        return ContainerNamesValid(names) && type is "normal" or "inline-size" or "size";
    }

    private static bool ColorSchemeValid(string value)
    {
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length != 0
            && tokens.All(token => CssText.AsciiLower(token) is "normal" or "light" or "dark" or "only")
            && tokens.Any(token => CssText.AsciiLower(token) is "normal" or "light" or "dark");
    }

    /// <summary>
    /// <c>polygon()</c> is the only painted clip-path shape, and a geometry-box
    /// variant such as <c>polygon(...) content-box</c> is not painted, so it must
    /// not pass a feature query.
    /// </summary>
    private static bool ClipPathPolygonValid(string value)
    {
        value = value.Trim();
        if (!CssText.StartsWithAscii(value, "polygon(") || !value.EndsWith(')'))
        {
            return false;
        }

        var inner = value["polygon(".Length..^1];
        if (CssLength.FindMatchingParen(inner + ")") != inner.Length)
        {
            return false;
        }

        var vertices = CssLength.SplitTopLevel(inner, ',');
        if (vertices.Count < 3)
        {
            return false;
        }

        foreach (var vertex in vertices)
        {
            var parts = SplitWhitespaceRespectingParens(vertex.Trim());
            if (parts.Count != 2)
            {
                return false;
            }

            foreach (var part in parts)
            {
                if (CssLength.ResolveContextual(part, 16f, 16f, 1f, 1f, 100f) is null)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidCustomPropertyName(string name) =>
        name.StartsWith("--", StringComparison.Ordinal) && name.Length > 2;

    /// <summary>
    /// CSS.supports() parses a declaration value, not an entire declaration
    /// list. A top-level semicolon, brace, or <c>!important</c> therefore makes
    /// the overload invalid. Delimiters inside strings or functions remain
    /// ordinary tokens.
    /// </summary>
    private static bool HasInvalidValueSyntax(string value)
    {
        var depth = 0;
        char? quote = null;
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index];
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
                        var rest = value[(index + 1)..].TrimStart();
                        if (rest.Length >= 9 && CssText.EqualsAscii(rest.AsSpan(0, 9), "important"))
                        {
                            return true;
                        }
                    }

                    break;
            }

            index++;
        }

        return quote is not null || depth != 0;
    }

    private static VariableSyntax VariableSubstitutionSyntax(string value)
    {
        var found = false;
        return Scan(value, ref found)
            ? found ? VariableSyntax.Valid : VariableSyntax.None
            : VariableSyntax.Invalid;

        static bool Scan(string value, ref bool found)
        {
            var index = 0;
            while (index < value.Length)
            {
                var character = value[index];
                if (character is '\'' or '"')
                {
                    var quote = character;
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

                var open = index;
                var identStart = open;
                while (identStart > 0
                    && (CssText.IsAsciiAlphanumeric(value[identStart - 1]) || value[identStart - 1] is '-' or '_'))
                {
                    identStart--;
                }

                if (CssLength.FindMatchingParen(value[(open + 1)..]) is not { } relativeClose)
                {
                    return false;
                }

                var close = open + 1 + relativeClose;
                var arguments = value[(open + 1)..close];
                if (CssText.EqualsAscii(value[identStart..open], "var"))
                {
                    var comma = arguments.IndexOf(',');
                    var name = comma < 0 ? arguments : arguments[..comma];
                    var fallback = comma < 0 ? null : arguments[(comma + 1)..];
                    if (!ValidCustomPropertyName(name.Trim()))
                    {
                        return false;
                    }

                    if (fallback is not null && !Scan(fallback, ref found))
                    {
                        return false;
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
    }

    /// <summary>The property names the renderer implements, from style.rs.</summary>
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "display", "direction", "width", "inline-size", "height", "block-size", "min-width",
        "min-inline-size", "min-height", "min-block-size", "max-width", "max-inline-size",
        "max-height", "max-block-size", "box-sizing", "container", "container-type",
        "container-name", "aspect-ratio", "margin", "margin-top", "margin-right",
        "margin-bottom", "margin-left", "margin-inline", "margin-inline-start",
        "margin-inline-end", "margin-block", "margin-block-start", "margin-block-end",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "padding-inline", "padding-inline-start", "padding-inline-end", "padding-block",
        "padding-block-start", "padding-block-end", "border-radius", "border-top-left-radius",
        "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius",
        "clip-path", "-webkit-clip-path", "border", "border-width", "border-top-width",
        "border-right-width", "border-bottom-width", "border-left-width", "border-style",
        "border-top-style", "border-right-style", "border-bottom-style", "border-left-style",
        "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
        "border-top", "border-right", "border-bottom", "border-left", "border-inline",
        "border-block", "border-inline-start", "border-inline-end", "border-block-start",
        "border-block-end", "border-inline-width", "border-inline-style",
        "border-inline-color", "border-block-width", "border-block-style",
        "border-block-color", "border-inline-start-width", "border-inline-start-style",
        "border-inline-start-color", "border-inline-end-width", "border-inline-end-style",
        "border-inline-end-color", "border-block-start-width", "border-block-start-style",
        "border-block-start-color", "border-block-end-width", "border-block-end-style",
        "border-block-end-color", "background", "background-color", "background-image",
        "background-size", "background-position", "background-repeat", "background-origin",
        "background-clip", "-webkit-background-clip", "mask-image", "-webkit-mask-image",
        "mask-size", "-webkit-mask-size", "mask-repeat", "-webkit-mask-repeat", "color",
        "content", "-webkit-text-fill-color", "fill", "stroke", "stroke-width", "border-color",
        "outline", "outline-width", "outline-style", "outline-color", "outline-offset",
        "color-scheme", "font-size", "letter-spacing", "font", "font-weight", "font-family",
        "font-style", "font-optical-sizing", "font-variation-settings", "text-align",
        "text-indent", "text-transform", "text-decoration", "text-decoration-line",
        "line-height", "white-space", "text-overflow", "-webkit-line-clamp",
        "-webkit-box-orient", "overflow-wrap", "word-wrap", "word-break", "text-wrap",
        "text-wrap-style", "align-items", "justify-items", "place-items", "align-self",
        "justify-self", "place-self", "align-content", "justify-content", "place-content",
        "flex-flow", "flex-direction", "flex-wrap", "flex-grow", "flex-shrink", "flex-basis",
        "flex", "order", "position", "float", "counter-reset", "counter-increment",
        "counter-set", "object-fit", "object-position", "top", "right", "bottom", "left",
        "inset", "inset-inline", "inset-inline-start", "inset-inline-end", "inset-block",
        "inset-block-start", "inset-block-end", "overflow", "overflow-x", "overflow-y",
        "scrollbar-gutter", "visibility", "opacity", "animation", "animation-name",
        "animation-duration", "animation-delay", "animation-fill-mode",
        "animation-iteration-count", "animation-direction", "animation-play-state", "z-index",
        "clear", "vertical-align", "list-style", "list-style-type", "gap", "grid-gap",
        "row-gap", "grid-row-gap", "column-gap", "grid-column-gap", "-webkit-column-gap",
        "column-count", "-webkit-column-count", "columns", "-webkit-columns", "break-inside",
        "-webkit-column-break-inside", "border-spacing", "border-collapse", "table-layout",
        "grid-template-columns", "grid-template-rows", "grid-auto-columns", "grid-auto-rows",
        "grid-template-areas", "grid-template", "grid", "grid-auto-flow", "grid-area",
        "grid-column", "grid-row", "grid-column-start", "grid-column-end", "grid-row-start",
        "grid-row-end", "transform", "transform-origin", "translate", "rotate", "scale",
        "filter", "backdrop-filter", "-webkit-backdrop-filter", "perspective", "contain",
        "will-change", "content-visibility", "box-shadow", "-webkit-box-shadow"
    };
}
