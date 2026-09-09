using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Api;
using Xunit;
// `Obscura.Browser` is also a namespace, and namespace lookup in an enclosing
// scope beats a using-alias, so inside the `Obscura.*` tree the type has to be
// reached through an alias of another name. Code outside that tree writes plain
// `Browser` after `using Obscura.Api;`.
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Tests for the embeddable library API (<c>crates/obscura</c> -&gt; the
/// <c>Obscura</c> project), including the port of
/// <c>crates/obscura/tests/attribute_injection.rs</c>.
/// </summary>
/// <remarks>
/// These belong in an <c>Obscura.Tests</c> project. They live here because
/// adding a project to the solution is outside this change's scope, and an
/// unreferenced test project would not run at all, which is worse than a
/// slightly wrong home.
/// </remarks>
public sealed class ObscuraApiTests
{
    private const string Fixture =
        "data:text/html,<html><head><title>API</title></head><body>" +
        "<div id=x data-safe=ok>hello</div>" +
        "<a href=\"/next\">Next</a>" +
        "</body></html>";

    private static double? Number(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;

    private static string? Text(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    // SEC-007 / #583: Element.Attribute must escape the attribute name before
    // interpolating it into page JS. A name containing a quote must not be able
    // to break out of the getAttribute('{name}') string literal and run
    // arbitrary JS, the same guarantee QuerySelector already gives for selectors.
    [Fact]
    public async Task Attribute_name_cannot_inject_js()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync("data:text/html,<div id=x data-safe=ok></div>");

        // Canary the injection would flip from 0 to 1.
        page.Evaluate("globalThis.__pwned = 0");

        var element = page.QuerySelector("#x");
        Assert.NotNull(element);

        // The payload breaks out of getAttribute('{name}') while staying a
        // single valid expression, carrying the assignment by concatenation:
        //   el.getAttribute('x' + (globalThis.__pwned = 1) + '')
        _ = element!.Attribute("x' + (globalThis.__pwned = 1) + '");

        var pwned = page.Evaluate("globalThis.__pwned");
        Assert.NotEqual(1.0, Number(pwned));
    }

    [Fact]
    public async Task Attribute_reads_ordinary_names()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync("data:text/html,<div id=x data-safe=ok></div>");

        var element = page.QuerySelector("#x");
        Assert.NotNull(element);
        // Escaping must not break reading a normal attribute name.
        Assert.Equal("ok", element!.Attribute("data-safe"));
    }

    [Fact]
    public async Task Query_selector_returns_null_for_a_missing_element()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(Fixture);
        Assert.Null(page.QuerySelector("#nope"));
    }

    [Fact]
    public async Task Page_exposes_url_title_and_content()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(Fixture);

        Assert.StartsWith("data:text/html,", page.Url, StringComparison.Ordinal);
        Assert.Equal("API", Text(page.Evaluate("document.title")));
        Assert.Contains("data-safe", page.Content(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Element_text_and_click_work_through_the_handle()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(
            "data:text/html,<button id=b onclick=\"globalThis.__hits=(globalThis.__hits||0)+1\">Go</button>");

        var button = page.QuerySelector("#b");
        Assert.NotNull(button);
        Assert.Equal("Go", button!.Text());
        button.Click();
        Assert.Equal(1.0, Number(page.Evaluate("globalThis.__hits")));
    }

    /// <summary>
    /// A handle whose node no longer resolves reports
    /// <c>Error::ElementNotFound("click failed")</c>.
    /// </summary>
    /// <remarks>
    /// Detaching the node is not enough: the arena entry survives a
    /// <c>remove()</c> and <c>_wrap</c> still hands back a working wrapper, in
    /// both engines. Navigating away is what actually invalidates the id.
    /// </remarks>
    [Fact]
    public async Task Click_on_a_handle_from_a_previous_document_reports_element_not_found()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(Fixture);

        var element = page.QuerySelector("#x");
        Assert.NotNull(element);

        await page.GotoAsync("data:text/html,<html><body><p>other</p></body></html>");

        var error = Assert.Throws<ObscuraException>(element!.Click);
        Assert.Equal(ObscuraErrorKind.ElementNotFound, error.Kind);
        Assert.Equal("element not found: click failed", error.Message);
    }

    [Fact]
    public async Task Wait_for_selector_resolves_when_the_node_appears()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(Fixture);

        // Inserted by an already-run script, so it is present on the first poll.
        page.Evaluate(
            "(()=>{const d=document.createElement('div');d.id='late';" +
            "d.textContent='here';document.body.appendChild(d)})()");

        var element = await page.WaitForSelectorAsync("#late", TimeSpan.FromSeconds(5));
        Assert.Equal("here", element.Text());
    }

    /// <summary>
    /// The library's <c>WaitForSelectorAsync</c> polls without driving the event
    /// loop, so a node created by a timer is never seen. This is the reference's
    /// behavior, not a port defect: <c>Page::wait_for_selector</c> in
    /// <c>crates/obscura/src/page.rs</c> loops over <c>evaluate</c> and
    /// <c>tokio::time::sleep</c> and never calls <c>settle</c>. The CLI's own
    /// <c>wait_for_selector</c> in <c>main.rs</c> does pump the loop, which is
    /// why <c>fetch --selector</c> works on a timer-inserted node and this does
    /// not. Callers of the library have to interleave <see cref="Page.SettleAsync"/>
    /// themselves.
    /// </summary>
    [Fact]
    public async Task Wait_for_selector_does_not_pump_the_event_loop()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(
            "data:text/html,<html><body><script>" +
            "setTimeout(()=>{const d=document.createElement('div');d.id='late';" +
            "d.textContent='here';document.body.appendChild(d)},10)" +
            "</script></body></html>");

        await Assert.ThrowsAsync<ObscuraException>(
            () => page.WaitForSelectorAsync("#late", TimeSpan.FromMilliseconds(300)));

        // Pumping the loop explicitly is what makes the node appear.
        await page.SettleAsync(500);
        var element = await page.WaitForSelectorAsync("#late", TimeSpan.FromSeconds(2));
        Assert.Equal("here", element.Text());
    }

    [Fact]
    public async Task Wait_for_selector_times_out_with_the_rust_message()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync(Fixture);

        var error = await Assert.ThrowsAsync<ObscuraException>(
            () => page.WaitForSelectorAsync("#never", TimeSpan.FromMilliseconds(200)));
        Assert.Equal(ObscuraErrorKind.Timeout, error.Kind);
        Assert.Equal("timeout: wait_for_selector(#never) timed out after 200ms", error.Message);
    }

    [Fact]
    public async Task Goto_reports_a_navigation_error_rather_than_a_page_exception()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        var error = await Assert.ThrowsAsync<ObscuraException>(
            () => page.GotoAsync("file:///definitely/not/here.html"));
        Assert.Equal(ObscuraErrorKind.Navigation, error.Kind);
        Assert.StartsWith("navigation error: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settle_lets_scheduled_work_run_between_evaluations()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        await page.GotoAsync("data:text/html,<html><body><p id=p>before</p></body></html>");

        page.Evaluate("setTimeout(()=>{document.getElementById('p').textContent='after'},10)");
        await page.SettleAsync(1000);
        Assert.Equal("after", Text(page.Evaluate("document.getElementById('p').textContent")));
    }

    [Fact]
    public async Task Preload_script_runs_before_the_pages_own_scripts()
    {
        var browser = ApiBrowser.New();
        var page = await browser.NewPageAsync();
        page.AddPreloadScript("globalThis.__preloaded = 'yes'");
        await page.GotoAsync(
            "data:text/html,<html><body><script>globalThis.__saw=globalThis.__preloaded</script></body></html>");
        Assert.Equal("yes", Text(page.Evaluate("globalThis.__saw")));
    }

    [Fact]
    public void Browser_builder_carries_every_configuration_field()
    {
        var config = BrowserConfig.Builder()
            .Proxy("socks5://127.0.0.1:1080")
            .Stealth(true)
            .UserAgent("UA/1.0")
            .StorageDir("/tmp/obscura-config-test")
            .Build();
        Assert.Equal("socks5://127.0.0.1:1080", config.Proxy);
        Assert.True(config.Stealth);
        Assert.Equal("UA/1.0", config.UserAgent);
        Assert.Equal("/tmp/obscura-config-test", config.StorageDir);

        var browser = ApiBrowser.Builder().UserAgent("UA/2.0").Stealth(true).Build();
        Assert.Equal("UA/2.0", browser.Context.UserAgent);
        Assert.True(browser.Context.Stealth);
    }

    [Fact]
    public async Task Pages_from_one_browser_are_independent_realms()
    {
        var browser = ApiBrowser.New();
        var first = await browser.NewPageAsync();
        var second = await browser.NewPageAsync();
        Assert.NotSame(first, second);

        await first.GotoAsync("data:text/html,<html><head><title>one</title></head><body></body></html>");
        await second.GotoAsync("data:text/html,<html><head><title>two</title></head><body></body></html>");

        first.Evaluate("globalThis.__marker = 'first'");
        Assert.Equal("one", Text(first.Evaluate("document.title")));
        Assert.Equal("two", Text(second.Evaluate("document.title")));
        // Each page gets its own isolate, so the marker must not leak across.
        Assert.Null(Text(second.Evaluate("globalThis.__marker")));
    }

    [Fact]
    public void Cookie_store_sets_reads_and_persists()
    {
        var browser = ApiBrowser.New();
        var cookies = browser.Cookies();
        cookies.Set("session=abc123; Domain=example.com; Path=/; HttpOnly", "https://example.com/");

        var all = cookies.GetAll();
        var session = Assert.Single(all, c => c.Name == "session");
        Assert.Equal("abc123", session.Value);
        Assert.True(session.HttpOnly);
        Assert.Equal("/", session.Path);

        var forUrl = cookies.GetForUrl("https://example.com/somewhere");
        Assert.Contains(forUrl, c => c is { Name: "session", Value: "abc123" });

        var path = Path.Combine(Path.GetTempPath(), $"obscura-cookies-{Guid.NewGuid():N}.json");
        try
        {
            cookies.SaveToFile(path);
            var restored = ApiBrowser.New().Cookies();
            Assert.Equal(1, restored.LoadFromFile(path));
            Assert.Contains(restored.GetAll(), c => c.Name == "session");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Cookie_new_uses_the_rust_defaults()
    {
        var cookie = Cookie.New("a", "b", "example.com");
        Assert.Equal("/", cookie.Path);
        Assert.False(cookie.Secure);
        Assert.False(cookie.HttpOnly);
    }

    [Fact]
    public void Cookie_store_rejects_a_relative_url_the_way_rust_does()
    {
        var cookies = ApiBrowser.New().Cookies();
        var error = Assert.Throws<ObscuraException>(() => cookies.Set("a=b", "not-a-url"));
        Assert.Equal(ObscuraErrorKind.Internal, error.Kind);
    }

    [Fact]
    public void Error_messages_match_the_thiserror_renderings()
    {
        Assert.Equal("navigation error: x", ObscuraException.Navigation("x").Message);
        Assert.Equal("JS evaluation error: x", ObscuraException.JsEval("x").Message);
        Assert.Equal("timeout: x", ObscuraException.Timeout("x").Message);
        Assert.Equal("element not found: x", ObscuraException.ElementNotFound("x").Message);
        Assert.Equal("no page session", ObscuraException.NoPage().Message);
        Assert.Equal("boom", ObscuraException.Internal(new InvalidOperationException("boom")).Message);
    }
}
