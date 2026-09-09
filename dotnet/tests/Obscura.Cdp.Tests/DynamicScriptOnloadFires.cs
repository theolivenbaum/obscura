using System.Diagnostics;
using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/dynamic_script_onload_fires.rs</c>.
/// </summary>
/// <remarks>
/// Regression for issue #474: external scripts inserted after a timer must be fetched and
/// execute before an explicit post-navigation settle completes.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class DynamicScriptOnloadFires
{
    private const string TimerDocument = """
        <html><body>
        <div id="r">stage1</div>
        <script>
        setTimeout(function () {
          var direct = document.createElement("script");
          direct.src = "/direct.js";
          direct.onload = function () { window.__directLoaded = true; };
          document.body.appendChild(direct);

          var box = document.createElement("div");
          var nested = document.createElement("script");
          nested.src = "/nested.js";
          nested.onload = function () { window.__nestedLoaded = true; };
          box.appendChild(nested);
          document.body.appendChild(box);
        }, 100);
        </script>
        </body></html>
        """;

    private const string ProbeExpression =
        "JSON.stringify({directExecuted: !!window.__directExecuted, "
        + "directLoaded: !!window.__directLoaded, "
        + "nestedExecuted: !!window.__nestedExecuted, "
        + "nestedLoaded: !!window.__nestedLoaded})";

    private static CoreCdpServer Serve() => CoreCdpServer.Advanced(path =>
        path.StartsWith("/direct.js", StringComparison.Ordinal)
            ? new CoreCdpServer.Reply(
                "window.__directExecuted = true;", "application/javascript", 200, null, 600)
            : path.StartsWith("/nested.js", StringComparison.Ordinal)
                ? new CoreCdpServer.Reply(
                    "window.__nestedExecuted = true;", "application/javascript", 200, null, 50)
                : new CoreCdpServer.Reply(TimerDocument, "text/html", 200));

    private static CoreCdpServer ServeDynamicOrderFixture(bool explicitlyInOrder)
    {
        string ordered = explicitlyInOrder ? "slow.async=false;fast.async=false;" : string.Empty;
        string document = $$"""
            <script>
            window.__dynamicOrder=[];
            var slow=document.createElement('script');
            var fast=document.createElement('script');
            {{ordered}}
            slow.src='/slow.js';
            fast.src='/fast.js';
            document.head.appendChild(slow);
            document.head.appendChild(fast);
            </script>
            """;
        return CoreCdpServer.Advanced(path =>
            path.StartsWith("/slow.js", StringComparison.Ordinal)
                ? new CoreCdpServer.Reply(
                    "window.__dynamicOrder.push('slow');", "application/javascript", 200, null, 700)
                : path.StartsWith("/fast.js", StringComparison.Ordinal)
                    ? new CoreCdpServer.Reply(
                        "window.__dynamicOrder.push('fast');", "application/javascript", 200, null, 500)
                    : new CoreCdpServer.Reply(document, "text/html", 200));
    }

    private static async Task<(List<string> Order, int Peak, long ElapsedMs)>
        NavigateDynamicOrderFixtureAsync(bool explicitlyInOrder)
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = ServeDynamicOrderFixture(explicitlyInOrder);
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "script-order-session";
        ctx.Sessions[sessionId] = pageId;

        long started = Stopwatch.GetTimestamp();
        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" },
            sessionId);
        var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        JsonNode result = await CoreCdp.EvalAsync(ctx, 2, "window.__dynamicOrder", sessionId);
        List<string> order = [.. JsonExt.AsJsonArray(result.Get("result").Get("value"))!
            .Select(entry => entry.AsString()!)];
        return (order, server.PeakConcurrency, elapsedMs);
    }

    [Fact]
    public async Task DynamicClassicFetchConcurrencyMatchesForceAsyncState()
    {
        (List<string> asyncOrder, int asyncPeak, long asyncElapsedMs) =
            await NavigateDynamicOrderFixtureAsync(false);
        Assert.Equal(["fast", "slow"], asyncOrder);
        Assert.Equal(2, asyncPeak);

        (List<string> orderedOrder, int orderedPeak, long orderedElapsedMs) =
            await NavigateDynamicOrderFixtureAsync(true);
        Assert.Equal(["slow", "fast"], orderedOrder);
        Assert.Equal(2, orderedPeak);
        Assert.True(
            orderedElapsedMs < asyncElapsedMs + 300,
            $"execution ordering must not serialize network fetches: async={asyncElapsedMs}ms ordered={orderedElapsedMs}ms");
    }

    [Fact]
    public async Task DynamicExternalScriptsExecuteAndFireLoad()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;

        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" },
            sessionId);

        JsonNode atLoad = await CoreCdp.EvalAsync(ctx, 2, ProbeExpression, sessionId);
        Assert.Equal(
            """{"directExecuted":false,"directLoaded":false,"nestedExecuted":false,"nestedLoaded":false}""",
            atLoad.Get("result").Get("value").AsString());

        // The automation caller opts into post-load work. This must drive the timer, both
        // concurrently fetched scripts, their bodies, and their load handlers without
        // relying on navigation to exceed browser load semantics.
        await ctx.Pages[0].SettleAsync(1_500);

        JsonNode settled = await CoreCdp.EvalAsync(ctx, 3, ProbeExpression, sessionId);
        Assert.Equal(
            """{"directExecuted":true,"directLoaded":true,"nestedExecuted":true,"nestedLoaded":true}""",
            settled.Get("result").Get("value").AsString());
    }

    [Fact]
    public async Task DynamicDataScriptsExecuteBeforeChainedLoadHandlers()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;

        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<html><head></head><body></body></html>",
                ["waitUntil"] = "load",
            },
            sessionId);

        await CoreCdp.EvalAsync(
            ctx,
            2,
            """
            (function () {
                var state = {
                    aExec: false,
                    aLoad: false,
                    bExec: false,
                    bLoad: false,
                    cExec: false,
                    cLoad: false
                };
                window.__dataScriptState = state;

                var a = document.createElement('script');
                a.src = 'data:,window.__dataScriptState.aExec=true';
                a.onload = function () {
                    state.aLoad = true;
                    var b = document.createElement('script');
                    b.src = 'data:text/html,' + encodeURIComponent('window.__dataScriptState.bExec=true');
                    b.onload = function () {
                        state.bLoad = true;
                        var c = document.createElement('script');
                        c.src = 'data:text/javascript;base64,' +
                            btoa('window.__dataScriptState.cExec=true').replace(/=+$/, '') +
                            '#ignored-fragment';
                        c.onload = function () { state.cLoad = true; };
                        document.head.appendChild(c);
                    };
                    document.head.appendChild(b);
                };
                document.head.appendChild(a);
                return 'kicked';
            })()
            """,
            sessionId);

        await ctx.Pages[0].SettleAsync(500);

        JsonNode result = await CoreCdp.EvalAsync(
            ctx, 3, "JSON.stringify(window.__dataScriptState)", sessionId);
        Assert.Equal(
            """{"aExec":true,"aLoad":true,"bExec":true,"bLoad":true,"cExec":true,"cLoad":true}""",
            result.Get("result").Get("value").AsString());
    }

    [Fact]
    public async Task InvalidDynamicDataScriptFiresErrorNotLoad()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;

        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<html><head></head><body></body></html>",
                ["waitUntil"] = "load",
            },
            sessionId);

        await CoreCdp.CdpAsync(
            ctx,
            2,
            "Runtime.callFunctionOn",
            new JsonObject
            {
                ["functionDeclaration"] = """
                    function () {
                        window.__invalidDataScript = { error: false, load: false };
                        var script = document.createElement('script');
                        script.src = 'data:text/javascript;base64,!';
                        script.onerror = function () { window.__invalidDataScript.error = true; };
                        script.onload = function () { window.__invalidDataScript.load = true; };
                        document.head.appendChild(script);
                    }
                    """,
                ["awaitPromise"] = true,
            },
            sessionId);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        JsonNode result = await CoreCdp.EvalAsync(
            ctx, 3, "JSON.stringify(window.__invalidDataScript)", sessionId);
        Assert.Equal(
            """{"error":true,"load":false}""",
            result.Get("result").Get("value").AsString());
    }
}
