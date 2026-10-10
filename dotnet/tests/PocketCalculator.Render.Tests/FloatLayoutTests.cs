// CSS 2.1 9.5 float layout against Chromium 141. No counterpart in crates/obscura-render, which
// lays floats out as flex rows; see "Floats are CSS floats, not flex rows" in todo.md. Each case
// is one of the pages in render-repros/floats/ (scripts/float-conformance/), and the numbers are
// Chromium's getBoundingClientRect() and, for the inline spans, getClientRects().
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Render.Tests;

public class FloatLayoutTests
{
    private const string Text =
        "Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut "
        + "labore et dolore magna aliqua";

    private const string Style =
        "<style>html,body{margin:0} body{font:16px/20px 'Liberation Sans'}"
        + " .c{width:400px} .l{float:left} .r{float:right} p{margin:0}</style>";

    private static (DomTree Tree, DomLayout Laid) Layout(string body)
    {
        DomTree tree = HtmlParsing.ParseHtml("<!doctype html><html><head>" + Style + "</head><body>" + body + "</body></html>");
        return (tree, RenderDom.LayoutDom(tree, (1280f, 720f)));
    }

    private static Rect Box(DomTree tree, DomLayout laid, string id) =>
        laid.Rects[tree.GetElementById(id) ?? throw new InvalidOperationException(id)];

    private static void AssertBox(DomTree tree, DomLayout laid, string id, float x, float y, float width, float height)
    {
        Rect box = Box(tree, laid, id);
        bool near = MathF.Abs(box.X - x) <= 1f
            && MathF.Abs(box.Y - y) <= 1f
            && MathF.Abs(box.Width - width) <= 1f
            && MathF.Abs(box.Height - height) <= 1f;
        Assert.True(near, $"{id}: expected {x},{y},{width},{height}, got {box}");
    }

    /// <summary>Each line fragment's left edge and top, within 2px of Chromium's.</summary>
    private static void AssertLines(DomTree tree, DomLayout laid, string id, params (float X, float Y)[] lines)
    {
        List<Rect> fragments = laid.InlineFragments[tree.GetElementById(id)!.Value];
        Assert.True(
            fragments.Count == lines.Length,
            $"{id}: expected {lines.Length} lines, got {string.Join(" ", fragments)}");
        for (int i = 0; i < lines.Length; i++)
        {
            Assert.True(
                MathF.Abs(fragments[i].X - lines[i].X) <= 2f && MathF.Abs(fragments[i].Y - lines[i].Y) <= 2f,
                $"{id} line {i}: expected {lines[i]}, got {fragments[i]}");
        }
    }

    [Fact]
    public void LineBoxesBesideALeftFloatAreShortened()
    {
        var (tree, laid) = Layout(
            $"<div class=c id=c><div class=l id=f style='width:100px;height:50px'></div><p id=p><span id=s>{Text}</span></p></div>");
        AssertBox(tree, laid, "c", 0, 0, 400, 80);
        AssertBox(tree, laid, "p", 0, 0, 400, 80);
        AssertLines(tree, laid, "s", (100, 1), (100, 21), (100, 41), (0, 61));
    }

    [Fact]
    public void PlainTextWrapsAroundAFloat()
    {
        // The same paragraph without an inline element goes through the owner-free shaping path.
        var (tree, laid) = Layout(
            $"<div class=c id=c><div class=l id=f style='width:100px;height:50px'></div><p id=p>{Text}</p></div>");
        AssertBox(tree, laid, "p", 0, 0, 400, 80);
    }

