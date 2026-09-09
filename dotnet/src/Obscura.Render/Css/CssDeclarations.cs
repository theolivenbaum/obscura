using System.Text;

namespace Obscura.Render.Css;

/// <summary>
/// Declaration-stream helpers.
/// </summary>
/// <remarks>
/// SHARED CODE: <see cref="Split"/> and <see cref="Partition"/> are ports of
/// <c>obscura-render/src/style.rs</c>'s <c>split_declarations</c> and
/// <c>partition_declarations</c>. css.rs calls them on nearly every path, so
/// they live here to keep this port self-contained; the coordinator should
/// forward them to the style.rs port once it lands.
/// </remarks>
public static class CssDeclarations
{
    /// <summary>
    /// Split a declaration block on top-level semicolons. Parentheses and
    /// braces both count as depth: once <c>@layer</c> bodies are admitted,
    /// CSS-nested rules appear inside declaration lists, and keeping a nested
    /// block as one chunk makes it a single unparseable declaration that is
    /// dropped rather than leaking its inner declarations into the parent rule.
    /// </summary>
    public static List<string> Split(string css)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var index = 0; index < css.Length; index++)
        {
            var current = css[index];
            if (quote is { } open)
            {
                if (current == open)
                {
                    quote = null;
                }

                continue;
            }

            switch (current)
            {
                case '\'':
                case '"':
                    quote = current;
                    break;
                case '(':
                case '{':
                    depth++;
                    break;
                case ')':
                case '}':
                    depth = Math.Max(depth - 1, 0);
                    break;
                case ';' when depth == 0:
                    parts.Add(css[start..index]);
                    start = index + 1;
                    break;
            }
        }

        parts.Add(css[start..]);
        return parts;
    }

    /// <summary>
    /// Split a declaration block into its normal and <c>!important</c> streams,
    /// normalizing each declaration to <c>name:value;</c>.
    /// </summary>
    public static (string Normal, string Important) Partition(string css)
    {
        var normal = new StringBuilder();
        var important = new StringBuilder();
        foreach (var raw in Split(css))
        {
            var declaration = raw.Trim();
            if (declaration.Length == 0)
            {
                continue;
            }

            var separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var name = declaration[..separator];
            var value = declaration[(separator + 1)..].Trim();
            var isImportant = false;
            var bang = value.LastIndexOf('!');
            if (bang >= 0 && CssText.EqualsAscii(value[(bang + 1)..].Trim(), "important"))
            {
                value = value[..bang].TrimEnd();
                isImportant = true;
            }

            var target = isImportant ? important : normal;
            target.Append(name.Trim());
            target.Append(':');
            target.Append(value);
            target.Append(';');
        }

        return (normal.ToString(), important.ToString());
    }

    internal static void AppendDeclarationStream(StringBuilder target, string declarations)
    {
        if (declarations.Trim().Length == 0)
        {
            return;
        }

        if (target.Length != 0 && target[^1] != ';')
        {
            target.Append(';');
        }

        target.Append(declarations);
    }
}

/// <summary>
/// Cached declaration features which otherwise require an extra stream walk or
/// an allocated substituted copy for every matching element.
/// </summary>
/// <remarks>
/// Computed once while the stylesheet is indexed. False positives only cost
/// work, while exact declaration-name checks keep the skip paths sound.
/// </remarks>
public readonly record struct DeclarationStreamFlags(
    bool HasCustomProperties,
    bool HasVar,
    bool HasColorScheme,
    bool HasAnimation,
    bool HasTransform,
    bool HasOpacity)
{
    public static DeclarationStreamFlags Compute(string css)
    {
        var hasCustomProperties = false;
        var hasVar = false;
        var hasColorScheme = false;
        var hasAnimation = false;
        var hasTransform = false;
        var hasOpacity = false;

        foreach (var declaration in CssDeclarations.Split(css))
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var name = declaration[..separator].Trim();
            var value = declaration[(separator + 1)..];

            hasCustomProperties |= name.StartsWith("--", StringComparison.Ordinal) && name.Length > 2;
            hasVar |= value.Contains("var(", StringComparison.Ordinal);
            if (CssText.EqualsAscii(name, "color-scheme"))
            {
                hasColorScheme = true;
            }

            if (CssText.EqualsAscii(name, "animation")
                || (name.Length >= 10 && CssText.EqualsAscii(name.AsSpan(0, 10), "animation-")))
            {
                hasAnimation = true;
            }

            if (CssText.EqualsAscii(name, "transform") || CssText.EqualsAscii(name, "all"))
            {
                hasTransform = true;
            }

            if (CssText.EqualsAscii(name, "opacity") || CssText.EqualsAscii(name, "all"))
            {
                hasOpacity = true;
            }
        }

        return new DeclarationStreamFlags(
            hasCustomProperties,
            hasVar,
            hasColorScheme,
            hasAnimation,
            hasTransform,
            hasOpacity);
    }
}

