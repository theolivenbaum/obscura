using System.Text.Json.Nodes;

using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// A frame's own POST navigation, and a form whose <c>target</c> names an iframe.
/// </summary>
/// <remarks>
/// Chromium 141 (measured with Puppeteer 24): a POST form inside a frame navigates the
/// frame with the urlencoded body, <c>Content-Type: application/x-www-form-urlencoded</c>,
/// the frame's origin as <c>Origin</c>, the frame's URL as <c>Referer</c> and
/// <c>Sec-Fetch-Dest: iframe</c>; a form in the page with <c>target</c> naming an iframe
/// navigates that iframe, not the page. The port used to drop the first and navigate the
/// page for the second.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class ChildFramePostNavigation
{
    private const string Inner =
        "<html><body><h1 id=t>Inner</h1><form id=f method=post action='/submitted'>"
        + "<input name=a value='1 2'></form></body></html>";

    private static CoreCdpServer Serve() => CoreCdpServer.Routed(path => path switch
    {
        "/inner.html" => (Inner, "text/html", 200),
        "/submitted" or "/target" => ($"<html><body><h1 id=t>{path}</h1></body></html>", "text/html", 200),
        _ => ("<html><body><p>outer</p><iframe name=inner src=\"/inner.html\"></iframe>"
            + "<form id=tf method=post action='/target' target=inner><input name=q value='t v'></form>"
            + "</body></html>", "text/html", 200),
    });

    private static async Task<string> OpenAsync(CdpContext ctx, string url)
    {
        CdpResponse created = await CoreCdp.DispatchAsync(
            ctx, 900, "Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null);
        string targetId = created.Result!["targetId"].AsString()!;
        CdpResponse attached = await CoreCdp.DispatchAsync(
            ctx, 901, "Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, null);
        string session = attached.Result!["sessionId"].AsString()!;
        await CoreCdp.CdpAsync(ctx, 902, "Runtime.enable", new JsonObject(), session);
        await CoreCdp.CdpAsync(ctx, 50, "Page.navigate", new JsonObject { ["url"] = url }, session);
        await WaitForFrameUrlAsync(ctx, session, "/inner.html");
        return session;
    }

    private static async Task<string?> WaitForFrameUrlAsync(CdpContext ctx, string session, string suffix)
    {
        string? url = null;
        for (int attempt = 0; attempt < 60 && url?.EndsWith(suffix, StringComparison.Ordinal) != true; attempt++)
        {
            await ctx.GetSessionPageMut(session)!.RunAutonomousEventLoopTurnAsync();
            JsonNode tree = await CoreCdp.CdpAsync(ctx, 52, "Page.getFrameTree", new JsonObject(), session);
            url = tree["frameTree"]?["childFrames"] is JsonArray { Count: > 0 } children
                ? children[0]?["frame"]?["url"]?.GetValue<string>()
                : null;
            if (url?.EndsWith(suffix, StringComparison.Ordinal) != true)
            {
                await Task.Delay(50);
            }
        }

        return url;
    }

    private static long FrameMainContext(CdpContext ctx, string session) =>
        ctx.PendingEvents.Last(e => e.Method == "Runtime.executionContextCreated"
            && e.SessionId == session
            && e.Params.Get("context").Get("auxData").Get("isDefault").AsBool() == true
            && e.Params.Get("context").Get("origin").AsString() is { } origin
            && e.Params.Get("context").Get("auxData").Get("frameId").AsString() is { } frame
            && !frame.Equals(ctx.GetSessionPage(session)!.FrameId, StringComparison.Ordinal))
            .Params.Get("context").Get("id").AsI64()!.Value;

    private static string HeadFor(CoreCdpServer server, string requestLine) =>
        server.Heads.Last(head => head.StartsWith(requestLine, StringComparison.Ordinal));

    [Fact]
    public async Task AFramesOwnPostFormNavigatesTheFrame()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string session = await OpenAsync(ctx, server.Url);

        await CoreCdp.CdpAsync(ctx, 60, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = "document.getElementById('f').submit()",
            ["contextId"] = FrameMainContext(ctx, session),
        }, session);

        Assert.EndsWith("/submitted", await WaitForFrameUrlAsync(ctx, session, "/submitted"));
        string head = HeadFor(server, "POST /submitted ");
        string origin = server.Url.TrimEnd('/');
        Assert.Contains("content-type: application/x-www-form-urlencoded", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-length: 5", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"origin: {origin}\r\n", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"referer: {origin}/inner.html\r\n", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sec-fetch-dest: iframe", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sec-fetch-site: same-origin", head, StringComparison.OrdinalIgnoreCase);
        JsonNode page = await CoreCdp.EvalAsync(ctx, 61, "document.querySelector('p').textContent", session);
        Assert.Equal("outer", page["result"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFormTargetingAnIframeNavigatesThatFrame()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = Serve();
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string session = await OpenAsync(ctx, server.Url);

        await CoreCdp.EvalAsync(ctx, 60, "document.getElementById('tf').submit()", session);

        Assert.EndsWith("/target", await WaitForFrameUrlAsync(ctx, session, "/target"));
        string head = HeadFor(server, "POST /target ");
        string origin = server.Url.TrimEnd('/');
        Assert.Contains("content-type: application/x-www-form-urlencoded", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"origin: {origin}\r\n", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"referer: {origin}/\r\n", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sec-fetch-dest: iframe", head, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(server.Url, ctx.GetSessionPage(session)!.UrlString());
    }
}
