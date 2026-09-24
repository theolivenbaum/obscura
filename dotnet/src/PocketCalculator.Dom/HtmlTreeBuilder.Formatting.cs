namespace PocketCalculator.Dom;

internal sealed partial class HtmlTreeBuilder
{
    /// <summary>
    /// An entry of the list of active formatting elements, which is a doubly linked list so the
    /// adoption agency can remove and insert entries in the middle in constant time. A marker
    /// has no element and opens a new <see cref="Segment"/>.
    /// </summary>
    private sealed class FmtEntry
    {
        public Rec? Element;
        public FmtEntry? Prev;
        public FmtEntry? Next;

        /// <summary>Increases along the list, so two entries can be ordered without a walk.</summary>
        public long Order;

        /// <summary>The segment an element entry belongs to, or the one a marker opens.</summary>
        public Segment Segment = null!;

        /// <summary>For a marker: the segment that was current before it.</summary>
        public Segment? Outer;

        /// <summary>For an element: a hash of its tag and attributes, for the Noah's Ark clause.</summary>
        public int Sig;
    }

    /// <summary>
    /// The entries between one marker and the next: how many of each tag, and the entries by
    /// signature. Both answer questions the specification asks by walking back to the last
    /// marker, which a long run of distinct formatting elements made quadratic.
    /// </summary>
    private sealed class Segment
    {
        public readonly int[] TagCounts = new int[(int)HtmlTag.Count];
        public readonly Dictionary<int, List<FmtEntry>> BySig = [];
    }

    private const long OrderGap = 1L << 36;

    private FmtEntry? _afeHead;
    private FmtEntry? _afeTail;
    private Segment _segment = new();

    private void AfeAppend(FmtEntry entry)
    {
        entry.Order = _afeTail is null ? 0 : _afeTail.Order + OrderGap;
        entry.Prev = _afeTail;
        entry.Next = null;
        if (_afeTail is null)
        {
            _afeHead = entry;
        }
        else
        {
            _afeTail.Next = entry;
        }

        _afeTail = entry;
    }

    private void AfeInsertAfter(FmtEntry anchor, FmtEntry entry)
    {
        if (anchor.Next is not { } next)
        {
            AfeAppend(entry);
            return;
        }

        if (next.Order - anchor.Order < 2)
        {
            Renumber();
        }

        entry.Order = anchor.Order + ((next.Order - anchor.Order) / 2);
        entry.Prev = anchor;
        entry.Next = next;
        anchor.Next = entry;
        next.Prev = entry;
    }

    private void Renumber()
    {
        long order = 0;
        for (var e = _afeHead; e is not null; e = e.Next)
        {
            e.Order = order;
            order += OrderGap;
        }
    }

    private void AfeUnlink(FmtEntry entry)
    {
        if (entry.Prev is { } prev)
        {
            prev.Next = entry.Next;
        }
        else
        {
            _afeHead = entry.Next;
        }

        if (entry.Next is { } next)
        {
            next.Prev = entry.Prev;
        }
        else
        {
            _afeTail = entry.Prev;
        }

        entry.Prev = entry.Next = null;
    }

    /// <summary>Put <paramref name="replacement"/> where <paramref name="entry"/> is.</summary>
    private void AfeReplace(FmtEntry entry, FmtEntry replacement)
    {
        replacement.Order = entry.Order;
        replacement.Prev = entry.Prev;
        replacement.Next = entry.Next;
        if (entry.Prev is { } prev)
        {
            prev.Next = replacement;
        }
        else
        {
            _afeHead = replacement;
        }

        if (entry.Next is { } next)
        {
            next.Prev = replacement;
        }
        else
        {
            _afeTail = replacement;
        }

        entry.Prev = entry.Next = null;
    }

    private void PushMarker()
    {
        var marker = new FmtEntry { Outer = _segment, Segment = new Segment() };
        AfeAppend(marker);
        _segment = marker.Segment;
    }

    private void ClearAfeToLastMarker()
    {
        while (_afeTail is { } entry)
        {
            AfeUnlink(entry);
            if (entry.Element is null)
            {
                _segment = entry.Outer!;
                return;
            }

            entry.Element.Fmt = null;
        }

        _segment = new Segment();
    }

