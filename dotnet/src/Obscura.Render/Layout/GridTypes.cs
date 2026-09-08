// Grid-facing style types from vendor/taffy/src/style/grid.rs.
//
// SCOPE: this file contains ONLY the data types and style interfaces that
// `Style` and the tree traits must reference so that the non-grid foundation
// compiles. The CSS Grid *algorithms* (compute/grid/**) are ported separately;
// the types here are deliberately shaped to match taffy so that work can drop
// in without reshaping the foundation.
namespace Obscura.Render.Layout;

/// <summary>
/// Controls whether grid items are placed row-wise or column-wise, and whether the sparse or dense
/// packing algorithm is used.
/// </summary>
public enum GridAutoFlow : byte
{
    /// <summary>Items are placed by filling each row in turn.</summary>
    Row,

    /// <summary>Items are placed by filling each column in turn.</summary>
    Column,

    /// <summary>Combines Row with the dense packing algorithm.</summary>
    RowDense,

    /// <summary>Combines Column with the dense packing algorithm.</summary>
    ColumnDense,
}

/// <summary>The kind of a <see cref="GridPlacement"/>.</summary>
public enum GridPlacementKind : byte
{
    /// <summary>Place item according to the auto-placement algorithm.</summary>
    Auto,

    /// <summary>Place item at the specified line index.</summary>
    Line,

    /// <summary>Place item at the specified named line.</summary>
    NamedLine,

    /// <summary>Item should span the specified number of tracks.</summary>
    Span,

    /// <summary>Item should span until the nth line with the given name.</summary>
    NamedSpan,
}

/// <summary>
/// A grid line placement specification, used for grid-[row/column]-[start/end].
/// </summary>
/// <remarks>
/// taffy makes this generic over a "cheap clone string" type for custom identifiers; the Obscura
/// port fixes that to <see cref="string"/> (taffy's own <c>DefaultCheapStr</c>).
/// </remarks>
public readonly record struct GridPlacement(GridPlacementKind Kind, short LineIndex, ushort SpanCount, string? Name)
{
    /// <summary>Place item according to the auto-placement algorithm.</summary>
    public static readonly GridPlacement Auto = new(GridPlacementKind.Auto, 0, 0, null);

    /// <summary>Place item at the specified line (column or row) index.</summary>
    public static GridPlacement FromLineIndex(short index) => new(GridPlacementKind.Line, index, 0, null);

    /// <summary>Place item at the specified named line.</summary>
    public static GridPlacement FromNamedLine(string name, short index) =>
        new(GridPlacementKind.NamedLine, index, 0, name);

    /// <summary>Item should span the specified number of tracks.</summary>
    public static GridPlacement FromSpan(ushort span) => new(GridPlacementKind.Span, 0, span, null);

    /// <summary>Item should span until the nth line with the given name.</summary>
    public static GridPlacement FromNamedSpan(string name, ushort span) =>
        new(GridPlacementKind.NamedSpan, 0, span, name);
}

/// <summary>
/// A track sizing function used as the minimum of a <c>minmax()</c> pair.
/// </summary>
public readonly record struct MinTrackSizingFunction
{
    private readonly CompactLength _inner;

    private MinTrackSizingFunction(CompactLength inner) => _inner = inner;

    /// <summary>Zero pixels.</summary>
    public static MinTrackSizingFunction Zero => new(CompactLength.Zero);

    /// <summary>The auto value.</summary>
    public static MinTrackSizingFunction Auto => new(CompactLength.Auto());

    /// <summary>The min-content value.</summary>
    public static MinTrackSizingFunction MinContent => new(CompactLength.MinContent());

    /// <summary>The max-content value.</summary>
    public static MinTrackSizingFunction MaxContent => new(CompactLength.MaxContent());

    /// <summary>An absolute length.</summary>
    public static MinTrackSizingFunction FromLength(float val) => new(CompactLength.Length(val));

    /// <summary>A percentage length.</summary>
    public static MinTrackSizingFunction FromPercent(float val) => new(CompactLength.Percent(val));

    /// <summary>Create from a raw <see cref="CompactLength"/>.</summary>
    public static MinTrackSizingFunction FromRaw(CompactLength val) => new(val);

    /// <summary>Widen a <see cref="LengthPercentage"/>.</summary>
    public static MinTrackSizingFunction From(LengthPercentage input) => new(input.IntoRaw());

    /// <summary>Widen a <see cref="LengthPercentageAuto"/>.</summary>
    public static MinTrackSizingFunction From(LengthPercentageAuto input) => new(input.IntoRaw());

    /// <summary>Widen a <see cref="Dimension"/>.</summary>
    public static MinTrackSizingFunction From(Dimension input) => new(input.IntoRaw());

    /// <summary>Narrow a <see cref="MaxTrackSizingFunction"/>, mapping fr/fit-content to auto.</summary>
    public static MinTrackSizingFunction From(MaxTrackSizingFunction input)
    {
        var raw = input.IntoRaw();
        return raw.IsFr || raw.IsFitContent ? Auto : new MinTrackSizingFunction(raw);
    }

    /// <summary>Get the underlying <see cref="CompactLength"/> representation of the value.</summary>
    public CompactLength IntoRaw() => _inner;
}

