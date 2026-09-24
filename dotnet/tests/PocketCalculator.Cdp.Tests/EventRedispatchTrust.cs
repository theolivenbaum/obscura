using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// Page script cannot keep a trusted event trusted by dispatching it again (SECURITY.md L10).
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, where an event the host marked trusted stays trusted for
/// any later dispatch. DOM's dispatchEvent() sets isTrusted to false. Measured in Chromium
/// with Playwright's build and the same page: the listener's nested re-dispatch throws
/// InvalidStateError, a later re-dispatch reaches listeners with isTrusted false, and the
/// event reads false afterwards.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class EventRedispatchTrust
{
    private const string SessionId = "event-redispatch-trust";

    private const string Body = """
        <!doctype html><html><head><style>
          html, body { margin: 0; }
          #b { position: absolute; left: 0; top: 0; width: 100px; height: 40px; }
        </style></head><body>
          <button id=b>b</button><div id=d>d</div>
          <script>
            window.log = [];
            let saved = null;
            b.addEventListener('click', e => {
              log.push('first:' + e.isTrusted); saved = e;
              try { d.dispatchEvent(e); log.push('nested-ok'); } catch (err) { log.push('nested:' + err.name); }
            });
            d.addEventListener('click', e => { log.push('d:' + e.isTrusted); });
            window.redispatch = () => { d.dispatchEvent(saved); log.push('after:' + saved.isTrusted); };
          </script>
        </body></html>
        """;

    private static async Task<JsonNode> CdpAsync(CdpContext ctx, ulong id, string method, JsonObject parameters)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest { Id = id, Method = method, Params = parameters, SessionId = SessionId },
            ctx);
        Assert.True(response.Error is null, $"CDP {method} failed: {response.Error?.Message}");
        return response.Result ?? new JsonObject();
    }

    private static JsonObject Mouse(string type) => (JsonObject)JsonNode.Parse(string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"type": "{{type}}", "x": 50, "y": 20, "button": "left", "clickCount": 1}"""))!;

    [Fact]
    public async Task RedispatchedTrustedEventIsUntrusted()
    {
        using CdpTestServer server = CdpTestServer.ServeHtml(Body);
        var ctx = CdpContext.New();
        ctx.Sessions[SessionId] = ctx.CreatePage();
        await CdpAsync(ctx, 1, "Page.navigate", new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" });

        await CdpAsync(ctx, 2, "Input.dispatchMouseEvent", Mouse("mousePressed"));
        await CdpAsync(ctx, 3, "Input.dispatchMouseEvent", Mouse("mouseReleased"));
        JsonNode result = await CdpAsync(ctx, 4, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = "redispatch(); log.join(' ')",
            ["returnByValue"] = true,
        });
        Assert.Equal("first:true nested:InvalidStateError d:false after:false", result["result"]!["value"]!.GetValue<string>());
    }
}
