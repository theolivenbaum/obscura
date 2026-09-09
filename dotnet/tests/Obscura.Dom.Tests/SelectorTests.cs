using Obscura.Dom;
using Obscura.Dom.Selectors;

namespace Obscura.Dom.Tests;

/// <summary>
/// Port of the <c>#[cfg(test)] mod tests</c> block in crates/obscura-dom/src/selector.rs.
/// </summary>
public class SelectorTests
{
    private static void AssertNodes(IReadOnlyList<NodeId> expected, IReadOnlyList<NodeId>? actual) =>
        Assert.Equal(expected, actual ?? []);

    private static void AssertKeys(IReadOnlyList<SelectorKey> expected, IReadOnlyList<SelectorKey> actual) =>
        Assert.Equal(expected, actual);

    private static NodeId ShadowElement(DomTree tree, string tag, params (string Name, string Value)[] attrs)
    {
        var list = new List<Attribute>();
        foreach (var (name, value) in attrs)
        {
            list.Add(new Attribute(QualName.Attr(name), value));
        }

        return tree.NewNode(NodeData.Element(QualName.Html(tag), list));
    }

    private static IReadOnlyList<SelectorKey> CandidateKeys(DomTree tree, string selector)
    {
        var compiled = tree.CompileRuleSelector(selector);
        Assert.True(compiled is not null, $"failed to compile {selector}");
        return compiled!.CandidateKeys;
    }

