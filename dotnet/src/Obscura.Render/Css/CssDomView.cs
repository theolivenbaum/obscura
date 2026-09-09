using Obscura.Dom;

namespace Obscura.Render.Css;

/// <summary>
/// The real <see cref="ICssElementView"/> over <see cref="DomTree"/> plus
/// <see cref="NodeId"/>, standing in for the <c>&amp;DomTree, NodeId</c> pair the
/// Rust invalidation predicates take.
/// </summary>
public readonly struct DomElementView : ICssElementView
{
    private readonly DomTree _tree;
    private readonly Node? _node;

    public DomElementView(DomTree tree, NodeId nid)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
        _node = tree.GetNode(nid);
    }

    public bool IsElement => _node?.IsElement ?? false;

    public string LocalName => _node?.AsElement()?.Name.Local ?? string.Empty;

    public string? GetAttribute(string name) => _node?.GetAttribute(name);

    public IReadOnlyList<string> AttributeNames
    {
        get
        {
            if (_node?.Attrs is not { } attributes || attributes.Count == 0)
            {
                return [];
            }

            var names = new string[attributes.Count];
            for (var index = 0; index < attributes.Count; index++)
            {
                names[index] = attributes[index].Name.Local;
            }

            return names;
        }
    }

    public bool IsQuirks => _tree?.IsQuirks ?? false;
}

/// <summary>Convenience wrappers for the invalidation predicates over the live tree.</summary>
public static class CssInvalidationDom
{
    /// <summary>Wrap one node as the element view the invalidation map needs.</summary>
    public static DomElementView View(DomTree tree, NodeId nid) => new(tree, nid);

    /// <summary>Rust <c>RelationalInvalidation::anchor_may_match</c> over the live tree.</summary>
    public static bool AnchorMayMatch(this RelationalInvalidation invalidation, DomTree tree, NodeId nid)
    {
        ArgumentNullException.ThrowIfNull(invalidation);
        return invalidation.AnchorMayMatch(new DomElementView(tree, nid));
    }

    /// <summary>Rust <c>RelationalInvalidation::relative_path_may_match</c> over the live tree.</summary>
    public static bool RelativePathMayMatch(this RelationalInvalidation invalidation, DomTree tree, NodeId nid)
    {
        ArgumentNullException.ThrowIfNull(invalidation);
        return invalidation.RelativePathMayMatch(new DomElementView(tree, nid));
    }

    /// <summary>Rust <c>StructuralInvalidation::subject_may_match</c> over the live tree.</summary>
    public static bool SubjectMayMatch(this StructuralInvalidation invalidation, DomTree tree, NodeId nid)
    {
        ArgumentNullException.ThrowIfNull(invalidation);
        return invalidation.SubjectMayMatch(new DomElementView(tree, nid));
    }

    /// <summary>Rust <c>InvalidationMap::node_may_start_sibling_selector</c> over the live tree.</summary>
    public static bool NodeMayStartSiblingSelector(this InvalidationMap map, DomTree tree, NodeId nid)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.NodeMayStartSiblingSelector(new DomElementView(tree, nid));
    }
}
