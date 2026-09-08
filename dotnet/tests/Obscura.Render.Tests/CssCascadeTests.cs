using Obscura.Dom;
using Obscura.Dom.Selectors;
using Obscura.Render.Css;
using Xunit;

namespace Obscura.Render.Tests;

/// <summary>
/// xUnit port of the indexed-cascade, container-query-evaluator and
/// animation-sampler half of the <c>#[cfg(test)] mod tests</c> block in
/// <c>crates/obscura-render/src/css.rs</c>. Test names and order follow the
/// Rust originals so the mapping stays obvious; the invalidation-map,
/// at-rule, media and <c>@supports</c> tests live in <see cref="CssTests"/>.
/// </summary>
public sealed class CssCascadeTests
{
    // ---------------------------------------------------------------- helpers

    private static readonly Dictionary<string, string> NoProps = new(StringComparer.Ordinal);

    /// <summary>Rust: <c>cascade_layer_target</c>.</summary>
    private static (LayoutStyle Style, Dictionary<string, string> Props, LayoutStyle? Before) CascadeLayerTarget(
        string[] sources,
        string? inlineCss)
    {
        var tree = HtmlParsing.ParseHtml("""<div id="target" class="target"></div>""");
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, sources);
        var matcher = tree.CreateMatcher();
        var style = new LayoutStyle();
        var effective = sheet.Apply(
            tree,
            matcher,
            target,
            "target",
            ["target"],
            "div",
            style,
            NoProps,
            inlineCss) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var before = sheet.PseudoStyles(tree, matcher, target, effective, style).Before;
        return (style, effective, before);
    }

    /// <summary>Rust: <c>sampled_animation_style</c>.</summary>
    private static LayoutStyle SampledAnimationStyle(string css, float sampleMs, string targetId)
    {
        var tree = HtmlParsing.ParseHtml($"""<div id="{targetId}"></div>""");
        var target = tree.GetElementById(targetId)!.Value;
        var sheet = Stylesheet.ParseForViewportAtAnimationTime(
            tree,
            [css],
            (1280f, 720f),
            new AnimationSampleTime(sampleMs));
        var node = tree.GetNode(target)!;
        var element = node.AsElement()!;
        var matcher = tree.CreateMatcher();
        var style = new LayoutStyle();
        sheet.Apply(
            tree,
            matcher,
            target,
            node.GetAttribute("id"),
            [],
            element.Name.Local,
            style,
            NoProps,
            null);
        return style;
    }

    /// <summary>Rust: <c>sampled_fade</c>.</summary>
    private static float SampledFade(string extra, float sampleMs)
    {
        var css = $$"""
                @keyframes fade { from { opacity:0 } to { opacity:1 } }
                #target {
                    opacity:.4;
                    animation:fade 1s linear infinite;
                    {{extra}}
                }
            """;
        return SampledAnimationStyle(css, sampleMs, "target").Opacity ?? 1f;
    }

    /// <summary>Rust: <c>assert_opacity</c>.</summary>
    private static void AssertOpacity(float actual, float expected) =>
        Assert.True(
            MathF.Abs(actual - expected) < 0.0001f,
            $"expected opacity {expected}, got {actual}");

    /// <summary>Rust: <c>apply_registered_property_test_style</c>.</summary>
    private static (LayoutStyle Style, Dictionary<string, string> Props) ApplyRegisteredPropertyTestStyle(
        Stylesheet sheet,
        DomTree tree,
        NodeId target,
        Dictionary<string, string> parentProps)
    {
        var node = tree.GetNode(target)!;
        var element = node.AsElement()!;
        var classes = node.GetAttribute("class")?.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries) ?? [];
        var matcher = tree.CreateMatcher();
        var style = new LayoutStyle();
        var effective = sheet.Apply(
            tree,
            matcher,
            target,
            node.GetAttribute("id"),
            classes,
            element.Name.Local,
            style,
            parentProps,
            null);
        return (style, effective ?? new Dictionary<string, string>(parentProps, StringComparer.Ordinal));
    }

    /// <summary>Rust: <c>evaluate_container_styles</c>.</summary>
    private static (LayoutStyle Style, ContainerDecisionSignature Signature, ContainerQueryStats Stats)
        EvaluateContainerStyles(DomTree tree, Stylesheet sheet, NodeId target, ContainerSnapshot snapshot)
    {
        var node = tree.GetNode(target)!;
        var element = node.AsElement()!;
        var classes = node.GetAttribute("class")?.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries) ?? [];
        var matcher = tree.CreateMatcher();
        var evaluator = new ContainerQueryEvaluator(tree, snapshot);
        var style = new LayoutStyle();
        sheet.ApplyWithContainerQueries(
            tree,
            matcher,
            target,
            node.GetAttribute("id"),
            classes,
            element.Name.Local,
            style,
            NoProps,
            null,
            evaluator);
        var (signature, stats) = evaluator.Finish();
        return (style, signature, stats);
    }

    /// <summary>Rust: <c>container_box</c>.</summary>
    private static ContainerBox ContainerBoxFor(
        ContainerType containerType,
        string[] names,
        float contentWidth,
        float fontSize,
        float contentHeight = 100f) =>
        new()
        {
            ContainerType = containerType,
            AvailableType = containerType,
            Names = names,
            ContentWidth = contentWidth,
            ContentHeight = contentHeight,
            FontSize = fontSize,
        };

    // ------------------------------------------------------------- hot passes

    [Fact(Skip = "release-only cascade microbenchmark")]
    public void BenchmarkSparseCascadeHotPath()
    {
        const int RuleCount = 96;
        const int Iterations = 6_000;

        var tree = HtmlParsing.ParseHtml("""<div id="target" class="target"></div>""");
        var target = tree.GetElementById("target")!.Value;
        var css = new System.Text.StringBuilder();
        for (var index = 0; index < RuleCount; index++)
        {
            css.Append(System.Globalization.CultureInfo.InvariantCulture, $".target {{ width:{index + 1}px; ");
            css.Append(System.Globalization.CultureInfo.InvariantCulture, $"margin-left:{index % 17}px; ");
            css.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $"color:rgb({index % 255}, {index * 3 % 255}, {index * 7 % 255}) }}");
        }

        var sheet = Stylesheet.Parse(tree, [css.ToString()]);
        var matcher = tree.CreateMatcher();
        string[] classes = ["target"];
        var parentProps = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < 32; index++)
        {
            parentProps[$"--inherited-{index}"] = $"{index + 1}px";
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var style = new LayoutStyle();
            sheet.Apply(tree, matcher, target, "target", classes, "div", style, parentProps, null);
        }

        started.Stop();
        Assert.True(started.Elapsed.TotalSeconds >= 0);
    }

    [Fact]
    public void DeclarationStreamFlagsGuardHotPassesAndBorrowPlainRules()
    {
        const string Plain = "width:12px;color:red;";
        var plainFlags = DeclarationStreamFlags.Compute(Plain);
        Assert.Equal(default, plainFlags);
        var borrowed = CssVariables.SubstituteDeclarations(Plain, NoProps, plainFlags.HasVar);
        Assert.True(ReferenceEquals(Plain, borrowed), "a var()-free stream must not be reallocated");

        const string Dynamic = "--tone:dark;color-scheme:light dark;animation-name:pulse;color:var(--tone);";
        var flags = DeclarationStreamFlags.Compute(Dynamic);
        Assert.True(flags.HasCustomProperties);
        Assert.True(flags.HasVar);
        Assert.True(flags.HasColorScheme);
        Assert.True(flags.HasAnimation);
        var props = new Dictionary<string, string>(StringComparer.Ordinal) { ["--tone"] = "rebeccapurple" };
        var expanded = CssVariables.SubstituteDeclarations(Dynamic, props, flags.HasVar);
        Assert.False(ReferenceEquals(Dynamic, expanded));
        Assert.Contains("color:rebeccapurple;", expanded, StringComparison.Ordinal);

        var similarlyNamed = DeclarationStreamFlags.Compute(
            "my-color-scheme:dark;animationish:fade;background:variety(red);");
        Assert.False(similarlyNamed.HasVar);
        Assert.False(similarlyNamed.HasColorScheme);
        Assert.False(similarlyNamed.HasAnimation);
    }

    [Fact]
    public void RootRulesUseTheDocumentElementBucketWithoutLosingIsArms()
    {
        var tree = HtmlParsing.ParseHtml("""<html><body><div id="card" class="card"></div></body></html>""");
        var sheet = Stylesheet.Parse(tree, ["""
                :root { width:321px }
                :is(:root, .card) { height:123px }
            """]);
        var (_, _, classKeys, _, _, universal) = sheet.DebugStats();
        Assert.Equal(1, classKeys);
        Assert.True(universal == 0, ":root must not remain in the universal bucket");

        var root = tree.QuerySelector("html")!.Value;
        var body = tree.QuerySelector("body")!.Value;
        var card = tree.GetElementById("card")!.Value;
        var matcher = tree.CreateMatcher();
        var rootStyle = new LayoutStyle();
        sheet.Apply(tree, matcher, root, null, [], "html", rootStyle, NoProps, null);
        Assert.Equal(Dimension.Px(321f), rootStyle.Width);
        Assert.Equal(Dimension.Px(123f), rootStyle.Height);

        var bodyStyle = new LayoutStyle();
        sheet.Apply(tree, matcher, body, null, [], "body", bodyStyle, NoProps, null);
        Assert.NotEqual(Dimension.Px(123f), bodyStyle.Height);

        var cardStyle = new LayoutStyle();
        sheet.Apply(tree, matcher, card, "card", ["card"], "div", cardStyle, NoProps, null);
        Assert.Equal(Dimension.Px(123f), cardStyle.Height);
    }

    [Fact]
    public void PseudoRulesUseSubjectBucketsAndDenseDedup()
    {
        var tree = HtmlParsing.ParseHtml("""
            <div id="both" class="alpha" data-kind="both"></div>
            <div id="attribute" data-kind="attribute"></div>
            """);
        var sheet = Stylesheet.Parse(tree, ["""
                :is(.alpha, [data-kind])::before { content:"indexed" }
                .missing::before { content:"wrong" }
            """]);

        Assert.Equal(2, sheet.BeforeRules.Rules.Count);
        Assert.Equal([0], sheet.BeforeRules.ByClass["alpha"]);
        Assert.Equal([0], sheet.BeforeRules.ByAttribute["data-kind"]);
        Assert.Equal(0u, sheet.BeforeRules.Rules[0].CandidateSlot);
        Assert.Equal(1, sheet.BeforeRules.CandidateSlotCount);

        foreach (var id in new[] { "both", "attribute" })
        {
            var target = tree.GetElementById(id)!.Value;
            var before = sheet.PseudoStyles(tree, tree.CreateMatcher(), target, NoProps, new LayoutStyle()).Before;
            Assert.NotNull(before);
            Assert.Equal("indexed", before.BeforeContent);
        }
    }

    [Fact]
    public void PseudoCandidateBucketDoesNotReplaceFullSelectorMatching()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="target" class="alpha blocked"></div>""");
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                .alpha::before { content:"base" }
                .alpha:not(.blocked)::before { content:"wrong" }
            """]);
        var before = sheet.PseudoStyles(tree, tree.CreateMatcher(), target, NoProps, new LayoutStyle()).Before;
        Assert.NotNull(before);
        Assert.Equal("base", before.BeforeContent);
    }

    [Fact]
    public void DisjointFunctionalSubjectsUseAllBucketsAndOneDenseSlot()
    {
        var tree = HtmlParsing.ParseHtml(
            """<div id="target" class="alpha" data-kind="both"></div><div data-kind="attribute"></div>""");
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, [":is(.alpha, [data-kind]) { width:47px }"]);

        Assert.Empty(sheet.Universal);
        Assert.Equal([0], sheet.ByClass["alpha"]);
        Assert.Equal([0], sheet.ByAttribute["data-kind"]);
        Assert.Equal(0u, sheet.Rules[0].CandidateSlot);
        Assert.Equal(1, sheet.CandidateSlotCount);

        var matcher = tree.CreateMatcher();
        var style = new LayoutStyle();
        sheet.Apply(tree, matcher, target, "target", ["alpha"], "div", style, NoProps, null);
        Assert.Equal(Dimension.Px(47f), style.Width);
    }

    // --------------------------------------------------------- cascade layers

    [Fact]
    public void SparsePriorityStreamsPreserveNormalAndImportantOrder()
    {
        var (style, _, _) = CascadeLayerTarget(["""
                @layer first, second;
                @layer second {
                    #target { width:20px }
                    #target { height:40px !important }
                }
                @layer first {
                    .target { width:10px }
                    .target { height:30px !important }
                }
                .target { width:50px }
            """], null);
        Assert.Equal(Dimension.Px(50f), style.Width);
        Assert.Equal(Dimension.Px(30f), style.Height);
    }

    [Fact]
    public void InheritedCustomPropertyFastPathReusesParentMap()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="target" class="target"></div>""");
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                .target {
                    width:var(--inherited-size);
                    background-image:url("/asset--content-hash.png");
                }
            """]);
        var parentProps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--inherited-size"] = "37px",
        };
        var matcher = tree.CreateMatcher();
        var style = new LayoutStyle();
        var effective = sheet.Apply(
            tree,
            matcher,
            target,
            "target",
            ["target"],
            "div",
            style,
            parentProps,
            null);

        Assert.Equal(Dimension.Px(37f), style.Width);
        Assert.True(
            effective is null,
            "an inherited var() use and `--` inside a value must not allocate a new property map");
    }

    [Fact]
    public void LayerOrderAppliesToCustomPropertiesAndPseudos()
    {
        var (style, props, before) = CascadeLayerTarget(["""
                @layer base, theme;
                @layer theme {
                    #target { --normal-size:30px; --critical-size:31px !important }
                    #target::before { content:"theme"; width:30px }
                }
                @layer base {
                    #target { --normal-size:10px; --critical-size:11px !important }
                    #target::before { content:"base" !important; width:10px }
                }
                #target { width:var(--normal-size); height:var(--critical-size) }
            """], null);
        Assert.Equal("30px", props["--normal-size"]);
        Assert.Equal("11px", props["--critical-size"]);
        Assert.Equal(Dimension.Px(30f), style.Width);
        Assert.Equal(Dimension.Px(11f), style.Height);
        Assert.NotNull(before);
        Assert.Equal("base", before.BeforeContent);
        Assert.Equal(Dimension.Px(30f), before.Width);
    }

    // ------------------------------------------------------- animation sampler

    [Fact]
    public void AnimationEffectImpactSeparatesPaintFromGeometryTracks()
    {
        static AnimationEffectImpact Sampled(string declarations) =>
            SampledAnimationStyle(
                $"@keyframes effect {{ from {{ {declarations} }} to {{ {declarations} }} }}"
                + "#target { animation:effect 1s linear infinite }",
                100f,
                "target").AnimationEffectImpact;

        Assert.Equal(
            AnimationEffectImpact.Paint,
            Sampled("opacity:.5;color:red;background-color:blue;border-color:green;"
                + "background-position:20% 30%;visibility:hidden"));
        foreach (var declarations in new[]
                 {
                     "transform:translateX(1px)",
                     "translate:1px 2px",
                     "rotate:10deg",
                     "scale:2",
                     "width:20px",
                     "height:20px",
                     "inset:1px",
                     "margin:1px",
                     "padding:1px",
                     "gap:1px",
                     "flex-basis:20px",
                 })
        {
            Assert.True(
                Sampled(declarations) == AnimationEffectImpact.Geometry,
                declarations);
        }

        Assert.Equal(AnimationEffectImpact.None, Sampled("--unsupported:1"));
    }

    [Fact]
    public void AnimationSamplesGeometryTransformAndPaintPropertiesAtT0()
    {
        const string Css = """
            @keyframes demo {
                from {
                    transform:translateX(100px);
                    translate:20px 30px;
                    rotate:10deg;
                    scale:2 3;
                    width:300px;
                    height:80px;
                    min-width:120px;
                    max-height:90px;
                    inset:1px 2px 3px 4px;
                    margin:5px 6px 7px 8px;
                    padding:9px 10px 11px 12px;
                    gap:13px 14px;
                    flex-basis:150px;
                    color:rgb(10,20,30);
                    background:red;
                    border-color:blue green yellow black;
                    background-position:25% 40%;
                    visibility:hidden;
                    opacity:.25;
                }
                to { opacity:.75 }
            }
            #target { width:50px; animation:demo 10s both }
            """;
        var style = SampledAnimationStyle(Css, 0f, "target");
        Assert.Equal(Dimension.Px(300f), style.Width);
        Assert.Equal(Dimension.Px(80f), style.Height);
        Assert.Equal(Dimension.Px(120f), style.MinWidth);
        Assert.Equal(Dimension.Px(90f), style.MaxHeight);
        Assert.Equal(Dimension.Px(4f), style.Inset[3]);
        Assert.Equal(8f, style.Margin.Left);
        Assert.Equal(11f, style.Padding.Bottom);
        Assert.Equal(13f, style.RowGap);
        Assert.Equal(14f, style.ColumnGap);
        Assert.Equal(Dimension.Px(150f), style.FlexBasis);
        Assert.Equal(new RgbaColor(10, 20, 30, 255), style.Color);
        Assert.Equal(new RgbaColor(255, 0, 0, 255), style.BackgroundColor);
        Assert.Equal(new RgbaColor(0, 0, 255, 255), style.BorderModel.Colors.Top);
        Assert.Equal(new RgbaColor(0, 128, 0, 255), style.BorderModel.Colors.Right);
        Assert.Equal(true, style.VisibilityHidden);
        AssertOpacity(style.Opacity!.Value, 0.25f);
        var translate = Assert.IsType<TransformOp.Translate>(Assert.Single(style.TransformOps));
        Assert.Equal(Dimension.Px(100f), translate.X.Value);
        Assert.Equal(Dimension.Px(0f), translate.Y.Value);
        Assert.Equal((Dimension.Px(20f), Dimension.Px(30f)), style.IndividualTranslate);
        Assert.Equal(10f, style.IndividualRotate);
        Assert.Equal((2f, 3f), style.IndividualScale);
    }

    [Fact(Skip = "needs dom.rs's layout_dom, which is a later port stage")]
    public void AnimationT0GeometryReachesTheRealLayoutPass()
    {
        // Rust: crate::dom::layout_dom over an animated flex row. Restore this
        // once dom.rs lands; the sampled geometry itself is covered by
        // AnimationSamplesGeometryTransformAndPaintPropertiesAtT0.
    }

    [Fact]
    public void AnimationSparseTracksIgnoreUnrelatedIntermediateKeyframes()
    {
        const string Css = """
            @keyframes sparse {
                from { width:100px; opacity:0; background-color:red }
                50% { opacity:1 }
                to { width:300px; opacity:0; background-color:blue }
            }
            #target { animation:sparse 1s linear both }
            """;
        var style = SampledAnimationStyle(Css, 500f, "target");
        Assert.Equal(Dimension.Px(200f), style.Width);
        AssertOpacity(style.Opacity!.Value, 1f);
        Assert.Equal(new RgbaColor(128, 0, 128, 255), style.BackgroundColor);

        const string Implicit = """
            @keyframes middle { 50% { width:150px } }
            #target { width:50px; animation:middle 1s linear both }
            """;
        Assert.Equal(Dimension.Px(100f), SampledAnimationStyle(Implicit, 250f, "target").Width);
        Assert.Equal(Dimension.Px(100f), SampledAnimationStyle(Implicit, 750f, "target").Width);
    }

    [Fact]
    public void KeyframeDuplicateOffsetsMergePerPropertyAndIgnoreImportant()
    {
        const string Css = """
            @keyframes merged {
                0% { width:100px; color:red; opacity:.1 }
                from { width:999px !important; opacity:.8 }
            }
            #target { width:40px; animation:merged 1s both }
            """;
        var style = SampledAnimationStyle(Css, 0f, "target");
        Assert.Equal(Dimension.Px(100f), style.Width);
        Assert.Equal(new RgbaColor(255, 0, 0, 255), style.Color);
        AssertOpacity(style.Opacity!.Value, 0.8f);
    }

    [Fact]
    public void AuthorImportantOverridesEverySampledAnimationProperty()
    {
        const string Css = """
            @keyframes forced { from, to { width:300px; background-color:red } }
            #target {
                animation:forced 1s both;
                width:80px !important;
                background-color:blue !important;
            }
            """;
        var style = SampledAnimationStyle(Css, 0f, "target");
        Assert.Equal(Dimension.Px(80f), style.Width);
        Assert.Equal(new RgbaColor(0, 0, 255, 255), style.BackgroundColor);
    }

    [Fact]
    public void WaapiSamplesOpacityAndTransformWithoutRewritingInlineCascade()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="target" style="opacity:.2"></div>""");
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.ParseForViewport(tree, [], (800f, 600f));
        var timeline = new AnimationTimelineState();
        timeline.RegisterWaapi(new WaapiAnimation
        {
            Id = 1,
            Node = target,
            Keyframes =
            [
                new WaapiKeyframe { Offset = 0f, Opacity = 0.2f, Transform = "translateX(0px)" },
                new WaapiKeyframe { Offset = 1f, Opacity = 1f, Transform = "translateX(100px)" },
            ],
            Timing = AnimationTiming.Default with { DurationMs = 100f, FillMode = AnimationFillMode.Both },
            StartTimeMs = 0f,
            HoldTimeMs = 50f,
            PlayState = WaapiPlayState.Paused,
        });
        var node = tree.GetNode(target)!;
        var element = node.AsElement()!;
        var matcher = tree.CreateMatcher();
        var style = new LayoutStyle();
        sheet.ApplyAtAnimationTime(
            tree,
            matcher,
            target,
            node.GetAttribute("id"),
            [],
            element.Name.Local,
            style,
            NoProps,
            node.GetAttribute("style"),
            AnimationSample.Document(50f),
            timeline);
        AssertOpacity(style.Opacity!.Value, 0.6f);
        var translate = Assert.IsType<TransformOp.Translate>(Assert.Single(style.TransformOps));
        Assert.Equal(Dimension.Px(50f), translate.X.Value);

        var importantTree = HtmlParsing.ParseHtml("""<div id="target" style="opacity:.2 !important"></div>""");
        var importantTarget = importantTree.GetElementById("target")!.Value;
        var importantSheet = Stylesheet.ParseForViewport(importantTree, [], (800f, 600f));
        var importantTimeline = new AnimationTimelineState();
        importantTimeline.RegisterWaapi(new WaapiAnimation
        {
            Id = 2,
            Node = importantTarget,
            Keyframes = [new WaapiKeyframe { Offset = 1f, Opacity = 1f, Transform = null }],
            Timing = AnimationTiming.Default with { DurationMs = 100f, FillMode = AnimationFillMode.Both },
            StartTimeMs = 0f,
            HoldTimeMs = 100f,
            PlayState = WaapiPlayState.Finished,
        });
        var importantNode = importantTree.GetNode(importantTarget)!;
        var importantMatcher = importantTree.CreateMatcher();
        var importantStyle = new LayoutStyle();
        importantSheet.ApplyAtAnimationTime(
            importantTree,
            importantMatcher,
            importantTarget,
            importantNode.GetAttribute("id"),
            [],
            "div",
            importantStyle,
            NoProps,
            importantNode.GetAttribute("style"),
            AnimationSample.Document(100f),
            importantTimeline);
        AssertOpacity(importantStyle.Opacity!.Value, 0.2f);
    }

    [Fact]
    public void RetainedWaapiTransformResamplingUsesUnderlyingAndRespectsImportant()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="target" style="transform:translateX(10px)"></div>""");
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.ParseForViewport(tree, [], (800f, 600f));

        AnimationTimelineState MakeTimeline()
        {
            var timeline = new AnimationTimelineState();
            timeline.RegisterWaapi(new WaapiAnimation
            {
                Id = 1,
                Node = target,
                Keyframes = [new WaapiKeyframe { Offset = 0.5f, Opacity = null, Transform = "translateX(50px)" }],
                Timing = AnimationTiming.Default with { DurationMs = 1_000f, FillMode = AnimationFillMode.Both },
                StartTimeMs = 0f,
                HoldTimeMs = null,
                PlayState = WaapiPlayState.Running,
            });
            return timeline;
        }

        LayoutStyle Apply(float time, AnimationTimelineState timeline)
        {
            var node = tree.GetNode(target)!;
            var matcher = tree.CreateMatcher();
            var style = new LayoutStyle();
            sheet.ApplyAtAnimationTime(
                tree,
                matcher,
                target,
                node.GetAttribute("id"),
                [],
                "div",
                style,
                NoProps,
                node.GetAttribute("style"),
                AnimationSample.Document(time),
                timeline);
            return style;
        }

        var retainedTimeline = MakeTimeline();
        var retained = Apply(0f, retainedTimeline);
        foreach (var time in new[] { 500f, 750f })
        {
            var resampled = CssAnimationSampler.ResampleVisualWaapi(
                retainedTimeline,
                target,
                retained,
                AnimationSample.Document(time));
            Assert.NotNull(resampled);
            var freshTimeline = MakeTimeline();
            var fresh = Apply(time, freshTimeline);
            Assert.Equal<IEnumerable<TransformOp>>(fresh.TransformOps, resampled.TransformOps);
        }

        var importantTree = HtmlParsing.ParseHtml(
            """<div id="target" style="transform:translateX(10px) !important"></div>""");
        var importantTarget = importantTree.GetElementById("target")!.Value;
        var importantSheet = Stylesheet.ParseForViewport(importantTree, [], (800f, 600f));
        var importantTimeline = new AnimationTimelineState();
        importantTimeline.RegisterWaapi(new WaapiAnimation
        {
            Id = 2,
            Node = importantTarget,
            Keyframes = [new WaapiKeyframe { Offset = 1f, Opacity = null, Transform = "translateX(100px)" }],
            Timing = AnimationTiming.Default with { DurationMs = 1_000f, FillMode = AnimationFillMode.Both },
            StartTimeMs = 0f,
            HoldTimeMs = null,
            PlayState = WaapiPlayState.Running,
        });
        var importantNode = importantTree.GetNode(importantTarget)!;
        var importantStyle = new LayoutStyle();
        importantSheet.ApplyAtAnimationTime(
            importantTree,
            importantTree.CreateMatcher(),
            importantTarget,
            importantNode.GetAttribute("id"),
            [],
            "div",
            importantStyle,
            NoProps,
            importantNode.GetAttribute("style"),
            AnimationSample.Document(0f),
            importantTimeline);
        Assert.Null(CssAnimationSampler.ResampleVisualWaapi(
            importantTimeline,
            importantTarget,
            importantStyle,
            AnimationSample.Document(500f)));
    }

    [Fact]
    public void ConditionalKeyframesAndEmptyLaterDefinitionsFollowBrowserSelection()
    {
        const string Css = """
            @keyframes chosen { from, to { width:222px } }
            @media (max-width:100px) {
                @keyframes chosen { from, to { width:111px } }
            }
            @supports (unknown-parity-property:value) {
                @keyframes chosen { from, to { width:123px } }
            }
            #target { width:50px; animation:chosen 1s both }
            """;
        Assert.Equal(Dimension.Px(222f), SampledAnimationStyle(Css, 0f, "target").Width);

        const string Empty = """
            @keyframes cleared { from, to { width:300px } }
            @keyframes cleared {}
            #target { width:50px; animation:cleared 1s both }
            """;
        Assert.Equal(Dimension.Px(50f), SampledAnimationStyle(Empty, 0f, "target").Width);
    }

    [Fact]
    public void KeyframeNamesFollowCascadeLayerOrderAndStandardPrefixPriority()
    {
        const string Unlayered = """
            @keyframes chosen { from, to { width:222px } }
            @layer outer { @keyframes chosen { from, to { width:111px } } }
            #target { animation:chosen 1s both }
            """;
        Assert.Equal(Dimension.Px(222f), SampledAnimationStyle(Unlayered, 0f, "target").Width);

        const string Ordered = """
            @layer weak, strong;
            @layer strong { @keyframes chosen { from, to { width:333px } } }
            @layer weak { @keyframes chosen { from, to { width:444px } } }
            #target { animation:chosen 1s both }
            """;
        Assert.Equal(Dimension.Px(333f), SampledAnimationStyle(Ordered, 0f, "target").Width);

        var reversed = Ordered.Replace("@layer weak, strong", "@layer strong, weak", StringComparison.Ordinal);
        Assert.Equal(Dimension.Px(444f), SampledAnimationStyle(reversed, 0f, "target").Width);

        const string Prefix = """
            @keyframes chosen { from, to { width:555px } }
            @-webkit-keyframes chosen { from, to { width:666px } }
            #target { animation:chosen 1s both }
            """;
        Assert.Equal(Dimension.Px(555f), SampledAnimationStyle(Prefix, 0f, "target").Width);
    }

    [Fact]
    public void AnimationTimelineHandlesDelayFillIterationsAndDirectionAtT0()
    {
        AssertOpacity(SampledFade("", 0f), 0f);
        AssertOpacity(SampledFade("animation-delay:250ms", 0f), 0.4f);
        AssertOpacity(SampledFade("animation-delay:250ms;animation-fill-mode:backwards", 0f), 0f);
        AssertOpacity(
            SampledFade("animation-delay:250ms;animation-fill-mode:backwards;animation-direction:reverse", 0f),
            1f);
        AssertOpacity(SampledFade("animation-delay:-250ms", 0f), 0.25f);
        AssertOpacity(SampledFade("animation-delay:-1s", 0f), 0f);
        AssertOpacity(SampledFade("animation-delay:-1s;animation-direction:alternate", 0f), 1f);
        AssertOpacity(
            SampledFade("animation-delay:-1s;animation-iteration-count:1;animation-fill-mode:none", 0f),
            0.4f);
        AssertOpacity(
            SampledFade("animation-delay:-1s;animation-iteration-count:1;animation-fill-mode:forwards", 0f),
            1f);
        AssertOpacity(
            SampledFade(
                "animation-delay:-1s;animation-iteration-count:1;animation-fill-mode:forwards;"
                + "animation-direction:reverse",
                0f),
            0f);
        AssertOpacity(
            SampledFade(
                "animation-delay:-2s;animation-iteration-count:2;animation-fill-mode:forwards;"
                + "animation-direction:alternate",
                0f),
            0f);
        AssertOpacity(SampledFade("animation-iteration-count:0;animation-fill-mode:forwards", 0f), 0f);
        AssertOpacity(
            SampledFade("animation-duration:0s;animation-iteration-count:1;animation-fill-mode:none", 0f),
            0.4f);
        AssertOpacity(
            SampledFade("animation-duration:0s;animation-iteration-count:1;animation-fill-mode:forwards", 0f),
            1f);
        AssertOpacity(
            SampledFade("animation-delay:-2.5s;animation-iteration-count:2.5;animation-fill-mode:forwards", 0f),
            0.5f);
    }

    [Fact]
    public void AnimationCalcDelayPauseAndImportantOriginAreDeterministic()
    {
        AssertOpacity(SampledFade("--step:.1s;animation-delay:calc(var(--step) * -2.5)", 0f), 0.25f);
        AssertOpacity(SampledFade("animation-play-state:paused", 500f), 0f);
        AssertOpacity(SampledFade("", 500f), 0.5f);
        AssertOpacity(SampledFade("animation-delay:-250ms !important;opacity:.8 !important", 0f), 0.8f);
    }

    [Fact]
    public void OpacitySegmentsUseUnderlyingEndpointsAndExactLaterBoundaries()
    {
        const string MissingEndpoints = """
            @keyframes middle { 50% { opacity:1 } }
            #target { opacity:.4; animation:middle 1s linear 1 both }
            """;
        AssertOpacity(SampledAnimationStyle(MissingEndpoints, 250f, "target").Opacity!.Value, 0.7f);
        AssertOpacity(SampledAnimationStyle(MissingEndpoints, 750f, "target").Opacity!.Value, 0.7f);

        const string ExactBoundary = """
            @keyframes peak {
                0% { opacity:0 }
                50% { opacity:1 }
                100% { opacity:0 }
            }
            #target { opacity:.4; animation:peak 1s linear 1 both }
            """;
        AssertOpacity(SampledAnimationStyle(ExactBoundary, 500f, "target").Opacity!.Value, 1f);
    }

    [Fact]
    public void MissingKeyframeOffsetsFollowWebAnimationDistribution()
    {
        static KeyframeStop Stop(float? offset) => new(offset, "opacity:1", 0);

        var single = new[] { Stop(null) };
        Assert.Equal(1f, CssKeyframes.NormalizedOffsets(single)[0].Offset);

        var pair = new[] { Stop(null), Stop(null) };
        Assert.Equal([0f, 1f], CssKeyframes.NormalizedOffsets(pair).Select(entry => entry.Offset));

        var distributed = new[] { Stop(0f), Stop(null), Stop(null), Stop(0.75f), Stop(null) };
        Assert.Equal(
            [0f, 0.25f, 0.5f, 0.75f, 1f],
            CssKeyframes.NormalizedOffsets(distributed).Select(entry => entry.Offset));
    }

    [Fact]
    public void WaapiDuplicateOffsetUsesLaterValueAtBoundary()
    {
        (float Offset, float Value)[] track = [(0f, 0f), (0.5f, 10f), (0.5f, 20f), (1f, 30f)];

        static bool Sample((float, float)[] track, float underlying, float progress, out float result) =>
            CssKeyframes.TrySampleWaapiTrack(
                track,
                underlying,
                progress,
                static (from, to, position) => from + ((to - from) * position),
                out result);

        Assert.True(Sample(track, -1f, 0.5f, out var atBoundary));
        Assert.Equal(20f, atBoundary);

        Assert.True(Sample(track, -1f, 0.499f, out var before));
        Assert.True(before < 10f, "incoming segment must end at the first duplicate");

        Assert.True(Sample(track, -1f, 0.501f, out var after));
        Assert.True(after > 20f, "outgoing segment must start at the later duplicate");
    }

    [Fact]
    public void MozillaStaggerSamplesOnlyTheDelayZeroFrame()
    {
        var html = new System.Text.StringBuilder();
        var selectors = new System.Text.StringBuilder();
        for (var frame = 0; frame < 12; frame++)
        {
            html.Append(System.Globalization.CultureInfo.InvariantCulture, $"""<svg id="frame{frame}" class="frame"></svg>""");
            selectors.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $"#frame{frame}{{animation-delay:calc(var(--base-delay) * {frame})}}");
        }

        var tree = HtmlParsing.ParseHtml(html.ToString());
        var css = $$"""
                @keyframes wave {
                    0%, 8.333% { opacity:1 }
                    8.4%, to { opacity:0 }
                }
                .frame {
                    --base-delay:.1s;
                    opacity:0;
                    animation:wave 1.2s linear infinite;
                }
                {{selectors}}
            """;
        var sheet = Stylesheet.ParseForViewportAtAnimationTime(tree, [css], (1280f, 720f), default);
        for (var frame = 0; frame < 12; frame++)
        {
            var target = tree.GetElementById($"frame{frame}")!.Value;
            var node = tree.GetNode(target)!;
            var element = node.AsElement()!;
            var matcher = tree.CreateMatcher();
            var style = new LayoutStyle();
            sheet.Apply(
                tree,
                matcher,
                target,
                node.GetAttribute("id"),
                ["frame"],
                element.Name.Local,
                style,
                NoProps,
                null);
            AssertOpacity(style.Opacity!.Value, frame == 0 ? 1f : 0f);
        }
    }

    // -------------------------------------------------- registered properties

    [Fact]
    public void RegisteredCustomPropertiesObeyInheritsDescriptors()
    {
        var tree = HtmlParsing.ParseHtml(
            """<div id="parent"><div id="child"></div></div><div id="initial"></div>""");
        const string Css = """
            @property --private {
                syntax:"<percentage>";
                inherits:false;
                initial-value:75%;
            }
            @property --shared {
                syntax:"<percentage>";
                inherits:true;
                initial-value:25%;
            }
            #parent { --private:20%; --shared:30% }
            #child, #initial {
                width:var(--private);
                height:var(--shared);
            }
            """;
        var sheet = Stylesheet.Parse(tree, [Css]);
        var parent = tree.GetElementById("parent")!.Value;
        var child = tree.GetElementById("child")!.Value;
        var initial = tree.GetElementById("initial")!.Value;
        var (_, parentProps) = ApplyRegisteredPropertyTestStyle(sheet, tree, parent, new(StringComparer.Ordinal));
        var (childStyle, _) = ApplyRegisteredPropertyTestStyle(sheet, tree, child, parentProps);
        Assert.Equal(Dimension.Percent(0.75f), childStyle.Width);
        Assert.Equal(Dimension.Percent(0.30f), childStyle.Height);

        var (initialStyle, _) = ApplyRegisteredPropertyTestStyle(sheet, tree, initial, new(StringComparer.Ordinal));
        Assert.Equal(Dimension.Percent(0.75f), initialStyle.Width);
        Assert.Equal(Dimension.Percent(0.25f), initialStyle.Height);
    }

    [Fact]
    public void RegisteredCustomPropertiesReuseAnUnchangedParentMap()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="target"></div>""");
        const string Css = """
            @property --private {
                syntax:"<percentage>";
                inherits:false;
                initial-value:75%;
            }
            @property --shared {
                syntax:"<percentage>";
                inherits:true;
                initial-value:25%;
            }
            #target { width:var(--private); height:var(--shared) }
            """;
        var sheet = Stylesheet.Parse(tree, [Css]);
        var target = tree.GetElementById("target")!.Value;
        var parentProps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--private"] = "75%",
            ["--shared"] = "30%",
        };
        var matcher = tree.CreateMatcher();
        var style = new LayoutStyle();
        var effective = sheet.Apply(tree, matcher, target, "target", [], "div", style, parentProps, null);
        Assert.Null(effective);
        Assert.Equal(Dimension.Percent(0.75f), style.Width);
        Assert.Equal(Dimension.Percent(0.30f), style.Height);

        var overriddenParent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--private"] = "60%",
            ["--shared"] = "30%",
        };
        style = new LayoutStyle();
        var overridden = sheet.Apply(tree, matcher, target, "target", [], "div", style, overriddenParent, null);
        Assert.True(overridden is not null, "non-inherited registered property must reset on the child");
        Assert.Equal("75%", overridden["--private"]);
        Assert.Equal("30%", overridden["--shared"]);
        Assert.Equal(Dimension.Percent(0.75f), style.Width);
        Assert.Equal(Dimension.Percent(0.30f), style.Height);
    }

    [Fact]
    public void RegisteredCustomPropertyOverridesAndVarFallbacksStayDistinct()
    {
        var tree = HtmlParsing.ParseHtml(
            """<div id="override"></div><div id="reset"></div><div id="invalid"></div>""");
        const string Css = """
            @property --registered {
                syntax:"<percentage>";
                inherits:false;
                initial-value:75%;
            }
            #override {
                --registered:60%;
                width:var(--registered, 10%);
                height:var(--missing, 12%);
            }
            #reset {
                --registered:initial;
                width:var(--registered, 10%);
                height:var(--missing, 12%);
            }
            #invalid {
                --registered:red;
                width:var(--registered, 10%);
            }
            """;
        var sheet = Stylesheet.Parse(tree, [Css]);
        var overrideId = tree.GetElementById("override")!.Value;
        var resetId = tree.GetElementById("reset")!.Value;
        var invalidId = tree.GetElementById("invalid")!.Value;

        var (overridden, _) = ApplyRegisteredPropertyTestStyle(sheet, tree, overrideId, new(StringComparer.Ordinal));
        Assert.Equal(Dimension.Percent(0.60f), overridden.Width);
        Assert.Equal(Dimension.Percent(0.12f), overridden.Height);

        var (reset, _) = ApplyRegisteredPropertyTestStyle(sheet, tree, resetId, new(StringComparer.Ordinal));
        Assert.Equal(Dimension.Percent(0.75f), reset.Width);
        Assert.Equal(Dimension.Percent(0.12f), reset.Height);

        var (invalid, invalidProps) =
            ApplyRegisteredPropertyTestStyle(sheet, tree, invalidId, new(StringComparer.Ordinal));
        Assert.Equal(Dimension.Percent(0.75f), invalid.Width);
        Assert.True(
            invalidProps.TryGetValue("--registered", out var registered) && registered == "75%",
            "an invalid typed value computes to the registered initial value");
    }

    // ------------------------------------------------------------ port seams

    /// <summary>
    /// Not a Rust test: <c>ICssElementView</c> is this port's stand-in for the
    /// <c>&amp;DomTree, NodeId</c> pair the Rust invalidation predicates take.
    /// This exercises the real implementation over the live tree so the seam is
    /// covered, not only the test double in <see cref="CssTests"/>.
    /// </summary>
    [Fact]
    public void DomElementViewDrivesTheInvalidationPredicates()
    {
        var tree = HtmlParsing.ParseHtml(
            """<!doctype html><section><span id="lead" class="left" data-role="lead"></span><b class="right"></b></section>""");
        var lead = tree.GetElementById("lead")!.Value;
        var view = CssInvalidationDom.View(tree, lead);
        Assert.True(view.IsElement);
        Assert.Equal("span", view.LocalName);
        Assert.Equal("lead", view.GetAttribute("id"));
        Assert.Equal(["id", "class", "data-role"], view.AttributeNames);
        Assert.False(view.IsQuirks);

        var map = Stylesheet.Parse(tree, [".left + .right { color:red }"]).InvalidationMap;
        Assert.True(map.HasAdjacentSiblingSelectors);
        Assert.True(
            map.NodeMayStartSiblingSelector(tree, lead),
            ".left is the subject of an adjacent-sibling selector");

        var unrelated = tree.QuerySelector("section")!.Value;
        Assert.False(
            map.NodeMayStartSiblingSelector(tree, unrelated),
            "an element no sibling selector keys on cannot start one");

        // Quirks mode reaches through the same view and forces the conservative answer.
        var quirks = HtmlParsing.ParseHtml("""<section><span class="left"></span></section>""");
        var quirksLead = quirks.QuerySelector(".left")!.Value;
        Assert.True(CssInvalidationDom.View(quirks, quirksLead).IsQuirks);
        Assert.True(
            Stylesheet.Parse(quirks, [".left + .right { color:red }"])
                .InvalidationMap
                .NodeMayStartSiblingSelector(quirks, quirks.QuerySelector("section")!.Value));
    }

    // ----------------------------------------------- container query evaluator

    [Fact]
    public void ContainerEvaluatorHonorsTailwindThresholdAndCache()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="container"><div id="target"></div></div>""");
        var container = tree.GetElementById("container")!.Value;
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                #target { width:1px; height:1px }
                @container (min-width:28rem) {
                    #target { width:2px }
                    #target { height:2px }
                }
            """]);

        ContainerSnapshot Snapshot(float width)
        {
            var snapshot = new ContainerSnapshot { RootFontSize = 16f };
            snapshot.Boxes[container] = ContainerBoxFor(ContainerType.InlineSize, [], width, 16f);
            return snapshot;
        }

        var (below, _, _) = EvaluateContainerStyles(tree, sheet, target, Snapshot(447f));
        Assert.Equal(Dimension.Px(1f), below.Width);
        Assert.Equal(Dimension.Px(1f), below.Height);

        var (at, _, stats) = EvaluateContainerStyles(tree, sheet, target, Snapshot(448f));
        Assert.Equal(Dimension.Px(2f), at.Width);
        Assert.Equal(Dimension.Px(2f), at.Height);
        Assert.Equal(1, stats.Evaluations);
        Assert.True(stats.CacheHits >= 1);
    }

    [Fact]
    public void ContainerEvaluatorMatchesSelectorBeforeAncestorLookup()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="container"><div id="target"></div></div>""");
        var container = tree.GetElementById("container")!.Value;
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                @container (min-width:1px) {
                    div[data-never-present] { width:999px }
                }
            """]);
        var snapshot = new ContainerSnapshot { RootFontSize = 16f };
        snapshot.Boxes[container] = ContainerBoxFor(ContainerType.InlineSize, [], 500f, 16f);
        var (_, signature, stats) = EvaluateContainerStyles(tree, sheet, target, snapshot);
        Assert.Equal(0, stats.Evaluations);
        Assert.Equal(0, stats.AncestorSteps);
        Assert.True(signature.IsEmpty);
    }

    [Fact]
    public void ContainerEvaluatorSelectsNearestEligibleNamedAncestor()
    {
        var tree = HtmlParsing.ParseHtml(
            """<div id="outer"><div id="inner"><div id="target"></div></div></div>""");
        var outer = tree.GetElementById("outer")!.Value;
        var inner = tree.GetElementById("inner")!.Value;
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                #target { width:1px; height:1px }
                @container shell (min-width:500px) { #target { width:11px } }
                @container (min-width:500px) { #target { height:22px } }
            """]);
        var snapshot = new ContainerSnapshot { RootFontSize = 16f };
        snapshot.Boxes[outer] = ContainerBoxFor(ContainerType.InlineSize, ["shell"], 600f, 16f);
        snapshot.Boxes[inner] = ContainerBoxFor(ContainerType.InlineSize, ["other"], 300f, 16f);
        var (style, _, stats) = EvaluateContainerStyles(tree, sheet, target, snapshot);
        Assert.Equal(Dimension.Px(11f), style.Width);
        Assert.Equal(Dimension.Px(1f), style.Height);
        Assert.True(stats.AncestorSteps >= 3);
    }

    [Fact]
    public void ContainerQueryEmUsesContainerFontAndRemUsesRootFont()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="container"><div id="target"></div></div>""");
        var container = tree.GetElementById("container")!.Value;
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                #target { width:1px; height:1px }
                @container (min-width:30em) { #target { width:3px } }
                @container (min-width:30rem) { #target { height:3px } }
            """]);
        var snapshot = new ContainerSnapshot { RootFontSize = 20f };
        snapshot.Boxes[container] = ContainerBoxFor(ContainerType.InlineSize, [], 400f, 10f);
        var (style, _, _) = EvaluateContainerStyles(tree, sheet, target, snapshot);
        Assert.Equal(Dimension.Px(3f), style.Width);
        Assert.Equal(Dimension.Px(1f), style.Height);
    }

    [Fact]
    public void ContainerSnapshotComparesOnlyAxesExposedByContainerType()
    {
        var inlineA = ContainerBoxFor(ContainerType.InlineSize, [], 400f, 16f, contentHeight: 100f);
        var inlineB = ContainerBoxFor(ContainerType.InlineSize, [], 400f, 16f, contentHeight: 900f);
        Assert.Equal(inlineA, inlineB);

        var sizeA = ContainerBoxFor(ContainerType.Size, [], 400f, 16f, contentHeight: 100f);
        var sizeB = ContainerBoxFor(ContainerType.Size, [], 400f, 16f, contentHeight: 900f);
        Assert.NotEqual(sizeA, sizeB);
    }

    [Fact]
    public void NestedContainerConditionsSelectIndependentContainers()
    {
        var tree = HtmlParsing.ParseHtml(
            """<div id="outer"><div id="inner"><div id="target"></div></div></div>""");
        var outer = tree.GetElementById("outer")!.Value;
        var inner = tree.GetElementById("inner")!.Value;
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                #target { width:1px }
                @container outer (min-width:500px) {
                    @container inner (min-width:200px) {
                        #target { width:9px }
                    }
                }
            """]);

        ContainerSnapshot Snapshot(float innerWidth)
        {
            var snapshot = new ContainerSnapshot { RootFontSize = 16f };
            snapshot.Boxes[outer] = ContainerBoxFor(ContainerType.InlineSize, ["outer"], 600f, 16f);
            snapshot.Boxes[inner] = ContainerBoxFor(ContainerType.InlineSize, ["inner"], innerWidth, 16f);
            return snapshot;
        }

        var (matching, _, _) = EvaluateContainerStyles(tree, sheet, target, Snapshot(200f));
        Assert.Equal(Dimension.Px(9f), matching.Width);
        var (failing, _, _) = EvaluateContainerStyles(tree, sheet, target, Snapshot(199f));
        Assert.Equal(Dimension.Px(1f), failing.Width);
    }

    [Fact]
    public void UnknownContainerAlternativeDoesNotMaskTrueAlternative()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="container"><div id="target"></div></div>""");
        var container = tree.GetElementById("container")!.Value;
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                #target { width:1px }
                @container (future(foo)), (min-width:100px) {
                    #target { width:7px }
                }
            """]);
        var snapshot = new ContainerSnapshot { RootFontSize = 16f };
        snapshot.Boxes[container] = ContainerBoxFor(ContainerType.InlineSize, [], 100f, 16f);
        var (style, _, _) = EvaluateContainerStyles(tree, sheet, target, snapshot);
        Assert.Equal(Dimension.Px(7f), style.Width);
    }

    [Fact]
    public void BlockAxisQueryRequiresSizeContainer()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="container"><div id="target"></div></div>""");
        var container = tree.GetElementById("container")!.Value;
        var target = tree.GetElementById("target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
                #target { width:1px }
                @container (height >= 100px) { #target { width:8px } }
            """]);

        ContainerSnapshot Snapshot(ContainerType containerType)
        {
            var snapshot = new ContainerSnapshot { RootFontSize = 16f };
            snapshot.Boxes[container] = ContainerBoxFor(containerType, [], 300f, 16f);
            return snapshot;
        }

        var (inlineOnly, _, _) = EvaluateContainerStyles(
            tree,
            sheet,
            target,
            Snapshot(ContainerType.InlineSize));
        Assert.Equal(Dimension.Px(1f), inlineOnly.Width);

        var (size, _, _) = EvaluateContainerStyles(tree, sheet, target, Snapshot(ContainerType.Size));
        Assert.Equal(Dimension.Px(8f), size.Width);
    }

    [Fact]
    public void UnresolvedContainerRulesDoNotEnterCascadeOrPseudos()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="target"></div>""");
        var target = tree.QuerySelector("#target")!.Value;
        var sheet = Stylesheet.Parse(tree, ["""
            #target{width:10px}
            @container (min-width:28rem){
                #target{width:999px}
                #target::before{content:"inactive"}
            }
            #target{height:20px}
            """]);
        Assert.True(sheet.Rules.Count == 3, "conditional rule remains indexed");
        Assert.Equal(2, sheet.ContainerConditions.Count);
        var matcher = tree.CreateMatcher();
        var style = new LayoutStyle();
        sheet.Apply(tree, matcher, target, "target", [], "div", style, NoProps, null);
        Assert.Equal(Dimension.Px(10f), style.Width);
        Assert.Equal(Dimension.Px(20f), style.Height);
        var (before, after) = sheet.PseudoStyles(tree, matcher, target, NoProps, style);
        Assert.True(before is null && after is null);
    }
}
