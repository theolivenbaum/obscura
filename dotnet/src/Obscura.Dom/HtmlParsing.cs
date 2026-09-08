using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Obscura.Dom;

/// <summary>
/// HTML parsing into the arena tree.
///
/// AngleSharp supplies the spec HTML5 tokenizer and tree builder, standing in for html5ever; its
/// result is walked exactly once and adapted into Obscura's arena. AngleSharp types never escape
/// this file.
/// </summary>
public static class HtmlParsing
{
    private static readonly HtmlParserOptions Options = new()
    {
        // Obscura parses in a scripting-enabled document, which is also html5ever's default. It
        // changes context-sensitive content such as <noscript>.
        IsScripting = true,
    };

    [ThreadStatic]
    private static HtmlParser? _parser;

    [ThreadStatic]
    private static IDocument? _contextDocument;

    private static HtmlParser Parser => _parser ??= new HtmlParser(Options);

    private static IDocument ContextDocument => _contextDocument ??= Parser.ParseDocument("");

    public static DomTree ParseHtml(string html)
    {
        var tree = new DomTree();
        tree.SetAllowDeclarativeShadowRoots(true);
        var document = Parser.ParseDocument(html);
        // Only full quirks mode makes CSS class/id selectors case-insensitive; limited-quirks
        // behaves like no-quirks for selector matching, and that is exactly what compatMode
        // distinguishes ("BackCompat" only for full quirks).
        tree.SetQuirks(string.Equals(document.CompatMode, "BackCompat", StringComparison.Ordinal));
        Adapt(tree, tree.Document, document.ChildNodes);
        return tree;
    }

    public static DomTree ParseFragment(string html) =>
        ParseFragmentWithContext(html, QualName.Html("body"));

    /// <summary>
    /// Parse an HTML fragment using the supplied context element.
    ///
    /// The tree builder's insertion mode depends on this context. Treating every innerHTML
    /// assignment as body content drops table-only elements such as a top-level <c>&lt;tr&gt;</c>
    /// and mis-parses select/template fragments. Browsers instead use the receiver element as the
    /// fragment parsing context.
    /// </summary>
    public static DomTree ParseFragmentWithContext(string html, QualName contextName)
    {
        var tree = new DomTree();
        // Fragment parsing deliberately leaves declarative shadow roots disabled.
        // The synthetic <html> root mirrors the wrapper html5ever's parse_fragment produces, which
        // is what FragmentRoot() and the innerHTML op walk.
        var root = tree.NewNode(NodeData.Element(QualName.Html("html")));
        tree.AppendChild(tree.Document, root);

        var context = CreateContextElement(contextName);
        var nodes = Parser.ParseFragment(html, context);
        Adapt(tree, root, nodes);
        return tree;
    }

    private static IElement CreateContextElement(QualName name)
    {
        var document = ContextDocument;
        if (string.IsNullOrEmpty(name.Ns)
            || string.Equals(name.Ns, Namespaces.Html, StringComparison.Ordinal))
        {
            return document.CreateElement(name.Local);
        }

        return document.CreateElement(name.Ns, name.Local);
    }

    // ------------------------------------------------------------------ the walk

    private static void Adapt(DomTree tree, NodeId destParent, INodeList nodes)
    {
        // Explicit stack: a deeply nested source document must not overflow the thread stack.
        var stack = new Stack<(NodeId Parent, INode Node)>();
        PushAll(stack, destParent, nodes);

        while (stack.Count > 0)
        {
            var (parent, node) = stack.Pop();
            switch (node)
            {
                case IElement element:
                    AdaptElement(tree, parent, element, stack);
                    break;

                case IText text:
                    // Matches the tree sink's text handling: coalesce into a trailing text node.
                    tree.AppendText(parent, text.Data);
                    break;

                case IComment comment:
                    tree.AppendChild(parent, tree.NewNode(NodeData.Comment(comment.Data)));
                    break;

                case IDocumentType doctype:
                    tree.AppendChild(parent, tree.NewNode(NodeData.Doctype(
                        doctype.Name,
                        doctype.PublicIdentifier ?? "",
                        doctype.SystemIdentifier ?? "")));
                    break;

                case IProcessingInstruction pi:
                    tree.AppendChild(
                        parent,
                        tree.NewNode(NodeData.ProcessingInstruction(pi.Target, pi.Data)));
                    break;
            }
        }
    }

