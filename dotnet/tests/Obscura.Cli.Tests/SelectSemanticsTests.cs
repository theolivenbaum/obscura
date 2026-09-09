using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/select_semantics.rs</c>.
/// </summary>
/// <remarks>
/// Select-element parity needed by jQuery and WooCommerce variation forms:
/// a single select with no explicit <c>selected</c> attribute implicitly selects
/// its first option (so <c>selectedIndex</c> is 0, not -1); <c>select.type</c> is
/// the fixed IDL string <c>select-one</c>/<c>select-multiple</c>; and
/// programmatic value assignment must not fire <c>change</c>.
/// </remarks>
public sealed class SelectSemanticsTests
{
    private const string Body =
        "<!doctype html><html><head><title>fixture</title></head><body>\n" +
        "<select id=\"single\"><option value=\"a\">A</option><option value=\"b\">B</option></select>\n" +
        "<select id=\"multi\" multiple><option value=\"x\">X</option></select>\n" +
        "<select id=\"empty\"></select>\n" +
        "</body></html>";

    [Fact]
    public async Task Select_defaults_match_browser_semantics()
    {
        using var server = LocalHttpServer.Html(Body);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);

        var probes = page.Evaluate(
            """
            (function () {
                var single = document.getElementById('single');
                var multi = document.getElementById('multi');
                var empty = document.getElementById('empty');
                var changes = 0;
                single.addEventListener('change', function () { changes++; });
                single.value = 'b';
                return {
                    single_index: single.selectedIndex,
                    single_value_after_set: single.value,
                    multi_index: multi.selectedIndex,
                    empty_index: empty.selectedIndex,
                    single_type: single.type,
                    multi_type: multi.type,
                    changes_after_assignment: changes,
                };
            })()
            """);

        Assert.NotNull(probes);
        Assert.Equal("b", PageProbe.Text(probes!["single_value_after_set"]));
        Assert.Equal(1.0, PageProbe.Number(probes["single_index"]));
        Assert.Equal(-1.0, PageProbe.Number(probes["multi_index"]));
        Assert.Equal(-1.0, PageProbe.Number(probes["empty_index"]));
        Assert.Equal("select-one", PageProbe.Text(probes["single_type"]));
        Assert.Equal("select-multiple", PageProbe.Text(probes["multi_type"]));
        Assert.Equal(0.0, PageProbe.Number(probes["changes_after_assignment"]));
    }

    [Fact]
    public async Task Select_without_explicit_selection_defaults_to_first_option()
    {
        using var server = LocalHttpServer.Html(Body);
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync(server.Base);

        var probes = page.Evaluate(
            """
            (function () {
                var single = document.getElementById('single');
                return { index: single.selectedIndex, value: single.value };
            })()
            """);

        Assert.NotNull(probes);
        Assert.Equal(0.0, PageProbe.Number(probes!["index"]));
        Assert.Equal("a", PageProbe.Text(probes["value"]));
    }
}
