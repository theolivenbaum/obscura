using System.Reflection;
using Obscura.Dom;
using Obscura.Render.Css;
using Xunit;
using NodeId = Obscura.Dom.NodeId;

namespace Obscura.Render.Tests;

/// <summary>
/// The incremental-invalidation gate: what a retained restyle is allowed to keep, and - far
/// more important - what it is not.
/// </summary>
/// <remarks>
/// Every fact here compares the incremental result against a full, from-scratch prepare of the
/// same mutated tree, which is the only geometry that is definitionally right. Whether the gate
/// engaged is read off <see cref="PreparedRender.Layout"/> by reference: keeping the layout
/// means literally handing back the same <see cref="DomLayout"/>, so reference identity is an
/// exact observation of it rather than a proxy.
/// </remarks>
public class RetainedLayoutReuseTests
{
    private static readonly (float Width, float Height) Viewport = (800f, 600f);

    private sealed record Restyled(
        PreparedRender Before,
        PreparedRender After,
        PreparedRender Full,
        DomTree Tree)
    {
        internal bool KeptLayout => ReferenceEquals(Before.Layout, After.Layout);

        internal Rect RectOf(string id) =>
            After.Layout.Rects[Tree.GetElementById(id) ?? throw new InvalidOperationException(id)];

        internal Rect FullRectOf(string id) =>
            Full.Layout.Rects[Tree.GetElementById(id) ?? throw new InvalidOperationException(id)];
    }

    /// <summary>
    /// Lay out <paramref name="html"/>, set one attribute, then restyle incrementally and
    /// - from a second, independent tree - fully.
    /// </summary>
    private static Restyled Restyle(string html, string id, string attribute, string value)
    {
        DomTree tree = HtmlParsing.ParseHtml(html);
        RenderResourceCache resources = new();
        StylesheetCache cache = new();
        PreparedRender before =
            RenderPaint.PrepareDomWithDynamicFontsAndStylesheetCache(
                tree, Viewport, null, resources, [], cache)
            ?? throw new InvalidOperationException("initial prepare failed");

        NodeId target = tree.GetElementById(id) ?? throw new InvalidOperationException(id);
        string? old = tree.GetNode(target)?.GetAttribute(attribute);
        tree.GetNode(target)!.SetAttribute(attribute, value);

        PreparedRender after = RenderPaint.PrepareDomWithRetainedAttributeStyles(
            tree,
            Viewport,
            null,
            resources,
            [],
            cache,
            before,
            [new AttributeStyleMutation(target, attribute, old, value)])
            ?? throw new InvalidOperationException("retained prepare failed");

        DomTree reference = HtmlParsing.ParseHtml(html);
        NodeId referenceTarget =
            reference.GetElementById(id) ?? throw new InvalidOperationException(id);
        reference.GetNode(referenceTarget)!.SetAttribute(attribute, value);
        PreparedRender full =
            RenderPaint.PrepareDom(reference, Viewport, null, new RenderResourceCache())
            ?? throw new InvalidOperationException("reference prepare failed");

        return new Restyled(before, after, full, tree);
    }

    private static void AssertGeometryMatchesAFullLayout(Restyled restyled, params string[] ids)
    {
        foreach (string id in ids)
        {
            Assert.Equal(restyled.FullRectOf(id), restyled.RectOf(id));
        }
    }

    private const string BOXES = """
        <html><head><style>
          #outer { display: inline-block; border: 1px solid black; }
          #child { width: 120px; height: 40px; }
          #sibling { width: 30px; height: 10px; }
        </style></head><body>
          <div id="outer"><div id="child">child</div><div id="sibling"></div></div>
        </body></html>
        """;

    [Fact]
    public void APaintOnlyWriteKeepsTheRetainedGeometry()
    {
        Restyled restyled = Restyle(BOXES, "child", "style", "opacity: 0.25");

        Assert.True(restyled.KeptLayout, "an opacity write must not run layout again");
        AssertGeometryMatchesAFullLayout(restyled, "outer", "child", "sibling");
        Assert.Equal(
            0.25f,
            restyled.After.Layout.Styles[restyled.Tree.GetElementById("child")!.Value].Opacity);
    }

    [Fact]
    public void AClassThatMatchesNoRuleKeepsTheRetainedGeometry()
    {
        Restyled restyled = Restyle(BOXES, "child", "class", "nothing-selects-this");

        Assert.True(restyled.KeptLayout, "a class no rule mentions must not run layout again");
        AssertGeometryMatchesAFullLayout(restyled, "outer", "child", "sibling");
    }

    [Fact]
    public void ASizeAffectingWriteRunsLayoutAgainAndReportsTheNewGeometry()
    {
        Restyled restyled = Restyle(BOXES, "child", "style", "width: 300px; height: 90px");

        Assert.False(restyled.KeptLayout, "a width write must run layout again");
        AssertGeometryMatchesAFullLayout(restyled, "outer", "child", "sibling");
        Assert.Equal(300f, restyled.RectOf("child").Width);
        Assert.Equal(90f, restyled.RectOf("child").Height);
    }

    /// <summary>
    /// The case the gate is most dangerous for: the changed box is a child, and what moves is
    /// its shrink-to-fit ancestor.
    /// </summary>
    [Fact]
    public void AChildWidthChangeUpdatesTheAncestorItSizes()
    {
        Restyled restyled = Restyle(BOXES, "child", "style", "width: 260px");
        NodeId outer = restyled.Tree.GetElementById("outer")!.Value;

        Assert.False(restyled.KeptLayout);
        AssertGeometryMatchesAFullLayout(restyled, "outer", "child");
        Assert.Equal(restyled.FullRectOf("outer").Width, restyled.RectOf("outer").Width);
        Assert.NotEqual(restyled.Before.Layout.Rects[outer].Width, restyled.RectOf("outer").Width);
    }

