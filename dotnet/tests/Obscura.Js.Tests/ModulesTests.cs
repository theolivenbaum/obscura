using System.Diagnostics;
using System.Net;

using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

using Obscura.Dom;
using Obscura.Js.Modules;
using Obscura.Js.Url;

using Xunit;

namespace Obscura.Js.Tests;

/// <summary>
/// Ports of the six <c>#[test]</c>s in <c>crates/obscura-js/src/import_map.rs</c>,
/// in source order, followed by new coverage for the three support modules that
/// shipped without in-file tests: specifier resolution in
/// <c>module_loader.rs</c>, the <c>document.write</c> stream, and the CDP
/// watchdog.
/// </summary>
public sealed class ImportMapTests
{
    // ---------------------------------------------------------------- ported

    /// <summary>Port of <c>exact_prefix_and_url_like_keys_are_normalized_against_the_map_base</c>.</summary>
    [Fact]
    public void ExactPrefixAndUrlLikeKeysAreNormalizedAgainstTheMapBase()
    {
        var map = Parse(
            """
            {
                "imports": {
                    "pkg": "../vendor/pkg.js",
                    "pkg/": "../vendor/pkg/",
                    "./local.js": "../vendor/local.js"
                }
            }
            """,
            "https://example.test/app/maps/import-map.json");
        var referrer = Url("https://example.test/app/main.js");

        Assert.Equal(
            "https://example.test/app/vendor/pkg.js",
            Resolve(map, "pkg", referrer));
        Assert.Equal(
            "https://example.test/app/vendor/pkg/features/a.js",
            Resolve(map, "pkg/features/a.js", referrer));
        Assert.Equal(
            "https://example.test/app/vendor/local.js",
            Resolve(map, "./maps/dir/../local.js", referrer));
    }

    /// <summary>Port of <c>most_specific_scope_wins_then_falls_back_to_top_level_imports</c>.</summary>
    [Fact]
    public void MostSpecificScopeWinsThenFallsBackToTopLevelImports()
    {
        var map = Parse(
            """
            {
                "imports": {
                    "shared": "/default.js",
                    "fallback": "/fallback.js"
                },
                "scopes": {
                    "/feature/": { "shared": "/feature.js" },
                    "/feature/nested/": { "shared": "/nested.js" }
                }
            }
            """,
            "https://example.test/app/index.html");

        var nested = Url("https://example.test/feature/nested/main.js");
        Assert.Equal("https://example.test/nested.js", Resolve(map, "shared", nested));
        Assert.Equal("https://example.test/fallback.js", Resolve(map, "fallback", nested));
    }

    /// <summary>Port of <c>later_maps_add_unrelated_rules_but_cannot_change_resolved_rules</c>.</summary>
    [Fact]
    public void LaterMapsAddUnrelatedRulesButCannotChangeResolvedRules()
    {
        var map = Parse(
            """{"imports":{"fixed":"/first.js"}}""",
            "https://example.test/app/index.html");
        var referrer = Url("https://example.test/app/main.js");

        Assert.Equal("https://example.test/first.js", Resolve(map, "fixed", referrer));

        map.Merge(Parse(
            """{"imports":{"fixed":"/second.js","later":"/later.js"}}""",
            "https://example.test/app/index.html"));

        Assert.Equal("https://example.test/first.js", Resolve(map, "fixed", referrer));
        Assert.Equal("https://example.test/later.js", Resolve(map, "later", referrer));
    }

    /// <summary>Port of <c>later_prefix_rules_cannot_capture_an_already_resolved_specifier</c>.</summary>
    [Fact]
    public void LaterPrefixRulesCannotCaptureAnAlreadyResolvedSpecifier()
    {
        var map = new ImportMap();
        var referrer = Url("https://example.test/app/main.js");
        Assert.Equal(
            "https://example.test/app/pkg/item.js",
            Resolve(map, "./pkg/item.js", referrer));

        map.Merge(Parse(
            """{"imports":{"./pkg/":"/replacement/","new/":"/new/"}}""",
            "https://example.test/app/main.js"));

        Assert.Equal(
            "https://example.test/app/pkg/item.js",
            Resolve(map, "./pkg/item.js", referrer));
        Assert.Equal(
            "https://example.test/new/item.js",
            Resolve(map, "new/item.js", referrer));
    }

    /// <summary>Port of <c>bare_relative_scope_prefix_is_resolved_as_a_url</c>.</summary>
    [Fact]
    public void BareRelativeScopePrefixIsResolvedAsAUrl()
    {
        var map = Parse(
            """{"scopes":{"feature/":{"pkg":"/scoped.js"}}}""",
            "https://example.test/app/index.html");
        var referrer = Url("https://example.test/app/feature/main.js");
        Assert.Equal("https://example.test/scoped.js", Resolve(map, "pkg", referrer));
    }

