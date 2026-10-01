using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Fetch's request-header guards and the no-cors method rule, in the shim and in
/// <c>op_fetch_url</c>. Deviation from Rust, which forwards every header page script
/// sets and any method in no-cors mode; Chromium follows Fetch, which these assert.
/// </summary>
public sealed partial class RuntimeTests
{
    private const string OkCorsResponse =
        "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";

    private static string HeaderBlock(string request)
    {
        var end = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return end < 0 ? request : request[..end];
    }

    private static bool HasHeader(string request, string name) =>
        HeaderBlock(request).Split("\r\n").Any(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void RequestHeaderGuardsAreVisibleToScript()
    {
        using var fixture = RuntimeFixture.Blank();
        var result = fixture.Runtime.Evaluate(
            """
            (() => {
                const out = {};
                const t = (k, f) => { try { out[k] = f(); } catch (e) { out[k] = e.constructor.name; } };
                t("put", () => new Request("http://a.test/", { mode: "no-cors", method: "PUT" }).method);
                t("post", () => new Request("http://a.test/", { mode: "no-cors", method: "post" }).method);
                t("navigate", () => new Request("http://a.test/", { mode: "navigate" }).mode);
                t("bogusMode", () => new Request("http://a.test/", { mode: "bogus" }).mode);
                t("noCorsInit", () => [...new Request("http://a.test/", { mode: "no-cors", headers: {
                    Authorization: "x", Accept: "y", "X-Custom": "1", Range: "bytes=0-1",
                    "Content-Type": "application/json", Cookie: "c" } }).headers]);
                t("noCorsMutate", () => {
                    const r = new Request("http://a.test/", { mode: "no-cors" });
                    r.headers.set("X-Custom", "1");
                    r.headers.append("Authorization", "b");
                    r.headers.set("Accept", "a");
                    r.headers.append("Accept-Language", "en");
                    r.headers.append("Accept-Language", "de");
                    r.headers.set("Content-Type", "text/plain");
                    r.headers.set("Content-Type", "application/json");
                    return [...r.headers];
                });
                t("noCorsCombinedTooLong", () => {
                    const r = new Request("http://a.test/", { mode: "no-cors" });
                    r.headers.append("Accept", "a".repeat(100));
                    r.headers.append("Accept", "b".repeat(40));
                    return r.headers.get("accept").length;
                });
                t("corsRequest", () => [...new Request("http://a.test/", { headers: {
                    Cookie: "a", Host: "h", "Sec-X": "1", "Proxy-Foo": "1", Origin: "o",
                    Authorization: "z", "X-HTTP-Method-Override": "TRACE" } }).headers]);
                t("plainHeaders", () => {
                    const h = new Headers({ Cookie: "a" });
                    h.set("Sec-Foo", "1");
                    return [...h];
                });
                t("badName", () => new Headers().set("bad name", "x"));
                t("badValue", () => new Headers().set("a", "x\ny"));
                t("xhr", () => {
                    const x = new XMLHttpRequest();
                    x.open("GET", "/");
                    x.setRequestHeader("Cookie", "a");
                    x.setRequestHeader("Sec-Fetch-Mode", "cors");
                    x.setRequestHeader("X-A", "b");
                    return Object.keys(x._headers);
                });
                return JSON.stringify(out);
            })()
            """);

        AssertJsonEquals(
            """
            {
              "put": "TypeError",
              "post": "POST",
              "navigate": "TypeError",
              "bogusMode": "TypeError",
              "noCorsInit": [["accept", "y"]],
              "noCorsMutate": [["accept", "a"], ["accept-language", "en, de"], ["content-type", "text/plain"]],
              "noCorsCombinedTooLong": 100,
              "corsRequest": [["authorization", "z"]],
              "plainHeaders": [["cookie", "a"], ["sec-foo", "1"]],
              "badName": "TypeError",
              "badValue": "TypeError",
              "xhr": ["X-A"]
            }
            """,
            System.Text.Json.Nodes.JsonNode.Parse(result!.GetValue<string>()));
    }

    /// <summary>
    /// A cross-origin no-cors fetch() sends only the no-CORS-safelisted headers:
    /// Authorization and X-Custom never reach the server, Accept does, and no
    /// forbidden header the script named goes out as the script wrote it.
    /// </summary>
    [Fact]
    public async Task NoCorsFetchSendsOnlySafelistedHeaders()
    {
        using var server = new RawHttpServer(_ => OkCorsResponse);
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const r = await fetch("{{server.Origin}}/nocors", { mode: "no-cors", headers: {
                    Authorization: "Bearer secret", "X-Custom": "1", Accept: "text/plain",
                    "Accept-Language": "en", Cookie: "evil=1", Host: "evil.test",
                    "Sec-Fetch-Site": "none", "Proxy-Authorization": "p" } });
                const req = new Request("{{server.Origin}}/nocors-request", { mode: "no-cors" });
                req.headers.set("Authorization", "Bearer secret");
                req.headers.set("Content-Language", "de");
                await fetch(req);
                return r.type;
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("opaque", result.Value!.GetValue<string>());

        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        var first = requests[0];
        Assert.StartsWith("GET /nocors ", first, StringComparison.Ordinal);
        Assert.False(HasHeader(first, "Authorization"), first);
        Assert.False(HasHeader(first, "X-Custom"), first);
        Assert.False(HasHeader(first, "Cookie"), first);
        Assert.False(HasHeader(first, "Sec-Fetch-Site"), first);
        Assert.False(HasHeader(first, "Proxy-Authorization"), first);
        Assert.DoesNotContain("evil.test", first, StringComparison.Ordinal);
        Assert.Contains("\r\nAccept: text/plain\r\n", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\r\nAccept-Language: en\r\n", first, StringComparison.OrdinalIgnoreCase);

        var second = requests[1];
        Assert.StartsWith("GET /nocors-request ", second, StringComparison.Ordinal);
        Assert.False(HasHeader(second, "Authorization"), second);
        Assert.Contains("\r\nContent-Language: de\r\n", second, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A no-cors fetch() with a method that is not CORS-safelisted rejects with a
    /// TypeError and never reaches the server; the op refuses it too.
    /// </summary>
    [Fact]
    public async Task NoCorsPutRejectsWithoutReachingTheServer()
    {
        using var server = new RawHttpServer(_ => OkCorsResponse);
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const viaFetch = await fetch("{{server.Origin}}/put", { mode: "no-cors", method: "PUT" })
                    .then(() => "resolved", e => e.constructor.name);
                const viaOp = await Promise.resolve(__obscura_test_ops.op_fetch_url(
                    "{{server.Origin}}/put-op", "PUT", "{}", new Uint8Array(0), "http://example.com",
                    "no-cors", "same-origin", false)).then(() => "resolved", () => "rejected");
                return { viaFetch, viaOp };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        AssertJsonEquals("""{ "viaFetch": "TypeError", "viaOp": "rejected" }""", result.Value);
        Assert.Empty(server.Requests);
    }

    /// <summary>
    /// The host-side filter holds when page script's headers reach the op without
    /// the shim's guard: no-cors drops the unsafe names, and forbidden headers are
    /// dropped in every mode. An internal load is left alone.
    /// </summary>
    [Fact]
    public async Task FetchOpFiltersScriptHeadersHostSide()
    {
        using var server = new RawHttpServer(request => request.StartsWith("OPTIONS ", StringComparison.Ordinal)
            ? "HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\n"
                + "Access-Control-Allow-Headers: x-custom\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            : OkCorsResponse);
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const headers = JSON.stringify({
                    Authorization: "Bearer secret", "X-Custom": "1", Accept: "text/plain",
                    Cookie: "evil=1", Host: "evil.test", Origin: "http://evil.test",
                    "Sec-Fetch-Dest": "document", "Content-Length": "999" });
                const op = (path, mode, internal) => Promise.resolve(__obscura_test_ops.op_fetch_url(
                    "{{server.Origin}}" + path, "GET", headers, new Uint8Array(0), "http://example.com",
                    mode, "same-origin", internal)).catch(() => null);
                await op("/op-no-cors", "no-cors", false);
                await op("/op-cors", "cors", false);
                await op("/op-bogus", "bogus", false);
                return true;
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        var requests = server.Requests;
        var noCors = Assert.Single(requests, r => r.StartsWith("GET /op-no-cors ", StringComparison.Ordinal));
        Assert.False(HasHeader(noCors, "Authorization"), noCors);
        Assert.False(HasHeader(noCors, "X-Custom"), noCors);
        Assert.Contains("\r\nAccept: text/plain\r\n", noCors, StringComparison.OrdinalIgnoreCase);

        // cors keeps Authorization / X-Custom behind the preflight; the preflight here
        // lists only x-custom, so the Authorization request is refused before it goes.
        Assert.DoesNotContain(requests, r => r.StartsWith("GET /op-cors ", StringComparison.Ordinal));
        var preflight = Assert.Single(requests, r => r.StartsWith("OPTIONS /op-cors ", StringComparison.Ordinal));
        Assert.Contains(
            "Access-Control-Request-Headers: authorization,x-custom\r\n",
            preflight,
            StringComparison.OrdinalIgnoreCase);

        // An unknown mode is cors, not a CORS-free request.
        Assert.Single(requests, r => r.StartsWith("OPTIONS /op-bogus ", StringComparison.Ordinal));

        foreach (var request in requests)
        {
            Assert.False(HasHeader(request, "Cookie"), request);
            Assert.False(HasHeader(request, "Sec-Fetch-Dest"), request);
            Assert.DoesNotContain("evil.test", request, StringComparison.Ordinal);
            Assert.DoesNotContain("Content-Length: 999", request, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Forbidden headers never reach the wire from fetch() in cors mode or from XHR,
    /// while an allowed custom header does (after the preflight admits it).
    /// </summary>
    [Fact]
    public async Task ForbiddenHeadersNeverReachTheWire()
    {
        using var server = new RawHttpServer(request => request.StartsWith("OPTIONS ", StringComparison.Ordinal)
            ? "HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\n"
                + "Access-Control-Allow-Headers: x-custom\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
            : OkCorsResponse);
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const headers = { "X-Custom": "1", Cookie: "evil=1", Host: "evil.test",
                    Origin: "http://evil.test", "Sec-Fetch-Site": "none", "Proxy-Foo": "1",
                    Referer: "http://evil.test/" };
                const viaFetch = await (await fetch("{{server.Origin}}/fetch", { headers })).text();
                const viaXhr = await new Promise(resolve => {
                    const xhr = new XMLHttpRequest();
                    xhr.onload = () => resolve(xhr.responseText);
                    xhr.onerror = () => resolve("error");
                    xhr.open("GET", "{{server.Origin}}/xhr");
                    for (const [k, v] of Object.entries(headers)) xhr.setRequestHeader(k, v);
                    xhr.send();
                });
                return { viaFetch, viaXhr };
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        AssertJsonEquals("""{ "viaFetch": "ok", "viaXhr": "ok" }""", result.Value);

        var requests = server.Requests;
        foreach (var path in new[] { "/fetch", "/xhr" })
        {
            var sent = Assert.Single(requests, r => r.StartsWith("GET " + path + " ", StringComparison.Ordinal));
            Assert.Contains("\r\nX-Custom: 1\r\n", sent, StringComparison.OrdinalIgnoreCase);
            Assert.False(HasHeader(sent, "Cookie"), sent);
            Assert.False(HasHeader(sent, "Sec-Fetch-Site"), sent);
            Assert.False(HasHeader(sent, "Proxy-Foo"), sent);
            // The page's own Referer goes out (strict-origin-when-cross-origin), never the
            // one script set.
            Assert.Contains("\r\nReferer: http://example.com/\r\n", sent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("evil.test", sent, StringComparison.Ordinal);
        }

        foreach (var request in requests)
        {
            if (request.StartsWith("OPTIONS ", StringComparison.Ordinal))
            {
                Assert.Contains("Access-Control-Request-Headers: x-custom\r\n", request, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// <c>mode: "same-origin"</c> to another origin is a network error, not a request
    /// whose response script can read unfiltered.
    /// </summary>
    [Fact]
    public async Task SameOriginModeRefusesCrossOriginFetch()
    {
        using var server = new RawHttpServer(_ =>
            "HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nsecret");
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => await fetch("{{server.Origin}}/same-origin", { mode: "same-origin" })
                .then(r => r.text(), e => e.constructor.name)
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("TypeError", result.Value!.GetValue<string>());
        Assert.Empty(server.Requests);
    }
}
