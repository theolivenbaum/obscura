using System.Text.Json.Nodes;

using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// <c>Page.navigate</c> honours <c>referrer</c> and <c>referrerPolicy</c> as Chromium 141 does:
/// the referrer goes through the policy (default strict-origin-when-cross-origin), loses
/// userinfo and fragment, sets the Referer and <c>document.referrer</c>, and leaves the
/// navigation browser-initiated. Upstream ignores both parameters.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class CdpNavigateReferrer
{
    [Fact]
    public async Task PageNavigateAppliesReferrerAndPolicy()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Html("<html><body>ok</body></html>");
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;
        string origin = server.Url.TrimEnd('/');

        (string Referrer, string? Policy, string Path, string? Expected)[] cases =
        [
            ("http://example.com/some/path?q=1", null, "nav1", "http://example.com/"),
            ("http://example.com/some/path?q=1", "unsafeUrl", "nav2", "http://example.com/some/path?q=1"),
            ("http://example.com/some/path?q=1", "noReferrer", "nav3", null),
            (origin + "/same/path?q=1", null, "nav4", origin + "/same/path?q=1"),
            ("https://example.com/some/path?q=1", null, "nav5", null),
            ("http://user:pw@example.com/some/path#frag", "unsafeUrl", "nav6", "http://example.com/some/path"),
            ("not a url", null, "nav7", null),
            ("http://example.com/p", "origin", "nav8", "http://example.com/"),
        ];
        ulong id = 1;
        foreach ((string referrer, string? policy, string path, string? expected) in cases)
        {
            var parameters = new JsonObject { ["url"] = $"{origin}/{path}", ["referrer"] = referrer };
            if (policy is not null)
            {
                parameters["referrerPolicy"] = policy;
            }
            await CoreCdp.CdpAsync(ctx, id++, "Page.navigate", parameters, sessionId);
            string head = server.Heads.Last(h => h.StartsWith($"GET /{path} ", StringComparison.Ordinal));
            string? sent = head.Split("\r\n")
                .FirstOrDefault(line => line.StartsWith("Referer:", StringComparison.OrdinalIgnoreCase))?[8..].Trim();
            Assert.True(expected == sent, $"{referrer} {policy}: Referer {sent ?? "(none)"}, expected {expected ?? "(none)"}");
            // Still browser-initiated.
            Assert.Contains("Sec-Fetch-Site: none", head, StringComparison.OrdinalIgnoreCase);
            JsonNode documentReferrer = await CoreCdp.EvalAsync(ctx, id++, "document.referrer", sessionId);
            Assert.Equal(expected ?? string.Empty, documentReferrer.Get("result").Get("value").AsString());
        }

        CdpResponse invalid = await CoreCdp.DispatchAsync(
            ctx,
            id,
            "Page.navigate",
            new JsonObject { ["url"] = $"{origin}/nav9", ["referrer"] = "http://example.com/", ["referrerPolicy"] = "bogus" },
            sessionId);
        Assert.Equal("Invalid referrerPolicy", invalid.Error?.Message);
        Assert.DoesNotContain(server.Requests, line => line.StartsWith("GET /nav9 ", StringComparison.Ordinal));
    }
}
