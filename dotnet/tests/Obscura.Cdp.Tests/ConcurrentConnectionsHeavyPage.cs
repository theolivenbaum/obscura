using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of
/// <c>crates/obscura-cdp/tests/concurrent_connections_heavy_page.rs</c>: issue
/// #430, concurrent CDP connections driving a subresource-heavy page must not
/// abort the process.
/// </summary>
/// <remarks>
/// <para>
/// Why this and not the <c>data:</c>-URL concurrency test: the #430 abort needs a
/// page whose event loop is still busy when the navigation settle loop's per-tick
/// timeout fires. A cancelled event-loop turn left an isolate entered on the
/// shared thread; a second connection's script execution then tripped V8's
/// per-thread isolate check and aborted. <c>data:</c> URLs settle instantly (no
/// subresources, no busy loop), so they never drive it. This test serves a local
/// page with SLOW subresources plus a <c>setInterval</c> so the settle loop keeps
/// pumping, then drives it from several independent connections at once.
/// </para>
/// <para>
/// On the single-scheduler server this aborted deterministically. The
/// thread-per-connection server confines each connection's isolates to their own
/// OS thread, so the abort cannot happen and all clients complete.
/// </para>
/// </remarks>
public sealed class ConcurrentConnectionsHeavyPageTests
{
    private const int Clients = 4;

