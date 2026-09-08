// CSS Grid track lists, implicit tracks, template/areas shorthands and grid
// placement from style.rs.
using System.Collections.Concurrent;
using Obscura.Render.Css;

namespace Obscura.Render;

/// <summary>
/// A grid track expression whose CSS math is resolved late, against the used
/// grid-axis basis rather than at computed-value time.
/// </summary>
/// <remarks>
/// Rust hands taffy the <c>Arc</c> pointer as an opaque <c>calc()</c> handle.
/// Managed code cannot hand out an address of a moving object, so the port
/// allocates a stable 8-byte-aligned handle and keeps a weak registry entry for
/// it; <see cref="LayoutStyle.GridCalcExpressions"/> owns the strong references
/// exactly as the Rust field does.
/// </remarks>
public sealed class GridCalcExpression
{
    private static readonly ConcurrentDictionary<nuint, WeakReference<GridCalcExpression>> Registry = new();
    private static nuint _nextHandle = 8;

    private string _expression = string.Empty;
    private float _emPx = 16f;
    private float _remPx = 16f;
    private float _vw;
    private float _vh;
    private bool _contextInitialized;

    private GridCalcExpression()
    {
    }

    /// <summary>The opaque handle taffy stores inside a <c>CompactLength</c>.</summary>
    public nuint Handle { get; private init; }

    /// <summary>The lowercased CSS math expression.</summary>
    public string Expression => _expression;

    /// <summary>Rust <c>GridCalcExpression::parse</c>.</summary>
    internal static GridCalcExpression? Parse(string value)
    {
        string lower = CssText.AsciiLower(value.Trim());
        if (!(lower.StartsWith("calc(", StringComparison.Ordinal)
            || lower.StartsWith("min(", StringComparison.Ordinal)
            || lower.StartsWith("max(", StringComparison.Ordinal)
            || lower.StartsWith("clamp(", StringComparison.Ordinal)
            || lower.StartsWith("round(", StringComparison.Ordinal)))
        {
            return null;
        }

        // Accept only supported length units, numbers, and math function names.
        foreach (string word in AlphabeticWords(lower))
        {
            if (!AllowedWords.Contains(word))
            {
                return null;
            }
        }

        // Probe more than one basis so malformed expressions and non-finite
        // arithmetic never become an opaque taffy handle.
        foreach (float basis in new[] { 0f, 100f })
        {
            if (ComputedStyle.ResolveContextualLength(lower, 16f, 16f, 10f, 10f, basis) is not { } resolved
                || !float.IsFinite(resolved))
            {
                return null;
            }
        }

        nuint handle;
        lock (Registry)
        {
            handle = _nextHandle;
            _nextHandle += 8;
        }

        GridCalcExpression expression = new() { Handle = handle, _expression = lower };
        Registry[handle] = new WeakReference<GridCalcExpression>(expression);
        if (Registry.Count > 4096)
        {
            PruneRegistry();
        }

        return expression;
    }

    private static void PruneRegistry()
    {
        foreach (KeyValuePair<nuint, WeakReference<GridCalcExpression>> entry in Registry)
        {
            if (!entry.Value.TryGetTarget(out _))
            {
                Registry.TryRemove(entry.Key, out _);
            }
        }
    }

    private static IEnumerable<string> AlphabeticWords(string value)
    {
        System.Text.StringBuilder builder = new();
        foreach (char character in value)
        {
            if (CssText.IsAsciiAlphabetic(character) || character == '-')
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length != 0)
            {
                string word = builder.ToString();
                builder.Clear();
                if (word != "-")
                {
                    yield return word;
                }
            }
        }

