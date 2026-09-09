using Obscura.Dom;
using Obscura.Js.Runtime;
using Xunit;

namespace Obscura.Js.Tests;

/// <summary>
/// The 2D context's drawing surface, measured through <c>getImageData</c>.
/// </summary>
/// <remarks>
/// <para>
/// The rasterizer lives in <c>bootstrap.js</c>, which both engines share
/// verbatim, so these are not port-only assertions: the same numbers hold for
/// the Rust reference and were checked against it. They are here because this
/// is where the shim is exercised in a test host.
/// </para>
/// <para>
/// Until this was implemented the context rasterized exactly one thing, a solid
/// <c>fillRect</c>. <c>stroke()</c>, <c>fill()</c> of a line path, any gradient
/// fill and every transform method were no-ops, so a page that drew a chart got
/// a blank canvas - which is what happens on most real dashboards, since a chart
/// is strokes and gradient fills over a transformed context. Each fact below
/// pins one of the operations that did nothing.
/// </para>
/// <para>
/// Counts are pixel counts on a small canvas, chosen because they are stable
/// across antialiasing: a filled 20x10 rect is exactly 200 whatever the edge
/// shading does, and a path fill is given a tolerance because its diagonal edge
/// is antialiased.
/// </para>
/// </remarks>
public sealed class Canvas2dConformanceTests
{
    private static ObscuraJsRuntime NewRuntime()
    {
        var rt = new ObscuraJsRuntime();
        rt.SetDom(HtmlParsing.ParseHtml("<html><body><p>canvas</p></body></html>"));
        rt.SetUrl("http://example.test/canvas");
        rt.SetViewport(64.0, 40.0);
        // Without page init the document surface the shim installs is not wired
        // up and every evaluation comes back null.
        rt.RunPageInit();
        return rt;
    }

    /// <summary>Non-transparent pixels after running <paramref name="draw"/> on a 40x20 canvas.</summary>
    private static int Painted(ObscuraJsRuntime rt, string draw)
    {
        var value = rt.Evaluate($$"""
            (() => {
              const c = document.createElement('canvas');
              c.width = 40; c.height = 20;
              const x = c.getContext('2d');
              {{draw}}
              const d = x.getImageData(0, 0, c.width, c.height).data;
              let n = 0;
              for (let i = 3; i < d.length; i += 4) if (d[i] > 8) n++;
              return n;
            })()
            """);
        return (int)(value?.GetValue<double>() ?? -1);
    }

    [Fact]
    public void ASolidFillRectStillCoversExactlyItsArea()
    {
        using var rt = NewRuntime();
        Assert.Equal(200, Painted(rt, "x.fillStyle='#f00'; x.fillRect(0,0,20,10);"));
    }

    [Fact]
    public void StrokingAPathPaints()
    {
        using var rt = NewRuntime();
        // A 4px line across 40px: 160 pixels, butt caps, no antialiased ends.
        Assert.Equal(160, Painted(rt,
            "x.strokeStyle='#0f0'; x.lineWidth=4; x.beginPath(); x.moveTo(0,10); x.lineTo(40,10); x.stroke();"));
    }

    [Fact]
    public void FillingAClosedLinePathPaints()
    {
        using var rt = NewRuntime();
        // Half of 40x20 plus the antialiased diagonal.
        var painted = Painted(rt,
            "x.fillStyle='#f0f'; x.beginPath(); x.moveTo(0,0); x.lineTo(40,0); x.lineTo(40,20); x.closePath(); x.fill();");
        Assert.InRange(painted, 390, 450);
    }

    [Fact]
    public void AGradientFillStylePaintsTheWholeRect()
    {
        using var rt = NewRuntime();
        Assert.Equal(800, Painted(rt, """
            const g = x.createLinearGradient(0,0,40,0);
            g.addColorStop(0,'#00f'); g.addColorStop(1,'#0ff');
            x.fillStyle = g; x.fillRect(0,0,40,20);
            """));
    }

    [Fact]
    public void TheTransformAppliesToSubsequentDrawing()
    {
        using var rt = NewRuntime();
        // setTransform(2,...) doubles both axes, so 10x5 covers 20x10 = 200.
        Assert.Equal(200, Painted(rt,
            "x.setTransform(2,0,0,2,0,0); x.fillStyle='#ff0'; x.fillRect(0,0,10,5);"));
        Assert.Equal(100, Painted(rt,
            "x.translate(10,10); x.fillStyle='#ff0'; x.fillRect(0,0,10,10);"));
    }

