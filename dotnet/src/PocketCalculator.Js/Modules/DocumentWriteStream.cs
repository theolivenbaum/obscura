using System.Globalization;
using System.Text;

using PocketCalculator.Dom;

namespace PocketCalculator.Js.Modules;

/// <summary>
/// Where one node produced by the input stream belongs. <see cref="Parent"/> is
/// null for a node at the head of the input stream, which belongs at the
/// insertion point.
/// </summary>
public readonly record struct Placement(NodeId? Parent, NodeId Node);

/// <summary>
/// The document's input stream for <c>document.write()</c>.
/// </summary>
/// <remarks>
/// <para>
/// There is one input stream per document, and the parser carries its state
/// across calls. A construct may therefore be split at any point, even in the
/// middle of a tag name.
/// See <see href="https://html.spec.whatwg.org/multipage/dynamic-markup-insertion.html#dom-document-write"/>.
/// </para>
/// <para>
/// This class inserts nothing itself. It creates the nodes and reports where each
/// belongs, because <c>Node.appendChild</c> on the JS side reports the mutation at
/// the same time, registers window named access, and loads a written stylesheet.
/// </para>
/// <para>
/// <b>Port note.</b> The Rust stream owns a live <c>html5ever</c> parser and feeds
/// it each call's argument, so the tokenizer's state persists and nothing is
/// re-tokenized. AngleSharp's tokenizer cannot be resumed once it has reached the end
/// of its input, so this port keeps the concatenated input stream and re-parses it per call. The
/// parser is deterministic and the input only ever grows at the end, so every node
/// keeps its position; nodes are therefore identified by their path from the
/// fragment root rather than by an arena id, and the handed-over map, the
/// fresh-children walk, the hold-back of incomplete script/template nodes, the
/// staging container and the subtree mapping are all ported unchanged.
/// The cost is that a write is linear in the whole stream rather than in the new
/// text; see the note on <see cref="Write"/>.
/// </para>
/// </remarks>
public sealed class DocumentWriteStream
{
    private readonly StringBuilder _input = new();

    /// <summary>
    /// Maps a node of the parser tree, by its path, to its copy in the document. A
    /// path missing here has not been handed over yet.
    /// </summary>
    private readonly Dictionary<string, NodeId> _handedOver = new(StringComparer.Ordinal);

    /// <summary>Staging for copying a whole subtree, reused.</summary>
    private NodeId? _staging;

    /// <summary>
    /// Keeps the document nodes the stream still maps to: a later write inserts under
    /// them, so the DOM collector must not free them while detached (DomTree.Gc.cs).
    /// </summary>
    public void MarkRoots(DomCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (_staging is { } staging)
        {
            collection.Keep(staging);
        }

        foreach (var node in _handedOver.Values)
        {
            collection.Keep(node);
        }
    }

