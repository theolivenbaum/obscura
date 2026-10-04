// The embedded CJK face (Noto Sans CJK SC Regular) and the Chromium behaviours CJK text
// exposes: fallback faces advance by whole pixels, join a `line-height: normal` line box, and are
// emboldened when they have no bold; ideographs, kana and fullwidth forms break like ID.
// None of this has a counterpart in crates/obscura-render, which embeds no CJK face; see
// "Known deviations" in todo.md.
//
// Every figure was measured on Chromium 141 with fontconfig limited to the faces this engine
// embeds (Liberation, DejaVu, Noto Color Emoji, Noto Sans CJK SC), so both sides draw from the
// same files.
using PocketCalculator.Dom;
using PocketCalculator.Render;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

public class CjkFontTests
{
    private const string Zh =
        "中文维基百科是维基百科的中文版本，是一个自由内容、公开编辑且多语言的网络百科全书协作计划。"
        + "它的目标是建立一个全面、准确、中立的百科全书。";

    private const string HostStyle = """
        <style>
            html,body{margin:0;padding:0}
            body{font-family:'Liberation Sans';font-size:16px}
            .p{width:300px}
            .n{width:80px}
        </style>
        """;

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    private static DomLayout Lay(DomTree tree) => RenderDom.LayoutDom(tree, (1000f, 2000f));

    private static FaceRecord? CjkFace(TextEngine engine)
    {
        foreach (FaceRecord face in engine.Database.Faces)
        {
            if (face.FamilyName == FontAssets.CjkFamily)
            {
                return face;
            }
        }

        return null;
    }

    private static List<LayoutGlyph> Glyphs(TextEngine engine, int item)
    {
        List<LayoutGlyph> glyphs = [];
        foreach (LayoutRun run in engine.Items[item].Buffer.LayoutRuns())
        {
            glyphs.AddRange(run.Glyphs);
        }

        return glyphs;
    }

    private static (TextEngine Engine, int Item) Shape(string text, string family, ushort weight = 400)
    {
        var engine = new TextEngine([], loadEmoji: false, loadCjk: true);
        DomTree tree = HtmlParsing.ParseHtml($"<p id='t'>{text}</p>");
        NodeId node = Id(tree, "t");
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontFamily = family,
            FontSize = 16f,
            FontWeight = weight.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        int item = engine.TryBuild(tree, node, new Dictionary<NodeId, LayoutStyle> { [node] = style })
            ?? throw new Xunit.Sdk.XunitException("CJK text shapes");
        engine.Measure(item, 10000f);
        return (engine, item);
    }

    [Fact]
    public void TheEmbeddedFaceCoversHanKanaHangulAndFullwidthForms()
    {
        using var engine = new TextEngine([], loadEmoji: false, loadCjk: true);
        FaceRecord face = CjkFace(engine) ?? throw new Xunit.Sdk.XunitException("CJK face loaded");
        Assert.Equal(1000f, face.UnitsPerEm);
        Assert.Equal(400, face.Weight);
        foreach (char ch in "中文漢字あいうアイウ한국어，。、「」：ＡＢ１２ㄅ")
        {
            Assert.True(face.ContainsCodepoint(ch), $"U+{(int)ch:X4} has a glyph");
        }

        // Extension A.
        Assert.True(face.ContainsCodepoint(0x3400));
        Assert.Equal(new FaceMetrics(1160f, 288f, 0f, 1000f, XHeight: 543f), FontAssets.BundledFaceMetrics(FontAssets.CjkFamily));
        Assert.Equal(face.Metrics.Ascent, FontAssets.BundledFaceMetrics(FontAssets.CjkFamily).Ascent);
        Assert.Equal(face.Metrics.Descent, FontAssets.BundledFaceMetrics(FontAssets.CjkFamily).Descent);
    }

