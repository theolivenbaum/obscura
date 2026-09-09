using System.Text.Json.Nodes;

using Obscura.Dom;

using Xunit;

using DomDomain = Obscura.Cdp.Domains.Dom;
using PageDomain = Obscura.Cdp.Domains.Page;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>tests</c> module in
/// <c>crates/obscura-cdp/src/domains/dom.rs</c>.
/// </summary>
/// <remarks>
/// The objectId escaping this file used to own now lives in <c>CdpUtil.ObjectIdLiteral</c>,
/// which is where its tests went with it: the three lookup sites embed the id as a JSON
/// literal rather than splicing it into a single-quoted string, so there is no per-domain
/// escaping left to assert on.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class DomDomainTests
{
    /// <summary>
    /// CDP clients (browser-use) focus an input via DOM.focus before typing;
    /// dispatchKeyEvent then targets document.activeElement. DOM.focus must actually move
    /// focus or keystrokes land on nothing.
    /// </summary>
    [Fact]
    public async Task DomFocusSetsActiveElement()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        string session = $"{pageId}-session";
        ctx.Sessions[session] = pageId;

        CdpDomainFixtures.Unwrap(await PageDomain.HandleAsync(
            "navigate",
            CdpDomainFixtures.Json("""{"url":"data:text/html,<input id=q>","waitUntil":"load"}"""),
            ctx,
            session));

        JsonNode qs = CdpDomainFixtures.Unwrap(await DomDomain.HandleAsync(
            "querySelector",
            new JsonObject { ["selector"] = "input" },
            ctx,
            session));
        ulong nid = qs["nodeId"].AsU64()!.Value;
        Assert.True(nid > 0, "the input element should be found");

        CdpDomainFixtures.Unwrap(await DomDomain.HandleAsync(
            "focus", new JsonObject { ["nodeId"] = nid }, ctx, session));

        JsonNode? active = ctx.GetSessionPageMut(session)!.Evaluate(
            "(function(){return document.activeElement?document.activeElement.tagName:'NONE';})()");
        Assert.Equal(
            "INPUT",
            active.AsString());
    }

    [Fact]
    public async Task ScrollIntoViewIfNeededResolvesAllNodeIdentifiers()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        string session = $"{pageId}-session";
        ctx.Sessions[session] = pageId;

        CdpDomainFixtures.Unwrap(await PageDomain.HandleAsync(
            "navigate",
            CdpDomainFixtures.Json(
                """{"url":"data:text/html,<main><button id=target>Go</button></main>","waitUntil":"load"}"""),
            ctx,
            session));

        JsonNode query = CdpDomainFixtures.Unwrap(await DomDomain.HandleAsync(
            "querySelector", new JsonObject { ["selector"] = "#target" }, ctx, session));
        ulong nodeId = query["nodeId"].AsU64()!.Value;

        JsonNode resolved = CdpDomainFixtures.Unwrap(await DomDomain.HandleAsync(
            "resolveNode", new JsonObject { ["nodeId"] = nodeId }, ctx, session));
        string objectId = resolved["object"]!["objectId"]!.GetValue<string>();

        JsonObject[] identifiers =
        [
            new() { ["nodeId"] = nodeId },
            new() { ["backendNodeId"] = nodeId },
            new() { ["objectId"] = objectId },
        ];
        foreach (JsonObject parameters in identifiers)
        {
            ctx.GetSessionPageMut(session)!.Evaluate("globalThis.__obscura_click_target = null");

            CdpDomainFixtures.Unwrap(
                await DomDomain.HandleAsync("scrollIntoViewIfNeeded", parameters, ctx, session));

            JsonNode? targetId = ctx.GetSessionPageMut(session)!.Evaluate(
                "globalThis.__obscura_click_target && globalThis.__obscura_click_target.id");
            Assert.Equal("target", targetId.AsString());
        }
    }

    [Fact]
    public async Task ScrollIntoViewIfNeededRequiresANodeIdentifier()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        string session = $"{pageId}-session";
        ctx.Sessions[session] = pageId;

        string error = CdpDomainFixtures.ErrorOf(
            await DomDomain.HandleAsync("scrollIntoViewIfNeeded", new JsonObject(), ctx, session));
        Assert.Equal("nodeId, backendNodeId, or objectId required", error);

        error = CdpDomainFixtures.ErrorOf(await DomDomain.HandleAsync(
            "scrollIntoViewIfNeeded",
            new JsonObject { ["nodeId"] = 999_999 },
            ctx,
            session));
        Assert.Equal("node 999999 could not be resolved to a scrollable element", error);
    }

    /// <summary>
    /// A hostile or heavy page can nest nodes tens of thousands deep (trivially scriptable,
    /// and the parser puts no cap on generic nesting). DOM.getDocument with the standard
    /// depth:-1 (which becomes uint.MaxValue here) must handle such a tree without crashing
    /// the worker.
    /// </summary>
    /// <remarks>
    /// Two failure modes: (1) a recursive SerializeNode overflows the stack building the
    /// value, and (2) even an iterative builder would emit a value so deeply nested that the
    /// serializer's own recursion overflows. The fix derecurses the builder AND bounds the
    /// depth, so the response is always safe to serialize.
    /// </remarks>
    [Fact]
    public void GetDocumentDeepTreeDoesNotOverflow()
    {
        // Build the deep chain directly (no parser) so setup is O(n) and fast.
        var dom = new DomTree();
        NodeId parent = dom.Document;
        const int depth = 50_000;
        for (int i = 0; i < depth; i++)
        {
            NodeId n = dom.NewNode(NodeData.Text(string.Empty));
            dom.AppendChild(parent, n);
            parent = n;
        }

        // Mirror getDocument {"depth": -1}: AsI64() == -1, then cast to uint.
        JsonNode? node = DomDomain.SerializeNode(dom, dom.Document, unchecked((uint)-1L), 0);

        // The serializer's own writer recurses over the value nesting; this must not
        // overflow either. That is why the fix bounds depth, not just the builder.
        string serialized = CdpJson.Serialize(node);
        Assert.NotEmpty(serialized);

        // Output nesting is bounded well below the tree's true depth (truncated, not
        // crashed) yet still serializes a meaningful prefix.
        JsonNode? current = node;
        int levels = 0;
        while (current?["children"] is JsonArray children)
        {
            if (children.Count == 0)
            {
                break;
            }

            current = children[0];
            levels++;
        }

        Assert.True(levels >= 100, $"should serialize a deep prefix, got {levels}");
        Assert.True(levels < depth, $"nesting must be bounded below the tree's true depth, got {levels}");
    }

    /// <summary>
    /// SEC-003 / #579 - DOM.setFileInputFiles reads local files and hands their bytes to
    /// page JS. Without a gate, any CDP client (e.g. against a Docker image that binds the
    /// port to 0.0.0.0) gets an arbitrary file-read primitive. It must honour the same
    /// <c>allow_file_access</c> opt-in as Page.navigate to <c>file://</c>, which defaults
    /// to off.
    /// </summary>
    [Fact]
    public async Task SetFileInputFilesRefusesWithoutAllowFileAccess()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        string session = $"{pageId}-session";
        ctx.Sessions[session] = pageId;

        CdpDomainFixtures.Unwrap(await PageDomain.HandleAsync(
            "navigate",
            CdpDomainFixtures.Json(
                """{"url":"data:text/html,<input type=file id=f>","waitUntil":"load"}"""),
            ctx,
            session));

        JsonNode qs = CdpDomainFixtures.Unwrap(await DomDomain.HandleAsync(
            "querySelector", new JsonObject { ["selector"] = "input" }, ctx, session));
        ulong nid = qs["nodeId"].AsU64()!.Value;

        // A real, readable file. Without the gate the handler slurps it and returns Ok;
        // with the gate it must refuse before touching the disk.
        string existing = System.Reflection.Assembly.GetExecutingAssembly().Location;
        Assert.True(File.Exists(existing));
        string error = CdpDomainFixtures.ErrorOf(await DomDomain.HandleAsync(
            "setFileInputFiles",
            new JsonObject { ["nodeId"] = nid, ["files"] = new JsonArray(existing) },
            ctx,
            session));
        Assert.Contains("allow-file-access", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #576: DOM.getBoxModel / getContentQuads build their quad from f64 pixel
    /// values, so the serializer emits integral coordinates as <c>256.0</c>. Strict CDP
    /// clients (Hermes Agent) deserialize the quad as i64 and reject the float.
    /// <c>CoordValue</c> must serialize integral coordinates as integers, the way Chrome
    /// does, while leaving genuinely fractional ones as floats.
    /// </summary>
    [Fact]
    public void BoxModelIntegralCoordinatesSerializeAsIntegers()
    {
        Assert.Equal("256", CdpJson.Serialize(DomDomain.CoordValue(256.0)));
        Assert.Equal("0", CdpJson.Serialize(DomDomain.CoordValue(0.0)));
        // Fractional coordinates stay floats.
        Assert.Equal("206.0390625", CdpJson.Serialize(DomDomain.CoordValue(206.0390625)));
        // A full quad mixes both, exactly as DOM.getBoxModel returns it.
        var quad = new JsonArray();
        foreach (double value in new[]
        {
            256.0, 206.0390625, 347.25, 206.0390625, 347.25, 225.0390625, 256.0, 225.0390625,
        })
        {
            quad.Add(DomDomain.CoordValue(value));
        }

        Assert.Equal(
            "[256,206.0390625,347.25,206.0390625,347.25,225.0390625,256,225.0390625]",
            CdpJson.Serialize(quad));
    }
}
