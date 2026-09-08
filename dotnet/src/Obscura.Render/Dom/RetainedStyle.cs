// Port of the retained-style damage planner in crates/obscura-render/src/dom.rs.
using Obscura.Dom;
using Obscura.Dom.Selectors;
using Obscura.Render.Css;

namespace Obscura.Render;

/// <summary>
/// One connected element attribute mutation eligible for conservative retained-style
/// invalidation.
/// </summary>
public sealed record AttributeStyleMutation(
    NodeId Node,
    string Name,
    string? OldValue,
    string? NewValue);

/// <summary>How an attribute mutation participates in retained style.</summary>
/// <remarks>
/// This classification is public so DOM bindings can decide whether to keep a prepared render
/// without maintaining a second, inevitably divergent HTML attribute allowlist.
/// </remarks>
public enum RetainedAttributeMutationKind : byte
{
    /// <summary>Only selector matching or declaration <c>attr()</c> can observe the value.</summary>
    Selector,

    /// <summary>The renderer maps the attribute into computed style or a native box.</summary>
    Subtree,

    /// <summary>
    /// The attribute can replace global CSS/resources or has document-wide semantics which
    /// cannot be represented by a bounded style dirty set.
    /// </summary>
    Full,
}

/// <summary>
/// A connected tree change which can reuse computed styles from the previous render.
/// </summary>
/// <remarks>
/// Node ids are stable across detach/reparent operations; recording both parents lets the
/// post-mutation traversal invalidate the old and new sibling scopes without retaining a
/// second DOM snapshot.
/// </remarks>
public abstract record TreeStyleMutation
{
    private TreeStyleMutation()
    {
    }

    public sealed record Insert(NodeId Node, NodeId? OldParent, NodeId NewParent) : TreeStyleMutation;

    public sealed record Remove(NodeId Node, NodeId OldParent) : TreeStyleMutation;

    public sealed record Text(NodeId Node, NodeId? Parent) : TreeStyleMutation;
}

/// <summary>Damage input for a retained-style rebuild.</summary>
public abstract record RetainedStyleMutation
{
    private RetainedStyleMutation()
    {
    }

    public sealed record Attribute(AttributeStyleMutation Mutation) : RetainedStyleMutation;

    public sealed record Tree(TreeStyleMutation Mutation) : RetainedStyleMutation;

    /// <summary>
    /// A document-timeline sample changed while the DOM and stylesheet stayed stable.
    /// </summary>
    public sealed record Animation(NodeId Node) : RetainedStyleMutation;

    /// <summary>
    /// A Web Animation sample changed. Obscura's WAAPI surface currently animates only
    /// transform and opacity; neither property inherits.
    /// </summary>
    public sealed record WaapiAnimation(NodeId Node) : RetainedStyleMutation;

    /// <summary>
    /// Cached image or font bytes became available. Resource selection, intrinsic sizes,
    /// shaping, layout, and paint must be rebuilt, but no style node is dirty.
    /// </summary>
    public sealed record Resource : RetainedStyleMutation
    {
        public static readonly Resource Instance = new();
    }

    public static RetainedStyleMutation From(AttributeStyleMutation mutation) => new Attribute(mutation);

    public static RetainedStyleMutation From(TreeStyleMutation mutation) => new Tree(mutation);
}

internal abstract record RetainedStylePlan
{
    private RetainedStylePlan()
    {
    }

    internal sealed record Reuse(HashSet<NodeId> Dirty, bool HasAnimationDamage) : RetainedStylePlan;

    internal sealed record Full : RetainedStylePlan
    {
        internal static readonly Full Instance = new();
    }
}

/// <summary>Retained-style damage classification and planning.</summary>
public static class RetainedStylePlanner
{
    private static readonly string[] GlobalStyleAttributes =
        ["as", "crossorigin", "disabled", "fetchpriority", "href", "hreflang", "imagesizes",
         "imagesrcset", "integrity", "media", "referrerpolicy", "rel", "sizes", "type"];

