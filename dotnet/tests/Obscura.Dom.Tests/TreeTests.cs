using Obscura.Dom;

namespace Obscura.Dom.Tests;

/// <summary>Port of the <c>#[cfg(test)] mod tests</c> block in crates/obscura-dom/src/tree.rs.</summary>
public class TreeTests
{
    private static void AssertNodes(IReadOnlyList<NodeId> expected, IReadOnlyList<NodeId>? actual) =>
        Assert.Equal(expected, actual ?? []);

    private static NodeId Element(DomTree tree, string local) =>
        tree.NewNode(NodeData.Element(QualName.Html(local)));

    private static NodeId ElementWithId(DomTree tree, string local, string id) =>
        tree.NewNode(NodeData.Element(
            QualName.Html(local),
            [new Attribute(QualName.Attr("id"), id)]));

    [Fact]
    public void TestNewTreeHasDocument()
    {
        var tree = new DomTree();
        Assert.Equal(1, tree.Count);
        var node = tree.GetNode(tree.Document)!;
        Assert.True(node.IsDocument);
    }

    [Fact]
    public void RemoveOutOfRangeIdIsANoop()
    {
        var tree = new DomTree();
        // Direct indexing into the arena threw out-of-bounds for an id past the end of the slot
        // vector; it must be a no-op instead.
        tree.Remove(NodeId.New(9999));
        Assert.Equal(1, tree.Count);
    }

