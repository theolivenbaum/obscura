using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// The DOM garbage collector (DomTree.Gc.cs). Rust never frees a detached node, so a page
/// that replaces content in a loop grows until the M7 budget refuses it.
/// </summary>
public sealed class DomGcTests
{
    private static NodeId Element(DomTree tree, string name, params (string Name, string Value)[] attrs)
    {
        var list = new List<Attribute>();
        foreach (var (n, v) in attrs)
        {
            list.Add(new Attribute(QualName.Attr(n), v));
        }

        return tree.NewNode(NodeData.Element(QualName.Html(name), list));
    }

    private static NodeId Body(DomTree tree)
    {
        var body = Element(tree, "body");
        tree.AppendChild(tree.Document, body);
        return body;
    }

    private static void FillRows(DomTree tree, NodeId parent, int rows)
    {
        for (var i = 0; i < rows; i++)
        {
            var tr = Element(tree, "tr");
            var td = Element(tree, "td");
            tree.AppendChild(td, tree.NewNode(NodeData.Text("cell " + i)));
            tree.AppendChild(tr, td);
            tree.AppendChild(parent, tr);
        }
    }

    private static void ClearChildren(DomTree tree, NodeId parent)
    {
        foreach (var child in tree.Children(parent))
        {
            tree.RemoveChild(child);
        }
    }

    private sealed class Holder(params NodeId[] held) : IDomGcParticipant
    {
        public List<NodeId> Held { get; } = [.. held];

        public List<NodeId> Freed { get; } = [];

        public void MarkRoots(DomCollection collection)
        {
            foreach (var id in Held)
            {
                collection.Keep(id);
            }
        }

        public void OnFreed(DomTree tree, IReadOnlyList<NodeId> freed) => Freed.AddRange(freed);
    }

    [Fact]
    public void DetachedSubtreesAreFreedAndCreditTheBudget()
    {
        var tree = new DomTree();
        var body = Body(tree);
        var table = Element(tree, "table");
        tree.AppendChild(body, table);
        long empty = tree.ContentBytes;

        FillRows(tree, table, 200);
        long full = tree.ContentBytes;
        Assert.True(full > empty);

        ClearChildren(tree, table);
        var result = tree.CollectGarbage();

        Assert.Equal(600, result.FreedNodes);
        Assert.Equal(full - empty, result.FreedBytes);
        Assert.Equal(empty, tree.ContentBytes);
        Assert.True(tree.IsConnected(table));
    }

    [Fact]
    public void ChurnUnderASmallBudgetStaysBoundedAndReusesSlots()
    {
        var tree = new DomTree { ContentByteBudget = 1024 * 1024 };
        var body = Body(tree);
        var table = Element(tree, "table");
        tree.AppendChild(body, table);

        int maxSlots = 0;
        for (var i = 0; i < 2000; i++)
        {
            ClearChildren(tree, table);
            if (tree.CollectionDue(inOperation: false))
            {
                tree.CollectGarbage();
            }

            FillRows(tree, table, 200);
            maxSlots = Math.Max(maxSlots, tree.NodeSlotCount);
        }

        // 600 live row nodes; the arena stays within a few collection thresholds of that.
        Assert.True(maxSlots < 4 * DomTree.MinCollectionNodes, $"slots grew to {maxSlots}");
        Assert.True(tree.ContentBytes < 1024 * 1024);
    }

    [Fact]
    public void AReusedSlotNeverAnswersToTheStaleId()
    {
        var tree = new DomTree();
        var body = Body(tree);
        var old = Element(tree, "p", ("id", "gone"));
        tree.AppendChild(body, old);
        tree.RemoveChild(old);
        tree.CollectGarbage();
        Assert.Null(tree.GetNode(old));

        var fresh = Element(tree, "span");
        Assert.Equal(old.Index, fresh.Index);
        Assert.NotEqual(old, fresh);
        Assert.Equal(old.Generation + 1, fresh.Generation);
        Assert.Null(tree.GetNode(old));
        Assert.NotNull(tree.GetNode(fresh));
        Assert.Null(tree.GetElementById("gone"));
    }

    [Fact]
    public void ASlotIsRetiredAtItsLastGeneration()
    {
        var tree = new DomTree();
        NodeId id = Element(tree, "i");
        for (var i = 0; i < NodeId.MaxGeneration; i++)
        {
            tree.Remove(id);
            var next = Element(tree, "i");
            Assert.Equal(id.Index, next.Index);
            id = next;
        }

        Assert.Equal(NodeId.MaxGeneration, id.Generation);
        tree.Remove(id);
        var after = Element(tree, "i");
        Assert.NotEqual(id.Index, after.Index);
        Assert.True(after.Value < (1u << 31));
    }