    [Fact]
    public void AFloatThatDoesNotFitBesideEarlierOnesMovesDownToTheFirstGap()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c style='width:300px'><div class=l id=f1 style='width:100px;height:100px'></div>"
            + "<div class=l id=f2 style='width:100px;height:50px'></div>"
            + "<div class=l id=f3 style='width:150px;height:30px'></div></div>");
        AssertBox(tree, laid, "c", 0, 0, 300, 0);
        AssertBox(tree, laid, "f2", 100, 0, 100, 50);
        AssertBox(tree, laid, "f3", 100, 50, 150, 30);
    }

    [Fact]
    public void LeftRightLeftPacksTheThirdFloatBesideTheFirst()
    {
        var (tree, laid) = Layout(
            "<div class=c><div class=l id=f1 style='width:100px;height:60px'></div><div class=r id=f2 style='width:100px;height:60px'></div>"
            + "<div class=l id=f3 style='width:100px;height:60px'></div><div class=l id=f4 style='width:150px;height:20px'></div></div>");
        AssertBox(tree, laid, "f2", 300, 0, 100, 60);
        AssertBox(tree, laid, "f3", 100, 0, 100, 60);
        AssertBox(tree, laid, "f4", 0, 60, 150, 20);
    }

    [Fact]
    public void ClearanceIgnoresTheMarginOnlyWhenTheBoxWouldSitAboveTheFloat()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><div id=x style='height:10px;margin-bottom:10px'></div><div class=l id=f1 style='width:100px;height:60px'></div>"
            + "<div id=k style='clear:both;margin-top:30px;height:20px'></div></div>");
        AssertBox(tree, laid, "f1", 0, 20, 100, 60);
        AssertBox(tree, laid, "k", 0, 80, 400, 20);
        AssertBox(tree, laid, "c", 0, 0, 400, 100);

        (tree, laid) = Layout(
            "<div class=c id=c><div class=l id=f1 style='width:100px;height:10px'></div><div id=x style='height:30px'></div>"
            + "<div id=k style='clear:both;margin-top:15px;height:20px'></div></div>");
        AssertBox(tree, laid, "k", 0, 45, 400, 20);
    }

    [Fact]
    public void ClearLeftLeavesARightFloatShorteningTheNextLines()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><div class=l id=f1 style='width:100px;height:60px'></div><div class=r id=f2 style='width:100px;height:90px'></div>"
            + $"<div id=k style='clear:left;height:20px'>cleared</div><p id=p><span id=s>{Text}</span></p></div>");
        AssertBox(tree, laid, "k", 0, 60, 400, 20);
        AssertBox(tree, laid, "s", 0, 81, 357.55f, 57);
        AssertBox(tree, laid, "c", 0, 0, 400, 140);
    }

    [Fact]
    public void AFloatSitsAfterThePendingMarginLevelWithTheNextBlock()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><div id=x style='height:20px;margin-bottom:25px'></div><div class=l id=f1 style='width:100px;height:30px'></div>"
            + "<div id=y style='margin-top:10px;height:20px'>text</div></div>");
        AssertBox(tree, laid, "f1", 0, 45, 100, 30);
        AssertBox(tree, laid, "y", 0, 45, 400, 20);
    }

    [Fact]
    public void MarginsCollapseThroughABlockHoldingOnlyAFloat()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><div id=x style='height:20px;margin-bottom:20px'></div>"
            + "<div id=e style='margin-top:10px;margin-bottom:30px'><div class=l id=f1 style='width:100px;height:30px'></div></div>"
            + "<div id=y style='height:20px;margin-top:10px'>after</div></div>");
        AssertBox(tree, laid, "e", 0, 40, 400, 0);
        AssertBox(tree, laid, "f1", 0, 40, 100, 30);
        AssertBox(tree, laid, "y", 0, 50, 400, 20);
    }

    [Fact]
    public void AFloatMovesWithTheMarginItsParentCollapsesWithLater()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c style='padding-top:1px'><div id=o style='margin-top:20px'>"
            + "<div class=l id=f1 style='width:100px;height:30px'></div><p id=p style='margin-top:30px'>text beside</p></div></div>");
        AssertBox(tree, laid, "o", 0, 31, 400, 20);
        AssertBox(tree, laid, "f1", 0, 31, 100, 30);
        AssertBox(tree, laid, "p", 0, 31, 400, 20);
    }

    [Fact]
    public void AFormattingContextRootIsNarrowedBesideAFloat()
    {
        var (tree, laid) = Layout(
            $"<div class=c id=c><div class=l id=f1 style='width:100px;height:60px'></div><div id=o style='overflow:hidden'>{Text}</div></div>");
        AssertBox(tree, laid, "o", 100, 0, 300, 80);
        AssertBox(tree, laid, "c", 0, 0, 400, 80);
    }

    [Fact]
    public void AFormattingContextRootThatDoesNotFitMovesBelowTheFloat()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><div class=l id=f1 style='width:150px;height:60px'></div>"
            + "<div id=o style='overflow:hidden;width:300px;height:20px'></div><div id=o2 style='overflow:hidden;width:200px;height:20px'></div></div>");
        AssertBox(tree, laid, "o", 0, 60, 300, 20);
        AssertBox(tree, laid, "o2", 0, 80, 200, 20);
    }

    [Fact]
    public void FormattingContextRootsContainTheirFloats()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c style='display:flow-root;padding:5px'><div class=r id=f1 style='width:100px;height:60px;margin:10px'></div></div><p id=p>next</p>");
        AssertBox(tree, laid, "c", 0, 0, 410, 90);
        AssertBox(tree, laid, "f1", 295, 15, 100, 60);
        AssertBox(tree, laid, "p", 0, 90, 1280, 20);

        (tree, laid) = Layout(
            "<div id=c style='width:400px;position:relative;height:200px'><div id=abs style='position:absolute;top:10px;left:10px'>"
            + "<div class=l id=f1 style='width:100px;height:60px'></div><div class=l id=f2 style='width:50px;height:30px'></div></div></div>");
        AssertBox(tree, laid, "abs", 10, 10, 150, 60);
        AssertBox(tree, laid, "f2", 110, 10, 50, 30);
    }

    [Fact]
    public void AnAutoWidthFloatIsShrinkToFit()
    {
        var (tree, laid) = Layout(
            $"<div class=c id=c><div class=l id=f1>short text</div><div class=r id=f2><span id=s>{Text}</span></div></div>");
        AssertBox(tree, laid, "f1", 0, 0, 65.81f, 20);
        AssertBox(tree, laid, "f2", 0, 20, 400, 60);
        AssertBox(tree, laid, "c", 0, 0, 400, 0);

        (tree, laid) = Layout(
            "<div class=c><div class=l id=f1 style='width:100px;min-height:40px'>x</div>"
            + "<div class=l id=f2 style='max-width:80px'>long text inside narrow float</div></div>");
        AssertBox(tree, laid, "f2", 100, 0, 80, 80);
    }

    [Fact]
    public void FloatMarginsDoNotCollapseAndPushTheLineBoxes()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><div class=l id=f1 style='width:100px;height:40px;margin:10px 20px 5px 15px'></div>"
            + $"<div class=l id=f2 style='width:100px;height:40px;margin:20px'></div><p id=p><span id=s>{Text}</span></p></div>");
        AssertBox(tree, laid, "f1", 15, 10, 100, 40);
        AssertBox(tree, laid, "f2", 155, 20, 100, 40);
        AssertLines(tree, laid, "s", (275, 1), (275, 21), (275, 41), (275, 61), (0, 81), (0, 101));
    }

    [Fact]
    public void AFirstWordThatDoesNotFitBesideAFloatMovesBelowIt()
    {
        var (tree, laid) = Layout(
            "<div class=c style='width:200px'><div class=l id=f style='width:150px;height:40px'></div>"
            + "<p id=p><span id=s>Supercalifragilistic word then more words</span></p></div>");
        AssertLines(tree, laid, "s", (0, 41), (0, 61));
        AssertBox(tree, laid, "p", 0, 0, 200, 80);
    }

    [Fact]
    public void AFloatInTheMiddleOfALineIsPlacedOnThatLine()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><p id=p>Some words <span class=r id=f1 style='width:60px;height:30px'></span>"
            + "<span id=s>and then more words follow here to fill the remaining lines of text</span></p></div>");
        AssertBox(tree, laid, "f1", 340, 0, 60, 30);
        AssertBox(tree, laid, "p", 0, 0, 400, 40);
        AssertLines(tree, laid, "s", (93.38f, 1), (0, 21));
    }

    [Fact]
    public void AFloatThatDoesNotFitTheRestOfItsLineGoesBelowIt()
    {
        var (tree, laid) = Layout(
            "<div class=c style='width:300px'><p id=p><span id=s1>Many words here that fill the line </span>"
            + "<span class=l id=f1 style='width:250px;height:30px'></span><span id=s2>and continue onto further lines here</span></p></div>");
        AssertBox(tree, laid, "f1", 0, 20, 250, 30);
        AssertBox(tree, laid, "p", 0, 0, 300, 70);
        AssertLines(tree, laid, "s2", (233.89f, 1), (0, 51));
    }

    [Fact]
    public void ALaterFloatInTheParagraphShortensTheLineItIsOn()
    {
        var (tree, laid) = Layout(
            $"<div class=c style='width:300px'><p id=p><span class=l id=f1 style='width:60px;height:30px'></span><span id=s1>{Text}</span>"
            + "<span class=l id=f2 style='width:60px;height:30px'></span><span id=s2> and more words after the second float here</span></p></div>");
        AssertBox(tree, laid, "f2", 0, 60, 60, 30);
        AssertBox(tree, laid, "p", 0, 0, 300, 100);
        AssertLines(tree, laid, "s1", (60, 1), (60, 21), (0, 41), (60, 61));
    }

    [Fact]
    public void ShrinkToFitCountsAFloatOnTheSameLineAsTheText()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><span id=ib style='display:inline-block'><span class=l id=f1 style='width:100px;height:60px'></span>txt</span> after</div>");
        AssertBox(tree, laid, "ib", 0, 0, 116.89f, 60);
    }

    [Fact]
    public void NestedFloatsShareTheirAncestorsFloatContext()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><div id=o1><div id=o2><div class=l id=f1 style='width:100px;height:80px'></div></div>"
            + $"<p id=p1>One two three</p></div><p id=p2><span id=s>{Text}</span></p></div>");
        AssertBox(tree, laid, "o2", 0, 0, 400, 0);
        AssertBox(tree, laid, "p1", 0, 0, 400, 20);
        AssertLines(tree, laid, "s", (100, 21), (100, 41), (100, 61), (0, 81));
        AssertBox(tree, laid, "c", 0, 0, 400, 100);
    }

    [Fact]
    public void LogicalFloatValuesMapToTheElementsDirection()
    {
        var (tree, laid) = Layout(
            "<div class=c id=c><div id=f1 style='float:inline-start;width:100px;height:40px'></div>"
            + "<div id=f2 style='float:inline-end;width:100px;height:40px'></div><div id=k style='clear:inline-start;height:10px'></div></div>");
        AssertBox(tree, laid, "f2", 300, 0, 100, 40);
        AssertBox(tree, laid, "k", 0, 40, 400, 10);

        (tree, laid) = Layout(
            "<div class=c dir=rtl><div id=f1 style='float:inline-start;width:100px;height:40px'></div></div>");
        AssertBox(tree, laid, "f1", 300, 0, 100, 40);
    }

    [Fact]
    public void AnInlineBlockBesideAFloatResolvesItsPercentageAgainstTheBlock()
    {
        // wikipedia.org's footer: a 35% float and a 65% inline-block share one line. Narrowing the
        // line must not shrink what the percentage is of.
        var (tree, laid) = Layout(
            "<div id=c style='width:1000px'><div class=l id=f1 style='width:35%;height:50px'></div>"
            + "<div id=n style='display:inline-block;width:65%;height:20px'></div></div>");
        AssertBox(tree, laid, "n", 350, 0, 650, 20);
    }

    [Fact]
    public void RightFloatsAmongInlineBlocksStayOnTheirLine()
    {
        // render-repros/right-float-navigation.html: a run of inline-blocks does not fold into a
        // shaped context, so its floats go before it rather than splitting it into lines.
        var (tree, laid) = Layout(
            "<nav id=bar style='width:400px;height:30px;overflow:hidden'>"
            + "<span id=a style='display:inline-block;width:60px;height:30px'></span>"
            + " <div class=r id=r1 style='width:40px;height:30px'></div>"
            + " <span id=b style='display:inline-block;width:80px;height:30px'></span>"
            + " <div class=r id=r2 style='width:50px;height:30px'></div>"
            + " <div class=r id=r3 style='width:30px;height:30px'></div> </nav>");
        AssertBox(tree, laid, "r1", 360, 0, 40, 30);
        AssertBox(tree, laid, "r2", 310, 0, 50, 30);
        AssertBox(tree, laid, "r3", 280, 0, 30, 30);
        AssertBox(tree, laid, "b", 64, 0, 80, 30);
    }

    [Fact]
    public void AClearfixPseudoElementContainsTheFloats()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<!doctype html><html><head>" + Style + "<style>.cf::after{content:'';display:table;clear:both}</style></head><body>"
            + "<div class='c cf' id=c><div class=l id=f1 style='width:100px;height:60px'></div><div class=r id=f2 style='width:100px;height:80px'></div></div>"
            + "<p id=p>next</p></body></html>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        AssertBox(tree, laid, "c", 0, 0, 400, 80);
        AssertBox(tree, laid, "p", 0, 80, 1280, 20);
    }

    [Fact]
    public void FloatIsIgnoredOnFlexAndGridItems()
    {
        var (tree, laid) = Layout(
            "<div class=c style='display:flex'><div class=r id=f1 style='width:100px;height:30px'></div><div id=f2 style='width:100px;height:40px'></div></div>");
        AssertBox(tree, laid, "f1", 0, 0, 100, 30);
        AssertBox(tree, laid, "f2", 100, 0, 100, 40);
    }

    [Fact]
    public void AParagraphOfHundredsOfFloatsStaysLinear()
    {
        // Each anchored float lays its paragraph out again; past a bound the rest are placed from
        // one layout, so this is not quadratic.
        var body = new System.Text.StringBuilder("<p id=p>");
        for (int i = 0; i < 600; i++)
        {
            body.Append("word <span style='float:left;width:3px;height:3px'></span>");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (tree, laid) = Layout(body.Append("</p>").ToString());
        Assert.True(Box(tree, laid, "p").Height > 0f);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"600 inline floats took {clock.Elapsed}");
    }
}
