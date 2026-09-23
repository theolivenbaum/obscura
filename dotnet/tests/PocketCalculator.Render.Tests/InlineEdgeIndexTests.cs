// InlineEdgeIndex answers line-edge sums from cached running sums instead of a scan. The
// answers move glyphs and decide line breaks, so they must be the scan's f32 sums bit for bit,
// in the scan's order: these compare against a copy of that scan over random nested and
// sibling boundary events with edges that are not short binary fractions.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class InlineEdgeIndexTests
{
    /// <summary>The scan the index replaced (InlineGeometry before the change).</summary>
    private static class Reference
    {
        public static bool OnLine(InlineItem item, InlineBoundaryEvent evt, int lineStart, int lineEnd)
        {
            int sourceEnd = item.OwnerText?.Length ?? 0;
            bool empty = false;
            foreach (InlineOwnerBox owner in item.OwnerBoxes)
            {
                if (owner.Owner == evt.Owner)
                {
                    empty = owner.Start == owner.End;
                    break;
                }
            }

            if (empty)
            {
                return (evt.Position >= lineStart && evt.Position < lineEnd)
                    || (lineEnd == sourceEnd && evt.Position == lineEnd);
            }

            return evt.IsStart
                ? evt.Position >= lineStart && evt.Position < lineEnd
                : evt.Position > lineStart && evt.Position <= lineEnd;
        }

        public static float BeforeEvent(InlineItem item, int eventIndex, int lineStart, int lineEnd)
        {
            float sum = 0f;
            for (int i = 0; i < eventIndex && i < item.BoundaryEvents.Count; i++)
            {
                InlineBoundaryEvent evt = item.BoundaryEvents[i];
                if (OnLine(item, evt, lineStart, lineEnd))
                {
                    sum += evt.Edge.Advance;
                }
            }

            return sum;
        }

        public static float BeforeText(InlineItem item, int position, int lineStart, int lineEnd)
        {
            float sum = 0f;
            foreach (InlineBoundaryEvent evt in item.BoundaryEvents)
            {
                if (OnLine(item, evt, lineStart, lineEnd) && evt.Position <= position)
                {
                    sum += evt.Edge.Advance;
                }
            }

            return sum;
        }

        public static float Negative(InlineItem item, int start, int end)
        {
            float sum = 0f;
            foreach (InlineBoundaryEvent evt in item.BoundaryEvents)
            {
                if (evt.Position >= start && evt.Position <= end)
                {
                    sum += F32.Min(evt.Edge.Advance, 0f);
                }
            }

            return sum;
        }
    }

    private static readonly float[] Edges = [0f, 1f, 1.3f, 4.8f, 2.4f, 0.1f, 16f / 3f, -2.3f, 0.7f, 3.14159f];

    /// <summary>
    /// Random inline owners over a text of <paramref name="length"/>: nesting, siblings, empty
    /// owners, several events at one position, and owners that close at the very end.
    /// </summary>
    private static InlineItem RandomItem(Random random, int owners, int length)
    {
        List<InlineBoundaryEvent> events = [];
        List<InlineOwnerBox> boxes = [];
        List<(NodeId Owner, int Start, InlineEdge End, int StartEvent)> open = [];
        int position = 0;
        uint next = 1;
        InlineEdge Edge() => new(
            Edges[random.Next(Edges.Length)],
            Edges[random.Next(Edges.Length)],
            Edges[random.Next(Edges.Length)]);

        while (boxes.Count + open.Count < owners || open.Count > 0)
        {
            int action = random.Next(6);
            if (action < 2 && boxes.Count + open.Count < owners)
            {
                var owner = NodeId.New(next++);
                InlineEdge start = Edge();
                open.Add((owner, position, Edge(), events.Count));
                events.Add(new InlineBoundaryEvent(owner, position, true, start));
            }
            else if (action < 4 && open.Count > 0)
            {
                (NodeId owner, int start, InlineEdge end, int startEvent) = open[^1];
                open.RemoveAt(open.Count - 1);
                int endEvent = events.Count;
                events.Add(new InlineBoundaryEvent(owner, position, false, end));
                boxes.Add(new InlineOwnerBox(owner, start, position, default, end, startEvent, endEvent, default));
            }
            else if (position < length && random.Next(3) != 0)
            {
                position = Math.Min(length, position + random.Next(1, 4));
            }
            else if (boxes.Count + open.Count >= owners && open.Count > 0 && position < length)
            {
                position = length;
            }
        }

        return new InlineItem
        {
            Buffer = new TextBuffer(new TextMetrics(16f, 20f)),
            OwnerText = new string('x', length),
            OwnerBoxes = boxes,
            BoundaryEvents = events,
        };
    }

    [Theory]
    [InlineData(1, 10, 20)]
    [InlineData(2, 40, 60)]
    [InlineData(3, 200, 150)]
    [InlineData(4, 600, 400)]
    [InlineData(5, 600, 40)]
    public void CachedSumsAreTheScansSumsBitForBit(int seed, int owners, int length)
    {
        var random = new Random(seed);
        for (int round = 0; round < (owners > 300 ? 4 : 20); round++)
        {
            InlineItem item = RandomItem(random, owners, length);
            int sourceEnd = length;
            int count = item.BoundaryEvents.Count;
            if (count > InlineEdgeIndex.LinearLimit)
            {
                Assert.True(item.EdgeIndex.Indexed);
            }

            // Lines as layout asks for them, plus arbitrary ranges, several times over so the
            // caches are exercised both cold and warm.
            List<(int Start, int End)> lines = [];
            int at = 0;
            while (at < sourceEnd)
            {
                int end = Math.Min(sourceEnd, at + random.Next(1, 30));
                lines.Add((at, end));
                at = end;
            }

            lines.Add((sourceEnd, sourceEnd));
            for (int extra = 0; extra < 40; extra++)
            {
                int a = random.Next(sourceEnd + 1);
                int b = random.Next(sourceEnd + 1);
                lines.Add((Math.Min(a, b), Math.Max(a, b)));
            }

            for (int pass = 0; pass < 2; pass++)
            {
                foreach ((int start, int end) in lines)
                {
                    Assert.Equal(
                        Bits(Reference.BeforeEvent(item, count, start, end)),
                        Bits(InlineGeometry.LineEdgeAdvance(item, start, end)));
                    for (int probe = 0; probe < 6; probe++)
                    {
                        int limit = random.Next(count + 2);
                        Assert.Equal(
                            Bits(Reference.BeforeEvent(item, limit, start, end)),
                            Bits(InlineGeometry.LineAdvanceBeforeEvent(item, limit, start, end)));
                        int text = random.Next(-1, sourceEnd + 2);
                        Assert.Equal(
                            Bits(Reference.BeforeText(item, text, start, end)),
                            Bits(InlineGeometry.LineAdvanceBeforeText(item, text, start, end)));
                    }

                    Assert.Equal(
                        Bits(Reference.Negative(item, start, end)),
                        Bits(item.EdgeIndex.NegativeEdges(start, end)));
                }
            }
        }
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}
