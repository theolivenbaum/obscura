using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/textarea_semantics.rs</c>.
/// </summary>
/// <remarks>
/// Textarea parity from #685: the element kept its parser handling and value
/// semantics but had no interface of its own and no control geometry, so it laid
/// out as a plain block (full containing-block width, zero height when empty).
/// Playwright resolves that zero-height box to <c>hidden</c>, and
/// wait_for_selector/click/fill then burn their full timeout. Both halves are
/// covered here: the DOM interface, and the intrinsic control box.
/// </remarks>
public sealed class TextareaSemanticsTests
{
    private const string Body =
        "<!doctype html><html><head><title>fixture</title></head><body>\n" +
        "<textarea id=\"t\"></textarea>\n" +
        "<textarea id=\"r\" rows=\"8\"></textarea>\n" +
        "<textarea id=\"c\" style=\"height:36px\"></textarea>\n" +
        "</body></html>";

    [Fact]
    public async Task Textarea_idl_matches_browser_semantics()
    {
        using var server = LocalHttpServer.Html(Body);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);

        var probes = page.Evaluate(
            """
            (function () {
                var t = document.getElementById('t');
                return {
                    rows_default: t.rows,
                    cols_default: t.cols,
                    type: t.type,
                    ctor: t.constructor.name,
                    is_textarea: t instanceof HTMLTextAreaElement,
                    is_element: t instanceof Element,
                    rows_reflect: (t.rows = 5, t.getAttribute('rows')),
                    rows_after_set: t.rows,
                    rows_invalid: (t.setAttribute('rows', '0'), t.rows),
                };
            })()
            """);

        Assert.NotNull(probes);
        Assert.Equal(2.0, PageProbe.Number(probes!["rows_default"]));
        Assert.Equal(20.0, PageProbe.Number(probes["cols_default"]));
        Assert.Equal("textarea", PageProbe.Text(probes["type"]));
        Assert.Equal("HTMLTextAreaElement", PageProbe.Text(probes["ctor"]));
        Assert.True(probes["is_textarea"]!.GetValue<bool>());
        Assert.True(probes["is_element"]!.GetValue<bool>());
        Assert.Equal("5", PageProbe.Text(probes["rows_reflect"]));
        Assert.Equal(5.0, PageProbe.Number(probes["rows_after_set"]));
        // rows/cols are limited to positive numbers; anything else reads back
        // as the default.
        Assert.Equal(2.0, PageProbe.Number(probes["rows_invalid"]));
    }

    [Fact]
    public async Task Textarea_gets_intrinsic_control_box()
    {
        using var server = LocalHttpServer.Html(Body);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);

        var probes = page.Evaluate(
            """
            (function () {
                var box = function (id) {
                    var r = document.getElementById(id).getBoundingClientRect();
                    return r.width.toFixed(0) + 'x' + r.height.toFixed(0);
                };
                var t = document.getElementById('t');
                return {
                    empty: box('t'),
                    rows8: box('r'),
                    css_height: box('c'),
                    display: getComputedStyle(t).display,
                    value_roundtrip: (t.value = 'hi', t.value),
                };
            })()
            """);

        Assert.NotNull(probes);
        // Chromium reference: an empty cols=20 rows=2 control is 168x36; each
        // extra row adds one 15px control line; authored height wins.
        Assert.Equal("168x36", PageProbe.Text(probes!["empty"]));
        Assert.Equal("168x126", PageProbe.Text(probes["rows8"]));
        Assert.Equal("168x36", PageProbe.Text(probes["css_height"]));
        Assert.Equal("inline-block", PageProbe.Text(probes["display"]));
        Assert.Equal("hi", PageProbe.Text(probes["value_roundtrip"]));
    }
}
