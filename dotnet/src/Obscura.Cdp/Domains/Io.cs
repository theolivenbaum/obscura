using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Obscura.Cdp.Domains;

/// <summary>
/// Bounded store of the response bodies handed out by <c>Fetch.takeResponseBodyAsStream</c>.
/// </summary>
/// <remarks>
/// Streaming exists to keep large downloads out of memory (issue #360), but each taken body is
/// moved out of the page's LRU-bounded cache into this map, which lives for the whole server
/// lifetime. A client that opens streams and never calls IO.close, or simply disconnects
/// mid-download, would otherwise pin every taken body forever and reintroduce exactly the unbounded
/// accumulation streaming was meant to avoid. Cap the number of open streams and their total bytes,
/// evicting the oldest first, so memory stays bounded regardless of client behavior. Reading an
/// evicted handle fails cleanly (the client re-takes or gives up), which is the right trade against
/// an OOM.
/// </remarks>
public sealed class IoStreamStore
{
    /// <summary>Default chunk size when the client does not pass <c>size</c>.</summary>
    /// <remarks>
    /// Chrome uses a similar order of magnitude; keeping chunks bounded is the point of streaming
    /// (issue #360), so we never return the whole body in one IO.read.
    /// </remarks>
    public const long DefaultChunk = 1L << 20;

    public const long MaxReadChunk = 4L << 20;

    private readonly Dictionary<string, Entry> _streams = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private long _totalBytes;
    private ulong _counter;

    public IoStreamStore()
        : this(MaxEntriesFromEnvironment(), MaxBytesFromEnvironment())
    {
    }

    private IoStreamStore(int maxEntries, long maxBytes)
    {
        _maxEntries = Math.Max(maxEntries, 1);
        _maxBytes = maxBytes;
    }

    /// <summary>
    /// Build a store with explicit limits. Rust keeps this private to the module and reaches it
    /// from the in-file tests; it is public here so the ported tests can construct one without an
    /// <c>InternalsVisibleTo</c> on a project file this component does not own.
    /// </summary>
    public static IoStreamStore WithLimits(int maxEntries, long maxBytes) =>
        new(maxEntries, maxBytes);

    private static int MaxEntriesFromEnvironment() =>
        int.TryParse(
            Environment.GetEnvironmentVariable("OBSCURA_IO_STREAM_MAX_ENTRIES"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 32;

    private static long MaxBytesFromEnvironment() =>
        long.TryParse(
            Environment.GetEnvironmentVariable("OBSCURA_IO_STREAM_MAX_BYTES"),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 256L * 1024 * 1024;

    /// <summary>
    /// Store a body and return its handle, evicting the oldest streams if this would push the store
    /// past its entry or byte cap. A single body larger than the byte cap is rejected rather than
    /// becoming an unbounded exception to the store's memory contract.
    /// </summary>
    public bool TryInsert(
        byte[] bytes,
        [NotNullWhen(true)] out string? handle,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength > _maxBytes)
        {
            handle = null;
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"IO stream body is {bytes.LongLength} bytes, exceeding the {_maxBytes}-byte per-context limit");
            return false;
        }

        while (_order.Count > 0
            && (_order.Count >= _maxEntries || _totalBytes > _maxBytes - bytes.LongLength))
        {
            var oldest = _order[0];
            _order.RemoveAt(0);
            if (_streams.Remove(oldest, out var evicted))
            {
                _totalBytes = Math.Max(0, _totalBytes - evicted.Bytes.LongLength);
            }
        }

        handle = "stream-" + _counter.ToString(CultureInfo.InvariantCulture);
        _counter++;
        _totalBytes += bytes.LongLength;
        _streams[handle] = new Entry(bytes);
        _order.Add(handle);
        error = null;
        return true;
    }

    /// <summary>
    /// Read up to <paramref name="size"/> bytes from the stream, advancing its cursor. Answers the
    /// base64 chunk and whether EOF was reached, or false for an unknown or already-freed handle.
    /// </summary>
    public bool TryRead(string handle, long? offset, long size, out string data, out bool eof)
    {
        data = string.Empty;
        eof = false;
        if (!_streams.TryGetValue(handle, out var entry))
        {
            return false;
        }

        if (offset is { } requested)
        {
            entry.Cursor = Math.Min(requested, entry.Bytes.LongLength);
        }

        size = Math.Min(size, MaxReadChunk);
        var start = Math.Min(entry.Cursor, entry.Bytes.LongLength);
        var end = Math.Min(SaturatingAdd(start, size), entry.Bytes.LongLength);
        data = Convert.ToBase64String(entry.Bytes.AsSpan((int)start, (int)(end - start)));
        entry.Cursor = end;
        eof = end >= entry.Bytes.LongLength;
        return true;
    }

    /// <summary>Free a stream's buffer (IO.close). A no-op for an unknown handle.</summary>
    public void Remove(string handle)
    {
        if (_streams.Remove(handle, out var entry))
        {
            _totalBytes -= entry.Bytes.LongLength;
            _order.Remove(handle);
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private sealed class Entry(byte[] bytes)
    {
        public byte[] Bytes { get; } = bytes;

        public long Cursor { get; set; }
    }
}

/// <summary>
/// CDP IO domain. Streams a response body handed out by <c>Fetch.takeResponseBodyAsStream</c>:
/// IO.read returns the next base64 chunk and IO.close frees the buffer. Nothing here runs unless a
/// client opened a stream.
/// </summary>
public static class Io
{
    public static Task<DomainResult> HandleAsync(string method, JsonNode? parameters, CdpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            switch (method)
            {
                case "read":
                {
                    var handle = parameters.Get("handle").AsString()
                        ?? throw new DomainError("IO.read requires handle");
                    var size = NonNegative(
                            parameters,
                            "size",
                            "IO.read size must be a non-negative integer")
                        ?? IoStreamStore.DefaultChunk;
                    var offset = NonNegative(
                        parameters,
                        "offset",
                        "IO.read offset must be a non-negative integer");

                    if (!ctx.IoStreams.TryRead(handle, offset, size, out var data, out var eof))
                    {
                        throw new DomainError($"IO.read: unknown handle {handle}");
                    }

                    return Task.FromResult(DomainResult.Ok(new JsonObject
                    {
                        ["data"] = data,
                        ["eof"] = eof,
                        ["base64Encoded"] = true,
                    }));
                }

                case "close":
                {
                    var handle = parameters.Get("handle").AsString()
                        ?? throw new DomainError("IO.close requires handle");
                    ctx.IoStreams.Remove(handle);
                    return Task.FromResult(DomainResult.Empty());
                }

                default:
                    return Task.FromResult(DomainResult.Err($"Unknown IO method: {method}"));
            }
        }
        catch (DomainError error)
        {
            return Task.FromResult(DomainResult.Err(error.Message));
        }
    }

    private static long? NonNegative(JsonNode? parameters, string name, string message)
    {
        if (!DomainParams.Has(parameters, name))
        {
            return null;
        }

        var value = parameters.Get(name).AsI64();
        return value is >= 0 ? value : throw new DomainError(message);
    }
}
