using System.Text.Json.Nodes;

using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// A child frame's CDP execution contexts: its own realm as the default context and a
/// world of its own per isolated-world name (SECURITY.md M6).
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, which announces child frames but gives them no
/// execution context, so a command addressed to a frame's context ran in the page realm
/// against the page's document. The expectations are Chromium's, measured with the
/// Chromium build Playwright ships: <c>executionContextCreated</c> for the frame's main
/// world and for each <c>addScriptToEvaluateOnNewDocument</c> world name, with the child's
/// <c>frameId</c> in <c>auxData</c>; <c>DOM.describeNode</c> of the iframe reports that
/// <c>frameId</c>; <c>DOM.getFrameOwner</c> answers the iframe; input at a point inside
/// the iframe reaches the frame's document.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class ChildFrameContexts
{
    private const string Inner =
        "<html><body style='margin:0'><h1 id=t>Inner</h1>"
        + "<button id=btn style='display:block;width:120px;height:40px' "
        + "onclick=\"document.getElementById('out').textContent='clicked:' + event.isTrusted\">Go</button>"
        + "<input id=name value=pre><div id=out></div>"
        + "<script>window.frameSecret = 'frame'; document.querySelector = () => null;"
        + "Array.prototype.map = function () { return ['tampered']; };</script></body></html>";

    private static CoreCdpServer Serve() => CoreCdpServer.Routed(path => path switch
    {
        "/inner.html" => (Inner, "text/html", 200),
        _ => ("<html><body style='margin:0'><p style='margin:0;height:50px'>outer</p>"
            + "<iframe id=f src=\"/inner.html\" width=400 height=300></iframe></body></html>", "text/html", 200),
    });

    private sealed record Opened(CdpContext Ctx, string Session, string MainFrameId, string ChildFrameId);

    private static async Task<Opened> OpenAsync(CdpContext ctx, string url)
    {
        CdpResponse created = await CoreCdp.DispatchAsync(
            ctx, 900, "Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null);
        string targetId = created.Result!["targetId"].AsString()!;
        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx, 901, "Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, null);
        string session = attached.Result!["sessionId"].AsString()!;
        await CoreCdp.CdpAsync(ctx, 902, "Runtime.enable", new JsonObject(), session);
        // What Playwright does before it navigates: a utility world every new document gets.
        await CoreCdp.CdpAsync(
            ctx, 903, "Page.addScriptToEvaluateOnNewDocument",
            new JsonObject { ["source"] = string.Empty, ["worldName"] = "utility" }, session);
        await CoreCdp.CdpAsync(ctx, 904, "Page.navigate", new JsonObject { ["url"] = url }, session);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            await CoreCdp.CdpAsync(ctx, 905, "Runtime.evaluate", new JsonObject { ["expression"] = "1" }, session);
            JsonNode tree = await CoreCdp.CdpAsync(ctx, 906, "Page.getFrameTree", new JsonObject(), session);
            if (tree["frameTree"]?["childFrames"]?[0]?["frame"]?["id"]?.GetValue<string>() is { } child)
            {
                return new Opened(ctx, session, tree["frameTree"]!["frame"]!["id"]!.GetValue<string>(), child);
            }

            await Task.Delay(50);
        }

        Assert.Fail("the child frame never appeared");
        return null!;
    }

    private static CdpEvent ContextCreated(Opened page, bool isDefault, string name) =>
        page.Ctx.PendingEvents.Single(e => e.Method == "Runtime.executionContextCreated"
            && e.SessionId == page.Session
            && e.Params.Get("context").Get("auxData").Get("frameId").AsString() == page.ChildFrameId
            && e.Params.Get("context").Get("auxData").Get("isDefault").AsBool() == isDefault
            && e.Params.Get("context").Get("name").AsString() == name);

    private static async Task<JsonNode?> EvaluateAsync(Opened page, long contextId, string expression)
    {
        JsonNode reply = await CoreCdp.CdpAsync(
            page.Ctx, 20, "Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["contextId"] = contextId, ["returnByValue"] = true },
            page.Session);
        return reply["result"]?["value"];
    }

    [Fact]
    public async Task ChildFramesGetAMainWorldAndTheirIsolatedWorlds()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Opened page = await OpenAsync(ctx, server.Url);

        CdpEvent main = ContextCreated(page, isDefault: true, name: string.Empty);
        CdpEvent utility = ContextCreated(page, isDefault: false, name: "utility");
        Assert.Equal("default", main.Params.Get("context").Get("auxData").Get("type").AsString());
        Assert.Equal("isolated", utility.Params.Get("context").Get("auxData").Get("type").AsString());
        long mainId = main.Params.Get("context").Get("id").AsI64()!.Value;
        long utilityId = utility.Params.Get("context").Get("id").AsI64()!.Value;

        // The frame's main world is the frame's realm, tampering and all.
        Assert.Equal("frame", (await EvaluateAsync(page, mainId, "window.frameSecret"))?.GetValue<string>());
        Assert.Equal("Inner", (await EvaluateAsync(page, mainId, "document.getElementById('t').textContent"))?.GetValue<string>());
        // The utility world is a realm of its own over the frame's document.
        Assert.Equal("undefined", (await EvaluateAsync(page, utilityId, "typeof window.frameSecret"))?.GetValue<string>());
        Assert.Equal("Inner", (await EvaluateAsync(page, utilityId, "document.querySelector('#t').textContent"))?.GetValue<string>());
        Assert.Equal("h1", (await EvaluateAsync(page, utilityId, "[...document.querySelectorAll('h1')].map(e => e.localName).join()"))?.GetValue<string>());
        // Neither is the page.
        JsonNode pageReply = await CoreCdp.EvalAsync(ctx, 21, "typeof window.frameSecret", page.Session);
        Assert.Equal("undefined", pageReply["result"]?["value"]?.GetValue<string>());

        // Page.createIsolatedWorld on the child answers the world it already has.
        JsonNode again = await CoreCdp.CdpAsync(
            ctx, 22, "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = page.ChildFrameId, ["worldName"] = "utility" }, page.Session);
        Assert.Equal(utilityId, again["executionContextId"].AsI64());
        JsonNode other = await CoreCdp.CdpAsync(
            ctx, 23, "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = page.ChildFrameId, ["worldName"] = "other" }, page.Session);
        long otherId = other["executionContextId"].AsI64()!.Value;
        Assert.NotEqual(utilityId, otherId);
        await EvaluateAsync(page, otherId, "window.mark = 'other'; 1");
        Assert.Equal("undefined", (await EvaluateAsync(page, utilityId, "typeof window.mark"))?.GetValue<string>());
    }

    [Fact]
    public async Task FrameObjectsResolveInTheirOwnDocument()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Opened page = await OpenAsync(ctx, server.Url);
        long utilityId = ContextCreated(page, isDefault: false, name: "utility").Params.Get("context").Get("id").AsI64()!.Value;

        // contentFrame(): describeNode of the iframe names the child frame.
        JsonNode iframe = await CoreCdp.CdpAsync(
            ctx, 30, "Runtime.evaluate", new JsonObject { ["expression"] = "document.getElementById('f')" }, page.Session);
        string iframeObject = iframe["result"]!["objectId"]!.GetValue<string>();
        JsonNode described = await CoreCdp.CdpAsync(
            ctx, 31, "DOM.describeNode", new JsonObject { ["objectId"] = iframeObject }, page.Session);
        Assert.Equal(page.ChildFrameId, described["node"]?["frameId"]?.GetValue<string>());
        ulong iframeNode = described["node"]!["backendNodeId"]!.GetValue<ulong>();

        // frame.frameElement(): getFrameOwner answers that iframe.
        JsonNode ownerReply = await CoreCdp.CdpAsync(
            ctx, 32, "DOM.getFrameOwner", new JsonObject { ["frameId"] = page.ChildFrameId }, page.Session);
        Assert.Equal(iframeNode, ownerReply["backendNodeId"]?.GetValue<ulong>());

        // A node handle from the frame's world describes the frame's node.
        JsonNode button = await CoreCdp.CdpAsync(
            ctx, 33, "Runtime.evaluate",
            new JsonObject { ["expression"] = "document.getElementById('btn')", ["contextId"] = utilityId }, page.Session);
        string buttonObject = button["result"]!["objectId"]!.GetValue<string>();
        JsonNode buttonNode = await CoreCdp.CdpAsync(
            ctx, 34, "DOM.describeNode", new JsonObject { ["objectId"] = buttonObject }, page.Session);
        Assert.Equal("button", buttonNode["node"]?["localName"]?.GetValue<string>());
        Assert.Equal("btn", buttonNode["node"]?["attributes"]?[1]?.GetValue<string>());

        // Its quads are in the page's viewport: past the 50px paragraph and the iframe's border.
        JsonNode quads = await CoreCdp.CdpAsync(
            ctx, 35, "DOM.getContentQuads", new JsonObject { ["objectId"] = buttonObject }, page.Session);
        JsonNode quad = quads["quads"]![0]!;
        double left = quad[0].AsF64()!.Value;
        double top = quad[1].AsF64()!.Value;
        Assert.True(left >= 2 && left < 20, $"left {left}");
        Assert.True(top > 50, $"top {top}");

        // A trusted click at that point runs the frame's handler.
        double x = left + 10;
        double y = top + 10;
        foreach (string type in new[] { "mousePressed", "mouseReleased" })
        {
            await CoreCdp.CdpAsync(
                ctx, 36, "Input.dispatchMouseEvent",
                new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = "left", ["clickCount"] = 1 },
                page.Session);
        }

        Assert.Equal("clicked:true", (await EvaluateAsync(page, utilityId, "document.getElementById('out').textContent"))?.GetValue<string>());

        // resolveNode into the frame's world, by the frame's own node id.
        JsonNode resolved = await CoreCdp.CdpAsync(
            ctx, 37, "DOM.resolveNode",
            new JsonObject { ["backendNodeId"] = buttonNode["node"]!["backendNodeId"]!.GetValue<ulong>(), ["executionContextId"] = utilityId },
            page.Session);
        JsonNode text = await CoreCdp.CdpAsync(
            ctx, 38, "Runtime.callFunctionOn",
            new JsonObject
            {
                ["objectId"] = resolved["object"]!["objectId"]!.GetValue<string>(),
                ["functionDeclaration"] = "function () { return this.id; }",
                ["returnByValue"] = true,
            },
            page.Session);
        Assert.Equal("btn", text["result"]?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task KeyboardInputGoesToTheFocusedFrame()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Opened page = await OpenAsync(ctx, server.Url);
        long utilityId = ContextCreated(page, isDefault: false, name: "utility").Params.Get("context").Get("id").AsI64()!.Value;

        // Playwright's fill(): focus and select in the frame's utility world, then insertText.
        await EvaluateAsync(page, utilityId, "const i = document.getElementById('name'); i.focus(); i.select(); 1");
        await CoreCdp.CdpAsync(ctx, 40, "Input.insertText", new JsonObject { ["text"] = "typed" }, page.Session);
        Assert.Equal("typed", (await EvaluateAsync(page, utilityId, "document.getElementById('name').value"))?.GetValue<string>());
    }
}
