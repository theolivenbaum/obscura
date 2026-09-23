using System.Globalization;
using System.Runtime.CompilerServices;

namespace PocketCalculator.Js.Ops;

/// <summary>
/// The per-page cap on <c>op_fetch_url</c> requests in flight at once; requests past it
/// queue until one finishes.
/// </summary>
/// <remarks>
/// Deviation from Rust, which starts every fetch a page asks for at once. Each in-flight
/// fetch can hold its body (up to <c>POCKETCALCULATOR_FETCH_MAX_BODY_BYTES</c>) plus the
/// UTF-8, base64 and JSON copies the op payload is built from, so about twenty parallel
/// gzip-bomb fetches exhausted a shared <c>serve</c> process. Chromium bounds concurrency
/// too, at six connections per host; this applies six per page, across hosts, which is
/// stricter only for a page fetching from many hosts at once. Queued requests still
/// complete, in order, so what a page can observe is timing, not results.
/// <c>POCKETCALCULATOR_FETCH_MAX_CONCURRENT</c> overrides the limit.
/// </remarks>
internal static class FetchConcurrency
{
    internal const int DefaultLimit = 6;

    private static readonly ConditionalWeakTable<PocketCalculatorState, SemaphoreSlim> Gates = new();

    internal static int Limit()
    {
        string? configured = Environment.GetEnvironmentVariable("POCKETCALCULATOR_FETCH_MAX_CONCURRENT");
        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            && value > 0
            ? value
            : DefaultLimit;
    }

    /// <summary>Wait for a slot on <paramref name="state"/>'s page; dispose the result to free it.</summary>
    internal static async Task<Slot> EnterAsync(PocketCalculatorState state)
    {
        SemaphoreSlim gate = Gates.GetValue(state, static _ => new SemaphoreSlim(Limit()));
        await gate.WaitAsync().ConfigureAwait(false);
        return new Slot(gate);
    }

    /// <summary>Requests in flight on <paramref name="state"/>'s page, for tests.</summary>
    internal static int InFlight(PocketCalculatorState state) =>
        Gates.TryGetValue(state, out SemaphoreSlim? gate) ? Limit() - gate.CurrentCount : 0;

    internal readonly struct Slot(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
