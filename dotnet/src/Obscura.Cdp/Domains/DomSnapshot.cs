// DOMSnapshot.captureSnapshot for layout-free engines.
//
// browser-use (and other CDP DOM-agent frameworks) build their interactive element index from
// DOMSnapshot.captureSnapshot: the per-node bounds, computed styles, and isClickable flag,
// correlated with DOM.getDocument by backendNodeId. Without this domain their DOM build aborts and
// the agent sees zero elements.
//
// Obscura has no layout/paint engine behind this domain, so there is no real geometry to report. We
// synthesize it: every node gets a distinct, on-screen, non-icon-sized box (a simple vertical
// stack) plus plausible computed styles (visible, opaque, pointer cursor on interactive tags). That
// is enough for the element detection path, which keys off tag name / ARIA / accessibility role and
// does not need true geometry. Clicking still falls back to JS .click() since the coordinates are
// synthetic. backendNodeId == nid, matching DOM.getDocument.
using System.Text.Json.Nodes;
using Obscura.Dom;

namespace Obscura.Cdp.Domains;

/// <summary>CDP <c>DOMSnapshot</c> domain.</summary>
public static class DomSnapshot
{
    /// <summary>
    /// Computed-style names browser-use requests, in the exact order it expects to read them back
    /// out of each layout node's <c>styles</c> index array.
    /// </summary>
    public static readonly string[] RequiredStyles =
    [
        "display",
        "visibility",
        "opacity",
        "overflow",
        "overflow-x",
        "overflow-y",
        "cursor",
        "pointer-events",
        "position",
        "background-color",
    ];

