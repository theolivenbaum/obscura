using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/concurrent_isolate_teardown.rs</c>.
/// </summary>
/// <remarks>
/// #756: dropping a page while another one is alive aborted the process. Each
/// page owns its own JS runtime, so N pages means N isolates on the thread, and
/// rusty_v8 enters an isolate on construction and exits it on drop, which makes
/// V8's entered-isolate stack the construction order. The Rust cases drop pages
/// out of that order; the managed equivalent is disposing them out of order,
/// which is what releases the isolate here.
/// </remarks>
public sealed class ConcurrentIsolateTeardownTests
{
    private const string Body =
        "<!doctype html><html><head><title>fixture</title></head><body>hi</body></html>";

    private static string? Title(Obscura.Api.Page page) =>
        PageProbe.Text(page.Evaluate("document.title"));

    [Fact]
    public async Task A_second_browser_can_be_disposed_without_aborting()
    {
        using var server = LocalHttpServer.Html(Body);

        var first = ApiBrowser.New();
        var pageOne = await first.NewPageAsync();
        await pageOne.GotoAsync(server.Base);

        var second = ApiBrowser.New();
        using var pageTwo = await second.NewPageAsync();
        await pageTwo.GotoAsync(server.Base);

        // Both isolates are live, and the first was entered first.
        pageOne.Dispose();

        Assert.Equal("fixture", Title(pageTwo));
    }

    /// <summary>
    /// The single-page case, which always worked: its isolate is the only one on
    /// the stack, so it is necessarily released last.
    /// </summary>
    [Fact]
    public async Task One_browser_one_page_disposes_cleanly()
    {
        using var server = LocalHttpServer.Html(Body);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);
        Assert.Equal("fixture", Title(page));
    }

    /// <summary>Two pages on ONE browser: one isolate each, same context.</summary>
    [Fact]
    public async Task Two_pages_on_one_browser_dispose_cleanly()
    {
        using var server = LocalHttpServer.Html(Body);
        var browser = ApiBrowser.New();
        var one = await browser.NewPageAsync();
        await one.GotoAsync(server.Base);
        using var two = await browser.NewPageAsync();
        await two.GotoAsync(server.Base);
        one.Dispose();
        Assert.Equal("fixture", Title(two));
    }

    /// <summary>
    /// Releasing from the <em>middle</em> of the stack, which needs the isolates
    /// entered after it to be unwound and then put back, not just its own exited.
    /// </summary>
    [Fact]
    public async Task A_page_in_the_middle_can_be_disposed_and_the_rest_keep_working()
    {
        using var server = LocalHttpServer.Html(Body);
        var browser = ApiBrowser.New();

        var first = await browser.NewPageAsync();
        await first.GotoAsync(server.Base);
        var middle = await browser.NewPageAsync();
        await middle.GotoAsync(server.Base);
        using var last = await browser.NewPageAsync();
        await last.GotoAsync(server.Base);

        middle.Dispose();

        // Both neighbours must still be able to run script: the one below the
        // hole on the entry stack and the one above it, which was exited and
        // re-entered to reach it.
        Assert.Equal("fixture", Title(first));
        Assert.Equal("fixture", Title(last));

        // And a page created after the hole works, so the stack was left in a
        // state a new isolate can be pushed onto.
        using var fresh = await browser.NewPageAsync();
        await fresh.GotoAsync(server.Base);
        Assert.Equal("fixture", Title(fresh));

        // Finally release out of order again, oldest first, and keep using the rest.
        first.Dispose();
        Assert.Equal("fixture", Title(last));
        Assert.Equal("fixture", Title(fresh));
    }

    /// <summary>
    /// The half of this that has nothing to do with teardown: a second page put
    /// the first one's isolate below the top of V8's entry stack, and running any
    /// script on a non-current isolate aborts. A second page therefore disabled
    /// the first one outright, with no teardown involved at all.
    /// </summary>
    [Fact]
    public async Task An_older_page_still_runs_script_while_a_newer_one_is_alive()
    {
        using var server = LocalHttpServer.Html(Body);
        var browser = ApiBrowser.New();

        using var first = await browser.NewPageAsync();
        await first.GotoAsync(server.Base);
        using var second = await browser.NewPageAsync();
        await second.GotoAsync(server.Base);

        // The newest page always worked; the older one is what aborted.
        Assert.Equal("fixture", Title(second));
        Assert.Equal("fixture", Title(first));

        // Interleaving the two must keep working in both directions.
        Assert.Equal(2.0, PageProbe.Number(first.Evaluate("1 + 1")));
        Assert.Equal(4.0, PageProbe.Number(second.Evaluate("2 + 2")));
        Assert.Equal(6.0, PageProbe.Number(first.Evaluate("3 + 3")));
    }
}