        if (builder.Length != 0)
        {
            string word = builder.ToString();
            if (word != "-")
            {
                yield return word;
            }
        }
    }

    private static readonly HashSet<string> AllowedWords = new(StringComparer.Ordinal)
    {
        "calc", "min", "max", "clamp", "round", "nearest", "up", "down", "to-zero",
        "px", "pt", "em", "rem", "ex", "vw", "vh", "dvw", "dvh", "svw", "svh", "lvw", "lvh",
        "vmin", "vmax",
    };

    /// <summary>Rust <c>GridCalcExpression::set_context</c>.</summary>
    internal void SetContext(float emPx, float remPx, float vw, float vh)
    {
        // Viewport units are used values and must follow every new layout viewport.
        _vw = vw;
        _vh = vh;
        // A cloned handle represents the same computed track value, so an explicit
        // `inherit` keeps the parent's em basis.
        if (_contextInitialized)
        {
            return;
        }

        _emPx = emPx;
        _remPx = remPx;
        _contextInitialized = true;
    }

    /// <summary>Resolve this expression against a grid-axis basis.</summary>
    internal float Resolve(float basis)
    {
        float? resolved = ComputedStyle.ResolveContextualLength(_expression, _emPx, _remPx, _vw, _vh, basis);
        float value = resolved is { } candidate && float.IsFinite(candidate) ? candidate : 0f;
        return F32.Max(value, 0f);
    }

    /// <summary>Look up a live expression by its taffy handle.</summary>
    internal static GridCalcExpression? FromHandle(nuint handle) =>
        Registry.TryGetValue(handle, out WeakReference<GridCalcExpression>? weak)
            && weak.TryGetTarget(out GridCalcExpression? expression)
            ? expression
            : null;
}

public static partial class ComputedStyle
{
    /// <summary>The four <c>grid_calc_expressions</c> buckets, allocated on first use.</summary>
    internal static List<object>[] GridCalcBuckets(LayoutStyle style) =>
        style.GridCalcExpressions ??= [[], [], [], []];

    /// <summary>Rust <c>set_grid_calc_context</c>.</summary>
    public static void SetGridCalcContext(LayoutStyle style, float emPx, float remPx, float vw, float vh)
    {
        if (style.GridCalcExpressions is not { } buckets)
        {
            return;
        }

        foreach (List<object> bucket in buckets)
        {
            foreach (object entry in bucket)
            {
                ((GridCalcExpression)entry).SetContext(emPx, remPx, vw, vh);
            }
        }
    }

    /// <summary>Rust <c>resolve_grid_calc</c>.</summary>
    public static float ResolveGridCalc(nuint handle, float basis) =>
        GridCalcExpression.FromHandle(handle) is { } expression ? expression.Resolve(basis) : 0f;

    /// <summary>Rust <c>tokenize_tracks</c>.</summary>
    internal static List<string> TokenizeTracks(string value)
    {
        List<string> output = [];
        System.Text.StringBuilder current = new();
        int depth = 0;
        bool inBracket = false;
        foreach (char character in value)
        {
            switch (character)
            {
                case '[':
                    inBracket = true;
                    current.Append(character);
                    break;
                case ']':
                    inBracket = false;
                    current.Append(character);
                    break;
                case '(':
                    depth++;
                    current.Append(character);
                    break;
                case ')':
                    depth--;
                    current.Append(character);
                    break;
                default:
                    if (char.IsWhiteSpace(character) && depth == 0 && !inBracket)
                    {
                        if (current.Length != 0)
                        {
                            output.Add(current.ToString());
                            current.Clear();
                        }
                    }
                    else
                    {
                        current.Append(character);
                    }

                    break;
            }
        }

        if (current.Length != 0)
        {
            output.Add(current.ToString());
        }

        return output;
    }

    /// <summary>Rust <c>build_line_map</c>: first occurrence of a name wins.</summary>
    internal static Dictionary<string, short> BuildLineMap(List<(string Name, short Line)> pairs)
    {
        Dictionary<string, short> map = new(StringComparer.Ordinal);
        foreach ((string name, short line) in pairs)
        {
            map.TryAdd(name, line);
        }

        return map;
    }