    [Fact]
    public void RemoveTwiceDoesNotAliasSlots()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var a = tree.NewNode(NodeData.Text("a"));
        tree.AppendChild(doc, a);
        tree.Remove(a);
        // Removing the already-freed node again must not push its slot onto the free list a second
        // time, or two later allocations alias one slot.
        tree.Remove(a);
        var x = tree.NewNode(NodeData.Text("x"));
        var y = tree.NewNode(NodeData.Text("y"));
        Assert.True(x != y, "double-free aliased two live nodes onto the same slot");
    }

    [Fact]
    public void NativeShadowRootKeepsLightAndShadowTreeScopesSeparate()
    {
        var tree = new DomTree();
        var document = tree.Document;
        var host = Element(tree, "x-card");
        var light = Element(tree, "span");
        tree.AppendChild(document, host);
        tree.AppendChild(host, light);

        var root = tree.AttachShadowRoot(host, ShadowRootMode.Closed);
        var shadow = Element(tree, "button");
        tree.AppendChild(root, shadow);

        Assert.Equal(new ShadowRoot(root, host, ShadowRootMode.Closed), tree.ShadowRootInfo(root));
        Assert.True(tree.IsShadowRoot(root));
        Assert.Equal(root, tree.ShadowRootOf(host));
        Assert.Null(tree.GetNode(root)!.Parent);
        AssertNodes([light], tree.Children(host));
        AssertNodes([shadow], tree.ShadowChildren(host));
        AssertNodes([shadow], tree.ShadowDescendants(host));

        var documentNodes = tree.Descendants(document);
        Assert.DoesNotContain(root, documentNodes);
        Assert.DoesNotContain(shadow, documentNodes);
        Assert.Equal(document, tree.TreeScopeRoot(light));
        Assert.Equal(root, tree.TreeScopeRoot(root));
        Assert.Equal(root, tree.TreeScopeRoot(shadow));
        Assert.Null(tree.ContainingShadowRoot(light));
        Assert.Equal(root, tree.ContainingShadowRoot(shadow));
        Assert.Equal(document, tree.ShadowIncludingRoot(shadow));
        Assert.Equal(
            AttachShadowError.HostAlreadyHasShadowRoot,
            tree.TryAttachShadowRoot(host, ShadowRootMode.Open, out _));
    }

    [Fact]
    public void SlotAssignmentUsesExactNamesFirstSlotAndFallbackChildren()
    {
        var tree = new DomTree();
        var host = Element(tree, "x-card");
        tree.AppendChild(tree.Document, host);
        var named = Element(tree, "span");
        tree.GetNode(named)!.SetAttribute("slot", "title");
        var defaultText = tree.NewNode(NodeData.Text("default"));
        tree.AppendChild(host, named);
        tree.AppendChild(host, defaultText);

        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var firstNamed = Element(tree, "slot");
        tree.GetNode(firstNamed)!.SetAttribute("name", "title");
        var duplicateNamed = Element(tree, "slot");
        tree.GetNode(duplicateNamed)!.SetAttribute("name", "title");
        var fallback = Element(tree, "b");
        tree.AppendChild(duplicateNamed, fallback);
        var defaultSlot = Element(tree, "slot");
        tree.AppendChild(root, firstNamed);
        tree.AppendChild(root, duplicateNamed);
        tree.AppendChild(root, defaultSlot);

        Assert.Equal(firstNamed, tree.AssignedSlot(named));
        Assert.Equal(defaultSlot, tree.AssignedSlot(defaultText));
        AssertNodes([named], tree.SlotRenderedChildren(firstNamed));
        AssertNodes([fallback], tree.SlotRenderedChildren(duplicateNamed));
        AssertNodes([defaultText], tree.SlotRenderedChildren(defaultSlot));
    }

    [Fact]
    public void DocumentIdLookupNeverExposesAShadowDescendant()
    {
        var tree = new DomTree();
        var document = tree.Document;
        var host = Element(tree, "x-card");
        tree.AppendChild(document, host);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);

        // The shadow element is created first, so it owns the best-effort global id-index entry.
        // Public document lookup still has to recover the light-tree match rather than leak across
        // the tree scope.
        var shadowMatch = ElementWithId(tree, "span", "shared");
        tree.AppendChild(root, shadowMatch);
        var lightMatch = ElementWithId(tree, "span", "shared");
        tree.AppendChild(host, lightMatch);

        Assert.Equal(lightMatch, tree.GetElementById("shared"));
        Assert.Equal(lightMatch, tree.QuerySelectorFrom(document, "#shared"));
        Assert.Equal(shadowMatch, tree.QuerySelectorFrom(root, "#shared"));
    }

    [Fact]
    public void ShadowHostEdgesParticipateInCycleRejection()
    {
        var tree = new DomTree();
        var document = tree.Document;
        var host = Element(tree, "x-card");
        tree.AppendChild(document, host);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadowChild = Element(tree, "span");
        tree.AppendChild(root, shadowChild);

        // Root nodes cannot become ordinary children.
        tree.AppendChild(host, root);
        Assert.Null(tree.GetNode(root)!.Parent);
        Assert.Empty(tree.Children(host));

        // A host is a host-including ancestor of every node in its shadow tree, even when it has no
        // light children.
        tree.AppendChild(root, host);
        tree.InsertBefore(shadowChild, host);
        Assert.Equal(document, tree.GetNode(host)!.Parent);
        AssertNodes([shadowChild], tree.Children(root));
    }

    [Fact]
    public void ConnectivityPropagatesAcrossLightAndShadowSubtrees()
    {
        var tree = new DomTree();
        var host = Element(tree, "x-card");
        var light = Element(tree, "span");
        tree.AppendChild(host, light);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadow = Element(tree, "button");
        tree.AppendChild(root, shadow);
        foreach (var node in new[] { host, light, root, shadow })
        {
            Assert.False(tree.IsConnected(node));
        }

        tree.AppendChild(tree.Document, host);
        foreach (var node in new[] { host, light, root, shadow })
        {
            Assert.True(tree.IsConnected(node));
        }

        tree.Detach(host);
        foreach (var node in new[] { host, light, root, shadow })
        {
            Assert.False(tree.IsConnected(node));
        }
    }

    [Fact]
    public void ConnectivitySurvivesConnectedMovesAndRootDetachAttempts()
    {
        var tree = new DomTree();
        var document = tree.Document;
        var left = Element(tree, "section");
        var right = Element(tree, "section");
        var host = Element(tree, "x-card");
        var light = Element(tree, "span");
        tree.AppendChild(document, left);
        tree.AppendChild(document, right);
        tree.AppendChild(host, light);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadow = Element(tree, "button");
        tree.AppendChild(root, shadow);
        tree.AppendChild(left, host);

        tree.AppendChild(right, host);
        Assert.Empty(tree.Children(left));
        AssertNodes([host], tree.Children(right));
        foreach (var node in new[] { document, left, right, host, light, root, shadow })
        {
            Assert.True(tree.IsConnected(node));
        }

        // Neither root participates in the ordinary child list, so a generic detach must not
        // corrupt the cached connectivity invariant.
        tree.Detach(document);
        tree.Detach(root);
        foreach (var node in new[] { document, left, right, host, light, root, shadow })
        {
            Assert.True(tree.IsConnected(node));
        }
    }

    [Fact]
    public void FreeingAHostReclaimsShadowNodesAndRegistryEntries()
    {
        var tree = new DomTree();
        var host = Element(tree, "x-card");
        tree.AppendChild(tree.Document, host);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadowHost = Element(tree, "nested-card");
        tree.AppendChild(root, shadowHost);
        var nestedRoot = tree.AttachShadowRoot(shadowHost, ShadowRootMode.Closed);
        var nestedChild = Element(tree, "span");
        tree.AppendChild(nestedRoot, nestedChild);

        Assert.Equal(6, tree.Count);
        tree.Remove(host);
        Assert.Equal(1, tree.Count);
        foreach (var removed in new[] { host, root, shadowHost, nestedRoot, nestedChild })
        {
            Assert.Null(tree.GetNode(removed));
            Assert.False(tree.IsShadowRoot(removed));
        }

        // Reusing freed slots must not resurrect either registry direction.
        var replacement = Element(tree, "div");
        Assert.Null(tree.ShadowRootOf(replacement));
        Assert.Null(tree.ShadowRootInfo(replacement));
    }

    [Fact]
    public void CloningAHostOmitsItsShadowTreeAndARootIsNotClonable()
    {
        var tree = new DomTree();
        var host = Element(tree, "x-card");
        var light = Element(tree, "span");
        tree.AppendChild(host, light);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        tree.AppendChild(root, Element(tree, "button"));

        Assert.Null(tree.CloneNode(root, true));
        var clone = tree.CloneNode(host, true);
        Assert.NotNull(clone);
        Assert.Null(tree.ShadowRootOf(clone.Value));
        Assert.Single(tree.Children(clone.Value));
    }

    [Fact]
    public void TestAppendChild()
    {
        var tree = new DomTree();
        var child = tree.NewNode(NodeData.Text("hello"));
        var doc = tree.Document;
        Assert.False(tree.IsConnected(child));
        tree.AppendChild(doc, child);

        Assert.Equal(2, tree.Count);
        var docNode = tree.GetNode(doc)!;
        Assert.Equal(child, docNode.FirstChild);
        Assert.Equal(child, docNode.LastChild);

        var childNode = tree.GetNode(child)!;
        Assert.Equal(doc, childNode.Parent);
        Assert.True(tree.IsConnected(child));
    }

    [Fact]
    public void TestMultipleChildren()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var c1 = tree.NewNode(NodeData.Text("a"));
        var c2 = tree.NewNode(NodeData.Text("b"));
        var c3 = tree.NewNode(NodeData.Text("c"));
        tree.AppendChild(doc, c1);
        tree.AppendChild(doc, c2);
        tree.AppendChild(doc, c3);

        AssertNodes([c1, c2, c3], tree.Children(doc));
    }

    [Fact]
    public void TestDetach()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var c1 = tree.NewNode(NodeData.Text("a"));
        var c2 = tree.NewNode(NodeData.Text("b"));
        tree.AppendChild(doc, c1);
        tree.AppendChild(doc, c2);

        tree.Detach(c1);
        AssertNodes([c2], tree.Children(doc));
        Assert.False(tree.IsConnected(c1));
        Assert.True(tree.IsConnected(c2));
    }

    [Fact]
    public void TestInsertBefore()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var c1 = tree.NewNode(NodeData.Text("a"));
        var c2 = tree.NewNode(NodeData.Text("b"));
        var c3 = tree.NewNode(NodeData.Text("c"));
        tree.AppendChild(doc, c1);
        tree.AppendChild(doc, c3);
        tree.InsertBefore(c3, c2);

        AssertNodes([c1, c2, c3], tree.Children(doc));
    }

    [Fact]
    public void TestTextContent()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var div = tree.NewNode(NodeData.Element(QualName.Html("div")));
        tree.AppendChild(doc, div);

        var t1 = tree.NewNode(NodeData.Text("Hello "));
        var t2 = tree.NewNode(NodeData.Text("World"));
        tree.AppendChild(div, t1);
        tree.AppendChild(div, t2);

        Assert.Equal("Hello World", tree.TextContent(div));
    }

    [Fact]
    public void TestGetElementById()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var div = tree.NewNode(NodeData.Element(
            QualName.Html("div"),
            [new Attribute(QualName.Attr("id"), "main")]));
        tree.AppendChild(doc, div);

        Assert.Equal(div, tree.GetElementById("main"));
        Assert.Null(tree.GetElementById("nonexistent"));
    }

    [Fact]
    public void TestReparentCycleIsRejected()
    {
        // document -> html -> body -> div. Moving an ancestor under one of its own descendants
        // would make the parent/child graph cyclic and hang every later Descendants() walk. Both
        // AppendChild and InsertBefore must reject it as a no-op (DOM HierarchyRequestError).
        var tree = new DomTree();
        var doc = tree.Document;
        var html = Element(tree, "html");
        var body = Element(tree, "body");
        var div = Element(tree, "div");
        tree.AppendChild(doc, html);
        tree.AppendChild(html, body);
        tree.AppendChild(body, div);

        var before = tree.Descendants(doc).Count;
        Assert.Equal(3, before);

        // AppendChild: html is an ancestor of div -> must be a no-op, no cycle.
        tree.AppendChild(div, html);
        Assert.Equal(before, tree.Descendants(doc).Count);
        Assert.Empty(tree.Descendants(div));

        // InsertBefore: html is an ancestor of body (div's parent) -> no-op.
        tree.InsertBefore(div, html);
        Assert.Equal(before, tree.Descendants(doc).Count);

        // Self-append / self-insert remain no-ops (existing guards).
        tree.AppendChild(div, div);
        tree.InsertBefore(div, div);
        Assert.Equal(before, tree.Descendants(doc).Count);
    }

    [Fact]
    public void TestInsertBeforePreviousSiblingNoCycle()
    {
        // Inserting a node before its own immediate previous sibling is a no-op reorder that
        // frameworks do constantly. It used to splice next_sibling = self via a prev id captured
        // before detach, hanging every later sibling walk (this hung ebay.com). The result must
        // stay a well-formed [a, b] with no cycle.
        var tree = new DomTree();
        var doc = tree.Document;
        var parent = Element(tree, "div");
        var a = Element(tree, "a");
        var b = Element(tree, "b");
        tree.AppendChild(doc, parent);
        tree.AppendChild(parent, a);
        tree.AppendChild(parent, b); // parent -> [a, b]

        // a is already b's previous sibling; this reorder must not create a cycle.
        tree.InsertBefore(b, a);

        AssertNodes([a, b], tree.Descendants(parent));
    }

    [Fact]
    public void TestAppendTextMerges()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        tree.AppendText(doc, "Hello ");
        tree.AppendText(doc, "World");

        Assert.Single(tree.Children(doc));
        Assert.Equal("Hello World", tree.TextContent(doc));
    }

    [Fact]
    public void TestRemoveSubtree()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var div = tree.NewNode(NodeData.Element(QualName.Html("div")));
        tree.AppendChild(doc, div);
        var text = tree.NewNode(NodeData.Text("hi"));
        tree.AppendChild(div, text);

        Assert.Equal(3, tree.Count);
        tree.Remove(div);
        Assert.Equal(1, tree.Count);
    }

    [Fact]
    public void TestNextInSubtreeFollowsDocumentOrderAndStaysWithinRoot()
    {
        var tree = new DomTree();
        var root = tree.NewNode(NodeData.Text("root"));
        var first = tree.NewNode(NodeData.Text("first"));
        var nested = tree.NewNode(NodeData.Text("nested"));
        var second = tree.NewNode(NodeData.Text("second"));
        tree.AppendChild(tree.Document, root);
        tree.AppendChild(root, first);
        tree.AppendChild(first, nested);
        tree.AppendChild(root, second);

        Assert.Equal(first, tree.NextInSubtree(root, root));
        Assert.Equal(nested, tree.NextInSubtree(root, first));
        Assert.Equal(second, tree.NextInSubtree(root, nested));
        Assert.Null(tree.NextInSubtree(root, second));
    }

    [Fact]
    public void TestNextAfterSubtreeSkipsDescendants()
    {
        // Same shape as above: root > [first > nested, second]. Stepping past `first` must land on
        // `second`, not descend into `nested`: that is what NodeFilter.FILTER_REJECT needs.
        var tree = new DomTree();
        var root = tree.NewNode(NodeData.Text("root"));
        var first = tree.NewNode(NodeData.Text("first"));
        var nested = tree.NewNode(NodeData.Text("nested"));
        var second = tree.NewNode(NodeData.Text("second"));
        tree.AppendChild(tree.Document, root);
        tree.AppendChild(root, first);
        tree.AppendChild(first, nested);
        tree.AppendChild(root, second);

        Assert.Equal(second, tree.NextAfterSubtree(root, first));
        // A leaf behaves identically to NextInSubtree: there is no subtree.
        Assert.Equal(second, tree.NextAfterSubtree(root, nested));
        Assert.Null(tree.NextAfterSubtree(root, second));
        // Rejecting the root itself exhausts the walk rather than escaping it.
        Assert.Null(tree.NextAfterSubtree(root, root));
    }

    [Fact]
    public void TestPrevInSubtreeReversesDocumentOrder()
    {
        // root > [first > nested, second]; document order is root, first, nested, second, so the
        // reverse walk must retrace it exactly.
        var tree = new DomTree();
        var root = tree.NewNode(NodeData.Text("root"));
        var first = tree.NewNode(NodeData.Text("first"));
        var nested = tree.NewNode(NodeData.Text("nested"));
        var second = tree.NewNode(NodeData.Text("second"));
        tree.AppendChild(tree.Document, root);
        tree.AppendChild(root, first);
        tree.AppendChild(first, nested);
        tree.AppendChild(root, second);

        // Previous sibling's deepest last descendant, not the sibling itself.
        Assert.Equal(nested, tree.PrevInSubtree(root, second));
        Assert.Equal(first, tree.PrevInSubtree(root, nested));
        // No previous sibling: the parent precedes it, and root is returnable.
        Assert.Equal(root, tree.PrevInSubtree(root, first));
        // Root has no predecessor inside its own subtree.
        Assert.Null(tree.PrevInSubtree(root, root));
    }

    // Builds a chain of `depth` nested <div> elements under the document and returns
    // (outermost_div, innermost_div). Depth this large overflows a recursive tree walk and aborts
    // the process, so it guards the iterative serialize / TextContent / import paths against
    // regressing to recursion.
    private static (NodeId Root, NodeId Leaf) BuildDeepChain(DomTree tree, int depth)
    {
        var root = tree.NewNode(NodeData.Element(QualName.Html("div")));
        tree.AppendChild(tree.Document, root);
        var current = root;
        for (var i = 1; i < depth; i++)
        {
            var next = tree.NewNode(NodeData.Element(QualName.Html("div")));
            tree.AppendChild(current, next);
            current = next;
        }

        return (root, current);
    }

    [Fact]
    public void TestOuterHtmlDeeplyNestedDoesNotOverflow()
    {
        var tree = new DomTree();
        var (root, leaf) = BuildDeepChain(tree, 100_000);
        var marker = tree.NewNode(NodeData.Text("leaf"));
        tree.AppendChild(leaf, marker);

        var html = tree.OuterHtml(root);
        Assert.StartsWith("<div>", html, StringComparison.Ordinal);
        Assert.Contains("leaf", html, StringComparison.Ordinal);
        Assert.EndsWith("</div>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TestTextContentDeeplyNestedDoesNotOverflow()
    {
        var tree = new DomTree();
        var (root, leaf) = BuildDeepChain(tree, 100_000);
        var marker = tree.NewNode(NodeData.Text("deep"));
        tree.AppendChild(leaf, marker);

        Assert.Equal("deep", tree.TextContent(root));
    }

    [Fact]
    public void TestImportDeeplyNestedDoesNotOverflow()
    {
        var source = new DomTree();
        BuildDeepChain(source, 100_000);

        var dest = new DomTree();
        var destDoc = dest.Document;
        dest.ImportChildrenFrom(destDoc, source, source.Document);

        Assert.True(dest.Count >= 100_000);
    }

    /// <summary>
    /// SEC-008 / #582: Children() must terminate on a corrupted cyclic sibling chain, the same way
    /// Descendants() already does. The public mutation API cannot create such a cycle, so we forge
    /// one by writing the node arena directly, then assert the walk stays bounded instead of
    /// hanging forever.
    /// </summary>
    [Fact]
    public void ChildrenWalkIsBoundedOnCorruptedSiblingCycle()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var root = Element(tree, "root");
        var a = Element(tree, "a");
        var b = Element(tree, "b");
        tree.AppendChild(doc, root);
        tree.AppendChild(root, a);
        tree.AppendChild(root, b);

        // Forge a sibling cycle a -> a that AppendChild never produces.
        tree.GetNode(a)!.NextSibling = a;

        var nodeCount = tree.NodeSlotCount;
        var kids = tree.Children(root);
        Assert.True(
            kids.Count <= nodeCount + 1,
            $"Children() must stay bounded on a cyclic sibling chain, got {kids.Count}");
    }

    /// <summary>SEC-008 / #582: the Ancestors() companion to the Children() cycle test.</summary>
    [Fact]
    public void AncestorsWalkIsBoundedOnCorruptedParentCycle()
    {
        var tree = new DomTree();
        var doc = tree.Document;
        var root = Element(tree, "root");
        var child = Element(tree, "child");
        tree.AppendChild(doc, root);
        tree.AppendChild(root, child);

        // Forge a parent cycle child -> child.
        tree.GetNode(child)!.Parent = child;

        var nodeCount = tree.NodeSlotCount;
        var ancestors = tree.Ancestors(child);
        Assert.True(
            ancestors.Count <= nodeCount + 1,
            $"Ancestors() must stay bounded on a cyclic parent chain, got {ancestors.Count}");
    }
}
