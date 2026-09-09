using Obscura.Parity.Tests.Harness;
using Xunit;

namespace Obscura.Parity.Tests;

/// <summary>
/// Differential coverage for how a page URL is serialized.
/// </summary>
/// <remarks>
/// The reference engine keeps the page URL as the <c>url</c> crate's <c>Url</c> and
/// prints its WHATWG serialization. The port kept it as a <see cref="Uri"/>, whose
/// <see cref="Uri.AbsoluteUri"/> percent-encodes <c>&lt;</c>, <c>&gt;</c> and space
/// in a cannot-be-a-base URL's opaque path, so every <c>data:</c> URL differed:
/// <c>data:text/html,%3Cb%3Ea%20b%3C/b%3E</c> against
/// <c>data:text/html,&lt;b&gt;a b&lt;/b&gt;</c>. It reached the CDP wire through
/// <c>Page.frameNavigated</c>, <c>Page.getFrameTree</c>, DOMSnapshot's
/// <c>documentURL</c>/<c>baseURL</c>, Runtime origins and <c>Target.getTargets</c>,
/// and page JavaScript through <c>location.href</c>, because the JS realm is built
/// with the page's URL string as its base.
///
/// These read the same value back out through <c>--eval</c>, which is the one CLI
/// surface that exposes it, so the check runs without a CDP client.
/// </remarks>
public sealed class UrlSerializationParityTests
{
    public static TheoryData<string, string> Cases() =>
        new()
        {
            // Markup in the opaque path: the case that diverged.
            { "data:text/html,<b>a b</b>", "location.href" },
            { "data:text/html,<b>a b</b>", "document.URL" },
            { "data:text/html,<b>a b</b>", "document.baseURI" },
            { "data:text/html,<b>a b</b>", "document.documentURI" },
            { "data:text/html,<b>a b</b>", "new URL(location.href).protocol" },
            // An opaque origin serializes as the string "null", not as scheme://host.
            { "data:text/html,<b>a b</b>", "location.origin" },
            { "data:text/html;charset=utf-8,<p>x y</p>", "location.href" },
            // Characters Uri.AbsoluteUri also rewrites: the backtick, the caret,
            // the pipe and a literal double quote.
            { "data:text/html,<i>a`b^c|d</i>", "location.href" },
            // A query and a fragment on an opaque path stay where the parser put them.
            { "data:text/html,<b>x</b>?q=a b#frag", "location.href" },
            { "data:text/html,<b>x</b>?q=a b#frag", "location.search" },
            { "data:text/html,<b>x</b>?q=a b#frag", "location.hash" },
            // about:blank has an opaque path too.
            { "about:blank", "location.href" },
            { "about:blank", "location.origin" },
        };

    [ParityTheory]
    [MemberData(nameof(Cases))]
    public void PageUrlSerializesIdentically(string url, string expression)
    {
        string[] args = [url, "--eval", expression, "--quiet"];
        EngineRun rust = ReferenceEngine.Rust(["fetch", .. args]);
        EngineRun port = ReferenceEngine.Port(["fetch", .. args]);

        Assert.Equal(rust.StdOut, port.StdOut);
        Assert.Equal(rust.ExitCode, port.ExitCode);
    }
}
