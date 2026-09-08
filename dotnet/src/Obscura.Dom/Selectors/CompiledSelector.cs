namespace Obscura.Dom.Selectors;

/// <summary>
/// The kind of rightmost simple selector used to index a rule for fast lookup, ordered by how
/// selective it is (universal least, root most).
/// </summary>
public enum SelectorKeyKind
{
    Universal = 0,
    Local = 1,
    Attribute = 2,
    Class = 3,
    Id = 4,
    /// <summary>
    /// The document element. <c>:root</c> can match only this one subject, so it is substantially
    /// more selective than the universal bucket despite having no id/class/tag token.
    /// </summary>
    Root = 5,
}

/// <summary>
/// The rightmost simple selector used to index a rule for fast lookup. A rule is only tested
/// against elements that carry its key.
/// </summary>
public readonly record struct SelectorKey(SelectorKeyKind Kind, string Value)
{
    public static SelectorKey Universal { get; } = new(SelectorKeyKind.Universal, "");

    public static SelectorKey Root { get; } = new(SelectorKeyKind.Root, "");

    public static SelectorKey Id(string value) => new(SelectorKeyKind.Id, value);

    public static SelectorKey Class(string value) => new(SelectorKeyKind.Class, value);

    public static SelectorKey Attribute(string value) => new(SelectorKeyKind.Attribute, value);

    public static SelectorKey Local(string value) => new(SelectorKeyKind.Local, value);

    internal int Rank => (int)Kind;

    public bool Equals(SelectorKey other) =>
        Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine((int)Kind, StringComparer.Ordinal.GetHashCode(Value));

    public override string ToString() =>
        Kind is SelectorKeyKind.Universal or SelectorKeyKind.Root ? Kind.ToString() : $"{Kind}({Value})";
}

/// <summary>A parsed selector plus its cached specificity, subject keys and ancestor hashes.</summary>
public sealed class CompiledSelector
{
    internal CompiledSelector(Selector selector, uint specificity, SelectorKey[] keys, AncestorHashes hashes)
    {
        Selector = selector;
        Specificity = specificity;
        Keys = keys;
        Hashes = hashes;
    }

    internal Selector Selector { get; }

    internal AncestorHashes Hashes { get; }

    private SelectorKey[] Keys { get; }

    public uint Specificity { get; }

    /// <summary>
    /// Whether this selector is eligible to match a featureless shadow host. Shadow-tree
    /// stylesheets use this to retain only <c>:host</c> selectors when matching against the host in
    /// their own tree scope.
    /// </summary>
    public bool MatchesFeaturelessHost() =>
        (Selector.MatchesFeaturelessHostSelectorOrPseudoElement() & FeaturelessHostMatches.ForHost) != 0;

    /// <summary>
    /// Whether this selector contains the <c>::slotted()</c> pseudo-element and must be collected
    /// at a slot-assignment boundary.
    /// </summary>
    public bool IsSlotted() => Selector.IsSlotted;

    /// <summary>
    /// The one bucket usable by legacy selector indexes. Selectors whose subject has multiple
    /// disjoint alternatives deliberately report Universal; use <see cref="CandidateKeys"/> to opt
    /// into multi-bucket indexing.
    /// </summary>
    public SelectorKey Key =>
        // Existing single-bucket consumers must keep treating disjoint alternatives as universal
        // until they opt into candidate keys.
        Keys.Length == 1 ? Keys[0] : SelectorKey.Universal;

    /// <summary>
    /// Deduplicated buckets that together cover every element this selector can match. A consumer
    /// must insert the rule in every returned bucket and deduplicate rule candidates before
    /// matching, since one element can carry keys from more than one alternative.
    /// </summary>
    public IReadOnlyList<SelectorKey> CandidateKeys => Keys;

    // ------------------------------------------------------------------ subject keys

    /// <summary>
    /// Candidate buckets for a selector's rightmost compound. A single ordinary id/class/local
    /// selector keeps the allocation-free common path. A pure <c>:is()</c>/<c>:where()</c> subject
    /// returns the deduplicated union of its arms, but only when every arm has a concrete key; one
    /// universal/pseudo-only arm makes the whole set universal because that arm can match an
    /// element carrying none of the other keys.
    ///
    /// When an outer compound key exists, disjoint functional-pseudo keys replace it only if every
    /// arm is strictly more selective. This is the same conservative contract as Gecko's
    /// <c>SelectorMap::find_bucket</c>: for <c>.control:is(button, #save)</c> keep <c>.control</c>,
    /// while <c>.control:is(#save, #cancel)</c> may use the two id buckets.
    /// </summary>
    internal static List<SelectorKey> SubjectKeys(Selector selector)
    {
        string? local = null;
        string? attribute = null;
        string? className = null;
        string? id = null;
        var root = false;
        var disjointSets = new List<List<SelectorKey>>();

        foreach (var component in selector.Subject.Components)
        {
            switch (component)
            {
                case IdComponent value:
                    id = value.Id;
                    break;
                case RootComponent:
                    root = true;
                    break;
                case ClassComponent value:
                    className = value.ClassName;
                    break;
                case LocalNameComponent value:
                    local = value.LowerName;
                    break;
                case AttributeComponent value:
                    attribute = value.LocalNameLower;
                    break;
                case IsComponent isComponent:
                    disjointSets.Add(AlternativeKeys(isComponent.List));
                    break;
                case WhereComponent where:
                    disjointSets.Add(AlternativeKeys(where.List));
                    break;
            }
        }

        SelectorKey direct;
        if (root)
        {
            direct = SelectorKey.Root;
        }
        else if (id is not null)
        {
            direct = SelectorKey.Id(id);
        }
        else if (className is not null)
        {
            direct = SelectorKey.Class(className);
        }
        else if (attribute is not null)
        {
            direct = SelectorKey.Attribute(attribute);
        }
        else if (local is not null)
        {
            direct = SelectorKey.Local(local);
        }
        else
        {
            direct = SelectorKey.Universal;
        }

        var directRank = direct.Rank;
        List<SelectorKey>? best = null;
        foreach (var keys in disjointSets)
        {
            var lowest = int.MaxValue;
            var disqualified = false;
            foreach (var key in keys)
            {
                if (key.Rank <= directRank)
                {
                    disqualified = true;
                    break;
                }

                lowest = Math.Min(lowest, key.Rank);
            }

            if (disqualified)
            {
                continue;
            }

            var minRank = keys.Count == 0 ? 0 : lowest;
            if (best is null)
            {
                best = keys;
                continue;
            }

            var currentMin = int.MaxValue;
            foreach (var key in best)
            {
                currentMin = Math.Min(currentMin, key.Rank);
            }

            if (best.Count == 0)
            {
                currentMin = 0;
            }

            if (minRank > currentMin || (minRank == currentMin && keys.Count < best.Count))
            {
                best = keys;
            }
        }

        return best ?? [direct];
    }

    private static List<SelectorKey> AlternativeKeys(SelectorList list)
    {
        var keys = new List<SelectorKey>();
        var universal = false;
        foreach (var alternative in list.Selectors)
        {
            var alternativeKeys = SubjectKeys(alternative);
            foreach (var key in alternativeKeys)
            {
                if (key.Kind == SelectorKeyKind.Universal)
                {
                    universal = true;
                    break;
                }
            }

            if (universal)
            {
                break;
            }

            foreach (var key in alternativeKeys)
            {
                if (!keys.Contains(key))
                {
                    keys.Add(key);
                }
            }
        }

        if (universal || keys.Count == 0)
        {
            keys.Clear();
            keys.Add(SelectorKey.Universal);
        }

        return keys;
    }
}
