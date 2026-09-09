using System.Text.Json.Nodes;

using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/structured_clone_crypto_parity.rs</c>.
/// </summary>
/// <remarks>
/// Regression parity for issue #389: Cloudflare managed challenges hang because
/// <c>bootstrap.js</c> stubbed two structured-clone primitives the turnstile orchestrate VM
/// depends on: <c>structuredClone</c> must preserve ArrayBuffer / TypedArray bytes, and a
/// <c>CryptoKey</c> must survive <c>structuredClone</c> and remain usable by
/// <c>crypto.subtle</c>.
/// </remarks>
[Collection(CdpDomainCollection.Name)]
public sealed class StructuredCloneCryptoParity
{
    private const string Body =
        "<html><body><script>window.__boot = true;</script></body></html>";

    private static async Task<JsonNode> ProbeAsync(string expression)
    {
        using var server = CoreCdpServer.Html(Body);
        (CdpContext ctx, string session) = await CoreCdp.NavigateAsync(server.Url);
        JsonNode evaluated = await CoreCdp.EvalAsync(ctx, 2, expression, session, awaitPromise: true);
        return CoreCdp.ParseStringified(evaluated);
    }

    /// <summary>
    /// A 4-byte view into a 4-byte buffer. The JSON fallback lost the buffer entirely
    /// (Uint8Array serializes to <c>{}</c>), so byteLength read back as 0.
    /// </summary>
    [Fact]
    public async Task StructuredClonePreservesArraybufferBytes()
    {
        JsonNode value = await ProbeAsync("""
            (async () => {
                const src = new Uint8Array([10, 20, 30, 40]);
                const clone = structuredClone(src);
                return JSON.stringify({
                    srcLen: src.byteLength,
                    cloneLen: clone.byteLength,
                    same: src.buffer === clone.buffer,
                    bytes: Array.from(clone),
                });
            })()
            """);
        Assert.Equal(4, value["srcLen"].AsI64());
        Assert.Equal(4, value["cloneLen"].AsI64());
        Assert.False(value["same"].AsBool(), "clone must be independent, not the same buffer");
        Assert.Equal("[10,20,30,40]", CdpJson.Serialize(value["bytes"]));
    }

    /// <summary>
    /// importKey -&gt; structuredClone -&gt; sign with the clone. Before the fix the clone had
    /// no WeakMap entry, so sign threw "Argument is not a valid CryptoKey".
    /// </summary>
    [Fact]
    public async Task CryptokeySurvivesStructuredCloneAndStillSigns()
    {
        JsonNode value = await ProbeAsync("""
            (async () => {
                const key = await crypto.subtle.importKey(
                    "raw", new Uint8Array(32),
                    { name: "HMAC", hash: "SHA-256" }, true, ["sign"]
                );
                const clone = structuredClone(key);
                const sig = await crypto.subtle.sign("HMAC", clone, new TextEncoder().encode("abc"));
                const b = new Uint8Array(sig);
                return JSON.stringify({
                    cloneType: clone.type,
                    cloneTag: clone[Symbol.toStringTag],
                    sigLen: b.length,
                });
            })()
            """);
        Assert.Equal("secret", value["cloneType"].AsString());
        Assert.Equal("CryptoKey", value["cloneTag"].AsString());
        Assert.Equal(32, value["sigLen"].AsI64());
    }

    /// <summary>
    /// DataView has no <c>.slice()</c> method, so the original TypedArray branch
    /// (<c>new Ctor(value.slice())</c>) threw <c>TypeError: value.slice is not a function</c>
    /// on every DataView clone. That is a new crash in the very buffers category this
    /// feature targets, so it must clone cleanly.
    /// </summary>
    [Fact]
    public async Task StructuredClonePreservesDataview()
    {
        JsonNode value = await ProbeAsync("""
            (async () => {
                const buf = new ArrayBuffer(8);
                const view = new DataView(buf);
                view.setUint32(0, 0x12345678);
                view.setUint32(4, 0x9abcdef0);
                const clone = structuredClone(view);
                return JSON.stringify({
                    len: clone.byteLength,
                    a: clone.getUint32(0),
                    b: clone.getUint32(4),
                    independent: clone.buffer !== view.buffer,
                });
            })()
            """);
        Assert.Equal(8, value["len"].AsI64());
        Assert.Equal(0x12345678, value["a"].AsI64());
        Assert.Equal(0x9abcdef0L, value["b"].AsI64());
        Assert.True(value["independent"].AsBool(), "clone must own its buffer");
    }

