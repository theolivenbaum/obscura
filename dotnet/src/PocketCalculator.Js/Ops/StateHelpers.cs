using PocketCalculator.Dom;
using PocketCalculator.Js.Url;
using PocketCalculator.Net;

namespace PocketCalculator.Js.Ops;

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

    /// <summary>
    /// The serialized origin of the document a realm holds, as the host knows it:
    /// <c>"null"</c> for an opaque origin (a data:, about:blank or file: document, or a
    /// sandboxed frame).
    /// </summary>
    /// <remarks>
    /// Port addition. The Rust shim computes every origin it hands an op with the page's
    /// own <c>URL</c> global, which page script can replace; the host decides instead,
    /// from the URL it committed (history.pushState does not move it).
    /// </remarks>
    public static string DocumentOrigin(PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.OpaqueOrigin ? "null" : UrlRecord.Parse(state.Url)?.AsciiOrigin ?? "null";
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
    public static void MarkScriptSubtreeStarted(PocketCalculatorState state, NodeId root)
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

    /// <summary>
    /// Whether <paramref name="ancestor"/> is a strict ancestor of <paramref name="node"/> in the
    /// light tree, by walking the parents of <paramref name="node"/>: O(depth), where collecting
    /// the descendants of <paramref name="ancestor"/> was O(size of its subtree) on every call.
    /// A shadow root's parent is null, so, as before, the walk does not cross into a host.
    /// </summary>
    internal static bool IsStrictAncestor(DomTree dom, NodeId ancestor, NodeId node)
    {
        if (dom.GetNode(ancestor) is null)
        {
            return false;
        }

        var bound = dom.NodeSlotCount;
        var current = dom.GetNode(node)?.Parent;
        for (var i = 0; current is { } parent && i <= bound; i++)
        {
            if (parent == ancestor)
            {
                return true;
            }

            current = dom.GetNode(parent)?.Parent;
        }

        return false;
    }

    /// <summary>
    /// <c>Range.toString()</c>: the start Text node's data from the start offset, the data of
    /// every Text node contained in the range in tree order, and the end Text node's data up to
    /// the end offset (DOM "Range stringifier").
    /// </summary>
    /// <remarks>
    /// The Text nodes contained in a range are exactly the ones that lie, in tree order, after
    /// the start boundary and before the end boundary, so this is one forward walk between the
    /// two. bootstrap used to walk the whole common-ancestor subtree and compare both boundary
    /// points against every Text node in it, two O(depth) ops per node. Offsets are clamped the
    /// way <c>String.prototype.slice</c> clamps them, because a Range here does not follow
    /// mutations and can hold an offset past the end of a node that has since shrunk.
    /// </remarks>
    internal static string RangeText(DomTree dom, NodeId sc, int so, NodeId ec, int eo)
    {
        var startNode = dom.GetNode(sc);
        var endNode = dom.GetNode(ec);
        if (startNode is null || endNode is null)
        {
            return string.Empty;
        }

        if (sc == ec && startNode.Data is TextData same)
        {
            return Slice(same.Contents, so, eo);
        }

        var sb = new System.Text.StringBuilder();
        if (startNode.Data is TextData startText)
        {
            sb.Append(Slice(startText.Contents, so, int.MaxValue));
        }

        var root = sc;
        var bound = dom.NodeSlotCount;
        for (var i = 0; dom.GetNode(root)?.Parent is { } parent && i <= bound; i++)
        {
            root = parent;
        }

        // First node after the start boundary, and the node the end boundary sits before
        // (null: the end of the tree).
        var first = startNode.IsElement || startNode.IsDocument
            ? ChildAt(dom, sc, so) ?? dom.NextAfterSubtree(root, sc)
            : dom.NextAfterSubtree(root, sc);
        NodeId? stop = endNode.IsElement || endNode.IsDocument
            ? ChildAt(dom, ec, eo) ?? dom.NextAfterSubtree(root, ec)
            : ec;

        // A Range here does not follow mutations, so a boundary can be left past the other one
        // by a later move; the old whole-subtree filter then found nothing contained, and nor
        // does this.
        var endRoot = ec;
        for (var i = 0; dom.GetNode(endRoot)?.Parent is { } parent && i <= bound; i++)
        {
            endRoot = parent;
        }

        if (endRoot != root || (first is { } f && stop is { } s && CompareNodeOrder(dom, f, s) > 0))
        {
            first = null;
        }

        var steps = 0;
        for (var node = first; node is { } id && id != stop; node = dom.NextInSubtree(root, id))
        {
            if ((++steps & 1023) == 0)
            {
                WorkCancellation.ThrowIfCancellationRequested();
            }

            if (steps > bound)
            {
                break;
            }

            if (id != sc && id != ec && dom.GetNode(id)?.Data is TextData text)
            {
                sb.Append(text.Contents);
            }
        }

        if (endNode.Data is TextData endText)
        {
            sb.Append(Slice(endText.Contents, 0, eo));
        }

        return sb.ToString();

        static string Slice(string s, int from, int to)
        {
            from = Math.Clamp(from, 0, s.Length);
            to = Math.Clamp(to, 0, s.Length);
            return to > from ? s[from..to] : string.Empty;
        }
    }

    private static NodeId? ChildAt(DomTree dom, NodeId parent, int index)
    {
        if (index < 0)
        {
            return null;
        }

        var child = dom.GetNode(parent)?.FirstChild;
        for (var i = 0; child is { } id && i < index; i++)
        {
            child = dom.GetNode(id)?.NextSibling;
        }

        return child;
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
    public static string? DocumentBaseUrl(PocketCalculatorState state)
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
    private static string? BaseHrefAttribute(PocketCalculatorState state)
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
    private static (string? Resolved, string? RawHref) BaseValuesMemoized(PocketCalculatorState state)
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

    /// <summary>
    /// The cookie context of <paramref name="state"/>'s own document at
    /// <paramref name="url"/> (<c>document.cookie</c>): same-site unless the document is a
    /// frame with a cross-site ancestor, partitioned by the page's site. Port addition
    /// (CHIPS; Rust reads and writes every document's cookies as a top-level document's).
    /// </summary>
    public static CookieAccess DocumentCookieAccess(PocketCalculatorState state, Uri url)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(url);
        var topLevel = TopLevelUri(state) ?? url;
        return new CookieAccess(
            state.CrossSiteAncestor ? SameSiteContext.CrossSite : SameSiteContext.SameSite,
            CookiePartitionKey.For(topLevel, state.CrossSiteAncestor, url));
    }

    /// <summary>
    /// The cookie context of a scripted request (fetch, XHR, an internal load) by
    /// <paramref name="state"/>'s document, whose origin is <paramref name="origin"/>, to
    /// <paramref name="target"/>: same-site only when the document, its ancestors and the
    /// target are all same-site with the page. Port addition (the site for cookies and
    /// CHIPS; Rust judges the document's origin alone).
    /// </summary>
    public static CookieAccess RequestCookieAccess(PocketCalculatorState state, string origin, Uri target)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);
        var sameSite = CookieJar.ContextForInitiator(origin, target);
        var topLevel = TopLevelUri(state);
        if (topLevel is not null && (state.CrossSiteAncestor || !CookieJar.IsSameSite(topLevel, target)))
        {
            sameSite = SameSiteContext.CrossSite;
        }

        topLevel ??= Uri.TryCreate(state.Url, UriKind.Absolute, out var own) ? own : null;
        return new CookieAccess(
            sameSite,
            topLevel is null ? null : CookiePartitionKey.For(topLevel, state.CrossSiteAncestor, target));
    }

    /// <summary>
    /// Give a request <paramref name="state"/>'s document starts the frame scope its cookies
    /// are judged in (<see cref="ResourceRequest.TopLevel"/>,
    /// <see cref="ResourceRequest.CrossSiteAncestor"/>). A no-op for the page's own document.
    /// </summary>
    public static ResourceRequest WithFrameScope(ResourceRequest request, PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);
        request.TopLevel = TopLevelUri(state);
        request.CrossSiteAncestor = state.CrossSiteAncestor;
        return request;
    }

    /// <summary>The page's URL for a frame's document; null for the page's own.</summary>
    public static Uri? TopLevelUri(PocketCalculatorState state) =>
        state.TopLevelUrl is { } top && Uri.TryCreate(top, UriKind.Absolute, out var parsed) ? parsed : null;

    /// <summary>
    /// The document's referrer policy: the last valid <c>&lt;meta name=referrer&gt;</c> in
    /// tree order, else the <c>Referrer-Policy</c> header, else the default. Memoized on the
    /// document's generations.
    /// </summary>
    /// <remarks>
    /// Port addition (Rust has no referrer policy). Chromium applies a meta from the moment
    /// the parser inserts it, so an image before a late meta still gets the earlier policy;
    /// here the whole document is parsed before any subresource loads, so a meta anywhere
    /// applies to every load.
    /// </remarks>
    public static ReferrerPolicy DocumentReferrerPolicy(PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ReferrerPolicyCache is { } cached
            && cached.Activity == state.ActivityGeneration
            && cached.Document == state.DocumentGeneration
            && cached.Header == state.ReferrerPolicyHeader)
        {
            return cached.Policy;
        }

        var policy = state.ReferrerPolicyHeader ?? ReferrerPolicies.Default;
        if (state.Dom is { } dom && dom.TryQuerySelectorAll("meta[name]", out var metas, out _))
        {
            foreach (var id in metas)
            {
                if (dom.GetNode(id) is { } node
                    && string.Equals(node.GetAttribute("name"), "referrer", StringComparison.OrdinalIgnoreCase)
                    && ReferrerPolicies.ParseMeta(node.GetAttribute("content")) is { } meta)
                {
                    policy = meta;
                }
            }
        }

        state.ReferrerPolicyCache = (state.ActivityGeneration, state.DocumentGeneration, state.ReferrerPolicyHeader, policy);
        return policy;
    }

    /// <summary>
    /// An element's own referrer policy: <c>rel=noreferrer</c> (on links), else a valid
    /// <c>referrerpolicy</c> attribute, else null for the document's.
    /// </summary>
    public static ReferrerPolicy? ElementReferrerPolicy(DomTree dom, NodeId id, bool honourNoreferrer = false)
    {
        ArgumentNullException.ThrowIfNull(dom);
        if (dom.GetNode(id) is not { } node)
        {
            return null;
        }

        if (honourNoreferrer && node.GetAttribute("rel") is { } rel)
        {
            foreach (var token in rel.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Equals("noreferrer", StringComparison.OrdinalIgnoreCase))
                {
                    return ReferrerPolicy.NoReferrer;
                }
            }
        }

        return ReferrerPolicies.ParseAttribute(node.GetAttribute("referrerpolicy"));
    }

    public static string? DocumentBaseUrlMemoized(PocketCalculatorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return BaseValuesMemoized(state).Resolved;
    }

    public static string? DocumentBaseHrefMemoized(PocketCalculatorState state)
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