    [Fact]
    public void AnInsetWriteOnAPositionedBoxRunsLayoutAgain()
    {
        const string html = """
            <html><head><style>
              #holder { position: relative; width: 400px; height: 300px; }
              #item { position: absolute; left: 10px; top: 20px; width: 50px; height: 50px; }
            </style></head><body><div id="holder"><div id="item"></div></div></body></html>
            """;

        Restyled restyled = Restyle(html, "item", "style", "left: 130px; top: 70px");

        Assert.False(restyled.KeptLayout, "an inset write must run layout again");
        AssertGeometryMatchesAFullLayout(restyled, "holder", "item");
        Assert.Equal(
            restyled.Before.Layout.Rects[restyled.Tree.GetElementById("item")!.Value].X + 120f,
            restyled.RectOf("item").X);
        Assert.Equal(
            restyled.Before.Layout.Rects[restyled.Tree.GetElementById("item")!.Value].Y + 50f,
            restyled.RectOf("item").Y);
    }

    /// <summary>
    /// A class that does select a sizing rule is the same shape of write as one that selects
    /// nothing, and must not be absorbed with it.
    /// </summary>
    [Fact]
    public void AClassThatSelectsASizingRuleRunsLayoutAgain()
    {
        const string html = """
            <html><head><style>
              .box { width: 120px; height: 40px; }
              .wide { width: 340px; height: 40px; }
            </style></head><body>
              <div id="outer"><div id="child" class="box">child</div></div>
            </body></html>
            """;

        Restyled restyled = Restyle(html, "child", "class", "wide");

        Assert.False(restyled.KeptLayout);
        AssertGeometryMatchesAFullLayout(restyled, "child");
        Assert.Equal(340f, restyled.RectOf("child").Width);
    }

    /// <summary>
    /// The counterpart, and the reason the gate compares computed styles rather than trusting
    /// the planner's dirty set: the class does select a rule, the cascade does recompute the
    /// element, and a more specific rule means nothing about it actually changes.
    /// </summary>
    [Fact]
    public void AClassOutrankedByAMoreSpecificRuleKeepsTheRetainedGeometry()
    {
        const string html = """
            <html><head><style>
              #child { width: 120px; height: 40px; }
              .wide { width: 340px; }
            </style></head><body>
              <div id="outer"><div id="child" class="">child</div></div>
            </body></html>
            """;

        Restyled restyled = Restyle(html, "child", "class", "wide");

        Assert.True(restyled.KeptLayout);
        AssertGeometryMatchesAFullLayout(restyled, "outer", "child");
        Assert.Equal(120f, restyled.RectOf("child").Width);
    }

    /// <summary>
    /// A paint-only write on a node whose descendants inherit from it still has to leave every
    /// descendant's geometry alone, and still has to be visible to paint.
    /// </summary>
    [Fact]
    public void AnInheritedPaintOnlyWriteKeepsGeometryAndReachesDescendants()
    {
        const string html = """
            <html><body><div id="outer" style="color: rgb(1, 2, 3)">
              <span id="inner">text</span>
            </div></body></html>
            """;

        Restyled restyled = Restyle(html, "outer", "style", "color: rgb(9, 8, 7)");

        Assert.True(restyled.KeptLayout, "a colour write must not run layout again");
        AssertGeometryMatchesAFullLayout(restyled, "outer", "inner");
        NodeId inner = restyled.Tree.GetElementById("inner")!.Value;
        NodeId referenceInner =
            restyled.Full.Layout.Rects.Keys.First(node => node == inner);
        Assert.Equal(
            restyled.Full.Layout.Styles[referenceInner].Color,
            restyled.After.Layout.Styles[inner].Color);
    }

    /// <summary>
    /// A font-size write inherits like a colour write and is not paint-only, so the gate must
    /// tell the two apart rather than treating "inherited" as a class of its own.
    /// </summary>
    [Fact]
    public void AnInheritedFontSizeWriteRunsLayoutAgain()
    {
        const string html = """
            <html><body><div id="outer" style="font-size: 12px; display: inline-block">
              <span id="inner">text</span>
            </div></body></html>
            """;

        Restyled restyled = Restyle(html, "outer", "style", "font-size: 40px; display: inline-block");

        Assert.False(restyled.KeptLayout);
        AssertGeometryMatchesAFullLayout(restyled, "outer", "inner");
    }

    /// <summary>
    /// The paint-only list is matched against <see cref="LayoutStyle"/> by field name, and a
    /// name that matches nothing would silently do nothing. Every other member - including one
    /// added later - is layout-affecting by omission, which is the safe direction.
    /// </summary>
    [Fact]
    public void EveryPaintOnlyMemberNamesARealLayoutStyleField()
    {
        FieldInfo[] declared = typeof(LayoutStyle).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        HashSet<string> names = [.. declared.Select(static field => field.Name)];
        FieldInfo listed = typeof(RetainedLayoutReuse).GetField(
            "PaintOnlyMembers",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PaintOnlyMembers is gone");

        foreach (string name in (string[])listed.GetValue(null)!)
        {
            Assert.Contains(name, names);
        }
    }
}
