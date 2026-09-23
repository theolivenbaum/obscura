using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The pseudo-element half of the CSSOM snapshot, reached from JavaScript as
/// <c>getComputedStyle(el, '::before')</c>.
/// </summary>
/// <remarks>
/// Every expectation was measured on Chromium 141 over the same markup, at a 1280x720 viewport
/// with a hand-launched browser. The Rust engine answers none of these: its op takes a node id
/// alone, so it reports the originating element's style for every pseudo-element argument.
/// </remarks>
public class ComputedStylePseudoElementTests
{
    private static PreparedRender Prepare(string html, out DomTree tree)
    {
        tree = HtmlParsing.ParseHtml(html);
        RenderResourceCache resources = new();
        return RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");
    }

    private static Dictionary<string, string>? Computed(string html, string id, string? pseudo)
    {
        PreparedRender prepared = Prepare(html, out DomTree tree);
        NodeId node = tree.GetElementById(id) ?? throw new InvalidOperationException($"no element #{id}");

        return prepared.ComputedStyle(node, pseudo);
    }

    private const string Markup = """
        <style>
        #a { width: 200px; color: rgb(10,20,30); font-size: 20px; }
        #a::before { content: "BEFORE"; color: rgb(1,2,3); font-size: 11px;
                     display: block; width: 33px; height: 7px; margin-left: 5px; }
        #a::after  { content: "AFT"; color: rgb(4,5,6); display: inline-block; width: 12px; }
        #b { width: 150px; color: rgb(70,80,90); font-size: 17px; }
        </style>
        <div id="a">A</div><div id="b">B</div>
        """;

    [Fact]
    public void MatchedBeforeReportsItsOwnStyleAndItsUsedBox()
    {
        Dictionary<string, string> before = Computed(Markup, "a", "::before")!;

        Assert.Equal("rgb(1, 2, 3)", before["color"]);
        Assert.Equal("11px", before["font-size"]);
        Assert.Equal("block", before["display"]);
        Assert.Equal("33px", before["width"]);
        Assert.Equal("7px", before["height"]);
        Assert.Equal("5px", before["margin-left"]);
        Assert.Equal("\"BEFORE\"", before["content"]);
    }

    [Fact]
    public void MatchedAfterInheritsWhatItDoesNotSpecify()
    {
        Dictionary<string, string> after = Computed(Markup, "a", "::after")!;

        Assert.Equal("rgb(4, 5, 6)", after["color"]);

        // Not specified on ::after, so it inherits the originating element's 20px.
        Assert.Equal("20px", after["font-size"]);
        Assert.Equal("inline-block", after["display"]);
        Assert.Equal("12px", after["width"]);
        Assert.Equal("\"AFT\"", after["content"]);
    }

    [Fact]
    public void ElementItselfIsUnaffectedByTheOverload()
    {
        Dictionary<string, string> element = Computed(Markup, "a", null)!;

        Assert.Equal("rgb(10, 20, 30)", element["color"]);
        Assert.Equal("20px", element["font-size"]);
        Assert.Equal("200px", element["width"]);

        // CSS Content 3: `content` is `normal` on an element, whatever its pseudo-elements say.
        Assert.Equal("normal", element["content"]);
    }

    [Fact]
    public void UnmatchedBeforeIsInitialPlusWhatItInherits()
    {
        Dictionary<string, string> before = Computed(Markup, "b", "::before")!;

        Assert.Equal("rgb(70, 80, 90)", before["color"]);
        Assert.Equal("17px", before["font-size"]);

        // A pseudo-element's initial display is inline, and it generated no box to measure.
        Assert.Equal("inline", before["display"]);
        Assert.Equal("auto", before["width"]);
        Assert.Equal("auto", before["height"]);

        // `normal` computes to `none` on ::before and ::after, which is how page script tells
        // a realized one from an absent one.
        Assert.Equal("none", before["content"]);
    }

    [Fact]
    public void RecognisedButUncomputedPseudoKeepsContentNormal()
    {
        // The cascade computes ::before, ::after, ::placeholder and ::-webkit-slider-thumb. A
        // ::first-line is a real pseudo-element it does not compute, so it answers with the
        // initial style plus what it inherits - and `normal` stays `normal` off ::before/::after.
        Dictionary<string, string> firstLine = Computed(Markup, "a", "::first-line")!;

        Assert.Equal("rgb(10, 20, 30)", firstLine["color"]);
        Assert.Equal("20px", firstLine["font-size"]);
        Assert.Equal("normal", firstLine["content"]);
    }

    [Theory]
    [InlineData(":before")]
    [InlineData("::BEFORE")]
    [InlineData("::Before")]
    public void LegacyAndUppercaseSpellingsSelectTheSamePseudo(string spelling)
    {
        Dictionary<string, string> before = Computed(Markup, "a", spelling)!;

        Assert.Equal("rgb(1, 2, 3)", before["color"]);
        Assert.Equal("\"BEFORE\"", before["content"]);
    }

    [Fact]
    public void UnknownPseudoHasNoSnapshotAtAll()
    {
        // Chromium answers a well-formed name it does not support with an empty declaration,
        // which is what a null snapshot becomes once bootstrap has it.
        Assert.Null(Computed(Markup, "a", "::bogus-thing"));
    }

    [Fact]
    public void CounterContentIsReportedUnresolved()
    {
        const string html = """
            <style>#g::before { content: counter(step) ". "; }</style>
            <div id="g">G</div>
            """;

        // A computed `content` still carries counter(), because the counter pass runs later.
        Assert.Equal("counter(step) \". \"", Computed(html, "g", "::before")!["content"]);
    }

    [Fact]
    public void QuotesInsideGeneratedContentAreEscaped()
    {
        const string html = """
            <style>#q::before { content: "ESC\"Q"; color: rgb(2,2,2); }</style>
            <div id="q">q</div>
            """;

        Dictionary<string, string> before = Computed(html, "q", "::before")!;

        Assert.Equal("\"ESC\\\"Q\"", before["content"]);
        Assert.Equal("rgb(2, 2, 2)", before["color"]);
    }
}
