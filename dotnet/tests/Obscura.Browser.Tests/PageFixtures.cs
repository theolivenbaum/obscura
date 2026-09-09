using System.Text.Json;
using System.Text.Json.Nodes;
using Obscura.Dom;
using Obscura.Js.Runtime;
using Xunit;

namespace Obscura.Browser.Tests;

/// <summary>
/// Shared construction helpers for the page tests, matching the fixture
/// functions declared in <c>page.rs</c>'s test module.
/// </summary>
internal static class PageFixtures
{
    /// <summary>
    /// Every page fixture opts into the private-network gate, exactly as the Rust
    /// tests do: the fixture servers all bind 127.0.0.1, which the SSRF gate blocks
    /// by default.
    /// </summary>
    internal static Page NewPage(string name) =>
        new(name, BrowserContext.WithStorageAndNetwork(name, null, false, null, null, true));

    /// <summary>Rust's `import_map_test_page`.</summary>
    internal static Page ImportMapTestPage(string name, string baseUrl, string html)
    {
        Page page = NewPage(name);
        page.Url = new Uri($"{baseUrl}/app/index.html");
        page.Dom = HtmlParsing.ParseHtml(html);
        page.InitJs();
        return page;
    }

    /// <summary>Rust's `frame_page` / `page_without_chain_limit`.</summary>
    internal static Page FramePage(string name) => NewPage(name);

    /// <summary>Rust's `chain_page`.</summary>
    internal static Page ChainPage(string name, int limit)
    {
        Page page = NewPage(name);
        page.SetNavigationChainLimit(limit);
        return page;
    }

    /// <summary>Builds the page runtime around an already-set DOM and URL.</summary>
    internal static ObscuraJsRuntime RuntimeFor(string url, string html, (float W, float H)? viewport = null)
    {
        var runtime = new ObscuraJsRuntime();
        runtime.SetDom(HtmlParsing.ParseHtml(html));
        runtime.SetUrl(url);
        if (viewport is { } size)
        {
            runtime.SetViewport(size.W, size.H);
        }
        runtime.RunPageInit();
        return runtime;
    }

    internal static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(
            JsonNode.Parse(expected)?.ToJsonString() ?? "null",
            actual?.ToJsonString() ?? "null");

    internal static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

    internal static double? AsDouble(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;

    internal static string? AsString(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
}
