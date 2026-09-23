using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace PocketCalculator.Cdp;

/// <summary>
/// One connection's outbound queue: unbounded in count, bounded in the total
/// size of the messages waiting in it.
/// </summary>
/// <remarks>
/// Deviation (SECURITY.md L3): upstream queues replies and events on an
/// unbounded channel, so a client that stops reading its socket while the page
/// keeps producing events (console messages, network events, screenshots) grows
/// the server's memory without limit. Writers here still never block - every
/// producer uses <c>TryWrite</c> - but a write that would take the queue past
/// its limit is refused, the queue is completed, and <see cref="Overflowed"/>
/// trips so the connection closes. A client that reads its socket never gets
/// near the limit.
/// </remarks>
internal sealed class ReplyQueue : Channel<string>
{
    /// <summary>
    /// The default limit, in UTF-16 code units (so about twice that in bytes).
    /// Generous next to anything one reply carries: a full-page screenshot of the
    /// largest capture the engine produces is well under it.
    /// </summary>
    internal const long DefaultMaxQueuedChars = 128L << 20;

    private readonly Channel<string> _inner =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _overflow = new();
    private readonly long _maxQueuedChars;
    private long _queuedChars;

    internal ReplyQueue(long maxQueuedChars = DefaultMaxQueuedChars)
    {
        _maxQueuedChars = maxQueuedChars;
        Reader = new QueueReader(this);
        Writer = new QueueWriter(this);
    }

    /// <summary>Trips once a write was refused for taking the queue past its limit.</summary>
    internal CancellationToken Overflowed => _overflow.Token;

    /// <summary>Characters waiting to be sent.</summary>
    internal long QueuedChars => Interlocked.Read(ref _queuedChars);

    private bool TryWrite(string item)
    {
        if (Interlocked.Add(ref _queuedChars, item.Length) > _maxQueuedChars)
        {
            Interlocked.Add(ref _queuedChars, -item.Length);
            if (_inner.Writer.TryComplete())
            {
                CdpLog.Warn("closing CDP connection: the client is not reading its replies");
                try
                {
                    _overflow.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return false;
        }

        if (!_inner.Writer.TryWrite(item))
        {
            Interlocked.Add(ref _queuedChars, -item.Length);
            return false;
        }

        return true;
    }

    private bool TryRead([MaybeNullWhen(false)] out string item)
    {
        if (_inner.Reader.TryRead(out item))
        {
            Interlocked.Add(ref _queuedChars, -item.Length);
            return true;
        }

        return false;
    }

    private sealed class QueueWriter(ReplyQueue queue) : ChannelWriter<string>
    {
        public override bool TryWrite(string item) => queue.TryWrite(item);

        public override bool TryComplete(Exception? error = null) => queue._inner.Writer.TryComplete(error);

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            queue._inner.Writer.WaitToWriteAsync(cancellationToken);
    }

    private sealed class QueueReader(ReplyQueue queue) : ChannelReader<string>
    {
        public override Task Completion => queue._inner.Reader.Completion;

        public override bool TryRead([MaybeNullWhen(false)] out string item) => queue.TryRead(out item);

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            queue._inner.Reader.WaitToReadAsync(cancellationToken);
    }
}
