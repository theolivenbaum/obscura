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
}