    /// <summary>Port of <c>malformed_scope_or_integrity_member_invalidates_the_complete_map</c>.</summary>
    [Fact]
    public void MalformedScopeOrIntegrityMemberInvalidatesTheCompleteMap()
    {
        Assert.False(ImportMap.TryParse(
            """{"imports":{"pkg":"/pkg.js"},"scopes":{"/app/":[]}}""",
            "https://example.test/index.html",
            out _,
            out _));
        Assert.False(ImportMap.TryParse(
            """{"imports":{"pkg":"/pkg.js"},"integrity":[]}""",
            "https://example.test/index.html",
            out _,
            out _));
    }

    // ------------------------------------------------------------------- new

    [Fact]
    public void LongestMatchingPrefixWins()
    {
        var map = Parse(
            """{"imports":{"pkg/":"/shallow/","pkg/deep/":"/deep/"}}""",
            "https://example.test/app/index.html");
        var referrer = Url("https://example.test/app/main.js");

        Assert.Equal("https://example.test/deep/a.js", Resolve(map, "pkg/deep/a.js", referrer));
        Assert.Equal("https://example.test/shallow/a.js", Resolve(map, "pkg/a.js", referrer));
    }

    [Fact]
    public void AnExactKeyBeatsAPrefixKeyThatAlsoMatches()
    {
        var map = Parse(
            """{"imports":{"pkg/a.js":"/exact.js","pkg/":"/dir/"}}""",
            "https://example.test/app/index.html");
        var referrer = Url("https://example.test/app/main.js");

        Assert.Equal("https://example.test/exact.js", Resolve(map, "pkg/a.js", referrer));
        Assert.Equal("https://example.test/dir/b.js", Resolve(map, "pkg/b.js", referrer));
    }

    [Fact]
    public void ANullAddressBlocksTheSpecifierInsteadOfFallingThrough()
    {
        var map = Parse("""{"imports":{"blocked":null}}""", "https://example.test/index.html");
        Assert.False(map.TryResolve("blocked", Url("https://example.test/a.js"), out _, out var error));
        Assert.Equal(
            "Module specifier \"blocked\" is blocked by import map entry \"blocked\"",
            error);
    }

    [Fact]
    public void ATrailingSlashKeyWhoseAddressLacksOneIsBlocked()
    {
        // Parsing keeps the key but drops the address, which turns every specifier
        // under the prefix into a block rather than a silent fall-through.
        var map = Parse("""{"imports":{"pkg/":"/vendor"}}""", "https://example.test/index.html");
        Assert.False(map.TryResolve("pkg/a.js", Url("https://example.test/a.js"), out _, out var error));
        Assert.Equal(
            "Module specifier \"pkg/a.js\" is blocked by import map prefix \"pkg/\"",
            error);
    }

    [Fact]
    public void APrefixRuleCannotBacktrackAboveItsOwnAddress()
    {
        var map = Parse(
            """{"imports":{"pkg/":"/vendor/pkg/"}}""",
            "https://example.test/index.html");
        Assert.False(map.TryResolve(
            "pkg/../../escape.js",
            Url("https://example.test/a.js"),
            out _,
            out var error));
        Assert.Equal(
            "Module specifier \"pkg/../../escape.js\" backtracks above import map prefix \"pkg/\"",
            error);
    }

    [Fact]
    public void ABareSpecifierWithNoMappingIsAnError()
    {
        var map = new ImportMap();
        Assert.False(map.TryResolve("lodash", Url("https://example.test/a.js"), out _, out var error));
        Assert.Equal(
            "Bare module specifier \"lodash\" was not remapped by the import map",
            error);
    }

    [Fact]
    public void RelativeAndAbsoluteSpecifiersResolveWithoutAnyMapping()
    {
        var map = new ImportMap();
        var referrer = Url("https://example.test/app/main.js");

        Assert.Equal("https://example.test/app/sib.js", Resolve(map, "./sib.js", referrer));
        Assert.Equal("https://example.test/root.js", Resolve(map, "/root.js", referrer));
        Assert.Equal("https://example.test/up.js", Resolve(map, "../up.js", referrer));
        Assert.Equal("https://other.test/x.js", Resolve(map, "https://other.test/x.js", referrer));
    }

    [Fact]
    public void ANonSpecialUrlSpecifierSkipsPrefixMatching()
    {
        // A data: URL is already a complete module identity. Only special schemes
        // participate in prefix remapping, so the prefix below must not capture it.
        var map = Parse(
            """{"imports":{"data:text/":"/never/"}}""",
            "https://example.test/index.html");
        Assert.Equal(
            "data:text/javascript,1",
            Resolve(map, "data:text/javascript,1", Url("https://example.test/a.js")));
    }

