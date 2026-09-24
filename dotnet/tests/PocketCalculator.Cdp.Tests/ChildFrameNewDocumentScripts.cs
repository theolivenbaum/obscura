using System.Text.Json.Nodes;

using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// New-document scripts and bindings in child frames' main worlds, the scope of a
/// target's init scripts, and a link followed inside a frame.
/// </summary>
/// <remarks>
/// The expectations are Chromium's, measured with Playwright 1.56 and Puppeteer 24
/// against the Chromium build Playwright ships: an init script and a
/// <c>Runtime.addBinding</c> binding run in every frame of their target, a binding call
/// from a frame reports the frame's execution context, a script registered on one target
/// does not run in another, and a click on a link inside a frame navigates that frame.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class ChildFrameNewDocumentScripts
{
    private const string Inner =
        "<html><body style='margin:0'><h1 id=t>Inner</h1>"
        + "<a id=link href='/next.html' style='display:block;width:120px;height:40px'>next</a></body></html>";

    private static CoreCdpServer Serve() => CoreCdpServer.Routed(path => path switch
    {
        "/inner.html" => (Inner, "text/html", 200),
        "/next.html" => ("<html><body><h1 id=t>Next</h1></body></html>", "text/html", 200),
        _ => ("<html><body style='margin:0'><p style='margin:0;height:50px'>outer</p>"
            + "<iframe id=f src=\"/inner.html\" width=400 height=300></iframe></body></html>", "text/html", 200),
    });

    private static async Task<string> AttachAsync(CdpContext ctx, ulong id)
    {
        CdpResponse created = await CoreCdp.DispatchAsync(
            ctx, id, "Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null);
        string targetId = created.Result!["targetId"].AsString()!;
        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx, id + 1, "Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, null);
        string session = attached.Result!["sessionId"].AsString()!;
        await CoreCdp.CdpAsync(ctx, id + 2, "Runtime.enable", new JsonObject(), session);
        return session;
    }

    /// <summary>Navigates and waits for the child frame; answers its protocol frame id.</summary>
    private static async Task<string> NavigateAsync(CdpContext ctx, string session, string url)
    {
        await CoreCdp.CdpAsync(ctx, 50, "Page.navigate", new JsonObject { ["url"] = url }, session);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await CoreCdp.CdpAsync(ctx, 51, "Runtime.evaluate", new JsonObject { ["expression"] = "1" }, session);
            JsonNode tree = await CoreCdp.CdpAsync(ctx, 52, "Page.getFrameTree", new JsonObject(), session);
            if (tree["frameTree"]?["childFrames"]?[0]?["frame"]?["id"]?.GetValue<string>() is { } child)
            {
                return child;
            }

            await Task.Delay(50);
        }

        Assert.Fail("the child frame never appeared");
        return null!;
    }

    private static long FrameMainContext(CdpContext ctx, string session, string frameId) =>
        ctx.PendingEvents.Last(e => e.Method == "Runtime.executionContextCreated"
            && e.SessionId == session
            && e.Params.Get("context").Get("auxData").Get("frameId").AsString() == frameId
            && e.Params.Get("context").Get("auxData").Get("isDefault").AsBool() == true)
            .Params.Get("context").Get("id").AsI64()!.Value;

    private static async Task<JsonNode?> EvaluateAsync(CdpContext ctx, string session, long? contextId, string expression)
    {
        var parameters = new JsonObject { ["expression"] = expression, ["returnByValue"] = true };
        if (contextId is { } id)
        {
            parameters["contextId"] = id;
        }

        JsonNode reply = await CoreCdp.CdpAsync(ctx, 60, "Runtime.evaluate", parameters, session);
        return reply["result"]?["value"];
    }

    [Fact]
    public async Task InitScriptsAndBindingsRunInChildFrames()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string session = await AttachAsync(ctx, 900);
        await CoreCdp.CdpAsync(
            ctx, 910, "Page.addScriptToEvaluateOnNewDocument",
            new JsonObject { ["source"] = "window.__init = (window.__init || '') + location.pathname;" }, session);
        await CoreCdp.CdpAsync(ctx, 911, "Runtime.addBinding", new JsonObject { ["name"] = "hostBinding" }, session);

        string child = await NavigateAsync(ctx, session, server.Url);
        long frameContext = FrameMainContext(ctx, session, child);

        Assert.Equal("/", (await EvaluateAsync(ctx, session, null, "window.__init"))?.GetValue<string>());
        Assert.Equal("/inner.html", (await EvaluateAsync(ctx, session, frameContext, "window.__init"))?.GetValue<string>());
        Assert.Equal("function", (await EvaluateAsync(ctx, session, frameContext, "typeof hostBinding"))?.GetValue<string>());

        ctx.PendingEvents.Clear();
        await EvaluateAsync(ctx, session, frameContext, "hostBinding('from-frame'), 1");
        CdpEvent call = Assert.Single(ctx.PendingEvents, e => e.Method == "Runtime.bindingCalled");
        Assert.Equal("from-frame", call.Params.Get("payload").AsString());
        Assert.Equal(frameContext, call.Params.Get("executionContextId").AsI64());
    }

    [Fact]
    public async Task InitScriptsBelongToTheTargetThatAddedThem()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string first = await AttachAsync(ctx, 900);
        string second = await AttachAsync(ctx, 920);
        await CoreCdp.CdpAsync(
            ctx, 930, "Page.addScriptToEvaluateOnNewDocument",
            new JsonObject { ["source"] = "window.__mine = 'first';" }, first);
        await CoreCdp.CdpAsync(ctx, 931, "Runtime.addBinding", new JsonObject { ["name"] = "firstOnly" }, first);

        await NavigateAsync(ctx, second, server.Url);
        Assert.Equal("undefined", (await EvaluateAsync(ctx, second, null, "typeof window.__mine"))?.GetValue<string>());
        Assert.Equal("undefined", (await EvaluateAsync(ctx, second, null, "typeof firstOnly"))?.GetValue<string>());

        await NavigateAsync(ctx, first, server.Url);
        Assert.Equal("first", (await EvaluateAsync(ctx, first, null, "window.__mine"))?.GetValue<string>());
        Assert.Equal("function", (await EvaluateAsync(ctx, first, null, "typeof firstOnly"))?.GetValue<string>());
    }

    /// <summary>
    /// A click on a link that loads a new document reports it as a navigation does: a new
    /// loader, the lifecycle and <c>loadEventFired</c>. It used to be a lone
    /// <c>frameNavigated</c> under the old loader, so Puppeteer's <c>waitForNavigation</c>
    /// and Playwright's <c>waitForURL</c> timed out after the page had moved.
    /// </summary>
    [Fact]
    public async Task AClickOnALinkReportsTheNewDocumentsLifecycle()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Routed(path => path switch
        {
            "/next.html" => ("<html><body>next</body></html>", "text/html", 200),
            _ => ("<html><body style='margin:0'><a id=link href='/next.html' "
                + "style='display:block;width:120px;height:40px'>next</a></body></html>", "text/html", 200),
        });
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string session = await AttachAsync(ctx, 900);
        await CoreCdp.CdpAsync(ctx, 80, "Page.enable", new JsonObject(), session);
        await CoreCdp.CdpAsync(ctx, 81, "Page.setLifecycleEventsEnabled", new JsonObject { ["enabled"] = true }, session);
        await CoreCdp.CdpAsync(ctx, 82, "Page.navigate", new JsonObject { ["url"] = server.Url }, session);
        string? firstLoader = ctx.PendingEvents.LastOrDefault(e => e.Method == "Page.frameNavigated")
            ?.Params.Get("frame").Get("loaderId").AsString();
        ctx.PendingEvents.Clear();

        foreach (string type in new[] { "mousePressed", "mouseReleased" })
        {
            await CoreCdp.CdpAsync(
                ctx, 83, "Input.dispatchMouseEvent",
                new JsonObject { ["type"] = type, ["x"] = 20, ["y"] = 20, ["button"] = "left", ["clickCount"] = 1 },
                session);
        }

        CdpEvent navigated = Assert.Single(ctx.PendingEvents, e => e.Method == "Page.frameNavigated");
        Assert.EndsWith("/next.html", navigated.Params.Get("frame").Get("url").AsString());
        string? loader = navigated.Params.Get("frame").Get("loaderId").AsString();
        Assert.NotEqual(firstLoader, loader);
        Assert.Contains(ctx.PendingEvents, e => e.Method == "Page.loadEventFired");
        Assert.Contains(ctx.PendingEvents, e => e.Method == "Page.lifecycleEvent"
            && e.Params.Get("name").AsString() == "load" && e.Params.Get("loaderId").AsString() == loader);
    }

    [Fact]
    public async Task ARoutedClickOnALinkNavigatesTheFrame()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string session = await AttachAsync(ctx, 900);
        string child = await NavigateAsync(ctx, session, server.Url);
        long frameContext = FrameMainContext(ctx, session, child);
        Assert.Equal("Inner", (await EvaluateAsync(ctx, session, frameContext, "document.getElementById('t').textContent"))?.GetValue<string>());

        // Past the 50px paragraph and the iframe's 2px border, then the frame's own <h1>.
        JsonNode link = await CoreCdp.CdpAsync(
            ctx, 70, "Runtime.evaluate",
            new JsonObject { ["expression"] = "document.getElementById('link')", ["contextId"] = frameContext }, session);
        JsonNode quads = await CoreCdp.CdpAsync(
            ctx, 71, "DOM.getContentQuads", new JsonObject { ["objectId"] = link["result"]!["objectId"]!.GetValue<string>() }, session);
        double x = quads["quads"]![0]![0].AsF64()!.Value + 10;
        double y = quads["quads"]![0]![1].AsF64()!.Value + 10;
        foreach (string type in new[] { "mousePressed", "mouseReleased" })
        {
            await CoreCdp.CdpAsync(
                ctx, 72, "Input.dispatchMouseEvent",
                new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 },
                session);
        }

        string? url = null;
        for (int attempt = 0; attempt < 40 && url?.EndsWith("/next.html", StringComparison.Ordinal) != true; attempt++)
        {
            // What the connection's autonomous pump does between commands.
            await ctx.GetSessionPageMut(session)!.RunAutonomousEventLoopTurnAsync();
            JsonNode tree = await CoreCdp.CdpAsync(ctx, 74, "Page.getFrameTree", new JsonObject(), session);
            url = tree["frameTree"]?["childFrames"]?[0]?["frame"]?["url"]?.GetValue<string>();
            if (url?.EndsWith("/next.html", StringComparison.Ordinal) != true)
            {
                await Task.Delay(50);
            }
        }

        Assert.EndsWith("/next.html", url);
        // The page itself stayed where it was.
        Assert.Equal("outer", (await EvaluateAsync(ctx, session, null, "document.querySelector('p').textContent"))?.GetValue<string>());
    }
}
