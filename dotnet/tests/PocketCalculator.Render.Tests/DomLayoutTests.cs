// xUnit port of `mod tests` in crates/obscura-render/src/dom.rs, in source order.
using System.Globalization;
using PocketCalculator.Dom;
using PocketCalculator.Render;
using PocketCalculator.Render.Css;
using Xunit;
using Display = PocketCalculator.Render.Display;
using NodeId = PocketCalculator.Dom.NodeId;
using Overflow = PocketCalculator.Render.Layout.Overflow;

namespace PocketCalculator.Render.Tests;

public class DomLayoutTests
{
    private static DomTree Parse(string html) => HtmlParsing.ParseHtml(html);

    private static NodeId Id(DomTree tree, string id) =>
        tree.GetElementById(id) ?? throw new InvalidOperationException($"fixture node {id}");

    private static NodeId AttachProgrammaticShadow(DomTree tree, NodeId host, NodeId source)
    {
        NodeId root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        foreach (NodeId child in tree.Children(source))
        {
            tree.AppendChild(root, child);
        }

        tree.Remove(source);
        return root;
    }

    private static NodeId LocalRoot(DomTree tree, string local)
    {
        foreach (NodeId id in tree.Descendants(tree.Document))
        {
            if (tree.GetNode(id)?.AsElement() is { } element
                && string.Equals(element.Name.Local, local, StringComparison.Ordinal))
            {
                return id;
            }
        }

        throw new InvalidOperationException($"no <{local}> element");
    }

    private static RgbaColor Rgb(byte r, byte g, byte b) => new(r, g, b, 0xFF);

    /// <summary>
    /// Compare every computed-style field through a deterministic reflected dump.
    /// <c>LayoutStyle</c> intentionally does not implement structural equality because it is
    /// a large, evolving renderer-internal type; keeping this check centralized means new
    /// fields automatically enter the retained-vs-full oracle. This is the C# analogue of
    /// Rust's <c>format!("{:#?}")</c> comparison.
    /// </summary>
    private static void AssertComputedStylesMatch(
        string caseName,
        DomLayout incremental,
        DomLayout full)
    {
        List<uint> incrementalKeys = [.. incremental.Styles.Keys.Select(n => n.Value).Order()];
        List<uint> fullKeys = [.. full.Styles.Keys.Select(n => n.Value).Order()];
        Assert.True(
            incrementalKeys.SequenceEqual(fullKeys),
            $"{caseName}: computed-style node set");
        foreach ((NodeId node, LayoutStyle incrementalStyle) in incremental.Styles)
        {
            string left = StyleDump.Dump(incrementalStyle);
            string right = StyleDump.Dump(full.Styles[node]);
            Assert.True(
                string.Equals(left, right, StringComparison.Ordinal),
                $"{caseName}: computed style for {node}\nincremental: {left}\nfull:        {right}");
        }
    }

    private static readonly Dictionary<NodeId, ReplacedIntrinsic> NoIntrinsic = [];

    /// <summary>The `Fixture` webfont the Rust tests embed from assets/liberation-serif.ttf.</summary>
    private static WebFont FixtureSerifFont() => new()
    {
        Data = FontAssets.Load("liberation-serif"),
        Family = "Fixture",
        Weight = (400, 400),
        Italic = false,
    };

    /// <summary>Structural map equality, the analogue of Rust's <c>assert_eq!</c> on HashMaps.</summary>
    private static void AssertMapsMatch<TValue>(
        IReadOnlyDictionary<NodeId, TValue> left,
        IReadOnlyDictionary<NodeId, TValue> right,
        string what)
    {
        Assert.True(
            left.Count == right.Count,
            $"{what}: {left.Count} vs {right.Count} entries");
        foreach ((NodeId node, TValue value) in left)
        {
            Assert.True(right.TryGetValue(node, out TValue? other), $"{what}: missing {node}");
            string dumped = StyleDump.Dump(value);
            string otherDumped = StyleDump.Dump(other);
            Assert.True(
                string.Equals(dumped, otherDumped, StringComparison.Ordinal),
                $"{what} for {node}:\n  left:  {dumped}\n  right: {otherDumped}");
        }
    }

    private static void AssertRectsMatch(DomLayout incremental, DomLayout full, string what) =>
        AssertMapsMatch(incremental.Rects, full.Rects, $"{what}: border boxes");

    private enum RetainedDifferentialExpectation
    {
        Incremental,
        ReuseAll,
        ConservativeFallback,
    }

    private sealed record RetainedDifferentialCase(
        string Name,
        string Css,
        string Target,
        string Attribute,
        string NewValue,
        RetainedDifferentialExpectation Expectation);

    private static void AssertRetainedTelemetry(
        string name,
        ContainerLayoutTelemetry telemetry,
        RetainedDifferentialExpectation expectation)
    {
        switch (expectation)
        {
            case RetainedDifferentialExpectation.Incremental:
                Assert.True(telemetry.RetainedFallback == 0, $"{name}: {telemetry}");
                Assert.True(telemetry.RetainedFresh > 0, $"{name}: {telemetry}");
                Assert.True(telemetry.RetainedReused > 0, $"{name}: {telemetry}");
                break;
            case RetainedDifferentialExpectation.ReuseAll:
                Assert.True(telemetry.RetainedFallback == 0, $"{name}: {telemetry}");
                Assert.True(telemetry.RetainedFresh == 0, $"{name}: {telemetry}");
                Assert.True(telemetry.RetainedReused > 0, $"{name}: {telemetry}");
                break;
            default:
                Assert.True(telemetry.RetainedFallback == 1, $"{name}: {telemetry}");
                Assert.True(telemetry.RetainedReused == 0, $"{name}: {telemetry}");
                break;
        }
    }

    private static void AssertRetainedLayoutsMatch(
        string name,
        DomLayout incremental,
        DomLayout full)
    {
        AssertComputedStylesMatch(name, incremental, full);
        AssertMapsMatch(incremental.Rects, full.Rects, $"{name}: border boxes");
        AssertMapsMatch(
            incremental.InlineFragments, full.InlineFragments, $"{name}: inline fragments");
        AssertMapsMatch(incremental.TextRuns, full.TextRuns, $"{name}: text runs");
        AssertMapsMatch(
            incremental.CustomProperties, full.CustomProperties, $"{name}: custom properties");
    }

