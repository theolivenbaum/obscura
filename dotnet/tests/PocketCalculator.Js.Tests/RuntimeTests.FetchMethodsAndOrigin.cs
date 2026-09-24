using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// todo.md "fetch:" leftovers, measured against Chromium 141: forbidden methods are refused
/// with Chromium's errors, method casing follows Fetch's normalization, script cannot replace
/// the User-Agent, and Origin goes out only on CORS requests to another origin and on
/// non-GET/HEAD requests.
/// </summary>
public sealed partial class RuntimeTests
{
    [Fact]
    public void FetchMethodChecksMatchChromium()
    {
        using var fixture = RuntimeFixture.Blank();
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const out = {};
                const t = (k, f) => { try { out[k] = f(); } catch (e) { out[k] = e.name + ": " + e.message; } };
                for (const m of ["CONNECT", "trace", "Track", "patch", "Get", "delete", "foo", "a b", ""]) {
                    t("req:" + m, () => new Request("http://a.test/", { method: m }).method);
                }
                t("reqUA", () => new Request("http://a.test/", { headers: { "User-Agent": "x" } }).headers.get("user-agent"));
                t("plainUA", () => { const h = new Headers(); h.set("User-Agent", "x"); return h.get("user-agent"); });
                t("xhrTrack", () => new XMLHttpRequest().open("track", "/x"));
                t("xhrBad", () => new XMLHttpRequest().open("a b", "/x"));
                t("xhrUA", () => {
                    const x = new XMLHttpRequest();
                    x.open("patch", "/");
                    x.setRequestHeader("User-Agent", "x");
                    return [x._method, Object.keys(x._headers).length];
                });
                return JSON.stringify(out);
            })()
            """);

        AssertJsonEquals(
            """
            {
              "req:CONNECT": "TypeError: Failed to construct 'Request': 'CONNECT' HTTP method is unsupported.",
              "req:trace": "TypeError: Failed to construct 'Request': 'trace' HTTP method is unsupported.",
              "req:Track": "TypeError: Failed to construct 'Request': 'Track' HTTP method is unsupported.",
              "req:patch": "patch",
              "req:Get": "GET",
              "req:delete": "DELETE",
              "req:foo": "foo",
              "req:a b": "TypeError: Failed to construct 'Request': 'a b' is not a valid HTTP method.",
              "req:": "TypeError: Failed to construct 'Request': '' is not a valid HTTP method.",
              "reqUA": null,
              "plainUA": "x",
              "xhrTrack": "SecurityError: Failed to execute 'open' on 'XMLHttpRequest': 'track' HTTP method is unsupported.",
              "xhrBad": "SyntaxError: Failed to execute 'open' on 'XMLHttpRequest': 'a b' is not a valid HTTP method.",
              "xhrUA": ["patch", 0]
            }
            """,
            System.Text.Json.Nodes.JsonNode.Parse(result!.GetValue<string>()));
    }

    [Fact]
    public async Task ForbiddenMethodsNeverReachTheServer()
    {
        using var server = new RawHttpServer(_ => OkCorsResponse);
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const viaFetch = await fetch("{{server.Origin}}/trace", { method: "TRACE" })
                    .then(() => "resolved", e => e.name + ": " + e.message);
                const viaOp = await Promise.resolve(__obscura_test_ops.op_fetch_url(
                    "{{server.Origin}}/connect-op", "connect", "{}", new Uint8Array(0), "",
                    "cors", "same-origin", false)).then(() => "resolved", () => "rejected");
                return { viaFetch, viaOp };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        AssertJsonEquals(
            """{ "viaFetch": "TypeError: Failed to execute 'fetch' on 'Window': 'TRACE' HTTP method is unsupported.", "viaOp": "rejected" }""",
            result.Value);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ScriptCannotReplaceTheUserAgent()
    {
        using var server = new RawHttpServer(_ => OkCorsResponse);
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                await fetch("{{server.Origin}}/fetch-ua", { headers: { "User-Agent": "evil-ua" } });
                await new Promise(resolve => {
                    const xhr = new XMLHttpRequest();
                    xhr.onload = xhr.onerror = resolve;
                    xhr.open("GET", "{{server.Origin}}/xhr-ua");
                    xhr.setRequestHeader("User-Agent", "evil-ua");
                    xhr.send();
                });
                await fetch("{{server.Origin}}/patch", { method: "patch" });
                return true;
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        var requests = server.Requests;
        foreach (var path in new[] { "/fetch-ua", "/xhr-ua" })
        {
            var sent = Assert.Single(requests, r => r.StartsWith("GET " + path + " ", StringComparison.Ordinal));
            Assert.DoesNotContain("evil-ua", sent, StringComparison.Ordinal);
            Assert.Contains("\r\nUser-Agent: Mozilla/5.0", sent, StringComparison.OrdinalIgnoreCase);
        }

        // Script sees 'patch' (above); SocketsHttpHandler writes every method it knows
        // (PATCH among them) in upper case, so the wire says PATCH where Chromium sends
        // 'patch'. A platform limit, recorded as a known deviation.
        Assert.Single(requests, r => r.StartsWith("PATCH /patch ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OriginHeaderFollowsFetchRules()
    {
        using var server = new RawHttpServer(_ => OkCorsResponse);
        using var crossFixture = RedirectRuntimeForOrigin("http://example.com");
        await crossFixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                await fetch("{{server.Origin}}/nocors-get", { mode: "no-cors" });
                await fetch("{{server.Origin}}/nocors-head", { mode: "no-cors", method: "HEAD" });
                await fetch("{{server.Origin}}/nocors-post", { mode: "no-cors", method: "POST", body: "x" });
                await fetch("{{server.Origin}}/cors-get");
                return true;
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        using var sameFixture = RedirectRuntimeForOrigin(server.Origin);
        await sameFixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                await fetch("{{server.Origin}}/same-get");
                await fetch("{{server.Origin}}/same-post", { method: "POST", body: "x" });
                return true;
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        var requests = server.Requests;
        string Sent(string start) => Assert.Single(requests, r => r.StartsWith(start, StringComparison.Ordinal));

        Assert.False(HasHeader(Sent("GET /nocors-get "), "Origin"));
        Assert.False(HasHeader(Sent("HEAD /nocors-head "), "Origin"));
        Assert.Contains("\r\nOrigin: http://example.com\r\n", Sent("POST /nocors-post "), StringComparison.Ordinal);
        Assert.Contains("\r\nOrigin: http://example.com\r\n", Sent("GET /cors-get "), StringComparison.Ordinal);
        Assert.False(HasHeader(Sent("GET /same-get "), "Origin"));
        Assert.Contains($"\r\nOrigin: {server.Origin}\r\n", Sent("POST /same-post "), StringComparison.Ordinal);
    }
}
