using System.Text;
using System.Threading.Channels;
using Xunit;

namespace PocketCalculator.Cdp.Tests;

/// <summary>
/// SECURITY.md L3: a CDP connection with no traffic stayed open forever. It now closes
/// after <c>POCKETCALCULATOR_CDP_IDLE_TIMEOUT_MS</c> with nothing from the client and no
/// command in flight; a client that pings, or a command still running, keeps it open.
/// </summary>
public sealed class ConnectionIdleTimeoutTests
{
    [Fact]
    public async Task SilentConnectionClosesAfterTheIdleTimeout()
    {
        using var stream = new ScriptedClientStream(pingEvery: null);
        var (connection, _) = await ConnectAsync(stream, TimeSpan.FromMilliseconds(300));

        await connection.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ZeroDisablesTheIdleTimeout()
    {
        using var stream = new ScriptedClientStream(pingEvery: null);
        var (connection, _) = await ConnectAsync(stream, TimeSpan.Zero);

        await Task.Delay(1_000, TestContext.Current.CancellationToken);
        Assert.False(connection.IsCompleted);
    }

    [Fact]
    public async Task ClientPingsKeepTheConnectionOpen()
    {
        using var stream = new ScriptedClientStream(pingEvery: TimeSpan.FromMilliseconds(100));
        var (connection, _) = await ConnectAsync(stream, TimeSpan.FromMilliseconds(600));

        await Task.Delay(1_800, TestContext.Current.CancellationToken);
        Assert.False(connection.IsCompleted, "pings are traffic");

        stream.StopPinging();
        await connection.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CommandInFlightKeepsTheConnectionOpen()
    {
        using var stream = new ScriptedClientStream(pingEvery: null, sendCommand: true);
        var (connection, replies) = await ConnectAsync(stream, TimeSpan.FromMilliseconds(400));

        // The processor has the command and has not answered it yet: three periods on,
        // the connection is still open (the dropped-command backstop is four).
        await Task.Delay(1_200, TestContext.Current.CancellationToken);
        Assert.False(connection.IsCompleted, "a command in flight is not idle");

        // Answering it leaves the connection idle again.
        replies.TryWrite("{\"id\":1,\"result\":{}}");
        await connection.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnparseableMessageDoesNotHoldTheConnectionOpen()
    {
        using var stream = new ScriptedClientStream(pingEvery: null, sendCommand: true);
        var (connection, replies, messages) = await ConnectWithMessagesAsync(stream, TimeSpan.FromMilliseconds(400));
        Assert.IsType<ServerMessage.Cdp>(await messages.ReadAsync(TestContext.Current.CancellationToken));

        // What the processor writes for a message that does not parse.
        replies.TryWrite(ServerSupport.UnansweredMarker);
        await connection.WaitAsync(TimeSpan.FromMilliseconds(1_400), TestContext.Current.CancellationToken);
    }

    private static async Task<(Task Connection, ChannelWriter<string> Replies)> ConnectAsync(
        ScriptedClientStream stream,
        TimeSpan idle)
    {
        var (connection, replies, _) = await ConnectWithMessagesAsync(stream, idle);
        return (connection, replies);
    }

    private static async Task<(Task Connection, ChannelWriter<string> Replies, ChannelReader<ServerMessage> Messages)>
        ConnectWithMessagesAsync(ScriptedClientStream stream, TimeSpan idle)
    {
        var messages = Channel.CreateUnbounded<ServerMessage>();
        var connection = CdpServer.HandleConnectionWsAsync(stream, messages.Writer, CancellationToken.None, idle);
        var announced = await messages.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var replies = Assert.IsType<ServerMessage.NewConnection>(announced).ReplyTx;
        replies.TryWrite("{\"__init\":true}");
        return (connection, replies, messages.Reader);
    }

    /// <summary>
    /// A client that upgrades, then optionally sends one masked text frame (a command)
    /// and masked ping frames on a period; otherwise its reads park. Writes are dropped.
    /// </summary>
    private sealed class ScriptedClientStream(TimeSpan? pingEvery, bool sendCommand = false) : Stream
    {
        private static readonly byte[] Upgrade = Encoding.ASCII.GetBytes(
            "GET /devtools/browser HTTP/1.1\r\nHost: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");

        // FIN + ping, masked, empty payload.
        private static readonly byte[] Ping = [0x89, 0x80, 1, 2, 3, 4];

        private readonly CancellationTokenSource _closed = new();
        private byte[] _pending = Upgrade;
        private int _offset;
        private bool _commandSent = !sendCommand;
        private volatile bool _pinging = pingEvery is not null;

        internal void StopPinging() => _pinging = false;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_offset < _pending.Length)
                {
                    var n = Math.Min(buffer.Length, _pending.Length - _offset);
                    _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
                    _offset += n;
                    return n;
                }

                if (!_commandSent)
                {
                    _commandSent = true;
                    Next(MaskedText("{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":\"1\"}}"));
                    continue;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token, cancellationToken);
                if (_pinging && pingEvery is { } period)
                {
                    await Task.Delay(period, linked.Token).ConfigureAwait(false);
                    if (_pinging)
                    {
                        Next(Ping);
                    }

                    continue;
                }

                await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
                return 0;
            }
        }

        private void Next(byte[] bytes)
        {
            _pending = bytes;
            _offset = 0;
        }

        private static byte[] MaskedText(string text)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            byte[] mask = [9, 8, 7, 6];
            var frame = new byte[2 + 4 + payload.Length];
            frame[0] = 0x81;
            frame[1] = (byte)(0x80 | payload.Length);
            mask.CopyTo(frame, 2);
            for (var i = 0; i < payload.Length; i++)
            {
                frame[6 + i] = (byte)(payload[i] ^ mask[i % 4]);
            }

            return frame;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed.IsCancellationRequested)
            {
                _closed.Cancel();
                _closed.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
