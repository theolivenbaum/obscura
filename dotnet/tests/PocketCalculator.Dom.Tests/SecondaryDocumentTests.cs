using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// Documents other than the page's (DomTree.Documents.cs), the XML parser (XmlParsing.cs) and
/// the XML serializer (DomTree.SerializeXml.cs). Port additions: the Rust engine has one
/// document per tree, no XML parser and HTML serialization only.
/// </summary>
public sealed class SecondaryDocumentTests
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

    private sealed class Holder(params NodeId[] held) : IDomGcParticipant
    {
        public void MarkRoots(DomCollection collection)
        {
            foreach (var id in held)
            {
                collection.Keep(id);
            }
        }

        public void OnFreed(DomTree tree, IReadOnlyList<NodeId> freed)
        {
        }
    }

    [Fact]
    public void ASecondaryDocumentIsNeverConnectedAndOwnsWhatIsInsertedIntoIt()
    {
        var tree = HtmlParsing.ParseHtml("<html><body><p id=x>page</p></body></html>");
        var page = tree.QuerySelector("#x")!.Value;
        var doc = tree.CreateDocument("text/html");
        var html = Element(tree, "html");
        var div = Element(tree, "div", ("id", "x"));

        Assert.True(tree.IsSecondaryDocument(doc));
        Assert.False(tree.IsSecondaryDocument(tree.Document));
        Assert.Null(tree.OwnerDocumentOf(doc));
        Assert.Equal(tree.Document, tree.OwnerDocumentOf(div));

        tree.AppendChild(doc, html);
        tree.AppendChild(html, div);
        Assert.Equal(doc, tree.OwnerDocumentOf(div));
        Assert.Equal(doc, tree.OwnerDocumentOf(html));
        Assert.False(tree.IsConnected(div));

        // The page's id lookups and queries never reach it.
        Assert.Equal(page, tree.GetElementById("x"));
        Assert.Equal(page, tree.QuerySelector("#x"));
        Assert.Single(tree.QuerySelectorAll("[id=x]"));

        // Moving a page node in adopts it; moving it back adopts it back.
        tree.AppendChild(html, page);
        Assert.Equal(doc, tree.OwnerDocumentOf(page));
        Assert.False(tree.IsConnected(page));
        var body = tree.QuerySelector("body")!.Value;
        tree.AppendChild(body, page);
        Assert.Equal(tree.Document, tree.OwnerDocumentOf(page));
        Assert.True(tree.IsConnected(page));

        // A detached node keeps its document; a document cannot be inserted anywhere.
        tree.Detach(div);
        Assert.Equal(doc, tree.OwnerDocumentOf(div));
        tree.AppendChild(body, doc);
        Assert.Null(tree.GetNode(doc)!.Parent);
    }

    [Fact]
    public void AdoptSubtreeCoversShadowTreesAndCloneKeepsTheDocument()
    {
        var tree = new DomTree();
        var doc = tree.CreateDocument("text/html");
        var host = Element(tree, "div");
        var inner = Element(tree, "span");
        tree.AppendChild(host, inner);
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadowChild = Element(tree, "b");
        tree.AppendChild(root, shadowChild);

        tree.AdoptSubtree(host, doc);
        Assert.Equal(doc, tree.OwnerDocumentOf(inner));
        Assert.Equal(doc, tree.OwnerDocumentOf(root));
        Assert.Equal(doc, tree.OwnerDocumentOf(shadowChild));

        var clone = tree.CloneNode(host, deep: true)!.Value;
        Assert.Equal(doc, tree.OwnerDocumentOf(clone));
        Assert.Equal(doc, tree.OwnerDocumentOf(tree.Children(clone)[0]));

        // A document's clone is a new document of the same type.
        tree.AppendChild(doc, Element(tree, "html"));
        var docClone = tree.CloneNode(doc, deep: true)!.Value;
        Assert.True(tree.IsSecondaryDocument(docClone));
        Assert.Equal("text/html", tree.DocumentContentType(docClone));
        Assert.Equal(docClone, tree.OwnerDocumentOf(tree.Children(docClone)[0]));

        tree.AdoptSubtree(host, tree.Document);
        Assert.Equal(tree.Document, tree.OwnerDocumentOf(shadowChild));
    }

    [Fact]
    public void ANodeKeepsItsDocumentAliveAndTheyAreFreedTogether()
    {
        var tree = new DomTree();
        var doc = tree.CreateDocument("application/xml");
        var element = Element(tree, "r");
        tree.AdoptSubtree(element, doc);

        tree.AddGcParticipant(new Holder(element));
        Assert.Equal(0, tree.CollectGarbage().FreedNodes);
        Assert.True(tree.IsSecondaryDocument(doc));

        var bare = new DomTree();
        var lone = bare.CreateDocument("text/html");
        var child = Element(bare, "html");
        bare.AppendChild(lone, child);
        Assert.Equal(2, bare.CollectGarbage().FreedNodes);
        Assert.False(bare.IsSecondaryDocument(lone));
    }

    [Fact]
    public void XmlParsingKeepsNamesNamespacesAndNodeKinds()
    {
        var tree = XmlParsing.Parse(
            "<?xml version=\"1.0\"?><!DOCTYPE r><?pi data?><r xmlns:s=\"urn:s\"><s:k a=\"1\">t<![CDATA[<c>]]><!--cm--></s:k><B/>tail</r>",
            DomTree.DefaultContentByteBudget);
        var children = tree.Children(tree.Document);
        Assert.Equal(3, children.Count);
        Assert.IsType<DoctypeData>(tree.GetNode(children[0])!.Data);
        Assert.Equal("pi", ((ProcessingInstructionData)tree.GetNode(children[1])!.Data).Target);
        var root = children[2];
        var k = tree.Children(root)[0];
        var name = tree.GetNode(k)!.ElementName!.Value;
        Assert.Equal(("s", "urn:s", "k"), (name.Prefix, name.Ns, name.Local));
        // CDATA joins the text before it (the tree has no CDATA node kind).
        Assert.Equal("t<c>", tree.TextContent(tree.Children(k)[0]));
        Assert.Equal("B", tree.GetNode(tree.Children(root)[1])!.ElementName!.Value.Local);
        Assert.Equal("<r xmlns:s=\"urn:s\"><s:k a=\"1\">t&lt;c&gt;<!--cm--></s:k><B/>tail</r>", tree.OuterXml(root));
    }

    [Fact]
    public void XmlParseErrorsReportAParserErrorAsChromiumDoes()
    {
        var tree = XmlParsing.Parse("<a><b></a>", DomTree.DefaultContentByteBudget);
        var root = tree.Children(tree.Document)[0];
        Assert.Equal("a", tree.GetNode(root)!.ElementName!.Value.Local);
        var error = tree.Children(root)[0];
        Assert.Equal(new QualName(null, Namespaces.Html, "parsererror"), tree.GetNode(error)!.ElementName);
        Assert.StartsWith("This page contains the following errors:", tree.TextContent(error), StringComparison.Ordinal);

        var junk = XmlParsing.Parse("junk", DomTree.DefaultContentByteBudget);
        Assert.StartsWith("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><parsererror ", junk.OuterXml(junk.Document), StringComparison.Ordinal);
    }

    [Fact]
    public void XmlParsingNeverFetchesExternalEntities()
    {
        var tree = XmlParsing.Parse(
            "<!DOCTYPE r [<!ENTITY ext SYSTEM \"file:///etc/passwd\">]><r>&ext;</r>",
            DomTree.DefaultContentByteBudget);
        Assert.DoesNotContain("root:", tree.TextContent(tree.Document), StringComparison.Ordinal);
    }

    [Fact]
    public void XmlSerializationOfHtmlMatchesChromium()
    {
        // Chromium 141: new XMLSerializer().serializeToString(...) of these HTML nodes.
        var tree = HtmlParsing.ParseHtml("<!doctype html><html><head><title>t</title></head><body><br><p a=\"&quot;<\">x &amp; y</p></body></html>");
        Assert.Equal("<br xmlns=\"http://www.w3.org/1999/xhtml\" />", tree.OuterXml(tree.QuerySelector("br")!.Value));
        Assert.Equal(
            "<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>t</title></head><body><br /><p a=\"&quot;&lt;\">x &amp; y</p></body></html>",
            tree.OuterXml(tree.Document));
    }
}
