using PocketCalculator.Dom;
using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Referrers of srcdoc and about:blank frames, measured on Chromium 141: a srcdoc frame's
/// requests carry its parent's URL under the parent's policy and its document.referrer is
/// the parent's origin by default; an about:blank frame's requests carry no Referer. The
/// port used to send a srcdoc frame's requests with no Referer, from about:srcdoc.
/// </summary>
public sealed partial class RuntimeTests
{
    private static RuntimeFixture FramedPage(string origin, string head)
    {
        var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml($"<html><head>{head}</head><body></body></html>"));
        runtime.SetUrl($"{origin}/src/page?x=1");
        runtime.SetHttpClient(new PocketCalculatorHttpClient(new CookieJar(), null, allowPrivateNetwork: true));
        runtime.RunPageInit();
        return fixture;
    }

    private static string? RefererOf(RawHttpServer server, string path)
    {
        foreach (var request in server.Requests)
        {
            if (RawHttpServer.RequestPath(request) == path)
            {
                foreach (var line in request.Split("\r\n"))
                {
                    if (line.StartsWith("Referer:", StringComparison.OrdinalIgnoreCase))
                    {
                        return line["Referer:".Length..].Trim();
                    }
                }

                return null;
            }
        }

        throw new InvalidOperationException(path + " never arrived");
    }

    [Fact]
    public async Task SrcdocFramesReferAsTheirParentAndAboutBlankFramesDoNot()
    {
        using var server = new RawHttpServer(_ => OkCorsResponse);
        using var page = FramedPage(server.Origin, string.Empty);
        using var srcdoc = FrameRealm.Create(page.Runtime, 1, 0, "about:srcdoc", "<html><body></body></html>");
        using var blank = FrameRealm.Create(page.Runtime, 2, 0, "about:blank", "<html><body></body></html>");
        using var nested = FrameRealm.Create(page.Runtime, 3, 1, "about:srcdoc", "<html><body></body></html>");
        Assert.NotNull(srcdoc);
        Assert.NotNull(blank);
        Assert.NotNull(nested);

        await FetchOps.FetchUrlAsync(page.Runtime.State, srcdoc.State, $"{server.Origin}/src/fetch1", "GET", "{}", [], "cors", "same-origin", internalLoad: false);
        await FetchOps.FetchUrlAsync(page.Runtime.State, blank.State, $"{server.Origin}/blank/fetch", "GET", "{}", [], "cors", "same-origin", internalLoad: false);
        await FetchOps.FetchUrlAsync(page.Runtime.State, nested.State, $"{server.Origin}/nested/fetch", "GET", "{}", [], "cors", "same-origin", internalLoad: false);

        Assert.Equal($"{server.Origin}/src/page?x=1", RefererOf(server, "/src/fetch1"));
        Assert.Null(RefererOf(server, "/blank/fetch"));
        Assert.Equal($"{server.Origin}/src/page?x=1", RefererOf(server, "/nested/fetch"));
        Assert.Equal($"{server.Origin}/", srcdoc.Evaluate("document.referrer")!.GetValue<string>());
    }

    [Fact]
    public async Task SrcdocFramesInheritTheParentPolicy()
    {
        using var server = new RawHttpServer(_ => OkCorsResponse);
        using var page = FramedPage(server.Origin, "<meta name=referrer content=no-referrer>");
        using var srcdoc = FrameRealm.Create(page.Runtime, 1, 0, "about:srcdoc", "<html><body></body></html>");
        using var own = FrameRealm.Create(page.Runtime, 2, 0, "about:srcdoc", "<html><head><meta name=referrer content=unsafe-url></head><body></body></html>");
        Assert.NotNull(srcdoc);
        Assert.NotNull(own);

        await FetchOps.FetchUrlAsync(page.Runtime.State, srcdoc.State, $"{server.Origin}/srcpol/fetch1", "GET", "{}", [], "cors", "same-origin", internalLoad: false);
        await FetchOps.FetchUrlAsync(page.Runtime.State, own.State, $"{server.Origin}/srcpol/own", "GET", "{}", [], "cors", "same-origin", internalLoad: false);

        Assert.Null(RefererOf(server, "/srcpol/fetch1"));
        Assert.Equal(string.Empty, srcdoc.Evaluate("document.referrer")!.GetValue<string>());
        // The frame's own meta still overrides what it inherited.
        Assert.Equal($"{server.Origin}/src/page?x=1", RefererOf(server, "/srcpol/own"));
        Assert.Equal(ReferrerPolicy.NoReferrer, srcdoc.State.ReferrerPolicyHeader);
    }
}
