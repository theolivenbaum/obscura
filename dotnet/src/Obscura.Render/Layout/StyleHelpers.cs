// Port of vendor/taffy/src/style_helpers.rs
//
// DEVIATION: taffy expresses these as generic traits (TaffyZero, TaffyAuto,
// FromLength, ...) that are implemented for every style type and for the geometry
// containers. C# cannot express "a const of the implementing type", so the port
// exposes the same values as typed static factory helpers. The per-type
// constructors live on the types themselves (Dimension.Auto, Rect helpers on
// GeometryExtensions); this class carries the free functions.
namespace Obscura.Render.Layout;

/// <summary>Helper functions for creating instances of the style and geometry types.</summary>
public static class StyleHelpers
{
    /// <summary>Returns an absolute length dimension.</summary>
    public static Dimension Length(float value) => Dimension.FromLength(value);

    /// <summary>Returns a percentage dimension ([0.0, 1.0]).</summary>
    public static Dimension Percent(float value) => Dimension.FromPercent(value);

    /// <summary>Returns the auto dimension.</summary>
    public static Dimension Auto() => Dimension.Auto;

    /// <summary>Returns a zero-length dimension.</summary>
    public static Dimension Zero() => Dimension.Zero;

    /// <summary>Specifies a grid line to place a grid item between, in CSS Grid Line coordinates.</summary>
    public static GridPlacement Line(short index) => GridPlacement.FromLineIndex(index);

    /// <summary>Returns a span placement.</summary>
    public static GridPlacement Span(ushort span) => GridPlacement.FromSpan(span);

    /// <summary>Returns a track sizing function with the given min and max.</summary>
    public static TrackSizingFunction MinMax(MinTrackSizingFunction min, MaxTrackSizingFunction max) =>
        TrackSizingFunction.MinMax(min, max);

    /// <summary>Shorthand for <c>minmax(0, Nfr)</c>.</summary>
    public static TrackSizingFunction Flex(float flexFraction) => TrackSizingFunction.Flex(flexFraction);

    /// <summary>Create an <c>fr</c> max track sizing function.</summary>
    public static MaxTrackSizingFunction Fr(float value) => MaxTrackSizingFunction.FromFr(value);

    /// <summary>Create a <c>fit-content(..)</c> max track sizing function.</summary>
    public static MaxTrackSizingFunction FitContent(LengthPercentage argument) =>
        MaxTrackSizingFunction.FitContent(argument);

    /// <summary>Returns an auto-repeated track definition.</summary>
    public static GridTemplateComponent Repeat(RepetitionCount count, List<TrackSizingFunction> tracks) =>
        GridTemplateComponent.FromRepeat(new GridTemplateRepetition { Count = count, Tracks = tracks });

    /// <summary>Returns an auto-repeated track definition with an integer count.</summary>
    public static GridTemplateComponent Repeat(ushort count, List<TrackSizingFunction> tracks) =>
        Repeat(RepetitionCount.FromCount(count), tracks);

    /// <summary>Returns an auto-repeated track definition from "auto-fit"/"auto-fill".</summary>
    public static GridTemplateComponent Repeat(string count, List<TrackSizingFunction> tracks) =>
        Repeat(RepetitionCount.FromString(count), tracks);

    /// <summary>Returns a grid template containing <paramref name="count"/> evenly sized tracks.</summary>
    public static List<GridTemplateComponent> EvenlySizedTracks(ushort count) =>
        [Repeat(count, [TrackSizingFunction.Flex(1.0f)])];
}
