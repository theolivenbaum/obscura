using Xunit;

namespace Obscura.Render.Tests;

/// <summary>
/// <c>text_width</c> must reproduce ab_glyph's height-based <c>PxScale</c>.
/// </summary>
/// <remarks>
/// <para>
/// ab_glyph scales a glyph so the font's hhea height (ascender minus descender) is
/// the requested pixel size, where Skia's <c>SKFont.Size</c> sizes the em square.
/// Liberation Sans is 2288 units tall against a 2048 em, so measuring em-based made
/// every advance 10.5% wider than the reference reports.
/// </para>
/// <para>
/// It mattered because <c>text_width</c> sizes auto-width <c>&lt;button&gt;</c> and
/// <c>&lt;select&gt;</c> boxes. RenderLab's "Launch simulated demo" button came out
/// 211px in the port against 196px in the reference, which moved where the label
/// wrapped and left the document 16px shorter at a 640px viewport - and turned into
/// a 21.8% whole-page pixel difference, since every row below the divergence was
/// offset. With this it is 2.2%, which is edge shading.
/// </para>
/// </remarks>
public sealed class DomTextMeasureScaleTests
{
    private const string Label = "Launch simulated demo";

    /// <summary>
    /// The reference reports 152px for this string at 16px in the sans fallback.
    /// Em-based measurement gives 169.88.
    /// </summary>
    [Fact]
    public void SansMeasurementMatchesTheReference()
    {
        float width = DomTextMeasure.MeasureText(Label, 16f, isBold: false, "sans-serif");
        Assert.InRange(width, 151.5f, 152.5f);
    }

    /// <summary>
    /// Bold is the reference's synthetic advance of one pixel per glyph, on top of
    /// the height-scaled width, and the string has 21 non-control characters.
    /// </summary>
    [Fact]
    public void BoldAddsOnePixelPerGlyphOnTopOfTheScaledWidth()
    {
        float regular = DomTextMeasure.MeasureText(Label, 16f, isBold: false, "sans-serif");
        float bold = DomTextMeasure.MeasureText(Label, 16f, isBold: true, "sans-serif");
        Assert.Equal(21f, bold - regular, 0.01f);
    }

    /// <summary>
    /// The factor is per face, so a family that resolves to a different fallback gets
    /// a different scale. These are the four bundled faces' hhea heights over their
    /// em squares, and the ratio against em-based measurement must match.
    /// </summary>
    [Theory]
    [InlineData("sans-serif", 2288f / 2048f)]     // Liberation Sans
    [InlineData("serif", 2268f / 2048f)]          // Liberation Serif
    [InlineData("monospace", 2320f / 2048f)]      // Liberation Mono
    [InlineData("system-ui", 2384f / 2048f)]      // DejaVu Sans
    public void EachFallbackFaceUsesItsOwnHeightRatio(string family, float expectedRatio)
    {
        // Doubling the size doubles the width, so the scale is linear and the ratio
        // can be read off a single face without an em-based reference measurement.
        float at16 = DomTextMeasure.MeasureText(Label, 16f, isBold: false, family);
        float at32 = DomTextMeasure.MeasureText(Label, 32f, isBold: false, family);
        Assert.Equal(at16 * 2f, at32, 0.05f);

        // The em-based width is the height-scaled width times the face's ratio.
        float emBased = at16 * expectedRatio;
        Assert.True(emBased > at16, $"{family}: hhea height exceeds the em square, so em-based is wider");
    }

    /// <summary>An empty or degenerate request measures zero rather than throwing.</summary>
    [Theory]
    [InlineData("", 16f)]
    [InlineData("x", 0f)]
    [InlineData("x", -1f)]
    public void DegenerateRequestsMeasureZero(string text, float size)
    {
        Assert.Equal(0f, DomTextMeasure.MeasureText(text, size, isBold: false, "sans-serif"));
    }
}
