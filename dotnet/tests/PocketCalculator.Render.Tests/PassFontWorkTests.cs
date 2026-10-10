using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The font work every layout pass repeats over the whole document - the <c>@font-face</c> rules
/// of every sheet, and the scan of every text node for the optional emoji and CJK faces - is
/// memoized by the identity of the text it reads, and so follows an edit of that text.
/// </summary>
public sealed class PassFontWorkTests
{
    private const string BaseUrl = "https://example.test/index.html";

    [Fact]
    public void AnEditedStyleSheetGetsItsNewFontFaces()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <html><head><style id="s">
            @font-face { font-family: Fixture; font-weight: 700; src: url(/fonts/fixture.ttf) format('truetype'); }
            body { font: 16px Fixture }
            </style></head><body><p>text</p></body></html>
            """);
        byte[] serif = FontAssets.Load("liberation-serif");
        RenderResourceCache resources = RenderResourceCache.WithLoader(
            url => url.StartsWith("https://example.test/fonts/", StringComparison.Ordinal) ? serif : null);

        List<WebFont> first = PaintFonts.CollectWebFonts(tree, BaseUrl, resources, []);
        List<WebFont> again = PaintFonts.CollectWebFonts(tree, BaseUrl, resources, []);
        WebFont face = Assert.Single(first);
        Assert.Equal("Fixture", face.Family);
        Assert.Equal(((ushort)700, (ushort)700), face.Weight);
        Assert.Equal("Fixture", Assert.Single(again).Family);

        NodeId style = tree.GetElementById("s")!.Value;
        NodeId text = tree.Children(style)[0];
        ((TextData)tree.GetNode(text)!.Data).Contents =
            "@font-face { font-family: Other; src: url(/fonts/other.ttf) format('truetype'); }";
        WebFont edited = Assert.Single(PaintFonts.CollectWebFonts(tree, BaseUrl, resources, []));
        Assert.Equal("Other", edited.Family);
        Assert.Null(edited.Weight);
    }

    [Fact]
    public void TheOptionalFaceScanOfALongTextFollowsItsContents()
    {
        string latin = new('a', 4000);
        string cjk = latin + "中";
        string emoji = latin + "☀";

        Assert.Equal((false, false), FontAssets.TextMayNeedOptionalFaces(latin));
        Assert.Equal((false, true), FontAssets.TextMayNeedOptionalFaces(cjk));
        Assert.Equal((false, true), FontAssets.TextMayNeedOptionalFaces(cjk));
        Assert.Equal((true, false), FontAssets.TextMayNeedOptionalFaces(emoji));

        // An equal string that is another instance answers the same.
        Assert.Equal((false, true), FontAssets.TextMayNeedOptionalFaces(new string(cjk.AsSpan())));
        Assert.Equal((false, false), FontAssets.TextMayNeedOptionalFaces("short é"));
        Assert.Equal((false, true), FontAssets.TextMayNeedOptionalFaces("short 中"));
    }
}
