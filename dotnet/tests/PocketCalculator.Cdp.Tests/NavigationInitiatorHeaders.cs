using System.Text.Json.Nodes;
using PocketCalculator.Js.Ops;
using Xunit;

using InputDomain = PocketCalculator.Cdp.Domains.Input;
using PageDomain = PocketCalculator.Cdp.Domains.Page;
using RuntimeDomain = PocketCalculator.Cdp.Domains.Runtime;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// CDP <c>Page.navigate</c> is the address bar: same-site for cookies, <c>Sec-Fetch-Site:
/// none</c>. A navigation the page starts, including one a client triggers through
/// <c>Runtime.evaluate</c> or a dispatched click, is judged against the page, as
/// Chromium 140 does. <c>localhost</c> and <c>127.0.0.1</c> are different sites.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class NavigationInitiatorHeaders
{
    // One body for every path: a full-viewport link to /next on 127.0.0.1, wherever the
    // page itself was loaded from.
    private const string Body =
        "<a id=l href='/next' style='position:fixed;left:0;top:0;width:400px;height:400px;display:block'>go</a>"
        + "<script>document.getElementById('l').href = "
        + "location.origin.replace('localhost', '127.0.0.1') + '/next'</script>";

    private static (CdpContext Ctx, string Session) Session(CdpTestServer server)
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        var jar = ctx.GetSessionPageMut(session)!.Context.CookieJar;
        jar.SetCookie("strict=1; SameSite=Strict; Path=/", new Uri(server.Url));
        jar.SetCookie("lax=1; SameSite=Lax; Path=/", new Uri(server.Url));
        return (ctx, session);
    }

    private static string CrossSite(CdpTestServer server, string path) =>
        server.Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal).TrimEnd('/') + path;

    private static string Request(CdpTestServer server, string path)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            foreach (string request in server.Requests)
            {
                if (request.StartsWith($"GET {path} ", StringComparison.Ordinal)
                    || request.StartsWith($"POST {path} ", StringComparison.Ordinal))
                {
                    return request;
                }
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException($"no request for {path}");
    }

    private static string? Header(string request, string name)
    {
        foreach (string line in request.Split("\r\n").Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && string.Equals(line[..colon], name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static string? Cookies(string request) =>
        Header(request, "cookie") is { } cookie
            ? string.Join("; ", cookie.Split("; ").Order(StringComparer.Ordinal))
            : null;

    private static async Task NavigateAsync(CdpContext ctx, string session, JsonObject parameters) =>
        CdpDomainFixtures.Unwrap(await PageDomain.HandleAsync("navigate", parameters, ctx, session));

    [Fact]
    public async Task PageNavigateKeepsStrictCookies()
    {
        using var server = CdpTestServer.ServeHtml(Body);
        (CdpContext ctx, string session) = Session(server);

        await NavigateAsync(ctx, session, new JsonObject { ["url"] = server.Url + "landing" });

        string request = Request(server, "/landing");
        Assert.Equal("lax=1; strict=1", Cookies(request));
        Assert.Equal("none", Header(request, "sec-fetch-site"));
        Assert.Equal("?1", Header(request, "sec-fetch-user"));
        Assert.Null(Header(request, "referer"));
    }

    [Fact]
    public async Task EvaluatedCrossSiteNavigationDropsStrictCookies()
    {
        using var server = CdpTestServer.ServeHtml(Body);
        (CdpContext ctx, string session) = Session(server);
        await NavigateAsync(ctx, session, new JsonObject { ["url"] = CrossSite(server, "/start") });

        CdpDomainFixtures.Unwrap(await RuntimeDomain.HandleAsync(
            "evaluate",
            new JsonObject { ["expression"] = "document.getElementById('l').click()" },
            ctx,
            session));

        string request = Request(server, "/next");
        Assert.Equal("lax=1", Cookies(request));
        Assert.Equal("cross-site", Header(request, "sec-fetch-site"));
        Assert.Null(Header(request, "sec-fetch-user"));
        Assert.Equal(CrossSite(server, "/"), Header(request, "referer"));
    }

    [Fact]
    public async Task DispatchedClickIsUserActivated()
    {
        using var server = CdpTestServer.ServeHtml(Body);
        (CdpContext ctx, string session) = Session(server);
        await NavigateAsync(ctx, session, new JsonObject { ["url"] = CrossSite(server, "/start") });

        foreach (string type in new[] { "mousePressed", "mouseReleased" })
        {
            CdpDomainFixtures.Unwrap(await InputDomain.HandleAsync(
                "dispatchMouseEvent",
                new JsonObject { ["type"] = type, ["x"] = 50, ["y"] = 50, ["button"] = "left", ["clickCount"] = 1 },
                ctx,
                session));
        }

        string request = Request(server, "/next");
        Assert.Equal("lax=1", Cookies(request));
        Assert.Equal("cross-site", Header(request, "sec-fetch-site"));
        Assert.Equal("?1", Header(request, "sec-fetch-user"));
    }

    [Fact]
    public async Task ForwardedPageNavigationCarriesItsInitiator()
    {
        // The server turns a queued page navigation into an internal Page.navigate; the
        // initiator travels with it so it does not go out as an address-bar navigation.
        using var server = CdpTestServer.ServeHtml(Body);
        (CdpContext ctx, string session) = Session(server);
        var pending = new PendingNavigation(server.Url + "forwarded", "POST", "q=1")
        {
            Initiator = CrossSite(server, "/form"),
        };

        await NavigateAsync(ctx, session, PageDomain.JsNavigationParams(pending));

        string request = Request(server, "/forwarded");
        Assert.StartsWith("POST ", request, StringComparison.Ordinal);
        Assert.Null(Header(request, "cookie"));
        Assert.Equal("cross-site", Header(request, "sec-fetch-site"));
        Assert.Equal(CrossSite(server, string.Empty), Header(request, "origin"));
        Assert.Equal(CrossSite(server, "/"), Header(request, "referer"));
    }
}
