using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// <c>Range.getClientRects()</c> and <c>getBoundingClientRect()</c> from the laid-out line
/// fragments (<c>op_range_rects</c>). Every expected rect is Chromium 141's for the same page at
/// a 1280x800 viewport; coordinates are compared within 0.05px.
/// </summary>
public sealed class RangeClientRectsTests
{
    private const string Page =
        """
        <!doctype html><html><body style="margin:0;font:16px/20px 'Liberation Sans'">
        <div id=c style="width:300px"><p id=p style="margin:0">Lorem <span id=s style="padding:0 4px">ipsum dolor</span> sit amet consectetur adipiscing elit sed do eiusmod tempor</p><p id=q style="margin:0;text-align:center;font-size:20px">second <b id=b>bold</b> para</p></div>
        <pre id=pre style="margin:0;font:16px/20px 'Liberation Mono'">line one
          line two</pre>
        </body></html>
        """;

    private const string Script =
        """
        (() => {
          const p = document.getElementById('p'), s = document.getElementById('s'), q = document.getElementById('q'), b = document.getElementById('b');
          const c = document.getElementById('c'), pre = document.getElementById('pre');
          const t1 = p.firstChild, t3 = s.nextSibling, tq = q.firstChild, tb = b.firstChild, tq2 = b.nextSibling;
          const cases = {
            mid: [t1, 2, t1, 4],
            collapsed: [t1, 3, t1, 3],
            acrossSpan: [t1, 2, t3, 4],
            wraps: [t3, 10, t3, 40],
            elemBoundary: [c, 0, c, 1],
            acrossParas: [t3, 50, tq, 3],
            centered: [tq, 0, tq2, 3],
            collapsedElem: [p, 1, p, 1],
            newline: [pre.firstChild, 5, pre.firstChild, 14],
          };
          const out = {};
          for (const [k, [a, ao, e, eo]] of Object.entries(cases)) {
            const r = document.createRange(); r.setStart(a, ao); r.setEnd(e, eo);
            const bound = r.getBoundingClientRect();
            out[k] = {
              rects: Array.from(r.getClientRects()).map((x) => [x.x, x.y, x.width, x.height]),
              bound: [bound.x, bound.y, bound.width, bound.height],
            };
          }
          return out;
        })()
        """;

    // Chromium 141, rounded to 0.01.
    private const string Expected =
        """
        {
          "mid": {"rects": [[17.8, 1, 14.23, 17]], "bound": [17.8, 1, 14.23, 17]},
          "collapsed": {"rects": [[23.13, 1, 0, 17]], "bound": [23.13, 1, 0, 17]},
          "acrossSpan": {"rects": [[17.8, 1, 32, 17], [49.8, 1, 90.7, 17], [53.8, 1, 82.7, 17], [140.5, 1, 20.45, 17]], "bound": [17.8, 1, 143.16, 17]},
          "wraps": {"rects": [[205.41, 1, 82.72, 17], [0, 21, 117.41, 17]], "bound": [0, 1, 288.13, 37]},
          "elemBoundary": {"rects": [[0, 0, 300, 40], [0, 1, 49.8, 17], [53.8, 1, 82.7, 17], [140.5, 1, 147.63, 17], [0, 21, 267.7, 17]], "bound": [0, 0, 300, 40]},
          "acrossParas": {"rects": [[195.66, 21, 72.05, 17], [71.06, 39, 31.13, 22]], "bound": [71.06, 21, 196.64, 40]},
          "centered": {"rects": [[71.06, 39, 70.06, 22], [141.13, 39, 42.22, 22], [141.13, 39, 42.22, 22], [183.34, 39, 27.81, 22]], "bound": [71.06, 39, 140.09, 22]},
          "collapsedElem": {"rects": [], "bound": [0, 0, 0, 0]},
          "newline": {"rects": [[48, 61, 28.81, 18], [76.81, 61, 0, 18], [0, 81, 48.02, 18]], "bound": [0, 61, 76.81, 38]}
        }
        """;

    [Fact]
    public void RangeRectsAreTheSelectedTextPerLineAndTheContainedElementsBoxes()
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(Page));
        rt.SetViewport(1280.0, 800.0);
        rt.RunPageInit();
        JsonNode actual = rt.Evaluate(Script)!;
        JsonNode expected = JsonNode.Parse(Expected)!;
        foreach ((string name, JsonNode? want) in expected.AsObject())
        {
            JsonNode got = actual[name]!;
            AssertRects(name + ".rects", want!["rects"]!.AsArray(), got["rects"]!.AsArray());
            AssertRects(name + ".bound", new JsonArray(want["bound"]!.DeepClone()), new JsonArray(got["bound"]!.DeepClone()));
        }
    }

    private static void AssertRects(string name, JsonArray expected, JsonArray actual)
    {
        Assert.True(expected.Count == actual.Count, $"{name}: expected {expected.ToJsonString()} got {actual.ToJsonString()}");
        for (int i = 0; i < expected.Count; i++)
        {
            JsonArray want = expected[i]!.AsArray();
            JsonArray got = actual[i]!.AsArray();
            for (int k = 0; k < 4; k++)
            {
                double w = want[k]!.GetValue<double>();
                double g = got[k]!.GetValue<double>();
                Assert.True(Math.Abs(w - g) <= 0.05, $"{name}[{i}][{k}]: expected {w} got {g} ({got.ToJsonString()})");
            }
        }
    }
}
