namespace Obscura.Dom.Selectors;

/// <summary>
/// A node in the tree viewed as a selector-matching element. The port of the reference engine's
/// <c>Element</c> trait implementation.
/// </summary>
public readonly struct DomElement(DomTree tree, NodeId nodeId) : IEquatable<DomElement>
{
    public DomTree Tree { get; } = tree;

    public NodeId NodeId { get; } = nodeId;

    public static DomElement New(DomTree tree, NodeId nodeId) => new(tree, nodeId);

    private Node? Node => Tree.GetNode(NodeId);

    /// <summary>
    /// The stable identity of this element. Must be stable per node: DomElement is a value type and
    /// gets a fresh copy on every traversal step, so an address-based identity would differ for the
    /// same node on each call, which breaks <c>:has</c> anchor matching.
    /// </summary>
    public NodeId Opaque => NodeId;

    public bool IsElement => Node?.IsElement ?? false;

    public DomElement? ParentElement()
    {
        if (Node?.Parent is not { } parentId)
        {
            return null;
        }

        var parent = Tree.GetNode(parentId);
        return parent is { IsElement: true } ? new DomElement(Tree, parentId) : null;
    }

    public bool ParentNodeIsShadowRoot() =>
        Node?.Parent is { } parent && Tree.IsShadowRoot(parent);

    public DomElement? ContainingShadowHost()
    {
        if (Tree.ContainingShadowRoot(NodeId) is not { } root
            || Tree.ShadowRootInfo(root) is not { } info)
        {
            return null;
        }

        return new DomElement(Tree, info.Host);
    }

    public DomElement? PseudoElementOriginatingElement() => null;

    public bool IsPseudoElement => false;

    public DomElement? PrevSiblingElement()
    {
        var current = Node?.PrevSibling;
        while (current is { } siblingId)
        {
            var sibling = Tree.GetNode(siblingId);
            if (sibling is null)
            {
                return null;
            }

            if (sibling.IsElement)
            {
                return new DomElement(Tree, siblingId);
            }

            current = sibling.PrevSibling;
        }

        return null;
    }

    public DomElement? NextSiblingElement()
    {
        var current = Node?.NextSibling;
        while (current is { } siblingId)
        {
            var sibling = Tree.GetNode(siblingId);
            if (sibling is null)
            {
                return null;
            }

            if (sibling.IsElement)
            {
                return new DomElement(Tree, siblingId);
            }

            current = sibling.NextSibling;
        }

        return null;
    }

    public DomElement? FirstElementChild()
    {
        var current = Node?.FirstChild;
        while (current is { } childId)
        {
            var child = Tree.GetNode(childId);
            if (child is null)
            {
                return null;
            }

            if (child.IsElement)
            {
                return new DomElement(Tree, childId);
            }

            current = child.NextSibling;
        }

        return null;
    }

    public bool IsHtmlElementInHtmlDocument() =>
        Node?.ElementName is { } name
        && string.Equals(name.Ns, Namespaces.Html, StringComparison.Ordinal);

    public bool HasLocalName(string localName) =>
        Node?.ElementName is { } name
        && string.Equals(name.Local, localName, StringComparison.Ordinal);

    public bool HasNamespace(string ns) =>
        Node?.ElementName is { } name && string.Equals(name.Ns, ns, StringComparison.Ordinal);

    public bool IsSameType(DomElement other)
    {
        if (Node?.ElementName is not { } a || other.Node?.ElementName is not { } b)
        {
            return false;
        }

        return string.Equals(a.Local, b.Local, StringComparison.Ordinal)
            && string.Equals(a.Ns, b.Ns, StringComparison.Ordinal);
    }

    public bool AttrMatches(
        NamespaceConstraintKind namespaceKind,
        string? namespaceUrl,
        string localName,
        AttrOperator? op,
        string? value,
        CaseSensitivity caseSensitivity)
    {
        var attrs = Node?.Attrs;
        if (attrs is null)
        {
            return false;
        }

        foreach (var attr in attrs)
        {
            var nsMatch = namespaceKind switch
            {
                NamespaceConstraintKind.Any => true,
                NamespaceConstraintKind.Specific =>
                    string.Equals(attr.Name.Ns, namespaceUrl, StringComparison.Ordinal),
                _ => string.Equals(attr.Name.Ns, Namespaces.None, StringComparison.Ordinal),
            };

            if (!nsMatch || !string.Equals(attr.Name.Local, localName, StringComparison.Ordinal))
            {
                continue;
            }

            if (op is not { } attrOperator)
            {
                return true;
            }

            if (AttrEvaluation.Eval(attr.Value, value!, attrOperator, caseSensitivity))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsLink()
    {
        var node = Node;
        if (node?.ElementName is not { } name)
        {
            return false;
        }

        return name.Local switch
        {
            "a" or "area" or "link" => node.GetAttribute("href") is not null,
            _ => false,
        };
    }

    public bool IsHtmlSlotElement() => Tree.IsHtmlSlotElement(NodeId);

    public DomElement? AssignedSlot() =>
        Tree.AssignedSlot(NodeId) is { } slot ? new DomElement(Tree, slot) : null;

    public bool HasId(string id, CaseSensitivity caseSensitivity) =>
        Node?.GetAttribute("id") is { } value && AttrEvaluation.Eq(value, id, caseSensitivity);

    public bool HasClass(string name, CaseSensitivity caseSensitivity)
    {
        if (Node?.GetAttribute("class") is not { } classAttr)
        {
            return false;
        }

        foreach (var range in classAttr.AsSpan().SplitAny(AttrEvaluation.SelectorWhitespace))
        {
            var candidate = classAttr.AsSpan()[range];
            if (candidate.Length != 0 && AttrEvaluation.Eq(candidate, name, caseSensitivity))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Is this a form control element that <c>:enabled</c>/<c>:disabled</c> apply to?</summary>
    public bool IsFormControl() => Node?.ElementName?.Local switch
    {
        "input" or "button" or "select" or "textarea" or "optgroup" or "option" or "fieldset" => true,
        _ => false,
    };

    /// <summary>
    /// A boolean HTML attribute's presence (its value does not matter: per HTML, <c>disabled=""</c>,
    /// <c>disabled="disabled"</c>, and bare <c>disabled</c> are all equally "set").
    /// </summary>
    public bool HasBooleanAttr(string name) => Node?.GetAttribute(name) is not null;

    public bool IsEmpty()
    {
        var node = Node;
        if (node is null)
        {
            return true;
        }

        var child = node.FirstChild;
        while (child is { } childId)
        {
            var childNode = Tree.GetNode(childId);
            if (childNode is null)
            {
                break;
            }

            switch (childNode.Data)
            {
                case ElementData:
                    return false;
                case TextData { Contents.Length: > 0 }:
                    return false;
            }

            child = childNode.NextSibling;
        }

        return true;
    }

    public bool IsRoot()
    {
        if (Node?.Parent is not { } parentId)
        {
            return false;
        }

        return !Tree.IsShadowRoot(parentId) && (Tree.GetNode(parentId)?.IsDocument ?? false);
    }

    public bool Equals(DomElement other) => NodeId == other.NodeId;

    public override bool Equals(object? obj) => obj is DomElement other && Equals(other);

    public override int GetHashCode() => NodeId.GetHashCode();

    public static bool operator ==(DomElement left, DomElement right) => left.Equals(right);

    public static bool operator !=(DomElement left, DomElement right) => !left.Equals(right);

    public override string ToString() => $"DomElement({NodeId})";
}

/// <summary>Attribute selector value comparison, ported from the reference <c>attr</c> module.</summary>
internal static class AttrEvaluation
{
    /// <summary>The definition of whitespace per CSS Selectors Level 3 section 4.</summary>
    internal const string SelectorWhitespace = " \t\n\r\f";

    internal static bool Eq(ReadOnlySpan<char> a, ReadOnlySpan<char> b, CaseSensitivity caseSensitivity) =>
        caseSensitivity == CaseSensitivity.CaseSensitive
            ? a.SequenceEqual(b)
            : a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string haystack, string needle, CaseSensitivity caseSensitivity) =>
        haystack.Contains(
            needle,
            caseSensitivity == CaseSensitivity.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase);

    internal static bool Eval(string elementValue, string selectorValue, AttrOperator op, CaseSensitivity cs)
    {
        var e = elementValue.AsSpan();
        var s = selectorValue.AsSpan();
        switch (op)
        {
            case AttrOperator.Equal:
                return Eq(e, s, cs);
            case AttrOperator.Prefix:
                return !s.IsEmpty && e.Length >= s.Length && Eq(e[..s.Length], s, cs);
            case AttrOperator.Suffix:
                return !s.IsEmpty && e.Length >= s.Length && Eq(e[(e.Length - s.Length)..], s, cs);
            case AttrOperator.Substring:
                return !s.IsEmpty && Contains(elementValue, selectorValue, cs);
            case AttrOperator.Includes:
            {
                if (s.IsEmpty)
                {
                    return false;
                }

                foreach (var range in elementValue.AsSpan().SplitAny(SelectorWhitespace))
                {
                    if (Eq(elementValue.AsSpan()[range], s, cs))
                    {
                        return true;
                    }
                }

                return false;
            }

            case AttrOperator.DashMatch:
                return Eq(e, s, cs)
                    || (e.Length > s.Length && e[s.Length] == '-' && Eq(e[..s.Length], s, cs));
            default:
                return false;
        }
    }
}
