using System.Runtime.CompilerServices;
using Obscura.Dom;
using Obscura.Render.Layout;
using Xunit;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render.Tests;

/// <summary>
/// Operator font directories (upstream 343fdc7 <c>--font-dir</c>): how a directory is walked,
/// how its faces join the embedded set, and that leaving it unset changes nothing.
/// </summary>
/// <remarks>
/// The process-wide setting is never configured from here: this test process renders
/// everywhere else, and a directory face would change fallback for every other test. Each test
/// builds its own <see cref="FontDirectorySet"/> and hands it to a <see cref="TextEngine"/>.
/// </remarks>
public sealed class FontDirectoryTests : IDisposable
{
    // "marhaban bil-alam".
    private const string Arabic = "مرحبا بالعالم";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "obscura-font-directory-test-" + Guid.NewGuid().ToString("N"));

    public FontDirectoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string ArabicFixture([CallerFilePath] string path = "") =>
        Path.Combine(Path.GetDirectoryName(path)!, "Fixtures", "fonts", "NotoSansArabic.ttf");

    /// <summary>Port of <c>configured_font_directory_loads_nested_fonts_and_skips_symlinks</c>.</summary>
    [Fact]
    public void ConfiguredFontDirectoryLoadsNestedFontsAndSkipsSymlinks()
    {
        string nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        byte[] font = File.ReadAllBytes(ArabicFixture());
        File.WriteAllBytes(Path.Combine(nested, "fixture.TTF"), font);
        File.WriteAllBytes(Path.Combine(nested, "ignored.txt"), font);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(nested, "cycle"), _root);
            File.CreateSymbolicLink(Path.Combine(_root, "linked.ttf"), Path.Combine(nested, "fixture.TTF"));
        }

        FontDirectorySet set = FontDirectorySet.Load([_root]);
        Assert.Equal([Path.Combine(nested, "fixture.TTF")], set.Files);

        using var engine = new TextEngine([], loadEmoji: false, set);
        Assert.True(engine.LoadedFamilies.ContainsKey("noto sans arabic"));
    }

    [Fact]
    public void DirectoryFilesLoadInPathOrderOnce()
    {
        string sub = Path.Combine(_root, "b");
        string dashed = Path.Combine(_root, "b-c");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(dashed);
        byte[] font = File.ReadAllBytes(ArabicFixture());
        File.WriteAllBytes(Path.Combine(dashed, "x.otf"), font);
        File.WriteAllBytes(Path.Combine(sub, "x.ttc"), font);
        File.WriteAllBytes(Path.Combine(_root, "a.OTC"), font);

        // The same directory twice loads its files once; order is by path component, so
        // `b/` sorts before `b-c/` although '-' sorts before '/'.
        FontDirectorySet set = FontDirectorySet.Load([_root, _root]);
        Assert.Equal(
            [Path.Combine(_root, "a.OTC"), Path.Combine(sub, "x.ttc"), Path.Combine(dashed, "x.otf")],
            set.Files);
    }

    [Fact]
    public void DirectoryFaceIsUsedWhenThePageNamesItsFamily()
    {
        File.Copy(ArabicFixture(), Path.Combine(_root, "NotoSansArabic.ttf"));
        FontDirectorySet set = FontDirectorySet.Load([_root]);

        using var engine = new TextEngine([], loadEmoji: false, set);
        (FontId shaped, float width) = ShapeFirstGlyph(engine, "Noto Sans Arabic");
        FaceRecord face = engine.Database.Face(shaped)!;
        Assert.Equal("Noto Sans Arabic", face.FamilyName);
        Assert.Equal(engine.LoadedFamilies["noto sans arabic"].Faces[0].FontId, shaped);

        // Without the directory the family does not exist and the text falls back elsewhere.
        using var plain = new TextEngine([], loadEmoji: false, FontDirectorySet.Empty);
        Assert.False(plain.LoadedFamilies.ContainsKey("noto sans arabic"));
        (FontId fallback, float fallbackWidth) = ShapeFirstGlyph(plain, "Noto Sans Arabic");
        Assert.NotEqual("Noto Sans Arabic", plain.Database.Face(fallback)!.FamilyName);
        Assert.NotEqual(fallbackWidth, width);
    }

    [Fact]
    public void DirectoryFacesFollowTheEmbeddedFacesAndPrecedeEmojiAndWebFonts()
    {
        File.Copy(ArabicFixture(), Path.Combine(_root, "NotoSansArabic.ttf"));
        FontDirectorySet set = FontDirectorySet.Load([_root]);
        using var engine = new TextEngine(
            [new WebFont { Data = FontAssets.Load("liberation-serif"), Family = "Page Face" }],
            loadEmoji: true,
            set);

        List<string> order = [.. engine.Database.Faces.Select(face => face.FamilyName)];
        int bundled = FontAssets.BundledFaceFiles.Length;
        Assert.Equal("Noto Sans Arabic", order[bundled]);
        Assert.Equal(FontAssets.EmojiFamily, order[bundled + 1]);
        Assert.Equal(FontAssets.SerifFamily, order[bundled + 2]);
    }

    [Fact]
    public void UnsetDirectoryLeavesLayoutIdentical()
    {
        // Nothing configures the process-wide setting in this test process.
        Assert.True(FontDirectories.Current.IsEmpty);
        Assert.Same(FontDirectorySet.Empty, FontDirectories.Current);

        using var baseline = new TextEngine([], loadEmoji: false, FontDirectorySet.Empty);
        using var engine = new TextEngine();
        Assert.Equal(
            baseline.Database.Faces.Select(face => (face.Id, face.FamilyName, face.Weight, face.Style)),
            engine.Database.Faces.Select(face => (face.Id, face.FamilyName, face.Weight, face.Style)));
        Assert.Equal(baseline.LoadedFamilies.Keys.Order(), engine.LoadedFamilies.Keys.Order());

        Assert.Equal(GlyphSnapshot(baseline, Arabic), GlyphSnapshot(engine, Arabic));
    }

    [Fact]
    public void BadFilesAreSkippedSilently()
    {
        File.WriteAllBytes(Path.Combine(_root, "garbage.ttf"), [1, 2, 3, 4, 5, 6, 7, 8]);
        File.WriteAllBytes(Path.Combine(_root, "empty.otf"), []);
        // A WOFF file renamed .ttf: fontdb reads raw sfnt only, so upstream loads nothing.
        File.WriteAllBytes(Path.Combine(_root, "renamed.ttf"), [(byte)'w', (byte)'O', (byte)'F', (byte)'F', 0, 1, 0, 0]);
        File.Copy(ArabicFixture(), Path.Combine(_root, "real.ttf"));
        string missing = Path.Combine(_root, "does-not-exist");

        FontDirectorySet set = FontDirectorySet.Load([missing, _root]);
        using var engine = new TextEngine([], loadEmoji: false, set);
        using var plain = new TextEngine([], loadEmoji: false, FontDirectorySet.Empty);

        // The unparseable files contribute no face; the real one still loads.
        Assert.Equal(plain.Database.Faces.Count + 1, engine.Database.Faces.Count);
        Assert.True(engine.LoadedFamilies.ContainsKey("noto sans arabic"));
        Assert.DoesNotContain(Path.Combine(_root, "renamed.ttf"), set.Files);

        // A directory set of nothing but bad files is the embedded set.
        File.Delete(Path.Combine(_root, "real.ttf"));
        using var bad = new TextEngine([], loadEmoji: false, FontDirectorySet.Load([_root]));
        Assert.Equal(plain.Database.Faces.Count, bad.Database.Faces.Count);
    }

    [Fact]
    public void ConfigureAfterTheFirstRenderIsRefused()
    {
        // Reading the setting is what the first render does; after it the setting is closed,
        // as upstream's `configure_font_directories` returns false once the base database is
        // built. This never succeeds here, so it cannot leak a directory into other tests.
        _ = FontDirectories.Current;
        Assert.False(FontDirectories.Configure([_root]));
        Assert.True(FontDirectories.Current.IsEmpty);
    }

    private static (FontId Id, float Width) ShapeFirstGlyph(TextEngine engine, string family)
    {
        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>" + Arabic + "</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle { Display = Display.Block, FontFamily = family, FontSize = 24f };
        int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })!.Value;
        (float width, _) = engine.Measure(item, 1000f);
        engine.Finalize(item, (0f, 0f), 1000f, null);
        foreach (LayoutRun run in engine.Items[item].Buffer.LayoutRuns())
        {
            if (run.Glyphs.Count > 0)
            {
                return (run.Glyphs[0].FontId, width);
            }
        }

        throw new Xunit.Sdk.XunitException("expected at least one shaped glyph");
    }

    private static List<(uint, float, float)> GlyphSnapshot(TextEngine engine, string text)
    {
        DomTree tree = HtmlParsing.ParseHtml("<p id='copy'>" + text + " Latin text</p>");
        NodeId copy = tree.GetElementById("copy")!.Value;
        var style = new LayoutStyle { Display = Display.Block, FontSize = 16f };
        int item = engine.TryBuild(tree, copy, new Dictionary<NodeId, LayoutStyle> { [copy] = style })!.Value;
        engine.Measure(item, 120f);
        engine.Finalize(item, (0f, 0f), 120f, null);
        List<(uint, float, float)> glyphs = [];
        foreach (LayoutRun run in engine.Items[item].Buffer.LayoutRuns())
        {
            foreach (LayoutGlyph glyph in run.Glyphs)
            {
                glyphs.Add(((uint)glyph.FontId.Value, glyph.X, glyph.Y));
            }
        }

        return glyphs;
    }
}
