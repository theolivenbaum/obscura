// `ch` and `ex` are defined against the element's own first available font, which
// crates/obscura-render has no notion of: style.rs scales the font size by one constant.
using System.Runtime.CompilerServices;
using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The font-relative CSS units that are measured on a face rather than computed from the font
/// size: <c>ch</c> (the advance of <c>0</c>) and <c>ex</c> (the x-height).
/// </summary>
/// <remarks>
/// Every expected number is a Chromium 141 measurement, taken over HTTP because a webfont does
/// not load over <c>file://</c> and every face silently falls back to Liberation Sans there.
/// The bundled-face numbers come from <c>width: 1ch</c> / <c>1ex</c> at <c>font-size: 1024px</c>,
/// which pins the em fraction to a sixteen-thousandth.
/// </remarks>
public class FontRelativeUnitTests
{
    private const float Tolerance = 0.01f;

    // Chromium 141, `width: 1ch` at font-size 1024px: 569.5 / 614.5 / 512 px.
    private const float SansChPerEm = 1139f / 2048f;
    private const float MonoChPerEm = 1229f / 2048f;
    private const float SerifChPerEm = 0.5f;

    // ...and `width: 1ex`: 541 / 541 / 470 px.
    private const float SansExPerEm = 1082f / 2048f;
    private const float SerifExPerEm = 940f / 2048f;

    private static string FontFixtures([CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "fonts");

    private static byte[] VariableFace() => File.ReadAllBytes(
        Path.Combine(FontFixtures(), "NotoSansArabic.ttf"));

    private static float ResolvedWidth(string css, string id = "probe")
    {
        DomTree tree = HtmlParsing.ParseHtml(
            $"<style>{css}</style><div id='{id}'>0</div>");
        NodeId node = tree.GetElementById(id)!.Value;
        DomLayout laid = RenderDom.LayoutDom(tree, (4000f, 1000f));
        Dimension width = laid.Styles[node].Width;
        Assert.Equal(DimensionKind.Px, width.Kind);
        return width.Value;
    }

    [Fact]
    public void ChIsTheSelectedFacesZeroAdvanceAndNotOneConstant()
    {
        const string Rule = "#probe{font-size:100px;width:10ch;font-family:";
        float sans = ResolvedWidth(Rule + "'Liberation Sans'}");
        float mono = ResolvedWidth(Rule + "'Liberation Mono'}");
        float serif = ResolvedWidth(Rule + "'Liberation Serif'}");

        Assert.Equal(1000f * SansChPerEm, sans, Tolerance);
        Assert.Equal(1000f * MonoChPerEm, mono, Tolerance);
        Assert.Equal(1000f * SerifChPerEm, serif, Tolerance);
        Assert.True(mono > sans && sans > serif, $"three faces, three widths: {serif} {sans} {mono}");
    }

    [Fact]
    public void ExIsTheSelectedFacesXHeightAndNotOneConstant()
    {
        const string Rule = "#probe{font-size:100px;width:10ex;font-family:";
        float sans = ResolvedWidth(Rule + "'Liberation Sans'}");
        float serif = ResolvedWidth(Rule + "'Liberation Serif'}");

        Assert.Equal(1000f * SansExPerEm, sans, Tolerance);
        Assert.Equal(1000f * SerifExPerEm, serif, Tolerance);
    }

    [Fact]
    public void AGenericFamilyKeywordPicksTheSameFaceAsItsName()
    {
        Assert.Equal(
            ResolvedWidth("#probe{font-size:100px;width:10ch;font-family:'Liberation Mono'}"),
            ResolvedWidth("#probe{font-size:100px;width:10ch;font-family:monospace}"),
            Tolerance);
        Assert.Equal(
            ResolvedWidth("#probe{font-size:100px;width:10ch;font-family:'Liberation Serif'}"),
            ResolvedWidth("#probe{font-size:100px;width:10ch;font-family:serif}"),
            Tolerance);
    }

    [Fact]
    public void ChAndExScaleWithTheFontSizeAndStayOnTheSameFace()
    {
        float atTwenty = ResolvedWidth("#probe{font-size:20px;width:10ch;font-family:'Liberation Mono'}");
        float atEighty = ResolvedWidth("#probe{font-size:80px;width:10ch;font-family:'Liberation Mono'}");

        Assert.Equal(200f * MonoChPerEm, atTwenty, Tolerance);
        Assert.Equal(800f * MonoChPerEm, atEighty, Tolerance);
    }

    [Fact]
    public void AnElementsOwnFamilyWinsOverTheInheritedOne()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<style>#outer{font-family:'Liberation Sans';font-size:100px}"
            + "#inner{font-family:'Liberation Mono';width:10ch}</style>"
            + "<div id='outer'><div id='inner'>0</div></div>");
        NodeId inner = tree.GetElementById("inner")!.Value;
        DomLayout laid = RenderDom.LayoutDom(tree, (4000f, 1000f));

