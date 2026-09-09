using System.Text;
using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/sequential_runtime_fetch.rs</c>: eight
/// browsers in a row must each drive 8 chained fetches plus 4 XHRs to
/// completion, so a runtime created after an earlier one was torn down still
/// pumps its own event loop.
/// </summary>
public sealed class SequentialRuntimeFetchTests
{
    [Fact]
    public async Task Sequential_browsers_complete_concurrent_fetch_and_xhr()
    {
        using var server = new LocalHttpServer(
            target => target.StartsWith("/api", StringComparison.Ordinal)
                ? ("application/json", Encoding.UTF8.GetBytes("{\"ok\":true}"))
                : ("text/html", Encoding.UTF8.GetBytes(
                    "<!doctype html><html><head><title>fixture</title></head><body></body></html>")),
            cors: true);

        for (var run = 0; run < 8; run++)
        {
            await RunBrowserAsync(server.Base);
        }
    }

    private static async Task RunBrowserAsync(string @base)
    {
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(@base);

        page.Evaluate($$"""
            (function() {
                var done = 0;
                function mark() {
                    done += 1;
                    document.body.setAttribute('data-done', String(done));
                }
                for (var i = 0; i < 8; i++) {
                    fetch('{{@base}}/api?fetch=' + i)
                        .then(function(r) { return r.json(); })
                        .then(function() { return fetch('{{@base}}/api?nested=' + i); })
                        .then(mark)
                        .catch(function() {});
                }
                for (var j = 0; j < 4; j++) {
                    var xhr = new XMLHttpRequest();
                    xhr.open('GET', '{{@base}}/api?xhr=' + j);
                    xhr.addEventListener('load', mark);
                    xhr.send();
                }
            })()
            """);

        await PageProbe.SettleUntilAsync(page, () => PageProbe.DoneMarker(page) == "12", 20, 250);
        Assert.Equal("12", PageProbe.DoneMarker(page));
    }
}
