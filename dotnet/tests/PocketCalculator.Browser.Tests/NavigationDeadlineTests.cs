using System.Diagnostics;
using System.Text;
using Xunit;

namespace PocketCalculator.Browser.Tests;

/// <summary>
/// The navigation deadline holds whatever the navigation awaits, and a page that loses
/// work to a watchdog still finishes loading. Found on reddit.com, where a CDP
/// <c>Page.navigate</c> went unanswered and the CLI failed to load the page.
/// </summary>
public sealed class NavigationDeadlineTests
{
    [Fact]
    public async Task ANavigationThatNeverCompletesStillReturnsAfterItsDeadline()
    {
        using Page page = PageFixtures.NewPage("deadline-backstop");
        page.SetNavigationTimeout(TimeSpan.FromMilliseconds(300));
        page.NavigationBackstopGrace = TimeSpan.FromMilliseconds(200);
        var never = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();

        // An await that ignores the token: before the backstop the caller waited forever.
        await page.RunWithNavigationDeadlineAsync(_ => never.Task, CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"returned after {clock.Elapsed}");
        // A fresh page counts as committed, so it is kept as it stood.
        Assert.True(page.LoadAbandoned);
    }

    [Fact]
    public async Task ACancellationFromInsideThePageEndsTheNavigationInsteadOfEscaping()
    {
        // ClearScript's ScriptInterruptedException is an OperationCanceledException. One that
        // escaped the navigation reached the CDP connection processor's shutdown handler,
        // and the connection was never served again.
        using Page page = PageFixtures.NewPage("stray-cancellation");

        await page.RunWithNavigationDeadlineAsync(
            async _ =>
            {
                await Task.Yield();
                throw new OperationCanceledException("interrupted by a script watchdog");
            },
            CancellationToken.None);

        Assert.True(page.LoadAbandoned);
    }

    [Fact]
    public async Task ACallersOwnCancellationStillPropagates()
    {
        using Page page = PageFixtures.NewPage("caller-cancellation");
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            page.RunWithNavigationDeadlineAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                caller.Token));
    }

    [Fact]
    public async Task ADynamicScriptBehindAnInterruptedTimerStillRunsAndTheDocumentLoads()
    {
        // reddit.com's shape: an animation-frame timer forces a layout that cannot finish
        // before the event loop's watchdog, and the execution of a load-delaying dynamic
        // script is a zero-delay timer taken in the same batch. The interrupt dropped it,
        // so the script never ran, never fired load, and the document's load event waited
        // out the whole script deadline.
        using TestHttpServer server = TestHttpServer.Start(request => request.Path switch
        {
            "/" => TestResponse.Html(InterruptedBatchPage()),
            "/dyn.js" => TestResponse.JavaScript("globalThis.__dynamicRan = true;"),
            _ => new TestResponse("text/plain", [], "404 Not Found"),
        });

        using Page page = PageFixtures.NewPage("interrupted-batch");
        page.SetNavigationTimeout(TimeSpan.FromSeconds(60));
        var clock = Stopwatch.StartNew();
        await page.NavigateAsync($"{server.Origin}/");

        Assert.False(page.LoadAbandoned);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(25),
            $"the document took {clock.Elapsed} to load (the script deadline is 30s)");
        PageFixtures.AssertJson(
            "[true, \"complete\"]",
            page.Js!.Evaluate("[globalThis.__dynamicRan === true, document.readyState]"));
    }

    /// <summary>
    /// A timer that forces layout after every mutation for longer than the event loop's
    /// watchdog allows a task, and a dynamic script whose fetch finishes while the parser
    /// script still holds the isolate, so its execution timer joins the layout timer's batch.
    /// </summary>
    private static string InterruptedBatchPage()
    {
        var html = new StringBuilder(
            """
            <!doctype html><html><head><script>
            const s = document.createElement('script');
            s.src = '/dyn.js';
            document.head.appendChild(s);
            setTimeout(() => {
              const box = document.getElementById('box');
              const stop = Date.now() + 60000;
              for (let i = 0; Date.now() < stop; i++) {
                box.style.width = (i % 200) + 'px';
                box.getBoundingClientRect();
              }
            }, 0);
            const until = Date.now() + 500;
            while (Date.now() < until) {}
            </script></head><body><div id=box>
            """);
        for (int i = 0; i < 400; i++)
        {
            html.Append("<p style=\"float:left;padding:1px\">paragraph ").Append(i).Append(" with some words in it</p>");
        }

        return html.Append("</div></body></html>").ToString();
    }
}