    private static void RunRetainedDifferentialCase(RetainedDifferentialCase differentialCase)
    {
        string html = $$"""
            <!doctype html><html><head><style>
                html,body{margin:0}
                .clean{width:71px;height:13px}
                {{differentialCase.Css}}
            </style></head><body>
                <section id="scope" class="scope theme-off" data-mode="off">
                  <div id="subject" class="subject state-off" data-state="off">
                    <span id="child" class="child"><i id="grand" class="grand leaf"></i></span>
                  </div>
                  <div id="adjacent" class="panel"><span id="adjacent-child" class="leaf"></span></div>
                  <div id="following" class="panel"><span id="following-child" class="leaf"></span></div>
                </section>
                <input id="control" type="text" size="20" value="old">
                <img id="image" alt="old">
                <aside id="clean" class="clean"><span id="clean-child"></span></aside>
            </body></html>
            """;
        DomTree tree = Parse(html);
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (320f, 240f), NoIntrinsic, [], cache);
        NodeId target = Id(tree, differentialCase.Target);
        string? oldValue = tree.GetNode(target)?.GetAttribute(differentialCase.Attribute);
        tree.GetNode(target)!.SetAttribute(differentialCase.Attribute, differentialCase.NewValue);
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree,
                (320f, 240f),
                NoIntrinsic,
                [],
                null,
                cache,
                retained,
                [RetainedStyleMutation.From(new AttributeStyleMutation(
                    target,
                    differentialCase.Attribute,
                    oldValue,
                    differentialCase.NewValue))]);
        DomLayout full = RenderDom.LayoutDom(tree, (320f, 240f));

        AssertRetainedTelemetry(
            differentialCase.Name, telemetry, differentialCase.Expectation);
        AssertRetainedLayoutsMatch(differentialCase.Name, incremental, full);
    }

    private static DomLayout FinishRetainedTreeCase(
        string name,
        DomTree tree,
        StylesheetCache cache,
        DomLayout initial,
        TreeStyleMutation mutation,
        RetainedDifferentialExpectation expectation) =>
        FinishRetainedTreeBatchCase(name, tree, cache, initial, [mutation], expectation);

    private static DomLayout FinishRetainedTreeBatchCase(
        string name,
        DomTree tree,
        StylesheetCache cache,
        DomLayout initial,
        IReadOnlyList<TreeStyleMutation> mutations,
        RetainedDifferentialExpectation expectation)
    {
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        RetainedStyleMutation[] wrapped =
            [.. mutations.Select(RetainedStyleMutation.From)];
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree, (360f, 260f), NoIntrinsic, [], null, cache, retained, wrapped);
        DomLayout full = RenderDom.LayoutDom(tree, (360f, 260f));
        AssertRetainedTelemetry(name, telemetry, expectation);
        AssertRetainedLayoutsMatch(name, incremental, full);
        return incremental;
    }

    // Upstream 04418a5: a host-fetched sheet cascades from the store beside its <link>, even
    // when it is not origin-clean, and no <style> element is created for it.
    [Fact]
    public void HostFetchedStylesheetCascadesWithoutAVisibleStyleNode()
    {
        DomTree tree = Parse(
            """<html><head><link rel="stylesheet" href="https://cdn.test/app.css"></head><body><div id="target"></div></body></html>""");
        NodeId link = tree.QuerySelector("link") ?? throw new InvalidOperationException("stylesheet link");
        tree.SetExternalStylesheet(link, "#target{width:37px;height:19px}", originClean: false);

        DomLayout laid = RenderDom.LayoutDom(tree, (200f, 100f));
        NodeId target = Id(tree, "target");
        Assert.Equal(37f, laid.Rects[target].Width);
        Assert.Equal(19f, laid.Rects[target].Height);
        Assert.Empty(tree.QuerySelectorAll("style"));

        // A disabled link keeps its bytes but contributes nothing (HTMLLinkElement.disabled
        // reflects the content attribute in Chromium).
        tree.GetNode(link)!.SetAttribute("disabled", string.Empty);
        Assert.Null(tree.ExternalStylesheetCss(link));
        Assert.True(tree.TryGetExternalStylesheet(link, out _));
    }

    [Fact]
    public void ShadowRenderedChildrenDistributeNamedAndDefaultSlots()
    {
        DomTree tree = Parse(
            """<x-card id="host"><span id="default"></span><span id="title" slot="title"></span><span id="unslotted" slot="missing"></span></x-card><div id="source"><div id="before"></div><slot id="title-slot" name="title"><b id="title-fallback"></b></slot><slot id="duplicate-title" name="title"><i id="duplicate-fallback"></i></slot><slot id="default-slot"><em id="default-fallback"></em></slot><div id="after"></div></div>""");
        NodeId host = Id(tree, "host");
        NodeId source = Id(tree, "source");
        NodeId before = Id(tree, "before");
        NodeId titleSlot = Id(tree, "title-slot");
        NodeId duplicateTitle = Id(tree, "duplicate-title");
        NodeId duplicateFallback = Id(tree, "duplicate-fallback");
        NodeId defaultSlot = Id(tree, "default-slot");
        NodeId after = Id(tree, "after");
        NodeId defaultLight = Id(tree, "default");
        NodeId titleLight = Id(tree, "title");
        NodeId unslotted = Id(tree, "unslotted");
        AttachProgrammaticShadow(tree, host, source);

        Assert.Equal(
            new List<NodeId> { before, titleSlot, duplicateTitle, defaultSlot, after },
            DomTraversal.RenderedChildren(tree, host));
        Assert.Equal(new List<NodeId> { titleLight }, DomTraversal.RenderedChildren(tree, titleSlot));
        Assert.Equal(
            new List<NodeId> { duplicateFallback },
            DomTraversal.RenderedChildren(tree, duplicateTitle));
        Assert.Equal(new List<NodeId> { defaultLight }, DomTraversal.RenderedChildren(tree, defaultSlot));
        Assert.Equal(host, DomTraversal.RenderedParent(tree, before));
        Assert.Equal(titleSlot, DomTraversal.RenderedParent(tree, titleLight));
        Assert.Equal(defaultSlot, DomTraversal.RenderedParent(tree, defaultLight));
        Assert.Null(DomTraversal.RenderedParent(tree, unslotted));
        Assert.DoesNotContain(unslotted, DomTraversal.RenderedChildren(tree, host));
    }

    [Fact]
    public void NativeShadowTreeAndSlottedLightChildGenerateLayoutBoxes()
    {
        DomTree tree = Parse(
            """<html style="width:100px"><body style="margin:0"><x-card id="host" style="display:block;width:30px"><span id="light" style="display:block;height:10px"></span><span id="unslotted" slot="missing" style="display:block;height:80px"></span></x-card><div id="source"><div id="before" style="height:10px"></div><slot id="slot"></slot><div id="after" style="height:10px"></div></div></body></html>""");
        NodeId host = Id(tree, "host");
        NodeId source = Id(tree, "source");
        NodeId light = Id(tree, "light");
        NodeId unslotted = Id(tree, "unslotted");
        NodeId before = Id(tree, "before");
        NodeId after = Id(tree, "after");
        AttachProgrammaticShadow(tree, host, source);

        DomLayout laid = RenderDom.LayoutDom(tree, (100f, 100f));
        Rect hostRect = laid.Rects[host];
        Rect beforeRect = laid.Rects[before];
        Rect lightRect = laid.Rects[light];
        Rect afterRect = laid.Rects[after];
        Assert.Equal((30f, 30f), (hostRect.Width, hostRect.Height));
        Assert.Equal(hostRect.Y, beforeRect.Y);
        Assert.Equal(hostRect.Y + 10f, lightRect.Y);
        Assert.Equal(hostRect.Y + 20f, afterRect.Y);
        Assert.False(laid.Rects.ContainsKey(unslotted));
    }

    [Fact]
    public void OutOfFlowBoxesResolveAgainstTheViewportSizedInitialContainingBlock()
    {
        // CSS 2.1 10.1: the initial containing block has the dimensions of the viewport.
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
                <div style="height:3000px"></div>
                <div id="fixed-bottom" style="position:fixed;bottom:0;left:0;width:10px;height:20px"></div>
                <div id="fixed-mid" style="position:fixed;top:50%;left:0;width:10px;height:20px"></div>
                <div id="abs-bottom" style="position:absolute;bottom:0;left:0;width:10px;height:20px"></div>
                <div id="rel" style="position:relative;height:400px;width:100px">
                    <div id="abs-in-rel" style="position:absolute;top:50%;width:10px;height:10px"></div>
                </div>
            </body></html>
            """);
        NodeId fixedBottom = Id(tree, "fixed-bottom");
        NodeId fixedMid = Id(tree, "fixed-mid");
        NodeId absBottom = Id(tree, "abs-bottom");
        NodeId rel = Id(tree, "rel");
        NodeId absInRel = Id(tree, "abs-in-rel");

        DomLayout laid = RenderDom.LayoutDom(tree, (200f, 200f));

        Assert.Equal(180f, laid.Rects[fixedBottom].Y);
        Assert.Equal(100f, laid.Rects[fixedMid].Y);
        Assert.Equal(180f, laid.Rects[absBottom].Y);
        Assert.Equal(laid.Rects[rel].Y + 200f, laid.Rects[absInRel].Y);
    }

    [Fact]
    public void RootScrollingOverflowUsesComposedShadowTree()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0"><body style="margin:0">
                <x-card id="host" style="display:block;position:relative;width:100px;height:20px">
                    <div id="unslotted" slot="missing"
                         style="position:absolute;top:600px;width:10px;height:30px"></div>
                </x-card>
                <div id="source">
                    <div id="shadow-overflow"
                         style="position:absolute;top:250px;width:10px;height:30px"></div>
                </div>
            </body></html>
            """);
        NodeId host = Id(tree, "host");
        NodeId source = Id(tree, "source");
        NodeId unslotted = Id(tree, "unslotted");
        NodeId shadowOverflow = Id(tree, "shadow-overflow");
        AttachProgrammaticShadow(tree, host, source);

        DomLayout laid = RenderDom.LayoutDom(tree, (100f, 100f));
        Assert.False(laid.Rects.ContainsKey(unslotted));
        Assert.Equal(250f, laid.Rects[shadowOverflow].Y);
        Assert.Equal((100f, 280f), laid.ScrollingContentSize(tree, (100f, 100f)));
    }

    [Fact]
    public void NativeShadowAuthorStylesAreOrderedIsolatedAndInheritFromHost()
    {
        DomTree tree = Parse(
            """
            <style>
                 .target { width:123px; height:9px; padding-left:13px; color:#ff0000 }
                 .only-first-root { margin-left:99px }
               </style>
               <x-one id="host-one" style="display:block;--host-width:41px;color:#123456">
                 <div id="light" class="target only-first-root"></div>
               </x-one>
               <x-two id="host-two" style="display:block"></x-two>
               <div id="source-one">
                 <style>.target { width:20px; height:var(--local-height) }</style>
                 <style>.target { width:var(--host-width); color:inherit }
                        .holder { --local-height:23px }
                        .only-first-root { margin-left:7px }
                 </style>
                 <section class="holder"><div id="shadow-one" class="target only-first-root"></div></section>
               </div>
               <div id="source-two">
                 <style>.target { width:67px; height:31px }</style>
                 <div id="shadow-two" class="target only-first-root"></div>
               </div>
            """);
        NodeId hostOne = Id(tree, "host-one");
        NodeId hostTwo = Id(tree, "host-two");
        NodeId light = Id(tree, "light");
        NodeId sourceOne = Id(tree, "source-one");
        NodeId sourceTwo = Id(tree, "source-two");
        NodeId shadowOne = Id(tree, "shadow-one");
        NodeId shadowTwo = Id(tree, "shadow-two");
        AttachProgrammaticShadow(tree, hostOne, sourceOne);
        AttachProgrammaticShadow(tree, hostTwo, sourceTwo);

        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 300f));
        LayoutStyle lightStyle = laid.Styles[light];
        LayoutStyle firstStyle = laid.Styles[shadowOne];
        LayoutStyle secondStyle = laid.Styles[shadowTwo];

        Assert.Equal(Dimension.Px(123f), lightStyle.Width);
        Assert.Equal(Dimension.Px(9f), lightStyle.Height);
        Assert.Equal(13f, lightStyle.Padding.Left);
        Assert.Equal(99f, lightStyle.Margin.Left);
        Assert.Equal(Dimension.Px(41f), firstStyle.Width);
        Assert.Equal(Dimension.Px(23f), firstStyle.Height);
        Assert.Equal(0f, firstStyle.Padding.Left);
        Assert.Equal(7f, firstStyle.Margin.Left);
        Assert.Equal(Rgb(0x12, 0x34, 0x56), firstStyle.Color);
        Assert.Equal(Dimension.Px(67f), secondStyle.Width);
        Assert.Equal(Dimension.Px(31f), secondStyle.Height);
        Assert.Equal(0f, secondStyle.Margin.Left);
    }

    [Fact]
    public void ShadowHostRulesMatchInScopeAndFollowEncapsulationCascadeOrder()
    {
        DomTree tree = Parse(
            """
            <style>
                 x-one { width:110px; --normal-size:66px; height:var(--normal-size) }
                 x-one { margin-left:8px !important; padding-left:9px !important; --critical:7px !important }
               </style>
               <x-one id="host-one" class="active" style="margin-left:11px !important"></x-one>
               <x-two id="host-two"></x-two>
               <div id="source-one">
                 <style>
                   :host { display:block; width:40px; --normal-size:22px }
                   :host(.active) { margin-left:31px !important; padding-left:var(--critical) !important; --critical:17px !important }
                   :host([hidden]) { border-left-width:19px }
                   .active { border-right-width:23px }
                 </style>
                 <div style="height:5px"></div>
               </div>
               <div id="source-two">
                 <style>:host { display:block; width:55px }</style>
                 <div style="height:7px"></div>
               </div>
            """);
        NodeId hostOne = Id(tree, "host-one");
        NodeId hostTwo = Id(tree, "host-two");
        NodeId sourceOne = Id(tree, "source-one");
        NodeId sourceTwo = Id(tree, "source-two");
        AttachProgrammaticShadow(tree, hostOne, sourceOne);
        AttachProgrammaticShadow(tree, hostTwo, sourceTwo);

        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 300f));
        LayoutStyle first = laid.Styles[hostOne];
        LayoutStyle second = laid.Styles[hostTwo];

        Assert.Equal(Display.Block, first.Display);
        Assert.Equal(Dimension.Px(110f), first.Width);
        Assert.Equal(Dimension.Px(66f), first.Height);
        Assert.Equal(31f, first.Margin.Left);
        Assert.Equal(17f, first.Padding.Left);
        Assert.Equal(0f, first.Border.Left);
        Assert.Equal(0f, first.Border.Right);
        Assert.Equal(127f, laid.Rects[hostOne].Width);

        Assert.Equal(Dimension.Px(55f), second.Width);
        Assert.Equal(Dimension.Auto, second.Height);
        Assert.Equal(0f, second.Margin.Left);
    }

    [Fact]
    public void SlottedRulesStyleOnlyAssignedLightChildrenWithShadowCascadeOrder()
    {
        DomTree tree = Parse(
            """
            <style>
                 .item { width:120px; height:var(--normal-size); --normal-size:33px }
                 .item { margin-left:5px !important; padding-left:var(--critical) !important; --critical:7px !important }
               </style>
               <x-one id="host-one">
                 <span id="assigned-one" class="item" slot="title" style="margin-left:9px !important"></span>
                 <span id="unslotted" class="item" slot="missing"></span>
               </x-one>
               <x-two id="host-two"><span id="assigned-two" class="item"></span></x-two>
               <div id="source-one">
                 <style>
                   ::slotted(.item) { display:block; width:40px; --normal-size:11px; border-left:3px solid }
                   .wrapper slot.named::slotted(.item) { margin-left:21px !important; padding-left:var(--critical) !important; --critical:17px !important }
                   ::slotted([hidden]) { border-top:19px solid }
                   .item { border-right:23px solid }
                 </style>
                 <div class="wrapper"><slot class="named" name="title"></slot></div>
               </div>
               <div id="source-two">
                 <style>::slotted(.item) { display:block; border-bottom:4px solid }</style>
                 <slot></slot>
               </div>
            """);
        NodeId hostOne = Id(tree, "host-one");
        NodeId hostTwo = Id(tree, "host-two");
        NodeId sourceOne = Id(tree, "source-one");
        NodeId sourceTwo = Id(tree, "source-two");
        NodeId assignedOne = Id(tree, "assigned-one");
        NodeId assignedTwo = Id(tree, "assigned-two");
        NodeId unslotted = Id(tree, "unslotted");
        AttachProgrammaticShadow(tree, hostOne, sourceOne);
        AttachProgrammaticShadow(tree, hostTwo, sourceTwo);

        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 300f));
        LayoutStyle first = laid.Styles[assignedOne];
        LayoutStyle second = laid.Styles[assignedTwo];
        LayoutStyle hidden = laid.Styles[unslotted];

        Assert.Equal(Display.Block, first.Display);
        Assert.Equal(Dimension.Px(120f), first.Width);
        Assert.Equal(Dimension.Px(33f), first.Height);
        Assert.Equal(21f, first.Margin.Left);
        Assert.Equal(17f, first.Padding.Left);
        Assert.Equal(3f, first.Border.Left);
        Assert.Equal(0f, first.Border.Right);
        Assert.Equal(0f, first.Border.Top);
        Assert.Equal(140f, laid.Rects[assignedOne].Width);

        Assert.Equal(4f, second.Border.Bottom);
        Assert.Equal(0f, second.Border.Left);
        Assert.Equal(0f, hidden.Border.Left);
        Assert.False(laid.Rects.ContainsKey(unslotted));
    }

    [Fact]
    public void AssignedNodesInheritSlotStylesAndCustomProperties()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <x-card id="host" style="display:block;color:#010203;font-size:13px;--host-width:61px">
                 <span id="assigned" slot="title" style="display:block;width:var(--slot-width);height:5px"></span>
                 <span id="unslotted" slot="missing" style="display:block;width:var(--host-width);height:80px"></span>
               </x-card>
               <div id="source">
                 <style>
                   slot { color:#123456; font-size:21px; font-weight:700; --slot-width:47px }
                   #empty { color:#654321; font-size:17px; --fallback-width:31px }
                 </style>
                 <slot id="title-slot" name="title">
                   <span id="suppressed-fallback" style="display:block;width:var(--slot-width);height:70px"></span>
                 </slot>
                 <slot id="empty" name="empty">
                   <span id="visible-fallback" style="display:block;width:var(--fallback-width);height:6px"></span>
                 </slot>
               </div>
               </body></html>
            """);
        NodeId host = Id(tree, "host");
        NodeId source = Id(tree, "source");
        NodeId assigned = Id(tree, "assigned");
        NodeId unslotted = Id(tree, "unslotted");
        NodeId suppressedFallback = Id(tree, "suppressed-fallback");
        NodeId visibleFallback = Id(tree, "visible-fallback");
        AttachProgrammaticShadow(tree, host, source);

        DomLayout laid = RenderDom.LayoutDom(tree, (200f, 200f));
        LayoutStyle assignedStyle = laid.Styles[assigned];
        Assert.Equal(Rgb(0x12, 0x34, 0x56), assignedStyle.Color);
        Assert.Equal(21f, assignedStyle.FontSize);
        Assert.Equal(700, ComputedStyle.UsedFontWeight(assignedStyle));
        Assert.Equal(Dimension.Px(47f), assignedStyle.Width);
        Assert.Equal("47px", laid.CustomProperties[assigned]["--slot-width"]);

        LayoutStyle fallbackStyle = laid.Styles[visibleFallback];
        Assert.Equal(Rgb(0x65, 0x43, 0x21), fallbackStyle.Color);
        Assert.Equal(17f, fallbackStyle.FontSize);
        Assert.Equal(Dimension.Px(31f), fallbackStyle.Width);
        Assert.True(laid.Rects.ContainsKey(visibleFallback));

        LayoutStyle suppressedStyle = laid.Styles[suppressedFallback];
        Assert.Equal(Rgb(0x12, 0x34, 0x56), suppressedStyle.Color);
        Assert.Equal(Dimension.Px(47f), suppressedStyle.Width);
        Assert.False(laid.Rects.ContainsKey(suppressedFallback));

        LayoutStyle unslottedStyle = laid.Styles[unslotted];
        Assert.Equal(Rgb(0x01, 0x02, 0x03), unslottedStyle.Color);
        Assert.Equal(13f, unslottedStyle.FontSize);
        Assert.Equal(Dimension.Px(61f), unslottedStyle.Width);
        Assert.False(laid.Rects.ContainsKey(unslotted));
    }

    [Fact]
    public void NestedSlotReassignmentUsesEachFlattenedParentInOrder()
    {
        DomTree tree = Parse(
            """
            <html><body style="margin:0">
               <x-outer id="outer" style="display:block">
                 <span id="target" slot="forward" style="display:block;width:var(--inner-width);height:var(--forward-height)"></span>
               </x-outer>
               <div id="outer-source">
                 <style>
                   slot[name=forward] { color:#445566; --forward-height:23px }
                 </style>
                 <x-inner id="inner">
                   <slot id="forward-slot" name="forward" slot="bridge"></slot>
                 </x-inner>
               </div>
               <div id="inner-source">
                 <style>
                   slot[name=bridge] { color:#112233; font-size:19px; font-weight:700; --inner-width:47px }
                 </style>
                 <slot id="bridge-slot" name="bridge"></slot>
               </div>
               </body></html>
            """);
        NodeId outer = Id(tree, "outer");
        NodeId inner = Id(tree, "inner");
        NodeId outerSource = Id(tree, "outer-source");
        NodeId innerSource = Id(tree, "inner-source");
        NodeId forwardSlot = Id(tree, "forward-slot");
        NodeId bridgeSlot = Id(tree, "bridge-slot");
        NodeId target = Id(tree, "target");
        AttachProgrammaticShadow(tree, outer, outerSource);
        AttachProgrammaticShadow(tree, inner, innerSource);

        Assert.Equal(forwardSlot, tree.AssignedSlot(target));
        Assert.Equal(bridgeSlot, tree.AssignedSlot(forwardSlot));

        DomLayout laid = RenderDom.LayoutDom(tree, (200f, 200f));
        LayoutStyle bridgeStyle = laid.Styles[bridgeSlot];
        LayoutStyle forwardStyle = laid.Styles[forwardSlot];
        LayoutStyle targetStyle = laid.Styles[target];

        Assert.Equal(Rgb(0x11, 0x22, 0x33), bridgeStyle.Color);
        Assert.Equal(Rgb(0x44, 0x55, 0x66), forwardStyle.Color);
        Assert.Equal(Rgb(0x44, 0x55, 0x66), targetStyle.Color);
        Assert.Equal(19f, forwardStyle.FontSize);
        Assert.Equal(19f, targetStyle.FontSize);
        Assert.Equal(700, ComputedStyle.UsedFontWeight(targetStyle));
        Assert.Equal(Dimension.Px(47f), targetStyle.Width);
        Assert.Equal(Dimension.Px(23f), targetStyle.Height);
        Assert.Equal("47px", laid.CustomProperties[target]["--inner-width"]);
        Assert.False(laid.Rects.ContainsKey(forwardSlot));
        Assert.True(laid.Rects.ContainsKey(target));
    }

    [Fact]
    public void DirectShadowInlineBlockIsBlockifiedAsAFlexItem()
    {
        DomTree tree = Parse(
            """<html><body style="margin:0"><x-row id="host" style="display:flex;width:100px"><div id="source"><span id="item" style="display:inline-block;width:20px"><span style="display:block;height:10px"></span><span style="display:block;height:10px"></span></span></div></x-row></body></html>""");
        NodeId host = Id(tree, "host");
        NodeId source = Id(tree, "source");
        NodeId item = Id(tree, "item");
        AttachProgrammaticShadow(tree, host, source);

        DomLayout laid = RenderDom.LayoutDom(tree, (120f, 80f));
        Assert.Equal(20f, laid.Rects[item].Height);
    }

    [Fact]
    public void LaysOutRealDom()
    {
        DomTree tree = Parse(
            "<html><body><div style=\"width: 300px\"><div style=\"width: 100px; height: 40px\"></div><div style=\"width: 100px; height: 60px\"></div></div></body></html>");
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));

        // Every element gets a rect.
        Assert.True(laid.Rects.Count >= 4, $"expected >=4 element rects, got {laid.Rects.Count}");

        // The two inner divs stack vertically inside the 300px container.
        List<(float Y, float Height)> stacks = [];
        foreach (Rect r in laid.Rects.Values)
        {
            if (MathF.Abs(r.Width - 100f) < 0.1f)
            {
                stacks.Add((r.Y, r.Height));
            }
        }

        Assert.Equal(2, stacks.Count);
        stacks.Sort((a, b) => a.Y.CompareTo(b.Y));
        Assert.True(
            MathF.Abs(stacks[0].Height - 40f) < 0.1f && MathF.Abs(stacks[1].Height - 60f) < 0.1f,
            $"children heights should be 40 and 60, got {stacks[0]} {stacks[1]}");
    }

    [Fact]
    public void NativeButtonIntrinsicWidthUsesOnlyRenderedInFlowContent()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                button{font-size:16px;padding:0 8px;border:0}
                .gone{display:none}
                .a11y{position:absolute;width:1px;height:1px}
              </style>
              <button id="plain">v5.3</button>
              <button id="labels"><span class="gone">Bootstrap</span><span class="a11y">Bootstrap </span>v5.3<span class="a11y">(switch to other versions)</span></button>
              <button id="icon"><svg style="width:16px;height:16px"></svg></button>
              <button id="icon-label"><svg style="width:16px;height:16px"></svg><span class="gone">Toggle theme</span></button>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect Rect(string id) => laid.Rects[Id(tree, id)];

        Assert.True(
            MathF.Abs(Rect("plain").Width - Rect("labels").Width) < 0.1f,
            $"hidden/out-of-flow labels enlarged the button: plain={Rect("plain")}, labels={Rect("labels")}");
        Assert.True(
            MathF.Abs(Rect("icon").Width - Rect("icon-label").Width) < 0.1f,
            $"display:none icon label enlarged the button: icon={Rect("icon")}, labelled={Rect("icon-label")}");
        Assert.True(
            MathF.Abs(Rect("icon").Width - 32f) < 0.1f,
            $"the in-flow 16px SVG and horizontal padding must contribute: {Rect("icon")}");
    }

    [Fact]
    public void AutoWidthButtonIsAsWideAsTheSameLabelInASpan()
    {
        // A button's box is sized from its label by the control pass, but the label is laid
        // out by the inline engine. Sizing with TextWidth used ab_glyph's height-based
        // PxScale while the shaper uses an em-based size, so the box came out 10.5%
        // narrower than its own text: layout still reported one line while paint wrapped
        // the label inside the button. Chromium makes button, span and inline-block
        // identical for identical text, so that equality is the invariant to hold,
        // independent of which face the engine embeds.
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .c{font-size:14px;padding:8px 16px;border:1px solid #ccc;box-sizing:border-box}
                span.c,div.c{display:inline-block}
            </style>
            <button class="c" id="b">Pulse UI</button>
            <span class="c" id="s">Pulse UI</span>
            <div class="c" id="d">Pulse UI</div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        Assert.True(
            MathF.Abs(Width("b") - Width("s")) < 0.5f,
            $"button must match an inline-block span: button={Width("b")}, span={Width("s")}");
        Assert.True(
            MathF.Abs(Width("b") - Width("d")) < 0.5f,
            $"button must match an inline-block div: button={Width("b")}, div={Width("d")}");
    }

    [Fact]
    public void TextAlignmentDoesNotShrinkWrapBlockGridAndFlexChildren()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #host{width:900px;text-align:center}
                #grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:20px}
                .card{overflow:hidden;height:30px}
                .wide{width:1200px;height:10px}
                #flex{display:flex}
                #flex > div{flex:1;height:20px}
            </style>
            <div id="host">
              <div id="grid">
                <div id="card-a" class="card"><div class="wide"></div></div>
                <div id="card-b" class="card"><div class="wide"></div></div>
              </div>
              <div id="flex"><div></div><div></div></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 300f));
        Rect Rect(string name) => laid.Rects[Id(tree, name)];
        Rect host = Rect("host");
        Rect grid = Rect("grid");
        Rect cardA = Rect("card-a");
        Rect cardB = Rect("card-b");
        Rect flex = Rect("flex");

        Assert.True(MathF.Abs(host.Width - 900f) < 0.01f, $"{host}");
        Assert.True(MathF.Abs(grid.Width - 900f) < 0.01f, $"{grid}");
        Assert.True(
            MathF.Abs(cardA.Width - 440f) < 0.01f
            && MathF.Abs(cardB.Width - 440f) < 0.01f
            && MathF.Abs(cardB.X - cardA.X - 460f) < 0.01f,
            $"grid tracks escaped their containing block: {cardA} {cardB}");
        Assert.True(MathF.Abs(flex.Width - 900f) < 0.01f, $"{flex}");
    }

    /// <summary>
    /// A grid item's containing block is its grid area, so a percentage block size resolves
    /// against the row track even when the grid container itself has no definite height.
    /// `align-items: center` is the case that matters: the item is not stretched, so nothing
    /// else can supply its height. Chromium lays the item and its absolutely-positioned
    /// child out 24px tall; the engine used to compute the percentage to `auto` and collapse
    /// both to 0.
    /// </summary>
    [Fact]
    public void GridItemPercentageHeightResolvesAgainstGridAreaWithoutStretch()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .grid{display:grid;grid-template-columns:40px;grid-template-rows:24px;width:200px;align-items:center}
                .item{height:100%;width:100%;padding:0;border:0;position:relative}
                .abs{position:absolute;top:0;left:0;width:100%;height:100%}
            </style>
            <div class="grid">
              <div id="item" class="item"><div id="abs" class="abs"></div></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 300f));
        Rect item = laid.Rects[Id(tree, "item")];
        Rect abs = laid.Rects[Id(tree, "abs")];

        Assert.True(MathF.Abs(item.Width - 40f) < 0.01f, $"{item}");
        Assert.True(MathF.Abs(item.Height - 24f) < 0.01f, $"{item}");
        Assert.True(MathF.Abs(abs.Width - 40f) < 0.01f, $"{abs}");
        Assert.True(MathF.Abs(abs.Height - 24f) < 0.01f, $"{abs}");
    }

    /// <summary>
    /// The stretched variant reaches the same size down a different path (stretch alignment
    /// rather than the item's own percentage), and is here so a change that only repairs one
    /// of the two cannot look complete.
    /// </summary>
    [Fact]
    public void GridItemPercentageHeightResolvesAgainstGridAreaWhenStretched()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .grid{display:grid;grid-template-columns:40px;grid-template-rows:24px;width:200px;align-items:stretch}
                .item{height:100%;width:100%;padding:0;border:0}
            </style>
            <div class="grid"><div id="item" class="item"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 300f));
        Rect item = laid.Rects[Id(tree, "item")];

        Assert.True(MathF.Abs(item.Width - 40f) < 0.01f, $"{item}");
        Assert.True(MathF.Abs(item.Height - 24f) < 0.01f, $"{item}");
    }

    /// <summary>
    /// An `auto` row is sized from its items, so the percentage must not feed back into the
    /// track: the item contributes its content height and then takes all of it. Chromium
    /// gives 10px, the height of the one child.
    /// </summary>
    [Fact]
    public void GridItemPercentageHeightAgainstAutoRowUsesContentHeight()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .grid{display:grid;grid-template-columns:40px;grid-template-rows:auto;width:200px;align-items:center}
                .item{height:100%;width:100%;padding:0;border:0}
                .spacer{height:10px}
            </style>
            <div class="grid"><div id="item" class="item"><div class="spacer"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 300f));
        Rect item = laid.Rects[Id(tree, "item")];

        Assert.True(MathF.Abs(item.Width - 40f) < 0.01f, $"{item}");
        Assert.True(MathF.Abs(item.Height - 10f) < 0.01f, $"{item}");
    }

    /// <summary>
    /// The grid-area rule is scoped to grid items: a percentage height under an ordinary
    /// auto-height block parent still behaves as `auto` and takes the content height (7px in
    /// Chromium), which is what keeps `.tss-card` from filling the viewport.
    /// </summary>
    [Fact]
    public void PercentageHeightUnderAutoHeightBlockParentStillBehavesAsAuto()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .parent{width:60px}
                .pct{height:50%}
                .filler{height:7px}
            </style>
            <div class="parent"><div id="pct" class="pct"><div class="filler"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 300f));
        Rect pct = laid.Rects[Id(tree, "pct")];

        Assert.True(MathF.Abs(pct.Width - 60f) < 0.01f, $"{pct}");
        Assert.True(MathF.Abs(pct.Height - 7f) < 0.01f, $"{pct}");
    }

    /// <summary>
    /// A functional block-axis percentage has to be flattened to px before layout, and a
    /// grid item's area size is not available in that pass, so a `calc()` height inside one
    /// stays `auto` rather than flattening against an invented basis. Chromium resolves it
    /// to 20px (24 - 4); the tripwire here is only that it stays inside the grid area
    /// instead of going negative or escaping it. See "Known deviations" in todo.md.
    /// </summary>
    [Fact]
    public void CalcPercentageHeightInsideGridItemStaysWithinTheGridArea()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .grid{display:grid;grid-template-columns:40px;grid-template-rows:24px;width:200px;align-items:center}
                .item{height:100%;width:100%;padding:0;border:0}
                .calcchild{height:calc(100% - 4px)}
            </style>
            <div class="grid"><div id="item" class="item"><div id="calcchild" class="calcchild"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 300f));
        Rect item = laid.Rects[Id(tree, "item")];
        Rect calc = laid.Rects[Id(tree, "calcchild")];

        Assert.True(MathF.Abs(item.Height - 24f) < 0.01f, $"{item}");
        Assert.True(calc.Height >= 0f && calc.Height <= 24f, $"{calc}");
    }

    [Fact]
    public void StickyNormalFlowExcludesOwnPixelAndPercentageTranslates()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #flow{width:300px;height:900px}
                .spacer{height:100px}
                .sticky{position:sticky;top:0}
                #pixel{width:120px;height:40px;transform:translate(7px,-15px)}
                #percent{width:120px;height:50px;transform:translateY(-110%)}
            </style>
            <div id="flow">
              <div class="spacer"></div>
              <div id="pixel" class="sticky"></div>
              <div class="spacer"></div>
              <div id="percent" class="sticky"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 240f));
        StickyLayout sticky = laid.RootStickyLayout(tree, (400f, 240f));

        foreach (string name in new[] { "pixel", "percent" })
        {
            NodeId id = Id(tree, name);
            Rect rect = laid.Rects[id];
            StickyFrame frame = sticky.Frames.Single(f => f.Id == id);
            Assert.True(
                MathF.Abs(frame.Normal.X - rect.X) < 0.01f
                && MathF.Abs(frame.Normal.Y - rect.Y) < 0.01f,
                $"{name} own transform must not alter its sticky normal position: rect={rect} frame={frame.Normal}");
        }
    }

    [Fact]
    public void IndividualTranslatePercentageUsesOwnBorderBox()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #containing-block{position:relative;width:453px;height:412px}
                #card{
                    position:absolute;
                    top:50%;
                    right:64px;
                    width:310px;
                    height:280px;
                    --tw-translate-x:0;
                    --tw-translate-y:calc(calc(1 / 2 * 100%) * -1);
                    translate:var(--tw-translate-x) var(--tw-translate-y);
                }
            </style>
            <div id="containing-block"><div id="card"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        NodeId card = Id(tree, "card");
        Rect rect = laid.Rects[card];
        (float X, float Y) translated = laid.Translates[card];

        Assert.True(
            MathF.Abs(rect.Y - 206f) < 0.01f,
            $"absolute top:50% must use the containing block: {rect}");
        Assert.True(
            MathF.Abs(translated.X) < 0.01f && MathF.Abs(translated.Y + 140f) < 0.01f,
            $"translate percentage must use the card's own 280px border box: {translated}");
        Assert.True(laid.Styles[card].EstablishesPositioningContainingBlock());
    }

    [Fact]
    public void StickyNormalFlowRetainsTransformedAncestorCoordinates()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #ancestor{
                    width:300px;
                    height:700px;
                    transform:translate(25px,30px)
                }
                #spacer{height:100px}
                #sticky{
                    position:sticky;
                    top:0;
                    width:120px;
                    height:40px;
                    transform:translate(-5px,-20px)
                }
            </style>
            <div id="ancestor">
              <div id="spacer"></div>
              <div id="sticky"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 240f));
        NodeId id = Id(tree, "sticky");
        Rect rect = laid.Rects[id];
        StickyLayout sticky = laid.RootStickyLayout(tree, (400f, 240f));
        StickyFrame frame = sticky.Frames.Single(f => f.Id == id);

        Assert.True(
            MathF.Abs(frame.Normal.X - (rect.X + 25f)) < 0.01f
            && MathF.Abs(frame.Normal.Y - (rect.Y + 30f)) < 0.01f,
            $"ancestor transform must remain in sticky constraint coordinates: rect={rect} frame={frame.Normal}");
    }

    [Fact]
    public void StickyHorizontalOverconstraintPrioritizesLogicalInlineStart()
    {
        float ltr = StickyLayout.StickyAxisPosition(
            0f, 80f, -100f, 100f, 0f, 100f, 30f, 30f, false);
        float rtl = StickyLayout.StickyAxisPosition(
            0f, 80f, -100f, 100f, 0f, 100f, 30f, 30f, true);
        Assert.Equal(30f, ltr);
        Assert.Equal(-10f, rtl);
    }

    [Fact]
    public void StickyOwnerValidationSkipsBoxlessScrollersAndDeclinesAffineSpaces()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #boxless{display:contents;overflow:auto}
                #root-sticky{position:sticky;top:0;width:20px;height:20px}
                #affine{transform:rotate(2deg)}
                #affine-sticky{position:sticky;top:0;width:20px;height:20px}
            </style>
            <div style="height:50px"></div>
            <div id="boxless"><div id="root-sticky"></div></div>
            <div id="affine"><div id="affine-sticky"></div></div>
            <div style="height:400px"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (200f, 100f));
        NodeId rootSticky = Id(tree, "root-sticky");
        NodeId affineSticky = Id(tree, "affine-sticky");
        StickyLayout sticky = laid.RootStickyLayout(tree, (200f, 100f));

        Assert.Contains(
            sticky.Frames,
            frame => frame.Id == rootSticky && frame.ScrollOwner == ScrollId.Root);
        Assert.DoesNotContain(sticky.Frames, frame => frame.Id == affineSticky);
    }

    [Fact]
    public void PublicStickyLayoutResolvesNestedZeroOffsetConstraints()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0}</style>
            <div style="width:100px;height:80px;overflow:hidden">
                <div style="height:100px"></div>
                <div id="sticky" style="position:sticky;bottom:0;width:20px;height:20px"></div>
                <div style="height:20px"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (120f, 100f));
        NodeId id = Id(tree, "sticky");
        StickyLayout sticky = laid.RootStickyLayout(tree, (120f, 100f));
        Assert.Equal(-40f, sticky.TranslationFor(id, (120f, 100f), (0f, 0f)).Y);
        Assert.Equal(-40f, sticky.Translations((120f, 100f), (0f, 0f))[id].Y);
    }

    [Fact]
    public void ContainerQueryUsesFinalContentBoxGeometry()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                #container {
                    box-sizing:border-box;
                    container-type:inline-size;
                    width:500px;
                    padding:20px;
                    border:10px solid;
                }
                #target { width:1px; height:1px }
                @container (width >= 440px) { #target { width:44px } }
                @container (width > 499px) { #target { width:50px } }
            </style></head><body>
                <div id="container"><div id="target"></div></div>
            </body></html>
            """);
        var (laid, telemetry) = RenderDom.LayoutDomWithWebFontsMeasured(
            tree, (1280f, 720f), new Dictionary<NodeId, ReplacedIntrinsic>(), []);
        NodeId container = Id(tree, "container");
        NodeId target = Id(tree, "target");
        Assert.True(MathF.Abs(laid.Rects[container].Width - 500f) < 0.1f);
        Assert.Equal(Dimension.Px(44f), laid.Styles[target].Width);
        Assert.Equal(2, telemetry.Passes);
        Assert.Equal(ContainerLayoutTermination.GeometryStable, telemetry.Termination);
    }

    [Fact]
    public void ContainerQueryNamedAndUnnamedLookupChooseDifferentAncestors()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                #outer { container: shell / inline-size; width:600px }
                #inner { container: other / inline-size; width:300px }
                #target { width:1px; height:1px }
                @container shell (min-width:500px) { #target { width:11px } }
                @container (min-width:500px) { #target { height:22px } }
            </style></head><body>
                <div id="outer"><div id="inner"><div id="target"></div></div></div>
            </body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId target = Id(tree, "target");
        Assert.Equal(Dimension.Px(11f), laid.Styles[target].Width);
        Assert.Equal(Dimension.Px(1f), laid.Styles[target].Height);
    }

    [Fact]
    public void NestedContainerQueriesSelectIndependentAncestors()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                #outer { container: outer / inline-size; width:600px }
                #inner { container: inner / inline-size; width:200px }
                #target { width:1px }
                @container outer (min-width:500px) {
                    @container inner (min-width:200px) {
                        #target { width:19px }
                    }
                }
            </style></head><body>
                <div id="outer"><div id="inner"><div id="target"></div></div></div>
            </body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId target = Id(tree, "target");
        Assert.Equal(Dimension.Px(19f), laid.Styles[target].Width);
    }

    [Fact]
    public void ContainerQueryPseudoCanSelectItsOriginatingElement()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                #container { container-type:inline-size; width:300px }
                @container (min-width:300px) {
                    #container::before { content:"active"; display:block }
                }
            </style></head><body><div id="container"></div></body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId container = Id(tree, "container");
        Assert.Equal("active", laid.Styles[container].BeforePseudo?.BeforeContent);
    }

    [Fact]
    public void ContainerTypeZeroesShrinkToFitInlineIntrinsicWidths()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #host{width:600px}
                .container{container-type:inline-size}
                .wide{width:240px;height:30px}
                #inline-block{display:inline-block}
                #inline-flex{display:inline-flex}
                #inline-grid{display:inline-grid}
            </style>
            <div id="host">
              <div id="inline-block" class="container"><div class="wide"></div></div>
              <div id="inline-flex" class="container"><div class="wide"></div></div>
              <div id="inline-grid" class="container"><div class="wide"></div></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 300f));
        foreach (string name in new[] { "inline-block", "inline-flex", "inline-grid" })
        {
            Rect rect = laid.Rects[Id(tree, name)];
            Assert.True(
                MathF.Abs(rect.Width) < 0.01f,
                $"{name} descendants leaked into contained intrinsic width: {rect}");
            Assert.True(
                rect.Height >= 29.9f,
                $"inline-size containment must retain content-driven block size: {rect}");
        }
    }

    [Fact]
    public void InlineOuterBoxesShrinkWrapInOnlyChildAndTextFlows()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .host{width:700px}
                .wide{width:400px;height:20px}
                #ib,#ibt{display:inline-block}
                #if,#ift{display:inline-flex}
                #ig,#igt{display:inline-grid}
            </style>
            <div class="host"><div id="ib"><div class="wide"></div></div></div>
            <div class="host"><div id="if"><div class="wide"></div></div></div>
            <div class="host"><div id="ig"><div class="wide"></div></div></div>
            <div class="host">A<div id="ibt"><div class="wide"></div></div>B</div>
            <div class="host">A<div id="ift"><div class="wide"></div></div>B</div>
            <div class="host">A<div id="igt"><div class="wide"></div></div>B</div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (700f, 300f));
        foreach (string name in new[] { "ib", "if", "ig", "ibt", "ift", "igt" })
        {
            Rect rect = laid.Rects[Id(tree, name)];
            Assert.True(
                MathF.Abs(rect.Width - 400f) < 0.01f,
                $"{name} did not shrink-wrap its 400px child: {rect}");
        }
    }

    [Fact]
    public void InlineFlexAndGridPreservePercentageAndBoxEdgeGeometry()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .host{width:700px}
                .percent{width:50%}
                .flex{display:inline-flex}
                .grid{display:inline-grid}
                .edges{padding:10px 20px;border:5px solid;margin:7px}
                .child{width:100px;height:20px}
            </style>
            <div class="host"><div id="flex-percent" class="flex percent"><div class="child"></div></div></div>
            <div class="host"><div id="grid-percent" class="grid percent"><div class="child"></div></div></div>
            <div class="host"><div id="flex-edges" class="flex edges"><div class="child"></div></div></div>
            <div class="host"><div id="grid-edges" class="grid edges"><div class="child"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        Rect Rect(string name) => laid.Rects[Id(tree, name)];
        foreach (string name in new[] { "flex-percent", "grid-percent" })
        {
            Rect item = Rect(name);
            Assert.True(
                MathF.Abs(item.Width - 350f) < 0.01f && MathF.Abs(item.Height - 20f) < 0.01f,
                $"{name}: {item}");
        }

        foreach (string name in new[] { "flex-edges", "grid-edges" })
        {
            Rect item = Rect(name);
            Assert.True(
                MathF.Abs(item.X - 7f) < 0.01f
                && MathF.Abs(item.Width - 150f) < 0.01f
                && MathF.Abs(item.Height - 50f) < 0.01f,
                $"{name}: {item}");
        }
    }

    [Fact]
    public void InlineFlexAndGridResolvePercentageMinMaxAgainstContainingBlock()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .host{width:700px}
                .flex{display:inline-flex}
                .grid{display:inline-grid}
                .max{max-width:50%}
                .min{min-width:50%}
                .wide{width:500px;height:20px}
                .narrow{width:100px;height:20px}
            </style>
            <div class="host"><div id="flex-max" class="flex max"><div class="wide"></div></div></div>
            <div class="host"><div id="grid-max" class="grid max"><div class="wide"></div></div></div>
            <div class="host"><div id="flex-min" class="flex min"><div class="narrow"></div></div></div>
            <div class="host"><div id="grid-min" class="grid min"><div class="narrow"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        foreach (string name in new[] { "flex-max", "grid-max", "flex-min", "grid-min" })
        {
            Rect item = laid.Rects[Id(tree, name)];
            Assert.True(
                MathF.Abs(item.Width - 350f) < 0.01f,
                $"{name} resolved its percentage constraint against the wrong box: {item}");
        }
    }

    [Fact]
    public void InlineAtomicVerticalAlignControlsCrossAxisPlacement()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .line{font:20px/40px sans-serif}
                .atom{display:inline-flex;width:30px}
                .bottom{vertical-align:bottom}
                .middle{vertical-align:middle}
                #bottom-short,#middle-short{height:10px}
                #bottom-tall,#middle-tall{height:30px}
            </style>
            <div class="line">x<span id="bottom-short" class="atom bottom"></span><span id="bottom-tall" class="atom bottom"></span></div>
            <div class="line">x<span id="middle-short" class="atom middle"></span><span id="middle-tall" class="atom middle"></span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 200f));
        Rect Rect(string name) => laid.Rects[Id(tree, name)];
        Rect bottomShort = Rect("bottom-short");
        Rect bottomTall = Rect("bottom-tall");
        Assert.True(
            MathF.Abs(bottomShort.Y + bottomShort.Height - bottomTall.Y - bottomTall.Height) < 0.01f,
            $"bottom-aligned atoms did not share a line bottom: {bottomShort} {bottomTall}");
        Rect middleShort = Rect("middle-short");
        Rect middleTall = Rect("middle-tall");
        Assert.True(
            MathF.Abs(
                middleShort.Y + (middleShort.Height / 2f) - middleTall.Y - (middleTall.Height / 2f))
            < 0.01f,
            $"middle-aligned atoms did not share a line center: {middleShort} {middleTall}");
    }

    [Fact]
    public void BlockifiedInlineFlexAndGridItemsRetainInnerLayout()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .host{display:flex;width:700px}
                #if{display:inline-flex}
                #ig{display:inline-grid;grid-template-columns:100px 100px}
                .item{width:100px;height:20px}
            </style>
            <div class="host"><div id="if"><div id="ifa" class="item"></div><div id="ifb" class="item"></div></div></div>
            <div class="host"><div id="ig"><div id="iga" class="item"></div><div id="igb" class="item"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (700f, 300f));
        Rect Rect(string name) => laid.Rects[Id(tree, name)];
        foreach ((string container, string first, string second) in new[]
        {
            ("if", "ifa", "ifb"),
            ("ig", "iga", "igb"),
        })
        {
            Rect containerRect = Rect(container);
            Rect firstRect = Rect(first);
            Rect secondRect = Rect(second);
            Assert.True(MathF.Abs(containerRect.Width - 200f) < 0.01f, $"{containerRect}");
            Assert.True(
                MathF.Abs(firstRect.Y - secondRect.Y) < 0.01f
                && MathF.Abs(secondRect.X - firstRect.X - 100f) < 0.01f,
                $"inner layout was destroyed: first={firstRect} second={secondRect}");
        }
    }

    [Fact]
    public void OverflowingGridItemAutoMarginsUseSafeLogicalStart()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .grid{display:grid;grid-template-columns:300px;grid-template-rows:20px;width:300px;position:relative}
                .item{height:20px}
                .wide{width:600px;height:1px}
                .left{margin-left:auto}
                .both{margin-left:auto;margin-right:auto}
                .center{justify-self:center}
                .end{justify-self:end}
                .absolute{position:absolute;grid-area:1/1;width:600px;height:20px}
            </style>
            <div id="ltr-center-grid" class="grid"><div id="ltr-center" class="item left center"><div class="wide"></div></div></div>
            <div id="ltr-end-grid" class="grid"><div id="ltr-end" class="item both end"><div class="wide"></div></div></div>
            <div id="absolute-center-grid" class="grid"><div id="absolute-center" class="absolute left center"></div></div>
            <div id="absolute-end-grid" class="grid"><div id="absolute-end" class="absolute left end"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        Rect Rect(string id) => laid.Rects[Id(tree, id)];

        foreach ((string gridId, string itemId, float expectedX) in new[]
        {
            ("ltr-center-grid", "ltr-center", 0f),
            ("ltr-end-grid", "ltr-end", 0f),
        })
        {
            Rect grid = Rect(gridId);
            Rect item = Rect(itemId);
            Assert.True(
                MathF.Abs(item.Width - 600f) < 0.01f,
                $"{itemId} lost its min-content floor: {item}");
            Assert.True(
                MathF.Abs(item.X - grid.X - expectedX) < 0.01f,
                $"{itemId} did not use safe logical-start overflow: grid={grid} item={item}");
        }

        foreach ((string gridId, string itemId, float expectedX) in new[]
        {
            ("absolute-center-grid", "absolute-center", -150f),
            ("absolute-end-grid", "absolute-end", -300f),
        })
        {
            Rect grid = Rect(gridId);
            Rect item = Rect(itemId);
            Assert.True(
                MathF.Abs(item.X - grid.X - expectedX) < 0.01f,
                $"{itemId} incorrectly used the in-flow auto-margin fallback: grid={grid} item={item}");
        }
    }

    [Fact]
    public void RootAndBodyInlineOuterGeometryMatchesBlockificationRules()
    {
        foreach (string display in new[] { "inline-block", "inline-flex", "inline-grid" })
        {
            DomTree tree = Parse(
                $$"""
                <style>
                    html{margin:0}body{margin:0;display:{{display}}}
                    .wide{width:400px;height:20px}
                </style><div class="wide"></div>
                """);
            DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
            NodeId body = LocalRoot(tree, "body");
            Assert.True(
                MathF.Abs(laid.Rects[body].Width - 400f) < 0.01f,
                $"body {display}: {laid.Rects[body]}");

            tree = Parse(
                $$"""
                <style>
                    html{margin:0;display:{{display}}}body{margin:0}
                    .wide{width:400px;height:20px}
                </style><div class="wide"></div>
                """);
            laid = RenderDom.LayoutDom(tree, (800f, 300f));
            NodeId root = LocalRoot(tree, "html");
            Assert.True(
                MathF.Abs(laid.Rects[root].Width - 800f) < 0.01f,
                $"root {display}: {laid.Rects[root]}");
        }
    }

    [Fact]
    public void ViewportOverflowClipDoesNotTruncateRootScrollingOverflow()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0;height:100%}
                html{overflow:hidden}
                main{padding-top:48px}
                #long{height:5000px}
            </style><main id="main"><div id="long"></div></main>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 1000f));
        Rect main = laid.Rects[Id(tree, "main")];
        (float Width, float Height) content = laid.ScrollingContentSize(tree, (900f, 1000f));
        Assert.True(MathF.Abs(main.Height - 5048f) < 0.01f, $"{main}");
        Assert.Equal((900f, 5048f), content);
    }

    [Fact]
    public void PropagatedBodyOverflowIsVisibleToLayoutAndDoesNotCreateABfc()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;overflow:visible">
               <body style="margin:0;overflow:hidden"><div style="height:20px"></div></body>
               </html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (100f, 60f));
        NodeId body = LocalRoot(tree, "body");
        LayoutStyle style = laid.Styles[body];
        Assert.True(style.OverflowPropagatedToViewport);
        Assert.False(DomStyleFixups.EstablishesBlockFormattingContext(style));
        PocketCalculator.Render.Layout.Style taffy = TaffyStyleMapping.ToTaffyStyle(style);
        Assert.Equal(Overflow.Visible, taffy.Overflow.X);
        Assert.Equal(Overflow.Visible, taffy.Overflow.Y);
    }

    [Fact]
    public void HtmlOwnedOverflowKeepsBodyClipInRootScrollingOverflow()
    {
        DomTree tree = Parse(
            """
            <html style="margin:0;overflow:auto">
               <body style="margin:0;width:100px;height:50px;overflow:hidden">
                 <div style="position:absolute;left:150px;top:60px;width:20px;height:20px"></div>
               </body>
               </html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (80f, 40f));
        Assert.Equal((100f, 50f), laid.ScrollingContentSize(tree, (80f, 40f)));
    }

    [Fact]
    public void DescendantOverflowClipStillBoundsRootScrollingOverflow()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #clip{height:100px;overflow:hidden}
                #long{height:5000px}
            </style><div id="clip"><div id="long"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 1000f));
        Assert.Equal((900f, 1000f), laid.ScrollingContentSize(tree, (900f, 1000f)));
    }

    [Fact]
    public void SizeContainerAutoBlockSizeIgnoresDescendants()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                .container{width:200px}
                .child{height:80px}
                #inline-only{container-type:inline-size}
                #both{container-type:size}
            </style>
            <div id="inline-only" class="container"><div class="child"></div></div>
            <div id="both" class="container"><div class="child"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 300f));
        Rect inlineOnly = laid.Rects[Id(tree, "inline-only")];
        Rect both = laid.Rects[Id(tree, "both")];
        Assert.True(MathF.Abs(inlineOnly.Height - 80f) < 0.01f, $"{inlineOnly}");
        Assert.True(
            MathF.Abs(both.Height) < 0.01f,
            $"size containment must use the zero contain-intrinsic block size: {both}");
    }

    [Fact]
    public void ContainerTypeEstablishesAFloatContainingFormattingContext()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #container{container-type:inline-size;width:200px}
                #float{float:left;width:50px;height:40px}
            </style>
            <div id="container"><div id="float"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 200f));
        Rect container = laid.Rects[Id(tree, "container")];
        Assert.True(
            MathF.Abs(container.Height - 40f) < 0.01f,
            $"query container must contain its descendant float: {container}");
    }

    [Fact]
    public void ConsecutivePercentageFloatsCollectAgainstTheFullBandWidth()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #container{box-sizing:border-box;width:780px}
                .cell{float:left;box-sizing:border-box;width:25%;padding:0 15px}
                #a{height:87px} #b{height:85px} #c{height:150px} #d{height:76px}
            </style>
            <div id="container">
              <div id="a" class="cell"></div>
              <div id="b" class="cell"></div>
              <div id="c" class="cell"></div>
              <div id="d" class="cell"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        string[] ids = ["a", "b", "c", "d"];
        for (int index = 0; index < ids.Length; index++)
        {
            Rect rect = laid.Rects[Id(tree, ids[index])];
            Assert.True(MathF.Abs(rect.Width - 195f) < 0.01f, $"{ids[index]}: {rect}");
            Assert.True(
                MathF.Abs(rect.Y) < 0.01f,
                $"four 25% floats must share the 780px band; {ids[index]} wrapped: {rect}");
            Assert.True(MathF.Abs(rect.X - (index * 195f)) < 0.01f, $"{ids[index]}: {rect}");
        }

        Rect container = laid.Rects[Id(tree, "container")];
        Assert.True(
            MathF.Abs(container.Height - 150f) < 0.01f,
            $"one float band must be as tall as its tallest float: {container}");
    }

    [Fact]
    public void SingleFloatKeepsNormalBlockOuterWidthAndShortensItsLineBand()
    {
        // Chromium 145 geometry for a Bootstrap-style single header float,
        // an ordinary full-width collapse block, and an overflow BFC below it.
        DomTree tree = Parse(
            """
            <style>
                *{box-sizing:border-box} html,body{margin:0}
                #parent{width:780px}
                #parent::before,#parent::after{display:table;content:" "}
                #parent::after{clear:both}
                #float{float:left;height:100px}
                #float::after{display:table;clear:both;content:" "}
                #brand{float:left;width:250px;height:100px}
                #flow{height:50px}
                #line{display:inline-block;width:400px;height:20px}
                #bfc{overflow:hidden;height:30px}
            </style>
            <div id="parent">
              <div id="float"><div id="brand"></div></div>
              <div id="flow"><span id="line"></span></div>
              <div id="bfc"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect parent = Get("parent");
        Rect floated = Get("float");
        Rect flow = Get("flow");
        Rect line = Get("line");
        Rect bfc = Get("bfc");

        Assert.Equal((0f, 0f, 780f, 100f), (parent.X, parent.Y, parent.Width, parent.Height));
        Assert.Equal((0f, 0f, 250f, 100f), (floated.X, floated.Y, floated.Width, floated.Height));
        Assert.Equal((0f, 0f, 780f, 50f), (flow.X, flow.Y, flow.Width, flow.Height));
        Assert.Equal((250f, 0f, 400f, 20f), (line.X, line.Y, line.Width, line.Height));
        Assert.Equal((250f, 50f, 530f, 30f), (bfc.X, bfc.Y, bfc.Width, bfc.Height));
    }

    [Fact]
    public void DisplayNoneFloatsDoNotHideALaterVisibleFloatFromTheBand()
    {
        // Bootstrap keeps its responsive toggle/cart floated even when a
        // desktop media query makes them display:none. Non-generated boxes
        // with display:none must not enter float selection or split the
        // visible brand out of its float band.
        DomTree tree = Parse(
            """
            <style>
                *{box-sizing:border-box} html,body{margin:0}
                #container{width:780px}
                #header{float:left;width:780px;height:100px}
                #header::before,#header::after{display:table;content:" "}
                #header::after{clear:both}
                .hidden-float{display:none;float:right}
                #brand{float:left;width:208px;height:100px;margin-right:33px}
                #flow{height:50px}
                #line{display:inline-block;width:400px;height:20px}
            </style>
            <div id="container">
              <div id="header">
                <button class="hidden-float">menu</button>
                <a id="brand"></a>
                <div id="flow"><span id="line"></span></div>
                <span class="hidden-float">cart</span>
              </div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect header = Get("header");
        Rect brand = Get("brand");
        Rect flow = Get("flow");
        Rect line = Get("line");

        Assert.Equal((0f, 0f, 208f, 100f), (brand.X, brand.Y, brand.Width, brand.Height));
        Assert.Equal((0f, 0f, 780f, 50f), (flow.X, flow.Y, flow.Width, flow.Height));
        Assert.Equal((241f, 0f, 400f, 20f), (line.X, line.Y, line.Width, line.Height));
        Assert.Equal((780f, 100f), (header.Width, header.Height));
    }

    [Fact]
    public void HtmlCommentsDoNotSplitNestedNativeFloatBands()
    {
        // HTML comments generate no box. Bootstrap emits descriptive comments
        // between its floated navbar header, ordinary full-width collapse
        // block, and the floats inside that block. Gecko's per-BFC float
        // manager ignores those nodes completely.
        DomTree tree = Parse(
            """
            <style>
                *{box-sizing:border-box} html,body{margin:0}
                #parent{width:780px;height:100px}
                #parent::before,#parent::after,#collapse::before,#collapse::after{
                    display:table;content:" "
                }
                #parent::after,#collapse::after{clear:both}
                #header{float:left;width:250px;height:100px}
                #collapse{position:relative;padding:0 15px}
                #nav{float:left;width:400px;height:50px;margin-top:20px}
                #right{float:right;width:80px;height:50px;margin-top:20px}
            </style>
            <div id="parent">
              <!-- floated brand wrapper -->
              <div id="header"></div><!-- /.navbar-header -->
              <div id="collapse">
                <!-- desktop navigation -->
                <div id="nav"></div><!-- /.navbar-nav -->
                <div id="right"></div>
              </div><!-- /.navbar-collapse -->
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect collapse = Get("collapse");
        Rect nav = Get("nav");
        Rect right = Get("right");

        Assert.Equal(
            (0f, 0f, 780f, 100f),
            (collapse.X, collapse.Y, collapse.Width, collapse.Height));
        Assert.Equal((250f, 20f, 400f, 50f), (nav.X, nav.Y, nav.Width, nav.Height));
        Assert.Equal((685f, 20f, 80f, 50f), (right.X, right.Y, right.Width, right.Height));
    }

    [Fact]
    public void AutoWidthFloatUsesItsOwnBfcForNestedFloatMaxContent()
    {
        // A float establishes a BFC. Its auto inline size must therefore be
        // measured from its nested float's max-content line, not through the
        // cyclic width:100% synthetic row used for non-native float zones.
        DomTree tree = Parse(
            """
            <style>
                *{box-sizing:border-box} html,body{margin:0}
                #container{width:780px}
                #container::before,#container::after,
                #header::before,#header::after{display:table;content:" "}
                #container::after,#header::after{clear:both}
                #header{float:left;height:100px}
                #brand{float:left;height:100px;padding:15px;margin-right:33px}
                #logo{width:70px;height:70px}
                #label{font-size:34.5px;line-height:20px}
                #toggle{display:none;float:right;padding:14px;border:1px solid}
                .icon{display:block;width:22px;height:2px}
                #flow{height:50px}
            </style>
            <div id="container"><div id="header"><button id="toggle"><span class="icon"></span><span class="icon"></span><span class="icon"></span></button><a id="brand"><img id="logo"><span id="label">porkbun</span></a></div><div id="flow"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect header = Get("header");
        Rect brand = Get("brand");
        Rect logo = Get("logo");

        Assert.Equal((70f, 70f), (logo.Width, logo.Height));
        Assert.True(
            brand.Width > 220f,
            $"the nested float must aggregate the image and label on its max-content line: {brand}");
        Assert.True(
            header.Width > 255f && header.Width < 259f,
            $"the hidden 22px toggle icon must not cap the outer shrink-to-fit width: {header}");
        Assert.True(
            MathF.Abs(header.Width - brand.Width - 33f) < 0.01f,
            $"the outer auto-width float must include the nested float's margin box: header={header}, brand={brand}");
    }

    [Fact]
    public void IneligibleNearestInlineContainerDoesNotFallThrough()
    {
        DomTree tree = Parse(
            """
            <style>
                #outer{container-type:inline-size;width:500px}
                #inner{display:inline;container-type:inline-size}
                #target{width:1px}
                @container (min-width:400px){#target{width:99px}}
            </style>
            <div id="outer"><span id="inner"><span id="target"></span></span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 200f));
        NodeId target = Id(tree, "target");
        Assert.Equal(
            Dimension.Px(1f),
            laid.Styles[target].Width);
    }

    [Fact]
    public void IneligibleNearestDisplayContentsContainerDoesNotFallThrough()
    {
        DomTree tree = Parse(
            """
            <style>
                #outer{container:query/inline-size;width:500px}
                #inner{display:contents;container:query/inline-size}
                #target{width:1px}
                @container query (min-width:400px){#target{width:99px}}
            </style>
            <div id="outer"><div id="inner"><div id="target"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 200f));
        NodeId target = Id(tree, "target");
        Assert.Equal(Dimension.Px(1f), laid.Styles[target].Width);
    }

    [Fact]
    public void IneligibleNearestTableContainerDoesNotFallThrough()
    {
        DomTree tree = Parse(
            """
            <style>
                #outer{container-type:inline-size;width:500px}
                #inner{display:table;container-type:size;width:300px}
                #content{height:80px}
                #target{width:1px}
                @container (min-width:400px){#target{width:99px}}
            </style>
            <div id="outer">
              <div id="inner"><div id="content"><div id="target"></div></div></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 200f));
        NodeId inner = Id(tree, "inner");
        NodeId target = Id(tree, "target");
        Assert.True(
            laid.Styles[inner].IsTableBox,
            "the internal block approximation lost authored table provenance");
        Assert.Equal(Dimension.Px(1f), laid.Styles[target].Width);
        Assert.True(
            MathF.Abs(laid.Rects[inner].Height - 80f) < 0.01f,
            $"container-type must not apply size containment to an authored table box: {laid.Rects[inner]}");
    }

    [Fact]
    public void ContainerTypeDoesNotSizeContainInternalTableCell()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                table{border-spacing:0}
                #cell{container-type:size;padding:0}
                #content{height:80px;width:120px}
            </style>
            <table><tr><td id="cell"><div id="content"></div></td></tr></table>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 200f));
        NodeId cell = Id(tree, "cell");
        Assert.True(
            laid.Styles[cell].InternalFlexContainer,
            "the test must exercise the internal table-cell representation");
        Assert.True(
            laid.Rects[cell].Height >= 79.9f,
            $"container-type must not zero an internal table cell: {laid.Rects[cell]}");
    }

    [Fact]
    public void FormerlyOscillatingInlineContainerSettlesConsistently()
    {
        DomTree tree = Parse(
            """
            <style>
                #container{display:inline-block;container-type:inline-size}
                #child{width:200px;height:10px}
                @container (min-width:100px){#child{width:50px}}
            </style>
            <div id="container"><div id="child"></div></div>
            """);
        (DomLayout laid, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsMeasured(
                tree,
                (600f, 200f),
                new Dictionary<NodeId, ReplacedIntrinsic>(),
                []);
        Rect container = laid.Rects[Id(tree, "container")];
        NodeId child = Id(tree, "child");
        Assert.True(MathF.Abs(container.Width) < 0.01f, $"{container}");
        Assert.Equal(Dimension.Px(200f), laid.Styles[child].Width);
        Assert.True(
            telemetry.Termination is ContainerLayoutTermination.GeometryStable
                or ContainerLayoutTermination.SignatureStable,
            $"{telemetry.Termination}");
    }

    [Fact]
    public void PassCapUsesVisibleConservativeFallbackWithoutPanicking()
    {
        DomTree tree = Parse(
            """
            <style>
                #c0{container:c0/inline-size;width:100px}
                #target{width:1px}
                @container c0 (min-width:100px){
                    #c1{container:c1/inline-size;width:100px}
                }
                @container c1 (min-width:100px){#target{width:99px}}
            </style>
            <div id="c0"><div id="c1"><div id="target"></div></div></div>
            """);
        (DomLayout laid, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree,
                (600f, 200f),
                new Dictionary<NodeId, ReplacedIntrinsic>(),
                [],
                2,
                null,
                null,
                []);
        NodeId target = Id(tree, "target");
        Assert.Equal(Dimension.Px(1f), laid.Styles[target].Width);
        Assert.Equal(ContainerLayoutTermination.PassCapFallback, telemetry.Termination);
        Assert.Equal(3, telemetry.Passes);
    }

    [Fact]
    public void TenLevelContainerActivationPropagatesWithoutFixedPassTruncation()
    {
        System.Text.StringBuilder css = new(
            "#c0{container:c0/inline-size;width:100px}#target{width:1px}");
        for (int level = 0; level < 10; level++)
        {
            css.Append(CultureInfo.InvariantCulture, $"@container c{level} (min-width:100px){{#c{level + 1}{{container:c{level + 1}/inline-size;width:100px}}}}");
        }

        css.Append("@container c10 (min-width:100px){#target{width:77px}}");
        System.Text.StringBuilder body = new();
        for (int level = 0; level <= 10; level++)
        {
            body.Append(CultureInfo.InvariantCulture, $"<div id=\"c{level}\">");
        }

        body.Append("<div id=\"target\"></div>");
        for (int level = 0; level <= 10; level++)
        {
            body.Append("</div>");
        }

        DomTree tree = Parse($"<style>{css}</style>{body}");
        (DomLayout laid, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsMeasured(
                tree,
                (800f, 600f),
                new Dictionary<NodeId, ReplacedIntrinsic>(),
                []);
        NodeId target = Id(tree, "target");
        Assert.Equal(Dimension.Px(77f), laid.Styles[target].Width);
        Assert.True(telemetry.Passes > 8, $"deep propagation unexpectedly truncated: {telemetry}");
        Assert.False(
            telemetry.Termination is ContainerLayoutTermination.PassCapFallback
                or ContainerLayoutTermination.OscillationFallback,
            $"{telemetry.Termination}");
    }

    [Fact]
    public void LayoutWithoutContainerQueriesKeepsOnePassFastPath()
    {
        DomTree tree = Parse(
            """
            <html><head><style>#target{width:25px}</style></head>
               <body><div id="target"></div></body></html>
            """);
        (_, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsMeasured(
                tree,
                (800f, 600f),
                new Dictionary<NodeId, ReplacedIntrinsic>(),
                []);
        Assert.Equal(
            new ContainerLayoutTelemetry(
                1,
                ContainerLayoutTermination.NoQueries,
                default,
                0,
                0,
                0),
            telemetry);
    }

    [Fact]
    public void DormantContainerQueriesWithoutAContainerKeepOnePassFastPath()
    {
        DomTree tree = Parse(
            """
            <html><head><style>
                #target{width:25px}
                @container (min-width: 1px){#target{width:99px}}
            </style></head><body><div id="target"></div></body></html>
            """);
        (DomLayout laid, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsMeasured(
                tree,
                (800f, 600f),
                new Dictionary<NodeId, ReplacedIntrinsic>(),
                []);
        NodeId target = Id(tree, "target");
        Assert.Equal(Dimension.Px(25f), laid.Styles[target].Width);
        Assert.Equal(
            new ContainerLayoutTelemetry(
                1,
                ContainerLayoutTermination.NoContainers,
                default,
                0,
                0,
                0),
            telemetry);
    }

    [Fact]
    public void RetainedAttributeStylesMatchForcedFullWithDormantContainerRules()
    {
        const string initialHtml = """
            <html><head><style>
            html,body{margin:0}
            .theme[data-theme=dark] .card{--card-width:42px;color:#123456}
            .card .child{width:var(--card-width,18px);height:2em;font-size:10px}
            .theme[data-theme=dark] .card::before{content:'dark';display:block;width:7px}
            .toggle[data-open=true] + .panel{--peer-width:31px}
            .panel .peer{width:var(--peer-width,13px);height:9px}
            .counter-scope{counter-reset:step}
            .counter-item{counter-increment:step;display:block;height:9px}
            .counter-item[data-double=true]{counter-increment:step 2}
            .counter-item::before{content:counter(step)}
            .clean{width:77px;height:11px}
            @container dormant (min-width:10px){.child{width:99px}}
        </style></head><body>
            <section id=theme class=theme data-theme=light><div id=card class=card><span id=child class=child></span></div></section>
            <div id=toggle class=toggle data-open=false></div><div class=panel><span id=peer class=peer></span></div>
            <section class=counter-scope><span id=counter-one class=counter-item></span><span id=counter-change class=counter-item data-double=false></span><span id=counter-after class=counter-item></span></section>
            <aside id=clean class=clean></aside>
            <div style="display:flex"><button id=control><span>button</span></button><div id=inherit style="display:inherit">x</div></div>
        </body></html>
        """;
        string finalHtml = initialHtml
            .Replace("data-theme=light", "data-theme=dark", StringComparison.Ordinal)
            .Replace("data-open=false", "data-open=true", StringComparison.Ordinal)
            .Replace("data-double=false", "data-double=true", StringComparison.Ordinal);
        DomTree tree = Parse(initialHtml);
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (300f, 200f), NoIntrinsic, [], cache);
        NodeId theme = Id(tree, "theme");
        NodeId toggle = Id(tree, "toggle");
        NodeId counterChange = Id(tree, "counter-change");
        tree.GetNode(theme)!.SetAttribute("data-theme", "dark");
        tree.GetNode(toggle)!.SetAttribute("data-open", "true");
        tree.GetNode(counterChange)!.SetAttribute("data-double", "true");
        RetainedStyleMutation[] mutations =
        [
            RetainedStyleMutation.From(
                new AttributeStyleMutation(theme, "data-theme", "light", "dark")),
            RetainedStyleMutation.From(
                new AttributeStyleMutation(toggle, "data-open", "false", "true")),
            RetainedStyleMutation.From(
                new AttributeStyleMutation(counterChange, "data-double", "false", "true")),
        ];
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree, (300f, 200f), NoIntrinsic, [], null, cache, retained, mutations);
        DomTree finalTree = Parse(finalHtml);
        DomLayout full = RenderDom.LayoutDom(finalTree, (300f, 200f));

        Assert.Equal(ContainerLayoutTermination.NoContainers, telemetry.Termination);
        Assert.True(telemetry.RetainedReused > 0, "clean branch must be reused");
        Assert.True(telemetry.RetainedFresh > 0, "dirty subtrees must be fresh");
        Assert.Equal(0, telemetry.RetainedFallback);
        foreach (string id in new[]
        {
            "card", "child", "peer", "counter-one", "counter-change", "counter-after",
            "clean", "control", "inherit",
        })
        {
            NodeId incrementalId = Id(tree, id);
            NodeId fullId = Id(finalTree, id);
            Rect? incrementalRect =
                incremental.Rects.TryGetValue(incrementalId, out Rect ir) ? ir : null;
            Rect? fullRect = full.Rects.TryGetValue(fullId, out Rect fr) ? fr : null;
            Assert.Equal(incrementalRect, fullRect);
            Assert.Equal(
                incremental.Styles[incrementalId].Width,
                full.Styles[fullId].Width);
            Assert.Equal(
                incremental.Styles[incrementalId].Color,
                full.Styles[fullId].Color);
            Assert.Equal(
                incremental.Styles[incrementalId].BeforeContent,
                full.Styles[fullId].BeforeContent);
            Assert.Equal(
                incremental.CustomProperties[incrementalId],
                full.CustomProperties[fullId]);
        }
    }

    [Fact]
    public void RetainedWaapiRestyleDirtiesOnlyTheNonInheritedEffectTarget()
    {
        DomTree tree = Parse(
            "<main><section id=target><div id=child><span id=grandchild></span></div></section><aside id=clean></aside></main>");
        NodeId target = Id(tree, "target");
        Stylesheet sheet = Stylesheet.ParseForViewport(tree, [], (800f, 600f));
        RetainedStylePlan plan = RetainedStylePlanner.Plan(
            tree,
            sheet,
            [new RetainedStyleMutation.WaapiAnimation(target)]);
        RetainedStylePlan.Reuse reuse = Assert.IsType<RetainedStylePlan.Reuse>(plan);
        Assert.Equal(new HashSet<NodeId> { target }, reuse.Dirty);
        Assert.True(reuse.HasAnimationDamage);
    }

    [Fact]
    public void RetainedAnimationRestyleReusesCleanBranchesAndMatchesFull()
    {
        System.Text.StringBuilder clean = new();
        for (int index = 0; index < 2_000; index++)
        {
            clean.Append(CultureInfo.InvariantCulture, $"<span class=clean data-index={index}><i></i></span>");
        }

        DomTree tree = Parse($$$"""
            <style>
                html,body{margin:0}
                @keyframes grow {
                    from { width:20px; color:#ff0000 }
                    to { width:120px; color:#0000ff }
                }
                #animated{height:20px;animation:grow 1000ms linear both}
                #animated > b{color:inherit}
                .clean{display:block;width:3px;height:1px}
            </style><main><section id=animated><b id=child>x</b></section>
            <aside>{{{clean}}}</aside></main>
            """);
        NodeId animated = Id(tree, "animated");
        StylesheetCache cache = new();
        AnimationTimelineState timeline = new();
        (DomLayout initial, _) = RenderDom.LayoutDomWithWebFontsPassLimitAtAnimationTime(
            tree,
            (800f, 600f),
            NoIntrinsic,
            [],
            null,
            cache,
            null,
            [],
            CssMediaType.Screen,
            AnimationSample.Document(0f),
            timeline);
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimitAtAnimationTime(
                tree,
                (800f, 600f),
                NoIntrinsic,
                [],
                null,
                cache,
                retained,
                [new RetainedStyleMutation.Animation(animated)],
                CssMediaType.Screen,
                AnimationSample.Document(500f),
                timeline);
        AnimationTimelineState fullTimeline = new();
        (DomLayout full, _) = RenderDom.LayoutDomWithWebFontsPassLimitAtAnimationTime(
            tree,
            (800f, 600f),
            NoIntrinsic,
            [],
            null,
            cache,
            null,
            [],
            CssMediaType.Screen,
            AnimationSample.Document(500f),
            fullTimeline);

        AssertComputedStylesMatch("animation retained-vs-full", incremental, full);
        AssertRectsMatch(incremental, full, "animation retained-vs-full");
        Assert.Equal(0, telemetry.RetainedFallback);
        Assert.True(telemetry.RetainedReused >= 4_000, $"{telemetry}");
        Assert.True(telemetry.RetainedFresh < 16, $"{telemetry}");
        Assert.True(
            MathF.Abs(incremental.Rects[animated].Width - 70f) < 0.1f,
            $"animated geometry did not advance: {incremental.Rects[animated]}");
        NodeId child = Id(tree, "child");
        Assert.Equal(new RgbaColor(128, 0, 128, 255), incremental.Styles[child].Color);
    }

    [Fact]
    public void RetainedInsertionsWithActiveContainerQueriesMatchForcedFull()
    {
        System.Text.StringBuilder clean = new();
        for (int index = 0; index < 200; index++)
        {
            clean.Append(CultureInfo.InvariantCulture, $"<i class=clean data-index={index}></i>");
        }

        DomTree tree = Parse($$$"""
            <!doctype html><style>
                #container{container-type:inline-size;width:200px}
                .item{width:11px;height:7px}
                @container (min-width:100px){.item{width:71px}}
                .clean{height:1px}
            </style><main><section id=container><div id=stable class=item></div>
                <div id=popup class=item><b id=first></b><b id=second></b></div>
            </section><aside>{{{clean}}}</aside></main>
            """);
        NodeId container = Id(tree, "container");
        NodeId popup = Id(tree, "popup");
        NodeId first = Id(tree, "first");
        NodeId second = Id(tree, "second");
        tree.RemoveChild(popup);
        tree.RemoveChild(first);
        tree.RemoveChild(second);

        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (500f, 300f), NoIntrinsic, [], cache);
        tree.AppendChild(container, popup);
        tree.AppendChild(popup, first);
        tree.AppendChild(popup, second);
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        RetainedStyleMutation[] mutations =
        [
            RetainedStyleMutation.From(new TreeStyleMutation.Insert(popup, null, container)),
            RetainedStyleMutation.From(new TreeStyleMutation.Insert(first, null, popup)),
            RetainedStyleMutation.From(new TreeStyleMutation.Insert(second, null, popup)),
        ];
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree, (500f, 300f), NoIntrinsic, [], null, cache, retained, mutations);
        DomLayout full = RenderDom.LayoutDom(tree, (500f, 300f));

        AssertComputedStylesMatch("active container insertion batch", incremental, full);
        AssertRectsMatch(incremental, full, "active container insertion batch");
        Assert.Equal(0, telemetry.RetainedFallback);
        Assert.True(telemetry.RetainedReused >= 200, $"{telemetry}");
        Assert.True(telemetry.RetainedFresh > 0, $"{telemetry}");
        Assert.Equal(Dimension.Px(71f), incremental.Styles[Id(tree, "stable")].Width);
        Assert.Equal(Dimension.Px(71f), incremental.Styles[popup].Width);
    }

    [Fact]
    public void RetainedDormantContainerQueriesDoNotDirtyLargeTree()
    {
        System.Text.StringBuilder peers = new();
        for (int index = 0; index < 1_000; index++)
        {
            peers.Append(CultureInfo.InvariantCulture, $"<i class=peer data-index={index}></i>");
        }

        DomTree tree = Parse($$$"""
            <!doctype html><style>
                .peer{height:1px}
                @container (min-width:1px){*{color:#123456}}
            </style><main><input id=subject>{{{peers}}}</main>
            """);
        NodeId subject = Id(tree, "subject");
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (500f, 300f), NoIntrinsic, [], cache);
        tree.GetNode(subject)!.SetAttribute("autocomplete", "off");
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (_, ContainerLayoutTelemetry telemetry) = RenderDom.LayoutDomWithWebFontsPassLimit(
            tree,
            (500f, 300f),
            NoIntrinsic,
            [],
            null,
            cache,
            retained,
            [RetainedStyleMutation.From(
                new AttributeStyleMutation(subject, "autocomplete", null, "off"))]);
        Assert.Equal(0, telemetry.RetainedFallback);
        Assert.Equal(0, telemetry.RetainedFresh);
        Assert.True(telemetry.RetainedReused >= 1_000, $"{telemetry}");
    }

    [Fact]
    public void RetainedNestedUniversalContainerResetIsBoundedAndMatchesFull()
    {
        System.Text.StringBuilder queried = new();
        System.Text.StringBuilder cleanNodes = new();
        for (int index = 0; index < 500; index++)
        {
            queried.Append(CultureInfo.InvariantCulture, $"<i class=item data-index={index}></i>");
            cleanNodes.Append(CultureInfo.InvariantCulture, $"<i class=clean data-index={index}></i>");
        }

        DomTree tree = Parse($$$"""
            <!doctype html><style>
                #outer,#inner{container-type:inline-size;width:200px}
                .item,.clean{height:1px}
                @container (min-width:100px){
                    *{color:#123456}
                    @container (min-width:100px){*{height:2px}}
                }
            </style><main><section id=outer><section id=inner>{{{queried}}}</section></section>
            <aside><input id=subject>{{{cleanNodes}}}</aside></main>
            """);
        NodeId subject = Id(tree, "subject");
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (800f, 600f), NoIntrinsic, [], cache);
        tree.GetNode(subject)!.SetAttribute("autocomplete", "off");
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree,
                (800f, 600f),
                NoIntrinsic,
                [],
                null,
                cache,
                retained,
                [RetainedStyleMutation.From(
                    new AttributeStyleMutation(subject, "autocomplete", null, "off"))]);
        DomLayout full = RenderDom.LayoutDom(tree, (800f, 600f));
        AssertComputedStylesMatch("nested universal CQ reset", incremental, full);
        AssertRectsMatch(incremental, full, "nested universal CQ reset");
        Assert.Equal(0, telemetry.RetainedFallback);
        Assert.True(telemetry.RetainedFresh < 530, $"{telemetry}");
        Assert.True(telemetry.RetainedReused >= 500, $"{telemetry}");
    }

    [Fact]
    public void RetainedAttributeStylesMatchForcedFullSelectorMatrix()
    {
        const RetainedDifferentialExpectation Incremental =
            RetainedDifferentialExpectation.Incremental;
        const RetainedDifferentialExpectation ReuseAll =
            RetainedDifferentialExpectation.ReuseAll;
        const RetainedDifferentialExpectation ConservativeFallback =
            RetainedDifferentialExpectation.ConservativeFallback;
        RetainedDifferentialCase[] cases =
        [
            new(
                "self attribute selector",
                ".subject[data-state=on]{width:41px;height:7px;color:#123456}",
                "subject", "data-state", "on", Incremental),
            new(
                "child and descendant selectors",
                ".subject[data-state=on]>.child .grand{width:43px;height:9px}",
                "subject", "data-state", "on", Incremental),
            new(
                "ancestor selector",
                ".scope[data-mode=on] .grand{width:47px;height:11px}",
                "scope", "data-mode", "on", Incremental),
            new(
                "adjacent sibling selector",
                ".subject[data-state=on]+.panel{width:53px;height:13px}",
                "subject", "data-state", "on", Incremental),
            new(
                "general sibling selector",
                ".subject[data-state=on]~.panel{width:59px;height:15px}",
                "subject", "data-state", "on", Incremental),
            new(
                "inheritance and custom property",
                ".scope[data-mode=on]{--leaf-width:61px;color:#234567;font-size:20px}.leaf{width:var(--leaf-width,17px);height:1em;color:inherit}",
                "scope", "data-mode", "on", Incremental),
            new(
                "generated before and after content",
                ".subject[data-state=on]::before{content:'before';display:block;width:5px}.subject[data-state=on]::after{content:'after';display:block;width:7px}",
                "subject", "data-state", "on", Incremental),
            new(
                "declaration attr dependency",
                ".subject::before{content:attr(data-state);display:block;width:11px}",
                "subject", "data-state", "on", Incremental),
            new(
                "inline style and inherited custom property",
                ".subject .grand{width:var(--leaf-width,17px);color:inherit}",
                "subject", "style",
                "width:113px;height:29px;--leaf-width:37px;color:#345678", Incremental),
            new(
                "inline style attribute selector reaches sibling",
                ".subject[style]+.panel{width:127px;height:31px}",
                "subject", "style", "height:23px", Incremental),
            new(
                "counter propagation through retained siblings",
                ".scope{counter-reset:item}.subject,.panel{counter-increment:item}.subject[data-state=on]{counter-increment:item 3}.subject::before,.panel::before{content:counter(item)}",
                "subject", "data-state", "on", Incremental),
            new(
                "class replacement",
                ".subject.state-on .grand{width:67px;height:17px}",
                "subject", "class", "subject state-on", Incremental),
            new(
                "id replacement",
                "#subject-on .grand{width:71px;height:19px}",
                "subject", "id", "subject-on", Incremental),
            new(
                "selector-list pseudo",
                ".scope:is([data-mode=on],.alternate) .grand{width:73px;height:21px}",
                "scope", "data-mode", "on", Incremental),
            new(
                "negation pseudo",
                ".subject:not([data-state=off]){width:79px;height:23px}",
                "subject", "data-state", "on", Incremental),
            new(
                "mixed sibling descendant traversal",
                ".subject[data-state=on]~.panel .leaf{width:83px;height:25px}",
                "subject", "data-state", "on", ConservativeFallback),
            new(
                "relative selector ancestor traversal",
                ".scope:has(.subject[data-state=on]){width:89px}",
                "subject", "data-state", "on", ConservativeFallback),
            new(
                "nth of selector structural traversal",
                ".subject:nth-child(1 of [data-state=on]){width:97px}",
                "subject", "data-state", "on", ConservativeFallback),
            new(
                "unreferenced aria mutation",
                ".subject{width:101px;height:27px}",
                "subject", "aria-expanded", "true", ReuseAll),
            new(
                "unreferenced ordinary autocomplete mutation",
                ".subject{width:103px;height:27px}",
                "subject", "autocomplete", "off", ReuseAll),
            new(
                "ordinary autocomplete attribute selector",
                ".subject[autocomplete=off]{width:107px;height:29px}",
                "subject", "autocomplete", "off", Incremental),
            new(
                "ordinary autocomplete declaration attr dependency",
                ".subject::before{content:attr(autocomplete);display:block;width:9px}",
                "subject", "autocomplete", "off", Incremental),
            new(
                "mapped width presentation hint",
                ".subject{height:31px}",
                "subject", "width", "109", Incremental),
            new(
                "native input size mutation",
                "#control{height:23px}",
                "control", "size", "40", Incremental),
            new(
                "image resource mutation",
                "#image{width:17px;height:19px}",
                "image", "src", "replacement.png", ConservativeFallback),
            new(
                "inherited language semantic mutation",
                ".scope{width:113px}",
                "scope", "lang", "fr", Incremental),
            new(
                "inherited direction semantic mutation",
                ".scope{width:127px}",
                "scope", "dir", "rtl", Incremental),
            new(
                "functional language selector",
                ".scope:lang(fr){width:131px}",
                "scope", "lang", "fr", ConservativeFallback),
            new(
                "functional direction selector",
                ".scope:dir(rtl){width:137px}",
                "scope", "dir", "rtl", ConservativeFallback),
        ];

        foreach (RetainedDifferentialCase differentialCase in cases)
        {
            RunRetainedDifferentialCase(differentialCase);
        }
    }

    [Fact]
    public void RetainedUnreferencedOrdinaryAttributeKeepsLargeTreeClean()
    {
        System.Text.StringBuilder peers = new();
        for (int index = 0; index < 2_000; index++)
        {
            peers.Append(CultureInfo.InvariantCulture, $"<span class=peer data-index={index}></span>");
        }

        DomTree tree = Parse($$$"""
            <!doctype html><style>.subject{width:31px;height:7px}.peer{height:1px}</style>
               <main><input id=subject class=subject>{{{peers}}}</main>
            """);
        NodeId subject = Id(tree, "subject");
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (800f, 600f), NoIntrinsic, [], cache);
        tree.GetNode(subject)!.SetAttribute("autocomplete", "off");
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree,
                (800f, 600f),
                NoIntrinsic,
                [],
                null,
                cache,
                retained,
                [RetainedStyleMutation.From(
                    new AttributeStyleMutation(subject, "autocomplete", null, "off"))]);
        DomLayout full = RenderDom.LayoutDom(tree, (800f, 600f));

        AssertComputedStylesMatch("ordinary attribute 2k tree", incremental, full);
        AssertRectsMatch(incremental, full, "ordinary attribute 2k tree");
        Assert.Equal(0, telemetry.RetainedFallback);
        Assert.Equal(0, telemetry.RetainedFresh);
        Assert.True(telemetry.RetainedReused >= 2_000, $"{telemetry}");
    }

    [Fact]
    public void RetainedTreeStylesMatchForcedFullMutationMatrix()
    {
        const RetainedDifferentialExpectation Incremental =
            RetainedDifferentialExpectation.Incremental;
        const RetainedDifferentialExpectation ConservativeFallback =
            RetainedDifferentialExpectation.ConservativeFallback;

        // A fresh insertion must cascade the inserted subtree and every
        // structurally affected sibling while retaining an unrelated branch.
        DomTree tree = Parse(
            """
            <style>
                .list{counter-reset:item}.item{counter-increment:item;height:9px}
                .item::before{content:counter(item)}
                .item:nth-child(2){width:83px}.item:first-child{color:#123456}
                .list:empty + .after{height:77px}.clean{width:91px;height:13px}
            </style><main><section id="list" class="list">
                <i id="first" class="item"></i><i id="insert" class="item"></i><i id="last" class="item"></i>
            </section><div class="after"></div><aside class="clean"></aside></main>
            """);
        NodeId list = Id(tree, "list");
        NodeId insert = Id(tree, "insert");
        NodeId last = Id(tree, "last");
        tree.RemoveChild(insert);
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.InsertBefore(last, insert);
        FinishRetainedTreeCase(
            "fresh insert with nth, counters, and pseudos",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Insert(insert, null, list),
            Incremental);

        // Removing a subtree must purge its now-detached style/custom-property
        // entries instead of growing the retained maps forever.
        tree = Parse(
            """
            <style>.row:nth-child(even){width:72px}.row::before{content:'x'}.clean{height:15px}</style>
               <main id="rows"><div class="row"></div><div id="removed" class="row"><b id="removed-child"></b></div><div class="row"></div></main><aside class="clean"></aside>
            """);
        NodeId rows = Id(tree, "rows");
        NodeId removed = Id(tree, "removed");
        NodeId removedChild = Id(tree, "removed-child");
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.RemoveChild(removed);
        DomLayout incremental = FinishRetainedTreeCase(
            "remove and purge detached subtree",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Remove(removed, rows),
            Incremental);
        Assert.False(incremental.Styles.ContainsKey(removed));
        Assert.False(incremental.Styles.ContainsKey(removedChild));
        Assert.False(incremental.CustomProperties.ContainsKey(removed));
        Assert.False(incremental.CustomProperties.ContainsKey(removedChild));

        // Character-data changes keep selector matching local but must rebuild
        // text shaping and parent geometry.
        tree = Parse(
            """<style>#label{display:block;width:120px}.clean{height:17px}</style><div id="label">a</div><aside class="clean"></aside>""");
        NodeId label = Id(tree, "label");
        NodeId text = tree.Children(label)[0];
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        ((TextData)tree.GetNode(text)!.Data).Contents = "a substantially longer replacement";
        FinishRetainedTreeCase(
            "text replacement",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Text(text, label),
            Incremental);

        // Keyed :has() rules do not poison unrelated mutations. A matching
        // insertion walks upward to the keyed anchor and retains clean peers.
        foreach ((string name, string insertedClass, RetainedDifferentialExpectation expectation)
            in new[]
            {
                ("unrelated keyed has insertion", "other", Incremental),
                ("matching keyed has insertion", "signal", Incremental),
            })
        {
            string html = $$"""
                <style>.host:has(.signal) .dependent{width:101px}.clean{height:19px}</style>
                   <section id="host" class="host"><div id="target"><i id="insert" class="{{insertedClass}}"></i></div><span class="dependent"></span></section><aside class="clean"></aside>
                """;
            DomTree keyedTree = Parse(html);
            NodeId target = Id(keyedTree, "target");
            NodeId keyedInsert = Id(keyedTree, "insert");
            keyedTree.RemoveChild(keyedInsert);
            StylesheetCache keyedCache = new();
            DomLayout keyedInitial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
                keyedTree, (360f, 260f), NoIntrinsic, [], keyedCache);
            keyedTree.AppendChild(target, keyedInsert);
            FinishRetainedTreeCase(
                name,
                keyedTree,
                keyedCache,
                keyedInitial,
                new TreeStyleMutation.Insert(keyedInsert, null, target),
                expectation);
        }

        foreach ((string name, string insertedTag, RetainedDifferentialExpectation expectation)
            in new[]
            {
                ("unrelated keyed tag has insertion", "i", Incremental),
                ("matching keyed tag has insertion", "span", Incremental),
            })
        {
            string html = $$"""
                <style>.host:has(> span) .dependent{width:103px}.clean{height:21px}</style>
                   <section id="host" class="host"><{{insertedTag}} id="insert"></{{insertedTag}}><div class="dependent"></div></section><aside class="clean"></aside>
                """;
            DomTree tagTree = Parse(html);
            NodeId target = Id(tagTree, "host");
            NodeId tagInsert = Id(tagTree, "insert");
            tagTree.RemoveChild(tagInsert);
            StylesheetCache tagCache = new();
            DomLayout tagInitial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
                tagTree, (360f, 260f), NoIntrinsic, [], tagCache);
            tagTree.AppendChild(target, tagInsert);
            FinishRetainedTreeCase(
                name,
                tagTree,
                tagCache,
                tagInitial,
                new TreeStyleMutation.Insert(tagInsert, null, target),
                expectation);
        }

        // Style text changes alter the cache key and must never reuse styles.
        tree = Parse(
            """<style id="sheet">.subject{width:20px}</style><div class="subject"></div>""");
        NodeId style = Id(tree, "sheet");
        NodeId styleText = tree.Children(style)[0];
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        ((TextData)tree.GetNode(styleText)!.Data).Contents = ".subject{width:140px}";
        FinishRetainedTreeCase(
            "style text fallback",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Text(styleText, style),
            ConservativeFallback);
    }

    [Fact]
    public void RetainedRelationalStylesMatchForcedFullDirectionMatrix()
    {
        const RetainedDifferentialExpectation Incremental =
            RetainedDifferentialExpectation.Incremental;
        const RetainedDifferentialExpectation ConservativeFallback =
            RetainedDifferentialExpectation.ConservativeFallback;

        // Descendant/child traversal and selector-list pseudos all invalidate
        // the keyed anchor without cascading an unrelated sibling branch.
        foreach ((string name, string relative) in new[]
        {
            ("has descendant insertion", ".signal"),
            ("has child insertion", "> .signal"),
            ("has is insertion", ":is(.signal,.alternate)"),
            ("has where insertion", ":where(.signal,.alternate)"),
        })
        {
            DomTree relTree = Parse($$"""
                <style>.host:has({{relative}}) .dependent{width:111px;height:7px}.clean{height:9px}</style>
                   <main><section id=host class=host><div id=target><i id=signal class=signal></i></div><span class=dependent></span></section><aside class=clean></aside></main>
                """);
            NodeId target = Id(relTree, "target");
            NodeId signal = Id(relTree, "signal");
            relTree.RemoveChild(signal);
            StylesheetCache relCache = new();
            DomLayout relInitial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
                relTree, (360f, 260f), NoIntrinsic, [], relCache);
            relTree.AppendChild(target, signal);
            FinishRetainedTreeCase(
                name,
                relTree,
                relCache,
                relInitial,
                new TreeStyleMutation.Insert(signal, null, target),
                Incremental);
        }

        // A relative sibling combinator searches toward earlier siblings of
        // the changed boundary. Both exact and general sibling paths retain a
        // clean branch.
        foreach ((string name, string combinator) in new[]
        {
            ("has adjacent sibling insertion", "+"),
            ("has general sibling insertion", "~"),
        })
        {
            DomTree sibTree = Parse($$"""
                <style>.host:has({{combinator}} .signal){width:113px;height:11px}.clean{height:13px}</style>
                   <main id=parent><section id=host class=host></section><i id=signal class=signal></i><aside id=clean class=clean></aside></main>
                """);
            NodeId parent = Id(sibTree, "parent");
            NodeId signal = Id(sibTree, "signal");
            NodeId clean = Id(sibTree, "clean");
            sibTree.RemoveChild(signal);
            StylesheetCache sibCache = new();
            DomLayout sibInitial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
                sibTree, (360f, 260f), NoIntrinsic, [], sibCache);
            sibTree.InsertBefore(clean, signal);
            FinishRetainedTreeCase(
                name,
                sibTree,
                sibCache,
                sibInitial,
                new TreeStyleMutation.Insert(signal, null, parent),
                Incremental);
        }

        // Inserting an unrelated node can break `+`, even though the inserted
        // subtree has none of the selector's indexed keys.
        DomTree tree = Parse(
            """
            <style>.host:has(> .left + .right){width:127px}.clean{height:15px}</style>
               <main><section id=host class=host><i class=left></i><b id=blocker></b><i id=right class=right></i></section><aside class=clean></aside></main>
            """);
        NodeId host = Id(tree, "host");
        NodeId blocker = Id(tree, "blocker");
        NodeId right = Id(tree, "right");
        tree.RemoveChild(blocker);
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.InsertBefore(right, blocker);
        FinishRetainedTreeCase(
            "has adjacent insertion side effect",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Insert(blocker, null, host),
            Incremental);

        // Structural matching changes even when the inserted node does not
        // carry the keyed relative subject class.
        tree = Parse(
            """
            <style>.host:has(> .signal:first-child){width:131px}.clean{height:17px}</style>
               <main><section id=host class=host><b id=blocker></b><i id=signal class=signal></i></section><aside class=clean></aside></main>
            """);
        host = Id(tree, "host");
        blocker = Id(tree, "blocker");
        NodeId structuralSignal = Id(tree, "signal");
        tree.RemoveChild(blocker);
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.InsertBefore(structuralSignal, blocker);
        FinishRetainedTreeCase(
            "has structural insertion side effect",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Insert(blocker, null, host),
            Incremental);

        // Character data can toggle :empty without changing any indexed key.
        tree = Parse(
            """
            <style>.host:has(.label:empty){width:133px}.clean{height:18px}</style>
               <main><section id=host class=host><span id=label class=label>x</span></section><aside class=clean></aside></main>
            """);
        NodeId emptyLabel = Id(tree, "label");
        NodeId emptyText = tree.Children(emptyLabel)[0];
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        ((TextData)tree.GetNode(emptyText)!.Data).Contents = string.Empty;
        FinishRetainedTreeCase(
            "has empty text side effect",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Text(emptyText, emptyLabel),
            Incremental);

        // The old sibling links are gone by the time removal invalidation
        // runs. Broadening each old ancestor boundary must cover both a keyed
        // removal and an adjacency match created by removing an interloper.
        foreach ((string name, string css, string removedId) in new[]
        {
            ("has descendant removal", ".host:has(.signal){width:137px}", "signal"),
            (
                "has adjacent removal side effect",
                ".host:has(> .left + .right){width:139px}",
                "blocker"),
        })
        {
            DomTree removalTree = Parse($$"""
                <style>{{css}}.clean{height:19px}</style><main><section id=host class=host><i class=left></i><b id=blocker></b><i class=right></i><i id=signal class=signal></i></section><aside class=clean></aside></main>
                """);
            NodeId removalHost = Id(removalTree, "host");
            NodeId removed = Id(removalTree, removedId);
            StylesheetCache removalCache = new();
            DomLayout removalInitial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
                removalTree, (360f, 260f), NoIntrinsic, [], removalCache);
            removalTree.RemoveChild(removed);
            FinishRetainedTreeCase(
                name,
                removalTree,
                removalCache,
                removalInitial,
                new TreeStyleMutation.Remove(removed, removalHost),
                Incremental);
        }

        // Detached nodes are mutable before the queued render flush. Removal
        // invalidation must not depend on selector keys still being present in
        // the post-mutation subtree.
        tree = Parse(
            """<style>.host:has(.signal){width:141px}.clean{height:20px}</style><main><section id=host class=host><i id=signal class=signal></i></section><aside class=clean></aside></main>""");
        host = Id(tree, "host");
        NodeId detachedSignal = Id(tree, "signal");
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.RemoveChild(detachedSignal);
        tree.GetNode(detachedSignal)!.SetAttribute("class", "detached-and-renamed");
        FinishRetainedTreeCase(
            "has removal after detached key mutation",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Remove(detachedSignal, host),
            Incremental);

        // Reparenting changes anchors in both old and new ancestor chains.
        tree = Parse(
            """
            <style>.host:has(> .signal){width:149px}.clean{height:21px}</style>
               <main><section id=old class=host><i id=signal class=signal></i></section><section id=new class=host></section><aside class=clean></aside></main>
            """);
        NodeId oldParent = Id(tree, "old");
        NodeId newParent = Id(tree, "new");
        NodeId reparented = Id(tree, "signal");
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.AppendChild(newParent, reparented);
        FinishRetainedTreeCase(
            "has reparent old and new anchors",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Insert(reparented, oldParent, newParent),
            Incremental);

        // A sibling-then-descendant path outside the anchor is deliberately
        // left on the correctness-first full path until dependency chains can
        // encode the continuation explicitly.
        tree = Parse(
            """<style>.host:has(.signal)~.panel .leaf{width:151px}</style><main><section id=host class=host><i id=signal class=signal></i></section><div class=panel><b class=leaf></b></div></main>""");
        host = Id(tree, "host");
        NodeId outerSignal = Id(tree, "signal");
        tree.RemoveChild(outerSignal);
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.AppendChild(host, outerSignal);
        FinishRetainedTreeCase(
            "has unrepresentable outer mixed traversal",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Insert(outerSignal, null, host),
            ConservativeFallback);
    }

    [Fact]
    public void RetainedMixedOuterHasDependenciesMatchForcedFull()
    {
        const RetainedDifferentialExpectation Incremental =
            RetainedDifferentialExpectation.Incremental;

        // `:has()` owns its relative path, but structural state on the anchor
        // remains an ordinary tree dependency and must not be discarded just
        // because both occur in the same compiled rule.
        DomTree tree = Parse(
            """
            <style>.host:has(.signal):first-child .desc{width:173px}.clean{height:7px}</style>
               <main id=parent><b id=blocker></b><section id=host class=host><i class=signal></i><span class=desc></span></section><aside class=clean></aside></main>
            """);
        NodeId parent = Id(tree, "parent");
        NodeId blocker = Id(tree, "blocker");
        NodeId host = Id(tree, "host");
        tree.RemoveChild(blocker);
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.InsertBefore(host, blocker);
        FinishRetainedTreeCase(
            "outer structural state beside has",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Insert(blocker, null, parent),
            Incremental);

        // A freshly inserted anchor at position zero can create both adjacent
        // and general sibling matches outside `:has()`.
        tree = Parse(
            """
            <style>
                 .left:has(.signal) + .adjacent{width:179px}
                 .left:has(.signal) ~ .following{height:31px}
                 .clean{height:9px}
               </style><main id=parent><section id=left class=left><i class=signal></i></section><b id=adjacent class=adjacent></b><b class=following></b><aside class=clean></aside></main>
            """);
        parent = Id(tree, "parent");
        NodeId left = Id(tree, "left");
        NodeId adjacent = Id(tree, "adjacent");
        tree.RemoveChild(left);
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.InsertBefore(adjacent, left);
        FinishRetainedTreeCase(
            "outer sibling paths beside has",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Insert(left, null, parent),
            Incremental);

        // Text can toggle an outer :empty while the relative `:has()` path is
        // entirely sibling-based and therefore has no text side effect.
        tree = Parse(
            """
            <style>.host:has(~ .peer):empty ~ .panel{width:181px}.clean{height:11px}</style>
               <main><section id=host class=host></section><i class=peer></i><b class=panel></b><aside class=clean></aside></main>
            """);
        host = Id(tree, "host");
        NodeId text = tree.NewNode(NodeData.Text(string.Empty));
        tree.AppendChild(host, text);
        cache = new StylesheetCache();
        initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        ((TextData)tree.GetNode(text)!.Data).Contents = "now non-empty";
        FinishRetainedTreeCase(
            "outer empty state beside has",
            tree,
            cache,
            initial,
            new TreeStyleMutation.Text(text, host),
            Incremental);
    }

    [Fact]
    public void RetainedBatchedInsertionsRestoreOldStructuralBoundaries()
    {
        const RetainedDifferentialExpectation Incremental =
            RetainedDifferentialExpectation.Incremental;

        DomTree tree = Parse(
            """
            <style>
                 .item:first-child{width:191px}.item:last-child{height:37px}
                 .item:only-child{color:#123456}
                 i:first-of-type{margin-left:13px}i:last-of-type{margin-right:17px}
                 i:only-of-type{padding-top:19px}.clean{height:13px}
               </style><main><section id=list><i id=before-a class=item></i><i id=before-b class=item></i><i id=old class=item></i><i id=after-a class=item></i><i id=after-b class=item></i></section><aside class=clean></aside></main>
            """);
        NodeId list = Id(tree, "list");
        NodeId old = Id(tree, "old");
        NodeId beforeA = Id(tree, "before-a");
        NodeId beforeB = Id(tree, "before-b");
        NodeId afterA = Id(tree, "after-a");
        NodeId afterB = Id(tree, "after-b");
        foreach (NodeId inserted in new[] { beforeA, beforeB, afterA, afterB })
        {
            tree.RemoveChild(inserted);
        }

        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (360f, 260f), NoIntrinsic, [], cache);
        tree.InsertBefore(old, beforeA);
        tree.InsertBefore(old, beforeB);
        tree.AppendChild(list, afterA);
        tree.AppendChild(list, afterB);
        TreeStyleMutation[] mutations =
        [
            .. new[] { beforeA, beforeB, afterA, afterB }
                .Select(node => new TreeStyleMutation.Insert(node, null, list)),
        ];
        FinishRetainedTreeBatchCase(
            "batched first last only boundaries",
            tree,
            cache,
            initial,
            mutations,
            Incremental);
    }

    [Fact]
    public void RetainedRelationalInvalidationKeepsTwoThousandNodePeerClean()
    {
        System.Text.StringBuilder clean = new();
        for (int index = 0; index < 2_000; index++)
        {
            clean.Append(CultureInfo.InvariantCulture, $"<span class=clean data-index={index}></span>");
        }

        DomTree tree = Parse($$$"""
            <style>.host:has(> .signal) .dependent{width:157px}.clean{height:1px}</style>
               <main><section id=host class=host><i id=signal class=signal></i><b class=dependent></b></section><aside>{{{clean}}}</aside></main>
            """);
        NodeId host = Id(tree, "host");
        NodeId signal = Id(tree, "signal");
        tree.RemoveChild(signal);
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (800f, 600f), NoIntrinsic, [], cache);
        tree.AppendChild(host, signal);
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree,
                (800f, 600f),
                NoIntrinsic,
                [],
                null,
                cache,
                retained,
                [RetainedStyleMutation.From(
                    new TreeStyleMutation.Insert(signal, null, host))]);
        DomLayout full = RenderDom.LayoutDom(tree, (800f, 600f));
        AssertComputedStylesMatch("relational 2k clean peer", incremental, full);
        AssertRectsMatch(incremental, full, "relational 2k clean peer");
        Assert.Equal(0, telemetry.RetainedFallback);
        Assert.True(telemetry.RetainedReused >= 2_000, $"{telemetry}");
        Assert.True(telemetry.RetainedFresh < 24, $"{telemetry}");
    }

    [Fact]
    public void RetainedTreeInvalidationKeepsLargeUnrelatedBranchClean()
    {
        System.Text.StringBuilder clean = new();
        for (int index = 0; index < 2_000; index++)
        {
            clean.Append(CultureInfo.InvariantCulture, $"<span class=clean data-index={index}></span>");
        }

        DomTree tree = Parse($$$"""
            <style>.item:nth-child(2){width:88px}.clean{height:1px}</style>
               <main><section id=list><i class=item></i><i id=insert class=item></i></section><aside>{{{clean}}}</aside></main>
            """);
        NodeId list = Id(tree, "list");
        NodeId insert = Id(tree, "insert");
        tree.RemoveChild(insert);
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (800f, 600f), NoIntrinsic, [], cache);
        tree.AppendChild(list, insert);
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree,
                (800f, 600f),
                NoIntrinsic,
                [],
                null,
                cache,
                retained,
                [RetainedStyleMutation.From(
                    new TreeStyleMutation.Insert(insert, null, list))]);
        DomLayout full = RenderDom.LayoutDom(tree, (800f, 600f));
        AssertComputedStylesMatch("large unrelated branch", incremental, full);
        AssertRectsMatch(incremental, full, "large unrelated branch");
        Assert.Equal(0, telemetry.RetainedFallback);
        Assert.True(telemetry.RetainedReused >= 2_000, $"{telemetry}");
        Assert.True(telemetry.RetainedFresh < 16, $"{telemetry}");
    }

    [Fact]
    public void RetainedKeyedStartInsertionKeepsLargeBodyBranchClean()
    {
        System.Text.StringBuilder clean = new();
        for (int index = 0; index < 2_000; index++)
        {
            clean.Append(CultureInfo.InvariantCulture, $"<span class=clean data-index={index}></span>");
        }

        DomTree tree = Parse($$$"""
            <!doctype html><html><head><style>
                   .item:nth-child(2){width:31px}
                   .left + .right{height:7px}
                   .early ~ .late{height:9px}
                   .maybe-empty:empty + .after{height:11px}
                   .clean{height:1px}
               </style></head><body><main id=stable>{{{clean}}}</main><i id=watcher data-scroll-watcher></i></body></html>
            """);
        NodeId body = tree.QuerySelector("body")
            ?? throw new InvalidOperationException("fixture body");
        NodeId stable = Id(tree, "stable");
        NodeId watcher = Id(tree, "watcher");
        tree.RemoveChild(watcher);
        StylesheetCache cache = new();
        DomLayout initial = RenderDom.LayoutDomWithWebFontsAndStylesheetCache(
            tree, (800f, 600f), NoIntrinsic, [], cache);
        tree.InsertBefore(stable, watcher);
        RetainedStyleMaps retained = initial.TakeRetainedStyleMaps();
        (DomLayout incremental, ContainerLayoutTelemetry telemetry) =
            RenderDom.LayoutDomWithWebFontsPassLimit(
                tree,
                (800f, 600f),
                NoIntrinsic,
                [],
                null,
                cache,
                retained,
                [RetainedStyleMutation.From(
                    new TreeStyleMutation.Insert(watcher, null, body))]);
        DomLayout full = RenderDom.LayoutDom(tree, (800f, 600f));
        AssertComputedStylesMatch("keyed start insertion large body branch", incremental, full);
        AssertRectsMatch(incremental, full, "keyed start insertion large body branch");
        Assert.Equal(0, telemetry.RetainedFallback);
        Assert.True(telemetry.RetainedReused >= 2_000, $"{telemetry}");
        Assert.True(telemetry.RetainedFresh < 16, $"{telemetry}");
    }

    [Fact]
    public void ContainerConvergenceClassifiesSelfConsistentStability()
    {
        Assert.Equal(
            ContainerLayoutTermination.GeometryStable,
            RenderDom.ContainerIterationTermination(true, "3", "2"));
        Assert.Equal(
            ContainerLayoutTermination.SignatureStable,
            RenderDom.ContainerIterationTermination(false, "3", "3"));
        Assert.Null(RenderDom.ContainerIterationTermination(false, "3", "2"));
        Assert.Equal(512, RenderDom.ContainerLayoutSafetyLimit);
    }

    [Fact]
    public void DeclarativeShadowStyleDoesNotLeakIntoDocumentCascade()
    {
        DomTree tree = Parse(
            """
            <style>
                 html, body { margin:0 }
                 .button { width:123px; height:20px }
               </style>
               <x-button>
                 <template shadowrootmode="open">
                   <style>.button { width:100% }</style>
                   <span class="button">shadow control</span>
                 </template>
               </x-button>
               <div id="light-button" class="button"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId button = Id(tree, "light-button");
        Rect rect = laid.Rects[button];

        Assert.True(MathF.Abs(rect.Width - 123f) < 0.1f, $"{rect}");
        Assert.True(MathF.Abs(rect.Height - 20f) < 0.1f, $"{rect}");
    }

    [Fact]
    public void MulticolBalancesAtomicBlocksLikeChromium()
    {
        // Chromium 140 geometry for this reduction is a 630x300 container,
        // 190px columns at x=0/220/440, and a 2/3/3 source-order partition.
        // Explicit heights keep this a layout-algorithm regression rather
        // than a font/raster comparison.
        DomTree tree = Parse(
            "<html><head><style>#columns{columns:1}@media (width >= 700px){#columns{columns:3}}</style></head>"
            + "<body style=\"margin:0\"><div id=\"columns\" style=\"column-gap:30px;width:630px\">"
            + "<div id=\"a\" style=\"height:180px;break-inside:avoid\"></div>"
            + "<div id=\"b\" style=\"height:120px;break-inside:avoid\"></div>"
            + "<div id=\"c\" style=\"height:60px;break-inside:avoid\"></div>"
            + "<div id=\"d\" style=\"height:150px;break-inside:avoid\"></div>"
            + "<div id=\"e\" style=\"height:90px;break-inside:avoid\"></div>"
            + "<div id=\"f\" style=\"height:120px;break-inside:avoid\"></div>"
            + "<div id=\"g\" style=\"height:80px;break-inside:avoid\"></div>"
            + "<div id=\"h\" style=\"height:100px;break-inside:avoid\"></div>"
            + "</div></body></html>");
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect columns = Get("columns");
        Assert.True(MathF.Abs(columns.Width - 630f) < 0.1f, $"{columns}");
        Assert.True(MathF.Abs(columns.Height - 300f) < 0.1f, $"{columns}");

        (string Id, float X, float Y, float Width, float Height)[] expected =
        [
            ("a", 0f, 0f, 190f, 180f),
            ("b", 0f, 180f, 190f, 120f),
            ("c", 220f, 0f, 190f, 60f),
            ("d", 220f, 60f, 190f, 150f),
            ("e", 220f, 210f, 190f, 90f),
            ("f", 440f, 0f, 190f, 120f),
            ("g", 440f, 120f, 190f, 80f),
            ("h", 440f, 200f, 190f, 100f),
        ];
        foreach ((string id, float x, float y, float width, float height) in expected)
        {
            Rect child = Get(id);
            Assert.True(MathF.Abs(child.X - columns.X - x) < 0.1f, $"{id}: {child}");
            Assert.True(MathF.Abs(child.Y - columns.Y - y) < 0.1f, $"{id}: {child}");
            Assert.True(MathF.Abs(child.Width - width) < 0.1f, $"{id}: {child}");
            Assert.True(MathF.Abs(child.Height - height) < 0.1f, $"{id}: {child}");
        }

        DomLayout narrow = RenderDom.LayoutDom(tree, (600f, 1000f));
        NodeId columnsId = Id(tree, "columns");
        NodeId cId = Id(tree, "c");
        Assert.Equal((ushort?)1, narrow.Styles[columnsId].ColumnCount);
        Assert.True(
            MathF.Abs(narrow.Rects[columnsId].Height - 900f) < 0.1f,
            $"{narrow.Rects[columnsId]}");
        Assert.True(
            MathF.Abs(narrow.Rects[cId].Y - narrow.Rects[columnsId].Y - 300f) < 0.1f,
            $"{narrow.Rects[cId]}");
    }

    [Fact]
    public void StaticPositionsUseFinalPostRepairGeometryWithoutPhantomOverflow()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #columns{columns:3;width:630px;column-gap:30px}
                #columns > div{height:100px;break-inside:avoid}
                #holder{height:20px}
                #outer{position:absolute;width:40px;height:40px}
                #wrapper{margin-top:10px;height:20px}
                #nested{position:absolute;width:5px;height:5px}
            </style>
            <div id="columns">
              <div></div><div></div><div></div>
              <div></div><div></div><div></div>
            </div>
            <div id="holder">
              <div id="outer"><div id="wrapper"><div id="nested"></div></div></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 400f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect columns = Get("columns");
        Rect holder = Get("holder");
        Rect outer = Get("outer");
        Rect wrapper = Get("wrapper");
        Rect nested = Get("nested");

        Assert.True(MathF.Abs(columns.Height - 200f) < 0.01f, $"{columns}");
        Assert.True(MathF.Abs(holder.Y - 200f) < 0.01f, $"{holder}");
        Assert.True(
            MathF.Abs(outer.Y - holder.Y) < 0.01f,
            $"outer static position was harvested before multicol repair: holder={holder} outer={outer}");
        Assert.True(
            MathF.Abs(nested.Y - wrapper.Y) < 0.01f,
            $"nested static-position candidate changed while reparenting its ancestor: wrapper={wrapper} nested={nested}");
        Assert.Equal(
            (800f, 400f),
            laid.ScrollingContentSize(tree, (800f, 400f)));
    }

    [Fact]
    public void MulticolDoesNotAtomizeBreakableProseBoxes()
    {
        DomTree tree = Parse(
            "<html><body style=\"margin:0\"><main id=\"columns\" style=\"columns:2;width:200px\">"
            + "<p id=\"first\" style=\"height:100px;margin:0\"></p>"
            + "<p id=\"second\" style=\"height:100px;margin:0\"></p>"
            + "</main></body></html>");
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 400f));
        Rect columns = laid.Rects[Id(tree, "columns")];
        Rect first = laid.Rects[Id(tree, "first")];
        Rect second = laid.Rects[Id(tree, "second")];
        Assert.True(MathF.Abs(columns.Height - 200f) < 0.1f, $"{columns}");
        Assert.True(MathF.Abs(first.Width - 200f) < 0.1f, $"{first}");
        Assert.True(MathF.Abs(second.X - first.X) < 0.1f, $"{second}");
        Assert.True(MathF.Abs(second.Y - first.Y - 100f) < 0.1f, $"{second}");
    }

    [Fact]
    public void DirectFlexTextIsOneWrappingAnonymousItem()
    {
        // Chromium 140: the pseudo is one flex item and the direct text run
        // is one anonymous flex item with a 276px inline formatting context.
        // The sentence wraps to three 20px lines; treating every word as an
        // outer flex item instead produces one overflowing 20px line.
        DomTree tree = Parse(
            "<style>"
            + "*{box-sizing:border-box}body{margin:0}"
            + "#quote{display:flex;gap:8px;width:300px;font:16px/20px 'Liberation Sans'}"
            + "#quote::before{content:'';display:block;width:16px;height:16px;flex-shrink:0}"
            + "</style>"
            + "<div id=\"quote\">This anonymous text item must wrap inside the remaining flex space instead of overflowing.</div>");
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 200f));
        Rect quote = laid.Rects[Id(tree, "quote")];
        Assert.True(MathF.Abs(quote.Width - 300f) < 0.1f, $"{quote}");
        Assert.True(MathF.Abs(quote.Height - 60f) < 0.1f, $"{quote}");
    }

    [Fact]
    public void AutoWidthColumnFlexTextUsesFitContentWidth()
    {
        // Chromium 145: the outer row leaves 533px beside the fixed 203px
        // sibling and 32px gap. The auto-width quote is fit-content, so its
        // direct anonymous text item reflows to five 24px lines. Measuring it
        // only at max-content instead produces a 2097px-wide, 24px-tall line.
        DomTree tree = Parse(
            """
            <style>
               * { box-sizing: border-box }
               body { margin: 0; font: 16px/24px "Liberation Sans" }
               #row { display: flex; gap: 32px; width: 768px }
               #column {
                 display: flex;
                 flex-direction: column;
                 align-items: flex-start;
               }
               #quote { display: flex; gap: 8px; margin: 0 }
               #quote::before {
                 content: "";
                 display: block;
                 flex-shrink: 0;
                 width: 16px;
                 height: 16px;
               }
               #logo { width: 203px; height: 128px; flex-shrink: 0 }
               </style>
               <section id="row">
                 <div id="column"><blockquote id="quote">MDN closely follows W3C standards which helps me keep up with important topics. It's a complete package as it caters to everything; complex APIs, new browser functionalities, and best practices. MDN serves as a truly valuable resource and continues to assist me in my everyday development.</blockquote></div>
                 <div id="logo"></div>
               </section>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect row = Get("row");
        Rect column = Get("column");
        Rect quote = Get("quote");
        Rect logo = Get("logo");
        Assert.True(MathF.Abs(row.Width - 768f) < 0.1f, $"{row}");
        Assert.True(MathF.Abs(row.Height - 128f) < 0.1f, $"{row}");
        Assert.True(MathF.Abs(column.Width - 533f) < 0.1f, $"{column}");
        Assert.True(MathF.Abs(quote.Width - 533f) < 0.1f, $"{quote}");
        Assert.True(MathF.Abs(quote.Height - 120f) < 0.1f, $"{quote}");
        Assert.True(MathF.Abs(logo.X - 565f) < 0.1f, $"{logo}");
        Assert.True(MathF.Abs(logo.Width - 203f) < 0.1f, $"{logo}");
    }

    [Fact]
    public void ColumnFlexFitContentPreservesUnbreakableMinContent()
    {
        // Chromium 145 keeps the unbreakable quote at 2561.25px and lets the
        // constrained row overflow. The synthetic fit-content cap must not
        // turn an intrinsic min-content floor into overflow-wrap:anywhere.
        string word = new('X', 240);
        DomTree tree = Parse($$$"""
            <style>
               * { box-sizing: border-box }
               body { margin: 0; font: 16px/24px "Liberation Sans" }
               #row { display: flex; gap: 32px; width: 768px }
               #column {
                 display: flex;
                 flex-direction: column;
                 align-items: flex-start;
               }
               #quote { display: flex; margin: 0 }
               #logo { width: 203px; height: 128px; flex-shrink: 0 }
               </style>
               <section id="row">
                 <div id="column"><blockquote id="quote">{{{word}}}</blockquote></div>
                 <div id="logo"></div>
               </section>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect column = Get("column");
        Rect quote = Get("quote");
        Rect logo = Get("logo");
        Assert.True(quote.Width > 2500f, $"{quote}");
        Assert.True(MathF.Abs(column.Width - quote.Width) < 0.1f, $"{column} {quote}");
        Assert.True(MathF.Abs(quote.Height - 24f) < 0.1f, $"{quote}");
        Assert.True(MathF.Abs(logo.X - quote.Width - 32f) < 0.1f, $"{logo}");
    }

    [Fact]
    public void FinalFlexReflowResolvesNestedPercentageAndFitContentWidths()
    {
        // A modern application shell commonly makes its page a 100%-wide item
        // in an outer navigation row, then nests full-width sections, oversized
        // sliding strips, calc()-sized artwork, and shrink-wrapped controls.
        // Intrinsic flex measurement may treat those percentages as cyclic,
        // but the final item reflow has a definite 800px inline size.
        DomTree tree = Parse(
            """
            <style>
               html, body { margin:0; font-size:16px }
               * { box-sizing:border-box }
               #shell { display:flex; width:800px }
               #app { width:100% }
               #tabs { width:100%; overflow:hidden }
               #strip { width:400%; height:10px }
               #pattern { width:calc(100% - 7rem); height:10px }
               #pattern-inner { width:calc(100% - 1rem); height:10px }
               #banner-row { display:flex; width:100% }
               #banner {
                 display:flex; flex-wrap:wrap; gap:8px;
                 width:fit-content; max-width:100%;
                 padding:10px; border:1px solid
               }
               #banner-a { width:120px; height:20px; flex-shrink:0 }
               #banner-b { width:140px; height:20px; flex-shrink:0 }
               </style>
               <main id="shell"><div id="app">
                 <section id="tabs"><div id="strip"></div></section>
                 <div id="pattern"><div id="pattern-inner"></div></div>
                 <div id="banner-row"><a id="banner">
                   <span id="banner-a"></span><span id="banner-b"></span>
                 </a></div>
               </div></main>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("shell").Width - 800f) < 0.01f, $"{Get("shell")}");
        Assert.True(MathF.Abs(Get("app").Width - 800f) < 0.01f, $"{Get("app")}");
        Assert.True(MathF.Abs(Get("tabs").Width - 800f) < 0.01f, $"{Get("tabs")}");
        Assert.True(MathF.Abs(Get("strip").Width - 3200f) < 0.01f, $"{Get("strip")}");
        Assert.True(MathF.Abs(Get("pattern").Width - 688f) < 0.01f, $"{Get("pattern")}");
        Assert.True(
            MathF.Abs(Get("pattern-inner").Width - 672f) < 0.01f, $"{Get("pattern-inner")}");
        Assert.True(MathF.Abs(Get("banner-row").Width - 800f) < 0.01f, $"{Get("banner-row")}");
        Assert.True(MathF.Abs(Get("banner").Width - 290f) < 0.01f, $"{Get("banner")}");

        Assert.Equal(Dimension.Percent(4f), laid.Styles[Id(tree, "strip")].Width);
        Assert.Equal(Dimension.Percent(1f), laid.Styles[Id(tree, "banner")].MaxWidth);
    }

    [Fact]
    public void CursorAndPointerEventsInheritDownTheTree()
    {
        DomTree tree = Parse(
            """
            <style>
               html, body { margin:0 }
               #clickable { cursor:pointer }
               #inert { pointer-events:none }
               </style>
               <div id="clickable"><span id="inner">x</span></div>
               <div id="inert"><span id="inert-inner">y</span></div>
               <a id="link" href="#">z</a>
               <a id="anchor">w</a>
               <p id="plain">p</p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));

        Assert.Equal("pointer", laid.Styles[Id(tree, "clickable")].Cursor);
        Assert.Equal("pointer", laid.Styles[Id(tree, "inner")].Cursor);
        Assert.Equal("none", laid.Styles[Id(tree, "inert")].PointerEvents);
        Assert.Equal("none", laid.Styles[Id(tree, "inert-inner")].PointerEvents);

        // Chromium's UA sheet points at a link, and only at one that is a link.
        Assert.Equal("pointer", laid.Styles[Id(tree, "link")].Cursor);
        Assert.Equal("auto", laid.Styles[Id(tree, "anchor")].Cursor);
        Assert.Equal("auto", laid.Styles[Id(tree, "plain")].Cursor);
        Assert.Equal("auto", laid.Styles[Id(tree, "plain")].PointerEvents);
    }

    [Fact]
    public void FormControlsInheritThePageFontFamilyThroughTheAuthorRule()
    {
        // The UA sheet's `arial` on a control is right; the author sheet every reset carries
        // is what takes it back off, and the label inside the button follows the control.
        DomTree tree = Parse(
            """
            <style>
               html, body { margin:0; font-size:16px; font-family:"Page Face", sans-serif }
               .app input, .app select, .app button { font-family:inherit; font-size:inherit }
               </style>
               <div class="app"><button id="btn"><span id="label">Go</span></button></div>
               <button id="ua">Plain</button>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));

        Assert.Equal("\"page face\", sans-serif", laid.Styles[Id(tree, "btn")].FontFamily);
        Assert.Equal("\"page face\", sans-serif", laid.Styles[Id(tree, "label")].FontFamily);
        Assert.Equal(16f, laid.Styles[Id(tree, "btn")].FontSize);

        // Out of the author rule's reach the UA font is still what a control gets.
        Assert.Equal("arial", laid.Styles[Id(tree, "ua")].FontFamily);
    }

    [Fact]
    public void BaselineContentAlignmentUsesItsFallbackInsteadOfStretching()
    {
        // Baseline alignment does not apply to content distribution; `align-content: baseline`
        // falls back to `start`, so the auto rows of a taller-than-content grid keep their
        // content size rather than being stretched to fill it.
        DomTree tree = Parse(
            """
            <style>
               html, body { margin:0; font-size:16px }
               #grid { display:grid; grid-template-columns:100px 100px; row-gap:12px;
                       align-content:baseline; width:220px; height:400px }
               .cell { height:40px }
               #stretchy { display:grid; grid-template-columns:100px; row-gap:12px;
                           width:100px; height:400px }
               </style>
               <div id="grid">
                 <div id="a" class="cell"></div><div class="cell"></div>
                 <div id="b" class="cell"></div><div class="cell"></div>
               </div>
               <div id="stretchy"><div class="cell"></div><div id="c" class="cell"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 900f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("a").Height - 40f) < 0.01f, $"{Get("a")}");
        Assert.True(MathF.Abs(Get("b").Y - (Get("a").Y + 52f)) < 0.01f, $"{Get("b")}");

        // `align-content: normal` still spreads the same grid's rows over the extra space.
        Assert.True(Get("c").Y > 200f, $"{Get("c")}");
    }

    [Fact]
    public void HeightFitContentHugsContentInsteadOfStretching()
    {
        // `height: fit-content` sizes to content in the block axis exactly like `auto`, but it
        // is not an automatic size, so stretch alignment does not apply to it.
        DomTree tree = Parse(
            """
            <style>
               html, body { margin:0; font-size:16px }
               #row { display:flex; width:400px; height:200px }
               #card, #stretchy { width:100px }
               #card { height:fit-content }
               #grid { display:grid; width:400px; height:200px }
               #gcard { height:fit-content }
               .inner { height:40px }
               </style>
               <div id="row">
                 <div id="card"><div class="inner"></div></div>
                 <div id="stretchy"><div class="inner"></div></div>
               </div>
               <div id="grid"><div id="gcard"><div class="inner"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(laid.Styles[Id(tree, "card")].HeightFitContent);
        Assert.True(MathF.Abs(Get("card").Height - 40f) < 0.01f, $"{Get("card")}");
        Assert.True(MathF.Abs(Get("stretchy").Height - 200f) < 0.01f, $"{Get("stretchy")}");
        Assert.True(MathF.Abs(Get("gcard").Height - 40f) < 0.01f, $"{Get("gcard")}");

        // The card is still placed at the start of its flex line.
        Assert.True(MathF.Abs(Get("card").Y - Get("row").Y) < 0.01f, $"{Get("card")}");
    }

    [Fact]
    public void HeightFitContentRespectsExplicitCrossAxisAlignment()
    {
        // Only the `normal`/`stretch` alignment is replaced; an authored align-items still wins.
        DomTree tree = Parse(
            """
            <style>
               html, body { margin:0; font-size:16px }
               #row { display:flex; align-items:center; width:400px; height:200px }
               #card { width:100px; height:fit-content }
               .inner { height:40px }
               </style>
               <div id="row"><div id="card"><div class="inner"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 600f));
        Rect row = laid.Rects[Id(tree, "row")];
        Rect card = laid.Rects[Id(tree, "card")];

        Assert.True(MathF.Abs(card.Height - 40f) < 0.01f, $"{card}");
        Assert.True(MathF.Abs(card.Y - (row.Y + 80f)) < 0.01f, $"{card}");
    }

    /// <summary>
    /// The inline-axis content of these fixtures is three 100px inline blocks, so min-content
    /// is 100px and max-content 300px with no text measurement involved. Every expectation
    /// was read off Chromium 141 on the same markup.
    /// </summary>
    [Fact]
    public void MinAndMaxWidthResolveTheIntrinsicSizingKeywords()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0; font-size:16px }
              .chip { display:inline-block; width:100px; height:20px }
              #cb { width:200px }
              #narrow { width:50px }
              #wide { width:400px }
              #a { min-width:max-content }
              #b { max-width:min-content }
              #c { min-width:min-content }
              #d { max-width:fit-content }
            </style>
            <div id="cb">
              <div id="a"><span class="chip"></span><span class="chip"></span><span class="chip"></span></div>
              <div id="b"><span class="chip"></span><span class="chip"></span><span class="chip"></span></div>
            </div>
            <div id="narrow">
              <div id="c"><span class="chip"></span><span class="chip"></span><span class="chip"></span></div>
            </div>
            <div id="wide">
              <div id="d"><span class="chip"></span><span class="chip"></span><span class="chip"></span></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 900f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        // A min-width keyword grows the box past its containing block; a max-width one shrinks
        // it below. Neither was applied at all before: the declaration computed to `auto`.
        Assert.True(MathF.Abs(Get("a").Width - 300f) < 0.01f, $"{Get("a")}");
        Assert.True(MathF.Abs(Get("b").Width - 100f) < 0.01f, $"{Get("b")}");
        Assert.True(MathF.Abs(Get("c").Width - 100f) < 0.01f, $"{Get("c")}");
        Assert.True(MathF.Abs(Get("d").Width - 300f) < 0.01f, $"{Get("d")}");
    }

    [Fact]
    public void MinAndMaxHeightResolveTheIntrinsicSizingKeywords()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0; font-size:16px }
              .tall { height:150px }
              #cb { width:200px }
              #e { width:100px; height:40px; min-height:min-content; overflow:hidden }
              #f { width:100px; height:400px; max-height:max-content; overflow:hidden }
              #g { width:100px; height:400px; min-height:min-content; overflow:hidden }
              #h { width:100px; height:20px; padding:10px; box-sizing:border-box;
                   min-height:min-content; overflow:hidden }
              #i { width:100px; height:20px; padding:10px; box-sizing:content-box;
                   min-height:min-content; overflow:hidden }
            </style>
            <div id="cb">
              <div id="e"><div class="tall"></div></div>
              <div id="f"><div class="tall"></div></div>
              <div id="g"><div class="tall"></div></div>
              <div id="h"><div class="tall"></div></div>
              <div id="i"><div class="tall"></div></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 900f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("e").Height - 150f) < 0.01f, $"{Get("e")}");
        Assert.True(MathF.Abs(Get("f").Height - 150f) < 0.01f, $"{Get("f")}");

        // A min-height keyword still loses to a larger `height`.
        Assert.True(MathF.Abs(Get("g").Height - 400f) < 0.01f, $"{Get("g")}");

        // The resolved block size is a border-box measurement, so the padding is inside it
        // under `border-box` and outside it under `content-box`.
        Assert.True(MathF.Abs(Get("h").Height - 170f) < 0.01f, $"{Get("h")}");
        Assert.True(MathF.Abs(Get("i").Height - 170f) < 0.01f, $"{Get("i")}");
        Assert.True(MathF.Abs(Get("i").Width - 120f) < 0.01f, $"{Get("i")}");
    }

    [Fact]
    public void IntrinsicMinAndMaxSizesApplyToFlexGridAndOutOfFlowBoxes()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0; font-size:16px }
              .chip { display:inline-block; width:100px; height:20px }
              .tall { height:150px }
              #col { display:flex; flex-direction:column; width:200px; height:60px }
              #grid { display:grid; grid-template-columns:200px; width:200px; height:60px }
              #abs { position:relative; width:200px; height:60px }
              #ci { width:100px; min-height:min-content; overflow:hidden }
              #gi { min-width:max-content }
              #ai { position:absolute; left:0; top:0; min-width:max-content }
            </style>
            <div id="col"><div id="ci"><div class="tall"></div></div></div>
            <div id="grid">
              <div id="gi"><span class="chip"></span><span class="chip"></span><span class="chip"></span></div>
            </div>
            <div id="abs">
              <div id="ai"><span class="chip"></span><span class="chip"></span><span class="chip"></span></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 900f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        // Without the min-height the column item shrinks to the container's 60px.
        Assert.True(MathF.Abs(Get("ci").Height - 150f) < 0.01f, $"{Get("ci")}");
        Assert.True(MathF.Abs(Get("gi").Width - 300f) < 0.01f, $"{Get("gi")}");
        Assert.True(MathF.Abs(Get("ai").Width - 300f) < 0.01f, $"{Get("ai")}");
    }

    [Fact]
    public void CalcPercentagesResolveAgainstAResizableFlexItemsUsedWidth()
    {
        // A row flex item's declared inline size is not its used inline size once
        // `flex-grow` or the default `flex-shrink` lets the flex algorithm move it.
        // Tesserae's page shell is `width:1px; min-width:0; flex-grow:1`, so resolving
        // a descendant `calc(100% - 4px)` against the 1px declaration collapsed every
        // card in the page body to zero width. Chromium gives 1022px and 596px.
        DomTree grow = Parse(
            """
            <style>
              * { box-sizing:border-box; margin:0 }
              #row { display:flex; width:1280px }
              #side { width:250px; flex:0 0 auto }
              #grow { width:1px; min-width:0; flex-grow:1 }
              #pad { padding:2px }
              #calc { width:calc(100% - 4px); height:10px }
            </style>
            <div id="row">
              <div id="side"></div>
              <div id="grow"><div id="pad"><div id="calc"></div></div></div>
            </div>
            """);
        DomLayout grown = RenderDom.LayoutDom(grow, (1280f, 600f));
        Assert.True(
            MathF.Abs(grown.Rects[Id(grow, "grow")].Width - 1030f) < 0.01f,
            $"{grown.Rects[Id(grow, "grow")]}");
        Assert.True(
            MathF.Abs(grown.Rects[Id(grow, "calc")].Width - 1022f) < 0.01f,
            "calc(100% - 4px) must sample the grown 1026px content box, not the 1px "
                + $"declaration: {grown.Rects[Id(grow, "calc")]}");

        DomTree shrink = Parse(
            """
            <style>
              * { box-sizing:border-box; margin:0 }
              #row { display:flex; width:600px }
              #item { width:1200px; min-width:0 }
              #calc { width:calc(100% - 4px); height:10px }
            </style>
            <div id="row"><div id="item"><div id="calc"></div></div></div>
            """);
        DomLayout shrunk = RenderDom.LayoutDom(shrink, (600f, 600f));
        Assert.True(
            MathF.Abs(shrunk.Rects[Id(shrink, "item")].Width - 600f) < 0.01f,
            $"{shrunk.Rects[Id(shrink, "item")]}");
        Assert.True(
            MathF.Abs(shrunk.Rects[Id(shrink, "calc")].Width - 596f) < 0.01f,
            $"a shrunk flex item's used width is the percentage basis: {shrunk.Rects[Id(shrink, "calc")]}");
    }

    [Fact]
    public void FunctionalBlockSizesResolveAgainstTheContainingBlockNotTheViewport()
    {
        // DEVIATION from the Rust reference, which uses the viewport height as the
        // percentage basis for every functional block-axis size. Chromium resolves a
        // block-axis percentage against the containing block's content-box height, and
        // treats it as auto when that height is indefinite. Tesserae's `.tss-card` is
        // `height: calc(100% - 4px)` inside an auto-height parent, which the reference
        // sized to a full viewport instead of to its content.
        DomTree tree = Parse(
            """
            <style>
              * { box-sizing:border-box; margin:0 }
              body { width:800px }
              #autoh { }
              #a { height:calc(100% - 4px) }
              #b { height:100% }
              #fixed { height:300px }
              #c { height:calc(100% - 4px) }
              #d { height:50% }
            </style>
            <div id="autoh"><div id="a">a</div><div id="b">b</div></div>
            <div id="fixed"><div id="c">c</div><div id="d">d</div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 900f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        // Indefinite containing block: the calc behaves as auto, so both lines are one
        // 18px line box, exactly as the bare percentage next to them already was.
        Assert.True(MathF.Abs(Get("autoh").Height - 36f) < 0.01f, $"{Get("autoh")}");
        Assert.True(MathF.Abs(Get("a").Height - 18f) < 0.01f, $"{Get("a")}");
        Assert.True(MathF.Abs(Get("b").Height - 18f) < 0.01f, $"{Get("b")}");

        // Definite 300px containing block: the calc samples it, not the 900px viewport.
        Assert.True(MathF.Abs(Get("fixed").Height - 300f) < 0.01f, $"{Get("fixed")}");
        Assert.True(MathF.Abs(Get("c").Height - 296f) < 0.01f, $"{Get("c")}");
        Assert.True(MathF.Abs(Get("d").Height - 150f) < 0.01f, $"{Get("d")}");
    }

    [Fact]
    public void FunctionalInsetsResolveAgainstTheAbsolutePositioningContainingBlock()
    {
        // DEVIATION from the Rust reference, which flattens a functional inset against the
        // viewport height. Every expected value below was read off headless Chromium at
        // 1280x720 (Tesserae's dropdown chevron reduced to its CSS).
        DomTree tree = Parse(
            """
            <style>
              body{margin:0;font:13px sans-serif;height:2000px}
              :root{--tiny:10px}
              .ctr{position:relative;width:300px;height:34px;margin-top:200px}
              .k{position:absolute;width:10px;height:10px}
              #a{right:8px;top:calc(50% - var(--tiny) / 2)}
              #b{right:30px;top:50%}
              #c{right:52px;top:calc(50% - 5px)}
              #d{right:74px;top:calc(50% - var(--tiny))}
              #e{right:96px;bottom:50%}
              #f{right:118px;top:0;margin-top:calc(50% - 5px)}
            </style>
            <div class="ctr" id="ctr">
              <div class="k" id="a"></div><div class="k" id="b"></div><div class="k" id="c"></div>
              <div class="k" id="d"></div><div class="k" id="e"></div><div class="k" id="f"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        float container = laid.Rects[Id(tree, "ctr")].Y;
        float Offset(string id) => laid.Rects[Id(tree, id)].Y - container;

        // Half of the 34px containing block, not half of the 720px viewport.
        Assert.True(MathF.Abs(Offset("a") - 12f) < 0.01f, $"a: {Offset("a")}");
        Assert.True(MathF.Abs(Offset("c") - 12f) < 0.01f, $"c: {Offset("c")}");
        Assert.True(MathF.Abs(Offset("d") - 7f) < 0.01f, $"d: {Offset("d")}");

        // Tripwires: the bare percentages and the percentage margin were already right.
        Assert.True(MathF.Abs(Offset("b") - 17f) < 0.01f, $"b: {Offset("b")}");
        Assert.True(MathF.Abs(Offset("e") - 7f) < 0.01f, $"e: {Offset("e")}");
        Assert.True(MathF.Abs(Offset("f") - 145f) < 0.01f, $"f: {Offset("f")}");
    }

    [Fact]
    public void FunctionalInsetsSampleThePaddingBoxOfTheNearestPositionedAncestor()
    {
        // Chromium at 1280x720. The basis is the ancestor's padding box on both axes, the
        // ancestor need not be the parent, and an auto-height ancestor still has a used
        // height a percentage resolves against.
        DomTree tree = Parse(
            """
            <style>
              body{margin:0;font:13px sans-serif}
              .k{position:absolute;width:10px;height:10px}
              #p1{position:relative;width:300px;height:34px;padding:20px 30px}
              #g1{top:calc(50% - 5px);left:calc(50% - 5px)}
              #p2{position:relative;width:300px}
              #p2 .filler{height:60px}
              #g2{top:calc(50% - 5px)}
              #p3{position:relative;width:300px;height:80px}
              #p3 .mid{height:20px}
              #g3{top:calc(50% - 5px);left:calc(25% - 5px)}
              #p7{position:relative;width:300px;height:40px;border:5px solid #999}
              #g7{bottom:calc(50% - 5px);right:calc(50% - 5px)}
            </style>
            <div id="p1"><div class="k" id="g1"></div></div>
            <div id="p2"><div class="filler"></div><div class="k" id="g2"></div></div>
            <div id="p3"><div class="mid"><div class="k" id="g3"></div></div></div>
            <div id="p7"><div class="k" id="g7"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        float Top(string child, string ancestor) => Get(child).Y - Get(ancestor).Y;
        float Left(string child, string ancestor) => Get(child).X - Get(ancestor).X;

        // 360x74 padding box: 0.5*74 - 5 and 0.5*360 - 5.
        Assert.True(MathF.Abs(Top("g1", "p1") - 32f) < 0.01f, $"g1 top: {Get("g1")}");
        Assert.True(MathF.Abs(Left("g1", "p1") - 175f) < 0.01f, $"g1 left: {Get("g1")}");

        // The ancestor's height is content-derived (60px) and still the basis.
        Assert.True(MathF.Abs(Top("g2", "p2") - 25f) < 0.01f, $"g2: {Get("g2")}");

        // #p3, not the 20px-tall .mid the box is parented to.
        Assert.True(MathF.Abs(Top("g3", "p3") - 35f) < 0.01f, $"g3 top: {Get("g3")}");
        Assert.True(MathF.Abs(Left("g3", "p3") - 70f) < 0.01f, $"g3 left: {Get("g3")}");

        // `bottom` off a padding box inset by the 5px border: 5 + 40 - 15 - 10.
        Assert.True(MathF.Abs(Top("g7", "p7") - 20f) < 0.01f, $"g7 top: {Get("g7")}");
        Assert.True(MathF.Abs(Left("g7", "p7") - 150f) < 0.01f, $"g7 left: {Get("g7")}");
    }

    [Fact]
    public void FunctionalRelativeOffsetsResolveAgainstTheContainingBlockHeight()
    {
        // Chromium at 1280x720. A relative offset is not taffy's to resolve on the block
        // axis (it passes a hard 0 there), so it stays flattened - against the containing
        // block's content-box height, and as `auto` when that height is indefinite.
        DomTree tree = Parse(
            """
            <style>
              body{margin:0;font:13px sans-serif}
              .k{width:10px;height:10px}
              #p4{width:300px;height:100px}
              #g4{position:relative;top:calc(50% - 5px);left:calc(10% - 5px)}
              #p5{width:300px}
              #g5{position:relative;top:calc(50% - 5px)}
              #g5b{position:relative;top:50%}
            </style>
            <div id="p4"><div class="k" id="g4"></div></div>
            <div id="p5"><div class="k" id="g5"></div><div class="k" id="g5b"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        // Definite 100px containing block: half of it, not half of the 720px viewport.
        Assert.True(MathF.Abs(Get("g4").Y - (Get("p4").Y + 45f)) < 0.01f, $"g4: {Get("g4")}");
        Assert.True(MathF.Abs(Get("g4").X - (Get("p4").X + 25f)) < 0.01f, $"g4: {Get("g4")}");

        // Indefinite containing block: the offset computes to auto, exactly as the bare
        // percentage beside it does.
        Assert.True(MathF.Abs(Get("g5").Y - Get("p5").Y) < 0.01f, $"g5: {Get("g5")}");
        Assert.True(MathF.Abs(Get("g5b").Y - (Get("p5").Y + 10f)) < 0.01f, $"g5b: {Get("g5b")}");
    }

    [Fact]
    public void FunctionalInlineSizesSampleTheUsedContainingBlockWidth()
    {
        // Chromium at 1280x720. `#cb` is a flex item that ends 200px wide; the top-down
        // style pass's own block-flow estimate of its width is the 300px row, so a
        // `calc()` percentage flattened there came out 100px too wide.
        DomTree tree = Parse(
            """
            <style>
              body{margin:0;font:13px sans-serif}
              .row{display:flex;width:300px}
              .fixed{flex:0 0 100px;height:20px}
              .grow{flex:1 1 auto;min-width:0}
              #t{width:calc(100% + 20px);height:10px}
              #t2{width:calc(50% - 10px);height:10px}
            </style>
            <div class="row"><div class="fixed"></div>
              <div class="grow" id="cb"><div id="t"></div><div id="t2"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("cb").Width - 200f) < 0.01f, $"cb: {Get("cb")}");
        Assert.True(MathF.Abs(Get("t").Width - 220f) < 0.01f, $"t: {Get("t")}");
        Assert.True(MathF.Abs(Get("t2").Width - 90f) < 0.01f, $"t2: {Get("t2")}");
    }

    [Fact]
    public void ButtonsTakeTheUserAgentControlFontIncludingLineHeightNormal()
    {
        // DEVIATION from the Rust reference, whose `button` UA arm sets no font, so a button
        // inherits the page's font-size, family and line-height. Chromium gives every form
        // control `font: 400 13.3333px Arial`; being the shorthand it also resets line-height
        // to normal, which an author rule setting only font-size does not restore. With
        // Tesserae's inherited `line-height: 1.4` every button was two line-heights tall.
        DomTree tree = Parse(
            """
            <style>
              body { margin:0; font-family: Georgia, serif; font-size: 20px; line-height: 1.8 }
              .sized { font-size: 13px }
            </style>
            <div><button id="plain">Plain</button></div>
            <div><button id="sized" class="sized">Sized</button></div>
            <div><span id="ref">Reference</span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        LayoutStyle Style(string id) => laid.Styles[Id(tree, id)];

        Assert.True(MathF.Abs((Style("plain").FontSize ?? 0f) - 13.333333f) < 0.01f);
        Assert.Equal(LineHeight.Normal, Style("plain").LineHeight);

        // An author font-size wins; the UA line-height and family do not come back with it.
        Assert.True(MathF.Abs((Style("sized").FontSize ?? 0f) - 13f) < 0.01f);
        Assert.Equal(LineHeight.Normal, Style("sized").LineHeight);

        // Ordinary content still inherits the page font and its 1.8 line-height.
        Assert.True(MathF.Abs((Style("ref").FontSize ?? 0f) - 20f) < 0.01f);
        // 17px of line box plus the UA `border: 2px outset` top and bottom, which is
        // Chromium's 21px exactly.
        Assert.True(MathF.Abs(laid.Rects[Id(tree, "plain")].Height - 21f) < 1.01f);
        Assert.True(MathF.Abs(laid.Rects[Id(tree, "ref")].Height - 22f) < 1.01f);
    }

    [Fact]
    public void ButtonCarriesTheUserAgentOutsetBorder()
    {
        // Chromium's UA sheet gives `button` `border: 2px outset ButtonBorder`. This arm set
        // padding but no border, so every unstyled button was 4px narrower and 4px shorter
        // than Chromium's, and painted with no border at all.
        DomTree tree = Parse(
            """
            <style>html,body{margin:0}</style>
            <button id=plain>x</button>
            <button id=none style="border:0">x</button>
            <button id=author style="border:5px dashed red">x</button>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        LayoutStyle Style(string id) => laid.Styles[Id(tree, id)];

        Assert.Equal(new Edges(2f, 2f, 2f, 2f), Style("plain").Border);
        Assert.Equal(BorderStyle.Outset, Style("plain").BorderModel.Styles.Top);

        // A button's `ButtonBorder` computes to rgb(0, 0, 0) - Chromium 141 on a bare
        // `<button>Hi</button>` reports exactly that - and the rgb(118, 118, 118) Chromium
        // shows comes from its native form-control painter, not from this value. This arm
        // carried the painted colour for a while and made every button report a border-colour
        // Chromium does not. The grey is PaintBorders' job; see NativeControlAppearance.
        Assert.Equal(new RgbaColor(0, 0, 0, 255), Style("plain").BorderModel.Colors.Top);
        Assert.Equal(new RgbaColor(0, 0, 0, 255), Style("plain").BorderColor);
        Assert.True(Style("plain").NativeControlAppearance);

        // The UA border is a normal declaration, so an author rule still replaces it in
        // either direction.
        Assert.Equal(new Edges(0f, 0f, 0f, 0f), Style("none").Border);
        Assert.Equal(new Edges(5f, 5f, 5f, 5f), Style("author").Border);
        Assert.Equal(BorderStyle.Dashed, Style("author").BorderModel.Styles.Top);

        // Which is exactly the 4px each way that separated this port from Chromium.
        float plainWidth = laid.Rects[Id(tree, "plain")].Width;
        float noneWidth = laid.Rects[Id(tree, "none")].Width;
        Assert.True(MathF.Abs((plainWidth - noneWidth) - 4f) < 0.01f, $"{plainWidth} vs {noneWidth}");
        Assert.True(
            MathF.Abs(laid.Rects[Id(tree, "plain")].Height - laid.Rects[Id(tree, "none")].Height - 4f) < 0.01f);
    }

    [Fact]
    public void ButtonTakesTheWidestItemOfAColumnFlexChildNotTheirSum()
    {
        // The native-control sizing pass walks a button's subtree and accumulates every
        // descendant's contribution, which is a row accumulation: only right when the content
        // shares one line. A column flex container gives each item its own line, so the
        // button's contribution is the widest item. Summing them made a [51px, 164px] column
        // measure 215px of content instead of 164px, and the identical subtree under an
        // inline-block div (which goes through real intrinsic sizing, not the shortcut) was
        // already correct - that mismatch is the invariant this pins.
        DomTree tree = Parse(
            """
            <style>
             html,body{margin:0}
             .col{display:flex;flex-direction:column}
             .a{width:164px;height:20px}
             .b{width:51px;height:20px}
             .plainbox{display:inline-block;border:0;padding:0}
            </style>
            <button id=b1><div class=col><div class=b></div><div class=a></div></div></button>
            <button id=b2><div class=col><div class=a></div></div></button>
            <div class=plainbox id=d1><div class=col><div class=b></div><div class=a></div></div></div>
            <button id=b4 style="display:flex"><div class=col><div class=b></div><div class=a></div></div></button>
            <button id=b5><div class=a></div><div class=b></div></button>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1440f, 950f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // The plain inline-block is the oracle: max, not sum.
        Assert.True(MathF.Abs(Width("d1") - 164f) < 0.01f, $"inline-block: {Width("d1")}");

        // The button adds its UA `padding: 1px 6px` and its UA `border: 2px outset`, so
        // 164 + 12 + 4 = 180, which is what Chromium reports for all four. These read 176
        // while the UA arm still carried no border.
        Assert.True(MathF.Abs(Width("b1") - 180f) < 0.01f, $"column child: {Width("b1")}");
        Assert.True(MathF.Abs(Width("b2") - 180f) < 0.01f, $"single-item column: {Width("b2")}");
        Assert.True(
            MathF.Abs(Width("b4") - 180f) < 0.01f,
            $"display:flex button around the same column: {Width("b4")}");

        // Block-level children of the button itself stack the same way.
        Assert.True(MathF.Abs(Width("b5") - 180f) < 0.01f, $"block children: {Width("b5")}");
    }

    [Fact]
    public void ButtonStillSumsInlineLevelContentOnOneLine()
    {
        // The guard for the fix above: a row of inline-level children does share a line, so
        // their contributions still add up. This is the shape of every icon-plus-label button
        // (`<i style="width:12px"></i><span style="margin-left:10px">...</span>`).
        DomTree tree = Parse(
            """
            <style>
             html,body{margin:0}
             button{padding:0;border:0}
             .icon{display:inline-block;width:12px;height:12px}
             .gap{display:inline-block;width:30px;height:12px;margin-left:10px}
            </style>
            <button id=b><span class=icon></span><span class=gap></span></button>
            <button id=row style="display:flex"><div class=icon></div><div class=gap></div></button>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1440f, 950f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        Assert.True(MathF.Abs(Width("b") - 52f) < 0.01f, $"inline run: {Width("b")}");
        Assert.True(MathF.Abs(Width("row") - 52f) < 0.01f, $"flex row: {Width("row")}");
    }

    [Fact]
    public void WidthMaxContentAndMinContentSizeToTheirMeasurement()
    {
        // `fit-content` was the only intrinsic sizing keyword the width parse recognized; the
        // other two fell through to `auto` and filled the 1200px containing block.
        DomTree tree = Parse(
            """
            <style>
              html,body{margin:0}
              .box{padding:0 20px;border:1px solid #999;box-sizing:border-box}
              .wide{width:160px;height:20px}
              .narrow{width:50px;height:20px}
            </style>
            <div style="width:1200px">
              <div class=box id=mx style="display:flex;flex-direction:column;width:max-content">
                <div class=wide></div><div class=narrow></div>
              </div>
              <div class=box id=mn style="display:flex;flex-direction:column;width:min-content">
                <div class=wide></div><div class=narrow></div>
              </div>
              <div class=box id=rw style="display:flex;width:max-content">
                <div class=wide></div><div class=narrow></div>
              </div>
              <div class=box id=fc style="display:flex;flex-direction:column;width:fit-content">
                <div class=wide></div><div class=narrow></div>
              </div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1440f, 950f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // max(160, 50) + 40 padding + 2 border.
        Assert.True(MathF.Abs(Width("mx") - 202f) < 0.01f, $"column max-content: {Width("mx")}");
        Assert.True(MathF.Abs(Width("mn") - 202f) < 0.01f, $"column min-content: {Width("mn")}");

        // A row sums its items: 160 + 50 + 42.
        Assert.True(MathF.Abs(Width("rw") - 252f) < 0.01f, $"row max-content: {Width("rw")}");

        // fit-content is unchanged, and here clamps to the same max-content.
        Assert.True(MathF.Abs(Width("fc") - 202f) < 0.01f, $"column fit-content: {Width("fc")}");
    }

    [Fact]
    public void CyclicPercentageImageKeepsNaturalIntrinsicContribution()
    {
        // A `width:100%` image inside a content-sized flex item is a cyclic
        // percentage: intrinsic flex sizing cannot resolve it against the
        // item's not-yet-known width. Chrome treats that percentage as `auto`
        // for the intrinsic contribution, so the item measures to the image's
        // natural width. Zeroing the contribution instead collapsed the item,
        // the restored percentage then resolved against 0, and the decoded
        // image laid out 0x0 and never painted (#698).
        DomTree tree = Parse(
            """
            <style>
               html, body { margin:0; font-size:16px }
               * { box-sizing:border-box }
               #shell { display:flex; width:800px }
               #item { display:block }
               #item img { display:block; width:100%; height:auto; max-width:100% }
               </style>
               <main id="shell"><div id="item"><img id="art" src="hero.png"></div></main>
            """);
        NodeId art = Id(tree, "art");
        Dictionary<NodeId, (float Width, float Height)> intrinsic = new()
        {
            [art] = (100f, 50f),
        };
        DomLayout laid = RenderDom.LayoutDomWithImages(tree, (800f, 300f), intrinsic);
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(
            MathF.Abs(Get("item").Width - 100f) < 0.01f,
            $"a content-sized flex item must measure the image's natural width: {Get("item")}");
        Assert.True(
            MathF.Abs(Get("art").Width - 100f) < 0.01f
                && MathF.Abs(Get("art").Height - 50f) < 0.01f,
            $"the percentage image must resolve against the measured item width: {Get("art")}");
    }

    [Fact]
    public void ABoxedPercentageImageDoesNotFloatItsFlexItemToTheNaturalWidth()
    {
        // DEVIATION from the Rust reference, which floors a content-sized flex item at every
        // deferred image's natural width unconditionally (the #698 fix above). That is only
        // sound when the image can actually reach that size. Tesserae's inline labels wrap a
        // `width: 100%` SVG in a `width: 14px` span, and the reference lifted the whole 60px
        // label to the SVG's natural width. The definite ancestor caps the contribution, so
        // the natural floor must not apply through it.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0; font: 13px Arial, sans-serif }
              * { box-sizing:border-box }
              #row { display:flex; flex-direction:row; width:358px }
              #label { display:inline-flex; align-items:center; gap:6px; width:fit-content;
                       height:24px; padding:0 8px; border:1px solid }
              #mark { width:14px; height:14px; flex:0 0 auto; display:flex }
              #mark img { display:block; width:100%; height:100% }
            </style>
            <div id="row">
              <a id="label"><span id="mark"><img id="icon" src="icon.svg"></span><span>Box</span></a>
            </div>
            """);
        Dictionary<NodeId, (float Width, float Height)> intrinsic = new()
        {
            [Id(tree, "icon")] = (150f, 150f),
        };
        DomLayout laid = RenderDom.LayoutDomWithImages(tree, (800f, 300f), intrinsic);
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(
            Get("label").Width < 80f,
            "a 14px-boxed icon must not float the label to the image's 150px natural width: "
                + $"{Get("label")}");
        Assert.True(
            MathF.Abs(Get("mark").Width - 14f) < 0.01f, $"{Get("mark")}");
        Assert.True(
            MathF.Abs(Get("icon").Width - 14f) < 0.01f, $"{Get("icon")}");
    }

    [Fact]
    public void TheInheritKeywordCopiesTheParentsBoxSizeEvenThoughItIsNotInherited()
    {
        // DEVIATION from the Rust reference, which drops the CSS-wide keyword `inherit` on the
        // box-size properties. They are not inherited properties, so the keyword has to copy
        // the parent's computed value explicitly. Tesserae's annotated text editor sizes its
        // textarea with `min-height: inherit` off a per-instance container, and every editor
        // collapsed to one row (58px against Chromium's 160px).
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0; font: 14px Arial, sans-serif }
              #box  { min-height:160px; width:400px }
              #tall { box-sizing:border-box; width:100%; min-height:inherit }
              #wide { min-width:220px; display:inline-block }
              #wide-in { min-width:inherit; display:block }
              #cap  { max-width:300px }
              #cap-in { max-width:inherit; display:block }
            </style>
            <div id="box"><div id="tall">tall</div></div>
            <div id="wide"><div id="wide-in">in</div></div>
            <div id="cap"><div id="cap-in">in</div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 600f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("tall").Height - 160f) < 0.01f, $"{Get("tall")}");
        Assert.True(MathF.Abs(Get("wide-in").Width - 220f) < 0.01f, $"{Get("wide-in")}");
        Assert.True(MathF.Abs(Get("cap-in").Width - 300f) < 0.01f, $"{Get("cap-in")}");
    }

    [Fact]
    public void ADefiniteFlexBasisMakesAColumnItemsBlockSizeDefiniteForPercentages()
    {
        // DEVIATION from the Rust reference, which calls a box's block size definite only when
        // `height` itself is a length or percentage. CSS Flexbox 9.8 also makes a flex item's
        // main size definite when it has a definite flex basis in a container with a definite
        // main size, and Chromium resolves descendant percentage heights against it. Tesserae's
        // time-histogram bars are `height: 100%` inside a `flex: 1 1 120px` column item, so the
        // reference computed them to auto and every bar laid out 0px tall.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              * { box-sizing:border-box }
              #chart { display:flex; flex-direction:column; height:190px; width:400px }
              #bars { flex:1 1 120px; min-height:0; display:flex; align-items:flex-end;
                      padding:8px 0 4px; border-bottom:1px solid }
              #bar { flex:1 1 0; height:100% }
              #rest { flex:0 0 auto; height:70px }
            </style>
            <div id="chart">
              <div id="bars"><div id="bar"></div></div>
              <div id="rest"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("bars").Height - 120f) < 0.01f, $"{Get("bars")}");
        Assert.True(
            MathF.Abs(Get("bar").Height - 107f) < 0.01f,
            $"height:100% must resolve against the 120px flex basis minus its edges: {Get("bar")}");
    }

    /// <summary>
    /// DEVIATION from the Rust reference, which calls a box's block size definite only when
    /// `height` itself is a length or percentage, so a flex item sized by the flex algorithm
    /// is an indefinite containing block and every descendant `height: %` under it computes to
    /// `auto`. CSS Flexbox 9.8 makes a flex item's post-flexing MAIN size definite whenever the
    /// container's main size is definite. Tesserae nests a `height: 100%` column inside a
    /// `flex-grow: 1` item carrying no height of its own, so the reference collapsed the chain
    /// to 0 and the connect-apps grid clipped 1676px of cards into an 8px box.
    /// Chromium (Playwright, /opt/pw-browsers/chromium) lays every box in this fixture out
    /// 400px tall.
    /// </summary>
    [Fact]
    public void AColumnFlexItemsPostFlexHeightIsDefiniteForDescendantPercentages()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              * { box-sizing:border-box }
              #col { display:flex; flex-direction:column; height:400px; width:200px }
              #item { flex-grow:1 }
              #a { height:100% }
              #b { height:100% }
              #c { height:50% }
            </style>
            <div id="col">
              <div id="item"><div id="a"><div id="b"><div id="c"></div></div></div></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("item").Height - 400f) < 0.01f, $"{Get("item")}");
        Assert.True(MathF.Abs(Get("a").Height - 400f) < 0.01f, $"{Get("a")}");
        Assert.True(
            MathF.Abs(Get("b").Height - 400f) < 0.01f,
            $"the percentage must keep chaining past the first level: {Get("b")}");
        Assert.True(MathF.Abs(Get("c").Height - 200f) < 0.01f, $"{Get("c")}");
    }

    /// <summary>
    /// The other half of CSS Flexbox 9.8: a STRETCHED item's cross size is definite whenever
    /// the container's cross size is, which is the block axis for a row container. Chromium
    /// lays the stretched item and its `height: 100%` child out 400px tall.
    /// </summary>
    [Fact]
    public void AStretchedRowFlexItemsCrossSizeIsDefiniteForDescendantPercentages()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              * { box-sizing:border-box }
              #row { display:flex; flex-direction:row; height:400px; width:300px }
              #item { width:100px }
              #fill { height:100% }
            </style>
            <div id="row"><div id="item"><div id="fill"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("item").Height - 400f) < 0.01f, $"{Get("item")}");
        Assert.True(MathF.Abs(Get("fill").Height - 400f) < 0.01f, $"{Get("fill")}");
    }

    /// <summary>
    /// The negative cases, so a fix that simply calls every flex item definite cannot pass.
    /// Verified against Chromium (Playwright, /opt/pw-browsers/chromium): a row item that is
    /// not stretched (`align-items: flex-start`, or an auto block-axis margin) and an item in a
    /// container whose own block size is indefinite all lay their `height: 100%` child out 0px
    /// tall, because in none of those does the flex algorithm hand the item a definite height.
    /// </summary>
    [Fact]
    public void AnUnstretchedOrIndefinitelySizedFlexItemStaysAnIndefiniteContainingBlock()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              * { box-sizing:border-box }
              .row { display:flex; flex-direction:row; height:400px; width:300px }
              .item { width:100px }
              .filler { height:50px }
              .fill { height:100% }
              #auto-col { display:flex; flex-direction:column; width:200px }
            </style>
            <div class="row" style="align-items:flex-start">
              <div id="start-item" class="item">
                <div class="filler"></div><div id="start-fill" class="fill"></div></div></div>
            <div class="row">
              <div id="margin-item" class="item" style="margin-top:auto">
                <div class="filler"></div><div id="margin-fill" class="fill"></div></div></div>
            <div id="auto-col">
              <div id="auto-item" style="flex-grow:1">
                <div class="filler"></div><div id="auto-fill" class="fill"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 900f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("start-item").Height - 50f) < 0.01f, $"{Get("start-item")}");
        Assert.True(MathF.Abs(Get("start-fill").Height) < 0.01f, $"{Get("start-fill")}");
        Assert.True(MathF.Abs(Get("margin-item").Height - 50f) < 0.01f, $"{Get("margin-item")}");
        Assert.True(MathF.Abs(Get("margin-fill").Height) < 0.01f, $"{Get("margin-fill")}");
        Assert.True(MathF.Abs(Get("auto-item").Height - 50f) < 0.01f, $"{Get("auto-item")}");
        Assert.True(MathF.Abs(Get("auto-fill").Height) < 0.01f, $"{Get("auto-fill")}");
    }

    /// <summary>
    /// The chain the connect-apps panel actually builds, reduced from the live DOM. Its whole
    /// weight rests on `#content`: a `flex-grow: 1` item of a definite-height COLUMN container,
    /// carrying no height of its own. Once that is an indefinite containing block, the
    /// `height: 100%` on every box below it computes to `auto` in turn, and the grid ends up
    /// its padding box tall with `overflow: auto` clipping the cards. `#shell` and `#pane` are
    /// each 400px either way, because a row container stretches them - which is why the live
    /// symptom looked like a paint bug and has to be checked at the ANCESTORS.
    /// Chromium lays every box in this fixture out 400px tall and the grid 360px.
    /// </summary>
    [Fact]
    public void APercentageStackUnderAFlexGrownItemKeepsItsScrollableGridOpen()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              * { box-sizing:border-box }
              #modal { display:flex; flex-direction:column; height:400px; width:300px }
              #content { display:flex; flex-grow:1; width:100%; overflow:hidden auto }
              #shell { display:flex; flex-direction:row; width:100%; height:100%; min-height:0 }
              #pane { height:100%; min-height:0; width:1px; flex-grow:1 }
              #outer { display:flex; flex-direction:column; height:100%; min-height:0;
                       overflow:hidden auto }
              #hub { display:flex; flex-direction:column; height:100%; min-height:0 }
              #card { flex-grow:1; flex-shrink:0; height:10px; overflow:hidden }
              #defer { width:100%; height:100%; min-height:0 }
              #inner { display:flex; flex-direction:column; height:100%; min-height:0;
                       overflow:hidden auto }
              #grid { display:grid; grid-template-columns:1fr; overflow:auto; width:100% }
              #grid > div { height:60px }
            </style>
            <div id="modal"><div id="content"><div id="shell"><div id="pane"><div id="outer">
              <div id="hub"><div id="card"><div id="defer"><div id="inner">
                <div id="grid">
                  <div></div><div></div><div></div><div></div><div></div><div></div>
                </div>
              </div></div></div></div>
            </div></div></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        foreach (string id in new[] { "content", "shell", "pane", "outer", "hub", "card", "defer", "inner" })
        {
            Assert.True(MathF.Abs(Get(id).Height - 400f) < 0.01f, $"#{id} {Get(id)}");
        }

        Assert.True(
            MathF.Abs(Get("grid").Height - 360f) < 0.01f,
            $"the scrollable grid must keep its six 60px rows, not collapse: {Get("grid")}");
    }

    /// <summary>
    /// DEVIATION from vendored taffy (`compute/block.rs`), which falls back to
    /// `min_size.height` as the children's percentage basis when a block has no resolved
    /// height. `min-height: 0` is the initial value and carries no information about the used
    /// height, so the fallback gave every `height: %` child a basis of 0 and collapsed it.
    /// Chromium sizes the box from its content and resolves the percentage against that, so
    /// `#gc` is 437px tall, not 0.
    /// Verified with Chromium (Playwright, /opt/pw-browsers/chromium).
    /// </summary>
    [Fact]
    public void AZeroMinHeightIsNotAPercentageBasisForChildren()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              * { box-sizing:border-box }
              #col { display:flex; flex-direction:column; height:950px; width:1000px }
              #mid { height:100%; min-height:0 }
              #gc { height:100% }
              #k { height:437px }
            </style>
            <div id="col"><div id="item"><div id="mid"><div id="gc">
              <div id="k"></div>
            </div></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 950f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        foreach (string id in new[] { "item", "mid", "gc", "k" })
        {
            Assert.True(MathF.Abs(Get(id).Height - 437f) < 0.01f, $"#{id} {Get(id)}");
        }
    }

    /// <summary>
    /// The other side of the same rule: a POSITIVE `min-height` does say how tall the box will
    /// be, and Chromium resolves a child percentage against it. A fix that simply drops the
    /// `min_size.height` fallback makes `#gc` 437px here instead of 600px.
    /// </summary>
    [Fact]
    public void APositiveMinHeightStaysAPercentageBasisForChildren()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              * { box-sizing:border-box }
              #col { display:flex; flex-direction:column; height:950px; width:1000px }
              #mid { height:100%; min-height:600px }
              #gc { height:100% }
              #k { height:437px }
            </style>
            <div id="col"><div id="item"><div id="mid"><div id="gc">
              <div id="k"></div>
            </div></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 950f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        foreach (string id in new[] { "item", "mid", "gc" })
        {
            Assert.True(MathF.Abs(Get(id).Height - 600f) < 0.01f, $"#{id} {Get(id)}");
        }

        Assert.True(MathF.Abs(Get("k").Height - 437f) < 0.01f, $"{Get("k")}");
    }

    /// <summary>
    /// The chain `#/manage/configure/subscription` actually builds, reduced from the live DOM:
    /// a settings group is an auto-height item of the app's definite-height COLUMN container,
    /// and it stacks two `height: 100%; min-height: 0` boxes, the inner one an
    /// `overflow: hidden auto` scroller. The zero minimum handed the scroller a percentage
    /// basis of 0, so it came out its padding box tall and scrolled all 464px of content away -
    /// the route rendered blank while every leaf sat at the right position inside it.
    /// Chromium (Playwright, /opt/pw-browsers/chromium): 512 / 496 / 480, with the two rows at
    /// their own 27px and 437px.
    /// </summary>
    [Fact]
    public void ASettingsGroupsScrollableStackKeepsItsContentHeight()
    {
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              * { box-sizing:border-box }
              #app { display:flex; flex-direction:column; height:950px; width:1000px }
              #group { display:flex; flex-direction:column; width:100%; min-width:0;
                       padding:8px }
              #content { width:100%; min-width:0; height:100%; min-height:0; padding:8px }
              #stack { display:flex; flex-direction:column; width:100%; min-width:0;
                       height:100%; min-height:0; padding:8px; overflow:hidden auto }
              #head { height:27px }
              #body { height:437px }
            </style>
            <div id="app"><div id="group"><div id="content"><div id="stack">
              <div id="head"></div><div id="body"></div>
            </div></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 950f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("group").Height - 512f) < 0.01f, $"#group {Get("group")}");
        Assert.True(MathF.Abs(Get("content").Height - 496f) < 0.01f, $"#content {Get("content")}");
        Assert.True(
            MathF.Abs(Get("stack").Height - 480f) < 0.01f,
            $"the scroller must keep its content height, not clip it: {Get("stack")}");
        Assert.True(MathF.Abs(Get("head").Height - 27f) < 0.01f, $"#head {Get("head")}");
        Assert.True(MathF.Abs(Get("body").Height - 437f) < 0.01f, $"#body {Get("body")}");
    }

    [Fact]
    public void AutoFitRepetitionCountsAMathFunctionTrackMinimumAsFixed()
    {
        // DEVIATION from vendored taffy, and so from the Rust reference which shares it: taffy
        // counts only a bare length or percentage as a track's fixed component, so a math
        // function reads as intrinsic. An auto-repetition beside a non-fixed track invalidates
        // the whole template and the grid falls back to zero explicit tracks - one implicit
        // column with every item stacked. Tesserae's grids are
        // `repeat(auto-fit, minmax(min(160px, 100%), 1fr))`; Chromium lays out five 177px
        // columns in a 924px container and the reference laid out one 924px column.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0; font: 13px Arial, sans-serif }
              .g { width:924px; gap:8px; display:grid }
              .g > div { height:20px }
              #nested { grid-template-columns: repeat(auto-fit, minmax(min(160px, 100%), 1fr)) }
              #calc   { grid-template-columns: repeat(auto-fit, minmax(calc(160px), 1fr)) }
              #plain  { grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)) }
            </style>
            <div class="g" id="nested"><div id="n0"></div><div></div><div></div>
              <div></div><div></div><div id="n5"></div></div>
            <div class="g" id="calc"><div id="c0"></div><div></div><div></div>
              <div></div><div></div><div></div></div>
            <div class="g" id="plain"><div id="p0"></div><div></div><div></div>
              <div></div><div></div><div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 600f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        // 924px with an 8px gap fits five 176.8px tracks; all three spellings agree.
        foreach (string first in new[] { "n0", "c0", "p0" })
        {
            Assert.True(
                MathF.Abs(Get(first).Width - 177f) < 1.01f,
                $"{first} should be one of five auto-fit tracks: {Get(first)}");
        }

        // The sixth item wraps to a second row rather than stacking one per row.
        Assert.True(Get("n5").Y > Get("n0").Y, $"{Get("n5")} {Get("n0")}");
        Assert.True(MathF.Abs(Get("n5").X - Get("n0").X) < 0.01f, $"{Get("n5")}");
    }

    [Fact]
    public void AStableScrollbarGutterIsReservedOnANestedScrollContainer()
    {
        // DEVIATION from the Rust reference, which reserves a scrollbar gutter only out of the
        // initial containing block, so a nested scroll container reserves none. Chromium takes
        // a stable gutter out of the content area on the inline axis and leaves the computed
        // padding untouched. Tesserae's annotated text editor overlays a highlight layer on a
        // `scrollbar-gutter: stable; scrollbar-width: thin` textarea; without the gutter the
        // overlay came out 10px wider than the text it marks.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              div { width:300px; height:60px }
              div > i { display:block; width:100%; height:10px }
              #thin  { overflow-y:scroll; scrollbar-gutter:stable; scrollbar-width:thin }
              #wide  { overflow-y:scroll; scrollbar-gutter:stable }
              #auto  { overflow-y:auto;   scrollbar-gutter:stable; scrollbar-width:thin }
              #none  { overflow-y:scroll; scrollbar-width:thin }
              #plain { overflow-y:scroll }
              #padded { overflow-y:scroll; scrollbar-gutter:stable; padding:0 10px;
                        box-sizing:content-box }
            </style>
            <div id="thin"><i id="a"></i></div>
            <div id="wide"><i id="b"></i></div>
            <div id="auto"><i id="c"></i></div>
            <div id="none"><i id="d"></i></div>
            <div id="plain"><i id="e"></i></div>
            <div id="padded"><i id="f"></i></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 600f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // scrollbar-width: thin reserves 10px, the classic gutter 15px, and `auto` overflow
        // reserves just as `scroll` does once the gutter is asked for.
        Assert.True(MathF.Abs(Width("a") - 290f) < 0.01f, $"{Width("a")}");
        Assert.True(MathF.Abs(Width("b") - 285f) < 0.01f, $"{Width("b")}");
        Assert.True(MathF.Abs(Width("c") - 290f) < 0.01f, $"{Width("c")}");

        // CORRECTION: an earlier version of this test asserted that `overflow-y: scroll` with no
        // `scrollbar-gutter` declaration takes no space, which is what Chromium does only when it
        // is told to hide scrollbars - Playwright passes `--hide-scrollbars` for every browser it
        // launches, and that is what the measurement behind it was taken through. Headless
        // Chromium driven over CDP without that flag draws classic scrollbars, and a `scroll` axis
        // always has one: 10px for `scrollbar-width: thin`, the classic 15px otherwise.
        Assert.True(MathF.Abs(Width("d") - 290f) < 0.01f, $"{Width("d")}");
        Assert.True(MathF.Abs(Width("e") - 285f) < 0.01f, $"{Width("e")}");

        // The gutter comes out of the content area; the padding is untouched by it.
        Assert.True(MathF.Abs(Width("f") - 285f) < 0.01f, $"{Width("f")}");
        Assert.True(
            MathF.Abs(laid.Styles[Id(tree, "padded")].Padding.Right - 10f) < 0.01f,
            "the reserved gutter must not show up as computed padding");
    }

    [Fact]
    public void AClassicScrollbarIsReservedByAnOverflowingScrollContainer()
    {
        // DEVIATION from the Rust reference, which reserves a scrollbar gutter only out of the
        // initial containing block. Headless Chromium draws classic (non-overlay) scrollbars, so
        // a scroll container's scrollbar takes space out of its scrollport: always on an
        // `overflow: scroll` axis, and on an `overflow: auto` axis once the content overflows.
        // Values measured against Chromium 141 driven over CDP - note that Playwright launches
        // browsers with `--hide-scrollbars`, under which none of these reserve anything.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              div { width:400px; height:100px }
              i { display:block; width:100%; height:10px }
              u { display:block; height:400px }
              s { display:block; width:900px; height:10px }
              #fits   { overflow:auto }
              #tall   { overflow:auto }
              #clip   { overflow:hidden }
              #always { overflow:scroll }
              #wide   { overflow:auto }
              #nobar  { overflow:auto; scrollbar-width:none }
              #thin   { overflow:auto; scrollbar-width:thin }
            </style>
            <div id="fits"><i id="a"></i></div>
            <div id="tall"><i id="b"></i><u></u></div>
            <div id="clip"><i id="c"></i><u></u></div>
            <div id="always"><i id="d"></i></div>
            <div id="wide"><i id="e"></i><s></s></div>
            <div id="nobar"><i id="f"></i><u></u></div>
            <div id="thin"><i id="g"></i><u></u></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 600f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // Content that fits keeps the whole scrollport; content that does not loses the classic
        // 15px to the vertical scrollbar.
        Assert.True(MathF.Abs(Width("a") - 400f) < 0.01f, $"{Width("a")}");
        Assert.True(MathF.Abs(Width("b") - 385f) < 0.01f, $"{Width("b")}");

        // `hidden` clips without a scrollbar; `scroll` shows one whether it is needed or not.
        Assert.True(MathF.Abs(Width("c") - 400f) < 0.01f, $"{Width("c")}");
        Assert.True(MathF.Abs(Width("d") - 385f) < 0.01f, $"{Width("d")}");

        // Overflow on the inline axis alone puts a scrollbar along the bottom, which costs
        // height, not width.
        Assert.True(MathF.Abs(Width("e") - 400f) < 0.01f, $"{Width("e")}");
        Assert.True(
            MathF.Abs(laid.Rects[Id(tree, "wide")].Height - 100f) < 0.01f,
            $"{laid.Rects[Id(tree, "wide")].Height}");

        // `scrollbar-width` sizes it: `none` removes the scrollbar entirely, `thin` is 10px.
        Assert.True(MathF.Abs(Width("f") - 400f) < 0.01f, $"{Width("f")}");
        Assert.True(MathF.Abs(Width("g") - 390f) < 0.01f, $"{Width("g")}");
    }

    [Fact]
    public void ACustomWebkitScrollbarSizesTheReservedScrollbar()
    {
        // Tesserae asks every scroll pane in Curiosity Workspace for a 9px scrollbar with a
        // page-wide `::-webkit-scrollbar { width: 9px }`, and Chromium takes exactly that out of
        // the scrollport - 9px, not the classic 15. A settings row inside one came out 9px too
        // wide, and every right-aligned control in it 9px too far right, until this was read.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              div { width:400px; height:100px; overflow:auto }
              i { display:block; width:100%; height:10px }
              u { display:block; height:400px }
              ::-webkit-scrollbar { width:7px; height:7px }
              .nine::-webkit-scrollbar { width:9px; height:9px }
              #gone::-webkit-scrollbar { display:none }
            </style>
            <div id="bare"><i id="a"></i><u></u></div>
            <div id="nine" class="nine"><i id="b"></i><u></u></div>
            <div id="gone"><i id="c"></i><u></u></div>
            <div id="fits" class="nine"><i id="d"></i></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 600f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // A pseudo-element rule with no originating compound is `*::-webkit-scrollbar`, and it
        // beats the user-agent thickness.
        Assert.True(MathF.Abs(Width("a") - 393f) < 0.01f, $"{Width("a")}");

        // A more specific rule wins over it, as any other declaration would.
        Assert.True(MathF.Abs(Width("b") - 391f) < 0.01f, $"{Width("b")}");

        // `display: none` on the scrollbar box hides it, so it costs nothing.
        Assert.True(MathF.Abs(Width("c") - 400f) < 0.01f, $"{Width("c")}");

        // Styling the scrollbar does not make one appear on a box that does not overflow.
        Assert.True(MathF.Abs(Width("d") - 400f) < 0.01f, $"{Width("d")}");
    }

    [Fact]
    public void AReservedScrollbarReResolvesFunctionalWidthsBelowIt()
    {
        // Reserving a classic scrollbar narrows the scroll container after the deferred cyclic
        // flex sizes have been resolved, and a functional inline size was flattened to a px
        // length against the containing block as it stood before that. Values measured against
        // Chromium 141 driven over CDP - Playwright launches browsers with `--hide-scrollbars`,
        // under which nothing is reserved and every box here is the control's width.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              ::-webkit-scrollbar { width:9px; height:9px }
              .row { display:flex; width:400px }
              .item { flex:1 1 auto }
              .scroller { height:100px; overflow-y:auto; overflow-x:hidden }
              .noscroll { height:100px; overflow:hidden }
              .cont { padding:2px }
              .card { display:block; width:calc(100% - 4px); box-sizing:border-box; height:10px }
              .pct { display:block; width:100%; height:10px }
              u { display:block; height:400px }
            </style>
            <div class="row"><div class="item"><div class="scroller">
              <div class="cont"><i id="a" class="card"></i><i id="b" class="pct"></i></div><u></u>
            </div></div></div>
            <div class="row"><div class="item"><div class="noscroll">
              <div class="cont"><i id="c" class="card"></i><i id="d" class="pct"></i></div><u></u>
            </div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 600f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // The scrollport is 400 - 9 = 391, so the container's content box is 387 and the card
        // 383. The bare percentage sibling reaches taffy typed and always re-resolved; the
        // calc() is the one that used to keep 392, its pre-gutter value.
        Assert.True(MathF.Abs(Width("a") - 383f) < 0.01f, $"{Width("a")}");
        Assert.True(MathF.Abs(Width("b") - 387f) < 0.01f, $"{Width("b")}");

        // Control: the same subtree in a box that reserves nothing. A fix that moves these has
        // broken the non-scrolling case.
        Assert.True(MathF.Abs(Width("c") - 392f) < 0.01f, $"{Width("c")}");
        Assert.True(MathF.Abs(Width("d") - 396f) < 0.01f, $"{Width("d")}");
    }

    [Fact]
    public void ANonFiniteInsetIsAnInvalidDeclaration()
    {
        // `nan` and `infinity` are identifiers in CSS, not numbers, so Chromium drops
        // `left: NaN%` / `top: Infinitypx` and the box keeps its static position.
        // masonry-layout writes exactly those strings on its first, pre-measure pass; a
        // box that accepted them laid out nowhere, measured as 0, and every later pass
        // recomputed NaN from that. Values measured against Chromium 141 over CDP.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              .box { position:relative; width:400px }
              .a { position:absolute; width:100%; height:20px; left:NaN%; top:Infinitypx }
              .b { position:absolute; width:NaNpx; height:20px; left:-Infinitypx; top:0 }
              .c { position:absolute; width:100%; height:20px; left:25%; top:40px }
            </style>
            <div class="box">
              <i id="a" class="a"></i><i id="b" class="b"></i><i id="c" class="c"></i>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 600f));
        Rect Box(string id) => laid.Rects[Id(tree, id)];

        // The invalid inset leaves the box at its static position, full containing-block wide.
        Assert.True(MathF.Abs(Box("a").X - 0f) < 0.01f, $"{Box("a").X}");
        Assert.True(MathF.Abs(Box("a").Y - 0f) < 0.01f, $"{Box("a").Y}");
        Assert.True(MathF.Abs(Box("a").Width - 400f) < 0.01f, $"{Box("a").Width}");
        Assert.True(MathF.Abs(Box("a").Height - 20f) < 0.01f, $"{Box("a").Height}");

        // An invalid width falls back to the auto/shrink-to-fit behavior, not to NaN.
        Assert.True(float.IsFinite(Box("b").Width), $"{Box("b").Width}");
        Assert.True(MathF.Abs(Box("b").X - 0f) < 0.01f, $"{Box("b").X}");

        // Control: a valid inset on the same shape still positions.
        Assert.True(MathF.Abs(Box("c").X - 100f) < 0.01f, $"{Box("c").X}");
        Assert.True(MathF.Abs(Box("c").Y - 40f) < 0.01f, $"{Box("c").Y}");
    }

    [Fact]
    public void FinalFlexReflowFinalizesFitContentBeforeDescendantCalc()
    {
        DomTree tree = Parse(
            """
            <style>
               html, body { margin:0 }
               .shell { display:flex; width:800px }
               .app { width:100% }
               .wrap { width:fit-content }
               .sizer { width:300px; height:10px }
               .calc { width:calc(100% - 10px); height:10px }
               </style>
               <main id="shell" class="shell"><div id="app" class="app">
                 <div id="wrap" class="wrap">
                   <div class="sizer"></div><div id="calc" class="calc"></div>
                 </div>
               </div></main>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 300f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        Assert.True(MathF.Abs(Width("shell") - 800f) < 0.01f, $"{Width("shell")}");
        Assert.True(MathF.Abs(Width("app") - 800f) < 0.01f, $"{Width("app")}");
        Assert.True(MathF.Abs(Width("wrap") - 300f) < 0.01f, $"{Width("wrap")}");
        Assert.True(MathF.Abs(Width("calc") - 290f) < 0.01f, $"{Width("calc")}");
    }

    [Fact]
    public void EmptyDocumentIsSafe()
    {
        // html5ever always synthesizes html/head/body, so an empty document
        // still has a few element rects. The point is that it does not panic.
        DomTree tree = Parse("");
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Assert.True(laid.Rects.Count <= 4, $"got {laid.Rects.Count}");
    }

    [Fact]
    public void AuthorCascadeHonorsImportantAndPresentationalHintOrder()
    {
        DomTree tree = Parse(
            """
            <style>
                #sheet { width: 320px !important; background: green !important }
                .case#sheet { width: 80px; background: red }
                .inline-normal { width: 320px !important; background: green !important }
                #inline-important { width: 80px !important; background: red !important }
                .custom { --w: 320px !important; --w: 80px; width: var(--w) }
                .hint { background: green }
            </style>
            <div id="sheet" class="case"></div>
            <div id="inline-normal" class="inline-normal" style="width:80px;background:red"></div>
            <div id="inline-important" style="width:320px!important;background:green!important"></div>
            <div id="inline-order" style="width:320px!important;width:80px;background:green!important;background:red"></div>
            <div id="custom" class="custom"></div>
            <div id="hint" class="hint" bgcolor="red"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        foreach (string id in new[]
        {
            "sheet", "inline-normal", "inline-important", "inline-order", "custom",
        })
        {
            NodeId nid = tree.QuerySelector($"#{id}")!.Value;
            LayoutStyle style = laid.Styles[nid];
            Assert.Equal(Dimension.Px(320f), style.Width);
        }

        foreach (string id in new[]
        {
            "sheet", "inline-normal", "inline-important", "inline-order", "hint",
        })
        {
            NodeId nid = tree.QuerySelector($"#{id}")!.Value;
            LayoutStyle style = laid.Styles[nid];
            Assert.Equal(new RgbaColor(0, 128, 0, 255), style.BackgroundColor);
        }
    }

    [Fact]
    public void BoxSizingControlsTheDeclaredSizeEdge()
    {
        DomTree tree = Parse(
            """
            <style>
                body { margin: 0 }
                .box { width:100px; height:50px; padding:10px; border:2px solid black }
                .border { box-sizing:border-box }
                .parent { width:400px }
                .half { width:50%; padding:10px; border:2px solid black }
                .limited { width:200px; max-width:100px; padding:10px; border:2px solid black }
                .inherit-parent { box-sizing:border-box }
                .inherit-child { box-sizing:inherit; width:100px; padding:10px; border:2px solid black }
            </style>
            <div id="content" class="box"></div>
            <div id="border" class="box border"></div>
            <div class="parent"><div id="half-content" class="half"></div><div id="half-border" class="half border"></div></div>
            <div id="max-content" class="limited"></div>
            <div id="max-border" class="limited border"></div>
            <div class="inherit-parent"><div id="inherited-border" class="inherit-child"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        (float Width, float Height) Size(string id)
        {
            NodeId nid = tree.QuerySelector($"#{id}")!.Value;
            Rect rect = laid.Rects[nid];
            return (rect.Width, rect.Height);
        }

        Assert.Equal((124f, 74f), Size("content"));
        Assert.Equal((100f, 50f), Size("border"));
        Assert.Equal(224f, Size("half-content").Width);
        Assert.Equal(200f, Size("half-border").Width);
        Assert.Equal(124f, Size("max-content").Width);
        Assert.Equal(100f, Size("max-border").Width);
        Assert.Equal(100f, Size("inherited-border").Width);
    }

    [Fact]
    public void LogicalSizesControlFinalHorizontalGeometry()
    {
        DomTree tree = Parse(
            """
            <style>
                body { margin:0 }
                #logical-last {
                    width:20px; inline-size:120px;
                    height:10px; block-size:30px;
                }
                #physical-last {
                    inline-size:120px; width:40px;
                    block-size:30px; height:15px;
                }
                #bounded {
                    inline-size:calc(50vw - 10px);
                    block-size:80px;
                    min-inline-size:300px;
                    max-block-size:40px;
                }
            </style>
            <div id="logical-last"></div>
            <div id="physical-last"></div>
            <div id="bounded"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 240f));
        (float Width, float Height) Size(string id)
        {
            NodeId nid = tree.QuerySelector($"#{id}")!.Value;
            Rect rect = laid.Rects[nid];
            return (rect.Width, rect.Height);
        }

        Assert.Equal((120f, 30f), Size("logical-last"));
        Assert.Equal((40f, 15f), Size("physical-last"));
        Assert.Equal((300f, 40f), Size("bounded"));
    }

    [Fact]
    public void RootPercentageFontSizeControlsRemLengths()
    {
        DomTree tree = Parse(
            """
            <style>
                html { font-size: 62.5% }
                body { margin: 0 }
                #box {
                    display: grid;
                    width: 4rem;
                    margin-top: 3rem;
                    row-gap: 4rem;
                    column-gap: calc(1rem + 2vw);
                }
                #box > div { height: 2rem }
            </style>
            <div id="box"><div></div><div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId html = tree.QuerySelector("html")!.Value;
        NodeId boxId = tree.QuerySelector("#box")!.Value;
        Assert.Equal(10f, laid.Styles[html].FontSize);
        Assert.Equal(40f, laid.Styles[boxId].RowGap);
        Assert.Equal(35.6f, laid.Styles[boxId].ColumnGap);
        Assert.Equal(40f, laid.Rects[boxId].Width);
        Assert.Equal(80f, laid.Rects[boxId].Height);
        Assert.Equal(30f, laid.Rects[boxId].Y);
    }

    [Fact]
    public void BlockAndGridSiblingMarginsCollapse()
    {
        DomTree tree = Parse(
            """
            <style>
                body { margin: 0 }
                .a { position: relative; height: 10px; margin-bottom: 30px }
                .b { position: relative; display: grid; height: 10px; margin-top: 40px }
            </style>
            <div class="a"></div>
            <div id="b" class="b"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId b = tree.QuerySelector("#b")!.Value;
        Assert.Equal(50f, laid.Rects[b].Y);
    }

    [Fact]
    public void TextTransformAppliesToWordLeavesInFlexUi()
    {
        DomTree tree = Parse(
            """<div id="cta" style="display:flex;text-transform:uppercase">get started</div>""");
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId cta = Id(tree, "cta");
        List<int> items = laid.RunIfcItems[cta];
        Assert.Single(items);
        Assert.Equal("GET STARTED", laid.TextEngine.ItemText(items[0]));
        Assert.Equal(
            "HELLO WORLD",
            DomBuild.TransformWordLeafText("hELLO wORLD", TextTransform.Capitalize));
    }

    [Fact]
    public void WordLeavesUseTheComputedLineHeight()
    {
        DomTree tree = Parse(
            """<div id="code" style="display:flex;font:16px/32px monospace">code</div>""");
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId code = Id(tree, "code");
        List<int> items = laid.RunIfcItems[code];
        Assert.Single(items);
        (_, float height) = laid.TextEngine.Measure(items[0], 1280f);
        Assert.Equal(32f, height);
    }

    [Fact]
    public void LightRootRejectsDarkPseudoGradientVariants()
    {
        DomTree tree = Parse(
            """
            <html class="light"><head><style>
               .hero::before {
                 content:"";
                 position:absolute;
                 background:radial-gradient(circle, #ebf3f9, #d6dee4);
               }
               @media (prefers-color-scheme:dark) {
                 :root:not(.light) .hero::before {
                   background:radial-gradient(circle, #111111, #222222);
                 }
               }
               :root.dark .hero::before {
                 background:radial-gradient(circle, #333333, #444444);
               }
               </style></head><body><div class="hero"></div></body></html>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId hero = tree.QuerySelector(".hero")!.Value;
        LayoutStyle pseudo = laid.Styles[hero].BeforePseudo!;
        (_, List<GradientStop> stops) = pseudo.BackgroundRadialGradient!.Value;
        Assert.Equal(new RgbaColor(0xEB, 0xF3, 0xF9, 255), stops[0].Color);
        Assert.Equal(new RgbaColor(0xD6, 0xDE, 0xE4, 255), stops[1].Color);
    }

    [Fact]
    public void TableCellContentBoxWidthIncludesPaddingAndBorderInTrack()
    {
        DomTree tree = Parse(
            """
            <style>
                table { border-spacing:0 }
                td { width:100px; padding:10px; border:2px solid black }
                .border { box-sizing:border-box }
            </style>
            <table><tr><td id="content-cell">content</td></tr></table>
            <table><tr><td id="border-cell" class="border">border</td></tr></table>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        float Width(string id) => laid.Rects[tree.QuerySelector($"#{id}")!.Value].Width;
        Assert.Equal(124f, Width("content-cell"));
        Assert.Equal(100f, Width("border-cell"));
    }

    [Fact]
    public void FixedTableLayoutUsesOnlyFirstRowForColumnGeometry()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                table { table-layout:fixed; width:300px; border-collapse:collapse }
                td { height:30px; padding:0; border:0 }
                #first { width:50px }
                #later { width:250px; white-space:nowrap }
            </style>
            <table><tr><td id="first"></td><td id="second"></td></tr>
            <tr><td id="later">XXXXXXXXXXXXXXXXXXXXXXXX</td><td id="last"></td></tr></table>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        foreach (string id in new[] { "first", "later" })
        {
            Assert.True(MathF.Abs(Get(id).Width - 50f) < 0.1f, $"{id}: {Get(id)}");
        }

        foreach (string id in new[] { "second", "last" })
        {
            Assert.True(MathF.Abs(Get(id).Width - 250f) < 0.1f, $"{id}: {Get(id)}");
        }
    }

    [Fact]
    public void FixedTableLayoutAccountsForSeparateBorderSpacing()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                table { table-layout:fixed; width:300px; border:4px solid black; border-spacing:10px 0 }
                td { height:30px; padding:0; border:0 }
                #first { width:50px }
            </style>
            <table id="table"><tr><td id="first"></td><td id="second"></td></tr></table>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Assert.True(MathF.Abs(Get("table").Width - 300f) < 0.1f, $"{Get("table")}");
        Assert.True(MathF.Abs(Get("first").X - 14f) < 0.1f, $"{Get("first")}");
        Assert.True(MathF.Abs(Get("first").Width - 50f) < 0.1f, $"{Get("first")}");
        Assert.True(MathF.Abs(Get("second").X - 74f) < 0.1f, $"{Get("second")}");
        Assert.True(MathF.Abs(Get("second").Width - 212f) < 0.1f, $"{Get("second")}");
    }

    [Fact]
    public void FixedContentBoxTableKeepsBorderSpacingInsideDeclaredWidth()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                table { box-sizing:content-box; table-layout:fixed; width:300px; border:4px solid black; border-spacing:10px 0 }
                td { height:30px; padding:0; border:0 }
                #first { width:50px }
            </style>
            <table id="table"><tr><td id="first"></td><td id="second"></td></tr></table>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Assert.True(MathF.Abs(Get("table").Width - 308f) < 0.1f, $"{Get("table")}");
        Assert.True(MathF.Abs(Get("first").Width - 50f) < 0.1f, $"{Get("first")}");
        Assert.True(MathF.Abs(Get("second").Width - 220f) < 0.1f, $"{Get("second")}");
    }

    [Fact]
    public void FixedPercentageTableResolvesTracksAgainstFinalUsedWidth()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                table { table-layout:fixed; width:50%; border-spacing:10px 0 }
                td { height:30px; padding:0; border:0 }
                #first { width:25% }
            </style>
            <table id="table"><tr><td id="first"></td><td id="second"></td></tr></table>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Assert.True(MathF.Abs(Get("table").Width - 400f) < 0.1f, $"{Get("table")}");
        // Final paint geometry is snapped to device pixels around the 92.5 /
        // 277.5 CSS-pixel track boundary.
        Assert.True(MathF.Abs(Get("first").Width - 92.5f) <= 0.5f, $"{Get("first")}");
        Assert.True(MathF.Abs(Get("second").Width - 277.5f) <= 0.5f, $"{Get("second")}");
    }

    [Fact]
    public void FixedColumnsWinOverFirstRowSpanningCellWidths()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                table { table-layout:fixed; width:300px; border-spacing:10px 0 }
                td { height:30px; padding:0; border:0 }
            </style>
            <table><col style="width:40px"><col><col>
              <tr><td id="span" colspan="2" style="width:200px"></td><td id="top-last"></td></tr>
              <tr><td id="one"></td><td id="two"></td><td id="three"></td></tr>
            </table>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;
        Assert.True(MathF.Abs(Width("span") - 145f) < 0.1f, $"{Width("span")}");
        Assert.True(MathF.Abs(Width("one") - 40f) < 0.1f, $"{Width("one")}");
        Assert.True(MathF.Abs(Width("two") - 95f) < 0.1f, $"{Width("two")}");
        Assert.True(MathF.Abs(Width("three") - 125f) < 0.1f, $"{Width("three")}");
    }

    [Fact]
    public void FixedTableLayoutWithAutoWidthFallsBackToAutoAlgorithm()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                table { table-layout:fixed; border-collapse:collapse }
                td { padding:0; border:0 }
                #first { width:50px }
                #later { width:250px }
            </style>
            <table><tr><td id="first"></td><td></td></tr>
            <tr><td id="later"></td><td></td></tr></table>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 300f));
        Rect first = laid.Rects[Id(tree, "first")];
        Rect later = laid.Rects[Id(tree, "later")];
        Assert.True(first.Width >= 249.9f, $"{first}");
        Assert.True(MathF.Abs(first.Width - later.Width) < 0.1f, $"{first} {later}");
    }

    [Fact]
    public void FixedTableColumnDistributionMatchesCssFixedRules()
    {
        static FixedTableColumn Column(float length, float percentage, bool specified) =>
            new(length, percentage, specified);
        static void AssertWidths(IReadOnlyList<float> actual, params float[] expected)
        {
            Assert.Equal(expected.Length, actual.Count);
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.True(
                    MathF.Abs(actual[index] - expected[index]) < 0.01f,
                    $"{actual[index]} != {expected[index]}");
            }
        }

        AssertWidths(
            DomTableSupport.DistributeFixedTableColumns(
                300f, [Column(50f, 0f, true), Column(0f, 0f, false)]),
            50f, 250f);
        AssertWidths(
            DomTableSupport.DistributeFixedTableColumns(
                300f, [Column(50f, 0f, true), Column(100f, 0f, true)]),
            100f, 200f);
        AssertWidths(
            DomTableSupport.DistributeFixedTableColumns(
                300f, [Column(50f, 0f, true), Column(0f, 0.5f, true)]),
            150f, 150f);
        AssertWidths(
            DomTableSupport.DistributeFixedTableColumns(
                300f, [Column(250f, 0f, true), Column(100f, 0f, true)]),
            250f, 100f);
        AssertWidths(
            DomTableSupport.DistributeFixedTableColumns(
                300f, [Column(0f, 0.75f, true), Column(0f, 0.75f, true)]),
            150f, 150f);
    }

    [Fact]
    public void InheritedRtlDirectionControlsFlexGridAndBlockStartEdges()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0; direction:rtl }
                .case { width:300px; height:40px; margin-bottom:20px }
                .flex { display:flex }
                .grid { display:grid; grid-template-columns:50px 50px }
                .item { width:50px; height:40px }
                .block > div { width:50px; height:40px }
            </style>
            <div id="flex" class="case flex"><div id="flex-one" class="item"></div><div id="flex-two" class="item"></div></div>
            <div id="grid" class="case grid"><div id="grid-one" class="item"></div><div id="grid-two" class="item"></div></div>
            <div class="case block"><div id="block"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        float X(string id) => laid.Rects[Id(tree, id)].X;
        Assert.True(MathF.Abs(X("flex-one") - 850f) < 0.1f, $"{X("flex-one")}");
        Assert.True(MathF.Abs(X("flex-two") - 800f) < 0.1f, $"{X("flex-two")}");
        Assert.True(MathF.Abs(X("grid-one") - 850f) < 0.1f, $"{X("grid-one")}");
        Assert.True(MathF.Abs(X("grid-two") - 800f) < 0.1f, $"{X("grid-two")}");
        Assert.True(MathF.Abs(X("block") - 850f) < 0.1f, $"{X("block")}");
        foreach (string id in new[] { "flex", "grid" })
        {
            Assert.Equal(Layout.Direction.Rtl, laid.Styles[Id(tree, id)].Direction);
        }
    }

    [Fact]
    public void HtmlDirHintIsOverriddenByAuthorCssAndInherits()
    {
        DomTree tree = Parse(
            """
            <style>#override { direction:ltr }</style>
            <div dir="rtl"><div id="inherited"></div><div id="override"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        LayoutStyle Style(string id) => laid.Styles[Id(tree, id)];
        Assert.Equal(Layout.Direction.Rtl, Style("inherited").Direction);
        Assert.Equal(Layout.Direction.Ltr, Style("override").Direction);
    }

    [Fact]
    public void InheritedDirectionResolvesLogicalBorderCascadeAfterStylesheets()
    {
        DomTree tree = Parse(
            """
            <style>
                body { direction:rtl; margin:0 }
                .box { width:200px; height:80px; box-sizing:border-box }
                .physical-low { border-right:4px solid green }
                #logical-last { border-inline-start:12px solid purple }
                .physical { border-inline-start:12px solid red }
                #physical-last { border-right:4px solid teal }
            </style>
            <div id="logical-last" class="box physical-low"></div>
            <div id="physical-last" class="box physical"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 300f));
        LayoutStyle Style(string id) => laid.Styles[Id(tree, id)];
        Assert.Equal(12f, Style("logical-last").Border.Right);
        // ID specificity wins regardless of source order: the physical alias
        // and logical property compete for the same final RTL side.
        Assert.Equal(
            new RgbaColor(128, 0, 128, 255),
            Style("logical-last").BorderModel.Colors.Right);
        Assert.Equal(4f, Style("physical-last").Border.Right);
        Assert.Equal(
            new RgbaColor(0, 128, 128, 255),
            Style("physical-last").BorderModel.Colors.Right);
    }

    [Fact]
    public void AuthoredTableWithMixedDirectContentPreservesNonCellChild()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                #group { display:table; width:300px }
                #cell { display:table-cell; width:100px; height:20px }
                #ordinary { display:block; width:80px; height:30px }
            </style>
            <div id="group">
                <div id="cell"></div>
                direct text
                <div id="ordinary"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 200f));

        Assert.True(laid.Rects.ContainsKey(Id(tree, "group")));
        Assert.True(laid.Rects.ContainsKey(Id(tree, "cell")));
        Rect ordinary = laid.Rects[Id(tree, "ordinary")];
        Assert.True(MathF.Abs(ordinary.Width - 80f) < 0.1f, $"{ordinary}");
        Assert.True(MathF.Abs(ordinary.Height - 30f) < 0.1f, $"{ordinary}");
        // Before the preflight check, finding `cell` was enough to take the
        // dedicated table path, which filtered `ordinary` (and the text) out.
        // Its geometry proves the mixed subtree instead used ordinary box
        // construction and kept the non-cell sibling.
    }

    [Fact]
    public void AuthoredTableCellsAllocateBootstrapInputGroupColumns()
    {
        DomTree tree = Parse(
            """
            <style>
                * { box-sizing:border-box }
                html,body { margin:0 }
                #group { display:table; width:600px; border-collapse:separate }
                #field,#addon,#button { display:table-cell; height:34px }
                #field { width:100% }
                #addon,#button {
                    width:1%; white-space:nowrap; padding:0 12px;
                    font:16px/normal "Liberation Sans";
                }
            </style>
            <div id="group"><div id="field"></div><div id="addon">Addon label</div><div id="button">Button</div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 200f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect group = Get("group");
        Rect field = Get("field");
        Rect addon = Get("addon");
        Rect button = Get("button");

        Assert.True(MathF.Abs(group.Width - 600f) < 0.1f, $"{group}");
        Assert.True(MathF.Abs(field.X - group.X) < 0.1f, $"{field} {group}");
        Assert.True(MathF.Abs(addon.X - (field.X + field.Width)) < 0.1f);
        Assert.True(MathF.Abs(button.X - (addon.X + addon.Width)) < 0.1f);
        Assert.True(
            MathF.Abs(button.X + button.Width - (group.X + group.Width)) < 0.1f);
        Assert.True(
            addon.Width > 80f, $"nowrap addon must keep its intrinsic width: {addon}");
        Assert.True(
            button.Width > 50f, $"nowrap button must keep its intrinsic width: {button}");
        Assert.True(field.Width > addon.Width + button.Width, $"{field} {addon} {button}");
        foreach (Rect cell in new[] { field, addon, button })
        {
            Assert.True(MathF.Abs(cell.Height - 34f) < 0.1f, $"{cell}");
        }
    }

    [Fact]
    public void NowrapTableCellKeepsBootstrapControlsOnOneRow()
    {
        DomTree tree = Parse(
            """
            <style>
                * { box-sizing:border-box }
                html,body { margin:0 }
                #group { display:table; width:750px; border-collapse:separate }
                #field,#actions { display:table-cell; height:55px }
                #field { float:left; width:100% }
                #actions { width:1%; white-space:nowrap }
                #bulk,#submit { display:inline-block; height:55px }
                #bulk { width:60px }
                #submit { width:42px }
            </style>
            <div id="group"><input id="field"><span id="actions"><a id="bulk"></a><button id="submit"></button></span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (900f, 250f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect group = Get("group");
        Rect field = Get("field");
        Rect actions = Get("actions");
        Rect bulk = Get("bulk");
        Rect submit = Get("submit");

        Assert.True(MathF.Abs(group.Height - 55f) < 0.1f, $"{group}");
        Assert.True(MathF.Abs(field.Height - 55f) < 0.1f, $"{field}");
        Assert.True(MathF.Abs(actions.Height - 55f) < 0.1f, $"{actions}");
        Assert.True(MathF.Abs(submit.X - (bulk.X + bulk.Width)) < 0.1f);
        Assert.True(MathF.Abs(bulk.Y - submit.Y) < 0.1f);
        Assert.True(MathF.Abs(submit.X + submit.Width - (group.X + group.Width)) < 0.1f);
    }

    [Fact]
    public void AutoWidthAuthoredTableHonorsPercentageCellIntrinsicConstraints()
    {
        DomTree tree = Parse(
            """
            <style>
                * { box-sizing:border-box }
                html,body { margin:0 }
                #host { width:600px }
                #group { display:table }
                #field,#button { display:table-cell; height:34px }
                #field { width:100% }
                #button { width:1%; min-width:100px }
            </style>
            <div id="host"><div id="group"><div id="field"></div><div id="button"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 200f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        Rect group = Get("group");
        Rect field = Get("field");
        Rect button = Get("button");

        // Chromium 145: group=600, field=500, button=100. An auto-width
        // table cannot shrink-wrap to 100px here: the 100% field and the
        // non-empty 1% neighbor make their percentage constraints fill the
        // definite available width.
        Assert.True(MathF.Abs(group.Width - 600f) < 0.1f, $"{group}");
        Assert.True(MathF.Abs(field.Width - 500f) < 0.1f, $"{field}");
        Assert.True(MathF.Abs(button.Width - 100f) < 0.1f, $"{button}");
        Assert.True(MathF.Abs(button.X - (field.X + field.Width)) < 0.1f);
    }

    [Fact]
    public void AutoTablePercentGuessReservesIntrinsicNeighborColumns()
    {
        List<float> widths = DomTableSupport.DistributeAutoTableColumns(
            600f,
            [0f, 100f, 70f],
            [0f, 100f, 70f],
            [null, null, null],
            [1f, 0.01f, 0.01f]);
        Assert.True(MathF.Abs(widths[0] - 430f) < 0.01f, $"{StyleDump.Dump(widths)}");
        Assert.True(MathF.Abs(widths[1] - 100f) < 0.01f, $"{StyleDump.Dump(widths)}");
        Assert.True(MathF.Abs(widths[2] - 70f) < 0.01f, $"{StyleDump.Dump(widths)}");
    }

    [Fact]
    public void AutoTablePercentageIntrinsicFloorMatchesChromiumCases()
    {
        float?[] percentages = [0.1f, null];
        Assert.True(
            MathF.Abs(
                DomTableSupport.AutoTablePercentageIntrinsicFloor([16f, 144f], percentages)
                - 160f) < 0.01f);
        percentages = [0.5f, null];
        Assert.True(
            MathF.Abs(
                DomTableSupport.AutoTablePercentageIntrinsicFloor(
                    [16f, 106.71875f], percentages) - 213.4375f) < 0.01f);
        percentages = [0.9f, null];
        Assert.True(
            DomTableSupport.AutoTablePercentageIntrinsicFloor([16f, 106.71875f], percentages)
            > 1000f);
    }

    [Fact]
    public void WhiteSpaceInheritsIntoABlockDescendantInlineContext()
    {
        DomTree tree = Parse(
            """
            <style>
                html, body, pre { margin:0 }
                pre { width:120px; white-space:pre; font-size:16px; line-height:20px }
                code { display:flex; flex-direction:column }
                .line { display:block }
                #normal { white-space:normal }
                #inherited::before { content:""; display:none }
            </style>
            <pre><code>
                <span id="inherited" class="line">alpha beta gamma delta</span>
                <span id="normal" class="line">alpha beta gamma delta</span>
            </code></pre>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 200f));
        NodeId inherited = Id(tree, "inherited");
        NodeId normal = Id(tree, "normal");

        Assert.Equal(WhiteSpace.Pre, laid.Styles[inherited].WhiteSpace);
        Assert.Equal(WhiteSpace.Normal, laid.Styles[normal].WhiteSpace);
        Assert.Equal(WhiteSpace.Pre, laid.Styles[inherited].BeforePseudo!.WhiteSpace);
        Assert.Equal(20f, laid.Rects[inherited].Height);
        Assert.True(
            laid.Rects[normal].Height > 20f,
            "the explicit normal line should still wrap at the containing width");
    }

    [Fact]
    public void ForcedBreakInPseudoJoinedInlineRunHasNoPhantomLine()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,h1,p { margin:0 }
                h1 { width:600px; font:40px/60px sans-serif }
                #heading::after, #consecutive::after {
                    content:"_"; display:inline; position:relative
                }
                p { width:300px; font:16px/20px sans-serif }
            </style>
            <h1 id="heading">first<br id="heading-break">second</h1>
            <p id="consecutive">A<br><br>B</p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 400f));
        NodeId heading = Id(tree, "heading");
        NodeId headingBreak = Id(tree, "heading-break");
        NodeId consecutive = Id(tree, "consecutive");
        LayoutStyle breakStyle = laid.Styles[headingBreak];

        Assert.Equal(Display.Inline, breakStyle.Display);
        Assert.Equal(Dimension.Auto, breakStyle.Width);
        Assert.Equal(Dimension.Auto, breakStyle.Height);
        Assert.True(
            MathF.Abs(laid.Rects[heading].Height - 120f) < 0.01f,
            $"A<br>B plus an inline pseudo must form two 60px lines: host={laid.Rects[heading]}, pseudo={laid.Styles[heading].AfterPseudo is not null}");
        Assert.Equal(
            0f,
            laid.Rects.TryGetValue(headingBreak, out Rect breakRect) ? breakRect.Width : 0f);
        Assert.True(
            MathF.Abs(laid.Rects[consecutive].Height - 60f) < 0.01f,
            $"A<br><br>B must form three 20px lines: {laid.Rects[consecutive]}");
    }

    [Fact]
    public void UaBlockMarginsResolveAgainstTheComputedElementFont()
    {
        DomTree tree = Parse(
            """
            <style>
                body { margin:0 }
                h1 { font-size:40px }
                p { font-size:24px }
            </style>
            <h1 id="heading">Heading</h1>
            <p id="paragraph">Paragraph</p>
            <section style="font-size:20px">
              <h1 id="h1">H1</h1><h2 id="h2">H2</h2><h3 id="h3">H3</h3>
              <h4 id="h4">H4</h4><h5 id="h5">H5</h5><h6 id="h6">H6</h6>
              <h1 id="inherit-size" style="font-size:inherit">Inherited</h1>
              <h1 id="initial-size" style="font-size:initial">Initial</h1>
            </section>
            <ul id="outer-list"><li>
              <ul id="second-list"><li>
                <ol><li><ul id="third-list"></ul></li></ol>
              </li></ul>
            </li></ul>
            <dl><dd><ul id="definition-list-child"></ul></dd></dl>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 400f));
        NodeId heading = Id(tree, "heading");
        NodeId paragraph = Id(tree, "paragraph");

        Assert.True(
            MathF.Abs(laid.Styles[heading].Margin.Top - 26.8f) < 0.01f
                && MathF.Abs(laid.Styles[heading].Margin.Bottom - 26.8f) < 0.01f,
            $"h1's 0.67em UA margins must follow its authored 40px font: {laid.Styles[heading].Margin}");
        Assert.True(
            MathF.Abs(laid.Styles[paragraph].Margin.Top - 24f) < 0.01f
                && MathF.Abs(laid.Styles[paragraph].Margin.Bottom - 24f) < 0.01f,
            $"p's 1em UA margins must follow its authored 24px font: {laid.Styles[paragraph].Margin}");
        foreach ((string id, float size, float margin) in new[]
        {
            ("h1", 40f, 26.8f),
            ("h2", 30f, 24.9f),
            ("h3", 23.4f, 23.4f),
            ("h4", 20f, 26.6f),
            ("h5", 16.6f, 27.722f),
            ("h6", 13.4f, 31.222f),
        })
        {
            LayoutStyle style = laid.Styles[Id(tree, id)];
            Assert.True(
                MathF.Abs(style.FontSize!.Value - size) < 0.01f
                    && MathF.Abs(style.Margin.Top - margin) < 0.01f
                    && MathF.Abs(style.Margin.Bottom - margin) < 0.01f,
                $"{id} UA font and margins must resolve from the inherited 20px font: size={style.FontSize}, margin={style.Margin}");
        }

        foreach ((string id, float size, float margin) in new[]
        {
            ("inherit-size", 20f, 13.4f),
            ("initial-size", 16f, 10.72f),
        })
        {
            LayoutStyle style = laid.Styles[Id(tree, id)];
            Assert.True(
                MathF.Abs(style.FontSize!.Value - size) < 0.01f
                    && MathF.Abs(style.Margin.Top - margin) < 0.01f,
                $"{id} CSS-wide font-size semantics must override the UA heading size: size={style.FontSize}, margin={style.Margin}");
        }

        foreach ((string id, ListStyle marker) in new[]
        {
            ("second-list", ListStyle.Circle),
            ("third-list", ListStyle.Square),
            ("definition-list-child", ListStyle.Disc),
        })
        {
            LayoutStyle style = laid.Styles[Id(tree, id)];
            Assert.Equal(0f, style.Margin.Top);
            Assert.Equal(0f, style.Margin.Bottom);
            Assert.Equal(marker, style.ListStyle);
        }
    }

    [Fact]
    public void GeneratedInlineBoxesApplySizesOnlyAfterDisplayBlockification()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                #flex { display:flex }
                #flex::before {
                    content:""; display:inline;
                    width:100px; height:70px;
                    min-width:250px; min-height:120px;
                    max-width:50px; max-height:40px;
                    background:red
                }
                #float::before {
                    content:""; display:inline; float:left;
                    width:80px; height:40px; background:blue
                }
                #relative::before {
                    content:""; display:inline; position:relative;
                    width:80px; height:40px; background:green
                }
            </style>
            <div id="flex"></div>
            <div id="float"></div>
            <div id="relative"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 300f));
        NodeId flex = Id(tree, "flex");
        NodeId floated = Id(tree, "float");
        NodeId relative = Id(tree, "relative");

        Rect Pseudo(NodeId hostNode) => laid.GeneratedBoxes
            .First(generated =>
                generated.Host == hostNode && generated.Kind == GeneratedBoxKind.Before)
            .Rect;

        Assert.Equal(Display.Block, laid.Styles[flex].BeforePseudo!.Display);
        Assert.Equal(Display.Block, laid.Styles[floated].BeforePseudo!.Display);
        Assert.Equal(Display.Inline, laid.Styles[relative].BeforePseudo!.Display);
        Assert.Equal(250f, Pseudo(flex).Width);
        Assert.Equal(120f, Pseudo(flex).Height);
        Assert.Equal(80f, Pseudo(floated).Width);
        Assert.Equal(40f, Pseudo(floated).Height);
        Assert.True(
            Pseudo(relative).Width < 80f && Pseudo(relative).Height < 40f,
            "an ordinary inline pseudo must ignore authored box sizes");
    }

    [Fact]
    public void OrdinaryInlineFragmentUsesFontBoxWhileLineKeepsLineHeight()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { height:auto; font:16px/40px monospace }
                code {
                    position:relative;
                    font:16px/40px monospace;
                    padding-top:5px; padding-bottom:7px;
                    border-top:2px solid; border-bottom:3px solid;
                    margin-top:19px; margin-bottom:23px;
                    background:red
                }
                #control { margin-top:0; margin-bottom:0 }
            </style>
            <p id="with-margin"><code id="token">token</code></p>
            <p id="without-margin"><code id="control">token</code></p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 200f));
        NodeId host = Id(tree, "with-margin");
        NodeId controlHost = Id(tree, "without-margin");
        NodeId token = Id(tree, "token");
        NodeId control = Id(tree, "control");
        LayoutStyle style = laid.Styles[token];
        float rawFontHeight = laid.TextEngine.InlineFontBoxHeight(style);
        float expectedFragmentHeight = rawFontHeight + 5f + 7f + 2f + 3f;

        Assert.Equal(40f, laid.Rects[host].Height);
        Assert.Equal(40f, laid.Rects[controlHost].Height);
        Assert.Equal(expectedFragmentHeight, laid.Rects[token].Height);
        Assert.Equal([laid.Rects[token]], laid.InlineFragments[token]);
        Assert.True(
            MathF.Abs(
                laid.Rects[token].Y - laid.Rects[host].Y
                - (((40f - rawFontHeight) / 2f) - 5f - 2f)) < 0.5f,
            "final shaped baseline must retain the expected centered font box");
        Assert.Equal(
            laid.Rects[token].Y - laid.Rects[host].Y,
            laid.Rects[control].Y - laid.Rects[controlHost].Y);
    }

    [Fact]
    public void NestedInlineUsesFinalShapingForItsCanonicalFragment()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { width:300px; font:16px/20px monospace }
            </style>
            <p id="host">before <span id="token">plain nested text</span> after</p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 100f));
        NodeId token = Id(tree, "token");
        List<Rect> fragments = laid.InlineFragments[token];

        Assert.Single(fragments);
        Assert.Equal(laid.Rects[token], fragments[0]);
        Assert.True(fragments[0].Width > 100f, $"{StyleDump.Dump(fragments)}");
        Assert.Single(laid.IfcItems);
    }

    [Fact]
    public void WrappedDecoratedInlineExposesThreeOrderedFontBoxFragments()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { width:80px; font:16px/20px monospace }
                code {
                    padding-top:2px; padding-bottom:3px;
                    border-top:1px solid; border-bottom:2px solid;
                    background:red
                }
            </style>
            <p><code id="token">alpha beta gamma</code></p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (300f, 120f));
        NodeId token = Id(tree, "token");
        List<Rect> fragments = laid.InlineFragments[token];

        Assert.Equal(3, fragments.Count);
        for (int index = 0; index + 1 < fragments.Count; index++)
        {
            Assert.True(fragments[index].Y < fragments[index + 1].Y);
        }

        Assert.All(fragments, fragment => Assert.True(fragment.Width > 0f));
        float left = fragments.Aggregate(float.PositiveInfinity, (a, r) => F32.Min(a, r.X));
        float top = fragments.Aggregate(float.PositiveInfinity, (a, r) => F32.Min(a, r.Y));
        float right = fragments.Aggregate(
            float.NegativeInfinity, (a, r) => F32.Max(a, r.X + r.Width));
        float bottom = fragments.Aggregate(
            float.NegativeInfinity, (a, r) => F32.Max(a, r.Y + r.Height));
        Assert.Equal(new Rect(left, top, right - left, bottom - top), laid.Rects[token]);
    }

    [Fact]
    public void InlineHorizontalEdgesAdvanceAdjacentContentButMarginsStayOutsideRect()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { font:16px/20px monospace; white-space:nowrap }
                #token {
                    margin-left:3px; padding:0 4px;
                    border-left:2px solid; border-right:2px solid;
                    margin-right:5px
                }
            </style>
            <p id="decorated"><span id="token">aa</span><span id="after">bb</span></p>
            <p id="plain-host"><span id="plain">aa</span><span id="plain-after">bb</span></p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 100f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        float LocalX(string id, string hostId) => Get(id).X - Get(hostId).X;

        Assert.True(MathF.Abs(Get("token").Width - Get("plain").Width - 12f) < 0.01f);
        Assert.True(
            MathF.Abs(LocalX("token", "decorated") - LocalX("plain", "plain-host") - 3f) < 0.01f);
        Assert.True(
            MathF.Abs(LocalX("after", "decorated") - LocalX("plain-after", "plain-host") - 20f)
            < 0.01f);
        Assert.True(
            MathF.Abs(Get("after").X - (Get("token").X + Get("token").Width) - 5f) < 0.01f);
    }

    [Fact]
    public void InlineEdgesParticipateInWrappingAndSliceOnlyOuterContinuationSides()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { width:70px; font:16px/20px monospace }
                #token { padding:0 10px; border-left:2px solid; border-right:2px solid }
            </style>
            <p><span id="token">aaaa aaaa aaaa</span></p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (200f, 100f));
        NodeId token = Id(tree, "token");
        List<Rect> fragments = laid.InlineFragments[token];

        Assert.Equal(3, fragments.Count);
        Assert.True(
            MathF.Abs(fragments[0].Width - fragments[1].Width - 12f) < 0.01f,
            $"{StyleDump.Dump(fragments)}");
        Assert.True(
            fragments[2].Width > fragments[1].Width,
            $"last continuation owns its end side: {StyleDump.Dump(fragments)}");
        Assert.All(fragments, fragment => Assert.True(fragment.Width <= 70.01f));
    }

    [Fact]
    public void EmptyDecoratedInlineHasABoxAndAdvancesFollowingText()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { font:16px/20px monospace; white-space:nowrap }
                #empty {
                    margin:0 3px; padding:2px 5px; border:1px solid;
                    border-left-width:2px; border-right-width:2px
                }
            </style>
            <p id="decorated">x<span id="empty"></span><span id="after">y</span></p>
            <p id="plain-host">x<span id="plain-after">y</span></p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (300f, 100f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("empty").Width - 14f) < 0.01f, $"{Get("empty")}");
        Assert.True(
            MathF.Abs(
                Get("empty").Height
                - laid.TextEngine.InlineFontBoxHeight(laid.Styles[Id(tree, "empty")])
                - 6f) < 0.01f);
        float decoratedAfter = Get("after").X - Get("decorated").X;
        float plainAfter = Get("plain-after").X - Get("plain-host").X;
        Assert.True(MathF.Abs(decoratedAfter - plainAfter - 20f) < 0.01f);
    }

    [Fact]
    public void InlineProvenanceMapsRepeatedMultibyteTextAcrossHardBreaks()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { width:200px; font:18px/24px sans-serif }
            </style>
            <p><span id="token">é界<br>é界</span></p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (300f, 100f));
        NodeId token = Id(tree, "token");
        List<Rect> fragments = laid.InlineFragments[token];

        Assert.Equal(2, fragments.Count);
        Assert.True(
            MathF.Abs(fragments[0].Width - fragments[1].Width) < 0.01f,
            $"{StyleDump.Dump(fragments)}");
        Assert.True(
            MathF.Abs(fragments[1].Y - fragments[0].Y - 24f) < 0.01f,
            $"{StyleDump.Dump(fragments)}");
    }

    [Fact]
    public void RelativeInlineOffsetsMoveCanonicalFragmentsInPixelsAndPercentages()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                p { width:200px; height:20px; font:16px/20px monospace }
                #pixels { position:relative; left:7px; top:5px }
                #percent { position:relative; left:10%; top:10% }
            </style>
            <p id="base-host"><span id="base">token</span></p>
            <p id="pixel-host"><span id="pixels">token</span></p>
            <p id="percent-host"><span id="percent">token</span></p>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (400f, 120f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];
        (float X, float Y) Local(string tokenId, string hostId)
        {
            Rect tokenRect = Get(tokenId);
            Rect hostRect = Get(hostId);
            return (tokenRect.X - hostRect.X, tokenRect.Y - hostRect.Y);
        }

        (float X, float Y) baseLocal = Local("base", "base-host");
        (float X, float Y) pixels = Local("pixels", "pixel-host");
        (float X, float Y) percent = Local("percent", "percent-host");

        Assert.True(MathF.Abs(pixels.X - baseLocal.X - 7f) < 0.01f, $"{baseLocal} {pixels}");
        Assert.True(MathF.Abs(pixels.Y - baseLocal.Y - 5f) < 0.01f, $"{baseLocal} {pixels}");
        Assert.True(MathF.Abs(percent.X - baseLocal.X - 20f) < 0.01f, $"{baseLocal} {percent}");
        Assert.True(MathF.Abs(percent.Y - baseLocal.Y - 2f) < 0.01f, $"{baseLocal} {percent}");
    }

    [Fact]
    public void CanonicalInlineFragmentsShapeWithTheLoadedWebfont()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                #token {
                    position:relative;
                    font-family:Fixture, sans-serif;
                    font-size:40px;
                    line-height:50px;
                    background:red
                }
            </style>
            <p><span id="token">WWWWiiii</span></p>
            """);
        NodeId token = Id(tree, "token");
        DomLayout fallback = RenderDom.LayoutDom(tree, (500f, 150f));
        DomLayout loaded = RenderDom.LayoutDomWithWebFonts(
            tree,
            (500f, 150f),
            NoIntrinsic,
            [FixtureSerifFont()]);

        Assert.True(loaded.InlineFragments.ContainsKey(token));
        Assert.NotEqual(fallback.Rects[token].Width, loaded.Rects[token].Width);
        Assert.NotEqual(fallback.Rects[token].Height, loaded.Rects[token].Height);
        Assert.Equal([loaded.Rects[token]], loaded.InlineFragments[token]);
    }

    [Fact]
    public void GeneratedPseudoContentShapesWithTheLoadedWebfont()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body,p { margin:0 }
                #token::before {
                    content:"WWWWiiii";
                    font-family:Fixture, sans-serif;
                    font-size:40px;
                    line-height:50px
                }
            </style>
            <p id="token"></p>
            """);
        NodeId token = Id(tree, "token");
        DomLayout fallback = RenderDom.LayoutDom(tree, (500f, 150f));
        DomLayout loaded = RenderDom.LayoutDomWithWebFonts(
            tree,
            (500f, 150f),
            NoIntrinsic,
            [FixtureSerifFont()]);

        Assert.True(
            loaded.WordIfcItems.ContainsKey(token),
            "generated text must retain its webfont-shaped paint item");
        Assert.NotEqual(
            fallback.TextRuns[token][0].Rect.Width,
            loaded.TextRuns[token][0].Rect.Width);
    }

    [Fact]
    public void PositionedPseudoInheritsTheHostsComputedFontMetrics()
    {
        DomTree tree = Parse(
            """
            <style>
                html { font-size:16px }
                button { font-size:.875rem; line-height:1.5 }
                button::after {
                    content:attr(text); position:absolute; inset:1px;
                }
            </style>
            <button id="cta" text="Get Started">Get Started</button>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        NodeId cta = Id(tree, "cta");
        LayoutStyle host = laid.Styles[cta];
        LayoutStyle pseudo = host.AfterPseudo!;

        Assert.Equal(14f, host.FontSize);
        Assert.Equal(host.FontSize, pseudo.FontSize);
        Assert.Equal(host.FontFamily, pseudo.FontFamily);
        Assert.Equal(host.FontWeight, pseudo.FontWeight);
        Assert.Equal(host.LineHeight, pseudo.LineHeight);
    }

    [Fact]
    public void GeneratedPseudoGridCalcUsesComputedFontAndViewportContext()
    {
        DomTree tree = Parse(
            """
            <style>
                html { font-size:16px }
                #host { font-size:20px }
                #host::before {
                    content:""; display:grid;
                    grid-template-columns:calc(1em + 1rem + 10vw);
                }
            </style>
            <div id="host"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1000f, 400f));
        NodeId host = Id(tree, "host");
        LayoutStyle pseudo = laid.Styles[host].BeforePseudo!;
        var calc = (GridCalcExpression)pseudo.GridCalcExpressions![0][0];

        Assert.True(MathF.Abs(ComputedStyle.ResolveGridCalc(calc.Handle, 1000f) - 136f) < 0.01f);
    }

    [Fact]
    public void PositionedPseudoDoesNotWrapShrinkToFitHostText()
    {
        DomTree tree = Parse(
            """
            <style>
                html, body { margin:0 }
                #host {
                    position:absolute; width:max-content;
                    padding:12px 24px; border:0;
                    font-size:14px; font-weight:600; color:transparent;
                }
                #host::after {
                    content:attr(text); position:absolute; inset:1px;
                    display:flex; align-items:center; justify-content:center;
                    color:black; background:white;
                }
            </style>
            <button id="host" text="Learn more">Learn more</button>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 300f));
        NodeId host = Id(tree, "host");
        Rect rect = laid.Rects[host];

        Assert.True(
            rect.Width is >= 120f and <= 130f,
            $"max-content width must include the shaped inter-word advance: {rect}");
        Assert.Equal(40f, rect.Height);
    }

    [Fact]
    public void VariableFontPropertiesInheritAndResetThroughElementsAndPseudos()
    {
        DomTree tree = Parse(
            """
            <style>
                #parent {
                    font-optical-sizing:none;
                    font-variation-settings:"opsz" 20, "wght" 650;
                }
                #parent::before { content:"before" }
                #reset {
                    font-optical-sizing:initial;
                    font-variation-settings:normal;
                }
                #reset::after {
                    content:"after";
                    font-optical-sizing:inherit;
                    font-variation-settings:"wght" 300;
                }
            </style>
            <div id="parent">
                <span id="inherited"></span>
                <span id="reset"><b id="reset-child"></b></span>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        LayoutStyle Get(string selector) => laid.Styles[tree.QuerySelector(selector)!.Value];
        LayoutStyle parent = Get("#parent");
        LayoutStyle inherited = Get("#inherited");
        Assert.Equal(FontOpticalSizing.None, parent.FontOpticalSizing);
        Assert.Equal(parent.FontOpticalSizing, inherited.FontOpticalSizing);
        Assert.Equal(
            StyleDump.Dump(parent.FontVariationSettings),
            StyleDump.Dump(inherited.FontVariationSettings));
        LayoutStyle before = parent.BeforePseudo!;
        Assert.Equal(parent.FontOpticalSizing, before.FontOpticalSizing);
        Assert.Equal(
            StyleDump.Dump(parent.FontVariationSettings),
            StyleDump.Dump(before.FontVariationSettings));

        LayoutStyle reset = Get("#reset");
        LayoutStyle resetChild = Get("#reset-child");
        Assert.Equal(FontOpticalSizing.Auto, reset.FontOpticalSizing);
        Assert.Equal([], reset.FontVariationSettings);
        Assert.Equal(reset.FontOpticalSizing, resetChild.FontOpticalSizing);
        Assert.Equal(
            StyleDump.Dump(reset.FontVariationSettings),
            StyleDump.Dump(resetChild.FontVariationSettings));
        LayoutStyle after = reset.AfterPseudo!;
        Assert.Equal(reset.FontOpticalSizing, after.FontOpticalSizing);
        Assert.Equal(
            new List<FontVariationSetting> { new("wght", 300f) },
            after.FontVariationSettings);
    }

    [Fact]
    public void NativeSelectSizesForItsWidestOption()
    {
        DomTree tree = Parse(
            """
            <select id="language">
                <option selected>En</option>
                <option>Brazilian Portuguese</option>
            </select>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 200f));
        NodeId select = Id(tree, "language");
        Rect rect = laid.Rects[select];

        Assert.True(rect.Width > 120f, $"widest option should set width: {rect}");
        Assert.True(
            rect.Height is >= 18f and <= 22f,
            $"closed native select should have one-line control height: {rect}");
    }

    [Fact]
    public void DisplayContentsDescendantsResolveAsNamedGridAreaItems()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #grid{
                    display:grid;width:400px;height:100px;
                    grid-template-columns:100px 300px;
                    grid-template-rows:40px 60px;
                    grid-template-areas:"nav head" "nav main";
                }
                #contents{display:contents}
                #nav{grid-area:nav}#head{grid-area:head}#main{grid-area:main}
            </style>
            <div id="grid"><div id="contents">
                <nav id="nav"></nav><header id="head"></header><article id="main"></article>
            </div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.Equal(new Rect(0f, 0f, 100f, 100f), Get("nav"));
        Assert.Equal(new Rect(100f, 0f, 300f, 40f), Get("head"));
        Assert.Equal(new Rect(100f, 40f, 300f, 60f), Get("main"));
    }

    [Fact]
    public void DisplayContentsGeneratedPseudoRemainsANamedGridItem()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0}
                #grid{
                    display:grid;width:400px;height:50px;
                    grid-template-columns:100px 300px;
                    grid-template-rows:50px;
                    grid-template-areas:"marker body";
                }
                #contents{display:contents}
                #contents::before{content:"";display:block;grid-area:marker}
                #body{grid-area:body}
            </style>
            <div id="grid"><div id="contents"><article id="body"></article></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 300f));
        NodeId contents = Id(tree, "contents");
        NodeId body = Id(tree, "body");
        GeneratedBox generated = laid.GeneratedBoxes.First(box =>
            box.Host == contents && box.Kind == GeneratedBoxKind.Before);

        Assert.Equal(new Rect(0f, 0f, 100f, 50f), generated.Rect);
        Assert.Equal(new Rect(100f, 0f, 300f, 50f), laid.Rects[body]);
    }

    [Fact]
    public void GridAutoColumnsSizeSuccessiveImplicitColumnTracks()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                #grid {
                    display:grid;
                    width:300px;
                    grid-template-columns:100px;
                    grid-auto-columns:50px;
                    grid-auto-flow:column;
                }
                .item { height:20px }
            </style>
            <div id="grid">
                <div id="first" class="item"></div>
                <div id="second" class="item"></div>
                <div id="third" class="item"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 200f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.Equal(new Rect(0f, 0f, 100f, 20f), Get("first"));
        Assert.Equal(new Rect(100f, 0f, 50f, 20f), Get("second"));
        Assert.Equal(new Rect(150f, 0f, 50f, 20f), Get("third"));
    }

    [Fact]
    public void GridAutoRowsSizeSuccessiveImplicitRowTracks()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                #grid {
                    display:grid;
                    width:100px;
                    height:300px;
                    grid-template-columns:100px;
                    grid-template-rows:100px;
                    grid-auto-rows:50px;
                    grid-auto-flow:row;
                }
            </style>
            <div id="grid">
                <div id="first"></div>
                <div id="second"></div>
                <div id="third"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 400f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.Equal(new Rect(0f, 0f, 100f, 100f), Get("first"));
        Assert.Equal(new Rect(0f, 100f, 100f, 50f), Get("second"));
        Assert.Equal(new Rect(0f, 150f, 100f, 50f), Get("third"));
    }

    [Fact]
    public void GridAutoTrackInheritUsesTheParentComputedLists()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                #parent { grid-auto-columns:60px;grid-auto-rows:70px }
                #grid {
                    display:grid;
                    grid-template-columns:100px;
                    grid-auto-columns:inherit;
                    grid-auto-rows:inherit;
                    grid-auto-flow:column;
                }
                #grid > div { height:20px }
            </style>
            <div id="parent">
                <div id="grid"><div></div><div id="implicit"></div></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 300f));
        NodeId grid = Id(tree, "grid");
        NodeId implicitItem = Id(tree, "implicit");

        Assert.Equal(100f, laid.Rects[implicitItem].X);
        Assert.Equal(60f, laid.Rects[implicitItem].Width);
        Assert.Single(laid.Styles[grid].GridAutoColumns);
        Assert.Single(laid.Styles[grid].GridAutoRows);
        Assert.False(laid.Styles[grid].GridAutoColumnsInherit);
        Assert.False(laid.Styles[grid].GridAutoRowsInherit);
    }

    [Fact]
    public void TextBreakModesMatchChromiumFixedWidthGeometry()
    {
        // Chromium 145 at DPR 1 with Liberation Sans produces the same
        // 20/40px line boxes. `break-word` and `anywhere` are emergency
        // wrapping modes; `break-all` adds typographic-letter opportunities.
        // keep-all must retain ordinary Latin word/space behavior.
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                .fixed { width:80px;font:16px/20px "Liberation Sans" }
                #normal { overflow-wrap:normal }
                #break-word { overflow-wrap:break-word }
                #anywhere { overflow-wrap:anywhere }
                #break-all { word-break:break-all }
                #keep-all { word-break:keep-all }
                #legacy { word-break:break-word }
            </style>
            <div id="normal" class="fixed">abcdefghijklmnop</div>
            <div id="break-word" class="fixed">abcdefghijklmnop</div>
            <div id="anywhere" class="fixed">abcdefghijklmnop</div>
            <div id="break-all" class="fixed">abcdefghijklmnop</div>
            <div id="latin-normal" class="fixed">alpha beta gamma</div>
            <div id="keep-all" class="fixed">alpha beta gamma</div>
            <div id="legacy" class="fixed">abcdefghijklmnop</div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 500f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.Equal(new Rect(0f, 0f, 80f, 20f), Get("normal"));
        Assert.Equal(new Rect(0f, 20f, 80f, 40f), Get("break-word"));
        Assert.Equal(new Rect(0f, 60f, 80f, 40f), Get("anywhere"));
        Assert.Equal(new Rect(0f, 100f, 80f, 40f), Get("break-all"));
        Assert.Equal(40f, Get("latin-normal").Height);
        Assert.Equal(Get("latin-normal").Height, Get("keep-all").Height);
        Assert.Equal(40f, Get("legacy").Height);
    }

    [Fact]
    public void TextBreakInheritanceAliasAndCssWideValuesMatchChromium()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                .parent { overflow-wrap:anywhere;word-break:break-all;font:16px/20px "Liberation Sans" }
                .fixed { width:80px }
                #initial { overflow-wrap:initial;word-break:initial }
                #unset { overflow-wrap:unset;word-break:unset }
                #inherit { overflow-wrap:inherit;word-break:inherit }
                #revert { overflow-wrap:revert;word-break:revert }
                #revert-layer { overflow-wrap:revert-layer;word-break:revert-layer }
                #alias { word-wrap:anywhere;word-break:normal }
                #inherited::before { content:"";display:none }
            </style>
            <section class="parent">
                <div id="inherited" class="fixed">abcdefghijklmnop</div>
                <div id="initial" class="fixed">abcdefghijklmnop</div>
                <div id="unset" class="fixed">abcdefghijklmnop</div>
                <div id="inherit" class="fixed">abcdefghijklmnop</div>
                <div id="revert" class="fixed">abcdefghijklmnop</div>
                <div id="revert-layer" class="fixed">abcdefghijklmnop</div>
                <div id="alias" class="fixed">abcdefghijklmnop</div>
            </section>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 500f));
        LayoutStyle Style(string id) => laid.Styles[Id(tree, id)];
        float Height(string id) => laid.Rects[Id(tree, id)].Height;

        Assert.Equal(OverflowWrap.Anywhere, Style("inherited").OverflowWrap);
        Assert.Equal(WordBreak.BreakAll, Style("inherited").WordBreak);
        Assert.Equal(40f, Height("inherited"));
        Assert.Equal(
            OverflowWrap.Anywhere, Style("inherited").BeforePseudo!.OverflowWrap);
        Assert.Equal(WordBreak.BreakAll, Style("inherited").BeforePseudo!.WordBreak);

        Assert.Equal(OverflowWrap.Normal, Style("initial").OverflowWrap);
        Assert.Equal(WordBreak.Normal, Style("initial").WordBreak);
        Assert.Equal(20f, Height("initial"));
        foreach (string id in new[] { "unset", "inherit", "revert", "revert-layer" })
        {
            Assert.Equal(OverflowWrap.Anywhere, Style(id).OverflowWrap);
            Assert.Equal(WordBreak.BreakAll, Style(id).WordBreak);
            Assert.Equal(40f, Height(id));
        }

        Assert.Equal(OverflowWrap.Anywhere, Style("alias").OverflowWrap);
        Assert.Equal(WordBreak.Normal, Style("alias").WordBreak);
        Assert.Equal(40f, Height("alias"));
    }

    [Fact]
    public void AnywhereButNotBreakWordReducesMinContent()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                .grid { display:grid;grid-template-columns:min-content;font:16px/20px "Liberation Sans" }
                .cell { display:block }
                #normal { overflow-wrap:normal }
                #break-word { overflow-wrap:break-word }
                #anywhere { overflow-wrap:anywhere }
                #break-all { word-break:break-all }
            </style>
            <div class="grid"><div id="normal" class="cell">abcdefghijklmnop</div></div>
            <div class="grid"><div id="break-word" class="cell">abcdefghijklmnop</div></div>
            <div class="grid"><div id="anywhere" class="cell">abcdefghijklmnop</div></div>
            <div class="grid"><div id="break-all" class="cell">abcdefghijklmnop</div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 1000f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        Assert.Equal(125f, Width("normal"));
        Assert.Equal(125f, Width("break-word"));
        Assert.Equal(14f, Width("anywhere"));
        Assert.Equal(14f, Width("break-all"));
    }

    [Fact]
    public void InlineDescendantBreakPoliciesStayAtTheirStyleBoundaries()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                .fixed { width:80px;font:16px/20px "Liberation Sans" }
                .normal { word-break:normal;overflow-wrap:normal }
                .break-all { word-break:break-all }
                .anywhere { overflow-wrap:anywhere }
            </style>
            <div id="child-break-all" class="fixed normal"><span class="break-all">abcdefghijklmnop</span></div>
            <div id="child-normal" class="fixed break-all"><span class="normal">abcdefghijklmnop</span></div>
            <div id="child-anywhere" class="fixed normal"><span class="anywhere">abcdefghijklmnop</span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 300f));
        float Height(string id) => laid.Rects[Id(tree, id)].Height;

        Assert.Equal(40f, Height("child-break-all"));
        Assert.Equal(20f, Height("child-normal"));
        Assert.Equal(40f, Height("child-anywhere"));
    }

    [Fact]
    public void BreakAllKeepsUax14PunctuationPairsInMinContent()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                .grid { display:grid;grid-template-columns:min-content;font:16px/20px "Liberation Sans" }
                #break-all { word-break:break-all }
                #anywhere { overflow-wrap:anywhere }
            </style>
            <div class="grid"><span id="break-all">(ab)</span></div>
            <div class="grid"><span id="anywhere">(ab)</span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 300f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(Get("break-all").Width > Get("anywhere").Width);
        Assert.Equal(40f, Get("break-all").Height);
        Assert.Equal(80f, Get("anywhere").Height);
    }

    [Fact]
    public void BreakAllUsesBlinkPunctuationPairSemantics()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                .grid { display:grid;grid-template-columns:min-content;font:20px/24px monospace }
                .break { word-break:break-all }
            </style>
            <div class="grid"><span id="close-normal">)a</span></div>
            <div class="grid"><span id="close-break" class="break">)a</span></div>
            <div class="grid"><span id="closing-normal">a)</span></div>
            <div class="grid"><span id="closing-break" class="break">a)</span></div>
            <div class="grid"><span id="slash-normal">a/b</span></div>
            <div class="grid"><span id="slash-break" class="break">a/b</span></div>
            <div class="grid"><span id="hyphen-normal">a-b</span></div>
            <div class="grid"><span id="hyphen-break" class="break">a-b</span></div>
            <div class="grid"><span id="bar-normal">a|b</span></div>
            <div class="grid"><span id="bar-break" class="break">a|b</span></div>
            <div class="grid"><span id="plus-normal">a+b</span></div>
            <div class="grid"><span id="plus-break" class="break">a+b</span></div>
            <div class="grid"><span id="prefix-normal">$a</span></div>
            <div class="grid"><span id="prefix-break" class="break">$a</span></div>
            <div class="grid"><span id="postfix-normal">a%</span></div>
            <div class="grid"><span id="postfix-break" class="break">a%</span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 1000f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        foreach (string stem in new[] { "close", "slash", "hyphen", "bar", "plus" })
        {
            Assert.True(
                Width($"{stem}-break") < Width($"{stem}-normal"),
                $"break-all should expose a narrower opportunity for {stem}");
        }

        foreach (string stem in new[] { "closing", "prefix", "postfix" })
        {
            Assert.True(
                Width($"{stem}-break") == Width($"{stem}-normal"),
                $"break-all must retain the prohibited boundary for {stem}");
        }
    }

    [Fact]
    public void KeepAllSuppressesMixedCjkLatinWordBoundaries()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body { margin:0 }
                .grid { display:grid;grid-template-columns:min-content;font:16px/20px "Liberation Sans" }
                #keep { word-break:keep-all }
            </style>
            <div class="grid"><span id="normal">標（準）萬ab國.碼</span></div>
            <div class="grid"><span id="keep">標（準）萬ab國.碼</span></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (500f, 500f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        Assert.True(Width("keep") > Width("normal"));
    }

    [Fact]
    public void TextControlSizeAttributeMeasuresTheFaceAverageCharacterWidth()
    {
        // Chromium sizes a text control from its face, not from a fixed fraction of the font
        // size: `size` columns of the OS/2 average character width, plus whatever the head
        // bounding box costs over one of them. Measured against Chromium 141 with this exact
        // face loaded as a web font, the content box is `8 * size + 8` at 13px.
        DomTree tree = Parse(
            """
            <style>
                html, body { margin: 0 }
                input { font: 13px "Liberation Sans"; border: 0; padding: 0 }
                div { display: inline-block; width: max-content }
            </style>
            <div><input id="s1" size="1"></div>
            <div><input id="s2" size="2"></div>
            <div><input id="s10" size="10"></div>
            <div><input id="s20" size="20"></div>
            <div><input id="s50" size="50"></div>
            <div><input id="dflt"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        Assert.Equal(16f, Width("s1"), 0.5f);
        Assert.Equal(24f, Width("s2"), 0.5f);
        Assert.Equal(88f, Width("s10"), 0.5f);
        Assert.Equal(168f, Width("s20"), 0.5f);
        Assert.Equal(408f, Width("s50"), 0.5f);

        // No `size` attribute is HTML's default of 20.
        Assert.Equal(Width("s20"), Width("dflt"), 0.5f);
    }

    [Fact]
    public void TextControlWithPercentageWidthStillContributesItsSizeBasedWidth()
    {
        // A percentage width that cannot be resolved behaves as `auto` for an intrinsic
        // contribution (CSS Sizing 3 5.2.2), and a control has no child boxes to be measured
        // from, so the size-based box is what it has to contribute. Publishing the intrinsic
        // size only into an `auto` width left every `width: 100%` text field reporting its
        // padding - 8px - as its max-content width.
        DomTree tree = Parse(
            """
            <style>
                html, body { margin: 0 }
                input { font: 13px "Liberation Sans" }
                .shrink { display: inline-block; width: max-content }
            </style>
            <div class="shrink"><input id="pct" style="width:100%"></div>
            <div class="shrink"><input id="auto"></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // 8 * 20 + 8 of content, plus the UA control's 2px border and 2px padding per side.
        Assert.Equal(176f, Width("auto"), 0.5f);
        Assert.Equal(Width("auto"), Width("pct"), 0.5f);
    }

    [Fact]
    public void TextControlSizeDoesNotRaiseAFlexItemAutomaticMinimumSize()
    {
        // Chromium contributes the size-based width to a max-content pass only: a text field
        // shrinks below the box its `size` attribute asks for rather than flooring the flex
        // item that holds it. A field with a resolvable width is not affected either way, so
        // only the percentage case can tell the two passes apart.
        DomTree tree = Parse(
            """
            <style>
                html, body { margin: 0 }
                input { font: 13px "Liberation Sans" }
                .row { display: flex; width: 1000px }
                .holder { flex: 0 1 500px }
                .filler { flex: 0 1 4000px; min-width: 0 }
            </style>
            <div class="row">
              <div class="holder" id="holder"><input style="width:100%"></div>
              <div class="filler"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));

        // 500 and 4000 shrink into 1000, so an unfloored holder lands on 1000/9 = 111.11.
        Assert.Equal(111.11f, laid.Rects[Id(tree, "holder")].Width, 1f);
    }

    [Theory]
    [InlineData("<div class=\"nw\" style=\"width:100%\">File Processing Queue Long</div>", "")]
    [InlineData("<div class=\"nw\" style=\"width:50%\">File Processing Queue Long</div>", "")]
    [InlineData(
        "<div class=\"nw\" style=\"width:100%;box-sizing:border-box\">File Processing Queue Long</div>",
        "")]
    [InlineData(
        "<div><div class=\"nw\" style=\"width:100%\">File Processing Queue Long</div></div>",
        "")]
    [InlineData(
        "<div class=\"nw\" style=\"width:100%;display:inline-flex\">File Processing Queue Long</div>",
        "")]
    [InlineData(
        "<div class=\"nw\" style=\"width:100%;display:flex\">File Processing Queue Long</div>",
        "")]
    [InlineData(
        "<div class=\"nw\" style=\"width:100%;min-width:80px\">File Processing Queue Long</div>",
        "")]
    [InlineData(
        "<div class=\"nw\" style=\"width:100%\">File Processing Queue Long</div>",
        "display:flex;flex-direction:column")]
    [InlineData(
        "<div class=\"nw\" style=\"width:100%\">File Processing Queue Long</div>",
        "display:flex")]
    public void PercentageDescendantStillFloorsAFlexItemAutomaticMinimumSize(
        string childHtml,
        string itemStyle)
    {
        // A cyclic percentage inline size is neutralized to a definite `0px` for the intrinsic
        // pass, which used to leave the item's content-based automatic minimum size measuring a
        // collapsed child - the item then shrank to 29 where Chromium floors it at the child's
        // min-content. Chromium reports 164.047 for every one of these shapes, and 164.047 for
        // the `width: auto` control below; the engine's own text metrics put that ~1px higher,
        // so the assertion is a band around Chromium's value plus exact agreement with the
        // control.
        DomTree tree = Parse(
            $$"""
            <style>
                html, body { margin: 0; padding: 0; font: 13px "Liberation Sans" }
                .outer { display: flex; width: 600px }
                .item { flex: 0 1 200px }
                .rest { flex: 0 1 4000px; min-width: 0 }
                .nw { white-space: nowrap }
            </style>
            <div class="outer">
              <div class="item" id="control"><div class="nw">File Processing Queue Long</div></div>
              <div class="rest"></div>
            </div>
            <div class="outer">
              <div class="item" id="item" style="{{itemStyle}}">{{childHtml}}</div>
              <div class="rest"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1600f, 900f));

        float control = laid.Rects[Id(tree, "control")].Width;
        float item = laid.Rects[Id(tree, "item")].Width;

        Assert.Equal(164.047f, control, 1.5f);
        Assert.Equal(control, item, 0.01f);
    }

    [Fact]
    public void TextareaIntrinsicBoxComesFromRowsAndCols()
    {
        // #685: an empty textarea must keep a real control box instead of
        // laying out as a plain block. Chromium lays cols=20/rows=2 out as a
        // 181x36 border box: 20 columns of the face's average character width,
        // the 15px scrollbar gutter, and one 15px control line per row.
        //
        // The fixture names no family, so the face is the UA's `monospace`, and
        // the reference has to be measured against the one this engine embeds -
        // Chromium picks its own from fontconfig and reports 182 with the
        // DejaVu Sans Mono it finds on a typical Linux host. Measured on
        // Chromium 141 with `Assets/liberation-mono.ttf` served as a web font:
        // 181 at cols 20, and 29 / 37 / 61 / 421 at cols 1 / 2 / 5 / 50.
        DomTree tree = Parse(
            """
            <style>html, body { margin: 0 }</style>
            <div><textarea id="plain"></textarea></div>
            <div><textarea id="rows8" rows="8"></textarea></div>
            <div><textarea id="cssheight" style="height: 36px"></textarea></div>
            <div><textarea id="invalid-rows" rows="0"></textarea></div>
            <div><textarea id="cols1" cols="1"></textarea></div>
            <div><textarea id="cols5" cols="5"></textarea></div>
            <div><textarea id="cols50" cols="50"></textarea></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1280f, 720f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Rect plain = Get("plain");
        Assert.True(MathF.Abs(plain.Width - 181f) < 0.5f, $"{plain.Width}");
        Assert.True(MathF.Abs(plain.Height - 36f) < 0.5f, $"{plain.Height}");

        Rect rows8 = Get("rows8");
        Assert.True(MathF.Abs(rows8.Width - 181f) < 0.5f);
        Assert.True(MathF.Abs(rows8.Height - 126f) < 0.5f, $"{rows8.Height}");

        // Author height wins over the rows-derived intrinsic height, and the
        // control is border-box, so 36px stays the border-box height.
        Rect cssHeight = Get("cssheight");
        Assert.True(MathF.Abs(cssHeight.Height - 36f) < 0.5f, $"{cssHeight.Height}");

        // rows/cols are limited to positive numbers; anything else falls
        // back to the HTML defaults (rows=2).
        Rect invalid = Get("invalid-rows");
        Assert.True(MathF.Abs(invalid.Height - 36f) < 0.5f, $"{invalid.Height}");

        // Away from cols=20 the fit is per-column plus one constant gutter, so a
        // calibrated constant and the real curve part company: 1 / 5 / 50 columns.
        Assert.True(MathF.Abs(Get("cols1").Width - 29f) < 0.5f, $"{Get("cols1").Width}");
        Assert.True(MathF.Abs(Get("cols5").Width - 61f) < 0.5f, $"{Get("cols5").Width}");
        Assert.True(MathF.Abs(Get("cols50").Width - 421f) < 0.5f, $"{Get("cols50").Width}");

        // The control is an atomic inline-block, not a stretched block.
        LayoutStyle style = laid.Styles[Id(tree, "plain")];
        Assert.Equal(Display.Inline, style.Display);
        Assert.True(style.IsInlineBlock);
    }

    /// <summary>
    /// A flex item's flex base size comes from its own `flex-basis`/`width`, never from the
    /// size a previous pass gave it. A percentage-width descendant defers the item's inline
    /// size to a second flex pass, and that pass used to re-shrink the already-shrunk width.
    /// Chromium: 212.016 / 6.781 / 1221.203 (inner bases 250/8/1440, deficit 258).
    /// </summary>
    [Fact]
    public void ShrunkFlexItemWithPercentageChildIsNotShrunkTwice()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0}</style>
            <div style="display:flex;width:1440px">
              <div id="a" style="width:250px;flex:0 1 auto"><div id="pct" style="width:100%"></div></div>
              <div id="b" style="width:8px;flex:0 1 auto"></div>
              <div id="c" style="width:100%;min-width:0;flex:0 1 auto"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1440f, 720f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Rect a = Get("a");
        Rect b = Get("b");
        Rect c = Get("c");
        Assert.True(MathF.Abs(a.Width - 212.016f) < 0.5f, $"{a.Width}");
        Assert.True(MathF.Abs(b.Width - 6.781f) < 0.5f, $"{b.Width}");
        Assert.True(MathF.Abs(c.Width - 1221.203f) < 0.5f, $"{c.Width}");

        // Obscura rounds used rects to integers, so these land on 212 / 7 / 1221.
        // The descendant percentage still resolves against the item's used inline size.
        Assert.True(MathF.Abs(Get("pct").Width - a.Width) < 0.5f, $"{Get("pct").Width}");
    }

    /// <summary>
    /// Same shape with border-box padding on the shrinking item. The scaled flex shrink
    /// factor uses the *inner* flex base size (250 - 24 = 226), so Chromium lands on
    /// 215.172 rather than 212.016.
    /// </summary>
    [Fact]
    public void ShrunkFlexItemWithPaddingAndPercentageChildIsNotShrunkTwice()
    {
        DomTree tree = Parse(
            """
            <style>html,body{margin:0}</style>
            <div style="display:flex;width:1440px">
              <div id="a" style="width:250px;padding:16px 12px;box-sizing:border-box;flex:0 1 auto"><div id="pct" style="width:100%"></div></div>
              <div id="b" style="width:8px;flex:0 1 auto"></div>
              <div id="c" style="width:100%;min-width:0;flex:0 1 auto"></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (1440f, 720f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Rect a = Get("a");
        Assert.True(MathF.Abs(a.Width - 215.172f) < 0.5f, $"{a.Width}");
        Assert.True(MathF.Abs(Get("pct").Width - (a.Width - 24f)) < 0.5f, $"{Get("pct").Width}");
    }

    private const string TableFloorFixture = """
        <style>
            html,body{margin:0;padding:0}
            table{border-spacing:0;border-collapse:collapse}
            td{padding:0}
            .outer{width:400px}
        </style>
        <div class=outer>
          <table id=wide style="width:100%"><tr><td><div style="width:461px;height:6px"></div></td></tr></table>
        </div>
        <div class=outer>
          <table id=narrow style="width:100%"><tr><td><div style="width:100px;height:6px"></div></td></tr></table>
        </div>
        <div class=outer>
          <table id=twocol style="width:100%">
            <tr><td><div style="width:461px;height:6px"></div></td>
                <td><div style="width:120px;height:6px"></div></td></tr>
          </table>
        </div>
        """;

    /// <summary>
    /// CSS 2.1 17.5.2: a table's used width is the greater of its specified width and what its
    /// columns need, so a `width: 100%` that resolves narrower than the content overflows its
    /// containing block instead of squeezing the columns. Chromium: 461 / 400 / 581.
    /// </summary>
    [Fact]
    public void PercentageWidthTableIsFlooredByItsMinContentWidth()
    {
        DomTree tree = Parse(TableFloorFixture);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 400f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("wide").Width - 461f) < 0.5f, $"wide {Get("wide").Width}");
        Assert.True(MathF.Abs(Get("twocol").Width - 581f) < 0.5f, $"twocol {Get("twocol").Width}");
    }

    /// <summary>The contrasting case: content that fits leaves the percentage alone.</summary>
    [Fact]
    public void PercentageWidthTableThatFitsKeepsItsContainingBlockWidth()
    {
        DomTree tree = Parse(TableFloorFixture);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 400f));

        Rect narrow = laid.Rects[Id(tree, "narrow")];
        Assert.True(MathF.Abs(narrow.Width - 400f) < 0.5f, $"narrow {narrow.Width}");
    }

    /// <summary>
    /// An inline-block with a definite width lays its contents out in a block formatting
    /// context: a block-level child keeps its own definite width and overflows rather than
    /// being shrunk, and an auto-width one still fills. Chromium: 461 / 461 / 400.
    /// </summary>
    [Fact]
    public void DefiniteWidthInlineBlockDoesNotShrinkItsBlockChildren()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0;padding:0}
                table{border-spacing:0;border-collapse:collapse}
                td{padding:0}
                .outer{width:400px}
                .host{display:inline-block;width:100%}
            </style>
            <div class=outer><div class=host><div id=fixed style="width:461px;height:6px"></div></div></div>
            <div class=outer><div class=host>
              <table id=auto style="width:auto"><tr><td><div style="width:461px;height:6px"></div></td></tr></table>
            </div></div>
            <div class=outer><div class=host><div id=fill style="height:6px"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 400f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(MathF.Abs(Get("fixed").Width - 461f) < 0.5f, $"fixed {Get("fixed").Width}");
        Assert.True(MathF.Abs(Get("auto").Width - 461f) < 0.5f, $"auto {Get("auto").Width}");
        Assert.True(MathF.Abs(Get("fill").Width - 400f) < 0.5f, $"fill {Get("fill").Width}");
    }

    /// <summary>
    /// The Code Diff reduction: a table inside a horizontally scrollable box takes its
    /// max-content width and overflows the scrollport, while one whose content fits does not.
    /// Chromium, with the same fixture at 13px monospace: the nowrap table is wider than its
    /// 300px scrollport, the wrappable one is exactly 300.
    /// </summary>
    [Fact]
    public void TableInAScrollableBoxTakesItsContentWidth()
    {
        DomTree tree = Parse(
            """
            <style>
                html,body{margin:0;padding:0}
                table{border-spacing:0;border-collapse:collapse;width:100%}
                td{padding:0}
                .box{width:300px;overflow-x:scroll;overflow-y:hidden;display:inline-block}
                .cell{display:inline-block;height:14px}
            </style>
            <div class=box><table id=nowrap><tr><td>
              <span class=cell style="white-space:nowrap"><i style="display:inline-block;width:900px;height:6px"></i></span>
            </td></tr></table></div>
            <div class=box><table id=fits><tr><td>
              <span class=cell><i style="display:inline-block;width:90px;height:6px"></i></span>
            </td></tr></table></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 400f));
        Rect Get(string id) => laid.Rects[Id(tree, id)];

        Assert.True(Get("nowrap").Width > 880f, $"nowrap {Get("nowrap").Width}");
        Assert.True(MathF.Abs(Get("fits").Width - 300f) < 1f, $"fits {Get("fits").Width}");
    }

    [Fact]
    public void APinnedCyclicFlexItemFollowsAReservedScrollbarGutter()
    {
        // F35's residual. `PinFlexItems` writes a cyclic flex item's *used* main size back as a
        // definite length and freezes its flex factors, so nothing re-derives it; the scrollbar
        // gutter is reserved afterwards. Where the row flex container sits inside the scroll
        // container the pin is stale, and everything below it follows. Values measured against
        // Chromium 141 driven over CDP - Playwright launches with `--hide-scrollbars`, under
        // which nothing is reserved and every box here is the control's width.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              ::-webkit-scrollbar { width:9px; height:9px }
              .sc { width:400px; height:100px; overflow-y:auto; overflow-x:hidden }
              .ns { width:400px; height:100px; overflow:hidden }
              .row { display:flex }
              .item { flex:1 1 auto }
              .cont { padding:2px }
              .card { display:block; width:calc(100% - 4px); box-sizing:border-box; height:10px }
              .pct { display:block; width:100%; height:10px }
              u { display:block; height:400px }
            </style>
            <div class="sc"><div class="row"><div id="in" class="item">
              <div class="cont"><i id="a" class="card"></i><i id="b" class="pct"></i></div>
            </div></div><u></u></div>
            <div class="ns"><div class="row"><div id="out" class="item">
              <div class="cont"><i id="c" class="card"></i><i id="d" class="pct"></i></div>
            </div></div><u></u></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 600f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // The scrollport is 400 - 9 = 391, so the item is 391, the container's content box 387
        // and the card 383.
        Assert.True(MathF.Abs(Width("in") - 391f) < 0.01f, $"in: {Width("in")}");
        Assert.True(MathF.Abs(Width("a") - 383f) < 0.01f, $"a: {Width("a")}");
        Assert.True(MathF.Abs(Width("b") - 387f) < 0.01f, $"b: {Width("b")}");

        // Control: the same subtree in a box that reserves nothing.
        Assert.True(MathF.Abs(Width("out") - 400f) < 0.01f, $"out: {Width("out")}");
        Assert.True(MathF.Abs(Width("c") - 392f) < 0.01f, $"c: {Width("c")}");
        Assert.True(MathF.Abs(Width("d") - 396f) < 0.01f, $"d: {Width("d")}");
    }

    [Fact]
    public void ACyclicPercentageIsMeasuredAsAutoOnlyWhereTheItemIsContentSized()
    {
        // CSS Sizing 3 5.2.2: a cyclic percentage behaves as `auto` for intrinsic contribution.
        // A content-sized flex item is measured from exactly the content the neutralization
        // touches, so it gets `auto`; an item sized from a declared width or basis is not
        // measured from its content, and carrying `auto` there is what made `width: 100%`
        // buttons shrink-wrap. Both halves are asserted here so a change that widens one
        // breaks the other. Values measured against Chromium 141 over CDP.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              .row { display:flex; width:400px }
              .p { flex:1 1 auto }
              .pct { display:block; width:100% }
              .l1 { display:block; width:40px; height:10px }
              .l2 { display:block; width:160px; height:10px }
              .side { width:1px; min-width:0; flex-grow:1 }
              .btn { display:inline-block; width:100%; border:1px solid #000; box-sizing:border-box; height:20px }
            </style>
            <div class="row">
              <div id="p1" class="p"><div class="pct"><i class="l1"></i></div></div>
              <div id="p2" class="p"><div class="pct"><i class="l2"></i></div></div>
            </div>
            <div class="row">
              <div id="stf"><div class="pct"><i class="l1"></i></div></div><div class="p"></div>
            </div>
            <div class="row">
              <div id="side" class="side"><span id="btn" class="btn"><i class="l1"></i></span></div>
            </div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (600f, 600f));
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // Two `flex: 1 1 auto` panels whose only content is percentage-sized: the bases are the
        // leaves' 40 and 160, and the 200 of free space is split evenly between them. A zero
        // base for both would split the row 200/200.
        Assert.True(MathF.Abs(Width("p1") - 140f) < 0.01f, $"p1: {Width("p1")}");
        Assert.True(MathF.Abs(Width("p2") - 260f) < 0.01f, $"p2: {Width("p2")}");

        // A shrink-to-fit block in a flex row gets its percentage child's contribution.
        Assert.True(MathF.Abs(Width("stf") - 40f) < 0.01f, $"stf: {Width("stf")}");

        // The contrast: an item whose width comes from a declaration keeps it, and the
        // `width: 100%` atomic inline inside fills it rather than shrink-wrapping to 40.
        Assert.True(MathF.Abs(Width("side") - 400f) < 0.01f, $"side: {Width("side")}");
        Assert.True(MathF.Abs(Width("btn") - 400f) < 0.01f, $"btn: {Width("btn")}");
    }

    [Fact]
    public void AnAutoRowIsSizedFromALoneItemWhoseWidthWasMeasuredFirst()
    {
        // A grid item whose width comes from stretch alignment is measured horizontally
        // first whenever the column axis has anything intrinsic to resolve - an unoccupied
        // `1fr` track is enough. That horizontal-only measurement short-circuits to
        // `(known width, 0)` inside the flex/grid/table/block algorithms, and the measure
        // cache used to hand the placeholder zero back for the following block-axis
        // measurement, collapsing the auto row to 0. Chromium 141 sizes every one of these
        // rows to the item's 30px content.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              .g { display:grid; grid-template-columns:200px 1fr; width:600px }
              .box { width:10px; height:30px }
              table { border-spacing:0 }
              td { padding:0 }
            </style>
            <div class="g" id="gflex"><div id="flex" style="display:flex"><div class="box"></div></div></div>
            <div class="g" id="ggrid"><div id="grid" style="display:grid"><div class="box"></div></div></div>
            <div class="g" id="gblock"><div id="block"><div class="box"></div></div></div>
            <div class="g" id="gstart"><div id="start" style="display:flex; align-self:start"><div class="box"></div></div></div>
            <div class="g" id="gtable"><table id="table"><tr><td><div class="box"></div></td></tr></table></div>
            <div class="g" id="gsib"><div id="sibflex" style="display:flex"><div class="box"></div></div><div id="sib"></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        float Height(string id) => laid.Rects[Id(tree, id)].Height;
        float Width(string id) => laid.Rects[Id(tree, id)].Width;

        // The row track, read off the grid container.
        foreach (string id in new[] { "gflex", "ggrid", "gblock", "gstart", "gtable", "gsib" })
        {
            Assert.True(MathF.Abs(Height(id) - 30f) < 0.01f, $"{id}: {Height(id)}");
        }

        // ... and the items stretched into it.
        foreach (string id in new[] { "flex", "grid", "block", "start", "table", "sibflex" })
        {
            Assert.True(MathF.Abs(Height(id) - 30f) < 0.01f, $"{id}: {Height(id)}");
            Assert.True(MathF.Abs(Width(id) - 200f) < 0.01f, $"{id}: {Width(id)}");
        }
    }

    [Fact]
    public void ALoneGridItemsRowIsSizedTheSameWhateverTheSecondTrackIs()
    {
        // The collapse was specific to a column axis that still had work to do after
        // initialisation: `200px 200px` skipped the inline pass entirely and came out right,
        // while `1fr`, `minmax(0,1fr)` and a third `1fr` all collapsed. Chromium 141 gives
        // 30 for every one of them.
        DomTree tree = Parse(
            """
            <style>
              html, body { margin:0 }
              .g { display:grid; width:600px }
              .box { width:10px; height:30px }
            </style>
            <div class="g" id="fixed" style="grid-template-columns:200px 200px"><div style="display:flex"><div class="box"></div></div></div>
            <div class="g" id="fr" style="grid-template-columns:200px 1fr"><div style="display:flex"><div class="box"></div></div></div>
            <div class="g" id="minmax" style="grid-template-columns:200px minmax(0,1fr)"><div style="display:flex"><div class="box"></div></div></div>
            <div class="g" id="three" style="grid-template-columns:200px 1fr 1fr"><div style="display:flex"><div class="box"></div></div></div>
            <div class="g" id="rows" style="grid-template-columns:200px 1fr; grid-template-rows:auto auto"><div style="display:flex"><div class="box"></div></div></div>
            <div class="g" id="placed" style="grid-template-columns:200px 1fr"><div style="display:flex; grid-row:1; grid-column:1"><div class="box"></div></div></div>
            """);
        DomLayout laid = RenderDom.LayoutDom(tree, (800f, 600f));
        float Height(string id) => laid.Rects[Id(tree, id)].Height;

        foreach (string id in new[] { "fixed", "fr", "minmax", "three", "rows", "placed" })
        {
            Assert.True(MathF.Abs(Height(id) - 30f) < 0.01f, $"{id}: {Height(id)}");
        }
    }
}


/// <summary>Deterministic reflected dump of renderer style graphs, used as a Debug analogue.</summary>
internal static class StyleDump
{
    public static string Dump(object? value)
    {
        System.Text.StringBuilder sb = new();
        Write(sb, value, 0, []);
        return sb.ToString();
    }

    private static void Write(
        System.Text.StringBuilder sb,
        object? value,
        int depth,
        HashSet<object> path)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
        }

        Type type = value.GetType();
        if (type.IsEnum || type.IsPrimitive || value is decimal)
        {
            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        if (depth > 12)
        {
            sb.Append("...");
            return;
        }

        if (!type.IsValueType && !path.Add(value))
        {
            sb.Append("<cycle>");
            return;
        }

        try
        {
            if (value is System.Collections.IDictionary dictionary)
            {
                List<string> entries = [];
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    System.Text.StringBuilder item = new();
                    Write(item, entry.Key, depth + 1, path);
                    item.Append(": ");
                    Write(item, entry.Value, depth + 1, path);
                    entries.Add(item.ToString());
                }

                entries.Sort(StringComparer.Ordinal);
                sb.Append('{').AppendJoin(", ", entries).Append('}');
                return;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                sb.Append('[');
                bool firstItem = true;
                foreach (object? item in sequence)
                {
                    if (!firstItem)
                    {
                        sb.Append(", ");
                    }

                    firstItem = false;
                    Write(sb, item, depth + 1, path);
                }

                sb.Append(']');
                return;
            }

            sb.Append(type.Name).Append('(');
            bool firstField = true;
            foreach (System.Reflection.FieldInfo field in Fields(type))
            {
                if (!firstField)
                {
                    sb.Append(", ");
                }

                firstField = false;
                sb.Append(field.Name).Append('=');
                Write(sb, field.GetValue(value), depth + 1, path);
            }

            sb.Append(')');
        }
        finally
        {
            if (!type.IsValueType)
            {
                path.Remove(value);
            }
        }
    }

    private static readonly Dictionary<Type, System.Reflection.FieldInfo[]> FieldCache = [];

    private static System.Reflection.FieldInfo[] Fields(Type type)
    {
        lock (FieldCache)
        {
            if (FieldCache.TryGetValue(type, out System.Reflection.FieldInfo[]? cached))
            {
                return cached;
            }

            System.Reflection.FieldInfo[] fields =
            [
                .. type
                    .GetFields(
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic)
                    .OrderBy(f => f.Name, StringComparer.Ordinal),
            ];
            FieldCache[type] = fields;
            return fields;
        }
    }
}
