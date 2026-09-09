// `Input.dispatchKeyEvent` interpolates the `key`/`code` params into a generated
// `KeyboardEvent(...)` snippet. They must be escaped for BOTH backslash and single-quote
// (issue #433): Chrome sends `key: "\\"` (U+005C) when the backslash key is pressed, and
// quote-only escaping turns that into `key:'\'` - the backslash escapes the closing quote, the
// literal runs on, and the whole `page.evaluate` is a syntax error, so the `keydown` is silently
// never dispatched. Regression test: the backslash key must arrive.
using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Cdp;
using Xunit;

namespace Obscura.Cdp.Tests;

[Collection(CdpDomainCollection.Name)]
public sealed class InputKeyEventEscaping
{
    // Serves a page that records the `key` of the last keydown event on the body.
    private static CdpTestServer ServePage() => CdpTestServer.ServeHtml(
        """
        <html><body>
        <input id="i">
        <textarea id="a"></textarea>
        <script>
        window.__keys = [];
        document.body.addEventListener('keydown', function (e) { window.__keys.push(e.key + '|' + e.code); });
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

    [Fact]
    public async Task DispatchKeyEventEscapesBackslashInKeyAndCode()
    {
        using CdpTestServer server = ServePage();
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        const string SessionId = "session-1";
        ctx.Sessions[SessionId] = pageId;

        await CdpAsync(
            ctx,
            1,
            "Page.navigate",
            $$"""{"url": "{{server.Url}}", "waitUntil": "load"}""",
            SessionId);

        // The backslash key: Chrome sends key="\" (a single backslash) code="Backslash".
        await CdpAsync(
            ctx,
            2,
            "Input.dispatchKeyEvent",
            """{"type": "keyDown", "key": "\\", "code": "Backslash"}""",
            SessionId);

        // A key whose name itself contains a quote AND a backslash, to exercise the ordering of
        // the two replacements.
        await CdpAsync(
            ctx,
            3,
            "Input.dispatchKeyEvent",
            """{"type": "keyDown", "key": "a", "code": "KeyA"}""",
            SessionId);

        JsonNode result = await CdpAsync(
            ctx,
            4,
            "Runtime.evaluate",
            """{"expression": "JSON.stringify(window.__keys)", "returnByValue": true}""",
            SessionId);

        string[] keys = JsonSerializer.Deserialize<string[]>(
            result["result"]!["value"]!.GetValue<string>())!;
        Assert.Equal(
            ["\\|Backslash", "a|KeyA"],
            keys);
    }

    // The same hazard one layer over: on the inserted *text* rather than on the key name.
    // `InsertTextJs` interpolates the text into the same kind of literal. #433 escaped the
    // backslash and the quote and stopped there, but a raw newline ends a JS string literal just
    // as a stray quote does, so the snippet is a syntax error and the character is silently
    // dropped.
    //
    // The `char` type is the path that reaches it with a newline: the `keyDown` branch skips
    // insertion for "\r" and "\n" because `key == "Enter"` handles those, and `char` has no such
    // guard, so a client entering a line break in a textarea this way loses it with no error
    // reported anywhere.
    [Fact]
    public async Task DispatchKeyEventCharCarriesANewlineIntoATextarea()
    {
        using CdpTestServer server = ServePage();
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        const string SessionId = "session-1";
        ctx.Sessions[SessionId] = pageId;

        await CdpAsync(
            ctx,
            1,
            "Page.navigate",
            $$"""{"url": "{{server.Url}}", "waitUntil": "load"}""",
            SessionId);
        await CdpAsync(
            ctx,
            2,
            "Runtime.evaluate",
            """
            {
                "expression": "(function () { document.getElementById('a').focus(); return 'ok'; })()",
                "returnByValue": true
            }
            """,
            SessionId);

        string[] characters = ["a", "\n", "b"];
        ulong id = 3;
        foreach (string character in characters)
        {
            await CdpAsync(
                ctx,
                id++,
                "Input.dispatchKeyEvent",
                $$"""{"type": "char", "text": {{CdpJson.String(character)}}}""",
                SessionId);
        }

        JsonNode result = await CdpAsync(
            ctx,
            6,
            "Runtime.evaluate",
            """
            {
                "expression": "JSON.stringify(document.getElementById('a').value)",
                "returnByValue": true
            }
            """,
            SessionId);
        Assert.Equal(
            "\"a\\nb\"",
            result["result"]?["value"]?.GetValue<string>() ?? string.Empty);
    }

    // #577: Playwright's fill() focuses the field in page and then types the whole value with one
    // Input.insertText call. The method must exist and drive the same snippet the key-event text
    // path uses, so quotes, backslashes, and newlines survive intact.
    [Fact]
    public async Task InsertTextTypesIntoTheFocusedField()
    {
        using CdpTestServer server = ServePage();
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        const string SessionId = "session-1";
        ctx.Sessions[SessionId] = pageId;

        await CdpAsync(
            ctx,
            1,
            "Page.navigate",
            $$"""{"url": "{{server.Url}}", "waitUntil": "load"}""",
            SessionId);
        await CdpAsync(
            ctx,
            2,
            "Runtime.evaluate",
            """{"expression": "document.getElementById('i').focus()", "returnByValue": true}""",
            SessionId);
        await CdpAsync(
            ctx,
            3,
            "Input.insertText",
            $$"""{"text": {{CdpJson.String("he'll\\o\nbye")}}}""",
            SessionId);

        JsonNode result = await CdpAsync(
            ctx,
            4,
            "Runtime.evaluate",
            """
            {
                "expression": "JSON.stringify(document.getElementById('i').value)",
                "returnByValue": true
            }
            """,
            SessionId);
        Assert.Equal(
            "\"he'll\\\\o\\nbye\"",
            result["result"]?["value"]?.GetValue<string>() ?? string.Empty);
    }
}