    [Fact]
    public void SaveAndRestoreCarryTheTransformAndTheClip()
    {
        using var rt = NewRuntime();
        Assert.Equal(100, Painted(rt,
            "x.save(); x.translate(20,20); x.restore(); x.fillStyle='#f00'; x.fillRect(0,0,10,10);"));
        // Restoring drops the clip, so the fill covers the whole canvas again.
        Assert.Equal(800, Painted(rt, """
            x.save(); x.beginPath(); x.rect(2,2,4,4); x.clip(); x.restore();
            x.fillStyle='#f0f'; x.fillRect(0,0,40,20);
            """));
    }

    [Fact]
    public void ClipConfinesLaterDrawing()
    {
        using var rt = NewRuntime();
        Assert.Equal(100, Painted(rt, """
            x.beginPath(); x.rect(5,5,10,10); x.clip();
            x.fillStyle='#f0f'; x.fillRect(0,0,40,20);
            """));
    }

    [Fact]
    public void LineDashLeavesGaps()
    {
        using var rt = NewRuntime();
        var solid = Painted(rt,
            "x.strokeStyle='#fff'; x.lineWidth=4; x.beginPath(); x.moveTo(0,10); x.lineTo(40,10); x.stroke();");
        var dashed = Painted(rt, """
            x.strokeStyle='#fff'; x.lineWidth=4; x.setLineDash([5,5]);
            x.beginPath(); x.moveTo(0,10); x.lineTo(40,10); x.stroke();
            """);
        Assert.Equal(160, solid);
        Assert.InRange(dashed, 60, 100); // half the run, give or take a dash boundary
    }

    /// <summary>
    /// The backing store is straight alpha, because the render layer documents
    /// it as straight alpha and premultiplies once itself. Compositing
    /// premultiplied here darkened every translucent pixel twice.
    /// </summary>
    [Fact]
    public void TranslucentFillsKeepTheirColourAtReducedAlpha()
    {
        using var rt = NewRuntime();
        var rgba = rt.Evaluate("""
            (() => {
              const c = document.createElement('canvas');
              c.width = 4; c.height = 4;
              const x = c.getContext('2d');
              x.fillStyle = 'rgba(255, 0, 0, 0.5)';
              x.fillRect(0, 0, 4, 4);
              const d = x.getImageData(0, 0, 4, 4).data;
              return [d[0], d[1], d[2], d[3]];
            })()
            """);
        // A browser reports (255,0,0,128) here, not (128,0,0,128).
        Assert.Equal("[255,0,0,128]", rgba?.ToJsonString());
    }

    [Fact]
    public void ColourSyntaxesThePreviousParserDroppedNowResolve()
    {
        using var rt = NewRuntime();
        var colours = rt.Evaluate("""
            (() => {
              const c = document.createElement('canvas');
              c.width = 1; c.height = 1;
              const x = c.getContext('2d');
              const read = css => {
                x.clearRect(0, 0, 1, 1);
                x.fillStyle = css;
                x.fillRect(0, 0, 1, 1);
                const d = x.getImageData(0, 0, 1, 1).data;
                return [d[0], d[1], d[2], d[3]];
              };
              return {
                named: read('rebeccapurple'),
                hsl: read('hsl(120, 100%, 50%)'),
                shortHexAlpha: read('#0f08'),
                spaceRgb: read('rgb(10 20 30)'),
              };
            })()
            """);
        Assert.Equal(
            """{"named":[102,51,153,255],"hsl":[0,255,0,255],"shortHexAlpha":[0,255,0,136],"spaceRgb":[10,20,30,255]}""",
            colours?.ToJsonString());
    }

    [Fact]
    public void IsPointInPathAndInStrokeAnswerFromTheRealGeometry()
    {
        using var rt = NewRuntime();
        var hits = rt.Evaluate("""
            (() => {
              const c = document.createElement('canvas');
              c.width = 60; c.height = 40;
              const x = c.getContext('2d');
              x.beginPath(); x.rect(10, 10, 20, 20);
              const inPath = [x.isPointInPath(15, 15), x.isPointInPath(5, 5)];
              x.beginPath(); x.moveTo(0, 20); x.lineTo(60, 20); x.lineWidth = 8;
              return [...inPath, x.isPointInStroke(30, 21), x.isPointInStroke(30, 2)];
            })()
            """);
        Assert.Equal("[true,false,true,false]", hits?.ToJsonString());
    }
}
