// CSS Grid track lists, implicit tracks, template/areas shorthands and grid
// placement from style.rs.
using System.Collections.Concurrent;
using PocketCalculator.Render.Css;

namespace PocketCalculator.Render;

/// <summary>
/// A CSS math expression whose percentage is resolved late, against the used basis taffy
/// has during layout rather than against an estimate at computed-value time. Named for its
/// first use, grid tracks; box offsets and inline-axis sizes now take the same route.
/// </summary>
/// <remarks>
/// Rust hands taffy the <c>Arc</c> pointer as an opaque <c>calc()</c> handle.
/// Managed code cannot hand out an address of a moving object, so the port
/// allocates a stable 8-byte-aligned handle and keeps a weak registry entry for
/// it; <see cref="LayoutStyle.GridCalcExpressions"/>, <see cref="LayoutStyle.InsetCalc"/>
/// and <see cref="LayoutStyle.SizeCalc"/> own the strong references exactly as the Rust
/// field does.
/// </remarks>
public sealed class GridCalcExpression
{
    private static readonly ConcurrentDictionary<nuint, WeakReference<GridCalcExpression>> Registry = new();
    private static nuint _nextHandle = 8;

    private string _expression = string.Empty;
    private FontUnits _font = FontUnits.FromEm(16f);
    private float _remPx = 16f;
    private float _vw;
    private float _vh;
    private bool _contextInitialized;
    private bool _allowNegative;

    private GridCalcExpression()
    {
    }

    /// <summary>The opaque handle taffy stores inside a <c>CompactLength</c>.</summary>
    public nuint Handle { get; private init; }

    /// <summary>The lowercased CSS math expression.</summary>
    public string Expression => _expression;

    /// <summary>Rust <c>GridCalcExpression::parse</c>.</summary>
    /// <param name="value">The CSS math expression.</param>
    /// <param name="allowNegative">
    /// Whether a resolved value below zero is kept. A grid track size is clamped at zero;
    /// a box offset or margin is not, so the inset path passes <c>true</c>.
    /// </param>
    internal static GridCalcExpression? Parse(string value, bool allowNegative = false)
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

        GridCalcExpression expression = new()
        {
            Handle = handle,
            _expression = lower,
            _allowNegative = allowNegative,
        };
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
    internal void SetContext(FontUnits font, float remPx, float vw, float vh)
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

