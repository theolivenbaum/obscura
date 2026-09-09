using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/dynamic_stylesheet_onload_fires.rs</c>.
/// </summary>
/// <remarks>
/// Regression for issue #409: a <c>&lt;link rel="stylesheet" href&gt;</c> inserted via JS
/// (createElement + appendChild) must fetch and fire <c>load</c>, so frameworks that await
/// the link's onload (Promise.all of lazy CSS + JS, antd/bootstrap loaders) resolve instead
/// of hanging forever. Before the fix the link fired neither load nor error, so the page
/// stayed on stage1.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class DynamicStylesheetOnloadFires
{
    private const string Document = """
        <html><head></head><body>
        <div id="r">stage1</div>
        <script>
        window.__loaded = new Promise(function (resolve, reject) {
          var l = document.createElement("link");
          l.rel = "stylesheet";
          l.href = "/style.css";
          l.onload = function () { document.getElementById("r").textContent = "stage2"; resolve("ok"); };
          l.onerror = function () { reject("err"); };
          document.head.appendChild(l);
        });
        </script>
        </body></html>
        """;

    [Fact]
    public async Task DynamicStylesheetFiresLoad()
    {
        using CoreCdpServer server = CoreCdpServer.Routed(path =>
            path.StartsWith("/style.css", StringComparison.Ordinal)
                ? ("body { color: red; }", "text/css", 200)
                : (Document, "text/html", 200));
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);

        // Race the link's onload promise against a 5s timeout. Before the fix the link
        // never settled, so this resolved with "timeout" and the assertion failed.
        JsonNode evaluated = await CoreCdp.CdpAsync(
            ctx,
            2,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] =
                    "(async () => { try { return await Promise.race([window.__loaded, "
                    + "new Promise((_, r) => setTimeout(() => r('timeout'), 5000))]); } "
                    + "catch (e) { return 'rejected:' + e; } })()",
                ["awaitPromise"] = true,
                ["returnByValue"] = true,
            },
            session);
        Assert.Equal("ok", evaluated["result"]!["value"].AsStringOr(string.Empty));

        JsonNode text = await CoreCdp.EvalAsync(
            ctx, 3, "document.getElementById('r').textContent", session);
        Assert.Equal("stage2", text["result"]!["value"].AsString());
    }
}
