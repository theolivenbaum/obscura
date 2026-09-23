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
}
