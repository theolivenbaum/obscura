using PocketCalculator.Render;
using PocketCalculator.Render.Css;
using Xunit;
using RgbaColor = PocketCalculator.Render.Css.RgbaColor;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// Regression tests for the computed-color serialization defects a 157-route
/// differential survey against Chromium turned up.
/// </summary>
public class PaintColorTests
{
    /// <summary>
    /// What Chromium reports for <c>rgba(1, 2, 3, a / 255)</c> for every
    /// <c>a</c> in 0..255, captured from the bundled headless Chromium.
    /// </summary>
    private const string CHROMIUM_ALPHA_TABLE =
        "0,0.004,0.008,0.01,0.016,0.02,0.024,0.027,0.03,0.035,0.04,0.043,0.047,0.05,0.055,0.06,0.063,0.067,0."
        + "07,0.075,0.08,0.082,0.086,0.09,0.094,0.098,0.1,0.106,0.11,0.114,0.118,0.12,0.125,0.13,0.133,0.137,0."
        + "14,0.145,0.15,0.153,0.157,0.16,0.165,0.17,0.173,0.176,0.18,0.184,0.19,0.192,0.196,0.2,0.204,0.208,0."
        + "21,0.216,0.22,0.224,0.227,0.23,0.235,0.24,0.243,0.247,0.25,0.255,0.26,0.263,0.267,0.27,0.275,0.28,0."
        + "282,0.286,0.29,0.294,0.298,0.3,0.306,0.31,0.314,0.318,0.32,0.325,0.33,0.333,0.337,0.34,0.345,0.35,0."
        + "353,0.357,0.36,0.365,0.37,0.373,0.376,0.38,0.384,0.39,0.392,0.396,0.4,0.404,0.408,0.41,0.416,0.42,0."
        + "424,0.427,0.43,0.435,0.44,0.443,0.447,0.45,0.455,0.46,0.463,0.467,0.47,0.475,0.48,0.482,0.486,0.49,0"
        + ".494,0.498,0.5,0.506,0.51,0.514,0.518,0.52,0.525,0.53,0.533,0.537,0.54,0.545,0.55,0.553,0.557,0.56,0"
        + ".565,0.57,0.573,0.576,0.58,0.584,0.59,0.592,0.596,0.6,0.604,0.608,0.61,0.616,0.62,0.624,0.627,0.63,0"
        + ".635,0.64,0.643,0.647,0.65,0.655,0.66,0.663,0.667,0.67,0.675,0.68,0.682,0.686,0.69,0.694,0.698,0.7,0"
        + ".706,0.71,0.714,0.718,0.72,0.725,0.73,0.733,0.737,0.74,0.745,0.75,0.753,0.757,0.76,0.765,0.77,0.773,"
        + "0.776,0.78,0.784,0.79,0.792,0.796,0.8,0.804,0.808,0.81,0.816,0.82,0.824,0.827,0.83,0.835,0.84,0.843,"
        + "0.847,0.85,0.855,0.86,0.863,0.867,0.87,0.875,0.88,0.882,0.886,0.89,0.894,0.898,0.9,0.906,0.91,0.914,"
        + "0.918,0.92,0.925,0.93,0.933,0.937,0.94,0.945,0.95,0.953,0.957,0.96,0.965,0.97,0.973,0.976,0.98,0.984"
        + ",0.99,0.992,0.996,1";

    private static string Serialize(string css) =>
        PaintCssValues.CssColor(CssColor.Parse(css)!.Value);

    private static string BackgroundColorOf(string declarations) =>
        PaintCssValues.CssColor(
            ComputedStyle.Compute("div", declarations).BackgroundColor
                ?? new RgbaColor(0, 0, 0, 0));

    [Theory]
    [InlineData("rgba(4, 67, 211, 0.1)", "rgba(4, 67, 211, 0.1)")]
    [InlineData("rgba(4, 67, 211, 0.12)", "rgba(4, 67, 211, 0.12)")]
    [InlineData("rgba(40, 167, 69, 0.5)", "rgba(40, 167, 69, 0.5)")]
    [InlineData("rgba(127, 127, 127, 0.06)", "rgba(127, 127, 127, 0.06)")]
    [InlineData("rgba(50, 49, 48, 0.024)", "rgba(50, 49, 48, 0.024)")]
    [InlineData("rgba(4, 67, 211, 0.14)", "rgba(4, 67, 211, 0.14)")]
    public void AlphaKeepsAuthoredSpelling(string input, string expected) =>
        Assert.Equal(expected, Serialize(input));

