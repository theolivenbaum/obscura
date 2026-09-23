using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// M4: a page's scripted fetches are capped at a fixed number in flight; the rest queue and
/// still complete.
/// </summary>
public sealed class FetchConcurrencyTests
{
    [Fact]
    public async Task ScriptedFetchesAreCappedInFlightPerPage()
    {
        Environment.SetEnvironmentVariable("POCKETCALCULATOR_ALLOW_PRIVATE_NETWORK", "1");
        int inFlight = 0;
        int peak = 0;
        using TestHttpServer server = TestHttpServer.Start(request =>
        {
            if (request.Path.StartsWith("/slow", StringComparison.Ordinal))
            {
                int now = Interlocked.Increment(ref inFlight);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now
                    && Interlocked.CompareExchange(ref peak, now, seen) != seen)
                {
                }

                Thread.Sleep(150);
                Interlocked.Decrement(ref inFlight);
                return TestResponse.Text("ok");
            }

            return TestResponse.Html("<!doctype html><html><body>fetch fan-out</body></html>");
        });

        BrowserContext context = BrowserContext.WithStorageAndNetwork(
            "fetch-concurrency", null, false, null, null, true);
        using var page = new Page("fetch-concurrency-page", context);
        await page.NavigateAsync(server.Origin);

        page.Evaluate(
            """
            (function() {
                window.__done = 0;
                for (let i = 0; i < 20; i++) {
                    fetch('/slow?' + i).then(r => r.text()).then(() => { window.__done++; },
                        () => { window.__done++; });
                }
            })()
            """);

        for (int attempt = 0; attempt < 100; attempt++)
        {
            await page.SettleAsync(100);
            if (PageFixtures.AsString(page.Evaluate("String(window.__done)")) == "20")
            {
                break;
            }
        }

        PageFixtures.AssertJson("\"20\"", page.Evaluate("String(window.__done)"));
        Assert.InRange(Volatile.Read(ref peak), 1, 6);
    }
}
