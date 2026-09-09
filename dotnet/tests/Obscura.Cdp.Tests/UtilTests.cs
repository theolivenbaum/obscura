using System.Text;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>tests</c> module in
/// <c>crates/obscura-cdp/src/util.rs</c>.
/// </summary>
public sealed class UtilTests
{
    [Fact]
    public void MatchesPlainFileUrl() =>
        Assert.True(CdpUtil.UrlIsFileScheme("file:///etc/passwd"));

    [Fact]
    public void MatchesCaseInsensitively()
    {
        Assert.True(CdpUtil.UrlIsFileScheme("FILE:///etc/passwd"));
        Assert.True(CdpUtil.UrlIsFileScheme("File:///etc/passwd"));
        Assert.True(CdpUtil.UrlIsFileScheme("fIlE:///etc/passwd"));
    }

    /// <summary>
    /// The URL parser strips or rejects leading whitespace depending on the
    /// implementation; either way the syntactic fallback still catches
    /// <c>   file:...</c> so callers cannot be tricked into letting it through.
    /// </summary>
    [Fact]
    public void MatchesWithLeadingWhitespaceFallback() =>
        Assert.True(CdpUtil.UrlIsFileScheme("   file:///etc/passwd"));

    [Fact]
    public void RejectsHttpHttpsAboutData()
    {
        Assert.False(CdpUtil.UrlIsFileScheme("http://example.com"));
        Assert.False(CdpUtil.UrlIsFileScheme("https://example.com"));
        Assert.False(CdpUtil.UrlIsFileScheme("about:blank"));
        Assert.False(CdpUtil.UrlIsFileScheme("data:text/plain,hi"));
        Assert.False(CdpUtil.UrlIsFileScheme(string.Empty));
    }

    /// <summary>
    /// <c>file</c> appearing anywhere except as the leading scheme must not match.
    /// </summary>
    [Fact]
    public void RejectsLookalikesThatAreNotFileScheme()
    {
        Assert.False(CdpUtil.UrlIsFileScheme("notfile:///x"));
        Assert.False(CdpUtil.UrlIsFileScheme("http://file/"));
    }

    /// <summary>
    /// The pair this replaced was
    /// <c>oid.replace('\\', "\\\\").replace('\'', "\\'")</c> feeding a
    /// single-quoted literal. A double-quoted JSON literal needs no escape for
    /// <c>'</c> and adds one for <c>"</c>, so both quote characters are covered
    /// rather than one.
    /// </summary>
    [Fact]
    public void ObjectIdLiteralQuotesAndEscapesWhatTheHandRolledPairDid()
    {
        Assert.Equal("\"plain\"", CdpUtil.ObjectIdLiteral("plain"));
        Assert.Equal("\"x\\\\\"", CdpUtil.ObjectIdLiteral("x\\"));
        Assert.Equal("\"a'b\"", CdpUtil.ObjectIdLiteral("a'b"));
        Assert.Equal("\"a\\\"b\"", CdpUtil.ObjectIdLiteral("a\"b"));

        // Carried over from the DOM helper this replaces: the backslash has to be
        // doubled before the quote is looked at, or `a\'b` comes out with the
        // escape attached to the wrong character.
        Assert.Equal("\"a\\\\'b\"", CdpUtil.ObjectIdLiteral("a\\'b"));
    }

    /// <summary>
    /// A raw LF or CR terminates a JS string literal, so these produced a syntax
    /// error and a silent resolution failure rather than a lookup. NUL parses but
    /// truncates the id against anything C-string shaped.
    /// </summary>
    [Fact]
    public void ObjectIdLiteralEscapesTheControlsTheHandRolledPairMissed()
    {
        Assert.Equal("\"a\\nb\"", CdpUtil.ObjectIdLiteral("a\nb"));
        Assert.Equal("\"a\\rb\"", CdpUtil.ObjectIdLiteral("a\rb"));
        Assert.Equal("\"a\\tb\"", CdpUtil.ObjectIdLiteral("a\tb"));
        Assert.Equal("\"a\\u0000b\"", CdpUtil.ObjectIdLiteral("a\u0000b"));
    }

    /// <summary>
    /// Ids are JSON documents themselves, so the literal has to carry a second
    /// round of quoting without losing the id.
    /// </summary>
    [Fact]
    public void ObjectIdLiteralSurvivesTheShapeTheRuntimeActuallyMints()
    {
        const string Oid = """{"injectedScriptId":1,"id":7}""";
        Assert.Equal(
            "\"{\\\"injectedScriptId\\\":1,\\\"id\\\":7}\"",
            CdpUtil.ObjectIdLiteral(Oid));

        // And a getProperties child id is that, plus `::`, plus a page-chosen
        // property name: the input this helper exists for.
        Assert.Equal(
            "\"{\\\"injectedScriptId\\\":1,\\\"id\\\":7}::lf\\nx\"",
            CdpUtil.ObjectIdLiteral(Oid + "::lf\nx"));
    }

    /// <summary>
    /// 199 ASCII bytes + U+20AC (3 bytes, occupying indices 199..201): byte 200
    /// falls inside the euro sign. This is exactly the shape of a malformed CDP
    /// frame that would reach the invalid-frame log preview.
    /// </summary>
    [Fact]
    public void TruncateNeverSplitsAMultibyteChar()
    {
        var s = new string('a', 199) + "\u20actail";
        Assert.Equal(199 + 3 + 4, Encoding.UTF8.GetByteCount(s));

        var safe = CdpUtil.TruncateOnCharBoundary(s, 200);
        Assert.StartsWith(safe, s, StringComparison.Ordinal);
        Assert.True(CdpUtil.Utf8Length(safe) <= 200);
        Assert.Equal(199, CdpUtil.Utf8Length(safe));
    }

    [Fact]
    public void TruncateReturnsWholeStringWhenShort()
    {
        Assert.Equal("hi", CdpUtil.TruncateOnCharBoundary("hi", 200));
        Assert.Equal(string.Empty, CdpUtil.TruncateOnCharBoundary(string.Empty, 10));

        // Exact-length and exact-boundary cases are returned unchanged.
        Assert.Equal("abc", CdpUtil.TruncateOnCharBoundary("abc", 3));
    }

    /// <summary>
    /// The budget is UTF-8 bytes, not UTF-16 units, so a surrogate pair is kept
    /// whole even though it is two chars on this side.
    /// </summary>
    [Fact]
    public void TruncateNeverSplitsASurrogatePair()
    {
        var s = new string('a', 2) + "\U0001F600";
        Assert.Equal("aa", CdpUtil.TruncateOnCharBoundary(s, 5));
        Assert.Equal(s, CdpUtil.TruncateOnCharBoundary(s, 6));
    }
}
