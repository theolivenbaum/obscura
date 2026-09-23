using System.Net.WebSockets;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// End-to-end checks of the CDP control-plane gate from upstream 04418a5 against
/// a live server: browser callers, rebound hosts, the bearer token, and an
/// oversized request head, on both the discovery endpoints and the WebSocket
/// upgrade.
/// </summary>
public sealed class ControlPlaneGuardsTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string Get(int port, string path, string extraHeaders = "", string? host = null) =>
        $"GET {path} HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{port}"}\r\n{extraHeaders}Connection: close\r\n\r\n";

    private static string Upgrade(int port, string extraHeaders = "") =>
        $"GET /devtools/browser HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{extraHeaders}"
        + "Upgrade: websocket\r\nConnection: Upgrade\r\n"
        + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";

    [Fact]
    public async Task NativeClientWithoutTokenIsServedOnLoopback()
    {
        await using var server = await CdpServerHandle.StartAsync();
        var version = await CdpTestClient.HttpGetAsync(server.Port, "/json/version", Timeout);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", version, StringComparison.Ordinal);

        using var ws = await CdpTestClient.ConnectAsync(server.Port);
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task BrowserOriginIsRefusedOnDiscoveryAndUpgrade()
    {
        await using var server = await CdpServerHandle.StartAsync();
        const string expected = "HTTP/1.1 403 Forbidden\r\nContent-Type: application/json\r\n"
            + "Content-Length: 27\r\nConnection: close\r\n\r\n{\"error\":\"request refused\"}";

        var discovery = await CdpTestClient.HttpRawAsync(
            server.Port, Get(server.Port, "/json/version", "Origin: https://evil.example\r\n"), Timeout);
        Assert.Equal(expected, discovery);

        var upgrade = await CdpTestClient.HttpRawAsync(
            server.Port, Upgrade(server.Port, "Origin: https://evil.example\r\n"), Timeout);
        Assert.Equal(expected, upgrade);
    }

    [Fact]
    public async Task ReboundHostIsRefused()
    {
        await using var server = await CdpServerHandle.StartAsync();
        var response = await CdpTestClient.HttpRawAsync(
            server.Port, Get(server.Port, "/json/list", host: $"rebind.example:{server.Port}"), Timeout);
        Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", response, StringComparison.Ordinal);
        Assert.EndsWith("{\"error\":\"request refused\"}", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredTokenIsRequiredOnDiscoveryAndUpgrade()
    {
        await using var server = await CdpServerHandle.StartWithTokenAsync(Token);
        const string unauthorized = "HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\n"
            + "Content-Length: 35\r\nConnection: close\r\n\r\n{\"error\":\"authentication required\"}";

        Assert.Equal(
            unauthorized,
            await CdpTestClient.HttpRawAsync(server.Port, Get(server.Port, "/json/version"), Timeout));
        Assert.Equal(
            unauthorized,
            await CdpTestClient.HttpRawAsync(
                server.Port, Get(server.Port, "/json/version", "Authorization: Bearer wrong\r\n"), Timeout));
        Assert.Equal(
            unauthorized,
            await CdpTestClient.HttpRawAsync(server.Port, Upgrade(server.Port), Timeout));

        var authorized = await CdpTestClient.HttpRawAsync(
            server.Port, Get(server.Port, "/json/version", $"Authorization: Bearer {Token}\r\n"), Timeout);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", authorized, StringComparison.Ordinal);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/devtools/browser"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    /// <summary>
    /// Upstream a156914: a loopback worker behind a balancer on 0.0.0.0 serves the
    /// DNS name a client used to reach the balancer, advertises it back, and still
    /// demands the token.
    /// </summary>
    [Fact]
    public async Task ForwardedWorkerServesAndAdvertisesTheBalancerDnsName()
    {
        await using var server = await CdpServerHandle.StartForwardedAsync(
            Token, new CdpServer.ForwardedAuthority(System.Net.IPAddress.Any, 9222));
        const string host = "cdp.example.test:9222";

        var unauthorized = await CdpTestClient.HttpRawAsync(
            server.Port, Get(server.Port, "/json/version", host: host), Timeout);
        Assert.StartsWith("HTTP/1.1 401 Unauthorized\r\n", unauthorized, StringComparison.Ordinal);

        var auth = $"Authorization: Bearer {Token}\r\n";
        var version = await CdpTestClient.HttpRawAsync(
            server.Port, Get(server.Port, "/json/version", auth, host), Timeout);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", version, StringComparison.Ordinal);
        Assert.Contains("\"webSocketDebuggerUrl\": \"ws://cdp.example.test:9222/devtools/browser\"", version, StringComparison.Ordinal);

        var list = await CdpTestClient.HttpRawAsync(server.Port, Get(server.Port, "/json/list", auth, host), Timeout);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", list, StringComparison.Ordinal);
        Assert.Contains("ws://cdp.example.test:9222/devtools/", list, StringComparison.Ordinal);

        // With no Host the worker advertises the balancer's port, not its own.
        var noHost = await CdpTestClient.HttpRawAsync(
            server.Port, $"GET /json/version HTTP/1.0\r\n{auth}\r\n", Timeout);
        Assert.Contains("\"webSocketDebuggerUrl\": \"ws://127.0.0.1:9222/devtools/browser\"", noHost, StringComparison.Ordinal);
    }

    /// <summary>A loopback forwarded authority adds no DNS name to what a worker serves.</summary>
    [Fact]
    public async Task LoopbackForwardedWorkerStillRefusesAReboundHost()
    {
        await using var server = await CdpServerHandle.StartForwardedAsync(
            null, new CdpServer.ForwardedAuthority(System.Net.IPAddress.Loopback, 9222));
        var response = await CdpTestClient.HttpRawAsync(
            server.Port, Get(server.Port, "/json/version", host: "rebind.example:9222"), Timeout);
        Assert.StartsWith("HTTP/1.1 403 Forbidden\r\n", response, StringComparison.Ordinal);

        var loopback = await CdpTestClient.HttpRawAsync(
            server.Port, Get(server.Port, "/json/version", host: "127.0.0.1:9222"), Timeout);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", loopback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedRequestHeadIsRefused()
    {
        await using var server = await CdpServerHandle.StartAsync();
        var padding = "X-Padding: " + new string('a', 8192) + "\r\n";
        var response = await CdpTestClient.HttpRawAsync(
            server.Port, Get(server.Port, "/json/version", padding), Timeout);
        Assert.Equal(
            "HTTP/1.1 431 Request Header Fields Too Large\r\nContent-Type: application/json\r\n"
            + "Content-Length: 34\r\nConnection: close\r\n\r\n{\"error\":\"request head too large\"}",
            response);
    }
}
