using Obscura.Net;
using Xunit;

namespace Obscura.Browser.Tests;

/// <summary>
/// The xUnit port of the tests in <c>crates/obscura-browser/src/context.rs</c>.
/// </summary>
public sealed class ContextTests
{
    [Fact]
    public void WithFullOptionsPropagatesUserAgentToHttpClient()
    {
        BrowserContext context = BrowserContext.WithFullOptions("test", null, false, "Custom-UA/1.0");
        Assert.Equal("Custom-UA/1.0", context.UserAgent);
        Assert.Equal("Custom-UA/1.0", context.HttpClient.UserAgent);
    }

    [Fact]
    public void WithFullOptionsFallsBackToChromeDefault()
    {
        BrowserContext context = BrowserContext.WithFullOptions("test", null, false, null);
        Assert.Contains("Chrome", context.UserAgent, StringComparison.Ordinal);
        Assert.Contains("Chrome", context.HttpClient.UserAgent, StringComparison.Ordinal);
        Assert.Equal(context.UserAgent, context.HttpClient.UserAgent);
    }

    [Fact]
    public void WithOptionsKeepsDefaultUserAgent()
    {
        BrowserContext context = BrowserContext.WithOptions("test", null, false);
        Assert.Contains("Chrome", context.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void IsolatedCopyDoesNotShareMutableNetworkState()
    {
        BrowserContext source = BrowserContext.WithFullOptions("source", null, false, "Template-UA/1.0");
        source.CookieJar.SetCookie("sid=source", new Uri("https://example.com"));

        BrowserContext persistent = source.IsolatedCopy("persistent", true);
        BrowserContext incognito = source.IsolatedCopy("incognito", false);

        Assert.Single(persistent.CookieJar.GetAllCookies());
        Assert.Empty(incognito.CookieJar.GetAllCookies());
        persistent.CookieJar.Clear();
        persistent.HttpClient.SetUserAgent("Changed-UA/2.0");

        Assert.Single(source.CookieJar.GetAllCookies());
        Assert.Equal("Template-UA/1.0", source.HttpClient.UserAgent);
    }
}
