using PocketCalculator.Dom;
using PocketCalculator.Render.Css;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// A paragraph split into lines takes each split-off tail's first visual line from the layout
/// of the line it was cut from, and shapes each line by copying the whole line's words
/// (<c>TextEngine.LineCarry</c>, <c>TextShaper.ShapeParagraphSlice</c>). Both are exact: the
/// whole render must be identical to laying out and shaping every tail afresh.
/// </summary>
public class LineCarryTests
{
    private static readonly (float Width, float Height) Viewport = (480f, 360f);

    public static TheoryData<string> Pages() =>
    [
        // Min-content of grid items: one word a line.
        "<div style='display:grid;grid-template-columns:repeat(4,1fr);gap:4px'>"
            + string.Concat(Enumerable.Range(0, 12).Select(i =>
                $"<div style='border:1px solid;padding:4px'><span>Item {i}</span> <em>some text that wraps a bit more {i}</em> <a href=#x>link</a></div>"))
            + "</div>",

        // Shrink-to-fit floats and inline-blocks around inline boxes with edges.
        "<div style='float:left'>float <b style='padding:0 6px;border:2px solid'>with padded</b> inline boxes <i style='margin:0 9px'>and margins</i> inside</div>"
            + "<span style='display:inline-block'>inline block <span style='padding-left:12px'>padded</span> text</span>",

        // Negative inline margins and text indents, positive and negative.
        "<div style='width:min-content'>neg <span style='margin-left:-20px'>margin span</span> words here</div>"
            + "<p style='width:200px;text-indent:30px'>indented paragraph with enough words to wrap over several lines of text</p>"
            + "<p style='width:200px;text-indent:-15px;padding-left:15px'>hanging indent paragraph with enough words to wrap over several lines</p>",

        // Justified text with inline box edges, center and right alignment.
        "<p style='width:180px;text-align:justify'>justified text <span style='padding:0 5px;border:1px solid'>with a box</span> in it and more words to wrap the line</p>"
            + "<p style='width:180px;text-align:center'>centred text that wraps onto a second and a third line</p>"
            + "<p style='width:180px;text-align:right'>right aligned text that wraps onto a second and a third line</p>",

        // Break opportunities that are not spaces: hyphens, soft hyphens, slashes, no-break spaces.
        "<div style='width:min-content'>state-of-the-art soft­hyphen­ated a/b/c and no break words</div>"
            + "<p style='width:90px;hyphens:manual'>super­cali­fragi­listic expi­ali­docious</p>",

        // Emergency breaks inside words.
        "<div style='width:min-content;overflow-wrap:anywhere'>averyveryverylongwordwithoutbreaks and short</div>"
            + "<div style='width:60px;word-break:break-all'>breakallbreakall words split anywhere</div>"
            + "<div style='width:60px;overflow-wrap:break-word'>breakwordbreakword fits</div>",

        // Tabs (their advance depends on the position in the paragraph), mixed sizes, spacing.
        "<pre style='white-space:pre-wrap;width:120px'>a\tb\tc\tddd eee\tfff ggg hhh</pre>"
            + "<p style='width:150px;letter-spacing:2px;word-spacing:4px'>spaced <big>big</big> and <small>small</small> words that wrap</p>",

        // Right-to-left and mixed text, which never carry.
        "<p style='width:120px' dir=rtl>שלום עולם english words שלום</p>"
            + "<div style='width:min-content'>abc אבג def</div>",

        // A long paragraph (probed in windows) measured at min-content and laid out narrow.
        "<div style='display:flex'><div style='flex:0 1 auto;min-width:min-content'>"
            + string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet consectetur ", 20))
            + "</div><div style='width:300px'>x</div></div>",

        // Floats beside text, forced breaks, nowrap runs.
        "<div style='width:260px'><div style='float:left;width:80px;height:40px'></div>text beside the float that wraps around it and below it<br>after a break <span style='white-space:nowrap'>no wrap run of words</span> end</div>",
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public void CarriedLinesAndShapeSlicesLayOutExactlyAsFreshOnes(string body)
    {
        string html = "<!doctype html><html><head><style>body{font:14px/1.3 sans-serif;margin:6px}</style></head><body>"
            + body + "</body></html>";
        DomTree tree = HtmlParsing.ParseHtml(html);

        string carried;
        string fresh;
        lock (Gate)
        {
            bool verify = TextShaper.VerifySlices;
            try
            {
                TextShaper.VerifySlices = true;
                (long _, long mismatchesBefore) = TextShaper.SliceCounts;
                carried = IncrementalLayoutDifferentialTests.Snapshot(tree, Prepare(tree));
                Assert.Equal(mismatchesBefore, TextShaper.SliceCounts.Mismatches);

                TextEngine.LineCarryDisabled = true;
                fresh = IncrementalLayoutDifferentialTests.Snapshot(tree, Prepare(tree));
            }
            finally
            {
                TextEngine.LineCarryDisabled = false;
                TextShaper.VerifySlices = verify;
            }
        }

        Assert.Equal(fresh, carried);
    }

    /// <summary>
    /// A min-content measurement of a paragraph with inline boxes shapes it once, not once per word.
    /// </summary>
    [Fact]
    public void AMinContentMeasurementShapesItsWordsAsSlices()
    {
        string html = "<!doctype html><html><body><div style='width:min-content'>one <b>two</b> three four five six seven eight</div></body></html>";
        DomTree tree = HtmlParsing.ParseHtml(html);
        lock (Gate)
        {
            (long before, _) = TextShaper.SliceCounts;
            Prepare(tree);
            Assert.True(TextShaper.SliceCounts.Slices - before >= 7, $"slices {TextShaper.SliceCounts.Slices - before}");
        }
    }

    // The two switches are process-wide; the theory cases flip them one at a time.
    private static readonly Lock Gate = new();

    private static PreparedRender Prepare(DomTree tree) =>
        RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(
            tree, Viewport, null, new RenderResourceCache(), [], new StylesheetCache())
        ?? throw new InvalidOperationException("did not prepare");
}
