using System.Text.Json.Nodes;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// An event dispatched in one realm runs the listeners every isolated world registered on
/// the node, in registration order, each world with its own wrappers (SECURITY.md M6).
/// </summary>
/// <remarks>
/// Measured in Chromium (Playwright's build): a listener a utility world adds to a node
/// runs for a click the page dispatches, between the page's listeners added before and
/// after it; <c>isTrusted</c> is the event's; a world's <c>stopPropagation</c> stops the
/// page's bubbling. The engine's own event model has no capture phase for nodes and does
/// not reach the window, so a world's window listeners run around the node dispatch.
/// </remarks>
public sealed class WorldEventDispatchTests
{
    private static readonly IsolatedWorldTarget Utility = new(100, "utility", []);

    private static async Task<JsonNode?> InWorld(PocketCalculatorJsRuntime runtime, string expression, IsolatedWorldTarget? world = null)
    {
        var info = await runtime.EvaluateForCdpWithTimeoutAsync(expression, true, true, 5_000, world ?? Utility);
        Assert.False(info.Thrown, info.Description);
        return info.Value;
    }

    private const string Page = "<html><body><div id=a><button id=b>go</button></div></body></html>";

    [Fact]
    public async Task PageDispatchRunsWorldListenersInRegistrationOrder()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("early", """
            globalThis.log = [];
            document.getElementById('b').addEventListener('ping', () => log.push('page-early'));
            """);
        await InWorld(runtime, """
            globalThis.seen = [];
            const b = document.getElementById('b');
            b.addEventListener('ping', function (e) {
              seen.push([e.type, e.target === b, e.currentTarget === b, this === b, e.eventPhase, e.isTrusted].join());
            });
            document.getElementById('a').addEventListener('ping', e => seen.push('bubbled:' + (e.target === b) + ':' + e.eventPhase));
            1
            """);
        runtime.ExecuteScript("dispatch", """
            document.getElementById('b').addEventListener('ping', () => log.push('page-late'));
            document.getElementById('b').dispatchEvent(new Event('ping', { bubbles: true }));
            """);
        Assert.Equal("page-early,page-late", runtime.Evaluate("log.join()")?.GetValue<string>());
        Assert.Equal("ping,true,true,true,2,false|bubbled:true:3", (await InWorld(runtime, "seen.join('|')"))?.GetValue<string>());

