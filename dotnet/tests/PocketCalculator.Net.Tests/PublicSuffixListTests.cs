namespace PocketCalculator.Net.Tests;

/// <summary>
/// The curated public suffix list. It moved here from <c>PocketCalculator.Js</c> so the cookie jar
/// can use it; the registrable-domain facts moved with it from <c>UrlTests</c>.
/// </summary>
public class PublicSuffixListTests
{
    [Fact]
    public void RegistrableDomainFollowsThePublicSuffixRules()
    {
        Assert.Equal("example.com", PublicSuffixList.RegistrableDomain("www.example.com"));
        Assert.Equal("example.co.uk", PublicSuffixList.RegistrableDomain("a.b.example.co.uk"));
        Assert.Equal("user.github.io", PublicSuffixList.RegistrableDomain("x.user.github.io"));
        Assert.Equal("example.unknown-tld", PublicSuffixList.RegistrableDomain("a.example.unknown-tld"));
        Assert.Null(PublicSuffixList.RegistrableDomain("com"));
        Assert.Null(PublicSuffixList.RegistrableDomain("co.uk"));
        Assert.Null(PublicSuffixList.RegistrableDomain(""));
    }

    [Fact]
    public void IsPublicSuffixCoversSingleLabelMultiLabelAndPrivateSuffixes()
    {
        Assert.True(PublicSuffixList.IsPublicSuffix("com"));
        Assert.True(PublicSuffixList.IsPublicSuffix("localhost"));
        Assert.True(PublicSuffixList.IsPublicSuffix("co.uk"));
        Assert.True(PublicSuffixList.IsPublicSuffix("github.io"));
        Assert.False(PublicSuffixList.IsPublicSuffix("example.com"));
        Assert.False(PublicSuffixList.IsPublicSuffix("example.co.uk"));
        Assert.False(PublicSuffixList.IsPublicSuffix("alice.github.io"));
        Assert.False(PublicSuffixList.IsPublicSuffix(""));
    }
}
