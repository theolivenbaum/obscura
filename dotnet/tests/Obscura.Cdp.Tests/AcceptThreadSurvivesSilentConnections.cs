using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of
/// <c>crates/obscura-cdp/tests/accept_thread_survives_silent_connections.rs</c>:
/// the accept thread must survive connections that never send a request head.
/// </summary>
/// <remarks>
/// <para>
/// The accept thread is one OS thread shared by every client. Before issue #715
/// it classified each fresh connection with a <em>blocking</em> peek, so a single
/// connection that was accepted but never sent anything (speculative browser
/// preconnect, port probe, stalled client) parked the thread forever. Every later
/// connection then sat in the kernel backlog with no 101 and no error until its
/// own connect timeout: Playwright's <c>connectOverCDP</c> hangs exactly like
/// that while a raw WebSocket client appears to work, since it only works as long
/// as it connects while no silent connection is parked.
/// </para>
/// <para>
/// This test parks several silent connections and then requires that a real CDP
/// WebSocket handshake plus a <c>Target.getTargets</c> round-trip still completes
/// promptly, and that the <c>/json/version</c> control plane, served by the same
/// thread, stays reachable too.
/// </para>
/// </remarks>
// Drives a live CDP server and its V8 isolates; joins the serial collection so
// it does not compete with the rest of the suite on a loaded host.
[Collection(CdpDomainCollection.Name)]
public sealed class AcceptThreadSurvivesSilentConnectionsTests
{
    private const int SilentConnections = 4;

    [Fact]
    public async Task AcceptThreadSurvivesSilentConnections()
    {
        await using var server = await CdpServerHandle.StartAsync();

        // Park connections that connect and then never send a byte. They must be
        // held by the server without occupying its attention.
        List<TcpClient> silent = [];
        try
        {
            for (var i = 0; i < SilentConnections; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, server.Port);
                silent.Add(client);
            }

            // Give the accept thread a moment to pick them up before the real
            // clients arrive, so the test cannot pass by racing the park.
            await Task.Delay(200);

            // 1. A real WebSocket client still gets its handshake and a CDP
            //    round-trip. Before the fix this hung until the timeout because
            //    the accept thread was parked inside a blocking peek.
            var cdp = await Task.Run(async () =>
            {
                using var ws = await CdpTestClient.ConnectAsync(server.Port);
                await CdpTestClient.SendAsync(
                    ws, new JsonObject { ["id"] = 1, ["method"] = "Target.getTargets" });
                var reply = await CdpTestClient.AwaitResponseAsync(ws, 1, TimeSpan.FromSeconds(5));
                Assert.NotNull(reply.Get("result").Get("targetInfos").AsJsonArray());
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(cdp, "CDP handshake wedged behind silent connections");

            // 2. The /json control plane shares the accept thread and must stay
            //    reachable too.
            var body = await CdpTestClient
                .HttpGetAsync(server.Port, "/json/version", TimeSpan.FromSeconds(5))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.StartsWith("HTTP/1.1 200", body, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }
    }
}
