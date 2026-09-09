using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Api;
using Xunit;
using ApiBrowser = Obscura.Api.Browser;

namespace Obscura.Cli.Tests;

/// <summary>
/// Port of <c>crates/obscura/tests/attribute_injection.rs</c>.
/// </summary>
/// <remarks>
/// SEC-007 / #583: <c>Element.Attribute</c> must escape the attribute name
/// before interpolating it into page JS. A name containing a quote must not be
/// able to break out of the <c>getAttribute('{name}')</c> string literal and run
/// arbitrary JS in the page, the same guarantee <c>QuerySelector</c> already
/// gives for selectors.
/// </remarks>
public sealed class AttributeInjectionTests
{
    private static double? Number(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;

    [Fact]
    public async Task Attribute_name_cannot_inject_js()
    {
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync("data:text/html,<div id=x data-safe=ok></div>");

        // Canary the injection would flip from 0 to 1.
        page.Evaluate("globalThis.__pwned = 0");

        var element = page.QuerySelector("#x");
        Assert.NotNull(element);

        // The payload breaks out of getAttribute('{name}') while staying a
        // single valid expression (the wrapper places it inside
        // `el ? ... : null`, which forbids a top-level comma), carrying the
        // assignment by concatenation:
        //   el.getAttribute('x' + (globalThis.__pwned = 1) + '')
        _ = element!.Attribute("x' + (globalThis.__pwned = 1) + '");

        var pwned = page.Evaluate("globalThis.__pwned");
        Assert.NotEqual(1.0, Number(pwned));
    }

    [Fact]
    public async Task Attribute_reads_ordinary_names()
    {
        var browser = ApiBrowser.New();
        using var page = await browser.NewPageAsync();
        await page.GotoAsync("data:text/html,<div id=x data-safe=ok></div>");

        var element = page.QuerySelector("#x");
        Assert.NotNull(element);
        // Escaping must not break reading a normal attribute name.
        Assert.Equal("ok", element!.Attribute("data-safe"));
    }
}