    /// <summary>Rust <c>parse_track_list_named</c>.</summary>
    internal static (List<Layout.GridTemplateComponent> Tracks,
        List<(string Name, short Line)> Names,
        List<object> CalcExpressions) ParseTrackListNamed(string value)
    {
        List<string> tokens = TokenizeTracks(value);
        List<Layout.GridTemplateComponent> tracks = [];
        List<(string Name, short Line)> names = [];
        List<object> calcExpressions = [];
        short line = 1;
        // A subgridded axis owns line names but no sizing functions.
        bool isSubgrid = tokens.Count > 0 && CssText.EqualsAscii(tokens[0], "subgrid");
        for (int index = isSubgrid ? 1 : 0; index < tokens.Count; index++)
        {
            ExpandTrackToken(tokens[index], tracks, names, calcExpressions, ref line);
        }

        return (tracks, names, calcExpressions);
    }

    private static void ExpandTrackToken(
        string token,
        List<Layout.GridTemplateComponent> tracks,
        List<(string Name, short Line)> names,
        List<object> calcExpressions,
        ref short line)
    {
        string trimmed = token.Trim();
        if (trimmed.StartsWith('['))
        {
            string inner = trimmed.TrimStart('[').TrimEnd(']');
            foreach (string name in SplitWhitespace(inner))
            {
                names.Add((name, line));
            }

            return;
        }

        string lower = CssText.AsciiLower(trimmed);
        if (lower.StartsWith("repeat(", StringComparison.Ordinal) && trimmed.EndsWith(')'))
        {
            string inner = trimmed["repeat(".Length..^1];
            int comma = inner.IndexOf(',');
            if (comma < 0)
            {
                return;
            }

            string countText = inner[..comma];
            List<string> subTokens = TokenizeTracks(inner[(comma + 1)..].Trim());
            Layout.RepetitionCount? repetition = CssText.AsciiLower(countText.Trim()) switch
            {
                "auto-fill" => Layout.RepetitionCount.AutoFill,
                "auto-fit" => Layout.RepetitionCount.AutoFit,
                _ => null,
            };
            if (repetition is { } count)
            {
                List<Layout.TrackSizingFunction> repeated = [];
                foreach (string subToken in subTokens)
                {
                    if (subToken.TrimStart().StartsWith('['))
                    {
                        continue;
                    }

                    repeated.Add(Track(subToken, calcExpressions));
                }

                tracks.Add(Layout.GridTemplateComponent.FromRepeat(new Layout.GridTemplateRepetition
                {
                    Count = count,
                    Tracks = repeated,
                    LineNames = [],
                }));
                line++;
                return;
            }

            int repeatCount = Math.Min(ParseUsize(countText.Trim()) ?? 1, 1000);
            for (int iteration = 0; iteration < repeatCount; iteration++)
            {
                foreach (string subToken in subTokens)
                {
                    ExpandTrackToken(subToken, tracks, names, calcExpressions, ref line);
                }
            }

            return;
        }

        tracks.Add(Layout.GridTemplateComponent.FromSingle(Track(trimmed, calcExpressions)));
        line++;
    }

