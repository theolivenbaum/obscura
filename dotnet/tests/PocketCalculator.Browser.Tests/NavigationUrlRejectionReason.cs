using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// What a rejected navigation reports, which is the observable end of the URL parser's
/// error channel.
/// </summary>
/// <remarks>
/// Rust's <c>navigate_single</c> is
/// <c>Url::parse(url_str).map_err(|e| PageError::InvalidUrl(e.to_string()))?</c>, so the
/// message names the component that was wrong. The port had no error channel out of
/// <c>UrlParser.Parse</c> and hardcoded "relative URL without a base" for every rejection,
/// which was right only for input that is not a URL at all.
/// </remarks>
public sealed class NavigationUrlRejectionReason
{
    [Theory]
    [InlineData("http://", "Invalid URL: empty host")]
    [InlineData("http://a:99999/", "Invalid URL: invalid port number")]
    [InlineData("http://[fe80::1", "Invalid URL: invalid IPv6 address")]
    [InlineData("https://xn--/", "Invalid URL: invalid international domain name")]
    [InlineData("http://1.2.3.4.5/", "Invalid URL: invalid IPv4 address")]
    [InlineData("not-a-url", "Invalid URL: relative URL without a base")]
    public async Task ARejectedNavigationNamesTheComponentThatWasWrong(string url, string expected)
    {
        using Page page = PageFixtures.NewPage("invalid-url-reason");

        PageException error = await Assert.ThrowsAsync<PageException>(
            () => page.NavigateAsync(url));

        Assert.Equal(PageErrorKind.InvalidUrl, error.Kind);
        Assert.Equal(expected, error.Message);
    }
}
