using System.Text.Json.Nodes;

using Xunit;

using PageDomain = Obscura.Cdp.Domains.Page;

namespace Obscura.Cdp.Tests;

/// <summary>
/// Port-specific regression cover for the <c>Page.Frame</c> fields that
/// <c>page.rs</c> derives from a parsed URL: <c>securityOrigin</c> (the crate's
/// <c>Origin::ascii_serialization()</c>) and <c>secureContextType</c> (its
/// <c>is_localhost</c> classification).
/// </summary>
/// <remarks>
/// These had no Rust counterpart because the Rust engine gets them from the <c>url</c>
/// crate for free. The port originally computed them from <see cref="System.Uri"/>, which
/// is not WHATWG-compliant: <c>Uri.Host</c> keeps an IDN host in Unicode where the crate
/// punycodes it, and <c>Uri</c> has no notion of a <c>blob:</c> URL's inner origin. Both
/// reach the wire, so both are asserted here.
/// </remarks>
public sealed class PageFrameOriginSerialization
{
    private static JsonObject Frame(string url) =>
        PageDomain.FrameValue("frame-1", null, "loader-1", url, "text/html");

    private static string OriginOf(string url) =>
        Frame(url).Get("securityOrigin").AsString()
        ?? throw new InvalidOperationException("securityOrigin must be a string");

    private static string SecureContextOf(string url) =>
        Frame(url).Get("secureContextType").AsString()
        ?? throw new InvalidOperationException("secureContextType must be a string");

    [Theory]
    // A tuple origin drops the scheme's default port and keeps any other.
    [InlineData("https://example.com/a?b#c", "https://example.com")]
    [InlineData("https://example.com:443/", "https://example.com")]
    [InlineData("https://example.com:8443/", "https://example.com:8443")]
    [InlineData("http://example.com/", "http://example.com")]
    [InlineData("http://example.com:8080/", "http://example.com:8080")]
    [InlineData("ws://example.com/socket", "ws://example.com")]
    [InlineData("wss://example.com/socket", "wss://example.com")]
    [InlineData("ftp://example.com/pub", "ftp://example.com")]
    [InlineData("http://127.0.0.1:9222/", "http://127.0.0.1:9222")]
    [InlineData("http://[::1]:9222/", "http://[::1]:9222")]
    // Every opaque origin serializes as the literal string "null".
    [InlineData("data:text/html,<b>a b</b>", "null")]
    [InlineData("file:///tmp/page.html", "null")]
    [InlineData("about:blank", "null")]
    [InlineData("obscura://internal/x", "null")]
    // Unparseable input has no origin at all.
    [InlineData("not a url", "null")]
    [InlineData("", "null")]
    public void SecurityOriginMatchesTheCrateAsciiSerialization(string url, string expected) =>
        Assert.Equal(expected, OriginOf(url));

    /// <summary>
    /// A <c>blob:</c> URL's origin is the origin of the URL in its path, which
    /// <see cref="System.Uri"/> cannot express: it reported <c>"null"</c> here.
    /// </summary>
    [Fact]
    public void ABlobUrlReportsTheOriginOfItsInnerUrl()
    {
        Assert.Equal(
            "https://example.com",
            OriginOf("blob:https://example.com/6b5c2a1e-0000-4000-8000-000000000000"));
        // A blob whose inner URL has no tuple origin still has none.
        Assert.Equal("null", OriginOf("blob:data:text/plain,x"));
    }

    /// <summary>
    /// The ASCII serialization of an origin is ASCII: the crate runs the host through
    /// IDNA, while <c>Uri.Host</c> handed back the Unicode label unchanged.
    /// </summary>
    [Fact]
    public void AnIdnHostIsPunycodedInTheSerializedOrigin()
    {
        string origin = OriginOf("https://ünicode.example/path");
        Assert.StartsWith("https://xn--", origin, StringComparison.Ordinal);
        Assert.True(
            origin.All(char.IsAscii),
            $"the ascii serialization of an origin must be ASCII, got {origin}");
    }

    /// <summary>
    /// The frame's <c>url</c> is the string the caller passed, never a reparse of it, so
    /// a WHATWG spelling arriving from <c>Page.UrlString()</c> travels through unchanged.
    /// </summary>
    [Fact]
    public void TheFrameUrlIsNotReserialized()
    {
        const string opaque = "data:text/html,<b>a b</b>";
        Assert.Equal(opaque, Frame(opaque).Get("url").AsString());
    }

    [Theory]
    [InlineData("https://example.com/", "Secure")]
    [InlineData("wss://example.com/", "Secure")]
    [InlineData("file:///tmp/page.html", "Secure")]
    [InlineData("about:blank", "Secure")]
    // is_localhost: the name itself, any subdomain of it, and either loopback literal.
    [InlineData("http://localhost/", "SecureLocalhost")]
    [InlineData("http://localhost:3000/", "SecureLocalhost")]
    [InlineData("http://LOCALHOST/", "SecureLocalhost")]
    [InlineData("http://app.localhost/", "SecureLocalhost")]
    [InlineData("ws://localhost:9222/devtools", "SecureLocalhost")]
    [InlineData("http://127.0.0.1/", "SecureLocalhost")]
    [InlineData("http://127.9.9.9/", "SecureLocalhost")]
    [InlineData("http://[::1]/", "SecureLocalhost")]
    // Not loopback, and not the localhost name.
    [InlineData("http://example.com/", "InsecureScheme")]
    [InlineData("http://notlocalhost/", "InsecureScheme")]
    [InlineData("http://localhost.example.com/", "InsecureScheme")]
    [InlineData("http://128.0.0.1/", "InsecureScheme")]
    [InlineData("http://[::2]/", "InsecureScheme")]
    [InlineData("data:text/html,hi", "InsecureScheme")]
    [InlineData("not a url", "InsecureScheme")]
    public void SecureContextTypeMatchesTheReferenceClassification(string url, string expected) =>
        Assert.Equal(expected, SecureContextOf(url));
}