    [Fact]
    public void AScopeOnlyAppliesToReferrersUnderIt()
    {
        var map = Parse(
            """{"imports":{"pkg":"/top.js"},"scopes":{"/feature/":{"pkg":"/scoped.js"}}}""",
            "https://example.test/index.html");

        Assert.Equal(
            "https://example.test/scoped.js",
            Resolve(map, "pkg", Url("https://example.test/feature/main.js")));
        Assert.Equal(
            "https://example.test/top.js",
            Resolve(map, "pkg", Url("https://example.test/other/main.js")));
    }

    [Fact]
    public void AScopePrefixWithoutATrailingSlashMatchesOnlyThatExactReferrer()
    {
        var map = Parse(
            """{"imports":{"pkg":"/top.js"},"scopes":{"/feature":{"pkg":"/scoped.js"}}}""",
            "https://example.test/index.html");

        Assert.Equal(
            "https://example.test/scoped.js",
            Resolve(map, "pkg", Url("https://example.test/feature")));
        Assert.Equal(
            "https://example.test/top.js",
            Resolve(map, "pkg", Url("https://example.test/feature/child.js")));
    }

    [Fact]
    public void MergingAddsANewScopeAndKeepsScopeOrderMostSpecificFirst()
    {
        var map = Parse(
            """{"scopes":{"/feature/":{"pkg":"/outer.js"}}}""",
            "https://example.test/index.html");
        map.Merge(Parse(
            """{"scopes":{"/feature/nested/":{"pkg":"/inner.js"}}}""",
            "https://example.test/index.html"));

        Assert.Equal(
            "https://example.test/inner.js",
            Resolve(map, "pkg", Url("https://example.test/feature/nested/main.js")));
        Assert.Equal(
            "https://example.test/outer.js",
            Resolve(map, "pkg", Url("https://example.test/feature/main.js")));
    }

    [Fact]
    public void MergingIntoAnExistingScopeKeepsTheEarlierRule()
    {
        var map = Parse(
            """{"scopes":{"/feature/":{"pkg":"/first.js"}}}""",
            "https://example.test/index.html");
        map.Merge(Parse(
            """{"scopes":{"/feature/":{"pkg":"/second.js","other":"/other.js"}}}""",
            "https://example.test/index.html"));

        var referrer = Url("https://example.test/feature/main.js");
        Assert.Equal("https://example.test/first.js", Resolve(map, "pkg", referrer));
        Assert.Equal("https://example.test/other.js", Resolve(map, "other", referrer));
    }

    [Fact]
    public void AnEmptyKeyIsIgnored()
    {
        var map = Parse(
            """{"imports":{"":"/ignored.js","pkg":"/pkg.js"}}""",
            "https://example.test/index.html");
        Assert.Equal("https://example.test/pkg.js", Resolve(map, "pkg", Url("https://example.test/a.js")));
        Assert.False(map.TryResolve("", Url("https://example.test/a.js"), out _, out _));
    }

