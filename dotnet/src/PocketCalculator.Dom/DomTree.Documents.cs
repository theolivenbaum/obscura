namespace PocketCalculator.Dom;

/// <summary>
/// Documents other than the page's own: the ones <c>DOMImplementation.createHTMLDocument</c>,
/// <c>createDocument</c>, <c>DOMParser</c>, <c>new Document()</c>, <c>cloneNode</c> of a document
/// and <c>XMLHttpRequest.responseXML</c> create.
/// </summary>
/// <remarks>
/// <para>
/// Port addition; the Rust engine has no second document. bootstrap.js stood one in as a plain
/// object over a detached <c>&lt;html&gt;</c> element of the page, so <c>ownerDocument</c> never
/// changed and <c>importNode</c>/<c>adoptNode</c> on it returned their argument. A script that
/// copied the page into such a document (dell.com's bot detector:
/// <c>createHTMLDocument("cloner-doc").importNode(documentElement, false)</c>, then appended
/// the result) moved the page's own <c>&lt;html&gt;</c> out of the page.
/// </para>
/// <para>
/// Here such a document is a document node in the same arena, so every op and the selector
/// engine work on it unchanged. It is never connected in the native sense
/// (<see cref="Node.Connected"/> means "in the page"), so nothing in it is rendered, matched by
/// the page's id index or reached by a walk from <see cref="Document"/>. Which document a node
/// belongs to is recorded here for nodes that do not belong to the page
/// (<see cref="OwnerDocumentOf"/>); inserting a node under a parent of another document adopts
/// it, as DOM's insert does.
/// </para>
/// </remarks>
public sealed partial class DomTree
{
    /// <summary>Secondary document nodes, with the content type each was created with.</summary>
    private Dictionary<NodeId, SecondaryDocument>? _documents;

    /// <summary>The node document of every node that belongs to a secondary document.</summary>
    private Dictionary<NodeId, NodeId>? _ownerDocuments;

    /// <summary>Whether any secondary document exists. Insertion pays nothing while none does.</summary>
    public bool HasSecondaryDocuments => _documents is { Count: > 0 };

    /// <summary>Create an empty secondary document of <paramref name="contentType"/>.</summary>
    public NodeId CreateDocument(string contentType, bool quirks = false)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        var id = NewNode(NodeData.Document);
        (_documents ??= [])[id] = new SecondaryDocument(contentType, quirks);
        return id;
    }

    /// <summary>Whether a secondary document is in quirks mode (DOMParser input without a doctype).</summary>
    public bool IsDocumentQuirks(NodeId id) =>
        id == Document ? IsQuirks : _documents is { } docs && docs.TryGetValue(id, out var info) && info.Quirks;

    /// <summary>Whether <paramref name="id"/> is a secondary document (not a fragment, not the page).</summary>
    public bool IsSecondaryDocument(NodeId id) => _documents is { } docs && docs.ContainsKey(id) && Slot(id) is not null;

    /// <summary>The content type of a secondary document, or null when <paramref name="id"/> is none.</summary>
    public string? DocumentContentType(NodeId id) =>
        _documents is { } docs && Slot(id) is not null && docs.TryGetValue(id, out var info) ? info.ContentType : null;

    /// <summary>
    /// The node document of <paramref name="node"/>: a document is its own, a node of a secondary
    /// document that document, and everything else the page's <see cref="Document"/>.
    /// </summary>
    public NodeId NodeDocumentOf(NodeId node)
    {
        if (_documents is not { Count: > 0 } docs)
        {
            return Document;
        }

        if (docs.ContainsKey(node))
        {
            return node;
        }

        return _ownerDocuments is { } owners && owners.TryGetValue(node, out var owner) ? owner : Document;
    }

    /// <summary><c>Node.ownerDocument</c>'s document: null for any document node, else <see cref="NodeDocumentOf"/>.</summary>
    public NodeId? OwnerDocumentOf(NodeId node)
    {
        if (node == Document || IsSecondaryDocument(node))
        {
            return null;
        }

        return NodeDocumentOf(node);
    }

    /// <summary>
    /// DOM "adopt": make <paramref name="document"/> the node document of <paramref name="root"/>
    /// and of its shadow-including descendants. Does not detach <paramref name="root"/>; the
    /// caller removes it first. Template contents stay where they are (their owner document is
    /// the template contents owner document in DOM, which the port does not model).
    /// </summary>
    public void AdoptSubtree(NodeId root, NodeId document)
    {
        if (Slot(root) is null || root == Document || IsSecondaryDocument(root))
        {
            return;
        }

        var toPage = document == Document || !IsSecondaryDocument(document);
        if (toPage && _ownerDocuments is not { Count: > 0 })
        {
            return;
        }

        var stack = new Stack<NodeId>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (++visited > _nodes.Count)
            {
                break;
            }

            if (Slot(current) is not { } node)
            {
                continue;
            }

            if (toPage)
            {
                _ownerDocuments!.Remove(current);
            }
            else
            {
                (_ownerDocuments ??= [])[current] = document;
                // The page's id index answers for the page only; an entry left pointing here
                // would send every page lookup of that id to the full scan.
                if (node.GetAttribute("id") is { } idValue
                    && _idIndex.TryGetValue(idValue, out var indexed)
                    && indexed == current)
                {
                    _idIndex.Remove(idValue);
                }
            }

            if (_shadowRootsByHost.TryGetValue(current, out var hosted))
            {
                stack.Push(hosted);
            }

            for (var child = node.LastChild; child is { } childId; child = Slot(childId)?.PrevSibling)
            {
                stack.Push(childId);
            }
        }
    }

    /// <summary>The insertion hook: a node inserted under another document's node joins that document.</summary>
    private void AdoptOnInsert(NodeId parentId, NodeId childId)
    {
        var parentDocument = NodeDocumentOf(parentId);
        if (NodeDocumentOf(childId) != parentDocument)
        {
            AdoptSubtree(childId, parentDocument);
        }
    }

    /// <summary>Drop the document bookkeeping for a slot being freed.</summary>
    private void ForgetDocumentState(NodeId id)
    {
        _documents?.Remove(id);
        _ownerDocuments?.Remove(id);
    }

    /// <summary>A collection keeps a node and its node document together (DomTree.Gc.cs).</summary>
    private void UnionOwnerDocuments(DomCollection collection, int slots)
    {
        if (_ownerDocuments is not { Count: > 0 } owners)
        {
            return;
        }

        foreach (var (node, document) in owners)
        {
            if (node.Index < slots && document.Index < slots && Slot(node) is not null && Slot(document) is not null)
            {
                collection.Union(node.Index, document.Index);
            }
        }
    }
}

/// <summary>What the tree records about a secondary document.</summary>
internal sealed record SecondaryDocument(string ContentType, bool Quirks);
