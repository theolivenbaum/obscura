using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// Where typed text lands in a field, as Chromium 141 puts it: after <c>focus()</c> the caret
/// is at the start (Puppeteer's <c>type()</c> writes "Xpre"), and a mouse press focuses the
/// field and a click past its text puts the caret at the end (click, then keys, writes
/// "preX"). The port used to append in both cases, and a press never moved focus.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class InputCaretPlacement
{
    private const string Page = "data:text/html,<html><body style='margin:0'>"
        + "<input id=a value=pre style='display:block;width:200px;height:20px'>"
        + "<input id=b value=pre style='display:block;width:200px;height:20px'></body></html>";

    private static async Task<string> ValueAsync(CdpContext ctx, string session, string id) =>
        (await CoreCdp.EvalAsync(ctx, 80, $"document.getElementById('{id}').value", session))["result"]!["value"]!.GetValue<string>();

    [Fact]
    public async Task TypingAfterFocusInsertsAtTheStart()
    {
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(Page);
        using IDisposable owned = CoreCdp.Owned(ctx);
        await CoreCdp.EvalAsync(ctx, 2, "document.getElementById('a').focus()", session);
        await CoreCdp.CdpAsync(ctx, 3, "Input.insertText", new JsonObject { ["text"] = "X" }, session);
        await CoreCdp.CdpAsync(ctx, 4, "Input.insertText", new JsonObject { ["text"] = "Y" }, session);
        Assert.Equal("XYpre", await ValueAsync(ctx, session, "a"));
    }

    [Fact]
    public async Task AClickFocusesTheFieldWithTheCaretAtTheEnd()
    {
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(Page);
        using IDisposable owned = CoreCdp.Owned(ctx);
        foreach (string type in new[] { "mousePressed", "mouseReleased" })
        {
            await CoreCdp.CdpAsync(ctx, 5, "Input.dispatchMouseEvent", new JsonObject
            {
                ["type"] = type, ["x"] = 150, ["y"] = 30, ["button"] = "left", ["clickCount"] = 1,
            }, session);
        }

        JsonNode active = await CoreCdp.EvalAsync(ctx, 6, "document.activeElement.id", session);
        Assert.Equal("b", active["result"]!["value"]!.GetValue<string>());
        await CoreCdp.CdpAsync(ctx, 7, "Input.insertText", new JsonObject { ["text"] = "X" }, session);
        Assert.Equal("preX", await ValueAsync(ctx, session, "b"));
        Assert.Equal("pre", await ValueAsync(ctx, session, "a"));
    }
}
