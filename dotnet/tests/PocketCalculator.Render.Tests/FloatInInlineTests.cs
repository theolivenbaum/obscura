using PocketCalculator.Dom;
using Xunit;
using NodeId = PocketCalculator.Dom.NodeId;

namespace PocketCalculator.Render.Tests;

/// <summary>
/// Floats inside inline boxes, against Chromium 141's <c>getClientRects()</c> for the inline box
/// (1280x720 viewport, 16px/20px Liberation Sans; within 0.05px).
/// </summary>
public sealed class FloatInInlineTests
{
    private const string Page =
        """
        <!doctype html><html><body style="margin:0;font:16px/20px 'Liberation Sans'">
        <div style="width:400px">
        <p id=p1 style="margin:0">Before <em id=e1>emph <span style="float:right;width:50px;height:50px"></span>after</em> tail</p>
        <p id=p2 style="margin:0;clear:both">Before <em id=e2><span style="float:right;width:50px;height:10px"></span>start float</em> tail</p>
        <p id=p4 style="margin:0;clear:both">Before <em id=e4 style="padding:0 5px;border:2px solid">pad <span style="float:left;width:20px;height:10px"></span>ded</em> tail</p>
        <p id=p5 style="margin:0;clear:both">Before <em id=e5>one <span style="float:right;width:20px;height:10px"></span>two <span style="float:right;width:20px;height:10px"></span>three</em> tail</p>
        <p id=p6 style="margin:0;clear:both;width:150px">Before <em id=e6>long words <span id=f6 style="float:right;width:20px;height:10px"></span>wrapping onto lines here</em> tail</p>
        <p id=p7 style="margin:0;clear:both">Before <em id=e7 style="background:yellow">emph <span style="float:right;width:50px;height:10px"></span>after</em> tail</p>
        </div></body></html>
        """;

    private static List<Rect> Rects(string id)
    {
        DomTree tree = HtmlParsing.ParseHtml(Page);
        RenderResourceCache resources = RenderResourceCache.WithLoader(_ => null);
        PreparedRender prepared = RenderPaint.PrepareDom(tree, (1280f, 720f), null, resources)!;
        ResolvedScrollState scroll = prepared.ResolveScrollState(tree, (0f, 0f), new Dictionary<NodeId, (float, float)>());
        return prepared.ViewportClientRectsWithScroll(tree.GetElementById(id)!.Value, scroll)!;
    }

    private static void AssertRects(string id, params float[][] expected)
    {
        List<Rect> actual = Rects(id);
        string got = string.Join(" ", actual.Select(r => $"[{r.X},{r.Y},{r.Width},{r.Height}]"));
        Assert.True(expected.Length == actual.Count, $"{id}: {got}");
        for (int i = 0; i < expected.Length; i++)
        {
            float[] e = expected[i];
            Rect r = actual[i];
            Assert.True(
                Math.Abs(r.X - e[0]) <= 0.05f && Math.Abs(r.Y - e[1]) <= 0.05f
                && Math.Abs(r.Width - e[2]) <= 0.05f && Math.Abs(r.Height - e[3]) <= 0.05f,
                $"{id}[{i}]: {got}");
        }
    }

    [Fact]
    public void AFloatInsideACulledInlineBoxSplitsItsFragment() =>
        AssertRects("e1", [51.59f, 1f, 44.47f, 17f], [96.06f, 1f, 32.02f, 17f]);

    [Fact]
    public void EachFloatInsideSplitsAgain() =>
        AssertRects("e5", [51.59f, 91f, 31.14f, 17f], [82.73f, 91f, 29.34f, 17f], [112.08f, 91f, 36.47f, 17f]);

    [Fact]
    public void AFloatAtTheStartDoesNotSplit() =>
        AssertRects("e2", [51.59f, 51f, 65.81f, 17f]);

    [Fact]
    public void ADecoratedInlineBoxKeepsOneFragment()
    {
        // A background, or a border and padding, gives the box a fragment of its own. The
        // float inside the bordered box is anchored to its line: the left float moves the
        // line's content right by its width, where the box was laid out as a flex row.
        AssertRects("e7", [51.59f, 171f, 76.48f, 17f]);
        AssertRects("e4", [71.59f, 69f, 71.84f, 21f]);
    }

    [Fact]
    public void AFloatAfterTheSpaceALineWrapsAtStaysOnThatLine() =>
        // "Before long words " is 128.97 without its trailing space and 133.42 with it: the
        // 20px float fits beside the first line of 150 (Chromium 141), so the second line has
        // the whole width for "wrapping onto".
        AssertRects("e6", [51.59f, 111f, 77.38f, 17f], [0f, 131f, 137.88f, 17f], [0f, 151f, 32.03f, 17f]);
}