    /// <summary>
    /// A reference cycle through <c>Error.cause</c> must clone without crashing (issue
    /// #419). The Error branch recursed into <c>cause</c> before recording itself in
    /// <c>seen</c>, so a self-referential cause blew the stack. Chrome clones this and
    /// preserves identity (<c>clone.cause === clone</c>).
    /// </summary>
    [Fact]
    public async Task StructuredCloneHandlesCircularErrorCause()
    {
        JsonNode value = await ProbeAsync("""
            (async () => {
                const e = new Error("boom");
                e.cause = e;
                const out = {};
                try {
                    const clone = structuredClone(e);
                    out.ok = true;
                    out.message = clone.message;
                    out.selfCycle = clone.cause === clone;
                    out.isError = clone instanceof Error;
                } catch (err) {
                    out.ok = false;
                    out.err = String(err && err.message || err);
                }
                return JSON.stringify(out);
            })()
            """);
        Assert.True(
            value["ok"].AsBool(),
            $"circular Error.cause crashed structuredClone: {value["err"].AsStringOr("")}");
        Assert.Equal("boom", value["message"].AsString());
        Assert.True(value["isError"].AsBool(), "clone must remain an Error");
        Assert.True(value["selfCycle"].AsBool(), "cyclic cause must resolve to the clone");
    }

    /// <summary>
    /// An own enumerable <c>__proto__</c> data property (what
    /// <c>JSON.parse('{"__proto__":...}')</c> produces) must clone as an own data property,
    /// not be routed through the inherited <c>__proto__</c> setter (issue #420). Plain
    /// objects must also clone onto <c>Object.prototype</c>, matching Chrome.
    /// </summary>
    [Fact]
    public async Task StructuredCloneReproducesOwnProtoProperty()
    {
        JsonNode value = await ProbeAsync("""
            (async () => {
                const src = JSON.parse('{"__proto__":{"polluted":true},"a":1}');
                const clone = structuredClone(src);
                return JSON.stringify({
                    hasOwnProto: Object.prototype.hasOwnProperty.call(clone, "__proto__"),
                    plainProto: Object.getPrototypeOf(clone) === Object.prototype,
                    polluted: clone.polluted === true,
                    a: clone.a,
                });
            })()
            """);
        Assert.True(value["hasOwnProto"].AsBool(), "own __proto__ data property was lost");
        Assert.True(value["plainProto"].AsBool(), "plain object must clone onto Object.prototype");
        Assert.False(value["polluted"].AsBool(), "clone prototype was reparented");
        Assert.Equal(1, value["a"].AsI64());
    }

    /// <summary>
    /// Functions and symbols are not structured-cloneable. The original early
    /// <c>typeof !== "object"</c> return passed them through by reference instead of
    /// throwing DataCloneError, so this guards both the throw and the name.
    /// </summary>
    [Fact]
    public async Task StructuredCloneRejectsFunctionsAndSymbols()
    {
        JsonNode value = await ProbeAsync("""
            (async () => {
                const out = {};
                try { structuredClone(function f(){}); out.fn = "cloned"; }
                catch (e) { out.fn = e instanceof DOMException ? e.name : "TypeError:" + e.message; }
                try { structuredClone(Symbol("s")); out.sym = "cloned"; }
                catch (e) { out.sym = e instanceof DOMException ? e.name : "TypeError:" + e.message; }
                return JSON.stringify(out);
            })()
            """);
        Assert.Equal("DataCloneError", value["fn"].AsString());
        Assert.Equal("DataCloneError", value["sym"].AsString());
    }

    /// <summary>
    /// <c>structuredClone</c> preserves reference identity within one graph, including for
    /// platform objects cloned through a hook (issue #423). The CryptoKey hook ignored the
    /// <c>seen</c> map <c>_structuredClone</c> hands it, so one key referenced twice came
    /// back as two distinct objects.
    /// </summary>
    [Fact]
    public async Task StructuredClonePreservesCryptokeyIdentity()
    {
        JsonNode value = await ProbeAsync("""
            (async () => {
                const key = await crypto.subtle.importKey(
                    "raw", new Uint8Array(32),
                    { name: "HMAC", hash: "SHA-256" }, true, ["sign"]
                );
                const clone = structuredClone({ a: key, b: key });
                const sig = await crypto.subtle.sign("HMAC", clone.a, new TextEncoder().encode("abc"));
                return JSON.stringify({
                    shared: clone.a === clone.b,
                    distinctFromSource: clone.a !== key,
                    tag: clone.a[Symbol.toStringTag],
                    sigLen: new Uint8Array(sig).length,
                });
            })()
            """);
        Assert.True(value["shared"].AsBool(), "one CryptoKey reached twice must clone to one object");
        Assert.True(value["distinctFromSource"].AsBool(), "clone must not alias the source key");
        Assert.Equal("CryptoKey", value["tag"].AsString());
        Assert.Equal(32, value["sigLen"].AsI64());
    }
}
