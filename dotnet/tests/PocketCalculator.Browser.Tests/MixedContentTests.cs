using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Url;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// SECURITY.md I7 in pages: mixed content from an https document is blocked (or, for an
/// image, upgraded) and reported on the console, and HSTS hosts are reached over https.
/// A recording proxy stands in for the network, so every request that would have left
/// is visible and none needs DNS: an http request arrives as <c>GET http://...</c>, an
/// https one as <c>CONNECT host:443</c>.
/// </summary>
public sealed class MixedContentTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private static (TestHttpServer Proxy, Page Page) SecurePage(string name, string pageUrl, string body)
    {
        TestHttpServer proxy = TestHttpServer.Start(request =>
            request.Method == "CONNECT"
                ? new TestResponse("text/plain", [], "502 Bad Gateway")
                : TestResponse.Text("from the network"));
        BrowserContext context = BrowserContext.WithStorageAndNetwork(name, proxy.Origin, false, null, null, true);
        var page = new Page(name, context)
        {
            Url = UrlRecord.Parse(pageUrl)!,
            Dom = HtmlParsing.ParseHtml($"<html><head></head><body>{body}</body></html>"),
        };
        page.InitJs();
        page.Js!.SetRuntimeEventsEnabled(true);
        return (proxy, page);
    }

    private static async Task<string?> AwaitGlobal(Page page, string name)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            await page.SettleAsync(100);
            if (PageFixtures.AsString(page.Evaluate($"window.{name} === undefined ? null : String(window.{name})")) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static List<string> ConsoleTexts(Page page)
    {
        List<string> texts = [];
        foreach (RuntimeEvent entry in page.TakePendingRuntimeEvents())
        {
            if (entry is RuntimeEvent.Console console && console.Event.Args.Count == 1
                && console.Event.Args[0]?["value"]?.GetValue<string>() is { } text)
            {
                texts.Add(console.Event.Kind + ": " + text);
            }
        }

        return texts;
    }

    [Fact]
    public async Task FetchFromAnHttpsPageToHttpIsBlockedAndReported()
    {
        var (proxy, page) = SecurePage("mixed-fetch", "https://secure.example/app", string.Empty);
        using (proxy)
        using (page)
        {
            page.Evaluate(
                "fetch('http://insecure.example/data').then(r => r.text()).then(t => { window.__r = 'ok:' + t; },"
                + " e => { window.__r = e.name + ': ' + e.message; })");
            string? result = await AwaitGlobal(page, "__r");
            Assert.NotNull(result);
            Assert.StartsWith("TypeError: Failed to fetch", result, StringComparison.Ordinal);
            Assert.False(proxy.TryNextPath(Quiet, out string leaked), $"the request left: {leaked}");
            Assert.Contains(
                "error: Mixed Content: The page at 'https://secure.example/app' was loaded over HTTPS, but requested an "
                + "insecure resource 'http://insecure.example/data'. This request has been blocked; the content must be "
                + "served over HTTPS.",
                ConsoleTexts(page));
        }
    }

    [Fact]
    public async Task DynamicHttpScriptOnAnHttpsPageIsBlocked()
    {
        var (proxy, page) = SecurePage("mixed-script", "https://secure.example/", string.Empty);
        using (proxy)
        using (page)
        {
            page.Evaluate(
                """
                const s = document.createElement('script');
                s.src = 'http://insecure.example/evil.js';
                s.onerror = () => { window.__r = 'error'; };
                s.onload = () => { window.__r = 'load'; };
                document.head.appendChild(s);
                """);
            Assert.Equal("error", await AwaitGlobal(page, "__r"));
            Assert.False(proxy.TryNextPath(Quiet, out string leaked), $"the request left: {leaked}");
        }
    }

    [Fact]
    public async Task HttpImageOnAnHttpsPageIsUpgraded()
    {
        var (proxy, page) = SecurePage("mixed-image", "https://secure.example/", """<img src="http://insecure.example/a.svg">""");
        using (proxy)
        using (page)
        {
            page.Evaluate("document.querySelector('img').getBoundingClientRect().width");
            page.QueuePendingRenderResources();
            // The only request is the upgraded one (the proxy refuses it, as a failed
            // upgrade is not retried over http).
            Assert.Equal("insecure.example:443", proxy.NextPath(TimeSpan.FromSeconds(5)));
            await page.PrepareScreenshotResourcesAsync(5_000);
            Assert.False(proxy.TryNextPath(Quiet, out string retried), $"retried: {retried}");
            Assert.Contains(ConsoleTexts(page), text => text.StartsWith(
                "warning: Mixed Content: The page at 'https://secure.example/' was loaded over HTTPS, but requested an insecure "
                + "element 'http://insecure.example/a.svg'. This request was automatically upgraded to HTTPS",
                StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task FetchToAKnownHstsHostGoesOverHttps()
    {
        var (proxy, page) = SecurePage("hsts-fetch", "http://plain.example/", string.Empty);
        using (proxy)
        using (page)
        {
            page.HttpClient.Hsts.Add("hsts.example", TimeSpan.FromHours(1), includeSubdomains: true);
            page.Evaluate("fetch('http://api.hsts.example/x').then(() => { window.__r = 'ok'; }, e => { window.__r = 'failed'; })");
            Assert.Equal("failed", await AwaitGlobal(page, "__r"));
            Assert.Equal("api.hsts.example:443", proxy.NextPath(TimeSpan.FromSeconds(5)));

            // Before HSTS the same page reaches plain http hosts as before.
            page.Evaluate("fetch('http://plain.example/y').then(r => r.text()).then(t => { window.__p = t; })");
            Assert.Equal("from the network", await AwaitGlobal(page, "__p"));
            Assert.Equal("http://plain.example/y", proxy.NextPath(TimeSpan.FromSeconds(5)));
        }
    }
}