    [Fact]
    public void CjkFallsBackToTheEmbeddedFaceWithRealGlyphsAndWholePixelAdvances()
    {
        (TextEngine engine, int item) = Shape("中あア，한A", "'Liberation Sans'");
        using (engine)
        {
            FontId cjk = CjkFace(engine)!.Id;
            List<LayoutGlyph> glyphs = Glyphs(engine, item);
            Assert.Equal(6, glyphs.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(cjk, glyphs[i].FontId);
                Assert.NotEqual(0, glyphs[i].GlyphId);
            }

            // Ideographs and kana are 1em.
            Assert.Equal(16f, glyphs[0].W, 3);
            Assert.Equal(16f, glyphs[1].W, 3);

            // Hangul is 920 units, 14.72px; Chromium advances a fallback glyph by whole pixels.
            Assert.Equal(15f, glyphs[4].W, 3);

            // The face kerns katakana against fullwidth punctuation (ア，is 30px, not 32, in
            // Chromium too); the five CJK glyphs together are 77px in Chromium 141.
            float cjkWidth = 0f;
            for (int i = 0; i < 5; i++)
            {
                cjkWidth += glyphs[i].W;
            }

            Assert.Equal(77f, cjkWidth, 3);

            // The Latin letter stays on the primary face.
            Assert.NotEqual(cjk, glyphs[5].FontId);
        }
    }

    [Fact]
    public void ThePrimaryCjkFaceKeepsFractionalAdvances()
    {
        (TextEngine engine, int item) = Shape("한한한한한한한한한한", "'Noto Sans CJK SC'");
        using (engine)
        {
            float width = 0f;
            foreach (LayoutGlyph glyph in Glyphs(engine, item))
            {
                width += glyph.W;
            }

            // Chromium 141: 147.203 with Noto Sans CJK SC named; 150 when it is a fallback.
            Assert.Equal(147.2f, width, 2);
        }
    }

    [Fact]
    public void SyntheticBoldFollowsChromiumsThresholds()
    {
        static bool Bold(string family, ushort weight)
        {
            (TextEngine engine, int item) = Shape("中", family, weight);
            using (engine)
            {
                LayoutGlyph glyph = Glyphs(engine, item)[0];
                Assert.Equal(16f, glyph.W, 3);
                return glyph.Physical((0f, 0f), 1f).CacheKey.FakeBold;
            }
        }

        // Ink measured on Chromium 141: as a fallback face, 600 is already emboldened; as the
        // named family, 600 is regular and 700 bold. The advance never changes.
        Assert.False(Bold("'Liberation Sans'", 500));
        Assert.True(Bold("'Liberation Sans'", 600));
        Assert.True(Bold("'Liberation Sans'", 700));
        Assert.False(Bold("'Noto Sans CJK SC'", 600));
        Assert.True(Bold("'Noto Sans CJK SC'", 700));
    }

    [Fact]
    public void ALatinOnlyLayoutDoesNotLoadTheCjkFace()
    {
        DomTree latin = HtmlParsing.ParseHtml(HostStyle + "<p>Plain Latin text, nothing else.</p>");
        Assert.Null(CjkFace(Lay(latin).TextEngine));

        DomTree han = HtmlParsing.ParseHtml(HostStyle + "<p>Latin and 中文</p>");
        Assert.NotNull(CjkFace(Lay(han).TextEngine));

        DomTree generated = HtmlParsing.ParseHtml(
            HostStyle + "<style>p::after{content:'한국어'}</style><p>Latin</p>");
        Assert.NotNull(CjkFace(Lay(generated).TextEngine));

        // Naming the face selects its own Latin glyphs, so it loads without any CJK text.
        DomTree named = HtmlParsing.ParseHtml(
            HostStyle + "<p style=\"font-family:'Noto Sans CJK SC', serif\">Latin text</p>");
        Assert.NotNull(CjkFace(Lay(named).TextEngine));

        Assert.False(FontAssets.TextMayNeedCjkFont("Plain text, arrows -> and emoji ✓"));
        Assert.True(FontAssets.TextMayNeedCjkFont("ＡＢＣ"));
        Assert.True(FontAssets.TextMayNeedCjkFont("𠀋"));
    }

