using System.Text.Json.Nodes;
using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// Mouse input and <c>DOM.getNodeForLocation</c> hit-test through shadow trees, as Chromium 141
/// does: the event is dispatched at the element inside the shadow tree, and a listener on the
/// document sees it retargeted to the host. The shim's old hit test never entered a shadow tree,
/// so a click on shadow content was dispatched at the host itself.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class InputShadowHitTests
{
    private static CdpTestServer ServeFixture() => CdpTestServer.ServeHtml(
        """
        <!doctype html><html><head><style>
            html, body { margin: 0; }
            #host { position: absolute; left: 20px; top: 20px; width: 300px; height: 100px; }
            #float { float: left; width: 100px; height: 50px; }
        </style></head><body>
          <div id="host"></div>
          <div style="position:absolute;top:200px;width:400px"><div id="float"></div><p id="para" style="margin:0">text beside the float</p></div>
          <script>
            const root = document.getElementById('host').attachShadow({ mode: 'open' });
            root.innerHTML = '<div style="height:40px"></div><button id="inner" style="width:120px;height:30px">press</button>';
            globalThis.clickLog = [];
            root.getElementById('inner').addEventListener('click', (event) => {
              clickLog.push('inner:' + event.composedPath()[0].id + ':' + event.isTrusted);
            });
            document.addEventListener('click', (event) => {
              clickLog.push('document:' + (event.target.id || event.target.tagName));
            });
          </script>
        </body></html>
        """);

    private static async Task<JsonNode> CdpAsync(CdpContext ctx, ulong id, string method, string parameters, string sessionId)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest
            {
                Id = id,
                Method = method,
                Params = CdpDomainFixtures.Json(parameters),
                SessionId = sessionId,
            },
            ctx);
        Assert.True(response.Error is null, $"CDP {method} failed: {response.Error?.Message}");
        return response.Result ?? new JsonObject();
    }

    private static async Task<(CdpContext Ctx, string SessionId, CdpTestServer Server)> SetupAsync()
    {
        CdpTestServer server = ServeFixture();
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        const string SessionId = "input-shadow-session";
        ctx.Sessions[SessionId] = pageId;
        await CdpAsync(ctx, 1, "Page.navigate", $$"""{"url": "{{server.Url}}", "waitUntil": "load"}""", SessionId);
        return (ctx, SessionId, server);
    }

    private static async Task<string> EvaluateStringAsync(CdpContext ctx, ulong id, string expression, string sessionId)
    {
        JsonNode result = await CdpAsync(
            ctx,
            id,
            "Runtime.evaluate",
            $$"""{"expression": {{CdpJson.String(expression)}}, "returnByValue": true}""",
            sessionId);
        return result["result"]!["value"]!.GetValue<string>();
    }

    [Fact]
    public async Task AClickOnShadowContentIsDispatchedAtTheElementInsideTheShadowTree()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            // The button is at 20,60 to 140,90 in the viewport.
            await CdpAsync(ctx, 2, "Input.dispatchMouseEvent", """{"type": "mousePressed", "x": 50, "y": 75, "button": "left", "clickCount": 1}""", sid);
            await CdpAsync(ctx, 3, "Input.dispatchMouseEvent", """{"type": "mouseReleased", "x": 50, "y": 75, "button": "left", "clickCount": 1}""", sid);
            string log = await EvaluateStringAsync(ctx, 4, "clickLog.join(',')", sid);
            Assert.Equal("inner:inner:true,document:host", log);

            // The page-facing API retargets the same point to the host.
            Assert.Equal("host", await EvaluateStringAsync(ctx, 5, "document.elementFromPoint(50, 75).id", sid));
        }
    }

    [Fact]
    public async Task GetNodeForLocationReportsTheDeepestElement()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            JsonNode inner = await CdpAsync(ctx, 2, "DOM.getNodeForLocation", """{"x": 50, "y": 75}""", sid);
            JsonNode described = await CdpAsync(
                ctx,
                3,
                "DOM.describeNode",
                $$"""{"backendNodeId": {{inner["backendNodeId"]!.GetValue<ulong>()}}}""",
                sid);
            Assert.Equal("button", described["node"]!["localName"]!.GetValue<string>());

            // A point inside the float hits the float, not the paragraph beside it.
            JsonNode floatHit = await CdpAsync(ctx, 4, "DOM.getNodeForLocation", """{"x": 30, "y": 220}""", sid);
            JsonNode floatNode = await CdpAsync(
                ctx,
                5,
                "DOM.describeNode",
                $$"""{"backendNodeId": {{floatHit["backendNodeId"]!.GetValue<ulong>()}}}""",
                sid);
            JsonArray attributes = floatNode["node"]!["attributes"]!.AsArray();
            Assert.Contains("float", attributes.Select(entry => entry?.GetValue<string>()));
        }
    }
}
