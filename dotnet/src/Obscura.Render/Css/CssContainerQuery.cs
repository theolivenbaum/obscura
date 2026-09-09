namespace Obscura.Render.Css;

/// <summary>Identifier of a node in the parsed <c>@container</c> condition arena.</summary>
public readonly record struct ContainerConditionId(uint Value)
{
    public static readonly ContainerConditionId None = new(0);

    public override string ToString() => $"ContainerConditionId({Value})";
}

/// <summary>
/// One <c>@container</c> rule's condition.
/// </summary>
/// <remarks>
/// Comma-separated queries in one prelude are alternatives; parent-linked nodes
/// represent nested <c>@container</c> rules that must all match.
/// </remarks>
public sealed record ContainerConditionNode(ContainerConditionId Parent, List<ContainerQuery> Alternatives)
{
    public bool Equals(ContainerConditionNode? other) =>
        other is not null && Parent == other.Parent && Alternatives.SequenceEqual(other.Alternatives);

    public override int GetHashCode() => HashCode.Combine(Parent, Alternatives.Count);
}

public sealed record ContainerQuery(string? Name, ContainerQueryExpr? Condition);

public abstract record ContainerQueryExpr
{
    public sealed record Feature(ContainerSizeFeature Value) : ContainerQueryExpr;

    /// <summary>
    /// Syntactically valid future/general-enclosed syntax has Kleene
    /// <c>unknown</c> truth. Retaining it prevents one unknown comma arm from
    /// discarding supported alternatives in the same <c>@container</c> rule.
    /// </summary>
    public sealed record Unknown : ContainerQueryExpr
    {
        public static readonly Unknown Instance = new();
    }

    public sealed record Not(ContainerQueryExpr Inner) : ContainerQueryExpr;

    public sealed record And(IReadOnlyList<ContainerQueryExpr> Items) : ContainerQueryExpr
    {
        public bool Equals(And? other) => other is not null && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => Items.Count;
    }

    public sealed record Or(IReadOnlyList<ContainerQueryExpr> Items) : ContainerQueryExpr
    {
        public bool Equals(Or? other) => other is not null && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => Items.Count;
    }
}

public enum ContainerQueryAxis
{
    Width,
    Height,
    InlineSize,
    BlockSize,
}

public enum ContainerQueryComparison
{
    Min,
    Max,
    GreaterThan,
    LessThan,
    Equal,
}

public readonly record struct ContainerSizeFeature(
    ContainerQueryAxis Axis,
    ContainerQueryComparison Comparison,
    ContainerQueryLength Length);

public readonly record struct ContainerQueryLength(ContainerQueryLengthUnit Unit, float Value)
{
    public static ContainerQueryLength Px(float value) => new(ContainerQueryLengthUnit.Px, value);

    public static ContainerQueryLength Em(float value) => new(ContainerQueryLengthUnit.Em, value);

    public static ContainerQueryLength Rem(float value) => new(ContainerQueryLengthUnit.Rem, value);
}

public enum ContainerQueryLengthUnit
{
    Px,
    Em,
    Rem,
}

/// <summary>Kleene three-valued truth used by container queries.</summary>
public enum ContainerQueryTruth
{
    True,
    False,
    Unknown,
}

internal static class ContainerQueryTruthOps
{
    public static ContainerQueryTruth And(this ContainerQueryTruth left, ContainerQueryTruth right) =>
        left == ContainerQueryTruth.False || right == ContainerQueryTruth.False
            ? ContainerQueryTruth.False
            : left == ContainerQueryTruth.True && right == ContainerQueryTruth.True
                ? ContainerQueryTruth.True
                : ContainerQueryTruth.Unknown;

    public static ContainerQueryTruth Or(this ContainerQueryTruth left, ContainerQueryTruth right) =>
        left == ContainerQueryTruth.True || right == ContainerQueryTruth.True
            ? ContainerQueryTruth.True
            : left == ContainerQueryTruth.False && right == ContainerQueryTruth.False
                ? ContainerQueryTruth.False
                : ContainerQueryTruth.Unknown;

