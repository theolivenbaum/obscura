namespace Obscura.Dom;

/// <summary>One arena node. Links are <see cref="NodeId"/>s, never references.</summary>
public sealed class Node
{
    public NodeId Id;

    /// <summary>
    /// Shadow-including document connectivity, maintained incrementally on insertion/removal so hot
    /// DOM mutation paths do not walk every ancestor.
    /// </summary>
    public bool Connected;

    public NodeId? Parent;
    public NodeId? FirstChild;
    public NodeId? LastChild;
    public NodeId? PrevSibling;
    public NodeId? NextSibling;
    public NodeData Data;

    internal Node(NodeId id, NodeData data)
    {
        Id = id;
        Data = data;
    }

    public bool IsDocument => Data is DocumentData;

    public bool IsElement => Data is ElementData;

    public bool IsText => Data is TextData;

    /// <summary>The element payload, or null for a non-element node.</summary>
    public ElementData? AsElement() => Data as ElementData;

    /// <summary>The element's qualified name, or null for a non-element node.</summary>
    public QualName? ElementName => Data is ElementData element ? element.Name : null;

    /// <summary>The element's attributes, or null for a non-element node.</summary>
    public List<Attribute>? Attrs => (Data as ElementData)?.Attrs;

    public string? GetAttribute(string name)
    {
        if (Data is not ElementData element)
        {
            return null;
        }

        var attrs = element.Attrs;
        for (var i = 0; i < attrs.Count; i++)
        {
            if (attrs[i].QualifiedNameEquals(name))
            {
                return attrs[i].Value;
            }
        }

        return null;
    }

    public void SetAttribute(string name, string value)
    {
        if (Data is not ElementData element)
        {
            return;
        }

        // Match by qualified name, consistent with GetAttribute and the remove_attribute op. A
        // parsed namespaced attribute is stored with a separate prefix (e.g. xlink:href ->
        // prefix="xlink", local="href"); matching on local name alone would miss it and push a
        // duplicate.
        var attrs = element.Attrs;
        for (var i = 0; i < attrs.Count; i++)
        {
            if (attrs[i].QualifiedNameEquals(name))
            {
                attrs[i].Value = value;
                return;
            }
        }

        attrs.Add(new Attribute(QualName.Attr(name), value));
    }

    public void RemoveAttribute(string name)
    {
        if (Data is not ElementData element)
        {
            return;
        }

        element.Attrs.RemoveAll(a => a.QualifiedNameEquals(name));
    }

    /// <summary>Read a namespaced attribute by (namespace, localName).</summary>
    public string? GetAttributeNs(string ns, string local)
    {
        if (Data is not ElementData element)
        {
            return null;
        }

        foreach (var attr in element.Attrs)
        {
            if (string.Equals(attr.Name.Ns, ns, StringComparison.Ordinal)
                && string.Equals(attr.Name.Local, local, StringComparison.Ordinal))
            {
                return attr.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Set a namespaced attribute using a proper qualified name: prefix and local name remain
    /// separate while qualified-name APIs and serialization reconstruct <c>prefix:local</c>.
    /// </summary>
    public void SetAttributeNs(string ns, string qualified, string value)
    {
        if (Data is not ElementData element)
        {
            return;
        }

        string? prefix = null;
        var local = qualified;
        var colon = qualified.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            prefix = qualified[..colon];
            local = qualified[(colon + 1)..];
        }

        foreach (var attr in element.Attrs)
        {
            if (string.Equals(attr.Name.Ns, ns, StringComparison.Ordinal)
                && string.Equals(attr.Name.Local, local, StringComparison.Ordinal))
            {
                attr.Name = attr.Name with { Prefix = prefix };
                attr.Value = value;
                return;
            }
        }

        element.Attrs.Add(new Attribute(new QualName(prefix, ns, local), value));
    }

    public void RemoveAttributeNs(string ns, string local)
    {
        if (Data is not ElementData element)
        {
            return;
        }

        element.Attrs.RemoveAll(a =>
            string.Equals(a.Name.Ns, ns, StringComparison.Ordinal)
            && string.Equals(a.Name.Local, local, StringComparison.Ordinal));
    }

    public string? TextContentOfTextNode => (Data as TextData)?.Contents;
}
