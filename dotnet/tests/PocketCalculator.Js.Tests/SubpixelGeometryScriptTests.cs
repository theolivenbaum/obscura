using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// CSSOM View geometry at Chromium's precision. Blink lays out in LayoutUnits (1/64px) and
/// snaps to pixels only when it paints, so <c>getBoundingClientRect()</c> is fractional while
/// the <c>offset*</c> and <c>client*</c> properties are integers. The port's taffy layout rounds
/// its final pass for paint; these read the unrounded layout beside it. Every expected value
/// is measured in Chromium 141 at the same viewport.
/// </summary>
public sealed class SubpixelGeometryScriptTests
{
    private static JsonNode? Measure(string html, string script, double width = 1280.0, double height = 800.0)
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(html));
        rt.SetViewport(width, height);
        rt.RunPageInit();
        return rt.Evaluate(script);
    }

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"expected {expected}\n  actual {actual?.ToJsonString() ?? "null"}");

    private const string RectScript =
        """
        (() => {
          const out = {};
          for (const id of ["c", "f", "ib"]) {
            const e = document.getElementById(id);
            const r = e.getBoundingClientRect();
            out[id] = [r.x, r.y, r.width, r.height,
                       e.offsetLeft, e.offsetTop, e.offsetWidth, e.offsetHeight, e.clientWidth];
          }
          return out;
        })()
        """;

    [Fact]
    public void BoundingRectKeepsTheFractionAndOffsetsRound()
    {
        // A 200.4px float and a 299.5px inline-block fill a 500px line. Chromium stores the
        // float's width as LayoutUnit(200.4), which truncates to 200.390625.
        var result = Measure(
            """
            <!doctype html><html><body style="margin:0">
              <div id="c" style="width:500px"><div id="f" style="float:left;width:200.4px;height:20px"></div><div id="ib" style="display:inline-block;width:299.5px;height:20px"></div></div>
            </body></html>
            """,
            RectScript);

        AssertJson(
            """
            {
              "c": [0, 0, 500, 24, 0, 0, 500, 24, 500],
              "f": [0, 0, 200.390625, 20, 0, 0, 200, 20, 200],
              "ib": [200.390625, 0, 299.5, 20, 200, 0, 300, 20, 300]
            }
            """,
            result);
    }

    [Fact]
    public void PercentageWidthsReportLayoutUnits()
    {
        // The wikipedia.org footer shape: a 98% container with 12.8px of padding-equivalent
        // margin, a fixed float and an inline-block that together fit the line exactly.
        var result = Measure(
            """
            <!doctype html><html><body style="margin:0">
              <div id="c" style="width:98%;margin:0 auto"><div id="f" style="float:left;width:439px;height:20px"></div><div id="ib" style="display:inline-block;width:815.25px;height:20px"></div></div>
            </body></html>
            """,
            RectScript);

        AssertJson(
            """
            {
              "c": [12.796875, 0, 1254.390625, 24, 13, 0, 1254, 24, 1254],
              "f": [12.796875, 0, 439, 20, 13, 0, 439, 20, 439],
              "ib": [451.796875, 0, 815.25, 20, 452, 0, 815, 20, 815]
            }
            """,
            result);
    }

    [Fact]
    public void OffsetMetricsAreRelativeToTheOffsetParentAndIgnoreScrolling()
    {
        var result = Measure(
            """
            <!doctype html><html><body style="margin:8px;border:3px solid">
              <div id="a" style="position:relative;left:10px;top:5px;border:2px solid;padding:3.3px;margin-left:20px;width:300.7px">
                <div id="b" style="margin-left:7.3px;height:10.5px;width:50.6px"></div>
                <div style="transform:translateX(40px)"><div id="d" style="width:10px;height:10px"></div></div>
              </div>
              <div id="e" style="width:33.3px;height:12.7px;margin-left:0.6px"></div>
              <div id="f" style="position:fixed;top:7.7px;left:9.4px;width:5.5px;height:5px"></div>
              <div id="g" style="display:none"></div>
              <div style="height:2000px"></div>
            </body></html>
            """,
            """
            (() => {
              window.scrollTo(0, 37);
              const out = {};
              for (const id of ["a", "b", "d", "e", "f", "g"]) {
                const e = document.getElementById(id);
                const p = e.offsetParent;
                out[id] = [p ? (p.id || p.tagName) : null,
                           e.offsetLeft, e.offsetTop, e.offsetWidth, e.offsetHeight];
              }
              out.body = [document.body.offsetParent, document.body.offsetLeft,
                          document.body.offsetTop, document.body.offsetWidth];
              out.bRect = document.getElementById("b").getBoundingClientRect().width;
              return out;
            })()
            """);

        AssertJson(
            """
            {
              "a": ["BODY", 41, 16, 311, 31],
              "b": ["a", 11, 3, 51, 11],
              "d": ["DIV", 0, 0, 10, 10],
              "e": ["BODY", 12, 42, 33, 13],
              "f": [null, 9, 8, 6, 5],
              "g": [null, 0, 0, 0, 0],
              "body": [null, 0, 0, 1264],
              "bRect": 50.59375
            }
            """,
            result);
    }

    [Fact]
    public void AtomicInlineLineBoxCarriesTheFlooredStrutDescent()
    {
        // The strut's leading is split with the ascent half floored, as text lines split it:
        // 16px Liberation Serif is 14 + 3 with 18px normal line-height, so the descent half is
        // 4 and the line is 20 + 4. An even split made it 23.5.
        var result = Measure(
            """
            <!doctype html><html><body style="margin:0">
              <div id="a" style="font:16px serif"><span style="display:inline-block;width:10px;height:20px"></span></div>
              <div id="b" style="font:13px sans-serif;line-height:1.5"><span style="display:inline-block;width:10px;height:20px"></span></div>
            </body></html>
            """,
            """
            [document.getElementById("a").getBoundingClientRect().height,
             document.getElementById("b").getBoundingClientRect().height]
            """);

        AssertJson("[24, 25.5]", result);
    }

    [Fact]
    public void UnitlessLineHeightTruncatesToLayoutUnits()
    {
        // 17px * 1.2 is 20.4, which Blink stores as LayoutUnit(20.4) = 20.390625; a length
        // line-height (here 20.3px) rounds instead. getComputedStyle keeps the plain product.
        var result = Measure(
            """
            <!doctype html><html><body style="margin:0">
              <div id="a" style="font:17px sans-serif;line-height:1.2">Hi</div>
              <div id="b" style="font:11px Arial;line-height:0.9;width:60px">aaaa bbbb cccc</div>
              <div id="c" style="font:16px serif;line-height:20.3px">Hi</div>
            </body></html>
            """,
            """
            [document.getElementById("a").getBoundingClientRect().height,
             document.getElementById("b").getBoundingClientRect().height,
             document.getElementById("c").getBoundingClientRect().height,
             getComputedStyle(document.getElementById("a")).lineHeight]
            """);

        AssertJson("""[20.390625, 19.78125, 20.296875, "20.4px"]""", result);
    }
}
