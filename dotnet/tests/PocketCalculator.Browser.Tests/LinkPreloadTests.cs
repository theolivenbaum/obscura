using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// <c>&lt;link rel=preload&gt;</c> fetches and fires <c>load</c> (or <c>error</c>), as in
/// Chromium 141. The port never fired either, so the loadCSS pattern
/// (<c>onload="this.rel='stylesheet'"</c>, or vk.com's clone-and-insert) never applied its
/// sheets and vk.com painted a blank page.
/// </summary>
public sealed class LinkPreloadTests
{
    [Fact]
    public async Task PreloadLinksFireLoadAndTheLoadCssPatternApplies()
    {
        using TestHttpServer server = TestHttpServer.Start(request => request.Path.Split('?')[0] switch
        {
            "/page" => TestResponse.Html(
                "<!doctype html><head>"
                + "<link rel=\"preload\" as=\"style\" href=\"a.css\" onload=\"window.L=(window.L||[]).concat('style');"
                + "const el=this.cloneNode();el.rel='stylesheet';el.removeAttribute('onload');this.parentNode.insertBefore(el,this);\">"
                + "<link rel=\"preload\" as=\"script\" href=\"g.js\" onload=\"window.L=(window.L||[]).concat('script')\">"
                + "<link rel=\"preload\" as=\"style\" href=\"missing.css\" onerror=\"window.L=(window.L||[]).concat('missing-error')\">"
                + "<link rel=\"preload\" as=\"bogus\" href=\"a.css?bogus\" onload=\"window.L=(window.L||[]).concat('bogus')\" onerror=\"window.L=(window.L||[]).concat('bogus-error')\">"
                + "</head><body><p>x</p><script>"
                + "const l = document.createElement('link'); l.rel = 'preload'; l.as = 'style'; l.href = 'a.css?dynamic';"
                + "l.onload = () => { window.L = (window.L || []).concat('dynamic'); }; document.head.appendChild(l);"
                + "</script></body>"),
            "/a.css" => TestResponse.Css("p { color: rgb(255, 0, 0); }"),
            "/g.js" => TestResponse.JavaScript("window.G = 1;"),
            _ => TestResponse.Text("missing") with { Status = "404 Not Found" },
        });
        using Page page = PageFixtures.NewPage("link-preload");
        await page.NavigateAsync($"{server.Origin}/page");
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await page.SettleAsync(50);
        }

        Assert.Equal(
            """[["dynamic","missing-error","script","style"],"rgb(255, 0, 0)",1]""",
            page.Evaluate(
                "JSON.stringify([(window.L || []).slice().sort(), getComputedStyle(document.querySelector('p')).color,"
                + " document.querySelectorAll('link[rel=stylesheet]').length])")!.GetValue<string>());
    }
}
