using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// reddit.com's shape over a real CDP connection: a page task the event loop's watchdog
/// has to interrupt, and a load-delaying dynamic script whose execution was queued behind
/// it. <c>Page.navigate</c> went unanswered (the interrupt escaped as a cancellation and the
/// connection processor took it for its own shutdown), or answered only once the script
/// deadline gave up on the dropped script; and nothing after it was answered either.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class InterruptedTaskNavigation
{
    private static string Document()
    {
        var html = new StringBuilder(
            """
            <!doctype html><html><head><script>
            const s = document.createElement('script');
            s.src = '/dyn.js';
            document.head.appendChild(s);
            setTimeout(() => {
              const box = document.getElementById('box');
              const stop = Date.now() + 60000;
              for (let i = 0; Date.now() < stop; i++) {
                box.style.width = (i % 200) + 'px';
                box.getBoundingClientRect();
              }
            }, 0);
            const until = Date.now() + 500;
            while (Date.now() < until) {}
            </script></head><body><div id=box>
            """);
        for (int i = 0; i < 400; i++)
        {
            html.Append("<p style=\"float:left;padding:1px\">paragraph ").Append(i).Append(" with some words in it</p>");
        }

        return html.Append("</div></body></html>").ToString();
    }

    [Fact]
    public async Task NavigateAnswersAndTheConnectionKeepsServing()
    {
        CoreCdp.AllowLoopback();
        string document = Document();
        using CoreCdpServer site = CoreCdpServer.Advanced(path =>
            path.StartsWith("/dyn.js", StringComparison.Ordinal)
                ? new CoreCdpServer.Reply("window.__dynamicRan = true;", "application/javascript", 200)
                : new CoreCdpServer.Reply(document, "text/html", 200));
        await using CdpServerHandle server = await CdpServerHandle.StartAsync();
        using ClientWebSocket ws = await CdpTestClient.ConnectAsync(server.Port);

        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 1,
            ["method"] = "Target.createTarget",
            ["params"] = new JsonObject { ["url"] = "about:blank" },
        });
        string? sessionId = null;
        using (var createDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (sessionId is null)
            {
                string text = await CdpTestClient.ReceiveTextAsync(ws, createDeadline.Token)
                    ?? throw new IOException("ws closed");
                sessionId = CdpJson.Parse(text).Get("params").Get("sessionId").AsString();
            }
        }

        var clock = Stopwatch.StartNew();
        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 2,
            ["method"] = "Page.navigate",
            ["sessionId"] = sessionId,
            ["params"] = new JsonObject { ["url"] = site.Url },
        });
        JsonNode? navigated = await CdpTestClient.AwaitResponseAsync(ws, 2, TimeSpan.FromSeconds(60));
        TimeSpan took = clock.Elapsed;
        Assert.NotNull(navigated.Get("result").Get("loaderId").AsString());
        Assert.True(took < TimeSpan.FromSeconds(25), $"Page.navigate took {took} (the script deadline is 30s)");

        await CdpTestClient.SendAsync(ws, new JsonObject
        {
            ["id"] = 3,
            ["method"] = "Runtime.evaluate",
            ["sessionId"] = sessionId,
            ["params"] = new JsonObject
            {
                ["expression"] = "[window.__dynamicRan === true, document.readyState].join()",
                ["returnByValue"] = true,
            },
        });
        JsonNode? evaluated = await CdpTestClient.AwaitResponseAsync(ws, 3, TimeSpan.FromSeconds(30));
        Assert.Equal("true,complete", evaluated.Get("result").Get("result").Get("value").AsString());
    }
}
