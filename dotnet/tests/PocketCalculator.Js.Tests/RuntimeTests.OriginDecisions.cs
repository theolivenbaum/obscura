using PocketCalculator.Dom;
using PocketCalculator.Net;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Origin decisions the host makes rather than the shim (SECURITY.md C1, C2, C3, H4, L9):
/// a page that replaces <c>URL</c>, <c>JSON.parse</c> or an iframe expando must not
/// change which origin a request, a message or a frame belongs to.
/// </summary>
public sealed partial class RuntimeTests
{
    /// <summary>
    /// C1: a page replacing <c>window.URL</c> so its document URL reports the target's
    /// origin must still fetch as its own origin: no cookies, and the CORS check fails.
    /// </summary>
    [Fact]
    public async Task FetchOriginIsTheDocumentsNotThePageComputedOne()
    {
        string? victimOrigin = null;
        using var server = new RawHttpServer(request =>
        {
            // Only the victim's own origin is allowed to read, with credentials.
            return "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n"
                + $"Access-Control-Allow-Origin: {victimOrigin}\r\n"
                + "Access-Control-Allow-Credentials: true\r\n"
                + "Content-Length: 6\r\nConnection: close\r\n\r\nsecret";
        });
        victimOrigin = server.Origin;
        var jar = new CookieJar();
        jar.SetCookie("sid=victim-session; Path=/", new Uri(server.Origin + "/"));
        using var fixture = RuntimeFixture.Blank();
        var runtime = fixture.Runtime;
        runtime.SetDom(HtmlParsing.ParseHtml("<html><body></body></html>"));
        runtime.SetUrl("http://attacker.example/page");
        runtime.SetHttpClient(new PocketCalculatorHttpClient(jar, null, allowPrivateNetwork: true));
        runtime.RunPageInit();

        var result = await runtime.CallFunctionOnForCdpAsync(
            $$"""
            async () => {
                const Real = URL;
                globalThis.URL = class extends Real {
                    get origin() { return "{{server.Origin}}"; }
                };
                try {
                    const r = await fetch("{{server.Origin}}/account");
                    return "read:" + await r.text();
                } catch (e) {
                    return "blocked";
                }
            }
            """,
            null,
            [],
            returnByValue: true,
            awaitPromise: true);

        Assert.Equal("blocked", result.Value!.GetValue<string>());
        var request = Assert.Single(server.Requests);
        Assert.DoesNotContain("victim-session", request, StringComparison.Ordinal);
        Assert.Contains("Origin: http://attacker.example\r\n", request, StringComparison.OrdinalIgnoreCase);
    }
}
