using System.Text.Json.Nodes;

using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// CDP isolated worlds are realms of their own over the page's document (SECURITY.md M6).
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, whose <c>Page.createIsolatedWorld</c> only records a
/// context and runs everything addressed to it in the page realm. The expectations here are
/// Chromium's, measured against the Chromium build Playwright ships: a separate global
/// object and built-ins, the same DOM nodes, object ids that belong to one world, and
/// <c>DOM.resolveNode</c> with an <c>executionContextId</c> to move a node between worlds.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class IsolatedWorlds
{
    /// <summary>What a hostile page does to the built-ins automation scripts reach for.</summary>
    private const string TamperingPage =
        "data:text/html,<p id=a>real</p><p id=b>two</p><script>"
        + "window.pageSecret = 'page';"
        + "document.querySelector = () => null;"
        + "Array.prototype.map = function () { return ['tampered']; };"
        + "Promise.prototype.then = function () { return new Promise(() => {}); };"
        + "JSON.stringify = () => '\"tampered\"';"
        + "</script>";

    private sealed record Attached(CdpContext Ctx, string Session, string FrameId);

    private static async Task<Attached> OpenAsync(CdpContext ctx, string url)
    {
        CdpResponse created = await CoreCdp.DispatchAsync(
            ctx, 1, "Target.createTarget", new JsonObject { ["url"] = url }, null);
        Assert.True(created.Error is null, $"createTarget failed: {created.Error?.Message}");
        string target = created.Result!["targetId"].AsString()!;
        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx, 2, "Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true }, null);
        string session = attached.Result!["sessionId"].AsString()!;
        await CoreCdp.CdpAsync(ctx, 3, "Runtime.enable", new JsonObject(), session);
        JsonNode tree = await CoreCdp.CdpAsync(ctx, 4, "Page.getFrameTree", new JsonObject(), session);
        return new Attached(ctx, session, tree["frameTree"]!["frame"]!["id"].AsString()!);
    }

    private static async Task<long> CreateWorldAsync(Attached page, string name)
    {
        JsonNode created = await CoreCdp.CdpAsync(
            page.Ctx, 10, "Page.createIsolatedWorld",
            new JsonObject { ["frameId"] = page.FrameId, ["worldName"] = name, ["grantUniveralAccess"] = true },
            page.Session);
        return created["executionContextId"].AsI64()!.Value;
    }

    private static async Task<JsonNode> EvaluateAsync(Attached page, string expression, long? contextId = null, bool byValue = true)
    {
        var parameters = new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = byValue,
            ["awaitPromise"] = true,
        };
        if (contextId is { } id)
        {
            parameters["contextId"] = id;
        }

        return await CoreCdp.CdpAsync(page.Ctx, 20, "Runtime.evaluate", parameters, page.Session);
    }

    private static JsonNode? Value(JsonNode reply) => reply["result"]!["value"];

    [Fact]
    public async Task CreateIsolatedWorldAnnouncesAnIsolatedContext()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached page = await OpenAsync(ctx, "data:text/html,<p>x</p>");
        ctx.PendingEvents.Clear();

        long world = await CreateWorldAsync(page, "__puppeteer_utility_world__");
        CdpEvent created = Assert.Single(ctx.PendingEvents, e => e.Method == "Runtime.executionContextCreated");
        JsonNode? context = created.Params.Get("context");
        Assert.Equal(world, context.Get("id").AsI64());
        Assert.Equal("__puppeteer_utility_world__", context.Get("name").AsString());
        Assert.False(string.IsNullOrEmpty(context.Get("uniqueId").AsString()));
        Assert.False(context.Get("auxData").Get("isDefault").AsBool());
        Assert.Equal("isolated", context.Get("auxData").Get("type").AsString());
        Assert.Equal(page.FrameId, context.Get("auxData").Get("frameId").AsString());

        // Asking again for the same name answers the same world, without a new event.
        ctx.PendingEvents.Clear();
        Assert.Equal(world, await CreateWorldAsync(page, "__puppeteer_utility_world__"));
        Assert.DoesNotContain(ctx.PendingEvents, e => e.Method == "Runtime.executionContextCreated");
    }

    [Fact]
    public async Task WorldHasItsOwnGlobalsOverTheSameDocument()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached page = await OpenAsync(ctx, "data:text/html,<p id=a>x</p><script>window.pageSecret='page'</script>");
        long world = await CreateWorldAsync(page, "utility");

        Assert.Equal("undefined", Value(await EvaluateAsync(page, "typeof pageSecret", world)).AsString());
        Assert.Equal("page", Value(await EvaluateAsync(page, "pageSecret")).AsString());
        await EvaluateAsync(page, "globalThis.worldSecret = 'world'; document.getElementById('a').textContent = 'from world'", world);
        Assert.Equal("undefined", Value(await EvaluateAsync(page, "typeof worldSecret")).AsString());
        Assert.Equal("from world", Value(await EvaluateAsync(page, "document.getElementById('a').textContent")).AsString());

        // uniqueContextId names the same world.
        string uniqueId = ctx.ContextById(world)!.UniqueId;
        JsonNode byUnique = await CoreCdp.CdpAsync(
            ctx, 30, "Runtime.evaluate",
            new JsonObject { ["expression"] = "worldSecret", ["returnByValue"] = true, ["uniqueContextId"] = uniqueId },
            page.Session);
        Assert.Equal("world", Value(byUnique).AsString());
    }

    /// <summary>
    /// The attack M6 describes: page script replaces what the utility script relies on. In
    /// the world those are the world's own built-ins and nothing the page did reaches them.
    /// </summary>
    [Fact]
    public async Task PageTamperingDoesNotReachUtilityWorldResults()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached page = await OpenAsync(ctx, TamperingPage);
        Assert.Equal("tampered", Value(await EvaluateAsync(page, "[1].map(x => x)[0]")).AsString());
        long world = await CreateWorldAsync(page, "__playwright_utility_world__");

        JsonNode reply = await CoreCdp.CdpAsync(
            ctx, 40, "Runtime.callFunctionOn",
            new JsonObject
            {
                ["functionDeclaration"] = """
                    async function (selector) {
                        const texts = [...document.querySelectorAll(selector)].map(p => p.textContent);
                        const first = document.querySelector(selector).id;
                        const later = await Promise.resolve(first).then(v => v + '!');
                        return JSON.stringify({ texts, later, secret: typeof pageSecret });
                    }
                    """,
                ["executionContextId"] = world,
                ["arguments"] = new JsonArray(new JsonObject { ["value"] = "p" }),
                ["returnByValue"] = true,
                ["awaitPromise"] = true,
            },
            page.Session);
        Assert.Null(reply["exceptionDetails"]);
        Assert.Equal("""{"texts":["real","two"],"later":"a!","secret":"undefined"}""", Value(reply).AsString());
    }

    [Fact]
    public async Task ObjectIdsBelongToTheWorldThatMintedThem()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached page = await OpenAsync(ctx, "data:text/html,<p id=a>x</p><script>window.pageSecret='page'</script>");
        long world = await CreateWorldAsync(page, "utility");

        JsonNode worldNode = await EvaluateAsync(page, "document.getElementById('a')", world, byValue: false);
        Assert.Equal("node", worldNode["result"]!["subtype"].AsString());
        string worldId = worldNode["result"]!["objectId"].AsString()!;
        JsonNode mainNode = await EvaluateAsync(page, "document.getElementById('a')", byValue: false);
        string mainId = mainNode["result"]!["objectId"].AsString()!;
        Assert.NotEqual(worldId, mainId);
        // The page realm's ids keep their Rust shape.
        Assert.StartsWith("{\"injectedScriptId\":1,", mainId, StringComparison.Ordinal);

        // callFunctionOn(objectId) runs where the object lives.
        JsonNode inWorld = await CoreCdp.CdpAsync(
            ctx, 50, "Runtime.callFunctionOn",
            new JsonObject
            {
                ["functionDeclaration"] = "function () { return this.id + ':' + typeof pageSecret; }",
                ["objectId"] = worldId,
                ["returnByValue"] = true,
            },
            page.Session);
        Assert.Equal("a:undefined", Value(inWorld).AsString());

        // An argument from another world is refused, as Chromium refuses it.
        CdpResponse crossed = await CoreCdp.DispatchAsync(
            ctx, 51, "Runtime.callFunctionOn",
            new JsonObject
            {
                ["functionDeclaration"] = "function (x) { return x; }",
                ["objectId"] = worldId,
                ["arguments"] = new JsonArray(new JsonObject { ["objectId"] = mainId }),
            },
            page.Session);
        Assert.NotNull(crossed.Error);
        Assert.Contains("same JavaScript world", crossed.Error!.Message, StringComparison.Ordinal);

        // getProperties walks the world's object.
        JsonNode list = await EvaluateAsync(page, "[document.getElementById('a'), 'two']", world, byValue: false);
        JsonNode properties = await CoreCdp.CdpAsync(
            ctx, 52, "Runtime.getProperties",
            new JsonObject { ["objectId"] = list["result"]!["objectId"].AsString() },
            page.Session);
        JsonArray descriptors = properties["result"]!.AsArray();
        Assert.Equal("node", descriptors[0]!["value"]!["subtype"].AsString());
        Assert.Equal("two", descriptors[1]!["value"]!["value"].AsString());
        // The child handle lives in the world too: DOM.describeNode finds it there.
        string childId = descriptors[0]!["value"]!["objectId"].AsString()!;
        JsonNode described = await CoreCdp.CdpAsync(
            ctx, 53, "DOM.describeNode", new JsonObject { ["objectId"] = childId }, page.Session);
        Assert.Equal("P", described["node"]!["nodeName"].AsString());

        // A released world object is gone from the world.
        await CoreCdp.CdpAsync(ctx, 54, "Runtime.releaseObject", new JsonObject { ["objectId"] = worldId }, page.Session);
        JsonNode released = await CoreCdp.CdpAsync(
            ctx, 55, "Runtime.callFunctionOn",
            new JsonObject
            {
                ["functionDeclaration"] = "function () { return this === globalThis; }",
                ["objectId"] = worldId,
                ["returnByValue"] = true,
            },
            page.Session);
        Assert.True(Value(released).AsBool());
    }

    [Fact]
    public async Task NodesMoveBetweenWorldsThroughTheirBackendId()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached page = await OpenAsync(ctx, "data:text/html,<p id=a>x</p><script>window.pageSecret='page'</script>");
        long world = await CreateWorldAsync(page, "utility");
        string worldId = (await EvaluateAsync(page, "document.getElementById('a')", world, byValue: false))
            ["result"]!["objectId"].AsString()!;
        string mainId = (await EvaluateAsync(page, "document.getElementById('a')", byValue: false))
            ["result"]!["objectId"].AsString()!;

        JsonNode fromWorld = await CoreCdp.CdpAsync(ctx, 60, "DOM.describeNode", new JsonObject { ["objectId"] = worldId }, page.Session);
        JsonNode fromMain = await CoreCdp.CdpAsync(ctx, 61, "DOM.describeNode", new JsonObject { ["objectId"] = mainId }, page.Session);
        long backend = fromWorld["node"]!["backendNodeId"].AsI64()!.Value;
        Assert.Equal(fromMain["node"]!["backendNodeId"].AsI64(), backend);
        Assert.Equal("P", fromWorld["node"]!["nodeName"].AsString());

        JsonNode requested = await CoreCdp.CdpAsync(ctx, 62, "DOM.requestNode", new JsonObject { ["objectId"] = worldId }, page.Session);
        Assert.Equal(backend, requested["nodeId"].AsI64());

        // resolveNode into the world makes a world handle; without a context, a page handle.
        JsonNode intoWorld = await CoreCdp.CdpAsync(
            ctx, 63, "DOM.resolveNode",
            new JsonObject { ["backendNodeId"] = backend, ["executionContextId"] = world },
            page.Session);
        JsonNode intoMain = await CoreCdp.CdpAsync(ctx, 64, "DOM.resolveNode", new JsonObject { ["backendNodeId"] = backend }, page.Session);
        foreach ((JsonNode resolved, string expected) in new[] { (intoWorld, "a:undefined"), (intoMain, "a:string") })
        {
            Assert.Equal("node", resolved["object"]!["subtype"].AsString());
            JsonNode probe = await CoreCdp.CdpAsync(
                ctx, 65, "Runtime.callFunctionOn",
                new JsonObject
                {
                    ["functionDeclaration"] = "function () { return this.id + ':' + typeof pageSecret; }",
                    ["objectId"] = resolved["object"]!["objectId"].AsString(),
                    ["returnByValue"] = true,
                },
                page.Session);
            Assert.Equal(expected, Value(probe).AsString());
        }

        // Geometry reads the same node whichever world's handle names it.
        JsonNode box = await CoreCdp.CdpAsync(ctx, 66, "DOM.getBoxModel", new JsonObject { ["objectId"] = worldId }, page.Session);
        Assert.NotNull(box["model"]);

        CdpResponse unknown = await CoreCdp.DispatchAsync(
            ctx, 67, "DOM.resolveNode", new JsonObject { ["backendNodeId"] = backend, ["executionContextId"] = 987654 }, page.Session);
        Assert.NotNull(unknown.Error);
    }

    [Fact]
    public async Task NavigationDestroysWorldsAndTheNextDocumentGetsFreshOnes()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached page = await OpenAsync(ctx, "data:text/html,<p>first</p>");
        long world = await CreateWorldAsync(page, "utility");
        await EvaluateAsync(page, "globalThis.carried = 1", world);
        ctx.PendingEvents.Clear();

        await CoreCdp.CdpAsync(
            ctx, 70, "Page.navigate",
            new JsonObject { ["url"] = "data:text/html,<p id=n>second</p>", ["waitUntil"] = "load" },
            page.Session);
        Assert.Contains(ctx.PendingEvents, e => e.Method == "Runtime.executionContextsCleared" && e.SessionId == page.Session);
        CdpEvent recreated = Assert.Single(
            ctx.PendingEvents,
            e => e.Method == "Runtime.executionContextCreated"
                && e.SessionId == page.Session
                && e.Params.Get("context").Get("name").AsString() == "utility");
        long next = recreated.Params.Get("context").Get("id").AsI64()!.Value;
        Assert.NotEqual(world, next);

        CdpResponse stale = await CoreCdp.DispatchAsync(
            ctx, 71, "Runtime.evaluate", new JsonObject { ["expression"] = "1", ["contextId"] = world }, page.Session);
        Assert.NotNull(stale.Error);
        Assert.Contains("Cannot find context", stale.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(
            "undefined:second",
            Value(await EvaluateAsync(page, "typeof carried + ':' + document.getElementById('n').textContent", next)).AsString());
    }

    /// <summary>
    /// A connection keeps one live runtime, so using a second page suspends the first. Its
    /// world comes back with the page, and a handle minted there is rebuilt the way a page
    /// realm handle is.
    /// </summary>
    [Fact]
    public async Task WorldHandlesSurviveSwitchingToAnotherPage()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached first = await OpenAsync(ctx, "data:text/html,<p id=a>first</p>");
        long world = await CreateWorldAsync(first, "utility");
        string handle = (await EvaluateAsync(first, "document.getElementById('a')", world, byValue: false))
            ["result"]!["objectId"].AsString()!;

        Attached second = await OpenAsync(ctx, "data:text/html,<p id=a>second</p>");
        Assert.Equal("second", Value(await EvaluateAsync(second, "document.getElementById('a').textContent")).AsString());

        JsonNode back = await CoreCdp.CdpAsync(
            ctx, 75, "Runtime.callFunctionOn",
            new JsonObject
            {
                ["functionDeclaration"] = "function () { return this.textContent + ':' + typeof pageSecret; }",
                ["objectId"] = handle,
                ["returnByValue"] = true,
            },
            first.Session);
        Assert.Equal("first:undefined", Value(back).AsString());
    }

    [Fact]
    public async Task WorldScriptsToEvaluateOnNewDocumentRunInTheWorld()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached page = await OpenAsync(ctx, "data:text/html,<p>x</p>");
        await CoreCdp.CdpAsync(
            ctx, 80, "Page.addScriptToEvaluateOnNewDocument",
            new JsonObject { ["source"] = "globalThis.worldInit = 'ran';", ["worldName"] = "utility" },
            page.Session);
        await CoreCdp.CdpAsync(
            ctx, 81, "Page.navigate",
            new JsonObject { ["url"] = "data:text/html,<p>y</p>", ["waitUntil"] = "load" },
            page.Session);
        long world = await CreateWorldAsync(page, "utility");

        Assert.Equal("ran", Value(await EvaluateAsync(page, "globalThis.worldInit", world)).AsString());
        Assert.Equal("undefined", Value(await EvaluateAsync(page, "typeof worldInit")).AsString());
    }

    [Fact]
    public async Task BindingsForAWorldReportTheWorldsContext()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        Attached page = await OpenAsync(ctx, "data:text/html,<p>x</p>");
        long world = await CreateWorldAsync(page, "utility");
        await CoreCdp.CdpAsync(
            ctx, 90, "Runtime.addBinding",
            new JsonObject { ["name"] = "worldBinding", ["executionContextName"] = "utility" },
            page.Session);
        Assert.Equal("undefined", Value(await EvaluateAsync(page, "typeof worldBinding")).AsString());
        ctx.PendingEvents.Clear();

        await EvaluateAsync(page, "worldBinding('hello'); 1", world);
        CdpEvent called = Assert.Single(ctx.PendingEvents, e => e.Method == "Runtime.bindingCalled");
        Assert.Equal("worldBinding", called.Params.Get("name").AsString());
        Assert.Equal("hello", called.Params.Get("payload").AsString());
        Assert.Equal(world, called.Params.Get("executionContextId").AsI64());
    }
}
