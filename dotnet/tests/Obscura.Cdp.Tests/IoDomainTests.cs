using System.Text.Json.Nodes;
using Obscura.Cdp.Domains;
using Xunit;

namespace Obscura.Cdp.Tests;

/// <summary>
/// The xUnit port of the <c>#[cfg(test)] mod tests</c> in
/// <c>crates/obscura-cdp/src/domains/io.rs</c>.
/// </summary>
[Collection(CdpDomainCollection.Name)]
public sealed class IoDomainTests
{
    private static byte[] Decode(string text) => Convert.FromBase64String(text);

    [Fact]
    public void ReadsChunksThenFrees()
    {
        var store = IoStreamStore.WithLimits(4, 1024);
        Assert.True(store.TryInsert("hello"u8.ToArray(), out string? handle, out _));

        Assert.True(store.TryRead(handle, null, 3, out string first, out bool firstEof));
        Assert.Equal("hel"u8.ToArray(), Decode(first));
        Assert.False(firstEof);

        Assert.True(store.TryRead(handle, null, 3, out string second, out bool secondEof));
        Assert.Equal("lo"u8.ToArray(), Decode(second));
        Assert.True(secondEof);

        store.Remove(handle);
        Assert.False(store.TryRead(handle, null, 3, out _, out _));
    }

    [Fact]
    public void ReadOffsetSeeksAndZeroSizeDoesNotAdvance()
    {
        var store = IoStreamStore.WithLimits(2, 1024);
        Assert.True(store.TryInsert("abcdef"u8.ToArray(), out string? handle, out _));

        Assert.True(store.TryRead(handle, 1, 0, out string empty, out bool emptyEof));
        Assert.Empty(Decode(empty));
        Assert.False(emptyEof);

        Assert.True(store.TryRead(handle, null, 2, out string middle, out bool middleEof));
        Assert.Equal("bc"u8.ToArray(), Decode(middle));
        Assert.False(middleEof);

        Assert.True(store.TryRead(handle, 4, 10, out string tail, out bool tailEof));
        Assert.Equal("ef"u8.ToArray(), Decode(tail));
        Assert.True(tailEof);
    }

    [Fact]
    public async Task ReadRejectsNegativeOrNonIntegerRanges()
    {
        var ctx = CdpContext.New();
        Assert.True(ctx.IoStreams.TryInsert("data"u8.ToArray(), out string? handle, out _));
        string[] cases =
        [
            $$"""{"handle": "{{handle}}", "offset": -1}""",
            $$"""{"handle": "{{handle}}", "size": -1}""",
            $$"""{"handle": "{{handle}}", "offset": 1.5}""",
        ];
        foreach (string parameters in cases)
        {
            DomainResult result = await Io.HandleAsync("read", CdpDomainFixtures.Json(parameters), ctx);
            Assert.False(result.IsOk, parameters);
        }
    }

    [Fact]
    public void EvictsOldestOverEntryCap()
    {
        var store = IoStreamStore.WithLimits(3, 1024);
        Assert.True(store.TryInsert([0], out string? h0, out _));
        Assert.True(store.TryInsert([1], out string? h1, out _));
        Assert.True(store.TryInsert([2], out _, out _));
        // 4th entry, cap 3 -> h0 evicted.
        Assert.True(store.TryInsert([3], out string? h3, out _));

        Assert.False(store.TryRead(h0, null, 10, out _, out _));
        Assert.True(store.TryRead(h1, null, 10, out _, out _));
        Assert.True(store.TryRead(h3, null, 10, out _, out _));
    }

    [Fact]
    public void EvictsOverByteCapAndRejectsOversizedBody()
    {
        var store = IoStreamStore.WithLimits(4, 10);
        Assert.True(store.TryInsert(new byte[8], out string? h0, out _));
        // 16 > 10 -> h0 evicted.
        Assert.True(store.TryInsert(new byte[8], out string? h1, out _));
        Assert.False(store.TryInsert(new byte[100], out _, out string? error));

        Assert.False(store.TryRead(h0, null, 100, out _, out _));
        Assert.True(store.TryRead(h1, null, 100, out _, out _));
        Assert.Contains("exceeding", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedReadSizeIsCapped()
    {
        var store = IoStreamStore.WithLimits(2, IoStreamStore.MaxReadChunk * 2);
        var body = new byte[IoStreamStore.MaxReadChunk + 17];
        Array.Fill(body, (byte)7);
        Assert.True(store.TryInsert(body, out string? handle, out _));

        Assert.True(store.TryRead(handle, null, long.MaxValue, out string first, out bool firstEof));
        Assert.Equal(IoStreamStore.MaxReadChunk, Decode(first).Length);
        Assert.False(firstEof);

        Assert.True(store.TryRead(handle, null, long.MaxValue, out string second, out bool secondEof));
        Assert.Equal(17, Decode(second).Length);
        Assert.True(secondEof);
    }

    /// <summary>
    /// Not in the Rust file: the handler surface itself. <c>IO.read</c> on a handle nobody opened
    /// must be an error, and <c>IO.close</c> on one must be a no-op.
    /// </summary>
    [Fact]
    public async Task UnknownHandleReadsFailAndUnknownHandleClosesAreNoOps()
    {
        var ctx = CdpContext.New();
        DomainResult read = await Io.HandleAsync(
            "read",
            CdpDomainFixtures.Json("""{"handle": "stream-404"}"""),
            ctx);
        Assert.Contains("unknown handle stream-404", CdpDomainFixtures.ErrorOf(read), StringComparison.Ordinal);

        DomainResult close = await Io.HandleAsync(
            "close",
            CdpDomainFixtures.Json("""{"handle": "stream-404"}"""),
            ctx);
        Assert.True(close.IsOk);

        Assert.False((await Io.HandleAsync("read", new JsonObject(), ctx)).IsOk);
        Assert.False((await Io.HandleAsync("close", new JsonObject(), ctx)).IsOk);
        Assert.False((await Io.HandleAsync("nope", new JsonObject(), ctx)).IsOk);
    }
}
