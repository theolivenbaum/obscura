using System.Text.Json.Nodes;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// A child frame's CDP isolated worlds are realms of their own over the frame's document,
/// and its default context is the frame's own realm (SECURITY.md M6).
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, which has no child-frame execution contexts at all and
/// runs every isolated-world command in the page realm. In Chromium each frame has a main
/// world and its own isolated worlds; Playwright and Puppeteer create a utility world in
/// every frame for frame locators.
/// </remarks>
public sealed class FrameIsolatedWorldTests
{
    private const string FrameHtml =
        "<html><body><h1 id=t>Child</h1><input id=i value=pre><div id=root></div></body></html>";

    private static async Task<JsonNode?> In(PocketCalculatorJsRuntime runtime, IsolatedWorldTarget target, string expression)
    {
        var info = await runtime.EvaluateForCdpWithTimeoutAsync(expression, true, true, 5_000, target);
        Assert.False(info.Thrown, info.Description);
        return info.Value;
    }

    [Fact]
    public async Task FrameWorldRunsOverTheFrameDocumentAndIgnoresItsTampering()
    {
        using var page = RuntimeFixture.Page("https://parent.example/page", "<html><body><h1 id=t>Parent</h1></body></html>");
        var runtime = page.Runtime;
        using var frame = FrameRealm.Create(runtime, 1, 0, "https://child.example/frame", FrameHtml);
        Assert.NotNull(frame);
        frame.ExecuteScript("""
            globalThis.frameSecret = 'frame';
            document.querySelector = () => null;
            Array.prototype.map = function () { return ['tampered']; };
            JSON.stringify = () => '"tampered"';
            """);

        var world = new IsolatedWorldTarget(200, "utility", [], frame.FrameId);
        Assert.Equal("Child", (await In(runtime, world, "document.querySelector('#t').textContent"))?.GetValue<string>());
        Assert.Equal("undefined", (await In(runtime, world, "typeof frameSecret"))?.GetValue<string>());
        Assert.Equal("{\"a\":1}", (await In(runtime, world, "JSON.stringify({a: [1].map(x => x)[0]})"))?.GetValue<string>());
        Assert.Equal("https://child.example/frame", (await In(runtime, world, "location.href"))?.GetValue<string>());

        // The frame's default context is the frame's own realm, tampering and all.
        var main = IsolatedWorldTarget.ForFrameMainWorld(201, frame.FrameId);
        Assert.Equal("frame", (await In(runtime, main, "frameSecret"))?.GetValue<string>());
        Assert.Equal("Child", (await In(runtime, main, "document.getElementById('t').textContent"))?.GetValue<string>());

        // The page realm is neither.
        Assert.Equal("Parent", runtime.Evaluate("document.querySelector('#t').textContent")?.GetValue<string>());
    }

    [Fact]
    public async Task FrameObjectIdsRouteToTheirRealm()
    {
        using var page = RuntimeFixture.Page("https://parent.example/page", "<html><body><h1 id=t>Parent</h1></body></html>");
        var runtime = page.Runtime;
        using var frame = FrameRealm.Create(runtime, 1, 0, "https://child.example/frame", FrameHtml);
        Assert.NotNull(frame);
        var world = new IsolatedWorldTarget(202, "utility", [], frame.FrameId);
        var main = IsolatedWorldTarget.ForFrameMainWorld(203, frame.FrameId);

        var inWorld = await runtime.EvaluateForCdpWithTimeoutAsync("document.getElementById('t')", false, false, 5_000, world);
        var inMain = await runtime.EvaluateForCdpWithTimeoutAsync("document.getElementById('t')", false, false, 5_000, main);
        Assert.Equal(202, PocketCalculatorJsRuntime.IsolatedWorldKeyOf(inWorld.ObjectId!));
        Assert.Equal(203, PocketCalculatorJsRuntime.IsolatedWorldKeyOf(inMain.ObjectId!));
        Assert.Equal(frame.FrameId, runtime.FrameIdOfObject(inWorld.ObjectId!));
        Assert.Equal(frame.FrameId, runtime.FrameIdOfObject(inMain.ObjectId!));

        frame.ExecuteScript("Element.prototype.getAttribute = () => 'tampered';");
        var fromWorld = await runtime.CallFunctionOnForCdpWithTimeoutAsync(
            "function () { return this.getAttribute('id') + ':' + this.textContent; }", inWorld.ObjectId, [], true, false, 5_000);
        Assert.Equal("t:Child", fromWorld.Value?.GetValue<string>());
        var fromMain = await runtime.CallFunctionOnForCdpWithTimeoutAsync(
            "function () { return this.getAttribute('id'); }", inMain.ObjectId, [], true, false, 5_000);
        Assert.Equal("tampered", fromMain.Value?.GetValue<string>());

        // An object of one realm is not an argument in another.
        await Assert.ThrowsAsync<JsRuntimeException>(() => runtime.CallFunctionOnForCdpWithTimeoutAsync(
            "function (x) { return x; }", inWorld.ObjectId, [new JsonObject { ["objectId"] = inMain.ObjectId }], true, false, 5_000));

        // The node id each realm reports is the frame document's.
        var nid = runtime.EvaluateInObjectRealm(
            inWorld.ObjectId!, $"__obscura_cdp.nidOf({System.Text.Json.JsonSerializer.Serialize(inWorld.ObjectId)})");
        Assert.Equal(frame.Evaluate("document.getElementById('t')._nid")?.GetValue<double>(), nid?.GetValue<double>());
    }