    /// <summary>
    /// Cap the synthesized snapshot so a pathologically large DOM cannot produce a runaway payload.
    /// Matches the spirit of the descendants() length cap.
    /// </summary>
    private const int MaxNodes = 20_000;

    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ = parameters;
        await Task.CompletedTask.ConfigureAwait(false);
        switch (method)
        {
            case "enable":
            case "disable":
                return DomainResult.Empty();

            case "captureSnapshot":
            {
                if (ctx.GetSessionPage(sessionId) is not { } page)
                {
                    return DomainResult.Err("No page");
                }

                string url = page.UrlString();
                string title = page.Title;
                JsonObject? snapshot = page.WithDom(dom => BuildCaptureSnapshot(dom, url, title));
                return snapshot is null
                    ? DomainResult.Err("No DOM loaded")
                    : DomainResult.Ok(snapshot);
            }

            // Permissive no-op for the rest of the domain (for example getSnapshot) so a client
            // that probes it does not abort on an Unknown-method error.
            default:
                return DomainResult.Empty();
        }
    }

    /// <summary>
    /// String table with de-duplication. Every string in a DOMSnapshot response is referenced by
    /// its index into the top-level <c>strings</c> array.
    /// </summary>
    private sealed class Interner
    {
        private readonly Dictionary<string, long> _map = new(StringComparer.Ordinal);

        internal Interner() => Intern(string.Empty);

        internal List<string> List { get; } = [];

        internal long Intern(string value)
        {
            if (_map.TryGetValue(value, out long existing))
            {
                return existing;
            }

            long index = List.Count;
            List.Add(value);
            _map[value] = index;
            return index;
        }
    }

    /// <summary>
    /// Pre-order DFS from the document, recording each node and its parent's index.
    /// </summary>
    /// <remarks>
    /// Iterative with an explicit stack: the node cap bounds the node count, but recursion depth is
    /// a separate axis. A deeply nested linear chain (script can build thousands of nested elements)
    /// would recurse that many frames deep and could overflow the stack before the count guard
    /// triggers. An explicit stack keeps the depth on the heap (issue #341).
    /// </remarks>
    private static void Walk(
        DomTree dom,
        NodeId root,
        long rootParent,
        List<NodeId> order,
        List<long> parentIndex)
    {
        // (node, parent index). Children are pushed in reverse so they pop in document order,
        // preserving the original pre-order traversal.
        var stack = new Stack<(NodeId Node, long Parent)>();
        stack.Push((root, rootParent));
        while (stack.Count > 0)
        {
            (NodeId id, long parent) = stack.Pop();
            if (order.Count >= MaxNodes)
            {
                break;
            }

            long my = order.Count;
            order.Add(id);
            parentIndex.Add(parent);
            List<NodeId> children = dom.Children(id);
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], my));
            }
        }
    }

    private static JsonObject BuildCaptureSnapshot(DomTree dom, string url, string title)
    {
        ArgumentNullException.ThrowIfNull(dom);
        var order = new List<NodeId>();
        var parentIndex = new List<long>();
        Walk(dom, dom.Document, -1, order, parentIndex);

        var strings = new Interner();
        long docUrlIndex = strings.Intern(url);
        long titleIndex = strings.Intern(title);

        int n = order.Count;
        var nodeType = new JsonArray();
        var nodeName = new JsonArray();
        var nodeValue = new JsonArray();
        var backendIds = new JsonArray();
        var attributes = new JsonArray();
        var clickable = new JsonArray();

        // Layout arrays are 1:1 with nodes (nodeIndex[i] == i).
        var layoutNodeIndex = new JsonArray();
        var bounds = new JsonArray();
        var styles = new JsonArray();
        var paintOrders = new JsonArray();
        var clientRects = new JsonArray();
        var layoutText = new JsonArray();

        for (int i = 0; i < n; i++)
        {
            NodeId nid = order[i];
            Node? node = dom.GetNode(nid);
            if (node is null)
            {
                // Keep the arrays aligned even for a vanished node.
                nodeType.Add(0);
                nodeName.Add(0);
                nodeValue.Add(0);
                backendIds.Add((long)nid.Index);
                attributes.Add(new JsonArray());
                layoutNodeIndex.Add((long)i);
                bounds.Add(new JsonArray(0.0, 0.0, 0.0, 0.0));
                styles.Add(new JsonArray());
                paintOrders.Add((long)i);
                clientRects.Add(new JsonArray(0.0, 0.0, 0.0, 0.0));
                layoutText.Add(-1);
                continue;
            }

            long ntype;
            string nname;
            string nval;
            List<(string Key, string Value)> attrs = [];
            string tag = string.Empty;
            switch (node.Data)
            {
                case DocumentData:
                    (ntype, nname, nval) = (9, "#document", string.Empty);
                    break;
                case DoctypeData doctype:
                    (ntype, nname, nval) = (10, doctype.Name, string.Empty);
                    break;
                case ElementData element:
                {
                    tag = element.Name.Local;
                    foreach (Obscura.Dom.Attribute attr in element.Attrs)
                    {
                        attrs.Add((attr.Name.Local, attr.Value));
                    }

                    ntype = 1;
                    nname = tag.ToUpperInvariant();
                    nval = string.Empty;
                    tag = tag.ToLowerInvariant();
                    break;
                }

                case TextData text:
                    (ntype, nname, nval) = (3, "#text", text.Contents);
                    break;
                case CommentData comment:
                    (ntype, nname, nval) = (8, "#comment", comment.Contents);
                    break;
                case ProcessingInstructionData pi:
                    (ntype, nname, nval) = (7, pi.Target, pi.Data);
                    break;
                default:
                    (ntype, nname, nval) = (0, string.Empty, string.Empty);
                    break;
            }

            nodeType.Add(ntype);
            nodeName.Add(strings.Intern(nname));
            nodeValue.Add(strings.Intern(nval));
            backendIds.Add((long)nid.Index);

            var attrIndex = new JsonArray();
            foreach ((string key, string value) in attrs)
            {
                attrIndex.Add(strings.Intern(key));
                attrIndex.Add(strings.Intern(value));
            }

            attributes.Add(attrIndex);

            bool interactive = tag is "a" or "button" or "input" or "select" or "textarea"
                or "summary" or "details" or "option" or "label";
            bool hasOnClick = attrs.Exists(
                pair => string.Equals(pair.Key, "onclick", StringComparison.OrdinalIgnoreCase));
            if (interactive || hasOnClick)
            {
                clickable.Add((long)i);
            }

            // Tags that never render box content; report display:none so the agent does not treat
            // them as visible.
            bool hidden = tag is "head" or "meta" or "title" or "script" or "style" or "link"
                or "noscript" or "base";
            string display = ntype == 1 && hidden ? "none" : "block";
            string cursor = interactive ? "pointer" : "auto";
            string[] styleValues =
            [
                display,
                "visible",
                "1",
                "visible",
                "visible",
                "visible",
                cursor,
                "auto",
                "static",
                "rgba(0, 0, 0, 0)",
            ];
            System.Diagnostics.Debug.Assert(
                styleValues.Length == RequiredStyles.Length,
                "each layout node carries all required computed styles");
            var styleIndex = new JsonArray();
            foreach (string styleValue in styleValues)
            {
                styleIndex.Add(strings.Intern(styleValue));
            }

            styles.Add(styleIndex);

            // Synthetic geometry: a vertical stack, full-width, 18px tall. Distinct and
            // non-icon-sized so visibility/size heuristics include the element; the coordinates are
            // not real (no layout engine behind this domain).
            double y = i * 18.0;
            bounds.Add(new JsonArray(0.0, y, 1280.0, 18.0));
            clientRects.Add(new JsonArray(0.0, y, 1280.0, 18.0));
            paintOrders.Add((long)i);
            layoutNodeIndex.Add((long)i);
            layoutText.Add(-1);
        }

        long contentHeight = (long)n * 18;
        var parentIndexArray = new JsonArray();
        foreach (long parent in parentIndex)
        {
            parentIndexArray.Add(parent);
        }

        var stringTable = new JsonArray();
        foreach (string entry in strings.List)
        {
            stringTable.Add(entry);
        }

        var document = new JsonObject
        {
            ["documentURL"] = docUrlIndex,
            ["title"] = titleIndex,
            ["baseURL"] = docUrlIndex,
            ["contentLanguage"] = 0,
            ["encodingName"] = 0,
            ["publicId"] = 0,
            ["systemId"] = 0,
            ["frameId"] = 0,
            ["nodes"] = new JsonObject
            {
                ["parentIndex"] = parentIndexArray,
                ["nodeType"] = nodeType,
                ["nodeName"] = nodeName,
                ["nodeValue"] = nodeValue,
                ["backendNodeId"] = backendIds,
                ["attributes"] = attributes,
                ["isClickable"] = new JsonObject { ["index"] = clickable },
            },
            ["layout"] = new JsonObject
            {
                ["nodeIndex"] = layoutNodeIndex,
                ["styles"] = styles,
                ["bounds"] = bounds,
                ["text"] = layoutText,
                ["paintOrders"] = paintOrders,
                ["clientRects"] = clientRects,
            },
            ["textBoxes"] = new JsonObject
            {
                ["layoutIndex"] = new JsonArray(),
                ["bounds"] = new JsonArray(),
                ["start"] = new JsonArray(),
                ["length"] = new JsonArray(),
            },
            ["scrollOffsetX"] = 0.0,
            ["scrollOffsetY"] = 0.0,
            ["contentWidth"] = 1280,
            ["contentHeight"] = contentHeight,
        };

        return new JsonObject
        {
            ["documents"] = new JsonArray(document),
            ["strings"] = stringTable,
        };
    }
}
