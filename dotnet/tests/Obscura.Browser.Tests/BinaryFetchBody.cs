using System.Text;
using Xunit;

namespace Obscura.Browser.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-browser/tests/binary_fetch_body.rs</c>.
/// </summary>
public sealed class BinaryFetchBodyTests
{
    private static readonly byte[] BinaryBody = [0, 128, 255, 16];

    private static (TestHttpServer Server, Func<byte[]?> Echoed) SpawnEchoServer()
    {
        byte[]? echoed = null;
        TestHttpServer server = TestHttpServer.Start(request =>
        {
            if (string.Equals(request.Path, "/binary", StringComparison.Ordinal))
            {
                Volatile.Write(ref echoed, request.Body);
                return TestResponse.Text("ok");
            }
            return TestResponse.Html(
                "<!doctype html><html><body>binary fetch fixture</body></html>");
        });
        return (server, () => Volatile.Read(ref echoed));
    }

    private static async Task AssertBinaryFetchBodyAsync(bool stealth)
    {
        Environment.SetEnvironmentVariable("OBSCURA_ALLOW_PRIVATE_NETWORK", "1");
        (TestHttpServer server, Func<byte[]?> echoed) = SpawnEchoServer();
        using (server)
        {
            BrowserContext context = BrowserContext.WithStorageAndNetwork(
                "binary-fetch", null, stealth, null, null, true);
            using var page = new Page("binary-fetch-page", context);
            await page.NavigateAsync(server.Origin);

            page.Evaluate(
                """
                (function() {
                    document.body.setAttribute('data-binary-fetch', 'pending');
                    fetch('/binary', {
                        method: 'POST',
                        headers: { 'content-type': 'application/octet-stream' },
                        body: new Uint8Array([0, 128, 255, 16]),
                    })
                        .then(function(response) {
                            if (!response.ok) throw new Error('HTTP ' + response.status);
                            document.body.setAttribute('data-binary-fetch', 'done');
                        })
                        .catch(function(error) {
                            document.body.setAttribute('data-binary-fetch', 'error: ' + String(error));
                        });
                })()
                """);

            for (int attempt = 0; attempt < 20; attempt++)
            {
                await page.SettleAsync(100);
                if (!string.Equals(
                        PageFixtures.AsString(page.Evaluate("document.body.getAttribute('data-binary-fetch')")),
                        "pending",
                        StringComparison.Ordinal))
                {
                    break;
                }
            }

            PageFixtures.AssertJson(
                "\"done\"",
                page.Evaluate("document.body.getAttribute('data-binary-fetch')"));
            Assert.Equal<byte>(BinaryBody, Assert.IsType<byte[]>(echoed()));
        }
    }

    [Fact]
    public Task FetchPreservesBinaryRequestBody() => AssertBinaryFetchBodyAsync(false);

    /// <summary>
    /// PORT DEVIATION: Rust gates this on the <c>stealth</c> feature and routes the
    /// request through <c>StealthHttpClient</c>. The stealth transport is a tracked
    /// gap in this port (see todo.md), so a stealth context runs through the ordinary
    /// client with tracker blocking on. The test still pins what it can here: a
    /// stealth context must not corrupt a binary request body.
    /// </summary>
    [Fact]
    public Task StealthFetchPreservesBinaryRequestBody() => AssertBinaryFetchBodyAsync(true);
}
