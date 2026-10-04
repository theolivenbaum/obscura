using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Render;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Layout-dependent reads reuse the retained layout until something that can change it has
/// changed, and a read after a write always sees the write. The expected geometry is
/// Chromium 141's for the same page, viewport and sequence of writes.
/// </summary>
public sealed class LayoutReadCacheTests
{
    private const string Square =
        "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    private const string Wide =
        "data:image/gif;base64,R0lGODlhAgABAIAAAAAAAP///yH5BAEAAAAALAAAAAACAAEAAAICBAoAOw==";

    private static readonly string Page = $$"""
        <!doctype html><html><head><style>
        body{margin:0}
        .row{display:flex;gap:4px;padding:2px;border:1px solid #000}
        .row div{height:10px;flex:1}
        .row div.wide{flex:none;width:300px}
        #pic{display:block;width:40%}
        #pre{height:20px}
        #pre img{width:10px}
        </style></head><body>
        <div id="pre"><img id="sq" src="{{Square}}"><img id="wd" src="{{Wide}}"></div>
        <div id="list">
        <div class="row" id="r0"><div id="a0"></div><div id="b0"></div></div>
        <div class="row" id="r1"><div id="a1"></div><div id="b1"></div></div>
        </div>
        <img id="pic" alt="">
        </body></html>
        """;

    private const string Snap = """
        const $ = (id) => document.getElementById(id);
        const snap = (label) => [label, $('a0').offsetWidth, $('b0').offsetWidth,
          $('r0').getBoundingClientRect().x, $('r1').offsetTop, $('r1').getBoundingClientRect().y,
          $('pic').offsetHeight, $('b1').clientWidth];
        """;

