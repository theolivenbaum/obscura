using PocketCalculator.Dom;

namespace PocketCalculator.Render;

/// <summary>
/// Answers "how much inline-axis margin, border and padding falls on the line [start, end)"
/// for one IFC's boundary events.
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-render/src/inline.rs, whose <c>line_edge_advance</c>,
/// <c>line_advance_before_event</c> and <c>line_advance_before_text</c> scan every event, and
/// through <c>boundary_event_on_line</c> every owner box per event to learn whether its owner
/// is empty: O(events x owners) per query, issued per line, per break candidate, per owner
/// fragment and per painted glyph, so 1200 nested bordered spans took minutes. Emptiness is
/// now precomputed per event, and a large event list answers from cached running sums
/// instead of a scan. The results are the same bits.
/// <para>
/// Events are appended in document order at the current text length, so positions are
/// non-decreasing in index order. The events on the line [s, e) are then two consecutive index
/// ranges: the events at positions [s, e), less those at exactly s that are a non-empty
/// owner's end, and after them the group at exactly e, of which a non-empty owner's end counts
/// when e &gt; s and an empty owner's events count when e is the end of the source text.
/// </para>
/// <para>
/// Every result must be the f32 sum the scan computed, in the scan's order, because it moves
/// glyphs and decides line breaks, and f32 addition does not associate: prefix differences
/// moved a glyph by an ulp and changed a few antialiased pixels. So the cache keeps, per line
/// start, the running f32 sum of that line's first range in index order, and per (start, end)
/// the running sum on through the end group. Each is extended only as far as a query reaches,
/// which for the queries layout and paint make is the line itself, so the total work is linear
/// in the events per shaping instead of per query.
/// </para>
/// </remarks>
internal sealed class InlineEdgeIndex
{
    /// <summary>At or below this many events the sequential scan is used.</summary>
    internal const int LinearLimit = 32;

    /// <summary>Line starts (and line ends) cached before the cache is dropped and rebuilt.</summary>
    private const int CacheLimit = 4096;

    private readonly List<InlineBoundaryEvent> _events;
    private readonly bool[] _empty;
    private readonly int _sourceEnd;
    private readonly bool _anyNegative;

    // Null when the scan is used.
    private readonly int[]? _positions;
    private readonly Dictionary<int, RunningSum>? _lineStarts;
    private readonly Dictionary<(int Start, int End), RunningSum>? _lineEnds;

    // The caches are filled by queries, and a prepared render could be painted from more than
    // one thread; the lock is uncontended in the ordinary single-threaded pass.
    private readonly Lock _gate = new();

    public InlineEdgeIndex(InlineItem item)
    {
        _events = item.BoundaryEvents;
        _sourceEnd = item.OwnerText?.Length ?? 0;
        int count = _events.Count;

        // The scan looked up the first owner box carrying the event's owner.
        var emptyByOwner = new Dictionary<NodeId, bool>(item.OwnerBoxes.Count);
        foreach (InlineOwnerBox owner in item.OwnerBoxes)
        {
            emptyByOwner.TryAdd(owner.Owner, owner.Start == owner.End);
        }

        _empty = new bool[count];
        bool ordered = true;
        for (int i = 0; i < count; i++)
        {
            InlineBoundaryEvent evt = _events[i];
            _empty[i] = emptyByOwner.TryGetValue(evt.Owner, out bool empty) && empty;
            _anyNegative |= evt.Edge.Advance < 0f;
            if (i > 0 && evt.Position < _events[i - 1].Position)
            {
                ordered = false;
            }
        }

        if (count <= LinearLimit || !ordered)
        {
            return;
        }

        _positions = new int[count];
        for (int i = 0; i < count; i++)
        {
            _positions[i] = _events[i].Position;
        }

        _lineStarts = [];
        _lineEnds = [];
    }

    /// <summary>Whether the cached path is in use (for tests).</summary>
    internal bool Indexed => _positions is not null;

    private bool OnLine(int index, int lineStart, int lineEnd)
    {
        int position = _events[index].Position;
        if (_empty[index])
        {
            return (position >= lineStart && position < lineEnd)
                || (lineEnd == _sourceEnd && position == lineEnd);
        }

        return _events[index].IsStart
            ? position >= lineStart && position < lineEnd
            : position > lineStart && position <= lineEnd;
    }

    /// <summary>Sum of the advances on the line of the events with index below <paramref name="limit"/>.</summary>
    public float Advance(int lineStart, int lineEnd, int limit)
    {
        int count = Math.Min(limit, _events.Count);
        if (_positions is null || lineEnd < lineStart)
        {
            return ScanAdvance(lineStart, lineEnd, count, int.MaxValue);
        }

        int first = Lower(lineStart);
        int groupStart = Lower(lineEnd);
        if (count <= first)
        {
            return 0f;
        }

        lock (_gate)
        {
            return count <= groupStart
                ? StartSum(lineStart, first, count)
                : EndSum(lineStart, lineEnd, first, groupStart, count);
        }
    }