    private static bool ElementHasSelectorKey(DomTree tree, NodeId nodeId, SelectorKey key)
    {
        var node = tree.GetNode(nodeId);
        Assert.NotNull(node);
        switch (key.Kind)
        {
            case SelectorKeyKind.Root:
                return node.Parent is { } parent && (tree.GetNode(parent)?.IsDocument ?? false);
            case SelectorKeyKind.Universal:
                return true;
            case SelectorKeyKind.Id:
                return string.Equals(node.GetAttribute("id"), key.Value, StringComparison.Ordinal);
            case SelectorKeyKind.Class:
                if (node.GetAttribute("class") is not { } classes)
                {
                    return false;
                }

                foreach (var value in classes.Split(
                    [' ', '\t', '\n', '\r', '\f'],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.Equals(value, key.Value, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            case SelectorKeyKind.Attribute:
                return node.GetAttribute(key.Value) is not null;
            case SelectorKeyKind.Local:
                return node.ElementName is { } name
                    && string.Equals(name.Local, key.Value, StringComparison.Ordinal);
            default:
                return false;
        }
    }

    [Fact]
    public void SelectorCandidateKeysExpandRightmostIsAndWhere()
    {
        var tree = HtmlParsing.ParseHtml("<main></main>");

        AssertKeys(
            [SelectorKey.Class("IssueLabel"), SelectorKey.Class("Label")],
            CandidateKeys(tree, ":is(.IssueLabel, .Label):hover"));
        AssertKeys(
            [SelectorKey.Local("button"), SelectorKey.Local("input"), SelectorKey.Local("select")],
            CandidateKeys(tree, ":where(button, input, select)"));

        // A legacy single-bucket index stays correct until it explicitly opts into inserting all
        // candidate keys.
        var compiled = tree.CompileRuleSelector(":is(.IssueLabel, .Label):hover")!;
        Assert.Equal(SelectorKey.Universal, compiled.Key);
    }

    [Fact]
    public void SelectorCandidateKeysDeduplicateEquivalentArms()
    {
        var tree = HtmlParsing.ParseHtml("<main></main>");
        var compiled = tree.CompileRuleSelector(":is(.btn, .btn:hover, :where(.btn))")!;

        AssertKeys([SelectorKey.Class("btn")], compiled.CandidateKeys);
        Assert.Equal(SelectorKey.Class("btn"), compiled.Key);
    }

    [Fact]
    public void SelectorCandidateKeysIndexAttributesAndFallBackForPseudos()
    {
        var tree = HtmlParsing.ParseHtml("<main></main>");
        AssertKeys(
            [SelectorKey.Class("btn"), SelectorKey.Attribute("data-kind")],
            CandidateKeys(tree, ":is(.btn, [data-kind])"));
        AssertKeys(
            [SelectorKey.Universal],
            CandidateKeys(tree, ":is(.btn, :first-child)"));
    }

    [Fact]
    public void SelectorCandidateKeysIndexRootSubjectsAndFunctionalArms()
    {
        var tree = HtmlParsing.ParseHtml("<html><body><main class=app></main></body></html>");
        AssertKeys([SelectorKey.Root], CandidateKeys(tree, ":root"));
        AssertKeys([SelectorKey.Root], CandidateKeys(tree, ":root[data-color-mode]"));
        AssertKeys(
            [SelectorKey.Root, SelectorKey.Class("app")],
            CandidateKeys(tree, ":is(:root, .app)"));
    }

    [Fact]
    public void SelectorCandidateKeysOnlyReplaceOuterKeyWhenMoreSelective()
    {
        var tree = HtmlParsing.ParseHtml("<main></main>");
        AssertKeys(
            [SelectorKey.Id("save"), SelectorKey.Id("cancel")],
            CandidateKeys(tree, ".control:is(#save, #cancel)"));
        AssertKeys(
            [SelectorKey.Class("control")],
            CandidateKeys(tree, ".control:is(button, #save)"));
    }

    [Fact]
    public void SelectorCandidateKeysUseSubjectsOfComplexIsArms()
    {
        var tree = HtmlParsing.ParseHtml("<main></main>");
        AssertKeys(
            [SelectorKey.Class("x"), SelectorKey.Class("y")],
            CandidateKeys(tree, ".scope :is(.a > .x, .b > .y)"));
    }

    [Fact]
    public void SelectorCandidateKeysCoverEveryActualMatch()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <section class="scope">
                <div class="alpha control" id="save" data-kind="x"></div>
                <button class="beta control"></button>
                <input class="control" id="cancel">
                <div class="a"><span class="x"></span></div>
                <div class="b"><span class="y"></span></div>
            </section>
            """);
        string[] selectors =
        [
            ":is(.alpha, .beta)",
            ":where(button, input)",
            ".control:is(#save, #cancel)",
            ".control:is(button, #save)",
            ":is(.alpha, [data-kind])",
            ":is(:root, .control)",
            ".scope :is(.a > .x, .b > .y)",
        ];

        foreach (var selector in selectors)
        {
            var keys = CandidateKeys(tree, selector);
            var matches = tree.QuerySelectorAll(selector);
            Assert.True(matches.Count > 0, $"fixture must exercise {selector}");
            foreach (var matched in matches)
            {
                var covered = false;
                foreach (var key in keys)
                {
                    if (ElementHasSelectorKey(tree, matched, key))
                    {
                        covered = true;
                        break;
                    }
                }

                Assert.True(
                    covered,
                    $"{selector} matched an element outside all candidate buckets: "
                        + string.Join(", ", keys));
            }
        }
    }

    [Fact]
    public void MatcherCandidateCollectionDeduplicatesAndReusesIndices()
    {
        var tree = HtmlParsing.ParseHtml("<main></main>");
        var matcher = tree.CreateMatcher();

        matcher.BeginCandidateCollection(4);
        Assert.True(matcher.MarkCandidate(2));
        Assert.False(matcher.MarkCandidate(2));

        matcher.BeginCandidateCollection(4);
        Assert.True(matcher.MarkCandidate(2));
        Assert.False(matcher.MarkCandidate(2));

        matcher.BeginCandidateCollection(9);
        Assert.True(matcher.MarkCandidate(8));
        Assert.False(matcher.MarkCandidate(8));

        matcher.CandidateGeneration = uint.MaxValue;
        matcher.BeginCandidateCollection(9);
        Assert.Equal(1u, matcher.CandidateGeneration);
        Assert.True(matcher.MarkCandidate(2));
        Assert.False(matcher.MarkCandidate(2));
    }

    [Fact]
    public void TestQuerySelectorTag()
    {
        var tree = HtmlParsing.ParseHtml("<html><body><h1>Title</h1><p>Text</p></body></html>");
        var result = tree.QuerySelector("h1");
        Assert.NotNull(result);
        var node = tree.GetNode(result.Value)!;
        Assert.Equal("h1", node.ElementName!.Value.Local);
    }

    [Fact]
    public void TestQuerySelectorClass()
    {
        var tree = HtmlParsing.ParseHtml("""<div class="foo bar">Content</div><div class="baz">Other</div>""");
        var result = tree.QuerySelector(".foo");
        Assert.NotNull(result);
        var node = tree.GetNode(result.Value)!;
        Assert.Equal("foo bar", node.GetAttribute("class"));
    }

    [Fact]
    public void TestQuerySelectorId()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="main">Content</div>""");
        var result = tree.QuerySelector("#main");
        Assert.NotNull(result);
    }

    [Fact]
    public void TestQuerySelectorAll()
    {
        var tree = HtmlParsing.ParseHtml("<ul><li>1</li><li>2</li><li>3</li></ul>");
        var results = tree.QuerySelectorAll("li");
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void TestHasParses()
    {
        // Isolate parse from match: :has must parse, not error.
        Assert.True(SelectorParser.TryParse("a:has(p.bt)", out _, out _), ":has failed to parse");
    }

    [Fact]
    public void TestQuerySelectorHas()
    {
        var tree = HtmlParsing.ParseHtml("""<a><p class="bt">x</p></a>""");
        var all = tree.QuerySelectorAll("a:has(p.bt)");
        Assert.True(all.Count == 1, "a:has(p.bt) should match the <a>");
        var none = tree.QuerySelectorAll("a:has(span.bt)");
        Assert.True(none.Count == 0, "a:has(span.bt) should match nothing");
    }

    [Fact]
    public void TestEnabledMatchesFormControlWithoutDisabledAttr()
    {
        var tree = HtmlParsing.ParseHtml("<button>Click</button><button disabled>Nope</button>");
        var enabled = tree.QuerySelectorAll("button:enabled");
        Assert.True(enabled.Count == 1, ":enabled should match only the non-disabled button");
        var disabled = tree.QuerySelectorAll("button:disabled");
        Assert.True(disabled.Count == 1, ":disabled should match only the disabled button");
    }

    [Fact]
    public void TestEnabledDoesNotMatchNonFormElements()
    {
        // :enabled/:disabled only apply to form controls; a plain div should never match either,
        // disabled attribute or not.
        var tree = HtmlParsing.ParseHtml("<div disabled>x</div>");
        Assert.Empty(tree.QuerySelectorAll("div:enabled"));
        Assert.Empty(tree.QuerySelectorAll("div:disabled"));
    }

    [Fact]
    public void TestCheckedMatchesCheckedAttribute()
    {
        var tree = HtmlParsing.ParseHtml("""<input type="checkbox" checked><input type="checkbox">""");
        Assert.Single(tree.QuerySelectorAll("input:checked"));
    }

    [Fact]
    public void UnfocusedSnapshotMatchesFocusNegationSelectors()
    {
        var tree = HtmlParsing.ParseHtml("""<div class="visually-hidden-focusable">Skip</div>""");
        var hidden = tree.QuerySelectorAll(".visually-hidden-focusable:not(:focus):not(:focus-within)");
        Assert.Single(hidden);
        Assert.Empty(tree.QuerySelectorAll(":focus-visible"));
    }

    [Fact]
    public void TestQuerySelectorDescendant()
    {
        var tree = HtmlParsing.ParseHtml("""<div id="outer"><div id="inner"><span>Target</span></div></div>""");
        var result = tree.QuerySelector("#outer span");
        Assert.NotNull(result);
        var node = tree.GetNode(result.Value)!;
        Assert.Equal("span", node.ElementName!.Value.Local);
    }

    [Fact]
    public void CompiledNestedIsDescendantMatchesWithAncestorFilter()
    {
        var tree = HtmlParsing.ParseHtml(
            """<div class="**:[.line]:block"><code><span id="target" class="line">x</span></code></div>""");
        var target = tree.GetElementById("target")!.Value;
        var compiled = tree.CompileRuleSelector(@":is(.\*\*\:\[\.line\]\:block *).line");
        Assert.NotNull(compiled);
        var matcher = tree.CreateMatcher();
        var ancestors = tree.Ancestors(target);
        for (var i = ancestors.Count - 1; i >= 0; i--)
        {
            matcher.PushAncestor(tree, ancestors[i]);
        }

        Assert.True(matcher.Matches(tree, target, compiled));
    }

    [Fact]
    public void TestQuerySelectorAttribute()
    {
        var tree = HtmlParsing.ParseHtml(
            """<input type="text" name="user"><input type="password" name="pass">""");
        var result = tree.QuerySelector("""input[type="password"]""");
        Assert.NotNull(result);
        var node = tree.GetNode(result.Value)!;
        Assert.Equal("pass", node.GetAttribute("name"));
    }

    [Fact]
    public void TestQuerySelectorNoMatch()
    {
        var tree = HtmlParsing.ParseHtml("<div>Hello</div>");
        Assert.Null(tree.QuerySelector("span"));
    }

    [Fact]
    public void QuirksModeMatchesClassAndIdCaseInsensitively()
    {
        // No doctype => quirks mode; class/id match ASCII case-insensitively.
        var tree = HtmlParsing.ParseHtml("""<div class="Foo" id="Bar">x</div>""");
        Assert.True(tree.QuerySelector(".foo") is not null, ".foo should match class=\"Foo\" in quirks mode");
        Assert.True(tree.QuerySelector("#bar") is not null, "#bar should match id=\"Bar\" in quirks mode");
    }

    [Fact]
    public void StandardsModeMatchesClassAndIdCaseSensitively()
    {
        // With a doctype => no-quirks; class/id remain case-sensitive.
        var tree = HtmlParsing.ParseHtml("""<!DOCTYPE html><div class="Foo" id="Bar">x</div>""");
        Assert.True(tree.QuerySelector(".foo") is null, ".foo must NOT match class=\"Foo\" in standards mode");
        Assert.True(tree.QuerySelector("#bar") is null, "#bar must NOT match id=\"Bar\" in standards mode");
    }

    [Fact]
    public void TestQuerySelectorComplex()
    {
        var tree = HtmlParsing.ParseHtml(
            """
            <div class="container">
                <ul class="list">
                    <li class="item active">First</li>
                    <li class="item">Second</li>
                    <li class="item active">Third</li>
                </ul>
            </div>
            """);
        var results = tree.QuerySelectorAll(".list .item.active");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void TestQuerySelectorAllFromScopesToSubtree()
    {
        var tree = HtmlParsing.ParseHtml(
            """<div id="a"><span class="x">in a</span></div><div id="b"><span class="x">in b</span></div>""");
        var a = tree.GetElementById("a")!.Value;
        var b = tree.GetElementById("b")!.Value;

        // Document-rooted: sees both spans.
        Assert.Equal(2, tree.QuerySelectorAll(".x").Count);
        // Scoped to #a: only its descendant span.
        var inA = tree.QuerySelectorAllFrom(a, ".x");
        Assert.Single(inA);
        var inB = tree.QuerySelectorAllFrom(b, ".x");
        Assert.Single(inB);
        Assert.NotEqual(inA[0], inB[0]);
    }

    [Fact]
    public void TestQuerySelectorFromReturnsFirstInSubtreeOnly()
    {
        var tree = HtmlParsing.ParseHtml("""<section id="s"><p>first</p><p>second</p></section><p>outside</p>""");
        var s = tree.GetElementById("s")!.Value;

        // Scoped to #s: skip the outside paragraph; return the first inside.
        var firstInS = tree.QuerySelectorFrom(s, "p");
        Assert.NotNull(firstInS);
        Assert.Equal("first", tree.TextContent(firstInS.Value));
    }

    [Fact]
    public void TestQuerySelectorFromExcludesSelf()
    {
        // The root element itself must not match its own scoped query, per the spec: querySelector
        // matches descendants only.
        var tree = HtmlParsing.ParseHtml("""<div id="root" class="x"><span>child</span></div>""");
        var root = tree.GetElementById("root")!.Value;

        // Only descendants are candidates: `.x` on root finds nothing.
        Assert.Null(tree.QuerySelectorFrom(root, ".x"));
        // `span` finds the child.
        Assert.NotNull(tree.QuerySelectorFrom(root, "span"));
    }

    [Fact]
    public void SelectorsRespectNativeShadowTreeScopesAndHostHooks()
    {
        var tree = HtmlParsing.ParseHtml(
            """<!doctype html><x-card id="host"><span id="light-only" class="target">light</span></x-card>""");
        var host = tree.GetElementById("host")!.Value;
        var light = tree.GetElementById("light-only")!.Value;
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var shadow = ShadowElement(tree, "span", ("id", "shadow-only"), ("class", "target"));
        tree.AppendChild(root, shadow);

        // Document- and host-rooted selectors walk only the light tree. The id assertion exercises
        // the O(1) id-index fast path as well as its scoped fallback; neither path may expose a
        // shadow descendant.
        AssertNodes([light], tree.QuerySelectorAll(".target"));
        Assert.Null(tree.QuerySelector("#shadow-only"));
        AssertNodes([light], tree.QuerySelectorAllFrom(host, ".target"));

        // A query rooted at the ShadowRoot remains useful to the shadow DOM API and sees only that
        // local tree scope.
        AssertNodes([shadow], tree.QuerySelectorAllFrom(root, ".target"));
        Assert.Equal(shadow, tree.QuerySelectorFrom(root, "#shadow-only"));

        var matchedShadow = new DomElement(tree, shadow);
        Assert.True(matchedShadow.ParentNodeIsShadowRoot());
        Assert.Equal(host, matchedShadow.ContainingShadowHost()?.NodeId);
        Assert.False(matchedShadow.IsRoot());
        Assert.False(tree.MatchesSelector(shadow, ":root"));

        // Each nested shadow scope reports its nearest host. Walking the same hook again from that
        // inner host reaches the outer host.
        var innerHost = ShadowElement(tree, "x-inner");
        tree.AppendChild(root, innerHost);
        var innerRoot = tree.AttachShadowRoot(innerHost, ShadowRootMode.Closed);
        var innerChild = ShadowElement(tree, "b");
        tree.AppendChild(innerRoot, innerChild);
        Assert.Equal(innerHost, new DomElement(tree, innerChild).ContainingShadowHost()?.NodeId);
        Assert.Equal(host, new DomElement(tree, innerHost).ContainingShadowHost()?.NodeId);
    }

    [Fact]
    public void HostSelectorsRequireAndRespectExplicitShadowScope()
    {
        var tree = HtmlParsing.ParseHtml(
            """<!doctype html><x-card id="host" class="active"></x-card><x-card id="other"></x-card>""");
        var host = tree.GetElementById("host")!.Value;
        var other = tree.GetElementById("other")!.Value;
        tree.AttachShadowRoot(host, ShadowRootMode.Open);

        var plain = tree.CompileRuleSelector(":host")!;
        var qualified = tree.CompileRuleSelector(":host(.active)")!;
        var rejected = tree.CompileRuleSelector(":host([hidden])")!;
        Assert.True(plain.MatchesFeaturelessHost());
        Assert.True(qualified.MatchesFeaturelessHost());

        var matcher = tree.CreateMatcher();
        Assert.True(matcher.MatchesShadowHost(tree, host, plain, host));
        Assert.True(matcher.MatchesShadowHost(tree, host, qualified, host));
        Assert.False(matcher.MatchesShadowHost(tree, host, rejected, host));
        Assert.False(matcher.MatchesShadowHost(tree, other, plain, host));

        // Parsing support must not make `:host` observable in document scope.
        Assert.False(tree.MatchesSelector(host, ":host"));
        Assert.Empty(tree.QuerySelectorAll(":host"));
    }

    [Fact]
    public void SlottedSelectorsMatchOnlyAssignedElementsInTheirShadowScope()
    {
        var tree = HtmlParsing.ParseHtml(
            """<!doctype html><x-card id="host"><span id="item" class="item" slot="title"></span><span id="unslotted" class="item" slot="missing"></span></x-card>""");
        var host = tree.GetElementById("host")!.Value;
        var item = tree.GetElementById("item")!.Value;
        var unslotted = tree.GetElementById("unslotted")!.Value;
        var root = tree.AttachShadowRoot(host, ShadowRootMode.Open);
        var slot = ShadowElement(tree, "slot", ("name", "title"), ("class", "outlet"));
        var wrapper = ShadowElement(tree, "div", ("class", "wrapper"));
        tree.AppendChild(root, wrapper);
        tree.AppendChild(wrapper, slot);

        var plain = tree.CompileRuleSelector("::slotted(.item)")!;
        var qualified = tree.CompileRuleSelector("slot.outlet::slotted(.item)")!;
        var descendant = tree.CompileRuleSelector(".wrapper slot.outlet::slotted(.item)")!;
        var rejected = tree.CompileRuleSelector("::slotted(.other)")!;
        Assert.True(plain.IsSlotted());

        var matcher = tree.CreateMatcher();
        Assert.True(matcher.MatchesInShadowScope(tree, item, plain, host));
        Assert.True(matcher.MatchesInShadowScope(tree, item, qualified, host));
        Assert.True(matcher.MatchesInShadowScope(tree, item, descendant, host));
        Assert.False(matcher.MatchesInShadowScope(tree, item, rejected, host));
        Assert.False(matcher.MatchesInShadowScope(tree, unslotted, plain, host));
        Assert.False(matcher.MatchesInShadowScope(tree, item, plain, item));

        // Document matching parses the selector but has no shadow scope.
        Assert.False(tree.MatchesSelector(item, "::slotted(.item)"));
    }
}
