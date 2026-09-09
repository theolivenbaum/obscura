using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Mcp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-mcp/src/lib.rs</c>.
/// </summary>
public sealed class LibTests
{
    private static JsonArray ListedTools() =>
        (JsonArray)McpServer.HandleToolsList(JsonNode.Parse("1")).Result!["tools"]!;

    private static JsonNode? Find(JsonArray tools, string name)
    {
        foreach (var tool in tools)
        {
            if (tool!["name"]!.GetValue<string>() == name)
            {
                return tool;
            }
        }

        return null;
    }

    [Fact]
    public void ToolSchemasExposeSnapshotLimitWithoutNestedProperties()
    {
        var tools = ListedTools();
        var snapshot = Find(tools, "browser_snapshot");
        Assert.NotNull(snapshot);
        Assert.Equal("number", snapshot["inputSchema"]!["properties"]!["max_chars"]!["type"]!.GetValue<string>());
        foreach (var tool in tools)
        {
            Assert.True(
                tool!["inputSchema"]!["properties"]!["properties"] is null,
                $"{tool["name"]} has a nested duplicate properties object");
        }
    }

    // Ported but inapplicable, not unwritten. The Rust test is
    // `#[cfg(not(feature = "render"))]`, and the C# port has no non-render build:
    // rendering is always compiled in (see the note in ToolSchemas), so
    // browser_screenshot / browser_pdf are always advertised. Writing the body would
    // assert the opposite of the port's documented behavior rather than find a bug.
    // The Rust body, verbatim:
    //
    //     let tools = listed_tools();
    //     assert!(tools.iter().all(|tool| {
    //         tool["name"] != "browser_screenshot" && tool["name"] != "browser_pdf"
    //     }));
    //
    // RenderToolsAreAdvertisedWithFlatSchemas below is the arm that does apply.
    [Fact(Skip = "cfg(not(feature = \"render\")): the C# port always compiles rendering in, so there is no build in which the render tools are absent")]
    public void RenderToolsAreNotAdvertisedWithoutRenderFeature()
    {
        var tools = ListedTools();
        Assert.All(tools, tool =>
        {
            Assert.NotEqual("browser_screenshot", tool!["name"]!.GetValue<string>());
            Assert.NotEqual("browser_pdf", tool["name"]!.GetValue<string>());
        });
    }

    [Fact]
    public void RenderToolsAreAdvertisedWithFlatSchemas()
    {
        var tools = ListedTools();
        foreach (var name in new[] { "browser_screenshot", "browser_pdf" })
        {
            var tool = Find(tools, name);
            Assert.True(tool is not null, $"missing {name}");
            Assert.Equal("object", tool!["inputSchema"]!["type"]!.GetValue<string>());
            Assert.False(tool["inputSchema"]!["additionalProperties"]!.GetValue<bool>());
            Assert.IsType<JsonObject>(tool["inputSchema"]!["properties"]);
            Assert.True(tool["inputSchema"]!["properties"]!["properties"] is null);
        }
    }

    [Fact]
    public async Task RenderToolCallsReturnMcpBinaryContentAndRejectBadOptions()
    {
        using var state = new BrowserState(null, null, false);
        await state.PageMut().NavigateAsync(
            "data:text/html,<html style='margin:0'><body style='margin:0;background:red'><div style='width:64px;height:48px'></div></body></html>");
        state.PageMut().SetViewport((64.0f, 48.0f));

        var screenshot = (await McpServer.HandleToolCallAsync(
            JsonNode.Parse("1"),
            JsonNode.Parse("""{ "name": "browser_screenshot", "arguments": {} }"""),
            state)).Result;
        Assert.NotNull(screenshot);
        var image = screenshot["content"]![0]!;
        Assert.Equal("image", image["type"]!.GetValue<string>());
        Assert.Equal("image/png", image["mimeType"]!.GetValue<string>());
        var png = Convert.FromBase64String(image["data"]!.GetValue<string>());
        // b"\x89PNG\r\n\x1a\n"; a u8 literal would UTF-8 encode 0x89 as two bytes.
        byte[] pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.True(png.AsSpan().StartsWith(pngMagic));

        var pdf = (await McpServer.HandleToolCallAsync(
            JsonNode.Parse("2"),
            JsonNode.Parse("""{ "name": "browser_pdf", "arguments": { "print_background": true } }"""),
            state)).Result;
        Assert.NotNull(pdf);
        var resource = pdf["content"]![0]!;
        Assert.Equal("resource", resource["type"]!.GetValue<string>());
        Assert.Equal("application/pdf", resource["resource"]!["mimeType"]!.GetValue<string>());
        var bytes = Convert.FromBase64String(resource["resource"]!["blob"]!.GetValue<string>());
        Assert.True(bytes.AsSpan().StartsWith("%PDF-"u8));

        var invalidScreenshot = (await McpServer.HandleToolCallAsync(
            JsonNode.Parse("3"),
            JsonNode.Parse("""{ "name": "browser_screenshot", "arguments": { "width": 0 } }"""),
            state)).Result;
        Assert.NotNull(invalidScreenshot);
        Assert.True(invalidScreenshot["isError"]!.GetValue<bool>());

        var invalidPdf = (await McpServer.HandleToolCallAsync(
            JsonNode.Parse("4"),
            JsonNode.Parse("""{ "name": "browser_pdf", "arguments": { "scale": 3 } }"""),
            state)).Result;
        Assert.NotNull(invalidPdf);
        Assert.True(invalidPdf["isError"]!.GetValue<bool>());
    }

