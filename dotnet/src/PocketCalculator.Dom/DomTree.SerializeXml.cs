using System.Text;

namespace PocketCalculator.Dom;

/// <summary>
/// DOM Parsing's XML serialization, for <c>XMLSerializer</c> and for <c>innerHTML</c> /
/// <c>outerHTML</c> of nodes in an XML document. Port addition: the Rust engine serializes
/// everything as HTML.
/// </summary>
/// <remarks>
/// Follows "produce an XML serialization" with the simplifications Chromium's output does not
/// distinguish: an element whose namespace differs from the inherited default namespace and has
/// no prefix gets an <c>xmlns</c> declaration (so an HTML element serializes with
/// <c>xmlns="http://www.w3.org/1999/xhtml"</c> at the top), a prefixed element or attribute
/// whose prefix is not yet declared gets an <c>xmlns:prefix</c> declaration, an empty HTML void
/// element is <c>&lt;br /&gt;</c>, another empty HTML element <c>&lt;p&gt;&lt;/p&gt;</c>, and any
/// other empty element <c>&lt;a/&gt;</c>. Well-formedness is not checked.
/// </remarks>
public sealed partial class DomTree
{
    /// <summary>The XML serialization of <paramref name="nodeId"/> itself.</summary>
    public string OuterXml(NodeId nodeId)
    {
        var sb = new StringBuilder();
        SerializeXmlNode(sb, nodeId, inheritedNs: null, prefixes: null, depth: 0);
        return sb.ToString();
    }

    /// <summary>The XML serialization of <paramref name="nodeId"/>'s children.</summary>
    public string InnerXml(NodeId nodeId)
    {
        var sb = new StringBuilder();
        string? ns = Slot(nodeId)?.Data is ElementData element ? NullIfEmpty(element.Name.Ns) : null;
        foreach (var child in ChildrenForXml(nodeId))
        {
            SerializeXmlNode(sb, child, ns, prefixes: null, depth: 0);
        }

        return sb.ToString();
    }

    private List<NodeId> ChildrenForXml(NodeId nodeId) =>
        Slot(nodeId)?.Data is ElementData { TemplateContents: { } contents } ? Children(contents) : Children(nodeId);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private void SerializeXmlNode(StringBuilder sb, NodeId nodeId, string? inheritedNs, Dictionary<string, string>? prefixes, int depth)
    {
        if (depth > HtmlParsing.MaxParserTreeDepth * 4)
        {
            return;
        }

        WorkCancellation.ThrowIfCancellationRequested();
        var node = Slot(nodeId);
        switch (node?.Data)
        {
            case null:
                return;
            case DocumentData:
                foreach (var child in Children(nodeId))
                {
                    SerializeXmlNode(sb, child, inheritedNs, prefixes, depth + 1);
                }

                return;
            case DoctypeData doctype:
                sb.Append("<!DOCTYPE ").Append(doctype.Name);
                if (doctype.PublicId.Length != 0)
                {
                    sb.Append(" PUBLIC \"").Append(doctype.PublicId).Append('"');
                }
                else if (doctype.SystemId.Length != 0)
                {
                    sb.Append(" SYSTEM");
                }

                if (doctype.SystemId.Length != 0)
                {
                    sb.Append(" \"").Append(doctype.SystemId).Append('"');
                }

                sb.Append('>');
                return;
            case TextData text:
                EscapeXml(sb, text.Contents, attribute: false);
                return;
            case CommentData comment:
                sb.Append("<!--").Append(comment.Contents).Append("-->");
                return;
            case ProcessingInstructionData pi:
                sb.Append("<?").Append(pi.Target).Append(' ').Append(pi.Data).Append("?>");
                return;
            case ElementData element:
                SerializeXmlElement(sb, nodeId, element, inheritedNs, prefixes, depth);
                return;
        }
    }