    private static void PushAll(Stack<(NodeId Parent, INode Node)> stack, NodeId parent, INodeList nodes)
    {
        for (var i = nodes.Length - 1; i >= 0; i--)
        {
            stack.Push((parent, nodes[i]));
        }
    }

    private static void AdaptElement(
        DomTree tree,
        NodeId parent,
        IElement element,
        Stack<(NodeId Parent, INode Node)> stack)
    {
        var name = ElementName(element);
        var attrs = ElementAttributes(element);
        var isTemplate = element is IHtmlTemplateElement
            && string.Equals(name.Ns, Namespaces.Html, StringComparison.Ordinal);

        var id = tree.NewNode(NodeData.Element(
            name,
            attrs,
            templateContents: null,
            mathmlAnnotationXmlIntegrationPoint: IsMathmlAnnotationXmlIntegrationPoint(name, attrs)));

        NodeId? contents = null;
        if (isTemplate)
        {
            contents = tree.NewNode(NodeData.Document);
            if (tree.GetNode(id)?.Data is ElementData data)
            {
                data.TemplateContents = contents;
            }
        }

        var consumed = isTemplate
            && AllowDeclarativeShadowRoots(tree, parent)
            && AttachDeclarativeShadow(tree, parent, id, attrs);

        if (!consumed)
        {
            tree.AppendChild(parent, id);
        }

        if (isTemplate && element is IHtmlTemplateElement template && contents is { } contentsId)
        {
            PushAll(stack, contentsId, template.Content.ChildNodes);
            return;
        }

        // Defensive: AngleSharp does not implement declarative shadow DOM today, but if a future
        // version attaches roots itself, mirror them instead of dropping the content.
        if (element.ShadowRoot is { } shadow && tree.ShadowRootOf(id) is null)
        {
            var mode = shadow.Mode == AngleSharp.Dom.ShadowRootMode.Closed
                ? ShadowRootMode.Closed
                : ShadowRootMode.Open;
            var rootId = tree.NewNode(NodeData.Document);
            if (tree.AttachShadowRootNode(id, rootId, mode) == AttachShadowError.None)
            {
                PushAll(stack, rootId, shadow.ChildNodes);
            }
        }

        PushAll(stack, id, element.ChildNodes);
    }

    private static QualName ElementName(IElement element)
    {
        var prefix = string.IsNullOrEmpty(element.Prefix) ? null : element.Prefix;
        return new QualName(prefix, element.NamespaceUri ?? Namespaces.None, element.LocalName);
    }

    private static List<Attribute> ElementAttributes(IElement element)
    {
        var attrs = new List<Attribute>(element.Attributes.Length);
        foreach (var attr in element.Attributes)
        {
            var ns = attr.NamespaceUri ?? Namespaces.None;
            var prefix = string.IsNullOrEmpty(attr.Prefix)
                ? PrefixForNamespace(ns, attr.LocalName)
                : attr.Prefix;
            attrs.Add(new Attribute(new QualName(prefix, ns, attr.LocalName), attr.Value));
        }

        return attrs;
    }

    // The HTML parser's foreign-attribute adjustment table gives xlink/xml/xmlns attributes a
    // namespace; html5ever keeps the source prefix alongside it, which serialization needs.
    private static string? PrefixForNamespace(string ns, string local) => ns switch
    {
        Namespaces.XLink => "xlink",
        Namespaces.Xml => "xml",
        Namespaces.XmlNs when !string.Equals(local, "xmlns", StringComparison.Ordinal) => "xmlns",
        _ => null,
    };