        _font = font;
        _remPx = remPx;
        _contextInitialized = true;
    }

    /// <summary>Resolve this expression against a grid-axis basis.</summary>
    internal float Resolve(float basis)
    {
        float? resolved = ComputedStyle.ResolveContextualLength(_expression, _font, _remPx, _vw, _vh, basis);
        float value = resolved is { } candidate && float.IsFinite(candidate) ? candidate : 0f;
        return _allowNegative ? value : F32.Max(value, 0f);
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
    public static void SetGridCalcContext(LayoutStyle style, FontUnits font, float remPx, float vw, float vh)
    {
        // A percentage-dependent `flex-basis` is the same kind of late-resolved expression and
        // reaches taffy through the same handle, so it takes its context from here too.
        style.FlexBasisCalc?.SetContext(font, remPx, vw, vh);

        if (style.GridCalcExpressions is not { } buckets)
        {
            return;
        }

        foreach (List<object> bucket in buckets)
        {
            foreach (object entry in bucket)
            {
                ((GridCalcExpression)entry).SetContext(font, remPx, vw, vh);
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
    /// <remarks>An invalid list (see <see cref="TryParseTrackListNamed"/>) yields no tracks.</remarks>
    internal static (List<Layout.GridTemplateComponent> Tracks,
        List<(string Name, short Line)> Names,
        List<object> CalcExpressions) ParseTrackListNamed(string value) =>
        TryParseTrackListNamed(value, out var tracks, out var names, out var calcExpressions)
            ? (tracks, names, calcExpressions)
            : ([], [], []);

    /// <summary>
    /// Rust <c>parse_track_list_named</c>, reporting whether the declaration is valid.
    /// </summary>
    /// <remarks>
    /// Deviation from Rust, which accepts <c>repeat()</c> inside <c>repeat()</c> and expands it,
    /// capping each level at 1000 repetitions: nesting three levels deep already makes 10^9
    /// tracks, and twenty levels of <c>repeat(2, ...)</c> took seconds. css-grid's
    /// <c>&lt;track-repeat&gt;</c> admits only line names and track sizes, so Chromium drops the
    /// whole declaration, and so does the port (<c>false</c>). The same holds for a repeat
    /// count of zero. A valid list is expanded up to <see cref="Layout.GridLimits.MaxTracks"/>
    /// tracks and then truncated, as Chromium truncates at <c>kGridMaxTracks</c>, instead of
    /// Rust's per-<c>repeat()</c> cap of 1000.
    /// </remarks>
    internal static bool TryParseTrackListNamed(
        string value,
        out List<Layout.GridTemplateComponent> tracks,
        out List<(string Name, short Line)> names,
        out List<object> calcExpressions)
    {
        List<string> tokens = TokenizeTracks(value);
        tracks = [];
        names = [];
        calcExpressions = [];
        short line = 1;
        // A subgridded axis owns line names but no sizing functions.
        bool isSubgrid = tokens.Count > 0 && CssText.EqualsAscii(tokens[0], "subgrid");
        for (int index = isSubgrid ? 1 : 0; index < tokens.Count; index++)
        {
            if (!ExpandTrackToken(tokens[index], tracks, names, calcExpressions, ref line, nested: false))
            {
                tracks = [];
                names = [];
                calcExpressions = [];
                return false;
            }
        }

        return true;
    }

    private static bool IsRepeatToken(string token) =>
        token.TrimStart().StartsWith("repeat(", StringComparison.OrdinalIgnoreCase);

    private static bool ExpandTrackToken(
        string token,
        List<Layout.GridTemplateComponent> tracks,
        List<(string Name, short Line)> names,
        List<object> calcExpressions,
        ref short line,
        bool nested)
    {
        const int maxTracks = Layout.GridLimits.MaxTracks;
        string trimmed = token.Trim();
        if (trimmed.StartsWith('['))
        {
            // Line names past the track limit name lines that do not exist.
            if (names.Count >= maxTracks)
            {
                return true;
            }

            string inner = trimmed.TrimStart('[').TrimEnd(']');
            foreach (string name in SplitWhitespace(inner))
            {
                names.Add((name, line));
            }

            return true;
        }

        string lower = CssText.AsciiLower(trimmed);
        if (lower.StartsWith("repeat(", StringComparison.Ordinal) && trimmed.EndsWith(')'))
        {
            if (nested)
            {
                return false;
            }

            string inner = trimmed["repeat(".Length..^1];
            int comma = inner.IndexOf(',');
            if (comma < 0)
            {
                return true;
            }

            string countText = inner[..comma];
            List<string> subTokens = TokenizeTracks(inner[(comma + 1)..].Trim());
            foreach (string subToken in subTokens)
            {
                if (IsRepeatToken(subToken))
                {
                    return false;
                }
            }

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
                return true;
            }

            // A count that is not a plain integer (calc(), say) keeps Rust's fallback of one
            // repetition; an integer past the limit saturates rather than failing to parse.
            int repeatCount = ParseIntegerClamped(countText.Trim(), maxTracks) is { } parsedCount
                ? (int)parsedCount
                : 1;
            if (repeatCount <= 0)
            {
                return false;
            }

            for (int iteration = 0; iteration < repeatCount; iteration++)
            {
                if (tracks.Count >= maxTracks || names.Count >= maxTracks)
                {
                    break;
                }

                foreach (string subToken in subTokens)
                {
                    if (!ExpandTrackToken(subToken, tracks, names, calcExpressions, ref line, nested: true))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        if (tracks.Count < maxTracks)
        {
            tracks.Add(Layout.GridTemplateComponent.FromSingle(Track(trimmed, calcExpressions)));
            line++;
        }

        return true;
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
    /// <remarks>
    /// Both track lists are parsed before either is applied, so an invalid one (nested
    /// <c>repeat()</c>) drops the whole shorthand, as it does in Chromium.
    /// </remarks>
    internal static void ParseGridTemplate(LayoutStyle style, string value)
    {
        int slash = value.IndexOf('/');
        string rowsPart = slash >= 0 ? value[..slash].Trim() : value.Trim();
        string? columnsPart = slash >= 0 ? value[(slash + 1)..].Trim() : null;

        bool rowsAreAreas = rowsPart.Contains('\'') || rowsPart.Contains('"');
        List<Layout.GridTemplateComponent> rowTracks = [];
        List<(string Name, short Line)> rowNames = [];
        List<object> rowCalc = [];
        if (!rowsAreAreas
            && rowsPart.Length != 0
            && !TryParseTrackListNamed(rowsPart, out rowTracks, out rowNames, out rowCalc))
        {
            return;
        }

        List<Layout.GridTemplateComponent> columnTracks = [];
        List<(string Name, short Line)> columnNames = [];
        List<object> columnCalc = [];
        if (columnsPart is { } columnsText
            && !TryParseTrackListNamed(columnsText, out columnTracks, out columnNames, out columnCalc))
        {
            return;
        }

        if (rowsAreAreas)
        {
            style.GridAreas = ParseGridAreas(rowsPart);
        }
        else if (rowsPart.Length != 0)
        {
            style.GridTemplateRows = rowTracks;
            style.GridTemplateRowsText = rowsPart;
            GridCalcBuckets(style)[1] = rowCalc;
            style.GridRowLineNames = rowNames.Count != 0 ? BuildLineMap(rowNames) : null;
        }

        if (columnsPart is { } columns)
        {
            style.GridTemplateColumnsSubgrid = IsSubgridTrackList(columns);
            style.GridTemplateColumns = columnTracks;
            style.GridTemplateColumnsText = columns;
            GridCalcBuckets(style)[0] = columnCalc;
            style.GridColLineNames = columnNames.Count != 0 ? BuildLineMap(columnNames) : null;
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
            if (!TryParseTrackListNamed(columns, out var tracks, out var names, out var calcExpressions))
            {
                return;
            }

            style.ClearGridTemplateRows();
            style.GridTemplateRowsText = null;
            GridCalcBuckets(style)[1].Clear();
            style.GridTemplateColumnsSubgrid = IsSubgridTrackList(columns);
            style.GridTemplateColumns = tracks;
            style.GridTemplateColumnsText = columns;
            GridCalcBuckets(style)[0] = calcExpressions;
            style.GridColLineNames = names.Count != 0 ? BuildLineMap(names) : null;
            style.GridAutoFlow = CssText.AsciiLower(rows).Contains("dense", StringComparison.Ordinal)
                ? Layout.GridAutoFlow.RowDense
                : Layout.GridAutoFlow.Row;
        }
        else if (CssText.AsciiLower(columns).Contains("auto-flow", StringComparison.Ordinal))
        {
            if (!TryParseTrackListNamed(rows, out var tracks, out var names, out var calcExpressions))
            {
                return;
            }

            style.ClearGridTemplateColumns();
            style.GridTemplateColumnsText = null;
            GridCalcBuckets(style)[0].Clear();
            style.GridTemplateColumnsSubgrid = false;
            style.GridTemplateRows = tracks;
            style.GridTemplateRowsText = rows;
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
            else if ((isColumn ? style.GridColumn : style.GridRow) is { } numeric)
            {
                // The other side was set without a name (grid-row-start: 2 before
                // grid-row-end: foo); keep it rather than resetting it to auto. Rust drops it.
                start = PlacementText(numeric.Start);
                end = PlacementText(numeric.End);
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

    /// <summary>A numeric or span placement as <c>&lt;grid-line&gt;</c> text.</summary>
    private static string PlacementText(Layout.GridPlacement placement) => placement.Kind switch
    {
        Layout.GridPlacementKind.Line when placement.LineIndex != 0 =>
            placement.LineIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Layout.GridPlacementKind.Span =>
            "span " + placement.SpanCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "auto",
    };

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
    /// <remarks>
    /// Deviation from Rust, which parses the integer as <c>i16</c>/<c>u16</c> and turns anything
    /// larger into <c>auto</c>: Chromium keeps the declaration and clamps the integer to
    /// <c>kGridMaxTracks</c>, so <c>grid-column: 1 / 99999999</c> spans the whole grid rather
    /// than being auto-placed. The port clamps to <see cref="Layout.GridLimits.MaxTracks"/>.
    /// </remarks>
    internal static Layout.GridPlacement ParseGridPlacement(string value)
    {
        const int maxTracks = Layout.GridLimits.MaxTracks;
        string trimmed = value.Trim();
        string lower = CssText.AsciiLower(trimmed);
        if (lower.StartsWith("span", StringComparison.Ordinal)
            && ParseIntegerClamped(lower[4..].Trim(), maxTracks) is { } span
            && span >= 0)
        {
            return Layout.GridPlacement.FromSpan((ushort)span);
        }

        if (ParseIntegerClamped(trimmed, maxTracks) is { } line)
        {
            return Layout.GridPlacement.FromLineIndex((short)line);
        }

        return Layout.GridPlacement.Auto;
    }
}