    /// <summary>
    /// Records the method, path and body of every request it serves, so a test can
    /// assert what actually left the process rather than what the page believes
    /// happened. Serves the issue's form at <c>/</c> and 200s everything else.
    /// </summary>
    private static (string Base, BlockingCollection<string> Requests) SpawnFormRecordingServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var baseUrl = $"http://127.0.0.1:{endpoint.Port}";
        var requests = new BlockingCollection<string>();
        var thread = new Thread(() =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var buf = new byte[8192];
                    var read = 0;
                    try
                    {
                        read = stream.Read(buf, 0, buf.Length);
                    }
                    catch (IOException)
                    {
                        // Fall through with what we have, as the Rust fixture does.
                    }

                    var raw = Encoding.UTF8.GetString(buf, 0, read);
                    var start = raw.Split('\n')[0].Trim('\r');
                    var parts = start.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var method = parts.Length > 0 ? parts[0] : string.Empty;
                    var path = parts.Length > 1 ? parts[1] : string.Empty;
                    var split = raw.Split("\r\n\r\n", 2);
                    var body = split.Length > 1 ? split[1] : string.Empty;
                    requests.Add($"{method} {path} body='{body}'");
                    const string page =
                        "<!doctype html><meta charset=utf-8>"
                        + "<form id=f method=POST action=/submitted>"
                        + "<input name=q id=q value=><button type=submit id=go>Envoyer</button></form>";
                    var response =
                        "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n"
                        + $"Content-Length: {Encoding.UTF8.GetByteCount(page)}\r\nConnection: close\r\n\r\n"
                        + page;
                    try
                    {
                        stream.Write(Encoding.UTF8.GetBytes(response));
                        stream.Flush();
                    }
                    catch (IOException)
                    {
                        // The client hung up; nothing else to do.
                    }
                }
            }
        })
        { IsBackground = true };
        thread.Start();
        return (baseUrl, requests);
    }

    private static string Recv(BlockingCollection<string> requests, string because)
    {
        Assert.True(requests.TryTake(out var value, TimeSpan.FromSeconds(5)), because);
        return value!;
    }

    [Fact]
    public async Task ClickOnASubmitButtonIssuesTheRequestBeforeReplying()
    {
        // A submit click runs the page's form glue and leaves the navigation as
        // pending state; it only becomes a request when a driving layer converts it.
        // The CDP path converts it, MCP did not, so the same click POSTed over CDP
        // and did nothing over MCP (#618). Worse than doing nothing:
        // `location.href` already reported the destination, so an agent was told the
        // submit had happened while no request had left the process.
        //
        // The assertion is deliberately at the wire, not on `location.href`, because
        // the URL was the thing that lied.
        var (baseUrl, requests) = SpawnFormRecordingServer();
        using var state = new BrowserState(null, null, false);
        await state.PageMut().NavigateAsync(baseUrl);
        Assert.StartsWith(
            "GET /",
            Recv(requests, "the form page itself must be fetched"),
            StringComparison.Ordinal);

        await Tools.FillAsync(JsonNode.Parse("""{ "selector": "#q", "value": "hello" }"""), state);
        await Tools.ClickAsync(JsonNode.Parse("""{ "selector": "#go" }"""), state);

        var submitted = Recv(
            requests, "the submit must reach the server before browser_click replies");
        Assert.True(
            submitted.StartsWith("POST /submitted", StringComparison.Ordinal),
            $"expected a POST to the form action, got {submitted}");
        Assert.True(
            submitted.Contains("q=hello", StringComparison.Ordinal),
            $"the submitted body must carry the filled field, got {submitted}");
    }

    [Fact]
    public async Task FillToolsNotifyControlledInputTracker()
    {
        using var state = new BrowserState(null, null, false);
        await state.PageMut().NavigateAsync("data:text/html,<div id=root><input id=field></div>");

        state.PageMut().Evaluate(
            """
            (function () {
                var root = document.getElementById('root');
                var input = document.getElementById('field');
                var descriptor = Object.getOwnPropertyDescriptor(input.constructor.prototype, 'value');
                var tracked = String(input.value);
                Object.defineProperty(input, 'value', {
                    configurable: true,
                    get: function () { return descriptor.get.call(this); },
                    set: function (value) {
                        tracked = String(value);
                        descriptor.set.call(this, value);
                    }
                });
                window.__controlledState = '';
                window.__controlledUpdates = 0;
                window.__lastInputTarget = '';
                window.__lastInputTrusted = false;
                root.addEventListener('input', function (event) {
                    window.__lastInputTarget = event.target.id;
                    window.__lastInputTrusted = event.isTrusted;
                    var next = String(event.target.value);
                    if (next !== tracked) {
                        tracked = next;
                        window.__controlledState = next;
                        window.__controlledUpdates++;
                    }
                });
            })()
            """);

        await Tools.FillAsync(JsonNode.Parse("""{ "selector": "#field", "value": "filled" }"""), state);
        await Tools.TypeAsync(JsonNode.Parse("""{ "selector": "#field", "text": "-typed" }"""), state);
        Tools.FillForm(
            JsonNode.Parse("""{ "fields": [{ "selector": "#field", "value": "form-filled" }] }"""),
            state);

        var actual = state.PageMut().Evaluate(
            """
            JSON.stringify({
                domValue: document.getElementById('field').value,
                controlledState: window.__controlledState,
                controlledUpdates: window.__controlledUpdates,
                lastInputTarget: window.__lastInputTarget,
                lastInputTrusted: window.__lastInputTrusted
            })
            """);
        Assert.Equal(
            """{"domValue":"form-filled","controlledState":"form-filled","controlledUpdates":3,"lastInputTarget":"field","lastInputTrusted":true}""",
            actual!.GetValue<string>());
    }

    [Fact]
    public async Task FillFormCheckAndSelectUseNativeSetterAndTrustedEvents()
    {
        using var state = new BrowserState(null, null, false);
        await state.PageMut().NavigateAsync(
            "data:text/html,<div id=root><input id=box type=checkbox>"
            + "<select id=sel><option value=a>a</option><option value=b>b</option></select></div>");

        // Install a React-style tracker on the checkbox's `checked`, redefined on the
        // instance. A direct `el.checked = true` runs this wrapper in lockstep, so a
        // change handler comparing target.checked to the tracked value sees no change
        // and never commits. Writing through the prototype setter (what
        // __obscura_setFieldValue does) leaves the tracker stale so the edit
        // registers. Also record whether the dispatched change is trusted.
        state.PageMut().Evaluate(
            """
            (function () {
                var box = document.getElementById('box');
                var root = document.getElementById('root');
                var d = Object.getOwnPropertyDescriptor(box.constructor.prototype, 'checked');
                var tracked = box.checked;
                Object.defineProperty(box, 'checked', {
                    configurable: true,
                    get: function () { return d.get.call(this); },
                    set: function (v) { tracked = !!v; d.set.call(this, v); }
                });
                window.__checkedCommitted = false;
                window.__checkTrusted = false;
                window.__selectTrusted = false;
                root.addEventListener('change', function (event) {
                    if (event.target.id === 'box') {
                        window.__checkTrusted = event.isTrusted;
                        if (event.target.checked !== tracked) {
                            tracked = event.target.checked;
                            window.__checkedCommitted = true;
                        }
                    } else if (event.target.id === 'sel') {
                        window.__selectTrusted = event.isTrusted;
                    }
                });
            })()
            """);

        Tools.FillForm(
            JsonNode.Parse(
                """
                {
                    "fields": [
                        { "selector": "#box", "type": "check" },
                        { "selector": "#sel", "type": "select", "value": "b" }
                    ]
                }
                """),
            state);

        var actual = state.PageMut().Evaluate(
            """
            JSON.stringify({
                domChecked: document.getElementById('box').checked,
                checkedCommitted: window.__checkedCommitted,
                checkTrusted: window.__checkTrusted,
                selValue: document.getElementById('sel').value,
                selectTrusted: window.__selectTrusted
            })
            """);
        Assert.Equal(
            """{"domChecked":true,"checkedCommitted":true,"checkTrusted":true,"selValue":"b","selectTrusted":true}""",
            actual!.GetValue<string>());
    }
}
