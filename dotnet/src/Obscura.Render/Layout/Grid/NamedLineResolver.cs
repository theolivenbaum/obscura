// Port of vendor/taffy/src/compute/grid/types/named.rs
//
// Resolves named grid lines and areas into line numbers.
//
// DEVIATION: taffy is generic over a "cheap clone string" type and wraps it in
// StrHasher so it can be used as a map key. The Obscura port fixes the identifier
// type to `string` (taffy's own DefaultCheapStr) and uses an ordinal-comparing
// Dictionary, so StrHasher has no counterpart.
namespace Obscura.Render.Layout;

/// <summary>
/// Resolver that takes grid line names and area names as input and can then resolve line names of
/// grid placement properties into line numbers.
/// </summary>
internal sealed class NamedLineResolver
{
    private readonly Dictionary<string, List<ushort>> _rowLines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ushort>> _columnLines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GridTemplateArea> _areas = new(StringComparer.Ordinal);

    private ushort _explicitColumnCount;
    private ushort _explicitRowCount;

    /// <summary>Create and initialise a new resolver.</summary>
    public NamedLineResolver(
        IGridContainerStyle style,
        ushort columnAutoRepetitions,
        ushort rowAutoRepetitions)
    {
        ushort areaColumnCount = 0;
        ushort areaRowCount = 0;

        var templateAreas = style.GridTemplateAreas;
        if (templateAreas is not null)
        {
            foreach (var area in templateAreas)
            {
                _areas[area.Name] = area;

                areaColumnCount = Math.Max(areaColumnCount, (ushort)(Math.Max(area.ColumnEnd, (ushort)1) - 1));
                areaRowCount = Math.Max(areaRowCount, (ushort)(Math.Max(area.RowEnd, (ushort)1) - 1));

                UpsertLineNameMap(_columnLines, area.Name + "-start", area.ColumnStart);
                UpsertLineNameMap(_columnLines, area.Name + "-end", area.ColumnEnd);
                UpsertLineNameMap(_rowLines, area.Name + "-start", area.RowStart);
                UpsertLineNameMap(_rowLines, area.Name + "-end", area.RowEnd);
            }
        }

        AreaColumnCount = areaColumnCount;
        AreaRowCount = areaRowCount;

        BuildLineNames(
            _columnLines, style.GridTemplateColumns, style.GridTemplateColumnNames, columnAutoRepetitions);
        BuildLineNames(_rowLines, style.GridTemplateRows, style.GridTemplateRowNames, rowAutoRepetitions);
    }

    /// <summary>Get the number of columns defined by the grid areas.</summary>
    public ushort AreaColumnCount { get; }

    /// <summary>Get the number of rows defined by the grid areas.</summary>
    public ushort AreaRowCount { get; }

    /// <summary>Set the number of columns in the explicit grid.</summary>
    public void SetExplicitColumnCount(ushort count) => _explicitColumnCount = count;

    /// <summary>Set the number of rows in the explicit grid.</summary>
    public void SetExplicitRowCount(ushort count) => _explicitRowCount = count;

    /// <summary>Resolve named lines for both ends of a row-axis grid placement.</summary>
    public Line<NonNamedGridPlacement> ResolveRowNames(Line<GridPlacement> line) =>
        ResolveLineNames(line, GridAreaAxis.Row);

    /// <summary>Resolve named lines for both ends of a column-axis grid placement.</summary>
    public Line<NonNamedGridPlacement> ResolveColumnNames(Line<GridPlacement> line) =>
        ResolveLineNames(line, GridAreaAxis.Column);