    /// <summary>Rust <c>track</c>.</summary>
    internal static Layout.TrackSizingFunction Track(string token, List<object> calcExpressions)
    {
        string trimmed = token.Trim();
        string lower = CssText.AsciiLower(trimmed);
        if (lower.StartsWith("minmax(", StringComparison.Ordinal) && lower.EndsWith(')'))
        {
            string inner = lower["minmax(".Length..^1];
            List<string> arguments = SplitTopLevel(inner, ',');
            if (arguments.Count == 2)
            {
                return new Layout.TrackSizingFunction(
                    MinTrack(arguments[0].Trim(), calcExpressions),
                    MaxTrack(arguments[1].Trim(), calcExpressions));
            }
        }

        if (lower.StartsWith("fit-content(", StringComparison.Ordinal) && lower.EndsWith(')'))
        {
            string limit = lower["fit-content(".Length..^1].Trim();
            Layout.MaxTrackSizingFunction max;
            if (limit.EndsWith('%') && ParseF32(limit[..^1].Trim()) is { } percent)
            {
                max = Layout.MaxTrackSizingFunction.FromRaw(
                    Layout.CompactLength.FitContentPercent(percent / 100f));
            }
            else if (PxValue(limit) is { } pixels)
            {
                max = Layout.MaxTrackSizingFunction.FromRaw(Layout.CompactLength.FitContentPx(pixels));
            }
            else
            {
                // taffy has no opaque-calc form for fit-content's clamp semantics.
                max = Layout.MaxTrackSizingFunction.Auto;
            }

            return new Layout.TrackSizingFunction(Layout.MinTrackSizingFunction.Auto, max);
        }

        return new Layout.TrackSizingFunction(
            MinTrack(trimmed, calcExpressions),
            MaxTrack(trimmed, calcExpressions));
    }

    private static Layout.MinTrackSizingFunction MinTrack(string token, List<object> calcExpressions)
    {
        string lower = CssText.AsciiLower(token);
        switch (lower)
        {
            case "min-content":
                return Layout.MinTrackSizingFunction.MinContent;
            case "max-content":
                return Layout.MinTrackSizingFunction.MaxContent;
            case "auto":
                return Layout.MinTrackSizingFunction.Auto;
        }

        if (lower.EndsWith("fr", StringComparison.Ordinal))
        {
            // Flexible tracks have an automatic minimum.
            return Layout.MinTrackSizingFunction.Auto;
        }

        if (lower.EndsWith('%') && ParseF32(lower[..^1].Trim()) is { } percent)
        {
            return Layout.MinTrackSizingFunction.FromPercent(percent / 100f);
        }

        if (PxValue(lower) is { } pixels)
        {
            return Layout.MinTrackSizingFunction.FromLength(pixels);
        }

        if (GridCalcExpression.Parse(lower) is { } calc)
        {
            calcExpressions.Add(calc);
            return Layout.MinTrackSizingFunction.FromRaw(Layout.CompactLength.Calc(calc.Handle));
        }

        return Layout.MinTrackSizingFunction.Auto;
    }

    private static Layout.MaxTrackSizingFunction MaxTrack(string token, List<object> calcExpressions)
    {
        string lower = CssText.AsciiLower(token);
        switch (lower)
        {
            case "min-content":
                return Layout.MaxTrackSizingFunction.MinContent;
            case "max-content":
                return Layout.MaxTrackSizingFunction.MaxContent;
            case "auto":
                return Layout.MaxTrackSizingFunction.Auto;
        }

        if (lower.EndsWith("fr", StringComparison.Ordinal) && ParseF32(lower[..^2].Trim()) is { } fraction)
        {
            return Layout.MaxTrackSizingFunction.FromFr(fraction);
        }

        if (lower.EndsWith('%') && ParseF32(lower[..^1].Trim()) is { } percent)
        {
            return Layout.MaxTrackSizingFunction.FromPercent(percent / 100f);
        }

        if (PxValue(lower) is { } pixels)
        {
            return Layout.MaxTrackSizingFunction.FromLength(pixels);
        }

        if (GridCalcExpression.Parse(lower) is { } calc)
        {
            calcExpressions.Add(calc);
            return Layout.MaxTrackSizingFunction.FromRaw(Layout.CompactLength.Calc(calc.Handle));
        }

        return Layout.MaxTrackSizingFunction.Auto;
    }

