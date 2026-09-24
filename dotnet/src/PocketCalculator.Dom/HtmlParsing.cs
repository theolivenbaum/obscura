namespace PocketCalculator.Dom;

/// <summary>
/// HTML parsing into the arena tree.
///
/// AngleSharp supplies the spec HTML5 tokenizer, standing in for html5ever's; the tree
/// construction stage is <see cref="HtmlTreeBuilder"/>, which builds straight into the arena.
/// AngleSharp types never escape the parser.
/// </summary>
public static class HtmlParsing
{
    public static DomTree ParseHtml(string html) => ParseHtml(html, DomTree.DefaultContentByteBudget);

    /// <summary>Parse a document into a tree with the given byte budget (M7).</summary>
    public static DomTree ParseHtml(string html, long contentByteBudget)
    {
        var tree = new DomTree { ContentByteBudget = contentByteBudget };
        tree.SetAllowDeclarativeShadowRoots(true);
        HtmlTreeBuilder.ParseDocument(tree, html);
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
    /// fragment parsing context, and so does this, including an SVG or MathML context, whose
    /// children are parsed as foreign content.
    /// </summary>
    public static DomTree ParseFragmentWithContext(string html, QualName contextName)
    {
        var tree = new DomTree();
        // Fragment parsing deliberately leaves declarative shadow roots disabled.
        // The synthetic <html> root mirrors the wrapper html5ever's parse_fragment produces, which
        // is what FragmentRoot() and the innerHTML op walk. It is the fragment parsing
        // algorithm's own root element, so the parsed nodes are built under it directly.
        var root = tree.NewNode(NodeData.Element(QualName.Html("html")));
        tree.AppendChild(tree.Document, root);
        HtmlTreeBuilder.ParseFragment(tree, root, html, contextName);
        return tree;
    }

    /// <summary>
    /// Chromium's <c>kMaximumHTMLParserDOMTreeDepth</c>: once the stack of open elements is deeper
    /// than this, <c>HTMLConstructionSite::AttachLater</c> attaches a new element or comment to the
    /// parent of the current node instead of the current node, so parsed content never nests
    /// deeper than 513 elements however the markup is written.
    /// </summary>
    /// <remarks>
    /// DEVIATION from crates/obscura-dom/src/tree_sink.rs, which nests parsed content as deep as
    /// the markup says. Three thousand unclosed <c>&lt;div&gt;</c>s then overflowed the stack in
    /// layout and killed the process (SECURITY.md C5). <see cref="HtmlTreeBuilder"/> applies the
    /// rule as Chromium does; text is not moved, as in Chromium.
    /// </remarks>
    internal const int MaxParserTreeDepth = 512;

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
