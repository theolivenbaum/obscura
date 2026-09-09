namespace Obscura.Dom.Selectors;

/// <summary>
/// Holds the selector match state plus an incremental ancestor bloom filter so a treewalk cascade
/// can test thousands of (element, rule) pairs cheaply: most rules are fast-rejected by the filter
/// before any combinator walking happens.
/// </summary>
public sealed class Matcher
{
    private readonly AncestorFilter _ancestors = new();
    private readonly List<uint> _candidateSeen = [];
    private uint _candidateGeneration;

    internal uint CandidateGeneration
    {
        get => _candidateGeneration;
        set => _candidateGeneration = value;
    }

    /// <summary>
    /// Start one indexed-candidate collection without clearing the reusable seen table. Stylesheet
    /// rules that live in several selector buckets use this to avoid matching and cascading the
    /// same rule twice.
    /// </summary>
    public void BeginCandidateCollection(int candidateCount)
    {
        _candidateGeneration = unchecked(_candidateGeneration + 1);
        if (_candidateGeneration == 0)
        {
            for (var i = 0; i < _candidateSeen.Count; i++)
            {
                _candidateSeen[i] = 0;
            }

            _candidateGeneration = 1;
        }

        if (_candidateSeen.Count > candidateCount)
        {
            _candidateSeen.RemoveRange(candidateCount, _candidateSeen.Count - candidateCount);
        }
        else
        {
            while (_candidateSeen.Count < candidateCount)
            {
                _candidateSeen.Add(0);
            }
        }
    }

    /// <summary>Return true once per candidate index in the current collection.</summary>
    public bool MarkCandidate(int index)
    {
        if ((uint)index >= (uint)_candidateSeen.Count)
        {
            // Candidate collection is an optimization. If a future caller supplies inconsistent
            // bounds, fail open to an extra match rather than silently dropping authored CSS.
            return true;
        }

        if (_candidateSeen[index] == _candidateGeneration)
        {
            return false;
        }

        _candidateSeen[index] = _candidateGeneration;
        return true;
    }

    /// <summary>
    /// Match <paramref name="nid"/> (matched as if it were the subject element; the ancestor filter
    /// reflects the node's ancestors, not the node itself) against <paramref name="compiled"/>.
    /// </summary>
    public bool Matches(DomTree tree, NodeId nid, CompiledSelector compiled)
    {
        if (!(tree.GetNode(nid)?.IsElement ?? false))
        {
            return false;
        }

        var context = new MatchingContext(QuirksMode.NoQuirks) { BloomFilter = _ancestors.Filter };
        return SelectorMatching.MatchesSelector(
            compiled.Selector,
            compiled.Hashes,
            new DomElement(tree, nid),
            context);
    }

    /// <summary>
    /// Match a selector from the host's shadow-tree scope against the host. Supplying the scope
    /// explicitly prevents <c>:host</c> from leaking into document stylesheets or a different
    /// shadow root.
    /// </summary>
    public bool MatchesShadowHost(DomTree tree, NodeId nid, CompiledSelector compiled, NodeId host)
    {
        if (nid != host)
        {
            return false;
        }

        return MatchesInShadowScope(tree, nid, compiled, host);
    }

    /// <summary>
    /// Match <paramref name="compiled"/> in the shadow tree hosted by <paramref name="host"/>. This
    /// supplies the scope used by <c>::slotted()</c>'s slot-assignment combinator without making
    /// shadow-only selectors observable to document matching.
    /// </summary>
    public bool MatchesInShadowScope(DomTree tree, NodeId nid, CompiledSelector compiled, NodeId host)
    {
        if (!(tree.GetNode(nid)?.IsElement ?? false))
        {
            return false;
        }

        // The caller's reusable bloom tracks the subject's DOM ancestors. A slotted selector
        // crosses to the assigned slot and then walks that slot's shadow-tree ancestors, so the DOM
        // bloom would cause false negatives for `.wrapper slot::slotted(...)`. Scoped rules are
        // already narrowed to a small per-shadow index; match them without an incompatible ancestor
        // filter.
        var context = new MatchingContext(QuirksMode.NoQuirks) { CurrentHost = host };
        return SelectorMatching.MatchesSelector(
            compiled.Selector,
            compiled.Hashes,
            new DomElement(tree, nid),
            context);
    }

    /// <summary>
    /// Push the node's hashes onto the ancestor filter before descending into its children. Must be
    /// paired with a matching <see cref="PopAncestor"/>.
    /// </summary>
    public void PushAncestor(DomTree tree, NodeId nid) => _ancestors.Push(tree, nid);

    /// <summary>Pop the hashes pushed by the most recent unmatched <see cref="PushAncestor"/>.</summary>
    public void PopAncestor() => _ancestors.Pop();

    /// <summary>
    /// An incremental ancestor bloom filter. Push is called with each element on the way down the
    /// tree, Pop on the way back up, so at any point the filter holds exactly the hashes of the
    /// current node's ancestor chain.
    /// </summary>
    private sealed class AncestorFilter
    {
        public BloomFilter Filter { get; } = new();

        /// <summary>Hashes pushed per tree-depth level, so Pop knows what to remove.</summary>
        private readonly List<uint[]> _levels = [];

        public void Push(DomTree tree, NodeId nid)
        {
            var hashes = new List<uint>(4);
            var node = tree.GetNode(nid);
            if (node?.ElementName is { } name)
            {
                PushHash(hashes, AncestorHashes.PrecomputedHash(name.Local));
                if (node.GetAttribute("id") is { } id)
                {
                    PushHash(hashes, AncestorHashes.PrecomputedHash(id));
                }

                if (node.GetAttribute("class") is { } classAttr)
                {
                    foreach (var range in classAttr.AsSpan().SplitAny(AttrEvaluation.SelectorWhitespace))
                    {
                        var candidate = classAttr.AsSpan()[range];
                        if (candidate.Length != 0)
                        {
                            PushHash(hashes, AncestorHashes.PrecomputedHash(candidate.ToString()));
                        }
                    }
                }
            }

            _levels.Add([.. hashes]);
        }

        public void Pop()
        {
            if (_levels.Count == 0)
            {
                return;
            }

            var hashes = _levels[^1];
            _levels.RemoveAt(_levels.Count - 1);
            foreach (var hash in hashes)
            {
                Filter.RemoveHash(hash);
            }
        }

        private void PushHash(List<uint> hashes, uint hash)
        {
            var masked = hash & BloomFilter.BloomHashMask;
            Filter.InsertHash(masked);
            hashes.Add(masked);
        }
    }
}
