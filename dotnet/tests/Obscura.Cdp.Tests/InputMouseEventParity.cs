using System.Globalization;
using System.Text.Json.Nodes;
using Obscura.Cdp;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/input_mouse_event_parity.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class InputMouseEventParity
{
    private static CdpTestServer ServeFixture() => CdpTestServer.ServeHtml(
        """
        <!doctype html><html><head><style>
                    html, body { margin: 0; }
                    #page { width: 1800px; height: 2400px; }
                    #box { position: absolute; left: 20px; top: 20px; width: 180px;
                           height: 120px; overflow: auto; border: 10px solid black; }
                    #inner { width: 700px; height: 800px; }
                </style></head><body>
                  <div id="page"></div>
                  <div id="box"><div id="inner"></div></div>
                  <input id="check" type="checkbox">
                  <form id="radio-form">
                    <input id="radio-a" type="radio" name="choice" checked>
                    <input id="radio-b" type="radio" name="choice">
                  </form>
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
        const string SessionId = "input-mouse-session";
        ctx.Sessions[SessionId] = pageId;
        await CdpAsync(
            ctx,
            1,
            "Page.navigate",
            $$"""{"url": "{{server.Url}}", "waitUntil": "load"}""",
            SessionId);
        return (ctx, SessionId, server);
    }

    private static Task WheelAsync(
        CdpContext ctx,
        ulong id,
        string sessionId,
        double x,
        double y,
        double deltaX,
        double deltaY) =>
        CdpAsync(
            ctx,
            id,
            "Input.dispatchMouseEvent",
            string.Create(
                CultureInfo.InvariantCulture,
                $$"""
                {"type": "mouseWheel", "x": {{x}}, "y": {{y}},
                 "deltaX": {{deltaX}}, "deltaY": {{deltaY}}}
                """),
            sessionId);

    private static async Task<JsonNode> ScrollStateAsync(CdpContext ctx, ulong id, string sessionId)
    {
        JsonNode result = await EvaluateAsync(
            ctx,
            id,
            """
            JSON.stringify({
                rootX: scrollX, rootY: scrollY,
                boxX: document.getElementById('box').scrollLeft,
                boxY: document.getElementById('box').scrollTop,
                rootScrollWidth: document.scrollingElement.scrollWidth,
                rootClientWidth: document.scrollingElement.clientWidth,
                pageRect: document.getElementById('page').getBoundingClientRect().toJSON(),
                maxBoxX: document.getElementById('box').scrollWidth - document.getElementById('box').clientWidth,
                maxBoxY: document.getElementById('box').scrollHeight - document.getElementById('box').clientHeight
            })
            """,
            sessionId);
        return CdpDomainFixtures.Json(result["result"]!["value"]!.GetValue<string>());
    }

    private static double Number(JsonNode? node) => node!.GetValue<double>();

    [Fact]
    public async Task WheelOverPageScrollsTheRootOnBothAxes()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await WheelAsync(ctx, 2, sid, 600.0, 300.0, 45.0, 160.0);
            JsonNode state = await ScrollStateAsync(ctx, 3, sid);
            Assert.Equal(45.0, Number(state["rootX"]));
            Assert.Equal(160.0, Number(state["rootY"]));
            Assert.Equal(0.0, Number(state["boxX"]));
            Assert.Equal(0.0, Number(state["boxY"]));
        }
    }

    [Fact]
    public async Task WheelOverNestedOverflowScrollsTheNestedContainer()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await WheelAsync(ctx, 2, sid, 50.0, 50.0, 70.0, 110.0);
            JsonNode state = await ScrollStateAsync(ctx, 3, sid);
            Assert.Equal(70.0, Number(state["boxX"]));
            Assert.Equal(110.0, Number(state["boxY"]));
            Assert.Equal(0.0, Number(state["rootX"]));
            Assert.Equal(0.0, Number(state["rootY"]));
        }
    }

    [Fact]
    public async Task WheelOffsetsClampToNestedScrollExtents()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await WheelAsync(ctx, 2, sid, 50.0, 50.0, 100000.0, 100000.0);
            JsonNode state = await ScrollStateAsync(ctx, 3, sid);
            Assert.Equal(Number(state["maxBoxX"]), Number(state["boxX"]));
            Assert.Equal(Number(state["maxBoxY"]), Number(state["boxY"]));

            await WheelAsync(ctx, 4, sid, 50.0, 50.0, -100000.0, -100000.0);
            state = await ScrollStateAsync(ctx, 5, sid);
            Assert.Equal(0.0, Number(state["boxX"]));
            Assert.Equal(0.0, Number(state["boxY"]));
        }
    }

    [Fact]
    public async Task WheelChainsToRootWhenNestedScrollerIsSaturated()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await EvaluateAsync(
                ctx,
                2,
                "(() => { const box = document.getElementById('box'); box.scrollTop = box.scrollHeight; })()",
                sid);
            JsonNode saturated = await ScrollStateAsync(ctx, 3, sid);
            Assert.Equal(Number(saturated["maxBoxY"]), Number(saturated["boxY"]));

            await WheelAsync(ctx, 4, sid, 50.0, 50.0, 0.0, 90.0);
            JsonNode state = await ScrollStateAsync(ctx, 5, sid);
            Assert.Equal(Number(state["maxBoxY"]), Number(state["boxY"]));
            Assert.Equal(90.0, Number(state["rootY"]));
        }
    }

    [Fact]
    public async Task CancelingWheelPreventsItsScrollDefault()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await EvaluateAsync(
                ctx,
                2,
                """
                (() => {
                    globalThis.wheelProbe = null;
                    const page = document.getElementById('page');
                    document.elementFromPoint = () => page;
                    page.addEventListener('wheel', event => {
                        wheelProbe = {
                            x: event.clientX, y: event.clientY,
                            dx: event.deltaX, dy: event.deltaY,
                            ctrl: event.ctrlKey, trusted: event.isTrusted
                        };
                        event.preventDefault();
                    });
                })()
                """,
                sid);
            await CdpAsync(
                ctx,
                3,
                "Input.dispatchMouseEvent",
                """
                {
                    "type": "mouseWheel", "x": 600.0, "y": 300.0,
                    "deltaX": 25.0, "deltaY": 75.0, "modifiers": 2
                }
                """,
                sid);
            JsonNode state = await ScrollStateAsync(ctx, 4, sid);
            Assert.Equal(0.0, Number(state["rootX"]));
            Assert.Equal(0.0, Number(state["rootY"]));

            JsonNode raw = await EvaluateAsync(ctx, 5, "JSON.stringify(wheelProbe)", sid);
            JsonNode probe = CdpDomainFixtures.Json(raw["result"]!["value"]!.GetValue<string>());
            Assert.Equal(600.0, Number(probe["x"]));
            Assert.Equal(300.0, Number(probe["y"]));
            Assert.Equal(25.0, Number(probe["dx"]));
            Assert.Equal(75.0, Number(probe["dy"]));
            Assert.True(probe["ctrl"]!.GetValue<bool>());
            Assert.True(probe["trusted"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task HitTestingClipsScrolledChildrenAtOverflowPaddingEdge()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            JsonNode raw = await EvaluateAsync(
                ctx,
                2,
                """
                (() => {
                    const box = document.getElementById('box');
                    box.scrollLeft = 50;
                    const inner = document.getElementById('inner').getBoundingClientRect();
                    return JSON.stringify({
                        hit: document.elementFromPoint(25, 50).id,
                        innerLeft: inner.left, innerRight: inner.right,
                        boxLeft: box.getBoundingClientRect().left
                    });
                })()
                """,
                sid);
            JsonNode result = CdpDomainFixtures.Json(raw["result"]!["value"]!.GetValue<string>());
            Assert.True(Number(result["innerLeft"]) <= 25.0);
            Assert.True(Number(result["innerRight"]) >= 25.0);
            Assert.Equal(20.0, Number(result["boxLeft"]));
            Assert.Equal("box", result["hit"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task PressReleaseOrdersEventsAndDefersClickActivation()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await EvaluateAsync(
                ctx,
                2,
                """
                (() => {
                    const target = document.getElementById('check');
                    document.elementFromPoint = () => target;
                    globalThis.mouseLog = [];
                    for (const type of ['mousedown', 'mouseup', 'click', 'input', 'change']) {
                        target.addEventListener(type, event => mouseLog.push({
                            type, checked: target.checked, x: event.clientX,
                            ctrl: event.ctrlKey, shift: event.shiftKey, trusted: event.isTrusted
                        }));
                    }
                })()
                """,
                sid);

            await CdpAsync(
                ctx,
                3,
                "Input.dispatchMouseEvent",
                """
                {
                    "type": "mousePressed", "x": 31.0, "y": 42.0,
                    "button": "left", "clickCount": 1, "modifiers": 10
                }
                """,
                sid);
            JsonNode pressedRaw = await EvaluateAsync(
                ctx,
                4,
                "JSON.stringify({log: mouseLog, checked: document.getElementById('check').checked})",
                sid);
            JsonNode pressed = CdpDomainFixtures.Json(
                pressedRaw["result"]!["value"]!.GetValue<string>());
            Assert.False(pressed["checked"]!.GetValue<bool>());
            Assert.Equal("mousedown", pressed["log"]![0]!["type"]!.GetValue<string>());
            Assert.Single(pressed["log"]!.AsArray());

            await CdpAsync(
                ctx,
                5,
                "Input.dispatchMouseEvent",
                """
                {
                    "type": "mouseReleased", "x": 31.0, "y": 42.0,
                    "button": "left", "clickCount": 1, "modifiers": 10
                }
                """,
                sid);
            JsonNode releasedRaw = await EvaluateAsync(
                ctx,
                6,
                "JSON.stringify({log: mouseLog, checked: document.getElementById('check').checked})",
                sid);
            JsonNode released = CdpDomainFixtures.Json(
                releasedRaw["result"]!["value"]!.GetValue<string>());
            List<string> types =
                [.. released["log"]!.AsArray().Select(entry => entry!["type"]!.GetValue<string>())];
            Assert.Equal(["mousedown", "mouseup", "click", "input", "change"], types);
            Assert.True(released["checked"]!.GetValue<bool>());
            Assert.True(released["log"]![2]!["checked"]!.GetValue<bool>());
            Assert.Equal(31.0, Number(released["log"]![2]!["x"]));
            Assert.True(released["log"]![2]!["ctrl"]!.GetValue<bool>());
            Assert.True(released["log"]![2]!["shift"]!.GetValue<bool>());
            Assert.True(released["log"]![2]!["trusted"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task RadioReleaseSelectsOnlyTheTargetInItsGroup()
    {
        (CdpContext ctx, string sid, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await EvaluateAsync(
                ctx,
                2,
                """
                (() => {
                    const a = document.getElementById('radio-a');
                    const b = document.getElementById('radio-b');
                    document.elementFromPoint = () => b;
                    globalThis.radioEvents = [];
                    for (const radio of [a, b]) {
                        for (const type of ['mousedown', 'mouseup', 'click', 'input', 'change']) {
                            radio.addEventListener(type, () => radioEvents.push(radio.id + ':' + type));
                        }
                    }
                })()
                """,
                sid);
            await CdpAsync(
                ctx,
                3,
                "Input.dispatchMouseEvent",
                """{"type": "mousePressed", "x": 10.0, "y": 10.0, "button": "left"}""",
                sid);
            await CdpAsync(
                ctx,
                4,
                "Input.dispatchMouseEvent",
                """{"type": "mouseReleased", "x": 10.0, "y": 10.0, "button": "left"}""",
                sid);
            JsonNode raw = await EvaluateAsync(
                ctx,
                5,
                "JSON.stringify({a: document.getElementById('radio-a').checked,"
                + " b: document.getElementById('radio-b').checked, events: radioEvents})",
                sid);
            JsonNode result = CdpDomainFixtures.Json(raw["result"]!["value"]!.GetValue<string>());
            Assert.False(result["a"]!.GetValue<bool>());
            Assert.True(result["b"]!.GetValue<bool>());
            CdpDomainFixtures.AssertJson(
                """
                ["radio-b:mousedown", "radio-b:mouseup", "radio-b:click", "radio-b:input",
                 "radio-b:change"]
                """,
                result["events"]);
        }
    }
}
