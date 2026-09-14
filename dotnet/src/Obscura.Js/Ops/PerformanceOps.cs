using System.Globalization;
using System.Text;

namespace Obscura.Js.Ops;

/// <summary>
/// The Performance Timeline ops: the subresource list behind
/// <c>performance.getEntriesByType('resource')</c>.
/// </summary>
/// <remarks>
/// <para>
/// PORT ADDITION: there is no counterpart in <c>crates/obscura-js/src/ops.rs</c>. The
/// shim used to hard-code <c>resource</c> to an empty array because nothing told JS
/// which subresources the host had fetched. The host does know - every script,
/// stylesheet, image and font goes through one of the Page's fetch paths, and
/// scripted fetch()/XHR goes through <c>op_fetch_url</c> - so both now append a
/// <see cref="ResourceTimingRecord"/> and this op hands them over. Sharing
/// <c>bootstrap.js</c> means the Rust engine sees the same shim; these ops are
/// absent there, and the shim treats a missing op as "no resources", which is the
/// behaviour it had before.
/// </para>
/// <para>
/// Only measured values travel. The transport does not instrument DNS, TCP, TLS or
/// request/response split, so those phases are not in
/// <see cref="ResourceTimingRecord"/> at all and the shim leaves them at 0, which is
/// what Resource Timing prescribes for a phase that did not occur or cannot be
/// reported.
/// </para>
/// </remarks>
public static class PerformanceOps
{
    /// <summary>
    /// Hard ceiling on retained resource-timing records, independent of the page's
    /// own <c>setResourceTimingBufferSize()</c>.
    /// </summary>
    /// <remarks>
    /// Records are dropped on arrival once the ceiling is reached rather than trimmed
    /// from the front, so an index handed to <c>op_resource_timings</c> never becomes
    /// stale and the shim can read incrementally.
    /// </remarks>
    public const int ResourceTimingCeiling = 1000;

    /// <summary>
    /// Now, as unix-epoch milliseconds. The shim rebases these onto the document's
    /// <c>performance.timeOrigin</c>, which only it knows.
    /// </summary>
    public static double UnixMilliseconds() =>
        (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalMilliseconds;

    /// <summary>
    /// The body size as it travelled, taken from <c>content-length</c>.
    /// </summary>
    /// <remarks>
    /// Falls back to <paramref name="decodedLength"/>, which is right for an
    /// unencoded response and is also all that is knowable once the handler has
    /// transparently decompressed one - it drops <c>content-length</c> along with
    /// <c>content-encoding</c> when it does, so there is no on-the-wire size left to
    /// report. Guessing a compression ratio instead would be worse than saying the
    /// body was its own size.
    /// </remarks>
    public static long EncodedBodySize(
        IReadOnlyDictionary<string, string> responseHeaders,
        long decodedLength)
    {
        ArgumentNullException.ThrowIfNull(responseHeaders);
        return responseHeaders.TryGetValue("content-length", out var raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            && length >= 0
                ? length
                : decodedLength;
    }

    /// <summary>Append one completed subresource to the document's timeline.</summary>
    public static void RecordResourceTiming(ObscuraState state, ResourceTimingRecord record)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);
        if (state.ResourceTimings.Count >= ResourceTimingCeiling)
        {
            return;
        }

        state.ResourceTimings.Add(record);
    }

    /// <summary>
    /// <c>op_resource_timings</c>. The records added since <paramref name="sinceIndex"/>,
    /// as a JSON array. The shim keeps its own cursor and its own buffer, so a
    /// <c>getEntries()</c> in a tight loop costs one empty array.
    /// </summary>
    public static string OpResourceTimings(ObscuraState state, double sinceIndex) => OpGuard.Run(
        "op_resource_timings",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            var records = state.ResourceTimings;
            var start = double.IsFinite(sinceIndex) && sinceIndex > 0 ? (int)sinceIndex : 0;
            if (start >= records.Count)
            {
                return "[]";
            }

            var sb = new StringBuilder(256 * (records.Count - start));
            sb.Append('[');
            for (var i = start; i < records.Count; i++)
            {
                if (i != start)
                {
                    sb.Append(',');
                }

                AppendRecord(sb, records[i]);
            }

            sb.Append(']');
            return sb.ToString();
        },
        "[]");

    /// <summary>
    /// <c>op_clear_resource_timings</c>. Answers the shim's own cursor reset by
    /// reporting how many records exist, so <c>clearResourceTimings()</c> hides
    /// exactly what has been produced so far without discarding shared state a frame
    /// realm may still be reading.
    /// </summary>
    public static double OpResourceTimingCount(ObscuraState state) => OpGuard.Run(
        "op_resource_timing_count",
        () =>
        {
            ArgumentNullException.ThrowIfNull(state);
            return (double)state.ResourceTimings.Count;
        },
        0d);

    private static void AppendRecord(StringBuilder sb, ResourceTimingRecord record)
    {
        sb.Append("{\"name\":");
        SerdeJson.AppendString(sb, record.Url);
        sb.Append(",\"initiatorType\":");
        SerdeJson.AppendString(sb, record.InitiatorType);
        sb.Append(",\"responseStatus\":")
          .Append(record.Status.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"startedAt\":").Append(SerdeJson.Number(record.StartedAtUnixMs));
        sb.Append(",\"endedAt\":").Append(SerdeJson.Number(record.EndedAtUnixMs));
        sb.Append(",\"decodedBodySize\":")
          .Append(record.DecodedBodySize.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"encodedBodySize\":")
          .Append(record.EncodedBodySize.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"contentType\":");
        SerdeJson.AppendString(sb, record.ContentType);
        sb.Append('}');
    }
}
