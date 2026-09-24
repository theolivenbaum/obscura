// getComputedStyle() values of the CSS Grid properties. crates/obscura-render reports none of
// them (the snapshot had only grid-auto-flow), so page script read an inline declaration or
// the empty string. Serialized as Chromium 141 does; see GridComputedStyleTests.
using System.Text;
using PocketCalculator.Render.Css;
using PocketCalculator.Render.Layout;

namespace PocketCalculator.Render;

internal static class GridCssValues
{
    /// <summary>
    /// The resolved value of <c>grid-template-columns</c>/<c>-rows</c> on a grid container:
    /// every track's used size in px, implicit tracks included, with the explicit grid's line
    /// names in front of the lines they name.
    /// </summary>
    public static string UsedTrackList(
        float[] sizes, int negativeImplicit, int explicitCount, string? text, Dictionary<string, short>? lineNames)
    {
        if (sizes.Length == 0)
        {
            return "none";
        }

        // Names per explicit line (index 0 is line 1): from the declared list, which knows a
        // name repeated by repeat(); otherwise from the name-to-line map, which keeps one line
        // per name.
        List<List<string>>? byLine = text is not null ? LineNamesFromText(text, explicitCount) : null;
        if (byLine is null && lineNames is { Count: > 0 })
        {
            byLine = [];
            foreach ((string name, short line) in lineNames)
            {
                while (byLine.Count < line)
                {
                    byLine.Add([]);
                }

                byLine[line - 1].Add(name);
            }
        }

        StringBuilder sb = new();
        for (int i = 0; i <= sizes.Length; i++)
        {
            // Track i starts at explicit line i - negativeImplicit + 1.
            int line = i - negativeImplicit;
            if (byLine is not null && line >= 0 && line < byLine.Count && byLine[line].Count != 0)
            {
                Separate(sb).Append('[').AppendJoin(' ', byLine[line]).Append(']');
            }

            if (i < sizes.Length)
            {
                Separate(sb).Append(PaintCssValues.CssPx(sizes[i]));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The line names of a declared track list, per explicit line, with fixed and automatic
    /// repetitions expanded (an automatic one as often as the explicit grid has room for), or
    /// null when it names no line.
    /// </summary>
    private static List<List<string>>? LineNamesFromText(string text, int explicitCount)
    {
        if (!text.Contains('['))
        {
            return null;
        }

        List<string> tokens = ComputedStyle.TokenizeTracks(text);
        int fixedTracks = 0;
        int autoTracks = 0;
        foreach (string token in tokens)
        {
            if (RepeatParts(token) is { } repeat)
            {
                int tracks = 0;
                foreach (string sub in repeat.Tokens)
                {
                    tracks += IsTrackToken(sub) ? 1 : 0;
                }

                if (repeat.Count is { } count)
                {
                    fixedTracks += count * tracks;
                }
                else
                {
                    autoTracks = tracks;
                }
            }
            else if (IsTrackToken(token))
            {
                fixedTracks++;
            }
        }

        int autoRepetitions = autoTracks > 0 ? Math.Max(explicitCount - fixedTracks, 0) / autoTracks : 0;
        List<List<string>> lines = [[]];
        foreach (string token in tokens)
        {
            if (RepeatParts(token) is { } repeat)
            {
                int count = repeat.Count ?? autoRepetitions;
                for (int i = 0; i < count && lines.Count <= explicitCount + 1; i++)
                {
                    foreach (string sub in repeat.Tokens)
                    {
                        AddLineToken(lines, sub);
                    }
                }
            }
            else
            {
                AddLineToken(lines, token);
            }
        }

        return lines;
    }

    private static void AddLineToken(List<List<string>> lines, string token)
    {
        string trimmed = token.Trim();
        if (trimmed.StartsWith('['))
        {
            lines[^1].AddRange(trimmed.TrimStart('[').TrimEnd(']').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        else if (IsTrackToken(trimmed))
        {
            lines.Add([]);
        }
    }

    private static bool IsTrackToken(string token)
    {
        string trimmed = token.Trim();
        return trimmed.Length != 0 && !trimmed.StartsWith('[') && !CssText.EqualsAscii(trimmed, "subgrid");
    }

    /// <summary>The count (null for auto-fill/auto-fit) and inner tokens of a <c>repeat()</c>.</summary>
    private static (int? Count, List<string> Tokens)? RepeatParts(string token)
    {
        string trimmed = token.Trim();
        if (!trimmed.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(')'))
        {
            return null;
        }

        string inner = trimmed["repeat(".Length..^1];
        int comma = inner.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        int? count = int.TryParse(inner[..comma].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? Math.Min(parsed, GridLimits.MaxTracks)
            : null;
        return (count, ComputedStyle.TokenizeTracks(inner[(comma + 1)..].Trim()));
    }

    /// <summary>
    /// The computed value of <c>grid-template-columns</c>/<c>-rows</c> on a box that is not a
    /// grid container: the declared list with lengths made absolute and <c>repeat()</c> kept.
    /// </summary>
    public static string SpecifiedTrackList(string? text, IReadOnlyList<GridTemplateComponent> tracks)
    {
        if (text is null)
        {
            return tracks.Count == 0 ? "none" : ExpandedTrackList(tracks);
        }

        string lower = CssText.AsciiLower(text.Trim());
        if (lower.Length == 0 || lower is "none" or "initial" or "unset" or "revert" or "revert-layer" or "inherit")
        {
            return "none";
        }

        StringBuilder sb = new();
        List<object> calcScratch = [];
        foreach (string token in ComputedStyle.TokenizeTracks(text))
        {
            AppendSpecifiedToken(sb, token.Trim(), calcScratch);
        }

        return sb.Length == 0 ? "none" : sb.ToString();
    }

    private static void AppendSpecifiedToken(StringBuilder sb, string token, List<object> calcScratch)
    {
        if (token.Length == 0)
        {
            return;
        }

        if (token.StartsWith('['))
        {
            string inner = token.TrimStart('[').TrimEnd(']');
            Separate(sb).Append('[').AppendJoin(' ', inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Append(']');
            return;
        }

        string lower = CssText.AsciiLower(token);
        if (lower == "subgrid")
        {
            Separate(sb).Append("subgrid");
            return;
        }

        if (lower.StartsWith("repeat(", StringComparison.Ordinal) && token.EndsWith(')'))
        {
            string inner = token["repeat(".Length..^1];
            int comma = inner.IndexOf(',');
            if (comma < 0)
            {
                return;
            }

            StringBuilder repeated = new();
            foreach (string sub in ComputedStyle.TokenizeTracks(inner[(comma + 1)..].Trim()))
            {
                AppendSpecifiedToken(repeated, sub.Trim(), calcScratch);
            }

            Separate(sb).Append("repeat(").Append(CssText.AsciiLower(inner[..comma].Trim())).Append(", ")
                .Append(repeated).Append(')');
            return;
        }

        Separate(sb).Append(TrackFunction(ComputedStyle.Track(token, calcScratch)));
    }

    private static string ExpandedTrackList(IReadOnlyList<GridTemplateComponent> tracks)
    {
        StringBuilder sb = new();
        foreach (GridTemplateComponent component in tracks)
        {
            if (component.Kind == GridTemplateComponentKind.Single)
            {
                Separate(sb).Append(TrackFunction(component.Single));
                continue;
            }

            GridTemplateRepetition repetition = component.Repetition!;
            Separate(sb).Append("repeat(").Append(repetition.Count.Kind switch
            {
                RepetitionCountKind.AutoFill => "auto-fill",
                RepetitionCountKind.AutoFit => "auto-fit",
                _ => repetition.Count.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }).Append(", ");
            for (int i = 0; i < repetition.Tracks.Count; i++)
            {
                sb.Append(i == 0 ? string.Empty : " ").Append(TrackFunction(repetition.Tracks[i]));
            }

            sb.Append(')');
        }

        return sb.ToString();
    }

    /// <summary>The computed value of <c>grid-auto-columns</c>/<c>-rows</c>.</summary>
    public static string AutoTracks(IReadOnlyList<TrackSizingFunction> tracks)
    {
        if (tracks.Count == 0)
        {
            return "auto";
        }

        StringBuilder sb = new();
        foreach (TrackSizingFunction track in tracks)
        {
            Separate(sb).Append(TrackFunction(track));
        }

        return sb.ToString();
    }

    /// <summary>One track sizing function, as specified.</summary>
    public static string TrackFunction(TrackSizingFunction track)
    {
        CompactLength min = track.Min.IntoRaw();
        CompactLength max = track.Max.IntoRaw();
        if (min.IsAuto && max.IsFr)
        {
            return Length(max);
        }

        if (min.IsAuto && max.IsFitContent)
        {
            return "fit-content(" + Length(max) + ")";
        }

        // A calc() track holds one handle per side, so compare what they serialize to.
        string minText = Length(min);
        string maxText = Length(max);
        return minText == maxText ? minText : "minmax(" + minText + ", " + maxText + ")";
    }

    private static string Length(CompactLength value) => value.Tag switch
    {
        CompactLength.LengthTag or CompactLength.FitContentPxTag => PaintCssValues.CssPx(value.Value),
        CompactLength.PercentTag or CompactLength.FitContentPercentTag => PaintCssValues.CssNumber(value.Value * 100f) + "%",
        CompactLength.FrTag => PaintCssValues.CssNumber(value.Value) + "fr",
        CompactLength.MinContentTag => "min-content",
        CompactLength.MaxContentTag => "max-content",
        _ when value.IsCalc => GridCalcExpression.FromHandle(value.CalcValue)?.Expression ?? "auto",
        _ => "auto",
    };

    /// <summary>The computed value of <c>grid-template-areas</c>.</summary>
    public static string Areas(List<List<string>>? areas)
    {
        if (areas is not { Count: > 0 })
        {
            return "none";
        }

        StringBuilder sb = new();
        foreach (List<string> row in areas)
        {
            Separate(sb).Append('"').AppendJoin(' ', row).Append('"');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The computed <c>grid-*-start</c> and <c>grid-*-end</c> of one axis, as specified: an area
    /// name, a raw value that names lines, or the parsed placement.
    /// </summary>
    public static (string Start, string End) Sides(LayoutStyle style, bool column)
    {
        if (style.GridAreaName is { } area)
        {
            return (area, area);
        }

        if ((column ? style.GridColumnRaw : style.GridRowRaw) is { } raw)
        {
            int slash = raw.IndexOf('/');
            string start = slash >= 0 ? raw[..slash] : raw;
            string end = slash >= 0 ? raw[(slash + 1)..] : raw;
            start = RawSide(start);
            end = slash >= 0 ? RawSide(end) : IsCustomIdent(start) ? start : "auto";
            return (start, end);
        }

        if ((column ? style.GridColumn : style.GridRow) is { } line)
        {
            return (Placement(line.Start), Placement(line.End));
        }

        return ("auto", "auto");
    }

    /// <summary>One <c>&lt;grid-line&gt;</c>, in Chromium's order: span, integer, name.</summary>
    private static string RawSide(string side)
    {
        string? span = null;
        string? number = null;
        string? name = null;
        foreach (string part in side.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string lower = CssText.AsciiLower(part);
            if (lower == "span")
            {
                span = "span";
            }
            else if (lower == "auto")
            {
                return "auto";
            }
            else if (int.TryParse(part, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                number = Math.Clamp(value, -GridLimits.MaxTracks, GridLimits.MaxTracks)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                name = part;
            }
        }

        StringBuilder sb = new();
        foreach (string? part in (ReadOnlySpan<string?>)[span, number, name])
        {
            if (part is not null)
            {
                Separate(sb).Append(part);
            }
        }

        return sb.Length == 0 ? "auto" : sb.ToString();
    }

    private static string Placement(GridPlacement placement) => placement.Kind switch
    {
        GridPlacementKind.Line => placement.LineIndex == 0 ? "auto" : Invariant(placement.LineIndex),
        GridPlacementKind.Span => "span " + Invariant(placement.SpanCount),
        GridPlacementKind.NamedLine => placement.LineIndex == 0
            ? placement.Name ?? "auto"
            : Invariant(placement.LineIndex) + " " + placement.Name,
        GridPlacementKind.NamedSpan => placement.SpanCount <= 1
            ? "span " + placement.Name
            : "span " + Invariant(placement.SpanCount) + " " + placement.Name,
        _ => "auto",
    };

    /// <summary>The <c>grid-column</c>/<c>grid-row</c> shorthand, with the end omitted where it is implied.</summary>
    public static string LineShorthand(string start, string end) =>
        end == ImpliedEnd(start) ? start : start + " / " + end;

    /// <summary>The <c>grid-area</c> shorthand, dropping trailing sides that are implied.</summary>
    public static string AreaShorthand(string rowStart, string columnStart, string rowEnd, string columnEnd)
    {
        if (columnEnd != ImpliedEnd(columnStart))
        {
            return $"{rowStart} / {columnStart} / {rowEnd} / {columnEnd}";
        }

        if (rowEnd != ImpliedEnd(rowStart))
        {
            return $"{rowStart} / {columnStart} / {rowEnd}";
        }

        return columnStart != ImpliedEnd(rowStart) ? $"{rowStart} / {columnStart}" : rowStart;
    }

    private static string ImpliedEnd(string start) => IsCustomIdent(start) ? start : "auto";

    private static bool IsCustomIdent(string value)
    {
        if (value.Length == 0 || value == "auto" || value.Contains(' '))
        {
            return false;
        }

        char first = value[0];
        return !(char.IsAsciiDigit(first) || ((first is '-' or '+') && value.Length > 1 && char.IsAsciiDigit(value[1])));
    }

    private static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static StringBuilder Separate(StringBuilder sb) => sb.Length == 0 ? sb : sb.Append(' ');
}
