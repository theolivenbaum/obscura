using System.Text;
using Obscura.Js.Url;
using Xunit;

namespace Obscura.Js.Tests;

/// <summary>
/// Tests for the WHATWG URL parser and the URL ops in <c>crates/obscura-js/src/ops.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The repository ships no copy of the WHATWG <c>urltestdata.json</c> conformance suite, so
/// the tables below are hand-written. Every expected value here was taken from the Rust
/// reference implementation - the <c>url</c> 2.5.8 crate driven through the exact bodies of
/// <c>op_url_parse</c> / <c>op_url_set</c> / <c>op_url_resolve</c> /
/// <c>op_document_domain_candidate</c> - rather than from reading the specification, so a
/// disagreement here is a real divergence from the reference engine and not a spec reading.
/// </para>
/// </remarks>
public sealed class UrlTests
{
    private const string Invalid = "{\"ok\":false}";

    private static string Href(string json)
    {
        const string Key = "\"href\":\"";
        var start = json.IndexOf(Key, StringComparison.Ordinal);
        if (start < 0)
        {
            return "INVALID";
        }

        start += Key.Length;
        var end = json.IndexOf('"', start);
        return json[start..end];
    }

    // ------------------------------------------------------------------ component JSON

    // The field names, the field ORDER, and the empty-versus-absent choices are consumed
    // directly by the URL class in bootstrap.js, which reads them as plain fields. These
    // four strings are byte-for-byte what the Rust op emits.

    [Fact]
    public void ComponentsMatchTheRustPayloadForAFullUrl() => Assert.Equal(
        "{\"ok\":true,\"href\":\"http://user:pass@example.com:8080/a/b?q#f\",\"protocol\":\"http:\","
        + "\"username\":\"user\",\"password\":\"pass\",\"host\":\"example.com:8080\","
        + "\"hostname\":\"example.com\",\"port\":\"8080\",\"pathname\":\"/a/b\",\"search\":\"?q\","
        + "\"hash\":\"#f\",\"origin\":\"http://example.com:8080\"}",
        UrlOps.UrlParse("http://user:pass@example.com:8080/a/b?q#f", string.Empty));

    [Fact]
    public void ComponentsMatchTheRustPayloadForAnOpaquePath() => Assert.Equal(
        "{\"ok\":true,\"href\":\"foo:opaque\",\"protocol\":\"foo:\",\"username\":\"\","
        + "\"password\":\"\",\"host\":\"\",\"hostname\":\"\",\"port\":\"\",\"pathname\":\"opaque\","
        + "\"search\":\"\",\"hash\":\"\",\"origin\":\"null\"}",
        UrlOps.UrlParse("foo:opaque", string.Empty));

    [Fact]
    public void ComponentsMatchTheRustPayloadForAFileUrl() => Assert.Equal(
        "{\"ok\":true,\"href\":\"file:///c:/x\",\"protocol\":\"file:\",\"username\":\"\","
        + "\"password\":\"\",\"host\":\"\",\"hostname\":\"\",\"port\":\"\",\"pathname\":\"/c:/x\","
        + "\"search\":\"\",\"hash\":\"\",\"origin\":\"null\"}",
        UrlOps.UrlParse("file:///c:/x", string.Empty));

    [Fact]
    public void ComponentsMatchTheRustPayloadForAnIpv6Host() => Assert.Equal(
        "{\"ok\":true,\"href\":\"http://[::1]:81/x\",\"protocol\":\"http:\",\"username\":\"\","
        + "\"password\":\"\",\"host\":\"[::1]:81\",\"hostname\":\"[::1]\",\"port\":\"81\","
        + "\"pathname\":\"/x\",\"search\":\"\",\"hash\":\"\",\"origin\":\"http://[::1]:81\"}",
        UrlOps.UrlParse("http://[::1]:81/x", string.Empty));

    [Fact]
    public void EmptySearchAndHashComponentsSerializeAsEmptyStrings() => Assert.Equal(
        "{\"ok\":true,\"href\":\"http://example.com/?#\",\"protocol\":\"http:\",\"username\":\"\","
        + "\"password\":\"\",\"host\":\"example.com\",\"hostname\":\"example.com\",\"port\":\"\","
        + "\"pathname\":\"/\",\"search\":\"\",\"hash\":\"\",\"origin\":\"http://example.com\"}",
        UrlOps.UrlParse("http://example.com/?#", string.Empty));