    private void SerializeXmlElement(StringBuilder sb, NodeId nodeId, ElementData element, string? inheritedNs, Dictionary<string, string>? prefixes, int depth)
    {
        var ns = NullIfEmpty(element.Name.Ns);
        var prefix = element.Name.Prefix;
        var name = prefix is null ? element.Name.Local : prefix + ":" + element.Name.Local;
        sb.Append('<').Append(name);

        var defaultNs = inheritedNs;
        Dictionary<string, string>? scope = prefixes;
        var declaresDefault = false;
        foreach (var attr in element.Attrs)
        {
            if (string.Equals(attr.Name.Ns, Namespaces.XmlNs, StringComparison.Ordinal))
            {
                if (attr.Name.Prefix is null && string.Equals(attr.Name.Local, "xmlns", StringComparison.Ordinal))
                {
                    declaresDefault = true;
                    defaultNs = NullIfEmpty(attr.Value);
                }
                else
                {
                    scope = new Dictionary<string, string>(scope ?? [], StringComparer.Ordinal) { [attr.Name.Local] = attr.Value };
                }
            }
        }

        if (prefix is null)
        {
            if (!declaresDefault && !string.Equals(ns, inheritedNs, StringComparison.Ordinal))
            {
                sb.Append(" xmlns=\"");
                EscapeXml(sb, ns ?? string.Empty, attribute: true);
                sb.Append('"');
                defaultNs = ns;
            }
        }
        else if (ns is not null && (scope is null || !scope.TryGetValue(prefix, out var bound) || !string.Equals(bound, ns, StringComparison.Ordinal)))
        {
            sb.Append(" xmlns:").Append(prefix).Append("=\"");
            EscapeXml(sb, ns, attribute: true);
            sb.Append('"');
            scope = new Dictionary<string, string>(scope ?? [], StringComparer.Ordinal) { [prefix] = ns };
        }

        foreach (var attr in element.Attrs)
        {
            var attrPrefix = attr.Name.Prefix;
            var attrNs = NullIfEmpty(attr.Name.Ns);
            if (attrPrefix is not null && attrNs is not null
                && !string.Equals(attrNs, Namespaces.XmlNs, StringComparison.Ordinal)
                && !string.Equals(attrNs, Namespaces.Xml, StringComparison.Ordinal)
                && (scope is null || !scope.TryGetValue(attrPrefix, out var boundAttr) || !string.Equals(boundAttr, attrNs, StringComparison.Ordinal)))
            {
                sb.Append(" xmlns:").Append(attrPrefix).Append("=\"");
                EscapeXml(sb, attrNs, attribute: true);
                sb.Append('"');
                scope = new Dictionary<string, string>(scope ?? [], StringComparer.Ordinal) { [attrPrefix] = attrNs };
            }

            sb.Append(' ').Append(attr.QualifiedName).Append("=\"");
            EscapeXml(sb, attr.Value, attribute: true);
            sb.Append('"');
        }

        var children = ChildrenForXml(nodeId);
        var html = string.Equals(ns, Namespaces.Html, StringComparison.Ordinal);
        if (children.Count == 0)
        {
            if (html && IsVoidElement(element.Name.Local))
            {
                sb.Append(" />");
            }
            else if (html)
            {
                sb.Append("></").Append(name).Append('>');
            }
            else
            {
                sb.Append("/>");
            }

            return;
        }

        sb.Append('>');
        foreach (var child in children)
        {
            SerializeXmlNode(sb, child, defaultNs, scope, depth + 1);
        }

        sb.Append("</").Append(name).Append('>');
    }

    private static void EscapeXml(StringBuilder sb, string value, bool attribute)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"' when attribute:
                    sb.Append("&quot;");
                    break;
                case '\t' when attribute:
                    sb.Append("&#9;");
                    break;
                case '\n' when attribute:
                    sb.Append("&#10;");
                    break;
                case '\r' when attribute:
                    sb.Append("&#13;");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }
}