/// <summary>
/// A track sizing function used as the maximum of a <c>minmax()</c> pair.
/// </summary>
public readonly record struct MaxTrackSizingFunction
{
    private readonly CompactLength _inner;

    private MaxTrackSizingFunction(CompactLength inner) => _inner = inner;

    /// <summary>Zero pixels.</summary>
    public static MaxTrackSizingFunction Zero => new(CompactLength.Zero);

    /// <summary>The auto value.</summary>
    public static MaxTrackSizingFunction Auto => new(CompactLength.Auto());

    /// <summary>The min-content value.</summary>
    public static MaxTrackSizingFunction MinContent => new(CompactLength.MinContent());

    /// <summary>The max-content value.</summary>
    public static MaxTrackSizingFunction MaxContent => new(CompactLength.MaxContent());

    /// <summary>An absolute length.</summary>
    public static MaxTrackSizingFunction FromLength(float val) => new(CompactLength.Length(val));

    /// <summary>A percentage length.</summary>
    public static MaxTrackSizingFunction FromPercent(float val) => new(CompactLength.Percent(val));

    /// <summary>An <c>fr</c> value.</summary>
    public static MaxTrackSizingFunction FromFr(float val) => new(CompactLength.Fr(val));

    /// <summary>A <c>fit-content()</c> value.</summary>
    public static MaxTrackSizingFunction FitContent(LengthPercentage limit)
    {
        var raw = limit.IntoRaw();
        return raw.Tag switch
        {
            CompactLength.LengthTag => new MaxTrackSizingFunction(CompactLength.FitContentPx(raw.Value)),
            CompactLength.PercentTag => new MaxTrackSizingFunction(CompactLength.FitContentPercent(raw.Value)),
            _ => throw new ArgumentOutOfRangeException(nameof(limit)),
        };
    }

    /// <summary>Create from a raw <see cref="CompactLength"/>.</summary>
    public static MaxTrackSizingFunction FromRaw(CompactLength val) => new(val);

    /// <summary>Widen a <see cref="LengthPercentage"/>.</summary>
    public static MaxTrackSizingFunction From(LengthPercentage input) => new(input.IntoRaw());

    /// <summary>Widen a <see cref="LengthPercentageAuto"/>.</summary>
    public static MaxTrackSizingFunction From(LengthPercentageAuto input) => new(input.IntoRaw());

    /// <summary>Widen a <see cref="Dimension"/>.</summary>
    public static MaxTrackSizingFunction From(Dimension input) => new(input.IntoRaw());

    /// <summary>Widen a <see cref="MinTrackSizingFunction"/>.</summary>
    public static MaxTrackSizingFunction From(MinTrackSizingFunction input) => new(input.IntoRaw());

    /// <summary>Get the underlying <see cref="CompactLength"/> representation of the value.</summary>
    public CompactLength IntoRaw() => _inner;
}

/// <summary>
/// A track sizing function: a <c>minmax()</c> pair. Alias of
/// <c>MinMax&lt;MinTrackSizingFunction, MaxTrackSizingFunction&gt;</c> in taffy.
/// </summary>
public readonly record struct TrackSizingFunction(MinTrackSizingFunction Min, MaxTrackSizingFunction Max)
{
    /// <summary>Auto in both directions.</summary>
    public static readonly TrackSizingFunction Auto =
        new(MinTrackSizingFunction.Auto, MaxTrackSizingFunction.Auto);

    /// <summary>Create a <c>minmax()</c>.</summary>
    public static TrackSizingFunction MinMax(MinTrackSizingFunction min, MaxTrackSizingFunction max) =>
        new(min, max);

    /// <summary>Shorthand for <c>minmax(0, Nfr)</c>.</summary>
    public static TrackSizingFunction Flex(float flexFraction) =>
        new(MinTrackSizingFunction.Zero, MaxTrackSizingFunction.FromFr(flexFraction));

    /// <summary>A fixed-length track.</summary>
    public static TrackSizingFunction FromLength(float val) =>
        new(MinTrackSizingFunction.FromLength(val), MaxTrackSizingFunction.FromLength(val));

    /// <summary>A percentage track.</summary>
    public static TrackSizingFunction FromPercent(float val) =>
        new(MinTrackSizingFunction.FromPercent(val), MaxTrackSizingFunction.FromPercent(val));
}

/// <summary>
/// The kind of automatic repetition to perform in a repeated track definition.
/// </summary>
public enum RepetitionCountKind : byte
{
    /// <summary>Auto-repeating tracks generated to fit the container (auto-fill).</summary>
    AutoFill,

