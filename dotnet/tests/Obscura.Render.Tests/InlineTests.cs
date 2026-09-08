using System.Runtime.CompilerServices;
using Obscura.Dom;
using Obscura.Render.Layout;
using Xunit;
using NodeId = Obscura.Dom.NodeId;
using RgbaColor = Obscura.Render.Css.RgbaColor;

namespace Obscura.Render.Tests;

/// <summary>
/// xUnit port of the in-file <c>mod tests</c> of <c>crates/obscura-render/src/inline.rs</c>,
/// in source order with the Rust names preserved.
/// </summary>
public class InlineTests
{
    private static readonly RgbaColor Red = new(255, 0, 0, 255);
    private static readonly RgbaColor Blue = new(0, 0, 255, 255);

    private const string Family = FontAssets.SansFamily;
    private const string SerifFamily = FontAssets.SerifFamily;
    private const string MonoFamily = FontAssets.MonoFamily;
    private const string SystemFamily = FontAssets.SystemFamily;

    /// <summary>The Rust fixture uses the bundled DejaVu Sans as its stand-in webfont.</summary>
    private static byte[] Fallback => FontAssets.Load("dejavu-sans");

    private static byte[] SansRegular => FontAssets.Load("liberation-sans");

    /// <summary>The repository root, derived from this file's compile-time path.</summary>
    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    private static (TextEngine Engine, int Item) SurfaceCullFixture()
    {
        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>Visible text</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontSize = 16f,
            LineHeight = LineHeight.Px(20f),
        };
        var engine = new TextEngine();
        int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })!.Value;
        engine.Finalize(item, (0f, 10f), 200f, null);
        return (engine, item);
    }

    [Fact]
    public void TextSurfaceCullIsConservativeForOffsetsAndInkOverhang()
    {
        (TextEngine engine, int item) = SurfaceCullFixture();
        Assert.True(TextEngine.InlineItemMayIntersectSurface(engine.Items[item], (0f, 0f), null, 100, 1f));

        engine.Items[item].Origin = (engine.Items[item].Origin.X, -60f);
        Assert.True(
            TextEngine.InlineItemMayIntersectSurface(engine.Items[item], (0f, 0f), null, 100, 1f),
            "the four-em ink guard must preserve unusual glyph overhang");

        engine.Items[item].Origin = (engine.Items[item].Origin.X, -500f);
        Assert.False(TextEngine.InlineItemMayIntersectSurface(engine.Items[item], (0f, 0f), null, 100, 1f));

        engine.Items[item].Origin = (engine.Items[item].Origin.X, 500f);
        Assert.False(TextEngine.InlineItemMayIntersectSurface(engine.Items[item], (0f, 0f), null, 100, 1f));
        Assert.True(
            TextEngine.InlineItemMayIntersectSurface(engine.Items[item], (0f, -490f), null, 100, 1f),
            "the transform/layer translation must be applied before culling");

        engine.Items[item].RelativeOwnerRanges.Add(new RelativeOwnerTextRange(0, 1, (0f, -490f)));
        Assert.True(
            TextEngine.InlineItemMayIntersectSurface(engine.Items[item], (0f, 0f), null, 100, 1f),
            "a relatively positioned inline may move visible ink into the surface");

        Assert.False(TextEngine.InlineItemMayIntersectSurface(
            engine.Items[item],
            (0f, 0f),
            new Rect(0f, 200f, 100f, 20f),
            100,
            1f));
    }

    [Fact]
    public void SystemUiResolvesInStackOrderToChromiumLinuxFace()
    {
        const string Stack = "system-ui, -apple-system, \"Segoe UI\", Roboto, "
            + "\"Helvetica Neue\", \"Noto Sans\", \"Liberation Sans\", Arial, sans-serif";
        Assert.Equal(SystemFamily, FontAssets.ResolveFontFamily(Stack));

        using var engine = new TextEngine();
        ResolvedFont system = FontResolution.ResolveLoadedFont(Stack, 600, false, engine.LoadedFamilies);
        Assert.Equal(SystemFamily, system.Family);
        FaceRecord face = engine.Database.Face(system.FontId!.Value)!;
        Assert.True(
            face.Weight == 700,
            "CSS weight 600 selects DejaVu Sans Bold just as Chromium does");

        ResolvedFont italic = FontResolution.ResolveLoadedFont("system-ui", 400, true, engine.LoadedFamilies);
        FaceRecord italicFace = engine.Database.Face(italic.FontId!.Value)!;
        Assert.Equal(400, italicFace.Weight);
        Assert.True(
            italicFace.Style == FaceStyle.Normal,
            "Chromium synthesizes system-ui italic from DejaVu Sans regular on this host");

        ResolvedFont boldItalic = FontResolution.ResolveLoadedFont("system-ui", 700, true, engine.LoadedFamilies);
        FaceRecord boldItalicFace = engine.Database.Face(boldItalic.FontId!.Value)!;
        Assert.Equal(700, boldItalicFace.Weight);
        Assert.True(
            boldItalicFace.Style == FaceStyle.Normal,
            "Chromium synthesizes system-ui bold italic from DejaVu Sans Bold on this host");

        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>Italic system UI</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontFamily = "system-ui",
            FontStyleItalic = true,
            FontSize = 32f,
        };
        int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })
            ?? throw new Xunit.Sdk.XunitException("system-ui italic shapes");
        engine.Measure(item, 400f);
        LayoutGlyph glyph = FirstGlyph(engine, item);
        Assert.True(
            glyph.Physical((0f, 0f), 1f).CacheKey.FakeItalic,
            "the normal DejaVu resource must retain the requested synthetic slant");

        ResolvedFont arial = FontResolution.ResolveLoadedFont("Arial, sans-serif", 600, false, engine.LoadedFamilies);
        Assert.Equal(Family, arial.Family);
    }

    [Fact]
    public void NormalLineHeightGridFitsEachFaceMetric()
    {
        // Values measured from Chromium 145 using the same bundled Linux platform faces. Small
        // fractional sizes expose the difference from rounding a single 1.15 multiplier.
        Assert.Equal(10f, FontAssets.NormalLineHeight(9.3333f, FontAssets.BundledFaceMetrics(Family)));
        Assert.Equal(14f, FontAssets.NormalLineHeight(12f, FontAssets.BundledFaceMetrics(Family)));
        Assert.Equal(16f, FontAssets.NormalLineHeight(13f, FontAssets.BundledFaceMetrics(SerifFamily)));
        Assert.Equal(15f, FontAssets.NormalLineHeight(13f, FontAssets.BundledFaceMetrics(MonoFamily)));

        // Poppins's 1000-unit hhea metrics are 1050/-350 with a 100-unit gap. A 64px normal line
        // is therefore 67 + 22 + 6 = 95px, rather than the 74px produced by the old generic-sans
        // constants.
        Assert.Equal(95f, FontAssets.NormalLineHeight(64f, new FaceMetrics(1050f, 350f, 100f, 1000f)));
    }

    [Fact]
    public void DeclaredWebFamilyKeepsTheLoadedFacesLineMetrics()
    {
        using var engine = new TextEngine(
            [new WebFont { Data = Fallback, Family = "Page Face", Weight = (400, 400), Italic = false }],
            loadEmoji: false);
        ResolvedFont resolved = FontResolution.ResolveLoadedFont("Page Face", 400, false, engine.LoadedFamilies);
        FaceMetrics expected = engine.Database.Face(resolved.FontId!.Value)!.Metrics;
        Assert.Equal(expected, resolved.Metrics);

        var style = new LayoutStyle
        {
            FontFamily = "Page Face",
            FontSize = 64f,
            LineHeight = LineHeight.Normal,
        };
        Assert.Equal(
            FontAssets.NormalLineHeight(64f, expected),
            FontResolution.UsedLineHeightForFont(style, resolved));
    }

    [Fact]
    public void ReplacedPercentageMathOnlyZeroesInlineMinContent()
    {
        foreach (int expressionIndex in new[] { 0, 4 })
        {
            using var engine = new TextEngine();
            var style = new LayoutStyle();
            style.SizeExpressions[expressionIndex] = "calc(100% - 1px)";
            int item = engine.RegisterReplaced(800f, 400f, style);
            var unknown = new Size<float?>(null, null);
            Size<float> minContent = engine.MeasureTaffy(
                item,
                unknown,
                new Size<AvailableSpace>(AvailableSpace.MinContent, AvailableSpace.MaxContent));
            Size<float> maxContent = engine.MeasureTaffy(
                item,
                unknown,
                new Size<AvailableSpace>(AvailableSpace.MaxContent, AvailableSpace.MaxContent));
            Size<float> finalSize = engine.MeasureTaffy(
                item,
                new Size<float?>(300f, null),
                new Size<AvailableSpace>(AvailableSpace.Definite(300f), AvailableSpace.MaxContent));

            Assert.Equal(0f, minContent.Width);
            Assert.Equal(800f, maxContent.Width);
            Assert.Equal(400f, maxContent.Height);
            Assert.Equal(300f, finalSize.Width);
            Assert.Equal(150f, finalSize.Height);
        }
    }

    [Fact]
    public void PartialReplacedIntrinsicsUseTheDefaultObjectAxis()
    {
        var unknown = new Size<float?>(null, null);
        var style = new LayoutStyle();
        Size<float> widthOnly = ReplacedItem
            .FromIntrinsic(new ReplacedIntrinsic(100f, null, null), style)
            .Size(unknown);
        Assert.Equal((100f, 150f), (widthOnly.Width, widthOnly.Height));

        Size<float> heightOnly = ReplacedItem
            .FromIntrinsic(new ReplacedIntrinsic(null, 100f, null), style)
            .Size(unknown);
        Assert.Equal((300f, 100f), (heightOnly.Width, heightOnly.Height));
    }

    [Fact]
    public void BothAutoReplacedConstraintsTransferThroughPreferredRatio()
    {
        var unknown = new Size<float?>(null, null);
        static Size<float> Measure(LayoutStyle style, Size<float?> unknown) =>
            ReplacedItem.FromStyle(512f, 323f, style).Size(unknown);

        var maxHeight = new LayoutStyle { MaxHeight = Dimension.Px(128f) };
        Size<float> size = Measure(maxHeight, unknown);
        Assert.True(MathF.Abs(size.Width - 202.89783f) < 0.001f, $"{size.Width}x{size.Height}");
        Assert.True(MathF.Abs(size.Height - 128f) < 0.001f, $"{size.Width}x{size.Height}");

        var maxWidth = new LayoutStyle { MaxWidth = Dimension.Px(256f) };
        size = Measure(maxWidth, unknown);
        Assert.True(MathF.Abs(size.Width - 256f) < 0.001f, $"{size.Width}x{size.Height}");
        Assert.True(MathF.Abs(size.Height - 161.5f) < 0.001f, $"{size.Width}x{size.Height}");

        var minWidth = new LayoutStyle { MinWidth = Dimension.Px(1024f) };
        size = Measure(minWidth, unknown);
        Assert.True(MathF.Abs(size.Width - 1024f) < 0.001f, $"{size.Width}x{size.Height}");
        Assert.True(MathF.Abs(size.Height - 646f) < 0.001f, $"{size.Width}x{size.Height}");

        var minHeight = new LayoutStyle { MinHeight = Dimension.Px(646f) };
        size = Measure(minHeight, unknown);
        Assert.True(MathF.Abs(size.Width - 1024f) < 0.001f, $"{size.Width}x{size.Height}");
        Assert.True(MathF.Abs(size.Height - 646f) < 0.001f, $"{size.Width}x{size.Height}");

        // A non-intrinsic authored ratio is the preferred ratio used for transfer, rather than
        // the decoded resource's natural ratio.
        var authoredRatio = new LayoutStyle { MaxHeight = Dimension.Px(128f), AspectRatio = 4f };
        size = Measure(authoredRatio, unknown);
        Assert.True(MathF.Abs(size.Width - 512f) < 0.001f, $"{size.Width}x{size.Height}");
        Assert.True(MathF.Abs(size.Height - 128f) < 0.001f, $"{size.Width}x{size.Height}");
    }

    [Fact]
    public void MissingFontWeightsFollowCssSearchOrder()
    {
        Assert.Equal(400, FontAssets.MatchFontWeight(500, [400, 700]));
        Assert.Equal(700, FontAssets.MatchFontWeight(600, [400, 500, 700]));
        Assert.Equal(100, FontAssets.MatchFontWeight(300, [100, 400, 700]));
        Assert.Equal(700, FontAssets.MatchFontWeight(800, [400, 700]));
        Assert.Equal(500, FontAssets.MatchFontWeight(500, [400, 500, 700]));
    }

    [Fact]
    public void DeclaredFamilySelectsAFaceWithADifferentInternalName()
    {
        var family = new LoadedFamily
        {
            Faces =
            [
                new LoadedFace("Poppins", null, FontAssets.BundledFaceMetrics(Family), 400, 400, false),
                new LoadedFace("Poppins Medium", null, FontAssets.BundledFaceMetrics(Family), 500, 500, false),
                new LoadedFace("Poppins", null, FontAssets.BundledFaceMetrics(Family), 700, 700, false),
            ],
        };
        var loaded = new Dictionary<string, LoadedFamily> { ["poppins"] = family };
        ResolvedFont medium = FontResolution.ResolveLoadedFont("Poppins, sans-serif", 500, false, loaded);
        Assert.Equal("Poppins Medium", medium.Family);
        ResolvedFont semibold = FontResolution.ResolveLoadedFont("Poppins", 600, false, loaded);
        Assert.Equal("Poppins", semibold.Family);
    }

    [Fact]
    public void RangedFaceShapesAtItsFontDatabaseWeight()
    {
        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>Variable family</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontFamily = "test variable",
            FontWeight = "700",
            FontSize = 32f,
        };
        var styles = new Dictionary<NodeId, LayoutStyle> { [copy] = style };

        using var engine = new TextEngine();
        FaceRecord regular = engine.Database.Faces.First(face =>
            face.Weight == 400
            && face.Style == FaceStyle.Normal
            && string.Equals(face.FamilyName, Family, StringComparison.Ordinal));

        ((Dictionary<string, LoadedFamily>)engine.LoadedFamilies)["test variable"] = new LoadedFamily
        {
            Faces = [new LoadedFace(Family, regular.Id, FontAssets.BundledFaceMetrics(Family), 100, 900, false)],
        };

        int item = engine.TryBuild(tree, copy, styles)!.Value;
        engine.Measure(item, null);
        FontId fontId = FirstGlyph(engine, item).FontId;
        FaceRecord face = engine.Database.Face(fontId)!;
        Assert.True(
            string.Equals(face.FamilyName, Family, StringComparison.Ordinal),
            "authored family must not fall through to an unrelated exact-weight face");
        Assert.Equal(400, face.Weight);
    }

    [Fact]
    public void DescriptorSelectedResourcePinsTheExactFontFace()
    {
        byte[] data = SansRegular;
        using var engine = new TextEngine(
            [
                new WebFont { Data = data, Family = "Pinned Variable", Weight = (100, 400), Italic = false },
                new WebFont { Data = data, Family = "Pinned Variable", Weight = (500, 900), Italic = false },
            ],
            loadEmoji: false);
        LoadedFamily loaded = engine.LoadedFamilies["pinned variable"];
        Assert.Equal(2, loaded.Faces.Count);
        FontId firstId = loaded.Faces[0].FontId!.Value;
        ResolvedFont selected = FontResolution.ResolveLoadedFont(
            "Pinned Variable", 700, false, engine.LoadedFamilies);
        FontId selectedId = selected.FontId!.Value;
        Assert.True(firstId != selectedId, "the fixture must contain two distinct database resources");
        FaceRecord selectedFont = engine.Database.Face(selectedId)
            ?? throw new Xunit.Sdk.XunitException("the descriptor-selected database face must be loadable");
        Assert.Equal(selectedId, selectedFont.Id);

        DomTree tree = HtmlParsing.ParseHtml(
            "<p id='copy'>Build whatever you want, without touching your CSS file.</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontFamily = "Pinned Variable",
            FontWeight = "700",
            FontSize = 32f,
        };
        int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })!.Value;
        Assert.True(
            engine.Items[item].Buffer.Lines[0].AttrsList.GetSpan(0).FontId == selectedId,
            "the resolved face must survive rich-text span construction");
        engine.Measure(item, null);
        Assert.True(
            FirstGlyph(engine, item).FontId == selectedId,
            "family/default-weight rematching must not replace the descriptor-selected resource");
    }

    [Fact]
    public void RangedFaceKeepsRequestedWeightAsFallbackAxisIntent()
    {
        var loaded = new Dictionary<string, LoadedFamily>
        {
            ["inter"] = new LoadedFamily
            {
                Faces = [new LoadedFace("Inter", null, FontAssets.BundledFaceMetrics(Family), 100, 900, false)],
            },
        };
        ResolvedFont resolved = FontResolution.ResolveLoadedFont("Inter, sans-serif", 725, false, loaded);
        Assert.Equal("Inter", resolved.Family);
        var style = new LayoutStyle { FontWeight = "725" };
        Assert.Equal(725, ComputedStyle.UsedFontWeight(style));
    }

    [Fact]
    public void GlyphMetadataKeepsFillAndVariationsIndependent()
    {
        var variations = new FontVariations();
        variations.Set(VariationTag.FromAscii("wght"), 725f);
        var attrs = new SpanAttrs
        {
            FontSize = 16f,
            LineHeight = 18f,
            LetterSpacing = 0f,
            LetterSpacingNonNormal = false,
            Weight = 400,
            OpticalSizing = FontOpticalSizing.Auto,
            FontId = null,
            Variations = variations,
            Italic = false,
            SyntheticItalic = false,
            Underline = true,
            Color = new RgbaColor(1, 2, 3, 255),
            Family = Family,
            ClipFill = 37,
            WhiteSpace = WhiteSpace.Normal,
            OverflowWrap = OverflowWrap.Normal,
            WordBreak = WordBreak.Normal,
        };
        TextAttrs shaped = attrs.ToAttrs(42);
        Assert.NotEqual(0UL, shaped.Metadata & InlineGeometry.MetaUnderline);
        Assert.Equal(37, InlineGeometry.MetadataFill(shaped.Metadata));
        Assert.Equal(41, InlineGeometry.MetadataVariation(shaped.Metadata));
        Assert.Equal(725f, shaped.Variations!.Find(VariationTag.FromAscii("wght")));
    }

    [Fact]
    public void StaticTextKeepsTheZeroAllocationVariationPath()
    {
        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>ordinary text</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle { Display = Display.Block };
        Assert.Null(Inline.ResolvedFontVariations(style));

        var styles = new Dictionary<NodeId, LayoutStyle> { [copy] = style };
        using var engine = new TextEngine();
        int item = engine.TryBuild(tree, copy, styles)!.Value;

        Assert.Empty(engine.Items[item].VariationSets);
        Assert.Equal(0, engine.Items[item].VariationSets.Capacity);
    }

    [Fact]
    public void ProgrammaticNonfiniteVariationsNeverReachShapeOrRaster()
    {
        var style = new LayoutStyle
        {
            FontVariationSettings =
            [
                new FontVariationSetting("opsz", float.NaN),
                new FontVariationSetting("wght", float.PositiveInfinity),
            ],
        };
        Assert.Null(Inline.ResolvedFontVariations(style));
    }

    [Fact]
    public void NonNormalLetterSpacingReachesShapingFeatures()
    {
        var span = new SpanAttrs
        {
            FontSize = 20f,
            LineHeight = 24f,
            LetterSpacing = 2f,
            LetterSpacingNonNormal = true,
            Weight = 400,
            OpticalSizing = FontOpticalSizing.Auto,
            FontId = null,
            Variations = null,
            Italic = false,
            SyntheticItalic = false,
            Underline = false,
            Color = new RgbaColor(0, 0, 0, 255),
            Family = Family,
            ClipFill = null,
            WhiteSpace = WhiteSpace.Normal,
            OverflowWrap = OverflowWrap.Normal,
            WordBreak = WordBreak.Normal,
        };
        TextAttrs attrs = span.ToAttrs(1);
        Assert.Equal(0.1f, attrs.LetterSpacingEm!.Value);
        Assert.Contains(attrs.Features, f => f.Tag == SpanAttrs.Tag("liga") && f.Value == 0);
        Assert.Contains(attrs.Features, f => f.Tag == SpanAttrs.Tag("clig") && f.Value == 0);

        SpanAttrs zero = span with { LetterSpacing = 0f };
        Assert.Empty(zero.ToAttrs(1).Features);
    }

    [Fact]
    public void KeepAllNeverInsertsControlsInsideGraphemeClusters()
    {
        const string Source = "각각 か\u3099か\u3099 \U0001F468‍\U0001F469‍\U0001F467‍\U0001F466 \U0001F44D\U0001F3FD";
        DomTree tree = HtmlParsing.ParseHtml($"<p id='copy'>{Source}</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var baseStyle = new LayoutStyle
        {
            Display = Display.Block,
            FontSize = 40f,
            LineHeight = LineHeight.Px(48f),
        };
        LayoutStyle keep = baseStyle.Clone();
        keep.WordBreak = WordBreak.KeepAll;

        using var normalEngine = new TextEngine();
        int normalItem = normalEngine
            .TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = baseStyle })!.Value;
        (float Width, float Height) normalSize = normalEngine.Measure(normalItem, null);

        using var keepEngine = new TextEngine();
        int keepItem = keepEngine
            .TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = keep })!.Value;
        (float Width, float Height) keepSize = keepEngine.Measure(keepItem, null);

        Assert.Equal(Source, keepEngine.ItemText(keepItem));
        Assert.Equal(normalSize, keepSize);
    }

    private static byte[] VariableFontFixture()
    {
        string path = Path.Combine(
            RepositoryRoot(),
            "crates", "obscura-render", "tests", "fonts", "obscura-vf-test.woff2.b64");
        string encoded = new(File.ReadAllText(path).Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        byte[] compressed = Convert.FromBase64String(encoded);
        Assert.True(Woff.TryDecode(compressed, out byte[]? sfnt), "decompress variable-font fixture");
        return sfnt!;
    }

    [Fact]
    public void VariableFontMultiAxisShapingPreservesSpaceAdvance()
    {
        using var engine = new TextEngine(
            [new WebFont { Data = VariableFontFixture(), Family = "Obscura VF Test", Weight = (100, 900), Italic = false }],
            loadEmoji: false);
        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>Tools and</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontFamily = "Obscura VF Test",
            FontWeight = "700",
            FontSize = 32f,
            FontOpticalSizing = FontOpticalSizing.Auto,
        };
        int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })!.Value;
        engine.Finalize(item, (0f, 0f), 600f, null);
        string shapedText = engine.ItemText(item);
        float? spaceAdvance = null;
        foreach (LayoutRun run in engine.Items[item].Buffer.LayoutRuns())
        {
            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                if (shapedText[glyph.Start..glyph.End] == " ")
                {
                    spaceAdvance = glyph.W;
                    break;
                }
            }

            if (spaceAdvance is not null)
            {
                break;
            }
        }

        Assert.True(spaceAdvance is not null, "shaped space glyph");
        Assert.True(
            spaceAdvance > 5f,
            "setting wght and opsz must not repeatedly remap avar coordinates and collapse the "
                + $"space advance: {spaceAdvance}");
    }

    private static (TextEngine Engine, FontId StaticId, FontId VariableId) IsolatedFallbackEngine()
    {
        string arabic = Path.Combine(RepositoryRoot(), "vendor", "cosmic-text", "fonts", "NotoSansArabic.ttf");
        var engine = new TextEngine(
            [
                new WebFont
                {
                    Data = File.ReadAllBytes(arabic),
                    Family = "Static Primary",
                    Weight = (400, 400),
                    Italic = false,
                },
                new WebFont
                {
                    Data = VariableFontFixture(),
                    Family = "Variable Fallback",
                    Weight = (100, 900),
                    Italic = false,
                },
            ],
            loadEmoji: false);
        FontId staticId = engine.LoadedFamilies["static primary"].Faces[0].FontId!.Value;
        FontId variableId = engine.LoadedFamilies["variable fallback"].Faces[0].FontId!.Value;
        List<FontId> remove = [.. engine.Database.Faces
            .Select(face => face.Id)
            .Where(id => id != staticId && id != variableId)];
        foreach (FontId id in remove)
        {
            engine.Database.RemoveFace(id);
        }

        return (engine, staticId, variableId);
    }

    private static (FontId ShapedId, Dictionary<string, HashSet<uint>> Axes) RenderVariableFallback(
        FontOpticalSizing opticalSizing)
    {
        (TextEngine engine, _, FontId variableId) = IsolatedFallbackEngine();
        using (engine)
        {
            DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>A</p>");
            NodeId copy = tree.GetElementById("copy")!.Value;
            var style = new LayoutStyle
            {
                Display = Display.Block,
                FontFamily = "Static Primary",
                FontWeight = "900",
                FontSize = 40f,
                FontOpticalSizing = opticalSizing,
            };
            int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })!.Value;
            engine.Measure(item, 100f);
            engine.Finalize(item, (0f, 0f), 100f, null);
            FontId shapedId = FirstGlyph(engine, item).FontId;
            Assert.Equal(variableId, shapedId);

            var pixmap = new Pixmap(100, 80);
            engine.PaintItem(item, pixmap, (0f, 0f));
            Dictionary<string, HashSet<uint>> axes = new(StringComparer.Ordinal);
            foreach ((GlyphCacheKey _, FontVariations? variations) in engine.Rasterizer.CachedKeys)
            {
                if (variations is null)
                {
                    continue;
                }

                foreach (FontVariation variation in variations.Items)
                {
                    if (!axes.TryGetValue(variation.Tag.ToString(), out HashSet<uint>? values))
                    {
                        values = [];
                        axes[variation.Tag.ToString()] = values;
                    }

                    values.Add(BitConverter.SingleToUInt32Bits(variation.Value.Value));
                }
            }

            return (shapedId, axes);
        }
    }

    [Fact]
    public void AutomaticAxesFollowTheActualVariableFallbackFace()
    {
        (_, Dictionary<string, HashSet<uint>> autoAxes) = RenderVariableFallback(FontOpticalSizing.Auto);
        (_, Dictionary<string, HashSet<uint>> noneAxes) = RenderVariableFallback(FontOpticalSizing.None);

        Assert.Equal(new HashSet<uint> { BitConverter.SingleToUInt32Bits(900f) }, autoAxes["wght"]);
        Assert.Equal(new HashSet<uint> { BitConverter.SingleToUInt32Bits(900f) }, noneAxes["wght"]);

        // The fixture's opsz range ends at 32; CSS's automatic 40px coordinate must be clamped
        // against the actual fallback face.
        Assert.Equal(new HashSet<uint> { BitConverter.SingleToUInt32Bits(32f) }, autoAxes["opsz"]);
        Assert.False(noneAxes.ContainsKey("opsz"));
    }

    [Fact]
    public void UnsupportedAndClampedAxesShareEffectiveRasterIdentity()
    {
        (TextEngine engine, _, FontId variableId) = IsolatedFallbackEngine();
        using (engine)
        {
            var firstSettings = new FontVariations();
            firstSettings.Set(VariationTag.FromAscii("NOPE"), 1f);
            firstSettings.Set(VariationTag.FromAscii("wght"), 5_000f);
            var secondSettings = new FontVariations();
            secondSettings.Set(VariationTag.FromAscii("NOPE"), 999f);
            secondSettings.Set(VariationTag.FromAscii("wght"), 900f);

            FontVariations first = engine.VariableCache.EffectiveVariations(
                variableId, 400f, 40f, false, firstSettings)!;
            FontVariations second = engine.VariableCache.EffectiveVariations(
                variableId, 400f, 40f, false, secondSettings)!;

            Assert.Equal(first, second);
            Assert.DoesNotContain(first.Items, v => v.Tag == VariationTag.FromAscii("NOPE"));
            Assert.Equal(900f, first.Find(VariationTag.FromAscii("wght")));
        }
    }

    private static ((float Width, float Height) Geometry, ulong Ink, List<ushort> CachedWeights)
        RenderVariableWeight(ushort weight)
    {
        using var engine = new TextEngine(
            [new WebFont { Data = VariableFontFixture(), Family = "Obscura VF Test", Weight = (100, 900), Italic = false }],
            loadEmoji: false);
        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>MMMMMMMM</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontFamily = "Obscura VF Test",
            FontWeight = weight.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 64f,
            LineHeight = LineHeight.Px(80f),
        };
        var styles = new Dictionary<NodeId, LayoutStyle> { [copy] = style };
        int item = engine.TryBuild(tree, copy, styles)!.Value;
        (float Width, float Height) geometry = engine.Measure(item, 600f);
        engine.Finalize(item, (0f, 0f), 600f, null);
        var pixmap = new Pixmap(600, 100);
        engine.PaintItem(item, pixmap, (0f, 0f));
        ulong ink = 0;
        foreach (PremultipliedColor pixel in pixmap.Pixels)
        {
            ink += pixel.A;
        }

        List<ushort> cachedWeights = [];
        foreach ((GlyphCacheKey _, FontVariations? variations) in engine.Rasterizer.CachedKeys)
        {
            if (variations?.Find(VariationTag.FromAscii("wght")) is { } value)
            {
                cachedWeights.Add((ushort)value);
            }
        }

        cachedWeights.Sort();
        cachedWeights = [.. cachedWeights.Distinct()];
        return (geometry, ink, cachedWeights);
    }

    [Fact]
    public void ShapedTextPreservesAuthoredAlphaThroughMaskRasterization()
    {
        static ulong RenderAlpha(byte alpha, bool underline)
        {
            DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>Alpha</p>");
            NodeId copy = tree.GetElementById("copy")!.Value;
            var style = new LayoutStyle
            {
                Display = Display.Block,
                FontSize = 32f,
                LineHeight = LineHeight.Px(40f),
                Color = new RgbaColor(0, 0, 0, alpha),
                Underline = underline,
            };
            var styles = new Dictionary<NodeId, LayoutStyle> { [copy] = style };
            using var engine = new TextEngine();
            int item = engine.TryBuild(tree, copy, styles)!.Value;
            engine.Finalize(item, (0f, 0f), 160f, null);
            var pixmap = new Pixmap(160, 48);
            engine.PaintItem(item, pixmap, (0f, 0f));
            ulong total = 0;
            foreach (PremultipliedColor pixel in pixmap.Pixels)
            {
                total += pixel.A;
            }

            return total;
        }

        Assert.True(RenderAlpha(0, true) == 0, "transparent glyphs and decorations must not paint");
        ulong half = RenderAlpha(128, false);
        ulong opaque = RenderAlpha(255, false);
        ulong ratio = half * 100 / opaque;
        Assert.True(
            ratio is >= 48 and <= 52,
            $"50% CSS alpha must retain half the opaque coverage: half={half}, opaque={opaque}");
    }

    [Fact]
    public void VariableWghtChangesShapeAndTrueOutline()
    {
        ((float Width, float Height) regularGeometry, ulong regularInk, List<ushort> regularCache) =
            RenderVariableWeight(400);
        ((float Width, float Height) blackGeometry, ulong blackInk, List<ushort> blackCache) =
            RenderVariableWeight(900);
        Assert.True(regularGeometry.Width > 0f && blackGeometry.Width > 0f);
        Assert.True(
            blackInk > regularInk * 13 / 10,
            $"wght=900 should carry substantially more raster ink: {regularInk} vs {blackInk}");
        Assert.Equal([(ushort)400], regularCache);
        Assert.Equal([(ushort)900], blackCache);
    }

    [Fact]
    public void VariableGlyphCacheKeysIncludeWeightAxis()
    {
        using var engine = new TextEngine(
            [new WebFont { Data = VariableFontFixture(), Family = "Obscura VF Test", Weight = (100, 900), Italic = false }],
            loadEmoji: false);
        DomTree tree = HtmlParsing.ParseHtml(
            "<p id='copy'><span id='regular'>M</span><span id='black'>M</span></p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        NodeId regular = tree.GetElementById("regular")!.Value;
        NodeId black = tree.GetElementById("black")!.Value;
        var baseStyle = new LayoutStyle
        {
            Display = Display.Block,
            FontFamily = "Obscura VF Test",
            FontSize = 64f,
            LineHeight = LineHeight.Px(80f),
        };
        LayoutStyle regularStyle = baseStyle.Clone();
        regularStyle.Display = Display.Inline;
        regularStyle.FontWeight = "400";
        LayoutStyle blackStyle = regularStyle.Clone();
        blackStyle.FontWeight = "900";
        var styles = new Dictionary<NodeId, LayoutStyle>
        {
            [copy] = baseStyle,
            [regular] = regularStyle,
            [black] = blackStyle,
        };
        int item = engine.TryBuild(tree, copy, styles)!.Value;
        engine.Measure(item, 200f);
        engine.Finalize(item, (0f, 0f), 200f, null);
        var pixmap = new Pixmap(200, 100);
        engine.PaintItem(item, pixmap, (0f, 0f));
        HashSet<ushort> weights = [];
        foreach ((GlyphCacheKey _, FontVariations? variations) in engine.Rasterizer.CachedKeys)
        {
            if (variations?.Find(VariationTag.FromAscii("wght")) is { } value)
            {
                weights.Add((ushort)value);
            }
        }

        Assert.Equal(new HashSet<ushort> { 400, 900 }, weights);
    }

    private static ((float Width, float Height) Geometry, int Lines, HashSet<uint> OpticalValues)
        RenderTailwindOpticalHeading(FontOpticalSizing opticalSizing, float? explicitOpsz, float width)
    {
        using var engine = new TextEngine(
            [new WebFont { Data = VariableFontFixture(), Family = "Obscura VF Test", Weight = (100, 900), Italic = false }],
            loadEmoji: false);
        DomTree tree = HtmlParsing.ParseHtml(
            "<h2 id='copy'>Build whatever you want, without touching your CSS file.</h2>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontFamily = "Obscura VF Test",
            FontWeight = "500",
            FontSize = 40f,
            LineHeight = LineHeight.Px(40f),
            LetterSpacing = -2f,
            LetterSpacingNonNormal = true,
            FontOpticalSizing = opticalSizing,
            FontVariationSettings = explicitOpsz is { } value
                ? [new FontVariationSetting("opsz", value)]
                : [],
        };
        var styles = new Dictionary<NodeId, LayoutStyle> { [copy] = style };
        int item = engine.TryBuild(tree, copy, styles)!.Value;
        (float Width, float Height) geometry = engine.Measure(item, width);
        int lines = engine.Items[item].Buffer.LayoutRuns().Count();
        engine.Finalize(item, (0f, 0f), width, null);
        var pixmap = new Pixmap(512, 140);
        engine.PaintItem(item, pixmap, (0f, 0f));
        HashSet<uint> opticalValues = [];
        foreach ((GlyphCacheKey _, FontVariations? variations) in engine.Rasterizer.CachedKeys)
        {
            if (variations?.Find(VariationTag.FromAscii("opsz")) is { } opsz)
            {
                opticalValues.Add(BitConverter.SingleToUInt32Bits(opsz));
            }
        }

        return (geometry, lines, opticalValues);
    }

    [Fact]
    public void OpticalSizingChangesTailwindHeadingShapeAndRasterAxes()
    {
        ((float Width, float Height) autoGeometry, int autoLines, HashSet<uint> autoAxes) =
            RenderTailwindOpticalHeading(FontOpticalSizing.Auto, null, 496f);
        ((float Width, float Height) noneGeometry, int noneLines, HashSet<uint> noneAxes) =
            RenderTailwindOpticalHeading(FontOpticalSizing.None, null, 496f);
        ((float Width, float Height) explicitGeometry, int explicitLines, HashSet<uint> explicitAxes) =
            RenderTailwindOpticalHeading(FontOpticalSizing.Auto, 14f, 496f);

        Assert.True(
            autoGeometry != noneGeometry,
            "automatic opsz must affect the shaped heading advances");
        Assert.Equal(noneLines, autoLines);
        Assert.Equal(noneLines, explicitLines);
        Assert.True(
            explicitGeometry != autoGeometry,
            "an explicit low-level opsz coordinate must override automatic sizing");
        Assert.Equal(new HashSet<uint> { BitConverter.SingleToUInt32Bits(32f) }, autoAxes);
        Assert.Empty(noneAxes);
        Assert.Equal(new HashSet<uint> { BitConverter.SingleToUInt32Bits(14f) }, explicitAxes);
    }

    [Fact]
    public void TextOnlyInlineBlockKeepsAnInternalShapingContext()
    {
        DomTree tree = HtmlParsing.ParseHtml("<span id='icon'>ligature_name</span>");
        NodeId icon = tree.GetElementById("icon")!.Value;
        var style = new LayoutStyle { Display = Display.Inline, IsInlineBlock = true };
        var styles = new Dictionary<NodeId, LayoutStyle> { [icon] = style };

        Assert.True(
            Inline.IsPureTextIfc(tree, icon, styles),
            "atomic inline participation must not disable shaping inside the box");
    }

    [Fact(Skip = "Needs crate::dom::layout_dom (dom.rs), which is not ported yet. The inline half "
        + "of this assertion - per-span metrics reaching shaping - is exercised by "
        + "VariableGlyphCacheKeysIncludeWeightAxis, which shapes two differently styled child "
        + "spans in one buffer.")]
    public void InlineDescendantKeepsItsComputedFontMetrics()
    {
        // Rust body, preserved verbatim so this can be restored once dom.rs lands:
        //
        //   let tree = obscura_dom::parse_html(
        //       r#"<style>
        //           #copy { font-size:16px; line-height:20px }
        //           #big { font-size:2em; line-height:1.5 }
        //       </style>
        //       <p id="copy">small <a id="big">large</a></p>"#);
        //   let laid = crate::dom::layout_dom(&tree, (500.0, 200.0));
        //   assert_eq!(laid.styles[&big].font_size, Some(32.0));
        //   let item = laid.ifc_items[&copy];
        //   let glyph_sizes = laid.text_engine.items[item].buffer.layout_runs()
        //       .flat_map(|run| run.glyphs.iter().map(|glyph| glyph.font_size)).collect::<Vec<_>>();
        //   assert!(glyph_sizes.iter().any(|size| (*size - 16.0).abs() < 0.01));
        //   assert!(glyph_sizes.iter().any(|size| (*size - 32.0).abs() < 0.01));
    }

    [Fact(Skip = "Needs crate::dom::layout_dom (dom.rs) and the CSS cascade for the UA sheet's "
        + "white-space:pre on <pre>; neither is ported yet.")]
    public void PreformattedNewlinesPreserveAuthoredLineCount()
    {
        // Rust body, preserved so this can be restored once dom.rs and the UA sheet land. It
        // lays out a <pre><code>, a white-space:pre-wrap div, and a white-space:normal div over
        // the same three authored lines at 200px / 16px/24px monospace, then asserts the first
        // two are 72px tall (three line boxes) and the third is 24px (one).
    }

    private static List<string> ShapedLineTexts(TextBuffer buffer)
    {
        List<string> lines = [];
        foreach (LayoutRun run in buffer.LayoutRuns())
        {
            int start = int.MaxValue;
            int end = int.MinValue;
            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                start = Math.Min(start, glyph.Start);
                end = Math.Max(end, glyph.End);
            }

            if (start == int.MaxValue)
            {
                start = 0;
                end = 0;
            }

            lines.Add(run.Text[start..Math.Max(start, end)].Trim());
        }

        return lines;
    }

    [Fact]
    public void TextIndentChangesOnlyTheFirstFormattedLine()
    {
        DomTree tree = HtmlParsing.ParseHtml(
            "<p id='copy'>alpha beta gamma delta epsilon zeta eta theta</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontSize = 16f,
            LineHeight = LineHeight.Px(20f),
            TextIndent = Dimension.Px(40f),
        };
        using var engine = new TextEngine();
        int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })!.Value;
        engine.Finalize(item, (0f, 0f), 150f, null);

        List<string> lines = ShapedLineTexts(engine.Items[item].Buffer);
        Assert.True(lines.Count >= 2, $"fixture must wrap: {string.Join(" | ", lines)}");
        Assert.Equal(40f, engine.Items[item].FirstLineOffset);
        List<LayoutRun> runs = [.. engine.Items[item].Buffer.LayoutRuns()];
        float firstX = runs[0].Glyphs[0].X + engine.Items[item].FirstLineOffset;
        float secondX = runs[1].Glyphs[0].X;
        Assert.True(MathF.Abs(firstX - 40f) < 0.01f, $"first line must start at the authored indent: {firstX}");
        Assert.True(MathF.Abs(secondX) < 0.01f, $"continuation lines must return to the content edge: {secondX}");

        var percentage = new LayoutStyle
        {
            Display = Display.Block,
            TextIndent = Dimension.Percent(0.25f),
        };
        int percentItem = engine
            .TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = percentage })!.Value;
        engine.Finalize(percentItem, (0f, 0f), 200f, null);
        Assert.Equal(50f, engine.Items[percentItem].FirstLineOffset);
    }

    [Fact]
    public void TextWrapBalanceChangesLineGroupingWithoutChangingLineCount()
    {
        DomTree tree = HtmlParsing.ParseHtml("<h1 id='hero'>Welcome to Mozilla</h1>");
        NodeId hero = tree.GetElementById("hero")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontSize = 128f,
            LineHeight = LineHeight.Px(128f),
            TextWrapStyle = TextWrapStyle.Balance,
        };
        using var engine = new TextEngine();
        int item = engine.TryBuild(tree, hero, new Dictionary<NodeId, LayoutStyle> { [hero] = style })!.Value;
        const float Width = 912f;
        engine.Measure(item, Width);
        List<string> naturalLines = ShapedLineTexts(engine.Items[item].Buffer);

        engine.Finalize(item, (0f, 0f), Width, null);
        List<string> balancedLines = ShapedLineTexts(engine.Items[item].Buffer);
        float balancedWidth = engine.Items[item].Buffer.Size.Width!.Value;

        Assert.Equal(["Welcome to", "Mozilla"], naturalLines);
        Assert.Equal(["Welcome", "to Mozilla"], balancedLines);
        Assert.True(
            balancedWidth < Width - 1f,
            $"balance should tighten the effective wrap width: {balancedWidth}");
    }

    [Fact(Skip = "Needs crate::dom::layout_dom (dom.rs) and the text-wrap/text-wrap-style cascade "
        + "in style.rs; neither is ported yet. This test asserts only cascade behavior, no inline "
        + "layout.")]
    public void TextWrapStyleIsInheritedAndCanBeReset()
    {
        // Rust body, preserved so this can be restored once dom.rs and the style.rs cascade
        // land. It asserts that `text-wrap: balance` on an ancestor computes to
        // TextWrapStyle::Balance on a descendant heading, and that `text-wrap-style: auto` on a
        // sibling resets it to TextWrapStyle::Auto. No inline layout is involved.
    }

    [Fact]
    public void ClipFillOnlyForTransparentClipText()
    {
        var s = new LayoutStyle();

        // Gradient + clip-to-text + transparent color: fills through the glyphs.
        s.BackgroundClipText = true;
        s.Color = new RgbaColor(0, 0, 0, 0);
        s.BackgroundGradient = (90f, [new GradientStop(Red, null), new GradientStop(Blue, null)]);
        Assert.NotNull(Inline.ClipTextFillFor(s));

        // Same, but opaque text: paints normally, no clip fill.
        s.Color = new RgbaColor(10, 20, 30, 255);
        Assert.Null(Inline.ClipTextFillFor(s));

        // Clip-to-text off: ordinary transparent text stays invisible.
        s.Color = new RgbaColor(0, 0, 0, 0);
        s.BackgroundClipText = false;
        Assert.Null(Inline.ClipTextFillFor(s));

        // Solid background color becomes a flat two-stop gradient.
        s.BackgroundClipText = true;
        s.BackgroundGradient = null;
        s.BackgroundColor = new RgbaColor(12, 34, 56, 255);
        ClipTextFill fill = Inline.ClipTextFillFor(s)
            ?? throw new Xunit.Sdk.XunitException("solid bg clip fill");
        Assert.Equal(2, fill.Stops.Count);
        Assert.Equal(new RgbaColor(12, 34, 56, 255), fill.Stops[0].Color);
    }

    [Fact]
    public void SampleGradientTintsLeftToRight()
    {
        // 90deg (to right): left edge is the first stop, right edge the last.
        var fill = new ClipTextFill(90f, [(Red, null), (Blue, null)]);
        RgbaColor left = Inline.SampleGradient(fill, 0f, 5f, 100f, 10f);
        RgbaColor right = Inline.SampleGradient(fill, 100f, 5f, 100f, 10f);
        Assert.True(left.R > left.B, $"left end should be reddish: {left}");
        Assert.True(right.B > right.R, $"right end should be bluish: {right}");

        // A single-color list samples to that color everywhere.
        var flat = new ClipTextFill(0f, [(new RgbaColor(7, 8, 9, 255), null)]);
        Assert.Equal(new RgbaColor(7, 8, 9, 255), Inline.SampleGradient(flat, 3f, 3f, 20f, 20f));
    }

    [Fact]
    public void EmojiFontIsLoadedOnlyForEmojiDocuments()
    {
        Assert.False(FontAssets.TextMayNeedEmojiFont("Plain text and arrows ->"));
        Assert.True(FontAssets.TextMayNeedEmojiFont("Add ➕ or remove ➖"));
        Assert.True(FontAssets.TextMayNeedEmojiFont("Launch \U0001F680"));

        using var plain = new TextEngine([], loadEmoji: false);
        Assert.False(plain.LoadedFamilies.ContainsKey("noto color emoji"));

        using var engine = new TextEngine([], loadEmoji: true);
        HashSet<FontId> emojiIds = [];
        foreach (LoadedFace face in engine.LoadedFamilies["noto color emoji"].Faces)
        {
            if (face.FontId is { } id)
            {
                emojiIds.Add(id);
            }
        }

        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>➕ \U0001F600 \U0001F680</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle
        {
            Display = Display.Block,
            FontSize = 48f,
            LineHeight = LineHeight.Px(64f),
        };
        int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })!.Value;
        engine.Finalize(item, (0f, 0f), 300f, null);
        HashSet<FontId> fontIds = [];
        foreach (LayoutRun run in engine.Items[item].Buffer.LayoutRuns())
        {
            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                fontIds.Add(glyph.FontId);
            }
        }

        Assert.True(fontIds.Overlaps(emojiIds), "emoji clusters must shape with the bundled color face");

        var pixmap = new Pixmap(300, 80);
        engine.PaintItem(item, pixmap, (0f, 0f));
        HashSet<(byte, byte, byte)> colors = [];
        foreach (PremultipliedColor pixel in pixmap.Pixels)
        {
            if (pixel.A != 0)
            {
                colors.Add((pixel.R, pixel.G, pixel.B));
            }
        }

        Assert.True(colors.Count > 20, "the bundled CBDT face must rasterize as color, not a monochrome mask");
    }

    private static LayoutGlyph FirstGlyph(TextEngine engine, int item)
    {
        foreach (LayoutRun run in engine.Items[item].Buffer.LayoutRuns())
        {
            if (run.Glyphs.Count > 0)
            {
                return run.Glyphs[0];
            }
        }

        throw new Xunit.Sdk.XunitException("expected at least one shaped glyph");
    }
}