    public static ContainerQueryTruth Not(this ContainerQueryTruth value) => value switch
    {
        ContainerQueryTruth.True => ContainerQueryTruth.False,
        ContainerQueryTruth.False => ContainerQueryTruth.True,
        _ => ContainerQueryTruth.Unknown,
    };
}

/// <summary>One query container's generated box, as seen by container queries.</summary>
public sealed class ContainerBox
{
    public required ContainerType ContainerType { get; init; }

    /// <summary>
    /// Axes on which size containment actually applies to the generated box.
    /// This can be <c>Normal</c> even when computed <c>container-type</c> is
    /// non-normal (for example a non-atomic inline or an internal table box).
    /// </summary>
    public required ContainerType AvailableType { get; init; }

    public required IReadOnlyList<string> Names { get; init; }

    public required float ContentWidth { get; init; }

    public required float ContentHeight { get; init; }

    public required float FontSize { get; init; }

    public override bool Equals(object? obj)
    {
        if (obj is not ContainerBox other)
        {
            return false;
        }

        if (ContainerType != other.ContainerType
            || AvailableType != other.AvailableType
            || !Names.SequenceEqual(other.Names, StringComparer.Ordinal))
        {
            return false;
        }

        return AvailableType switch
        {
            ContainerType.Normal => true,
            ContainerType.InlineSize => ContentWidth == other.ContentWidth && FontSize == other.FontSize,
            ContainerType.Size => ContentWidth == other.ContentWidth
                && ContentHeight == other.ContentHeight
                && FontSize == other.FontSize,
            _ => false,
        };
    }

    public override int GetHashCode() => HashCode.Combine(ContainerType, AvailableType, Names.Count);
}

internal readonly record struct ContainerQueryRequiredAxes(bool Inline, bool Block);

/// <summary>
/// <c>@container</c> prelude parsing and Kleene evaluation.
/// </summary>
public static class CssContainerQuery
{
    internal const int MaxDepth = 64;

    public static List<ContainerQuery>? ParseQueryList(string prelude)
    {
        var queries = new List<ContainerQuery>();
        foreach (var part in CssMediaQuery.SplitList(prelude))
        {
            if (ParseQuery(part) is not { } query)
            {
                return null;
            }

            queries.Add(query);
        }

        return queries.Count != 0 ? queries : null;
    }

    public static ContainerQuery? ParseQuery(string input)
    {
        input = input.Trim();
        if (input.Length == 0)
        {
            return null;
        }

        var startsWithCondition = input.StartsWith('(') || StripAsciiKeyword(input, "not") is not null;
        string? name;
        string? condition;
        if (startsWithCondition)
        {
            name = null;
            condition = input;
        }
        else
        {
            var split = -1;
            for (var index = 0; index < input.Length; index++)
            {
                if (CssText.IsWhitespace(input[index]))
                {
                    split = index;
                    break;
                }
            }

            if (split >= 0)
            {
                name = ParseQueryName(input[..split]);
                if (name is null)
                {
                    return null;
                }

                var tail = input[split..].Trim();
                condition = tail.Length != 0 ? tail : null;
            }
            else
            {
                name = ParseQueryName(input);
                if (name is null)
                {
                    return null;
                }

                condition = null;
            }
        }

        ContainerQueryExpr? expression = null;
        if (condition is not null)
        {
            expression = ParseExpr(condition);
            if (expression is null)
            {
                return null;
            }
        }

        return new ContainerQuery(name, expression);
    }

    public static string? ParseQueryName(string input)
    {
        var ident = CssTokenizer.SoleIdent(input.Trim());
        if (ident is null)
        {
            return null;
        }

        return IsReservedCustomIdent(CssText.AsciiLower(ident)) ? null : ident;
    }

    private static bool IsReservedCustomIdent(string lower) =>
        lower is "none" or "not" or "and" or "or" or "default"
            or "initial" or "inherit" or "unset" or "revert" or "revert-layer";

    public static ContainerQueryExpr? ParseExpr(string input) => ParseExprAtDepth(input, 0);

