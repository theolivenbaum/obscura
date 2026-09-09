using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Obscura.Js.Runtime;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// Coverage for <c>crates/obscura-cdp/src/domains/browser.rs</c> and
/// <c>lp.rs</c>, neither of which carries a <c>#[cfg(test)] mod tests</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class BrowserDomainTests
{
    [Fact]
    public async Task GetVersionReportsTheHeadlessChromeIdentity()
    {
        JsonNode version = CdpDomainFixtures.Unwrap(await Domains.Browser.HandleAsync("getVersion", null));
        Assert.Equal("1.3", version["protocolVersion"]!.GetValue<string>());
        Assert.Equal("Chrome/145.0.0.0", version["product"]!.GetValue<string>());
        Assert.Equal(
            "@0000000000000000000000000000000000000000",
            version["revision"]!.GetValue<string>());
        Assert.Equal(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + "Chrome/145.0.0.0 Safari/537.36",
            version["userAgent"]!.GetValue<string>());
        Assert.Equal("14.5.0.0", version["jsVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task WindowMethodsAnswerTheFixedBoundsAndPermissionsAreAcked()
    {
        JsonNode forTarget = CdpDomainFixtures.Unwrap(
            await Domains.Browser.HandleAsync("getWindowForTarget", null));
        Assert.Equal(1, forTarget["windowId"]!.GetValue<int>());
        CdpDomainFixtures.AssertJson(
            """{"left": 0, "top": 0, "width": 1280, "height": 720, "windowState": "normal"}""",
            forTarget["bounds"]);

        JsonNode bounds = CdpDomainFixtures.Unwrap(await Domains.Browser.HandleAsync("getWindowBounds", null));
        CdpDomainFixtures.AssertJson(
            """{"left": 0, "top": 0, "width": 1280, "height": 720, "windowState": "normal"}""",
            bounds["bounds"]);

        string[] acks =
        [
            "close", "setDownloadBehavior", "setWindowBounds", "grantPermissions", "resetPermissions",
        ];
        foreach (string method in acks)
        {
            CdpDomainFixtures.AssertJson("{}", CdpDomainFixtures.Unwrap(
                await Domains.Browser.HandleAsync(method, null)));
        }

        Assert.Contains(
            "Unknown Browser method: nope",
            CdpDomainFixtures.ErrorOf(await Domains.Browser.HandleAsync("nope", null)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LpGetMarkdownWalksTheLiveDom()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        await ctx.GetSessionPageMut(session)!.NavigateAsync(
            "data:text/html,<html><body><h1>Title</h1><p>Body <b>text</b>.</p></body></html>");

        JsonNode result = CdpDomainFixtures.Unwrap(
            await Lp.HandleAsync("getMarkdown", null, ctx, session));
        string markdown = result["markdown"]!.GetValue<string>();
        Assert.Contains("# Title", markdown, StringComparison.Ordinal);
        Assert.Contains("**text**", markdown, StringComparison.Ordinal);

        // The domain is the CDP face of the shared extraction script, not a second copy of it.
        Assert.Equal(
            ctx.GetSessionPageMut(session)!.Evaluate(MarkdownScript.HtmlToMarkdown)!.GetValue<string>(),
            markdown);

        Assert.Contains(
            "Unknown LP method: nope",
            CdpDomainFixtures.ErrorOf(await Lp.HandleAsync("nope", null, ctx, session)),
            StringComparison.Ordinal);
        Assert.Contains(
            "No page",
            CdpDomainFixtures.ErrorOf(await Lp.HandleAsync("getMarkdown", null, ctx, null)),
            StringComparison.Ordinal);
    }
}
