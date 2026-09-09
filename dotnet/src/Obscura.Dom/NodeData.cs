namespace Obscura.Dom;

/// <summary>One attribute of an element, keyed by a qualified name.</summary>
public sealed class Attribute(QualName name, string value)
{
    public QualName Name = name;
    public string Value = value;

    public string QualifiedName => Name.Prefix is null ? Name.Local : $"{Name.Prefix}:{Name.Local}";

    /// <summary>
    /// Compare against a qualified name (<c>prefix:local</c>) without allocating. Attribute lookup
    /// is by qualified name everywhere; matching on the local name alone would miss a parsed
    /// namespaced attribute such as <c>xlink:href</c> and push a duplicate on write.
    /// </summary>
    public bool QualifiedNameEquals(string name)
    {
        var prefix = Name.Prefix;
        if (prefix is null)
        {
            return string.Equals(Name.Local, name, StringComparison.Ordinal);
        }

        return name.Length == prefix.Length + Name.Local.Length + 1
            && name.AsSpan(0, prefix.Length).SequenceEqual(prefix)
            && name[prefix.Length] == ':'
            && name.AsSpan(prefix.Length + 1).SequenceEqual(Name.Local);
    }

    public Attribute Clone() => new(Name, Value);

    public override string ToString() => $"{QualifiedName}=\"{Value}\"";
}

/// <summary>The payload of a <see cref="Node"/>; the port of html5ever's <c>NodeData</c> enum.</summary>
public abstract class NodeData
{
    private protected NodeData() { }

    /// <summary>A document (also used for shadow roots and template contents fragments).</summary>
    public static DocumentData Document => DocumentData.Instance;

    public static DoctypeData Doctype(string name, string publicId, string systemId) =>
        new(name, publicId, systemId);

    public static ElementData Element(
        QualName name,
        List<Attribute>? attrs = null,
        NodeId? templateContents = null,
        bool mathmlAnnotationXmlIntegrationPoint = false) =>
        new(name, attrs ?? [], templateContents, mathmlAnnotationXmlIntegrationPoint);

    public static TextData Text(string contents) => new(contents);

    public static CommentData Comment(string contents) => new(contents);

    public static ProcessingInstructionData ProcessingInstruction(string target, string data) =>
        new(target, data);

    /// <summary>Deep copy, matching Rust's <c>#[derive(Clone)]</c> on <c>NodeData</c>.</summary>
    public abstract NodeData Clone();
}

public sealed class DocumentData : NodeData
{
    internal static readonly DocumentData Instance = new();

    private DocumentData() { }

    /// <summary>Document payloads carry no data, so a clone can share the instance.</summary>
    public override NodeData Clone() => this;
}

public sealed class DoctypeData(string name, string publicId, string systemId) : NodeData
{
    public string Name = name;
    public string PublicId = publicId;
    public string SystemId = systemId;

    public override NodeData Clone() => new DoctypeData(Name, PublicId, SystemId);
}

public sealed class ElementData(
    QualName name,
    List<Attribute> attrs,
    NodeId? templateContents,
    bool mathmlAnnotationXmlIntegrationPoint) : NodeData
{
    public QualName Name = name;
    public List<Attribute> Attrs = attrs;
    public NodeId? TemplateContents = templateContents;
    public bool MathmlAnnotationXmlIntegrationPoint = mathmlAnnotationXmlIntegrationPoint;

    public override NodeData Clone()
    {
        var attrs = new List<Attribute>(Attrs.Count);
        foreach (var attr in Attrs)
        {
            attrs.Add(attr.Clone());
        }

        return new ElementData(Name, attrs, TemplateContents, MathmlAnnotationXmlIntegrationPoint);
    }
}

public sealed class TextData(string contents) : NodeData
{
    public string Contents = contents;

    public override NodeData Clone() => new TextData(Contents);
}

public sealed class CommentData(string contents) : NodeData
{
    public string Contents = contents;

    public override NodeData Clone() => new CommentData(Contents);
}

public sealed class ProcessingInstructionData(string target, string data) : NodeData
{
    public string Target = target;
    public string Data = data;

    public override NodeData Clone() => new ProcessingInstructionData(Target, Data);
}