    private static RuntimeFixture Load()
    {
        var fixture = RuntimeFixture.Blank();
        fixture.Runtime.SetDom(HtmlParsing.ParseHtml(Page));
        fixture.Runtime.SetViewport(1280, 800);
        fixture.Runtime.RunPageInit();
        return fixture;
    }

    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), actual),
            $"expected {expected}\n  actual {actual?.ToJsonString() ?? "null"}");

    [Fact]
    public void ReadsInterleavedWithWritesSeeEachWrite()
    {
        using var fixture = Load();
        var result = fixture.Runtime.Evaluate($$"""
            (() => {
              {{Snap}}
              const out = [snap('initial'), snap('repeat')];
              $('a0').classList.add('wide'); out.push(snap('class'));
              $('r0').style.marginLeft = '25px'; out.push(snap('style'));
              const row = document.createElement('div'); row.className = 'row'; row.id = 'rn';
              row.appendChild(document.createElement('div'));
              $('list').insertBefore(row, $('r1')); out.push(snap('insert'));
              row.remove(); out.push(snap('remove'));
              $('pic').src = $('sq').src; out.push(snap('img-square'));
              $('pic').src = $('wd').src; out.push(snap('img-wide'));
              $('pic').removeAttribute('src'); out.push(snap('img-none'));
              $('a0').classList.remove('wide'); $('r0').style.marginLeft = ''; out.push(snap('reverted'));
              return out;
            })()
            """);

        // Chromium 141, 1280x800.
        AssertJson(
            """
            [["initial",635,635,0,36,36,0,635],["repeat",635,635,0,36,36,0,635],
             ["class",300,970,0,36,36,0,635],["style",300,945,25,36,36,0,635],
             ["insert",300,945,25,52,52,0,635],["remove",300,945,25,36,36,0,635],
             ["img-square",300,945,25,36,36,512,635],["img-wide",300,945,25,36,36,256,635],
             ["img-none",300,945,25,36,36,0,635],["reverted",635,635,0,36,36,0,635]]
            """,
            result);
    }

    [Fact]
    public void RepeatedReadsWithoutWritesReuseOneLayout()
    {
        using var fixture = Load();
        var rt = fixture.Runtime;
        // The first task after page init moves the animation sample onto the document timeline;
        // settle that before taking the layout the reads below must keep.
        rt.Evaluate("document.getElementById('a0').offsetWidth");
        rt.Evaluate("document.getElementById('a0').offsetWidth");
        var prepared = rt.State.PreparedRender;
        Assert.NotNull(prepared);

        rt.Evaluate($$"""
            (() => {
              {{Snap}}
              let s = 0;
              for (let i = 0; i < 200; i++) s += snap('x')[1] + getComputedStyle($('b0')).width.length;
              // A write that changes nothing (the same class, the same inline value) is not damage.
              $('r0').className = $('r0').className;
              $('r0').setAttribute('id', 'r0');
              return s + $('r0').getBoundingClientRect().width;
            })()
            """);
        Assert.Same(prepared, rt.State.PreparedRender);

        // A write that can change the layout is seen by the next read, from a new layout.
        Assert.Equal(300, rt.Evaluate(
            "document.getElementById('a0').classList.add('wide'), document.getElementById('a0').offsetWidth")!
            .GetValue<double>());
        Assert.NotSame(prepared, rt.State.PreparedRender);
    }

    [Fact]
    public void AnImageSourceSwapKeepsTheRetainedStylesAndDropsTheOldIntrinsicSize()
    {
        using var fixture = Load();
        var rt = fixture.Runtime;
        Assert.Equal(512, rt.Evaluate(
            "document.getElementById('pic').src = document.getElementById('sq').src, document.getElementById('pic').offsetHeight")!
            .GetValue<double>());
        Assert.NotNull(rt.State.PreparedRender);

        // The swap is queued as retained style damage on the <img> instead of discarding the
        // whole style graph, as `src` used to (crates/obscura-render classifies it Full).
        rt.Evaluate("document.getElementById('pic').removeAttribute('src')");
        Assert.NotNull(rt.State.PreparedRender);
        Assert.Contains(
            rt.State.PendingStyleMutations,
            mutation => mutation is RetainedStyleMutation.Attribute { Mutation.Name: "src" });

        // The retained <img> style carried the square's natural size; without a fresh style
        // it would keep reporting 512 for an image that has no source at all.
        Assert.Equal(0, rt.Evaluate("document.getElementById('pic').offsetHeight")!.GetValue<double>());
        Assert.Equal(256, rt.Evaluate(
            "document.getElementById('pic').src = document.getElementById('wd').src, document.getElementById('pic').offsetHeight")!
            .GetValue<double>());
    }

    [Fact]
    public void AnOffsetReadInALaterTaskKeepsTheLayoutWhenOnlyPaintIsAnimated()
    {
        var dom = HtmlParsing.ParseHtml("""
            <style>
                @keyframes fade { from { opacity:0 } to { opacity:1 } }
                body { margin:0 }
                #box { width:40px;height:20px;animation:fade 1000ms linear infinite }
            </style><div id="box"></div>
            """);
        var box = dom.GetElementById("box")!.Value;
        var state = new PocketCalculatorState { Dom = dom, AnimationSample = AnimationSample.Document(0.0f) };
        var nid = box.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("\"width\":40", RenderOps.OpLayoutOffset(state, nid));
        var prepared = state.PreparedRender;
        Assert.NotNull(prepared);

        // A new task samples the running opacity animation at a later time. offset* read no
        // paint state, so that sample must not cost them a layout.
        state.SetAnimationTimelineElapsed(TimeSpan.FromMilliseconds(250));
        RenderOps.OpBeginRenderTask(state);
        Assert.Contains("\"width\":40", RenderOps.OpLayoutOffset(state, nid));
        Assert.Same(prepared, state.PreparedRender);
    }

    [Fact]
    public void AnInterruptedLayoutLeavesNoLayoutBehindAndTheNextReadRebuildsIt()
    {
        var dom = HtmlParsing.ParseHtml(
            "<style>body{margin:0}.wide{width:300px}</style><div id=a style='width:100px;height:10px'></div>");
        var a = dom.GetElementById("a")!.Value;
        var state = new PocketCalculatorState { Dom = dom };
        var nid = a.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("\"width\":100", RenderOps.OpLayoutOffset(state, nid));
        Assert.NotNull(state.PreparedRender);

        DomOps.OpDom(state, "set_attribute", nid, "class\0wide");
        DomOps.OpDom(state, "set_attribute", nid, "style\0height:10px");
        Assert.NotEmpty(state.PendingStyleMutations);

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            using var scope = WorkCancellation.Enter(cancelled.Token);
            Assert.ThrowsAny<OperationCanceledException>(() => RenderOps.OpLayoutOffset(state, nid));
        }

        // Nothing half-built is left looking valid, and the queued damage is not lost: the
        // next read lays the document out again from scratch.
        Assert.True(
            state.PreparedRender is null || state.PendingStyleMutations.Count != 0,
            "an interrupted pass left a layout that does not reflect the queued writes");
        Assert.Contains("\"width\":300", RenderOps.OpLayoutOffset(state, nid));
        Assert.NotNull(state.PreparedRender);
    }
}
