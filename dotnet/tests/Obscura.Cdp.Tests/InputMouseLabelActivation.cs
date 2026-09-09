using System.Globalization;
using System.Text.Json.Nodes;
using Obscura.Cdp;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/input_mouse_label_activation.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class InputMouseLabelActivation
{
    // The labels carry their own boxes so a coordinate click has a target regardless of
    // inline-wrapper geometry.
    private static CdpTestServer ServeFixture() => CdpTestServer.ServeHtml(
        """
        <!doctype html><html><head><style>
                    html, body { margin: 0; font: 16px monospace }
                    label { display: block; width: 200px; height: 40px }
                </style></head><body>
                  <label id="explicit" for="boxa">a</label><input id="boxa" type="checkbox">
                  <label id="implicit">b <input id="boxb" type="checkbox"></label>
                  <label id="deep" for="boxc"><span><b id="deep-text">c</b></span></label>
                  <input id="boxc" type="checkbox">
                  <label id="off" for="boxd">d</label><input id="boxd" type="checkbox" disabled>
                  <script>
                    window.events = [];
                    for (const id of ['boxa','boxb','boxc','boxd']) {
                      const el = document.getElementById(id);
                      for (const t of ['click','input','change']) {
                        el.addEventListener(t, e => window.events.push(id + ':' + t + ':' + e.isTrusted));
                      }
                    }
                  </script>
                </body></html>
        """);

    private static async Task<JsonNode> CdpAsync(
        CdpContext ctx,
        ulong id,
        string method,
        string parameters,
        string sessionId)
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

    private static Task<JsonNode> EvaluateAsync(
        CdpContext ctx,
        ulong id,
        string expression,
        string sessionId) =>
        CdpAsync(
            ctx,
            id,
            "Runtime.evaluate",
            $$"""
            {
                "expression": {{CdpJson.String(expression)}},
                "returnByValue": true,
                "awaitPromise": true
            }
            """,
            sessionId);

    private static async Task<(CdpContext Ctx, string SessionId, CdpTestServer Server)> SetupAsync()
    {
        CdpTestServer server = ServeFixture();
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        const string SessionId = "label-activation-session";
        ctx.Sessions[SessionId] = pageId;
        await CdpAsync(
            ctx,
            1,
            "Page.navigate",
            $$"""{"url": "{{server.Url}}", "waitUntil": "load"}""",
            SessionId);
        return (ctx, SessionId, server);
    }

    /// <summary>Click the centre of <paramref name="selector"/> the way a real pointer would.</summary>
    private static async Task ClickElementAsync(
        CdpContext ctx,
        ulong id,
        string sessionId,
        string selector)
    {
        JsonNode raw = await EvaluateAsync(
            ctx,
            id,
            $"JSON.stringify(document.querySelector('{selector}').getBoundingClientRect().toJSON())",
            sessionId);
        JsonNode rect = CdpDomainFixtures.Json(raw["result"]!["value"]!.GetValue<string>());
        double x = rect["x"]!.GetValue<double>() + (rect["width"]!.GetValue<double>() / 2.0);
        double y = rect["y"]!.GetValue<double>() + (rect["height"]!.GetValue<double>() / 2.0);
        string[] kinds = ["mousePressed", "mouseReleased"];
        foreach (string kind in kinds)
        {
            await CdpAsync(
                ctx,
                id + 1,
                "Input.dispatchMouseEvent",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $$"""
                    {"type": "{{kind}}", "x": {{x}}, "y": {{y}}, "button": "left", "clickCount": 1}
                    """),
                sessionId);
        }
    }

    private static async Task<JsonNode> StateAsync(CdpContext ctx, ulong id, string sessionId)
    {
        JsonNode raw = await EvaluateAsync(
            ctx,
            id,
            """
            JSON.stringify({
                a: document.getElementById('boxa').checked,
                b: document.getElementById('boxb').checked,
                c: document.getElementById('boxc').checked,
                d: document.getElementById('boxd').checked,
                events: window.events.join(',')
            })
            """,
            sessionId);
        return CdpDomainFixtures.Json(raw["result"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task MouseClickOnALabelActivatesItsControl()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await ClickElementAsync(ctx, 10, sid, "#explicit");
            await ClickElementAsync(ctx, 20, sid, "#implicit");
            JsonNode state = await StateAsync(ctx, 30, sid);
            Assert.True(state["a"]!.GetValue<bool>(), $"explicit for= label: {state}");
            Assert.True(state["b"]!.GetValue<bool>(), $"implicit nested label: {state}");
            Assert.Equal(
                "boxa:click:true,boxa:input:true,boxa:change:true,"
                + "boxb:click:true,boxb:input:true,boxb:change:true",
                state["events"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task MouseClickDeepInsideALabelActivatesItsControl()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await ClickElementAsync(ctx, 10, sid, "#deep-text");
            JsonNode state = await StateAsync(ctx, 20, sid);
            Assert.True(
                state["c"]!.GetValue<bool>(),
                $"click on a nested element resolves its label: {state}");
        }
    }

    [Fact]
    public async Task MouseClickOnALabelForADisabledControlDoesNothing()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await ClickElementAsync(ctx, 10, sid, "#off");
            await ClickElementAsync(ctx, 20, sid, "#boxd");
            JsonNode state = await StateAsync(ctx, 30, sid);
            Assert.False(state["d"]!.GetValue<bool>(), $"disabled control must not toggle: {state}");
            Assert.Equal(string.Empty, state["events"]!.GetValue<string>());
        }
    }
}
