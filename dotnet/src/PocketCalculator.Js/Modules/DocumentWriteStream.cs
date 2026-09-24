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
/// Like the Rust stream (crates/obscura-js/src/write_stream.rs), which owns a live
/// <c>html5ever</c> parser, this keeps one tree builder alive across calls
/// (<see cref="HtmlTreeBuilder.Feed"/>) and parses only what each call adds, so
/// nodes keep their arena ids from call to call. What the end of a call might still
/// change, such as a tag cut off in its name, waits for the next call.
/// </para>
/// <para>
/// DEVIATION (SECURITY.md M11): the port used to re-parse the whole stream on every
/// call and identify nodes by their path, because AngleSharp's tree builder could not
/// be fed in pieces. A write cost as much as everything written before it, so 5,000
/// small writes took 30 s.
/// </para>
/// </remarks>
public sealed class DocumentWriteStream
{
    /// <summary>The input from where the tree builder stopped, newlines normalized.</summary>
    private readonly StringBuilder _pending = new();

    /// <summary>A CR ended the last call; it becomes one newline with a following LF.</summary>
    private bool _pendingCr;

    private DomTree? _source;
    private HtmlTreeBuilder? _builder;

    /// <summary>
    /// Maps a node of the parser tree to its copy in the document. A node missing here
    /// has not been handed over yet.
    /// </summary>
    private readonly Dictionary<NodeId, NodeId> _handedOver = [];

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

        AppendNormalized(html);
        if (_builder is null)
        {
            _source = new DomTree();
            var fragmentRoot = _source.NewNode(NodeData.Element(QualName.Html("html")));
            _source.AppendChild(_source.Document, fragmentRoot);
            _builder = HtmlTreeBuilder.CreateIncremental(_source, fragmentRoot, QualName.Html("body"));
        }

        var consumed = _builder.Feed(_pending.ToString());
        _pending.Remove(0, consumed);

        var source = _source!;
        var root = source.FragmentRoot();
        var placements = new List<Placement>();
        var stack = new List<NodeId>();
        AppendFreshChildren(stack, source, root);

        while (stack.Count > 0)
        {
            var current = stack[^1];
            stack.RemoveAt(stack.Count - 1);

            var node = source.GetNode(current);
            if (node is null)
            {
                continue;
            }

            if (_handedOver.TryGetValue(current, out var known))
            {
                // A text node at the end of the input stream grows with every call
                // and stays the same node. Elements no longer change after their
                // creation.
                if (node.IsText && dom.GetNode(known)?.Data is TextData text)
                {
                    var grown = source.TextContent(current);
                    dom.ChargeGrowth(2L * (grown.Length - text.Contents.Length));
                    text.Contents = grown;
                }

                AppendFreshChildren(stack, source, current);
                continue;
            }

            NodeId? parent;
            if (node.Parent is not { } sourceParent)
            {
                continue;
            }

            if (sourceParent == root)
            {
                // A direct child of the fragment root belongs at the insertion point.
                parent = null;
            }
            else if (_handedOver.TryGetValue(sourceParent, out var parentCopy))
            {
                parent = parentCopy;
            }
            else
            {
                // If the parent is held back, this one waits along.
                continue;
            }

            if (NeedsToBeComplete(source, current))
            {
                // Still open: the parser can still add to it.
                if (_builder.IsOpen(current))
                {
                    continue;
                }

                var container = _staging ??= dom.NewNode(NodeData.Document);
                var complete = dom.ImportNodeFrom(container, source, current);
                if (complete is not { } completeId)
                {
                    continue;
                }

                dom.Detach(completeId);
                MapSubtree(source, current, dom, completeId);
                placements.Add(new Placement(parent, completeId));
                continue;
            }

            // Handed over shallow and left to grow in place. That makes an element
            // that is never closed appear at once instead of never.
            var copy = dom.NewNode(node.Data.Clone());
            _handedOver[current] = copy;
            placements.Add(new Placement(parent, copy));
            AppendFreshChildren(stack, source, current);
        }

        return placements;
    }

    /// <summary>
    /// The input stream's newline normalization, which the tree builder's resumption
    /// relies on: CRLF and CR become LF. A CR at the end waits, since an LF may follow.
    /// </summary>
    private void AppendNormalized(string html)
    {
        if (html.Length == 0)
        {
            return;
        }

        var text = _pendingCr ? "\r" + html : html;
        _pendingCr = text[^1] == '\r';
        if (_pendingCr)
        {
            text = text[..^1];
        }

        if (text.Contains('\r'))
        {
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }

        _pending.Append(text);
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
    private void AppendFreshChildren(List<NodeId> stack, DomTree source, NodeId parent)
    {
        for (var child = source.GetNode(parent)?.LastChild; child is { } id; child = source.GetNode(id)?.PrevSibling)
        {
            stack.Add(id);
            if (_handedOver.ContainsKey(id))
            {
                break;
            }
        }
    }

    /// <summary>
    /// After copying, both trees have the same shape, so a lockstep walk aligns the
    /// nodes.
    /// </summary>
    private void MapSubtree(DomTree source, NodeId sourceNode, DomTree dom, NodeId copy)
    {
        var pairs = new List<(NodeId From, NodeId To)> { (sourceNode, copy) };
        while (pairs.Count > 0)
        {
            var (from, to) = pairs[^1];
            pairs.RemoveAt(pairs.Count - 1);
            _handedOver[from] = to;

            var fromChildren = source.Children(from);
            var toChildren = dom.Children(to);
            var shared = Math.Min(fromChildren.Count, toChildren.Count);
            for (var i = 0; i < shared; i++)
            {
                pairs.Add((fromChildren[i], toChildren[i]));
            }
        }
    }
}
