// Port-specific bound on the size of a grid; taffy has none.
namespace PocketCalculator.Render.Layout;

/// <summary>
/// The most tracks a grid's template and placements may name, and the most tracks one axis of
/// a grid may hold.
/// </summary>
/// <remarks>
/// Chromium bounds a grid with <c>kGridMaxTracks</c> (10,000,000, measured in Chromium 141): a
/// longer track list is truncated, a line number or span is clamped to it at parse time, the
/// auto-repeat count stops at it, and an item placed past it is pulled back into the last
/// track, so an axis never holds more than that many tracks, explicit and implicit together.
/// taffy has no bound at all, and neither does the Rust engine beyond capping each
/// <c>repeat()</c> at 1000; the result was an explicit grid of 2^20 tracks from nested
/// <c>repeat()</c>, an auto-fill count computed by casting an unbounded float to <c>u16</c>
/// (a hang), and 16-bit line arithmetic that wrapped.
/// <para>
/// The port keeps Chromium's rules with two smaller limits. What a stylesheet names (template
/// tracks, auto-repeat count, line numbers, spans) stops at <see cref="MaxTracks"/>. What an
/// axis holds, explicit and implicit together, stops at <see cref="MaxAxisTracks"/>: auto-placed
/// items add implicit tracks one by one, and a grid of more items than 10,000 (a long list laid
/// out as a one-column grid) must keep giving each its own track. Every track the port holds
/// is an object the track sizing algorithm walks several times, unlike Chromium's collapsed
/// track ranges, and a hundred items spanning 10,000 tracks each reach the axis limit, so it is
/// set where that stays cheap. Measured on such a page: at 100,000 tracks the layout takes
/// about 0.3 s and 25 MB over the page's baseline; at 1,000,000 it took 0.8 s and 300 MB.
/// </para>
/// </remarks>
internal static class GridLimits
{
    /// <summary>The template track, auto-repeat, line number and span limit.</summary>
    public const int MaxTracks = 10_000;

    /// <summary>The most tracks, explicit and implicit together, one axis of a grid holds.</summary>
    public const int MaxAxisTracks = 100_000;

    /// <summary>A span, clamped to the limit.</summary>
    public static ushort ClampSpan(ushort span) => span > MaxTracks ? (ushort)MaxTracks : span;

    /// <summary>
    /// The origin-zero lines an axis may occupy, given the earliest line any item asks for: as
    /// many negative implicit tracks as fit beside the explicit grid, then up to
    /// <see cref="MaxAxisTracks"/> tracks in all (or the explicit grid's end, should that be
    /// further).
    /// </summary>
    public static GridWindow WindowFor(int earliestLine, int explicitCount)
    {
        int negative = Math.Min(Math.Max(-earliestLine, 0), Math.Max(MaxAxisTracks - explicitCount, 0));
        return new GridWindow(-negative, -negative + Math.Max(MaxAxisTracks, negative + explicitCount));
    }

    /// <summary>
    /// Clamp an estimate of an axis's track counts so it fits the limit: the negative implicit
    /// tracks first, then the positive ones, the order Chromium resolves them in.
    /// </summary>
    public static TrackCounts ClampCounts(TrackCounts counts)
    {
        int room = Math.Max(MaxAxisTracks - counts.Explicit, 0);
        int negative = Math.Min(counts.NegativeImplicit, room);
        int positive = Math.Min(counts.PositiveImplicit, room - negative);
        return new TrackCounts(negative, counts.Explicit, positive);
    }
}

/// <summary>A half-open range of origin-zero lines a grid axis may occupy.</summary>
internal readonly record struct GridWindow(int Start, int End)
{
    /// <summary>
    /// Pull a span into the window the way Chromium pulls an item placed past
    /// <c>kGridMaxTracks</c> back: truncate the end, and put a start past the end into the
    /// last track.
    /// </summary>
    public Line<OriginZeroLine> Clamp(Line<OriginZeroLine> span)
    {
        int start = span.Start.Value;
        int end = span.End.Value;
        if (start >= Start && end <= End)
        {
            return span;
        }

        end = Math.Min(end, End);
        start = Math.Max(Math.Min(start, End - 1), Start);
        end = Math.Max(end, start + 1);
        return new Line<OriginZeroLine>(new OriginZeroLine(start), new OriginZeroLine(end));
    }

    /// <summary>Whether a span lies wholly inside the window.</summary>
    public bool Contains(Line<OriginZeroLine> span) => span.Start.Value >= Start && span.End.Value <= End;
}