    /// <summary>Auto-repeating tracks generated to fit the container (auto-fit).</summary>
    AutoFit,

    /// <summary>The specified tracks are repeated exactly N times.</summary>
    Count,
}

/// <summary>The first argument to a repeated track definition.</summary>
public readonly record struct RepetitionCount(RepetitionCountKind Kind, ushort Count)
{
    /// <summary>Auto-fill repetition.</summary>
    public static readonly RepetitionCount AutoFill = new(RepetitionCountKind.AutoFill, 0);

    /// <summary>Auto-fit repetition.</summary>
    public static readonly RepetitionCount AutoFit = new(RepetitionCountKind.AutoFit, 0);

    /// <summary>Exactly N repetitions.</summary>
    public static RepetitionCount FromCount(ushort count) => new(RepetitionCountKind.Count, count);

    /// <summary>Parse "auto-fit"/"auto-fill" into a repetition count.</summary>
    public static RepetitionCount FromString(string value) => value switch
    {
        "auto-fit" => AutoFit,
        "auto-fill" => AutoFill,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Invalid repetition value"),
    };
}

/// <summary>A typed representation of a <c>repeat(..)</c> in a <c>grid-template-*</c> value.</summary>
public sealed class GridTemplateRepetition : IEquatable<GridTemplateRepetition>
{
    /// <summary>The number of times the repeat is repeated.</summary>
    public RepetitionCount Count { get; set; }

    /// <summary>The tracks to repeat.</summary>
    public List<TrackSizingFunction> Tracks { get; set; } = [];

    /// <summary>The line names for the repeated tracks.</summary>
    public List<List<string>> LineNames { get; set; } = [];

    /// <summary>Create a deep copy.</summary>
    public GridTemplateRepetition Clone() => new()
    {
        Count = Count,
        Tracks = [.. Tracks],
        LineNames = [.. LineNames.Select(static names => new List<string>(names))],
    };

    /// <inheritdoc/>
    public bool Equals(GridTemplateRepetition? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (!Count.Equals(other.Count) || Tracks.Count != other.Tracks.Count
            || LineNames.Count != other.LineNames.Count)
        {
            return false;
        }

        for (int i = 0; i < Tracks.Count; i++)
        {
            if (!Tracks[i].Equals(other.Tracks[i]))
            {
                return false;
            }
        }

        for (int i = 0; i < LineNames.Count; i++)
        {
            if (!LineNames[i].SequenceEqual(other.LineNames[i], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as GridTemplateRepetition);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Count, Tracks.Count, LineNames.Count);
}

/// <summary>The kind of a <see cref="GridTemplateComponent"/>.</summary>
public enum GridTemplateComponentKind : byte
{
    /// <summary>A single non-repeated track.</summary>
    Single,

    /// <summary>An auto-generated repetition.</summary>
    Repeat,
}

/// <summary>
/// An element in a <c>grid-template-columns</c> or <c>grid-template-rows</c> definition.
/// </summary>
public sealed class GridTemplateComponent : IEquatable<GridTemplateComponent>
{
    private GridTemplateComponent(
        GridTemplateComponentKind kind,
        TrackSizingFunction single,
        GridTemplateRepetition? repetition)
    {
        Kind = kind;
        Single = single;
        Repetition = repetition;
    }

    /// <summary>Which variant this is.</summary>
    public GridTemplateComponentKind Kind { get; }

    /// <summary>The single track, valid when <see cref="Kind"/> is Single.</summary>
    public TrackSizingFunction Single { get; }

    /// <summary>The repetition, valid when <see cref="Kind"/> is Repeat.</summary>
    public GridTemplateRepetition? Repetition { get; }

    /// <summary>Create a single-track component.</summary>
    public static GridTemplateComponent FromSingle(TrackSizingFunction track) =>
        new(GridTemplateComponentKind.Single, track, null);

    /// <summary>Create a repeated component.</summary>
    public static GridTemplateComponent FromRepeat(GridTemplateRepetition repetition) =>
        new(GridTemplateComponentKind.Repeat, default, repetition);

    /// <summary>Create a deep copy.</summary>
    public GridTemplateComponent Clone() =>
        Kind == GridTemplateComponentKind.Single ? FromSingle(Single) : FromRepeat(Repetition!.Clone());

    /// <inheritdoc/>
    public bool Equals(GridTemplateComponent? other) =>
        other is not null
        && Kind == other.Kind
        && Single.Equals(other.Single)
        && (Repetition is null ? other.Repetition is null : Repetition.Equals(other.Repetition));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as GridTemplateComponent);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Single);
}

/// <summary>Defines a named grid area.</summary>
public readonly record struct GridTemplateArea(
    string Name,
    ushort RowStart,
    ushort RowEnd,
    ushort ColumnStart,
    ushort ColumnEnd);

/// <summary>Defines a named grid line.</summary>
public readonly record struct NamedGridLine(string Name, ushort Index);
