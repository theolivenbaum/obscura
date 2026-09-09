using Obscura.Dom;

namespace Obscura.Dom.Tests;

/// <summary>
/// Port of the <c>#[cfg(test)] mod tests</c> block in crates/obscura-dom/src/tree_sink.rs.
/// </summary>
public class TreeSinkTests
{
    private static void AssertNodes(IReadOnlyList<NodeId> expected, IReadOnlyList<NodeId>? actual) =>
        Assert.Equal(expected, actual ?? []);

    private static List<NodeId> ElementChildren(DomTree tree, NodeId parent)
    {
        var result = new List<NodeId>();
        foreach (var child in tree.Children(parent))
        {
            if (tree.GetNode(child)?.IsElement == true)
            {
                result.Add(child);
            }
        }

        return result;
    }

    [Fact]
    public void TestParseSimpleHtml()
    {
        var tree = HtmlParsing.ParseHtml("<html><head></head><body><h1>Hello</h1></body></html>");
        Assert.True(tree.Count > 3);
        var text = tree.TextContent(tree.Document);
        Assert.Contains("Hello", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TestParseWithAttributes()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="main" class="container">Text</div>""");
        var main = tree.GetElementById("main");
        Assert.NotNull(main);
        var node = tree.GetNode(main.Value)!;
        Assert.Equal("container", node.GetAttribute("class"));
    }

    [Fact]
    public void TestParseNestedStructure()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <html><body>
                <div id="outer">
                    <p id="para">Hello <strong>World</strong></p>
                    <ul>
                        <li>Item 1</li>
                        <li>Item 2</li>
                    </ul>
                </div>
            </body></html>
            """);

        var outer = tree.GetElementById("outer")!.Value;
        var text = tree.TextContent(outer);
        Assert.Contains("Hello", text, StringComparison.Ordinal);
        Assert.Contains("World", text, StringComparison.Ordinal);
        Assert.Contains("Item 1", text, StringComparison.Ordinal);
        Assert.Contains("Item 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TestParseMalformedHtml()
    {
        var tree = HtmlParsing.ParseHtml("<div><p>Unclosed paragraph<p>Another<div>Nested wrong</div>");
        Assert.True(tree.Count > 3);
        var text = tree.TextContent(tree.Document);
        Assert.Contains("Unclosed paragraph", text, StringComparison.Ordinal);
        Assert.Contains("Another", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TestParseDoctype()
    {
        var tree = HtmlParsing.ParseHtml("<!DOCTYPE html><html><body>Hello</body></html>");
        var firstChild = tree.Children(tree.Document)[0];
        var node = tree.GetNode(firstChild)!;
        Assert.IsType<DoctypeData>(node.Data);
    }

    [Fact]
    public void TestParseFragment()
    {
        var tree = HtmlParsing.ParseFragment("<p>Hello</p><p>World</p>");
        var text = tree.TextContent(tree.Document);
        Assert.Contains("Hello", text, StringComparison.Ordinal);
        Assert.Contains("World", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TestParseFragmentUsesTableContext()
    {
        var tree = HtmlParsing.ParseFragmentWithContext(
            "<tr><td>cell</td></tr>",
            QualName.Html("template"));
        var row = tree.QuerySelector("tr");
        Assert.NotNull(row);
        Assert.Equal("cell", tree.TextContent(row.Value));
    }

    [Fact]
    public void FullDocumentConsumesOpenAndClosedDeclarativeShadowTemplates()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <x-open id="open-host">
                 <template id="open-template" shadowrootmode="open">
                   <span id="open-content">open shadow</span>
                 </template>
                 <b id="open-light">open light</b>
               </x-open>
               <x-closed id="closed-host">
                 <template id="closed-template" shadowrootmode="closed">
                   <span id="closed-content">closed shadow</span>
                 </template>
                 <b id="closed-light">closed light</b>
               </x-closed>
            """);

        var openHost = tree.GetElementById("open-host")!.Value;
        var closedHost = tree.GetElementById("closed-host")!.Value;
        var openLight = tree.GetElementById("open-light")!.Value;
        var closedLight = tree.GetElementById("closed-light")!.Value;
        var openRoot = tree.ShadowRootOf(openHost);
        Assert.NotNull(openRoot);
        var closedRoot = tree.ShadowRootOf(closedHost);
        Assert.NotNull(closedRoot);

        Assert.Equal(ShadowRootMode.Open, tree.ShadowRootInfo(openRoot.Value)!.Value.Mode);
        Assert.Equal(ShadowRootMode.Closed, tree.ShadowRootInfo(closedRoot.Value)!.Value.Mode);
        AssertNodes([openLight], ElementChildren(tree, openHost));
        AssertNodes([closedLight], ElementChildren(tree, closedHost));
        Assert.Null(tree.GetElementById("open-template"));
        Assert.Null(tree.GetElementById("closed-template"));
        Assert.NotNull(tree.QuerySelectorFrom(openRoot.Value, "#open-content"));
        Assert.NotNull(tree.QuerySelectorFrom(closedRoot.Value, "#closed-content"));
        Assert.True(
            tree.GetElementById("open-content") is null && tree.GetElementById("closed-content") is null,
            "document id lookup must not pierce either shadow mode");
    }

    [Fact]
    public void InvalidDeclarativeShadowModeRemainsAnOrdinaryTemplate()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <x-card id="host"><template id="invalid" shadowrootmode="Open"><span id="inside"></span></template></x-card>
               <button id="invalid-host"><template id="invalid-host-template" shadowrootmode="open"><i id="invalid-host-content"></i></template></button>
            """);
        var host = tree.GetElementById("host")!.Value;
        var template = tree.GetElementById("invalid")!.Value;
        var contents = tree.TemplateContents(template)!.Value;
        var inside = tree.GetElementById("inside")!.Value;

        Assert.Null(tree.ShadowRootOf(host));
        AssertNodes([template], ElementChildren(tree, host));
        AssertNodes([inside], ElementChildren(tree, contents));

        var invalidHost = tree.GetElementById("invalid-host")!.Value;
        var invalidHostTemplate = tree.GetElementById("invalid-host-template")!.Value;
        Assert.Null(tree.ShadowRootOf(invalidHost));
        Assert.Equal(
            new[] { invalidHostTemplate },
            ElementChildren(tree, invalidHost));
    }

    [Fact]
    public void DuplicateDeclarativeShadowRootFallsBackToAnInertTemplate()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <x-card id="host">
                 <template shadowrootmode="open"><span id="first"></span></template>
                 <template id="duplicate" shadowrootmode="closed"><span id="second"></span></template>
                 <b id="light"></b>
               </x-card>
            """);
        var host = tree.GetElementById("host")!.Value;
        var light = tree.GetElementById("light")!.Value;
        var duplicate = tree.GetElementById("duplicate")!.Value;
        var duplicateContents = tree.TemplateContents(duplicate)!.Value;
        var second = tree.GetElementById("second")!.Value;
        var root = tree.ShadowRootOf(host)!.Value;

        Assert.Equal(ShadowRootMode.Open, tree.ShadowRootInfo(root)!.Value.Mode);
        Assert.NotNull(tree.QuerySelectorFrom(root, "#first"));
        AssertNodes([duplicate, light], ElementChildren(tree, host));
        AssertNodes([second], ElementChildren(tree, duplicateContents));
    }

    [Fact]
    public void NestedDeclarativeShadowRootsKeepDistinctTreeScopes()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <x-outer id="outer-host">
                 <template shadowrootmode="open">
                   <x-inner id="inner-host">
                     <template shadowrootmode="closed"><i id="inner-shadow"></i></template>
                     <b id="inner-light"></b>
                   </x-inner>
                 </template>
               </x-outer>
            """);
        var outerHost = tree.GetElementById("outer-host")!.Value;
        var outerRoot = tree.ShadowRootOf(outerHost)!.Value;
        var innerHost = tree.QuerySelectorFrom(outerRoot, "#inner-host")!.Value;
        var innerRoot = tree.ShadowRootOf(innerHost)!.Value;

        Assert.Equal(outerRoot, tree.ContainingShadowRoot(innerHost));
        Assert.Equal(ShadowRootMode.Closed, tree.ShadowRootInfo(innerRoot)!.Value.Mode);
        Assert.Null(tree.QuerySelectorFrom(outerRoot, "#inner-shadow"));
        Assert.NotNull(tree.QuerySelectorFrom(innerRoot, "#inner-shadow"));
        Assert.NotNull(tree.QuerySelectorFrom(outerRoot, "#inner-light"));
    }

    [Fact]
    public void FragmentParsingKeepsDeclarativeShadowTemplatesInert()
    {
        var tree = HtmlParsing.ParseFragmentWithContext(
            """<template id="shadow" shadowrootmode="open"><span id="inside"></span></template>""",
            QualName.Html("x-card"));
        var template = tree.GetElementById("shadow")!.Value;
        var contents = tree.TemplateContents(template)!.Value;
        var inside = tree.GetElementById("inside")!.Value;

        Assert.False(tree.AllowsDeclarativeShadowRoots);
        AssertNodes([inside], ElementChildren(tree, contents));
        Assert.Null(tree.ContainingShadowRoot(inside));
    }

    [Fact]
    public void OrdinaryTemplateParsingIsUnchanged()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <div id="host">
                 <template id="ordinary"><span id="inside">content</span></template>
                 <span id="outside">light</span>
               </div>
            """);

        var host = tree.GetElementById("host")!.Value;
        var template = tree.GetElementById("ordinary")!.Value;
        var contents = tree.TemplateContents(template)!.Value;
        var inside = tree.GetElementById("inside")!.Value;
        var outside = tree.GetElementById("outside")!.Value;

        AssertNodes([template, outside], ElementChildren(tree, host));
        Assert.Empty(tree.Children(template));
        AssertNodes([inside], ElementChildren(tree, contents));
        Assert.Equal(contents, tree.GetNode(inside)!.Parent);
    }

    [Fact]
    public void DormantTreeSinkHookReusesTheTemplateContentsIdentity()
    {
        var tree = new DomTree();
        var host = tree.NewNode(NodeData.Element(QualName.Html("x-card")));
        tree.AppendChild(tree.Document, host);
        var contents = tree.NewNode(NodeData.Document);
        var template = tree.NewNode(NodeData.Element(
            QualName.Html("template"),
            attrs: null,
            templateContents: contents));
        tree.AppendChild(host, template);
        var attrs = new List<Attribute>
        {
            new(QualName.Attr("shadowrootmode"), "closed"),
        };

        Assert.False(HtmlParsing.AllowDeclarativeShadowRoots(tree, host));
        Assert.True(HtmlParsing.AttachDeclarativeShadow(tree, host, template, attrs));
        Assert.Equal(contents, tree.ShadowRootOf(host));
        Assert.Empty(tree.Children(host));
        Assert.Null(tree.GetNode(template)!.Parent);
        Assert.Equal(ShadowRootMode.Closed, tree.ShadowRootInfo(contents)!.Value.Mode);
    }
}
