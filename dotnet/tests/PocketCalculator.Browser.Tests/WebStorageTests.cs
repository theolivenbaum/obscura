using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// Web Storage outlives the document, as in Chromium 141: localStorage belongs to the browser
/// context and sessionStorage to the page, both keyed by origin. The shim kept both in the
/// document's realm, so a navigation (a reload included) started them empty, and a site that
/// carries state across its own redirects (duolingo.com, mail.ru) lost it.
/// </summary>
public sealed class WebStorageTests
{
    private static TestHttpServer Server() =>
        TestHttpServer.Start(_ => TestResponse.Html("<!doctype html><html><body>page</body></html>"));

    private static string Eval(Page page, string expression) => page.Evaluate(expression)!.GetValue<string>();

    [Fact]
    public async Task StorageSurvivesNavigationAndIsSharedAsInChromium()
    {
        using TestHttpServer server = Server();
        BrowserContext context = BrowserContext.WithStorageAndNetwork("web-storage", null, false, null, null, true);
        using var page = new Page("web-storage-page", context);
        await page.NavigateAsync($"{server.Origin}/a");
        Assert.Equal("0|0", Eval(page, """
            (() => {
              localStorage.setItem('l', 'one');
              localStorage.n = 'two';
              sessionStorage.setItem('s', 'three');
              return localStorage.length - 2 + '|' + (sessionStorage.length - 1);
            })()
            """));

        // Same origin, another document: both areas are still there (key order is unspecified;
        // Chromium's is neither insertion nor sorted order).
        await page.NavigateAsync($"{server.Origin}/b");
        Assert.Equal(
            """{"l":"one","n":"two","keys":["l","n"],"k1":true,"k5":null,"s":"three","len":2,"has":true,"missing":null}""",
            Eval(page, """
                JSON.stringify({ l: localStorage.getItem('l'), n: localStorage.n, keys: Object.keys(localStorage).sort(),
                  k1: ['l', 'n'].includes(localStorage.key(1)), k5: localStorage.key(5), s: sessionStorage.getItem('s'), len: localStorage.length,
                  has: 'l' in localStorage, missing: localStorage.getItem('zz') })
                """));

        // Another origin sees neither.
        string other = server.Origin.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        await page.NavigateAsync($"{other}/c");
        Assert.Equal("0|0", Eval(page, "localStorage.length + '|' + sessionStorage.length"));

        // Another page of the same context shares localStorage but has its own sessionStorage.
        using var second = new Page("web-storage-second", context);
        await second.NavigateAsync($"{server.Origin}/d");
        Assert.Equal("one|null", Eval(second, "localStorage.getItem('l') + '|' + sessionStorage.getItem('s')"));
        Eval(second, "(() => { localStorage.removeItem('n'); localStorage.setItem('fromSecond', 'x'); return ''; })()");
        await page.NavigateAsync($"{server.Origin}/e");
        Assert.Equal("[\"fromSecond\",\"l\"]", Eval(page, "JSON.stringify(Object.keys(localStorage).sort())"));

        // clear() empties the origin's area for every page of the context.
        Eval(second, "(() => { localStorage.clear(); return ''; })()");
        Assert.Equal("0", Eval(page, "String(localStorage.length)"));
    }

    [Fact]
    public async Task SetItemOverTheQuotaThrowsAndKeepsTheArea()
    {
        using TestHttpServer server = Server();
        BrowserContext context = BrowserContext.WithStorageAndNetwork("web-storage-quota", null, false, null, null, true);
        using var page = new Page("web-storage-quota-page", context);
        await page.NavigateAsync($"{server.Origin}/a");
        Assert.Equal(
            "QuotaExceededError|Failed to execute 'setItem' on 'Storage': Setting the value of 'big' exceeded the quota.|1|small",
            Eval(page, """
                (() => {
                  localStorage.setItem('small', 'small');
                  let r = 'nothrow';
                  try { localStorage.setItem('big', 'x'.repeat(6 * 1024 * 1024)); } catch (e) { r = e.name + '|' + e.message; }
                  return r + '|' + localStorage.length + '|' + localStorage.getItem('small');
                })()
                """));
    }
}