    /// <summary>Classify an element attribute for conservative retained-style planning.</summary>
    /// <remarks>
    /// Unknown attributes are deliberately selector-only. Custom elements and frameworks
    /// routinely toggle attributes such as <c>autocomplete</c>, <c>part</c>, and
    /// <c>itemprop</c>; forcing a document cascade for an unreferenced attribute is both
    /// unnecessary and particularly costly during hydration.
    /// </remarks>
    public static RetainedAttributeMutationKind RetainedAttributeMutationKindOf(
        DomTree tree,
        NodeId node,
        string name)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(name);
        name = name.ToLowerInvariant();
        if (string.Equals(name, "style", StringComparison.Ordinal))
        {
            return RetainedAttributeMutationKind.Subtree;
        }

        if (tree.GetNode(node)?.AsElement() is not { } element)
        {
            return RetainedAttributeMutationKind.Full;
        }

        string local = element.Name.Local.ToLowerInvariant();

        // These nodes feed document-global stylesheet, URL, metadata, or script state.
        if ((local is "base" && name is "href" or "target")
            || (local is "meta" && name is "charset" or "content" or "http-equiv" or "name")
            || (local is "style" && name is "media" or "type" or "title")
            || (local is "link" && Array.IndexOf(GlobalStyleAttributes, name) >= 0)
            || (local is "script" && name is "async" or "crossorigin" or "defer" or "fetchpriority"
                or "integrity" or "nomodule" or "referrerpolicy" or "src" or "type"))
        {
            return RetainedAttributeMutationKind.Full;
        }

        // Resource selection changes require the embedding runtime to rebuild its
        // decoded-resource/intrinsic-size inputs, not merely recompute CSS.
        if ((local is "img" && name is "crossorigin" or "decoding" or "fetchpriority"
                or "referrerpolicy" or "sizes" or "src" or "srcset")
            || (local is "source" && name is "height" or "media" or "sizes" or "src" or "srcset"
                or "type" or "width")
            || (local is "audio" or "video" or "track" && name is "crossorigin" or "kind" or "label"
                or "media" or "poster" or "preload" or "src" or "srclang" or "type")
            || (local is "embed" or "iframe" && name is "src" or "srcdoc" or "type")
            || (local is "object" && name is "data" or "type")
            || (local is "image" or "use" && name is "href" or "xlink:href"))
        {
            return RetainedAttributeMutationKind.Full;
        }

        // These values enter the UA/presentational cascade or are folded into a native
        // control's final used style.
        if (name is "align" or "bgcolor" or "cellpadding" or "cellspacing" or "color" or "height"
                or "hidden" or "valign" or "viewbox" or "width"
            || (local is "input" && name is "size" or "type" or "value")
            || (local is "select" && name is "size")
            || (local is "textarea" && name is "cols" or "rows" or "wrap")
            || name is "dir" or "lang" or "xml:lang")
        {
            return RetainedAttributeMutationKind.Subtree;
        }

