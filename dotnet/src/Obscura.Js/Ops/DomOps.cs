using System.Globalization;
using System.Text;
using Obscura.Dom;
using Obscura.Js.Modules;
using Obscura.Render;

namespace Obscura.Js.Ops;

/// <summary>
/// <c>op_dom</c>: the whole DOM surface multiplexed over one op.
/// </summary>
/// <remarks>
/// <para>
/// <c>op_dom(cmd, arg1, arg2, frameId) -&gt; string</c>. Arguments and results are
/// strings; structured results are JSON. <b>Failure is a string too.</b> An unknown
/// command, a missing node, or a failed operation returns <c>""</c>, <c>"null"</c>,
/// <c>"-1"</c> or <c>"false"</c> depending on the command, and the shim branches on
/// exactly those values. Every choice here is copied from <c>op_dom_inner</c>.
/// </para>
/// <para>
/// Selector commands go through the <c>Try*</c> query variants on purpose: a
/// selector parse error must yield an empty NodeList, not an exception. Verified
/// against the Rust binary - <c>document.querySelectorAll('::before').length</c> is 0.
/// </para>
/// </remarks>
public static class DomOps
{
    private static readonly char[] TitleWhitespace = ['\t', '\n', '\f', '\r', ' '];

    /// <summary>
    /// The op entry point. The Rust op wraps the body in <c>catch_unwind</c> because
    /// a panic would unwind into V8's FFI frame, where <c>V8_Fatal</c> calls
    /// <c>abort(3)</c> and takes the whole engine down. <see cref="OpGuard"/> is the
    /// managed equivalent: one malformed selector or inconsistent tree node degrades
    /// to a null result for that single call.
    /// </summary>
    public static string OpDom(ObscuraState state, string cmd, string arg1, string arg2) =>
        OpGuard.Run("op_dom", () => Inner(state, cmd, arg1, arg2), "null");