        // Interleaving: a world listener registered between two page listeners runs between them.
        runtime.ExecuteScript("order", """
            globalThis.order = [];
            document.getElementById('b').addEventListener('order', () => order.push('p1:' + document.getElementById('b').getAttribute('data-w')));
            """);
        await InWorld(runtime, "document.getElementById('b').addEventListener('order', () => document.getElementById('b').setAttribute('data-w', '1')); 1");
        runtime.ExecuteScript("order2", """
            document.getElementById('b').addEventListener('order', () => order.push('p2:' + document.getElementById('b').getAttribute('data-w')));
            document.getElementById('b').dispatchEvent(new Event('order'));
            """);
        Assert.Equal("p1:null,p2:1", runtime.Evaluate("order.join()")?.GetValue<string>());
    }

    [Fact]
    public async Task TrustedHostInputIsTrustedInTheWorld()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        await InWorld(runtime, """
            globalThis.clicks = [];
            document.getElementById('b').addEventListener('click', e => clicks.push(e.isTrusted + ':' + (e instanceof MouseEvent)));
            1
            """);
        runtime.EvaluateHost("""
            (function () {
              var h = __obscura_host.dom;
              h.dispatch(h.querySelector(h.document(), '#b'),
                h.event('MouseEvent', 'click', { __proto__: null, bubbles: true, cancelable: true }, true));
              return true;
            })()
            """);
        runtime.ExecuteScript("untrusted", "document.getElementById('b').dispatchEvent(new MouseEvent('click', { bubbles: true }));");
        Assert.Equal("true:true,false:true", (await InWorld(runtime, "clicks.join()"))?.GetValue<string>());
    }

    [Fact]
    public async Task PropagationFlagsCrossRealms()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("page", """
            globalThis.log = [];
            document.getElementById('b').addEventListener('stop', e => { log.push('page-b'); e.preventDefault(); });
            document.getElementById('a').addEventListener('stop', () => log.push('page-a'));
            """);
        await InWorld(runtime, """
            globalThis.saw = [];
            document.getElementById('b').addEventListener('stop', e => { saw.push('prevented:' + e.defaultPrevented); e.stopPropagation(); });
            1
            """);
        var result = runtime.Evaluate("document.getElementById('b').dispatchEvent(new Event('stop', { bubbles: true, cancelable: true }))");
        Assert.False(result?.GetValue<bool>());
        Assert.Equal("page-b", runtime.Evaluate("log.join()")?.GetValue<string>());
        Assert.Equal("prevented:true", (await InWorld(runtime, "saw.join()"))?.GetValue<string>());

        // A world's preventDefault is the page's answer.
        await InWorld(runtime, "document.getElementById('a').addEventListener('cancel', e => e.preventDefault()); 1");
        Assert.False(runtime.Evaluate("document.getElementById('b').dispatchEvent(new Event('cancel', { bubbles: true, cancelable: true }))")?.GetValue<bool>());
        Assert.True(runtime.Evaluate("document.getElementById('b').dispatchEvent(new Event('cancel', { bubbles: true }))")?.GetValue<bool>());
    }

    [Fact]
    public async Task WorldDispatchRunsEveryRealmOnce()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("page", """
            globalThis.log = [];
            document.getElementById('b').addEventListener('mine', e => log.push('page:' + e.isTrusted + ':' + e.detail.n));
            """);
        var result = await InWorld(runtime, """
            const b = document.getElementById('b');
            const runs = [];
            const ev = new CustomEvent('mine', { bubbles: true, cancelable: true, detail: { n: 7 } });
            b.addEventListener('mine', e => runs.push(e === ev));
            document.body.addEventListener('mine', e => { runs.push('body:' + (e === ev)); e.preventDefault(); });
            const answer = b.dispatchEvent(ev);
            [runs.join(), answer, ev.defaultPrevented].join('|')
            """);
        Assert.Equal("true,body:true|false|true", result?.GetValue<string>());
        Assert.Equal("page:false:7", runtime.Evaluate("log.join()")?.GetValue<string>());
    }

    [Fact]
    public async Task WorldWindowListenersSeeNodeEvents()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("page", """
            globalThis.log = [];
            document.getElementById('b').addEventListener('click', () => log.push('page'));
            """);
        // Playwright's hit-target interceptor: a capturing window listener in its utility world.
        await InWorld(runtime, """
            globalThis.hits = [];
            window.addEventListener('click', e => {
              hits.push('capture:' + e.target.id + ':' + e.isTrusted + ':' + e.eventPhase);
              if (globalThis.block) { e.stopPropagation(); e.preventDefault(); }
            }, { capture: true });
            window.addEventListener('click', e => hits.push('bubble:' + e.eventPhase));
            1
            """);
        const string Click = """
            (function () {
              var h = __obscura_host.dom;
              var b = h.querySelector(h.document(), '#b');
              return h.dispatch(b, h.event('MouseEvent', 'click', { __proto__: null, bubbles: true, cancelable: true }, true));
            })()
            """;
        Assert.True(runtime.EvaluateHost(Click)?.GetValue<bool>());
        Assert.Equal("page", runtime.Evaluate("log.join()")?.GetValue<string>());
        Assert.Equal("capture:b:true:1,bubble:3", (await InWorld(runtime, "hits.join()"))?.GetValue<string>());

        await InWorld(runtime, "globalThis.block = true; 1");
        Assert.False(runtime.EvaluateHost(Click)?.GetValue<bool>());
        Assert.Equal("page", runtime.Evaluate("log.join()")?.GetValue<string>());
    }

    [Fact]
    public async Task OnceAndRemovedListenersStop()
    {
        using var fixture = RuntimeFixture.Setup(Page);
        var runtime = fixture.Runtime;
        await InWorld(runtime, """
            globalThis.n = 0; globalThis.m = 0;
            const b = document.getElementById('b');
            b.addEventListener('tick', () => n++, { once: true });
            globalThis.counter = () => m++;
            b.addEventListener('tick', counter);
            1
            """);
        runtime.ExecuteScript("t1", "for (let i = 0; i < 3; i++) document.getElementById('b').dispatchEvent(new Event('tick'));");
        Assert.Equal("1,3", (await InWorld(runtime, "[n, m].join()"))?.GetValue<string>());
        await InWorld(runtime, "document.getElementById('b').removeEventListener('tick', counter); 1");
        runtime.ExecuteScript("t2", "document.getElementById('b').dispatchEvent(new Event('tick'));");
        Assert.Equal("1,3", (await InWorld(runtime, "[n, m].join()"))?.GetValue<string>());
    }

    /// <summary>
    /// A file input's selection is node state: the files the host gave the page realm are
    /// the files a world reads, with the page's trusted change event reaching the world.
    /// </summary>
    [Fact]
    public async Task FileInputSelectionIsSharedWithWorlds()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><input id=f type=file></body></html>");
        var runtime = fixture.Runtime;
        await InWorld(runtime, """
            globalThis.changes = [];
            document.getElementById('f').addEventListener('change', e => changes.push(e.isTrusted + ':' + e.target.files.length));
            [document.getElementById('f').files.length, document.getElementById('f').value].join('|')
            """);
        runtime.EvaluateHost("""
            (function () {
              var h = __obscura_host.dom;
              __obscura_host.setInputFiles(h.querySelector(h.document(), '#f'),
                [{ name: 'a.txt', type: 'text/plain', b64: 'aGk=' }, { name: 'b.bin', type: '', b64: '' }]);
              return true;
            })()
            """);
        Assert.Equal("2:a.txt", runtime.Evaluate("document.getElementById('f').files.length + ':' + document.getElementById('f').files[0].name")?.GetValue<string>());
        var seen = await InWorld(runtime, """
            (async () => {
              const input = document.getElementById('f');
              const files = input.files;
              return [files.length, files[0].name, files[0].type, await files[0].text(), input.value,
                files === input.files, changes.join()].join('|');
            })()
            """);
        Assert.Equal("2|a.txt|text/plain|hi|C:\\fakepath\\a.txt|true|true:2", seen?.GetValue<string>());
    }

    [Fact]
    public async Task FrameDispatchReachesOnlyThatFramesWorlds()
    {
        using var page = RuntimeFixture.Page("https://parent.example/page", Page);
        var runtime = page.Runtime;
        using var frame = FrameRealm.Create(runtime, 1, 0, "https://child.example/frame", Page);
        Assert.NotNull(frame);
        var frameWorld = new IsolatedWorldTarget(200, "utility", [], frame.FrameId);
        await InWorld(runtime, "globalThis.got = []; document.getElementById('b').addEventListener('x', () => got.push('frame-world')); 1", frameWorld);
        await InWorld(runtime, "globalThis.got = []; document.getElementById('b').addEventListener('x', () => got.push('page-world')); 1");

        frame.ExecuteScript("document.getElementById('b').dispatchEvent(new Event('x'));");
        Assert.Equal("frame-world", (await InWorld(runtime, "got.join()", frameWorld))?.GetValue<string>());
        Assert.Equal(string.Empty, (await InWorld(runtime, "got.join()"))?.GetValue<string>());
        runtime.ExecuteScript("page", "document.getElementById('b').dispatchEvent(new Event('x'));");
        Assert.Equal("page-world", (await InWorld(runtime, "got.join()"))?.GetValue<string>());
        Assert.Equal("frame-world", (await InWorld(runtime, "got.join()", frameWorld))?.GetValue<string>());
    }
}
