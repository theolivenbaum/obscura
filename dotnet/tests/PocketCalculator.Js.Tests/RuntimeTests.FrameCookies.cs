using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Cookies, origin and base URL of about:srcdoc and about:blank frames, measured on Chromium
/// 141: they read and write cookies for their creator's URL (a cookie they set takes the
/// creator URL's default path), in the creator's partition and site for cookies; they have
/// the creator's origin and resolve against its base; and one with an opaque origin throws
/// SecurityError from <c>document.cookie</c>. The port used the about: URL, which saw no
/// cookie and resolved nothing.
/// </summary>
public sealed partial class RuntimeTests
{
    [Fact]
    public void SrcdocAndBlankFramesUseTheirCreatorsCookieUrl()
    {
        var jar = new CookieJar();
        jar.SetCookie("pc=1; Path=/", new Uri("https://top-a.com/"));
        using var page = CookiePage("https://top-a.com/dir/page?q=1", jar);
        using var srcdoc = FrameRealm.Create(page.Runtime, 1, 0, "about:srcdoc", "<html><body></body></html>");
        using var blank = FrameRealm.Create(page.Runtime, 2, 0, "about:blank", "<html><body></body></html>");
        using var nested = FrameRealm.Create(page.Runtime, 3, 1, "about:srcdoc", "<html><body></body></html>");
        Assert.NotNull(srcdoc);
        Assert.NotNull(blank);
        Assert.NotNull(nested);

        Assert.Equal("pc=1", DocumentCookie(srcdoc));
        Assert.Equal("pc=1", DocumentCookie(blank));
        Assert.Equal("pc=1", DocumentCookie(nested));
        srcdoc.Evaluate("document.cookie = 'fromsrcdoc=1'; 0");
        blank.Evaluate("document.cookie = 'fromblank=1'; 0");
        Assert.Equal("pc=1; fromsrcdoc=1; fromblank=1", DocumentCookie(page.Runtime));
        Assert.Equal("/dir", Assert.Single(jar.GetAllCookies(), c => c.Name == "fromsrcdoc").Path);

        // The parent's stand-in for its initial about:blank frame reads the same cookies.
        Assert.Equal(
            "pc=1; fromsrcdoc=1; fromblank=1",
            page.Runtime.Evaluate(
                "(() => { const f = document.createElement('iframe'); document.body.appendChild(f);"
                + " return f.contentDocument.cookie; })()")!.GetValue<string>());

        Assert.Equal("https://top-a.com", srcdoc.Origin);
        Assert.Equal("https://top-a.com", StateHelpers.DocumentOrigin(nested.State));
        Assert.Equal(
            "https://top-a.com/dir/rel.png",
            srcdoc.Evaluate("(() => { const a = document.createElement('a'); a.href = 'rel.png'; return a.href; })()")!
                .GetValue<string>());
    }

    [Fact]
    public void SrcdocInACrossSiteFrameUsesThatFramesCookieScope()
    {
        var jar = new CookieJar();
        jar.SetCookie("lax=1; Secure; SameSite=Lax; Path=/", new Uri("https://third-b.com/"));
        jar.SetCookie("none=1; Secure; SameSite=None; Path=/", new Uri("https://third-b.com/"));
        using var page = CookiePage("https://top-a.com/page", jar);
        using var frame = FrameRealm.Create(page.Runtime, 1, 0, "https://third-b.com/frame", "<html><body></body></html>");
        using var srcdoc = FrameRealm.Create(page.Runtime, 2, 1, "about:srcdoc", "<html><body></body></html>");
        Assert.NotNull(frame);
        Assert.NotNull(srcdoc);

        Assert.Equal("none=1", DocumentCookie(srcdoc));
        srcdoc.Evaluate("document.cookie = 'part=1; Secure; SameSite=None; Partitioned; Path=/'; 0");
        Assert.Equal("none=1; part=1", DocumentCookie(srcdoc));
        var partitioned = Assert.Single(jar.GetAllCookies(), cookie => cookie.Name == "part");
        Assert.Equal("third-b.com", partitioned.Domain.TrimStart('.'));
        Assert.Equal(new CookiePartitionKey("https://top-a.com", true), partitioned.PartitionKey);
        Assert.Equal("https://third-b.com", srcdoc.Origin);
    }

    [Fact]
    public void OpaqueOriginFramesThrowFromDocumentCookie()
    {
        var jar = new CookieJar();
        jar.SetCookie("pc=1; Path=/", new Uri("https://top-a.com/"));
        using var page = CookiePage("https://top-a.com/page", jar);
        using var sandboxed = FrameRealm.Create(page.Runtime, 1, 0, "about:srcdoc", "<html><body></body></html>", opaqueOrigin: true);
        using var child = FrameRealm.Create(page.Runtime, 2, 1, "about:srcdoc", "<html><body></body></html>");
        using var https = FrameRealm.Create(page.Runtime, 3, 0, "https://top-a.com/frame", "<html><body></body></html>", opaqueOrigin: true);
        Assert.NotNull(sandboxed);
        Assert.NotNull(child);
        Assert.NotNull(https);

        Assert.Equal("null", sandboxed.Origin);
        // A srcdoc frame of an opaque-origin document is opaque too.
        Assert.Equal("null", child.Origin);
        foreach (var frame in new[] { sandboxed, child, https })
        {
            Assert.Equal(
                "SecurityError: Failed to read the 'cookie' property from 'Document': The document is sandboxed and lacks the 'allow-same-origin' flag.",
                frame.Evaluate("(() => { try { return document.cookie; } catch (e) { return e.name + ': ' + e.message; } })()")!
                    .GetValue<string>());
            Assert.Equal(
                "SecurityError: Failed to set the 'cookie' property on 'Document': The document is sandboxed and lacks the 'allow-same-origin' flag.",
                frame.Evaluate("(() => { try { document.cookie = 'x=1'; return 'set'; } catch (e) { return e.name + ': ' + e.message; } })()")!
                    .GetValue<string>());
        }

        Assert.Equal("pc=1", DocumentCookie(page.Runtime));
    }
}