/// <summary>
/// <c>var()</c> substitution.
/// </summary>
public static class CssVariables
{
    private const int MaxSubstitutionDepth = 16;

    /// <summary>
    /// Resolve variables one declaration at a time. An invalid variable poisons
    /// its entire declaration at computed-value time, but must not erase
    /// unrelated declarations in the same rule.
    /// </summary>
    public static string SubstituteDeclarations(
        string css,
        IReadOnlyDictionary<string, string> properties,
        bool hasVar)
    {
        // Partitioned rule streams are already normalized declaration blocks.
        // If no value contains var(), applying them directly is both equivalent
        // and avoids a String plus one split/serialize pass for every matched
        // rule.
        if (!hasVar)
        {
            return css;
        }

        var expanded = new StringBuilder();
        foreach (var declaration in CssDeclarations.Split(css))
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var value = SubstituteVarValue(declaration[(separator + 1)..].Trim(), properties, 0);
            if (value is null)
            {
                continue;
            }

            expanded.Append(declaration[..separator].Trim());
            expanded.Append(':');
            expanded.Append(value);
            expanded.Append(';');
        }

        return expanded.ToString();
    }

    /// <summary>
    /// Substitute every <c>var(--name, fallback?)</c> in one property value.
    /// </summary>
    /// <returns>
    /// <c>null</c> represents CSS's guaranteed-invalid value. Invalidity
    /// propagates through an intermediate custom property so an outer var() can
    /// use its own fallback
    /// (<c>--toggle:var(--missing) dark; color:var(--toggle,light)</c>).
    /// </returns>
    public static string? SubstituteVarValue(
        string input,
        IReadOnlyDictionary<string, string> properties,
        int depth)
    {
        if (depth > MaxSubstitutionDepth)
        {
            return null;
        }

        if (!input.Contains("var(", StringComparison.Ordinal))
        {
            return input;
        }

        var output = new StringBuilder();
        var rest = input;
        while (true)
        {
            var position = rest.IndexOf("var(", StringComparison.Ordinal);
            if (position < 0)
            {
                break;
            }

            output.Append(rest, 0, position);
            var after = rest[(position + 4)..];

            // Matching close paren, respecting nesting.
            var nesting = 1;
            var end = -1;
            for (var index = 0; index < after.Length; index++)
            {
                switch (after[index])
                {
                    case '(':
                        nesting++;
                        break;
                    case ')':
                        nesting--;
                        if (nesting == 0)
                        {
                            end = index;
                        }

                        break;
                }

                if (end >= 0)
                {
                    break;
                }
            }

            if (end < 0)
            {
                return null;
            }

            var inner = after[..end];
            var comma = inner.IndexOf(',');
            var name = comma >= 0 ? inner[..comma].Trim() : inner.Trim();
            var fallback = comma >= 0 ? inner[(comma + 1)..].Trim() : null;

            string? resolved = null;
            if (properties.TryGetValue(name, out var stored))
            {
                resolved = SubstituteVarValue(stored, properties, depth + 1);
            }

            string replacement;
            if (resolved is not null)
            {
                replacement = resolved;
            }
            else
            {
                if (fallback is null)
                {
                    return null;
                }

                var expanded = SubstituteVarValue(fallback, properties, depth + 1);
                if (expanded is null)
                {
                    return null;
                }

                replacement = expanded;
            }

            // var() substitutes a token sequence, not source text. Insert a
            // separator only where reparsing would merge boundary tokens:
            // `2px` + `solid` must not become the dimension `2pxsolid`, and
            // `10` + `%` must not become a percentage. Do not add unconditional
            // whitespace: it is significant around calc()'s `+` and `-`.
            if (output.Length != 0 && replacement.Length != 0
                && BoundaryMerges(output[^1], replacement[0]))
            {
                output.Append(' ');
            }

            output.Append(replacement);

            var tail = after[(end + 1)..];
            if (replacement.Length != 0 && tail.Length != 0
                && BoundaryMerges(replacement[^1], tail[0]))
            {
                output.Append(' ');
            }

            rest = tail;
        }

        output.Append(rest);
        return output.ToString();
    }

    internal static bool BoundaryMerges(char left, char right)
    {
        if (CssText.IsAsciiDigit(left) && right == '-')
        {
            // A number followed by `-` remains two tokens. Separating these
            // would incorrectly make `calc(var(--n)- 1px)` satisfy calc's
            // whitespace requirement.
            return false;
        }

        return (IsName(left) && IsName(right))
            || ((CssText.IsAsciiDigit(left) || left == '.') && (right == '.' || right == '%' || IsName(right)))
            || (left is '#' or '@' && IsName(right))
            || (!CssText.IsAsciiDigit(left) && IsName(left) && right == '(')
            || (left == '+' && (CssText.IsAsciiDigit(right) || right == '.'))
            || (left == '/' && right == '*');

        static bool IsName(char value) =>
            CssText.IsAsciiAlphanumeric(value) || value is '_' or '-' or '\\' || !CssText.IsAscii(value);
    }
}
