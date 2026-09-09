using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Obscura.Net;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-cdp/src/domains/network.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class NetworkDomainTests
{
    internal static CookieInfo SampleCookie(string name) => new()
    {
        Name = name,
        Value = "v",
        Domain = "example.com",
        Path = "/",
        Secure = false,
        HttpOnly = false,
        SameSite = string.Empty,
        Expires = null,
    };

    private static Task<DomainResult> HandleAsync(
        string method,
        string parameters,
        CdpContext ctx,
        string? sessionId = null) =>
        Network.HandleAsync(method, CdpDomainFixtures.Json(parameters), ctx, sessionId);

    [Fact]
    public async Task SetCookieWithoutSessionTargetsDefaultContext()
    {
        var ctx = CdpContext.New();
        JsonNode response = CdpDomainFixtures.Unwrap(await HandleAsync(
            "setCookie",
            """{"name": "sid", "value": "abc", "domain": "example.com", "path": "/"}""",
            ctx));
        Assert.True(response["success"]!.GetValue<bool>());
        List<CookieInfo> cookies = ctx.DefaultContext.CookieJar.GetAllCookies();
        Assert.Single(cookies);
        Assert.Equal("sid", cookies[0].Name);
    }

    [Fact]
    public async Task SetCookiesWithoutSessionTargetsDefaultContext()
    {
        var ctx = CdpContext.New();
        Assert.True((await HandleAsync(
            "setCookies",
            """
            {
                "cookies": [
                    { "name": "a", "value": "1", "domain": "example.com", "path": "/" },
                    { "name": "b", "value": "2", "domain": "example.com", "path": "/" }
                ]
            }
            """,
            ctx)).IsOk);
        Assert.Equal(2, ctx.DefaultContext.CookieJar.GetAllCookies().Count);
    }

    [Fact]
    public async Task DeleteCookiesWithoutSessionTargetsDefaultContext()
    {
        var ctx = CdpContext.New();
        ctx.DefaultContext.CookieJar.SetCookiesFromCdp([SampleCookie("sid")]);
        Assert.True((await HandleAsync(
            "deleteCookies",
            """{"name": "sid", "domain": "example.com"}""",
            ctx)).IsOk);
        Assert.Empty(ctx.DefaultContext.CookieJar.GetAllCookies());
    }

    [Fact]
    public async Task GetAllCookiesReturnsEveryCookieInJar()
    {
        var ctx = CdpContext.New();
        ctx.DefaultContext.CookieJar.SetCookiesFromCdp([SampleCookie("a"), SampleCookie("b")]);
        JsonNode response = CdpDomainFixtures.Unwrap(await HandleAsync("getAllCookies", "{}", ctx));
        Assert.Equal(2, response["cookies"]!.AsArray().Count);
    }

    [Fact]
    public async Task GetCookiesFallsBackToDefaultContextWhenNoSession()
    {
        var ctx = CdpContext.New();
        ctx.DefaultContext.CookieJar.SetCookiesFromCdp([SampleCookie("sid")]);
        JsonNode response = CdpDomainFixtures.Unwrap(await HandleAsync("getCookies", "{}", ctx));
        JsonArray cookies = response["cookies"]!.AsArray();
        Assert.Single(cookies);
        Assert.Equal("sid", cookies[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClearBrowserCookiesWithoutSessionClearsDefaultContext()
    {
        var ctx = CdpContext.New();
        ctx.DefaultContext.CookieJar.SetCookiesFromCdp([SampleCookie("sid")]);
        Assert.True((await HandleAsync("clearBrowserCookies", "{}", ctx)).IsOk);
        Assert.Empty(ctx.DefaultContext.CookieJar.GetAllCookies());
    }

    [Fact]
    public async Task SetBlockedUrlsTargetsSessionPageWithoutEnablingInterception()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        string pageId = ctx.Sessions[session];

        Assert.True((await HandleAsync(
            "setBlockedURLs",
            """{"urls": ["*://*.example.com/*.png", "*://cdn.example.com/*"]}""",
            ctx,
            session)).IsOk);

        var page = ctx.GetPage(pageId);
        Assert.NotNull(page);
        Assert.Equal(
            ["*://*.example.com/*.png", "*://cdn.example.com/*"],
            page.BlockedUrlPatterns);
        Assert.False(page.InterceptEnabled);
        Assert.Empty(page.InterceptBlockPatterns);
    }

    [Fact]
    public async Task SetBlockedUrlsWithoutSessionUpdatesExistingPages()
    {
        var ctx = CdpContext.New();
        string left = ctx.CreatePage();
        string right = ctx.CreatePage();

        Assert.True((await HandleAsync(
            "setBlockedURLs",
            """{"urls": ["*://tiles.example.test/*"]}""",
            ctx)).IsOk);

        Assert.Equal(["*://tiles.example.test/*"], ctx.GetPage(left)!.BlockedUrlPatterns);
        Assert.Equal(["*://tiles.example.test/*"], ctx.GetPage(right)!.BlockedUrlPatterns);
    }

    [Fact]
    public async Task SetBlockedUrlsReplacesExistingPatterns()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        string pageId = ctx.Sessions[session];

        Assert.True((await HandleAsync(
            "setBlockedURLs",
            """{"urls": ["*://old.example.test/*"]}""",
            ctx,
            session)).IsOk);
        Assert.True((await HandleAsync(
            "setBlockedURLs",
            """{"urls": ["*://new.example.test/*"]}""",
            ctx,
            session)).IsOk);

        Assert.Equal(["*://new.example.test/*"], ctx.GetPage(pageId)!.BlockedUrlPatterns);
    }

    [Fact]
    public async Task GetResponseBodyReturnsStoredDocumentBody()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        string pageId = ctx.Sessions[session];

        var page = ctx.GetPageMut(pageId);
        Assert.NotNull(page);
        await page.NavigateAsync("data:text/html,<html><body>hello body</body></html>");
        string requestId = page.NetworkEvents[0].RequestId;

        JsonNode result = CdpDomainFixtures.Unwrap(await HandleAsync(
            "getResponseBody",
            $$"""{"requestId": "{{requestId}}"}""",
            ctx,
            session));

        Assert.Equal("<html><body>hello body</body></html>", result["body"]!.GetValue<string>());
        Assert.False(result["base64Encoded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetResponseBodyErrorsForUnknownRequestId()
    {
        var ctx = CdpContext.New();
        string error = CdpDomainFixtures.ErrorOf(await HandleAsync(
            "getResponseBody",
            """{"requestId": "missing"}""",
            ctx));
        Assert.Contains("missing", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkDisableClearsStoredResponseBodies()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        string pageId = ctx.Sessions[session];

        var page = ctx.GetPageMut(pageId);
        Assert.NotNull(page);
        await page.NavigateAsync("data:text/html,<html><body>temporary body</body></html>");
        string requestId = page.NetworkEvents[0].RequestId;

        Assert.True((await HandleAsync("disable", "{}", ctx, session)).IsOk);

        string error = CdpDomainFixtures.ErrorOf(await HandleAsync(
            "getResponseBody",
            $$"""{"requestId": "{{requestId}}"}""",
            ctx,
            session));
        Assert.Contains("No response body found", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not in the Rust file: the CDP cookie projection itself, whose <c>size</c>, <c>expires</c>,
    /// <c>session</c> and <c>sourcePort</c> defaults are what a client reads back.
    /// </summary>
    [Fact]
    public async Task CookieProjectionCarriesTheChromeDefaults()
    {
        var ctx = CdpContext.New();
        ctx.DefaultContext.CookieJar.SetCookiesFromCdp([new CookieInfo
        {
            Name = "sid",
            Value = "abc",
            Domain = "example.com",
            Path = "/",
            Secure = true,
            HttpOnly = true,
            SameSite = string.Empty,
            Expires = null,
        }]);

        JsonNode response = CdpDomainFixtures.Unwrap(await HandleAsync("getCookies", "{}", ctx));
        JsonNode cookie = response["cookies"]!.AsArray()[0]!;
        Assert.Equal(-1, cookie["expires"]!.GetValue<long>());
        Assert.Equal(6, cookie["size"]!.GetValue<int>());
        Assert.True(cookie["session"]!.GetValue<bool>());
        Assert.Equal("Lax", cookie["sameSite"]!.GetValue<string>());
        Assert.False(cookie["sameParty"]!.GetValue<bool>());
        Assert.Equal("Secure", cookie["sourceScheme"]!.GetValue<string>());
        Assert.Equal(443, cookie["sourcePort"]!.GetValue<int>());
        Assert.Equal("Medium", cookie["priority"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownMethodIsAnError()
    {
        var ctx = CdpContext.New();
        Assert.Contains(
            "Unknown Network method: nope",
            CdpDomainFixtures.ErrorOf(await HandleAsync("nope", "{}", ctx)),
            StringComparison.Ordinal);
    }
}
