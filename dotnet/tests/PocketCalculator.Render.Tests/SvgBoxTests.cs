using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// <c>getBoundingClientRect()</c> on the descendants of an inline <c>&lt;svg&gt;</c>: the
/// object bounding box of a shape, a container's union, and the viewport a nested
/// <c>&lt;svg&gt;</c> establishes.
/// </summary>
/// <remarks>
/// Every expectation here was read off Chromium 141 on the same markup, not off the Rust
/// engine: <c>crates/obscura-render</c> hands an inline SVG to resvg as one opaque raster and
/// has no per-element SVG geometry to port. See "Known deviations" in todo.md.
/// </remarks>
public class SvgBoxTests
{
    /// <summary>
    /// The reduction of <c>#/view/Charts</c>: a <c>viewBox</c>-scaled chart svg holding the
    /// shapes, text and groups that route collapses, next to a plainly sized one.
    /// </summary>
    private const string ProbeHtml = """
        <!doctype html><html><head><style>
        body{margin:0;font:13px "Segoe UI",SegoeUI,"Helvetica Neue",Helvetica,Arial,sans-serif}
        .w{width:600px;height:200px}
        </style></head><body>
        <div style="position:absolute;left:40px;top:30px">
        <svg id="a" class="w" viewBox="0 0 600 200" preserveAspectRatio="none">
          <rect id="a1" x="8" y="6" width="10" height="10" rx="2" fill="#268"></rect>
          <text id="a2" x="24" y="16" font-size="11">Revenue</text>
          <text id="a3" x="22" y="249" text-anchor="end" font-size="10">0</text>
          <text id="a4" x="111.583" y="60" text-anchor="middle" font-size="10">Jan</text>
          <line id="a5" x1="28" y1="150" x2="560" y2="150" stroke="#888" stroke-width="3"></line>
          <circle id="a6" cx="28" cy="100" r="3" fill="#268"><title>t</title></circle>
          <ellipse id="a7" cx="100" cy="100" rx="20" ry="8"></ellipse>
          <polyline id="a8" points="10,10 40,80 90,20" fill="none" stroke="#000"></polyline>
          <polygon id="a9" points="200,10 240,80 290,20"></polygon>
          <path id="a10" d="M 28 181.2 C 100 20 200 180 300 60"></path>
          <defs><clipPath id="cp"><rect id="a11" x="0" y="0" width="50" height="50"/></clipPath></defs>
          <g id="a12" clip-path="url(#cp)">
            <rect id="a13" x="300" y="20" width="40" height="30"></rect>
            <circle id="a14" cx="500" cy="120" r="10"></circle>
          </g>
          <g id="a15"></g>
          <g id="a16" transform="translate(100,20) scale(2)">
            <rect id="a17" x="10" y="5" width="20" height="10"></rect>
          </g>
          <g id="a18" style="display:none"><rect id="a19" x="0" y="0" width="5" height="5"/></g>
          <svg id="a20" x="400" y="100" width="100" height="60" viewBox="0 0 10 6">
            <rect id="a21" x="0" y="0" width="10" height="6"/>
          </svg>
        </svg>
        </div>
        <div style="position:absolute;left:40px;top:280px">
        <svg id="b" width="300" height="120">
          <rect id="b1" x="5" y="5" width="30" height="20"></rect>
          <text id="b2" x="50" y="40" font-size="16">Hello</text>
          <line id="b3" x1="0" y1="60" x2="300" y2="60" stroke="#000"></line>
        </svg>
        </div>
        </body></html>
        """;

