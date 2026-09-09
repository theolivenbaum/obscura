using System.Text.Json.Nodes;

using Xunit;

using PageDomain = Obscura.Cdp.Domains.Page;
using RuntimeDomain = Obscura.Cdp.Domains.Runtime;

namespace Obscura.Cdp.Tests;

/// <summary>
/// Port-specific regression cover for the <c>value</c> field of a
/// <c>Runtime.RemoteObject</c>.
/// </summary>
/// <remarks>
/// Rust carries it as <c>Option&lt;serde_json::Value&gt;</c> and stores
/// <c>Some(Value::Null)</c> for a null result, so the reply is
/// <c>{"type":"object","subtype":"null","description":"null","value":null}</c> and a
/// client reading <c>result.value</c> sees null, the way Chrome reports it. The port's
/// field is a <see cref="JsonNode"/>, where a JSON null and an absent value are both a C#
/// null, and the key was dropped: <c>result.value</c> came back <c>undefined</c>. The
/// distinction is rebuilt in <c>RemoteObjectFromInfo</c>, so both halves are asserted -
/// the key is present for a null, and still absent everywhere Rust omits it.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class RuntimeRemoteObjectNullValue
{
    private static async Task<JsonNode?> EvaluateAsync(
        CdpContext ctx,
        string session,
        string expression,
        bool returnByValue)
    {
        JsonNode reply = CdpDomainFixtures.Unwrap(await RuntimeDomain.HandleAsync(
            "evaluate",
            new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = returnByValue,
                ["timeout"] = 3000,
            },
            ctx,
            session));
        Assert.Null(reply.Get("exceptionDetails"));
        return reply.Get("result");
    }

    private static async Task<(CdpContext Ctx, string Session)> LivePageAsync()
    {
        var ctx = CdpContext.New();
        string pageId = ctx.CreatePage();
        string session = $"{pageId}-session";
        ctx.Sessions[session] = pageId;
        CdpDomainFixtures.Unwrap(await PageDomain.HandleAsync(
            "navigate",
            CdpDomainFixtures.Json("""{"url":"data:text/html,<p>hi</p>","waitUntil":"load"}"""),
            ctx,
            session));
        return (ctx, session);
    }

    [Fact]
    public async Task ANullResultCarriesAnExplicitJsonNullValue()
    {
        (CdpContext ctx, string session) = await LivePageAsync();
        using IDisposable owned = CoreCdp.Owned(ctx);

        JsonNode? result = await EvaluateAsync(ctx, session, "null", returnByValue: true);

        // The whole object as it goes on the wire, key for key and in insertion order,
        // the way remote_object_from_info builds it: type, subtype, description, value.
        // className is empty and so omitted.
        Assert.Equal(
            """{"type":"object","subtype":"null","description":"null","value":null}""",
            CdpJson.Serialize(result));
        Assert.True(
            result is JsonObject shape && shape.ContainsKey("value"),
            $"the value key must be present: {CdpJson.Serialize(result)}");
    }

    /// <summary>
    /// A null that arrives by way of an expression rather than the literal takes the same
    /// path, so it reports the same shape.
    /// </summary>
    [Fact]
    public async Task AComputedNullReportsTheSameShape()
    {
        (CdpContext ctx, string session) = await LivePageAsync();
        using IDisposable owned = CoreCdp.Owned(ctx);

        foreach (string expression in new[]
        {
            "(function() { return null; })()",
            "[null][0]",
            "({ a: null }).a",
        })
        {
            JsonNode? result = await EvaluateAsync(ctx, session, expression, returnByValue: true);
            Assert.Equal(
                """{"type":"object","subtype":"null","description":"null","value":null}""",
                CdpJson.Serialize(result));
        }
    }

    /// <summary>
    /// The guard must not start emitting <c>value</c> where Rust omits it entirely: an
    /// object, array or function reported by reference travels as an objectId alone, and
    /// so does a null reported by reference (<c>info_from_meta</c> supplies no value for
    /// an <c>object</c> type, whatever its subtype).
    /// </summary>
    [Fact]
    public async Task ValueStaysAbsentWhereTheReferenceOmitsIt()
    {
        (CdpContext ctx, string session) = await LivePageAsync();
        using IDisposable owned = CoreCdp.Owned(ctx);

        foreach (string expression in new[] { "({a:1})", "[1,2,3]", "(function f() {})", "null" })
        {
            JsonNode? byReference =
                await EvaluateAsync(ctx, session, expression, returnByValue: false);
            Assert.NotNull(byReference.Get("objectId").AsString());
            Assert.Contains(byReference.Get("type").AsString(), new[] { "object", "function" });
            Assert.False(
                byReference is JsonObject held && held.ContainsKey("value"),
                $"a handle must carry no value: {CdpJson.Serialize(byReference)}");
        }
    }

    /// <summary>
    /// The other by-value primitives keep reporting their own value, so the added branch
    /// has not displaced the ordinary path. Compared as wire text because the number's
    /// <c>2.0</c> is an f64 in both engines, which a JSON-node comparison would flatten
    /// to <c>2</c>.
    /// </summary>
    [Fact]
    public async Task ByValuePrimitivesStillReportTheirValue()
    {
        (CdpContext ctx, string session) = await LivePageAsync();
        using IDisposable owned = CoreCdp.Owned(ctx);

        Assert.Equal(
            """{"type":"number","description":"2.0","value":2.0}""",
            CdpJson.Serialize(await EvaluateAsync(ctx, session, "1 + 1", returnByValue: true)));
        Assert.Equal(
            """{"type":"boolean","description":"false","value":false}""",
            CdpJson.Serialize(await EvaluateAsync(ctx, session, "1 > 2", returnByValue: true)));
        Assert.Equal(
            """{"type":"string","description":"hi","value":"hi"}""",
            CdpJson.Serialize(await EvaluateAsync(ctx, session, "'hi'", returnByValue: true)));
    }

    /// <summary>
    /// <c>undefined</c> reported by reference is the case that looks closest to a null but
    /// must not be touched: <c>info_from_meta</c> gives a non-object type its description
    /// as the value, and <c>undefined</c>'s description is the empty string, so the
    /// reference server emits <c>"value": ""</c> here rather than a null or nothing.
    /// </summary>
    [Fact]
    public async Task UndefinedByReferenceReportsTheEmptyDescriptionAsItsValue()
    {
        (CdpContext ctx, string session) = await LivePageAsync();
        using IDisposable owned = CoreCdp.Owned(ctx);

        JsonNode? result = await EvaluateAsync(ctx, session, "undefined", returnByValue: false);
        Assert.Equal("undefined", result.Get("type").AsString());
        Assert.Null(result.Get("subtype"));
        Assert.Equal(string.Empty, result.Get("value").AsString());
    }
}