    private static ContainerQueryExpr? ParseExprAtDepth(string input, int depth)
    {
        if (depth >= MaxDepth)
        {
            return null;
        }

        input = input.Trim();
        var orParts = CssSupports.SplitOperator(input, "or");
        var andParts = CssSupports.SplitOperator(input, "and");

        // One grammar level is either a homogeneous AND chain or a homogeneous
        // OR chain. Authors must parenthesize any mixture.
        if (orParts is not null && andParts is not null)
        {
            return null;
        }

        if (orParts is not null)
        {
            var items = new List<ContainerQueryExpr>(orParts.Count);
            foreach (var part in orParts)
            {
                if (ParseInParens(part, depth + 1) is not { } item)
                {
                    return null;
                }

                items.Add(item);
            }

            return new ContainerQueryExpr.Or(items);
        }

        if (andParts is not null)
        {
            var items = new List<ContainerQueryExpr>(andParts.Count);
            foreach (var part in andParts)
            {
                if (ParseInParens(part, depth + 1) is not { } item)
                {
                    return null;
                }

                items.Add(item);
            }

            return new ContainerQueryExpr.And(items);
        }

        if (StripAsciiKeyword(input, "not") is { } rest)
        {
            return ParseInParens(rest, depth + 1) is { } inner ? new ContainerQueryExpr.Not(inner) : null;
        }

        return ParseInParens(input, depth + 1);
    }

    private static ContainerQueryExpr? ParseInParens(string input, int depth)
    {
        if (depth >= MaxDepth)
        {
            return null;
        }

        if (CssSupports.EnclosingParenthesized(input) is not { } inner)
        {
            return null;
        }

        if (ParseSizeFeature(inner) is { } feature)
        {
            return feature;
        }

        return ParseExprAtDepth(inner, depth + 1)
            ?? (IsGeneralEnclosed(inner) ? ContainerQueryExpr.Unknown.Instance : null);
    }

    private static bool IsGeneralEnclosed(string input) =>
        CssTokenizer.StartsWithIdentOrFunction(input.Trim());

    internal static string? StripAsciiKeyword(string input, string keyword)
    {
        if (input.Length < keyword.Length || !CssText.EqualsAscii(input.AsSpan(0, keyword.Length), keyword))
        {
            return null;
        }

        var rest = input[keyword.Length..];
        return rest.Length != 0 && CssText.IsWhitespace(rest[0]) ? rest.TrimStart() : null;
    }

