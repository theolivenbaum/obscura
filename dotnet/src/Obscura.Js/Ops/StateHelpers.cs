using Obscura.Dom;
using Obscura.Js.Url;

namespace Obscura.Js.Ops;

/// <summary>
/// The free functions <c>ops.rs</c> shares between ops: script "already started"
/// bookkeeping, the memoized document base URL, tree-order comparison, and the
/// bounded push used for the append-only lists.
/// </summary>
public static class StateHelpers
{
    /// <summary>
    /// Cap on the append-only <c>fetched_urls</c> asset list. A page can otherwise
    /// loop fetch()/XHR and grow it without bound on the process heap, where V8's
    /// heap-limit guard never sees it.
    /// </summary>
    internal const int MaxFetchedUrls = 16384;

    /// <summary>
    /// Pushes <paramref name="item"/> onto <paramref name="list"/>, evicting the
    /// oldest entries so it never holds more than <paramref name="max"/>.
    /// </summary>
    internal static void PushCapped(List<string> list, string item, int max)
    {
        list.Add(item);
        if (list.Count > max)
        {
            list.RemoveRange(0, list.Count - max);
        }
    }

    public static bool NodeIsScript(DomTree dom, NodeId nodeId)
    {
        var element = dom.GetNode(nodeId)?.AsElement();
        return element is not null && element.Name.Local.Equals("script", StringComparison.OrdinalIgnoreCase);
    }

    private static List<NodeId> ScriptNodesIncludingTemplateContents(DomTree dom, NodeId root)
    {
        List<NodeId> scripts = [];
        List<NodeId> stack = [root];
        while (stack.Count > 0)
        {
            var nodeId = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (NodeIsScript(dom, nodeId))
            {
                scripts.Add(nodeId);
            }

            var templateContents = (dom.GetNode(nodeId)?.Data as ElementData)?.TemplateContents;
            if (templateContents is { } contents)
            {
                stack.Add(contents);
            }

            var children = dom.Children(nodeId);
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Add(children[i]);
            }
        }