    /// <summary>
    /// Pushes a call's arguments into the input stream and mirrors what the parser
    /// made of them. Returns the nodes to insert, parents before children.
    /// </summary>
    /// <remarks>
    /// Only what is new is handed over. The walk into already-handed-over subtrees
    /// stops at the first child that is already done, because the parser only
    /// appends at the back; without that, a walk over all children would cost as
    /// much per call as the input stream written so far is long, and be quadratic
    /// over a thousand calls.
    /// </remarks>
    public List<Placement> Write(string html, DomTree dom)
    {
        ArgumentNullException.ThrowIfNull(dom);

        _input.Append(html);
        var stream = _input.ToString();

        // The elements still open at the end of the stream, which the parser would still be
        // able to add to. html5ever answers this through TreeSink::trace_handles; the tree
        // builder reports its stack of open elements as the input runs out. That includes a
        // raw-text element such as an unterminated <script>. When the stream ends inside a
        // tag, the tag produces no node, so there is nothing of it to hold back.
        var retained = new HashSet<NodeId>();
        var source = HtmlParsing.ParseFragmentWithContext(stream, QualName.Html("body"), retained);
        var root = source.FragmentRoot();

        var placements = new List<Placement>();
        var stack = new List<Entry>();
        AppendFreshChildren(stack, source, root, string.Empty);

        while (stack.Count > 0)
        {
            var entry = stack[^1];
            stack.RemoveAt(stack.Count - 1);

            var node = source.GetNode(entry.Node);
            if (node is null)
            {
                continue;
            }

            if (_handedOver.TryGetValue(entry.Path, out var known))
            {
                // A text node at the end of the input stream grows with every call
                // and stays the same node. Elements no longer change after their
                // creation.
                if (node.IsText && dom.GetNode(known)?.Data is TextData text)
                {
                    var grown = source.TextContent(entry.Node);
                    dom.ChargeGrowth(2L * (grown.Length - text.Contents.Length));
                    text.Contents = grown;
                }

                AppendFreshChildren(stack, source, entry.Node, entry.Path);
                continue;
            }

            NodeId? parent;
            if (entry.Path.Length == 0)
            {
                continue;
            }

            var separator = entry.Path.LastIndexOf('/');
            if (separator < 0)
            {
                // A direct child of the fragment root belongs at the insertion point.
                parent = null;
            }
            else if (_handedOver.TryGetValue(entry.Path[..separator], out var parentCopy))
            {
                parent = parentCopy;
            }
            else
            {
                // If the parent is held back, this one waits along.
                continue;
            }

            if (NeedsToBeComplete(source, entry.Node))
            {
                if (retained.Contains(entry.Node))
                {
                    continue;
                }

                var container = _staging ??= dom.NewNode(NodeData.Document);
                var complete = dom.ImportNodeFrom(container, source, entry.Node);
                if (complete is not { } completeId)
                {
                    continue;
                }

                dom.Detach(completeId);
                MapSubtree(source, entry.Node, entry.Path, dom, completeId);
                placements.Add(new Placement(parent, completeId));
                continue;
            }

            // Handed over shallow and left to grow in place. That makes an element
            // that is never closed appear at once instead of never.
            var copy = dom.NewNode(node.Data.Clone());
            _handedOver[entry.Path] = copy;
            placements.Add(new Placement(parent, copy));
            AppendFreshChildren(stack, source, entry.Node, entry.Path);
        }

        return placements;
    }

    /// <summary>
    /// A <c>&lt;script&gt;</c> runs as soon as it is inserted, so a half-written one
    /// must not. A <c>&lt;template&gt;</c> keeps its children in its own contents
    /// document, which a child walk does not reach, so it can only be copied as a
    /// whole.
    /// </summary>
    private static bool NeedsToBeComplete(DomTree source, NodeId node) =>
        source.GetNode(node)?.ElementName?.Local is { } local
        && (string.Equals(local, "script", StringComparison.Ordinal)
            || string.Equals(local, "template", StringComparison.Ordinal));

    /// <summary>
    /// The children of <paramref name="parent"/> still to do, pushed so the top of
    /// <paramref name="stack"/> is processed first.
    /// </summary>
    /// <remarks>
    /// The walk goes backward from the last child and stops at the first one already
    /// handed over. The parser only appends at the back, so everything before it is
    /// done. This one already handed-over child comes back along, because a text
    /// node at the end of the input stream keeps growing and an open element still
    /// receives children.
    /// </remarks>
    private void AppendFreshChildren(
        List<Entry> stack,
        DomTree source,
        NodeId parent,
        string parentPath)
    {
        var children = source.Children(parent);
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var path = ChildPath(parentPath, i);
            stack.Add(new Entry(children[i], path));
            if (_handedOver.ContainsKey(path))
            {
                break;
            }
        }
    }

    /// <summary>
    /// After copying, both trees have the same shape, so a lockstep walk aligns the
    /// nodes.
    /// </summary>
    private void MapSubtree(
        DomTree source,
        NodeId sourceNode,
        string sourcePath,
        DomTree dom,
        NodeId copy)
    {
        var pairs = new List<(NodeId From, string FromPath, NodeId To)>
        {
            (sourceNode, sourcePath, copy),
        };

        while (pairs.Count > 0)
        {
            var (from, fromPath, to) = pairs[^1];
            pairs.RemoveAt(pairs.Count - 1);
            _handedOver[fromPath] = to;

            var fromChildren = source.Children(from);
            var toChildren = dom.Children(to);
            var shared = Math.Min(fromChildren.Count, toChildren.Count);
            for (var i = 0; i < shared; i++)
            {
                pairs.Add((fromChildren[i], ChildPath(fromPath, i), toChildren[i]));
            }
        }
    }

    private static string ChildPath(string parentPath, int index) =>
        parentPath.Length == 0
            ? index.ToString(CultureInfo.InvariantCulture)
            : string.Concat(parentPath, "/", index.ToString(CultureInfo.InvariantCulture));

    private readonly record struct Entry(NodeId Node, string Path);
}
