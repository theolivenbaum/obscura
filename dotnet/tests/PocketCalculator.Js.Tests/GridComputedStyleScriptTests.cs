using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// <c>getComputedStyle()</c> for the CSS Grid properties. crates/obscura-js answers none of them
/// from the renderer, so a page read the inline declaration or the empty string. On a grid
/// container the track lists resolve to used sizes; elsewhere, and for placements, the value
/// is as specified. Every expected string is Chromium 141's for the same markup.
/// </summary>
public sealed class GridComputedStyleScriptTests
{
    private static string[] Evaluate(string html, string script)
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(html));
        rt.SetViewport(1280.0, 720.0);
        rt.RunPageInit();
        var result = rt.Evaluate(script) ?? throw new InvalidOperationException("no result");
        return [.. result.AsArray().Select(static node => node!.GetValue<string>())];
    }

    [Fact]
    public void GridContainerTracksResolveToUsedSizes()
    {
        string[] result = Evaluate(
            """
            <html><head><style>
              html, body { margin: 0 }
              #g { display: grid; grid-template-columns: 100px 1fr 2fr; width: 700px }
              #n { display: grid; grid-template-columns: [a] 100px [b c] 200px [d]; width: 500px }
              #r { display: grid; grid-template-columns: repeat(2, [x] 100px [y]); width: 500px }
              #f { display: grid; grid-template-columns: repeat(auto-fill, 100px); width: 450px }
            </style></head><body>
              <div id="g"><div id="a" style="grid-column: 2 / 4">a</div><div>b</div></div>
              <div id="n"><div>a</div></div>
              <div id="r"><div>a</div></div>
              <div id="f"><div>a</div></div>
            </body></html>
            """,
            """
            (() => {
              const cs = id => getComputedStyle(document.getElementById(id));
              return [cs("g").gridTemplateColumns, cs("g").gridTemplateRows, cs("n").gridTemplateColumns,
                cs("r").gridTemplateColumns, cs("f").gridTemplateColumns];
            })()
            """);
        Assert.Equal(
            ["100px 200px 400px", "18px 18px", "[a] 100px [b c] 200px [d]", "[x] 100px [y x] 100px [y]", "100px 100px 100px 100px"],
            result);
    }

    [Fact]
    public void NonContainerTracksKeepTheSpecifiedList()
    {
        string[] result = Evaluate(
            """
            <html><head><style>
              #b { grid-template-columns: repeat(3, 1fr) 20px; grid-template-rows: auto }
              #f { grid-template-columns: repeat(auto-fill, 100px) }
              #a { grid-auto-rows: minmax(20px, auto) 30px; grid-template-areas: "h h" "s m" }
            </style></head><body><div id="b"></div><div id="f"></div><div id="a"></div><div id="x"></div></body></html>
            """,
            """
            (() => {
              const cs = id => getComputedStyle(document.getElementById(id));
              return [cs("b").gridTemplateColumns, cs("b").gridTemplateRows, cs("f").gridTemplateColumns,
                cs("a").gridAutoRows, cs("a").gridTemplateAreas, cs("x").gridTemplateColumns,
                cs("x").gridAutoColumns, cs("x").gridTemplateAreas];
            })()
            """);
        Assert.Equal(
            ["repeat(3, 1fr) 20px", "auto", "repeat(auto-fill, 100px)", "minmax(20px, auto) 30px", "\"h h\" \"s m\"", "none", "auto", "none"],
            result);
    }

    [Fact]
    public void PlacementsSerializeAsSpecified()
    {
        string[] result = Evaluate(
            """
            <html><head><style>
              .g { display: grid; grid-template-columns: repeat(4, 50px) }
              #p { grid-column: 2 / span 2; grid-row: 3 }
              #s { grid-row: span 2 / -1 }
              #m { grid-area: m }
              #n { grid-row-start: 2; grid-row-end: foo }
            </style></head><body><div class="g" style="grid-template-areas: 'm m m m'">
              <div id="p"></div><div id="s"></div><div id="m"></div><div id="n"></div><div id="z"></div>
            </div></body></html>
            """,
            """
            (() => {
              const out = [];
              for (const id of ["p", "s", "m", "n", "z"]) {
                const cs = getComputedStyle(document.getElementById(id));
                out.push([cs.gridColumnStart, cs.gridColumnEnd, cs.gridRowStart, cs.gridRowEnd,
                  cs.gridColumn, cs.gridRow, cs.gridArea].join("|"));
              }
              return out;
            })()
            """);
        Assert.Equal(
            [
                "2|span 2|3|auto|2 / span 2|3|3 / 2 / auto / span 2",
                "auto|auto|span 2|-1|auto|span 2 / -1|span 2 / auto / -1",
                "m|m|m|m|m|m|m",
                "auto|auto|2|foo|auto|2 / foo|2 / auto / foo",
                "auto|auto|auto|auto|auto|auto|auto",
            ],
            result);
    }

    [Fact]
    public void RightToLeftTracksAreListedInLogicalOrder()
    {
        // The layout mirrors an RTL grid's columns; getComputedStyle lists them from the start
        // edge, implicit tracks included, as Chromium does.
        string[] result = Evaluate(
            """
            <html><head><style>
              #g { display: grid; direction: rtl; width: 300px; grid-template-columns: 10px 20px 30px;
                   grid-auto-columns: 5px 7px; grid-template-rows: 10px }
            </style></head><body><div id="g">
              <div style="grid-column: 5"></div><div style="grid-column: -6"></div>
            </div></body></html>
            """,
            """[getComputedStyle(document.getElementById("g")).gridTemplateColumns]""");
        Assert.Equal(["5px 7px 10px 20px 30px 5px 7px"], result);
    }

    [Fact]
    public void RelativeLengthsComputeToPx()
    {
        // em against the element's font-size, rem against the root's, vw against the viewport.
        string[] result = Evaluate(
            """
            <html style="font-size: 20px"><head><style>
              #n { font-size: 40px; grid-template-columns: 2em 1rem minmax(1em, 2em) repeat(2, 1em) fit-content(3em) 10vw;
                   grid-template-rows: 1em; grid-auto-columns: 1em; grid-auto-rows: minmax(1em, auto) }
            </style></head><body><div id="n"></div></body></html>
            """,
            """
            (() => {
              const cs = getComputedStyle(document.getElementById("n"));
              return [cs.gridTemplateColumns, cs.gridTemplateRows, cs.gridAutoColumns, cs.gridAutoRows];
            })()
            """);
        Assert.Equal(
            ["80px 20px minmax(40px, 80px) repeat(2, 40px) fit-content(120px) 128px", "40px", "40px", "minmax(40px, auto)"],
            result);
    }

    [Fact]
    public void MathFunctionsSerializeInCanonicalOrder()
    {
        // Chromium's computed serialization: lengths made px, like terms summed, a sum ordered
        // percentage, px, functions, and a lone term without calc().
        string[] input =
        [
            "calc(50px + 10%)", "calc(1em + 10px)", "calc(10% + 5px - 5px)", "calc(5px - 10%)",
            "calc(min(10%, 5px) + 2px)", "min(1em, 100px)", "max(1em, 10%)", "min(10% + 5px, 20px)",
            "calc(2 * (10% + 1em))", "calc(10% / 3)", "clamp(1em, 10%, 30%)", "calc(1em - 1em)",
            "minmax(calc(10% + 1em), max-content)", "fit-content(calc(1em + 1px))", "round(up, 10%, 3px)",
        ];
        string[] result = Evaluate(
            """<html style="font-size: 20px"><body><div id="x" style="font-size: 40px"></div></body></html>""",
            $$"""
            (() => {
              const e = document.getElementById("x");
              return {{System.Text.Json.JsonSerializer.Serialize(input)}}.map(v => {
                e.style.gridTemplateColumns = v;
                return getComputedStyle(e).gridTemplateColumns;
              });
            })()
            """);
        Assert.Equal(
            [
                "calc(10% + 50px)", "50px", "10%", "calc(-10% + 5px)",
                "calc(2px + min(10%, 5px))", "40px", "max(40px, 10%)", "min(10% + 5px, 20px)",
                "calc(20% + 80px)", "3.33333%", "clamp(40px, 10%, 30%)", "0px",
                "minmax(calc(10% + 40px), max-content)", "fit-content(41px)", "round(up, 10%, 3px)",
            ],
            result);
    }

    [Theory]
    [InlineData("block", "", "none", "none / none / none / row / auto / auto")]
    [InlineData("block", "grid-template-columns:10px;grid-template-rows:20px 30px", "20px 30px / 10px", "20px 30px / 10px / none / row / auto / auto")]
    [InlineData("block", "grid-template-areas:'a b' 'c d'", "", "none / none / \"a b\" \"c d\" / row / auto / auto")]
    [InlineData("block", "grid-template:[a] 'x' 10px [b] 'y' [c] / 1fr", "[a] \"x\" 10px [b] \"y\" [c] / 1fr", "[a] 10px [b] auto [c] / 1fr / \"x\" \"y\" / row / auto / auto")]
    [InlineData("block", "grid:auto-flow dense 10px / 20px 30px", "none / 20px 30px", "none / 20px 30px / none / dense / 10px / auto")]
    [InlineData("block", "grid-template-rows:repeat(auto-fill, 10px)", "repeat(auto-fill, 10px) / none", "repeat(auto-fill, 10px) / none / none / row / auto / auto")]
    [InlineData("grid", "", "0px / 300px", "0px / 300px / none / row / auto / auto")]
    [InlineData("grid", "grid-template-columns:1fr 2fr", "0px / 1fr 2fr", "0px / 100px 200px / none / row / auto / auto")]
    [InlineData("grid", "grid-template-areas:'a b' 'c d'", "\"a b\" 0px \"c d\" 0px / 150px 150px", "0px 0px / 150px 150px / \"a b\" \"c d\" / row / auto / auto")]
    [InlineData("grid", "grid-template:'a a' 10px 'b c' 20px / 1fr 2fr", "\"a a\" 10px \"b c\" 20px / 1fr 2fr", "10px 20px / 100px 200px / \"a a\" \"b c\" / row / auto / auto")]
    [InlineData("grid", "grid:10px / auto-flow dense 20px", "10px / 20px", "10px / 20px / none / column dense / auto / 20px")]
    [InlineData("grid", "grid-template-columns:repeat(2, 10px)", "0px / repeat(2, 10px)", "0px / 10px 10px / none / row / auto / auto")]
    public void TemplateAndGridShorthandsSerializeAsChromium(string display, string declarations, string template, string grid)
    {
        // Chromium 141: grid-template from the computed track lists (the used ones where a list
        // is none, row strings before their sizes); grid as all six longhands, tracks resolved.
        string[] result = Evaluate(
            $$"""
            <html><body style="margin: 0"><div style="width: 300px">
              <div id="e" style="display: {{display}}; {{declarations}}"><i></i></div>
            </div></body></html>
            """,
            """
            (() => {
              const cs = getComputedStyle(document.getElementById("e"));
              return [cs.gridTemplate, cs.grid];
            })()
            """);
        Assert.Equal([template, grid], result);
    }
}