    private static ContainerQueryExpr? ParseSizeFeature(string input)
    {
        if (ParseAxis(input) is { } bareAxis)
        {
            return new ContainerQueryExpr.Feature(new ContainerSizeFeature(
                bareAxis,
                ContainerQueryComparison.GreaterThan,
                ContainerQueryLength.Px(0f)));
        }

        var colon = input.IndexOf(':');
        if (colon >= 0)
        {
            (ContainerQueryComparison Comparison, ContainerQueryAxis Axis)? named =
                CssText.AsciiLower(input[..colon].Trim()) switch
                {
                    "min-width" => (ContainerQueryComparison.Min, ContainerQueryAxis.Width),
                    "max-width" => (ContainerQueryComparison.Max, ContainerQueryAxis.Width),
                    "width" => (ContainerQueryComparison.Equal, ContainerQueryAxis.Width),
                    "min-height" => (ContainerQueryComparison.Min, ContainerQueryAxis.Height),
                    "max-height" => (ContainerQueryComparison.Max, ContainerQueryAxis.Height),
                    "height" => (ContainerQueryComparison.Equal, ContainerQueryAxis.Height),
                    "min-inline-size" => (ContainerQueryComparison.Min, ContainerQueryAxis.InlineSize),
                    "max-inline-size" => (ContainerQueryComparison.Max, ContainerQueryAxis.InlineSize),
                    "inline-size" => (ContainerQueryComparison.Equal, ContainerQueryAxis.InlineSize),
                    "min-block-size" => (ContainerQueryComparison.Min, ContainerQueryAxis.BlockSize),
                    "max-block-size" => (ContainerQueryComparison.Max, ContainerQueryAxis.BlockSize),
                    "block-size" => (ContainerQueryComparison.Equal, ContainerQueryAxis.BlockSize),
                    _ => null,
                };

            if (named is not { } feature)
            {
                return null;
            }

            return ParseLength(input[(colon + 1)..]) is { } length
                ? new ContainerQueryExpr.Feature(
                    new ContainerSizeFeature(feature.Axis, feature.Comparison, length))
                : null;
        }

        if (SplitRange(input) is not { } range)
        {
            return null;
        }

        var (operands, operators) = range;
        if (operands.Count == 2 && operators.Count == 1)
        {
            if (ParseAxis(operands[0]) is { } leftAxis)
            {
                return MakeFeature(leftAxis, operators[0], operands[1], axisOnLeft: true);
            }

            return ParseAxis(operands[1]) is { } rightAxis
                ? MakeFeature(rightAxis, operators[0], operands[0], axisOnLeft: false)
                : null;
        }

        if (operands.Count == 3 && operators.Count == 2)
        {
            // Chained ranges must point consistently through the feature:
            // `10px < width <= 20px` or the fully reversed equivalent. Equality
            // is valid only in a single comparison, and a mixed direction such
            // as `10px < width > 20px` is not a range.
            var forward = operators[0] is "<" or "<=" && operators[1] is "<" or "<=";
            var reverse = operators[0] is ">" or ">=" && operators[1] is ">" or ">=";
            if (!forward && !reverse)
            {
                return null;
            }

            if (ParseAxis(operands[1]) is not { } axis)
            {
                return null;
            }

            var lower = MakeFeature(axis, operators[0], operands[0], axisOnLeft: false);
            var upper = MakeFeature(axis, operators[1], operands[2], axisOnLeft: true);
            return lower is null || upper is null ? null : new ContainerQueryExpr.And([lower, upper]);
        }

        return null;

        static ContainerQueryExpr? MakeFeature(
            ContainerQueryAxis axis,
            string op,
            string value,
            bool axisOnLeft)
        {
            ContainerQueryComparison comparison;
            switch (op)
            {
                case ">=" when axisOnLeft:
                case "<=" when !axisOnLeft:
                    comparison = ContainerQueryComparison.Min;
                    break;
                case "<=" when axisOnLeft:
                case ">=" when !axisOnLeft:
                    comparison = ContainerQueryComparison.Max;
                    break;
                case ">" when axisOnLeft:
                case "<" when !axisOnLeft:
                    comparison = ContainerQueryComparison.GreaterThan;
                    break;
                case "<" when axisOnLeft:
                case ">" when !axisOnLeft:
                    comparison = ContainerQueryComparison.LessThan;
                    break;
                case "=":
                    comparison = ContainerQueryComparison.Equal;
                    break;
                default:
                    return null;
            }

            return ParseLength(value) is { } length
                ? new ContainerQueryExpr.Feature(new ContainerSizeFeature(axis, comparison, length))
                : null;
        }
    }

    private static ContainerQueryAxis? ParseAxis(string input) => CssText.AsciiLower(input.Trim()) switch
    {
        "width" => ContainerQueryAxis.Width,
        "height" => ContainerQueryAxis.Height,
        "inline-size" => ContainerQueryAxis.InlineSize,
        "block-size" => ContainerQueryAxis.BlockSize,
        _ => null,
    };

    private static (List<string> Operands, List<string> Operators)? SplitRange(string input)
    {
        var operands = new List<string>();
        var operators = new List<string>();
        var start = 0;
        var index = 0;
        while (index < input.Length)
        {
            if (input[index] is '<' or '>' or '=')
            {
                var end = index + 1 < input.Length && input[index + 1] == '=' ? index + 2 : index + 1;
                var operand = input[start..index].Trim();
                if (operand.Length == 0)
                {
                    return null;
                }

                operands.Add(operand);
                operators.Add(input[index..end]);
                start = end;
                index = end;
                continue;
            }

            index++;
        }

        if (operators.Count == 0 || operators.Count > 2)
        {
            return null;
        }

        var tail = input[start..].Trim();
        if (tail.Length == 0)
        {
            return null;
        }

        operands.Add(tail);
        return (operands, operators);
    }

