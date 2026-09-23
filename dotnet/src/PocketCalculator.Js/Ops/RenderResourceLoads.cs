using System.Collections.Concurrent;
using PocketCalculator.Net;
using PocketCalculator.Render;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// One finished page-transport load for the renderer cache. Port of
/// <c>RenderResourceLoad</c> in <c>crates/obscura-js/src/ops.rs</c> (upstream 97ff86d).
/// </summary>
/// <param name="Generation">The <c>DocumentGeneration</c> the request was made for.</param>
/// <param name="Request">The network URL, image request profile and resource kind.</param>
/// <param name="Response">The response, or null when the request failed or was cancelled.</param>
public sealed record RenderResourceLoad(
    ulong Generation,
    RenderResourceMiss Request,
    Response? Response,
    double StartedAtUnixMs,
    double EndedAtUnixMs);

/// <summary>
/// A transport response the runtime applied; the page turns it into the Network events
/// and resource timing a client expects for a subresource.
/// </summary>
public sealed record RenderResourceEvent(
    bool IsFont,
    Response Response,
    double StartedAtUnixMs,
    double EndedAtUnixMs);

/// <summary>
/// The page-transport loads of one document: results waiting to be applied, the requests
/// still in flight, and the applied responses the page has not reported yet.
/// </summary>
/// <remarks>
/// Upstream keeps these as separate <c>PocketCalculatorState</c> fields and swaps the mpsc channel
/// when a document is retired. The port retires the whole set instead: a load that is
/// still finishing delivers into the retired instance, which nothing reads any more, and
/// <see cref="RenderResourceLoad.Generation"/> fences the live one as well. Only
/// <see cref="Deliver"/> runs off the runtime's thread; everything else is touched by
/// the thread that drives the runtime.
/// </remarks>
public sealed class RenderResourceLoads
{
    /// <summary>
    /// Page-wide limit on concurrent background render-resource requests (Rust
    /// <c>RENDER_RESOURCE_CONCURRENCY</c>).
    /// </summary>
    public const int Concurrency = 16;

    /// <summary>
    /// Bound on requests in flight for one document (Rust <c>MAX_PENDING_RENDER_RESOURCES</c>).
    /// </summary>
    public const int MaxPending = 128;

    /// <summary>
    /// Bound on applied responses kept for a page that never collects them.
    /// Rust leaves the vector unbounded; a runtime driven without a page would grow it.
    /// </summary>
    internal const int MaxEvents = 1024;

    private readonly ConcurrentQueue<RenderResourceLoad> _results = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cancellation = new();

    internal HashSet<RenderResourceMiss> InFlight { get; } = [];

    internal List<RenderResourceEvent> Events { get; } = [];

    internal CancellationToken Token => _cancellation.Token;

    internal bool HasResults => !_results.IsEmpty;

    internal bool TryTake(out RenderResourceLoad load) => _results.TryDequeue(out load!);

    /// <summary>Hand one finished load to the runtime. Thread-safe.</summary>
    internal void Deliver(RenderResourceLoad load)
    {
        _results.Enqueue(load);
        _signal.Release();
    }

    /// <summary>
    /// Wait until a load was delivered since the last wait, or the timeout passes. The
    /// signal counts deliveries, so a load that lands between a pending check and this
    /// call is not missed.
    /// </summary>
    internal Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _signal.WaitAsync(timeout, cancellationToken);

    /// <summary>Abort every request of this document. Results already delivered stay unread.</summary>
    internal void Retire()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // A cancellation callback threw; the requests are cancelled either way.
        }
    }
}
