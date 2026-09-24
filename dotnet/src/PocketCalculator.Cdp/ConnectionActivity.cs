namespace PocketCalculator.Cdp;

/// <summary>
/// A pass-through stream that records when bytes last moved in each direction, and
/// how many commands a connection has read without answering yet. The idle timeout
/// (<see cref="CdpServer.IdleTimeout"/>) reads it.
/// </summary>
/// <remarks>
/// It sits under the WebSocket, so a client's ping or pong frame counts as inbound
/// traffic even though <see cref="System.Net.WebSockets.WebSocket.ReceiveAsync(Memory{byte}, CancellationToken)"/>
/// never surfaces control frames.
/// </remarks>
internal sealed class ConnectionActivity(Stream inner) : Stream
{
    private long _lastInbound = Environment.TickCount64;
    private long _lastOutbound = Environment.TickCount64;
    private int _inFlight;

    /// <summary>Milliseconds since the client last sent a byte.</summary>
    internal long InboundIdleMs => Environment.TickCount64 - Volatile.Read(ref _lastInbound);

    /// <summary>Milliseconds since the server last wrote a byte.</summary>
    internal long OutboundIdleMs => Environment.TickCount64 - Volatile.Read(ref _lastOutbound);

    /// <summary>Commands read and not yet answered.</summary>
    internal int InFlight => Volatile.Read(ref _inFlight);

    internal void CommandReceived() => Interlocked.Increment(ref _inFlight);

    /// <summary>Called for each outbound message; a response (<c>{"id":</c>) ends a command.</summary>
    internal void MessageSent(string message)
    {
        if ((message.StartsWith("{\"id\":", StringComparison.Ordinal)
                || ReferenceEquals(message, ServerSupport.UnansweredMarker))
            && Interlocked.Decrement(ref _inFlight) < 0)
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <summary>
    /// Whether the connection has been idle for <paramref name="timeoutMs"/>: nothing
    /// from the client for that long, and no command still being processed. A message
    /// that does not parse is answered with <see cref="ServerSupport.UnansweredMarker"/>,
    /// so it does not count; as a backstop against a command the processor dropped, an
    /// unanswered command stops counting once the connection has been silent in both
    /// directions for four periods.
    /// </summary>
    internal bool IsIdle(long timeoutMs) =>
        InboundIdleMs >= timeoutMs
        && (InFlight <= 0 || (InboundIdleMs >= 4 * timeoutMs && OutboundIdleMs >= 4 * timeoutMs));

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => Inbound(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inbound(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Outbound();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Outbound();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    private int Inbound(int read)
    {
        if (read > 0)
        {
            Volatile.Write(ref _lastInbound, Environment.TickCount64);
        }

        return read;
    }

    private void Outbound() => Volatile.Write(ref _lastOutbound, Environment.TickCount64);
}
