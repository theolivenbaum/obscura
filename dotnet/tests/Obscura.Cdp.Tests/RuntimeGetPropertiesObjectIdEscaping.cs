using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of
/// <c>crates/obscura-cdp/tests/runtime_get_properties_objectid_escaping.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Runtime.getProperties</c> mints a child objectId as <c>parentOid + '::' + key</c> and
/// interpolates it straight back into a generated <c>__obscura_objects['&lt;oid&gt;']</c>
/// lookup on the next call. The key comes from the page, so the page - not the client -
/// decides which characters land inside that literal (issue #709).
/// </para>
/// <para>
/// Escaping only <c>\</c> and <c>'</c> leaves every C0 control alone, and a raw newline
/// ends a JS string literal exactly as a stray quote does. The generated snippet is then a
/// syntax error, the evaluation yields no array, and the handler falls through to
/// <c>result: []</c>. That is the part worth a regression test: the call still answers
/// without an error and with an empty property list, so a client walking a nested object
/// sees an object that has no properties rather than a failure.
/// </para>
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class RuntimeGetPropertiesObjectIdEscaping
{
    /// <summary>The <c>objectId</c> of the property descriptor named <paramref name="name"/>.</summary>
    private static string? ChildObjectId(JsonNode properties, string name) =>
        JsonExt.AsJsonArray(properties.Get("result"))?
            .FirstOrDefault(d => d.Get("name").AsString() == name)
            .Get("value")
            .Get("objectId")
            .AsString();

    /// <summary>The string value of the property descriptor named <paramref name="name"/>.</summary>
    private static string? StringProperty(JsonNode properties, string name) =>
        JsonExt.AsJsonArray(properties.Get("result"))?
            .FirstOrDefault(d => d.Get("name").AsString() == name)
            .Get("value")
            .Get("value")
            .AsString();

    /// <summary>
    /// The keys are assembled with <c>String.fromCharCode</c> so this expression carries no
    /// backslash of its own: the characters under test are exactly the four below and not
    /// an artefact of how the literal was written. A page is free to define every one of
    /// them, and a client cannot sanitise them away - it never sees the key until
    /// getProperties has already built an objectId out of it.
    /// </summary>
    private const string Setup = """
        (function () {
            var LF = String.fromCharCode(10);
            var CR = String.fromCharCode(13);
            var NUL = String.fromCharCode(0);
            var BACKSLASH = String.fromCharCode(92);
            var o = {};
            o["lf" + LF + "x"] = { mark: "lf" };
            o["cr" + CR + "x"] = { mark: "cr" };
            o["nul" + NUL + "x"] = { mark: "nul" };
            o["quote'" + BACKSLASH + "x"] = { mark: "quote" };
            globalThis.__t = o;
            return o;
        })()
        """;

    [Fact]
    public async Task GetPropertiesWalksIntoAChildWhoseKeyHoldsAControlCharacter()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;

        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject { ["url"] = "about:blank", ["waitUntil"] = "load" },
            sessionId);

        // returnByValue omitted, so the result is a handle rather than a copy.
        JsonNode root = await CoreCdp.CdpAsync(
            ctx, 2, "Runtime.evaluate", new JsonObject { ["expression"] = Setup }, sessionId);
        string rootOid = root.Get("result").Get("objectId").AsString()
            ?? throw new InvalidOperationException(
                "Runtime.evaluate must hand back an objectId for an object result");

        JsonNode top = await CoreCdp.CdpAsync(
            ctx,
            3,
            "Runtime.getProperties",
            new JsonObject { ["objectId"] = rootOid },
            sessionId);

        // The last pair is the case the hand-rolled escaping already covered. It is here so
        // a regression in either direction shows up in one run.
        (string Key, string Mark)[] cases =
        [
            ("lf\nx", "lf"),
            ("cr\rx", "cr"),
            ("nul\0x", "nul"),
            ("quote'\\x", "quote"),
        ];

        for (int i = 0; i < cases.Length; i++)
        {
            (string key, string mark) = cases[i];
            string childOid = ChildObjectId(top, key)
                ?? throw new InvalidOperationException(
                    $"no child objectId for key {key}; descriptors were {CdpJson.Serialize(top)}");

            JsonNode child = await CoreCdp.CdpAsync(
                ctx,
                (ulong)(10 + i),
                "Runtime.getProperties",
                new JsonObject { ["objectId"] = childOid },
                sessionId);

            Assert.Equal(mark, StringProperty(child, "mark"));
        }
    }

    /// <summary>
    /// The same page-minted objectId does not stay inside Runtime. Puppeteer's <c>$$</c>
    /// flow is evaluate -&gt; getProperties -&gt; asElement, and asElement handles go on to
    /// <c>DOM.describeNode</c> / <c>DOM.resolveNode</c>, which interpolate the id into the
    /// same kind of lookup. The DOM domain used to do that through its own escaper - the
    /// same two replacements, and so the same hole. Here the fallback was worse than an
    /// empty list: describeNode's fallback answers with node 0, so the client was handed a
    /// description of the wrong element rather than a failure.
    /// </summary>
    private const string SetupNodes = """
        (function () {
            var LF = String.fromCharCode(10);
            var o = {};
            o["plain"] = document.getElementById("a");
            o["lf" + LF + "x"] = document.getElementById("b");
            globalThis.__n = o;
            return o;
        })()
        """;

    [Fact]
    public async Task DescribeNodeResolvesAChildHandleWhoseKeyHoldsAControlCharacter()
    {
        var ctx = CdpContext.New();
        using IDisposable owned = CoreCdp.Owned(ctx);
        string pageId = ctx.CreatePage();
        const string sessionId = "session-1";
        ctx.Sessions[sessionId] = pageId;

        await CoreCdp.CdpAsync(
            ctx,
            1,
            "Page.navigate",
            new JsonObject
            {
                ["url"] = "data:text/html,<div id=a></div><p id=b></p>",
                ["waitUntil"] = "load",
            },
            sessionId);

        JsonNode root = await CoreCdp.CdpAsync(
            ctx, 2, "Runtime.evaluate", new JsonObject { ["expression"] = SetupNodes }, sessionId);
        string rootOid = root.Get("result").Get("objectId").AsString()
            ?? throw new InvalidOperationException(
                "Runtime.evaluate must hand back an objectId for an object result");

        JsonNode top = await CoreCdp.CdpAsync(
            ctx,
            3,
            "Runtime.getProperties",
            new JsonObject { ["objectId"] = rootOid },
            sessionId);

        (string Key, string NodeName)[] cases = [("plain", "DIV"), ("lf\nx", "P")];
        for (int i = 0; i < cases.Length; i++)
        {
            (string key, string nodeName) = cases[i];
            string childOid = ChildObjectId(top, key)
                ?? throw new InvalidOperationException(
                    $"no child objectId for key {key}; descriptors were {CdpJson.Serialize(top)}");

            JsonNode described = await CoreCdp.CdpAsync(
                ctx,
                (ulong)(20 + i),
                "DOM.describeNode",
                new JsonObject { ["objectId"] = childOid },
                sessionId);

            Assert.Equal(nodeName, described.Get("node").Get("nodeName").AsString());
        }
    }
}
