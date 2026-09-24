using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// Stylesheet subresources are referred by the stylesheet, measured on Chromium 141 with
/// the same markup: a nested <c>@import</c> carries the importing sheet's URL under that
/// sheet's Referrer-Policy header, else the document's policy; a <c>background-image</c> or
/// <c>@font-face</c> URL in an external sheet carries the sheet's URL under the sheet's
/// header policy, else the default (the document's meta does not reach it). The port used
/// to send all of them with the document as referrer.
/// </summary>
public sealed class CssSubresourceReferrerTests
{
    private const string SheetA =
        "@import \"b.css\"; .bg{width:10px;height:10px;background-image:url(bg.png)}"
        + " @font-face{font-family:F;src:url(f.woff)} .f{font-family:F}";

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
                ?? (path.EndsWith(".css", StringComparison.Ordinal)
                    ? TestResponse.Css(path.EndsWith("/a.css", StringComparison.Ordinal) ? SheetA : "div{color:red}")
                    : TestResponse.Html("<!doctype html><title>ok</title>"));
            return response.ExtraHeaders is null
                ? response with { ExtraHeaders = [("Access-Control-Allow-Origin", "*")] }
                : response;
        });

    private static async Task LoadAsync(Page page, string url)
    {
        await page.NavigateAsync(url);
        page.Evaluate("document.querySelector('.bg').getBoundingClientRect().width + document.querySelector('.f').getBoundingClientRect().width");
        await page.PrepareScreenshotResourcesAsync(5_000);
    }

    private const string Body = "<div class=bg>x</div><span class=f>t</span>";

    [Fact]
    public async Task ExternalSheetsReferTheirImportsImagesAndFonts()
    {
        using TestHttpServer server = Server(path => path switch
        {
            "/css/page" => TestResponse.Html($"<!doctype html><link rel=stylesheet href='/css/a.css'>{Body}"),
            _ => null,
        });
        using Page page = PageFixtures.NewPage("css-referrer-same");
        await LoadAsync(page, $"{server.Origin}/css/page");

        Assert.Equal($"{server.Origin}/css/page", Referer(Request(server, "/css/a.css")));
        Assert.Equal($"{server.Origin}/css/a.css", Referer(Request(server, "/css/b.css")));
        Assert.Equal($"{server.Origin}/css/a.css", Referer(Request(server, "/css/bg.png")));
        Assert.Equal($"{server.Origin}/css/a.css", Referer(Request(server, "/css/f.woff")));
    }

    [Fact]
    public async Task CrossOriginSheetsReferAsThemselves()
    {
        using TestHttpServer cross = Server(_ => null);
        using TestHttpServer server = Server(path => path switch
        {
            "/xcss/page" => TestResponse.Html($"<!doctype html><link rel=stylesheet href='{cross.Origin}/css/a.css'>{Body}"),
            _ => null,
        });
        using Page page = PageFixtures.NewPage("css-referrer-cross");
        await LoadAsync(page, $"{server.Origin}/xcss/page");

        Assert.Equal($"{server.Origin}/", Referer(Request(cross, "/css/a.css")));
        Assert.Equal($"{cross.Origin}/css/a.css", Referer(Request(cross, "/css/b.css")));
        Assert.Equal($"{cross.Origin}/css/a.css", Referer(Request(cross, "/css/bg.png")));
        Assert.Equal($"{cross.Origin}/css/a.css", Referer(Request(cross, "/css/f.woff")));
    }

    [Fact]
    public async Task DocumentMetaGovernsImportsButNotSheetImages()
    {
        using TestHttpServer server = Server(path => path switch
        {
            "/pol/page" => TestResponse.Html(
                $"<!doctype html><meta name=referrer content=origin><link rel=stylesheet href='/css/a.css'>{Body}"),
            _ => null,
        });
        using Page page = PageFixtures.NewPage("css-referrer-meta");
        await LoadAsync(page, $"{server.Origin}/pol/page");

        Assert.Equal($"{server.Origin}/", Referer(Request(server, "/css/a.css")));
        Assert.Equal($"{server.Origin}/", Referer(Request(server, "/css/b.css")));
        Assert.Equal($"{server.Origin}/css/a.css", Referer(Request(server, "/css/bg.png")));
        Assert.Equal($"{server.Origin}/css/a.css", Referer(Request(server, "/css/f.woff")));
    }

    [Fact]
    public async Task ASheetsOwnHeaderGovernsWhatItFetches()
    {
        using TestHttpServer server = Server(path => path switch
        {
            "/polhdr/page" => TestResponse.Html($"<!doctype html><link rel=stylesheet href='/csspol/a.css'>{Body}"),
            "/csspol/a.css" => TestResponse.Css(SheetA) with
            {
                ExtraHeaders = [("Referrer-Policy", "no-referrer")],
            },
            _ => null,
        });
        using Page page = PageFixtures.NewPage("css-referrer-header");
        await LoadAsync(page, $"{server.Origin}/polhdr/page");

        Assert.Equal($"{server.Origin}/polhdr/page", Referer(Request(server, "/csspol/a.css")));
        Assert.Null(Referer(Request(server, "/csspol/b.css")));
        Assert.Null(Referer(Request(server, "/csspol/bg.png")));
    }

    // An inline <style> with an @import, an image and a font, a style="" image and an <img>.
    private static string InlinePage(string imageOrigin, string head) =>
        $"<!doctype html><html><head>{head}<style>@import \"/inl/imp.css\";"
        + $" .bg{{width:10px;height:10px;background-image:url({imageOrigin}/inl/bg.png)}}"
        + $" @font-face{{font-family:F;src:url({imageOrigin}/inl/f.woff)}} .f{{font-family:F}}</style></head><body>"
        + $"{Body}<div style=\"width:10px;height:10px;background-image:url({imageOrigin}/inl/attr.png)\"></div>"
        + $"<img src=\"{imageOrigin}/inl/img.png\"></body></html>";

    [Fact]
    public async Task InlineStyleImagesAndFontsUseTheDefaultPolicyNotTheDocuments()
    {
        // Chromium 141: meta no-referrer silences the <img> and the inline @import, while the
        // inline sheet's image and font and the style attribute's image still carry the full
        // same-origin URL (strict-origin-when-cross-origin).
        using TestHttpServer server = Server(path => path switch
        {
            "/inl/page" => TestResponse.Html(InlinePage(string.Empty, "<meta name=referrer content=no-referrer>")),
            _ => null,
        });
        using Page page = PageFixtures.NewPage("css-referrer-inline-meta");
        await LoadAsync(page, $"{server.Origin}/inl/page?q=1");

        Assert.Null(Referer(Request(server, "/inl/imp.css")));
        Assert.Null(Referer(Request(server, "/inl/img.png")));
        Assert.Equal($"{server.Origin}/inl/page?q=1", Referer(Request(server, "/inl/bg.png")));
        Assert.Equal($"{server.Origin}/inl/page?q=1", Referer(Request(server, "/inl/f.woff")));
        Assert.Equal($"{server.Origin}/inl/page?q=1", Referer(Request(server, "/inl/attr.png")));
    }

    [Fact]
    public async Task InlineStyleImagesIgnoreTheDocumentsHeaderPolicy()
    {
        // Chromium 141: Referrer-Policy: unsafe-url gives the <img> the full URL cross-origin;
        // the inline sheet's image and the style attribute's image send the origin only.
        using TestHttpServer cross = Server(_ => null);
        using TestHttpServer server = Server(path => path switch
        {
            "/inl/page" => TestResponse.Html(InlinePage(cross.Origin, string.Empty)) with
            {
                ExtraHeaders = [("Referrer-Policy", "unsafe-url")],
            },
            _ => null,
        });
        using Page page = PageFixtures.NewPage("css-referrer-inline-header");
        await LoadAsync(page, $"{server.Origin}/inl/page?q=1");

        Assert.Equal($"{server.Origin}/inl/page?q=1", Referer(Request(server, "/inl/imp.css")));
        Assert.Equal($"{server.Origin}/inl/page?q=1", Referer(Request(cross, "/inl/img.png")));
        Assert.Equal($"{server.Origin}/", Referer(Request(cross, "/inl/bg.png")));
        Assert.Equal($"{server.Origin}/", Referer(Request(cross, "/inl/f.woff")));
        Assert.Equal($"{server.Origin}/", Referer(Request(cross, "/inl/attr.png")));
    }

    [Fact]
    public async Task ScriptInsertedSheetsReferTheirImportsAndImages()
    {
        using TestHttpServer cross = Server(_ => null);
        using TestHttpServer server = Server(path => path switch
        {
            "/dyn/page" => TestResponse.Html(
                $"<!doctype html>{Body}<script>const l = document.createElement('link'); l.rel = 'stylesheet';"
                + $" l.href = '{cross.Origin}/css/a.css'; document.head.appendChild(l);</script>"),
            _ => null,
        });
        using Page page = PageFixtures.NewPage("css-referrer-dynamic");
        await page.NavigateAsync($"{server.Origin}/dyn/page");
        Request(cross, "/css/b.css");
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await page.SettleAsync(50);
        }
        page.Evaluate("document.querySelector('.bg').getBoundingClientRect().width");
        await page.PrepareScreenshotResourcesAsync(5_000);

        Assert.Equal($"{server.Origin}/", Referer(Request(cross, "/css/a.css")));
        Assert.Equal($"{cross.Origin}/css/a.css", Referer(Request(cross, "/css/b.css")));
        Assert.Equal($"{cross.Origin}/css/a.css", Referer(Request(cross, "/css/bg.png")));
    }
}
