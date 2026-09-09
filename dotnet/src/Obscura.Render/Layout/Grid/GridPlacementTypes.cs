// Port of the placement-coordinate half of vendor/taffy/src/style/grid.rs:
// GenericGridPlacement, OriginZeroGridPlacement, NonNamedGridPlacement,
// GridAreaAxis, GridAreaEnd, and the Line<..> resolution helpers.
//
// DEVIATION: taffy expresses these as one generic `GenericGridPlacement<LineType:
// GridCoordinate>`. C# cannot constrain a generic to the operators these need, so
// the port instantiates the two coordinate systems taffy actually uses as two
// concrete structs with identical shape.
namespace Obscura.Render.Layout;

/// <summary>Which variant a generic grid placement is.</summary>
internal enum GenericGridPlacementKind : byte
{
    /// <summary>Place item according to the auto-placement algorithm.</summary>
    Auto,

    /// <summary>Place item at the specified line index.</summary>
    Line,

    /// <summary>Item should span the specified number of tracks.</summary>
    Span,
}

/// <summary>Axis as <c>Row</c> or <c>Column</c>.</summary>
internal enum GridAreaAxis : byte
{
    /// <summary>The row axis.</summary>
    Row,

    /// <summary>The column axis.</summary>
    Column,
}

/// <summary>Logical end (<c>Start</c> or <c>End</c>).</summary>
internal enum GridAreaEnd : byte
{
    /// <summary>The start end.</summary>
    Start,

    /// <summary>The end end.</summary>
    End,
}

/// <summary>
/// A grid line placement using CSS grid line coordinates. Named lines are expected to have already
/// been resolved by the time values of this type are constructed.
/// </summary>
internal readonly record struct NonNamedGridPlacement(GenericGridPlacementKind Kind, GridLine LineValue, ushort SpanValue)
{
    /// <summary>Auto placement.</summary>
    public static readonly NonNamedGridPlacement Auto = new(GenericGridPlacementKind.Auto, default, 0);

    /// <summary>Placement at a specific line.</summary>
    public static NonNamedGridPlacement FromLine(GridLine line) => new(GenericGridPlacementKind.Line, line, 0);

    /// <summary>Placement spanning a number of tracks.</summary>
    public static NonNamedGridPlacement FromSpan(ushort span) => new(GenericGridPlacementKind.Span, default, span);

    /// <summary>Apply a mapping function if this is a Line. Otherwise return self unmodified.</summary>
    public OriginZeroGridPlacement IntoOriginZeroPlacement(ushort explicitTrackCount) => Kind switch
    {
        GenericGridPlacementKind.Auto => OriginZeroGridPlacement.Auto,
        GenericGridPlacementKind.Span => OriginZeroGridPlacement.FromSpan(SpanValue),
        // Grid line zero is an invalid index, so it gets treated as Auto.
        _ => LineValue.AsI16() == 0
            ? OriginZeroGridPlacement.Auto
            : OriginZeroGridPlacement.FromLine(LineValue.IntoOriginZeroLine(explicitTrackCount)),
    };
}

/// <summary>A grid line placement using the normalized OriginZero coordinates.</summary>
internal readonly record struct OriginZeroGridPlacement(
    GenericGridPlacementKind Kind,
    OriginZeroLine LineValue,
    ushort SpanValue)
{
    /// <summary>Auto placement.</summary>
    public static readonly OriginZeroGridPlacement Auto = new(GenericGridPlacementKind.Auto, default, 0);

    /// <summary>Placement at a specific line.</summary>
    public static OriginZeroGridPlacement FromLine(OriginZeroLine line) =>
        new(GenericGridPlacementKind.Line, line, 0);

    /// <summary>Placement spanning a number of tracks.</summary>
    public static OriginZeroGridPlacement FromSpan(ushort span) =>
        new(GenericGridPlacementKind.Span, default, span);
}

/// <summary>Resolution helpers over lines of grid placements.</summary>
internal static class GridPlacementExtensions
{
    // ------------------------------------------------------- GridPlacement (public style type)

