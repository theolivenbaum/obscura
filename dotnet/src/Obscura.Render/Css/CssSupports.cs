namespace Obscura.Render.Css;

/// <summary>
/// <c>@supports</c> condition evaluation.
/// </summary>
/// <remarks>
/// This follows the same shape as Gecko's SupportsCondition tree: declaration
/// and selector leaves, arbitrary grouping, unary <c>not</c>, and top-level
/// <c>and</c>/<c>or</c> chains. Unknown or malformed future syntax is false
/// instead of optimistically enabling a fallback branch.
/// </remarks>
public static class CssSupports
{
    public static bool ConditionApplies(string condition)
    {
        condition = condition.Trim();
        condition = (StripPrefix(condition, "@supports") ?? StripPrefix(condition, "supports") ?? condition).Trim();
        return Evaluate(condition) ?? false;
    }

    private static string? StripPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : null;

    internal static bool? Evaluate(string condition)
    {
        condition = condition.Trim();
        if (condition.Length == 0 || !BalancedSyntax(condition))
        {
            return null;
        }

        if (EnclosingParenthesized(condition) is { } inner)
        {
            return Evaluate(inner);
        }

        if (condition.Length >= 3
            && CssText.EqualsAscii(condition.AsSpan(0, 3), "not")
            && condition.Length > 3
            && CssText.IsAsciiWhitespace(condition[3]))
        {
            return Evaluate(condition[3..].Trim()) is { } negated ? !negated : null;
        }

        var orParts = SplitOperator(condition, "or");
        var andParts = SplitOperator(condition, "and");

        // CSS Conditional Rules does not permit `and` and `or` at the same
        // nesting level. Treat the entire condition as invalid rather than
        // inventing JavaScript-like precedence.
        if (orParts is not null && andParts is not null)
        {
            return null;
        }

        if (orParts is not null)
        {
            var any = false;
            foreach (var part in orParts)
            {
                if (Evaluate(part) is not { } result)
                {
                    return null;
                }

                any |= result;
            }

            return any;
        }

        if (andParts is not null)
        {
            var all = true;
            foreach (var part in andParts)
            {
                if (Evaluate(part) is not { } result)
                {
                    return null;
                }

                all &= result;
            }

            return all;
        }

        if (CssText.StartsWithAscii(condition, "selector(") && condition.EndsWith(')'))
        {
            var selectorText = condition["selector(".Length..^1];
            return CssHost.SelectorParses(selectorText.Trim());
        }

        var separator = condition.IndexOf(':');
        if (separator < 0)
        {
            // A syntactically balanced unknown functional condition is a valid
            // general-enclosed leaf whose result is false. Everything else is a
            // malformed condition, important for `not`: invalid syntax must not
            // become true merely because it was negated.
            var open = condition.IndexOf('(');
            return open > 0 && condition.EndsWith(')') ? false : null;
        }

        return CssHost.SupportsDeclaration(condition[..separator], condition[(separator + 1)..]);
    }

    internal static bool BalancedSyntax(string condition)
    {
        var stack = new Stack<char>();
        char? quote = null;
        var escaped = false;
        foreach (var character in condition)
        {
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
                    stack.Push(')');
                    break;
                case '[':
                    stack.Push(']');
                    break;
                case ')':
                case ']':
                    if (stack.Count == 0 || stack.Pop() != character)
                    {
                        return false;
                    }

                    break;
            }
        }

        return !escaped && quote is null && stack.Count == 0;
    }

    /// <summary>
    /// Return the contents when one outer parenthesis pair encloses the complete
    /// expression. A declaration leaf such as <c>(display:grid)</c> intentionally
    /// becomes <c>display:grid</c> and is handled after boolean operators.
    /// </summary>
    internal static string? EnclosingParenthesized(string condition)
    {
        if (!condition.StartsWith('('))
        {
            return null;
        }

        var depth = 0;
        char? quote = null;
        for (var index = 0; index < condition.Length; index++)
        {
            var character = condition[index];
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
                    if (depth < 0)
                    {
                        return null;
                    }

                    if (depth == 0)
                    {
                        return index + 1 == condition.Length ? condition[1..index] : null;
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Split a condition on a whitespace-delimited top-level boolean operator.
    /// Null when the operator does not occur at this level or the condition is
    /// malformed.
    /// </summary>
    internal static List<string>? SplitOperator(string condition, string op)
    {
        var parts = new List<string>();
        var start = 0;
        var index = 0;
        var depth = 0;
        char? quote = null;
        while (index < condition.Length)
        {
            var character = condition[index];
            if (quote is { } active)
            {
                if (character == active)
                {
                    quote = null;
                }
                else if (character == '\\')
                {
                    index++;
                }

                index++;
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
                    if (depth < 0)
                    {
                        return null;
                    }

                    break;
                default:
                    if (depth == 0
                        && index + op.Length <= condition.Length
                        && CssText.EqualsAscii(condition.AsSpan(index, op.Length), op)
                        && index > 0
                        && CssText.IsAsciiWhitespace(condition[index - 1])
                        && index + op.Length < condition.Length
                        && CssText.IsAsciiWhitespace(condition[index + op.Length]))
                    {
                        var part = condition[start..index].Trim();
                        if (part.Length == 0)
                        {
                            return null;
                        }

                        parts.Add(part);
                        index += op.Length;
                        start = index;
                        continue;
                    }

                    break;
            }

            index++;
        }

        if (depth != 0 || quote is not null || parts.Count == 0)
        {
            return null;
        }

        var tail = condition[start..].Trim();
        if (tail.Length == 0)
        {
            return null;
        }

        parts.Add(tail);
        return parts;
    }
}
