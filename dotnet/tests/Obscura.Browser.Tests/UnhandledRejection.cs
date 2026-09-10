using Xunit;

namespace Obscura.Browser.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-browser/tests/unhandled_rejection.rs</c>.
/// </summary>
public sealed class UnhandledRejectionTests
{
    private static TestHttpServer SpawnPage() => TestHttpServer.Start(_ => TestResponse.Html(
        """
        <!doctype html><html><body data-ready="false">
            <script>
                globalThis.__rejectionObserved = false;
                addEventListener('unhandledrejection', function (event) {
                    globalThis.__rejectionObserved = event.reason.message === 'background failure';
                });
                Promise.reject(new Error('background failure'));
                setTimeout(function () {
                    document.body.setAttribute('data-ready', 'true');
                }, 20);
            </script>
        </body></html>
        """));

    // BLOCKED on a gap in Obscura.Js, not on this port: `DenoCoreShim` stores the
    // callback `Deno.core.setUnhandledPromiseRejectionHandler` registers but nothing
    // ever invokes it, so bootstrap.js never dispatches `unhandledrejection`.
    // Verified locally that the second half of this test - the timer still runs, so
    // a rejected background promise does not stop the page event loop - already
    // holds; only the event dispatch is missing.
    [Fact]
    public async Task RejectedBackgroundPromiseDoesNotStopThePageEventLoop()
    {
        Environment.SetEnvironmentVariable("OBSCURA_ALLOW_PRIVATE_NETWORK", "1");
        using TestHttpServer server = SpawnPage();
        BrowserContext context = BrowserContext.WithStorageAndNetwork(
            "unhandled-rejection", null, false, null, null, true);
        using var page = new Page("unhandled-rejection-page", context);
        await page.NavigateAsync(server.Origin);
        await page.SettleAsync(200);

        PageFixtures.AssertJson("true", page.Evaluate("globalThis.__rejectionObserved"));
        PageFixtures.AssertJson("\"true\"", page.Evaluate("document.body.getAttribute('data-ready')"));
    }
}
