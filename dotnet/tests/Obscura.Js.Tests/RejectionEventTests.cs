using System.Text.Json.Nodes;
using Obscura.Js.Runtime;
using Xunit;

namespace Obscura.Js.Tests;

/// <summary>
/// The <c>unhandledrejection</c> / <c>rejectionhandled</c> path.
/// </summary>
/// <remarks>
/// bootstrap.js registers handlers through
/// <c>Deno.core.setUnhandledPromiseRejectionHandler</c> and builds a real
/// <c>PromiseRejectionEvent</c> from them. Storing the callback is not enough:
/// nothing in V8 invokes it unless the engine's promise-rejection callback is
/// wired to it, and for a while in this port it was not, so
/// <c>window.onunhandledrejection</c> never fired at all.
/// </remarks>
public sealed class RejectionEventTests
{
    /// <summary>Evaluate returns a JSON value; unwrap it as a number.</summary>
    private static double Num(object? value) => ((JsonNode)value!).GetValue<double>();

    [Fact]
    public void An_unhandled_rejection_reaches_onunhandledrejection()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.ExecuteScript("setup", """
            globalThis.__seen = [];
            globalThis.onunhandledrejection = (event) => {
                globalThis.__seen.push(String(event.reason));
                event.preventDefault();
            };
            Promise.reject(new Error("boom"));
            """);

        // The callback fires on the microtask checkpoint, which the pump drives.
        runtime.RunEventLoopBoundedAsync(200).GetAwaiter().GetResult();

        var seen = runtime.Evaluate("globalThis.__seen.join('|')")?.ToString() ?? string.Empty;
        Assert.Contains("boom", seen, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unhandled_rejection_does_not_stop_the_event_loop()
    {
        // Browsers report an unhandled rejection without tearing down the page.
        using var runtime = new ObscuraJsRuntime();
        runtime.ExecuteScript("setup", """
            globalThis.__ticks = 0;
            Promise.reject(new Error("ignored"));
            setTimeout(() => { globalThis.__ticks++; }, 0);
            """);

        runtime.RunEventLoopBoundedAsync(500).GetAwaiter().GetResult();

        Assert.Equal(1d, Num(runtime.Evaluate("globalThis.__ticks")));
        Assert.Equal(2d, Num(runtime.Evaluate("1 + 1")));
    }

    [Fact]
    public void A_handled_rejection_does_not_report_as_unhandled()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.ExecuteScript("setup", """
            globalThis.__unhandled = 0;
            globalThis.onunhandledrejection = () => { globalThis.__unhandled++; };
            Promise.reject(new Error("caught")).catch(() => {});
            """);

        runtime.RunEventLoopBoundedAsync(200).GetAwaiter().GetResult();

        Assert.Equal(0d, Num(runtime.Evaluate("globalThis.__unhandled")));
    }

    /// <summary>
    /// A page terminated by the watchdog while it is producing unhandled
    /// rejections must still come back under host control.
    /// </summary>
    /// <remarks>
    /// V8 calls the promise-rejection hook on its own frame, and that hook
    /// re-enters script to deliver the report. Once the isolate is terminating,
    /// every such re-entry raises <c>ScriptInterruptedException</c>. Letting it
    /// out of the callback unwinds into ClearScript's thunk, which does not
    /// contain it the way the host-object thunks do: before this was fixed, this
    /// exact script left the test host wedged, and the same boundary killed an
    /// <c>obscura serve</c> process during a route survey with an unhandled
    /// <c>ObjectDisposedException</c> reported against
    /// <c>V8SplitProxyManaged.InvokePromiseRejectionCallback</c>.
    /// </remarks>
    [Fact]
    public void Terminating_a_page_that_is_producing_rejections_stays_contained()
    {
        var runtime = new ObscuraJsRuntime();
        var token = runtime.ArmWatchdog(TimeSpan.FromMilliseconds(200));
        Assert.Throws<JsRuntimeException>(() => runtime.ExecuteScript("spin", """
            globalThis.__keep = [];
            let n = 0;
            while (true) {
                n++;
                if ((n % 1000) === 0) { globalThis.__keep.push(Promise.reject(new Error("leak"))); }
                if (globalThis.__keep.length > 2000) { globalThis.__keep.length = 0; }
            }
            """));

        Assert.True(runtime.DisarmWatchdog(token));

        // Disposing is the other half of the same boundary: while V8's
        // promise-reject hook is registered it can be called with the engine
        // half torn down.
        runtime.Dispose();
    }

    /// <summary>
    /// Rejection reporting is suspended while the isolate is terminating, and
    /// clearing the termination turns it back on.
    /// </summary>
    /// <remarks>
    /// Suspending is what stops a terminated page re-entering script once per
    /// rejection, of which there can be thousands. It must not be permanent, or
    /// a page that survives one watchdog fire silently stops reporting
    /// <c>unhandledrejection</c> for the rest of its life.
    /// </remarks>
    [Fact]
    public void Rejection_reporting_resumes_after_the_termination_is_cleared()
    {
        using var runtime = new ObscuraJsRuntime();
        runtime.ExecuteScript("setup", """
            globalThis.__seen = [];
            globalThis.onunhandledrejection = (event) => {
                globalThis.__seen.push(String(event.reason));
                event.preventDefault();
            };
            """);

        var token = runtime.ArmWatchdog(TimeSpan.FromMilliseconds(200));
        Assert.Throws<JsRuntimeException>(() => runtime.ExecuteScript("spin", """
            let n = 0;
            while (true) {
                n++;
                if ((n % 1000) === 0) { Promise.reject(new Error("during")); }
            }
            """));
        Assert.True(runtime.DisarmWatchdog(token));

        runtime.ExecuteScript("after", """Promise.reject(new Error("after"));""");
        runtime.RunEventLoopBoundedAsync(500).GetAwaiter().GetResult();

        var seen = runtime.Evaluate("globalThis.__seen.join('|')")?.ToString() ?? string.Empty;
        Assert.Contains("after", seen, StringComparison.Ordinal);
    }
}
