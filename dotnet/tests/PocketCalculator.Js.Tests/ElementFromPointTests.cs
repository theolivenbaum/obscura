using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// <c>document.elementFromPoint</c>, <c>elementsFromPoint</c> and the shadow root's, through the
/// renderer's hit test (<c>op_hit_test</c>). Every expected value is Chromium 141's for the same
/// page at a 1280x800 viewport.
/// </summary>
public sealed class ElementFromPointTests
{
    private const string Page =
        """
        <!doctype html><html><body style="margin:0;font:16px/20px 'Liberation Sans'">
        <div id=c style="width:400px;background:#eee"><div id=f style="float:left;width:100px;height:60px;background:#0a0"></div><p id=p style="margin:0">Lorem <span id=s>ipsum dolor</span> sit amet consectetur adipiscing elit sed do eiusmod tempor</p></div>
        <div id=z1 style="position:absolute;left:10px;top:100px;width:100px;height:50px;z-index:2;background:red"></div>
        <div id=z2 style="position:absolute;left:50px;top:120px;width:100px;height:50px;z-index:1;background:blue"></div>
        <div id=pe style="position:absolute;left:200px;top:100px;width:100px;height:50px;pointer-events:none;background:#ccc"><div id=pea style="pointer-events:auto;width:40px;height:20px;background:#333"></div></div>
        <div id=host style="position:absolute;left:0;top:200px;width:200px;height:40px"></div>
        </body></html>
        """;

    private const string Script =
        """
        (() => {
          document.getElementById('host').attachShadow({ mode: 'open' }).innerHTML =
            '<div id=inner style="width:100px;height:30px;background:pink">in</div>';
          const l = (e) => e ? (e.id || e.tagName.toLowerCase()) : null;
          const pts = [[5,5],[150,5],[60,10],[350,30],[20,110],[60,130],[120,160],[210,105],[260,120],[5,205],[150,205],[600,600]];
          const sr = document.getElementById('host').shadowRoot;
          return {
            one: pts.map(([x, y]) => l(document.elementFromPoint(x, y))),
            all: pts.map(([x, y]) => document.elementsFromPoint(x, y).map(l).join(',')),
            shadow: [l(sr.elementFromPoint(5, 205)), l(sr.elementFromPoint(150, 205)), sr.elementsFromPoint(5, 205).map(l).join(',')],
          };
        })()
        """;

    private static JsonNode? Measure(string html, string script)
    {
        using var fixture = RuntimeFixture.Blank();
        var rt = fixture.Runtime;
        rt.SetDom(HtmlParsing.ParseHtml(html));
        rt.SetViewport(1280.0, 800.0);
        rt.RunPageInit();
        return rt.Evaluate(script);
    }

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"expected {expected}\n  actual {actual?.ToJsonString() ?? "null"}");

    [Fact]
    public void HitTestingFollowsChromiumsPaintOrder()
    {
        // A point in the float hits the float, not the paragraph after it; text in a span hits
        // the span and the rest of a line its block; z-index orders the positioned boxes;
        // pointer-events: none passes through to the root; a shadow tree is retargeted to its
        // host for the document and not for its own root.
        AssertJson(
            """
            {
              "one": ["f","s","f","p","z1","z1","z2","pea","html","host","host","html"],
              "all": ["f,p,c,body,html","s,p,c,body,html","f,p,c,body,html","p,c,body,html","z1,html","z1,z2,html","z2,html","pea,html","html","host,html","host,html","html"],
              "shadow": ["inner","host","inner,host,html"]
            }
            """,
            Measure(Page, Script));
    }

    [Fact]
    public void OutsideTheViewportNothingIsHit()
    {
        AssertJson(
            """[null, null, 0]""",
            Measure(
                Page,
                "[document.elementFromPoint(-1, 10), document.elementFromPoint(10, 900), document.elementsFromPoint(1300, 5).length]"));
    }

    [Fact]
    public void ATransformedBoxIsHitThroughItsTransform()
    {
        // Chromium 141: a 100x40 box rotated 90 degrees about its centre covers x 30..70,
        // y 0..100 on screen; (35, 80) is inside it. The box's top margin collapses through
        // the body, so (10, 20) is above every box and hits the root.
        AssertJson(
            """["r", "html"]""",
            Measure(
                """
                <!doctype html><html><body style="margin:0"><div style="height:100px"><div id=r style="width:100px;height:40px;margin-top:30px;transform:rotate(90deg);background:red"></div></div></body></html>
                """,
                "(() => { const l = (e) => e ? (e.id || e.tagName.toLowerCase()) : null; return [l(document.elementFromPoint(35, 80)), l(document.elementFromPoint(10, 20))]; })()"));
    }

    [Fact]
    public void AScrolledOverflowClipHidesWhatItScrollsAway()
    {
        // Chromium 141: after scrolling 50px the second child is under (10, 45), the first
        // child's last 10px under (10, 35), and the part of it scrolled above the box is
        // clipped, so (10, 25) hits the block before the scroller.
        AssertJson(
            """["b", "a", "sp", "body"]""",
            Measure(
                """
                <!doctype html><html><body style="margin:0"><div id=sp style="height:30px"></div><div id=s style="overflow:hidden;height:60px;width:100px"><div id=a style="height:60px">a</div><div id=b style="height:60px">b</div></div></body></html>
                """,
                "(() => { const l = (e) => e ? (e.id || e.tagName.toLowerCase()) : null; document.getElementById('s').scrollTop = 50; return [l(document.elementFromPoint(10, 45)), l(document.elementFromPoint(10, 35)), l(document.elementFromPoint(10, 25)), l(document.elementFromPoint(150, 45))]; })()"));
    }
}
