using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/cdp_click_submit_parity.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class CdpClickSubmitParity
{
    private const string FormDocument = """
        <html><body>
        <form id="f" action="/submitted">
          <input type="hidden" name="vacancy_id" value="123">
          <textarea name="message">hello</textarea>
          <input type="checkbox" name="agree" value="yes" checked>
          <button id="submit" type="submit">Go</button>
        </form>
        <script>
        function submitCompat() {
          const form = document.getElementById('f');
          const params = new URLSearchParams();
          form.querySelectorAll('input, textarea').forEach(function(field) {
            if (field.type === 'checkbox' && !field.checked) return;
            params.append(field.name, field.value);
          });
          location.href = form.action + '?' + params.toString();
        }
        document.querySelector('button').addEventListener('click', function(e) {
          e.preventDefault();
          submitCompat();
        });
        </script>
        </body></html>
        """;

    [Fact]
    public async Task RuntimeClickSubmitPreventDefaultNavigationUpdatesPage()
    {
        CoreCdp.AllowLoopback();
        using CoreCdpServer server = CoreCdpServer.Routed(path =>
            path.StartsWith("/submitted", StringComparison.Ordinal)
                ? ("<html><body>submitted</body></html>", "text/html", 200)
                : (FormDocument, "text/html", 200));
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;

        // Explicit `waitUntil: 'load'` so the inline <script> that defines submitCompat
        // runs. Page.navigate defaults to DomContentLoaded (matching real Chrome's
        // lifecycle streaming so Puppeteer/Playwright clients don't time out on JS-heavy
        // pages), which by design returns before parser-discovered scripts execute.
        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject { ["url"] = server.Url, ["waitUntil"] = "load" },
            sessionId);

        JsonNode submitCompatType = await CoreCdp.EvalAsync(
            ctx, 2, "typeof submitCompat", sessionId);
        Assert.Equal("function", submitCompatType.Get("result").Get("value").AsString());

        JsonNode button = await CoreCdp.CdpAsync(
            ctx,
            3,
            "Runtime.evaluate",
            new JsonObject { ["expression"] = "document.getElementById('submit')" },
            sessionId);
        string objectId = button.Get("result").Get("objectId").AsString()!;

        await CoreCdp.CdpAsync(
            ctx,
            4,
            "Runtime.callFunctionOn",
            new JsonObject
            {
                ["objectId"] = objectId,
                ["functionDeclaration"] = "function() { this.click(); }",
            },
            sessionId);

        Obscura.Browser.Page page = ctx.GetPageMut(pageId)!;
        Assert.Equal("/submitted", page.Url!.Path);
        Assert.Equal("vacancy_id=123&message=hello&agree=yes", page.Url!.Query);
        Assert.Contains(
            "submitted",
            page.Evaluate("document.body.textContent").AsStringOr(string.Empty),
            StringComparison.Ordinal);
    }
}
