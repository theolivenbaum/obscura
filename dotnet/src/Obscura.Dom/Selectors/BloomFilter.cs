namespace Obscura.Dom.Selectors;

/// <summary>
/// A counting Bloom filter with 8-bit counters, the same structure browsers use to fast-reject
/// selector matches during a treewalk.
/// </summary>
public sealed class BloomFilter
{
    public const uint BloomHashMask = 0x00ffffff;

    private const int KeySize = 12;
    private const int ArraySize = 1 << KeySize;
    private const uint KeyMask = (1 << KeySize) - 1;

    private readonly byte[] _counters = new byte[ArraySize];

    private static uint Hash1(uint hash) => hash & KeyMask;

    private static uint Hash2(uint hash) => (hash >> KeySize) & KeyMask;

    private void Adjust(uint slot, bool increment)
    {
        ref var counter = ref _counters[slot];
        if (counter == 0xff)
        {
            // Saturated: this slot can never be cleared again, which only costs false positives.
            return;
        }

        if (increment)
        {
            counter++;
        }
        else if (counter != 0)
        {
            counter--;
        }
    }

    public void InsertHash(uint hash)
    {
        Adjust(Hash1(hash), true);
        Adjust(Hash2(hash), true);
    }

    public void RemoveHash(uint hash)
    {
        Adjust(Hash1(hash), false);
        Adjust(Hash2(hash), false);
    }

    /// <summary>
    /// Whether the filter might contain an item with the given hash. This can return true for items
    /// that are not in the filter, but never false for items that are.
    /// </summary>
    public bool MightContainHash(uint hash) =>
        _counters[Hash1(hash)] != 0 && _counters[Hash2(hash)] != 0;

    public void Clear() => Array.Clear(_counters);
}

/// <summary>
/// The hashes of a selector's ancestor compounds, used to fast-reject a rule against the ancestor
/// bloom filter before any combinator walking happens.
/// </summary>
public sealed class AncestorHashes
{
    private const int MaxHashes = 4;

    private readonly uint[] _hashes;

    private AncestorHashes(uint[] hashes)
    {
        _hashes = hashes;
    }

    public static AncestorHashes Create(Selector selector, QuirksMode quirksMode)
    {
        var hashes = new List<uint>(MaxHashes);
        CollectAncestorHashes(selector, quirksMode, hashes);
        return new AncestorHashes([.. hashes]);
    }

    public bool MayMatch(BloomFilter filter)
    {
        foreach (var hash in _hashes)
        {
            if (!filter.MightContainHash(hash))
            {
                return false;
            }
        }

        return true;
    }

    private static void CollectAncestorHashes(Selector selector, QuirksMode quirksMode, List<uint> hashes)
    {
        // Walk left from the subject compound, collecting only compounds reached across an ancestor
        // (descendant/child) combinator. Compounds behind a sibling combinator describe siblings of
        // an ancestor, not ancestors, so they are skipped, but the walk continues past them.
        var index = 0;
        while (index < selector.Combinators.Length)
        {
            var combinator = selector.Combinators[index];
            index++;
            if (combinator is not (Combinator.Descendant or Combinator.Child))
            {
                continue;
            }

            if (!CollectCompoundHashes(selector.Compounds[index], quirksMode, hashes))
            {
                return;
            }
        }
    }

    /// <summary>Returns false once the hash budget is exhausted.</summary>
    private static bool CollectCompoundHashes(
        CompoundSelector compound,
        QuirksMode quirksMode,
        List<uint> hashes)
    {
        foreach (var component in compound.Components)
        {
            uint hash;
            switch (component)
            {
                case LocalNameComponent localName:
                    // Only insert the local name if it is all lowercase; otherwise we would need to
                    // test both hashes.
                    if (!string.Equals(localName.Name, localName.LowerName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    hash = PrecomputedHash(localName.Name);
                    break;

                // In quirks mode, class and id selectors match case-insensitively, so just avoid
                // inserting them into the filter.
                case IdComponent id when quirksMode != QuirksMode.Quirks:
                    hash = PrecomputedHash(id.Id);
                    break;

                case ClassComponent className when quirksMode != QuirksMode.Quirks:
                    hash = PrecomputedHash(className.ClassName);
                    break;

                case IsComponent isComponent when isComponent.List.Count == 1:
                    // :is and :where OR their selectors, so nothing can go in the filter when there
                    // is more than one arm.
                    if (!CollectSingleArm(isComponent.List.Selectors[0], quirksMode, hashes))
                    {
                        return false;
                    }

                    continue;

                case WhereComponent where when where.List.Count == 1:
                    if (!CollectSingleArm(where.List.Selectors[0], quirksMode, hashes))
                    {
                        return false;
                    }

                    continue;

                default:
                    continue;
            }

            hashes.Add(hash & BloomFilter.BloomHashMask);
            if (hashes.Count == MaxHashes)
            {
                return false;
            }
        }

        return true;
    }

    private static bool CollectSingleArm(Selector selector, QuirksMode quirksMode, List<uint> hashes)
    {
        var before = hashes.Count;
        CollectAncestorHashes(selector, quirksMode, hashes);
        return hashes.Count < MaxHashes || hashes.Count == before;
    }

    /// <summary>
    /// The stable string hash the reference implementation uses for selector identifiers. Selector
    /// side and element side must agree; nothing else depends on the exact value.
    /// </summary>
    internal static uint PrecomputedHash(string value)
    {
        uint h = 5381;
        foreach (var c in value)
        {
            h = unchecked((h * 33) + c);
        }

        return h;
    }
}