        Assert.Equal(1000f * MonoChPerEm, laid.Styles[inner].Width.Value, Tolerance);
    }

    [Fact]
    public void EmAndRemAndViewportUnitsAreUnaffected()
    {
        Assert.Equal(
            1000f,
            ResolvedWidth("#probe{font-size:100px;width:10em;font-family:'Liberation Mono'}"),
            Tolerance);
        Assert.Equal(
            160f,
            ResolvedWidth("#probe{font-size:100px;width:10rem;font-family:'Liberation Mono'}"),
            Tolerance);
        Assert.Equal(
            400f,
            ResolvedWidth("#probe{font-size:100px;width:10vw;font-family:'Liberation Mono'}"),
            Tolerance);
        Assert.Equal(
            100f,
            ResolvedWidth("#probe{font-size:100px;width:10vh;font-family:'Liberation Mono'}"),
            Tolerance);
    }

    [Fact]
    public void ChInsideCalcReadsTheSameFace()
    {
        Assert.Equal(
            (1000f * MonoChPerEm) + 7f,
            ResolvedWidth("#probe{font-size:100px;width:calc(10ch + 7px);font-family:'Liberation Mono'}"),
            Tolerance);
    }

    [Fact]
    public void ChFollowsTheVariableWeightAxisRatherThanTheBaseInstance()
    {
        static float Width(ushort weight)
        {
            DomTree tree = HtmlParsing.ParseHtml(
                "<style>#probe{font-size:100px;width:10ch;font-family:'Probe';"
                + $"font-weight:{weight}}}</style><div id='probe'>0</div>");
            NodeId node = tree.GetElementById("probe")!.Value;
            DomLayout laid = RenderDom.LayoutDomWithWebFonts(
                tree,
                (4000f, 1000f),
                new Dictionary<NodeId, ReplacedIntrinsic>(),
                [new WebFont { Data = VariableFace(), Family = "Probe", Weight = (100, 900), Italic = false }]);
            return laid.Styles[node].Width.Value;
        }

        float light = Width(100);
        float black = Width(900);

        // A variable face's digit grows with `wght`, so reading the base instance - which is what
        // a naive per-family constant would do - is wrong on exactly the pages that use one.
        // Archivo, the case this was found on, is 0.573em at 400 and 0.625em at 800 in Chromium.
        Assert.True(black > light, $"wght 900 must be wider than wght 100: {light} vs {black}");
        Assert.True(light > 0f, "a measured face must produce a positive ch");
    }

    [Fact]
    public void TenChIsTheWidthOfTenZeroesInTheSameFace()
    {
        foreach (string family in new[] { "Liberation Sans", "Liberation Mono", "Liberation Serif" })
        {
            DomTree tree = HtmlParsing.ParseHtml(
                $"<style>#probe{{font-size:100px;font-family:'{family}';white-space:pre;"
                + "display:inline-block}</style><div id='probe'>0000000000</div>");
            NodeId node = tree.GetElementById("probe")!.Value;
            DomLayout shaped = RenderDom.LayoutDom(tree, (8000f, 1000f));
            float zeroes = shaped.Rects[node].Width;

            float declared = ResolvedWidth($"#probe{{font-size:100px;width:10ch;font-family:'{family}'}}");

            // CSS Values 4 says this in as many words: 1ch IS the advance of U+0030. Layout
            // rounds a used width to whole pixels, so allow the rounding and nothing more.
            Assert.True(
                MathF.Abs(zeroes - declared) <= 1f,
                $"{family}: ten zeroes measured {zeroes}, 10ch resolved to {declared}");
        }
    }

    [Fact]
    public void TransformTranslateResolvesFontRelativeUnitsAgainstTheElementsOwnFont()
    {
        static float TranslatedX(string declaration)
        {
            DomTree tree = HtmlParsing.ParseHtml(
                "<style>html,body{margin:0}#probe{font-size:100px;font-family:'Liberation Mono';"
                + $"width:10px;height:10px;transform:{declaration}}}</style><div id='probe'>0</div>");
            NodeId node = tree.GetElementById("probe")!.Value;
            DomLayout laid = RenderDom.LayoutDom(tree, (4000f, 1000f));

            // A transform is applied at paint time, so the laid-out rect does not carry it; this
            // is the function the painter and `getBoundingClientRect` both go through.
            return DomTransforms.ResolvedOwnTranslate(
                laid.Styles[node], laid.Rects[node], 16f, (4000f, 1000f)).Item1;
        }

        // Chromium 141 over HTTP, same fixture: 200 / 600.098 / 528.320.
        Assert.Equal(200f, TranslatedX("translateX(2em)"), Tolerance);
        Assert.Equal(1000f * MonoChPerEm, TranslatedX("translateX(10ch)"), Tolerance);
        Assert.Equal(1000f * SansExPerEm, TranslatedX("translateX(10ex)"), Tolerance);
    }

    /// <summary>
    /// <c>FontUnitResolver.AxisTuple</c> deliberately reproduces
    /// <c>TextShaping.ShapingVariations</c> rather than calling it, so that measuring a unit
    /// never has to build the inline layer's <c>TextAttrs</c>. The two must agree: if they
    /// drift, <c>ch</c> on a variable face silently stops matching the advance the shaper uses
    /// for the very same glyph, and nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void TheMeasuringAxisTupleMatchesTheShapingAxisTuple()
    {
        using TextEngine engine = new(
            [new WebFont { Data = VariableFace(), Family = "Probe", Weight = (100, 900), Italic = false }],
            loadEmoji: false);
        FontId id = engine.LoadedFamilies["probe"].Faces[0].FontId!.Value;
        FaceRecord face = engine.Database.Face(id)!;
        Assert.True(face.IsVariable, "the fixture must be a variable face for this to mean anything");

        List<FontVariationSetting> authored = [new FontVariationSetting("wdth", 75f)];
        foreach (ushort weight in new ushort[] { 100, 400, 700, 900 })
        {
            foreach (bool italic in new[] { false, true })
            {
                foreach (float? optical in new float?[] { null, 42f })
                {
                    foreach (List<FontVariationSetting>? settings in new[] { null, authored })
                    {
                        FontVariations? measuring = FontUnitResolver.AxisTuple(
                            face, weight, italic, optical, settings);
                        FontVariations? shaping = TextShaper.ShapingVariations(
                            face,
                            new TextAttrs
                            {
                                Family = "Probe",
                                FontWeightAxis = weight,
                                FontOpticalSize = optical,
                                FontItalicAxis = italic,
                                Variations = settings is null ? null : Tuple(settings),
                            });

                        Assert.Equal(shaping is null, measuring is null);
                        if (shaping is not null)
                        {
                            Assert.Equal(Describe(shaping), Describe(measuring!));
                        }
                    }
                }
            }
        }

        static FontVariations Tuple(List<FontVariationSetting> settings)
        {
            FontVariations variations = new();
            foreach (FontVariationSetting setting in settings)
            {
                variations.Set(VariationTag.FromAscii(setting.Tag), setting.Value);
            }

            return variations;
        }

        static string Describe(FontVariations variations)
        {
            List<string> parts = [];
            for (int i = 0; i < variations.Count; i++)
            {
                FontVariation item = variations.Items[i];
                parts.Add($"{item.Tag}={item.Value.Value}");
            }

            return string.Join(',', parts);
        }
    }
}