    [Fact]
    public async Task ConcurrentConnectionsHeavyPageDoNotAbortV8()
    {
        // Bind the fixture listener up front so we can hand its port to the
        // clients.
        using var fixture = new HeavyFixtureServer();
        var pageUrl = $"http://127.0.0.1:{fixture.Port}/";

        await using var server = await CdpServerHandle.StartAsync();

        List<Task<string?>> clients = [];
        for (var i = 0; i < Clients; i++)
        {
            var idBase = (ulong)(i + 1) * 1000;
            clients.Add(Task.Run(() => OneClientAsync(server.Port, pageUrl, idBase)));
        }

        var results = await Task.WhenAll(clients);
        List<string> errors = [];
        var ok = 0;
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is { } error)
            {
                errors.Add($"client {i}: {error}");
            }
            else
            {
                ok++;
            }
        }

        Assert.True(
            errors.Count == 0,
            $"clients failed (a V8 abort would instead kill the process): {string.Join("; ", errors)}");
        Assert.Equal(Clients, ok);

        // The fixture must actually have served each client the page and its slow
        // script; that is what keeps the settle loop pumping and makes this a #430
        // repro at all. Without this assertion, pointing the page URL at a dead
        // port still passes, and the test silently degrades into the `data:`-URL
        // one it was written to replace. A healthy run serves three per client -
        // the document, the <img> the render warmup seeds, and /slow.js - so two
        // is a floor that still fails a client whose page never loaded.
        var hits = fixture.Served;
        Assert.True(
            hits >= Clients * 2,
            $"fixture served {hits} requests, expected at least {Clients * 2} ({Clients} clients " +
            "x document + /slow.js): the heavy page never loaded, so this run proves nothing");
    }

    /// <summary>
    /// One client: open its own CDP connection, create a target at the heavy page,
    /// then repeatedly evaluate an awaited promise. Returns an error string on
    /// protocol failure; a V8 abort would take the whole process down instead.
    /// </summary>
    private static async Task<string?> OneClientAsync(int wsPort, string pageUrl, ulong idBase)
    {
        try
        {
            using var ws = await CdpTestClient.ConnectAsync(wsPort);
            await CdpTestClient.SendAsync(ws, new JsonObject
            {
                ["id"] = idBase,
                ["method"] = "Target.createTarget",
                ["params"] = new JsonObject { ["url"] = pageUrl },
            });

            string? sessionId = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                while (sessionId is null)
                {
                    var text = await CdpTestClient.ReceiveTextAsync(ws, deadline.Token)
                               ?? throw new IOException("ws closed");
                    sessionId = CdpJson.Parse(text).Get("params").Get("sessionId").AsString();
                }
            }

            // A few awaited evaluations: each drives promise resolution while the
            // page's setInterval keeps the loop busy, which is the settle and
            // evaluate cancellation path.
            for (ulong k = 0; k < 3; k++)
            {
                var id = idBase + 100 + k;
                await CdpTestClient.SendAsync(ws, new JsonObject
                {
                    ["id"] = id,
                    ["method"] = "Runtime.evaluate",
                    ["sessionId"] = sessionId,
                    ["params"] = new JsonObject
                    {
                        ["expression"] = "new Promise(r => setTimeout(() => r(1 + 1), 40))",
                        ["awaitPromise"] = true,
                        ["returnByValue"] = true,
                    },
                });
                await CdpTestClient.AwaitResponseAsync(ws, id, TimeSpan.FromSeconds(30));
            }

            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Minimal HTTP fixture: a root page with two subresources that respond after
    /// a delay (keeping the navigation settle loop pumping) and an inline
    /// <c>setInterval</c> that keeps the event loop non-idle. Serves every
    /// connection it accepts until the test ends.
    /// </summary>
    private sealed class HeavyFixtureServer : IDisposable
    {
        private const string Body =
            "<!DOCTYPE html><html><head>" +
            "<script src=\"/slow.js\"></script>" +
            "</head><body><h1>heavy</h1><img src=\"/slow.png\">" +
            "<script>setInterval(function(){var x=0;for(var i=0;i<2000;i++){x+=i;}}, 3);</script>" +
            "</body></html>";

        private static readonly byte[] OnePixelPng =
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d,
            0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4, 0x89, 0x00, 0x00, 0x00,
            0x0a, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9c, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0d, 0x0a, 0x2d, 0xb4, 0x00, 0x00, 0x00, 0x00, 0x49,
            0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82,
        ];

        /// <summary>
        /// Serving threads, started before the first connection arrives.
        /// </summary>
        /// <remarks>
        /// Twelve covers this fixture's whole workload - four clients, each fetching
        /// a document and two subresources - so a connection never waits for one.
        /// </remarks>
        private const int Workers = 12;

        private readonly BlockingCollection<TcpClient> _accepted = [];
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private int _served;

        internal HeavyFixtureServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            // Dedicated threads with blocking I/O rather than the thread pool. The
            // fixture serves the page this test is timing, and four clients plus the
            // server already saturate the pool on a small box: a pool-scheduled
            // fixture then adds its own scheduling delay to every subresource, on top
            // of the delay it is supposed to be simulating.
            //
            // Started up front rather than one per connection. The whole assembly runs
            // at the runner's parallelism on a small box, so `new Thread(...).Start()`
            // on the serving path waits for the scheduler to place a brand-new thread
            // among all of them: a connection whose request had already been read sat
            // over twelve seconds before its handler ran (both ends ESTABLISHED, both
            // queues empty). The engine's navigation deadline and this test's client
            // deadline are both 30s, so a subresource delayed like that fails the test
            // rather than merely slowing it.
            for (var i = 0; i < Workers; i++)
            {
                new Thread(Serve)
                {
                    IsBackground = true,
                    Name = "heavy-fixture-connection",
                }.Start();
            }

            new Thread(Accept)
            {
                IsBackground = true,
                Name = "heavy-fixture-accept",
            }.Start();
        }

        internal int Port { get; }

        /// <summary>
        /// Requests answered. The test asserts on it: without that, pointing the
        /// page URL at a dead port still passes, because nothing else here checks
        /// the heavy page ever loaded.
        /// </summary>
        internal int Served => Volatile.Read(ref _served);

        private void Accept()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException
                                              or ObjectDisposedException)
                {
                    return;
                }

                try
                {
                    _accepted.Add(client);
                }
                catch (InvalidOperationException)
                {
                    // The fixture is shutting down.
                    client.Dispose();
                    return;
                }
            }
        }

        /// <summary>One serving thread: take accepted connections until the test ends.</summary>
        private void Serve()
        {
            try
            {
                foreach (var client in _accepted.GetConsumingEnumerable(_stop.Token))
                {
                    ServeOne(client);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException
                                          or InvalidOperationException)
            {
            }
        }

        private void ServeOne(TcpClient client)
        {
            using (client)
            {
                try
                {
                    // A worker is a shared resource now, so a peer that connects and
                    // says nothing must not hold one. Real clients send their head
                    // immediately.
                    client.ReceiveTimeout = 5_000;
                    client.SendTimeout = 5_000;
                    var stream = client.GetStream();

                    // Read the whole head rather than whatever the first read
                    // returned: a request split across segments used to be answered
                    // from a truncated request line, and the leftover bytes then made
                    // the close abortive, which discards the response with it.
                    var buffer = new byte[2048];
                    var received = 0;
                    while (received < buffer.Length)
                    {
                        var read = stream.Read(buffer, received, buffer.Length - received);
                        if (read == 0)
                        {
                            break;
                        }

                        received += read;
                        if (buffer.AsSpan(0, received).IndexOf("\r\n\r\n"u8) >= 0)
                        {
                            break;
                        }
                    }

                    if (received == 0)
                    {
                        return;
                    }

                    Interlocked.Increment(ref _served);
                    var request = Encoding.UTF8.GetString(buffer, 0, received);
                    var parts = request.Split(' ');
                    var path = parts.Length > 1 ? parts[1] : "/";

                    // Slow subresources: respond after a delay so the page's
                    // in-flight request count stays above zero through several
                    // settle ticks, which is what forces the per-tick timeout to
                    // cancel an event-loop turn mid-poll.
                    string contentType;
                    byte[] payload;
                    if (string.Equals(path, "/slow.js", StringComparison.Ordinal))
                    {
                        Thread.Sleep(120);
                        contentType = "application/javascript";
                        payload = "void 0;"u8.ToArray();
                    }
                    else if (string.Equals(path, "/slow.png", StringComparison.Ordinal))
                    {
                        Thread.Sleep(120);
                        contentType = "image/png";
                        payload = OnePixelPng;
                    }
                    else
                    {
                        contentType = "text/html";
                        payload = Encoding.UTF8.GetBytes(Body);
                    }

                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\n" +
                        $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
                catch (Exception e) when (e is IOException or SocketException
                                              or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _accepted.CompleteAdding();
            while (_accepted.TryTake(out var pending))
            {
                pending.Dispose();
            }

            _accepted.Dispose();
            _stop.Dispose();
        }
    }
}