    private static bool IsMathmlAnnotationXmlIntegrationPoint(QualName name, List<Attribute> attrs)
    {
        if (!string.Equals(name.Ns, Namespaces.MathMl, StringComparison.Ordinal)
            || !string.Equals(name.Local, "annotation-xml", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var attr in attrs)
        {
            if (string.Equals(attr.Name.Local, "encoding", StringComparison.Ordinal)
                && (attr.Value.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                    || attr.Value.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ declarative shadow roots

    /// <summary>
    /// The tree sink's <c>allow_declarative_shadow_roots</c> hook: whether a declarative shadow
    /// template inserted into <paramref name="intendedParent"/> may be consumed.
    /// </summary>
    internal static bool AllowDeclarativeShadowRoots(DomTree tree, NodeId intendedParent) =>
        tree.AllowsDeclarativeShadowRoots
        && IsValidShadowHost(tree, intendedParent)
        && tree.ShadowRootOf(intendedParent) is null;

    /// <summary>
    /// The tree sink's <c>attach_declarative_shadow</c> hook: turn a parsed
    /// <c>&lt;template shadowrootmode&gt;</c> into the intended parent's native shadow root.
    /// </summary>
    internal static bool AttachDeclarativeShadow(
        DomTree tree,
        NodeId location,
        NodeId template,
        IReadOnlyList<Attribute> attrs)
    {
        ShadowRootMode? mode = null;
        foreach (var attr in attrs)
        {
            if (!string.Equals(attr.Name.Local, "shadowrootmode", StringComparison.Ordinal))
            {
                continue;
            }

            mode = attr.Value switch
            {
                "open" => ShadowRootMode.Open,
                "closed" => ShadowRootMode.Closed,
                _ => null,
            };
            break;
        }

        if (mode is not { } resolved)
        {
            return false;
        }

        if ((tree.GetNode(template)?.Data as ElementData)?.TemplateContents is not { } root)
        {
            return false;
        }

        if (tree.AttachShadowRootNode(location, root, resolved) != AttachShadowError.None)
        {
            return false;
        }

        // The temporary template was never inserted on the successful path, but node creation
        // registered any `id` before attachment. Use the DOM removal path so that stale template
        // ids cannot escape through document.getElementById; template contents are a separate
        // fragment and remain alive as the native root.
        tree.RemoveChild(template);
        return true;
    }

    /// <summary>
    /// DOM's valid-shadow-host-name predicate. Gecko's
    /// <c>nsContentUtils::IsValidShadowHostName</c> uses this same HTML allowlist plus valid
    /// custom-element names; keeping the check at the parser boundary makes an invalid declarative
    /// template fall back to an ordinary inert template.
    /// </summary>
    internal static bool IsValidShadowHost(DomTree tree, NodeId id)
    {
        if (tree.GetNode(id)?.ElementName is not { } name)
        {
            return false;
        }

        if (!string.Equals(name.Ns, Namespaces.Html, StringComparison.Ordinal))
        {
            return false;
        }

        var local = name.Local;
        switch (local)
        {
            case "article" or "aside" or "blockquote" or "body" or "div" or "footer" or "h1"
                or "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "main" or "nav" or "p"
                or "section" or "span":
                return true;
        }

        if (local.Length == 0
            || !char.IsAsciiLetterLower(local[0])
            || !local.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in local)
        {
            if (char.IsAsciiLetterUpper(ch)
                || ch == '\0'
                || ch is '\t' or '\n' or '\f' or '\r' or ' '
                || ch is '/' or '>')
            {
                return false;
            }
        }

        return local switch
        {
            "annotation-xml" or "color-profile" or "font-face" or "font-face-src"
                or "font-face-uri" or "font-face-format" or "font-face-name"
                or "missing-glyph" => false,
            _ => true,
        };
    }
}
