using System.Text.Json.Nodes;
using PocketCalculator.Js.Modules;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// CDP isolated worlds are realms of their own over the page's document (SECURITY.md M6).
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, which runs every isolated-world command in the page
/// realm. Chromium's isolated world has its own global object and built-ins and shares
/// only the DOM, so a page cannot see or replace what automation scripts use there.
/// </remarks>
public sealed class IsolatedWorldTests
{
    private static readonly IsolatedWorldTarget Utility = new(100, "__puppeteer_utility_world__", []);

    private static async Task<JsonNode?> InWorld(PocketCalculatorJsRuntime runtime, string expression, IsolatedWorldTarget? world = null)
    {
        var info = await runtime.EvaluateForCdpWithTimeoutAsync(expression, true, true, 5_000, world ?? Utility);
        Assert.False(info.Thrown, info.Description);
        return info.Value;
    }

    [Fact]
    public async Task WorldGlobalsAreNotThePages()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p id=a>x</p></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("page", "globalThis.pageSecret = 'page'; window.shared = 1;");
        Assert.Equal("undefined", (await InWorld(runtime, "typeof pageSecret"))?.GetValue<string>());

        await InWorld(runtime, "globalThis.worldSecret = 'world'; 1");
        Assert.Equal("undefined", runtime.Evaluate("typeof worldSecret")?.GetValue<string>());
        // The same world keeps its globals across calls.
        Assert.Equal("world", (await InWorld(runtime, "worldSecret"))?.GetValue<string>());
    }

    [Fact]
    public async Task PageTamperingDoesNotReachTheWorld()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p id=a>real</p><p id=b>two</p></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("tamper", """
            document.querySelector = () => null;
            Document.prototype.querySelector = () => null;
            Element.prototype.querySelectorAll = () => [];
            Array.prototype.map = function () { return ['tampered']; };
            JSON.stringify = () => '"tampered"';
            Promise.prototype.then = function () { throw new Error('tampered'); };
            """);
        Assert.Equal("tampered", runtime.Evaluate("[1].map(x => x)[0]")?.GetValue<string>());

        var text = await InWorld(runtime, "document.querySelector('#a').textContent");
        Assert.Equal("real", text?.GetValue<string>());
        var mapped = await InWorld(runtime, "[...document.body.querySelectorAll('p')].map(p => p.id).join(',')");
        Assert.Equal("a,b", mapped?.GetValue<string>());
        var json = await InWorld(runtime, "JSON.stringify({ok: true})");
        Assert.Equal("{\"ok\":true}", json?.GetValue<string>());
        var awaited = await InWorld(runtime, "Promise.resolve(41).then(v => v + 1)");
        Assert.Equal(42, awaited?.GetValue<double>());
    }

    [Fact]
    public async Task NodesAreSharedAndWrappersAreDistinct()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p id=a>x</p></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("mark", "document.getElementById('a').pageExpando = 1;");
        Assert.Equal("undefined", (await InWorld(runtime, "typeof document.getElementById('a').pageExpando"))?.GetValue<string>());

        // A DOM change in either realm is the same document.
        await InWorld(runtime, "document.getElementById('a').textContent = 'from world'; 1");
        Assert.Equal("from world", runtime.Evaluate("document.getElementById('a').textContent")?.GetValue<string>());
        runtime.ExecuteScript("add", "document.body.appendChild(document.createElement('span')).id = 's';");
        Assert.Equal("SPAN", (await InWorld(runtime, "document.getElementById('s').tagName"))?.GetValue<string>());
    }

    [Fact]
    public async Task FormStateFocusAndSelectionAreShared()
    {
        using var fixture = RuntimeFixture.Setup(
            "<html><body><input id=i value=initial><input id=c type=checkbox></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("set", "document.getElementById('i').value = 'typed';");
        Assert.Equal("typed", (await InWorld(runtime, "document.getElementById('i').value"))?.GetValue<string>());

        await InWorld(runtime, "const i = document.getElementById('i'); i.focus(); i.select(); document.getElementById('c').checked = true; 1");
        Assert.Equal("i", runtime.Evaluate("document.activeElement.id")?.GetValue<string>());
        Assert.Equal(5, runtime.Evaluate("document.activeElement.selectionEnd")?.GetValue<double>());
        Assert.True(runtime.Evaluate("document.getElementById('c').checked")?.GetValue<bool>());
        Assert.Equal("i", (await InWorld(runtime, "document.activeElement.id"))?.GetValue<string>());

        await InWorld(runtime, "document.getElementById('i').value = 'from world'; 1");
        Assert.Equal("from world", runtime.Evaluate("document.getElementById('i').value")?.GetValue<string>());
    }

    [Fact]
    public async Task WorldEventsReachPageListenersUntrusted()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><select id=s><option>a</option></select></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("listen", """
            globalThis.seen = [];
            document.body.addEventListener('change', e => seen.push(e.type + ':' + e.isTrusted + ':' + e.bubbles));
            """);
        await InWorld(runtime, "document.getElementById('s').dispatchEvent(new Event('change', {bubbles: true}))");
        Assert.Equal("change:false:true", runtime.Evaluate("seen.join('|')")?.GetValue<string>());
    }

    [Fact]
    public async Task PageMutationsReachWorldObservers()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=root></div></body></html>");
        var runtime = fixture.Runtime;
        await InWorld(runtime, """
            globalThis.records = [];
            new MutationObserver(list => { for (const r of list) records.push(r.type + ':' + (r.addedNodes[0] ? r.addedNodes[0].id : '') + (r.attributeName || '')); })
              .observe(document, {childList: true, subtree: true, attributes: true});
            1
            """);
        runtime.ExecuteScript("mutate", """
            const n = document.createElement('p'); n.id = 'late';
            document.getElementById('root').appendChild(n);
            """);
        runtime.ExecuteScript("mutate2", "document.getElementById('late').setAttribute('data-x', '1');");
        var seen = await InWorld(runtime, "new Promise(r => setTimeout(() => r(records.join('|')), 0))");
        Assert.Contains("childList:late", seen?.GetValue<string>() ?? string.Empty);
        Assert.Contains("attributes:data-x", seen?.GetValue<string>() ?? string.Empty);
    }

    [Fact]
    public async Task WorldMutationsReachPageObserversAndCachesStayFresh()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><div id=a></div><div id=b></div><p id=p></p></body></html>");
        var runtime = fixture.Runtime;
        // The world reads (and caches) the paragraph's parent before the page moves it.
        Assert.Equal("BODY", (await InWorld(runtime, "document.getElementById('p').parentNode.tagName"))?.GetValue<string>());
        runtime.ExecuteScript("move", "document.getElementById('a').appendChild(document.getElementById('p'));");
        Assert.Equal("a", (await InWorld(runtime, "document.getElementById('p').parentNode.id"))?.GetValue<string>());

        runtime.ExecuteScript("observe", """
            globalThis.pageRecords = [];
            new MutationObserver(list => { for (const r of list) pageRecords.push(r.type + ':' + r.target.id); })
              .observe(document.body, {childList: true, subtree: true, attributes: true});
            """);
        Assert.Equal("a", runtime.Evaluate("document.getElementById('p').parentNode.id")?.GetValue<string>());
        await InWorld(runtime, "document.getElementById('b').appendChild(document.getElementById('p')); document.getElementById('b').setAttribute('title', 't'); 1");
        await runtime.RunEventLoopBoundedAsync(20);
        Assert.Equal("b", runtime.Evaluate("document.getElementById('p').parentNode.id")?.GetValue<string>());
        var records = runtime.Evaluate("pageRecords.join('|')")?.GetValue<string>() ?? string.Empty;
        Assert.Contains("childList:b", records);
        Assert.Contains("attributes:b", records);
    }

    [Fact]
    public async Task ObjectIdsNameTheirWorld()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p id=a>x</p></body></html>");
        var runtime = fixture.Runtime;
        var handle = await runtime.EvaluateForCdpWithTimeoutAsync("document.getElementById('a')", false, false, 5_000, Utility);
        Assert.NotNull(handle.ObjectId);
        Assert.Equal(Utility.Key, PocketCalculatorJsRuntime.IsolatedWorldKeyOf(handle.ObjectId!));
        Assert.Equal("node", handle.Subtype);

        runtime.ExecuteScript("tamper", "Element.prototype.getAttribute = () => 'tampered';");
        var id = await runtime.CallFunctionOnForCdpWithTimeoutAsync(
            "function () { return this.getAttribute('id'); }", handle.ObjectId, [], true, false, 5_000);
        Assert.Equal("a", id.Value?.GetValue<string>());

        var page = await runtime.EvaluateForCdpWithTimeoutAsync("document.body", false, false, 5_000);
        Assert.Equal(0, PocketCalculatorJsRuntime.IsolatedWorldKeyOf(page.ObjectId!));
        await Assert.ThrowsAsync<JsRuntimeException>(() => runtime.CallFunctionOnForCdpWithTimeoutAsync(
            "function (x) { return x; }", handle.ObjectId, [new JsonObject { ["objectId"] = page.ObjectId }], true, false, 5_000));

        // DOM.describeNode's lookup runs in the object's own realm.
        var nid = runtime.EvaluateInObjectRealm(
            handle.ObjectId!, $"__obscura_cdp.objects[{System.Text.Json.JsonSerializer.Serialize(handle.ObjectId)}]._nid");
        Assert.Equal(runtime.Evaluate("document.getElementById('a')._nid")?.GetValue<double>(), nid?.GetValue<double>());
    }

    /// <summary>
    /// A page that shadows <c>document.querySelector</c> still has a body: Chromium never
    /// derives it from a script-visible method, and automation reached through the page
    /// realm (hit testing, <c>document.body.appendChild</c> in the page's own script) needs it.
    /// </summary>
    [Fact]
    public void ShadowingDocumentQuerySelectorKeepsBodyAndHead()
    {
        using var fixture = RuntimeFixture.Setup("<html><head></head><body><p id=a>x</p></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("shadow", "document.querySelector = () => null;");
        Assert.Equal("BODY:HEAD", runtime.Evaluate("document.body.tagName + ':' + document.head.tagName")?.GetValue<string>());
        Assert.Null(runtime.Evaluate("document.querySelector('p')"));
    }

    /// <summary>A world is a realm of the page's isolate, so the page's watchdog stops it.</summary>
    [Fact]
    public async Task TheWatchdogStopsARunawayWorld()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        await InWorld(runtime, "1");
        var armed = CdpWatchdog.Arm(runtime.IsolateHandleForWatchdog, TimeSpan.FromMilliseconds(300));
        var error = await Assert.ThrowsAsync<JsRuntimeException>(
            () => runtime.EvaluateForCdpWithTimeoutAsync("for (;;) {}", true, false, 5_000, Utility));
        Assert.Contains("execution terminated", error.Message, StringComparison.Ordinal);
        Assert.True(CdpWatchdog.Disarm(armed));
        runtime.CancelTermination();
        Assert.Equal(2, (await InWorld(runtime, "1 + 1"))?.GetValue<double>());
    }

    [Fact]
    public async Task WorldsPerDocumentAreCapped()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        for (var key = 2; key < 2 + PocketCalculatorJsRuntime.MaxIsolatedWorlds; key++)
        {
            await InWorld(runtime, "1", new IsolatedWorldTarget(key, "w" + key, []));
        }
        var error = await Assert.ThrowsAsync<JsRuntimeException>(() => runtime.EvaluateForCdpWithTimeoutAsync(
            "1", true, false, 5_000, new IsolatedWorldTarget(1000, "one-too-many", [])));
        Assert.Contains("Too many isolated worlds", error.Message, StringComparison.Ordinal);
        // An existing world still answers.
        Assert.Equal(1, (await InWorld(runtime, "1", new IsolatedWorldTarget(2, "w2", [])))?.GetValue<double>());
    }

    [Fact]
    public async Task WorldInitScriptsRunAtCreation()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        var world = new IsolatedWorldTarget(101, "w", ["globalThis.initRan = (globalThis.initRan || 0) + 1;"]);
        Assert.Equal(1, (await InWorld(runtime, "initRan", world))?.GetValue<double>());
        Assert.Equal(1, (await InWorld(runtime, "initRan", world))?.GetValue<double>());
        Assert.Equal("undefined", runtime.Evaluate("typeof initRan")?.GetValue<string>());
    }
}
