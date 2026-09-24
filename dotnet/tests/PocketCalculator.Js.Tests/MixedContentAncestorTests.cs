using PocketCalculator.Js.Ops;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// SECURITY.md I7 leftovers: mixed content follows the ancestor chain (a srcdoc,
/// about:blank or http frame inside an https page is still checked, as Chromium checks
/// the top frame), and <c>new WebSocket("ws:...")</c> from a secure context throws
/// SecurityError, as in Chromium.
/// </summary>
public sealed class MixedContentAncestorTests
{
    private const string Blocked =
        "SecurityError: Failed to construct 'WebSocket': An insecure WebSocket connection may not be initiated from a page loaded over HTTPS.";

    private const string TryWebSocket =
        "(() => { try { new WebSocket('ws://insecure.example/s'); return 'ok'; } catch (e) { return e.name + ': ' + e.message; } })()";

    [Fact]
    public void FramesInheritTheNearestSecureAncestor()
    {
        using var page = RuntimeFixture.Page("https://secure.example/page", "<html><body></body></html>");
        using var srcdoc = FrameRealm.Create(page.Runtime, 1, 0, "about:srcdoc", "<html><body></body></html>");
        using var http = FrameRealm.Create(page.Runtime, 2, 0, "http://plain.example/f", "<html><body></body></html>");
        using var nested = FrameRealm.Create(page.Runtime, 3, 2, "about:blank", "<html><body></body></html>");
        Assert.NotNull(srcdoc);
        Assert.NotNull(http);
        Assert.NotNull(nested);

        Assert.Equal("https://secure.example/page", srcdoc.State.SecureAncestorUrl);
        Assert.Equal("https://secure.example/page", http.State.SecureAncestorUrl);
        Assert.Equal("https://secure.example/page", nested.State.SecureAncestorUrl);
        Assert.Equal("https://secure.example/page", FetchOps.MixedContentContext(srcdoc.State)!.AbsoluteUri);
        Assert.Equal("https://secure.example/page", FetchOps.MixedContentContext(nested.State)!.AbsoluteUri);
    }

    [Fact]
    public void InsecureWebSocketIsRefusedInSecureContextsOnly()
    {
        using var secure = RuntimeFixture.Page("https://secure.example/page", "<html><body></body></html>");
        Assert.Equal(Blocked, secure.Runtime.Evaluate(TryWebSocket)!.GetValue<string>());
        Assert.Equal(
            "ok",
            secure.Runtime.Evaluate(
                "(() => { new WebSocket('wss://secure.example/s'); new WebSocket('ws://127.0.0.1:9/s');"
                + " new WebSocket('ws://localhost/s'); return 'ok'; })()")!.GetValue<string>());

        using var frame = FrameRealm.Create(secure.Runtime, 1, 0, "about:srcdoc", "<html><body></body></html>");
        Assert.NotNull(frame);
        Assert.Equal(Blocked, frame.Evaluate(TryWebSocket)!.GetValue<string>());

        using var plain = RuntimeFixture.Page("http://plain.example/page", "<html><body></body></html>");
        Assert.Equal("ok", plain.Runtime.Evaluate(TryWebSocket)!.GetValue<string>());
    }
}