    [Fact]
    public void CjkFamilyNamesResolveTheWayChromiumDoes()
    {
        Assert.Equal(FontAssets.CjkFamily, FontAssets.BundledFamilyForCssToken("'Noto Sans CJK SC'"));
        Assert.Equal(FontAssets.CjkFamily, FontAssets.BundledFamilyForCssToken("Noto Sans CJK JP"));
        Assert.Equal(FontAssets.CjkFamily, FontAssets.BundledFamilyForCssToken("\"noto sans cjk kr\""));

        // Chromium falls through these to the next family; their CJK glyphs come by fallback.
        Assert.Null(FontAssets.BundledFamilyForCssToken("Microsoft YaHei"));
        Assert.Null(FontAssets.BundledFamilyForCssToken("PingFang SC"));
        Assert.Null(FontAssets.BundledFamilyForCssToken("SimSun"));
        Assert.Null(FontAssets.BundledFamilyForCssToken("Meiryo"));
        Assert.Equal(FontAssets.SerifFamily, FontAssets.ResolveFontFamily("'Microsoft YaHei', serif"));

        // Chromium 141: "Latin text" is 69.33px wide and 24px tall in the named face, and 61.77px
        // (Liberation Serif) when the first family is one it does not have.
        DomTree tree = HtmlParsing.ParseHtml(
            HostStyle
            + "<div style='white-space:nowrap'>"
            + "<span id='noto' style=\"font-family:'Noto Sans CJK SC', serif\">Latin text</span> "
            + "<span id='yahei' style=\"font-family:'Microsoft YaHei', serif\">Latin text</span>"
            + "</div>");
        DomLayout laid = Lay(tree);
        Assert.Equal(69.33f, laid.Rects[Id(tree, "noto")].Width, 0.05f);
        Assert.Equal(24f, laid.Rects[Id(tree, "noto")].Height, 2);
        Assert.Equal(61.77f, laid.Rects[Id(tree, "yahei")].Width, 0.05f);
    }