    [Fact]
    public async Task FrameWorldSharesNodeStateAndMutationsWithTheFrame()
    {
        using var page = RuntimeFixture.Page("https://parent.example/page", "<html><body><input id=i value=page></body></html>");
        var runtime = page.Runtime;
        using var frame = FrameRealm.Create(runtime, 1, 0, "https://child.example/frame", FrameHtml);
        Assert.NotNull(frame);
        var world = new IsolatedWorldTarget(204, "utility", [], frame.FrameId);

        frame.ExecuteScript("document.getElementById('i').value = 'typed';");
        Assert.Equal("typed", (await In(runtime, world, "document.getElementById('i').value"))?.GetValue<string>());
        await In(runtime, world, "const i = document.getElementById('i'); i.value = 'from world'; i.focus(); 1");
        Assert.Equal("from world", frame.Evaluate("document.getElementById('i').value")?.GetValue<string>());
        Assert.Equal("i", frame.Evaluate("document.activeElement && document.activeElement.id")?.GetValue<string>());
        // The page's own input is a different node in a different document.
        Assert.Equal("page", runtime.Evaluate("document.getElementById('i').value")?.GetValue<string>());

        await In(runtime, world, """
            globalThis.records = [];
            new MutationObserver(list => { for (const r of list) records.push(r.type + ':' + (r.addedNodes[0] ? r.addedNodes[0].id : '')); })
              .observe(document, {childList: true, subtree: true});
            1
            """);
        frame.ExecuteScript("const p = document.createElement('p'); p.id = 'late'; document.getElementById('root').appendChild(p);");
        runtime.ExecuteScript("page-mutation", "document.body.appendChild(document.createElement('span')).id = 'pagenode';");
        var seen = (await In(runtime, world, "new Promise(r => setTimeout(() => r(records.join('|')), 0))"))?.GetValue<string>() ?? string.Empty;
        Assert.Contains("childList:late", seen, StringComparison.Ordinal);
        Assert.DoesNotContain("pagenode", seen, StringComparison.Ordinal);

        runtime.ExecuteScript("page-observe", "globalThis.pageRecords = []; new MutationObserver(l => { for (const r of l) pageRecords.push(r.type); }).observe(document, {childList: true, subtree: true, attributes: true});");
        await In(runtime, world, "document.getElementById('root').appendChild(document.createElement('b')).id = 'fromworld'; 1");
        Assert.Equal("B", frame.Evaluate("document.getElementById('fromworld').tagName")?.GetValue<string>());
        await runtime.RunEventLoopBoundedAsync(20);
        Assert.Equal(string.Empty, runtime.Evaluate("pageRecords.join('|')")?.GetValue<string>());
    }

    [Fact]
    public async Task FrameWorldsGoAwayWithTheirFrame()
    {
        using var page = RuntimeFixture.Page("https://parent.example/page", "<html><body></body></html>");
        var runtime = page.Runtime;
        var frame = FrameRealm.Create(runtime, 1, 0, "https://child.example/frame", FrameHtml);
        Assert.NotNull(frame);
        var world = new IsolatedWorldTarget(205, "utility", [], frame.FrameId);
        var main = IsolatedWorldTarget.ForFrameMainWorld(206, frame.FrameId);
        var pageWorld = new IsolatedWorldTarget(207, "utility", []);
        var handle = await runtime.EvaluateForCdpWithTimeoutAsync("document.body", false, false, 5_000, world);
        await In(runtime, main, "1");
        await In(runtime, pageWorld, "1");
        Assert.Equal(2, runtime.IsolatedWorlds.Count);

        frame.Dispose();
        Assert.Single(runtime.IsolatedWorlds);
        var error = await Assert.ThrowsAsync<JsRuntimeException>(() => runtime.EvaluateForCdpWithTimeoutAsync("1", true, false, 5_000, world));
        Assert.Contains("Cannot find context", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<JsRuntimeException>(() => runtime.EvaluateForCdpWithTimeoutAsync("1", true, false, 5_000, main));
        await Assert.ThrowsAsync<JsRuntimeException>(() => runtime.CallFunctionOnForCdpWithTimeoutAsync(
            "function () { return 1; }", handle.ObjectId, [], true, false, 5_000));
        Assert.Equal(1, (await In(runtime, pageWorld, "1"))?.GetValue<double>());
    }

    [Fact]
    public async Task WorldsAreCappedPerDocument()
    {
        using var page = RuntimeFixture.Page("https://parent.example/page", "<html><body></body></html>");
        var runtime = page.Runtime;
        using var frame = FrameRealm.Create(runtime, 1, 0, "https://child.example/frame", FrameHtml);
        Assert.NotNull(frame);
        for (var key = 300; key < 300 + PocketCalculatorJsRuntime.MaxIsolatedWorlds; key++)
        {
            await In(runtime, new IsolatedWorldTarget(key, "w" + key, [], frame.FrameId), "1");
        }
        var error = await Assert.ThrowsAsync<JsRuntimeException>(() => runtime.EvaluateForCdpWithTimeoutAsync(
            "1", true, false, 5_000, new IsolatedWorldTarget(1000, "one-too-many", [], frame.FrameId)));
        Assert.Contains("Too many isolated worlds", error.Message, StringComparison.Ordinal);
        // The page's document has a budget of its own.
        Assert.Equal(1, (await In(runtime, new IsolatedWorldTarget(1001, "page", []), "1"))?.GetValue<double>());
    }
}
