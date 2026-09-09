using Obscura.Cdp.Domains;
using Obscura.Net;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-cdp/src/domains/storage.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class StorageDomainTests
{
    private static CookieInfo SampleCookie(string value) => new()
    {
        Name = "sid",
        Value = value,
        Domain = "example.com",
        Path = "/",
        Secure = false,
        HttpOnly = false,
        SameSite = string.Empty,
        Expires = null,
    };

    [Fact]
    public async Task ClearCookiesIsScopedToBrowserContext()
    {
        var ctx = CdpContext.New();
        string browserContextId = ctx.CreateBrowserContext();
        ctx.BrowserContextById(browserContextId)!.CookieJar
            .SetCookiesFromCdp([SampleCookie("isolated")]);
        ctx.DefaultContext.CookieJar.SetCookiesFromCdp([SampleCookie("default")]);

        Assert.True((await Storage.HandleAsync(
            "clearCookies",
            CdpDomainFixtures.Json($$"""{"browserContextId": "{{browserContextId}}"}"""),
            ctx,
            null)).IsOk);

        Assert.Empty(ctx.BrowserContextById(browserContextId)!.CookieJar.GetAllCookies());
        List<CookieInfo> defaults = ctx.DefaultContext.CookieJar.GetAllCookies();
        Assert.Single(defaults);
        Assert.Equal("default", defaults[0].Value);
    }

    [Fact]
    public async Task ClearCookiesWithoutContextClearsDefaultContext()
    {
        var ctx = CdpContext.New();
        ctx.DefaultContext.CookieJar.SetCookiesFromCdp([SampleCookie("default")]);

        Assert.True((await Storage.HandleAsync(
            "clearCookies",
            CdpDomainFixtures.Json("{}"),
            ctx,
            null)).IsOk);

        Assert.Empty(ctx.DefaultContext.CookieJar.GetAllCookies());
    }

    /// <summary>
    /// Not in the Rust file: an unknown <c>browserContextId</c> is the one Storage path that
    /// answers with an error instead of the domain's permissive <c>{}</c>.
    /// </summary>
    [Fact]
    public async Task UnknownBrowserContextIsAnErrorButUnknownMethodIsANoOp()
    {
        var ctx = CdpContext.New();
        Assert.Contains(
            "Browser context not found: context-404",
            CdpDomainFixtures.ErrorOf(await Storage.HandleAsync(
                "getCookies",
                CdpDomainFixtures.Json("""{"browserContextId": "context-404"}"""),
                ctx,
                null)),
            StringComparison.Ordinal);

        Assert.True((await Storage.HandleAsync(
            "trackCacheStorageForOrigin",
            CdpDomainFixtures.Json("{}"),
            ctx,
            null)).IsOk);
    }
}
