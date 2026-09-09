namespace Obscura.Render.Css;

/// <summary>
/// The part of the tree whose selector match may change when a dependency on
/// one element changes. Multiple bits can be present for selectors which use
/// the same key in more than one compound.
/// </summary>
public readonly record struct InvalidationReaches(byte Bits)
{
    public static readonly InvalidationReaches None = new(0);
    public static readonly InvalidationReaches Self = new(1 << 0);
    public static readonly InvalidationReaches Descendants = new(1 << 1);
    public static readonly InvalidationReaches Siblings = new(1 << 2);

    /// <summary>
    /// The selector needs a correctness-first fallback which phase 2 must not
    /// narrow to a local traversal.
    /// </summary>
    public static readonly InvalidationReaches Conservative = new(1 << 3);

    public bool Contains(InvalidationReaches other) => (Bits & other.Bits) == other.Bits;

    public InvalidationReaches Union(InvalidationReaches other) => new((byte)(Bits | other.Bits));
}

/// <summary>One compiled rule's dependency on a selector key.</summary>
public readonly record struct InvalidationDependency(int RuleOrder, InvalidationReaches Reaches);

/// <summary>
/// A cheap positive key from the compound which anchors one <c>:has()</c>.
/// </summary>
/// <remarks>
/// This is only an early rejection filter. Compounds without a direct key
/// remain unkeyed rather than borrowing a key from <c>:is()</c>/<c>:not()</c>,
/// whose boolean structure cannot be represented by one key without false
/// negatives.
/// </remarks>
public readonly record struct RelationalSelectorKey(RelationalSelectorKeyKind Kind, string Value)
{
    public static RelationalSelectorKey Id(string value) => new(RelationalSelectorKeyKind.Id, value);

    public static RelationalSelectorKey Class(string value) => new(RelationalSelectorKeyKind.Class, value);

    public static RelationalSelectorKey Attribute(string value) => new(RelationalSelectorKeyKind.Attribute, value);

    public static RelationalSelectorKey LocalName(string value) => new(RelationalSelectorKeyKind.LocalName, value);
}

public enum RelationalSelectorKeyKind
{
    Id,
    Class,
    Attribute,
    LocalName,
}

/// <summary>
/// The element view the invalidation map needs from the DOM.
/// </summary>
/// <remarks>
/// SHARED SEAM: the Rust original takes <c>&amp;DomTree</c> plus a
/// <c>NodeId</c>. The DOM port lives in <c>Obscura.Dom</c>, so the predicates
/// here take this narrow view instead. The coordinator should implement it on
/// the ported tree's node handle.
/// </remarks>
public interface ICssElementView
{
    /// <summary>False for text, comment and document nodes.</summary>
    bool IsElement { get; }

    /// <summary>The element's lowercase-insensitive local name.</summary>
    string LocalName { get; }

    /// <summary>Unqualified HTML attribute lookup, as <c>Node::get_attribute</c>.</summary>
    string? GetAttribute(string name);

    /// <summary>Every attribute local name present on the element.</summary>
    IReadOnlyList<string> AttributeNames { get; }

    /// <summary>Whether the owning document is in quirks mode.</summary>
    bool IsQuirks { get; }
}

/// <summary>Invalidation metadata for one <c>:has()</c> occurrence.</summary>
/// <remarks>
/// Gecko models the selector inside <c>:has()</c> as an upward dependency chain
/// (parent/ancestors/previous siblings), then resumes the ordinary selector path
/// outside the anchor. Obscura stores the smaller information needed by its
/// whole-subtree cascade: an optional anchor key, the outward reach, and whether
/// a key-independent child-list side effect can change the match.
/// </remarks>
public sealed record RelationalInvalidation
{
    public required int RuleOrder { get; init; }

    internal RelationalSelectorKey? AnchorKey { get; init; }

    internal IReadOnlyList<RelationalSelectorKey> RelativeKeys { get; init; } = [];

    public required InvalidationReaches AnchorReaches { get; init; }

    public required bool UnkeyedSubject { get; init; }

