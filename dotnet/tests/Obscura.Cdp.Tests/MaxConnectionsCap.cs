using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-cdp/tests/max_connections_cap.rs</c>:
/// <c>--max-connections</c> bounds the thread-per-connection server.
/// </summary>
/// <remarks>
/// <para>
/// Each CDP connection owns an OS thread and its pages' V8 isolates, so without a
/// cap a client can grow the server's thread count and memory without limit. The
/// cap must do three things, and this test pins all three: connections up to the
/// limit are accepted and usable; the one past the limit is refused with an
/// explicit 503 carrying <c>X-Obscura-Reason: max-connections</c>, not dropped
/// with a bare reset; and closing a connection frees its slot, so the server
/// recovers rather than wedging shut once it has ever been full.
/// </para>
/// <para>
/// The third is the one that actually bites: a slot leaked on any
/// connection-teardown path would only show up as a server that stops accepting
/// after N lifetime connections, which no other test would catch.
/// </para>
/// </remarks>
// Drives a live CDP server and its V8 isolates; joins the serial collection so
// it does not compete with the rest of the suite on a loaded host.
[Collection(CdpDomainCollection.Name)]
public sealed class MaxConnectionsCapTests
{
    private const int Limit = 2;

    /// <summary>
    /// A CDP connection that is actually driven, not just opened:
    /// <c>Target.getTargets</c> round-trips through the connection's own
    /// processor, proving the connection holds a live slot rather than a socket
    /// the server has forgotten about.
    /// </summary>
    private static async Task<ClientWebSocket> OpenAndUseAsync(int port, ulong id)
    {
        var ws = await CdpTestClient.ConnectAsync(port);
        try
        {
            await CdpTestClient.SendAsync(
                ws, new JsonObject { ["id"] = id, ["method"] = "Target.getTargets" });
            await CdpTestClient.AwaitResponseAsync(ws, id, TimeSpan.FromSeconds(10));
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    [Fact]
    public async Task MaxConnectionsRefusesThenRecovers()
    {
        await using var server = await CdpServerHandle.StartAsync(Limit);

        // 1. Fill the server to its limit.
        List<ClientWebSocket> held = [];
        try
        {
            for (var i = 0; i < Limit; i++)
            {
                held.Add(await OpenAndUseAsync(server.Port, 100UL + (ulong)i));
            }

            // 2. One past the limit is refused, and says why.
            var refused = await CdpTestClient.RawHandshakeStatusAsync(server.Port);
            Assert.StartsWith("HTTP/1.1 503", refused, StringComparison.Ordinal);
            Assert.Contains("X-Obscura-Reason: max-connections", refused, StringComparison.Ordinal);

            // 3. Free one slot and confirm the server accepts again. Closing is
            //    asynchronous (the connection thread unwinds and releases its
            //    slot), so poll rather than assume an instant handover.
            var freed = held[^1];
            held.RemoveAt(held.Count - 1);
            try
            {
                await freed.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }

            freed.Dispose();

            ClientWebSocket? reconnected = null;
            for (var attempt = 0; attempt < 40 && reconnected is null; attempt++)
            {
                await Task.Delay(100);
                try
                {
                    reconnected = await OpenAndUseAsync(server.Port, 900);
                }
                catch (Exception e) when (e is WebSocketException or IOException or TimeoutException
                                              or OperationCanceledException)
                {
                }
            }

            Assert.True(
                reconnected is not null,
                "a closed connection must release its slot; the server is wedged at the cap");
            held.Add(reconnected!);
        }
        finally
        {
            foreach (var ws in held)
            {
                ws.Dispose();
            }
        }
    }
}
