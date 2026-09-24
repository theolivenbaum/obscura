using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md I10: what a page can learn about the engine by enumerating its global object.
/// Chromium's global has no <c>_*</c> or <c>*obscura*</c> names at all; the only engine name
/// left here is ClearScript's non-configurable <c>EngineInternal</c> (see
/// <see cref="EngineInternalHidden"/>).
/// </summary>
/// <remarks>
/// The probe enumerates through a Proxy, whose default ownKeys trap reaches the global's
/// real keys past the reflection filter bootstrap.js installs on
/// <c>Object.getOwnPropertyNames</c> / <c>Reflect.ownKeys</c>, so the filter cannot hide a
/// leak from this test. It also walks the global's prototype chain.
/// </remarks>
public sealed class GlobalObjectFingerprint
{
    /// <summary>Every engine-looking name on the global or its prototypes, comma-separated.</summary>
    private const string Probe = """
        (() => {
          // The test assembly's own seams (TestOpsExposure), not the engine's.
          delete globalThis.__obscura_test_ops;
          delete globalThis.__obscura_test_host;
          const engine = (k) => typeof k === 'string'
            && (k.startsWith('_') || /obscura/i.test(k) || k === 'EngineInternal' || k === 'Deno');
          const found = [];
          let target = globalThis;
          for (let depth = 0; target && depth < 8; depth++) {
            for (const k of Reflect.ownKeys(new Proxy(target, {}))) {
              if (engine(k) && !(depth > 0 && target === Object.prototype && k.startsWith('__'))) {
                found.push((depth ? depth + ':' : '') + k);
              }
            }
            target = Object.getPrototypeOf(target);
          }
          return found.sort().join(',');
        })()
        """;

    /// <summary>
    /// The names upstream's shim leaves on the global; each is looked up with <c>in</c>,
    /// which no reflection filter can intercept.
    /// </summary>
    private const string UpstreamNames = """
        (() => [
          '__obscura_init', '__obscura_hide_list', '__obscura_binding_called', '__obscura_ua',
          '__obscura_platform', '__obscura_ua_platform', '__obscura_ua_platform_version',
          '__obscura_stealth', '__obscura_frameId', '__obscura_parentFrameId', '__obscura_hw',
          '__obscura_mem', '__obscura_errors', '__obscura_viewport_w', '__obscura_viewport_h',
          '__obscura_screen_w', '__obscura_screen_h', '__obscura_screen_emulated',
          '__obscura_recompute_intersections', '__obscura_recompute_resizes',
          '__obscura_hasPendingDynamicScripts', '__obscura_hasPendingLoadDelayingScripts',
          '__obscura_nextPendingTimeoutDelay', '__obscura_clone_hooks', '__obscura_shadowHostNames',
          '__documentReadyState__', '__currentScriptNid', '__currentUrl', '__markParserScripts',
          '__notifyMutation', '__mutationObservers', '__intersectionObservers', '__resizeObservers',
          '__windowListeners', '__blobStore', '__fetchInterceptEnabled', '__fetchInterceptCallback',
          '__ariaQuerySelector', '__ariaQuerySelectorAll', '__processDynScriptQueue',
          '_formValues', '_formChecked', '_formIndeterminate', '_eventRegistry', '_markNative',
          '_rawFragment', '_wrap', '_domParse', '_nativeStr', '__obscura_host', '__obscura_host_handoff',
          '__obscura_core_handoff', '__obscura_deno_core', 'Deno',
        ].filter((name) => name in globalThis).join(','))()
        """;

    [Fact]
    public void ThePageGlobalCarriesNoEngineNamesButEngineInternal()
    {
        using var fixture = RuntimeFixture.Setup("<html><body><p>x</p><script>var pageVar = 1;</script></body></html>");
        Assert.Equal("EngineInternal", fixture.Runtime.Evaluate(Probe)!.GetValue<string>());
        Assert.Equal(string.Empty, fixture.Runtime.Evaluate(UpstreamNames)!.GetValue<string>());
    }

    [Fact]
    public void AFrameRealmCarriesNoEngineNamesButEngineInternal()
    {
        using var page = RuntimeFixture.Page("https://parent.example/", "<html><body></body></html>");
        using var frame = FrameRealm.Create(
            page.Runtime, 1, 0, "https://child.example/f", "<html><body><p>frame</p></body></html>");
        Assert.NotNull(frame);
        Assert.Equal("EngineInternal", frame.Evaluate(Probe)!.GetValue<string>());
        Assert.Equal(string.Empty, frame.Evaluate(UpstreamNames)!.GetValue<string>());
    }

    [Fact]
    public void AnInstalledBindingAddsOnlyItsOwnName()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        runtime.ExecutePreloadScript(BindingPreload.Source("probeBinding"));
        Assert.Equal("EngineInternal", runtime.Evaluate(Probe)!.GetValue<string>());
        Assert.Equal(
            "function,function () { [native code] },1",
            runtime.Evaluate(
                "[typeof probeBinding, String(probeBinding), (probeBinding('a'), probeBinding(), probeBinding(1, 2), 1)].join(',')")!
                .GetValue<string>());
        Assert.Equal(new[] { ("probeBinding", "a") }, runtime.TakePendingBindingCalls());
    }

    /// <summary>
    /// The values the host sets still reach the page's surfaces, through the host
    /// variables instead of globals.
    /// </summary>
    [Fact]
    public void HostVariablesStillDriveThePageSurfaces()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var runtime = fixture.Runtime;
        runtime.SetUserAgent("Mozilla/5.0 (probe) Chrome/140.0.0.0");
        runtime.SetPlatform("ProbeOS", "Probe", "1.2.3");
        runtime.SetDocumentReadyState("interactive");
        Assert.Equal(
            "Mozilla/5.0 (probe) Chrome/140.0.0.0|ProbeOS|interactive",
            runtime.Evaluate("[navigator.userAgent, navigator.platform, document.readyState].join('|')")!
                .GetValue<string>());
        // A page writing the upstream global names changes nothing the shim reads.
        runtime.ExecuteScript(
            "tamper",
            "globalThis.__obscura_ua = 'evil'; globalThis.__obscura_platform = 'evil';"
            + " globalThis.__documentReadyState__ = 'loading';");
        Assert.Equal(
            "Mozilla/5.0 (probe) Chrome/140.0.0.0|ProbeOS|interactive",
            runtime.Evaluate("[navigator.userAgent, navigator.platform, document.readyState].join('|')")!
                .GetValue<string>());
    }
}
