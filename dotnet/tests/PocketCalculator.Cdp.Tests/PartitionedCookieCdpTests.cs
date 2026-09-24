using System.Text.Json.Nodes;
using PocketCalculator.Cdp.Domains;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// CHIPS on the CDP cookie surfaces, as Chromium 141 answers them: a partitioned cookie
/// reports <c>partitionKey</c> as <c>{topLevelSite, hasCrossSiteAncestor}</c> (absent on an
/// ordinary cookie), <c>Network.setCookie</c> takes the same object with both fields and
/// reduces the site, a malformed key or a partitioned cookie that is not Secure fails the
/// call, and
/// <c>deleteCookies</c> without a key leaves partitioned cookies alone.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class PartitionedCookieCdpTests
{
    private static Task<DomainResult> HandleAsync(string method, string parameters, CdpContext ctx) =>
        Network.HandleAsync(method, CdpDomainFixtures.Json(parameters), ctx, null);

    [Fact]
    public async Task SetCookieWithAPartitionKeyRoundTripsInChromiumsShape()
    {
        var ctx = CdpContext.New();
        JsonNode set = CdpDomainFixtures.Unwrap(await HandleAsync(
            "setCookie",
            """
            {"name": "p", "value": "1", "url": "https://b.example.org/",
             "partitionKey": {"topLevelSite": "https://a.example.com", "hasCrossSiteAncestor": true}}
            """,
            ctx));
        Assert.True(set["success"]!.GetValue<bool>());
        Assert.True((await HandleAsync(
            "setCookie", """{"name": "p", "value": "plain", "url": "https://b.example.org/", "secure": true}""", ctx)).IsOk);

        JsonNode all = CdpDomainFixtures.Unwrap(await HandleAsync("getAllCookies", "{}", ctx));
        var cookies = all["cookies"]!.AsArray();
        Assert.Equal(2, cookies.Count);
        JsonNode partitioned = cookies.Single(c => c!["value"]!.GetValue<string>() == "1")!;
        Assert.True(partitioned["secure"]!.GetValue<bool>());
        Assert.Equal(
            """{"topLevelSite":"https://example.com","hasCrossSiteAncestor":true}""",
            partitioned["partitionKey"]!.ToJsonString());
        Assert.Null(cookies.Single(c => c!["value"]!.GetValue<string>() == "plain")!["partitionKey"]);
    }

    [Theory]
    [InlineData("""{"topLevelSite": "not a url", "hasCrossSiteAncestor": true}""")]
    [InlineData("""{"topLevelSite": "https://a.example.com"}""")]
    [InlineData("\"https://a.example.com\"")]
    public async Task MalformedPartitionKeyFailsSetCookie(string key)
    {
        var ctx = CdpContext.New();
        DomainResult result = await HandleAsync(
            "setCookie", $$"""{"name": "p", "value": "1", "url": "https://b.example.org/", "partitionKey": {{key}}}""", ctx);
        Assert.False(result.IsOk);
        Assert.Empty(ctx.DefaultContext.CookieJar.GetAllCookies());
    }

    [Fact]
    public async Task DeleteCookiesIsScopedToOnePartition()
    {
        var ctx = CdpContext.New();
        const string key = """{"topLevelSite": "https://example.com", "hasCrossSiteAncestor": true}""";
        Assert.True((await HandleAsync(
            "setCookies",
            $$"""
            {"cookies": [
                {"name": "p", "value": "part", "domain": "b.example.org", "path": "/", "secure": true, "partitionKey": {{key}}},
                {"name": "p", "value": "plain", "domain": "b.example.org", "path": "/", "secure": true}
            ]}
            """,
            ctx)).IsOk);

        Assert.True((await HandleAsync("deleteCookies", """{"name": "p", "domain": "b.example.org"}""", ctx)).IsOk);
        Assert.Equal("part", Assert.Single(ctx.DefaultContext.CookieJar.GetAllCookies()).Value);

        Assert.True((await HandleAsync(
            "deleteCookies", $$"""{"name": "p", "domain": "b.example.org", "partitionKey": {{key}}}""", ctx)).IsOk);
        Assert.Empty(ctx.DefaultContext.CookieJar.GetAllCookies());
    }

    private const string PlainKey = """{"topLevelSite": "http://plain.test", "hasCrossSiteAncestor": false}""";

    /// <summary>
    /// Chromium 141 refuses a partitioned cookie that is not Secure ("Sanitizing cookie
    /// failed"): one for an http url, or with a domain, without <c>secure</c>. The port
    /// used to force Secure on and accept it.
    /// </summary>
    [Theory]
    [InlineData("\"url\": \"http://plain.test/\"")]
    [InlineData("\"domain\": \"plain.test\", \"path\": \"/\"")]
    public async Task SetCookieRefusesAPartitionedCookieThatIsNotSecure(string scope)
    {
        var ctx = CdpContext.New();
        DomainResult result = await HandleAsync(
            "setCookie", $$"""{"name": "p", "value": "1", {{scope}}, "partitionKey": {{PlainKey}}}""", ctx);
        Assert.Equal("Sanitizing cookie failed", CdpDomainFixtures.ErrorOf(result));
        Assert.Empty(ctx.DefaultContext.CookieJar.GetAllCookies());

        JsonNode set = CdpDomainFixtures.Unwrap(await HandleAsync(
            "setCookie", $$"""{"name": "p", "value": "1", {{scope}}, "secure": true, "partitionKey": {{PlainKey}}}""", ctx));
        Assert.True(set["success"]!.GetValue<bool>());
        Assert.True(Assert.Single(ctx.DefaultContext.CookieJar.GetAllCookies()).Secure);
    }

    /// <summary>One cookie Chromium would refuse fails a whole <c>setCookies</c> call, which then sets none.</summary>
    [Theory]
    [InlineData("Network")]
    [InlineData("Storage")]
    public async Task SetCookiesWithOneRefusedCookieSetsNone(string domain)
    {
        var ctx = CdpContext.New();
        JsonNode parameters = CdpDomainFixtures.Json($$"""
            {"cookies": [
                {"name": "ok", "value": "1", "url": "http://plain.test/"},
                {"name": "p", "value": "1", "url": "http://plain.test/", "partitionKey": {{PlainKey}}}
            ]}
            """);
        DomainResult result = domain == "Network"
            ? await Network.HandleAsync("setCookies", parameters, ctx, null)
            : await Storage.HandleAsync("setCookies", parameters, ctx, null);
        Assert.Equal("Invalid cookie fields", CdpDomainFixtures.ErrorOf(result));
        Assert.Empty(ctx.DefaultContext.CookieJar.GetAllCookies());
    }

    /// <summary>A cookie set for an https url is Secure in Chromium 141, whatever <c>secure</c> says.</summary>
    [Fact]
    public async Task SetCookieForAnHttpsUrlIsSecure()
    {
        var ctx = CdpContext.New();
        Assert.True(CdpDomainFixtures.Unwrap(await HandleAsync(
            "setCookie", """{"name": "s", "value": "1", "url": "https://plain.test/", "secure": false}""", ctx))["success"]!.GetValue<bool>());
        Assert.True(CdpDomainFixtures.Unwrap(await HandleAsync(
            "setCookie", """{"name": "p", "value": "1", "url": "https://plain.test/", "partitionKey": {{PlainKey}}}""".Replace("{{PlainKey}}", PlainKey, StringComparison.Ordinal), ctx))["success"]!.GetValue<bool>());
        Assert.All(ctx.DefaultContext.CookieJar.GetAllCookies(), cookie => Assert.True(cookie.Secure));
        JsonNode forHttp = CdpDomainFixtures.Unwrap(await HandleAsync(
            "getCookies", """{"urls": ["http://plain.test/"]}""", ctx));
        Assert.Empty(forHttp["cookies"]!.AsArray());
    }

    /// <summary>Chromium 141 answers a SameSite=None cookie that is not Secure with success: false.</summary>
    [Fact]
    public async Task SetCookieSameSiteNoneWithoutSecureIsNotStored()
    {
        var ctx = CdpContext.New();
        JsonNode set = CdpDomainFixtures.Unwrap(await HandleAsync(
            "setCookie", """{"name": "n", "value": "1", "url": "http://plain.test/", "sameSite": "None"}""", ctx));
        Assert.False(set["success"]!.GetValue<bool>());
        Assert.Empty(ctx.DefaultContext.CookieJar.GetAllCookies());
    }
}