    [Fact]
    public void AMalformedMapIsRejectedWithAMessage()
    {
        Assert.False(ImportMap.TryParse("[]", "https://example.test/", out _, out var topLevel));
        Assert.Equal("Import map top level must be an object", topLevel);

        Assert.False(ImportMap.TryParse("""{"imports":[]}""", "https://example.test/", out _, out var imports));
        Assert.Equal("Import map \"imports\" must be an object", imports);

        Assert.False(ImportMap.TryParse("""{"scopes":[]}""", "https://example.test/", out _, out var scopes));
        Assert.Equal("Import map \"scopes\" must be an object", scopes);

        Assert.False(ImportMap.TryParse("{ not json", "https://example.test/", out _, out var json));
        Assert.StartsWith("Invalid import map JSON:", json, StringComparison.Ordinal);

        Assert.False(ImportMap.TryParse("{}", "not-a-url", out _, out var baseUrl));
        Assert.StartsWith("Invalid import map base URL not-a-url:", baseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableAddressLeavesTheKeyBlocking()
    {
        // "pkg" is bare, so it is not a URL and not path-relative; the address
        // cannot be resolved and the entry becomes a block.
        var map = Parse("""{"imports":{"a":"also-bare"}}""", "https://example.test/index.html");
        Assert.False(map.TryResolve("a", Url("https://example.test/x.js"), out _, out var error));
        Assert.Equal("Module specifier \"a\" is blocked by import map entry \"a\"", error);
    }

    // --------------------------------------------------------------- helpers

    private static ImportMap Parse(string json, string baseUrl)
    {
        Assert.True(ImportMap.TryParse(json, baseUrl, out var map, out var error), error);
        return map;
    }

    private static UrlRecord Url(string href) =>
        UrlRecord.Parse(href) ?? throw new InvalidOperationException("bad test URL " + href);

    private static string Resolve(ImportMap map, string specifier, UrlRecord referrer)
    {
        Assert.True(map.TryResolve(specifier, referrer, out var resolved, out var error), error);
        return resolved!.Href;
    }
}

/// <summary>
/// New coverage for <c>module_loader.rs</c>'s resolution half, which shipped
/// without in-file tests. The fetch half needs a network and is exercised by the
/// runtime's integration tests.
/// </summary>
public sealed class ModuleLoaderTests
{
    private const string Base = "https://example.test/app/index.html";

    [Fact]
    public void TheGraphRootBypassesTheImportMap()
    {
        // A browser resolves <script type=module src> as a resource URL before it
        // starts a graph, so the document import map must not remap that root.
        var loader = LoaderWith("""{"imports":{"./main.js":"/remapped.js"}}""");
        Assert.Equal("https://example.test/app/main.js", Resolve(loader, "./main.js", "."));

        // The same specifier from a real referrer does go through the map.
        Assert.Equal(
            "https://example.test/remapped.js",
            Resolve(loader, "./main.js", "https://example.test/app/index.html"));
    }

    [Fact]
    public void ABareSpecifierAtTheGraphRootIsRejected()
    {
        using var loader = new ObscuraModuleLoader(Base);
        Assert.False(loader.TryResolve("lodash", ".", out _, out var error));
        Assert.Equal(
            "Relative import path \"lodash\" not prefixed with / or ./ or ../",
            error);
    }

    [Fact]
    public void AnAbsoluteSpecifierAtTheGraphRootIsKeptVerbatim()
    {
        using var loader = new ObscuraModuleLoader(Base);
        Assert.Equal("https://cdn.test/a.js", Resolve(loader, "https://cdn.test/a.js", "."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<anonymous>")]
    [InlineData("about:blank")]
    public void SyntheticReferrersFallBackToTheDocumentBaseUrl(string referrer)
    {
        using var loader = new ObscuraModuleLoader(Base);
        Assert.Equal("https://example.test/app/dep.js", Resolve(loader, "./dep.js", referrer));
    }

    [Fact]
    public void ADependencyResolvesAgainstItsImporterNotTheDocument()
    {
        using var loader = new ObscuraModuleLoader(Base);
        Assert.Equal(
            "https://cdn.test/pkg/dep.js",
            Resolve(loader, "./dep.js", "https://cdn.test/pkg/main.js"));
    }

    [Fact]
    public void AnUnparseableReferrerIsReportedRatherThanThrown()
    {
        using var loader = new ObscuraModuleLoader(Base);
        Assert.False(loader.TryResolve("./a.js", "not-a-url", out var resolved, out var error));
        Assert.Null(resolved);
        Assert.Equal("Invalid module referrer not-a-url: relative URL without a base", error);
    }

    [Fact]
    public void ResolutionGoesThroughTheSharedImportMap()
    {
        var map = new ImportMap();
        using var loader = new ObscuraModuleLoader(
            Base,
            null,
            map,
            () => ModuleNetworkContext.Failed("no network in this test"));

        Assert.False(loader.TryResolve("pkg", Base, out _, out _));

        // The runtime merges a later <script type="importmap"> into the same map
        // instance the loader holds, so the new rule is live immediately.
        Assert.True(ImportMap.TryParse(
            """{"imports":{"pkg":"/pkg.js"}}""",
            Base,
            out var later,
            out _));
        map.Merge(later);

        Assert.Equal("https://example.test/pkg.js", Resolve(loader, "pkg", Base));
    }

    [Fact]
    public void TheLoaderStartsWithNoLoadedSpecifiersAndNoActivity()
    {
        using var loader = new ObscuraModuleLoader(Base, "http://proxy.test:8080");
        Assert.Equal("http://proxy.test:8080", loader.ProxyUrl);
        Assert.Equal(Base, loader.BaseUrl);
        Assert.Empty(loader.LoadedSpecifiers);
        Assert.False(loader.Activity.IsPendingOrRecent(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void StaticGraphScopesNestAndUnwind()
    {
        using var loader = new ObscuraModuleLoader(Base);
        Assert.False(loader.InStaticGraph);
        var outer = loader.BeginStaticGraph();
        Assert.True(loader.InStaticGraph);
        var inner = loader.BeginStaticGraph();
        inner.Dispose();
        Assert.True(loader.InStaticGraph);
        outer.Dispose();
        Assert.False(loader.InStaticGraph);
        // Disposing twice must not underflow the depth.
        outer.Dispose();
        Assert.False(loader.InStaticGraph);
    }

    private static ObscuraModuleLoader LoaderWith(string importMapJson)
    {
        Assert.True(ImportMap.TryParse(importMapJson, Base, out var map, out var error), error);
        return new ObscuraModuleLoader(
            Base,
            null,
            map,
            () => ModuleNetworkContext.Failed("no network in this test"));
    }

    private static string Resolve(ObscuraModuleLoader loader, string specifier, string referrer)
    {
        Assert.True(loader.TryResolve(specifier, referrer, out var resolved, out var error), error);
        return resolved!.Href;
    }
}

/// <summary>
/// New coverage for the dynamic-import activity signal, which is what keeps a
/// module graph's fetches distinguishable from page fetch/XHR traffic.
/// </summary>
public sealed class ModuleLoadActivityTests
{
    [Fact]
    public void AFreshCounterIsNeitherPendingNorRecent()
    {
        var activity = new ModuleLoadActivity();
        Assert.Equal(0, activity.Pending);
        Assert.False(activity.IsPendingOrRecent(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ARegisteredLoadIsPendingRegardlessOfGrace()
    {
        var activity = new ModuleLoadActivity();
        using (activity.Begin())
        {
            Assert.Equal(1, activity.Pending);
            Assert.True(activity.IsPendingOrRecent(TimeSpan.Zero));
        }

        Assert.Equal(0, activity.Pending);
    }

    [Fact]
    public void ConcurrentLoadsAreCountedIndependently()
    {
        var activity = new ModuleLoadActivity();
        var first = activity.Begin();
        var second = activity.Begin();
        Assert.Equal(2, activity.Pending);
        first.Dispose();
        Assert.Equal(1, activity.Pending);
        Assert.True(activity.IsPendingOrRecent(TimeSpan.Zero));
        second.Dispose();
        Assert.Equal(0, activity.Pending);
    }

    [Fact]
    public void AFinishedLoadStaysRecentForTheGraceWindowOnly()
    {
        var activity = new ModuleLoadActivity();
        activity.Begin().Dispose();
        Thread.Sleep(40);

        Assert.True(activity.IsPendingOrRecent(TimeSpan.FromSeconds(30)));
        Assert.False(activity.IsPendingOrRecent(TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void DisposingAGuardTwiceDoesNotUnderflowTheCounter()
    {
        var activity = new ModuleLoadActivity();
        var guard = activity.Begin();
        guard.Dispose();
        guard.Dispose();
        Assert.Equal(0, activity.Pending);
    }
}

/// <summary>
/// New coverage for <c>write_stream.rs</c>, which shipped without in-file tests.
/// </summary>
public sealed class DocumentWriteStreamTests
{
    [Fact]
    public void AnOpenElementIsHandedOverImmediatelyWithItsText()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        var placements = stream.Write("<p>hello", dom);

        Assert.Equal(2, placements.Count);
        // Parents before children, and a null parent means the insertion point.
        Assert.Null(placements[0].Parent);
        Assert.Equal("p", dom.GetNode(placements[0].Node)!.ElementName!.Value.Local);
        Assert.Equal(placements[0].Node, placements[1].Parent);
        Assert.Equal("hello", Text(dom, placements[1].Node));
    }

    [Fact]
    public void TrailingTextGrowsInPlaceInsteadOfBeingHandedOverAgain()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        var first = stream.Write("<p>he", dom);
        var textNode = first[1].Node;

        var second = stream.Write("llo", dom);

        Assert.Empty(second);
        Assert.Equal("hello", Text(dom, textNode));
    }

    [Fact]
    public void AConstructSplitMidTagIsHandedOverOnlyOnceItParses()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        Assert.Empty(stream.Write("<di", dom));

        var placements = stream.Write("v>x", dom);
        Assert.Equal(2, placements.Count);
        Assert.Equal("div", dom.GetNode(placements[0].Node)!.ElementName!.Value.Local);
        Assert.Equal("x", Text(dom, placements[1].Node));
    }

    [Fact]
    public void AnIncompleteScriptIsHeldBackUntilItsEndTagArrives()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        // A <script> runs as soon as it is inserted, so a half-written one must not
        // be handed over.
        Assert.Empty(stream.Write("<script>var a = 1;", dom));

        var placements = stream.Write("</script>", dom);
        var script = Assert.Single(placements);
        Assert.Null(script.Parent);
        Assert.Equal("script", dom.GetNode(script.Node)!.ElementName!.Value.Local);
        Assert.Equal("var a = 1;", dom.TextContent(script.Node));
    }

    [Fact]
    public void AnEndTagInsideAScriptDoesNotEndItEarly()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        // Raw text: only the literal end tag closes the element, so the stream is
        // still inside the script here.
        Assert.Empty(stream.Write("<script>var a = '</p>';", dom));
        Assert.Single(stream.Write("</script>", dom));
    }

    [Fact]
    public void ACompleteScriptIsHandedOverWithItsWholeSubtree()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        var placements = stream.Write("<script>ok()</script>", dom);
        var script = Assert.Single(placements);
        Assert.Equal("ok()", dom.TextContent(script.Node));
        // The copy is detached: insertion is the JS side's job, because that is
        // where the mutation is reported and the script is prepared.
        Assert.Null(dom.GetNode(script.Node)!.Parent);
    }

    [Fact]
    public void AScriptIsNotHandedOverTwice()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        Assert.Single(stream.Write("<script>ok()</script>", dom));

        var second = stream.Write("<p>after", dom);
        Assert.Equal(2, second.Count);
        Assert.Equal("p", dom.GetNode(second[0].Node)!.ElementName!.Value.Local);
    }

    [Fact]
    public void ACompleteTemplateIsCopiedWithItsContentsDocument()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        // A <template> keeps its children in its own contents document, which a
        // child walk does not reach, so it can only be copied as a whole.
        var placements = stream.Write("<template><p>x</p></template>", dom);
        var template = Assert.Single(placements);
        Assert.Equal("template", dom.GetNode(template.Node)!.ElementName!.Value.Local);
        Assert.Empty(dom.Children(template.Node));

        var contents = dom.TemplateContents(template.Node);
        Assert.NotNull(contents);
        var child = Assert.Single(dom.Children(contents!.Value));
        Assert.Equal("p", dom.GetNode(child)!.ElementName!.Value.Local);
    }

    [Fact]
    public void AnUnclosedTemplateIsHeldBack()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        Assert.Empty(stream.Write("<template><p>x</p>", dom));
        Assert.Single(stream.Write("</template>", dom));
    }

    [Fact]
    public void NestedElementsAreReportedParentsBeforeChildren()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        var placements = stream.Write("<div><span>a</span><b>c", dom);

        var seen = new HashSet<NodeId>();
        foreach (var placement in placements)
        {
            if (placement.Parent is { } parent)
            {
                Assert.Contains(parent, seen);
            }

            seen.Add(placement.Node);
        }

        Assert.Equal(5, placements.Count);
        Assert.Null(placements[0].Parent);
        Assert.Equal("div", dom.GetNode(placements[0].Node)!.ElementName!.Value.Local);
    }

    [Fact]
    public void SiblingsAtTheHeadOfTheStreamAllReportTheInsertionPoint()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        var placements = stream.Write("<i>a</i><u>b", dom);

        Assert.Equal(4, placements.Count);
        Assert.Null(placements[0].Parent);
        Assert.Equal("i", dom.GetNode(placements[0].Node)!.ElementName!.Value.Local);
        Assert.Null(placements[2].Parent);
        Assert.Equal("u", dom.GetNode(placements[2].Node)!.ElementName!.Value.Local);
    }

    [Fact]
    public void AnElementStillOpenKeepsReceivingChildrenAcrossCalls()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        var first = stream.Write("<ul><li>one", dom);
        var list = first[0].Node;

        var second = stream.Write("<li>two", dom);

        // The second <li> is a sibling of the first inside the still-open <ul>.
        Assert.Equal(2, second.Count);
        Assert.Equal(list, second[0].Parent);
        Assert.Equal("li", dom.GetNode(second[0].Node)!.ElementName!.Value.Local);
        Assert.Equal("two", Text(dom, second[1].Node));
    }

    [Fact]
    public void AttributesSurviveTheHandOver()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();

        var placements = stream.Write("""<a href="/x" data-k="v">link""", dom);
        var anchor = dom.GetNode(placements[0].Node)!;
        Assert.Equal("/x", anchor.GetAttribute("href"));
        Assert.Equal("v", anchor.GetAttribute("data-k"));
    }

    [Fact]
    public void AFreshStreamRestartsTheInputStream()
    {
        var dom = new DomTree();

        // document.open() discards what the input stream holds by dropping it; the
        // replacement must not inherit any hand-over state.
        var first = new DocumentWriteStream();
        Assert.Equal(2, first.Write("<p>hello", dom).Count);

        var second = new DocumentWriteStream();
        var placements = second.Write("<p>hello", dom);
        Assert.Equal(2, placements.Count);
        Assert.Equal("hello", Text(dom, placements[1].Node));
    }

    [Fact]
    public void AnEmptyWriteProducesNothing()
    {
        var dom = new DomTree();
        var stream = new DocumentWriteStream();
        Assert.Empty(stream.Write(string.Empty, dom));
    }

    private static string Text(DomTree dom, NodeId node) =>
        dom.GetNode(node)!.Data is TextData text ? text.Contents : string.Empty;
}

/// <summary>
/// New coverage for <c>cdp_watchdog.rs</c>, which shipped without in-file tests.
/// </summary>
public sealed class CdpWatchdogTests
{
    private sealed class RecordingHandle : IIsolateHandle
    {
        private int _terminations;