    /// <summary>Rust <c>parse_grid_auto_track_list</c>.</summary>
    internal static (List<Layout.TrackSizingFunction> Tracks, List<object> CalcExpressions)?
        ParseGridAutoTrackList(string value)
    {
        List<string> tokens = TokenizeTracks(value);
        if (tokens.Count == 0)
        {
            return null;
        }

        foreach (string token in tokens)
        {
            string lower = CssText.AsciiLower(token.Trim());
            if (lower.StartsWith('[')
                || lower.StartsWith("repeat(", StringComparison.Ordinal)
                || lower == "subgrid"
                || lower is "initial" or "inherit" or "unset" or "revert" or "revert-layer"
                || !ValidGridAutoTrack(lower))
            {
                return null;
            }
        }

        List<object> calcExpressions = [];
        List<Layout.TrackSizingFunction> tracks = [];
        foreach (string token in tokens)
        {
            tracks.Add(Track(token, calcExpressions));
        }

        return (tracks, calcExpressions);
    }

    /// <summary>Rust <c>valid_grid_auto_track</c>.</summary>
    internal static bool ValidGridAutoTrack(string value)
    {
        if (value is "auto" or "min-content" or "max-content")
        {
            return true;
        }

        if (value.StartsWith("minmax(", StringComparison.Ordinal) && value.EndsWith(')'))
        {
            string inner = value["minmax(".Length..^1];
            int comma = inner.IndexOf(',');
            if (comma < 0)
            {
                return false;
            }

            return ValidGridTrackBreadth(inner[..comma].Trim(), false)
                && ValidGridTrackBreadth(inner[(comma + 1)..].Trim(), true);
        }

        if (value.StartsWith("fit-content(", StringComparison.Ordinal) && value.EndsWith(')'))
        {
            return ValidGridTrackLengthPercentage(value["fit-content(".Length..^1].Trim());
        }

        return ValidGridTrackBreadth(value, true);
    }

    /// <summary>Rust <c>valid_grid_track_breadth</c>.</summary>
    internal static bool ValidGridTrackBreadth(string value, bool flexAllowed)
    {
        if (value is "auto" or "min-content" or "max-content")
        {
            return true;
        }

        if (flexAllowed
            && value.EndsWith("fr", StringComparison.Ordinal)
            && ParseF32(value[..^2].Trim()) is { } fraction
            && float.IsFinite(fraction)
            && fraction >= 0f)
        {
            return true;
        }

        return ValidGridTrackLengthPercentage(value);
    }

    /// <summary>Rust <c>valid_grid_track_length_percentage</c>.</summary>
    internal static bool ValidGridTrackLengthPercentage(string value)
    {
        string lower = CssText.AsciiLower(value);
        if (GridCalcExpression.Parse(lower) is not null)
        {
            return true;
        }

        if (lower == "0")
        {
            return true;
        }

        foreach (string suffix in GridTrackUnits)
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
            {
                return ParseF32(lower[..^suffix.Length].Trim()) is { } number
                    && float.IsFinite(number)
                    && number >= 0f;
            }
        }

