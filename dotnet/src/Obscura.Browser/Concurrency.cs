namespace Obscura.Browser;

/// <summary>
/// The two shapes of <c>futures::stream::...</c> concurrency <c>page.rs</c> uses.
/// </summary>
/// <remarks>
/// Bounding concurrency is load-bearing: a page with 100 external scripts would
/// otherwise open 100 sockets at once, exhausting the connection pool and
/// ephemeral ports. 16 is above the per-host pool ceiling a browser uses.
/// </remarks>
internal static class Buffered
{
    /// <summary>
    /// <c>futures::stream::iter(..).buffered(limit).collect()</c>: at most
    /// <paramref name="limit"/> in flight, results in submission order.
    /// </summary>
    internal static async Task<List<T>> AllAsync<T>(
        IReadOnlyList<Func<Task<T>>> factories,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Not disposed: when one factory throws, the remaining tasks are abandoned
        // rather than awaited, and they still release the gate as they finish.
        var gate = new SemaphoreSlim(limit, limit);
        var running = new Task<T>[factories.Count];
        for (int i = 0; i < factories.Count; i++)
        {
            Func<Task<T>> factory = factories[i];
            running[i] = RunAsync(factory);
        }

        List<T> results = new(factories.Count);
        foreach (Task<T> task in running)
        {
            results.Add(await task.ConfigureAwait(false));
        }
        return results;

        async Task<T> RunAsync(Func<Task<T>> factory)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await factory().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// <c>buffer_unordered(limit)</c> consumed until <paramref name="deadline"/>.
    /// </summary>
    /// <remarks>
    /// Each completed value is handed to <paramref name="onCompleted"/> as it
    /// arrives. Work still in flight when the deadline passes is abandoned rather
    /// than awaited, which is what lets a slow resource be retried by a later warmup
    /// instead of being negatively cached.
    /// </remarks>
    internal static async Task UnorderedUntilAsync<T>(
        IReadOnlyList<Func<Task<T>>> factories,
        int limit,
        DateTime deadlineUtc,
        Func<T, Task> onCompleted)
    {
        var gate = new SemaphoreSlim(limit, limit);
        var pending = new List<Task<T>>(factories.Count);
        foreach (Func<Task<T>> factory in factories)
        {
            pending.Add(RunAsync(factory));
        }

        while (pending.Count != 0)
        {
            TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }
            using var timer = new CancellationTokenSource(remaining);
            Task delay = Task.Delay(Timeout.InfiniteTimeSpan, timer.Token);
            Task finished = await Task.WhenAny([.. pending, delay]).ConfigureAwait(false);
            if (ReferenceEquals(finished, delay))
            {
                return;
            }
            var completed = (Task<T>)finished;
            pending.Remove(completed);
            timer.Cancel();
            // Observe the cancelled timer so it does not surface as an unobserved
            // task exception.
            _ = delay.ContinueWith(static _ => { }, TaskScheduler.Default);
            await onCompleted(await completed.ConfigureAwait(false)).ConfigureAwait(false);
        }

        async Task<T> RunAsync(Func<Task<T>> factory)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await factory().ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
