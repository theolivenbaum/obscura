using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PocketCalculator.Cdp;

public static partial class CdpServer
{
    private const int MaxHandshakeBytes = 16 * 1024;

    /// <summary>The default cap on one inbound message.</summary>
    internal const int DefaultMaxMessageBytes = 16 << 20;

    /// <summary>
    /// The largest inbound message a connection accepts, from
    /// <c>POCKETCALCULATOR_CDP_MAX_MESSAGE_BYTES</c> or <see cref="DefaultMaxMessageBytes"/>.
    /// </summary>
    /// <remarks>
    /// Deviation (SECURITY.md L3): upstream accepts 64 MiB per message, which at
    /// 128 connections is 8 GiB buffered and then copied into a string. The port
    /// defaults to 16 MiB, far above any command a client normally sends, and
    /// the variable raises it for a client that uploads large files as base64
    /// (Playwright's <c>setInputFiles</c> against a remote browser).
    /// </remarks>
    internal static readonly int MaxMessageBytes = ReadMaxMessageBytes();

    private static int ReadMaxMessageBytes()
    {
        var configured = Environment.GetEnvironmentVariable("POCKETCALCULATOR_CDP_MAX_MESSAGE_BYTES");
        return int.TryParse(configured, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : DefaultMaxMessageBytes;
    }

    /// <summary>The default idle timeout: 30 minutes.</summary>
    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a connection may sit with no traffic from its client and no command in
    /// flight before the server closes it: <c>POCKETCALCULATOR_CDP_IDLE_TIMEOUT_MS</c>
    /// (0 disables), or <see cref="DefaultIdleTimeout"/>.
    /// </summary>
    /// <remarks>
    /// Deviation (SECURITY.md L3): upstream keeps a silent connection, and the page and
    /// isolate behind it, open for the life of the process, so a client that connected
    /// and went away without closing held a connection slot and its memory forever.
    /// Any inbound byte counts as traffic, WebSocket ping frames included, and a
    /// command still being processed keeps the connection open, so a long evaluation
    /// or a client that pings is never cut off. Puppeteer and Playwright send no pings
    /// over CDP, hence the generous default. Chromium has no such timeout.
    /// </remarks>
    internal static readonly TimeSpan IdleTimeout = ReadIdleTimeout();

    private static TimeSpan ReadIdleTimeout()
    {
        var configured = Environment.GetEnvironmentVariable("POCKETCALCULATOR_CDP_IDLE_TIMEOUT_MS");
        return long.TryParse(configured, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? TimeSpan.FromMilliseconds(value)
            : DefaultIdleTimeout;
    }

    /// <summary>
    /// Close the connection once <paramref name="activity"/> has been idle for
    /// <paramref name="timeout"/>, by cancelling its read.
    /// </summary>
    private static async Task WatchIdleAsync(
        ConnectionActivity activity,
        TimeSpan timeout,
        CancellationTokenSource closeRead,
        CancellationToken stop)
    {
        var timeoutMs = (long)timeout.TotalMilliseconds;
        var period = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs / 4, 10, 15_000));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(period, stop).ConfigureAwait(false);
                if (activity.IsIdle(timeoutMs))
                {
                    CdpLog.Info($"WS closed: idle for {timeoutMs} ms");
                    await closeRead.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Complete the WebSocket upgrade and pump frames between the socket and this
    /// connection's processor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference server disables the WebSocket write buffer: CDP traffic is
    /// many small (~100-byte) frames, and buffering adds latency per frame. .NET's
    /// <see cref="WebSocket"/> writes straight through to the stream it is given,
    /// so pairing it with an unbuffered <c>NetworkStream</c> and
    /// <c>NoDelay</c> gets the same per-frame latency.
    /// </para>
    /// <para>
    /// <paramref name="processorStopped"/> trips when this connection's processor
    /// has stopped, which is the only thing that ever answers the <c>__init</c>
    /// handshake below.
    /// </para>
    /// </remarks>
    internal static async Task HandleConnectionWsAsync(
        Stream stream,
        ChannelWriter<ServerMessage> msgTx,
        CancellationToken processorStopped,
        TimeSpan? idleTimeout = null)
    {
        var activity = new ConnectionActivity(stream);
        stream = activity;
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

        var replies = new ReplyQueue();

        msgTx.TryWrite(new ServerMessage.NewConnection(replies.Writer));
        try
        {
            if (await replies.Reader.WaitToReadAsync(processorStopped).ConfigureAwait(false) &&
                replies.Reader.TryRead(out var init))
            {
                CdpLog.Debug($"Connection init: {ServerSupport.Utf8Preview(init, 100)}");
            }
        }
        catch (OperationCanceledException)
        {
            // The processor stopped before it could answer, so no `__init` is
            // ever coming and this connection cannot serve anything. Waiting
            // without a token stranded it for the life of the process whenever
            // shutdown was already signalled as the socket was handed off.
            CdpLog.Info("WS closed: the connection's processor stopped before the init handshake");
            return;
        }

        using var sendStop = new CancellationTokenSource();
        var sendTask = SendLoopAsync(ws, replies.Reader, activity, sendStop.Token);

        // The read ends when the client stops taking its replies (ReplyQueue) or the
        // connection has been idle too long.
        using var closeRead = CancellationTokenSource.CreateLinkedTokenSource(replies.Overflowed);
        var idle = idleTimeout ?? IdleTimeout;
        var idleTask = idle > TimeSpan.Zero
            ? WatchIdleAsync(activity, idle, closeRead, sendStop.Token)
            : Task.CompletedTask;

        try
        {
            var buffer = new byte[16 * 1024];
            var frame = new ArrayBufferWriter<byte>(1024);
            while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    // The overflow token ends the read when this client has stopped
                    // taking its replies (ReplyQueue), which closes the connection.
                    result = await ws.ReceiveAsync(buffer.AsMemory(), closeRead.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
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
                        frame.ResetWrittenCount();
                    }

                    continue;
                }

                if ((long)frame.WrittenCount + result.Count > MaxMessageBytes)
                {
                    CdpLog.Warn("WS read error: frame exceeds the maximum message size");
                    break;
                }

                frame.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                // Decoded straight from the frame buffer, not from a copy of it.
                var text = Encoding.UTF8.GetString(frame.WrittenSpan);
                if (frame.Capacity > 1 << 20)
                {
                    // Do not keep a large message's buffer for the life of the connection.
                    frame = new ArrayBufferWriter<byte>(1024);
                }
                else
                {
                    frame.ResetWrittenCount();
                }

                // Deviation: Rust closes the connection on any message whose text
                // contains "Browser.close", a string argument included, and so did
                // the port. The substring is now only a cheap prefilter: the
                // message closes the connection when its parsed method is
                // Browser.close, and anything else takes the ordinary path.
                if (text.Contains("\"Browser.close\"", StringComparison.Ordinal)
                    && CdpRequest.TryParse(text) is { Method: "Browser.close" } close)
                {
                    replies.Writer.TryWrite(
                        CdpResponse.Success(close.Id, new JsonObject(), null).ToJson());
                    break;
                }

                activity.CommandReceived();
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

            await idleTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Let the send loop drain what is already queued before the socket goes
    /// away, and give up after a short grace period.
    /// </summary>
    /// <remarks>
    /// Completing the writer is what ends <see cref="SendLoopAsync"/>: its
    /// <c>WaitToReadAsync</c> answers false once the queue is empty, so the last
    /// response still goes out and the loop then returns on its own. Polling
    /// <c>Reader.Count</c> instead threw <see cref="NotSupportedException"/> on
    /// every single connection - a single-reader unbounded channel does not
    /// count - which aborted the whole teardown below, so the send loop was
    /// never stopped and the flush this method exists for never happened. All
    /// writers use <c>TryWrite</c>, which answers false on a completed channel
    /// rather than throwing, so a processor still finishing a command is
    /// unaffected.
    /// </remarks>
    private static async Task FlushRepliesAsync(ReplyQueue replies, Task sendTask)
    {
        replies.Writer.TryComplete();
        await Task.WhenAny(sendTask, Task.Delay(250)).ConfigureAwait(false);
    }

    private static async Task SendLoopAsync(
        WebSocket ws,
        ChannelReader<string> replies,
        ConnectionActivity activity,
        CancellationToken stop)
    {
        try
        {
            while (await replies.WaitToReadAsync(stop).ConfigureAwait(false))
            {
                while (replies.TryRead(out var message))
                {
                    activity.MessageSent(message);
                    if (ReferenceEquals(message, ServerSupport.UnansweredMarker)
                        || message.Contains("\"__init\"", StringComparison.Ordinal))
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
