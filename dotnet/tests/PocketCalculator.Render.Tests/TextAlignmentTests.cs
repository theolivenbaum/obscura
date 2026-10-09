// Line alignment against Chromium 141: trailing white space, justification, text-align-last,
// text-indent, right-to-left paragraphs and inline-box edges on aligned lines. Every number is
// a getClientRects() fragment Chromium reports for the same markup; the pages are the ones in
// render-repros/text-align (scripts/text-align-conformance), which score them at 0.5px.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class TextAlignmentTests
{
    private const string T =
        "Lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua";

    private const string HE = "שלום עולם זהו משפט ארוך בעברית שנועד לבדוק שבירת שורות ויישור טקסט מימין לשמאל בדפדפן";

    private const string Style = """
        <style>
            html, body { margin: 0; }
            body { font: 16px/20px 'Liberation Sans', sans-serif; }
            .c { width: 300px; margin-bottom: 10px; }
            .he { font-family: 'DejaVu Sans', sans-serif; }
            .ib { display: inline-block; width: 40px; height: 12px; vertical-align: top; }
            .f { width: 60px; height: 30px; }
        </style>
        """;

    private static (DomTree Tree, DomLayout Laid) Lay(string body)
    {
        DomTree tree = HtmlParsing.ParseHtml("<!doctype html><html><head>" + Style + "</head><body>" + body + "</body></html>");
        return (tree, RenderDom.LayoutDom(tree, (1280f, 720f)));
    }

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    /// <summary>The x and width of each line fragment of an inline element, in order.</summary>
    private static void AssertLines(DomTree tree, DomLayout laid, string id, params (float X, float Width)[] expected)
    {
        List<Rect> fragments = [.. laid.InlineFragments[Id(tree, id)].Where(rect => rect.Width > 0f)];
        Assert.True(expected.Length == fragments.Count, $"{id}: {expected.Length} fragments expected, got {string.Join(" ", fragments)}");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(
                MathF.Abs(expected[i].X - fragments[i].X) <= 0.5f && MathF.Abs(expected[i].Width - fragments[i].Width) <= 0.5f,
                $"{id} line {i}: Chromium {expected[i]}, got ({fragments[i].X}, {fragments[i].Width})");
        }
    }

    private static void AssertBox(DomTree tree, DomLayout laid, string id, float x, float width)
    {
        Rect rect = laid.Rects[Id(tree, id)];
        Assert.True(MathF.Abs(rect.X - x) <= 0.5f && MathF.Abs(rect.Width - width) <= 0.5f, $"{id}: Chromium ({x}, {width}), got {rect}");
    }

    /// <summary>
    /// CSS Text 3 4.1.3: the space a soft wrap leaves at the end of a line is removed, so a
    /// centred line sits half a space, and a right- or end-aligned one a whole space, further
    /// right than its advance with the space would put it.
    /// </summary>
    [Fact]
    public void TrailingSpaceAtASoftWrapTakesNoPartInAlignment()
    {
        (DomTree tree, DomLayout laid) = Lay($"""
            <div class=c style="text-align:center"><span id=center>{T}</span></div>
            <div class=c style="text-align:right"><span id=right>{T}</span></div>
            <div class=c style="text-align:end"><span id=end>{T}</span></div>
            """);
        AssertLines(tree, laid, "center", (9.94f, 280.13f), (16.14f, 267.7f), (22.8f, 254.39f), (128.64f, 42.7f));
        AssertLines(tree, laid, "right", (19.88f, 280.13f), (32.3f, 267.7f), (45.61f, 254.39f), (257.3f, 42.7f));
        AssertLines(tree, laid, "end", (19.88f, 280.13f), (32.3f, 267.7f), (45.61f, 254.39f), (257.3f, 42.7f));
    }

    /// <summary>Spaces before a forced break are removed too, and end no fragment.</summary>
    [Fact]
    public void SpacesBeforeABreakAreRemoved()
    {
        (DomTree tree, DomLayout laid) = Lay("""
            <div class=c style="text-align:center"><span id=a1>alpha beta </span><br><span id=a2>gamma</span> <br> <span id=a3>delta</span> </div>
            <div class=c style="text-align:right"><span id=b1>alpha beta </span><br><span id=b2>gamma</span></div>
            <div class=c style="text-align:right"><span id=d1>alpha</span>   <br>   <span id=d2>beta</span>   </div>
            """);
        AssertLines(tree, laid, "a1", (112.63f, 74.73f));
        AssertLines(tree, laid, "a2", (123.31f, 53.36f));
        AssertLines(tree, laid, "b1", (225.27f, 74.73f));
        AssertLines(tree, laid, "d1", (260.84f, 39.16f));
    }

    /// <summary>A span whose only text on a line is the space a wrap removed has no fragment there.</summary>
    [Fact]
    public void ASpaceLeftAtASoftWrapStartsNoFragment()
    {
        (DomTree tree, DomLayout laid) = Lay("""
            <div class=c><span id=b1>Lorem ipsum dolor sit amet consectetur</span><span id=b2> adipiscing elit</span></div>
            """);
        AssertLines(tree, laid, "b1", (0f, 280.13f));
        AssertLines(tree, laid, "b2", (0f, 96.06f));
        Assert.Equal(21f, laid.Rects[Id(tree, "b2")].Y, 0.5f);
    }

    /// <summary>
    /// pre-wrap's space hangs at a soft wrap, as a fragment of its own past the content, and
    /// counts before a forced break; pre keeps its spaces.
    /// </summary>
    [Fact]
    public void PreservedSpacesHangOnlyAtASoftWrap()
    {
        // A raw string would not survive editors that trim trailing spaces.
        const string preserved = "alpha beta   \ngamma";
        (DomTree tree, DomLayout laid) = Lay(
            $"<div class=c style=\"text-align:center;white-space:pre-wrap\"><span id=a1>{preserved}</span></div>"
            + $"<div class=c style=\"text-align:right;white-space:pre-wrap\"><span id=d1>{T}</span></div>"
            + $"<div class=c style=\"text-align:right;white-space:pre\"><span id=e1>{preserved}</span></div>");
        AssertLines(tree, laid, "a1", (105.95f, 88.08f), (123.31f, 53.36f));
        AssertLines(
            tree,
            laid,
            "d1",
            (19.88f, 280.13f),
            (300f, 4.45f),
            (32.3f, 267.7f),
            (300f, 4.45f),
            (45.61f, 254.39f),
            (300f, 4.45f),
            (257.3f, 42.7f));
        AssertLines(tree, laid, "e1", (211.92f, 88.08f), (246.64f, 53.36f));
    }

    /// <summary>
    /// Justified lines fill the line by widening their inner spaces; the last line, and the
    /// line before a forced break, are start-aligned.
    /// </summary>
    [Fact]
    public void JustifiedLinesFillTheLine()
    {
        (DomTree tree, DomLayout laid) = Lay($"""
            <div class=c style="text-align:justify"><span id=b1>{T}</span></div>
            <div class=c style="text-align:justify"><span id=d1>Lorem ipsum dolor sit amet</span><br><span id=d2>{T}</span></div>
            <div class=c style="text-align:justify"><span id=e1 style="padding:0 5px">Lorem ipsum</span> dolor sit amet consectetur <span id=e2>adipiscing elit sed do eiusmod</span></div>
            """);
        AssertLines(tree, laid, "b1", (0f, 300f), (0f, 300f), (0f, 300f), (0f, 42.7f));
        AssertLines(tree, laid, "d1", (0f, 192.97f));
        AssertLines(tree, laid, "d2", (0f, 300f), (0f, 300f), (0f, 300f), (0f, 42.7f));
        AssertLines(tree, laid, "e1", (0f, 104.45f));
    }

    /// <summary><c>text-align-last</c> aligns the last line, justify included.</summary>
    [Fact]
    public void TextAlignLastAlignsTheLastLine()
    {
        (DomTree tree, DomLayout laid) = Lay($"""
            <div class=c style="text-align:justify;text-align-last:right"><span id=right>{T}</span></div>
            <div class=c style="text-align:justify;text-align-last:justify"><span id=justify>{T}</span></div>
            <div class=c style="text-align-last:right"><span id=only>{T}</span></div>
            """);
        AssertLines(tree, laid, "right", (0f, 300f), (0f, 300f), (0f, 300f), (257.3f, 42.7f));
        AssertLines(tree, laid, "justify", (0f, 300f), (0f, 300f), (0f, 300f), (0f, 42.7f));
        AssertLines(tree, laid, "only", (0f, 280.13f), (0f, 267.7f), (0f, 254.39f), (257.3f, 42.7f));
    }

    /// <summary>
    /// The first line's indent is at its start; an aligned line is aligned in what the indent
    /// leaves, a justified one fills it, and a negative indent widens the line past the box.
    /// </summary>
    [Fact]
    public void TextIndentNarrowsTheFirstLineAtItsStart()
    {
        (DomTree tree, DomLayout laid) = Lay($"""
            <div class=c style="text-align:center;text-indent:40px"><span id=center>{T}</span></div>
            <div class=c style="text-align:right;text-indent:40px"><span id=right>{T}</span></div>
            <div class=c style="text-align:justify;text-indent:40px"><span id=justify>{T}</span></div>
            <div class=c style="text-align:right;text-indent:-30px;padding-left:30px;width:270px"><span id=negative>{T}</span></div>
            """);
        AssertLines(tree, laid, "center", (73.52f, 192.97f), (32.14f, 235.7f), (14.36f, 271.27f), (77.5f, 145f));
        AssertLines(tree, laid, "right", (107.03f, 192.97f), (64.3f, 235.7f), (28.73f, 271.27f), (155f, 145f));
        AssertLines(tree, laid, "justify", (40f, 260f), (0f, 300f), (0f, 300f), (0f, 145f));
        AssertLines(tree, laid, "negative", (19.88f, 280.13f), (32.3f, 267.7f), (45.61f, 254.39f), (257.3f, 42.7f));
    }

    /// <summary>
    /// A right-to-left paragraph starts at the right: start and the initial value are right,
    /// end is left, left and right are physical, and a Latin paragraph keeps its words in order.
    /// </summary>
    [Fact]
    public void RightToLeftLinesStartAtTheRight()
    {
        (DomTree tree, DomLayout laid) = Lay($"""
            <div dir=rtl>
            <div class=c><span id=start>{T}</span></div>
            <div class=c style="text-align:end"><span id=end>{T}</span></div>
            <div class=c style="text-align:left"><span id=left>{T}</span></div>
            <div class=c style="text-align:center"><span id=center>{T}</span></div>
            </div>
            <div style="direction:rtl"><div class=c><span id=css>{T}</span></div></div>
            """);
        AssertLines(tree, laid, "start", (999.88f, 280.13f), (1012.3f, 267.7f), (1025.61f, 254.39f), (1237.3f, 42.7f));
        AssertLines(tree, laid, "end", (980f, 280.13f), (980f, 267.7f), (980f, 254.39f), (980f, 42.7f));
        AssertLines(tree, laid, "left", (980f, 280.13f), (980f, 267.7f), (980f, 254.39f), (980f, 42.7f));
        AssertLines(tree, laid, "center", (989.94f, 280.13f), (996.14f, 267.7f), (1002.8f, 254.39f), (1108.64f, 42.7f));
        AssertLines(tree, laid, "css", (999.88f, 280.13f), (1012.3f, 267.7f), (1025.61f, 254.39f), (1237.3f, 42.7f));
    }

    [Fact]
    public void HebrewParagraphsAlignInTheirDirection()
    {
        (DomTree tree, DomLayout laid) = Lay($"""
            <div dir=rtl class=he>
            <div class=c style="text-align:center"><span id=center>{HE}</span></div>
            <div class=c style="text-align:left"><span id=left>{HE}</span></div>
            </div>
            """);
        AssertLines(tree, laid, "center", (985.94f, 288.11f), (998.23f, 263.52f), (1076.38f, 107.23f));
        AssertLines(tree, laid, "left", (980f, 288.11f), (980f, 263.52f), (980f, 107.23f));
    }

    /// <summary>
    /// The paragraph direction is the block's, not its first strong character's: a left-to-
    /// right line that opens with Hebrew starts at the left. A span gets one fragment per bidi
    /// run on a line, left to right, and a run of Hebrew keeps its spaces with it.
    /// </summary>
    [Fact]
    public void ParagraphDirectionComesFromTheBlock()
    {
        (DomTree tree, DomLayout laid) = Lay("""
            <div class=he>
            <div class=c dir=rtl><span id=mixed>שלום עולם hello world זהו משפט עם English words בתוכו וגם מספרים 12345 בסוף</span></div>
            <div class=c dir=ltr><span id=d1>שלום עולם</span> <span id=d2>hello</span></div>
            <div class=c dir=ltr style="text-align:right"><span id=e1>שלום עולם hello</span></div>
            </div>
            """);
        AssertLines(
            tree,
            laid,
            "mixed",
            (33.7f, 98.81f),
            (132.52f, 87.81f),
            (220.33f, 79.67f),
            (5.23f, 50.91f),
            (56.14f, 133.33f),
            (189.47f, 110.53f),
            (265.77f, 34.23f));
        AssertLines(tree, laid, "d1", (0f, 74.58f));
        AssertLines(tree, laid, "d2", (79.67f, 38.67f));
        AssertLines(tree, laid, "e1", (181.67f, 74.58f), (256.25f, 43.75f));
    }

    /// <summary>Right to left, the indent and the floats sit on the other side.</summary>
    [Fact]
    public void RightToLeftIndentAndFloatsMirror()
    {
        (DomTree tree, DomLayout laid) = Lay($"""
            <div dir=rtl>
            <div class=c style="text-indent:40px"><span id=indent>{T}</span></div>
            <div class=c style="text-indent:40px;text-align:center"><span id=centered>{T}</span></div>
            <div class=c><div class=f style="float:left"></div><span id=floated>{T}</span></div>
            </div>
            """);
        AssertLines(tree, laid, "indent", (1047.03f, 192.97f), (1044.3f, 235.7f), (1008.73f, 271.27f), (1135f, 145f));
        AssertLines(tree, laid, "centered", (1013.52f, 192.97f), (1012.14f, 235.7f), (994.36f, 271.27f), (1057.5f, 145f));
        AssertLines(tree, laid, "floated", (1087.03f, 192.97f), (1044.3f, 235.7f), (1008.73f, 271.27f), (1135f, 145f));
    }

    /// <summary>
    /// An inline box's border and padding sit around its text on an aligned line, on the side
    /// its own direction starts and ends it.
    /// </summary>
    [Fact]
    public void InlineBoxEdgesFollowAlignmentAndDirection()
    {
        (DomTree tree, DomLayout laid) = Lay("""
            <div class=c dir=rtl><span id=b1 style="padding:0 6px;border:2px solid">alpha beta</span> <span id=b2>gamma</span></div>
            <div class="c he" dir=rtl><span id=e1 style="padding:0 6px;border:2px solid">שלום עולם</span> <span id=e2>זהו משפט</span></div>
            <div class="c he" dir=rtl style="text-align:center"><span>שלום</span> <span id=g2 style="padding:0 4px;margin:0 3px;border:1px solid">עולם זהו</span> <span>משפט</span></div>
            """);
        AssertLines(tree, laid, "b1", (151.45f, 90.73f));
        AssertLines(tree, laid, "b2", (246.64f, 53.36f));
        AssertLines(tree, laid, "e1", (209.42f, 90.58f));
        AssertLines(tree, laid, "e2", (136.31f, 68.02f));
        AssertLines(tree, laid, "g2", (118.81f, 69.53f));
    }

    /// <summary>A row of atomic inlines takes the same alignment and direction.</summary>
    [Fact]
    public void AtomicInlinesFollowAlignmentAndDirection()
    {
        (DomTree tree, DomLayout laid) = Lay("""
            <div class=c style="text-align:center"><span>alpha</span> <span class=ib id=ib1></span> <span>beta gamma</span></div>
            <div class=c style="text-align:right"><span class=ib id=ib2></span> <span class=ib id=ib3></span> </div>
            <div class="c he" dir=rtl>שלום <span class=ib id=ib4></span> עולם זהו</div>
            <div class="c he" dir=rtl style="text-align:left">שלום עולם <span class=ib id=ib5></span></div>
            <div class=c dir=rtl><img id=im2 width=30 height=20 style="vertical-align:top"> alpha</div>
            """);
        AssertBox(tree, laid, "ib1", 105.11f, 40f);
        AssertBox(tree, laid, "ib2", 215.55f, 40f);
        AssertBox(tree, laid, "ib3", 260f, 40f);
        AssertBox(tree, laid, "ib4", 219.5f, 40f);
        AssertBox(tree, laid, "ib5", 0f, 40f);
        AssertBox(tree, laid, "im2", 270f, 30f);
    }

    /// <summary>getComputedStyle reports the keyword, as Chromium does.</summary>
    [Fact]
    public void ComputedTextAlignIsTheKeyword()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <div id=left style="text-align:left"><p id=inherit></p></div>
            <div id=right style="text-align:right;text-align-last:center"><p id=last></p></div>
            <div id=justify style="text-align:justify"></div>
            <div id=initial></div>
            """);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (800f, 600f), null, new RenderResourceCache())!;
        string Align(string id, string property = "text-align") => prepared.ComputedStyle(Id(tree, id))![property];
        Assert.Equal("left", Align("left"));
        Assert.Equal("left", Align("inherit"));
        Assert.Equal("right", Align("right"));
        Assert.Equal("center", Align("last", "text-align-last"));
        Assert.Equal("justify", Align("justify"));
        Assert.Equal("start", Align("initial"));
        Assert.Equal("auto", Align("initial", "text-align-last"));
    }
}