    /// <summary>
    /// Convert a <see cref="GridPlacement"/> into OriginZero coordinates, mapping named
    /// lines/spans to auto (they are accounted for separately).
    /// </summary>
    public static OriginZeroGridPlacement IntoOriginZeroPlacementIgnoringNamed(
        this GridPlacement self,
        ushort explicitTrackCount) => self.Kind switch
        {
            GridPlacementKind.Auto => OriginZeroGridPlacement.Auto,
            GridPlacementKind.Span => OriginZeroGridPlacement.FromSpan(self.SpanCount),
            // Grid line zero is an invalid index, so it gets treated as Auto.
            GridPlacementKind.Line => self.LineIndex == 0
                ? OriginZeroGridPlacement.Auto
                : OriginZeroGridPlacement.FromLine(
                    new GridLine(self.LineIndex).IntoOriginZeroLine(explicitTrackCount)),
            _ => OriginZeroGridPlacement.Auto,
        };

    /// <summary>Convert both ends of a placement line into OriginZero coordinates, ignoring names.</summary>
    public static Line<OriginZeroGridPlacement> IntoOriginZeroIgnoringNamed(
        this Line<GridPlacement> self,
        ushort explicitTrackCount) =>
        new(
            self.Start.IntoOriginZeroPlacementIgnoringNamed(explicitTrackCount),
            self.End.IntoOriginZeroPlacementIgnoringNamed(explicitTrackCount));

    /// <summary>
    /// Whether the track position is definite in this axis (or the item will need auto placement).
    /// </summary>
    public static bool IsDefinite(this Line<GridPlacement> self)
    {
        if (self.Start.Kind == GridPlacementKind.Line && self.Start.LineIndex != 0)
        {
            return true;
        }

        if (self.End.Kind == GridPlacementKind.Line && self.End.LineIndex != 0)
        {
            return true;
        }

        return self.Start.Kind == GridPlacementKind.NamedLine || self.End.Kind == GridPlacementKind.NamedLine;
    }

    // --------------------------------------------------------------- NonNamedGridPlacement

    /// <summary>
    /// Whether the track position is definite in this axis (or the item will need auto placement).
    /// </summary>
    public static bool IsDefinite(this Line<NonNamedGridPlacement> self)
    {
        if (self.Start.Kind == GenericGridPlacementKind.Line && self.Start.LineValue.AsI16() != 0)
        {
            return true;
        }

        return self.End.Kind == GenericGridPlacementKind.Line && self.End.LineValue.AsI16() != 0;
    }

    /// <summary>Convert both ends of a placement line into OriginZero coordinates.</summary>
    public static Line<OriginZeroGridPlacement> IntoOriginZero(
        this Line<NonNamedGridPlacement> self,
        ushort explicitTrackCount) =>
        new(
            self.Start.IntoOriginZeroPlacement(explicitTrackCount),
            self.End.IntoOriginZeroPlacement(explicitTrackCount));

    /// <summary>
    /// Resolves the span for an indefinite placement (a placement that does not consist of two
    /// lines). Throws if called on a definite placement.
    /// </summary>
    public static ushort IndefiniteSpan(this Line<NonNamedGridPlacement> self) =>
        IndefiniteSpanInner(self.Start.Kind, self.Start.SpanValue, self.End.Kind, self.End.SpanValue);

    // --------------------------------------------------------------- OriginZeroGridPlacement

    /// <summary>
    /// Whether the track position is definite in this axis (or the item will need auto placement).
    /// </summary>
    public static bool IsDefinite(this Line<OriginZeroGridPlacement> self) =>
        self.Start.Kind == GenericGridPlacementKind.Line || self.End.Kind == GenericGridPlacementKind.Line;

    /// <summary>
    /// Resolves the span for an indefinite placement (a placement that does not consist of two
    /// lines). Throws if called on a definite placement.
    /// </summary>
    public static ushort IndefiniteSpan(this Line<OriginZeroGridPlacement> self) =>
        IndefiniteSpanInner(self.Start.Kind, self.Start.SpanValue, self.End.Kind, self.End.SpanValue);

