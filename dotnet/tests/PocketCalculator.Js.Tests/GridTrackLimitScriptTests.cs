using System.Diagnostics;
using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Grid track limits as a page sees them: nested <c>repeat()</c> is an invalid declaration, and
/// huge repeat counts, line numbers and auto-fill counts are clamped (Chromium's
/// <c>kGridMaxTracks</c>) instead of expanding without a bound. Expected values are measured
/// in Chromium 141. The port does not serialize resolved grid tracks in
/// <c>getComputedStyle</c> (a pre-existing gap), so geometry is read instead.
/// </summary>
public sealed class GridTrackLimitScriptTests
{
    [Fact]
    public void CssSupportsRejectsNestedRepeatAndAcceptsHugeCounts()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
              let nested = "1fr";
              for (let i = 0; i < 40; i++) nested = `repeat(2, ${nested})`;
              return [
                CSS.supports("grid-template-columns", "repeat(2, repeat(2, 1px))"),
                CSS.supports("grid-template-rows", nested),
                CSS.supports("grid-template-columns", "repeat(2, repeat(auto-fill, 1px))"),
                CSS.supports("grid-template-columns", "repeat(0, 1px)"),
                CSS.supports("grid-template-columns", "repeat(100000000, 1px)"),
                CSS.supports("grid-column", "1 / 99999999"),
                CSS.supports("column-count", "2147483648"),
                CSS.supports("column-count", "1e9")
              ];
            })()
            """);
        Assert.Equal("[false,false,false,false,true,true,true,false]", result?.ToJsonString());
    }

    [Fact]
    public void OverLimitGridsLayOutQuicklyWithChromiumGeometry()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        string nested = "1fr";
        for (int i = 0; i < 22; i++)
        {
            nested = $"repeat(2, {nested})";
        }

        rt.SetDom(HtmlParsing.ParseHtml(
            $$"""
            <html><head><style>
              html, body { margin: 0 }
              .g { display: grid; width: 1000px; grid-template-columns: 10px 10px }
              #nested { grid-template-columns: 100px 100px; grid-template-columns: {{nested}} }
              #huge { grid-template-columns: repeat(100000000, 1px) }
              #fill { width: 100000000px; grid-template-columns: repeat(auto-fill, 1px) }
              #wide > :first-child { grid-column: 1 / 99999999 }
            </style></head><body>
              <div class="g" id="nested"><div>a</div><div>b</div></div>
              <div class="g" id="huge"><div>a</div><div>b</div></div>
              <div class="g" id="fill"><div>a</div><div>b</div></div>
              <div class="g" id="wide"><div>a</div><div>b</div></div>
            </body></html>
            """));
        rt.SetViewport(1000.0, 600.0);
        rt.RunPageInit();

        var clock = Stopwatch.StartNew();
        var result = rt.Evaluate(
            """
            (() => {
              const w = (id, n) => document.getElementById(id).children[n].getBoundingClientRect().width;
              return [w("nested", 0), w("huge", 0), w("fill", 0), w("wide", 0)];
            })()
            """);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"layout took {clock.Elapsed}");
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse("[100,1,1,1000]"), result),
            result?.ToJsonString());
    }
}