    /// <summary>Resolve named lines for both the start and the end of a grid placement.</summary>
    public Line<NonNamedGridPlacement> ResolveLineNames(Line<GridPlacement> line, GridAreaAxis axis)
    {
        var startResolved = line.Start.Kind == GridPlacementKind.NamedLine
            ? GridPlacement.FromLineIndex(
                FindLineIndex(line.Start.Name!, line.Start.LineIndex, axis, GridAreaEnd.Start, Identity).AsI16())
            : line.Start;

        var endResolved = line.End.Kind == GridPlacementKind.NamedLine
            ? GridPlacement.FromLineIndex(
                FindLineIndex(line.End.Name!, line.End.LineIndex, axis, GridAreaEnd.End, Identity).AsI16())
            : line.End;

        short explicitTrackCount = axis == GridAreaAxis.Row
            ? (short)_explicitRowCount
            : (short)_explicitColumnCount;

        // If both the *-start and *-end values specify a line, the grid span is implicit. If it has an
        // explicit span value, its grid span is explicit. Otherwise its grid span is 1.
        // https://drafts.csswg.org/css-grid-2/#grid-span
        if (startResolved.Kind == GridPlacementKind.Line && endResolved.Kind == GridPlacementKind.NamedSpan)
        {
            short startLine = startResolved.LineIndex;
            ushort normalizedStartLine = startLine > 0
                ? (ushort)startLine
                : (ushort)Math.Max(explicitTrackCount + 1 + startLine, 0);
            var endLine = FindLineIndex(
                endResolved.Name!,
                (short)endResolved.SpanCount,
                axis,
                GridAreaEnd.End,
                lines =>
                {
                    int point = PartitionPoint(lines, value => value <= normalizedStartLine);
                    return lines.GetRange(point, lines.Count - point);
                });
            return new Line<NonNamedGridPlacement>(
                NonNamedGridPlacement.FromLine(new GridLine(startLine)),
                NonNamedGridPlacement.FromLine(endLine));
        }

        if (startResolved.Kind == GridPlacementKind.NamedSpan && endResolved.Kind == GridPlacementKind.Line)
        {
            short endLine = endResolved.LineIndex;
            ushort normalizedEndLine = endLine > 0
                ? (ushort)endLine
                : (ushort)Math.Max(explicitTrackCount + 1 + endLine, 0);
            var startLine = FindLineIndex(
                startResolved.Name!,
                (short)startResolved.SpanCount,
                axis,
                GridAreaEnd.Start,
                lines =>
                {
                    int point = PartitionPoint(lines, value => value < normalizedEndLine);
                    return lines.GetRange(0, point);
                });
            return new Line<NonNamedGridPlacement>(
                NonNamedGridPlacement.FromLine(startLine),
                NonNamedGridPlacement.FromLine(new GridLine(endLine)));
        }

        return new Line<NonNamedGridPlacement>(ToNonNamed(startResolved), ToNonNamed(endResolved));
    }

    private static List<ushort> Identity(List<ushort> lines) => lines;

    private static NonNamedGridPlacement ToNonNamed(GridPlacement placement) => placement.Kind switch
    {
        GridPlacementKind.Auto => NonNamedGridPlacement.Auto,
        GridPlacementKind.Line => NonNamedGridPlacement.FromLine(new GridLine(placement.LineIndex)),
        GridPlacementKind.Span => NonNamedGridPlacement.FromSpan(placement.SpanCount),
        GridPlacementKind.NamedSpan => NonNamedGridPlacement.FromSpan(1),
        _ => throw new InvalidOperationException("Named lines must be resolved before this point"),
    };

    private static int PartitionPoint(List<ushort> lines, Func<ushort, bool> predicate)
    {
        int point = 0;
        while (point < lines.Count && predicate(lines[point]))
        {
            point++;
        }

        return point;
    }

    private static void UpsertLineNameMap(Dictionary<string, List<ushort>> map, string key, ushort value)
    {
        if (map.TryGetValue(key, out var lines))
        {
            lines.Add(value);
        }
        else
        {
            map[key] = [value];
        }
    }

