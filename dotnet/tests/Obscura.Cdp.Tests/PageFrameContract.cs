using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/page_frame_contract.rs</c>.
/// </summary>
/// <remarks>
/// Every path that returns or emits a <c>Page.Frame</c> must produce the same
/// protocol-complete object, because generated CDP clients deserialize it before their
/// frame managers ever see it.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class PageFrameContract
{
    private const string ChildBody = "<html><body>child</body></html>";

    private const string RootBody = """
        <!doctype html><html><body>
            <a id="route" href="#next">next</a>
            <iframe src="/child.html"></iframe>
            <script>
                route.addEventListener('click', event => {
                    event.preventDefault();
                    history.pushState({}, '', '/next');
                });
            </script>
        </body></html>
        """;

    private static void AssertFrameContract(JsonNode? frame)
    {
        foreach (string field in new[]
        {
            "id", "loaderId", "url", "domainAndRegistry", "securityOrigin", "mimeType",
            "secureContextType", "crossOriginIsolatedContextType",
        })
        {
            Assert.True(
                frame.Get(field).AsString() is not null,
                $"{field} is missing or not a string: {CdpJson.Serialize(frame)}");
        }

        Assert.Contains(
            frame.Get("secureContextType").AsString(),
            new[] { "Secure", "SecureLocalhost", "InsecureScheme", "InsecureAncestor" });
        Assert.Contains(
            frame.Get("crossOriginIsolatedContextType").AsString(),
            new[] { "Isolated", "NotIsolated", "NotIsolatedFeatureDisabled" });
        Assert.True(
            JsonExt.AsJsonArray(frame.Get("gatedAPIFeatures")) is not null,
            $"gatedAPIFeatures is missing: {CdpJson.Serialize(frame)}");
    }

    [Fact]
    public async Task EveryPageFramePathUsesTheCurrentCdpContract()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Routed(path =>
            path.StartsWith("/child.html", StringComparison.Ordinal)
                ? (ChildBody, "text/html", 200)
                : (RootBody, "text/html", 200));
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);

        JsonNode created = await CoreCdp.CdpAsync(
            ctx, 1, "Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null);
        string pageId = created["targetId"].AsString()!;
        JsonNode attached = await CoreCdp.CdpAsync(
            ctx,
            2,
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = pageId, ["flatten"] = true },
            null);
        string sessionId = attached["sessionId"].AsString()!;

        JsonNode initialTree = await CoreCdp.CdpAsync(
            ctx, 3, "Page.getFrameTree", new JsonObject(), sessionId);
        JsonNode? initialFrame = initialTree["frameTree"]!["frame"];
        AssertFrameContract(initialFrame);
        Assert.Equal($"loader-blank-{pageId}", initialFrame.Get("loaderId").AsString());
        Assert.Equal("Secure", initialFrame.Get("secureContextType").AsString());

        int navigationEventStart = ctx.PendingEvents.Count;
        JsonNode navigated = await CoreCdp.CdpAsync(
            ctx,
            4,
            "Page.navigate",
            new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" },
            sessionId);
        string loaderId = navigated["loaderId"].AsString()!;
        JsonNode? navigationFrame = ctx.PendingEvents
            .Skip(navigationEventStart)
            .First(e => e.Method == "Page.frameNavigated"
                && e.Params.Get("frame").Get("id").AsString() == pageId)
            .Params.Get("frame");
        AssertFrameContract(navigationFrame);
        Assert.Equal(loaderId, navigationFrame.Get("loaderId").AsString());
        Assert.Equal("SecureLocalhost", navigationFrame.Get("secureContextType").AsString());

        await CoreCdp.EvalAsync(ctx, 5, "document.title", sessionId);
        JsonNode loadedTree = await CoreCdp.CdpAsync(
            ctx, 6, "Page.getFrameTree", new JsonObject(), sessionId);
        JsonNode? root = loadedTree["frameTree"];
        AssertFrameContract(root.Get("frame"));
        Assert.Equal(loaderId, root.Get("frame").Get("loaderId").AsString());
        JsonNode? child = root.Get("childFrames").Get(0).Get("frame");
        AssertFrameContract(child);
        Assert.Equal(root.Get("frame").Get("id").AsString(), child.Get("parentId").AsString());
        JsonNode? childEventFrame = ctx.PendingEvents
            .First(e => e.Method == "Page.frameNavigated"
                && e.Params.Get("frame").Get("id").AsString() == child.Get("id").AsString())
            .Params.Get("frame");
        AssertFrameContract(childEventFrame);

        await CoreCdp.CdpAsync(
            ctx,
            7,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] =
                    "document.elementFromPoint = () => document.getElementById('route')",
            },
            sessionId);
        await CoreCdp.CdpAsync(
            ctx,
            8,
            "Input.dispatchMouseEvent",
            new JsonObject
            {
                ["type"] = "mousePressed",
                ["x"] = 0,
                ["y"] = 0,
                ["button"] = "left",
            },
            sessionId);
        int routeEventStart = ctx.PendingEvents.Count;
        await CoreCdp.CdpAsync(
            ctx,
            9,
            "Input.dispatchMouseEvent",
            new JsonObject
            {
                ["type"] = "mouseReleased",
                ["x"] = 0,
                ["y"] = 0,
                ["button"] = "left",
            },
            sessionId);
        JsonNode? routeFrame = ctx.PendingEvents
            .Skip(routeEventStart)
            .First(e => e.Method == "Page.frameNavigated"
                && e.Params.Get("frame").Get("id").AsString() == pageId)
            .Params.Get("frame");
        AssertFrameContract(routeFrame);
        Assert.Equal(loaderId, routeFrame.Get("loaderId").AsString());
        Assert.EndsWith("/next", routeFrame.Get("url").AsStringOr(string.Empty), StringComparison.Ordinal);
    }
}