        public int Terminations => Volatile.Read(ref _terminations);

        public void TerminateExecution() => Interlocked.Increment(ref _terminations);
    }

    [Fact]
    public void AnOverrunCommandIsTerminatedAndDisarmReportsIt()
    {
        var handle = new RecordingHandle();
        var armed = CdpWatchdog.Arm(handle, TimeSpan.FromMilliseconds(50));

        Assert.True(SpinUntil(() => armed.Fired, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, handle.Terminations);
        // The dispatcher must learn that it fired, so it can clear the isolate's
        // termination state before the next command runs.
        Assert.True(CdpWatchdog.Disarm(armed));
    }

    [Fact]
    public void ACommandDisarmedInTimeIsNeverTerminated()
    {
        var handle = new RecordingHandle();
        var armed = CdpWatchdog.Arm(handle, TimeSpan.FromMinutes(10));

        Assert.False(CdpWatchdog.Disarm(armed));
        Thread.Sleep(120);
        Assert.Equal(0, handle.Terminations);
        Assert.False(armed.Fired);
    }

    [Fact]
    public void ConcurrentlyArmedCommandsEachGetTheirOwnSlot()
    {
        // A single global slot would let one connection's arm overwrite another's
        // and leave that command unbounded.
        var longLived = new RecordingHandle();
        var shortLived = new RecordingHandle();

        var slow = CdpWatchdog.Arm(longLived, TimeSpan.FromMinutes(10));
        var fast = CdpWatchdog.Arm(shortLived, TimeSpan.FromMilliseconds(50));

        Assert.True(SpinUntil(() => fast.Fired, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, shortLived.Terminations);
        Assert.Equal(0, longLived.Terminations);

        Assert.True(CdpWatchdog.Disarm(fast));
        Assert.False(CdpWatchdog.Disarm(slow));
        Assert.Equal(0, longLived.Terminations);
    }

    [Fact]
    public void ArmingATighterDeadlineWakesTheWorkerEarly()
    {
        // The worker is parked on the far deadline; the new arm has to pulse it
        // awake or the near deadline is missed by minutes.
        var far = new RecordingHandle();
        var near = new RecordingHandle();

        var slow = CdpWatchdog.Arm(far, TimeSpan.FromMinutes(10));
        Thread.Sleep(20);
        var quick = CdpWatchdog.Arm(near, TimeSpan.FromMilliseconds(50));

        Assert.True(SpinUntil(() => quick.Fired, TimeSpan.FromSeconds(10)));
        CdpWatchdog.Disarm(quick);
        Assert.False(CdpWatchdog.Disarm(slow));
    }

    [Fact]
    public void AZeroBudgetFiresWithoutWaiting()
    {
        var handle = new RecordingHandle();
        var armed = CdpWatchdog.Arm(handle, TimeSpan.Zero);
        Assert.True(SpinUntil(() => armed.Fired, TimeSpan.FromSeconds(10)));
        Assert.True(CdpWatchdog.Disarm(armed));
    }

    [Fact]
    public void TheWatchdogInterruptsRunawaySynchronousJavaScript()
    {
        // Synchronous V8 work runs unbounded, so a timeout that only cancels at
        // await points cannot interrupt it. This is the whole reason the watchdog
        // terminates from another thread.
        using var engine = new V8ScriptEngine();
        var armed = CdpWatchdog.Arm(new V8IsolateHandle(engine), TimeSpan.FromMilliseconds(250));
        try
        {
            Assert.Throws<ScriptInterruptedException>(
                () => engine.Execute("while (true) { }"));
        }
        finally
        {
            Assert.True(CdpWatchdog.Disarm(armed));
        }
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }
}

/// <summary>
/// New end-to-end coverage: a real module graph fetched over HTTP through
/// <see cref="Obscura.Net.ObscuraHttpClient"/> and evaluated by V8, which is the
/// part of <c>module_loader.rs</c> that unit tests of resolution alone cannot
/// reach - the ClearScript loader contract, redirect bookkeeping, HTTP failure
/// handling, and the dynamic-import activity signal.
/// </summary>
public sealed class ModuleGraphLoadTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _origin;
    private readonly Obscura.Net.ObscuraHttpClient _client;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;

    public ModuleGraphLoadTests()
    {
        var port = FreePort();
        _origin = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_origin + "/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);

        // The SSRF gate blocks loopback by default; this is the documented
        // --allow-private-network escape, which the test server needs.
        _client = new Obscura.Net.ObscuraHttpClient(
            new Obscura.Net.CookieJar(),
            null,
            allowPrivateNetwork: true);
    }

    [Fact]
    public void AGraphLoadsThroughTheImportMapAndTheHttpClient()
    {
        using var loader = Loader("""{"imports":{"dep":"/vendor/dep.js"}}""");
        using var engine = NewEngine(loader);

        engine.Execute(RootInfo(), "import { v } from 'dep'; globalThis.result = v + 1;");

        Assert.Equal(42, engine.Evaluate("globalThis.result"));
        Assert.Contains(_origin + "/vendor/dep.js", loader.LoadedSpecifiers);
    }

    [Fact]
    public void ARedirectRecordsBothTheRequestedAndTheFinalSpecifier()
    {
        using var loader = Loader("{}");
        using var engine = NewEngine(loader);

        engine.Execute(RootInfo(), "import { f } from '/redirected.js'; globalThis.f = f;");

        Assert.Equal("final", engine.Evaluate("globalThis.f"));
        Assert.Contains(_origin + "/redirected.js", loader.LoadedSpecifiers);
        Assert.Contains(_origin + "/final.js", loader.LoadedSpecifiers);
    }

    [Fact]
    public void ADiamondDependencyIsFetchedOnce()
    {
        using var loader = Loader("{}");
        using var engine = NewEngine(loader);

        engine.Execute(
            RootInfo(),
            "import { a } from '/a.js'; import { b } from '/b.js'; globalThis.sum = a + b;");

        Assert.Equal(211, engine.Evaluate("globalThis.sum"));
        Assert.Single(loader.LoadedSpecifiers, s => s == _origin + "/c.js");
    }

    [Fact]
    public void AFailedFetchBecomesAScriptErrorRatherThanEscapingIntoV8()
    {
        using var loader = Loader("{}");
        using var engine = NewEngine(loader);

        var error = Assert.Throws<ScriptEngineException>(
            () => engine.Execute(RootInfo(), "import '/missing.js';"));
        Assert.Contains("returned HTTP 404", error.Message, StringComparison.Ordinal);

        // The engine is still usable, which is the whole point of not letting the
        // failure cross the boundary raw.
        Assert.Equal(3, engine.Evaluate("1 + 2"));
    }

    [Fact]
    public void ABareSpecifierWithNoMappingFailsTheImportWithTheRustMessage()
    {
        using var loader = Loader("{}");
        using var engine = NewEngine(loader);

        var error = Assert.Throws<ScriptEngineException>(
            () => engine.Execute(RootInfo(), "import 'lodash';"));
        Assert.Contains(
            "Bare module specifier \"lodash\" was not remapped by the import map",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADynamicImportRegistersModuleLoadActivityAndAStaticGraphDoesNot()
    {
        using var loader = Loader("{}");
        using var engine = NewEngine(loader);

        // A statically declared graph is already accounted for by the script queue
        // that owns it, so it must not show up as module-load activity.
        using (loader.BeginStaticGraph())
        {
            engine.Execute(RootInfo(), "import { v } from '/vendor/dep.js'; globalThis.v = v;");
        }

        Assert.Equal(41, engine.Evaluate("globalThis.v"));
        Assert.False(loader.Activity.IsPendingOrRecent(TimeSpan.FromSeconds(30)));

        // A lazy graph is not, and its fetch has to keep the page observably busy.
        engine.Execute(
            RootInfo("dynamic.js"),
            "import('/dyn.js').then(m => { globalThis.d = m.d; });");

        Assert.Equal("dyn", engine.Evaluate("globalThis.d"));
        Assert.True(loader.Activity.IsPendingOrRecent(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, loader.Activity.Pending);
    }

    private ObscuraModuleLoader Loader(string importMapJson)
    {
        Assert.True(
            ImportMap.TryParse(importMapJson, _origin + "/index.html", out var map, out var error),
            error);
        return new ObscuraModuleLoader(
            _origin + "/index.html",
            null,
            map,
            () => ModuleNetworkContext.From(_client, null, null));
    }

    private static V8ScriptEngine NewEngine(ObscuraModuleLoader loader)
    {
        // Without EnableDynamicModuleImports, ClearScript answers every import()
        // with "Not supported" and the loader is never consulted.
        var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableDynamicModuleImports);
        loader.Install(engine);
        return engine;
    }

    private DocumentInfo RootInfo(string name = "root.js") =>
        new(new Uri($"{_origin}/{name}")) { Category = ModuleCategory.Standard };

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            var response = context.Response;
            try
            {
                if (string.Equals(path, "/redirected.js", StringComparison.Ordinal))
                {
                    response.StatusCode = 302;
                    response.RedirectLocation = _origin + "/final.js";
                    response.Close();
                    continue;
                }

                var body = path switch
                {
                    "/vendor/dep.js" => "export const v = 41;",
                    "/final.js" => "export const f = 'final';",
                    "/dyn.js" => "export const d = 'dyn';",
                    "/a.js" => "import { c } from '/c.js'; export const a = 1 + c;",
                    "/b.js" => "import { c } from '/c.js'; export const b = 10 + c;",
                    "/c.js" => "export const c = 100;",
                    _ => null,
                };

                if (body is null)
                {
                    response.StatusCode = 404;
                    response.Close();
                    continue;
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                response.StatusCode = 200;
                response.ContentType = "text/javascript; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                response.Close();
            }
            catch (HttpListenerException)
            {
                // The client went away mid-response; nothing to do.
            }
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        try
        {
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener was closed out from under GetContextAsync.
        }

        _client.Dispose();
        _stopping.Dispose();
    }
}