    private static void BuildLineNames(
        Dictionary<string, List<ushort>> lineMap,
        IReadOnlyList<GridTemplateComponent>? tracks,
        IReadOnlyList<IReadOnlyList<string>>? lineNames,
        ushort autoRepetitions)
    {
        ushort currentLine = 0;
        if (tracks is not null && lineNames is not null)
        {
            int trackIndex = 0;
            foreach (var names in lineNames)
            {
                currentLine += 1;
                foreach (string lineName in names)
                {
                    UpsertLineNameMap(lineMap, lineName, currentLine);
                }

                // taffy advances the track iterator once per line-name set, whether or not the
                // component it yields is a repetition.
                GridTemplateComponent? component = trackIndex < tracks.Count ? tracks[trackIndex++] : null;
                if (component is not { Kind: GridTemplateComponentKind.Repeat })
                {
                    continue;
                }

                var repeat = component.Repetition!;
                ushort repeatCount = repeat.Count.Kind == RepetitionCountKind.Count
                    ? repeat.Count.Count
                    : autoRepetitions;

                for (int rep = 0; rep < repeatCount; rep++)
                {
                    foreach (var lineNameSet in repeat.LineNames)
                    {
                        foreach (string lineName in lineNameSet)
                        {
                            UpsertLineNameMap(lineMap, lineName, currentLine);
                        }

                        currentLine += 1;
                    }

                    // Last line name set collapses with the following line name set
                    currentLine -= 1;
                }

                // Last line name set collapses with the following line name set
                currentLine -= 1;
            }
        }

        // Sort and dedup lines for each name
        foreach (var lines in lineMap.Values)
        {
            lines.Sort();
            int write = 0;
            for (int read = 0; read < lines.Count; read++)
            {
                if (read == 0 || lines[read] != lines[write - 1])
                {
                    lines[write++] = lines[read];
                }
            }

            lines.RemoveRange(write, lines.Count - write);
        }
    }

    /// <summary>Resolve the grid line for a named grid line or span.</summary>
    private GridLine FindLineIndex(
        string name,
        short idx,
        GridAreaAxis axis,
        GridAreaEnd end,
        Func<List<ushort>, List<ushort>> filterLines)
    {
        short explicitTrackCount = axis == GridAreaAxis.Row
            ? (short)_explicitRowCount
            : (short)_explicitColumnCount;

        // An index of 0 is used to represent "no index specified".
        if (idx == 0)
        {
            idx = 1;
        }

        var lineLookup = axis == GridAreaAxis.Row ? _rowLines : _columnLines;
        if (lineLookup.TryGetValue(name, out var found))
        {
            return new GridLine(GetLine(filterLines(found), explicitTrackCount, idx));
        }

        string implicitName = end == GridAreaEnd.Start ? name + "-start" : name + "-end";
        if (lineLookup.TryGetValue(implicitName, out var implicitFound))
        {
            return new GridLine(GetLine(filterLines(implicitFound), explicitTrackCount, idx));
        }

        // The CSS Grid specification has a quirk where it matches non-existent line names to the
        // first (positive) implicit line in the grid.
        //
        // We add/subtract 2 to the explicit track count because (in each axis) a grid has one more
        // explicit grid line than it has tracks. And the fallback line is the line *after* that.
        //
        // See: https://github.com/w3c/csswg-drafts/issues/966#issuecomment-277042153
        short line = idx > 0
            ? (short)(explicitTrackCount + 1 + idx)
            : (short)(-(explicitTrackCount + 1 + idx));

        return new GridLine(line);
    }

    private static short GetLine(List<ushort> lines, short explicitTrackCount, short idx)
    {
        int absIdx = Math.Abs((int)idx);
        bool enoughLines = absIdx <= lines.Count;
        if (enoughLines)
        {
            return idx > 0 ? (short)lines[absIdx - 1] : (short)lines[lines.Count - absIdx];
        }

        int remainingLines = (absIdx - lines.Count) * Math.Sign((int)idx);
        return idx > 0
            ? (short)(explicitTrackCount + 1 + remainingLines)
            : (short)(-(explicitTrackCount + 1 + remainingLines));
    }
}
