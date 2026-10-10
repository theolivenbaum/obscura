using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// Manual slot assignment, shadow root options and slot change tracking (DomTree.Slots.cs), port
/// additions behind HTMLSlotElement.assign() and slotchange. Expectations follow Chromium 141.
/// </summary>
public class ManualSlotAssignmentTests
{
    private static NodeId Element(DomTree tree, string local) =>
        tree.NewNode(NodeData.Element(QualName.Html(local)));

    private static (DomTree Tree, NodeId Host, NodeId Root, NodeId A, NodeId B, NodeId Text) ManualHost()
    {
        var tree = new DomTree();
        var host = Element(tree, "div");
        tree.AppendChild(tree.Document, host);
        var a = Element(tree, "a");
        var b = Element(tree, "b");
        tree.GetNode(b)!.SetAttribute("slot", "x");
        var text = tree.NewNode(NodeData.Text("t"));
        tree.AppendChild(host, a);
        tree.AppendChild(host, b);
        tree.AppendChild(host, text);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        Assert.True(tree.SetShadowRootOptions(root, manualSlotAssignment: true, delegatesFocus: false, clonable: false, serializable: false));
        return (tree, host, root, a, b, text);
    }

    [Fact]
    public void ManualRootsIgnoreNamesAndFollowAssignOrder()
    {
        var (tree, _, root, a, b, text) = ManualHost();
        var m1 = Element(tree, "slot");
        tree.GetNode(m1)!.SetAttribute("name", "x");
        var m2 = Element(tree, "slot");
        tree.AppendChild(root, m1);
        tree.AppendChild(root, m2);

        Assert.Empty(tree.AssignedNodes(m1)!);
        Assert.Null(tree.AssignedSlot(b));

        Assert.True(tree.AssignSlottablesManually(m2, [b, a, text, a]));
        Assert.Equal([b, a, text], tree.AssignedNodes(m2));
        Assert.Equal(m2, tree.AssignedSlot(a));

        // A node assigned elsewhere leaves its old slot.
        tree.AssignSlottablesManually(m1, [a]);
        Assert.Equal([a], tree.AssignedNodes(m1));
        Assert.Equal([b, text], tree.AssignedNodes(m2));
        Assert.Equal([b, text], tree.SlotRenderedChildren(m2));
    }

    [Fact]
    public void AssignmentOutlivesRemovalFromTheHost()
    {
        var (tree, host, root, _, b, _) = ManualHost();
        var slot = Element(tree, "slot");
        tree.AppendChild(root, slot);
        tree.AssignSlottablesManually(slot, [tree.Document, b]);
        Assert.Equal([b], tree.AssignedNodes(slot));
        tree.RemoveChild(b);
        Assert.Empty(tree.AssignedNodes(slot)!);
        tree.AppendChild(host, b);
        Assert.Equal([b], tree.AssignedNodes(slot));
    }

    [Fact]
    public void SlotChangesCompareWithTheStateBeforeTheFirstMark()
    {
        var tree = new DomTree();
        var host = Element(tree, "div");
        tree.AppendChild(tree.Document, host);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var named = Element(tree, "slot");
        tree.GetNode(named)!.SetAttribute("name", "a");
        var fallback = Element(tree, "slot");
        tree.AppendChild(root, named);
        tree.AppendChild(root, fallback);
        Assert.Empty(tree.TakeSlotChanges());

        // A child for the default slot: only that slot changed.
        tree.MarkHostSlotsDirty(host);
        var p = Element(tree, "p");
        tree.AppendChild(host, p);
        Assert.Equal([fallback], tree.TakeSlotChanges());
        Assert.Empty(tree.TakeSlotChanges());

        // A change undone before the check reports nothing.
        tree.MarkHostSlotsDirty(host);
        tree.GetNode(p)!.SetAttribute("slot", "a");
        tree.GetNode(p)!.RemoveAttribute("slot");
        Assert.Empty(tree.TakeSlotChanges());

        // A removed slot that had nodes is reported first.
        tree.MarkHostSlotsDirty(host);
        tree.GetNode(p)!.SetAttribute("slot", "a");
        Assert.Equal([named, fallback], tree.TakeSlotChanges());
        tree.MarkContainingSlotsDirty(named);
        tree.RemoveChild(named);
        Assert.Equal([named], tree.TakeSlotChanges());
    }

    [Fact]
    public void MayCarrySlotLooksForSlotsInTheMovedSubtree()
    {
        var tree = new DomTree();
        var host = Element(tree, "div");
        tree.AppendChild(tree.Document, host);
        tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var wrapper = Element(tree, "div");
        var plain = Element(tree, "span");
        tree.AppendChild(wrapper, plain);
        Assert.False(tree.MayCarrySlot(wrapper));
        tree.AppendChild(plain, Element(tree, "slot"));
        Assert.True(tree.MayCarrySlot(wrapper));
    }
}
