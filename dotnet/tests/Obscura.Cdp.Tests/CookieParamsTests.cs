using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>tests</c> module in
/// <c>crates/obscura-cdp/src/cookie_params.rs</c>.
/// </summary>
public sealed class CookieParamsTests
{
    [Fact]
    public void ParseWithExplicitDomainPath()
    {
        var v = new JsonObject
        {
            ["name"] = "session",
            ["value"] = "abc",
            ["domain"] = ".example.com",
            ["path"] = "/app",
            ["secure"] = true,
            ["httpOnly"] = true,
            ["sameSite"] = "Strict",
            ["expires"] = 1_900_000_000.0,
        };
        var parsed = CookieParams.ParseCdpCookie(v);
        Assert.NotNull(parsed);
        Assert.False(parsed.HostOnly);
        var c = parsed.Cookie;
        Assert.Equal("session", c.Name);
        Assert.Equal("abc", c.Value);
        Assert.Equal(".example.com", c.Domain);
        Assert.Equal("/app", c.Path);
        Assert.True(c.Secure);
        Assert.True(c.HttpOnly);
        Assert.Equal("Strict", c.SameSite);
        Assert.Equal(1_900_000_000L, c.Expires);
    }

    /// <summary>
    /// RFC 6265 5.1.4 default-path: a cookie set under <c>/v1/things</c> with no
    /// explicit Path is scoped to the directory <c>/v1</c>, not the full request
    /// path. Puppeteer's <c>page.setCookie</c> and Playwright's <c>addCookies</c>
    /// reach this path when the caller omits <c>path</c>.
    /// </summary>
    [Fact]
    public void ParseWithUrlFallbackForDomainAndPath()
    {
        var v = new JsonObject
        {
            ["name"] = "tok",
            ["value"] = "xyz",
            ["url"] = "https://api.example.com/v1/things",
        };
        var parsed = CookieParams.ParseCdpCookie(v);
        Assert.NotNull(parsed);
        Assert.True(parsed.HostOnly);
        var c = parsed.Cookie;
        Assert.Equal("api.example.com", c.Domain);
        Assert.Equal("/v1", c.Path);

        // Upstream 04418a5: a cookie set from a url is host-only in the jar too.
        var jar = new Obscura.Net.CookieJar();
        jar.SetCookiesFromCdpWithScope([(c, parsed.HostOnly)]);
        Assert.Contains("tok=xyz", jar.GetCookieHeader(new Uri("https://api.example.com/v1/item")), StringComparison.Ordinal);
        Assert.Equal(string.Empty, jar.GetCookieHeader(new Uri("https://sub.api.example.com/v1/item")));
    }

    /// <summary>An explicit Path attribute wins over the RFC default-path.</summary>
    [Fact]
    public void ParseExplicitPathOverridesDefault()
    {
        var v = new JsonObject
        {
            ["name"] = "tok",
            ["value"] = "xyz",
            ["url"] = "https://api.example.com/v1/things",
            ["path"] = "/v1/things",
        };
        var c = CookieParams.ParseCdpCookie(v)?.Cookie;
        Assert.NotNull(c);
        Assert.Equal("/v1/things", c.Path);
    }

    [Fact]
    public void ParseRejectsWhenNoDomainOrUrl() =>
        Assert.Null(CookieParams.ParseCdpCookie(
            new JsonObject { ["name"] = "x", ["value"] = "y" }));

    [Fact]
    public void ParseDefaultPathWhenUrlHasNoPath()
    {
        var v = new JsonObject
        {
            ["name"] = "tok",
            ["value"] = "v",
            ["domain"] = "example.com",
        };
        var c = CookieParams.ParseCdpCookie(v)?.Cookie;
        Assert.NotNull(c);
        Assert.Equal("/", c.Path);
    }

    [Fact]
    public void DeleteFilterRequiresName() =>
        Assert.Null(CookieParams.ParseDeleteCookiesParams(
            new JsonObject { ["domain"] = "example.com" }));

    [Fact]
    public void DeleteFilterUsesUrlForDomainAndPath()
    {
        var v = new JsonObject
        {
            ["name"] = "session",
            ["url"] = "https://example.com/admin",
        };
        var f = CookieParams.ParseDeleteCookiesParams(v);
        Assert.NotNull(f);
        Assert.Equal("session", f.Name);
        Assert.Equal("example.com", f.Domain);
        Assert.Equal("/admin", f.Path);
    }
}
