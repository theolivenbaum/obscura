using System.Diagnostics;
using System.Text;
using PocketCalculator.Render.Css;
using Xunit;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// H8: work inside ops is bounded. <c>&lt;use&gt;</c> fan-out has a per-render element budget,
/// and <c>var()</c> substitution a length cap past which the value is guaranteed-invalid.
/// </summary>
public class WorkBudgetTests
{
    /// <summary>Custom properties where each level references the previous one ten times.</summary>
    private static Dictionary<string, string> FanOutProperties(int levels)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal) { ["--v0"] = "x" };
        for (int level = 1; level <= levels; level++)
        {
            var value = new StringBuilder();
            for (int i = 0; i < 10; i++)
            {
                value.Append("var(--v").Append(level - 1).Append(") ");
            }

            properties[$"--v{level}"] = value.ToString();
        }

        return properties;
    }

    [Fact]
    public void VarSubstitutionOverTheLengthCapIsGuaranteedInvalid()
    {
        // Seven levels substitute to 2 * 10^7 characters, past the 2 MiB cap.
        Dictionary<string, string> properties = FanOutProperties(7);
        Assert.Null(CssVariables.SubstituteVarValue("var(--v7)", properties, 0));

        // Five levels are 2 * 10^5 characters and still substitute.
        string? five = CssVariables.SubstituteVarValue("var(--v5)", properties, 0);
        Assert.NotNull(five);
        Assert.True(five!.Length > 100_000);
    }

    [Fact]
    public void CustomPropertyOverTheLengthCapIsInvalidAndItsFallbackApplies()
    {
        var declarations = new StringBuilder("--v0:x;");
        for (int level = 1; level <= 7; level++)
        {
            declarations.Append("--v").Append(level).Append(':');
            for (int i = 0; i < 10; i++)
            {
                declarations.Append("var(--v").Append(level - 1).Append(") ");
            }

            declarations.Append(';');
        }

        PocketCalculator.Dom.DomTree tree = PocketCalculator.Dom.HtmlParsing.ParseHtml(
            $"<html style='margin:0'><body style='margin:0'><div style='width:20px;height:20px;{declarations}"
            + "background-color:var(--v7, rgb(0, 255, 0))'></div></body></html>");
        using Pixmap pixmap = RenderPaint.PaintDom(tree, (40f, 40f), null)!;
        PremultipliedColor pixel = pixmap.Pixels[(10 * 40) + 10];
        Assert.Equal((0, 255, 0, 255), (pixel.R, pixel.G, pixel.B, pixel.A));
    }

    [Fact]
    public void DeepVarFanOutFinishesQuickly()
    {
        Dictionary<string, string> properties = FanOutProperties(16);
        var stopwatch = Stopwatch.StartNew();
        Assert.Null(CssVariables.SubstituteVarValue("var(--v16)", properties, 0));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public void SvgUseFanOutStopsAtTheElementBudget()
    {
        // Ten <use> per level, eight levels deep: 10^8 rects if expanded in full.
        var svg = new StringBuilder(
            "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><defs>"
            + "<rect id='l0' width='20' height='20' fill='red'/>");
        for (int level = 1; level <= 8; level++)
        {
            svg.Append("<g id='l").Append(level).Append("'>");
            for (int i = 0; i < 10; i++)
            {
                svg.Append("<use href='#l").Append(level - 1).Append("'/>");
            }

            svg.Append("</g>");
        }

        svg.Append("</defs><use href='#l8'/></svg>");
        byte[] bytes = Encoding.UTF8.GetBytes(svg.ToString());

        var run = Task.Run(() => SvgRenderer.Render(bytes, 20, 20));
        Assert.True(run.Wait(TimeSpan.FromSeconds(30)), "<use> fan-out did not finish within 30 s");
        using Pixmap? pixmap = run.Result;
        Assert.NotNull(pixmap);

        // The part expanded inside the budget still paints.
        PremultipliedColor pixel = pixmap!.Pixels[(10 * 20) + 10];
        Assert.Equal((255, 0, 0, 255), (pixel.R, pixel.G, pixel.B, pixel.A));
    }
}