    /// <summary>
    /// Blink unites a fallback face's leaded metrics into a <c>line-height: normal</c> line box:
    /// Noto Sans CJK SC is 19 up and 5 down at 16px, against Liberation Sans's 14 and 4.
    /// </summary>
    [Fact]
    public void CjkLinesTakeTheFallbackFacesLineHeight()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            HostStyle
            + $"<div class='p' id='zh16'>{Zh}</div>"
            + $"<div class='p' id='zh12' style='font-size:12px'>{Zh}</div>"
            + "<div class='p' id='zh24' style='font-size:24px'>中文维基百科是维基百科的中文版本，是一个自由内容、公开编辑且多语言的网络百科全书协作计划。</div>"
            + "<div class='p' id='ko16'>한국어 위키백과는 위키백과의 한국어판입니다. 누구나 편집할 수 있는 자유 백과사전이며 문서의 수는 육십만 개를 넘었습니다.</div>"
            + "<div class='p' id='mix16'>Hello world 你好世界 mixed Latin and 汉字 text on one line.</div>"
            + "<div class='p' id='latin16'>Hello world plain Latin.</div>"
            + "<div class='p' id='lh20' style='line-height:20px'>中文维基百科是维基百科的中文版本，是一个自由内容、公开编辑且多语言的网络。</div>"
            + "<div class='p' id='lh1' style='line-height:1'>中文维基百科是维基百科的中文版本，是一个自由内容、公开编辑且多语言的网络。</div>"
            + "<div class='p' id='span20' style='line-height:20px'>Latin <span style='line-height:normal'>中文</span> text</div>"
            + "<div class='p' id='dejavu'>Hello ✓ world</div>");
        DomLayout laid = Lay(tree);

        // Chromium 141: 4 x 24, 3 x 17, 4 x 35, 3 x 24, 2 x 24, 18; a fixed line-height is
        // unaffected (3 x 20, 3 x 16), an inner normal box still unites (24), and the same rule
        // applies to a DejaVu fallback glyph (19).
        Assert.Equal(96f, laid.Rects[Id(tree, "zh16")].Height, 2);
        Assert.Equal(51f, laid.Rects[Id(tree, "zh12")].Height, 2);
        Assert.Equal(140f, laid.Rects[Id(tree, "zh24")].Height, 2);
        Assert.Equal(72f, laid.Rects[Id(tree, "ko16")].Height, 2);
        Assert.Equal(48f, laid.Rects[Id(tree, "mix16")].Height, 2);
        Assert.Equal(18f, laid.Rects[Id(tree, "latin16")].Height, 2);
        Assert.Equal(60f, laid.Rects[Id(tree, "lh20")].Height, 2);
        Assert.Equal(48f, laid.Rects[Id(tree, "lh1")].Height, 2);
        Assert.Equal(24f, laid.Rects[Id(tree, "span20")].Height, 2);
        Assert.Equal(19f, laid.Rects[Id(tree, "dejavu")].Height, 2);
    }

    /// <summary>The line each character of <paramref name="text"/> lands on, as "abc|def".</summary>
    private static string Wrapped(string text, string width)
    {
        string spans = string.Concat(text.Select((ch, i) => ch == ' ' ? " " : $"<span id='c{i}'>{ch}</span>"));
        DomTree tree = HtmlParsing.ParseHtml(HostStyle + $"<div style='width:{width}'>{spans}</div>");
        DomLayout laid = Lay(tree);
        var lines = new System.Text.StringBuilder();
        float? top = null;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                continue;
            }

            float y = laid.Rects[Id(tree, $"c{i}")].Y;
            if (top is { } previous && MathF.Abs(y - previous) > 3f)
            {
                lines.Append('|');
            }

            top = y;
            lines.Append(text[i]);
        }

        return lines.ToString();
    }

    [Fact]
    public void ChineseWrapsBetweenIdeographsWithKinsoku()
    {
        // Chromium 141 at 300px, 16px Liberation Sans with the CJK face as fallback.
        Assert.Equal(
            "中文维基百科是维基百科的中文版本，是|一个自由内容、公开编辑且多语言的网络|百科全书协作计划。它的目标是建立一个|全面、准确、中立的百科全书。",
            Wrapped(Zh, "300px"));

        // An ideographic full stop may end a full line; a closing bracket may not start one, so
        // its opening bracket and the ideograph between move down together.
        Assert.Equal(
            "一二三四五六七八九。|一二三四五六七八九、|一二三四五六七八|「九」",
            Wrapped("一二三四五六七八九。一二三四五六七八九、一二三四五六七八「九」", "160px"));
    }

    [Fact]
    public void KanaAndFullwidthFormsBreakLikeChromium()
    {
        // Small kana and the prolonged sound mark may start a line (CSS line-break: auto, which
        // Chromium resolves like UAX#14's "normal" tailoring), the fullwidth colon, semicolon,
        // katakana middle dot and wave dash may not, and fullwidth letters, digits, bopomofo
        // and the enclosed ideographs break between each pair. Chromium 141 at 80px.
        Assert.Equal("一二三四ア|ー六七", Wrapped("一二三四アー六七", "80px"));
        Assert.Equal("一二三四ア|ァ六七", Wrapped("一二三四アァ六七", "80px"));
        Assert.Equal("一二三四ㇰ|ㇱ六七", Wrapped("一二三四ㇰㇱ六七", "80px"));
        Assert.Equal("一二三四|五：六七", Wrapped("一二三四五：六七", "80px"));
        Assert.Equal("一二三四|五；六七", Wrapped("一二三四五；六七", "80px"));
        Assert.Equal("一二三四|五・六七", Wrapped("一二三四五・六七", "80px"));
        Assert.Equal("一二三四|五〜六七", Wrapped("一二三四五〜六七", "80px"));
        Assert.Equal("一二三四|五゛六七", Wrapped("一二三四五゛六七", "80px"));
        Assert.Equal("一二三四１|２３４", Wrapped("一二三四１２３４", "80px"));
        Assert.Equal("一二三四Ａ|ＢＣＤ", Wrapped("一二三四ＡＢＣＤ", "80px"));
        Assert.Equal("一二三四ㄅ|ㄆㄇㄈ", Wrapped("一二三四ㄅㄆㄇㄈ", "80px"));
        Assert.Equal("一二三四〇|〇〇〇", Wrapped("一二三四〇〇〇〇", "80px"));
        Assert.Equal("一二三四|（五六七", Wrapped("一二三四（五六七", "80px"));
        Assert.Equal("一二三四|五）六七", Wrapped("一二三四五）六七", "80px"));
    }

    [Fact]
    public void HangulBreaksBetweenSyllablesAtWholePixelAdvances()
    {
        // Chromium 141: the fallback Hangul syllables are 15px each, which is what puts 니|다
        // across the first break.
        Assert.Equal(
            "한국어위키백과는위키백과의한국어판입니|다.누구나편집할수있는자유백과사전이|며문서의수는육십만개를넘었습니다.",
            Wrapped("한국어 위키백과는 위키백과의 한국어판입니다. 누구나 편집할 수 있는 자유 백과사전이며 문서의 수는 육십만 개를 넘었습니다.", "300px"));
    }
}