    public required bool SiblingSideEffect { get; init; }

    public required bool StructuralSideEffect { get; init; }

    public required bool TextSideEffect { get; init; }

    public required bool UnrepresentableOuterPath { get; init; }

    public bool AnchorMayMatch(ICssElementView node) =>
        CssInvalidationKeys.KeyMayMatch(AnchorKey, node);

    public bool RelativePathMayMatch(ICssElementView node)
    {
        if (!node.IsElement)
        {
            return false;
        }

        var quirks = node.IsQuirks;
        foreach (var key in RelativeKeys)
        {
            switch (key.Kind)
            {
                case RelationalSelectorKeyKind.Id:
                    if (node.GetAttribute("id") is { } id && Matches(id, key.Value, quirks))
                    {
                        return true;
                    }

                    break;
                case RelationalSelectorKeyKind.Class:
                    if (node.GetAttribute("class") is { } classes)
                    {
                        foreach (var actual in classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (Matches(actual, key.Value, quirks))
                            {
                                return true;
                            }
                        }
                    }

                    break;
                case RelationalSelectorKeyKind.Attribute:
                    foreach (var attribute in node.AttributeNames)
                    {
                        if (CssText.EqualsAscii(attribute, key.Value))
                        {
                            return true;
                        }
                    }

                    break;
                case RelationalSelectorKeyKind.LocalName:
                    if (CssText.EqualsAscii(node.LocalName, key.Value))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool Matches(string actual, string expected, bool quirks) =>
        string.Equals(actual, expected, StringComparison.Ordinal)
        || (quirks && CssText.EqualsAscii(actual, expected));

    public bool Equals(RelationalInvalidation? other) =>
        other is not null
        && RuleOrder == other.RuleOrder
        && Nullable.Equals(AnchorKey, other.AnchorKey)
        && RelativeKeys.SequenceEqual(other.RelativeKeys)
        && AnchorReaches == other.AnchorReaches
        && UnkeyedSubject == other.UnkeyedSubject
        && SiblingSideEffect == other.SiblingSideEffect
        && StructuralSideEffect == other.StructuralSideEffect
        && TextSideEffect == other.TextSideEffect
        && UnrepresentableOuterPath == other.UnrepresentableOuterPath;

    public override int GetHashCode() => HashCode.Combine(RuleOrder, AnchorKey, AnchorReaches, UnkeyedSubject);
}

public sealed record StructuralInvalidation
{
    public required int RuleOrder { get; init; }

    public required string State { get; init; }

    internal RelationalSelectorKey? SubjectKey { get; init; }

    public required InvalidationReaches Reaches { get; init; }

    public required bool InsideRelational { get; init; }

    public bool SubjectMayMatch(ICssElementView node) => CssInvalidationKeys.KeyMayMatch(SubjectKey, node);
}

internal static class CssInvalidationKeys
{
    public static bool KeyMayMatch(RelationalSelectorKey? key, ICssElementView node)
    {
        if (!node.IsElement)
        {
            return false;
        }

        if (key is not { } expected)
        {
            return true;
        }

        var quirks = node.IsQuirks;
        return expected.Kind switch
        {
            RelationalSelectorKeyKind.Id =>
                node.GetAttribute("id") is { } id && Matches(id, expected.Value, quirks),
            RelationalSelectorKeyKind.Class =>
                node.GetAttribute("class") is { } classes
                && classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Any(actual => Matches(actual, expected.Value, quirks)),
            RelationalSelectorKeyKind.Attribute => node.GetAttribute(expected.Value) is not null,
            RelationalSelectorKeyKind.LocalName => CssText.EqualsAscii(node.LocalName, expected.Value),
            _ => false,
        };
    }

    private static bool Matches(string actual, string expected, bool quirks) =>
        string.Equals(actual, expected, StringComparison.Ordinal)
        || (quirks && CssText.EqualsAscii(actual, expected));
}

/// <summary>
/// Selector dependencies retained alongside the compiled stylesheet.
/// </summary>
/// <remarks>
/// This follows Gecko's conservative invalidation-map shape: live mutations look
/// up the changed id/class/attribute/local-name/state and receive one or more
/// traversal reaches. The renderer uses those reaches to retain clean computed
/// styles, with a full-cascade fallback for unrepresentable paths.
/// </remarks>
public sealed class InvalidationMap
{
    private static readonly InvalidationDependency[] Empty = [];

    private readonly Dictionary<string, List<InvalidationDependency>> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<InvalidationDependency>> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<InvalidationDependency>> _attributes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<InvalidationDependency>> _localNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<InvalidationDependency>> _states = new(StringComparer.Ordinal);
    private readonly List<int> _conservativeRuleOrders = [];
    private readonly List<int> _relationalRuleOrders = [];
    private readonly List<int> _unkeyedRelationalRuleOrders = [];
    private readonly List<RelationalInvalidation> _relationalInvalidations = [];
    private readonly List<StructuralInvalidation> _structuralInvalidations = [];

    internal bool AdjacentSiblingSelectors;
    internal bool GeneralSiblingSelectors;
    internal bool UnkeyedSiblingSelectors;

    public IReadOnlyList<InvalidationDependency> IdDependencies(string id) =>
        _ids.TryGetValue(id, out var dependencies) ? dependencies : Empty;

    public IReadOnlyList<InvalidationDependency> ClassDependencies(string className) =>
        _classes.TryGetValue(className, out var dependencies) ? dependencies : Empty;

    public IReadOnlyList<InvalidationDependency> AttributeDependencies(string attribute) =>
        _attributes.TryGetValue(CssText.AsciiLower(attribute), out var dependencies) ? dependencies : Empty;

    public IReadOnlyList<InvalidationDependency> LocalNameDependencies(string localName) =>
        _localNames.TryGetValue(CssText.AsciiLower(localName), out var dependencies) ? dependencies : Empty;

    public IReadOnlyList<InvalidationDependency> StateDependencies(string state) =>
        _states.TryGetValue(CssText.AsciiLower(state), out var dependencies) ? dependencies : Empty;

    public IReadOnlyList<int> ConservativeRuleOrders => _conservativeRuleOrders;

    public bool RequiresConservativeInvalidation => _conservativeRuleOrders.Count != 0;

    public bool IsRelationalRule(int ruleOrder) => _relationalRuleOrders.Contains(ruleOrder);

    public bool HasUnkeyedRelationalRules => _unkeyedRelationalRuleOrders.Count != 0;

    public IReadOnlyList<RelationalInvalidation> RelationalInvalidations => _relationalInvalidations;

    public List<StructuralInvalidation> StructuralInvalidations(string state) =>
        _structuralInvalidations
            .Where(invalidation => string.Equals(invalidation.State, state, StringComparison.Ordinal))
            .ToList();

    public bool HasAdjacentSiblingSelectors => AdjacentSiblingSelectors;

    public bool HasGeneralSiblingSelectors => GeneralSiblingSelectors;

    public bool NodeMayStartSiblingSelector(ICssElementView node)
    {
        if (UnkeyedSiblingSelectors
            || (node.IsQuirks && (AdjacentSiblingSelectors || GeneralSiblingSelectors)))
        {
            return true;
        }

        if (node.GetAttribute("id") is { } id && ReachesSibling(IdDependencies(id)))
        {
            return true;
        }

        if (node.GetAttribute("class") is { } classes)
        {
            foreach (var className in classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ReachesSibling(ClassDependencies(className)))
                {
                    return true;
                }
            }
        }

        foreach (var attribute in node.AttributeNames)
        {
            if (ReachesSibling(AttributeDependencies(attribute)))
            {
                return true;
            }
        }

        return node.IsElement && ReachesSibling(LocalNameDependencies(node.LocalName));

        static bool ReachesSibling(IReadOnlyList<InvalidationDependency> dependencies)
        {
            foreach (var dependency in dependencies)
            {
                if (dependency.Reaches.Contains(InvalidationReaches.Siblings))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public int DependencyCount =>
        _ids.Values.Sum(list => list.Count)
        + _classes.Values.Sum(list => list.Count)
        + _attributes.Values.Sum(list => list.Count)
        + _localNames.Values.Sum(list => list.Count)
        + _states.Values.Sum(list => list.Count);

    private static void Push(
        Dictionary<string, List<InvalidationDependency>> map,
        string key,
        int ruleOrder,
        InvalidationReaches reaches)
    {
        if (!map.TryGetValue(key, out var dependencies))
        {
            dependencies = [];
            map[key] = dependencies;
        }

        for (var index = 0; index < dependencies.Count; index++)
        {
            if (dependencies[index].RuleOrder == ruleOrder)
            {
                dependencies[index] = dependencies[index] with
                {
                    Reaches = dependencies[index].Reaches.Union(reaches),
                };
                return;
            }
        }

        dependencies.Add(new InvalidationDependency(ruleOrder, reaches));
    }

    internal void PushId(string key, int ruleOrder, InvalidationReaches reaches) =>
        Push(_ids, key, ruleOrder, reaches);

    internal void PushClass(string key, int ruleOrder, InvalidationReaches reaches) =>
        Push(_classes, key, ruleOrder, reaches);

    internal void PushAttribute(string key, int ruleOrder, InvalidationReaches reaches) =>
        Push(_attributes, CssText.AsciiLower(key), ruleOrder, reaches);

    internal void PushLocalName(string key, int ruleOrder, InvalidationReaches reaches) =>
        Push(_localNames, CssText.AsciiLower(key), ruleOrder, reaches);

    internal void PushState(string key, int ruleOrder, InvalidationReaches reaches) =>
        Push(_states, CssText.AsciiLower(key), ruleOrder, reaches);

    internal void MarkConservative(int ruleOrder)
    {
        if ((_conservativeRuleOrders.Count == 0 || _conservativeRuleOrders[^1] != ruleOrder)
            && !_conservativeRuleOrders.Contains(ruleOrder))
        {
            _conservativeRuleOrders.Add(ruleOrder);
        }
    }

    internal void MarkRelational(int ruleOrder, bool unkeyed)
    {
        if (!_relationalRuleOrders.Contains(ruleOrder))
        {
            _relationalRuleOrders.Add(ruleOrder);
        }

        if (unkeyed && !_unkeyedRelationalRuleOrders.Contains(ruleOrder))
        {
            _unkeyedRelationalRuleOrders.Add(ruleOrder);
        }
    }

    internal void PushRelationalInvalidation(RelationalInvalidation invalidation)
    {
        if (!_relationalInvalidations.Contains(invalidation))
        {
            _relationalInvalidations.Add(invalidation);
        }
    }

    internal void PushStructuralInvalidation(StructuralInvalidation invalidation)
    {
        if (!_structuralInvalidations.Contains(invalidation))
        {
            _structuralInvalidations.Add(invalidation);
        }
    }
}

/// <summary>
/// Records selector and declaration dependencies into an
/// <see cref="InvalidationMap"/>.
/// </summary>
public static class CssInvalidationBuilder
{
    private static InvalidationReaches ComposeReach(
        InvalidationMap map,
        InvalidationReaches inner,
        InvalidationReaches outer,
        int ruleOrder)
    {
        if (outer == InvalidationReaches.Self)
        {
            return inner;
        }

        if (inner == InvalidationReaches.Self || inner == outer)
        {
            return outer;
        }

        // A sibling traversal nested under an ancestor traversal (or vice
        // versa) cannot be represented by the three simple phase-1 reaches.
        map.MarkConservative(ruleOrder);
        return inner.Union(outer).Union(InvalidationReaches.Conservative);
    }

    internal static void NoteCompoundDependencies(
        InvalidationMap map,
        string compound,
        InvalidationReaches reaches,
        int ruleOrder,
        bool insideRelational)
    {
        if (CssSelectorText.CompoundLocalName(compound) is { } localName)
        {
            map.PushLocalName(localName, ruleOrder, reaches);
        }

        var chars = compound.AsSpan();
        var index = 0;
        while (index < chars.Length)
        {
            switch (chars[index])
            {
                case '#':
                {
                    var (id, next) = CssSelectorText.ConsumeIdentifier(chars, index + 1);
                    if (id.Length == 0)
                    {
                        map.MarkConservative(ruleOrder);
                    }
                    else
                    {
                        map.PushId(id, ruleOrder, reaches);
                    }

                    index = Math.Max(next, index + 1);
                    break;
                }

                case '.':
                {
                    var (className, next) = CssSelectorText.ConsumeIdentifier(chars, index + 1);
                    if (className.Length == 0)
                    {
                        map.MarkConservative(ruleOrder);
                    }
                    else
                    {
                        map.PushClass(className, ruleOrder, reaches);
                    }

                    index = Math.Max(next, index + 1);
                    break;
                }

                case '[':
                {
                    if (CssSelectorText.MatchingDelimiter(chars, index, '[', ']') is not { } close)
                    {
                        map.MarkConservative(ruleOrder);
                        return;
                    }

                    var nameIndex = index + 1;
                    while (nameIndex < chars.Length && CssText.IsWhitespace(chars[nameIndex]))
                    {
                        nameIndex++;
                    }

                    if (nameIndex < chars.Length && chars[nameIndex] is '*' or '|')
                    {
                        nameIndex++;
                    }

                    var (first, firstEnd) = CssSelectorText.ConsumeIdentifier(chars, nameIndex);
                    string attribute;
                    int end;
                    if (firstEnd < chars.Length && chars[firstEnd] == '|')
                    {
                        (attribute, end) = CssSelectorText.ConsumeIdentifier(chars, firstEnd + 1);
                    }
                    else
                    {
                        (attribute, end) = (first, firstEnd);
                    }

                    if (attribute.Length == 0 || end > close)
                    {
                        map.MarkConservative(ruleOrder);
                    }
                    else
                    {
                        map.PushAttribute(attribute, ruleOrder, reaches);
                    }

                    index = close + 1;
                    break;
                }

                case ':':
                {
                    if (index + 1 < chars.Length && chars[index + 1] == ':')
                    {
                        var (_, afterPseudoElement) = CssSelectorText.ConsumeIdentifier(chars, index + 2);
                        index = Math.Max(afterPseudoElement, index + 2);
                        continue;
                    }

                    var (rawName, next) = CssSelectorText.ConsumeIdentifier(chars, index + 1);
                    var name = CssText.AsciiLower(rawName);
                    if (name.Length == 0)
                    {
                        map.MarkConservative(ruleOrder);
                        index++;
                        continue;
                    }

                    if (next >= chars.Length || chars[next] != '(')
                    {
                        map.PushState(name, ruleOrder, reaches);
                        if (name is "empty" or "first-child" or "last-child" or "only-child"
                            or "first-of-type" or "last-of-type" or "only-of-type")
                        {
                            map.PushStructuralInvalidation(new StructuralInvalidation
                            {
                                RuleOrder = ruleOrder,
                                State = name,
                                SubjectKey = CssSelectorText.RelationalAnchorKey(compound),
                                Reaches = reaches,
                                InsideRelational = insideRelational,
                            });
                        }

                        if (name is "root" or "scope" or "target" or "link" or "any-link" or "visited"
                            or "empty" or "first-child" or "last-child" or "only-child"
                            or "first-of-type" or "last-of-type" or "only-of-type")
                        {
                            // These change when nodes are inserted, removed, or
                            // reordered. Phase 2 has no tree-structural mutation
                            // lookup yet, so keep the complete cascade fallback.
                            map.MarkConservative(ruleOrder);
                        }

                        index = next;
                        continue;
                    }

                    if (CssSelectorText.MatchingDelimiter(chars, next, '(', ')') is not { } closeParen)
                    {
                        map.MarkConservative(ruleOrder);
                        return;
                    }

                    var arguments = compound[(next + 1)..closeParen];
                    switch (name)
                    {
                        case "is":
                        case "where":
                        case "not":
                            foreach (var alternative in CssSelectorText.SplitSelectorList(arguments))
                            {
                                NoteSelectorDependencies(
                                    map,
                                    alternative.Trim(),
                                    reaches,
                                    ruleOrder,
                                    !insideRelational);
                            }

                            break;

                        case "has":
                        {
                            // Relative selectors invalidate anchors upwards,
                            // which Self/Descendants/Siblings cannot express
                            // soundly.
                            map.PushState(name, ruleOrder, InvalidationReaches.Conservative);
                            map.MarkConservative(ruleOrder);
                            var alternatives = CssSelectorText.SplitSelectorList(arguments);
                            var relativeKeys = new List<RelationalSelectorKey>();
                            foreach (var alternative in alternatives)
                            {
                                CssSelectorText.CollectRelationalSelectorKeys(alternative.Trim(), relativeKeys);
                            }

                            var unkeyed = alternatives.Any(alternative =>
                                !CssSelectorText.RelativeSelectorSubjectHasKey(alternative.Trim()));
                            map.MarkRelational(ruleOrder, unkeyed);
                            var siblingSideEffect = alternatives.Any(alternative =>
                                CssSelectorText.SelectorContainsAdjacentCombinator(alternative.Trim()));
                            var structuralSideEffect = alternatives.Any(alternative =>
                                CssSelectorText.SelectorContainsPseudo(
                                    alternative.Trim(),
                                    "empty",
                                    "first-child",
                                    "last-child",
                                    "only-child",
                                    "first-of-type",
                                    "last-of-type",
                                    "only-of-type",
                                    "nth-child",
                                    "nth-last-child",
                                    "nth-of-type",
                                    "nth-last-of-type"));
                            var textSideEffect = alternatives.Any(alternative =>
                                CssSelectorText.SelectorContainsPseudo(alternative.Trim(), "empty"));

                            map.PushRelationalInvalidation(new RelationalInvalidation
                            {
                                RuleOrder = ruleOrder,
                                AnchorKey = CssSelectorText.RelationalAnchorKey(compound),
                                RelativeKeys = relativeKeys,
                                AnchorReaches = reaches,
                                UnkeyedSubject = unkeyed,
                                SiblingSideEffect = siblingSideEffect,
                                StructuralSideEffect = structuralSideEffect,
                                TextSideEffect = textSideEffect,
                                UnrepresentableOuterPath = reaches.Contains(InvalidationReaches.Conservative),
                            });

                            foreach (var alternative in alternatives)
                            {
                                NoteSelectorDependencies(
                                    map,
                                    alternative.Trim(),
                                    InvalidationReaches.Conservative,
                                    ruleOrder,
                                    false);
                            }

                            break;
                        }

                        case "nth-child":
                        case "nth-last-child":
                        case "nth-of-type":
                        case "nth-last-of-type":
                        {
                            map.PushState(name, ruleOrder, reaches);
                            map.PushStructuralInvalidation(new StructuralInvalidation
                            {
                                RuleOrder = ruleOrder,
                                State = name,
                                SubjectKey = CssSelectorText.RelationalAnchorKey(compound),
                                Reaches = reaches,
                                InsideRelational = insideRelational,
                            });

                            // Structural index changes and `of <complex-selector>`
                            // need sibling-wide bookkeeping not present in phase 1.
                            map.MarkConservative(ruleOrder);
                            if (CssSelectorText.NthOfSelector(arguments) is { } ofSelector)
                            {
                                foreach (var alternative in CssSelectorText.SplitSelectorList(ofSelector))
                                {
                                    NoteSelectorDependencies(
                                        map,
                                        alternative.Trim(),
                                        InvalidationReaches.Conservative,
                                        ruleOrder,
                                        !insideRelational);
                                }
                            }

                            break;
                        }

                        case "dir":
                        case "lang":
                            // The corresponding HTML attributes inherit through
                            // descendants, while the functional selector may
                            // observe language/direction resolved above the
                            // mutated node. Keep the dependency keyed, but require
                            // the retained planner's sound full fallback.
                            map.PushState(name, ruleOrder, InvalidationReaches.Conservative);
                            map.MarkConservative(ruleOrder);
                            break;

                        default:
                            // A compiled functional pseudo outside the explicitly
                            // modeled set may hide selector or document state.
                            map.PushState(name, ruleOrder, reaches);
                            map.MarkConservative(ruleOrder);
                            break;
                    }

                    index = closeParen + 1;
                    break;
                }

                case '\\':
                    index = Math.Min(index + 2, chars.Length);
                    break;

                default:
                    index++;
                    break;
            }
        }
    }

    internal static void NoteSelectorDependencies(
        InvalidationMap map,
        string selector,
        InvalidationReaches outerReaches,
        int ruleOrder,
        bool recordTreeSiblings)
    {
        if (recordTreeSiblings)
        {
            var (adjacent, general) = CssSelectorText.SelectorSiblingCombinators(selector);
            map.AdjacentSiblingSelectors |= adjacent;
            map.GeneralSiblingSelectors |= general;
        }

        var (compounds, malformed) = CssSelectorText.InvalidationCompounds(selector);
        if (malformed)
        {
            map.MarkConservative(ruleOrder);
        }

        foreach (var (compound, localReaches) in compounds)
        {
            if (recordTreeSiblings
                && localReaches.Contains(InvalidationReaches.Siblings)
                && !CssSelectorText.RelativeSelectorSubjectHasKey(compound))
            {
                map.UnkeyedSiblingSelectors = true;
            }

            if (localReaches.Contains(InvalidationReaches.Conservative))
            {
                map.MarkConservative(ruleOrder);
            }

            var reaches = ComposeReach(map, localReaches, outerReaches, ruleOrder);
            NoteCompoundDependencies(map, compound, reaches, ruleOrder, !recordTreeSiblings);
        }
    }

    public static void NoteSelectorForInvalidation(InvalidationMap map, string selector, int ruleOrder) =>
        NoteSelectorDependencies(map, selector, InvalidationReaches.Self, ruleOrder, true);

    /// <summary>
    /// Record element attributes read from declaration values.
    /// </summary>
    /// <remarks>
    /// Selector invalidation alone is insufficient for generated content such as
    /// <c>.label::before { content: attr(data-label) }</c>: changing
    /// <c>data-label</c> changes the computed pseudo style even though the
    /// attribute does not occur in the selector. This is deliberately broader
    /// than the property parser; an extra self invalidation is cheap, while
    /// missing a supported <c>attr()</c> spelling would retain stale computed
    /// values.
    /// </remarks>
    public static void NoteDeclarationAttributeDependencies(
        InvalidationMap map,
        string declarations,
        int ruleOrder)
    {
        // Avoid another declaration-vector allocation on the stylesheet hot
        // path: almost every rule exits through this scan, and the uncommon rule
        // containing `attr` pays for the balanced character walk below.
        if (!ContainsAttrAscii(declarations))
        {
            return;
        }

        var chars = declarations.AsSpan();
        var index = 0;
        char? quote = null;
        while (index < chars.Length)
        {
            var current = chars[index];
            if (current == '\\')
            {
                index = Math.Min(index + 2, chars.Length);
                continue;
            }

            if (quote is { } active)
            {
                if (current == active)
                {
                    quote = null;
                }

                index++;
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                index++;
                continue;
            }

            if (current == '/' && index + 1 < chars.Length && chars[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < chars.Length && !(chars[index] == '*' && chars[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, chars.Length);
                continue;
            }

            if (!(CssText.IsAlphabetic(current) || current is '_' or '-' || !CssText.IsAscii(current)))
            {
                index++;
                continue;
            }

            var (function, next) = CssSelectorText.ConsumeIdentifier(chars, index);
            index = Math.Max(next, index + 1);
            if (!CssText.EqualsAscii(function, "attr"))
            {
                continue;
            }

            var open = index;
            while (open < chars.Length && CssText.IsWhitespace(chars[open]))
            {
                open++;
            }

            if (open >= chars.Length || chars[open] != '(')
            {
                continue;
            }

            if (CssSelectorText.MatchingDelimiter(chars, open, '(', ')') is not { } close)
            {
                // An unterminated function cannot be consumed by the current
                // declaration parser, so it has no live attribute dependency.
                break;
            }

            var nameStart = open + 1;
            while (nameStart < chars.Length && CssText.IsWhitespace(chars[nameStart]))
            {
                nameStart++;
            }

            var (attribute, nameEnd) = CssSelectorText.ConsumeIdentifier(chars, nameStart);
            if (attribute.Length != 0 && nameEnd <= close)
            {
                map.PushAttribute(attribute, ruleOrder, InvalidationReaches.Self);
            }

            index = close + 1;
        }
    }

    private static bool ContainsAttrAscii(string declarations)
    {
        for (var index = 0; index + 4 <= declarations.Length; index++)
        {
            if (CssText.EqualsAscii(declarations.AsSpan(index, 4), "attr"))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Builds an <see cref="InvalidationMap"/> from stylesheet sources.
/// </summary>
/// <remarks>
/// This reproduces the invalidation-recording half of Rust's
/// <c>Stylesheet::parse</c>, including its rule-order accounting: a rule whose
/// selector the matcher cannot compile advances the source order only when it
/// still needs conservative tracking. The selector-compilation predicate is a
/// seam because the selector engine lives in <c>Obscura.Dom</c>; the default
/// accepts every selector, which matches the reference matcher for all syntax
/// it supports.
/// </remarks>
public static class CssInvalidationMapBuilder
{
    public static InvalidationMap Build(
        IEnumerable<string> sources,
        (float Width, float Height) viewport,
        CssMediaType mediaType = CssMediaType.Screen,
        Func<string, bool>? selectorCompiles = null)
    {
        selectorCompiles ??= static _ => true;
        var map = new InvalidationMap();
        var order = 0;
        var conditions = CssParser.NewConditionArena();
        var layers = new LayerRegistry();

        foreach (var source in sources)
        {
            var parsed = CssParser.ParseStylesheetForViewportPreservingContainersInLayer(
                source,
                viewport,
                mediaType,
                conditions,
                ContainerConditionId.None,
                layers,
                null);

            foreach (var rule in parsed)
            {
                var selector = rule.Selector;
                if (selector.StartsWith(CssAtRules.KeyframesSelectorPrefix, StringComparison.Ordinal)
                    || selector.StartsWith(CssAtRules.WebkitKeyframesSelectorPrefix, StringComparison.Ordinal))
                {
                    order++;
                    continue;
                }

                if (selector.StartsWith(CssAtRules.PropertyRegistrationSelectorPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var trimmed = selector.Trim();
                var pseudoBase = CssSelectorText.StripPseudoElement(trimmed, "before")
                    ?? CssSelectorText.StripPseudoElement(trimmed, "after")
                    ?? CssSelectorText.StripPseudoElement(trimmed, "placeholder");

                if (pseudoBase is not null)
                {
                    if (selectorCompiles(pseudoBase))
                    {
                        CssInvalidationBuilder.NoteSelectorForInvalidation(map, pseudoBase, order);
                        CssInvalidationBuilder.NoteDeclarationAttributeDependencies(map, rule.Declarations, order);
                    }
                    else if (CssSelectorText.SelectorRequiresConservativeTracking(pseudoBase))
                    {
                        // Keep correctness metadata for relative/structural syntax
                        // that the current selector matcher cannot yet compile.
                        CssInvalidationBuilder.NoteSelectorForInvalidation(map, pseudoBase, order);
                    }

                    order++;
                    continue;
                }

                if (!selectorCompiles(selector))
                {
                    if (CssSelectorText.SelectorRequiresConservativeTracking(selector))
                    {
                        CssInvalidationBuilder.NoteSelectorForInvalidation(map, selector, order);
                        order++;
                    }

                    continue;
                }

                CssInvalidationBuilder.NoteSelectorForInvalidation(map, selector, order);
                CssInvalidationBuilder.NoteDeclarationAttributeDependencies(map, rule.Declarations, order);
                order++;
            }
        }

        return map;
    }
}
