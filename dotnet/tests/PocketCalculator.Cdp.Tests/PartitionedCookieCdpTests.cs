using System.Text.Json.Nodes;
using PocketCalculator.Cdp.Domains;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// CHIPS on the CDP cookie surfaces, as Chromium 141 answers them: a partitioned cookie
/// reports <c>partitionKey</c> as <c>{topLevelSite, hasCrossSiteAncestor}</c> (absent on an
/// ordinary cookie), <c>Network.setCookie</c> takes the same object with both fields,
/// reduces the site and forces Secure, a malformed key fails the call, and
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
}
