using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The pieces the Rust integration tests get from <c>tokio-tungstenite</c> and
/// raw <c>tokio::net</c>: a free port, a WebSocket client, and a raw handshake
/// that returns the server's status line instead of an opaque client error.
/// </summary>
internal static class CdpTestClient
{
    /// <summary>
    /// Pick a free port for a test server.
    /// </summary>
    /// <remarks>
    /// There is a small window between releasing the listener here and the
    /// server's own bind a few lines later; under heavy parallelism another
    /// process could take the port in between. The Rust tests accept the same
    /// window.
    /// </remarks>
    internal static int PickPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        probe.Listen(1);
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    internal static async Task<ClientWebSocket> ConnectAsync(int port, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/devtools/browser"), ct)
                .ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    internal static Task SendAsync(WebSocket ws, JsonNode message, CancellationToken ct = default) =>
        ws.SendAsync(
            Encoding.UTF8.GetBytes(CdpJson.Serialize(message)),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);

    /// <summary>Read one complete text frame, or null when the socket closed.</summary>
    internal static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct = default)
    {
        var buffer = new byte[16 * 1024];
        var message = new List<byte>(1024);
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.AddRange(buffer.AsSpan(0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                message.Clear();
                continue;
            }

            return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    /// <summary>Drive the connection until the response with <paramref name="id"/> arrives.</summary>
    internal static async Task<JsonNode?> AwaitResponseAsync(
        WebSocket ws,
        ulong id,
        TimeSpan timeout,
        Action<JsonNode?>? observe = null)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            var text = await ReceiveTextAsync(ws, deadline.Token).ConfigureAwait(false)
                       ?? throw new IOException("ws closed");
            var value = CdpJson.Parse(text);
            observe?.Invoke(value);
            if (value.Get("id").AsU64() == id)
            {
                return value;
            }
        }
    }

    /// <summary>
    /// Raw handshake, returning the server's response head.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ConnectAsync"/>: a refusal is an HTTP response,
    /// and we want to read it rather than have the WebSocket client turn it into
    /// an opaque error.
    /// </remarks>
    internal static async Task<string> RawHandshakeStatusAsync(int port)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        var stream = socket.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"GET /devtools/browser HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
        await stream.WriteAsync(request).ConfigureAwait(false);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[1024];
        var n = await stream.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    /// <summary>Issue one plain HTTP GET and return the whole response.</summary>
    internal static async Task<string> HttpGetAsync(int port, string path, TimeSpan timeout)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        var stream = socket.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request).ConfigureAwait(false);

        using var deadline = new CancellationTokenSource(timeout);
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            int n;
            try
            {
                n = await stream.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (n == 0)
            {
                break;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            if (sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) &&
                sb.Length > 0 &&
                !stream.DataAvailable)
            {
                // The server closes after the body, so one more zero-length read
                // ends the loop; bail early once the body is plainly complete.
                if (sb.ToString().TrimEnd().EndsWith('}') ||
                    sb.ToString().TrimEnd().EndsWith(']'))
                {
                    break;
                }
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Runs a CDP server for the life of the test and stops it deterministically.
/// </summary>
/// <remarks>
/// The Rust tests <c>spawn_local</c> the server and let the LocalSet die with the
/// test. xUnit shares one process across the whole assembly, so a leaked accept
/// thread and its connection threads would outlive their test; this disposes the
/// server instead.
/// </remarks>
internal sealed class CdpServerHandle : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _server;

    private CdpServerHandle(int port, CancellationTokenSource stop, Task server)
    {
        Port = port;
        _stop = stop;
        _server = server;
    }

    internal int Port { get; }

    internal static Task<CdpServerHandle> StartAsync(int maxConnections = CdpServer.DefaultMaxConnections) =>
        StartAsync(CdpTestClient.PickPort(), maxConnections);

    internal static async Task<CdpServerHandle> StartAsync(int port, int maxConnections)
    {
        var stop = new CancellationTokenSource();

        // allow_private_network so the server may fetch 127.0.0.1 fixtures, which
        // is what the Rust integration tests pass too.
        var server = Task.Run(() => CdpServer.StartWithServeOptionsAndLimitAsync(
            port,
            "127.0.0.1",
            null,
            false,
            null,
            false,
            null,
            true,
            maxConnections,
            stop.Token));

        var handle = new CdpServerHandle(port, stop, server);
        await handle.WaitUntilListeningAsync().ConfigureAwait(false);
        return handle;
    }

    private async Task WaitUntilListeningAsync()
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            if (_server.IsFaulted)
            {
                await _server.ConfigureAwait(false);
            }

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"CDP server never bound 127.0.0.1:{Port}");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _server.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