    [Fact]
    public void ParticipantsAndPinsKeepTheirWholeComponent()
    {
        var tree = new DomTree();
        var body = Body(tree);
        var outer = Element(tree, "div");
        var inner = Element(tree, "span");
        var text = tree.NewNode(NodeData.Text("kept"));
        tree.AppendChild(inner, text);
        tree.AppendChild(outer, inner);
        tree.AppendChild(body, outer);
        tree.RemoveChild(outer);

        // Holding only the innermost text keeps its ancestors: script can walk parentNode.
        var holder = new Holder(text);
        tree.AddGcParticipant(holder);
        Assert.Equal(0, tree.CollectGarbage().FreedNodes);
        Assert.Equal(inner, tree.GetNode(text)!.Parent);

        holder.Held.Clear();
        var owner = new object();
        tree.Pin(owner, outer);
        Assert.Equal(0, tree.CollectGarbage().FreedNodes);

        tree.UnpinAll(owner);
        var result = tree.CollectGarbage();
        Assert.Equal(3, result.FreedNodes);
        Assert.Equal(3, holder.Freed.Count);
        Assert.Null(tree.GetNode(inner));
    }

    [Fact]
    public void ShadowTreesAndTemplateContentsFollowTheirHost()
    {
        var tree = new DomTree();
        var body = Body(tree);
        var host = Element(tree, "x-card");
        tree.AppendChild(body, host);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadowChild = Element(tree, "b");
        tree.AppendChild(root, shadowChild);
        var template = Element(tree, "template");
        tree.AppendChild(body, template);
        var contents = tree.TemplateContents(template)!.Value;
        var inContents = Element(tree, "i");
        tree.AppendChild(contents, inContents);

        // Connected host and template: nothing goes, although the shadow root's own parent is
        // null and the contents document is never connected.
        Assert.Equal(0, tree.CollectGarbage().FreedNodes);
        Assert.NotNull(tree.GetNode(shadowChild));
        Assert.NotNull(tree.GetNode(inContents));

        // A wrapper held for a shadow descendant keeps the detached host.
        tree.RemoveChild(host);
        tree.RemoveChild(template);
        var holder = new Holder(shadowChild, inContents);
        tree.AddGcParticipant(holder);
        Assert.Equal(0, tree.CollectGarbage().FreedNodes);
        Assert.Equal(root, tree.ShadowRootOf(host));

        holder.Held.Clear();
        Assert.Equal(6, tree.CollectGarbage().FreedNodes);
        Assert.False(tree.IsShadowRoot(root));
        Assert.Null(tree.GetNode(contents));
    }

    [Fact]
    public void InOperationCollectionSparesNewAndExposedNodes()
    {
        var tree = new DomTree();
        var body = Body(tree);

        // Created by an op and not yet attached: script may hold the bare id.
        var fresh = Element(tree, "div");

        // Detached after being connected, and handed to script in this epoch.
        var exposed = Element(tree, "p");
        tree.AppendChild(body, exposed);
        tree.RemoveChild(exposed);
        tree.NoteExposed(exposed);

        // Detached after being connected, never handed out: garbage even mid-op.
        var dropped = Element(tree, "p");
        tree.AppendChild(body, dropped);
        tree.RemoveChild(dropped);

        Assert.Equal(1, tree.CollectGarbage(inOperation: true).FreedNodes);
        Assert.Null(tree.GetNode(dropped));
        Assert.NotNull(tree.GetNode(fresh));
        Assert.NotNull(tree.GetNode(exposed));

        // Once script yields the exposure no longer counts; a full collection takes both.
        tree.AdvanceExposureEpoch();
        Assert.Equal(1, tree.CollectGarbage(inOperation: true).FreedNodes);
        Assert.Equal(1, tree.CollectGarbage().FreedNodes);
        Assert.Null(tree.GetNode(fresh));
    }

    [Fact]
    public void ReattachedNodesAreKeptAndIntact()
    {
        var tree = new DomTree();
        var body = Body(tree);
        var list = Element(tree, "ul", ("id", "list"));
        var item = Element(tree, "li");
        tree.AppendChild(item, tree.NewNode(NodeData.Text("one")));
        tree.AppendChild(list, item);
        tree.AppendChild(body, list);
        tree.RemoveChild(list);

        var holder = new Holder(list);
        tree.AddGcParticipant(holder);
        FillRows(tree, body, 5000);
        ClearChildren(tree, body);
        tree.CollectGarbage();

        tree.AppendChild(body, list);
        Assert.Equal("one", tree.TextContent(list));
        Assert.True(tree.IsConnected(item));
    }
}
