using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The runtime.rs tests that came with the upstream fetch()/XHR security fixes
/// (04f0475, ebe5973, 05846de, 4778192, 04418a5 part C). They sit in their own
/// file of the same class so the port of each fix stays in one place.
/// </summary>
public sealed partial class RuntimeTests
{
    /// <summary>
    /// Upstream 04f0475, <c>denied_preflight_never_sends_the_unsafe_request</c>: the
    /// preflight allows only <c>content-type</c>, so a DELETE with an Authorization
    /// header must reject, and the DELETE itself must never reach the server.
    /// </summary>
    [Fact]
    public async Task DeniedPreflightNeverSendsTheUnsafeRequest()
    {
        using var server = new RawHttpServer(request => request.StartsWith("OPTIONS ", StringComparison.Ordinal)
            ? "HTTP/1.1 204 No Content\r\n"
                + "Access-Control-Allow-Origin: *\r\n"
                + "Access-Control-Allow-Headers: content-type\r\n"
                + "Content-Length: 0\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\n"
                + "Content-Length: 2\r\nConnection: close\r\n\r\nok");
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => await fetch("{{server.Origin}}/resource", {
                method: "DELETE",
                headers: { "Authorization": "Bearer test" },
            }).then(() => "resolved", () => "rejected")
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("rejected", result.Value!.GetValue<string>());

        var requests = server.Requests;
        Assert.Single(requests);
        Assert.StartsWith("OPTIONS /resource ", requests[0], StringComparison.Ordinal);
        Assert.Contains("Access-Control-Request-Headers: authorization\r\n", requests[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The allowed counterpart: a preflight that lists the method and the header lets
    /// the request through, and the request carries the header.
    /// </summary>
    [Fact]
    public async Task AllowedPreflightSendsTheRequest()
    {
        using var server = new RawHttpServer(request => request.StartsWith("OPTIONS ", StringComparison.Ordinal)
            ? "HTTP/1.1 204 No Content\r\n"
                + "Access-Control-Allow-Origin: *\r\n"
                + "Access-Control-Allow-Methods: DELETE\r\n"
                + "Access-Control-Allow-Headers: x-trace\r\n"
                + "Content-Length: 0\r\nConnection: close\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\n"
                + "Content-Length: 2\r\nConnection: close\r\n\r\nok");
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => (await fetch("{{server.Origin}}/resource", {
                method: "DELETE",
                headers: { "X-Trace": "1" },
            })).text()
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("ok", result.Value!.GetValue<string>());
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        Assert.StartsWith("DELETE /resource ", requests[1], StringComparison.Ordinal);
        Assert.Contains("X-Trace: 1\r\n", requests[1], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Upstream 4778192 (#940), <c>queued_navigation_does_not_expose_another_origins_cookies</c>:
    /// a queued navigation must not move document.cookie's jar scope before it commits.
    /// </summary>
    [Fact]
    public void QueuedNavigationDoesNotExposeAnotherOriginsCookies()
    {
        using var fixture = SetupRuntimeWithCookies("<html><body></body></html>", out var jar);
        var rt = fixture.Runtime;
        jar.SetCookie("secret=victimtoken; Path=/", new Uri("https://victim.example/"));

        rt.Evaluate("location.href = 'https://victim.example/'");
        rt.Evaluate("document.cookie = 'planted=1; Path=/'");
        var cookies = rt.Evaluate("document.cookie")!.GetValue<string>();

        Assert.DoesNotContain("victimtoken", cookies, StringComparison.Ordinal);
        Assert.DoesNotContain("planted", jar.GetCookieHeader(new Uri("https://victim.example/")), StringComparison.Ordinal);
    }

    /// <summary>
    /// Upstream 05846de (#973), <c>cors_mode_blocks_unauthorized_cross_origin_redirect_hop</c>:
    /// in cors mode a cross-origin 302 without Access-Control-Allow-Origin is rejected
    /// before it is followed, even though the final response would allow the request.
    /// </summary>
    [Fact]
    public async Task CorsModeBlocksUnauthorizedCrossOriginRedirectHop()
    {
        using var redirector = new RawHttpServer(request => request.Contains("/final", StringComparison.Ordinal)
            ? "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"
            : "HTTP/1.1 302 Found\r\nLocation: /final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                try {
                    await fetch("{{redirector.Origin}}/start", { mode: "cors" });
                    return "allowed";
                } catch (e) {
                    return "blocked: " + e.message;
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        var value = result.Value!.GetValue<string>();
        Assert.StartsWith("blocked: ", value, StringComparison.Ordinal);
        Assert.Contains("cross-origin redirect from", value, StringComparison.Ordinal);
        Assert.Single(redirector.Requests);
    }

    /// <summary>
    /// Upstream 05846de (#973): a non-ok preflight reports its HTTP status, not a
    /// missing Access-Control-Allow-Origin.
    /// </summary>
    [Fact]
    public async Task NonOkPreflightReportsItsStatusFirst()
    {
        using var server = new RawHttpServer(
            _ => "HTTP/1.1 404 Nope\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = RedirectRuntimeForOrigin("http://example.com");
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => fetch("{{server.Origin}}/r", { method: "DELETE" })
                .then(() => "resolved", e => String(e && e.message))
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        Assert.Contains("CORS preflight returned HTTP 404 Not Found", result.Value!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Upstream ebe5973 (#967), end to end: a same-origin request with an
    /// Authorization header that 302s to another origin reaches the target without
    /// it. Chromium strips Authorization on a cross-origin redirect.
    /// </summary>
    [Fact]
    public async Task CrossOriginRedirectDropsAuthorization()
    {
        using var target = new RawHttpServer(
            _ => "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        using var source = new RawHttpServer(
            _ => $"HTTP/1.1 302 Found\r\nLocation: {target.Origin}/final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var fixture = RedirectRuntimeForOrigin(source.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => await fetch("/start", {
                headers: { "Authorization": "Bearer secret", "X-Keep": "yes" },
            }).then(() => "resolved", () => "rejected")
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        Assert.NotNull(result.Value);

        Assert.Contains("Authorization: Bearer secret", source.Requests[0], StringComparison.OrdinalIgnoreCase);
        var hop = Assert.Single(target.Requests);
        Assert.DoesNotContain("authorization:", hop, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Keep: yes", hop, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deviation from Rust (which downgrades every 301/302/303): a PUT that 302s
    /// keeps its method, body and Content-Type, as in Chromium.
    /// </summary>
    [Fact]
    public async Task PutThatRedirectsWith302KeepsItsMethodAndBody()
    {
        using var server = new RawHttpServer(request =>
            RawHttpServer.RequestPath(request) == "/start"
                ? "HTTP/1.1 302 Found\r\nLocation: /final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        using var fixture = RedirectRuntimeForOrigin(server.Origin);
        var result = await fixture.Runtime.CallFunctionOnForCdpAsync(
            """
            async () => (await fetch("/start", {
                method: "PUT",
                headers: { "Content-Type": "text/plain" },
                body: "payload",
            })).text()
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);
        Assert.Equal("ok", result.Value!.GetValue<string>());
        var requests = server.Requests;
        Assert.Equal(2, requests.Count);
        Assert.StartsWith("PUT /final ", requests[1], StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain", requests[1], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("payload", requests[1], StringComparison.Ordinal);
    }
}