    private static (PreparedRender Prepared, DomTree Tree) Prepare(string html)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        RenderResourceCache resources = new();
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1600f, 900f), null, resources)
            ?? throw new InvalidOperationException("layout did not prepare");

        return (prepared, tree);
    }

    private static Rect Box((PreparedRender Prepared, DomTree Tree) page, string id)
    {
        NodeId node = page.Tree.GetElementById(id)
            ?? throw new InvalidOperationException($"no element #{id}");

        return page.Prepared.DocumentRect(node) ?? new Rect(0f, 0f, 0f, 0f);
    }

    private static void AssertBox(
        (PreparedRender Prepared, DomTree Tree) page,
        string                                  id,
        float                                   x,
        float                                   y,
        float                                   width,
        float                                   height)
    {
        Rect box = Box(page, id);
        Assert.True(
            MathF.Abs(box.X - x) <= 0.5f
            && MathF.Abs(box.Y - y) <= 0.5f
            && MathF.Abs(box.Width - width) <= 0.5f
            && MathF.Abs(box.Height - height) <= 0.5f,
            $"#{id}: expected [{x}, {y}, {width}, {height}], got [{box.X}, {box.Y}, {box.Width}, {box.Height}]");
    }

    [Fact]
    public void ShapesInAViewBoxScaledSvgReportTheirObjectBoundingBox()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        AssertBox(prepared, "a", 40f, 30f, 600f, 200f);
        AssertBox(prepared, "a1", 48f, 36f, 10f, 10f);
        AssertBox(prepared, "a6", 65f, 127f, 6f, 6f);
        AssertBox(prepared, "a7", 120f, 122f, 40f, 16f);
        AssertBox(prepared, "a8", 50f, 40f, 80f, 70f);
        AssertBox(prepared, "a9", 240f, 40f, 90f, 70f);
    }

    /// <summary>
    /// The box excludes the stroke, so a horizontal <c>&lt;line&gt;</c> is zero-height however
    /// thick it is drawn. This is what the Charts route's 43 collapsed grid lines are.
    /// </summary>
    [Fact]
    public void AStrokedLineIsZeroHeightAndKeepsItsFullLength()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        AssertBox(prepared, "a5", 68f, 180f, 532f, 0f);
        AssertBox(prepared, "b3", 40f, 340f, 300f, 0f);
    }

    /// <summary>A curve contributes its geometric extrema, not its control points.</summary>
    [Fact]
    public void APathUsesTightCurveBoundsNotControlPoints()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        AssertBox(prepared, "a10", 68f, 90f, 272f, 121.2f);
    }

    [Fact]
    public void TextIsAnchoredAndSizedByTheFontsRoundedAscentAndDescent()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);

        // 11px Liberation Sans is 10 above the baseline and 2 below it.
        AssertBox(prepared, "a2", 64f, 36f, 44.05f, 12f);

        // `text-anchor: end` moves the origin back by the advance; `middle` by half of it.
        Assert.InRange(Box(prepared, "a3").X, 56f, 57f);
        Assert.Equal(270f, Box(prepared, "a3").Y, 1);
        Assert.Equal(11f, Box(prepared, "a3").Height, 1);
        Assert.InRange(Box(prepared, "a4").X, 143f, 144.1f);
        Assert.Equal(81f, Box(prepared, "a4").Y, 1);

        // 16px: 14 above the baseline, 3 below.
        Assert.Equal(306f, Box(prepared, "b2").Y, 1);
        Assert.Equal(17f, Box(prepared, "b2").Height, 1);
    }

    /// <summary>
    /// A container unions its children in its own user space; a <c>clip-path</c> does not shrink
    /// it, and an empty one keeps a zero box at that space's origin.
    /// </summary>
    [Fact]
    public void AGroupUnionsItsChildrenAndIgnoresItsClipPath()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        AssertBox(prepared, "a12", 340f, 50f, 210f, 110f);
        AssertBox(prepared, "a13", 340f, 50f, 40f, 30f);
        AssertBox(prepared, "a14", 530f, 140f, 20f, 20f);
        AssertBox(prepared, "a15", 40f, 30f, 0f, 0f);
    }

    [Fact]
    public void AGroupTransformAppliesToItAndToItsChildren()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        AssertBox(prepared, "a16", 160f, 60f, 40f, 20f);
        AssertBox(prepared, "a17", 160f, 60f, 40f, 20f);
    }

    /// <summary>
    /// A nested <c>&lt;svg&gt;</c> reports its own viewport rectangle and re-bases its children
    /// on it, <c>viewBox</c> scaling included.
    /// </summary>
    [Fact]
    public void ANestedSvgEstablishesItsOwnViewport()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        AssertBox(prepared, "a20", 440f, 130f, 100f, 60f);
        AssertBox(prepared, "a21", 440f, 130f, 100f, 60f);
    }

    /// <summary>
    /// Nothing outside the SVG rendering model gets a box - not a <c>&lt;defs&gt;</c>, not what
    /// is inside a <c>&lt;clipPath&gt;</c>, and nothing under <c>display: none</c>.
    /// </summary>
    [Fact]
    public void NonRenderedElementsHaveNoBox()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        AssertBox(prepared, "a11", 0f, 0f, 0f, 0f);
        AssertBox(prepared, "a18", 0f, 0f, 0f, 0f);
        AssertBox(prepared, "a19", 0f, 0f, 0f, 0f);
    }

    /// <summary>
    /// An SVG shape is not a CSS layout box, so it answers a bounding box while reporting no
    /// client box - the same split Chromium makes.
    /// </summary>
    [Fact]
    public void AnSvgShapeHasNoClientBox()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        NodeId shape = prepared.Tree.GetElementById("a1")!.Value;
        Assert.Equal((0f, 0f), prepared.Prepared.ClientSize(shape));
        Assert.Equal((600f, 200f), prepared.Prepared.ClientSize(prepared.Tree.GetElementById("a")!.Value));
    }

    /// <summary>
    /// An svg with explicit CSS dimensions and no <c>viewBox</c> places its children at the
    /// content-box origin with no scaling.
    /// </summary>
    [Fact]
    public void AnUnscaledSvgPlacesChildrenAtItsContentOrigin()
    {
        (PreparedRender Prepared, DomTree Tree) prepared = Prepare(ProbeHtml);
        AssertBox(prepared, "b", 40f, 280f, 300f, 120f);
        AssertBox(prepared, "b1", 45f, 285f, 30f, 20f);
    }
}
