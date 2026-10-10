using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// The line layouts <see cref="ShapeCache"/> keeps beside a shaped paragraph are the layouts
/// <see cref="TextLayout.LayoutToBuffer"/> computes for the same arguments.
/// </summary>
public sealed class LineLayoutMemoTests
{
    private static (TextShaper Shaper, ShapeCache Cache, ShapeLine Line) Shape(string text)
    {
        var engine = new TextEngine();
        var cache = new ShapeCache([]);
        var shaper = new TextShaper(engine.Database) { Cache = cache };
        var attrs = new AttrsList(new TextAttrs
        {
            Family = "Liberation Sans",
            Metrics = new TextMetrics(16f, 18f),
        });
        return (shaper, cache, shaper.ShapeParagraph(text, attrs, 8));
    }

    private static string Describe(List<LayoutLine> lines) =>
        string.Join(
            " | ",
            lines.Select(line => $"{line.W:R}:" + string.Join(
                ",",
                line.Glyphs.Select(glyph => $"{glyph.Start}-{glyph.End}@{glyph.X:R}/{glyph.Y:R}w{glyph.W:R}"))));

    [Fact]
    public void AMemoizedLayoutIsTheLayoutOfTheSameArguments()
    {
        const string text = "The quick brown fox jumps over the lazy dog, twice: the quick brown fox.";
        (_, ShapeCache cache, ShapeLine line) = Shape(text);
        float?[] widths = [null, 0f, 61.5f, 120f, 120.00001f, 400f];
        foreach (Wrap wrap in new[] { Wrap.WordOrGlyph, Wrap.None, Wrap.Glyph })
        {
            foreach (float? width in widths)
            {
                List<LayoutLine> first = cache.Layout(line, 16f, width, wrap, null, null);
                List<LayoutLine> second = cache.Layout(line, 16f, width, wrap, null, null);
                Assert.Same(first, second);
                Assert.Equal(
                    Describe(TextLayout.LayoutToBuffer(line, 16f, width, wrap, null, null)),
                    Describe(first));
            }
        }

        // Neighbouring widths that wrap differently never share an entry.
        Assert.NotEqual(
            Describe(cache.Layout(line, 16f, 61.5f, Wrap.WordOrGlyph, null, null)),
            Describe(cache.Layout(line, 16f, 400f, Wrap.WordOrGlyph, null, null)));
        Assert.True(cache.LayoutHits > 0);
    }

    [Fact]
    public void TheMemoKeepsABoundedNumberOfLayoutsPerParagraph()
    {
        (_, ShapeCache cache, ShapeLine line) = Shape("one two three four five six seven eight nine ten");
        for (int round = 0; round < 3; round++)
        {
            for (int width = 10; width < 10 + (ShapeCache.MemoSlots * 2); width++)
            {
                List<LayoutLine> layout = cache.Layout(line, 16f, width * 10f, Wrap.WordOrGlyph, null, null);
                Assert.Equal(
                    Describe(TextLayout.LayoutToBuffer(line, 16f, width * 10f, Wrap.WordOrGlyph, null, null)),
                    Describe(layout));
            }
        }

        Assert.NotNull(line.LayoutMemo);
        Assert.Equal(ShapeCache.MemoSlots, line.LayoutMemo!.Count);
    }
}