    /// <summary>
    /// Blink quantizes alpha to 8 bits too - it reports <c>rgba(1, 2, 3, 0.9999)</c> as
    /// opaque - so the whole of the difference was in how the byte is spelled back out.
    /// </summary>
    [Fact]
    public void AlphaSerializationMatchesChromiumForEveryByte()
    {
        string[] expected = CHROMIUM_ALPHA_TABLE.Split(',');
        Assert.Equal(256, expected.Length);

        for (int alpha = 0; alpha < 256; alpha++)
        {
            string actual = alpha == 255 ? "1" : PaintCssValues.CssAlpha((byte)alpha);
            Assert.Equal(expected[alpha], actual);
        }
    }

    [Fact]
    public void OpaqueColorsStillSerializeAsRgb() =>
        Assert.Equal("rgb(255, 255, 255)", Serialize("rgba(255, 255, 255, 1)"));

    /// <summary>
    /// <c>background: var(--x) none repeat scroll 0% 0%</c> with the custom property
    /// resolving to <c>rgb(...)</c> is what Tesserae's <c>.tss-searchbox-container</c> and
    /// <c>.tss-dropdown-container</c> compute to. The functional color used to fail to
    /// parse out of the layer and the element lost its background entirely.
    /// </summary>
    [Theory]
    [InlineData("background: rgb(255, 255, 255) none repeat scroll 0% 0%;", "rgb(255, 255, 255)")]
    [InlineData("background: rgba(4, 67, 211, 0.12) none repeat scroll 0% 0%;", "rgba(4, 67, 211, 0.12)")]
    [InlineData("background: hsl(0, 0%, 100%) none repeat scroll 0% 0%;", "rgb(255, 255, 255)")]
    [InlineData("background: #ffffff none repeat scroll 0% 0%;", "rgb(255, 255, 255)")]
    [InlineData("background: none repeat scroll 0% 0% rgb(249, 250, 251);", "rgb(249, 250, 251)")]
    [InlineData("background: url(icon.png) no-repeat center / cover rgb(1, 2, 3);", "rgb(1, 2, 3)")]
    public void BackgroundShorthandLayerKeepsItsColor(string declarations, string expected) =>
        Assert.Equal(expected, BackgroundColorOf(declarations));

    /// <summary>
    /// The leniency above must not leak into the longhands: a value that is not a single
    /// <c>&lt;color&gt;</c> and not a plausible background layer still invalidates.
    /// </summary>
    [Theory]
    [InlineData("background-color: rgb(1, 2, 3) garbage;")]
    [InlineData("background-color: rgb(1, 2, 3) definitely-not-a-keyword;")]
    public void MalformedColorLonghandIsStillRejected(string declarations) =>
        Assert.Null(ComputedStyle.Compute("div", declarations).BackgroundColor);

    /// <summary>
    /// Obscura dropped <c>color(srgb ...)</c> on the floor, so the element painted no
    /// background at all. The model is 8-bit sRGB, so it reads back as legacy rgb()/rgba()
    /// rather than Chromium's wide-gamut spelling - the numbers are the same.
    /// </summary>
    [Theory]
    [InlineData("color(srgb 0.0156863 0.262745 0.827451 / 0.14)", "rgba(4, 67, 211, 0.14)")]
    [InlineData("color(srgb 1 0 0)", "rgb(255, 0, 0)")]
    [InlineData("color(srgb 100% 0% 0%)", "rgb(255, 0, 0)")]
    [InlineData("color(srgb 1 1 1 / 50%)", "rgba(255, 255, 255, 0.5)")]
    public void ColorFunctionInSrgbIsParsed(string input, string expected) =>
        Assert.Equal(expected, Serialize(input));

    [Theory]
    [InlineData("color(display-p3 1 0 0)")]
    [InlineData("color(srgb 1 0)")]
    [InlineData("color(srgb 1 0 0) trailing")]
    public void UnsupportedOrMalformedColorFunctionIsRejected(string value) =>
        Assert.Null(CssColor.Parse(value));

    [Theory]
    [InlineData("light-dark(red, blue) trailing")]
    [InlineData("light-dark(red)")]
    [InlineData("light-dark(red garbage, blue)")]
    public void MalformedLightDarkIsStillRejected(string value) =>
        Assert.Null(CssColor.Parse(value));
}
