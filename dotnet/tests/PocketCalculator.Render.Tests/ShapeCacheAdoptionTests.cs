using PocketCalculator.Dom;
using PocketCalculator.Render;
using PocketCalculator.Render.Css;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// A relayout takes over the previous pass's shaped paragraphs when both passes were built from
/// the same fonts, including on a page whose text is set in an <c>@font-face</c> web font.
/// </summary>
public sealed class ShapeCacheAdoptionTests
{
    private static WebFont Face(byte[] data, string family = "Fixture") =>
        new() { Data = data, Family = family, Weight = (400, 400), Italic = false };

    [Fact]
    public void AFontSetMatchesByDecodedBytesAndDescriptors()
    {
        byte[] serif = FontAssets.Load("liberation-serif");
        var cache = new ShapeCache([Face(serif)]);

        // CollectWebFonts builds new WebFont objects on every pass around the same memoized bytes.
        Assert.True(cache.MatchesFontSet([Face(serif)]));
        Assert.False(cache.MatchesFontSet([Face([.. serif])]));
        Assert.False(cache.MatchesFontSet([Face(serif, "Other")]));
        Assert.False(cache.MatchesFontSet([new WebFont { Data = serif, Family = "Fixture", Weight = (700, 700), Italic = false }]));
        Assert.False(cache.MatchesFontSet([Face(serif), Face(serif, "Other")]));
        Assert.False(cache.MatchesFontSet([]));
    }

    [Fact]
    public void ARelayoutOfAWebFontPageReusesTheShapedText()
    {
        DomTree tree = HtmlParsing.ParseHtml("""
            <html><head><style>
            @font-face { font-family: Fixture; src: url(/fonts/fixture.ttf) format('truetype'); }
            body { margin: 0; font: 16px Fixture }
            </style></head><body>
            <p id="text">The quick brown fox jumps over the lazy dog.</p><div id="box" style="height:10px"></div>
            </body></html>
            """);
        byte[] serif = FontAssets.Load("liberation-serif");
        RenderResourceCache resources = RenderResourceCache.WithLoader(
            url => url == "https://example.test/fonts/fixture.ttf" ? serif : null);
        StylesheetCache stylesheets = new();
        const string BaseUrl = "https://example.test/index.html";
        PreparedRender first = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(
            tree, (400f, 300f), BaseUrl, resources, [], stylesheets)!;
        // The web font arrived during that pass (the loader is synchronous); lay out once more
        // so both passes below see the same font set.
        first = RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(
            tree, (400f, 300f), BaseUrl, resources, [], stylesheets)!;
        (int entries, _, int misses) = first.Layout.TextEngine.ShapeCacheStats;
        Assert.True(entries > 0);

        NodeId box = tree.GetElementById("box")!.Value;
        tree.GetNode(box)!.SetAttribute("style", "height:20px");
        PreparedRender second = RenderPaint.PrepareDomWithRetainedStyles(
            tree,
            (400f, 300f),
            BaseUrl,
            resources,
            [],
            stylesheets,
            first,
            [RetainedStyleMutation.From(new AttributeStyleMutation(box, "style", null, null))])!;

        (int _, int hits, int missesAfter) = second.Layout.TextEngine.ShapeCacheStats;
        // The paragraph is unchanged, so the relayout either takes over its inline item outright
        // (RetainedTaffyLayout) or shapes it again through the carried-over cache.
        Assert.True(
            hits > 0 || second.Layout.AdoptedInlineItems > 0,
            "the relayout reshaped text the previous pass had shaped");
        Assert.Equal(misses, missesAfter);
        Assert.Equal(20f, second.DocumentRect(box)!.Value.Height);
    }

    [Fact]
    public void ACacheIsNotTakenOverAcrossAChangeInTheEmojiFace()
    {
        byte[] serif = FontAssets.Load("liberation-serif");
        var plain = new TextEngine([Face(serif)], loadEmoji: false);
        plain.AdoptShapeCache(null);
        var same = new TextEngine([Face(serif)], loadEmoji: false);
        same.AdoptShapeCache(plain);
        var emoji = new TextEngine([Face(serif)], loadEmoji: true);
        emoji.AdoptShapeCache(plain);

        // The emoji face loads ahead of the web fonts, so it renumbers their FontIds, which key
        // every shaped paragraph.
        Assert.Same(plain.CurrentShapeCache, same.CurrentShapeCache);
        Assert.NotSame(plain.CurrentShapeCache, emoji.CurrentShapeCache);
    }
}