        return false;
    }

    private static readonly string[] GridTrackUnits =
        ["rem", "vmin", "vmax", "px", "pt", "em", "ex", "vw", "vh", "%"];

    /// <summary>Rust <c>apply_grid_auto_tracks</c>.</summary>
    internal static void ApplyGridAutoTracks(LayoutStyle style, string value, bool columns)
    {
        string lower = CssText.AsciiLower(value.Trim());
        List<Layout.TrackSizingFunction> tracks;
        bool inherit;
        List<object> calcExpressions;
        switch (lower)
        {
            case "inherit":
                tracks = [];
                inherit = true;
                calcExpressions = [];
                break;
            case "initial":
            case "unset":
            case "revert":
            case "revert-layer":
                tracks = [];
                inherit = false;
                calcExpressions = [];
                break;
            default:
                if (ParseGridAutoTrackList(value) is not { } parsed)
                {
                    return;
                }

                tracks = parsed.Tracks;
                inherit = false;
                calcExpressions = parsed.CalcExpressions;
                break;
        }

        if (columns)
        {
            style.GridAutoColumns = tracks;
            GridCalcBuckets(style)[2] = calcExpressions;
            style.GridAutoColumnsInherit = inherit;
        }
        else
        {
            style.GridAutoRows = tracks;
            GridCalcBuckets(style)[3] = calcExpressions;
            style.GridAutoRowsInherit = inherit;
        }
    }

    /// <summary>Rust <c>parse_grid_areas</c>.</summary>
    internal static List<List<string>> ParseGridAreas(string value)
    {
        List<List<string>> rows = [];
        bool inString = false;
        System.Text.StringBuilder current = new();
        foreach (char character in value)
        {
            if (character is '\'' or '"')
            {
                if (inString)
                {
                    rows.Add(SplitWhitespace(current.ToString()));
                    current.Clear();
                    inString = false;
                }
                else
                {
                    inString = true;
                }
            }
            else if (inString)
            {
                current.Append(character);
            }
        }

        return rows;
    }

    /// <summary>Rust <c>parse_grid_template</c>.</summary>
    internal static void ParseGridTemplate(LayoutStyle style, string value)
    {
        int slash = value.IndexOf('/');
        string rowsPart = slash >= 0 ? value[..slash].Trim() : value.Trim();
        string? columnsPart = slash >= 0 ? value[(slash + 1)..].Trim() : null;

        if (rowsPart.Contains('\'') || rowsPart.Contains('"'))
        {
            style.GridAreas = ParseGridAreas(rowsPart);
        }
        else if (rowsPart.Length != 0)
        {
            (List<Layout.GridTemplateComponent> tracks,
                List<(string Name, short Line)> names,
                List<object> calcExpressions) = ParseTrackListNamed(rowsPart);
            style.GridTemplateRows = tracks;
            GridCalcBuckets(style)[1] = calcExpressions;
            style.GridRowLineNames = names.Count != 0 ? BuildLineMap(names) : null;
        }

        if (columnsPart is { } columns)
        {
            (List<Layout.GridTemplateComponent> tracks,
                List<(string Name, short Line)> names,
                List<object> calcExpressions) = ParseTrackListNamed(columns);
            style.GridTemplateColumnsSubgrid = IsSubgridTrackList(columns);
            style.GridTemplateColumns = tracks;
            GridCalcBuckets(style)[0] = calcExpressions;
            style.GridColLineNames = names.Count != 0 ? BuildLineMap(names) : null;
        }
    }

    /// <summary>Rust <c>parse_grid_shorthand</c>.</summary>
    internal static void ParseGridShorthand(LayoutStyle style, string value)
    {
        int slash = value.IndexOf('/');
        if (slash < 0)
        {
            ParseGridTemplate(style, value);
            return;
        }

        string rows = value[..slash].Trim();
        string columns = value[(slash + 1)..].Trim();
        if (CssText.AsciiLower(rows).Contains("auto-flow", StringComparison.Ordinal))
        {
            style.GridTemplateRows.Clear();
            GridCalcBuckets(style)[1].Clear();
            (List<Layout.GridTemplateComponent> tracks,
                List<(string Name, short Line)> names,
                List<object> calcExpressions) = ParseTrackListNamed(columns);
            style.GridTemplateColumnsSubgrid = IsSubgridTrackList(columns);
            style.GridTemplateColumns = tracks;
            GridCalcBuckets(style)[0] = calcExpressions;
            style.GridColLineNames = names.Count != 0 ? BuildLineMap(names) : null;
            style.GridAutoFlow = CssText.AsciiLower(rows).Contains("dense", StringComparison.Ordinal)
                ? Layout.GridAutoFlow.RowDense
                : Layout.GridAutoFlow.Row;
        }
        else if (CssText.AsciiLower(columns).Contains("auto-flow", StringComparison.Ordinal))
        {
            style.GridTemplateColumns.Clear();
            GridCalcBuckets(style)[0].Clear();
            style.GridTemplateColumnsSubgrid = false;
            (List<Layout.GridTemplateComponent> tracks,
                List<(string Name, short Line)> names,
                List<object> calcExpressions) = ParseTrackListNamed(rows);
            style.GridTemplateRows = tracks;
            GridCalcBuckets(style)[1] = calcExpressions;
            style.GridRowLineNames = names.Count != 0 ? BuildLineMap(names) : null;
            style.GridAutoFlow = CssText.AsciiLower(columns).Contains("dense", StringComparison.Ordinal)
                ? Layout.GridAutoFlow.ColumnDense
                : Layout.GridAutoFlow.Column;
        }
        else
        {
            ParseGridTemplate(style, value);
        }
    }

    /// <summary>Rust <c>is_subgrid_track_list</c>.</summary>
    internal static bool IsSubgridTrackList(string value)
    {
        List<string> tokens = TokenizeTracks(value);
        return tokens.Count > 0 && CssText.EqualsAscii(tokens[0], "subgrid");
    }

    /// <summary>Rust <c>parse_grid_auto_flow</c>.</summary>
    internal static Layout.GridAutoFlow ParseGridAutoFlow(string value)
    {
        string lower = CssText.AsciiLower(value.Trim());
        List<string> tokens = SplitWhitespace(lower);
        bool dense = tokens.Contains("dense");
        bool column = tokens.Contains("column");
        return (column, dense) switch
        {
            (false, false) => Layout.GridAutoFlow.Row,
            (false, true) => Layout.GridAutoFlow.RowDense,
            (true, false) => Layout.GridAutoFlow.Column,
            _ => Layout.GridAutoFlow.ColumnDense,
        };
    }

    /// <summary>Rust <c>set_grid_area</c>.</summary>
    internal static void SetGridArea(LayoutStyle style, string value)
    {
        if (CssText.AsciiLower(value.Trim()) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
        {
            style.GridAreaName = null;
            SetGridPlacement(style, "auto / auto", true);
            SetGridPlacement(style, "auto / auto", false);
            return;
        }

        List<string> parts = [];
        foreach (string part in SplitTopLevel(value, '/'))
        {
            parts.Add(part.Trim());
        }

        if (parts.Count == 0 || parts.Count > 4)
        {
            return;
        }

        foreach (string part in parts)
        {
            if (part.Length == 0 || ParseGridLineKind(part) is null)
            {
                return;
            }
        }

        string rowStart = parts[0];
        string columnStart = parts.Count > 1 ? parts[1] : GridAreaOmittedSide(rowStart);
        string rowEnd = parts.Count > 2 ? parts[2] : GridAreaOmittedSide(rowStart);
        string columnEnd = parts.Count > 3 ? parts[3] : GridAreaOmittedSide(columnStart);

        style.GridAreaName = parts.Count == 1 && IsGridCustomIdent(rowStart) ? rowStart : null;

        SetGridPlacement(style, $"{columnStart} / {columnEnd}", true);
        SetGridPlacement(style, $"{rowStart} / {rowEnd}", false);
    }

    private static string GridAreaOmittedSide(string start) => IsGridCustomIdent(start) ? start : "auto";

    private static bool IsGridCustomIdent(string value) => ParseGridLineKind(value) == GridLineKind.IdentOnly;

    /// <summary>Rust <c>set_grid_placement</c>.</summary>
    internal static void SetGridPlacement(LayoutStyle style, string value, bool isColumn)
    {
        if (GridLineHasName(value))
        {
            string raw = value.Trim();
            if (isColumn)
            {
                style.GridColumnRaw = raw;
                style.GridColumn = null;
            }
            else
            {
                style.GridRowRaw = raw;
                style.GridRow = null;
            }

            return;
        }

        Layout.Line<Layout.GridPlacement>? line = ParseGridLine(value);
        if (isColumn)
        {
            style.GridColumn = line;
            style.GridColumnRaw = null;
        }
        else
        {
            style.GridRow = line;
            style.GridRowRaw = null;
        }
    }

    /// <summary>Rust <c>set_grid_placement_side</c>.</summary>
    internal static void SetGridPlacementSide(LayoutStyle style, string value, bool isColumn, bool isStart)
    {
        if (GridLineHasName(value))
        {
            string? rawSlot = isColumn ? style.GridColumnRaw : style.GridRowRaw;
            string start;
            string end;
            if (rawSlot is { } raw && raw.Contains('/'))
            {
                int slash = raw.IndexOf('/');
                start = raw[..slash].Trim();
                end = raw[(slash + 1)..].Trim();
            }
            else if (rawSlot is { } single)
            {
                start = single.Trim();
                end = single.Trim();
            }
            else
            {
                start = "auto";
                end = "auto";
            }

            if (isStart)
            {
                start = value.Trim();
            }
            else
            {
                end = value.Trim();
            }

            if (isColumn)
            {
                style.GridColumnRaw = $"{start} / {end}";
                style.GridColumn = null;
            }
            else
            {
                style.GridRowRaw = $"{start} / {end}";
                style.GridRow = null;
            }

            return;
        }

        Layout.GridPlacement placement = ParseGridPlacement(value);
        Layout.Line<Layout.GridPlacement> line;
        if (isColumn)
        {
            style.GridColumnRaw = null;
            line = style.GridColumn ?? new Layout.Line<Layout.GridPlacement>(
                Layout.GridPlacement.Auto,
                Layout.GridPlacement.Auto);
        }
        else
        {
            style.GridRowRaw = null;
            line = style.GridRow ?? new Layout.Line<Layout.GridPlacement>(
                Layout.GridPlacement.Auto,
                Layout.GridPlacement.Auto);
        }

        if (isStart)
        {
            line.Start = placement;
        }
        else
        {
            line.End = placement;
        }

        if (isColumn)
        {
            style.GridColumn = line;
        }
        else
        {
            style.GridRow = line;
        }
    }

    /// <summary>Rust <c>grid_line_has_name</c>.</summary>
    internal static bool GridLineHasName(string value)
    {
        string trimmed = value.Trim();
        if (CssText.EqualsAscii(trimmed, "auto")
            || CssText.AsciiLower(trimmed) is "initial" or "inherit" or "unset" or "revert" or "revert-layer")
        {
            return false;
        }

        foreach (string part in trimmed.Split('/'))
        {
            string candidate = part.Trim();
            if (CssText.EqualsAscii(candidate, "auto"))
            {
                continue;
            }

            string lower = CssText.AsciiLower(candidate);
            string rest = lower.StartsWith("span", StringComparison.Ordinal) ? lower[4..].Trim() : lower;
            foreach (char character in rest)
            {
                if (CssText.IsAsciiAlphabetic(character))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Rust <c>parse_grid_line</c>.</summary>
    internal static Layout.Line<Layout.GridPlacement> ParseGridLine(string value)
    {
        int slash = value.IndexOf('/');
        string startText = slash >= 0 ? value[..slash] : value;
        Layout.GridPlacement start = ParseGridPlacement(startText);
        Layout.GridPlacement end = slash >= 0
            ? ParseGridPlacement(value[(slash + 1)..])
            : Layout.GridPlacement.Auto;
        return new Layout.Line<Layout.GridPlacement>(start, end);
    }

    /// <summary>Rust <c>parse_grid_placement</c>.</summary>
    internal static Layout.GridPlacement ParseGridPlacement(string value)
    {
        string trimmed = value.Trim();
        string lower = CssText.AsciiLower(trimmed);
        if (lower.StartsWith("span", StringComparison.Ordinal)
            && ParseU16(lower[4..].Trim()) is { } span)
        {
            return Layout.GridPlacement.FromSpan(span);
        }

        if (ParseI16(trimmed) is { } line)
        {
            return Layout.GridPlacement.FromLineIndex(line);
        }

        return Layout.GridPlacement.Auto;
    }
}
