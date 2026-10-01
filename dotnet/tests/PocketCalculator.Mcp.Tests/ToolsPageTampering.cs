using System.Text.Json.Nodes;
using Xunit;

namespace PocketCalculator.Mcp.Tests;

/// <summary>
/// A page that replaces the built-ins and DOM methods the tools' snippets use does not
/// redirect or break the tools (SECURITY.md L10).
/// </summary>
/// <remarks>
/// DEVIATION from crates/obscura-mcp, whose snippets call <c>document.querySelector</c>,
/// <c>el.click</c>, <c>new Event</c> and <c>Array.prototype.map</c> on whatever the page
/// has left there: a page could send an agent's fill to another field, swallow its click
/// or answer a query with its own list.
/// </remarks>
public sealed class ToolsPageTampering
{
    private const string Page =
        "data:text/html,<form id=f action=/x><input id=q name=q><select id=sel><option value=a>A</option><option value=b>B</option></select></form>"
        + "<button id=go onclick=\"document.getElementById('out').textContent='clicked:'+document.getElementById('q').value\">go</button>"
        + "<a id=l1 href=https://example.com/one>  one   link </a><a href=https://example.com/two>two</a>"
        + "<p id=out></p><h1>Title</h1>"
        + "<script>"
        + "window.__trusted = [];"
        + "document.getElementById('q').addEventListener('input', e => __trusted.push(e.isTrusted));"
        + "document.querySelector = () => document.getElementById('out');"
        + "Document.prototype.querySelector = () => null;"
        + "document.querySelectorAll = () => [];"
        + "Document.prototype.querySelectorAll = () => [];"
        + "Element.prototype.querySelectorAll = () => [];"
        + "HTMLElement.prototype.click = function () {};"
        + "Element.prototype.click = function () {};"
        + "Array.prototype.map = function () { return ['tampered']; };"
        + "Array.prototype.filter = function () { return ['tampered']; };"
        + "Element.prototype.dispatchEvent = function () { return true; };"
        + "window.Event = function () { throw new Error('tampered'); };"
        + "window.KeyboardEvent = function () { throw new Error('tampered'); };"
        + "JSON.stringify = () => '\"tampered\"';"
        + "String.prototype.trim = function () { return 'tampered'; };"
        + "Object.defineProperty(document.body, 'childNodes', { get: () => [] });"
        + "</script>";

    private static async Task<BrowserState> OpenAsync()
    {
        var state = new BrowserState(null, null, false);
        await state.PageMut().NavigateAsync(Page);
        return state;
    }

    [Fact]
    public async Task FillAndClickReachTheNamedElements()
    {
        using var state = await OpenAsync();
        await Tools.FillAsync(JsonNode.Parse("""{ "selector": "#q", "value": "hello" }"""), state);
        await Tools.ClickAsync(JsonNode.Parse("""{ "selector": "#go" }"""), state);

        Assert.Equal("clicked:hello", state.PageMut().Evaluate("document.getElementById('out').textContent")?.GetValue<string>());
        Assert.Equal("true", state.PageMut().Evaluate("String(__trusted[0])")?.GetValue<string>());
    }

    [Fact]
    public async Task SelectAndFillFormReachTheNamedElements()
    {
        using var state = await OpenAsync();
        Assert.Equal("Selected 'b' in '#sel'", Tools.SelectOption(JsonNode.Parse("""{ "selector": "#sel", "value": "b" }"""), state));
        Assert.Equal("b", state.PageMut().Evaluate("document.getElementById('sel').value")?.GetValue<string>());

        Assert.Equal(
            "Filled 1 fields.",
            Tools.FillForm(JsonNode.Parse("""{ "fields": [{ "selector": "#q", "value": "form" }] }"""), state));
        Assert.Equal("form", state.PageMut().Evaluate("document.getElementById('q').value")?.GetValue<string>());
    }

    [Fact]
    public async Task QueriesAnswerFromTheDocument()
    {
        using var state = await OpenAsync();
        Assert.Equal("2", Tools.Count(JsonNode.Parse("""{ "selector": "a" }"""), state));
        Assert.Equal(
            "https://example.com/one",
            Tools.GetAttribute(JsonNode.Parse("""{ "selector": "#l1", "attribute": "href" }"""), state));

        var links = Tools.Links(JsonNode.Parse("{}"), state);
        Assert.Equal(
            """
            {"text":"one link","href":"https://example.com/one"}
            {"text":"two","href":"https://example.com/two"}
            """.ReplaceLineEndings("\n"),
            links);

        var extracted = JsonNode.Parse(Tools.Extract(JsonNode.Parse("""{ "schema": { "heading": "h1", "links[]": "a@href" } }"""), state));
        Assert.Equal("Title", extracted!["heading"]!.GetValue<string>());
        Assert.Equal(2, extracted["links"]!.AsArray().Count);

        var forms = Tools.DetectForms(state);
        Assert.Contains("\"name\": \"q\"", forms, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkdownWalksTheDocument()
    {
        using var state = await OpenAsync();
        var markdown = Tools.Markdown(JsonNode.Parse("{}"), state);
        Assert.Contains("# Title", markdown, StringComparison.Ordinal);
    }
    /// <summary>
    /// L10: the scroll tool reports the host's offset and viewport, not the page's
    /// replaceable <c>window.scrollY</c>, <c>scrollBy</c> and <c>innerHeight</c>.
    /// </summary>
    [Fact]
    public async Task ScrollReportsTheHostsOffset()
    {
        using var state = await OpenAsync();
        state.PageMut().Evaluate(
            "Object.defineProperty(window, 'scrollY', { get: () => 12345, configurable: true });"
            + "Object.defineProperty(window, 'scrollX', { get: () => 12345, configurable: true });"
            + "window.scrollBy = function () {}; window.scrollTo = function () {}; window.innerHeight = 1; 1");

        string result = Tools.Scroll(JsonNode.Parse("""{ "direction": "down" }"""), state);
        Assert.DoesNotContain("12345", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"viewport_h\":1}", result, StringComparison.Ordinal);
        Assert.StartsWith("Scrolled down. {\"x\":0,\"y\":", result, StringComparison.Ordinal);
    }
}
