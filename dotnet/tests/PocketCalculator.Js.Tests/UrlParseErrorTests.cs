using PocketCalculator.Js.Url;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The reason a URL was rejected, which the <c>url</c> crate carries as the <c>Err</c>
/// half of <c>Url::parse</c> and prints through <c>ParseError</c>'s <c>Display</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is a wire surface, not a diagnostic: <c>page.rs</c> maps a failed parse onto
/// <c>PageError::InvalidUrl(e.to_string())</c>, so the text reaches a client verbatim on
/// every rejected navigation. The port returned <c>UrlRecord?</c> with no error channel
/// and <c>Page.Navigation</c> hardcoded one string, so all four inputs in the first table
/// below reported "relative URL without a base" where the reference engine names the
/// component that was wrong.
/// </para>
/// <para>
/// The expected strings are the crate's <c>simple_enum_error!</c> table and are asserted
/// literally rather than through <see cref="UrlParseErrorText.Message"/>, so a reworded
/// message fails here instead of silently agreeing with itself.
/// </para>
/// </remarks>
public sealed class UrlParseErrorTests
{
    /// <summary>
    /// The four inputs diffed against the reference binary, each of which the port already
    /// rejected but described as a relative reference.
    /// </summary>
    [Theory]
    [InlineData("http://", "empty host")]
    [InlineData("http://a:99999/", "invalid port number")]
    [InlineData("http://[fe80::1", "invalid IPv6 address")]
    [InlineData("https://xn--/", "invalid international domain name")]
    public void TheFourDiffedInputsReportTheReasonTheReferenceReports(string input, string expected)
    {
        Assert.Null(UrlRecord.Parse(input, out UrlParseError error));
        Assert.Equal(expected, error.Message());
    }

    /// <summary>
    /// The rest of the reasons this parser can produce, so a later refactor cannot collapse
    /// them back onto one another.
    /// </summary>
    [Theory]
    // No scheme at all, and no base to resolve against: the one failure that is not a
    // malformed URL, and the reason the port used to give for every rejection.
    [InlineData("not-a-url", "relative URL without a base")]
    [InlineData("", "relative URL without a base")]
    [InlineData("/just/a/path", "relative URL without a base")]
    // Bracketed hosts: unterminated, and terminated but not an address.
    [InlineData("http://[fe80::1]extra/", "invalid IPv6 address")]
    [InlineData("http://[zz::1]/", "invalid IPv6 address")]
    [InlineData("http://[1:2:3:4:5:6:7:8:9]/", "invalid IPv6 address")]
    // A host that ends in a number is an IPv4 address or nothing.
    [InlineData("http://1.2.3.4.5/", "invalid IPv4 address")]
    [InlineData("http://999.1.1.1/", "invalid IPv4 address")]
    // Ports.
    [InlineData("http://example.com:65536/", "invalid port number")]
    [InlineData("http://example.com:abc/", "invalid port number")]
    // Special schemes need a host; the credentials form reports the same reason.
    [InlineData("https://", "empty host")]
    [InlineData("http://user@/path", "empty host")]
    // IDNA rejects the label outright, which is not the same as normalizing it away.
    [InlineData("http://xn--a/", "invalid international domain name")]
    // A non-special scheme parses an opaque host, whose failure is a forbidden code point.
    [InlineData("foo://a<b/", "invalid domain character")]
    public void EachRejectionNamesItsOwnComponent(string input, string expected)
    {
        Assert.Null(UrlRecord.Parse(input, out UrlParseError error));
        Assert.Equal(expected, error.Message());
    }

    /// <summary>
    /// A URL that parses reports no failure, which is the half that would break if the
    /// reason were threaded out of a path that also runs on success.
    /// </summary>
    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com:8443/a?b#c")]
    [InlineData("http://[::1]:8080/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("file:///tmp/x")]
    [InlineData("data:text/html,<b>a b</b>")]
    [InlineData("about:blank")]
    public void AParsedUrlReportsNoFailure(string input) =>
        Assert.NotNull(UrlRecord.Parse(input, out _));

    /// <summary>
    /// The reason travels out of <c>HostParser</c> as well, which is where five of the
    /// eight are decided.
    /// </summary>
    [Fact]
    public void TheHostParserReportsItsOwnReasons()
    {
        Assert.False(HostParser.TryParse("[fe80::1", out _, out UrlParseError unterminated));
        Assert.Equal(UrlParseError.InvalidIpv6Address, unterminated);

        Assert.False(HostParser.TryParse("xn--", out _, out UrlParseError idna));
        Assert.Equal(UrlParseError.IdnaError, idna);

        Assert.False(HostParser.TryParse("1.2.3.4.5", out _, out UrlParseError ipv4));
        Assert.Equal(UrlParseError.InvalidIpv4Address, ipv4);

        Assert.False(HostParser.TryParseOpaque("a<b", out _, out UrlParseError forbidden));
        Assert.Equal(UrlParseError.InvalidDomainCharacter, forbidden);

        Assert.True(HostParser.TryParse("example.com", out _, out _));
    }

    /// <summary>
    /// The two-argument overloads are what the rest of the engine calls, so they must keep
    /// answering exactly as before.
    /// </summary>
    [Fact]
    public void TheOverloadsWithoutAReasonAreUnchanged()
    {
        Assert.Null(UrlRecord.Parse("http://"));
        Assert.NotNull(UrlRecord.Parse("http://example.com/"));
        Assert.False(HostParser.TryParse("[fe80::1", out _));
        Assert.True(HostParser.TryParse("example.com", out ParsedHost host));
        Assert.Equal("example.com", host.Domain);
    }
}