    /// <summary>Sum of the advances on the line of the events at or before <paramref name="position"/>.</summary>
    public float AdvanceBeforeText(int position, int lineStart, int lineEnd)
    {
        if (_positions is null || lineEnd < lineStart)
        {
            return ScanAdvance(lineStart, lineEnd, _events.Count, position);
        }

        if (position < lineStart)
        {
            return 0f;
        }

        // Events at or before position form an index prefix; below the line end that prefix
        // stops inside the line's first range.
        if (position >= lineEnd)
        {
            return Advance(lineStart, lineEnd, _events.Count);
        }

        int first = Lower(lineStart);
        int to = Upper(position);
        if (to <= first)
        {
            return 0f;
        }

        lock (_gate)
        {
            return StartSum(lineStart, first, to);
        }
    }

    /// <summary>Sum of the negative advances of the events at positions in [start, end].</summary>
    public float NegativeEdges(int start, int end)
    {
        if (!_anyNegative)
        {
            return 0f;
        }

        int from = 0;
        int to = _events.Count;
        if (_positions is not null)
        {
            from = Lower(start);
            to = Upper(end);
        }

        float sum = 0f;
        for (int i = from; i < to; i++)
        {
            InlineBoundaryEvent evt = _events[i];
            if (evt.Position >= start && evt.Position <= end)
            {
                sum += F32.Min(evt.Edge.Advance, 0f);
            }
        }

        return sum;
    }

    private float ScanAdvance(int lineStart, int lineEnd, int count, int maxPosition)
    {
        float sum = 0f;
        for (int i = 0; i < count; i++)
        {
            if (OnLine(i, lineStart, lineEnd) && _events[i].Position <= maxPosition)
            {
                sum += _events[i].Edge.Advance;
            }
        }

        return sum;
    }

    /// <summary>
    /// The scan's running sum over the line's first range, the events from index
    /// <paramref name="first"/> up to (not including) <paramref name="to"/>.
    /// </summary>
    private float StartSum(int lineStart, int first, int to)
    {
        if (!_lineStarts!.TryGetValue(lineStart, out RunningSum? running))
        {
            if (_lineStarts.Count >= CacheLimit)
            {
                _lineStarts.Clear();
                _lineEnds!.Clear();
            }

            running = new RunningSum(first, 0f);
            _lineStarts[lineStart] = running;
        }

        while (running.End < to)
        {
            int index = running.End;
            InlineBoundaryEvent evt = _events[index];

            // Past the start position every event counts; at it, a non-empty owner's end does not.
            bool counts = evt.Position > lineStart || _empty[index] || evt.IsStart;
            running.Append(counts ? running.Last + evt.Edge.Advance : running.Last);
        }

        return running.At(to);
    }

    /// <summary>The scan's sum over the whole first range, then the end group up to <paramref name="to"/>.</summary>
    private float EndSum(int lineStart, int lineEnd, int first, int groupStart, int to)
    {
        if (!_lineEnds!.TryGetValue((lineStart, lineEnd), out RunningSum? running))
        {
            if (_lineEnds.Count >= CacheLimit)
            {
                _lineEnds.Clear();
            }

            float before = groupStart <= first ? 0f : StartSum(lineStart, first, groupStart);
            running = new RunningSum(groupStart, before);
            _lineEnds[(lineStart, lineEnd)] = running;
        }

        int groupEnd = Math.Min(to, Upper(lineEnd));
        while (running.End < groupEnd)
        {
            int index = running.End;
            bool counts = _empty[index]
                ? lineEnd == _sourceEnd
                : !_events[index].IsStart && lineEnd > lineStart;
            running.Append(counts ? running.Last + _events[index].Edge.Advance : running.Last);
        }

        return running.At(Math.Min(to, running.End));
    }

    /// <summary>First event index whose position is at least <paramref name="position"/>.</summary>
    private int Lower(int position)
    {
        int[] positions = _positions!;
        int lo = 0;
        int hi = positions.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (positions[mid] < position)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>First event index whose position is greater than <paramref name="position"/>.</summary>
    private int Upper(int position)
    {
        int[] positions = _positions!;
        int lo = 0;
        int hi = positions.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (positions[mid] <= position)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// Running f32 sums over event indices [<see cref="Begin"/>, <see cref="End"/>): entry i is
    /// the sum before event <see cref="Begin"/> + i, starting from a given value.
    /// </summary>
    private sealed class RunningSum(int begin, float initial)
    {
        private float[] _sums = [initial, 0f, 0f, 0f];
        private int _length = 1;

        public int Begin { get; } = begin;

        public int End => Begin + _length - 1;

        public float Last => _sums[_length - 1];

        public float At(int index) => _sums[index - Begin];

        public void Append(float value)
        {
            if (_length == _sums.Length)
            {
                Array.Resize(ref _sums, _sums.Length * 2);
            }

            _sums[_length++] = value;
        }
    }
}