    /// <summary>
    /// The last element with <paramref name="tag"/> between the end of the list and the last
    /// marker, or null. The segment's count answers "none" without a walk.
    /// </summary>
    private Rec? LastFormattingElement(HtmlTag tag)
    {
        if (_segment.TagCounts[(int)tag] == 0)
        {
            return null;
        }

        for (var e = _afeTail; e is not null; e = e.Prev)
        {
            if (e.Element is not { } element)
            {
                return null;
            }

            if (element.IsHtml(tag))
            {
                return element;
            }
        }

        return null;
    }

    private static int Signature(Rec element, List<Attribute> attrs)
    {
        // Order-independent over the attributes, since the comparison is as sets.
        var sum = 0;
        foreach (var attr in attrs)
        {
            sum += HashCode.Combine(attr.Name, attr.Value);
        }

        return HashCode.Combine(element.Local, attrs.Count, sum);
    }

    private void PushFormatting(Rec element, List<Attribute> tokenAttrs)
    {
        var sig = Signature(element, tokenAttrs);

        // Noah's Ark: at most three identical entries after the last marker. Identical entries
        // share a signature, so only those are compared.
        if (_segment.BySig.TryGetValue(sig, out var same))
        {
            FmtEntry? earliest = null;
            var identical = 0;
            foreach (var other in same)
            {
                var otherElement = other.Element!;
                if (otherElement.Tag == element.Tag
                    && string.Equals(otherElement.Local, element.Local, StringComparison.Ordinal)
                    && SameAttributes(otherElement.TokenAttrs!, tokenAttrs))
                {
                    identical++;
                    if (earliest is null || other.Order < earliest.Order)
                    {
                        earliest = other;
                    }
                }
            }

            if (identical >= 3)
            {
                RemoveFromAfe(earliest!.Element!);
            }
        }

        element.TokenAttrs = CloneAttributes(tokenAttrs);
        var entry = new FmtEntry { Element = element, Segment = _segment, Sig = sig };
        element.Fmt = entry;
        AddToSegment(entry);
        AfeAppend(entry);
    }

    private static void AddToSegment(FmtEntry entry)
    {
        var segment = entry.Segment;
        segment.TagCounts[(int)entry.Element!.Tag]++;
        if (!segment.BySig.TryGetValue(entry.Sig, out var list))
        {
            list = new List<FmtEntry>(3);
            segment.BySig[entry.Sig] = list;
        }

        list.Add(entry);
    }

    private static void RemoveFromSegment(FmtEntry entry, Rec element)
    {
        var segment = entry.Segment;
        segment.TagCounts[(int)element.Tag]--;
        if (segment.BySig.TryGetValue(entry.Sig, out var list))
        {
            list.Remove(entry);
            if (list.Count == 0)
            {
                segment.BySig.Remove(entry.Sig);
            }
        }
    }

    private void RemoveFromAfe(Rec element)
    {
        if (element.Fmt is not { } entry)
        {
            return;
        }

        RemoveFromSegment(entry, element);
        AfeUnlink(entry);
        element.Fmt = null;
    }

    private static bool SameAttributes(List<Attribute> a, List<Attribute> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var x in a)
        {
            var found = false;
            foreach (var y in b)
            {
                if (x.Name.Equals(y.Name))
                {
                    found = string.Equals(x.Value, y.Value, StringComparison.Ordinal);
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static List<Attribute> CloneAttributes(List<Attribute> attrs)
    {
        var copy = new List<Attribute>(attrs.Count);
        foreach (var attr in attrs)
        {
            copy.Add(attr.Clone());
        }

        return copy;
    }

    private void ReconstructActiveFormattingElements()
    {
        var entry = _afeTail;
        if (entry is null || entry.Element is null || entry.Element.Index >= 0)
        {
            return;
        }

        // Rewind to the earliest entry after the last marker or open element.
        while (entry.Prev is { } prev && prev.Element is { Index: < 0 })
        {
            entry = prev;
        }

        for (; entry is not null; entry = entry.Next)
        {
            var old = entry.Element!;
            var created = InsertHtmlElement(old.Local, CloneAttributes(old.TokenAttrs!));
            created.TokenAttrs = old.TokenAttrs;
            old.Fmt = null;
            created.Fmt = entry;
            entry.Element = created;
        }
    }
}
