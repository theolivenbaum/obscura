namespace PocketCalculator.Net.Tests;

/// <summary>
/// The public suffix list (the full Mozilla list, embedded). It moved here from
/// <c>PocketCalculator.Js</c> so the cookie jar can use it; the registrable-domain facts
/// moved with it from <c>UrlTests</c>.
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

    // SECURITY.md L6: the curated list lacked these, so tenants of the same hosting
    // suffix shared a site and could set cookies for each other.
    [Theory]
    [InlineData("ngrok-free.app", "a.ngrok-free.app")]
    [InlineData("onrender.com", "app.onrender.com")]
    [InlineData("fly.dev", "app.fly.dev")]
    [InlineData("myshopify.com", "shop.myshopify.com")]
    [InlineData("r2.dev", "pub-123.r2.dev")]
    [InlineData("s3.eu-west-1.amazonaws.com", "bucket.s3.eu-west-1.amazonaws.com")]
    [InlineData("s3.dualstack.us-east-2.amazonaws.com", "b.s3.dualstack.us-east-2.amazonaws.com")]
    [InlineData("gov.br", "portal.gov.br")]
    public void FullListCoversHostingAndRegionalSuffixes(string suffix, string registrable)
    {
        Assert.True(PublicSuffixList.IsPublicSuffix(suffix));
        Assert.Equal(registrable, PublicSuffixList.RegistrableDomain(registrable));
        Assert.Equal(registrable, PublicSuffixList.RegistrableDomain("x.y." + registrable));
    }

    [Fact]
    public void WildcardAndExceptionRulesFollowTheListAlgorithm()
    {
        // *.kawasaki.jp and !city.kawasaki.jp
        Assert.True(PublicSuffixList.IsPublicSuffix("foo.kawasaki.jp"));
        Assert.Equal("bar.foo.kawasaki.jp", PublicSuffixList.RegistrableDomain("a.bar.foo.kawasaki.jp"));
        Assert.Equal("city.kawasaki.jp", PublicSuffixList.RegistrableDomain("www.city.kawasaki.jp"));
        // *.ck and !www.ck
        Assert.True(PublicSuffixList.IsPublicSuffix("anything.ck"));
        Assert.Equal("www.ck", PublicSuffixList.RegistrableDomain("a.www.ck"));
        // Case-insensitive, with a trailing dot kept on the answer.
        Assert.Equal("Example.CO.UK", PublicSuffixList.RegistrableDomain("www.Example.CO.UK"));
        Assert.Equal("example.com.", PublicSuffixList.RegistrableDomain("www.example.com."));
        // Malformed hosts have no registrable domain.
        Assert.Null(PublicSuffixList.RegistrableDomain("a..com"));
        Assert.Null(PublicSuffixList.RegistrableDomain(".com"));
    }

    [Fact]
    public void IdnRulesMatchPunycodedHosts()
    {
        // "\u6771\u4eac.jp" (Tokyo) is a rule; hosts reach the lookup in ACE form.
        var ace = new System.Globalization.IdnMapping().GetAscii("\u6771\u4eac.jp");
        Assert.True(PublicSuffixList.IsPublicSuffix(ace));
        Assert.Equal("shop." + ace, PublicSuffixList.RegistrableDomain("www.shop." + ace));
    }

    [Fact]
    public void LookupDoesNotAllocate()
    {
        PublicSuffixList.TryGetRegistrableDomain("warm.example.co.uk", out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            PublicSuffixList.TryGetRegistrableDomain("a.b.shop.myshopify.com", out _);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(PublicSuffixList.RuleCount > 9000);
    }
}
