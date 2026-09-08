using Obscura.Render.Css;
using Xunit;

namespace Obscura.Render.Tests;

/// <summary>
/// xUnit port of the <c>#[cfg(test)] mod tests</c> block in
/// <c>crates/obscura-render/src/css.rs</c>. Test names and order follow the
/// Rust originals so the mapping stays obvious.
/// </summary>
public sealed class CssTests
{
    // ---------------------------------------------------------------- helpers

    /// <summary>Rust: <c>test_invalidation_map</c>.</summary>
    private static InvalidationMap TestInvalidationMap(string css) =>
        CssInvalidationMapBuilder.Build([css], (1280f, 720f));

    /// <summary>Rust: <c>dependencies_reach</c>.</summary>
    private static bool DependenciesReach(
        IReadOnlyList<InvalidationDependency> dependencies,
        InvalidationReaches reaches)
    {
        foreach (var dependency in dependencies)
        {
            if (dependency.Reaches.Contains(reaches))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rust: <c>condition_arena_root</c>.</summary>
    private static List<ContainerConditionNode> ConditionArenaRoot() => CssParser.NewConditionArena();

    /// <summary>Rust: <c>container_box</c>.</summary>
    private static ContainerBox ContainerBoxFor(
        ContainerType containerType,
        string[] names,
        float contentWidth,
        float fontSize) =>
        new()
        {
            ContainerType = containerType,
            AvailableType = containerType,
            Names = names,
            ContentWidth = contentWidth,
            ContentHeight = 100f,
            FontSize = fontSize,
        };

    /// <summary>
    /// Stands in for the <c>DomTree</c> node the Rust invalidation predicates
    /// take. The DOM lives in <c>Obscura.Dom</c>; this port sees it through
    /// <see cref="ICssElementView"/>.
    /// </summary>
    private sealed class FakeElement(string localName, params (string Name, string Value)[] attributes)
        : ICssElementView
    {
        public bool IsElement => true;

        public string LocalName { get; } = localName;

        public bool IsQuirks { get; init; }

        public string? GetAttribute(string name)
        {
            foreach (var (attributeName, value) in attributes)
            {
                if (string.Equals(attributeName, name, StringComparison.Ordinal))
                {
                    return value;
                }
            }

            return null;
        }

        public IReadOnlyList<string> AttributeNames => [.. attributes.Select(attribute => attribute.Name)];
    }

    // -------------------------------------------------------- invalidation map

    [Fact]
    public void InvalidationMapClassifiesCompoundReachWithoutFalseNegatives()
    {
        var map = TestInvalidationMap("""
                #self { color:red }
                #ancestor .subject { color:red }
                #parent > .child { color:red }
                #previous + .adjacent { color:red }
                #earlier ~ .later { color:red }
                .repeat .repeat { color:red }
            """);

        Assert.True(DependenciesReach(map.IdDependencies("self"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.IdDependencies("ancestor"), InvalidationReaches.Descendants));
        Assert.True(DependenciesReach(map.IdDependencies("parent"), InvalidationReaches.Descendants));
        Assert.True(DependenciesReach(map.IdDependencies("previous"), InvalidationReaches.Siblings));
        Assert.True(DependenciesReach(map.IdDependencies("earlier"), InvalidationReaches.Siblings));

        var repeated = map.ClassDependencies("repeat");
        Assert.True(repeated.Count == 1, "same-rule dependencies must merge");
        Assert.True(repeated[0].Reaches.Contains(InvalidationReaches.Self));
        Assert.True(repeated[0].Reaches.Contains(InvalidationReaches.Descendants));
        Assert.False(map.RequiresConservativeInvalidation);
    }

    [Fact]
    public void InvalidationMapDistinguishesTreeSiblingPathsFromHasPaths()
    {
        var map = TestInvalidationMap("""
                .left + .right { color:red }
                :is(.early ~ .late, .plain) { color:blue }
                .host:has(.inside + .peer) { color:green }
                [data-token="+"] { color:black }
            """);
        Assert.True(map.HasAdjacentSiblingSelectors);
        Assert.True(map.HasGeneralSiblingSelectors);

        var relationalOnly = TestInvalidationMap(".host:has(.inside + .peer){color:red}");
        Assert.False(relationalOnly.HasAdjacentSiblingSelectors);
        Assert.False(relationalOnly.HasGeneralSiblingSelectors);
    }

    [Fact]
    public void InvalidationMapIndexesAttributesStatesFunctionsAndEscapedKeys()
    {
        var map = TestInvalidationMap("""
                [data-theme] .panel:hover { color:red }
                :is(.alpha, #beta) > button:focus-visible { color:red }
                button:not([disabled]) { color:red }
                .sm\:hover\:px-2[data-KIND] { color:red }
                .group:focus-within .icon { color:red }
            """);

        Assert.True(DependenciesReach(map.AttributeDependencies("DATA-THEME"), InvalidationReaches.Descendants));
        Assert.True(DependenciesReach(map.ClassDependencies("panel"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.StateDependencies("hover"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.ClassDependencies("alpha"), InvalidationReaches.Descendants));
        Assert.True(DependenciesReach(map.IdDependencies("beta"), InvalidationReaches.Descendants));
        Assert.True(DependenciesReach(map.StateDependencies("focus-visible"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.LocalNameDependencies("BUTTON"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.AttributeDependencies("disabled"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.ClassDependencies("sm:hover:px-2"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.AttributeDependencies("data-kind"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.StateDependencies("focus-within"), InvalidationReaches.Descendants));
        Assert.False(map.RequiresConservativeInvalidation);
    }

    [Fact]
    public void InvalidationMapIndexesDeclarationAttrDependencies()
    {
        var map = TestInvalidationMap("""
                .label::before { content: ATTR( DATA-LABEL ) }
                .typed { --label: attr(Aria-Label string, "fallback") }
                .noise::after {
                    content: "attr(data-in-string)";
                    background: url("attr(data-in-url)");
                    color: red /* attr(data-in-comment) */;
                }
            """);

        Assert.True(DependenciesReach(map.AttributeDependencies("data-label"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.AttributeDependencies("ARIA-LABEL"), InvalidationReaches.Self));
        Assert.Empty(map.AttributeDependencies("data-in-string"));
        Assert.Empty(map.AttributeDependencies("data-in-url"));
        Assert.Empty(map.AttributeDependencies("data-in-comment"));
    }

    [Fact]
    public void InvalidationMapMarksRelativeAndNthDependenciesConservative()
    {
        var map = TestInvalidationMap("""
                .card:has(> .badge) { color:red }
                .row:has(+ .notice) .label { color:red }
                .item:nth-child(2n of .eligible) { color:red }
                .typed:nth-of-type(odd) { color:red }
                .trigger ~ .branch .leaf { color:red }
                :root { color:red }
                a:link, a:visited, :target { color:red }
            """);

        Assert.True(map.RequiresConservativeInvalidation);
        Assert.True(map.ConservativeRuleOrders.Count >= 3);
        Assert.False(map.HasUnkeyedRelationalRules);
        Assert.True(DependenciesReach(map.ClassDependencies("badge"), InvalidationReaches.Conservative));
        Assert.True(DependenciesReach(map.ClassDependencies("notice"), InvalidationReaches.Conservative));
        Assert.True(DependenciesReach(map.ClassDependencies("eligible"), InvalidationReaches.Conservative));
        Assert.NotEmpty(map.StateDependencies("nth-child"));
        Assert.NotEmpty(map.StateDependencies("nth-of-type"));

        var trigger = map.ClassDependencies("trigger");
        Assert.True(DependenciesReach(trigger, InvalidationReaches.Siblings));
        Assert.True(DependenciesReach(trigger, InvalidationReaches.Conservative));
        Assert.True(DependenciesReach(map.ClassDependencies("branch"), InvalidationReaches.Descendants));

        foreach (var state in (string[])["root", "link", "visited", "target"])
        {
            Assert.True(
                map.StateDependencies(state).Count != 0,
                $"missing conservative state dependency for :{state}");
        }
    }

    [Fact]
    public void InvalidationMapDistinguishesKeyedAndUnkeyedRelationalSubjects()
    {
        var keyed = TestInvalidationMap(
            ".card:has(> .badge), .row:has(.icon[data-live]), .copy:has(> span), .choice:has(:is(.yes,button)) { color:red }");
        Assert.False(keyed.HasUnkeyedRelationalRules);
        Assert.Contains(keyed.ClassDependencies("badge"), dependency => keyed.IsRelationalRule(dependency.RuleOrder));
        Assert.Contains(keyed.LocalNameDependencies("span"), dependency => keyed.IsRelationalRule(dependency.RuleOrder));

        foreach (var selector in (string[])
        [
            ".card:has(> *){color:red}",
            ".card:has(:is(.badge,*)){color:red}",
            ".card:has(:empty){color:red}",
        ])
        {
            Assert.True(TestInvalidationMap(selector).HasUnkeyedRelationalRules, selector);
        }
    }

    [Fact]
    public void RelationalInvalidationRecordsAnchorReachAndTreeSideEffects()
    {
        var map = TestInvalidationMap("""
                .host:has(.signal) .out { color:red }
                .row:has(+ :is(.notice,.warning)) { color:red }
                .card:has(.item:first-child) { color:red }
                .shell:has(.label:empty) { color:red }
                .anchor:has(.signal) ~ .panel .leaf { color:red }
            """);
        var entries = map.RelationalInvalidations;
        Assert.Equal(5, entries.Count);
        Assert.True(entries[0].AnchorReaches.Contains(InvalidationReaches.Descendants));
        Assert.False(entries[0].UnkeyedSubject);
        Assert.True(entries[1].SiblingSideEffect);
        Assert.False(entries[1].UnkeyedSubject);
        Assert.True(entries[2].StructuralSideEffect);
        Assert.False(entries[2].TextSideEffect);
        Assert.True(entries[3].StructuralSideEffect);
        Assert.True(entries[3].TextSideEffect);
        Assert.True(entries[4].UnrepresentableOuterPath);

        var host = new FakeElement("section", ("id", "host"), ("class", "host"));
        var other = new FakeElement("section", ("id", "other"), ("class", "other"));
        Assert.True(entries[0].AnchorMayMatch(host));
        Assert.False(entries[0].AnchorMayMatch(other));
    }

    [Fact]
    public void RelationalAnchorFilterNeverUnqualifiesNamespacedAttributes()
    {
        foreach (var compound in (string[])
        [
            "[xlink|href]:has(.signal)",
            "[*|href]:has(.signal)",
            "[|href]:has(.signal)",
        ])
        {
            Assert.True(CssSelectorText.RelationalAnchorKey(compound) is null, compound);
        }

        Assert.Equal(
            RelationalSelectorKey.Class("host"),
            CssSelectorText.RelationalAnchorKey("[xlink|href].host:has(.signal)"));
    }

    [Fact]
    public void InvalidationMapIncludesGeneratedContentHostDependencies()
    {
        var map = TestInvalidationMap("""
                .toolbar[data-open] > .button:hover::before { content:"x" }
                #status::after { content:"ok" }
            """);

        Assert.True(DependenciesReach(map.ClassDependencies("toolbar"), InvalidationReaches.Descendants));
        Assert.True(DependenciesReach(map.AttributeDependencies("data-open"), InvalidationReaches.Descendants));
        Assert.True(DependenciesReach(map.ClassDependencies("button"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.StateDependencies("hover"), InvalidationReaches.Self));
        Assert.True(DependenciesReach(map.IdDependencies("status"), InvalidationReaches.Self));
    }

    // ------------------------------------------------------------ cascade layers

    [Fact]
    public void LayerOrderStatementBeatsBlockSourceOrder()
    {
        var registry = new LayerRegistry();
        registry.RegisterStatement(null, "base, components, utilities");
        var utilities = registry.RegisterNamed(null, "utilities");
        var baseLayer = registry.RegisterNamed(null, "base");

        // The statement fixed the order, so a later `@layer base` block is still
        // weaker than `utilities`.
        Assert.True(CssAtRules.CompareLayerOrder(utilities, baseLayer) > 0);
    }

    [Fact]
    public void UnlayeredNormalBeatsLayeredHigherSpecificity()
    {
        var registry = new LayerRegistry();
        var layer = registry.RegisterNamed(null, "components");
        Assert.True(CssAtRules.CompareRuleCascade(null, 1, 0, layer, 1000, 99, important: false) > 0);
    }

    [Fact]
    public void ImportantLayerOrderIsReversedAndBeatsUnlayered()
    {
        var registry = new LayerRegistry();
        var first = registry.RegisterNamed(null, "first");
        var second = registry.RegisterNamed(null, "second");

        Assert.True(CssAtRules.CompareRuleCascade(second, 1, 0, first, 1, 1, important: false) > 0);
        Assert.True(CssAtRules.CompareRuleCascade(first, 1, 0, second, 1, 1, important: true) > 0);
        Assert.True(CssAtRules.CompareRuleCascade(first, 1, 0, null, 1, 1, important: true) > 0);
    }

    [Fact]
    public void NestedAndAnonymousLayersKeepParentDirectPrecedence()
    {
        var registry = new LayerRegistry();
        var parent = registry.RegisterNamed(null, "framework");
        var child = registry.RegisterNamed(parent, "reset");
        var anonymous = registry.RegisterAnonymous(parent);

        // A direct declaration in the containing layer is the implicit final
        // sub-layer, so the parent beats both of its children.
        Assert.True(CssAtRules.CompareLayerOrder(parent, child) > 0);
        Assert.True(CssAtRules.CompareLayerOrder(anonymous, child) > 0);
        Assert.True(CssAtRules.CompareLayerOrder(parent, anonymous) > 0);
    }

    [Fact]
    public void LayerRegistrySpansMultipleStylesheetSources()
    {
        var registry = new LayerRegistry();
        var fromFirstSource = registry.RegisterNamed(null, "shared");
        var fromSecondSource = registry.RegisterNamed(null, "shared");
        Assert.Equal(0, CssAtRules.CompareLayerOrder(fromFirstSource, fromSecondSource));
    }

    // -------------------------------------------------------- generated content

    [Fact]
    public void GeneratedContentResolvesHostAttributesAndResets()
    {
        string? Attributes(string name) =>
            string.Equals(name, "data-label", StringComparison.Ordinal) ? "Get Started" : null;

        var resolved = CssValues.ExtractContent("content:attr(data-label)", Attributes);
        Assert.True(resolved.Found);
        Assert.Equal(
            [new GeneratedContentItem.Text("Get Started")],
            resolved.Items);

        var reset = CssValues.ExtractContent("""content:"fallback";content:none""", Attributes);
        Assert.True(reset.Found);
        Assert.Null(reset.Items);
    }

    [Fact]
    public void GeneratedContentParsesCounterItemsAndStyles()
    {
        var parsed = CssValues.ExtractContent(
            """content:"[" counters(section, ".", upper-roman) "] " counter(line)""",
            static _ => null);

        Assert.True(parsed.Found);
        Assert.Equal(
            [
                new GeneratedContentItem.Text("["),
                new GeneratedContentItem.Counters("section", ".", GeneratedCounterStyle.UpperRoman),
                new GeneratedContentItem.Text("] "),
                new GeneratedContentItem.Counter("line", GeneratedCounterStyle.Decimal),
            ],
            parsed.Items);

        Assert.Equal("aa", CssValues.FormatCounterValue(27, GeneratedCounterStyle.LowerAlpha));
        Assert.Equal("XIV", CssValues.FormatCounterValue(14, GeneratedCounterStyle.UpperRoman));
        Assert.Equal("-04", CssValues.FormatCounterValue(-4, GeneratedCounterStyle.DecimalLeadingZero));
    }

    [Fact]
    public void KeyframesRetainEveryAnimationOffset()
    {
        const string Css = """
            @keyframes dismiss {
                from { opacity: 1; visibility: visible; }
                50% { opacity: .5; }
                to { opacity: 0; visibility: hidden; }
            }
            @-webkit-keyframes slide {
                0% { transform: translateX(0); }
                100% { transform: translateX(20px); }
            }
            """;

        var keyframes = CssParser.ExtractKeyframes(Css)
            .ToDictionary(entry => entry.Name, entry => entry.Frames, StringComparer.Ordinal);

        var dismiss = CssKeyframes.NormalizedOffsets(keyframes["dismiss"].Stops);
        Assert.Equal([0f, 0.5f, 1f], dismiss.Select(stop => stop.Offset));
        Assert.Contains("opacity: 1", dismiss[0].Stop.Declarations, StringComparison.Ordinal);
        Assert.Contains("visibility: hidden", dismiss[2].Stop.Declarations, StringComparison.Ordinal);

        var slide = CssKeyframes.NormalizedOffsets(keyframes["slide"].Stops);
        Assert.Equal([0f, 1f], slide.Select(stop => stop.Offset));
        Assert.Contains("translateX(20px)", slide[1].Stop.Declarations, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ media queries

    [Fact]
    public void MediaRulesUseTheLiveWidthAndHeight()
    {
        const string Css = """
            .base { color: black; }
            @media (max-width: 950px) { .narrow { color: green; } }
            @media (min-height: 900px) { .tall { color: blue; } }
            @media (orientation: portrait) { .portrait { color: red; } }
            """;

        static List<string> Selectors(string css, (float, float) viewport) =>
            [.. CssParser.ParseStylesheetForViewport(css, viewport).Select(rule => rule.Selector)];

        var narrowTall = Selectors(Css, (900f, 1000f));
        Assert.Contains(".base", narrowTall);
        Assert.Contains(".narrow", narrowTall);
        Assert.Contains(".tall", narrowTall);
        Assert.Contains(".portrait", narrowTall);

        var wideShort = Selectors(Css, (1280f, 720f));
        Assert.Contains(".base", wideShort);
        Assert.DoesNotContain(".narrow", wideShort);
        Assert.DoesNotContain(".tall", wideShort);
        Assert.DoesNotContain(".portrait", wideShort);
    }

    [Fact]
    public void NestedMediaRulesUseTheLiveViewport()
    {
        const string Css = ".card { display:block; @media (max-width: 950px) { width:100%; } }";
        var narrow = CssParser.ParseStylesheetForViewport(Css, (900f, 1000f));
        var wide = CssParser.ParseStylesheetForViewport(Css, (1280f, 720f));
        Assert.Contains(narrow, rule =>
            rule.Selector == ".card" && rule.Declarations.Contains("width:100%", StringComparison.Ordinal));
        Assert.DoesNotContain(wide, rule => rule.Declarations.Contains("width:100%", StringComparison.Ordinal));
    }

    // -------------------------------------------------------- container queries

    [Fact]
    public void TailwindContainerQueryIsRetainedButSimpleParserOmitsIt()
    {
        const string Css = """
            .base { width: 10px }
            @container (min-width: 28rem) {
                .\@md\:flex-row { flex-direction: row }
            }
            @container main not (max-inline-size: 60em) {
                .named { width: 20px }
            }
            """;

        var conditions = ConditionArenaRoot();
        var parsed = CssParser.ParseStylesheetForViewportPreservingContainers(
            Css,
            (1280f, 720f),
            CssMediaType.Screen,
            conditions,
            ContainerConditionId.None);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(new ContainerConditionId(1), parsed[1].ContainerConditionId);
        Assert.Equal(new ContainerConditionId(2), parsed[2].ContainerConditionId);
        Assert.Equal(3, conditions.Count);
        Assert.Equal(
            new ContainerQueryExpr.Feature(new ContainerSizeFeature(
                ContainerQueryAxis.Width,
                ContainerQueryComparison.Min,
                ContainerQueryLength.Rem(28f))),
            conditions[1].Alternatives[0].Condition);
        Assert.Equal("main", conditions[2].Alternatives[0].Name);
        Assert.IsType<ContainerQueryExpr.Not>(conditions[2].Alternatives[0].Condition);
        Assert.Single(CssParser.ParseStylesheet(Css));
    }

    [Fact]
    public void ContainerAtRuleKeywordIsAsciiInsensitiveAndExact()
    {
        Assert.Equal("(min-width:1px)", CssAtRules.Prelude("CoNtAiNeR (min-width:1px)", "container"));
        Assert.Equal("(min-width:1px)", CssAtRules.Prelude("container/**/(min-width:1px)", "container"));
        Assert.Null(CssAtRules.Prelude("containerfoo (min-width:1px)", "container"));
        Assert.Null(CssAtRules.Prelude("container-type (min-width:1px)", "container"));

        const string Css = """
            @CONTAINER (min-width:1px) {
                .top-level { width:1px }
            }
            .host {
                @CoNtAiNeR (min-width:2px) { width:2px }
                @containerfoo (min-width:3px) { height:3px }
            }
            .comment-host {
                @CONTAINER/**/(min-width:5px) { height:5px }
            }
            @containerfoo (min-width:4px) {
                .unknown-prefix { width:4px }
            }
            """;

        var conditions = ConditionArenaRoot();
        var parsed = CssParser.ParseStylesheetForViewportPreservingContainers(
            Css,
            (1280f, 720f),
            CssMediaType.Screen,
            conditions,
            ContainerConditionId.None);

        Assert.Equal(4, conditions.Count);
        Assert.True(parsed.Count == 3, "unknown prefix at-rules must be dropped");
        Assert.Contains(parsed, rule => rule.Selector == ".top-level");
        Assert.Contains(parsed, rule =>
            rule.Selector == ".host" && rule.Declarations.Contains("width:2px", StringComparison.Ordinal));
        Assert.Contains(parsed, rule =>
            rule.Selector == ".comment-host" && rule.Declarations.Contains("height:5px", StringComparison.Ordinal));
        Assert.DoesNotContain(parsed, rule =>
            rule.Selector == ".unknown-prefix" || rule.Declarations.Contains("height:3px", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalAtRulesInsideContainerAreNotConditioned()
    {
        const string Css = """
            @container (min-width:10000px) {
                @property --cq-token {
                    syntax: "<length>";
                    inherits: false;
                    initial-value: 17px;
                }
                .conditional { width:999px }
            }
            """;

        var conditions = ConditionArenaRoot();
        var parsed = CssParser.ParseStylesheetForViewportPreservingContainers(
            Css,
            (1280f, 720f),
            CssMediaType.Screen,
            conditions,
            ContainerConditionId.None);

        Assert.Equal(2, conditions.Count);
        var registration = Assert.Single(
            parsed.Where(rule => rule.Selector == "\0property:--cq-token"));
        Assert.Equal(ContainerConditionId.None, registration.ContainerConditionId);
        Assert.Contains("initial-value: 17px", registration.Declarations, StringComparison.Ordinal);

        var conditional = Assert.Single(parsed.Where(rule => rule.Selector == ".conditional"));
        Assert.Equal(new ContainerConditionId(1), conditional.ContainerConditionId);
        Assert.Empty(CssParser.ParseStylesheet(Css));
    }

    // ----------------------------------------------- registered custom properties

    [Fact]
    public void WildcardDuplicateAndInvalidPropertyRegistrationsAreBounded()
    {
        Assert.Equal(
            new RegisteredCustomProperty("*", false, null),
            CssPropertyRegistration.Parse("""syntax:"*"; inherits:false"""));

        // No `inherits` descriptor: the registration is dropped entirely.
        Assert.Null(CssPropertyRegistration.Parse("""syntax:"<number>"; initial-value:9"""));

        // An unsupported syntax is dropped.
        Assert.Null(CssPropertyRegistration.Parse(
            """syntax:"<angle>"; inherits:false; initial-value:30deg"""));

        // A typed registration needs an initial value that matches the syntax.
        Assert.Equal(
            new RegisteredCustomProperty("<number>", false, "2"),
            CssPropertyRegistration.Parse("""syntax:"<number>"; inherits:false; initial-value:2"""));
        Assert.Null(CssPropertyRegistration.Parse(
            """syntax:"<number>"; inherits:false; initial-value:red"""));
    }

    [Fact]
    public void RegisteredPercentageInitialValueKeepsRadialGradientValid()
    {
        var registration = CssPropertyRegistration.Parse(
            """syntax:"<percentage>"; inherits:false; initial-value:75%""");
        Assert.NotNull(registration);
        Assert.Equal("75%", registration.InitialValue);
        Assert.True(CssPropertyRegistration.ValueMatches(registration, "75%"));
        Assert.False(CssPropertyRegistration.ValueMatches(registration, "75px"));
    }

    [Fact]
    public void VarSubstitutionPreservesNeighboringTokenBoundaries()
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--stroke"] = "2px",
            ["--amount"] = "10",
        };

        Assert.Equal("2px solid", CssVariables.SubstituteVarValue("var(--stroke)solid", properties, 0));
        Assert.Equal(
            "calc(2px*3)",
            CssVariables.SubstituteVarValue("calc(var(--stroke)*3)", properties, 0));
        Assert.Equal(
            "calc(10- 2px)",
            CssVariables.SubstituteVarValue("calc(var(--amount)- 2px)", properties, 0));
        Assert.Equal("+ 10", CssVariables.SubstituteVarValue("+var(--amount)", properties, 0));
        Assert.Equal("10 %", CssVariables.SubstituteVarValue("var(--amount)%", properties, 0));

        // Invalidity propagates through an intermediate custom property so an
        // outer var() can use its own fallback.
        var chained = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--toggle"] = "var(--missing) dark",
        };
        Assert.Null(CssVariables.SubstituteVarValue("var(--toggle)", chained, 0));
        Assert.Equal("light", CssVariables.SubstituteVarValue("var(--toggle,light)", chained, 0));
    }

    [Fact]
    public void ContainerBooleanGrammarRejectsMixedOperators()
    {
        foreach (var invalid in (string[])
        [
            "(min-width:1px) and (max-width:2px) or (min-inline-size:3px)",
            "not (min-width:1px) and (max-width:2px)",
            "(min-width:1px) or not (max-width:2px)",
        ])
        {
            Assert.True(
                CssContainerQuery.ParseExpr(invalid) is null,
                $"invalid boolean grammar was accepted: {invalid}");
        }

        Assert.IsType<ContainerQueryExpr.And>(
            CssContainerQuery.ParseExpr("(min-width:1px) and ((max-width:2px) or (min-inline-size:3px))"));
        Assert.IsType<ContainerQueryExpr.Not>(
            CssContainerQuery.ParseExpr("not ((min-width:1px) and (max-inline-size:2px))"));
    }

    [Fact]
    public void ContainerCustomIdentRejectsCssWideAndDefault()
    {
        foreach (var reserved in (string[])
        [
            "none", "not", "and", "or", "default",
            "initial", "inherit", "unset", "revert", "revert-layer",
        ])
        {
            Assert.True(
                CssContainerQuery.ParseQueryName(reserved) is null,
                $"reserved custom-ident was accepted: {reserved}");
            if (reserved != "not")
            {
                Assert.True(
                    CssContainerQuery.ParseQuery($"{reserved} (min-width:1px)") is null,
                    $"reserved query name was accepted: {reserved}");
            }
        }

        var unary = CssContainerQuery.ParseQuery("not (min-width:1px)");
        Assert.True(
            unary is { Name: null, Condition: ContainerQueryExpr.Not },
            "`not` is reserved as a name but valid as the unary query operator");

        foreach (var valid in (string[])["auto", "normal", "container", "--card", "main"])
        {
            Assert.True(
                CssContainerQuery.ParseQueryName(valid) == valid,
                $"valid query name was rejected: {valid}");
        }
    }

    [Fact]
    public void UnknownCommaArmDoesNotDropSupportedArm()
    {
        var queries = CssContainerQuery.ParseQueryList("(future(foo)), main (min-width:1px)");
        Assert.NotNull(queries);
        Assert.Equal(2, queries.Count);
        Assert.Equal(ContainerQueryExpr.Unknown.Instance, queries[0].Condition);
        Assert.Equal("main", queries[1].Name);
        Assert.IsType<ContainerQueryExpr.Feature>(queries[1].Condition);

        var conditions = ConditionArenaRoot();
        var parsed = CssParser.ParseStylesheetForViewportPreservingContainers(
            "@container (future(foo)), main (min-width:1px) {.card{display:grid}}",
            (1280f, 720f),
            CssMediaType.Screen,
            conditions,
            ContainerConditionId.None);

        Assert.Single(parsed);
        Assert.Equal(2, conditions[1].Alternatives.Count);
    }

    [Fact]
    public void ContainerQueryRecursionDepthIsBounded()
    {
        var nested = new string('(', CssContainerQueryDepth + 16)
            + "min-width:1px"
            + new string(')', CssContainerQueryDepth + 16);
        Assert.True(CssContainerQuery.ParseExpr(nested) is null);
    }

    private const int CssContainerQueryDepth = 64;

    [Fact]
    public void SupportsAcceptsContainerCssWideValues()
    {
        foreach (var property in (string[])["container", "container-name", "container-type"])
        {
            foreach (var keyword in (string[])["initial", "inherit", "unset", "revert", "revert-layer"])
            {
                Assert.True(
                    CssSupports.ConditionApplies($"({property}:{keyword})"),
                    $"{property}:{keyword} is a valid whole-value CSS-wide declaration");
            }
        }
    }

    [Fact]
    public void ContainerQueryBooleanEvaluationUsesKleeneTruthTables()
    {
        var container = ContainerBoxFor(ContainerType.InlineSize, [], 200f, 16f);
        static ContainerQueryExpr MinWidth(float threshold) =>
            new ContainerQueryExpr.Feature(new ContainerSizeFeature(
                ContainerQueryAxis.Width,
                ContainerQueryComparison.Min,
                ContainerQueryLength.Px(threshold)));

        Assert.Equal(
            ContainerQueryTruth.True,
            CssContainerQuery.Evaluate(
                new ContainerQueryExpr.Or([MinWidth(100f), ContainerQueryExpr.Unknown.Instance]),
                container,
                16f));
        Assert.Equal(
            ContainerQueryTruth.False,
            CssContainerQuery.Evaluate(
                new ContainerQueryExpr.And([MinWidth(300f), ContainerQueryExpr.Unknown.Instance]),
                container,
                16f));
        Assert.Equal(
            ContainerQueryTruth.Unknown,
            CssContainerQuery.Evaluate(
                new ContainerQueryExpr.Or([MinWidth(300f), ContainerQueryExpr.Unknown.Instance]),
                container,
                16f));
        Assert.Equal(
            ContainerQueryTruth.Unknown,
            CssContainerQuery.Evaluate(
                new ContainerQueryExpr.Not(ContainerQueryExpr.Unknown.Instance),
                container,
                16f));
    }

    [Fact]
    public void ContainerRangeSyntaxSupportsStrictInclusiveAndChainedQueries()
    {
        var container = ContainerBoxFor(ContainerType.Size, [], 200f, 16f);
        foreach (var query in (string[])
        [
            "(width)",
            "(width > 199px)",
            "(width>=200px)",
            "(199px < width)",
            "(199px < width <= 200px)",
            "(height = 100px)",
            "(block-size >= 100px)",
        ])
        {
            var expression = CssContainerQuery.ParseExpr(query);
            Assert.NotNull(expression);
            Assert.True(
                CssContainerQuery.Evaluate(expression, container, 16f) == ContainerQueryTruth.True,
                query);
        }

        foreach (var query in (string[])
        [
            "(width > 200px)",
            "(width < 200px)",
            "(200px < width < 300px)",
            "(height > 100px)",
        ])
        {
            var expression = CssContainerQuery.ParseExpr(query);
            Assert.NotNull(expression);
            Assert.True(
                CssContainerQuery.Evaluate(expression, container, 16f) == ContainerQueryTruth.False,
                query);
        }

        foreach (var invalid in (string[])
        [
            "(100px < width > 200px)",
            "(200px > width < 100px)",
            "(100px = width = 100px)",
            "(100px < width = 200px)",
        ])
        {
            Assert.True(
                CssContainerQuery.ParseExpr(invalid) is null,
                $"invalid mixed/equality chain parsed: {invalid}");
        }
    }

    [Fact]
    public void NestedContainerRulesFormAParentConditionChain()
    {
        const string Css = "@container shell (min-width:40rem){"
            + "@container (max-inline-size:50rem){.card{display:grid}}}";
        var conditions = ConditionArenaRoot();
        var parsed = CssParser.ParseStylesheetForViewportPreservingContainers(
            Css,
            (1280f, 720f),
            CssMediaType.Screen,
            conditions,
            ContainerConditionId.None);

        Assert.Single(parsed);
        Assert.Equal(new ContainerConditionId(2), parsed[0].ContainerConditionId);
        Assert.Equal(new ContainerConditionId(1), conditions[2].Parent);
        Assert.Equal(ContainerConditionId.None, conditions[1].Parent);
    }

    [Fact]
    public void NoContainerParserOutputAndOrderAreUnchanged()
    {
        const string Css = ".card{width:10px}"
            + "@supports (display:grid){.card{display:grid}}"
            + "@media (min-width:64rem){.card{width:20px}}"
            + ".card{height:30px}";

        Assert.Equal(
            [
                (".card", "width:10px;"),
                (".card", "display:grid;"),
                (".card", "width:20px;"),
                (".card", "height:30px;"),
            ],
            CssParser.ParseStylesheetForViewport(Css, (1280f, 720f)));

        var conditions = ConditionArenaRoot();
        var rich = CssParser.ParseStylesheetForViewportPreservingContainers(
            Css,
            (1280f, 720f),
            CssMediaType.Screen,
            conditions,
            ContainerConditionId.None);

        Assert.Single(conditions);
        Assert.All(rich, rule => Assert.Equal(ContainerConditionId.None, rule.ContainerConditionId));
    }

    // ---------------------------------------------------------------- @supports

    [Fact]
    public void SupportsConditionsGateLegacyFrameworkFallbacks()
    {
        const string LegacyProbe = "(((-webkit-hyphens:none)) and "
            + "(not (margin-trim:inline))) or "
            + "((-moz-orient:inline) and "
            + "(not (color:rgb(from red r g b))))";

        Assert.False(CssSupports.ConditionApplies(LegacyProbe));
        Assert.True(CssSupports.ConditionApplies("(display:grid) and (selector(.card > *))"));
        Assert.True(CssSupports.ConditionApplies("not (unknown-engine-prop:value)"));

        var css = $"@supports {LegacyProbe} {{ .legacy {{ line-height:1.5 }} }}"
            + "@supports (display:grid) { .modern { display:grid } }"
            + $".host {{ @supports {LegacyProbe} {{ width:999px }} }}";
        var rules = CssParser.ParseStylesheetForViewport(css, (1280f, 720f));

        Assert.DoesNotContain(rules, rule =>
            rule.Selector == ".legacy" || rule.Declarations.Contains("999px", StringComparison.Ordinal));
        Assert.Contains(rules, rule =>
            rule.Selector == ".modern" && rule.Declarations.Contains("display:grid", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportsConditionsRejectInvalidBooleanGrammar()
    {
        Assert.True(CssSupports.ConditionApplies("(display:grid) and (word-break:break-all)"));
        Assert.True(CssSupports.ConditionApplies("(display:grid) or (unknown:value)"));
        Assert.False(CssSupports.ConditionApplies("(display:grid) and (word-break:break-all) or (display:flex)"));
        Assert.False(CssSupports.ConditionApplies("not ((display:grid) and (word-break:break-all) or (display:flex))"));
        Assert.False(CssSupports.ConditionApplies("not ()"));
        Assert.False(CssSupports.ConditionApplies("(display:grid;)"));
        Assert.False(CssSupports.ConditionApplies("not (display:grid"));
    }

    [Fact]
    public void SupportsPolygonClipPathActivatesOnlyPaintedShapeSubset()
    {
        const string Css = "@supports (clip-path:polygon(0 0,100% 0,50% 100%)){"
            + ".transition{height:90px}"
            + "}"
            + "@supports (clip-path:polygon(0 0,100% 0,50% 100%) content-box){"
            + ".unsupported{height:999px}"
            + "}";

        var rules = CssParser.ParseStylesheetForViewport(Css, (1280f, 720f));
        Assert.Contains(rules, rule =>
            rule.Selector == ".transition" && rule.Declarations.Contains("height:90px", StringComparison.Ordinal));
        Assert.DoesNotContain(rules, rule => rule.Selector == ".unsupported");
    }

    [Fact]
    public void MediaBreakpointsSupportFontRelativeLengthsAndRanges()
    {
        Assert.False(CssMediaQuery.AppliesForViewport("@media (min-width: 64rem)", (900f, 1000f)));
        Assert.True(CssMediaQuery.AppliesForViewport("@media (min-width: 64rem)", (1024f, 768f)));
        Assert.True(CssMediaQuery.AppliesForViewport("@media (56.25rem <= width)", (900f, 1000f)));
        Assert.False(CssMediaQuery.AppliesForViewport("@media (width > calc(60em - 1px))", (900f, 1000f)));

        const string TwoSidebarBreakpoint = "@media (width < calc(1rem * 2 + (15rem + 2rem) * 2 + 31rem))";
        Assert.True(CssMediaQuery.AppliesForViewport(TwoSidebarBreakpoint, (1000f, 900f)));
        Assert.False(CssMediaQuery.AppliesForViewport(TwoSidebarBreakpoint, (1440f, 1000f)));

        const string LeftWidthCalc = "@media (calc(1rem * 2 + (15rem + 2rem) * 2 + 31rem) <= width)";
        Assert.False(CssMediaQuery.AppliesForViewport(LeftWidthCalc, (1000f, 900f)));
        Assert.True(CssMediaQuery.AppliesForViewport(LeftWidthCalc, (1440f, 1000f)));

        const string LeftHeightCalc = "@media (calc(40rem + (2rem * 2)) < height)";
        Assert.False(CssMediaQuery.AppliesForViewport(LeftHeightCalc, (1280f, 704f)));
        Assert.True(CssMediaQuery.AppliesForViewport(LeftHeightCalc, (1280f, 705f)));
    }

    [Fact]
    public void MediaTypeSelectsScreenPrintNegationAndQueryLists()
    {
        var viewport = (800f, 600f);
        bool Applies(string query, CssMediaType media) =>
            CssMediaQuery.AppliesForViewportAndType(query, viewport, media);

        Assert.True(Applies("screen", CssMediaType.Screen));
        Assert.False(Applies("screen", CssMediaType.Print));
        Assert.True(Applies("print", CssMediaType.Print));
        Assert.False(Applies("print", CssMediaType.Screen));
        Assert.True(Applies("not print", CssMediaType.Screen));
        Assert.False(Applies("not print", CssMediaType.Print));
        Assert.True(Applies("not screen", CssMediaType.Print));
        Assert.False(Applies("not screen", CssMediaType.Screen));
        Assert.True(Applies("speech, print", CssMediaType.Print));
        Assert.False(Applies("speech, print", CssMediaType.Screen));
        Assert.True(Applies("print and (min-width: 700px)", CssMediaType.Print));
        Assert.False(Applies("print and (min-width: 900px)", CssMediaType.Print));
        Assert.True(Applies("not all and (min-width: 900px)", CssMediaType.Screen));
    }

    [Fact]
    public void NegatedMinWidthQueriesFormMaxBreakpoints()
    {
        Assert.True(CssMediaQuery.AppliesForViewport("@media not all and (min-width: 40rem)", (639f, 900f)));
        Assert.False(CssMediaQuery.AppliesForViewport("@media not all and (min-width: 40rem)", (1280f, 900f)));

        const string Css = """
            .hidden { display: none }
            @media not all and (min-width: 40rem) {
                .max-sm\:inline { display: inline }
            }
            @media (min-width: 80rem) {
                .xl\:inline { display: inline }
            }
            """;

        var desktop = CssParser.ParseStylesheetForViewport(Css, (1280f, 900f));
        Assert.DoesNotContain(desktop, rule => rule.Selector == @".max-sm\:inline");
        Assert.Contains(desktop, rule => rule.Selector == @".xl\:inline");
    }

    [Fact]
    public void MediaQueryListsAreOrConditions()
    {
        Assert.True(CssMediaQuery.AppliesForViewport("@media print, (min-width: 64rem)", (1280f, 720f)));
        Assert.False(CssMediaQuery.AppliesForViewport("@media print, (min-width: 64rem)", (900f, 1000f)));
    }

    [Fact]
    public void RemBreakpointDoesNotRevealDesktopMenuOnNarrowViewport()
    {
        const string Css = """
            header .menu-toolkit { display: none }
            @media (min-width: 64rem) {
                header .menu-toolkit { display: flex }
            }
            """;

        var narrow = CssParser.ParseStylesheetForViewport(Css, (900f, 1000f));
        Assert.Contains(narrow, rule =>
            rule.Selector == "header .menu-toolkit"
            && rule.Declarations.Contains("display: none", StringComparison.Ordinal));
        Assert.DoesNotContain(narrow, rule =>
            rule.Declarations.Contains("display: flex", StringComparison.Ordinal));

        var wide = CssParser.ParseStylesheetForViewport(Css, (1024f, 768f));
        Assert.Contains(wide, rule => rule.Declarations.Contains("display: flex", StringComparison.Ordinal));
    }

    // ------------------------------------------- tokenizer (no Rust counterpart)

    [Fact]
    public void TokenizerMatchesCssParserTokenShapes()
    {
        var tokens = CssTokenizer.Tokenize("""a.b#c[d="e"] { --x: 1.5e2px; y: url(z) }""")
            .Where(token => token.Kind is not (CssTokenKind.Whitespace or CssTokenKind.Comment))
            .ToList();

        Assert.Equal(CssTokenKind.Ident, tokens[0].Kind);
        Assert.Equal("a", tokens[0].Value);
        Assert.Equal(CssTokenKind.Delim, tokens[1].Kind);
        Assert.Equal('.', tokens[1].Delim);
        Assert.Equal(CssTokenKind.IdHash, tokens[3].Kind);
        Assert.Equal("c", tokens[3].Value);
        Assert.Equal(CssTokenKind.SquareBracketBlock, tokens[4].Kind);
        Assert.Equal(CssTokenKind.QuotedString, tokens[7].Kind);
        Assert.Equal("e", tokens[7].Value);

        var dimension = tokens.First(token => token.Kind == CssTokenKind.Dimension);
        Assert.Equal(150d, dimension.Number);
        Assert.Equal("px", dimension.Unit);
        Assert.False(dimension.IsInteger);

        var url = tokens.First(token => token.Kind == CssTokenKind.Url);
        Assert.Equal("z", url.Value);
    }

    [Fact]
    public void TokenizerDecodesEscapesAndRecoversFromBadInput()
    {
        Assert.Equal("sm:hover", CssTokenizer.SoleIdent(@"sm\:hover"));
        Assert.Equal("\u200b", CssTokenizer.SoleIdent(@"\200b "));
        Assert.Equal("--card", CssTokenizer.SoleIdent("--card"));
        Assert.Null(CssTokenizer.SoleIdent("a b"));
        Assert.Null(CssTokenizer.SoleIdent("future(foo)"));

        // Error recovery: an unterminated string yields a token, never an
        // exception, and a newline inside a string produces BadString.
        var bad = CssTokenizer.Tokenize("\"unterminated\nrest");
        Assert.Equal(CssTokenKind.BadString, bad[0].Kind);

        var range = CssTokenizer.Tokenize("U+4??");
        Assert.Equal(CssTokenKind.UnicodeRange, range[0].Kind);
        Assert.Equal(0x400u, range[0].RangeStart);
        Assert.Equal(0x4FFu, range[0].RangeEnd);
    }

    [Fact]
    public void ValueHelpersMatchTheRustParsers()
    {
        // Colors: hex, legacy and modern function syntax, named, and the
        // Tailwind v4 families the Rust parser accepts.
        Assert.Equal(new RgbaColor(0x12, 0x34, 0x56, 0xff), CssColor.Parse("#123456"));
        Assert.Equal(new RgbaColor(0xAA, 0xBB, 0xCC, 0xff), CssColor.Parse("#abc"));
        Assert.Equal(new RgbaColor(255, 0, 0, 128), CssColor.Parse("rgba(255, 0, 0, 0.5)"));
        Assert.Equal(new RgbaColor(0, 128, 0, 255), CssColor.Parse("green"));
        Assert.Equal(RgbaColor.Transparent, CssColor.Parse("transparent"));
        Assert.Null(CssColor.Parse("rgb(from red r g b)"));
        Assert.Null(CssColor.Parse("not-a-color"));

        // Typed calc: grouping and scalar arithmetic, invariant culture.
        Assert.Equal(
            1072f,
            CssLength.ResolveContextual(
                "calc(1rem * 2 + (15rem + 2rem) * 2 + 31rem)", 16f, 16f, 10f, 10f, 100f));
        Assert.Equal(50f, CssLength.ResolveContextual("50%", 16f, 16f, 10f, 10f, 100f));
        Assert.Null(CssLength.ResolveContextual("banana", 16f, 16f, 10f, 10f, 100f));

        // A hex escape decodes and eats exactly one trailing whitespace.
        Assert.Equal("\u200bx", CssValues.UnescapeString(@"\200b x"));
        Assert.Equal("\"", CssValues.UnescapeString(@"\"""));
    }

    [Fact]
    public void ParserNeverThrowsOnMalformedInput()
    {
        foreach (var css in (string[])
        [
            "}.after-stray{color:red}",
            ".unclosed{color:red",
            "/* unterminated comment .a{b:c}",
            "@media {",
            "@container (((((",
            ".a{b:\"unterminated}",
            "@layer;.b{c:d}",
        ])
        {
            var rules = CssParser.ParseStylesheet(css);
            Assert.NotNull(rules);
        }

        Assert.True(CssParser.ParseStylesheet("}.after-stray{color:red}")
            .Any(rule => rule.Selector == ".after-stray"));
    }
}