        return scripts;
    }

    /// <summary>Marks every script in a subtree as already started, so it stays inert.</summary>
    public static void MarkScriptSubtreeStarted(ObscuraState state, NodeId root)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Dom is not { } dom)
        {
            return;
        }

        foreach (var script in ScriptNodesIncludingTemplateContents(dom, root))
        {
            state.AlreadyStartedScripts.Add(script);
        }
    }

    /// <summary>Carries the already-started flag from a source subtree onto its clone.</summary>
    internal static void PropagateScriptStartState(
        DomTree dom,
        NodeId sourceRoot,
        NodeId clonedRoot,
        HashSet<NodeId> started)
    {
        List<(NodeId Source, NodeId Cloned)> pairs = [(sourceRoot, clonedRoot)];
        List<NodeId> additions = [];
        while (pairs.Count > 0)
        {
            var (source, cloned) = pairs[^1];
            pairs.RemoveAt(pairs.Count - 1);
            if (started.Contains(source))
            {
                additions.Add(cloned);
            }

            var sourceTemplate = (dom.GetNode(source)?.Data as ElementData)?.TemplateContents;
            var clonedTemplate = (dom.GetNode(cloned)?.Data as ElementData)?.TemplateContents;
            if (sourceTemplate is { } sourceContents && clonedTemplate is { } clonedContents)
            {
                pairs.Add((sourceContents, clonedContents));
            }

            var sourceChildren = dom.Children(source);
            var clonedChildren = dom.Children(cloned);
            var count = Math.Min(sourceChildren.Count, clonedChildren.Count);
            for (var i = count - 1; i >= 0; i--)
            {
                pairs.Add((sourceChildren[i], clonedChildren[i]));
            }
        }

        foreach (var node in additions)
        {
            started.Add(node);
        }
    }

    /// <summary>Index of <paramref name="n"/> among its parent's children (0-based).</summary>
    internal static int NodeChildIndex(DomTree dom, NodeId n)
    {
        var i = 0;
        var current = dom.GetNode(n)?.PrevSibling;
        while (current is { } p)
        {
            i++;
            current = dom.GetNode(p)?.PrevSibling;
        }

        return i;
    }

    /// <summary>Ancestor chain of <paramref name="n"/> from the root down to it.</summary>
    private static List<NodeId> NodeAncestorsRootFirst(DomTree dom, NodeId n)
    {
        List<NodeId> v = [n];
        var current = n;
        while (dom.GetNode(current)?.Parent is { } p)
        {
            v.Add(p);
            current = p;
        }

        v.Reverse();
        return v;
    }

    /// <summary>Preorder (document) order of two nodes: -1 before, 1 after, 0 same.</summary>
    internal static int CompareNodeOrder(DomTree dom, NodeId a, NodeId b)
    {
        if (a == b)
        {
            return 0;
        }

        var aa = NodeAncestorsRootFirst(dom, a);
        var bb = NodeAncestorsRootFirst(dom, b);

        // Different roots: order is undefined per spec; keep it stable by node id.
        if (aa[0] != bb[0])
        {
            return a.Index < b.Index ? -1 : 1;
        }

        var i = 0;
        while (i < aa.Count && i < bb.Count && aa[i] == bb[i])
        {
            i++;
        }

        if (i >= aa.Count)
        {
            return -1; // a is an ancestor of b -> a precedes
        }

        if (i >= bb.Count)
        {
            return 1; // b is an ancestor of a -> a follows
        }

        return NodeChildIndex(dom, aa[i]) < NodeChildIndex(dom, bb[i]) ? -1 : 1;
    }

    internal static bool NodeIsConnected(DomTree dom, NodeId node) => dom.IsConnected(node);

    /// <summary>Every node reachable from the document through light and shadow trees.</summary>
    internal static HashSet<NodeId> ShadowIncludingConnectedNodes(DomTree dom)
    {
        HashSet<NodeId> connected = [];
        List<NodeId> stack = [dom.Document];
        while (stack.Count > 0)
        {
            var node = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (!connected.Add(node))
            {
                continue;
            }

            stack.AddRange(dom.Children(node));
            if (dom.ShadowChildren(node) is { } shadowChildren)
            {
                stack.AddRange(shadowChildren);
            }
        }

        return connected;
    }

    /// <summary>
    /// The base for relative URLs. Not tied to the render feature: the JS layer
    /// resolves every relative URL through here, in all build variants.
    /// </summary>
    public static string? DocumentBaseUrl(ObscuraState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var documentUrl = UrlRecord.Parse(state.Url);
        if (documentUrl is null)
        {
            return null;
        }

        var baseHref = BaseHrefAttribute(state);
        if (baseHref is null)
        {
            return documentUrl.Href;
        }

        // https://html.spec.whatwg.org/multipage/semantics.html#set-the-frozen-base-url
        // A data: or javascript: base falls back to the document URL. Accepting it
        // would instead make every later relative resolution fail.
        var joined = documentUrl.Join(baseHref);
        if (joined is not null
            && !string.Equals(joined.Scheme, "data", StringComparison.Ordinal)
            && !string.Equals(joined.Scheme, "javascript", StringComparison.Ordinal))
        {
            return joined.Href;
        }

        return documentUrl.Href;
    }

    /// <summary>
    /// The raw <c>href</c> attribute of the first <c>&lt;base href&gt;</c>, unresolved.
    /// The JS layer needs it after <c>history.pushState</c>: the document URL has
    /// moved and only JS knows the new one.
    /// </summary>
    private static string? BaseHrefAttribute(ObscuraState state)
    {
        if (state.Dom is not { } dom)
        {
            return null;
        }

        if (!dom.TryQuerySelector("base[href]", out var id, out _) || id is not { } found)
        {
            return null;
        }

        return dom.GetNode(found)?.GetAttribute("href");
    }

    /// <summary>
    /// Both base values behind a cache. Uncached, each one walks the tree and runs
    /// the selector engine, which would make <c>a.href</c> an O(nodes) read.
    /// </summary>
    private static (string? Resolved, string? RawHref) BaseValuesMemoized(ObscuraState state)
    {
        if (state.BaseUrlCache is { } cached
            && cached.ActivityGeneration == state.ActivityGeneration
            && cached.DocumentGeneration == state.DocumentGeneration
            && string.Equals(cached.Url, state.Url, StringComparison.Ordinal))
        {
            return (cached.Resolved, cached.RawHref);
        }

        var resolved = DocumentBaseUrl(state);
        var rawHref = BaseHrefAttribute(state);
        state.BaseUrlCache = new BaseUrlCache
        {
            ActivityGeneration = state.ActivityGeneration,
            DocumentGeneration = state.DocumentGeneration,
            Url = state.Url,
            Resolved = resolved,
            RawHref = rawHref,
        };
        return (resolved, rawHref);
    }

    public static string? DocumentBaseUrlMemoized(ObscuraState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return BaseValuesMemoized(state).Resolved;
    }

    public static string? DocumentBaseHrefMemoized(ObscuraState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return BaseValuesMemoized(state).RawHref;
    }

    /// <summary>
    /// Splits the packed <c>set_inner_html_context</c> / <c>set_fragment_html_executable</c>
    /// argument into a context element name and the HTML source.
    /// </summary>
    /// <remarks>
    /// The current bootstrap encodes <c>namespace\0qualifiedName\0html</c>. Older
    /// snapshots encoded <c>local\0html</c>, and the oldest just <c>html</c>; both
    /// remain accepted.
    /// </remarks>
    internal static (QualName Context, string Html) FragmentContextAndHtml(string arg)
    {
        var parts = arg.Split('\0', 3);
        var first = parts.Length > 0 ? parts[0] : "body";
        string ns;
        string qualified;
        string html;
        if (parts.Length >= 3)
        {
            ns = first;
            qualified = parts[1];
            html = parts[2];
        }
        else if (parts.Length == 2)
        {
            ns = Namespaces.Html;
            qualified = first;
            html = parts[1];
        }
        else
        {
            ns = Namespaces.Html;
            qualified = "body";
            html = first;
        }

        string? prefix = null;
        string local;
        var colon = qualified.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon < qualified.Length - 1)
        {
            prefix = qualified[..colon];
            local = qualified[(colon + 1)..];
        }
        else
        {
            local = qualified.Length == 0 ? "body" : qualified;
        }

        return (new QualName(prefix, ns, local), html);
    }
}