    internal static string Inner(ObscuraState gs, string cmd, string arg1, string arg2)
    {
        ArgumentNullException.ThrowIfNull(gs);
        Prelude(gs, cmd, arg1, arg2);

        if (gs.Dom is not { } dom)
        {
            return "null";
        }

        switch (cmd)
        {
            case "document_node_id":
                return Index(dom.Document);

            case "document_title":
            {
                // The DOM is authoritative after parsing. In particular, script
                // changes through title.textContent must be reflected by
                // document.title, not hidden behind the navigation-time snapshot.
                var title = string.Empty;
                if (dom.TryQuerySelector("title", out var titleId, out _) && titleId is { } found)
                {
                    var parts = dom.TextContent(found)
                        .Split(TitleWhitespace, StringSplitOptions.RemoveEmptyEntries);
                    title = string.Join(" ", parts);
                }

                return SerdeJson.String(title);
            }

            case "document_url":
                return SerdeJson.String(gs.Url);

            // The base for relative URLs. It differs from document_url exactly when
            // the page carries a <base href>, and that is the point: HTML resolves
            // against the base, not the document.
            case "document_base_url":
                return SerdeJson.String(StateHelpers.DocumentBaseUrlMemoized(gs) ?? gs.Url);

            // The unresolved attribute. After history.pushState only JS knows the
            // URL, so only JS can resolve a relative base against it.
            case "document_base_href":
                return SerdeJson.String(StateHelpers.DocumentBaseHrefMemoized(gs) ?? string.Empty);

            case "document_referrer":
                return SerdeJson.String(gs.Referrer);

            case "document_encoding":
                return SerdeJson.String(gs.Encoding);

            case "document_element":
            {
                foreach (var cid in dom.Children(dom.Document))
                {
                    var element = dom.GetNode(cid)?.AsElement();
                    if (element is not null && string.Equals(element.Name.Local, "html", StringComparison.Ordinal))
                    {
                        return Index(cid);
                    }
                }

                return "-1";
            }

            case "document_doctype":
            {
                foreach (var cid in dom.Children(dom.Document))
                {
                    if (dom.GetNode(cid)?.Data is DoctypeData doctype)
                    {
                        var sb = new StringBuilder(96);
                        sb.Append("{\"name\":");
                        SerdeJson.AppendString(sb, doctype.Name);
                        sb.Append(",\"publicId\":");
                        SerdeJson.AppendString(sb, doctype.PublicId);
                        sb.Append(",\"systemId\":");
                        SerdeJson.AppendString(sb, doctype.SystemId);
                        sb.Append(",\"nodeId\":");
                        sb.Append(cid.Index.ToString(CultureInfo.InvariantCulture));
                        sb.Append('}');
                        return sb.ToString();
                    }
                }

                return "null";
            }

            case "get_element_by_id":
            {
                // Verify the indexed node is in the live document. The id index is
                // best-effort: it only registers nodes at creation time and does not
                // update on reparent, so it can point to a detached clone while the
                // live node is elsewhere in the tree.
                var doc = dom.Document;
                var nid = dom.GetElementById(arg1);
                if (nid is { } indexed && dom.Ancestors(indexed).Contains(doc))
                {
                    return Index(indexed);
                }

                // Fall back to full scan for the live document.
                var selector = "[id=\"" + arg1.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"]";
                return dom.TryQuerySelector(selector, out var scanned, out _) && scanned is { } hit
                    ? Index(hit)
                    : "-1";
            }

            case "query_selector":
                return dom.TryQuerySelector(arg1, out var queried, out _) && queried is { } one
                    ? Index(one)
                    : "-1";

            case "query_selector_all":
            {
                dom.TryQuerySelectorAll(arg1, out var all, out _);
                return SerdeJson.IntArray(Indices(all));
            }

            case "query_selector_scoped":
            {
                var rootNid = ParseNodeOrZero(arg1);
                return dom.TryQuerySelectorFrom(rootNid, arg2, out var scoped, out _) && scoped is { } scopedHit
                    ? Index(scopedHit)
                    : "-1";
            }

            case "query_selector_all_scoped":
            {
                var rootNid = ParseNodeOrZero(arg1);
                dom.TryQuerySelectorAllFrom(rootNid, arg2, out var scopedAll, out _);
                return SerdeJson.IntArray(Indices(scopedAll));
            }

            case "matches_selector":
            {
                var nid = ParseNodeOrZero(arg1);
                dom.TryMatchesSelector(nid, arg2, out var matches, out _);
                return Bool(matches);
            }

            case "node_type":
            {
                var node = dom.GetNode(ParseNodeOrZero(arg1));
                return node?.Data switch
                {
                    DocumentData => "9",
                    ElementData => "1",
                    TextData => "3",
                    CommentData => "8",
                    DoctypeData => "10",
                    ProcessingInstructionData => "7",
                    _ => "0",
                };
            }

            case "node_name":
            {
                var node = dom.GetNode(ParseNodeOrZero(arg1));
                var name = node?.Data switch
                {
                    DocumentData => "#document",
                    ElementData element => element.Name.Local.ToUpperInvariant(),
                    TextData => "#text",
                    CommentData => "#comment",
                    DoctypeData doctype => doctype.Name,
                    ProcessingInstructionData pi => pi.Target,
                    _ => string.Empty,
                };
                return SerdeJson.String(name);
            }

            case "text_content":
                return SerdeJson.String(dom.TextContent(ParseNodeOrZero(arg1)));

            case "parent_node":
            case "first_child":
            case "last_child":
            case "next_sibling":
            case "prev_sibling":
            {
                var node = dom.GetNode(ParseNodeOrZero(arg1));
                var target = node is null ? null : cmd switch
                {
                    "parent_node" => node.Parent,
                    "first_child" => node.FirstChild,
                    "last_child" => node.LastChild,
                    "next_sibling" => node.NextSibling,
                    "prev_sibling" => node.PrevSibling,
                    _ => null,
                };
                return target is { } id ? Index(id) : "-1";
            }

            case "next_in_subtree":
            {
                var found = dom.NextInSubtree(ParseNodeOrZero(arg1), ParseNodeOrZero(arg2));
                return found is { } id ? Index(id) : "-1";
            }

            // Reverse document order within a subtree, for NodeIterator's backward
            // walk (which prunes nothing, so the whole step fits in the DOM layer).
            case "prev_in_subtree":
            {
                var found = dom.PrevInSubtree(ParseNodeOrZero(arg1), ParseNodeOrZero(arg2));
                return found is { } id ? Index(id) : "-1";
            }

            // Step past a whole subtree rather than into it: NodeFilter.FILTER_REJECT
            // prunes the rejected node's descendants, unlike FILTER_SKIP.
            case "next_after_subtree":
            {
                var found = dom.NextAfterSubtree(ParseNodeOrZero(arg1), ParseNodeOrZero(arg2));
                return found is { } id ? Index(id) : "-1";
            }

            case "child_nodes":
                return SerdeJson.IntArray(Indices(dom.Children(ParseNodeOrZero(arg1))));

            case "tag_name":
            {
                var element = dom.GetNode(ParseNodeOrZero(arg1))?.AsElement();
                var name = string.Empty;
                if (element is not null)
                {
                    name = string.Equals(element.Name.Ns, Namespaces.Html, StringComparison.Ordinal)
                        ? element.Name.Local.ToUpperInvariant()
                        : element.Name.Prefix is { } prefix
                            ? prefix + ":" + element.Name.Local
                            : element.Name.Local;
                }

                return SerdeJson.String(name);
            }

            case "local_name":
            {
                var element = dom.GetNode(ParseNodeOrZero(arg1))?.AsElement();
                return SerdeJson.String(element?.Name.Local ?? string.Empty);
            }

            // The tree builder already assigns foreign content (an <svg>/<math>
            // subtree) its own namespace; expose it so JS does not have to guess the
            // namespace from the tag name.
            case "namespace_uri":
            {
                var element = dom.GetNode(ParseNodeOrZero(arg1))?.AsElement();
                return SerdeJson.String(element?.Name.Ns ?? string.Empty);
            }

            case "get_attribute":
            {
                var value = dom.GetNode(ParseNodeOrZero(arg1))?.GetAttribute(arg2);
                return value is null ? "null" : SerdeJson.String(value);
            }

            case "attribute_names":
            {
                var attrs = dom.GetNode(ParseNodeOrZero(arg1))?.Attrs;
                if (attrs is null)
                {
                    return "[]";
                }

                var names = new List<string>(attrs.Count);
                foreach (var attr in attrs)
                {
                    names.Add(attr.QualifiedName);
                }

                return SerdeJson.StringArray(names);
            }

            case "set_attribute":
            {
                var nodeId = ParseNodeOrZero(arg1);
                var split = arg2.IndexOf('\0', StringComparison.Ordinal);
                if (split >= 0)
                {
                    var name = arg2[..split];
                    var value = arg2[(split + 1)..];
                    if (string.Equals(name, "id", StringComparison.Ordinal))
                    {
                        var oldId = dom.GetNode(nodeId)?.GetAttribute("id");
                        dom.GetNode(nodeId)?.SetAttribute(name, value);
                        dom.UpdateIdIndex(nodeId, oldId, value);
                    }
                    else
                    {
                        dom.GetNode(nodeId)?.SetAttribute(name, value);
                    }
                }

                return "true";
            }

            case "inner_html":
                return SerdeJson.String(dom.InnerHtml(ParseNodeOrZero(arg1)));

            case "outer_html":
                return SerdeJson.String(dom.OuterHtml(ParseNodeOrZero(arg1)));

            case "append_child":
            {
                // Reject if either nid failed to parse (was "undefined"/empty) - those
                // default to 0, which is the document root, and silently operating on
                // it corrupts the tree. Require both args to be valid integers.
                if (!uint.TryParse(arg1, out var parentRaw) || !uint.TryParse(arg2, out var childRaw))
                {
                    return "false";
                }

                var parent = NodeId.New(parentRaw);
                var child = NodeId.New(childRaw);
                dom.AppendChild(parent, child);
                return Bool(dom.GetNode(child)?.Parent == parent);
            }

            case "remove_child":
            {
                if (!uint.TryParse(arg1, out var childRaw))
                {
                    return "false";
                }

                var child = NodeId.New(childRaw);
                var hadParent = dom.GetNode(child)?.Parent is not null;
                dom.RemoveChild(child);
                return Bool(hadParent && dom.GetNode(child) is { Parent: null });
            }

            case "insert_before":
            {
                if (!uint.TryParse(arg1, out var newRaw) || !uint.TryParse(arg2, out var refRaw))
                {
                    return "false";
                }

                var refNode = NodeId.New(refRaw);
                var newNode = NodeId.New(newRaw);
                var expectedParent = dom.GetNode(refNode)?.Parent;
                dom.InsertBefore(refNode, newNode);
                return Bool(expectedParent is not null
                    && dom.GetNode(newNode)?.Parent == expectedParent);
            }

            case "remove_attribute":
            {
                var attrs = dom.GetNode(ParseNodeOrZero(arg1))?.Attrs;
                attrs?.RemoveAll(a => a.QualifiedNameEquals(arg2));
                return "true";
            }

            // Namespace-aware attribute ops. arg2 packs the pieces with a NUL:
            //   get/remove: "<namespace>\0<localName>"
            //   set:        "<namespace>\0<qualifiedName>\0<value>"
            case "get_attribute_ns":
            {
                var (ns, local) = SplitOnceNul(arg2);
                var value = dom.GetNode(ParseNodeOrZero(arg1))?.GetAttributeNs(ns, local);
                return value is null ? "null" : SerdeJson.String(value);
            }

            case "set_attribute_ns":
            {
                var nodeId = ParseNodeOrZero(arg1);
                var parts = arg2.Split('\0', 3);
                var ns = parts.Length > 0 ? parts[0] : string.Empty;
                var qualified = parts.Length > 1 ? parts[1] : string.Empty;
                var value = parts.Length > 2 ? parts[2] : string.Empty;
                if (qualified.Length != 0)
                {
                    var colon = qualified.IndexOf(':', StringComparison.Ordinal);
                    var local = colon >= 0 ? qualified[(colon + 1)..] : qualified;
                    if (ns.Length == 0 && string.Equals(local, "id", StringComparison.Ordinal))
                    {
                        var oldId = dom.GetNode(nodeId)?.GetAttribute("id");
                        dom.GetNode(nodeId)?.SetAttributeNs(ns, qualified, value);
                        dom.UpdateIdIndex(nodeId, oldId, value);
                    }
                    else
                    {
                        dom.GetNode(nodeId)?.SetAttributeNs(ns, qualified, value);
                    }
                }

                return "true";
            }

            case "remove_attribute_ns":
            {
                var nodeId = ParseNodeOrZero(arg1);
                var (ns, local) = SplitOnceNul(arg2);
                if (ns.Length == 0 && string.Equals(local, "id", StringComparison.Ordinal))
                {
                    var oldId = dom.GetNode(nodeId)?.GetAttribute("id");
                    dom.GetNode(nodeId)?.RemoveAttributeNs(ns, local);
                    dom.UpdateIdIndex(nodeId, oldId, null);
                }
                else
                {
                    dom.GetNode(nodeId)?.RemoveAttributeNs(ns, local);
                }

                return "true";
            }

            case "set_inner_html":
            {
                // nid=0 is the document root; never allow innerHTML to clear it. A nid
                // parse failure (for example "undefined") lands here too.
                if (!uint.TryParse(arg1, out var raw) || raw == 0)
                {
                    return "false";
                }

                var target = NodeId.New(raw);
                foreach (var child in dom.Children(target))
                {
                    dom.Detach(child);
                }

                if (arg2.Length != 0)
                {
                    var contextName = (dom.GetNode(target)?.Data as ElementData)?.Name;
                    var fragment = contextName is { } context
                        ? HtmlParsing.ParseFragmentWithContext(arg2, context)
                        : HtmlParsing.ParseFragment(arg2);
                    dom.ImportChildrenFrom(target, fragment, fragment.FragmentRoot());
                    foreach (var child in dom.Children(target))
                    {
                        StateHelpers.MarkScriptSubtreeStarted(gs, child);
                    }
                }

                return "true";
            }

            case "set_inner_html_context":
            {
                if (!uint.TryParse(arg1, out var raw) || raw == 0)
                {
                    return "false";
                }

                var target = NodeId.New(raw);
                var (contextName, html) = StateHelpers.FragmentContextAndHtml(arg2);
                foreach (var child in dom.Children(target))
                {
                    dom.Detach(child);
                }

                if (html.Length != 0)
                {
                    var fragment = HtmlParsing.ParseFragmentWithContext(html, contextName);
                    dom.ImportChildrenFrom(target, fragment, fragment.FragmentRoot());
                    foreach (var child in dom.Children(target))
                    {
                        StateHelpers.MarkScriptSubtreeStarted(gs, child);
                    }
                }

                return "true";
            }

            // Range.createContextualFragment has a deliberately different script
            // policy from innerHTML: scripts remain eligible and are prepared when the
            // returned fragment is inserted into a connected document.
            case "set_fragment_html_executable":
            {
                if (!uint.TryParse(arg1, out var raw) || raw == 0)
                {
                    return "false";
                }

                var target = NodeId.New(raw);
                var (contextName, html) = StateHelpers.FragmentContextAndHtml(arg2);
                foreach (var child in dom.Children(target))
                {
                    dom.Detach(child);
                }

                if (html.Length != 0)
                {
                    var fragment = HtmlParsing.ParseFragmentWithContext(html, contextName);
                    dom.ImportChildrenFrom(target, fragment, fragment.FragmentRoot());
                }

                return "true";
            }

            // document.write() feeds the document's input stream, so the calls share
            // one parser and one tokenizer state. Returns the nodes that became
            // complete with this call, as [[parent, node], ...], parents before
            // children. A `parent` of 0 means the node belongs at the insertion point,
            // which the caller knows. Nothing is inserted here: that must go through
            // Node.appendChild on the JS side, because that call also reports the
            // mutation, registers window named access, and loads a written stylesheet.
            case "document_write":
            {
                gs.WriteStream ??= new DocumentWriteStream();
                var placements = gs.WriteStream.Write(arg2, dom);
                var sb = new StringBuilder(placements.Count * 8 + 2);
                sb.Append('[');
                for (var i = 0; i < placements.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append('[');
                    sb.Append((placements[i].Parent?.Index ?? 0).ToString(CultureInfo.InvariantCulture));
                    sb.Append(',');
                    sb.Append(placements[i].Node.Index.ToString(CultureInfo.InvariantCulture));
                    sb.Append(']');
                }

                sb.Append(']');
                return sb.ToString();
            }

            // document.open() discards what the input stream holds and starts over.
            case "document_write_reset":
                gs.WriteStream = null;
                return "true";

            case "set_text_content":
            {
                var node = dom.GetNode(ParseNodeOrZero(arg1));
                switch (node?.Data)
                {
                    case TextData text:
                        text.Contents = arg2;
                        break;
                    case CommentData comment:
                        comment.Contents = arg2;
                        break;
                    case ProcessingInstructionData pi:
                        pi.Data = arg2;
                        break;
                }

                return "true";
            }

            // A <template>'s children live in a separate contents document, so this is
            // the only route to them from JS. Allocates one on demand for templates
            // built via createElement.
            case "template_contents":
            {
                var contents = dom.TemplateContents(ParseNodeOrZero(arg1));
                return contents is { } id ? Index(id) : "-1";
            }

            case "create_document_fragment":
                return Index(dom.NewNode(NodeData.Document));

            case "clone_node":
            {
                if (!uint.TryParse(arg1, out var raw))
                {
                    return "-1";
                }

                var source = NodeId.New(raw);
                var cloned = dom.CloneNode(source, string.Equals(arg2, "true", StringComparison.Ordinal));
                if (cloned is not { } copy)
                {
                    return "-1";
                }

                StateHelpers.PropagateScriptStartState(dom, source, copy, gs.AlreadyStartedScripts);
                return Index(copy);
            }

            case "create_element":
                return Index(dom.NewNode(NodeData.Element(QualName.Html(arg1))));

            case "create_element_ns":
            {
                var (ns, qualified) = SplitOnceNul(arg1);
                string? prefix;
                string local;
                var colon = qualified.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && colon < qualified.Length - 1)
                {
                    prefix = qualified[..colon];
                    local = qualified[(colon + 1)..];
                }
                else if (colon < 0 && qualified.Length != 0)
                {
                    prefix = null;
                    local = qualified;
                }
                else
                {
                    return "-1";
                }

                return Index(dom.NewNode(NodeData.Element(new QualName(prefix, ns, local))));
            }

            case "create_text_node":
                return Index(dom.NewNode(NodeData.Text(arg1)));

            case "create_comment_node":
                return Index(dom.NewNode(NodeData.Comment(arg1)));

            // arg1 = target, arg2 = data
            case "create_processing_instruction":
                return Index(dom.NewNode(NodeData.ProcessingInstruction(arg1, arg2)));

            // arg1 = name, arg2 = public_id. system_id is stored only in the JS
            // wrapper, since neither current WPT test reads it back from the tree.
            case "create_doctype":
                return Index(dom.NewNode(NodeData.Doctype(arg1, arg2, string.Empty)));

            case "pi_target":
            {
                var pi = dom.GetNode(ParseNodeOrZero(arg1))?.Data as ProcessingInstructionData;
                return SerdeJson.String(pi?.Target ?? string.Empty);
            }

            case "doctype_name":
            {
                var doctype = dom.GetNode(ParseNodeOrZero(arg1))?.Data as DoctypeData;
                return SerdeJson.String(doctype?.Name ?? string.Empty);
            }

            case "doctype_public_id":
            {
                var doctype = dom.GetNode(ParseNodeOrZero(arg1))?.Data as DoctypeData;
                return SerdeJson.String(doctype?.PublicId ?? string.Empty);
            }

            case "element_children":
            {
                var ids = new List<int>();
                foreach (var id in dom.Children(ParseNodeOrZero(arg1)))
                {
                    if (dom.GetNode(id)?.IsElement == true)
                    {
                        ids.Add(id.Index);
                    }
                }

                return SerdeJson.IntArray(ids);
            }

            case "has_child_nodes":
                return Bool(dom.GetNode(ParseNodeOrZero(arg1))?.FirstChild is not null);

            case "contains":
                return Bool(dom.Descendants(ParseNodeOrZero(arg1)).Contains(ParseNodeOrZero(arg2)));

            // Connectivity is maintained incrementally by DomTree. Exposing the cached
            // bit avoids an ancestor op crossing for every level when JS builds a deep
            // detached subtree.
            case "is_connected":
                return Bool(dom.IsConnected(ParseNodeOrZero(arg1)));

            // Index of a node among its parent's children. Walks prev siblings here,
            // avoiding the per-step JS->op round trips a Range comparison would make.
            case "node_index":
                return StateHelpers.NodeChildIndex(dom, ParseNodeOrZero(arg1))
                    .ToString(CultureInfo.InvariantCulture);

            // Document (preorder) tree order of two nodes: -1 if a precedes b, 1 if a
            // follows b, 0 if equal. Used by the Range boundary-point algorithms.
            case "compare_order":
                return StateHelpers.CompareNodeOrder(dom, ParseNodeOrZero(arg1), ParseNodeOrZero(arg2))
                    .ToString(CultureInfo.InvariantCulture);

            // Root (topmost ancestor) of a node, in one op rather than an O(depth)
            // walk of parentNode ops from JS.
            case "node_root":
            {
                var current = ParseNodeOrZero(arg1);
                while (dom.GetNode(current)?.Parent is { } parent)
                {
                    current = parent;
                }

                return Index(current);
            }

            default:
                return "null";
        }
    }

    /// <summary>
    /// The invalidation pass every <c>op_dom</c> call runs before dispatching.
    /// </summary>
    /// <remarks>
    /// Any changed attribute on a connected node can participate in an author
    /// selector. Detached subtree construction, failed operations, and no-op value
    /// assignments cannot change live layout and preserve the prepared render. The
    /// next relevant mutation invalidates once; subsequent writes are coalesced until
    /// geometry is read again.
    /// </remarks>
    private static void Prelude(ObscuraState state, string cmd, string arg1, string arg2)
    {
        // Scroll offsets belong to a node at its current tree position. Temporary
        // box/style loss keeps that latent state, but DOM removal, reparenting, and
        // subtree replacement reset the affected identities, matching Chromium's
        // lifecycle behavior.
        HashSet<NodeId> resetNodes = [];
        if (state.Dom is { } tree)
        {
            List<NodeId> roots = [];
            switch (cmd)
            {
                case "remove_child":
                    if (uint.TryParse(arg1, out var removed))
                    {
                        roots.Add(NodeId.New(removed));
                    }

                    break;
                case "append_child":
                    if (uint.TryParse(arg2, out var appended)
                        && tree.GetNode(NodeId.New(appended))?.Parent is not null)
                    {
                        roots.Add(NodeId.New(appended));
                    }

                    break;
                case "insert_before":
                    if (uint.TryParse(arg1, out var inserted)
                        && tree.GetNode(NodeId.New(inserted))?.Parent is not null)
                    {
                        roots.Add(NodeId.New(inserted));
                    }

                    break;
                case "set_inner_html":
                case "set_inner_html_context":
                case "set_text_content":
                    if (uint.TryParse(arg1, out var replaced))
                    {
                        roots.AddRange(tree.Children(NodeId.New(replaced)));
                    }

                    break;
            }

            foreach (var root in roots)
            {
                resetNodes.Add(root);
                foreach (var descendant in tree.Descendants(root))
                {
                    resetNodes.Add(descendant);
                }
            }
        }

        var impact = state.Dom is { } dom
            ? RenderInvalidation.MutationImpact(dom, cmd, arg1, arg2)
            : RenderMutationImpact.None;
        var retainedStyleMutation = state.Dom is { } styleDom
            ? RenderInvalidation.RetainedMutation(styleDom, cmd, arg1, arg2)
            : null;
        var invalidate = impact.Connected && impact.ActualChange;
        if (invalidate)
        {
            state.ActivityGeneration = unchecked(state.ActivityGeneration + 1);
        }

        if (resetNodes.Count != 0)
        {
            foreach (var node in resetNodes)
            {
                state.ElementScrollOffsets.Remove(node);
            }

            state.ScrollGeneration = unchecked(state.ScrollGeneration + 1);
            if (invalidate)
            {
                state.AnimationTimeline.RemoveSubtree(resetNodes);
            }
        }

        if (!invalidate)
        {
            return;
        }

        var mutationTimeMs = (float)Math.Min(
            state.AnimationTimelineElapsedMilliseconds,
            float.MaxValue);

        // Keep animation birth epochs local to the changed subtree. A single
        // document-global timestamp made a later unrelated write restart every
        // not-yet-sampled animation at the same instant.
        NodeId? directRoot = cmd switch
        {
            "append_child" => uint.TryParse(arg2, out var a) ? NodeId.New(a) : null,
            "insert_before" or "set_attribute" or "remove_attribute" or "set_attribute_ns"
                or "remove_attribute_ns" => uint.TryParse(arg1, out var b) ? NodeId.New(b) : null,
            _ => null,
        };
        if (directRoot is { } direct && state.Dom is { } directDom)
        {
            state.AnimationTimeline.NoteStartCandidate(direct, mutationTimeMs);
            foreach (var descendant in directDom.Descendants(direct))
            {
                state.AnimationTimeline.NoteStartCandidate(descendant, mutationTimeMs);
            }
        }

        NodeId? scopeRoot = cmd switch
        {
            "append_child" => uint.TryParse(arg1, out var p) ? NodeId.New(p) : null,
            "insert_before" => uint.TryParse(arg2, out var r)
                ? state.Dom?.GetNode(NodeId.New(r))?.Parent
                : null,
            "remove_child" => uint.TryParse(arg1, out var c)
                ? state.Dom?.GetNode(NodeId.New(c))?.Parent
                : null,
            "set_inner_html" or "set_inner_html_context" or "set_text_content" =>
                uint.TryParse(arg1, out var t) ? NodeId.New(t) : null,
            _ => null,
        };
        if (scopeRoot is { } scope)
        {
            state.AnimationTimeline.NoteSubtreeStartCandidate(scope, mutationTimeMs);
        }

        if (retainedStyleMutation is { } mutation)
        {
            var retained = state.PreparedRender is not null
                && RenderInvalidation.QueueRetainedStyleMutation(state.PendingStyleMutations, mutation);
            if (!retained)
            {
                state.PreparedRender = null;
                state.PendingStyleMutations.Clear();
            }
        }
        else
        {
            state.PreparedRender = null;
            state.PendingStyleMutations.Clear();
        }

        state.ResolvedScroll = null;
    }

    private static NodeId ParseNodeOrZero(string value) =>
        NodeId.New(uint.TryParse(value, out var raw) ? raw : 0);

    private static string Index(NodeId id) => id.Index.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static List<int> Indices(List<NodeId> ids)
    {
        var result = new List<int>(ids.Count);
        foreach (var id in ids)
        {
            result.Add(id.Index);
        }

        return result;
    }

    private static (string Namespace, string Local) SplitOnceNul(string value)
    {
        var split = value.IndexOf('\0', StringComparison.Ordinal);
        return split < 0 ? (string.Empty, value) : (value[..split], value[(split + 1)..]);
    }
}
