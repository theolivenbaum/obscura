using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// <c>attachShadow</c> inside a child frame acts on the frame's document. The shadow ops were
/// bound to the page's state, so the frame's node id was resolved in the page's arena:
/// vkvideo.ru's player inside mail.ru threw NotSupportedError for a &lt;div&gt;, and a node
/// id the page also had got its shadow root on the page's node. Chromium 141 attaches in both
/// cases below and leaves the parent document alone.
/// </summary>
public sealed class FrameShadowRootTests
{
    [Fact]
    public async Task AttachShadowInAChildFrameUsesTheFramesDocument()
    {
        using TestHttpServer server = TestHttpServer.Start(request => request.Path switch
        {
            "/page" => TestResponse.Html(
                "<html><body><div id=a></div><div id=b></div><iframe srcdoc=\"<body><script>"
                + "const r = [];"
                + "try { const d = document.createElement('div'); d.attachShadow({ mode: 'open' }); r.push('detached ' + !!d.shadowRoot); }"
                + " catch (e) { r.push('detached ' + e.name); }"
                + "try { const d = document.createElement('div'); document.body.appendChild(d); d.attachShadow({ mode: 'open' }); r.push('connected ok'); }"
                + " catch (e) { r.push('connected ' + e.name); }"
                + "globalThis.result = r.join('|');"
                + "</script></body>\"></iframe></body></html>"),
            _ => TestResponse.Html(""),
        });
        using Page page = PageFixtures.NewPage("frame-shadow-root");
        await page.NavigateAsync($"{server.Origin}/page");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await page.SettleAsync(50);
        }

        FrameRealm frame = Assert.Single(page.Frames, f => f.Url == "about:srcdoc");
        Assert.Equal("detached true|connected ok", frame.Evaluate("globalThis.result")!.GetValue<string>());
        Assert.Equal(
            "0",
            page.Evaluate("String([...document.querySelectorAll('*')].filter((e) => e.shadowRoot).length)")!.GetValue<string>());
    }
}