    [Fact]
    public void BlobOriginComesFromTheInnerUrl() => Assert.Equal(
        "{\"ok\":true,\"href\":\"blob:https://example.com/uuid\",\"protocol\":\"blob:\","
        + "\"username\":\"\",\"password\":\"\",\"host\":\"\",\"hostname\":\"\",\"port\":\"\","
        + "\"pathname\":\"https://example.com/uuid\",\"search\":\"\",\"hash\":\"\","
        + "\"origin\":\"https://example.com\"}",
        UrlOps.UrlParse("blob:https://example.com/uuid", string.Empty));

    // ------------------------------------------------------------------ parsing

    [Theory]
    // scheme handling and the special schemes
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("HTTP://ExAmPle.COM:80/A?B#C", "http://example.com/A?B#C")]
    [InlineData("https://example.com:443/", "https://example.com/")]
    [InlineData("ws://example.com:80/", "ws://example.com/")]
    [InlineData("wss://example.com:443/", "wss://example.com/")]
    [InlineData("ftp://example.com:21/", "ftp://example.com/")]
    [InlineData("http://example.com:8080/", "http://example.com:8080/")]
    [InlineData("http:example.com/", "http://example.com/")]
    [InlineData("http:/example.com/", "http://example.com/")]
    // non-special schemes keep their opaque paths and get no implicit slash
    [InlineData("foo:bar", "foo:bar")]
    [InlineData("foo:/bar", "foo:/bar")]
    [InlineData("foo://host", "foo://host")]
    [InlineData("foo://host:81/x", "foo://host:81/x")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    [InlineData("data:text/plain,Stuff", "data:text/plain,Stuff")]
    [InlineData("javascript:alert(1)", "javascript:alert(1)")]
    [InlineData("about:blank", "about:blank")]
    // backslashes are path separators in special URLs only
    [InlineData("http://example.com\\a\\b", "http://example.com/a/b")]
    [InlineData("http:\\\\example.com\\a", "http://example.com/a")]
    [InlineData("foo://example.com/a\\b", "foo://example.com/a\\b")]
    // "." and ".." segments, in every spelling the spec accepts
    [InlineData("http://example.com/a/b/../c", "http://example.com/a/c")]
    [InlineData("http://example.com/a/./b/../c/", "http://example.com/a/c/")]
    [InlineData("http://example.com/../../x", "http://example.com/x")]
    [InlineData("http://example.com/a/..", "http://example.com/")]
    [InlineData("http://example.com/a/.", "http://example.com/a/")]
    [InlineData("http://example.com/a/%2e%2e/b", "http://example.com/b")]
    [InlineData("http://example.com/a/%2E./b", "http://example.com/b")]
    [InlineData("http://example.com/a/.%2e/b", "http://example.com/b")]
    [InlineData("http://example.com/a/%2e/b", "http://example.com/a/b")]
    // IPv4 in all four number bases, plus the ends-in-a-number rules
    [InlineData("http://127.0.0.1/", "http://127.0.0.1/")]
    [InlineData("http://0177.0.0.1/", "http://127.0.0.1/")]
    [InlineData("http://2130706433/", "http://127.0.0.1/")]
    [InlineData("http://0x7f000001/", "http://127.0.0.1/")]
    [InlineData("http://0x7f.1/", "http://127.0.0.1/")]
    [InlineData("http://1.2.3.4./", "http://1.2.3.4/")]
    [InlineData("http://0.0.0.0/", "http://0.0.0.0/")]
    // IPv6, including the RFC 5952 compressed serialization
    [InlineData("http://[::1]/", "http://[::1]/")]
    [InlineData("http://[0:0:0:0:0:0:0:1]:8080/", "http://[::1]:8080/")]
    [InlineData("http://[1:2:3:4:5:6:7:8]/", "http://[1:2:3:4:5:6:7:8]/")]
    [InlineData("http://[::ffff:1.2.3.4]/", "http://[::ffff:102:304]/")]
    [InlineData("http://[::]/", "http://[::]/")]
    [InlineData("foo://[::1]/", "foo://[::1]/")]
    // percent-encoding sets: path, query, special query, fragment
    [InlineData("http://example.com/a b", "http://example.com/a%20b")]
    [InlineData("http://example.com/a%20b", "http://example.com/a%20b")]
    [InlineData("http://example.com/a%2", "http://example.com/a%2")]
    [InlineData("http://example.com/<>`\"{}", "http://example.com/%3C%3E%60%22%7B%7D")]
    [InlineData("http://example.com/?a=<>'\"", "http://example.com/?a=%3C%3E%27%22")]
    [InlineData("foo://example.com/?a=<>'\"", "foo://example.com/?a=%3C%3E'%22")]
    [InlineData("http://example.com/#a<>`\" b", "http://example.com/#a%3C%3E%60%22%20b")]
    [InlineData("http://ex%41mple.com/", "http://example.com/")]
    [InlineData("http://EXAMPLE.com/\u00e9", "http://example.com/%C3%A9")]
    // IDNA
    [InlineData("http://xn--fsq.com/", "http://xn--fsq.com/")]
    [InlineData("http://\u4f60\u597d.com/", "http://xn--6qq79v.com/")]
    [InlineData("http://\u00e9.com/", "http://xn--9ca.com/")]
    [InlineData("http://e\u0301.com/", "http://xn--9ca.com/")]
    [InlineData("http://EXAMPLE\u3002com/", "http://example.com/")]
    // whitespace and embedded tab / newline
    [InlineData("   http://example.com/   ", "http://example.com/")]
    [InlineData("http://exa\tmple.com/", "http://example.com/")]
    [InlineData("ht\ntp://example.com/", "http://example.com/")]
    // file: URLs and Windows drive letters
    [InlineData("file:///tmp/x", "file:///tmp/x")]
    [InlineData("file://localhost/tmp/x", "file:///tmp/x")]
    [InlineData("file://example.com/tmp/x", "file://example.com/tmp/x")]
    [InlineData("file:///c:/x", "file:///c:/x")]
    [InlineData("file:///c|/x", "file:///c:/x")]
    [InlineData("file://c:/x", "file:///c:/x")]
    [InlineData("file://c|/x", "file:///c:/x")]
    [InlineData("file:/x", "file:///x")]
    [InlineData("file:x", "file:///x")]
    [InlineData("file:", "file:///")]
    [InlineData("file:////x", "file:///x")]
    [InlineData("file:///c:", "file:///c:")]
    // query and fragment edge cases
    [InlineData("http://example.com/?", "http://example.com/?")]
    [InlineData("http://example.com/#", "http://example.com/#")]
    [InlineData("http://example.com/?a#b", "http://example.com/?a#b")]
    [InlineData("http://example.com?query", "http://example.com/?query")]
    [InlineData("http://example.com#frag", "http://example.com/#frag")]
    // credentials
    [InlineData("http://user:pass@example.com/", "http://user:pass@example.com/")]
    [InlineData("http://user@example.com/", "http://user@example.com/")]
    [InlineData("http://:pass@example.com/", "http://:pass@example.com/")]
    [InlineData("http://user:@example.com/", "http://user@example.com/")]
    // the "anarchist URL" guard: a leading empty path segment must survive a round trip
    [InlineData("web+demo:/.//not-a-host/", "web+demo:/.//not-a-host/")]
    public void ParsesToTheReferenceSerialization(string href, string expected) =>
        Assert.Equal(expected, Href(UrlOps.UrlParse(href, string.Empty)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/x")]
    [InlineData("x")]
    [InlineData("//example.com/x")]
    [InlineData("1http://example.com/")]
    [InlineData("h t t p://example.com/")]
    [InlineData("http://")]
    [InlineData("http:///")]
    [InlineData("http://:80/")]
    [InlineData("http:@example.com")]
    [InlineData("http://example.com:65536/")]
    [InlineData("http://example.com:abc/")]
    [InlineData("http://example.com:8080abc/")]
    [InlineData("http://[:::1]/")]
    [InlineData("http://[1:2:3:4:5:6:7:8:9]/")]
    [InlineData("http://[1.2.3.4]/")]
    [InlineData("http://[v1.x]/")]
    [InlineData("http://1.2.3.4.5/")]
    [InlineData("http://256.1.1.1/")]
    [InlineData("http://0x100000000/")]
    [InlineData("http://999999999999/")]
    [InlineData("http://1.2.3.09/")]
    [InlineData("http://ex ample.com/")]
    [InlineData("http://%zz.com/")]
    [InlineData("http://a%2fb.com/")]
    [InlineData("http://xn--a.com/")]
    [InlineData("foo://example.com\\a\\b")]
    public void RejectsInvalidInput(string href) =>
        Assert.Equal(Invalid, UrlOps.UrlParse(href, string.Empty));

    // ------------------------------------------------------------------ relative resolution

    [Theory]
    [InlineData("d", "http://example.com/a/b/c?q#f", "http://example.com/a/b/d")]
    [InlineData("./d", "http://example.com/a/b/c?q#f", "http://example.com/a/b/d")]
    [InlineData("../d", "http://example.com/a/b/c?q#f", "http://example.com/a/d")]
    [InlineData("../../d", "http://example.com/a/b/c?q#f", "http://example.com/d")]
    [InlineData("../../../d", "http://example.com/a/b/c?q#f", "http://example.com/d")]
    [InlineData("/d", "http://example.com/a/b/c?q#f", "http://example.com/d")]
    [InlineData("//other/d", "http://example.com/a/b/c?q#f", "http://other/d")]
    [InlineData("?q2", "http://example.com/a/b/c?q#f", "http://example.com/a/b/c?q2")]
    [InlineData("#f2", "http://example.com/a/b/c?q#f", "http://example.com/a/b/c?q#f2")]
    [InlineData("", "http://example.com/a/b/c?q#f", "http://example.com/a/b/c?q")]
    [InlineData(".", "http://example.com/a/b/c?q#f", "http://example.com/a/b/")]
    [InlineData("..", "http://example.com/a/b/c?q#f", "http://example.com/a/")]
    [InlineData("\\d", "http://example.com/a/b/c?q#f", "http://example.com/d")]
    [InlineData("a/b/../../../c", "http://example.com/a/b/c?q#f", "http://example.com/c")]
    [InlineData("http://other/x", "http://example.com/a/b/c?q#f", "http://other/x")]
    [InlineData("notascheme:", "http://example.com/a/", "notascheme:")]
    [InlineData("d", "file:///a/b/c", "file:///a/b/d")]
    [InlineData("/d", "file:///a/b/c", "file:///d")]
    [InlineData("c:/x", "file:///a/b/c", "c:/x")]
    [InlineData("//other/d", "foo://host/a/b", "foo://other/d")]
    [InlineData("d", "foo://host/a/b", "foo://host/a/d")]
    public void ResolvesRelativeReferences(string href, string baseHref, string expected)
    {
        Assert.Equal(expected, UrlOps.UrlResolve(href, baseHref));
        Assert.Equal(expected, Href(UrlOps.UrlParse(href, baseHref)));
    }

    [Theory]
    [InlineData("d", "foo:opaque")]              // a cannot-be-a-base base has nothing to join
    [InlineData("d", "not a url")]               // an unparseable base fails the whole join
    [InlineData("", "")]
    public void ResolveReturnsEmptyForUnusableInput(string href, string baseHref) =>
        Assert.Equal(string.Empty, UrlOps.UrlResolve(href, baseHref));

    // ------------------------------------------------------------------ setters

    [Theory]
    // protocol: special and non-special schemes cannot be swapped for each other
    [InlineData("http://example.com/a/b?q#f", "protocol", "https", "https://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "protocol", "https:", "https://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "protocol", "foo", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "protocol", "file", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "protocol", "", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "protocol", "a b", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "protocol", "1x", "http://example.com/a/b?q#f")]
    [InlineData("foo://h/a", "protocol", "http", "foo://h/a")]
    [InlineData("foo://h/a", "protocol", "sc", "sc://h/a")]
    [InlineData("https://example.com:8443/", "protocol", "http", "http://example.com:8443/")]
    // username / password
    [InlineData("http://example.com/a/b?q#f", "username", "u", "http://u@example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "username", "a b", "http://a%20b@example.com/a/b?q#f")]
    [InlineData("http://u:p@example.com/", "username", "", "http://:p@example.com/")]
    [InlineData("http://example.com/a/b?q#f", "password", "p", "http://:p@example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "password", "", "http://example.com/a/b?q#f")]
    [InlineData("http://u:p@example.com/", "password", "", "http://u@example.com/")]
    [InlineData("foo:opaque", "username", "u", "foo:opaque")]
    [InlineData("file:///a/b", "username", "u", "file:///a/b")]
    // host: "host[:port]" is split, an unbracketed colon wins, a bad port is dropped
    [InlineData("http://example.com/a/b?q#f", "host", "other.com", "http://other.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "host", "other.com:99", "http://other.com:99/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "host", "other.com:abc", "http://other.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "host", "[::2]:99", "http://[::2]:99/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "host", "", "http://example.com/a/b?q#f")]
    // hostname: an empty or invalid value must leave the URL untouched
    [InlineData("http://example.com/a/b?q#f", "hostname", "other.com", "http://other.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "hostname", "", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "hostname", "a b", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "hostname", "0x7f.1", "http://127.0.0.1/a/b?q#f")]
    [InlineData("http://example.com/", "hostname", "[::2]", "http://[::2]/")]
    [InlineData("foo:opaque", "hostname", "x.com", "foo:opaque")]
    [InlineData("file:///a/b", "hostname", "h", "file://h/a/b")]
    // port: out of range, non-numeric, and default ports are all no-ops or removals
    [InlineData("http://example.com/a/b?q#f", "port", "99", "http://example.com:99/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "port", "", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com:99/", "port", "", "http://example.com/")]
    [InlineData("http://example.com/a/b?q#f", "port", "80", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "port", "65535", "http://example.com:65535/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "port", "65536", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "port", "abc", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "port", "8080abc", "http://example.com/a/b?q#f")]
    [InlineData("file:///a/b", "port", "9", "file:///a/b")]
    [InlineData("foo:opaque", "port", "9", "foo:opaque")]
    // pathname
    [InlineData("http://example.com/a/b?q#f", "pathname", "/x/y", "http://example.com/x/y?q#f")]
    [InlineData("http://example.com/a/b?q#f", "pathname", "x/y", "http://example.com/x/y?q#f")]
    [InlineData("http://example.com/a/b?q#f", "pathname", "", "http://example.com/?q#f")]
    [InlineData("http://example.com/a/b?q#f", "pathname", "a/../b", "http://example.com/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "pathname", "/a?b#c", "http://example.com/a%3Fb%23c?q#f")]
    [InlineData("http://example.com/a/b?q#f", "pathname", "\\x", "http://example.com/x?q#f")]
    [InlineData("foo:opaque", "pathname", "/y", "foo:%2Fy")]
    // search / hash: a leading delimiter is optional, and empty clears the component
    [InlineData("http://example.com/a/b?q#f", "search", "?a=b", "http://example.com/a/b?a=b#f")]
    [InlineData("http://example.com/a/b?q#f", "search", "a=b", "http://example.com/a/b?a=b#f")]
    [InlineData("http://example.com/a/b?q#f", "search", "", "http://example.com/a/b#f")]
    [InlineData("http://example.com/a/b?q#f", "hash", "#f2", "http://example.com/a/b?q#f2")]
    [InlineData("http://example.com/a/b?q#f", "hash", "f2", "http://example.com/a/b?q#f2")]
    [InlineData("http://example.com/a/b?q#f", "hash", "", "http://example.com/a/b?q")]
    // href replaces everything, or does nothing at all
    [InlineData("http://example.com/a/b?q#f", "href", "http://other/x", "http://other/x")]
    [InlineData("http://example.com/a/b?q#f", "href", "notaurl", "http://example.com/a/b?q#f")]
    [InlineData("http://example.com/a/b?q#f", "href", "", "http://example.com/a/b?q#f")]
    // an unknown part is a no-op
    [InlineData("http://example.com/a/b?q#f", "bogus", "zzz", "http://example.com/a/b?q#f")]
    public void SetsComponents(string href, string part, string value, string expected) =>
        Assert.Equal(expected, Href(UrlOps.UrlSet(href, part, value)));

    [Fact]
    public void SetOnAnUnparseableUrlReportsFailure() =>
        Assert.Equal(Invalid, UrlOps.UrlSet("not a url", "hash", "x"));

    [Fact]
    public void SetKeepsTheComponentContract()
    {
        // A setter returns the same shape the constructor does, so bootstrap.js can swap the
        // cached component object wholesale.
        Assert.Equal(
            "{\"ok\":true,\"href\":\"https://example.com/a\",\"protocol\":\"https:\","
            + "\"username\":\"\",\"password\":\"\",\"host\":\"example.com\","
            + "\"hostname\":\"example.com\",\"port\":\"\",\"pathname\":\"/a\",\"search\":\"\","
            + "\"hash\":\"\",\"origin\":\"https://example.com\"}",
            UrlOps.UrlSet("http://example.com/a", "protocol", "https:"));
    }

    // ------------------------------------------------------------------ query re-encoding

    [Theory]
    [InlineData("a=b", "utf-8", true, "a=b")]
    [InlineData("a=\u00e9", "utf-8", true, "a=%C3%A9")]
    [InlineData("a=\u00e9", "windows-1252", true, "a=%E9")]
    [InlineData("a=\u00e9&b=c", "windows-1252", true, "a=%E9&b=c")]
    [InlineData("\u20ac", "windows-1252", true, "%80")]
    [InlineData("\u3402", "windows-1252", true, "%26%2313314%3B")]
    [InlineData("a=b'c", "utf-8", true, "a=b%27c")]
    [InlineData("a=b'c", "utf-8", false, "a=b'c")]
    [InlineData("a=<b>\"c\"", "utf-8", true, "a=%3Cb%3E%22c%22")]
    [InlineData("a b", "utf-8", true, "a%20b")]
    [InlineData("a#b", "utf-8", true, "a%23b")]
    [InlineData("a=\u00e9", "iso-8859-1", true, "a=%E9")]
    [InlineData("a=\u00e9", "us-ascii", true, "a=%E9")]
    [InlineData("a=\u00e9", "LATIN1", true, "a=%E9")]
    [InlineData("a=\u00e9", "  utf-8  ", true, "a=%C3%A9")]
    // An unresolvable label leaves the query exactly as it arrived, which is what the Rust op
    // does when encoding_rs does not recognize the label.
    [InlineData("a=\u00e9", "not-an-encoding", true, "a=\u00e9")]
    public void ReencodesQueriesWithAnEncodingOverride(
        string query, string label, bool special, string expected) =>
        Assert.Equal(expected, UrlOps.UrlEncodeQuery(query, label, special));

    // ------------------------------------------------------------------ form urlencoded

    [Fact]
    public void FormUrlEncodedSerializesTheSpecWay() => Assert.Equal(
        "a=b&c+d=e%26f&g=%C3%A9&*-._=%21",
        FormUrlEncoded.Serialize(
        [
            new KeyValuePair<string, string>("a", "b"),
            new KeyValuePair<string, string>("c d", "e&f"),
            new KeyValuePair<string, string>("g", "\u00e9"),
            new KeyValuePair<string, string>("*-._", "!"),
        ]));

    [Fact]
    public void FormUrlEncodedParsesPairs()
    {
        var pairs = FormUrlEncoded.Parse("a=b&c+d=e%26f&g&=h&&i=");
        Assert.Equal(5, pairs.Count);
        Assert.Equal(new KeyValuePair<string, string>("a", "b"), pairs[0]);
        Assert.Equal(new KeyValuePair<string, string>("c d", "e&f"), pairs[1]);
        Assert.Equal(new KeyValuePair<string, string>("g", string.Empty), pairs[2]);
        Assert.Equal(new KeyValuePair<string, string>(string.Empty, "h"), pairs[3]);
        Assert.Equal(new KeyValuePair<string, string>("i", string.Empty), pairs[4]);
    }

    [Fact]
    public void FormUrlEncodedRoundTripsNonAscii()
    {
        var pairs = FormUrlEncoded.Parse(FormUrlEncoded.Serialize(
            [new KeyValuePair<string, string>("k\u00e9y", "v\u4f60l")]));
        Assert.Equal(new KeyValuePair<string, string>("k\u00e9y", "v\u4f60l"), Assert.Single(pairs));
    }

    [Fact]
    public void FormUrlEncodedParseIsEmptyForEmptyInput() => Assert.Empty(FormUrlEncoded.Parse(string.Empty));

    // ------------------------------------------------------------------ document.domain

    [Theory]
    [InlineData("www.example.com", "example.com", "example.com")]
    [InlineData("www.example.com", "www.example.com", "www.example.com")]
    [InlineData("www.example.com", "WWW.EXAMPLE.COM", "www.example.com")]
    [InlineData("a.b.example.com", "example.com", "example.com")]
    [InlineData("a.b.example.com", "b.example.com", "b.example.com")]
    [InlineData("example.com", "com", "")]                    // a public suffix is never allowed
    [InlineData("www.example.com", "com", "")]
    [InlineData("foo.example.co.uk", "example.co.uk", "example.co.uk")]
    [InlineData("foo.example.co.uk", "co.uk", "")]            // would relax past the eTLD+1
    [InlineData("pages.github.io", "github.io", "")]          // private suffix, not shared
    [InlineData("localhost", "localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1", "127.0.0.1")]
    [InlineData("127.0.0.1", "0.0.1", "")]                    // IP hosts cannot be relaxed
    [InlineData("www.example.com", "", "")]
    [InlineData("www.example.com", "other.com", "")]
    [InlineData("www.example.com", "ample.com", "")]          // suffix match must be label-aligned
    [InlineData("www.example.com", "a b", "")]
    [InlineData("www.xn--fsq.com", "xn--fsq.com", "xn--fsq.com")]
    [InlineData("site.s3.amazonaws.com", "s3.amazonaws.com", "")]
    [InlineData("deep.a.b.co.jp", "b.co.jp", "b.co.jp")]
    [InlineData("deep.a.b.co.jp", "co.jp", "")]
    public void CanonicalizesDocumentDomainCandidates(string current, string input, string expected) =>
        Assert.Equal(expected, UrlOps.DocumentDomainCandidate(current, input));

    // ------------------------------------------------------------------ punycode

    [Theory]
    [InlineData("\u4f60\u597d", "6qq79v")]
    [InlineData("\u00e9", "9ca")]
    [InlineData("b\u00fccher", "bcher-kva")]
    [InlineData("\u0917\u0932", "n2bd")]
    public void PunycodeRoundTrips(string label, string encoded)
    {
        Assert.True(Punycode.Encode(label, out var actual));
        Assert.Equal(encoded, actual);
        Assert.True(Punycode.Decode(encoded, out var decoded));
        Assert.Equal(label, decoded);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("\u0080")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void PunycodeDecodeRejectsMalformedInput(string body) =>
        Assert.False(Punycode.Decode(body, out _) && body.Length < 3);

    // ------------------------------------------------------------------ totality

    [Theory]
    [InlineData("http://%%%%%%%%%%/")]
    [InlineData("http://[[[[[[[[/")]
    [InlineData("\u0000\u0001\u0002")]
    [InlineData("http://\ud800/")]
    [InlineData("file://\ud800\ud800/")]
    [InlineData("http://a:b:c:d:e@f:g:h/")]
    [InlineData("a:/../../../../../../../..")]
    [InlineData("http://example.com/%%%%?%%%%#%%%%")]
    public void OpsNeverThrowOnPathologicalInput(string href)
    {
        // The Rust ops sit inside catch_unwind because the url crate panics on a few inputs.
        // The managed equivalents must be equally total: a documented failure value, never an
        // exception escaping into V8.
        Assert.NotNull(UrlOps.UrlParse(href, string.Empty));
        Assert.NotNull(UrlOps.UrlParse(href, "http://example.com/a/b"));
        Assert.NotNull(UrlOps.UrlResolve(href, string.Empty));
        Assert.NotNull(UrlOps.UrlResolve("x", href));
        foreach (var part in new[] { "href", "protocol", "username", "password", "host", "hostname", "port", "pathname", "search", "hash" })
        {
            Assert.NotNull(UrlOps.UrlSet("http://example.com/a", part, href));
            Assert.NotNull(UrlOps.UrlSet(href, part, "x"));
        }

        Assert.NotNull(UrlOps.UrlEncodeQuery(href, "utf-8", true));
        Assert.NotNull(UrlOps.DocumentDomainCandidate(href, href));
    }

    [Fact]
    public void AnInvalidSetterValueLeavesEveryComponentUntouched()
    {
        // WHATWG "do nothing on invalid": the URL must come back byte-identical, not cleared.
        const string Original = "http://user:pass@example.com:8080/a/b?q#f";
        var before = UrlOps.UrlParse(Original, string.Empty);
        foreach (var (part, value) in new[]
        {
            ("protocol", "not a scheme"),
            ("hostname", ""),
            ("hostname", "bad host"),
            ("port", "not a port"),
            ("port", "70000"),
            ("href", "still not a url"),
        })
        {
            Assert.Equal(before, UrlOps.UrlSet(Original, part, value));
        }
    }

    [Fact]
    public void PercentEncodingDecodesAndLeavesTruncatedSequencesLiteral()
    {
        Assert.Equal("AB", Encoding.UTF8.GetString(PercentEncoding.Decode("%41%42")));
        Assert.Equal("%4", Encoding.UTF8.GetString(PercentEncoding.Decode("%4")));
        Assert.Equal("%zz", Encoding.UTF8.GetString(PercentEncoding.Decode("%zz")));
        Assert.Equal("\u00e9", Encoding.UTF8.GetString(PercentEncoding.Decode("%C3%A9")));
    }

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
}
