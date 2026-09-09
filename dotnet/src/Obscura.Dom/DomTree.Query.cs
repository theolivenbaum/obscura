using Obscura.Dom.Selectors;

namespace Obscura.Dom;

public sealed partial class DomTree
{
    /// <summary>
    /// If <paramref name="selector"/> is a bare ASCII id selector like <c>#main</c>, return the id
    /// (without the <c>#</c>). Conservative: escapes, combinators, commas, whitespace, non-ASCII, or
    /// a non-letter/underscore first character fall through to the full selector engine.
    /// </summary>
    private static string? SimpleIdSelector(string selector)
    {
        var trimmed = selector.AsSpan().Trim();
        if (trimmed.Length < 2 || trimmed[0] != '#')
        {
            return null;
        }

        var id = trimmed[1..];
        var first = id[0];
        if (!char.IsAsciiLetter(first) && first != '_')
        {
            return null;
        }

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
            {
                return null;
            }
        }

        return id.ToString();
    }

    public NodeId? QuerySelector(string selector) => QuerySelectorFrom(Document, selector);

    public List<NodeId> QuerySelectorAll(string selector) => QuerySelectorAllFrom(Document, selector);

    public NodeId? QuerySelectorFrom(NodeId root, string selector)
    {
        // Fast path: a bare "#id" selector resolves through the id index in O(1) instead of
        // scanning every descendant. The index holds the first element in tree order per id, which
        // is exactly what the full scan would return. In quirks mode `#id` matches
        // ASCII-case-insensitively, but the id index is keyed on the exact-case id, so skip the
        // fast path and let the selector engine (below) do the case-insensitive match.
        if (!IsQuirks && SimpleIdSelector(selector) is { } id)
        {
            // querySelector matches strict descendants of root only, so the indexed element must
            // have root among its ancestors. An index miss or stale entry (detached node) falls
            // through to the full scan: the id index is best-effort, registering nodes only at
            // creation time.
            if (GetElementById(id) is { } nid && Ancestors(nid).Contains(root))
            {
                return nid;
            }
        }

        var selectorList = SelectorParser.Parse(selector);
        var context = new MatchingContext(SelectorQuirksMode);

        foreach (var descId in Descendants(root))
        {
            if (GetNode(descId)?.IsElement != true)
            {
                continue;
            }

            var element = new DomElement(this, descId);
            if (SelectorMatching.MatchesSelectorList(selectorList, element, context))
            {
                return descId;
            }
        }

        return null;
    }

    // Map the document's quirks flag onto the selector engine's quirks mode. In quirks mode
    // class/id selectors match ASCII-case-insensitively.
    private QuirksMode SelectorQuirksMode => IsQuirks ? QuirksMode.Quirks : QuirksMode.NoQuirks;

    public List<NodeId> QuerySelectorAllFrom(NodeId root, string selector)
    {
        var selectorList = SelectorParser.Parse(selector);
        var context = new MatchingContext(SelectorQuirksMode);
        var results = new List<NodeId>();

        foreach (var descId in Descendants(root))
        {
            if (GetNode(descId)?.IsElement != true)
            {
                continue;
            }

            var element = new DomElement(this, descId);
            if (SelectorMatching.MatchesSelectorList(selectorList, element, context))
            {
                results.Add(descId);
            }
        }

        return results;
    }

    /// <summary>
    /// Test one element as the selector subject, including when it is detached or is a direct child
    /// of a shadow-tree compatibility root.
    /// </summary>
    public bool MatchesSelector(NodeId nid, string selector)
    {
        if (GetNode(nid)?.IsElement != true)
        {
            return false;
        }

        var selectorList = SelectorParser.Parse(selector);
        var context = new MatchingContext(QuirksMode.NoQuirks);
        return SelectorMatching.MatchesSelectorList(selectorList, new DomElement(this, nid), context);
    }

    // The reference API returns Result<_, String> and op_dom branches on the error (a bad selector
    // yields an empty result rather than a thrown JS error). These mirror that shape so callers do
    // not have to catch.

    /// <summary>Query variant that reports an invalid selector instead of throwing.</summary>
    public bool TryQuerySelector(string selector, out NodeId? result, out string? error) =>
        TryQuerySelectorFrom(Document, selector, out result, out error);

    /// <summary>Query variant that reports an invalid selector instead of throwing.</summary>
    public bool TryQuerySelectorFrom(NodeId root, string selector, out NodeId? result, out string? error)
    {
        try
        {
            result = QuerySelectorFrom(root, selector);
            error = null;
            return true;
        }
        catch (SelectorParseException e)
        {
            result = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>Query variant that reports an invalid selector instead of throwing.</summary>
    public bool TryQuerySelectorAll(string selector, out List<NodeId> results, out string? error) =>
        TryQuerySelectorAllFrom(Document, selector, out results, out error);

    /// <summary>Query variant that reports an invalid selector instead of throwing.</summary>
    public bool TryQuerySelectorAllFrom(
        NodeId root,
        string selector,
        out List<NodeId> results,
        out string? error)
    {
        try
        {
            results = QuerySelectorAllFrom(root, selector);
            error = null;
            return true;
        }
        catch (SelectorParseException e)
        {
            results = [];
            error = e.Message;
            return false;
        }
    }

    /// <summary>Match variant that reports an invalid selector instead of throwing.</summary>
    public bool TryMatchesSelector(NodeId nid, string selector, out bool matches, out string? error)
    {
        try
        {
            matches = MatchesSelector(nid, selector);
            error = null;
            return true;
        }
        catch (SelectorParseException e)
        {
            matches = false;
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Parse a single selector once for repeated single-element matching, and precompute its
    /// specificity, ancestor hashes, and "subject key" (the rightmost id/class/attribute/tag used to
    /// bucket the rule for fast candidate lookup). Returns null if the selector fails to parse.
    ///
    /// This is the primitive a stylesheet cascade builds on: compile every rule once, index by key,
    /// then only test the handful of candidate rules against each element instead of scanning the
    /// whole tree per rule.
    /// </summary>
    public CompiledSelector? CompileRuleSelector(string selector)
    {
        if (!SelectorParser.TryParse(selector, out var list, out _) || list.Count == 0)
        {
            return null;
        }

        var sel = list.Selectors[0];
        var keys = CompiledSelector.SubjectKeys(sel);
        var hashes = AncestorHashes.Create(sel, QuirksMode.NoQuirks);
        return new CompiledSelector(sel, sel.Specificity, [.. keys], hashes);
    }

    /// <summary>
    /// Does a single element match a precompiled selector? Allocates a fresh matcher each call; for
    /// many matches in a row use <see cref="CreateMatcher"/>.
    /// </summary>
    public bool ElementMatches(NodeId nid, CompiledSelector compiled) =>
        CreateMatcher().Matches(this, nid, compiled);

    /// <summary>
    /// A reusable matcher that holds the match state and an ancestor bloom filter, so a cascade can
    /// fast-reject most (element, rule) pairs without walking the selector's combinators. Reuse one
    /// across a whole cascade pass and drive <see cref="Matcher.PushAncestor"/> /
    /// <see cref="Matcher.PopAncestor"/> as you descend/ascend the tree so the filter tracks the
    /// current path.
    /// </summary>
    public Matcher CreateMatcher() => new();
}
