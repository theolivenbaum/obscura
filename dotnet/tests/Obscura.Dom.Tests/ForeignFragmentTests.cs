using Xunit;

namespace Obscura.Dom.Tests;

/// <summary>
/// Fragment parsing where the context element lives in SVG or MathML.
/// </summary>
/// <remarks>
/// AngleSharp's <c>ParseFragment</c> does not enter foreign-content mode for a
/// namespaced context element: it returns the children in the HTML namespace with
/// lowercased names. html5ever honors the context's namespace, so the port wraps the
/// markup in a literal foreign root and unwraps after parsing.
///
/// The expected values here were taken from the release Rust binary, not from the
/// spec text:
///   svg.innerHTML = '&lt;linearGradient id=g&gt;&lt;stop/&gt;&lt;/linearGradient&gt;'
///   -&gt; http://www.w3.org/2000/svg | linearGradient
/// </remarks>
public sealed class ForeignFragmentTests
{
    [Fact]
    public void SvgContextKeepsTheSvgNamespaceAndCamelCaseName()
    {
        var tree = HtmlParsing.ParseFragmentWithContext(
            "<linearGradient id=\"g\"><stop/></linearGradient>",
            new QualName(null, Namespaces.Svg, "svg"));

        var children = tree.Children(tree.FragmentRoot());
        Assert.Single(children);

        var name = tree.GetNode(children[0])!.ElementName!.Value;
        Assert.Equal(Namespaces.Svg, name.Ns);
        // Case matters: the HTML path would lowercase this to "lineargradient".
        Assert.Equal("linearGradient", name.Local);
    }

    [Fact]
    public void NestedSvgContextAlsoKeepsTheNamespace()
    {
        var tree = HtmlParsing.ParseFragmentWithContext(
            "<circle r=\"5\"/>",
            new QualName(null, Namespaces.Svg, "g"));

        var children = tree.Children(tree.FragmentRoot());
        Assert.Single(children);

        var name = tree.GetNode(children[0])!.ElementName!.Value;
        Assert.Equal(Namespaces.Svg, name.Ns);
        Assert.Equal("circle", name.Local);
    }

    [Fact]
    public void HtmlContextIsUnaffected()
    {
        // The wrap-and-unwrap path must not disturb ordinary HTML fragment parsing,
        // where the context element still selects the insertion mode.
        var tree = HtmlParsing.ParseFragmentWithContext("<tr><td>x</td></tr>", QualName.Html("tbody"));

        var children = tree.Children(tree.FragmentRoot());
        Assert.Single(children);

        var name = tree.GetNode(children[0])!.ElementName!.Value;
        Assert.Equal(Namespaces.Html, name.Ns);
        Assert.Equal("tr", name.Local);
    }
}
