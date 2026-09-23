using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// What a fragment navigation does to the URL and to the window's events.
/// </summary>
/// <remarks>
/// These have no Rust counterpart: <c>bootstrap.js</c> is shared, but nothing in
/// <c>crates/obscura-js</c> pins this behavior, and every value asserted here was measured
/// against real Chromium on a trivial page. Chromium fires <c>hashchange</c> then
/// <c>popstate</c> for every location-driven fragment navigation and for an anchor click,
/// and fires <em>neither</em> for <c>history.pushState</c> / <c>replaceState</c>, even when
/// the URL they write differs in its fragment.
/// </remarks>
public sealed class FragmentNavigationTests
{
    /// <summary>Counts both events and remembers what the last hashchange was given.</summary>
    private const string Instrument =
        """
        (function () {
            globalThis.__hc = 0;
            globalThis.__ps = 0;
            globalThis.__hcNew = '';
            addEventListener('hashchange', function (e) { __hc++; __hcNew = e.newURL || ''; });
            addEventListener('popstate', function () { __ps++; });
        })()
        """;

    private static RuntimeFixture Instrumented()
    {
        var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        fixture.Runtime.Evaluate(Instrument);
        return fixture;
    }

    private static void AssertProbe(
        RuntimeFixture fixture,
        string navigation,
        string expectedHref,
        int hashchange,
        int popstate)
    {
        fixture.Runtime.Evaluate(navigation);
        JsonNode? probe = fixture.Runtime.Evaluate("[location.href, __hc, __ps]");

        Assert.NotNull(probe);
        JsonArray values = Assert.IsType<JsonArray>(probe);
        Assert.Equal(expectedHref, values[0]!.GetValue<string>());
        Assert.Equal(hashchange, values[1]!.GetValue<int>());
        Assert.Equal(popstate, values[2]!.GetValue<int>());
    }

    [Theory]
    [InlineData("location.hash = '/x'")]
    [InlineData("location.href = '#/x'")]
    [InlineData("location.assign('#/x')")]
    [InlineData("location.replace('#/x')")]
    public void ALocationDrivenFragmentNavigationFiresHashchangeThenPopstate(string navigation)
    {
        using RuntimeFixture fixture = Instrumented();

        AssertProbe(fixture, navigation, "http://example.com/test#/x", 1, 1);
    }

    [Fact]
    public void PushStateToAnotherFragmentFiresNeitherEvent()
    {
        using RuntimeFixture fixture = Instrumented();

        // Per spec, and per Chromium: pushState moves the URL and queues no event at all.
        // Firing hashchange here made a router that both pushes state and listens for
        // hashchange route twice for one navigation.
        AssertProbe(fixture, "history.pushState({}, '', '#/x')", "http://example.com/test#/x", 0, 0);
    }

    [Fact]
    public void ReplaceStateToAnotherFragmentFiresNeitherEvent()
    {
        using RuntimeFixture fixture = Instrumented();

        AssertProbe(fixture, "history.replaceState({}, '', '#/x')", "http://example.com/test#/x", 0, 0);
    }

    [Fact]
    public void ClickingAnAnchorWithAFragmentHrefNavigates()
    {
        using RuntimeFixture fixture = Instrumented();

        // The click path used to skip a fragment href outright, back when every
        // location.assign tore the document down. That meant clicking an in-page link --
        // how most single page apps route -- did nothing at all: no URL change, no event.
        AssertProbe(
            fixture,
            "(function () { var a = document.createElement('a'); a.href = '#/x';"
            + " document.body.appendChild(a); a.click(); })()",
            "http://example.com/test#/x",
            1,
            1);
    }

    [Fact]
    public void ClickingAnAnchorHrefHashFromAFragmentlessUrlStillFiresHashchange()
    {
        using RuntimeFixture fixture = Instrumented();

        // An empty fragment is not the same as no fragment, though `new URL(u).hash`
        // reports '' for both. Chromium fires hashchange here and not on a second click.
        AssertProbe(
            fixture,
            "(function () { var a = document.createElement('a'); a.href = '#';"
            + " document.body.appendChild(a); a.click(); })()",
            "http://example.com/test#",
            1,
            1);
    }

    [Fact]
    public void NavigatingToTheFragmentAlreadyInTheUrlFiresPopstateOnly()
    {
        using RuntimeFixture fixture = Instrumented();
        fixture.Runtime.Evaluate("location.hash = '/x'");

        // Still a fragment navigation -- Chromium creates a history entry and fires
        // popstate -- but the fragment did not move, so there is no second hashchange.
        AssertProbe(fixture, "location.assign('#/x')", "http://example.com/test#/x", 1, 2);
    }

    [Fact]
    public void TheHashchangeEventCarriesTheNewUrl()
    {
        using RuntimeFixture fixture = Instrumented();
        fixture.Runtime.Evaluate("location.hash = '/x'");

        Assert.Equal(
            "http://example.com/test#/x",
            fixture.Runtime.Evaluate("__hcNew")!.GetValue<string>());
    }

    [Fact]
    public void OnHashchangeAndOnPopstatePropertiesAreCalledToo()
    {
        using RuntimeFixture fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;

        // window.dispatchEvent only runs addEventListener registrations, so the
        // `window.onhashchange = fn` form (still common in hash routers) needs its own call.
        rt.Evaluate(
            "(function () { globalThis.__seen = [];"
            + " onhashchange = function () { __seen.push('hashchange'); };"
            + " onpopstate = function () { __seen.push('popstate'); }; })()");
        rt.Evaluate("location.hash = '/x'");

        Assert.Equal("""["hashchange","popstate"]""", rt.Evaluate("__seen")!.ToJsonString());
    }

    [Fact]
    public void APathChangeIsNotAFragmentNavigation()
    {
        using RuntimeFixture fixture = Instrumented();

        // The guard that keeps the fragment path from swallowing a real navigation: only
        // the fragment may differ. This one asks for a document, so no event fires here and
        // the host is handed a pending navigation instead.
        fixture.Runtime.Evaluate("location.href = '/other'");

        JsonNode? events = fixture.Runtime.Evaluate("[__hc, __ps]");
        Assert.Equal("[0,0]", events!.ToJsonString());
    }
}
