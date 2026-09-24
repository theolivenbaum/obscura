using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// Referrer policy in pages (todo.md "Referrer policy"), each expectation measured on
/// Chromium 141 with the same markup: the Referrer-Policy header, <c>&lt;meta
/// name=referrer&gt;</c>, <c>referrerpolicy</c> on scripts, stylesheets, iframes and links,
/// <c>rel=noreferrer</c>, fetch()'s <c>referrerPolicy</c> and <c>referrer</c>, and a policy
/// on a redirect. The Rust engine sent every request under the default and fetch()/XHR
/// with no Referer at all.
/// </summary>
public sealed class ReferrerPolicyTests
{
    private static string? Referer(TestRequest request)
    {
        foreach (var line in request.Headers.Split("\r\n"))
        {
            if (line.StartsWith("Referer:", StringComparison.OrdinalIgnoreCase))
            {
                return line["Referer:".Length..].Trim();
            }
        }

        return null;
    }

    private static TestRequest Request(TestHttpServer server, string path)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            foreach (var request in server.Requests)
            {
                if (request.Path.Split('?')[0] == path)
                {
                    return request;
                }
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"{path} never arrived");
    }

    private static TestHttpServer Server(Func<string, TestResponse?> pages) =>
        TestHttpServer.Start(request =>
        {
            string path = request.Path.Split('?')[0];
            TestResponse response = pages(path)
                ?? (path.EndsWith(".js", StringComparison.Ordinal)
                    ? TestResponse.JavaScript("window.__loaded = (window.__loaded || 0) + 1;")
                    : path.EndsWith(".css", StringComparison.Ordinal)
                        ? TestResponse.Css("p { color: red; }")
                        : TestResponse.Html("<!doctype html><title>ok</title>"));
            return response.ExtraHeaders is null
                ? response with { ExtraHeaders = [("Access-Control-Allow-Origin", "*")] }
                : response;
        });

    private static async Task SettleAsync(Page page, string global)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await page.SettleAsync(50);
            if (PageFixtures.AsString(page.Evaluate($"String(window.{global})")) == "true")
            {
                return;
            }
        }
    }

    [Fact]
    public async Task FetchFollowsTheDefaultPolicyAndItsOptions()
    {
        using TestHttpServer cross = Server(_ => null);
        using TestHttpServer server = Server(path => path == "/page"
            ? TestResponse.Html(
                $$"""
                <!doctype html><script>
                (async () => {
                  await fetch('/f-same');
                  await fetch('{{cross.Origin}}/f-cross');
                  await fetch('{{cross.Origin}}/f-noref', { referrerPolicy: 'no-referrer' });
                  await fetch('{{cross.Origin}}/f-unsafe', { referrerPolicy: 'unsafe-url' });
                  await fetch('/f-origin', { referrerPolicy: 'origin' });
                  await fetch('/f-empty', { referrer: '' });
                  await fetch('/f-custom', { referrer: '/custom/path?x#y' });
                  await fetch('/f-foreign', { referrer: '{{cross.Origin}}/x' });
                  await new Promise(r => { const x = new XMLHttpRequest(); x.onloadend = r; x.open('GET', '/xhr'); x.send(); });
                  window.__done = true;
                })();
                </script>
                """)
            : null);
        using Page page = PageFixtures.NewPage("referrer-fetch");
        await page.NavigateAsync($"{server.Origin}/page?q=1#frag");
        await SettleAsync(page, "__done");

        string pageUrl = $"{server.Origin}/page?q=1";
        Assert.Equal(pageUrl, Referer(Request(server, "/f-same")));
        Assert.Equal($"{server.Origin}/", Referer(Request(cross, "/f-cross")));
        Assert.Null(Referer(Request(cross, "/f-noref")));
        Assert.Equal(pageUrl, Referer(Request(cross, "/f-unsafe")));
        Assert.Equal($"{server.Origin}/", Referer(Request(server, "/f-origin")));
        Assert.Null(Referer(Request(server, "/f-empty")));
        Assert.Equal($"{server.Origin}/custom/path?x", Referer(Request(server, "/f-custom")));
        Assert.Equal(pageUrl, Referer(Request(server, "/f-foreign")));
        Assert.Equal(pageUrl, Referer(Request(server, "/xhr")));

        PageFixtures.AssertJson(
            """["about:client", "", "origin", "TypeError"]""",
            page.Evaluate(
                "(() => { const r = new Request('/x'); const o = new Request('/x', { referrerPolicy: 'origin' });"
                + " let e = ''; try { new Request('/x', { referrerPolicy: 'bogus' }); } catch (x) { e = x.name; }"
                + " return [r.referrer, r.referrerPolicy, o.referrerPolicy, e]; })()"));
    }

    [Fact]
    public async Task MetaAndHeaderSetTheDocumentPolicy()
    {
        using TestHttpServer cross = Server(_ => null);
        using TestHttpServer server = Server(path => path switch
        {
            "/meta" => TestResponse.Html(
                $"<!doctype html><meta name=referrer content=no-referrer><script src='{cross.Origin}/meta.js'></script>"
                + "<script>fetch('/meta-fetch').then(() => { window.__done = true; });</script>"),
            "/header" => TestResponse.Html(
                $"<!doctype html><script src='{cross.Origin}/header.js'></script>"
                + $"<script>fetch('{cross.Origin}/header-fetch').then(() => {{ window.__done = true; }});</script>") with
            {
                ExtraHeaders = [("Referrer-Policy", "unsafe-url")],
            },
            "/both" => TestResponse.Html(
                "<!doctype html><meta name=referrer content=unsafe-url>"
                + $"<script>fetch('{cross.Origin}/both-fetch').then(() => {{ window.__done = true; }});</script>") with
            {
                ExtraHeaders = [("Referrer-Policy", "no-referrer")],
            },
            _ => null,
        });

        using (Page page = PageFixtures.NewPage("referrer-meta"))
        {
            await page.NavigateAsync($"{server.Origin}/meta?q=1");
            await SettleAsync(page, "__done");
            Assert.Null(Referer(Request(cross, "/meta.js")));
            Assert.Null(Referer(Request(server, "/meta-fetch")));
        }

        using (Page page = PageFixtures.NewPage("referrer-header"))
        {
            await page.NavigateAsync($"{server.Origin}/header?q=1");
            await SettleAsync(page, "__done");
            Assert.Equal($"{server.Origin}/header?q=1", Referer(Request(cross, "/header.js")));
            Assert.Equal($"{server.Origin}/header?q=1", Referer(Request(cross, "/header-fetch")));
        }

        using (Page page = PageFixtures.NewPage("referrer-both"))
        {
            // The meta overrides the header.
            await page.NavigateAsync($"{server.Origin}/both?q=1");
            await SettleAsync(page, "__done");
            Assert.Equal($"{server.Origin}/both?q=1", Referer(Request(cross, "/both-fetch")));
        }
    }

    [Fact]
    public async Task ElementAttributesAndRedirectPoliciesApply()
    {
        using TestHttpServer cross = Server(_ => null);
        using TestHttpServer server = Server(path => path switch
        {
            "/page" => TestResponse.Html(
                $"<!doctype html><script referrerpolicy=no-referrer src='{cross.Origin}/noref.js'></script>"
                + $"<link rel=stylesheet referrerpolicy=unsafe-url href='{cross.Origin}/unsafe.css'>"
                + "<iframe referrerpolicy=origin src='/frame-origin.html'></iframe>"
                + "<script>"
                + $"const s = document.createElement('script'); s.referrerPolicy = 'unsafe-url'; s.src = '{cross.Origin}/dyn.js';"
                + "document.head.appendChild(s);"
                + "fetch('/redir').then(() => { window.__done = true; });"
                + "</script>"),
            "/redir" => new TestResponse(
                "text/plain",
                [],
                "302 Found",
                [("Location", $"{cross.Origin}/redir-target"), ("Referrer-Policy", "no-referrer"), ("Access-Control-Allow-Origin", "*")]),
            _ => null,
        });
        using Page page = PageFixtures.NewPage("referrer-attributes");
        await page.NavigateAsync($"{server.Origin}/page?q=1");
        await SettleAsync(page, "__done");

        Assert.Null(Referer(Request(cross, "/noref.js")));
        Assert.Equal($"{server.Origin}/page?q=1", Referer(Request(cross, "/unsafe.css")));
        Assert.Equal($"{server.Origin}/", Referer(Request(server, "/frame-origin.html")));
        Assert.Equal($"{server.Origin}/page?q=1", Referer(Request(cross, "/dyn.js")));
        Assert.Equal($"{server.Origin}/page?q=1", Referer(Request(server, "/redir")));
        Assert.Null(Referer(Request(cross, "/redir-target")));
        Assert.Equal("unsafe-url", PageFixtures.AsString(page.Evaluate("document.querySelector('link').referrerPolicy")));
        Assert.Equal(
            "",
            PageFixtures.AsString(page.Evaluate(
                "(() => { const i = document.createElement('img'); i.referrerPolicy = 'BOGUS'; return i.referrerPolicy; })()")));
    }

    [Fact]
    public async Task LinkNavigationsCarryTheLinkPolicyIntoDocumentReferrer()
    {
        using TestHttpServer cross = Server(_ => null);
        using TestHttpServer server = Server(path => path == "/page"
            ? TestResponse.Html(
                $"<!doctype html><a id=noref rel=noreferrer href='{cross.Origin}/nav-noref.html'>x</a>"
                + "<a id=origin referrerpolicy=origin href='/nav-origin.html'>y</a>")
            : null);

        using (Page page = PageFixtures.NewPage("referrer-noreferrer"))
        {
            await page.NavigateAsync($"{server.Origin}/page?q=2");
            page.Evaluate("document.getElementById('noref').click()");
            await page.ProcessPendingNavigationAsync();
            Assert.Null(Referer(Request(cross, "/nav-noref.html")));
            Assert.Equal("", PageFixtures.AsString(page.Evaluate("document.referrer")));
        }

        using (Page page = PageFixtures.NewPage("referrer-link-origin"))
        {
            await page.NavigateAsync($"{server.Origin}/page?q=3");
            page.Evaluate("document.getElementById('origin').click()");
            await page.ProcessPendingNavigationAsync();
            Assert.Equal($"{server.Origin}/", Referer(Request(server, "/nav-origin.html")));
            Assert.Equal($"{server.Origin}/", PageFixtures.AsString(page.Evaluate("document.referrer")));
        }
    }
}
