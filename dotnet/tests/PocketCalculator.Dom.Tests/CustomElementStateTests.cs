using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// Port addition: the custom element state behind <c>:defined</c> and the is value an element
/// keeps without an is attribute. Expected values are Chromium 141's.
/// </summary>
public class CustomElementStateTests
{
    private static List<string> Ids(DomTree tree, string selector)
    {
        var ids = new List<string>();
        foreach (var id in tree.QuerySelectorAll(selector))
        {
            ids.Add(tree.GetNode(id)!.GetAttribute("id")!);
        }

        return ids;
    }

    [Fact]
    public void DefinedMatchesEverythingButUnupgradedCandidates()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <body><div id="d"></div><x-a id="a"></x-a><button is="x-b" id="b"></button>
            <font-face id="ff"></font-face><svg><g id="g"></g><x-s id="s"></x-s></svg></body>
            """);
        Assert.Equal(["a", "b"], Ids(tree, "body :not(:defined)"));

        var a = tree.QuerySelector("#a")!.Value;
        ((ElementData)tree.GetNode(a)!.Data).CustomElementState = CustomElementState.Custom;
        var b = tree.QuerySelector("#b")!.Value;
        ((ElementData)tree.GetNode(b)!.Data).CustomElementState = CustomElementState.Failed;
        Assert.Equal(["b"], Ids(tree, "body :not(:defined)"));
        Assert.Contains("a", Ids(tree, "body :defined"));
    }

    [Fact]
    public void AnIsValueWithoutAnAttributeIsSerializedAndCloned()
    {
        var tree = HtmlParsing.ParseHtml("<body></body>");
        var button = tree.NewNode(NodeData.Element(QualName.Html("button")));
        ((ElementData)tree.GetNode(button)!.Data).IsValue = "x-btn";
        Assert.Equal("""<button is="x-btn"></button>""", tree.OuterHtml(button));

        var clone = tree.CloneNode(button, false)!.Value;
        Assert.Equal("""<button is="x-btn"></button>""", tree.OuterHtml(clone));
        var cloned = (ElementData)tree.GetNode(clone)!.Data;
        Assert.Equal(CustomElementState.Unknown, cloned.CustomElementState);
        Assert.False(cloned.IsDefined());
    }
}
