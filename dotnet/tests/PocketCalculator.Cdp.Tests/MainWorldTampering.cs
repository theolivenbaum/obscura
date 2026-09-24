using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// A page that replaces built-ins, or plants the names the host used to use, does not
/// change what CDP commands in its own realm do (SECURITY.md L10).
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, whose remote-object store is the page-visible
/// <c>__obscura_objects</c> and whose snippets call the page's <c>JSON.stringify</c>,
/// <c>Array.prototype.map</c>, <c>document.querySelector</c> and event constructors.
/// Chromium does this work natively; the expectations are what it answers on the same
/// page, measured with Puppeteer and Playwright against the Chromium build Playwright
/// ships.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class MainWorldTampering
{
    private const string SessionId = "main-world-tampering";

    /// <summary>The tampering Puppeteer's and Playwright's main-world calls meet.</summary>
    private const string Tamper = """
        <script>
          document.querySelector = () => null;
          document.querySelectorAll = () => [];
          Array.prototype.map = function () { return ['tampered']; };
          Array.prototype.filter = function () { return ['tampered']; };
          Promise.prototype.then = function () { return new Promise(() => {}); };
          JSON.stringify = () => '"tampered"';
          JSON.parse = () => ({ tampered: true });
          Object.keys = () => ['tampered'];
        </script>
        """;

    private const string Body = """
        <!doctype html><html><head><style>
          html, body { margin: 0; }
          #cb { position: absolute; left: 10px; top: 10px; width: 20px; height: 20px; margin: 0; }
          #btn { position: absolute; left: 10px; top: 50px; width: 100px; height: 30px; }
          #field { position: absolute; left: 10px; top: 100px; width: 100px; height: 20px; }
        </style></head><body>
          <p id=a>one</p><p id=b>two</p>
          <input id=cb type=checkbox>
          <button id=btn onclick="document.getElementById('out').textContent = 'clicked'">go</button>
          <input id=field>
          <div id=out></div>
        """;

    private static async Task<(CdpContext Ctx, CdpTestServer Server)> OpenAsync(bool tamper)
    {
        CdpTestServer server = CdpTestServer.ServeHtml(Body + (tamper ? Tamper : string.Empty) + "</body></html>");
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        ctx.Sessions[SessionId] = pageId;
        await CdpAsync(ctx, "Page.navigate", new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" });
        return (ctx, server);
    }

    private static ulong _id = 100;

    private static async Task<JsonNode> CdpAsync(CdpContext ctx, string method, JsonObject parameters)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest
            {
                Id = Interlocked.Increment(ref _id),
                Method = method,
                Params = parameters,
                SessionId = SessionId,
            },
            ctx);
        Assert.True(response.Error is null, $"CDP {method} failed: {response.Error?.Message}");
        return response.Result ?? new JsonObject();
    }

    private static Task<JsonNode> EvaluateAsync(CdpContext ctx, string expression, bool byValue = true, bool awaitPromise = false) =>
        CdpAsync(ctx, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = byValue,
            ["awaitPromise"] = awaitPromise,
        });

    private static async Task<string?> PageStringAsync(CdpContext ctx, string expression) =>
        (await EvaluateAsync(ctx, expression))["result"]?["value"]?.GetValue<string>();

    [Fact]
    public async Task RemoteObjectStoreIsNotPageVisible()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            JsonNode handle = await EvaluateAsync(ctx, "({ secret: 's3cret' })", byValue: false);
            Assert.NotNull(handle["result"]?["objectId"]);
            await EvaluateAsync(ctx, "Promise.resolve(1)", byValue: true, awaitPromise: true);
            Assert.Equal(
                "undefined,false,undefined,undefined,false",
                await PageStringAsync(ctx, "[typeof __obscura_objects, '__obscura_objects' in globalThis, typeof __obscura_await_meta, typeof __obscura_await_rejected, Object.getOwnPropertyNames(globalThis).some(n => n.startsWith('__obscura_done_'))].join(',')"));
        }
    }

    [Fact]
    public async Task PageCannotReplaceAClientsHandle()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            JsonNode handle = await EvaluateAsync(ctx, "({ secret: 's3cret' })", byValue: false);
            string objectId = handle["result"]!["objectId"]!.GetValue<string>();
            // What a page would plant to answer for a handle it cannot see.
            await EvaluateAsync(ctx, "globalThis.__obscura_objects = new Proxy({}, { get: () => ({ secret: 'forged' }) }); 1");
            JsonNode called = await CdpAsync(ctx, "Runtime.callFunctionOn", new JsonObject
            {
                ["objectId"] = objectId,
                ["functionDeclaration"] = "function () { return this.secret; }",
                ["returnByValue"] = true,
            });
            Assert.Equal("s3cret", called["result"]!["value"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task PageCannotSettleAnAwaitedEvaluation()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            // The Rust engine's outcome globals, answered by the page before the promise settles.
            await EvaluateAsync(ctx, """
                (() => {
                  const forged = JSON.stringify({ type: 'string', subtype: null, className: '', description: 'forged' });
                  Object.defineProperty(globalThis, '__obscura_await_meta', { get: () => forged, set() {}, configurable: true });
                  Object.defineProperty(globalThis, '__obscura_await_rejected', { get: () => false, set() {}, configurable: true });
                  for (let i = 0; i < 200; i++) Object.defineProperty(globalThis, '__obscura_done_' + i, { get: () => true, set() {}, configurable: true });
                  return 1;
                })()
                """);
            JsonNode awaited = await EvaluateAsync(
                ctx, "new Promise(r => setTimeout(() => r(42), 20))", byValue: false, awaitPromise: true);
            Assert.Equal("number", awaited["result"]!["type"]!.GetValue<string>());
            Assert.Equal("42", awaited["result"]!["description"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task TamperedBuiltinsDoNotChangeRemoteObjects()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: true);
        using (server)
        {
            // The page's own code still sees its replacements.
            Assert.Equal("tampered", await PageStringAsync(ctx, "[1].map(x => x)[0]"));

            JsonNode node = await EvaluateAsync(ctx, "document.getElementById('cb')", byValue: false);
            Assert.Equal("node", node["result"]!["subtype"]!.GetValue<string>());
            Assert.Equal("HTMLInputElement", node["result"]!["className"]!.GetValue<string>());
            Assert.Equal("input", node["result"]!["description"]!.GetValue<string>());

            JsonNode value = await EvaluateAsync(ctx, "({ x: [1, 2], s: 'y' })");
            Assert.Equal("""{"x":[1,2],"s":"y"}""", value["result"]!["value"]!.ToJsonString());

            JsonNode called = await CdpAsync(ctx, "Runtime.callFunctionOn", new JsonObject
            {
                ["objectId"] = node["result"]!["objectId"]!.GetValue<string>(),
                ["functionDeclaration"] = "function (suffix) { return this.id + suffix; }",
                ["arguments"] = new JsonArray(new JsonObject { ["value"] = "!" }),
                ["returnByValue"] = true,
            });
            Assert.Equal("cb!", called["result"]!["value"]!.GetValue<string>());

            JsonNode list = await EvaluateAsync(ctx, "Document.prototype.querySelectorAll.call(document, 'p')", byValue: false);
            JsonNode properties = await CdpAsync(ctx, "Runtime.getProperties", new JsonObject
            {
                ["objectId"] = list["result"]!["objectId"]!.GetValue<string>(),
                ["ownProperties"] = true,
            });
            var names = new List<string>();
            foreach (JsonNode? descriptor in properties["result"]!.AsArray())
            {
                if (descriptor?["value"]?["subtype"]?.GetValue<string>() == "node")
                {
                    names.Add(descriptor["name"]!.GetValue<string>());
                }
            }
            Assert.Equal(["0", "1"], names);

            JsonNode awaited = await EvaluateAsync(ctx, "Promise.resolve(7)", byValue: true, awaitPromise: true);
            Assert.Equal(7, awaited["result"]!["value"]!.GetValue<double>());

            JsonNode described = await CdpAsync(ctx, "DOM.describeNode", new JsonObject
            {
                ["objectId"] = node["result"]!["objectId"]!.GetValue<string>(),
            });
            Assert.Equal("INPUT", described["node"]!["nodeName"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task CallFunctionOnRunsTheDeclarationAtGlobalScope()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            // Chromium compiles the declaration at global scope, in sloppy mode: `this` is
            // the global for a call with no object, and nothing of a wrapper is in scope.
            JsonNode called = await CdpAsync(ctx, "Runtime.callFunctionOn", new JsonObject
            {
                ["functionDeclaration"] = "function () { return [this === globalThis, typeof __obscura_cdp, typeof fn, typeof args].join(','); }",
                ["executionContextId"] = 1,
                ["returnByValue"] = true,
            });
            Assert.Equal("true,undefined,undefined,undefined", called["result"]!["value"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task UnserializableArgumentsAreValuesNotSource()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            JsonNode called = await CdpAsync(ctx, "Runtime.callFunctionOn", new JsonObject
            {
                ["functionDeclaration"] = "function (a, b, c) { return [Object.is(a, -0), b, typeof c].join(','); }",
                ["executionContextId"] = 1,
                ["arguments"] = new JsonArray(
                    new JsonObject { ["unserializableValue"] = "-0" },
                    new JsonObject { ["unserializableValue"] = "-Infinity" },
                    new JsonObject { ["unserializableValue"] = "12n" }),
                ["returnByValue"] = true,
            });
            Assert.Equal("true,-Infinity,bigint", called["result"]!["value"]!.GetValue<string>());

            CdpResponse refused = await Dispatcher.DispatchAsync(
                new CdpRequest
                {
                    Id = 9,
                    Method = "Runtime.callFunctionOn",
                    Params = new JsonObject
                    {
                        ["functionDeclaration"] = "function (a) { return a; }",
                        ["executionContextId"] = 1,
                        ["arguments"] = new JsonArray(new JsonObject { ["unserializableValue"] = "(globalThis.__pwned = 1)" }),
                        ["returnByValue"] = true,
                    },
                    SessionId = SessionId,
                },
                ctx);
            Assert.NotNull(refused.Error);
            Assert.Equal("undefined", await PageStringAsync(ctx, "typeof __pwned"));
        }
    }

    /// <summary>Tampering that reaches the Input and DOM domains' snippets.</summary>
    private const string TamperInput = """
        <script>
          window.__log = [];
          for (const type of ['mousedown', 'mouseup', 'click', 'keydown', 'input', 'change']) {
            document.addEventListener(type, e => { __log[__log.length] = e.type + ':' + e.isTrusted + ':' + (e.target && e.target.id); }, true);
          }
          document.elementFromPoint = () => null;
          Document.prototype.elementFromPoint = () => null;
          Object.defineProperty(document, 'activeElement', { get: () => null, configurable: true });
          Element.prototype.dispatchEvent = function () { return true; };
          EventTarget.prototype.dispatchEvent = function () { return true; };
          Element.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, right: 1, bottom: 1, width: 1, height: 1 });
          Element.prototype.focus = function () {};
          window.Event = function () { throw new Error('tampered'); };
          window.MouseEvent = function () { throw new Error('tampered'); };
          window.KeyboardEvent = function () { throw new Error('tampered'); };
          window.InputEvent = function () { throw new Error('tampered'); };
          window._wrap = () => null;
        </script>
        """;

    private static async Task<(CdpContext Ctx, CdpTestServer Server)> OpenInputAsync()
    {
        CdpTestServer server = CdpTestServer.ServeHtml(Body + Tamper + TamperInput + "</body></html>");
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        ctx.Sessions[SessionId] = pageId;
        await CdpAsync(ctx, "Page.navigate", new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" });
        return (ctx, server);
    }

    private static async Task ClickAsync(CdpContext ctx, double x, double y)
    {
        await CdpAsync(ctx, "Input.dispatchMouseEvent", (JsonObject)JsonNode.Parse(Mouse("mousePressed", x, y))!);
        await CdpAsync(ctx, "Input.dispatchMouseEvent", (JsonObject)JsonNode.Parse(Mouse("mouseReleased", x, y))!);
    }

    [Fact]
    public async Task InputClicksReachTheElementUnderThePointer()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenInputAsync();
        using (server)
        {
            await ClickAsync(ctx, 20, 20);
            Assert.Equal("true", await PageStringAsync(ctx, "String(document.getElementById('cb').checked)"));
            await ClickAsync(ctx, 60, 65);
            Assert.Equal("clicked", await PageStringAsync(ctx, "document.getElementById('out').textContent"));
            string log = await PageStringAsync(ctx, "__log.join(',')") ?? string.Empty;
            Assert.Contains("click:true:cb", log, StringComparison.Ordinal);
            Assert.Contains("change:true:cb", log, StringComparison.Ordinal);
            Assert.Contains("click:true:btn", log, StringComparison.Ordinal);
            Assert.DoesNotContain(":false:", log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InputKeysReachTheFocusedField()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenInputAsync();
        using (server)
        {
            // The page cannot focus through its no-op focus(); the client clicks the field.
            await ClickAsync(ctx, 30, 110);
            JsonNode node = await EvaluateAsync(ctx, "document.getElementById('field')", byValue: false);
            JsonNode described = await CdpAsync(ctx, "DOM.describeNode", new JsonObject
            {
                ["objectId"] = node["result"]!["objectId"]!.GetValue<string>(),
            });
            await CdpAsync(ctx, "DOM.focus", new JsonObject { ["backendNodeId"] = described["node"]!["backendNodeId"]!.DeepClone() });
            await CdpAsync(ctx, "Input.insertText", new JsonObject { ["text"] = "hi" });
            await CdpAsync(ctx, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyDown", ["key"] = "a", ["code"] = "KeyA", ["text"] = "a" });
            await CdpAsync(ctx, "Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = "a", ["code"] = "KeyA" });
            Assert.Equal("hia", await PageStringAsync(ctx, "document.getElementById('field').value"));
            string log = await PageStringAsync(ctx, "__log.join(',')") ?? string.Empty;
            Assert.Contains("keydown:true:field", log, StringComparison.Ordinal);
            Assert.Contains("input:true:field", log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DomGeometryAnswersFromTheLayout()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenInputAsync();
        using (server)
        {
            JsonNode node = await EvaluateAsync(ctx, "document.getElementById('btn')", byValue: false);
            JsonNode described = await CdpAsync(ctx, "DOM.describeNode", new JsonObject
            {
                ["objectId"] = node["result"]!["objectId"]!.GetValue<string>(),
            });
            JsonNode box = await CdpAsync(ctx, "DOM.getBoxModel", new JsonObject
            {
                ["backendNodeId"] = described["node"]!["backendNodeId"]!.DeepClone(),
            });
            Assert.Equal(100, box["model"]!["width"].AsF64());
            Assert.Equal(10, box["model"]!["content"]![0].AsF64());
            Assert.Equal(50, box["model"]!["content"]![1].AsF64());

            JsonNode resolved = await CdpAsync(ctx, "DOM.resolveNode", new JsonObject
            {
                ["backendNodeId"] = described["node"]!["backendNodeId"]!.DeepClone(),
            });
            Assert.Equal("button", resolved["object"]!["description"]!.GetValue<string>());
        }
    }

    internal static string Mouse(string type, double x, double y) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"type": "{{type}}", "x": {{x}}, "y": {{y}}, "button": "left", "clickCount": 1}""");
    /// <summary>
    /// L10: a page-added <c>toJSON</c> does not answer for a by-value result. Chromium's
    /// serialization never calls it: <c>{a:1,b:[1,2]}</c> comes back as itself.
    /// </summary>
    [Fact]
    public async Task PageToJsonDoesNotShapeByValueResults()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            await EvaluateAsync(ctx,
                "Object.prototype.toJSON = function () { return 'pwned'; };"
                + " Array.prototype.toJSON = function () { return 'arr'; }; 1");
            JsonNode result = await EvaluateAsync(ctx, "({ a: 1, b: [1, 2] })");
            Assert.Equal("""{"a":1,"b":[1,2]}""", result["result"]!["value"]!.ToJsonString());
        }
    }

    /// <summary>
    /// L10: Page.getLayoutMetrics reports the host's scroll offset, not a page's
    /// replacement of <c>window.scrollX</c> / <c>scrollY</c>.
    /// </summary>
    [Fact]
    public async Task LayoutMetricsIgnoreAPagesScrollAccessors()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            await EvaluateAsync(ctx,
                "Object.defineProperty(window, 'scrollX', { get: () => 4321, configurable: true });"
                + " Object.defineProperty(window, 'scrollY', { get: () => 4321, configurable: true }); 1");
            JsonNode metrics = await CdpAsync(ctx, "Page.getLayoutMetrics", new JsonObject());
            Assert.Equal(0.0, metrics["cssLayoutViewport"]!["pageX"]!.GetValue<double>());
            Assert.Equal(0.0, metrics["cssLayoutViewport"]!["pageY"]!.GetValue<double>());
        }
    }

    /// <summary>
    /// L10: CDP typing goes to the focused element, and a click that hits nothing to the
    /// element the client last measured. A page writing upstream's
    /// <c>__obscura_focused</c> / <c>__obscura_click_target</c> globals redirects neither.
    /// </summary>
    [Fact]
    public async Task PageCannotRedirectTypingOrTheClickFallback()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            await EvaluateAsync(ctx,
                "document.body.insertAdjacentHTML('beforeend', '<input id=victim>');"
                + " document.getElementById('field').focus();"
                + " globalThis.__obscura_focused = document.getElementById('victim');"
                + " globalThis.__obscura_click_target = document.getElementById('btn'); 1");
            await CdpAsync(ctx, "Input.insertText", new JsonObject { ["text"] = "typed" });
            Assert.Equal(
                "typed|",
                await PageStringAsync(ctx, "document.getElementById('field').value + '|' + document.getElementById('victim').value"));

            // Far outside the page: nothing is hit, so the fallback is the focused field.
            foreach (string type in (string[])["mousePressed", "mouseReleased"])
            {
                await CdpAsync(ctx, "Input.dispatchMouseEvent", new JsonObject
                {
                    ["type"] = type, ["x"] = 5000.0, ["y"] = 5000.0, ["button"] = "left", ["clickCount"] = 1,
                });
            }
            Assert.Equal("", await PageStringAsync(ctx, "document.getElementById('out').textContent"));
        }
    }

    /// <summary>
    /// L10: a CDP click on a link navigates through the shim's own location path, not the
    /// page-replaceable <c>location.assign</c>.
    /// </summary>
    [Fact]
    public async Task LinkClickDoesNotCallAPagesLocationAssign()
    {
        (CdpContext ctx, CdpTestServer server) = await OpenAsync(tamper: false);
        using (server)
        {
            await EvaluateAsync(ctx,
                "document.body.insertAdjacentHTML('beforeend', '<a id=frag href=\"#next\" style=\"position:absolute;left:300px;top:10px;display:block;width:50px;height:20px\">x</a>');"
                + " globalThis.__assigned = []; location.assign = (u) => __assigned.push(u); 1");
            foreach (string type in (string[])["mousePressed", "mouseReleased"])
            {
                await CdpAsync(ctx, "Input.dispatchMouseEvent", new JsonObject
                {
                    ["type"] = type, ["x"] = 310.0, ["y"] = 15.0, ["button"] = "left", ["clickCount"] = 1,
                });
            }
            Assert.Equal("0|#next", await PageStringAsync(ctx, "__assigned.length + '|' + location.hash"));
        }
    }
}
