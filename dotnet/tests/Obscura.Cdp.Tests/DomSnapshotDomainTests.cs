using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Xunit;
using DomDomain = Obscura.Cdp.Domains.Dom;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-cdp/src/domains/domsnapshot.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class DomSnapshotDomainTests
{
    private static void CollectBackendIds(JsonNode? node, List<long> output)
    {
        // DOM.getDocument mints its ids as u64 and DOMSnapshot as i64, so both go through the
        // serde-shaped accessor rather than a CLR-typed GetValue.
        if (node?["backendNodeId"].AsI64() is { } id)
        {
            output.Add(id);
        }

        if (node?["children"] is JsonArray children)
        {
            foreach (JsonNode? child in children)
            {
                CollectBackendIds(child, output);
            }
        }
    }

    private static long? FindBackendIdByName(JsonNode? node, string name)
    {
        if (node?["nodeName"]?.GetValue<string>() == name)
        {
            return node["backendNodeId"].AsI64();
        }

        if (node?["children"] is JsonArray children)
        {
            foreach (JsonNode? child in children)
            {
                if (FindBackendIdByName(child, name) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static async Task<(CdpContext Ctx, string SessionId)> NavigateAsync(string body)
    {
        (CdpContext ctx, string session) = CdpDomainFixtures.NewSession("snapshot-session");
        await ctx.GetSessionPageMut(session)!.NavigateAsync("data:text/html," + body);
        return (ctx, session);
    }

    [Fact]
    public async Task CaptureSnapshotMatchesGetDocumentAndFlagsClickable()
    {
        (CdpContext ctx, string session) =
            await NavigateAsync("<button id=go>Go</button><a href=/x>L</a>");

        // DOM.getDocument is the structural source; the snapshot must use the same
        // backendNodeId scheme so a client can correlate the two.
        JsonNode doc = CdpDomainFixtures.Unwrap(await DomDomain.HandleAsync(
            "getDocument",
            CdpDomainFixtures.Json("""{"depth": -1}"""),
            ctx,
            session));
        var docIds = new List<long>();
        CollectBackendIds(doc["root"], docIds);
        Assert.NotEmpty(docIds);

        JsonNode snap = CdpDomainFixtures.Unwrap(
            await DomSnapshot.HandleAsync("captureSnapshot", new JsonObject(), ctx, session));

        JsonArray documents = snap["documents"]!.AsArray();
        Assert.Single(documents);
        JsonNode nodes = documents[0]!["nodes"]!;
        JsonNode layout = documents[0]!["layout"]!;

        List<long> snapIds = [.. nodes["backendNodeId"]!.AsArray().Select(v => v.AsI64()!.Value)];

        // Every node from getDocument must appear in the snapshot under the identical
        // backendNodeId.
        foreach (long id in docIds)
        {
            Assert.Contains(id, snapIds);
        }

        // String table is populated (everything is referenced by index).
        Assert.True(snap["strings"]!.AsArray().Count > 1, "string table should be populated");

        // Layout arrays are aligned 1:1 with nodes and carry bounds plus the 10 computed styles
        // browser-use reads back positionally.
        int n = snapIds.Count;
        Assert.Equal(n, layout["nodeIndex"]!.AsArray().Count);
        Assert.Equal(n, layout["bounds"]!.AsArray().Count);
        Assert.Equal(n, layout["styles"]!.AsArray().Count);
        Assert.Equal(4, layout["bounds"]![0]!.AsArray().Count);
        Assert.Equal(DomSnapshot.RequiredStyles.Length, layout["styles"]![0]!.AsArray().Count);

        // Interactive elements are flagged isClickable (by node index).
        List<long> clickable =
            [.. nodes["isClickable"]!["index"]!.AsArray().Select(v => v!.GetValue<long>())];
        foreach (string tag in (string[])["BUTTON", "A"])
        {
            long backendId = FindBackendIdByName(doc["root"], tag)
                ?? throw new InvalidOperationException($"{tag} should be in the document");
            long index = snapIds.IndexOf(backendId);
            Assert.True(index >= 0, $"{tag} should be in the snapshot");
            Assert.Contains(index, clickable);
        }
    }

    [Fact]
    public async Task UnknownDomSnapshotMethodIsPermissiveNoop()
    {
        // Probing the domain (for example getSnapshot) must not abort with an Unknown-method error
        // the way an unhandled domain would.
        var ctx = CdpContext.New();
        JsonNode result = CdpDomainFixtures.Unwrap(
            await DomSnapshot.HandleAsync("getSnapshot", new JsonObject(), ctx, null));
        Assert.IsType<JsonObject>(result);
    }

    /// <summary>
    /// Not in the Rust file: the synthetic geometry, the string interning, and the display:none
    /// projection for the tags that never paint. browser-use reads all three positionally.
    /// </summary>
    [Fact]
    public async Task SynthesizedLayoutIsAStackAndNonPaintingTagsReportDisplayNone()
    {
        (CdpContext ctx, string session) =
            await NavigateAsync("<title>T</title><p>hi</p><span onclick='x()'>c</span>");

        JsonNode snap = CdpDomainFixtures.Unwrap(
            await DomSnapshot.HandleAsync("captureSnapshot", new JsonObject(), ctx, session));
        JsonNode document = snap["documents"]!.AsArray()[0]!;
        List<string> strings = [.. snap["strings"]!.AsArray().Select(v => v!.GetValue<string>())];

        Assert.Equal(string.Empty, strings[0]);
        Assert.Equal(strings.Count, strings.Distinct(StringComparer.Ordinal).Count());

        JsonArray nodeNames = document["nodes"]!["nodeName"]!.AsArray();
        JsonArray styles = document["layout"]!["styles"]!.AsArray();
        JsonArray bounds = document["layout"]!["bounds"]!.AsArray();
        JsonArray parents = document["nodes"]!["parentIndex"]!.AsArray();

        Assert.Equal(-1, parents[0]!.GetValue<long>());
        int displayIndex = Array.IndexOf(DomSnapshot.RequiredStyles, "display");
        int cursorIndex = Array.IndexOf(DomSnapshot.RequiredStyles, "cursor");

        for (int i = 0; i < nodeNames.Count; i++)
        {
            string name = strings[(int)nodeNames[i]!.GetValue<long>()];
            string display = strings[(int)styles[i]!.AsArray()[displayIndex]!.GetValue<long>()];
            bool hidden = name is "HEAD" or "META" or "TITLE" or "SCRIPT" or "STYLE" or "LINK"
                or "NOSCRIPT" or "BASE";
            Assert.Equal(hidden ? "none" : "block", display);
            JsonArray box = bounds[i]!.AsArray();
            Assert.Equal(0.0, box[0]!.GetValue<double>());
            Assert.Equal(i * 18.0, box[1]!.GetValue<double>());
            Assert.Equal(1280.0, box[2]!.GetValue<double>());
            Assert.Equal(18.0, box[3]!.GetValue<double>());
        }

        // An onclick attribute makes an otherwise inert element clickable, with a pointer cursor
        // reserved for the interactive tags.
        int spanIndex = nodeNames.Select((v, i) => (v, i))
            .First(pair => strings[(int)pair.v!.GetValue<long>()] == "SPAN").i;
        List<long> clickable =
            [.. document["nodes"]!["isClickable"]!["index"]!.AsArray().Select(v => v!.GetValue<long>())];
        Assert.Contains(spanIndex, clickable);
        Assert.Equal("auto", strings[(int)styles[spanIndex]!.AsArray()[cursorIndex]!.GetValue<long>()]);

        Assert.Equal(
            nodeNames.Count * 18L,
            document["contentHeight"]!.GetValue<long>());
        Assert.Equal(1280, document["contentWidth"]!.GetValue<int>());
    }
}
