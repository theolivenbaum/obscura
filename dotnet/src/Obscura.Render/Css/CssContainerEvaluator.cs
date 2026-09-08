using Obscura.Dom;

namespace Obscura.Render.Css;

/// <summary>Which subject a container query was evaluated for.</summary>
internal enum ContainerQuerySubjectKind
{
    /// <summary>An ordinary element: the container search starts at its parent.</summary>
    Element,

    /// <summary>
    /// A pseudo-element: its originating element is itself a candidate
    /// container, so the search starts there.
    /// </summary>
    OriginatingPseudo,
}

internal readonly record struct ContainerQueryCacheKey(
    NodeId Subject,
    ContainerConditionId Condition,
    ContainerQuerySubjectKind Kind);

internal sealed record ContainerQueryDecision(
    ContainerQueryTruth Truth,
    List<NodeId?> SelectedContainers)
{
    public bool Equals(ContainerQueryDecision? other) =>
        other is not null
        && Truth == other.Truth
        && SelectedContainers.Count == other.SelectedContainers.Count
        && SequencesEqual(SelectedContainers, other.SelectedContainers);

    public override int GetHashCode() => HashCode.Combine(Truth, SelectedContainers.Count);

    private static bool SequencesEqual(List<NodeId?> left, List<NodeId?> right)
    {
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The container boxes visible to one cascade pass, keyed by the element that
/// generated them.
/// </summary>
public sealed class ContainerSnapshot
{
    public Dictionary<NodeId, ContainerBox> Boxes { get; } = [];

    public float RootFontSize { get; set; }

    public override bool Equals(object? obj)
    {
        if (obj is not ContainerSnapshot other)
        {
            return false;
        }

        if (RootFontSize != other.RootFontSize || Boxes.Count != other.Boxes.Count)
        {
            return false;
        }

        foreach (var (node, box) in Boxes)
        {
            if (!other.Boxes.TryGetValue(node, out var otherBox) || !box.Equals(otherBox))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Boxes.Count, RootFontSize);
}

/// <summary>
/// Every container-query decision one cascade pass reached. Retained style
/// compares signatures to tell whether a relayout changed any query answer.
/// </summary>
public sealed class ContainerDecisionSignature
{
    internal Dictionary<ContainerQueryCacheKey, ContainerQueryDecision> Decisions { get; init; } = [];

    /// <summary>Whether the pass reached no container-query decision at all.</summary>
    public bool IsEmpty => Decisions.Count == 0;

    public int Count => Decisions.Count;

    public override bool Equals(object? obj)
    {
        if (obj is not ContainerDecisionSignature other)
        {
            return false;
        }

        if (Decisions.Count != other.Decisions.Count)
        {
            return false;
        }

        foreach (var (key, decision) in Decisions)
        {
            if (!other.Decisions.TryGetValue(key, out var otherDecision) || !decision.Equals(otherDecision))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode() => Decisions.Count;
}

/// <summary>Counters for one container-query evaluation pass.</summary>
public readonly record struct ContainerQueryStats(int Evaluations, int CacheHits, int AncestorSteps);

/// <summary>
/// Evaluates <c>@container</c> conditions for a cascade pass, memoizing every
/// (subject, condition) answer.
/// </summary>
public sealed class ContainerQueryEvaluator(DomTree tree, ContainerSnapshot snapshot)
{
    private readonly DomTree _tree = tree;
    private readonly ContainerSnapshot _snapshot = snapshot;
    private readonly Dictionary<ContainerQueryCacheKey, ContainerQueryDecision> _cache = [];
    private int _evaluations;
    private int _cacheHits;
    private int _ancestorSteps;

    public (ContainerDecisionSignature Signature, ContainerQueryStats Stats) Finish() =>
        (new ContainerDecisionSignature { Decisions = _cache },
            new ContainerQueryStats(_evaluations, _cacheHits, _ancestorSteps));

    internal bool ConditionMatches(
        Stylesheet sheet,
        NodeId subject,
        ContainerConditionId condition,
        ContainerQuerySubjectKind kind) =>
        EvaluateConditionChain(sheet, subject, condition, kind).Truth == ContainerQueryTruth.True;

    private ContainerQueryDecision EvaluateConditionChain(
        Stylesheet sheet,
        NodeId subject,
        ContainerConditionId condition,
        ContainerQuerySubjectKind kind)
    {
        if (condition == ContainerConditionId.None)
        {
            return new ContainerQueryDecision(ContainerQueryTruth.True, []);
        }

        var key = new ContainerQueryCacheKey(subject, condition, kind);
        if (_cache.TryGetValue(key, out var cached))
        {
            _cacheHits++;
            return cached;
        }

        _evaluations++;
        var node = sheet.ContainerConditions[(int)condition.Value];
        var ownTruth = ContainerQueryTruth.False;
        var selectedContainers = new List<NodeId?>(node.Alternatives.Count);
        foreach (var query in node.Alternatives)
        {
            var (truth, selected) = EvaluateQuery(subject, kind, query);
            ownTruth = ownTruth.Or(truth);
            selectedContainers.Add(selected);
            if (ownTruth == ContainerQueryTruth.True)
            {
                break;
            }
        }

        if (ownTruth == ContainerQueryTruth.False)
        {
            var falseDecision = new ContainerQueryDecision(ContainerQueryTruth.False, selectedContainers);
            _cache[key] = falseDecision;
            return falseDecision;
        }

        var parent = EvaluateConditionChain(sheet, subject, node.Parent, kind);
        selectedContainers.AddRange(parent.SelectedContainers);
        var decision = new ContainerQueryDecision(ownTruth.And(parent.Truth), selectedContainers);
        _cache[key] = decision;
        return decision;
    }

    private (ContainerQueryTruth Truth, NodeId? Selected) EvaluateQuery(
        NodeId subject,
        ContainerQuerySubjectKind kind,
        ContainerQuery query)
    {
        var requiredAxes = query.Condition is null
            ? default
            : CssContainerQuery.RequiredAxes(query.Condition);
        var candidate = kind == ContainerQuerySubjectKind.Element
            ? _tree.GetNode(subject)?.Parent
            : subject;

        while (candidate is { } id)
        {
            _ancestorSteps++;
            var parent = _tree.GetNode(id)?.Parent;
            if (_snapshot.Boxes.TryGetValue(id, out var container))
            {
                var supportsAxis = container.ContainerType switch
                {
                    ContainerType.Normal => !requiredAxes.Inline && !requiredAxes.Block,
                    ContainerType.InlineSize => !requiredAxes.Block,
                    _ => true,
                };
                var nameMatches = query.Name is null || ContainsName(container.Names, query.Name);
                if (supportsAxis && nameMatches)
                {
                    var axisAvailable = container.AvailableType switch
                    {
                        ContainerType.Normal => !requiredAxes.Inline && !requiredAxes.Block,
                        ContainerType.InlineSize => !requiredAxes.Block,
                        _ => true,
                    };
                    if (!axisAvailable)
                    {
                        return (ContainerQueryTruth.Unknown, id);
                    }

                    var truth = query.Condition is null
                        ? ContainerQueryTruth.True
                        : CssContainerQuery.Evaluate(query.Condition, container, _snapshot.RootFontSize);
                    return (truth, id);
                }
            }

            candidate = parent;
        }

        return (ContainerQueryTruth.False, null);
    }

    private static bool ContainsName(IReadOnlyList<string> names, string name)
    {
        for (var index = 0; index < names.Count; index++)
        {
            if (string.Equals(names[index], name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
