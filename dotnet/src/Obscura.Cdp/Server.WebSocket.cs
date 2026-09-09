using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Obscura.Cdp;

public static partial class CdpServer
{
    private const int MaxHandshakeBytes = 16 * 1024;
    private const int MaxFrameBytes = 64 << 20;

    /// <summary>
    /// Complete the WebSocket upgrade and pump frames between the socket and this
    /// connection's processor.
    /// </summary>
    /// <remarks>
    /// The reference server disables the WebSocket write buffer: CDP traffic is
    /// many small (~100-byte) frames, and buffering adds latency per frame. .NET's
    /// <see cref="WebSocket"/> writes straight through to the stream it is given,
    /// so pairing it with an unbuffered <c>NetworkStream</c> and
    /// <c>NoDelay</c> gets the same per-frame latency.
    /// </remarks>
    internal static async Task HandleConnectionWsAsync(
        Stream stream,
        ChannelWriter<ServerMessage> msgTx)
    {
        var key = await ReadHandshakeAsync(stream).ConfigureAwait(false);
        var accept = ComputeWebSocketAccept(key);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);

        using var ws = WebSocket.CreateFromStream(
            stream,
            new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        CdpLog.Info("WebSocket connected");

        var replies = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });

        msgTx.TryWrite(new ServerMessage.NewConnection(replies.Writer));
        if (await replies.Reader.WaitToReadAsync().ConfigureAwait(false) &&
            replies.Reader.TryRead(out var init))
        {
            CdpLog.Debug($"Connection init: {ServerSupport.Utf8Preview(init, 100)}");
        }

        using var sendStop = new CancellationTokenSource();
        var sendTask = SendLoopAsync(ws, replies.Reader, sendStop.Token);

        try
        {
            var buffer = new byte[16 * 1024];
            var frame = new List<byte>(1024);
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
                {
                    CdpLog.Warn($"WS read error: {e.Message}");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    CdpLog.Info("WS closed by client");
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    // Binary and control frames are ignored, as they are upstream.
                    if (result.EndOfMessage)
                    {
                        frame.Clear();
                    }

                    continue;
                }

                if (frame.Count + result.Count > MaxFrameBytes)
                {
                    CdpLog.Warn("WS read error: frame exceeds the maximum message size");
                    break;
                }

                frame.AddRange(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(frame.ToArray());
                frame.Clear();

                if (text.Contains("\"Browser.close\"", StringComparison.Ordinal))
                {
                    if (CdpRequest.TryParse(text) is { } close)
                    {
                        replies.Writer.TryWrite(
                            CdpResponse.Success(close.Id, new JsonObject(), null).ToJson());
                    }

                    break;
                }

                if (ServerSupport.FastPathResponse(text) is { } fast)
                {
                    replies.Writer.TryWrite(fast);
                }
                else
                {
                    msgTx.TryWrite(new ServerMessage.Cdp(text, replies.Writer));
                }
            }
        }
        finally
        {
            // Give the writer a moment to flush the last response (a Browser.close
            // reply, or the events a final command produced) before tearing the
            // socket down, then stop it the way `send_task.abort()` does.
            await FlushRepliesAsync(replies, sendTask).ConfigureAwait(false);
            await sendStop.CancelAsync().ConfigureAwait(false);
            try
            {
                await sendTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task FlushRepliesAsync(Channel<string> replies, Task sendTask)
    {
        var deadline = Environment.TickCount64 + 250;
        while (replies.Reader.Count > 0 && !sendTask.IsCompleted &&
               Environment.TickCount64 < deadline)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static async Task SendLoopAsync(
        WebSocket ws,
        ChannelReader<string> replies,
        CancellationToken stop)
    {
        try
        {
            while (await replies.WaitToReadAsync(stop).ConfigureAwait(false))
            {
                while (replies.TryRead(out var message))
                {
                    if (message.Contains("\"__init\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        await ws.SendAsync(
                                Encoding.UTF8.GetBytes(message),
                                WebSocketMessageType.Text,
                                endOfMessage: true,
                                stop)
                            .ConfigureAwait(false);
                    }
                    catch (Exception e)
                        when (e is WebSocketException or IOException or ObjectDisposedException
                                  or OperationCanceledException or InvalidOperationException)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>
    /// Read the upgrade request head and return its <c>Sec-WebSocket-Key</c>.
    /// </summary>
    /// <remarks>
    /// The accept thread only peeked at these bytes, so they are still queued on
    /// the socket and have to be consumed here before the 101 goes out.
    /// </remarks>
    private static async Task<string> ReadHandshakeAsync(Stream stream)
    {
        var buffer = new byte[MaxHandshakeBytes];
        var received = 0;
        var headerEnd = -1;
        while (received < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(received)).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            received += n;
            headerEnd = buffer.AsSpan(0, received).IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd < 0)
        {
            throw new WebSocketExceptionShim("WebSocket handshake head never terminated");
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        foreach (var line in head.Split("\r\n"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            if (line.AsSpan(0, colon).Trim()
                .Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        throw new WebSocketExceptionShim("WebSocket handshake carried no Sec-WebSocket-Key");
    }
}
