namespace PocketCalculator.Net.Tests;

/// <summary>
/// Referrer policy parsing and application, each case measured on Chromium 141. The Rust
/// engine has no referrer policy: every request used strict-origin-when-cross-origin.
/// </summary>
public sealed class ReferrerPolicyTests
{
    [Theory]
    [InlineData("no-referrer, unsafe-url", ReferrerPolicy.UnsafeUrl)]
    [InlineData("unsafe-url, bogus", ReferrerPolicy.UnsafeUrl)]
    [InlineData("bogus, unsafe-url", ReferrerPolicy.UnsafeUrl)]
    [InlineData("unsafe-url,no-referrer", ReferrerPolicy.NoReferrer)]
    [InlineData("UNSAFE-URL", ReferrerPolicy.UnsafeUrl)]
    [InlineData(" unsafe-url ", ReferrerPolicy.UnsafeUrl)]
    [InlineData("unsafe-url, , ", ReferrerPolicy.UnsafeUrl)]
    [InlineData("a, unsafe-url, b", ReferrerPolicy.UnsafeUrl)]
    [InlineData("same-origin", ReferrerPolicy.SameOrigin)]
    [InlineData("strict-origin", ReferrerPolicy.StrictOrigin)]
    [InlineData("origin-when-cross-origin", ReferrerPolicy.OriginWhenCrossOrigin)]
    [InlineData("no-referrer-when-downgrade", ReferrerPolicy.NoReferrerWhenDowngrade)]
    [InlineData("always", null)]
    [InlineData("no-referrer, bogus, unsafe-url, bogus2", null)]
    [InlineData("", null)]
    public void HeaderParsesAsChromiumDoes(string header, ReferrerPolicy? expected) =>
        Assert.Equal(expected, ReferrerPolicies.ParseHeader(header));

    [Theory]
    [InlineData("UNSAFE-URL", ReferrerPolicy.UnsafeUrl)]
    [InlineData("never", ReferrerPolicy.NoReferrer)]
    [InlineData("always", ReferrerPolicy.UnsafeUrl)]
    [InlineData("default", ReferrerPolicy.StrictOriginWhenCrossOrigin)]
    [InlineData("origin-when-crossorigin", ReferrerPolicy.OriginWhenCrossOrigin)]
    [InlineData(" unsafe-url ", null)]
    [InlineData("no-referrer, origin", null)]
    [InlineData("", null)]
    [InlineData("bogus", null)]
    public void MetaParsesAsChromiumDoes(string content, ReferrerPolicy? expected) =>
        Assert.Equal(expected, ReferrerPolicies.ParseMeta(content));

    [Fact]
    public void AttributeTakesStandardTokensOnly()
    {
        Assert.Equal(ReferrerPolicy.Origin, ReferrerPolicies.ParseAttribute("ORIGIN"));
        Assert.Null(ReferrerPolicies.ParseAttribute("never"));
        Assert.Null(ReferrerPolicies.ParseAttribute(""));
    }

    private static readonly Uri Page = new("http://a.test:8711/default?q=1#frag");
    private static readonly Uri Same = new("http://a.test:8711/f");
    private static readonly Uri Cross = new("http://b.test:8712/f");

    [Theory]
    [InlineData(ReferrerPolicy.StrictOriginWhenCrossOrigin, "http://a.test:8711/default?q=1", "http://a.test:8711/")]
    [InlineData(ReferrerPolicy.NoReferrer, null, null)]
    [InlineData(ReferrerPolicy.UnsafeUrl, "http://a.test:8711/default?q=1", "http://a.test:8711/default?q=1")]
    [InlineData(ReferrerPolicy.Origin, "http://a.test:8711/", "http://a.test:8711/")]
    [InlineData(ReferrerPolicy.OriginWhenCrossOrigin, "http://a.test:8711/default?q=1", "http://a.test:8711/")]
    [InlineData(ReferrerPolicy.SameOrigin, "http://a.test:8711/default?q=1", null)]
    [InlineData(ReferrerPolicy.StrictOrigin, "http://a.test:8711/", "http://a.test:8711/")]
    [InlineData(ReferrerPolicy.NoReferrerWhenDowngrade, "http://a.test:8711/default?q=1", "http://a.test:8711/default?q=1")]
    public void PoliciesApplyToSameAndCrossOriginTargets(ReferrerPolicy policy, string? same, string? cross)
    {
        Assert.Equal(same, ReferrerPolicies.Referrer(Page, Same, policy));
        Assert.Equal(cross, ReferrerPolicies.Referrer(Page, Cross, policy));
    }

    [Fact]
    public void DowngradesStripAndLocalhostIsTrustworthy()
    {
        var secure = new Uri("https://user:pw@secure.test/p?x#y");
        Assert.Null(ReferrerPolicies.Referrer(secure, new Uri("http://plain.test/"), ReferrerPolicy.StrictOriginWhenCrossOrigin));
        Assert.Null(ReferrerPolicies.Referrer(secure, new Uri("http://plain.test/"), ReferrerPolicy.NoReferrerWhenDowngrade));
        Assert.Equal(
            "https://secure.test/",
            ReferrerPolicies.Referrer(secure, new Uri("http://plain.test/"), ReferrerPolicy.Origin));
        Assert.Equal(
            "https://secure.test/",
            ReferrerPolicies.Referrer(secure, new Uri("http://127.0.0.1:9/"), ReferrerPolicy.StrictOriginWhenCrossOrigin));
        // Userinfo and fragment never go out.
        Assert.Equal(
            "https://secure.test/p?x",
            ReferrerPolicies.Referrer(secure, new Uri("https://secure.test/q"), ReferrerPolicy.StrictOriginWhenCrossOrigin));
    }

    [Fact]
    public void ReferrersLongerThan4096AreCutToTheirOrigin()
    {
        var longPage = new Uri("http://a.test:8711/default?" + new string('x', 5000));
        Assert.Equal("http://a.test:8711/", ReferrerPolicies.Referrer(longPage, Same, ReferrerPolicy.StrictOriginWhenCrossOrigin));
        Assert.Equal("http://a.test:8711/", ReferrerPolicies.Referrer(longPage, Cross, ReferrerPolicy.UnsafeUrl));
    }

    [Fact]
    public void RequestProfileCarriesThePolicyAcrossRedirects()
    {
        var request = ResourceRequest.Subresource(ResourceType.Image, Page) with { ReferrerPolicy = ReferrerPolicy.UnsafeUrl };
        Assert.Equal("http://a.test:8711/default?q=1", PocketCalculatorHttpClient.RequestReferrer(request, Cross));
        var none = request with { ReferrerPolicy = ReferrerPolicy.NoReferrer };
        Assert.Null(PocketCalculatorHttpClient.RequestReferrer(none, Same, []));
    }
}
