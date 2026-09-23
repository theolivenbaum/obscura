using System.Text.Json.Nodes;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The shim's host helpers (<c>markTrusted</c>, <c>deliverMessage</c>, ...) are reachable
/// from host script as <c>__obscura_host</c> and from nowhere a page can name.
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, which publishes each helper as a page-visible
/// <c>__obscura_*</c> global, so <c>__obscura_markTrusted(new Event('x')).isTrusted</c>
/// is true for any page. In Chromium page script can never create a trusted event.
/// </remarks>
public sealed class HostHelpersHidden
{
    /// <summary>Every global that used to carry a helper, or could hand one over.</summary>
    private const string FormerGlobals = """
        ['__obscura_markTrusted', '__obscura_setFieldValue', '__obscura_setInputFiles',
         '__obscura_deliverMessage', '__obscura_activateLabel', '__obscura_isDisabled',
         '__obscura_labeledControl', '__obscura_interactiveHost',
         '__obscura_registerLinkedStylesheet', '__obscura_tryFragmentNavigate',
         '__obscura_set_screen_override', '__obscura_liveFrameIds', '__obscura_forgetFrame',
         '__obscura_mouse_down', '__obscura_host', '__obscura_host_handoff',
         '__obscura_core_handoff', '__obscura_deno_core', 'Deno']
        """;

    /// <summary>
    /// The names each reachable path reports; an empty array means none leaks.
    /// </summary>
    private const string ReachableProbe = "(() => { const names = " + FormerGlobals + """
        ;
          const own = new Set(Object.getOwnPropertyNames(globalThis));
          const keys = new Set(Reflect.ownKeys(globalThis));
          const leaks = [];
          for (const name of names) {
            if (typeof globalThis[name] !== 'undefined') leaks.push('typeof ' + name);
            if (Object.prototype.hasOwnProperty.call(globalThis, name)) leaks.push('own ' + name);
            if (name in globalThis) leaks.push('in ' + name);
            if (own.has(name) || keys.has(name)) leaks.push('listed ' + name);
            if (Object.getOwnPropertyDescriptor(globalThis, name) !== undefined) leaks.push('descriptor ' + name);
          }
          return JSON.stringify(leaks);
        })()
        """;

