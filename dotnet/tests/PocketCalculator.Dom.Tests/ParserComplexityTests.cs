// SECURITY.md M11: markup that made the parser quadratic. No counterpart in crates/obscura-dom.
using System.Diagnostics;

using PocketCalculator.Dom;

namespace PocketCalculator.Dom.Tests;

/// <summary>
/// Inputs that cost time quadratic in their size before the tree builder was replaced: every
/// one took tens of seconds at these sizes (50,000 nested divs took 20 s, a 50,000-sibling
/// fragment 90 s) and each finishes in well under a second now. The bounds are loose, so a
/// loaded machine does not fail them, and still far below the old cost.
/// </summary>
public class ParserComplexityTests
{
    private const int N = 50_000;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(8);

    private static string Repeat(string s, int count) => string.Concat(Enumerable.Repeat(s, count));

    private static TimeSpan Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }

    [Fact]
    public void DeeplyNestedDivsParseInLinearTime()
    {
        DomTree? tree = null;
        var elapsed = Time(() => tree = HtmlParsing.ParseHtml(Repeat("<div>", N)));
        Assert.True(elapsed < Bound, $"{N} nested divs took {elapsed}");
        Assert.Equal(N, tree!.QuerySelectorAll("div").Count);

        elapsed = Time(() => tree = HtmlParsing.ParseFragment(Repeat("<div>", N)));
        Assert.True(elapsed < Bound, $"{N} nested divs as a fragment took {elapsed}");
    }

    [Fact]
    public void AFragmentWithManySiblingsParsesInLinearTime()
    {
        DomTree? tree = null;
        var elapsed = Time(() => tree = HtmlParsing.ParseFragmentWithContext(Repeat("<div>x</div>", N), QualName.Html("div")));
        Assert.True(elapsed < Bound, $"a fragment of {N} siblings took {elapsed}");
        Assert.Equal(N, tree!.Children(tree.FragmentRoot()).Count);
    }

    public static TheoryData<string> Adversarial() =>
    [
        "p-object-divs", "spans-unmatched-end", "divs-li", "divs-dd", "divs-tables", "divs-buttons",
        "b-distinct-attrs", "html-attrs", "divs-endp-text", "table-foster-text", "svg-g-end",
        "templates", "b-spans-div-endb", "a-many", "select-options",
    ];

    private static string Build(string name) => name switch
    {
        // A p below a scope boundary: "p in button scope" used to walk every div.
        "p-object-divs" => "<p><object>" + Repeat("<div>", N),
        // "Any other end tag" used to walk the whole stack for a name that is not on it.
        "spans-unmatched-end" => Repeat("<span>", N) + Repeat("</x>", N),
        // The li and dd walks down to the first special element.
        "divs-li" => Repeat("<div>", N) + Repeat("<li>", N),
        "divs-dd" => Repeat("<div>", N) + Repeat("<dd>", N),
        // Resetting the insertion mode walked down to body after every table.
        "divs-tables" => Repeat("<div>", N) + Repeat("<table></table>", N),
        "divs-buttons" => Repeat("<div>", N) + Repeat("<button></button>", N),
        // Noah's Ark compared every entry since the last marker.
        "b-distinct-attrs" => string.Concat(Enumerable.Range(0, N).Select(i => $"<b x={i}>")),
        // Merging attributes into html compared against every attribute it already had.
        "html-attrs" => string.Concat(Enumerable.Range(0, N).Select(i => $"<html a{i}>")),
        // Text appended to one node while elements go elsewhere (past the depth cap, or fostered
        // before a table) copied the whole text each time.
        "divs-endp-text" => Repeat("<div>", N) + Repeat("</p>x", N),
        "table-foster-text" => "<table>" + Repeat("x<tr>", N),
        // Foreign end tags walked the stack for a name that is not on it.
        "svg-g-end" => "<svg>" + Repeat("<g>", N) + Repeat("</x>", N),
        "templates" => Repeat("<template>", N),
        // The adoption agency removed stack entries one at a time.
        "b-spans-div-endb" => "<b>" + Repeat("<span>", N) + "<div>" + Repeat("</b>", 100),
        "a-many" => Repeat("<a>", N),
        "select-options" => "<select>" + Repeat("<option>x", N),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Adversarial))]
    public void AdversarialMarkupParsesInLinearTime(string name)
    {
        var html = Build(name);
        var elapsed = Time(() => HtmlParsing.ParseHtml(html));
        Assert.True(elapsed < Bound, $"{name} took {elapsed} as a document");

        elapsed = Time(() => HtmlParsing.ParseFragment(html));
        Assert.True(elapsed < Bound, $"{name} took {elapsed} as a fragment");
    }

    [Fact]
    public void ATagWithManyAttributesIsBounded()
    {
        var names = Enumerable.Range(0, 20_000).Select(i => $"a{i}").ToList();
        DomTree? tree = null;
        var elapsed = Time(() => tree = HtmlParsing.ParseHtml("<div " + string.Join(' ', names) + ">"));
        Assert.True(elapsed < Bound, $"a tag with {names.Count} attributes took {elapsed}");

        // DEVIATION: a tag keeps its first 512 distinct attributes; Chromium keeps all of them.
        var div = tree!.GetNode(tree.QuerySelector("div")!.Value)!;
        Assert.Equal(HtmlTreeBuilder.MaxAttributesPerTag, div.Attrs!.Count);
        Assert.Equal("a0", div.Attrs[0].Name.Local);
        Assert.Equal("a511", div.Attrs[^1].Name.Local);
    }

    [Fact]
    public void DuplicateAttributesKeepTheFirstValue()
    {
        var tree = HtmlParsing.ParseHtml("<div a=1 b=2 a=3 B=4 c=5></div>");
        var div = tree.GetNode(tree.QuerySelector("div")!.Value)!;
        Assert.Equal(["a=1", "b=2", "c=5"], div.Attrs!.Select(a => $"{a.Name.Local}={a.Value}"));
    }

    [Fact]
    public void ParsingObservesCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using (WorkCancellation.Enter(source.Token))
        {
            Assert.Throws<OperationCanceledException>(() => HtmlParsing.ParseHtml(Repeat("<div>", 1000)));
            Assert.Throws<OperationCanceledException>(() => HtmlParsing.ParseFragment(Repeat("<p>x", 1000)));
        }
    }
}
