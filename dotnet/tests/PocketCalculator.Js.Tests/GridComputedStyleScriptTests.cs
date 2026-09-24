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
}