    /// <summary>
    /// If at least one of the start and end positions is a line, then the other end can be resolved
    /// into a line purely based on the information contained within the placement specification.
    /// </summary>
    public static Line<OriginZeroLine> ResolveDefiniteGridLines(this Line<OriginZeroGridPlacement> self)
    {
        var start = self.Start;
        var end = self.End;

        if (start.Kind == GenericGridPlacementKind.Line && end.Kind == GenericGridPlacementKind.Line)
        {
            return start.LineValue == end.LineValue
                ? new Line<OriginZeroLine>(start.LineValue, start.LineValue + (ushort)1)
                : new Line<OriginZeroLine>(
                    OriginZeroLine.Min(start.LineValue, end.LineValue),
                    OriginZeroLine.Max(start.LineValue, end.LineValue));
        }

        if (start.Kind == GenericGridPlacementKind.Line && end.Kind == GenericGridPlacementKind.Span)
        {
            return new Line<OriginZeroLine>(start.LineValue, start.LineValue + end.SpanValue);
        }

        if (start.Kind == GenericGridPlacementKind.Line && end.Kind == GenericGridPlacementKind.Auto)
        {
            return new Line<OriginZeroLine>(start.LineValue, start.LineValue + (ushort)1);
        }

        if (start.Kind == GenericGridPlacementKind.Span && end.Kind == GenericGridPlacementKind.Line)
        {
            return new Line<OriginZeroLine>(end.LineValue - start.SpanValue, end.LineValue);
        }

        if (start.Kind == GenericGridPlacementKind.Auto && end.Kind == GenericGridPlacementKind.Line)
        {
            return new Line<OriginZeroLine>(end.LineValue - (ushort)1, end.LineValue);
        }

        throw new InvalidOperationException(
            "ResolveDefiniteGridLines should only be called on definite grid tracks");
    }

    /// <summary>
    /// For absolutely positioned items: lines resolve to definite lines, spans resolve relative to a
    /// definite other end (or to null), and auto resolves to null.
    /// </summary>
    public static Line<OriginZeroLine?> ResolveAbsolutelyPositionedGridTracks(
        this Line<OriginZeroGridPlacement> self)
    {
        var start = self.Start;
        var end = self.End;

        if (start.Kind == GenericGridPlacementKind.Line && end.Kind == GenericGridPlacementKind.Line)
        {
            return start.LineValue == end.LineValue
                ? new Line<OriginZeroLine?>(start.LineValue, start.LineValue + (ushort)1)
                : new Line<OriginZeroLine?>(
                    OriginZeroLine.Min(start.LineValue, end.LineValue),
                    OriginZeroLine.Max(start.LineValue, end.LineValue));
        }

        if (start.Kind == GenericGridPlacementKind.Line && end.Kind == GenericGridPlacementKind.Span)
        {
            return new Line<OriginZeroLine?>(start.LineValue, start.LineValue + end.SpanValue);
        }

        if (start.Kind == GenericGridPlacementKind.Line && end.Kind == GenericGridPlacementKind.Auto)
        {
            return new Line<OriginZeroLine?>(start.LineValue, null);
        }

        if (start.Kind == GenericGridPlacementKind.Span && end.Kind == GenericGridPlacementKind.Line)
        {
            return new Line<OriginZeroLine?>(end.LineValue - start.SpanValue, end.LineValue);
        }

        if (start.Kind == GenericGridPlacementKind.Auto && end.Kind == GenericGridPlacementKind.Line)
        {
            return new Line<OriginZeroLine?>(null, end.LineValue);
        }

        return new Line<OriginZeroLine?>(null, null);
    }

    private static ushort IndefiniteSpanInner(
        GenericGridPlacementKind startKind,
        ushort startSpan,
        GenericGridPlacementKind endKind,
        ushort endSpan)
    {
        if (startKind == GenericGridPlacementKind.Line && endKind == GenericGridPlacementKind.Line)
        {
            throw new InvalidOperationException(
                "IndefiniteSpan should only be called on indefinite grid tracks");
        }

        if (startKind == GenericGridPlacementKind.Span)
        {
            return startSpan;
        }

        if (endKind == GenericGridPlacementKind.Span)
        {
            // (Line, Span) and (Auto, Span) both resolve to the end span.
            return endSpan;
        }

        return 1;
    }
}