    [Fact]
    public void PageScriptCannotReachAnyHostHelper()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><input id=i></body></html>");
        AssertEmptyArray(fixture.Runtime.Evaluate(ReachableProbe));
    }

    [Fact]
    public void FrameScriptCannotReachAnyHostHelper()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://child.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        AssertEmptyArray(frame.Evaluate(ReachableProbe));
    }

    /// <summary>
    /// A page-built event stays untrusted whatever the page tries: every former helper
    /// name, and replacing <c>WeakSet.prototype</c> methods to capture the set the shim
    /// keeps trusted events in and add to it.
    /// </summary>
    [Fact]
    public void PageCreatedEventStaysUntrusted()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><input id=i></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecuteScript("hook", """
            globalThis.__sets = new Set();
            const has = WeakSet.prototype.has, add = WeakSet.prototype.add;
            globalThis.__add = add;
            WeakSet.prototype.has = function (v) { __sets.add(this); return has.call(this, v); };
            WeakSet.prototype.add = function (v) { __sets.add(this); return add.call(this, v); };
            """);
        // A trusted event built by the host, so every WeakSet the shim touches on the
        // trusted path has been handed to the hooks at least once.
        Assert.True(runtime.EvaluateHost(
            "(() => { const e = __obscura_host.markTrusted(new Event('warm')); document.dispatchEvent(e); return e.isTrusted; })()")!
            .GetValue<bool>());

        var result = runtime.Evaluate("(() => { const names = " + FormerGlobals + """
            ;
              const seen = [];
              const ev = new Event('forged', { bubbles: true });
              document.getElementById('i').addEventListener('forged', (e) => seen.push(e.isTrusted));
              for (const name of names) {
                try { const f = globalThis[name]; if (typeof f === 'function') f(ev); } catch (_) {}
                try { const f = globalThis[name]?.markTrusted; if (typeof f === 'function') f(ev); } catch (_) {}
              }
              for (const set of __sets) { try { __add.call(set, ev); } catch (_) {} }
              document.getElementById('i').dispatchEvent(ev);
              return JSON.stringify({ isTrusted: ev.isTrusted, seen, sets: __sets.size });
            })()
            """);
        var parsed = JsonNode.Parse(result!.GetValue<string>())!;
        Assert.False(parsed["isTrusted"]!.GetValue<bool>());
        Assert.Equal("[false]", parsed["seen"]!.ToJsonString());
    }

    /// <summary>The host still reaches every helper, in the page realm and in a frame's.</summary>
    [Fact]
    public void HostScriptStillReachesTheHelpers()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><input id=i></body></html>");
        var runtime = fixture.Runtime;
        Assert.True(runtime.EvaluateHost("__obscura_host.markTrusted(new Event('x')).isTrusted")!.GetValue<bool>());
        Assert.Equal(
            "native",
            runtime.EvaluateHost(
                "(() => { const el = document.getElementById('i'); __obscura_host.setFieldValue(el, 'value', 'native'); return el.value; })()")!
                .GetValue<string>());
        Assert.Equal(
            "function",
            runtime.EvaluateHost(
                "[typeof __obscura_host.deliverMessage, typeof __obscura_host.activateLabel, typeof __obscura_host.setInputFiles].every(t => t === 'function') ? 'function' : 'missing'")!
                .GetValue<string>());
        // Frozen: host script cannot be handed a helper a page swapped in, and a script
        // that tries to swap one fails.
        Assert.True(runtime.EvaluateHost("Object.isFrozen(__obscura_host)")!.GetValue<bool>());

        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://child.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);
        Assert.True(frame.EvaluateHost("__obscura_host.markTrusted(new Event('x')).isTrusted")!.GetValue<bool>());
        Assert.Equal("[]", frame.EvaluateHost("__obscura_host.liveFrameIds()")!.ToJsonString());
    }

    /// <summary>
    /// A frame that overwrites <c>__obscura_frameId</c> still posts as itself. Rust sends the
    /// page-writable global as the source, so a frame could claim a sibling's id and have
    /// <c>event.source</c> name the sibling's window.
    /// </summary>
    [Fact]
    public void FrameCannotSpoofItsPostMessageSource()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://child.example/f", "<html><body></body></html>");
        Assert.NotNull(frame);

        frame.ExecuteScript("globalThis.__obscura_frameId = 7; parent.postMessage('hi', '*');");
        var queued = page.Runtime.TakePendingFrameMessages();
        Assert.Single(queued);
        Assert.Equal(1u, queued[0].SourceFrameId);
    }

    /// <summary>
    /// A frame's form-state writes land in the frame's own document even when its script
    /// sets <c>__obscura_frameId</c> to the page's id.
    /// </summary>
    [Fact]
    public void FrameFormStateStaysInTheFrameDocument()
    {
        const string html = "<html><body><input id=i></body></html>";
        using var page = RuntimeFixture.Page("https://parent.example/", html);
        using var frame = FrameRealm.Create(page.Runtime, 1, 0, "https://child.example/f", html);
        Assert.NotNull(frame);

        frame.ExecuteScript("globalThis.__obscura_frameId = 0; document.getElementById('i').value = 'evil';");

        var pageValue = page.Runtime.WithDom(dom =>
            dom.TryGetDirtyFormValue(dom.QuerySelector("#i")!.Value, out var value) ? value : null);
        Assert.Null(pageValue);
        Assert.Equal("evil", frame.Evaluate("document.getElementById('i').value")!.GetValue<string>());
    }

    private static void AssertEmptyArray(JsonNode? node)
    {
        Assert.NotNull(node);
        Assert.Equal("[]", node.GetValue<string>());
    }
}
