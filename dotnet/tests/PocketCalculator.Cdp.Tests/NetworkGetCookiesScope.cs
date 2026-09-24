using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// <c>Network.getCookies</c> answers the cookies of the URLs it is asked about, as Chromium
/// 141 does: the given <c>urls</c>, read in the page's partition, or without them the
/// page's URL and its frames' URLs. <c>Network.getAllCookies</c> still answers every
/// cookie. The Rust engine answered every cookie for both.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class NetworkGetCookiesScope
{
    private static async Task SetAsync(CdpContext ctx, string session, JsonObject cookie) =>
        await CoreCdp.CdpAsync(ctx, 50, "Network.setCookie", cookie, session);

    private static async Task<List<string>> NamesAsync(CdpContext ctx, string session, string method, JsonObject parameters)
    {
        JsonNode result = await CoreCdp.CdpAsync(ctx, 60, method, parameters, session);
        HashSet<string> names = [];
        foreach (JsonNode? cookie in result["cookies"]!.AsArray())
        {
            Assert.True(names.Add(cookie!["name"]!.GetValue<string>()), "a cookie is listed once");
        }

        List<string> sorted = [.. names];
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    [Fact]
    public async Task GetCookiesAnswersThePageAndItsFramesOrTheGivenUrls()
    {
        // The fixture's Url ends in a slash; the frame is cross-site (localhost).
        CoreCdpServer server = null!;
        server = CoreCdpServer.Routed(path => path switch
        {
            "/frame" => ("<p>frame</p>", "text/html", 200),
            _ => ($"<p>outer</p><iframe src=\"{server.Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal)}frame\"></iframe>",
                "text/html", 200),
        });
        using (server)
        {
            (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url + "sub/page");
            using IDisposable owned = CoreCdp.Owned(ctx);
            for (int attempt = 0; attempt < 40 && ctx.GetSessionPage(session)!.Frames.Count == 0; attempt++)
            {
                await CoreCdp.EvalAsync(ctx, 70, "1", session);
                await Task.Delay(50);
            }

            Assert.NotEmpty(ctx.GetSessionPage(session)!.Frames);

            await SetAsync(ctx, session, new JsonObject { ["name"] = "a", ["value"] = "1", ["url"] = server.Url });
            await SetAsync(ctx, session, new JsonObject { ["name"] = "b", ["value"] = "2", ["url"] = server.Url + "sub/", ["path"] = "/sub" });
            await SetAsync(ctx, session, new JsonObject { ["name"] = "wrongPath", ["value"] = "1", ["url"] = server.Url, ["path"] = "/elsewhere" });
            await SetAsync(ctx, session, new JsonObject { ["name"] = "c", ["value"] = "3", ["url"] = "http://other.test/" });
            await SetAsync(ctx, session, new JsonObject
            {
                ["name"] = "l", ["value"] = "4", ["url"] = server.Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal),
            });
            await SetAsync(ctx, session, new JsonObject { ["name"] = "d", ["value"] = "5", ["url"] = "https://secure.test/" });
            await SetAsync(ctx, session, new JsonObject
            {
                ["name"] = "pagePartition", ["value"] = "6", ["url"] = "https://secure.test/",
                ["partitionKey"] = new JsonObject { ["topLevelSite"] = "http://127.0.0.1", ["hasCrossSiteAncestor"] = false },
            });
            await SetAsync(ctx, session, new JsonObject
            {
                ["name"] = "otherPartition", ["value"] = "7", ["url"] = "https://secure.test/",
                ["partitionKey"] = new JsonObject { ["topLevelSite"] = "https://secure.test", ["hasCrossSiteAncestor"] = false },
            });

            Assert.Equal(["a", "b", "l"], await NamesAsync(ctx, session, "Network.getCookies", []));
            Assert.Equal(["c"], await NamesAsync(ctx, session, "Network.getCookies", new JsonObject { ["urls"] = new JsonArray("http://other.test/x") }));
            Assert.Equal(["a", "c"], await NamesAsync(ctx, session, "Network.getCookies", new JsonObject
            {
                ["urls"] = new JsonArray("http://other.test/", server.Url, server.Url),
            }));
            Assert.Empty(await NamesAsync(ctx, session, "Network.getCookies", new JsonObject { ["urls"] = new JsonArray() }));
            Assert.Equal(["d", "pagePartition"], await NamesAsync(ctx, session, "Network.getCookies", new JsonObject
            {
                ["urls"] = new JsonArray("https://secure.test/x"),
            }));

            Assert.Equal(8, (await NamesAsync(ctx, session, "Network.getAllCookies", [])).Count);
            Assert.Equal(8, (await NamesAsync(ctx, session, "Storage.getCookies", [])).Count);
        }
    }
}