    internal static ContainerQueryLength? ParseLength(string input)
    {
        var lower = CssText.AsciiLower(input.Trim());
        if (Suffix(lower, "rem") is { } rem)
        {
            return ContainerQueryLength.Rem(rem);
        }

        if (Suffix(lower, "em") is { } em)
        {
            return ContainerQueryLength.Em(em);
        }

        if (Suffix(lower, "px") is { } px)
        {
            return ContainerQueryLength.Px(px);
        }

        var bare = CssNumber.ParseFiniteFloat(lower);
        return bare == 0f ? ContainerQueryLength.Px(0f) : null;

        static float? Suffix(string value, string unit) =>
            value.EndsWith(unit, StringComparison.Ordinal)
                ? CssNumber.ParseFiniteFloat(value.AsSpan()[..^unit.Length])
                : null;
    }

    internal static ContainerQueryRequiredAxes RequiredAxes(ContainerQueryExpr expr)
    {
        switch (expr)
        {
            case ContainerQueryExpr.Feature feature:
                return feature.Value.Axis is ContainerQueryAxis.Width or ContainerQueryAxis.InlineSize
                    ? new ContainerQueryRequiredAxes(true, false)
                    : new ContainerQueryRequiredAxes(false, true);
            case ContainerQueryExpr.Unknown:
                return default;
            case ContainerQueryExpr.Not not:
                return RequiredAxes(not.Inner);
            case ContainerQueryExpr.And and:
                return Fold(and.Items);
            case ContainerQueryExpr.Or or:
                return Fold(or.Items);
            default:
                return default;
        }

        static ContainerQueryRequiredAxes Fold(IReadOnlyList<ContainerQueryExpr> items)
        {
            var axes = default(ContainerQueryRequiredAxes);
            foreach (var item in items)
            {
                var itemAxes = RequiredAxes(item);
                axes = new ContainerQueryRequiredAxes(axes.Inline || itemAxes.Inline, axes.Block || itemAxes.Block);
            }

            return axes;
        }
    }

    public static ContainerQueryTruth Evaluate(ContainerQueryExpr expr, ContainerBox container, float rootFontSize)
    {
        switch (expr)
        {
            case ContainerQueryExpr.Feature feature:
            {
                var actual = feature.Value.Axis is ContainerQueryAxis.Width or ContainerQueryAxis.InlineSize
                    ? container.ContentWidth
                    : container.ContentHeight;
                var threshold = feature.Value.Length.Unit switch
                {
                    ContainerQueryLengthUnit.Px => feature.Value.Length.Value,
                    ContainerQueryLengthUnit.Em => feature.Value.Length.Value * container.FontSize,
                    _ => feature.Value.Length.Value * rootFontSize,
                };

                if (!float.IsFinite(actual) || !float.IsFinite(threshold))
                {
                    return ContainerQueryTruth.Unknown;
                }

                var matches = feature.Value.Comparison switch
                {
                    ContainerQueryComparison.Min => actual >= threshold,
                    ContainerQueryComparison.Max => actual <= threshold,
                    ContainerQueryComparison.GreaterThan => actual > threshold,
                    ContainerQueryComparison.LessThan => actual < threshold,
                    _ => actual == threshold,
                };

                return matches ? ContainerQueryTruth.True : ContainerQueryTruth.False;
            }

            case ContainerQueryExpr.Unknown:
                return ContainerQueryTruth.Unknown;

            case ContainerQueryExpr.Not not:
                return Evaluate(not.Inner, container, rootFontSize).Not();

            case ContainerQueryExpr.And and:
            {
                var truth = ContainerQueryTruth.True;
                foreach (var item in and.Items)
                {
                    truth = truth.And(Evaluate(item, container, rootFontSize));
                    if (truth == ContainerQueryTruth.False)
                    {
                        break;
                    }
                }

                return truth;
            }

            case ContainerQueryExpr.Or or:
            {
                var truth = ContainerQueryTruth.False;
                foreach (var item in or.Items)
                {
                    truth = truth.Or(Evaluate(item, container, rootFontSize));
                    if (truth == ContainerQueryTruth.True)
                    {
                        break;
                    }
                }

                return truth;
            }

            default:
                return ContainerQueryTruth.Unknown;
        }
    }
}
