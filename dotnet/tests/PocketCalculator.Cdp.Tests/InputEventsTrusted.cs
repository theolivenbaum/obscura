using System.Globalization;
using System.Text.Json.Nodes;
using PocketCalculator.Cdp;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// CDP input still produces trusted events once the shim's host helpers are hidden from
/// page script, and page script still cannot produce one.
/// </summary>
/// <remarks>
/// DEVIATION from the Rust engine, whose input snippets call the page-visible
/// <c>globalThis.__obscura_markTrusted</c>; here they run through
/// <c>Page.EvaluateHost</c> and reach it as <c>__obscura_host.markTrusted</c>.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class InputEventsTrusted
{
    private const string SessionId = "input-trusted-session";

    private static CdpTestServer ServeFixture() => CdpTestServer.ServeHtml(
        """
        <!doctype html><html><head><style>
          html, body { margin: 0; }
          #btn { position: absolute; left: 10px; top: 10px; width: 100px; height: 40px; }
          #field { position: absolute; left: 10px; top: 80px; }
        </style></head><body>
          <button id="btn">go</button>
          <input id="field">
          <input id="file" type="file">
          <script>
            window.__log = [];
            for (const type of ['mousedown', 'mouseup', 'click', 'keydown', 'keyup', 'input', 'change']) {
              document.addEventListener(type, (e) => window.__log.push(
                e.type + ':' + e.isTrusted + ':' + ((e.target && e.target.id) || '')), true);
            }
          </script>
        </body></html>
        """);

    private static async Task<JsonNode> CdpAsync(CdpContext ctx, ulong id, string method, string parameters)
    {
        CdpResponse response = await Dispatcher.DispatchAsync(
            new CdpRequest
            {
                Id = id,
                Method = method,
                Params = CdpDomainFixtures.Json(parameters),
                SessionId = SessionId,
            },
            ctx);
        Assert.True(response.Error is null, $"CDP {method} failed: {response.Error?.Message}");
        return response.Result ?? new JsonObject();
    }

    private static async Task<string> EvaluateStringAsync(CdpContext ctx, ulong id, string expression)
    {
        JsonNode result = await CdpAsync(
            ctx,
            id,
            "Runtime.evaluate",
            $$"""{"expression": {{CdpJson.String(expression)}}, "returnByValue": true}""");
        return result["result"]!["value"]!.GetValue<string>();
    }

    private static async Task<(CdpContext Ctx, CdpTestServer Server)> SetupAsync()
    {
        CdpTestServer server = ServeFixture();
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        ctx.Sessions[SessionId] = pageId;
        await CdpAsync(ctx, 1, "Page.navigate", $$"""{"url": "{{server.Url}}", "waitUntil": "load"}""");
        return (ctx, server);
    }

    private static Task<string> TakeLogAsync(CdpContext ctx, ulong id) =>
        EvaluateStringAsync(ctx, id, "(() => { const l = window.__log.join(','); window.__log = []; return l; })()");

    private static string Mouse(string type, double x, double y) => string.Create(
        CultureInfo.InvariantCulture,
        $$"""{"type": "{{type}}", "x": {{x}}, "y": {{y}}, "button": "left", "clickCount": 1}""");

    [Fact]
    public async Task DispatchMouseEventProducesTrustedEvents()
    {
        (CdpContext ctx, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await CdpAsync(ctx, 2, "Input.dispatchMouseEvent", Mouse("mousePressed", 50, 30));
            await CdpAsync(ctx, 3, "Input.dispatchMouseEvent", Mouse("mouseReleased", 50, 30));
            string log = await TakeLogAsync(ctx, 4);
            Assert.Contains("mousedown:true:btn", log, StringComparison.Ordinal);
            Assert.Contains("mouseup:true:btn", log, StringComparison.Ordinal);
            Assert.Contains("click:true:btn", log, StringComparison.Ordinal);
            Assert.DoesNotContain(":false:", log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DispatchKeyEventAndInsertTextProduceTrustedEvents()
    {
        (CdpContext ctx, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            await EvaluateStringAsync(ctx, 2, "(() => { document.getElementById('field').focus(); window.__log = []; return ''; })()");
            await CdpAsync(ctx, 3, "Input.dispatchKeyEvent", """{"type": "keyDown", "key": "a", "code": "KeyA"}""");
            await CdpAsync(ctx, 4, "Input.dispatchKeyEvent", """{"type": "keyUp", "key": "a", "code": "KeyA"}""");
            await CdpAsync(ctx, 5, "Input.insertText", """{"text": "hi"}""");
            string log = await TakeLogAsync(ctx, 6);
            Assert.Contains("keydown:true:field", log, StringComparison.Ordinal);
            Assert.Contains("keyup:true:field", log, StringComparison.Ordinal);
            Assert.Contains("input:true:field", log, StringComparison.Ordinal);
            Assert.DoesNotContain(":false:", log, StringComparison.Ordinal);
            Assert.Equal("hi", await EvaluateStringAsync(ctx, 7, "document.getElementById('field').value"));
        }
    }

    [Fact]
    public async Task SetFileInputFilesFiresTrustedInputAndChange()
    {
        (CdpContext ctx, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            ctx.GetSessionPageMut(SessionId)!.Context.AllowFileAccess = true;
            string path = Path.Combine(Path.GetTempPath(), $"obscura-upload-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(path, "hello", TestContext.Current.CancellationToken);
            try
            {
                JsonNode doc = await CdpAsync(ctx, 2, "DOM.getDocument", "{}");
                JsonNode found = await CdpAsync(
                    ctx,
                    3,
                    "DOM.querySelector",
                    $$"""{"nodeId": {{doc["root"]!["nodeId"]!.ToJsonString()}}, "selector": "#file"}""");
                await CdpAsync(
                    ctx,
                    4,
                    "DOM.setFileInputFiles",
                    $$"""{"nodeId": {{found["nodeId"]!.ToJsonString()}}, "files": [{{CdpJson.String(path)}}]}""");
                string log = await TakeLogAsync(ctx, 5);
                Assert.Equal("input:true:file,change:true:file", log);
                Assert.Equal(
                    Path.GetFileName(path),
                    await EvaluateStringAsync(ctx, 6, "document.getElementById('file').files[0].name"));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Through Runtime.evaluate, which is how page-level code arrives from a CDP client,
    /// none of the former helper globals exists and a page-built event stays untrusted.
    /// </summary>
    [Fact]
    public async Task PageScriptCannotMarkItsOwnEventTrusted()
    {
        (CdpContext ctx, CdpTestServer server) = await SetupAsync();
        using (server)
        {
            string result = await EvaluateStringAsync(ctx, 2, """
                (() => {
                  const names = ['__obscura_markTrusted', '__obscura_setFieldValue', '__obscura_setInputFiles',
                    '__obscura_deliverMessage', '__obscura_activateLabel', '__obscura_host',
                    '__obscura_host_handoff', '__obscura_mouse_down'];
                  const present = names.filter((n) => n in globalThis);
                  const ev = new Event('click', { bubbles: true });
                  for (const n of names) { try { globalThis[n](ev); } catch (_) {} }
                  document.getElementById('btn').dispatchEvent(ev);
                  return JSON.stringify({ present, trusted: ev.isTrusted });
                })()
                """);
            Assert.Equal("""{"present":[],"trusted":false}""", result);
            Assert.Equal("click:false:btn", await TakeLogAsync(ctx, 3));
        }
    }
}
