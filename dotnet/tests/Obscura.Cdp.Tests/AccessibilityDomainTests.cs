using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Obscura.Dom;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-cdp/src/domains/accessibility.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class AccessibilityDomainTests
{
    private static JsonObject AxNodeFor(List<JsonObject> nodes, NodeId backendNodeId) =>
        nodes.Find(node => node["backendDOMNodeId"]!.GetValue<uint>() == backendNodeId.Raw)
        ?? throw new InvalidOperationException("element is present in the AX tree");

    private static void AssertAxValue(
        List<JsonObject> nodes,
        DomTree dom,
        string id,
        string? expected)
    {
        JsonObject node = AxNodeFor(nodes, dom.GetElementById(id)!.Value);
        string? actual = node.TryGetPropertyValue("value", out JsonNode? value)
            ? value!["value"]!.GetValue<string>()
            : null;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ContentEditingHostsExposeAxValuesWithoutDuplicatingDescendants()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <div id="true" contenteditable="TrUe">true</div>
                           <div id="empty-keyword" contenteditable>empty keyword</div>
                           <div id="plaintext" contenteditable="PlAiNtExT-OnLy">plaintext</div>
                           <div id="empty-host" contenteditable></div>
                           <div id="host" contenteditable="true">host<span id="inherited"> inherited</span><span id="invalid" contenteditable="bogus"> invalid</span><span id="nested-explicit" contenteditable="plaintext-only"> nested</span></div>
                           <div id="disabled" contenteditable="FaLsE">disabled<span id="invalid-disabled" contenteditable="bogus"> inherited off</span><span id="reenabled" contenteditable="TRUE">reenabled<span id="reenabled-child"> child</span></span></div>
            """);
        List<JsonObject> nodes = Accessibility.BuildAxNodes(dom);

        AssertAxValue(nodes, dom, "true", "true");
        AssertAxValue(nodes, dom, "empty-keyword", "empty keyword");
        AssertAxValue(nodes, dom, "plaintext", "plaintext");
        AssertAxValue(nodes, dom, "empty-host", string.Empty);
        AssertAxValue(nodes, dom, "host", "host inherited invalid nested");
        AssertAxValue(nodes, dom, "inherited", null);
        AssertAxValue(nodes, dom, "invalid", null);
        AssertAxValue(nodes, dom, "nested-explicit", null);
        AssertAxValue(nodes, dom, "disabled", null);
        AssertAxValue(nodes, dom, "invalid-disabled", null);
        AssertAxValue(nodes, dom, "reenabled", "reenabled child");
        AssertAxValue(nodes, dom, "reenabled-child", null);
    }

    /// <summary>
    /// Not in the Rust file: the role mapping, the property list, and the parent/child links a
    /// client walks. These are the fields <c>Accessibility.getFullAXTree</c> exists to deliver.
    /// </summary>
    [Fact]
    public void RolesPropertiesAndTreeLinksMatchTheElementMapping()
    {
        DomTree dom = HtmlParsing.ParseHtml(
            """
            <main id="m"><h3 id="h">Title</h3>
            <a id="link" href="/x">go</a><a id="anchor">no href</a>
            <button id="b" disabled>press</button>
            <input id="text" required>
            <input id="check" type="checkbox" checked>
            <textarea id="area"></textarea>
            <select id="one"></select><select id="many" multiple></select>
            <div id="role" role="banner"></div><div id="odd" role="nonsense"></div>
            <img id="pic" alt="a cat">
            <!-- comment --></main>
            """);
        List<JsonObject> nodes = Accessibility.BuildAxNodes(dom);

        string RoleOf(string id) =>
            AxNodeFor(nodes, dom.GetElementById(id)!.Value)["role"]!["value"]!.GetValue<string>();

        Assert.Equal("main", RoleOf("m"));
        Assert.Equal("heading", RoleOf("h"));
        Assert.Equal("link", RoleOf("link"));
        Assert.Equal("generic", RoleOf("anchor"));
        Assert.Equal("button", RoleOf("b"));
        Assert.Equal("textbox", RoleOf("text"));
        Assert.Equal("checkbox", RoleOf("check"));
        Assert.Equal("textbox", RoleOf("area"));
        Assert.Equal("combobox", RoleOf("one"));
        Assert.Equal("listbox", RoleOf("many"));
        Assert.Equal("banner", RoleOf("role"));
        Assert.Equal("generic", RoleOf("odd"));
        Assert.Equal("image", RoleOf("pic"));

        List<string> PropertiesOf(string id)
        {
            JsonObject node = AxNodeFor(nodes, dom.GetElementById(id)!.Value);
            return node.TryGetPropertyValue("properties", out JsonNode? props)
                ? [.. props!.AsArray().Select(p => p!["name"]!.GetValue<string>())]
                : [];
        }

        Assert.Equal(["level"], PropertiesOf("h"));
        Assert.Equal(["focusable", "disabled"], PropertiesOf("b"));
        Assert.Equal(["focusable", "editable", "required"], PropertiesOf("text"));
        Assert.Equal(["focusable", "editable", "checked"], PropertiesOf("check"));
        Assert.Equal(["focusable", "editable", "multiline"], PropertiesOf("area"));
        Assert.Equal(
            3u,
            AxNodeFor(nodes, dom.GetElementById("h")!.Value)["properties"]!
                .AsArray()[0]!["value"]!["value"]!.GetValue<uint>());

        // The image's accessible name comes from alt=, and a comment contributes no AX node.
        Assert.Equal(
            "a cat",
            AxNodeFor(nodes, dom.GetElementById("pic")!.Value)["name"]!["value"]!.GetValue<string>());

        // Child and parent links are AX ids, and skip over the nodes with no role.
        JsonObject main = AxNodeFor(nodes, dom.GetElementById("m")!.Value);
        JsonObject heading = AxNodeFor(nodes, dom.GetElementById("h")!.Value);
        Assert.Contains(
            heading["nodeId"]!.GetValue<string>(),
            main["childIds"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.Equal(main["nodeId"]!.GetValue<string>(), heading["parentId"]!.GetValue<string>());

        // The document itself is the RootWebArea and has no parent.
        JsonObject root = nodes[0];
        Assert.Equal("RootWebArea", root["role"]!["value"]!.GetValue<string>());
        Assert.False(root.ContainsKey("parentId"));
        Assert.False(root["ignored"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetFullAxTreeAnswersForASessionPageAndOtherMethodsAreNoOps()
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession();
        await ctx.GetSessionPageMut(session)!.NavigateAsync(
            "data:text/html,<button id=go>Go</button>");

        JsonNode tree = CdpDomainFixtures.Unwrap(
            await Accessibility.HandleAsync("getFullAXTree", null, ctx, session));
        JsonArray nodes = tree["nodes"]!.AsArray();
        Assert.NotEmpty(nodes);
        Assert.Contains(
            nodes,
            node => node!["role"]!["value"]!.GetValue<string>() == "button");

        Assert.True((await Accessibility.HandleAsync("enable", null, ctx, session)).IsOk);
        Assert.True((await Accessibility.HandleAsync("queryAXTree", null, ctx, session)).IsOk);
        Assert.Contains(
            "No page",
            CdpDomainFixtures.ErrorOf(
                await Accessibility.HandleAsync("getFullAXTree", null, ctx, null)),
            StringComparison.Ordinal);
    }
}