        return RetainedAttributeMutationKind.Selector;
    }

    internal static void AddStyleSubtree(DomTree tree, NodeId root, HashSet<NodeId> dirty)
    {
        dirty.Add(root);
        foreach (NodeId descendant in tree.Descendants(root))
        {
            dirty.Add(descendant);
        }

        // Rebuild the context chain too. A retained ancestor may carry a final post-layout Px
        // repair where a full pass would feed its specified Auto/inherit form to the fresh
        // descendant's normalization.
        foreach (NodeId ancestor in tree.Ancestors(root))
        {
            dirty.Add(ancestor);
        }
    }

    internal static void AddContainerQueryResetScopes(
        DomTree tree,
        NodeId id,
        Stylesheet sheet,
        Matcher matcher,
        HashSet<NodeId> activeContainers,
        bool insideActiveContainer,
        bool selectedAncestor,
        HashSet<NodeId> dirty)
    {
        bool isElement = tree.GetNode(id)?.IsElement == true;
        bool canQueryHere = insideActiveContainer || activeContainers.Contains(id);
        bool selectedHere = !selectedAncestor
            && canQueryHere
            && isElement
            && sheet.NodeMatchesContainerQueryRule(tree, matcher, id);
        bool selected = selectedAncestor || selectedHere;
        if (selected)
        {
            // The retained style is the previous converged, query-enabled value.
            dirty.Add(id);
        }

        if (selectedHere)
        {
            // Stop as soon as an already-recorded context node is reached.
            NodeId? ancestor = tree.GetNode(id)?.Parent;
            while (ancestor is { } parent)
            {
                if (!dirty.Add(parent))
                {
                    break;
                }

                ancestor = tree.GetNode(parent)?.Parent;
            }
        }

        if (isElement)
        {
            matcher.PushAncestor(tree, id);
        }

        bool childrenInsideContainer = insideActiveContainer || activeContainers.Contains(id);
        foreach (NodeId child in tree.Children(id))
        {
            AddContainerQueryResetScopes(
                tree,
                child,
                sheet,
                matcher,
                activeContainers,
                childrenInsideContainer,
                selected,
                dirty);
        }

        if (isElement)
        {
            matcher.PopAncestor();
        }
    }

    private static void AddFollowingSiblingSubtrees(DomTree tree, NodeId node, HashSet<NodeId> dirty)
    {
        NodeId? sibling = tree.GetNode(node)?.NextSibling;
        while (sibling is { } id)
        {
            AddStyleSubtree(tree, id, dirty);
            sibling = tree.GetNode(id)?.NextSibling;
        }
    }

    private static bool SubtreeContainsStyleElement(DomTree tree, NodeId root)
    {
        if (DomTraversal.IsLocal(tree, root, "style"))
        {
            return true;
        }

        foreach (NodeId id in tree.Descendants(root))
        {
            if (DomTraversal.IsLocal(tree, id, "style"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool NodeIsStyleText(DomTree tree, NodeId node, NodeId? parent) =>
        (parent is { } p && DomTraversal.IsLocal(tree, p, "style"))
        || DomTraversal.IsLocal(tree, node, "style");

    private static bool SubtreeMayMatchRelationalPath(
        DomTree tree,
        RelationalInvalidation invalidation,
        NodeId root)
    {
        if (invalidation.RelativePathMayMatch(new DomElementView(tree, root)))
        {
            return true;
        }

        foreach (NodeId id in tree.Descendants(root))
        {
            if (invalidation.RelativePathMayMatch(new DomElementView(tree, id)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Candidate anchors after an insertion. Relative selectors search from the changed
    /// subtree toward ancestors and earlier siblings.
    /// </summary>
    private static void AddInsertedRelationalAnchorCandidates(
        DomTree tree,
        NodeId node,
        HashSet<NodeId> candidates)
    {
        NodeId? current = node;
        while (current is { } id)
        {
            NodeId? sibling = tree.GetNode(id)?.PrevSibling;
            while (sibling is { } previous)
            {
                candidates.Add(previous);
                sibling = tree.GetNode(previous)?.PrevSibling;
            }

            current = tree.GetNode(id)?.Parent;
            if (current is { } parent)
            {
                candidates.Add(parent);
            }
        }
    }

    /// <summary>
    /// Removal has already destroyed the old sibling links. Inspect every sibling at each old
    /// ancestor boundary.
    /// </summary>
    private static void AddRemovedRelationalAnchorCandidates(
        DomTree tree,
        NodeId oldParent,
        HashSet<NodeId> candidates)
    {
        NodeId? current = oldParent;
        while (current is { } id)
        {
            candidates.Add(id);
            foreach (NodeId child in tree.Children(id))
            {
                candidates.Add(child);
            }

            NodeId? parent = tree.GetNode(id)?.Parent;
            if (parent is { } parentId)
            {
                foreach (NodeId child in tree.Children(parentId))
                {
                    candidates.Add(child);
                }
            }

            current = parent;
        }
    }

    private static void AddRelationalAnchorScope(
        DomTree tree,
        NodeId anchor,
        InvalidationReaches reaches,
        HashSet<NodeId> dirty)
    {
        // Re-cascading the anchor subtree covers anchor-self changes, inheritance, and every
        // descendant subject.
        AddStyleSubtree(tree, anchor, dirty);
        if (reaches.Contains(InvalidationReaches.Siblings))
        {
            AddFollowingSiblingSubtrees(tree, anchor, dirty);
        }
    }

    /// <summary>
    /// Apply Gecko-style upward <c>:has()</c> invalidation for one child-list or text
    /// mutation. Returns false only when the path outside the anchor combines traversals which
    /// the renderer's flat reach bits cannot represent soundly.
    /// </summary>
    private static bool AddRelationalTreeInvalidation(
        DomTree tree,
        InvalidationMap map,
        TreeStyleMutation mutation,
        HashSet<NodeId> dirty)
    {
        if (map.RelationalInvalidations.Count == 0)
        {
            return true;
        }

        HashSet<NodeId> candidates = [];
        switch (mutation)
        {
            case TreeStyleMutation.Insert insert:
                AddInsertedRelationalAnchorCandidates(tree, insert.Node, candidates);
                if (insert.OldParent is { } oldParent)
                {
                    AddRemovedRelationalAnchorCandidates(tree, oldParent, candidates);
                }

                break;
            case TreeStyleMutation.Remove remove:
                AddRemovedRelationalAnchorCandidates(tree, remove.OldParent, candidates);
                break;
            case TreeStyleMutation.Text text:
                AddInsertedRelationalAnchorCandidates(tree, text.Node, candidates);
                if (text.Parent is { } textParent)
                {
                    candidates.Add(textParent);
                }

                break;
        }

        foreach (RelationalInvalidation invalidation in map.RelationalInvalidations)
        {
            bool triggered = mutation switch
            {
                TreeStyleMutation.Remove => true,
                TreeStyleMutation.Insert insert =>
                    SubtreeMayMatchRelationalPath(tree, invalidation, insert.Node)
                    || invalidation.UnkeyedSubject
                    || invalidation.SiblingSideEffect
                    || invalidation.StructuralSideEffect,
                TreeStyleMutation.Text => invalidation.TextSideEffect,
                _ => false,
            };
            if (!triggered)
            {
                continue;
            }

            foreach (NodeId anchor in candidates)
            {
                if (!invalidation.AnchorMayMatch(new DomElementView(tree, anchor)))
                {
                    continue;
                }

                if (invalidation.UnrepresentableOuterPath)
                {
                    return false;
                }

                AddRelationalAnchorScope(tree, anchor, invalidation.AnchorReaches, dirty);
            }
        }

        return true;
    }

    private static void AddStyleContextChain(DomTree tree, NodeId node, HashSet<NodeId> dirty)
    {
        dirty.Add(node);
        foreach (NodeId ancestor in tree.Ancestors(node))
        {
            dirty.Add(ancestor);
        }
    }

    private static void AddTableRowChildScope(DomTree tree, NodeId parent, HashSet<NodeId> dirty)
    {
        if (!DomTraversal.IsLocal(tree, parent, "tr"))
        {
            return;
        }

        // The table fallback assigns surplus growth to the trailing auto cell after cascade.
        foreach (NodeId child in DomTraversal.ElementChildren(tree, parent))
        {
            AddStyleSubtree(tree, child, dirty);
        }
    }

    /// <summary>
    /// Re-cascade a node whose structural pseudo state may have changed.
    /// </summary>
    private static void AddStructuralCandidateScope(
        DomTree tree,
        InvalidationMap map,
        string state,
        NodeId candidate,
        NodeId parent,
        HashSet<NodeId> dirty)
    {
        List<StructuralInvalidation> invalidations = [];
        foreach (StructuralInvalidation invalidation in map.StructuralInvalidations(state))
        {
            if (!invalidation.InsideRelational
                && invalidation.SubjectMayMatch(new DomElementView(tree, candidate)))
            {
                invalidations.Add(invalidation);
            }
        }

        if (invalidations.Count == 0)
        {
            return;
        }

        foreach (StructuralInvalidation invalidation in invalidations)
        {
            if (invalidation.Reaches.Contains(InvalidationReaches.Conservative))
            {
                AddStyleSubtree(tree, parent, dirty);
                return;
            }
        }

        AddStyleSubtree(tree, candidate, dirty);
        foreach (StructuralInvalidation invalidation in invalidations)
        {
            if (invalidation.Reaches.Contains(InvalidationReaches.Siblings))
            {
                AddFollowingSiblingSubtrees(tree, candidate, dirty);
                return;
            }
        }
    }

    private static void AddInsertedStructuralScopes(
        DomTree tree,
        InvalidationMap map,
        NodeId node,
        NodeId parent,
        IReadOnlyList<RetainedStyleMutation> mutations,
        HashSet<NodeId> dirty)
    {
        List<NodeId> siblings = DomTraversal.ElementChildren(tree, parent);
        int position = siblings.IndexOf(node);
        if (position < 0)
        {
            return;
        }

        void Add(string state, IReadOnlyList<NodeId> candidates)
        {
            foreach (NodeId candidate in candidates)
            {
                AddStructuralCandidateScope(tree, map, state, candidate, parent, dirty);
            }
        }

        int insertionCount = 0;
        foreach (RetainedStyleMutation mutation in mutations)
        {
            if (mutation is RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Insert insert }
                && insert.NewParent == parent
                && tree.GetNode(insert.Node)?.IsElement == true)
            {
                insertionCount++;
            }
        }

        if (insertionCount > 1)
        {
            // Mutation records do not retain the old sibling boundaries.
            foreach (string state in new[]
            {
                "first-child", "last-child", "only-child",
                "first-of-type", "last-of-type", "only-of-type",
            })
            {
                Add(state, siblings);
            }
        }

        if (position == 0)
        {
            Add("first-child", siblings.GetRange(0, Math.Min(siblings.Count, 2)));
        }

        if (position + 1 == siblings.Count)
        {
            int start = Math.Max(position - 1, 0);
            Add("last-child", siblings.GetRange(start, siblings.Count - start));
        }

        if (siblings.Count <= 2)
        {
            Add("only-child", siblings);
        }

        Add("nth-child", siblings.GetRange(position, siblings.Count - position));
        Add("nth-last-child", siblings.GetRange(0, position + 1));

        if (DomTraversal.ElementLocalName(tree, node) is not { } local)
        {
            return;
        }

        List<NodeId> sameType = [];
        foreach (NodeId candidate in siblings)
        {
            if (string.Equals(DomTraversal.ElementLocalName(tree, candidate), local, StringComparison.Ordinal))
            {
                sameType.Add(candidate);
            }
        }

        int typePosition = sameType.IndexOf(node);
        if (typePosition < 0)
        {
            return;
        }

        if (typePosition == 0)
        {
            Add("first-of-type", sameType.GetRange(0, Math.Min(sameType.Count, 2)));
        }

        if (typePosition + 1 == sameType.Count)
        {
            int start = Math.Max(typePosition - 1, 0);
            Add("last-of-type", sameType.GetRange(start, sameType.Count - start));
        }

        if (sameType.Count <= 2)
        {
            Add("only-of-type", sameType);
        }

        Add("nth-of-type", sameType.GetRange(typePosition, sameType.Count - typePosition));
        Add("nth-last-of-type", sameType.GetRange(0, typePosition + 1));
    }

    private static void AddRemovedStructuralScopes(
        DomTree tree,
        InvalidationMap map,
        NodeId removed,
        NodeId parent,
        HashSet<NodeId> dirty)
    {
        List<NodeId> siblings = DomTraversal.ElementChildren(tree, parent);
        if (siblings.Count > 0)
        {
            AddStructuralCandidateScope(tree, map, "first-child", siblings[0], parent, dirty);
            AddStructuralCandidateScope(tree, map, "last-child", siblings[^1], parent, dirty);
        }

        if (siblings.Count <= 1)
        {
            foreach (NodeId candidate in siblings)
            {
                AddStructuralCandidateScope(tree, map, "only-child", candidate, parent, dirty);
            }
        }

        foreach (string state in new[] { "nth-child", "nth-last-child" })
        {
            foreach (NodeId candidate in siblings)
            {
                AddStructuralCandidateScope(tree, map, state, candidate, parent, dirty);
            }
        }

        if (DomTraversal.ElementLocalName(tree, removed) is not { } local)
        {
            return;
        }

        List<NodeId> sameType = [];
        foreach (NodeId candidate in siblings)
        {
            if (string.Equals(DomTraversal.ElementLocalName(tree, candidate), local, StringComparison.Ordinal))
            {
                sameType.Add(candidate);
            }
        }

        if (sameType.Count > 0)
        {
            AddStructuralCandidateScope(tree, map, "first-of-type", sameType[0], parent, dirty);
            AddStructuralCandidateScope(tree, map, "last-of-type", sameType[^1], parent, dirty);
        }

        if (sameType.Count <= 1)
        {
            foreach (NodeId candidate in sameType)
            {
                AddStructuralCandidateScope(tree, map, "only-of-type", candidate, parent, dirty);
            }
        }

        foreach (string state in new[] { "nth-of-type", "nth-last-of-type" })
        {
            foreach (NodeId candidate in sameType)
            {
                AddStructuralCandidateScope(tree, map, state, candidate, parent, dirty);
            }
        }
    }

    private static void AddInsertedSiblingScopes(
        DomTree tree,
        InvalidationMap map,
        NodeId node,
        NodeId parent,
        HashSet<NodeId> dirty)
    {
        List<NodeId> siblings = DomTraversal.ElementChildren(tree, parent);
        int position = siblings.IndexOf(node);
        if (position < 0)
        {
            return;
        }

        bool mayStartSiblingSelector = map.NodeMayStartSiblingSelector(new DomElementView(tree, node));
        if (map.HasAdjacentSiblingSelectors
            && (position != 0 || mayStartSiblingSelector)
            && position + 1 < siblings.Count)
        {
            AddStyleSubtree(tree, siblings[position + 1], dirty);
        }

        if (map.HasGeneralSiblingSelectors && mayStartSiblingSelector)
        {
            for (int index = position + 1; index < siblings.Count; index++)
            {
                AddStyleSubtree(tree, siblings[index], dirty);
            }
        }
    }

    private static void AddRemovedSiblingScopes(
        DomTree tree,
        InvalidationMap map,
        NodeId parent,
        HashSet<NodeId> dirty)
    {
        if (!map.HasAdjacentSiblingSelectors && !map.HasGeneralSiblingSelectors)
        {
            return;
        }

        // Removal records intentionally do not retain old sibling pointers.
        foreach (NodeId sibling in DomTraversal.ElementChildren(tree, parent))
        {
            AddStyleSubtree(tree, sibling, dirty);
        }
    }

    private static bool EmptyStateMayHaveChanged(
        DomTree tree,
        NodeId parent,
        IReadOnlyList<RetainedStyleMutation> mutations)
    {
        int relevantChildren = 0;
        foreach (NodeId child in tree.Children(parent))
        {
            Node? node = tree.GetNode(child);
            if (node is null)
            {
                continue;
            }

            if (node.IsElement || (node.TextContentOfTextNode is { Length: > 0 }))
            {
                relevantChildren++;
            }
        }

        int boundaryMutations = 0;
        foreach (RetainedStyleMutation mutation in mutations)
        {
            bool matches = mutation switch
            {
                RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Insert insert } =>
                    insert.NewParent == parent || insert.OldParent == parent,
                RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Remove remove } =>
                    remove.OldParent == parent,
                RetainedStyleMutation.Tree { Mutation: TreeStyleMutation.Text text } =>
                    text.Parent == parent,
                _ => false,
            };
            if (matches)
            {
                boundaryMutations++;
            }
        }

        // At least one relevant child untouched by this mutation batch proves the parent was
        // non-empty before and after every queued boundary operation.
        return relevantChildren <= boundaryMutations;
    }

    private static void AddEmptyParentScope(
        DomTree tree,
        InvalidationMap map,
        NodeId parent,
        IReadOnlyList<RetainedStyleMutation> mutations,
        HashSet<NodeId> dirty)
    {
        if (!EmptyStateMayHaveChanged(tree, parent, mutations))
        {
            return;
        }

        List<StructuralInvalidation> invalidations = [];
        foreach (StructuralInvalidation invalidation in map.StructuralInvalidations("empty"))
        {
            if (!invalidation.InsideRelational
                && invalidation.SubjectMayMatch(new DomElementView(tree, parent)))
            {
                invalidations.Add(invalidation);
            }
        }

        if (invalidations.Count == 0)
        {
            return;
        }

        AddStyleSubtree(tree, parent, dirty);
        foreach (StructuralInvalidation invalidation in invalidations)
        {
            if (invalidation.Reaches.Contains(InvalidationReaches.Siblings)
                || invalidation.Reaches.Contains(InvalidationReaches.Conservative))
            {
                AddFollowingSiblingSubtrees(tree, parent, dirty);
                return;
            }
        }
    }

    /// <summary>
    /// Convert old/new selector keys into a conservative set of fresh cascade roots.
    /// </summary>
    internal static RetainedStylePlan Plan(
        DomTree tree,
        Stylesheet sheet,
        IReadOnlyList<RetainedStyleMutation> mutations)
    {
        HashSet<NodeId> dirty = [];
        bool hasAnimationDamage = false;
        foreach (RetainedStyleMutation mutation in mutations)
        {
            switch (mutation)
            {
                case RetainedStyleMutation.Resource:
                    continue;
                case RetainedStyleMutation.Animation animation:
                    // Animated color and visibility inherit, keyframe endpoints can consume
                    // inherited custom properties, and generated pseudos are rebuilt with
                    // their originating element.
                    AddStyleSubtree(tree, animation.Node, dirty);
                    hasAnimationDamage = true;
                    continue;
                case RetainedStyleMutation.WaapiAnimation waapi:
                    dirty.Add(waapi.Node);
                    hasAnimationDamage = true;
                    continue;
                case RetainedStyleMutation.Tree tree_mutation:
                {
                    InvalidationMap map = sheet.InvalidationMap;
                    switch (tree_mutation.Mutation)
                    {
                        case TreeStyleMutation.Insert insert:
                        {
                            // Inserting or moving a style subtree changes the ordered author
                            // stylesheet, so parsing/index reuse is forbidden.
                            if (SubtreeContainsStyleElement(tree, insert.Node)
                                || NodeIsStyleText(tree, insert.Node, insert.OldParent)
                                || NodeIsStyleText(tree, insert.Node, insert.NewParent))
                            {
                                return RetainedStylePlan.Full.Instance;
                            }

                            if (!AddRelationalTreeInvalidation(tree, map, insert, dirty))
                            {
                                return RetainedStylePlan.Full.Instance;
                            }

                            AddStyleSubtree(tree, insert.Node, dirty);
                            if (insert.OldParent is { } oldParent)
                            {
                                AddStyleContextChain(tree, oldParent, dirty);
                                AddTableRowChildScope(tree, oldParent, dirty);
                                AddRemovedStructuralScopes(tree, map, insert.Node, oldParent, dirty);
                                AddRemovedSiblingScopes(tree, map, oldParent, dirty);
                                AddEmptyParentScope(tree, map, oldParent, mutations, dirty);
                            }

                            AddStyleContextChain(tree, insert.NewParent, dirty);
                            AddTableRowChildScope(tree, insert.NewParent, dirty);
                            AddInsertedStructuralScopes(tree, map, insert.Node, insert.NewParent, mutations, dirty);
                            AddInsertedSiblingScopes(tree, map, insert.Node, insert.NewParent, dirty);
                            AddEmptyParentScope(tree, map, insert.NewParent, mutations, dirty);
                            break;
                        }

                        case TreeStyleMutation.Remove remove:
                        {
                            if (SubtreeContainsStyleElement(tree, remove.Node)
                                || NodeIsStyleText(tree, remove.Node, remove.OldParent))
                            {
                                return RetainedStylePlan.Full.Instance;
                            }

                            if (!AddRelationalTreeInvalidation(tree, map, remove, dirty))
                            {
                                return RetainedStylePlan.Full.Instance;
                            }

                            AddStyleContextChain(tree, remove.OldParent, dirty);
                            AddTableRowChildScope(tree, remove.OldParent, dirty);
                            AddRemovedStructuralScopes(tree, map, remove.Node, remove.OldParent, dirty);
                            AddRemovedSiblingScopes(tree, map, remove.OldParent, dirty);
                            AddEmptyParentScope(tree, map, remove.OldParent, mutations, dirty);
                            break;
                        }

                        case TreeStyleMutation.Text text:
                        {
                            if (NodeIsStyleText(tree, text.Node, text.Parent))
                            {
                                return RetainedStylePlan.Full.Instance;
                            }

                            if (!AddRelationalTreeInvalidation(tree, map, text, dirty))
                            {
                                return RetainedStylePlan.Full.Instance;
                            }

                            if (text.Parent is { } textParent)
                            {
                                AddStyleContextChain(tree, textParent, dirty);
                                AddEmptyParentScope(tree, map, textParent, mutations, dirty);
                            }
                            else
                            {
                                dirty.Add(text.Node);
                            }

                            break;
                        }
                    }

                    continue;
                }
            }

            AttributeStyleMutation attribute = ((RetainedStyleMutation.Attribute)mutation).Mutation;
            string name = attribute.Name.ToLowerInvariant();
            switch (RetainedAttributeMutationKindOf(tree, attribute.Node, name))
            {
                case RetainedAttributeMutationKind.Full:
                    return RetainedStylePlan.Full.Instance;
                case RetainedAttributeMutationKind.Subtree:
                    // Mapped hints and native-control sizing enter the mutable LayoutStyle
                    // graph. Re-cascade from specified values before post-cascade used-value
                    // repair runs again.
                    AddStyleSubtree(tree, attribute.Node, dirty);
                    break;
            }

            InvalidationMap attributeMap = sheet.InvalidationMap;
            List<InvalidationDependency> dependencies = [.. attributeMap.AttributeDependencies(name)];
            if (string.Equals(name, "id", StringComparison.Ordinal))
            {
                if (attribute.OldValue is { } oldId)
                {
                    dependencies.AddRange(attributeMap.IdDependencies(oldId));
                }

                if (attribute.NewValue is { } newId)
                {
                    dependencies.AddRange(attributeMap.IdDependencies(newId));
                }
            }
            else if (string.Equals(name, "class", StringComparison.Ordinal))
            {
                foreach (string? value in new[] { attribute.OldValue, attribute.NewValue })
                {
                    if (value is null)
                    {
                        continue;
                    }

                    foreach (string className in value.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        dependencies.AddRange(attributeMap.ClassDependencies(className));
                    }
                }
            }

            // Several HTML boolean/value attributes also back selector pseudo states.
            string[] states = name switch
            {
                "checked" or "selected" => ["checked"],
                "disabled" => ["disabled", "enabled"],
                "dir" => ["dir"],
                "href" => ["any-link", "link", "visited"],
                "lang" or "xml:lang" => ["lang"],
                "open" => ["open"],
                "placeholder" or "value" => ["placeholder-shown"],
                "readonly" => ["read-only", "read-write"],
                "required" => ["required", "optional"],
                _ => [],
            };
            foreach (string state in states)
            {
                dependencies.AddRange(attributeMap.StateDependencies(state));
            }

            foreach (InvalidationDependency dependency in dependencies)
            {
                InvalidationReaches reaches = dependency.Reaches;
                if (reaches.Contains(InvalidationReaches.Conservative))
                {
                    return RetainedStylePlan.Full.Instance;
                }

                if (reaches.Contains(InvalidationReaches.Self)
                    || reaches.Contains(InvalidationReaches.Descendants))
                {
                    AddStyleSubtree(tree, attribute.Node, dirty);
                }

                if (reaches.Contains(InvalidationReaches.Siblings))
                {
                    AddFollowingSiblingSubtrees(tree, attribute.Node, dirty);
                }
            }
        }

        return new RetainedStylePlan.Reuse(dirty, hasAnimationDamage);
    }
}
